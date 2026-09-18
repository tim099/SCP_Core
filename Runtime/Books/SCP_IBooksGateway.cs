// 區塊職責：書店那三個會動錢的動作（捐贈／發表／打賞）需要的**宿主能力**。
// 物理意義：本體（`SCP_BooksOps`）只認得檔案與規則；而**動錢、發券、寫 log、投遞續寫包**
//           在 Unity Editor 與 Senate Server 上是兩條完全不同的路
//           （Editor 要 spawn `senate.exe`，Server 直接 `SCP_CmdRegistry.Dispatch`）。
//           ⇒ 把那四格抽成介面，本體才搬得出 Unity。
// 數值影響：本介面**不定義任何規則** —— 「哪些 kind 先扣券」住在 `SCP_SpendPolicy`，
//           實作端只負責把請求送到它該去的地方。
//
// 🩸 判準：
//   ① **付款回 (券, token) 兩個數字，⛔ 不回總額。** 「3 券 ＋ 7 token」與「10 token」
//      只差一個總數 —— 付款方式看不出來的話，券被吃掉就不會有人知道
//      （2026-09-18 打賞單上實際發生過：`tokens_spent: 6` 而帳本只扣 2）。
//   ② **失敗一律 throw，⛔ 不回 false 讓呼叫端自己決定。** 錢沒動而流程往下走，
//      留下的是「書登記了、錢沒扣」，而那張登記之後沒有人會回來看。
//   ③ **`Warn` 不是選配。** 本體有幾處是**刻意的 fail-soft**（券發不出去記 pending、
//      續寫包投遞失敗只多一行）—— 那些地方如果連一行字都不留，
//      「沒發生」與「發生了但沒成功」就同形了。
#nullable enable
using SCP.Core.Json;

namespace SCP.Core.Books
{
    /// <summary>書店動錢那幾支需要的宿主能力（Editor／Server 各自實作）。</summary>
    public interface SCP_IBooksGateway
    {
        /// <summary>
        /// 付一筆錢：**主動消費自動先扣酒館券**，不足的才扣 token（規則在 `SCP_SpendPolicy`）。
        /// <para>回 (券付幾張, token 付幾個)。⚠ <paramref name="iWalletPersona"/> 為空 ⇒
        /// 定位不到錢包 ⇒ 實作端走純 token 並**留一行 warn**（判準③）。</para>
        /// <para>失敗 throw（判準②）。</para>
        /// </summary>
        (int Voucher, int Token) Pay(string iBank, string iWalletPersona, int iAmount, string iKind,
                                     string iRef, string iDescription, string iIdemKey);

        /// <summary>發券給某個 persona（券 id 由呼叫端給：`canvas` / `tavern`）。失敗 throw。</summary>
        void GrantVoucher(string iPersona, string iVoucherId, int iAmount, string iSource, string iRef);

        /// <summary>fail-soft 的地方留下的那一行字。⛔ 不可以是空實作（判準③）。</summary>
        void Warn(string iMessage);

        /// <summary>
        /// 投遞續寫包（發表時掛的那一步）。⚠ **非致命** —— 書已經登記了，
        /// 失敗只在回報裡多一行。
        /// </summary>
        /// <returns>成功回那一行字；失敗回 null 並把原因放進 <paramref name="oError"/>。</returns>
        string? DeliverDossier(string iBook, string iAuthorPersona, SCP_JsonData iEntry, out string oError);
    }
}
