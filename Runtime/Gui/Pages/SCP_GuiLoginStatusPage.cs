// 區塊職責：**登入管理頁（最小版）** —— 設定 persona 信件夾根目錄、列出那底下的 persona 與上線狀態。
// 物理意義：對照 Unity 端的 UCL_LoginStatusPage，但只保留兩件事：設定路徑、顯示狀態。
//           ⛔ 本版**沒有**手動登入／登出／清 lock —— 那些會寫別人的 session 狀態，
//           而這邊還沒有任何一格讀數證明它寫得對。少做的功能是選擇，不是遺漏。
//
//           ⭐ 2026-08-30 從 Senate.Cli/Pages/LoginStatusPage.cs 搬進 SCP_Core（六步的第 4 步）。
//           搬得動的前提是前三步：掃描層進了 SCP_Core（SCP_PersonaLetters）、
//           設定改走 prefs 介面（本頁**不知道** senate.local.json 這個檔名的存在）。
// 數值影響：畫面純讀（走 SCP_PersonaLetters.Scan）。唯一的寫入是「儲存路徑」那顆鈕，
//           走 ISCP_Prefs.Write —— 值落在哪個檔由宿主決定，本頁不知道也不該知道。
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
using SCP.Core.Prefs;

namespace SCP.Core.Gui
{
    public sealed class SCP_GuiLoginStatusPage : SCP_GuiToolPage
    {
        readonly ISCP_GuiAppContext m_Ctx;

        /// <summary>信件夾根的 key —— 宿主決定它落在哪個檔（見 ISCP_Prefs）。</summary>
        static readonly SCP_PrefKey<string> KeyLettersRoot = SCP_PrefKey.String("awakening", "lettersRoot", "");

        /// <summary>編輯中的路徑（按「儲存」才寫回檔案）。</summary>
        string m_RootDraft = "";

        /// <summary>檔案裡目前的值 —— 用來判斷「改了還沒存」。</summary>
        string m_RootSaved = "";

        /// <summary>設定檔讀不到／壞掉的原因（null ＝ 沒問題）。</summary>
        string? m_ConfigError;

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
            m_ConfigError = null;

            // 三態逐格處理 —— 這正是 prefs 這一層存在的理由：
            //   ReadError（檔壞了）與 Missing（還沒設定）**不同形**，
            //   混起來的話「壞檔」會長得像「還沒設定」，而那時按下儲存就是不可逆的覆寫。
            SCP_PrefRead<string> aRead = m_Ctx.Prefs.Read(KeyLettersRoot);
            if (aRead.State == SCP_PrefState.ReadError)
            {
                m_ConfigError = $"設定讀不了，本頁不提供編輯（沒有被動過）：{aRead.Error}";
                m_Scan = null;
                return;
            }
            m_RootSaved = SCP_PersonaLetters.CleanPath(aRead.IsPresent ? aRead.Value : "");
            m_RootDraft = m_RootSaved;
            Rescan();
        }

        void Rescan()
        {
            m_Scan = SCP_PersonaLetters.Scan(m_RootSaved);
        }

        protected override void DrawContent(SCP_Ui g)
        {
            if (m_ConfigError != null)
            {
                g.Note($"⚠ {m_ConfigError}");
                if (g.Button("重新讀取", "login/reload")) Load();
                return;
            }

            DrawSetting(g);
            g.Separator();
            DrawStatus(g);

            if (m_Message != null) g.Note(m_Message);
        }

        // ── 設定 ──────────────────────────────────────────────────────

        void DrawSetting(SCP_Ui g)
        {
            using (g.Box("信件夾設定"))
            {
                g.Note("persona 信件庫的根目錄，例如 `D:/Unity/Bar/AgentCommands/ChatTavern/baton/letters`。"
                       + "存進宿主的設定（awakening.lettersRoot）—— 之後的登入／早安流程從那裡拿路徑。");

                m_RootDraft = g.TextField("信件夾根目錄", m_RootDraft, "login/root");

                bool aDirty = !string.Equals(
                    SCP_PersonaLetters.CleanPath(m_RootDraft), m_RootSaved, StringComparison.Ordinal);

                using (g.Row())
                {
                    if (g.Button("💾 儲存", "login/save")) Save();
                    if (aDirty && g.Button("放棄改動", "login/revert"))
                    {
                        m_RootDraft = m_RootSaved;
                        m_Message = "・已還原成檔案裡的值（檔案沒有被動過）";
                    }
                    if (g.Button("🔄 重新掃描", "login/rescan"))
                    {
                        Rescan();
                        m_Message = "・已重新掃描（讀的是磁碟現況，不是上一次的快取）";
                    }
                }

                if (aDirty) g.Note("⚠ 有改動還沒儲存 —— 下面那張表用的仍是**已儲存**的路徑（本頁刻意不自動存）");
            }
        }

        void Save()
        {
            (bool aOk, string aMsg) = m_Ctx.Prefs.Write(KeyLettersRoot, m_RootDraft);
            m_Message = (aOk ? "✓ " : "⚠ ") + aMsg;
            if (!aOk) return;
            m_RootSaved = SCP_PersonaLetters.CleanPath(m_RootDraft);
            Rescan();
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
