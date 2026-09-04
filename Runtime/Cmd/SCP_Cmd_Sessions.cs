// 區塊職責：`cmd sessions` —— 活動 session 的查詢與關場（**原生**，查詢不需要 Unity）。
// 物理意義：這是 UCL 那側 `UCL_SessionAdminPage` 與 `Cmd_SessionStatus` 的資料面搬家（TASK-0127 ⑤）。
//           讀是純讀、走 SCP_ActivitySessionStore；**關場委派**給登記了 gateway 的那一端
//           （Senate 側＝委派回 Editor，因為結算就是金流、金流不搬 —— TASK-0106 Tim 拍 B 不動）。
// 數值影響：`op=list|show` 一個位元組都不寫；`op=close` 會關掉一場（且可能發薪）⇒ 要 `confirm=1`。
//
// ⚠ 空清單的兩種意思本 Cmd **不合成一句**：「這個人沒有進行中的場」與「這個 kind 沒被登記過所以沒看」
//   是不同的答案，後者被印成前者的話，讀的人會拿它當「他現在有空」的證據。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using SCP.Core.Paths;
using SCP.Core.Session;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Sessions : SCP_Cmd
    {
        public override string Name => "sessions";

        public override string Summary => "活動 session：列出誰在哪一場／看某人的場／關掉過期殘留（關場委派給 Editor）";

        public override string Details =>
            "資料源＝`<data_root>/sessions/<persona>.json`（**一人一檔位**，kind 是欄位不是路徑段）。\n"
            + "⭐ 三種狀態分開印，不合併：🟢 進行中／⚠ 殘留（active 但已過 end_ts）／⚪ 已收工。\n"
            + "⚠ `op=close` **只收殘留**：進行中的場要走該 kind 自己的收工步驟（那裡才有收工公告與同場者判定）。\n"
            + "⚠ 關場**不是本層做的** —— 交給登記了 close gateway 的那一端（Senate ⇒ 委派 Editor 的 `SessionClose`）。\n"
            + "   沒有登記 gateway 時**只翻三欄、不結算**，而且會明說（那是降級，不是成功）。\n"
            + "⚠ 「沒查到」不等於「他不在任何 session」：未登記的 kind 本層看不到，回報一律附掃描範圍。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("sessions");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            // ⚠ 仍是 iRequired：**本層真的需要它**（它不會自己去讀任何設定檔）。
            //   而呼叫端不必每次手打 —— senate CLI 沒給時會用唯一那格設定（`SCP_PathId.AgentCommandsRoot`）
            //   補上並**印出來**。⇒ 「必填」講的是這一層的需求，「可以不打」講的是宿主的便利，兩件事。
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）"
                + "—— senate CLI 沒給時用「路徑管理」頁那一格補上並印出來", iRequired: true),
            new SCP_CmdArgSpec("op", "list（預設）| show | close"),
            new SCP_CmdArgSpec("target_persona", "show／close 要看／要關誰的場（⚠ 不猜身分）"),
            new SCP_CmdArgSpec("reason", "close 寫進 end_reason 的一句話（預設 closed-by-senate）"),
            new SCP_CmdArgSpec("confirm", "close 必填 1 —— 這會寫別人的 session 檔，觀影場還會發薪"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            var aRoot = new SCP_DataRoot(iArgs.Get("data_root"));
            string aOp = iArgs.Get("op");
            if (aOp.Length == 0) aOp = "list";
            string aTarget = iArgs.Get("target_persona").Trim();

            switch (aOp)
            {
                case "list": return OpList(aRoot);
                case "show": return OpShow(aRoot, aTarget);
                case "close": return OpClose(aRoot, aTarget, iArgs.Get("reason"), iArgs.Get("confirm") == "1");
                default:
                    return SCP_CmdResult.Fail(2, "✗ op 只吃 list|show|close（收到 '" + aOp + "'）");
            }
        }

        // ── list ──────────────────────────────────────────────────

        static SCP_CmdResult OpList(SCP_DataRoot iRoot)
        {
            var aProblems = new List<string>();
            List<SCP_ActivitySession> aAll = SCP_ActivitySessionStore.LoadAll(iRoot, aProblems);
            var aResult = SCP_CmdResult.Success();
            aResult.Lines.Add("## 活動 session（" + SCP_ActivitySessionStore.Dir(iRoot) + "）");
            if (aAll.Count == 0)
            {
                aResult.Lines.Add("・沒有任何 session 檔");
            }
            int aRunning = 0, aStale = 0, aClosed = 0;
            DateTime aNow = DateTime.Now;
            for (int i = 0; i < aAll.Count; ++i)
            {
                SCP_ActivitySession aS = aAll[i];
                bool aRun = aS.IsRunningAt(aNow, out DateTime? aEnd);
                string aState;
                if (aRun) { aState = "🟢 進行中"; aRunning++; }
                else if (aS.active) { aState = "⚠ 殘留（active 但已過 end_ts）"; aStale++; }
                else { aState = "⚪ 已收工"; aClosed++; }
                string aKind = aS.kind.Length == 0 ? "(未標 kind)" : aS.kind;
                if (!SCP_ActivitySessionKind.IsRegistered(aS.kind)) aKind += "（未登記 —— 本層不當它是現行 session）";
                aResult.Lines.Add("・" + Pad(aS.persona, 12) + " " + Pad(aKind, 16) + " " + aState
                                  + "　收工時刻 " + (aS.until_local.Length == 0 ? "—" : aS.until_local)
                                  + (aS.end_reason.Length == 0 ? "" : "　reason=" + aS.end_reason));
            }
            for (int i = 0; i < aProblems.Count; ++i) aResult.Lines.Add("⚠ " + aProblems[i]);
            // 掃描範圍要印出來：「沒查到」與「不在」是兩件事。
            aResult.Lines.Add("· 掃描範圍（已登記的 kind）：" + string.Join(" / ", SCP_ActivitySessionKind.Kinds));
            aResult.AddValue("sessions", aAll.Count.ToString());
            aResult.AddValue("running", aRunning.ToString());
            aResult.AddValue("stale", aStale.ToString());
            aResult.AddValue("closed", aClosed.ToString());
            aResult.AddValue("problems", aProblems.Count.ToString());
            return aResult;
        }

        // ── show ──────────────────────────────────────────────────

        static SCP_CmdResult OpShow(SCP_DataRoot iRoot, string iTarget)
        {
            if (iTarget.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ op=show 需要 --arg target_persona=<誰>（不猜身分）");
            SCP_ActivitySession? aS = SCP_ActivitySessionStore.Load(iRoot, iTarget);
            var aResult = SCP_CmdResult.Success();
            if (aS == null)
            {
                aResult.Lines.Add("・`" + iTarget + "` 沒有 session 檔（或讀不回來）");
                aResult.Lines.Add("· 掃描範圍：" + string.Join(" / ", SCP_ActivitySessionKind.Kinds)
                                  + "　⚠ 「沒查到」不等於「他不在任何 session」");
                aResult.AddValue("found", "0");
                return aResult;
            }
            bool aRun = aS.IsRunningAt(DateTime.Now, out DateTime? aEnd);
            aResult.Lines.Add("・persona: " + aS.persona);
            aResult.Lines.Add("・kind: " + (aS.kind.Length == 0 ? "(未標)" : aS.kind)
                              + (SCP_ActivitySessionKind.IsRegistered(aS.kind) ? "" : "（未登記）"));
            aResult.Lines.Add("・session_id: " + aS.session_id);
            aResult.Lines.Add("・開場: " + aS.start_ts + "　預定收工: " + aS.end_ts + "（" + aS.until_local + "）");
            aResult.Lines.Add("・狀態: " + (aRun ? "🟢 進行中" : aS.active ? "⚠ 殘留" : "⚪ 已收工")
                              + (aEnd.HasValue ? "　end=" + aEnd.Value.ToString("yyyy-MM-dd HH:mm") : ""));
            if (!aS.active)
                aResult.Lines.Add("・收工: reason=" + aS.end_reason + "　ended_at=" + aS.ended_at);
            aResult.AddValue("found", "1");
            aResult.AddValue("kind", aS.kind);
            aResult.AddValue("running", aRun ? "1" : "0");
            aResult.AddValue("stale", !aRun && aS.active ? "1" : "0");
            return aResult;
        }

        // ── close ─────────────────────────────────────────────────

        static SCP_CmdResult OpClose(SCP_DataRoot iRoot, string iTarget, string iReason, bool iConfirm)
        {
            if (iTarget.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ op=close 需要 --arg target_persona=<誰>（不猜 —— 猜錯會關掉別人的場）");
            string aReason = iReason.Trim().Length == 0 ? "closed-by-senate" : iReason.Trim();

            SCP_ActivitySession? aS = SCP_ActivitySessionStore.Load(iRoot, iTarget);
            if (aS == null)
                return SCP_CmdResult.Fail(1, "✗ `" + iTarget + "` 沒有 session 檔 ⇒ 沒有可關的場（掃描範圍："
                                             + string.Join(" / ", SCP_ActivitySessionKind.Kinds) + "）");

            bool aRun = aS.IsRunningAt(DateTime.Now, out DateTime? aEnd);
            if (aRun)
            {
                // 擋而指路：原因 ＋ 可以直接複製執行的出口（措辭原則：祈使句、指令附上、不解釋代價）。
                var aBlocked = SCP_CmdResult.Fail(1, "✗ 這場**還在進行中**（至 "
                                                     + (aEnd.HasValue ? aEnd.Value.ToString("HH:mm") : "無截止")
                                                     + " 本地）⇒ 不從這裡關");
                aBlocked.Lines.Add("  出口：`senate ucmd run " + (aS.kind.Length == 0 ? "<那個 kind>" : aS.kind)
                                   + " --persona " + iTarget + " --arg step=end`");
                aBlocked.Lines.Add("  ⛔ 不給 force：正常收工還有**收工公告**與**同場者判定**，那些不是本 Cmd 做的事。");
                aBlocked.AddValue("blocked", "running");
                return aBlocked;
            }
            if (!aS.active)
            {
                var aNoop = SCP_CmdResult.Success("・這場已經收過工（reason=" + aS.end_reason
                                                  + "　ended_at=" + aS.ended_at + "）⇒ 未動作");
                aNoop.Lines.Add("  ⛔ 不重複結算 —— 重複發薪不會有人喊，而帳對不上時沒有人查得出是這裡。");
                aNoop.AddValue("closed", "0");
                aNoop.AddValue("noop", "already_closed");
                return aNoop;
            }
            if (!iConfirm)
            {
                var aNeed = SCP_CmdResult.Fail(2, "✗ 缺 confirm —— `" + iTarget + "` 有一場過期殘留的 "
                                                  + aS.kind + "（" + aS.session_id + "），可以收");
                aNeed.Lines.Add("  這一步會寫別人的 session 檔，觀影場還會**發薪** ⇒ 要顯式確認：");
                aNeed.Lines.Add("  `senate cmd sessions --arg op=close --arg target_persona=" + iTarget + " --arg confirm=1`");
                aNeed.AddValue("blocked", "need_confirm");
                return aNeed;
            }

            SCP_ActivitySessionCloseResult aClose =
                SCP_ActivitySessionStore.CloseWithSettlement(iRoot, iTarget, aS, aReason);

            var aRes = aClose.Closed ? SCP_CmdResult.Success() : SCP_CmdResult.Fail(1, "✗ 關場沒有落地");
            aRes.Lines.Add("・關場路徑："
                           + (aClose.HasHandler
                               ? "gateway（" + aS.kind + "）—— 那一端連結算一起做"
                               : "**本層 base close**（這個 kind 沒有登記 gateway ⇒ 只翻三欄、**不結算**，明確降級）"));
            for (int i = 0; i < aClose.SettleLines.Count; ++i) aRes.Lines.Add("  " + aClose.SettleLines[i]);
            if (aClose.SettleError.Length > 0)
            {
                aRes.Lines.Add("⚠ 那一端回報失敗：" + aClose.SettleError);
                aRes.Lines.Add("  ⚠ 而「場關了沒」看下一行的**回讀**，不是看這一行 —— 兩本帳分開。");
            }
            // ⭐ 判準是回讀，不是 gateway 說什麼（它在另一個 process 裡）。
            aRes.Lines.Add("・回讀磁碟：active=" + (aClose.Closed ? "false ✅" : "true ❌（沒關成）"));
            aRes.AddValue("closed", aClose.Closed ? "1" : "0");
            aRes.AddValue("by_gateway", aClose.ClosedByGateway ? "1" : "0");
            aRes.AddValue("has_handler", aClose.HasHandler ? "1" : "0");
            return aRes;
        }

        static string Pad(string iText, int iWidth)
            => iText.Length >= iWidth ? iText : iText + new string(' ', iWidth - iText.Length);
    }
}
