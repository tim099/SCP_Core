// 區塊職責：自由時間活動清單的**唯一掃描器**（雙層 md）＋ frontmatter 單欄讀寫 ＋ 在專案層新增活動。
// 物理意義：共用層跟著 UCL_Core 走（`<UCL_Core>/Docs~/zh-Hant/FreeTime/Activities`）、專案層跟著 repo 走
//          （`<project_root>/docs/FreeTime/Activities`）；同 id 專案層覆蓋共用層。
//          Cmd 擲骰與後台頁**共用這一份**：兩份掃描器的漂移症狀是「頁面看到的清單跟實際擲出來的不一樣」，而它不會報錯。
// 數值影響：Scan 純讀；WriteField／CreateProjectActivity 各寫一個 md（原子換檔 ＋ 讀回確認）。
//
// ⚠ UCL_Core 根的解析：SCP_Core 裡**沒有**既有的 UCL_Core 根解析器（skill ucl-core-paths 列的三個都在 Editor／python 端）。
//   ⇒ 這裡照 Senate 唯一的先例（`Cmd_Task.WorkMemoryTool`）：讀 `<project_root>/.gitmodules`，取路徑以 `UCL_Core`
//     結尾的那一個 submodule；**不是剛好一個就不猜**（回 null ＋ 原因，呼叫端印出來）。⛔ 不寫死 `Assets/Plugins/UCL_Core`。
// ⚠ frontmatter 讀法刻意**不用** `SCP_LetterText.ReadFrontmatterField`：那支不要求開頭 `---`、只看前 1200 字元、
//   不剝引號 —— 跟 Unity 版（本檔 ReadField）語意不同，而活動 md 的 `how:` 常常很長且帶引號。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Io;

namespace SCP.Core.FreeTime
{
    public static class SCP_FreeTimeCatalog
    {
        /// <summary>共用層在 UCL_Core 根底下的相對位置。</summary>
        public const string SharedRelDir = "Docs~/zh-Hant/FreeTime/Activities";

        /// <summary>專案層在專案根底下的相對位置。</summary>
        public const string ProjectRelDir = "docs/FreeTime/Activities";

        // ── 路徑 ──────────────────────────────────────────────────

        /// <summary>
        /// UCL_Core submodule 的絕對路徑（從 <c>&lt;project_root&gt;/.gitmodules</c> 找路徑以 `UCL_Core` 結尾的那一個）。
        /// <para>不是剛好一個 ⇒ null ＋ <paramref name="oWhy"/>（⛔ 不猜 —— 猜錯的樣子是「掃到另一份 core 的活動」）。</para>
        /// </summary>
        public static string? UclCoreRoot(string iProjectRoot, out string oWhy)
        {
            oWhy = "";
            if (string.IsNullOrWhiteSpace(iProjectRoot)) { oWhy = "沒有專案根（project_root）"; return null; }
            string aModules = System.IO.Path.Combine(iProjectRoot, ".gitmodules");
            if (!File.Exists(aModules)) { oWhy = "專案根沒有 .gitmodules（" + iProjectRoot + "）"; return null; }
            var aHits = new List<string>();
            try
            {
                foreach (string aLine in File.ReadAllLines(aModules))
                {
                    string t = aLine.Trim();
                    if (!t.StartsWith("path", StringComparison.Ordinal)) continue;
                    int eq = t.IndexOf('=');
                    if (eq < 0) continue;
                    string p = t.Substring(eq + 1).Trim();
                    string n = p.Replace('\\', '/').TrimEnd('/');
                    if (n.EndsWith("/UCL_Core", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(n, "UCL_Core", StringComparison.OrdinalIgnoreCase))
                        aHits.Add(p);
                }
            }
            catch (Exception e) { oWhy = ".gitmodules 讀不了：" + e.Message; return null; }
            if (aHits.Count != 1) { oWhy = $".gitmodules 裡以 UCL_Core 結尾的 submodule 有 {aHits.Count} 個（⛔ 不猜）"; return null; }
            return System.IO.Path.GetFullPath(System.IO.Path.Combine(iProjectRoot, aHits[0])).Replace('\\', '/');
        }

        /// <summary>共用層目錄；解不出 UCL_Core 根 ⇒ null ＋ 原因。</summary>
        public static string? SharedDir(string iProjectRoot, out string oWhy)
        {
            string? aCore = UclCoreRoot(iProjectRoot, out oWhy);
            return aCore == null ? null : (aCore + "/" + SharedRelDir);
        }

        /// <summary>專案層目錄（不檢查存在 —— 專案層本來就可以還沒建）。</summary>
        public static string ProjectDir(string iProjectRoot)
            => System.IO.Path.GetFullPath(System.IO.Path.Combine(iProjectRoot, ProjectRelDir)).Replace('\\', '/');

        // ── 掃描 ──────────────────────────────────────────────────

        /// <summary>
        /// 掃兩層活動 md，回合併後的清單（**含停用項**，id 序）。
        /// <para>同 id 專案層覆蓋共用層 —— **含 enabled:false 停用覆蓋**（kotoko QA 血證：過濾必須發生在 merge 之後，
        /// 否則專案層的「停用」會被共用層的「啟用」蓋回去）。⇒ 回傳含停用項，由呼叫端決定要不要濾。</para>
        /// <para><paramref name="oWarnings"/>：共用層解不出／目錄不存在／單一 md 讀不了 —— 呼叫端**必須印出來**
        /// （兩層都空時「掃不到」與「沒有活動」同形）。</para>
        /// </summary>
        public static List<SCP_FreeTimeActivity> Scan(string iProjectRoot, List<string> oWarnings)
        {
            var aMerged = new Dictionary<string, SCP_FreeTimeActivity>(StringComparer.Ordinal);
            string? aShared = SharedDir(iProjectRoot, out string aWhy);
            if (aShared == null) oWarnings.Add("共用層活動目錄解不出來：" + aWhy + " ⇒ 只剩專案層");
            else if (!Directory.Exists(aShared)) oWarnings.Add("共用層活動目錄不存在：" + aShared);
            else ScanDir(aShared, false, aMerged, oWarnings);
            string aProject = ProjectDir(iProjectRoot);
            if (Directory.Exists(aProject)) ScanDir(aProject, true, aMerged, oWarnings);   // 專案層不存在是常態，不出聲
            var aList = new List<SCP_FreeTimeActivity>(aMerged.Values);
            aList.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return aList;
        }

        static void ScanDir(string iDir, bool iIsProject, Dictionary<string, SCP_FreeTimeActivity> ioMerged,
                            List<string> oWarnings)
        {
            string[] aFiles;
            try { aFiles = Directory.GetFiles(iDir, "*.md"); }
            catch (Exception e) { oWarnings.Add($"列不出活動目錄 {iDir}：{e.Message}"); return; }
            Array.Sort(aFiles, StringComparer.Ordinal);
            foreach (string aMdRaw in aFiles)
            {
                string aMd = aMdRaw.Replace('\\', '/');
                if (System.IO.Path.GetFileName(aMd).StartsWith("_", StringComparison.Ordinal)) continue;   // _README.md 等說明檔
                try
                {
                    string aStem = System.IO.Path.GetFileNameWithoutExtension(aMd);
                    string aId = Nz(ReadField(aMd, "id"), aStem);
                    int.TryParse(ReadField(aMd, "min_minutes"), out int aMin);
                    var aKind = ParseKind(ReadField(aMd, "kind"), out string aKindErr);
                    ioMerged[aId] = new SCP_FreeTimeActivity
                    {
                        Kind = aKind,
                        KindParseError = aKindErr,
                        Id = aId,
                        Name = Nz(ReadField(aMd, "name"), aStem),
                        How = ReadField(aMd, "how"),
                        Group = ReadField(aMd, "group").Trim(),
                        Path = aMd,
                        MinMinutes = aMin,
                        Enabled = !string.Equals(Nz(ReadField(aMd, "enabled"), "true"), "false", StringComparison.OrdinalIgnoreCase),
                        IsProjectLayer = iIsProject,
                        NeedsSession = !string.Equals(Nz(ReadField(aMd, "needs_session"), "true"), "false", StringComparison.OrdinalIgnoreCase),
                        Tool = ReadField(aMd, "tool").Trim(),
                        Steps = ParseList(ReadField(aMd, "steps")),
                        PersonaFlag = ReadField(aMd, "persona_flag").Trim(),
                        StepsNeedPersona = ParseList(ReadField(aMd, "steps_need_persona")),
                        CmdSteps = ParseList(ReadField(aMd, "cmd_steps")),
                        CmdPersonaArg = ReadField(aMd, "cmd_persona_arg").Trim(),
                        CmdStepArg = ReadField(aMd, "cmd_step_arg").Trim(),
                    };
                    if (aKindErr.Length > 0)
                        oWarnings.Add($"`{aId}` 的 kind='{aKindErr}' 認不得 —— 當 Default，並在骰面標記"
                                      + $"（可用值：{string.Join(" / ", Enum.GetNames(typeof(SCP_FreeTimeActivityKind)))}）");
                }
                catch (Exception e) { oWarnings.Add($"活動 md 讀取失敗，跳過：{aMd}（{e.Message}）"); }
            }
        }

        /// <summary>
        /// `kind` 字串 → enum。空＝Default。**認不得的值不是靜默 Default**：回填 oParseError 讓它在骰面上顯形。
        /// </summary>
        public static SCP_FreeTimeActivityKind ParseKind(string? iRaw, out string oParseError)
        {
            oParseError = "";
            string aVal = (iRaw ?? "").Trim();
            if (aVal.Length == 0) return SCP_FreeTimeActivityKind.Default;
            if (Enum.TryParse(aVal, true, out SCP_FreeTimeActivityKind aKind)
                && Enum.IsDefined(typeof(SCP_FreeTimeActivityKind), aKind))
                return aKind;
            oParseError = aVal;
            return SCP_FreeTimeActivityKind.Default;
        }

        // `steps: move, board, lobby` → 清單。白名單存在的理由是**不把任意子命令交給派遣層**；空 ⇒ 一律拒跑（fail-closed）。
        static List<string> ParseList(string iRaw)
        {
            var aList = new List<string>();
            if (string.IsNullOrWhiteSpace(iRaw)) return aList;
            foreach (string aPart in iRaw.Split(','))
            {
                string aTrim = aPart.Trim();
                if (aTrim.Length > 0) aList.Add(aTrim);
            }
            return aList;
        }

        static string Nz(string iVal, string iFallback) => string.IsNullOrEmpty(iVal) ? iFallback : iVal;

        // ── frontmatter 單欄讀寫 ──────────────────────────────────

        /// <summary>
        /// 讀 md frontmatter 單欄：
        /// 第一行必須是 `---`；最多看 100 行；遇到收尾 `---` 停；`欄位:` 前綴比對（區分大小寫、不吃縮排）；
        /// 值 Trim 後剝掉兩端的 `"`／`'`；找不到或讀不了 ⇒ 空字串。
        /// </summary>
        public static string ReadField(string iPath, string iField)
        {
            try
            {
                using (var aReader = new StreamReader(iPath, Encoding.UTF8))
                {
                    string? aLine = aReader.ReadLine();
                    if (aLine == null || aLine.Trim() != "---") return "";
                    string aPrefix = iField + ":";
                    for (int i = 0; i < 100; i++)
                    {
                        aLine = aReader.ReadLine();
                        if (aLine == null || aLine.Trim() == "---") return "";
                        if (aLine.StartsWith(aPrefix, StringComparison.Ordinal))
                            return aLine.Substring(aPrefix.Length).Trim().Trim('"', '\'');
                    }
                }
            }
            catch (Exception) { /* 讀不了 ＝ 空字串（Unity 版同側；呼叫端掃描時會因 id 回退檔名而照樣列出） */ }
            return "";
        }

        /// <summary>
        /// 值含 `:`／`#`／前後空白時加雙引號（內部 `"` 跳脫成 `\"`）—— 否則 YAML 讀回來會截斷或變成註解。
        /// </summary>
        public static string Quote(string? iVal)
        {
            string aRaw = iVal ?? "";
            bool aNeed = aRaw.Contains(":") || aRaw.Contains("#") || aRaw != aRaw.Trim();
            return aNeed ? "\"" + aRaw.Replace("\"", "\\\"") + "\"" : aRaw;
        }

        /// <summary>
        /// 寫 md frontmatter 單欄（<see cref="ReadField"/> 的對偶）。**正文一個字都不碰**。
        /// <para>欄位已存在 ⇒ 就地換值；不存在 ⇒ 插在收尾 `---` 之前。檔不存在／沒有起始或收尾 `---` ⇒ 回 false，
        /// **不代為新建**（替沒有 frontmatter 的檔硬生一段，等於替使用者決定那個檔是什麼）。</para>
        /// <para>原子換檔，寫完**讀回**那一欄比對 —— ⛔ 不拿「沒丟例外」當落盤的證據。</para>
        /// </summary>
        public static bool WriteField(string iMdPath, string iField, string iValue, out string? oError)
        {
            oError = null;
            try
            {
                if (string.IsNullOrWhiteSpace(iField) || iField.IndexOf(':') >= 0 || iField.IndexOf('\n') >= 0)
                { oError = $"欄位名不合法：'{iField}'"; return false; }
                if ((iValue ?? "").IndexOf('\n') >= 0 || (iValue ?? "").IndexOf('\r') >= 0)
                { oError = "值不可以含換行（frontmatter 單行欄位）"; return false; }
                if (!File.Exists(iMdPath)) { oError = "檔案不存在：" + iMdPath; return false; }
                var aLines = new List<string>(File.ReadAllLines(iMdPath, Encoding.UTF8));
                if (aLines.Count == 0 || aLines[0].Trim() != "---") { oError = "缺起始 ---：" + iMdPath; return false; }
                int aEnd = -1;
                for (int i = 1; i < aLines.Count; i++)
                    if (aLines[i].Trim() == "---") { aEnd = i; break; }
                if (aEnd < 0) { oError = "缺結束 ---：" + iMdPath; return false; }

                string aOut = Quote(iValue);
                string aNewLine = iField + ": " + aOut;
                int aFound = -1;
                string aPrefix = iField + ":";
                for (int i = 1; i < aEnd; i++)
                    if (aLines[i].StartsWith(aPrefix, StringComparison.Ordinal)) { aFound = i; break; }
                if (aFound >= 0) aLines[aFound] = aNewLine;
                else aLines.Insert(aEnd, aNewLine);

                string aTmp = iMdPath + ".tmp";
                File.WriteAllLines(aTmp, aLines, new UTF8Encoding(false));
                SCP_TextFile.ReplaceOrMove(aTmp, iMdPath);

                // 讀回：期望值 ＝ 讀取端會怎麼解這一行（Trim 後剝兩端引號）。
                string aExpected = aOut.Trim().Trim('"', '\'');
                string aBack = ReadField(iMdPath, iField);
                if (aBack != aExpected)
                {
                    oError = $"寫完讀回對不上：期望 `{aExpected}`、讀回 `{aBack}`"
                             + (aFound < 0 && aEnd > 100 ? "（frontmatter 超過 100 行，讀取端看不到新插入的那一行）" : "")
                             + "：" + iMdPath;
                    return false;
                }
                return true;
            }
            catch (Exception e) { oError = $"frontmatter 寫入失敗 {iMdPath}: {e.GetType().Name}: {e.Message}"; return false; }
        }

        // ── 新增活動（一律建在專案層）──────────────────────────────

        /// <summary>
        /// 在專案層新增一份活動 md。
        /// <para>為什麼一律建在專案層：共用層屬於 UCL_Core（跨專案），從專案的管理頁往那裡新增等於替別的專案做決定；
        /// 專案層同 id 會覆蓋共用層 —— 要改共用活動也走這裡。</para>
        /// <para>驗證：id 非空、不含非法檔名字元、不以 `_` 開頭（底線開頭的 md 被視為說明檔）、建議時間 ≥ 0、
        /// **已存在不覆寫**。正文只放一段待補 —— GUI 生得出欄位，生不出「這個活動是什麼」。</para>
        /// </summary>
        public static bool CreateProjectActivity(string iProjectRoot, string iId, string iName, string iHow, string iGroup,
            int iMinMinutes, out string oPath, out string? oError)
        {
            oPath = "";
            oError = null;
            string aId = (iId ?? "").Trim();
            if (aId.Length == 0) { oError = "id 不可空白"; return false; }
            if (aId.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0) { oError = "id 含非法檔名字元：" + aId; return false; }
            if (aId.StartsWith("_", StringComparison.Ordinal)) { oError = "id 不可以 _ 開頭（底線開頭的 md 被視為說明檔，不算活動）"; return false; }
            if (iMinMinutes < 0) { oError = $"建議時間需為 ≥0 的整數（got '{iMinMinutes}'）"; return false; }
            if (string.IsNullOrWhiteSpace(iProjectRoot)) { oError = "沒有專案根（project_root）"; return false; }

            string aDir = ProjectDir(iProjectRoot);
            string aPath = aDir + "/" + aId + ".md";
            oPath = aPath;
            if (File.Exists(aPath)) { oError = "已存在，未覆寫：" + aPath; return false; }
            try
            {
                Directory.CreateDirectory(aDir);
                string aName = string.IsNullOrWhiteSpace(iName) ? aId : iName.Trim();
                var aSb = new StringBuilder();
                aSb.AppendLine("---");
                aSb.AppendLine("id: " + aId);
                aSb.AppendLine("name: " + Quote(aName));
                aSb.AppendLine("how: " + Quote((iHow ?? "").Trim()));
                aSb.AppendLine("enabled: true");
                string aGroup = (iGroup ?? "").Trim();
                if (aGroup.Length > 0) aSb.AppendLine("group: " + Quote(aGroup));
                aSb.AppendLine("min_minutes: " + iMinMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                aSb.AppendLine("---");
                aSb.AppendLine();
                aSb.AppendLine("# " + aName);
                aSb.AppendLine();
                aSb.AppendLine("> ⚠ 正文待補 —— 這一段是給挑到這個活動的人看的「怎麼做」。");
                aSb.AppendLine("> GUI 生得出欄位，生不出「這個活動是什麼」。");
                // ⚠ 新建用「不存在才建」：檢查與建立之間有人搶先建了同名檔時，⛔ 不覆寫它。
                if (!SCP_AtomicFile.TryCreateNew(aPath, aSb.ToString())) { oError = "已存在，未覆寫：" + aPath; return false; }
            }
            catch (Exception e) { oError = $"建立失敗 {aPath}: {e.Message}"; return false; }

            if (ReadField(aPath, "id") != aId) { oError = "建立後讀回 id 對不上：" + aPath; return false; }
            return true;
        }
    }
}
