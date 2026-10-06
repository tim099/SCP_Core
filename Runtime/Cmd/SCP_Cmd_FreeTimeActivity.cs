// 區塊職責：`cmd free-time-activity` —— 自由時間「活動層」入口（TASK-0360：Unity `ucmd run FreeTimeActivity` 的搬家版）。
//          op=pick 選活動並取得執行方式／op=step **代跑一步**（in-process 派遣 md `cmd_steps` 宣告的 cmd）／op=done 收活動並指回換骰。
// 物理意義：提示長在**唯一的入口**上 —— 人一旦進到活動工具，那些工具的輸出一個字都沒提自由時間，流程就斷在那裡。
//          邏輯本體在 `SCP_FreeTimeActivityOps`；本檔只做「參數 → 現場 → 分派」。
// 數值影響：見 SCP_FreeTimeActivityOps 檔頭。回傳檔 `letters/<P>/cmd/freetime_activity.md`。
// ⚠ 與 Unity 版刻意的差異：python `tool:` 那條路**沒有移植**（沒有任何 md 宣告它）——
//   只宣告 `tool:` 的活動 op=step 會擋並說「python 工具步驟已不支援 —— 改成 cmd_steps」。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System.Collections.Generic;
using SCP.Core.FreeTime;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_FreeTimeActivity : SCP_Cmd
    {
        public override string Name => "free-time-activity";
        public override string Parent => SCP_CmdRegistry.NameOf<SCP.Core.Cmd.SCP_Cmd_FreeTime>();

        public override string Summary => "自由時間活動層：op=pick 選活動／op=step 代跑一步（in-process cmd）／op=done 收活動並指回換骰 —— **不需要 Editor**";

        public override string Details =>
            "迴圈：free-time step=next（骰清單＋讀未讀＋可帶 body 聊天）→ op=pick → op=step（可重複）→ op=done → 回到 step=next … 直到 Cmd 宣布收工。\n"
            + "· op=pick：`activity=<id>` 必填（不確定就不帶，會列清單）；記 session 的 activity／activities_done ＋ 飢餓統計，發開場宣告。\n"
            + "· op=step：`step=<子命令>` 必須在活動 md 的 `steps` 白名單內、且有 `cmd_steps` 路由；\n"
            + "  `step_args` 吃 **cmd 原生寫法** `--arg k=v`（⛔ 不是 `--flag value`），身分（`steps_need_persona`）與資料根會自動補。\n"
            + "  那一步失敗 ⇒ 本 Cmd 也失敗（回傳檔照寫完）。⛔ python `tool:` 步驟已不支援。\n"
            + "· op=done：發收筆宣告（可帶 body），指回 `free-time step=next`。⚠ 不改 activities_done（那在 pick 就記了）。\n"
            + "· md 宣告 `needs_session: false` 的活動（例：下棋）**沒有自由時間場次也能用** —— 但要顯式帶 `activity=`。\n"
            + "· 守衛只擋「沒有 session／已收工」；逾時但仍 active ⇒ **放行**（截止是軟的，收工判定權在 step=next）。\n"
            + "exit：0 成功／2 被守衛擋下（回傳檔有 reason）／1 代跑的那一步失敗。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("free-time-activity --arg op=pick --arg persona=basecamp --arg activity=chess");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("op", "pick | step | done", iRequired: true, iChoices: new[] { "pick", "step", "done" }),
            new SCP_CmdArgSpec("persona", "誰在做活動（必填 —— 不猜身分：猜錯會替別人記活動，而那看起來完全正常）", iRequired: true),
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）—— senate CLI 沒給時用「路徑管理」頁那一格補上並印出來", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（絕對路徑）—— senate CLI 沒給時用設定檔補上", iRequired: true),
            new SCP_CmdArgSpec("project_root", "專案根（絕對路徑；找活動 md）—— senate CLI 沒給時用設定檔補上", iRequired: true),
            new SCP_CmdArgSpec("activity", "活動 id（op=pick 必填；op=step 沒給就用本場 pick 過的那一件；不綁場次的活動一律要給）"),
            new SCP_CmdArgSpec("step", "op=step 必填：子命令（須在該活動 md 的 `steps` 白名單內）"),
            new SCP_CmdArgSpec("step_args", "op=step 選填：交給那支 cmd 的參數，寫法 `--arg k=v --arg k2=\"含 空白\"`"),
            new SCP_CmdArgSpec("body", "op=pick／done 選填：開場／收筆想跟同事說的話。長內文走 --arg-file"),
            new SCP_CmdArgSpec("followed_dice", "op=pick：false ＝ 在宣告裡註明「本輪未跟骰」", iDefault: "true",
                               iChoices: new[] { "true", "false" }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aOp = iArgs.Get("op").Trim().ToLowerInvariant();
            string aPersona = iArgs.Get("persona").Trim();
            if (!IsSafePersona(aPersona))
                return SCP_CmdResult.Fail(2, "✗ persona 不合法（不可含 `/` `\\` `:` 或是 `.` / `..`）：'" + aPersona + "'");

            var aCtx = new SCP_FreeTimeContext(iArgs.Get("data_root").Trim(), iArgs.Get("letters_root").Trim(),
                                               iArgs.Get("project_root").Trim());
            // ⚠ 參數**依 op 讀** —— 讀了不屬於這個 op 的參數，「給了而從來沒被讀」那盞燈（TASK-0289）就永遠不會亮。
            string aActivity = iArgs.Get("activity");
            string aStep = aOp == "step" ? iArgs.Get("step") : "";
            string aStepArgs = aOp == "step" ? iArgs.Get("step_args") : "";
            string aBody = aOp == "step" ? "" : iArgs.Get("body");
            bool aFollowed = aOp != "pick" || iArgs.Get("followed_dice").Trim().ToLowerInvariant() != "false";
            SCP_CmdResult aRes = SCP_FreeTimeActivityOps.Run(aCtx, aOp, aPersona, aActivity, aStep, aStepArgs, aBody, aFollowed);
            if (aCtx.SettingsError != null) aRes.Lines.Add("⚠ 自由時間設定：" + aCtx.SettingsError);
            return aRes;
        }

        /// <summary>persona 會變成路徑段（回傳檔／統計檔）⇒ 擋穿越（同 SCP_ActivitySessionStore.PathOf 的規則）。</summary>
        public static bool IsSafePersona(string iPersona)
        {
            if (string.IsNullOrWhiteSpace(iPersona)) return false;
            if (iPersona.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return false;
            return iPersona != "." && iPersona != "..";
        }
    }
}
