// 區塊職責：per-persona 酒館已讀游標的讀寫（`ChatTavern/_inbox_cursor/<persona>.json`）＋「還沒看過哪些訊息」。
// 物理意義：移植自 UCL_Core `UCL_TavernCursor`（TASK-0303：早安 catchup 不再依賴 Editor）。
//          判準只有一條：**`ts > last_seen_ts` 即未讀**（ISO-8601 UTC 同格式時字典序＝時間序）。
//          欄位名 `last_seen_ts` / `updated_at` 與 Editor 端、python 端一致，⛔ 別改。
// ⚠ 游標是 read-modify-write。Editor 端原本只有「單調」一道擋板而**沒有跨 process 鎖** ——
//   單調擋不住這個交錯：兩端都讀到 X，寫 Z 的先完成，另一端再寫 Y（X < Y < Z）⇒ 水位倒退、整段重播。
//   ⇒ 本側的比較＋寫入整段包在 `SCP_FileLock` 裡。📌 Editor 端（FreeTime／StreamWatch）還沒改走這裡，
//   它的寫入**不拿這把鎖** —— 兩端並存期間這個窗口仍在，收斂成單一實作是後續的事（寫在 TASK-0303）。
// 數值影響：讀取由舊到新交付一批 SCAN_LIMIT 則（必要時往回捲到 BACKLOG_SCAN_CAP）；推進游標寫一次檔（原子）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Io;
using SCP.Core.Json;

namespace SCP.Core.Tavern
{
    public static class SCP_TavernCursor
    {
        /// <summary>一次交付的未讀**批量**（不是「只看得到這麼多」）。與 Editor 端同值。</summary>
        public const int SCAN_LIMIT = 60;

        /// <summary>為了找出「最舊的那則未讀」最多往回捲多少則 —— **成本上限，不是正確性上限**：
        /// 捲不到底時 oNewestTs 回 null（拒推游標）。與 Editor 端同值。</summary>
        public const int BACKLOG_SCAN_CAP = 4000;

        public static string CursorPath(string iDataRoot, string iPersona)
            => Path.Combine(iDataRoot, "ChatTavern", "_inbox_cursor", iPersona + ".json").Replace('\\', '/');

        /// <summary>讀 last_seen_ts；沒有游標檔／讀不到／壞檔回 null（語意＝「從未設過」）。</summary>
        public static string? ReadCursor(string iDataRoot, string iPersona)
        {
            try
            {
                string aPath = CursorPath(iDataRoot, iPersona);
                if (!File.Exists(aPath)) return null;
                string aTs = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8)).GetString("last_seen_ts", "");
                return aTs.Length == 0 ? null : aTs;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// 推進游標（單調＋原子＋跨 process 鎖）。回傳 null＝寫了或本來就不必寫；非 null＝失敗原因。
        /// ⚠ 呼叫端要**讀回**比對才能宣告推進成功 —— 回傳 null 只代表沒丟例外。
        /// </summary>
        public static string? WriteCursor(string iDataRoot, string iPersona, string iLastSeenTs)
        {
            if (string.IsNullOrEmpty(iLastSeenTs)) return null;
            string aPath = CursorPath(iDataRoot, iPersona);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
                using (SCP_FileLock.Acquire(aPath))
                {
                    string? aCur = ReadCursor(iDataRoot, iPersona);
                    // 單調：只前進不後退（Editor 端／python 端同一條規則）
                    if (!string.IsNullOrEmpty(aCur) && string.CompareOrdinal(iLastSeenTs, aCur) <= 0) return null;
                    var aJd = SCP_JsonData.NewObject();
                    aJd["last_seen_ts"] = iLastSeenTs;
                    aJd["updated_at"] = DateTime.UtcNow.ToString("o");
                    string aTmp = aPath + ".tmp";
                    File.WriteAllText(aTmp, SCP_JsonWriter.Write(aJd, SCP_JsonStyle.UclLegacy), new UTF8Encoding(false));
                    // File.Replace 而不是 Delete＋Move：後者之間檔案不存在，不拿鎖的讀取端會讀成「從未設過」
                    // ⇒ 整部歷史重新變未讀（TASK-0264 同一族）。
                    if (File.Exists(aPath)) File.Replace(aTmp, aPath, null);
                    else File.Move(aTmp, aPath);
                }
                return null;
            }
            catch (Exception e) { return e.GetType().Name + ": " + e.Message; }
        }

        /// <summary>
        /// 取未讀訊息（不推進 —— 推進由呼叫端在**印出來之後**才做）。
        /// oNewestTs＝這一批真的交付出去的最新 ts（＝可安全推進的水位）；null 時呼叫端一律不得推進。
        /// oTruncated＝還有未讀沒交付。沒有游標時不回放整部歷史，只給最近 SCAN_LIMIT 則。
        /// </summary>
        public static List<SCP_TavernMessage> ReadUnread(string iDataRoot, string iPersona, string iRoom,
                                                         out string? oNewestTs, out bool oTruncated)
        {
            oNewestTs = null;
            oTruncated = false;
            var aResult = new List<SCP_TavernMessage>();
            string? aCursor = ReadCursor(iDataRoot, iPersona);

            if (string.IsNullOrEmpty(aCursor))
            {
                foreach (var aMsg in SCP_TavernRead.Tail(iDataRoot, iRoom, SCAN_LIMIT))
                {
                    if (string.IsNullOrEmpty(aMsg.Ts)) continue;
                    aResult.Add(aMsg);
                    if (oNewestTs == null || string.CompareOrdinal(aMsg.Ts, oNewestTs) > 0) oNewestTs = aMsg.Ts;
                }
                return aResult;
            }

            // 往回捲到「窗口最舊的那則已經讀過」為止 —— 只有這個條件成立，才證明最舊的未讀在窗口內。
            int aWindow = SCAN_LIMIT;
            List<SCP_TavernMessage> aScan;
            bool aReachedOldest = false;
            while (true)
            {
                aScan = SCP_TavernRead.Tail(iDataRoot, iRoom, aWindow);
                if (aScan.Count < aWindow) { aReachedOldest = true; break; }
                string? aOldestTs = null;
                foreach (var aMsg in aScan)
                {
                    if (string.IsNullOrEmpty(aMsg.Ts)) continue;
                    aOldestTs = aMsg.Ts;
                    break;
                }
                if (aOldestTs == null || string.CompareOrdinal(aOldestTs, aCursor) <= 0) { aReachedOldest = true; break; }
                if (aWindow >= BACKLOG_SCAN_CAP) break;
                aWindow = Math.Min(aWindow * 4, BACKLOG_SCAN_CAP);
            }

            var aUnread = new List<SCP_TavernMessage>();
            foreach (var aMsg in aScan)
            {
                if (string.IsNullOrEmpty(aMsg.Ts)) continue;
                if (string.CompareOrdinal(aMsg.Ts, aCursor) <= 0) continue;
                aUnread.Add(aMsg);
            }

            if (!aReachedOldest)
            {
                // 捲到上限仍沒碰到已讀邊界 ⇒ 最舊的未讀不在手上 ⇒ 任何推進都會跳過它們，拒推。
                oTruncated = true;
                for (int i = 0; i < aUnread.Count && i < SCAN_LIMIT; i++) aResult.Add(aUnread[i]);
                return aResult;
            }

            int aTake = Math.Min(aUnread.Count, SCAN_LIMIT);
            for (int i = 0; i < aTake; i++)
            {
                aResult.Add(aUnread[i]);
                if (oNewestTs == null || string.CompareOrdinal(aUnread[i].Ts, oNewestTs) > 0) oNewestTs = aUnread[i].Ts;
            }
            oTruncated = aUnread.Count > aTake;
            return aResult;
        }
    }
}
