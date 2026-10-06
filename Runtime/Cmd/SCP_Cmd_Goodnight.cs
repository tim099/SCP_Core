// 區塊職責：晚安流程的 `senate cmd` 入口 —— `goodnight-check` / `-portrait` / `-letter` / `-sleep` / `-logout`。
// 物理意義：邏輯本體在 `SCP_Goodnight`；本檔只做「參數 → 呼叫 → 落回傳檔」＋ sleep 的組裝（預檢 → 寫入 → 關場 → 廣播）。
//           與 `SCP_Goodnight` 住同一層 ⇒ 提示下一步一律用 `SCP_CmdRegistry.InvokeOf<型別>()`，指令改名不過時。
//           只有宿主才有的事走 `SCP_LocalRootsCmd.Host`：酒館寫入（含排隊）、Editor 在不在、把觀影場交給 Editor 結算。
// 數值影響：回傳檔 `letters/<P>/cmd/goodnight_<step>.md`；sleep／logout 刪 lock 與 now_status、關本人活動 session、發下線廣播。
// ⚠ 只有一段要 Editor：本人**進行中的觀影場**結算。Editor 沒開就跳過那一段並寫明，⛔ 不卡住晚安。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace SCP.Core.Cmd
{
    // ── ① check ──────────────────────────────────────────────────────

    public sealed class SCP_Cmd_GoodnightCheck : SCP_LocalRootsCmd
    {
        public override string Name => "goodnight-check";
        public override string Summary => "晚安①唯讀起手：待辦盤點＋酒館最後一眼＋Task 對帳";
        public override string Details =>
            "純讀：lock 狀態、酒館最近 10 筆（peek 不動游標）、Task 對帳（見叢引用／未關單／逾期認領／記憶連結／收工預告），\n"
            + "最後印人工收尾清單（portrait 與 letter 標 **required**，會實擋）。";
        public override string Example => SCP_CmdRegistry.InvokeOf<SCP_Cmd_GoodnightCheck>("--arg persona=Template");
        protected override string CliNextHint =>
            "照回傳檔的收尾清單走 → " + SCP_CmdRegistry.InvokeOf<SCP_Cmd_GoodnightPortrait>("--arg persona=<P> --arg about=<同事> --arg-file body=<檔>");
        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new List<SCP_CmdArgSpec>(MorningSpecs());

        protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
        {
            string p = iArgs.Get("persona").Trim();
            return SCP_GoodnightCmds.Finish(iRoots, p, "check", SCP_Goodnight.Check(iRoots, p), ioResult);
        }
    }

    // ── ② portrait ───────────────────────────────────────────────────

    public sealed class SCP_Cmd_GoodnightPortrait : SCP_LocalRootsCmd
    {
        public override string Name => "goodnight-portrait";
        public override string Summary => "晚安②見人畫像投遞（親筆），或顯式跳過";
        public override string Details =>
            "兩條路二擇一（**會擋 letter**）：\n"
            + "  · 畫一幅：about ＋ body（親筆公開層）必填；headline／private_body／affinity 選填。\n"
            + "    事實源寫進自己的 sketchbook，公開層投遞到對方的 portraits（私層不留痕跡）。about 必須是現有 persona。\n"
            + "  · 今夜不畫：skip_reason —— 理由會印進下線廣播。";
        public override string Example =>
            SCP_CmdRegistry.InvokeOf<SCP_Cmd_GoodnightPortrait>("--arg persona=Template --arg about=basecamp --arg headline=<標題> --arg-file body=D:/tmp/p.md");
        protected override string CliNextHint => SCP_CmdRegistry.InvokeOf<SCP_Cmd_GoodnightLetter>("--arg persona=<P> --arg-file letter_body=<檔>");
        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
        {
            get
            {
                var a = new List<SCP_CmdArgSpec>(MorningSpecs());
                a.Add(new SCP_CmdArgSpec("about", "畫誰（同事的 persona 名）"));
                a.Add(new SCP_CmdArgSpec("headline", "一句話標題"));
                a.Add(new SCP_CmdArgSpec("body", "公開層內文（**親筆**，工具不代筆）。長內文走 --arg-file"));
                a.Add(new SCP_CmdArgSpec("private_body", "私層內文（選填，只留在自己的 sketchbook）"));
                a.Add(new SCP_CmdArgSpec("affinity", "好感讀數，如 `11/在意`（選填）"));
                a.Add(new SCP_CmdArgSpec("skip_reason", "今夜不畫的理由（會印進下線廣播）"));
                return a;
            }
        }

        protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
        {
            string p = iArgs.Get("persona").Trim();
            return SCP_GoodnightCmds.Finish(iRoots, p, "portrait", SCP_Goodnight.Portrait(iRoots, p,
                iArgs.Get("about"), iArgs.Get("headline"), iArgs.Get("body"), iArgs.Get("private_body"),
                iArgs.Get("skip_reason"), iArgs.Get("affinity")), ioResult);
        }
    }

    // ── ③ letter ─────────────────────────────────────────────────────

    public sealed class SCP_Cmd_GoodnightLetter : SCP_LocalRootsCmd
    {
        public override string Name => "goodnight-letter";
        public override string Summary => "晚安③收尾信落檔（body 必須親筆）";
        public override string Details =>
            "寫 `wakes/<N>_<ts>.md`（N＝信數＋1）並同步 `_latest.md`。\n"
            + "⚠ 前置：今天已投遞畫像或顯式跳過（goodnight-portrait）。\n"
            + "⚠ 目標編號已有信就擋 —— 不覆寫（編號推導與磁碟不一致時，蓋掉舊信是最糟的結果）。";
        public override string Example => SCP_CmdRegistry.InvokeOf<SCP_Cmd_GoodnightLetter>("--arg persona=Template --arg-file letter_body=D:/tmp/letter.md");
        protected override string CliNextHint => SCP_CmdRegistry.InvokeOf<SCP_Cmd_GoodnightSleep>("--arg persona=<P> [--arg-file summary=<檔>]");
        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
        {
            get
            {
                var a = new List<SCP_CmdArgSpec>(MorningSpecs());
                a.Add(new SCP_CmdArgSpec("letter_body", "寫給未來自己的收尾信（**親筆**）。長內文走 --arg-file", iRequired: true));
                return a;
            }
        }

        protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
        {
            string p = iArgs.Get("persona").Trim();
            return SCP_GoodnightCmds.Finish(iRoots, p, "letter", SCP_Goodnight.Letter(iRoots, p, iArgs.Get("letter_body")), ioResult);
        }
    }

    // ── ④ sleep ／ ⑤ logout ─────────────────────────────────────────

    public sealed class SCP_Cmd_GoodnightSleep : SCP_LocalRootsCmd
    {
        public override string Name => "goodnight-sleep";
        public override string Summary => "晚安④下線：收工閘→解鎖→關場→下線廣播（Editor 沒開也下得了線）";
        public override string Details => SCP_GoodnightCmds.SleepDetails
            + "\n⚠ **收工閘會實擋**：有未收工的單時非零退出。`skip_reason` 可以過閘 —— 理由寫進那幾張單的時間線並併入下線廣播。\n"
            + "⚠ 需要先寫信（goodnight-letter）。不想寫信的下線走 goodnight-logout。";
        public override string Example => SCP_CmdRegistry.InvokeOf<SCP_Cmd_GoodnightSleep>("--arg persona=Template --arg-file summary=D:/tmp/s.md");
        protected override string CliNextHint =>
            "（晚安到此結束 —— 要重新上線走 " + SCP_CmdRegistry.InvokeOf<SCP_Cmd_MorningWake>("--arg persona=<P>") + "）";
        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => SCP_GoodnightCmds.SleepSpecs(MorningSpecs(), iSleep: true);

        protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
            => SCP_GoodnightCmds.RunSleep(Host!, iRoots, iArgs, ioResult, iNoLetter: false);
    }

    public sealed class SCP_Cmd_GoodnightLogout : SCP_LocalRootsCmd
    {
        public override string Name => "goodnight-logout";
        public override string Summary => "手動登出／cleanup（不寫信，廣播標明未留信）";
        public override string Details =>
            "**這不是晚安的第五步，是另一條路** —— session 壞掉、或只想清掉 lock 時走它。\n"
            + "⚠ 不套收工閘（那是 cleanup 不是收工）；不寫信 ⇒ 廣播標明未留信。**它不能代替 goodnight-sleep。**\n"
            + "⚠ lock 在但讀不了（壞檔）也會刪掉並明說 —— 那正是 cleanup 要處理的情況。\n" + SCP_GoodnightCmds.SleepDetails;
        public override string Example => SCP_CmdRegistry.InvokeOf<SCP_Cmd_GoodnightLogout>("--arg persona=Template");
        protected override string CliNextHint =>
            "（cleanup 完成 —— 要正常收工走 " + SCP_CmdRegistry.InvokeOf<SCP_Cmd_GoodnightCheck>("--arg persona=<P>") + "）";
        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => SCP_GoodnightCmds.SleepSpecs(MorningSpecs(), iSleep: false);

        protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
            => SCP_GoodnightCmds.RunSleep(Host!, iRoots, iArgs, ioResult, iNoLetter: true);
    }

    /// <summary>晚安 Cmd 的共用段（落回傳檔／sleep 組裝）。</summary>
    internal static class SCP_GoodnightCmds
    {
        internal const string SleepDetails =
            "順序是**不變式**：預檢（全部守衛，零寫入）→ 刪 lock／now_status → 關本人活動 session → 下線廣播（best-effort）。\n"
            + "token 只住 lock，刪 lock 就是作廢。只有一段要 Editor：進行中的**觀影場**結算 ——\n"
            + "Editor 活著 ⇒ 只把那一段（關場＋結算）交給 Editor；沒開 ⇒ 照走，跳過那一段並在回傳檔明說（觀影場到期成殘留後由殘留結算補付）。";

        internal static IReadOnlyList<SCP_CmdArgSpec> SleepSpecs(IEnumerable<SCP_CmdArgSpec> iBase, bool iSleep)
        {
            var a = new List<SCP_CmdArgSpec>(iBase);
            if (iSleep)
            {
                a.Add(new SCP_CmdArgSpec("summary", "公開的睡前心得（選填，併入下線廣播）"));
                a.Add(new SCP_CmdArgSpec("skip_reason", "跳過收工閘的理由（寫進那幾張單的時間線）"));
            }
            a.Add(new SCP_CmdArgSpec("note", "附註（選填，併入下線廣播）"));
            a.Add(new SCP_CmdArgSpec("timeout", "等酒館 Server／Editor 回執的秒數（預設 30）"));
            return a;
        }

        /// <summary>check／portrait／letter：落回傳檔、blocked 非零退出。</summary>
        internal static string Finish(SCP_MorningRoots iRoots, string iPersona, string iStep, SCP_MorningStepResult iRes, SCP_CmdResult ioResult)
        {
            string aPath = SCP_Goodnight.StepPayloadPath(iRoots, iPersona, iStep);
            SCP_CmdPayload.Write(aPath, iRes.Report);
            if (!iRes.Ok)
            {
                ioResult.ExitCode = 1;
                ioResult.Lines.Add("⛔ 被擋下 —— 原因與出口在回傳檔的 `## blocked`");
            }
            else ioResult.Lines.Add($"✓ goodnight {iStep} 完成");
            return aPath;
        }

        static string Value(SCP_CmdResult iR, string iKey)
        {
            foreach (KeyValuePair<string, string> kv in iR.Values) if (kv.Key == iKey) return kv.Value;
            return "";
        }

        internal static string? RunSleep(ISCP_LocalCmdHost iHost, SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult, bool iNoLetter)
        {
            string aPersona = iArgs.Get("persona").Trim();
            string aStep = iNoLetter ? "logout" : "sleep";
            string aReason = iNoLetter ? "goodnight-logout" : "goodnight-sleep";
            string aSkip = iNoLetter ? "" : iArgs.Get("skip_reason").Trim();
            string aPath = SCP_Goodnight.StepPayloadPath(iRoots, aPersona, aStep);

            // ① 預檢（零寫入）
            SCP_GoodnightPreflight aPre = SCP_Goodnight.SleepPreflight(iRoots, aPersona, iNoLetter, aSkip);
            if (aPre.Blocked)
            {
                SCP_CmdPayload.Write(aPath, aPre.Report);
                ioResult.ExitCode = 1;
                ioResult.Lines.Add("⛔ 被擋下（零寫入）—— 原因與出口在回傳檔的 `## blocked`");
                return aPath;
            }

            // ② 需要 Editor 的那一段（觀影場結算）：活著就只把那一段交出去（④）；沒開就照走、跳過那段
            var aSkipped = new List<string>();
            bool aSettleViaEditor = false;
            if (aPre.NeedsEditor.Length > 0)
            {
                if (iHost.EditorAlive(iRoots.DataRoot, out string aWhy))
                {
                    aSettleViaEditor = true;
                    ioResult.Lines.Add($"⤷ 這一步有一段要 Editor（{aPre.NeedsEditor}）；Editor 活著（{aWhy}）"
                        + "⇒ **只有那一段**（關場＋結算）交給 Editor；解鎖與下線廣播在本地做");
                }
                else
                {
                    ioResult.Lines.Add($"⚠ 這一步有一段要 Editor（{aPre.NeedsEditor}），而 Editor 沒開（{aWhy}）⇒ 照走晚安，**只跳過那一段**");
                    if (aPre.ActiveStreamWatchId.Length > 0)
                        aSkipped.Add($"觀影場 `{aPre.ActiveStreamWatchId}` 沒結算、沒關 —— 到期後成為殘留，下次 StreamWatch start 或 "
                            + SCP_CmdRegistry.InvokeNamed("sessions", $"--arg op=close --arg target_persona={aPersona} --arg confirm=1")
                            + " 會補結算（付到 ends_at）");
                }
            }

            // ②b 收工閘顯式跳過 ⇒ 理由寫進那幾張單的時間線。寫不成只是警告並記在回傳檔與廣播 —— 下線本身不因此失敗。
            if (aPre.NeedsTaskSkipWrite)
                foreach (SCP.Core.Tasks.SCP_TaskEntry t in aPre.PendingWrapups)
                {
                    SCP_CmdResult aW = SCP_CmdRegistry.Dispatch("task", new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["op"] = "wrapup_skip", ["persona"] = aPersona, ["data_root"] = iRoots.DataRoot,
                        ["index"] = t.index.ToString(CultureInfo.InvariantCulture), ["reason"] = aSkip,
                    });
                    if (aW.Ok) { ioResult.Lines.Add($"📋 {t.Id} 的時間線已記下跳過收工的理由"); continue; }
                    aSkipped.Add($"{t.Id} 的收工跳過理由**沒寫進單子時間線**（task op=wrapup_skip exit {aW.ExitCode}"
                        + (aW.ExitCode == 7 ? "，結果不明 —— 先回讀那張單再補" : "") + $"），改記在這裡與下線廣播：{aSkip}");
                }

            // ③ 寫入：刪 lock／now_status、組廣播
            SCP_GoodnightSleep aApply = SCP_Goodnight.SleepApply(iRoots, aPersona, iNoLetter, aPre);
            // ④ 關本人活動 session（位置在解鎖之後；關場失敗不擋下線）
            string aSessionLine = aSettleViaEditor
                ? iHost.CloseStreamWatchViaEditor(iRoots, aPersona, aReason, iArgs.Get("timeout"), ioResult)
                : SCP_Goodnight.CloseOwnSessionNative(iRoots, aPersona, iNoLetter);

            // ⑤ 下線廣播（best-effort）
            string aSummary = iNoLetter ? "" : iArgs.Get("summary").Trim();
            string aBody = aApply.BroadcastBody.Replace("{SUMMARY}", aSummary.Length == 0 ? "" : $"💭 **今日心得**\n{aSummary}\n\n");
            if (!iNoLetter)
            {
                string? aPortraitSkip = SCP_Goodnight.PortraitSkipReasonToday(iRoots, aPersona);
                if (!string.IsNullOrEmpty(aPortraitSkip)) aBody += $"\n- 🖼 本夜未畫像，理由：{aPortraitSkip}";
                if (aPre.NeedsTaskSkipWrite) aBody += $"\n- 📋 收工閘顯式跳過（{aPre.PendingWrapups.Count} 張），理由：{aSkip}";
            }
            string aNote = iArgs.Get("note");
            if (aNote.Length > 0) aBody += $"\n- Note: {aNote}";
            var aMeta = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["tag"] = "goodnight-protocol", ["category"] = "meta", ["status-change"] = "offline" };
            string aBroadcastLine;
            SCP_TavernPostDraft aDraft = SCP_TavernPostCompose.Build(iRoots.DataRoot, iRoots.LettersRoot, iRoots.ProjectRoot,
                iRoots.GlossaryRoot, iRoots.Region, "tavern", aPersona, aBody, aMeta);
            foreach (string n in aDraft.Notes) ioResult.Lines.Add("⚠ " + n);
            if (aDraft.Message == null)
                aBroadcastLine = $"未發（組訊息被拒：{aDraft.Error}）—— 核心已落地，同事看 lock 判在線";
            else
            {
                string aTimeout = iArgs.Get("timeout");
                SCP_LocalTavernWrite aW = iHost.WriteTavern(new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["data_root"] = iRoots.DataRoot, ["room"] = "tavern",
                    ["msg_json"] = SCP_TavernWriter.Serialize(aDraft.Message),
                    ["timeout"] = aTimeout.Length > 0 ? aTimeout : "30",
                });
                string aSeq = Value(aW.Result, "seq");
                string aFail = Value(aW.Result, "delegate_failure");
                if (aW.Queued) ioResult.AddValue("queued", "1");
                aBroadcastLine = aW.Queued
                    ? $"📥 **已排隊**（酒館 Server 不在；cmd_id {Value(aW.Result, "queued_cmd_id")}）—— 起來後送出，⛔ 不要補發"
                    : aW.Result.Ok && aSeq.Length > 0 ? $"seq **{aSeq}**"
                    : aFail == "timeout" || aFail == "unknown"
                        ? $"**不知道**有沒有發（delegate_failure={aFail}）—— ⛔ 別直接補發，先 {SCP_CmdRegistry.InvokeOf<SCP_Cmd_TavernQuery>("--arg kind=tail")} 回讀"
                        : $"未發（{(aFail.Length > 0 ? "delegate_failure=" + aFail : "exit " + aW.Result.ExitCode)}）—— 核心已落地，補發非必要（同事看 lock 判在線）";
                if (aSeq.Length > 0) ioResult.AddValue("post_seq", aSeq);
            }

            bool aLockStill = File.Exists(SCP_LettersPaths.SessionLockPath(iRoots.Letters, aPersona));

            var aSb = new StringBuilder(aApply.Report);
            aSb.AppendLine();
            if (aSkipped.Count > 0)
            {
                aSb.AppendLine("## ⚠ 因 Editor 沒開或寫入失敗而跳過的段（晚安不因此卡住）");
                foreach (string s in aSkipped) aSb.AppendLine("- " + s);
            }
            aSb.AppendLine("## verify（讀回的事實）");
            aSb.AppendLine($"- lock: exists={aLockStill}（應為 False）");
            aSb.AppendLine($"- broadcast: {aBroadcastLine}");
            aSb.AppendLine(aLockStill
                ? "- session_token: ⚠ **仍有效** —— lock 還在（見上一行），token 跟著它"
                : "- session_token: 隨 lock 一起失效（token 只住 lock）");
            aSb.AppendLine(aSessionLine);
            aSb.AppendLine("## next");
            aSb.AppendLine("- 收工。明天醒來：" + SCP_CmdRegistry.InvokeOf<SCP_Cmd_MorningWake>($"--arg persona={aPersona}"));
            if (!iNoLetter)
                aSb.AppendLine("- （可選）還想花錢再睡 → " + SCP_CmdRegistry.InvokeOf<SCP_Cmd_Spend>($"--arg op=roll --arg persona={aPersona}"));
            SCP_CmdPayload.Write(aPath, aSb.ToString());
            ioResult.Lines.Add($"✓ 已下線（廣播：{aBroadcastLine.Split('—')[0].Trim()}）" + (aSkipped.Count > 0 ? $"　⚠ 跳過 {aSkipped.Count} 段（見回傳檔）" : ""));
            return aPath;
        }
    }
}
