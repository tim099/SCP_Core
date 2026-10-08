// 區塊職責：工作記憶區 —— 以「工作主題」為單位的 knowhow 庫（所有 agent 共用）。
// 物理意義：資料落 `<資料根>/WorkMemory/<topic>/`：
//             `_topic.md`（主題卡）＋ `<type>_<slug>.md`（fragment，寫一次不改寫正文；更新走 status／links／supersede）
//             ＋ `_index.md`（機械生成，手改必被覆寫）。fragment 之間可跨主題關聯（links: [<topic>/<id>]）。
//           讀取時另寫一份共讀 briefing 到 `<資料根>/WorkMemoryReadBriefs/`（不在 WorkMemory 裡 ⇒ 不會被當成事實源）。
//           契約：本層**只讀** Task 側（`memory_topic` 由任務寫入端寫），主題卡的 `task_indices`／`status` 由本層寫。
// 數值影響：寫檔一律 UTF-8 無 BOM、CRLF —— 與既有資料（Windows 上 python 文字模式寫出的）逐位元組同形。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Git;

namespace SCP.Core.WorkMemory
{
    /// <summary>一份 fragment／主題卡：frontmatter（保序）＋正文＋路徑。值是 string 或 List&lt;string&gt;。</summary>
    public sealed class SCP_WorkMemoryDoc
    {
        public readonly List<KeyValuePair<string, object>> Meta = new List<KeyValuePair<string, object>>();
        public string Body = "";
        public string Path = "";

        public string Get(string iKey, string iFallback = "")
        {
            foreach (var kv in Meta) if (kv.Key == iKey) return kv.Value is List<string> l ? string.Join(", ", l) : (string)kv.Value;
            return iFallback;
        }
        public bool Has(string iKey) => Meta.Any(kv => kv.Key == iKey);
        public List<string> GetList(string iKey)
        {
            foreach (var kv in Meta)
                if (kv.Key == iKey) return kv.Value is List<string> l ? new List<string>(l) : new List<string>();
            return new List<string>();
        }
        /// <summary>有就原位換值、沒有就接在最後（與 python dict 的賦值同序）。</summary>
        public void Set(string iKey, object iValue)
        {
            for (int i = 0; i < Meta.Count; i++)
                if (Meta[i].Key == iKey) { Meta[i] = new KeyValuePair<string, object>(iKey, iValue); return; }
            Meta.Add(new KeyValuePair<string, object>(iKey, iValue));
        }
        public void Remove(string iKey) => Meta.RemoveAll(kv => kv.Key == iKey);
    }

    /// <summary>一次操作的結果：給人看的行＋退出碼＋寫過的檔。</summary>
    public sealed class SCP_WorkMemoryResult
    {
        public int Exit;
        public readonly List<string> Lines = new List<string>();
        public readonly List<string> Written = new List<string>();
        public string BriefPath = "";
        public SCP_WorkMemoryResult Say(string iLine) { Lines.Add(iLine); return this; }
        public SCP_WorkMemoryResult Fail(int iExit, string iLine) { Exit = iExit; Lines.Add(iLine); return this; }
    }

    public sealed class SCP_WorkMemory
    {
        public static readonly string[] FragmentTypes = { "decision", "knowhow", "pitfall", "state", "pointer" };
        public static readonly string[] TopicStatus = { "active", "archived" };
        static readonly string[] s_ClosedTask = { "done", "cancelled" };
        public const int BriefMaxSourceLines = 100;

        /// <summary>AgentCommands 資料根。</summary>
        public readonly string DataRoot;
        /// <summary>
        /// 具名根（related_docs 的 `&lt;名&gt;:` 前綴 ⇒ 這個根，例 `senate:`、`scp_core:`）—— 由宿主給（<see cref="HostNamedRoots"/>）。
        /// <para>TASK-0390：沒前綴的相對路徑以**資料根**為基準；舊的 `AgentCommands/…` 去前綴後同樣接資料根。
        /// `ucl_core:`／`Assets/…` 這類 Unity 專案裡的檔照實說解不了，⛔ 不猜。</para>
        /// </summary>
        public readonly IReadOnlyDictionary<string, string> NamedRoots;

        /// <summary>宿主宣告的具名根（Senate：`senate`＝repo 根、`scp_core`＝SCP_Core）。沒裝 ⇒ 只有資料根。</summary>
        public static Func<IReadOnlyDictionary<string, string>>? HostNamedRoots { get; set; }

        /// <summary>舊慣例前綴：既有 related_docs 裡的 `AgentCommands/…`（同 SCP_TavernRefPath.LegacyPrefix）。</summary>
        const string LegacyDataPrefix = "AgentCommands/";

        public string WmRoot => P(DataRoot, "WorkMemory");
        public string BriefRoot => P(DataRoot, "WorkMemoryReadBriefs");
        public string TasksRoot => SCP.Core.Tasks.SCP_TaskIO.TasksDir(new SCP.Core.Paths.SCP_DataRoot(DataRoot));   // 版面唯一一處（TASK-0390）
        public string TombstonePath => P(WmRoot, "_tombstones.md");

        public SCP_WorkMemory(string iDataRoot, IReadOnlyDictionary<string, string>? iNamedRoots = null)
        {
            DataRoot = iDataRoot.Replace('\\', '/').TrimEnd('/');
            var aRoots = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var kv in iNamedRoots ?? HostNamedRoots?.Invoke() ?? new Dictionary<string, string>())
                if (!string.IsNullOrWhiteSpace(kv.Value)) aRoots[kv.Key] = kv.Value.Replace('\\', '/').TrimEnd('/');
            NamedRoots = aRoots;
        }

        static string P(params string[] iParts) => Path.Combine(iParts).Replace('\\', '/');
        string TopicDir(string iTopic) => P(WmRoot, iTopic);
        static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        static string UtcStamp() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        // ===========================================================
        // frontmatter —— 輕量 flat 解析（value 含冒號 OK；list 只認單行 [a, b]）
        // ===========================================================
        /// <summary>讀檔成文字：與 python `read_text` 同 —— 換行一律收成 `\n`。</summary>
        public static string ReadText(string iPath)
            => File.ReadAllText(iPath, new UTF8Encoding(false)).Replace("\r\n", "\n").Replace("\r", "\n");

        /// <summary>寫檔：`\n` 一律落成 CRLF、UTF-8 無 BOM（與 Windows 上 python 文字模式同）。</summary>
        public static void WriteText(string iPath, string iText)
        {
            string? aDir = Path.GetDirectoryName(iPath);
            if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
            File.WriteAllText(iPath, iText.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(false));
        }

        public static void ParseFrontmatter(string iText, SCP_WorkMemoryDoc ioDoc)
        {
            ioDoc.Body = iText;
            if (!iText.StartsWith("---", StringComparison.Ordinal)) return;
            // python `text.split("---", 2)`：最多切兩刀
            int a = 3;
            int b = iText.IndexOf("---", a, StringComparison.Ordinal);
            if (b < 0) return;
            string aHead = iText.Substring(a, b - a);
            ioDoc.Body = PyStrip(iText.Substring(b + 3));
            foreach (string aRaw in SplitLines(aHead))
            {
                string aLine = PyStrip(aRaw);
                if (aLine.Length == 0 || aLine.StartsWith("#", StringComparison.Ordinal) || aLine.IndexOf(':') < 0) continue;
                int c = aLine.IndexOf(':');
                string aKey = PyStrip(aLine.Substring(0, c)), aVal = PyStrip(aLine.Substring(c + 1));
                if (aVal.StartsWith("[", StringComparison.Ordinal) && aVal.EndsWith("]", StringComparison.Ordinal))
                {
                    var aList = new List<string>();
                    foreach (string v in aVal.Substring(1, aVal.Length - 2).Split(','))
                        if (PyStrip(v).Length > 0) aList.Add(PyStrip(v));
                    ioDoc.Set(aKey, aList);
                }
                else ioDoc.Set(aKey, aVal);
            }
        }

        public static string DumpFrontmatter(IEnumerable<KeyValuePair<string, object>> iMeta)
        {
            var sb = new StringBuilder("---\n");
            foreach (var kv in iMeta)
                sb.Append(kv.Key).Append(": ")
                  .Append(kv.Value is List<string> l ? "[" + string.Join(", ", l) + "]" : (string)kv.Value).Append('\n');
            return sb.Append("---").ToString();
        }

        /// <summary>python `str.strip()`：去頭尾空白（含全形空白等 Unicode 空白）。</summary>
        static string PyStrip(string s) => s.Trim();

        /// <summary>python `str.splitlines()` 認得的行界。</summary>
        public static List<string> SplitLines(string s)
        {
            var aOut = new List<string>();
            var sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                char ch = s[i];
                if (ch == '\r' || ch == '\n' || ch == '\v' || ch == '\f' || ch == '\x1c' || ch == '\x1d' || ch == '\x1e'
                    || ch == '\x85' || ch == '\u2028' || ch == '\u2029')
                {
                    if (ch == '\r' && i + 1 < s.Length && s[i + 1] == '\n') i++;
                    aOut.Add(sb.ToString()); sb.Clear();
                }
                else sb.Append(ch);
            }
            if (sb.Length > 0) aOut.Add(sb.ToString());
            return aOut;
        }

        // ===========================================================
        // 讀寫底層
        // ===========================================================
        public static SCP_WorkMemoryDoc? Load(string iPath)
        {
            try
            {
                var d = new SCP_WorkMemoryDoc { Path = iPath };
                ParseFrontmatter(ReadText(iPath), d);
                return d;
            }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
        }

        /// <summary>只重寫 frontmatter，正文原樣保留。</summary>
        static void SaveMeta(SCP_WorkMemoryDoc iDoc) => WriteText(iDoc.Path, DumpFrontmatter(iDoc.Meta) + "\n\n" + iDoc.Body + "\n");

        static int TypeRank(string t) { int i = Array.IndexOf(FragmentTypes, t); return i < 0 ? 99 : i; }

        public List<SCP_WorkMemoryDoc> ListFragments(string iTopic)
        {
            string d = TopicDir(iTopic);
            var aOut = new List<SCP_WorkMemoryDoc>();
            if (!Directory.Exists(d)) return aOut;
            foreach (string f in Directory.GetFiles(d, "*.md").OrderBy(x => x.ToLowerInvariant(), StringComparer.Ordinal))
            {
                if (Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal)) continue;
                SCP_WorkMemoryDoc? frag = Load(f.Replace('\\', '/'));
                if (frag != null) aOut.Add(frag);
            }
            return aOut.OrderBy(x => x.Get("status", "active") == "active" ? 0 : 1)
                       .ThenBy(x => TypeRank(x.Get("type")))
                       .ThenBy(x => x.Get("id"), StringComparer.Ordinal).ToList();
        }

        public SCP_WorkMemoryDoc? LoadCard(string iTopic)
        {
            string p = P(TopicDir(iTopic), "_topic.md");
            return File.Exists(p) ? Load(p) : null;
        }

        IEnumerable<string> TopicNames()
            => Directory.Exists(WmRoot)
               ? Directory.GetDirectories(WmRoot).Select(x => Path.GetFileName(x)!).OrderBy(x => x.ToLowerInvariant(), StringComparer.Ordinal)
               : Enumerable.Empty<string>();

        /// <summary>task_indices 正規化：去 `#`／`TASK-`、壞值略過但出聲、排序去重。</summary>
        static List<int> IntList(IEnumerable<string> iVals, SCP_WorkMemoryResult? ioWarn)
        {
            var aOut = new List<int>();
            foreach (string v in iVals)
            {
                string s = v.Trim().TrimStart('#');
                if (s.Length == 0) continue;
                if (s.StartsWith("TASK-", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) aOut.Add(n);
                else ioWarn?.Say($"⚠ task_indices 裡有一個不是數字的值，已略過：'{v}'");
            }
            return aOut.Distinct().OrderBy(x => x).ToList();
        }

        // ===========================================================
        // 反向索引 —— Task 側只讀
        // ===========================================================
        string TaskPath(int i) => P(TasksRoot, i.ToString("D4", CultureInfo.InvariantCulture) + ".md");

        SCP_WorkMemoryDoc? LoadTask(int i)
        {
            string p = TaskPath(i);
            return File.Exists(p) ? Load(p) : null;
        }

        string DescribeTask(int i)
        {
            string id = "TASK-" + i.ToString("D4", CultureInfo.InvariantCulture);
            if (!File.Exists(TaskPath(i)))
                return $"{id}　⚠ **號碼在，但單檔讀不到**（{Path.GetFileName(TaskPath(i))}）—— 這是「連結壞了」不是「沒有這張單」";
            SCP_WorkMemoryDoc? t = LoadTask(i);
            if (t == null) return $"{id}　⚠ **讀取失敗** —— 沒有讀數，不是沒有內容";
            string status = t.Get("status").Trim();
            if (status.Length == 0) status = "?";
            string who = Participants(TaskPath(i));
            string mark = s_ClosedTask.Contains(status) ? "✅" : "🔸";
            return $"{mark} {id}　`{status}`　{t.Get("title").Trim()}" + (who.Length > 0 ? $"　[{who}]" : "");
        }

        static string Participants(string iPath)
        {
            string[] aLines;
            try { aLines = ReadText(iPath).Split('\n'); } catch (IOException) { return ""; }
            var aOut = new List<string>();
            string persona = ""; bool inBlock = false;
            foreach (string line in aLines)
            {
                if (line.StartsWith("participants:", StringComparison.Ordinal)) { inBlock = true; continue; }
                if (!inBlock) continue;
                if (line.Length == 0 || (line[0] != ' ' && line[0] != '-') || line.StartsWith("---", StringComparison.Ordinal)) break;
                Match m = Regex.Match(line, @"^\s*-\s*persona:\s*(\S+)");
                if (m.Success) { persona = m.Groups[1].Value; continue; }
                m = Regex.Match(line, @"^\s*role:\s*(\S+)");
                if (m.Success && persona.Length > 0) { aOut.Add($"{persona}({m.Groups[1].Value})"); persona = ""; }
            }
            return string.Join("、", aOut);
        }

        /// <summary>掃全部單檔找 `memory_topic == topic` —— 關聯關係的真相源。</summary>
        List<int> ScanTasksByTopic(string iTopic)
        {
            var aOut = new List<int>();
            if (!Directory.Exists(TasksRoot)) return aOut;
            foreach (string f in Directory.GetFiles(TasksRoot, "*.md"))
            {
                SCP_WorkMemoryDoc? t = Load(f);
                if (t == null || t.Get("memory_topic").Trim() != iTopic) continue;
                if (int.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)) aOut.Add(n);
            }
            aOut.Sort();
            return aOut;
        }

        string TaskBucket(int i)
        {
            SCP_WorkMemoryDoc? t = File.Exists(TaskPath(i)) ? LoadTask(i) : null;
            if (t == null) return "unreadable";
            return s_ClosedTask.Contains(t.Get("status").Trim()) ? "closed" : "open";
        }

        public List<string> DescribeRelatedTasks(SCP_WorkMemoryDoc iCard)
        {
            string topic = iCard.Get("id").Trim();
            List<int> scanned = topic.Length > 0 ? ScanTasksByTopic(topic) : new List<int>();
            List<int> declared = IntList(iCard.GetList("task_indices"), null);
            List<int> oneway = declared.Where(i => !scanned.Contains(i)).ToList();
            List<int> idx = scanned.Concat(oneway).ToList();
            if (idx.Count == 0)
                return new List<string> { "🔗 關聯 Task：**沒有任何單指向這個主題**（沒有單的 `memory_topic` 是它，記憶側也沒宣告）—— 這不等於「沒有單」，只等於**沒有人建過這個連結**" };
            var rows = new List<string> { $"🔗 關聯 Task（{idx.Count} 張；掃 `memory_topic` 得 {scanned.Count}" + (oneway.Count > 0 ? $"，另有 {oneway.Count} 張只在記憶側宣告" : "") + "）：" };
            foreach (int i in idx)
                rows.Add("    · " + DescribeTask(i) + (oneway.Contains(i) ? "　⚠ **單向**：Task 側的 `memory_topic` 沒指回來" : ""));
            int open = 0, closed = 0, unreadable = 0;
            foreach (int i in idx)
                switch (TaskBucket(i)) { case "open": open++; break; case "closed": closed++; break; default: unreadable++; break; }
            rows.Add($"    ⇒ 未關 **{open}** ／ 已關 {closed} ／ **讀不到 {unreadable}**（共 {idx.Count} 張）");
            if (unreadable > 0) rows.Add("      ⛔ **有讀不到的單 ⇒ 不建議歸檔** —— 「讀不到」不是「已完成」，先把那幾筆查清楚");
            else if (open == 0) rows.Add("      ✅ 全部關了 ⇒ 這個主題可以考慮歸檔（op=archive）");
            else rows.Add("      ⇒ 還不到歸檔的時候");
            return rows;
        }

        // ===========================================================
        // git 守衛 —— 歸檔／刪除之前先驗「已入版控」
        // ===========================================================
        static (int Code, string Out, string Err) Git(string iCwd, params string[] iArgs)
        {
            try
            {
                SCP_GitResult r = SCP_Git.RunTimeout(iCwd, 30_000, iArgs);
                return (r.Exit, (r.StdOut ?? "").Trim(), (r.StdErr ?? "").Trim());
            }
            catch (Exception e) { return (-1, "", e.Message); }
        }

        static string? OwningWorktree(string iPath)
        {
            string cwd = Directory.Exists(iPath) ? iPath : (Path.GetDirectoryName(iPath) ?? iPath);
            var r = Git(cwd, "rev-parse", "--show-toplevel");
            return r.Code == 0 && r.Out.Length > 0 ? r.Out.Replace('\\', '/') : null;
        }

        static string Rel(string iTop, string iPath)
        {
            string top = Path.GetFullPath(iTop).Replace('\\', '/').TrimEnd('/') + "/";
            string full = Path.GetFullPath(iPath).Replace('\\', '/');
            return full.StartsWith(top, StringComparison.OrdinalIgnoreCase) ? full.Substring(top.Length) : Path.GetFileName(full);
        }

        /// <summary>
        /// 這個目錄是不是**真的在版控裡且乾淨**。先問 ls-files（每個磁碟檔都被追蹤），再問 status。
        /// git 不可用／問不出屬於哪個工作區 ⇒ 不乾淨（⛔ 沒有讀數不是乾淨）。
        /// </summary>
        public static (bool Clean, string Detail) GitDirStatus(string iPath)
        {
            string? top = OwningWorktree(iPath);
            if (top == null) return (false, "⚠ 問不出這個路徑屬於哪個 git 工作區 —— 這不是「乾淨」，是**沒有讀數**（git 不可用／不在任何 repo 內）");
            string rel = Rel(top, iPath);
            if (rel.Length == 0) rel = ".";
            var ls = Git(top, "ls-files", "--", rel);
            if (ls.Code != 0) return (false, $"⚠ `git ls-files` 回非零（{ls.Code}）：{ls.Err} —— 沒有讀數，不放行");
            var tracked = new HashSet<string>(ls.Out.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0));
            IEnumerable<string> disk = Directory.Exists(iPath)
                ? Directory.GetFiles(iPath, "*", SearchOption.AllDirectories).OrderBy(x => x, StringComparer.Ordinal)
                : new[] { iPath };
            var missing = new List<string>();
            foreach (string p in disk)
            {
                string r = Rel(top, p);
                if (tracked.Contains(r)) continue;
                var ci = Git(top, "check-ignore", "-v", "--", r);
                string why = ci.Code == 0 && ci.Out.Length > 0 ? ci.Out.Split('\t')[0].Trim() : "";
                missing.Add(r + "　" + (why.Length > 0 ? $"← **被 ignore**（{why}）" : "← untracked"));
            }
            if (tracked.Count == 0)
                return (false, $"[工作區 {top}]\n**`{rel}` 底下沒有任何檔案在版控裡**（`git ls-files` 回 0 筆）\n" + string.Join("\n", missing));
            if (missing.Count > 0)
                return (false, $"[工作區 {top}]\n磁碟上有 {missing.Count} 個檔**不在版控裡**（ignore 與 untracked 分開標）：\n" + string.Join("\n", missing));
            var st = Git(top, "status", "--porcelain", "--untracked-files=all", "--", rel);
            if (st.Code != 0) return (false, $"⚠ git 回非零（{st.Code}）：{st.Err} —— 沒有讀數，不放行");
            return (st.Out.Length == 0, st.Out.Length > 0 ? $"[工作區 {top}]\n" + st.Out : "");
        }

        /// <summary>**擁有這份內容的那個工作區**的 HEAD（WorkMemory 可能是巢狀 submodule，⛔ 不取父 repo 的）。</summary>
        public static string GitHeadSha(string iPath)
        {
            string? top = OwningWorktree(iPath);
            if (top == null) return "";
            var r = Git(top, "rev-parse", "HEAD");
            return r.Code == 0 ? r.Out : "";
        }

        // ===========================================================
        // op
        // ===========================================================
        public SCP_WorkMemoryResult Topics()
        {
            var r = new SCP_WorkMemoryResult();
            if (!Directory.Exists(WmRoot)) return r.Say("（工作記憶區為空 —— 用 op=init 建第一個主題）");
            var groups = TopicStatus.ToDictionary(s => s, s => new List<string>());
            var unknown = new List<string>();
            foreach (string name in TopicNames())
            {
                string d = TopicDir(name);
                SCP_WorkMemoryDoc? card = LoadCard(name);
                int n = Directory.GetFiles(d, "*.md").Count(f => !Path.GetFileName(f).StartsWith("_", StringComparison.Ordinal));
                string status = card != null ? card.Get("status", "?").Trim() : "?";
                List<int> tasks = card != null ? IntList(card.GetList("task_indices"), r) : new List<int>();
                string rel = card != null ? string.Join(", ", card.GetList("related_topics")) : "";
                string row = $"  - {name}  「{card?.Get("title") ?? ""}」 fragments={n}"
                             + (tasks.Count > 0 ? $"  🔗 TASK {string.Join(",", tasks)}" : "")
                             + (rel.Length > 0 ? $"  ↔ {rel}" : "");
                if (status == "archived")
                {
                    string sha = card!.Get("archived_commit").Trim();
                    row += $"  📦 已歸檔（{(sha.Length > 0 ? sha.Substring(0, Math.Min(9, sha.Length)) : "sha 未記")}）";
                }
                (groups.ContainsKey(status) ? groups[status] : unknown).Add(row);
            }
            r.Say("🧠 工作記憶主題：");
            r.Say($"\n■ active（{groups["active"].Count} 個）—— 還在做的");
            r.Say(groups["active"].Count > 0 ? string.Join("\n", groups["active"]) : "  （無）");
            r.Say($"\n■ archived（{groups["archived"].Count} 個）—— 已退場，內容仍在磁碟上，全文照樣讀得到");
            r.Say(groups["archived"].Count > 0 ? string.Join("\n", groups["archived"]) : "  （無）");
            if (unknown.Count > 0)
            {
                r.Say($"\n■ ⚠ status 認不得（{unknown.Count} 個）—— 主題卡壞了或缺 status，不是 active 也不是 archived");
                r.Say(string.Join("\n", unknown));
            }
            if (File.Exists(TombstonePath))
            {
                int nt = ReadText(TombstonePath).Split('\n').Count(x => x.StartsWith("- ", StringComparison.Ordinal));
                r.Say($"\n🪦 另有 **{nt}** 個主題已被刪除（墓碑在 {Path.GetFileName(TombstonePath)}，內容在 git 裡）");
            }
            return r;
        }

        public SCP_WorkMemoryResult Init(string iTopic, string iTitle, string iDesc)
        {
            var r = new SCP_WorkMemoryResult();
            string card = P(TopicDir(iTopic), "_topic.md");
            if (File.Exists(card)) return r.Fail(1, $"⚠ 主題已存在：{iTopic}");
            var d = new SCP_WorkMemoryDoc { Path = card };
            d.Set("id", iTopic); d.Set("title", iTitle); d.Set("status", "active"); d.Set("created_at", Today());
            d.Set("related_topics", new List<string>()); d.Set("key_docs", new List<string>());
            WriteText(card, DumpFrontmatter(d.Meta) + "\n\n" + (iDesc.Length > 0 ? iDesc : iTitle) + "\n");
            r.Written.Add(card);
            r.Say($"✅ 主題已建立：{iTopic}（{iTitle}）");
            RebuildIndex(iTopic, r);
            return r;
        }

        static List<string> CsvList(string s) => s.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        public SCP_WorkMemoryResult Add(string iTopic, string iType, string iId, string iTitle, string iBody,
                                        string iLinks, string iDocs, string iBy)
        {
            var r = new SCP_WorkMemoryResult();
            if (!FragmentTypes.Contains(iType)) return r.Fail(2, $"✗ type 必須是 {string.Join("／", FragmentTypes)}");
            if (!File.Exists(P(TopicDir(iTopic), "_topic.md"))) return r.Fail(2, $"✗ 主題不存在：{iTopic}（先 op=init）");
            string fragId = iId.StartsWith(iType + "_", StringComparison.Ordinal) ? iId : iType + "_" + iId;
            string path = P(TopicDir(iTopic), fragId + ".md");
            if (File.Exists(path)) return r.Fail(2, $"✗ fragment 已存在：{fragId} —— 記憶寫一次不改寫；要更新走 op=supersede");
            string body = iBody.Replace("\r\n", "\n").Replace("\r", "\n");
            if (body.Trim().Length == 0) return r.Fail(2, "✗ 缺 body");
            List<(string, string, string)> similar = SimilarTitles(iTitle, iTopic, fragId);
            if (similar.Count > 0)
            {
                r.Say("⚠ 防洗版警示 —— 發現標題近似的既有 fragment（確認不是同一條再繼續；是同一條請改用 link／supersede）：");
                foreach (var s in similar.Take(5)) r.Say($"    - {s.Item1}/{s.Item2} — {s.Item3}");
            }
            var d = new SCP_WorkMemoryDoc { Path = path };
            d.Set("id", fragId); d.Set("topic", iTopic); d.Set("title", iTitle); d.Set("type", iType);
            d.Set("status", "active"); d.Set("created_at", Today()); d.Set("created_by", iBy.Length > 0 ? iBy : "unknown");
            d.Set("links", CsvList(iLinks)); d.Set("related_docs", CsvList(iDocs));
            WriteText(path, DumpFrontmatter(d.Meta) + "\n\n" + body.Trim() + "\n");
            r.Written.Add(path);
            r.Say($"✅ fragment 已寫入：{iTopic}/{fragId}");
            RebuildIndex(iTopic, r);
            return r;
        }

        static HashSet<string> Bigrams(string s)
        {
            string t = new string(s.ToLowerInvariant().Where(ch => !char.IsWhiteSpace(ch)).ToArray());
            var set = new HashSet<string>();
            if (t.Length > 1) for (int i = 0; i < t.Length - 1; i++) set.Add(t.Substring(i, 2));
            else set.Add(t);
            return set;
        }

        List<(string, string, string)> SimilarTitles(string iTitle, string iTopic, string iFragId)
        {
            var aOut = new List<(string, string, string)>();
            HashSet<string> q = Bigrams(iTitle);
            if (!Directory.Exists(WmRoot) || q.Count == 0) return aOut;
            foreach (string t in TopicNames())
                foreach (SCP_WorkMemoryDoc f in ListFragments(t))
                {
                    if (t == iTopic && f.Get("id") == iFragId) continue;
                    HashSet<string> o = Bigrams(f.Get("title"));
                    if (o.Count == 0) continue;
                    int inter = q.Count(o.Contains), union = q.Union(o).Count();
                    if ((double)inter / Math.Max(union, 1) > 0.5) aOut.Add((t, f.Get("id"), f.Get("title")));
                }
            return aOut;
        }

        public SCP_WorkMemoryResult Supersede(string iTopic, string iId, string iBy,
                                              string iNewId, string iNewTitle, string iNewBody, string iNewBy)
        {
            var r = new SCP_WorkMemoryResult();
            SCP_WorkMemoryDoc? frag = ListFragments(iTopic).FirstOrDefault(f => f.Get("id") == iId);
            if (frag == null) return r.Fail(2, $"✗ 找不到 fragment：{iTopic}/{iId}");
            string newId = iBy;
            if (iNewId.Length > 0)
            {
                string body = iNewBody.Replace("\r\n", "\n").Replace("\r", "\n");
                if (body.Trim().Length == 0 || iNewTitle.Length == 0) return r.Fail(2, "✗ 一步式需要 new_title 與 new_body");
                string type = frag.Get("type", "state");
                string full = iNewId.StartsWith(type + "_", StringComparison.Ordinal) ? iNewId : type + "_" + iNewId;
                string np = P(TopicDir(iTopic), full + ".md");
                if (File.Exists(np)) return r.Fail(2, $"✗ 新 fragment 已存在：{full}");
                var d = new SCP_WorkMemoryDoc { Path = np };
                d.Set("id", full); d.Set("topic", iTopic); d.Set("title", iNewTitle); d.Set("type", type);
                d.Set("status", "active"); d.Set("created_at", Today());
                d.Set("created_by", iNewBy.Length > 0 ? iNewBy : frag.Get("created_by", "unknown"));
                d.Set("links", new List<string> { $"{iTopic}/{frag.Get("id")}" });
                d.Set("related_docs", frag.GetList("related_docs"));
                WriteText(np, DumpFrontmatter(d.Meta) + "\n\n" + body.Trim() + "\n");
                r.Written.Add(np);
                newId = full;
                r.Say($"✅ 新 fragment 已寫入：{iTopic}/{full}");
            }
            frag.Set("status", "superseded");
            if (newId.Length > 0)
            {
                List<string> links = frag.GetList("links");
                string target = newId.Contains("/") ? newId : $"{iTopic}/{newId}";
                if (!links.Contains(target)) links.Add(target);
                frag.Set("links", links);
            }
            SaveMeta(frag);
            r.Written.Add(frag.Path);
            r.Say($"✅ {iTopic}/{iId} → superseded" + (newId.Length > 0 ? $"（由 {newId} 取代）" : ""));
            RebuildIndex(iTopic, r);
            return r;
        }

        static bool TryRef(string iRef, out string oTopic, out string oFrag)
        {
            oTopic = oFrag = "";
            int i = iRef.IndexOf('/');
            if (i < 0) return false;
            oTopic = iRef.Substring(0, i); oFrag = iRef.Substring(i + 1);
            return true;
        }

        public SCP_WorkMemoryResult Link(string iFrom, string iTo)
        {
            var r = new SCP_WorkMemoryResult();
            if (!TryRef(iFrom, out string ta, out string fa)) return r.Fail(2, $"✗ 關聯引用需為 <topic>/<fragment-id>：{iFrom}");
            if (!TryRef(iTo, out string tb, out string fb)) return r.Fail(2, $"✗ 關聯引用需為 <topic>/<fragment-id>：{iTo}");
            int changed = 0;
            foreach (var (t, f, other) in new[] { (ta, fa, $"{tb}/{fb}"), (tb, fb, $"{ta}/{fa}") })
            {
                SCP_WorkMemoryDoc? frag = ListFragments(t).FirstOrDefault(x => x.Get("id") == f);
                if (frag == null) return r.Fail(2, $"✗ 找不到 {t}/{f}");
                List<string> links = frag.GetList("links");
                if (links.Contains(other)) continue;
                links.Add(other);
                frag.Set("links", links);
                SaveMeta(frag);
                r.Written.Add(frag.Path);
                changed++;
            }
            r.Say($"✅ 已建立雙向關聯：{ta}/{fa} ↔ {tb}/{fb}（更新 {changed} 檔）");
            RebuildIndex(ta, r);
            if (tb != ta) RebuildIndex(tb, r);
            return r;
        }

        /// <summary>機械生成 `_index.md` —— 事實源永遠是 fragment 檔。</summary>
        public void RebuildIndex(string iTopic, SCP_WorkMemoryResult? ioR = null)
        {
            List<SCP_WorkMemoryDoc> frags = ListFragments(iTopic);
            var lines = new List<string> { $"# 工作記憶索引 — {iTopic}", "",
                "> 機械生成（work_memory.py index）— 手改會被覆寫。事實源 = 各 fragment 檔。", "" };
            foreach (string t in FragmentTypes)
            {
                List<SCP_WorkMemoryDoc> rows = frags.Where(f => f.Get("type") == t).ToList();
                if (rows.Count == 0) continue;
                lines.Add("## " + t);
                foreach (SCP_WorkMemoryDoc f in rows)
                {
                    string st = f.Get("status", "active");
                    string mark = st == "active" ? "" : $" ~~[{st}]~~";
                    List<string> links = f.GetList("links");
                    lines.Add($"- **{f.Get("id")}** — {f.Get("title")}{mark}" + (links.Count > 0 ? "  ↔ " + string.Join(", ", links) : ""));
                }
                lines.Add("");
            }
            string p = P(TopicDir(iTopic), "_index.md");
            WriteText(p, string.Join("\n", lines).TrimEnd() + "\n");
            ioR?.Written.Add(p);
        }

        public SCP_WorkMemoryResult Index(string iTopic)
        {
            var r = new SCP_WorkMemoryResult();
            foreach (string t in iTopic.Length > 0 ? new[] { iTopic } : TopicNames().ToArray())
            {
                RebuildIndex(t, r);
                r.Say($"✅ 索引重建：{t}");
            }
            return r;
        }

        // ── read ──────────────────────────────────────────────────

        /// <summary>related_docs 的本地檔案 ref → 絕對路徑；非本地或越界的回原因。</summary>
        public string? ResolveRelatedDoc(string iRef, out string oWhy)
        {
            oWhy = "";
            string raw = iRef.Trim();
            if (raw.StartsWith("commit:", StringComparison.Ordinal) || raw.StartsWith("tavern:", StringComparison.Ordinal)
                || raw.StartsWith("workmem:", StringComparison.Ordinal)) { oWhy = "非本地檔案引用"; return null; }
            string baseDir = DataRoot;
            // `<名>:` 前綴（小寫、不是磁碟代號 `D:/`）⇒ 宿主宣告的具名根；沒宣告的照實說，⛔ 不猜
            Match aPrefix = Regex.Match(raw, @"^([a-z_]+):(?![\\/])");
            if (aPrefix.Success)
            {
                string aName = aPrefix.Groups[1].Value;
                if (!NamedRoots.TryGetValue(aName, out string? aRoot))
                {
                    oWhy = aName == "ucl_core"
                        ? "`ucl_core:` 是 Unity 專案裡的檔（UCL_Core）—— Senate 不讀 Unity 專案"
                        : $"`{aName}:` 不是宿主宣告的根（有：{(NamedRoots.Count > 0 ? string.Join("、", NamedRoots.Keys) : "無")}）";
                    return null;
                }
                baseDir = aRoot; raw = raw.Substring(aPrefix.Length);
            }
            else if (raw.StartsWith(LegacyDataPrefix, StringComparison.OrdinalIgnoreCase)) raw = raw.Substring(LegacyDataPrefix.Length);
            raw = Regex.Replace(raw, @":\d+(?:-\d+)?$", "");
            string cand;
            try
            {
                cand = Path.GetFullPath(Path.IsPathRooted(raw) ? raw : Path.Combine(baseDir, raw)).Replace('\\', '/');
            }
            catch (Exception e) { oWhy = "路徑解析失敗：" + e.Message; return null; }
            bool Under(string? root) => root != null
                && cand.StartsWith(Path.GetFullPath(root).Replace('\\', '/').TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase);
            if (!Under(DataRoot) && !NamedRoots.Values.Any(Under))
            { oWhy = "路徑不在允許的工作區範圍（資料根＋宿主宣告的根；Unity 專案裡的檔 Senate 不讀）"; return null; }
            if (!File.Exists(cand)) { oWhy = "檔案不存在或不是一般檔案"; return null; }
            return cand;
        }

        string SaveBrief(string iTopic, bool iWithLinks, string iTypes, string iResult, List<string> iLines, List<string> iDocs)
        {
            DateTime at = DateTime.UtcNow;
            string path = P(BriefRoot, at.ToString("yyyyMMdd_HHmmss_ffffff", CultureInfo.InvariantCulture) + "Z_" + iTopic + ".md");
            var b = new List<string> { "---", $"title: 工作記憶 Read Brief — {iTopic}", $"topic: {iTopic}",
                $"read_at_utc: {at.ToString("yyyy-MM-ddTHH:mm:ss.ffffff+00:00", CultureInfo.InvariantCulture)}",
                $"result: {iResult}", $"with_links: {(iWithLinks ? "true" : "false")}", $"types: {(iTypes.Length > 0 ? iTypes : "all")}",
                "---", "", "# 工作記憶 Read Brief", "", "## 本次記憶摘要", "" };
            b.AddRange(iLines);
            if (iDocs.Count > 0)
            {
                b.Add("## 已嵌入的本地來源"); b.Add("");
                foreach (string rf in iDocs.Distinct())
                {
                    b.Add($"### `{rf}`"); b.Add("");
                    string? src = ResolveRelatedDoc(rf, out string why);
                    if (src == null) { b.Add($"> 未嵌入：{why}。"); b.Add(""); continue; }
                    string content;
                    try { content = ReadText(src); }
                    catch (Exception e) { b.Add($"> 未嵌入：讀取失敗：{e.Message}"); b.Add(""); continue; }
                    string suffix = Path.GetExtension(src).TrimStart('.');
                    if (suffix.Length == 0) suffix = "text";
                    List<string> sl = SplitLines(content);
                    b.Add("~~~~" + suffix); b.Add(string.Join("\n", sl.Take(BriefMaxSourceLines))); b.Add("~~~~"); b.Add("");
                    if (sl.Count > BriefMaxSourceLines)
                    {
                        b.Add($"> ⚠ 已截斷：僅顯示前 {BriefMaxSourceLines} / {sl.Count} 行，後續 {sl.Count - BriefMaxSourceLines} 行請直接查看原始檔 `{rf}`。");
                        b.Add("");
                    }
                }
            }
            WriteText(path, string.Join("\n", b).TrimEnd() + "\n");
            return path;
        }

        public SCP_WorkMemoryResult Read(string iTopic, bool iWithLinks, string iTypes)
        {
            var r = new SCP_WorkMemoryResult();
            SCP_WorkMemoryDoc? card = LoadCard(iTopic);
            if (card == null)
            {
                var nf = new List<string> { $"✗ 主題不存在：{iTopic}。現有主題：" };
                if (Directory.Exists(WmRoot)) nf.AddRange(TopicNames().Select(t => "  - " + t));
                else nf.Add("（工作記憶區為空 —— 用 op=init 建第一個主題）");
                r.BriefPath = SaveBrief(iTopic, iWithLinks, iTypes, "not_found", nf, new List<string>());
                r.Lines.AddRange(nf);
                r.Exit = 2;
                return r;
            }
            List<string> want = CsvList(iTypes);
            if (want.Count == 0) want = FragmentTypes.ToList();
            var lines = new List<string>();
            var docs = new List<string>();
            lines.Add($"🧠 工作記憶 — {card.Get("title", iTopic)}  [{card.Get("status", "?")}]");
            if (card.Get("status").Trim() == "archived")
            {
                string sha = card.Get("archived_commit").Trim(), at = card.Get("archived_at").Trim();
                lines.Add("   📦 **已歸檔**" + (at.Length > 0 ? $"（{at}）" : "")
                          + (sha.Length > 0 ? $"　commit `{sha}`" : "　⚠ 沒有記 archived_commit —— 接手的人不知道去哪顆 commit 找"));
                lines.Add("   ⇒ **這不是「沒有記憶」** —— 正文照樣在下面，它只是不再被維護了。");
            }
            lines.AddRange(DescribeRelatedTasks(card));
            List<string> keyDocs = card.GetList("key_docs");
            if (keyDocs.Count > 0) { lines.Add("   📚 權威文件：" + string.Join(", ", keyDocs)); docs.AddRange(keyDocs); }
            List<string> relTopics = card.GetList("related_topics");
            if (relTopics.Count > 0) lines.Add("   ↔ 關聯主題：" + string.Join(", ", relTopics));
            if (card.Body.Length > 0) { lines.Add(""); lines.Add(card.Body); lines.Add(""); }

            List<SCP_WorkMemoryDoc> frags = ListFragments(iTopic).Where(f => want.Contains(f.Get("type"))).ToList();
            var linked = new List<string>();
            foreach (SCP_WorkMemoryDoc f in frags)
            {
                string st = f.Get("status", "active");
                if (st != "active")
                {
                    lines.Add($"--- ~~{f.Get("id")}~~ [{st}]（正文略；取代鏈見 links：{string.Join(", ", f.GetList("links"))}）");
                    continue;
                }
                lines.Add($"--- [{f.Get("type")}] {f.Get("title")}  (id: {f.Get("id")}, by {f.Get("created_by")} @ {f.Get("created_at")})");
                List<string> rd = f.GetList("related_docs");
                if (rd.Count > 0) { lines.Add("    📚 " + string.Join(", ", rd)); docs.AddRange(rd); }
                List<string> lk = f.GetList("links");
                if (lk.Count > 0) { lines.Add("    ↔ " + string.Join(", ", lk)); linked.AddRange(lk); }
                lines.Add(f.Body); lines.Add("");
            }
            if (iWithLinks && linked.Count > 0)
            {
                var seen = new HashSet<string>(frags.Select(f => $"{iTopic}/{f.Get("id")}"));
                lines.Add("═══ 關聯記憶（1-hop，with_links）═══");
                foreach (string rf in linked.Distinct())
                {
                    if (!seen.Add(rf)) continue;
                    if (!TryRef(rf, out string t, out string fid)) continue;
                    SCP_WorkMemoryDoc? lf = ListFragments(t).FirstOrDefault(x => x.Get("id") == fid);
                    if (lf == null) { lines.Add($"--- ⚠ 關聯目標不存在（dangling，可能是未來主題）：{rf}"); continue; }
                    lines.Add($"--- [來自 {t}] [{lf.Get("type")}] {lf.Get("title")}  (id: {lf.Get("id")})");
                    docs.AddRange(lf.GetList("related_docs"));
                    lines.Add(lf.Body); lines.Add("");
                }
            }
            r.BriefPath = SaveBrief(iTopic, iWithLinks, iTypes, "success", lines, docs);
            r.Lines.AddRange(lines);
            return r;
        }

        // ── tasks／archive／delete ─────────────────────────────────

        /// <summary>主題卡的 task_indices。<paramref name="iSet"/> 為 null ＝ 沒給（空字串＝清空）；三個都沒給 ＝ 只印現況。</summary>
        public SCP_WorkMemoryResult Tasks(string iTopic, string? iSet, string iAdd, string iRemove, string iTaskCmdHint)
        {
            var r = new SCP_WorkMemoryResult();
            SCP_WorkMemoryDoc? card = LoadCard(iTopic);
            if (card == null) return r.Fail(2, $"✗ 主題不存在：{iTopic}");
            List<int> before = IntList(card.GetList("task_indices"), r);
            if (iSet == null && iAdd.Length == 0 && iRemove.Length == 0)
            {
                r.Say($"🔗 {iTopic} 目前的 task_indices：{Show(before)}");
                r.Lines.AddRange(DescribeRelatedTasks(card));
                return r;
            }
            List<int> after;
            if (iSet != null) after = IntList(iSet.Split(','), r);
            else
            {
                after = new List<int>(before);
                after.AddRange(IntList(iAdd.Split(','), r));
                var drop = new HashSet<int>(IntList(iRemove.Split(','), r));
                after = after.Distinct().Where(i => !drop.Contains(i)).ToList();
            }
            after = after.Distinct().OrderBy(x => x).ToList();
            card.Set("task_indices", after.Select(i => i.ToString(CultureInfo.InvariantCulture)).ToList());
            SaveMeta(card);
            r.Written.Add(card.Path);
            List<int> reread = IntList((LoadCard(iTopic) ?? new SCP_WorkMemoryDoc()).GetList("task_indices"), r);
            r.Say($"✅ task_indices：{Show(before)} → {Show(reread)}（回讀自 _topic.md）");
            if (!reread.SequenceEqual(after)) return r.Fail(1, $"⚠ 回讀值與預期不符（預期 {Show(after)}）—— 有第二個寫入者，或 frontmatter 解析漏了");
            r.Lines.AddRange(DescribeRelatedTasks(LoadCard(iTopic)!));
            r.Say("");
            r.Say("📌 Task 側的 `memory_topic` 由任務寫入端寫，本指令不碰它。要補另一半：" + iTaskCmdHint);
            return r;
        }

        static string Show(List<int> l) => l.Count == 0 ? "（空）" : "[" + string.Join(", ", l) + "]";

        public SCP_WorkMemoryResult Archive(string iTopic, string iCommit, bool iUndo, string iTaskCmdHint)
        {
            var r = new SCP_WorkMemoryResult();
            SCP_WorkMemoryDoc? card = LoadCard(iTopic);
            if (card == null) return r.Fail(2, $"✗ 主題不存在：{iTopic}");
            string cur = card.Get("status").Trim();
            string target = iUndo ? "active" : "archived";
            if (cur == target) return r.Say($"⚠ {iTopic} 已經是 `{target}` —— 什麼都沒做");
            string d = TopicDir(iTopic);
            if (!iUndo)
            {
                List<int> idx = IntList(card.GetList("task_indices"), r);
                List<int> open = idx.Where(i => File.Exists(TaskPath(i)) && TaskBucket(i) == "open").ToList();
                if (open.Count > 0)
                {
                    r.Say("⚠ 這個主題還有未關的關聯單：");
                    foreach (int i in open) r.Say("    · " + DescribeTask(i));
                    r.Say("  ⇒ **警示不是擋**（歸檔是 PM 的判斷）。但接手的人會拿不到「上次做到哪」。");
                }
                else if (idx.Count == 0)
                    r.Say("⚠ 這個主題**沒有建過反向索引**（task_indices 是空的）⇒ 無法檢查「相關 Task 是不是都關了」。這不是「都關了」，是**沒有讀數**。");
                var (clean, detail) = GitDirStatus(d);
                if (!clean)
                {
                    r.Say($"\n🛑 **擋下**：`{Rel(DataRoot, d)}` 在 git 裡不乾淨 ⇒ 不歸檔。");
                    r.Say("   實際讀數：");
                    foreach (string ln in detail.Length > 0 ? detail.Split('\n') : new[] { "（空 —— 但上面說不乾淨，這本身就是要看的訊號）" }) r.Say("     " + ln);
                    return r.Fail(3, "   ⇒ 先 commit 再來。「刪掉也沒關係，git 有」是一個需要被驗的前提。");
                }
                r.Say($"✅ git 守衛：`{Rel(DataRoot, d)}` 乾淨（無 modified／staged／untracked）");
            }
            string sha = iCommit.Trim();
            if (sha.Length == 0 && !iUndo) sha = GitHeadSha(d);
            card.Set("status", target);
            if (iUndo) { card.Remove("archived_at"); card.Remove("archived_commit"); }
            else
            {
                card.Set("archived_at", UtcStamp());
                card.Set("archived_commit", sha);
                if (sha.Length == 0) r.Say("⚠ 拿不到 HEAD sha ⇒ `archived_commit` 留空（接手的人會知道它是空的，而不是被塞一個假 sha）");
            }
            SaveMeta(card);
            r.Written.Add(card.Path);
            SCP_WorkMemoryDoc re = LoadCard(iTopic) ?? new SCP_WorkMemoryDoc();
            r.Say($"✅ {iTopic}：`{cur}` → `{re.Get("status")}`（回讀自 _topic.md）"
                  + (re.Get("archived_commit").Length > 0 ? $"　commit `{re.Get("archived_commit")}`" : ""));
            if (!iUndo)
            {
                r.Say("");
                r.Say("📌 Task 側那一格本指令不寫。要讓回看單子的人接得回來，對每一張關聯單跑：");
                r.Say("   " + iTaskCmdHint.Replace("<sha>", sha.Length > 0 ? sha : "<sha>"));
            }
            return r;
        }

        public SCP_WorkMemoryResult Delete(string iTopic, string iCommit, string iBy, bool iConfirm, string iTaskCmdHint)
        {
            var r = new SCP_WorkMemoryResult();
            string d = TopicDir(iTopic);
            SCP_WorkMemoryDoc? card = LoadCard(iTopic);
            if (card == null) return r.Fail(2, $"✗ 主題不存在：{iTopic}");
            var (clean, detail) = GitDirStatus(d);
            if (!clean)
            {
                r.Say($"🛑 **擋下**：`{Rel(DataRoot, d)}` 在 git 裡不乾淨 ⇒ 不刪。");
                r.Say("   實際讀數：");
                foreach (string ln in detail.Length > 0 ? detail.Split('\n') : new[] { "（空）" }) r.Say("     " + ln);
                return r.Fail(3, "   ⇒ **刪除是不可逆的**，而沒入版控的內容刪掉就真的沒了。");
            }
            string sha = iCommit.Trim();
            if (sha.Length == 0) sha = GitHeadSha(d);
            if (!iConfirm)
            {
                r.Say("🛑 **dry-run**（沒帶 confirm=1）⇒ 什麼都沒刪。");
                r.Say($"   git 守衛已通過（目錄乾淨，HEAD `{(sha.Length > 0 ? sha.Substring(0, Math.Min(9, sha.Length)) : "?")}`）。");
                return r.Say("   要真的刪：同一道指令加 `--arg confirm=1`。");
            }
            int n = Directory.GetFiles(d, "*.md").Length;
            Directory.Delete(d, true);
            if (!File.Exists(TombstonePath))
                WriteText(TombstonePath, "# 工作記憶 — 墓碑（append-only）\n\n"
                    + "> **刪除可以, 失聯不行。** 每一行指向那個主題最後存在的 commit。\n"
                    + "> 這個檔以 `_` 開頭 ⇒ topics/index 的目錄掃描天生略過它。\n\n");
            string line = $"- `{iTopic}` 「{card.Get("title")}」 — 刪於 {UtcStamp()} by {(iBy.Length > 0 ? iBy : "unknown")}；"
                          + $"{n} 個檔的內容在 commit `{(sha.Length > 0 ? sha : "（拿不到 HEAD sha）")}`\n";
            File.AppendAllText(TombstonePath, line.Replace("\n", "\r\n"), new UTF8Encoding(false));
            r.Written.Add(TombstonePath);
            r.Say($"🪦 已刪除 `{iTopic}`（{n} 個檔）並留墓碑：{Rel(DataRoot, TombstonePath)}");
            r.Say($"   內容在 commit `{(sha.Length > 0 ? sha : "?")}` —— 刪除可以，失聯不行。");
            r.Say("");
            r.Say("📌 對每一張關聯單補上墓碑指標（那一格歸任務寫入端）：");
            r.Say("   " + iTaskCmdHint.Replace("<sha>", sha.Length > 0 ? sha : "<sha>"));
            return r;
        }

        // ⛔ Senate 不讀 Unity 專案（TASK-0390）⇒ 不去找 UCL_Core 根。
    }
}
