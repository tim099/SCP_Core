// 區塊職責：**舊 `Treasury/` → 新 `Bank/` 的自動遷移**（TASK-0275）。
// 物理意義：2026-09-18 金流權威切到 `Bank/`，而**單據與設定**留在舊 `Treasury/`。
//          TASK-0274 把程式改成只讀新路徑，⛔ 而那只收乾淨了**一棵資料樹**（Florin）——
//          BTC 那棵（`origin/main`）仍在舊路徑，而**同一份 SCP_Core 同時服務兩棵樹**
//          ⇒ 「已遷移」與「還沒遷」在程式碼那側長得一模一樣。
// 數值影響：只搬檔，⛔ 不改內容、不動任何一毛錢。搬完之後全系統只有一條路。
//
// ⚖ 為什麼是「自動搬」而不是「讀不到就退回舊路徑」（Tim 2026-09-22 要求自動觸發）：
//   · **退回舊路徑**是讀取端的 fallback ⇒ 兩條路會一直並存，而「誰還在讀舊的」永遠查不出來。
//   · **自動搬**是一次性的、單向的、會出聲的 ⇒ 搬完之後只剩一條路，而它自己會說它搬了什麼。
//   ⇒ 所以本層刻意做成 **one-way**：只有 `Treasury → Bank`，⛔ 沒有反向。
//
// ⚠ 它是「讀取路徑上的副作用」，而那通常是壞味道。這裡接受它，判準是：
//   要求每個消費端「記得先跑一支遷移指令」＝把正確性押在人記得上，
//   而這個專案已經有一整排血證說那不行。⇒ 掛在必經路上，並且**冪等 ＋ 出聲**。
//
// 🔴 三態要分得開（⛔ 不可以合併成「有沒有搬」）：
//   · 新的在、舊的不在 ⇒ 已是新版，安靜（**最常見，所以它必須零成本**）。
//   · 新的不在、舊的在 ⇒ 搬，並把搬了什麼寫進 report。
//   · **兩個都在** ⇒ ⛔ **不動**，出聲要人處理 —— 那是兩份真相源，
//     而自動挑一份等於幫使用者做了一個他不知道自己做過的決定。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace SCP.Core.Bank
{
    /// <summary>舊 `Treasury/` → 新 `Bank/` 的一次性搬遷。冪等、單向、會出聲。</summary>
    public static class SCP_BankMigration
    {
        /// <summary>要搬的東西：`(舊相對路徑, 新相對路徑, 是不是資料夾)`。</summary>
        public static readonly (string Old, string New, bool IsDir)[] Moves =
        {
            ("Treasury/bank_settings.json", "Bank/bank_settings.json", false),
            ("Treasury/requests",           "Bank/requests",           true),
            ("Treasury/transfer_requests",  "Bank/transfer_requests",  true),
        };

        static readonly HashSet<string> s_Done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        static readonly object s_Lock = new object();

        /// <summary>
        /// 掛在必經路上的入口：**同一棵樹一個 process 只真的跑一次**（之後零成本）。
        /// ⚠ 記憶化的射程只到「這個 process」—— 跨 process 由磁碟狀態本身保證冪等。
        /// </summary>
        public static void EnsureOnce(string iDataRoot)
        {
            if (string.IsNullOrWhiteSpace(iDataRoot)) return;
            lock (s_Lock)
            {
                if (!s_Done.Add(iDataRoot)) return;
            }
            var aReport = new List<string>();
            try { Migrate(iDataRoot, aReport); }
            catch (Exception e) { aReport.Add("遷移丟例外（不擋呼叫端）：" + e.Message); }
            if (aReport.Count > 0) LastReport = aReport;
        }

        /// <summary>最近一次 `EnsureOnce` 有話要說時留在這裡 —— 呼叫端負責印它（本層不假設有 log）。</summary>
        public static List<string>? LastReport { get; private set; }

        /// <summary>印過了就清掉 —— ⛔ 不清的話同一則會在每個呼叫端各印一次，看起來像搬了好幾次。</summary>
        public static void ClearReport() { LastReport = null; }

        /// <summary>真的搬。冪等：已經是新版就回 0 且 <paramref name="oReport"/> 不加東西。</summary>
        public static int Migrate(string iDataRoot, List<string> oReport)
        {
            int aMoved = 0;
            foreach ((string aOldRel, string aNewRel, bool aIsDir) in Moves)
            {
                string aOld = Combine(iDataRoot, aOldRel);
                string aNew = Combine(iDataRoot, aNewRel);
                bool aHasOld = aIsDir ? Directory.Exists(aOld) : File.Exists(aOld);
                bool aHasNew = aIsDir ? Directory.Exists(aNew) : File.Exists(aNew);

                if (aHasNew && aHasOld)
                {
                    // 🔴 兩份真相源。⛔ 不猜哪一份是對的。
                    oReport.Add($"⛔ `{aOldRel}` 與 `{aNewRel}` **兩個都在** ⇒ 沒有搬。"
                                + " 那是兩份真相源，要人看過才知道留哪一份（新的可能是空殼，舊的可能還在被寫）。");
                    continue;
                }
                if (!aHasOld) continue;            // 已是新版（或這棵樹根本沒有這一格）⇒ 安靜

                string? aParent = Path.GetDirectoryName(aNew);
                if (!string.IsNullOrEmpty(aParent)) Directory.CreateDirectory(aParent!);
                if (aIsDir) Directory.Move(aOld, aNew);
                else File.Move(aOld, aNew);
                aMoved++;
                oReport.Add($"🚚 已搬：`{aOldRel}` → `{aNewRel}`（TASK-0275 自動遷移）");
            }
            return aMoved;
        }

        static string Combine(string iRoot, string iRel)
            => Path.Combine(iRoot, iRel.Replace('/', Path.DirectorySeparatorChar));
    }
}
