// 區塊職責：「commit 訊息裡的 `Fixes/Refs TASK-n` → 推進那張單」這一步的**委派閘**。
// 物理意義：狀態機不在這裡 —— 有 blocker 不推進／有 QA 推 in_review／沒 QA 才 done，
//           那些判斷全在 Editor 的 `Cmd_Task op=commit`。本層只負責**把訊號送過去**。
//           複製一份狀態機到這邊就是兩份產線：兩邊都不報錯，而它們遲早各說各話。
// 數值影響：本檔只有介面與結局型別，零行為。沒有宿主登記 ⇒ 呼叫端會明說「沒有推進」。
//
// ⚠ 三態不是兩態（與 SCP_ITavernPostGateway 同一套理由）：
//   `Advanced`／`NotSent`（確定沒送出，補送安全）／`Unresolved`（沒等到回執，⛔ 先回讀）。
//   把 `Unresolved` 併進 `NotSent` 會讓人去補送一個**可能已經生效**的推進 ——
//   而推進是對別人的宣告（清單上少一筆＝所有人不再看它）。
using System.Collections.Generic;

namespace SCP.Core.Tasks
{
    /// <summary>推單的三態結局。⛔ 判斷「要不要手動補」一律看這個，不要看布林。</summary>
    public enum SCP_TaskAdvanceOutcome
    {
        /// <summary>Editor 收到並回報成功。</summary>
        Advanced = 0,

        /// <summary>**確定沒送出去**（送出前就被擋下）⇒ 手動補是安全的。</summary>
        NotSent = 1,

        /// <summary>送出了但**沒等到回執** ⇒ ⛔ 它可能已經推進了，先回讀再決定。</summary>
        Unresolved = 2,
    }

    public readonly struct SCP_TaskAdvanceVerdict
    {
        public SCP_TaskAdvanceVerdict(SCP_TaskAdvanceOutcome iOutcome, string iDetail, string iRecheckHint = "")
        {
            Outcome = iOutcome;
            Detail = iDetail ?? "";
            RecheckHint = iRecheckHint ?? "";
        }

        public SCP_TaskAdvanceOutcome Outcome { get; }

        /// <summary>人看的說明（成功時是讀數，失敗時是真因）。</summary>
        public string Detail { get; }

        /// <summary>`Unresolved` 時要給人的**可複製回讀指令**；其餘狀態是空字串。</summary>
        public string RecheckHint { get; }

        public static SCP_TaskAdvanceVerdict Good(string iDetail)
            => new SCP_TaskAdvanceVerdict(SCP_TaskAdvanceOutcome.Advanced, iDetail);

        public static SCP_TaskAdvanceVerdict Bad(string iDetail)
            => new SCP_TaskAdvanceVerdict(SCP_TaskAdvanceOutcome.NotSent, iDetail);

        public static SCP_TaskAdvanceVerdict Unknown(string iDetail, string iRecheckHint)
            => new SCP_TaskAdvanceVerdict(SCP_TaskAdvanceOutcome.Unresolved, iDetail, iRecheckHint);
    }

    public interface SCP_ITaskCommitGateway
    {
        /// <summary>印在輸出裡的宿主定語 —— 讓人知道這一步是誰跑的。</summary>
        string HostQualifier { get; }

        /// <summary>把一張單的 commit 訊號送出去。</summary>
        /// <param name="iMode"><c>fixes</c>（較強的宣告，會推狀態）或 <c>refs</c>（只掛 SHA）。</param>
        SCP_TaskAdvanceVerdict Advance(string iPersona, string iIndex, string iSha, string iMode,
                                       List<string> oLines);
    }

    /// <summary>宿主登記處。沒登記 ⇒ <see cref="Create"/> 回 null，而那**不等於推進成功**。</summary>
    public static class SCP_TaskCommitGatewayHost
    {
        public static System.Func<string, SCP_ITaskCommitGateway>? Factory;

        public static SCP_ITaskCommitGateway? Create(string iDataRoot)
        {
            System.Func<string, SCP_ITaskCommitGateway>? aFactory = Factory;
            return aFactory == null ? null : aFactory(iDataRoot);
        }
    }
}
