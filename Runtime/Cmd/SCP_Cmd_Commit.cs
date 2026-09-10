// 區塊職責：`senate cmd commit` —— commit 的最後一步：組 trailer、提交、公告領薪、推進單號。
//           **git 與 trailer 本地跑（Editor 沒開也成）；公告與推單委派 Editor。**
// 物理意義：trailer 以前是手打的，於是它會漂 —— 同一位同事出現過 (GPT)/(GPT-5)/(GPT-5.6) 與兩種
//           domain。身分／型號／信箱三欄全部推導自檔案，手不碰就不會漂。
//           而**公告不是附帶動作，是領薪** —— 漏發就是錢沒領到（血證：新制上線後
//           `source_kind=commit` 曾 82 天零領取），所以它沒有關閉開關。
// 數值影響：唯一會改變外界狀態的三件事 —— ① `git commit`（本地）② 酒館一則貼文（**不可回復**）
//           ③ 任務單狀態。三者**分開結算**，見下面的出口碼。
//
// ⛔ 不做的事（少做是選擇）：**stage 什麼、切哪個分支、要不要 push，一格都不碰。**
//   那些維持原本手動流程 —— 一支會順手 stage 的提交工具，遲早會收走別人正在寫的檔。
//
// ⚠ 與 `git_commit.py` 的三個**刻意不同**（不是漏搬）：
//   ① 多位參與者走 `personas=a,b,c`（逗號分隔），不是可重複的 `--persona`
//      —— `SCP_CmdArgs.Get` 是單值介面，硬做多值要動共用層，那不在本單射程。
//   ② **公告失敗拆成兩個出口**：`6`＝確定沒發（補發安全）／`7`＝不知道（⛔ 先回讀）。
//      python 那支把兩者併成一個 `6` 並無條件印「手動補一則」——
//      🩸 而它自己的註解就寫著「送出之後的任何失敗都可能其實已經貼上了…同一個 SHA 貼兩次 = 付兩次錢」。
//      那條紀律**寫在程式碼裡，卻沒有寫進出口**。走 gateway 之後三態是型別給的，想併都併不了。
//   ③ 沒有 `--strict-email-source`：那個旗標擋的是「信箱取自快照而非現場值」，
//      而**本層直接讀 `profile/` 的檔案，沒有快照那一段** ⇒ 那個條件在這條路上永遠不成立。
//      ⛔ 收一個永遠不會生效的旗標，比不收它更糟（它看起來像一道防線）。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Git;
using SCP.Core.Letters;
using SCP.Core.Tasks;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Commit : SCP_Cmd
    {
        public override string Name => "commit";

        public override string Summary =>
            "提交（只做最後一步，**不 stage 不 push**）：組 Co-Authored-By ＋ git commit ——"
            + " 本地跑，Editor 沒開也成；**酒館公告領薪與單號推進委派 Editor**"
            + "（沒開＝exit 6 確定沒發／等不到回執＝**exit 7 不知道，先回讀別補發**）";

        public override string Details =>
            "⚠ **三本帳分開結算**（⛔ 不是「成功／失敗」兩種）：\n"
            + "  · `exit 0` ＝ commit ＋ 公告都成\n"
            + "  · `exit 4` ＝ **沒有 staged 變更** —— 本 Cmd 只提交，stage 請自己來\n"
            + "  · `exit 5` ＝ commit 就失敗了 ⇒ **什麼都沒落地**\n"
            + "  · **`exit 6` ＝ commit 落地了、公告「確定沒發」** ⇒ 錢沒領到，補發是安全的\n"
            + "  · **`exit 7` ＝ commit 落地了、公告「不知道」**（沒等到回執）⇒ ⛔ **先回讀再決定**：\n"
            + "     同一個 SHA 貼兩次 ＝ **付兩次錢**，而「逾時」與「真的沒發」在 CLI 這端同形。\n"
            + "\n"
            + "🛡 `expect_files=N` —— 宣告這一筆該收幾個檔，不符就擋下（**在 commit 之前**返回）。\n"
            + "   它把「我以為我在提交幾個檔」變成一個必須先算過的數字。不帶＝不檢查。\n"
            + "\n"
            + "📋 訊息裡的 `Fixes TASK-<n>` / `Refs TASK-<n>` 會推進那幾張單（頂格錨定；\n"
            + "   同一張單同時寫兩者時 **Fixes 優先**）。⚠ 推單掛在**公告成功之後**，\n"
            + "   而且失敗只警告 —— commit 與領薪是主線，推進是附帶效果。";

        public override string Example =>
            "senate cmd commit --arg repo=<repo> --arg personas=gura --arg expect_files=2 --arg-file message=<訊息檔>";

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("repo", "git 工作目錄（submodule 就指到該 submodule）", iRequired: true),
            new SCP_CmdArgSpec("personas", "參與者 persona，**逗號分隔**；每位一行 trailer（順序即 trailer 順序，"
                               + "第一位是公告的署名人）", iRequired: true),
            new SCP_CmdArgSpec("message", "commit 訊息 —— 長內文走 `--arg-file message=<檔>`", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根 —— 信箱預設表、公告與推單都要用", iRequired: true),
            new SCP_CmdArgSpec("region", "現地的區域（貨幣）ID —— **不給的話 trailer 的 agent 欄會缺席**"),
            new SCP_CmdArgSpec("expect_files", "宣告這一筆應該收幾個檔；與實際 staged 數不符就擋下不提交"),
            new SCP_CmdArgSpec("allow_unset", "=1 ⇒ 信箱未設定仍提交（預設拒絕 —— 假位址進了 history 改不掉）"),
            new SCP_CmdArgSpec("dry_run", "=1 ⇒ 只印組出來的訊息，**不提交、不公告、不推單**"),
            new SCP_CmdArgSpec("announce_body", "公告的開場白（插在標題與 commit 內文之間，寫給現在在酒館的同事）"),
            new SCP_CmdArgSpec("bump_of", "本筆是某主 commit 的 pointer bump：公告壓成一行並指向該 SHA（帳照領）"),
        };

        const string TrailerPrefix = "Co-Authored-By:";

        /// <summary>沒有 staged 變更 —— 本 Cmd 只提交，不 stage。</summary>
        public const int ExitNothingStaged = 4;

        /// <summary>`git commit` 就失敗了 ⇒ 什麼都沒落地。</summary>
        public const int ExitCommitFailed = 5;

        /// <summary>commit 落地了，而公告**確定沒發**（錢沒領到；補發是安全的）。</summary>
        public const int ExitAnnounceNotSent = 6;

        /// <summary>
        /// commit 落地了，而公告那半**不知道成沒成**（沒等到回執）。
        /// <para>⛔ 與 6 分開的理由：同一個 SHA 貼兩次 ＝ 付兩次錢。兩者處置相反。</para>
        /// </summary>
        public const int ExitAnnounceUnresolved = 7;

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aRepo = iArgs.Get("repo");
            string aLettersRoot = iArgs.Get("letters_root");
            string aDataRoot = iArgs.Get("data_root");
            string aRegion = iArgs.Get("region");
            string aBody = iArgs.Get("message");
            bool aDryRun = IsOn(iArgs.Get("dry_run"));
            bool aAllowUnset = IsOn(iArgs.Get("allow_unset"));

            List<string> aPersonas = SplitPersonas(iArgs.Get("personas"));
            if (aPersonas.Count == 0)
                return SCP_CmdResult.Fail(2, "⛔ `personas` 是空的 —— 至少要一位（逗號分隔可多位）");
            if (aBody.Trim().Length == 0)
                return SCP_CmdResult.Fail(2, "⛔ commit 訊息是空的（長內文走 `--arg-file message=<檔>`）");
            if (!SCP_Git.IsRepo(aRepo))
                return SCP_CmdResult.Fail(2, "⛔ 不是 git 工作目錄：" + aRepo);

            SCP_CmdResult aResult = SCP_CmdResult.Success();
            if (aRegion.Length == 0)
                aResult.Lines.Add("⚠ 沒給 `region` ⇒ trailer 的 **agent 欄會缺席**（`?@<persona>`）。"
                                  + "那是**沒人告訴我區域**，不是這個人沒有帳號。");

            // ── ① trailer（本地跑，不需要 Editor）──────────────────────────
            List<string> aTrailers = new List<string>();
            List<string> aProblems = new List<string>();
            BuildTrailers(aPersonas, aLettersRoot, aRegion, aDataRoot, aAllowUnset,
                          aTrailers, aProblems, aResult);
            if (aProblems.Count > 0)
            {
                SCP_CmdResult aFail = SCP_CmdResult.Fail(3);
                foreach (string aOne in aProblems) aFail.Lines.Add("⛔ " + aOne);
                aFail.Lines.Add("   ⇒ 假位址進了 git history 就改不掉；要硬幹請顯式帶 `--arg allow_unset=1`。");
                return aFail;
            }

            string aMessage = ComposeMessage(aBody, aTrailers);

            if (aDryRun)
            {
                aResult.Lines.Add("─── 將提交的訊息（`dry_run=1`，**一個位元組都沒寫**）───");
                aResult.Lines.Add(aMessage);
                aResult.Lines.Add("─── 未提交、未公告、未推單 ───");
                return aResult.AddValue("dry_run", "1");
            }

            // ── ② staged 檢查 ＋ expect_files（都在 commit **之前**）──────────
            SCP_GitResult aStaged = SCP_Git.Run(aRepo, "diff", "--cached", "--name-only");
            if (!aStaged.Ok)
                return SCP_CmdResult.Fail(ExitCommitFailed, "⛔ 讀 staged 清單失敗：" + aStaged.ReasonLine);
            List<string> aFiles = new List<string>();
            foreach (string aLine in aStaged.OutLines())
                if (aLine.Trim().Length > 0) aFiles.Add(aLine.Trim());
            if (aFiles.Count == 0)
                return SCP_CmdResult.Fail(ExitNothingStaged,
                    "⛔ " + aRepo + " **沒有 staged 變更** —— 本 Cmd 只做提交，stage 請自己來。");

            SCP_CmdResult? aExpect = CheckExpectFiles(iArgs.Get("expect_files"), aFiles);
            if (aExpect != null) return aExpect;

            // ── ③ git commit（訊息走暫存檔：SCP_Git 沒有 stdin，而長訊息也不該經過 shell）──
            SCP_CmdResult? aCommitFail = DoCommit(aRepo, aMessage, out string aSha);
            if (aCommitFail != null) return aCommitFail;
            aResult.AddValue("sha", aSha);
            aResult.AddValue("staged_files", aFiles.Count.ToString(CultureInfo.InvariantCulture));

            // ── ④ 公告領薪（委派；獨立結算）──────────────────────────────
            string aPrimary = aPersonas[0];
            string aAnnounce = BuildAnnouncement(aMessage, aSha, aRepo, aPersonas,
                                                 iArgs.Get("announce_body"), iArgs.Get("bump_of"));
            SCP_TavernPostVerdict aPosted = Announce(aDataRoot, aPrimary, aAnnounce, aSha, aResult);
            aResult.AddValue("announce", aPosted.Outcome.ToString());

            if (aPosted.Outcome == SCP_TavernPostOutcome.Unresolved)
            {
                // ⛔ 這一段**不准印補發指令** —— 印了就是替讀者做一個他還沒有讀數可以做的決定。
                aResult.Lines.Add("⚠ **commit 落地了（" + aSha + "）；公告「不知道」** —— 沒等到回執，"
                                  + "⛔ 這**不代表沒發**。");
                aResult.Lines.Add("   → 先回讀（判準：`result=Success` ＋ 有 `post_seq` ⇒ 發了）：");
                aResult.Lines.Add("     " + (aPosted.RecheckHint.Length > 0
                    ? aPosted.RecheckHint
                    : "（閘沒有給回讀指令 —— 去看 " + aDataRoot + "/_cmd_results/ 最新那筆與酒館）"));
                aResult.Lines.Add("   ⛔ **確認真的沒發之前不要補** —— 同一個 SHA 貼兩次是**付兩次錢**。");
                aResult.Lines.Add("   ⛔ 單號推進本次**沒有做**（它掛在公告成功之後）。");
                aResult.ExitCode = ExitAnnounceUnresolved;
                return aResult;
            }
            if (aPosted.Outcome == SCP_TavernPostOutcome.NotPosted)
            {
                aResult.Lines.Add("⚠ **commit 落地了（" + aSha + "）；公告確定沒發** —— 只有領薪那步沒完成。");
                aResult.Lines.Add("   → 補發（酒館發文只有 Editor 那條路）：senate ucmd run Tavern --persona "
                                  + aPrimary + " --arg op=post --arg-file body=<檔> --arg meta=tag:commit;sha:"
                                  + aSha + ";category:meta");
                aResult.Lines.Add("   ⛔ 單號推進本次**沒有做**（它掛在公告成功之後）。");
                aResult.ExitCode = ExitAnnounceNotSent;
                return aResult;
            }

            aResult.Lines.Add("✅ " + aSha + " 已提交並公告（" + aPrimary + "／" + aFiles.Count + " 檔）"
                              + (aPosted.Seq.Length > 0 ? "　seq=" + aPosted.Seq : ""));

            // ── ⑤ 單號推進（委派；失敗只警告 —— commit 與領薪是主線）─────────
            AdvanceTasks(aMessage, aSha, aPrimary, aDataRoot, aResult);
            return aResult;
        }

        // 區塊職責：`personas=a,b,c` → 去重保序的清單。
        // 物理意義：順序就是 trailer 的順序，而**第一位是公告的署名人** —— 那不是隨便挑的。
        // 數值影響：空白項與重複項丟掉（重跑同一道指令不該長出兩行同樣的 trailer）。
        static List<string> SplitPersonas(string iRaw)
        {
            List<string> aOut = new List<string>();
            HashSet<string> aSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string aOne in (iRaw ?? "").Split(','))
            {
                string aT = aOne.Trim();
                if (aT.Length == 0 || !aSeen.Add(aT)) continue;
                aOut.Add(aT);
            }
            return aOut;
        }

        // 區塊職責：每位 persona 一行 trailer；擋不下的問題收進 oProblems。
        // 數值影響：⛔ 信箱是哨兵或形狀可疑 ⇒ **預設擋下**（除非 allow_unset）。
        //          agent 欄空白只出聲不擋 —— 那會印成 `?@<persona>`，難看但不是假資訊。
        static void BuildTrailers(List<string> iPersonas, string iLettersRoot, string iRegion,
                                  string iDataRoot, bool iAllowUnset,
                                  List<string> oTrailers, List<string> oProblems, SCP_CmdResult ioResult)
        {
            foreach (string aPersona in iPersonas)
            {
                SCP_AgentEmailInfo aInfo = SCP_AgentEmail.Resolve(iLettersRoot, aPersona, iRegion,
                                                                  iDataRoot, w => ioResult.Lines.Add("⚠ " + w));
                if (aInfo.Source == "fallback")
                    ioResult.Lines.Add("⚠ " + aPersona + " 的信箱吃**全域 fallback**（" + aInfo.Email
                                       + "），不是自己的位址。");
                bool aBad = aInfo.Email == SCP_AgentEmail.UnsetSentinel
                            || !SCP_AgentEmail.LooksLikeEmail(aInfo.Email);
                if (aBad)
                {
                    string aMsg = aPersona + " 的信箱未設定或形狀可疑（" + aInfo.Email
                                  + "）—— 到 Editor 的 Persona & Agent 管理頁設定";
                    if (iAllowUnset) ioResult.Lines.Add("⚠ " + aMsg + "（`allow_unset=1` 已放行）");
                    else { oProblems.Add(aMsg); continue; }
                }
                oTrailers.Add(SCP_AgentEmail.BuildTrailer(iLettersRoot, aPersona, iRegion, iDataRoot,
                                                          w => ioResult.Lines.Add("⚠ " + w)));
            }
        }

        // 區塊職責：把 trailer 併到訊息尾端；已經有同一行就不重複加。
        // 物理意義：重跑同一道指令不該長出兩份 trailer —— 那會讓 history 出現看起來像兩個人的一個人。
        static string ComposeMessage(string iBody, List<string> iTrailers)
        {
            string aText = (iBody ?? "").TrimEnd('\n', '\r');
            HashSet<string> aExisting = new HashSet<string>(StringComparer.Ordinal);
            string[] aLines = aText.Replace("\r\n", "\n").Split('\n');
            foreach (string aLine in aLines)
                if (aLine.Trim().StartsWith(TrailerPrefix, StringComparison.Ordinal))
                    aExisting.Add(aLine.Trim());
            List<string> aFresh = new List<string>();
            foreach (string aT in iTrailers) if (!aExisting.Contains(aT)) aFresh.Add(aT);
            if (aFresh.Count == 0) return aText + "\n";
            bool aTailIsTrailer = aLines.Length > 0
                && aLines[aLines.Length - 1].Trim().StartsWith(TrailerPrefix, StringComparison.Ordinal);
            string aSep = (aExisting.Count > 0 && aTailIsTrailer) ? "\n" : "\n\n";
            return aText + aSep + string.Join("\n", aFresh) + "\n";
        }

        // 區塊職責：`expect_files` 的比對與擋下。
        // 🩸 為什麼有這一格：2026-08-24 `git add <目錄>` 收走同事正在寫的兩張單，
        //   而 `--name-only` 的清單**印出來了、就在下一行**。⇒「下次記得看」是願望；
        //   有效的只有兩種形狀：把清單縮短，或把手勢換掉。本旗標屬後者。
        // 數值影響：不符 ⇒ exit 2 **且在 git commit 之前**返回 ⇒ 沒有東西落地。不帶＝不檢查。
        static SCP_CmdResult? CheckExpectFiles(string iRaw, List<string> iFiles)
        {
            string aRaw = (iRaw ?? "").Trim();
            if (aRaw.Length == 0) return null;
            if (!int.TryParse(aRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aWant))
                return SCP_CmdResult.Fail(2, "⛔ `expect_files` 不是整數：" + aRaw);
            if (aWant == iFiles.Count) return null;
            SCP_CmdResult aFail = SCP_CmdResult.Fail(2,
                "⛔ `expect_files=" + aWant + "` 但實際 staged **" + iFiles.Count + "** 個檔 ⇒ 擋下，沒有提交。",
                "   完整清單（這就是那個「印出來了而沒被讀」的讀數）：");
            foreach (string aF in iFiles) aFail.Lines.Add("     - " + aF);
            aFail.Lines.Add("   ⇒ 要嘛改數字（先確認每一個檔都是你要收的），要嘛 unstage 不該收的"
                            + "（別人正在寫的檔不會有任何一層喊）。");
            return aFail.AddValue("staged_files", iFiles.Count.ToString(CultureInfo.InvariantCulture));
        }

        // 區塊職責：真正的 `git commit`。
        // 物理意義：訊息走**暫存檔**（`-F <file>`）—— `SCP_Git` 沒有 stdin，而長訊息也不該經過 shell
        //          （🩸 反引號被當命令替換執行掉、公告缺一段，而已公告領薪的訊息無法 amend）。
        // 數值影響：暫存檔放**系統暫存區不放 repo 內** —— 放 repo 裡就有被下一個 `git add` 收走的風險。
        static SCP_CmdResult? DoCommit(string iRepo, string iMessage, out string oSha)
        {
            oSha = "";
            string aTmp = Path.Combine(Path.GetTempPath(),
                                       "scp_commit_" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(aTmp, iMessage, new UTF8Encoding(false));
                SCP_GitResult aRun = SCP_Git.Run(iRepo, "commit", "-F", aTmp);
                if (!aRun.Ok)
                    return SCP_CmdResult.Fail(ExitCommitFailed,
                        "⛔ `git commit` 失敗 ⇒ **什麼都沒落地**：" + aRun.ReasonLine);
            }
            catch (Exception e)
            {
                return SCP_CmdResult.Fail(ExitCommitFailed, "⛔ 寫訊息暫存檔失敗：" + e.Message);
            }
            finally
            {
                try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch { /* ephemeral，刪不掉不影響已落地的 commit */ }
            }
            SCP_GitResult aRev = SCP_Git.Run(iRepo, "rev-parse", "--short", "HEAD");
            oSha = aRev.Ok ? SCP_Git.FirstLine(aRev.StdOut).Trim() : "";
            return null;
        }

        // 區塊職責：commit 訊息 → 公告內文。
        // 物理意義：commit 訊息本來就是寫給人看的；再叫人另外寫一份公告等於同一件事寫兩遍 ——
        //          寫兩遍一定有一遍會被省略，而被省略的通常是後面那遍（所以錢才領不到）。
        // 數值影響：trailer 行從公告裡剝掉（讀的人不需要看信箱），改成一行「參與者」。
        static string BuildAnnouncement(string iMessage, string iSha, string iRepo,
                                        List<string> iPersonas, string iIntro, string iBumpOf)
        {
            List<string> aLines = new List<string>();
            foreach (string aLine in iMessage.Replace("\r\n", "\n").Split('\n'))
                if (!aLine.Trim().StartsWith(TrailerPrefix, StringComparison.Ordinal)) aLines.Add(aLine);
            string aLabel = (iRepo == "." || iRepo.Length == 0) ? "主專案" : new DirectoryInfo(iRepo).Name;
            string aSubject = aLines.Count > 0 ? aLines[0].Trim() : "(無標題)";
            if (!string.IsNullOrEmpty(iBumpOf))
                // pointer bump 的公告刻意極簡：帳要留（SHA 在 meta 裡），但版面不該跟主 commit 搶。
                return "📦 **" + aLabel + " `" + iSha + "`** — " + aSubject
                       + "（pointer bump，內容見 `" + iBumpOf + "` 那則）";
            while (aLines.Count > 0 && aLines[aLines.Count - 1].Trim().Length == 0)
                aLines.RemoveAt(aLines.Count - 1);
            StringBuilder aSb = new StringBuilder();
            aSb.Append("📦 **").Append(aLabel).Append(" `").Append(iSha).Append("`** — ")
               .Append(aSubject).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(iIntro)) aSb.Append(iIntro.Trim()).Append("\n\n");
            if (aLines.Count > 1)
            {
                string aRest = string.Join("\n", aLines.GetRange(1, aLines.Count - 1)).Trim();
                if (aRest.Length > 0) aSb.Append(aRest).Append("\n\n");
            }
            aSb.Append("👥 參與者：");
            for (int i = 0; i < iPersonas.Count; ++i)
            {
                if (i > 0) aSb.Append(" / ");
                aSb.Append('@').Append(iPersonas[i]);
            }
            return aSb.ToString();
        }

        static SCP_TavernPostVerdict Announce(string iDataRoot, string iPersona, string iBody,
                                              string iSha, SCP_CmdResult ioResult)
        {
            SCP_ITavernPostGateway? aGate = SCP_TavernPostGatewayHost.Create(iDataRoot);
            if (aGate == null)
                // ⚠ 沒登記閘 ≠ 發出去了。
                return SCP_TavernPostVerdict.Bad("本宿主**沒有登記發文閘** ⇒ 這一則沒有發出去");
            Dictionary<string, string> aMeta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tag"] = "commit",
                ["sha"] = iSha,
                ["category"] = "meta",
            };
            try { return aGate.Post(iPersona, iBody, aMeta, "", ioResult.Lines); }
            catch (Exception e) { return SCP_TavernPostVerdict.Bad("發文閘丟例外：" + e.Message); }
        }

        // 區塊職責：訊息裡的 `Fixes/Refs TASK-<n>` → 逐張送出推進訊號。
        // 物理意義：把狀態推進掛在**修東西的人一定會走的路**上 —— 舊的任務系統死因就是
        //          「狀態要有人專程回來推」，而沒有人會專程回來。
        // 數值影響：**只警告不致命** —— commit 已經落地、錢也領了，不該讓它看起來失敗。
        // ⚠ 頂格錨定：縮排代表引用（`git log` 貼上是四空白），引述別人的 trailer 不該觸發推進。
        static void AdvanceTasks(string iMessage, string iSha, string iPersona, string iDataRoot,
                                 SCP_CmdResult ioResult)
        {
            // (index → mode) 保序去重；同一張單同時寫兩者時 **Fixes 優先**（它是較強的宣告）。
            List<KeyValuePair<string, string>> aSeen = new List<KeyValuePair<string, string>>();
            HashSet<string> aHas = new HashSet<string>(StringComparer.Ordinal);
            foreach (var aKw in new[] { new[] { "Fixes", "fixes" }, new[] { "Refs", "refs" } })
            {
                Regex aRe = new Regex(@"^" + aKw[0] + @"[ \t]+TASK-(\d+)\b",
                                      RegexOptions.IgnoreCase | RegexOptions.Multiline);
                foreach (Match aM in aRe.Matches(iMessage))
                {
                    // `TASK-0001` 與 `TASK-1` 是同一張單 ⇒ 去掉前導零再比。
                    string aN = int.Parse(aM.Groups[1].Value, CultureInfo.InvariantCulture)
                                   .ToString(CultureInfo.InvariantCulture);
                    if (aHas.Add(aN)) aSeen.Add(new KeyValuePair<string, string>(aN, aKw[1]));
                }
            }
            if (aSeen.Count == 0) return;

            SCP_ITaskCommitGateway? aGate = SCP_TaskCommitGatewayHost.Create(iDataRoot);
            if (aGate == null)
            {
                ioResult.Lines.Add("⚠ 本宿主**沒有登記推單閘** ⇒ 下列單**狀態沒有動**（commit 與領薪不受影響）：");
                foreach (var aKv in aSeen)
                    ioResult.Lines.Add("   · TASK-" + aKv.Key + "　手動補：senate ucmd run Task --persona "
                                       + iPersona + " --arg op=commit --arg index=" + aKv.Key
                                       + " --arg sha=" + iSha + " --arg mode=" + aKv.Value);
                return;
            }
            foreach (var aKv in aSeen)
            {
                SCP_TaskAdvanceVerdict aV;
                try { aV = aGate.Advance(iPersona, aKv.Key, iSha, aKv.Value, ioResult.Lines); }
                catch (Exception e) { aV = SCP_TaskAdvanceVerdict.Bad("推單閘丟例外：" + e.Message); }

                if (aV.Outcome == SCP_TaskAdvanceOutcome.Advanced)
                {
                    // ⛔ 這裡**不宣告**「已完成」：落到 in_review 還是 done 由 Cmd 判，讀單檔才知道。
                    ioResult.Lines.Add("📋 TASK-" + aKv.Key + " 已收到 commit（" + aKv.Value + " / " + iSha
                                       + "）—— 落到哪一格讀單檔。" + aV.Detail);
                    continue;
                }
                if (aV.Outcome == SCP_TaskAdvanceOutcome.Unresolved)
                {
                    ioResult.Lines.Add("⚠ TASK-" + aKv.Key + " 的推進**不知道成沒成**（" + aV.Detail
                                       + "）—— ⛔ 這不代表沒推。先回讀單檔再決定，別盲目手動補。");
                    if (aV.RecheckHint.Length > 0) ioResult.Lines.Add("   → " + aV.RecheckHint);
                    continue;
                }
                ioResult.Lines.Add("⚠ TASK-" + aKv.Key + " **沒有送出去**（" + aV.Detail
                                   + "）—— 單子狀態沒動，手動補：senate ucmd run Task --persona " + iPersona
                                   + " --arg op=commit --arg index=" + aKv.Key + " --arg sha=" + iSha
                                   + " --arg mode=" + aKv.Value);
            }
        }

        static bool IsOn(string iValue)
        {
            string aV = (iValue ?? "").Trim();
            return aV == "1" || aV.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
    }
}
