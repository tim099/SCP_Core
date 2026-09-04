// 區塊職責：**活動 session** 的共通資料模型 —— 誰的、哪一種、什麼時候開、什麼時候該收、收了沒。
// 物理意義：這是 UCL 那側 `UCL_SessionBase`（2026-08-18 立、08-27 扁平化）的搬家版本。
//           磁碟格式**逐鍵相同** —— 既有的 `<DataRoot>/sessions/<persona>.json` 必須讀得回來，
//           否則「讀不到」與「這個人沒有 session」在輸出上同形（active=false 跟沒這場長得一樣）。
// 數值影響：純資料 ＋ 判斷，零 IO（IO 走 SCP_ActivitySessionStore）。
//
// ⚠⚠ **名字要跟登入鎖分開**：`letters/<p>/profile/_session.json`（TASK-0105 搬過來的 persona 登入鎖）
//    也叫 session，而它跟本檔**是兩件事**。本族一律叫 **Activity**Session；
//    看到 `_session.json` 那個字就是登入鎖，看到 `sessions/<persona>.json` 才是本族。
//    🩸 不分名的後果不是編譯錯，是三個月後有人拿登入鎖的讀數去回答活動 session 的問題，而兩邊都不報錯。
//
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Globalization;

namespace SCP.Core.Session
{
    /// <summary>
    /// 一場活動 session 的共通資料（<c>&lt;DataRoot&gt;/sessions/&lt;persona&gt;.json</c>）。
    /// <para>各 kind 的專屬欄位（自由時間的 rounds、觀影的 paid_minutes…）**不在這裡** ——
    /// 那些仍住在各自的宿主，本層只認共通的那幾格。</para>
    /// </summary>
    /// <remarks>
    /// ⚠ 欄位名**就是 JSON 的鍵名**，改名＝舊檔讀回預設值，而預設值長得跟「沒這場」一樣。
    /// 要動 schema 走單子，不要順手改。
    ///
    /// ⚠ **可被繼承**（TASK-0127 ⑦）：各 kind 的宿主用子類別補自己的欄位
    /// （自由時間的 `rounds`、觀影的 `paid_minutes`…），走 <c>SCP_ActivitySessionStore.Load&lt;T&gt;</c>。
    /// 序列化吃的是**執行期型別**的成員清單（<c>SCP_Reflect.SchemaOf</c>）⇒ 子類別欄位自動進出。
    /// 📌 那為什麼 <see cref="Raw"/> 還留著：**讀成子類別 ≠ 認識全部的鍵** ——
    /// 管理頁與關場路徑讀的是本基底類別，此時 kind 專屬欄位一個都不認識，
    /// 而它們必須原封不動寫回去（🩸 2026-09-04 的活體就是那條路吃掉了 `rounds`／`activity`）。
    /// </remarks>
    public class SCP_ActivitySession
    {
        /// <summary>這場屬於誰（＝檔名，冗餘存一份供人直讀 json 時對帳）。</summary>
        public string persona = "";

        /// <summary>
        /// 這場是哪一種（<see cref="SCP_ActivitySessionKind"/> 的值）。
        /// <para>路徑扁平化之後 kind **從路徑段變成資料欄位**：檔案位置只剩一人一檔位，
        /// 「同一個人同時兩種 session」在**資料形狀層**就不可能發生。</para>
        /// ⚠ 空字串一律視為不符 —— 舊檔沒有這個欄位，而舊檔不該被當成任何 kind 的現行 session。
        /// </summary>
        public string kind = "";

        /// <summary>場次 id。額度／統計類資料以此綁定場次。</summary>
        public string session_id = "";

        /// <summary>開場 UTC ISO。</summary>
        public string start_ts = "";

        /// <summary>預定收工 UTC ISO（<c>yyyy-MM-ddTHH:mm:ss.fffZ</c>）。</summary>
        public string end_ts = "";

        /// <summary>預定收工的本地時刻字串（給人讀的，不參與判定）。</summary>
        public string until_local = "";

        /// <summary>是否仍在進行。⚠ 只看它不夠 —— 超時沒回來收工的人會一直停在 true（用 <see cref="IsRunningAt"/>）。</summary>
        public bool active = false;

        /// <summary>收工原因（未收工時為空字串）。</summary>
        public string end_reason = "";

        /// <summary>實際收工 UTC ISO（未收工時為空字串）。</summary>
        public string ended_at = "";

        // ===========================================================
        // 區塊職責：**保留這一層不認識的鍵** —— 各 kind 的專屬欄位。
        // 物理意義：磁碟上的 session 檔不是只有共通欄位。實測（2026-09-04，LY 的 8 份真檔）：
        //          FreeTime 那幾份帶著 `rounds` / `activities_done` / `activity`，觀影那幾份帶更多。
        //          本層只認共通欄位 ⇒ 若寫回時只寫自己認識的那幾格，**那些欄位會安靜地消失**，
        //          而收工是「讀出來→翻三欄→寫回去」⇒ 每收一次工就吃掉一次別人的資料。
        // 數值影響：Load 時把原始 JSON 整份記下來，Save 時以它為底、只覆寫共通欄位。
        // 🩸 這一格是 JSON 規範「未知鍵 round-trip」那條的活體：
        //    不保留的後果不是解析失敗（那會喊），是**下一個讀那些欄位的人拿到預設值**。
        // ===========================================================
        /// <summary>載入時的原始 JSON（含本層不認識的 kind 專屬欄位）—— 寫回時以它為底。</summary>
        [SCP.Core.Reflect.SCP_Ignore]
        public Json.SCP_JsonData? Raw;

        // ===========================================================
        // 區塊職責：把「這場還算不算進行中」收成唯一一個判斷點。
        // 物理意義：`active` 只在有人真的跑收工步驟時才被翻成 false ——
        //          超時就消失的人會把 true 留在檔案裡。**光看 active 會把早就下線的人算成在線。**
        // 數值影響：純判斷不寫檔。end_ts 解析不出來時**回 true** —— 沒有截止欄位只能信 active；
        //          寧可誤判「還在」也不要把一場真的在跑的 session 當不存在（後者會讓人疊開第二場）。
        // ===========================================================
        /// <summary>此刻是否仍在進行（active 且未過 end_ts）。<paramref name="iNowLocal"/> 傳本地時間。</summary>
        public bool IsRunningAt(DateTime iNowLocal, out DateTime? oEndLocal)
        {
            oEndLocal = ParseIsoToLocal(end_ts);
            if (!active) return false;
            if (!oEndLocal.HasValue) return true;
            return iNowLocal <= oEndLocal.Value;
        }

        /// <summary>此刻是否仍在進行（不需要收工時刻時的簡寫）。</summary>
        public bool IsRunningNow() => IsRunningAt(DateTime.Now, out _);

        /// <summary>把 UTC ISO 字串轉本地時間；解析不出來回 null（壞欄位不該讓整個動作死掉）。</summary>
        public static DateTime? ParseIsoToLocal(string? iIso)
        {
            if (string.IsNullOrEmpty(iIso)) return null;
            return DateTime.TryParse(iIso, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime aDt)
                ? aDt.ToLocalTime() : (DateTime?)null;
        }

        /// <summary>收工時刻的寫法（與 UCL 那側 `UCL_AwakeningService.NowIso()` 同形）。</summary>
        public static string NowIso()
            => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    }
}
