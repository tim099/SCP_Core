// 區塊職責：`cmd bank-reconcile` —— 動錢對帳器的入口（TASK-0245）。**原生，不需要 Editor。**
// 物理意義：規則本體在 `SCP_BankReconcile`；本檔只把它開給 CLI（report／apply／status）。
// 數值影響：`report`／`status` 不動錢（report 只落一份執行紀錄）；`apply` 要 `confirm=1`，
//          **只補酒館那一類**（它就是發放路當時會送的那一筆，冪等鍵同一把）。
// @doc-sync: <Senate>/Docs/API/Cli_Reference.md（`cmd` 節）
//
// ⛔ 為什麼 apply 只補酒館那一類：觀影結算／請款／轉帳各有自己的核准流程與付款人，
//   對帳器替它們補錢＝繞過那道核准。它們的缺口**要人看**，本支只負責讓它看得見。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SCP.Core.Bank;
using SCP.Core.Json;
using SCP.Core.Tavern;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_BankReconcile : SCP_Cmd
    {
        public override string Name => "bank-reconcile";
        public override string Category => SCP_CmdCategory.Bank;

        public override string Summary =>
            "動錢對帳：事件存在 ∧ 帳上沒有（涵蓋帳上每一種 kind）—— report 唯讀／apply 補酒館那一類（要 confirm）";

        public override string Details =>
            "⭐ 判準是**事實差集**：從獨立於帳本的事實源（訊息檔／觀影結算／請款單／轉帳單）推出帳上應有哪幾筆，\n"
            + "  對照帳本實際有的。⛔ 不偵測失敗成因 —— 成因有很多種，差集只量結果。\n"
            + "⭐ 酒館那一類逐則問 `SCP_TavernPayroll.Plan()`（寫入端發薪本人），⛔ 不另抄規則。\n"
            + "🔴 比對鍵 `(type, kind, ref)` 不是冪等鍵：0296 之前的舊寫入端冪等鍵格式不同、ref 相同。\n"
            + "**覆蓋表**每一列說得出自己是 covered（算差集）／external（別的機械在對）／no_event（分錄本身即事件）；\n"
            + "  帳上出現而表上沒列的 kind 印成「⚠ 未分類」—— 那是新的動錢路徑長出來了。\n"
            + "**執行紀錄**：每跑一次落 `<Bank>/reconcile/last_run.json`（＋`runs.jsonl`）；`op=status` 讀它。\n"
            + "  ⇒ 沒人跑的樣子是「上次停在很久以前」，⛔ 不是「沒有缺口」。早安 brief 每天第一個人會自動跑一次唯讀版。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("bank-reconcile --arg days=7");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "資料根（`Bank/`、`ChatTavern/rooms`、`StreamWatch/` 都從這裡找）", iRequired: true),
            new SCP_CmdArgSpec("op", "report（預設）／apply（補酒館那一類，要 confirm=1）／status（只讀上次紀錄）／daily（今天沒跑過才跑唯讀版）", iDefault: "report"),
            new SCP_CmdArgSpec("days", "射程＝最近 N 天（含今天，UTC）", iDefault: "7"),
            new SCP_CmdArgSpec("from", "起日 `YYYY-MM-DD`（給了就蓋過 days；早於 2026-09-18 會被收窄）", iDefault: ""),
            new SCP_CmdArgSpec("to", "迄日 `YYYY-MM-DD`（預設今天）", iDefault: ""),
            new SCP_CmdArgSpec("confirm", "apply 才看：=1 才真的動錢", iDefault: ""),
            new SCP_CmdArgSpec("limit", "差集最多印幾筆", iDefault: "30"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aData = iArgs.Get("data_root").Trim();
            if (!Directory.Exists(aData)) return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aData);
            string aOp = iArgs.Get("op").Trim().ToLowerInvariant();
            if (aOp.Length == 0) aOp = "report";
            if (aOp != "report" && aOp != "apply" && aOp != "status" && aOp != "daily")
                return SCP_CmdResult.Fail(2, "✗ 不認得的 op：" + aOp + "（report | apply | status | daily）");

            var aR = new SCP_CmdResult();
            aR.Lines.Add("# 🧾 動錢對帳（事實差集）");

            if (aOp == "status")
            {
                SCP_JsonData? aLast = SCP_BankReconcile.ReadLastRun(aData, out string? aErr);
                if (aErr != null) return SCP_CmdResult.Fail(1, "✗ 執行紀錄讀不動：" + aErr + "（⛔ 不是從沒跑過）");
                if (aLast == null)
                {
                    aR.Lines.Add("- ⚠ **從來沒跑過**（沒有 `" + SCP_BankReconcile.RunDirName + "/" + SCP_BankReconcile.LastRunFileName
                                 + "`）—— ⛔ 不是「帳是平的」");
                    aR.AddValue("last_run", "none");
                    return aR;
                }
                aR.Lines.Add("- " + LastRunLine(aLast));
                aR.AddValue("last_run", aLast.GetString("at_utc", ""));
                aR.AddValue("missing", aLast.GetInt("missing", -1).ToString());
                return aR;
            }

            if (aOp == "daily")
            {
                // 每日觸發的**同一支**（早安 brief 也呼叫它）：今天跑過 ⇒ 只讀；沒跑過 ⇒ 跑唯讀版並落紀錄。
                //   給其他宿主掛（Server 迴圈／酒保 daemon）用，⛔ 不另寫一份「今天跑過沒」的判斷。
                SCP_JsonData? aRun = SCP_BankReconcile.EnsureDaily(aData, "cmd:daily", ParseInt(iArgs.Get("days"), 7),
                                                                    out bool aRanNow, out string? aDErr);
                if (aDErr != null) return SCP_CmdResult.Fail(1, "✗ 執行紀錄讀不動：" + aDErr + "（⛔ 不是從沒跑過）");
                aR.Lines.Add("- " + (aRun != null ? LastRunLine(aRun) : "（無紀錄）") + (aRanNow ? "　（**本次剛跑**）" : "　（今天已跑過 ⇒ 只讀）"));
                aR.AddValue("ran_now", aRanNow ? "1" : "0");
                aR.AddValue("missing", (aRun?.GetInt("missing", -1) ?? -1).ToString());
                return aR;
            }

            (string aFrom, string aTo) = SCP_BankReconcile.DefaultWindow(ParseInt(iArgs.Get("days"), 7));
            if (iArgs.Get("from").Trim().Length > 0) aFrom = iArgs.Get("from").Trim();
            if (iArgs.Get("to").Trim().Length > 0) aTo = iArgs.Get("to").Trim();
            int aLimit = ParseInt(iArgs.Get("limit"), 30);

            if (aOp == "apply" && iArgs.Get("confirm").Trim() != "1")
                return SCP_CmdResult.Fail(2, "✗ `op=apply` 沒帶 `confirm=1` —— **這一趟沒有執行**（增發不會自動回收）。"
                                             + " 先跑 `op=report` 看會補哪幾筆。");

            SCP_ReconcileResult r = SCP_BankReconcile.Run(aData, aFrom, aTo);
            int aApplied = 0, aDup = 0, aFailed = 0;
            if (aOp == "apply")
            {
                // ── 兩道閘（TASK-0332：Unity 補款那支有、本支原本沒有）—— ⛔ 往付錢的方向不猜 ──
                // ① 結清清單讀不動 ⇒ 不知道哪些則已經用別的方式補過 ⇒ 補下去就是付第二次錢。
                if (r.SettledUnreadable)
                    return SCP_CmdResult.Fail(1,
                        "✗ `" + SCP_BankReconcile.SettledFileName + "` 讀不動 —— **拒絕補發，一筆都沒補**：",
                        "  不知道哪些則已經用請款等方式補過，補下去會重複增發。先修好那份清單再跑（`op=report` 照樣看得到差集）。");
                // ② 射程內帳上一筆 work_post 都沒有、而酒館推得出應有 ⇒ 分不出「真的沒發過」與「讀不到帳／根給錯」。
                int aWorkPostExpected = r.Coverage.Where(c => c.Kind == SCP_TavernPayroll.KindWorkPost).Sum(c => c.Expected);
                if (r.LedgerWorkPostScanned == 0 && aWorkPostExpected > 0)
                    return SCP_CmdResult.Fail(1,
                        "✗ 射程 " + r.From + "～" + r.To + " 內帳上**一筆 work_post 都讀不到**，而酒館推得出應有 " + aWorkPostExpected
                        + " 筆 —— **拒絕補發，一筆都沒補**：",
                        "  分不出「真的沒發過」與「讀不到帳（路徑錯／全部壞檔）」；後者照補會把每一則都當成沒發過而重複增發。先人工確認帳本。");

                var aBankRoot = SCP_TavernPayroll.BankRootOf(aData);
                if (aBankRoot.Error != null || aBankRoot.Value.Length == 0)
                    return SCP_CmdResult.Fail(1, "✗ 銀行根解不出來（" + aBankRoot.Error + "）⇒ 一筆都沒補");
                foreach (SCP_ReconcileGap g in r.Gaps.Where(x => x.Item != null))
                {
                    SCP_CmdResult b;
                    try { b = SCP_CmdRegistry.Dispatch("bank", SCP_TavernPayroll.ToBankArgs(g.Item!, aBankRoot.Value)); }
                    catch (Exception e) { b = SCP_CmdResult.Fail(1, "例外：" + e.GetType().Name + ": " + e.Message); }
                    if (b.Ok)
                    {
                        bool aIsDup = b.Values.Any(v => v.Key == "duplicate" && v.Value == "1");
                        if (aIsDup) aDup++; else aApplied++;
                        aR.Lines.Add("💰 補 " + g.Detail + (aIsDup ? "　（冪等命中，錢沒動）" : ""));
                    }
                    else
                    {
                        aFailed++;
                        aR.Lines.Add("✗ 補發失敗 " + g.Detail + "　exit=" + b.ExitCode);
                        foreach (string l in b.Lines.Take(3)) aR.Lines.Add("    " + l);
                    }
                }
                // 補完重量一次：⛔ 「送出成功」不是「帳上有了」
                if (aApplied + aDup > 0) r = SCP_BankReconcile.Run(aData, aFrom, aTo);
            }

            Render(r, aR, aLimit, aOp == "apply");
            string aHost = AppDomain.CurrentDomain.FriendlyName;
            try
            {
                string aPath = SCP_BankReconcile.RecordRun(aData, SCP_BankReconcile.ToRecord(r, "manual:" + aOp, aHost, aApplied, aFailed));
                aR.Lines.Add("");
                aR.Lines.Add("📄 執行紀錄：" + aPath);
                aR.AddOutput(aPath);
            }
            catch (Exception e)
            {
                aR.Lines.Add("⚠ 執行紀錄**寫不進去**（" + e.GetType().Name + ": " + e.Message + "）⇒ `op=status` 會看不到這一趟");
            }
            aR.AddValue("from", r.From);
            aR.AddValue("to", r.To);
            aR.AddValue("missing", r.MissingTotal.ToString());
            aR.AddValue("applied", aApplied.ToString());
            aR.AddValue("apply_dup", aDup.ToString());
            aR.AddValue("apply_failed", aFailed.ToString());
            aR.AddValue("unclassified_kinds", r.UnclassifiedKinds.Count.ToString());
            aR.AddValue("amount_mismatches", r.AmountMismatches.Count.ToString());
            return aR;
        }

        static void Render(SCP_ReconcileResult r, SCP_CmdResult aR, int iLimit, bool iAfterApply)
        {
            aR.Lines.Add("");
            aR.Lines.Add("- 射程：" + r.From + " ～ " + r.To + "（UTC）／掃帳 " + r.LedgerEntriesScanned + " 筆／酒館訊息 "
                         + r.TavernMessagesScanned + " 則" + (iAfterApply ? "　⇒ 下表是**補完之後重量**的讀數" : ""));
            aR.Lines.Add("- " + (r.MissingTotal == 0 ? "✓ **差集 0**（在下表 covered 那幾列的射程內）" : "⚠ **差集 " + r.MissingTotal + " 筆**"));
            aR.Lines.Add("");
            aR.Lines.Add("| kind | 覆蓋 | 事實源 | 應有 | 帳上 | 結清 | 缺 | 量不到 | 註 |");
            aR.Lines.Add("|---|---|---|---:|---:|---:|---:|---:|---|");
            foreach (SCP_ReconcileCoverage c in r.Coverage)
            {
                string aMark = SCP_BankReconcile.CoverageName(c.Coverage);
                bool aCounted = c.Coverage == SCP_ReconcileCoverageKind.Covered;
                aR.Lines.Add("| " + c.Kind + " | " + aMark + " | " + c.Source + " | "
                             + (aCounted ? c.Expected + " | " + c.Matched + " | " + c.Settled + " | **" + c.Missing + "** | " + c.Unmeasurable
                                         : "— | — | — | — | —")
                             + " | " + c.Note + " |");
            }
            if (r.UnclassifiedKinds.Count > 0)
            {
                aR.Lines.Add("");
                aR.Lines.Add("- ⚠ **未分類的 kind**（帳上有、覆蓋表沒列 ⇒ 新的動錢路徑，對帳器不知道它的事實源）："
                             + string.Join("／", r.UnclassifiedKinds.Select(kv => kv.Key + " " + kv.Value)));
            }
            if (r.Gaps.Count > 0)
            {
                aR.Lines.Add("");
                aR.Lines.Add("## 差集（事件在、帳上沒有）");
                foreach (SCP_ReconcileGap g in r.Gaps.Take(iLimit))
                    aR.Lines.Add("- " + g.Day + "　`" + g.Key + "`　" + g.Detail
                                 + (g.Item == null ? "　（⛔ 只報不補：走它自己的核准流程）" : ""));
                if (r.Gaps.Count > iLimit) aR.Lines.Add("- …另有 " + (r.Gaps.Count - iLimit) + " 筆（`--arg limit=` 放大）");
            }
            if (r.AmountMismatches.Count > 0)
            {
                aR.Lines.Add("");
                aR.Lines.Add("## ref 對上、金額不同（⛔ 不算缺口，要人看）");
                foreach (string m in r.AmountMismatches.Take(iLimit)) aR.Lines.Add("- " + m);
            }
            if (r.Problems.Count > 0)
            {
                aR.Lines.Add("");
                aR.Lines.Add("## 問題（讀不動／射程收窄 —— 這些格子是**量不到**，⛔ 不是沒事）");
                foreach (string p in r.Problems.Take(iLimit)) aR.Lines.Add("- " + p);
                if (r.Problems.Count > iLimit) aR.Lines.Add("- …另有 " + (r.Problems.Count - iLimit) + " 則");
            }
        }

        /// <summary>一行摘要（brief／status 共用）。</summary>
        public static string LastRunLine(SCP_JsonData iLast)
        {
            int aMissing = iLast.GetInt("missing", -1);
            int aUnclassified = iLast["unclassified_kinds"].Exists ? iLast["unclassified_kinds"].Count : 0;
            // 頂層 missing 只數量得到的差集 ⇒ 某類整類量不到時它照樣是 0
            // （TASK-0359：09-30 work_post 246 則量不到，被印成「差集 0 ✓」）
            var aUnmeasurable = new List<string>();
            SCP_JsonData aCoverage = iLast["coverage"];
            if (aCoverage.Exists)
                foreach (string k in aCoverage.Keys)
                {
                    int n = aCoverage[k].GetInt("unmeasurable", 0);
                    if (n > 0) aUnmeasurable.Add(k + " 量不到 " + n + " 則");
                }
            bool aClean = aMissing == 0 && aUnmeasurable.Count == 0;
            return (aClean ? "✓" : "⚠") + " 上次對帳 `" + iLast.GetString("at_utc", "?") + "`（" + iLast.GetString("trigger", "?")
                   + "）射程 " + iLast.GetString("from", "?") + "～" + iLast.GetString("to", "?")
                   + "：差集 **" + aMissing + "**"
                   + (aUnmeasurable.Count > 0 ? "／⚠ " + string.Join("、", aUnmeasurable) + "（⛔ 不是沒差，是沒量）" : "")
                   + (aUnclassified > 0 ? "／⚠ 未分類 kind " + aUnclassified : "")
                   + (iLast.GetInt("problems", 0) > 0 ? "／問題 " + iLast.GetInt("problems", 0) : "");
        }

        static int ParseInt(string iRaw, int iFallback)
            => int.TryParse(iRaw.Trim(), out int v) && v > 0 ? v : iFallback;
    }
}
