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

        // ⚠ registry 的 `state` 只有這三種**被程式讀**（其餘值會被當成「還沒裁決」）：
        //   · migrated     ＝ 這筆 Archive 的內容已經進正本 ⇒ 掃描預設隱藏（重複資料）
        //   · kept_archive ＝ **刻意不遷**，Archive 就是它的正本 ⇒ ⛔ 不隱藏，但標成已裁決
        //   · born_new     ＝ 這筆資料是新流程直接在 Library 建的，**不經遷移**（TASK-0171 ④）
        // 🩸 TASK-0171：本檔原本只認得 `migrated` ⇒ 帳本回答的是「誰用過 migrate」，
        //   而「刻意不遷」與「還沒遷」在帳上同形、「新流程直接建」在帳上根本不存在。
        public const string StateMigrated = "migrated";
        public const string StateKeptArchive = "kept_archive";
        public const string StateBornNew = "born_new";

        /// <summary>registry.json 的絕對路徑（唯一入口 —— ⛔ 不要在別處各自組一次）。</summary>
        public static string RegistryPath(string iDataRoot)
            => Path.Combine(SCP_BookStore.BookNotesRoot(iDataRoot), MigrationDirName, RegistryJsonName);

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
            foreach (KeyValuePair<string, ArchiveDecision> kv in LoadArchiveDecisions(iDataRoot))
                if (kv.Value.State == StateMigrated) aOut.Add(kv.Key);
            return aOut;
        }

        /// <summary>registry 對某個 Archive slug 的裁決：狀態＋人話理由（理由可以是空字串）。</summary>
        public readonly struct ArchiveDecision
        {
            public readonly string State;
            public readonly string Reason;
            public ArchiveDecision(string iState, string iReason) { State = iState; Reason = iReason; }
        }

        // ===========================================================
        // 區塊職責：把 registry 裡**所有對 Archive 的裁決**讀成一張表（不只 migrated）。
        // 物理意義：「已裁決」與「還沒裁決」是兩件事，而**處置不同**：
        //          已遷移 ⇒ 隱藏（重複）／刻意不遷 ⇒ 顯示但標註（它就是正本）／沒有紀錄 ⇒ 要人看。
        // 數值影響：唯讀，一次檔案讀取；缺檔／壞檔 → 空表（fail-open，理由同下面那支）。
        // ⚠ 同一個 slug 有多筆紀錄時**取最後一筆** —— registry 是 append-only 的帳，
        //   後面那筆是比較新的決定；⛔ 不合併兩筆（合併＝替人重寫他的決定）。
        // ===========================================================
        public static Dictionary<string, ArchiveDecision> LoadArchiveDecisions(string iDataRoot)
        {
            var aOut = new Dictionary<string, ArchiveDecision>(StringComparer.OrdinalIgnoreCase);
            SCP_JsonData? aReg = SCP_LibraryIO.LoadJson(RegistryPath(iDataRoot), out _);
            if (aReg == null || !aReg.Contains("records")) return aOut;
            SCP_JsonData aRecords = aReg["records"];
            if (aRecords == null || !aRecords.IsArray) return aOut;

            const string aPrefix = "BookNotes/Archive/";
            for (int i = 0; i < aRecords.Count; i++)
            {
                SCP_JsonData aRec = aRecords[i];
                if (aRec == null || !aRec.IsObject) continue;
                string aState = aRec.GetString("state", "");
                if (aState != StateMigrated && aState != StateKeptArchive) continue;
                string aSrc = aRec.GetString("source_id", "");
                if (!aSrc.StartsWith(aPrefix, StringComparison.Ordinal)) continue;
                string aSlug = aSrc.Substring(aPrefix.Length).Trim().TrimEnd('/');
                aOut[aSlug] = new ArchiveDecision(aState, aRec.GetString("disposition", ""));
            }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：往 registry append 一筆紀錄（唯一寫入端）。
        // 物理意義：registry 是**帳**不是快取 ⇒ append-only、不改既有紀錄、不排序。
        // 數值影響：`iDedupeSourceId` 已經有紀錄時**零寫入**回 false ——
        //          重跑 `media_init` 是常態（它自己就是冪等的），帳不該因此長出第二筆。
        // ⚠ 寫回時整份重新序列化（2 空格＋冒號後空格，貼齊手寫的原樣）——
        //   ⛔ 不用字串接龍插一段：那會產生第二種 JSON 寫法，而壞掉的樣子是「檔在、解析不了」。
        // ===========================================================
        public static bool AppendRecord(string iDataRoot, SCP_JsonData iRecord, string iDedupeSourceId,
                                        out string? oError)
        {
            oError = null;
            string aPath = RegistryPath(iDataRoot);
            SCP_JsonData? aReg = SCP_LibraryIO.LoadJson(aPath, out string? aErr);
            if (aReg == null) { oError = aErr ?? "registry 讀不回來"; return false; }
            if (!aReg.Contains("records") || !aReg["records"].IsArray)
            { oError = "registry 沒有 records 陣列 —— ⛔ 不自己建一份，那會蓋掉一份讀不懂的帳"; return false; }

            SCP_JsonData aRecords = aReg["records"];
            for (int i = 0; i < aRecords.Count; i++)
                if (aRecords[i].IsObject
                    && string.Equals(aRecords[i].GetString("source_id", ""), iDedupeSourceId,
                                     StringComparison.OrdinalIgnoreCase))
                    return false;                                  // 已經在帳上 ⇒ 不重複記

            aRecords.Add(iRecord);
            try
            {
                SCP_TextFile.WriteCrLf(aPath,
                    SCP_JsonWriter.Write(aReg, SCP_JsonStyle.Default.WithIndent("  ")) + "\n");
            }
            catch (Exception e) { oError = $"registry 寫不進去：{e.GetType().Name}: {e.Message}"; return false; }
            return true;
        }

        // ===========================================================
        // 區塊職責：`op=media_init` 建出一個**新流程直接生在 Library 的 media** 時記一筆。
        // 物理意義：TASK-0171 ④ —— 遷移帳本原本只記「誰用過 migrate」，
        //          於是 StreamWatch／閱讀流程直接建的 media 在帳上永遠是「沒遷」，
        //          而那跟「該遷還沒遷」同形。⇒ 這一筆讓帳回答「資料現在在哪」。
        // 數值影響：只在 media.json **真的被建立**那一次呼叫；重跑 ⇒ AppendRecord 判重、零寫入。
        // ⛔ 它**不宣告**任何 Archive slug 已被取代 —— 那個連結是人的判斷（`op=scan` 的 A 類候選），
        //    工具替它簽名就是替遷移決策簽名。
        // ===========================================================
        public static bool RecordBornNew(string iDataRoot, string iMediaId, string iWorkId, string iMediaKind,
                                         string iPersona, out string? oError)
        {
            string aSourceId = "Library/media/" + iMediaId;
            SCP_JsonData aRec = SCP_JsonData.NewObject()
                .Set("source_id", aSourceId)
                .Set("state", StateBornNew)
                .Set("target_work_id", iWorkId)
                .Set("target_media_id", iMediaId)
                .Set("media_kind", iMediaKind ?? "")
                .Set("target_reader", iPersona ?? "")
                .Set("created_at", SCP_LibraryIO.Today())
                .Set("decided_by", "（機械）op=media_init 直接在新 store 建，未經遷移")
                .Set("disposition", "新流程產物 ⇒ 不是遷移待辦；⛔ 本筆不宣告任何 Archive slug 已被取代");
            return AppendRecord(iDataRoot, aRec, aSourceId, out oError);
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
            Dictionary<string, ArchiveDecision> aDecisions = LoadArchiveDecisions(iDataRoot);
            int aHiddenMigrated = 0;
            var aSectionKept = new StringBuilder();
            int aKeptCount = 0;

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
                    aDecisions.TryGetValue(aSlug, out ArchiveDecision aDecision);
                    if (!iShowMigrated && aDecision.State == StateMigrated) { aHiddenMigrated++; continue; }
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
                        // 🩸 TASK-0171 ③：**刻意不遷**的那些，命中了也不算候選 ——
                        //   它們已經有人裁決過，繼續列在 A 類就跟「還沒有人看過」同形，
                        //   而那正是這張單要拆開的那一格。⛔ 但它們不隱藏（Archive 就是它的正本）。
                        if (aDecision.State == StateKeptArchive)
                        {
                            aKeptCount++;
                            aSectionKept.AppendLine($"- Archive `{aSlug}`（{aTitle}） ↔ Library `{m.MediaId}`（{m.Title}）"
                                + (aDecision.Reason.Length > 0 ? $"　⛔ 刻意不遷：{aDecision.Reason}" : "　⛔ 刻意不遷"));
                            continue;
                        }
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
            // ⚠ 這一節**一定印**（0 組也印）—— 「已裁決不遷」如果只在有資料時才出現，
            //   那麼「沒有人裁決過」與「這份報告沒有這一節」在讀的人眼裡又會同形。
            aSb.AppendLine($"## A′. 已裁決：**刻意不遷**（{aKeptCount} 組 —— registry `state=kept_archive`，"
                           + "⛔ 不是候選、也不隱藏：Archive 就是它們的正本）");
            aSb.AppendLine();
            aSb.Append(aSectionKept.Length > 0 ? aSectionKept.ToString() : "（無）\n");
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
