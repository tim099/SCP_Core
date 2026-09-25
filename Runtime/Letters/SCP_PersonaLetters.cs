// 區塊職責：**persona 信件庫的掃描與上線狀態讀取** —— 那底下有哪些 persona、誰現在在線。
// 物理意義：persona 資料住在 `<資料根>/ChatTavern/baton/letters/<persona>/`；
//           「在線」的真相源是同一個目錄底下的 `profile/_session.json`
//           這顆 session lock（登入時寫、登出時刪；路徑唯一決定點 SCP_LettersPaths.SessionLockPath）。
//           ⇒ 本檔只讀檔、不動 registry；**唯一的寫入是 <see cref="ForceLogout"/>**（手動登出刪 lock，TASK-0294）。
//
//           ⭐ 2026-08-30 從 Senate.Core/PersonaLetters.cs 搬進來（六步的第 2 步）。
//           搬的是**掃描那一半**；「信件夾根設在哪」屬於宿主的設定政策，留在 Senate。
//           判準是那一句：功能碼不該知道設定檔的檔名與形狀（Coding_Standards.md §3）。
//           JSON 由 System.Text.Json 換成 SCP_Json —— 前者在 Unity 那側不存在（§2）。
//           ⭐ 2026-09-03（TASK-0105）lock 從資料根的 `_session/` 搬進 persona 的 `profile/`：
//           本檔原本「從信件夾往上找第一個 `_session`」那支推導退場 —— 那是 lock 位置的第五種算法，
//           而信件夾根一漂它就指到另一棵樹、印出一份合理但屬於別的專案的在線名單。
//
// ⚠ 三態不可壓成兩態（本檔最重要的一條）：
//   「沒有人在線」與「我量不到」必須看得出差別。lock 檔在但讀不了的時候，
//   把那個人印成「離線」是**捏造讀數** —— 那個畫面跟真的離線一模一樣。
//   ⇒ SCP_PersonaOnline.Unknown 存在的唯一理由就是這個。
//
// 📌 「誰是 persona」用資料判、不用名字猜：**letters 底下有 `profile/` 子目錄的那些**。
//   實測基準（2026-08-29，D:/Unity/Bar）：letters 底下 35 個目錄，其中 21 個有 `profile/`，
//   而那 21 個跟 `AwakenInit/_persona_profile_snapshot.json` 的 `pool` 陣列**逐字相同**。
//   用名字猜（跳過底線開頭、跳過 Template…）會在下一個命名慣例出現時安靜地漏人。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    /// <summary>persona 的上線狀態。⚠ 三態 —— <see cref="Unknown"/> 不是「大概離線」。</summary>
    public enum SCP_PersonaOnline
    {
        /// <summary>量不到（lock 檔在但讀不了／壞了）。**不可以顯示成離線。**</summary>
        Unknown = 0,

        /// <summary>有 session lock（`profile/_session.json` 存在且讀得出來）。</summary>
        Online = 1,

        /// <summary>persona 目錄讀得到，而 `profile/_session.json` 不存在。</summary>
        Offline = 2,
    }

    /// <summary>一個 persona 的讀數。欄位空字串 ＝ lock 裡沒有那一格（不是「值是空的」）。</summary>
    public sealed class SCP_PersonaStatus
    {
        public string Name { get; set; } = "";
        public SCP_PersonaOnline Online { get; set; } = SCP_PersonaOnline.Unknown;
        public string LettersDir { get; set; } = "";
        public string Agent { get; set; } = "";
        public string ActualAgent { get; set; } = "";
        public string Model { get; set; } = "";
        public string BankAccount { get; set; } = "";
        public string LockedAt { get; set; } = "";
        public string SessionKey { get; set; } = "";

        /// <summary>
        /// lock 上那顆 session token（酒館 enforce 開著時發文要附）。
        /// <para>⚠ 跟 <see cref="SessionKey"/> **不是同一格** —— key 是這場 session 的識別，
        /// token 是發文的憑證。名字像，用途不同，混用不會報錯只會被 reject。</para>
        /// </summary>
        public string SessionToken { get; set; } = "";
        public int Pid { get; set; }
        public int WakeExpected { get; set; }
        public string LockPath { get; set; } = "";

        /// <summary>lock 檔在、但讀不了的原因。⚠ 這種情況是 <see cref="SCP_PersonaOnline.Unknown"/> 不是 Offline。</summary>
        public string? LockError { get; set; }
    }

    /// <summary>一次掃描的全部結果 —— 含**掃不到的原因**（那跟「掃到零個」不同形）。</summary>
    public sealed class SCP_PersonaScan
    {
        public string LettersRoot { get; set; } = "";

        public List<SCP_PersonaStatus> Personas { get; } = new List<SCP_PersonaStatus>();

        /// <summary>量不到的原因（空清單 ＝ 沒問題）。**有問題時畫面一定要印出來。**</summary>
        public List<string> Problems { get; } = new List<string>();

        /// <summary>信件夾**有沒有被列出來過**。false ＝ 連列目錄都失敗（跟「列出來是空的」不同形）。</summary>
        public bool Enumerated { get; set; }

        public int OnlineCount { get { return Count(SCP_PersonaOnline.Online); } }
        public int OfflineCount { get { return Count(SCP_PersonaOnline.Offline); } }
        public int UnknownCount { get { return Count(SCP_PersonaOnline.Unknown); } }

        int Count(SCP_PersonaOnline iState)
        {
            int n = 0;
            foreach (var p in Personas) if (p.Online == iState) n++;
            return n;
        }
    }

    public static class SCP_PersonaLetters
    {
        // ── 掃描 ──────────────────────────────────────────────────

        /// <summary>
        /// 掃一次：信件夾底下有 `profile/` 的目錄 ＝ persona，再看各自的 `profile/_session.json` 判上線。
        /// <para>⚠ lock 檔在但讀不了 ⇒ 那個人是 <see cref="SCP_PersonaOnline.Unknown"/>（<c>LockError</c> 有值），
        /// **不會退化成「離線」**；信件夾根本身列不出來 ⇒ 整份 <see cref="SCP_PersonaScan.Problems"/> 出聲。</para>
        /// </summary>
        public static SCP_PersonaScan Scan(string? iLettersRoot)
        {
            var aScan = new SCP_PersonaScan();
            string aRoot = CleanPath(iLettersRoot ?? "");
            aScan.LettersRoot = aRoot;

            if (aRoot.Length == 0)
            {
                aScan.Problems.Add("還沒設定 persona 信件夾根目錄。");
                return aScan;
            }
            if (!Directory.Exists(aRoot))
            {
                aScan.Problems.Add($"信件夾根目錄不存在：{aRoot}");
                return aScan;
            }
            var aLettersRoot = new SCP_LettersRoot(aRoot);

            string[] aDirs;
            try { aDirs = Directory.GetDirectories(aRoot); }
            catch (Exception e)
            {
                aScan.Problems.Add($"列不出信件夾底下的目錄：{e.GetType().Name}: {e.Message}");
                return aScan;
            }
            aScan.Enumerated = true;

            Array.Sort(aDirs, (x, y) => string.Compare(Path.GetFileName(x), Path.GetFileName(y),
                                                       StringComparison.OrdinalIgnoreCase));

            foreach (string aPersonaDir in aDirs)
            {
                string aName = Path.GetFileName(aPersonaDir);
                // persona 的判準是資料（有 profile/），不是名字 —— 見檔頭 📌
                if (!Directory.Exists(Path.Combine(aPersonaDir, SCP_LettersPaths.ProfileDirName))) continue;

                var aStatus = new SCP_PersonaStatus { Name = aName, LettersDir = CleanPath(aPersonaDir) };
                string aLockPath = SCP_LettersPaths.SessionLockPath(aLettersRoot, aName);
                if (File.Exists(aLockPath))
                {
                    aStatus.LockPath = CleanPath(aLockPath);
                    ReadLock(aLockPath, aStatus);                            // 讀不了 ⇒ Unknown，不是 Offline
                }
                else
                {
                    aStatus.Online = SCP_PersonaOnline.Offline;
                }
                aScan.Personas.Add(aStatus);
            }
            return aScan;
        }

        /// <summary>
        /// 讀**單一** persona 的 lock（給熱路徑用：<see cref="Scan"/> 會列整個信件夾，
        /// 而 <c>GetRaw</c> 一次只問一個人）。
        /// <para>回 null ＝ 沒有 lock 檔（＝離線）；
        /// 回的物件 <c>Online != Online</c> ＝ 檔在但讀不了（<c>LockError</c> 有值）。
        /// **兩者不同形** —— 「沒上線」與「量不到」不可以壓成同一個答案。</para>
        /// <para>⚠ 解析走同一支 <see cref="ReadLock"/>：兩支解析器對同一顆 lock 給出不同答案時，
        /// 不會有任何一層報錯。</para>
        /// </summary>
        public static SCP_PersonaStatus? ReadPersonaLock(string iLettersRoot, string iPersona)
        {
            if (string.IsNullOrWhiteSpace(iPersona)) return null;
            string aRoot = CleanPath(iLettersRoot ?? "");
            if (aRoot.Length == 0) return null;

            string aPath = SCP_LettersPaths.SessionLockPath(new SCP_LettersRoot(aRoot), iPersona);
            if (!File.Exists(aPath)) return null;

            var aStatus = new SCP_PersonaStatus { Name = iPersona, LockPath = CleanPath(aPath) };
            ReadLock(aPath, aStatus);
            return aStatus;
        }

        // 區塊職責：讀一顆 lock 檔。
        // 物理意義：解析失敗 ⇒ Unknown ＋ LockError（**不是 Offline**：檔明明在）。
        // 數值影響：走 SCP_Json 的**帶預設值** getter —— 這裡是刻意用寬鬆版：
        //          lock 是別人（awakening 端）寫的檔，欄位隨版本增減是常態，
        //          少一格不該讓整顆 lock 判成壞掉。⇒ 缺欄位 ⇒ 空字串／0，
        //          而「整顆讀不了」才是 LockError。兩者不同形。
        static void ReadLock(string iPath, SCP_PersonaStatus oStatus)
        {
            try
            {
                SCP_JsonData aRoot = SCP_JsonParser.Parse(File.ReadAllText(iPath, Encoding.UTF8));
                oStatus.Online = SCP_PersonaOnline.Online;
                oStatus.Agent = aRoot.GetString("agent", "");
                oStatus.ActualAgent = aRoot.GetString("actual_agent", "");
                oStatus.Model = aRoot.GetString("model", "");
                oStatus.BankAccount = aRoot.GetString("bank_account", "");
                oStatus.LockedAt = aRoot.GetString("locked_at", "");
                oStatus.SessionKey = aRoot.GetString("session_key", "");
                oStatus.SessionToken = aRoot.GetString("session_token", "");
                oStatus.Pid = aRoot.GetInt("pid", 0);
                oStatus.WakeExpected = aRoot.GetInt("wake_expected", 0);
            }
            catch (Exception e)
            {
                oStatus.Online = SCP_PersonaOnline.Unknown;
                oStatus.LockError = $"{e.GetType().Name}: {e.Message}";
            }
        }

        // ── 手動登出（TASK-0294）──────────────────────────────────────

        // 區塊職責：**手動登出** —— 忘記登出或流程故障時，由人刪掉那顆 lock（Tim 2026-09-25）。
        // 物理意義：在線判準只看 lock 檔在不在（本檔 Scan、Editor 端 UCL_ActivePersonaLocks 都是）
        //          ⇒ 刪 lock 就足以解除「同一 persona 不得同時登入兩次」的卡死；**不需要 Unity Editor**。
        //          對照 Unity 端 UCL_LoginStatusPage 的 Force Remove（該頁之後廢棄，全面改用 Senate 端）。
        // ⚠ 刪之前**重讀一次 lock**：畫面上看到的是上一次掃描，而那之後可能有人登出又登入了 ——
        //   預期的 session 身分（<see cref="SessionIdentity"/>）對不上就拒絕，⛔ 不刪到別人剛開的新一場。
        //   ⛔ 只比 session_key 不夠：它是 `{actual_agent}-{persona}` 的常數（UCL_AwakeningService 登入端），
        //   同一個 agent 登出再登入 key 完全相同（TASK-0294 QA @kotoko）⇒ 身分要帶每次登入重生的 locked_at。
        //   iExpectedIdentity 為 null ＝ 確認時 lock 是壞的（Unknown），那就要求它**現在仍然**是壞的。
        // 數值影響：只刪 `profile/_session.json` 與 `cmd/now_status.json`。⛔ **不動** profile 的 status 欄
        //          （它是快取，不當在線判準）、不讓 token 失效、不發酒館廣播、不寫信 —— 回傳訊息逐條講明。
        /// <summary>
        /// 一場 session 的身分 ＝ `session_key@locked_at`。⚠ session_key 單獨不是每場唯一（見 <see cref="ForceLogout"/>），
        /// locked_at 只在登入時寫、每次登入重生。
        /// </summary>
        public static string SessionIdentity(SCP_PersonaStatus iStatus) => $"{iStatus.SessionKey}@{iStatus.LockedAt}";

        public static (bool Done, string Message) ForceLogout(string iLettersRoot, string iPersona, string? iExpectedIdentity)
        {
            string aRoot = CleanPath(iLettersRoot ?? "");
            if (aRoot.Length == 0 || string.IsNullOrWhiteSpace(iPersona))
                return (false, "✗ 沒有信件庫根或 persona —— 要登出誰不能用猜的");

            SCP_PersonaStatus? aNow = ReadPersonaLock(aRoot, iPersona);
            if (aNow == null)
                return (false, $"・{iPersona} 的 lock 已經不在（可能已經登出）—— 沒有東西可刪");
            if (iExpectedIdentity != null)
            {
                if (aNow.Online != SCP_PersonaOnline.Online || !string.Equals(SessionIdentity(aNow), iExpectedIdentity, StringComparison.Ordinal))
                    return (false, $"✗ 拒絕：{iPersona} 的 lock 在確認之後變了（確認時 `{iExpectedIdentity}`，"
                                 + $"現在 {(aNow.Online == SCP_PersonaOnline.Online ? "`" + SessionIdentity(aNow) + "`" : "讀不了")}）"
                                 + " —— 可能有人重新登入了。請重新掃描後再確認一次");
            }
            else if (aNow.Online != SCP_PersonaOnline.Unknown)
            {
                return (false, $"✗ 拒絕：確認時 {iPersona} 的 lock 讀不了，現在讀得出來了（session_key=`{aNow.SessionKey}`）"
                             + " —— 狀態變了，請重新掃描後再確認一次");
            }

            try { File.Delete(aNow.LockPath); }
            catch (Exception e) { return (false, $"✗ 刪 lock 失敗：{e.GetType().Name}: {e.Message}（{aNow.LockPath}）"); }
            if (File.Exists(aNow.LockPath))
                return (false, $"✗ 刪完 lock 還在（{aNow.LockPath}）—— 可能被別的程式佔住，稍後再試");

            string aStatusPath = SCP_LettersPaths.NowStatusPath(new SCP_LettersRoot(aRoot), iPersona);
            string aStatusNote = "";
            try { if (File.Exists(aStatusPath)) File.Delete(aStatusPath); }
            catch (Exception e) { aStatusNote = $"（now_status 沒刪掉：{e.Message} —— 不影響登出；下一場登入時會再清一次，讀取端也比對 session_key＋locked_at）"; }

            return (true, $"✓ {iPersona} 已手動登出：lock 已刪（{aNow.LockPath}）{aStatusNote}\n"
                        + "  ⚠ 沒做的事：profile 的 status 欄沒改（快取，不當在線判準）／token 沒失效／沒發酒館廣播／沒寫信");
        }

        /// <summary>去掉包住整串的引號與尾斜線（檔案總管「複製路徑」帶雙引號 —— 專案關聯頁同一課）。</summary>
        public static string CleanPath(string? iRaw)
        {
            string s = (iRaw ?? "").Trim();
            if (s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"') s = s.Substring(1, s.Length - 2).Trim();
            return s.Replace('\\', '/').TrimEnd('/');
        }
    }
}
