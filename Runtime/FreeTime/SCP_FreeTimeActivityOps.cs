// 區塊職責：`free-time-activity` 的三個 op —— pick（選活動、回傳「怎麼執行」）／step（**代跑一步**）／done（收活動、指回換骰）。
// 物理意義：移植自 Unity `Cmd_FreeTimeActivity`（Tim 2026-08-18 拍板；TASK-0360）。
//          原本活動是「自己去跑各活動工具」，於是自由時間的流程提示**只活在 free-time 的回傳檔裡** ——
//          人一旦進到活動工具，那些工具的輸出一個字都沒提自由時間，流程就斷在那裡。包一層之後，提示長在**唯一的入口**上。
//   迴圈形狀：free-time step=next → op=pick → op=step（可重複）→ op=done → 回到 step=next … 直到 Cmd 宣布收工。
//   ⚠ Unity 版作者記下的判斷錯誤（保留）：「活動是多步互動，Cmd 跑不完」—— **活動橫跨很多步 ≠ 一次呼叫做不完一步**。
//     走一子、放一個像素本來就是次秒級的一次性動作 ⇒ op=step 代跑**一步**，然後在回傳檔接上下一步。
// 數值影響：寫 session 的 activity／activities_done（pick）；寫活動統計（pick）；op=step **in-process** 派遣一支 SCP cmd；
//          發酒館訊息（pick／done）；每個 op 各寫一份回傳檔（`cmd/freetime_activity.md`）。
// ⛔ python `tool:` 那條路**沒有移植**（TASK-0360：沒有任何 md 宣告 `tool:`，而 Senate 這側也不該 spawn python）——
//   宣告了 `tool:` 而沒有 `cmd_steps` 的活動，op=step 會擋並說「python 工具步驟已不支援 —— 改成 cmd_steps」。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Session;

namespace SCP.Core.FreeTime
{
    public static class SCP_FreeTimeActivityOps
    {
        static string Cmd(string iTail) => SCP_CmdRegistry.Invoke(iTail);

        /// <summary>
        /// 進入點：守衛（真的在自由時間嗎／活動不綁場次嗎）→ 時間欄 → 分派到 op。
        /// </summary>
        public static SCP_CmdResult Run(SCP_FreeTimeContext iCtx, string iOp, string iPersona, string iActivity, string iStep,
                                        string iStepArgs, string iBody, bool iFollowedDice)
        {
            string aPath = iCtx.PayloadPath(iPersona, "activity");
            var aR = new StringBuilder();
            aR.AppendLine($"# FreeTimeActivity op={iOp} persona={iPersona}  ts=`{DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture)}`（本地時間）");
            aR.AppendLine();

            SCP_FreeTimeSession? aSession = iCtx.LoadSession(iPersona);
            DateTime aNow = DateTime.Now;
            DateTime? aEnd = aSession != null ? SCP_ActivitySession.ParseIsoToLocal(aSession.end_ts) : null;

            // ── 不綁自由時間的活動（md `needs_session: false`，Tim 2026-09-11）──
            // ⚠ 判準讀**md 的宣告**，⛔ 不在這裡寫 `if (id == "chess")`。
            // ⚠ 豁免要先知道「是哪個活動」：沒有 session 時 activity 無處可 fallback ⇒ 沒帶就解不開 ⇒ 照舊擋並印出口。
            //   ⛔ 不猜活動 —— 猜錯會把「下棋」記成「閱讀」，而帳面上看不出來。
            string aWantId = iActivity.Trim();
            if (aWantId.Length == 0 && aSession != null) aWantId = (aSession.activity ?? "").Trim();
            bool aSessionFree = false;
            if (aWantId.Length > 0)
            {
                SCP_FreeTimeActivity? aAny = iCtx.FindActivity(aWantId, false);
                if (aAny != null) aSessionFree = !aAny.NeedsSession;
            }

            // ── 守衛：session 必須存在且尚未收工 ──
            // ⚠ 判準刻意**不是** IsRunningAt —— 那條含「已過 end_ts」，而截止是**軟的**。拿它當守衛的話，
            //   逾時那一刻起 op=done 進不來 ⇒ 截止後收筆的活動在帳上永遠只能是「放棄了」
            //   （🩸 summit 2026-08-31 12:10:21 棋局收筆被擋，而同一份回傳檔抬頭還印著「軟截止」）。
            // ⇒ 只擋兩種真的不能做事的狀態：沒有 session／已經收工。逾時但仍 active ⇒ **放行**並明講已逾時。
            if ((aSession == null || !aSession.active) && !aSessionFree)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine(aSession == null
                    ? "- reason: 沒有自由時間 session"
                    : $"- reason: session 已收工（{(string.IsNullOrEmpty(aSession.end_reason) ? "未記原因" : aSession.end_reason)}）");
                if (aWantId.Length == 0)
                    aR.AppendLine("- ⚠ 而本次**沒帶 `--arg activity=<id>`** ⇒ 解不開是哪個活動，"
                                  + "所以連「這個活動不綁自由時間」都判不了（有些活動 md 宣告 `needs_session: false`）。"
                                  + "⇒ 帶上 activity 再試一次，它可能就過了。");
                aR.AppendLine($"- exit①: 開新場 → {Cmd("free-time --arg step=start --arg persona=" + iPersona + " --arg until=<HH:mm>")}");
                aR.AppendLine($"- exit②: 過期殘留要結算 → {Cmd("free-time --arg step=next --arg persona=" + iPersona)}（它會宣布收工）");
                return SCP_FreeTimeFlow.Blocked(iCtx, aPath, aR, "free-time-activity blocked：不在自由時間中");
            }
            // 豁免生效時一律當「沒有場次」走 —— ⛔ 不用一個收工的場次冒充在場（activities_done 會加在已結算的場上）。
            if (aSessionFree && (aSession == null || !aSession.active)) aSession = null;

            bool aOvertime = aSession != null && aEnd.HasValue && aNow > aEnd.Value;
            int aRemain = aSession != null && aEnd.HasValue ? (int)Math.Max(0, (aEnd.Value - aNow).TotalMinutes) : 0;
            aR.AppendLine("## time（時間感由 Cmd 供給 —— 別自己心算）");
            // ⛔ 不印剩餘分鐘（Tim 2026-09-04）；逾時那半保留 —— 那是「時間到了」這個**狀態**，不是倒數。
            if (aSession == null)
            {
                aR.AppendLine($"- 當前時間: **{aNow:yyyy-MM-dd HH:mm}**　⭐ **本活動不綁自由時間**"
                              + $"（`{aWantId}` 的 md 宣告 `needs_session: false`）—— 沒有場次時鐘，做完就收。");
                aR.AppendLine("- ⚠ 本次**不計入場次統計**（`activities_done`／換骰輪次不動）；飢餓統計照記（那一份只吃 persona，不吃場次）。");
            }
            else
            {
                aR.AppendLine(aOvertime
                    ? $"- 當前時間: **{aNow:yyyy-MM-dd HH:mm}**　自由時間到: **{aSession.until_local}**　⏰ **時間到了**（軟截止 —— 手上這件做完就跑 step=next 收工，別再開新的）"
                    : $"- 當前時間: **{aNow:yyyy-MM-dd HH:mm}**　自由時間到: **{aSession.until_local}**　**時間還沒到** —— 挑下一項活動");
                aR.AppendLine($"- 本場換骰 {aSession.rounds} 輪｜活動實作 {aSession.activities_done} 件");
            }
            aR.AppendLine();

            switch (iOp)
            {
                case "pick": return OpPick(iCtx, iPersona, aSession, iActivity, iBody.Trim(), iFollowedDice, aRemain, aR, aPath);
                case "step": return OpStep(iCtx, iPersona, aSession, iActivity, iStep.Trim(), iStepArgs.Trim(), aR, aPath);
                default: return OpDone(iCtx, iPersona, aSession, iBody.Trim(), aR, aPath);
            }
        }

        // ===========================================================
        // 區塊職責：op=pick —— 選一件活動，回傳**它怎麼執行**。
        // 物理意義：執行方式取自活動 md 的 frontmatter（`how`）與 md 路徑本身，**不在本檔另建一張對照表** ——
        //          兩份清單漂移時，症狀是「Cmd 說這樣跑、md 說那樣跑」，而兩邊都不會報錯。
        // 數值影響：session.activity／activities_done 遞增（活動層是唯一寫入端）；飢餓統計記一筆；發一則開場宣告。
        // ===========================================================
        static SCP_CmdResult OpPick(SCP_FreeTimeContext iCtx, string iPersona, SCP_FreeTimeSession? ioSession, string iActivity,
                                    string iBody, bool iFollowedDice, int iRemain, StringBuilder ioR, string iPath)
        {
            string aWant = iActivity.Trim().ToLowerInvariant();
            SCP_FreeTimeActivity? aHit = aWant.Length > 0 ? iCtx.FindActivity(aWant, true) : null;
            if (aHit == null)
            {
                // 不猜活動 —— 猜錯會把「下棋」記成「閱讀」，而帳面上看不出來。
                ioR.AppendLine("## blocked");
                ioR.AppendLine(aWant.Length == 0 ? "- reason: 沒給 activity" : $"- reason: 找不到活動 id '{aWant}'");
                ioR.AppendLine("- 可用的 id（掃活動 md 得來，不是寫死的清單）：");
                foreach (var a in iCtx.Activities) if (a.Enabled) ioR.AppendLine($"  - `{a.Id}` — {a.Name}");
                ioR.AppendLine($"- 用法: {Cmd("free-time-activity --arg op=pick --arg persona=" + iPersona + " --arg activity=<id>")}");
                return SCP_FreeTimeFlow.Blocked(iCtx, iPath, ioR, "op=pick blocked：活動 id 無效");
            }

            // ⚠ 不綁自由時間的活動進來時 ioSession 是 null ⇒ **不寫場次計數器**（⛔ 不新造一個假場次來裝）。
            bool aSessionSaved = true;
            if (ioSession != null)
            {
                ioSession.activity = aHit.Id;
                ioSession.activities_done += 1;
                aSessionSaved = iCtx.SaveSession(iPersona, ioSession);
            }
            // 飢餓統計的唯一寫入端就是這裡（Tim 2026-08-24）。⚠ **骰面出現不算被選**。
            int aPicks = SCP_FreeTimeStatsIO.RecordPick(iCtx.Letters, iPersona, aHit.Id, out string aPickErr);

            // 開場宣告（跟骰／未跟骰要可觀測）
            var aPost = new StringBuilder();
            aPost.AppendLine($"▶️ 自由時間開做：**{aHit.Name}**" + (iFollowedDice ? "" : "（**本輪未跟骰** —— 自由意志優先）"));
            if (iBody.Length > 0) { aPost.AppendLine(); aPost.AppendLine(iBody); }
            SCP_FreeTimePostResult aSent = iCtx.Post(iPersona, aPost.ToString(), "activity-pick");

            ioR.AppendLine($"## 已選：**{aHit.Name}**（id `{aHit.Id}`）");
            ioR.AppendLine($"- 跟骰: {(iFollowedDice ? "是" : "**否 —— 已在宣告註明未跟骰**")}");
            if (!aSessionSaved) ioR.AppendLine("- ⚠ 選擇**寫不進 session 檔**（activities_done 沒有加上去）");
            ioR.AppendLine(aPicks < 0
                ? $"- ⚠ 活動統計**寫入失敗**（本次選擇沒被記進飢餓度）：{aPickErr}"
                : $"- 📊 這件活動累計做過 **{aPicks} 次**（飢餓度已歸零）");
            ioR.AppendLine($"- 開場宣告: {aSent.Describe("未發（best-effort）")}");
            if (aHit.MinMinutes > 0 && ioSession != null && iRemain < aHit.MinMinutes)
            {
                // 不擋 —— 截止是軟的；但要說清楚，別讓人以為系統認可這個選擇沒有代價。
                ioR.AppendLine($"- ⏳ **本場時間可能不夠**（建議 ≥{aHit.MinMinutes} 分）—— 沒擋你，但別怪骰子");
            }
            ioR.AppendLine();
            ioR.AppendLine("## 怎麼執行（取自活動 md 的 frontmatter，不是本 Cmd 另編的）");
            ioR.AppendLine($"- {(string.IsNullOrEmpty(aHit.How) ? "（該 md 沒填 how）" : aHit.How)}");
            ioR.AppendLine($"- 📄 細節全文：`{aHit.Path}`");
            ioR.AppendLine();
            // ⚠ 2026-08-18 實跑時補上 —— 原本只指 op=done，漏了 op=step。漏的後果不是報錯，是**代跑能力隱形**。
            ioR.AppendLine("## ▶ 下一步");
            if (aHit.HasCmdRunner)
            {
                ioR.AppendLine("**本活動支援 Cmd 代跑一步**（in-process 派遣 `cmd_steps` 宣告的 cmd）—— 一步一步來，每一步的回傳都會接上下一步：");
                ioR.AppendLine("```bash");
                ioR.AppendLine(Cmd("free-time-activity") + " \\");
                ioR.AppendLine($"    --arg op=step --arg persona={iPersona} --arg activity={aHit.Id} \\");
                ioR.AppendLine($"    --arg step=<{string.Join("|", aHit.Steps)}> --arg step_args=\"--arg k=v …\"");
                ioR.AppendLine("```");
                ioR.AppendLine($"- ⚠ 已改走 in-process cmd 的 step（宣告在 md 的 `cmd_steps`）：`{string.Join("` / `", aHit.CmdSteps)}`");
                ioR.AppendLine("  它們的 `step_args` 吃的是 **cmd 原生寫法** `--arg k=v`（⛔ 不是 `--flag value`），身分與資料根會自動補。");
                // 提示裡直接把身分寫好（Tim 2026-08-18）：本 Cmd 手上就有 persona，留空殼的話照著抄的人會漏掉身分。
                if (aHit.CmdPersonaArg.Length > 0 || aHit.StepsNeedPersona.Count > 0)
                    ioR.AppendLine($"- ℹ 身分（`--arg {(aHit.CmdPersonaArg.Length > 0 ? aHit.CmdPersonaArg.TrimStart('-') : "<persona 參數>")}={iPersona}`）"
                                   + "需要時由 op=step **自動補**（宣告在 md 的 `steps_need_persona`／`cmd_persona_arg`）—— 自己帶也可以，帶了就以你帶的為準。");
                ioR.AppendLine("- 也可以自己直接跑那支 cmd —— 但走 op=step 的話輸出會併進回傳檔，流程不會斷。");
            }
            else if (aHit.Tool.Length > 0)
            {
                ioR.AppendLine($"本活動宣告了 python 工具 `{aHit.Tool}` 但沒有 `cmd_steps` —— **python 工具步驟已不支援**（op=step 會擋）。"
                               + $"照上面的方式自己跑；要接代跑請在 `{aHit.Path}` 的 frontmatter 改成 `cmd_steps:`。");
            }
            else
            {
                ioR.AppendLine($"本活動**尚未支援 Cmd 代跑** —— 照上面的方式自己跑（要接的話在 `{aHit.Path}` 的 frontmatter 加 `steps:` / `cmd_steps:`）。");
            }
            ioR.AppendLine();
            ioR.AppendLine("這件活動告一段落再跑這行（**不要直接跳去換骰**）：");
            ioR.AppendLine("```bash");
            ioR.AppendLine(Cmd("free-time-activity") + " \\");
            ioR.AppendLine($"    --arg op=done --arg persona={iPersona} [--arg-file body=<一句心得／收筆>]");
            ioR.AppendLine("```");
            ioR.AppendLine("- 走 op=done 而不是直接換骰，是為了讓「做完了」跟「放棄了」在帳上不同形。");
            SCP_FreeTimeFlow.AppendTail(ioR, iCtx);

            var aRes = SCP_CmdResult.Success($"✓ free-time-activity op=pick：**{aHit.Name}**（`{aHit.Id}`）　開場宣告 {aSent.Describe("未發")}");
            SCP_FreeTimeFlow.Write(aRes, iPath, ioR);
            return aRes.AddValue("activity", aHit.Id)
                       .AddValue("picks", aPicks.ToString(CultureInfo.InvariantCulture))
                       .AddValue("activities_done", (ioSession?.activities_done ?? 0).ToString(CultureInfo.InvariantCulture))
                       .AddValue("session_bound", ioSession != null ? "1" : "0")
                       .AddValue("post_seq", aSent.Seq.ToString(CultureInfo.InvariantCulture));
        }

        // ===========================================================
        // 區塊職責：op=step —— **代跑活動的一步**（in-process 派遣），回傳結果並接上下一步。
        // 邊界（刻意的）：
        //   - **白名單**：step 必須在該活動 md 的 `steps` 裡。`steps` 空 ⇒ 拒跑並指回 op=pick ——「還沒接」與「壞掉」要長得不一樣。
        //   - **輸出原樣搬**（Lines／Outputs／Values 三段），不由本層改寫 —— 工具已經分好的區別，任何重新措辭都可能把它磨平。
        //   - 🩸 TASK-0073：那一步失敗 ⇒ **這一筆 Cmd 就是失敗**（回傳檔照寫完，寫完才回非零）。
        // 數值影響：派遣一支 SCP cmd、寫一份回傳檔；**不動 activities_done**（那在 pick 記）。
        // ===========================================================
        static SCP_CmdResult OpStep(SCP_FreeTimeContext iCtx, string iPersona, SCP_FreeTimeSession? iSession, string iActivity,
                                    string iStep, string iStepArgs, StringBuilder ioR, string iPath)
        {
            string aWant = iActivity.Trim().ToLowerInvariant();
            // iSession 為 null（不綁自由時間的活動）⇒ 沒有場次可 fallback，activity 一定是呼叫端顯式帶的（閘那邊已要求過）。
            if (aWant.Length == 0 && iSession != null) aWant = (iSession.activity ?? "").Trim().ToLowerInvariant();
            SCP_FreeTimeActivity? aHit = aWant.Length > 0 ? iCtx.FindActivity(aWant, true) : null;
            if (aHit == null)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine($"- reason: 找不到活動 id '{aWant}'（op=step 要 --arg activity=，或先跑 op=pick 記錄選擇）");
                return SCP_FreeTimeFlow.Blocked(iCtx, iPath, ioR, "op=step blocked：活動無效");
            }
            if (!aHit.HasCmdRunner)
            {
                // 還沒接 ≠ 壞掉；python 路已死 ≠ 還沒接。三種各自一句。
                ioR.AppendLine("## blocked");
                if (aHit.Tool.Length > 0 && aHit.CmdSteps.Count == 0)
                    ioR.AppendLine($"- reason: 活動 **{aHit.Name}** 宣告的是 python 工具 `{aHit.Tool}` —— **python 工具步驟已不支援 —— 改成 cmd_steps**");
                else
                    ioR.AppendLine($"- reason: 活動 **{aHit.Name}** 尚未支援代跑（md frontmatter 沒有 `cmd_steps` 或沒有 `steps`）");
                ioR.AppendLine($"- exit: 走 op=pick 取得指令自己跑 → {Cmd("free-time-activity --arg op=pick --arg persona=" + iPersona + " --arg activity=" + aHit.Id)}");
                ioR.AppendLine($"- 要接的話：在 `{aHit.Path}` 的 frontmatter 加 `steps:` 與 `cmd_steps:`（`<step>=<cmd>:<op>`）");
                return SCP_FreeTimeFlow.Blocked(iCtx, iPath, ioR, $"op=step blocked：{aHit.Id} 未支援代跑");
            }
            bool aAllowed = false;
            foreach (string aS in aHit.Steps)
                if (string.Equals(aS, iStep, StringComparison.OrdinalIgnoreCase)) { aAllowed = true; break; }
            if (!aAllowed)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine(iStep.Length == 0 ? "- reason: 沒給 step" : $"- reason: step '{iStep}' 不在白名單內");
                ioR.AppendLine($"- 可用 step（取自 `{aHit.Path}` 的 `steps`）：{string.Join(" / ", aHit.Steps)}");
                return SCP_FreeTimeFlow.Blocked(iCtx, iPath, ioR, "op=step blocked：step 不在白名單");
            }
            // ⚠ 宣告壞了**不回退舊路** —— 靜默回退的症狀是「我以為它改走 C# 了」，而畫面跟成功時一樣。
            var (aRouted, aRouteCmd, aRouteOp, aRouteErr) = aHit.CmdRouteForStep(iStep);
            if (aRouteErr.Length > 0 || !aRouted)
            {
                ioR.AppendLine("## blocked");
                ioR.AppendLine(aRouteErr.Length > 0
                    ? $"- reason: `cmd_steps` 宣告壞了：{aRouteErr}"
                    : $"- reason: step `{iStep}` 在白名單裡、但**沒有 `cmd_steps` 路由** —— python 工具步驟已不支援 —— 改成 cmd_steps");
                ioR.AppendLine($"- 要修：`{aHit.Path}` 的 frontmatter（`cmd_steps: {iStep}=<cmd>:<op>`）");
                return SCP_FreeTimeFlow.Blocked(iCtx, iPath, ioR, $"op=step blocked：{aHit.Id}/{iStep} 沒有可用的 cmd 路由");
            }

            var aRun = RunCmdStep(iCtx, aHit, aRouteCmd, aRouteOp, iStep, iStepArgs, iPersona);

            ioR.AppendLine($"## {aHit.Name} — step `{iStep}`　{(aRun.Ok ? "✅ 成功" : "❌ 失敗")}");
            ioR.AppendLine($"- 路線: **in-process cmd**（`{aRouteCmd}`，`{(aHit.CmdStepArg.Length > 0 ? aHit.CmdStepArg : "op")}={aRouteOp}`）"
                           + ("　參數: `" + iStepArgs + "`").TrimEnd() + "　⛔ 不經過 shell，也不 spawn 任何行程");
            // ⚠ 印**這一步實際用的**參數名，不是活動層預設（同一支工具的旗標可能逐 step 不同 —— 2026-08-18 實測印錯過一次）。
            if (aRun.Injected)
                ioR.AppendLine($"- ℹ 自動補上身分：`--arg {aHit.PersonaArgForStep(iStep)}={iPersona}`（宣告在 md 的 `steps_need_persona`／`cmd_persona_arg`）");
            if (!aRun.Ok) ioR.AppendLine($"- 錯誤: {aRun.Err}");
            ioR.AppendLine();
            ioR.AppendLine("### 工具輸出（原樣，未經改寫）");
            ioR.AppendLine("```");
            ioR.AppendLine(string.IsNullOrWhiteSpace(aRun.Stdout) ? "(無輸出)" : aRun.Stdout.TrimEnd());
            ioR.AppendLine("```");

            if (!aRun.Ok)
            {
                ioR.AppendLine();
                ioR.AppendLine("## ▶ 下一步（這一步**沒有成功**）");
                ioR.AppendLine("- 先看上面的輸出／錯誤行 —— 那是工具自己說的話，本層原樣轉交");
                ioR.AppendLine("- 參數要調 → 再跑一次 op=step（換 `--arg step_args=`）");
                ioR.AppendLine($"- 這件活動要收 → `{Cmd("free-time-activity --arg op=done --arg persona=" + iPersona)} [--arg-file body=<一句心得>]`");
                SCP_FreeTimeFlow.AppendTail(ioR, iCtx);
                var aFail = SCP_CmdResult.Fail(SCP_FreeTimeFlow.ExitFailed,
                    $"✗ free-time-activity op=step 失敗：{aHit.Id}/{iStep} —— {aRun.Err}（詳見回傳檔）");
                SCP_FreeTimeFlow.Write(aFail, iPath, ioR);
                return aFail.AddValue("activity", aHit.Id).AddValue("step", iStep)
                            .AddValue("step_exit", aRun.ExitCode.ToString(CultureInfo.InvariantCulture));
            }

            ioR.AppendLine();
            ioR.AppendLine("## ▶ 下一步（自由時間**進行中** —— 時間還沒到，挑下一項活動）");
            ioR.AppendLine("- 這件活動還要再走一步 → 再跑一次 op=step（換 `--arg step=` / `--arg step_args=`）");
            ioR.AppendLine($"- 這件活動告一段落 → `{Cmd("free-time-activity --arg op=done --arg persona=" + iPersona)} [--arg-file body=<一句心得>]`");
            ioR.AppendLine("- ⚠ 別直接跳去 step=next —— 走 op=done 才留下「做完了」的紀錄（跟「放棄了」不同形）。");
            SCP_FreeTimeFlow.AppendTail(ioR, iCtx);
            var aRes = SCP_CmdResult.Success($"✓ free-time-activity op=step：{aHit.Id}/{iStep}（{aRouteCmd}:{aRouteOp}）");
            SCP_FreeTimeFlow.Write(aRes, iPath, ioR);
            return aRes.AddValue("activity", aHit.Id).AddValue("step", iStep).AddValue("step_exit", "0");
        }

        // ===========================================================
        // 區塊職責：把一步交給 SCP_Core 的指令系統 —— **in-process，不 spawn**（逐格照 Unity `RunCmdStep`）。
        // 物理意義：in-process 的價值不只是快：**它不經過 shell 那一層**，引號不必同時扮「綁詞」與「當內容」兩個角色。
        // ⛔ `step_args` 在這條路上是 **cmd 原生寫法**（`--arg k=v`），**不做 `--flag value` 翻譯** ——
        //   翻譯要猜 kebab→snake 與引號規則，而猜錯的那一次我不會知道；認不得的 token 一律當場擋下（⛔ 不靜默丟掉）。
        // ===========================================================
        static (bool Ok, string Stdout, string Err, bool Injected, int ExitCode) RunCmdStep(SCP_FreeTimeContext iCtx,
            SCP_FreeTimeActivity iActivity, string iCmd, string iOp, string iStep, string iStepArgs, string iPersona)
        {
            SCP_Cmd? aTarget = SCP_CmdRegistry.Find(iCmd);
            if (aTarget == null) return (false, "", $"認不得的 cmd '{iCmd}'（宣告在 md 的 `cmd_steps`）", false, 2);

            var aArgs = new Dictionary<string, string>(StringComparer.Ordinal);
            List<string> aToks = SplitStepArgs(iStepArgs);
            for (int i = 0; i < aToks.Count; i++)
            {
                if (aToks[i] != "--arg")
                    return (false, "", $"這一步走 cmd 路線，`step_args` 只吃 `--arg k=v`（收到 `{aToks[i]}`）", false, 2);
                if (i + 1 >= aToks.Count) return (false, "", "`--arg` 後面沒有 `k=v`", false, 2);
                string aPair = aToks[++i];
                int aEq = aPair.IndexOf('=');
                if (aEq <= 0) return (false, "", $"`--arg` 要 `k=v`（收到 `{aPair}`）", false, 2);
                aArgs[aPair.Substring(0, aEq)] = aPair.Substring(aEq + 1);
            }

            // 選子命令的那個參數（預設 `op`）—— 呼叫端顯式給了就不覆蓋（顯式優先）。
            string aStepArgName = iActivity.CmdStepArg.Length > 0 ? iActivity.CmdStepArg : "op";
            if (!aArgs.ContainsKey(aStepArgName)) aArgs[aStepArgName] = iOp;

            // 資料根：**只在那支 cmd 真的宣告了 `data_root` 時才注入** —— 無條件塞會讓沒宣告它的 cmd 在預檢那關被擋
            //   （而那個失敗看起來會像「呼叫端打錯參數」）。
            bool aDeclaresDataRoot = false;
            foreach (SCP_CmdArgSpec aSpec in aTarget.ArgSpecs)
                if (aSpec.Name == "data_root") { aDeclaresDataRoot = true; break; }
            if (aDeclaresDataRoot && !aArgs.ContainsKey("data_root")) aArgs["data_root"] = iCtx.DataRootRaw;

            bool aInjected = false;
            string aPersonaArg = iActivity.PersonaArgForStep(iStep);
            if (aPersonaArg.Length > 0 && !aArgs.ContainsKey(aPersonaArg))
            {
                aArgs[aPersonaArg] = iPersona;
                aInjected = true;
            }

            SCP_CmdResult aResult;
            try { aResult = SCP_CmdRegistry.Dispatch(iCmd, aArgs); }
            catch (Exception e)
            {
                // Dispatch 自己會接住 Cmd 丟的例外（回 exit 70）⇒ 走到這裡代表是**派遣層**炸了。
                return (false, "", $"dispatch exception: {e.GetType().Name}: {e.Message}", aInjected, 70);
            }

            // 輸出照抄 CLI 那側的三段（Lines／Outputs／Values）—— ⛔ 不發明第二種格式，也不吞掉 Values（那些常常就是下一步要接的讀數）。
            var aOut = new StringBuilder();
            foreach (string aLine in aResult.Lines) aOut.AppendLine(aLine);
            foreach (string aOutput in aResult.Outputs) aOut.AppendLine($"📄 回傳檔：{aOutput}");
            foreach (KeyValuePair<string, string> aValue in aResult.Values) aOut.AppendLine($"🔢 {aValue.Key} = {aValue.Value}");
            string aText = aOut.ToString().TrimEnd();
            if (!aResult.Ok) return (false, aText, $"exit={aResult.ExitCode}", aInjected, aResult.ExitCode);
            return (true, aText, "", aInjected, 0);
        }

        // ===========================================================
        // 區塊職責：把 `step_args` 這一行字切成 token（逐格照 Unity `SplitStepArgs`）。
        // 規則兩條，分別對應引號的兩種身分：
        //   ① **引號在 token 開頭 ＝ 綁詞用**：吃到配對的收尾引號為止，**引號本身不進內容**。
        //   ② **引號在 token 中間 ＝ 內容**（JSON 語法）：原樣保留，但**它一樣會讓引號內的空白不切詞**。
        // 🩸 第三個 case（`[{"k":"值 含空白"}]`）是第二次才補上的：單元驗證只證明我想到的 case，端到端才會餵我沒想到的那個。
        // 數值影響：純字串。未配對的引號 ⇒ 讀到行尾（不丟例外）；空字串 ⇒ 空清單（不產生一個空 token）。
        // ===========================================================
        public static List<string> SplitStepArgs(string iRaw)
        {
            var aOut = new List<string>();
            if (string.IsNullOrWhiteSpace(iRaw)) return aOut;
            int i = 0;
            while (i < iRaw.Length)
            {
                while (i < iRaw.Length && char.IsWhiteSpace(iRaw[i])) i++;
                if (i >= iRaw.Length) break;
                var aTok = new StringBuilder();
                if (iRaw[i] == '"')
                {
                    i++;
                    while (i < iRaw.Length && iRaw[i] != '"') { aTok.Append(iRaw[i]); i++; }
                    if (i < iRaw.Length) i++;   // 收尾引號
                }
                else
                {
                    bool aInQuote = false;
                    while (i < iRaw.Length && (aInQuote || !char.IsWhiteSpace(iRaw[i])))
                    {
                        if (iRaw[i] == '"') aInQuote = !aInQuote;
                        aTok.Append(iRaw[i]);
                        i++;
                    }
                }
                if (aTok.Length > 0) aOut.Add(aTok.ToString());
            }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：op=done —— 收一件活動，把下一步指回換骰。
        // 物理意義：這一步存在的唯一理由是**接住流程** —— 活動做完那一刻是最容易斷線的位置。
        // 數值影響：發一則收筆宣告（可選 body）；**不改 activities_done**（那在 pick 就記了 —— 再加一次會算兩遍）。
        // ===========================================================
        static SCP_CmdResult OpDone(SCP_FreeTimeContext iCtx, string iPersona, SCP_FreeTimeSession? iSession, string iBody,
                                    StringBuilder ioR, string iPath)
        {
            string aWhat = iSession == null
                ? "（不綁自由時間的活動 —— 沒有場次紀錄）"
                : (string.IsNullOrEmpty(iSession.activity) ? "（本場沒有經 op=pick 記錄的活動）" : iSession.activity);
            var aPost = new StringBuilder();
            aPost.AppendLine($"⏹ [{iPersona} 大小姐] 活動收筆：**{aWhat}**");
            if (iBody.Length > 0) { aPost.AppendLine(); aPost.AppendLine(iBody); }
            SCP_FreeTimePostResult aSent = iCtx.Post(iPersona, aPost.ToString(), "activity-done");

            ioR.AppendLine($"## 已收筆：{aWhat}");
            ioR.AppendLine($"- 收筆宣告: {aSent.Describe("未發（best-effort）")}");
            if (iSession != null && string.IsNullOrEmpty(iSession.activity))
                ioR.AppendLine("- ⚠ 本場沒有經 `op=pick` 選過活動 —— 這則收筆記在帳上，但沒有對應的開工紀錄。");
            ioR.AppendLine();
            // ⚠ 不綁自由時間時**不要指去換骰** —— 那條路會擋（沒有場次），而一個指向死路的「下一步」比沒有下一步更貴。
            if (iSession == null)
            {
                ioR.AppendLine("## ▶ 下一步（本活動不綁自由時間 —— 沒有換骰這一步）");
                ioR.AppendLine("- 想再做一輪就再跑一次 `op=step`；⛔ 不要跑 `free-time step=next`（沒有場次，它會擋）。");
                ioR.AppendLine("- 要進自由時間另算：`" + Cmd("free-time --arg step=start --arg persona=" + iPersona + " --arg until=<HH:mm>") + "`");
            }
            else
            {
                ioR.AppendLine("## ▶ 下一步（換骰 —— **順便讀未讀訊息、順便跟同事講話**）");
                ioR.AppendLine("```bash");
                ioR.AppendLine(Cmd("free-time --arg step=next --arg persona=" + iPersona) + " [--arg-file body=<想跟同事說的話>]");
                ioR.AppendLine("```");
                ioR.AppendLine("- 換骰的回傳檔**同一份**就含：未讀酒館訊息（會推已讀游標）＋ 新骰面 ＋ 剩餘時間。");
                ioR.AppendLine("- **截止是軟的**：時間到不打斷進行中的活動；到期時換骰那一步會自己宣布收工並結算。");
            }
            SCP_FreeTimeFlow.AppendTail(ioR, iCtx);
            var aRes = SCP_CmdResult.Success($"✓ free-time-activity op=done：{aWhat}　收筆宣告 {aSent.Describe("未發")}");
            SCP_FreeTimeFlow.Write(aRes, iPath, ioR);
            return aRes.AddValue("activity", iSession?.activity ?? "")
                       .AddValue("session_bound", iSession != null ? "1" : "0")
                       .AddValue("post_seq", aSent.Seq.ToString(CultureInfo.InvariantCulture));
        }
    }
}
