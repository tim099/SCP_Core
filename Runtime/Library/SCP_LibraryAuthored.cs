using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Books;
using SCP.Core.Json;

// 區塊職責：**寫書線**（authored）那四欄的讀寫、舊 store ↔ 新 store 的逐欄對拍（③）與搬遷（④）。
// 物理意義：`UCL_ReadingLibraryIO` 的 authored 家族移進 SCP_Core（TASK-0166 ①，Tim 2026-09-17 拍板全搬）。
//          舊 store ＝ `BookNotes/<slug>/book.json`；新 store ＝ `BookNotes/Library/works/<work_id>/work.json`。
// 數值影響：`TrySetWorkAuthored` **空值不落盤** ⇒ 沒帶寫書線的 work.json 逐位元組不變；
//          `DiffWorkAuthored` 純讀；`MigrateAuthoredWork` 不給 confirm ⇒ 零寫入。
// ⚠ 本檔最重要的一條判準：**「兩邊都空」與「全對」不可以回同一個值** ——
//   逐欄比兩個空字串會得到「相同」，於是「還沒搬」會回報「搬好了」。
//   同族第二格：**欄位全對而正文容器對不上**也不准產生 ✓（那一格是 2026-09-10 自己咬到自己的）。
namespace SCP.Core.Library
{
    /// <summary>正文容器的存在性讀數 —— ⚠ 三態，⛔ 不是檔數。</summary>
    /// <remarks>🩸 現行讀取端數章數用「目錄不存在就回 0」⇒ **容器放錯位置＝靜默 0**，
    /// 而第一本要搬的那部正好真的是 0 章 ⇒ 兩者同形，**搬壞了會長得像搬對了**。
    /// ⇒ 把「沒有這個目錄」與「有目錄但 0 個檔」做成兩個值，⛔ 不留一個 0 讓人去猜
    /// （人往空格裡填的一定是成功）。</remarks>
    public enum SCP_WorkProseState { NoDir, EmptyDir, HasFiles }

    /// <summary>寫書線四欄。</summary>
    public sealed class SCP_WorkAuthored
    {
        public string AuthorPersona = "";
        public string Status = "";
        public string PublishStatus = "";
        public string Origin = "";

        /// <summary>四欄全空 ＝ 這份 work.json 上沒有寫書線（⛔ 不等於「它不是 authored」，只是這裡沒寫）。</summary>
        public bool IsEmpty => AuthorPersona.Length == 0 && Status.Length == 0
                               && PublishStatus.Length == 0 && Origin.Length == 0;

        /// <summary>對寫書線讀取端可見的唯一條件 —— `origin` 必須**逐字**是 `authored`。</summary>
        public bool VisibleToWritingLine => Origin == SCP_LibraryAuthored.OriginAuthored;
    }

    public enum SCP_AuthoredDiffOutcome
    {
        /// <summary>舊 store 那本 `book.json` 不存在 ⇒ 沒有比較的左邊（⛔ 不是「值不同」）。</summary>
        OldStoreMissing,
        /// <summary>新 store 那份 `work.json` 不存在 ⇒ 還沒建（⛔ 不是「值不同」）。</summary>
        NewStoreMissing,
        /// <summary>任一邊解析不動 ⇒ ⛔ 不回報「不同」，那會把壞檔講成搬壞了。</summary>
        ParseFailed,
        /// <summary>兩邊都沒有寫書線四欄 ⇒ 這本不是 authored，⛔ 對拍沒有通過，只是無事可拍。</summary>
        NeitherHasWritingLine,
        /// <summary>舊 store 有寫書線而新 store 四欄全空 ⇒ **還沒搬**（⛔ 掉進 AllMatch 就是綠得最假的一格）。</summary>
        NewStoreNoWritingLine,
        /// <summary>有欄位對不上 ⇒ `oMismatchedFields` 逐欄指名。</summary>
        Mismatch,
        /// <summary>四欄逐欄相同，**而正文容器對不上**（舊有檔、新沒有）⇒ 搬下去會靜默丟掉正文。</summary>
        FieldsMatchProseMissing,
        /// <summary>四欄逐欄相同、新 store 真的有寫書線，且正文容器沒有遺漏。</summary>
        AllMatch,
    }

    public enum SCP_AuthoredMigrateOutcome
    {
        /// <summary>舊 store 沒有這本 ⇒ 沒有可搬的來源（⛔ 不是「搬完了」）。</summary>
        OldStoreMissing,
        /// <summary>舊 store 的 book.json 解析不動 ⇒ ⛔ 不當成「沒有內容」。</summary>
        ParseFailed,
        /// <summary>舊 store 那本 `origin` 不是 `authored` ⇒ 這支不搬它。</summary>
        NotAuthored,
        /// <summary>沒給 confirm ⇒ 只回計畫，**一個位元組都沒寫**。</summary>
        Planned,
        /// <summary>真的搬了（報告裡附回讀對拍的結果）。</summary>
        Migrated,
    }

    public static class SCP_LibraryAuthored
    {
        public const string Key_AuthorPersona = "author_persona";
        public const string Key_PublishStatus = "publish_status";
        public const string Key_Origin = "origin";
        public const string OriginAuthored = "authored";

        /// <summary>正文容器名 —— 舊 store 與新 store **相對版面相同**，⛔ 不另立一套舊版面的名字。</summary>
        public const string WorkChaptersDirName = "chapters";
        public const string WorkArcsDirName = "arcs";

        /// <summary>authored 正文的容器：`works/&lt;work_id&gt;/chapters/`。</summary>
        public static string WorkChaptersRoot(string iDataRoot, string iWorkId)
            => Path.Combine(SCP_LibraryStore.WorkRoot(iDataRoot, iWorkId), WorkChaptersDirName);

        /// <summary>authored 卷／弧的容器：`works/&lt;work_id&gt;/arcs/`。</summary>
        public static string WorkArcsRoot(string iDataRoot, string iWorkId)
            => Path.Combine(SCP_LibraryStore.WorkRoot(iDataRoot, iWorkId), WorkArcsDirName);

        /// <summary>舊 store 的草稿檔位置（四欄的事實源）。</summary>
        public static string OldStoreBookJsonPath(string iDataRoot, string iBookSlug)
            => Path.Combine(SCP_BookStore.BookNotesRoot(iDataRoot), iBookSlug, "book.json");

        public static SCP_WorkProseState ProbeWorkProse(string? iDir, out int oFileCount)
        {
            oFileCount = 0;
            if (string.IsNullOrEmpty(iDir) || !Directory.Exists(iDir)) return SCP_WorkProseState.NoDir;
            try { oFileCount = Directory.GetFiles(iDir!).Length; }
            catch (Exception) { return SCP_WorkProseState.NoDir; }
            return oFileCount > 0 ? SCP_WorkProseState.HasFiles : SCP_WorkProseState.EmptyDir;
        }

        /// <summary>把寫書線四欄寫上**既有的** work.json（只寫非空的那幾欄）。work.json 不存在 ⇒ 不建、回 false。</summary>
        /// <remarks>⛔ 刻意不順手建一份：這裡若建檔，「作品本來就在」與「我剛剛替它生了一份」會同形。</remarks>
        public static bool TrySetWorkAuthored(string iDataRoot, string iWorkId, SCP_WorkAuthored? iFields,
                                              out string? oError)
        {
            oError = null;
            if (iFields == null) { oError = "fields 是 null"; return false; }
            string aPath = SCP_LibraryStore.WorkJsonPath(iDataRoot, iWorkId);
            if (!File.Exists(aPath))
            {
                oError = $"work.json 不存在：`{aPath}` —— ⛔ 本函式不建檔（那是 media_init 的職責）";
                return false;
            }
            SCP_JsonData? aWork = SCP_LibraryIO.LoadJson(aPath, out string? aLoadErr);
            if (aWork == null) { oError = aLoadErr; return false; }
            if (iFields.AuthorPersona.Length > 0) aWork[Key_AuthorPersona] = iFields.AuthorPersona;
            if (iFields.Status.Length > 0) aWork[SCP_LibraryIO.Key_Status] = iFields.Status;
            if (iFields.PublishStatus.Length > 0) aWork[Key_PublishStatus] = iFields.PublishStatus;
            if (iFields.Origin.Length > 0) aWork[Key_Origin] = iFields.Origin;
            SCP_LibraryIO.SaveJson(aPath, aWork);
            return true;
        }

        /// <summary>讀回寫書線四欄。work.json 不存在或解析不動 ⇒ 回 false，⛔ 不回一個空物件假裝讀到了。</summary>
        public static bool TryReadWorkAuthored(string iDataRoot, string iWorkId,
                                               out SCP_WorkAuthored? oFields, out string? oError)
        {
            oFields = null; oError = null;
            string aPath = SCP_LibraryStore.WorkJsonPath(iDataRoot, iWorkId);
            if (!File.Exists(aPath)) { oError = $"work.json 不存在：`{aPath}`"; return false; }
            SCP_JsonData? aWork = SCP_LibraryIO.LoadJson(aPath, out string? aLoadErr);
            if (aWork == null) { oError = aLoadErr; return false; }
            oFields = new SCP_WorkAuthored
            {
                AuthorPersona = aWork.GetString(Key_AuthorPersona, ""),
                Status = aWork.GetString(SCP_LibraryIO.Key_Status, ""),
                PublishStatus = aWork.GetString(Key_PublishStatus, ""),
                Origin = aWork.GetString(Key_Origin, ""),
            };
            return true;
        }

        // ===========================================================
        // ③ 逐欄對拍 —— 回傳的不是 bool，是「對不上的欄位名」。
        // 🩸 一個永遠回「全對」的對拍與一個真的全對的對拍，在 exit code 上逐位元組同形。
        // ===========================================================
        public static SCP_AuthoredDiffOutcome DiffWorkAuthored(string iDataRoot, string iBookSlug, string iWorkId,
            out List<string> oMismatchedFields, out string oReport)
        {
            oMismatchedFields = new List<string>();
            var aSb = new StringBuilder();

            string aOldPath = OldStoreBookJsonPath(iDataRoot, iBookSlug);
            string aNewPath = SCP_LibraryStore.WorkJsonPath(iDataRoot, iWorkId);
            aSb.AppendLine($"- 舊 store：`{aOldPath}`");
            aSb.AppendLine($"- 新 store：`{aNewPath}`");
            aSb.AppendLine();

            if (!File.Exists(aOldPath))
            {
                aSb.AppendLine("⛔ **舊 store 那本不存在** ⇒ 沒有比較的左邊。" +
                               "⛔ 這不是「值不同」，是 `book` 給錯或那本不在舊 store。");
                oReport = aSb.ToString();
                return SCP_AuthoredDiffOutcome.OldStoreMissing;
            }
            if (!File.Exists(aNewPath))
            {
                aSb.AppendLine("⛔ **新 store 還沒有這份 work.json** ⇒ 這本還沒建（④ 的前置）。⛔ 這不是「值不同」。");
                oReport = aSb.ToString();
                return SCP_AuthoredDiffOutcome.NewStoreMissing;
            }

            SCP_JsonData? aOldData = SCP_LibraryIO.LoadJson(aOldPath, out string? aOldErr);
            if (aOldData == null)
            {
                aSb.AppendLine($"⛔ 舊 store 解析失敗：{aOldErr}　⇒ ⛔ 不回報「不同」—— 壞檔與搬壞了是兩件事。");
                oReport = aSb.ToString();
                return SCP_AuthoredDiffOutcome.ParseFailed;
            }
            if (!TryReadWorkAuthored(iDataRoot, iWorkId, out SCP_WorkAuthored? aNewFields, out string? aNewErr)
                || aNewFields == null)
            {
                aSb.AppendLine($"⛔ 新 store 讀取失敗：{aNewErr}　⇒ ⛔ 不回報「不同」。");
                oReport = aSb.ToString();
                return SCP_AuthoredDiffOutcome.ParseFailed;
            }

            var aOldFields = new SCP_WorkAuthored
            {
                AuthorPersona = aOldData.GetString(Key_AuthorPersona, ""),
                Status = aOldData.GetString(SCP_LibraryIO.Key_Status, ""),
                PublishStatus = aOldData.GetString(Key_PublishStatus, ""),
                Origin = aOldData.GetString(Key_Origin, ""),
            };

            // 逐欄表 —— 順序固定，`origin` 放第一列：它是寫書線可見性的承重欄。
            var aKeys = new[] { Key_Origin, Key_AuthorPersona, SCP_LibraryIO.Key_Status, Key_PublishStatus };
            var aOldVals = new[] { aOldFields.Origin, aOldFields.AuthorPersona, aOldFields.Status, aOldFields.PublishStatus };
            var aNewVals = new[] { aNewFields.Origin, aNewFields.AuthorPersona, aNewFields.Status, aNewFields.PublishStatus };

            aSb.AppendLine("| 欄位 | 舊 store | 新 store | 判定 |");
            aSb.AppendLine("|---|---|---|---|");
            for (int i = 0; i < aKeys.Length; i++)
            {
                bool aSame = aOldVals[i] == aNewVals[i];
                if (!aSame) oMismatchedFields.Add(aKeys[i]);
                aSb.AppendLine($"| `{aKeys[i]}` | {ShowFieldValue(aOldVals[i])} | {ShowFieldValue(aNewVals[i])} | " +
                               (aSame ? "✓ 相同" : "**✗ 對不上**") + " |");
            }
            aSb.AppendLine();
            aSb.Append(ProseSection(iDataRoot, iBookSlug, iWorkId, out bool aProseMissing));

            // ⭐ 空對空不准算全對。
            if (aOldFields.IsEmpty && aNewFields.IsEmpty)
            {
                aSb.AppendLine();
                aSb.AppendLine("⚠ **兩邊都沒有寫書線四欄** ⇒ 這本不是 authored。⛔ 這不是「對拍通過」，是無事可拍。");
                oReport = aSb.ToString();
                return SCP_AuthoredDiffOutcome.NeitherHasWritingLine;
            }
            if (aNewFields.IsEmpty)
            {
                aSb.AppendLine();
                aSb.AppendLine("⛔ **新 store 那份 work.json 上四欄全空 ⇒ 這本還沒搬。**");
                aSb.AppendLine("　⚠ 上表把四個空字串逐欄比出「對不上」是對的讀數，但**處置不同**：" +
                               "這裡要跑的是 ④ 搬遷（而 ④ 的前置是本人的搬遷確認），不是去修欄位值。");
                oReport = aSb.ToString();
                return SCP_AuthoredDiffOutcome.NewStoreNoWritingLine;
            }
            if (oMismatchedFields.Count > 0)
            {
                aSb.AppendLine();
                aSb.AppendLine($"⛔ **對不上的欄位（{oMismatchedFields.Count} 欄）：** " +
                               string.Join("、", oMismatchedFields.ConvertAll(k => "`" + k + "`")));
                oReport = aSb.ToString();
                return SCP_AuthoredDiffOutcome.Mismatch;
            }

            aSb.AppendLine();
            aSb.AppendLine("✓ **四欄逐欄相同**，且新 store 真的有寫書線" +
                           (aNewFields.VisibleToWritingLine
                               ? "（`origin=authored` ⇒ 對寫書線讀取端可見）。"
                               : "　⚠ 但 `origin` 不是 `authored` ⇒ **對寫書線讀取端仍然不可見**。"));

            // ⭐ 欄位全對**不足以**產生一個 ✓ —— 正文容器對不上時，搬下去會靜默丟掉正文。
            if (aProseMissing)
            {
                aSb.AppendLine();
                aSb.AppendLine("⛔ **但正文容器對不上**（上表已逐列指出）⇒ **這一趟還不能搬**：" +
                               "欄位對得起來只證明 metadata，正文是另一本帳。");
                oReport = aSb.ToString();
                return SCP_AuthoredDiffOutcome.FieldsMatchProseMissing;
            }
            oReport = aSb.ToString();
            return SCP_AuthoredDiffOutcome.AllMatch;
        }

        // ===========================================================
        // ④ 把一本 authored 書從舊 store 搬進新 store（四欄 ＋ 正文容器）。
        // 物理意義：**複製，不是移動** —— 舊 store 那份原地保留。
        //   🩸 移動的話舊側會從 `HasFiles` 變 `EmptyDir`，而**那個讀數與「搬壞了」同形**。
        // ⛔ 只搬 `origin=authored` 的書；不是就出聲拒絕，⛔ 不靜默跳過、也不替它補上 origin。
        // ===========================================================
        public static SCP_AuthoredMigrateOutcome MigrateAuthoredWork(string iDataRoot,
            string iBookSlug, string iWorkId, bool iConfirm, out string oReport, out string? oError)
        {
            oError = null;
            var aSb = new StringBuilder();
            string aOldRoot = Path.Combine(SCP_BookStore.BookNotesRoot(iDataRoot), iBookSlug);
            string aOldPath = OldStoreBookJsonPath(iDataRoot, iBookSlug);
            aSb.AppendLine($"- 舊 store：`{aOldPath}`");
            aSb.AppendLine($"- 新 store：`{SCP_LibraryStore.WorkJsonPath(iDataRoot, iWorkId)}`");
            aSb.AppendLine($"- 模式：{(iConfirm ? "**confirm ⇒ 真的寫**" : "**dry-run ⇒ 零寫入**（要寫就加 `confirm=1`）")}");
            aSb.AppendLine();

            if (!File.Exists(aOldPath))
            {
                oError = $"舊 store 沒有這本：`{aOldPath}`";
                oReport = aSb.ToString();
                return SCP_AuthoredMigrateOutcome.OldStoreMissing;
            }
            SCP_JsonData? aOldData = SCP_LibraryIO.LoadJson(aOldPath, out string? aLoadErr);
            if (aOldData == null)
            {
                oError = $"舊 store 解析失敗：{aLoadErr} —— ⛔ 這不是「沒有內容」";
                oReport = aSb.ToString();
                return SCP_AuthoredMigrateOutcome.ParseFailed;
            }

            var aFields = new SCP_WorkAuthored
            {
                AuthorPersona = aOldData.GetString(Key_AuthorPersona, ""),
                Status = aOldData.GetString(SCP_LibraryIO.Key_Status, ""),
                PublishStatus = aOldData.GetString(Key_PublishStatus, ""),
                Origin = aOldData.GetString(Key_Origin, ""),
            };
            if (!aFields.VisibleToWritingLine)
            {
                oError = $"舊 store 那本的 `{Key_Origin}` 是 `{aFields.Origin}`，不是 `{OriginAuthored}` ⇒ " +
                         "這支只搬寫書線的書。⛔ 不靜默跳過，也不替它補上 origin。";
                oReport = aSb.ToString();
                return SCP_AuthoredMigrateOutcome.NotAuthored;
            }

            aSb.AppendLine("## 要搬的四欄（照搬，⛔ 不轉換）");
            aSb.AppendLine();
            aSb.AppendLine("| 欄 | 值 |");
            aSb.AppendLine("|---|---|");
            aSb.AppendLine($"| `{Key_Origin}` | `{aFields.Origin}` |");
            aSb.AppendLine($"| `{Key_AuthorPersona}` | `{aFields.AuthorPersona}` |");
            aSb.AppendLine($"| `{SCP_LibraryIO.Key_Status}` | `{aFields.Status}` |");
            aSb.AppendLine($"| `{Key_PublishStatus}` | `{aFields.PublishStatus}` |");
            aSb.AppendLine();

            aSb.AppendLine("## 正文容器（三態，⛔ 不是檔數）");
            aSb.AppendLine();
            aSb.AppendLine(ProseSection(iDataRoot, iBookSlug, iWorkId, out _));

            if (!iConfirm)
            {
                aSb.AppendLine("⇒ **dry-run 到此為止** —— 上面每一格都是讀出來的，沒有任何寫入。");
                oReport = aSb.ToString();
                return SCP_AuthoredMigrateOutcome.Planned;
            }

            // --- 這行以下才會動磁碟 ---
            aSb.AppendLine("## 寫入");
            aSb.AppendLine();
            var aAliases = new List<string>();
            SCP_JsonData? aOldAliases = aOldData.Contains(SCP_LibraryIO.Key_Aliases)
                ? aOldData[SCP_LibraryIO.Key_Aliases] : null;
            if (aOldAliases != null && aOldAliases.IsArray)
                for (int i = 0; i < aOldAliases.Count; i++)
                {
                    string a = SCP_LibraryRecall.AliasToString(aOldAliases[i]);
                    if (!string.IsNullOrEmpty(a) && !aAliases.Contains(a)) aAliases.Add(a);
                }
            aSb.Append(SCP_LibraryInit.EnsureWorkJson(iDataRoot, iWorkId,
                aOldData.GetString(SCP_LibraryIO.Key_Title, iBookSlug),
                aOldData.GetString(SCP_LibraryIO.Key_TitleOriginal, ""),
                aOldData.GetString(SCP_LibraryIO.Key_Author, ""),
                aAliases, null));

            if (!TrySetWorkAuthored(iDataRoot, iWorkId, aFields, out string? aSetErr))
            {
                oError = $"四欄寫入失敗：{aSetErr}";
                oReport = aSb.ToString();
                return SCP_AuthoredMigrateOutcome.ParseFailed;
            }
            aSb.AppendLine("- ✅ 四欄已寫上 work.json（空值不落盤）");

            aSb.Append(CopyProseDir(Path.Combine(aOldRoot, WorkChaptersDirName),
                                    WorkChaptersRoot(iDataRoot, iWorkId), WorkChaptersDirName));
            aSb.Append(CopyProseDir(Path.Combine(aOldRoot, WorkArcsDirName),
                                    WorkArcsRoot(iDataRoot, iWorkId), WorkArcsDirName));

            // 回讀：⛔ 不印「寫入成功」當收據 —— 用同一支對拍器（③）重讀一次落地結果。
            aSb.AppendLine();
            aSb.AppendLine("## 回讀對拍（走 ③ 那支 `DiffWorkAuthored`，⛔ 不是本函式自己說了算）");
            aSb.AppendLine();
            SCP_AuthoredDiffOutcome aAfter = DiffWorkAuthored(iDataRoot, iBookSlug, iWorkId,
                out List<string> aMism, out string aDiffReport);
            aSb.AppendLine($"**{aAfter}**" + (aMism.Count > 0 ? $"　對不上：{string.Join("、", aMism)}" : ""));
            aSb.AppendLine();
            aSb.Append(aDiffReport);

            oReport = aSb.ToString();
            return SCP_AuthoredMigrateOutcome.Migrated;
        }

        /// <summary>空字串要看得出是空的 —— ⛔ 不印成空白格（空白格與「我沒讀那一欄」同形）。</summary>
        static string ShowFieldValue(string? iValue) => string.IsNullOrEmpty(iValue) ? "_(空)_" : "`" + iValue + "`";

        static string ProseSection(string iDataRoot, string iBookSlug, string iWorkId, out bool oAnyMissing)
        {
            string aOldRoot = Path.Combine(SCP_BookStore.BookNotesRoot(iDataRoot), iBookSlug);
            var aSb = new StringBuilder();
            aSb.AppendLine("| 正文容器 | 舊 store | 新 store | 判定 |");
            aSb.AppendLine("|---|---|---|---|");
            aSb.Append(ProseRow(WorkChaptersDirName, Path.Combine(aOldRoot, WorkChaptersDirName),
                                WorkChaptersRoot(iDataRoot, iWorkId), out bool aMissA));
            aSb.Append(ProseRow(WorkArcsDirName, Path.Combine(aOldRoot, WorkArcsDirName),
                                WorkArcsRoot(iDataRoot, iWorkId), out bool aMissB));
            oAnyMissing = aMissA || aMissB;
            return aSb.ToString();
        }

        /// <summary>複製一個正文容器（⛔ 不移動、⛔ 不覆寫既有同名檔）。</summary>
        /// <remarks>⚠ 來源目錄不存在 ⇒ 回一行「NoDir，無事可搬」而**不建空目錄** ——
        /// 建了的話新側會從 `NoDir` 變成 `EmptyDir`，而那正是三態要分開的兩個值。</remarks>
        static string CopyProseDir(string iSrcDir, string iDstDir, string iLabel)
        {
            SCP_WorkProseState aSrc = ProbeWorkProse(iSrcDir, out int aSrcCount);
            if (aSrc == SCP_WorkProseState.NoDir) return $"- `{iLabel}/`：舊 store `NoDir` ⇒ 無事可搬（⛔ 不建空目錄）\n";
            if (aSrc == SCP_WorkProseState.EmptyDir) return $"- `{iLabel}/`：舊 store `EmptyDir`（0 檔）⇒ 無事可搬\n";

            Directory.CreateDirectory(iDstDir);
            int aCopied = 0, aSkipped = 0;
            foreach (string f in Directory.GetFiles(iSrcDir))
            {
                string aDst = Path.Combine(iDstDir, Path.GetFileName(f));
                if (File.Exists(aDst)) { aSkipped++; continue; }
                File.Copy(f, aDst);
                aCopied++;
            }
            string aLine = $"- `{iLabel}/`：來源 {aSrcCount} 檔 ⇒ 複製 {aCopied}";
            if (aSkipped > 0) aLine += $"、**跳過 {aSkipped}（目標已有同名檔，⛔ 不覆寫）**";
            return aLine + "\n";
        }

        /// <param name="oMissing">只在「**舊 store 有檔而新 store 沒有**」時為 true ——
        /// 那是唯一會靜默丟內容的那一種。⛔ 舊 store 是空目錄或沒有容器時**不算遺漏**。</param>
        static string ProseRow(string iName, string iOldDir, string iNewDir, out bool oMissing)
        {
            SCP_WorkProseState o = ProbeWorkProse(iOldDir, out int aOldCount);
            SCP_WorkProseState n = ProbeWorkProse(iNewDir, out int aNewCount);
            oMissing = o == SCP_WorkProseState.HasFiles && n != SCP_WorkProseState.HasFiles;
            string aVerdict;
            if (oMissing)
                aVerdict = $"⛔ **舊 store 有 {aOldCount} 個檔而新 store 沒有** ⇒ 搬過去會靜默丟掉它";
            else if (o == SCP_WorkProseState.HasFiles)
                aVerdict = aOldCount == aNewCount ? $"✓ 兩邊都 {aOldCount} 個檔" : $"⚠ 檔數不同（{aOldCount} → {aNewCount}）";
            else if (o == SCP_WorkProseState.EmptyDir)
                aVerdict = "⚠ 舊 store 是**空目錄** ⇒ ⛔ 不是「沒有這個容器」，也不是「有內容」";
            else
                aVerdict = "· 舊 store 沒有這個容器 ⇒ 無事可搬";
            return $"| `{iName}/` | {ShowProseState(o, aOldCount)} | {ShowProseState(n, aNewCount)} | {aVerdict} |\n";
        }

        static string ShowProseState(SCP_WorkProseState iState, int iCount)
        {
            switch (iState)
            {
                case SCP_WorkProseState.NoDir: return "`NoDir`（沒有這個目錄）";
                case SCP_WorkProseState.EmptyDir: return "`EmptyDir`（目錄在、0 個檔）";
                default: return $"`HasFiles`（{iCount} 個檔）";
            }
        }
    }
}
