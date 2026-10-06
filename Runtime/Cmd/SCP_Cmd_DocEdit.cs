// 區塊職責：`cmd doc-edit` —— 文件編輯類自由時間活動的「改完一份之後」：登記、驗收它真的動了、指回流程（TASK-0367）。
// 物理意義：Unity `ucmd run DocEdit` 的搬家版 —— **不需要 Editor**。邏輯本體在 `SCP_DocEdit`；本檔只做「參數 → 守衛 → 落檔」。
//          ⛔ 不搬內容、不寫任何 .md（理由見 SCP_DocEdit 檔頭）。
// 數值影響：唯讀；回傳檔 `letters/<P>/cmd/docedit_<kind>.md`（被擋時 `docedit_<kind>-blocked.md`；沒帶 persona 不落檔，報告印在輸出）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.FreeTime;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_DocEdit : SCP_Cmd
    {
        public override string Name => "doc-edit";
        public override string Category => SCP_CmdCategory.Routine;

        public override string Summary =>
            "文件編輯活動的一步：登記剛改完的那份 .md、驗收它在本場真的動了、指回自由時間流程 —— 不搬內容、不寫檔、**不需要 Editor**";

        public override string Details =>
            "三個自由時間活動共用：`doc-reflection`（kind=doc）／`letter-to-self`（kind=letter）／`constitution`。\n"
            + "· kind=doc：`target` 必填（repo 相對或絕對）；帶 persona 才驗得出「本場改過沒」。\n"
            + "· kind=letter：persona 必填；不給 target ⇒ 取 letters 頂層最新一封 `letter_to_future_self`。\n"
            + "· kind=constitution：persona 必填；固定指向自己的 `_constitution.md`（忽略 target）。\n"
            + "· 驗收：存在／是 .md／在 repo 內；在自由時間中時拿**開場時刻**當基準判「本場改過沒」—— 判沒改過**不擋**（登記不是收銀台）。\n"
            + "exit：0 登記成功／2 參數不合或目標被擋（回傳檔有 reason）。\n"
            + "機讀：`verdict`＝yes|no|unknown、`target`。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("doc-edit --arg kind=doc --arg persona=basecamp --arg target=Docs/AI_READABILITY_GUIDELINES.md");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("kind", "doc | letter | constitution", iRequired: true, iChoices: SCP_DocEdit.Kinds),
            new SCP_CmdArgSpec("persona", "誰改的（letter／constitution **必填** —— 目標綁在某個人身上，猜錯會驗到別人的信與憲法；doc 選填）", iDefault: ""),
            new SCP_CmdArgSpec("target", "改的那份 .md（doc 必填；letter 選填；constitution 忽略）", iDefault: ""),
            new SCP_CmdArgSpec("note", "一句心得（選填）", iDefault: ""),
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）—— senate CLI 沒給時用設定檔補上", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（絕對路徑）—— senate CLI 沒給時用設定檔補上", iRequired: true),
            new SCP_CmdArgSpec("project_root", "專案根（絕對路徑；target 的相對基準、repo 內判定）—— senate CLI 沒給時用設定檔補上", iRequired: true),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aKind = iArgs.Get("kind").Trim().ToLowerInvariant();
            string aPersona = iArgs.Get("persona").Trim();
            if ((aKind == "letter" || aKind == "constitution") && aPersona.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ kind=" + aKind + " 需要 --arg persona=<名字> —— "
                    + "這兩種的目標綁在某個人身上，猜錯會驗到別人的信／憲法而且看起來完全正常（不猜身分）");
            if (aPersona.Length > 0 && !SCP_Cmd_FreeTimeActivity.IsSafePersona(aPersona))
                return SCP_CmdResult.Fail(2, "✗ persona 不合法（不可含 `/` `\\` `:` 或是 `.` / `..`）：'" + aPersona + "'");
            string aData = iArgs.Get("data_root").Trim();
            string aLetters = iArgs.Get("letters_root").Trim();
            string aProject = iArgs.Get("project_root").Trim();
            if (!Directory.Exists(aProject)) return SCP_CmdResult.Fail(2, "✗ 專案根不存在：" + aProject);

            var aLettersRoot = new SCP_LettersRoot(aLetters);
            SCP_DocEditResult r = SCP_DocEdit.Run(new SCP_DataRoot(aData), aLettersRoot, aProject, aKind, aPersona,
                                                  iArgs.Get("target").Trim(), iArgs.Get("note").Trim(), DateTime.Now);

            var aRes = r.Blocked.Length > 0
                ? SCP_CmdResult.Fail(2, "✗ blocked：" + r.Blocked)
                : SCP_CmdResult.Success("✓ 已登記 " + r.TargetFull + "（本場改過：" + r.Verdict + "）");
            if (aPersona.Length > 0)
            {
                string aPath = SCP_LettersPaths.CmdPayload(aLettersRoot, aPersona, "docedit",
                                                            r.Blocked.Length > 0 ? aKind + "-blocked" : aKind);
                try { SCP_CmdPayload.Write(aPath, r.Report); aRes.AddOutput(aPath); }
                catch (Exception e) { aRes.Lines.Add("⚠ 回傳檔寫不進去（報告附在下面）：" + e.Message); aRes.Lines.Add(r.Report); }
            }
            else aRes.Lines.Add(r.Report);
            aRes.AddValue("verdict", r.Verdict);
            aRes.AddValue("target", r.TargetFull);
            return aRes;
        }
    }
}
