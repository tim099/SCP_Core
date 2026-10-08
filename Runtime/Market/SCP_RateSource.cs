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
    /// **帶標頭、回狀態碼**的抓取（TASK-0319：Discord API 要 `Authorization: Bot …`，而且 401／403 要分得出來）。
    /// ⚠ 是**可選**介面：宿主的抓取器有實作才用得到（`SCP_HttpFetch.Current as ISCP_HttpHeaderFetcher`）；
    ///   沒實作 ⇒ 呼叫端要明說「本宿主不能帶標頭」，⛔ 不退回不帶標頭的 GET（那只會換來一個看起來像權限問題的 401）。
    /// ⚠ 標頭值可能是憑證 ⇒ 實作端 ⛔ 不得把標頭寫進錯誤訊息或 log。
    /// </summary>
    public interface ISCP_HttpHeaderFetcher : ISCP_HttpFetcher
    {
        /// <summary>GET 並帶標頭。<paramref name="oStatus"/>＝HTTP 狀態碼（沒拿到回應 ⇒ 0）。非 2xx 回 false。</summary>
        bool TryGetText(string iUrl, IReadOnlyDictionary<string, string> iHeaders, int iTimeoutSec,
                        out string oBody, out int oStatus, out string? oError);
    }

    /// <summary>
    /// **POST JSON**（TASK-0316／0320：Discord webhook 發文）。可選介面，同 <see cref="ISCP_HttpHeaderFetcher"/>。
    /// <paramref name="oRetryAfterSec"/>＝429 時對方要求等幾秒（`Retry-After` 標頭或回應的 `retry_after`；沒有 ⇒ 0）。
    /// ⚠ URL 可能含憑證（webhook token）⇒ 實作端 ⛔ 不得把 URL 寫進錯誤訊息或 log。
    /// </summary>
    public interface ISCP_HttpPoster
    {
        bool TryPostJson(string iUrl, string iJson, int iTimeoutSec,
                         out string oBody, out int oStatus, out double oRetryAfterSec, out string? oError);
    }

    /// <summary>
    /// **抓位元組**（TASK-0323：Discord 附件下載）。可選介面，同 <see cref="ISCP_HttpHeaderFetcher"/>。
    /// <paramref name="iMaxBytes"/>＝上限：回應超過就中止並回 false（⛔ 不先整包讀進記憶體再判）。
    /// ⚠ 附件 URL 帶簽章 ⇒ 實作端 ⛔ 不得把 URL 寫進錯誤訊息。
    /// </summary>
    public interface ISCP_HttpBytesFetcher
    {
        bool TryGetBytes(string iUrl, long iMaxBytes, int iTimeoutSec, out byte[] oData, out int oStatus, out string? oError);
    }

    /// <summary>multipart 的一個檔案段。</summary>
    public sealed class SCP_HttpFilePart
    {
        public string FieldName = "";
        public string FileName = "";
        public string ContentType = "application/octet-stream";
        public byte[] Data = new byte[0];
    }

    /// <summary>
    /// **帶標頭的表單 POST**（TASK-0362：Plurk 的 OAuth 1.0a —— 每一次請求都要自己簽的 `Authorization` 標頭）。
    /// <para><paramref name="iFiles"/> 為 null ⇒ `application/x-www-form-urlencoded`（欄位走 body）；
    /// 非 null ⇒ `multipart/form-data`（欄位當字串段、檔案當檔案段 —— Plurk 上傳圖片那條）。</para>
    /// <para>回傳語意與其他介面**刻意不同**：**拿到 HTTP 回應就回 true（不論狀態碼）**，body 照樣填 ——
    /// Plurk 的 4xx body 帶 `error_text`，那正是要印給人看的東西；只有連線層失敗（逾時／DNS）才回 false 且 <paramref name="oStatus"/>=0。</para>
    /// ⚠ 標頭含簽章 ⇒ 實作端 ⛔ 不得把標頭寫進錯誤訊息或 log。
    /// </summary>
    public interface ISCP_HttpFormRequester
    {
        bool TryPostForm(string iUrl, IReadOnlyDictionary<string, string> iHeaders,
                         IReadOnlyList<KeyValuePair<string, string>> iFields, IReadOnlyList<SCP_HttpFilePart>? iFiles,
                         int iTimeoutSec, out string oBody, out int oStatus, out string? oError);
    }

    /// <summary>
    /// **multipart/form-data POST**（TASK-0323：Discord webhook 帶圖）：一段 `payload_json` ＋ N 個檔案段。
    /// 回傳語意同 <see cref="ISCP_HttpPoster.TryPostJson"/>（含 429 的 Retry-After）。⛔ URL 不進錯誤訊息。
    /// </summary>
    public interface ISCP_HttpMultipartPoster
    {
        bool TryPostMultipart(string iUrl, string iPayloadJson, IReadOnlyList<SCP_HttpFilePart> iFiles, int iTimeoutSec,
                              out string oBody, out int oStatus, out double oRetryAfterSec, out string? oError);
    }

    /// <summary>
    /// 全域抓取器插座。宿主（Senate CLI／Server）啟動時塞一個進來；
    /// 沒塞的宿主 ⇒ `op=sync` 會明說「本宿主沒有抓取器」而不是靜默沒事。
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

        /// <summary>
        /// 法幣匯率表（TASK-0371）—— 回應是 `{"rates": {"TWD": 32.1, "JPY": 149.3, …}}`、**以 USD 為底**
        /// （1 USD 兌多少該幣，例：open.er-api.com `/v6/latest/USD`）。
        /// ⚠ 方向跟本系統相反：報價模型存的是「1 單位該幣值多少 USD」⇒ 解析時**取倒數**。
        /// 取哪一格由報價自己的 Symbol 決定 —— ⛔ 不另開欄位寫路徑，一個 URL 就能餵所有法幣。
        /// 只有中間價 ⇒ Bid ＝ Ask、two_sided = false（成本由手續費承擔，同 <see cref="MidPriceJson"/>）。
        /// </summary>
        public const string FxRatesPerUsd = "fx_rates_per_usd";

        public static bool IsKnown(string iKind)
            => iKind == BinanceBookTicker || iKind == MidPriceJson || iKind == FxRatesPerUsd;

        public static IReadOnlyList<string> All => new[] { BinanceBookTicker, MidPriceJson, FxRatesPerUsd };
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
                case SCP_RateSourceKind.FxRatesPerUsd: return ParseFxRatesPerUsd(iSymbol, iBody);
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

        static SCP_RateFetchResult ParseFxRatesPerUsd(string iSymbol, string iBody)
        {
            SCP_JsonData aJd;
            try { aJd = SCP_JsonParser.Parse(iBody); }
            catch (Exception e) { return SCP_RateFetchResult.Fail(iSymbol, $"回應不是合法 JSON（{e.GetType().Name}）：{Sample(iBody)}"); }

            // 端點自己說失敗（open.er-api：`"result": "error"`）⇒ 照實回報，⛔ 不往下找數字
            string aResult = aJd.GetString("result", "");
            if (aResult.Length > 0 && !string.Equals(aResult, "success", StringComparison.OrdinalIgnoreCase))
                return SCP_RateFetchResult.Fail(iSymbol, $"端點回報失敗（result={aResult}）：{Sample(iBody)}");

            // 底幣必須是 USD：底幣換了，倒數出來的就不是「值多少 USD」，而數字仍然合理 —— 不會有人發現
            string aBase = aJd.GetString("base_code", aJd.GetString("base", "USD"));
            if (!string.Equals(aBase, "USD", StringComparison.OrdinalIgnoreCase))
                return SCP_RateFetchResult.Fail(iSymbol, $"匯率表的底幣是 '{aBase}' 不是 USD ⇒ 倒數會是錯的單位，拒收");

            var aRates = aJd["rates"];
            if (!aRates.IsObject)
                return SCP_RateFetchResult.Fail(iSymbol, $"回應缺 rates 物件（端點改格式了？）：{Sample(iBody)}");
            string aKey = iSymbol.Trim().ToUpperInvariant();
            if (!aRates[aKey].Exists)
                return SCP_RateFetchResult.Fail(iSymbol, $"匯率表裡沒有 '{aKey}'（這個端點不報這個幣）");
            if (!TryDec(aRates[aKey].AsString(), out decimal aPerUsd) || aPerUsd <= 0m)
                return SCP_RateFetchResult.Fail(iSymbol, $"'{aKey}' 的匯率不是正數：'{aRates[aKey].AsString()}'");

            decimal aUsdPerUnit = 1m / aPerUsd;
            return Validate(iSymbol, aUsdPerUnit, aUsdPerUnit, iTwoSided: false);
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
