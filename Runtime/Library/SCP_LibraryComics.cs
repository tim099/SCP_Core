using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

// 區塊職責：掃描外部漫畫資料夾，與 Library 媒材比對。
// 物理意義：掃描服務接受宿主解析的根；Senate 入口讀設定快照。
// 數值影響：純讀；掃描警告交回呼叫端，不建立漫畫或閱讀資料。
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
        //   （同 `SCP_LibraryBookshelf` 的轉發警告 —— SCP_Core 沒有 logger）。
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
                            if (Path.GetFileName(aChDir).StartsWith(".", StringComparison.Ordinal)) continue;
                            try
                            {
                                int aPages = 0;
                                foreach (string aFile in Directory.GetFiles(aChDir))
                                    if (s_ImageExts.Contains(Path.GetExtension(aFile))) aPages++;
                                if (aPages > 0)
                                {
                                    aVol.Chapters.Add(Path.GetFileName(aChDir));
                                    aVol.PageCount += aPages;
                                }
                            }
                            catch { }
                        }
                    }
                    if (aVol.Chapters.Count == 0)
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

                    // TASK-0411：先證明有頁面才登記作品；工具、快取、空卷不因名稱成為漫畫。
                    if (aVol.PageCount == 0) continue;
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

        // ===========================================================
        // 區塊職責：回答「某部漫畫的某一話，要看的頁檔實際在哪」（`op=comic_pages`，TASK-0400）。
        // 物理意義：兩種來源、兩種版面 —— 外部 `<根>/<作品 卷>/<話>/<頁>.jpg`、內部
        //          `ArtGallery/Comic/<slug>/Chapters/NNN.md`（三位）＋ `RawImages/NNN_pNN.png`（從分鏡稿的圖片連結取）。
        //          呼叫端（agent）**不必自己拼路徑、也不必呼叫 python**。
        // 數值影響：純讀。內部漫畫優先（它**沒有**外部資料夾，⛔ 不要因為外部根沒設就說找不到）。
        // ⚠ 缺的頁要**列出來標缺**，⛔ 不靜默略過：略過的話「這話有 9 頁」與「這話 10 頁而第 4 頁掉了」同形，
        //   而逐頁看圖寫心得的人會把 9 頁當成全部。
        // ===========================================================

        /// <summary>某一話的一頁。<see cref="Exists"/> false ＝ 來源（分鏡稿連結）指到卻不在磁碟上。</summary>
        public sealed class SCP_ComicPage
        {
            public string Path = "";
            public bool Exists;
        }

        public sealed class SCP_ComicChapterPages
        {
            /// <summary>external／internal。</summary>
            public string Source = "";
            public string ChapterId = "";
            /// <summary>外部＝章資料夾；內部＝分鏡稿 .md（正文在那裡，圖在 RawImages）。</summary>
            public string ChapterPath = "";
            public List<SCP_ComicPage> Pages = new List<SCP_ComicPage>();
        }

        /// <summary>某部漫畫有哪些話（沒給 chapter_id 時用）。</summary>
        public sealed class SCP_ComicChapterIndex
        {
            public string Source = "";
            public List<string> ChapterIds = new List<string>();
        }

        static readonly Regex s_MarkdownImage = new Regex(@"!\[[^\]]*\]\(([^)\s]+)\)", RegexOptions.Compiled);

        static string? SlugOfComicMedia(string iMediaId)
            => iMediaId.StartsWith("comic-", StringComparison.Ordinal) && iMediaId.Length > "comic-".Length
                ? iMediaId.Substring("comic-".Length) : null;

        /// <summary>內部漫畫的話檔名：`0001` ⇒ `001`（三位；0 ⇒ 000）。</summary>
        static string InternalChapterName(string iChapterId)
            => int.TryParse(iChapterId, out int aN) && aN >= 0 ? aN.ToString("D3") : iChapterId;

        /// <summary>
        /// 列出某話的頁檔。回 null ＝ 說不出來（原因在 <paramref name="oError"/>，且會帶上「有哪些可選」）。
        /// </summary>
        public static SCP_ComicChapterPages? ListChapterPages(string iDataRoot, string? iComicRoot, string iMediaId,
                                                              string iChapterId, out string? oError)
        {
            oError = null;
            string? aSlug = SlugOfComicMedia(iMediaId);
            if (aSlug == null)
            {
                oError = $"media_id 必須是 `comic-<slug>`：`{iMediaId}`（漫畫一律獨立 comic media）";
                return null;
            }

            string aInternalDir = System.IO.Path.Combine(iDataRoot, InternalComicDirName, "Comic", aSlug);
            if (Directory.Exists(aInternalDir)) return ListInternalPages(aInternalDir, iChapterId, out oError);
            return ListExternalPages(iDataRoot, iComicRoot, iMediaId, iChapterId, out oError);
        }

        /// <summary>列出某部漫畫現有的話號（內部優先、外部其次）。回 null ＝ 找不到這部漫畫。</summary>
        public static SCP_ComicChapterIndex? ListChapterIds(string iDataRoot, string? iComicRoot, string iMediaId,
                                                            out string? oError)
        {
            oError = null;
            string? aSlug = SlugOfComicMedia(iMediaId);
            if (aSlug == null)
            {
                oError = $"media_id 必須是 `comic-<slug>`：`{iMediaId}`";
                return null;
            }
            var aOut = new SCP_ComicChapterIndex();
            string aInternalDir = System.IO.Path.Combine(iDataRoot, InternalComicDirName, "Comic", aSlug);
            if (Directory.Exists(aInternalDir))
            {
                aOut.Source = "internal";
                string aChapters = System.IO.Path.Combine(aInternalDir, "Chapters");
                if (Directory.Exists(aChapters))
                {
                    foreach (string aFile in Directory.GetFiles(aChapters, "*.md"))
                    {
                        string aName = System.IO.Path.GetFileNameWithoutExtension(aFile);
                        if (int.TryParse(aName, out int aN) && aN >= 0) aOut.ChapterIds.Add(aN.ToString("D4"));
                    }
                    aOut.ChapterIds.Sort(StringComparer.Ordinal);
                }
                return aOut;
            }
            SCP_ExternalComicSeries? aSeries = FindExternalSeries(iDataRoot, iComicRoot, iMediaId, out oError);
            if (aSeries == null) return null;
            aOut.Source = "external";
            foreach (SCP_ExternalComicVolume v in aSeries.Volumes) aOut.ChapterIds.AddRange(v.Chapters);
            aOut.ChapterIds.Sort(StringComparer.OrdinalIgnoreCase);
            return aOut;
        }

        static SCP_ExternalComicSeries? FindExternalSeries(string iDataRoot, string? iComicRoot, string iMediaId,
                                                           out string? oError)
        {
            oError = null;
            if (string.IsNullOrWhiteSpace(iComicRoot) || !Directory.Exists(iComicRoot))
            {
                oError = $"`{iMediaId}` 不是內部漫畫（ArtGallery/Comic 底下沒有它），而外部漫畫庫根沒設定或不存在：`{iComicRoot}`";
                return null;
            }
            List<SCP_ExternalComicSeries> aAll = ScanExternalComics(iDataRoot, iComicRoot, out string? aWarn);
            foreach (SCP_ExternalComicSeries s in aAll)
                if (string.Equals(s.MediaId, iMediaId, StringComparison.OrdinalIgnoreCase) && s.Volumes.Count > 0)
                    return s;
            int aReal = 0;
            foreach (SCP_ExternalComicSeries s in aAll) if (s.Volumes.Count > 0) aReal++;
            oError = $"外部漫畫庫 `{iComicRoot}` 找不到 `{iMediaId}`（掃到 {aReal} 個系列；用 op=comics 看清單）"
                     + (aWarn != null ? "　⚠ " + aWarn : "");
            return null;
        }

        static SCP_ComicChapterPages? ListExternalPages(string iDataRoot, string? iComicRoot, string iMediaId,
                                                        string iChapterId, out string? oError)
        {
            SCP_ExternalComicSeries? aSeries = FindExternalSeries(iDataRoot, iComicRoot, iMediaId, out oError);
            if (aSeries == null) return null;

            foreach (SCP_ExternalComicVolume v in aSeries.Volumes)
            {
                bool aHas = false;
                foreach (string c in v.Chapters)
                    if (string.Equals(c, iChapterId, StringComparison.OrdinalIgnoreCase)) { aHas = true; break; }
                if (!aHas) continue;

                // 章資料夾存在就用它；否則是「根目錄直接放圖（單章）」那種卷
                string aChDir = System.IO.Path.Combine(v.FolderPath, iChapterId);
                string aDir = Directory.Exists(aChDir) && Array.Exists(Directory.GetFiles(aChDir),
                    f => s_ImageExts.Contains(System.IO.Path.GetExtension(f))) ? aChDir : v.FolderPath;
                var aOut = new SCP_ComicChapterPages { Source = "external", ChapterId = iChapterId, ChapterPath = aDir.Replace('\\', '/') };
                var aFiles = new List<string>();
                foreach (string f in Directory.GetFiles(aDir))
                    if (s_ImageExts.Contains(System.IO.Path.GetExtension(f))) aFiles.Add(f);
                aFiles.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string f in aFiles)
                    aOut.Pages.Add(new SCP_ComicPage { Path = f.Replace('\\', '/'), Exists = true });
                return aOut;
            }

            var aIds = new List<string>();
            foreach (SCP_ExternalComicVolume v in aSeries.Volumes) aIds.AddRange(v.Chapters);
            aIds.Sort(StringComparer.OrdinalIgnoreCase);
            oError = $"`{iMediaId}` 沒有第 `{iChapterId}` 話"
                     + (aIds.Count == 0 ? "（這部沒有任何話）" : $"（有 {aIds.Count} 話：{aIds[0]} … {aIds[aIds.Count - 1]}）");
            return null;
        }

        static SCP_ComicChapterPages? ListInternalPages(string iComicDir, string iChapterId, out string? oError)
        {
            oError = null;
            string aName = InternalChapterName(iChapterId);
            string aChaptersDir = System.IO.Path.Combine(iComicDir, "Chapters");
            string aMd = System.IO.Path.Combine(aChaptersDir, aName + ".md");
            if (!File.Exists(aMd))
            {
                var aIds = new List<string>();
                if (Directory.Exists(aChaptersDir))
                    foreach (string f in Directory.GetFiles(aChaptersDir, "*.md"))
                        aIds.Add(System.IO.Path.GetFileNameWithoutExtension(f));
                aIds.Sort(StringComparer.Ordinal);
                oError = $"內部漫畫沒有分鏡稿 `{aMd.Replace('\\', '/')}`"
                         + (aIds.Count == 0 ? "（Chapters 底下沒有任何 .md）" : $"（有：{string.Join("、", aIds)}）");
                return null;
            }

            var aOut = new SCP_ComicChapterPages { Source = "internal", ChapterId = iChapterId, ChapterPath = aMd.Replace('\\', '/') };
            string aText = File.ReadAllText(aMd, Encoding.UTF8);
            foreach (Match m in s_MarkdownImage.Matches(aText))
            {
                string aRef = m.Groups[1].Value;
                if (aRef.Contains("://")) continue;   // 外部網址不是頁檔
                string aFull = System.IO.Path.GetFullPath(System.IO.Path.Combine(aChaptersDir, aRef)).Replace('\\', '/');
                aOut.Pages.Add(new SCP_ComicPage { Path = aFull, Exists = File.Exists(aFull) });
            }
            return aOut;
        }
    }
}
