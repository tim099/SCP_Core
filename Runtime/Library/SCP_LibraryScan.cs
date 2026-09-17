using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Books;
using SCP.Core.Io;
using SCP.Core.Json;

// 區塊職責：`op=scan` —— Library / Archive 的重複與異常**候選**審計（唯讀），以及
//          「已遷移 Archive」集合的讀取（`_migration/registry.json` 是唯一標記處）。
// 物理意義：`UCL_ReadingLibraryIO.ScanLibrary` 移進 SCP_Core（TASK-0166 ①，Tim 2026-09-17 拍板全搬）。
//          Q4 定案：**先印候選、人工核對** —— 本層不合併不搬移不改任何資料檔。
// 數值影響：唯一的寫入是報告檔 `BookNotes/_migration/scan_report.md`（機械產物，每次覆寫）；
//          資料層一個位元組都不動。
// ⚠ 判準沿 Plan_Library_Media_Migration 的實測教訓：**前綴法誤報 60%、title 法漏一半**
//   ⇒ 用 normalize 撒網、人工收網。normalize 相等 ＝ 候選，**不等於**同作品。
namespace SCP.Core.Library
{
    public static class SCP_LibraryScan
    {
        public const string MigrationDirName = "_migration";
        public const string RegistryJsonName = "registry.json";
        public const string ScanReportName = "scan_report.md";
        public const string ArchiveDirName = "Archive";

        // ===========================================================
        // 區塊職責：讀「已遷移 Archive」集合。
        // 物理意義：**Archive 不可修改**（Tim 鐵律），所以「已遷移」不寫進 Archive 本身，
        //          寫在 registry（`state=migrated` 的 record）。
        // 數值影響：唯讀；registry 缺檔／壞檔 → 空集合（fail-open：**寧可多列不可少列** ——
        //          少列的失效是「已裁決過的東西悄悄消失」，而那跟「沒有那筆」同形）。
        // ===========================================================
        public static HashSet<string> LoadMigratedArchiveSlugs(string iDataRoot)
        {
            var aOut = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string aPath = Path.Combine(SCP_BookStore.BookNotesRoot(iDataRoot), MigrationDirName, RegistryJsonName);
            SCP_JsonData? aReg = SCP_LibraryIO.LoadJson(aPath, out _);
            if (aReg == null || !aReg.Contains("records")) return aOut;
            SCP_JsonData aRecords = aReg["records"];
            if (aRecords == null || !aRecords.IsArray) return aOut;

            const string aPrefix = "BookNotes/Archive/";
            for (int i = 0; i < aRecords.Count; i++)
            {
                SCP_JsonData aRec = aRecords[i];
                if (aRec == null || !aRec.IsObject) continue;
                if (aRec.GetString("state", "") != "migrated") continue;
                string aSrc = aRec.GetString("source_id", "");
                if (aSrc.StartsWith(aPrefix, StringComparison.Ordinal))
                    aOut.Add(aSrc.Substring(aPrefix.Length).Trim().TrimEnd('/'));
            }
            return aOut;
        }

        // ===========================================================
        // 掃四類：
        //   A. Archive ↔ Library 疑似同作品（slug／title／title_original／aliases normalize 命中）
        //   B. Library 內部疑似重複（同 normalize title 但**不同 work_id** —— 同 work 多 media 是
        //      設計上的合法形狀，不列）
        //   C. reader 異常：資料夾名 unknown ／ 缺 reader.json ／ reader_persona 與資料夾名不一致
        //      （含大小寫不一致 —— NTFS 遮著它，Linux 上會把追回檔寫到版控外）
        //   D. Archive 讀不到 metadata 的 entry（book.json 缺或壞 —— 連被比對的資格都沒有，要人看）
        // ⚠ 已遷移的 Archive 預設不進候選（Tim 2026-08-07）；而**隱藏幾筆一定印出來** ——
        //   靜默隱藏＝下一隻閘門讀的是一份被裁剪過而它不知道的清單。
        // ===========================================================
        public static string ScanLibrary(string iDataRoot, out string? oReportPath, out string? oError,
                                         bool iShowMigrated = false)
        {
            oError = null;
            oReportPath = null;
            var aSb = new StringBuilder();
            List<SCP_MediaEntry> aMediaEntries = SCP_LibraryCatalog.ListMediaEntries(iDataRoot);
            HashSet<string> aMigrated = LoadMigratedArchiveSlugs(iDataRoot);
            int aHiddenMigrated = 0;

            // media 的 normalize 鍵集合（title／mediaId 去前綴／work_id／aliases）
            var aMediaKeys = new List<(SCP_MediaEntry Entry, HashSet<string> Keys)>();
            foreach (SCP_MediaEntry m in aMediaEntries)
            {
                var aKeys = new HashSet<string>();
                AddKey(aKeys, m.Title);
                AddKey(aKeys, m.WorkId);
                int aDash = m.MediaId.IndexOf('-');
                AddKey(aKeys, aDash > 0 ? m.MediaId.Substring(aDash + 1) : m.MediaId);
                SCP_JsonData? aWork = string.IsNullOrEmpty(m.WorkId) ? null
                    : SCP_LibraryIO.LoadJson(SCP_LibraryStore.WorkJsonPath(iDataRoot, m.WorkId), out _);
                if (aWork != null)
                {
                    AddKey(aKeys, aWork.GetString(SCP_LibraryIO.Key_TitleOriginal, ""));
                    SCP_JsonData? aAliases = aWork.Contains(SCP_LibraryIO.Key_Aliases)
                        ? aWork[SCP_LibraryIO.Key_Aliases] : null;
                    if (aAliases != null && aAliases.IsArray)
                        for (int i = 0; i < aAliases.Count; i++)
                            AddKey(aKeys, SCP_LibraryRecall.AliasToString(aAliases[i]));
                }
                aMediaKeys.Add((m, aKeys));
            }

            aSb.AppendLine("---");
            aSb.AppendLine("type: library_scan_report");
            aSb.AppendLine($"generated_at: {DateTime.Now:yyyy-MM-ddTHH:mm:sszzz}");
            aSb.AppendLine("generated: mechanical   # 每次 op=scan 覆寫；本工具唯讀，遷移一律人工");
            aSb.AppendLine("---");
            aSb.AppendLine();
            aSb.AppendLine("# 🔍 Library 審計報告（op=scan）");
            aSb.AppendLine();
            aSb.AppendLine($"- Library media：{aMediaEntries.Count} 個");

            // ── A + D：Archive 比對 ──
            string aArchiveRoot = Path.Combine(SCP_BookStore.BookNotesRoot(iDataRoot), ArchiveDirName);
            int aArchiveCount = 0, aHitCount = 0;
            var aSectionA = new StringBuilder();
            var aSectionD = new StringBuilder();
            if (Directory.Exists(aArchiveRoot))
            {
                foreach (string aDir in Directory.GetDirectories(aArchiveRoot))
                {
                    string aSlug = Path.GetFileName(aDir);
                    // `_` 開頭是系統目錄（_recommended／_search_reports…），不是書
                    if (aSlug.StartsWith("_", StringComparison.Ordinal)) continue;
                    if (!iShowMigrated && aMigrated.Contains(aSlug)) { aHiddenMigrated++; continue; }
                    aArchiveCount++;
                    SCP_JsonData? aBook = SCP_LibraryIO.LoadJson(Path.Combine(aDir, "book.json"), out string? aBookErr);
                    if (aBook == null)
                    {
                        aSectionD.AppendLine($"- `{aSlug}`：{aBookErr}");
                        continue;
                    }
                    string aTitle = aBook.GetString(SCP_LibraryIO.Key_Title, "");
                    string aTitleOriginal = aBook.GetString(SCP_LibraryIO.Key_TitleOriginal, "");
                    var aArchiveKeys = new HashSet<string>();
                    AddKey(aArchiveKeys, aSlug);
                    AddKey(aArchiveKeys, aTitle);
                    AddKey(aArchiveKeys, aTitleOriginal);
                    foreach ((SCP_MediaEntry m, HashSet<string> aKeys) in aMediaKeys)
                    {
                        bool aHit = false;
                        foreach (string k in aArchiveKeys)
                            if (aKeys.Contains(k)) { aHit = true; break; }
                        if (!aHit) continue;
                        aHitCount++;
                        aSectionA.AppendLine($"- Archive `{aSlug}`（{aTitle}） ↔ Library `{m.MediaId}`（{m.Title}）" +
                                             $"　readers: {string.Join(", ", m.Readers)}");
                    }
                }
            }
            aSb.AppendLine($"- Archive entry：{aArchiveCount} 個"
                           + (aHiddenMigrated > 0
                               ? $"（另 {aHiddenMigrated} 筆已遷移預設隱藏 —— `--arg show_migrated=true` 顯示）"
                               : ""));
            aSb.AppendLine();
            aSb.AppendLine($"## A. Archive ↔ Library 疑似同作品（{aHitCount} 組 —— 逐組人工裁決，不自動遷移）");
            aSb.AppendLine();
            aSb.Append(aSectionA.Length > 0 ? aSectionA.ToString() : "（無命中）\n");
            aSb.AppendLine();

            // ── B：Library 內部疑似重複 ──
            aSb.AppendLine("## B. Library 內部疑似重複（同名但不同 work_id —— arakawa 型爛帳的形狀）");
            aSb.AppendLine();
            var aByTitle = new Dictionary<string, List<SCP_MediaEntry>>();
            foreach (SCP_MediaEntry m in aMediaEntries)
            {
                string k = Normalize(m.Title);
                if (k.Length == 0) continue;
                if (!aByTitle.TryGetValue(k, out List<SCP_MediaEntry>? aList)) aByTitle[k] = aList = new List<SCP_MediaEntry>();
                aList.Add(m);
            }
            int aDupGroups = 0;
            foreach (KeyValuePair<string, List<SCP_MediaEntry>> kv in aByTitle)
            {
                var aWorkIds = new HashSet<string>();
                foreach (SCP_MediaEntry m in kv.Value) aWorkIds.Add(m.WorkId);
                if (kv.Value.Count < 2 || aWorkIds.Count < 2) continue;   // 同 work 多 media 合法
                aDupGroups++;
                aSb.AppendLine($"- 「{kv.Value[0].Title}」：" +
                               string.Join(" / ", kv.Value.ConvertAll(m => $"`{m.MediaId}`(work={m.WorkId})")));
            }
            if (aDupGroups == 0) aSb.AppendLine("（無命中）");
            aSb.AppendLine();

            // ── C：reader 異常 ──
            aSb.AppendLine("## C. reader 異常（unknown / 缺 reader.json / persona 與資料夾名不一致）");
            aSb.AppendLine();
            int aAnomalies = 0;
            foreach (SCP_MediaEntry m in aMediaEntries)
            {
                foreach (string aReader in m.Readers)
                {
                    string aReaderJson = SCP_LibraryStore.ReaderJsonPath(iDataRoot, m.MediaId, aReader);
                    if (aReader == "unknown")
                    {
                        aAnomalies++;
                        aSb.AppendLine($"- `{m.MediaId}/readers/unknown`：persona 解析失敗的 fallback 產物 —— " +
                                       "逐檔認領或併入正主，不可當真讀者");
                        continue;
                    }
                    if (!File.Exists(aReaderJson))
                    {
                        aAnomalies++;
                        aSb.AppendLine($"- `{m.MediaId}/readers/{aReader}`：缺 reader.json");
                        continue;
                    }
                    SCP_JsonData? aReader0 = SCP_LibraryIO.LoadJson(aReaderJson, out _);
                    string aDeclared = aReader0 != null ? aReader0.GetString(SCP_LibraryIO.Key_ReaderPersona, "") : "";
                    if (aDeclared != aReader)
                    {
                        aAnomalies++;
                        bool aCaseOnly = string.Equals(aDeclared, aReader, StringComparison.OrdinalIgnoreCase);
                        aSb.AppendLine($"- `{m.MediaId}/readers/{aReader}`：reader.json 宣告 `{aDeclared}`" +
                                       (aCaseOnly ? "（**大小寫不一致** —— NTFS 遮著，Linux 上追回檔會寫進版控外的 letters/）"
                                           : "（宣告與路徑不同人）"));
                    }
                }
            }
            if (aAnomalies == 0) aSb.AppendLine("（無異常）");
            aSb.AppendLine();
            if (aSectionD.Length > 0)
            {
                aSb.AppendLine("## D. Archive metadata 讀不到（連被比對的資格都沒有 —— 要人看）");
                aSb.AppendLine();
                aSb.Append(aSectionD);
                aSb.AppendLine();
            }
            aSb.AppendLine("> 本報告唯讀生成；**任何合併 / 搬移 / 改名都不由工具代辦**（Q3 定案：偵測自動、遷移人工）。");

            string aReport = aSb.ToString();
            try
            {
                string aDir = Path.Combine(SCP_BookStore.BookNotesRoot(iDataRoot), MigrationDirName);
                Directory.CreateDirectory(aDir);
                oReportPath = Path.Combine(aDir, ScanReportName);
                // ⚠ 報告本體是 `AppendLine` 組的 ⇒ 在 Windows 上本來就是 CRLF；
                //   走 WriteCrLf 是讓它**在哪個平台跑都一樣**，不是改版面（UCL 那側是原樣寫出）。
                SCP_TextFile.WriteCrLf(oReportPath, aReport);
            }
            catch (Exception e)
            {
                // 報告落檔失敗**不吞掉輸出** —— 印出來的那份還在，而「沒有報告」與「報告沒寫成檔」是兩件事
                oError = $"報告檔寫出失敗（內容仍在輸出中）：{e.Message}";
                oReportPath = null;
            }
            return aReport;
        }

        static void AddKey(HashSet<string> ioKeys, string? iRaw)
        {
            string k = Normalize(iRaw);
            if (k.Length > 0) ioKeys.Add(k);
        }

        /// <summary>normalize：小寫 ＋ 只留字母數字（含 CJK）—— 標點、空白、連字號全掃掉。</summary>
        /// <remarks>用途是**撒網不是判定**：normalize 相等 ＝ 候選，不等於同作品（人工收網）。</remarks>
        public static string Normalize(string? iRaw)
        {
            if (string.IsNullOrEmpty(iRaw)) return "";
            var aSb = new StringBuilder();
            foreach (char c in iRaw!.ToLowerInvariant())
                if (char.IsLetterOrDigit(c)) aSb.Append(c);
            return aSb.ToString();
        }
    }
}
