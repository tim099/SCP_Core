// 區塊職責：晚安的**收工閘** —— 這次上線後動過、還開著、我是參與者，
//           而**最後一次收工之後又動過**（或從沒收過工）的單。
// 物理意義：本檔是 `UCL_TaskReconcile.PendingWrapups` 的移植（Tim 2026-08-31 拍板一起遷）。
//           判準逐條照搬，**不是重寫** —— 那些條件每一條都有血證，換一條就換掉一個防線。
// 數值影響：純讀（tasks/ 一次 ＋ lock 檔一次）。⛔ 只算不改：晚安 `step=check` 的契約是唯讀起手。
//
// ⚠ **判準裡沒有日曆**，這是本檔最容易被「改好看一點」改壞的一格：
//   🩸 血證 2026-08-25（UCL 端，探針 TASK-0029）：舊版用 `UtcNow.Date` 當「今天」再字串比對
//     ⇒ 換日落在 **UTC 午夜 ＝ 本地早上 08:00**，也就是這團隊每天開工的時刻。實測
//     `01:00Z`（本地 09:00）🛑 擋下；`23:50Z`（**本地同一天 07:50**）✅ **靜默放行，一個字都沒說**。
//   📌 一般形：**「今天」是人的概念，人講的今天是本地日；拿 UTC 日代表它，
//     等於把換日點搬到一個沒有人會注意的時刻。**
//   ⇒ 而正解不是換一套曆（Tim 同日拍板「系統面一律 UTC」），是**這裡根本不該用曆**：
//     這道閘問的從來不是「今天」，是「**我這次上線之後**」——
//     而 session 起點寫在 `_session/_persona_<p>.json` 的 `locked_at`（本來就是 UTC）。
//   ⇒ 兩個述詞都是**跟 `locked_at` 比大小的純 UTC 時間戳比較**：零日曆、零時區轉換。
//     附帶好處：跨夜 session 整段都算得進來，而日曆做不到。
//
// ⚠ 解析不出來的時間戳一律**倒向擋下**那一側 —— 因為擋下有出口（`skip_reason`），放行沒有。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Paths;

namespace SCP.Core.Tasks
{
    public static class SCP_TaskReconcile
    {
        // ===========================================================
        // 區塊職責：本次 session 的起點（UTC）。
        // 物理意義：`locked_at` 是早安登入寫的，**它就是這段工作的起點**。
        //          lock 的位置走 SCP_LettersPaths.SessionLockPath（唯一決定點）—— 本檔原本自己拼
        //          `_session/_persona_<p>.json`，那是同一顆檔的第三種算法（TASK-0105 收掉）。
        // ⚠ 讀不到就退回「UTC 今天 00:00」—— 那是拍板的預設曆，**不是本地日**。
        //   讀不到的情境：沒登入就跑閘、lock 被清掉。此時退回日曆是**刻意的降級不是 fail-open**：
        //   它仍然會擋 UTC 今天動過的單，只是拿不到跨夜那一段。
        // ===========================================================
        public static DateTime SessionStartUtc(SCP_DataRoot iRoot, string iPersona, out string oOrigin)
        {
            string aPath = SCP_LettersPaths.SessionLockPath(SCP_DataPaths.Letters(iRoot), iPersona);
            try
            {
                if (File.Exists(aPath))
                {
                    string aLockedAt = ReadJsonStringField(File.ReadAllText(aPath, Encoding.UTF8), "locked_at");
                    if (TryParseUtc(aLockedAt, out DateTime aUtc))
                    {
                        oOrigin = "locked_at ＝ " + aUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                        return aUtc;
                    }
                }
            }
            catch { /* 落到下面的降級 —— 降級是有曆的那個，不是沒有閘 */ }
            oOrigin = "⚠ 讀不到 lock（沒登入／lock 被清掉）⇒ 降級成 UTC 今天 00:00，拿不到跨夜那一段";
            return DateTime.UtcNow.Date;
        }

        // ===========================================================
        // 區塊職責：收工閘的候選單。
        // 物理意義（Tim 2026-08-24 補的洞）：跨多日接回真正會斷的地方不是「忘了寫記憶」，
        //   是**單子還開著、狀態還是 in_progress，而沒有人知道停在哪一步**。
        //   ⇒ 閘的判準是「**這次上線後有動靜**」而不是「有沒有記憶」：
        //     今天沒碰的單不該擋我下線（那是別天的事）。
        // ===========================================================
        public static List<SCP_TaskEntry> PendingWrapups(SCP_DataRoot iRoot, string iPersona,
                                                         DateTime iSinceUtc, Action<string>? iWarn = null)
        {
            var aOut = new List<SCP_TaskEntry>();
            foreach (SCP_TaskEntry e in SCP_TaskIO.LoadAll(iRoot, iWarn))
            {
                if (e.IsClosed()) continue;                              // 已關的不看
                if (!e.HasParticipant(iPersona)) continue;                // 別人的單不看
                if (!IsAfterUtc(e.updated_at, iSinceUtc)) continue;       // ① 這次上線後沒動過的不看
                // ② 因果判準：**最後一次收工之後，有沒有又動過**。
                //   舊版問的是「本次上線後有沒有收過工」⇒ 10:00 收工、11:00 又改了照樣放行，
                //   而那正是閘要防的：**收工留言寫完之後又動了，那份收工紀錄就過期了。**
                //   🩸 UCL 端讀數 2026-08-25（探針 TASK-0042）：wrapup 02:19:50 → updated_at 推到 02:59
                //     ⇒ 舊版收工閘**零命中**。
                DateTime aLastWrapup = LastWrapupUtc(iRoot, e);
                if (aLastWrapup != DateTime.MinValue && !IsAfterUtc(e.updated_at, aLastWrapup)) continue;
                aOut.Add(e);
            }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：這張單**最後一次 `wrapup`** 的時戳（UTC）；沒有回 <see cref="DateTime.MinValue"/>。
        // 來源順序（**刻意不是「缺值就擋」**）：
        //   ① frontmatter 的 `last_wrapup_at` —— `op=wrapup` 寫的，正常路徑走這條
        //   ② 讀不到就**回頭問時間線**（`wrapup` 事件本來就在那裡）
        //   ③ 兩邊都沒有 ⇒ 從來沒收過工 ⇒ 回 MinValue ⇒ 呼叫端**擋下**
        // ⚠ 為什麼不直接「缺值就擋」：`last_wrapup_at` 是 2026-08-25 才加的欄位，
        //   **所有既有單都缺值** ⇒ 一律擋的話，上線當晚每個人都會被自己收過工的舊單擋住
        //   （「修完立刻天天亮」）。時間線是同一件事的既有紀錄，問它就不必回填。
        // ⚠ `last_wrapup_at` **不是第二真相源**：它由 `op=wrapup` 在同一次寫入裡落下，
        //   讀不到時回頭問時間線 —— 兩者永遠指向同一個事件。
        // ===========================================================
        public static DateTime LastWrapupUtc(SCP_DataRoot iRoot, SCP_TaskEntry? e)
        {
            if (e == null) return DateTime.MinValue;
            if (TryParseUtc(e.last_wrapup_at, out DateTime aField)) return aField;
            return LastWrapupFromTimeline(iRoot, e.index);
        }

        /// <summary>時間線裡**最後一筆** `wrapup` 的時戳；沒有回 <see cref="DateTime.MinValue"/>。</summary>
        static DateTime LastWrapupFromTimeline(SCP_DataRoot iRoot, int iIndex)
        {
            DateTime aOut = DateTime.MinValue;
            try
            {
                string aPath = SCP_TaskIO.TaskPath(iRoot, iIndex);
                if (!File.Exists(aPath)) return aOut;
                foreach (string aLine in File.ReadAllLines(aPath, Encoding.UTF8))
                {
                    string aTrim = aLine.TrimStart();
                    if (!aTrim.StartsWith("- ", StringComparison.Ordinal)) continue;
                    if (aLine.IndexOf("`wrapup`", StringComparison.Ordinal) < 0) continue;
                    string aStamp = aTrim.Substring(2).TrimStart();
                    // ⚠ 切法含**全形空白** —— 時間線那幾行用它當分隔，漏了會整行拿去 parse 而失敗
                    //   （失敗＝當成沒收工＝擋下，所以它不會炸，只會讓閘變得太嚴）。
                    int aCut = aStamp.IndexOfAny(new[] { ' ', '\t', '　' });
                    if (aCut > 0) aStamp = aStamp.Substring(0, aCut);
                    if (TryParseUtc(aStamp, out DateTime aUtc) && aUtc > aOut) aOut = aUtc;
                }
            }
            catch { /* 讀不到就當沒有 —— 呼叫端會擋下，而擋下有出口 */ }
            return aOut;
        }

        /// <summary>ISO 時間戳晚於 <paramref name="iSinceUtc"/> 嗎（純 UTC 比大小，不碰時區轉換）。</summary>
        public static bool IsAfterUtc(string iIso, DateTime iSinceUtc)
            => TryParseUtc(iIso, out DateTime aUtc) && aUtc > iSinceUtc;

        static bool TryParseUtc(string? iIso, out DateTime oUtc)
        {
            oUtc = DateTime.MinValue;
            if (string.IsNullOrWhiteSpace(iIso)) return false;
            return DateTime.TryParse(iIso, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out oUtc);
        }

        // ===========================================================
        // 區塊職責：從 lock 檔撈一個字串欄位。
        // 物理意義：只要一格（`locked_at`）⇒ 不拉整套 JSON 反序列化進來。
        // ⚠ 找不到回空字串，而呼叫端把空字串當「讀不到」⇒ 降級成 UTC 日 —— 那條路是刻意的。
        // ===========================================================
        static string ReadJsonStringField(string iJson, string iField)
        {
            string aKey = "\"" + iField + "\"";
            int i = iJson.IndexOf(aKey, StringComparison.Ordinal);
            if (i < 0) return "";
            i = iJson.IndexOf(':', i + aKey.Length);
            if (i < 0) return "";
            int aStart = iJson.IndexOf('"', i + 1);
            if (aStart < 0) return "";
            int aEnd = iJson.IndexOf('"', aStart + 1);
            return aEnd > aStart ? iJson.Substring(aStart + 1, aEnd - aStart - 1) : "";
        }
    }
}
