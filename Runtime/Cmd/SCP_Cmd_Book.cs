// 區塊職責：`cmd book` —— 書本筆記庫（`BookNotes/<slug>/`）的建檔（**原生**，不需要 Unity）。
// 物理意義：這是 `library.py add-book` 的移植（TASK-0143 ②-bis 那條線的第一刀）。
//           落點是**舊 store**（`BookNotes/<slug>/book.json` ＋ `chapters/`／`characters/`），
//           ⛔ 不是 `BookNotes/Library/work|media/` 那個新 store ——
//           兩者名字近而住在不同目錄，`Cmd_Library.media_init` 也**沒有 origin=authored 的概念**
//           （2026-09-06 對拍，見 TASK-0143 工作記憶）。⇒ 這一支不是它的重複實作。
//
// ⚠ **本支只新增，不搬遷、不改寫既有書。** 舊 store 裡有兩本**別人正在寫**的 authored 書
//   （@gura《深海對拍錄》／@Sirius《熄燈前的燈》），舊 store 的去留（先搬資料再退 python ／
//   authored 線留到那兩本發布）**還沒拍板** ⇒ 本檔刻意只做「加一本新書」這一格，
//   已存在就拒絕覆寫（跟 python 同行為）。
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
using SCP.Core.Json;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Book : SCP_Cmd
    {
        public override string Name => "book";

        public override string Summary =>
            "書本筆記庫建檔：在 `BookNotes/<slug>/` 開一本新書（`op=add`）"
            + " —— **本地跑，不需要 Editor**";

        public override string Details =>
            "⭐ `origin=authored` ＝ 自由時間**寫書**（作者＝該 persona）：多帶 `author_persona`／\n"
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
            new SCP_CmdArgSpec("op", "add（建一本新書）｜writing（列出寫到一半的書，**純讀**）"
                               + " —— 預設 writing，⭐ 純讀的那個當預設"),
            new SCP_CmdArgSpec("persona", "op=writing 用：只看這位作者的書（省略＝全部作者）"),
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
                "writing" => OpWriting(aDataRoot, iArgs),
                _ => SCP_CmdResult.Fail(2, $"✗ 不認得的 op：`{aOp}`（吃的是 add｜writing）"),
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
            WriteTextCrLf(aBookJson, aText);

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

        static string Today()
        {
            return DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// 寫檔：UTF-8 **無 BOM** ＋ **CRLF**，temp → replace（近 atomic）。
        /// 🩸 CRLF 不是風格選擇：python 那側是文字模式寫入，在 Windows 上就是 CRLF，
        ///   而「內容一樣、逐位元組不同」沒有任何一層會喊（TASK-0143 第五刀的血證）。
        /// </summary>
        static void WriteTextCrLf(string iPath, string iText)
        {
            string aDir = Path.GetDirectoryName(iPath) ?? "";
            if (aDir.Length > 0) Directory.CreateDirectory(aDir);
            string aNormalized = iText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            // python 用 pid 當 temp 尾碼避免撞名；netstandard2.1 沒有 `Environment.ProcessId`，
            // 這裡改用 GUID —— 它只影響 temp 檔名，**不落在產物裡**，所以不破壞逐位元組對拍。
            string aTmp = iPath + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(aTmp, aNormalized, new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Delete(iPath);
            File.Move(aTmp, iPath);
        }
    }
}
