// 區塊職責：晚安 check 的「Task 對帳」那一段（markdown）—— 移植自 UCL_Core `UCL_TaskReconcile.BuildReport`（TASK-0305）。
// 物理意義：以下每一段都**只印**：
//   ① 見叢裡還留著 `[TASK-n]` 引用 ⇒ 舊規則殘留（2026-09-07 起見叢只放個人代辦）
//   ② 跟我有關、還開著的單的張數 —— 只報數字，逐張列在早安 brief 的 §2.5 見單
//   ③ 我掛在 in_progress 且逾期 ⇒ 認領變成占位（釋放走 op=sweep，顯式）
//   ④ Task ↔ 工作記憶：(a) 連結壞掉 (b) 久未更新
//   ⑤ 收工預告：等一下 sleep 會擋什麼 —— 印的是 `SCP_TaskReconcile.PendingWrapups` 本人，**跟閘同一個述詞**
// ⚠ 「跟我有關」的 persona 比對用 OrdinalIgnoreCase（照 Editor 版 UCL_TaskEntry.RolesOf）；
//   SCP 的 RolesOf 是 Ordinal —— 兩把尺的差異原本就存在，這裡刻意沿用 Editor 那把，讓搬家前後讀數相同。
// 數值影響：純讀。回傳字串一定非空 —— 「沒印」跟「沒對」在回傳檔上長得一樣。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Paths;

namespace SCP.Core.Tasks
{
    public static class SCP_TaskReconcileReport
    {
        /// <summary>未關單多久沒動算「冷掉」—— 與 Editor `UCL_TaskIO.STALE_DAYS` 同值（sweep 量的是同一件事）。</summary>
        public const int STALE_DAYS = 14;

        static readonly Regex s_TaskRef = new Regex(@"TASK-(\d+)", RegexOptions.Compiled);

        public struct KeysRef
        {
            /// <summary>引用所在行（原文，已 Trim）。</summary>
            public string Line;
            /// <summary>擁有這一行的**未完**條目序號（1-based，與 `senate cmd keys --arg done_index=` 同一把尺）；0＝不屬於任何未完條目。</summary>
            public int OpenIndex;
        }

        // 🩸 TASK-0149：舊版逐行找 `TASK-\d+` 而**不看 `[ ]`／`[x]`** ⇒ 叫人去勾銷已經勾銷的行（每晚重報同一批）。
        // 🩸 TASK-0180：只看「這一行自己是不是 `- [x]`」也不夠 —— 見叢條目是多行的，引用可以長在**已勾銷條目的續行**裡。
        //   ⇒ 判準是「這一行屬於哪一筆條目」：縮排續行沿用擁有者，空行／非縮排散文結束一段。
        // ⭐ OpenIndex 的尺刻意跟 `senate cmd keys` 的 done_index 對齊（`- [ ]` 的 1-based 出現序），印出來能直接貼進指令。
        /// <summary>見叢裡**還沒勾銷**的條目（含其續行）提到的單號 → 那筆引用。已勾銷的條目連同續行都不算。</summary>
        public static Dictionary<int, KeysRef> ReadKeysRefs(string iKeysPath)
        {
            var aOut = new Dictionary<int, KeysRef>();
            if (!File.Exists(iKeysPath)) return aOut;
            int aOpenSeq = 0, aOwnerOpen = 0;
            bool aOwnerChecked = false;
            foreach (string aLine in File.ReadAllLines(iKeysPath, Encoding.UTF8))
            {
                string t = aLine.TrimStart();
                if (t.StartsWith("- [ ]", StringComparison.Ordinal)) { aOpenSeq++; aOwnerOpen = aOpenSeq; aOwnerChecked = false; }
                else if (t.StartsWith("- [x]", StringComparison.Ordinal) || t.StartsWith("- [X]", StringComparison.Ordinal))
                { aOwnerOpen = 0; aOwnerChecked = true; }
                else if (aLine.Trim().Length == 0 || !char.IsWhiteSpace(aLine[0])) { aOwnerOpen = 0; aOwnerChecked = false; }
                // else：縮排的續行 ⇒ 沿用目前這一段的擁有者
                if (aOwnerChecked) continue;
                foreach (Match m in s_TaskRef.Matches(aLine))
                {
                    if (!int.TryParse(m.Groups[1].Value, out int aIdx) || aOut.ContainsKey(aIdx)) continue;
                    aOut[aIdx] = new KeysRef { Line = aLine.Trim(), OpenIndex = aOwnerOpen };
                }
            }
            return aOut;
        }

        static bool IsParticipantCI(SCP_TaskEntry e, string iPersona)
        {
            foreach (var p in e.participants)
                if (string.Equals(p.persona, iPersona, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static bool Involves(SCP_TaskEntry e, string iPersona)
            => IsParticipantCI(e, iPersona) || string.Equals(e.reporter, iPersona, StringComparison.OrdinalIgnoreCase);

        /// <summary>距上次更新幾天；updated_at 解析不了回 -1（與 Editor 版同）。</summary>
        public static int DaysSinceUpdate(SCP_TaskEntry e, DateTime iNowUtc)
        {
            if (!DateTime.TryParse(e.updated_at, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var aTs)) return -1;
            return (int)(iNowUtc - aTs).TotalDays;
        }

        static string Trunc(string? s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "…";
        }

        public static string BuildReport(SCP_DataRoot iRoot, string iPersona, string iKeysPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("## 📋 Task 對帳（見叢引用 ✕ 單子實際狀態）—— **只印不改**");
            try
            {
                var aAll = SCP_TaskIO.LoadAll(iRoot);
                if (aAll.Count == 0)
                {
                    sb.AppendLine("- 系統裡目前沒有任何單（`AgentCommands/Tasks/tasks/` 是空的）——"
                        + " 這是「沒有單」，不是「沒對帳」。");
                    return sb.ToString();
                }
                var aRefs = ReadKeysRefs(iKeysPath);
                sb.AppendLine($"- 讀數：單 **{aAll.Count}** 張／見叢引用 **{aRefs.Count}** 筆"
                    + $"（見叢：`{iKeysPath}`{(File.Exists(iKeysPath) ? "" : " ⚠ **檔不存在**")}）");

                // ① 見叢裡的 `[TASK-n]` 引用 —— 新規則下一筆都不該有；每一句只講量到的東西（TASK-0180）
                var aStaleRefs = new List<string>();
                var aCheckOff = new SortedSet<int>();
                var aKeys = new List<int>(aRefs.Keys);
                aKeys.Sort();
                foreach (int k in aKeys)
                {
                    KeysRef r = aRefs[k];
                    SCP_TaskEntry? e = aAll.Find(t => t.index == k);
                    string aWhere = r.OpenIndex > 0
                        ? $"見叢第 **#{r.OpenIndex}** 筆（未完）引用了它"
                        : "而引用它的那一行**不屬於任何未完條目**（勾不掉 —— 要手動從見叢移走）";
                    string aState;
                    if (e == null) aState = $"**單子不存在** —— {aWhere}";
                    else if (e.IsClosed())
                    {
                        aState = $"已 `{e.status}` —— {aWhere}";
                        if (r.OpenIndex > 0) aCheckOff.Add(r.OpenIndex);
                    }
                    else aState = $"還開著（早安 §2.5 已經會列它，這行是重複的）—— {aWhere}";
                    aStaleRefs.Add($"TASK-{k:0000} {aState}\n      · 見叢原文：{Trunc(r.Line, 120)}");
                }
                string aHint = aCheckOff.Count == 0 ? ""
                    : $"\n    ⇒ `senate cmd keys --arg persona={iPersona} --arg done_index={string.Join(",", aCheckOff)}`";
                sb.AppendLine(aStaleRefs.Count == 0
                    ? "- ✅ ① 見叢裡沒有任何 `[TASK-n]` 引用（合乎新規則：見叢只放個人代辦）"
                    : $"- ⚠ ① 見叢還有 **{aStaleRefs.Count}** 筆 `[TASK-n]` 引用 —— 舊規則殘留：" + aHint);
                foreach (string s in aStaleRefs) sb.AppendLine("    · " + s);

                // ② 只報張數
                var aMine = aAll.FindAll(e => !e.IsClosed() && Involves(e, iPersona));
                sb.AppendLine($"- 📋 ② 跟我有關的未關單 **{aMine.Count}** 張"
                    + " —— 逐張列在早安 brief 的 **§2.5 見單**（機械撈取，不需要抄進見叢）");

                // ③ 逾期認領
                DateTime aNow = DateTime.UtcNow;
                var aStaleClaims = aAll.FindAll(e => !e.IsClosed() && e.status == SCP_TaskStatus.in_progress
                    && IsParticipantCI(e, iPersona) && DaysSinceUpdate(e, aNow) >= STALE_DAYS);
                sb.AppendLine(aStaleClaims.Count == 0
                    ? $"- ✅ ③ 我沒有逾期認領（in_progress 超過 {STALE_DAYS} 天沒動）"
                    : $"- ⏳ ③ 有 **{aStaleClaims.Count}** 張我認領後逾期未動 ⇒ 認領已經變成占位：");
                foreach (var e in aStaleClaims) sb.AppendLine($"    · {e.Id} {Trunc(e.title, 60)}　{DaysSinceUpdate(e, aNow)} 天沒動");
                if (aStaleClaims.Count > 0)
                    sb.AppendLine("    ⇒ 釋放回 todo（機械、可重跑）：`run Task --arg op=sweep --arg confirm=1`");

                // ④ Task ↔ 工作記憶（只判「主題不存在」，不判「主題在但沒有 state」—— 後者是拍板後的正確形狀）
                var aBrokenLink = new List<string>();
                var aColdMemory = new List<string>();
                foreach (var e in aMine)
                {
                    string aTopic = (e.memory_topic ?? "").Trim();
                    if (aTopic.Length > 0 && !File.Exists(Path.Combine(iRoot.Value, "WorkMemory", aTopic, "_topic.md")))
                        aBrokenLink.Add($"{e.Id} → `{aTopic}`　"
                            + ((e.memory_archived_commit ?? "").Length > 0
                                ? $"（已歸檔 `{e.memory_archived_commit}` —— 這是正常的，只是提醒接手要去 git 找）"
                                : "**主題不在磁碟上且沒有歸檔 sha** ⇒ 連結壞了，不是沒有記憶"));
                    int aDays = DaysSinceUpdate(e, aNow);
                    if (aDays >= STALE_DAYS)
                        aColdMemory.Add($"{e.Id} `{e.status}` {Trunc(e.title, 50)}　**{aDays} 天沒動**"
                            + (aTopic.Length == 0 ? "（沒掛記憶 ⇒ 接手的人只有這張單）" : $"　記憶：`{aTopic}`"));
                }
                sb.AppendLine(aBrokenLink.Count == 0
                    ? "- ✅ ④a 記憶連結沒有壞的（掛了主題的單，主題都在）"
                    : $"- ⚠ ④a 有 **{aBrokenLink.Count}** 筆記憶連結要看：");
                foreach (string s in aBrokenLink) sb.AppendLine("    · " + s);
                sb.AppendLine(aColdMemory.Count == 0
                    ? $"- ✅ ④b 跟我有關的未關單都在 {STALE_DAYS} 天內動過"
                    : $"- 🧊 ④b 有 **{aColdMemory.Count}** 張未關單超過 {STALE_DAYS} 天沒動"
                        + "（跨多日大 Task 死在這裡：單還開著，而沒人記得上次做到哪）：");
                foreach (string s in aColdMemory) sb.AppendLine("    · " + s);
                if (aColdMemory.Count > 0)
                    sb.AppendLine("    ⇒ **只印不改**（契約②）：要嘛去推進它，要嘛把現況寫進它的記憶主題，"
                        + "要嘛 `op=update` 改狀態說明它為什麼停著。");

                // ⑤ 收工預告 —— 跟閘同一個述詞；零筆也要印
                var aPre = SCP_TaskReconcile.PendingWrapups(iRoot, iPersona, SCP_TaskReconcile.SessionStartUtc(iRoot, iPersona, out _));
                sb.AppendLine(aPre.Count == 0
                    ? "- ✅ ⑤ 收工預告：目前**沒有單會擋下線**"
                        + "（本次醒來後有動靜（含別人在單上留言）、還開著、而收工紀錄已過期或從未收工的單：0 張）"
                    : $"- 🔔 ⑤ 收工預告：有 **{aPre.Count}** 張單會在 `step=sleep` **實擋**"
                        + "（本次醒來後有動靜（含別人在單上留言）＋ 還開著 ＋ 我是參與者 ＋ 最後一次收工之後又有動靜／從沒收過工）：");
                foreach (var e in aPre) sb.AppendLine($"    · {e.Id} `{e.status}` {Trunc(e.title, 60)}");
                if (aPre.Count > 0)
                {
                    sb.AppendLine("    ⇒ 現在就可以收（**本段只列不擋**，擋的是 `step=sleep`）：");
                    foreach (var e in aPre)
                        sb.AppendLine($"      `run Task --arg op=wrapup --arg index={e.index}"
                            + " --arg-file progress=<還剩什麼、下一步從哪接>"
                            + " [--arg-file why=<為什麼卡住／試過什麼不行 ⇒ 進工作記憶>]`");
                    sb.AppendLine("    ⇒ 真的沒東西可寫 → `step=sleep` 帶 `--arg skip_reason=<一句話>`"
                        + "（理由會寫進那幾張單的時間線）。");
                }
            }
            catch (Exception ex)
            {
                // 失敗要看得見：「今晚沒對到帳」不可以長得像「對過帳沒問題」
                sb.AppendLine($"- ⚠ **對帳失敗**（{ex.Message}）—— 這一段沒有讀數，不要當成「沒有不一致」。");
            }
            return sb.ToString();
        }
    }
}
