// 區塊職責：quest 投影的**渲染層**（Senate 側）—— `task_list` / `task_state` / `task_next` 三支的輸出。
// 物理意義：TASK-0287。逐字對齊 Editor 側 `Cmd_Tavern.Op_TaskList`／`Op_TaskState`／`Op_TaskNext`
//           的版面，理由是驗收③要做輸出對拍 —— 版面不同的話那個比對就沒有受詞了。
// 數值影響：**純讀**，只組字串。
//
// ⚠ 小數一律走 `InvariantCulture`：`age` 那一格是 `F1`，而**小數點符號是 culture 的**。
//   兩端目前都會給 `.`，⛔ 但我不把逐位元組相同這件事交給 culture 去決定。
//
// 🔴 與 Editor 的**已知差異**（⛔ 不假裝沒有，見 SCP_TavernQuestState 檔頭）：
//   ① 本側不回收過期租約 ⇒ 過期的 lease 照實顯示 STALE（Editor 會先回收再顯示成 pending）。
//   ② `age` / STALE 依賴 now ⇒ 兩端在不同時刻跑必然不同。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SCP.Core.Tavern
{
    public static class SCP_TavernQuestRender
    {
        static string Dash(string iS) => string.IsNullOrEmpty(iS) ? "-" : iS;

        static string F1(double iV) => iV.ToString("F1", CultureInfo.InvariantCulture);

        /// <summary>逐字同 Editor 側 `Truncate`：超過 max 就切到 max-3 再補三個點。</summary>
        static string Truncate(string iS, int iMax)
        {
            if (string.IsNullOrEmpty(iS)) return "";
            return iS.Length <= iMax ? iS : iS.Substring(0, iMax - 3) + "...";
        }

        // ===========================================================
        // task_list
        // ===========================================================
        public static string TaskList(string iRoom, Dictionary<string, SCP_QuestTaskState> iStates,
                                      string iOwnerFilter, string iRoleFilter, string iStatusCsv)
        {
            var aStatusFilters = new HashSet<string>(StringComparer.Ordinal);
            if (!string.IsNullOrEmpty(iStatusCsv))
                foreach (string aS in iStatusCsv.Split(',')) aStatusFilters.Add(aS.Trim());

            var aSb = new StringBuilder();
            aSb.AppendLine("# 📋 Task List");
            aSb.AppendLine();
            aSb.AppendLine("room: `" + iRoom + "` / total tasks: " + iStates.Count);
            aSb.AppendLine();
            aSb.AppendLine("| Task | Status | Owner | Role | Deps | Last Progress |");
            aSb.AppendLine("|---|---|---|---|---|---|");

            int aMatched = 0;
            foreach (var aKv in iStates)
            {
                SCP_QuestTaskState aSt = aKv.Value;
                string aEff = SCP_TavernQuestState.EffectiveStatus(aSt, iStates);

                // ⚠ `stale` 是 orthogonal flag，⛔ 不吃掉原 status（同 Editor 側）：
                //   一個 task 可以同時是 claimed 又 stale，filter 寫 status=stale 時兩者都算命中。
                if (aStatusFilters.Count > 0)
                {
                    bool aMatch = aStatusFilters.Contains(aEff);
                    if (!aMatch && aStatusFilters.Contains("stale") && aSt.IsStale) aMatch = true;
                    if (!aMatch) continue;
                }
                if (iOwnerFilter.Length > 0 && aSt.Owner != iOwnerFilter) continue;
                if (iRoleFilter.Length > 0 && aSt.Role != iRoleFilter) continue;

                string aDeps = aSt.DependsOn.Count > 0 ? string.Join(",", aSt.DependsOn) : "-";
                string aProg = string.IsNullOrEmpty(aSt.LastProgressSummary)
                    ? "-" : Truncate(aSt.LastProgressSummary, 40);
                string aCell = aEff + (aSt.IsStale ? " ⚠STALE" : "");
                aSb.AppendLine("| `" + aSt.Id + "` | " + aCell + " | " + Dash(aSt.Owner) + " | "
                               + Dash(aSt.Role) + " | " + aDeps + " | " + aProg + " |");
                aMatched++;
            }

            if (aMatched == 0) aSb.AppendLine("| _(無符合 filter 的 task)_ | | | | | |");
            aSb.AppendLine();
            aSb.AppendLine("_filter: owner=" + iOwnerFilter + ", role=" + iRoleFilter
                           + ", status=" + iStatusCsv + "_");
            return aSb.ToString();
        }

        // ===========================================================
        // task_state
        // ===========================================================
        public static string TaskState(SCP_QuestTaskState iSt, Dictionary<string, SCP_QuestTaskState> iAll)
        {
            var aSb = new StringBuilder();
            aSb.AppendLine("# 📜 task_state — `" + iSt.Id + "`");
            aSb.AppendLine();
            string aEff = SCP_TavernQuestState.EffectiveStatus(iSt, iAll);
            aSb.AppendLine("- title: **" + iSt.Title + "**");
            aSb.AppendLine("- status: **" + aEff + "**" + (iSt.IsStale ? " ⚠ STALE" : ""));
            aSb.AppendLine("- owner: " + Dash(iSt.Owner));
            aSb.AppendLine("- role: " + Dash(iSt.Role) + " | priority: " + iSt.Priority
                           + " (+age " + iSt.AgeFactor + ")");
            aSb.AppendLine("- depends_on: " + (iSt.DependsOn.Count > 0
                                               ? string.Join(", ", iSt.DependsOn) : "-"));
            aSb.AppendLine("- downstream_weight (阻擋 N 個下游): **" + iSt.DownstreamWeight + "**");
            aSb.AppendLine("- age: " + F1(iSt.AgeDays) + " days (created: " + iSt.CreatedAt + ")");
            aSb.AppendLine("- lease_until: " + Dash(iSt.LeaseUntil));
            if (iSt.RejectCount > 0)
                aSb.AppendLine("- reject_count: **" + iSt.RejectCount + "** (被退回 "
                               + iSt.RejectCount + " 次)");
            aSb.AppendLine();
            aSb.AppendLine("## Lifecycle Timeline");
            aSb.AppendLine();
            foreach (SCP_TavernQuestEvent aEv in iSt.Lifecycle)
            {
                string aDetail = "";
                if (aEv.Data.Count > 0)
                {
                    var aParts = new List<string>();
                    foreach (var aKv in aEv.Data) aParts.Add(aKv.Key + "=" + aKv.Value);
                    aDetail = " — " + string.Join(", ", aParts);
                }
                aSb.AppendLine("- **seq=" + aEv.Seq + "** [" + aEv.Ts + "] `" + aEv.Type
                               + "` by " + aEv.Actor + aDetail);
            }
            aSb.AppendLine();
            aSb.AppendLine("_spec: tasks/" + iSt.Id + ".md_");
            return aSb.ToString();
        }

        // ===========================================================
        // task_next
        // ===========================================================
        public static string TaskNext(string iRoom, string iAgentId, int iTop,
                                      Dictionary<string, SCP_QuestTaskState> iStates)
        {
            if (iTop < 1) iTop = 1;
            var aCandidates = new List<SCP_QuestTaskState>();
            foreach (SCP_QuestTaskState aSt in iStates.Values)
            {
                if (aSt.Status != "pending") continue;
                if (!SCP_TavernQuestState.IsReady(aSt, iStates)) continue;
                aCandidates.Add(aSt);
            }

            // 排序：優先度＋老化 → suggested_owner 命中 → downstream_weight → created_seq asc
            // ⚠ 四層逐字同 Editor 側；少一層或換順序 ⇒ 同一份資料兩端會給**不同的建議**，
            //   而兩邊的輸出都合理 —— 那種不一致沒有人會發現。
            aCandidates.Sort((a, b) =>
            {
                int aSa = SCP_TavernQuestState.PriorityScore(a);
                int aSb2 = SCP_TavernQuestState.PriorityScore(b);
                if (aSa != aSb2) return aSb2 - aSa;
                int aSuggA = a.SuggestedOwner == iAgentId ? 1 : 0;
                int aSuggB = b.SuggestedOwner == iAgentId ? 1 : 0;
                if (aSuggA != aSuggB) return aSuggB - aSuggA;
                if (a.DownstreamWeight != b.DownstreamWeight) return b.DownstreamWeight - a.DownstreamWeight;
                return a.CreatedSeq - b.CreatedSeq;
            });
            if (aCandidates.Count > iTop) aCandidates = aCandidates.GetRange(0, iTop);

            var aOut = new StringBuilder();
            aOut.AppendLine("# 🎯 task_next — " + iAgentId);
            aOut.AppendLine();
            aOut.AppendLine("room: `" + iRoom + "` / top=" + iTop);
            aOut.AppendLine();
            if (aCandidates.Count == 0)
            {
                aOut.AppendLine("_(無可接任務 — 全部 done / blocked / claimed by 別人)_");
                return aOut.ToString();
            }

            aOut.AppendLine("| Rank | Task | Priority | Age | DownW | Suggested-match | Title |");
            aOut.AppendLine("|---|---|---|---|---|---|---|");
            for (int i = 0; i < aCandidates.Count; ++i)
            {
                SCP_QuestTaskState aSt = aCandidates[i];
                string aMatch = aSt.SuggestedOwner == iAgentId ? "✓" : "-";
                aOut.AppendLine("| " + (i + 1) + " | `" + aSt.Id + "` | " + aSt.Priority + "+"
                                + aSt.AgeFactor + " | " + F1(aSt.AgeDays) + "d | "
                                + aSt.DownstreamWeight + " | " + aMatch + " | " + aSt.Title + " |");
            }
            aOut.AppendLine();
            aOut.AppendLine("建議下一步：`task_claim task_id=" + aCandidates[0].Id
                            + " claimer=" + iAgentId + "`");
            return aOut.ToString();
        }
    }
}
