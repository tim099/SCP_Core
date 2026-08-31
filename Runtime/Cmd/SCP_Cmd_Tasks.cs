// 區塊職責：`cmd tasks` —— 任務單的**唯讀**查詢。**原生**，不需要 Unity。
// 物理意義：任務單是 `Tasks/tasks/<index>.md`（frontmatter markdown），磁碟就是真相源。
//           讀取不需要 Editor，所以不該綁在「Editor 開著」這個前提上 ——
//           而早安 brief 的 §9／§6 需要這份讀數，那兩節正是卡在這裡。
// 數值影響：純讀。⛔ **本 Cmd 不寫任何東西** —— 開單／改狀態／配號仍走 Editor 的 `Cmd_Task`，
//           理由在 `SCP_TaskIO` 檔頭（配號是沒有跨 process lock 的 read-modify-write）。
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

        public override string Summary => "任務單唯讀查詢：計數／清單／單張；可落一份 JSON 給程式讀";

        public override string Details =>
            "資料源＝`<data_root>/Tasks/tasks/*.md`（frontmatter markdown），磁碟即事實。\n"
            + "⛔ **只讀**：開單／改狀態／配號請走 Editor 的 `Cmd_Task`（`senate ucmd run Task`）——\n"
            + "   配號是沒有跨 process lock 的 read-modify-write，兩個寫者會靜默撞號。\n"
            + "⚠ 壞欄位（例如 status 寫著篩選成員 `all`）會**出聲**並落回預設，不靜默接受。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("tasks --arg data_root=D:/Unity/LY/AgentCommands --arg persona=summit");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("index", "只看某一張單（給了就印那張的細節）"),
            new SCP_CmdArgSpec("persona", "以誰的視角統計「跟我有關的未關單」（選填）"),
            new SCP_CmdArgSpec("status", "只列這個狀態（wire 名，如 todo / in_review）"),
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
            if (!Directory.Exists(aTasksDir))
                // 「資料根設錯」與「還沒有人開單」是兩件事 —— 印出路徑讓人自己分辨。
                return SCP_CmdResult.Fail(1,
                    "✗ 找不到任務單目錄：" + aTasksDir,
                    "  （資料根：" + aDataRoot + "）");

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
                AppendOne(aResult, aRoot, aOne, aWarnings);
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
            string aPersona = aPersona0;
            var aRows = new List<SCP_TaskEntry>();
            int aOpen = 0, aMine = 0, aMineOpen = 0;
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
                if (aStatusFilter.Length == 0 || string.Equals(e.status.ToString(), aStatusFilter, StringComparison.Ordinal))
                    aRows.Add(e);
            }

            aResult.Lines.Add("# 📋 任務單 —— 共 " + aAll.Count + " 張（開著 " + aOpen + "）");
            aResult.Lines.Add("· 資料源：" + aTasksDir);
            aResult.Lines.Add("· 狀態分布：" + Describe(aByStatus));
            aResult.Lines.Add("· 種類分布：" + Describe(aByType));
            if (aPersona.Length > 0)
                aResult.Lines.Add("· " + aPersona + " 參與 " + aMine + " 張（其中開著 " + aMineOpen + "）");
            if (aStatusFilter.Length > 0)
                aResult.Lines.Add("· 篩選 status=" + aStatusFilter + " ⇒ " + aRows.Count + " 張");
            aResult.Lines.Add("");
            foreach (SCP_TaskEntry e in aRows)
                aResult.Lines.Add("- " + e.Id + "　" + e.status + "／" + e.type + "／" + e.priority
                                  + "　" + e.title);

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
            }
            EmitJson(aResult, iArgs, aRows);
            return aResult;
        }

        // ===========================================================
        // 區塊職責：收工閘的 CLI 讀數 —— 判準本體在 `SCP_TaskReconcile`（**不在本檔重算**）。
        // 物理意義：這一格存在的理由是**對拍**：UCL 端晚安 step=check 的⑤也印同一件事，
        //          兩邊該給出同一份清單。⛔ 只印不改（同 UCL 端的契約）。
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
                              List<string> ioWarnings)
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
            oResult.Lines.Add("· 留言 " + e.comments.Count + " 則");
            AppendWarnings(oResult, ioWarnings);
            oResult.AddValue("index", e.index.ToString(CultureInfo.InvariantCulture));
            oResult.AddValue("status", e.status.ToString());
            oResult.AddValue("comment_count", e.comments.Count.ToString(CultureInfo.InvariantCulture));
            oResult.AddValue("open_blockers", aBlockers.Count.ToString(CultureInfo.InvariantCulture));
            oResult.AddValue("bad_field_warnings", ioWarnings.Count.ToString(CultureInfo.InvariantCulture));
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
    }
}
