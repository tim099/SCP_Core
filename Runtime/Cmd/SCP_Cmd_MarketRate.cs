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
            + "⚠ 無緩存資訊之券種一律視為無法兌換（並非所有券都能互相兌換，Tim 2026-09-22 拍板）。\n"
            + "⚠ 成本在**手續費**不在人造價差（Tim 2026-09-23）：真實盤口極薄（實測往返 2.42 ppm ≒ 免費），\n"
            + "  而現實的成本是每筆成交的手續費。⇒ **按腿計**：A→USD→B 兩筆成交收兩次；一端是 USD 只收一次。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("rate --arg data_root=<AgentCommands> --arg op=list");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "資料根目錄（絕對路徑）。省略時自動嘗試推導", iDefault: ""),
            new SCP_CmdArgSpec("op", "做什麼（list|get|set|toggle|fee|source|sync，預設 list）", iDefault: "list",
                iChoices: new[] { "list", "get", "set", "toggle", "fee", "source", "sync" }),
            new SCP_CmdArgSpec("symbol", "券種代號（如 BTC, GOLD, USD）", iDefault: ""),
            new SCP_CmdArgSpec("from", "來源券種代號（op=get 查匯率對時使用）", iDefault: ""),
            new SCP_CmdArgSpec("to", "目標券種代號（op=get 查匯率對時使用）", iDefault: ""),
            new SCP_CmdArgSpec("bid", "買入價（賣出 1 單位資產可得之 USD 單價，正數）", iDefault: ""),
            new SCP_CmdArgSpec("ask", "賣出價（買入 1 單位資產需支付之 USD 單價，正數）", iDefault: ""),
            new SCP_CmdArgSpec("enabled", "是否啟用（1=啟用, 0=停用）", iDefault: "1"),
            new SCP_CmdArgSpec("source", "資料來源標記（預設 manual）", iDefault: "manual"),
            new SCP_CmdArgSpec("fee_pct", "手續費率（0.001 ＝ 0.1%）。op=fee 設全域；op=set 設該券覆寫 —— ⛔ **留空＝不覆寫（吃全域）**，不是 0", iDefault: ""),
            new SCP_CmdArgSpec("url", "op=source：抓取端點完整網址", iDefault: ""),
            new SCP_CmdArgSpec("kind", "op=source：回應解析器（binance_bookticker｜mid_price_json）", iDefault: ""),
            new SCP_CmdArgSpec("force", "op=sync：1＝無視 TTL 一律重抓（預設只抓過期的）", iDefault: "0"),
            new SCP_CmdArgSpec("timeout_sec", "op=sync：單一端點逾時秒數", iDefault: "12"),
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
                _ => SCP_CmdResult.Fail(2, $"✗ 認不得的 op='{aOp}'（list|get|set|toggle|fee|source|sync）"),
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
            string aOnly = iArgs.Get("symbol").Trim().ToUpperInvariant();
            if (!int.TryParse(iArgs.Get("timeout_sec").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aTimeout) || aTimeout <= 0)
                aTimeout = 12;

            DateTime aNow = DateTime.UtcNow;
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

            if (aUpdated.Count > 0 && !SCP_MarketRateCache.Save(iDataRoot, iConfig, out string? aSaveErr))
                return SCP_CmdResult.Fail(1, $"✗ 抓到了但落盤失敗（磁碟未變更）：{aSaveErr}");

            var aR = aFailed.Count > 0
                ? SCP_CmdResult.Success($"⚠ 匯率同步完成 —— 更新 {aUpdated.Count} 筆，**失敗 {aFailed.Count} 筆**，跳過 {aSkipped.Count} 筆")
                : SCP_CmdResult.Success($"✅ 匯率同步完成 —— 更新 {aUpdated.Count} 筆，跳過 {aSkipped.Count} 筆");

            aR.Lines.Add($"- 抓取器：`{aFetcher.FetcherName}`　逾時 {aTimeout}s　TTL {iConfig.SyncTtlMinutes} 分{(aForce ? "（**force：無視 TTL**）" : "")}");
            if (aUpdated.Count > 0) { aR.Lines.Add(""); aR.Lines.Add("**已更新**"); foreach (string s in aUpdated) aR.Lines.Add("- " + s); }
            if (aFailed.Count > 0) { aR.Lines.Add(""); aR.Lines.Add("**失敗（舊值原封保留，⛔ 沒有被 0 或殘值蓋掉）**"); foreach (string s in aFailed) aR.Lines.Add("- " + s); }
            if (aSkipped.Count > 0) { aR.Lines.Add(""); aR.Lines.Add("**跳過**"); foreach (string s in aSkipped) aR.Lines.Add("- " + s); }

            aR.AddValue("updated", aUpdated.Count.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("failed", aFailed.Count.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("skipped", aSkipped.Count.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("fetcher", aFetcher.FetcherName);
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
