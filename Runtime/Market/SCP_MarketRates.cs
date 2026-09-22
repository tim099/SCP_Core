// 區塊職責：**匯率模型與快取資料層**（TASK-0272）。
// 物理意義：`Market/rates_cache.json`。記錄各券種相對於基準貨幣（USD）之買入價（Bid）與賣出價（Ask）。
//           解耦邊界：匯率端點定時刷新或後台手填只寫入本檔，兌換引擎只讀取本檔。
// 數值影響：原子寫入（tmp → replace）。
// 🩸 守衛：
//   ① **無緩存資訊時，視為無法兌換**（Tim 2026-09-22 拍板：並非所有券都能互相兌換）。
//      未列入快取、未啟用、或價格 ≤ 0 之券種，兌換計算一律嚴格拒絕。
//   ② **USD 中介樞紐雙向撮合**：賣出 A 依 A 之 Bid 換 USD，再依 B 之 Ask 買入 B。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Json;
using SCP.Core.Voucher;

namespace SCP.Core.Market
{
    /// <summary>單一券種相對於基準幣（USD）的報價。</summary>
    public sealed class SCP_RateQuote
    {
        /// <summary>券種代號（如 BTC, GOLD, USD）。</summary>
        public string Symbol = "";

        /// <summary>基準貨幣，預設固定為 USD。</summary>
        public string BaseCurrency = "USD";

        /// <summary>買入價（用戶賣出該資產可獲得之 USD 單價）。</summary>
        public decimal Bid;

        /// <summary>賣出價（用戶支付 USD 買入該資產所需之 USD 單價）。</summary>
        public decimal Ask;

        /// <summary>報價更新時間戳（UTC ISO8601）。</summary>
        public string UpdatedAtUtc = "";

        /// <summary>資料來源（如 manual, admin, binance, coingecko）。</summary>
        public string Source = "manual";

        /// <summary>是否開放兌換（開關）。</summary>
        public bool IsEnabled = true;

        public SCP_JsonData ToJson()
        {
            var aData = SCP_JsonData.NewObject();
            aData.Set("symbol", SCP_JsonData.NewString(Symbol));
            aData.Set("base_currency", SCP_JsonData.NewString(BaseCurrency));
            aData.Set("bid", SCP_JsonData.NewNumber((double)Bid));
            aData.Set("ask", SCP_JsonData.NewNumber((double)Ask));
            aData.Set("updated_at_utc", SCP_JsonData.NewString(UpdatedAtUtc));
            aData.Set("source", SCP_JsonData.NewString(Source));
            aData.Set("is_enabled", SCP_JsonData.NewBool(IsEnabled));
            return aData;
        }

        public static SCP_RateQuote FromJson(SCP_JsonData iData)
        {
            return new SCP_RateQuote
            {
                Symbol = iData.GetString("symbol", "").Trim().ToUpperInvariant(),
                BaseCurrency = iData.GetString("base_currency", "USD").Trim().ToUpperInvariant(),
                Bid = (decimal)iData.GetDouble("bid", 0.0),
                Ask = (decimal)iData.GetDouble("ask", 0.0),
                UpdatedAtUtc = iData.GetString("updated_at_utc", ""),
                Source = iData.GetString("source", "manual"),
                IsEnabled = iData.GetBool("is_enabled", true),
            };
        }
    }

    /// <summary>匯率市場全域設定與各券種報價字典。</summary>
    public sealed class SCP_MarketRateConfig
    {
        public string BaseCurrency = "USD";
        public string UpdatedAtUtc = "";
        public bool FxSystemEnabled = true;
        public decimal DefaultSpreadPct = 0.005m;
        public Dictionary<string, SCP_RateQuote> Quotes = new Dictionary<string, SCP_RateQuote>(StringComparer.OrdinalIgnoreCase);

        public SCP_JsonData ToJson()
        {
            var aData = SCP_JsonData.NewObject();
            aData.Set("base_currency", SCP_JsonData.NewString(BaseCurrency));
            aData.Set("updated_at_utc", SCP_JsonData.NewString(UpdatedAtUtc));
            aData.Set("fx_system_enabled", SCP_JsonData.NewBool(FxSystemEnabled));
            aData.Set("default_spread_pct", SCP_JsonData.NewNumber((double)DefaultSpreadPct));

            var aQuotesObj = SCP_JsonData.NewObject();
            foreach (var kvp in Quotes)
            {
                aQuotesObj.Set(kvp.Key, kvp.Value.ToJson());
            }
            aData.Set("quotes", aQuotesObj);
            return aData;
        }

        public static SCP_MarketRateConfig FromJson(SCP_JsonData iData)
        {
            var aCfg = new SCP_MarketRateConfig
            {
                BaseCurrency = iData.GetString("base_currency", "USD").Trim().ToUpperInvariant(),
                UpdatedAtUtc = iData.GetString("updated_at_utc", ""),
                FxSystemEnabled = iData.GetBool("fx_system_enabled", true),
                DefaultSpreadPct = (decimal)iData.GetDouble("default_spread_pct", 0.005),
            };

            SCP_JsonData aQuotes = iData["quotes"];
            if (aQuotes.Exists && aQuotes.IsObject)
            {
                foreach (string aKey in aQuotes.Keys)
                {
                    SCP_JsonData aItem = aQuotes[aKey];
                    if (aItem.IsObject)
                    {
                        var aQuote = SCP_RateQuote.FromJson(aItem);
                        if (string.IsNullOrEmpty(aQuote.Symbol)) aQuote.Symbol = aKey.ToUpperInvariant();
                        aCfg.Quotes[aQuote.Symbol] = aQuote;
                    }
                }
            }
            return aCfg;
        }
    }

    /// <summary>匯率快取檔案管理與兌換撮合計算。</summary>
    public static class SCP_MarketRateCache
    {
        public const string MarketDirName = "Market";
        public const string RatesCacheFileName = "rates_cache.json";

        public static string MarketDir(string iDataRoot) => Path.Combine(iDataRoot, MarketDirName);
        public static string RatesCachePath(string iDataRoot) => Path.Combine(MarketDir(iDataRoot), RatesCacheFileName);

        /// <summary>載入匯率快取檔。檔案不存在時回傳包含預設 USD 報價之設定。</summary>
        public static SCP_MarketRateConfig Load(string iDataRoot, out string? oError)
        {
            oError = null;
            string aPath = RatesCachePath(iDataRoot);
            if (!File.Exists(aPath))
            {
                var aDefault = new SCP_MarketRateConfig();
                // 預設給予 USD 1:1 基準報價
                aDefault.Quotes["USD"] = new SCP_RateQuote
                {
                    Symbol = "USD",
                    BaseCurrency = "USD",
                    Bid = 1.0m,
                    Ask = 1.0m,
                    Source = "builtin",
                    UpdatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                    IsEnabled = true
                };
                return aDefault;
            }

            try
            {
                string aText = File.ReadAllText(aPath);
                var aJd = SCP_JsonParser.Parse(aText);
                var aCfg = SCP_MarketRateConfig.FromJson(aJd);
                if (!aCfg.Quotes.ContainsKey("USD"))
                {
                    aCfg.Quotes["USD"] = new SCP_RateQuote
                    {
                        Symbol = "USD",
                        BaseCurrency = "USD",
                        Bid = 1.0m,
                        Ask = 1.0m,
                        Source = "builtin",
                        UpdatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        IsEnabled = true
                    };
                }
                return aCfg;
            }
            catch (Exception e)
            {
                oError = $"匯率快取讀取失敗（{aPath}）：{e.GetType().Name}: {e.Message}";
                return new SCP_MarketRateConfig();
            }
        }

        /// <summary>儲存匯率快取檔（原子寫入：tmp → replace）。</summary>
        public static bool Save(string iDataRoot, SCP_MarketRateConfig iConfig, out string? oError)
        {
            oError = null;
            string aPath = RatesCachePath(iDataRoot);
            string aDir = Path.GetDirectoryName(aPath)!;
            try
            {
                if (!Directory.Exists(aDir)) Directory.CreateDirectory(aDir);
                iConfig.UpdatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(iConfig.ToJson()) + "\n");
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
                return true;
            }
            catch (Exception e)
            {
                oError = $"匯率快取寫入失敗（{aPath}）：{e.Message}";
                return false;
            }
        }

        /// <summary>
        /// 嘗試取得特定券種之有效報價。
        /// 守衛：未列入快取、未啟用、或價格 ≤ 0 視為無效。
        /// </summary>
        public static bool TryGetQuote(SCP_MarketRateConfig iConfig, string iSymbol, out SCP_RateQuote? oQuote)
        {
            oQuote = null;
            if (string.IsNullOrWhiteSpace(iSymbol)) return false;
            string aKey = iSymbol.Trim().ToUpperInvariant();

            if (aKey == "USD")
            {
                oQuote = new SCP_RateQuote
                {
                    Symbol = "USD",
                    BaseCurrency = "USD",
                    Bid = 1.0m,
                    Ask = 1.0m,
                    IsEnabled = true,
                    Source = "builtin"
                };
                return true;
            }

            if (!iConfig.Quotes.TryGetValue(aKey, out var aQ)) return false;
            if (!aQ.IsEnabled) return false;
            if (aQ.Bid <= 0 || aQ.Ask <= 0) return false;

            oQuote = aQ;
            return true;
        }

        /// <summary>
        /// 計算兩券之間雙向折算比率（以 USD 為樞紐）。
        /// 賣出 1 A 可換得 B：Bid(A) / Ask(B)
        /// 買入 1 B 需支付 A：Ask(B) / Bid(A)
        /// </summary>
        public static bool TryGetPairRate(SCP_MarketRateConfig iConfig, string iFromSymbol, string iToSymbol,
                                          out decimal oSellRate, out decimal oBuyRate, out string? oReason)
        {
            oSellRate = 0;
            oBuyRate = 0;
            oReason = null;

            if (!iConfig.FxSystemEnabled)
            {
                oReason = "匯率系統未啟用（fx_system_enabled: false）";
                return false;
            }

            if (!TryGetQuote(iConfig, iFromSymbol, out var aFromQuote))
            {
                oReason = $"券種 '{iFromSymbol}' 無有效匯率快取報價，視為無法兌換（並非所有券都能互相兌換）";
                return false;
            }

            if (!TryGetQuote(iConfig, iToSymbol, out var aToQuote))
            {
                oReason = $"券種 '{iToSymbol}' 無有效匯率快取報價，視為無法兌換（並非所有券都能互相兌換）";
                return false;
            }

            // 賣出 1 A 換 B：A 賣出換 USD (Bid_A)，USD 買入 B (Ask_B)
            oSellRate = aFromQuote!.Bid / aToQuote!.Ask;

            // 買入 1 B 需支付 A：買 1 B 需 USD (Ask_B)，賣 A 換 USD (Bid_A)
            oBuyRate = aToQuote.Ask / aFromQuote.Bid;

            return true;
        }

        /// <summary>
        /// 核心撮合換算：將 <paramref name="iFromAmount"/> 張來源券兌換為目標券。
        /// 依據 TASK-0271 零頭模型，目標券產出以 1e-8 單位整數回傳（滿 1e8 即進位 1 張可用券）。
        /// </summary>
        public static bool TryCalculateSwap(SCP_MarketRateConfig iConfig, string iFromSymbol, string iToSymbol,
                                            int iFromAmount, out long oToUnitsE8, out decimal oEffectiveRate,
                                            out string? oReason)
        {
            oToUnitsE8 = 0;
            oEffectiveRate = 0;
            oReason = null;

            if (!iConfig.FxSystemEnabled)
            {
                oReason = "匯率互換系統目前未啟用（fx_system_enabled: false）";
                return false;
            }

            if (iFromAmount <= 0)
            {
                oReason = $"兌換數量必須 > 0（收到 {iFromAmount}）";
                return false;
            }

            string aFrom = iFromSymbol.Trim().ToUpperInvariant();
            string aTo = iToSymbol.Trim().ToUpperInvariant();

            if (string.Equals(aFrom, aTo, StringComparison.Ordinal))
            {
                oReason = $"來源券與目標券相同（{aFrom}），無需兌換";
                return false;
            }

            if (!TryGetQuote(iConfig, aFrom, out var aFromQuote))
            {
                oReason = $"來源券 '{iFromSymbol}' 無有效匯率快取報價，視為無法兌換（並非所有券都能互相兌換）";
                return false;
            }

            if (!TryGetQuote(iConfig, aTo, out var aToQuote))
            {
                oReason = $"目標券 '{iToSymbol}' 無有效匯率快取報價，視為無法兌換（並非所有券都能互相兌換）";
                return false;
            }

            // 兩段撮合算式：Rate = Bid(A) / Ask(B)
            oEffectiveRate = aFromQuote!.Bid / aToQuote!.Ask;

            decimal aTargetTotal = (decimal)iFromAmount * oEffectiveRate;
            long aUnitsE8 = (long)Math.Floor(aTargetTotal * (decimal)SCP_VoucherBook.FractionScale);

            if (aUnitsE8 <= 0)
            {
                oReason = $"折算後目標券數量不足 1e-8 單位（{aTargetTotal:0.########}），無法完成兌換";
                return false;
            }

            oToUnitsE8 = aUnitsE8;
            return true;
        }
    }
}
