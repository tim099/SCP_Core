// 區塊職責：**房間 inbox 的附加與修剪** —— `<資料根>/ChatTavern/rooms/<room>/inbox/<id>.md` 的唯一寫入實作（TASK-0299）。
// 物理意義：Tim 2026-09-25「直接打 tavern-write 的訊息不會觸發 @mention 通知 —— 優先處理」。
//           mention 通知要搬到寫入端（Server 也要能寫 inbox）⇒ inbox 的附加／修剪從 Editor 的
//           `UCL_ChatTavernQuestIO.AppendInbox` 搬到這裡，**Editor 那支改成呼叫本類** —— ⛔ 不留兩份。
// 數值影響：條目格式、數量上限（50）、年齡上限（7 天）、歸檔檔名與截斷標記**逐字搬自 Editor 版（2026-09-25）**。
//   兩件是新的：
//   ① **跨 process 鎖**（`<id>.md.lock`，FileShare.None）包住「附加＋修剪」整段 ——
//      修剪是整檔重寫，兩個寫入端（Editor 與 Server）交錯時後寫的會把先寫的那條吃掉（TASK-0264 同族）。
//      ⚠ 射程：python `inbox_ack.py` **不走這把鎖**（既有缺口，本單不處理）。
//   ② **同一個 (seq, 標題) 已經在 inbox 就不再附加** —— 切換期間兩條路各發一次時，第二次是 no-op。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace SCP.Core.Tavern
{
    public enum SCP_InboxAppendOutcome { Appended, Duplicate }

    public static class SCP_TavernInbox
    {
        public const int CapMax = 50;       // 觸發 trim 門檻
        public const int CapKeep = 50;      // trim 後保留最新 N 條
        public const int MaxAgeDays = 7;    // 年齡上限（Tim 2026-09-02）
        const int LockWaitMs = 3000;

        public static string InboxDir(string iDataRoot, string iRoom) => SCP_TavernRooms.RoomDir(iDataRoot, iRoom) + "/inbox";
        public static string InboxPath(string iDataRoot, string iRoom, string iAgentId) => InboxDir(iDataRoot, iRoom) + "/" + iAgentId + ".md";

        /// <summary>
        /// 附加一筆條目（＋lazy trim）。整段在跨 process 鎖內。⚠ 拿不到鎖 ⇒ 丟例外（呼叫端決定要不要吞），⛔ 不無鎖硬寫。
        /// </summary>
        public static SCP_InboxAppendOutcome Append(string iDataRoot, string iRoom, string iAgentId, int iEventSeq,
                                                    string iTitle, string iBody, Action<string>? iLog = null)
        {
            Directory.CreateDirectory(InboxDir(iDataRoot, iRoom));
            string aPath = InboxPath(iDataRoot, iRoom, iAgentId);
            using (AcquireLock(aPath + ".lock"))
            {
                string aHeadPrefix = $"## [seq={iEventSeq}] {iTitle} (";
                if (File.Exists(aPath))
                {
                    foreach (string aLine in File.ReadLines(aPath, Encoding.UTF8))
                        if (aLine.StartsWith(aHeadPrefix, StringComparison.Ordinal)) return SCP_InboxAppendOutcome.Duplicate;
                }
                var sb = new StringBuilder();
                if (!File.Exists(aPath))
                {
                    sb.AppendLine($"> 📥 **{iAgentId}** 的 inbox — 新到最舊由上往下 append。時間為**本機時區**。");
                    sb.AppendLine($"> 處理完跑 `inbox_ack.py` 歸檔；要看被截斷的全文跑 `tavern_query.py seq <N> --full`。");
                }
                sb.AppendLine();
                // 時區顯式標註（crest-001 QA 2026-07-29）＋ 下一行的 UTC 權威時戳（跨房可比的唯一水位；沿革見 git 歷史 UCL_ChatTavernQuestIO）
                sb.AppendLine($"## [seq={iEventSeq}] {iTitle} ({DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zz", CultureInfo.InvariantCulture)})");
                sb.AppendLine($"_at {DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture)}_");
                if (!string.IsNullOrEmpty(iBody)) { sb.AppendLine(); sb.AppendLine(iBody); }
                File.AppendAllText(aPath, sb.ToString(), new UTF8Encoding(false));
                try { TrimIfOverCap(aPath, iAgentId, iRoom, iLog); }
                catch (Exception e) { iLog?.Invoke($"[Inbox] trim 失敗（容忍，append 已完成）：{e.Message}"); }
                return SCP_InboxAppendOutcome.Appended;
            }
        }

        /// <summary>這筆條目是不是「太舊」。⚠ 拿不到時戳一律當**舊**（新條目必有 `_at`；舊格式殘留的沿革見 git 歷史 UCL_ChatTavernQuestIO，2026-09-25 之前）。</summary>
        public static bool IsEntryStale(string iEntry, DateTime iNowUtc, int iMaxAgeDays = MaxAgeDays)
        {
            Match m = Regex.Match(iEntry, @"(?m)^_at (\S+?)Z?_\s*$");
            if (!m.Success) return true;
            if (!DateTime.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime ts)) return true;
            return (iNowUtc - ts).TotalDays > iMaxAgeDays;
        }

        static void TrimIfOverCap(string iInboxPath, string iAgentId, string iRoom, Action<string>? iLog)
        {
            if (!File.Exists(iInboxPath)) return;
            string raw = File.ReadAllText(iInboxPath, Encoding.UTF8);
            if (string.IsNullOrEmpty(raw)) return;
            List<string> entries = SplitEntries(raw);
            if (entries.Count == 0) return;
            // ① 數量閘
            int countCut = entries.Count > CapMax ? entries.Count - CapKeep : 0;
            // ② 年齡閘：從最舊往新掃，連續「太舊」前綴歸檔；檔頭跟著前綴走但不能自己驅動
            int ageCut = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                if (!entries[i].StartsWith("## ")) { ageCut = i + 1; continue; }
                if (!IsEntryStale(entries[i], DateTime.UtcNow)) break;
                ageCut = i + 1;
            }
            if (ageCut > 0 && !entries[ageCut - 1].StartsWith("## ")) ageCut = 0;
            int archiveCount = Math.Max(countCut, ageCut);
            if (archiveCount <= 0) return;
            string why = countCut >= ageCut
                ? (ageCut > 0 ? $"數量 >{CapMax} 且有 >{MaxAgeDays} 天的" : $"數量 >{CapMax}")
                : $">{MaxAgeDays} 天";
            var archive = new StringBuilder();
            for (int i = 0; i < archiveCount; i++) archive.Append(entries[i]);
            string archivePath = iInboxPath.Replace(".md", "_archive.md");
            File.AppendAllText(archivePath, archive.ToString(), new UTF8Encoding(false));
            var newInbox = new StringBuilder();
            newInbox.AppendLine($"> ⚠ **inbox truncated** — {archiveCount} 條較舊待辦已歸檔到 `{Path.GetFileName(archivePath)}`（規則：{why}；{DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}）");
            newInbox.AppendLine();
            for (int i = archiveCount; i < entries.Count; i++) newInbox.Append(entries[i]);
            File.WriteAllText(iInboxPath, newInbox.ToString(), new UTF8Encoding(false));
            iLog?.Invoke($"[Inbox] trim {iAgentId}@{iRoom}: archived {archiveCount} entries（{why}）→ {Path.GetFileName(archivePath)}; 剩 {entries.Count - archiveCount} 條");
        }

        /// <summary>切條目：檔頭（到第一個 "## "）是 entry 0；之後每個 "## " 起新 entry。沒有任何 "## " ⇒ 空清單（不 trim）。</summary>
        public static List<string> SplitEntries(string iRaw)
        {
            var entries = new List<string>();
            using var sr = new StringReader(iRaw);
            string? line;
            var current = new StringBuilder();
            bool seenHeader = false;
            while ((line = sr.ReadLine()) != null)
            {
                if (line.StartsWith("## "))
                {
                    if (current.Length > 0) { entries.Add(current.ToString()); current.Clear(); }
                    seenHeader = true;
                }
                current.AppendLine(line);
            }
            if (current.Length > 0) entries.Add(current.ToString());
            if (!seenHeader) return new List<string>();
            return entries;
        }

        /// <summary>跨 process 獨佔鎖（開一個 FileShare.None 的鎖檔）。等不到 ⇒ 丟 IOException。</summary>
        static FileStream AcquireLock(string iLockPath)
        {
            var aDeadline = DateTime.UtcNow.AddMilliseconds(LockWaitMs);
            int aAttempt = 0;
            while (true)
            {
                try { return new FileStream(iLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) when (DateTime.UtcNow < aDeadline)
                {
                    Thread.Sleep(Math.Min(50, 5 + 5 * ++aAttempt));
                }
            }
        }
    }
}
