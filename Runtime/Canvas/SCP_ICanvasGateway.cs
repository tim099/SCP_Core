// 區塊職責：畫布本體與**宿主能力**之間的那道閘 —— 付款、自由時間資格、放點分享三件事。
// 物理意義：這三件事的權威實作只有 Unity Editor 那側有（券／token 的 canonical ledger、
//           UCL_SessionService、酒館 seq 分配）。本體不該知道自己跑在哪個宿主上，
//           所以它只認這個介面；誰來實作是宿主啟動時裝上的。
//           ⇒ CLI／Server 的實作走 AgentCommand 檔案協議派給 Editor（AgentCmdClient）；
//              Editor 內的實作直呼那些 ledger（in-process，不繞檔案協議繞回自己）。
// 數值影響：⚠ 資格查詢是**三態**不是 bool —— Yes／No／**Unknown**。
//           🩸 「不知道」與「不在」必須不同形（canvas.py 2026-08-26 起就是這個語意，不可退化）：
//           拿不知道冒充「不在自由時間」，使用者會照著去開一場他其實已經在的場；
//           而那個錯不會有任何一層喊，因為兩者都是「沒有免費像素」。
// 設計取捨：介面刻意窄到只有六個方法 —— 每多一個方法就是多一個「本體開始知道宿主細節」的入口。
//           ⛔ 這裡不出現任何 ledger 型別、不出現 queue 路徑、不出現 Unity 型別；
//           SCP_Core 是三宿主共用的碼，碰了其中任何一個就搬不進去別的宿主。
namespace SCP.Core.Canvas
{
    /// <summary>三態答案 —— <see cref="Unknown"/> 是「問不到」，不是「否」。</summary>
    public enum SCP_CanvasTriState
    {
        Yes,
        No,
        Unknown,
    }

    /// <summary>一次閘操作的結果：成不成，以及**為什麼**（人讀，要能貼進回報裡）。</summary>
    public readonly struct SCP_CanvasGateResult
    {
        public readonly bool Ok;
        public readonly string Detail;

        public SCP_CanvasGateResult(bool iOk, string iDetail)
        {
            Ok = iOk;
            Detail = iDetail ?? "";
        }

        public static SCP_CanvasGateResult Good(string iDetail) { return new SCP_CanvasGateResult(true, iDetail); }
        public static SCP_CanvasGateResult Bad(string iDetail) { return new SCP_CanvasGateResult(false, iDetail); }
    }

    public interface SCP_ICanvasGateway
    {
        /// <summary>
        /// 宿主定語 —— 「這一則是誰在哪裡跑出來的」。
        /// <para>🩸 存在的理由是《無定語的成功》(2026-08-30)：委派成功的輸出跟原生的長得一模一樣，
        /// 於是「我在 CLI 跑完了」與「Editor 替我跑完了」變成同一句話。</para>
        /// </summary>
        string HostQualifier { get; }

        /// <summary>此刻在不在自由時間（三態）。<paramref name="oDetail"/> 帶「這個值怎麼拿到的」。</summary>
        SCP_CanvasTriState QueryInFreeTime(string iPersona, out string oDetail);

        /// <summary>本場限時券（＝自由時間免費像素）餘量；-1 ＝ 問不到。</summary>
        int QueryExpiringVouchers(string iPersona, out string oDetail);

        /// <summary>永久券餘量；-1 ＝ 問不到。</summary>
        int QueryPermanentVouchers(string iPersona, out string oDetail);

        /// <summary>token 餘額；-1 ＝ 問不到（**不是 0** —— 0 是「查到了，沒錢」）。</summary>
        long QueryTokenBalance(string iAccountId, out string oDetail);

        /// <summary>扣券（限時優先由實作端決定，本體只說扣幾張）。</summary>
        SCP_CanvasGateResult ConsumeVouchers(string iPersona, int iCount, string iSourceRef, string iDescription);

        /// <summary>扣 token。</summary>
        SCP_CanvasGateResult DebitTokens(string iAccountId, int iAmount, string iSourceKind,
                                         string iSourceRef, string iDescription);

        /// <summary>放點後的分享（發不出去**不該讓放點失敗** —— 廣播是 best-effort）。</summary>
        SCP_CanvasGateResult Share(string iPersona, string iRoom, string iBody);
    }

    /// <summary>
    /// 宿主在啟動時把**工廠**裝上（與 <c>UnityDelegateCmd.ConfigProvider</c> 同形）。
    /// <para>⚠ 沒裝上時 <see cref="For"/> 回 null，而呼叫端**必須 fail loud** ——
    /// 不准 fallback 到一個「假裝付過了」的實作：那種假成功會讓像素落盤而錢沒扣，
    /// 而帳本對不上要到很久以後才有人發現。</para>
    /// <para>🩸 為什麼是工廠而不是一個現成實例：閘要用的資料根（queue 在哪）與畫布狀態的資料根
    /// **必須是同一棵樹**。若閘在啟動時自己解析一個根，而 Cmd 吃的是 <c>--arg data_root</c>，
    /// 兩者不一致時會安靜地把付款派到另一個專案 —— 錢從那邊扣、像素落在這邊。
    /// ⇒ 把根當參數傳進來，形狀上就不可能不一致（TASK-0112 那一族的預防形式）。</para>
    /// </summary>
    public static class SCP_CanvasGatewayHost
    {
        public static System.Func<string, SCP_ICanvasGateway?>? Factory;

        /// <summary>取這棵資料樹的閘；宿主沒裝工廠就回 null（呼叫端要大聲失敗）。</summary>
        public static SCP_ICanvasGateway? For(string iDataRoot)
        {
            return Factory?.Invoke(iDataRoot);
        }
    }
}
