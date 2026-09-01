// 區塊職責：見人的**讀取端** —— 把「最新一版濃縮」＋「本期未歸檔的逐幅畫像」組成一份可讀的看法。
// 物理意義：這是 TASK-0097 的第一步，而它必須**先於**搬檔上線 ——
//           🩸 python `portraits.py::sketchbook_by` 對 sketchbook **根層** `glob("*.md")`、不進子目錄。
//             所以誰先把逐幅畫像搬進 `<target>/raw/`，隔天他的 brief §6.5 就會空掉，
//             而空掉的樣子跟「我最近沒在看人」一模一樣（不報錯、不缺檔、讀數全綠）。
//           ⇒ 讀取端補上「濃縮那半」之後，搬檔才是無害的。
// 數值影響：**純讀**，一個位元組都不寫。版本取最大值走整數解析（不是字串排序）。
//
// ⚠ 設計意圖（Tim 2026-09-01 拍板，讀錯它會把這支做歪）：
//   **對一個人的看法本來就隨時間改變，所以不追求精確** —— 對方也在變，舊看法的權重本來就該衰減。
//   ⇒ 本支**不回頭撈遠期 raw**：只讀 `max(v)` ＋ 根層未歸檔的那幾幅。
//     已歸檔的逐幅畫像存在的理由是「回頭對帳」，不是「每次都讀」。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    /// <summary>一份濃縮檔的指標。<see cref="Version"/> 的真相源是**檔名**，不是檔頭。</summary>
    public sealed class SCP_ConsolidatedRef
    {
        public string Path = "";
        public string Target = "";

        /// <summary>版號 —— 由檔名解析（`&lt;target&gt;_v012.md` ⇒ 12）。檔頭的 `version:` 是派生值。</summary>
        public int Version;

        /// <summary>檔頭 `wake_range:`（含主體才有意義，例如 `gura 33-49`）。讀不到就是空字串。</summary>
        public string WakeRange = "";

        /// <summary>檔頭 `by:` —— 誰的 wake 編號。空字串＝檔頭沒寫（那時 WakeRange 就是無定語的數字）。</summary>
        public string By = "";

        public string ConsolidatedAt = "";

        /// <summary>檔頭 `version:` 與檔名解析出來的不一致 ⇒ 這裡留原值。null ＝ 一致或檔頭沒寫。</summary>
        public string? HeaderVersionMismatch;
    }

    /// <summary>某個對象的完整看法視圖（濃縮 ＋ 未歸檔）。</summary>
    public sealed class SCP_PortraitTargetView
    {
        public string Target = "";

        /// <summary>最新一版濃縮。null ＝ 這個對象還沒有任何濃縮檔（不是錯誤，是還沒開始）。</summary>
        public SCP_ConsolidatedRef? Latest;

        /// <summary>該對象一共有幾版（給「是不是多版」一個讀數；1 版就別說「有多版」）。</summary>
        public int VersionCount;

        /// <summary>已歸檔（`&lt;target&gt;/raw/`）的逐幅畫像數 —— 只數不讀。</summary>
        public int ArchivedRawCount;

        /// <summary>根層**未歸檔**的逐幅畫像（新 → 舊）。</summary>
        public List<string> UnarchivedPaths = new List<string>();

        /// <summary>
        /// 目錄名與請求的大小寫不同時的實際目錄名。null ＝ 沒有這個問題。
        /// <para>⚠ 這一格不是潔癖：Windows `core.ignorecase=true` 讓兩者在磁碟上是同一個目錄，
        /// 而 git 會記成兩筆 ⇒ 症狀出現在別人 clone 之後，不在這台。</para>
        /// </summary>
        public string? DirCaseVariant;

        /// <summary>讀取過程中「量不到」的原因（不是「沒有資料」）。⚠ 空清單才代表真的量到了。</summary>
        public List<string> Problems = new List<string>();
    }

    /// <summary>見人 (c) 段的一筆：某個對象「最新一幅畫像」＋（若有）最新一版濃縮的指標。</summary>
    public sealed class SCP_PortraitItem
    {
        public string About = "";
        public string At = "";
        public string Headline = "";

        /// <summary>那幅未歸檔畫像的路徑。空字串 ＝ 這人近 N 天沒被畫（但可能有濃縮）。</summary>
        public string Path = "";

        public List<string> Body = new List<string>();

        /// <summary>私層（只存在自己的 sketchbook，投遞件沒有這段）。</summary>
        public List<string> Private = new List<string>();

        public SCP_ConsolidatedRef? Consolidated;

        /// <summary>
        /// 份量 —— 這個對象累積過幾幅畫像（未歸檔 ＋ 已歸檔）。
        /// <para>⚠ 只用在**同時間的排序第二鍵**，不進任何顯示文字：它是「我畫過他幾次」，
        /// 不是「我多在意他」—— 兩者相關但不相等，印出來會被讀成後者。</para>
        /// </summary>
        public int Weight;
    }

    public static class SCP_PortraitView
    {
        /// <summary>私層分隔標記（跨端契約：python `portraits.PRIVATE_MARKER` 逐字相同）。</summary>
        public const string PrivateMarker = "<!-- private:below-this-line-stays-in-sketchbook -->";

        /// <summary>
        /// 找某個對象的濃縮目錄實際名字（處理大小寫變體）。
        /// <para>回傳 (實際目錄路徑, 實際目錄名)。目錄不存在時回傳 canonical 路徑與 null 名字。</para>
        /// <para>⚠ 不建目錄、不改名 —— 本支純讀。改名是寫入端的事，而寫入端要**擋**變體不是修它。</para>
        /// </summary>
        public static (string Dir, string? ActualName) ResolveTargetDir(string iLettersRoot, string iPersona,
                                                                       string iTarget)
        {
            var aRoot = new SCP_LettersRoot(iLettersRoot);
            string aCanonical = SCP_LettersPaths.SketchbookTargetDir(aRoot, iPersona, iTarget);
            string aSketchbook = SCP_LettersPaths.SketchbookDir(aRoot, iPersona);
            if (!Directory.Exists(aSketchbook)) return (aCanonical, null);

            // 🩸 **不准用 `Directory.Exists(canonical)` 抄捷徑** ——
            //   NTFS 是大小寫不敏感的，問 `sirius/` 存不存在時 `Sirius/` 會回 true，
            //   於是「偵測大小寫變體」的那格會被它要偵測的那件事本身騙過去（2026-09-01 實測，
            //   第一版就是這樣寫的，fixture 裡 `Sirius/` 對 `sirius` 一聲都沒出）。
            //   ⇒ 名字的比對一律**列目錄拿磁碟上的真名**再 Ordinal 比。
            foreach (string aDir in SafeDirs(aSketchbook))
            {
                string aName = Path.GetFileName(aDir);
                if (string.Equals(aName, iTarget, StringComparison.OrdinalIgnoreCase))
                    return (aDir, aName);
            }
            return (aCanonical, null);
        }

        /// <summary>
        /// 這個 persona 畫過誰 —— 根層未歸檔的對象名 ∪ 已有濃縮目錄的對象名。
        /// <para>⚠ 兩邊都要取：只看根層會漏掉「全部搬完的人」，只看目錄會漏掉「還沒濃縮過的人」。</para>
        /// </summary>
        public static List<string> Targets(string iLettersRoot, string iPersona)
        {
            var aRoot = new SCP_LettersRoot(iLettersRoot);
            string aSketchbook = SCP_LettersPaths.SketchbookDir(aRoot, iPersona);
            var aSeen = new List<string>();
            if (!Directory.Exists(aSketchbook)) return aSeen;

            foreach (string aFile in SafeFiles(aSketchbook, "*.md"))
            {
                string aTarget = TargetOfPortraitFile(Path.GetFileName(aFile));
                if (aTarget.Length > 0 && !ContainsIgnoreCase(aSeen, aTarget)) aSeen.Add(aTarget);
            }
            foreach (string aDir in SafeDirs(aSketchbook))
            {
                string aName = Path.GetFileName(aDir);
                if (aName.StartsWith("_", StringComparison.Ordinal)) continue;
                if (!ContainsIgnoreCase(aSeen, aName)) aSeen.Add(aName);
            }
            aSeen.Sort(StringComparer.OrdinalIgnoreCase);
            return aSeen;
        }

        /// <summary>組一個對象的視圖（純讀）。</summary>
        public static SCP_PortraitTargetView Build(string iLettersRoot, string iPersona, string iTarget)
        {
            var aView = new SCP_PortraitTargetView { Target = iTarget };
            var aRoot = new SCP_LettersRoot(iLettersRoot);

            (string aDir, string? aActual) = ResolveTargetDir(iLettersRoot, iPersona, iTarget);
            if (aActual != null && !string.Equals(aActual, iTarget, StringComparison.Ordinal))
                aView.DirCaseVariant = aActual;

            // ── 濃縮那半：版號一律解析整數取最大 ──────────────────────
            if (Directory.Exists(aDir))
            {
                var aRefs = new List<SCP_ConsolidatedRef>();
                foreach (string aFile in SafeFiles(aDir, "*.md"))
                {
                    int aVersion = VersionOf(Path.GetFileName(aFile));
                    if (aVersion <= 0) continue;      // 不是濃縮檔（例如手放的備註）⇒ 不當一版
                    aRefs.Add(new SCP_ConsolidatedRef
                    {
                        Path = aFile,
                        Target = iTarget,
                        Version = aVersion,
                    });
                }
                aView.VersionCount = aRefs.Count;
                if (aRefs.Count > 0)
                {
                    SCP_ConsolidatedRef aMax = aRefs[0];
                    foreach (SCP_ConsolidatedRef aRef in aRefs)
                        if (aRef.Version > aMax.Version) aMax = aRef;   // 整數比較，不是字串排序
                    FillHeader(aMax);
                    aView.Latest = aMax;
                }

                string aRawDir = Path.Combine(aDir, SCP_LettersPaths.SketchbookRawDirName);
                if (Directory.Exists(aRawDir)) aView.ArchivedRawCount = SafeFiles(aRawDir, "*.md").Count;
            }

            // ── 未歸檔那半：根層 `<ts>__about_<target>.md` ────────────
            string aSketchbook = SCP_LettersPaths.SketchbookDir(aRoot, iPersona);
            if (!Directory.Exists(aSketchbook))
            {
                aView.Problems.Add("沒有 sketchbook 目錄：" + aSketchbook);
                return aView;
            }
            foreach (string aFile in SafeFiles(aSketchbook, "*.md"))
            {
                string aName = Path.GetFileName(aFile);
                if (aName.StartsWith("_", StringComparison.Ordinal)) continue;   // 機械產物不是畫像
                if (!string.Equals(TargetOfPortraitFile(aName), iTarget, StringComparison.OrdinalIgnoreCase))
                    continue;
                aView.UnarchivedPaths.Add(aFile);
            }
            // 檔名前綴是 UTC 時間戳 ⇒ 檔名倒序即新到舊（同 python 端的取法）。
            aView.UnarchivedPaths.Sort((a, b) => string.CompareOrdinal(Path.GetFileName(b), Path.GetFileName(a)));
            return aView;
        }

        /// <summary>
        /// 把視圖排版成 md 行 —— **每一段自己說出定語**（哪個區間、幾幅、已歸檔還是未濃縮）。
        /// <para>🩸 少了定語，讀者分不出手上這句是三個月前的結論還是昨晚的觀察，
        /// 而那正是這片林的主病：四個真讀數共用一條錯定語，就串成一個假結論。</para>
        /// </summary>
        public static List<string> ViewLines(SCP_PortraitTargetView iView, bool iIncludeBodies)
        {
            var aOut = new List<string>();
            aOut.Add("### 🧑 " + iView.Target);

            if (iView.DirCaseVariant != null)
                aOut.Add("> ⚠ 濃縮目錄實際叫 `" + iView.DirCaseVariant + "`，與查詢的 `" + iView.Target
                         + "` 大小寫不同 —— 這台看起來是同一個目錄，git 會記成兩筆。");

            if (iView.Latest != null)
            {
                SCP_ConsolidatedRef aRef = iView.Latest;
                string aWhen = aRef.WakeRange.Length > 0 ? aRef.WakeRange : "區間不明（檔頭沒寫 wake_range）";
                string aBy = aRef.By.Length > 0 ? aRef.By + " " : "";
                aOut.Add("**⚓ 濃縮 v" + aRef.Version.ToString(CultureInfo.InvariantCulture)
                         + "**（" + aBy + aWhen + "・共 " + iView.VersionCount + " 版・已歸檔 "
                         + iView.ArchivedRawCount + " 幅）　`" + Path.GetFileName(aRef.Path) + "`");
                if (aRef.HeaderVersionMismatch != null)
                    aOut.Add("> ⚠ 檔頭寫 `version: " + aRef.HeaderVersionMismatch
                             + "` 與檔名不一致 —— **以檔名為準**（檔頭那格是派生值）。");
                if (iIncludeBodies)
                {
                    aOut.Add("");
                    aOut.AddRange(SCP_LetterText.DemoteHeadings(BodyLines(aRef.Path)));
                }
            }
            else
            {
                aOut.Add("**⚓ 濃縮**：尚無（這個對象還沒被濃縮過）");
            }

            aOut.Add("");
            if (iView.UnarchivedPaths.Count == 0)
            {
                aOut.Add("**🖼 未濃縮的近期畫像**：0 幅");
            }
            else
            {
                aOut.Add("**🖼 未濃縮的近期畫像**：" + iView.UnarchivedPaths.Count + " 幅（新 → 舊）");
                foreach (string aPath in iView.UnarchivedPaths)
                {
                    aOut.Add("- `" + Path.GetFileName(aPath) + "`");
                    if (!iIncludeBodies) continue;
                    aOut.Add("");
                    aOut.AddRange(SCP_LetterText.DemoteHeadings(BodyLines(aPath)));
                    aOut.Add("");
                }
            }

            foreach (string aProblem in iView.Problems)
                aOut.Add("> ⚠ " + aProblem);
            return aOut;
        }

        /// <summary>
        /// 見人 (c) 段的素材 —— **每人只取最新一幅**，最多 iCount 人、只看近 iDays 天。
        /// <para>⚠ 一個對象只要**有濃縮檔**就進得來，即使近 N 天一幅未歸檔畫像都沒有 ——
        /// 這一格就是「搬 raw 之後 §6.5 不會空」的實作點（TASK-0097 施工順序那條）。</para>
        /// <para>排序鍵：未歸檔那幅的時間；沒有未歸檔的人用濃縮檔的 `consolidated_at`
        /// （再沒有就墊空字串排到最後 —— 排最後不是排除）。</para>
        /// </summary>
        public static List<SCP_PortraitItem> LatestPerPerson(string iLettersRoot, string iPersona,
                                                             int iCount, int iDays)
        {
            var aItems = new List<SCP_PortraitItem>();
            DateTime aCutoff = DateTime.UtcNow.AddDays(-iDays);

            foreach (string aTarget in Targets(iLettersRoot, iPersona))
            {
                SCP_PortraitTargetView aView = Build(iLettersRoot, iPersona, aTarget);
                var aItem = new SCP_PortraitItem { About = aTarget, Consolidated = aView.Latest };

                foreach (string aPath in aView.UnarchivedPaths)      // 已是新 → 舊
                {
                    string aAt = SCP_LetterText.ReadFrontmatterField(aPath, "at");
                    if (aAt.Length == 0) aAt = TimestampOfFileName(Path.GetFileName(aPath));
                    // ⚠ 解析不出來就**保留**（同 python：寧可多列，不吞內容）——
                    //   時間讀不到不是「這幅太舊」的證據。
                    if (TryParseUtc(aAt, out DateTime aWhen) && aWhen < aCutoff) continue;
                    aItem.At = aAt;
                    aItem.Headline = SCP_LetterText.ReadFrontmatterField(aPath, "headline");
                    aItem.Path = aPath;
                    (aItem.Body, aItem.Private) = SplitPrivate(BodyLines(aPath));
                    break;                                            // 每人只要最新那幅
                }

                if (aItem.Path.Length == 0 && aItem.Consolidated == null) continue;   // 這人近期沒畫、也沒濃縮
                if (aItem.At.Length == 0 && aItem.Consolidated != null)
                    aItem.At = aItem.Consolidated.ConsolidatedAt;
                aItem.Weight = aView.UnarchivedPaths.Count + aView.ArchivedRawCount;
                aItems.Add(aItem);
            }

            // 新 → 舊；⚠ 第二鍵是**歸檔幅數**（份量重的先）——
            //   🩸 折人那天所有人的 `consolidated_at` 都是同一天，只用時間排會讓
            //     「只畫過一幅的人」跟「畫過十六幅的人」平手，然後 top-5 由插入順序決定
            //     ⇒ 畫面上留下的是最不重要的那幾位，而每一格讀數都正常。
            aItems.Sort((a, b) =>
            {
                int aCmp = string.CompareOrdinal(b.At, a.At);
                if (aCmp != 0) return aCmp;
                aCmp = b.Weight.CompareTo(a.Weight);
                return aCmp != 0 ? aCmp : string.Compare(a.About, b.About, StringComparison.OrdinalIgnoreCase);
            });
            if (aItems.Count > iCount) aItems.RemoveRange(iCount, aItems.Count - iCount);
            return aItems;
        }

        /// <summary>
        /// 剝掉畫像檔開頭連續的門面行（`# 🖼 &lt;about&gt; — by &lt;誰&gt;` 與重複的 `**headline**`）。
        /// <para>⚠ 只剝**開頭**，不掃全文 —— 內文中間同樣的字是作者寫的，那是內容不是雜訊。
        /// 剝太多比留一行重複更糟（會吞掉別人寫的東西）。</para>
        /// </summary>
        public static List<string> StripChrome(List<string> iLines, string iAbout, string iHeadline)
        {
            int aIndex = 0;
            while (aIndex < iLines.Count)
            {
                string aLine = iLines[aIndex].Trim();
                if (aLine.Length == 0) { aIndex++; continue; }
                if (aLine.StartsWith("# ", StringComparison.Ordinal)
                    && iAbout.Length > 0 && aLine.Contains(iAbout)) { aIndex++; continue; }
                if (iHeadline.Length > 0
                    && (aLine == "**" + iHeadline + "**" || aLine == iHeadline)) { aIndex++; continue; }
                break;
            }
            return iLines.GetRange(aIndex, iLines.Count - aIndex);
        }

        /// <summary>把畫像內文切成 (公開層, 私層)。沒有標記就是全公開。</summary>
        static (List<string> Public, List<string> Private) SplitPrivate(List<string> iLines)
        {
            for (int i = 0; i < iLines.Count; i++)
            {
                if (iLines[i].Trim() != PrivateMarker) continue;
                return (iLines.GetRange(0, i),
                        iLines.GetRange(i + 1, iLines.Count - i - 1));
            }
            return (iLines, new List<string>());
        }

        /// <summary>`<ts>__about_<target>.md` 的時戳部分（檔名前綴）。取不到回空字串。</summary>
        static string TimestampOfFileName(string iFileName)
        {
            int aMark = iFileName.IndexOf(SCP_LettersPaths.PortraitAboutInfix, StringComparison.Ordinal);
            return aMark <= 0 ? "" : iFileName.Substring(0, aMark);
        }

        /// <summary>吃兩種形狀：`2026-09-01T09:10:43.229815Z` 與緊湊的 `20260901T091043Z`。</summary>
        static bool TryParseUtc(string iValue, out DateTime oWhen)
        {
            oWhen = default;
            if (iValue.Length == 0) return false;
            if (DateTime.TryParse(iValue, CultureInfo.InvariantCulture,
                                  DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out oWhen))
                return true;
            // 🩸 緊湊 ISO 是活的（我自己的信件庫兩種格式並存）——
            //   只吃帶連字號那種的話，這一格會安靜地把所有緊湊格式當成「解析不出來」。
            return DateTime.TryParseExact(iValue, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                                          DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                          out oWhen);
        }

        // ── 解析小工具 ────────────────────────────────────────────

        /// <summary>
        /// 從濃縮檔名解析版號（`&lt;任何前綴&gt;_v&lt;數字&gt;.md`）。不是濃縮檔就回 0。
        /// <para>🩸 為什麼要解析而不是排序：`sort` 給的順序是 `v10 &lt; v2 &lt; v9`
        /// ⇒ 第 10 版之後「取最後一個」會安靜地拿到 v9，沿途沒有一格會紅。</para>
        /// </summary>
        public static int VersionOf(string iFileName)
        {
            if (!iFileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return 0;
            string aStem = iFileName.Substring(0, iFileName.Length - 3);
            int aMark = aStem.LastIndexOf("_v", StringComparison.Ordinal);
            if (aMark < 0) return 0;
            string aDigits = aStem.Substring(aMark + 2);
            if (aDigits.Length == 0) return 0;
            foreach (char aChar in aDigits) if (aChar < '0' || aChar > '9') return 0;
            return int.TryParse(aDigits, NumberStyles.None, CultureInfo.InvariantCulture, out int aValue)
                   ? aValue : 0;
        }

        /// <summary>從逐幅畫像檔名取對象名（`&lt;ts&gt;__about_&lt;target&gt;.md`）。不是那個形狀就回空字串。</summary>
        public static string TargetOfPortraitFile(string iFileName)
        {
            if (!iFileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return "";
            int aMark = iFileName.IndexOf(SCP_LettersPaths.PortraitAboutInfix, StringComparison.Ordinal);
            if (aMark < 0) return "";
            int aStart = aMark + SCP_LettersPaths.PortraitAboutInfix.Length;
            return iFileName.Substring(aStart, iFileName.Length - 3 - aStart);
        }

        static void FillHeader(SCP_ConsolidatedRef iRef)
        {
            iRef.WakeRange = SCP_LetterText.ReadFrontmatterField(iRef.Path, "wake_range");
            iRef.By = SCP_LetterText.ReadFrontmatterField(iRef.Path, "by");
            iRef.ConsolidatedAt = SCP_LetterText.ReadFrontmatterField(iRef.Path, "consolidated_at");

            // 檔頭的 version 只是給人看的派生值 —— 不一致時**不採用它**，但要說出來。
            // 🩸 行尾註解要先剝掉：寫入端刻意在那一格寫 `1   # 派生值，權威是檔名`，
            //   而第一版的比較拿整串去 parse ⇒ **每一個自己寫出來的檔都被判定不一致**。
            //   一個對每份正常檔案都喊狼來了的警語，等於把這格警語關掉（2026-09-01 fixture 實測）。
            string aHeader = SCP_LetterText.ReadFrontmatterField(iRef.Path, "version");
            int aComment = aHeader.IndexOf('#');
            if (aComment >= 0) aHeader = aHeader.Substring(0, aComment);
            aHeader = aHeader.Trim();
            if (aHeader.Length > 0
                && (!int.TryParse(aHeader, NumberStyles.None, CultureInfo.InvariantCulture, out int aValue)
                    || aValue != iRef.Version))
                iRef.HeaderVersionMismatch = aHeader;
        }

        static List<string> BodyLines(string iPath)
        {
            try
            {
                string aText = SCP_LetterText.StripFrontmatter(File.ReadAllText(iPath)).Trim();
                return new List<string>(aText.Replace("\r\n", "\n").Split('\n'));
            }
            catch (Exception e)
            {
                // 讀不到就把原因寫成內容 —— 一段空白會被讀成「這個人沒什麼可說的」。
                return new List<string> { "（讀不到：" + e.GetType().Name + ": " + e.Message + "）" };
            }
        }

        static bool ContainsIgnoreCase(List<string> iList, string iValue)
        {
            foreach (string aItem in iList)
                if (string.Equals(aItem, iValue, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static List<string> SafeFiles(string iDir, string iPattern)
        {
            try { return new List<string>(Directory.GetFiles(iDir, iPattern)); }
            catch (Exception) { return new List<string>(); }
        }

        static List<string> SafeDirs(string iDir)
        {
            try { return new List<string>(Directory.GetDirectories(iDir)); }
            catch (Exception) { return new List<string>(); }
        }
    }
}
