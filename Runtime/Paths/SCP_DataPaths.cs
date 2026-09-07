// 區塊職責：AgentCommands **資料根**底下的版面 —— queue 分道、酒館、信件夾、session token 表。
// 物理意義：這些目錄名是**跨端契約**（C# Editor / python run_cmd.py / Senate 三邊都走），
//           所以它們只能有一個拼字的地方。2026-08-30 掃到的現況：`"queues"` 在
//           `AgentCmdClient.QueueFolder` 與 `Program.cs` 的 status 分支**各拼一次** ——
//           改一個漏一個的症狀是 `senate cmd status` 掃一個空目錄印「沒有東西卡住」，
//           而那跟**真的**沒卡住一模一樣。
// 數值影響：純字串組裝，零 IO。根由呼叫端傳入（型別是 SCP_DataRoot，傳錯根編不過）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;

namespace SCP.Core.Paths
{
    public static class SCP_DataPaths
    {
        // ── 目錄／檔名常數（跨端契約，改了要對三邊）──────────────────

        public const string QueuesDirName = "queues";
        public const string QueueFileName = "queue.json";
        public const string TriggerFileName = "pending.trigger";
        public const string SessionDirName = "_session";
        public const string ChatTavernDirName = "ChatTavern";
        public const string BatonDirName = "baton";
        public const string LettersDirName = "letters";

        /// <summary>
        /// Cmd 回傳判定檔的目錄名（<c>&lt;某個根&gt;/_cmd_results/&lt;cmd_id&gt;.json</c>）。
        /// <para>⚠ **它掛在兩個不同的根底下，而那是刻意的**：派遣端寫在**資料根**
        /// （<c>AgentCmdClient</c>，Editor Runner 讀它）、Server 執行器寫在**Server 根**
        /// （<c>ServerExecutor</c>）。⇒ 共用的是**目錄名**，不是完整路徑
        /// —— 所以這裡是一個 <c>const</c>，⛔ 不是 <c>SCP_PathRegistry</c> 的衍生條目
        /// （那條從 <c>AgentCommandsRoot</c> 長出來，拿它去接 Server 根會**靜默換掉**解析結果）。</para>
        /// <para>🩸 TASK-0103 ①（@summit 2026-09-05 指認、09-07 複驗仍 fail）：這個字面
        /// 原本在條文點名的那兩端**各自拼一次**。而它的失效樣子跟本檔開頭記的 <c>"queues"</c>
        /// 那次同族 —— 改一個漏一個 ⇒ 一端寫進 A、另一端去 B 撈，撈不到就變成
        /// 「**沒有回傳檔**」，而那跟「**Cmd 還沒跑完**」在畫面上一模一樣。</para>
        /// </summary>
        public const string CmdResultsDirName = "_cmd_results";

        /// <summary>
        /// 沒有 persona 時的 queue 分道名。
        /// <para>🩸 它不是「預設值」是**症狀**：全員掉進這一道會互相阻塞
        /// （summit 2026-08-16 兩次 ensure_idle 逾時、kiara 2026-08-17 卡 120s）。
        /// 看到它出現在路徑裡 ＝ 有人沒帶 <c>--persona</c>。</para>
        /// </summary>
        public const string AnonymousQueueId = "anonymous";

        // ── 版面 ──────────────────────────────────────────────────

        public static string Queues(SCP_DataRoot iRoot)
            => iRoot.Value + "/" + QueuesDirName;

        /// <summary>
        /// 某個 persona 的 queue 分道。
        /// <para>⚠ 內建**路徑穿越防護**：persona 常常直接來自 CLI 參數，
        /// 不擋的話 <c>..</c> 是一條寫出 <c>queues/</c> 之外的路。
        /// 擋下時退回 <see cref="AnonymousQueueId"/> —— 那一道本來就代表「這筆身分不明」。</para>
        /// </summary>
        /// <summary>
        /// 某個 persona 的 queue **資料夾**。⚠ 帶子分道時（<c>&lt;persona&gt;/&lt;lane&gt;</c>）
        /// 回的仍是那個人的資料夾 —— 子分道住在檔名裡，不是另一個資料夾（見 <see cref="SplitQueueId"/>）。
        /// </summary>
        public static string QueueFolder(SCP_DataRoot iRoot, string? iPersona)
            => Queues(iRoot) + "/" + SplitQueueId(iPersona).Folder;

        public static string QueueFile(SCP_DataRoot iRoot, string? iPersona)
        {
            (string aFolder, string aLane) = SplitQueueId(iPersona);
            return Queues(iRoot) + "/" + aFolder + "/"
                   + (aLane.Length == 0 ? QueueFileName : "queue-" + aLane + ".json");
        }

        public static string TriggerFile(SCP_DataRoot iRoot, string? iPersona)
        {
            (string aFolder, string aLane) = SplitQueueId(iPersona);
            return Queues(iRoot) + "/" + aFolder + "/"
                   + (aLane.Length == 0 ? TriggerFileName : "pending-" + aLane + ".trigger");
        }

        /// <summary>
        /// session token 表（<c>_tokens.json</c> / <c>_token_enforce.json</c>）住的地方。
        /// <para>⚠ persona lock **不在這裡**（TASK-0105，2026-09-03 起住 <c>letters/&lt;p&gt;/profile/_session.json</c>，
        /// 見 <see cref="SCP_LettersPaths.SessionLockPath"/>）。在這個目錄底下找 <c>_persona_*.json</c>
        /// 只會找到搬遷時因衝突留下的殘檔，不是在線名單。</para>
        /// </summary>
        public static string SessionDir(SCP_DataRoot iRoot)
            => iRoot.Value + "/" + SessionDirName;

        public static string ChatTavern(SCP_DataRoot iRoot)
            => iRoot.Value + "/" + ChatTavernDirName;

        public static string Baton(SCP_DataRoot iRoot)
            => ChatTavern(iRoot) + "/" + BatonDirName;

        /// <summary>
        /// 慣例上的信件夾根（<c>&lt;資料根&gt;/ChatTavern/baton/letters</c>）。
        /// <para>⚠ 它是**慣例值**不是唯一解 —— 設定可以把信件夾指到別處，
        /// 所以 <see cref="SCP_LettersRoot"/> 是獨立型別，不從這裡自動轉換。</para>
        /// </summary>
        public static SCP_LettersRoot Letters(SCP_DataRoot iRoot)
            => new SCP_LettersRoot(Baton(iRoot) + "/" + LettersDirName);

        // ── 現地定語 ──────────────────────────────────────────────

        /// <summary>
        /// 定語欄位「沒有人給我這個值」時填的字面。
        /// <para>⛔ 它**不是**「沒有區域／沒有專案」，是**未宣告** —— 讀取端不准腦補成本區。
        /// （2026-09-02 之前的信整段沒有這兩欄，那也是未宣告；⚠ 兩者要能分辨：
        /// 少欄＝那個寫入端不知道有這回事，寫 <c>unstated</c>＝它知道而沒人給值。）</para>
        /// </summary>
        public const string UnstatedQualifier = "unstated";

        /// <summary>
        /// 從資料根算出**專案名**（＝資料根的上一層目錄名）。**純路徑運算，不碰磁碟、不猜。**
        /// <para>沒給資料根就回 <see cref="UnstatedQualifier"/> —— 印一個猜的專案名比留空更難查。</para>
        /// <para>⚠ 它是「現地定語」的一半：另一半 <c>region</c>（貨幣 ID）的真相源是宿主的央行設定，
        /// **本層不長讀它的嘴**，由宿主傳進來。兩欄都要的理由：宿主的 <c>CurrencyId</c> 缺值時
        /// 回預設而不是空 ⇒ 兩個沒設定過的專案會印出同一個 region，而 project 在那種情況下
        /// 仍然分岔 —— **一個恆同的欄位不帶資訊**。</para>
        /// <para>📌 放在這裡而不是各寫一份：磁碟上本來就有兩份（brief 一份、Editor 收尾信一份），
        /// 而分岔的症狀是**信少一欄、沒有任何一層會喊**（TASK-0134 QA 2026-09-05 抓到的那格）。</para>
        /// </summary>
        public static string ProjectNameOf(string? iDataRoot)
        {
            if (string.IsNullOrWhiteSpace(iDataRoot)) return UnstatedQualifier;
            try
            {
                string aNorm = iDataRoot!.Replace('\\', '/').TrimEnd('/');
                string? aParent = System.IO.Path.GetDirectoryName(aNorm);
                string aName = string.IsNullOrEmpty(aParent) ? "" : System.IO.Path.GetFileName(aParent!);
                return string.IsNullOrWhiteSpace(aName) ? UnstatedQualifier : aName;
            }
            catch { return UnstatedQualifier; }
        }

        // ── 判準 ──────────────────────────────────────────────────

        /// <summary>
        /// 把 persona 正規化成一個安全的 queue 分道名。
        /// <para>空白／含 <c>..</c>／含分隔符 ⇒ 退回 <see cref="AnonymousQueueId"/>。</para>
        /// </summary>
        public static string SafeQueueId(string? iPersona)
        {
            string a = (iPersona ?? "").Trim();
            if (a.Length == 0) return AnonymousQueueId;
            if (a.Contains("..") || a.IndexOf('/') >= 0 || a.IndexOf('\\') >= 0) return AnonymousQueueId;
            return a;
        }

        /// <summary>
        /// 拆 queue id：<c>&lt;persona&gt;</c> 或 <c>&lt;persona&gt;/&lt;lane&gt;</c>
        /// → (資料夾, 子分道)。沒有子分道時 <c>Lane</c> 是空字串。
        /// <para>物理意義：**身分是資料夾、子分道是檔名後綴** ——
        /// <c>queues/&lt;persona&gt;/queue-&lt;lane&gt;.json</c> ＋ <c>pending-&lt;lane&gt;.trigger</c>。
        /// 與 python <c>run_cmd.py</c> 的 <c>queue_path()</c> / <c>trigger_path()</c> **逐字同形**
        /// —— Editor 端的 watcher 掃的是 <c>queue*.json</c>，形狀差一個字就等於那筆永遠不被取走。</para>
        /// <para>🩸 為什麼子分道不做成資料夾（2026-08-01 chess 的血證，寫在 <c>chess.py</c> 的註解裡）：
        /// 舊寫法 <c>--agent-id chess-&lt;局號&gt;</c> 長出 <c>queues/chess-1/</c> <c>queues/chess-2/</c>
        /// —— **棋局不是人**，那是身分層污染。身分要回到真正下棋的那個人身上。</para>
        /// <para>⚠ 兩段各自過 <see cref="SafeQueueId"/> 的同一道防護；任一段不合法就**整筆**退回
        /// anonymous —— 只退一半會寫出一個看起來合理、而沒有人打算指向的位置。</para>
        /// </summary>
        public static (string Folder, string Lane) SplitQueueId(string? iQueueId)
        {
            string a = (iQueueId ?? "").Trim();
            if (a.Length == 0) return (AnonymousQueueId, "");
            int aSlash = a.IndexOf('/');
            if (aSlash < 0) return (SafeQueueId(a), "");

            string aLane = a.Substring(aSlash + 1);
            // 多一個斜線 ＝ 呼叫端在講一個本層不認得的形狀 ⇒ 不猜，整筆退回 anonymous。
            if (aLane.IndexOf('/') >= 0) return (AnonymousQueueId, "");
            string aSafeFolder = SafeQueueId(a.Substring(0, aSlash));
            string aSafeLane = SafeQueueId(aLane);
            if (aSafeFolder == AnonymousQueueId || aSafeLane == AnonymousQueueId) return (AnonymousQueueId, "");
            return (aSafeFolder, aSafeLane);
        }
    }
}
