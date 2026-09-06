// 區塊職責：把 StreamWatch 期間的酒館 seq 區間**排版成一章實錄**（`Books/watch-<media>/NNN.txt`）。
// 物理意義：這一層是**書的正確性**所在 —— 收哪些、排什麼序、未收錄的怎麼交代，全在這裡。
//           產物是**機械匯出**：手改會被下次匯出覆寫，要改內容請改酒館訊息本身。
// 數值影響：`BuildChapter` **零 IO 寫入**（只讀訊息檔）；落檔與台帳回填由呼叫端另外做。
//
// ⚠ **移植自 `library.py::cmd_export_watch`（TASK-0143 ⑤ 第三刀）**，
//   逐行對照 @summit 2026-09-06 `84617ee8` 落地的那一版 —— 移植不改行為，血證註解一條不刪。
//
// ⭐ 為什麼把「排版」與「落檔」切開：排版是**逐位元組敏感**的（章要能跟舊產物對拍），
//   而落檔會寫真的書。切開之後驗收可以在 clean-room 跑完整個排版，⛔ 不必碰任何真產物。
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
    /// <summary>被收進章裡的一則。</summary>
    public sealed class SCP_WatchKept : SCP_WatchLedger.ISeqItem
    {
        public long Seq { get; set; }
        public string Ts = "";
        public string Persona = "";
        public string Tag = "";
        public string Subtag = "";
        public string Body = "";
    }

    /// <summary>排版結果。<see cref="Error"/> 非空 ＝ **沒有章**（其餘欄位不准當有效值）。</summary>
    public sealed class SCP_WatchChapter
    {
        public string Error = "";
        public string Text = "";
        public List<SCP_WatchKept> Kept = new List<SCP_WatchKept>();
        /// <summary>未收錄逐筆（seq, 理由）—— ⛔ 一定要進章內清單，讀者才分得出
        /// 「本來就只有這些」與「被濾掉了」。</summary>
        public List<KeyValuePair<long, string>> Excluded = new List<KeyValuePair<long, string>>();
        public int Stripped;
        public int WithSeg;
        public int WithoutSeg;
        public string Span = "";
    }

    public static class SCP_WatchExport
    {
        /// <summary>
        /// 自動附掛區塊（`📖 本回提到的新詞`）的清除樣式。
        /// <para>⚠ 與 python 端**同一個字面**：`[\r\n]+---[\r\n]+\s*📖\s*\*\*本回提到的新詞\*\*.*\Z`。
        /// 改它＝改產物內容 ⇒ 兩邊要一起改，否則同一段素材會出兩種章。</para>
        /// </summary>
        static readonly Regex s_AutoAttach = new Regex(
            "[\r\n]+---[\r\n]+\\s*📖\\s*\\*\\*本回提到的新詞\\*\\*[\\s\\S]*\\z",
            RegexOptions.Compiled);

        /// <summary>預設排除的公告類 tag（機器代組、剛好落在觀影期間但不是看片時的發言）。</summary>
        public const string DefaultExcludeTags =
            "commit,free-time,goodnight-protocol,bartender-relay,bartender-rule-announce,mbti,canvas,spend-time";

        static readonly string[] s_BartenderNames = { "酒保", "bartender", "tavern-keeper" };

        public static string MessagesDir(string iDataRoot, string iRoom)
            => Path.Combine(iDataRoot, "ChatTavern", "rooms", iRoom, "messages").Replace('\\', '/');

        /// <summary>
        /// 撈 <c>[iLo, iHi]</c> 的訊息（**seq 來自檔名，不是內文欄位**）。
        /// <para>物理意義：訊息落在 <c>messages/&lt;date&gt;/&lt;8 位 seq&gt;.json</c>，日期資料夾是**寫入當天**
        /// ⇒ 跨午夜的場次會橫跨兩個資料夾，**一律列舉全部日期再按 seq 過濾**，
        /// ⛔ 不要用日期推算範圍（那是「位置推導的游標會漂」的同一族）。</para>
        /// <para>⚠ 讀不動的檔**不靜默跳過**：回一個 null 內容的項目，呼叫端把它計入「未收錄」。</para>
        /// </summary>
        public static List<(long Seq, SCP_JsonData? Msg, string File)> IterMessages(
            string iDataRoot, string iRoom, long iLo, long iHi, List<string> oWarnings)
        {
            var aOut = new List<(long, SCP_JsonData?, string)>();
            string aBase = MessagesDir(iDataRoot, iRoom);
            if (!Directory.Exists(aBase)) return aOut;
            var aFiles = new List<string>(Directory.GetFiles(aBase, "*.json", SearchOption.AllDirectories));
            aFiles.Sort(StringComparer.Ordinal);
            foreach (string aF in aFiles)
            {
                if (!long.TryParse(Path.GetFileNameWithoutExtension(aF), NumberStyles.Integer,
                                   CultureInfo.InvariantCulture, out long aSeq)) continue;
                if (aSeq < iLo || aSeq > iHi) continue;
                try { aOut.Add((aSeq, SCP_JsonData.Parse(File.ReadAllText(aF, Encoding.UTF8)), aF)); }
                catch (Exception e)
                {
                    oWarnings.Add($"⚠ 讀不動 {Path.GetFileName(aF)}: {e.Message}"
                                  + "（本筆計入『未收錄』，**不靜默跳過**）");
                    aOut.Add((aSeq, null, aF));
                }
            }
            aOut.Sort((x, y) => x.Item1.CompareTo(y.Item1));
            return aOut;
        }

        /// <summary>
        /// 排版一章。**只讀不寫** —— 落檔與台帳回填是呼叫端的事。
        /// </summary>
        /// <param name="iChapter">三位數章號字串（已決定好）。</param>
        /// <param name="iAllowZeroStripped">附掛清除數為 0 時是否放行 —— ⛔ 預設不放行，見下方血證。</param>
        public static SCP_WatchChapter BuildChapter(
            string iDataRoot, string iRoom, IReadOnlyList<SCP_SeqRange> iRanges,
            string iChapter, string iMedia, string? iTitle, string? iSubtitle,
            string? iWorkTitle, string? iSessions, string? iNote,
            string? iOnlyPersonas, string? iExcludeTags, bool iAllowZeroStripped,
            List<string> oWarnings)
        {
            var aOut = new SCP_WatchChapter();

            var aExclude = new HashSet<string>(StringComparer.Ordinal);
            foreach (string t in (iExcludeTags ?? DefaultExcludeTags).Split(','))
                if (t.Trim().Length > 0) aExclude.Add(t.Trim());
            string[]? aOnly = string.IsNullOrWhiteSpace(iOnlyPersonas) ? null : iOnlyPersonas!.Split(',');

            foreach (SCP_SeqRange r in iRanges)
            {
                foreach (var (aSeq, aMsg, _) in IterMessages(iDataRoot, iRoom, r.Lo, r.Hi, oWarnings))
                {
                    if (aMsg == null)
                    { aOut.Excluded.Add(new KeyValuePair<long, string>(aSeq, "讀檔失敗")); continue; }

                    SCP_JsonData aMeta = aMsg["meta"];
                    string aPersona = aMsg.GetString("sender_persona", "");
                    if (aPersona.Length == 0) aPersona = aMsg.GetString("sender_id", "");
                    if (aPersona.Length == 0) aPersona = "?";
                    string aTag = aMeta.Exists ? aMeta.GetString("tag", "") : "";
                    string aBody = aMsg.GetString("body", "");

                    // 排除：酒保系統廣播（不是在場的人說的話）
                    if (Array.IndexOf(s_BartenderNames, aPersona) >= 0)
                    {
                        aOut.Excluded.Add(new KeyValuePair<long, string>(
                            aSeq, $"系統廣播 tag={(aTag.Length > 0 ? aTag : "-")} sender={aPersona}"));
                        continue;
                    }
                    // 排除：機器代組的公告類訊息 —— 它們**剛好落在觀影期間**，但不是在看片時說的話。
                    // 🩸 首版只擋酒保，於是 002 混進 3 則 commit 公告、004 混進 1 則自由時間公告
                    //    （001 那章沒事只是因為它的區間裡剛好沒有公告 ——
                    //     **樣本乾淨不等於過濾器對**）。
                    if (aExclude.Contains(aTag))
                    {
                        aOut.Excluded.Add(new KeyValuePair<long, string>(
                            aSeq, $"公告類 tag={aTag}（機器代組，非觀影發言）"));
                        continue;
                    }
                    if (aOnly != null && Array.IndexOf(aOnly, aPersona) < 0)
                    {
                        aOut.Excluded.Add(new KeyValuePair<long, string>(
                            aSeq, $"不在 --only-personas 名單 ({aPersona})"));
                        continue;
                    }

                    string aNewBody = s_AutoAttach.Replace(aBody, "", 1);
                    if (!ReferenceEquals(aNewBody, aBody) && aNewBody.Length != aBody.Length) ++aOut.Stripped;
                    aOut.Kept.Add(new SCP_WatchKept
                    {
                        Seq = aSeq,
                        Ts = aMsg.GetString("ts", ""),
                        Persona = aPersona,
                        Tag = aTag,
                        Subtag = aMeta.Exists ? aMeta.GetString("subtag", "") : "",
                        Body = aNewBody.Trim(),
                    });
                }
            }

            if (aOut.Kept.Count == 0)
            {
                aOut.Error = "❌ 區間內沒有可收錄的訊息 —— 先確認 seq 區間與 --room 對不對。";
                return aOut;
            }

            // 🩸 附掛清除數若為 0，很可能是**樣式又沒對上**（001 首版就是這樣靜默的）。
            //   真的一則附掛都沒有時要人明說，⛔ 別讓工具自己決定「這次沒有」。
            if (aOut.Stripped == 0 && !iAllowZeroStripped)
            {
                aOut.Error = "❌ 自動附掛清除數 = 0 —— 這個欄位存在的唯一理由就是防靜默過濾，"
                             + "而它自己回報 0 通常代表 pattern 沒對上（換行 \\r\\n 混排是慣犯）。\n"
                             + "   確認過真的沒有附掛區塊 → 加 --allow-zero-stripped 明說。";
                return aOut;
            }

            // ── 依段序重排（TASK-0061）────────────────────────────
            // 🩸 `IterMessages` 照 tavern seq 掃 ⇒ 書把**河道的亂序原樣複印**。
            //   實證 010.txt：章內素材時間 20:51:59 → 20:52:31 → **20:52:19** → 20:53:51 → **20:54:21**。
            //   ⇒ 河道亂序是當下不好讀；**書亂序是永久錯的實錄**。
            // ⚠ 排序來源一律明印在表頭 —— ⛔ 沒有台帳時**不得靜默 fallback**：
            //   「照段序排過」與「照 seq 排的舊章」必須在產物上分得出來。
            Dictionary<long, int> aSegOfSeq = SCP_WatchLedger.LoadSegmentOrder(iDataRoot, oWarnings);
            string aSortNote;
            if (aSegOfSeq.Count > 0)
            {
                aOut.Kept = SCP_WatchLedger.OrderBySegment(aOut.Kept, aSegOfSeq,
                                                           out int aWith, out int aWithout);
                aOut.WithSeg = aWith; aOut.WithoutSeg = aWithout;
            }
            else { aOut.WithSeg = 0; aOut.WithoutSeg = aOut.Kept.Count; }

            if (aOut.WithSeg > 0)
                aSortNote = $"**段序**（`segments.jsonl`）—— 有段號 {aOut.WithSeg} 則"
                            + (aOut.WithoutSeg > 0
                               ? $"／無段號 {aOut.WithoutSeg} 則（保留原 seq 相對位置，併在前一段之後）"
                               : "／全部有段號");
            else
                aSortNote = "**tavern seq**（本章區間在段台帳裡沒有任何一則有段號 —— "
                            + "舊章或台帳上線前的場次）⇒ 素材時間可能亂序，這不是漏排是沒得排";

            // 收錄人次：**依筆數降冪，同票保持首次出現序**（python `sorted` 是穩定排序）。
            // ⚠ 不穩定的排序會讓同票兩人在不同機器上換位置 ⇒ 產物逐位元組不同而內容一樣。
            var aOrder = new List<string>();
            var aByPersona = new Dictionary<string, int>(StringComparer.Ordinal);
            long aMin = long.MaxValue, aMax = long.MinValue;
            foreach (SCP_WatchKept k in aOut.Kept)
            {
                if (!aByPersona.ContainsKey(k.Persona)) { aByPersona[k.Persona] = 0; aOrder.Add(k.Persona); }
                aByPersona[k.Persona] += 1;
                if (k.Seq < aMin) aMin = k.Seq;
                if (k.Seq > aMax) aMax = k.Seq;
            }
            var aRanked = new List<string>(aOrder);
            // ⚠ `List.Sort` **不保證穩定** ⇒ 自己做穩定排序（帶 index 當第二鍵）。
            var aIdx = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < aOrder.Count; ++i) aIdx[aOrder[i]] = i;
            aRanked.Sort((x, y) =>
            {
                int c = aByPersona[y].CompareTo(aByPersona[x]);   // 降冪
                return c != 0 ? c : aIdx[x].CompareTo(aIdx[y]);
            });
            aOut.Span = aMin.ToString(CultureInfo.InvariantCulture) + " – "
                        + aMax.ToString(CultureInfo.InvariantCulture);

            int aChapterNum = int.TryParse(iChapter, NumberStyles.Integer, CultureInfo.InvariantCulture,
                                           out int n) ? n : 0;
            var aLines = new List<string>();
            aLines.Add(!string.IsNullOrEmpty(iTitle)
                       ? $"# 第 {aChapterNum} 章 · {iTitle}" : $"# 第 {aChapterNum} 章");
            // 副標：併章時保留另一位匯出者取的章名 —— 併章是為了不讓同一段 seq 出現兩次，
            //       **不是為了讓其中一個人的命名消失**。
            if (!string.IsNullOrEmpty(iSubtitle)) { aLines.Add(""); aLines.Add($"### —— {iSubtitle}"); }
            aLines.Add("");
            aLines.Add($"> 機械匯出 —— 內容為聊天酒館 seq {aOut.Span} 原文，僅移除自動附掛區塊。");
            aLines.Add("> 手改會被下次匯出覆寫；要改內容請改酒館訊息本身。");
            aLines.Add("");
            aLines.Add("## 場次讀數");
            aLines.Add("");
            aLines.Add("| | |");
            aLines.Add("|---|---|");
            if (!string.IsNullOrEmpty(iWorkTitle)) aLines.Add($"| 作品 | {iWorkTitle} |");
            aLines.Add($"| 媒材 | `{iMedia}` |");
            if (!string.IsNullOrEmpty(iSessions))
                aLines.Add("| 場次 | " + string.Join(" ／ ", iSessions!.Split(',')) + " |");
            var aRangeTexts = new List<string>();
            foreach (SCP_SeqRange r in iRanges) aRangeTexts.Add($"{r.Lo}–{r.Hi}");
            aLines.Add("| seq 區間 | " + string.Join(" ／ ", aRangeTexts) + " |");
            aLines.Add($"| 排序 | {aSortNote} |");
            var aCounts = new List<string>();
            foreach (string p in aRanked) aCounts.Add($"{p} {aByPersona[p]}");
            aLines.Add($"| 收錄 | **{aOut.Kept.Count} 筆**（" + string.Join("／", aCounts)
                       + $"）／未收錄 **{aOut.Excluded.Count} 筆**／清掉自動附掛 **{aOut.Stripped}** 處 |");
            if (!string.IsNullOrEmpty(iNote)) aLines.Add($"| 備註 | {iNote} |");
            aLines.Add("");
            if (aOut.Excluded.Count > 0)
            {
                // 未收錄逐筆列出（含理由）—— 讀者才分得出「本來就只有這些」與「被濾掉了」
                aLines.Add("<details><summary>未收錄清單（點開）</summary>");
                aLines.Add("");
                foreach (var kv in aOut.Excluded) aLines.Add($"- seq {kv.Key} — {kv.Value}");
                aLines.Add("");
                aLines.Add("</details>");
                aLines.Add("");
            }
            aLines.Add("## 實錄");
            aLines.Add("");
            foreach (SCP_WatchKept k in aOut.Kept)
            {
                string aHhmm = (k.Ts ?? "").Length >= 16 ? k.Ts!.Substring(11, 5) : "";
                string aHead = $"### [seq {k.Seq}] {aHhmm} · {k.Persona}";
                if (k.Subtag.Length > 0) aHead += $" · {k.Subtag}";
                aLines.Add(aHead);
                aLines.Add("");
                aLines.Add(k.Body);
                aLines.Add("");
            }

            aOut.Text = string.Join("\n", aLines).Replace("\r\n", "\n");
            return aOut;
        }

        // ===========================================================
        // 區塊職責：把**已經落檔的章**的表頭讀回成重建所需的參數（媒材／seq 區間／章名／…）。
        // 物理意義：這是「拿現有產物重跑一次現行實作，看還原不還原得出來」那條路的入口 ——
        //          `selftest` 的重出對拍與 `cmd watch --arg op=audit` **都吃這一支**。
        // 🩸 為什麼搬進 SCP_Core：它原本只住在 `Senate.Cli/SelfTest.cs`，而 audit 也要用。
        //   兩份各自維護的解析器＝兩個會各自漂的真相源，而漂掉的症狀是
        //   「selftest 說 5 章符合、audit 說 7 章符合」，**兩邊都不報錯**。
        // 數值影響：純唯讀、不碰檔案。解析不出來回 false ——
        //   ⛔ 呼叫端不准把它讀成「這章沒問題」，那是**沒讀到**不是通過。
        // ===========================================================
        public static bool TryParseChapterHeader(string iText, out string oMedia,
                                                 out List<SCP_SeqRange> oRanges, out string oTitle,
                                                 out string oSubtitle, out string oWork,
                                                 out string oSessions, out string oNote)
        {
            oMedia = ""; oRanges = new List<SCP_SeqRange>(); oTitle = ""; oSubtitle = "";
            oWork = ""; oSessions = ""; oNote = "";

            Match aTitle = Regex.Match(iText, @"^# 第 \d+ 章(?: · (.*))?$", RegexOptions.Multiline);
            if (!aTitle.Success) return false;
            oTitle = aTitle.Groups[1].Success ? aTitle.Groups[1].Value : "";

            Match aSub = Regex.Match(iText, @"^### —— (.*)$", RegexOptions.Multiline);
            if (aSub.Success) oSubtitle = aSub.Groups[1].Value;

            Match aMedia = Regex.Match(iText, @"^\| 媒材 \| `([^`]+)` \|$", RegexOptions.Multiline);
            if (!aMedia.Success) return false;
            oMedia = aMedia.Groups[1].Value;

            Match aWork = Regex.Match(iText, @"^\| 作品 \| (.*) \|$", RegexOptions.Multiline);
            if (aWork.Success) oWork = aWork.Groups[1].Value;

            Match aSess = Regex.Match(iText, @"^\| 場次 \| (.*) \|$", RegexOptions.Multiline);
            if (aSess.Success) oSessions = aSess.Groups[1].Value.Replace(" ／ ", ",");

            Match aNote = Regex.Match(iText, @"^\| 備註 \| (.*) \|$", RegexOptions.Multiline);
            if (aNote.Success) oNote = aNote.Groups[1].Value;

            Match aRng = Regex.Match(iText, @"^\| seq 區間 \| (.*) \|$", RegexOptions.Multiline);
            if (!aRng.Success) return false;
            foreach (string aPart in aRng.Groups[1].Value.Split(new[] { " ／ " }, StringSplitOptions.None))
            {
                Match m = Regex.Match(aPart.Trim(), @"^(\d+)[–\-](\d+)$");
                if (!m.Success) return false;
                oRanges.Add(new SCP_SeqRange(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                                             long.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)));
            }
            return oRanges.Count > 0;
        }
    }
}
