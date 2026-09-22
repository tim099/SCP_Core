// 區塊職責：**新帳本的每日結帳（warm start）** —— `Bank/closing/<UTC 日>.json`。
// 物理意義：一個 UTC 日一份，內容是「**含**該日全部 entry 之後」的各帳戶餘額。
//          ⇒ 餘額 = 最近一份結帳 + 那一天之後的 entry，讀取成本從 O(全部歷史) 變成 O(今天)。
//          ⚠ 日期一律用 **UTC**，跟 ledger 的日期夾同一套曆 ——
//            兩邊用不同曆會讓結帳邊界與檔案位置對不上，症狀是「餘額偶爾差一點，而且只在半夜出現」。
// 數值影響：本層**一毛錢都不動**。它只讀 ledger、寫 closing 快照；錢永遠只由 `SCP_BankLedger` 動。
//
// 🩸 為什麼現在做（2026-09-22 量的，⛔ 不是覺得會慢）：
//   新帳本 `Bank/ledger` **1014 檔**，而它每天長 100~500 筆（09-17 312／09-18 541／09-21 154）。
//   舊帳本同一條路走到 **21872 檔**，而當年逼出結帳機制的現場是：
//   銀行後台兩個表格各自對 40 個帳戶現場查餘額 ⇒ 開頁卡一分鐘、IMGUI 控制項計數錯亂、
//   Unity 內部 PropertyEditor 連鎖 NullReferenceException。
//   ⇒ 這一格不是效能潔癖，是**同一隻蟲在新帳本上重新長出來的倒數計時**。
//
// ⚠ 判準三條（都在防「餘額是錯的而看起來完全正常」這一族）：
//   ① **只結「完整的日子」** —— 今天還在長 entry，結了它就是一張會過期的快照。
//   ② **鏈要驗得動**：每份結帳記 `prev_date` 與 `entry_count`。讀的時候鏈接不上就
//      ⛔ **拒絕 warm start、退回全量重放並出聲** —— 而不是拿一個接不上的基準去加。
//      🩸 這一格防的是「有人手動刪了中間某一天的 closing」：少掉的那一天會被**靜默略過**，
//        於是餘額少算一整天，而每一層都會回成功。
//   ③ **寫入順序：先算完整份再落檔**（`File.Replace` 語意由呼叫端保證一次寫完）——
//      半份結帳比沒有結帳危險，因為它讀得動。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Bank
{
    /// <summary>一天的結帳快照（含該日全部 entry 之後的餘額）。</summary>
    public sealed class SCP_BankClosingSnapshot
    {
        public const int CurrentSchemaVersion = 1;

        /// <summary>這一份結的是哪個 UTC 日（`yyyy-MM-dd`）。</summary>
        public string Date = "";

        /// <summary>上一份結帳的日期；`""` ＝ 這是第一份（鏈的頭）。</summary>
        public string PrevDate = "";

        public string GeneratedAt = "";

        /// <summary>該日資料夾裡讀得動的 entry 筆數（鏈的體檢用，⛔ 不參與算餘額）。</summary>
        public int EntryCount;

        /// <summary>`貨幣 → (帳號 → 餘額)`。⚠ 分貨幣存，⛔ 不假設只有一種。</summary>
        public Dictionary<string, Dictionary<string, int>> Balances =
            new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

        public int SchemaVersion = CurrentSchemaVersion;

        public SCP_JsonData ToJson()
        {
            var d = SCP_JsonData.NewObject();
            d.Set("date", SCP_JsonData.NewString(Date));
            d.Set("prev_date", SCP_JsonData.NewString(PrevDate));
            d.Set("generated_at", SCP_JsonData.NewString(GeneratedAt));
            d.Set("entry_count", SCP_JsonData.NewNumber((long)EntryCount));
            var aCur = SCP_JsonData.NewObject();
            foreach (KeyValuePair<string, Dictionary<string, int>> kv in Balances)
            {
                var aAcc = SCP_JsonData.NewObject();
                foreach (KeyValuePair<string, int> a in kv.Value)
                    aAcc.Set(a.Key, SCP_JsonData.NewNumber((long)a.Value));
                aCur.Set(kv.Key, aAcc);
            }
            d.Set("balances", aCur);
            d.Set("schema_version", SCP_JsonData.NewNumber((long)SchemaVersion));
            return d;
        }

        public static SCP_BankClosingSnapshot FromJson(SCP_JsonData d)
        {
            var s = new SCP_BankClosingSnapshot
            {
                Date = d.GetString("date", ""),
                PrevDate = d.GetString("prev_date", ""),
                GeneratedAt = d.GetString("generated_at", ""),
                EntryCount = d.GetInt("entry_count", 0),
                SchemaVersion = d.GetInt("schema_version", 0),
            };
            SCP_JsonData aCur = d["balances"];
            if (aCur.Exists && !aCur.IsNull)
                foreach (string aCurrency in aCur.Keys)
                {
                    SCP_JsonData aAcc = aCur[aCurrency];
                    if (!aAcc.Exists || aAcc.IsNull) continue;
                    var aMap = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (string aId in aAcc.Keys) aMap[aId] = aAcc.GetInt(aId, 0);
                    s.Balances[aCurrency] = aMap;
                }
            return s;
        }
    }

    /// <summary>每日結帳的產生與讀取。⛔ 不動錢、不寫 ledger。</summary>
    public static class SCP_BankClosing
    {
        public const string ClosingDirName = "closing";
        public const string DateFormat = "yyyy-MM-dd";

        public static string ClosingDir(string iBankRoot) => Path.Combine(iBankRoot, ClosingDirName);

        public static string ClosingPath(string iBankRoot, string iDateKey)
            => Path.Combine(ClosingDir(iBankRoot), iDateKey + ".json");

        public static string DateKey(DateTime iUtc) => iUtc.ToString(DateFormat, CultureInfo.InvariantCulture);

        /// <summary>ledger 底下所有日期夾（`yyyy-MM-dd`），已排序。⛔ 形狀不對的夾子不算數。</summary>
        public static List<string> LedgerDayKeys(string iBankRoot)
        {
            var aOut = new List<string>();
            string aRoot = SCP_BankLedger.LedgerDir(iBankRoot);
            if (!Directory.Exists(aRoot)) return aOut;
            foreach (string aDir in Directory.GetDirectories(aRoot))
            {
                string aName = Path.GetFileName(aDir);
                if (IsDateKey(aName)) aOut.Add(aName);
            }
            aOut.Sort(StringComparer.Ordinal);
            return aOut;
        }

        /// <summary>已結帳的日期，已排序。</summary>
        public static List<string> ClosedDayKeys(string iBankRoot)
        {
            var aOut = new List<string>();
            string aDir = ClosingDir(iBankRoot);
            if (!Directory.Exists(aDir)) return aOut;
            foreach (string aFile in Directory.GetFiles(aDir, "*.json"))
            {
                string aName = Path.GetFileNameWithoutExtension(aFile);
                if (IsDateKey(aName)) aOut.Add(aName);
            }
            aOut.Sort(StringComparer.Ordinal);
            return aOut;
        }

        public static bool IsDateKey(string iName)
            => DateTime.TryParseExact(iName, DateFormat, CultureInfo.InvariantCulture,
                                      DateTimeStyles.None, out _);

        public static SCP_BankClosingSnapshot? Load(string iBankRoot, string iDateKey)
        {
            string aPath = ClosingPath(iBankRoot, iDateKey);
            if (!File.Exists(aPath)) return null;
            try { return SCP_BankClosingSnapshot.FromJson(SCP_JsonParser.Parse(File.ReadAllText(aPath))); }
            catch { return null; }
        }

        // ── 讀：暖啟動基準 ────────────────────────────────────────────────

        /// <summary>
        /// 找出「可以拿來當基準」的最後一份結帳 —— ⛔ **鏈驗不過就回 null**（退回全量重放）。
        /// </summary>
        /// <param name="oWhy">為什麼拿不到基準（給呼叫端印出來 —— 「沒有基準」不可以靜默）。</param>
        public static SCP_BankClosingSnapshot? FindWarmStart(string iBankRoot, out string oWhy)
        {
            oWhy = "";
            List<string> aClosed = ClosedDayKeys(iBankRoot);
            if (aClosed.Count == 0) { oWhy = "還沒有任何結帳檔"; return null; }

            string aLast = aClosed[aClosed.Count - 1];
            SCP_BankClosingSnapshot? aSnap = Load(iBankRoot, aLast);
            if (aSnap == null) { oWhy = $"最後一份結帳 {aLast}.json 讀不動"; return null; }

            // 🔴 鏈體檢：從最後一份往回走，每一份宣稱的 prev_date 都要真的在檔案清單裡。
            //   ⛔ 不做這一格的話，中間被刪掉的那一天會被「靜默略過」⇒ 餘額少算一整天。
            var aHave = new HashSet<string>(aClosed, StringComparer.Ordinal);
            string aCursor = aLast;
            int aGuard = 0;
            while (true)
            {
                if (++aGuard > 100000) { oWhy = "結帳鏈體檢超過上限（疑似循環）"; return null; }
                SCP_BankClosingSnapshot? aCur = Load(iBankRoot, aCursor);
                if (aCur == null) { oWhy = $"結帳鏈斷在 {aCursor}（讀不動）"; return null; }
                if (aCur.PrevDate.Length == 0) break;              // 走到鏈頭，完整
                if (!aHave.Contains(aCur.PrevDate))
                {
                    oWhy = $"結帳鏈斷在 {aCursor} → 它宣稱的上一份 {aCur.PrevDate} 不在磁碟上";
                    return null;
                }
                aCursor = aCur.PrevDate;
            }
            return aSnap;
        }

        /// <summary>
        /// 某個貨幣的全體餘額 —— **暖啟動**：最後一份結帳 + 那天之後的 entry。
        /// ⭐ 結帳跟得上的話，這裡只會掃到**今天那一個日期夾**。
        /// </summary>
        /// <param name="oScannedDays">這一次真的掃了幾個日期夾（讀數，⛔ 讓呼叫端看得到暖啟動有沒有生效）。</param>
        /// <param name="oWarmFrom">用了哪一份結帳當基準；`""` ＝ 這次是全量重放。</param>
        public static Dictionary<string, int> GetAllBalancesWarm(
            string iBankRoot, string iCurrency, out int oScannedDays, out string oWarmFrom,
            List<string>? oProblems = null)
        {
            SCP_BankClosingSnapshot? aBase = FindWarmStart(iBankRoot, out string aWhy);
            var aOut = new Dictionary<string, int>(StringComparer.Ordinal);
            string aFrom = "";
            if (aBase != null)
            {
                aFrom = aBase.Date;
                if (aBase.Balances.TryGetValue(iCurrency, out Dictionary<string, int>? aSeed) && aSeed != null)
                    foreach (KeyValuePair<string, int> kv in aSeed) aOut[kv.Key] = kv.Value;
            }
            else oProblems?.Add("暖啟動未生效（全量重放）：" + aWhy);

            oWarmFrom = aFrom;
            int aScanned = 0;
            foreach (string aDay in LedgerDayKeys(iBankRoot))
            {
                // 基準那一天**已經含在快照裡** ⇒ 只吃它之後的（Ordinal 比較對 `yyyy-MM-dd` 等價於時序比較）。
                if (aFrom.Length > 0 && string.CompareOrdinal(aDay, aFrom) <= 0) continue;
                aScanned++;
                foreach (SCP_BankEntry e in EnumerateDay(iBankRoot, aDay, oProblems))
                {
                    if (!string.Equals(e.Currency, iCurrency, StringComparison.Ordinal)) continue;
                    aOut.TryGetValue(e.AccountId, out int aCur);
                    aOut[e.AccountId] = aCur + e.Delta;
                }
            }
            oScannedDays = aScanned;
            return aOut;
        }

        /// <summary>一天的 entry。⚠ 讀不動的檔跳過但要被看見。</summary>
        public static IEnumerable<SCP_BankEntry> EnumerateDay(string iBankRoot, string iDateKey,
                                                              List<string>? oProblems = null)
        {
            string aDir = Path.Combine(SCP_BankLedger.LedgerDir(iBankRoot), iDateKey);
            if (!Directory.Exists(aDir)) yield break;
            string[] aFiles = Directory.GetFiles(aDir, "*.json");
            Array.Sort(aFiles, StringComparer.Ordinal);
            foreach (string aFile in aFiles)
            {
                SCP_BankEntry? e = null;
                try { e = SCP_BankEntry.FromJson(SCP_JsonParser.Parse(File.ReadAllText(aFile))); }
                catch (Exception ex) { oProblems?.Add($"{aFile}：{ex.GetType().Name}: {ex.Message}"); }
                if (e != null) yield return e;
            }
        }

        // ── 寫：補結帳 ────────────────────────────────────────────────────

        /// <summary>
        /// 把**所有完整的日子**補上結帳檔（今天不結 —— 它還在長）。回傳新寫了幾份。
        /// </summary>
        /// <param name="oSummary">人讀的一句話（含掃描範圍與寫了哪幾天）。</param>
        public static int GenerateMissing(string iBankRoot, out string oSummary,
                                          List<string>? oProblems = null)
            => GenerateMissing(iBankRoot, DateTime.UtcNow, out oSummary, oProblems);

        /// <summary>同上，而「今天」由呼叫端給 —— 測試要能指定日界，⛔ 不靠改系統時鐘。</summary>
        public static int GenerateMissing(string iBankRoot, DateTime iUtcNow, out string oSummary,
                                          List<string>? oProblems = null)
        {
            string aToday = DateKey(iUtcNow);
            List<string> aDays = LedgerDayKeys(iBankRoot);
            var aClosed = new HashSet<string>(ClosedDayKeys(iBankRoot), StringComparer.Ordinal);

            var aMissing = new List<string>();
            foreach (string aDay in aDays)
            {
                if (string.CompareOrdinal(aDay, aToday) >= 0) continue;   // ① 只結完整的日子
                if (!aClosed.Contains(aDay)) aMissing.Add(aDay);
            }
            if (aMissing.Count == 0)
            {
                oSummary = $"結帳已是最新（ledger {aDays.Count} 天／已結 {aClosed.Count} 天／今天 {aToday} 不結）";
                return 0;
            }

            // 基準：**第一個缺口之前**最後一份結帳。找不到就從頭全量累加（⛔ 不從缺口當天憑空起算）。
            string aFirstMissing = aMissing[0];
            string aBaseDate = "";
            var aRunning = new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);
            foreach (string aDay in ClosedDayKeys(iBankRoot))
            {
                if (string.CompareOrdinal(aDay, aFirstMissing) >= 0) break;
                aBaseDate = aDay;
            }
            if (aBaseDate.Length > 0)
            {
                SCP_BankClosingSnapshot? aBase = Load(iBankRoot, aBaseDate);
                if (aBase == null)
                {
                    oProblems?.Add($"基準結帳 {aBaseDate}.json 讀不動 ⇒ 改成從頭全量累加");
                    aBaseDate = "";
                }
                else
                    foreach (KeyValuePair<string, Dictionary<string, int>> kv in aBase.Balances)
                        aRunning[kv.Key] = new Dictionary<string, int>(kv.Value, StringComparer.Ordinal);
            }

            string aPrev = aBaseDate;
            int aWritten = 0;
            var aWroteDays = new List<string>();
            Directory.CreateDirectory(ClosingDir(iBankRoot));

            foreach (string aDay in aDays)
            {
                if (aBaseDate.Length > 0 && string.CompareOrdinal(aDay, aBaseDate) <= 0) continue;
                if (string.CompareOrdinal(aDay, aToday) >= 0) break;      // 今天以後不碰

                int aCount = 0;
                foreach (SCP_BankEntry e in EnumerateDay(iBankRoot, aDay, oProblems))
                {
                    aCount++;
                    if (!aRunning.TryGetValue(e.Currency, out Dictionary<string, int>? aMap) || aMap == null)
                    {
                        aMap = new Dictionary<string, int>(StringComparer.Ordinal);
                        aRunning[e.Currency] = aMap;
                    }
                    aMap.TryGetValue(e.AccountId, out int aCur);
                    aMap[e.AccountId] = aCur + e.Delta;
                }

                // 已經有結帳的日子照樣要**累加**（不然鏈會少一段），⛔ 但不覆寫它。
                if (aClosed.Contains(aDay)) { aPrev = aDay; continue; }

                var aSnap = new SCP_BankClosingSnapshot
                {
                    Date = aDay,
                    PrevDate = aPrev,
                    GeneratedAt = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    EntryCount = aCount,
                };
                foreach (KeyValuePair<string, Dictionary<string, int>> kv in aRunning)
                    aSnap.Balances[kv.Key] = new Dictionary<string, int>(kv.Value, StringComparer.Ordinal);

                File.WriteAllText(ClosingPath(iBankRoot, aDay), aSnap.ToJson().ToJson(true));
                aWritten++;
                aWroteDays.Add(aDay);
                aPrev = aDay;
            }

            oSummary = $"補了 {aWritten} 份結帳（{string.Join(", ", aWroteDays)}）"
                     + $"；ledger {aDays.Count} 天／今天 {aToday} 不結";
            return aWritten;
        }
    }
}
