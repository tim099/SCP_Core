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

    public static class SCP_PortraitView
    {
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
            string aHeader = SCP_LetterText.ReadFrontmatterField(iRef.Path, "version");
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
