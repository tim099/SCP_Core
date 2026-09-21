// 區塊職責：章的**落檔那一半** —— 決定書 slug／章號、兩道守衛、寫檔、回讀驗證、台帳回填。
// 物理意義：這是 `export-watch` 唯一會**寫東西**的地方（產物 `Books/<book>/NNN.txt` ＋ 台帳一筆 export 事件）。
//           排版在 `SCP_WatchExport.BuildChapter`（零寫入）—— 兩半刻意切開，
//           所以驗收可以在 clean-room 跑完排版而**不碰任何真產物**。
// 數值影響：寫一個章檔；台帳 append N 筆（N＝涵蓋到的場次數）。**永不改既有台帳行。**
//
// ⚠ **移植自 `library.py::cmd_export_watch` 的後半（TASK-0143 ⑤ 第四刀）**，
//   逐行對照 @summit 2026-09-06 `84617ee8` 落地的那一版 —— 移植不改行為，血證註解一條不刪。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Json;

namespace SCP.Core.Watch
{
    /// <summary>落檔結果。<see cref="Error"/> 非空 ＝ **什麼都沒寫**（守衛擋下或排版失敗）。</summary>
    /// <summary>`RetitleChapter` 的結果 —— ⚠ `Ok` 為 false 時 `Error` 一定有值。</summary>
    public sealed class SCP_WatchRetitleResult
    {
        public bool Ok;
        public string Error = "";
        public string Book = "";
        public string Chapter = "";
        public string Path = "";
        public string OldTitle = "";
        public string NewTitle = "";
        /// <summary>回讀驗證：除標題那一行外逐位元組相同（**讀數，不是宣稱**）。</summary>
        public bool BodyUnchanged;
        /// <summary>台帳對齊了幾個場次（0 ＝ 表頭沒有場次欄，或台帳不存在）。</summary>
        public int LedgerSessions;
    }

    public sealed class SCP_WatchWriteResult
    {
        public string Error = "";
        public string OutPath = "";
        public string Book = "";
        public string Chapter = "";
        /// <summary>
        /// 這一次寫出去的是第幾版：**1 ＝ 正本 `NNN.txt`**；≥2 ＝ `NNN_vN.txt`（TASK-0152）。
        /// <para>⚠ 讀的人要分得出來：`OutPath` 是**真的寫到哪**，而 `Chapter` 仍然是章號
        /// —— 兩者在 v1 時同形，v2 之後就不是了。</para>
        /// </summary>
        public int Version = 1;
        /// <summary>v≥2 時：被讓開的那個正本路徑（**它一個位元組都沒被動過**）。</summary>
        public string BasePath = "";
        /// <summary>回讀落地檔案量到的（⛔ 不是組字串時算的）。</summary>
        public int BackLines;
        public int BackChars;
        public int BackEntries;
        /// <summary>台帳實際 append 了哪幾場。空＝沒有對得上的場次（**「已匯出」在台帳上仍與未匯出同形**）。</summary>
        public List<string> LedgerAppended = new List<string>();
        public SCP_WatchChapter? Chapterized;
    }

    public static class SCP_WatchWriter
    {
        public static string BooksRoot(string iDataRoot)
            => Path.Combine(iDataRoot, "Books").Replace('\\', '/');

        public static string MediaRoot(string iDataRoot, string iMediaId)
            => Path.Combine(iDataRoot, "BookNotes", "Library", "media", iMediaId).Replace('\\', '/');

        static readonly Regex s_ChapterFile = new Regex(@"^\d{3}$", RegexOptions.Compiled);
        static readonly Regex s_SeqRangeCell = new Regex(@"seq 區間 \|([^|]+)\|", RegexOptions.Compiled);
        static readonly Regex s_SeqPair = new Regex(@"(\d+)\s*[–\-]\s*(\d+)", RegexOptions.Compiled);
        /// <summary>第一則實錄的 `### [seq N] …` 與它底下的本文（TASK-0217 身分核對用）。
        /// <para>🩸 TASK-0254：第一版寫死 LF（`\n\n`），而章檔在 Windows 上是 **CRLF** 落盤 ⇒
        /// `[^\n]*` 吃掉 CR 之後下一個位元組還是 CR，**45/45 有實錄段的章檔全部匹配失敗**。
        /// 而失敗的樣子是守衛喊「找不到任何一則實錄」並改口指控跨區 seq ——
        /// **一個行尾字元讓錯誤訊息指向了另一個成因**。⇒ 行尾一律 `\r?\n`，⛔ 別再假設 LF。</para></summary>
        static readonly Regex s_FirstEntry = new Regex(@"^### \[seq (\d+)\][^\r\n]*\r?\n\r?\n(.{0,200})",
            RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.Singleline);

        /// <summary>
        /// 決定書 slug。
        /// <para>🩸 2026-08-17 實跑踩到：預設寫成 <c>watch-&lt;media_id&gt;</c>，而既有那本是
        /// <c>watch-&lt;work_id&gt;</c>（media `anim-apocalypse-hotel` 的 work 是 `apocalypse-hotel`）
        /// ⇒ **同一部片長出兩本書，而兩邊都能寫、都不報錯**。
        /// ⇒ 先看既有目錄，再決定；兩本都不存在時才用 media 命名。</para>
        /// <para>⛔ 兩本都存在 ⇒ **不猜**，要人用 book 指定。</para>
        /// </summary>
        public static string ResolveBook(string iDataRoot, string iMedia, string? iBook, out string oError)
        {
            oError = "";
            if (!string.IsNullOrWhiteSpace(iBook)) return iBook!.Trim();
            var aCands = new List<string> { "watch-" + iMedia };
            try
            {
                string aMj = Path.Combine(MediaRoot(iDataRoot, iMedia), "media.json");
                if (File.Exists(aMj))
                {
                    string aWid = SCP_JsonData.Parse(File.ReadAllText(aMj, Encoding.UTF8)).GetString("work_id", "");
                    if (aWid.Length > 0) aCands.Add("watch-" + aWid);
                }
            }
            catch { /* 讀不動 ⇒ 只用 media 那個候選（python 同一條退路） */ }
            var aExisting = new List<string>();
            foreach (string c in aCands) if (Directory.Exists(Path.Combine(BooksRoot(iDataRoot), c))) aExisting.Add(c);
            if (aExisting.Count > 1)
            {
                oError = "❌ 同一部片有多本觀影實錄：" + string.Join("、", aExisting)
                         + " —— **不猜**，用 book 指定要寫哪一本";
                return "";
            }
            return aExisting.Count == 1 ? aExisting[0] : aCands[aCands.Count - 1];
        }

        /// <summary>
        /// 既有章檔的「身分核對」—— **這一章的 seq，現在還指著同一批訊息嗎？**（TASK-0217）
        /// <para>🩸 為什麼要這一格：章檔表頭只記 `seq A – B`，**沒有記是哪一區的 seq**，
        /// 而酒館 seq 會隨區域分岔。2026-09-15 逐章量：有實錄段的 39 章裡
        /// **本文對得上 0 章**、對不上 27 章、那個 seq 現在根本不存在 12 章。
        /// ⇒ 重出任一章都會產出一份**格式完整、seq 連續、回讀驗證全過**而內容是別人工作公告的東西。</para>
        /// <para>⛔ 比的是**本文**不是時間：第一版拿章裡的 `HH:mm` 去比訊息 `ts` 的 UTC 時分 ⇒ 40/40 全紅，
        /// 其中還有「同一個人差兩分鐘」的格 —— 那是尺壞了，不是資料全錯。</para>
        /// <returns>對得上 ＝ true；false 時 <paramref name="oWhy"/> 一定有話說（空字串是 bug）。</returns>
        /// </summary>
        public static bool VerifyChapterIdentity(string iDataRoot, string iRoom, string iChapterPath,
                                                 out string oWhy)
        {
            oWhy = "";
            string aText;
            try { aText = File.ReadAllText(iChapterPath, Encoding.UTF8); }
            catch (Exception e) { oWhy = $"讀不動既有章檔（{e.GetType().Name}: {e.Message}）"; return false; }

            Match m = s_FirstEntry.Match(aText);
            if (!m.Success)
            {
                // ⚠ 沒有實錄段 ⇒ **沒有可比的東西**，不是「對得上」。
                //   ⛔ 這裡回 true 的話，「無從判斷」就跟「驗過了」同形。
                oWhy = "既有章檔裡找不到任何一則實錄（`### [seq N]`）⇒ **無從核對身分**";
                return false;
            }
            long aSeq = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            string aWas = Squash(m.Groups[2].Value);

            var aWarn = new List<string>();
            List<(long Seq, SCP_JsonData? Msg, string File)> aNow =
                SCP_WatchExport.IterMessages(iDataRoot, iRoom, aSeq, aSeq, aWarn);
            if (aNow.Count == 0 || aNow[0].Msg == null)
            {
                oWhy = $"章檔第一則是 seq **{aSeq}**，而這個 seq 在 `{iRoom}` 現在的訊息庫裡**不存在**"
                       + "　⇒ 那些號碼多半屬於**另一條 seq 軸**（酒館 seq 隨區域分岔），"
                       + "⛔ 不是「訊息被刪了」";
                return false;
            }
            string aIs = Squash(aNow[0].Msg!.GetString("body", ""));
            int aN = Math.Min(24, Math.Min(aWas.Length, aIs.Length));
            if (aN > 0 && string.CompareOrdinal(aWas, 0, aIs, 0, aN) == 0) return true;

            oWhy = $"章檔第一則是 seq **{aSeq}**，而那個號碼現在指著**別的訊息**："
                   + $"　章檔說「{Clip(aWas)}…」／現在是「{Clip(aIs)}…」"
                   + "　⇒ 多半是跨區（酒館 seq 隨區域分岔）或 seq 重新編號過";
            return false;
        }

        /// <summary>把空白壓掉再比 —— 行尾與縮排不是內容。</summary>
        static string Squash(string iText)
        {
            var sb = new StringBuilder();
            foreach (char c in iText)
            {
                if (char.IsWhiteSpace(c)) continue;
                sb.Append(c);
                if (sb.Length >= 64) break;
            }
            return sb.ToString();
        }

        static string Clip(string iText) => iText.Length <= 22 ? iText : iText.Substring(0, 22);


        /// <summary>
        /// 第 N 版的路徑（<c>NNN_vN.txt</c>，N≥2）。TASK-0152：既有章**永不覆蓋**，重出往這裡放。
        /// <para>⚠ 檔名刻意**不符合** <c>^\d{3}$</c> ⇒ 章的列舉（`???.txt`）看不到它。**版本不是章。**</para>
        /// </summary>
        public static string VersionPath(string iBookDir, string iChapter, int iVersion)
            => iBookDir.TrimEnd('/') + "/" + iChapter + "_v" + iVersion.ToString(CultureInfo.InvariantCulture) + ".txt";

        /// <summary>章號：給了就三位數正規化；沒給就取現有 <c>NNN.txt</c> 的 max+1（沒有就 001）。</summary>
        public static string ResolveChapter(string iBookDir, string? iChapter)
        {
            if (!string.IsNullOrWhiteSpace(iChapter)
                && int.TryParse(iChapter!.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
                return n.ToString("000", CultureInfo.InvariantCulture);
            int aMax = 0;
            if (Directory.Exists(iBookDir))
                foreach (string f in Directory.GetFiles(iBookDir, "???.txt"))
                {
                    string aStem = Path.GetFileNameWithoutExtension(f);
                    if (s_ChapterFile.IsMatch(aStem) && int.TryParse(aStem, out int v) && v > aMax) aMax = v;
                }
            return (aMax + 1).ToString("000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 擋「同一段 seq 被兩章各自收錄」。
        /// <para>🩸 2026-08-17 首日就發生：basecamp 匯出 005（15777-15817）、gura 十分鐘後匯出 006
        /// （15780-15816）—— 同一話兩章、區間重疊，而**兩邊都成功、都不報錯**。
        /// 章 ≠ 場，但**一話也不該有兩章**。</para>
        /// <para>物理意義：既有章的表頭本來就寫著自己的 seq 區間（機械產物、可回讀）
        /// ⇒ 拿它當事實源，⛔ 不另建索引。</para>
        /// </summary>
        public static List<(string File, long Lo, long Hi)> FindOverlaps(
            string iBookDir, string iOutName, IReadOnlyList<SCP_SeqRange> iRanges)
        {
            var aClash = new List<(string, long, long)>();
            if (!Directory.Exists(iBookDir)) return aClash;
            var aPrev = new List<string>(Directory.GetFiles(iBookDir, "???.txt"));
            aPrev.Sort(StringComparer.Ordinal);
            foreach (string aP in aPrev)
            {
                string aName = Path.GetFileName(aP);
                if (string.Equals(aName, iOutName, StringComparison.Ordinal)) continue;
                if (!s_ChapterFile.IsMatch(Path.GetFileNameWithoutExtension(aName))) continue;
                string aHead;
                try
                {
                    string aAll = File.ReadAllText(aP, Encoding.UTF8);
                    aHead = aAll.Length > 2000 ? aAll.Substring(0, 2000) : aAll;
                }
                catch { continue; }
                Match m = s_SeqRangeCell.Match(aHead);
                if (!m.Success) continue;
                foreach (Match seg in s_SeqPair.Matches(m.Groups[1].Value))
                {
                    long a = long.Parse(seg.Groups[1].Value, CultureInfo.InvariantCulture);
                    long b = long.Parse(seg.Groups[2].Value, CultureInfo.InvariantCulture);
                    foreach (SCP_SeqRange r in iRanges)
                        if (r.Lo <= b && a <= r.Hi) aClash.Add((aName, a, b));
                }
            }
            return aClash;
        }

        /// <summary>
        /// 排版 ＋ 落檔 ＋ 回讀 ＋ 台帳回填。**章的正文只有這一支寫得出來。**
        /// <para>⚠ 本檔另有一個寫入端 <see cref="RetitleChapter"/>（TASK-0255）——
        /// 它的射程**只有表頭那一行標題**，逐位元組驗證其餘內容不變。
        /// ⛔ 那不是「又一個匯出入口」，它不產生內容、不碰台帳以外的東西。</para>
        /// </summary>
        /// <param name="iForce">既有章檔存在時是否覆寫。⛔ 預設不覆寫。</param>
        /// <param name="iAllowOverlap">seq 區間與既有章重疊時是否放行（要人明說要並存）。</param>
        public static SCP_WatchWriteResult WriteChapter(
            string iDataRoot, string iRoom, IReadOnlyList<SCP_SeqRange> iRanges,
            string iMedia, string? iBook, string? iChapter,
            string? iTitle, string? iSubtitle, string? iWorkTitle, string? iSessions, string? iNote,
            string? iOnlyPersonas, string? iExcludeTags,
            bool iForce, bool iAllowOverlap, bool iAllowZeroStripped, List<string> oLines)
        {
            var aOut = new SCP_WatchWriteResult();

            string aBook = ResolveBook(iDataRoot, iMedia, iBook, out string aBookErr);
            if (aBookErr.Length > 0) { aOut.Error = aBookErr; return aOut; }
            aOut.Book = aBook;
            string aBdir = Path.Combine(BooksRoot(iDataRoot), aBook).Replace('\\', '/');
            string aChapter = ResolveChapter(aBdir, iChapter);
            aOut.Chapter = aChapter;
            string aOutPath = Path.Combine(aBdir, aChapter + ".txt").Replace('\\', '/');
            aOut.OutPath = aOutPath;

            // ── TASK-0152（Tim 2026-09-15 拍板）：**無論如何都不覆蓋既有章。** ──
            // 🩸 為什麼：含人工修訂的章只靠正文裡一行字保護自己，而收工自動匯出天生帶
            //   `iForce: true` ⇒ 一次重出，手改與那行警告**一起消失**，之後連「被改過」都讀不出來。
            //   ⇒ 修法不是把警告寫得更好，是**把覆寫這個選項拿掉**。
            // 形狀：既有 `NNN.txt` 一個位元組都不動，新的那份出成 `NNN_v2.txt`（再來 _v3…）。
            // ⚠ `_vN` **不符合 `^\d{3}$`** ⇒ 它不會被 `ResolveChapter`／`FindOverlaps` 當成新的一章，
            //   也不會把章號往前推。⛔ 這是刻意的：版本不是章。
            // 📌 `iForce` 的語意因此收窄（⛔ 它不再是「覆寫」）：
            //   · `false` ⇒ 既有章存在就**拒絕**（沒說要重出的人維持原行為，錯誤訊息照舊）
            //   · `true`  ⇒ 允許重出，而重出**落在新版本上**
            //   ⚠ 這個參數名現在比它的行為大 —— 改名要跨兩個 repo 的呼叫端，留一筆待辦，⛔ 不在本次順手改。
            if (File.Exists(aOutPath))
            {
                // 🔴 TASK-0217：重出之前先問**這一章的 seq 現在還指著同一批訊息嗎**。
                //   對不上就整個拒絕 —— ⛔ 連 v2 都不出：一份內容全錯的 v2 比沒有更糟，
                //   它日後會被當成「另一版」而不是「另一批人的訊息」。
                //   ⚠ 這一格要排在 `iForce` 判斷**之前**：身分不對時，「有沒有說要重出」根本不該被問到。
                if (!VerifyChapterIdentity(iDataRoot, iRoom, aOutPath, out string aIdWhy))
                {
                    aOut.Error = "❌ **拒絕重出**：" + aIdWhy
                                 + "。⛔ 本次一個檔都沒有生出來（含 `_vN`）。"
                                 + "　⇒ 要重出這一章，得先把它的 seq 換成**本區**的號碼（或在本區重新匯出成新的一章）。";
                    return aOut;
                }

                if (!iForce)
                {
                    aOut.Error = $"❌ {aOutPath} 已存在 —— **拒絕重出**（⛔ 本層永不覆蓋）。"
                                 + "要重出請顯式允許（force）⇒ 會另外出一份 `NNN_v2.txt`，既有那份不動。";
                    return aOut;
                }
                string aBase = aOutPath;
                int aV = 2;
                while (File.Exists(VersionPath(aBdir, aChapter, aV))) aV++;
                aOutPath = VersionPath(aBdir, aChapter, aV);
                aOut.Version = aV;
                aOut.BasePath = aBase;
                aOut.OutPath = aOutPath;
                oLines.Add($"📄 `{Path.GetFileName(aBase)}` 已存在 ⇒ **不覆蓋**，本次出成 "
                           + $"`{Path.GetFileName(aOutPath)}`（第 {aV} 版，TASK-0152）");
                oLines.Add("   ⚠ 正本仍是 `" + Path.GetFileName(aBase) + "` —— 讀的人預設讀到的還是它；"
                           + "要讓新版取代它是**人的決定**，⛔ 機器不替你決定");
            }

            var aClash = FindOverlaps(aBdir, aChapter + ".txt", iRanges);
            if (aClash.Count > 0 && !iAllowOverlap)
            {
                var aSb = new StringBuilder("❌ seq 區間與既有章重疊 —— **一話不該有兩章**：");
                foreach (var c in aClash) aSb.Append($"\n   {c.File} 已收錄 {c.Lo}–{c.Hi}");
                aSb.Append("\n   ⇒ 併成一章（把兩邊區間一起給、覆寫那一章）或 allow_overlap 明說要並存。");
                aOut.Error = aSb.ToString();
                return aOut;
            }

            SCP_WatchChapter aCh = SCP_WatchExport.BuildChapter(
                iDataRoot, iRoom, iRanges, aChapter, iMedia, iTitle, iSubtitle, iWorkTitle,
                iSessions, iNote, iOnlyPersonas, iExcludeTags, iAllowZeroStripped, oLines);
            aOut.Chapterized = aCh;
            if (aCh.Error.Length > 0) { aOut.Error = aCh.Error; return aOut; }

            Directory.CreateDirectory(aBdir);
            // 🔴 最後一道防線：走到這裡 `aOutPath` **必須**不存在。
            //   ⛔ 不是為了防上面那段（它就在二十行前），是為了防**未來有人在中間插一條路** ——
            //   而那種插入不會有任何一層喊，症狀是一份手改安靜地消失。
            if (File.Exists(aOutPath))
            {
                aOut.Error = $"❌ 內部錯誤：走到落檔時 {aOutPath} 仍然存在 ⇒ **拒絕寫入**"
                             + "（TASK-0152 的不變式是「永不覆蓋」）。這是程式錯誤，不是使用者錯誤。";
                return aOut;
            }
            // ⚠ 行尾：python 那支用**文字模式**寫（`write_text` 的 newline=None
            //   ⇒ Windows 上 `\n` 被翻成 `\r\n`）⇒ 磁碟上既有的章**全部是 CRLF**。
            //   這裡跟著平台走，否則同一個資料夾裡兩種行尾，內容全對而 git diff 整段翻動
            //   （`SCP_LetterWriter` 檔頭同一課；`SCP_Cmd_Keys` 也踩過）。
            // 🩸 而這一格是**實跑抓到的，不是我看 code 看出來的**：selftest 兩邊都先
            //   `Replace("\r\n","\n")` 才比 —— **我把唯一的差異正規化掉了**，於是它綠著。
            //   ⇒ 「一樣／不一樣」的問題只能用**位元組**回答。
            File.WriteAllText(aOutPath, aCh.Text.Replace("\n", Environment.NewLine),
                              new UTF8Encoding(false));

            // 印 ✓ 不算數 —— **回讀落地的檔案**再報數字。
            string aBack = File.ReadAllText(aOutPath, Encoding.UTF8);
            aOut.BackChars = aBack.Length;
            aOut.BackLines = aBack.Split('\n').Length;
            int aEntries = 0, aAt = 0;
            while ((aAt = aBack.IndexOf("### [seq ", aAt, StringComparison.Ordinal)) >= 0)
            { ++aEntries; aAt += 9; }
            aOut.BackEntries = aEntries;
            oLines.Add($"✅ 匯出 {aOutPath}");
            oLines.Add($"   收錄 {aCh.Kept.Count} 筆 / 未收錄 {aCh.Excluded.Count} 筆 / "
                       + $"清掉附掛 {aCh.Stripped} 處 / seq {aCh.Span}");
            oLines.Add($"   回讀驗證：{aOut.BackLines} 行、{aOut.BackChars} 字元、實錄段 {aEntries} 則");
            if (aCh.Excluded.Count > 0)
                oLines.Add("   ⚠ 未收錄清單已寫進章內 <details>，**不靜默截斷**");

            // ── BUG-9：台帳 append 一筆 `record_type=export`（append-only，**不就地改行**）──
            //   ⛔ 場次列自己的 `exported_chapter` 欄從來不會被填 —— 它從建立到永遠都是 ""，
            //      所以「這一場進了哪一章」只能靠這些 export 紀錄回答，
            //      讀那個欄位一定得到「還沒進章」。
            //   對象＝明列的場次 ∪ **區間被本章完全涵蓋**的場次（陪同場常常沒被列進 sessions）。
            var aWant = new List<string>();
            foreach (string s in (iSessions ?? "").Split(','))
                if (s.Trim().Length > 0) aWant.Add(s.Trim());
            try
            {
                var aWarn = new List<string>();
                Dictionary<string, SCP_WatchSessionRow> aState = SCP_WatchLedger.SessionsLogState(iDataRoot, aWarn);
                var aOrdered = new List<KeyValuePair<string, SCP_WatchSessionRow>>(aState);
                aOrdered.Sort((x, y) => x.Value.Order.CompareTo(y.Value.Order));   // ⚠ 見 Order 的血證
                foreach (var kv in aOrdered)
                {
                    long lo0 = kv.Value.GetLong("start_seq"), hi0 = kv.Value.GetLong("end_seq");
                    if (lo0 == 0 || hi0 == 0) continue;
                    bool aCovered = false;
                    foreach (SCP_SeqRange r in iRanges) if (r.Lo <= lo0 && hi0 <= r.Hi) { aCovered = true; break; }
                    if (aCovered && !aWant.Contains(kv.Key)) aWant.Add(kv.Key);
                }
                aOut.LedgerAppended = SCP_WatchLedger.AppendExportEvents(
                    iDataRoot, aWant, aChapter, aBook, iTitle ?? "", iWorkTitle ?? "", oLines);
                oLines.AddRange(aWarn);
                if (aOut.LedgerAppended.Count > 0)
                {
                    oLines.Add($"   ↳ 台帳 append {aOut.LedgerAppended.Count} 筆 export 紀錄"
                               + $"（chapter={aChapter}）：{string.Join(", ", aOut.LedgerAppended)}");
                    oLines.Add("     ⚠ 場次列的 exported_chapter 欄**不會被填**（append-only）"
                               + "—— 查章號請掃 export 紀錄");
                }
                else
                    oLines.Add("   ⚠ 台帳沒有 append 任何 export 紀錄（沒有對得上的場次）"
                               + "—— **「已匯出」在台帳上仍與「未匯出」同形**");
            }
            catch (Exception e)
            {
                // ⚠ 章**已經落地**了 ⇒ 台帳失敗不回頭、不刪檔，只出聲。
                //   兩本帳分開結算：產物成了、回填沒成 —— ⛔ 不把它報成整件事失敗。
                oLines.Add($"   ⚠ 台帳 append export 紀錄失敗（**章已落地，不回頭**）：{e.Message}");
            }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：補章名 —— **只改章檔表頭那一行標題**，不重出、不動正文。
        // 物理意義：TASK-0255。在此之前補名這條路沒有出口：
        //   `op=untitled` 的指引寫「帶 force=1 重出」，而 TASK-0152 之後 force 的語意是
        //   「重出落在新版本上」⇒ 正本仍叫哨兵、旁邊多一份有名字的 `_v2`，
        //   而 `_vN` 不符合 `^\d{3}$` ⇒ 章的列舉看不到它 ⇒ 下次照樣報同樣的章數。
        // 🩸 為什麼這樣不牴觸 TASK-0152：0152 保護的是**含人工修訂的正文**
        //   （一次重出會把手改與那行警告一起抹掉）。本支不重跑匯出、不產生任何內容，
        //   只把標題那一行換掉，且**逐位元組驗證其餘內容一字不變** ——
        //   ⇒ 它保護的那個東西在這條路上結構性地動不到。
        // 數值影響：寫 1 個檔（就地改一行）＋ 台帳 append N 筆修訂事件（沿用 export 事件形狀）。
        //   ⛔ 不出 `_vN`：那是「重出」的產物，而這不是重出。
        // ===========================================================
        /// <summary>把既有章檔表頭的 `# 第 N 章 · &lt;title&gt;` 換成新標題。</summary>
        /// <param name="iTitle">新章名（**親筆** —— 工具不代取，空字串直接擋）。</param>
        public static SCP_WatchRetitleResult RetitleChapter(
            string iDataRoot, string iBook, string iChapter, string iTitle, List<string> oLines)
        {
            var aOut = new SCP_WatchRetitleResult();

            if (string.IsNullOrWhiteSpace(iTitle))
            {
                aOut.Error = "✗ `title` 是空的 —— ⛔ 章名不代取（工具沒有資格替人取名）。";
                return aOut;
            }
            string aBook = (iBook ?? "").Trim();
            string aChapter = (iChapter ?? "").Trim();
            if (aBook.Length == 0 || aChapter.Length == 0)
            {
                aOut.Error = "✗ `book` 與 `chapter` 都要給 —— ⛔ 不猜是哪一章（猜錯是改到別章的名字）。";
                return aOut;
            }
            if (aChapter.Length != 3 || !int.TryParse(aChapter, NumberStyles.Integer,
                                                      CultureInfo.InvariantCulture, out int aChapterNum))
            {
                aOut.Error = "✗ `chapter` 要是三位數章號（收到 `" + aChapter + "`）。"
                             + "⚠ `_vN` 不是章 —— 版本檔不在本支的射程內。";
                return aOut;
            }

            string aBdir = Path.Combine(BooksRoot(iDataRoot), aBook).Replace('\\', '/');
            string aPath = Path.Combine(aBdir, aChapter + ".txt").Replace('\\', '/');
            aOut.Book = aBook;
            aOut.Chapter = aChapter;
            aOut.Path = aPath;
            if (!File.Exists(aPath))
            {
                aOut.Error = "✗ 章檔不存在：" + aPath;
                return aOut;
            }

            string aBefore;
            try { aBefore = File.ReadAllText(aPath, Encoding.UTF8); }
            catch (Exception e) { aOut.Error = "✗ 讀不到章檔：" + e.Message; return aOut; }

            // 表頭那一行 —— 解析不出來就**拒絕**，⛔ 不猜第一行是不是標題
            //   （手改過表頭的章，猜錯會把別的東西換掉，而那不會有任何一層喊）。
            // 🩸 `\r?$` 不是裝飾（2026-09-21 實測）：章檔是 **CRLF**（`007.txt` 量到 1820 個
            //   `\r\n`、0 個純 `\n`），而 .NET 的 `$` 在 Multiline 下匹配 `\n` 之前
            //   ⇒ `\r` 擋在那裡。⚠ 而且捕獲組要用 `[^\r\n]*` 不是 `.*`：`.` 會把 `\r`
            //   吃進 `aM.Value`，那樣替換後的新行**掉了 `\r`** ⇒ 一份 CRLF 檔裡混進一行 LF，
            //   而 diff 看起來只是「改了標題」。
            //   📌 這也是 `SCP_Cmd_Watch.s_ChapterTitleLine` 的同一條式子 —— 兩邊一起改。
            var aRe = new Regex(@"^# 第 \d+ 章(?: · ([^\r\n]*))?\r?$", RegexOptions.Multiline);
            Match aM = aRe.Match(aBefore);
            if (!aM.Success)
            {
                aOut.Error = "✗ 這個章檔的表頭裡找不到 `# 第 N 章` 那一行 ⇒ **無從定位標題**。"
                             + "　⛔ 本次一個位元組都沒有動。";
                return aOut;
            }
            aOut.OldTitle = aM.Groups[1].Success ? aM.Groups[1].Value : "";
            string aNewLine = "# 第 " + aChapterNum.ToString(CultureInfo.InvariantCulture)
                              + " 章 · " + iTitle.Trim();
            // 🔴 保留原行尾。🩸 2026-09-21 活體踩過：式子裡的 `\r?$` **會把 `\r` 吃進 `aM.Value`**
            //   （`\r?` 是 match 的一部分，不是 lookahead）⇒ 被替換掉的範圍含 `\r`，
            //   而新行沒有 ⇒ **一份 CRLF 檔裡多出一行 LF**。
            //   實測：`006.txt` / `007.txt` 補名後第 1 行結尾的 CR 消失，全檔變成
            //   「1559 個 CRLF ＋ 1 個純 LF」—— 而 `git diff` 只顯示「改了標題那一行」，
            //   `--numstat` 也只有 1/1 ⇒ **兩條驗證路徑都看不到它**。
            //   ⛔ 這一格不能靠「記得」：行尾是唯一一種 diff 工具預設會替你藏起來的差異。
            if (aM.Value.EndsWith("\r", StringComparison.Ordinal)) aNewLine += "\r";
            aOut.NewTitle = iTitle.Trim();
            if (string.Equals(aM.Value, aNewLine, StringComparison.Ordinal))
            {
                aOut.Error = "✗ 這一章的標題已經是「" + aOut.NewTitle + "」—— 本次沒有要改的東西。";
                return aOut;
            }

            string aAfter = aBefore.Substring(0, aM.Index) + aNewLine
                            + aBefore.Substring(aM.Index + aM.Length);
            try { File.WriteAllText(aPath, aAfter, new UTF8Encoding(false)); }
            catch (Exception e) { aOut.Error = "✗ 寫不回章檔：" + e.Message; return aOut; }

            // 🔴 回讀驗證 —— **把「我只動了一行」從宣稱變成讀數**。
            //   ⛔ 不是驗「標題變了」（那只證明我想改的那格有動），
            //   是驗**其餘每一個位元組都沒動**：把回讀的內容把標題行換回舊的，要跟改之前逐字相同。
            string aBack;
            try { aBack = File.ReadAllText(aPath, Encoding.UTF8); }
            catch (Exception e) { aOut.Error = "✗ 寫了但回讀不到：" + e.Message; return aOut; }
            if (!string.Equals(aBack, aAfter, StringComparison.Ordinal))
            {
                aOut.Error = "❌ 回讀與寫出去的內容不一致 —— **落地的不是我寫的那份**。";
                return aOut;
            }
            string aRestored = aBack.Substring(0, aM.Index) + aM.Value
                               + aBack.Substring(aM.Index + aNewLine.Length);
            aOut.BodyUnchanged = string.Equals(aRestored, aBefore, StringComparison.Ordinal);
            if (!aOut.BodyUnchanged)
            {
                aOut.Error = "❌ 除了標題那一行以外，內容也變了 —— ⛔ 本支不該碰正文。"
                             + "　⚠ 檔案已經寫下去了（" + aPath + "），請用 git diff 看它動了什麼。";
                return aOut;
            }
            aOut.Ok = true;
            oLines.Add("✏️ `" + aBook + "/" + aChapter + ".txt` 標題："
                       + (aOut.OldTitle.Length > 0 ? "`" + aOut.OldTitle + "`" : "（無）")
                       + " ⇒ `" + aOut.NewTitle + "`");
            oLines.Add("   ✅ 回讀：除標題那一行外**逐位元組相同**（不是「看起來沒動」）");

            // 台帳對齊 —— 章名的真相源是台帳，只改章檔會長出第二個真相源。
            //   場次 id 從**表頭**讀（它本來就寫在那裡）；讀不到就明說沒對齊，⛔ 不靜默。
            SCP_WatchExport.TryParseChapterHeader(aBack, out _, out _, out _, out _,
                                                  out string aWork, out string aSessions, out _);
            if (string.IsNullOrWhiteSpace(aSessions))
            {
                oLines.Add("   ⚠ 表頭沒有「場次」欄 ⇒ **台帳未對齊**（台帳那側的 chapter_title 仍是舊值）。"
                           + "　⛔ 這不是失敗，是這一章的台帳關聯查不到 —— 兩邊不一致時 `op=audit` 會看到。");
                return aOut;
            }
            try
            {
                var aWarn = new List<string>();
                var aIds = SCP_WatchLedger.AppendExportEvents(iDataRoot, aSessions.Split(','),
                                                              aChapter, aBook, aOut.NewTitle, aWork, aWarn);
                foreach (string w in aWarn) oLines.Add("   " + w);
                aOut.LedgerSessions = aIds.Count;
                if (aIds.Count > 0)
                    oLines.Add("   📒 台帳 append " + aIds.Count.ToString(CultureInfo.InvariantCulture)
                               + " 筆修訂事件（chapter_title 對齊；⛔ 不改既有行）");
            }
            catch (Exception e)
            {
                oLines.Add("   ⚠ 台帳 append 失敗（**章檔已改，不回頭**）：" + e.Message);
            }
            return aOut;
        }
    }
}
