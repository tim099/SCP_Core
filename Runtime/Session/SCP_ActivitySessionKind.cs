// 區塊職責：活動 session 的**種類登記表**。
// 物理意義：關場統一入口是 SCP_ActivitySessionStore.CloseVerified（TASK-0055「所有關場路徑走同一個門」）。
//           ⚠ 沒有任何 kind 需要結算 ⇒ 不登記 per-kind 的 close handler（TASK-0448）。
// 數值影響：純查表，零 IO。
//
// ⚠ 新增一種 kind 的門檻（2026-08-27 拍板，不放寬）：**先跑一場真的，再加進來。**
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

        /// <summary>改 C# 的施工場（TASK-0058）——**全域同時至多一人**，見 <see cref="IsGlobalExclusive"/>。</summary>
        public const string Coding = "Coding";

        /// <summary>
        /// 本層實際會去看的種類。
        /// <para>⚠ 「不在這張表裡」的語意是**沒被看過**，不是「不存在」——
        /// 回報時一律連同 <see cref="Kinds"/> 一起說，否則「沒查到」會被讀成「不在」。</para>
        /// </summary>
        public static readonly string[] Kinds = { FreeTime, StreamWatch, Coding };

        // ===========================================================
        // 區塊職責：標記哪些 kind 是**全域**互斥（同一時間整個資料根只准一個人持有）。
        // 物理意義：這是與「每人一場」**正交的第二條軸**（TASK-0058 驗收第一格明寫兩條都要過）——
        //          第一條軸問「這個人忙不忙」（SCP_ActivitySessionStore.FindRunning，per-persona）；
        //          這一條問「這件事現在有沒有別人在做」（FindRunningGlobal，掃全體）。
        //          兩條軸都過才准開場；缺任何一條，另一條看起來都完全正常。
        // 數值影響：純查表，零 IO。空表＝沒有任何 kind 全域互斥（本層不預設任何一種）。
        // ⚠ 加進這張表**不會**自己生效 —— 生效點在 SCP_ActivitySessionStore.TryStart，
        //   而 TryStart 目前只有走它的呼叫端才受保護（2026-09-05 量：生產端只有 Coding 走）。
        //   ⇒ 回報互斥狀態時要說「掃到的是走 TryStart 的那些」，不要說成「沒有人在做」。
        // ===========================================================
        /// <summary>全域互斥的 kind（同一資料根同時至多一人持有）。</summary>
        public static readonly string[] GlobalExclusiveKinds = { Coding };

        /// <summary>這個 kind 是不是全域互斥（空字串一律 false）。</summary>
        public static bool IsGlobalExclusive(string? iKind)
            => !string.IsNullOrEmpty(iKind) && Array.IndexOf(GlobalExclusiveKinds, iKind) >= 0;

        /// <summary>這個字串是不是一個已登記的 kind（空字串一律 false —— 舊檔沒有這個欄位）。</summary>
        public static bool IsRegistered(string? iKind)
            => !string.IsNullOrEmpty(iKind) && Array.IndexOf(Kinds, iKind) >= 0;
    }
}
