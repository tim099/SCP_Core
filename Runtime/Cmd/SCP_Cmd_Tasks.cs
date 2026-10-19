// 區塊職責：`cmd tasks` —— 任務單的**唯讀**查詢。
// 物理意義：任務單是 `Tasks/tasks/<index>.md`（frontmatter markdown），磁碟就是真相源；
//           早安 brief 的 §9／§6 也要這份讀數。
// 數值影響：純讀。⛔ **本 Cmd 不寫任何東西** —— 開單／改狀態／配號走 `senate cmd task`
//           （寫入端是 Senate Server 的 `task-write`，TASK-0349；理由在 `SCP_TaskStore` 檔頭）。
//
// ⚠ 回傳形狀照 Tim 2026-08-31 拍板：**values 只放平的純量；巢狀資料走寫檔（JSON）**。
//   ⇒ `--arg out_json=<路徑>` 才落 JSON，路徑進 outputs；不給就只印人讀的摘要與純量。
//   不變式：**摘要與 JSON 同源同一份資料**（同一次 LoadAll 的結果），不是各算一次。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Paths;
using SCP.Core.Tasks;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Tasks : SCP_Cmd
    {
        public override string Name => "tasks";
        public override string Category => SCP_CmdCategory.Task;

        public override string Summary => "任務單唯讀查詢：計數／清單／單張；可落一份 JSON 給程式讀";

        public override string Details =>
            "資料源＝`<data_root>/Tasks/tasks/*.md`（frontmatter markdown），磁碟即事實。\n"
            + "⛔ **只讀**：開單／改狀態／留言／勾格請走 `" + SCP_CmdRegistry.Invoke("task") + "`"
            + "（寫入端是 Senate Server —— TASK-0349）\n"
            + "⚠ 壞欄位（例如 status 寫著篩選成員 `all`）會**出聲**並落回預設，不靜默接受。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("tasks --arg persona=summit");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("index", "只看某一張單（給了就印那張的細節）"),
            new SCP_CmdArgSpec("persona",
                "以誰的視角統計（選填）。印兩把尺：**參與**（我在 participants 裡）與"
                + "**涉及**（我開的 ＋ 參與 —— 早安 brief §2.5／§6 用的是這把）"),
            new SCP_CmdArgSpec("mine",
                "`1` ＝ 清單只列**我涉及**的單（我開的 ＋ 參與），需要 persona；"
                + "與 status／type 是 AND。早安 brief §2.5 指路的就是這一格"),
            new SCP_CmdArgSpec("status",
                "只列這個狀態。合法值＝`SCP_TaskStatus` 全體："
                + "backlog / todo / in_progress / in_review / done / cancelled，"
                + "外加兩個**篩選成員**：`all`（全部）／`open`（未關的）。"
                + "⛔ 認不得的值**不會靜默回 0 筆** —— 出聲並 exit 2（同 `senate ui --page` 的語意）"),
            new SCP_CmdArgSpec("type",
                "只列這個種類。合法值＝`SCP_TaskType` 全體："
                + "feature / improvement / refactor / spike / subtask / bug / epic，"
                + "外加一個**篩選成員**：`all`（全部）。"
                + "⛔ 認不得的值同 status：出聲並 exit 2，不回「0 張」。"
                + "可與 status 同時給（兩個條件是 AND）"),
            new SCP_CmdArgSpec("project",
                "只列這個專案的單（TASK-0618；比母單更上一層，例：`Blockside`）。照**有效專案**算："
                + "子單沒標 ⇒ 跟著母單。比對不分大小寫；`none` ＝ 沒有專案的。與 status／type／mine 是 AND"),
            new SCP_CmdArgSpec("out_json", "把完整資料落成 JSON 的路徑（巢狀資料走檔案，不進 values）"),
            new SCP_CmdArgSpec("wrapup",
                "`1` ＝ 改印**收工閘**候選（這次上線後動過／還開著／我是參與者／收工紀錄已過期）。"
                + "需要 persona"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            var aResult = new SCP_CmdResult();
            string aDataRoot = iArgs.Get("data_root");
            var aRoot = new SCP_DataRoot(aDataRoot);
            string aTasksDir = SCP_TaskIO.TasksDir(aRoot);
            // 區塊職責：把「呼叫端打的字」換成「實際讀的那個目錄」
            // 物理意義：`--arg data_root=<相對值>` 是照 **process cwd** 解析的（實測：同一個
            //          `data_root=AgentCommands` 在專案根讀得到 193 張、在 `D:/Unity` 找不到），
            //          而宿主補的那條是設定檔的絕對路徑 —— 同一個參數名、兩個基準。
            // 🩸 逐字回音會讓兩棵樹上的成功輸出**一模一樣**：`· 資料源：AgentCommands/Tasks/tasks`
            //   在 Bar 與 LY 底下同形，而它讀的是不同的單。定語不能靠讀的人自己補。
            // ⛔ 射程：這裡只改**回音**（顯示層）。統一兩條路的解析基準是契約變更，不在本刀。
            string aTasksDirAbs = AbsOrRaw(aTasksDir);
            if (!Directory.Exists(aTasksDir))
                // 「資料根設錯」與「還沒有人開單」是兩件事 —— 印出路徑讓人自己分辨。
                return SCP_CmdResult.Fail(1,
                    "✗ 找不到任務單目錄：" + aTasksDirAbs,
                    "  （資料根：" + aDataRoot + " ⇒ 解析後 " + AbsOrRaw(aDataRoot) + "）");

            // 壞欄位的聲音要離開私有欄位 —— 收進輸出，不是丟掉。
            var aWarnings = new List<string>();
            List<SCP_TaskEntry> aAll = SCP_TaskIO.LoadAll(aRoot, aWarnings.Add);

            string aIndexArg = iArgs.Get("index");
            if (aIndexArg.Length > 0)
            {
                if (!int.TryParse(aIndexArg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aIndex))
                    return SCP_CmdResult.Fail(2, "✗ index 不是整數：" + aIndexArg);
                SCP_TaskEntry? aOne = null;
                foreach (SCP_TaskEntry e in aAll) if (e.index == aIndex) { aOne = e; break; }
                if (aOne == null)
                    return SCP_CmdResult.Fail(1, "✗ 找不到 TASK-"
                        + aIndex.ToString("0000", CultureInfo.InvariantCulture)
                        + "（掃到 " + aAll.Count + " 張，最大 index="
                        + SCP_TaskIO.ReadMaxIndexOnDisk(aRoot) + "）");
                AppendOne(aResult, aRoot, aOne, aWarnings, SCP_TaskProjects.Effective(aAll)[aOne.index]);
                EmitJson(aResult, iArgs, new List<SCP_TaskEntry> { aOne });
                return aResult;
            }

            string aPersona0 = iArgs.Get("persona");
            if (iArgs.Get("wrapup").Trim() == "1")
            {
                if (aPersona0.Length == 0)
                    return SCP_CmdResult.Fail(2, "✗ wrapup=1 需要 --arg persona=<誰>",
                        "  收工閘是「**我**這次上線後動過的單」—— 沒有 persona 這個問題沒有答案");
                AppendWrapup(aResult, aRoot, aPersona0, aAll, aWarnings);
                return aResult;
            }

            string aStatusFilter = iArgs.Get("status");
            // ===========================================================
            // `all` / `open` 是 `SCP_TaskStatus` 的前兩格 —— **篩選成員，不可落盤**
            //   （Tim 2026-08-26 拍板不另開第二個 enum）。
            //   ⇒ 它們**不會出現在任何單檔的 status 上**，所以純字面比對永遠回 0 筆。
            // 🩸 血證 2026-09-16（TASK-0221）：`--arg status=open` 印「0 張」，
            //   而**同一畫面**的 `open_count` 是 15 —— 一則輸出裡兩個數字講相反的事，沒有一層出聲。
            //   ⛔ 而「未實作」跟「真的沒有符合的單」在輸出上完全同形。
            // 認不得的值走 exit 2 ＋ 印合法清單：**跟 `senate ui --page <壞 key>` 同一種語意**
            //   （靜默開在首頁會讓「打錯 key」與「那頁是空的」同形 —— 同一顆 exe 不該有兩套規矩）。
            // ⛔ 沒動預設：不給 status 仍是「全部」。改預設會動到既有呼叫端，那是拍板的事。
            // ===========================================================
            SCP_TaskStatus aStatusWanted = SCP_TaskStatus.all;
            if (aStatusFilter.Length > 0 && !SCP_TaskWire.TryParse(aStatusFilter, out aStatusWanted))
                return SCP_CmdResult.Fail(2,
                    "✗ 認不得的 status：`" + aStatusFilter + "`",
                    "  合法值：" + string.Join(" / ", Enum.GetNames(typeof(SCP_TaskStatus))),
                    "  · 其中 `all`（全部）與 `open`（未關的）是**篩選成員**，不會是任何單子的狀態",
                    "  ⛔ 不回「0 張」—— 那跟「真的沒有符合的單」長得一樣");

            // ===========================================================
            // type 篩選：跟上面 status 那一格**同一種語意**，⛔ 不另立規矩。
            // 物理意義：`SCP_TaskType` 的第一格也是 `all`（篩選成員、不落盤），
            //   所以泛型 `SCP_TaskWire.TryParse` 兩邊共用，認不得的值一律 exit 2。
            // 🩸 為什麼補這一格（2026-09-16，calli wake#52）：早安 brief §6 印的指路指令是
            //   `cmd tasks --arg data_root=<root> --arg type=bug` —— 而本 Cmd 當時**沒有 type**，
            //   照著跑當場被 ArgSpec 預檢擋下（exit 2）。⇒ 補的是「讓那條指路指令變成真的」，
            //   ⛔ 不是改 brief 去指一條較弱的路（`status=open` 列得出來但不分種類）。
            // ===========================================================
            string aTypeFilter = iArgs.Get("type");
            SCP_TaskType aTypeWanted = SCP_TaskType.all;
            if (aTypeFilter.Length > 0 && !SCP_TaskWire.TryParse(aTypeFilter, out aTypeWanted))
                return SCP_CmdResult.Fail(2,
                    "✗ 認不得的 type：`" + aTypeFilter + "`",
                    "  合法值：" + string.Join(" / ", Enum.GetNames(typeof(SCP_TaskType))),
                    "  · 其中 `all`（全部）是**篩選成員**，不會是任何單子的種類",
                    "  ⛔ 不回「0 張」—— 那跟「真的沒有符合的單」長得一樣");

            // ===========================================================
            // mine 篩選：跟上面 type 那一格**同一個理由** —— 讓 brief 的指路指令變成真的。
            // 🩸 2026-10-11（kaguya wake 24）：brief §2.5 印「todo／backlog 2 張　清單 → `cmd tasks
            //   --arg persona=kaguya`」，照著跑列出全庫 496 張，而摘要行說「參與 23 張（其中開著 0）」。
            //   兩個數都真：brief 數「我開的 ＋ 參與」，這裡只數參與；TASK-0393／0395 是她開的、
            //   她不在參與者裡。⇒ 判準收成一支 `SCP_TaskEntry.Involves`，兩邊都問它。
            // ⛔ 沒動「參與」那一行與 mine_total／mine_open 的語意 —— 既有讀取端在讀它們。
            // ===========================================================
            string aMineArg = iArgs.Get("mine").Trim();
            bool aMineOnly = aMineArg == "1";
            if (aMineArg.Length > 0 && aMineArg != "0" && aMineArg != "1")
                return SCP_CmdResult.Fail(2, "✗ mine 只收 `0`／`1`：`" + aMineArg + "`");
            if (aMineOnly && aPersona0.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ mine=1 需要 --arg persona=<誰>",
                    "  「我涉及的單」沒有 persona 這個問題沒有答案 —— ⛔ 不回全庫");

            // 專案（TASK-0618）：照有效專案篩（子單沒標 ⇒ 跟著母單），判準在 SCP_TaskProjects，⛔ 不在這裡重算
            string aProjectFilter = iArgs.Get("project").Trim();
            Dictionary<int, (string project, int from)> aEff = SCP_TaskProjects.Effective(aAll);
            SortedDictionary<string, (int open, int total)> aByProject = SCP_TaskProjects.Counts(aAll, aEff);
            if (aProjectFilter.Length > 0 && !string.Equals(aProjectFilter, SCP_TaskProjects.None, StringComparison.OrdinalIgnoreCase)
                && !aByProject.ContainsKey(aProjectFilter))
                return SCP_CmdResult.Fail(2,
                    "✗ 沒有這個專案：`" + aProjectFilter + "`",
                    "  現有：" + (aByProject.Count == 0 ? "（還沒有任何單標專案）" : string.Join(" / ", ProjectNames(aByProject))),
                    "  · `none` ＝ 沒有專案的單",
                    "  ⛔ 不回「0 張」—— 那跟「這個專案真的沒有單」長得一樣");

            string aPersona = aPersona0;
            var aRows = new List<SCP_TaskEntry>();
            int aOpen = 0, aMine = 0, aMineOpen = 0, aInvolved = 0, aInvolvedOpen = 0;
            var aByStatus = new Dictionary<string, int>();
            var aByType = new Dictionary<string, int>();
            foreach (SCP_TaskEntry e in aAll)
            {
                Bump(aByStatus, e.status.ToString());
                Bump(aByType, e.type.ToString());
                if (!e.IsClosed()) aOpen++;
                if (aPersona.Length > 0 && e.HasParticipant(aPersona))
                {
                    aMine++;
                    if (!e.IsClosed()) aMineOpen++;
                }
                bool aIsInvolved = aPersona.Length > 0 && e.Involves(aPersona);
                if (aIsInvolved)
                {
                    aInvolved++;
                    if (!e.IsClosed()) aInvolvedOpen++;
                }
                if (KeepByStatus(e, aStatusFilter, aStatusWanted)
                    && KeepByType(e, aTypeFilter, aTypeWanted)
                    && KeepByProject(aEff[e.index].project, aProjectFilter)
                    && (!aMineOnly || aIsInvolved)) aRows.Add(e);
            }

            aResult.Lines.Add("# 📋 任務單 —— 共 " + aAll.Count + " 張（開著 " + aOpen + "）");
            aResult.Lines.Add("· 資料源：" + aTasksDirAbs);
            aResult.Lines.Add("· 狀態分布：" + Describe(aByStatus));
            aResult.Lines.Add("· 種類分布：" + Describe(aByType));
            aResult.Lines.Add("· 專案（未關／總數；子單沒標算母單的）：" + DescribeProjects(aByProject));
            if (aPersona.Length > 0)
            {
                aResult.Lines.Add("· " + aPersona + " 參與 " + aMine + " 張（其中開著 " + aMineOpen + "）");
                aResult.Lines.Add("· " + aPersona + " 涉及（我開的 ＋ 參與）" + aInvolved + " 張（其中開著 "
                                  + aInvolvedOpen + "）—— 早安 brief §2.5／§6 數的是這一行");
            }
            // ⚠ 兩個篩選是 AND，所以那個張數**屬於兩者的交集** —— 要把兩個條件一起印出來。
            //   只印其中一個的話，「status=open ⇒ 1 張」看起來會像「open 只有 1 張」，
            //   而真相是「open 且 type=bug 有 1 張」：同一個數字，兩種讀法。
            if (aStatusFilter.Length > 0 || aTypeFilter.Length > 0 || aMineOnly || aProjectFilter.Length > 0)
            {
                var aCond = new List<string>();
                if (aStatusFilter.Length > 0) aCond.Add("status=" + aStatusFilter);
                if (aTypeFilter.Length > 0) aCond.Add("type=" + aTypeFilter);
                if (aProjectFilter.Length > 0) aCond.Add("project=" + aProjectFilter);
                if (aMineOnly) aCond.Add(aPersona + " 涉及");
                aResult.Lines.Add("· 篩選 " + string.Join(" 且 ", aCond.ToArray())
                                  + " ⇒ " + aRows.Count + " 張");
            }
            aResult.Lines.Add("");
            foreach (SCP_TaskEntry e in aRows)
                aResult.Lines.Add("- " + e.Id + "　" + e.status + "／" + e.type + "／" + e.priority
                                  + ProjectTag(aEff[e.index]) + "　" + e.title);

            AppendWarnings(aResult, aWarnings);
            aResult.AddValue("task_total", aAll.Count.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("open_count", aOpen.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("listed_count", aRows.Count.ToString(CultureInfo.InvariantCulture));
            // 0 也印：只在非零時出現的欄位，讀者分不出「乾淨」與「沒量」。
            aResult.AddValue("bad_field_warnings", aWarnings.Count.ToString(CultureInfo.InvariantCulture));
            if (aPersona.Length > 0)
            {
                aResult.AddValue("mine_total", aMine.ToString(CultureInfo.InvariantCulture));
                aResult.AddValue("mine_open", aMineOpen.ToString(CultureInfo.InvariantCulture));
                aResult.AddValue("involved_total", aInvolved.ToString(CultureInfo.InvariantCulture));
                aResult.AddValue("involved_open", aInvolvedOpen.ToString(CultureInfo.InvariantCulture));
            }
            EmitJson(aResult, iArgs, aRows);
            return aResult;
        }

        // ===========================================================
        // 區塊職責：收工閘的 CLI 讀數 —— 判準本體在 `SCP_TaskReconcile`（**不在本檔重算**）。
        // 物理意義：這一格存在的理由是**對拍**：晚安 `goodnight-check` 的⑤（`SCP_TaskReconcileReport`）也印同一件事，
        //          兩邊該給出同一份清單。⛔ 只印不改。
        // ===========================================================
        static void AppendWrapup(SCP_CmdResult oResult, SCP_DataRoot iRoot, string iPersona,
                                 List<SCP_TaskEntry> iAll, List<string> ioWarnings)
        {
            DateTime aSince = SCP_TaskReconcile.SessionStartUtc(iRoot, iPersona, out string aOrigin);
            List<SCP_TaskEntry> aPending = SCP_TaskReconcile.PendingWrapups(iRoot, iPersona, aSince, ioWarnings.Add);
            oResult.Lines.Add("# 🛑 收工閘 —— " + iPersona);
            oResult.Lines.Add("· session 起點：" + aOrigin);
            oResult.Lines.Add("· 掃到 " + iAll.Count + " 張單 ⇒ **會擋下線的 " + aPending.Count + " 張**");
            oResult.Lines.Add("· 判準：還開著 ∧ 我是參與者 ∧ updated_at > session 起點 ∧ "
                              + "（沒收過工 ∨ updated_at > 最後一次 wrapup）—— **零日曆**");
            oResult.Lines.Add("");
            foreach (SCP_TaskEntry e in aPending)
            {
                DateTime aWrap = SCP_TaskReconcile.LastWrapupUtc(iRoot, e);
                oResult.Lines.Add("- " + e.Id + "　" + e.status + "　" + e.title);
                oResult.Lines.Add("    updated_at " + e.updated_at + "　最後收工 "
                                  + (aWrap == DateTime.MinValue ? "（從未）" : aWrap.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)));
            }
            if (aPending.Count == 0)
                oResult.Lines.Add("（沒有單會擋下線 —— 這是讀數，不是「沒量」）");
            AppendWarnings(oResult, ioWarnings);
            oResult.AddValue("task_total", iAll.Count.ToString(CultureInfo.InvariantCulture));
            oResult.AddValue("pending_wrapups", aPending.Count.ToString(CultureInfo.InvariantCulture));
            oResult.AddValue("bad_field_warnings", ioWarnings.Count.ToString(CultureInfo.InvariantCulture));
        }

        static void AppendOne(SCP_CmdResult oResult, SCP_DataRoot iRoot, SCP_TaskEntry e,
                              List<string> ioWarnings, (string project, int from) iProject)
        {
            oResult.Lines.Add("# " + e.Id + "　" + e.title);
            oResult.Lines.Add("· " + e.status + "／" + e.type + "／" + e.priority + "／severity=" + e.severity);
            oResult.Lines.Add("· 開單：" + e.reporter + "　建立 " + e.created_at + "　更新 " + e.updated_at);
            if (e.participants.Count == 0) oResult.Lines.Add("· ⚠ **尚無參與者**（沒有人在做這件事）");
            else
            {
                var aWho = new List<string>();
                foreach (SCP_TaskParticipant p in e.participants) aWho.Add(p.persona + "(" + p.role + ")");
                oResult.Lines.Add("· 參與：" + string.Join("、", aWho.ToArray()));
            }
            List<string> aBlockers = SCP_TaskIO.OpenBlockers(iRoot, e, ioWarnings.Add);
            if (aBlockers.Count > 0)
                oResult.Lines.Add("· ⛔ 還開著的阻塞：" + string.Join("、", aBlockers.ToArray()));
            if (e.memory_topic.Length > 0) oResult.Lines.Add("· 記憶主題：" + e.memory_topic);
            string aProjRoot = "", aProjDocs = "";
            if (iProject.project.Length > 0)
            {
                oResult.Lines.Add("· 專案：" + iProject.project + (iProject.from > 0
                    ? "（自己沒標，跟著母單 TASK-" + iProject.from.ToString("0000", CultureInfo.InvariantCulture) + "）" : ""));
                // 綁定的外部專案（SCP_ProjectLinks）：這張單要去哪個資料夾做事、文件在哪
                oResult.Lines.Add("  ↳ " + SCP_ProjectLinks.Describe(iProject.project, out aProjRoot, out aProjDocs));
            }
            oResult.Lines.Add("· 留言 " + e.comments.Count + " 則");
            AppendWarnings(oResult, ioWarnings);
            oResult.AddValue("index", e.index.ToString(CultureInfo.InvariantCulture));
            oResult.AddValue("status", e.status.ToString());
            oResult.AddValue("comment_count", e.comments.Count.ToString(CultureInfo.InvariantCulture));
            oResult.AddValue("open_blockers", aBlockers.Count.ToString(CultureInfo.InvariantCulture));
            oResult.AddValue("bad_field_warnings", ioWarnings.Count.ToString(CultureInfo.InvariantCulture));
            oResult.AddValue("project", iProject.project);
            oResult.AddValue("project_root", aProjRoot);     // 空 ＝ 沒標專案、沒綁、或路徑不存在（摘要那一行說是哪一種）
            oResult.AddValue("project_docs", aProjDocs);
        }

        static void AppendWarnings(SCP_CmdResult oResult, List<string> iWarnings)
        {
            if (iWarnings.Count == 0) return;
            oResult.Lines.Add("");
            oResult.Lines.Add("⚠ **壞欄位 " + iWarnings.Count + " 筆**（已落回預設值 —— 去修單檔 frontmatter）：");
            foreach (string aW in iWarnings) oResult.Lines.Add("  " + aW);
        }

        // ===========================================================
        // 區塊職責：巢狀資料落 JSON 檔（**不進 values**）。
        // 物理意義：Tim 2026-08-31 拍板 —— values 只放平的純量，巢狀走寫檔。
        //          手寫序列化的理由：這份 JSON 是**跨端契約**，欄位名就是 frontmatter 的鍵名，
        //          用反射的話改個 C# 欄位名就靜默改了契約。
        // ===========================================================
        static void EmitJson(SCP_CmdResult oResult, SCP_CmdArgs iArgs, List<SCP_TaskEntry> iRows)
        {
            string aOut = iArgs.Get("out_json");
            if (aOut.Length == 0) return;
            var sb = new StringBuilder();
            sb.Append("{\"count\":").Append(iRows.Count).Append(",\"tasks\":[");
            for (int i = 0; i < iRows.Count; i++)
            {
                if (i > 0) sb.Append(',');
                SCP_TaskEntry e = iRows[i];
                sb.Append('{');
                J(sb, "index", e.index); sb.Append(',');
                J(sb, "id", e.Id); sb.Append(',');
                J(sb, "type", e.type.ToString()); sb.Append(',');
                J(sb, "priority", e.priority.ToString()); sb.Append(',');
                J(sb, "severity", e.severity.ToString()); sb.Append(',');
                J(sb, "status", e.status.ToString()); sb.Append(',');
                J(sb, "title", e.title); sb.Append(',');
                J(sb, "reporter", e.reporter); sb.Append(',');
                J(sb, "milestone", e.milestone); sb.Append(',');
                J(sb, "project", e.project); sb.Append(',');
                J(sb, "epic_id", e.epic_id); sb.Append(',');
                J(sb, "created_at", e.created_at); sb.Append(',');
                J(sb, "updated_at", e.updated_at); sb.Append(',');
                J(sb, "closed_at", e.closed_at); sb.Append(',');
                J(sb, "last_wrapup_at", e.last_wrapup_at); sb.Append(',');
                J(sb, "memory_topic", e.memory_topic); sb.Append(',');
                J(sb, "memory_archived_commit", e.memory_archived_commit); sb.Append(',');
                JIntList(sb, "blocked_by", e.blocked_by); sb.Append(',');
                JIntList(sb, "blocks", e.blocks); sb.Append(',');
                JIntList(sb, "related_to", e.related_to); sb.Append(',');
                JIntList(sb, "subtask_indices", e.subtask_indices); sb.Append(',');
                JStrList(sb, "tags", e.tags); sb.Append(',');
                JStrList(sb, "commit_shas", e.commit_shas); sb.Append(',');
                sb.Append("\"participants\":[");
                for (int p = 0; p < e.participants.Count; p++)
                {
                    if (p > 0) sb.Append(',');
                    SCP_TaskParticipant aP = e.participants[p];
                    sb.Append('{');
                    J(sb, "persona", aP.persona); sb.Append(',');
                    J(sb, "role", aP.role.ToString()); sb.Append(',');
                    J(sb, "assigned_at", aP.assigned_at);
                    sb.Append('}');
                }
                sb.Append("],");
                J(sb, "comment_count", e.comments.Count);
                sb.Append('}');
            }
            sb.Append("]}");
            string? aDir = Path.GetDirectoryName(aOut);
            if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
            File.WriteAllText(aOut, sb.ToString(), new UTF8Encoding(false));
            oResult.AddOutput(aOut);
            oResult.Lines.Add("📄 JSON：" + aOut + "（" + iRows.Count + " 張）");
        }

        static void J(StringBuilder sb, string iKey, string iValue)
            => sb.Append('"').Append(iKey).Append("\":").Append(Esc(iValue));

        static void J(StringBuilder sb, string iKey, int iValue)
            => sb.Append('"').Append(iKey).Append("\":").Append(iValue.ToString(CultureInfo.InvariantCulture));

        static void JIntList(StringBuilder sb, string iKey, List<int> iList)
        {
            sb.Append('"').Append(iKey).Append("\":[");
            for (int i = 0; i < iList.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(iList[i].ToString(CultureInfo.InvariantCulture));
            }
            sb.Append(']');
        }

        static void JStrList(StringBuilder sb, string iKey, List<string> iList)
        {
            sb.Append('"').Append(iKey).Append("\":[");
            for (int i = 0; i < iList.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(Esc(iList[i]));
            }
            sb.Append(']');
        }

        static string Esc(string iRaw)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in iRaw ?? "")
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        // ===========================================================
        // status 篩選的唯一判準（⚠ 呼叫端已保證 iWire 解得出來 —— 解不出的在 Execute 就 exit 2 了）。
        // ⭐ `open` 走的是 `IsClosed()`，**跟頁首 `open_count` 同一支判準** ——
        //   那兩個數字本來就該一致，而它們不一致正是 TASK-0221 的病灶。
        //   ⛔ 別在這裡另寫一份「什麼叫關了」，那會長出第二個答案。
        // ===========================================================
        static bool KeepByStatus(SCP_TaskEntry iEntry, string iWire, SCP_TaskStatus iWanted)
        {
            if (iWire.Length == 0) return true;               // 不給 ＝ 全部（維持既有預設）
            if (iWanted == SCP_TaskStatus.all) return true;
            if (iWanted == SCP_TaskStatus.open) return !iEntry.IsClosed();
            return iEntry.status == iWanted;
        }

        // ===========================================================
        // type 篩選的唯一判準 —— 形狀刻意跟 `KeepByStatus` 一模一樣。
        // ⚠ 這裡**沒有** `open` 那種跨狀態的篩選成員：種類只有 `all`，
        //   因為「未關」是狀態的性質，不是種類的性質。⛔ 別在這裡混進狀態判斷，
        //   那會讓 `--arg type=` 這一格悄悄多出一個只有它自己知道的語意。
        // ===========================================================
        static bool KeepByType(SCP_TaskEntry iEntry, string iWire, SCP_TaskType iWanted)
        {
            if (iWire.Length == 0) return true;               // 不給 ＝ 全部（維持既有預設）
            if (iWanted == SCP_TaskType.all) return true;
            return iEntry.type == iWanted;
        }

        /// <summary>專案篩選（TASK-0618）：不給 ＝ 全部；`none` ＝ 有效專案是空的；其他不分大小寫比對有效專案。</summary>
        static bool KeepByProject(string iEffective, string iWire)
        {
            if (iWire.Length == 0) return true;
            if (string.Equals(iWire, SCP_TaskProjects.None, StringComparison.OrdinalIgnoreCase)) return iEffective.Length == 0;
            return string.Equals(iEffective, iWire, StringComparison.OrdinalIgnoreCase);
        }

        static List<string> ProjectNames(SortedDictionary<string, (int open, int total)> iMap)
        {
            var aOut = new List<string>();
            foreach (string k in iMap.Keys) if (k.Length > 0) aOut.Add(k);
            return aOut;
        }

        /// <summary>`Blockside=3/12／（未分類）=40/500`；未分類排最後。</summary>
        static string DescribeProjects(SortedDictionary<string, (int open, int total)> iMap)
        {
            var aParts = new List<string>();
            foreach (KeyValuePair<string, (int open, int total)> kv in iMap)
                if (kv.Key.Length > 0) aParts.Add(kv.Key + "=" + kv.Value.open + "/" + kv.Value.total);
            if (iMap.TryGetValue("", out (int open, int total) n)) aParts.Add("（未分類）=" + n.open + "/" + n.total);
            return aParts.Count == 0 ? "（沒有單）" : string.Join("／", aParts.ToArray());
        }

        /// <summary>清單每列的專案標記：自己標的 `［Blockside］`、跟著母單的 `［Blockside←TASK-0553］`、沒有 ⇒ 空。</summary>
        static string ProjectTag((string project, int from) iEff)
        {
            if (iEff.project.Length == 0) return "";
            return "　［" + iEff.project + (iEff.from > 0 ? "←TASK-" + iEff.from.ToString("0000", CultureInfo.InvariantCulture) : "") + "］";
        }

        static void Bump(Dictionary<string, int> ioMap, string iKey)
            => ioMap[iKey] = ioMap.TryGetValue(iKey, out int v) ? v + 1 : 1;

        static string Describe(Dictionary<string, int> iMap)
        {
            var aParts = new List<string>();
            var aKeys = new List<string>(iMap.Keys);
            aKeys.Sort(StringComparer.Ordinal);
            foreach (string k in aKeys) aParts.Add(k + "=" + iMap[k]);
            return string.Join("／", aParts.ToArray());
        }

        // 區塊職責：把路徑攤成絕對路徑**給人看**，⛔ 攤不動就原樣印
        // 物理意義：`Path.GetFullPath` 的基準是 process cwd —— 與相對 `data_root` 實際被解析的
        //          基準同一個（實測：`D:/Unity` 底下同一個相對值就找不到目錄）⇒ 印出來的絕對路徑
        //          確實是**它剛剛讀的那一個**，不是另一種推導。
        // 數值影響：純字串，不碰磁碟；回傳值只進輸出，⛔ 不拿去讀寫檔。
        // ⚠ 非法字元／過長路徑會 throw ——「為了印一行定語把整支唯讀指令炸掉」是更壞的交換，
        //   所以吞掉例外並退回原字串（那一格會少一個定語，而不是少一份輸出）。
        static string AbsOrRaw(string iPath)
        {
            if (string.IsNullOrEmpty(iPath)) return iPath;
            try { return Path.GetFullPath(iPath); }
            catch { return iPath; }
        }
    }
}
