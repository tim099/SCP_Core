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
    }
}
