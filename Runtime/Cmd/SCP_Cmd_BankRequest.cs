// 區塊職責：`cmd bank-request` —— **開請款單／轉帳單、撤單、列單**的 CLI 出口（TASK-0325 第一批：從 Unity `Cmd_Treasury` 搬來）。
// 物理意義：agent 主張「該付我」或「這筆該從 A 搬到 B」的正規管道 —— 有單據、可審批、可駁回、可追溯。
//           審批仍是 `cmd bank --arg op=approve|reject`（Server）；本支**只寫單子、一毛錢都不動**。
//           單子是一張一檔（uuid 檔名）⇒ 本地跑就好，不需要 Server、不需要 Editor。
// 數值影響：`op=list` 零寫入；`op=request`／`op=transfer` 各寫一個新檔；`op=cancel` 只改那張單的裁決欄（pending ⇒ cancelled）。
//           驗證不過一律零寫入。
// ⚠ 帳戶一律給**帳號 id**（例 cc / zeta / Myth），⛔ 不是 persona 名 —— 本支不推斷（2026-07-31 血證：錢進影子帳戶）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Bank;
using SCP.Core.Letters;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_BankRequest : SCP_Cmd
    {
        public override string Name => "bank-request";

        public override string Summary => "開請款單／轉帳單、撤單、列單（**只寫單子、不動錢**；審批走 `bank op=approve`）—— **本地跑，不需要 Editor**";

        public override string Details =>
            "單據：`<data_root>/Bank/requests/<日>/*__request.json`、`Bank/transfer_requests/<日>/*__transfer.json`（跟 Unity 版同格式、同位置）。\n"
            + "· `op=request --arg target_bank=<收款帳號> --arg amount=N --arg-file reason=<檔> [--arg source_kind=...] [--arg source_ref=SHA/seq/單號] [--arg funding=central|mint]`\n"
            + "    funding 沒給：`source_kind=work_post_backfill`（補薪）⇒ mint；其他 ⇒ 留空，審批端用央行撥款。\n"
            + "· `op=transfer --arg from_bank=<出款帳號> --arg to_bank=<收款帳號> --arg amount=N --arg-file reason=<檔> [--arg kind=...]`（守恆 A→B；常見用途是歸戶）\n"
            + "· `op=cancel --arg request_id=<單號> [--arg note=...]`：撤回 pending 的單（請款與轉帳都行；已批就撤不了）\n"
            + "· `op=list [--arg pending_only=0] [--arg max=N]`：列請款單（新到舊）。待審的請款＋轉帳一起看 ⇒ `bank --arg op=requests`。\n"
            + "⛔ 帳戶給帳號 id，不是 persona 名。開單人（persona／agent）會寫進單子，審批者看得到是誰提的。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("bank-request --arg op=request --arg persona=gura --arg target_bank=Myth --arg amount=5 --arg-file reason=reason.md");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（查開單人的 agent 用；沒給就只記 persona）", iDefault: ""),
            new SCP_CmdArgSpec("op", "做什麼", iDefault: "list", iChoices: new[] { "list", "request", "transfer", "cancel" }),
            new SCP_CmdArgSpec("persona", "開單／撤單的人（request／transfer／cancel 必填）", iDefault: ""),
            new SCP_CmdArgSpec("agent", "開單人的 agent（沒給 ⇒ 從 persona 的登入狀態讀）", iDefault: ""),
            new SCP_CmdArgSpec("target_bank", "op=request：收款帳號 id（⛔ 不是 persona 名）", iDefault: ""),
            new SCP_CmdArgSpec("from_bank", "op=transfer：出款帳號 id", iDefault: ""),
            new SCP_CmdArgSpec("to_bank", "op=transfer：收款帳號 id", iDefault: ""),
            new SCP_CmdArgSpec("amount", "金額（正整數）", iDefault: "0"),
            new SCP_CmdArgSpec("reason", "為什麼（必填；長文走 --arg-file）", iDefault: ""),
            new SCP_CmdArgSpec("source_kind", "op=request：核准後寫進帳本的 source_kind（預設 manual_request）", iDefault: ""),
            new SCP_CmdArgSpec("source_ref", "op=request：憑證（SHA／seq／單號）", iDefault: ""),
            new SCP_CmdArgSpec("funding", "op=request：central（央行撥款）／mint（增發）；空＝未宣告", iDefault: ""),
            new SCP_CmdArgSpec("kind", "op=transfer：分類（預設 manual_transfer）", iDefault: ""),
            new SCP_CmdArgSpec("currency", "幣別", iDefault: "tavern_token"),
            new SCP_CmdArgSpec("request_id", "op=cancel：單號", iDefault: ""),
            new SCP_CmdArgSpec("note", "op=cancel：備註", iDefault: ""),
            new SCP_CmdArgSpec("pending_only", "op=list：1＝只列 pending", iDefault: "1", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("max", "op=list：最多幾張", iDefault: "50"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aRoot = iArgs.Get("data_root").Trim();
            if (!System.IO.Directory.Exists(aRoot)) return SCP_CmdResult.Fail(1, $"✗ 資料根不存在：{aRoot}");
            string aOp = iArgs.Get("op").Trim();
            if (aOp == "list") return List(aRoot, iArgs);

            string aPersona = iArgs.Get("persona").Trim();
            if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, $"✗ op={aOp} 要 `--arg persona=<你>` —— 單子上要寫得出是誰提的");
            string aAgent = iArgs.Get("agent").Trim();
            if (aAgent.Length == 0)
            {
                string aLetters = iArgs.Get("letters_root").Trim();
                if (aLetters.Length > 0) aAgent = SCP_PersonaLetters.ReadPersonaLock(aLetters, aPersona)?.Agent ?? "";
            }

            if (aOp == "cancel")
            {
                string aId = iArgs.Get("request_id").Trim();
                if (!SCP_TreasuryRequests.Cancel(aRoot, aId, $"{aAgent}@{aPersona}", iArgs.Get("note"), out string aKind, out string aErr))
                    return SCP_CmdResult.Fail(2, "✗ 沒有撤回：" + aErr);
                var aC = SCP_CmdResult.Success($"🗑 已撤回{(aKind == "payout" ? "請款單" : "轉帳單")} `{aId}`（pending ⇒ cancelled；回讀）");
                aC.AddValue("request_id", aId);
                aC.AddValue("kind", aKind);
                return aC;
            }

            if (!int.TryParse(iArgs.Get("amount").Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int aAmount) || aAmount <= 0)
                return SCP_CmdResult.Fail(2, $"✗ amount 要正整數（收到 '{iArgs.Get("amount")}'）—— 沒有寫入");

            if (aOp == "request")
            {
                if (!SCP_TreasuryRequests.CreatePayout(aRoot, iArgs.Get("target_bank"), aAmount, iArgs.Get("reason"),
                        iArgs.Get("source_kind"), iArgs.Get("source_ref"), aAgent, aPersona, iArgs.Get("currency"), iArgs.Get("funding"),
                        out SCP_PayoutRequest r, out string aErr))
                    return SCP_CmdResult.Fail(2, "✗ 沒有開單：" + aErr);
                var aR = SCP_CmdResult.Success(
                    $"# 🧾 請款單已開 —— `{r.RequestId}`（回讀）",
                    $"- 金額：**{r.Amount} {r.Currency}** → 收款帳號 **{r.TargetBank}**",
                    $"- 理由：{r.Reason}",
                    $"- source_kind：{r.SourceKind}　資金來源：{(r.Funding.Length == 0 ? "⚠ 未宣告 ⇒ 審批端用央行撥款" : SCP_PayoutFunding.Describe(r.Funding))}",
                    $"- 開單人：{r.RequesterAgent}@{r.RequesterPersona}",
                    $"- 狀態：**{r.Status}** —— 錢還沒動。審批：`senate cmd bank --arg op=approve --arg request_id={r.RequestId} --arg confirm=1`",
                    $"落點：{r.Path}");
                aR.AddValue("request_id", r.RequestId);
                aR.AddValue("path", r.Path);
                return aR;
            }

            // transfer
            if (!SCP_TreasuryRequests.CreateTransfer(aRoot, iArgs.Get("from_bank"), iArgs.Get("to_bank"), aAmount, iArgs.Get("reason"),
                    iArgs.Get("kind"), aAgent, aPersona, iArgs.Get("currency"), out SCP_TransferRequest t, out string aTErr))
                return SCP_CmdResult.Fail(2, "✗ 沒有開單：" + aTErr);
            var aT = SCP_CmdResult.Success(
                $"# 💸 轉帳單已開 —— `{t.RequestId}`（回讀）",
                $"- 金額：**{t.Amount} {t.Currency}**：**{t.FromBank}** → **{t.ToBank}**（分類 {t.Kind}）",
                $"- 理由：{t.Reason}",
                $"- 開單人：{aAgent}@{t.RequesterPersona}",
                $"- 狀態：**{t.Status}** —— 錢還沒動。審批：`senate cmd bank --arg op=approve --arg request_id={t.RequestId} --arg confirm=1`",
                $"落點：{t.Path}");
            aT.AddValue("request_id", t.RequestId);
            aT.AddValue("path", t.Path);
            return aT;
        }

        static SCP_CmdResult List(string iRoot, SCP_CmdArgs iArgs)
        {
            bool aPending = iArgs.Get("pending_only").Trim() != "0";
            if (!int.TryParse(iArgs.Get("max").Trim(), out int aMax) || aMax <= 0) aMax = 50;
            var aProblems = new List<string>();
            List<SCP_PayoutRequest> aList = SCP_TreasuryRequests.ListPayouts(iRoot, aPending, aMax, aProblems);
            var aR = SCP_CmdResult.Success($"# 🧾 請款單（{(aPending ? "只列 pending" : "全部")}，{aList.Count} 張）");
            if (aList.Count == 0) aR.Lines.Add("（無）");
            foreach (SCP_PayoutRequest r in aList)
            {
                aR.Lines.Add($"- `{r.RequestId}` **{r.Amount} {r.Currency}** → `{r.TargetBank}`　[{r.Status}]　{r.RequesterAgent}@{r.RequesterPersona}　{r.RequestedAt}");
                aR.Lines.Add($"    理由：{(r.Reason.Length > 120 ? r.Reason.Substring(0, 120) + "…" : r.Reason)}");
                if (r.DecisionNote.Length > 0) aR.Lines.Add($"    審批備註：{r.DecisionNote}");
            }
            foreach (string p in aProblems) aR.Lines.Add("⚠ " + p);
            aR.AddValue("count", aList.Count.ToString(CultureInfo.InvariantCulture));
            return aR;
        }
    }
}
