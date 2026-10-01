// 區塊職責：在 Senate 側把「一則 persona 發言」組成要交給寫入端的 SCP_TavernMessage —— 不需要 Unity Editor。
// 物理意義：移植自 UCL_Core `Cmd_Tavern.Op_Post` 的**寫入前**那一段（TASK-0303：早安 intro 不再依賴 Editor）。
//          寫入端（Server 的 `tavern-write`）已經負責 seq／ts／uuid／@mention 通知／發薪（TASK-0296／0299），
//          這裡只補它**之前**還只有 Editor 做的事：
//            ① sender_id   ＝ persona 的 agent（profile 反推；沒有就用 persona 名）
//            ② sender_name ＝ **persona id**（Tim 2026-09-28，TASK-0317：顯示名不另存，直接用 persona id）
//            ③ 頭像        ＝ **不在訊息上蓋**：顯示端照 persona 去它的信件夾拿（`SCP_TavernDisplay`／`SCP_PersonaDisplay`）
//                            ⇒ 換頭像不必改歷史訊息；⛔ 也不再讀 UCL_Asset 的 persona 卡／identity 卡
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

            // ② sender_name ＝ persona id（TASK-0317）
            string aName = iPersona;

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

        // ===========================================================
        // 區塊職責：**沒有 persona** 的發言（系統元件／人在後台頁打字）—— 組訊息的第二個入口（TASK-0366）。
        // 物理意義：Unity `Cmd_Tavern.Op_Post` 匿名那條路的搬家版（酒保廣播、酒館頁、沒帶 persona 的棋局廣播）。
        //          與 <see cref="Build"/> 共用 ⑤ CLI 判定與 ④ 詞典判準（`SCP_Glossary.ShouldAutoAttach` 對系統 sender 本來就不附）；
        //          差在身分：sender_id 由呼叫端給、sender_name ＝ 顯式給的 → 新銀行帳戶的顯示名 → id，**沒有 sender_persona**
        //          ⇒ 寫入端不計酬（計酬一律由 persona 解析，SCP_TavernPayroll）。
        // ⚠ 為什麼不併進 Build（persona 給空就當匿名）：「刻意匿名」與「忘了帶 persona」在輸入上同形 ——
        //   併成一支的話，忘了帶的人會安靜地發成匿名、少領薪水。分兩支 ⇒ 匿名要**點名**才走得到。
        // ===========================================================
        public static SCP_TavernPostDraft BuildSystem(string iDataRoot, string iBankRoot, string iProjectRoot, string iGlossaryRoot,
                                                      string iRoom, string iSenderId, string iSenderName, string iBody,
                                                      IReadOnlyDictionary<string, string> iMeta)
        {
            var aOut = new SCP_TavernPostDraft();
            string aId = (iSenderId ?? "").Trim();
            if (aId.Length == 0) { aOut.Error = "系統發言要點名 sender（⛔ 不猜身分）"; return aOut; }
            if (string.IsNullOrEmpty(iBody)) { aOut.Error = "body 是空的"; return aOut; }
            if (!Directory.Exists(SCP_TavernRooms.RoomDir(iDataRoot, iRoom))) { aOut.Error = $"房間不存在：{iRoom}"; return aOut; }

            string aName = (iSenderName ?? "").Trim();
            if (aName.Length == 0)
            {
                try
                {
                    var aAcc = SCP.Core.Bank.SCP_BankAccounts.TryLoad(iBankRoot, aId, out _);
                    if (aAcc != null && aAcc.DisplayName.Length > 0) aName = aAcc.DisplayName;
                }
                catch (Exception e) { aOut.Notes.Add($"帳戶顯示名讀不到（{e.Message}）—— 顯示成 id"); }
                if (aName.Length == 0) { aName = aId; aOut.Notes.Add($"sender '{aId}' 沒有帳戶顯示名 —— 顯示成 id"); }
            }

            bool aIsCli = SCP_TavernCli.LooksLikeCliCommand(iDataRoot, iBody);
            bool aAttach = !aIsCli && SCP_Glossary.ShouldAutoAttach(aId, iMeta);
            var aMsg = new SCP_TavernMessage
            {
                Room = iRoom,
                SenderId = aId,
                SenderName = aName,
                Kind = "chat",
                Body = aAttach ? AppendGlossaryRefs(iProjectRoot, iGlossaryRoot, iBody) : iBody,
            };
            foreach (var kv in iMeta) aMsg.Meta[kv.Key] = kv.Value;
            if (aIsCli)
            {
                if (!aMsg.Meta.TryGetValue("tag", out string? aTag) || string.IsNullOrEmpty(aTag))
                    aMsg.Meta["tag"] = SCP_TavernCli.Tag;
                aMsg.Meta[SCP_TavernCli.MetaKey] = "true";
            }
            aOut.Message = aMsg;
            return aOut;
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
