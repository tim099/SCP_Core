// 區塊職責：**哪些扣款會先吃酒館券**，以及一筆混合付款怎麼拆。
// 物理意義：酒館券（券 id `tavern`）＝**個人錢包**，面額與 token 1:1，
//           主動消費時**自動先扣券、不足的部分再扣 token**
//           （Tim 2026-09-18：10 token 的消費可以是 3 券 ＋ 7 token）。
// 數值影響：本層**一毛錢都不動** —— 只回答「這筆算不算主動消費」與「怎麼拆」。
//           真正的扣款由呼叫端按這個計畫去做。
//
// 🩸 判準：
//   ① **白名單，⛔ 不是黑名單**（fail-closed）。名單外的 kind 一律**純 token**。
//      反過來（預設吃券、例外列出來）的失效樣子是：新加一種系統費用忘了排除
//      ⇒ 它安靜地把別人的錢包吃掉，而帳面上每一筆都合法。
//      ⇒ 兩種錯的代價不對稱：漏列一個消費 kind ＝「券沒被用到」（看得見、可補）；
//        漏排一個系統費用 ＝「券被吃掉了」（看不見、補不回來）。
//   ② **保管費／罰款／系統費用不吃券**（Tim 2026-09-18 拍板）——
//      券要在「我決定要花錢」的時候被花掉，⛔ 不是在睡覺的時候慢慢消失。
//   ③ **先檢查兩邊夠不夠，不夠就整筆不做**（Tim 拍板）——
//      ⛔ 不部分扣款。半扣的帳沒有人能對，而它不會出現在任何待處理清單上。
//
// ⚠ 這份名單是**手寫的**，而「漏列一個消費 kind」不會叫 ——
//   症狀只是「券怎麼都花不掉」。新增消費管道時要回來加一行。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Bank
{
    public static class SCP_SpendPolicy
    {
        /// <summary>酒館券的券 id。⚠ ⛔ 刻意**不叫** `token` —— 它跟銀行帳戶餘額是兩本帳，
        /// 名字長得一樣的話，「錢包剩多少」與「戶頭剩多少」會在畫面上同形。</summary>
        public const string TavernVoucherId = "tavern";

        // ⚠ 這幾個字串是**協議**（事件檔與帳本裡就是這麼寫的），⛔ 不是可以順手改的常數。
        //   名單來自 2026-09-18 對現有扣款 kind 的實測，⛔ 不是設計時想出來的。
        static readonly HashSet<string> ActiveSpendKinds = new HashSet<string>(StringComparer.Ordinal)
        {
            "canvas_pixel",        // 畫布放點
            "sculpture_place",     // 雕刻放點
            "book_donation",       // 贊助一本書
            "book_tip",            // 打賞
        };

        /// <summary>
        /// 這筆扣款算不算**主動消費**（＝會先吃酒館券）。
        /// <para>⛔ 名單外一律 <c>false</c> ＝ 純 token。見判準①：兩種錯的代價不對稱。</para>
        /// </summary>
        public static bool IsActiveSpend(string? iKind)
            => !string.IsNullOrWhiteSpace(iKind) && ActiveSpendKinds.Contains(iKind!.Trim());

        /// <summary>目前名單（給 `--list` 之類的地方印出來 —— 手寫名單要看得見才有人會維護它）。</summary>
        public static IReadOnlyCollection<string> Kinds => ActiveSpendKinds;

        /// <summary>一筆混合付款的拆法。<c>Voucher + Token == Amount</c>。</summary>
        public readonly struct Plan
        {
            public readonly int Voucher;
            public readonly int Token;
            public Plan(int iVoucher, int iToken) { Voucher = iVoucher; Token = iToken; }
            public int Amount => Voucher + Token;
        }

        // ===========================================================
        // 區塊職責：算出「先扣券、再扣 token」的拆法，並在**動手之前**判定夠不夠。
        // 🩸 判準③：回 false ＝ **整筆不做**。⛔ 呼叫端不准拿 oPlan 去做「能扣多少算多少」——
        //   所以失敗時 oPlan 是 default（0/0），讓那個部分扣款在物理上拿不到數字。
        // ⚠ 射程：本層只看「這一刻的兩個餘額」。檢查完到扣款之間餘額仍可能變 ——
        //   擋住那一格的是**單一寫入端**，⛔ 不是這個函式。
        // ===========================================================
        public static bool TryPlan(int iAmount, int iVoucherBalance, long iTokenBalance, bool iIsActiveSpend,
                                   out Plan oPlan, out string oWhy)
        {
            oPlan = default;
            oWhy = "";
            if (iAmount <= 0) { oWhy = $"金額要是正整數（收到 {iAmount}）"; return false; }
            if (iVoucherBalance < 0 || iTokenBalance < 0)
            {
                // ⛔ 負數是「查不到」不是「沒有」 —— 前者不准被當成後者去做一個扣款決定。
                oWhy = "查不到餘額（券 " + iVoucherBalance + "／token " + iTokenBalance + "）"
                       + " —— 這是「我不知道」不是「沒有」，本次不扣款";
                return false;
            }

            int aFromVoucher = iIsActiveSpend ? Math.Min(iVoucherBalance, iAmount) : 0;
            long aNeedToken = iAmount - aFromVoucher;
            if (aNeedToken > iTokenBalance)
            {
                oWhy = iIsActiveSpend
                    ? $"合計不足：需 {iAmount}，酒館券 {iVoucherBalance} ＋ token {iTokenBalance}"
                      + $" ＝ {iVoucherBalance + iTokenBalance} ⇒ **整筆不做**"
                    : $"token 不足：需 {iAmount}，餘額 {iTokenBalance}"
                      + "（這個 kind 不吃酒館券 —— 系統費用走純 token）⇒ **整筆不做**";
                return false;
            }

            oPlan = new Plan(aFromVoucher, (int)aNeedToken);
            return true;
        }
    }
}
