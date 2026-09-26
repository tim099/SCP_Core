// 區塊職責：在 Senate 側把「一則 persona 發言」組成要交給寫入端的 SCP_TavernMessage —— 不需要 Unity Editor。
// 物理意義：移植自 UCL_Core `Cmd_Tavern.Op_Post` 的**寫入前**那一段（TASK-0303：早安 intro 不再依賴 Editor）。
//          寫入端（Server 的 `tavern-write`）已經負責 seq／ts／uuid／@mention 通知／發薪（TASK-0296／0299），
//          這裡只補它**之前**還只有 Editor 做的事：
//            ① sender_id   ＝ persona 的 agent（profile 反推；沒有就用 persona 名）
//            ② sender_name ＝ `Treasury/accounts/<id>.json` 的 display_name（⚠ 不是 Bank/accounts —— 那裡是 id 本身）
//            ③ 頭像        ＝ persona 卡 → identity 卡的 `AvatarSprite`
//            ④ session token 驗證（`_session/_token_enforce.json` enforce=true 才驗）
//            ⑤ glossary 自動附註（演算法與輸出逐字對齊 `Cmd_Glossary.AppendRefsToText`）
// ⚠ 刻意沒搬的（寫在這裡讓人查得到）：task-assign/commit 的 meta schema 檢查、alter pacing、
//   CLI 指令偵測、creative 歸檔 —— 早安 intro 都碰不到；要把**一般發文**搬過來的那一天（epic 0295 ③）再補。
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
        /// <summary>非致命的提醒（沒有顯示名、token 身分不符…）—— 呼叫端要印出來。</summary>
        public List<string> Notes = new List<string>();
    }

    public static class SCP_TavernPostCompose
    {
        public const string AUTO_ATTACH_MARKER = "📖 **本回提到的新詞** (auto-attached by Cmd_Glossary):";
        const string AssetsRel = "Assets/.BuiltinModules/ModulesRoot/Modules/Core/UCL_Assets";

        public static SCP_TavernPostDraft Build(string iDataRoot, string iLettersRoot, string iProjectRoot, string iRegion,
                                                string iRoom, string iPersona, string iBody,
                                                IReadOnlyDictionary<string, string> iMeta, string iSessionToken)
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

            // ④ token（enforce 才驗；系統 sender `_` 開頭不驗）
            string? aTokenWarning = null;
            if (!aSenderId.StartsWith("_", StringComparison.Ordinal) && TokenEnforceEnabled(iDataRoot))
            {
                if (string.IsNullOrEmpty(iSessionToken)) { aOut.Error = "[T07] token enforce ON，但沒有 session_token"; return aOut; }
                string? aErr = CheckToken(iDataRoot, iSessionToken, out string aRealBank, out string aRealPersona);
                if (aErr != null) { aOut.Error = "[T07] token 校驗失敗: " + aErr; return aOut; }
                if (aRealBank != aSenderId || aRealPersona != iPersona)
                    aTokenWarning = $"⚠ [T07 identity mismatch] 真實身分 — bank: {aRealBank} / persona: {aRealPersona}";
            }

            // ② sender_name
            string aName = ReadDisplayName(iDataRoot, aSenderId);
            if (aName.Length == 0)
            {
                aOut.Notes.Add($"sender '{aSenderId}' 沒有帳戶顯示名（Treasury/accounts/{aSenderId}.json）—— 暫時顯示成 id");
                aName = aSenderId;
            }

            // ⑤ glossary（在 token 警告之前 —— 確保警告在最尾端）
            string aBody = iMeta.TryGetValue("glossary-auto-attach", out string? aGa) && aGa == "false"
                ? iBody : AppendGlossaryRefs(iProjectRoot, iBody);
            if (aTokenWarning != null) aBody = aBody + "\n\n---\n" + aTokenWarning;

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
            aOut.Message = aMsg;
            return aOut;
        }

        static bool TokenEnforceEnabled(string iDataRoot)
        {
            try
            {
                string aPath = Path.Combine(iDataRoot, "_session", "_token_enforce.json");
                if (!File.Exists(aPath)) return false;
                return SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8)).GetBool("enforce", false);
            }
            catch (Exception) { return false; }   // 讀檔失敗 → 預設 OFF（Editor 版同一側）
        }

        static string? CheckToken(string iDataRoot, string iToken, out string oBank, out string oPersona)
        {
            oBank = ""; oPersona = "";
            try
            {
                string aPath = Path.Combine(iDataRoot, "_session", "_tokens.json");
                if (!File.Exists(aPath)) return "_tokens.json 不存在（跑 senate cmd morning-wake --arg persona=<P> 發 token）";
                SCP_JsonData aRec = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8))["tokens"][iToken];
                if (!aRec.IsObject) return "token 不存在（可能 typo / 已失效 / 從未發過）";
                string aStatus = aRec.GetString("status", "");
                if (aStatus != "active") return $"token status={aStatus}（非 active）";
                oBank = aRec.GetString("bank_account", "");
                oPersona = aRec.GetString("persona", "");
                return null;
            }
            catch (Exception e) { return "verify exception: " + e.Message; }
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

        public static string AppendGlossaryRefs(string iProjectRoot, string iText, int iCap = 5)
        {
            if (string.IsNullOrEmpty(iText) || iText.Contains(AUTO_ATTACH_MARKER)) return iText;
            try
            {
                string aDir = Path.Combine(iProjectRoot, "docs", "Glossary");
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
                    sb.AppendLine($"(docs/Glossary/{h.E.Rel})");
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
