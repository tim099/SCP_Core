using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

// 區塊職責：**外部實體漫畫庫**（本機資料夾，例如 D:\commic）的掃描，與 Library 既有 media 的三態比對。
// 物理意義：`UCL_ReadingLibraryIO` 的漫畫那一叢移進 SCP_Core（TASK-0166 ①，Tim 2026-09-17 拍板全搬）。
// ⚠ **根目錄不由本層決定**：`ScanExternalComics` 吃一個 `iComicRoot` 參數。
//   理由是身分層而不是偷懶 —— 那個根的真相源是 `UCL_ProjectEditorPrefs`（Unity EditorPrefs，
//   不上 git、per-project 隔離），而 SCP_Core 叫不到 Unity。
//   ⛔ 讓本層自己去讀 `.comic_root.local` 快照 ＝ 同一個量有**兩個**真相源，而它們分岔時
//   兩邊都讀得出一個「看起來正常」的路徑 ⇒ 沒有任何一層會喊。
//   ⇒ 解析根是**呼叫端的職責**（Editor 讀 prefs／CLI 讀快照或吃 --arg），掃描只有這一份。
// 數值影響：純讀。掃不到根 ⇒ 回空清單（⛔ 不丟例外 —— 沒設定漫畫庫是常態，不是錯誤）。
namespace SCP.Core.Library
{
    /// <summary>外部漫畫資料夾與 Library 的比對結果。</summary>
    public enum SCP_ComicMatchStatus
    {
        /// <summary>🟢 已在 Library 建檔且本機實體資料夾存在。</summary>
        Synced,
        /// <summary>🟡 已在 Library 建檔但本機實體資料夾失聯。</summary>
        MissingSource,
        /// <summary>⚪ 本機實體資料夾存在但尚未在 Library 建檔。</summary>
        Unregistered,
    }

    public sealed class SCP_ExternalComicVolume
    {
        public string FolderName = "";
        public string FolderPath = "";
        public string VolumeLabel = "";
        public List<string> Chapters = new List<string>();
        public int PageCount = 0;
    }

    public sealed class SCP_ExternalComicSeries
    {
        public string SeriesName = "";
        public string Slug = "";
        public string MediaId = "";
        public List<SCP_ExternalComicVolume> Volumes = new List<SCP_ExternalComicVolume>();
        public int TotalChapters = 0;
        public int TotalPages = 0;
        public SCP_ComicMatchStatus Status = SCP_ComicMatchStatus.Unregistered;
        public bool HasWorkJson = false;
        public bool HasMediaJson = false;
        public string RegisteredTitle = "";
    }

    public static class SCP_LibraryComics
    {
        /// <summary>給 Python／CLI 唯讀消費的本機快照檔名（Editor 端 write-on-change 落盤；gitignored）。</summary>
        public const string ComicRootSnapshotFileName = ".comic_root.local";

        /// <summary>同事自己創作的內部漫畫住這裡 —— 它們**沒有**外部實體資料夾，⛔ 不算失聯。</summary>
        public const string InternalComicDirName = "ArtGallery";

        static readonly Regex s_VolumeRegex = new Regex(
            @"^(.*?)[ _\.\-]+(?:[vV]ol\.?|[vV]olume|第)?\s*(\d{1,4})(?:[卷冊話期])?$",
            RegexOptions.Compiled);

        static readonly HashSet<string> s_ImageExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".gif"
        };

        /// <summary>解析資料夾名為系列名與卷數（"Hunter x Hunter 01" → "Hunter x Hunter" / "01"）。</summary>
        /// <remarks>認不出卷號時回**整個資料夾名**當系列名、卷號給 "01"，
        /// ⛔ 不回空字串 —— 空的系列名會讓那一疊書從清單上消失，而「消失」跟「沒有」同形。</remarks>
        public static void ParseSeriesAndVolume(string? iFolderName, out string oSeriesName, out string oVolumeLabel)
        {
            if (string.IsNullOrWhiteSpace(iFolderName))
            {
                oSeriesName = "";
                oVolumeLabel = "01";
                return;
            }

            string aName = iFolderName!.Trim();
            Match aMatch = s_VolumeRegex.Match(aName);
            if (aMatch.Success && aMatch.Groups.Count >= 3)
            {
                oSeriesName = aMatch.Groups[1].Value.Trim();
                oVolumeLabel = aMatch.Groups[2].Value.Trim();
                if (string.IsNullOrEmpty(oSeriesName)) oSeriesName = aName;
            }
            else
            {
                oSeriesName = aName;
                oVolumeLabel = "01";
            }
        }

        /// <summary>系列名 → slug（"Hunter x Hunter" → "hunter-x-hunter"）。</summary>
        public static string NormalizeSeriesSlug(string? iRaw)
        {
            if (string.IsNullOrWhiteSpace(iRaw)) return "";
            var aSb = new StringBuilder();
            foreach (char c in iRaw!.ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) aSb.Append(c);
                else if (c == ' ' || c == '_' || c == '-') aSb.Append('-');
            }
            string aSlug = aSb.ToString();
            while (aSlug.Contains("--")) aSlug = aSlug.Replace("--", "-");
            return aSlug.Trim('-');
        }

        // ===========================================================
        // 區塊職責：掃外部漫畫庫 → 聚合成系列清單 → 與 Library 既有 media 三態比對。
        // 數值影響：純讀（不建檔、不改 Library）。
        // ⚠ <paramref name="oWarning"/>：掃描途中的 IO 例外交回呼叫端，⛔ 本層不自己開 log 管道
        //   （同 `SCP_LibraryBookshelf` 的轉發警告 —— SCP_Core 叫不到 Unity 的 Debug）。
        //   🩸 而它**不是**「沒有錯誤就是空的」：根不存在與掃壞掉都回空清單，
        //   分辨它們靠的就是這個字串。
        // ===========================================================
        public static List<SCP_ExternalComicSeries> ScanExternalComics(string iDataRoot, string? iComicRoot,
                                                                       out string? oWarning)
        {
            oWarning = null;
            var aResults = new List<SCP_ExternalComicSeries>();
            if (string.IsNullOrWhiteSpace(iComicRoot) || !Directory.Exists(iComicRoot)) return aResults;

            var aSeriesMap = new Dictionary<string, SCP_ExternalComicSeries>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string aDir in Directory.GetDirectories(iComicRoot))
                {
                    string aFolderName = Path.GetFileName(aDir);
                    if (string.IsNullOrEmpty(aFolderName) || aFolderName.StartsWith(".", StringComparison.Ordinal)) continue;

                    ParseSeriesAndVolume(aFolderName, out string aSeriesName, out string aVolumeLabel);
                    if (string.IsNullOrEmpty(aSeriesName)) continue;

                    if (!aSeriesMap.TryGetValue(aSeriesName, out SCP_ExternalComicSeries? aSeries))
                    {
                        string aSlug = NormalizeSeriesSlug(aSeriesName);
                        aSeries = new SCP_ExternalComicSeries
                        {
                            SeriesName = aSeriesName,
                            Slug = aSlug,
                            MediaId = "comic-" + aSlug,
                        };
                        aSeriesMap[aSeriesName] = aSeries;
                    }

                    var aVol = new SCP_ExternalComicVolume
                    {
                        FolderName = aFolderName,
                        FolderPath = aDir,
                        VolumeLabel = aVolumeLabel,
                    };

                    string[] aChapterDirs = Directory.GetDirectories(aDir);
                    if (aChapterDirs.Length > 0)
                    {
                        Array.Sort(aChapterDirs, StringComparer.OrdinalIgnoreCase);
                        foreach (string aChDir in aChapterDirs)
                        {
                            aVol.Chapters.Add(Path.GetFileName(aChDir));
                            try
                            {
                                foreach (string aFile in Directory.GetFiles(aChDir))
                                    if (s_ImageExts.Contains(Path.GetExtension(aFile))) aVol.PageCount++;
                            }
                            catch { }
                        }
                    }
                    else
                    {
                        // 根目錄直接放圖（單章）
                        try
                        {
                            foreach (string aFile in Directory.GetFiles(aDir))
                                if (s_ImageExts.Contains(Path.GetExtension(aFile))) aVol.PageCount++;
                            if (aVol.PageCount > 0) aVol.Chapters.Add("0001");
                        }
                        catch { }
                    }

                    aSeries.Volumes.Add(aVol);
                }
            }
            catch (Exception e)
            {
                oWarning = $"ScanExternalComics 掃描 {iComicRoot} 途中失敗：{e.Message}";
            }

            List<SCP_MediaEntry> aAllMedia = SCP_LibraryCatalog.ListMediaEntries(iDataRoot);
            var aMatchedMediaIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (KeyValuePair<string, SCP_ExternalComicSeries> aKvp in aSeriesMap)
            {
                SCP_ExternalComicSeries aSeries = aKvp.Value;
                aSeries.Volumes.Sort((a, b) => string.Compare(a.VolumeLabel, b.VolumeLabel, StringComparison.OrdinalIgnoreCase));

                int aTotalCh = 0, aTotalPages = 0;
                foreach (SCP_ExternalComicVolume v in aSeries.Volumes)
                {
                    aTotalCh += v.Chapters.Count;
                    aTotalPages += v.PageCount;
                }
                aSeries.TotalChapters = aTotalCh;
                aSeries.TotalPages = aTotalPages;

                SCP_MediaEntry? aMatched = aAllMedia.Find(
                    m => string.Equals(m.MediaId, aSeries.MediaId, StringComparison.OrdinalIgnoreCase));
                if (aMatched != null)
                {
                    aSeries.HasMediaJson = true;
                    aSeries.RegisteredTitle = aMatched.Title;
                    aSeries.Status = SCP_ComicMatchStatus.Synced;
                    aMatchedMediaIds.Add(aMatched.MediaId);
                }
                else
                {
                    aSeries.HasWorkJson = File.Exists(SCP_LibraryStore.WorkJsonPath(iDataRoot, aSeries.Slug));
                    aSeries.Status = SCP_ComicMatchStatus.Unregistered;
                }

                aResults.Add(aSeries);
            }

            // 已建檔但本機目錄失聯（MissingSource）
            foreach (SCP_MediaEntry aMedia in aAllMedia)
            {
                if (aMedia.MediaKind != "comic" || aMatchedMediaIds.Contains(aMedia.MediaId)) continue;

                // ⛔ 同事自己畫的內部漫畫不算失聯 —— 它們本來就沒有外部資料夾。
                string aSlug = aMedia.MediaId.StartsWith("comic-", StringComparison.Ordinal)
                    ? aMedia.MediaId.Substring("comic-".Length) : aMedia.MediaId;
                string aInternalPath = Path.Combine(iDataRoot, InternalComicDirName, "Comic", aSlug);
                if (Directory.Exists(aInternalPath)) continue;

                aResults.Add(new SCP_ExternalComicSeries
                {
                    SeriesName = aMedia.Title,
                    RegisteredTitle = aMedia.Title,
                    Slug = aSlug,
                    MediaId = aMedia.MediaId,
                    HasMediaJson = true,
                    HasWorkJson = true,
                    Status = SCP_ComicMatchStatus.MissingSource,
                });
            }

            aResults.Sort((a, b) => string.Compare(a.SeriesName, b.SeriesName, StringComparison.OrdinalIgnoreCase));
            return aResults;
        }
    }
}
