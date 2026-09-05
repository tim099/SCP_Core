// 區塊職責：**登入管理頁（最小版）** —— 設定 persona 信件夾根目錄、列出那底下的 persona 與上線狀態。
// 物理意義：對照 Unity 端的 UCL_LoginStatusPage，但只保留兩件事：設定路徑、顯示狀態。
//           ⛔ 本版**沒有**手動登入／登出／清 lock —— 那些會寫別人的 session 狀態，
//           而這邊還沒有任何一格讀數證明它寫得對。少做的功能是選擇，不是遺漏。
//
//           ⭐ 2026-08-30 從 Senate.Cli/Pages/LoginStatusPage.cs 搬進 SCP_Core（六步的第 4 步）。
//           搬得動的前提是前三步：掃描層進了 SCP_Core（SCP_PersonaLetters）、
//           設定改走 prefs 介面（本頁**不知道** senate.local.json 這個檔名的存在）。
// 數值影響：**畫面純讀，本頁零寫入**（走 SCP_PersonaLetters.Scan）。
//           🩸 2026-09-05 拿掉了「信件夾根」的輸入框與儲存鈕：本頁曾自己走
//           `Prefs.Read(awakening.lettersRoot)` 讀**存起來的原始值**，而那一格是 `[SCP_PathAuto]` 的 ——
//           有人填 `auto` 時本頁會拿字面 `"auto"` 去掃目錄，掃不到 ⇒ 畫面說「這裡真的還沒有人」，
//           **而同一台的 CLI 解得出真正的路徑**。兩邊都不報錯，差別只在誰走了解析器。
//           ⇒ 路徑一律問宿主（`m_Ctx.LettersRoot`，＝「路徑管理」頁那一格），要改值去那一頁。
//
// ⚠ 這一頁最容易騙人的一格是「離線」：lock 檔在但讀不了時，把那個人畫成離線的畫面
//   跟「真的離線」一模一樣。⇒ 狀態是三態，未知就印「未知」，並把原因印在上面。
//   （SCP_PersonaLetters 那邊有同一條註解 —— 兩邊都要守，因為顯示端也可以自己把三態壓成兩態。）
// ⚠ lock 住 `letters/<p>/profile/_session.json`（TASK-0105）—— 本頁**沒有** sessionDir 設定，
//   lock 的位置由 persona 目錄唯一決定，沒有第二個可以填錯的地方。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Gui
{
    public sealed class SCP_GuiLoginStatusPage : SCP_GuiToolPage
    {
        readonly ISCP_GuiAppContext m_Ctx;

        /// <summary>宿主解出來的信件庫根（值／來源／取不到的原因）—— 每次 OnPush 重讀，本頁不快取成字串。</summary>
        SCP_PathResolution m_Root = new SCP_PathResolution("", "?", "還沒讀");

        /// <summary>上一次掃描結果。null ＝ 這一輪還沒掃過。</summary>
        SCP_PersonaScan? m_Scan;

        /// <summary>上一次動作的結果（成功或失敗都要有話說）。</summary>
        string? m_Message;

        public SCP_GuiLoginStatusPage(ISCP_GuiAppContext iCtx) : base() { m_Ctx = iCtx; }

        public override string Key { get { return PageKey; } }
        public const string PageKey = "login";
        public override string Title { get { return "登入狀態"; } }
        public override string? MenuGroup { get { return "診斷"; } }

        /// <summary>讀檔在 OnPush 不在建構子 —— 頁面目錄會建一次實例只為了讀標題（同專案關聯頁）。</summary>
        public override void OnPush() { base.OnPush(); Load(); }

        void Load()
        {
            // 三態逐格處理（解出來／沒有人填過／取不到）—— 這正是宿主回 SCP_PathResolution 的理由。
            // ⚠ 拿的是**解析後**的值：`auto` 這種原始值到不了這裡，也就不會被當成目錄去掃。
            m_Root = m_Ctx.LettersRoot;
            if (!HasRoot) { m_Scan = null; return; }
            Rescan();
        }

        void Rescan()
        {
            m_Scan = SCP_PersonaLetters.Scan(SCP_PersonaLetters.CleanPath(m_Root.Value));
        }

        /// <summary>信件庫根解出來了沒有（<c>Error</c> 有值或值是空的 ⇒ 沒有資料來源）。</summary>
        bool HasRoot { get { return m_Root.Error == null && m_Root.Value.Length > 0; } }

        protected override void DrawContent(SCP_Ui g)
        {
            DrawRootRow(g);
            if (!HasRoot)
            {
                // 三態不同形：「沒設定根」與「取不到」都不是「這裡沒有人」，而它們彼此也不同形。
                g.Note("⚠ 本頁**沒有資料來源** ⇒ 這不是「還沒有人登入」，是「量不到」。原因："
                       + (string.IsNullOrEmpty(m_Root.Error) ? "信件庫根是空的（沒有人填過）" : m_Root.Error));
                g.Note("· 要設它請去「路徑管理」頁（`senate ui --page paths`）的 **persona 信件庫根** 那一格"
                       + " —— **本頁不存路徑**，它讀的就是那一格。");
                if (g.Button("重新讀取", "login/reload")) Load();
                if (m_Message != null) g.Note(m_Message);
                return;
            }

            g.Separator();
            DrawStatus(g);

            if (m_Message != null) g.Note(m_Message);
        }

        // ── 設定 ──────────────────────────────────────────────────────

        // ⚠ **唯讀一行，沒有輸入框也沒有儲存鈕**（2026-09-05 改）：信件庫根的設定只有一處。
        //   印出 `Origin`（手填／`auto` ⇒ 由 AgentCommandsRoot 推導）是刻意的 ——
        //   「這個值是誰給的」比「這個值是什麼」更常是問題的答案，而這一格正好是
        //   **本頁上一版讀錯的那個東西**（讀了原始值，於是 `auto` 會變成一個掃不到的目錄名）。
        // ⚠ 鈕面不放 emoji：視窗那側的字型是 msjh＋seguisym，🔄 這類字彙不在裡面 ⇒ 畫成 `?`
        //   （2026-09-05 截圖實測）。純文字那側看起來正常 —— 兩側不同形，而只有截圖看得到。
        void DrawRootRow(SCP_Ui g)
        {
            g.Note("· persona 信件庫根：`" + (m_Root.Value.Length > 0 ? m_Root.Value : "（解不出來）")
                   + "`　來源：" + m_Root.Origin + "　—— 設定在「路徑管理」頁，本頁只讀");

            using (g.Row())
            {
                if (g.Button("重新讀取", "login/reload"))
                {
                    Load();
                    m_Message = "・已重讀路徑並重新掃描（讀的是磁碟現況，不是上一次的快取）";
                }
                if (HasRoot && g.Button("重新掃描", "login/rescan"))
                {
                    Rescan();
                    m_Message = "・已重新掃描（路徑沒有重讀 —— 要連路徑一起重讀請按「重新讀取」）";
                }
            }
        }

        // ── 狀態 ──────────────────────────────────────────────────────

        void DrawStatus(SCP_Ui g)
        {
            if (m_Scan == null) { g.Note("（還沒掃描）"); return; }
            SCP_PersonaScan aScan = m_Scan;

            // 問題先講。⚠ 有問題卻只顯示一張空表 ＝ 讓「量不到」長得像「沒有人」。
            foreach (string aProblem in aScan.Problems) g.Note($"⚠ {aProblem}");

            // ⚠ 掃描沒走完就**什麼讀數都不要畫**：那些欄位這時是預設值不是量到的值，
            //   而預設值畫出來會變成一句斬釘截鐵的假話（人數 0 —— 那一輪根本沒列過目錄）。
            if (!aScan.Enumerated) return;

            g.Note($"lock：`<persona>/{SCP.Core.Paths.SCP_LettersPaths.ProfileDirName}/"
                   + $"{SCP.Core.Paths.SCP_LettersPaths.SessionLockFileName}`（檔在＝在線；位置由 persona 目錄唯一決定）");

            // 未知那一格單獨列出來 —— 它跟離線不同類，混在一句「N 人離線」裡就看不見了。
            g.Label($"persona {aScan.Personas.Count} 人　"
                    + $"在線 {aScan.OnlineCount}　離線 {aScan.OfflineCount}　未知 {aScan.UnknownCount}");

            if (aScan.Personas.Count == 0)
            {
                g.Note($"這個資料夾底下沒有任何含 `{SCP.Core.Paths.SCP_LettersPaths.ProfileDirName}/` 的子目錄 —— "
                       + "要嘛路徑指錯了，要嘛這裡真的還沒有人。**這兩者本頁分不出來**，請自己確認路徑。");
                return;
            }

            using (g.Table("persona", "狀態", "agent", "model", "登入時間"))
            {
                foreach (SCP_PersonaStatus p in aScan.Personas)
                {
                    g.TableRow(
                        p.Name,
                        StatusText(p),
                        p.Agent.Length > 0 ? p.Agent : "—",
                        p.Model.Length > 0 ? p.Model : "—",
                        p.LockedAt.Length > 0 ? p.LockedAt : "—");
                }
            }

            // 在線的人多印一行細節（lock 檔裡有什麼就印什麼，不從別處補）
            foreach (SCP_PersonaStatus p in aScan.Personas)
            {
                if (p.Online == SCP_PersonaOnline.Online)
                {
                    g.Note($"● {p.Name}　wake#{p.WakeExpected}　session_key={p.SessionKey}　pid={p.Pid}"
                           + $"　bank={p.BankAccount}　lock={p.LockPath}");
                }
                else if (p.LockError != null)
                {
                    g.Note($"？ {p.Name}　lock 檔在但讀不了 ⇒ **狀態未知不是離線**：{p.LockError}");
                }
            }
        }

        static string StatusText(SCP_PersonaStatus iStatus) => iStatus.Online switch
        {
            SCP_PersonaOnline.Online => "● 在線",
            SCP_PersonaOnline.Offline => "・離線",
            _ => "？ 未知",
        };
    }
}
