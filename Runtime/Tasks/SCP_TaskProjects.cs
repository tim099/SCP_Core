// 區塊職責：任務的**專案**（TASK-0618）—— 比母單（epic）更上一層的分類，例：`Blockside`。
// 物理意義：磁碟上只有一格 frontmatter `project:`（一個值、自由字串；空 ⇒ 那一行不落盤）。
//           「子任務沒標 ⇒ 跟著母單」**只在讀的時候算**（顯示、篩選），⛔ 不寫回子單 ——
//           母單換專案，子單自然跟著走；寫回去就變成兩份要一起改的資料。
// 數值影響：純函式，不碰 IO；寫入端（SCP_TaskOps）、`cmd tasks`、任務頁都問這一支 ⇒ 判準只有一份。
//
// ⚠ 專案名比對**不分大小寫**：已經有 `Blockside` 時寫 `blockside` 由寫入端擋下（指出既有拼法），
//   免得同一個專案分成兩類 —— 而兩類在任何一頁上都會各自看起來是完整的。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SCP.Core.Tasks
{
    public static class SCP_TaskProjects
    {
        /// <summary>篩選用的「沒有專案」（`cmd tasks --arg project=none`）。⛔ 不能拿來當專案名。</summary>
        public const string None = "none";

        public const int MaxLength = 40;

        /// <summary>保留字（篩選成員，同 status 的 all／open）：不可落盤成專案名。</summary>
        static readonly string[] Reserved = { None, "all", "全部", "未分類" };

        /// <summary>專案名合法嗎 ⇒ null 或「為什麼不行」。</summary>
        public static string? Validate(string iName)
        {
            string n = (iName ?? "").Trim();
            if (n.Length == 0) return "專案名是空的（要清掉請用 unset=project）";
            if (n.Length > MaxLength) return $"專案名太長（{n.Length} 字，上限 {MaxLength}）";
            foreach (char ch in n)
                if (char.IsControl(ch) || ch == ',' || ch == '[' || ch == ']' || ch == '#')
                    return $"專案名不能有控制字元或 `,` `[` `]` `#`：`{n}`";
            foreach (string r in Reserved)
                if (string.Equals(n, r, StringComparison.OrdinalIgnoreCase))
                    return $"`{n}` 是篩選用的保留字（{string.Join(" / ", Reserved)}），不能當專案名";
            return null;
        }

        /// <summary>已經有一個只差大小寫的專案 ⇒ 回那個既有拼法；沒有 ⇒ null（一模一樣的拼法不算撞）。</summary>
        public static string? CaseClash(IEnumerable<SCP_TaskEntry> iAll, string iName)
        {
            string n = (iName ?? "").Trim();
            foreach (SCP_TaskEntry e in iAll)
                if (e.project.Length > 0 && !string.Equals(e.project, n, StringComparison.Ordinal)
                    && string.Equals(e.project, n, StringComparison.OrdinalIgnoreCase))
                    return e.project;
            return null;
        }

        /// <summary>
        /// 母單是誰（同任務頁 TASK-0600 的認法）：子單自己的 `epic_id`（`553` 或 `TASK-0553`）優先，
        /// 其次是哪一張單的 `subtask_indices` 列了它。沒有 ⇒ 0。
        /// </summary>
        public static int ParentOf(SCP_TaskEntry e, Dictionary<int, SCP_TaskEntry> iByIndex, Dictionary<int, int> iListedBy)
        {
            var aDigits = new System.Text.StringBuilder();
            foreach (char ch in e.epic_id) if (char.IsDigit(ch)) aDigits.Append(ch);
            if (aDigits.Length > 0 && int.TryParse(aDigits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out int p)
                && p != e.index && iByIndex.ContainsKey(p)) return p;
            return iListedBy.TryGetValue(e.index, out int q) && q != e.index ? q : 0;
        }

        /// <summary>
        /// 每一張單的**有效專案**：自己標了就用自己的；沒標 ⇒ 沿母單往上找第一個有標的（標明是跟著哪一張）。
        /// 回傳 index → (專案或空字串, 跟著哪一張；0 ＝ 自己標的或沒有)。成環的鏈走到重複就停（⛔ 不無限迴圈）。
        /// </summary>
        public static Dictionary<int, (string project, int from)> Effective(IList<SCP_TaskEntry> iAll)
        {
            var aByIndex = new Dictionary<int, SCP_TaskEntry>();
            foreach (SCP_TaskEntry e in iAll) aByIndex[e.index] = e;
            var aListedBy = new Dictionary<int, int>();
            foreach (SCP_TaskEntry e in iAll)
                foreach (int k in e.subtask_indices)
                    if (k != e.index && !aListedBy.ContainsKey(k)) aListedBy[k] = e.index;
            var aOut = new Dictionary<int, (string, int)>();
            foreach (SCP_TaskEntry e in iAll)
            {
                if (e.project.Length > 0) { aOut[e.index] = (e.project, 0); continue; }
                var aSeen = new HashSet<int> { e.index };
                SCP_TaskEntry c = e;
                (string, int) aHit = ("", 0);
                while (true)
                {
                    int p = ParentOf(c, aByIndex, aListedBy);
                    if (p == 0 || !aSeen.Add(p)) break;
                    c = aByIndex[p];
                    if (c.project.Length > 0) { aHit = (c.project, p); break; }
                }
                aOut[e.index] = aHit;
            }
            return aOut;
        }

        /// <summary>每個專案幾張（照有效專案算；空字串 ＝ 未分類）。回傳 專案 → (未關, 總數)，照名字排序。</summary>
        public static SortedDictionary<string, (int open, int total)> Counts(IList<SCP_TaskEntry> iAll,
            Dictionary<int, (string project, int from)> iEffective)
        {
            var aOut = new SortedDictionary<string, (int, int)>(StringComparer.OrdinalIgnoreCase);
            foreach (SCP_TaskEntry e in iAll)
            {
                string p = iEffective.TryGetValue(e.index, out var x) ? x.project : e.project;
                aOut.TryGetValue(p, out (int open, int total) v);
                aOut[p] = (v.open + (e.IsClosed() ? 0 : 1), v.total + 1);
            }
            return aOut;
        }
    }
}
