// 區塊職責：`cmd demurrage-voucher` —— 保管費轉券的**查與發**。原生，不需要 Editor。
// 物理意義：輸入是帳本上**已經發生**的保管費扣繳，輸出是券。⇒ 它是扣款的下游一層。
// 數值影響：`op=preview` **零寫入**；`op=issue` 會鑄券（而且**收不回來**）。
//
// ⭐ 為什麼 preview 是預設：發券的錯不會在當下叫 —— 券進了別人的簿子，數字完全正常。
//   ⇒ 讓「看一眼」比「做下去」更容易打，是這支唯一能提供的保護。
#nullable enable
using System.Collections.Generic;
using System.IO;
using SCP.Core.Bank;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_DemurrageVoucher : SCP_Cmd
    {
        public override string Name => "demurrage-voucher";

        public override string Summary =>
            "保管費轉券：撈某一天已落帳的保管費扣繳 → 換算券 → 發給該帳戶底下的 persona —— **預設只看不發**";

        public override string Details =>
            "· `op=preview`（預設）：**零寫入**。撈出那天的扣繳、算出誰會拿幾張。\n"
            + "  ⇒ 這就是「拿上一次真的扣繳跑一次發券測試」的入口 —— ⛔ 不必先偽造一次扣款。\n"
            + "· `op=issue`：真的發。**要 `confirm=1`**。\n"
            + "· `date` 省略 ⇒ 自動取**最近一天有扣繳的日子**；那天一筆都沒有時會說出來，⛔ 不回一個空的 0 筆。\n"
            + "· 綁定名單由**正向綁定檔**導出（`bank/<region>.md`），⛔ 不讀 registry 的 `bank_personas`。\n"
            + "· 除不盡的零頭**不發**（⛔ 不倒給任何人）；小數累積是 TASK-0271 的事，不在這裡。\n"
            + "⚠ 冪等靠本 Cmd 自己的轉券簿（`Bank/voucher_issued/<date>.json`）——\n"
            + "  **券系統刻意不記歷史**，所以同一天跑兩次在券那一側是發兩次，而兩次都不會叫。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("demurrage-voucher --arg letters_root=<letters> --arg data_root=<AgentCommands>"
                                   + " --arg region=Florin");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("data_root", "資料根（`Bank/` 與設定都從這裡找）", iRequired: true),
            new SCP_CmdArgSpec("region", "區域（貨幣）ID。⛔ 必填，本 Cmd 不推導", iRequired: true),
            new SCP_CmdArgSpec("op", "preview（預設，零寫入）／issue（真的發）"),
            new SCP_CmdArgSpec("date", "哪一天的扣繳（`YYYY-MM-DD`）。省略＝最近一天有扣繳的"),
            new SCP_CmdArgSpec("confirm", "=1 ⇒ `op=issue` 才會真的發"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLetters = iArgs.Get("letters_root").Trim();
            string aData = iArgs.Get("data_root").Trim();
            string aRegion = iArgs.Get("region").Trim();
            string aOp = iArgs.Get("op").Trim();
            if (aOp.Length == 0) aOp = "preview";
            string aDate = iArgs.Get("date").Trim();

            if (aRegion.Length == 0) return SCP_CmdResult.Fail(2, "✗ region 是必填");
            if (!Directory.Exists(aLetters)) return SCP_CmdResult.Fail(1, "✗ 信件夾根不存在：" + aLetters);
            if (!Directory.Exists(aData)) return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aData);
            if (aOp != "preview" && aOp != "issue")
                return SCP_CmdResult.Fail(2, $"✗ 認不得的 op `{aOp}`（preview / issue）");

            bool aAutoDate = aDate.Length == 0;
            if (aAutoDate)
            {
                aDate = SCP_DemurrageVoucher.LatestFeeDate(aData);
                if (aDate.Length == 0)
                    // ⛔ 「整本帳從來沒有扣繳」與「某一天沒有扣繳」是兩件事 —— 不要用同一個 0 筆帶過。
                    return SCP_CmdResult.Fail(4, "✗ 整本帳裡找不到任何一筆保管費扣繳 ⇒ 沒有東西可以轉券",
                                              "  （⛔ 這不是「今天沒有」——是一筆都沒有）");
            }

            SCP_DemurragePlan aPlan;
            try { aPlan = SCP_DemurrageVoucher.Plan(aData, aLetters, aRegion, aDate); }
            catch (System.Exception e) { return SCP_CmdResult.Fail(1, "✗ " + e.Message); }

            var aR = new SCP_CmdResult();
            aR.Lines.Add($"# 保管費轉券　`{aDate}`" + (aAutoDate ? "（自動取最近一天有扣繳的）" : ""));
            aR.Lines.Add(aPlan.PolicyEnabled
                ? $"- 政策：1 Token → **{aPlan.RatioPerToken}** 張 `{aPlan.VoucherType}` 券"
                : "- 政策：**沒開**（券種空白或比例 0）⇒ 只會列出扣繳，**一張都不發**");
            foreach (string p in aPlan.Problems) aR.Lines.Add("⚠ " + p);

            if (aPlan.Rows.Count == 0)
            {
                aR.Lines.Add($"- 那天**沒有**保管費扣繳（0 筆）");
                aR.AddValue("fee_rows", "0");
                aR.AddValue("date", aDate);
                return aR;
            }

            int aTotalV = 0, aTotalDust = 0, aPending = 0, aNoPersona = 0;
            aR.Lines.Add("");
            aR.Lines.Add("| 帳戶 | 扣繳 | 換算券 | persona | 每人 | 零頭 | 狀態 |");
            aR.Lines.Add("|---|---:|---:|---|---:|---:|---|");
            foreach (SCP_DemurragePlanRow r in aPlan.Rows)
            {
                string aState = r.AlreadyIssued ? "已轉過" : (r.NoPersona ? "🔴 沒有 persona" : "待發");
                if (!r.AlreadyIssued && !r.NoPersona && r.TotalVouchers > 0) { aPending++; aTotalV += r.TotalVouchers; aTotalDust += r.Dust; }
                if (r.NoPersona) aNoPersona++;
                aR.Lines.Add($"| `{r.AccountId}` | {r.Fee} | {r.TotalVouchers} | "
                             + (r.Personas.Count == 0 ? "—" : string.Join(", ", r.Personas))
                             + $" | {r.PerPersona} | {r.Dust} | {aState} |");
            }
            aR.AddValue("date", aDate);
            aR.AddValue("fee_rows", aPlan.Rows.Count.ToString());
            aR.AddValue("pending_rows", aPending.ToString());
            aR.AddValue("total_vouchers", aTotalV.ToString());
            aR.AddValue("dust", aTotalDust.ToString());
            aR.AddValue("no_persona_rows", aNoPersona.ToString());

            if (aOp == "preview")
            {
                aR.Lines.Add("");
                aR.Lines.Add("⇒ **零寫入**。要真的發：加 `--arg op=issue --arg confirm=1`");
                return aR;
            }

            if (iArgs.Get("confirm").Trim() != "1")
                return SCP_CmdResult.Fail(2, "✗ `op=issue` 要帶 `--arg confirm=1` —— 券發出去**收不回來**");

            var aLog = new List<string>();
            var aProblems = new List<string>();
            int aDoneRows = SCP_DemurrageVoucher.Issue(aData, aLetters, aRegion, aPlan, aLog, aProblems);
            aR.Lines.Add("");
            aR.Lines.Add("## 發券結果");
            foreach (string l in aLog) aR.Lines.Add(l);
            foreach (string p in aProblems) aR.Lines.Add(p);
            aR.AddValue("issued_rows", aDoneRows.ToString());
            aR.AddValue("problems", aProblems.Count.ToString());
            if (aProblems.Count > 0) aR.ExitCode = 5;
            return aR;
        }
    }
}
