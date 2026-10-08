// 區塊職責：任務單**寫入 op** 的本體 —— create / claim / assign / unassign / update / comment / check / link /
//           resolve / commit / sweep / wrapup / wrapup_skip（TASK-0349）。
// 物理意義：狀態機與所有閘**只有這一份**，住在 SCP_Core；只在 Senate Server 的 `task-write` 裡被呼叫。
//           判準與措辭每一段都有來由（⛔ 不要順手重寫）：
//           閘（blocker／QA／confirm／signer／expect_text／縮水秤）、鎖內重判（形狀乙）、回報用落檔後那一份。
//
// ⚠ 本檔**不做**三件要等別的 process 的事 —— 它們在持鎖的 Server 裡做會卡住所有寫入，
//   ⇒ 由呼叫端（`senate cmd task`，CLI 那一側）在寫完之後做，本檔只把「要做什麼」交回去：
//   ① 酒館通知 ⇒ `SCP_TaskOpResult.Notices`（訊息本體在這裡組好，發文走 `SCP_ITavernPostGateway`）
//   ② 工作記憶（wrapup 的 `why`）⇒ `SCP_TaskOpResult.Memory`（呼叫端寫進工作記憶，`SCP_WorkMemory`）
//   ③ Coding 場（claim 的 `scope`）⇒ 呼叫端在**寫單之前**開場（開不了就不認領），寫不成再回捲
// 數值影響：每個 op 至多寫一張單（link 兩張、sweep N 張）；回報是一份 markdown（落 `letters/<P>/cmd/task_<op>.md`）。
// ⚠ 方言限制：C# 9 / netstandard2.1。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Paths;

namespace SCP.Core.Tasks
{
    /// <summary>一則待發的酒館通知（寫入端只組、不發）。</summary>
    public sealed class SCP_TaskNotice
    {
        /// <summary>created / status / assigned / comment —— 落進訊息 meta 的 `kind`。</summary>
        public string Kind = "";
        public string TaskId = "";
        public string Body = "";
    }

    /// <summary>一筆待寫的工作記憶（wrapup 的 `why`；寫入端只交代、不跑 python）。</summary>
    public sealed class SCP_TaskMemoryRequest
    {
        public string Topic = "";
        public string Type = "";
        public string Id = "";
        public string Title = "";
        public string Body = "";
        public string By = "";
    }

    public sealed class SCP_TaskOpResult
    {
        /// <summary>0 ＝ 成功（含「刻意零寫入」的 dry-run／冪等）；1 ＝ 被閘擋下或失敗（**零寫入**，除非 Report 另有說明）。</summary>
        public int ExitCode;
        /// <summary>完整回報（markdown）。⚠ 不論成功失敗都有內容 —— 呼叫端保證會讀的只有這一份。</summary>
        public StringBuilder Report = new StringBuilder();
        /// <summary>一行摘要（CLI 印在最前面）。</summary>
        public string Headline = "";
        /// <summary>這一趟**真的寫了**幾張單（dry-run／冪等／擋下 ⇒ 0）。</summary>
        public int Wrote;
        /// <summary>主要那張單的號碼（create 是新號）。</summary>
        public int Index;
        public List<SCP_TaskNotice> Notices = new List<SCP_TaskNotice>();
        public SCP_TaskMemoryRequest? Memory;
        public Dictionary<string, string> Values = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    /// <summary>op 內部「擋下」用的例外：訊息會落進回報的 `## ❌ 失敗`，退出碼 1。</summary>
    public sealed class SCP_TaskOpException : Exception
    {
        public SCP_TaskOpException(string iMessage) : base(iMessage) { }
    }

    public static class SCP_TaskOps
    {
        /// <summary>寫入 op 全集（順序＝說明文件的順序）。</summary>
        public static readonly string[] WriteOps =
        {
            "create", "claim", "assign", "unassign", "update", "comment", "check",
            "link", "resolve", "commit", "sweep", "wrapup", "wrapup_skip",
        };

        // ===========================================================
        // 區塊職責：每個 op **必填**與**會讀**的鍵。
        // ⚠ 兩欄的保守方向相反：Required 從嚴（多列會砍掉合法呼叫）、Known 從寬（少列會擋掉合法呼叫）。
        //   🩸 Known 存在的理由（2026-09-21）：`--arg kind=related_to` 打錯參數名被靜默吃掉 ⇒
        //      `op_link` 取預設 `blocked_by` ⇒ 兩張單被標成阻塞，回傳 ✓Success。
        // ⚠ `persona` 不在這裡 —— 它是呼叫者身分，每個 op 都吃。
        // ===========================================================
        public static readonly Dictionary<string, string[]> Required = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["create"] = new[] { "title" },
            ["claim"] = new[] { "index" },
            ["assign"] = new[] { "index", "target_persona" },
            ["unassign"] = new[] { "index", "target_persona" },
            ["update"] = new[] { "index" },
            ["comment"] = new[] { "index", "body" },
            ["check"] = new[] { "index" },
            ["link"] = new[] { "index", "target" },
            ["resolve"] = new[] { "index" },
            ["commit"] = new[] { "index", "sha" },
            ["sweep"] = new string[0],
            ["wrapup"] = new[] { "index", "progress" },
            ["wrapup_skip"] = new[] { "index", "reason" },
        };

        public static readonly Dictionary<string, string[]> Known = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["create"] = new[] { "title", "type", "description", "evidence", "criteria", "severity", "priority",
                                 "status", "epic_id", "milestone", "tags", "memory_topic" },
            ["claim"] = new[] { "index", "role", "scope" },
            ["assign"] = new[] { "index", "target_persona", "role", "replace" },
            ["unassign"] = new[] { "index", "target_persona", "role" },
            ["update"] = new[] { "index", "title", "description", "criteria", "status", "priority", "severity",
                                 "milestone", "memory_topic", "memory_archived_commit", "allow_shrink", "unset" },
            ["comment"] = new[] { "index", "body" },
            ["check"] = new[] { "index", "criteria_index", "expect_text" },
            ["link"] = new[] { "index", "target", "op_link", "remove" },
            ["resolve"] = new[] { "index", "status", "confirm", "note", "qa_note" },
            ["commit"] = new[] { "index", "sha", "mode" },
            ["sweep"] = new[] { "assignee", "confirm" },
            ["wrapup"] = new[] { "index", "progress", "why", "memory_type" },
            ["wrapup_skip"] = new[] { "index", "reason" },
        };

        /// <summary>可被 `unset=` 清空的欄位白名單（TASK-0079）—— status／priority／title 沒有「空」這個合法狀態。</summary>
        static readonly string[] UNSETTABLE = { "memory_topic", "memory_archived_commit", "milestone" };

        const int ShrinkFloor = 10;

        /// <summary>指令提示一律印**這個宿主**的寫法（`senate cmd task …`）。</summary>
        static string Cmd(string iRest) => SCP_CmdRegistry.Invoke("task " + iRest);

        // ===========================================================
        // 區塊職責：分派。例外一律收成回報的 `## ❌ 失敗` ＋ exit 1（原因**必須**落進回報 —— 呼叫端保證會讀的只有它）。
        // ===========================================================
        public static SCP_TaskOpResult Run(SCP_DataRoot iRoot, string iOp, string iActor,
                                           IReadOnlyDictionary<string, string> iArgs)
        {
            var r = new SCP_TaskOpResult();
            string aOp = (iOp ?? "").Trim().ToLowerInvariant();
            string aActor = string.IsNullOrWhiteSpace(iActor) ? "unknown" : iActor.Trim();
            r.Report.AppendLine($"# Task op={aOp} persona={aActor}  ts=`{DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture)}`（本地時間）");
            r.Report.AppendLine("> ⤷ 寫入端：Senate Server（`task-write`，TASK-0349）—— 任務單唯一的寫入端");
            r.Report.AppendLine();
            var c = new Ctx(iRoot, aActor, iArgs, r);
            try
            {
                switch (aOp)
                {
                    case "create": OpCreate(c); break;
                    case "claim": OpClaim(c); break;
                    case "assign": OpAssign(c); break;
                    case "unassign": OpUnassign(c); break;
                    case "update": OpUpdate(c); break;
                    case "comment": OpComment(c); break;
                    case "check": OpCheck(c); break;
                    case "link": OpLink(c); break;
                    case "resolve": OpResolve(c); break;
                    case "commit": OpCommit(c); break;
                    case "sweep": OpSweep(c); break;
                    case "wrapup": OpWrapup(c); break;
                    case "wrapup_skip": OpWrapupSkip(c); break;
                    default:
                        throw new SCP_TaskOpException($"[Task] 認不得的寫入 op='{aOp}'（{string.Join("|", WriteOps)}）"
                            + " —— 讀取（list／show／kanban）走 `senate cmd tasks`");
                }
            }
            catch (Exception e)
            {
                r.ExitCode = 1;
                r.Report.AppendLine();
                r.Report.AppendLine("## ❌ 失敗");
                r.Report.AppendLine($"- reason: {e.Message}");
                if (!(e is SCP_TaskOpException))
                    r.Report.AppendLine($"- 例外型別：`{e.GetType().Name}`（不是閘擋下 —— 是寫入端自己出事，去看 Server log）");
                if (r.Headline.Length == 0) r.Headline = "✗ " + FirstLine(e.Message);
            }
            if (r.Headline.Length == 0) r.Headline = r.ExitCode == 0 ? "✓ op=" + aOp : "✗ op=" + aOp;
            r.Values["wrote"] = r.Wrote.ToString(CultureInfo.InvariantCulture);
            if (r.Index > 0) r.Values["index"] = r.Index.ToString(CultureInfo.InvariantCulture);
            return r;
        }

        sealed class Ctx
        {
            public readonly SCP_DataRoot Root;
            public readonly string Actor;
            readonly IReadOnlyDictionary<string, string> m_Args;
            public readonly SCP_TaskOpResult R;
            public StringBuilder Rep => R.Report;

            public Ctx(SCP_DataRoot iRoot, string iActor, IReadOnlyDictionary<string, string> iArgs, SCP_TaskOpResult iR)
            { Root = iRoot; Actor = iActor; m_Args = iArgs; R = iR; }

            public string Arg(string iKey, string iDefault = "")
                => m_Args.TryGetValue(iKey, out string? v) && v != null ? v : iDefault;

            public string Trim(string iKey, string iDefault = "") => Arg(iKey, iDefault).Trim();
        }

        // ===========================================================
        // 區塊職責：開新單。`criteria` 必填（bug 單改 `evidence` 必填、criteria 由兩段骨架自帶）。
        // ⚠ **所有參數在配號之前解析完**（TASK-0349）：🩸 舊版把 priority／severity／status 的解析放在
        //   配號之後的 lambda 裡 ⇒ 打錯一個 enum 值就吃掉一個號碼（2026-09-30：0351／0352）。
        //   現在寫入端也改成「建檔成功才寫計數檔」，這一格是雙保險。
        // ===========================================================
        static void OpCreate(Ctx c)
        {
            string aTitle = c.Trim("title");
            string aCriteria = c.Trim("criteria");
            string aEvidence = c.Trim("evidence");
            var aType = ParseEnum(c, "type", SCP_TaskType.feature);
            if (aType == SCP_TaskType.all) throw new SCP_TaskOpException("[Task] type=`all` 是篩選用的成員，不是任務種類");
            var aPriority = ParseEnum(c, "priority", SCP_TaskPriority.normal);
            var aSeverity = ParseEnum(c, "severity", aType == SCP_TaskType.bug ? SCP_TaskSeverity.wrong : SCP_TaskSeverity.none);
            var aStatus = ParseEnum(c, "status", SCP_TaskStatus.todo);
            if (aStatus == SCP_TaskStatus.all || aStatus == SCP_TaskStatus.open)
                throw new SCP_TaskOpException($"[Task] status=`{aStatus}` 是篩選用的成員，不是可落盤的狀態");

            var aMissing = new List<string>();
            if (string.IsNullOrWhiteSpace(aTitle)) aMissing.Add("title");
            if (aType == SCP_TaskType.bug) { if (aEvidence.Length == 0) aMissing.Add("evidence"); }
            else if (string.IsNullOrWhiteSpace(aCriteria)) aMissing.Add("criteria");
            if (aMissing.Count > 0)
            {
                c.Rep.AppendLine("## blocked");
                c.Rep.AppendLine($"- reason: 缺必填欄位：{string.Join(" / ", aMissing)}");
                if (aMissing.Contains("evidence"))
                {
                    c.Rep.AppendLine("- `evidence` 要放**感官騙不了的硬證**（error code／log 行號／重現指令／round-trip diff），");
                    c.Rep.AppendLine("  並寫明**這個讀數是怎麼拿到的** —— 閘擋得住「沒證據」，出處才擋得住「假證據」。重述現象不算。");
                    c.Rep.AppendLine("  用法：`--arg-file evidence=<檔>`（走檔案，內文不經過命令列）");
                    c.Rep.AppendLine("- 拿不出硬證的（提示缺漏／流程摩擦），改 `--arg type=improvement --arg tags=friction`（或 `suggestion`）。");
                }
                else
                {
                    c.Rep.AppendLine("- `criteria` 要寫**可以被客觀量測**的條件（QA 有權以「這條驗不了」退回）。");
                    c.Rep.AppendLine("  例：`- [ ] op=link 之後兩張單的 blocked_by/blocks 各自讀回有對方`");
                }
                throw new SCP_TaskOpException($"[Task] create 缺必填：{string.Join(",", aMissing)}");
            }

            string aNow = SCP_TaskStore.NowUtc();
            string aDescription = c.Trim("description");
            if (aEvidence.Length > 0)
                aDescription = (aDescription.Length == 0 ? "" : aDescription + "\n\n")
                    + "### 🔬 證據（開單時附；含「讀數怎麼拿到的」）\n\n" + aEvidence;
            List<string> aTags = SplitList(c.Arg("tags"));
            string aMilestone = c.Trim("milestone"), aEpic = c.Trim("epic_id"), aTopic = c.Trim("memory_topic");

            SCP_TaskEntry? e = null;
            SCP_TaskCreateResult aCreated = SCP_TaskStore.Create(c.Root, aIdx =>
            {
                var n = new SCP_TaskEntry
                {
                    index = aIdx, type = aType, priority = aPriority, severity = aSeverity, status = aStatus,
                    title = aTitle, reporter = c.Actor, milestone = aMilestone, epic_id = aEpic, memory_topic = aTopic,
                    created_at = aNow, updated_at = aNow,
                };
                n.tags.AddRange(aTags);
                e = n;
                string aCrit = aCriteria;
                // bug 單兩段骨架（⛔ 不自帶「異源複驗」那一格 —— Tim 2026-09-08 拍板：一個做不到的驗收條件
                //   跟沒有驗收條件在看板上長得一樣）。骨架寫著 `Fixes TASK-<n>` ⇒ 要在拿到號碼之後才組。
                if (aType == SCP_TaskType.bug)
                {
                    string aSkeleton = "- [ ] ① 重現讀數：見「任務描述 › 🔬 證據」（讀數＋怎麼拿到的）\n"
                        + $"- [ ] ② 修正落盤（commit 帶 `Fixes {n.Id}`）";
                    aCrit = aCrit.Length == 0 ? aSkeleton : aCrit.TrimEnd() + "\n" + aSkeleton;
                }
                return (n, SCP_TaskWrite.Body($"{aNow}　`{n.status}`　由 {c.Actor} 開單", aCrit, aDescription));
            });
            if (aCreated.Index <= 0 || e == null)
                throw new SCP_TaskOpException("[Task] op=create 沒有落檔 —— 建構回了 null ⇒ **一個位元組都沒寫**（計數檔也沒動）。");

            c.R.Index = aCreated.Index; c.R.Wrote = 1;
            c.R.Headline = $"✓ 已建單 {e.Id}：{e.title}";
            c.R.Values["heal_attempts"] = aCreated.HealAttempts.ToString(CultureInfo.InvariantCulture);
            c.Rep.AppendLine($"## ✅ 已建單 **{e.Id}**");
            c.Rep.AppendLine($"- `{e.type}` / " + (e.severity == SCP_TaskSeverity.none ? "" : $"`{e.severity}` / ")
                + $"`{e.priority}` / `{e.status}`　開單：{e.reporter}");
            c.Rep.AppendLine($"- title: {e.title}");
            c.Rep.AppendLine($"- 單檔：`{SCP_TaskIO.TaskPath(c.Root, e.index)}`");
            if (aCreated.CounterNote.Length > 0) c.Rep.AppendLine($"- ⚠ 配號讀數：{aCreated.CounterNote}");
            if (aCreated.HealAttempts > 0)
                c.Rep.AppendLine($"- ⚠ 撞檔 {aCreated.HealAttempts} 次才建成 —— **單一寫入端的世界裡這不該發生**，代表有人繞過寫入端直接建檔。");
            AppendSimilar(c, e);
            c.Rep.AppendLine();
            c.Rep.AppendLine("## ⚠ 這張單現在沒有任何參與者");
            c.Rep.AppendLine("- 指派走後台頁或 `op=assign`。**沒有指名 QA 的單，結單由開單人或 PM 做** —— 那不是預設值，是一個選擇；");
            c.Rep.AppendLine("  要有人驗就 `op=assign --arg role=qa`，`resolve` 才會有閘門擋。");
            c.Rep.AppendLine();
            c.Rep.AppendLine("## ▶ 下一步");
            c.Rep.AppendLine($"- 認領 → `{Cmd($"--arg op=claim --arg persona=<你> --arg index={e.index} --arg role=dev")}`");
            c.Rep.AppendLine("- ⛔ **不要抄進見叢** —— 認領之後它會自己出現在早安 brief 的 **§2.5 見單**。");
            c.Rep.AppendLine($"- 做完 commit 訊息帶 `Fixes {e.Id}`（提交時自動推進 —— 有 QA 進 in_review，沒 QA 直接 done）");
            Notify(c, e, "created", aDescription);
        }

        /// <summary>查重提示 —— 只呈現、不阻擋（標題字詞重疊＋tags 交集，**不是語意檢索**：查不到 ≠ 不存在）。</summary>
        static void AppendSimilar(Ctx c, SCP_TaskEntry iNew)
        {
            var aHits = new List<(int score, SCP_TaskEntry t)>();
            HashSet<string> aWords = Tokens(iNew.title);
            foreach (SCP_TaskEntry t in SCP_TaskIO.LoadAll(c.Root))
            {
                if (t.index == iNew.index || t.IsClosed()) continue;
                int aScore = 0;
                foreach (string w in Tokens(t.title)) if (aWords.Contains(w)) aScore++;
                foreach (string aTag in iNew.tags) if (t.tags.Contains(aTag)) { aScore += 1; break; }
                if (aScore > 0) aHits.Add((aScore, t));
            }
            if (aHits.Count == 0) return;
            aHits.Sort((a, b) => b.score.CompareTo(a.score));
            c.Rep.AppendLine();
            c.Rep.AppendLine("⚠ **可能重複（未阻擋，請自行判斷）** —— v1 粗篩：標題字詞重疊＋tags 交集，**不是語意檢索**。查不到 ≠ 不存在。");
            for (int i = 0; i < aHits.Count && i < 3; i++)
                c.Rep.AppendLine($"  - {aHits[i].t.Id}　`{aHits[i].t.status}`　{aHits[i].t.title}");
        }

        static HashSet<string> Tokens(string s)
        {
            var aSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(s)) return aSet;
            foreach (string t in s.Split(new[] { ' ', '\t', '/', '\\', '(', ')', '[', ']', '，', '、', '：', ':', '。', '—' },
                         StringSplitOptions.RemoveEmptyEntries))
                if (t.Length >= 2) aSet.Add(t);
            return aSet;
        }

        // ===========================================================
        // 區塊職責：認領 —— 把自己加進參與者；**執行角色**且單子在 backlog/todo 才推 in_progress。
        // 🩸 basecamp 2026-08-24：無條件推狀態 ⇒ `role=qa` 認領也把單子推成「進行中」。
        // ⚠ `scope`（開 Coding 場）由呼叫端在本 op **之前**處理（見檔頭 ③）；這裡只看它有沒有被給。
        // ===========================================================
        static void OpClaim(Ctx c)
        {
            SCP_TaskEntry e = Require(c, out int aIndex);
            var aRole = ParseEnum(c, "role", SCP_TaskRole.dev);
            string aNow = SCP_TaskStore.NowUtc();
            bool aNew = false;
            var aFrom = e.status;
            bool aDoingRole = aRole == SCP_TaskRole.dev || aRole == SCP_TaskRole.design
                || aRole == SCP_TaskRole.sound || aRole == SCP_TaskRole.art;
            string? aWhyNoMove = null;
            bool aWrote = SCP_TaskStore.Mutate(c.Root, aIndex, m =>
            {
                aNew = AddParticipant(m, c.Actor, aRole, aNow);
                aFrom = m.status;
                bool aNotStarted = aFrom == SCP_TaskStatus.backlog || aFrom == SCP_TaskStatus.todo;
                aWhyNoMove = null;
                if (!aDoingRole) aWhyNoMove = $"`{aRole}` 是驗收／協調角色，不是「開工」⇒ 狀態不動";
                else if (!aNotStarted) aWhyNoMove = $"單子已經在 `{aFrom}` ⇒ 不往回推（認領只從 backlog/todo 推進）";
                if (aWhyNoMove == null) m.status = SCP_TaskStatus.in_progress;
                m.updated_at = aNow;
                return SCP_TaskWrite.Line(aWhyNoMove == null
                    ? $"{aNow}　`in_progress`　{c.Actor} 認領（role={aRole}，原狀態 {aFrom}）"
                    : $"{aNow}　`{m.status}`　{c.Actor} 加入為 {aRole}（狀態不動：{aWhyNoMove}）");
            });
            if (!aWrote)
                throw new SCP_TaskOpException($"[Task] TASK-{aIndex:0000} 認領沒有落檔 —— 鎖內重讀時那張單不在了（被刪或被搬）⇒ **寫入沒有發生**。");
            e = SCP_TaskIO.Find(c.Root, aIndex) ?? e;   // 回報用落檔後那一份（🩸 2026-09-11：不重讀的話，剛加的人不在成功回報裡）
            c.R.Index = aIndex; c.R.Wrote = 1;
            c.R.Headline = aWhyNoMove == null ? $"✓ {e.Id} 已認領：{aFrom} → in_progress（{aRole}）" : $"✓ {e.Id} 加入為 {aRole}（狀態維持 {aFrom}）";
            c.Rep.AppendLine($"## ✅ {e.Id} 已認領");
            string aDup = aNew ? "" : "（這個 persona＋role 本來就在參與者裡，沒有重複加）";
            c.Rep.AppendLine(aWhyNoMove == null
                ? $"- {aFrom} → **in_progress**　role=`{aRole}`" + aDup
                : $"- 狀態**維持 `{aFrom}`**　role=`{aRole}` —— {aWhyNoMove}" + aDup);
            c.Rep.AppendLine($"- 參與：{Participants(e.participants)}");
            if (c.Trim("scope").Length == 0)
                c.Rep.AppendLine("- ⚠ **沒有開 Coding 場**（沒給 `--arg scope=`）⇒ 這只是「記錄我在做這件事」。要動工（會改 C#）請帶範圍："
                    + $"`{Cmd($"--arg op=claim --arg persona={c.Actor} --arg index={aIndex} --arg scope=<絕對路徑>")}`");
            List<string> aBlockers = SCP_TaskStore.OpenBlockers(c.Root, e);
            if (aBlockers.Count > 0)
            {
                c.Rep.AppendLine($"- 🛑 **注意：這張單有未解 blocker** —— {string.Join("；", aBlockers)}");
                c.Rep.AppendLine("  認領不擋（也許妳就是要去解它），但 `resolve` 會擋。");
            }
            Notify(c, e, "status", aWhyNoMove == null
                ? $"{aFrom} → **in_progress**（{c.Actor} 認領 role={aRole}）"
                : $"{c.Actor} 加入為 `{aRole}`（狀態維持 `{aFrom}` —— {aWhyNoMove}）");
        }

        // ===========================================================
        // 區塊職責：指派（append）。`replace=1` ＝ 換角色（先拿掉這個人既有的其他角色）。
        // 🩸 TASK-0131：加一個角色與換角色在指令上同形 ⇒ 沒帶 replace 而那個人已有別的角色時**要說出來**。
        // ===========================================================
        static void OpAssign(Ctx c)
        {
            SCP_TaskEntry e = Require(c, out int aIndex);
            string aTarget = c.Trim("target_persona");
            if (aTarget.Length == 0) throw new SCP_TaskOpException("[Task] op=assign 需要 --arg target_persona=<誰>");
            var aRole = ParseEnum(c, "role", SCP_TaskRole.dev);
            bool aReplace = c.Trim("replace") == "1";
            string aNow = SCP_TaskStore.NowUtc();
            string aRemoved = "";
            bool aNew = false;
            var aExisting = new List<SCP_TaskRole>();
            var aOtherRoles = new List<SCP_TaskRole>();
            bool aWrote = SCP_TaskStore.Mutate(c.Root, aIndex, m =>
            {
                aExisting = m.RolesOfAnyCase(aTarget);
                aOtherRoles = aExisting.Where(x => x != aRole).ToList();
                aRemoved = "";
                if (aReplace && aOtherRoles.Count > 0)
                {
                    m.participants.RemoveAll(p => string.Equals(p.persona, aTarget, StringComparison.OrdinalIgnoreCase) && p.role != aRole);
                    aRemoved = string.Join("／", aOtherRoles);
                }
                aNew = AddParticipant(m, aTarget, aRole, aNow);
                m.updated_at = aNow;
                return SCP_TaskWrite.Line($"{aNow}　`assign`　{c.Actor} 指派 {aTarget} 為 {aRole}"
                    + (aRemoved.Length > 0 ? $"（**換角色**：拿掉 {aRemoved}）" : ""));
            });
            if (!aWrote)
                throw new SCP_TaskOpException($"[Task] TASK-{aIndex:0000} 指派沒有落檔 —— 鎖內重讀時那張單不在了 ⇒ **寫入沒有發生**，{aTarget} 沒有被指派。");
            e = SCP_TaskIO.Find(c.Root, aIndex) ?? e;
            c.R.Index = aIndex; c.R.Wrote = 1;
            c.R.Headline = $"✓ {e.Id} 參與者已更新：{aTarget} ← {aRole}";
            c.Rep.AppendLine($"## ✅ {e.Id} 參與者已更新");
            c.Rep.AppendLine($"- {(aNew ? "新增" : "已存在，未重複加")}：{aTarget}（{aRole}）");
            if (aRemoved.Length > 0)
                c.Rep.AppendLine($"- 🔁 **換角色**（`replace=1`）：{aTarget} 原本的 `{aRemoved}` 已拿掉");
            else if (aOtherRoles.Count > 0)
            {
                var aNowRoles = new List<SCP_TaskRole>(aExisting) { aRole };
                c.Rep.AppendLine($"- ⚠ **{aTarget} 本來就有角色 `{string.Join("／", aOtherRoles)}`** —— 本次是**加上** `{aRole}`，"
                    + $"現在他同時是 `{string.Join("／", aNowRoles.Distinct())}`。");
                c.Rep.AppendLine($"  · 想**換角色**請帶 `--arg replace=1`："
                    + $"`{Cmd($"--arg op=assign --arg persona={c.Actor} --arg index={e.index} --arg target_persona={aTarget} --arg role={aRole} --arg replace=1")}`");
                c.Rep.AppendLine("  · ⚠ 一人多角是合法的（`pm`＋`qa` 很常見）—— 這一行不是錯誤，是**要你確認你按的是哪一個**。");
            }
            c.Rep.AppendLine($"- 參與：{Participants(e.participants)}");
            Notify(c, e, "assigned", $"{aTarget} ← `{aRole}`");
            c.Rep.AppendLine($"- 📌 這張單會出現在 {aTarget} 早安 brief 的 **§2.5 見單** —— 但只有 `in_progress`／`in_review` 才逐張列；");
            c.Rep.AppendLine("  還在 `todo`／`backlog` 時只算進張數。⇒ **酒館通知仍是他今天就知道這件事的那條路。**");
        }

        // ===========================================================
        // 區塊職責：移除參與者（`assign` 的反向）。找不到那個人 ⇒ 零寫入，且與「單子不在」分得出來。
        // ===========================================================
        static void OpUnassign(Ctx c)
        {
            SCP_TaskEntry e = Require(c, out int aIndex);
            string aTarget = c.Trim("target_persona");
            if (aTarget.Length == 0) throw new SCP_TaskOpException("[Task] op=unassign 需要 --arg target_persona=<誰>");
            bool aHasRole = c.Trim("role").Length > 0;
            var aRole = aHasRole ? ParseEnum(c, "role", SCP_TaskRole.dev) : SCP_TaskRole.dev;
            string aNow = SCP_TaskStore.NowUtc();
            int aRemoved = 0;
            var aAfter = new List<SCP_TaskParticipant>();
            bool aWrote = SCP_TaskStore.Mutate(c.Root, aIndex, m =>
            {
                int aBefore = m.participants.Count;
                m.participants.RemoveAll(p => string.Equals(p.persona, aTarget, StringComparison.OrdinalIgnoreCase)
                                              && (!aHasRole || p.role == aRole));
                aRemoved = aBefore - m.participants.Count;
                aAfter = new List<SCP_TaskParticipant>(m.participants);
                if (aRemoved == 0) return SCP_TaskWrite.Skip;
                m.updated_at = aNow;
                return SCP_TaskWrite.Line($"{aNow}　`unassign`　{c.Actor} 移除 {aTarget}"
                    + (!aHasRole ? "（全部角色）" : $"（role={aRole}）") + $"　共 {aRemoved} 筆");
            }, out bool aFound);
            c.R.Index = aIndex;
            if (!aWrote)
            {
                if (!aFound) throw new SCP_TaskOpException($"[Task] TASK-{aIndex:0000} 移除沒有落檔 —— 鎖內重讀時那張單不在了 ⇒ **寫入沒有發生**。");
                c.R.Headline = $"· {e.Id} 沒有變更（{aTarget} 不在參與者裡）";
                c.Rep.AppendLine($"## {e.Id} 沒有變更");
                c.Rep.AppendLine($"- {aTarget}" + (!aHasRole ? "" : $"（role={aRole}）") + " 不在參與者裡 ⇒ **什麼都沒寫**（這是「找不到」，不是「移除成功」）");
                c.Rep.AppendLine($"- 現有參與：{Participants(aAfter)}");
                return;
            }
            c.R.Wrote = 1;
            c.R.Headline = $"✓ {e.Id} 已移除 {aRemoved} 筆參與（{aTarget}）";
            c.Rep.AppendLine($"## ✅ {e.Id} 已移除 {aRemoved} 筆參與");
            c.Rep.AppendLine($"- 移除：{aTarget}{(!aHasRole ? "（全部角色）" : $"（{aRole}）")}");
            c.Rep.AppendLine($"- 現有參與：{Participants(aAfter)}");
            bool aHasQa = aAfter.Any(p => p.role == SCP_TaskRole.qa);
            if (!aHasQa) c.Rep.AppendLine("- ⚠ 這張單**現在沒有 QA** ⇒ `resolve` 沒有閘會擋，結單由開單人或 PM 做。");
        }

        // ===========================================================
        // 區塊職責：屬性更新。⛔ 不准推 done/cancelled（結單走 resolve —— 那條路上有兩道閘）。
        //   criteria／description 是**整段覆寫** ⇒ 縮水秤（TASK-0188）擋「只送勾選格」那一刀。
        // ===========================================================
        static void OpUpdate(Ctx c)
        {
            SCP_TaskEntry e = Require(c, out int aIndex);
            string aNow = SCP_TaskStore.NowUtc();
            var aChanges = new List<string>();
            var aUnsetNotes = new List<string>();
            // 參數先全部解析（鎖外）—— 不合法的值在碰任何檔之前就擋下
            string aStatusRaw = c.Trim("status");
            SCP_TaskStatus? aStatus = null;
            if (aStatusRaw.Length > 0)
            {
                var s = ParseEnum(c, "status", SCP_TaskStatus.todo);
                if (s == SCP_TaskStatus.all || s == SCP_TaskStatus.open)
                    throw new SCP_TaskOpException($"[Task] status=`{s}` 是篩選用的成員，不是可落盤的狀態");
                if (s == SCP_TaskStatus.done || s == SCP_TaskStatus.cancelled)
                    throw new SCP_TaskOpException("[Task] 結單請走 `op=resolve`（那條路上有 blocker 與 QA 兩道閘，而 update 沒有）。這不是麻煩，是刻意不留旁路。");
                aStatus = s;
            }
            SCP_TaskPriority? aPri = c.Trim("priority").Length > 0 ? ParseEnum(c, "priority", SCP_TaskPriority.normal) : (SCP_TaskPriority?)null;
            SCP_TaskSeverity? aSev = c.Trim("severity").Length > 0 ? ParseEnum(c, "severity", SCP_TaskSeverity.none) : (SCP_TaskSeverity?)null;
            var aUnset = new List<string>();
            foreach (string aRawField in c.Trim("unset").Split(','))
            {
                string aField = aRawField.Trim();
                if (aField.Length == 0) continue;
                if (!UNSETTABLE.Contains(aField))
                    throw new SCP_TaskOpException($"[Task] unset 認不得欄位 `{aField}`（可清的只有：{string.Join(" / ", UNSETTABLE)}）"
                        + "　—— status／priority／title 這種**沒有「空」這個合法狀態**的欄位不在清單上。");
                aUnset.Add(aField);
            }
            string aCriteria = c.Arg("criteria");
            string aDescription = c.Arg("description");
            bool aAllowShrink = c.Trim("allow_shrink") == "1";

            bool aWrote = SCP_TaskStore.Mutate(c.Root, aIndex, m =>
            {
                aChanges.Clear(); aUnsetNotes.Clear();
                if (aStatus.HasValue)
                {
                    aChanges.Add($"status {m.status} → {aStatus.Value}");
                    m.status = aStatus.Value;
                    // 🩸 2026-08-24：誤關再改回 todo 而 closed_at 留著 ⇒ 資料自己跟自己打架。從已關改回未關一律清掉。
                    if (m.closed_at.Length > 0)
                    {
                        aChanges.Add($"closed_at 清空（原 {m.closed_at} —— 未關的單不該有結案時間）");
                        m.closed_at = "";
                    }
                }
                if (aPri.HasValue) { aChanges.Add($"priority {m.priority} → {aPri.Value}"); m.priority = aPri.Value; }
                if (aSev.HasValue) { aChanges.Add($"severity {m.severity} → {aSev.Value}"); m.severity = aSev.Value; }
                string aTitle = c.Trim("title");
                if (aTitle.Length > 0) { aChanges.Add("title 改寫"); m.title = aTitle; }
                string aMilestone = c.Trim("milestone");
                if (aMilestone.Length > 0) { aChanges.Add($"milestone → {aMilestone}"); m.milestone = aMilestone; }
                string aMemTopic = c.Trim("memory_topic");
                if (aMemTopic.Length > 0)
                {
                    aChanges.Add($"memory_topic {(m.memory_topic.Length == 0 ? "(空)" : m.memory_topic)} → {aMemTopic}"
                        + (TopicExists(c.Root, aMemTopic) ? "" : "　⚠ **這個主題目前不在磁碟上**（照樣寫入，但要知道）"));
                    m.memory_topic = aMemTopic;
                }
                string aMemSha = c.Trim("memory_archived_commit");
                if (aMemSha.Length > 0) { aChanges.Add($"memory_archived_commit → {aMemSha}"); m.memory_archived_commit = aMemSha; }
                // 顯式清除（TASK-0079）：本來就空 ⇒ 不計入變更，但逐格印出「本來就是空的」
                foreach (string aField in aUnset)
                {
                    string aOld = aField == "memory_topic" ? m.memory_topic
                                : aField == "memory_archived_commit" ? m.memory_archived_commit : m.milestone;
                    if ((aOld ?? "").Trim().Length == 0) { aUnsetNotes.Add($"`{aField}` 本來就是空的 ⇒ 沒有寫入"); continue; }
                    if (aField == "memory_topic") m.memory_topic = "";
                    else if (aField == "memory_archived_commit") m.memory_archived_commit = "";
                    else m.milestone = "";
                    aChanges.Add($"{aField} 清空（原 `{aOld}`）");
                }
                // 🩸 TASK-0188：整段覆寫前先量會掉多少（851 → 200 行那一刀）
                var aShrinkNotes = new List<string>();
                if (aCriteria.Trim().Length > 0)
                    GuardSectionOverwrite("驗收標準", SCP_TaskStore.ReadCriteria(c.Root, aIndex), aCriteria, aAllowShrink, aShrinkNotes);
                if (aDescription.Trim().Length > 0)
                    GuardSectionOverwrite("任務描述", SCP_TaskStore.ReadDescription(c.Root, aIndex), aDescription, aAllowShrink, aShrinkNotes);
                aChanges.AddRange(aShrinkNotes);
                if (aCriteria.Trim().Length > 0) aChanges.Add("criteria 整段改寫");
                if (aDescription.Trim().Length > 0) aChanges.Add("description 整段改寫");

                if (aChanges.Count == 0) return SCP_TaskWrite.Skip;
                m.updated_at = aNow;
                return SCP_TaskWrite.Body($"{aNow}　`update`　{c.Actor}：{string.Join("／", aChanges)}", aCriteria, aDescription);
            }, out bool aFound);
            c.R.Index = aIndex;
            if (!aWrote)
            {
                if (!aFound) throw new SCP_TaskOpException($"[Task] TASK-{aIndex:0000} 更新沒有落檔 —— 鎖內重讀時那張單不在了 ⇒ **寫入沒有發生**。");
                c.R.Headline = $"· {e.Id} 沒有任何變更";
                c.Rep.AppendLine($"## {e.Id} 沒有任何變更");
                foreach (string n in aUnsetNotes) c.Rep.AppendLine($"- ✓ {n}");
                if (aUnsetNotes.Count > 0) return;
                c.Rep.AppendLine("- 沒給任何可更新的欄位（status / priority / severity / title / milestone / memory_topic /"
                    + " memory_archived_commit / criteria / description / unset=<欄位>）⇒ **什麼都沒寫**。");
                return;
            }
            c.R.Wrote = 1;
            c.R.Headline = $"✓ {e.Id} 已更新：{string.Join("／", aChanges)}";
            c.Rep.AppendLine($"## ✅ {e.Id} 已更新");
            foreach (string x in aChanges) c.Rep.AppendLine($"- {x}");
            foreach (string n in aUnsetNotes) c.Rep.AppendLine($"- ✓ {n}");
        }

        static void CountSection(string iText, out int oNonEmpty, out int oBoxes, out int oProse, out List<string> oProseLines)
        {
            oNonEmpty = 0; oBoxes = 0; oProse = 0; oProseLines = new List<string>();
            foreach (string aRaw in (iText ?? "").Replace("\r", "").Split('\n'))
            {
                string aLine = aRaw.Trim();
                if (aLine.Length == 0) continue;
                oNonEmpty++;
                if (aLine.StartsWith("- [", StringComparison.Ordinal)) { oBoxes++; continue; }
                oProse++;
                oProseLines.Add(aLine);
            }
        }

        // 兩條判準命中任一就擋（`allow_shrink=1` 放行）：① 散文歸零 ② 腰斬（舊 ≥ 10 行且新 < 一半）。
        // ⚠ 反向對照：「只改一格字面、其餘照抄」行數幾乎不動 ⇒ 不會被擋（守衛連正常改字都擋，人會養成一律加旗標的習慣）。
        static void GuardSectionOverwrite(string iSectionName, string iOld, string iNew, bool iAllowShrink, List<string> oNotes)
        {
            CountSection(iOld, out int aOldLines, out int aOldBoxes, out int aOldProse, out List<string> aOldProseLines);
            CountSection(iNew, out int aNewLines, out int aNewBoxes, out int aNewProse, out _);
            bool aProseWiped = aOldProse > 0 && aNewProse == 0;
            bool aHalved = aOldLines >= ShrinkFloor && aNewLines * 2 < aOldLines;
            string aReading = $"{iSectionName}區段：{aOldLines} → {aNewLines} 行（勾選格 {aOldBoxes}→{aNewBoxes}／非勾選行 {aOldProse}→{aNewProse}）";
            if ((aProseWiped || aHalved) && !iAllowShrink)
            {
                var aMsg = new StringBuilder();
                aMsg.AppendLine($"[Task] op=update 擋下：`{iSectionName}` 是**整段覆寫**，這次會縮水。");
                aMsg.AppendLine($"  {aReading}");
                aMsg.AppendLine(aProseWiped
                    ? $"  ⛔ 舊段有 {aOldProse} 行**不是勾選格**的內容，而新內容一行都沒有 —— 那是「只送勾選格」的形狀（TASK-0188 血證：851 → 200 行）。"
                    : $"  ⛔ 非空行少於舊段的一半（{aOldLines} → {aNewLines}）。");
                int aShow = Math.Min(5, aOldProseLines.Count);
                if (aShow > 0)
                {
                    aMsg.AppendLine($"  ── 將被刪掉的非勾選行（共 {aOldProse} 行，前 {aShow} 行）──");
                    for (int i = 0; i < aShow; i++)
                    {
                        string aOne = aOldProseLines[i];
                        if (aOne.Length > 100) aOne = aOne.Substring(0, 100) + "…";
                        aMsg.AppendLine("  │ " + aOne);
                    }
                }
                aMsg.AppendLine("  ⇒ 想保留就把**整段**讀出來改（`senate cmd tasks --arg index=<n>` 或直接讀單檔）再整段送回；");
                aMsg.Append("  ⇒ 真的要縮，顯式帶 `--arg allow_shrink=1`。");
                throw new SCP_TaskOpException(aMsg.ToString());
            }
            oNotes.Add(aReading + (iAllowShrink && (aProseWiped || aHalved) ? "　⚠ 縮水已由 `allow_shrink=1` 放行" : ""));
        }

        // ===========================================================
        // 區塊職責：留言。`NextCommentId` 必須對**鎖內重讀**的那一份算（鎖外算會撞號 ⇒ 一則留言靜默消失）。
        // ===========================================================
        static void OpComment(Ctx c)
        {
            SCP_TaskEntry e = Require(c, out int aIndex);
            string aBody = c.Trim("body");
            if (aBody.Length == 0) throw new SCP_TaskOpException("[Task] op=comment 需要 --arg body=<內容>");
            string aNow = SCP_TaskStore.NowUtc();
            int aCommentId = 0, aPrevComments = 0;
            bool aWrote = SCP_TaskStore.Mutate(c.Root, aIndex, m =>
            {
                aCommentId = SCP_TaskStore.NextCommentId(m);
                aPrevComments = m.comments.Count;
                m.comments.Add(new SCP_TaskComment { id = aCommentId, persona = c.Actor, at = aNow, body = aBody });
                m.updated_at = aNow;
                return SCP_TaskWrite.Line($"{aNow}　`comment`　{c.Actor} 留言 #{aCommentId}");
            });
            if (!aWrote)
                throw new SCP_TaskOpException($"[Task] TASK-{aIndex:0000} 留言沒有落檔 —— 鎖內重讀時那張單不在了 ⇒ **寫入沒有發生**，妳的內容沒有進磁碟。");
            c.R.Index = aIndex; c.R.Wrote = 1;
            c.R.Headline = $"✓ {e.Id} 已留言 #{aCommentId}";
            c.R.Values["comment_id"] = aCommentId.ToString(CultureInfo.InvariantCulture);
            c.Rep.AppendLine($"## ✅ {e.Id} 已留言 #{aCommentId}");
            c.Rep.AppendLine($"- 作者：{c.Actor}　時間：{aNow}");
            c.Rep.AppendLine("- 落點：單檔的 `## 留言` 區塊（時間線只留一行索引）");
            c.Rep.AppendLine();
            c.Rep.AppendLine("```markdown");
            c.Rep.AppendLine(aBody);
            c.Rep.AppendLine("```");
            c.Rep.AppendLine();
            AppendRefWarnings(c, aBody, aPrevComments, SCP_TaskStore.ReadCriteria(c.Root, aIndex));
            SCP_TaskEntry aFresh = SCP_TaskIO.Find(c.Root, aIndex) ?? e;
            Notify(c, aFresh, "comment", "", aBody);
            c.Rep.AppendLine("- ⚠ 留言**會推進 `updated_at`** ⇒ 它會讓 stale 計時歸零（「留言說我還在做」跟「真的有做」在 stale 讀數上同形）。");
        }

        // ===========================================================
        // 區塊職責：勾驗收格並**留下是誰勾的**（TASK-0119）。⛔ 沒有 qa_note 代簽出口（沒勾的格不卡任何人）。
        //   危害不是掉更新，是**簽到別的格子上** ⇒ 鎖內重讀並比對文字，對不上整批不做。
        // ===========================================================
        static void OpCheck(Ctx c)
        {
            SCP_TaskEntry e = Require(c, out int aIndex);
            string aCriteria = SCP_TaskStore.ReadCriteria(c.Root, aIndex);
            List<string> aOpen = SCP_TaskStore.ListUncheckedCriteria(aCriteria);
            List<string> aDone = SCP_TaskStore.ListCheckedCriteria(aCriteria);
            List<string> aQa = e.QaPersonas();
            bool aAllowed;
            string aWho;
            if (aQa.Count > 0)
            {
                aAllowed = aQa.Any(s => string.Equals(s, c.Actor, StringComparison.OrdinalIgnoreCase));
                aWho = "本單指名的 QA：" + string.Join(" / ", aQa);
            }
            else
            {
                aAllowed = e.RolesOfAnyCase(c.Actor).Count > 0 || string.Equals(e.reporter, c.Actor, StringComparison.OrdinalIgnoreCase);
                var aNames = e.participants.Select(p => p.persona).Concat(new[] { e.reporter })
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                aWho = "本單**沒有指名 QA** ⇒ 參與者與開單人：" + string.Join(" / ", aNames);
            }
            // TASK-0194：一格一個尺度的簽名人（`[signer:<persona>]`）—— 只有 owner 能簽，owner 不必先入列
            List<string?> aOwners = aOpen.Select(SCP_TaskStore.CriteriaSigner).ToList();
            bool aOwnsAny = aOwners.Any(o => o != null && string.Equals(o, c.Actor, StringComparison.OrdinalIgnoreCase));
            c.R.Index = aIndex;

            c.Rep.AppendLine($"## {e.Id} 驗收標準　已勾 **{aDone.Count}** / 未勾 **{aOpen.Count}**");
            c.Rep.AppendLine($"- 可以勾的人：{aWho}");
            c.Rep.AppendLine($"- 你是：`{c.Actor}`　⇒ {(aAllowed ? "✅ 有權" : "🛑 **無權**")}（整張單的尺度）");
            if (aOwners.Any(o => o != null))
                c.Rep.AppendLine($"- ⭐ 本單有 **{aOwners.Count(o => o != null)}** 格帶 `[signer:…]` 指定簽名人 ⇒ 那幾格**只有本人**簽得掉（QA 與開單人也不行），且本人不必先入列。"
                    + $"　你{(aOwnsAny ? "**有**" : "沒有")}這樣的格子。");
            c.Rep.AppendLine();
            string aRuleText = aQa.Count > 0 ? "本單有指名 QA ⇒ 只有 " + string.Join(" / ", aQa) + " 能簽" : "本單沒有指名 QA ⇒ 參與者與開單人能簽";
            if (!aAllowed && !aOwnsAny)
            {
                c.Rep.AppendLine("## blocked");
                c.Rep.AppendLine("- reason: 勾驗收標準是**簽名行為** —— 不在名單上的人勾了它，那個勾看起來跟真的驗收一模一樣，而它沒有任何人負責。");
                c.Rep.AppendLine("- exits:");
                c.Rep.AppendLine($"    · 先入列 → `{Cmd($"--arg op=claim --arg persona={c.Actor} --arg index={aIndex} --arg role=qa")}`");
                c.Rep.AppendLine("    · 或請上面那些人跑，或請他們 `op=assign` 把你加進來");
                c.Rep.AppendLine("- ⛔ 本 op **沒有** `qa_note=` 代簽出口（`resolve` 有）—— 沒勾的驗收格不卡任何人 ⇒ 沒有正當的破例用例。");
                throw new SCP_TaskOpException($"[Task] op=check 擋下：`{c.Actor}` 不在 {e.Id} 的可簽名名單裡（{aRuleText}），不能替它簽名");
            }

            string aRaw = c.Trim("criteria_index");
            if (aRaw.Length == 0)
            {
                c.R.Headline = $"· {e.Id} dry-run：未勾 {aOpen.Count} 格（零寫入）";
                c.Rep.AppendLine("## 未勾的驗收格（序號＝**未勾清單**的序號，不是檔案行號）");
                if (aOpen.Count == 0 && aDone.Count == 0)
                {
                    int aTextLines = CountTextLines(aCriteria);
                    c.Rep.AppendLine("- 🛑 **這張單一格勾選格都沒有**（⛔ 不是「全部都勾了」）"
                        + (aTextLines > 0 ? $" —— 驗收標準那一段有 {aTextLines} 行文字，但沒有一行是 `- [ ]` 開頭" : " —— 驗收標準那一段是空的"));
                    c.Rep.AppendLine("  ⇒ 它現在**結構上簽不掉**。▶ 修法：每一條改寫成 `- [ ] <一格一行>`，走整份覆寫：");
                    c.Rep.AppendLine($"    `{Cmd($"--arg op=update --arg persona={c.Actor} --arg index={aIndex} --arg-file criteria=<整段>")}`");
                }
                else if (aOpen.Count == 0) c.Rep.AppendLine($"- （全部都勾了 —— 已勾 {aDone.Count} 格）");
                for (int i = 0; i < aOpen.Count; i++)
                {
                    string? aOwn = aOwners[i];
                    string aTag = aOwn == null ? ""
                        : (string.Equals(aOwn, c.Actor, StringComparison.OrdinalIgnoreCase) ? $"　🖊 **只有 `{aOwn}` 能簽 ⇒ 那是你**" : $"　🔒 只有 `{aOwn}` 能簽");
                    c.Rep.AppendLine($"- #{i + 1}　{Trunc(aOpen[i], 160)}{aTag}");
                }
                c.Rep.AppendLine();
                c.Rep.AppendLine("- 🛑 **dry-run**（沒帶 `criteria_index=`）⇒ **一個位元組都沒寫**。");
                c.Rep.AppendLine($"  勾它：`{Cmd($"--arg op=check --arg persona={c.Actor} --arg index={aIndex} --arg criteria_index=<n[,n...]>")}`");
                return;
            }

            var aWant = new List<int>();
            foreach (string aTok in aRaw.Split(','))
            {
                string t = aTok.Trim();
                if (t.Length == 0) continue;
                if (!int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v))
                    throw new SCP_TaskOpException($"[Task] criteria_index 裡有不是數字的東西：「{t}」—— 整批不執行");
                if (aOpen.Count == 0)
                {
                    if (aDone.Count == 0)
                        throw new SCP_TaskOpException($"[Task] {e.Id} **一格勾選格都沒有**（⛔ 不是「全部都勾了」）—— 驗收標準那一段有 {CountTextLines(aCriteria)} 行文字，"
                            + "但沒有一行是 `- [ ]` 開頭 ⇒ 這張單結構上簽不掉。不帶 `criteria_index` 跑一次，它會印修法");
                    throw new SCP_TaskOpException($"[Task] {e.Id} 的驗收標準**全部都勾了**（已勾 {aDone.Count} 格）—— 沒有格子可以勾。⚠ 已勾的行不在序號範圍內");
                }
                if (v < 1 || v > aOpen.Count)
                    throw new SCP_TaskOpException($"[Task] criteria_index={v} 在範圍外（目前未勾 1..{aOpen.Count}）—— ⚠ 序號是**未勾清單**的序號，不是檔案行號。整批不執行");
                if (!aWant.Contains(v)) aWant.Add(v);
            }
            if (aWant.Count == 0) throw new SCP_TaskOpException("[Task] criteria_index 解析後一個序號都不剩（只有分隔符？）");

            foreach (int v in aWant)
            {
                string? aOwn = aOwners[v - 1];
                if (aOwn == null)
                {
                    if (aAllowed) continue;
                    c.Rep.AppendLine("## blocked");
                    c.Rep.AppendLine($"- reason: 序號 #{v} 沒有 `[signer:…]` 標記 ⇒ 它走**整張單**的尺度，而 `{c.Actor}` 不在名單裡（{aWho}）。⇒ **一個位元組都沒寫**。");
                    throw new SCP_TaskOpException($"[Task] op=check 擋下：序號 #{v} 走整張單的尺度，而 `{c.Actor}` 不在 {e.Id} 的可簽名名單裡，不能替它簽名");
                }
                if (string.Equals(aOwn, c.Actor, StringComparison.OrdinalIgnoreCase)) continue;
                c.Rep.AppendLine("## blocked");
                c.Rep.AppendLine($"- reason: 序號 #{v} 帶 `[signer:{aOwn}]` ⇒ **只有 `{aOwn}` 本人簽得掉**。⛔ 你是 `{c.Actor}` —— 就算你是 QA 或開單人也不行，**代簽正是這個標記要防的那件事**。⇒ 一個位元組都沒寫。");
                c.Rep.AppendLine($"  ▶ 出口：請 `{aOwn}` 自己跑（他不必先入列）；⛔ 不要替他勾這一格。");
                throw new SCP_TaskOpException($"[Task] op=check 擋下：序號 #{v} 指定簽名人是 `{aOwn}`，而你是 `{c.Actor}` —— 這一格不接受代簽");
            }

            // expect_text：呼叫端把「我看到的那一行」帶進來當錨（序號會位移、文字不會）
            var aWantAsTyped = new List<int>(aWant);
            string aExpectRaw = c.Trim("expect_text");
            if (aExpectRaw.Length > 0)
            {
                var aParts = aExpectRaw.Split('|').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (aParts.Count != aWantAsTyped.Count)
                    throw new SCP_TaskOpException($"[Task] expect_text 給了 {aParts.Count} 筆、criteria_index 給了 {aWantAsTyped.Count} 個 —— **筆數必須相同**（用 `|` 分隔，照 criteria_index 的順序配對）。整批不執行");
                for (int i = 0; i < aParts.Count; i++)
                {
                    int v = aWantAsTyped[i];
                    string aSeen = aOpen[v - 1].Trim();
                    if (aSeen.StartsWith(aParts[i], StringComparison.Ordinal)) continue;
                    c.Rep.AppendLine("## blocked");
                    c.Rep.AppendLine($"- reason: `expect_text` 對不上 —— 序號 #{v} 現在指到的是「{Trunc(aSeen, 60)}」，而你帶進來的是「{Trunc(aParts[i], 60)}」⇒ **一個位元組都沒寫**。");
                    c.Rep.AppendLine($"  ▶ 重讀清單再決定：`{Cmd($"--arg op=check --arg persona={c.Actor} --arg index={aIndex}")}`");
                    throw new SCP_TaskOpException("[Task] check 沒有落檔（expect_text 對不上 ⇒ 序號位移，整批不做）");
                }
            }

            aWant.Sort(); aWant.Reverse();   // 由大到小套用 —— 勾掉一格會讓未勾清單縮短
            DateTime aNowLocal = DateTime.Now;
            string aNowUtc = SCP_TaskStore.NowUtc();
            var aHit = new List<string>();
            Dictionary<int, string> aAnchor = aWant.ToDictionary(v => v, v => aOpen[v - 1]);
            string? aRaceNote = null;
            bool aWrote = SCP_TaskStore.Mutate(c.Root, aIndex, m =>
            {
                string aFresh = SCP_TaskStore.ReadCriteria(c.Root, aIndex);
                List<string> aFreshOpen = SCP_TaskStore.ListUncheckedCriteria(aFresh);
                aHit.Clear();
                foreach (int v in aWant)
                {
                    if (v > aFreshOpen.Count) { aRaceNote = $"序號 {v} 現在超出未勾清單（鎖內只剩 {aFreshOpen.Count} 格）—— 有人在這中間勾了格子"; return SCP_TaskWrite.Skip; }
                    if (aFreshOpen[v - 1] != aAnchor[v])
                    {
                        aRaceNote = $"序號 {v} 指到的已經不是同一條驗收標準了（我要簽的是「{Trunc(aAnchor[v], 40)}」，鎖內那一格是「{Trunc(aFreshOpen[v - 1], 40)}」）—— 序號位移了，整批不做";
                        return SCP_TaskWrite.Skip;
                    }
                    string? aBody = SCP_TaskStore.CheckOffCriteria(ref aFresh, v, c.Actor, aNowLocal);
                    if (aBody == null) { aRaceNote = $"序號 {v} 勾不起來（鎖內重讀的清單與預期不符）—— 整批不做"; return SCP_TaskWrite.Skip; }
                    aHit.Add(aBody);
                }
                aHit.Reverse();
                m.updated_at = aNowUtc;
                return SCP_TaskWrite.Body($"{aNowUtc}　`check`　{c.Actor} 勾了 {aHit.Count} 格驗收標準（{string.Join("，", aWant.OrderBy(x => x))}）", aFresh, "");
            }, out bool aFound);
            if (!aWrote)
            {
                c.Rep.AppendLine("## blocked");
                c.Rep.AppendLine(aRaceNote != null
                    ? $"- reason: {aRaceNote} ⇒ **一個位元組都沒寫**，沒有任何格子被簽名。"
                    : (aFound ? "- reason: 鎖內判定不成立 ⇒ **零寫入**。" : $"- reason: 鎖內重讀時 TASK-{aIndex:0000} 不在了 ⇒ **寫入沒有發生**。"));
                throw new SCP_TaskOpException("[Task] check 沒有落檔（鎖內重判：序號位移或單子不在）");
            }
            string aBack = SCP_TaskStore.ReadCriteria(c.Root, aIndex);
            int aBackDone = SCP_TaskStore.ListCheckedCriteria(aBack).Count;
            int aBackOpen = SCP_TaskStore.ListUncheckedCriteria(aBack).Count;
            c.R.Wrote = 1;
            c.R.Headline = $"✓ {e.Id} 勾了 {aHit.Count} 格（已勾 {aBackDone}／未勾 {aBackOpen}）";
            c.Rep.AppendLine($"## ✅ 勾了 {aHit.Count} 格（署名 `{c.Actor}` {aNowLocal.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}）");
            foreach (string s in aHit) c.Rep.AppendLine($"- [x] {Trunc(s, 160)}");
            c.Rep.AppendLine();
            c.Rep.AppendLine($"- 已勾 {aDone.Count} → **{aBackDone}**　未勾 {aOpen.Count} → **{aBackOpen}**（⭐ 這兩個後值是**回讀單檔**數的，不是寫入端的回傳值）");
            c.Rep.AppendLine("- ⚠ 勾**不會**推進 status —— 結單仍走 `op=resolve`（本 op 只動那一欄）。");
        }

        // ===========================================================
        // 區塊職責：建立／解除關聯（雙向）。`target` 收 TASK-0008 / 8 / 0008；`remove=1` 解除。
        // ===========================================================
        static void OpLink(Ctx c)
        {
            Require(c, out int aIndex);
            string aKind = Norm(c.Arg("op_link", "blocked_by"));
            string aTargetRaw = c.Trim("target");
            if (aTargetRaw.Length == 0) throw new SCP_TaskOpException("[Task] op=link 需要 --arg target=<對方單號>（收 TASK-0008 / 8 / 0008）");
            int aTarget = SCP_TaskStore.ParseTaskRef(aTargetRaw);
            if (aTarget <= 0)
                throw new SCP_TaskOpException($"[Task] op=link 認不得的 target 參照 '{aTargetRaw}'（收 TASK-0008 / 8 / 0008）—— ⛔ 這不是「沒帶」，是**帶了但讀不出來**，不猜。");
            bool aRemove = c.Trim("remove") == "1";
            string aErr;
            bool aChanged = aRemove
                ? SCP_TaskStore.Unlink(c.Root, aIndex, aTarget, aKind, c.Actor, out aErr)
                : SCP_TaskStore.Link(c.Root, aIndex, aTarget, aKind, c.Actor, out aErr);
            c.R.Index = aIndex;
            if (aErr.Length > 0)
            {
                c.Rep.AppendLine("## blocked");
                c.Rep.AppendLine($"- reason: {aErr}");
                throw new SCP_TaskOpException($"[Task] {(aRemove ? "unlink" : "link")} 失敗：{aErr}");
            }
            SCP_TaskEntry? a = SCP_TaskIO.Find(c.Root, aIndex);
            SCP_TaskEntry? b = SCP_TaskIO.Find(c.Root, aTarget);
            if (aChanged) c.R.Wrote = 2;
            string aHead = aRemove
                ? (aChanged ? "✅ 已解除關聯（雙向對稱移除，時間線兩邊都留了一筆）" : "（這個關聯本來就不存在，沒有東西可解）")
                : (aChanged ? "✅ 已建立關聯" : "（關聯本來就存在，沒有重複寫）");
            c.R.Headline = $"{(aChanged ? "✓" : "·")} {aKind}：TASK-{aIndex:0000} ↔ TASK-{aTarget:0000}　{aHead}";
            c.Rep.AppendLine($"## {aHead}");
            c.Rep.AppendLine($"- `{aKind}`：{a?.Id} ↔ {b?.Id}");
            c.Rep.AppendLine("- 回讀（**雙向都要有，單向寫入是靜默錯**）:");
            if (a != null) c.Rep.AppendLine($"    · {a.Id}: {RelationLine(a)}");
            if (b != null) c.Rep.AppendLine($"    · {b.Id}: {RelationLine(b)}");
            if (aKind == "blocked_by" && b != null && b.participants.Count == 0 && !aRemove)
            {
                c.Rep.AppendLine($"- ⚠ **{b.Id} 沒有任何參與者，而它現在卡著 {a?.Id}。** 沒有人在解的 blocker 會讓被卡的單永久停住。");
                c.Rep.AppendLine($"  ⇒ 指派一個人去解它：`{Cmd($"--arg op=assign --arg persona={c.Actor} --arg index={b.index} --arg target_persona=<誰> --arg role=dev")}`");
            }
        }

        // ===========================================================
        // 區塊職責：結單 —— 三道閘（blocker / QA / confirm），一道都不留旁路；三道閘在鎖內重判一次。
        // ===========================================================
        static void OpResolve(Ctx c)
        {
            SCP_TaskEntry e = Require(c, out int aIndex);
            var aStatus = ParseEnum(c, "status", SCP_TaskStatus.done);
            if (aStatus != SCP_TaskStatus.done && aStatus != SCP_TaskStatus.cancelled)
                throw new SCP_TaskOpException($"[Task] resolve 的 status 只能是 done|cancelled（收到 '{aStatus}'）");
            string aNote = c.Trim("note");
            string aQaNote = c.Trim("qa_note");
            c.R.Index = aIndex;
            c.Rep.AppendLine($"## resolve 前的閘（{e.Id} `{e.status}` → `{aStatus}`）");

            List<string> aBlockers = SCP_TaskStore.OpenBlockers(c.Root, e);
            if (aStatus == SCP_TaskStatus.done && aBlockers.Count > 0)
            {
                c.Rep.AppendLine($"- 🛑 **擋下**：還有 {aBlockers.Count} 個未解 blocker —— {string.Join("；", aBlockers)}");
                c.Rep.AppendLine("  這是機械攔截：blocker 沒解而推 Done，等於宣告一件還做不到的事已經完成。");
                throw new SCP_TaskOpException($"[Task] resolve 擋下：{e.Id} 還有 {aBlockers.Count} 個未解 blocker");
            }
            c.Rep.AppendLine(aBlockers.Count == 0 ? "- ✅ blocker 閘：沒有未解的 blocker"
                : $"- ⚠ blocker 閘：有 {aBlockers.Count} 個未解，但 status=cancelled ⇒ 放行（取消一張被卡的單是合理的）");

            string? aQaBlock = SCP_TaskStore.QaGateBlocked(e, c.Actor, aQaNote);
            if (aStatus == SCP_TaskStatus.done && aQaBlock != null)
            {
                c.Rep.AppendLine($"- 🛑 **擋下（QA 閘）**：{aQaBlock}");
                throw new SCP_TaskOpException("[Task] resolve 擋下：QA 未簽");
            }
            List<string> aQa = e.QaPersonas();
            c.Rep.AppendLine(aQa.Count == 0
                ? "- ⚠ QA 閘：**這張單沒有指名 QA** ⇒ 沒有閘可以擋（開單時就沒有人被指名驗收）"
                : $"- ✅ QA 閘：{string.Join(" / ", aQa)}" + (aQaNote.Length > 0 ? $"（代簽，附驗收紀錄：{aQaNote}）" : "（本人結單）"));

            if (c.Trim("confirm") != "1")
            {
                c.R.Headline = $"· {e.Id} resolve dry-run（沒帶 confirm=1，零寫入）";
                c.Rep.AppendLine("- 🛑 **dry-run**（沒帶 `confirm=1`）⇒ 什麼都沒寫。上面兩道閘的結果是真的讀數，重跑同一道指令加 `--arg confirm=1` 才會真的結單。");
                return;
            }

            string aNow = SCP_TaskStore.NowUtc();
            var aFrom = e.status;
            string? aRaceBlock = null;
            bool aWrote = SCP_TaskStore.Mutate(c.Root, aIndex, m =>
            {
                List<string> aNowBlockers = SCP_TaskStore.OpenBlockers(c.Root, m);
                if (aStatus == SCP_TaskStatus.done && aNowBlockers.Count > 0)
                { aRaceBlock = $"鎖內重讀時它有 {aNowBlockers.Count} 個未解 blocker（{string.Join("；", aNowBlockers)}）—— 上面那道閘讀的是鎖外的快照"; return SCP_TaskWrite.Skip; }
                string? aNowQa = SCP_TaskStore.QaGateBlocked(m, c.Actor, aQaNote);
                if (aStatus == SCP_TaskStatus.done && aNowQa != null)
                { aRaceBlock = $"鎖內重讀時 QA 閘不放行：{aNowQa}（有人在這中間改了參與者）"; return SCP_TaskWrite.Skip; }
                aFrom = m.status;
                m.status = aStatus;
                m.closed_at = aNow;
                if (aNote.Length > 0) m.resolution_note = aNote;
                if (aQaNote.Length > 0)
                {
                    // 讀取層不解析結單說明 ⇒ 先撈磁碟那一份再接（否則代簽紀錄會把既有說明蓋掉）
                    if (m.resolution_note.Length == 0) m.resolution_note = SCP_TaskStore.ReadSection(SCP_TaskIO.TaskPath(c.Root, aIndex), "## 結單說明");
                    m.resolution_note = (m.resolution_note + "\n\n**QA 代簽紀錄**：" + aQaNote).Trim();
                }
                m.updated_at = aNow;
                return SCP_TaskWrite.Line($"{aNow}　`{aStatus}`　{c.Actor} 結單（原狀態 {aFrom}）"
                    + (aNote.Length == 0 ? "" : $"：{aNote.Replace("\r", " ").Replace("\n", " ")}"));
            }, out bool aFound);
            if (!aWrote)
            {
                c.Rep.AppendLine("## blocked");
                c.Rep.AppendLine(aRaceBlock != null
                    ? $"- reason: {aRaceBlock} ⇒ **一個位元組都沒寫**，單子沒有被關。"
                    : (aFound ? "- reason: 鎖內判定不成立 ⇒ **零寫入**。" : $"- reason: 鎖內重讀時 TASK-{aIndex:0000} 不在了 ⇒ **寫入沒有發生**。"));
                throw new SCP_TaskOpException("[Task] resolve 沒有落檔（鎖內重判）");
            }
            e = SCP_TaskIO.Find(c.Root, aIndex) ?? e;
            c.R.Wrote = 1;
            c.R.Headline = $"✓ {e.Id} 已結單：{aFrom} → {aStatus}";
            c.Rep.AppendLine();
            c.Rep.AppendLine($"## ✅ {e.Id} 已結單");
            c.Rep.AppendLine($"- {aFrom} → **{aStatus}**　closed_at: {e.closed_at}");
            if (aStatus == SCP_TaskStatus.done) AppendUncheckedCriteria(c, aIndex);
            if (e.blocks.Count > 0)
            {
                c.Rep.AppendLine($"- ▶ 它本來卡著 {Ids(e.blocks)} —— 那幾張現在可能可以動了（去看一眼）：");
                foreach (int i in e.blocks)
                {
                    SCP_TaskEntry? b = SCP_TaskIO.Find(c.Root, i);
                    if (b != null && !b.IsClosed())
                        c.Rep.AppendLine($"    · {b.Id} `{b.status}` {b.title}　剩餘 blocker: {SCP_TaskStore.OpenBlockers(c.Root, b).Count}");
                }
            }
            Notify(c, e, "status", $"{aFrom} → **{aStatus}**" + (aNote.Length == 0 ? "" : $"：{aNote}"));
        }

        /// <summary>關單那一刻把「驗收標準還有幾格沒勾」連原文印出來（警示不是擋 —— 有些格是互斥分支）。</summary>
        static void AppendUncheckedCriteria(Ctx c, int iIndex)
        {
            string aCriteria = SCP_TaskStore.ReadCriteria(c.Root, iIndex);
            List<string> aOpen = SCP_TaskStore.ListUncheckedCriteria(aCriteria);
            int aDone = SCP_TaskStore.ListCheckedCriteria(aCriteria).Count;
            if (aOpen.Count == 0)
            {
                if (aDone == 0) c.Rep.AppendLine("- ⚠ **這張單沒有任何勾選格** ⇒ 這不是「驗收過了」，是**結構上沒有東西可以驗**（看板分不出這兩件事）");
                return;
            }
            c.Rep.AppendLine($"- ⚠ **驗收標準還有 {aOpen.Count} 格沒勾**（已勾 {aDone} / 共 {aDone + aOpen.Count}）—— 關單的這一刻，沒有人簽過它們：");
            int aShow = Math.Min(aOpen.Count, 6);
            for (int i = 0; i < aShow; ++i) c.Rep.AppendLine("    · " + Trunc(aOpen[i], 110));
            if (aOpen.Count > aShow)
                c.Rep.AppendLine($"    · …還有 {aOpen.Count - aShow} 格（全部：`{Cmd($"--arg op=check --arg persona=<你> --arg index={iIndex}")}`）");
            c.Rep.AppendLine("  ⛔ 這不是擋 —— 有些格是**互斥分支**，本來就該留空。⚠ 而「刻意留空」與「還沒做」機器分不出來 ⇒ **那一格由你判**。");
        }

        // ===========================================================
        // 區塊職責：commit 訊息 `Fixes TASK-n` / `Refs TASK-n` 的落地端（由 `senate cmd commit` 呼叫）。
        // 物理意義：**狀態機只有一份**。refs ⇒ 只追加 sha；fixes ⇒ blocker 存在不推進／有 QA → in_review／沒 QA → done。
        //   已關的單只追加 sha 並明說（不靜默重開、不假裝有推進）。
        // ===========================================================
        static void OpCommit(Ctx c)
        {
            SCP_TaskEntry e = Require(c, out int aIndex);
            string aSha = c.Trim("sha");
            if (aSha.Length == 0) throw new SCP_TaskOpException("[Task] op=commit 需要 --arg sha=<commit SHA>");
            string aMode = Norm(c.Arg("mode", "fixes"));
            if (aMode != "fixes" && aMode != "refs") throw new SCP_TaskOpException($"[Task] op=commit 的 mode 只能是 fixes|refs（收到 '{aMode}'）");
            string aNow = SCP_TaskStore.NowUtc();
            bool aShaNew = false;
            var aFrom = e.status;
            string aVerdict = "";
            bool aWrote = SCP_TaskStore.Mutate(c.Root, aIndex, m =>
            {
                aShaNew = !m.commit_shas.Contains(aSha);
                if (aShaNew) m.commit_shas.Add(aSha);
                aFrom = m.status;
                List<string> aBlockers = SCP_TaskStore.OpenBlockers(c.Root, m);
                if (m.IsClosed()) aVerdict = $"這張單已經是 `{aFrom}` ⇒ **只追加 sha，狀態不動**（不靜默重開）";
                else if (aMode == "refs") aVerdict = "`Refs` ⇒ 只追加 sha，狀態不動（這是 Refs 的定義，不是失敗）";
                else if (aBlockers.Count > 0)
                    aVerdict = $"🛑 **不推進**：還有 {aBlockers.Count} 個未解 blocker —— {string.Join("；", aBlockers)}。commit 不是特權通道，機械閘照樣生效。";
                else
                {
                    List<string> aQa = m.QaPersonas();
                    if (aQa.Count > 0)
                    {
                        m.status = SCP_TaskStatus.in_review;
                        aVerdict = $"→ **in_review**（單上有 QA：{string.Join(" / ", aQa)} —— commit 不能替 QA 簽名）";
                    }
                    else
                    {
                        m.status = SCP_TaskStatus.done;
                        m.closed_at = aNow;
                        aVerdict = "→ **done**（這張單沒有指名 QA ⇒ 沒有人要驗，commit 直接結）";
                        // 落差要出聲（TASK-0015）：有 dev 以外的角色卻沒有 qa ⇒「沒有人要驗」這個假設要攤開
                        var aNonDev = m.participants.Where(p => p.role != SCP_TaskRole.dev).Select(p => $"{p.persona}({p.role})").Distinct().ToList();
                        if (aNonDev.Count > 0)
                            aVerdict += $"\n  ⚠ **本單沒有 QA 卻有其他角色：{string.Join("、", aNonDev)}** —— 若非預期請 reopen 並補 `op=assign --arg role=qa`"
                                + "（`pm` 不是 QA 閘：PM 排序、QA 簽名）";
                    }
                }
                m.updated_at = aNow;
                return SCP_TaskWrite.Line($"{aNow}　`{m.status}`　commit `{aSha}`（{aMode}）by {c.Actor}" + (aShaNew ? "" : "（這個 sha 本來就在，沒重複加）"));
            });
            if (!aWrote)
                throw new SCP_TaskOpException($"[Task] TASK-{aIndex:0000} 掛 commit 沒有落檔 —— 鎖內重讀時那張單不在了 ⇒ **寫入沒有發生**，那顆 sha 沒有掛上去。");
            e = SCP_TaskIO.Find(c.Root, aIndex) ?? e;
            c.R.Index = aIndex; c.R.Wrote = 1;
            c.R.Values["status"] = e.status.ToString();
            c.R.Values["from_status"] = aFrom.ToString();
            c.R.Headline = $"✓ {e.Id} ← commit {aSha}（{aMode}）：{aFrom}" + (aFrom == e.status ? "（不變）" : $" → {e.status}");
            c.Rep.AppendLine($"## {e.Id} ← commit `{aSha}`（mode=`{aMode}`）");
            if (!aShaNew) c.Rep.AppendLine("- ♻ **這顆 sha 本來就在單上，這次呼叫沒有改變 `commit_shas`**（重複掛載，不是新進度）");
            c.Rep.AppendLine($"- 狀態: `{aFrom}` {(aFrom == e.status ? "（不變）" : $"→ `{e.status}`")}");
            c.Rep.AppendLine($"- 判定: {aVerdict}");
            if (aFrom != e.status) AppendUncheckedCriteria(c, aIndex);
            c.Rep.AppendLine($"- commit_shas 回讀: {string.Join(" ", e.commit_shas)}" + (aShaNew ? "" : $"（{e.commit_shas.Count} 顆，本次 0 新增）"));
            if (e.status == SCP_TaskStatus.in_review)
            {
                List<string> aQa = e.QaPersonas();
                c.Rep.AppendLine($"- ▶ 等 QA 結單：`{Cmd($"--arg op=resolve --arg persona=<QA> --arg index={e.index} --arg status=done --arg note=<驗收讀數> --arg confirm=1")}`");
                c.Rep.AppendLine($"  （要由 {string.Join(" / ", aQa)} 跑；別人跑要帶 `--arg qa_note=`）");
            }
            // ⚠ 只有**狀態真的變了**才通知（一則說「什麼都沒發生」的訊息會訓練大家忽略這個 tag）
            if (aFrom != e.status) Notify(c, e, "status", $"{aFrom} → **{e.status}**（commit `{aSha}`）");
            else c.Rep.AppendLine("- 📣 狀態沒有變 ⇒ **不發酒館通知**");
        }

        // ===========================================================
        // 區塊職責：收工（「我今天不做了」）—— progress 進留言、`last_wrapup_at` 與 `updated_at` 同一次寫入；
        //   why ⇒ 交回呼叫端寫進工作記憶（`SCP_WorkMemory`）。**不改 status。**
        // ⚠ 等號陷阱（TASK-0036）：`last_wrapup_at` 與 `Touch` 共用同一個 aNow ⇒ 寫完當下必然相等（晚安閘用嚴格大於）。
        // ===========================================================
        static void OpWrapup(Ctx c)
        {
            SCP_TaskEntry e = Require(c, out int aIndex);
            string aProgress = c.Trim("progress");
            string aWhy = c.Trim("why");
            c.R.Index = aIndex;
            if (aProgress.Length == 0)
            {
                c.Rep.AppendLine("## blocked");
                c.Rep.AppendLine("- reason: `progress` 必填 —— **收工的意義就是「還剩什麼、下一步從哪接」**。用法：`--arg-file progress=<檔>`");
                throw new SCP_TaskOpException("[Task] wrapup 缺 progress");
            }
            string aTopic = (e.memory_topic ?? "").Trim();
            if (aWhy.Length > 0 && aTopic.Length == 0)
            {
                c.Rep.AppendLine("## blocked");
                c.Rep.AppendLine($"- reason: 給了 `why` 但 {e.Id} 沒有 `memory_topic` —— **我不猜主題名**。");
                c.Rep.AppendLine($"  先設：`{Cmd($"--arg op=update --arg persona={c.Actor} --arg index={e.index} --arg memory_topic=<主題>")}`，或這次只寫 progress。");
                throw new SCP_TaskOpException("[Task] wrapup: 有 why 但沒有 memory_topic");
            }
            string aType = Norm(c.Arg("memory_type", "pitfall"));
            if (aType != "pitfall" && aType != "decision" && aType != "knowhow") aType = "pitfall";
            string aNow = SCP_TaskStore.NowUtc();
            int aCommentId = 0, aPrevComments = 0;
            var aFrom = e.status;
            string aTopicAtWrite = aTopic;
            bool aTopicLost = false;
            bool aWrote = SCP_TaskStore.Mutate(c.Root, aIndex, m =>
            {
                aTopicAtWrite = (m.memory_topic ?? "").Trim();
                if (aWhy.Length > 0 && aTopicAtWrite.Length == 0) { aTopicLost = true; return SCP_TaskWrite.Skip; }
                aFrom = m.status;
                aCommentId = SCP_TaskStore.NextCommentId(m);
                aPrevComments = m.comments.Count;
                m.comments.Add(new SCP_TaskComment { id = aCommentId, persona = c.Actor, at = aNow, body = "**[收工 wrapup]**\n\n" + aProgress });
                m.updated_at = aNow;
                m.last_wrapup_at = aNow;
                return SCP_TaskWrite.Line($"{aNow}　`wrapup`　{c.Actor} 收工（狀態不動：{aFrom}）留言 #{aCommentId}");
            });
            if (!aWrote)
            {
                c.Rep.AppendLine("## blocked");
                c.Rep.AppendLine(aTopicLost
                    ? $"- reason: 鎖內重讀時 {e.Id} 的 `memory_topic` 是空的（鎖外讀到的是 `{aTopic}`，中間被清掉了）⇒ **一個位元組都沒寫**。"
                    : $"- reason: 鎖內重讀時 TASK-{aIndex:0000} 不在了 ⇒ **寫入沒有發生**。");
                throw new SCP_TaskOpException("[Task] wrapup 沒有落檔（鎖內重讀）");
            }
            c.R.Wrote = 1;
            c.R.Headline = $"✓ {e.Id} 已收工（留言 #{aCommentId}，狀態維持 {aFrom}）";
            c.Rep.AppendLine($"## ✅ {e.Id} 已收工（`wrapup`）");
            c.Rep.AppendLine($"- 狀態：**維持 `{aFrom}`** —— 收工不是結單也不是放棄");
            c.Rep.AppendLine($"- 進度寫進留言 #{aCommentId}（進度真相源是 Task）");
            c.Rep.AppendLine();
            c.Rep.AppendLine("```markdown");
            c.Rep.AppendLine(aProgress);
            c.Rep.AppendLine("```");
            AppendRefWarnings(c, aProgress, aPrevComments, SCP_TaskStore.ReadCriteria(c.Root, aIndex));
            if (aWhy.Length > 0)
            {
                c.R.Memory = new SCP_TaskMemoryRequest
                {
                    Topic = aTopicAtWrite, Type = aType,
                    Id = $"{aType}_wrapup-{e.index:0000}-{DateTime.UtcNow.ToString("yyyyMMddHHmm", CultureInfo.InvariantCulture)}",
                    Title = $"收工紀錄 {e.Id}：{Trunc(e.title, 40)}", Body = aWhy, By = c.Actor,
                };
            }
            else
            {
                c.Rep.AppendLine();
                c.Rep.AppendLine("- 🧠 沒帶 `why` ⇒ 沒寫記憶（**這是合法的** —— 為了通關而寫的記憶比沒有更糟）");
            }
            SCP_TaskEntry aFresh = SCP_TaskIO.Find(c.Root, aIndex) ?? e;
            Notify(c, aFresh, "comment", "", "**[收工 wrapup]**\n\n" + aProgress);
        }

        // ===========================================================
        // 區塊職責：晚安收工閘的**顯式跳過** —— 把理由寫進那張單的時間線。
        // 物理意義：跳過要留在**別人看得到的地方**（basecamp 拍板：可跳過但留名，比不可跳過更持久；TASK-0349 ③）。
        // ===========================================================
        static void OpWrapupSkip(Ctx c)
        {
            Require(c, out int aIndex);
            string aReason = c.Trim("reason");
            if (aReason.Length == 0) throw new SCP_TaskOpException("[Task] op=wrapup_skip 需要 --arg reason=<為什麼跳過收工>");
            string aNow = SCP_TaskStore.NowUtc();
            bool aWrote = SCP_TaskStore.Mutate(c.Root, aIndex, m =>
            {
                m.updated_at = aNow;
                return SCP_TaskWrite.Line($"{aNow}　`wrapup-skip`　{c.Actor} 顯式跳過收工：" + aReason.Replace("\r", " ").Replace("\n", " "));
            });
            c.R.Index = aIndex;
            if (!aWrote)
                throw new SCP_TaskOpException($"[Task] TASK-{aIndex:0000} 的 `wrapup-skip` **沒有落盤**（鎖內重讀時那張單不在了）⇒ 跳過的理由沒有留在單上。");
            c.R.Wrote = 1;
            c.R.Headline = $"✓ TASK-{aIndex:0000} 已記錄跳過收工";
            c.Rep.AppendLine($"## ✅ TASK-{aIndex:0000} 已記錄 `wrapup-skip`");
            c.Rep.AppendLine($"- 理由：{aReason}");
        }

        // ===========================================================
        // 區塊職責：逾期認領的機械釋放 —— in_progress 且 ≥ STALE_DAYS 沒動 ⇒ 退回 todo（要 confirm=1）。
        //   候選清單是**提示**：每張在鎖內把判定原樣再跑一次，不成立就跳過。
        // ===========================================================
        static void OpSweep(Ctx c)
        {
            DateTime aNowUtc = DateTime.UtcNow;
            string aOnly = c.Trim("assignee");
            var aCandidates = SCP_TaskIO.LoadAll(c.Root).Where(e => !e.IsClosed() && e.status == SCP_TaskStatus.in_progress
                && e.DaysSinceUpdate(aNowUtc) >= SCP_TaskStore.STALE_DAYS
                && (aOnly.Length == 0 || e.RolesOfAnyCase(aOnly).Count > 0)).ToList();
            c.Rep.AppendLine($"## sweep（逾期認領釋放 —— in_progress 且 ≥{SCP_TaskStore.STALE_DAYS} 天沒動）");
            c.Rep.AppendLine($"- 候選 **{aCandidates.Count}** 張" + (aOnly.Length == 0 ? "（全部人）" : $"（只看 {aOnly} 參與的）"));
            if (aCandidates.Count == 0)
            {
                c.R.Headline = "· sweep：沒有逾期認領";
                c.Rep.AppendLine("- ✅ 沒有逾期認領 —— 這是「沒有候選」，不是「沒有掃」。");
                return;
            }
            foreach (SCP_TaskEntry e in aCandidates)
                c.Rep.AppendLine($"    · {e.Id} `{e.status}` {e.title}　{e.DaysSinceUpdate(aNowUtc)} 天沒動　參與：{Participants(e.participants)}");
            if (c.Trim("confirm") != "1")
            {
                c.R.Headline = $"· sweep dry-run：候選 {aCandidates.Count} 張（零寫入）";
                c.Rep.AppendLine("- 🛑 **dry-run**（沒帶 `confirm=1`）⇒ 一張都沒改。要釋放就重跑同一道指令加 `--arg confirm=1`。");
                return;
            }
            int aDone = 0, aSkipped = 0;
            foreach (SCP_TaskEntry aHint in aCandidates)
            {
                string aTs = SCP_TaskStore.NowUtc();
                var aFromCaptured = SCP_TaskStatus.todo;
                int aDaysCaptured = 0;
                bool aWrote = SCP_TaskStore.Mutate(c.Root, aHint.index, m =>
                {
                    if (m.status != SCP_TaskStatus.in_progress) return SCP_TaskWrite.Skip;
                    int aDays = m.DaysSinceUpdate(aNowUtc);
                    if (aDays < SCP_TaskStore.STALE_DAYS) return SCP_TaskWrite.Skip;
                    if (aOnly.Length > 0 && m.RolesOfAnyCase(aOnly).Count == 0) return SCP_TaskWrite.Skip;
                    aFromCaptured = m.status;
                    aDaysCaptured = aDays;
                    m.status = SCP_TaskStatus.todo;
                    m.updated_at = aTs;
                    return SCP_TaskWrite.Line($"{aTs}　`todo`　sweep 釋放（{aFromCaptured} 已 {aDaysCaptured} 天沒動作，逾期 {SCP_TaskStore.STALE_DAYS} 天門檻）by {c.Actor}");
                });
                if (!aWrote)
                {
                    aSkipped++;
                    c.Rep.AppendLine($"- ⏭ {aHint.Id} **跳過**：鎖內重讀之後它已經不符合釋放條件 ⇒ 這不是失敗，是判定在寫入時重做的結果。");
                    continue;
                }
                aDone++;
                SCP_TaskEntry aFresh = SCP_TaskIO.Find(c.Root, aHint.index) ?? aHint;
                Notify(c, aFresh, "status", $"{aFromCaptured} → **todo**（sweep：認領後 {aDaysCaptured} 天沒動，釋放回待領）");
            }
            c.R.Wrote = aDone;
            c.R.Headline = $"✓ sweep：釋放 {aDone} 張、跳過 {aSkipped} 張";
            c.Rep.AppendLine($"- ✅ 已釋放 **{aDone}** 張回 `todo`（每張的時間線都留了釋放理由）");
            if (aSkipped > 0) c.Rep.AppendLine($"- ⏭ **{aSkipped}** 張在鎖內重判時已不符條件而跳過（候選清單是提示不是判定）。");
            c.Rep.AppendLine("- ⚠ 釋放**不代表那件事不必做** —— 它只是把「有人在做」這個假讀數收回來。");
        }

        // ===========================================================
        // 區塊職責：組酒館通知 —— 本檔只組、呼叫端發。
        // 物理意義：第一行要能單獨站著；@ 名單放最後一行（參與者 ＋ 開單人 − 動手的人）。
        //   ⚠ 參與者為空時**明說**，不印一個空的 @ 行（那看起來像「已經通知了」）。
        // ===========================================================
        static void Notify(Ctx c, SCP_TaskEntry e, string iKind, string iDetail, string iCommentBody = "")
        {
            string aHead;
            switch (iKind)
            {
                case "created": aHead = $"📋 **{e.Id} 開單**（{e.type} / {e.priority}）：{e.title}"; break;
                case "status": aHead = $"📋 **{e.Id}** {iDetail}：{e.title}"; break;
                case "assigned": aHead = $"📋 **{e.Id}** 指派變動（{iDetail}）：{e.title}"; break;
                default: aHead = $"💬 **{e.Id}** 有新留言：{e.title}"; break;
            }
            var sb = new StringBuilder();
            sb.Append(aHead).Append("\n\n");
            if (iKind == "comment" && !string.IsNullOrWhiteSpace(iCommentBody)) sb.Append(iCommentBody.Trim()).Append("\n\n");
            else if (iKind == "created" && !string.IsNullOrWhiteSpace(iDetail)) sb.Append(iDetail.Trim()).Append("\n\n");
            sb.Append($"- 狀態：`{e.status}`");
            List<string> aBlockers = SCP_TaskStore.OpenBlockers(c.Root, e);
            if (aBlockers.Count > 0) sb.Append($"　🛑 未解 blocker {aBlockers.Count} 個");
            sb.Append($"　操作：{c.Actor}\n");
            sb.Append($"- 單檔：`AgentCommands/Tasks/tasks/{e.index:0000}.md`　查看：`{SCP_CmdRegistry.Invoke("tasks --arg index=" + e.index)}`\n");
            List<string> aMentions = Mentions(e, c.Actor);
            sb.Append('\n');
            if (aMentions.Count > 0) sb.Append(string.Join(" ", aMentions.Select(s => "@" + s)));
            else if (e.participants.Count == 0) sb.Append("⚠ 這張單**沒有任何參與者** ⇒ 沒有人被 @ 到（不是通知失敗，是沒有人在做這件事）");
            else sb.Append("（唯一的參與者就是操作者本人 ⇒ 沒有人需要被 @）");
            c.R.Notices.Add(new SCP_TaskNotice { Kind = iKind, TaskId = e.Id, Body = sb.ToString() });
            c.Rep.AppendLine(aMentions.Count > 0
                ? $"- 📣 酒館通知（寫完之後由入口發）將 @：{string.Join(" ", aMentions.Select(s => "@" + s))}"
                : "- 📣 酒館通知（寫完之後由入口發）**不會 @ 任何人** —— 參與者與開單人扣掉操作者之後是空的");
        }

        /// <summary>要被 @ 的人：參與者 ＋ 開單人 − 動手的人（🩸 2026-08-24：只 @ 參與者 ⇒ 開單人沒被通知到）。</summary>
        public static List<string> Mentions(SCP_TaskEntry e, string iActor)
            => e.participants.Select(p => p.persona).Concat(new[] { e.reporter })
                .Where(s => !string.IsNullOrWhiteSpace(s) && !string.Equals(s, iActor, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>門牌引用守衛（TASK-0177）的警語 —— 純輸出，不擋下。</summary>
        static void AppendRefWarnings(Ctx c, string iBody, int iPrevComments, string iCriteria)
        {
            List<string> aWarn = SCP_TaskRefCheck.Scan(iBody, iPrevComments, iCriteria);
            if (aWarn.Count == 0) return;
            c.Rep.AppendLine($"- ⚠ **門牌引用對不上（{aWarn.Count} 筆）** —— 內容**已經落檔**，這只是讀數：");
            foreach (string aLine in aWarn) c.Rep.AppendLine($"    · {aLine}");
            c.Rep.AppendLine("  ⇒ 最常見的成因是 **`index` 打錯**（收工時一口氣 wrapup 好幾張）。這句話只在**現在**這一刻說得出口 —— 事後掃全庫會系統性低報。");
        }

        // ── 小工具 ────────────────────────────────────────────────

        static SCP_TaskEntry Require(Ctx c, out int oIndex)
        {
            string aRaw = c.Trim("index");
            if (aRaw.Length == 0) throw new SCP_TaskOpException("[Task] 這個 op 需要 --arg index=<單號>（收 TASK-0008 / 8 / 0008）");
            oIndex = SCP_TaskStore.ParseTaskRef(aRaw);
            if (oIndex <= 0)
                throw new SCP_TaskOpException($"[Task] 認不得的 index 參照 '{aRaw}'（收 TASK-0008 / 8 / 0008）—— ⛔ 這不是「沒帶」，是**帶了但讀不出來**，不猜。");
            SCP_TaskEntry? e = SCP_TaskIO.Find(c.Root, oIndex);
            if (e == null)
                throw new SCP_TaskOpException($"[Task] TASK-{oIndex:0000} 不存在（單檔：{SCP_TaskIO.TaskPath(c.Root, oIndex)}）—— 「查不到」不等於「已經關掉」");
            return e;
        }

        static bool AddParticipant(SCP_TaskEntry e, string iPersona, SCP_TaskRole iRole, string iNow)
        {
            foreach (SCP_TaskParticipant p in e.participants)
                if (string.Equals(p.persona, iPersona, StringComparison.OrdinalIgnoreCase) && p.role == iRole) return false;
            e.participants.Add(new SCP_TaskParticipant { persona = iPersona, role = iRole, assigned_at = iNow });
            return true;
        }

        public static string Participants(List<SCP_TaskParticipant> iList)
        {
            if (iList == null || iList.Count == 0) return "**無**（沒有人在做這件事）";
            return string.Join("、", iList.Select(p => $"{p.persona}({p.role})"));
        }

        static string RelationLine(SCP_TaskEntry e)
            => $"blocked_by={Ids(e.blocked_by)} blocks={Ids(e.blocks)} related_to={Ids(e.related_to)}"
             + $" epic_id={(e.epic_id.Length == 0 ? "—" : e.epic_id)} subtasks={Ids(e.subtask_indices)}";

        static string Ids(List<int> iList)
            => iList == null || iList.Count == 0 ? "—" : string.Join(" ", iList.Select(i => "TASK-" + i.ToString("0000", CultureInfo.InvariantCulture)));

        /// <summary>工作記憶主題存在嗎（`<data_root>/WorkMemory/<topic>/_topic.md`）。</summary>
        static bool TopicExists(SCP_DataRoot iRoot, string iTopic)
            => iTopic.Trim().Length > 0 && File.Exists(iRoot.Value + "/WorkMemory/" + iTopic.Trim() + "/_topic.md");

        static int CountTextLines(string iCriteria)
        {
            if (string.IsNullOrWhiteSpace(iCriteria)) return 0;
            int n = 0;
            foreach (string aLine in iCriteria.Replace("\r", "").Split('\n')) if (aLine.Trim().Length > 0) n++;
            return n;
        }

        static string Trunc(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        static string FirstLine(string s)
        {
            string t = (s ?? "").Replace("\r", "");
            int i = t.IndexOf('\n');
            return i < 0 ? t : t.Substring(0, i);
        }

        /// <summary>正規化：小寫、`-`／空白 → `_`（`in-progress` 吃成 `in_progress`）。</summary>
        static string Norm(string iRaw) => (iRaw ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_");

        /// <summary>enum 參數解析：打錯字**當場炸並列出合法值**（舊版裸字串照單全收 ⇒ 落盤成一張篩選查不到的單）。</summary>
        static T ParseEnum<T>(Ctx c, string iKey, T iDefault) where T : struct, Enum
        {
            string v = Norm(c.Arg(iKey));
            if (v.Length == 0) return iDefault;
            if (SCP_TaskWire.TryParse(v, out T aV)) return aV;
            throw new SCP_TaskOpException($"[Task] --arg {iKey}={v} 不是合法值（{string.Join("|", Enum.GetNames(typeof(T)))}）");
        }

        static List<string> SplitList(string iRaw)
        {
            var aOut = new List<string>();
            foreach (string p in (iRaw ?? "").Split(','))
            {
                string t = p.Trim();
                if (t.Length > 0) aOut.Add(t);
            }
            return aOut;
        }
    }
}
