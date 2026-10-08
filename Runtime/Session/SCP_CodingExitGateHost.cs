// 區塊職責：`Coding` 退場時那道**編譯閘**由宿主提供 —— 本層只知道「去問那一端」。
// 物理意義：介面在共用層，實作在宿主（Senate 是 `dotnet build`；`build.sh` 出廠驗收另外跑）。
// 數值影響：本檔零 IO。閘的實作可能**跑一次編譯**（秒級）—— 那是它的重點，不是副作用。
//
// ⚠ **沒有登記閘 ≠ 編譯是綠的**：那兩件事必須不同形。沒登記時退場路徑要印
//   「本宿主沒有登記退出閘 ⇒ **未驗編譯**」——「沒有量」與「量過了是綠的」印同一句話，
//   就是這個專案反覆咬人的那個形狀。
//
// 🩸 而 Senate 側有一格物理限制要寫下來：**`build.sh` 不能從 `senate.exe` 裡面跑** ——
//   它會停 Server、殺掉開著的 senate 視窗、然後覆寫 `publish/senate.exe`，
//   而那正是**當下正在執行的那個檔**（Access denied）。
//   ⇒ Senate 側的閘只能是**編譯**這一格（`dotnet build`），
//   **出廠驗收（`build.sh`）是人要另外跑的那一格** —— 閘要把這句話印出來，不要讓人以為它驗過了。
#nullable enable
using System;

namespace SCP.Core.Session
{
    /// <summary>編譯閘的判定：綠燈與否 ＋ **它到底量了什麼**（射程要跟著結論走）。</summary>
    public readonly struct SCP_CodingExitVerdict
    {
        public SCP_CodingExitVerdict(bool iGreen, string iSummary, string iScope)
        {
            Green = iGreen;
            Summary = iSummary ?? "";
            Scope = iScope ?? "";
        }

        /// <summary>編譯是不是綠的。</summary>
        public bool Green { get; }

        /// <summary>讀數摘要（錯誤數／耗時／指令）—— ⛔ 不要只回 true/false。</summary>
        public string Summary { get; }

        /// <summary>**這一格量到的射程**（例：「`dotnet build`；⛔ 不含 `build.sh` 出廠驗收」）。</summary>
        public string Scope { get; }
    }

    /// <summary>
    /// 閘要量哪一場：資料根（＝哪個專案）、施工範圍、開場時刻。
    /// <para>宿主靠它判斷讀數夠不夠新（晚於開場才算本場的）。</para>
    /// </summary>
    public sealed class SCP_CodingExitRequest
    {
        public SCP_CodingExitRequest(string iDataRoot, string iScope, string iStartTs)
        {
            DataRoot = iDataRoot ?? "";
            Scope = iScope ?? "";
            StartTs = iStartTs ?? "";
        }

        /// <summary>這一場的資料根（`<專案>/AgentCommands`）。</summary>
        public string DataRoot { get; }

        /// <summary>施工範圍（`|` 分段；空字串 ＝ 未宣告 ＝ 全域）。</summary>
        public string Scope { get; }

        /// <summary>開場時刻（ISO-8601）。</summary>
        public string StartTs { get; }
    }

    /// <summary>宿主注入編譯閘的地方（同 <see cref="SCP.Core.Canvas.SCP_CanvasGatewayHost"/> 的形狀）。</summary>
    public static class SCP_CodingExitGateHost
    {
        /// <summary>
        /// 宿主的閘。<c>null</c> ＝ **這個宿主沒有登記** ⇒ 退場路徑要明說「未驗編譯」，
        /// ⛔ 不可以印成綠燈。
        /// </summary>
        public static Func<SCP_CodingExitRequest, SCP_CodingExitVerdict>? Gate;

        /// <summary>跑一次閘。沒登記時回 <c>null</c>（**跟「跑了但紅」不同形**）。</summary>
        public static SCP_CodingExitVerdict? Run(SCP_CodingExitRequest iRequest)
        {
            Func<SCP_CodingExitRequest, SCP_CodingExitVerdict>? aGate = Gate;
            if (aGate == null) return null;
            try { return aGate(iRequest); }
            catch (Exception e)
            {
                // 閘自己炸掉**不是綠燈**，也不是「沒登記」—— 它是第三種狀態，照實回紅並帶原因。
                return new SCP_CodingExitVerdict(false, "閘自己炸了：" + e.GetType().Name + ": " + e.Message,
                                                 "（閘未完成，這不是編譯結果）");
            }
        }
    }
}
