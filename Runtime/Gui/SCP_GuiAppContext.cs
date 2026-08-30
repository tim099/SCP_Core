// 區塊職責：**頁面看得到的宿主** —— 一頁需要宿主提供什麼，全部列在這裡，而且只有這裡。
// 物理意義：搬頁面搬不動的真正原因不是頁面本身，是它的 ctor 吃著宿主的 model
//           （Senate 的 8 頁全部吃 `SenateModel`）。⇒ 頁面改吃本介面之後，
//           「這一頁需要宿主給什麼」變成一份**寫得出來的清單**，而不是「它碰得到 model 的全部」。
//           📌 判準：一個東西要不要進本介面，問「**沒有它這一頁畫不出來嗎**」。
//           宿主自己的讀數（Senate 的專案清單／環境探測）不進來 —— 那些頁本來就該留在宿主那側。
// 數值影響：純介面，零 IO。實作由宿主提供（Senate 是 `SenateModel`）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System.Collections.Generic;
using System.Reflection;
using SCP.Core.Prefs;

namespace SCP.Core.Gui
{
    /// <summary>
    /// 頁面與宿主之間的契約。**功能碼只認得它** —— 不認得設定檔的檔名、路徑與結構。
    /// <para>目前只有兩格，而兩格都是「沒有它畫不出來」的：顯示尺寸、專案層設定。</para>
    /// <code>
    /// sealed class MyPage : SCP_GuiToolPage
    /// {
    ///     readonly ISCP_GuiAppContext m_Ctx;
    ///     public MyPage(ISCP_GuiAppContext iCtx) : base() { m_Ctx = iCtx; }
    /// }
    /// </code>
    /// </summary>
    public interface ISCP_GuiAppContext
    {
        /// <summary>顯示參數（尺寸／間距／字級）。</summary>
        SCP_GuiStyle Style { get; }

        /// <summary>
        /// 上一次改尺寸的結果（成功或失敗都要有話說）。<c>null</c> ＝ 這一輪還沒人改過。
        /// <para>⚠ 三態：null／成功／失敗 —— 把失敗畫成「沒訊息」會讓「存不進去」看起來像「存好了」。</para>
        /// </summary>
        string? StyleMessage { get; }

        /// <summary>
        /// 套用尺寸**並持久化**。存不存得起來由宿主決定，但**結果一定要落到
        /// <see cref="StyleMessage"/>** —— 「這次有效、下次沒有」比「安靜地都沒有」好查。
        /// </summary>
        void ApplyStyle(SCP_GuiSize iSize);

        /// <summary>專案層設定（PlayerPrefs 概念，但三態不得同形 —— 見 SCP_Prefs）。</summary>
        ISCP_Prefs Prefs { get; }

        /// <summary>
        /// 宿主想補在尺寸頁底下的說明（例：CLI 的一次性覆寫旗標、Unity 的 Editor 行為）。
        /// <para>⚠ 為什麼要這一格：尺寸頁的**功能**是共用的，但它底下那幾句註腳
        /// （「`--scale` 不寫回檔案」）**只在某一個宿主為真**。
        /// 把宿主專屬的句子硬留在共用頁裡，另一個宿主的使用者會讀到一句假話 ——
        /// 而那種假話不會報錯，只會讓人去找一個不存在的旗標。</para>
        /// <para>空清單 ＝ 不畫（不是畫一個空框）。</para>
        /// </summary>
        IReadOnlyList<string> StyleNotes { get; }

        /// <summary>
        /// 要拿去做「頁面發現」反射掃描的 assembly（見 <see cref="SCP_GuiPageCatalog.Discover"/>）。
        /// <para>⚠ 由**宿主**給，本層不呼叫 <c>AppDomain.CurrentDomain.GetAssemblies()</c> ——
        /// .NET 是用到才載 assembly，「現在載了哪些」會隨執行路徑變，
        /// 而那個差異**不報錯**（症狀是同一份程式在別台機器少列兩頁）。</para>
        /// <para>空清單 ＝ 不做發現（那也是一個合法選擇，但畫面上就不會有「漏登記」的警示）。</para>
        /// </summary>
        IReadOnlyList<Assembly> PageAssemblies { get; }
    }
}
