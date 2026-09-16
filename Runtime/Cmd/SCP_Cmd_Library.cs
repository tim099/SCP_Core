// 區塊職責：閱讀庫的 **Senate／宿主中立入口** —— 把 `SCP.Core.Library` 那一層掛上 Cmd 系統。
// 物理意義：TASK-0166 ② 要的「第二個入口」在今天之前**不存在**（`SCP_Core/Runtime/Cmd/` 只有
//           `SCP_Cmd_Book`）⇒ 那一格不是「還沒量」，是**結構上量不了**。本檔讓它變成可量。
//   ⭐ 而它跟 Editor 端 `Cmd_Library` 是**兩個入口讀寫同一份資料**，不是兩套實作 ——
//     兩邊的本體都是 `SCP.Core.Library`（Editor 那側要等薄殼落地，見 PortNote）。
// 數值影響：所有寫入都落在 `<data_root>/BookNotes/Library/` 與 `<letters_root>/<persona>/`；
//           本檔自己不解析任何路徑，一律走 `SCP_LibraryStore` / `SCP_LettersPaths`。
// ⚠ **本 Cmd 是 Native**：它不派給任何人，Editor 沒開也跑得完。
//   ⛔ 別因為 Editor 端有一支同名的 op 就以為要委派 —— 委派回去等於繞一圈寫同一個檔。
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Library;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Library : SCP_Cmd
    {
        public override string Name => "library";

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
            SCP_CmdRegistry.Invoke("library --arg data_root=<AgentCommands> --arg letters_root=<letters>"
                                   + " --arg op=recall --arg media_id=<id> --arg persona=<誰>");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）—— 閱讀卡與追回檔的落點",
                               iRequired: true),
            new SCP_CmdArgSpec("op", "media_init｜register_reader｜note_chapter｜bookmark｜add_character"
                                     + "｜revise_view｜recall｜sync_shelf｜paths（預設 paths＝純讀）"),
            new SCP_CmdArgSpec("persona", "讀者 persona（除了 paths 之外都必填）"),
            new SCP_CmdArgSpec("media_id", "媒材 id（除了 paths 之外都必填）"),
            new SCP_CmdArgSpec("work_id", "op=media_init 用：作品 id（必填）"),
            new SCP_CmdArgSpec("media_kind", "op=media_init 用：comic｜anim｜film｜series｜stream｜book"),
            new SCP_CmdArgSpec("title", "op=media_init 用：作品名（必填）"),
            new SCP_CmdArgSpec("title_original", "op=media_init 用：原文名"),
            new SCP_CmdArgSpec("author", "op=media_init 用：作者／監督"),
            new SCP_CmdArgSpec("anticipation", "op=media_init 用：期待度 0-5（預設 3＝中性，"
                                               + "**工具不替本人表態**）"),
            new SCP_CmdArgSpec("aliases", "op=media_init 用：別名，`|` 或 `,` 分隔"),
            new SCP_CmdArgSpec("genre_tags", "op=media_init 用：類型標籤，分隔同上"),
            new SCP_CmdArgSpec("chapter_id", "op=note_chapter 用：四位數章號（必填；`0000`＝序章）"),
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
            string aDataRoot = iArgs.Get("data_root").Trim();
            if (!Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(2, "✗ 資料根不存在：" + aDataRoot);
            string aLettersRaw = iArgs.Get("letters_root").Trim();
            if (aLettersRaw.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ 缺 `letters_root` —— 閱讀卡與追回檔要落在那棵樹上，⛔ 不從資料根推導",
                    "  🩸 推導出來的根會靜默指錯樹：檔案照樣寫得出來，而沒有人看得到它們。");
            var aLetters = new SCP_LettersRoot(aLettersRaw);

            // ⭐ 預設是**純讀**那一支 —— 打錯 op 的代價要是「什麼都沒發生」，不是「建了一部作品」。
            string aOp = iArgs.Get("op").Trim();
            if (aOp.Length == 0) aOp = "paths";

            string aPersona = iArgs.Get("persona").Trim();
            string aMediaId = iArgs.Get("media_id").Trim();
            if (aOp != "paths" && (aPersona.Length == 0 || aMediaId.Length == 0))
                return SCP_CmdResult.Fail(2, $"✗ op=`{aOp}` 需要 `persona` 與 `media_id` —— 兩者都不代取",
                    "  ⚠ 讀可以跨 persona，**寫只寫自己**：身分猜錯是把心得記到別人頭上。");

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
                _ => SCP_CmdResult.Fail(2,
                    $"✗ 不認得的 op：`{aOp}`（吃的是 media_init｜register_reader｜note_chapter｜bookmark"
                    + "｜add_character｜revise_view｜recall｜sync_shelf｜paths）"),
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

        static bool Truthy(string? iValue)
        {
            string v = (iValue ?? "").Trim().ToLowerInvariant();
            return v == "1" || v == "true" || v == "yes" || v == "on";
        }
    }
}
