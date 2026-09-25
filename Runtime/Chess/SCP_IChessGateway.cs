// 區塊職責：棋局本體與**宿主能力**之間的閘 —— 廣播酒館、發對局獎勵券兩件事。
// 物理意義：TASK-0268 ⑥ —— `chess.py` 的廣播是 `subprocess` 叫 `senate.exe ucmd run Tavern`；
//           搬進 C# 之後這兩件事在不同宿主上是不同的路：
//           · Senate CLI：酒館走 `AgentCmdClient` 派給 Editor 的 `Cmd_Tavern`（同 process，不 spawn）、
//             券走 `SCP_CmdRegistry.Dispatch("voucher")`
//           · Unity Editor：酒館走 registry 拿 `Cmd_Tavern` in-process、券走 `UCL_VoucherAuthority`
//           ⇒ 本體不該知道自己跑在哪個宿主上，它只認這個介面。形狀照抄 `SCP_ICanvasGateway` /
//           `SCP_IBooksGateway`（同一個問題已經有兩份答案，⛔ 不發明第三套裝配時機）。
// 數值影響：介面刻意窄到三個方法。
//   ⚠ 兩件事都是 **best-effort**（同 python）：棋步已落盤，廣播沒發出去或券沒發下來**不回滾棋步** ——
//     但**必須出聲**：回 false ＋ 理由，由本體印進回報。沒發出去的廣播與發出去的，在呼叫端本來完全同形。
#nullable enable
namespace SCP.Core.Chess
{
    public interface SCP_IChessGateway
    {
        /// <summary>宿主定語 —— 「廣播／發券是誰在哪裡跑的」（印進回報第一行）。</summary>
        string HostQualifier { get; }

        /// <summary>
        /// 發一則酒館（`room=tavern`，`meta` 由本體給）。
        /// <para><paramref name="iSenderPersona"/> 可為 null（系統代發）—— ⛔ 不假造一個叫 chess-system 的人。</para>
        /// <para><paramref name="iLane"/>＝每局一條子分道（`chess-&lt;index&gt;`）：同一個人同時有兩局時，兩筆廣播不互相排隊。
        /// ⛔ 它是**通道不是身分**。</para>
        /// </summary>
        /// <returns>true ＝ 宿主回報送出；false ＝ 沒送出，<paramref name="oDetail"/> 說為什麼。</returns>
        bool Broadcast(string? iSenderPersona, string iLane, string iBody, string iMetaJson, out string oDetail);

        /// <summary>
        /// 發永久畫布券（`voucher=canvas`）。
        /// </summary>
        /// <returns>發放後的可花餘額；-1 ＝ 發了但讀不回餘額；null ＝ 沒發成（<paramref name="oDetail"/> 說為什麼）。</returns>
        int? GrantCanvasVoucher(string iPersona, int iAmount, string iSource, string iRef, out string oDetail);
    }

    // ===========================================================
    // 區塊職責：宿主把自己的閘裝上來的那一格。
    // ⚠ 形狀**照抄 `SCP_CanvasGatewayHost`**（吃資料根，⛔ 不自己解析）。
    // ⛔ 沒裝工廠就回 null，呼叫端要**大聲說**「這個宿主沒有廣播／發券能力」——
    //   回一個空實作的話，「這個宿主不支援」會長得跟「送出了而什麼都沒發生」一模一樣。
    // ===========================================================
    public static class SCP_ChessGatewayHost
    {
        public static System.Func<string, SCP_IChessGateway?>? Factory;

        public static SCP_IChessGateway? For(string iDataRoot) => Factory?.Invoke(iDataRoot);
    }
}
