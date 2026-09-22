// 區塊職責：**新舊兩條路的對拍**（TASK-0278 ② —— 那一格是閘，不是參考資訊）。
// 物理意義：舊實作（Unity 那側）留在磁碟上的產物就是**它的輸出**：某一天 `ledger/` 裡那批
//           `overnight_storage_fee` 扣繳與 `overnight_storage_fee_deposit` 入庫。
//           ⇒ 拿那一天**扣繳發生前**的餘額快照餵給新實作，逐帳戶比對金額。
// 數值影響：**零寫入**。它只讀帳本。
//
// ⭐ 為什麼不是「兩條 code path 同時留著跑一次」：那會要求舊實作活到對拍之後，
//   而搬家單的驗收若依賴「舊的還在」，就沒有人敢刪它（TASK-0278 ⑦ 要求整段刪除）。
//   ⇒ 這裡比的是**舊實作已經寫在帳本上的真實輸出**，所以舊 code 刪掉之後這個對拍**照樣跑得動**。
//
// 🩸 判準：**快照的時點要取「那一天第一筆保管費 entry 之前」**，⛔ 不是那一天的 00:00 ——
//   舊實作讀餘額的時刻就在它寫第一筆之前那一瞬間，而同一天稍早還有別的金流。
//   取錯時點的症狀是「差幾個帳戶對不上」，而它看起來會像**新實作算錯了**。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Bank
{
    public sealed class SCP_DemurrageParityRow
    {
        public string AccountId = "";
        /// <summary>舊實作那天真的扣了多少（帳本上的 entry）。</summary>
        public int OldFee;
        /// <summary>新實作用同一份快照算出來多少。</summary>
        public int NewFee;
        /// <summary>舊實作那天真的入庫多少（該繳費者對應的那一筆 credit）。</summary>
        public int OldDeposit;
        public int NewDeposit;
        public bool FeeMatch => OldFee == NewFee;
        public bool DepositMatch => OldDeposit == NewDeposit;
        public bool Match => FeeMatch && DepositMatch;
    }

    public sealed class SCP_DemurrageParityReport
    {
        public string Date = "";
        /// <summary>快照時點（那一天第一筆保管費 entry 的 `at_utc`）。空＝那天沒有保管費。</summary>
        public string SnapshotAtUtc = "";
        public int AccountsInSnapshot;
        public readonly List<SCP_DemurrageParityRow> Rows = new List<SCP_DemurrageParityRow>();
        public readonly List<string> Problems = new List<string>();
        public int Mismatches
        {
            get { int n = 0; foreach (SCP_DemurrageParityRow r in Rows) if (!r.Match) n++; return n; }
        }

        /// <summary>只算**費用**那一欄不符的筆數 —— 「誰付了多少」是這張單真正要保住的東西。</summary>
        public int FeeMismatches
        {
            get { int n = 0; foreach (SCP_DemurrageParityRow r in Rows) if (!r.FeeMatch) n++; return n; }
        }

        /// <summary>
        /// 只算**入庫歸屬**不符的筆數。
        /// <para>⚠ 它跟 <see cref="FeeMismatches"/> 要分開看：入庫的總額可以一樣而歸屬不同
        /// —— 舊實作把兩個繳費者併成一筆 credit（因為它用「回讀餘額」算入庫金額，
        /// 而前一筆的錢已經扣在同一個帳戶上了）。⇒ **錢守恆，ref 的歸屬不同。**</para>
        /// </summary>
        public int DepositMismatches
        {
            get { int n = 0; foreach (SCP_DemurrageParityRow r in Rows) if (!r.DepositMatch) n++; return n; }
        }

        public int OldFeeTotal { get { int s = 0; foreach (SCP_DemurrageParityRow r in Rows) s += r.OldFee; return s; } }
        public int NewFeeTotal { get { int s = 0; foreach (SCP_DemurrageParityRow r in Rows) s += r.NewFee; return s; } }
        public int OldDepositTotal { get { int s = 0; foreach (SCP_DemurrageParityRow r in Rows) s += r.OldDeposit; return s; } }
        public int NewDepositTotal { get { int s = 0; foreach (SCP_DemurrageParityRow r in Rows) s += r.NewDeposit; return s; } }
    }

    public static class SCP_DemurrageParity
    {
        /// <summary>那一天有沒有保管費 entry；有的話回最早那一筆的 `at_utc`（＝快照時點）。</summary>
        public static string FindSnapshotMoment(string iBankRoot, string iDate)
        {
            string aEarliest = "";
            foreach (SCP_BankEntry e in SCP_BankLedger.EnumerateEntries(iBankRoot))
            {
                if (e.Kind != SCP_Demurrage.FeeKind && e.Kind != SCP_Demurrage.DepositKind) continue;
                if (!e.AtUtc.StartsWith(iDate, StringComparison.Ordinal)) continue;
                if (aEarliest.Length == 0 || string.CompareOrdinal(e.AtUtc, aEarliest) < 0) aEarliest = e.AtUtc;
            }
            return aEarliest;
        }

        /// <summary>
        /// 重放出某個時點**之前**的餘額。
        /// <para>⚠ 它掃全帳本重放，⛔ 不吃每日結帳的暖啟動 —— 結帳檔只涵蓋「已經結束的日子」，
        /// 而我們要的時點在某一天的**中間**。</para>
        /// </summary>
        public static Dictionary<string, int> ReplayBalancesBefore(string iBankRoot, string iAtUtcExclusive,
                                                                   List<string>? oProblems = null)
        {
            var aOut = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SCP_BankEntry e in SCP_BankLedger.EnumerateEntries(iBankRoot, oProblems))
            {
                if (!string.Equals(e.Currency, SCP_BankEntry.DefaultCurrency, StringComparison.Ordinal)) continue;
                if (string.CompareOrdinal(e.AtUtc, iAtUtcExclusive) >= 0) continue;
                aOut.TryGetValue(e.AccountId, out int aSum);
                aOut[e.AccountId] = e.Type == SCP_BankEntryType.Credit ? aSum + e.Amount : aSum - e.Amount;
            }
            return aOut;
        }

        public static SCP_DemurrageParityReport Run(string iDataRoot, string iBankRoot, string iDate)
        {
            var aReport = new SCP_DemurrageParityReport { Date = iDate };
            aReport.SnapshotAtUtc = FindSnapshotMoment(iBankRoot, iDate);
            if (aReport.SnapshotAtUtc.Length == 0)
            {
                // ⛔ 「那天沒有保管費」與「對拍通過」是兩件事 —— 不要用同一個 0 mismatch 帶過。
                aReport.Problems.Add($"{iDate} 帳本上**一筆保管費都沒有** ⇒ 沒有東西可以對拍"
                                     + "（⛔ 這不是「對拍通過」）");
                return aReport;
            }

            Dictionary<string, int> aSnapshot = ReplayBalancesBefore(iBankRoot, aReport.SnapshotAtUtc, aReport.Problems);
            aReport.AccountsInSnapshot = aSnapshot.Count;

            // 舊實作那天的真實輸出
            var aOldFee = new Dictionary<string, int>(StringComparer.Ordinal);
            var aOldDep = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SCP_BankEntry e in SCP_BankLedger.EnumerateEntries(iBankRoot))
            {
                if (!e.AtUtc.StartsWith(iDate, StringComparison.Ordinal)) continue;
                if (e.Kind == SCP_Demurrage.FeeKind)
                {
                    aOldFee.TryGetValue(e.AccountId, out int v);
                    aOldFee[e.AccountId] = v + e.Amount;
                }
                else if (e.Kind == SCP_Demurrage.DepositKind)
                {
                    // ⚠ 入庫那筆掛在**央行**帳上 ⇒ 繳費者只能從 ref 的尾段拿。
                    string aPrefix = SCP_Demurrage.DepositRef(iDate, "");
                    if (!e.Ref.StartsWith(aPrefix, StringComparison.Ordinal)) continue;
                    string aPayer = e.Ref.Substring(aPrefix.Length);
                    // ⚠ ref 尾段是**呼叫端當時給的字串**（`Sirius` / `sirius` 都出現過），
                    //   而比對的另一邊是正規化過的帳號 ⇒ 這裡也要正規化。
                    //   🩸 少了它：09-18 那一天九個帳戶全報「入庫不符」，而錢一毛不差。
                    SCP_BankIdResult aPn = SCP_BankId.Normalize(aPayer);
                    string aPk = aPn.Ok ? aPn.Id : aPayer;
                    aOldDep.TryGetValue(aPk, out int v);
                    aOldDep[aPk] = v + e.Amount;
                }
            }

            // 新實作：同一份快照，dry-run（零寫入）
            SCP_DemurrageOutcome aNew = SCP_Demurrage.Apply(iDataRoot, iBankRoot, iDate, iDryRun: true, aSnapshot);
            // ⚠ **按歸一後的帳戶聚合** —— 帳本上的 `account_id` 是歸一後的那個。
            //   🩸 第一次跑這支時我用了未歸一的 id ⇒ sirius／spectre 兩格報「不符」，
            //     而錢其實一毛不差（sirius 的費用本來就扣在 spectre 身上）。
            //     ⇒ 對拍的受詞挑錯，讀數會指控一個沒有犯錯的實作。
            var aNewFee = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SCP_DemurrageCharge c in aNew.Charges)
            {
                // ⚠ 再走一次 `Normalize` —— 解析器回的是**正式寫法**（`Spectre`），
                //   而帳本上存的是寫入端正規化過的小寫（`spectre`）。
                //   🩸 少了這一步：八個帳戶全部報「費用不符」，而合計一模一樣（425 vs 425）——
                //     一個只差大小寫的 key，讓一次完全正確的搬家看起來像全錯。
                string aRaw = c.ChargeAccountId.Length > 0 ? c.ChargeAccountId : c.AccountId;
                SCP_BankIdResult aNorm = SCP_BankId.Normalize(aRaw);
                string aKey = aNorm.Ok ? aNorm.Id : aRaw;
                aNewFee.TryGetValue(aKey, out int v);
                aNewFee[aKey] = v + c.PlannedFee;
            }

            var aIds = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string k in aOldFee.Keys) aIds.Add(k);
            foreach (string k in aNewFee.Keys) aIds.Add(k);
            foreach (string k in aOldDep.Keys) aIds.Add(k);
            foreach (string aId in aIds)
            {
                aOldFee.TryGetValue(aId, out int aOf);
                aNewFee.TryGetValue(aId, out int aNf);
                aOldDep.TryGetValue(aId, out int aOd);
                // 新實作的入庫金額 ＝ 該帳戶的費用（判準③：一腳扣、一腳存，金額相同）
                aReport.Rows.Add(new SCP_DemurrageParityRow
                {
                    AccountId = aId, OldFee = aOf, NewFee = aNf, OldDeposit = aOd, NewDeposit = aNf,
                });
            }
            return aReport;
        }
    }
}
