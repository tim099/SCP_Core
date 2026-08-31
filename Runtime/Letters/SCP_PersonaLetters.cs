// 區塊職責：**persona 信件庫的掃描與上線狀態讀取** —— 那底下有哪些 persona、誰現在在線。
// 物理意義：persona 資料住在 `<資料根>/ChatTavern/baton/letters/<persona>/`；
//           而「在線」的真相源**不在信件庫裡**，是 `<資料根>/_session/_persona_<name>.json`
//           這顆 session lock（登入時寫、登出時刪）。⇒ 本檔只讀檔，不寫 lock、不動 registry。
//
//           ⭐ 2026-08-30 從 Senate.Core/PersonaLetters.cs 搬進來（六步的第 2 步）。
//           搬的是**掃描那一半**；「信件夾根設在哪」屬於宿主的設定政策，留在 Senate。
//           判準是那一句：功能碼不該知道設定檔的檔名與形狀（Coding_Standards.md §3）。
//           JSON 由 System.Text.Json 換成 SCP_Json —— 前者在 Unity 那側不存在（§2）。
//
// ⚠ 三態不可壓成兩態（本檔最重要的一條）：
//   「沒有人在線」與「我量不到」必須看得出差別。`_session` 找不到的時候，
//   把每個人都印成「離線」是**捏造讀數** —— 那個畫面跟真的全體離線一模一樣。
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
        /// <summary>量不到（`_session` 目錄找不到／讀不了／lock 檔壞了）。**不可以顯示成離線。**</summary>
        Unknown = 0,

        /// <summary>有 session lock。</summary>
        Online = 1,

        /// <summary>`_session` 讀得到，而這個人沒有 lock。</summary>
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
        public string SessionDir { get; set; } = "";

        /// <summary>SessionDir 是推導出來的（不是設定裡指名的）。畫面要說出來，不然人不知道它為什麼看那裡。</summary>
        public bool SessionDirDerived { get; set; }

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
        /// <summary>`sessionDir` 填這個值 ＝ 由 <see cref="ResolveSessionDir"/> 從信件夾往上推導。</summary>
        public const string AutoSessionDir = "auto";

        // ── 路徑推導 ──────────────────────────────────────────────

        /// <summary>
        /// 找 `_session` 目錄。設定裡指名了就**逐字採用**（存不存在由 <see cref="Scan"/> 出聲）；
        /// 填 <see cref="AutoSessionDir"/> 或留空 → 從信件夾**逐層往上找第一個含 `_session` 的祖先**。
        /// <para>⚠ 刻意不寫死「往上三層」：`letters → baton → ChatTavern → AgentCommands` 是這個
        /// 專案今天的形狀，不是契約。寫死層數的錯法是**安靜的** —— 指到一個不存在的目錄，
        /// 然後所有人顯示離線。</para>
        /// </summary>
        public static (string Dir, bool Derived) ResolveSessionDir(string iLettersRoot, string? iConfigured)
        {
            string aCfg = CleanPath(iConfigured ?? "");
            if (aCfg.Length > 0 && !string.Equals(aCfg, AutoSessionDir, StringComparison.OrdinalIgnoreCase))
                return (aCfg, false);

            string aRoot = CleanPath(iLettersRoot);
            if (aRoot.Length == 0) return ("", true);

            DirectoryInfo aDir;
            try { aDir = new DirectoryInfo(aRoot); }
            catch { return ("", true); }

            // 從信件夾自己開始往上（含自己）—— 找得到就停，找不到回空字串讓 Scan 講話。
            for (DirectoryInfo? d = aDir; d != null; d = d.Parent)
            {
                string aCandidate = Path.Combine(d.FullName, SCP_DataPaths.SessionDirName);
                if (Directory.Exists(aCandidate)) return (CleanPath(aCandidate), true);
            }
            return ("", true);
        }

        // ── 掃描 ──────────────────────────────────────────────────

        /// <summary>
        /// 掃一次：信件夾底下有 `profile/` 的目錄 ＝ persona，再對照 `_session` 的 lock 判上線。
        /// <para>⚠ 找不到 `_session` 時所有人是 <see cref="SCP_PersonaOnline.Unknown"/> 並在
        /// <see cref="SCP_PersonaScan.Problems"/> 留一句 —— **不會退化成「全部離線」**。</para>
        /// </summary>
        public static SCP_PersonaScan Scan(string? iLettersRoot, string? iConfiguredSessionDir)
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

            (string aSessionDir, bool aDerived) = ResolveSessionDir(aRoot, iConfiguredSessionDir);
            aScan.SessionDir = aSessionDir;
            aScan.SessionDirDerived = aDerived;

            // lock 表：persona（不分大小寫）→ lock 檔路徑。null ＝ 量不到（跟「空表」不同！）
            Dictionary<string, string>? aLocks = null;
            if (aSessionDir.Length == 0)
            {
                aScan.Problems.Add(
                    $"從 {aRoot} 往上找不到 `_session` 目錄 ⇒ 上線狀態**量不到**（顯示為「未知」，不是離線）。");
            }
            else if (!Directory.Exists(aSessionDir))
            {
                aScan.Problems.Add(
                    $"`_session` 目錄不存在：{aSessionDir} ⇒ 上線狀態**量不到**（顯示為「未知」，不是離線）。");
            }
            else
            {
                try
                {
                    var aFound = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    string aPrefix = SCP_LettersPaths.LockPrefix;
                    foreach (string aFile in Directory.GetFiles(aSessionDir, aPrefix + "*.json"))
                    {
                        string aName = Path.GetFileNameWithoutExtension(aFile);
                        if (aName.Length <= aPrefix.Length) continue;
                        aFound[aName.Substring(aPrefix.Length)] = aFile;
                    }
                    aLocks = aFound;
                }
                catch (Exception e)
                {
                    aScan.Problems.Add($"讀 `_session` 失敗 ⇒ 上線狀態量不到：{e.GetType().Name}: {e.Message}");
                }
            }

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
                if (aLocks == null)
                {
                    aStatus.Online = SCP_PersonaOnline.Unknown;             // 量不到，不是離線
                }
                else if (aLocks.TryGetValue(aName, out string? aLockPath))
                {
                    aStatus.LockPath = CleanPath(aLockPath);
                    ReadLock(aLockPath, aStatus);
                }
                else
                {
                    aStatus.Online = SCP_PersonaOnline.Offline;
                }
                aScan.Personas.Add(aStatus);
            }

            // lock 有、而信件夾沒有那個人 ⇒ 說出來（那是兩份資料對不上，不是沒事）
            if (aLocks != null)
            {
                var aKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in aScan.Personas) aKnown.Add(p.Name);

                var aOrphans = new List<string>();
                foreach (string k in aLocks.Keys) if (!aKnown.Contains(k)) aOrphans.Add(k);
                aOrphans.Sort(StringComparer.OrdinalIgnoreCase);

                foreach (string aOrphan in aOrphans)
                    aScan.Problems.Add($"`_session` 有 {aOrphan} 的 lock，但信件夾裡沒有這個人（兩份資料對不上，不是沒事）。");
            }
            return aScan;
        }

        /// <summary>
        /// 讀**單一** persona 的 lock（給熱路徑用：<see cref="Scan"/> 會列整個信件夾，
        /// 而 <c>GetRaw</c> 一次只問一個人）。
        /// <para>回 null ＝ 沒有 lock 檔 or 量不到 <c>_session</c>；
        /// 回的物件 <c>Online != Online</c> ＝ 檔在但讀不了（<c>LockError</c> 有值）。
        /// **兩者不同形** —— 「沒上線」與「量不到」不可以壓成同一個答案。</para>
        /// <para>⚠ 解析走同一支 <see cref="ReadLock"/>：兩支解析器對同一顆 lock 給出不同答案時，
        /// 不會有任何一層報錯。</para>
        /// </summary>
        public static SCP_PersonaStatus? ReadPersonaLock(string iLettersRoot, string iPersona,
                                                         string? iConfiguredSessionDir = null)
        {
            if (string.IsNullOrWhiteSpace(iPersona)) return null;
            string aRoot = CleanPath(iLettersRoot ?? "");
            if (aRoot.Length == 0) return null;

            (string aSessionDir, bool _) = ResolveSessionDir(aRoot, iConfiguredSessionDir);
            if (aSessionDir.Length == 0 || !Directory.Exists(aSessionDir)) return null;

            string aPath = Path.Combine(aSessionDir, SCP_LettersPaths.LockFileName(iPersona));
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
                oStatus.Pid = aRoot.GetInt("pid", 0);
                oStatus.WakeExpected = aRoot.GetInt("wake_expected", 0);
            }
            catch (Exception e)
            {
                oStatus.Online = SCP_PersonaOnline.Unknown;
                oStatus.LockError = $"{e.GetType().Name}: {e.Message}";
            }
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
