// 區塊職責：**跑一條 git 指令** —— 這一層是整個共用碼裡唯一的 git process 出口。
// 物理意義：判準是「跟人在終端機打的那顆 git 逐字同行為」——
//           .gitignore 邊界、submodule、CRLF、hooks、credential helper 的細節，
//           換一套實作（libgit2 之類）就會有差異，而那種差異**不報錯**，
//           只會在某天生出一筆內容不對的 commit。⇒ 只呼叫真的 git.exe，不做第二套實作。
//           ⭐ 為什麼要「唯一出口」：護欄（quotepath / TERMINAL_PROMPT / 逾時 kill）
//           每多一份實作就多一份可能漏掉其中一格的地方，而漏掉的症狀全是靜默的。
// 數值影響：本層不判斷指令的讀寫語意 —— 那由呼叫端的 args 決定（寫入端集中在 SCP_GitSync）。
//           每次呼叫固定釘兩個東西：
//           · `-c core.quotepath=false` —— 否則非 ASCII 路徑會被印成 C 風格八進位轉義
//             （一個中文字＝三段反斜線碼），拿去比對就會把每個中文檔名都判成「不一樣」。
//             🩸 LY 專案 2026-08-22 實撞。
//           · `GIT_TERMINAL_PROMPT=0` —— 非互動環境不該彈認證視窗；彈了就是卡到逾時才有人發現，
//             而「卡住的失敗」是最難抓的一種。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）—— 不用 record struct、不用檔案級 namespace。
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using SCP.Core.Proc;

namespace SCP.Core.Git
{
    /// <summary>
    /// 一次 git 呼叫的結果。
    /// <para>⚠ 刻意把 <see cref="Exit"/> 留在外面而不只給一個 bool：
    /// 有些指令用 exit code 當答案（<c>rev-parse --verify --quiet</c> ＝ 存在性查詢、
    /// <c>merge-base --is-ancestor</c> ＝ 祖先判定），把它壓成成功／失敗會把答案丟掉。</para>
    /// </summary>
    public struct SCP_GitResult
    {
        public int Exit;
        public string StdOut;
        public string StdErr;

        public SCP_GitResult(int iExit, string iStdOut, string iStdErr)
        {
            Exit = iExit;
            StdOut = iStdOut ?? "";
            StdErr = iStdErr ?? "";
        }

        public bool Ok { get { return Exit == 0; } }

        /// <summary>給人看的一句：有 stderr 就用它，否則用 stdout（git 的成功訊息常在 stderr）。</summary>
        public string Message
        {
            get { return string.IsNullOrWhiteSpace(StdErr) ? (StdOut ?? "").Trim() : StdErr.Trim(); }
        }

        /// <summary>把 <see cref="Message"/> 收成一行 —— 報告逐項一行時用（多行會把表格撐爛）。</summary>
        public string FirstLine { get { return SCP_Git.FirstLine(Message); } }

        /// <summary>
        /// **真正在講結果的那一行** —— 報告 push / pull 這類多行輸出時用它，不要用 <see cref="FirstLine"/>。
        /// <para>🩸 實測（2026-08-26 沙盒）：`git push` 的 stderr 第一行是 <c>To &lt;url&gt;</c>，
        /// 而真正的內容（失敗時是 <c>! [rejected] … (non-fast-forward)</c>、
        /// 成功時是 <c>fee8294..c4c832a master -&gt; master</c>）在後面。
        /// 於是「印第一行」會產出一句**看起來像有講原因、其實只講了推去哪**的訊息 ——
        /// 而那種訊息比沒有訊息糟，因為它讓人不再往下查。</para>
        /// <para>成功與失敗共用同一支：兩邊要挑的都是「最有資訊的那一行」，
        /// 分成兩支只會讓其中一支哪天忘了同步。</para>
        /// </summary>
        public string ReasonLine { get { return SCP_Git.ReasonLine(Message); } }

        /// <summary>stdout 逐行切開（空行丟掉）。</summary>
        public List<string> OutLines() { return SCP_Git.SplitLines(StdOut); }
    }

    /// <summary>git CLI 的共用封裝。認證走系統 credential manager，與命令列行為一致。</summary>
    public static class SCP_Git
    {
        /// <summary>本機操作的逾時上限。命中代表卡住（鎖／磁碟），不是「檔案很多」。</summary>
        public const int DefaultTimeoutMs = 120_000;

        /// <summary>
        /// 走網路的操作（fetch / pull / push）的逾時上限。
        /// <para>⚠ 跟本機操作**分開兩個常數**：同一個數字要嘛對網路太短、要嘛對本機太長，
        /// 而「太長」的代價是卡住的失敗要等五分鐘才看得到。</para>
        /// </summary>
        public const int NetworkTimeoutMs = 300_000;

        /// <summary><c>--pathspec-from-file</c> 需要的最低 git 版本（2.25，2020 年）。</summary>
        public static readonly Version MinVersionForPathspecFromFile = new Version(2, 25);

        /// <summary>
        /// detached HEAD 時 <c>rev-parse --abbrev-ref HEAD</c> 的回答。
        /// <para>⚠ 這是一種**擋下的理由**，不是分支名 —— 拿它去 checkout / push 都是錯的。</para>
        /// </summary>
        public const string DetachedHead = "HEAD";

        /// <summary>
        /// 每條指令跑之前叫一次（給 CLI 的 verbose／視窗端的進度用）。null ＝ 不追蹤。
        /// <para>⚠ 只用來**看**，不要拿它當同步點 —— 背景執行緒會呼叫它。</para>
        /// </summary>
        public static Action<string>? Trace;

        /// <summary>沒有人開 <see cref="Scope"/> 時，登記用的 tag。</summary>
        public const string DefaultProcessTag = "git";

        // 區塊職責：這一輪 git 呼叫要用哪個 tag / owner 去登記（見 SCP_ProcessRegistry）。
        // 物理意義：一次批次動作會打幾十條 git，把 tag/owner 塞進每一條的參數列只會讓呼叫端全是雜訊，
        //          而漏傳的那幾條就變成登記不到主人的孤兒。⇒ 做成「這一段程式碼的環境」。
        // ⚠ 為什麼是 [ThreadStatic]：批次跑在背景執行緒，而 UI 執行緒同時可能在跑自己的 git
        //   （例如另一頁在掃描）。共用一份 static 會讓兩邊互相蓋掉對方的 tag，
        //   於是「收掉上一輪批次」會收到別人的 process 上 —— 那正是 registry 要防的誤殺。
        //   一條執行緒一份，語意才跟「一次批次」對得起來。
        [ThreadStatic] static string? s_ScopeTag;
        [ThreadStatic] static string? s_ScopeOwner;

        /// <summary>
        /// 宣告接下來這一段的 git 呼叫都算在 <paramref name="iTag"/> 名下（登記用）。
        /// <code>
        /// using (SCP_Git.Scope("git_submodule_sync", nameof(MyPage)))
        /// {
        ///     // …這裡面所有 Run 都以該 tag 登記…
        /// }
        /// </code>
        /// <para>⚠ 本 scope **不做** singleton guard（不會收掉既存同 tag）——
        /// 「收掉上一輪」的粒度是**一次批次**，不是一條指令。要那個效果就在批次開始前
        /// 自己呼叫一次 <c>SCP_ProcessRegistry.KillAllByTag</c>。</para>
        /// </summary>
        public static IDisposable Scope(string iTag, string iOwner = "")
        {
            return new ProcessScope(iTag, iOwner);
        }

        sealed class ProcessScope : IDisposable
        {
            readonly string? m_PrevTag;
            readonly string? m_PrevOwner;

            public ProcessScope(string iTag, string iOwner)
            {
                // 舊值存下來再蓋 —— 巢狀（掃描裡再跑一個子動作）要能還原，
                // 直接清成 null 會讓外層剩下的呼叫掉回預設 tag，而那不會報錯。
                m_PrevTag = s_ScopeTag;
                m_PrevOwner = s_ScopeOwner;
                s_ScopeTag = iTag;
                s_ScopeOwner = iOwner;
            }

            public void Dispose()
            {
                s_ScopeTag = m_PrevTag;
                s_ScopeOwner = m_PrevOwner;
            }
        }

        /// <summary>
        /// 執行 <c>git &lt;args&gt;</c>（同步，會擋住呼叫端 ⇒ UI 端只在背景執行緒呼叫）。
        /// <para>⚠ 引數是 <c>params string[]</c> 而**不是**一整串字：
        /// 自己拼字串就要自己處理引號，而含空白／中文的路徑一旦拼錯，git 會拿到一個
        /// 「合法但不是你要的」引數 —— 那不會報錯。逐個傳進 ArgumentList 讓 OS 層負責跳脫。</para>
        /// </summary>
        public static SCP_GitResult Run(string iWorkDir, params string[] iArgs)
        {
            return RunTimeout(iWorkDir, DefaultTimeoutMs, iArgs);
        }

        /// <summary>同 <see cref="Run"/>，但自訂逾時（走網路的指令用 <see cref="NetworkTimeoutMs"/>）。</summary>
        public static SCP_GitResult RunTimeout(string iWorkDir, int iTimeoutMs, params string[] iArgs)
        {
            var aInfo = new ProcessStartInfo("git");
            aInfo.WorkingDirectory = iWorkDir;
            aInfo.RedirectStandardOutput = true;
            aInfo.RedirectStandardError = true;
            aInfo.UseShellExecute = false;
            aInfo.CreateNoWindow = true;
            aInfo.StandardOutputEncoding = Encoding.UTF8;
            aInfo.StandardErrorEncoding = Encoding.UTF8;

            // 護欄①：非 ASCII 路徑印回原字（見檔頭血證）。釘在這裡，不靠呼叫端記得。
            aInfo.ArgumentList.Add("-c");
            aInfo.ArgumentList.Add("core.quotepath=false");
            for (int i = 0; i < iArgs.Length; ++i)
            {
                if (iArgs[i] == null) continue;
                aInfo.ArgumentList.Add(iArgs[i]);
            }
            // 護欄②：沒有終端可以輸入密碼，所以不要停在那裡等。
            aInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";

            Action<string>? aTrace = Trace;
            if (aTrace != null) aTrace("git " + string.Join(" ", iArgs) + "  @ " + iWorkDir);

            var aOut = new StringBuilder();
            var aErr = new StringBuilder();
            using (var aProc = new Process())
            {
                aProc.StartInfo = aInfo;
                // 雙 stream **非同步**讀：同步 ReadToEnd 兩條會死鎖（一條的 buffer 滿了，
                // 對方就卡在寫），而死鎖的症狀是「按了沒反應」，跟「很慢」分不出來。
                aProc.OutputDataReceived += (iSender, iEvent) => { if (iEvent.Data != null) aOut.AppendLine(iEvent.Data); };
                aProc.ErrorDataReceived += (iSender, iEvent) => { if (iEvent.Data != null) aErr.AppendLine(iEvent.Data); };
                try
                {
                    aProc.Start();
                }
                catch (Exception e)
                {
                    // git 不在 PATH 上是**環境**問題，不是「這個 repo 有問題」—— 訊息要分得出來。
                    return new SCP_GitResult(-1, "", "啟動 git 失敗（PATH 上有 git 嗎？）：" + e.Message);
                }
                aProc.BeginOutputReadLine();
                aProc.BeginErrorReadLine();
                // 登記＋自動反登記：逾時被 kill 的那條也會反登記（Dispose 走例外路徑一樣會跑），
                // 所以不會留下一筆指向已死 PID 的記錄。registry 沒被宿主 Configure 時這裡是 no-op。
                using (SCP_ProcessRegistry.RegisterScope(aProc,
                           s_ScopeTag ?? DefaultProcessTag,
                           "git " + Truncate(string.Join(" ", iArgs), 80),
                           s_ScopeOwner ?? "",
                           iAllowMultiple: true))
                {
                    if (!aProc.WaitForExit(iTimeoutMs))
                    {
                        try { aProc.Kill(); } catch { /* 已經死了就算了 */ }
                        return new SCP_GitResult(-1, aOut.ToString(),
                            "git 逾時（" + iTimeoutMs + " ms）：git " + string.Join(" ", iArgs));
                    }
                    aProc.WaitForExit();   // flush 非同步讀取的殘餘（少這行尾巴會被截掉）
                    return new SCP_GitResult(aProc.ExitCode, aOut.ToString(), aErr.ToString());
                }
            }
        }

        /// <summary>
        /// git 版本；問不到回 <c>null</c>。
        /// <para>⚠ **不要**回 0.0 —— 「問不到」與「很舊」是兩件事，壓成同一個值之後
        /// 「這台機器沒裝 git」會長得像「git 太舊」。</para>
        /// </summary>
        public static Version? Version()
        {
            SCP_GitResult aRes = RunTimeout(GetSafeCwd(), 15_000, "--version");
            if (!aRes.Ok) return null;
            // "git version 2.39.2.windows.1" → 只取前兩段（第三段以後的形狀各平台不同）
            string[] aParts = aRes.StdOut.Trim().Split(' ');
            if (aParts.Length < 3) return null;
            string[] aSeg = aParts[2].Split('.');
            int aMajor;
            int aMinor;
            if (aSeg.Length >= 2 && int.TryParse(aSeg[0], out aMajor) && int.TryParse(aSeg[1], out aMinor))
                return new Version(aMajor, aMinor);
            return null;
        }

        /// <summary>這個目錄在 git repo 裡嗎（目錄不存在回 false，不丟例外）。</summary>
        public static bool IsRepo(string iDir)
        {
            return System.IO.Directory.Exists(iDir)
                && RunTimeout(iDir, 15_000, "rev-parse", "--git-dir").Ok;
        }

        /// <summary>取第一行 —— 報告逐項一行時用。</summary>
        public static string FirstLine(string? iText)
        {
            if (string.IsNullOrEmpty(iText)) return "";
            int aIndex = iText!.IndexOf('\n');
            string aLine = aIndex < 0 ? iText : iText.Substring(0, aIndex);
            return aLine.TrimEnd('\r');
        }

        /// <summary>
        /// 從多行輸出裡挑出**講原因**的那一行（見 <see cref="SCP_GitResult.ReasonLine"/> 的血證）。
        /// <para>順序：<c>!</c> 開頭（push 的逐 ref 結果）→ 含 <c>rejected</c> / <c>error:</c> /
        /// <c>fatal:</c> / <c>hint:</c> 以外的線索 → 第一行不是 <c>To …</c> 的那行 → 退回第一行。</para>
        /// <para>⚠ 退回第一行是**最後手段**而不是預設：認不出格式時至少要說點什麼，
        /// 但不能讓「認不出」長得跟「這就是原因」一樣 —— 所以挑選順序寫死在這裡，
        /// 讓下一個人改的時候看得到判準，而不是在各個呼叫端各猜一次。</para>
        /// </summary>
        public static string ReasonLine(string? iText)
        {
            List<string> aLines = SplitLines(iText);
            if (aLines.Count == 0) return "";
            for (int i = 0; i < aLines.Count; ++i)
            {
                if (aLines[i].StartsWith("!", StringComparison.Ordinal)) return aLines[i];
            }
            for (int i = 0; i < aLines.Count; ++i)
            {
                string aLine = aLines[i];
                if (aLine.IndexOf("rejected", StringComparison.OrdinalIgnoreCase) >= 0
                    || aLine.StartsWith("error:", StringComparison.OrdinalIgnoreCase)
                    || aLine.StartsWith("fatal:", StringComparison.OrdinalIgnoreCase))
                    return aLine;
            }
            for (int i = 0; i < aLines.Count; ++i)
            {
                // `To <url>` 只講了「推去哪」，不是原因 —— 它是最沒有資訊的一行。
                if (!aLines[i].StartsWith("To ", StringComparison.Ordinal)) return aLines[i];
            }
            return aLines[0];
        }

        /// <summary>逐行切開並去掉空行（git 的 stdout 幾乎都是「一行一筆」）。</summary>
        public static List<string> SplitLines(string? iText)
        {
            var aList = new List<string>();
            if (string.IsNullOrEmpty(iText)) return aList;
            string[] aRaw = iText!.Split('\n');
            for (int i = 0; i < aRaw.Length; ++i)
            {
                string aLine = aRaw[i].Trim();
                if (aLine.Length > 0) aList.Add(aLine);
            }
            return aList;
        }

        /// <summary>
        /// 問版本這種「跟哪個 repo 無關」的指令也要有個工作目錄。
        /// <para>⚠ 不能假設 CurrentDirectory 一定存在（服務帳號／被刪掉的資料夾）——
        /// 拿不到就退回暫存目錄，為了問版本而丟例外不值得。</para>
        /// </summary>
        /// <summary>登記用的敘述要短 —— 一長串 pathspec 塞進 json 只是把檔案撐大，沒人讀得完。</summary>
        static string Truncate(string iText, int iMax)
        {
            if (string.IsNullOrEmpty(iText) || iText.Length <= iMax) return iText;
            return iText.Substring(0, iMax) + "…";
        }

        static string GetSafeCwd()
        {
            try
            {
                string aCwd = Environment.CurrentDirectory;
                if (System.IO.Directory.Exists(aCwd)) return aCwd;
            }
            catch { /* 拿不到就用暫存 */ }
            return System.IO.Path.GetTempPath();
        }
    }
}
