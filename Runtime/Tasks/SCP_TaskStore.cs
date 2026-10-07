// 區塊職責：任務單的**寫入端** —— 配號、單檔整檔重寫、依賴雙向寫入、驗收格勾選的字串層（TASK-0349）。
// 物理意義：`Tasks/tasks/<index>.md` 的唯一寫入面。**只在 Senate Server 裡被呼叫**（`task-write`）——
//           CLI（`senate cmd task`）、後台頁、晚安 skip 一律委派到那裡。
//           ⇒ 同一時間只有一個 process 在寫；本檔的鎖是那個前提的**第二道**，不是唯一一道。
//
// ⚠⚠ 併發安全來自「兩把鎖 ＋ 只有三個寫入入口」：
//   · 入口：`Mutate`（改一張既有的單）／`Create`（開一張新的單）／`Link`・`Unlink`（兩張一起改）。
//     `Save` 是 **private** ⇒ 呼叫端在型別上拿不到「不在鎖內的 entry」，也拿不到 `Save`。
//   · 鎖一：process 內（每個資料根一把 `Monitor`，Server 是多執行緒的 —— Coding_Standards §5.1）。
//   · 鎖二：跨 process（`SCP_FileLock`，鎖 `_index.txt` 旁邊那顆 `.lock`）——
//     🩸 UCL 那支只有鎖一，檔頭自己寫著「python／另一個 Editor 實例同時寫，本鎖答不出來」。
//     寫入端搬到 Server 之後理論上只剩一個 process，而「理論上」正是會漂的那種前提 ⇒ 這一格用機制補。
//   · 🩸 UCL 的 `Link`／`Unlink` 是**鎖外**寫兩次（`AssertHoldsRmwLock` 每次都該叫）—— 本檔收進鎖內。
//
// ⚠ 配號（`Create`）的形狀換了，照酒館 `SCP_TavernWriter` 那條走過的路：
//   舊：先把 `_index.txt` +1 寫回、再建構、再 Save ⇒ 建構丟例外（參數不合法）時**號碼已經被吃掉**
//       （🩸 2026-09-30 basecamp 量到：兩次 `priority=medium` 被擋，0351／0352 從此不存在）。
//   新：號碼＝max(計數檔, 磁碟最大檔名)+1 → 建構 → **原子建檔**（`FileMode.CreateNew`）→ 建成了才寫回計數檔。
//       ⇒ 建構失敗時一個位元組都沒寫、計數檔也沒動 ⇒ **跳號在結構上不可能**；
//       撞檔（有人繞過入口手建）⇒ 往下一號重試並回報次數，⛔ 不覆寫別人的檔。
//   ⇒ 計數檔降級成**快取**：事實是磁碟上的檔名（與酒館 `_seq.txt` 同一個判準）。
//
// ⚠ 落盤格式**逐位元組固定**（鍵序、`id:` 行、severity=none 不落行、`_(未填)_`、
//   留言行首 `#` 逃脫、UTF-8 無 BOM、`\n`）—— 既有 358 張單由舊寫入端寫出，新寫入端重寫任何一張都**不該有結構 diff**。
//   ⛔ 改格式要同時改 `SCP_TaskIO`（讀取端），而那是另一張單的事。
// 數值影響：純檔案 IO；一次 Mutate ＝ 一次讀 ＋ 一次寫（temp → replace）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也編這份，雖然它不再呼叫寫入面）。
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Io;
using SCP.Core.Paths;

namespace SCP.Core.Tasks
{
    // ===========================================================
    // 區塊職責：一次寫入要落盤的東西 —— 時間線那一行 ＋（選填）兩個內文區塊。
    // 物理意義：`criteria`／`description` 給空字串＝**沿用磁碟那一份**（跟留言與結單說明同一族的沿用規則）
    //   ⇒ 「不動內文」與「把內文寫成空的」在參數上同形，而前者是常態 ⇒ 用三個具名建構子說出意圖。
    // 邊界：`Skip` ＝ 這次不寫（鎖內重判不成立的出口）。判準是 `activity` 是否為空。
    // ===========================================================
    public struct SCP_TaskWrite
    {
        public string activity;      // 時間線那一行；空 ⇒ 這次不寫
        public string criteria;      // 空 ⇒ 沿用磁碟那一份
        public string description;   // 空 ⇒ 沿用磁碟那一份

        /// <summary>這次不寫（鎖內重判不成立）。⛔ 不是失敗，是刻意零寫入。</summary>
        public static SCP_TaskWrite Skip => default;
        public static SCP_TaskWrite Line(string iActivity) => new SCP_TaskWrite { activity = iActivity };
        public static SCP_TaskWrite Body(string iActivity, string iCriteria, string iDescription)
            => new SCP_TaskWrite { activity = iActivity, criteria = iCriteria, description = iDescription };

        public bool WillWrite => !string.IsNullOrEmpty(activity);
    }

    /// <summary><see cref="SCP_TaskStore.Create"/> 的結果。</summary>
    public sealed class SCP_TaskCreateResult
    {
        /// <summary>真正落盤的 index；&lt;= 0 ＝ 沒有落盤（建構回 null）。</summary>
        public int Index;
        /// <summary>撞檔後往下一號重試了幾次。⚠ 單一寫入端的世界裡它該永遠是 0。</summary>
        public int HealAttempts;
        /// <summary>計數檔落後於磁碟時拉齊的讀數（空＝沒有落後）。</summary>
        public string CounterNote = "";
    }

    public static class SCP_TaskStore
    {
        /// <summary>in_progress 超過這個天數沒動 ⇒ stale。與 UCL 同一個數字。</summary>
        public const int STALE_DAYS = 14;

        /// <summary>撞檔重試上限 —— 與酒館寫入端同一個數字（`SCP_TavernWriter.MaxHealRetries`）。</summary>
        public const int MaxHealRetries = 3;

        // ── 鎖 ──────────────────────────────────────────────────────
        // 鎖一：每個資料根一把（Server 同時服務多個專案的資料根時，彼此不互等）。
        static readonly ConcurrentDictionary<string, object> s_Locks =
            new ConcurrentDictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        // 本執行緒已持有哪些根 —— 讓 `Link` 裡呼叫 `Save` 兩次、`Mutate` 的 lambda 裡讀別的單時**不重拿**檔案鎖
        //   （`SCP_FileLock` 不可重入：同一顆 process 第二次開 `FileShare.None` 會等到逾時）。
        [ThreadStatic] static HashSet<string>? s_Held;

        static string Key(SCP_DataRoot iRoot) => iRoot.Value.Replace('\\', '/').TrimEnd('/');

        /// <summary>進入寫入臨界區。<c>using</c> 進出；同一執行緒重入是 no-op。</summary>
        sealed class Scope : IDisposable
        {
            readonly object? m_Monitor;
            readonly SCP_FileLock? m_File;
            readonly string m_Key;

            public Scope(SCP_DataRoot iRoot)
            {
                m_Key = Key(iRoot);
                s_Held ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (s_Held.Contains(m_Key)) return;             // 重入：外層已經拿著兩把鎖
                m_Monitor = s_Locks.GetOrAdd(m_Key, _ => new object());
                System.Threading.Monitor.Enter(m_Monitor);
                try
                {
                    Directory.CreateDirectory(SCP_TaskIO.TasksDir(iRoot));
                    m_File = SCP_FileLock.Acquire(SCP_TaskIO.IndexPath(iRoot));
                }
                catch
                {
                    System.Threading.Monitor.Exit(m_Monitor);
                    throw;
                }
                s_Held.Add(m_Key);
            }

            public void Dispose()
            {
                if (m_Monitor == null) return;                  // 重入那一層不放
                s_Held?.Remove(m_Key);
                m_File?.Dispose();
                System.Threading.Monitor.Exit(m_Monitor);
            }
        }

        static bool Holds(SCP_DataRoot iRoot) => s_Held != null && s_Held.Contains(Key(iRoot));

        // ===========================================================
        // 區塊職責：**唯一**帶鎖的 read-modify-write 入口（改一張既有的單）。
        // 物理意義：跨度起於**鎖內的 READ**（`Find`）——呼叫端鎖外撈的那份只算「提示」。
        // 邊界：
        //   · `iMutator` 回 `Skip` ⇒ 不寫（判定在鎖內重做的出口）；`oFound` 分辨「單不在」與「判定不寫」。
        //   · ⛔ `iMutator` 裡**不得**做會等別的 process 的事（發文、跑 python）—— 那會在持鎖時卡住所有寫入。
        // ===========================================================
        public static bool Mutate(SCP_DataRoot iRoot, int iIndex, Func<SCP_TaskEntry, SCP_TaskWrite> iMutator, out bool oFound)
        {
            oFound = false;
            if (iMutator == null) return false;
            using (new Scope(iRoot))
            {
                SCP_TaskEntry? e = SCP_TaskIO.Find(iRoot, iIndex);
                if (e == null) return false;
                oFound = true;
                SCP_TaskWrite aWrite = iMutator(e);
                if (!aWrite.WillWrite) return false;
                Save(iRoot, e, aWrite.criteria ?? "", aWrite.description ?? "", aWrite.activity);
                return true;
            }
        }

        public static bool Mutate(SCP_DataRoot iRoot, int iIndex, Func<SCP_TaskEntry, SCP_TaskWrite> iMutator)
            => Mutate(iRoot, iIndex, iMutator, out _);

        // ===========================================================
        // 區塊職責：開一張新的單 —— 配號、建構、原子建檔在同一段臨界區。
        // 物理意義：見檔頭「配號的形狀換了」。`iBuild` 收號碼（bug 單的驗收骨架要寫 `Fixes TASK-<n>`）。
        // 邊界：
        //   · `iBuild` 丟例外 ⇒ 原樣往上丟，**一個位元組都沒寫、計數檔沒動**（⇒ 號碼沒被吃掉）。
        //   · `iBuild` 回 null 單 ⇒ `Index=-1`、零寫入。
        //   · 撞檔 ⇒ 往下一號重建（`iBuild` 會以新號碼再被呼叫一次 ⇒ 它必須是純的）。
        // ===========================================================
        public static SCP_TaskCreateResult Create(SCP_DataRoot iRoot,
            Func<int, (SCP_TaskEntry? entry, SCP_TaskWrite write)> iBuild)
        {
            var aOut = new SCP_TaskCreateResult { Index = -1 };
            if (iBuild == null) return aOut;
            using (new Scope(iRoot))
            {
                int aCounter = SCP_TaskIO.ReadCurrentIndex(iRoot);
                int aDiskMax = MaxIndexByFileName(iRoot);
                if (aDiskMax > aCounter)
                    aOut.CounterNote = $"計數檔={aCounter} 落後於磁碟最大檔名={aDiskMax} ⇒ 從 {aDiskMax + 1} 起配"
                        + "（有人繞過寫入端建了單，或上一次建檔後沒寫回計數檔）";
                int aNext = Math.Max(aCounter, aDiskMax) + 1;
                for (int aTry = 0; aTry <= MaxHealRetries; ++aTry)
                {
                    var (e, aWrite) = iBuild(aNext);
                    if (e == null) return aOut;
                    e.index = aNext;                         // 號碼由本入口決定，不信呼叫端填的那格
                    string aText = Render(e, aWrite.criteria ?? "", aWrite.description ?? "",
                        string.IsNullOrEmpty(aWrite.activity) ? new List<string>() : new List<string> { "- " + aWrite.activity });
                    if (SCP_AtomicFile.TryCreateNew(SCP_TaskIO.TaskPath(iRoot, aNext), aText))
                    {
                        // ⚠ 建成了才寫回計數檔 —— 它是快取；寫失敗不影響事實（下一次從磁碟檔名拉齊）。
                        try { File.WriteAllText(SCP_TaskIO.IndexPath(iRoot), aNext.ToString(CultureInfo.InvariantCulture), new UTF8Encoding(false)); }
                        catch (IOException ex) { aOut.CounterNote += (aOut.CounterNote.Length > 0 ? "；" : "") + "計數檔沒寫回（" + ex.Message + "）—— 下一次從檔名拉齊"; }
                        aOut.Index = aNext;
                        aOut.HealAttempts = aTry;
                        return aOut;
                    }
                    ++aNext;                                  // 撞檔：那個號碼有人了 ⇒ 下一號
                }
                throw new IOException($"[Task] 開單連續撞檔 {MaxHealRetries + 1} 次（最後試到 {aNext - 1}）——"
                    + " 單一寫入端的世界裡這不該發生：有東西正在繞過寫入端大量建檔。⛔ 本次一張都沒寫。");
            }
        }

        /// <summary>磁碟上最大的單號（**看檔名**，不解析內容 —— 撞不撞號只看檔名有沒有人佔）。</summary>
        public static int MaxIndexByFileName(SCP_DataRoot iRoot)
        {
            string aDir = SCP_TaskIO.TasksDir(iRoot);
            if (!Directory.Exists(aDir)) return 0;
            int aMax = 0;
            foreach (string aPath in Directory.GetFiles(aDir, "*.md"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(aPath), NumberStyles.Integer,
                        CultureInfo.InvariantCulture, out int v) && v > aMax) aMax = v;
            return aMax;
        }

        // ===========================================================
        // 區塊職責：寫一張既有的單（整檔重寫，但歷史／留言／內文沿用磁碟）。⛔ private：只經由入口呼叫。
        // ===========================================================
        static void Save(SCP_DataRoot iRoot, SCP_TaskEntry e, string iCriteria, string iDescription, string iActivityLine)
        {
            if (!Holds(iRoot))
                throw new InvalidOperationException($"[Task] Save(index={e.index}) 在鎖外被呼叫 —— "
                    + "本檔的寫入只有 Mutate／Create／Link／Unlink 四個入口，這一行代表有人新加了一條繞過它們的路徑。");
            string aPath = SCP_TaskIO.TaskPath(iRoot, e.index);
            var aTimeline = new List<string>();
            if (File.Exists(aPath))
            {
                aTimeline = ReadTimeline(aPath);
                // 留言／內文／結單說明：呼叫端沒給就沿用磁碟那一份 ——「沒給」＝沿用，⛔ 不是清空。
                if (e.comments.Count == 0) e.comments = SCP_TaskIO.ReadComments(aPath);
                if (string.IsNullOrEmpty(iCriteria)) iCriteria = ReadSection(aPath, "## 驗收標準");
                if (string.IsNullOrEmpty(iDescription)) iDescription = ReadSection(aPath, "## 任務描述");
                // 🩸 UCL TASK-0158：`resolution_note` 只有寫入端 —— 讀取層只解析 frontmatter，漏撈就會被整段刪掉。
                if (string.IsNullOrEmpty(e.resolution_note)) e.resolution_note = ReadSection(aPath, "## 結單說明");
            }
            if (!string.IsNullOrEmpty(iActivityLine)) aTimeline.Add("- " + iActivityLine);

            string aText = Render(e, iCriteria, iDescription, aTimeline);
            string aTmp = aPath + ".tmp";
            File.WriteAllText(aTmp, aText, new UTF8Encoding(false));
            SCP_TextFile.ReplaceOrMove(aTmp, aPath);
        }

        // ===========================================================
        // 區塊職責：把一張單排成磁碟格式（純函式）。⚠ 逐位元組照 UCL `Save`，見檔頭。
        // ===========================================================
        public static string Render(SCP_TaskEntry e, string iCriteria, string iDescription, List<string> iTimeline)
        {
            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append($"index: {e.index}\n");
            sb.Append($"id: {e.Id}\n");
            sb.Append($"type: {e.type}\n");
            sb.Append($"priority: {e.priority}\n");
            // severity=none 不落行 —— 缺席即 none（非缺陷單常態），既有單零 diff
            if (e.severity != SCP_TaskSeverity.none) sb.Append($"severity: {e.severity}\n");
            sb.Append($"status: {e.status}\n");
            sb.Append($"title: {OneLine(e.title)}\n");
            sb.Append($"reporter: {OneLine(e.reporter)}\n");
            sb.Append("participants:\n");
            foreach (SCP_TaskParticipant p in e.participants)
            {
                sb.Append($"  - persona: {OneLine(p.persona)}\n");
                sb.Append($"    role: {p.role}\n");
                sb.Append($"    assigned_at: {OneLine(p.assigned_at)}\n");
            }
            sb.Append($"milestone: {OneLine(e.milestone)}\n");
            sb.Append($"epic_id: {OneLine(e.epic_id)}\n");
            sb.Append($"blocked_by: {IntList(e.blocked_by)}\n");
            sb.Append($"blocks: {IntList(e.blocks)}\n");
            sb.Append($"related_to: {IntList(e.related_to)}\n");
            sb.Append($"subtask_indices: {IntList(e.subtask_indices)}\n");
            sb.Append($"tags: {StrList(e.tags)}\n");
            sb.Append($"commit_shas: {StrList(e.commit_shas)}\n");
            sb.Append($"created_at: {e.created_at}\n");
            sb.Append($"updated_at: {e.updated_at}\n");
            sb.Append($"closed_at: {e.closed_at}\n");
            sb.Append($"last_wrapup_at: {e.last_wrapup_at}\n");
            sb.Append($"memory_topic: {OneLine(e.memory_topic)}\n");
            sb.Append($"memory_archived_commit: {OneLine(e.memory_archived_commit)}\n");
            sb.Append("---\n\n");

            sb.Append($"# {e.Id} — {e.title}\n\n");
            sb.Append($"> `{e.type}` / "
                + (e.severity == SCP_TaskSeverity.none ? "" : $"`{e.severity}` / ")
                + $"`{e.priority}` / `{e.status}`　開單：{Nz2(e.reporter)}");
            if (e.participants.Count > 0)
            {
                sb.Append("　參與：");
                for (int i = 0; i < e.participants.Count; i++)
                {
                    if (i > 0) sb.Append("、");
                    sb.Append($"{e.participants[i].persona}({e.participants[i].role})");
                }
            }
            else
            {
                // 沒有人被指派時**明說** —— 空白看起來像「還沒填」，而這一格的意思是「現在沒有人在做這件事」
                sb.Append("　⚠ **尚無參與者**（沒有人在做這件事）");
            }
            sb.Append("\n\n");
            sb.Append("## 驗收標準\n\n").Append(Nz(iCriteria)).Append("\n\n");
            sb.Append("## 任務描述\n\n").Append(Nz(iDescription)).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(e.resolution_note))
                sb.Append("## 結單說明\n\n").Append(e.resolution_note).Append("\n\n");

            sb.Append("## 留言\n\n");
            if (e.comments.Count == 0)
            {
                sb.Append("_(還沒有人留言)_\n\n");
            }
            else
            {
                foreach (SCP_TaskComment c in e.comments)
                {
                    sb.Append(SCP_TaskIO.CommentHeader(c)).Append('\n');
                    foreach (string aLine in (c.body ?? "").TrimEnd().Replace("\r", "").Split('\n'))
                        sb.Append(SCP_TaskIO.EscapeCommentLine(aLine)).Append('\n');
                    sb.Append('\n');
                }
            }
            sb.Append("## 活動與討論時間線\n\n");
            foreach (string h in iTimeline) sb.Append(h).Append('\n');
            return sb.ToString();
        }

        // ===========================================================
        // 區塊職責：依賴關係的**雙向**寫入 —— 兩張單在同一段臨界區裡各寫一次。
        // 🩸 單向寫入是**靜默錯**：從 A 看得到「我被 B 卡住」，從 B 完全看不出「我卡住了誰」。
        // 數值影響：回傳是否真的有變動（冪等：關聯本來就在 ⇒ false、零寫入）。
        // ===========================================================
        public static bool Link(SCP_DataRoot iRoot, int iIndex, int iTarget, string iKind, string iActor, out string oError)
            => Relate(iRoot, iIndex, iTarget, iKind, iActor, iRemove: false, out oError);

        /// <summary>解除關聯 —— <see cref="Link"/> 的雙向對稱反操作；兩張單各留一筆 `unlink`。</summary>
        public static bool Unlink(SCP_DataRoot iRoot, int iIndex, int iTarget, string iKind, string iActor, out string oError)
            => Relate(iRoot, iIndex, iTarget, iKind, iActor, iRemove: true, out oError);

        static bool Relate(SCP_DataRoot iRoot, int iIndex, int iTarget, string iKind, string iActor, bool iRemove, out string oError)
        {
            oError = "";
            if (iIndex == iTarget) { oError = iRemove ? "不能對自己解除關聯" : "不能把一張單連到自己"; return false; }
            using (new Scope(iRoot))
            {
                SCP_TaskEntry? a = SCP_TaskIO.Find(iRoot, iIndex);
                SCP_TaskEntry? b = SCP_TaskIO.Find(iRoot, iTarget);
                if (a == null) { oError = $"TASK-{iIndex:0000} 不存在"; return false; }
                if (b == null) { oError = $"TASK-{iTarget:0000} 不存在"; return false; }

                string aNow = NowUtc();
                bool aChanged = false;
                string aLineA, aLineB;
                string k = (iKind ?? "").Trim().ToLowerInvariant();
                switch (k)
                {
                    case "blocked_by":
                        aChanged |= iRemove ? a.blocked_by.Remove(iTarget) : AddOnce(a.blocked_by, iTarget);
                        aChanged |= iRemove ? b.blocks.Remove(iIndex) : AddOnce(b.blocks, iIndex);
                        aLineA = iRemove ? $"`unlink`　{iActor} 解除「被 {b.Id} 阻塞」" : $"`link`　{iActor} 標記被 {b.Id} 阻塞";
                        aLineB = iRemove ? $"`unlink`　{iActor} 解除「它阻塞了 {a.Id}」" : $"`link`　{iActor} 標記它阻塞了 {a.Id}";
                        break;
                    case "blocks":
                        aChanged |= iRemove ? a.blocks.Remove(iTarget) : AddOnce(a.blocks, iTarget);
                        aChanged |= iRemove ? b.blocked_by.Remove(iIndex) : AddOnce(b.blocked_by, iIndex);
                        aLineA = iRemove ? $"`unlink`　{iActor} 解除「它阻塞了 {b.Id}」" : $"`link`　{iActor} 標記它阻塞了 {b.Id}";
                        aLineB = iRemove ? $"`unlink`　{iActor} 解除「被 {a.Id} 阻塞」" : $"`link`　{iActor} 標記被 {a.Id} 阻塞";
                        break;
                    // 父子關係：**兩個欄位一起寫才叫一個關係**（子的 epic_id 指向父、父的 subtask_indices 收子）。
                    case "subtask_of":
                        if (iRemove) { if (a.epic_id == b.Id) { a.epic_id = ""; aChanged = true; } }
                        else if (a.epic_id != b.Id) { a.epic_id = b.Id; aChanged = true; }
                        aChanged |= iRemove ? b.subtask_indices.Remove(iIndex) : AddOnce(b.subtask_indices, iIndex);
                        aLineA = iRemove ? $"`unlink`　{iActor} 解除「它是 {b.Id} 的子任務」（epic_id 清空）"
                                         : $"`link`　{iActor} 標記它是 {b.Id} 的子任務（epic_id={b.Id}）";
                        aLineB = iRemove ? $"`unlink`　{iActor} 移出子任務 {a.Id}" : $"`link`　{iActor} 收 {a.Id} 為子任務";
                        break;
                    case "has_subtask":
                        aChanged |= iRemove ? a.subtask_indices.Remove(iTarget) : AddOnce(a.subtask_indices, iTarget);
                        if (iRemove) { if (b.epic_id == a.Id) { b.epic_id = ""; aChanged = true; } }
                        else if (b.epic_id != a.Id) { b.epic_id = a.Id; aChanged = true; }
                        aLineA = iRemove ? $"`unlink`　{iActor} 移出子任務 {b.Id}" : $"`link`　{iActor} 收 {b.Id} 為子任務";
                        aLineB = iRemove ? $"`unlink`　{iActor} 解除「它是 {a.Id} 的子任務」（epic_id 清空）"
                                         : $"`link`　{iActor} 標記它是 {a.Id} 的子任務（epic_id={a.Id}）";
                        break;
                    case "related_to":
                        aChanged |= iRemove ? a.related_to.Remove(iTarget) : AddOnce(a.related_to, iTarget);
                        aChanged |= iRemove ? b.related_to.Remove(iIndex) : AddOnce(b.related_to, iIndex);
                        aLineA = iRemove ? $"`unlink`　{iActor} 解除與 {b.Id} 的關聯" : $"`link`　{iActor} 關聯 {b.Id}";
                        aLineB = iRemove ? $"`unlink`　{iActor} 解除與 {a.Id} 的關聯" : $"`link`　{iActor} 關聯 {a.Id}";
                        break;
                    default:
                        oError = $"認不得的關聯種類 '{iKind}'（blocked_by|blocks|subtask_of|has_subtask|related_to）";
                        return false;
                }
                if (!aChanged) return false;
                a.updated_at = aNow; b.updated_at = aNow;
                Save(iRoot, a, "", "", $"{aNow}　{aLineA}");
                Save(iRoot, b, "", "", $"{aNow}　{aLineB}");
                return true;
            }
        }

        // ── 查詢（寫入端的閘與回報用；讀取層沒有的那幾支）────────────

        /// <summary>
        /// 還沒關掉的 blocker（UCL 同一個措辭）。⚠ 指到**不存在**的單也算未解 —— 「查不到」不等於「已經解決」。
        /// </summary>
        public static List<string> OpenBlockers(SCP_DataRoot iRoot, SCP_TaskEntry? e)
        {
            var aOut = new List<string>();
            if (e == null) return aOut;
            foreach (int i in e.blocked_by)
            {
                SCP_TaskEntry? b = SCP_TaskIO.Find(iRoot, i);
                if (b == null) { aOut.Add($"TASK-{i:0000}（**單子不存在** —— 查不到不等於已解決）"); continue; }
                if (!b.IsClosed()) aOut.Add($"{b.Id} `{b.status}` {b.title}");
            }
            return aOut;
        }

        /// <summary>子任務進度：幾張、幾張已關、剩哪些、哪些號碼查不到。</summary>
        public static void SubtaskProgress(SCP_DataRoot iRoot, SCP_TaskEntry? e,
            out int oTotal, out int oClosed, out List<string> oOpenList, out List<int> oMissing)
        {
            oTotal = 0; oClosed = 0; oOpenList = new List<string>(); oMissing = new List<int>();
            if (e == null) return;
            foreach (int i in e.subtask_indices)
            {
                oTotal++;
                SCP_TaskEntry? c = SCP_TaskIO.Find(iRoot, i);
                if (c == null) { oMissing.Add(i); continue; }
                if (c.IsClosed()) oClosed++;
                else oOpenList.Add($"{c.Id} `{c.status}` {c.title}");
            }
        }

        /// <summary>open / stale / blocked 讀數（後台頁與清單共用）。時戳壞掉的不算 stale，另外報。</summary>
        public static void CountStats(SCP_DataRoot iRoot, List<SCP_TaskEntry> iAll,
            out int oOpen, out int oStale, out int oBroken, out int oBlocked)
        {
            oOpen = 0; oStale = 0; oBroken = 0; oBlocked = 0;
            DateTime aNow = DateTime.UtcNow;
            foreach (SCP_TaskEntry e in iAll)
            {
                if (e.IsClosed()) continue;
                oOpen++;
                if (OpenBlockers(iRoot, e).Count > 0) oBlocked++;
                if (e.status != SCP_TaskStatus.in_progress) continue;
                int aDays = e.DaysSinceUpdate(aNow);
                if (aDays < 0) oBroken++;
                else if (aDays >= STALE_DAYS) oStale++;
            }
        }

        /// <summary>QA 閘：單上有 QA 而動手結單的人不是那位 QA 且沒附驗收紀錄 ⇒ 擋。回 null＝可以過。</summary>
        public static string? QaGateBlocked(SCP_TaskEntry e, string iActor, string iQaNote)
        {
            List<string> aQa = e.QaPersonas();
            if (aQa.Count == 0) return null;
            foreach (string q in aQa)
                if (string.Equals(q, iActor, StringComparison.OrdinalIgnoreCase)) return null;
            if (!string.IsNullOrWhiteSpace(iQaNote)) return null;
            return $"這張單指名的 QA 是 {string.Join(" / ", aQa)}，而動手的是 {iActor}。"
                 + " 要嘛由那位 QA 跑 resolve，要嘛帶 `--arg qa_note=<驗收紀錄>`（誰驗的、驗了什麼讀數）。";
        }

        /// <summary>把 `TASK-0008` / `8` / `0008` 都收成整數；認不出回 -1（不猜）。</summary>
        public static int ParseTaskRef(string iRaw)
        {
            string s = (iRaw ?? "").Trim();
            if (s.Length == 0) return -1;
            if (s.StartsWith("TASK-", StringComparison.OrdinalIgnoreCase)) s = s.Substring(5);
            return int.TryParse(s.TrimStart('0').Length == 0 ? "0" : s,
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
        }

        /// <summary>下一則留言的編號（現有最大 +1）。⚠ 必須對**鎖內重讀**的那一份算，否則兩則同時留言會撞號。</summary>
        public static int NextCommentId(SCP_TaskEntry e)
        {
            int aMax = 0;
            foreach (SCP_TaskComment c in e.comments) if (c.id > aMax) aMax = c.id;
            return aMax + 1;
        }

        public static string NowUtc() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        // ── 驗收標準的勾選格（唯一認得 `- [ ]` 形狀的地方）──────────
        // ⚠ 序號口徑：**未勾清單的 1-based 序號**，不是檔案行號（與見叢 `done_index` 同一個口徑）。
        // ⚠ 勾是**簽名行為**：勾完的行尾接 `　✅ <persona> <yyyy-MM-dd>`（署名只有這一份可見文字）。

        public static string ReadCriteria(SCP_DataRoot iRoot, int iIndex) => ReadSection(SCP_TaskIO.TaskPath(iRoot, iIndex), "## 驗收標準");
        public static string ReadDescription(SCP_DataRoot iRoot, int iIndex) => ReadSection(SCP_TaskIO.TaskPath(iRoot, iIndex), "## 任務描述");

        /// <summary>行首是勾選格嗎？只認**行首**（縮排的 `- [ ]` 是上一格的續行）。</summary>
        static int CriteriaBoxWidth(string? iLine, out bool oChecked)
        {
            oChecked = false;
            if (iLine == null) return 0;
            if (iLine.StartsWith("- [ ] ", StringComparison.Ordinal)) return 6;
            if (iLine.StartsWith("- [x] ", StringComparison.Ordinal)) { oChecked = true; return 6; }
            if (iLine.StartsWith("- [X] ", StringComparison.Ordinal)) { oChecked = true; return 6; }
            return 0;
        }

        public static List<string> ListUncheckedCriteria(string? iCriteria)
        {
            var aOut = new List<string>();
            foreach (string aLine in (iCriteria ?? "").Replace("\r", "").Split('\n'))
                if (CriteriaBoxWidth(aLine, out bool aChecked) > 0 && !aChecked) aOut.Add(aLine.Substring(6).Trim());
            return aOut;
        }

        public static List<string> ListCheckedCriteria(string? iCriteria)
        {
            var aOut = new List<string>();
            foreach (string aLine in (iCriteria ?? "").Replace("\r", "").Split('\n'))
                if (CriteriaBoxWidth(aLine, out bool aChecked) > 0 && aChecked) aOut.Add(aLine.Substring(6).Trim());
            return aOut;
        }

        /// <summary>`[signer:&lt;persona&gt;]` 指定簽名人；沒有回 null。⛔ 不認裸 `@persona`（那是「提到誰」不是「誰簽」）。</summary>
        public static string? CriteriaSigner(string? iCriteriaLine)
        {
            if (string.IsNullOrEmpty(iCriteriaLine)) return null;
            Match aMatch = Regex.Match(iCriteriaLine, @"\[signer:\s*([A-Za-z0-9_\-\.]+)\s*\]", RegexOptions.IgnoreCase);
            return aMatch.Success ? aMatch.Groups[1].Value : null;
        }

        /// <summary>
        /// 勾未勾清單的第 <paramref name="iOneBased"/> 格並簽名。成功回那一行內容、<paramref name="ioCriteria"/> 換成新的整段；
        /// 越界回 null 且一個字元都不動。⚠ 多筆要**由大到小**呼叫（勾完未勾清單會縮短）。
        /// </summary>
        public static string? CheckOffCriteria(ref string ioCriteria, int iOneBased, string iActor, DateTime iNowLocal)
        {
            if (iOneBased < 1) return null;
            var aLines = new List<string>((ioCriteria ?? "").Replace("\r", "").Split('\n'));
            int aSeen = 0;
            for (int i = 0; i < aLines.Count; i++)
            {
                if (CriteriaBoxWidth(aLines[i], out bool aChecked) == 0 || aChecked) continue;
                if (++aSeen != iOneBased) continue;
                string aBody = aLines[i].Substring(6);
                aLines[i] = "- [x] " + aBody.TrimEnd() + $"　✅ {Nz2(iActor)} {iNowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}";
                ioCriteria = string.Join("\n", aLines);
                return aBody.Trim();
            }
            return null;
        }

        // ── 小工具 ────────────────────────────────────────────────

        /// <summary>時間線（`## 活動與討論時間線` 底下每一行 `- `，已 Trim）。整檔重寫時它是**不可重建**的那一段。</summary>
        public static List<string> ReadTimeline(string iPath)
        {
            var aOut = new List<string>();
            if (!File.Exists(iPath)) return aOut;
            bool aIn = false;
            foreach (string aLine in File.ReadAllLines(iPath, Encoding.UTF8))
            {
                if (SCP_TaskIO.IsHeading(aLine, "## 活動與討論時間線")) { aIn = true; continue; }
                if (aIn && SCP_TaskIO.IsSectionHeading(aLine)) aIn = false;
                if (aIn && aLine.TrimStart().StartsWith("- ", StringComparison.Ordinal)) aOut.Add(aLine.Trim());
            }
            return aOut;
        }

        /// <summary>讀單檔某個頂層區塊（`_(未填)_` 回空字串）。邊界只認 SECTION_HEADINGS 的**整行**（🩸 BUG-7：認任何 `## ` 會截斷內文小節；TASK-0349：認前綴也會）。</summary>
        public static string ReadSection(string iPath, string iHeading)
        {
            try
            {
                if (!File.Exists(iPath)) return "";
                var sb = new StringBuilder();
                bool aIn = false;
                foreach (string aLine in File.ReadAllLines(iPath, Encoding.UTF8))
                {
                    if (SCP_TaskIO.IsHeading(aLine, iHeading)) { aIn = true; continue; }
                    if (aIn && SCP_TaskIO.IsSectionHeading(aLine)) break;
                    if (aIn) sb.Append(aLine).Append('\n');
                }
                string s = sb.ToString().Trim();
                return s == "_(未填)_" ? "" : s;
            }
            catch (IOException) { return ""; }
        }

        static bool AddOnce(List<int> ioList, int iValue)
        {
            if (ioList.Contains(iValue)) return false;
            ioList.Add(iValue);
            return true;
        }

        static string IntList(List<int> iList)
        {
            if (iList == null || iList.Count == 0) return "[]";
            var aParts = new List<string>();
            foreach (int v in iList) aParts.Add(v.ToString(CultureInfo.InvariantCulture));
            return "[" + string.Join(", ", aParts) + "]";
        }

        static string StrList(List<string> iList)
        {
            if (iList == null || iList.Count == 0) return "[]";
            var aParts = new List<string>();
            foreach (string s in iList) aParts.Add(OneLine(s));
            return "[" + string.Join(", ", aParts) + "]";
        }

        // frontmatter 一行一值 —— 換行會把後面的內容變成別的 key ⇒ 進 frontmatter 前一律壓成單行。
        static string OneLine(string? s) => (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        static string Nz(string? s) => string.IsNullOrWhiteSpace(s) ? "_(未填)_" : s!;
        internal static string Nz2(string? s) => string.IsNullOrWhiteSpace(s) ? "unknown" : s!;
    }
}
