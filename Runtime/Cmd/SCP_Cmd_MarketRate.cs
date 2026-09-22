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

        public override string Summary =>
            "匯率快取管理：查詢各券種報價、手填 Bid/Ask 價格、切換互換開關";

        public override string Details =>
            "· `op=list`（預設）：列出快取中所有券種相對於 USD 之買入價 (Bid) 與賣出價 (Ask)。\n"
            + "· `op=get`：查詢特定券種報價（`--arg symbol=<券名>`）或兩券折算比率（`--arg from=<A> --arg to=<B>`）。\n"
            + "· `op=set`：手填新增或更新特定券種相對於 USD 之報價（`--arg symbol=<券名> --arg bid=<買價> --arg ask=<賣價>`）。\n"
            + "· `op=toggle`：切換全域匯率互換系統開關（`--arg enabled=1/0`）。\n"
            + "⚠ 無緩存資訊之券種一律視為無法兌換（並非所有券都能互相兌換，Tim 2026-09-22 拍板）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("rate --arg data_root=<AgentCommands> --arg op=list");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "資料根目錄（絕對路徑）。省略時自動嘗試推導", iDefault: ""),
            new SCP_CmdArgSpec("op", "做什麼（list|get|set|toggle，預設 list）", iDefault: "list",
                iChoices: new[] { "list", "get", "set", "toggle" }),
            new SCP_CmdArgSpec("symbol", "券種代號（如 BTC, GOLD, USD）", iDefault: ""),
            new SCP_CmdArgSpec("from", "來源券種代號（op=get 查匯率對時使用）", iDefault: ""),
            new SCP_CmdArgSpec("to", "目標券種代號（op=get 查匯率對時使用）", iDefault: ""),
            new SCP_CmdArgSpec("bid", "買入價（賣出 1 單位資產可得之 USD 單價，正數）", iDefault: ""),
            new SCP_CmdArgSpec("ask", "賣出價（買入 1 單位資產需支付之 USD 單價，正數）", iDefault: ""),
            new SCP_CmdArgSpec("enabled", "是否啟用（1=啟用, 0=停用）", iDefault: "1"),
            new SCP_CmdArgSpec("source", "資料來源標記（預設 manual）", iDefault: "manual"),
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
                _ => SCP_CmdResult.Fail(2, $"✗ 認不得的 op='{aOp}'（list|get|set|toggle）"),
            };
        }

        static SCP_CmdResult OpList(SCP_MarketRateConfig iConfig)
        {
            var aR = SCP_CmdResult.Success("# 匯率快取報價清單（基準幣：USD）");
            aR.Lines.Add($"- 匯率互換系統開關：{(iConfig.FxSystemEnabled ? "🟢 **已啟用**" : "🔴 **已停用**")}");
            aR.Lines.Add($"- 最後更新時間：`{iConfig.UpdatedAtUtc}`");
            aR.Lines.Add("");
            aR.Lines.Add("| 券種代號 | 買入價 (Bid USD) | 賣出價 (Ask USD) | 點差 (Spread) | 狀態 | 資料來源 | 更新時間 |");
            aR.Lines.Add("|---|---:|---:|---:|---|---|---|");

            foreach (var kvp in iConfig.Quotes)
            {
                var q = kvp.Value;
                decimal aSpread = q.Ask > 0 ? ((q.Ask - q.Bid) / q.Ask * 100m) : 0;
                string aStatus = q.IsEnabled ? "🟢 可兌換" : "⚪ 已停用";
                aR.Lines.Add($"| `{q.Symbol}` | {q.Bid:0.########} | {q.Ask:0.########} | {aSpread:0.##}% | {aStatus} | {q.Source} | {q.UpdatedAtUtc} |");
            }

            aR.AddValue("fx_system_enabled", iConfig.FxSystemEnabled ? "1" : "0");
            aR.AddValue("quotes_count", iConfig.Quotes.Count.ToString());
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
                aPairRes.Lines.Add($"- 賣出 1 `{aFrom}` 可換得：**{aSellRate:0.########}** `{aTo}`");
                aPairRes.Lines.Add($"- 買入 1 `{aTo}` 需支付：**{aBuyRate:0.########}** `{aFrom}`");
                aPairRes.AddValue("can_swap", "1");
                aPairRes.AddValue("sell_rate", aSellRate.ToString("0.########", CultureInfo.InvariantCulture));
                aPairRes.AddValue("buy_rate", aBuyRate.ToString("0.########", CultureInfo.InvariantCulture));
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

            var aQuote = new SCP_RateQuote
            {
                Symbol = aSymbol,
                BaseCurrency = "USD",
                Bid = aBid,
                Ask = aAsk,
                IsEnabled = aEnabled,
                Source = aSource,
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
            aR.AddValue("symbol", aSymbol);
            aR.AddValue("bid", aQuote.Bid.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("ask", aQuote.Ask.ToString(CultureInfo.InvariantCulture));
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
