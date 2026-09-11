// 區塊職責：`Coding` kind 的**跨端資料形狀** —— 兩個宿主寫的是同一份鍵。
// 物理意義：Unity 那側先有 `UCL_CodingSession`（TASK-0058 A1）；A2 要在 Senate 那側**也能進場**，
//          而兩邊各宣告一份欄位就是**兩份會漂的 schema** —— 漂掉的症狀不是解析失敗（那會喊），
//          是**下一個讀那些欄位的人拿到預設值**（`SCP_ActivitySession.Raw` 那條血證的同一族）。
//          ⇒ 形狀搬進共用層，Unity 那側改成用它。
//          ✅ 2026-09-05 已完成（@summit）：`UCL_CodingSession` **整個刪掉**，
//             `Cmd_Coding` 直接用本類別 ⇒ 「兩邊逐鍵相同」不再是一份靠人維持的契約，
//             而是**編譯期的事實**。⛔ 沒有選第二選項（「或至少逐鍵對齊」）——
//             那條要求「加欄位時記得兩邊都加」，而漏了不會報錯，只會讓一端拿到預設值。
// 數值影響：純資料類別，零 IO。欄位名**就是 JSON 的鍵名** —— 改名＝舊檔讀回預設值。
//
// ⚠ `Coding` 是第一個**全域獨佔**的 kind（同時至多一人），也是第一個**沒有天然時長**的 kind。
//   後者的坑寫在 `Docs~/Session_Kinds.md` §5.5：沒有 `end_ts` 的場永遠是「進行中」，
//   **永遠不會落進補收工那條路** ⇒ 持有者掉線就永遠擋住所有人。
//   ⇒ 本 kind 一律帶 `end_ts`（PM basecamp 2026-09-05 拍 (A)），續期走 `op=status`。
#nullable enable
namespace SCP.Core.Session
{
    /// <summary>
    /// 一場 `Coding` session（改 C# 的施工場）。
    /// <para>⚠ **兩個宿主都用這一個類別**（Unity 側 <c>Cmd_Coding</c> ／ Senate 側 <c>SCP_Cmd_Coding</c>）——
    /// 欄位名就是 JSON 的鍵名，而它們現在只有一份。改欄位＝兩端同時改，編譯器會擋。</para>
    /// <para>⚠ 而 <c>end_ts</c> / <c>until_local</c> 由**兩個入口各自寫入**，格式必須逐字一致
    /// （<c>yyyy-MM-ddTHH:mm:ss.fffZ</c> ／ <c>yyyy-MM-dd HH:mm</c>）—— 那一格編譯器擋不到。</para>
    /// </summary>
    public class SCP_CodingSession : SCP_ActivitySession
    {
        /// <summary>正在改哪一部分（一句話）。**進場必填** —— 別人被擋下時要看得到你在做什麼。</summary>
        public string status = "";

        /// <summary>`status` 上次更新的 UTC ISO。⚠ 它同時是「這個人還在不在」的唯一時間訊號。</summary>
        public string status_updated = "";

        /// <summary>顯式 force 退場的理由（沒 force 時是空字串）。</summary>
        public string force_reason = "";

        // 區塊職責：這一場綁了哪幾張單（逗號分隔的單號，例 `129,193`）。
        // 物理意義：綁定是**多對多** —— 一場可以綁多張單，一張單也可能跨好幾場。
        //           它存在的目的只有一個：讓「所有綁定單都離開施工狀態」變成一個**可判的條件**，
        //           好讓場自動收掉。⛔ 它不是權限，也不是所有權宣告。
        // 數值影響：空字串 ＝ 沒綁 ⇒ **永遠不會自動收**（只能手動 `op=end`）。那是刻意的：
        //           沒宣告過綁定的場，沒有任何客觀條件可以判它做完了沒。
        public string tasks = "";

        // 區塊職責：這一場的**施工範圍**（絕對路徑，取施工的最大範圍）。TASK-0201。
        // 物理意義：全域互斥從「這個 kind 有人在跑就擋」收窄成「範圍撞到才擋」——
        //           判定在 `SCP_ActivitySessionStore.FindConflictingGlobal`，重疊規則在 `SCP_SessionScope`。
        //           ⚠ 宣告要取**最大範圍**：宣告得比實際改的窄，擋不住真正會撞的人，
        //           而失效樣子是「兩個人都進場了，然後改到同一支檔」——**這道閘不會叫**。
        // 數值影響：空字串 ＝ **沒宣告 ⇒ 退化成舊行為（整個 kind 全域獨佔）**。
        //           ⛔ 那是安全側，不是待辦：舊 session 檔沒有這一格，
        //           而「少一個欄位」不可以讓一道既有的閘變成誰都擋不住。
        // ⚠ 拍板（Tim 2026-09-11）：**純路徑判準** —— 同一個 repo 的兩份工作副本
        //   （`Senate/SCP_Core` 與 `LY/Assets/Plugins/SCP_Core`）**視為不衝突**。
        //   代價已知且被選擇：兩人各改一份副本的同一支檔時這道閘不叫，要到 push 分叉才現形。
        public string scope = "";

        // 區塊職責：收場當下工作區還剩幾個未提交的 `Assets/**/*.cs`。
        // 物理意義：**這是資訊，不是閘**（Tim 2026-09-10 拍板）——
        //           擋下會讓場握得更久，而**縮短持有**正是自動收場存在的理由。
        //           但下一個進場的人要看得到「上一場在樹上留了什麼」。
        // 數值影響：只寫進 session 檔並印出來，⛔ 一格都不影響收不收。
        public string left_dirty_cs = "";
    }
}
