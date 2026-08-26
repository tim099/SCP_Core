// 區塊職責：對**單一 repo** 取唯讀讀數 —— branch / 髒不髒 / staged / remote / branch 清單 /
//           ahead-behind / 上次 fetch 多久以前。
// 物理意義：這一層刻意**只讀不寫**：分成兩個檔（讀在這、寫在 SCP_GitSync）之後，
//           「這個方法會不會動到我的工作區」不必看實作就知道 —— 看它在哪個檔。
//           ⚠ 每一格都要能分辨**三態**：問到了是 A / 問到了是 B / 問不到。
//           把「問不到」壓成其中一個答案是這套系統最貴的錯誤形狀
//           （🩸 LY 2026-08-21：查無帳戶被回成餘額 0，於是「不存在」長得跟「零」一樣）。
// 數值影響：全部唯讀。`status` / `rev-parse` / `for-each-ref` / 讀 FETCH_HEAD 的 mtime，
//           不動 index、不動工作目錄、不走網路（⇒ ahead-behind 的新鮮度取決於上次 fetch，
//           所以 LastFetchUtc 存在的理由就是讓呼叫端**逐 repo**標出那把尺有多舊）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace SCP.Core.Git
{
    /// <summary>
    /// 工作目錄乾淨嗎 —— **三態**。
    /// <para><see cref="Unknown"/> 存在的理由：<c>git status</c> 失敗（鎖住／權限／不是 repo）時
    /// 不可以回 Clean。安全線讀到 Clean 就會放行，於是一個問不到答案的 repo 會被當成可以動的
    /// —— 而它的報告會印 ✓。</para>
    /// </summary>
    public enum SCP_GitDirtyState
    {
        /// <summary>問不到（status 失敗）。⚠ 安全線一律當成**擋下**處理。</summary>
        Unknown = 0,
        Clean,
        Dirty,
    }

    /// <summary>本地 / 本地＋remote 的 branch 名清單。</summary>
    public struct SCP_GitBranchList
    {
        /// <summary>本地 branch（<c>refs/heads</c>）。</summary>
        public List<string> Local;

        /// <summary>本地 ＋ 指定 remote 的 branch 名（去重、已去掉 <c>remote/</c> 前綴與 HEAD）。</summary>
        public List<string> All;

        /// <summary>問不到（<c>for-each-ref</c> 失敗）—— 空清單與「問不到」不同形。</summary>
        public bool Known;
    }

    /// <summary>對 upstream 的 ahead / behind。</summary>
    public struct SCP_GitAheadBehind
    {
        /// <summary>
        /// 問到了嗎。false ＝ 沒設 upstream / 沒 fetch 過。
        /// <para>⚠ 問不到時**不要顯示 0** —— 0 的意思是「對齊」，那是一個答案，不是沒有答案。</para>
        /// </summary>
        public bool Known;
        public int Ahead;
        public int Behind;
    }

    /// <summary>單一 repo 的唯讀讀數。</summary>
    public static class SCP_GitRepo
    {
        /// <summary>
        /// 目前 branch；detached 時回 <see cref="SCP_Git.DetachedHead"/>（<c>"HEAD"</c>），問不到回 null。
        /// </summary>
        public static string? Branch(string iDir)
        {
            SCP_GitResult aRes = SCP_Git.Run(iDir, "rev-parse", "--abbrev-ref", "HEAD");
            return aRes.Ok ? aRes.StdOut.Trim() : null;
        }

        /// <summary>detached HEAD 嗎（問不到也回 true —— 狀態不明時不該被當成「在某條分支上」）。</summary>
        public static bool IsDetached(string iDir)
        {
            string? aBranch = Branch(iDir);
            return aBranch == null || aBranch == SCP_Git.DetachedHead;
        }

        /// <summary>
        /// 安全線用的「髒不髒」尺：**有沒有未 commit 的追蹤檔修改**（<c>--untracked-files=no</c>）。
        /// <para>⚠ untracked 檔刻意**不算髒**：算進來會讓每個有 <c>Library/</c> 殘檔的 submodule 都紅，
        /// 而假警報會訓練人忽略警報 —— 那比沒有警報糟。</para>
        /// <para>⚠ 這把尺**每次要動之前重新量**，不要拿掃描時的快照
        /// （🩸 Unity Editor 在兩次點擊之間會 import asset、寫 .meta、存 scene ——
        /// 照片乾淨、現在髒了的話，「dirty 就跳過」這個承諾會靜默失效，而報告照印 ✓）。</para>
        /// </summary>
        public static SCP_GitDirtyState DirtyState(string iDir)
        {
            SCP_GitResult aRes = SCP_Git.Run(iDir, "status", "--porcelain", "--untracked-files=no");
            if (!aRes.Ok) return SCP_GitDirtyState.Unknown;
            return aRes.StdOut.Trim().Length > 0 ? SCP_GitDirtyState.Dirty : SCP_GitDirtyState.Clean;
        }

        /// <summary>
        /// 工作區有幾筆改動（**含 untracked**）—— 給人看的「這個專案動了多少」，問不到回 null。
        /// <para>⚠ 跟 <see cref="DirtyState"/> 是**兩把不同的尺**，不要互相代用：
        /// 這一把含 untracked（適合顯示），那一把不含（適合當安全線）。
        /// 名字寫明白，因為「為什麼顯示 12 筆改動卻說 clean」是一定會有人問的問題。</para>
        /// </summary>
        public static int? ChangeCount(string iDir)
        {
            SCP_GitResult aRes = SCP_Git.Run(iDir, "status", "--porcelain=v1", "--untracked-files=all");
            if (!aRes.Ok) return null;
            return aRes.OutLines().Count;
        }

        /// <summary>
        /// 呼叫本工具**之前**就已經 staged 的檔案清單（空 ＝ index 乾淨）。
        /// <para>物理意義：自動 commit 一類的動作要用它當「別人正在寫東西」的證據 ——
        /// 有人先 stage 了一半，外部工具就不該接手 index。</para>
        /// </summary>
        public static List<string> StagedPaths(string iDir)
        {
            SCP_GitResult aRes = SCP_Git.Run(iDir, "diff", "--cached", "--name-only");
            return aRes.Ok ? aRes.OutLines() : new List<string>();
        }

        /// <summary>
        /// 這個 repo 的 remote 名清單（問不到回空清單，呼叫端要自己分辨「沒有 remote」與「問不到」——
        /// 兩者都是空的，所以需要時用 <see cref="TryRemotes"/>）。
        /// </summary>
        public static List<string> Remotes(string iDir)
        {
            List<string> aList;
            TryRemotes(iDir, out aList);
            return aList;
        }

        /// <summary>remote 清單 ＋「有沒有問到」。回 false ＝ <c>git remote</c> 本身失敗。</summary>
        public static bool TryRemotes(string iDir, out List<string> oRemotes)
        {
            SCP_GitResult aRes = SCP_Git.Run(iDir, "remote");
            oRemotes = aRes.Ok ? aRes.OutLines() : new List<string>();
            return aRes.Ok;
        }

        /// <summary>
        /// branch 名清單（本地 ＋ 指定 remote，去重排序）。
        /// <para>物理意義：**一定要把 remote 那半算進來** —— submodule update 完的 repo 常常一條
        /// 本地 branch 都沒有（detached），只看本地會讓所有靠 branch 清單的判斷整批失效。</para>
        /// </summary>
        public static SCP_GitBranchList Branches(string iDir, string iRemote = "origin")
        {
            var aOut = new SCP_GitBranchList();
            aOut.Local = new List<string>();
            aOut.All = new List<string>();
            aOut.Known = false;

            // ⚠ 用 `%(refname)`（**全名**）而不是 `%(refname:short)`。
            // 🩸 實測（2026-08-26，git 2.49）：`refs/remotes/origin/HEAD` 的 short 形是
            //    **`origin`** —— 它不以 `origin/` 開頭，於是「以 remote 名開頭就算遠端」那種寫法
            //    會把它當成一條**本地分支**收進清單。後果不是多一個名字而已：
            //    「本地只有一條分支 → 就用它」這條啟發式會因為 local 從 1 變 2 而失效，
            //    而失效的樣子是「它挑了另一條合理的分支」，看不出有東西壞了。
            //    全名沒有這種歧義：字面就分得出 heads / remotes，也認得出 HEAD 那顆指標。
            SCP_GitResult aRes = SCP_Git.Run(iDir,
                "for-each-ref", "--format=%(refname)", "refs/heads", "refs/remotes/" + iRemote);
            if (!aRes.Ok) return aOut;

            aOut.Known = true;
            const string aHeadsPrefix = "refs/heads/";
            string aRemotePrefix = "refs/remotes/" + iRemote + "/";
            var aSeen = new HashSet<string>();
            List<string> aLines = aRes.OutLines();
            for (int i = 0; i < aLines.Count; ++i)
            {
                string aRef = aLines[i];
                if (aRef.StartsWith(aHeadsPrefix, StringComparison.Ordinal))
                {
                    string aName = aRef.Substring(aHeadsPrefix.Length);
                    if (aName.Length == 0) continue;
                    aOut.Local.Add(aName);
                    if (aSeen.Add(aName)) aOut.All.Add(aName);
                }
                else if (aRef.StartsWith(aRemotePrefix, StringComparison.Ordinal))
                {
                    string aName = aRef.Substring(aRemotePrefix.Length);
                    // `origin/HEAD` 是一顆**指標**（指向預設分支），不是一條分支本身。
                    if (aName.Length == 0 || aName == "HEAD") continue;
                    if (aSeen.Add(aName)) aOut.All.Add(aName);
                }
                // 其餘（refs/tags 之類）不該出現在這個查詢裡 —— 出現了就是 git 換了行為，
                // 靜默收進清單會讓 tag 名變成可以 checkout 的「分支」。
            }
            aOut.All.Sort(StringComparer.OrdinalIgnoreCase);
            aOut.Local.Sort(StringComparer.OrdinalIgnoreCase);
            return aOut;
        }

        /// <summary>本地有這條 branch 嗎（<c>refs/heads/&lt;name&gt;</c>）。</summary>
        public static bool HasLocalBranch(string iDir, string iBranch)
        {
            return SCP_Git.Run(iDir, "rev-parse", "--verify", "--quiet", "refs/heads/" + iBranch).Ok;
        }

        /// <summary>remote-tracking 有這條 branch 嗎（<c>refs/remotes/&lt;remote&gt;/&lt;name&gt;</c>）。</summary>
        public static bool HasRemoteBranch(string iDir, string iBranch, string iRemote = "origin")
        {
            return SCP_Git.Run(iDir, "rev-parse", "--verify", "--quiet",
                "refs/remotes/" + iRemote + "/" + iBranch).Ok;
        }

        /// <summary>
        /// <paramref name="iMaybeAncestor"/> 是 <paramref name="iDescendant"/> 的祖先嗎。
        /// <para>⚠ 這是「切走會不會掉東西」那道安全線的核心判斷 ——
        /// 而它的答案只跟**那把尺有多新**一樣可靠（見 <see cref="LastFetchUtc"/>）。</para>
        /// </summary>
        public static bool IsAncestor(string iDir, string iMaybeAncestor, string iDescendant)
        {
            return SCP_Git.Run(iDir, "merge-base", "--is-ancestor", iMaybeAncestor, iDescendant).Ok;
        }

        /// <summary>對 <c>@{upstream}</c> 的 ahead / behind。沒設 upstream ⇒ <c>Known=false</c>。</summary>
        public static SCP_GitAheadBehind AheadBehind(string iDir)
        {
            var aOut = new SCP_GitAheadBehind();
            SCP_GitResult aRes = SCP_Git.Run(iDir, "rev-list", "--left-right", "--count", "@{upstream}...HEAD");
            if (!aRes.Ok) return aOut;
            string[] aParts = aRes.StdOut.Trim().Split('\t');
            int aBehind;
            int aAhead;
            if (aParts.Length == 2 && int.TryParse(aParts[0], out aBehind) && int.TryParse(aParts[1], out aAhead))
            {
                aOut.Known = true;
                aOut.Behind = aBehind;
                aOut.Ahead = aAhead;
            }
            return aOut;
        }

        /// <summary>這個 repo 的 <c>.git</c> 目錄絕對路徑（submodule 是 <c>.git/modules/…</c>），問不到回 null。</summary>
        public static string? GitDir(string iDir)
        {
            SCP_GitResult aRes = SCP_Git.Run(iDir, "rev-parse", "--git-dir");
            if (!aRes.Ok) return null;
            string aPath = aRes.StdOut.Trim();
            if (aPath.Length == 0) return null;
            return Path.IsPathRooted(aPath) ? aPath : Path.Combine(iDir, aPath);
        }

        /// <summary>
        /// 上一次 fetch 的時間（<c>FETCH_HEAD</c> 的 mtime，UTC）；沒 fetch 過回 null。
        /// <para>物理意義：ahead / behind 是拿 remote-tracking ref 量的，而那個 ref 只有 fetch
        /// 才會動 ⇒ **它的新鮮度就是這個時間**。呼叫端要**逐 repo**標出來：
        /// 一句全域警語會把「剛 fetch 的」跟「三天沒動的」混為一談。</para>
        /// </summary>
        public static DateTime? LastFetchUtc(string iDir)
        {
            string? aGitDir = GitDir(iDir);
            if (aGitDir == null) return null;
            string aFetchHead = Path.Combine(aGitDir, "FETCH_HEAD");
            if (!File.Exists(aFetchHead)) return null;
            try { return File.GetLastWriteTimeUtc(aFetchHead); }
            catch { return null; }
        }
    }
}
