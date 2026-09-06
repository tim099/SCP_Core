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
    /// <summary>
    /// 一次發文的三種結局。
    /// <para>🩸 <see cref="Unresolved"/> 是 TASK-0134 QA（summit 2026-09-05）用一次真的小歇量出來的：
    /// 她拿到「沒發」的回報，而 <b>Editor 是開著的、廣播其實成功了</b>（<c>post_seq 19082</c>）——
    /// 那一格的真實語意是「<b>等待端沒拿到回執</b>」，不是「沒發」。</para>
    /// <para>⛔ 為什麼非拆不可：兩者的**處置相反** ——
    /// 真沒發要補發；沒等到卻去補發，就是在全域遞增的 seq 上多出**第二則**。
    /// 一個 bool 表達不了「不知道」，而讀的人往那個空格裡填的一定是其中一邊。</para>
    /// </summary>
    public enum SCP_TavernPostOutcome
    {
        /// <summary>發出去了，而且**拿得到 seq**（配過號＝寫入真的發生過的直接證據）。</summary>
        Posted = 0,

        /// <summary>確定沒發（沒有閘／提交前就炸／宿主回報失敗）⇒ **補發是安全的**。</summary>
        NotPosted = 1,

        /// <summary>不知道（等回執逾時）⇒ ⛔ **先回讀再決定**，補發可能發出第二則。</summary>
        Unresolved = 2,
    }

    /// <summary>一次發文的判定：成不成、**為什麼**、以及發出去的那則是誰（seq）。</summary>
    public readonly struct SCP_TavernPostVerdict
    {
        public SCP_TavernPostVerdict(SCP_TavernPostOutcome iOutcome, string iDetail, string iSeq,
                                     string iRecheckHint = "")
        {
            Outcome = iOutcome;
            Detail = iDetail ?? "";
            Seq = iSeq ?? "";
            RecheckHint = iRecheckHint ?? "";
        }

        /// <summary>三態結局。⛔ 判斷「要不要補發」一律看這個，不要看 <see cref="Posted"/>。</summary>
        public SCP_TavernPostOutcome Outcome { get; }

        /// <summary>
        /// 真的發出去了嗎。
        /// <para>⚠ <c>false</c> **不等於**「沒發」—— <see cref="SCP_TavernPostOutcome.Unresolved"/>
        /// 在這裡也是 <c>false</c>。這個屬性只回答「有沒有拿到成功回執」。</para>
        /// </summary>
        public bool Posted => Outcome == SCP_TavernPostOutcome.Posted;

        /// <summary>人讀的理由／讀數（要能直接貼進回報）。⛔ 不要只回 true/false。</summary>
        public string Detail { get; }

        /// <summary>發出去那則的 seq；**空字串＝沒有這個讀數**（跟 seq=0 不同形）。</summary>
        public string Seq { get; }

        /// <summary>
        /// 未定時要人去跑的**可複製指令**（回讀 result／酒館）。空＝這一態不需要。
        /// <para>📌 summit 那次靠的是輸出裡半句括號「那不代表它沒發」——
        /// 而她還得自己知道怎麼查。⇒ 這一欄是把那半句括號變成**一行可以貼的指令**。</para>
        /// </summary>
        public string RecheckHint { get; }

        public static SCP_TavernPostVerdict Good(string iDetail, string iSeq)
            => new SCP_TavernPostVerdict(SCP_TavernPostOutcome.Posted, iDetail, iSeq);

        /// <summary>**確定沒發**（補發安全）。⛔ 逾時不要用這個 —— 那是 <see cref="Unknown"/>。</summary>
        public static SCP_TavernPostVerdict Bad(string iDetail)
            => new SCP_TavernPostVerdict(SCP_TavernPostOutcome.NotPosted, iDetail, "");

        /// <summary>**不知道有沒有發**（等回執逾時）—— 呼叫端要印 <paramref name="iRecheckHint"/> 而不是補發指令。</summary>
        public static SCP_TavernPostVerdict Unknown(string iDetail, string iRecheckHint)
            => new SCP_TavernPostVerdict(SCP_TavernPostOutcome.Unresolved, iDetail, "", iRecheckHint);
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
