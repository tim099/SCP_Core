// 區塊職責：舊書本筆記庫（`BookNotes/<slug>/book.json`）的**唯一讀取點**。
// 物理意義：`cmd book op=writing` 與早安 brief 的「我在寫什麼」那一節共用本檔 ——
//           ⛔ 兩端各自 glob 一次 `BookNotes/*` 就是兩個會各自漂的真相源，
//           而漂掉的症狀是「CLI 說有三本、brief 說有兩本，兩邊都不報錯」。
// 數值影響：純唯讀。**永遠不寫**、不建目錄、不修檔。
//
// 🩸 為什麼回傳的是 (bool ok, why) 而不是「一個可能是空的清單」：
//   「一本都沒有」與「我沒去看」在畫面上同形，而人往那個空格裡填的一定是「沒事」。
//   ⇒ 讀不到的時候要**說得出為什麼**，讓上層印「未量」而不是印「0 本」。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Books
{
    /// <summary>一本 `origin=authored` 的書 —— 全部欄位都是從 `book.json` 讀出來的，沒有推導值。</summary>
    public sealed class SCP_AuthoredBook
    {
        public string Id = "";
        public string Title = "";
        public string AuthorPersona = "";
        public string Status = "";
        public string PublishStatus = "";
        public int ChapterCount;
        public DateTime LastWriteUtc;

        /// <summary>這一列是從哪個檔讀來的 —— ⭐ 讀數要一起說出「我是怎麼拿到這個值的」。</summary>
        public string SourcePath = "";

        /// <summary>尚未發布 ⇒ 算「寫到一半」。⚠ 判準是 `publish_status`，不是 `status`。</summary>
        public bool IsUnpublished =>
            !string.Equals(PublishStatus, "published", StringComparison.Ordinal);
    }

    public static class SCP_BookStore
    {
        /// <summary>舊 store 的根。⛔ 不是 `BookNotes/Library/`（那是另一個 store）。</summary>
        public static string BookNotesRoot(string iDataRoot)
        {
            return Path.Combine(iDataRoot, "BookNotes");
        }

        /// <summary>
        /// 列出所有 `origin=authored` 的書（**不過濾發布狀態** —— 過濾是呼叫端的事，
        /// 這裡只負責如實回報讀到什麼）。
        /// </summary>
        /// <returns>
        /// 讀得到 ⇒ true，<paramref name="oBooks"/> 有值（可能是空清單＝真的一本都沒有）。
        /// 讀不到 ⇒ false，<paramref name="oWhy"/> 說出原因 —— ⛔ 呼叫端這時**不准印 0**。
        /// </returns>
        public static bool TryListAuthored(string? iDataRoot,
                                           out List<SCP_AuthoredBook> oBooks,
                                           out string oWhy)
        {
            oBooks = new List<SCP_AuthoredBook>();
            oWhy = "";

            if (string.IsNullOrEmpty(iDataRoot))
            {
                oWhy = "沒給資料根";
                return false;
            }

            string aRoot = BookNotesRoot(iDataRoot!);
            if (!Directory.Exists(aRoot))
            {
                oWhy = "書庫目錄不存在：" + aRoot;
                return false;
            }

            string[] aDirs;
            try { aDirs = Directory.GetDirectories(aRoot); }
            catch (Exception e) { oWhy = "書庫目錄讀不動：" + e.Message; return false; }

            Array.Sort(aDirs, StringComparer.Ordinal);
            foreach (string aDir in aDirs)
            {
                string aJson = Path.Combine(aDir, "book.json");
                if (!File.Exists(aJson)) continue;      // `Library/` 之類的非書目錄自然被排除

                SCP_JsonData aData;
                try { aData = SCP_JsonData.Parse(File.ReadAllText(aJson, Encoding.UTF8)); }
                catch (Exception)
                {
                    // 壞掉的一本不該讓整份清單消失 —— 但也不准靜默跳過，
                    // 所以它以「讀不動」的樣子留在清單裡（欄位空、來源路徑在）。
                    oBooks.Add(new SCP_AuthoredBook
                    {
                        Id = Path.GetFileName(aDir),
                        Title = "（book.json 解析失敗）",
                        SourcePath = aJson,
                        LastWriteUtc = SafeWriteTime(aJson),
                    });
                    continue;
                }

                if (aData.GetString("origin", "") != "authored") continue;

                oBooks.Add(new SCP_AuthoredBook
                {
                    Id = aData.GetString("id", Path.GetFileName(aDir)),
                    Title = aData.GetString("title", ""),
                    AuthorPersona = aData.GetString("author_persona", ""),
                    Status = aData.GetString("status", ""),
                    PublishStatus = aData.GetString("publish_status", ""),
                    ChapterCount = CountChapters(aDir),
                    LastWriteUtc = SafeWriteTime(aJson),
                    SourcePath = aJson,
                });
            }
            return true;
        }

        /// <summary>
        /// 只回「寫到一半」的（authored ＋ 尚未發布）。
        /// <paramref name="iAuthorPersona"/> 非空 ⇒ 只留那個人的。
        /// </summary>
        public static bool TryListWriting(string? iDataRoot, string? iAuthorPersona,
                                          out List<SCP_AuthoredBook> oBooks, out string oWhy)
        {
            oBooks = new List<SCP_AuthoredBook>();
            if (!TryListAuthored(iDataRoot, out List<SCP_AuthoredBook> aAll, out oWhy)) return false;

            foreach (SCP_AuthoredBook aBook in aAll)
            {
                if (!aBook.IsUnpublished) continue;
                if (!string.IsNullOrEmpty(iAuthorPersona)
                    && !string.Equals(aBook.AuthorPersona, iAuthorPersona, StringComparison.Ordinal))
                    continue;
                oBooks.Add(aBook);
            }
            return true;
        }

        /// <summary>章數 —— 目錄不在就是 0（那是真的 0 章，不是讀不到）。</summary>
        static int CountChapters(string iBookDir)
        {
            string aDir = Path.Combine(iBookDir, "chapters");
            if (!Directory.Exists(aDir)) return 0;
            try { return Directory.GetFiles(aDir).Length; }
            catch (Exception) { return 0; }
        }

        static DateTime SafeWriteTime(string iPath)
        {
            try { return File.GetLastWriteTimeUtc(iPath); }
            catch (Exception) { return DateTime.MinValue; }
        }
    }
}
