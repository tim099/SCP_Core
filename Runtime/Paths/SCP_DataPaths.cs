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
        public static string QueueFolder(SCP_DataRoot iRoot, string? iPersona)
            => Queues(iRoot) + "/" + SafeQueueId(iPersona);

        public static string QueueFile(SCP_DataRoot iRoot, string? iPersona)
            => QueueFolder(iRoot, iPersona) + "/" + QueueFileName;

        public static string TriggerFile(SCP_DataRoot iRoot, string? iPersona)
            => QueueFolder(iRoot, iPersona) + "/" + TriggerFileName;

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
    }
}
