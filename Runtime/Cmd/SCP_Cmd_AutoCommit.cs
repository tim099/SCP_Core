// 區塊職責：`cmd auto-commit` —— 把機器生成的檔分群整批 commit。**原生**，不需要 Unity。
// 物理意義：取代 Unity 端的 `Cmd_AutoCommit`（TASK-0340，Tim 2026-09-30：Unity 端準備退場）。
//          規則與引擎在 SCP_AutoCommitRules／SCP_AutoCommit —— 本檔只負責「參數 → 掃 → 逐群提交 → 回報」，
//          Senate 的自動 Commit 頁走同一支引擎（頁面與 Cmd 對同一個檔給出同一個群，這不是巧合而是結構）。
// 數值影響：
//   · **`op=scan` 是預設**（純讀）。要真的 commit 得顯式 `op=commit` —— 批次提交的預設值必須是「不提交」，
//     它的破壞面是整批 repo 的 index。
//   · 掃描範圍**統一**（不再有 `mode=`）：AgentCommands 本層＋每個信件庫＋有設定檔的 submodule。
//     ⛔ 不再有 `include_online`：在線守衛已拿掉（理由見 SCP_AutoCommit 檔頭②）。
//   · **不 push、不 bump 父層 pointer**；走純 git commit，⛔ 不走 `senate cmd commit`（無 trailer／無公告／不領薪）。
// ⚠ 「commits 為什麼是這個數」要能從機讀值上讀出來 —— 這一族的血證是 2026-08-31 summit：
//   `candidate_files=270 / commits=0`，而 blocked／prestaged／disabled 全是 0，呼叫端手上沒有任何一格解釋那個 0。
//   ⇒ 下面那幾格 **0 也印**：只在非零時才出現的欄位，讀者分不出「乾淨」與「沒量」。
//   對帳式：`candidate_files − other_files − other_untracked_files − subptr_files` ＝ 現在可自動收的檔數。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
// @doc-sync: <SCP_Core>/Docs~/AutoCommit.md（Cmd 參數與機讀值）
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Git;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_AutoCommit : SCP_Cmd
    {
        public override string Name => "auto-commit";

        public override string Summary =>
            "自動 commit：機器生成的檔分群整批提交（AgentCommands＋全部信件庫＋有設定檔的 submodule）—— **預設只掃不提交**";

        public override string Details =>
            "規則與 Senate「自動 Commit」頁共用同一支引擎（`SCP_AutoCommit`）。\n"
            + "⛔ 永不自動收：`__other`（未分類）／`__other_untracked`（未分類且從未進版控）／`__subptr`（submodule pointer）"
            + " —— 要收得顯式列進 `groups=`。\n"
            + "⛔ 擋下：detached HEAD／呼叫前 index 已有 staged 檔（BUG-30）／設定檔壞掉或不合法。\n"
            + "⚠ 不 push、不 bump 父層 pointer；純 git commit（無 trailer、無酒館公告、不領薪）。\n"
            + "⚠ 在線守衛已拿掉（Tim 2026-09-30）：自動群收的都是機器獨佔的檔，親筆檔本來就落未分類。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("auto-commit --arg data_root=D:/Unity/LY/AgentCommands"
                                   + " --arg letters_root=D:/Unity/LY/AgentCommands/ChatTavern/baton/letters");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）—— 底下每個 repo 都會被掃", iRequired: true),
            new SCP_CmdArgSpec("op", "scan（預設，純讀）｜commit（逐群提交）", iDefault: "scan",
                               iChoices: new[] { "scan", "commit" }),
            new SCP_CmdArgSpec("groups",
                "只做這幾群（逗號分隔的群 key；對所有 repo 一體適用）。不給＝**每個 repo 各自**的 DefaultOn 群。"
                + "特殊群 `__other` / `__other_untracked` / `__subptr` 只有列在這裡才會做"),
            // ⚠ 參數名刻意**不叫** `persona`：派遣端會把 `--persona <me>` 戳進 args（那是「這筆是誰派的」），
            //   叫 persona 就會被那個宣告當成篩選條件。🩸 UCL 端實測：`--persona kiara` 讓掃描範圍從 9 個 repo 縮成 1 個。
            new SCP_CmdArgSpec("only", "只做這幾個 repo（逗號分隔的顯示名，例：AgentCommands,kotoko,Chess）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aOp = iArgs.Get("op");
            bool aCommit = aOp == "commit";
            // 相對路徑照 process cwd 解析 ⇒ 回音一律印**解析後**的絕對路徑（同一個字串在兩棵樹下同形，定語不能靠讀的人補）。
            string aDataRoot = Path.GetFullPath(iArgs.Get("data_root"));
            string aLettersRoot = Path.GetFullPath(iArgs.Get("letters_root"));
            if (!Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aDataRoot);

            HashSet<string>? aExplicit = ParseList(iArgs.Get("groups"));
            HashSet<string>? aOnly = ParseList(iArgs.Get("only"));

            var aNotes = new List<string>();
            List<SCP_AutoCommitRepo> aRepos;
            using (SCP_Git.Scope(SCP_AutoCommit.ProcessTag, nameof(SCP_Cmd_AutoCommit)))
            {
                aRepos = SCP_AutoCommit.Discover(new SCP_DataRoot(aDataRoot), new SCP_LettersRoot(aLettersRoot), aNotes);
                if (aOnly != null)
                {
                    var aUnknown = new List<string>();
                    foreach (string aName in aOnly)
                        if (!aRepos.Exists(r => r.Name == aName)) aUnknown.Add(aName);
                    // 打錯的 repo 名**不准靜默變成「沒有東西可收」** —— 那跟「真的乾淨」同形。
                    if (aUnknown.Count > 0)
                        return SCP_CmdResult.Fail(2, "✗ only= 裡有認不得的 repo：" + string.Join(", ", aUnknown),
                            "  這一輪發現的：" + string.Join(", ", aRepos.ConvertAll(r => r.Name)));
                    aRepos = aRepos.FindAll(r => aOnly.Contains(r.Name));
                }
                foreach (var aRepo in aRepos) SCP_AutoCommit.Scan(aRepo);
            }

            var aResult = new SCP_CmdResult();
            aResult.Lines.Add($"[auto-commit] op={aOp} repos={aRepos.Count} groups="
                              + (aExplicit == null ? "(各 repo 的 DefaultOn)" : string.Join(",", aExplicit)));
            aResult.Lines.Add($"  資料根：{aDataRoot}");
            aResult.Lines.Add($"  letters 根：{aLettersRoot}");
            foreach (string aNote in aNotes) aResult.Lines.Add("  " + aNote);

            int aCandidates = 0, aEphemeral = 0, aCommits = 0, aFailed = 0, aSkippedGroups = 0, aEmptyGroups = 0;
            int aBlocked = 0, aPreStagedRepos = 0, aDisabled = 0, aOther = 0, aOtherU = 0, aSubPtr = 0, aLocked = 0;
            int aExtraCommits = 0;
            var aShas = new List<string>();
            var aFailures = new List<string>();

            foreach (var aRepo in aRepos)
            {
                aCandidates += aRepo.CandidateCount;
                aEphemeral += aRepo.Ephemeral;
                aOther += Count(aRepo, SCP_AutoCommitRules.KeyOther);
                aOtherU += Count(aRepo, SCP_AutoCommitRules.KeyOtherUntracked);
                aSubPtr += Count(aRepo, SCP_AutoCommitRules.KeySubPtr);

                string aHead = $"  📁 {aRepo.Name}（{aRepo.SourceLabel}{(aRepo.Branch.Length > 0 ? "／" + aRepo.Branch : "")}）";
                if (aRepo.Disabled)
                {
                    aDisabled++;
                    aResult.Lines.Add(aHead + " ・設定為停用（Enabled=false）—— 到自動 Commit 頁或設定檔開啟");
                    continue;
                }
                if (aRepo.Blocked.Length > 0)
                {
                    aBlocked++;
                    aResult.Lines.Add(aHead + " ⛔ " + aRepo.Blocked + " —— 跳過");
                    continue;
                }
                if (aRepo.CandidateCount == 0) continue;   // 乾淨的 repo 不佔版面（總數在最後那行）
                aResult.Lines.Add(aHead + $" 候選 {aRepo.CandidateCount} 檔");

                // 硬擋③：index 不是空的 ⇒ commit 擋下；scan 只警告（scan 不動 index，擋了就看不到分群 ——
                // 而「先 scan 再決定」正是擋下之後唯一的出路）。
                if (aRepo.PreStaged.Count > 0)
                {
                    aPreStagedRepos++;
                    string aHint = $"呼叫前 index 已有 {aRepo.PreStaged.Count} 個 staged 檔 —— 它們會被併進第一個群並掛上那個群的訊息"
                                   + "（BUG-30）；請先自己 commit 或 unstage";
                    aResult.Lines.Add((aCommit ? "    ⛔ " : "    ⚠ ") + aHint + (aCommit ? " —— 跳過" : "（op=commit 會擋下這個 repo）"));
                    foreach (string aLine in SCP_AutoCommit.Preview(aRepo.PreStaged)) aResult.Lines.Add("        " + aLine);
                    if (aCommit) { aBlocked++; continue; }
                }

                foreach (string aKey in aExplicit ?? aRepo.DefaultOnGroups())
                {
                    if (!aRepo.Groups.TryGetValue(aKey, out var aFiles) || aFiles.Count == 0) { aEmptyGroups++; continue; }
                    if (!aCommit)
                    {
                        aResult.Lines.Add($"    → [{aKey}] {aFiles.Count} 檔：{aRepo.MessageOf(aKey)}");
                        foreach (string aLine in SCP_AutoCommit.Preview(aFiles)) aResult.Lines.Add("        " + aLine);
                        continue;
                    }
                    SCP_AutoCommitGroupResult aGroup;
                    using (SCP_Git.Scope(SCP_AutoCommit.ProcessTag, nameof(SCP_Cmd_AutoCommit)))
                        aGroup = SCP_AutoCommit.CommitGroup(aRepo, aKey);
                    foreach (string aLine in aGroup.Log) aResult.Lines.Add("    " + aLine);
                    if (aGroup.Ok)
                    {
                        aCommits++;
                        aShas.Add(aRepo.Name + ":" + aGroup.Sha);
                        if (aGroup.Extra.Count > 0) aExtraCommits++;
                    }
                    else if (aGroup.Skipped) aSkippedGroups++;
                    else
                    {
                        aFailed++;
                        if (aGroup.IsLocked) aLocked++;
                        aFailures.Add($"{aRepo.Name}[{aKey}] {aGroup.Error}");
                    }
                }

                // 永不自動收的那幾群：清單一律印出來（看與收不該共用同一個手勢 —— UCL 端 TASK-0129）。
                foreach (string aNever in SCP_AutoCommitRules.NeverAutoKeys)
                {
                    if (aExplicit != null && aExplicit.Contains(aNever)) continue;   // 顯式要了 ⇒ 上面那圈已經印過
                    if (!aRepo.Groups.TryGetValue(aNever, out var aList) || aList.Count == 0) continue;
                    aResult.Lines.Add($"    ⚠ [{aNever}] {aList.Count} 檔 —— **不自動收**，要收得顯式 `--arg groups={aNever}`：");
                    foreach (string aLine in SCP_AutoCommit.Preview(aList)) aResult.Lines.Add("        " + aLine);
                }
            }

            aResult.Lines.Add($"  ⇒ {(aCommit ? "提交" : "掃描")}完成：候選檔 {aCandidates}／ephemeral 略過 {aEphemeral}／"
                              + $"commit {aCommits}／失敗的群 {aFailed}／跳過的群 {aSkippedGroups}／空的群 {aEmptyGroups}／"
                              + $"__other {aOther}／__other_untracked {aOtherU}／__subptr {aSubPtr}（三者不自動收）／"
                              + $"擋下的 repo {aBlocked}／停用 {aDisabled}");
            if (aCommits > 0)
                aResult.Lines.Add("  ↳ 未 push、父層 pointer 未動 —— bump 與 push 是人的決定。");

            aResult.AddValue("op", aOp)
                   .AddValue("repos", aRepos.Count.ToString())
                   .AddValue("candidate_files", aCandidates.ToString())
                   .AddValue("ephemeral_skipped", aEphemeral.ToString())
                   .AddValue("commits", aCommits.ToString())
                   .AddValue("failed_groups", aFailed.ToString())
                   .AddValue("skipped_groups", aSkippedGroups.ToString())
                   .AddValue("empty_groups", aEmptyGroups.ToString())
                   .AddValue("blocked_repos", aBlocked.ToString())
                   .AddValue("prestaged_repos", aPreStagedRepos.ToString())
                   .AddValue("disabled_repos", aDisabled.ToString())
                   .AddValue("other_files", aOther.ToString())
                   .AddValue("other_untracked_files", aOtherU.ToString())
                   .AddValue("subptr_files", aSubPtr.ToString())
                   .AddValue("locked_repos", aLocked.ToString())
                   .AddValue("reconcile_mismatch", aExtraCommits.ToString())
                   .AddValue("failed_repos", string.Join(" ／ ", aFailures))
                   .AddValue("shas", string.Join(" ", aShas));

            // 值先報完再判 —— 呼叫端要的是「幾群失敗、為什麼」。git 操作失敗過的一輪**不判 Success**：
            // 已經成功的那幾群是真的（SHA 在 shas 欄），所以這不是回滾，是拒絕把部分成功說成完成。
            if (aFailed > 0 || aExtraCommits > 0)
            {
                aResult.ExitCode = 1;
                if (aFailed > 0)
                    aResult.Lines.Add($"✗ {aFailed} 個群的 git 操作失敗（commit {aCommits} 群成功）：{string.Join(" ／ ", aFailures)}"
                                      + (aLocked > 0 ? $"　⏳ 其中 {aLocked} 筆是 `index.lock`（別人正握著那個 repo 的 index）—— 等一下重跑就好。" : "")
                                      + "　⚠ 本 Cmd 刻意**不重試、不刪 lock**（刪別人的 lock 會讓那個 process 寫壞 index）。");
                if (aExtraCommits > 0)
                    aResult.Lines.Add($"✗ {aExtraCommits} 筆 commit 對帳不符（多帶了不在分群清單裡的檔）—— 要人看。");
            }
            return aResult;
        }

        static int Count(SCP_AutoCommitRepo iRepo, string iKey)
            => iRepo.Groups.TryGetValue(iKey, out var aList) ? aList.Count : 0;

        /// <summary>逗號清單 → 集合；沒給回 **null**（⚠ 不是空集合：空集合的語意是「什麼都不要做」，兩者不可同形）。</summary>
        static HashSet<string>? ParseList(string iArg)
        {
            string a = (iArg ?? "").Trim();
            if (a.Length == 0) return null;
            var aSet = new HashSet<string>();
            foreach (string aRaw in a.Split(','))
            {
                string aKey = aRaw.Trim();
                if (aKey.Length > 0) aSet.Add(aKey);
            }
            return aSet;
        }
    }
}
