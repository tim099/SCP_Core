// 區塊職責：自動 commit 的**引擎** —— 找出要管的 repo、逐個掃描分群、一群一筆 commit。
//          Senate 的自動 Commit 頁（人按）與 `senate cmd auto-commit`（agent 跑）共用這一份。
// 物理意義：⭐ Tim 2026-09-30 兩條拍板定下形狀：
//            ① **不再分模式** —— AgentCommands 本層、`letters/*` 每個信件庫、帶 `.ucl_autocommit.json`
//               的 submodule **一次掃完、一張清單**（原本是 agent / letters / submodules 三種模式各掃各的）。
//            ② **不再判斷 persona 是否在線** —— 自動 commit 管理的部分**不應該手動 commit**
//               ⇒ 那些群的檔是機器獨佔的，沒有「她正在寫」這回事；親筆檔本來就不在任何一群裡。
//          🩸 下沉時順手收掉一個分岔：Unity 頁面的提交路徑**沒有** Cmd 那三道 BUG-30 守衛
//            （呼叫前 index 已有 staged 檔 → 擋／pathspec 提交／提交後對帳），失敗時還整個 `git reset`。
//            兩個入口同一件事、守衛不一樣，而兩邊各自看起來都正常。⇒ 這裡只有一條提交路徑，守衛以 Cmd 版為準。
// 數值影響：
//   · 掃描唯讀（rev-parse / diff --cached / status / config）。
//   · 提交只寫**該 repo 自己的 history**：具名 stage（⛔ 絕不 `git add -A`）→ `--pathspec-from-file` 提交
//     → 回讀 SHA → 提交後對帳。**不 push、不動父層 pointer**（那兩件是人的決定）。
//   · 走純 git commit，⛔ **不走 `senate cmd commit`**：那支的 trailer／酒館公告／領薪是給「有作者的工作產出」用的，
//     這裡收的是機器生成的狀態殘渣，掛誰的名字領誰的薪都是假帳。
//   · 提交順序：設定檔 repo → 信件庫 → AgentCommands 本層（**父層排最後**）——
//     子 repo 先 commit，父層那一輪掃到的 submodule pointer 才是新的（但 pointer 群仍然永不自動收）。
// ⚠ 四道硬擋（都是「不會當場叫」的那種錯，所以擋在必經路上）：
//   ① `__other` / `__other_untracked` / `__subptr` **永遠不自動收**，要收得顯式指定。
//   ② **detached HEAD 的 repo 直接擋下** —— 那裡 commit 出來沒有分支指到它，下次 checkout 只剩 reflog 找得到。
//   ③ **呼叫前 index 已有 staged 檔的 repo，提交直接擋下**（BUG-30）—— 分群只決定「我 stage 哪些檔」，
//      index 裡本來就有的東西會被併進第一個群、掛上那個群的訊息，而 commit 會成功。
//      🩸 2026-08-21：`git mv` 21 個檔後直接跑，那批改名落進 `[chat] … [3 files]` —— 訊息說 3 個檔、實際 24 個。
//   ④ 設定檔壞掉／不合法的 repo 擋下並說為什麼（「設定寫錯」與「沒設定」不同形）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。git 一律走 SCP_Git（唯一出口）。
// @doc-sync: <SCP_Core>/Docs~/AutoCommit.md（掃描範圍／守衛／提交順序）
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Paths;

namespace SCP.Core.Git
{
    /// <summary>這個 repo 的規則從哪裡來。**一個 repo 只有一個規則來源**。</summary>
    public enum SCP_AutoCommitSource
    {
        /// <summary>AgentCommands 本層（寫死的 AgentGroupDefs）。</summary>
        AgentCommands = 0,
        /// <summary>`letters/&lt;persona&gt;/` 信件庫（寫死的 PersonaGroupDefs）。</summary>
        PersonaLetters = 1,
        /// <summary>自帶 `.ucl_autocommit.json` 的 submodule（規則由該檔宣告）。</summary>
        Configured = 2,
    }

    /// <summary>一個 repo 的發現＋掃描結果。</summary>
    public sealed class SCP_AutoCommitRepo
    {
        /// <summary>git 工作目錄（絕對路徑，正斜線）。</summary>
        public string Root = "";
        /// <summary>顯示名（AgentCommands / persona 名 / 設定檔的 Name）。⚠ 同一輪內唯一 —— 頁面拿它當 id。</summary>
        public string Name = "";
        public SCP_AutoCommitSource Source;
        /// <summary>這個 repo 用的分群規則。</summary>
        public SCP_AutoCommitGroupDef[] Defs = Array.Empty<SCP_AutoCommitGroupDef>();
        /// <summary>設定檔路徑（Configured 才有）。</summary>
        public string ConfigPath = "";

        /// <summary>分支名；detached 或還沒掃 ＝ 空。</summary>
        public string Branch = "";
        /// <summary>非空 ＝ 擋下的理由（不能 commit）。</summary>
        public string Blocked = "";
        /// <summary>設定檔把自己標為停用（`Enabled=false`）。**不是錯誤**，所以不寫進 Blocked。</summary>
        public bool Disabled;
        /// <summary>掃過了嗎（停用／發現階段就擋下的不掃）。</summary>
        public bool Scanned;

        /// <summary>群 key → 檔案清單（正斜線相對路徑）。含三個特殊群。</summary>
        public Dictionary<string, List<string>> Groups = new Dictionary<string, List<string>>();
        /// <summary>掃描**之前**就已經 staged 的檔。非空 ⇒ 提交擋下（硬擋③）。</summary>
        public List<string> PreStaged = new List<string>();
        /// <summary>被 ephemeral 規則排除的檔數。</summary>
        public int Ephemeral;

        /// <summary>進候選的檔數（不含 ephemeral）。</summary>
        public int CandidateCount
        {
            get
            {
                int aCount = 0;
                foreach (var aList in Groups.Values) aCount += aList.Count;
                return aCount;
            }
        }

        /// <summary>現在能不能提交（沒擋下、沒停用、index 本來是空的）。</summary>
        public bool CanCommit => Blocked.Length == 0 && !Disabled && Scanned && PreStaged.Count == 0;

        /// <summary>規則來源的人話。</summary>
        public string SourceLabel
        {
            get
            {
                switch (Source)
                {
                    case SCP_AutoCommitSource.AgentCommands: return "AgentCommands 本層";
                    case SCP_AutoCommitSource.PersonaLetters: return "persona 信件庫";
                    default: return "設定檔 " + SCP_AutoCommitConfig.FileName;
                }
            }
        }

        /// <summary>這個 repo 預設要收的群（DefaultOn 的具名群；特殊群永遠不在其中）。</summary>
        public HashSet<string> DefaultOnGroups()
        {
            var aSet = new HashSet<string>();
            foreach (var aDef in Defs) if (aDef.DefaultOn) aSet.Add(aDef.Key);
            return aSet;
        }

        /// <summary>某群的顯示名。</summary>
        public string LabelOf(string iKey)
        {
            foreach (var aDef in Defs) if (aDef.Key == iKey) return aDef.Label;
            return SCP_AutoCommitRules.LabelOfSpecial(iKey);
        }

        /// <summary>某群的 commit 訊息（帶檔數）。</summary>
        public string MessageOf(string iKey)
        {
            int aCount = Groups.TryGetValue(iKey, out var aList) ? aList.Count : 0;
            return SCP_AutoCommitRules.MessageOf(iKey, Defs, aCount);
        }

        /// <summary>顯示順序：具名群照規則表順序，再來三個特殊群。只回有檔的。</summary>
        public List<string> OrderedGroupKeys()
        {
            var aKeys = new List<string>();
            foreach (var aDef in Defs)
                if (Groups.TryGetValue(aDef.Key, out var aList) && aList.Count > 0) aKeys.Add(aDef.Key);
            foreach (string aKey in SCP_AutoCommitRules.NeverAutoKeys)
                if (Groups.TryGetValue(aKey, out var aList) && aList.Count > 0) aKeys.Add(aKey);
            return aKeys;
        }
    }

    /// <summary>一群提交的結果。</summary>
    public sealed class SCP_AutoCommitGroupResult
    {
        public string RepoName = "";
        public string RepoRoot = "";
        public string Key = "";
        public string Message = "";
        public int FileCount;
        /// <summary>commit 成功（SHA 讀得回來）。</summary>
        public bool Ok;
        /// <summary>沒做但**不是失敗**（掃描後檔案又被還原 ⇒ 沒有東西可提交）。</summary>
        public bool Skipped;
        public string Sha = "";
        /// <summary>失敗／跳過的理由（一行）。</summary>
        public string Error = "";
        /// <summary>提交後對帳：這一筆多帶了哪些**不在分群清單裡**的檔（非空 ＝ 要人看）。</summary>
        public List<string> Extra = new List<string>();
        /// <summary>逐行經過（給報告的完整版）。</summary>
        public List<string> Log = new List<string>();

        /// <summary>stderr 認得出 `index.lock` ⇒ 別人正握著 index，等一下重跑就好（不是 repo 壞了）。</summary>
        public bool IsLocked => Error.IndexOf("index.lock", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>自動 commit 引擎（純靜態、無狀態 ⇒ 多執行緒可同時呼叫）。</summary>
    public static class SCP_AutoCommit
    {
        /// <summary>登記 git process 用的 tag（呼叫端開 <c>SCP_Git.Scope(ProcessTag, …)</c>）。</summary>
        public const string ProcessTag = "auto_commit_git";

        /// <summary>每批 `git add` 的路徑數 —— Windows 命令列 32k 上限。</summary>
        const int Chunk = 40;

        /// <summary>列路徑的節流：最多 20 筆，其餘只報數。洗版會讓人不讀，而不讀＝這些字等於沒寫。</summary>
        public const int PreviewLimit = 20;

        // ===========================================================
        // 發現
        // ===========================================================
        // 區塊職責：決定要管哪些 repo（**一個 repo 只有一個規則來源**）。
        // 物理意義：三個來源各有自己的判準，⛔ 不猜：
        //   · AgentCommands 本層 —— 資料根自己。
        //   · 信件庫 —— letters 根底下**每個**有 `.git` 的目錄（submodule 的 .git 是檔、獨立 clone 是目錄，兩種都收）。
        //     ⚠ 判準刻意不是 `.gitmodules`：有些信件庫沒登記在父層的 .gitmodules 裡，而它們一樣有機器檔要收。
        //   · 設定檔 repo —— 資料根 `.gitmodules` 裡的 submodule，**有設定檔才收**（設定檔是加入的唯一憑據）。
        //     信件庫即使也在 .gitmodules 裡，規則仍是信件庫那張表（不重複收、不讓設定檔覆寫）。
        // 數值影響：只讀目錄與設定檔，不跑 git。回傳順序＝提交順序（父層最後）。
        // ⚠ 信件庫裡出現 `.ucl_autocommit.json` ⇒ **擋下並說出來**：兩份規則同時宣稱管同一個 repo，
        //   挑哪一份都是猜，而猜錯的症狀是「親筆檔被自動收走」。
        public static List<SCP_AutoCommitRepo> Discover(SCP_DataRoot iDataRoot, SCP_LettersRoot? iLettersRoot,
                                                        List<string> oNotes)
        {
            var aConfigured = new List<SCP_AutoCommitRepo>();
            var aLetters = new List<SCP_AutoCommitRepo>();
            string aData = Norm(iDataRoot.Value);
            string aLettersRoot = iLettersRoot.HasValue ? Norm(iLettersRoot.Value.Value) : "";

            // ── 信件庫 ──
            if (aLettersRoot.Length > 0)
            {
                if (!Directory.Exists(aLettersRoot))
                    oNotes.Add($"⚠ letters 根不存在：{aLettersRoot} —— 本輪沒有掃任何信件庫（⛔ 不等於「信件庫全乾淨」）");
                else
                {
                    foreach (string aDir in Directory.GetDirectories(aLettersRoot))
                    {
                        string aGit = Path.Combine(aDir, ".git");
                        if (!File.Exists(aGit) && !Directory.Exists(aGit)) continue;
                        string aRoot = Norm(aDir);
                        var aRepo = new SCP_AutoCommitRepo
                        {
                            Root = aRoot,
                            Name = Path.GetFileName(aDir),
                            Source = SCP_AutoCommitSource.PersonaLetters,
                            Defs = SCP_AutoCommitRules.PersonaGroupDefs,
                        };
                        if (SCP_AutoCommitConfig.Exists(aRoot))
                        {
                            aRepo.ConfigPath = SCP_AutoCommitConfig.PathOf(aRoot);
                            aRepo.Blocked = $"信件庫裡同時有 {SCP_AutoCommitConfig.FileName} —— 兩份規則宣稱管同一個 repo，"
                                            + "⛔ 不猜用哪一份（信件庫的分群寫死在 SCP_AutoCommitRules.PersonaGroupDefs）";
                        }
                        aLetters.Add(aRepo);
                    }
                    aLetters.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
                }
            }
            else
            {
                oNotes.Add("⚠ 沒有 letters 根 —— 本輪沒有掃任何信件庫（⛔ 不等於「信件庫全乾淨」）");
            }

            // ── 設定檔 repo（資料根的 submodule）──
            int aNoConfig = 0;
            foreach (string aDir in SCP_AutoCommitConfig.ListSubmodulePaths(aData))
            {
                string aRoot = Norm(aDir);
                if (aLettersRoot.Length > 0 && IsUnder(aRoot, aLettersRoot)) continue;   // 信件庫那邊已經收了
                var aLoad = SCP_AutoCommitConfig.Load(aRoot);
                if (aLoad.State == SCP_AutoCommitConfigState.Missing) { aNoConfig++; continue; }
                string aDirName = Path.GetFileName(aRoot);
                var aRepo = new SCP_AutoCommitRepo
                {
                    Root = aRoot,
                    Name = aDirName,
                    Source = SCP_AutoCommitSource.Configured,
                    ConfigPath = aLoad.Path,
                };
                if (aLoad.State == SCP_AutoCommitConfigState.Error || aLoad.Config == null)
                {
                    aRepo.Blocked = "設定檔讀取失敗：" + aLoad.Error;
                }
                else
                {
                    var aConfig = aLoad.Config;
                    if (aConfig.Name.Length > 0) aRepo.Name = aConfig.Name;
                    aRepo.Defs = aConfig.ToGroupDefs();
                    var aErrors = aConfig.Validate();
                    if (aErrors.Count > 0) aRepo.Blocked = "設定不合法：" + string.Join("；", aErrors);
                    else if (!aConfig.Enabled) aRepo.Disabled = true;
                }
                aConfigured.Add(aRepo);
            }
            aConfigured.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            if (aNoConfig > 0)
                oNotes.Add($"・另有 {aNoConfig} 顆 submodule 沒有 {SCP_AutoCommitConfig.FileName}（不收 —— 設定檔是加入的唯一憑據）");

            // ── 組起來：子 repo 在前、父層最後 ──
            var aAll = new List<SCP_AutoCommitRepo>();
            aAll.AddRange(aConfigured);
            aAll.AddRange(aLetters);
            if (Directory.Exists(aData))
            {
                aAll.Add(new SCP_AutoCommitRepo
                {
                    Root = aData,
                    Name = "AgentCommands",
                    Source = SCP_AutoCommitSource.AgentCommands,
                    Defs = SCP_AutoCommitRules.AgentGroupDefs,
                });
            }
            else
            {
                oNotes.Add($"✗ 資料根不存在：{aData}");
            }

            // 顯示名撞名（設定檔的 Name 可以隨便寫）⇒ 補目錄名區分。頁面拿 Name 當 id，撞名會共用勾選。
            var aSeen = new HashSet<string>();
            foreach (var aRepo in aAll)
            {
                if (aSeen.Add(aRepo.Name)) continue;
                aRepo.Name = aRepo.Name + "(" + Path.GetFileName(aRepo.Root) + ")";
                aSeen.Add(aRepo.Name);
            }
            return aAll;
        }

        // ===========================================================
        // 掃描
        // ===========================================================
        /// <summary>
        /// 掃一個 repo：分支 → 呼叫前的 index 快照 → status → 分群。
        /// <para>發現階段就擋下或停用的不掃（它們此刻不能 commit，掃了也只是多跑 git）。</para>
        /// <para>⚠ status 用 <c>-z</c>：NUL 分隔、**不加引號也不轉義** ⇒ 中文／空白／引號路徑都原樣拿到。
        /// 舊版剝外層引號的做法對「路徑裡有引號或反斜線」會安靜地拿到錯的字串。</para>
        /// </summary>
        public static void Scan(SCP_AutoCommitRepo iRepo)
        {
            iRepo.Groups.Clear();
            iRepo.PreStaged.Clear();
            iRepo.Ephemeral = 0;
            iRepo.Scanned = false;
            if (iRepo.Blocked.Length > 0 || iRepo.Disabled) return;

            var aBranch = SCP_Git.Run(iRepo.Root, "rev-parse", "--abbrev-ref", "HEAD");
            if (!aBranch.Ok) { iRepo.Blocked = "git rev-parse 失敗（" + aBranch.FirstLine + "）"; return; }
            string aName = aBranch.StdOut.Trim();
            if (aName == SCP_Git.DetachedHead)
            {
                iRepo.Blocked = "detached HEAD —— commit 會落在游離節點，先 switch 回追蹤分支";
                return;
            }
            iRepo.Branch = aName;

            // 呼叫前的 index 快照 —— 在 stage 任何東西**之前**問，之後就分不出「誰放的」了。
            var aStaged = SCP_Git.Run(iRepo.Root, "diff", "--cached", "--name-only", "-z");
            if (!aStaged.Ok) { iRepo.Blocked = "git diff --cached 失敗（" + aStaged.FirstLine + "）"; return; }
            foreach (string aPath in SplitNul(aStaged.StdOut)) iRepo.PreStaged.Add(aPath);

            var aStatus = SCP_Git.Run(iRepo.Root, "status", "--porcelain=v1", "--untracked-files=all", "-z");
            if (!aStatus.Ok) { iRepo.Blocked = "git status 失敗（" + aStatus.FirstLine + "）"; return; }

            HashSet<string> aSubPaths = SubmodulePaths(iRepo.Root);
            List<string> aEntries = SplitNul(aStatus.StdOut);
            for (int i = 0; i < aEntries.Count; ++i)
            {
                string aEntry = aEntries[i];
                if (aEntry.Length < 4) continue;
                string aXy = aEntry.Substring(0, 2);
                string aPath = aEntry.Substring(3);
                // -z 的 rename／copy：`XY 新名\0舊名\0` ⇒ 下一格是舊名，吃掉它（stage 新名時 git 會一併記錄）。
                if (aXy[0] == 'R' || aXy[0] == 'C') i++;
                if (aPath.Length == 0) continue;

                // ⚠ `--untracked-files=all` 會把一般目錄展開成逐檔 ⇒ 還以 `dir/` 結尾出現的只剩**巢狀 repo**
                //   （還沒登記進 .gitmodules 的那種）。它跟 submodule pointer 同一族：`git add` 會把它當成
                //   沒有 .gitmodules 的 gitlink 塞進去，而那不會報錯。
                // 🩸 TASK-0340 沙盒實測：新的信件庫 `ChatTavern/baton/letters/alice/` 被 `ChatTavern/` 前綴吃進
                //   **runtime 群（預設勾選）**—— UCL 版的規則表同形，一樣會中。⇒ 一律歸 __subptr（永不自動收）。
                bool aNestedRepo = aPath.EndsWith("/", StringComparison.Ordinal);
                string? aKey = SCP_AutoCommitRules.Classify(aPath, iRepo.Defs, aNestedRepo || aSubPaths.Contains(aPath));
                if (aKey == null) { iRepo.Ephemeral++; continue; }
                // 未分類**且 untracked**（`??`）拆成獨立一群 —— 那是別人做過的決定，替他翻案要顯式。
                // ⚠ 只在 key 已經是 __other 時改判，⛔ 不碰任何具名群（那些檔本來就該被它們的規則收）。
                if (aKey == SCP_AutoCommitRules.KeyOther && aXy == "??")
                    aKey = SCP_AutoCommitRules.KeyOtherUntracked;
                if (!iRepo.Groups.TryGetValue(aKey, out var aList))
                    iRepo.Groups[aKey] = aList = new List<string>();
                aList.Add(aPath);
            }
            iRepo.Scanned = true;
        }

        /// <summary>發現＋逐個掃描（同步；UI 端請放背景執行緒）。</summary>
        public static List<SCP_AutoCommitRepo> DiscoverAndScan(SCP_DataRoot iDataRoot, SCP_LettersRoot? iLettersRoot,
                                                               List<string> oNotes, Action<string>? iProgress = null)
        {
            var aRepos = Discover(iDataRoot, iLettersRoot, oNotes);
            foreach (var aRepo in aRepos)
            {
                iProgress?.Invoke("掃描 " + aRepo.Name);
                Scan(aRepo);
            }
            return aRepos;
        }

        /// <summary>該 repo 的 submodule 路徑集合（pointer 變更要跟一般檔案分開治理）。</summary>
        static HashSet<string> SubmodulePaths(string iRoot)
        {
            var aSet = new HashSet<string>();
            if (!File.Exists(Path.Combine(iRoot, ".gitmodules"))) return aSet;
            var aResult = SCP_Git.Run(iRoot, "config", "--file", ".gitmodules", "--get-regexp", @"^submodule\..*\.path$");
            if (!aResult.Ok) return aSet;
            foreach (string aLine in aResult.OutLines())
            {
                int aSp = aLine.IndexOf(' ');
                if (aSp > 0 && aSp + 1 < aLine.Length) aSet.Add(aLine.Substring(aSp + 1).Trim());
            }
            return aSet;
        }

        // ===========================================================
        // 提交
        // ===========================================================
        /// <summary>
        /// 一群一筆 commit：具名 stage（分批）→ **pathspec 提交** → 回讀 SHA → 提交後對帳。
        /// <para>⚠ 前提：<see cref="SCP_AutoCommitRepo.CanCommit"/>。不成立就回失敗並說為什麼（⛔ 不猜著做）。</para>
        /// <para>⚠ 失敗時**只 unstage 這一群的路徑**（⛔ 不整個 `git reset`、⛔ 絕不 `--hard`）——
        /// 走到這裡的前提是 index 本來是空的，所以 unstage 這批不可能動到別人放進去的東西；
        /// 而不還原的話，殘留會讓下一次被硬擋③鎖在門外（2026-08-31 summit：80 個 staged 殘留）。</para>
        /// </summary>
        public static SCP_AutoCommitGroupResult CommitGroup(SCP_AutoCommitRepo iRepo, string iKey)
        {
            var aOut = new SCP_AutoCommitGroupResult
            {
                RepoName = iRepo.Name,
                RepoRoot = iRepo.Root,
                Key = iKey,
                Message = iRepo.MessageOf(iKey),
            };
            if (!iRepo.CanCommit)
            {
                aOut.Error = iRepo.Blocked.Length > 0 ? iRepo.Blocked
                           : iRepo.Disabled ? "設定為停用（Enabled=false）"
                           : !iRepo.Scanned ? "還沒掃描"
                           : $"呼叫前 index 已有 {iRepo.PreStaged.Count} 個 staged 檔（BUG-30）—— 先自己 commit 或 unstage";
                return aOut;
            }
            if (!iRepo.Groups.TryGetValue(iKey, out var aFiles) || aFiles.Count == 0)
            {
                aOut.Skipped = true;
                aOut.Error = "這一群沒有候選檔";
                return aOut;
            }
            aOut.FileCount = aFiles.Count;

            Version? aVer = SCP_Git.Version();
            if (aVer == null || aVer < SCP_Git.MinVersionForPathspecFromFile)
            {
                aOut.Error = $"git 版本 {(aVer == null ? "問不到" : aVer.ToString())} —— `--pathspec-from-file` 需要 ≥ "
                             + SCP_Git.MinVersionForPathspecFromFile + "（⛔ 不退回無 pathspec 的提交：那正是 BUG-30 的入口）";
                return aOut;
            }

            // ① 具名 stage（分批）
            for (int i = 0; i < aFiles.Count; i += Chunk)
            {
                var aArgs = new List<string> { "add", "--" };
                for (int j = i; j < aFiles.Count && j < i + Chunk; ++j) aArgs.Add(aFiles[j]);
                var aAdd = SCP_Git.Run(iRepo.Root, aArgs.ToArray());
                if (!aAdd.Ok)
                {
                    aOut.Error = "git add：" + Why(aAdd);
                    aOut.Log.Add($"✗ {iRepo.Name} [{iKey}] git add 失敗 —— {aAdd.Message}");
                    Rollback(iRepo, aFiles, aOut);
                    return aOut;
                }
            }

            // ② 掃描之後檔案又被還原 ⇒ index 仍是空的 ⇒ 沒有東西可提交（不是失敗）。
            var aNow = SCP_Git.Run(iRepo.Root, "diff", "--cached", "--name-only", "-z");
            if (aNow.Ok && SplitNul(aNow.StdOut).Count == 0)
            {
                aOut.Skipped = true;
                aOut.Error = "掃描後已無變更 —— 跳過";
                aOut.Log.Add($"⏭ {iRepo.Name} [{iKey}] 掃描後已無變更 —— 跳過");
                return aOut;
            }

            // ③ pathspec 提交：只提交這一群的路徑 ⇒ index 裡的其他東西**在物理上不可能**被順手帶走。
            //    訊息與路徑清單都走檔案（⛔ 不賭 argv 引號與 32k 上限：砍下來的形狀是「這筆少了幾個檔」，不是報錯）。
            //    路徑清單用 NUL 分隔（`--pathspec-file-nul`）⇒ 路徑裡有什麼字元都不必跳脫。
            string aTmpDir = Path.GetTempPath();
            string aMsgFile = Path.Combine(aTmpDir, $"scp_autocommit_msg_{Guid.NewGuid():N}.txt");
            string aSpecFile = Path.Combine(aTmpDir, $"scp_autocommit_spec_{Guid.NewGuid():N}.txt");
            try
            {
                File.WriteAllText(aMsgFile, aOut.Message + "\n", new UTF8Encoding(false));
                File.WriteAllText(aSpecFile, string.Join("\0", aFiles.ToArray()), new UTF8Encoding(false));
                var aCommit = SCP_Git.Run(iRepo.Root, "commit", "-F", aMsgFile,
                                          "--pathspec-from-file=" + aSpecFile, "--pathspec-file-nul");
                if (!aCommit.Ok)
                {
                    aOut.Error = "git commit：" + Why(aCommit);
                    aOut.Log.Add($"✗ {iRepo.Name} [{iKey}] git commit 失敗 —— {aCommit.Message}");
                    Rollback(iRepo, aFiles, aOut);
                    return aOut;
                }
            }
            finally
            {
                try { if (File.Exists(aMsgFile)) File.Delete(aMsgFile); } catch { /* 暫存檔清不掉不影響結果 */ }
                try { if (File.Exists(aSpecFile)) File.Delete(aSpecFile); } catch { /* 同上 */ }
            }

            // ④ 印 ✓ 不算數，讀回來才算：SHA 從 git 自己撈。
            var aHead = SCP_Git.Run(iRepo.Root, "rev-parse", "--short", "HEAD");
            aOut.Sha = aHead.Ok ? aHead.StdOut.Trim() : "?";
            aOut.Ok = true;
            aOut.Log.Add($"✓ {iRepo.Name} [{aOut.Sha}] {aOut.Message}");

            // ⑤ 提交後對帳：「我挑了哪些檔」與「這一筆實際提交了哪些檔」是兩件事。
            //    ⚠ 只報「多出來的」：少了通常是合法的（挑到的檔內容其實沒變），而誤報會讓人不信這條對帳。
            var aShow = SCP_Git.Run(iRepo.Root, "show", "--pretty=format:", "--name-only", "-z", "HEAD");
            if (!aShow.Ok)
            {
                aOut.Log.Add($"⚠ {iRepo.Name} [{aOut.Sha}] 提交後對帳失敗（git show：{aShow.FirstLine}）—— 這筆沒有對帳讀數");
            }
            else
            {
                var aPicked = new HashSet<string>(aFiles);
                foreach (string aPath in SplitNul(aShow.StdOut))
                    if (!aPicked.Contains(aPath)) aOut.Extra.Add(aPath);
                if (aOut.Extra.Count > 0)
                {
                    aOut.Log.Add($"✗ {iRepo.Name} [{aOut.Sha}] **對帳不符**：這筆多帶了 {aOut.Extra.Count} 個不在分群清單裡的檔"
                                 + " —— 分群訊息與實際內容已經脫鉤，要人看（BUG-30 同族）");
                    foreach (string aLine in Preview(aOut.Extra)) aOut.Log.Add("    " + aLine);
                }
            }
            return aOut;
        }

        /// <summary>失敗時把這一群從 index 還原（只 unstage，⛔ 不碰工作區）。還原本身失敗也要說出來。</summary>
        static void Rollback(SCP_AutoCommitRepo iRepo, List<string> iFiles, SCP_AutoCommitGroupResult oResult)
        {
            for (int i = 0; i < iFiles.Count; i += Chunk)
            {
                var aArgs = new List<string> { "reset", "--quiet", "--" };
                for (int j = i; j < iFiles.Count && j < i + Chunk; ++j) aArgs.Add(iFiles[j]);
                var aReset = SCP_Git.Run(iRepo.Root, aArgs.ToArray());
                if (aReset.Ok) continue;
                oResult.Log.Add($"⚠ {iRepo.Name}：失敗後還原 index 也失敗 —— {Why(aReset)}"
                                + "　⇒ **index 裡還留著這一群的殘留**，下一次會被「呼叫前已有 staged 檔」擋下；"
                                + "請手動 `git -C <repo> reset` 之後再跑（工作區沒有被動過）");
                return;
            }
            oResult.Log.Add($"↩ {iRepo.Name}：已把這一群從 index 還原（工作區未動）—— 可直接重試");
        }

        // ===========================================================
        // 小工具
        // ===========================================================
        /// <summary>長清單截斷（最多 <see cref="PreviewLimit"/> 筆，其餘只報數）。</summary>
        public static List<string> Preview(List<string> iPaths)
        {
            var aList = new List<string>();
            for (int i = 0; i < iPaths.Count && i < PreviewLimit; ++i) aList.Add(iPaths[i]);
            if (iPaths.Count > PreviewLimit) aList.Add($"…另有 {iPaths.Count - PreviewLimit} 筆未列");
            return aList;
        }

        /// <summary>
        /// 失敗理由的**那一行**：跳過 git 的 `warning:`／`hint:` 前導行，取第一條真正在講原因的。
        /// <para>🩸 TASK-0340 沙盒實測：pre-commit hook 拒絕時，stderr 第一行是
        /// `warning: … LF will be replaced by CRLF` —— 取首行的話報告會是一句**看起來有講原因、其實沒講**的話，
        /// 而那比沒有訊息糟（它讓人不再往下查）。全部都是 warning 才退回首行。</para>
        /// </summary>
        static string Why(SCP_GitResult iResult)
        {
            foreach (string aLine in SCP_Git.SplitLines(iResult.Message))
            {
                string aTrim = aLine.Trim();
                if (aTrim.Length == 0) continue;
                if (aTrim.StartsWith("warning:", StringComparison.Ordinal) || aTrim.StartsWith("hint:", StringComparison.Ordinal)) continue;
                return aTrim;
            }
            return iResult.FirstLine;
        }

        static List<string> SplitNul(string iText)
        {
            var aList = new List<string>();
            if (string.IsNullOrEmpty(iText)) return aList;
            foreach (string aPart in iText.Split('\0'))
            {
                // ⚠ SCP_Git 逐行收 stdout 時補的是換行 ⇒ NUL 分隔的輸出尾端會黏一個換行，要剝掉。
                string aPath = aPart.Trim('\r', '\n');
                if (aPath.Length > 0) aList.Add(aPath);
            }
            return aList;
        }

        static string Norm(string iPath) => (iPath ?? "").Replace('\\', '/').TrimEnd('/');

        static bool IsUnder(string iPath, string iRoot)
            => iPath.Equals(iRoot, StringComparison.OrdinalIgnoreCase)
               || iPath.StartsWith(iRoot + "/", StringComparison.OrdinalIgnoreCase);
    }
}
