// 區塊職責：在 Senate 側把「一則 persona 發言」組成要交給寫入端的 SCP_TavernMessage —— 不需要 Unity Editor。
// 物理意義：移植自 UCL_Core `Cmd_Tavern.Op_Post` 的**寫入前**那一段（TASK-0303：早安 intro 不再依賴 Editor）。
//          寫入端（Server 的 `tavern-write`）已經負責 seq／ts／uuid／@mention 通知／發薪（TASK-0296／0299），
//          這裡只補它**之前**還只有 Editor 做的事：
//            ① sender_id   ＝ persona 的 agent（profile 反推；沒有就用 persona 名）
//            ② sender_name ＝ `Treasury/accounts/<id>.json` 的 display_name（⚠ 不是 Bank/accounts —— 那裡是 id 本身）
//            ③ 頭像        ＝ persona 卡 → identity 卡的 `AvatarSprite`
//            ④ glossary 自動附註（演算法與輸出逐字對齊 `Cmd_Glossary.AppendRefsToText`）
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
        public const string AUTO_ATTACH_MARKER = "📖 **本回提到的新詞** (auto-attached by Cmd_Glossary):";
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

            // ④ glossary（`glossary-auto-attach=false` 不分大小寫，同 Editor `ToLowerInvariant() == "false"`）
            bool aOptOut = iMeta.TryGetValue("glossary-auto-attach", out string? aGa)
                           && string.Equals((aGa ?? "").Trim(), "false", StringComparison.OrdinalIgnoreCase);
            string aBody = aOptOut || aIsCli ? iBody : AppendGlossaryRefs(iProjectRoot, iGlossaryRoot, iBody);

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

        // ── glossary 自動附註（逐字對齊 Cmd_Glossary.AppendRefsToText；任何例外都回原文）──────────

        sealed class Entry
        {
            public string Term = "", Slug = "", OneLine = "", Rel = "";
            public List<string> Aliases = new List<string>();
        }

        sealed class Hit
        {
            public Entry E = null!;
            public int Start, Length;
        }

        /// <summary>
        /// 詞典附註裡印的路徑前綴（讀的人拿它去 Read 那個 .md）。
        /// <para>預設詞典根 ⇒ 逐字 `docs/Glossary`（Editor `Cmd_Glossary` 印的就是這個 —— 欄位同形對拍靠它）；
        /// 自訂在專案底下 ⇒ 相對專案根；在專案外 ⇒ 絕對路徑（⛔ 不印一個指不到檔的相對路徑）。</para>
        /// </summary>
        public static string GlossaryDisplayPrefix(string iProjectRoot, string iGlossaryRoot)
        {
            const string EditorPrefix = "docs/Glossary";
            try
            {
                string aProj = Path.GetFullPath(iProjectRoot).Replace('\\', '/').TrimEnd('/');
                string aGlo = Path.GetFullPath(iGlossaryRoot).Replace('\\', '/').TrimEnd('/');
                string aDefault = Path.GetFullPath(Path.Combine(iProjectRoot, EditorPrefix)).Replace('\\', '/').TrimEnd('/');
                if (string.Equals(aGlo, aDefault, StringComparison.OrdinalIgnoreCase)) return EditorPrefix;
                if (aGlo.StartsWith(aProj + "/", StringComparison.OrdinalIgnoreCase)) return aGlo.Substring(aProj.Length + 1);
                return aGlo;
            }
            catch (Exception) { return EditorPrefix; }
        }

        public static string AppendGlossaryRefs(string iProjectRoot, string iGlossaryRoot, string iText, int iCap = 5)
        {
            if (string.IsNullOrEmpty(iText) || iText.Contains(AUTO_ATTACH_MARKER)) return iText;
            try
            {
                string aDir = iGlossaryRoot;
                string aPrefix = GlossaryDisplayPrefix(iProjectRoot, iGlossaryRoot);
                var aEntries = LoadEntries(aDir);
                if (aEntries.Count == 0) return iText;
                var aHits = DetectHits(iText, aEntries, Math.Max(1, iCap));
                if (aHits.Count == 0) return iText;
                var sb = new StringBuilder();
                sb.Append(iText.TrimEnd());
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine(AUTO_ATTACH_MARKER);
                sb.AppendLine();
                foreach (var h in aHits)
                {
                    sb.AppendLine($"- **{h.E.Term}**: {h.E.OneLine}");
                    sb.AppendLine($"({aPrefix}/{h.E.Rel})");
                }
                return sb.ToString();
            }
            catch (Exception) { return iText; }
        }

        static List<Entry> LoadEntries(string iDir)
        {
            var aList = new List<Entry>();
            if (!Directory.Exists(iDir)) return aList;
            foreach (var f in Directory.GetFiles(iDir, "*.md", SearchOption.AllDirectories))
            {
                string aName = Path.GetFileNameWithoutExtension(f);
                if (aName.Equals("README", StringComparison.OrdinalIgnoreCase) || aName.StartsWith("_", StringComparison.Ordinal)) continue;
                Entry? e = ParseEntry(f);
                if (e == null) continue;
                e.Rel = f.Substring(iDir.Length).TrimStart('\\', '/').Replace('\\', '/');
                aList.Add(e);
            }
            return aList;
        }

        static Entry? ParseEntry(string iPath)
        {
            try
            {
                string aContent = File.ReadAllText(iPath, Encoding.UTF8);
                if (!aContent.StartsWith("---", StringComparison.Ordinal)) return null;
                int aEnd = aContent.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (aEnd < 0) return null;
                var e = new Entry();
                string? aList = null;
                foreach (string aRaw in aContent.Substring(3, aEnd - 3).Split('\n'))
                {
                    string aLine = aRaw.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(aLine)) continue;
                    if (aLine.StartsWith("  - ", StringComparison.Ordinal) && aList == "aliases") { e.Aliases.Add(Unescape(aLine.Substring(4).Trim())); continue; }
                    if (aLine.StartsWith("- ", StringComparison.Ordinal) && aList == "aliases") { e.Aliases.Add(Unescape(aLine.Substring(2).Trim())); continue; }
                    int c = aLine.IndexOf(':');
                    if (c < 0) continue;
                    string aKey = aLine.Substring(0, c).Trim();
                    string aVal = c < aLine.Length - 1 ? aLine.Substring(c + 1).Trim() : "";
                    if (aVal.Length == 0) { aList = aKey; continue; }
                    aList = null;
                    switch (aKey)
                    {
                        case "term": e.Term = Unescape(aVal); break;
                        case "slug": e.Slug = aVal; break;
                        case "one_line": e.OneLine = Unescape(aVal); break;
                    }
                }
                return e.Term.Length == 0 || e.Slug.Length == 0 ? null : e;
            }
            catch (Exception) { return null; }
        }

        static List<Hit> DetectHits(string iText, List<Entry> iEntries, int iCap)
        {
            var aRaw = new List<Hit>();
            string aLower = iText.ToLowerInvariant();
            foreach (var e in iEntries)
            {
                AddHits(e, e.Term, aLower, aRaw);
                foreach (string a in e.Aliases) AddHits(e, a, aLower, aRaw);
            }
            aRaw.Sort((a, b) => { int c = a.Start.CompareTo(b.Start); return c != 0 ? c : b.Length.CompareTo(a.Length); });
            var aSelected = new List<Hit>();
            int aLastEnd = -1;
            foreach (var h in aRaw)
            {
                if (h.Start < aLastEnd) continue;
                aSelected.Add(h);
                aLastEnd = h.Start + h.Length;
            }
            var aSeen = new HashSet<string>();
            var aResult = new List<Hit>();
            foreach (var h in aSelected)
            {
                if (!aSeen.Add(h.E.Slug)) continue;
                aResult.Add(h);
                if (aResult.Count >= iCap) break;
            }
            return aResult;
        }

        static void AddHits(Entry e, string iTrigger, string iLowerText, List<Hit> oHits)
        {
            if (string.IsNullOrEmpty(iTrigger)) return;
            string t = iTrigger.ToLowerInvariant();
            int i = 0;
            while (i < iLowerText.Length)
            {
                int f = iLowerText.IndexOf(t, i, StringComparison.Ordinal);
                if (f < 0) break;
                oHits.Add(new Hit { E = e, Start = f, Length = t.Length });
                i = f + t.Length;
            }
        }

        static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s.StartsWith("\"", StringComparison.Ordinal) && s.EndsWith("\"", StringComparison.Ordinal))
                s = s.Substring(1, s.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            return s;
        }
    }
}
