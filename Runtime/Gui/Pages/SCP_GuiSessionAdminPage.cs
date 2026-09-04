// 區塊職責：**活動 session 管理頁** —— 列出每個人的場（進行中／殘留／已收工）、對殘留補收工。
// 物理意義：這是 Unity 那側 `UCL_SessionAdminPage` 的搬家版本（TASK-0127 ⑥）。
//           資料讀走 `SCP_ActivitySessionStore`（純讀）；**關場不在這裡做** ——
//           交給 close gateway（Senate ⇒ 委派 Editor 的 `SessionClose`，因為結算就是金流）。
// 數值影響：讀＝每次 Refresh 掃一次 `sessions/*.json`；寫＝只有「補收工」那一條，且要二段確認。
//
// ⚠ 三條界線是從舊頁**原樣搬過來的，不是新加的**：
//   ① **補收工只對「殘留」開放**（active 但已過 end_ts）。進行中的場要走該 kind 的 `step=end` ——
//      那裡才有收工公告與同場者判定，從後台直接關會留下對不上的帳。
//   ② 二段確認（第一次 arm、再按一次才真的動）—— 誤點的後果是關掉別人**真的在跑**的場。
//   ③ 「開啟資料夾」宿主沒能力就不畫（畫一顆按了沒事的鈕比沒有那顆鈕糟），改把路徑印成字。
//
// 🩸 ⛔ **委派不得同步阻塞畫面迴圈**（本頁最容易寫錯的一格）：
//   關場是一次 Cmd round-trip（檔案協議 ＋ Watcher 輪詢，1〜3 秒）。在會重畫的宿主上同步等待
//   ＝ 視窗凍住 1〜3 秒，而 `ui --soak` 的凍窗閘 2026-09-04 已退場 ⇒ **現在沒有機器抓得到它**。
//   ⇒ 會重畫的宿主走背景 task ＋ `⏳ 委派中` 態；不會重畫的宿主（CLI 單次 render）**才**同步跑
//     —— 那裡沒有第二幀可以觀察結果，背景 task 等於把答案丟掉。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）—— 不用 record、不用檔案級 namespace。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
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

        /// <summary>
        /// 資料根**顯式設定**，不從信件夾根推導。
        /// <para>⚠ 那個推導寫得出來（信件夾是 `&lt;dataRoot&gt;/ChatTavern/baton/letters`），而它正是本專案
        /// 禁止的那一種：路徑不該被推導、該被傳遞。推導錯的症狀是**讀到另一棵樹的 session，然後全部顯示正常**。</para>
        /// </summary>
        static readonly SCP_PrefKey<string> KeyDataRoot = SCP_PrefKey.String("sessions", "dataRoot", "");

        /// <summary>視窗模式下兩次掃描之間至少隔多久（同 Process 頁：每幀掃目錄是穩定的效能坑，而它不會叫）。</summary>
        public const double RefreshIntervalSeconds = 2.0;

        readonly ISCP_GuiAppContext m_Ctx;

        string m_RootDraft = "";
        string m_RootSaved = "";
        List<SCP_ActivitySession> m_Rows = new List<SCP_ActivitySession>();
        List<string> m_Problems = new List<string>();
        DateTime m_LastRefreshUtc = DateTime.MinValue;
        string? m_Message;

        /// <summary>進行中的委派（null ＝ 沒有）。⚠ 它活在背景 thread 上，主迴圈只讀 <c>IsCompleted</c>。</summary>
        Task<string>? m_CloseJob;
        string m_CloseJobTarget = "";

        public SCP_GuiSessionAdminPage(ISCP_GuiAppContext iCtx) : base() { m_Ctx = iCtx; }

        public override string Key { get { return PageKey; } }
        public override string Title { get { return "Session 管理"; } }
        public override string? MenuGroup { get { return "診斷"; } }

        public override void OnPush()
        {
            base.OnPush();
            SCP_PrefRead<string> aRead = m_Ctx.Prefs.Read(KeyDataRoot);
            m_RootSaved = (aRead.IsPresent ? aRead.Value : "").Trim();
            m_RootDraft = m_RootSaved;
            Refresh();
        }

        // ── 讀 ────────────────────────────────────────────────────

        void Refresh()
        {
            m_Problems = new List<string>();
            m_Rows = m_RootSaved.Length == 0
                ? new List<SCP_ActivitySession>()
                : SCP_ActivitySessionStore.LoadAll(new SCP_DataRoot(m_RootSaved), m_Problems);
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
            if (m_RootSaved.Length == 0)
            {
                // 三態不同形：「沒設定根」不是「沒有 session」。
                g.Note("⚠ 還沒設定資料根 ⇒ 本頁**沒有資料來源**。這不是「沒有人在 session」，是「量不到」。");
                return;
            }

            PumpCloseJob();          // ⚠ 先收委派結果，再畫 —— 不然畫面會比磁碟晚一幀
            RefreshIfDue();
            DrawToolRow(g);
            g.Separator();
            DrawRows(g);

            for (int i = 0; i < m_Problems.Count; ++i) g.Note("⚠ " + m_Problems[i]);
            g.Note("· 掃描範圍（已登記的 kind）：" + string.Join(" / ", SCP_ActivitySessionKind.Kinds)
                   + "　⚠ 未登記的 kind 本頁看不到 —— 「沒查到」不等於「他不在任何 session」");
            if (m_CloseJob != null)
                g.Note("⏳ 委派中：正在請 Editor 關掉 `" + m_CloseJobTarget + "` 的場（1〜3 秒，畫面不會凍住）…");
            if (m_Message != null) g.Note(m_Message);
        }

        void DrawRootRow(SCP_Ui g)
        {
            bool aSave = false;
            using (g.Row())
            {
                m_RootDraft = g.TextField("AgentCommands 資料根", m_RootDraft, "sessions/root");
                if (g.Button("儲存", "sessions/save-root")) aSave = true;
            }
            if (!aSave) return;
            string aNext = m_RootDraft.Trim();
            (bool aOk, string aMsg) = m_Ctx.Prefs.Write(KeyDataRoot, aNext);
            if (!aOk) { m_Message = "⚠ 設定寫不進去：" + aMsg; return; }
            m_RootSaved = aNext;
            Refresh();
            m_Message = "✓ 資料根已存（" + (aNext.Length == 0 ? "（清空）" : aNext) + "）；本頁讀的就是它";
        }

        void DrawToolRow(SCP_Ui g)
        {
            string aDir = SCP_ActivitySessionStore.Dir(new SCP_DataRoot(m_RootSaved));
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

                    // ① 只有殘留能從這裡收。進行中的場**不畫鈕** —— 畫了就是在邀請人做那件不該做的事。
                    if (!aStale) continue;
                    if (m_CloseJob != null) { g.Label("（等前一筆委派完成）"); continue; }
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
                m_Message = "⚠ 待確認：再按一次才會真的關掉 `" + aArm + "` 的場"
                            + "（觀影場會**補結算＋發薪**，由 Editor 那端執行）";
            }
            else if (aDoClose != null)
            {
                g.SetField(PendingCloseId, "");
                StartClose(aDoClose);
            }
        }

        // ── 關場（委派）───────────────────────────────────────────

        /// <summary>
        /// 起一次關場。會重畫的宿主走背景 task；不會重畫的（CLI 單次 render）同步跑。
        /// </summary>
        /// <remarks>
        /// 🩸 兩種宿主要分開，理由不是效能是**答案會不會被看到**：
        /// CLI 那側整個 render 只有一幀，背景 task 的結果永遠沒有第二幀可以印 ——
        /// 而畫面會顯示「⏳ 委派中」然後程式就結束了，看起來像它沒做。
        /// </remarks>
        void StartClose(string iPersona)
        {
            string aRoot = m_RootSaved;
            if (SCP_GuiHost.RedrawsContinuously)
            {
                m_CloseJobTarget = iPersona;
                m_CloseJob = Task.Run(() => CloseOne(aRoot, iPersona));
                m_Message = null;
                return;
            }
            m_Message = CloseOne(aRoot, iPersona);
            Refresh();
        }

        /// <summary>收背景委派的結果（每幀呼叫；沒有進行中的就什麼都不做）。</summary>
        void PumpCloseJob()
        {
            if (m_CloseJob == null || !m_CloseJob.IsCompleted) return;
            Task<string> aJob = m_CloseJob;
            m_CloseJob = null;
            try { m_Message = aJob.Result; }
            catch (Exception e) { m_Message = "⚠ 委派本身炸了：" + e.GetType().Name + ": " + e.Message; }
            Refresh();   // 磁碟才是判準 —— 回讀之後再畫
        }

        /// <summary>實際那一步（背景 thread 上跑）。⚠ 只讀寫檔案與委派，不碰任何 UI 狀態。</summary>
        static string CloseOne(string iRoot, string iPersona)
        {
            var aRoot = new SCP_DataRoot(iRoot);
            SCP_ActivitySession? aS = SCP_ActivitySessionStore.Load(aRoot, iPersona);
            if (aS == null) return "⚠ `" + iPersona + "` 的 session 檔讀不回來 ⇒ 未動作";
            if (aS.IsRunningAt(DateTime.Now, out _))
                return "⛔ `" + iPersona + "` 的場又變回進行中了 ⇒ 未動作（進行中要走該 kind 的 step=end）";
            if (!aS.active) return "・`" + iPersona + "` 已經收過工 ⇒ 未動作（不重複結算）";

            SCP_ActivitySessionCloseResult aRes =
                SCP_ActivitySessionStore.CloseWithSettlement(aRoot, iPersona, aS, "closed-by-admin-page");

            string aWho = aRes.HasHandler ? "gateway（那一端連結算一起做）" : "**本層 base close**（沒有 gateway ⇒ 不結算，明確降級）";
            string aBody = "・關場路徑：" + aWho;
            for (int i = 0; i < aRes.SettleLines.Count; ++i) aBody += "\n  " + aRes.SettleLines[i];
            if (aRes.SettleError.Length > 0) aBody += "\n⚠ 那一端回報失敗：" + aRes.SettleError;
            // ⭐ 判準是回讀，不是誰說了什麼。
            aBody += "\n・回讀磁碟：" + (aRes.Closed ? "active=false ✅ 關成了" : "active=true ❌ **沒關成**");
            return aBody;
        }
    }
}
