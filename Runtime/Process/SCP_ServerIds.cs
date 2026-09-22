// 區塊職責：常駐 Server 的**身分**（serverId）—— 兩顆以上並存時，誰是誰。
// 物理意義：TASK-0244（D1）。在這之前 Server 這一層通篇假設只有一顆：單例鎖一把、queue 根一個、
//           心跳一份、停機請求一份、registry tag 一個。⇒ 不帶身分就起第二顆的失效順序是
//           ① 拿不到單例鎖被擋（這格會叫）② 若繞過鎖：兩顆掃**同一個** `queues/` ⇒
//           **互相接手對方的 lane**，而它會**成功**（兩顆載同一份實作）⇒ 沒有任何一層會叫。
// 數值影響：只決定檔名與 tag 怎麼拼，不碰任何協議內容。
//
// 📌 為什麼住共用層（TASK-0267，Tim 2026-09-22 拍板）：`ServerAutoStart` 搬進 SCP_Core，
//    而它每一條路都要先 `Normalize(iServerId)` ⇒ 這支是它的必要依賴。
//    ⭐ 而這支**本來就該在這裡**：它是純字串規則（零 Server 本體、零 process、零路徑推導），
//    兩個宿主底下語意完全相同 —— ⛔ 不像 `Probe`／`BuildId` 那幾格會在 Unity 端變成一個
//    沒有受詞的欄位。⇒ 「能不能進共用層」的判準是**這個概念在兩邊是不是同一件事**，
//    不是「誰需要它」。
//
// ⚠ 為什麼 `main` 不帶後綴（唯一的不對稱，而它刻意只活在 <see cref="SCP_ServerIds.Suffix"/> 這一個方法裡）：
//   全部一律加後綴的話，**升級當下正在跑的那顆舊 Server 會從新版 binary 的視野裡消失**
//   （它登記的 tag 是舊的 `senate_server`、心跳在舊檔名）⇒ 新版會判「沒有人在跑」而起第二顆，
//   **那正好是本檔要防的那件事**。⇒ 所以預設那顆的路徑逐字沿用舊值：零遷移、零孤兒 queue、
//   升級中途的那顆仍然看得見。⛔ 不對稱的代價是真的，但它比升級當下開一個競態窗便宜。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Text.RegularExpressions;

namespace SCP.Core.Proc
{
    public static class SCP_ServerIds
    {
        /// <summary>預設那顆（銀行／通用委派都在它身上）。⚠ 它的路徑與 tag **逐字沿用本功能之前的舊值**。</summary>
        public const string Default = "main";

        /// <summary>酒館那顆（TASK-0106／0239 用）。這裡先給名字，⛔ 不代表它已經存在。</summary>
        public const string Tavern = "tavern";

        // 會變成檔名的一部分 ⇒ 限制比「看起來夠用」嚴一格：只收小寫英數與 - _，開頭必須是英數。
        static readonly Regex s_Valid = new Regex(@"^[a-z0-9][a-z0-9_-]{0,31}$", RegexOptions.Compiled);

        /// <summary>
        /// 檢查並正規化。不合法**丟例外**而不是靜默改掉 —— 它會變成檔名，
        /// 而「我打的 id」與「實際用的 id」不一樣的那種錯，症狀是**兩顆 Server 各自服務一棵樹**。
        /// </summary>
        public static string Normalize(string? iId)
        {
            string aId = (iId ?? "").Trim().ToLowerInvariant();
            if (aId.Length == 0) return Default;
            if (!s_Valid.IsMatch(aId))
                throw new ArgumentException(
                    $"serverId '{iId}' 不合法 —— 只收小寫英數與 - _、開頭是英數、最長 32 字"
                    + "（它會變成檔名，⛔ 本層不替你改寫成一個你沒打的值）。");
            return aId;
        }

        /// <summary>
        /// **檔名**的後綴。預設那顆回空字串 —— 那格不對稱的理由見檔頭。
        /// </summary>
        public static string Suffix(string iId)
            => string.Equals(Normalize(iId), Default, StringComparison.Ordinal) ? "" : "." + Normalize(iId);

        /// <summary>
        /// **registry tag** 的後綴。⚠ 跟 <see cref="Suffix"/> 用不同的分隔符（`-` 而不是 `.`），
        /// 而這不是筆誤：
        /// <para>🩸 2026-09-20 實跑抓到：`SCP_ProcessRegistry.SanitizeTag` 只留 <c>[A-Za-z0-9-_]</c>，
        /// **點號會被改寫成底線**。於是登記時存的是 `senate_server_tavern`，
        /// 而查詢時拿 `senate_server.tavern` 去比 ⇒ **永遠對不上** ⇒
        /// 那顆 Server 明明活著、單例鎖也握著，`server list` 卻報 `not_running`。
        /// ⇒ 「它不在」與「我認不出它」同形，而這正是本功能要防的那一族。</para>
        /// <para>⛔ 不去改 <c>SanitizeTag</c>：那是 registry 的地板（tag 會變成檔名），
        /// 要配合的是**呼叫端別用會被改寫的字元**。</para>
        /// </summary>
        public static string TagSuffix(string iId)
            => string.Equals(Normalize(iId), Default, StringComparison.Ordinal) ? "" : "-" + Normalize(iId);

        /// <summary>queue 根的目錄名（`runtime/server` ／ `runtime/server-tavern`）。</summary>
        public static string RootDirName(string iId)
            => string.Equals(Normalize(iId), Default, StringComparison.Ordinal) ? "server" : "server-" + Normalize(iId);
    }
}
