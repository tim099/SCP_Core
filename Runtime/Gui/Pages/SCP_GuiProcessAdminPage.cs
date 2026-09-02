// 區塊職責：**Process 管理頁** —— SCP_ProcessRegistry 的 UI 入口：列出所有登記過的 process、
//           即時身分驗證（Alive / Dead / PidReused / Unknown）、防誤殺 kill、殘留記錄清理。
// 物理意義：對照 Unity 端的 UCL_ProcessAdminPage（2026-07-27 Tim 拍板那套的頁面半邊）。
//           它是 TASK-0101，也是 Senate 常駐 Server（TASK-0100）的第一格：Server 之後會用
//           tag `senate_server` 把自己登記進同一個 registry，這一頁就是人看得到它的地方。
//           ⛔ 本頁**不 spawn 任何 process** —— 它只讀、只 kill 身分驗證過的、只清殘檔。
// 數值影響：讀 SCP_ProcessRegistry（每筆 Validate 會打一次 OS API）；寫入只有三種：
//           KillRegistered（Alive 且二段確認）、Unregister（非 Alive 的記錄檔）、CleanupStale。
//
// ⚠ 刻意跟 UCL 那版不同的三格：
//   ① **二段確認住 session 欄位，不用 5 秒計時器。** CLI 每次呼叫是新 process ⇒ 頁面欄位裡的
//      「已 arm」活不過一個指令，而計時器在純文字那側根本沒有第二幀可以過期
//      （SubmoduleSyncPage 2026-08 踩過：待確認態放頁面欄位 ⇒ 純文字那側永遠停在第一步）。
//      ⇒ 待確認的那顆用 `<tag>_<pid>` 記在 session，直到確認／取消／它不再 Alive 為止。
//   ② **「開啟資料夾」跟工具列的「原始碼」鈕同規矩**：宿主沒裝 SCP_GuiHost.RevealInFileManager
//      就不畫那顆鈕（畫一顆按了沒事的鈕比沒有那顆鈕糟），改把路徑印成字。
//   ③ **registry 沒 Configure 是一種狀態，不是「沒有 process」** —— 三態同形是這套系統最貴的錯，
//      所以那一格單獨印出來，而且不畫空表。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）—— 不用 record、不用檔案級 namespace。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Proc;

namespace SCP.Core.Gui
{
    public sealed class SCP_GuiProcessAdminPage : SCP_GuiToolPage
    {
        public const string PageKey = "process";

        /// <summary>待確認 kill 的那顆（session 欄位；值是 <c>&lt;tag&gt;_&lt;pid&gt;</c>，空 ＝ 沒有待確認）。</summary>
        public const string PendingKillId = "process/pending-kill";

        /// <summary>
        /// 視窗模式下兩次 Validate 之間至少隔多久 —— 每幀對每顆 process 打 OS API 是穩定的效能坑，
        /// 而它不會叫（UCL 那版也是 2 秒）。純文字／指令模式每次都是新 process，這個節流自然不生效。
        /// </summary>
        public const double RefreshIntervalSeconds = 2.0;

        List<KeyValuePair<SCP_ProcessRecord, SCP_ProcessStatus>> m_Rows =
            new List<KeyValuePair<SCP_ProcessRecord, SCP_ProcessStatus>>();

        /// <summary>上一次 Refresh 的時刻（UTC）。MinValue ＝ 還沒讀過。</summary>
        DateTime m_LastRefreshUtc = DateTime.MinValue;

        /// <summary>上一次動作的結果（成功或失敗都要有話說；null ＝ 這一輪還沒人動過）。</summary>
        string? m_Message;

        /// <summary>顯式呼叫 base()：SCP_GuiToolPage 靠這一格抓原始碼路徑（隱式 ctor 拿不到）。</summary>
        public SCP_GuiProcessAdminPage() : base() { }

        public override string Key { get { return PageKey; } }
        public override string Title { get { return "Process 管理"; } }
        public override string? MenuGroup { get { return "診斷"; } }

        public override void OnPush() { base.OnPush(); Refresh(); }

        void Refresh()
        {
            m_Rows = SCP_ProcessRegistry.Enabled
                ? SCP_ProcessRegistry.LoadAllWithStatus()
                : new List<KeyValuePair<SCP_ProcessRecord, SCP_ProcessStatus>>();
            m_LastRefreshUtc = DateTime.UtcNow;
        }

        /// <summary>視窗模式節流；其他模式一律當作要重讀（每次呼叫都是新 process，快取本來就不存在）。</summary>
        void RefreshIfDue()
        {
            if (!SCP_GuiHost.RedrawsContinuously) { if (m_LastRefreshUtc == DateTime.MinValue) Refresh(); return; }
            if ((DateTime.UtcNow - m_LastRefreshUtc).TotalSeconds >= RefreshIntervalSeconds) Refresh();
        }

        protected override void DrawContent(SCP_Ui g)
        {
            g.Note("本程式開的每一顆外部 process 都登記在這裡。kill 前做 PID＋name＋start time 三重身分驗證，"
                   + "PID 被 OS 回收再發時**拒絕動手**（防誤殺）。");

            // ③ 三態：沒 Configure ≠ 沒有 process。這一格不畫表，直接說。
            if (!SCP_ProcessRegistry.Enabled)
            {
                g.Note("⚠ 宿主沒有 SCP_ProcessRegistry.Configure(registryDir) ⇒ 本頁**沒有資料來源**。"
                       + "這不是「沒有 process」，是「量不到」—— 宿主啟動時漏掛了那一行。");
                return;
            }

            RefreshIfDue();
            DrawToolRow(g);
            g.Separator();
            DrawRows(g);

            if (m_Message != null) g.Note(m_Message);
        }

        // ── 工具列（頁面內的那排，不是 SCP_GuiToolPage 的導覽列）────────

        void DrawToolRow(SCP_Ui g)
        {
            string aDir = SCP_ProcessRegistry.RegistryDir ?? "";
            // ⚠ 先收集動作、離開 Row 之後才執行 —— handler 裡 Refresh 會改 m_Rows，
            //   在 Row 中途改變版面會讓後面幾顆鈕的 id 跟著漂（同 SCP_GuiToolPage.DrawToolBar 的規矩）。
            int aAction = 0;   // 0 none / 1 refresh / 2 cleanup / 3 open dir
            using (g.Row())
            {
                if (g.Button("重新整理", "process/refresh")) aAction = 1;
                if (g.Button("清理失效記錄（Dead／PID 已易主）", "process/cleanup")) aAction = 2;
                // ② 沒有 reveal 能力就不畫這顆；路徑下面那行 Note 一定印，讓人至少能自己去開。
                if (SCP_GuiHost.RevealInFileManager != null && g.Button("開啟資料夾", "process/open-dir")) aAction = 3;
            }
            g.Note($"登記目錄：`{aDir}`　共 {m_Rows.Count} 筆記錄"
                   + (m_LastRefreshUtc == DateTime.MinValue ? "" : $"　（讀於 {m_LastRefreshUtc.ToLocalTime():HH:mm:ss}）"));

            if (aAction == 1)
            {
                Refresh();
                m_Message = $"・已重新整理（磁碟現況，{m_Rows.Count} 筆）";
            }
            else if (aAction == 2)
            {
                int aRemoved = SCP_ProcessRegistry.CleanupStale();
                Refresh();
                // 0 筆也要說 —— 「按了沒事」跟「按了但沒有東西可清」在畫面上要分得出來。
                m_Message = $"✓ 清理失效記錄：移除 {aRemoved} 筆（Dead／PID 已易主）；Unknown 不動";
            }
            else if (aAction == 3)
            {
                OpenRegistryDir(aDir);
            }
        }

        void OpenRegistryDir(string iDir)
        {
            Func<string, string>? aReveal = SCP_GuiHost.RevealInFileManager;
            if (aReveal == null) { m_Message = $"⚠ 這個環境開不了檔案總管 —— 目錄是 {iDir}"; return; }
            try
            {
                // 目錄可能還不存在（從來沒登記過任何 process）—— 建起來再開，
                // 否則 reveal 會對一個不存在的路徑失敗，而那看起來像「這顆鈕壞了」。
                if (!Directory.Exists(iDir)) Directory.CreateDirectory(iDir);
            }
            catch (Exception e)
            {
                m_Message = $"⚠ 建不出登記目錄 {iDir}：{e.GetType().Name}: {e.Message}";
                return;
            }
            string aResult = aReveal(iDir);
            // 成功時宿主可能回空字串（有視窗的宿主：跳出來的檔案總管本身就是讀數）—— 那就不畫。
            m_Message = string.IsNullOrWhiteSpace(aResult) ? null : aResult;
        }

        // ── 記錄列 ────────────────────────────────────────────────

        void DrawRows(SCP_Ui g)
        {
            if (m_Rows.Count == 0)
            {
                g.Note("（目前沒有登記記錄 —— C# spawn 端經 SCP_ProcessRegistry.Register 登記後會出現在這裡；"
                       + "Server 上線後 `senate_server` 那顆也在這張表）");
                return;
            }

            string aPending = g.FieldValue(PendingKillId, "");
            bool aPendingStillAlive = false;

            // 收集動作、離開所有 Box 之後再執行（同工具列規矩：handler 會 Refresh 改 m_Rows）。
            SCP_ProcessRecord? aKillTarget = null;
            SCP_ProcessRecord? aRemoveTarget = null;
            string? aArmKey = null;
            bool aCancel = false;

            for (int i = 0; i < m_Rows.Count; ++i)
            {
                SCP_ProcessRecord aRec = m_Rows[i].Key;
                SCP_ProcessStatus aStatus = m_Rows[i].Value;
                string aRowKey = RowKey(aRec);
                bool aArmed = aStatus == SCP_ProcessStatus.Alive && aPending == aRowKey;
                if (aArmed) aPendingStillAlive = true;

                using (g.Box($"{StatusMark(aStatus)}　[{aRec.Tag}]　PID {aRec.Pid}", "process/row/" + aRowKey))
                {
                    if (aRec.Description.Length > 0) g.Label(aRec.Description);
                    g.Note($"name: {aRec.ProcessName}　start: {FmtLocal(aRec.StartTimeUtcText)}　"
                           + $"registered_by: {(aRec.RegisteredBy.Length > 0 ? aRec.RegisteredBy : "—")} @ {FmtLocal(aRec.RegisteredAtUtcText)}");
                    if (aRec.CommandLine.Length > 0)
                        g.Note("cmd: " + (aRec.CommandLine.Length > 160 ? aRec.CommandLine.Substring(0, 160) + "…" : aRec.CommandLine));
                    g.Note("狀態：" + SCP_ProcessRegistry.StatusText(aStatus));

                    using (g.Row())
                    {
                        if (aStatus == SCP_ProcessStatus.Alive)
                        {
                            // ① 兩段式：第一按 arm（寫 session），第二按才真 kill。
                            if (aArmed)
                            {
                                if (g.Button($"⚠ 確定 kill PID {aRec.Pid}", "process/kill-confirm/" + aRowKey)) aKillTarget = aRec;
                                if (g.Button("取消", "process/kill-cancel/" + aRowKey)) aCancel = true;
                            }
                            else if (g.Button("Kill", "process/kill/" + aRowKey))
                            {
                                aArmKey = aRowKey;
                            }
                        }
                        else
                        {
                            // Dead / PidReused / Unknown ⇒ 只能移除記錄檔，**不提供 kill**（防誤殺是本頁存在的理由）。
                            // Unknown 也讓人手動移除：CleanupStale 刻意不碰它，但人看過之後可以決定。
                            if (g.Button("移除記錄", "process/remove/" + aRowKey)) aRemoveTarget = aRec;
                        }
                    }
                }
            }

            // 待確認的那顆已經不在 Alive 清單裡 ⇒ 自動解除，並說出來（不然那個 session 值會一直掛著）。
            if (aPending.Length > 0 && !aPendingStillAlive)
            {
                g.SetField(PendingKillId, "");
                m_Message = $"・待確認的 kill（{aPending}）已解除：那顆已不是 Alive";
            }

            if (aCancel)
            {
                g.SetField(PendingKillId, "");
                m_Message = "・已取消（沒有動任何 process）";
            }
            else if (aArmKey != null)
            {
                g.SetField(PendingKillId, aArmKey);
                m_Message = $"⚠ 再按一次「確定 kill」才會真的 kill {aArmKey}（或按取消）";
            }
            else if (aKillTarget != null)
            {
                g.SetField(PendingKillId, "");
                string aErr;
                bool aOk = SCP_ProcessRegistry.KillRegistered(aKillTarget, out aErr);
                Refresh();
                m_Message = aOk
                    ? $"✓ 已 kill [{aKillTarget.Tag}] PID {aKillTarget.Pid}（記錄檔一併移除）"
                    : $"✗ kill 拒絕／失敗 [{aKillTarget.Tag}] PID {aKillTarget.Pid}：{aErr}";
            }
            else if (aRemoveTarget != null)
            {
                SCP_ProcessRegistry.Unregister(aRemoveTarget.Pid, aRemoveTarget.Tag);
                Refresh();
                // 回讀：檔還在就是沒刪掉 —— 不拿「我呼叫了 Unregister」當成功。
                bool aGone = string.IsNullOrEmpty(aRemoveTarget.SourceFile) || !File.Exists(aRemoveTarget.SourceFile);
                m_Message = aGone
                    ? $"✓ 已移除記錄 [{aRemoveTarget.Tag}] PID {aRemoveTarget.Pid}（process 本身沒被碰）"
                    : $"✗ 記錄檔還在：{aRemoveTarget.SourceFile}（Warn 那條有原因）";
            }
        }

        // ── 小工具 ────────────────────────────────────────────────

        /// <summary>一列的識別字 ＝ 記錄檔的基底名（<c>&lt;tag&gt;_&lt;pid&gt;</c>）—— 跟磁碟上的檔一一對應。</summary>
        static string RowKey(SCP_ProcessRecord iRec) => iRec.Tag + "_" + iRec.Pid;

        /// <summary>純文字標記，不用 emoji —— 缺字不報錯，只會變成方塊（SenateFonts 的血證）。</summary>
        static string StatusMark(SCP_ProcessStatus iStatus)
        {
            switch (iStatus)
            {
                case SCP_ProcessStatus.Alive: return "● ALIVE";
                case SCP_ProcessStatus.Dead: return "・DEAD";
                case SCP_ProcessStatus.PidReused: return "▲ PID 已易主";
                default: return "？ 無法驗證";
            }
        }

        /// <summary>UTC ISO 文字 → 本地時間；解析不了就**原樣印**（印不出來跟「沒有時間」是兩件事）。</summary>
        static string FmtLocal(string iUtcIso)
        {
            if (string.IsNullOrEmpty(iUtcIso)) return "—";
            DateTime aTime;
            if (DateTime.TryParse(iUtcIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out aTime))
                return aTime.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            return iUtcIso;
        }
    }
}
