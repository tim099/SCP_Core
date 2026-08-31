// 區塊職責：任務單的**讀取層** —— 掃 `Tasks/tasks/*.md`、解析 frontmatter 與留言、給查詢。
// 物理意義：磁碟是既成事實。本檔**只讀不寫** —— 寫入端（`Save` / 配號 / 酒館公告）
//           留在 Unity Editor 那側，理由不是懶：
//           🩸 配號（`_index.txt`）是**沒有跨 process lock** 的 read-modify-write，
//              而 UCL 那支自己的 self-heal 訊息就在描述「有人繞過 Cmd 建單」。
//              兩個 process 同時配號 ⇒ 拿到同號 ⇒ 第二個 Save 覆蓋第一個，**靜默**。
//              （同族：ChatTavern 的 `_seq.txt`，它的檔頭也寫著「prototype 階段不做跨 process lock」。）
//           ⇒ 判準：**分配單調 id 或持有鎖的寫入，只能有一個寫者。** 整格搬或整格不搬。
// 數值影響：純讀。無 `tasks/` 目錄回空清單（那是「還沒有人開單」的誠實讀數，不是錯誤）。
//
// ⚠ **語意與 UCL 端逐條對齊**（`UCL_TaskIO.LoadFile`）—— 本檔是那支的移植，不是重寫：
//   · frontmatter 邊界＝第一個與第二個 `---`
//   · `participants` 是巢狀清單 ⇒ 手寫極簡 YAML 子集，只認 `- persona:` / `role:` / `assigned_at:`
//   · 認不得的鍵**跳過但不吞掉整張單** —— 一個雜鍵不該讓一張單消失
//   · `index <= 0` ⇒ 這不是一張單（回 null）
//   · 壞 enum 落回預設並**出聲**（`iWarn`）
//
// ⚠ 讀檔一律走 **ReadAllLines**（不是自己 split "\n"）——
//   🩸 2026-08-31 我第一版的對帳腳本自己 split，於是 96 張裡 **13 張 CRLF** 的值尾巴留著 `\r`
//   （`'todo\r'`），對拍出 13 個假不符。**壞的是我的尺，不是被量的碼。**
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Paths;

namespace SCP.Core.Tasks
{
    public static class SCP_TaskIO
    {
        public const string TasksDirName = "Tasks";
        public const string IndexFileName = "_index.txt";

        public static string Dir(SCP_DataRoot iRoot) => iRoot.Value + "/" + TasksDirName;
        public static string TasksDir(SCP_DataRoot iRoot) => Dir(iRoot) + "/tasks";
        public static string IndexPath(SCP_DataRoot iRoot) => Dir(iRoot) + "/" + IndexFileName;

        /// <summary>單檔路徑。⚠ 檔名補零**只為排序好看**，權威是 frontmatter 的整數 index。</summary>
        public static string TaskPath(SCP_DataRoot iRoot, int iIndex)
            => TasksDir(iRoot) + "/" + iIndex.ToString("0000", CultureInfo.InvariantCulture) + ".md";

        // ── 留言表示法（與 UCL 端同一條 regex）─────────────────────
        static readonly Regex COMMENT_HEAD = new Regex(
            @"^###\s+💬\s+#(?<id>\d+)\s+(?<persona>\S+)\s+(?<at>\S+)\s*$", RegexOptions.Compiled);

        /// <summary>單檔的頂層區塊標題 —— 留言區的邊界靠它判。</summary>
        static readonly string[] SECTION_HEADINGS =
        {
            "## 驗收標準", "## 任務描述", "## 結單說明", "## 留言", "## 活動與討論時間線",
        };

        // ===========================================================
        // 區塊職責：全掃。
        // 數值影響：一次目錄列舉 ＋ 每張單一次讀檔。96 張是 2026-08-31 的讀數 ——
        //          Editor 端本來就在做全掃，所以搬過來不變差；但**多了一個每次早安都全掃的呼叫者**。
        // ===========================================================
        public static List<SCP_TaskEntry> LoadAll(SCP_DataRoot iRoot, Action<string>? iWarn = null)
        {
            var aList = new List<SCP_TaskEntry>();
            string aDir = TasksDir(iRoot);
            if (!Directory.Exists(aDir)) return aList;
            foreach (string aPath in Directory.GetFiles(aDir, "*.md"))
            {
                SCP_TaskEntry? aEntry = LoadFile(aPath, iWarn);
                if (aEntry != null) aList.Add(aEntry);
            }
            aList.Sort((a, b) => a.index.CompareTo(b.index));
            return aList;
        }

        public static SCP_TaskEntry? Find(SCP_DataRoot iRoot, int iIndex, Action<string>? iWarn = null)
        {
            string aPath = TaskPath(iRoot, iIndex);
            return File.Exists(aPath) ? LoadFile(aPath, iWarn) : null;
        }

        // ===========================================================
        // 區塊職責：解析一張單。
        // ⚠ 例外一律吞成「跳過這張單」＋出聲 —— 一張壞檔不該讓整個清單消失，
        //   但**安靜地少一張**跟「本來就沒有那張」長得一樣，所以必須出聲。
        // ===========================================================
        public static SCP_TaskEntry? LoadFile(string iPath, Action<string>? iWarn = null)
        {
            try
            {
                var e = new SCP_TaskEntry();
                bool aIn = false;
                SCP_TaskParticipant? aCur = null;
                foreach (string aLine in File.ReadAllLines(iPath, Encoding.UTF8))
                {
                    if (aLine.StartsWith("---", StringComparison.Ordinal))
                    {
                        if (!aIn) { aIn = true; continue; }
                        break;
                    }
                    if (!aIn) continue;

                    string aTrim = aLine.TrimStart();
                    if (aTrim.StartsWith("- persona:", StringComparison.Ordinal))
                    {
                        aCur = new SCP_TaskParticipant { persona = After(aTrim, "- persona:") };
                        if (aCur.persona.Length > 0) e.participants.Add(aCur);
                        continue;
                    }
                    if (aCur != null && aTrim.StartsWith("role:", StringComparison.Ordinal))
                    {
                        aCur.role = SCP_TaskWire.ParseOr(After(aTrim, "role:"), SCP_TaskRole.dev,
                            iPath + " participants.role", iWarn);
                        continue;
                    }
                    if (aCur != null && aTrim.StartsWith("assigned_at:", StringComparison.Ordinal))
                    { aCur.assigned_at = After(aTrim, "assigned_at:"); continue; }

                    // 其餘縮排行不是頂層鍵 —— 跳過（不是錯誤，是巢狀結構的其他行）
                    if (aLine.StartsWith(" ", StringComparison.Ordinal)) continue;
                    int c = aLine.IndexOf(':');
                    if (c <= 0) continue;
                    string k = aLine.Substring(0, c).Trim();
                    string v = aLine.Substring(c + 1).Trim();
                    switch (k)
                    {
                        case "index": int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out e.index); break;
                        case "type":
                            e.type = SCP_TaskWire.ParseOr(v, SCP_TaskType.feature, iPath + " type", iWarn);
                            // `all` 是篩選成員不是任務種類 —— 落盤檔帶著它＝壞檔
                            if (e.type == SCP_TaskType.all)
                            {
                                iWarn?.Invoke($"[Task] {iPath} type: `all` 是篩選成員不是任務種類"
                                    + " —— 落回 `feature`，去修單檔 frontmatter");
                                e.type = SCP_TaskType.feature;
                            }
                            break;
                        case "priority": e.priority = SCP_TaskWire.ParseOr(v, SCP_TaskPriority.normal, iPath + " priority", iWarn); break;
                        case "severity": e.severity = SCP_TaskWire.ParseOr(v, SCP_TaskSeverity.none, iPath + " severity", iWarn); break;
                        case "status":
                            e.status = SCP_TaskWire.ParseOr(v, SCP_TaskStatus.todo, iPath + " status", iWarn);
                            if (e.status == SCP_TaskStatus.all || e.status == SCP_TaskStatus.open)
                            {
                                iWarn?.Invoke($"[Task] {iPath} status: `{v}` 是篩選成員不是狀態"
                                    + " —— 落回 `todo`，去修單檔 frontmatter");
                                e.status = SCP_TaskStatus.todo;
                            }
                            break;
                        case "title": e.title = v; break;
                        case "reporter": e.reporter = v; break;
                        case "milestone": e.milestone = v; break;
                        case "epic_id": e.epic_id = v; break;
                        case "blocked_by": e.blocked_by = ParseIntList(v); break;
                        case "blocks": e.blocks = ParseIntList(v); break;
                        case "related_to": e.related_to = ParseIntList(v); break;
                        case "subtask_indices": e.subtask_indices = ParseIntList(v); break;
                        case "tags": e.tags = ParseStrList(v); break;
                        case "commit_shas": e.commit_shas = ParseStrList(v); break;
                        case "created_at": e.created_at = v; break;
                        case "updated_at": e.updated_at = v; break;
                        case "last_wrapup_at": e.last_wrapup_at = v; break;
                        case "closed_at": e.closed_at = v; break;
                        case "memory_topic": e.memory_topic = v; break;
                        case "memory_archived_commit": e.memory_archived_commit = v; break;
                        case "participants": aCur = null; break;
                    }
                }
                if (e.index <= 0) return null;
                e.comments = ReadComments(iPath);
                return e;
            }
            catch (Exception ex)
            {
                iWarn?.Invoke($"[Task] 讀取失敗，跳過：{iPath}（{ex.Message}）");
                return null;
            }
        }

        // ===========================================================
        // 區塊職責：把 `## 留言` 區塊解析成留言清單。
        // ⚠ 認不出標頭的行**歸給前一則的內文**而不是丟掉 ——
        //   丟掉會讓「有人手改壞了格式」與「他沒寫過那句話」長得一樣。
        // ===========================================================
        public static List<SCP_TaskComment> ReadComments(string iPath)
        {
            var aOut = new List<SCP_TaskComment>();
            try
            {
                if (!File.Exists(iPath)) return aOut;
                bool aIn = false;
                SCP_TaskComment? aCur = null;
                var aBody = new StringBuilder();
                foreach (string aLine in File.ReadAllLines(iPath, Encoding.UTF8))
                {
                    if (aLine.StartsWith("## 留言", StringComparison.Ordinal)) { aIn = true; continue; }
                    if (!aIn) continue;
                    if (IsSectionHeading(aLine)) break;

                    Match aM = COMMENT_HEAD.Match(aLine);
                    if (aM.Success)
                    {
                        Flush(aOut, ref aCur, aBody);
                        aCur = new SCP_TaskComment
                        {
                            persona = aM.Groups["persona"].Value,
                            at = aM.Groups["at"].Value,
                        };
                        int.TryParse(aM.Groups["id"].Value, NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out aCur.id);
                        continue;
                    }
                    if (aCur != null) aBody.AppendLine(aLine);
                }
                Flush(aOut, ref aCur, aBody);
            }
            catch (Exception)
            {
                // 留言讀不出來不該讓整張單消失 —— 回目前收到的（可能是空的）。
            }
            return aOut;
        }

        static void Flush(List<SCP_TaskComment> ioList, ref SCP_TaskComment? ioCur, StringBuilder ioBody)
        {
            if (ioCur != null)
            {
                ioCur.body = ioBody.ToString().Trim();
                ioList.Add(ioCur);
            }
            ioCur = null;
            ioBody.Length = 0;
        }

        static bool IsSectionHeading(string iLine)
        {
            foreach (string aH in SECTION_HEADINGS)
                if (iLine.StartsWith(aH, StringComparison.Ordinal)) return true;
            return false;
        }

        // ── 純讀的計數（配號本身留在 Editor）──────────────────────

        /// <summary>計數檔現值。⚠ **只讀** —— 遞增留在 Editor 那側的單一寫者。</summary>
        public static int ReadCurrentIndex(SCP_DataRoot iRoot)
        {
            string aPath = IndexPath(iRoot);
            if (!File.Exists(aPath)) return 0;
            return int.TryParse(File.ReadAllText(aPath).Trim(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        /// <summary>磁碟上最大的 index。<para>⚠ 它 &gt; 計數檔 ＝ 有人繞過 Cmd 手建單。</para></summary>
        public static int ReadMaxIndexOnDisk(SCP_DataRoot iRoot, Action<string>? iWarn = null)
        {
            int aMax = 0;
            foreach (SCP_TaskEntry aEntry in LoadAll(iRoot, iWarn))
                if (aEntry.index > aMax) aMax = aEntry.index;
            return aMax;
        }

        // ── 查詢 ──────────────────────────────────────────────────

        /// <summary>本單還開著的阻塞來源（`blocked_by` 指到的單裡沒關的那些）。</summary>
        public static List<string> OpenBlockers(SCP_DataRoot iRoot, SCP_TaskEntry iEntry,
                                                Action<string>? iWarn = null)
        {
            var aOut = new List<string>();
            foreach (int aIndex in iEntry.blocked_by)
            {
                SCP_TaskEntry? aOther = Find(iRoot, aIndex, iWarn);
                // ⚠ 指到不存在的單也算「還開著」—— 一條指向虛空的依賴不是「已解除」，
                //   而把它當已解除會讓一張其實沒人在看的單直接放行。
                if (aOther == null) { aOut.Add("TASK-" + aIndex.ToString("0000", CultureInfo.InvariantCulture) + "（單不存在）"); continue; }
                if (!aOther.IsClosed()) aOut.Add(aOther.Id + "（" + aOther.status + "）");
            }
            return aOut;
        }

        static string After(string iLine, string iPrefix) => iLine.Substring(iPrefix.Length).Trim();

        static List<int> ParseIntList(string iRaw)
        {
            var aOut = new List<int>();
            string s = (iRaw ?? "").Trim().Trim('[', ']');
            if (s.Length == 0) return aOut;
            foreach (string aPart in s.Split(','))
                if (int.TryParse(aPart.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    aOut.Add(v);
            return aOut;
        }

        static List<string> ParseStrList(string iRaw)
        {
            var aOut = new List<string>();
            string s = (iRaw ?? "").Trim().Trim('[', ']');
            if (s.Length == 0) return aOut;
            foreach (string aPart in s.Split(','))
            {
                string t = aPart.Trim();
                if (t.Length > 0) aOut.Add(t);
            }
            return aOut;
        }
    }
}
