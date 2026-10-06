// 區塊職責：`cmd rate` —— 匯率快取管理（TASK-0272）。
// 物理意義：查閱與維護 `Market/rates_cache.json`。可查詢報價、手填 Bid/Ask 價格、切換全域互換開關。
// 數值影響：`op=list/get` 零寫入；`op=set/toggle` 原子寫入 `rates_cache.json`。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Market;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_MarketRate : SCP_Cmd
    {
        public override string Name => "rate";
        public override string Category => SCP_CmdCategory.Bank;

        /// <summary>
        /// 手續費率的合理性上限（**不含**）。現實中沒有任何交易所收到 5%
        /// （Binance 現貨 taker 0.1%、最貴的零售管道也在 1% 量級）。
        /// 🩸 血證 2026-09-23：這一格原本寫成 `> 0.5m`，而 **0.5 正是它要擋的那個手滑**
        /// （想打 0.5% 卻漏掉百分比那一格）—— `0.5 > 0.5` 為 false ⇒ 50% 手續費被寫進真檔。
        /// ⇒ 兩個教訓：① 邊界要用 `>=`，② 上限要離**合理值**近、離**手滑值**遠，
        ///   把門檻剛好設在最可能打錯的那個數字上，等於沒有門。
        /// </summary>
        const decimal FeeSanityCap = 0.05m;

        public override string Summary =>
            "匯率快取管理：查詢各券種報價、手填 Bid/Ask 價格、切換互換開關";

        public override string Details =>
            "· `op=list`（預設）：列出快取中所有券種相對於 USD 之買入價 (Bid) 與賣出價 (Ask)。\n"
            + "· `op=get`：查詢特定券種報價（`--arg symbol=<券名>`）或兩券折算比率（`--arg from=<A> --arg to=<B>`）。\n"
            + "· `op=set`：手填新增或更新特定券種相對於 USD 之報價（`--arg symbol=<券名> --arg bid=<買價> --arg ask=<賣價>`）。\n"
            + "· `op=toggle`：切換全域匯率互換系統開關（`--arg enabled=1/0`）。\n"
            + "· `op=fee`：設定**全域單筆成交手續費率**（`--arg fee_pct=0.001` ＝ 0.1%）。\n"
            + "· `op=source`：設定某券的抓取端點（`--arg symbol=<券> --arg url=<端點> --arg kind=<解析器>`）。\n"
            + "· `op=sync`：從已設定的端點刷新報價並落盤（`--arg symbol=<單一券>` 可只刷一個；`--arg force=1` 無視 TTL）。\n"
            + "  ⛔ 抓取由**宿主注入**（Senate CLI 有；Unity 端沒有 ⇒ 會明說「本宿主未註冊抓取器」而不是靜默沒事）。\n"
            + "  ⭐ **平常不手動跑**（Tim 2026-09-28）：每天由 Senate 端的 `demurrage op=run` 發完券之後觸發（`--arg day=<UTC 日>`，一天一版）。\n"
            + "  ⭐ 有更新才寫**一個歷史版本**（`Market/history/rates_<抓取時間>.json`，一版一檔、先於快取落盤）；沒更新不寫。\n"
            + "· `op=history`：歷史匯率。`--arg symbol=<券>` 查走勢（中間價序列＋變動幅度＋波動度，`since`／`until` 限區間）；\n"
            + "  `--arg version=<版本代號>` 讀回單一版本的完整報價表；兩者都不給 ⇒ 列出全部版本。\n"
            + "⚠ 無緩存資訊之券種一律視為無法兌換（並非所有券都能互相兌換，Tim 2026-09-22 拍板）。\n"
            + "⚠ 成本在**手續費**不在人造價差（Tim 2026-09-23）：真實盤口極薄（實測往返 2.42 ppm ≒ 免費），\n"
            + "  而現實的成本是每筆成交的手續費。⇒ **按腿計**：A→USD→B 兩筆成交收兩次；一端是 USD 只收一次。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("rate --arg op=list");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "資料根目錄（絕對路徑）。省略時自動嘗試推導", iDefault: ""),
            new SCP_CmdArgSpec("op", "做什麼（list|get|set|toggle|fee|source|sync|history，預設 list）", iDefault: "list",
                iChoices: new[] { "list", "get", "set", "toggle", "fee", "source", "sync", "history" }),
            new SCP_CmdArgSpec("symbol", "券種代號（如 BTC, GOLD, USD）", iDefault: ""),
            new SCP_CmdArgSpec("from", "來源券種代號（op=get 查匯率對時使用）", iDefault: ""),
            new SCP_CmdArgSpec("to", "目標券種代號（op=get 查匯率對時使用）", iDefault: ""),
            new SCP_CmdArgSpec("bid", "買入價（賣出 1 單位資產可得之 USD 單價，正數）", iDefault: ""),
            new SCP_CmdArgSpec("ask", "賣出價（買入 1 單位資產需支付之 USD 單價，正數）", iDefault: ""),
            new SCP_CmdArgSpec("enabled", "是否啟用（1=啟用, 0=停用）", iDefault: "1"),
            new SCP_CmdArgSpec("source", "資料來源標記（預設 manual）", iDefault: "manual"),
            new SCP_CmdArgSpec("fee_pct", "手續費率（0.001 ＝ 0.1%）。op=fee 設全域；op=set 設該券覆寫 —— ⛔ **留空＝不覆寫（吃全域）**，不是 0", iDefault: ""),
            new SCP_CmdArgSpec("url", "op=source：抓取端點完整網址", iDefault: ""),
            new SCP_CmdArgSpec("kind", "op=source：回應解析器（binance_bookticker｜mid_price_json｜fx_rates_per_usd —— 後者讀「1 USD 兌多少」的匯率表並取倒數，例 https://open.er-api.com/v6/latest/USD）", iDefault: ""),
            new SCP_CmdArgSpec("force", "op=sync：1＝無視 TTL 一律重抓（預設只抓過期的）", iDefault: "0"),
            new SCP_CmdArgSpec("timeout_sec", "op=sync：單一端點逾時秒數", iDefault: "12"),
            new SCP_CmdArgSpec("day", "op=sync：**每日一版**模式（`yyyy-MM-dd`，UTC 日）—— 那天已有刷新版本就不抓，沒有就無視 TTL 抓一次。`demurrage op=run` 發完券後用的就是這個", iDefault: ""),
            new SCP_CmdArgSpec("version", "op=history：讀回單一版本（版本代號＝檔名 `rates_<代號>.json` 的中段）", iDefault: ""),
            new SCP_CmdArgSpec("since", "op=history：區間起點（`yyyy-MM-dd` 當地整天起，或 ISO 8601）；留空＝不限", iDefault: ""),
            new SCP_CmdArgSpec("until", "op=history：區間終點（`yyyy-MM-dd` 當地整天止，或 ISO 8601）；留空＝不限", iDefault: ""),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = ResolveDataRoot(iArgs.Get("data_root"));
            if (string.IsNullOrWhiteSpace(aDataRoot) || !Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(1, $"✗ 資料根不存在或無效：{aDataRoot}");

            string aOp = iArgs.Get("op").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(aOp)) aOp = "list";

            var aConfig = SCP_MarketRateCache.Load(aDataRoot, out string? aLoadErr);
            if (aLoadErr != null)
                return SCP_CmdResult.Fail(1, $"✗ 匯率快取載入失敗：{aLoadErr}");

            return aOp switch
            {
                "list" => OpList(aConfig),
                "get" => OpGet(aConfig, iArgs),
                "set" => OpSet(aDataRoot, aConfig, iArgs),
                "toggle" => OpToggle(aDataRoot, aConfig, iArgs),
                "fee" => OpFee(aDataRoot, aConfig, iArgs),
                "source" => OpSource(aDataRoot, aConfig, iArgs),
                "sync" => OpSync(aDataRoot, aConfig, iArgs),
                "history" => OpHistory(aDataRoot, iArgs),
                _ => SCP_CmdResult.Fail(2, $"✗ 認不得的 op='{aOp}'（list|get|set|toggle|fee|source|sync|history）"),
            };
        }

        static SCP_CmdResult OpList(SCP_MarketRateConfig iConfig)
        {
            var aR = SCP_CmdResult.Success("# 匯率快取報價清單（基準幣：USD）");
            aR.Lines.Add($"- 匯率互換系統開關：{(iConfig.FxSystemEnabled ? "🟢 **已啟用**" : "🔴 **已停用**")}");
            aR.Lines.Add($"- 最後更新時間：`{iConfig.UpdatedAtUtc}`");
            aR.Lines.Add($"- 全域單筆成交手續費：**{iConfig.TakerFeePct * 100m:0.####}%**（改：`op=fee --arg fee_pct=<率>`）");
            aR.Lines.Add("");
            // ⚠ 點差欄位以前印 `0.##%` ⇒ 真實盤口（0.000012%）一律顯示成 **0%**，
            //   而「薄到看不見」與「沒有價差」在那個格式下逐字同形。⇒ 改印 6 位。
            aR.Lines.Add("| 券種代號 | 買入價 (Bid USD) | 賣出價 (Ask USD) | 點差 (Spread) | 手續費 | 狀態 | 資料來源 | 更新時間 |");
            aR.Lines.Add("|---|---:|---:|---:|---:|---|---|---|");

            foreach (var kvp in iConfig.Quotes)
            {
                var q = kvp.Value;
                decimal aSpread = q.Ask > 0 ? ((q.Ask - q.Bid) / q.Ask * 100m) : 0;
                string aStatus = q.IsEnabled ? "🟢 可兌換" : "⚪ 已停用";
                decimal aFee = SCP_MarketRateCache.FeePctOf(iConfig, q.Symbol);
                string aFeeStr = q.FeePct.HasValue ? $"**{aFee * 100m:0.####}%**" : $"{aFee * 100m:0.####}%";
                aR.Lines.Add($"| `{q.Symbol}` | {q.Bid:0.########} | {q.Ask:0.########} | {aSpread:0.######}% | {aFeeStr} | {aStatus} | {q.Source} | {q.UpdatedAtUtc} |");
            }

            if (iConfig.FixedPairs != null && iConfig.FixedPairs.Count > 0)
            {
                aR.Lines.Add("");
                aR.Lines.Add("# 📌 固定折算清單 (Fixed Swap Pairs)");
                foreach (var kvp in iConfig.FixedPairs)
                {
                    aR.Lines.Add($"- `{kvp.Key}`：固定折算比率 **{kvp.Value:0.########}**（免手續費）");
                }
            }

            aR.Lines.Add("");
            aR.Lines.Add("⚠ 手續費欄**粗體＝該券自己的覆寫**，非粗體＝吃全域。⛔ USD 是樞紐不是成交標的 ⇒ 永遠 0%。");

            aR.AddValue("fx_system_enabled", iConfig.FxSystemEnabled ? "1" : "0");
            aR.AddValue("quotes_count", iConfig.Quotes.Count.ToString());
            aR.AddValue("taker_fee_pct", iConfig.TakerFeePct.ToString(CultureInfo.InvariantCulture));
            return aR;
        }

        static SCP_CmdResult OpGet(SCP_MarketRateConfig iConfig, SCP_CmdArgs iArgs)
        {
            string aSymbol = iArgs.Get("symbol").Trim().ToUpperInvariant();
            string aFrom = iArgs.Get("from").Trim().ToUpperInvariant();
            string aTo = iArgs.Get("to").Trim().ToUpperInvariant();

            if (!string.IsNullOrEmpty(aFrom) && !string.IsNullOrEmpty(aTo))
            {
                var aPairRes = SCP_CmdResult.Success($"# 匯率折算查詢：`{aFrom}` ➔ `{aTo}`");
                if (!SCP_MarketRateCache.TryGetPairRate(iConfig, aFrom, aTo, out decimal aSellRate, out decimal aBuyRate, out string? aReason))
                {
                    aPairRes.Lines.Add($"🔴 **無法兌換**：{aReason}");
                    aPairRes.AddValue("can_swap", "0");
                    aPairRes.AddValue("reason", aReason ?? "unknown");
                    return aPairRes;
                }

                aPairRes.Lines.Add($"🟢 **可兌換**");

                // 🩸 毛率與淨率**一起印**。只印毛率的話，它會跟 `voucher-swap` 實際給的張數差一個手續費，
                //   而那個差額沒有任何一層會說它是手續費 —— 看起來就像其中一支算錯了。
                if (!SCP_MarketRateCache.TryGetPairRateNet(iConfig, aFrom, aTo,
                        out decimal aSellNet, out decimal aBuyNet, out decimal aFeeFactor, out string? aNetReason))
                {
                    aPairRes.Lines.Add($"🔴 **淨率算不出來**：{aNetReason}");
                    aPairRes.AddValue("can_swap", "0");
                    return aPairRes;
                }

                decimal aFeeTotalPct = (1m - aFeeFactor) * 100m;
                int aLegs = (aFrom == "USD" ? 0 : 1) + (aTo == "USD" ? 0 : 1);

                aPairRes.Lines.Add($"- 賣出 1 `{aFrom}` 可換得：**{aSellNet:0.########}** `{aTo}`　（毛率 {aSellRate:0.########}）");
                aPairRes.Lines.Add($"- 買入 1 `{aTo}` 需支付：**{aBuyNet:0.########}** `{aFrom}`　（毛率 {aBuyRate:0.########}）");
                aPairRes.Lines.Add($"- 手續費：**{aFeeTotalPct:0.####}%**（{aLegs} 筆成交 —— USD 樞紐那一腿不收）");
                aPairRes.Lines.Add($"⚠ 粗體是**淨率**（實際到手）；毛率是市場價，不含手續費。`voucher-swap` 給的是淨率。");
                aPairRes.AddValue("can_swap", "1");
                aPairRes.AddValue("sell_rate", aSellNet.ToString("0.########", CultureInfo.InvariantCulture));
                aPairRes.AddValue("buy_rate", aBuyNet.ToString("0.########", CultureInfo.InvariantCulture));
                aPairRes.AddValue("sell_rate_gross", aSellRate.ToString("0.########", CultureInfo.InvariantCulture));
                aPairRes.AddValue("buy_rate_gross", aBuyRate.ToString("0.########", CultureInfo.InvariantCulture));
                aPairRes.AddValue("fee_total_pct", aFeeTotalPct.ToString("0.######", CultureInfo.InvariantCulture));
                aPairRes.AddValue("fee_legs", aLegs.ToString(CultureInfo.InvariantCulture));
                return aPairRes;
            }

            if (string.IsNullOrEmpty(aSymbol))
                return SCP_CmdResult.Fail(2, "✗ 請提供 `--arg symbol=<券名>` 或 `--arg from=<A> --arg to=<B>`");

            if (!SCP_MarketRateCache.TryGetQuote(iConfig, aSymbol, out var aQuote) || aQuote == null)
            {
                return SCP_CmdResult.Fail(1, $"✗ 券種 '{aSymbol}' 無有效匯率快取報價，視為無法兌換");
            }

            var aR = SCP_CmdResult.Success($"# 券種報價：`{aQuote.Symbol}`");
            aR.Lines.Add($"- 買入價 (Bid)：**{aQuote.Bid}** USD");
            aR.Lines.Add($"- 賣出價 (Ask)：**{aQuote.Ask}** USD");
            aR.Lines.Add($"- 狀態：{(aQuote.IsEnabled ? "🟢 可兌換" : "⚪ 已停用")}");
            aR.Lines.Add($"- 來源：`{aQuote.Source}`");
            aR.Lines.Add($"- 更新時間：`{aQuote.UpdatedAtUtc}`");
            aR.AddValue("symbol", aQuote.Symbol);
            aR.AddValue("bid", aQuote.Bid.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("ask", aQuote.Ask.ToString(CultureInfo.InvariantCulture));
            return aR;
        }

        static SCP_CmdResult OpSet(string iDataRoot, SCP_MarketRateConfig iConfig, SCP_CmdArgs iArgs)
        {
            string aSymbol = iArgs.Get("symbol").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(aSymbol))
                return SCP_CmdResult.Fail(2, "✗ 缺 `--arg symbol=<券種代號>`");

            string aBidStr = iArgs.Get("bid").Trim();
            string aAskStr = iArgs.Get("ask").Trim();
            if (!decimal.TryParse(aBidStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal aBid) || aBid <= 0)
                return SCP_CmdResult.Fail(2, $"✗ bid 必須為大於 0 之數字（收到 '{aBidStr}'）");
            if (!decimal.TryParse(aAskStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal aAsk) || aAsk <= 0)
                return SCP_CmdResult.Fail(2, $"✗ ask 必須為大於 0 之數字（收到 '{aAskStr}'）");

            if (aAsk < aBid)
            {
                return SCP_CmdResult.Fail(2, $"✗ 賣出價 Ask ({aAsk}) 不得小於買入價 Bid ({aBid})，避免無成本倒掛套利");
            }

            bool aEnabled = iArgs.Get("enabled").Trim() != "0";
            string aSource = iArgs.Get("source").Trim();
            if (string.IsNullOrEmpty(aSource)) aSource = "manual";

            // ⛔ 留空 ＝ **不覆寫**（吃全域），不是 0。
            //   兩者在算式裡差一整筆手續費，而在指令列上只差「有沒有打那個參數」。
            decimal? aFeeOverride = null;
            string aFeeStr = iArgs.Get("fee_pct").Trim();
            if (aFeeStr.Length > 0)
            {
                if (!decimal.TryParse(aFeeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal aFee))
                    return SCP_CmdResult.Fail(2, $"✗ fee_pct 必須是數字（收到 '{aFeeStr}'）");
                if (aFee < 0m)
                    return SCP_CmdResult.Fail(2, $"✗ fee_pct 不得為負（收到 {aFee}）—— 負手續費＝憑空生錢");
                if (aFee >= FeeSanityCap)
                    return SCP_CmdResult.Fail(2, $"✗ fee_pct 必須 < {FeeSanityCap}（＝{FeeSanityCap * 100m:0.#}%），收到 {aFee}（＝{aFee * 100m:0.####}%）。⚠ 手滑最常見的是**漏掉百分比那一格**：0.5% ＝ `0.005`、0.1% ＝ `0.001`。現實中沒有任何交易所收到 {FeeSanityCap * 100m:0.#}%");
                aFeeOverride = aFee;
            }

            var aQuote = new SCP_RateQuote
            {
                Symbol = aSymbol,
                BaseCurrency = "USD",
                Bid = aBid,
                Ask = aAsk,
                IsEnabled = aEnabled,
                Source = aSource,
                FeePct = aFeeOverride,
                UpdatedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            };

            // 🩸 沿用既有的抓取端點（TASK-0371）：手填只是改價，⛔ 不是「改成純手填」——
            //   以前這裡整個換新物件，端點跟著消失 ⇒ 那個幣從此 `op=sync` 都跳過，而沒有任何一層會說。
            if (iConfig.Quotes.TryGetValue(aSymbol, out var aPrevQuote))
            {
                aQuote.SourceUrl = aPrevQuote.SourceUrl;
                aQuote.SourceKind = aPrevQuote.SourceKind;
                aQuote.TwoSided = aPrevQuote.TwoSided;
            }
            iConfig.Quotes[aSymbol] = aQuote;

            if (!SCP_MarketRateCache.Save(iDataRoot, iConfig, out string? aSaveErr))
                return SCP_CmdResult.Fail(1, $"✗ 匯率快取落盤失敗：{aSaveErr}");

            var aR = SCP_CmdResult.Success($"✅ 券種 `{aSymbol}` 報價已更新並落盤");
            aR.Lines.Add($"- 買入價 (Bid)：{aQuote.Bid} USD");
            aR.Lines.Add($"- 賣出價 (Ask)：{aQuote.Ask} USD");
            aR.Lines.Add($"- 狀態：{(aQuote.IsEnabled ? "🟢 可兌換" : "⚪ 已停用")}");
            aR.Lines.Add($"- 來源：`{aQuote.Source}`");
            aR.Lines.Add(aQuote.FeePct.HasValue
                ? $"- 手續費：**{aQuote.FeePct.Value * 100m:0.####}%**（該券覆寫）"
                : $"- 手續費：{iConfig.TakerFeePct * 100m:0.####}%（吃全域，**未覆寫**）");
            aR.AddValue("symbol", aSymbol);
            aR.AddValue("bid", aQuote.Bid.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("ask", aQuote.Ask.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("fee_pct", SCP_MarketRateCache.FeePctOf(iConfig, aSymbol).ToString(CultureInfo.InvariantCulture));
            aR.AddValue("fee_is_override", aQuote.FeePct.HasValue ? "1" : "0");
            return aR;
        }

        /// <summary>
        /// 設定全域單筆成交手續費率。
        /// 🩸 上限刻意擋在 50%：一個打錯小數點的 0.5（＝50%）跟 0.005 在畫面上差一個字，
        /// 而它的失效樣子是「兌換突然只剩一半」，看起來像匯率崩了，不像手滑。
        /// </summary>
        static SCP_CmdResult OpFee(string iDataRoot, SCP_MarketRateConfig iConfig, SCP_CmdArgs iArgs)
        {
            string aFeeStr = iArgs.Get("fee_pct").Trim();
            if (string.IsNullOrEmpty(aFeeStr))
                return SCP_CmdResult.Fail(2, $"✗ 缺 `--arg fee_pct=<費率>`（0.001 ＝ 0.1%）。目前全域費率＝{iConfig.TakerFeePct:0.######}");

            if (!decimal.TryParse(aFeeStr, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal aFee))
                return SCP_CmdResult.Fail(2, $"✗ fee_pct 必須是數字（收到 '{aFeeStr}'）");
            if (aFee < 0m)
                return SCP_CmdResult.Fail(2, $"✗ fee_pct 不得為負（收到 {aFee}）—— 負手續費＝憑空生錢");
            if (aFee >= FeeSanityCap)
                return SCP_CmdResult.Fail(2, $"✗ fee_pct 必須 < {FeeSanityCap}（＝{FeeSanityCap * 100m:0.#}%），收到 {aFee}（＝{aFee * 100m:0.####}%）。⚠ 手滑最常見的是**漏掉百分比那一格**：0.5% ＝ `0.005`、0.1% ＝ `0.001`。現實中沒有任何交易所收到 {FeeSanityCap * 100m:0.#}%");

            decimal aOld = iConfig.TakerFeePct;
            iConfig.TakerFeePct = aFee;

            if (!SCP_MarketRateCache.Save(iDataRoot, iConfig, out string? aSaveErr))
            {
                iConfig.TakerFeePct = aOld;   // 🩸 沒落盤就不算改過 —— 記憶體與磁碟不得分岔
                return SCP_CmdResult.Fail(1, $"✗ 匯率快取落盤失敗（費率未變更）：{aSaveErr}");
            }

            var aR = SCP_CmdResult.Success($"✅ 全域單筆成交手續費率：{aOld:0.######} ➔ **{aFee:0.######}**（{aFee * 100m:0.####}%）");
            aR.Lines.Add($"- 一端是 USD 的兌換（只有一筆成交）：收 **{aFee * 100m:0.####}%**");
            aR.Lines.Add($"- 兩端都不是 USD（A→USD→B 兩筆成交）：合計收 **{(1m - (1m - aFee) * (1m - aFee)) * 100m:0.####}%**");
            aR.Lines.Add($"- 個別券種要用別的費率 ⇒ `op=set --arg symbol=<券> --arg fee_pct=<率>`");
            aR.AddValue("taker_fee_pct", aFee.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("prev_taker_fee_pct", aOld.ToString(CultureInfo.InvariantCulture));
            return aR;
        }

        /// <summary>設定某券的抓取端點。⛔ 只寫設定，**不順便抓** —— 設定與取得是兩件事，混在一起會讓「設定存好了沒」被網路狀況綁架。</summary>
        static SCP_CmdResult OpSource(string iDataRoot, SCP_MarketRateConfig iConfig, SCP_CmdArgs iArgs)
        {
            string aSymbol = iArgs.Get("symbol").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(aSymbol))
                return SCP_CmdResult.Fail(2, "✗ 缺 `--arg symbol=<券種代號>`");
            if (aSymbol == "USD")
                return SCP_CmdResult.Fail(2, "✗ USD 是基準幣本身（永遠 1:1），沒有端點可設");

            if (!iConfig.Quotes.TryGetValue(aSymbol, out var aQ))
                return SCP_CmdResult.Fail(1, $"✗ 券種 '{aSymbol}' 還不在快取裡 —— 先 `op=set` 建立報價再設端點");

            string aUrl = iArgs.Get("url").Trim();
            string aKind = iArgs.Get("kind").Trim().ToLowerInvariant();

            // 留空兩個 ⇒ 清掉端點（退回純手填）。這是顯式動作，所以要說清楚。
            if (aUrl.Length == 0 && aKind.Length == 0)
            {
                aQ.SourceUrl = "";
                aQ.SourceKind = "";
                if (!SCP_MarketRateCache.Save(iDataRoot, iConfig, out string? aClrErr))
                    return SCP_CmdResult.Fail(1, $"✗ 落盤失敗：{aClrErr}");
                var aClr = SCP_CmdResult.Success($"✅ `{aSymbol}` 的抓取端點已清除 ⇒ 退回**純手填**（`op=sync` 會跳過它並說出來）");
                aClr.AddValue("symbol", aSymbol);
                aClr.AddValue("source_url", "");
                return aClr;
            }

            if (aUrl.Length == 0) return SCP_CmdResult.Fail(2, "✗ 缺 `--arg url=<端點>`（要清除請 url 與 kind 都留空）");
            if (!aUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return SCP_CmdResult.Fail(2, $"✗ 端點必須是 https（收到 '{aUrl}'）—— 明文抓來的價格會被中間人改掉，而改掉的價格長得跟真的一樣");
            if (!SCP_RateSourceKind.IsKnown(aKind))
                return SCP_CmdResult.Fail(2, $"✗ 認不得的 kind='{aKind}'（可用：{string.Join(" / ", SCP_RateSourceKind.All)}）");

            aQ.SourceUrl = aUrl;
            aQ.SourceKind = aKind;

            if (!SCP_MarketRateCache.Save(iDataRoot, iConfig, out string? aSaveErr))
                return SCP_CmdResult.Fail(1, $"✗ 落盤失敗：{aSaveErr}");

            var aR = SCP_CmdResult.Success($"✅ `{aSymbol}` 抓取端點已設定");
            aR.Lines.Add($"- 端點：`{aUrl}`");
            aR.Lines.Add($"- 解析器：`{aKind}`");
            aR.Lines.Add("⛔ 本指令**只存設定、沒有去抓** —— 要抓請跑 `op=sync`。");
            aR.AddValue("symbol", aSymbol);
            aR.AddValue("source_url", aUrl);
            aR.AddValue("source_kind", aKind);
            return aR;
        }

        /// <summary>
        /// 從已設定的端點刷新報價。
        /// 🩸 最關鍵的一格：**任何一個券抓失敗，就原封不動保留它的舊值**，
        /// 並在讀數裡逐一列出失敗原因。⛔ 絕不用 0 或上一輪的殘值去覆蓋 ——
        /// 「抓不到」與「抓到了但沒變」在快取檔上逐字同形，而覆蓋會讓前者偽裝成後者。
        /// </summary>
        static SCP_CmdResult OpSync(string iDataRoot, SCP_MarketRateConfig iConfig, SCP_CmdArgs iArgs)
        {
            if (!SCP_HttpFetch.IsAvailable)
            {
                return SCP_CmdResult.Fail(3,
                    "✗ **本宿主未註冊抓取器** ⇒ 抓不了。\n"
                    + "  · 這不是「沒有新報價」，是這個宿主沒有網路出口（SCP_Core 刻意不引入 HTTP，見 SCP_RateSource.cs 守衛①）。\n"
                    + "  · Senate CLI／Server 會註冊；Unity Editor 端不會 ⇒ 請從 Senate 那側跑。");
            }

            var aFetcher = SCP_HttpFetch.Current!;
            bool aForce = iArgs.Get("force").Trim() == "1";

            // `day=` ⇒ **每日一版**模式（TASK-0272 ②；呼叫端是 `demurrage op=run` 發完券之後那一步）。
            //   那一天已經有 `origin=sync` 的版本 ⇒ 不抓、明說；沒有 ⇒ 無視 TTL 抓一次。
            //   ⛔ 不靠 TTL 判：每天扣繳的時刻會漂，早 2 分鐘跑就差 2 分鐘才過期 ⇒ **整天沒有版本**，而那不會叫。
            string aDayStr = iArgs.Get("day").Trim();
            if (aDayStr.Length > 0)
            {
                if (!DateTime.TryParseExact(aDayStr, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime aDay))
                    return SCP_CmdResult.Fail(2, $"✗ day 要 `yyyy-MM-dd`（UTC 日），收到 '{aDayStr}'");
                // 🩸 實測 2026-09-28：給明天的日期 ⇒ 今天抓的版本永遠落不到「明天或之後」⇒ **每跑一次就多抓一版**，
                //   而每一次都回「已更新」。⇒ 還沒到的日子沒有「那天的版本」可言，直接拒絕。
                if (aDay.Date > DateTime.UtcNow.Date)
                    return SCP_CmdResult.Fail(2, $"✗ day={aDayStr} 還沒到（現在 UTC 是 {DateTime.UtcNow:yyyy-MM-dd}）—— 那一天的版本要等那一天才抓得到");
                if (SCP_RateHistory.HasSyncVersionOnOrAfter(iDataRoot, aDay, out string? aHave, out string? aDayErr))
                {
                    var aAlready = SCP_CmdResult.Success($"✅ `{aDayStr}`（UTC）已經有匯率版本 `{aHave}` ⇒ **不再刷新**（一天一版）");
                    aAlready.AddValue("day_state", "already");
                    aAlready.AddValue("history_version", aHave ?? "");
                    aAlready.AddValue("updated", "0");
                    return aAlready;
                }
                if (aDayErr != null)
                    return SCP_CmdResult.Fail(1, $"✗ 判不出 `{aDayStr}` 有沒有版本（{aDayErr}）⇒ **不刷新**（判不出來 ≠ 沒有）");
                aForce = true;
            }
            string aOnly = iArgs.Get("symbol").Trim().ToUpperInvariant();
            if (!int.TryParse(iArgs.Get("timeout_sec").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aTimeout) || aTimeout <= 0)
                aTimeout = 12;

            DateTime aNow = DateTime.UtcNow;
            // 刷新前的快取原樣留一份 —— 下面的迴圈會就地改 iConfig，而歸檔要的是「改之前」那份。
            SCP_MarketRateConfig aBefore = SCP_MarketRateConfig.FromJson(iConfig.ToJson());
            var aUpdated = new List<string>();
            var aFailed = new List<string>();
            var aSkipped = new List<string>();

            foreach (var kvp in new List<KeyValuePair<string, SCP_RateQuote>>(iConfig.Quotes))
            {
                var q = kvp.Value;
                if (q.Symbol == "USD") continue;
                if (aOnly.Length > 0 && !string.Equals(q.Symbol, aOnly, StringComparison.OrdinalIgnoreCase)) continue;

                if (string.IsNullOrEmpty(q.SourceUrl) || string.IsNullOrEmpty(q.SourceKind))
                {
                    aSkipped.Add($"`{q.Symbol}`：**純手填**（沒設端點）—— `op=source` 可以給它一個");
                    continue;
                }

                bool aStale = SCP_MarketRateCache.IsQuoteStale(iConfig, q, aNow, out double aAge);
                if (!aForce && !aStale)
                {
                    aSkipped.Add($"`{q.Symbol}`：還沒過期（{aAge:0} 分 / TTL {iConfig.SyncTtlMinutes} 分）—— `--arg force=1` 可無視");
                    continue;
                }

                if (!aFetcher.TryGetText(q.SourceUrl, aTimeout, out string aBody, out string? aFetchErr))
                {
                    // 🩸 抓失敗 ⇒ 舊值**一個 byte 都不動**。
                    aFailed.Add($"`{q.Symbol}`：抓取失敗（舊值保留）—— {aFetchErr}");
                    continue;
                }

                var aRes = SCP_RateSourceParser.Parse(q.Symbol, q.SourceKind, aBody);
                if (!aRes.Ok)
                {
                    aFailed.Add($"`{q.Symbol}`：解析失敗（舊值保留）—— {aRes.Error}");
                    continue;
                }

                decimal aOldBid = q.Bid, aOldAsk = q.Ask;
                q.Bid = aRes.Bid;
                q.Ask = aRes.Ask;
                q.TwoSided = aRes.TwoSided;
                q.Source = aFetcher.FetcherName + ":" + q.SourceKind;
                q.UpdatedAtUtc = aNow.ToString("o", CultureInfo.InvariantCulture);

                string aTwo = aRes.TwoSided ? "" : "　⚠ 單向報價（Bid＝Ask，來源只給中間價）";
                aUpdated.Add($"`{q.Symbol}`：{aOldBid:0.########}/{aOldAsk:0.########} ➔ **{q.Bid:0.########}/{q.Ask:0.########}**{aTwo}");
            }

            // 區塊職責：歷史版本（驗收⑨⑫）—— **歷史先寫、快取後寫**，任何一格失敗就整趟不寫快取。
            // ⚠ 沒有任何一筆更新 ⇒ 這整段不跑 ⇒ 歷史資料夾一個新檔都不出現（抓不到／TTL 跳過都走這條）。
            string? aPreArchivedId = null;
            string aNewVersionId = "";
            if (aUpdated.Count > 0)
            {
                // ③ 刷新前的快取若跟最新一版歷史不同（中間有人手填過）⇒ 先把它原樣歸成一版。
                if (SCP_RateHistory.NeedsPreSyncArchive(iDataRoot, aBefore, out DateTime aPreStamp, out string? aPreErr))
                {
                    if (!SCP_RateHistory.TryWriteVersion(iDataRoot, aBefore, aPreStamp, "pre_sync", out _, out string? aPreWriteErr))
                        return SCP_CmdResult.Fail(1, $"✗ 刷新前歸檔失敗 ⇒ **整趟不寫**（快取與歷史都未變更）：{aPreWriteErr}");
                    aPreArchivedId = SCP_RateHistory.VersionIdOf(aPreStamp);
                }
                else if (aPreErr != null)
                {
                    return SCP_CmdResult.Fail(1, $"✗ {aPreErr} ⇒ **整趟不寫**（快取與歷史都未變更）");
                }

                if (!SCP_RateHistory.TryWriteVersion(iDataRoot, iConfig, aNow, "sync", out string aNewPath, out string? aHistErr))
                    return SCP_CmdResult.Fail(1, $"✗ 新版本寫進歷史失敗 ⇒ **整趟不寫快取**（⛔ 不接受「歷史沒存到但新價寫進去了」）：{aHistErr}");
                aNewVersionId = SCP_RateHistory.VersionIdOf(aNow);

                if (!SCP_MarketRateCache.Save(iDataRoot, iConfig, aNow, out string? aSaveErr))
                {
                    // 快取沒存進去 ⇒ 撤回剛寫的那一版，讓「最新一版歷史」與「現在的快取」保持同一份。
                    bool aRetracted = SCP_RateHistory.TryRetractVersion(aNewPath, out string? aRetractErr);
                    return SCP_CmdResult.Fail(1, $"✗ 抓到了但快取落盤失敗（快取未變更）：{aSaveErr}"
                        + (aRetracted ? "；剛寫的歷史版本已撤回" : $"；⚠ 剛寫的歷史版本**撤不回**（{aRetractErr}）⇒ 歷史比快取多一版：{aNewPath}"));
                }
            }

            var aR = aFailed.Count > 0
                ? SCP_CmdResult.Success($"⚠ 匯率同步完成 —— 更新 {aUpdated.Count} 筆，**失敗 {aFailed.Count} 筆**，跳過 {aSkipped.Count} 筆")
                : SCP_CmdResult.Success($"✅ 匯率同步完成 —— 更新 {aUpdated.Count} 筆，跳過 {aSkipped.Count} 筆");

            aR.Lines.Add($"- 抓取器：`{aFetcher.FetcherName}`　逾時 {aTimeout}s　TTL {iConfig.SyncTtlMinutes} 分{(aForce ? "（**force：無視 TTL**）" : "")}");
            if (aUpdated.Count > 0) { aR.Lines.Add(""); aR.Lines.Add("**已更新**"); foreach (string s in aUpdated) aR.Lines.Add("- " + s); }
            if (aFailed.Count > 0) { aR.Lines.Add(""); aR.Lines.Add("**失敗（舊值原封保留，⛔ 沒有被 0 或殘值蓋掉）**"); foreach (string s in aFailed) aR.Lines.Add("- " + s); }
            if (aSkipped.Count > 0) { aR.Lines.Add(""); aR.Lines.Add("**跳過**"); foreach (string s in aSkipped) aR.Lines.Add("- " + s); }

            aR.Lines.Add("");
            if (aNewVersionId.Length > 0)
            {
                aR.Lines.Add($"**歷史**：新版本 `{aNewVersionId}` 已寫入 `Market/history/`（先於快取落盤）");
                if (aPreArchivedId != null)
                    aR.Lines.Add($"- 刷新前的快取跟最新一版歷史不同（中間有手填）⇒ 先歸成一版 `{aPreArchivedId}`（origin=pre_sync）");
            }
            else
            {
                aR.Lines.Add("**歷史**：沒有任何一筆更新 ⇒ **不寫新版本**（「沒有新報價」不會在歷史上長得像「有新報價但沒變」）");
            }

            aR.AddValue("day_state", aDayStr.Length == 0 ? "" : aNewVersionId.Length > 0 ? "synced" : "failed");
            aR.AddValue("history_version", aNewVersionId);
            aR.AddValue("history_pre_sync_version", aPreArchivedId ?? "");
            aR.AddValue("updated", aUpdated.Count.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("failed", aFailed.Count.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("skipped", aSkipped.Count.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("fetcher", aFetcher.FetcherName);
            return aR;
        }

        /// <summary>
        /// 歷史匯率（驗收⑩⑪）。三種用法：
        /// `version=` 讀回單一版本的完整報價表／`symbol=` 查走勢／都不給 ⇒ 列出全部版本。
        /// 🩸 區間內沒有資料 ⇒ **明說無歷史資料並 exit 1**，⛔ 不回 0、空表或最近一筆 ——
        ///   那三種都長得像「查到了」。
        /// </summary>
        static SCP_CmdResult OpHistory(string iDataRoot, SCP_CmdArgs iArgs)
        {
            string aVersion = iArgs.Get("version").Trim();
            string aSymbol = iArgs.Get("symbol").Trim().ToUpperInvariant();

            if (aVersion.Length > 0)
            {
                string aPath = SCP_RateHistory.VersionPath(iDataRoot, aVersion);
                if (!File.Exists(aPath))
                    return SCP_CmdResult.Fail(1, $"✗ 找不到歷史版本 `{aVersion}`（{aPath}）—— 不給 version 可列出全部版本");
                if (!SCP_RateHistory.TryReadVersion(aPath, out var v, out string? aErr))
                    return SCP_CmdResult.Fail(1, $"✗ 歷史版本 `{aVersion}` 讀不了：{aErr}");

                var aV = SCP_CmdResult.Success($"# 歷史版本 `{v.VersionId}`（完整報價表）");
                aV.Lines.Add($"- 抓取時間：`{v.FetchedAtUtc:o}`　歸檔時間：`{v.ArchivedAtUtc}`　來由：`{v.Origin}`");
                aV.Lines.Add($"- 當時全域手續費：**{v.Config.TakerFeePct * 100m:0.####}%**／筆　互換系統：{(v.Config.FxSystemEnabled ? "啟用" : "停用")}");
                aV.Lines.Add("");
                aV.Lines.Add("| 券種 | Bid (USD) | Ask (USD) | 手續費 | 雙向盤口 | 來源 | 端點 | 該券更新時間 |");
                aV.Lines.Add("|---|---:|---:|---:|---|---|---|---|");
                foreach (var q in v.Config.Quotes.Values)
                {
                    decimal aFee = SCP_MarketRateCache.FeePctOf(v.Config, q.Symbol);
                    aV.Lines.Add($"| `{q.Symbol}` | {q.Bid:0.########} | {q.Ask:0.########} | {aFee * 100m:0.####}%{(q.FeePct.HasValue ? "（覆寫）" : "")} | {(q.TwoSided ? "是" : "否（中間價）")} | {q.Source} | {(q.SourceUrl.Length > 0 ? q.SourceUrl : "—")} | {q.UpdatedAtUtc} |");
                }
                aV.AddValue("version", v.VersionId);
                aV.AddValue("quotes_count", v.Config.Quotes.Count.ToString(CultureInfo.InvariantCulture));
                return aV;
            }

            if (aSymbol.Length == 0)
            {
                List<string> aFiles = SCP_RateHistory.ListVersionFiles(iDataRoot);
                if (aFiles.Count == 0)
                    return SCP_CmdResult.Fail(1, "✗ **無歷史資料**：歷史資料夾是空的（還沒有任何一次成功的刷新）。");

                var aL = SCP_CmdResult.Success($"# 歷史匯率版本（共 {aFiles.Count} 版，一版一檔）");
                aL.Lines.Add("| 版本代號 | 抓取時間 (UTC) | 來由 | 券種數 | 全域手續費 |");
                aL.Lines.Add("|---|---|---|---:|---:|");
                int aBad = 0;
                foreach (string f in aFiles)
                {
                    if (!SCP_RateHistory.TryReadVersion(f, out var v, out string? aErr))
                    {
                        aBad++;
                        aL.Lines.Add($"| `{Path.GetFileName(f)}` | ⚠ **讀不了**：{aErr} | | | |");
                        continue;
                    }
                    aL.Lines.Add($"| `{v.VersionId}` | {v.FetchedAtUtc:yyyy-MM-dd HH:mm:ss} | {v.Origin} | {v.Config.Quotes.Count} | {v.Config.TakerFeePct * 100m:0.####}% |");
                }
                aL.Lines.Add("");
                aL.Lines.Add("・走勢：`op=history --arg symbol=<券>`；單一版本全表：`op=history --arg version=<版本代號>`");
                aL.AddValue("versions", aFiles.Count.ToString(CultureInfo.InvariantCulture));
                aL.AddValue("unreadable", aBad.ToString(CultureInfo.InvariantCulture));
                return aL;
            }

            if (!SCP_RateHistory.TryParseBound(iArgs.Get("since"), false, out DateTime? aSince, out string? aSinceErr))
                return SCP_CmdResult.Fail(2, "✗ since " + aSinceErr);
            if (!SCP_RateHistory.TryParseBound(iArgs.Get("until"), true, out DateTime? aUntil, out string? aUntilErr))
                return SCP_CmdResult.Fail(2, "✗ until " + aUntilErr);
            if (aSince.HasValue && aUntil.HasValue && aSince.Value > aUntil.Value)
                return SCP_CmdResult.Fail(2, $"✗ since 晚於 until（{aSince.Value:o} > {aUntil.Value:o}）");

            if (aSymbol == "USD")
                return SCP_CmdResult.Fail(2, "✗ USD 是基準幣本身（永遠 1:1），沒有走勢 —— 請查其他券");

            SCP_RateHistorySeries aS = SCP_RateHistory.BuildSeries(iDataRoot, aSymbol, aSince, aUntil);
            string? aEmpty = SCP_RateHistory.DescribeEmpty(aS);
            if (aEmpty != null)
            {
                var aE = SCP_CmdResult.Fail(1, "✗ " + aEmpty);
                foreach (string u in aS.Unreadable) aE.Lines.Add("  · ⚠ 讀不了的歷史檔：" + u);
                return aE;
            }

            string aRange = (aSince.HasValue ? aSince.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "最早")
                          + " ～ " + (aUntil.HasValue ? aUntil.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "最新");
            var aR = SCP_CmdResult.Success($"# `{aSymbol}` 歷史走勢（USD 計價，{aRange}，{aS.Points.Count} 版）");

            var aMids = new List<double>(aS.Points.Count);
            foreach (var p in aS.Points) aMids.Add((double)p.Mid);
            aR.Lines.Add($"走勢　`{SCP.Core.Gui.SCP_GuiSparkline.Render(aMids, 48)}`");
            aR.Lines.Add("");
            aR.Lines.Add($"- 首版中間價：**{aS.FirstMid:0.########}**　末版：**{aS.LastMid:0.########}**");
            aR.Lines.Add(aS.ChangePct.HasValue
                ? $"- 區間變動：**{aS.ChangePct.Value:+0.####;-0.####;0}%**"
                : "- 區間變動：—（只有 1 版，沒有「變動」可言）");
            aR.Lines.Add($"- 區間最低／最高：{aS.MinMid:0.########} ／ {aS.MaxMid:0.########}");
            double? aVol = aS.VolatilityPct;
            aR.Lines.Add(aVol.HasValue
                ? $"- 波動度：**{aVol.Value:0.####}%**（相鄰兩版對數報酬的標準差，**每版**，未年化）"
                : "- 波動度：—（少於 3 版，算不出報酬的標準差）");
            if (aS.GapDays > 0)
                aR.Lines.Add($"- ⚠ 期間有 **{aS.GapDays} 天沒有版本**（那幾天沒抓，不是「沒有變」）");
            aR.Lines.Add("");
            aR.Lines.Add("| 版本代號 | 抓取時間（當地） | Bid | Ask | 中間價 | 較上一版 | 手續費 | 來源 | 來由 |");
            aR.Lines.Add("|---|---|---:|---:|---:|---:|---:|---|---|");
            decimal? aPrev = null;
            foreach (var p in aS.Points)
            {
                string aChg = aPrev.HasValue && aPrev.Value > 0m ? $"{(p.Mid - aPrev.Value) / aPrev.Value * 100m:+0.####;-0.####;0}%" : "—";
                aR.Lines.Add($"| `{p.VersionId}` | {p.FetchedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm} | {p.Bid:0.########} | {p.Ask:0.########} | {p.Mid:0.########} | {aChg} | {p.FeePct * 100m:0.####}% | {p.Source} | {p.Origin} |");
                aPrev = p.Mid;
            }
            foreach (string u in aS.Unreadable) aR.Lines.Add("⚠ 讀不了的歷史檔（未列入走勢）：" + u);

            aR.AddValue("symbol", aSymbol);
            aR.AddValue("points", aS.Points.Count.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("first_mid", aS.FirstMid.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("last_mid", aS.LastMid.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("change_pct", aS.ChangePct.HasValue ? aS.ChangePct.Value.ToString("0.########", CultureInfo.InvariantCulture) : "");
            aR.AddValue("volatility_pct", aVol.HasValue ? aVol.Value.ToString("0.########", CultureInfo.InvariantCulture) : "");
            aR.AddValue("gap_days", aS.GapDays.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("unreadable", aS.Unreadable.Count.ToString(CultureInfo.InvariantCulture));
            return aR;
        }

        static SCP_CmdResult OpToggle(string iDataRoot, SCP_MarketRateConfig iConfig, SCP_CmdArgs iArgs)
        {
            string aEnabledStr = iArgs.Get("enabled").Trim();
            bool aEnabled = aEnabledStr != "0";
            iConfig.FxSystemEnabled = aEnabled;

            if (!SCP_MarketRateCache.Save(iDataRoot, iConfig, out string? aSaveErr))
                return SCP_CmdResult.Fail(1, $"✗ 匯率快取落盤失敗：{aSaveErr}");

            var aR = SCP_CmdResult.Success($"✅ 匯率互換系統已設定為：{(aEnabled ? "🟢 啟用" : "🔴 停用")}");
            aR.AddValue("fx_system_enabled", aEnabled ? "1" : "0");
            return aR;
        }

        static string ResolveDataRoot(string iGiven)
        {
            if (!string.IsNullOrWhiteSpace(iGiven)) return iGiven.Trim().Replace('\\', '/');
            string aCandidate = "D:/Unity/Bar/AgentCommands";
            if (Directory.Exists(aCandidate)) return aCandidate;
            return Directory.GetCurrentDirectory().Replace('\\', '/');
        }
    }
}
