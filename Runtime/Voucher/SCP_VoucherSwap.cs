// 區塊職責：**券互換交易執行器**（TASK-0272）。
// 物理意義：讀取匯率快取（rates_cache.json）進行 USD 中介兩段撮合，
//           自 Persona 扣除來源券（TryConsume），並將折算後之目標券（含零頭小數 AddE8）發至目標券本。
// 數值影響：來源券減少（整數），目標券增加（整數 ＋ 1e-8 零頭池）。雙方原子寫回磁碟。
// 🩸 守衛：
//   ① **無快取或無有效報價時一律拒絕**（並非所有券都能互相兌換）。
//   ② **餘額不足整筆不扣**（TryConsume 守衛，嚴禁扣出負數）。
//   ③ **小數點無縫進位**（套用 TASK-0271 零頭進位模型，滿 1e8 自動進位可用永久券）。
#nullable enable
using System;
using SCP.Core.Market;
using SCP.Core.Paths;

namespace SCP.Core.Voucher
{
    public sealed class SCP_VoucherSwapResult
    {
        public bool Success;
        public string? Error;
        public string Persona = "";
        public string FromVoucher = "";
        public string ToVoucher = "";
        public int FromConsumed;
        public int FromRemainingPermanent;
        public int FromRemainingSpendable;
        public long ToAddedUnitsE8;
        public int ToPermanentAdded;
        public int ToNewPermanent;
        public long ToNewFractionalE8;
        public decimal ToNewFractionalValue => (decimal)ToNewFractionalE8 / SCP_VoucherBook.FractionScale;
        public decimal EffectiveRate;
        public bool IsPreview;
    }

    public static class SCP_VoucherSwap
    {
        /// <summary>
        /// 試算兌換（零寫入）。驗證匯率快取、餘額充足性與折算目標量。
        /// </summary>
        public static SCP_VoucherSwapResult PreviewSwap(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                                       string iPersona, string iFromVoucher, string iToVoucher,
                                                       int iAmount, DateTime iNow)
        {
            return ExecuteInternal(iLettersRoot, iDataRoot, iPersona, iFromVoucher, iToVoucher, iAmount, iNow,
                                   iRegion: "swap", iCommitWrite: false);
        }

        /// <summary>
        /// 實際執行兌換。原子寫回來源券與目標券本。
        /// </summary>
        public static SCP_VoucherSwapResult ExecuteSwap(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                                       string iPersona, string iFromVoucher, string iToVoucher,
                                                       int iAmount, DateTime iNow, string iRegion = "swap")
        {
            return ExecuteInternal(iLettersRoot, iDataRoot, iPersona, iFromVoucher, iToVoucher, iAmount, iNow,
                                   iRegion, iCommitWrite: true);
        }

        static SCP_VoucherSwapResult ExecuteInternal(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                                     string iPersona, string iFromVoucher, string iToVoucher,
                                                     int iAmount, DateTime iNow, string iRegion, bool iCommitWrite)
        {
            var aResult = new SCP_VoucherSwapResult
            {
                Persona = iPersona,
                FromVoucher = iFromVoucher.Trim().ToLowerInvariant(),
                ToVoucher = iToVoucher.Trim().ToLowerInvariant(),
                FromConsumed = iAmount,
                IsPreview = !iCommitWrite
            };

            // 1. 載入匯率快取設定
            var aConfig = SCP_MarketRateCache.Load(iDataRoot, out string? aCfgErr);
            if (aCfgErr != null)
            {
                aResult.Success = false;
                aResult.Error = aCfgErr;
                return aResult;
            }

            // 2. 算式與匯率撮合守衛（無報價直接被擋）
            if (!SCP_MarketRateCache.TryCalculateSwap(aConfig, aResult.FromVoucher, aResult.ToVoucher,
                                                     iAmount, out long aToUnitsE8, out decimal aRate,
                                                     out string? aCalcReason))
            {
                aResult.Success = false;
                aResult.Error = aCalcReason;
                return aResult;
            }

            aResult.EffectiveRate = aRate;
            aResult.ToAddedUnitsE8 = aToUnitsE8;

            // 3. 讀取來源券本並檢查餘額
            var aFromBook = SCP_VoucherStore.Load(iLettersRoot, iPersona, aResult.FromVoucher, out string? aFromErr);
            if (aFromErr != null)
            {
                aResult.Success = false;
                aResult.Error = aFromErr;
                return aResult;
            }

            int aSpendableBefore = aFromBook.Spendable(iNow);
            if (aSpendableBefore < iAmount)
            {
                aResult.Success = false;
                aResult.Error = $"來源券 '{aResult.FromVoucher}' 餘額不足：可花 {aSpendableBefore} 張，欲兌換 {iAmount} 張";
                return aResult;
            }

            // 4. 讀取目標券本
            var aToBook = SCP_VoucherStore.Load(iLettersRoot, iPersona, aResult.ToVoucher, out string? aToErr);
            if (aToErr != null)
            {
                aResult.Success = false;
                aResult.Error = aToErr;
                return aResult;
            }

            int aToPermanentBefore = aToBook.Permanent;

            // 5. 試算或執行扣款與進位
            if (!SCP_VoucherStore.TryConsume(aFromBook, iAmount, iNow, out string? aConsumeWhy))
            {
                aResult.Success = false;
                aResult.Error = aConsumeWhy ?? "來源券扣減失敗";
                return aResult;
            }

            aToBook.AddE8(aToUnitsE8);

            aResult.FromRemainingPermanent = aFromBook.Permanent;
            aResult.FromRemainingSpendable = aFromBook.Spendable(iNow);
            aResult.ToPermanentAdded = aToBook.Permanent - aToPermanentBefore;
            aResult.ToNewPermanent = aToBook.Permanent;
            aResult.ToNewFractionalE8 = aToBook.FractionalE8;

            if (!iCommitWrite)
            {
                // 預覽模式：純算完畢，零寫入
                aResult.Success = true;
                return aResult;
            }

            // 6. 原子寫回磁碟（先寫來源、再寫目標）
            if (!SCP_VoucherStore.Save(iLettersRoot, aFromBook, iNow, iRegion, out _, out string? aSaveFromErr))
            {
                aResult.Success = false;
                aResult.Error = $"來源券落盤失敗：{aSaveFromErr}";
                return aResult;
            }

            if (!SCP_VoucherStore.Save(iLettersRoot, aToBook, iNow, iRegion, out _, out string? aSaveToErr))
            {
                aResult.Success = false;
                aResult.Error = $"目標券落盤失敗：{aSaveToErr}";
                return aResult;
            }

            aResult.Success = true;
            return aResult;
        }
    }
}
