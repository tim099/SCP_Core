using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Json;

// 區塊職責：Library 的**總表**（media 逐筆的 metadata ＋ 讀者名單）。
// 物理意義：這是 `UCL_ReadingLibraryIO.ListMediaEntries` 移進 SCP_Core 的那一刀（TASK-0166 ①，
//          Tim 2026-09-17 拍板「剩下那批全部搬進 SCP_Core」）。
//          它只讀 metadata（media.json ＋ work.json ＋ readers 目錄名），**不碰章節正文** ——
//          瀏覽下拉、外部漫畫三態比對、scan 三方都吃它，而它們要的都是「有哪些、叫什麼」。
// 數值影響：純讀。缺 media.json／work.json ⇒ 該欄退回 mediaId，⛔ 不中斷整張表
//          （缺料的那一筆要**看得見**，不是整張表消失）。
namespace SCP.Core.Library
{
    /// <summary>Library 總表的一筆：一個 media ＋ 它作品層的名字與讀者名單。</summary>
    public sealed class SCP_MediaEntry
    {
        public string MediaId = "";
        public string MediaKind = "";
        public string WorkId = "";
        public string Title = "";

        /// <summary>作品層的搜尋用名稱（<c>title_original</c> ＋ <c>aliases</c>）。</summary>
        /// <remarks>⚠ 這一欄住在 <c>works/&lt;work&gt;/work.json</c>，不在 media.json；
        /// 查詢端只比 MediaId／WorkId／Title 的話，簡體、原文名、俗名一律 0 筆 ——
        /// 而 0 筆的樣子跟「這部作品不存在」一模一樣。</remarks>
        public List<string> Aliases = new List<string>();
        public List<string> Readers = new List<string>();
    }

    public static class SCP_LibraryCatalog
    {
        // ===========================================================
        // 區塊職責：全 Library 的 media 總表。
        // ⚠ 別名兩形狀（字串陣列／物件陣列）由 `SCP_LibraryRecall.AliasToString` 吸收 ——
        //   ⛔ 這裡不自己再判一次形狀（第二把尺會跟第一把分岔，而分岔時兩邊都不報錯）。
        // ===========================================================
        public static List<SCP_MediaEntry> ListMediaEntries(string iDataRoot)
        {
            var aResult = new List<SCP_MediaEntry>();
            string aRoot = SCP_LibraryStore.MediaDir(iDataRoot);
            if (!Directory.Exists(aRoot)) return aResult;

            foreach (string aDir in Directory.GetDirectories(aRoot))
            {
                var aEntry = new SCP_MediaEntry { MediaId = Path.GetFileName(aDir) };
                SCP_JsonData? aMedia = SCP_LibraryIO.LoadJson(
                    Path.Combine(aDir, SCP_LibraryStore.MediaJsonName), out _);
                if (aMedia != null)
                {
                    aEntry.MediaKind = aMedia.GetString(SCP_LibraryIO.Key_MediaKind, "");
                    aEntry.WorkId = aMedia.GetString(SCP_LibraryIO.Key_WorkId, "");
                }

                aEntry.Title = aEntry.MediaId;
                if (!string.IsNullOrEmpty(aEntry.WorkId))
                {
                    SCP_JsonData? aWork = SCP_LibraryIO.LoadJson(
                        SCP_LibraryStore.WorkJsonPath(iDataRoot, aEntry.WorkId), out _);
                    if (aWork != null)
                    {
                        aEntry.Title = aWork.GetString(SCP_LibraryIO.Key_Title, aEntry.MediaId);
                        string aOriginal = aWork.GetString(SCP_LibraryIO.Key_TitleOriginal, "");
                        if (!string.IsNullOrEmpty(aOriginal)) aEntry.Aliases.Add(aOriginal);
                        SCP_JsonData? aAliases = aWork.Contains(SCP_LibraryIO.Key_Aliases)
                            ? aWork[SCP_LibraryIO.Key_Aliases] : null;
                        if (aAliases != null && aAliases.IsArray)
                        {
                            for (int i = 0; i < aAliases.Count; i++)
                            {
                                string a = SCP_LibraryRecall.AliasToString(aAliases[i]);
                                if (!string.IsNullOrEmpty(a) && !aEntry.Aliases.Contains(a)) aEntry.Aliases.Add(a);
                            }
                        }
                    }
                }

                string aReadersRoot = Path.Combine(aDir, SCP_LibraryStore.ReadersDirName);
                if (Directory.Exists(aReadersRoot))
                {
                    foreach (string aReaderDir in Directory.GetDirectories(aReadersRoot))
                        aEntry.Readers.Add(Path.GetFileName(aReaderDir));
                    aEntry.Readers.Sort(StringComparer.OrdinalIgnoreCase);
                }

                aResult.Add(aEntry);
            }

            aResult.Sort((a, b) => string.Compare(a.MediaId, b.MediaId, StringComparison.Ordinal));
            return aResult;
        }
    }
}
