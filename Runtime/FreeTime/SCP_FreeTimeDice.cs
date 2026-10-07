// 區塊職責：擲骰 —— 活動清單 → 可用性 → 時間感知 → 飢餓置頂 → 脫離 → 分組 → 兩層洗牌 → 骰面（回傳檔段落與酒館一行版）。
// 物理意義：順序的每一道防的是不同的事：
//            ① 可用性（隱藏）：做不成的活動**整項不列** —— 沒開播的陪看留在骰面上是在浪費一個選項的位置。
//            ② 時間感知：min_minutes 不足者降到最尾並標明（**不隱藏**），而且**壓過優先層** ——
//               「最優先但這場做不完」是自相矛盾的建議。
//            ③ 飢餓置頂（**通用**，不看 kind）：上限需要全域視野，所以住這裡而不是 gating 的 switch 裡。
//            ④ 脫離：觸發特殊規則的活動**脫離分組成單獨一項**排最前 —— 它此刻值得做的理由是它自己的，
//               被組名蓋住就傳達不到（「繪圖」這個組名不會告訴你券超過 100 了）。脫離的必須從原組移除，
//               否則同一件事在骰面出現兩次，會被讀成兩件不同的事。
//            ⑤ 分組 ＋ 兩層各自洗牌（優先不等於指定，層內仍隨機）。
// 數值影響：純計算 ＋ 唯讀（gating 會讀棋局／session／券餘額）。不寫任何檔。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using SCP.Core.Cmd;

namespace SCP.Core.FreeTime
{
    /// <summary>骰面上的一件具體活動（擲骰當下的快照：名字已附上 gating／飢餓／時間的後綴）。</summary>
    public struct SCP_FreeTimeDiceItem
    {
        public string Id, Name, How, Path, Group;
        public int MinMinutes;
        public bool Priority, TooLong;
    }

    /// <summary>骰面的「一項」—— 一個分組（內含多件具體活動），或單獨一件活動。</summary>
    public sealed class SCP_FreeTimeDiceEntry
    {
        /// <summary>顯示標題：分組名，或單獨項的活動名。</summary>
        public string Label = "";
        public bool Priority;
        /// <summary>true＝觸發特殊規則、從分組脫離出來的單獨項。</summary>
        public bool Hoisted;
        /// <summary>脫離前的原分組（僅 Hoisted 時非空 —— 讓人看得出它從哪一組來的）。</summary>
        public string FromGroup = "";
        /// <summary>本項整體時間不夠（組內**全員**都不夠才成立）。</summary>
        public bool TooLong;
        public List<SCP_FreeTimeDiceItem> Items = new List<SCP_FreeTimeDiceItem>();

        /// <summary>
        /// 酒館一行版。**讀的人要能直接下 op=pick** ⇒ 分組項要把組員 id 列出來
        /// （只印組名的話，想選的人得再跑一次 Cmd 才知道 id —— 那就是把路指到一半）。
        /// </summary>
        public string TavernLine()
        {
            string aMark = TooLong ? " ⏳（本場時間不夠）" : "";
            if (Items.Count == 1)
            {
                var a = Items[0];
                string aFrom = Hoisted && FromGroup.Length > 0 ? $"（{FromGroup} 組）" : "";
                return $"{a.Name}{aFrom}　`{a.Id}`{aMark}";
            }
            var aIds = new List<string>();
            foreach (var a in Items) aIds.Add($"{a.Name} `{a.Id}`");
            return $"{Label}{aMark} — {string.Join(" ／ ", aIds)}";
        }
    }

    /// <summary>一次擲骰的結果。</summary>
    public sealed class SCP_FreeTimeRoll
    {
        public List<SCP_FreeTimeDiceEntry> List = new List<SCP_FreeTimeDiceEntry>();
        /// <summary>清單來源一行（共用 N ＋ 專案 M｜本人第 K 場｜飢餓置頂…）。</summary>
        public string Source = "";
        /// <summary>直播中（看得到 StreamWatch 活動就是在播）。</summary>
        public bool IsLive;
        public int PriorityCount
        {
            get { int n = 0; foreach (var e in List) if (e.Priority) n++; return n; }
        }
    }

    public static class SCP_FreeTimeDice
    {
        /// <summary>
        /// 擲骰。<paramref name="iRemainMinutes"/> ≤ 0 ⇒ 時間感知那道跳過（剩餘算不出來就不假裝知道 —— shuffle 傳 0）。
        /// </summary>
        public static SCP_FreeTimeRoll Roll(SCP_FreeTimeContext iCtx, string iPersona, int iRemainMinutes, Random? iRng = null)
        {
            var aRng = iRng ?? new Random();
            int aShared = 0, aProject = 0;
            bool aIsLive = false;
            var aVisible = new List<SCP_FreeTimeDiceItem>();

            // ── ① 可用性 ＋ ② 時間感知（活動層判定，與分組無關）──
            foreach (var a in iCtx.Activities)
            {
                if (!a.Enabled) continue;   // 過濾在 merge 之後（專案層的 enabled:false 才擋得住共用層的啟用）
                SCP_FreeTimeGateResult aGate = SCP_FreeTimeGating.Evaluate(iCtx, a, iPersona);
                if (!aGate.Visible) continue;
                if (a.Kind == SCP_FreeTimeActivityKind.StreamWatch) aIsLive = true;
                bool aTooLong = iRemainMinutes > 0 && a.MinMinutes > 0 && a.MinMinutes > iRemainMinutes;
                aVisible.Add(new SCP_FreeTimeDiceItem
                {
                    Id = a.Id,
                    Name = a.Name + (aGate.NameSuffix ?? "")
                           + (aTooLong ? $" ⏳（建議 ≥{a.MinMinutes} 分 —— 本場可能做不完）" : ""),
                    How = a.How,
                    Path = a.Path,
                    MinMinutes = a.MinMinutes,
                    Priority = aGate.Priority && !aTooLong,   // 降級了就不該再標成優先（標記要跟實際位置一致）
                    Group = a.Group,
                    TooLong = aTooLong,
                });
                if (a.IsProjectLayer) aProject++; else aShared++;
            }

            // ── ③ 飢餓置頂（通用）──  ⚠ 不動 visible（飢餓不能讓做不成的活動復活）；不覆蓋 tooLong。
            SCP_FreeTimeStats aStats = SCP_FreeTimeStatsIO.Load(iCtx.Letters, iPersona);
            var aStarveIds = new List<string>();
            foreach (var a in aVisible) if (!a.TooLong) aStarveIds.Add(a.Id);
            Dictionary<string, int> aStarved = SCP_FreeTimeStatsIO.PickStarved(aStats, aStarveIds, iCtx.Settings, out int aOverflow);
            for (int i = 0; i < aVisible.Count; i++)
            {
                var a = aVisible[i];
                if (a.TooLong || !aStarved.TryGetValue(a.Id, out int aGap)) continue;
                a.Priority = true;
                a.Name += SCP_FreeTimeStatsIO.StarveSuffix(aGap, aStats.Picks(a.Id));
                aVisible[i] = a;   // struct ⇒ 寫回去，不然改的是複本
            }

            // ── ④ 脫離 ──
            var aHoisted = new List<SCP_FreeTimeDiceEntry>();
            var aRest = new List<SCP_FreeTimeDiceItem>();
            foreach (var a in aVisible)
            {
                if (a.Priority)
                {
                    var aEntry = new SCP_FreeTimeDiceEntry { Label = a.Name, Priority = true, Hoisted = a.Group.Length > 0, FromGroup = a.Group };
                    aEntry.Items.Add(a);
                    aHoisted.Add(aEntry);
                }
                else aRest.Add(a);
            }

            // ── ⑤ 分組（List 保序 —— Dictionary 的列舉序是實作細節，靠它就是把隨機性交給不保證的東西）──
            var aGroups = new List<SCP_FreeTimeDiceEntry>();
            foreach (var a in aRest)
            {
                if (a.Group.Length == 0)
                {
                    var aSolo = new SCP_FreeTimeDiceEntry { Label = a.Name };
                    aSolo.Items.Add(a);
                    aGroups.Add(aSolo);
                    continue;
                }
                SCP_FreeTimeDiceEntry? aFound = null;
                foreach (var g in aGroups) if (g.Items.Count > 0 && g.Label == a.Group) { aFound = g; break; }
                if (aFound == null) { aFound = new SCP_FreeTimeDiceEntry { Label = a.Group }; aGroups.Add(aFound); }
                aFound.Items.Add(a);
            }
            // 組項整體時間不夠 ＝ 組內**全員**都不夠。有一個做得成就不該把整組標成做不完。
            foreach (var g in aGroups)
            {
                bool aAll = g.Items.Count > 0;
                foreach (var a in g.Items) if (!a.TooLong) { aAll = false; break; }
                g.TooLong = aAll;
            }

            Shuffle(aHoisted, aRng);
            Shuffle(aGroups, aRng);
            var aFit = new List<SCP_FreeTimeDiceEntry>();
            var aTail = new List<SCP_FreeTimeDiceEntry>();
            foreach (var g in aGroups) { if (g.TooLong) aTail.Add(g); else aFit.Add(g); }

            var aRoll = new SCP_FreeTimeRoll { IsLive = aIsLive };
            aRoll.List.AddRange(aHoisted);
            aRoll.List.AddRange(aFit);
            aRoll.List.AddRange(aTail);
            // ⚠ overflow 一定要說出來：「只有 2 項餓」與「有 9 項餓而我只頂 2 項」在骰面上同形。
            string aStarveNote = aStarved.Count == 0 ? ""
                : $"｜💤 飢餓置頂 {aStarved.Count} 項"
                  + (aOverflow > 0 ? $"（另有 {aOverflow} 項也超過 {iCtx.Settings.StarveThreshold} 場沒選，本輪沒頂上來）" : "");
            string aStatsNote = aStats.Loaded
                ? $"｜本人第 {aStats.SessionsTotal} 場"
                : (aStats.LoadError.Length > 0
                    ? "｜⚠ 活動統計讀不了（不是 0 場，是沒有讀數）：" + aStats.LoadError
                    : "｜⚠ 尚無活動統計（不是 0 場，是沒有讀數）");
            aRoll.Source = $"UCL_Core 共用 {aShared} + 專案 {aProject}{aStatsNote}{aStarveNote}";
            return aRoll;
        }

        /// <summary>Fisher-Yates（System.Random —— 擲骰不需要密碼學強度）。</summary>
        static void Shuffle<T>(List<T> ioList, Random iRng)
        {
            for (int i = ioList.Count - 1; i > 0; i--)
            {
                int j = iRng.Next(i + 1);
                (ioList[i], ioList[j]) = (ioList[j], ioList[i]);
            }
        }

        /// <summary>優先層的一行說明（開場／換骰宣告共用一份）。優先**不是指定**，仍是參考。</summary>
        public static void AppendPriorityNote(StringBuilder ioBody, SCP_FreeTimeRoll iRoll)
        {
            int aPri = iRoll.PriorityCount;
            if (aPri <= 0) return;
            ioBody.AppendLine(iRoll.IsLive
                ? $"⭐ 優先層 {aPri} 項排在前面（含📺直播中；層內仍隨機、不強制）"
                : $"⭐ 優先層 {aPri} 項排在前面（條件成立才會進來；層內仍隨機、不強制）");
        }

        /// <summary>
        /// 回傳檔的骰面段。**要能直接下 op=pick 的東西是具體活動的 id** ⇒ 組項一定要把組員 id 列出來。
        /// 骰面只印「是什麼」，**不印「怎麼做」**（md 全文路徑在 op=pick 之後才印 —— Tim 2026-08-21）。
        /// </summary>
        public static void AppendDiceSection(StringBuilder ioR, SCP_FreeTimeRoll iRoll, string iPersona)
        {
            ioR.AppendLine("## dice（兩層隨機排序，僅供參考 — 自由意志優先；無明確意圖從前 3 挑）");
            // ⚠ 這一行是**圖例** —— 改 gating 任何一個 kind 條件或飢餓判準 ⇒ **回來改這一行**
            //   （🩸 2026-09-11：Chess 條件改了而這行還寫著舊的 —— 骰面自己解釋錯自己）。
            ioR.AppendLine("- ⭐＝優先層；層內仍隨機。條件（任一成立即置頂）：");
            ioR.AppendLine("    · 直播中　· 棋局**輪到你走**且對手也在自由時間　· **有人開了一局在等而你配得上去**");
            ioR.AppendLine("    · 永久繪圖券囤太多　· 💤 太久沒被選（**通用**飢餓置頂，判準不看活動是什麼）");
            ioR.AppendLine("- 做不成的活動**已隱藏**（例：沒開播時不列「觀看直播」）—— 清單長度會隨當下狀況變動，那是正常的。");
            ioR.AppendLine("- **同組收成同一項**；觸發特殊規則的活動會**脫離分組成單獨一項**排最前（理由跟著印在它旁邊）。");
            // ⚠ 跨 Cmd 指路一律帶主詞（TASK-0192）：本支的參數叫 `step=`，而 `op=` 屬於 free-time-activity
            //   —— 那支**也有**一個叫 `step` 的參數 ⇒ 少了主詞，讀者會把它接到手上這一支（🩸 kiara 2026-09-10）。
            ioR.AppendLine("- 挑好之後：`" + SCP_CmdRegistry.Invoke("free-time-activity --arg op=pick --arg persona=" + iPersona
                           + " --arg activity=<id>") + "`");
            ioR.AppendLine("  ⚠ 是 **free-time-activity** 不是本支 **free-time** —— 本支的參數叫 `step=`，那支叫 `op=`（而那支**也有**一個 `step`）。");
            ioR.AppendLine("  `activity=` 填**具體活動 id**（下面反引號裡那個），不是組名。");
            ioR.AppendLine("- ℹ 這裡只說「是什麼」；**怎麼做**（活動 md 全文路徑）在 `free-time-activity op=pick` 之後才印。");
            for (int i = 0; i < iRoll.List.Count; i++)
            {
                var aEntry = iRoll.List[i];
                if (aEntry.Items.Count == 1)
                {
                    var a = aEntry.Items[0];
                    string aFrom = aEntry.Hoisted && aEntry.FromGroup.Length > 0
                        ? $"（⭐ 自「{aEntry.FromGroup}」組脫離 —— 此刻特別值得做）" : "";
                    ioR.AppendLine($"{i + 1}. {(aEntry.Priority ? "⭐ " : "")}**{a.Name}**　`{a.Id}`{aFrom}"
                                   + (string.IsNullOrEmpty(a.How) ? "" : $" — {a.How}"));
                    continue;
                }
                ioR.AppendLine($"{i + 1}. {(aEntry.Priority ? "⭐ " : "")}**{aEntry.Label}**"
                               + $"（{aEntry.Items.Count} 項{(aEntry.TooLong ? "，本組全員本場時間不夠" : "")}）");
                foreach (var a in aEntry.Items)
                    ioR.AppendLine($"   - `{a.Id}` **{a.Name}**" + (string.IsNullOrEmpty(a.How) ? "" : $" — {a.How}"));
            }
            ioR.AppendLine($"- [清單來源: {iRoll.Source}]");
        }
    }
}
