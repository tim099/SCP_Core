// 區塊職責：`cmd book` —— 書本筆記庫（`BookNotes/<slug>/`）的建檔（**原生**，不需要 Unity）。
// 物理意義：這是 `library.py add-book` 的移植（TASK-0143 ②-bis 那條線的第一刀）。
//           落點是**舊 store**（`BookNotes/<slug>/book.json` ＋ `chapters/`／`characters/`），
//           ⛔ 不是 `BookNotes/Library/work|media/` 那個新 store ——
//           兩者名字近而住在不同目錄，`Cmd_Library.media_init` 也**沒有 origin=authored 的概念**
//           （2026-09-06 對拍，見 TASK-0143 工作記憶）。⇒ 這一支不是它的重複實作。
//
// ⚠ **本支不搬遷資料、不改寫既有書的元資料。** 舊 store 裡有兩本**別人正在寫**的 authored 書
//   （@gura《深海對拍錄》／@Sirius《熄燈前的燈》）—— 新增章節／階段大綱會寫進**該書自己的目錄**，
//   已存在的書本身不覆寫（`op=add` 撞名照樣拒絕，跟 python 同行為）。
//
// 🩸 ②-bis 拍板（Tim 2026-09-07）＝ **(a) 先搬 authored 線再退 python**，
//   而「搬」的**實際形狀是搬寫入端，不是搬資料** —— 那一格是量出來的，不是條文寫的：
//   條文寫「C# 那側零檔案 ⇒ 兩本是孤兒」，而那個 0 量的是 `BookNotes/Library/`（**另一個 store**）。
//   C# authored 線的 store 就是 `BookNotes/<slug>/book.json`，兩本書本來就住在那裡
//   ⇒ `senate cmd book op=writing` 當場讀出 2 本（實測 2026-09-07 18:5x）。
//   ⇒ 所以資料一個位元組都不必動；缺的是 `log-chapter` / `arc` 這兩個**寫入端**（本次補上）。
//
// ⚠ `--reader` 分支路由**不是 authored 線專用**：漫畫／動畫那側的 `branches/<reader>/` 是同事
//   天天在寫的（`comic-delicious-in-dungeon/branches/{gura,Sirius}`、`anim-apocalypse-hotel/branches/calli`、
//   `summit-masthead-bet/branches/gura` 四份活的）⇒ 只移植 main 那半就會**靜默**咬到他們的讀書筆記。
//   本檔把 main 與 branch 兩條路一起移植（含首次啟用分支時的 `book.json` 自動 init）。
//
// 🩸 兩個「原文那個語言免費給我」的保證，這裡都**量過**才敢依賴（TASK-0143 判準③）：
//   ① python `dict` 保證插入序，而 `data["status"] = "writing"` 覆寫既有 key **不移動位置**
//      ⇒ 真檔的欄位序是 `… status … characters, origin, author_persona, publish_status`。
//      `SCP_JsonData.Set` 對既有 key 同樣不動位置（讀過實作），所以照抄得出來。
//   ② python `_atomic_write` 是**文字模式** ⇒ 在 Windows 上 `\n` 會變 CRLF。
//      實測既有三份 `book.json`：CRLF 數 == 換行總數（21/21、20/20、20/20）——
//      ⇒ 這裡必須寫 CRLF，寫 LF 會**內容一樣而逐位元組不同**，且沒有任何一層會喊。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Books;
using SCP.Core.Io;
using SCP.Core.Json;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Book : SCP_Cmd
    {
        public override string Name => "book";

        public override string Summary =>
            "書本筆記庫：開新書（`op=add`）／記一章（`op=log-chapter`）／記階段大綱（`op=arc`）"
            + "／列出寫到一半的書（`op=writing`）—— **本地跑，不需要 Editor**";

        public override string Details =>
            "⭐ `op=log-chapter` 寫 `<book>/chapters/ch<NN>_<slug>.md` ＋ 把 `book.json` 的\n"
            + "  `progress.current_chapter` **只往前推**（比現況小的章號不會倒退書籤）。\n"
            + "⭐ `op=arc` 寫 `<book>/arcs/arc_<範圍>.md` ＋在 `book.json` 的 `arcs[]` 登記；\n"
            + "  同一個 `chapters` 範圍**取代**舊那筆（並移到陣列尾端）。\n"
            + "⭐ `--arg reader=` 非初始讀者 ⇒ 自動走 `branches/<reader>/`，首次啟用時 init 分支\n"
            + "  `book.json`（帶 `continue_from` 則只複製對方的章號當起點，**不寫回來源**）。\n"
            + "⭐ `origin=authored` ＝ 自由時間**寫書**（作者＝該 persona）：多帶 `author_persona`／\n"
            + "  `publish_status=draft`，且 `status` 由 `reading` 換成 `writing`（欄位**留在原位**）。\n"
            + "  `origin=imported` ＝ 調入別人的書。兩者都不帶 ⇒ 沿用現況（舊書照常）。\n"
            + "⛔ **書已存在就拒絕**（不覆寫、不合併）—— 覆寫一本正在寫的書是不可逆的。\n"
            + "⚠ 落點是**舊 store**（`BookNotes/<slug>/`），與 `Cmd_Library.media_init` 的\n"
            + "  `BookNotes/Library/work|media/` 是**兩個不同的 store**，不要混用。\n"
            + "⚠ 產物逐位元組對齊 `library.py add-book`：JSON 縮排 2 空格、非 ASCII 不轉義、\n"
            + "  換行 **CRLF**（python 文字模式在 Windows 的結果）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("book --arg data_root=<AgentCommands> --arg op=add"
                                   + " --arg title=<書名> --arg aliases=<別名;別名>");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("op", "add（建一本新書）｜log-chapter（記一章）｜arc（記階段大綱）"
                               + "｜writing（列出寫到一半的書，**純讀**）"
                               + " —— 預設 writing，⭐ 純讀的那個當預設"),
            new SCP_CmdArgSpec("persona", "op=writing 用：只看這位作者的書（省略＝全部作者）"),
            new SCP_CmdArgSpec("book", "op=log-chapter／arc 用：書本 slug（必填）"),
            new SCP_CmdArgSpec("chapter", "op=log-chapter 用：章號，整數（必填）"),
            new SCP_CmdArgSpec("slug", "op=log-chapter 用：章節檔名 slug（省略＝由 title 生成，再省略＝ch<N>）"),
            new SCP_CmdArgSpec("summary", "內容摘要／階段大綱（省略＝「（待補）」）"),
            new SCP_CmdArgSpec("events", "op=log-chapter 用：關鍵事件，用 `;` `|` 或換行分隔"),
            new SCP_CmdArgSpec("views", "op=log-chapter 用：本章對人物的新認識，分隔同上"),
            new SCP_CmdArgSpec("new_characters", "op=log-chapter 用：本章新出場人物，分隔同上"),
            new SCP_CmdArgSpec("foreshadow", "op=log-chapter 用：伏筆／待解，分隔同上（省略＝「（無）」）"),
            new SCP_CmdArgSpec("chapters", "op=arc 用：涵蓋章節範圍，如 `1-6`（必填）"),
            new SCP_CmdArgSpec("threads", "op=arc 用：貫穿線索／伏筆狀態，分隔同上"),
            new SCP_CmdArgSpec("reader", "讀者 persona；非初始讀者 ⇒ 自動走 `branches/<reader>/` 分支筆記"
                               + "（不影響初始讀者）"),
            new SCP_CmdArgSpec("continue_from", "首次開分支時：從誰的進度接續（只複製章號，不寫回來源）"),
            new SCP_CmdArgSpec("title", "書名（必填）"),
            new SCP_CmdArgSpec("aliases", "別名，用 `;` `|` 或換行分隔；"
                               + "**使用者提供的書名必須含在裡面**（必填）"),
            new SCP_CmdArgSpec("id", "書本 slug（省略＝由 title 生成）"),
            new SCP_CmdArgSpec("title_original", "原文書名"),
            new SCP_CmdArgSpec("author", "作者（人類作者名，字串）"),
            new SCP_CmdArgSpec("reader_persona", "讀者 persona（省略＝author_persona，再省略＝basecamp）"),
            new SCP_CmdArgSpec("origin", "authored（原創寫書）｜imported（調入別人的書）；省略＝沿用現況"),
            new SCP_CmdArgSpec("author_persona", "原創書作者 persona（origin=authored 時；預設＝reader_persona）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root").Trim();
            string aOp = iArgs.Get("op").Trim();
            // ⭐ 預設是**純讀**那一支 —— 打錯 op 的代價要是「什麼都沒發生」，不是「建了一本書」。
            if (aOp.Length == 0) aOp = "writing";
            if (!Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(2, "✗ 資料根不存在：" + aDataRoot);
            return aOp switch
            {
                "add" => OpAdd(aDataRoot, iArgs),
                "log-chapter" => OpLogChapter(aDataRoot, iArgs),
                "arc" => OpArc(aDataRoot, iArgs),
                "writing" => OpWriting(aDataRoot, iArgs),
                _ => SCP_CmdResult.Fail(2,
                    $"✗ 不認得的 op：`{aOp}`（吃的是 add｜log-chapter｜arc｜writing）"),
            };
        }

        // ── op=writing ────────────────────────────────────────────────────────
        // 區塊職責：列出「寫到一半」的書（`origin=authored` 且尚未發布）。
        // 🩸 讀不到的時候**不准印「0 本」** —— 「沒有人在寫書」與「我沒去看」在畫面上同形，
        //    而人往那個空格裡填的一定是「沒事」。
        static SCP_CmdResult OpWriting(string iDataRoot, SCP_CmdArgs iArgs)
        {
            string aPersona = iArgs.Get("persona").Trim();

            if (!SCP_BookStore.TryListWriting(iDataRoot, aPersona.Length > 0 ? aPersona : null,
                                              out List<SCP_AuthoredBook> aBooks, out string aWhy))
            {
                return SCP_CmdResult.Fail(2,
                    "⚠ **未量** —— 書庫讀不到（" + aWhy + "）。",
                    "　 ⛔ 這不是「沒有人在寫書」：那兩件事在畫面上同形，而這一行是它們唯一分得開的地方。");
            }

            if (aBooks.Count == 0)
            {
                var aNone = SCP_CmdResult.Success(
                    aPersona.Length > 0
                        ? $"✅ @{aPersona} 目前沒有寫到一半的書（authored 且未發布：0 本）"
                        : "✅ 目前沒有任何寫到一半的書（authored 且未發布：0 本）",
                    "　 讀自：" + SCP_BookStore.BookNotesRoot(iDataRoot));
                aNone.AddValue("writing", "0");
                return aNone;
            }

            var aResult = SCP_CmdResult.Success(
                $"✍ 寫到一半的書 **{aBooks.Count}** 本"
                + (aPersona.Length > 0 ? $"（只看 @{aPersona}）" : "（全部作者）"));
            foreach (SCP_AuthoredBook aBook in aBooks)
            {
                aResult.Lines.Add(
                    $"  《{aBook.Title}》  作者 @{aBook.AuthorPersona}"
                    // ⭐ 兩層分開印 —— 只印一個數的話，「正文 1 章而草稿 0 篇」會被寫成「0 章」。
                    + $"  正文 {aBook.ProseCount} 章／草稿筆記 {aBook.NoteCount} 篇  status={aBook.Status}"
                    + $"  publish={aBook.PublishStatus}");
                // ⭐ 每一列說得出它是從哪個檔讀來的 —— 沒有出處的值救不了人。
                aResult.Lines.Add(
                    $"      最後更動 {aBook.LastWriteUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
                    + $"　讀自 `{aBook.SourcePath}`");
            }
            aResult.AddValue("writing", aBooks.Count.ToString(CultureInfo.InvariantCulture));
            return aResult;
        }

        // ── op=add ────────────────────────────────────────────────────────────
        static SCP_CmdResult OpAdd(string iDataRoot, SCP_CmdArgs iArgs)
        {
            string aTitle = iArgs.Get("title").Trim();
            if (aTitle.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ 缺 `title` —— 書名不代取。");

            // ⛔ aliases 在 python 那側是 required：使用者說出口的那個書名必須留下來，
            //    否則下次用同一個名字找這本書會「找不到」，而那是靜默的。
            string aAliasesRaw = iArgs.Get("aliases");
            if (aAliasesRaw.Trim().Length == 0)
                return SCP_CmdResult.Fail(2,
                    "✗ 缺 `aliases` —— 使用者提供的書名必須放進來（否則之後用那個名字找不到這本書）。");

            string aTitleOriginal = iArgs.Get("title_original").Trim();
            string aAuthor = iArgs.Get("author").Trim();
            string aOrigin = iArgs.Get("origin").Trim();
            string aAuthorPersona = iArgs.Get("author_persona").Trim();
            string aReaderPersona = iArgs.Get("reader_persona").Trim();

            if (aOrigin.Length > 0 && aOrigin != "authored" && aOrigin != "imported")
                return SCP_CmdResult.Fail(2,
                    $"✗ `origin` 只吃 authored｜imported（收到 `{aOrigin}`）。");

            string aSlug = iArgs.Get("id").Trim();
            if (aSlug.Length == 0) aSlug = Slugify(aTitle);

            // ⛔ 不在這裡再寫一次 "BookNotes" 字面 —— 根的解析只留 SCP_BookStore 一處。
            string aBookDir = Path.Combine(SCP_BookStore.BookNotesRoot(iDataRoot), aSlug);
            string aBookJson = Path.Combine(aBookDir, "book.json");

            // ⛔ 已存在就停 —— 與 python 同行為（exit 1、不覆寫）。
            //   一本正在寫的書被覆寫是不可逆的，而它的失敗樣子是「檔還在、內容變成空的」。
            if (File.Exists(aBookJson))
                return SCP_CmdResult.Fail(1, $"⚠ 書已存在: {aSlug}（不覆寫）", "   " + aBookDir);

            string aReader = aReaderPersona;
            if (aReader.Length == 0) aReader = aAuthorPersona;
            if (aReader.Length == 0) aReader = "basecamp";

            var aData = SCP_JsonData.NewObject();
            aData.Set("id", SCP_JsonData.NewString(aSlug));
            aData.Set("title", SCP_JsonData.NewString(aTitle));
            aData.Set("title_original", SCP_JsonData.NewString(aTitleOriginal));
            aData.Set("author", SCP_JsonData.NewString(aAuthor));
            aData.Set("aliases", BuildAliases(aTitle, aAliasesRaw, aTitleOriginal));
            aData.Set("reader_persona", SCP_JsonData.NewString(aReader));
            aData.Set("status", SCP_JsonData.NewString("reading"));

            var aProgress = SCP_JsonData.NewObject();
            aProgress.Set("current_chapter", SCP_JsonData.NewNumber(0));
            aProgress.Set("last_read", SCP_JsonData.NewString(Today()));
            aData.Set("progress", aProgress);

            aData.Set("characters", SCP_JsonData.NewArray());

            if (aOrigin == "authored")
            {
                aData.Set("origin", SCP_JsonData.NewString("authored"));
                aData.Set("author_persona",
                          SCP_JsonData.NewString(aAuthorPersona.Length > 0 ? aAuthorPersona : aReader));
                aData.Set("publish_status", SCP_JsonData.NewString("draft"));
                // ⭐ 覆寫既有 key ⇒ 位置**不動**（留在 characters 之前），與 python dict 同。
                aData.Set("status", SCP_JsonData.NewString("writing"));
            }
            else if (aOrigin == "imported")
            {
                aData.Set("origin", SCP_JsonData.NewString("imported"));
            }

            Directory.CreateDirectory(Path.Combine(aBookDir, "chapters"));
            Directory.CreateDirectory(Path.Combine(aBookDir, "characters"));

            string aText = SCP_JsonWriter.Write(aData, iIndented: true, iIndent: "  ") + "\n";
            SCP_TextFile.WriteCrLf(aBookJson, aText);

            string aKind = aOrigin == "authored" ? "✍ 原創書(草稿)" : "📖 書";
            string aAuthorTail = aOrigin == "authored"
                ? "  作者: " + aData["author_persona"].AsString()
                : "";
            var aResult = SCP_CmdResult.Success(
                $"✅ 建立{aKind}: {aSlug}  《{aTitle}》 / {(aAuthor.Length > 0 ? aAuthor : "?")}{aAuthorTail}",
                "   " + aBookDir);
            if (aOrigin == "authored")
                aResult.Lines.Add("   → 用 UCL_BookEditPage 寫章節; 完稿後跑 publish --book 發布入庫");
            aResult.AddValue("book", aSlug);
            return aResult;
        }

        // ── op=log-chapter ────────────────────────────────────────────────────
        // 區塊職責：記一章筆記 —— 寫 `<book>/chapters/ch<NN>_<slug>.md` ＋ bump `book.json` 的 progress。
        // 物理意義：移植 `library.py cmd_log_chapter`（②-bis 拍板 (a) 缺的那個寫入端）。
        // 🩸 逐位元組對拍的代價寫在 Frontmatter 那支：python `f"{k}: {v}"` 在空值時會留一個
        //    **尾隨空格**（`title: `），而「差一個空白」跟「內容不同」在畫面上同形。
        static SCP_CmdResult OpLogChapter(string iDataRoot, SCP_CmdArgs iArgs)
        {
            string aBook = iArgs.Get("book").Trim();
            if (aBook.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ 缺 `book` —— 書名不代取。");

            string aChapterRaw = iArgs.Get("chapter").Trim();
            if (aChapterRaw.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ 缺 `chapter` —— 章號不代取。");
            if (!long.TryParse(aChapterRaw, NumberStyles.Integer, CultureInfo.InvariantCulture,
                               out long aChapter))
                return SCP_CmdResult.Fail(2, $"✗ `chapter` 要是整數（收到 `{aChapterRaw}`）。");

            if (!TryResolveBookDir(iDataRoot, aBook, iArgs,
                                   out string aBookDir, out string aBranchNote, out string aError))
                return SCP_CmdResult.Fail(1, aError);

            // ⚠ 這幾格**刻意不 Trim** —— python 寫的是 argv 原值，Trim 會讓產物逐位元組不同。
            string aTitle = iArgs.Get("title");
            string aSlugSource = iArgs.Get("slug").Trim();
            if (aSlugSource.Length == 0) aSlugSource = aTitle;
            if (aSlugSource.Trim().Length == 0)
                aSlugSource = "ch" + aChapter.ToString(CultureInfo.InvariantCulture);

            string aFileName = "ch" + aChapter.ToString("00", CultureInfo.InvariantCulture)
                               + "_" + Slugify(aSlugSource) + ".md";
            string aChapterPath = Path.Combine(aBookDir, "chapters", aFileName);

            var aFront = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("book", aBook),
                new KeyValuePair<string, string>("chapter",
                                                 aChapter.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("title", aTitle),
                new KeyValuePair<string, string>("reading_date", Today()),
                new KeyValuePair<string, string>(
                    "new_characters", FormatInlineList(SplitList(iArgs.Get("new_characters")))),
            };

            var aBody = new List<string>
            {
                Frontmatter(aFront), "",
                "## 內容摘要", OrFallback(iArgs.Get("summary"), "（待補）"), "",
                "## 關鍵事件",
            };
            AppendBullets(aBody, SplitList(iArgs.Get("events")), "（待補）");
            aBody.Add("");
            aBody.Add("## 本章對人物的新認識");
            AppendBullets(aBody, SplitList(iArgs.Get("views")), "（待補）");
            aBody.Add("");
            aBody.Add("## 伏筆 / 待解");
            AppendBullets(aBody, SplitList(iArgs.Get("foreshadow")), "（無）");
            aBody.Add("");
            SCP_TextFile.WriteCrLf(aChapterPath, string.Join("\n", aBody));

            // book.json：章號只往前推（python 同判斷），last_read 每次都寫。
            string aBookJson = Path.Combine(aBookDir, "book.json");
            if (!TryReadJsonFile(aBookJson, out SCP_JsonData aData, out string aWhy))
                return SCP_CmdResult.Fail(2,
                    "⚠ 章節檔已寫成，但 `book.json` 讀不回來（" + aWhy + "）⇒ **progress 未更新**。",
                    "   " + aChapterPath,
                    "   ⛔ 這不是「記錄失敗」也不是「記錄成功」—— 兩本帳分開："
                    + "章節檔在磁碟上，progress 那一格未落。");

            SCP_JsonData aProgress = aData["progress"];
            if (!aProgress.Exists || aProgress.IsNull)
            {
                // ⚠ 與 python 的**已知差異**（照實寫）：那邊 `bk["progress"]` 缺 key 會 KeyError 當掉；
                //   這裡補一個空的再寫。實測全部 8 份活的 book.json 都有 progress ⇒ 這條路沒有現場讀數。
                aProgress = SCP_JsonData.NewObject();
                aData.Set("progress", aProgress);
            }

            long aCurrent = 0;
            SCP_JsonData aCurrentNode = aProgress["current_chapter"];
            if (aCurrentNode.Exists && !aCurrentNode.IsNull)
                long.TryParse(aCurrentNode.AsString(), NumberStyles.Integer,
                              CultureInfo.InvariantCulture, out aCurrent);
            if (aChapter > aCurrent)
                aProgress.Set("current_chapter", SCP_JsonData.NewNumber(aChapter));
            aProgress.Set("last_read", SCP_JsonData.NewString(Today()));
            SCP_TextFile.WriteCrLf(aBookJson, SCP_JsonWriter.Write(aData, iIndented: true, iIndent: "  ") + "\n");

            var aResult = SCP_CmdResult.Success(
                $"✅ 記錄章節: {aBook} ch{aChapter} → {aFileName}",
                "   " + aChapterPath);
            if (aBranchNote.Length > 0) aResult.Lines.Add("   " + aBranchNote);
            aResult.AddValue("chapter_file", aFileName);
            return aResult;
        }

        // ── op=arc ────────────────────────────────────────────────────────────
        // 區塊職責：記一個跨章「階段大綱」（見林視角）—— 寫 `<book>/arcs/arc_<range>.md`
        //           ＋在 `book.json` 的 `arcs[]` 登記索引。
        // 物理意義：移植 `library.py cmd_arc`。同一個 `chapters` 範圍**取代**舊那筆（python 同行為：
        //           先濾掉同範圍再 append ⇒ 位置會移到尾端，這裡照抄那個順序副作用）。
        static SCP_CmdResult OpArc(string iDataRoot, SCP_CmdArgs iArgs)
        {
            string aBook = iArgs.Get("book").Trim();
            if (aBook.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ 缺 `book` —— 書名不代取。");

            string aChapters = iArgs.Get("chapters").Trim();
            if (aChapters.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ 缺 `chapters` —— 涵蓋範圍（如 `1-6`）不代取。");

            if (!TryResolveBookDir(iDataRoot, aBook, iArgs,
                                   out string aBookDir, out string aBranchNote, out string aError))
                return SCP_CmdResult.Fail(1, aError);

            string aTitle = iArgs.Get("title");
            string aFileSlug = Regex.Replace(aChapters, "[^0-9]+", "-").Trim('-');
            if (aFileSlug.Length == 0) aFileSlug = "x";
            string aFileName = "arc_" + aFileSlug + ".md";
            string aArcPath = Path.Combine(aBookDir, "arcs", aFileName);

            var aFront = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("book", aBook),
                new KeyValuePair<string, string>("chapters", aChapters),
                new KeyValuePair<string, string>("title", aTitle),
                new KeyValuePair<string, string>("date", Today()),
            };

            var aBody = new List<string>
            {
                Frontmatter(aFront), "",
                "## 階段大綱（見林）", OrFallback(iArgs.Get("summary"), "（待補）"), "",
                "## 貫穿線索 / 伏筆狀態",
            };
            AppendBullets(aBody, SplitList(iArgs.Get("threads")), "（待補）");
            aBody.Add("");
            SCP_TextFile.WriteCrLf(aArcPath, string.Join("\n", aBody));

            string aBookJson = Path.Combine(aBookDir, "book.json");
            if (!TryReadJsonFile(aBookJson, out SCP_JsonData aData, out string aWhy))
                return SCP_CmdResult.Fail(2,
                    "⚠ 大綱檔已寫成，但 `book.json` 讀不回來（" + aWhy + "）⇒ **arcs[] 未登記**。",
                    "   " + aArcPath,
                    "   ⛔ 兩本帳分開：檔案在磁碟上，索引那一格未落。");

            var aArcs = SCP_JsonData.NewArray();
            SCP_JsonData aOld = aData["arcs"];
            if (aOld.Exists && !aOld.IsNull)
            {
                for (int i = 0; i < aOld.Count; i++)
                {
                    SCP_JsonData aEntry = aOld[i];
                    if (aEntry.GetString("chapters", "") == aChapters) continue;
                    aArcs.Add(aEntry);
                }
            }
            var aNew = SCP_JsonData.NewObject();
            aNew.Set("chapters", SCP_JsonData.NewString(aChapters));
            aNew.Set("title", SCP_JsonData.NewString(aTitle));
            aNew.Set("file", SCP_JsonData.NewString(aFileName));
            aNew.Set("date", SCP_JsonData.NewString(Today()));
            aArcs.Add(aNew);
            aData.Set("arcs", aArcs);
            SCP_TextFile.WriteCrLf(aBookJson, SCP_JsonWriter.Write(aData, iIndented: true, iIndent: "  ") + "\n");

            var aResult = SCP_CmdResult.Success(
                $"✅ 階段大綱: {aBook} 第 {aChapters} 章 — {aTitle}",
                "   " + aArcPath);
            if (aBranchNote.Length > 0) aResult.Lines.Add("   " + aBranchNote);
            aResult.AddValue("arc_file", aFileName);
            return aResult;
        }

        // ── helpers ───────────────────────────────────────────────────────────

        /// <summary>
        /// 別名去重且**保序**：title → 使用者給的別名（依序）→ title_original。
        /// ⚠ python 用 `dict.fromkeys` 去重，靠的正是 dict 的插入序保證；
        ///   這裡用 HashSet 只做「看過沒」，順序由 List 自己保管，不依賴任何集合的列舉序。
        /// </summary>
        static SCP_JsonData BuildAliases(string iTitle, string iAliasesRaw, string iTitleOriginal)
        {
            var aOrdered = new List<string>();
            var aSeen = new HashSet<string>(StringComparer.Ordinal);

            void Push(string iValue)
            {
                if (iValue.Length == 0) return;
                if (!aSeen.Add(iValue)) return;
                aOrdered.Add(iValue);
            }

            Push(iTitle);
            foreach (string aPart in SplitList(iAliasesRaw)) Push(aPart);
            if (iTitleOriginal.Length > 0) Push(iTitleOriginal);

            var aArray = SCP_JsonData.NewArray();
            foreach (string aValue in aOrdered) aArray.Add(SCP_JsonData.NewString(aValue));
            return aArray;
        }

        /// <summary>用 `;` `|` 換行 切開並去掉空白項（對齊 python `_split_list`）。</summary>
        static List<string> SplitList(string iValue)
        {
            var aOut = new List<string>();
            if (string.IsNullOrEmpty(iValue)) return aOut;
            foreach (string aPart in Regex.Split(iValue, "[;|\n]+"))
            {
                string aTrimmed = aPart.Trim();
                if (aTrimmed.Length > 0) aOut.Add(aTrimmed);
            }
            return aOut;
        }

        /// <summary>
        /// 對齊 python `_slugify`：lower → 非 `\w`／`-` 一律換成 `-` → 收斂連續 `-` → 去頭尾 `-`。
        /// ⚠ 已知差異一格（照實寫，不假裝逐字等價）：python 的 `\w` 還吃 `Nl`／`No` 這兩類數字，
        ///   .NET 的 `\w` 不吃。實務書名（CJK ＋ 拉丁 ＋ 數字）兩邊結果相同；
        ///   真要撞到得用羅馬數字之類當書名，⇒ 那時 `--arg id=` 顯式給 slug。
        /// </summary>
        static string Slugify(string iValue)
        {
            string aText = (iValue ?? "").Trim().ToLowerInvariant();
            aText = Regex.Replace(aText, @"[^\w-]+", "-");
            aText = Regex.Replace(aText, "-+", "-").Trim('-');
            return aText.Length > 0 ? aText : "untitled";
        }

        /// <summary>
        /// `--reader` 路由（對齊 python `_activate_branch`）：reader 為空**或等於初始讀者** ⇒ main；
        /// 否則 ⇒ `branches/<reader>/`，且**首次啟用時自動 init 分支 `book.json`**。
        /// 🩸 這一支不是 authored 線專用 —— 漫畫／動畫那側四份活的 `branches/*` 都靠它，
        ///   只做 main 那半的話，同事的分支筆記會被寫到 main（**靜默污染別人的進度**）。
        /// </summary>
        static bool TryResolveBookDir(string iDataRoot, string iBook, SCP_CmdArgs iArgs,
                                      out string oBookDir, out string oBranchNote, out string oError)
        {
            oBookDir = "";
            oBranchNote = "";
            oError = "";

            string aMainDir = Path.Combine(SCP_BookStore.BookNotesRoot(iDataRoot), iBook);
            string aMainJson = Path.Combine(aMainDir, "book.json");
            if (!File.Exists(aMainJson))
            {
                oError = $"❌ 找不到書: {iBook}（請先 `op=add`）　查的是 {aMainJson}";
                return false;
            }

            string aReader = iArgs.Get("reader").Trim();
            string aInitial = "";
            if (TryReadJsonFile(aMainJson, out SCP_JsonData aMain, out _))
                aInitial = aMain.GetString("reader_persona", "");

            if (aReader.Length == 0 || aReader == aInitial)
            {
                oBookDir = aMainDir;
                return true;
            }

            string aBranchDir = Path.Combine(aMainDir, "branches", aReader);
            string aBranchJson = Path.Combine(aBranchDir, "book.json");
            if (!File.Exists(aBranchJson))
            {
                string aContinueFrom = iArgs.Get("continue_from").Trim();
                long aStartCh = 0;
                string aSeedNote = "";
                if (aContinueFrom.Length > 0)
                {
                    string aSrcJson = aContinueFrom == aInitial
                        ? aMainJson
                        : Path.Combine(aMainDir, "branches", aContinueFrom, "book.json");
                    if (File.Exists(aSrcJson)
                        && TryReadJsonFile(aSrcJson, out SCP_JsonData aSrc, out _))
                    {
                        SCP_JsonData aSrcCh = aSrc["progress"]["current_chapter"];
                        if (aSrcCh.Exists && !aSrcCh.IsNull)
                            long.TryParse(aSrcCh.AsString(), NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out aStartCh);
                        aSeedNote = $"（branch 起點：接續 {aContinueFrom} 讀到的第 {aStartCh} 章，"
                                    + $"之後獨立推進，不影響 {aContinueFrom}）";
                    }
                }

                var aData = SCP_JsonData.NewObject();
                aData.Set("id", SCP_JsonData.NewString(aMain.GetString("id", iBook) + "::" + aReader));
                aData.Set("title", SCP_JsonData.NewString(aMain.GetString("title", iBook)));
                aData.Set("title_original", SCP_JsonData.NewString(aMain.GetString("title_original", "")));
                aData.Set("author", SCP_JsonData.NewString(aMain.GetString("author", "")));
                aData.Set("reader_persona", SCP_JsonData.NewString(aReader));
                aData.Set("branch_of", SCP_JsonData.NewString(iBook));
                aData.Set("branched_from",
                          SCP_JsonData.NewString(aContinueFrom.Length > 0 ? aContinueFrom : "(獨立起讀)"));
                aData.Set("status", SCP_JsonData.NewString("reading"));
                var aProgress = SCP_JsonData.NewObject();
                aProgress.Set("current_chapter", SCP_JsonData.NewNumber(aStartCh));
                aProgress.Set("last_read", SCP_JsonData.NewString(Today()));
                aProgress.Set("bookmark_note", SCP_JsonData.NewString(aSeedNote));
                aData.Set("progress", aProgress);
                aData.Set("characters", SCP_JsonData.NewArray());
                SCP_TextFile.WriteCrLf(aBranchJson,
                              SCP_JsonWriter.Write(aData, iIndented: true, iIndent: "  ") + "\n");
                oBranchNote = $"🌿 已開分支筆記: {iBook}/branches/{aReader}/"
                              + (aContinueFrom.Length > 0
                                     ? $"（接續 {aContinueFrom} 第 {aStartCh} 章）"
                                     : "（獨立從頭）");
            }
            else
            {
                oBranchNote = $"🌿 你在【{aReader} 的分支筆記】—— 不影響初始讀者 "
                              + (aInitial.Length > 0 ? aInitial : "(未宣告)");
            }

            oBookDir = aBranchDir;
            return true;
        }

        /// <summary>讀一份 JSON 檔；⛔ 讀不到／解析失敗**不回空物件** —— 那會跟「檔裡真的沒東西」同形。</summary>
        static bool TryReadJsonFile(string iPath, out SCP_JsonData oData, out string oWhy)
        {
            oData = SCP_JsonData.NewObject();
            oWhy = "";
            if (!File.Exists(iPath)) { oWhy = "檔不存在：" + iPath; return false; }
            try
            {
                oData = SCP_JsonData.Parse(File.ReadAllText(iPath));
                return true;
            }
            catch (Exception aEx)
            {
                oWhy = aEx.GetType().Name + ": " + aEx.Message;
                return false;
            }
        }

        /// <summary>
        /// 對齊 python `_frontmatter`：`---` ／ 每行 `k: v` ／ `---`。
        /// 🩸 空值那行是 `"k: "`（**尾隨一個空格**）—— python `f"{k}: {v}"` 就是這樣，
        ///   而少那一個空格的失效樣子是「內容看起來一樣，逐位元組不同」，沒有一層會喊。
        /// </summary>
        static string Frontmatter(List<KeyValuePair<string, string>> iFields)
        {
            var aLines = new List<string> { "---" };
            foreach (KeyValuePair<string, string> aField in iFields)
                aLines.Add(aField.Key + ": " + aField.Value);
            aLines.Add("---");
            return string.Join("\n", aLines);
        }

        /// <summary>python 的 list 行內表示：`[a, b]`（空 list ⇒ `[]`）。</summary>
        static string FormatInlineList(List<string> iItems)
        {
            return "[" + string.Join(", ", iItems) + "]";
        }

        /// <summary>把每一項寫成 `- x`；⛔ 一項都沒有時寫 fallback（python `or [...]` 的形狀）。</summary>
        static void AppendBullets(List<string> ioBody, List<string> iItems, string iFallback)
        {
            if (iItems.Count == 0)
            {
                ioBody.Add("- " + iFallback);
                return;
            }
            foreach (string aItem in iItems) ioBody.Add("- " + aItem);
        }

        /// <summary>
        /// python `x or fallback` 的等價：**只有空字串**才換 fallback。
        /// ⚠ 全空白的 `" "` 在 python 是 truthy ⇒ 這裡也不能用 IsNullOrWhiteSpace，否則兩邊產物不同。
        /// </summary>
        static string OrFallback(string iValue, string iFallback)
        {
            return string.IsNullOrEmpty(iValue) ? iFallback : iValue;
        }

        static string Today()
        {
            return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

    }
}
