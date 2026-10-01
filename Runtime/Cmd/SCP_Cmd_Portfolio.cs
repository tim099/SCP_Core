// 區塊職責：`senate cmd portfolio` —— 投資組合帳的讀取與開帳（TASK-0371）。
// 物理意義：規則本體在 `SCP_Portfolio`（事件簿／開帳快照／平均成本重算）；本檔只做參數與輸出。
//           Senate「投資組合」頁（PortfolioPage）讀的是同一支 `SCP_Portfolio.Build` ⇒ 頁面與指令的數字同源。
// 數值影響：`op=show`／`op=events` 純讀；`op=open --arg confirm=1` 寫 `Market/portfolio/opening.json`（只能一次）。
// @doc-sync: <SCP_Core>/Docs~/Portfolio.md（指令參數、成本規則、事件檔格式）
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Market;
using SCP.Core.Paths;
using SCP.Core.Voucher;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Portfolio : SCP_Cmd
    {
        public override string Name => "portfolio";

        public override string Summary =>
            "投資組合：某 persona 各券的成本、現值、報酬率（加權平均成本）／開帳快照／交易事件";

        public override string Details =>
            "· `op=show`（預設）：`--arg persona=<P>` 的持倉與報酬。`--arg ccy=<幣>` 換算顯示幣別（預設 USD，需有報價）。\n"
            + "· `op=open`：開帳快照 —— 用**當下報價**把所有人可估值的持倉寫成成本（Tim 2026-10-01 拍板）。\n"
            + "  不帶 `confirm=1` ＝ 只試算；⛔ **只能寫一次**（重拍會把所有人的成本基準改到今天）。\n"
            + "· `op=events`：交易事件（新→舊），`--arg persona=<P>` 篩人、`--arg limit=<N>`。\n"
            + "⭐ 事件由券的寫入端自動記：兌換（兩個入口）、保管費轉券、`voucher op=grant/consume/migrate`。**只記有報價的券**。\n"
            + "⚠ 券簿數量 ≠ 紀錄數量時，差額**單獨列出、不計入報酬**（來源與成本都不知道，⛔ 不當成 0 成本）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("portfolio --arg op=show --arg persona=Template");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("op", "show｜open｜events", iDefault: "show", iChoices: new[] { "show", "open", "events" }),
            new SCP_CmdArgSpec("persona", "看誰（op=show 必填；op=events 選填）", iDefault: ""),
            new SCP_CmdArgSpec("ccy", "op=show：顯示幣別（USD 或任一有報價的幣，例 TWD／JPY）", iDefault: "USD"),
            new SCP_CmdArgSpec("confirm", "op=open：1 ＝ 真的寫入（只能一次）", iDefault: "0"),
            new SCP_CmdArgSpec("limit", "op=events：最多列幾筆", iDefault: "30"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aData = iArgs.Get("data_root").Trim().Replace('\\', '/');
            string aLettersRaw = iArgs.Get("letters_root").Trim().Replace('\\', '/');
            if (aData.Length == 0 || !Directory.Exists(aData)) return SCP_CmdResult.Fail(1, $"✗ 資料根不存在：'{aData}'");
            if (aLettersRaw.Length == 0 || !Directory.Exists(aLettersRaw)) return SCP_CmdResult.Fail(1, $"✗ letters 根不存在：'{aLettersRaw}'");
            var aLetters = new SCP_LettersRoot(aLettersRaw);

            string aOp = iArgs.Get("op").Trim().ToLowerInvariant();
            if (aOp.Length == 0) aOp = "show";
            return aOp switch
            {
                "show" => OpShow(aData, aLetters, iArgs),
                "open" => OpOpen(aData, aLetters, iArgs.Get("confirm").Trim() == "1"),
                "events" => OpEvents(aData, iArgs),
                _ => SCP_CmdResult.Fail(2, $"✗ 認不得的 op='{aOp}'（show｜open｜events）"),
            };
        }

        static SCP_CmdResult OpShow(string iData, SCP_LettersRoot iLetters, SCP_CmdArgs iArgs)
        {
            string aPersona = iArgs.Get("persona").Trim();
            if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=show 缺 `persona` —— 投資組合是一個人的，⛔ 不猜是誰");

            var aConfig = SCP_MarketRateCache.Load(iData, out string? aErr);
            if (aErr != null) return SCP_CmdResult.Fail(1, $"✗ 匯率快取讀不了：{aErr}");
            if (!TryCcy(aConfig, iArgs.Get("ccy"), out string aCcy, out decimal aUsdPerCcy, out string? aCcyErr))
                return SCP_CmdResult.Fail(2, "✗ " + aCcyErr);

            var v = SCP_Portfolio.Build(iData, iLetters, aPersona, aConfig, DateTime.UtcNow);
            var r = SCP_CmdResult.Success($"# 💼 `{aPersona}` 的投資組合（顯示幣別 {aCcy}）");
            r.Lines.Add(v.OpeningExists
                ? $"- 開帳快照：{v.OpeningAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}（之前的持倉以這一刻的現值為成本）"
                : "- ⚠ **尚未開帳** ⇒ 既有持倉沒有成本，只有開帳之後的交易才算得出報酬（`op=open --arg confirm=1`）");
            r.Lines.Add("");
            r.Lines.Add($"| 券 | 持有 | 成本 | 均價 | 現價 | 現值 | 未實現 | 報酬率 | 已實現 | 成本來源 |");
            r.Lines.Add("|---|---:|---:|---:|---:|---:|---:|---:|---:|---|");
            foreach (var p in v.Positions)
            {
                r.Lines.Add($"| {p.Symbol} | {Units(p.ActualE8)} | {Money(p.CostHeldUsd / aUsdPerCcy)} | {AvgText(p, aUsdPerCcy)} | "
                            + $"{Money(p.BidUsd / aUsdPerCcy)} | {Money(p.ActualValueUsd / aUsdPerCcy)} | {Signed(p.UnrealizedUsd / aUsdPerCcy)} | "
                            + $"{Pct(p.Roi)} | {Signed(p.RealizedUsd / aUsdPerCcy)} | {p.BasisLabel} |");
                if (p.DriftE8 != 0)
                    r.Lines.Add($"|  ↳ ⚠ 與紀錄不符 | {Signed((decimal)p.DriftE8 / SCP_VoucherBook.FractionScale)} 張 | 來源不明，⛔ 不計入報酬 |||||||");
            }
            if (v.Positions.Count == 0) r.Lines.Add("| （沒有可估值的持倉） ||||||||||");
            r.Lines.Add("");
            decimal aCost = v.TotalCostHeldUsd, aVal = v.TotalValueHeldUsd;
            r.Lines.Add($"- 合計（只算有紀錄的部分）：成本 {Money(aCost / aUsdPerCcy)}／現值 {Money(aVal / aUsdPerCcy)}／未實現 {Signed((aVal - aCost) / aUsdPerCcy)}"
                        + $"（{Pct(aCost > 0 ? (aVal - aCost) / aCost : (decimal?)null)}）／已實現 {Signed(v.TotalRealizedUsd / aUsdPerCcy)}");
            if (v.Unquoted.Count > 0)
            {
                var aParts = new List<string>();
                foreach (var kv in v.Unquoted) aParts.Add($"{kv.Key} {Units(kv.Value)}");
                r.Lines.Add($"- 沒有報價的券（不估值）：{string.Join("、", aParts)}");
            }
            foreach (string s in v.Problems) r.Lines.Add("- ⚠ " + s);

            r.AddValue("persona", aPersona);
            r.AddValue("opened", v.OpeningExists ? "1" : "0");
            r.AddValue("positions", v.Positions.Count.ToString(CultureInfo.InvariantCulture));
            r.AddValue("cost_usd", aCost.ToString(CultureInfo.InvariantCulture));
            r.AddValue("value_usd", aVal.ToString(CultureInfo.InvariantCulture));
            r.AddValue("events", v.Events.Count.ToString(CultureInfo.InvariantCulture));
            r.AddValue("problems", v.Problems.Count.ToString(CultureInfo.InvariantCulture));
            return r;
        }

        static SCP_CmdResult OpOpen(string iData, SCP_LettersRoot iLetters, bool iConfirm)
        {
            var aProblems = new List<string>();
            var aExisting = SCP_Portfolio.LoadOpening(iData, out string? aLoadErr);
            if (aLoadErr != null) return SCP_CmdResult.Fail(1, "✗ " + aLoadErr);
            if (aExisting != null)
                return SCP_CmdResult.Fail(1, $"✗ 已經開過帳（{aExisting.AtUtc:o}，{aExisting.Positions.Count} 筆持倉）⇒ 拒絕重拍 —— 重拍會把所有人的成本基準改到今天");

            var o = SCP_Portfolio.BuildOpening(iData, iLetters, DateTime.UtcNow, aProblems);
            decimal aSum = 0m;
            foreach (var p in o.Positions) aSum += p.ValueUsd;

            var r = SCP_CmdResult.Success(iConfirm ? "# 📘 開帳快照已寫入" : "# 🔍 開帳快照試算（零寫入）");
            r.Lines.Add($"- 可估值持倉 {o.Positions.Count} 筆，合計現值 {Money(aSum)} USD（＝這些持倉的成本）");
            r.Lines.Add($"- 沒有報價、不進快照：{o.Unquoted.Count} 筆");
            foreach (var p in o.Positions)
                r.Lines.Add($"  · {p.Persona} {p.Symbol} {Units(p.UnitsE8)} × {p.BidUsd.ToString("0.########", CultureInfo.InvariantCulture)} = {Money(p.ValueUsd)} USD");
            foreach (string s in aProblems) r.Lines.Add("- ⚠ " + s);
            if (aProblems.Count > 0 && iConfirm)
                return SCP_CmdResult.Fail(1, "✗ 有讀不了的資料（見上）⇒ **沒有寫入** —— 快照只能拍一次，殘缺的快照拍下去就改不回來");

            if (iConfirm)
            {
                if (!SCP_Portfolio.TryWriteOpening(iData, o, out string? aWriteErr))
                    return SCP_CmdResult.Fail(1, "✗ " + aWriteErr);
                r.Lines.Add($"- 落點：`{SCP_Portfolio.OpeningPath(iData)}`");
            }
            else r.Lines.Add("💡 這是試算。確認無誤再加 `--arg confirm=1`（⛔ 只能一次）。");

            r.AddValue("positions", o.Positions.Count.ToString(CultureInfo.InvariantCulture));
            r.AddValue("value_usd", aSum.ToString(CultureInfo.InvariantCulture));
            r.AddValue("written", iConfirm ? "1" : "0");
            return r;
        }

        static SCP_CmdResult OpEvents(string iData, SCP_CmdArgs iArgs)
        {
            string aPersona = iArgs.Get("persona").Trim();
            int aLimit = int.TryParse(iArgs.Get("limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 30;
            var aProblems = new List<string>();
            var aEvents = SCP_Portfolio.ReadEvents(iData, aPersona.Length == 0 ? null : aPersona, aProblems);
            var r = SCP_CmdResult.Success($"# 🧾 交易事件（{(aPersona.Length == 0 ? "全部" : aPersona)}，共 {aEvents.Count} 筆，新→舊）");
            for (int i = aEvents.Count - 1, k = 0; i >= 0 && k < aLimit; i--, k++) r.Lines.Add("- " + Describe(aEvents[i]));
            foreach (string s in aProblems) r.Lines.Add("- ⚠ " + s);
            r.AddValue("events", aEvents.Count.ToString(CultureInfo.InvariantCulture));
            return r;
        }

        /// <summary>一筆事件的人話（頁面與指令共用）。</summary>
        public static string Describe(SCP_PortfolioEvent e)
        {
            string aT = e.AtUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
            if (e.Kind == SCP_PortfolioEvent.KindSwap)
                return $"{aT} {e.Persona} 兌換 {Units(e.FromE8)} {e.FromSymbol} → {Units(e.ToE8)} {e.ToSymbol}（市值 {Money(e.ValueUsd)} USD）";
            string aVerb = e.DeltaE8 >= 0 ? "＋" : "－";
            return $"{aT} {e.Persona} {aVerb}{Units(Math.Abs(e.DeltaE8))} {e.Symbol}（{e.Source}，市值 {Money(e.ValueUsd)} USD）";
        }

        /// <summary>顯示幣別：USD 或任一有報價的幣（用中間價換算）。</summary>
        public static bool TryCcy(SCP_MarketRateConfig iConfig, string iCcy, out string oCcy, out decimal oUsdPerCcy, out string? oError)
        {
            oCcy = (iCcy ?? "").Trim().ToUpperInvariant();
            if (oCcy.Length == 0) oCcy = "USD";
            oUsdPerCcy = 1m; oError = null;
            if (!SCP_MarketRateCache.TryGetQuote(iConfig, oCcy, out var q))
            { oError = $"顯示幣別 '{oCcy}' 沒有有效報價 ⇒ 換算不了"; return false; }
            oUsdPerCcy = (q!.Bid + q.Ask) / 2m;
            return true;
        }

        public static string Units(long iE8) => ((decimal)iE8 / SCP_VoucherBook.FractionScale).ToString("#,0.########", CultureInfo.InvariantCulture);
        public static string Money(decimal iV) => iV.ToString("#,0.00", CultureInfo.InvariantCulture);
        public static string Signed(decimal iV) => (iV > 0 ? "+" : "") + iV.ToString("#,0.00", CultureInfo.InvariantCulture);
        public static string Pct(decimal? iV) => iV.HasValue ? (iV.Value > 0 ? "+" : "") + (iV.Value * 100m).ToString("0.##", CultureInfo.InvariantCulture) + "%" : "—";

        static string AvgText(SCP_PortfolioPosition p, decimal iUsdPerCcy)
            => p.HeldTrackedE8 > 0
                ? (p.CostHeldUsd / ((decimal)p.HeldTrackedE8 / SCP_VoucherBook.FractionScale) / iUsdPerCcy).ToString("#,0.########", CultureInfo.InvariantCulture)
                : "—";
    }
}
