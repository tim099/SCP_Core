// 區塊職責：**活動 session 管理頁** —— 列出每個人的場（進行中／殘留／已收工）、對殘留補收工。
// 物理意義：這是 Unity 那側 `UCL_SessionAdminPage` 的搬家版本（TASK-0127 ⑥）。
//           資料讀走 `SCP_ActivitySessionStore`（純讀）；關場走 `SCP_ActivitySessionStore.CloseVerified`
//           （與 `sessions op=close` 同一個門；TASK-0448 起就地做、不委派 Editor、不結算）。
// 數值影響：讀＝每次 Refresh 掃一次 `sessions/*.json`；寫＝只有「補收工」那一條，且要二段確認。
//
// ⚠ 三條界線是從舊頁**原樣搬過來的，不是新加的**：
//   ① **補收工只對「殘留」開放**（active 但已過 end_ts）。進行中的場要走該 kind 的 `step=end` ——
//      那裡才有收工公告與同場者判定，從後台直接關會留下對不上的帳。
//   ② 二段確認（第一次 arm、再按一次才真的動）—— 誤點的後果是關掉別人**真的在跑**的場。
//   ③ 「開啟資料夾」宿主沒能力就不畫（畫一顆按了沒事的鈕比沒有那顆鈕糟），改把路徑印成字。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）—— 不用 record、不用檔案級 namespace。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Paths;
using SCP.Core.Prefs;
using SCP.Core.Session;

namespace SCP.Core.Gui
{
    public sealed class SCP_GuiSessionAdminPage : SCP_GuiToolPage
    {
        public const string PageKey = "sessions";

        /// <summary>待確認補收工的那一個（session 欄位；值是 persona，空 ＝ 沒有待確認）。</summary>
        public const string PendingCloseId = "sessions/pending-close";

        // ⚠ 這裡曾經有一格 `SCP_PrefKey.String("sessions", "dataRoot")` —— **本頁自己存一份手填的資料根**。
        //   拿掉的理由不是重複而已：那是同一個值的第二份，而第二份可以跟 `senate.local.json` 那格說不一樣的話，
        //   而症狀是**本頁讀到另一棵樹的 session，然後每一列都顯示正常**。
        //   🩸 2026-09-04 的現場更難看：整個 CLI 早就解得出那個根（每支 cmd 都印 `data_root=…`），
        //   而本頁印著「還沒設定資料根」⇒ 我把自己的 bug 讀成了設定的缺口，還去寫了使用者的 prefs。
        //   ⇒ 資料根一律問宿主（`m_Ctx.AgentCommandsRoot`，＝「路徑管理」頁那一格），**本頁不存路徑**。

        /// <summary>視窗模式下兩次掃描之間至少隔多久（同 Process 頁：每幀掃目錄是穩定的效能坑，而它不會叫）。</summary>
        public const double RefreshIntervalSeconds = 2.0;

        readonly ISCP_GuiAppContext m_Ctx;

        /// <summary>宿主解出來的資料根（值／來源／取不到的原因）—— 每次 OnPush 重讀，本頁不快取成字串。</summary>
        SCP_PathResolution m_Root = new SCP_PathResolution("", "?", "還沒讀");
        List<SCP_ActivitySession> m_Rows = new List<SCP_ActivitySession>();
        List<string> m_Problems = new List<string>();
        DateTime m_LastRefreshUtc = DateTime.MinValue;
        string? m_Message;

        public SCP_GuiSessionAdminPage(ISCP_GuiAppContext iCtx) : base() { m_Ctx = iCtx; }

        public override string Key { get { return PageKey; } }
        public override string Title { get { return "Session 管理"; } }
        public override string? MenuGroup { get { return "診斷"; } }

        public override void OnPush()
        {
            base.OnPush();
            m_Root = m_Ctx.AgentCommandsRoot;
            Refresh();
        }

        // ── 讀 ────────────────────────────────────────────────────

        void Refresh()
        {
            m_Problems = new List<string>();
            m_Rows = !HasRoot
                ? new List<SCP_ActivitySession>()
                : SCP_ActivitySessionStore.LoadAll(new SCP_DataRoot(m_Root.Value), m_Problems);
            m_LastRefreshUtc = DateTime.UtcNow;
        }

        void RefreshIfDue()
        {
            if (!SCP_GuiHost.RedrawsContinuously) { if (m_LastRefreshUtc == DateTime.MinValue) Refresh(); return; }
            if ((DateTime.UtcNow - m_LastRefreshUtc).TotalSeconds >= RefreshIntervalSeconds) Refresh();
        }

        // ── 畫 ────────────────────────────────────────────────────

        protected override void DrawContent(SCP_Ui g)
        {
            g.Note("每個人的活動 session（自由時間／觀影…）。**一人一檔位**：`<資料根>/sessions/<persona>.json`，"
                   + "kind 是檔案裡的欄位，不是路徑段。");

            DrawRootRow(g);
            if (!HasRoot)
            {
                // 三態不同形：「沒設定根」與「取不到」都不是「沒有 session」，而它們彼此也不同形。
                g.Note("⚠ 本頁**沒有資料來源** ⇒ 這不是「沒有人在 session」，是「量不到」。原因："
                       + (string.IsNullOrEmpty(m_Root.Error) ? "資料根是空的" : m_Root.Error));
                g.Note("· 要設它請去「路徑管理」頁（`senate ui --page paths`）的 **AgentCommands 資料根** 那一格"
                       + " —— **本頁不存路徑**，它讀的就是那一格。");
                return;
            }

            RefreshIfDue();
            DrawToolRow(g);
            g.Separator();
            DrawRows(g);

            for (int i = 0; i < m_Problems.Count; ++i) g.Note("⚠ " + m_Problems[i]);
            g.Note("· 掃描範圍（已登記的 kind）：" + string.Join(" / ", SCP_ActivitySessionKind.Kinds)
                   + "　⚠ 未登記的 kind 本頁看不到 —— 「沒查到」不等於「他不在任何 session」");
            // ⚠ 這一行是**語意**不是裝飾：沒有它，「兩個人合法並存」與「互斥守衛壞了」在本頁同形。
            //   kind 名單取自 `GlobalExclusiveKinds` 而不是寫死「Coding」—— 表變了這行要跟著變，
            //   ⛔ 否則它自己就會變成下一句過期的字面（TASK-0210 治的正是那個）。
            g.Note("· `" + string.Join(" / ", SCP_ActivitySessionKind.GlobalExclusiveKinds)
                   + "` 是**範圍**互斥：範圍不重疊的人可以同時 ● 進行中"
                   + " ⇒ **兩列同時進行中不代表守衛壞了**；未宣告範圍的那一場退化成全域獨佔（擋所有人）");
            if (m_Message != null) g.Note(m_Message);
        }

        /// <summary>資料根解出來了沒有（`Error` 有值或值是空的 ⇒ 沒有資料來源）。</summary>
        bool HasRoot { get { return m_Root.Error == null && m_Root.Value.Length > 0; } }

        // ⚠ **唯讀一行，沒有輸入框也沒有儲存鈕**（2026-09-04 改）：資料根的設定只有一處。
        //   印出 `Origin`（手填／auto ⇒ 由 ProjectRoot 推導）是刻意的 ——
        //   「這個值是誰給的」比「這個值是什麼」更常是問題的答案。
        void DrawRootRow(SCP_Ui g)
        {
            g.Note("· AgentCommands 資料根：`" + (m_Root.Value.Length > 0 ? m_Root.Value : "（解不出來）")
                   + "`　來源：" + m_Root.Origin + "　—— 設定在「路徑管理」頁，本頁只讀");
        }

        void DrawToolRow(SCP_Ui g)
        {
            string aDir = SCP_ActivitySessionStore.Dir(new SCP_DataRoot(m_Root.Value));
            int aAction = 0;   // 0 none / 1 refresh / 2 open dir
            using (g.Row())
            {
                if (g.Button("重新整理", "sessions/refresh")) aAction = 1;
                if (SCP_GuiHost.RevealInFileManager != null && g.Button("開啟資料夾", "sessions/open-dir")) aAction = 2;
            }
            int aRunning = 0, aStale = 0;
            DateTime aNow = DateTime.Now;
            for (int i = 0; i < m_Rows.Count; ++i)
            {
                if (m_Rows[i].IsRunningAt(aNow, out _)) aRunning++;
                else if (m_Rows[i].active) aStale++;
            }
            g.Note("session 目錄：`" + aDir + "`　共 " + m_Rows.Count + " 份"
                   + "（● 進行中 " + aRunning + "／▲ 殘留 " + aStale + "）"
                   + (m_LastRefreshUtc == DateTime.MinValue ? "" : "　（讀於 " + m_LastRefreshUtc.ToLocalTime().ToString("HH:mm:ss") + "）"));

            if (aAction == 1) { Refresh(); m_Message = "・已重新整理（磁碟現況，" + m_Rows.Count + " 份）"; }
            else if (aAction == 2) OpenDir(aDir);
        }

        void OpenDir(string iDir)
        {
            Func<string, string>? aReveal = SCP_GuiHost.RevealInFileManager;
            if (aReveal == null) { m_Message = "⚠ 這個環境開不了檔案總管 —— 目錄是 " + iDir; return; }
            try { if (!Directory.Exists(iDir)) Directory.CreateDirectory(iDir); }
            catch (Exception e) { m_Message = "⚠ 建不出 session 目錄 " + iDir + "：" + e.GetType().Name + ": " + e.Message; return; }
            string aResult = aReveal(iDir);
            m_Message = string.IsNullOrWhiteSpace(aResult) ? null : aResult;
        }

        // ── 列 ────────────────────────────────────────────────────

        void DrawRows(SCP_Ui g)
        {
            if (m_Rows.Count == 0)
            {
                g.Note("（這個資料根底下沒有任何 session 檔 —— 有人開過場之後才會出現）");
                return;
            }

            string aPending = g.FieldValue(PendingCloseId, "");
            DateTime aNow = DateTime.Now;
            string? aArm = null;
            string? aDoClose = null;
            bool aCancel = false;
            bool aPendingStillStale = false;

            for (int i = 0; i < m_Rows.Count; ++i)
            {
                SCP_ActivitySession aS = m_Rows[i];
                bool aRun = aS.IsRunningAt(aNow, out DateTime? aEnd);
                bool aStale = !aRun && aS.active;
                string aKind = aS.kind.Length == 0 ? "(未標 kind)" : aS.kind;
                if (!SCP_ActivitySessionKind.IsRegistered(aS.kind)) aKind += "（未登記）";
                string aState = aRun ? "● 進行中" : aStale ? "▲ 殘留（active 但已過 end_ts）" : "○ 已收工";

                using (g.Row())
                {
                    g.Label(aS.persona);
                    g.Label(aKind);
                    g.Label(aState);
                    if (aRun && aEnd.HasValue)
                        g.Label("剩 " + ((int)Math.Max(0, (aEnd.Value - aNow).TotalMinutes)) + " 分");
                    else if (!aS.active && aS.end_reason.Length > 0)
                        g.Label("reason=" + aS.end_reason);

                    // 區塊職責：把這一場宣告的**施工範圍**畫出來。
                    // 物理意義：只對**全域互斥**的 kind 畫 —— 其餘 kind 沒有「範圍」這個概念，
                    //           替它們畫一個永遠空的欄位，讀的人會把它讀成「這個人沒宣告」。
                    // 數值影響：純讀 `Raw`（`ScopeOf` 是唯一入口，⛔ 不在這裡各自 `Raw["scope"]`）；零 IO。
                    // 🩸 為什麼它值得佔一格版面（2026-09-15 summit 一小時內兩次現場）：
                    //   本頁印得出 `running=2` 卻印不出「他們各自在哪一塊」⇒
                    //   **「兩個人合法並存」與「互斥守衛壞了」在這個畫面上同形**，
                    //   而我兩次都用推的去補那一格，兩次都推錯（一次差點去修一個沒壞的守衛，
                    //   一次拿十分鐘前的列表當現況）。⇒ 補的是讀數，不是提醒。
                    // ⚠ 已收工的場仍印範圍（那是歷史事實，狀態欄已寫著「○ 已收工」）；
                    //   但「未宣告」那句**只對還擋得到人的場**印 —— 對收工的場說「擋所有人」是假的。
                    if (SCP_ActivitySessionKind.IsGlobalExclusive(aS.kind))
                    {
                        string aScope = SCP_ActivitySessionStore.ScopeOf(aS);
                        if (aScope.Length > 0) g.Label("範圍=" + aScope);
                        else if (aRun || aStale) g.Label("⚠ 未宣告範圍 ⇒ 全域獨佔（擋所有人）");
                    }

                    // ① 只有殘留能從這裡收。進行中的場**不畫鈕** —— 畫了就是在邀請人做那件不該做的事。
                    if (!aStale) continue;
                    bool aArmed = aPending == aS.persona;
                    if (aArmed) aPendingStillStale = true;
                    if (g.Button(aArmed ? "⚠ 再按一次確認補收工" : "🧹 補收工", "sessions/close/" + aS.persona))
                    {
                        if (aArmed) aDoClose = aS.persona; else aArm = aS.persona;
                    }
                    if (aArmed && g.Button("取消", "sessions/cancel/" + aS.persona)) aCancel = true;
                }
            }

            // arm 過的那一筆已經不是殘留了（別人先收了／它被重開）⇒ 自己解除，不要留一顆會誤觸的鈕。
            if (aPending.Length > 0 && !aPendingStillStale && aDoClose == null)
            {
                g.SetField(PendingCloseId, "");
                m_Message = "・`" + aPending + "` 已經不是殘留（有人先收了或它被重開）⇒ 待確認自動解除";
            }
            if (aCancel) { g.SetField(PendingCloseId, ""); m_Message = "・已取消（沒有動任何 session）"; }
            else if (aArm != null)
            {
                g.SetField(PendingCloseId, aArm);
                m_Message = "⚠ 待確認：再按一次才會真的關掉 `" + aArm + "` 的場";
            }
            else if (aDoClose != null)
            {
                g.SetField(PendingCloseId, "");
                StartClose(aDoClose);
            }
        }

        // ── 關場 ────────────────────────────────────────────────

        /// <summary>
        /// 關一場（同步）。關場是本地翻三欄＋回讀（TASK-0448），幾毫秒的事 ⇒ 不再需要背景 task。
        /// <para>🩸 舊版是委派 Editor 的 1〜3 秒 round-trip，才有「背景 task＋⏳ 委派中」那一套；委派拔掉後它就是死碼。</para>
        /// </summary>
        void StartClose(string iPersona)
        {
            try { m_Message = CloseOne(m_Root.Value, iPersona); }
            catch (Exception e) { m_Message = "⚠ 關場炸了：" + e.GetType().Name + ": " + e.Message; }
            Refresh();   // 磁碟才是判準 —— 回讀之後再畫
        }

        /// <summary>實際那一步。⚠ 只讀寫檔案，不碰 UI 狀態。</summary>
        static string CloseOne(string iRoot, string iPersona)
        {
            var aRoot = new SCP_DataRoot(iRoot);
            SCP_ActivitySession? aS = SCP_ActivitySessionStore.Load(aRoot, iPersona);
            if (aS == null) return "⚠ `" + iPersona + "` 的 session 檔讀不回來 ⇒ 未動作";
            if (aS.IsRunningAt(DateTime.Now, out _))
                return "⛔ `" + iPersona + "` 的場又變回進行中了 ⇒ 未動作（進行中要走該 kind 的 step=end）";
            if (!aS.active) return "・`" + iPersona + "` 已經收過工 ⇒ 未動作";

            // TASK-0448：就地關（翻三欄＋回讀），不委派 Editor、不結算 —— 與 `sessions op=close` 同一個門。
            bool aClosed = SCP_ActivitySessionStore.CloseVerified(aRoot, iPersona, aS, "closed-by-admin-page");
            return "・關場：Senate 就地翻三欄（kind=" + (aS.kind.Length == 0 ? "未標" : aS.kind) + "）—— 不結算"
                   + "\n・回讀磁碟：" + (aClosed ? "active=false ✅ 關成了" : "active=true ❌ **沒關成**");
        }
    }
}
