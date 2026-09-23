// 區塊職責：quest 事件流的**投影層**（Senate 側）—— 把事件重放成「每個 task 現在怎麼樣」。
// 物理意義：TASK-0287。Editor 側 `UCL_ChatTavernQuestIO.ComputeTaskStates` 的同義移植，
//           供 `task_list` / `task_state` / `task_next` 三個 kind 使用。
// 數值影響：**純讀**。⛔ 不寫、不建目錄、不回收過期租約 —— 見下面那段，那是本檔最重要的一格。
//
// 🔴 **本層刻意不做 lease 回收**（與 Editor 側的差別，⛔ 別把它當成漏掉的）：
//   Editor 的三支 op 每一支開頭都跑 `AutoRecoverStaleLeases`，而它 `AppendEvent` ⇒ **會寫**。
//   ⇒ 那三支在 Editor 側其實**不是純讀 op**（TASK-0239 §四把它們分在純讀那格，是錯的）。
//   本層若照抄，它就成為第二個寫入端 —— 撞 TASK-0106「單一寫入端」拍板，
//   也與 TASK-0287 ①「Server 沒跑也跑得完」結構互斥。⇒ 選純讀，並把缺口寫在單上（⑦）。
//   ⚠ 後果要說清楚：**Editor 沒開時沒有人在回收過期租約** ⇒ 本層會把過期的 lease 照實顯示成 STALE，
//     而那正是它的真相（回收只是把 STALE 變成 pending，⛔ 它不產生新資訊）。
//
// 🔴 `AgeDays` / `AgeFactor` / `IsStale` 依賴 **now** ⇒ 兩端在不同時刻跑**必然不同**。
//   ⇒ 跟 Editor 做輸出對拍時，那幾個欄位**結構上比不了**（TASK-0287 ③ 的受詞因此收窄）。
//   ⛔ 不是實作不一致，是那個比較本身沒有受詞。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Tavern
{
    /// <summary>事件重放算出的 task 當前狀態（純衍生，⛔ 不持久化）。欄位語意逐字對齊 Editor 側。</summary>
    public sealed class SCP_QuestTaskState
    {
        public string Id = "";
        public string Title = "";
        public string Role = "";
        public List<string> DependsOn = new List<string>();
        public string SuggestedOwner = "";
        public string Priority = "normal";
        public string GroupId = "";

        public string Status = "pending";
        public string Owner = "";
        public string LeaseUntil = "";
        public string LastProgressSummary = "";
        public string LastProgressAt = "";
        public string CreatedAt = "";
        public int CreatedSeq;
        public int LastEventSeq;
        public int RejectCount;

        // 衍生（第二輪算）——⚠ 下面三個依賴 now，見檔頭。
        public int DownstreamWeight;
        public double AgeDays;
        public int AgeFactor;
        public bool IsStale;

        public List<SCP_TavernQuestEvent> Lifecycle = new List<SCP_TavernQuestEvent>();
    }

    public static class SCP_TavernQuestState
    {
        /// <summary>讀一房事件流並重放成 task 狀態表。⚠ 依 <see cref="SCP_TavernQuestRead.LoadAll"/> 的 seq 語意。</summary>
        public static Dictionary<string, SCP_QuestTaskState> Compute(string iDataRoot, string iRoom,
                                                                     List<string>? oWarn)
        {
            var aStates = new Dictionary<string, SCP_QuestTaskState>(StringComparer.Ordinal);
            List<SCP_TavernQuestEvent> aEvents = SCP_TavernQuestRead.LoadAll(iDataRoot, iRoom, oWarn);
            foreach (SCP_TavernQuestEvent aEv in aEvents)
            {
                if (string.IsNullOrEmpty(aEv.TaskId)) continue;
                if (!aStates.TryGetValue(aEv.TaskId, out SCP_QuestTaskState? aSt))
                {
                    aSt = new SCP_QuestTaskState { Id = aEv.TaskId };
                    aStates[aEv.TaskId] = aSt;
                }
                aSt.LastEventSeq = aEv.Seq;
                aSt.Lifecycle.Add(aEv);
                ApplyEvent(aSt, aEv);
            }
            ComputeDerived(aStates);
            return aStates;
        }

        /// <summary>單筆事件的狀態轉移 —— ⚠ case 標籤與 data 的 key 都**逐字**對齊 Editor 側。</summary>
        static void ApplyEvent(SCP_QuestTaskState ioSt, SCP_TavernQuestEvent iEv)
        {
            string aV;
            switch (iEv.Type)
            {
                case "task_create":
                    ioSt.Status = "pending";
                    ioSt.CreatedSeq = iEv.Seq;
                    ioSt.CreatedAt = iEv.Ts;
                    if (iEv.Data.TryGetValue("title", out aV)) ioSt.Title = aV;
                    if (iEv.Data.TryGetValue("role", out aV)) ioSt.Role = aV;
                    if (iEv.Data.TryGetValue("suggested_owner", out aV)) ioSt.SuggestedOwner = aV;
                    if (iEv.Data.TryGetValue("priority", out aV)) ioSt.Priority = aV;
                    if (iEv.Data.TryGetValue("group_id", out aV)) ioSt.GroupId = aV;
                    if (iEv.Data.TryGetValue("depends_on", out aV))
                    {
                        ioSt.DependsOn = string.IsNullOrEmpty(aV)
                            ? new List<string>()
                            : new List<string>(aV.Split(','));
                    }
                    break;

                case "task_claim":
                    ioSt.Status = "claimed";
                    ioSt.Owner = iEv.Actor;
                    if (iEv.Data.TryGetValue("lease_until", out aV)) ioSt.LeaseUntil = aV;
                    break;

                case "task_progress":
                    ioSt.Status = "in_progress";
                    ioSt.LastProgressAt = iEv.Ts;
                    if (iEv.Data.TryGetValue("summary", out aV)) ioSt.LastProgressSummary = aV;
                    if (iEv.Data.TryGetValue("lease_until", out aV)) ioSt.LeaseUntil = aV;
                    break;

                case "task_done":
                    ioSt.Status = "done";
                    break;

                case "task_release":
                    // 主動放棄：退回 pending、清 owner／lease，⛔ 而 reason 留在 lifecycle 裡（不吃掉）。
                    ioSt.Status = "pending";
                    ioSt.Owner = "";
                    ioSt.LeaseUntil = "";
                    break;

                case "task_review_request":
                    ioSt.Status = "review";
                    break;

                case "task_reject":
                    ioSt.RejectCount++;
                    ioSt.Status = "in_progress";
                    break;

                case "task_reopen":
                    ioSt.Status = "in_progress";
                    break;

                case "task_force_reclaim":
                    ioSt.Status = "claimed";
                    ioSt.Owner = iEv.Actor;
                    if (iEv.Data.TryGetValue("lease_until", out aV)) ioSt.LeaseUntil = aV;
                    break;

                // ⛔ 未知 type 一律不動狀態（與 Editor 側同語意）—— 它們仍然進 Lifecycle。
            }
        }

        /// <summary>衍生欄位：DownstreamWeight / Age / IsStale。⚠ 後兩者依賴 now，見檔頭。</summary>
        static void ComputeDerived(Dictionary<string, SCP_QuestTaskState> ioStates)
        {
            DateTime aNow = DateTime.UtcNow;
            foreach (SCP_QuestTaskState aSt in ioStates.Values)
            {
                if (DateTime.TryParse(aSt.CreatedAt, out DateTime aCa))
                    aSt.AgeDays = (aNow - aCa.ToUniversalTime()).TotalDays;
                aSt.AgeFactor = (int)Math.Ceiling(aSt.AgeDays / 7.0);
                if (!string.IsNullOrEmpty(aSt.LeaseUntil) && DateTime.TryParse(aSt.LeaseUntil, out DateTime aLu))
                    aSt.IsStale = aLu.ToUniversalTime() < aNow && aSt.Status != "done";
            }

            // DownstreamWeight：從每個 task 出發 BFS，算它擋住多少下游（transitive）。
            foreach (SCP_QuestTaskState aSrc in ioStates.Values)
            {
                var aVisited = new HashSet<string>(StringComparer.Ordinal);
                var aQueue = new Queue<string>();
                foreach (var aKv in ioStates)
                    if (aKv.Value.DependsOn.Contains(aSrc.Id)) aQueue.Enqueue(aKv.Key);
                while (aQueue.Count > 0)
                {
                    string aT = aQueue.Dequeue();
                    if (!aVisited.Add(aT)) continue;
                    foreach (var aKv in ioStates)
                        if (aKv.Value.DependsOn.Contains(aT) && !aVisited.Contains(aKv.Key))
                            aQueue.Enqueue(aKv.Key);
                }
                aSrc.DownstreamWeight = aVisited.Count;
            }
        }

        /// <summary>pending 且所有 DependsOn 都 done ⇒ ready。</summary>
        public static bool IsReady(SCP_QuestTaskState iSt, Dictionary<string, SCP_QuestTaskState> iAll)
        {
            if (iSt.Status != "pending") return false;
            foreach (string aDep in iSt.DependsOn)
                if (!iAll.TryGetValue(aDep, out SCP_QuestTaskState? aD) || aD.Status != "done") return false;
            return true;
        }

        /// <summary>顯示用狀態：pending 且 ready ⇒ 印 `ready`（與 Editor 側同語意）。</summary>
        public static string EffectiveStatus(SCP_QuestTaskState iSt, Dictionary<string, SCP_QuestTaskState> iAll)
            => iSt.Status == "pending" && IsReady(iSt, iAll) ? "ready" : iSt.Status;

        /// <summary>排序分：優先度 ＋ 老化（每 7 天加 1 級，饑餓緩解）。</summary>
        public static int PriorityScore(SCP_QuestTaskState iSt)
        {
            int aBase = iSt.Priority == "high" ? 100 : iSt.Priority == "low" ? 0 : 50;
            return aBase + iSt.AgeFactor;
        }
    }
}
