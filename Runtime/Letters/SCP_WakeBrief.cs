// 區塊職責：**早安信件讀取流程的組裝端** —— 把 persona 信件庫的各記憶層拼成一份 wake brief。
// 物理意義：醒來的人只 Read 一份檔就完成 onboarding，所以這份檔的順序**即優先序**：
//           憲法 → 見叢（當期要做的）→ 見森（縱向骨架）→ 見林（10 夜濃縮）→ 見樹（昨夜的信）。
//           每一層的真相源都是 `letters/<persona>/` 底下的原檔，本檔只讀不改（唯一例外是
//           `_latest.md` 的指標自癒，見 SCP_WakeLetters.SyncLatestPointer）。
// 數值影響：主檔行數上限 <see cref="BriefLineCap"/>；超出的**非必讀**區塊整段移進續讀檔
//           （不砍內容 —— 砍掉的那段沒有人會知道它存在過）。
//
// ⚠ **射程：本檔只做「信件讀取」那幾層**（2026-08-29 首版）。python `wake_brief.py` 還有
//   §1 見根（要 fragment 索引渲染器）、§5.5 回憶、§6 記憶維護狀態（要 registry 的 wake_count
//   對帳）、§6.5 見人（relationship ＋ 畫像）、§6.6 見書、§9 動作清單（Task/Bug 讀數）。
//   那些**沒有移植**，不是漏了 —— 它們各自依賴信件庫以外的子系統。
//   ⇒ 這份 C# brief 與 python brief **不是同一份輸出**，不要拿其中一份當另一份的驗收。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Paths;
using SCP.Core.Tasks;

namespace SCP.Core.Letters
{
    /// <summary>一段區塊。<see cref="Essential"/> ＝ 溢出時也不准移走。</summary>
    public sealed class SCP_BriefSection
    {
        public string Title = "";
        public List<string> Lines = new List<string>();
        public bool Essential;
    }

    /// <summary>組裝結果。<see cref="Part2"/> 為 null ＝ 這次沒溢出。</summary>
    public sealed class SCP_WakeBriefResult
    {
        public string Main = "";
        public string? Part2;

        /// <summary>`_latest.md` 這次有沒有被校正。⚠ 有的話呼叫端要說出來，不要靜默。</summary>
        public bool LatestPointerHealed;

        /// <summary>主檔行數（給「有沒有逼近上限」一個可讀的數字）。</summary>
        public int MainLineCount;

        /// <summary>被移進續讀檔的區塊標題。</summary>
        public List<string> MovedSections = new List<string>();
    }

    public static class SCP_WakeBrief
    {
        /// <summary>主檔行數上限。對齊 python BRIEF_LINE_CAP。</summary>
        public const int BriefLineCap = 2000;

        /// <summary>
        /// 見樹往前合併的**唯一門檻**：累積內文行數未超過這個數就（啟動／繼續）往前撈。
        /// <para>⚠ 啟動與停止是同一個問題的兩面，所以只准有一顆數字。python 端曾經給了兩顆
        /// （入口閘 10、目標 200），結果 200 從來沒被評估過 —— 機制看起來活著，
        /// 但**條件從沒成立**。任何「各給一個值」的寫法都是留一條互相抵銷的縫。</para>
        /// <para>⚠ 語意是「還要不要再撈下一封」不是總量上限：判斷在加入之前但量的是已累積，
        /// 所以撞線那封會整封進去，總行數可能遠超這個數。刻意保留 —— 改成「加了會超過就不收」
        /// 會讓「3 行短信 ＋ 一封 200 行長信」一封都補不到。</para>
        /// </summary>
        public const int MergeStopLines = 200;

        /// <summary>往前最多再撈幾封（不含最新那封）。9 ＝ 對齊見林一單位 10 封。</summary>
        /// <remarks>
        /// ⚠ 尺只有一把，量的是**封數不是天數**。python 端一度多加一個「或超過 N 天前」的日期閘，
        /// 而那把多出來的尺剛好對著最需要補信的情況（空窗久）關門。
        /// **規格沒說的維度不要自己補一把尺。**
        /// </remarks>
        public const int MergeMaxExtra = 9;

        /// <summary>
        /// 生成 brief 文字。**不寫檔**（落檔由呼叫端決定，見 <see cref="Write"/>）。
        /// </summary>
        /// <param name="iLettersRoot">信件夾根目錄。</param>
        /// <param name="iPersona">persona 名。</param>
        /// <param name="iWakeCount">
        /// 本次醒來是第幾次。**由呼叫端給**，本檔不推導 ——
        /// ⚠ 「用 `wakes/` 的信數當 wake_count」是錯的：實測 basecamp 信數 78 而 wake_count 79
        /// （這一次的信還沒寫）。推導出來的數字會**每天都差一**，而且一路正常地印在標題上。
        /// </param>
        /// <param name="iDataRoot">
        /// 資料根（給了才數缺陷單）。⚠ 不給**不是**零張 —— §6 會明說「這台沒給資料根」，
        /// 因為印 0 會被讀成「沒有 bug」，而那是一句沒有讀數的話。
        /// </param>
        public static SCP_WakeBriefResult Build(string iLettersRoot, string iPersona, int iWakeCount,
                                               string? iDataRoot = null)
        {
            var aResult = new SCP_WakeBriefResult();

            // 見樹的真相源修復要在讀它之前 —— 讀完再修就是拿舊的那份組 brief。
            (string? aPointer, bool aHealed) = SCP_WakeLetters.SyncLatestPointer(iLettersRoot, iPersona);
            aResult.LatestPointerHealed = aHealed;

            var aHead = new List<string>
            {
                "---",
                "type: wake_brief",
                "persona: " + iPersona,
                "wake_count: " + iWakeCount.ToString(),
                "generated_at: " + UtcNowIso(),
                "generated: mechanical   # morning 每次重生成 — 手改會被覆寫；事實來源見各層原檔",
                "source: SCP_WakeBrief (C#)   # ⚠ 只含信件讀取層，與 python wake_brief.py 不是同一份輸出",
                "---",
                "",
                "# 🌅 Wake Brief — " + iPersona + " wake #" + iWakeCount.ToString(),
                "",
                "> 讀這一份即完成信件層的 onboarding：**憲法 → 見叢 → 見森 → 見林 → 見樹**。",
                "> 順序即優先序；主檔溢出時先被移進續讀檔的是後面的非必讀層。",
                "> 各層原檔路徑都附在區塊標題後，需要細節再點進去。",
                "",
            };

            // 憲法 —— **緊接 header，不走 sections 機制**：sections 會因主檔溢出被移進續讀檔，
            // 而一份會被移走的憲法不算憲法。
            aHead.AddRange(ConstitutionLines(iLettersRoot, iPersona));

            var aSections = new List<SCP_BriefSection>
            {
                RootSection(iLettersRoot, iPersona),
                KeysSection(iLettersRoot, iPersona),
                ForestSection(iLettersRoot, iPersona),
                DigestSection(iLettersRoot, iPersona),
                TreeSection(iLettersRoot, iPersona, aPointer),
                RecallSection(iLettersRoot, iPersona, iWakeCount),
                MaintenanceSection(iLettersRoot, iPersona, iWakeCount, iDataRoot),
                PeopleSection(iLettersRoot, iPersona),
                BookshelfSection(iLettersRoot, iPersona, iWakeCount),
                NextActionsSection(iLettersRoot, iPersona, iWakeCount),
            };

            // 組裝 ＋ 上限處理：超出上限的「非必讀」區塊整段移進續讀檔（不砍內容）
            var aMain = new List<string>(aHead);
            var aOverflow = new List<string>();
            int aUsed = aHead.Count;
            foreach (SCP_BriefSection aSection in aSections)
            {
                List<string> aBlock = SCP_LetterText.SectionLines(aSection.Title, aSection.Lines);
                if (aSection.Essential || aUsed + aBlock.Count <= BriefLineCap)
                {
                    aMain.AddRange(aBlock);
                    aUsed += aBlock.Count;
                }
                else
                {
                    aOverflow.AddRange(aBlock);
                    aResult.MovedSections.Add(aSection.Title);
                }
            }
            if (aResult.MovedSections.Count > 0)
            {
                aMain.Add("## 📎 可續讀（超出主檔上限，已分檔不刪內容）");
                aMain.Add("");
                foreach (string aTitle in aResult.MovedSections) aMain.Add("- " + aTitle);
                aMain.Add("");
                aMain.Add("→ 續讀檔：`wake_brief_part2.md`（視情況再讀）");
                aMain.Add("");
            }

            aResult.Main = string.Join("\n", aMain);
            aResult.MainLineCount = aMain.Count;
            aResult.Part2 = aOverflow.Count > 0 ? string.Join("\n", aOverflow) : null;
            return aResult;
        }

        /// <summary>生成並落檔。回主檔路徑。</summary>
        /// <remarks>
        /// ⚠ 沒溢出時會**刪掉**上一輪殘留的續讀檔 —— 留著的話下次有人去讀，
        /// 讀到的是一份格式完整、內容過期的檔（「舊快照假綠」的同族）。
        /// </remarks>
        public static (string Path, SCP_WakeBriefResult Result) Write(
            string iLettersRoot, string iPersona, int iWakeCount, string iOutDir, string? iDataRoot = null)
        {
            SCP_WakeBriefResult aResult = Build(iLettersRoot, iPersona, iWakeCount, iDataRoot);
            Directory.CreateDirectory(iOutDir);

            string aMainPath = Path.Combine(iOutDir, "wake_brief.md");
            File.WriteAllText(aMainPath, aResult.Main);

            string aPart2Path = Path.Combine(iOutDir, "wake_brief_part2.md");
            if (aResult.Part2 != null)
            {
                File.WriteAllText(aPart2Path,
                    "---\ntype: wake_brief_part2\npersona: " + iPersona
                    + "\ngenerated_at: " + UtcNowIso() + "\n---\n\n" + aResult.Part2);
            }
            else if (File.Exists(aPart2Path))
            {
                try { File.Delete(aPart2Path); } catch (Exception) { /* 刪不掉不該蓋掉主檔已成功 */ }
            }
            return (aMainPath, aResult);
        }

        // ── 各層 ──────────────────────────────────────────────────

        static List<string> ConstitutionLines(string iLettersRoot, string iPersona)
        {
            string aPath = SCP_WakeLetters.ConstitutionPath(iLettersRoot, iPersona);
            if (!File.Exists(aPath))
            {
                // ⚠ 「還沒立憲」與「讀不到」不同形：前者是常態（新 persona），要說得出來是哪一種。
                return new List<string> { "> 📜 （本 persona 尚未立憲：找不到 `_constitution.md`）", "" };
            }
            var aOut = new List<string>
            {
                "> 📜 **" + iPersona + " 憲法** — 事實源 `letters/" + iPersona + "/_constitution.md`",
                "",
            };
            try
            {
                string aText = SCP_LetterText.StripFrontmatter(File.ReadAllText(aPath)).Trim();
                aOut.AddRange(SCP_LetterText.DemoteHeadings(aText.Split('\n')));
            }
            catch (Exception e)
            {
                aOut.Add("> ⚠ 憲法讀不到（檔在但讀失敗）：" + e.GetType().Name + ": " + e.Message);
            }
            aOut.Add("");
            return aOut;
        }

        // 區塊職責：§1 見根 —— 必讀關鍵記憶索引。
        // 物理意義：**渲染器不在本檔** —— 直接用 SCP_Fragments.RootIndexBody，
        //          也就是 `_root_index.md` 那份檔用的同一支。索引檔與 brief §1 是
        //          **同一份內容的兩個框**，不是兩份各自算的清單。
        //          🩸 為什麼堅持共用：兩處各寫一份的話，症狀是「索引說 18 筆、brief 說 17 筆」，
        //          而兩邊都不報錯 —— 同族活體見 UCL 那側的 commands_schema（宣告 30 op／實作 39 分支）。
        // 數值影響：純讀。無 fragment 時 body 仍會印表頭與 0 筆 —— 那是「這個人還沒留碎片」的
        //          誠實讀數，不是缺陷（⚠ 與 WriteRootIndex 的「0 筆不建檔」刻意不同：
        //          不建檔是因為一份 0 筆的**檔案**跟沒開始長得一樣，而 brief 裡那一節缺席才是誤導）。
        static SCP_BriefSection RootSection(string iLettersRoot, string iPersona)
        {
            var aSection = new SCP_BriefSection
            {
                Title = "🌱 §1 見根 — 必讀關鍵記憶（`_root_index.md`）",
            };
            aSection.Lines.AddRange(SCP_Fragments.RootIndexBody(iLettersRoot, iPersona, iHeadingPrefix: "###"));
            return aSection;
        }

        static SCP_BriefSection KeysSection(string iLettersRoot, string iPersona)
        {
            (List<string> aTodo, List<string> aDone) = SCP_WakeLetters.KeysEntries(iLettersRoot, iPersona);
            var aLines = new List<string>();
            if (aTodo.Count == 0) aLines.Add("(當期無未勾銷事項)");
            else foreach (string aItem in aTodo) aLines.Add("- [ ] " + aItem);

            if (aDone.Count > 0)
            {
                aLines.Add("");
                for (int i = Math.Max(0, aDone.Count - 3); i < aDone.Count; i++)
                    aLines.Add("- [x] " + aDone[i]);
            }
            return new SCP_BriefSection
            {
                Title = "🌿 §2 見叢 — 當期交棒清單（" + aTodo.Count + " 未完 / " + aDone.Count + " 已完）",
                Lines = aLines,
                Essential = true,
            };
        }

        static SCP_BriefSection ForestSection(string iLettersRoot, string iPersona)
        {
            List<string> aForests = SCP_WakeLetters.ListForests(iLettersRoot, iPersona);
            List<string> aDigests = SCP_WakeLetters.ListDigests(iLettersRoot, iPersona);
            if (aForests.Count == 0)
            {
                return new SCP_BriefSection
                {
                    Title = "🌲 §3 見森",
                    Lines = new List<string>
                    {
                        "(未達門檻：見林 " + aDigests.Count + "/" + SCP_WakeLetters.ForestDigestThreshold
                        + " 份，第 " + SCP_WakeLetters.ForestDigestThreshold + " 份見林起開始折疊)",
                    },
                    Essential = true,
                };
            }
            string aLatest = aForests[aForests.Count - 1];
            return new SCP_BriefSection
            {
                Title = "🌲 §3 見森 gen" + aForests.Count + "（`" + Path.GetFileName(aLatest) + "`）",
                Lines = BodyLines(aLatest),
                Essential = false,
            };
        }

        static SCP_BriefSection DigestSection(string iLettersRoot, string iPersona)
        {
            List<string> aDigests = SCP_WakeLetters.ListDigests(iLettersRoot, iPersona);
            if (aDigests.Count == 0)
            {
                return new SCP_BriefSection
                {
                    Title = "🌳 §4 見林",
                    Lines = new List<string> { "(尚無 digest)" },
                    Essential = true,
                };
            }
            // ⚠ 全文 inline 不截斷：見林本身已經是壓縮產物，再砍一次等於壓縮兩次，
            //   而被砍掉的尾段正是「反覆踩的陷阱 / 未解線」那些最該進反射弧的部分。
            //   留一行「其餘見 <path>」看似誠實，但沒人會為了 20 行去開第二個檔 ——
            //   **要人多開一個檔的資訊等於沒給**。
            string aLatest = aDigests[aDigests.Count - 1];
            List<string> aLines = SCP_LetterText.DemoteHeadings(BodyLines(aLatest));
            return new SCP_BriefSection
            {
                Title = "🌳 §4 見林（`" + Path.GetFileName(aLatest) + "`，全文 " + aLines.Count + " 行）",
                Lines = aLines,
                Essential = false,
            };
        }

        static SCP_BriefSection TreeSection(string iLettersRoot, string iPersona, string? iPointer)
        {
            if (iPointer == null || !File.Exists(iPointer))
            {
                return new SCP_BriefSection
                {
                    Title = "🍃 §5 見樹",
                    Lines = new List<string> { "(尚無自寫的收尾信)" },
                    Essential = true,
                };
            }

            List<string> aBody = ReadLetterBody(iPointer);
            string aTitle = "🍃 §5 見樹 — 最新 letter（`_latest.md`）";

            List<SCP_LetterRef> aLetters = SCP_WakeLetters.RecentSelfLetters(iLettersRoot, iPersona);
            var aUsed = new List<SCP_LetterRef>();
            int aTotal = 0;
            if (aLetters.Count > 0 && SCP_LetterText.BodyLineCount(aLetters[0].Path) <= MergeStopLines)
            {
                aUsed.Add(aLetters[0]);
                aTotal = SCP_LetterText.BodyLineCount(aLetters[0].Path);
                for (int i = 1; i < aLetters.Count && i <= MergeMaxExtra; i++)
                {
                    if (aTotal > MergeStopLines) break;      // 已經夠讀了
                    aUsed.Add(aLetters[i]);
                    aTotal += SCP_LetterText.BodyLineCount(aLetters[i].Path);
                }
            }

            // ⚠ 只有真的補到第二封才算合併。**顯示層說謊比排版難看嚴重** ——
            //   印「已往前合併 1 封」而實際一封都沒補，讀的人會以為手上有更多上下文。
            if (aUsed.Count > 1)
            {
                var aMerged = new List<string>();
                for (int i = aUsed.Count - 1; i >= 0; i--)   // 倒序＝最早 → 最新，時序推進才讀得順
                {
                    SCP_LetterRef aRef = aUsed[i];
                    bool aIsNewest = (i == 0);
                    string aWhen = aRef.Day.Length > 0 ? aRef.Day : "日期不明";
                    if (aMerged.Count > 0) { aMerged.Add(""); aMerged.Add("---"); aMerged.Add(""); }
                    aMerged.Add("### 📅 " + aWhen + "（" + (aIsNewest ? "最新一封" : "往前補") + "）");
                    aMerged.Add("");
                    aMerged.AddRange(ReadLetterBody(aRef.Path));
                }
                aBody = aMerged;
                aTitle = "🍃 §5 見樹 — 已往前合併 " + aUsed.Count + " 封收尾信（共 " + aTotal
                         + " 行內文；由早到近，最新那封在最後）";
            }

            return new SCP_BriefSection { Title = aTitle, Lines = aBody, Essential = false };
        }

        // ── §6 記憶維護狀態 ───────────────────────────────────────
        // 區塊職責：機械判定「該不該去濃縮了」。最短、必讀。
        // 物理意義：見林一單位 = DigestSpan 個 wake ⇒ gap = 本次 wake − 最後一份見林涵蓋到的 wake。
        // 數值影響：只印不改檔；**不替沒有讀數的格子填 0** —— 缺讀數與零是兩件事。
        static SCP_BriefSection MaintenanceSection(string iLettersRoot, string iPersona, int iWakeCount,
                                                   string? iDataRoot)
        {
            List<string> aDigests = SCP_WakeLetters.ListDigests(iLettersRoot, iPersona);
            List<string> aForests = SCP_WakeLetters.ListForests(iLettersRoot, iPersona);
            var aLines = new List<string>();

            int aCovered = aDigests.Count > 0 ? LastCoveredWake(aDigests[aDigests.Count - 1]) : 0;
            if (aDigests.Count == 0)
            {
                aLines.Add("- 見林進度：尚無見林（本次 wake " + iWakeCount + "）");
            }
            else if (aCovered <= 0)
            {
                // 🩸 檔名解析不出來時**不准假裝 gap 是 0** —— 那會讓「該濃縮了」永遠不出現。
                aLines.Add("- ⚠ 見林進度：最後一份是 `" + Path.GetFileName(aDigests[aDigests.Count - 1])
                           + "`，但檔名解不出涵蓋到第幾個 wake ⇒ **gap 量不到**（不是 0）");
            }
            else
            {
                int aGap = iWakeCount - aCovered;
                string aMark = aGap >= DigestGapOverdue ? "🔴 **OVERDUE**" : "✓";
                aLines.Add("- " + aMark + " 見林進度：gap=" + aGap + "/" + DigestGapOverdue
                           + "（上次到 wake " + aCovered + "）");
            }

            aLines.Add("- " + (aForests.Count > 0
                       ? "見森已折到第 " + aForests.Count + " 份（gen" + aForests.Count + "）"
                       : "見森：尚未折過（見林 " + aDigests.Count + " 份）"));
            aLines.Add(BugCountLine(iDataRoot));
            return new SCP_BriefSection { Title = "📋 §6 記憶維護狀態", Lines = aLines, Essential = true };
        }

        /// <summary>見林一單位幾個 wake（gap 到這個數就算 OVERDUE）。對齊 python BRIEF 那側的 10。</summary>
        public const int DigestGapOverdue = 10;

        /// <summary>從見林檔名（`wake_072-081.md`）取涵蓋到的最後一個 wake。解不出來回 0。</summary>
        static int LastCoveredWake(string iPath)
        {
            string aName = Path.GetFileNameWithoutExtension(iPath);
            int aDash = aName.LastIndexOf('-');
            if (aDash < 0 || aDash + 1 >= aName.Length) return 0;
            string aTail = aName.Substring(aDash + 1);
            return int.TryParse(aTail, NumberStyles.None, CultureInfo.InvariantCulture, out int aValue)
                   ? aValue : 0;
        }

        // ── §6.5 見人 ─────────────────────────────────────────────
        // 區塊職責：回答「我認識誰」—— 見根答我是誰、見叢答我要做什麼、見樹答我昨天經歷什麼，
        //           **沒有一層答『這些同事是誰』**（Tim 2026-08-01）。
        // 物理意義：三段 —— (a) 在線同事＋好感＋最近看法 (b) 離線前 N 高 (c) 我畫的印象（全文）。
        //           ⭐ (c) 走 SCP_PortraitView.LatestPerPerson ⇒ **與 `cmd people` 同一支邏輯**
        //             （TASK-0097「一份實作、兩個消費端」）。兩處各組一次的症狀不是報錯，
        //             是「CLI 說信任、brief 說 65」而兩邊都不紅。
        // 數值影響：非必讀。分數即時讀 relationship，本段**不快照** ——
        //           分數由事件重算，抄一份就是第二個真相源。
        static SCP_BriefSection PeopleSection(string iLettersRoot, string iPersona)
        {
            var aLines = new List<string>();
            SCP_RelationshipSet aRel = SCP_Relationship.Load(iLettersRoot, iPersona);
            SCP_PersonaScan aScan = SCP_PersonaLetters.Scan(iLettersRoot, null);

            var aOnline = new List<string>();
            foreach (SCP_PersonaStatus aStatus in aScan.Personas)
            {
                if (aStatus.Online != SCP_PersonaOnline.Online) continue;
                if (string.Equals(aStatus.Name, iPersona, StringComparison.OrdinalIgnoreCase)) continue;
                aOnline.Add(aStatus.Name);
            }
            aOnline.Sort((a, b) => ScoreOf(aRel, b).CompareTo(ScoreOf(aRel, a)));

            if (aOnline.Count > 0)
            {
                aLines.Add("**🟢 現在在線（" + aOnline.Count + " 人）**");
                foreach (string aName in aOnline) aLines.AddRange(PersonBlock(aRel, aName));
                aLines.Add("");
            }
            else
            {
                // ⚠ 空要說出是哪一種空：真的沒人 vs lock 量不到。合成一句就是拿未知冒充事實。
                aLines.Add(aScan.UnknownCount > 0 || aScan.Problems.Count > 0
                           ? "**🟢 現在在線**：(量不到 —— lock 未知 " + aScan.UnknownCount
                             + " 人；**未知 ≠ 沒人**)"
                           : "**🟢 現在在線**：(掃到 " + aScan.Personas.Count + " 人，其他人都離線)");
                aLines.Add("");
            }

            var aOffline = new List<SCP_RelationshipEntry>();
            foreach (SCP_RelationshipEntry aEntry in aRel.Entries)
            {
                if (ContainsName(aOnline, aEntry.Target)) continue;
                if (string.Equals(aEntry.Target, iPersona, StringComparison.OrdinalIgnoreCase)) continue;
                if (string.Equals(aEntry.Target, "Tim", StringComparison.OrdinalIgnoreCase)) continue;
                aOffline.Add(aEntry);
            }
            aOffline.Sort((a, b) => b.SurfaceScore.CompareTo(a.SurfaceScore));
            if (aOffline.Count > 0)
            {
                aLines.Add("**⚪ 離線・好感前 " + PeopleOfflineTop + "**");
                for (int i = 0; i < aOffline.Count && i < PeopleOfflineTop; i++)
                    aLines.AddRange(PersonBlock(aRel, aOffline[i].Target));
                aLines.Add("");
            }

            List<SCP_PortraitItem> aItems = SCP_PortraitView.LatestPerPerson(
                iLettersRoot, iPersona, PeoplePortraitCount, PeoplePortraitDays);
            if (aItems.Count > 0)
            {
                aLines.Add("**🖼 最近印象最深的 " + aItems.Count + " 位（我的 sketchbook，近 "
                           + PeoplePortraitDays + " 天・全文）**");
                aLines.Add("");
                foreach (SCP_PortraitItem aItem in aItems)
                {
                    string aWhen = aItem.At.Length >= 10 ? aItem.At.Substring(0, 10) : aItem.At;
                    aLines.Add("### 🖼 " + aItem.About + "　_" + aWhen + "_"
                               + (aItem.Headline.Length > 0 ? "　" + aItem.Headline : ""));
                    if (aItem.Consolidated != null)
                    {
                        SCP_ConsolidatedRef aRef = aItem.Consolidated;
                        aLines.Add("> ⚓ 另有濃縮 **v" + aRef.Version + "**"
                                   + (aRef.WakeRange.Length > 0
                                      ? "（" + (aRef.By.Length > 0 ? aRef.By + " " : "") + aRef.WakeRange + "）"
                                      : "（區間不明）")
                                   + "　`" + Path.GetFileName(aRef.Path) + "`");
                    }
                    aLines.Add("");
                    if (aItem.Path.Length == 0)
                    {
                        // 只有濃縮、近期沒畫 —— 這一格就是「搬 raw 之後 §6.5 不會空」的落點。
                        aLines.Add("_(近 " + PeoplePortraitDays + " 天沒有未歸檔畫像；上面那版濃縮就是目前的看法)_");
                        aLines.Add("");
                        continue;
                    }
                    aLines.AddRange(SCP_PortraitView.StripChrome(aItem.Body, aItem.About, aItem.Headline));
                    aLines.Add("");
                    if (aItem.Private.Count > 0)
                    {
                        // 私層只存在自己的 sketchbook ⇒ brief 要印：藏起來等於當初白寫。
                        aLines.Add("> 🔒 **只給我自己看**（不在對方那份裡）");
                        aLines.Add(">");
                        foreach (string aLine in aItem.Private)
                            aLines.Add(aLine.Trim().Length > 0 ? "> " + aLine : ">");
                        aLines.Add("");
                    }
                }
            }
            else
            {
                aLines.Add("**🖼 印象**：近 " + PeoplePortraitDays + " 天還沒畫過任何人 ——"
                           + "晚安時挑 1~3 位今天印象最深的同事寫下。");
                aLines.Add("");
            }

            if (aRel.Entries.Count == 0)
                aLines.Add(aRel.LoadError != null
                           ? "⚠ 關係讀取失敗（" + aRel.LoadError
                             + "）—— **這不代表沒有關係紀錄**，是這一區沒生成出來。"
                           : "_(還沒有關係紀錄 —— 跟同事互動後走 `ucl-relationship` 寫一筆)_");

            return new SCP_BriefSection { Title = "🧑 §6.5 見人 — 我認識誰", Lines = aLines, Essential = false };
        }

        /// <summary>每人印幾筆最近看法。</summary>
        public const int PeopleOpinionCount = 2;

        /// <summary>離線同事取前幾高好感。</summary>
        public const int PeopleOfflineTop = 3;

        /// <summary>(c) 段印幾位。</summary>
        public const int PeoplePortraitCount = 5;

        /// <summary>(c) 段只看近 N 天 —— 時效讓舊印象自然退場，不變成常駐標籤。</summary>
        public const int PeoplePortraitDays = 14;

        static int ScoreOf(SCP_RelationshipSet iRel, string iName)
        {
            SCP_RelationshipEntry? aEntry = iRel.Find(iName);
            return aEntry == null ? 0 : aEntry.SurfaceScore;
        }

        static List<string> PersonBlock(SCP_RelationshipSet iRel, string iName)
        {
            var aOut = new List<string>();
            SCP_RelationshipEntry? aEntry = iRel.Find(iName);
            // ⚠ 沒有關係紀錄時印 `—` 不印 0：0 分是一個判斷，沒紀錄是**沒有**判斷。
            string aScore = aEntry == null ? "—"
                            : (aEntry.ScoreParsed ? aEntry.SurfaceScore.ToString(CultureInfo.InvariantCulture) : "?");
            string aTier = (aEntry != null && aEntry.Tier.Length > 0) ? "（" + aEntry.Tier + "）" : "";
            aOut.Add("- **" + iName + "**　好感 " + aScore + aTier);
            if (aEntry == null) return aOut;
            int aFrom = Math.Max(0, aEntry.Opinions.Count - PeopleOpinionCount);
            for (int i = aFrom; i < aEntry.Opinions.Count; i++)
            {
                string aText = aEntry.Opinions[i].Replace("\r\n", " ").Replace("\n", " ").Trim();
                if (aText.Length > 0) aOut.Add("    · " + aText);
            }
            return aOut;
        }

        static bool ContainsName(List<string> iList, string iName)
        {
            foreach (string aItem in iList)
                if (string.Equals(aItem, iName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ── §9 今日動作清單 ───────────────────────────────────────
        // 區塊職責：把 §6 的機械判定翻成**當場可執行的一行**。必讀、最短。
        // 物理意義：只列「現在就成立」的動作 —— 不成立的動作寫上去會被當成待辦而永遠躺著。
        static SCP_BriefSection NextActionsSection(string iLettersRoot, string iPersona, int iWakeCount)
        {
            List<string> aDigests = SCP_WakeLetters.ListDigests(iLettersRoot, iPersona);
            var aLines = new List<string>();

            int aCovered = aDigests.Count > 0 ? LastCoveredWake(aDigests[aDigests.Count - 1]) : 0;
            int aGap = aCovered > 0 ? iWakeCount - aCovered : -1;
            if (aGap >= DigestGapOverdue)
                aLines.Add("- 🔴 **見林 OVERDUE**（gap=" + aGap + "）⇒ `cmd consolidate --arg letters_root=<root>"
                           + " --arg persona=" + iPersona + "`（不給 digest_body ＝ 只看狀態）");
            else if (aGap < 0)
                aLines.Add("- ⚠ 見林 gap 量不到（檔名解不出涵蓋範圍）⇒ 去看 `longterm/` 那幾份的檔名");
            else
                aLines.Add("- 記憶維護無待辦（見 §6）。");

            aLines.Add("- 隨時可丟未解線（不限儀式）：`cmd keys --arg letters_root=<root> --arg persona="
                       + iPersona + " --arg add=<一句話>`");
            aLines.Add("- 對同事的看法（濃縮＋未歸檔，**與本檔 §6.5 同一支邏輯**）：`cmd people --arg letters_root=<root>"
                       + " --arg persona=" + iPersona + " --arg online=1`");
            aLines.Add("- 本檔是機械產物，**手改無效**（下次覆寫）—— 要改去改 fragment / letter / 見叢原檔。");
            return new SCP_BriefSection { Title = "🎯 §9 今日動作清單", Lines = aLines, Essential = true };
        }

        // ── §5.5 回憶（Recall）───────────────────────────────────
        // 區塊職責：在見樹（最近的連續日子）之外，額外端**一封遠方的**收尾信全文。
        // 物理意義：見樹解決「接得上昨天」，回憶解決另一個問題 ——
        //           長壽 persona 的中段記憶會沉底：見林把它濃縮成幾行結論，原信從此沒人再讀。
        //           所以本段刻意端**原信全文**而不是摘要（摘要見林已經有了，再摘一次沒有新資訊）。
        // 數值影響：只影響顯示，不寫任何檔。抽不到（池空）就整段不出現，**不印空殼**。
        //
        // ⚠ 抽籤是 deterministic 的：種子 = (persona, wake_count)。
        //   brief 每次 morning 重生成，若用真隨機，同一個 wake 重跑就換一封信 ⇒
        //   「今天回憶到哪一封」不可複驗、git diff 也會無故翻動。
        // 🩸 **與 python 抽到的不會是同一封，這是規格差異不是 bug**：
        //   python 用 `random.Random("<persona>:<wake>")`（MT19937 ＋ 字串種子），
        //   .NET 沒有同一顆 PRNG ⇒ 要位元組對拍就得在 C# 重造 python 的抽法。
        //   本檔改採「穩定雜湊取模」（FNV-1a）：同一個 wake 必抽同一封、可複驗、跨端可重算，
        //   而**抽到哪一封本身不是規格的一部分**（回憶的用途是「想起遠方」，不是「想起特定那封」）。
        static SCP_BriefSection RecallSection(string iLettersRoot, string iPersona, int iWakeCount)
        {
            var aEmpty = new SCP_BriefSection { Title = "", Lines = new List<string>() };
            if (iWakeCount <= RecallMinWake) return aEmpty;      // 新生 persona 沒有「遠方」

            List<SCP_LetterRef> aLetters = SCP_WakeLetters.RecentSelfLetters(iLettersRoot, iPersona);
            var aPool = new List<SCP_LetterRef>();
            foreach (SCP_LetterRef aRef in aLetters)
            {
                int aWake = WakeNoOf(aRef.FileName);
                if (aWake <= 0) continue;                        // 解不出 wake 編號 ⇒ 算不出距離
                if (iWakeCount - aWake < RecallMinAgeWakes) continue;
                aPool.Add(aRef);
            }
            if (aPool.Count == 0) return aEmpty;                 // 空池是常態，不是異常

            aPool.Sort((a, b) => string.CompareOrdinal(a.FileName, b.FileName));   // 可複驗要先定序
            int aPick = (int)(StableHash(iPersona + ":" + iWakeCount) % (uint)aPool.Count);
            SCP_LetterRef aChosen = aPool[aPick];
            int aChosenWake = WakeNoOf(aChosen.FileName);

            var aLines = new List<string>
            {
                "> 🎲 穩定抽出（種子＝persona+wake_count，同一次醒來必抽同一封，可複驗）",
                "> 來源：wake #" + aChosenWake + "（距今 " + (iWakeCount - aChosenWake) + " 個 wake）"
                + (aChosen.Day.Length > 0 ? " · 📅 " + aChosen.Day : "")
                + " · `" + aChosen.FileName + "`",
                ">",
                "> 這是我自己寫的，只是久到已經被見林濃縮過了 —— 對照一下結論與現場。",
                "",
            };
            aLines.AddRange(SCP_LetterText.DemoteHeadings(BodyLines(aChosen.Path)));
            return new SCP_BriefSection
            {
                Title = "🕯 §5.5 回憶 — 一封遠方的收尾信",
                Lines = aLines,
                Essential = false,
            };
        }

        /// <summary>wake_count 超過這個數才開始有回憶（新生 persona 的信全在見樹／見林射程內）。</summary>
        public const int RecallMinWake = 20;

        /// <summary>「遠方」的門檻：距今至少幾個 wake。15 ＝ 見林一單位的 1.5 倍。</summary>
        public const int RecallMinAgeWakes = 15;

        /// <summary>從 `000058_<ts>.md` 取 wake 編號。解不出來回 0。</summary>
        static int WakeNoOf(string iFileName)
        {
            int aUnderscore = iFileName.IndexOf('_');
            if (aUnderscore <= 0) return 0;
            string aHead = iFileName.Substring(0, aUnderscore);
            return int.TryParse(aHead, NumberStyles.None, CultureInfo.InvariantCulture, out int aValue)
                   ? aValue : 0;
        }

        /// <summary>
        /// FNV-1a 32-bit —— 抽籤用的穩定雜湊。
        /// <para>⚠ 不可以用 <c>string.GetHashCode()</c>：.NET Core 起它**每個 process 都不一樣**
        /// （randomized hashing）⇒ 同一個 wake 重跑會換一封信，而那正是本段要避免的事。
        /// 這種壞法不會報錯，只會讓「可複驗」這句話變成假的。</para>
        /// </summary>
        static uint StableHash(string iValue)
        {
            uint aHash = 2166136261u;
            foreach (char aChar in iValue)
            {
                aHash ^= aChar;
                aHash *= 16777619u;
            }
            return aHash;
        }

        // ── §6.6 見書 ─────────────────────────────────────────────
        // 區塊職責：回答「我讀到哪」—— 見人答『我認識誰』，本段答『我在讀什麼』（Tim 2026-08-07）。
        // 物理意義：閱讀卡是 reader.json 的機械投影，本段是**唯讀消費端**；
        //           要改內容去改 reader.json 再 Sync。
        // 數值影響：只影響顯示。抽籤同 §5.5 的理由與作法（穩定雜湊，不是真隨機）。
        static SCP_BriefSection BookshelfSection(string iLettersRoot, string iPersona, int iWakeCount)
        {
            string aDir = SCP_LettersPaths.PersonaDir(new SCP_LettersRoot(iLettersRoot), iPersona)
                          + "/" + BookshelfDirName;
            var aEmpty = new SCP_BriefSection { Title = "", Lines = new List<string>() };
            if (!Directory.Exists(aDir)) return aEmpty;

            List<string> aCards;
            try { aCards = new List<string>(Directory.GetFiles(aDir, "*.md")); }
            catch (Exception) { return aEmpty; }
            if (aCards.Count == 0) return aEmpty;
            aCards.Sort(StringComparer.Ordinal);

            int aPick = (int)(StableHash(iPersona + ":bookshelf:" + iWakeCount) % (uint)aCards.Count);
            string aCard = aCards[aPick];

            var aLines = new List<string>
            {
                "**📖 穩定端上一張閱讀卡（共 " + aCards.Count + " 張・全文）**",
                "",
            };
            aLines.AddRange(SCP_LetterText.DemoteHeadings(BodyLines(aCard)));
            aLines.Add("");
            aLines.Add("> 來源：`" + BookshelfDirName + "/" + Path.GetFileName(aCard)
                       + "`（機械投影，改內容請改 reader.json 後重新 Sync）");
            return new SCP_BriefSection { Title = "📖 §6.6 見書 — 我在讀什麼", Lines = aLines, Essential = false };
        }

        /// <summary>閱讀卡目錄名（跨端契約：python `wake_brief.BOOKSHELF_DIR_NAME` 同名）。</summary>
        public const string BookshelfDirName = "bookshelf";

        /// <summary>
        /// §6 的缺陷單那一行。<paramref name="iDataRoot"/> 為空 ⇒ **說出「沒給資料根」而不是印 0**。
        /// <para>🩸 缺讀數與零張在畫面上同形，而其中一個會讓人以為沒有 bug。</para>
        /// </summary>
        static string BugCountLine(string? iDataRoot)
        {
            if (string.IsNullOrEmpty(iDataRoot))
                return "- 🐛 缺陷單張數：**未量**（本次沒給資料根 —— 未量 ≠ 零張）";
            try
            {
                var aRoot = new SCP_DataRoot(iDataRoot!);
                List<SCP_TaskEntry> aAll = SCP_TaskIO.LoadAll(aRoot);
                int aOpen = 0;
                foreach (SCP_TaskEntry aEntry in aAll)
                    if (aEntry.type == SCP_TaskType.bug && !aEntry.IsClosed()) aOpen++;
                return "- 🐛 缺陷單（type=bug）：open **" + aOpen.ToString(CultureInfo.InvariantCulture) + "** 張"
                       + "（清單 → `cmd tasks --arg data_root=<root> --arg type=bug`）";
            }
            catch (Exception e)
            {
                // 讀失敗要出聲：靜默回 0 會把「量不到」講成「沒有」。
                return "- 🐛 缺陷單張數：**量不到**（" + e.GetType().Name + ": " + e.Message + "）";
            }
        }

        // ── 讀檔小工具 ────────────────────────────────────────────

        /// <summary>剝一層 frontmatter 的內文行（見森／見林用）。讀不到就把原因寫成內容，不留空白。</summary>
        static List<string> BodyLines(string iPath)
        {
            try
            {
                string aText = SCP_LetterText.StripFrontmatter(File.ReadAllText(iPath)).Trim();
                return new List<string>(aText.Split('\n'));
            }
            catch (Exception e)
            {
                // 「讀不到」不可以長得像「內容是空的」。
                return new List<string> { "⚠ 讀不到 `" + Path.GetFileName(iPath) + "`：" + e.GetType().Name + ": " + e.Message };
            }
        }

        /// <summary>剝**多層** frontmatter ＋ 標題降階的信件內文（見樹用）。</summary>
        static List<string> ReadLetterBody(string iPath)
        {
            try
            {
                return SCP_LetterText.DemoteHeadings(
                    SCP_LetterText.StripAllFrontmatter(File.ReadAllText(iPath)));
            }
            catch (Exception e)
            {
                return new List<string> { "⚠ 讀不到 `" + Path.GetFileName(iPath) + "`：" + e.GetType().Name + ": " + e.Message };
            }
        }

        static string UtcNowIso()
            => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                System.Globalization.CultureInfo.InvariantCulture);
    }
}
