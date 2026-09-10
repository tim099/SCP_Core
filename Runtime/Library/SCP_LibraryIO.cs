using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;

// 區塊職責：新 Library store 的**資料鍵、JSON/文字讀寫、以及讀取端**（reader.json 載入與章節分類）。
// 物理意義：這是 `UCL_ReadingLibraryIO` 移進 SCP_Core 的第二刀。第一刀（`SCP_LibraryStore`）只回答
//          「路徑在哪」，本層回答「那個檔裡有什麼、讀不讀得動、讀出來算第幾章」。
//          ⛔ **寫入端（media_init / note_chapter / bookmark / add_character / revise_view）還沒搬**。
// 數值影響：`LoadJson` 讀不動一律回 null ＋ error（⛔ 不回空物件 —— 那會讓下一次寫入把壞檔
//          覆蓋成「乾淨」，原始資料連救都救不回來）。寫檔一律 UTF-8 無 BOM ＋ **CRLF**（見下）。
namespace SCP.Core.Library
{
    /// <summary>章節與現有進度的關係（Tim 2026-08-06 拍板：**分類，不是閘門**）。</summary>
    public enum SCP_ChapterRelation { FirstEver, Reread, Next, Gap, Prologue }

    public static class SCP_LibraryIO
    {
        // ── 資料鍵（逐字對齊 UCL_ReadingLibraryIO，⛔ 不趁機改名）──────────────
        // ⚠ 這些是磁碟上 342 份既有 JSON 的實際鍵名；改一個字 = 讀不到舊值，
        //   而「讀不到」會退回 fallback ⇒ 看起來像「這欄還沒填」，不像「我把鍵改錯了」。
        public const string Key_SchemaVersion = "schema_version";
        public const string Key_ReaderPersona = "reader_persona";
        public const string Key_MediaId = "media_id";
        public const string Key_MediaKind = "media_kind";
        public const string Key_WorkId = "work_id";
        public const string Key_Status = "status";
        public const string Key_Anticipation = "anticipation";
        public const string Key_Progress = "progress";
        public const string Key_CurrentChapterId = "current_chapter_id";
        public const string Key_LastRead = "last_read";
        public const string Key_BookmarkNote = "bookmark_note";
        public const string Key_CurrentImpression = "current_impression";
        public const string Key_UpdatedAt = "updated_at";
        public const string Key_ChapterId = "chapter_id";
        public const string Key_DisplayNumber = "display_number";
        public const string Key_Title = "title";
        public const string Key_TitleOriginal = "title_original";
        public const string Key_Author = "author";
        public const string Key_TimeRange = "time_range";
        public const string Key_Rounds = "rounds";
        public const string Key_Round = "round";
        public const string Key_ReadingDate = "reading_date";
        public const string Key_File = "file";
        public const string Key_Aliases = "aliases";
        public const string Key_GenreTags = "genre_tags";
        public const string Key_CharacterId = "character_id";
        public const string Key_Name = "name";
        public const string Key_NameOriginal = "name_original";
        public const string Key_Facts = "facts";

        /// <summary>合法的 media_kind —— ⚠ media_id 的前綴必須與它同字（兩欄互為校驗）。</summary>
        public static readonly string[] MediaKinds = { "comic", "anim", "film", "series", "stream", "book" };

        // ── 檔案寫入 ────────────────────────────────────────────────────────
        // 🩸 **CRLF 不是風格選擇**：磁碟上既有的 chapter.json 量到 CRLF 14／純 LF 0，
        //   而 `SCP_JsonWriter` 的 `NewLineIndent` 送的是 LF。直接寫出去的話
        //   **內容一樣而逐位元組不同**，342 份檔會在下一次寫入時整批翻紅，
        //   ⛔ 而那件事沒有任何一層會喊（`SCP_Cmd_Book` 檔頭記的是同一隻，TASK-0143 第五刀血證）。
        // ⚠ 本函式與 `SCP_Cmd_Book.WriteTextCrLf` **同源而各留一份** —— 兩層的產物格式其實不同
        //   （Books 是 2 空格縮排、Library 是 tab），只有「換行正規化＋近 atomic 落檔」這段一樣。
        //   ⇒ 現在提取共用要動 Books 那條**已逐位元組對拍過**的路徑，成本大於收益；
        //     等第三個使用者出現時再提取（那時它才真的是共用邏輯，而不是兩個巧合）。
        static void WriteTextCrLf(string iPath, string iText)
        {
            string aDir = Path.GetDirectoryName(iPath) ?? "";
            if (aDir.Length > 0) Directory.CreateDirectory(aDir);
            string aNormalized = iText.Replace("\r\n", "\n").Replace("\n", "\r\n");
            string aTmp = iPath + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(aTmp, aNormalized, new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Delete(iPath);
            File.Move(aTmp, iPath);
        }

        /// <summary>
        /// 讀 JSON。讀不動一律回 <c>null</c> ＋ <paramref name="oError"/>。
        /// ⛔ **不回空物件** —— 那會讓下一次寫入把壞檔覆蓋成「乾淨」，原始資料救不回來。
        /// </summary>
        public static SCP_JsonData? LoadJson(string iPath, out string? oError)
        {
            oError = null;
            if (!File.Exists(iPath)) { oError = "檔案不存在：" + iPath; return null; }
            try
            {
                SCP_JsonData aData = SCP_JsonData.Parse(File.ReadAllText(iPath, Encoding.UTF8));
                if (aData == null || !aData.IsObject) { oError = "不是 JSON object：" + iPath; return null; }
                return aData;
            }
            catch (Exception e)
            {
                oError = "JSON 解析失敗（" + iPath + "）：" + e.Message;
                return null;
            }
        }

        /// <summary>寫 JSON（UTF-8 無 BOM、tab 縮排、非 ASCII 原生字元、CRLF）。父目錄自動建立。</summary>
        /// <remarks>
        /// ⭐ 非 ASCII **不需要**額外還原：`SCP_JsonWriter.WriteString` 天生照原字寫
        /// （UCL 那版得先跑一支 `UnescapeNonAscii` 把逃脫轉回來，本層不必搬那個補丁）。
        /// </remarks>
        public static void SaveJson(string iPath, SCP_JsonData iData)
        {
            WriteTextCrLf(iPath, iData.ToJson(true) + "\n");
        }

        /// <summary>寫純文字（UTF-8 無 BOM、CRLF）。父目錄自動建立。</summary>
        public static void SaveText(string iPath, string iText)
        {
            WriteTextCrLf(iPath, iText);
        }

        public static string Today()
        {
            return DateTime.Now.ToString("yyyy-MM-dd");
        }

        /// <summary>
        /// 組 JSON 字串陣列 —— 去重（保序、不分大小寫）、去空白、把必含值一起收進來。
        /// 物理意義：aliases 一定要含 title / title_original 自己 —— 否則「用正式名搜尋卻搜不到」。
        /// </summary>
        public static SCP_JsonData ToStringArray(IList<string>? iValues, params string[] iAlsoInclude)
        {
            SCP_JsonData aArray = SCP_JsonData.NewArray();
            var aSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            void Push(string iV)
            {
                if (string.IsNullOrWhiteSpace(iV)) return;
                string aTrimmed = iV.Trim();
                if (!aSeen.Add(aTrimmed)) return;
                aArray.Add(aTrimmed);
            }
            if (iAlsoInclude != null) foreach (string v in iAlsoInclude) Push(v);
            if (iValues != null) foreach (string v in iValues) Push(v);
            return aArray;
        }

        /// <summary>把 a|b|c 或 a,b,c 切成清單（別名含逗號時用直線）。</summary>
        public static List<string> SplitList(string? iRaw)
        {
            var aResult = new List<string>();
            if (string.IsNullOrWhiteSpace(iRaw)) return aResult;
            char aSeparator = iRaw!.Contains("|") ? '|' : ',';
            foreach (string aPart in iRaw.Split(aSeparator))
            {
                if (!string.IsNullOrWhiteSpace(aPart)) aResult.Add(aPart.Trim());
            }
            return aResult;
        }

        // ── reader.json 讀取 ＋ 身分校驗 ──────────────────────────────────
        // 物理意義：路徑上的 persona 與檔內 reader_persona 不符 ＝ 資料放錯讀者根目錄，
        //          那是「替別人代筆閱讀史」的前一步，必須擋。
        public static SCP_JsonData? LoadReader(string iDataRoot, string iMediaId, string iPersona,
                                               out string? oError)
        {
            // ⚠ 「還不是這部的 reader」與「檔壞了」是兩件事，而 LoadJson 只會說「檔案不存在」——
            //   那是一句**死路**：它描述現況，不指出出口。所以這一格在進 LoadJson 之前先攔。
            string aReaderPath = SCP_LibraryStore.ReaderJsonPath(iDataRoot, iMediaId, iPersona);
            if (!File.Exists(aReaderPath))
            {
                oError = NotAReaderYetMessage(iDataRoot, iMediaId, iPersona, aReaderPath);
                return null;
            }
            SCP_JsonData? aReader = LoadJson(aReaderPath, out oError);
            if (aReader == null) return null;

            string aDeclaredPersona = aReader.GetString(Key_ReaderPersona, "");
            if (aDeclaredPersona != iPersona)
            {
                oError = "reader.json." + Key_ReaderPersona + "=" + aDeclaredPersona
                         + "，與路徑 persona=" + iPersona + " 不一致";
                return null;
            }
            string aDeclaredMedia = aReader.GetString(Key_MediaId, "");
            if (aDeclaredMedia != iMediaId)
            {
                oError = "reader.json." + Key_MediaId + "=" + aDeclaredMedia
                         + "，與請求 media_id=" + iMediaId + " 不一致";
                return null;
            }
            return aReader;
        }

        // 區塊職責：「你還不是這部的 reader」時，把**出口**印出來（不是只描述現況）。
        // 🩸 TASK-0137（2026-09-05 summit）：三支 op 全回「檔案不存在：…/reader.json」，
        //   她讀不出出口於是沒寫成接續點 —— 而場次結算 exit 0、公告照發
        //   ⇒ **場次帳是綠的、記憶帳是空的**。那是窄報，不是拒絕。
        // ⚠ 指令字面暫時保留 `ucmd run Library` 的形狀 —— **那條現在還活著**，
        //   而 `senate cmd library` 這一刀還沒建出來。⛔ 指路牌不指向還沒出生的路；
        //   Cmd 殼落地那一刀要回來改這裡（本註解就是那筆待辦的錨）。
        static string NotAReaderYetMessage(string iDataRoot, string iMediaId, string iPersona,
                                           string iReaderPath)
        {
            string aWorkId = "<work_id>";
            string aMediaKind = "<media_kind>";
            string aTitle = "<作品中文名>";
            SCP_JsonData? aMedia = LoadJson(SCP_LibraryStore.MediaJsonPath(iDataRoot, iMediaId), out _);
            if (aMedia != null)
            {
                aWorkId = aMedia.GetString(Key_WorkId, aWorkId);
                aMediaKind = aMedia.GetString(Key_MediaKind, aMediaKind);
                SCP_JsonData? aWork = LoadJson(SCP_LibraryStore.WorkJsonPath(iDataRoot, aWorkId), out _);
                if (aWork != null) aTitle = aWork.GetString(Key_Title, aTitle);
            }
            return
                "你還不是 `" + iMediaId + "` 的 reader —— `reader.json` 不存在：" + iReaderPath + "\n" +
                "⇒ 出口（**這一支就是登記入口**，不是只給新作品用的）：\n" +
                "   Library op=media_init --arg persona=" + iPersona + " --arg media_id=" + iMediaId +
                " --arg work_id=" + aWorkId + " --arg media_kind=" + aMediaKind + " --arg title=" + aTitle +
                " --arg anticipation=<1-5 期待度>\n" +
                "⚠ 它的名字只說了一半：media 已存在時 **work.json / media.json 一律不覆寫**，" +
                "只補建你自己的 reader.json（既有讀者的進度不受影響）。";
        }

        // 區塊職責：章節連續性分類。
        // 數值影響：**不擋任何寫入**；0000 序章不參與連續性判定（它非必有）。
        public static SCP_ChapterRelation ClassifyChapter(SCP_JsonData? iReader, string iChapterId)
        {
            if (iChapterId == SCP_LibraryStore.PrologueChapterId) return SCP_ChapterRelation.Prologue;
            if (iReader == null) return SCP_ChapterRelation.FirstEver;

            string aCurrent = iReader.IsObject && iReader.Contains(Key_Progress)
                ? iReader[Key_Progress].GetString(Key_CurrentChapterId, "")
                : "";
            if (string.IsNullOrEmpty(aCurrent) || aCurrent == SCP_LibraryStore.PrologueChapterId)
                return SCP_ChapterRelation.Next;
            if (aCurrent == iChapterId) return SCP_ChapterRelation.Reread;
            if (int.TryParse(aCurrent, out int aCur) && int.TryParse(iChapterId, out int aReq)
                && aReq == aCur + 1)
                return SCP_ChapterRelation.Next;
            return SCP_ChapterRelation.Gap;
        }
    }
}
