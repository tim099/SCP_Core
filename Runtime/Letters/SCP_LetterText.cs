// 區塊職責：**信件文字的排版小工具** —— frontmatter 剝除、標題降階、區塊組裝、欄位讀取。
// 物理意義：這些檔是 md，開頭常有一到多層 `--- ... ---` 的機器欄位；把信 inline 進 brief 時
//           那些欄位不該再出現（讀信的人要的是信），而內文的 h1/h2 會跟 brief 自己的
//           §區塊標題撞層級 ⇒ 一律降一階。
// 數值影響：純字串處理，零 IO。
//
// 📌 這是 python 端 `wake_brief.py` 那四支小工具的 C# 對應
//    （_strip_frontmatter / _strip_all_frontmatter / _demote_headings / _section_lines）。
//    ⚠ 兩端是**同一份規格的兩個實作**，不是主從：任一端改行為，另一端要跟著改，
//      而不同步的症狀是**兩邊各生出一份長得都很正常的 brief**，沒有一層會喊。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace SCP.Core.Letters
{
    public static class SCP_LetterText
    {
        /// <summary>
        /// 把 `\r\n` / 單獨的 `\r` 一律正規化成 `\n`。**所有讀進來的信件文字都要先過這一關。**
        /// <para>🩸 2026-08-29 實測抓到（Template 對拍）：python 的 <c>read_text</c> 走 universal newlines，
        /// 讀進來就已經是 `\n`；C# 的 <c>File.ReadAllText</c> **原樣保留 CRLF**。
        /// 於是 <c>Trim('\n')</c> 對 `"\r\n\r\n### …"` 一個字元都剝不掉（開頭是 `\r` 不是 `\n`），
        /// 而 <c>Split('\n')</c> 又把殘留的 `\r` 切成兩行空白 ——
        /// 症狀是 §5 每封信的日期標題後**多兩行空白**，內容全對、格式悄悄歪掉。</para>
        /// <para>⚠ 這種差異不會有任何一層喊：兩邊都產出「一份看起來正常的 brief」。
        /// 抓到它的是**跟 python 逐行 diff**，不是讀 code。</para>
        /// </summary>
        public static string NormalizeNewlines(string iText)
            => (iText ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

        /// <summary>
        /// 去掉**一層** md 開頭的 frontmatter。沒有就原樣回傳。
        /// <para>對應 python `_strip_frontmatter`。</para>
        /// </summary>
        public static string StripFrontmatter(string iText)
        {
            string aText = NormalizeNewlines(iText);
            string aTrimmed = aText.TrimStart();
            if (!aTrimmed.StartsWith("---", StringComparison.Ordinal)) return aText;
            int aEnd = aTrimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (aEnd < 0) return aText;
            return aTrimmed.Substring(aEnd + 4).TrimStart('\n');
        }

        /// <summary>
        /// 剝掉**連續多層**的 frontmatter，回內文行陣列。
        /// <para>物理意義：letter 常有兩層 —— 寫信工具寫的外層（actor / written_at / trigger）
        /// 加上作者自己寫的內層。只剝一層的話，§5 開頭會杵著一坨機器欄位，
        /// 讀信的人要先跨過機器的自言自語。對應 python `_strip_all_frontmatter`。</para>
        /// </summary>
        public static List<string> StripAllFrontmatter(string iText)
        {
            string aText = NormalizeNewlines(iText);
            while (true)
            {
                string aTrimmed = aText.TrimStart();
                if (!aTrimmed.StartsWith("---", StringComparison.Ordinal)) break;
                int aEnd = aTrimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (aEnd < 0) break;
                aText = aTrimmed.Substring(aEnd + 4);
            }
            return new List<string>(aText.Trim('\n').Split('\n'));
        }

        /// <summary>把內文的 h1/h2 降一階 —— 避免 inline 後跟 brief 自己的 §區塊標題撞層級。</summary>
        public static List<string> DemoteHeadings(IEnumerable<string> iLines)
        {
            var aOut = new List<string>();
            foreach (string aLine in iLines)
            {
                aOut.Add(aLine.StartsWith("# ", StringComparison.Ordinal)
                         || aLine.StartsWith("## ", StringComparison.Ordinal)
                    ? "#" + aLine
                    : aLine);
            }
            return aOut;
        }

        /// <summary>組一個 `## 標題` 區塊（前後各留一行空白）。對應 python `_section_lines`。</summary>
        public static List<string> SectionLines(string iTitle, IEnumerable<string> iLines)
        {
            var aOut = new List<string> { "## " + iTitle, "" };
            aOut.AddRange(iLines);
            aOut.Add("");
            return aOut;
        }

        /// <summary>
        /// 從檔頭的 frontmatter 抓某一欄（written_at / type / span_wake…）。讀不到回空字串。
        /// <para>⚠ 只讀檔頭 1200 字元（同 python 端）—— 那是為了不把一封長信整份讀進來只為了一欄。
        /// 代價是欄位若排在 1200 字元之後就抓不到，而**抓不到與沒有這一欄同形**。
        /// 今天所有寫入端都把 frontmatter 放在最前面，所以這個代價還沒有實害。</para>
        /// </summary>
        public static string ReadFrontmatterField(string iPath, string iField)
        {
            string aHead;
            try
            {
                using var aReader = new StreamReader(iPath);
                var aBuf = new char[1200];
                int aRead = aReader.Read(aBuf, 0, aBuf.Length);
                aHead = new string(aBuf, 0, Math.Max(0, aRead));
            }
            catch (Exception) { return ""; }

            string aPrefix = iField + ":";
            foreach (string aLine in aHead.Split('\n'))
            {
                string aTrimmed = aLine.TrimEnd('\r');
                if (!aTrimmed.StartsWith(aPrefix, StringComparison.Ordinal)) continue;
                return aTrimmed.Substring(aPrefix.Length).Trim();
            }
            return "";
        }

        /// <summary>信的**內文**行數（剝掉 frontmatter、不算空行）。</summary>
        /// <remarks>
        /// 數值影響：用內文而非整檔行數 —— frontmatter 固定佔 5-7 行，
        /// 拿整檔量會讓「一句話的信」看起來有 9 行而躲過門檻。量的是給人讀的部分。
        /// </remarks>
        public static int BodyLineCount(string iPath)
        {
            try
            {
                int aCount = 0;
                foreach (string aLine in StripAllFrontmatter(File.ReadAllText(iPath)))
                {
                    if (aLine.Trim().Length > 0) aCount++;
                }
                return aCount;
            }
            catch (Exception) { return 0; }
        }

        // ===========================================================
        // 區塊職責：信裡的**現地宣告**（TASK-0418）—— 作者在內文抄的那行「📍 現地：區域 X ／ 專案 P」，
        //          以及作者自寫 frontmatter 被機器值蓋掉而留下的 `region_as_written`／`project_as_written`。
        // 物理意義：現地是判斷「信裡的 seq／座標屬於哪一軸」的那一格。機器值（frontmatter 的 region／project）
        //          是寫信當下量的；作者手抄的那行可能寫錯（calli wake#63：內文寫 BTC／Bar，實際寫在 Florin／LY）。
        //          🩸 寫入端記了 `_as_written` 留痕，但**沒有人會被叫到**：寫的人不知道寫錯，
        //             讀的人在 brief 上看到兩個並排、互相矛盾的現地。
        // 數值影響：純字串處理，零 IO。
        // ===========================================================
        static readonly Regex s_BodyLocale = new Regex(
            @"^\s*📍\s*現地[：:]\s*區域\s*`?([^`／/\s]+)`?\s*[／/]\s*專案\s*`?([^`\s]+)`?",
            RegexOptions.CultureInvariant);

        /// <summary>這一行是不是作者手抄的現地宣告（`📍 現地：區域 X ／ 專案 P`，反引號可有可無）。</summary>
        public static bool TryParseBodyLocale(string iLine, out string oRegion, out string oProject)
        {
            Match aM = s_BodyLocale.Match(iLine ?? "");
            oRegion = aM.Success ? aM.Groups[1].Value : "";
            oProject = aM.Success ? aM.Groups[2].Value : "";
            return aM.Success;
        }

        /// <summary>從整份信的文字讀**開頭連續幾層** frontmatter 的某一欄（第一個命中的為準）。讀不到回空字串。</summary>
        public static string FrontmatterFieldOfText(string iText, string iField)
        {
            string aText = NormalizeNewlines(iText);
            string aPrefix = iField + ":";
            while (true)
            {
                string aTrimmed = aText.TrimStart();
                if (!aTrimmed.StartsWith("---", StringComparison.Ordinal)) return "";
                int aEnd = aTrimmed.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (aEnd < 0) return "";
                foreach (string aLine in aTrimmed.Substring(3, aEnd - 3).Split('\n'))
                    if (aLine.StartsWith(aPrefix, StringComparison.Ordinal))
                        return aLine.Substring(aPrefix.Length).Trim();
                aText = aTrimmed.Substring(aEnd + 4);
            }
        }

        /// <summary>
        /// 一封信裡**作者寫的現地**與**寫信當下的機器值**對不上的地方（一項一行，給寫入端印在回傳檔）。
        /// <para>機器值讀不到或是 `unstated` ⇒ 回空（沒有基準就不判，⛔ 不拿作者寫的去當基準）。</para>
        /// </summary>
        public static List<string> LocaleConflicts(string iLetterText)
        {
            var aOut = new List<string>();
            string aRegion = FrontmatterFieldOfText(iLetterText, "region");
            string aProject = FrontmatterFieldOfText(iLetterText, "project");
            if (aRegion.Length == 0 || aRegion == "unstated") return aOut;

            string aRegionW = FrontmatterFieldOfText(iLetterText, "region_as_written");
            string aProjectW = FrontmatterFieldOfText(iLetterText, "project_as_written");
            if (aRegionW.Length > 0)
                aOut.Add("frontmatter 妳寫 `region: " + aRegionW + "`，寫信當下的機器值是 `" + aRegion + "`（機器值勝出，妳寫的留在 `region_as_written`）");
            if (aProjectW.Length > 0)
                aOut.Add("frontmatter 妳寫 `project: " + aProjectW + "`，寫信當下的機器值是 `" + aProject + "`（機器值勝出，妳寫的留在 `project_as_written`）");

            List<string> aBody = StripAllFrontmatter(iLetterText);
            for (int i = 0; i < aBody.Count; i++)
            {
                if (!TryParseBodyLocale(aBody[i], out string aR, out string aP)) continue;
                if (aR == aRegion && (aProject.Length == 0 || aP == aProject)) continue;
                aOut.Add("內文第 " + (i + 1) + " 行寫「📍 現地：區域 `" + aR + "` ／ 專案 `" + aP
                         + "`」，但這封信實際寫在 `" + aRegion + "` ／ `" + aProject + "`");
            }
            return aOut;
        }
    }
}
