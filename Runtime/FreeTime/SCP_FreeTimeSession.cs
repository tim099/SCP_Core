// 區塊職責：一場自由時間 session 的資料形狀（`<data_root>/sessions/<persona>.json`，`kind` 欄 = FreeTime）。
// 物理意義：共通欄位（persona／session_id／start_ts／end_ts／
//          until_local／active／end_reason／ended_at）在 `SCP_ActivitySession`；這裡只加自由時間自己的三格。
//          ⚠ 欄位名**就是 JSON 的鍵名** —— Unity 那份子類別寫的是同一組鍵，兩個宿主讀寫同一個檔。
//          改名＝舊檔讀回預設值，而預設值長得跟「這一場沒有輪次」一模一樣。
// 數值影響：純資料，零 IO（IO 走 SCP_ActivitySessionStore；不認識的鍵由基底的 Raw 原樣保留）。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using SCP.Core.Session;

namespace SCP.Core.FreeTime
{
    /// <summary>自由時間 session（<c>sessions/&lt;persona&gt;.json</c>）。</summary>
    public class SCP_FreeTimeSession : SCP_ActivitySession
    {
        /// <summary>已擲過幾輪活動（step=next 換骰才加；roll=0 不加）。</summary>
        public int rounds = 0;

        // ⚠ 這兩格**只是紀錄，不是尺**（Tim 2026-09-04 拍板：自由時間不是強制活動）——
        //   一度有人拿 `rounds - activities_done >= 2` 掛「別再骰了」的警告，那個差在真實資料上響 3 次、被券帳打臉 3 次。
        //   要判「哪些活動很久沒做」走 SCP_FreeTimeStatsIO（飢餓度，另一套）。
        /// <summary>本場實際開始過幾件活動（唯一寫入端：free-time-activity op=pick）。</summary>
        public int activities_done = 0;

        /// <summary>最後一件開始的活動 id（空＝本場還沒開始任何活動）。</summary>
        public string activity = "";
    }
}
