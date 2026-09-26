// 區塊職責：「叮 / 醒來時的酒館 catch-up」—— 在線一覽 ＋ 未讀訊息 ＋ persona inbox，組成一份簡報並（可選）推游標。
// 物理意義：移植自 UCL_Core `UCL_TavernCatchupService.Build`（TASK-0303：早安 catchup 不再依賴 Editor）。
//          輸出版面逐行對齊 Editor 版（`letters/<p>/cmd/ding_brief.md`），讀的人不必分辨是哪一端產的。
// ⚠ 順序不可反：**先組出簡報、再推游標**。反過來的話，回傳檔寫入失敗時訊息已被標成已讀
//   ⇒ 那批訊息永遠不會再出現在任何人的未讀裡，而且沒有錯誤訊息。
//   ⇒ 本類只**組**簡報並回傳「可推到哪」；真正推游標由呼叫端在回傳檔落地之後呼叫 AdvanceAfterWrite。
// 與 Editor 版刻意的差異（兩處，都寫在這裡讓人查得到）：
//   ① 補足段的去重比 **seq**（Editor 比物件參考 —— 它有共用快取，本側每次讀都是新物件）。
//   ② 在線名單讀不了的 lock（Unknown）照 Editor 版丟掉，但**計數印出來**（Editor 只寫 warning log，畫面上看不到）。
// 數值影響：純讀（游標推進另走 AdvanceAfterWrite）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Tavern
{
    public sealed class SCP_TavernCatchupResult
    {
        /// <summary>簡報本文 —— **尚未**含最後那行游標結果（那行要等回傳檔落地、推完游標才知道）。</summary>
        public string Body = "";
        /// <summary>顯示出來的未讀筆數（排除自己與系統廣播之後）。</summary>
        public int Unread;
        /// <summary>可安全推進到的水位；null＝不得推進（0 筆未讀／積壓超過回捲上限）。</summary>
        public string? NewestTs;
        public bool Truncated;
    }

    public static class SCP_TavernCatchup
    {
        public const int DefaultMinCount = 10;
        public const int DefaultInboxShow = 10;

        public static SCP_TavernCatchupResult Build(
            string iDataRoot, string iLettersRoot, string iPersona, string iRoom, int iMinCount,
            bool iQuietSystem, bool iIncludeSelf, int iInboxShow)
        {
            var aOut = new SCP_TavernCatchupResult();
            string room = string.IsNullOrEmpty(iRoom) ? "tavern" : iRoom;
            int minCount = iMinCount <= 0 ? DefaultMinCount : iMinCount;
            var sb = new StringBuilder();

            string? cursorBefore = SCP_TavernCursor.ReadCursor(iDataRoot, iPersona);
            sb.AppendLine($"# 📬 叮 catchup — {iPersona}　`{room}`");
            sb.AppendLine();
            sb.AppendLine($"- 游標（本次之前）：{(string.IsNullOrEmpty(cursorBefore) ? "**（從未設過）** —— 下面會是最近 " + minCount + " 筆，不是全庫" : "`" + cursorBefore + "`")}");
            sb.AppendLine();

            AppendOnline(sb, iLettersRoot, iPersona);

            // ── 未讀 ──
            var unread = SCP_TavernCursor.ReadUnread(iDataRoot, iPersona, room, out string? newestTs, out bool truncated);
            var shown = new List<SCP_TavernMessage>();
            int hiddenSystem = 0, hiddenSelf = 0;
            foreach (var m in unread)
            {
                if (!iIncludeSelf && IsMine(m, iPersona)) { hiddenSelf++; continue; }
                if (iQuietSystem && IsSystem(m)) { hiddenSystem++; continue; }
                shown.Add(m);
            }
            aOut.Unread = shown.Count;
            aOut.NewestTs = newestTs;
            aOut.Truncated = truncated;

            // 未讀不足 minCount 時補最近的舊訊息（Tim 2026-05-28），補進來的要標記。
            var backfill = new List<SCP_TavernMessage>();
            if (shown.Count < minCount)
            {
                var shownSeq = new HashSet<int>();
                foreach (var m in shown) shownSeq.Add(m.Seq);
                var recent = SCP_TavernRead.Tail(iDataRoot, room, minCount * 3);
                for (int i = recent.Count - 1; i >= 0 && backfill.Count < (minCount - shown.Count); i--)
                {
                    var m = recent[i];
                    if (shownSeq.Contains(m.Seq)) continue;
                    if (!iIncludeSelf && IsMine(m, iPersona)) continue;
                    if (iQuietSystem && IsSystem(m)) continue;
                    backfill.Insert(0, m);
                }
            }

            sb.AppendLine($"## 💬 未看訊息　**{shown.Count}** 筆"
                + (hiddenSelf > 0 ? $"（已排除自己 {hiddenSelf} 筆）" : "")
                + (hiddenSystem > 0 ? $"（已隱藏酒保系統廣播 {hiddenSystem} 筆 —— 打款／獎金可能在裡面，`quiet_system=0` 看得到）" : ""));
            if (truncated)
                sb.AppendLine("⚠ **未讀一次交付不完** —— 這批是**最舊的**那段；更新的還留在未讀裡，"
                    + "再跑一次 catchup 會接著給（不會遺失）。");
            sb.AppendLine();
            ReadClips(iDataRoot, out int clipNormal, out int clipMention);
            foreach (var m in shown)
            {
                bool mentioned = MentionsMe(m, iPersona);
                AppendMsg(sb, m, mentioned ? "🔔 **@你**" : "", mentioned ? clipMention : clipNormal);
            }
            if (backfill.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine($"### 🔁 補足最近 {minCount} 筆（**這些是已看過的**，不是新訊息）");
                foreach (var m in backfill) AppendMsg(sb, m, "（已讀）", clipNormal);
            }

            AppendInbox(sb, iDataRoot, iPersona, iInboxShow <= 0 ? DefaultInboxShow : iInboxShow);
            sb.AppendLine();
            aOut.Body = sb.ToString();
            return aOut;
        }

        /// <summary>
        /// 回傳檔落地**之後**才呼叫：推游標並讀回確認。回傳那一行要附在簡報最後的文字，以及讀回的值。
        /// </summary>
        public static (string Line, string? AdvancedTo) AdvanceAfterWrite(
            string iDataRoot, string iPersona, SCP_TavernCatchupResult iBuilt, bool iAdvance)
        {
            if (!iAdvance)
                return ("- 游標：**未推進**（`advance=0`）—— 這次讀到的下次還會再出現。", null);
            if (string.IsNullOrEmpty(iBuilt.NewestTs))
                return (iBuilt.Truncated
                    ? "- 游標：**未推進**（積壓超過回捲上限 —— 最舊的未讀還沒進到窗口）"
                      + " ⇒ 推了就會永久跳過它們。請先消化積壓或調高 `BACKLOG_SCAN_CAP`。"
                    : "- 游標：**未推進**（本次 0 筆未讀）—— 沒有讀數就不該移動水位。", null);
            string? aErr = SCP_TavernCursor.WriteCursor(iDataRoot, iPersona, iBuilt.NewestTs!);
            string? readBack = SCP_TavernCursor.ReadCursor(iDataRoot, iPersona);
            string aLine = readBack == iBuilt.NewestTs
                ? $"- ✓ 游標已推進到 `{readBack}`（寫入後讀回確認）"
                : $"- ✗ 游標寫入後讀回不符：期望 `{iBuilt.NewestTs}`、實際 `{readBack}` —— 下次會重讀這一段"
                  + (aErr != null ? $"（寫入丟了：{aErr}）" : "");
            return (aLine, readBack);
        }

        // 判準：body 裡出現 `@<persona>`（不分大小寫）；刻意不認顯示名或 agent 名（那兩個會變）。
        static bool MentionsMe(SCP_TavernMessage m, string iPersona)
            => !string.IsNullOrEmpty(iPersona)
               && (m.Body ?? "").IndexOf("@" + iPersona, StringComparison.OrdinalIgnoreCase) >= 0;

        static bool IsMine(SCP_TavernMessage m, string iPersona)
            => !string.IsNullOrEmpty(iPersona)
               && string.Equals(m.SenderPersona, iPersona, StringComparison.OrdinalIgnoreCase);

        // 系統廣播＝酒保代發的自動訊息。判準走 sender_id，不看內容關鍵字（會吃掉同事**談論**打款的訊息）。
        static bool IsSystem(SCP_TavernMessage m)
        {
            string s = m.SenderId ?? "";
            return string.Equals(s, "tavern-keeper", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "subconscious-daemon", StringComparison.OrdinalIgnoreCase);
        }

        static void AppendMsg(StringBuilder sb, SCP_TavernMessage m, string iMark, int iClip)
        {
            string tag = m.Meta.TryGetValue("tag", out string? tg) && !string.IsNullOrEmpty(tg) ? $" «{tg}»" : "";
            string name = string.IsNullOrEmpty(m.SenderName) ? (string.IsNullOrEmpty(m.SenderId) ? "?" : m.SenderId) : m.SenderName;
            string persona = string.IsNullOrEmpty(m.SenderPersona) ? "" : "@" + m.SenderPersona;
            sb.AppendLine($"- **[seq {m.Seq}]** {LocalHm(m.Ts)} **{name}{persona}**{tag} {iMark}");
            string body = (m.Body ?? "").Replace("\r", "").Replace("\n", " ⏎ ").Trim();
            if (iClip > 0 && body.Length > iClip) body = body.Substring(0, iClip) + $"…（全文 {body.Length} 字）";
            sb.AppendLine($"    {body}");
        }

        static string LocalHm(string iTs)
        {
            if (DateTime.TryParse(iTs, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                return t.ToLocalTime().ToString("MM-dd HH:mm:ss");
            return "??:??:??";
        }

        // 顯示截斷（`ChatTavern/render_settings.json`，與 Editor `UCL_ChatTavernSettings` 同鍵同預設同夾值）。
        static void ReadClips(string iDataRoot, out int oNormal, out int oMention)
        {
            oNormal = 600; oMention = 1500;
            try
            {
                string aPath = Path.Combine(iDataRoot, "ChatTavern", "render_settings.json");
                if (!File.Exists(aPath)) return;
                var aJd = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                oNormal = Clip(aJd.GetInt("message_body_clip", 600));
                oMention = Clip(aJd.GetInt("message_body_clip_mentioned", 1500));
            }
            catch (Exception) { /* 只影響顯示長度，讀不到就用預設 */ }
        }

        static int Clip(int v) => v <= 0 ? 0 : Math.Max(80, Math.Min(4000, v));

        // ⚠ 空清單**不是**「沒有人在線」，是「查不到 lock」—— 兩者必須長得不一樣。
        static void AppendOnline(StringBuilder sb, string iLettersRoot, string iMe)
        {
            SCP_PersonaScan aScan;
            try { aScan = SCP_PersonaLetters.Scan(iLettersRoot); }
            catch (Exception e)
            {
                sb.AppendLine($"## 🟢 在線：**讀取失敗** —— {e.Message}（空 ≠ 沒人）");
                sb.AppendLine();
                return;
            }
            var online = new List<SCP_PersonaStatus>();
            int unknown = 0;
            foreach (var p in aScan.Personas)
            {
                if (p.Online == SCP_PersonaOnline.Online) online.Add(p);
                else if (p.Online == SCP_PersonaOnline.Unknown && File.Exists(p.LockPath)) unknown++;
            }
            online.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));   // Editor 版用 Ordinal
            sb.AppendLine($"## 🟢 在線（{online.Count}）");
            if (online.Count == 0)
                sb.AppendLine("- （查不到任何 lock）—— **空不代表沒人**，只代表這裡讀不到。");
            foreach (var l in online)
            {
                string me = string.Equals(l.Name, iMe, StringComparison.OrdinalIgnoreCase) ? "　← 你" : "";
                string status = ReadNowStatus(iLettersRoot, l);
                sb.AppendLine($"- **{l.Name}**（{l.Agent}）{(status.Length == 0 ? "" : "　💬 " + status)}{me}");
            }
            if (unknown > 0)
                sb.AppendLine($"- ⚠ 另有 {unknown} 個 lock 讀不了（壞檔／被佔用）—— 沒列進來，不代表他們不在線");
            sb.AppendLine();
        }

        // now_status 的 session_key **與** locked_at 都要等於這一場 lock 的 —— 對不上＝上一場的殘留（TASK-0294）。
        static string ReadNowStatus(string iLettersRoot, SCP_PersonaStatus iLock)
        {
            try
            {
                string aPath = SCP_LettersPaths.NowStatusPath(new SCP_LettersRoot(iLettersRoot), iLock.Name);
                if (!File.Exists(aPath)) return "";
                var aJd = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                if (!string.Equals(aJd.GetString("session_key", ""), iLock.SessionKey, StringComparison.Ordinal)
                    || !string.Equals(aJd.GetString("locked_at", ""), iLock.LockedAt, StringComparison.Ordinal)) return "";
                return aJd.GetString("now_status", "");
            }
            catch (Exception) { return ""; }   // 只供顯示、不閘任何行為
        }

        // persona 層 inbox 摘要。ack 的語意是「已處理」不是「已看過」⇒ 這裡只列，不代為歸檔。
        static void AppendInbox(StringBuilder sb, string iDataRoot, string iPersona, int iShow)
        {
            string raw;
            try
            {
                string aPath = SCP_TavernInbox.InboxPath(iDataRoot, "tavern", iPersona);
                // Editor 版 ReadInbox 對不存在的檔回字面 "(inbox 為空)"（非空白）⇒ 下面印「0 筆待處理」。照抄這個行為。
                raw = File.Exists(aPath) ? File.ReadAllText(aPath, Encoding.UTF8) : "(inbox 為空)";
            }
            catch (Exception e)
            {
                sb.AppendLine($"## 📥 inbox：讀取失敗 —— {e.Message}");
                return;
            }
            if (string.IsNullOrWhiteSpace(raw))
            {
                sb.AppendLine("## 📥 inbox：（空）");
                return;
            }
            // 條目判準是標題行 `## [seq=…]`（⛔ 不能用 SCP_TavernInbox.SplitEntries —— 它切任何 `## `，body 裡的小標會被切開）
            var lines = raw.Replace("\r", "").Split('\n');
            var titles = new List<string>();
            var snippets = new List<string>();
            var atLines = new List<string>();
            string? curTitle = null, curFirstBody = null, curAt = null;
            foreach (var ln in lines)
            {
                if (ln.StartsWith("## [seq=", StringComparison.Ordinal))
                {
                    if (curTitle != null) { titles.Add(curTitle); snippets.Add(curFirstBody ?? ""); atLines.Add(curAt ?? ""); }
                    curTitle = ln.Substring(3).Trim();
                    curFirstBody = null;
                    curAt = null;
                }
                else if (curTitle != null && ln.TrimStart().StartsWith("_at ", StringComparison.Ordinal))
                {
                    curAt = ln.Trim();
                }
                else if (curTitle != null && curFirstBody == null)
                {
                    string t = ln.Trim();
                    if (t.Length > 0 && !t.StartsWith("_at ", StringComparison.Ordinal)
                        && !t.StartsWith(">", StringComparison.Ordinal) && !t.StartsWith("---", StringComparison.Ordinal))
                        curFirstBody = t.Length > 90 ? t.Substring(0, 90) + "…" : t;
                }
            }
            if (curTitle != null) { titles.Add(curTitle); snippets.Add(curFirstBody ?? ""); atLines.Add(curAt ?? ""); }

            var nowUtc = DateTime.UtcNow;
            var fresh = new List<int>();
            int stale = 0;
            for (int i = 0; i < titles.Count; i++)
            {
                if (SCP_TavernInbox.IsEntryStale(atLines[i], nowUtc)) stale++;
                else fresh.Add(i);
            }
            int days = SCP_TavernInbox.MaxAgeDays;
            sb.AppendLine($"## 📥 inbox（persona 層）　**{fresh.Count}** 筆待處理"
                + $"（{days} 天內）"
                + (fresh.Count > iShow ? $"，以下為最新 {iShow} 筆" : "")
                + (stale > 0 ? $"　·　⚠ 另有 **{stale}** 筆超過 {days} 天已折起（仍在 inbox 檔裡，未歸檔）" : ""));
            for (int k = Math.Max(0, fresh.Count - iShow); k < fresh.Count; k++)
            {
                int i = fresh[k];
                sb.AppendLine($"- {titles[i]}");
                if (!string.IsNullOrEmpty(snippets[i])) sb.AppendLine($"    ↳ {snippets[i]}");
            }
            sb.AppendLine();
            sb.AppendLine("　↳ 處理完才歸檔（ack ＝ **已處理**，不是已看過）：`inbox_ack.py --agent " + iPersona + "`");
        }
    }
}
