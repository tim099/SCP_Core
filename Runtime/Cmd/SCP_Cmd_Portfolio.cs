// 區塊職責：`senate cmd portfolio` —— 投資組合帳的讀取與開帳（TASK-0371）。
// 物理意義：規則本體在 `SCP_Portfolio`（事件簿／開帳快照／平均成本重算）；本檔只做參數與輸出。
//           Senate「投資組合」頁（PortfolioPage）讀的是同一支 `SCP_Portfolio.Build` ⇒ 頁面與指令的數字同源。
// 數值影響：`op=show`／`op=events` 純讀；`op=open --arg confirm=1` 寫各人的 `letters/<P>/portfolio/opening.json`（每人只能一次）；
//          `op=migrate --arg confirm=1` 把資料根的舊落點複製進信件夾（舊檔不刪）。
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
        public override string Category => SCP_CmdCategory.Bank;

        public override string Summary =>
            "投資組合：某 persona 各券的成本、現值、報酬率（加權平均成本）／開帳快照／交易事件";

        public override string Details =>
            "· `op=show`（預設）：`--arg persona=<P>` 的持倉與報酬。`--arg ccy=<幣>` 換算顯示幣別（預設 USD，需有報價）。\n"
            + "· `op=open`：開帳快照 —— 用**當下報價**把可估值的持倉寫成成本（Tim 2026-10-01 拍板），一人一份。\n"
            + "  不帶 `confirm=1` ＝ 只試算；⛔ **每人只能寫一次**；已有交易紀錄的人不開帳（他的持倉有實際成本）。\n"
            + "· `op=events`：交易事件（新→舊），`--arg persona=<P>` 篩人、`--arg limit=<N>`。\n"
            + "· `op=migrate`：把資料根舊落點 `Market/portfolio/` 複製進各人的 `letters/<P>/portfolio/`。不帶 `confirm=1` ＝ 只試算；舊檔不刪。\n"
            + "  ⚠ 舊落點是專案的 —— **每台機器、每個專案的資料根都要各搬一次**，搬完由 git 會合。\n"
            + "⭐ 帳住在**那個人的信件夾**（`letters/<P>/portfolio/`），跟券簿一起跨專案走。\n"
            + "⭐ 事件由券的寫入端自動記：兌換（兩個入口）、保管費轉券、`voucher op=grant/consume/migrate`。**只記有報價的券**。\n"
            + "⚠ 券簿數量 ≠ 紀錄數量時，差額**單獨列出、不計入報酬**（來源與成本都不知道，⛔ 不當成 0 成本）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("portfolio --arg op=show --arg persona=Template");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("op", "show｜open｜events｜migrate", iDefault: "show", iChoices: new[] { "show", "open", "events", "migrate" }),
            new SCP_CmdArgSpec("persona", "看誰（op=show 必填；op=events 選填）", iDefault: ""),
            new SCP_CmdArgSpec("ccy", "op=show：顯示幣別（USD 或任一有報價的幣，例 TWD／JPY）", iDefault: "USD"),
            new SCP_CmdArgSpec("confirm", "op=open／migrate：1 ＝ 真的寫入", iDefault: "0"),
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
                "events" => OpEvents(aLetters, iArgs),
                "migrate" => OpMigrate(aData, aLetters, iArgs.Get("confirm").Trim() == "1"),
                _ => SCP_CmdResult.Fail(2, $"✗ 認不得的 op='{aOp}'（show｜open｜events｜migrate）"),
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
            if (SCP_Portfolio.HasUnmigratedLegacy(iData))
                return SCP_CmdResult.Fail(1, $"✗ 資料根還有沒搬的舊交易紀錄（`{SCP_Portfolio.LegacyPortfolioDir(iData)}`）⇒ 先 `op=migrate` —— "
                                             + "沒搬就開帳，會替已經有紀錄的人重拍一次成本基準");

            var o = SCP_Portfolio.BuildOpening(iData, iLetters, DateTime.UtcNow, aProblems);
            decimal aSum = 0m;
            foreach (var p in o.Positions) aSum += p.ValueUsd;

            var r = SCP_CmdResult.Success(iConfirm ? "# 📘 開帳快照已寫入" : "# 🔍 開帳快照試算（零寫入）");
            r.Lines.Add($"- 可估值持倉 {o.Positions.Count} 筆，合計現值 {Money(aSum)} USD（＝這些持倉的成本）");
            r.Lines.Add($"- 沒有報價、不進快照：{o.Unquoted.Count} 筆");
            r.Lines.Add($"- 不開帳的人：{o.Skipped.Count} 位" + (o.Skipped.Count > 0 ? "（" + string.Join("、", o.Skipped) + "）" : ""));
            foreach (var p in o.Positions)
                r.Lines.Add($"  · {p.Persona} {p.Symbol} {Units(p.UnitsE8)} × {p.BidUsd.ToString("0.########", CultureInfo.InvariantCulture)} = {Money(p.ValueUsd)} USD");
            foreach (string s in aProblems) r.Lines.Add("- ⚠ " + s);
            if (aProblems.Count > 0 && iConfirm)
                return SCP_CmdResult.Fail(1, "✗ 有讀不了的資料（見上）⇒ **沒有寫入** —— 快照每人只能拍一次，殘缺的快照拍下去就改不回來");

            if (iConfirm)
            {
                bool aOk = SCP_Portfolio.TryWriteOpening(iLetters, o, out var aWritten, out var aErrors);
                r.Lines.Add($"- 已寫入 {aWritten.Count} 位：落點 `letters/<P>/{SCP_LettersPaths.PortfolioDirName}/{SCP_Portfolio.OpeningFileName}`");
                foreach (string s in aErrors) r.Lines.Add("- ✗ " + s);
                if (!aOk) { r.ExitCode = 1; r.Lines.Add("✗ 開帳快照有人沒寫成（見上）"); }
            }
            else r.Lines.Add("💡 這是試算。確認無誤再加 `--arg confirm=1`（⛔ 每人只能一次）。");

            r.AddValue("positions", o.Positions.Count.ToString(CultureInfo.InvariantCulture));
            r.AddValue("value_usd", aSum.ToString(CultureInfo.InvariantCulture));
            r.AddValue("written", iConfirm ? "1" : "0");
            return r;
        }

        static SCP_CmdResult OpMigrate(string iData, SCP_LettersRoot iLetters, bool iConfirm)
        {
            var m = SCP_Portfolio.MigrateLegacy(iData, iLetters, iConfirm, DateTime.UtcNow);
            if (!m.LegacyExists)
                return SCP_CmdResult.Success($"# ✅ 沒有舊落點可搬（`{m.LegacyDir}` 不存在）").AddValue("copied", "0");

            var r = SCP_CmdResult.Success(iConfirm ? "# 📦 舊交易紀錄已搬進信件夾" : "# 🔍 搬家試算（零寫入）");
            r.Lines.Add($"- 舊落點：`{m.LegacyDir}`" + (m.AlreadyMarked ? "（已有搬完標記 —— 這次只補還沒搬的）" : ""));
            r.Lines.Add($"- 事件檔：{(iConfirm ? "搬了" : "會搬")} {m.EventsCopied} 筆、已經在信件夾 {m.EventsAlready} 筆");
            foreach (var kv in m.EventsByPersona) r.Lines.Add($"  · {kv.Key}：{kv.Value} 筆");
            r.Lines.Add($"- 開帳快照：{(iConfirm ? "寫了" : "會寫")} {m.OpeningsWritten.Count} 位、已經在 {m.OpeningsAlready.Count} 位"
                        + (m.OpeningsWritten.Count > 0 ? "（" + string.Join("、", m.OpeningsWritten) + "）" : ""));
            if (m.SkippedNoPersona.Count > 0)
            {
                r.Lines.Add($"- ⚠ 信件夾裡沒有這個人 ⇒ 沒搬 {m.SkippedNoPersona.Count} 筆（舊檔還在，沒有東西遺失）：");
                foreach (string s in m.SkippedNoPersona) r.Lines.Add("  · " + s);
            }
            foreach (string s in m.Problems) r.Lines.Add("- ✗ " + s);
            if (iConfirm)
            {
                r.Lines.Add(m.MarkerWritten
                    ? $"- 已寫搬完標記（`{SCP_Portfolio.LegacyMigratedMarker}`）—— 舊檔留著當存檔，⛔ 不再讀寫"
                    : "- ⚠ **沒寫搬完標記**（有衝突或讀不了的檔，見上）⇒ 頁面會繼續提示「還有沒搬的」");
                if (m.Problems.Count > 0) r.ExitCode = 1;
            }
            else r.Lines.Add("💡 這是試算。確認無誤再加 `--arg confirm=1`。⚠ 每台機器、每個專案的資料根都要各搬一次。");

            r.AddValue("copied", m.EventsCopied.ToString(CultureInfo.InvariantCulture));
            r.AddValue("already", m.EventsAlready.ToString(CultureInfo.InvariantCulture));
            r.AddValue("openings", m.OpeningsWritten.Count.ToString(CultureInfo.InvariantCulture));
            r.AddValue("skipped_no_persona", m.SkippedNoPersona.Count.ToString(CultureInfo.InvariantCulture));
            r.AddValue("problems", m.Problems.Count.ToString(CultureInfo.InvariantCulture));
            r.AddValue("marker", m.MarkerWritten ? "1" : "0");
            return r;
        }

        static SCP_CmdResult OpEvents(SCP_LettersRoot iLetters, SCP_CmdArgs iArgs)
        {
            string aPersona = iArgs.Get("persona").Trim();
            int aLimit = int.TryParse(iArgs.Get("limit"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) && n > 0 ? n : 30;
            var aProblems = new List<string>();
            var aEvents = SCP_Portfolio.ReadEvents(iLetters, aPersona.Length == 0 ? null : aPersona, aProblems);
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
