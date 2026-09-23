// 區塊職責：**匯率來源契約與解析器**（TASK-0272 驗收②）。
// 物理意義：把「去哪裡抓」與「抓回來怎麼讀」拆成兩件事 ——
//           **抓**（HTTP）由宿主注入，**讀**（解析成 Bid/Ask）是純函式住在這裡。
// 數值影響：本檔零 I/O、零 async。解析全部是字串 → decimal。
// 🩸 守衛：
//   ① **SCP_Core 不引入網路與 async**（2026-09-23 拍板）。
//      理由是量出來的：本層在本次改動前**零 `System.Net.Http`、零 `async Task`**，
//      而它釘在 netstandard2.1 / C# 9 **因為 Unity 也要編它** ——
//      在這裡開第一筆網路 I/O，等於讓 Unity Editor 從此背著一條 HTTP 依賴。
//      ⇒ 契約與解析留在這裡（跨宿主共用、可測），抓取交給宿主（`ISCP_HttpFetcher`）。
//   ② **沒有註冊抓取器時要大聲說**，不可靜默當成「沒有新報價」——
//      「抓不到」與「抓到了但沒變」在快取檔上**逐字同形**。
//   ③ **只給中間價的來源，不准自己編造價差**（Tim 2026-09-23：成本在手續費不在人造價差）。
//      ⇒ 這種來源一律 `Bid = Ask = price` 並標記 `two_sided = false`，
//      **不知道的事就讓它看起來像不知道**。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Json;

namespace SCP.Core.Market
{
    /// <summary>
    /// 宿主注入的抓取器契約。**同步、回傳字串、不丟例外**。
    /// 之所以不是 async：見本檔守衛①。之所以不丟例外：呼叫端是 Cmd，
    /// 它要把失敗**印成一行字**，不是讓整支指令炸掉。
    /// </summary>
    public interface ISCP_HttpFetcher
    {
        /// <summary>抓一段文字回來。成功回 true 並填 <paramref name="oBody"/>；失敗回 false 並填 <paramref name="oError"/>。</summary>
        bool TryGetText(string iUrl, int iTimeoutSec, out string oBody, out string? oError);

        /// <summary>這個抓取器是誰（印在讀數裡，讓人查得出那一筆是誰抓的）。</summary>
        string FetcherName { get; }
    }

    /// <summary>
    /// 全域抓取器插座。宿主（Senate CLI／Server）啟動時塞一個進來；
    /// Unity 那側**刻意不塞** ⇒ `op=sync` 會明說「本宿主沒有抓取器」而不是靜默沒事。
    /// </summary>
    public static class SCP_HttpFetch
    {
        public static ISCP_HttpFetcher? Current;

        public static bool IsAvailable => Current != null;
    }

    /// <summary>一次抓取＋解析的結果。</summary>
    public sealed class SCP_RateFetchResult
    {
        public string Symbol = "";
        public decimal Bid;
        public decimal Ask;

        /// <summary>來源有沒有給**雙向**盤口。false ＝ 只有中間價（Bid ＝ Ask）。</summary>
        public bool TwoSided;

        /// <summary>解析成功與否；false 時看 <see cref="Error"/>。</summary>
        public bool Ok;
        public string? Error;

        public static SCP_RateFetchResult Fail(string iSymbol, string iError)
            => new SCP_RateFetchResult { Symbol = iSymbol, Ok = false, Error = iError };
    }

    /// <summary>
    /// 來源種類 —— **字串常數不是列舉**：它要能往返 JSON，
    /// 而列舉的序號在增刪一項之後會指到別人身上，且那不會報錯。
    /// </summary>
    public static class SCP_RateSourceKind
    {
        /// <summary>Binance `/api/v3/ticker/bookTicker` —— 原生雙向盤口（`bidPrice` / `askPrice`）。</summary>
        public const string BinanceBookTicker = "binance_bookticker";

        /// <summary>gold-api `/price/XAU` 之類 —— **只有中間價**（`price`）。</summary>
        public const string MidPriceJson = "mid_price_json";

        public static bool IsKnown(string iKind)
            => iKind == BinanceBookTicker || iKind == MidPriceJson;

        public static IReadOnlyList<string> All => new[] { BinanceBookTicker, MidPriceJson };
    }

    /// <summary>
    /// 回應解析器。**純函式：字串進、數字出**，沒有 I/O ⇒ 可以拿固定樣本直接測。
    /// 🩸 每一個失敗都要說出**收到的原文前綴** —— 端點改格式時，
    /// 「解析不出來」與「這個券沒報價」在上層長得一模一樣。
    /// </summary>
    public static class SCP_RateSourceParser
    {
        const int ErrorSampleLen = 160;

        public static SCP_RateFetchResult Parse(string iSymbol, string iKind, string iBody)
        {
            if (string.IsNullOrWhiteSpace(iBody))
                return SCP_RateFetchResult.Fail(iSymbol, "回應是空的（零位元組）—— 那不是「沒有報價」，是沒有收到東西");

            switch (iKind)
            {
                case SCP_RateSourceKind.BinanceBookTicker: return ParseBinance(iSymbol, iBody);
                case SCP_RateSourceKind.MidPriceJson: return ParseMidPrice(iSymbol, iBody);
                default:
                    return SCP_RateFetchResult.Fail(iSymbol,
                        $"認不得的來源種類 '{iKind}'（可用：{string.Join(" / ", SCP_RateSourceKind.All)}）");
            }
        }

        static SCP_RateFetchResult ParseBinance(string iSymbol, string iBody)
        {
            SCP_JsonData aJd;
            try { aJd = SCP_JsonParser.Parse(iBody); }
            catch (Exception e) { return SCP_RateFetchResult.Fail(iSymbol, $"回應不是合法 JSON（{e.GetType().Name}）：{Sample(iBody)}"); }

            string aBidStr = aJd.GetString("bidPrice", "");
            string aAskStr = aJd.GetString("askPrice", "");
            if (aBidStr.Length == 0 || aAskStr.Length == 0)
                return SCP_RateFetchResult.Fail(iSymbol, $"回應缺 bidPrice/askPrice（端點改格式了？）：{Sample(iBody)}");

            if (!TryDec(aBidStr, out decimal aBid) || !TryDec(aAskStr, out decimal aAsk))
                return SCP_RateFetchResult.Fail(iSymbol, $"bidPrice/askPrice 不是數字：bid='{aBidStr}' ask='{aAskStr}'");

            return Validate(iSymbol, aBid, aAsk, iTwoSided: true);
        }

        static SCP_RateFetchResult ParseMidPrice(string iSymbol, string iBody)
        {
            SCP_JsonData aJd;
            try { aJd = SCP_JsonParser.Parse(iBody); }
            catch (Exception e) { return SCP_RateFetchResult.Fail(iSymbol, $"回應不是合法 JSON（{e.GetType().Name}）：{Sample(iBody)}"); }

            if (!aJd["price"].Exists)
                return SCP_RateFetchResult.Fail(iSymbol, $"回應缺 price（端點改格式了？）：{Sample(iBody)}");

            decimal aMid = (decimal)aJd.GetDouble("price", 0.0);

            // ⛔ 不編造價差：不知道盤口就讓 Bid ＝ Ask，並標 two_sided = false。
            //    成本本來就該由手續費承擔（守衛③）。
            return Validate(iSymbol, aMid, aMid, iTwoSided: false);
        }

        static SCP_RateFetchResult Validate(string iSymbol, decimal iBid, decimal iAsk, bool iTwoSided)
        {
            if (iBid <= 0 || iAsk <= 0)
                return SCP_RateFetchResult.Fail(iSymbol, $"報價必須 > 0（bid={iBid} ask={iAsk}）—— 0 在這裡是「沒抓到」不是「不值錢」");
            if (iAsk < iBid)
                return SCP_RateFetchResult.Fail(iSymbol, $"賣出價低於買入價（bid={iBid} ask={iAsk}）—— 倒掛的盤口等於無成本套利，拒收");

            return new SCP_RateFetchResult
            {
                Symbol = iSymbol,
                Bid = iBid,
                Ask = iAsk,
                TwoSided = iTwoSided,
                Ok = true
            };
        }

        static bool TryDec(string iStr, out decimal oVal)
            => decimal.TryParse(iStr, NumberStyles.Any, CultureInfo.InvariantCulture, out oVal);

        static string Sample(string iBody)
        {
            string aOne = iBody.Replace('\r', ' ').Replace('\n', ' ').Trim();
            return aOne.Length <= ErrorSampleLen ? aOne : aOne.Substring(0, ErrorSampleLen) + "…";
        }
    }
}
