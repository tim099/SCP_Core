// 區塊職責：`cmd watch` —— 觀影實錄的匯出與查詢（**原生**，不需要 Unity）。
// 物理意義：這是 `library.py export-watch` / `list-untitled` 的 Cmd 殼（TASK-0143 ⑤ 第五刀）。
//           底下四層都已經移進 SCP_Core：台帳 `SCP_WatchLedger`／反查 `SCP_WatchResolve`／
//           排版 `SCP_WatchExport`／落檔 `SCP_WatchWriter`。本檔只做**參數收斂與輸出**。
// 數值影響：`op=untitled` 純讀；`op=export` 寫一個章檔 ＋ 台帳 append N 筆（**永不覆寫既有行**）。
//
// ⚠ **PortStatus 是 `Native` 而且那是誠實的**：整條路只碰檔案 —— 酒館訊息是**讀**、
//   `Books/` 與台帳是**寫**，而那兩個落點的寫者數不會因為搬家而增加
//   （章檔本來就只有一個寫者；台帳本來就是兩個，而它們不互相蓋只因為兩邊都只 append）。
//   ⛔ 所以這一支**不需要委派**，跟 `cmd rest` 的廣播那半不同族。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Watch;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Watch : SCP_Cmd
    {
        public override string Name => "watch";

        public override string Summary =>
            "觀影實錄：把酒館 seq 區間匯出成一章（`op=export`）／列出章名仍掛哨兵的章（`op=untitled`）"
            + "—— **本地跑，不需要 Editor**";

        public override string Details =>
            "⭐ 章號與章名的真相源是**場次台帳**（`sessions_log.jsonl` 的 export 事件，取最後一筆），\n"
            + "  `prepared/<media_id>.json` 只是**開場前的意圖**且是 per-media 單槽（下一話就被覆寫）\n"
            + "  ⇒ 跨集之後拿它解舊場會解成別集的章（TASK-0142）。兩邊不一致時採台帳並**印出兩個值**。\n"
            + "⚠ 產物是**機械匯出**：手改會被下次匯出覆寫，要改內容請改酒館訊息本身。\n"
            + "⚠ 兩道守衛預設都是**擋**：章檔已存在（`force=1` 才覆寫）／seq 區間與既有章重疊\n"
            + "  （`allow_overlap=1` 才並存）—— 🩸 後者是 2026-08-17 首日就發生過的：\n"
            + "  同一話兩章、區間重疊，而**兩邊都成功、都不報錯**。\n"
            + "⚠ 排序來源（段序／tavern seq）**一律印在章的表頭** —— 沒有段台帳時不靜默 fallback，\n"
            + "  「照段序排過」與「照 seq 排的舊章」必須在產物上分得出來。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("watch --arg data_root=<AgentCommands> --arg op=export"
                                   + " --arg from_session=<場次 id> --arg title=<章名>");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("op", "export｜untitled（預設 untitled —— **純讀的那個當預設**）"),
            new SCP_CmdArgSpec("from_session", "由台帳反查 media／seq 區間／同場清單／章號章名"
                               + "（收工自動匯出走這條）"),
            new SCP_CmdArgSpec("media", "媒材 id；用 from_session 時可省略"),
            new SCP_CmdArgSpec("seq_ranges", "一或多段 seq 區間，如 `15440-15459,15430-15433`"),
            new SCP_CmdArgSpec("chapter", "章號（三位數）；省略＝取現有最大值 +1"),
            // ⛔ 章名不代取：工具沒有資格替人取名（TASK-0064 的既有拍板）。
            //   沒有名字時走哨兵值出書 —— 那是**看得見的缺**，勝過「整本書不存在」那種安靜的錯。
            new SCP_CmdArgSpec("title", "章名（**親筆**；機械匯出不代取）"),
            new SCP_CmdArgSpec("subtitle", "副標（併章時保留另一位匯出者的章名）"),
            new SCP_CmdArgSpec("work_title", "作品名＋話數，寫進場次讀數表"),
            new SCP_CmdArgSpec("sessions", "場次 id（逗號分隔），寫進表頭並用於台帳回填"),
            new SCP_CmdArgSpec("book", "Books slug（預設先看既有目錄，兩本都在就**不猜**）"),
            new SCP_CmdArgSpec("room", "酒館房 id（預設 tavern）"),
            new SCP_CmdArgSpec("note", "備註一行"),
            new SCP_CmdArgSpec("only_personas", "只收這些 persona（逗號分隔）；被排除的**一律列在未收錄清單**"),
            new SCP_CmdArgSpec("exclude_tags", "排除這些 meta.tag 的公告類訊息；給空字串＝全收"),
            new SCP_CmdArgSpec("force", "=1 ⇒ 章檔已存在時覆寫"),
            new SCP_CmdArgSpec("allow_overlap", "=1 ⇒ seq 區間與既有章重疊時仍然寫（明說要並存）"),
            new SCP_CmdArgSpec("allow_zero_stripped", "=1 ⇒ 自動附掛清除數為 0 時放行"
                               + "（⛔ 預設擋 —— 那個數字回 0 通常代表樣式沒對上）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root").Trim();
            string aOp = iArgs.Get("op").Trim();
            if (aOp.Length == 0) aOp = "untitled";
            if (!Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(2, "✗ 資料根不存在：" + aDataRoot);

            return aOp switch
            {
                "untitled" => OpUntitled(aDataRoot),
                "export" => OpExport(aDataRoot, iArgs),
                _ => SCP_CmdResult.Fail(2, $"✗ 不認得的 op：`{aOp}`（吃的是 export｜untitled）"),
            };
        }

        /// <summary>哨兵值 —— 由**設定檔**供給（TASK-0064：改設定即改字串，兩端同源）。</summary>
        static string UntitledMarker(string iDataRoot, List<string> oLines)
        {
            const string aFallback = "##None##";
            string aPath = Path.Combine(iDataRoot, "StreamWatch", "settings.json");
            if (!File.Exists(aPath)) return aFallback;
            try
            {
                string aV = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8))
                                        .GetString("untitled_marker", "").Trim();
                return aV.Length > 0 ? aV : aFallback;
            }
            catch (Exception e)
            {
                // ⚠ 這一行是設定沒生效時**唯一會出聲的地方** —— ⛔ 不可以靜默用地板值。
                oLines.Add($"⚠ StreamWatch/settings.json 讀不動（{e.Message}）⇒ 本次用內建地板值，"
                           + "**設定沒有生效**。");
                return aFallback;
            }
        }

        /// <summary>列出章名仍掛哨兵的章 —— **哨兵的可查性由這支提供**。</summary>
        /// <remarks>🩸 沒有這一格，本單就變成「書有了、名字永遠是哨兵，而沒有人會發現」——
        /// 那是把一種靜默換成另一種。實測活體：某章掛哨兵掛了 **2 天**，
        /// 期間五個人在同一個媒材上工作過而**沒有任何一層提醒**。</remarks>
        static SCP_CmdResult OpUntitled(string iDataRoot)
        {
            var aResult = new SCP_CmdResult();
            string aMarker = UntitledMarker(iDataRoot, aResult.Lines);
            string aRoot = SCP_WatchWriter.BooksRoot(iDataRoot);
            if (!Directory.Exists(aRoot)) return SCP_CmdResult.Fail(1, "✗ 找不到 Books/（" + aRoot + "）");

            var aHits = new List<(string Book, string Chapter, string First)>();
            var aBooks = new List<string>(Directory.GetDirectories(aRoot));
            aBooks.Sort(StringComparer.Ordinal);
            foreach (string aBook in aBooks)
            {
                var aChs = new List<string>(Directory.GetFiles(aBook, "???.txt"));
                aChs.Sort(StringComparer.Ordinal);
                foreach (string aCh in aChs)
                {
                    string aStem = Path.GetFileNameWithoutExtension(aCh);
                    if (aStem.Length != 3 || !int.TryParse(aStem, out _)) continue;
                    string aAll;
                    try { aAll = File.ReadAllText(aCh, Encoding.UTF8); } catch { continue; }
                    string aHead = aAll.Length > 2000 ? aAll.Substring(0, 2000) : aAll;
                    if (aHead.IndexOf(aMarker, StringComparison.Ordinal) < 0) continue;
                    string aFirst = aHead.Replace("\r\n", "\n").Split('\n')[0].Trim();
                    aHits.Add((Path.GetFileName(aBook), Path.GetFileName(aCh), aFirst));
                }
            }
            if (aHits.Count == 0)
            {
                aResult.Lines.Add($"✅ 沒有任何章掛著 {aMarker}");
                aResult.AddValue("untitled", "0");
                return aResult;
            }
            aResult.Lines.Add($"⚠ {aHits.Count} 章章名未定（{aMarker}）：");
            foreach (var h in aHits) aResult.Lines.Add($"  {h.Book}/{h.Chapter}  {h.First}");
            aResult.Lines.Add("補名：改 `prepared/<media_id>.json` 的 chapter_title 後帶 `force=1` 重出，"
                              + "或直接給 `title=` 重出 —— ⛔ **不能手改 .txt**（機械產物，下次匯出會覆寫）。");
            aResult.AddValue("untitled", aHits.Count.ToString(CultureInfo.InvariantCulture));
            return aResult;
        }

        static SCP_CmdResult OpExport(string iDataRoot, SCP_CmdArgs iArgs)
        {
            var aResult = new SCP_CmdResult();
            string aMarker = UntitledMarker(iDataRoot, aResult.Lines);
            string aRoom = iArgs.Get("room").Trim(); if (aRoom.Length == 0) aRoom = "tavern";
            string aMedia = iArgs.Get("media").Trim();
            string aSeqRanges = iArgs.Get("seq_ranges").Trim();
            string aSessions = iArgs.Get("sessions").Trim();
            string aChapter = iArgs.Get("chapter").Trim();
            string aTitle = iArgs.Get("title").Trim();
            string aWorkTitle = iArgs.Get("work_title").Trim();
            string aNote = iArgs.Get("note").Trim();
            string aFromSession = iArgs.Get("from_session").Trim();

            List<SCP_SeqRange> aRanges;
            if (aFromSession.Length > 0)
            {
                SCP_WatchResolveResult aR = SCP_WatchResolve.FromSession(
                    iDataRoot, aFromSession, aChapter.Length > 0 ? aChapter : null, aMarker, aResult.Lines);
                if (aR.Error.Length > 0) return SCP_CmdResult.Fail(1, aR.Error);
                if (aMedia.Length == 0) aMedia = aR.Media;
                if (aSessions.Length == 0) aSessions = string.Join(",", aR.Sessions);
                if (aChapter.Length == 0) aChapter = aR.Chapter;
                // 章名的真相源順序（同章號）：明示 → **台帳記過的** → 準備檔（意圖）→ 哨兵。
                // ⚠ 台帳排在準備檔前面，因為準備檔是 per-media 單槽 ⇒ 跨集之後它是**下一話的名字**，
                //   而台帳那一筆是「這一章上次匯出時叫什麼」＝ 這一章的事實。
                if (aTitle.Length == 0) aTitle = aR.LedgerTitle;
                if (aTitle.Length == 0) aTitle = aR.Prepared.GetString("chapter_title", "").Trim();
                if (aWorkTitle.Length == 0) aWorkTitle = aR.LedgerWorkTitle;
                if (aWorkTitle.Length == 0)
                {
                    aWorkTitle = aR.Prepared.GetString("export_work_title", "").Trim();
                    if (aWorkTitle.Length == 0) aWorkTitle = aR.Prepared.GetString("show_title", "").Trim();
                }
                if (aTitle.Length == 0)
                {
                    // ⛔ 章名一定要親筆 —— 不拿影片標題當預設值。
                    // ✅ 但「沒有章名」不該讓**整本書不存在**（Tim 2026-08-26 拍板，TASK-0064）：
                    //    🩸 實撞：四場全收工、48 則觀察都在河道上，而書一章都沒有 ——
                    //    而「書不存在」跟「這一話沒人看」在產物上**完全同形**，沒有任何一層會喊。
                    aTitle = aMarker;
                    string aAdd = $"⚠ 章名未定（{aMarker}）—— 匯出時準備檔沒有 chapter_title。"
                                  + "補名**不能手改本檔**（機械產物，下次匯出會覆寫）："
                                  + "改 prepared/<media_id>.json 的 chapter_title 後 force=1 重出，或直接給 title=。";
                    aNote = (aNote.Length > 0 ? aNote + " " : "") + aAdd;
                    aResult.Lines.Add($"⚠ 準備檔沒有 chapter_title ⇒ 章名用哨兵值 {aMarker} 出書"
                                      + "（章名仍要人親筆補）。");
                }
                aRanges = new List<SCP_SeqRange>(aR.Ranges);
                aResult.Lines.Add($"↩ from-session {aFromSession}：media={aMedia} "
                                  + $"章={(aChapter.Length > 0 ? aChapter : "(自動)")} "
                                  + $"區間={string.Join(",", aRanges)} 場次={aSessions}");
            }
            else
            {
                if (aMedia.Length == 0 || aSeqRanges.Length == 0)
                    return SCP_CmdResult.Fail(2,
                        "✗ 缺 media / seq_ranges（或改用 from_session 由台帳反查）。");
                aRanges = ParseRanges(aSeqRanges, out string aWhy);
                if (aWhy.Length > 0) return SCP_CmdResult.Fail(2, aWhy);
            }

            SCP_WatchWriteResult aW = SCP_WatchWriter.WriteChapter(
                iDataRoot, aRoom, aRanges, aMedia,
                NullIfEmpty(iArgs.Get("book")), NullIfEmpty(aChapter),
                NullIfEmpty(aTitle), NullIfEmpty(iArgs.Get("subtitle")), NullIfEmpty(aWorkTitle),
                NullIfEmpty(aSessions), NullIfEmpty(aNote),
                NullIfEmpty(iArgs.Get("only_personas")), NullIfEmpty(iArgs.Get("exclude_tags")),
                iForce: iArgs.Get("force").Trim() == "1",
                iAllowOverlap: iArgs.Get("allow_overlap").Trim() == "1",
                iAllowZeroStripped: iArgs.Get("allow_zero_stripped").Trim() == "1",
                aResult.Lines);
            if (aW.Error.Length > 0) return SCP_CmdResult.Fail(1, aW.Error);

            aResult.AddOutput(aW.OutPath);
            aResult.AddValue("book", aW.Book);
            aResult.AddValue("chapter", aW.Chapter);
            aResult.AddValue("kept", aW.Chapterized!.Kept.Count.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("excluded", aW.Chapterized.Excluded.Count.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("stripped", aW.Chapterized.Stripped.ToString(CultureInfo.InvariantCulture));
            // ⚠ 這三個是**回讀落地檔案量到的**，不是組字串時算的 —— 印 ✓ 不算數。
            aResult.AddValue("back_lines", aW.BackLines.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("back_entries", aW.BackEntries.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("ledger_appended", aW.LedgerAppended.Count.ToString(CultureInfo.InvariantCulture));
            return aResult;
        }

        static string? NullIfEmpty(string? iV) => string.IsNullOrWhiteSpace(iV) ? null : iV!.Trim();

        /// <summary>`15440-15459,15430-15433` → 兩段。**排序、不合併**（合併是反查那一層的事）。</summary>
        static List<SCP_SeqRange> ParseRanges(string iSpec, out string oWhy)
        {
            oWhy = "";
            var aOut = new List<SCP_SeqRange>();
            foreach (string aPart in (iSpec ?? "").Split(new[] { ',', ' ', '\t', '\n', '\r' },
                                                         StringSplitOptions.RemoveEmptyEntries))
            {
                string[] aBits = aPart.Split(new[] { '-', '~', '–' }, 2);
                if (aBits.Length != 2
                    || !long.TryParse(aBits[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long lo)
                    || !long.TryParse(aBits[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long hi))
                { oWhy = $"✗ seq 區間格式錯誤：`{aPart}`（要 15440-15459 這種形狀）"; return aOut; }
                if (hi < lo) { oWhy = $"✗ seq 區間反了：`{aPart}`"; return aOut; }
                aOut.Add(new SCP_SeqRange(lo, hi));
            }
            aOut.Sort((x, y) => x.Lo != y.Lo ? x.Lo.CompareTo(y.Lo) : x.Hi.CompareTo(y.Hi));
            return aOut;
        }
    }
}
