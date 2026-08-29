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
    }
}
