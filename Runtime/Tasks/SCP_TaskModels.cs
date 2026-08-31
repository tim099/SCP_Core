// 區塊職責：任務單的**資料模型與 wire 詞彙**（enum ＋ 三個 POCO）。
// 物理意義：磁碟格式是 `Tasks/tasks/<index:0000>.md` 的 **frontmatter markdown**，不是 JSON。
//           enum 的**成員名就是 wire format** —— 改成員名＝改磁碟格式，動之前要盤既有單
//           （2026-08-31 讀數：96 張）。
// 數值影響：純資料，不碰 IO。
//
// ⚠ **本型別刻意沒有 JSON 基底類別。** UCL 那邊的 `UCL_TaskEntry` 繼承
//   `UnityJsonSerializable`，而 2026-08-31 實測**沒有任何明確的 JSON 消費端**
//   （grep 只命中宣告本身；後台頁是手繪 `DrawRow`，不走通用 inspector）。
//   ⇒ 這裡不帶那個基底，也因此**不受它的限制**（那個基底會把 bool 寫成 "True"/"False" 字串，
//   所以 UCL 那型別刻意一個 bool 欄位都沒有）。
//   ⊘ 未驗：`UCL_GUILayout` 的反射繪製與 `UCLI_CopyPaste` 這條路我沒排除 ——
//     所以**本檔不去刪 UCL 那邊的基底**，只在自己這側不帶。
//
// ⚠ enum 的 `all` / `open` 是**篩選成員不是狀態** —— 落盤檔帶著它們＝壞檔。
//   解析端要出聲退回（見 SCP_TaskIO），不是靜默接受。
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SCP.Core.Tasks
{
    /// <summary>任務種類。⚠ `all` 是篩選成員，不可落盤。</summary>
    public enum SCP_TaskType
    {
        all,
        feature,
        improvement,
        refactor,
        spike,
        subtask,
        bug,
        epic,
    }

    public enum SCP_TaskPriority
    {
        urgent,
        high,
        normal,
        low,
    }

    /// <summary>傷害形狀（跟 priority 不同軸 —— 折進 priority 會丟資訊）。</summary>
    public enum SCP_TaskSeverity
    {
        none,
        blocking,
        wrong,
        annoying,
    }

    /// <summary>狀態。⚠ `all` / `open` 是篩選成員，不可落盤。</summary>
    public enum SCP_TaskStatus
    {
        all,
        open,
        backlog,
        todo,
        in_progress,
        in_review,
        done,
        cancelled,
    }

    public enum SCP_TaskRole
    {
        dev,
        design,
        pm,
        qa,
        reviewer,
        sound,
        art,
    }

    // ===========================================================
    // 區塊職責：wire 字串 ↔ enum。
    // 物理意義：**認不得的值不靜默取預設** —— 落回預設值時一定出聲，
    //          因為「這張單是 todo」與「這張單的 status 欄壞了所以被當成 todo」
    //          在任何一頁上都長得一模一樣。
    // 數值影響：純轉換。iWarn 為 null ＝ 呼叫端明示不要那個聲音（例：純計數的掃描）。
    // ===========================================================
    public static class SCP_TaskWire
    {
        public static bool TryParse<T>(string iWire, out T oValue) where T : struct, Enum
            => Enum.TryParse((iWire ?? "").Trim(), ignoreCase: false, out oValue);

        public static T ParseOr<T>(string iWire, T iFallback, string iContext,
                                   Action<string>? iWarn) where T : struct, Enum
        {
            if (TryParse(iWire, out T aValue)) return aValue;
            iWarn?.Invoke($"[Task] {iContext}: '{iWire}' 不是合法的 {typeof(T).Name}"
                + $"（{string.Join("|", Enum.GetNames(typeof(T)))}）—— 落回 `{iFallback}`，去修單檔 frontmatter");
            return iFallback;
        }
    }

    public sealed class SCP_TaskParticipant
    {
        public string persona = "";
        public SCP_TaskRole role = SCP_TaskRole.dev;
        public string assigned_at = "";
    }

    public sealed class SCP_TaskComment
    {
        public int id;
        public string persona = "";
        public string at = "";
        public string body = "";
    }

    public sealed class SCP_TaskEntry
    {
        public int index;
        public SCP_TaskType type = SCP_TaskType.feature;
        public SCP_TaskPriority priority = SCP_TaskPriority.normal;
        public SCP_TaskSeverity severity = SCP_TaskSeverity.none;
        public SCP_TaskStatus status = SCP_TaskStatus.todo;
        public string title = "";
        public string milestone = "";
        public string epic_id = "";
        public string reporter = "";
        public string resolution_note = "";
        public string created_at = "";
        public string updated_at = "";
        public string closed_at = "";
        public string last_wrapup_at = "";
        public string memory_topic = "";
        public string memory_archived_commit = "";
        public List<SCP_TaskParticipant> participants = new List<SCP_TaskParticipant>();
        public List<int> blocked_by = new List<int>();
        public List<int> blocks = new List<int>();
        public List<int> related_to = new List<int>();
        public List<int> subtask_indices = new List<int>();
        public List<string> tags = new List<string>();
        public List<string> commit_shas = new List<string>();
        public List<SCP_TaskComment> comments = new List<SCP_TaskComment>();

        /// <summary>單號。⚠ 補零只為排序好看；**權威是 frontmatter 的整數 index**，不是檔名。</summary>
        public string Id => "TASK-" + index.ToString("0000", CultureInfo.InvariantCulture);

        /// <summary>已關（done / cancelled）—— 「關了」與「還開著」是收工閘唯一在意的二分。</summary>
        public bool IsClosed() => status == SCP_TaskStatus.done || status == SCP_TaskStatus.cancelled;

        /// <summary>這個人在本單的角色（可多個 —— 同一人可以既是 dev 又是 qa）。</summary>
        public List<SCP_TaskRole> RolesOf(string iPersona)
        {
            var aOut = new List<SCP_TaskRole>();
            foreach (SCP_TaskParticipant aP in participants)
                if (string.Equals(aP.persona, iPersona, StringComparison.Ordinal)) aOut.Add(aP.role);
            return aOut;
        }

        /// <summary>這個人是不是參與者。</summary>
        public bool HasParticipant(string iPersona)
        {
            foreach (SCP_TaskParticipant aP in participants)
                if (string.Equals(aP.persona, iPersona, StringComparison.Ordinal)) return true;
            return false;
        }
    }
}
