// 區塊職責：**往酒館發一則訊息**這件事的那道閘 —— 本體只知道「去問宿主」。
// 物理意義：酒館發文的權威實作只有 Unity Editor 那側有：seq 配號、category 路由、
//          鏡像與 Discord 轉發、以及發文掛的那些 hook。⇒ 這一格**沒有本地版**，
//          而那不是「還沒移植」，是「同時只能有一個寫入端」（seq 是全域遞增的）。
//          ⇒ CLI／Server 的實作走 AgentCommand 檔案協議派給 Editor；
//             Editor 內的實作直呼 Cmd_Tavern（in-process，不繞檔案協議繞回自己）。
//          形狀同 `SCP_ICanvasGateway` / `SCP_IActivitySessionCloseGateway`。
// 數值影響：本檔零 IO。實作會做一次 Cmd round-trip（檔案協議＋Watcher 輪詢，1〜3 秒）。
//
// ⚠ **沒有登記閘 ≠ 發出去了**：兩件事必須不同形。沒登記時呼叫端要印
//   「本宿主沒有登記發文閘 ⇒ **這一則沒有發出去**」——
//   「沒有這個能力」與「發了」印同一句話，就是這個專案反覆咬人的那個形狀。
//
// 📌 而「發文失敗」對呼叫端**通常不是致命的**：小歇的核心是信落磁碟，廣播是附帶。
//   ⇒ 閘只回判定，**要不要因此失敗是呼叫端的事**（TASK-0133 的兩本帳分開結算）。
#nullable enable
using System.Collections.Generic;

namespace SCP.Core.Letters
{
    /// <summary>一次發文的判定：成不成、**為什麼**、以及發出去的那則是誰（seq）。</summary>
    public readonly struct SCP_TavernPostVerdict
    {
        public SCP_TavernPostVerdict(bool iPosted, string iDetail, string iSeq)
        {
            Posted = iPosted;
            Detail = iDetail ?? "";
            Seq = iSeq ?? "";
        }

        /// <summary>真的發出去了嗎。</summary>
        public bool Posted { get; }

        /// <summary>人讀的理由／讀數（要能直接貼進回報）。⛔ 不要只回 true/false。</summary>
        public string Detail { get; }

        /// <summary>發出去那則的 seq；**空字串＝沒有這個讀數**（跟 seq=0 不同形）。</summary>
        public string Seq { get; }

        public static SCP_TavernPostVerdict Good(string iDetail, string iSeq) => new SCP_TavernPostVerdict(true, iDetail, iSeq);
        public static SCP_TavernPostVerdict Bad(string iDetail) => new SCP_TavernPostVerdict(false, iDetail, "");
    }

    public interface SCP_ITavernPostGateway
    {
        /// <summary>
        /// 宿主定語 —— 「這一則是誰在哪裡發的」。
        /// <para>🩸 同 `SCP_ICanvasGateway.HostQualifier`：委派成功的輸出跟原生的長得一模一樣，
        /// 於是「我發了」與「Editor 替我發了」變成同一句話。</para>
        /// </summary>
        string HostQualifier { get; }

        /// <summary>
        /// 發一則。<paramref name="iSenderPersona"/> 決定署名（⛔ 不要另外傳顯示身分 ——
        /// 那是 UCL 端 BUG-23/24 的形狀：繞過推導不會報錯，只會署錯名字）。
        /// </summary>
        /// <param name="iMeta">tag／category 等；category 決定它會不會轉進 Discord。</param>
        /// <param name="iSessionToken">token enforce 開著時必帶；空＝不附。</param>
        /// <param name="oLines">過程行（宿主定語、回傳檔路徑…）—— 直接接到 Cmd 的輸出上。</param>
        SCP_TavernPostVerdict Post(string iSenderPersona, string iBody,
                                   IReadOnlyDictionary<string, string> iMeta,
                                   string iSessionToken, List<string> oLines);
    }

    /// <summary>宿主注入發文閘的地方（同 <see cref="SCP.Core.Session.SCP_ActivitySessionGatewayHost"/> 的形狀）。</summary>
    public static class SCP_TavernPostGatewayHost
    {
        /// <summary>
        /// 宿主的工廠（吃資料根）。<c>null</c> ＝ **這個宿主沒有登記**
        /// ⇒ 呼叫端要明說「沒發出去」，⛔ 不可以印成發過了。
        /// </summary>
        public static System.Func<string, SCP_ITavernPostGateway>? Factory;

        /// <summary>取一個閘。沒登記時回 <c>null</c>（**跟「發了但失敗」不同形**）。</summary>
        public static SCP_ITavernPostGateway? Create(string iDataRoot)
        {
            System.Func<string, SCP_ITavernPostGateway>? aFactory = Factory;
            return aFactory == null ? null : aFactory(iDataRoot ?? "");
        }
    }
}
