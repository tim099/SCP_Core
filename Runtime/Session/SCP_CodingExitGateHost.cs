// 區塊職責：`Coding` 退場時那道**編譯閘**由宿主提供 —— 本層只知道「去問那一端」。
// 物理意義：⭐ **兩個宿主的尺不同形，而且不可以合成一把**：
//          Unity 側＝`check_compile`（tracker ＋ ErrorLog 對帳）；
//          Senate 側＝`dotnet build`／`build.sh` 出廠驗收。
//          硬湊一把兩邊共用的尺，會讓其中一邊量的**不是它自己的編譯**（TASK-0058 A/B/C 拍板附註）。
//          ⇒ 同 `SCP_ActivitySessionGatewayHost` 的形狀：介面在共用層，實作在宿主。
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

    /// <summary>宿主注入編譯閘的地方（同 <see cref="SCP_ActivitySessionGatewayHost"/> 的形狀）。</summary>
    public static class SCP_CodingExitGateHost
    {
        /// <summary>
        /// 宿主的閘。<c>null</c> ＝ **這個宿主沒有登記** ⇒ 退場路徑要明說「未驗編譯」，
        /// ⛔ 不可以印成綠燈。
        /// </summary>
        public static Func<SCP_CodingExitVerdict>? Gate;

        /// <summary>跑一次閘。沒登記時回 <c>null</c>（**跟「跑了但紅」不同形**）。</summary>
        public static SCP_CodingExitVerdict? Run()
        {
            Func<SCP_CodingExitVerdict>? aGate = Gate;
            if (aGate == null) return null;
            try { return aGate(); }
            catch (Exception e)
            {
                // 閘自己炸掉**不是綠燈**，也不是「沒登記」—— 它是第三種狀態，照實回紅並帶原因。
                return new SCP_CodingExitVerdict(false, "閘自己炸了：" + e.GetType().Name + ": " + e.Message,
                                                 "（閘未完成，這不是編譯結果）");
            }
        }
    }
}
