// 區塊職責：在 Senate 側把「一則 persona 發言」組成要交給寫入端的 SCP_TavernMessage —— 不需要 Unity Editor。
// 物理意義：移植自 UCL_Core `Cmd_Tavern.Op_Post` 的**寫入前**那一段（TASK-0303：早安 intro 不再依賴 Editor）。
//          寫入端（Server 的 `tavern-write`）已經負責 seq／ts／uuid／@mention 通知／發薪（TASK-0296／0299），
//          這裡只補它**之前**還只有 Editor 做的事：
//            ① sender_id   ＝ persona 的 agent（profile 反推；沒有就用 persona 名）
//            ② sender_name ＝ `Treasury/accounts/<id>.json` 的 display_name（⚠ 不是 Bank/accounts —— 那裡是 id 本身）
//            ③ 頭像        ＝ persona 卡 → identity 卡的 `AvatarSprite`
//            ④ glossary 自動附註（唯一實作 `SCP_Glossary`，TASK-0313；寫入端 `tavern-write` 對沒附過的訊息會再補一次，冪等）
//            ⑤ 酒保 CLI 指令判定（`SCP_TavernCli`，TASK-0312）⇒ 打 `tag=cli-cmd`／`cli_cmd=true`、跳過 ④
// ⚠ 不在這裡的（各有自己的一支，呼叫端串）：meta schema（`SCP_TavernMetaSchema`，寫入前擋）、
//   alter pacing（`SCP_TavernAlterPacing`，決定要不要延後）、creative 留念信（`SCP_TavernCreativeArchive`，寫入後寄）。
// ⛔ 不驗 session token（Tim 2026-09-27，TASK-0308）：在線機制是擋同一 persona 重複登入，不是發言許可 ——
//   下線後 commit 信件 repo 照樣要發公告。
// 數值影響：純讀。拒絕時回 Error（不組訊息）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Glossary;
using SCP.Core.Json;
using SCP.Core.Letters;

namespace SCP.Core.Tavern
{
    public sealed class SCP_TavernPostDraft
    {
        public SCP_TavernMessage? Message;
        public string? Error;
        /// <summary>非致命的提醒（沒有顯示名…）—— 呼叫端要印出來。</summary>
        public List<string> Notes = new List<string>();
    }

    public static class SCP_TavernPostCompose
    {
        public const string AUTO_ATTACH_MARKER = SCP_Glossary.AutoAttachMarker;
        const string AssetsRel = "Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets";

        /// <param name="iGlossaryRoot">詞典根（`SCP_PathId.GlossaryRoot`；呼叫端傳 `SCP_MorningRoots.GlossaryRoot`）。</param>
        public static SCP_TavernPostDraft Build(string iDataRoot, string iLettersRoot, string iProjectRoot, string iGlossaryRoot,
                                                string iRegion, string iRoom, string iPersona, string iBody,
                                                IReadOnlyDictionary<string, string> iMeta)
        {
            var aOut = new SCP_TavernPostDraft();
            if (string.IsNullOrEmpty(iBody)) { aOut.Error = "body 是空的"; return aOut; }
            if (!Directory.Exists(SCP_TavernRooms.RoomDir(iDataRoot, iRoom))) { aOut.Error = $"房間不存在：{iRoom}"; return aOut; }

            // ① sender_id
            string aSenderId = iPersona;
            try
            {
                string aAgent = (SCP_PersonaProfile.GetRaw(iLettersRoot, iPersona, iRegion)?.GetString("agent", "") ?? "").Trim();
                if (aAgent.Length > 0) aSenderId = aAgent;
            }
            catch (Exception e) { aOut.Notes.Add($"persona 的 agent 讀不到（{e.Message}）—— sender 用 persona 名"); }

            // ② sender_name
            string aName = ReadDisplayName(iDataRoot, aSenderId);
            if (aName.Length == 0)
            {
                aOut.Notes.Add($"sender '{aSenderId}' 沒有帳戶顯示名（Treasury/accounts/{aSenderId}.json）—— 暫時顯示成 id");
                aName = aSenderId;
            }

            // ⑤ 酒保 CLI 指令（TASK-0312，判準 SCP_TavernCli —— 與 Editor 同一支）：是指令 ⇒ 不附詞典、打分流標記。
            //   🩸 2026-08-19：附註被當成指令的一部分，群發把整本詞典打進別人輸入框並按 Enter。
            bool aIsCli = SCP_TavernCli.LooksLikeCliCommand(iDataRoot, iBody);

            // ④ glossary —— 判準與寫入端同一支（`SCP_Glossary.ShouldAutoAttach`：`glossary-auto-attach=false`／系統元件）。
            //   CLI 指令這一格在這裡先判（meta 的 cli_cmd 還沒打上）；寫入端讀得到下面打上的 cli_cmd，所以不會補回去。
            bool aAttach = !aIsCli && SCP_Glossary.ShouldAutoAttach(aSenderId, iMeta);
            string aBody = aAttach ? AppendGlossaryRefs(iProjectRoot, iGlossaryRoot, iBody) : iBody;

            var aMsg = new SCP_TavernMessage
            {
                Room = iRoom,
                SenderId = aSenderId,
                SenderName = aName,
                SenderPersona = iPersona,
                SenderAvatarSprite = ReadAvatar(iProjectRoot, iPersona, aSenderId),
                Kind = "chat",
                Body = aBody,
            };
            foreach (var kv in iMeta) aMsg.Meta[kv.Key] = kv.Value;
            if (aIsCli)
            {
                // 已有 tag 不覆蓋（發話端顯式給的優先），改用獨立鍵保證分流訊號不丟 —— 逐字照 Editor `Op_Post`。
                if (!aMsg.Meta.TryGetValue("tag", out string? aTag) || string.IsNullOrEmpty(aTag))
                    aMsg.Meta["tag"] = SCP_TavernCli.Tag;
                aMsg.Meta[SCP_TavernCli.MetaKey] = "true";
            }
            aOut.Message = aMsg;
            return aOut;
        }

        // 顯示名的唯一真相源是 `Treasury/accounts/<id>.json`（Tim 2026-08-20；Editor 版 GetDisplayName 同一份）。
        static string ReadDisplayName(string iDataRoot, string iSenderId)
        {
            try
            {
                string aPath = Path.Combine(iDataRoot, "Treasury", "accounts", iSenderId + ".json");
                if (!File.Exists(aPath)) return "";
                return SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8)).GetString("display_name", "").Trim();
            }
            catch (Exception) { return ""; }
        }

        // 頭像：persona 卡優先，identity 卡次之；"Default" 視同沒有。讀不到就空（渲染端用預設）。
        static string ReadAvatar(string iProjectRoot, string iPersona, string iSenderId)
        {
            string? a = ReadAvatarFrom(Path.Combine(iProjectRoot, AssetsRel, "UCL_ChatTavernPersonaCardAsset", iPersona + ".json"));
            if (!string.IsNullOrEmpty(a)) return a!;
            return ReadAvatarFrom(Path.Combine(iProjectRoot, AssetsRel, "UCL_ChatTavernIdentityAsset", iSenderId + ".json")) ?? "";
        }

        static string? ReadAvatarFrom(string iPath)
        {
            try
            {
                if (!File.Exists(iPath)) return null;
                string a = SCP_JsonData.Parse(File.ReadAllText(iPath, Encoding.UTF8)).GetString("AvatarSprite", "");
                return string.IsNullOrEmpty(a) || a == "Default" ? null : a;
            }
            catch (Exception) { return null; }
        }

        // ── glossary 自動附註 ⇒ 唯一實作在 `SCP.Core.Glossary.SCP_Glossary`（TASK-0313）────────────
        //   此前這裡是一份「逐字對齊 Editor Cmd_Glossary」的移植版；兩份都對，而改一份不會讓另一份知道。
        //   ⇒ 本檔只留兩個薄包裝（既有呼叫端與 selftest 用），⛔ 不再持有任何解析／偵測邏輯。

        /// <summary>詞典附註裡印的路徑前綴 —— 委派 <see cref="SCP_Glossary.DisplayPrefix"/>。</summary>
        public static string GlossaryDisplayPrefix(string iProjectRoot, string iGlossaryRoot)
            => SCP_Glossary.DisplayPrefix(iProjectRoot, iGlossaryRoot);

        /// <summary>附註 —— 委派 <see cref="SCP_Glossary.AppendRefs"/>（冪等；任何例外都回原文）。</summary>
        public static string AppendGlossaryRefs(string iProjectRoot, string iGlossaryRoot, string iText, int iCap = SCP_Glossary.AutoAttachCap)
            => SCP_Glossary.AppendRefs(iText, iGlossaryRoot, SCP_Glossary.DisplayPrefix(iProjectRoot, iGlossaryRoot), iCap);
    }
}
