// 區塊職責：活動 session 的**種類登記表** ＋ 每種的 **close handler**（關場統一入口的另一半）。
// 物理意義：關場過去散在各處各寫一次（各 Cmd 自己翻三欄、管理頁自己呼叫 Close）——
//           於是「補收工」與「正常收工」走的是兩條路，而其中一條**跳過結算**（TASK-0055 的 known-issue）。
//           ⇒ 這裡讓每個 kind 登記一個 handler，所有關場路徑走同一個門（SCP_ActivitySessionStore.CloseWithSettlement）。
// 數值影響：純登記表 ＋ 委派，零 IO。
//
// ⚠ 新增一種 kind 的門檻（沿用 UCL 那側 2026-08-27 的拍板，不放寬）：**先跑一場真的，再加進來。**
//   理由是欄位缺席時 typed model 只會拿到預設值，而 `active=false` 跟「沒這場」長得一樣
//   ⇒ 沒實跑就登記，等於多一格「看起來查過了」的假讀數。
//
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Session
{
    /// <summary>已登記的活動 session 種類。</summary>
    public static class SCP_ActivitySessionKind
    {
        /// <summary>自由時間。</summary>
        public const string FreeTime = "FreeTime";

        /// <summary>觀影。</summary>
        public const string StreamWatch = "StreamWatch";

        /// <summary>
        /// 本層實際會去看的種類。
        /// <para>⚠ 「不在這張表裡」的語意是**沒被看過**，不是「不存在」——
        /// 回報時一律連同 <see cref="Kinds"/> 一起說，否則「沒查到」會被讀成「不在」。</para>
        /// </summary>
        public static readonly string[] Kinds = { FreeTime, StreamWatch };

        /// <summary>這個字串是不是一個已登記的 kind（空字串一律 false —— 舊檔沒有這個欄位）。</summary>
        public static bool IsRegistered(string? iKind)
            => !string.IsNullOrEmpty(iKind) && Array.IndexOf(Kinds, iKind) >= 0;
    }

    /// <summary>關場的結果 —— 三本帳分開回報，不壓成一句「成功」。</summary>
    /// <remarks>
    /// 🩸 憲法⑥（三本帳分開結算）的落點：**權威狀態**（session 檔翻三欄）先落地，
    /// **結算**（金流／台帳）best-effort，**廣播**再 best-effort；
    /// 任何一步炸掉**不得冒充整場失敗** —— 那正是 0043/0044 那族（回報層炸掉冒充主動作失敗）。
    /// </remarks>
    public sealed class SCP_ActivitySessionCloseResult
    {
        /// <summary>權威狀態有沒有落地（session 檔的三欄有沒有翻成收工）。</summary>
        public bool Closed;

        /// <summary>有沒有跑結算。<c>false</c> 且 <see cref="SettleError"/> 為空 ＝ 這個 kind 沒有登記 handler（明確降級）。</summary>
        public bool Settled;

        /// <summary>結算失敗的原因（成功或未跑時為空）。⚠ 它**不會**讓 <see cref="Closed"/> 變 false。</summary>
        public string SettleError = "";

        /// <summary>結算那一步自己說了什麼（台帳行數、金額…）—— 給回傳檔逐行印出來。</summary>
        public List<string> SettleLines = new List<string>();

        /// <summary>這個 kind 有沒有登記 handler（false ＝ 走 base close，明確降級不靜默）。</summary>
        public bool HasHandler;
    }

    /// <summary>
    /// 一種 kind 的關場結算 handler —— 由**宿主**注入（Senate 側串 ucmd 委派回 Editor；Editor 側就地跑）。
    /// </summary>
    /// <remarks>
    /// ⚠ 這是介面不是實作，理由是**結算就是金流**：`SettleAsync` 內含 `UCL_TreasuryLedger.Credit`
    /// ⇒ 金流搬家是 TASK-0106（Tim 拍 B 不動）。⇒ 本層永遠不自己算錢，只知道「去問那一端」。
    /// </remarks>
    public interface SCP_IActivitySessionSettleGateway
    {
        /// <summary>這個 gateway 管哪一種 kind。</summary>
        string Kind { get; }

        /// <summary>
        /// 對一場**已經被翻成收工**的 session 跑結算。
        /// <para>⚠ 呼叫時序是固定的：**權威狀態先落地，才輪到這裡** ——
        /// 反過來的話，結算成功而狀態沒寫，下一次會再結算一次。</para>
        /// </summary>
        /// <returns>成功回 true；失敗回 false 並把原因寫進 <paramref name="oError"/>（⛔ 不丟例外）。</returns>
        bool TrySettle(SCP_ActivitySession iSession, string iReason, List<string> oLines, out string oError);
    }

    /// <summary>
    /// 宿主注入 handler 的地方（同 <c>SCP_CanvasGatewayHost</c> 的形狀 —— 那是 Tim 2026-09-03 拍過的樣板）。
    /// </summary>
    public static class SCP_ActivitySessionGatewayHost
    {
        static readonly Dictionary<string, SCP_IActivitySessionSettleGateway> s_Gates
            = new Dictionary<string, SCP_IActivitySessionSettleGateway>(StringComparer.Ordinal);

        /// <summary>登記一種 kind 的結算 gateway（同 kind 再登記 ＝ 覆蓋，最後一個贏）。</summary>
        public static void Register(SCP_IActivitySessionSettleGateway iGate)
        {
            if (iGate == null || string.IsNullOrEmpty(iGate.Kind)) return;
            lock (s_Gates) s_Gates[iGate.Kind] = iGate;
        }

        /// <summary>取某 kind 的 gateway；沒有回 null（＝這個 kind 走 base close，明確降級）。</summary>
        public static SCP_IActivitySessionSettleGateway? For(string? iKind)
        {
            if (string.IsNullOrEmpty(iKind)) return null;
            lock (s_Gates) return s_Gates.TryGetValue(iKind!, out var aGate) ? aGate : null;
        }

        /// <summary>目前登記了哪些 kind —— 回報時要附上（「沒結算」與「沒有 handler」是兩件事）。</summary>
        public static string[] RegisteredKinds()
        {
            lock (s_Gates)
            {
                var aKeys = new string[s_Gates.Count];
                s_Gates.Keys.CopyTo(aKeys, 0);
                return aKeys;
            }
        }

        /// <summary>清空（測試用；正式流程不呼叫）。</summary>
        public static void ClearForTest()
        {
            lock (s_Gates) s_Gates.Clear();
        }
    }
}
