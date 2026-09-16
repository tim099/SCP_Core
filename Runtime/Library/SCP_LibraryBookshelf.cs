// 區塊職責：閱讀卡（bookshelf.md）—— 由 reader.json 生成的**人可讀投影**，以及它往 letters 的轉發。
// 物理意義：真相源永遠是 `reader.json`。Library 內那份 `bookshelf.md` 是投影，
//           letters 底下那份是**投影的投影**（給 persona 隨身帶的副本）。
//           ⇒ 任何流程都**只准讀它、不准回寫**；要改內容去改 reader.json 再 Sync。
// 數值影響：每次呼叫整份覆寫兩個檔（正本先、副本後）。
// 🩸 為什麼這一叢要跟 scaffolding 一起搬（TASK-0166，2026-09-16 逐支量的呼叫端）：
//   `EnsureReaderJson` 的最後一行呼叫本支 ⇒ 只搬 `MediaInit`／`EnsureReaderJson` 而不搬這裡的話，
//   落地的樣子是「reader.json 建得出來、回 ✅，**而閱讀卡停在上一次**」—— 每一層都綠的失效。
// ⚠ 而它**到此為止**：本支 ⛔ 不呼叫 `RenderRecall`。
//   （追回檔那條的入口是 `WriteRecallBrief`，由 NoteChapter／AddCharacter／ReviseView／Bookmark 各自呼叫，
//    與本支平行、不串接。⚠ 我在 2026-09-16 一度把兩條讀成同一條，真因是 `sed` 的範圍右端
//    切在 `RenderRecall` 的**宣告行**上 —— 那不是呼叫。）
using System;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Library
{
    public static class SCP_LibraryBookshelf
    {
        // ===========================================================
        // 區塊職責：由 reader.json 重算閱讀卡，寫正本並轉發副本。
        // 數值影響：讀不到 reader.json ⇒ **一個檔都不寫**（⛔ 不寫一張空卡片 ——
        //           那會讓「還沒讀」與「讀不到」在書架上同形）。
        // ⚠ <paramref name="oForwardWarning"/> 是本層與 Editor 版**唯一**的行為差異，而它是結構性的：
        //   Editor 版轉發失敗時吞掉例外並 `Debug.LogWarning`，而 SCP_Core 叫不到 Unity，
        //   也 ⛔ 不該自己造第二套 log 管道。⇒ 警告**交出去**，由呼叫端決定印不印。
        //   📌 檔案產物逐位元組相同；差的只有「那句警告從哪裡出來」。
        //   而方向沒有變：**轉發失敗不連累正本**（正本先寫、副本後寫，失敗不回滾）。
        // ===========================================================
        public static void SyncBookshelf(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                         string iMediaId, string iPersona,
                                         out string? oError, out string? oForwardWarning)
        {
            oForwardWarning = null;
            string? aText = RenderCard(iDataRoot, iMediaId, iPersona, out oError);
            if (aText == null) return;

            SCP_LibraryIO.SaveText(
                Path.Combine(SCP_LibraryStore.ReaderRoot(iDataRoot, iMediaId, iPersona),
                             SCP_LibraryStore.BookshelfName),
                aText);
            ForwardToLetters(iLettersRoot, iMediaId, iPersona, aText, out oForwardWarning);
        }

        // ===========================================================
        // 區塊職責：**只渲染、不落檔** —— 閱讀卡的內容由這裡決定。
        // 🩸 為什麼要把它從 `SyncBookshelf` 裡拆出來（TASK-0166）：那一支的唯一出口是寫檔，
        //   ⇒ 「它產出什麼」這件事**無法被對拍**，除非先寫進真的資料樹再讀回來 ——
        //   而那正是「我自己造的證人跟我同源」那一族：用寫入端去驗寫入端。
        //   ⇒ 拆出純函式之後，Editor 端落在磁碟上的那幾百份 `bookshelf.md` 才變成得了對照組。
        // 數值影響：零寫入。讀不到 reader.json ⇒ 回 null ＋ oError（⛔ 不回一張空卡片）。
        // ===========================================================
        public static string? RenderCard(string iDataRoot, string iMediaId, string iPersona,
                                         out string? oError)
        {
            SCP_JsonData? aReader = SCP_LibraryIO.LoadReader(iDataRoot, iMediaId, iPersona, out oError);
            if (aReader == null) return null;

            SCP_JsonData? aMedia = SCP_LibraryIO.LoadJson(
                SCP_LibraryStore.MediaJsonPath(iDataRoot, iMediaId), out _);
            string aWorkId = aMedia != null ? aMedia.GetString(SCP_LibraryIO.Key_WorkId, "") : "";
            string aMediaKind = aMedia != null ? aMedia.GetString(SCP_LibraryIO.Key_MediaKind, "unknown") : "unknown";
            SCP_JsonData? aWork = string.IsNullOrEmpty(aWorkId)
                ? null
                : SCP_LibraryIO.LoadJson(SCP_LibraryStore.WorkJsonPath(iDataRoot, aWorkId), out _);
            string aTitle = aWork != null ? aWork.GetString(SCP_LibraryIO.Key_Title, aWorkId) : aWorkId;

            SCP_JsonData? aProgress = aReader.Contains(SCP_LibraryIO.Key_Progress)
                ? aReader[SCP_LibraryIO.Key_Progress] : null;
            string aChapterId = aProgress != null ? aProgress.GetString(SCP_LibraryIO.Key_CurrentChapterId, "") : "";
            string aLastRead = aProgress != null ? aProgress.GetString(SCP_LibraryIO.Key_LastRead, "") : "";
            string aBookmark = aProgress != null ? aProgress.GetString(SCP_LibraryIO.Key_BookmarkNote, "") : "";

            var aSb = new StringBuilder();
            aSb.AppendLine("---");
            aSb.AppendLine($"{SCP_LibraryIO.Key_WorkId}: {aWorkId}");
            aSb.AppendLine($"{SCP_LibraryIO.Key_MediaId}: {iMediaId}");
            aSb.AppendLine($"{SCP_LibraryIO.Key_MediaKind}: {aMediaKind}");
            aSb.AppendLine($"{SCP_LibraryIO.Key_ReaderPersona}: {iPersona}");
            aSb.AppendLine($"{SCP_LibraryIO.Key_Status}: {aReader.GetString(SCP_LibraryIO.Key_Status, "reading")}");
            aSb.AppendLine($"{SCP_LibraryIO.Key_Anticipation}: {aReader.GetInt(SCP_LibraryIO.Key_Anticipation, 0)}");
            // ⚠ 章號**帶引號**是刻意的：`0001` 不帶引號會被 YAML 讀成數字 1，而章號是字串。
            aSb.AppendLine($"progress_snapshot_chapter: \"{aChapterId}\"");
            aSb.AppendLine($"progress_snapshot_last_read: {aLastRead}");
            aSb.AppendLine($"{SCP_LibraryIO.Key_UpdatedAt}: {aReader.GetString(SCP_LibraryIO.Key_UpdatedAt, SCP_LibraryIO.Today())}");
            // ⛔ 這一行逐字保留（含「由 UCL_ReadingLibraryIO」那個出處）——
            //   改它會讓既有 400+ 份閱讀卡在下一次同步時整批翻紅，而那不是本單要付的帳。
            //   出處名要換是另一個決定（連同一次正規化），不是順手改。
            aSb.AppendLine("generated: mechanical   # 由 UCL_ReadingLibraryIO 由 reader.json 生成；手改會被覆寫");
            aSb.AppendLine("---");
            aSb.AppendLine();
            aSb.AppendLine($"# {iPersona} 的《{aTitle}》閱讀卡");
            aSb.AppendLine();
            aSb.AppendLine("> `reader.json` 是本卡片的資料真相源；此檔是人可讀投影，每次寫入後重新生成。");
            aSb.AppendLine();
            aSb.AppendLine($"**期待度：{aReader.GetInt(SCP_LibraryIO.Key_Anticipation, 0)}／5**");
            aSb.AppendLine();
            aSb.AppendLine("## 目前進度");
            aSb.AppendLine();
            aSb.AppendLine(string.IsNullOrEmpty(aBookmark) ? "（尚無書籤）" : aBookmark);
            aSb.AppendLine();
            aSb.AppendLine("## 目前看法");
            aSb.AppendLine();
            aSb.AppendLine(aReader.GetString(SCP_LibraryIO.Key_CurrentImpression, "（尚無）"));

            return aSb.ToString();
        }

        // ===========================================================
        // 區塊職責：把閱讀卡多送一份到該 persona 自己的信件目錄 —— 讓「我跟這本書的關係」
        //          跟 sketchbook（我對**人**的看法）並排：一個看書、一個看人。
        // 物理意義：**投影的投影，不是第三個真相源。**
        // ⚠ `letters/<persona>/` 每一個都是獨立 git submodule —— 這裡每寫一次就弄髒該 persona 的 repo。
        //   目前觸發點是「該 persona 自己寫心得」，弄髒的是自己的 repo，代價收斂在當事人身上；
        //   ⛔ 若日後有「一次同步全部 persona」的批次入口，請先想清楚那會一次弄髒 N 個 repo。
        // 邊界：寫檔失敗**只回警告**，不讓副本失敗連累已經落盤的正本。
        // ===========================================================
        static void ForwardToLetters(SCP_LettersRoot iLettersRoot, string iMediaId, string iPersona,
                                     string iText, out string? oWarning)
        {
            oWarning = null;
            try
            {
                string aPath = Path.Combine(SCP_LettersPaths.PersonaDir(iLettersRoot, iPersona),
                                            SCP_WakeBrief.BookshelfDirName, iMediaId + ".md");
                SCP_LibraryIO.SaveText(aPath, iText);
            }
            catch (Exception e)
            {
                oWarning = "bookshelf 轉發至 letters 失敗（正本已寫入，不影響資料）："
                           + e.GetType().Name + ": " + e.Message;
            }
        }
    }
}
