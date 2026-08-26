// 區塊職責：submodule 樹的列舉 ＋ `.gitmodules` 的 branch 欄 ＋「這一顆該追哪條 branch」的啟發式。
// 物理意義：多層 submodule 專案的日常痛點是「`submodule update` 之後全員 detached HEAD、
//           分支跑掉、誰 ahead 誰 behind 沒人一眼看得到」。要收成一張表，先要有這一層：
//           **一份清單 ＋ 每一顆的目標 branch 是怎麼決定的**。
//           ⚠ 目標 branch 的決定過程刻意攤開成四層（覆寫 > .gitmodules > 全域預設 > 啟發式），
//           因為「它為什麼想切到 Dev」是使用者一定會問的問題 ——
//           一個算好的答案不帶來源，人只能猜，而猜錯的代價是把別人的分支切掉。
// 數值影響：全部唯讀（`submodule status` / `config -f .gitmodules --get-regexp`）。
//           不動 index、不動工作目錄、不走網路。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace SCP.Core.Git
{
    /// <summary><c>git submodule status</c> 每行的第一個字元 —— 這一顆現在是什麼狀況。</summary>
    public enum SCP_GitSubmoduleFlag
    {
        /// <summary>認不得的旗標（git 加了新的？）—— 保守，當成不要動它。</summary>
        Unknown = 0,

        /// <summary>正常（旗標是空白）。</summary>
        Ok,

        /// <summary>旗標 <c>-</c>：還沒 init，**內容不在本機** ⇒ 沒有工作目錄可以問，也沒東西可以切。</summary>
        Uninitialized,

        /// <summary>旗標 <c>+</c>：目前 checkout 的 SHA 與 parent 記的 gitlink 不同（＝父層還沒 bump）。</summary>
        ShaMismatch,

        /// <summary>旗標 <c>U</c>：有合併衝突 ⇒ 人要先處理，工具一律不碰。</summary>
        Conflict,
    }

    /// <summary>一顆 submodule 的靜態身分（**不含**會變的狀態讀數 —— 那些走 SCP_GitRepo 逐項問）。</summary>
    public sealed class SCP_GitSubmoduleEntry
    {
        /// <summary>相對 root 的路徑，一律正斜線（`git submodule status` 本來就給正斜線）。</summary>
        public string Path = "";

        /// <summary>目前 checkout 的 SHA。</summary>
        public string Sha = "";

        public SCP_GitSubmoduleFlag Flag = SCP_GitSubmoduleFlag.Unknown;

        /// <summary>
        /// 擁有它的那份 `.gitmodules` 裡的 <c>branch =</c> 欄（空 ＝ 沒填）。
        /// <para>物理意義：這是 **git 原生欄位**，意思就是「這顆 submodule 該追哪條 branch」——
        /// 已經填了的直接尊重，不要拿工具的預設去蓋掉版控裡的宣告。</para>
        /// </summary>
        public string GitmodulesBranch = "";

        /// <summary>掃描時算好的啟發式預設（規則見 <see cref="SCP_GitSubmodule.HeuristicBranch"/>）。</summary>
        public string HeuristicBranch = "";

        /// <summary>還沒 init ⇒ 沒有工作目錄，所有逐項讀數與寫入動作都要跳過。</summary>
        public bool Uninitialized { get { return Flag == SCP_GitSubmoduleFlag.Uninitialized; } }

        /// <summary>巢狀深度（`a/b/c` ＝ 3）—— push 由深到淺排序用。</summary>
        public int Depth { get { return SCP_GitSubmodule.PathDepth(Path); } }
    }

    /// <summary>submodule 樹的唯讀查詢。</summary>
    public static class SCP_GitSubmodule
    {
        /// <summary>
        /// 列出 <paramref name="iRoot"/> 底下的 submodule。
        /// <para><paramref name="iRecursive"/>=true ＝ 連巢狀的一起列（路徑是相對 root 的完整路徑）。</para>
        /// <para>回 false ＝ <c>git submodule status</c> 本身失敗（<paramref name="oError"/> 帶原因）；
        /// 回 true ＋ 空清單 ＝ **這個 repo 真的沒有 submodule**。⚠ 這兩件事不得同形：
        /// 壓成「空清單」之後，一個壞掉的 repo 會看起來完全正常。</para>
        /// </summary>
        public static bool TryStatus(string iRoot, bool iRecursive,
            out List<SCP_GitSubmoduleEntry> oList, out string oError)
        {
            oList = new List<SCP_GitSubmoduleEntry>();
            oError = "";
            SCP_GitResult aRes = iRecursive
                ? SCP_Git.Run(iRoot, "submodule", "status", "--recursive")
                : SCP_Git.Run(iRoot, "submodule", "status");
            if (!aRes.Ok)
            {
                oError = aRes.FirstLine;
                return false;
            }
            List<string> aLines = SCP_Git.SplitLines(aRes.StdOut);
            for (int i = 0; i < aLines.Count; ++i)
            {
                SCP_GitSubmoduleEntry? aEntry = ParseStatusLine(aLines[i]);
                if (aEntry != null) oList.Add(aEntry);
            }
            return true;
        }

        /// <summary>
        /// 解析一行 <c>git submodule status</c>：<c>&lt;flag&gt;&lt;sha&gt; &lt;path&gt; (&lt;desc&gt;)</c>。
        /// <para>⚠ 路徑**不是**用空白切出來的：submodule 的資料夾名可以含空白，
        /// 而「切第二段」在那種情況下會安靜地拿到半個路徑（然後所有後續操作都對著一個不存在的目錄跑，
        /// 每一格都失敗但每一格的原因都看起來像別的問題）。
        /// 作法：sha 是固定長度 ⇒ 路徑＝其後到行尾，再把尾巴的 <c>" (…)"</c> 剝掉。</para>
        /// </summary>
        public static SCP_GitSubmoduleEntry? ParseStatusLine(string iLine)
        {
            if (string.IsNullOrEmpty(iLine)) return null;
            string aLine = iLine.TrimEnd();
            // 旗標那一格：空白也是合法值，而 SplitLines 已經把行首空白 Trim 掉了 ⇒
            // 這裡靠「第一個字元是不是 16 進位」反推：不是的話那就是旗標。
            char aFlagChar = aLine[0];
            SCP_GitSubmoduleFlag aFlag;
            int aShaStart;
            if (IsHex(aFlagChar))
            {
                aFlag = SCP_GitSubmoduleFlag.Ok;   // 行首空白被 Trim 掉了 ⇒ 原本是 ' '
                aShaStart = 0;
            }
            else
            {
                aFlag = FlagFromChar(aFlagChar);
                aShaStart = 1;
            }
            int aSpace = aLine.IndexOf(' ', aShaStart);
            if (aSpace < 0) return null;                     // 只有 sha 沒有路徑 ⇒ 不是我們認得的形狀
            string aSha = aLine.Substring(aShaStart, aSpace - aShaStart);
            if (aSha.Length == 0) return null;

            string aRest = aLine.Substring(aSpace + 1).Trim();
            if (aRest.Length == 0) return null;
            // 尾巴的 `(<describe>)` 是給人看的，不是路徑的一部分。
            // ⚠ 只在真的以 ')' 收尾時才剝，而且找**最後**一個 " (" —— 路徑自己含括號時才不會被切錯。
            if (aRest[aRest.Length - 1] == ')')
            {
                int aOpen = aRest.LastIndexOf(" (", StringComparison.Ordinal);
                if (aOpen > 0) aRest = aRest.Substring(0, aOpen).TrimEnd();
            }

            var aEntry = new SCP_GitSubmoduleEntry();
            aEntry.Sha = aSha;
            aEntry.Path = aRest.Replace('\\', '/');
            aEntry.Flag = aFlag;
            return aEntry;
        }

        /// <summary>
        /// 把 `.gitmodules` 的 <c>branch =</c> 欄回填到清單上。
        /// <para>物理意義：要查的不只 root —— **每一顆自己還有 `.gitmodules` 的 submodule 都要查**，
        /// 否則巢狀第二層以下的 branch 宣告會整批被忽略（而忽略的症狀是它們默默落到啟發式那一層）。</para>
        /// <para>作法：`config -f .gitmodules --get-regexp` 一次拿 path ＋ branch 兩種鍵，
        /// 用 <c>submodule.&lt;name&gt;</c> 這段共同前綴配對（**不要**假設 path 等於 name，
        /// 那兩個欄位在 git 裡本來就可以不一樣）。</para>
        /// </summary>
        public static void FillGitmodulesBranch(string iRoot, List<SCP_GitSubmoduleEntry> iList)
        {
            if (iList == null) return;

            // 擁有者清單：root（""）＋ 每一顆 submodule
            var aOwners = new List<string>();
            aOwners.Add("");
            for (int i = 0; i < iList.Count; ++i) aOwners.Add(iList[i].Path);

            for (int i = 0; i < aOwners.Count; ++i)
            {
                string aOwner = aOwners[i];
                string aOwnerAbs = aOwner.Length == 0 ? iRoot : Path.Combine(iRoot, aOwner);
                if (!File.Exists(Path.Combine(aOwnerAbs, ".gitmodules"))) continue;

                SCP_GitResult aRes = SCP_Git.Run(aOwnerAbs,
                    "config", "-f", ".gitmodules", "--get-regexp", "submodule\\..*\\.(path|branch)");
                if (!aRes.Ok) continue;

                var aNameToPath = new Dictionary<string, string>();
                var aNameToBranch = new Dictionary<string, string>();
                List<string> aLines = aRes.OutLines();
                for (int j = 0; j < aLines.Count; ++j)
                {
                    string aLine = aLines[j];
                    int aSpace = aLine.IndexOf(' ');
                    if (aSpace <= 0) continue;
                    string aKey = aLine.Substring(0, aSpace);
                    string aValue = aLine.Substring(aSpace + 1).Trim();
                    if (aKey.EndsWith(".path", StringComparison.Ordinal))
                        aNameToPath[aKey.Substring(0, aKey.Length - ".path".Length)] = aValue;
                    else if (aKey.EndsWith(".branch", StringComparison.Ordinal))
                        aNameToBranch[aKey.Substring(0, aKey.Length - ".branch".Length)] = aValue;
                }

                foreach (KeyValuePair<string, string> aPair in aNameToBranch)
                {
                    string aRelPath;
                    if (!aNameToPath.TryGetValue(aPair.Key, out aRelPath)) continue;
                    string aFull = aOwner.Length == 0 ? aRelPath : aOwner + "/" + aRelPath;
                    SCP_GitSubmoduleEntry? aHit = Find(iList, aFull);
                    if (aHit != null) aHit.GitmodulesBranch = aPair.Value;
                }
            }
        }

        /// <summary>
        /// 「資料夾名前綴 → 該追哪條 branch」的宿主規則（先命中先贏，比對區分大小寫）。
        /// <para>物理意義：這是**專案自己的命名慣例**，不是 git 的性質 ——
        /// 寫死在共用層等於把一個專案的家規變成所有專案的預設，
        /// 而下一個專案要嘛被它偷偷影響、要嘛得再開一個開關繞過它。⇒ 由宿主宣告。</para>
        /// <para>例（LY 系）：<c>SCP_GitSubmodule.PrefixBranchRules.Add(
        /// new KeyValuePair&lt;string,string&gt;("UCL_", "Dev"));</c></para>
        /// <para>預設是**空的** —— 沒宣告就直接走下面兩條與命名無關的通用規則。</para>
        /// </summary>
        public static readonly List<KeyValuePair<string, string>> PrefixBranchRules
            = new List<KeyValuePair<string, string>>();

        /// <summary>
        /// 「這一顆大概該追哪條 branch」的啟發式 —— **四層解析的最後一層**，前三層都空才用它。
        /// <para>規則（骨架取自 Tim 2026-08-07 拍板）：</para>
        /// <list type="number">
        /// <item>資料夾名命中 <see cref="PrefixBranchRules"/> → 該規則指定的 branch（宿主宣告，預設無）</item>
        /// <item>全 repo（本地＋remote）只有一條 branch → 就是它（沒有歧義可言）</item>
        /// <item>其餘 → <c>master</c>；沒有 master 才 <c>main</c>
        ///       （2020 前的 repo 是 master、之後是 main；兩者並存時 master 贏）</item>
        /// </list>
        /// <para>⚠ 猜不到就回**空字串**，呼叫端要因此**跳過並列出**這一顆 ——
        /// 絕不可以拿「目前所在 branch」頂替：那會讓一顆分支跑掉的 submodule
        /// 被「同步」到它現在誤停的地方，而報告會印 ✓。</para>
        /// </summary>
        public static string HeuristicBranch(string iPath, SCP_GitBranchList iBranches)
        {
            string aDirName = LastSegment(iPath);
            for (int i = 0; i < PrefixBranchRules.Count; ++i)
            {
                KeyValuePair<string, string> aRule = PrefixBranchRules[i];
                if (!string.IsNullOrEmpty(aRule.Key)
                    && aDirName.StartsWith(aRule.Key, StringComparison.Ordinal))
                    return aRule.Value;
            }
            if (!iBranches.Known) return "";

            List<string> aLocal = iBranches.Local ?? new List<string>();
            List<string> aAll = iBranches.All ?? new List<string>();
            if (aLocal.Count == 1) return aLocal[0];
            if (aLocal.Count == 0 && aAll.Count == 1) return aAll[0];
            if (aAll.Contains("master")) return "master";
            if (aAll.Contains("main")) return "main";
            return "";
        }

        /// <summary>
        /// 目標 branch 的四層解析。回空字串 ＝ **解析不到**（呼叫端跳過並列出，不要自己補一個值）。
        /// <para>順序：逐項覆寫 &gt; `.gitmodules` 的 branch 欄 &gt; 全域預設 &gt; 啟發式。</para>
        /// </summary>
        public static string ResolveTargetBranch(SCP_GitSubmoduleEntry iEntry,
            string? iOverride, string? iGlobalDefault)
        {
            if (!string.IsNullOrEmpty(iOverride)) return iOverride!;
            return ResolveAutoBranch(iEntry, iGlobalDefault);
        }

        /// <summary>
        /// 覆寫**以外**的三層。
        /// <para>為什麼要單獨開這一支：UI 上的「(自動)」選項要顯示「不覆寫的話會變成什麼」——
        /// 顯示含覆寫的結果，人在選之前就看不到自動會挑誰。</para>
        /// </summary>
        public static string ResolveAutoBranch(SCP_GitSubmoduleEntry iEntry, string? iGlobalDefault)
        {
            if (iEntry == null) return "";
            if (!string.IsNullOrEmpty(iEntry.GitmodulesBranch)) return iEntry.GitmodulesBranch;
            if (!string.IsNullOrEmpty(iGlobalDefault)) return iGlobalDefault!;
            return iEntry.HeuristicBranch ?? "";
        }

        /// <summary>
        /// 依路徑深度**由深到淺**排序 —— push 的順序不變量。
        /// <para>物理意義：parent 的 bump commit 引用 child 的 SHA。先推 parent 的話，
        /// 別人 pull 下來會拿到一個**指向遠端還不存在的 commit** 的 gitlink ——
        /// 而且是靜默壞（只有 clone / update 的人才會發現）。⇒ 巢狀最深的先推，root 最後。</para>
        /// </summary>
        public static void SortDeepestFirst(List<SCP_GitSubmoduleEntry> iList)
        {
            if (iList == null) return;
            iList.Sort((iA, iB) => iB.Depth.CompareTo(iA.Depth));
        }

        /// <summary>巢狀深度（`a/b/c` ＝ 3；空路徑 ＝ 0，代表 root）。</summary>
        public static int PathDepth(string? iPath)
        {
            if (string.IsNullOrEmpty(iPath)) return 0;
            int aDepth = 1;
            for (int i = 0; i < iPath!.Length; ++i)
            {
                if (iPath[i] == '/') ++aDepth;
            }
            return aDepth;
        }

        /// <summary>按路徑找一顆（找不到回 null）。</summary>
        public static SCP_GitSubmoduleEntry? Find(List<SCP_GitSubmoduleEntry> iList, string iPath)
        {
            if (iList == null) return null;
            for (int i = 0; i < iList.Count; ++i)
            {
                if (string.Equals(iList[i].Path, iPath, StringComparison.Ordinal)) return iList[i];
            }
            return null;
        }

        static string LastSegment(string? iPath)
        {
            if (string.IsNullOrEmpty(iPath)) return "";
            int aSlash = iPath!.LastIndexOf('/');
            return aSlash < 0 ? iPath : iPath.Substring(aSlash + 1);
        }

        static bool IsHex(char iChar)
        {
            return (iChar >= '0' && iChar <= '9')
                || (iChar >= 'a' && iChar <= 'f')
                || (iChar >= 'A' && iChar <= 'F');
        }

        static SCP_GitSubmoduleFlag FlagFromChar(char iChar)
        {
            switch (iChar)
            {
                case ' ': return SCP_GitSubmoduleFlag.Ok;
                case '-': return SCP_GitSubmoduleFlag.Uninitialized;
                case '+': return SCP_GitSubmoduleFlag.ShaMismatch;
                case 'U': return SCP_GitSubmoduleFlag.Conflict;
                // 認不得的旗標回 Unknown 而不是 Ok —— git 哪天多一種狀態時，
                // 「不認得」該讓工具保守跳過，不該讓它當成正常照樣動手。
                default: return SCP_GitSubmoduleFlag.Unknown;
            }
        }
    }
}
