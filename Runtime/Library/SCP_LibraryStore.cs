using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SCP.Core.Books;

// 區塊職責：新 Library store（`work → media → reader_persona`）的**路徑與唯讀列舉**層。
// 物理意義：這是 `UCL_ReadingLibraryIO` 移進 SCP_Core 的第一刀 —— 只搬「路徑怎麼算」與
//          「目錄裡有什麼」，⛔ 還沒搬任何寫入端。挑這一刀先做的理由是它**可以被單獨驗證**：
//          兩邊對同一個 data_root 算出的路徑字串必須逐字相同，而那不需要動任何檔案。
// 數值影響：全層純讀，不建目錄、不寫檔。目錄不存在一律回空清單（⛔ 不拋例外 ——
//          「還沒有人讀過任何東西」是正常狀態，不是錯誤）。
//
// ⚠ **與 `SCP_BookStore` 是兩個 store，不要混用**：那邊是舊 store（`BookNotes/<slug>/`，
//   作品本體與 authored 寫書線），本層是新 store（`BookNotes/Library/`，讀者心得與 round 版本史）。
//   同一本書可以**同時**在兩邊而且兩邊都對 —— 那不是重複，是「作品」與「誰讀了它」。
// ⭐ 而 BookNotes 根**刻意重用** `SCP_BookStore.BookNotesRoot(iDataRoot)`：
//   兩層各算一次的話，哪天有人改了其中一個，兩個 store 會安靜地分家到不同的樹底下。
namespace SCP.Core.Library
{
    public static class SCP_LibraryStore
    {
        // ── 目錄與檔名（逐字對齊 UCL_ReadingLibraryIO，⛔ 不趁機改名）────────────
        // ⚠ 這些字串是**磁碟上既有 342 份 chapter.json 的實際位置**，改一個字就是找不到舊資料，
        //   而「找不到」與「這位讀者還沒讀過」在回傳值上同形（都是空清單）。
        public const string LibraryDirName = "Library";
        public const string MediaDirName = "media";
        public const string WorksDirName = "works";
        public const string ReadersDirName = "readers";
        public const string ChaptersDirName = "chapters";
        public const string CharactersDirName = "characters";
        public const string ReaderJsonName = "reader.json";
        public const string MediaJsonName = "media.json";
        public const string WorkJsonName = "work.json";

        /// <summary>序章保留章號 —— `0000` 不是「第 0 章」，是**序章**。</summary>
        public const string PrologueChapterId = "0000";

        /// <summary>合法的 media / work / reader id 形狀。</summary>
        static readonly Regex k_IdPattern = new Regex(@"^[A-Za-z0-9][A-Za-z0-9_-]*$");

        /// <summary>章號是**四位數字**（`0000`-`9999`）—— ⛔ 不是整數，`3` 與 `0003` 不是同一個章。</summary>
        static readonly Regex k_ChapterIdPattern = new Regex(@"^\d{4}$");

        // ── 路徑（全部吃 iDataRoot，⛔ 沒有靜態推導的那條路）──────────────────
        // 🩸 UCL 那版是 `static string BookNotesRoot => …UCL_RepoPath.AgentCommandsDir…`，
        //   在 Editor 裡成立是因為那時只有一個專案在跑。CLI 這側同一支行程可以被指到任何一棵樹，
        //   靜態推導會讓「我以為在算 A 專案」與「它算的是 B 專案」在字串上長得一模一樣
        //   （TASK-0126 就是這一族：讀對樹、寫錯樹，而回讀跟著寫入端走所以全綠）。
        public static string LibraryRoot(string iDataRoot)
        {
            return Path.Combine(SCP_BookStore.BookNotesRoot(iDataRoot), LibraryDirName);
        }

        public static string MediaDir(string iDataRoot)
        {
            return Path.Combine(LibraryRoot(iDataRoot), MediaDirName);
        }

        public static string WorksDir(string iDataRoot)
        {
            return Path.Combine(LibraryRoot(iDataRoot), WorksDirName);
        }

        public static string MediaRoot(string iDataRoot, string iMediaId)
        {
            return Path.Combine(MediaDir(iDataRoot), iMediaId);
        }

        public static string WorkRoot(string iDataRoot, string iWorkId)
        {
            return Path.Combine(WorksDir(iDataRoot), iWorkId);
        }

        public static string MediaJsonPath(string iDataRoot, string iMediaId)
        {
            return Path.Combine(MediaRoot(iDataRoot, iMediaId), MediaJsonName);
        }

        public static string WorkJsonPath(string iDataRoot, string iWorkId)
        {
            return Path.Combine(WorkRoot(iDataRoot, iWorkId), WorkJsonName);
        }

        public static string ReaderRoot(string iDataRoot, string iMediaId, string iPersona)
        {
            return Path.Combine(MediaRoot(iDataRoot, iMediaId), ReadersDirName, iPersona);
        }

        public static string ReaderJsonPath(string iDataRoot, string iMediaId, string iPersona)
        {
            return Path.Combine(ReaderRoot(iDataRoot, iMediaId, iPersona), ReaderJsonName);
        }

        public static string ChapterDir(string iDataRoot, string iMediaId, string iPersona, string iChapterId)
        {
            return Path.Combine(ReaderRoot(iDataRoot, iMediaId, iPersona), ChaptersDirName, iChapterId);
        }

        public static string CharacterDir(string iDataRoot, string iMediaId, string iPersona, string iCharacterId)
        {
            return Path.Combine(ReaderRoot(iDataRoot, iMediaId, iPersona), CharactersDirName, iCharacterId);
        }

        // ── 驗證 ──────────────────────────────────────────────────────────
        public static bool IsValidId(string iValue)
        {
            return !string.IsNullOrEmpty(iValue) && k_IdPattern.IsMatch(iValue);
        }

        public static bool IsValidChapterId(string iValue)
        {
            return !string.IsNullOrEmpty(iValue) && k_ChapterIdPattern.IsMatch(iValue);
        }

        // ── 唯讀列舉 ──────────────────────────────────────────────────────
        // 數值影響：排序一律 `StringComparer.Ordinal` —— 與 UCL 那版同一把尺。
        //   ⚠ 不用預設字串比較：那會隨作業系統的文化設定變，而「順序不一樣」在對拍時
        //     跟「內容不一樣」長得一模一樣。

        /// <summary>列出所有 media id。目錄不存在 ⇒ 空清單（那是「還沒有任何 media」，不是錯）。</summary>
        public static List<string> ListMediaIds(string iDataRoot)
        {
            var aResult = new List<string>();
            string aRoot = MediaDir(iDataRoot);
            if (!Directory.Exists(aRoot)) return aResult;
            foreach (string aDir in Directory.GetDirectories(aRoot))
            {
                aResult.Add(Path.GetFileName(aDir));
            }
            aResult.Sort(StringComparer.Ordinal);
            return aResult;
        }

        /// <summary>列出所有 work id。</summary>
        public static List<string> ListWorkIds(string iDataRoot)
        {
            var aResult = new List<string>();
            string aRoot = WorksDir(iDataRoot);
            if (!Directory.Exists(aRoot)) return aResult;
            foreach (string aDir in Directory.GetDirectories(aRoot))
            {
                aResult.Add(Path.GetFileName(aDir));
            }
            aResult.Sort(StringComparer.Ordinal);
            return aResult;
        }

        /// <summary>列出某個 media 底下有哪些 reader persona。</summary>
        public static List<string> ListReaderPersonas(string iDataRoot, string iMediaId)
        {
            var aResult = new List<string>();
            if (string.IsNullOrEmpty(iMediaId)) return aResult;
            string aRoot = Path.Combine(MediaRoot(iDataRoot, iMediaId), ReadersDirName);
            if (!Directory.Exists(aRoot)) return aResult;
            foreach (string aDir in Directory.GetDirectories(aRoot))
            {
                aResult.Add(Path.GetFileName(aDir));
            }
            aResult.Sort(StringComparer.Ordinal);
            return aResult;
        }

        /// <summary>列出某位 reader 讀過的章號（四位數字，已排序）。</summary>
        public static List<string> ListChapterIds(string iDataRoot, string iMediaId, string iPersona)
        {
            var aResult = new List<string>();
            if (string.IsNullOrEmpty(iMediaId) || string.IsNullOrEmpty(iPersona)) return aResult;
            string aRoot = Path.Combine(ReaderRoot(iDataRoot, iMediaId, iPersona), ChaptersDirName);
            if (!Directory.Exists(aRoot)) return aResult;
            foreach (string aDir in Directory.GetDirectories(aRoot))
            {
                aResult.Add(Path.GetFileName(aDir));
            }
            aResult.Sort(StringComparer.Ordinal);
            return aResult;
        }
    }
}
