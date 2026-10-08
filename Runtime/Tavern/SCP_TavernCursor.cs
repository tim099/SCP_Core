// 區塊職責：per-persona 酒館已讀游標的讀寫（`ChatTavern/_inbox_cursor/<persona>.json`）＋「還沒看過哪些訊息」。
// 物理意義：TASK-0303。判準只有一條：**`ts > last_seen_ts` 即未讀**（ISO-8601 UTC 同格式時字典序＝時間序）。
//          欄位名 `last_seen_ts` / `updated_at` 與 python 端一致，⛔ 別改。
// ⚠ 游標是 read-modify-write。只有「單調」一道擋板而**沒有跨 process 鎖**的話，
//   單調擋不住這個交錯：兩端都讀到 X，寫 Z 的先完成，另一端再寫 Y（X < Y < Z）⇒ 水位倒退、整段重播。
//   ⇒ 比較＋寫入整段包在 `SCP_FileLock` 裡。
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
        /// <summary>一次交付的未讀**批量**（不是「只看得到這麼多」）。</summary>
        public const int SCAN_LIMIT = 60;

        /// <summary>回捲上限的**預設值**（則）。實際值讀 `render_settings.json` 的 `backlog_scan_cap`（酒館設定頁可改）。
        /// 積壓超過它時更舊的那段不讀、由 catchup 點名（TASK-0407）。
        /// Tim 2026-10-06（TASK-0408）：「太舊」＝ 100 則 —— 過舊的不處理，重要訊息走掛號信。</summary>
        public const int BACKLOG_SCAN_CAP = 100;

        public static string CursorPath(string iDataRoot, string iPersona)
            => Path.Combine(SCP.Core.Paths.SCP_DataPaths.ChatTavern(new SCP.Core.Paths.SCP_DataRoot(iDataRoot)), "_inbox_cursor", iPersona + ".json").Replace('\\', '/');

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
                    // 單調：只前進不後退（python 端同一條規則）
                    if (!string.IsNullOrEmpty(aCur) && string.CompareOrdinal(iLastSeenTs, aCur) <= 0) return null;
                    var aJd = SCP_JsonData.NewObject();
                    aJd["last_seen_ts"] = iLastSeenTs;
                    aJd["updated_at"] = DateTime.UtcNow.ToString("o");
                    string aTmp = aPath + ".tmp";
                    File.WriteAllText(aTmp, SCP_JsonWriter.Write(aJd, SCP_JsonStyle.UclLegacy), new UTF8Encoding(false));
                    // File.Replace 而不是 Delete＋Move：後者之間檔案不存在，不拿鎖的讀取端會讀成「從未設過」
                    // ⇒ 整部歷史重新變未讀（TASK-0264 同一族）。
                    SCP_TextFile.ReplaceOrMove(aTmp, aPath);
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
            => ReadUnread(iDataRoot, iPersona, iRoom, SCP_TavernRenderSettings.ReadOrDefault(iDataRoot).BacklogCap,
                          out oNewestTs, out oTruncated, out _);

        /// <summary>
        /// 同上，回捲上限由呼叫端給（<paramref name="iBacklogCap"/>），並回報有沒有跳過太舊的那段（TASK-0407）。
        /// <para>積壓超過回捲上限時：<b>上限內照常由舊到新交付</b>（游標跟著批次往前），上限之外更舊的那段<b>不讀</b>，
        /// 並在 <paramref name="oSkip"/> 回報從哪個游標起、到哪一則之前、至少幾則。⛔ 不需要任何顯式參數。</para>
        /// <para>🩸 為什麼改成自動（TASK-0369 當時選了顯式參數）：跳過等於宣告「那段我沒讀」，但「拒推」讓游標每天更舊、
        /// 照預設跑<b>永遠解不開</b>（Template 的游標停在 08-31 不動）；Tim 2026-10-05 把那個選擇翻掉。
        /// 宣告改由回傳檔承擔：跳過的那段照實點名，⛔ 不靜默。</para>
        /// <para>⚠ 積壓<b>剛好</b>落在上限內（更舊的那端沒有比游標新的訊息）⇒ 什麼都沒跳，<paramref name="oSkip"/>.Applied＝false。</para>
        /// </summary>
        public static List<SCP_TavernMessage> ReadUnread(string iDataRoot, string iPersona, string iRoom, int iBacklogCap,
                                                         out string? oNewestTs, out bool oTruncated,
                                                         out SCP_TavernBacklogSkip oSkip)
        {
            oNewestTs = null;
            oTruncated = false;
            oSkip = default;
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
            // ⚠ 上限可以比一批小（合法 1～10000，TASK-0408）⇒ 窗口照上限取，⛔ 不撐回 SCAN_LIMIT（設 10 就只讀最新 10 則）。
            int aCap = Math.Max(1, iBacklogCap);
            int aWindow = Math.Min(SCAN_LIMIT, aCap);
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
                if (aWindow >= aCap) break;
                aWindow = Math.Min(aWindow * 4, aCap);
            }

            // 捲到上限仍沒碰到已讀邊界：窗口（最新 aCap 則）整個都比游標新。窗口**外**還有沒有比游標新的？
            //   多撈 SCAN_LIMIT 則探一下 —— 有 ⇒ 那是「太舊、不讀」的一段（至少這麼多則）；一則都沒有 ⇒ 未讀剛好全在窗口內，什麼都沒跳。
            int aOutsideAtLeast = 0;
            if (!aReachedOldest)
            {
                List<SCP_TavernMessage> aProbe = SCP_TavernRead.Tail(iDataRoot, iRoom, aCap + SCAN_LIMIT);
                int aOutside = Math.Max(0, aProbe.Count - aScan.Count);
                for (int i = 0; i < aOutside; i++)
                    if (!string.IsNullOrEmpty(aProbe[i].Ts) && string.CompareOrdinal(aProbe[i].Ts, aCursor) > 0) ++aOutsideAtLeast;
                if (aOutsideAtLeast == 0) aReachedOldest = true;
            }

            var aUnread = new List<SCP_TavernMessage>();
            foreach (var aMsg in aScan)
            {
                if (string.IsNullOrEmpty(aMsg.Ts)) continue;
                if (string.CompareOrdinal(aMsg.Ts, aCursor) <= 0) continue;
                aUnread.Add(aMsg);
            }

            // 太舊的那段被跳過 ⇒ 點名：游標之後、窗口第一則之前（交付從窗口第一則起，由舊到新，跟平常一樣分批）。
            if (!aReachedOldest)
                oSkip = new SCP_TavernBacklogSkip(true, aOutsideAtLeast, aCursor!, aUnread.Count > 0 ? aUnread[0].Seq : 0);

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

    /// <summary>
    /// 「太舊的那段不讀」實際跳過了什麼（TASK-0369／0407）。<see cref="Applied"/>＝false ⇒ 沒有跳（積壓在上限內）。
    /// <para>⚠ <see cref="SkippedAtLeast"/> 是**下限**：只往窗口外多探了 SCAN_LIMIT 則，更舊的沒數到。</para>
    /// </summary>
    public readonly struct SCP_TavernBacklogSkip
    {
        public readonly bool Applied;
        /// <summary>回捲窗口**外**、比游標新而被跳過的筆數（**至少**這麼多 —— 更舊的沒數到）。</summary>
        public readonly int SkippedAtLeast;
        /// <summary>跳過那段的起點 ＝ 本次之前的游標（嚴格大於它的才是未讀）。</summary>
        public readonly string FromCursorTs;
        /// <summary>交付出去的第一則 seq ⇒ 被跳過的是「游標之後、這一則之前」。0 ＝ 沒交付任何一則。</summary>
        public readonly int FirstKeptSeq;

        public SCP_TavernBacklogSkip(bool iApplied, int iSkippedAtLeast, string iFromCursorTs, int iFirstKeptSeq)
        {
            Applied = iApplied;
            SkippedAtLeast = iSkippedAtLeast;
            FromCursorTs = iFromCursorTs ?? "";
            FirstKeptSeq = iFirstKeptSeq;
        }
    }
}
