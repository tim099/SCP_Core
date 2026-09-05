// 區塊職責：**寫一封「給未來自己的信」** —— 組 frontmatter、落檔、同步 `_latest.md`。
// 物理意義：小歇（`cmd_rest`）與晚安收尾信寫的是**同一種檔**，差別只在 trigger 與落點資料夾。
//          在此之前這個組裝只活在 python（`awakening.py write_letter`）與 Editor 各一份 ——
//          ⛔ 而本檔存在的理由正是**不要有第三份**：
//          frontmatter 少一欄不會有任何一層報錯，只會讓讀信的人拿到預設值
//          （`SCP_ActivitySession.Raw` 那條血證的同一族）。
// 數值影響：一封信寫**兩個檔**（`rests/<ts>.md` 與 `_latest.md`，內容逐位元組相同）。
//          ⚠ `_latest.md` 是**內容副本不是連結**（見 `SCP_WakeLetters` 開頭）——
//          少寫它的症狀是「見樹指到上一封」，而那封信本身完全正常。
//
// ⚠ **與 python `awakening.py write_letter` 逐字同形**（同 `SCP_Cmd_Keys` 那條並存規矩）：
//   檔名 `yyyyMMddTHHmmssZ.md`、機器欄五個（type/actor/written_at/written_by_persona/trigger）、
//   作者自寫的 frontmatter 併進來、同名欄留 `<key>_as_written`、結尾補一個換行。
//   兩個寫入端要並存一段時間 ⇒ **形狀一旦分岔，是讀信那天才會發現**。
//
// ⛔ 本檔**不碰** wake_count／perturbation／offline／unlock —— 那是晚安的事。
//   小歇與晚安的唯一差別就在那幾格，而「共用寫信器」最容易順手把它們帶進來。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    /// <summary>一次寫信的讀數 —— ⛔ 不要只回 true/false（呼叫端要印出「寫到哪、寫了多少」）。</summary>
    public sealed class SCP_LetterWriteResult
    {
        /// <summary>信本體的路徑（`rests/<ts>.md`）。</summary>
        public string Path = "";

        /// <summary>見樹指標（`_latest.md`）的路徑。</summary>
        public string LatestPath = "";

        /// <summary>落檔內容的位元組數（回讀量的，不是組字串時算的）。</summary>
        public int Bytes;

        /// <summary>作者把換行寫成了字面 <c>\n</c> ⇒ 已修（要印出來，否則沒有人知道內容被動過）。</summary>
        public bool NormalizedEscapedNewlines;

        /// <summary>作者自己也寫了 frontmatter ⇒ 已併入（不是疊第二坨）。</summary>
        public int AuthorFrontmatterFields;
    }

    /// <summary>寫「給未來自己的信」的唯一組裝點（小歇／自寫信）。</summary>
    public static class SCP_LetterWriter
    {
        /// <summary>小歇片刻的 trigger 值 —— ⚠ 這個字串會進 frontmatter，改它＝改磁碟格式。</summary>
        public const string TriggerRest = "cmd_rest";

        static readonly UTF8Encoding s_Utf8NoBom = new UTF8Encoding(false);

        /// <summary>
        /// 寫一封自寫信到 <c>rests/</c> 並同步 <c>_latest.md</c>。
        /// <para>⛔ body 空白時**丟例外而不是寫一個空檔** —— 空的信比沒有信更糟：
        /// 它會讓 `_latest.md` 指到一封什麼都沒說的信，而醒來的人讀不出「這裡出過錯」。</para>
        /// </summary>
        public static SCP_LetterWriteResult WriteSelfLetter(string iLettersRoot, string iPersona,
                                                            string iActor, string iBody,
                                                            string iTrigger = TriggerRest,
                                                            DateTime? iNowUtc = null)
        {
            if (string.IsNullOrWhiteSpace(iPersona)) throw new ArgumentException("persona 是空的", nameof(iPersona));
            if (string.IsNullOrWhiteSpace(iBody)) throw new ArgumentException("信的內文是空的", nameof(iBody));

            DateTime aNow = iNowUtc ?? DateTime.UtcNow;
            var aRoot = new SCP_LettersRoot(iLettersRoot);
            string aPersonaDir = SCP_LettersPaths.PersonaDir(aRoot, iPersona);
            string aRestsDir = SCP_LettersPaths.RestsDir(aRoot, iPersona);
            Directory.CreateDirectory(aRestsDir);

            var aResult = new SCP_LetterWriteResult();
            string aBody = NormalizeEscapedNewlines(iBody, out bool aFixed);
            aResult.NormalizedEscapedNewlines = aFixed;

            // 機器欄位（provenance）—— 這五個以本函式為準，作者寫的同名欄留 `_as_written`。
            var aMachine = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("type", "letter_to_future_self"),
                new KeyValuePair<string, string>("actor", iActor ?? ""),
                new KeyValuePair<string, string>("written_at", IsoMillis(aNow)),
                new KeyValuePair<string, string>("written_by_persona", iPersona),
                new KeyValuePair<string, string>("trigger", string.IsNullOrWhiteSpace(iTrigger) ? TriggerRest : iTrigger),
            };
            aBody = SplitAuthorFrontmatter(aBody, aMachine, out List<string> aExtra);
            aResult.AuthorFrontmatterFields = aExtra.Count;

            var aText = new StringBuilder();
            aText.Append("---\n");
            foreach (var kv in aMachine) aText.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
            foreach (string aLine in aExtra) aText.Append(aLine).Append('\n');
            aText.Append("---\n\n").Append(aBody).Append('\n');
            // ⚠ 行尾：python 那支用文字模式寫（Windows 上 `\n` → `\r\n`）⇒ 這裡跟著平台走，
            //   否則同一個資料夾裡兩種行尾，功能全對而 git diff 整段翻動（`SCP_Cmd_Keys` 的同一課）。
            string aOut = aText.ToString().Replace("\n", Environment.NewLine);

            string aPath = aRestsDir + "/" + aNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture) + ".md";
            File.WriteAllText(aPath, aOut, s_Utf8NoBom);
            string aLatest = SCP_LettersPaths.LatestPointerPath(aRoot, iPersona);
            Directory.CreateDirectory(aPersonaDir);
            File.WriteAllText(aLatest, aOut, s_Utf8NoBom);

            aResult.Path = aPath;
            aResult.LatestPath = aLatest;
            // 回讀量位元組 —— 「我寫了」不是「它在裡面」。
            aResult.Bytes = File.Exists(aPath) ? (int)new FileInfo(aPath).Length : 0;
            return aResult;
        }

        /// <summary>
        /// 把作者自己寫的 frontmatter 從 body 拆出來（回去頭的 body，額外欄位放 <paramref name="oExtra"/>）。
        /// <para>📌 為什麼要拆：letter 模板教作者自己寫一份 frontmatter，而寫信器又包一層 ——
        /// 於是每封信開頭疊兩坨幾乎一樣的 header。**不是誰寫錯，是兩邊都以為自己負責那塊**
        /// （Tim 2026-07-31 抓到）。</para>
        /// <para>⚠ 作者寫了同名欄且值不同 ⇒ 機器版勝出，但留 `<key>_as_written`
        /// —— ⛔ 不靜默丟掉他寫的東西。</para>
        /// </summary>
        static string SplitAuthorFrontmatter(string iBody, List<KeyValuePair<string, string>> iMachine,
                                             out List<string> oExtra)
        {
            oExtra = new List<string>();
            string aS = iBody.TrimStart('\n');
            if (!aS.StartsWith("---", StringComparison.Ordinal)) return iBody;
            int aEnd = aS.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (aEnd < 0) return iBody;                 // 有頭無尾＝不是 frontmatter，⛔ 別亂切
            string aBlock = aS.Substring(3, aEnd - 3).Trim('\n');
            string aRest = aS.Substring(aEnd + 4).TrimStart('\n');
            foreach (string aRaw in aBlock.Split('\n'))
            {
                string aLine = aRaw.Trim();
                if (aLine.Length == 0 || aLine.StartsWith("#", StringComparison.Ordinal)) continue;
                int aColon = aRaw.IndexOf(':');
                if (aColon < 0) continue;
                string aKey = aRaw.Substring(0, aColon).Trim();
                string aVal = aRaw.Substring(aColon + 1).Trim();
                string? aMachineVal = null;
                for (int i = 0; i < iMachine.Count; ++i)
                    if (iMachine[i].Key == aKey) { aMachineVal = iMachine[i].Value; break; }
                if (aMachineVal != null)
                {
                    if (aVal.Length > 0 && aVal != aMachineVal) oExtra.Add(aKey + "_as_written: " + aVal);
                    continue;
                }
                oExtra.Add(aKey + ": " + aVal);
            }
            return aRest;
        }

        /// <summary>
        /// 修「作者本來要換行、傳進來卻是兩個字元 backslash+n」的內容。
        /// <para>⚠ 門檻與 python `escaped_newlines.normalize` 逐格相同：
        /// 字面序列 &gt;= 2 次**且**真換行 &lt;= 2 個才動手。</para>
        /// <para>🩸 為什麼不無腦替換：`summit/20260512T235620Z.md` 有 32 個真換行、1 個字面 \n，
        /// 而那個 \n 是**內文正在討論的符號本身**。兩個門檻就是為了讓它不被動到。</para>
        /// </summary>
        public static string NormalizeEscapedNewlines(string iText, out bool oFixed)
        {
            oFixed = false;
            if (string.IsNullOrEmpty(iText)) return iText;
            const int MIN_HITS = 2, MAX_REAL_LF = 2;
            int aHits = CountOccurrences(iText, "\\r\\n") + CountOccurrences(iText, "\\n");
            int aRealLf = CountOccurrences(iText, "\n");
            if (aHits < MIN_HITS || aRealLf > MAX_REAL_LF) return iText;
            // ⛔ 順序不能反：先換 `\n` 會把 `\r\n` 拆成孤立的 `\r`。
            oFixed = true;
            return iText.Replace("\\r\\n", "\n").Replace("\\n", "\n");
        }

        static int CountOccurrences(string iText, string iNeedle)
        {
            int aCount = 0, aAt = 0;
            while ((aAt = iText.IndexOf(iNeedle, aAt, StringComparison.Ordinal)) >= 0)
            {
                ++aCount;
                aAt += iNeedle.Length;
            }
            return aCount;
        }

        /// <summary>frontmatter 的 `written_at` 格式（毫秒 ＋ Z），與 python `utcnow_iso` 同形。</summary>
        static string IsoMillis(DateTime iUtc)
            => iUtc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    }
}
