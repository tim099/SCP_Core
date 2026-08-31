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
using System.IO;

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
        public static SCP_WakeBriefResult Build(string iLettersRoot, string iPersona, int iWakeCount)
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
            string iLettersRoot, string iPersona, int iWakeCount, string iOutDir)
        {
            SCP_WakeBriefResult aResult = Build(iLettersRoot, iPersona, iWakeCount);
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
