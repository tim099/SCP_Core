// 區塊職責：`cmd payroll-audit` —— 發文領薪的**差集稽核**入口（TASK-0273 ⑥）。**原生，不需要 Editor／Server。**
// 物理意義：規則本體在 `SCP_PayrollAudit`；本檔只把它開給 CLI／brief／晚安對帳用。
// 數值影響：**零寫入。** 只數檔案。
// @doc-sync: <Senate>/Docs/API/Cli_Reference.md（`cmd` 節的「領薪差集稽核」小節）
//
// ⛔ 為什麼它不偵測成因（值得寫在入口上，因為改它的人第一個念頭會是「加一個檢查」）：
//   2026-09-22 同一天量到「整天 0 筆落帳」有**三種**成因，而三者外觀完全相同。
//   ⇒ 偵測成因的守衛，明天會被第四種繞過去。本支量**結果**：兩邊各數一次、相減。
#nullable enable
using System.Collections.Generic;
using System.IO;
using SCP.Core.Bank;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_PayrollAudit : SCP_Cmd
    {
        public override string Name => "payroll-audit";

        public override string Summary =>
            "發文領薪差集：那一天有幾則訊息 vs 帳上有幾筆 `work_post` —— **唯讀，不需要 Editor**";

        public override string Details =>
            "🩸 由來：2026-09-22 整天 **0 筆** `work_post` 落帳而酒館有 **135 則**訊息，"
            + "每一筆發文都回 `announce = Posted` —— **沒有任何一層喊**（TASK-0273）。\n"
            + "⭐ 判準是**量差集，⛔ 不偵測成因** —— 那一天量到三種成因、外觀完全相同：\n"
            + "  ① 呼叫端手填了 Cmd 端已拒收的參數　② persona 解析不到（⚠ 那是刻意的，不是病）\n"
            + "  ③ Server 與 CLI 的 build 不符。⇒ 偵測成因的守衛明天會被第四種繞過去。\n"
            + "\n"
            + "**判決**：`alarm`（有一堆應計酬而帳上 0 筆）／`warn`（有缺口）／`clean`／\n"
            + "`no_sample`（那天沒訊息 —— ⛔ 不是「沒問題」）／`unmeasurable`（比對關係壞了 —— ⛔ 不是「全漏」）。\n"
            + "⚠ 射程：本層**讀不到 category 計不計酬的設定**（那在 Unity 的 routing 資產裡）\n"
            + "  ⇒ 差集按 category 分組印出來讓人自己判斷；某一類整天全缺多半是那一類本來就不計酬。\n"
            + "⚠ 不給 `letters_root`/`region` ⇒ 「persona 解析不到」那些則**扣不掉**，報告會明說它沒扣。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("payroll-audit --arg data_root=<AgentCommands> --arg region=Florin");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "資料根（`ChatTavern/rooms` 與 `Bank/ledger` 都從這裡找）", iRequired: true),
            new SCP_CmdArgSpec("day", "要量哪一個 **UTC** 日（`YYYY-MM-DD`）；不給＝今天", iDefault: ""),
            new SCP_CmdArgSpec("days", "改成量「最近 N 天」（含今天）；給了就蓋過 `day`", iDefault: ""),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根 —— 給了才扣得掉「解析不到帳號」那些則", iDefault: ""),
            new SCP_CmdArgSpec("region", "區域（貨幣）ID —— 與 `letters_root` 成對，少一個就扣不掉", iDefault: ""),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aData = iArgs.Get("data_root").Trim();
            if (!Directory.Exists(aData)) return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aData);

            string aLetters = iArgs.Get("letters_root").Trim();
            string aRegion = iArgs.Get("region").Trim();
            string aDay = iArgs.Get("day").Trim();
            string aDaysRaw = iArgs.Get("days").Trim();

            var aDays = new List<string>();
            if (aDaysRaw.Length > 0)
            {
                if (!int.TryParse(aDaysRaw, out int aN) || aN <= 0 || aN > 400)
                    return SCP_CmdResult.Fail(2, "✗ `days` 要是 1~400 的整數（給的是 `" + aDaysRaw + "`）");
                System.DateTime aNow = System.DateTime.UtcNow.Date;
                for (int i = aN - 1; i >= 0; i--)
                    aDays.Add(aNow.AddDays(-i).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            }
            else aDays.Add(aDay.Length > 0 ? aDay : SCP_PayrollAudit.TodayUtcKey());

            var aR = SCP_CmdResult.Success("# 💸 發文領薪差集稽核");
            int aWorst = 0;   // 0 clean/no_sample ／ 1 warn/unmeasurable ／ 2 alarm
            SCP_PayrollAuditResult? aLast = null;

            foreach (string aKey in aDays)
            {
                SCP_PayrollAuditResult a = SCP_PayrollAudit.Audit(aData, aKey, aLetters, aRegion);
                aLast = a;
                aR.Lines.Add("");
                aR.Lines.Add("- " + a.HeadLine());
                aR.Lines.Add("  · " + a.Why);
                aR.Lines.Add("  · 內訳：沒有 persona **" + a.WithoutPersona + "**（結構上不計酬）"
                             + "／解析不到帳號 **" + a.Unresolvable + "**"
                             + (a.ResolverAvailable ? "（刻意不計酬）" : " ⚠ **未扣**（沒給 letters_root/region）")
                             + "／帳上 work_post **" + a.LedgerWorkPostEntries + "** 筆"
                             + (a.Settled > 0
                                ? "／**請款結清 " + a.Settled + " 則**（憑據＝`"
                                  + SCP_PayrollAudit.SettledFileName + "` 的 ref 清單，⛔ 不是逐則分錄）"
                                : ""));

                if (a.Missing > 0)
                {
                    aR.Lines.Add("  · 差集 by category：" + Join(a.MissingByCategory)
                                 + "　⚠ 某一類整天全缺多半是那一類本來就不計酬");
                    aR.Lines.Add("  · 差集 by persona：" + Join(a.MissingByPersona));
                    aR.Lines.Add("  · 差集 by room：" + Join(a.MissingByRoom));
                }
                if (a.LedgerRefsUnmatched > 0)
                    aR.Lines.Add("  · ⚠ 帳上有 **" + a.LedgerRefsUnmatched + "** 筆 `work_post` 的 ref 找不到對應訊息"
                                 + "（⛔ 那不是漏發，是比對關係的體檢讀數）");
                if (a.Problems.Count > 0)
                {
                    aR.Lines.Add("  · ⚠ **有 " + a.Problems.Count + " 個檔讀不動**（⛔ 不當成「那裡沒有東西」）：");
                    for (int i = 0; i < a.Problems.Count && i < 5; i++) aR.Lines.Add("    · " + a.Problems[i]);
                    if (a.Problems.Count > 5) aR.Lines.Add("    · …另有 " + (a.Problems.Count - 5) + " 個");
                }

                int aLevel = a.Verdict switch
                {
                    SCP_PayrollVerdict.Alarm => 2,
                    SCP_PayrollVerdict.Warn => 1,
                    SCP_PayrollVerdict.Unmeasurable => 1,
                    _ => 0,
                };
                if (aLevel > aWorst) aWorst = aLevel;
            }

            aR.Lines.Add("");
            aR.Lines.Add("⛔ 本 Cmd **一毛錢都不動**，也不寫任何檔 —— 它只數兩邊的檔案然後相減。");
            aR.Lines.Add("⚠ 差集不等於「該補這麼多」：補發要人看過 category 那一欄再決定（那一格本層量不到）。");

            if (aLast != null)
            {
                aR.AddValue("day", aLast.DayKey);
                aR.AddValue("messages", aLast.Messages.ToString());
                aR.AddValue("candidates", aLast.Candidates.ToString());
                aR.AddValue("paid", aLast.Paid.ToString());
                aR.AddValue("missing", aLast.Missing.ToString());
                aR.AddValue("verdict", aLast.Verdict.ToString().ToLowerInvariant());
            }
            aR.AddValue("days_scanned", aDays.Count.ToString());
            aR.AddValue("worst", aWorst.ToString());

            // ⚠ 退出碼刻意**不**因為「有差集」就非零：本支掛在早安 brief 與晚安對帳那條必經路上，
            //   讓它 exit 非零會把整套儀式變成紅的，而人會開始略過整段 —— 那就把警報本身弄壞了。
            //   ⇒ 判決走 `verdict`／`worst` 這兩個值，⛔ 不走退出碼。
            return aR;
        }

        static string Join(Dictionary<string, int> iMap)
        {
            if (iMap.Count == 0) return "（無）";
            var aParts = new List<string>();
            foreach (KeyValuePair<string, int> kv in iMap) aParts.Add(kv.Key + " " + kv.Value);
            aParts.Sort(System.StringComparer.Ordinal);
            return string.Join("／", aParts);
        }
    }
}
