// 區塊職責：**保管費轉券** —— 讀某一天已經落帳的保管費扣繳，換算成券，發給該帳戶底下的 persona。
// 物理意義：它是**扣款的下游一層，而不是扣款的一部分**（Tim 2026-09-22 拍板：發券與扣款分層）。
//           ⇒ 輸入是**帳本上已經發生的事實**（`kind=overnight_storage_fee` 的 debit），
//             不是「扣款當下順手算的那個數字」。
// 數值影響：**會鑄券**（券是憑空生出來的，⛔ 不從任何人身上扣）。
//           ⚠ 而券發出去在別人手上，**收不回來** ⇒ 本層每一個出口都要能回答「這一筆發過了沒」。
//
// ⭐ 為什麼分層是對的（而不只是好聽）：
//   ① 扣款壞了不會連帶讓券發錯 —— 兩件事各自有各自的讀數。
//   ② **可以拿上一次真的扣繳重跑一次發券**（`Plan` 是純讀）⇒ 測試不必先偽造一次扣款。
//   ③ 扣款端哪天搬家（Unity → Senate），本層一行都不用改 —— 它只認帳本。
//
// 🩸 冪等這格要自己扛：券系統**刻意不記歷史**（TASK-0243，`senate cmd help voucher` 自己印著
//   「券不記歷史，所以那個前提是它成立的必要條件」）⇒ 同一天跑兩次就是**發兩次**，
//   而兩次都會成功、都不會叫。⇒ 所以本層自己留一本「哪幾筆扣繳已經轉過券」的簿子。
//   ⛔ 那不是快取（快取丟了只是變慢）；它丟了會**重複鑄幣**。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Voucher;

namespace SCP.Core.Bank
{
    /// <summary>一個帳戶在某一天的轉券計畫（**純算，不寫**）。</summary>
    public sealed class SCP_DemurragePlanRow
    {
        public string FeeEntryId = "";     // 來源扣繳 entry 的 id ＝ 冪等鍵
        public string AccountId = "";      // 帳本上的原樣（⚠ 帳本寫小寫）
        /// <summary>歸一後的正式帳號 —— **查綁定要用這個**（帳本小寫／綁定檔原拼法，直接比會查到空）。</summary>
        public string CanonicalAccountId = "";
        public int Fee;                    // 那天被扣了幾個 Token
        public int TotalVouchers;          // fee × ratio
        public List<string> Personas = new List<string>();
        public int PerPersona;             // 每人拿到的**可用整數**張數
        /// <summary>每人實際分到的量，單位 1e-8 —— 除不盡的部分進零頭池（TASK-0271）。</summary>
        public long PerPersonaE8;
        /// <summary>連 1e-8 都除不盡的殘量（單位 1e-8）。⛔ 不發給任何人。</summary>
        public long UnsplittableE8;
        public bool AlreadyIssued;         // 這一筆之前已經轉過券
        /// <summary>沒有人綁在這個帳戶底下 ⇒ **一張都不發**（⛔ 不倒給央行、不憑空找一個人）。</summary>
        public bool NoPersona => Personas.Count == 0;
    }

    public sealed class SCP_DemurragePlan
    {
        public string Date = "";
        public string VoucherType = "";
        public int RatioPerToken;
        public bool PolicyEnabled;
        public List<SCP_DemurragePlanRow> Rows = new List<SCP_DemurragePlanRow>();
        public List<string> Problems = new List<string>();
    }

    public static class SCP_DemurrageVoucher
    {
        /// <summary>帳本上「跨日保管費扣繳」那一類 entry 的 kind。⚠ 與扣款端寫入的字面必須一致。</summary>
        public const string FeeKind = "overnight_storage_fee";

        const string IssuedDirName = "voucher_issued";

        public static string BankRoot(string iDataRoot) => Path.Combine(iDataRoot, "Bank");
        static string IssuedPath(string iDataRoot, string iDate)
            => Path.Combine(BankRoot(iDataRoot), IssuedDirName, iDate + ".json");

        // ==========================================================
        // 區塊職責：找「上一次有扣繳的那一天」。
        // 物理意義：給人用的預設值 —— 要測發券的人手上通常沒有日期，只有「上次那批」。
        // ⛔ 找不到回空字串，**不退回今天** —— 「今天沒有扣繳」與「今天就是最後一次」
        //   在回傳值上必須分得出來，不然預覽會對著一個空日子印「0 筆」然後看起來很正常。
        // ==========================================================
        public static string LatestFeeDate(string iDataRoot)
        {
            string aDir = SCP_BankLedger.LedgerDir(BankRoot(iDataRoot));
            if (!Directory.Exists(aDir)) return "";
            var aDates = new List<string>();
            foreach (string d in Directory.GetDirectories(aDir)) aDates.Add(Path.GetFileName(d));
            aDates.Sort(StringComparer.Ordinal);
            for (int i = aDates.Count - 1; i >= 0; i--)
                if (ReadFeeEntries(iDataRoot, aDates[i], null).Count > 0) return aDates[i];
            return "";
        }

        /// <summary>讀某一天的保管費扣繳 entry（純讀）。</summary>
        public static List<SCP_BankEntry> ReadFeeEntries(string iDataRoot, string iDate, List<string>? oProblems)
        {
            var aOut = new List<SCP_BankEntry>();
            string aDayDir = Path.Combine(SCP_BankLedger.LedgerDir(BankRoot(iDataRoot)), iDate);
            if (!Directory.Exists(aDayDir)) return aOut;
            foreach (string f in Directory.GetFiles(aDayDir, "*.json"))
            {
                SCP_JsonData aJd;
                try { aJd = SCP_JsonParser.Parse(File.ReadAllText(f)); }
                catch (Exception e) { oProblems?.Add("讀不了 " + Path.GetFileName(f) + "：" + e.Message); continue; }
                if (!aJd.IsObject) continue;
                if (!string.Equals(aJd.GetString("kind", ""), FeeKind, StringComparison.Ordinal)) continue;
                if (!string.Equals(aJd.GetString("type", ""), "debit", StringComparison.Ordinal)) continue;
                aOut.Add(new SCP_BankEntry
                {
                    Id = aJd.GetString("id", ""),
                    Type = SCP_BankEntryType.Debit,
                    AccountId = aJd.GetString("account_id", ""),
                    Amount = aJd.GetInt("amount", 0),
                    Kind = FeeKind,
                    Ref = aJd.GetString("ref", ""),
                    AtUtc = aJd.GetString("at_utc", ""),
                });
            }
            aOut.Sort((a, b) => string.CompareOrdinal(a.AccountId, b.AccountId));
            return aOut;
        }

        /// <summary>某一天已經轉過券的扣繳 entry id。</summary>
        public static HashSet<string> LoadIssued(string iDataRoot, string iDate)
        {
            var aOut = new HashSet<string>(StringComparer.Ordinal);
            string aPath = IssuedPath(iDataRoot, iDate);
            if (!File.Exists(aPath)) return aOut;
            try
            {
                SCP_JsonData aJd = SCP_JsonParser.Parse(File.ReadAllText(aPath));
                if (aJd.Contains("entries"))
                    foreach (string k in aJd["entries"].Keys) aOut.Add(k);
            }
            // ⛔ 讀不了**不當成空的** —— 空的意思是「一筆都沒發過」，而那會讓整天重發一次。
            //   ⇒ 這裡丟例外，讓呼叫端停手。
            catch (Exception e) { throw new InvalidOperationException("轉券簿讀不了（⛔ 不當成沒發過）：" + e.Message); }
            return aOut;
        }

        // ==========================================================
        // 區塊職責：算出某一天要發什麼（**純讀，零副作用**）。
        // ⇒ 這支就是「手動撈上一次扣繳跑發券測試」的那個入口：它不需要先偽造一次扣款。
        // ==========================================================
        public static SCP_DemurragePlan Plan(string iDataRoot, string iLettersRoot, string iRegion, string iDate)
        {
            var aPlan = new SCP_DemurragePlan { Date = iDate };
            SCP_BankPolicy.Reading aPol = SCP_BankPolicy.Read(iDataRoot, out string? aWhy);
            if (aWhy != null) aPlan.Problems.Add(aWhy);
            aPlan.VoucherType = aPol.VoucherType;
            aPlan.RatioPerToken = aPol.VoucherRatio;
            aPlan.PolicyEnabled = aPol.VoucherEnabled;

            List<SCP_BankEntry> aFees = ReadFeeEntries(iDataRoot, iDate, aPlan.Problems);
            HashSet<string> aIssued = LoadIssued(iDataRoot, iDate);

            foreach (SCP_BankEntry e in aFees)
            {
                var aRow = new SCP_DemurragePlanRow
                {
                    FeeEntryId = e.Id,
                    AccountId = e.AccountId,
                    Fee = e.Amount,
                    AlreadyIssued = aIssued.Contains(e.Id),
                };
                aRow.TotalVouchers = aPol.VoucherEnabled ? e.Amount * aPol.VoucherRatio : 0;

                // 🩸 **先把帳本上的 account_id 歸一，再去查綁定** ——
                //   帳本寫的是小寫（`myth` / `altair` / `frs`），而綁定檔存的是原拼法（`Myth` / `Altair` / `FRS`），
                //   而綁定名單是 Ordinal 比對 ⇒ 直接拿帳本那個字串去查會**查到 0 個人**。
                //   ⚠ 那個失效樣子最毒：它不是錯誤，是一個**看起來很合理的空名單** ——
                //     報表會說「這個帳戶底下沒有人」，而實際上那底下有五個。
                //   ⭐ 2026-09-22 第一次 preview 就是這樣：5 個帳戶有 3 個報「沒有 persona」。
                //     ⇒ 抓到它的是 preview（零寫入），⛔ 不是我更仔細。
                SCP_BankResolution aRes = SCP_BankAccountResolver.Resolve(iLettersRoot, iDataRoot, iRegion, e.AccountId);
                string aCanonical = aRes.IsUnresolved ? e.AccountId : aRes.AccountId;
                aRow.CanonicalAccountId = aCanonical;

                // ⚠ 綁定名單**由正向綁定檔導出**（`SCP_BankAccountResolver`）——
                //   ⛔ 不讀 registry 的 `bank_personas`：那張表 2026-09-07 就退出解析，
                //     而它今天還有兩筆是舊值 ⇒ 照它算，有人會拿到「0 個 persona」而沒有任何一層會叫。
                aRow.Personas = SCP_BankAccountResolver.GetBoundPersonas(iLettersRoot, iDataRoot, iRegion, aCanonical);
                if (aRow.Personas.Count > 0 && aRow.TotalVouchers > 0)
                {
                    // 🩸 這裡**不 floor 丟掉零頭**（那是 2026-09-22 的佔位做法，Tim 當天就否掉了）：
                    //   在只有整數的世界裡，除不盡的那幾張要嘛丟掉、要嘛塞給前面幾個人 ——
                    //   後者是**發放順序決定誰多拿**，而那種不公平不會叫。
                    //   ⇒ 改成連零頭一起發：每人拿 total/N（1e-8 精度），不足一張的留在他自己的零頭池，
                    //     加總滿 1 才變成可用券（TASK-0271）。
                    int aN = aRow.Personas.Count;
                    long aTotalE8 = (long)aRow.TotalVouchers * SCP_VoucherBook.FractionScale;
                    aRow.PerPersonaE8 = aTotalE8 / aN;
                    aRow.UnsplittableE8 = aTotalE8 % aN;   // 連 1e-8 都除不盡的那一點點
                    aRow.PerPersona = (int)(aRow.PerPersonaE8 / SCP_VoucherBook.FractionScale);
                }
                aPlan.Rows.Add(aRow);
            }
            return aPlan;
        }

        // ==========================================================
        // 區塊職責：照計畫發券，並把「這筆發過了」寫進轉券簿。
        // ⚠ 每一筆**先發券再記簿**：反過來的話，記完簿而發券失敗 ＝ 那個人的券永遠不會來，
        //   而簿子說已經給過了。⇒ 兩種失敗裡選**會重試**的那一種，⛔ 不選會靜默漏掉的那一種。
        //   （代價：發券成功而記簿失敗 ⇒ 下次會重發。所以記簿失敗要**大聲**，見回傳的 oProblems。）
        // ==========================================================
        public static int Issue(string iDataRoot, string iLettersRoot, string iRegion,
                                SCP_DemurragePlan iPlan, List<string> oLog, List<string> oProblems)
        {
            if (!iPlan.PolicyEnabled) { oLog.Add("· 政策沒開（券種空白或比例 0）⇒ **一張都沒發**"); return 0; }

            var aRoot = new SCP_LettersRoot(iLettersRoot.Replace('\\', '/').TrimEnd('/'));
            DateTime aNow = DateTime.UtcNow;
            var aDone = new List<string>();
            int aIssuedRows = 0;

            foreach (SCP_DemurragePlanRow r in iPlan.Rows)
            {
                if (r.AlreadyIssued) { oLog.Add($"· `{r.AccountId}`：**跳過**（這筆扣繳已經轉過券）"); continue; }
                if (r.TotalVouchers <= 0) { oLog.Add($"· `{r.AccountId}`：換算 0 張 ⇒ 不發"); continue; }
                if (r.NoPersona)
                {
                    // ⛔ 沒有人綁在這個帳戶底下 ⇒ **不發、也不記成已發**。
                    //   記成已發的話，之後補了綁定也永遠補不回來；而「沒有人」通常是綁定漏登記，不是真的沒人。
                    oProblems.Add($"⚠ `{r.AccountId}` 底下沒有任何 persona ⇒ {r.TotalVouchers} 張沒有發"
                                  + "（⛔ 也沒有記成已發 —— 補好綁定再跑一次就會補上）");
                    continue;
                }

                bool aRowOk = true;
                foreach (string p in r.Personas)
                {
                    SCP_VoucherBook aBook = SCP_VoucherStore.Load(aRoot, p, iPlan.VoucherType, out string? aProblem);
                    if (aProblem != null)
                    { oProblems.Add($"✗ `{p}` 的 `{iPlan.VoucherType}` 券讀不了 ⇒ 沒發：{aProblem}"); aRowOk = false; continue; }
                    // ⚠ 走 `AddE8` 而不是直接加 `Permanent` —— 進位與零頭的規則只有一份（TASK-0271），
                    //   在這裡自己算一次就是第二份，而兩份會漂。
                    aBook.AddE8(r.PerPersonaE8);
                    if (!SCP_VoucherStore.Save(aRoot, aBook, aNow, iRegion, out int _, out string? aErr))
                    { oProblems.Add($"✗ `{p}` 發券寫入失敗 ⇒ {aErr}"); aRowOk = false; }
                }
                if (!aRowOk)
                {
                    oProblems.Add($"⚠ `{r.AccountId}` 這一筆**沒有記成已發** —— 下次會重試，"
                                  + "⚠ 而已經成功那幾位會**再拿一次**。⛔ 補跑前先看上面是誰失敗的。");
                    continue;
                }

                aDone.Add(r.FeeEntryId);
                aIssuedRows++;
                decimal aPer = (decimal)r.PerPersonaE8 / SCP_VoucherBook.FractionScale;
                oLog.Add($"🏦 `{r.AccountId}` 扣繳 {r.Fee} Token → 提撥 **{r.TotalVouchers}** 張 `{iPlan.VoucherType}` 券，"
                         + $"均分給 {string.Join(", ", r.Personas)} 各 **{aPer:0.########}** 張"
                         + (aPer != decimal.Truncate(aPer)
                            ? $"（可用 {r.PerPersona} 張 ＋ 零頭，滿 1 張時自動進位）" : "")
                         + (r.UnsplittableE8 > 0 ? $"　⚠ 另有 {r.UnsplittableE8}/1e8 連最小單位都除不盡，**未發**" : ""));
            }

            if (aDone.Count > 0 && !AppendIssued(iDataRoot, iPlan.Date, aDone, out string? aSaveErr))
                oProblems.Add("🔴 **券已經發出去了，而轉券簿沒寫成功** ⇒ 下次會重發："
                              + aSaveErr + "　⛔ 補跑前先把這本簿子修好。");
            return aIssuedRows;
        }

        static bool AppendIssued(string iDataRoot, string iDate, List<string> iEntryIds, out string? oError)
        {
            oError = null;
            string aPath = IssuedPath(iDataRoot, iDate);
            try
            {
                SCP_JsonData aJd = File.Exists(aPath)
                    ? SCP_JsonParser.Parse(File.ReadAllText(aPath))
                    : SCP_JsonData.Parse("{}");
                if (!aJd.Contains("entries")) aJd["entries"] = SCP_JsonData.NewObject();
                string aStamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
                foreach (string id in iEntryIds) aJd["entries"][id] = aStamp;

                string? aDir = Path.GetDirectoryName(aPath);
                if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
                string aTmp = aPath + ".tmp";
                // ⚠ 不寫 BOM —— 這個檔之後可能被 python 讀，而 `json.load` 撞 BOM 是直接拋例外。
                File.WriteAllText(aTmp, aJd.ToJson(true) + "\n", new UTF8Encoding(false));
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);

                // 回讀複驗 —— 寫入成功不等於讀得回來。
                HashSet<string> aBack = LoadIssued(iDataRoot, iDate);
                foreach (string id in iEntryIds)
                    if (!aBack.Contains(id)) { oError = "寫入後回讀不到 " + id; return false; }
                return true;
            }
            catch (Exception e) { oError = e.Message; return false; }
        }
    }
}
