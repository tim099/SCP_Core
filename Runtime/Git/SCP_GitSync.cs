// 區塊職責：**唯一會改變狀態的一層** —— fetch / checkout / pull / push，以及它們前面那幾道安全線。
// 物理意義：這一層的價值不在「會呼叫 git」，在**什麼時候拒絕呼叫**。
//           核心原則是 fail loud：dirty、detached 上有未合併 commit、解析不到目標 branch，
//           一律**跳過並列出**。不 stash、不 force、不替人做 merge 決定 ——
//           「自動 stash」是把別人的工作區當自己的，而預設值是裝填好的槍。
// 數值影響：checkout / pull 移動 HEAD；push 寫遠端；fetch 只動 remote-tracking ref（不碰工作目錄）。
//           安全線一律**現場重問 git**，不吃任何掃描快照（理由見 Checkout 的註解）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Git
{
    /// <summary>一次同步動作的結果類別。</summary>
    public enum SCP_GitSyncOutcome
    {
        /// <summary>做完了（或本來就沒事要做）。</summary>
        Ok = 0,

        /// <summary>刻意沒動它，理由在 <see cref="SCP_GitSyncResult.Summary"/>。**這不是失敗。**</summary>
        Skipped,

        /// <summary>試了但失敗。</summary>
        Failed,
    }

    /// <summary>
    /// 一個 repo 跑完一輪的結果。
    /// <para>⚠ 呼叫端請讀 <see cref="Outcome"/>，**不要去比對 <see cref="Summary"/> 的開頭字元**。
    /// 靠 `Summary.StartsWith("✓")` 統計的那種寫法，會在有人改一個字的那天靜默把成功算成失敗。</para>
    /// </summary>
    public sealed class SCP_GitSyncResult
    {
        public SCP_GitSyncOutcome Outcome = SCP_GitSyncOutcome.Ok;

        /// <summary>給人看的一句（會顯示在狀態表那一列上）。</summary>
        public string Summary = "";

        /// <summary>推成功幾個 remote / 總共幾個（沒做 push 時都是 0）。</summary>
        public int PushOk;
        public int PushTotal;

        /// <summary>推失敗的 remote 名字（部分成功時要看得到是哪一邊掛了）。</summary>
        public List<string> PushFailedRemotes = new List<string>();

        public bool IsOk { get { return Outcome == SCP_GitSyncOutcome.Ok; } }
        public bool IsSkipped { get { return Outcome == SCP_GitSyncOutcome.Skipped; } }
        public bool IsFailed { get { return Outcome == SCP_GitSyncOutcome.Failed; } }

        public static SCP_GitSyncResult Ok(string iSummary)
        {
            var aRes = new SCP_GitSyncResult();
            aRes.Outcome = SCP_GitSyncOutcome.Ok;
            aRes.Summary = iSummary;
            return aRes;
        }

        public static SCP_GitSyncResult Skip(string iSummary)
        {
            var aRes = new SCP_GitSyncResult();
            aRes.Outcome = SCP_GitSyncOutcome.Skipped;
            aRes.Summary = iSummary;
            return aRes;
        }

        public static SCP_GitSyncResult Fail(string iSummary)
        {
            var aRes = new SCP_GitSyncResult();
            aRes.Outcome = SCP_GitSyncOutcome.Failed;
            aRes.Summary = iSummary;
            return aRes;
        }
    }

    /// <summary>一輪同步要做哪幾步。</summary>
    public sealed class SCP_GitSyncOptions
    {
        /// <summary>切到目標 branch（安全線見 <see cref="SCP_GitSync.Checkout"/>）。</summary>
        public bool Checkout;

        /// <summary><c>pull --ff-only</c>。分岔就失敗列出，不替人 merge / rebase。</summary>
        public bool Pull;

        public bool Push;

        /// <summary>
        /// push 推去該 repo 的**每一個** remote（關 ＝ 只推 <see cref="PullRemote"/>）。
        /// <para>物理意義：同一份程式碼同時掛 GitHub 與 GitLab 時，只推一邊會讓另一邊
        /// **靜默落後** —— 而落後的那一邊不會叫（沒人 pull 它就沒人知道）。</para>
        /// </summary>
        public bool PushAllRemotes;

        /// <summary>
        /// pull 從哪個 remote 合併（預設 origin）。
        /// <para>⚠ <see cref="PushAllRemotes"/> **刻意不影響這一格**：
        /// 「從哪裡合併」是 merge 決策，不是同步動作 —— 那該由人決定，不是由一個開關順便決定。</para>
        /// </summary>
        public string PullRemote = "origin";
    }

    /// <summary>寫入型 git 動作（唯一會改變狀態的一層）。</summary>
    public static class SCP_GitSync
    {
        /// <summary>
        /// 對**一個 repo** 跑 checkout → pull → push。
        /// <para>三步共用一個入口：「一鍵同步」與單獨按鈕走同一條路徑，不各寫一份 ——
        /// 各寫一份的話，改一邊忘一邊的那天不會有人發現。</para>
        /// <param name="iRepoDir">這個 repo 的絕對路徑</param>
        /// <param name="iLabel">報告裡怎麼稱呼它（通常是相對路徑，root 用 <c>"(root)"</c>）</param>
        /// <param name="iTarget">目標 branch。**空字串 ＝ 解析不到 ⇒ 跳過**，本層不替它猜一個</param>
        /// <param name="iLog">逐條經過的紀錄（可 null）。Summary 是結論，這裡是過程</param>
        /// </summary>
        public static SCP_GitSyncResult Apply(string iRepoDir, string iLabel, string iTarget,
            SCP_GitSyncOptions iOptions, Action<string>? iLog = null)
        {
            if (iOptions == null) iOptions = new SCP_GitSyncOptions();

            if (string.IsNullOrEmpty(iTarget))
            {
                Say(iLog, "⏭ " + iLabel + " 解析不到目標 branch（覆寫 / .gitmodules / 全域預設 / 啟發式全空）");
                return SCP_GitSyncResult.Skip("⏭ 無目標 branch");
            }

            // 目前在哪 —— **這是即時值**，下面每一步都吃它而不是吃掃描快照。
            string? aCurrent = SCP_GitRepo.Branch(iRepoDir);
            if (aCurrent == null)
            {
                Say(iLog, "✗ " + iLabel + " 讀不到目前 branch —— 不動它");
                return SCP_GitSyncResult.Fail("✗ 狀態不明");
            }

            if (iOptions.Checkout && aCurrent != iTarget)
            {
                SCP_GitSyncResult aCheckout = Checkout(iRepoDir, iLabel, iTarget, aCurrent, iLog);
                if (!aCheckout.IsOk) return aCheckout;
                // ⭐ 這一行是 2026-08-10 修掉的真 bug 的核心：切完之後**要更新手上的即時值**。
                //   舊版在 push 那半讀的是掃描快照（還停在切之前的 (detached)），
                //   於是每一個「剛被切好的」repo 都會在 push 前被判成不在目標 branch 而靜默跳過
                //   —— 一鍵同步推不動東西的原因就是這一格。
                aCurrent = iTarget;
            }

            if (iOptions.Pull)
            {
                SCP_GitSyncResult aPull = PullFfOnly(iRepoDir, iLabel, iTarget, aCurrent, iOptions.PullRemote, iLog);
                if (!aPull.IsOk) return aPull;
            }

            if (iOptions.Push)
            {
                return Push(iRepoDir, iLabel, iTarget, aCurrent, iOptions, iLog);
            }

            return SCP_GitSyncResult.Ok("✓");
        }

        /// <summary>
        /// 切到目標 branch —— 四道安全線，每一道都**跳過而不硬上**。
        /// <para>順序（Tim 2026-08-11 定案）：dirty 檢查 → <c>fetch</c> → **把本地目標分支快轉** →
        /// 存在性檢查 → 祖先檢查 → <c>checkout</c>。</para>
        /// <para>🩸 為什麼要多那一步「快轉本地目標分支」：<c>git fetch</c> 只更新
        /// <c>refs/remotes/*</c>，**不動 <c>refs/heads/*</c>**。而本地已有該 branch 時，
        /// 下面兩道檢查拿的是本地那條 ⇒ 本地落後時會發生兩件事，**兩件都不會叫**：
        /// ① detached 在 <c>origin/&lt;target&gt;</c> tip 的 repo 被判成「HEAD 未合併」整列跳過
        ///    —— 那道安全線在保護一個不存在的風險，而跳過訊息看起來完全像盡責。
        /// ② 就算通過，checkout 會把工作目錄**倒退**到舊 commit，等後面 pull 再前進
        ///    —— Unity 專案白吃一輪 reimport。
        /// <c>fetch origin &lt;t&gt;:&lt;t&gt;</c> 可以在**不 checkout** 的情況下快轉本地分支，
        /// 而且非 fast-forward 時 git 自己會拒絕（不需要我們再判一次）。</para>
        /// </summary>
        public static SCP_GitSyncResult Checkout(string iRepoDir, string iLabel, string iTarget,
            string iCurrentBranch, Action<string>? iLog = null)
        {
            // 安全線①：dirty 就不切 —— 切 branch 會吃掉未收的工作。
            // ⚠ 這把尺**現在**才量，不吃掃描快照：掃描與按下按鈕之間，宿主（Unity Editor）
            //   會 import asset、寫 .meta、存 scene。照片乾淨、現在髒了的話，
            //   「dirty 就跳過」的承諾會靜默失效，而報告照印 ✓。
            SCP_GitDirtyState aDirty = SCP_GitRepo.DirtyState(iRepoDir);
            if (aDirty != SCP_GitDirtyState.Clean)
            {
                string aWhy = aDirty == SCP_GitDirtyState.Dirty ? "有未 commit 修改" : "status 問不到（狀態不明）";
                Say(iLog, "⏭ " + iLabel + " dirty（" + aWhy + "）—— 不切 branch，請先自行處理");
                return SCP_GitSyncResult.Skip("⏭ dirty");
            }

            // 只有真的要切的 repo 才 fetch（不是全員）；離線就用本地既有 ref 繼續並記一筆。
            SCP_GitResult aFetch = SCP_Git.RunTimeout(iRepoDir, SCP_Git.NetworkTimeoutMs, "fetch", "--quiet");
            if (!aFetch.Ok)
                Say(iLog, "⚠ " + iLabel + " fetch 失敗（" + aFetch.ReasonLine + "）—— 以下判斷用本地既有 ref");

            bool aHasLocal = SCP_GitRepo.HasLocalBranch(iRepoDir, iTarget);
            bool aHasRemote = SCP_GitRepo.HasRemoteBranch(iRepoDir, iTarget);

            // 把本地目標分支快轉到 origin（見本方法的血證段）。
            // ⚠ 只在「本地已有這條 branch 且不是目前所在」時做：
            //   · 目標就是目前所在 → git 直接拒絕（本方法的呼叫端保證 cur != target，
            //     但條件寫明，不靠上下文）
            //   · 本地還沒有這條 branch → 留給下面的 `checkout -b --track` 建，
            //     那條路會**順便設好 upstream**；refspec 建出來的分支沒有 upstream，
            //     於是 ahead/behind 會變成「未知」。
            //   plain fetch 已經失敗（離線）就不再試第二次 —— 同一個原因報兩次是雜訊。
            if (aFetch.Ok && aHasLocal && iCurrentBranch != iTarget)
            {
                SCP_GitResult aFf = SCP_Git.RunTimeout(iRepoDir, SCP_Git.NetworkTimeoutMs,
                    "fetch", "origin", iTarget + ":" + iTarget);
                if (!aFf.Ok)
                {
                    // 非 ff（本地分支有 origin 沒有的 commit）→ 不硬上，維持舊尺往下走；
                    // 下面的祖先檢查仍然是最後一道關。
                    Say(iLog, "⚠ " + iLabel + " 本地 " + iTarget + " 無法快轉到 origin（"
                        + aFf.ReasonLine + "）—— 以下判斷用本地既有位置");
                }
            }

            // 安全線②：目標 branch 本地與遠端都不存在 ⇒ 無中生有一條 branch 不是同步，是建構。
            if (!aHasLocal && !aHasRemote)
            {
                Say(iLog, "⏭ " + iLabel + " 找不到 branch「" + iTarget + "」（本地與剛 fetch 完的 origin 都沒有）");
                return SCP_GitSyncResult.Skip("⏭ branch 不存在");
            }

            // 安全線③：HEAD 必須已在目標 branch 歷史上才切 ——
            // detached 上可能有未合併的 commit，切走就脫錨（reflog 能救，但沒人會去看 reflog）。
            string aCheckRef = aHasLocal ? iTarget : "origin/" + iTarget;
            if (!SCP_GitRepo.IsAncestor(iRepoDir, "HEAD", aCheckRef))
            {
                // 訊息要講清楚這把尺是新的 —— 舊版在本地分支落後時也印同一句，
                // 於是「真的有未合併 commit」跟「尺過期」長得一模一樣，而後者才是常態。
                Say(iLog, "⏭ " + iLabel + " 目前 HEAD 不在「" + aCheckRef + "」歷史上（"
                    + aCheckRef + " 已先快轉到 origin，所以這是真的有未合併 commit）—— 不切，請先合併後再來");
                return SCP_GitSyncResult.Skip("⏭ HEAD 未合併");
            }

            SCP_GitResult aCheckout = aHasLocal
                ? SCP_Git.Run(iRepoDir, "checkout", iTarget)
                : SCP_Git.Run(iRepoDir, "checkout", "-b", iTarget, "--track", "origin/" + iTarget);
            if (!aCheckout.Ok)
            {
                Say(iLog, "✗ " + iLabel + " checkout " + iTarget + " 失敗: " + aCheckout.ReasonLine);
                return SCP_GitSyncResult.Fail("✗ checkout 失敗");
            }
            Say(iLog, "✓ " + iLabel + " 已切到 " + iTarget);
            return SCP_GitSyncResult.Ok("✓ 已切到 " + iTarget);
        }

        /// <summary>
        /// <c>pull --ff-only</c>。
        /// <para>⚠ **dirty 也要擋**，不只 checkout 那半：git 確實會拒絕覆蓋衝突檔，
        /// 但那是**逐檔**的保護 —— 不衝突的檔照 ff 過去。於是「我還沒 commit 的工作」跟
        /// 「剛拉下來的新版」混在同一個工作目錄裡，而人不會知道那一刻發生過合併。
        /// （🩸 這道原本只有 checkout 那半有，而按鈕下方寫的是「dirty 一律跳過」——
        /// 承諾涵蓋全頁、實作只涵蓋一半，那種說明比沒有說明更糟，因為它讓人不去查。）</para>
        /// <para>ff-only：分岔就失敗列出。merge / rebase 的選擇不該由批次工具代下。</para>
        /// </summary>
        public static SCP_GitSyncResult PullFfOnly(string iRepoDir, string iLabel, string iTarget,
            string iCurrentBranch, string iRemote = "origin", Action<string>? iLog = null)
        {
            if (iCurrentBranch != iTarget)
            {
                // 指路而不是死路：單獨按「pull」時 detached 的列必定落在這裡，而
                // 「不在目標 branch」只講了事實、沒講下一步該做什麼（那是一個沒有出口的訊息）。
                Say(iLog, "⏭ " + iLabel + " 不在目標 branch（" + iCurrentBranch + " ≠ " + iTarget
                    + "）—— 不 pull；要一次到位請連 checkout 一起跑");
                return SCP_GitSyncResult.Skip("⏭ 不在目標 branch");
            }

            SCP_GitDirtyState aDirty = SCP_GitRepo.DirtyState(iRepoDir);
            if (aDirty != SCP_GitDirtyState.Clean)
            {
                string aWhy = aDirty == SCP_GitDirtyState.Dirty ? "有未 commit 修改" : "status 問不到（狀態不明）";
                Say(iLog, "⏭ " + iLabel + " dirty（" + aWhy + "）—— 不 pull，請先自行處理");
                return SCP_GitSyncResult.Skip("⏭ dirty");
            }

            SCP_GitResult aPull = SCP_Git.RunTimeout(iRepoDir, SCP_Git.NetworkTimeoutMs,
                "pull", "--ff-only", iRemote, iTarget);
            if (!aPull.Ok)
            {
                Say(iLog, "✗ " + iLabel + " pull 失敗（可能分岔，需人工 merge/rebase）: " + aPull.ReasonLine);
                return SCP_GitSyncResult.Fail("✗ pull 失敗");
            }
            Say(iLog, "✓ " + iLabel + " pull: " + SCP_Git.FirstLine(aPull.StdOut));
            return SCP_GitSyncResult.Ok("✓ pull");
        }

        /// <summary>
        /// push 到一個或全部 remote。
        /// <para>⚠ push **不受 dirty 影響**：推的是已 commit 的東西，跟工作目錄乾不乾淨無關。</para>
        /// <para>多 remote 的規則：<list type="bullet">
        /// <item>remote 清單**即時重問** git，不吃掃描快照 —— 掃描後才加的 remote 會被照片漏掉，而漏掉不會叫</item>
        /// <item>一個 remote 失敗**不中斷**其他 remote —— GitHub 成功、GitLab 認證掛掉是兩件獨立的事，
        ///       為後者放棄前者等於白跑</item>
        /// <item>但整列記成**失敗** —— 部分成功不是成功</item>
        /// <item>該 repo 沒有任何 remote ⇒ 跳過並列出，不靜默算成 ✓
        ///       （那會讓報告說推完了，而其實一個位元組都沒出去）</item>
        /// </list></para>
        /// <para>⚠ push 端刻意**不**強制 fetch：non-fast-forward 被拒本來就很大聲，
        /// 先 fetch 只是把「遠端大聲拒絕」換成「本地大聲跳過」，沒換到資訊。</para>
        /// </summary>
        public static SCP_GitSyncResult Push(string iRepoDir, string iLabel, string iTarget,
            string iCurrentBranch, SCP_GitSyncOptions iOptions, Action<string>? iLog = null)
        {
            if (iOptions == null) iOptions = new SCP_GitSyncOptions();
            if (iCurrentBranch != iTarget)
            {
                Say(iLog, "⏭ " + iLabel + " 不在目標 branch（" + iCurrentBranch + " ≠ " + iTarget + "）—— 不 push");
                return SCP_GitSyncResult.Skip("⏭ 不在目標 branch");
            }

            var aRemotes = new List<string>();
            if (iOptions.PushAllRemotes)
            {
                List<string> aFound;
                if (!SCP_GitRepo.TryRemotes(iRepoDir, out aFound))
                {
                    Say(iLog, "✗ " + iLabel + " 讀不到 remote 清單 —— 不 push");
                    return SCP_GitSyncResult.Fail("✗ remote 讀取失敗");
                }
                if (aFound.Count == 0)
                {
                    // 「沒有 remote」不是成功也不是失敗，是**沒地方推**。
                    Say(iLog, "⏭ " + iLabel + " 沒有設定任何 remote —— 不 push");
                    return SCP_GitSyncResult.Skip("⏭ 無 remote");
                }
                aRemotes.AddRange(aFound);
            }
            else
            {
                aRemotes.Add(iOptions.PullRemote);
            }

            var aResult = new SCP_GitSyncResult();
            aResult.PushTotal = aRemotes.Count;
            for (int i = 0; i < aRemotes.Count; ++i)
            {
                string aRemote = aRemotes[i];
                SCP_GitResult aPush = SCP_Git.RunTimeout(iRepoDir, SCP_Git.NetworkTimeoutMs,
                    "push", aRemote, iTarget);
                if (!aPush.Ok)
                {
                    Say(iLog, "✗ " + iLabel + " push " + aRemote + " 失敗: " + aPush.ReasonLine);
                    aResult.PushFailedRemotes.Add(aRemote);
                    continue;
                }
                ++aResult.PushOk;
                // push 成功的訊息在 **stderr**（git 的慣例）—— 只看 stdout 會以為它沒說話。
                Say(iLog, "✓ " + iLabel + " push " + aRemote + ": " + aPush.ReasonLine);
            }

            if (aResult.PushFailedRemotes.Count > 0)
            {
                aResult.Outcome = SCP_GitSyncOutcome.Failed;
                aResult.Summary = aResult.PushOk > 0
                    ? "✗ push " + aResult.PushOk + "/" + aResult.PushTotal
                      + "（失敗: " + string.Join(",", aResult.PushFailedRemotes.ToArray()) + "）"
                    : "✗ push 失敗";
                return aResult;
            }
            aResult.Outcome = SCP_GitSyncOutcome.Ok;
            aResult.Summary = aResult.PushTotal > 1 ? "✓ push ×" + aResult.PushTotal : "✓";
            return aResult;
        }

        /// <summary>
        /// 對一個 repo 單獨跑 <c>fetch</c>（掃描前的「把尺換新」用）。
        /// <para>唯讀語意：只動 remote-tracking ref，不碰工作目錄。失敗不是致命的 ——
        /// 呼叫端要把它記成一行警告，然後**繼續用舊的尺並標明它是舊的**。</para>
        /// </summary>
        public static SCP_GitResult Fetch(string iRepoDir)
        {
            return SCP_Git.RunTimeout(iRepoDir, SCP_Git.NetworkTimeoutMs, "fetch", "--quiet");
        }

        static void Say(Action<string>? iLog, string iMessage)
        {
            Action<string>? aLog = iLog;
            if (aLog != null) aLog(iMessage);
        }
    }
}
