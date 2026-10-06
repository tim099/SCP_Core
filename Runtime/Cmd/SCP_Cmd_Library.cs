// 區塊職責：閱讀庫的宿主中立 Cmd 入口，根目錄由宿主設定提供。
// 物理意義：資料根與信件庫根各自解析，persona 決定讀者與信件落點。
// 數值影響：寫入前驗證根目錄；正文、閱讀卡與追回檔使用同一組已解析根。
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Letters;
using SCP.Core.Library;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Library : SCP_Cmd
    {
        /// <summary>宿主每次呼叫讀取設定並解析根目錄；未註冊時拒絕執行。</summary>
        public static Func<SCP_LibraryRoots>? RootsProvider { get; set; }

        public override string Name => "library";
        public override string Category => SCP_CmdCategory.Reading;

        public override string Summary =>
            "閱讀庫（work → media → reader）：建檔／登記讀者／落章節心得／書籤／人物與看法版本史／追回檔。"
            + "**本地跑，Editor 沒開也成。**";

        public override string Details =>
            "⚠ 這裡是**閱讀線**（`BookNotes/Library/` 的 work → media → reader），\n"
            + "⛔ 不是寫書線（`BookNotes/<slug>/book.json`，那條走 `"
            + SCP_CmdRegistry.Invoke("book") + "`）。\n"
            + "🩸 兩邊有同名的 op 而**動的是兩份資料** —— 判「有沒有等價 op」要看**寫入目錄**，不是看名字。\n\n"
            + "⭐ 每一支寫入 op 都會連帶重生成兩個投影：閱讀卡 `bookshelf.md` 與追回檔 "
            + "`letters/<persona>/cmd/reading_recall_<media>.md`。\n"
            + "　 那不是順手做的 —— **stale 投影比沒有投影更糟**（下次續讀撈到上一次的視圖，而它看起來完全正常）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("library"
                                   + " --arg op=recall --arg media_id=<id> --arg persona=<誰>");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("op", "media_init｜register_reader｜note_chapter｜bookmark｜add_character"
                                     + "｜revise_view｜recall｜sync_shelf｜paths（預設 paths＝純讀）"
                                     + "｜**scan｜comics｜comic_pages｜authored_diff｜authored_migrate｜share_body｜share**"
                                     + "（paths／scan／comics／authored_diff／authored_migrate 不需要 persona／media_id；"
                                     + "comic_pages 只要 media_id）"),
            new SCP_CmdArgSpec("show_migrated", "op=scan 用：=1 ⇒ 連已遷移的 Archive 一起列（預設隱藏，而隱藏幾筆會印出來）"),
            new SCP_CmdArgSpec("book", "authored_diff／authored_migrate 用：舊 store 的書 slug（必填）"),
            new SCP_CmdArgSpec("confirm", "op=authored_migrate 用：=1 才真的寫（預設 dry-run，零寫入）"),
            new SCP_CmdArgSpec("round", "op=share_body／share 用：要貼哪一個 round（省略＝該章最大那個）"),
            new SCP_CmdArgSpec("persona", "讀者 persona（讀者操作必填）"),
            new SCP_CmdArgSpec("media_id", "媒材 id（讀者操作必填）"),
            new SCP_CmdArgSpec("work_id", "op=media_init 用：作品 id（必填）"),
            new SCP_CmdArgSpec("media_kind", "op=media_init 用：comic｜anim｜film｜series｜stream｜book"),
            new SCP_CmdArgSpec("title", "op=media_init 用：作品名（必填）"),
            new SCP_CmdArgSpec("title_original", "op=media_init 用：原文名"),
            new SCP_CmdArgSpec("author", "op=media_init 用：作者／監督"),
            new SCP_CmdArgSpec("anticipation", "op=media_init 用：期待度 0-5（預設 3＝中性，"
                                               + "**工具不替本人表態**）"),
            new SCP_CmdArgSpec("aliases", "op=media_init 用：別名，`|` 或 `,` 分隔"),
            new SCP_CmdArgSpec("genre_tags", "op=media_init 用：類型標籤，分隔同上"),
            new SCP_CmdArgSpec("chapter_id", "op=note_chapter／share_body／share 用：四位數章號（必填；`0000`＝序章）；"
                                             + "op=comic_pages 用：選填 —— 省略＝只列這部有哪些話"),
            new SCP_CmdArgSpec("display_number", "op=note_chapter 用：人話章號（如 `第 1 話`）"),
            new SCP_CmdArgSpec("chapter_title", "op=note_chapter 用：章名"),
            new SCP_CmdArgSpec("time_range", "op=note_chapter 用：時間段（如 `12:37-13:41`）"),
            new SCP_CmdArgSpec("body", "op=note_chapter 用：正文（必填，**長文走 --arg-file**）"),
            new SCP_CmdArgSpec("impression", "note_chapter／bookmark 用：目前看法"),
            new SCP_CmdArgSpec("note", "op=bookmark 用：書籤（上次寫到哪）"),
            new SCP_CmdArgSpec("bookmark_note", "op=note_chapter 用：書籤"),
            new SCP_CmdArgSpec("status", "op=bookmark 用：reading｜completed｜dropped…"),
            new SCP_CmdArgSpec("append", "op=note_chapter 用：=1 ⇒ **追加進既有 round**（同一話的第二場），"
                                         + "⛔ 不開新的 r{N}"),
            new SCP_CmdArgSpec("append_round", "op=note_chapter 用：要續寫哪一個 round（省略＝最大那個）"),
            new SCP_CmdArgSpec("character_id", "add_character／revise_view 用：人物 id（必填）"),
            new SCP_CmdArgSpec("name", "op=add_character 用：人物名（必填）"),
            new SCP_CmdArgSpec("name_original", "op=add_character 用：原文讀音"),
            new SCP_CmdArgSpec("facts", "add_character／revise_view 用：已確認 facts，一行一條"),
            new SCP_CmdArgSpec("view", "add_character／revise_view 用：主觀看法（必填）"),
            new SCP_CmdArgSpec("change_reason", "op=revise_view 用：**為什麼改觀** —— 比「改成什麼」更難事後重建"),
            new SCP_CmdArgSpec("full", "op=recall 用：=0 ⇒ 只列 round 索引不展開全文（預設展開）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            SCP_LibraryRoots aRoots;
            try
            {
                if (RootsProvider == null)
                    return SCP_CmdResult.Fail(3, "✗ 宿主尚未提供閱讀庫路徑設定");
                aRoots = RootsProvider();
            }
            catch (Exception e)
            {
                return SCP_CmdResult.Fail(3, "✗ 讀取閱讀庫路徑設定失敗：" + e.Message);
            }
            string? aError = RootError(aRoots.DataRoot, "資料根")
                             ?? RootError(aRoots.LettersRoot, "信件庫根");
            if (aError != null) return SCP_CmdResult.Fail(3, aError);
            string aDataRoot = aRoots.DataRoot.Value;
            var aLetters = new SCP_LettersRoot(aRoots.LettersRoot.Value);

            // ⭐ 預設是**純讀**那一支 —— 打錯 op 的代價要是「什麼都沒發生」，不是「建了一部作品」。
            string aOp = iArgs.Get("op").Trim();
            if (aOp.Length == 0) aOp = "paths";

            string aPersona = iArgs.Get("persona").Trim();
            string aMediaId = iArgs.Get("media_id").Trim();
            // ⚠ 這幾支是**庫層**的（掃全庫／比對兩個 store／外部漫畫）—— 它們沒有「誰的進度」這一維，
            //   要求 persona／media_id 只會讓人被迫填兩個不會被讀的值，而那種值日後會被人當成真的。
            //   comic_pages 是庫層的、**只要 media_id**（誰讀都一樣的頁檔位置），⇒ 不要求 persona。
            bool aLibraryWideOp = aOp is "paths" or "scan" or "comics" or "authored_diff" or "authored_migrate"
                                  or "comic_pages";
            if (aOp == "comic_pages" && aMediaId.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ op=`comic_pages` 需要 `media_id`（`comic-<slug>`）—— 不代取");
            if (!aLibraryWideOp && (aPersona.Length == 0 || aMediaId.Length == 0))
                return SCP_CmdResult.Fail(2, $"✗ op=`{aOp}` 需要 `persona` 與 `media_id` —— 兩者都不代取",
                    "  ⚠ 讀可以跨 persona，**寫只寫自己**：身分猜錯是把心得記到別人頭上。");

            // persona 必須是已存在的身分；拼錯名字不能自動長出第二棵信件樹。
            if (aPersona.Length > 0 && (!SCP_LibraryStore.IsValidId(aPersona)
                || !Directory.Exists(SCP_LettersPaths.ProfileDir(aLetters, aPersona))))
                return SCP_CmdResult.Fail(2, "✗ persona 不存在或格式不合法：" + aPersona);

            return aOp switch
            {
                "paths" => OpPaths(aDataRoot, aLetters, aPersona, aMediaId),
                "media_init" => OpMediaInit(aDataRoot, aLetters, aPersona, aMediaId, iArgs),
                "register_reader" => OpRegisterReader(aDataRoot, aLetters, aPersona, aMediaId),
                "note_chapter" => OpNoteChapter(aDataRoot, aLetters, aPersona, aMediaId, iArgs),
                "bookmark" => OpBookmark(aDataRoot, aLetters, aPersona, aMediaId, iArgs),
                "add_character" => OpAddCharacter(aDataRoot, aLetters, aPersona, aMediaId, iArgs),
                "revise_view" => OpReviseView(aDataRoot, aLetters, aPersona, aMediaId, iArgs),
                "recall" => OpRecall(aDataRoot, aLetters, aPersona, aMediaId, iArgs),
                "sync_shelf" => OpSyncShelf(aDataRoot, aLetters, aPersona, aMediaId),
                "scan" => OpScan(aDataRoot, iArgs),
                "comics" => OpComics(aDataRoot, aRoots.ComicRoot),
                "authored_diff" => OpAuthoredDiff(aDataRoot, iArgs),
                "authored_migrate" => OpAuthoredMigrate(aDataRoot, iArgs),
                "share_body" => OpShareBody(aDataRoot, aPersona, aMediaId, iArgs),
                "share" => OpShare(aDataRoot, aPersona, aMediaId, iArgs),
                "comic_pages" => OpComicPages(aDataRoot, aRoots.ComicRoot, aMediaId, iArgs),
                _ => SCP_CmdResult.Fail(2,
                    $"✗ 不認得的 op：`{aOp}`（吃的是 media_init｜register_reader｜note_chapter｜bookmark"
                    + "｜add_character｜revise_view｜recall｜sync_shelf｜paths"
                    + "｜scan｜comics｜comic_pages｜authored_diff｜authored_migrate｜share_body｜share）"),
            };
        }

        // ── op=paths（純讀，⛔ 零寫入）────────────────────────────────────────
        // 區塊職責：把「這些東西住在哪」印出來，讓人自己去看檔案。
        // ⚠ 印路徑的同時要印**在不在** —— 只印路徑的話，「還沒建」與「我找錯地方」同形。
        static SCP_CmdResult OpPaths(string iDataRoot, SCP_LettersRoot iLetters,
                                     string iPersona, string iMediaId)
        {
            var aR = new SCP_CmdResult();
            aR.Lines.Add("# 📚 閱讀庫路徑（唯讀，⛔ 沒有寫入任何東西）");
            void Row(string iLabel, string iPath)
                => aR.Lines.Add($"  {(File.Exists(iPath) || Directory.Exists(iPath) ? "✅" : "⬚")} {iLabel}：`{iPath}`");
            Row("資料根", iDataRoot);
            Row("信件庫根", iLetters.Value);
            Row("庫根", SCP_LibraryStore.LibraryRoot(iDataRoot));
            if (iMediaId.Length > 0)
            {
                Row("media.json", SCP_LibraryStore.MediaJsonPath(iDataRoot, iMediaId));
                if (iPersona.Length > 0)
                {
                    Row("reader.json", SCP_LibraryStore.ReaderJsonPath(iDataRoot, iMediaId, iPersona));
                    Row("閱讀卡", Path.Combine(SCP_LibraryStore.ReaderRoot(iDataRoot, iMediaId, iPersona),
                                             SCP_LibraryStore.BookshelfName));
                    Row("追回檔", SCP_LettersPaths.CmdPayload(iLetters, iPersona, "reading_recall", iMediaId));
                }
            }
            aR.Lines.Add("  ⬚ ＝ 不存在（**這不是錯誤**，只是還沒建）");
            aR.Values.Add(new KeyValuePair<string, string>("library_root", SCP_LibraryStore.LibraryRoot(iDataRoot)));
            return aR;
        }

        static SCP_CmdResult OpMediaInit(string iDataRoot, SCP_LettersRoot iLetters,
                                         string iPersona, string iMediaId, SCP_CmdArgs iArgs)
        {
            string aWorkId = iArgs.Get("work_id").Trim();
            string aTitle = iArgs.Get("title").Trim();
            if (aWorkId.Length == 0 || aTitle.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ media_init 需要 `work_id` 與 `title` —— ⛔ 都不代取",
                    "  🩸 由 media_id 反推作品身分是替作品層捏身分，而它「看起來會很正常」。");
            string aKind = iArgs.Get("media_kind").Trim();
            if (aKind.Length == 0) aKind = GuessKindFromMediaId(iMediaId);
            if (Array.IndexOf(SCP_LibraryIO.MediaKinds, aKind) < 0)
                return SCP_CmdResult.Fail(2,
                    $"✗ 不認得的 media_kind：`{aKind}`（吃的是 {string.Join("｜", SCP_LibraryIO.MediaKinds)}）",
                    "  ⚠ media_id 的**前綴**必須與它同字 —— 兩欄互為校驗。");

            int aAnticipation = 3;
            string aRaw = iArgs.Get("anticipation").Trim();
            if (aRaw.Length > 0 && !int.TryParse(aRaw, out aAnticipation))
                return SCP_CmdResult.Fail(2, $"✗ anticipation 不是整數：`{aRaw}`");

            string aLog = SCP_LibraryInit.MediaInit(iLetters, iDataRoot, aWorkId, iMediaId, aKind, iPersona,
                aTitle, iArgs.Get("title_original"), iArgs.Get("author"), aAnticipation,
                SCP_LibraryIO.SplitList(iArgs.Get("aliases")), SCP_LibraryIO.SplitList(iArgs.Get("genre_tags")),
                out string? aErr);
            if (aErr != null) return SCP_CmdResult.Fail(1, "✗ media_init：" + aErr, aLog);
            var aR = new SCP_CmdResult();
            aR.Lines.Add("# 📚 建檔");
            aR.Lines.Add(aLog.TrimEnd());
            return aR;
        }

        static SCP_CmdResult OpRegisterReader(string iDataRoot, SCP_LettersRoot iLetters,
                                              string iPersona, string iMediaId)
        {
            if (!SCP_LibraryInit.RegisterReader(iLetters, iDataRoot, iMediaId, iPersona,
                                                out bool aCreated, out string aLog, out string? aErr))
                return SCP_CmdResult.Fail(1, "✗ register_reader：" + aErr);
            var aR = new SCP_CmdResult();
            // ⚠ 「新建」與「本來就在」要分得出來 —— 兩者都成功，而它們回答的是不同的問題。
            aR.Lines.Add(aCreated ? "# 📖 已登記為讀者（**新建**）" : "# 📖 已經是讀者了（**零寫入**）");
            aR.Lines.Add(aLog.TrimEnd());
            aR.Values.Add(new KeyValuePair<string, string>("created", aCreated ? "1" : "0"));
            return aR;
        }

        static SCP_CmdResult OpNoteChapter(string iDataRoot, SCP_LettersRoot iLetters,
                                           string iPersona, string iMediaId, SCP_CmdArgs iArgs)
        {
            string aChapterId = iArgs.Get("chapter_id").Trim();
            if (!SCP_LibraryStore.IsValidChapterId(aChapterId))
                return SCP_CmdResult.Fail(2, $"✗ chapter_id 必須是四位數字：`{aChapterId}`",
                    "  ⚠ `0000` 是**序章**的保留章號，不是「第 0 章」。");
            string aBody = iArgs.Get("body");
            if (string.IsNullOrWhiteSpace(aBody))
                return SCP_CmdResult.Fail(2, "✗ note_chapter 需要 `body` —— ⛔ 不落一份空心得",
                    "  ⚠ 長文走 `--arg-file body=<檔>`，別經過 shell。");

            int aAppendRound = 0;
            string aRoundRaw = iArgs.Get("append_round").Trim();
            if (aRoundRaw.Length > 0 && !int.TryParse(aRoundRaw, out aAppendRound))
                return SCP_CmdResult.Fail(2, $"✗ append_round 不是整數：`{aRoundRaw}`");

            string? aLog = SCP_LibraryNote.NoteChapter(iLetters, iDataRoot, iMediaId, iPersona, aChapterId,
                iArgs.Get("display_number"), iArgs.Get("chapter_title"), iArgs.Get("time_range"),
                aBody, iArgs.Get("impression"), iArgs.Get("bookmark_note"),
                Truthy(iArgs.Get("append")), aAppendRound,
                out string? aRoundPath, out int aRound, out string? aErr);
            if (aLog == null) return SCP_CmdResult.Fail(1, "✗ note_chapter：" + aErr);
            var aR = new SCP_CmdResult();
            aR.Lines.Add("# 📝 章節心得已落檔");
            aR.Lines.Add(aLog.TrimEnd());
            if (aRoundPath != null) aR.AddOutput(aRoundPath);
            aR.Values.Add(new KeyValuePair<string, string>("round", aRound.ToString()));
            return aR;
        }

        static SCP_CmdResult OpBookmark(string iDataRoot, SCP_LettersRoot iLetters,
                                        string iPersona, string iMediaId, SCP_CmdArgs iArgs)
        {
            string? aLog = SCP_LibraryCharacter.Bookmark(iLetters, iDataRoot, iMediaId, iPersona,
                iArgs.Get("note"), iArgs.Get("impression"), iArgs.Get("status"), out string? aErr);
            if (aLog == null) return SCP_CmdResult.Fail(1, "✗ bookmark：" + aErr);
            var aR = new SCP_CmdResult();
            aR.Lines.Add("# 🔖 書籤");
            aR.Lines.Add(aLog.TrimEnd());
            return aR;
        }

        static SCP_CmdResult OpAddCharacter(string iDataRoot, SCP_LettersRoot iLetters,
                                            string iPersona, string iMediaId, SCP_CmdArgs iArgs)
        {
            string aId = iArgs.Get("character_id").Trim();
            string aName = iArgs.Get("name").Trim();
            string aView = iArgs.Get("view");
            if (aId.Length == 0 || aName.Length == 0 || string.IsNullOrWhiteSpace(aView))
                return SCP_CmdResult.Fail(2, "✗ add_character 需要 `character_id`／`name`／`view`");
            string? aLog = SCP_LibraryCharacter.AddCharacter(iLetters, iDataRoot, iMediaId, iPersona,
                aId, aName, iArgs.Get("name_original"), iArgs.Get("facts"), aView, out string? aErr);
            if (aLog == null) return SCP_CmdResult.Fail(1, "✗ add_character：" + aErr);
            var aR = new SCP_CmdResult();
            aR.Lines.Add("# 🧑 人物");
            aR.Lines.Add(aLog.TrimEnd());
            return aR;
        }

        static SCP_CmdResult OpReviseView(string iDataRoot, SCP_LettersRoot iLetters,
                                          string iPersona, string iMediaId, SCP_CmdArgs iArgs)
        {
            string aId = iArgs.Get("character_id").Trim();
            string aView = iArgs.Get("view");
            if (aId.Length == 0 || string.IsNullOrWhiteSpace(aView))
                return SCP_CmdResult.Fail(2, "✗ revise_view 需要 `character_id` 與 `view`");
            string? aLog = SCP_LibraryCharacter.ReviseView(iLetters, iDataRoot, iMediaId, iPersona,
                aId, aView, iArgs.Get("change_reason"), iArgs.Get("facts"), out string? aErr);
            if (aLog == null) return SCP_CmdResult.Fail(1, "✗ revise_view：" + aErr);
            var aR = new SCP_CmdResult();
            aR.Lines.Add("# 🧑 看法已 fork");
            aR.Lines.Add(aLog.TrimEnd());
            return aR;
        }

        static SCP_CmdResult OpRecall(string iDataRoot, SCP_LettersRoot iLetters,
                                      string iPersona, string iMediaId, SCP_CmdArgs iArgs)
        {
            bool aFull = iArgs.Get("full").Trim().ToLowerInvariant() != "0";
            string? aPath = SCP_LibraryRecall.WriteRecallBrief(iLetters, iDataRoot, iMediaId, iPersona,
                                                               aFull, out string? aErr);
            if (aPath == null) return SCP_CmdResult.Fail(1, "✗ recall：" + aErr);
            var aR = new SCP_CmdResult();
            aR.Lines.Add("# 📖 追回檔已生成");
            aR.Lines.Add($"  · persona `{iPersona}`　media `{iMediaId}`　full_rounds `{aFull}`");
            // ⚠ 內容**不印在這裡**：它可以很長，而回傳檔有自己的家。指到檔，讓讀的人去讀。
            aR.Lines.Add("  ⇒ **Read 那份檔**（章節 round 全文 ＋ 人物 facts 與看法版本史都在裡面）");
            aR.AddOutput(aPath);
            return aR;
        }

        static SCP_CmdResult OpSyncShelf(string iDataRoot, SCP_LettersRoot iLetters,
                                         string iPersona, string iMediaId)
        {
            SCP_LibraryBookshelf.SyncBookshelf(iLetters, iDataRoot, iMediaId, iPersona,
                                               out string? aErr, out string? aWarn);
            if (aErr != null) return SCP_CmdResult.Fail(1, "✗ sync_shelf：" + aErr);
            var aR = new SCP_CmdResult();
            aR.Lines.Add("# 🗄 閱讀卡已同步");
            // ⚠ 轉發失敗**不吞** —— 正本寫成了而副本沒有，那件事要看得見。
            if (aWarn != null) aR.Lines.Add("  ⚠ " + aWarn);
            aR.AddOutput(Path.Combine(SCP_LibraryStore.ReaderRoot(iDataRoot, iMediaId, iPersona),
                                      SCP_LibraryStore.BookshelfName));
            return aR;
        }

        /// <summary>
        /// 由 media_id 的前綴猜 media_kind。⚠ 猜不到就回空字串讓上面**擋下來**，
        /// ⛔ 不回一個預設值 —— media_kind 猜錯會讓兩欄的互相校驗從此失效。
        /// </summary>
        static string GuessKindFromMediaId(string iMediaId)
        {
            foreach (string aKind in SCP_LibraryIO.MediaKinds)
                if (iMediaId.StartsWith(aKind + "-", StringComparison.Ordinal)) return aKind;
            return "";
        }

        // ── op=scan（純讀；唯一寫入是報告檔）────────────────────────────────
        static SCP_CmdResult OpScan(string iDataRoot, SCP_CmdArgs iArgs)
        {
            string aReport = SCP_LibraryScan.ScanLibrary(iDataRoot, out string? aPath, out string? aErr,
                                                         Truthy(iArgs.Get("show_migrated")));
            var aR = new SCP_CmdResult();
            aR.Lines.Add(aReport.TrimEnd());
            // ⚠ 報告落檔失敗**不吞**，而且不讓它看起來像整支失敗 —— 印出來的那份還在。
            if (aErr != null) aR.Lines.Add("  ⚠ " + aErr);
            if (aPath != null) aR.AddOutput(aPath);
            return aR;
        }

        // ── op=comics（純讀）──────────────────────────────────────────────
        static SCP_CmdResult OpComics(string iDataRoot, SCP_PathResolution iComicRoot)
        {
            string? aError = RootError(iComicRoot, "外部漫畫庫");
            if (aError != null) return SCP_CmdResult.Fail(3, aError);
            string aRoot = iComicRoot.Value;

            List<SCP_ExternalComicSeries> aList = SCP_LibraryComics.ScanExternalComics(iDataRoot, aRoot, out string? aWarn);
            var aR = new SCP_CmdResult();
            aR.Lines.Add($"# 📚 外部漫畫庫掃描：`{aRoot}`");
            if (aWarn != null) aR.Lines.Add("  ⚠ " + aWarn);
            int aSynced = 0, aMissing = 0, aUnreg = 0;
            foreach (SCP_ExternalComicSeries s in aList)
            {
                string aMark = s.Status switch
                {
                    SCP_ComicMatchStatus.Synced => "🟢",
                    SCP_ComicMatchStatus.MissingSource => "🟡",
                    _ => "⚪",
                };
                if (s.Status == SCP_ComicMatchStatus.Synced) aSynced++;
                else if (s.Status == SCP_ComicMatchStatus.MissingSource) aMissing++;
                else aUnreg++;
                aR.Lines.Add($"  {aMark} {s.SeriesName}　`{s.MediaId}`"
                             + $"　卷 {s.Volumes.Count}／章 {s.TotalChapters}／頁 {s.TotalPages}"
                             + (s.RegisteredTitle.Length > 0 ? $"　（庫內：{s.RegisteredTitle}）" : ""));
            }
            if (aList.Count == 0) aR.Lines.Add("  （掃不到任何系列 —— 根目錄不存在或底下沒有資料夾）");
            aR.Values.Add(new KeyValuePair<string, string>("series", aList.Count.ToString()));
            aR.Values.Add(new KeyValuePair<string, string>("synced", aSynced.ToString()));
            aR.Values.Add(new KeyValuePair<string, string>("missing_source", aMissing.ToString()));
            aR.Values.Add(new KeyValuePair<string, string>("unregistered", aUnreg.ToString()));
            return aR;
        }

        // ── op=authored_diff（純讀）───────────────────────────────────────
        static SCP_CmdResult OpAuthoredDiff(string iDataRoot, SCP_CmdArgs iArgs)
        {
            string aBook = iArgs.Get("book").Trim();
            string aWorkId = iArgs.Get("work_id").Trim();
            if (aBook.Length == 0 || aWorkId.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ authored_diff 需要 `book`（舊 store slug）與 `work_id`（新 store）",
                    "  ⛔ 兩邊的 id 慣例不同，互相推導會讓「id 對不上」與「這本沒搬」同形。");

            SCP_AuthoredDiffOutcome aOutcome = SCP_LibraryAuthored.DiffWorkAuthored(
                iDataRoot, aBook, aWorkId, out List<string> aMismatch, out string aReport);
            var aR = new SCP_CmdResult();
            aR.Lines.Add($"# 🔎 寫書線逐欄對拍：**{aOutcome}**");
            aR.Lines.Add(aReport.TrimEnd());
            aR.Values.Add(new KeyValuePair<string, string>("outcome", aOutcome.ToString()));
            aR.Values.Add(new KeyValuePair<string, string>("mismatched", string.Join(",", aMismatch)));
            // ⚠ 判定用 Values 交出去，⛔ 不靠 exit code 分級：
            //   `AllMatch` 之外有七種各自不同的成因，壓成一個非零碼就等於把它們講成同一件事。
            return aR;
        }

        // ── op=authored_migrate（預設 dry-run）────────────────────────────
        static SCP_CmdResult OpAuthoredMigrate(string iDataRoot, SCP_CmdArgs iArgs)
        {
            string aBook = iArgs.Get("book").Trim();
            string aWorkId = iArgs.Get("work_id").Trim();
            if (aBook.Length == 0 || aWorkId.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ authored_migrate 需要 `book` 與 `work_id`");

            bool aConfirm = Truthy(iArgs.Get("confirm"));
            SCP_AuthoredMigrateOutcome aOutcome = SCP_LibraryAuthored.MigrateAuthoredWork(
                iDataRoot, aBook, aWorkId, aConfirm, out string aReport, out string? aErr);
            if (aErr != null) return SCP_CmdResult.Fail(1, $"✗ authored_migrate（{aOutcome}）：{aErr}", aReport);
            var aR = new SCP_CmdResult();
            aR.Lines.Add($"# 🚚 寫書線搬遷：**{aOutcome}**");
            aR.Lines.Add(aReport.TrimEnd());
            aR.Values.Add(new KeyValuePair<string, string>("outcome", aOutcome.ToString()));
            return aR;
        }

        // ── op=share_body（純讀 —— ⛔ 本支不發文）──────────────────────────
        static SCP_CmdResult OpShareBody(string iDataRoot, string iPersona, string iMediaId, SCP_CmdArgs iArgs)
        {
            string aChapterId = iArgs.Get("chapter_id").Trim();
            if (aChapterId.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ share_body 需要 `chapter_id`");
            int aRound = 0;
            string aRaw = iArgs.Get("round").Trim();
            if (aRaw.Length > 0 && !int.TryParse(aRaw, out aRound))
                return SCP_CmdResult.Fail(2, $"✗ round 不是整數：`{aRaw}`");

            string? aBody = SCP_LibraryShare.BuildShareBody(iDataRoot, iMediaId, iPersona, aChapterId,
                                                            ref aRound, out string? aErr);
            if (aBody == null) return SCP_CmdResult.Fail(1, "✗ share_body：" + aErr);
            var aR = new SCP_CmdResult();
            aR.Lines.Add($"# 📖 發文內文（r{aRound}）—— ⛔ 本支**沒有發文**，只是把要貼的東西組出來");
            aR.Lines.Add(aBody);
            aR.Values.Add(new KeyValuePair<string, string>("round", aRound.ToString()));
            return aR;
        }

        // ── op=comic_pages（純讀）─────────────────────────────────────────
        // 區塊職責：回答「這部漫畫（這一話）的頁檔實際在哪」，讓漫畫閱讀不必自己拼路徑或呼叫 python（TASK-0400）。
        // 物理意義：內部漫畫（ArtGallery/Comic）優先；不是內部才走外部漫畫庫根（`SCP_PathId.ComicRoot`）。
        // 數值影響：純讀。外部根沒設／解不出來只影響外部漫畫，⛔ 不擋內部漫畫。
        static SCP_CmdResult OpComicPages(string iDataRoot, SCP_PathResolution iComicRoot, string iMediaId,
                                          SCP_CmdArgs iArgs)
        {
            string? aRoot = iComicRoot.Error == null ? iComicRoot.Value : null;
            string aRootNote = iComicRoot.Error == null ? "" : "\n  ⚠ 外部漫畫庫根：" + iComicRoot.Error;
            string aChapterId = iArgs.Get("chapter_id").Trim();

            if (aChapterId.Length == 0)
            {
                SCP_LibraryComics.SCP_ComicChapterIndex? aIndex =
                    SCP_LibraryComics.ListChapterIds(iDataRoot, aRoot, iMediaId, out string? aIdxErr);
                if (aIndex == null) return SCP_CmdResult.Fail(1, "✗ comic_pages：" + aIdxErr + aRootNote);
                var aList = new SCP_CmdResult();
                aList.Lines.Add($"# 📚 `{iMediaId}` 的話（{(aIndex.Source == "internal" ? "內部 ArtGallery/Comic" : "外部漫畫庫")}）");
                if (aIndex.ChapterIds.Count == 0) aList.Lines.Add("  （沒有任何話）");
                else aList.Lines.Add($"  共 {aIndex.ChapterIds.Count} 話：{aIndex.ChapterIds[0]} … "
                                     + aIndex.ChapterIds[aIndex.ChapterIds.Count - 1]);
                aList.AddValue("source", aIndex.Source);
                aList.AddValue("chapters", aIndex.ChapterIds.Count.ToString());
                return aList;
            }

            if (!SCP_LibraryStore.IsValidChapterId(aChapterId))
                return SCP_CmdResult.Fail(2, "✗ comic_pages 的 `chapter_id` 須為四位數字：`" + aChapterId + "`");
            SCP_LibraryComics.SCP_ComicChapterPages? aPages =
                SCP_LibraryComics.ListChapterPages(iDataRoot, aRoot, iMediaId, aChapterId, out string? aErr);
            if (aPages == null) return SCP_CmdResult.Fail(1, "✗ comic_pages：" + aErr + aRootNote);

            var aR = new SCP_CmdResult();
            aR.Lines.Add($"# 📚 `{iMediaId}` 第 {aChapterId} 話（{(aPages.Source == "internal" ? "內部：分鏡稿＋畫稿" : "外部漫畫庫")}）");
            if (aPages.Source == "internal")
                aR.Lines.Add($"  📝 分鏡稿：`{aPages.ChapterPath}`（正文在這裡，圖逐張看）");
            else
                aR.Lines.Add($"  📂 章資料夾：`{aPages.ChapterPath}`");
            int aMissing = 0;
            foreach (SCP_LibraryComics.SCP_ComicPage p in aPages.Pages)
            {
                if (!p.Exists) aMissing++;
                aR.Lines.Add($"  {(p.Exists ? "✅" : "⬚ 缺檔")} {p.Path}");
            }
            if (aPages.Pages.Count == 0)
                aR.Lines.Add("  ⚠ 這一話找不到任何頁檔 —— ⛔ 沒有頁就沒有可看的東西，不要憑空寫心得");
            if (aMissing > 0)
                aR.Lines.Add($"  ⚠ {aMissing} 頁在磁碟上不存在 —— 不是「這話只有 {aPages.Pages.Count - aMissing} 頁」，是有頁掉了");
            aR.AddValue("source", aPages.Source);
            aR.AddValue("pages", aPages.Pages.Count.ToString());
            aR.AddValue("missing_pages", aMissing.ToString());
            return aR;
        }

        // ── op=share（發酒館 ＋ 回寫 shared_seq）──────────────────────────────
        // 區塊職責：把某章某 round 的心得發進酒館，並把 seq 落回該 round 當 receipt（Unity 版 Op_Share 的對應，TASK-0399）。
        // 物理意義：組稿共用 op=share_body 同一份（SCP_LibraryShare.BuildShareBody）；發文走宿主登記的發文閘
        //          （Senate＝Senate 組訊息＋酒館 Server 寫入，計酬記在 persona 上；房間固定 tavern）。
        //          ⛔ 本層不自己組訊息、不碰錢。
        // 數值影響：同 round 已有 shared_seq ⇒ 拒發（BuildShareBody 擋，防重複計酬）。四態各自處置：
        //          Posted＝落 receipt／Queued＝已排隊、沒有 seq、⛔ 不要補發／Unresolved＝exit 7 先回讀／NotPosted＝exit 6 補發安全。
        //          發文失敗不回滾任何檔（檔優先於投影）。
        const int ExitNotPosted = 6;
        const int ExitUnresolved = 7;

        static SCP_CmdResult OpShare(string iDataRoot, string iPersona, string iMediaId, SCP_CmdArgs iArgs)
        {
            string aChapterId = iArgs.Get("chapter_id").Trim();
            if (!SCP_LibraryStore.IsValidChapterId(aChapterId))
                return SCP_CmdResult.Fail(2, "✗ share 需要四位數字的 `chapter_id`：`" + aChapterId + "`");
            int aRound = 0;
            string aRaw = iArgs.Get("round").Trim();
            if (aRaw.Length > 0 && !int.TryParse(aRaw, out aRound))
                return SCP_CmdResult.Fail(2, $"✗ round 不是整數：`{aRaw}`");

            string? aBody = SCP_LibraryShare.BuildShareBody(iDataRoot, iMediaId, iPersona, aChapterId,
                                                            ref aRound, out string? aErr);
            // ⛔ 沒發 ⇒ 沒有任何東西被寫：exit 1（閘擋下，零寫入），⚠ 跟發文後的 6／7 不同形。
            if (aBody == null) return SCP_CmdResult.Fail(1, "✗ share：" + aErr);

            string aTag = $"{iMediaId} / {aChapterId} r{aRound} by {iPersona}";
            SCP_ITavernPostGateway? aGate = SCP_TavernPostGatewayHost.Create(iDataRoot);
            if (aGate == null)
            {
                // ⚠ 沒登記閘 ≠ 發出去了：兩件事必須不同形。
                SCP_CmdResult aNoGate = SCP_CmdResult.Fail(ExitNotPosted,
                    $"✗ share（{aTag}）：**本宿主沒有登記發文閘 ⇒ 這一則沒有發出去**（補發安全）");
                aNoGate.AddValue("posted", "0");
                return aNoGate;
            }

            var aMeta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tag"] = "reading-note",
                ["category"] = "reading",
            };
            var aR = new SCP_CmdResult();
            aR.Lines.Add("# 📚 Library share");
            SCP_TavernPostVerdict aVerdict;
            try
            {
                aVerdict = aGate.Post(iPersona, aBody, aMeta, aR.Lines);
            }
            catch (Exception e)
            {
                // 例外時不知道寫入端做到哪一步 ⇒ 當「不知道」處理（⛔ 不當成「確定沒發」去補發）。
                aR.Lines.Add("✗ 發文丟出例外：" + e.GetType().Name + ": " + e.Message);
                aR.Lines.Add("  ⇒ 不知道有沒有發出去：⛔ **先回讀酒館再決定要不要重新 share**（重發會重複計酬）。");
                aR.ExitCode = ExitUnresolved;
                aR.AddValue("posted", "unknown");
                return aR;
            }

            // ⛔ 逐態寫明，不靠預設分支：新的一態不准安靜落進「確定沒發」。
            switch (aVerdict.Outcome)
            {
                case SCP_TavernPostOutcome.Posted:
                    return ShareReceipt(iDataRoot, iPersona, iMediaId, aChapterId, aRound, aTag, aVerdict, aR);

                case SCP_TavernPostOutcome.Queued:
                    aR.Lines.Add($"- 📥 酒館發文已排隊（{aTag}）：{aVerdict.Detail}");
                    aR.Lines.Add("> ⚠ 還沒有 seq ⇒ receipt（shared_seq）**這一趟沒落**。⛔ **不要重新 share**"
                                 + "（排著的那一則送出時會多一則、領兩次錢）；送出後要補 receipt，回讀酒館拿 seq 再人工補進該 round。");
                    aR.AddValue("posted", "queued");
                    aR.AddValue("receipt", "0");
                    if (aVerdict.QueuedCmdId.Length > 0) aR.AddValue("post_queued_cmd_id", aVerdict.QueuedCmdId);
                    return aR;

                case SCP_TavernPostOutcome.Unresolved:
                    aR.Lines.Add($"✗ share（{aTag}）：**不知道有沒有發出去** —— {aVerdict.Detail}");
                    aR.Lines.Add("  ⛔ 先回讀再決定要不要重新 share（重發會重複計酬）：");
                    aR.Lines.Add("     " + (aVerdict.RecheckHint.Length > 0
                        ? aVerdict.RecheckHint
                        : SCP_CmdRegistry.Invoke("tavern-query --arg kind=tail --arg room=tavern")));
                    aR.ExitCode = ExitUnresolved;
                    aR.AddValue("posted", "unknown");
                    return aR;

                case SCP_TavernPostOutcome.NotPosted:
                    aR.Lines.Add($"✗ share（{aTag}）：**確定沒發** —— {aVerdict.Detail}");
                    aR.Lines.Add("  心得檔不受影響；修好之後重新 share 即可（補發安全）。");
                    aR.ExitCode = ExitNotPosted;
                    aR.AddValue("posted", "0");
                    return aR;

                default:
                    aR.Lines.Add($"✗ share（{aTag}）：**認不得的判定 {aVerdict.Outcome}** —— ⛔ 當成不知道處理，先回讀、別補發。");
                    aR.ExitCode = ExitUnresolved;
                    aR.AddValue("posted", "unknown");
                    return aR;
            }
        }

        // 區塊職責：發文已確認（拿得到 seq）之後落 receipt，並照實報告 receipt 有沒有落。
        // ⚠ receipt 沒落 ⇒ 仍是 exit 0（文已經發了，非 0 會誘導重跑＝重複計酬），改用 Values 與警告行大聲說。
        static SCP_CmdResult ShareReceipt(string iDataRoot, string iPersona, string iMediaId, string iChapterId,
                                          int iRound, string iTag, SCP_TavernPostVerdict iVerdict, SCP_CmdResult ioResult)
        {
            ioResult.AddValue("posted", "1");
            if (!int.TryParse(iVerdict.Seq, out int aSeq) || aSeq <= 0)
            {
                ioResult.Lines.Add($"- ✅ 已發酒館（{iTag}），但閘回的 seq 讀不懂：`{iVerdict.Seq}`");
                ioResult.Lines.Add("> ⚠ receipt **沒落**。⛔ 別重發（會重複計酬）；回讀酒館拿 seq 再人工補進該 round。");
                ioResult.AddValue("receipt", "0");
                return ioResult;
            }
            SCP_LibraryShare.RecordSharedSeq(iDataRoot, iMediaId, iPersona, iChapterId, iRound, aSeq, out string? aRecErr);
            ioResult.AddValue("post_seq", aSeq.ToString());
            ioResult.Lines.Add($"- ✅ 已發酒館：seq={aSeq}（{iTag}）");
            if (string.IsNullOrEmpty(aRecErr))
            {
                ioResult.Lines.Add($"- receipt：`shared_seq={aSeq}` 已落 chapter.json round {iRound}");
                ioResult.AddValue("receipt", "1");
            }
            else
            {
                ioResult.Lines.Add($"> ⚠ 已發文（seq={aSeq}）但 receipt 落檔失敗：{aRecErr}");
                ioResult.Lines.Add($"> 請人工把 shared_seq={aSeq} 補進該 round —— 別重發（會重複計酬）。");
                ioResult.AddValue("receipt", "0");
            }
            return ioResult;
        }

        // 區塊職責：阻止設定錯誤變成寫入錯樹；不建立或修補根目錄。
        static string? RootError(SCP_PathResolution iRoot, string iLabel)
        {
            if (iRoot.Error != null) return "✗ " + iLabel + "設定無法解析：" + iRoot.Error;
            if (string.IsNullOrWhiteSpace(iRoot.Value) || !Path.IsPathRooted(iRoot.Value)
                || !Directory.Exists(iRoot.Value))
                return "✗ " + iLabel + "必須是已存在的絕對路徑：" + iRoot.Value;
            return null;
        }

        static bool Truthy(string? iValue)
        {
            string v = (iValue ?? "").Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }
    }
}
