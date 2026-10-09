// 區塊職責：**券互換交易執行器**（TASK-0272）。
// 物理意義：讀取匯率快取（rates_cache.json）進行 USD 中介兩段撮合，
//           自 Persona 扣除來源券（TryConsume），並將折算後之目標券（含零頭小數 AddE8）發至目標券本。
// 數值影響：來源券減少（整數），目標券增加（整數 ＋ 1e-8 零頭池）。雙方原子寫回磁碟。
// 🩸 守衛：
//   ① **無快取或無有效報價時一律拒絕**（並非所有券都能互相兌換）。
//   ② **餘額不足整筆不扣**（TryConsume 守衛，嚴禁扣出負數）。
//   ③ **小數點無縫進位**（套用 TASK-0271 零頭進位模型，滿 1e8 自動進位可用永久券）。
//   ④ **目標券簿永久券不得超過 long 上限**（TASK-0371；TASK-0476 由 int 放寬）：法幣一張＝一單位，單位極小
//      （1 KRW ≈ 0.0007 USD），173 BTC 換 KRW 就是兩百多億張 —— int 時代 `AddE8` 的轉型會**靜默繞成負數**（現在是 long）。
//      ⚠ 單次兌換的產出走 1e-8 單位（long）⇒ 一次最多約 922 億張（9.2e18／1e8）；超過時 Convert 的 decimal→long 會丟例外、不會繞回。
//      ⇒ 試算階段就擋下並說明，⛔ 不讓它走到落盤。
//   ⑤ **成交後寫一筆交易事件**（TASK-0371，給報酬率用）：兩個券檔都落盤之後才寫；
//      事件沒寫成**不推翻已成立的兌換**（券已經動了），而是放進 `PortfolioWarning` 讓呼叫端印出來。
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
        public long FromConsumed;
        public long FromRemainingPermanent;
        public long FromRemainingSpendable;
        public long ToAddedUnitsE8;
        public long ToPermanentAdded;
        public long ToNewPermanent;
        public long ToNewFractionalE8;
        public decimal ToNewFractionalValue => (decimal)ToNewFractionalE8 / SCP_VoucherBook.FractionScale;
        public decimal EffectiveRate;
        public bool IsPreview;

        /// <summary>兌換成立、但交易事件沒寫成時的原因（null ＝ 已記）。⚠ 呼叫端必須印出來 —— 漏記會讓報酬率少一筆。</summary>
        public string? PortfolioWarning;
    }

    public static class SCP_VoucherSwap
    {
        /// <summary>
        /// 試算兌換（零寫入）。驗證匯率快取、餘額充足性與折算目標量。
        /// </summary>
        public static SCP_VoucherSwapResult PreviewSwap(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                                       string iPersona, string iFromVoucher, string iToVoucher,
                                                       long iAmount, DateTime iNow)
        {
            return ExecuteInternal(iLettersRoot, iDataRoot, iPersona, iFromVoucher, iToVoucher, iAmount, iNow,
                                   iRegion: "swap", iCommitWrite: false);
        }

        /// <summary>
        /// 實際執行兌換。原子寫回來源券與目標券本。
        /// </summary>
        public static SCP_VoucherSwapResult ExecuteSwap(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                                       string iPersona, string iFromVoucher, string iToVoucher,
                                                       long iAmount, DateTime iNow, string iRegion = "swap")
        {
            return ExecuteInternal(iLettersRoot, iDataRoot, iPersona, iFromVoucher, iToVoucher, iAmount, iNow,
                                   iRegion, iCommitWrite: true);
        }

        static SCP_VoucherSwapResult ExecuteInternal(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                                     string iPersona, string iFromVoucher, string iToVoucher,
                                                     long iAmount, DateTime iNow, string iRegion, bool iCommitWrite)
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

            long aSpendableBefore = aFromBook.Spendable(iNow);
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

            long aToPermanentBefore = aToBook.Permanent;

            // 守衛④：進位後的永久券會不會超過 long 上限（用 decimal 比，比較本身不溢位）
            long aToTotalE8 = aToBook.FractionalE8 + aToUnitsE8;
            if ((decimal)aToPermanentBefore + aToTotalE8 / SCP_VoucherBook.FractionScale > long.MaxValue)
            {
                aResult.Success = false;
                aResult.Error = $"兌換後 `{aResult.ToVoucher}` 會有 {(decimal)aToPermanentBefore + aToTotalE8 / SCP_VoucherBook.FractionScale} 張，"
                                + $"超過券簿上限 {long.MaxValue} 張 ⇒ 拒絕（請分批、或少換一點）";
                return aResult;
            }

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

            // 守衛⑤：記交易事件（給報酬率用）。報價取試算那一刻的同一份設定 —— 跟實際成交率同源。
            SCP_MarketRateCache.TryGetQuote(aConfig, aResult.FromVoucher, out var aFromQ);
            SCP_MarketRateCache.TryGetQuote(aConfig, aResult.ToVoucher, out var aToQ);
            decimal aFromBidUsd = aFromQ?.Bid ?? 0m;
            decimal aToAskUsd = aToQ?.Ask ?? 0m;
            if (aFromBidUsd == 0m && aToAskUsd > 0m && aResult.EffectiveRate > 0m)
            {
                // 固定折算對（如 FLORIN -> GOLD）：來源券隱含 USD 價值以目標券市價乘折算率
                aFromBidUsd = aToAskUsd * aResult.EffectiveRate;
            }
            aResult.PortfolioWarning = SCP_Portfolio.RecordSwap(iLettersRoot, iPersona,
                aResult.FromVoucher, (long)iAmount * SCP_VoucherBook.FractionScale, aFromBidUsd,
                aResult.ToVoucher, aToUnitsE8, aToAskUsd,
                SCP_MarketRateCache.TotalFeeFactor(aConfig, aResult.FromVoucher, aResult.ToVoucher),
                iNow, iRegion);
            return aResult;
        }
    }
}
