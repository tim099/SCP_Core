// 區塊職責：**匯率模型與快取資料層**（TASK-0272）。
// 物理意義：`Market/rates_cache.json`。記錄各券種相對於基準貨幣（USD）之買入價（Bid）與賣出價（Ask）。
//           解耦邊界：匯率端點定時刷新或後台手填只寫入本檔，兌換引擎只讀取本檔。
// 數值影響：原子寫入（tmp → replace）。
// 🩸 守衛：
//   ① **無緩存資訊時，視為無法兌換**（Tim 2026-09-22 拍板：並非所有券都能互相兌換）。
//      未列入快取、未啟用、或價格 ≤ 0 之券種，兌換計算一律嚴格拒絕。
//   ② **USD 中介樞紐雙向撮合**：賣出 A 依 A 之 Bid 換 USD，再依 B 之 Ask 買入 B。
//   ③ **成本在手續費，不在人造價差**（Tim 2026-09-23 拍板：「免費往返沒問題，就是想模擬真實情況、
//      按照實際交易手續費配置」）。真實交易所盤口價差極薄 —— 實測 BTCUSDT 0.000012%、
//      PAXGUSDT 0.000230%，BTC→GOLD→BTC 往返損耗 **2.42 ppm ≒ 免費**。
//      🩸 **那是現實，不是 bug**：現實裡的成本是**每筆成交的手續費**，不是盤口。
//      ⇒ 手續費**按腿計**：USD 樞紐兩段撮合＝兩筆成交＝收兩次；
//        一端是 USD 時那一腿不存在（沒有成交）⇒ **不收**。
//   🩸 前身 `default_spread_pct` 已**整個移除**（不留過渡欄位，歷史在 git）：
//      它只被序列化與反序列化，沒有任何一處拿它算過東西 ——
//      看得見、改得動、存得下、讀得回，**而改它不會改變任何一個數字**。
//      ⇒ 一個假的政策旋鈕比沒有旋鈕更糟：它讓人以為價差已經設好了。
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

        /// <summary>
        /// 這個券種**單筆成交**的手續費率覆寫（0.001 ＝ 0.1%）。
        /// <c>null</c> ＝ 沒有覆寫，吃 <see cref="SCP_MarketRateConfig.TakerFeePct"/>。
        /// ⚠ 用 <c>null</c> 而不是 0：**「沒設定」與「設成免手續費」是兩件事**，
        /// 用 0 當「沒設定」的話，真的要開免手續費的券就寫不出來了。
        /// </summary>
        public decimal? FeePct;

        /// <summary>
        /// 這個券種要去哪裡抓報價（空 ＝ 純手填，`op=sync` 會**顯式跳過並說出來**）。
        /// 🩸 這一格存在的理由：`Source` 只寫得下 "admin_ui" / "binance" 這種名字，
        /// 查不出**是哪一個端點**。而端點改格式的那一天，查得出來與查不出來差一整個下午。
        /// </summary>
        public string SourceUrl = "";

        /// <summary>回應要用哪個解析器讀（見 <see cref="SCP_RateSourceKind"/>）。空 ＝ 沒設定。</summary>
        public string SourceKind = "";

        /// <summary>
        /// 來源有沒有給**雙向**盤口。false ＝ 只拿得到中間價（Bid ＝ Ask）。
        /// ⚠ 這不是瑕疵、也不該被補上人造價差 —— 成本由手續費承擔（見檔頭守衛③）。
        /// 它存在只是為了讓「盤口真的很薄」與「我們根本沒有盤口」**分得出來**。
        /// </summary>
        public bool TwoSided = true;

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
            // ⛔ 沒有覆寫時**不寫這個 key** —— 寫一個 0 進去，下次讀回來就變成「這個券免手續費」。
            if (FeePct.HasValue) aData.Set("fee_pct", SCP_JsonData.NewNumber((double)FeePct.Value));
            // 同理：沒設來源就不寫那兩個 key，讓「純手填」在檔案上看得出來是純手填。
            if (!string.IsNullOrEmpty(SourceUrl)) aData.Set("source_url", SCP_JsonData.NewString(SourceUrl));
            if (!string.IsNullOrEmpty(SourceKind)) aData.Set("source_kind", SCP_JsonData.NewString(SourceKind));
            aData.Set("two_sided", SCP_JsonData.NewBool(TwoSided));
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
                // ⚠ 靠 key 在不在判「有沒有覆寫」—— 不能用 GetDouble 的預設值，
                //   那會讓「沒寫」與「寫了 0」讀回來長得一模一樣。
                FeePct = iData["fee_pct"].Exists ? (decimal)iData.GetDouble("fee_pct", 0.0) : (decimal?)null,
                SourceUrl = iData.GetString("source_url", ""),
                SourceKind = iData.GetString("source_kind", ""),
                // ⚠ 舊檔沒有這個 key ⇒ 預設 true（手填的 Bid/Ask 本來就是兩個獨立的數）。
                TwoSided = iData.GetBool("two_sided", true),
            };
        }
    }

    /// <summary>匯率市場全域設定與各券種報價字典。</summary>
    public sealed class SCP_MarketRateConfig
    {
        public string BaseCurrency = "USD";
        public string UpdatedAtUtc = "";
        public bool FxSystemEnabled = true;

        /// <summary>
        /// 全域**單筆成交**手續費率（0.001 ＝ 0.1%）。預設值取自現實：
        /// Binance 現貨 taker 0.1%（BTCUSDT 與 PAXGUSDT 都在那個場子）。
        /// 個別券種要用別的費率就在該券的 <see cref="SCP_RateQuote.FeePct"/> 覆寫。
        /// ⚠ 這個值**真的會進算式**（<see cref="SCP_MarketRateCache.TryCalculateSwap"/>）——
        /// 它不是前身 `default_spread_pct` 那種只存不用的欄位。
        /// </summary>
        public decimal TakerFeePct = 0.001m;

        /// <summary>
        /// 報價幾分鐘後算過期（預設 1440 ＝ 一天，對應「跨日結算」）。
        /// ⚠ 過期**不等於不可用** —— 它只是「該去刷新了」的讀數。
        /// 🩸 刻意不讓過期自動變成「無法兌換」：那會讓一次網路中斷升級成全體停擺，
        /// 而停擺的樣子跟「這個券本來就不能換」在上層逐字同形。
        /// </summary>
        public int SyncTtlMinutes = 1440;

        public Dictionary<string, SCP_RateQuote> Quotes = new Dictionary<string, SCP_RateQuote>(StringComparer.OrdinalIgnoreCase);

        public SCP_JsonData ToJson()
        {
            var aData = SCP_JsonData.NewObject();
            aData.Set("base_currency", SCP_JsonData.NewString(BaseCurrency));
            aData.Set("updated_at_utc", SCP_JsonData.NewString(UpdatedAtUtc));
            aData.Set("fx_system_enabled", SCP_JsonData.NewBool(FxSystemEnabled));
            aData.Set("taker_fee_pct", SCP_JsonData.NewNumber((double)TakerFeePct));
            aData.Set("sync_ttl_minutes", SCP_JsonData.NewNumber(SyncTtlMinutes));

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
                TakerFeePct = (decimal)iData.GetDouble("taker_fee_pct", 0.001),
                SyncTtlMinutes = (int)iData.GetDouble("sync_ttl_minutes", 1440.0),
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
        /// 這筆報價過期了沒。
        /// 🩸 三種結果要分得開，**不可共用一個 bool**：
        ///   · <c>oAgeMinutes &lt; 0</c> ⇒ **時間戳讀不出來**（不是「很新」也不是「很舊」，是沒有讀數）
        ///   · 過期 ⇒ 該刷新，但**仍可兌換**（見 <see cref="SCP_MarketRateConfig.SyncTtlMinutes"/> 的註解）
        ///   · 未過期 ⇒ 讀本地快取即可
        /// ⚠ 壞掉的時間戳回 true（視為過期）—— 讓它去刷新，而不是永遠當成新的。
        /// </summary>
        public static bool IsQuoteStale(SCP_MarketRateConfig iConfig, SCP_RateQuote iQuote, DateTime iNowUtc, out double oAgeMinutes)
        {
            oAgeMinutes = -1;
            if (iQuote == null || string.IsNullOrWhiteSpace(iQuote.UpdatedAtUtc)) return true;

            if (!DateTime.TryParse(iQuote.UpdatedAtUtc, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime aTs))
                return true;

            oAgeMinutes = (iNowUtc - aTs).TotalMinutes;
            if (oAgeMinutes < 0) oAgeMinutes = 0;   // 時鐘偏移：未來的時間戳當成剛更新，不當成過期
            return oAgeMinutes > iConfig.SyncTtlMinutes;
        }

        /// <summary>
        /// 這個券種單筆成交要收的手續費率：券別覆寫優先，沒有就吃全域。
        /// ⛔ USD 是樞紐本身、不是成交標的 ⇒ 永遠 0（走 USD 那一腿根本沒有成交）。
        /// 🩸 回傳值夾在 [0, 1)：費率 ≥ 1 會讓兌換結果變成 0 或負數，
        ///    而那在下游長得像「金額不足」，不像「設定填錯」。
        /// </summary>
        public static decimal FeePctOf(SCP_MarketRateConfig iConfig, string iSymbol)
        {
            if (string.IsNullOrWhiteSpace(iSymbol)) return 0m;
            string aKey = iSymbol.Trim().ToUpperInvariant();
            if (aKey == "USD") return 0m;

            decimal aFee = iConfig.TakerFeePct;
            if (iConfig.Quotes.TryGetValue(aKey, out var aQ) && aQ.FeePct.HasValue) aFee = aQ.FeePct.Value;

            if (aFee < 0m) return 0m;
            if (aFee >= 1m) return 0.999999m;
            return aFee;
        }

        /// <summary>
        /// 一趟 A→USD→B 撮合總共要收幾腿手續費、合計吃掉多少比例。
        /// ⇒ 兩端都不是 USD ＝ 兩筆成交；有一端是 USD ＝ 只有一筆。
        /// </summary>
        public static decimal TotalFeeFactor(SCP_MarketRateConfig iConfig, string iFromSymbol, string iToSymbol)
        {
            decimal aFactor = 1m;
            aFactor *= (1m - FeePctOf(iConfig, iFromSymbol));   // 賣出那一腿（USD ⇒ 費率 0 ⇒ 不影響）
            aFactor *= (1m - FeePctOf(iConfig, iToSymbol));     // 買入那一腿
            return aFactor;
        }

        /// <summary>
        /// 計算兩券之間雙向折算比率（以 USD 為樞紐）。
        /// 賣出 1 A 可換得 B：Bid(A) / Ask(B)
        /// 買入 1 B 需支付 A：Ask(B) / Bid(A)
        /// ⚠ **這是毛率（gross），不含手續費** —— 要拿來對帳實際到手數量請用
        /// <see cref="TryGetPairRateNet"/> 或 <see cref="TryCalculateSwap"/>。
        /// 🩸 兩個數字並存是刻意的：毛率是「市場價」、淨率是「我實際換得到多少」，
        ///   只印其中一個的話，另一個會在別處以不同的數字出現而沒有人解釋得了差額。
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
        /// 含手續費的淨折算率 —— **這一支才是「我實際換得到多少」**。
        /// <paramref name="oFeeFactor"/> 是手續費後剩下的比例（0.998 ＝ 總共吃掉 0.2%）。
        /// ⚠ 買入方向的費用是**加上去**不是乘下去：要拿到 1 B 得多付一點 A 來墊手續費。
        /// </summary>
        public static bool TryGetPairRateNet(SCP_MarketRateConfig iConfig, string iFromSymbol, string iToSymbol,
                                             out decimal oSellRateNet, out decimal oBuyRateNet,
                                             out decimal oFeeFactor, out string? oReason)
        {
            oSellRateNet = 0;
            oBuyRateNet = 0;
            oFeeFactor = 1m;

            if (!TryGetPairRate(iConfig, iFromSymbol, iToSymbol, out decimal aSellGross, out decimal aBuyGross, out oReason))
                return false;

            oFeeFactor = TotalFeeFactor(iConfig, iFromSymbol, iToSymbol);
            if (oFeeFactor <= 0m)
            {
                oReason = $"手續費設定吃掉了全部本金（費率合計 ≥ 100%）：{iFromSymbol} → {iToSymbol}";
                return false;
            }

            oSellRateNet = aSellGross * oFeeFactor;
            oBuyRateNet = aBuyGross / oFeeFactor;
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

            // 兩段撮合算式：毛率 Rate = Bid(A) / Ask(B)，再按腿扣手續費。
            // 🩸 `oEffectiveRate` 回傳的是**淨率**（已扣費）—— 它的名字叫 effective，
            //   而使用者實際拿到的就是這個數。若回毛率，下游印出的率與帳上增加的張數會對不起來，
            //   差額剛好是手續費，**而沒有任何一層會說那是手續費**。
            decimal aGrossRate = aFromQuote!.Bid / aToQuote!.Ask;
            decimal aFeeFactor = TotalFeeFactor(iConfig, aFrom, aTo);
            if (aFeeFactor <= 0m)
            {
                oReason = $"手續費設定吃掉了全部本金（費率合計 ≥ 100%）：{aFrom} → {aTo}";
                return false;
            }
            oEffectiveRate = aGrossRate * aFeeFactor;

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
