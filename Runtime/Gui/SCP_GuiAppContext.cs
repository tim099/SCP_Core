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
        /// AgentCommands 資料根 —— **由宿主解析同一格設定**（<see cref="Paths.SCP_PathId.AgentCommandsRoot"/>）。
        /// <para>⚠ 回的是 <see cref="Paths.SCP_PathResolution"/> 而不是 <c>string</c>：
        /// 「解出來了」「沒有人填過」「取不到（例：兩個啟用專案 ⇒ 資料根不唯一）」**三態不可同形**，
        /// 而頁面必須說得出它是哪一態 —— 空字串會讓「量不到」長得像「沒有人在 session」。</para>
        /// </summary>
        /// <remarks>
        /// 🩸 為什麼這一格要進本介面（判準是檔頭那句「沒有它這一頁畫不出來嗎」）：
        /// 2026-09-04 我第一版讓 Session 管理頁**自己存一格手填的資料根**（`sessions/dataRoot` pref）。
        /// 那是同一個值的第二份，而且是手填的 ⇒ 它可以跟 `senate.local.json` 那格說不一樣的話，
        /// 而症狀是**頁面讀到另一棵樹的 session，然後每一列都顯示正常**。
        /// 同一天的現場更難看：**整個 CLI 早就解得出那個根**（每支 cmd 都印 `data_root=…`），
        /// 而那一頁印著「還沒設定資料根」—— 我把自己的 bug 讀成了設定的缺口。
        /// ⇒ 判準（與「路徑管理」頁檔頭同一條）：**能被推導或已經被存過的路徑，不准再存第二份。**
        /// 要改值一律去「路徑管理」頁（`senate ui --page paths`），本介面只讀。
        /// </remarks>
        Paths.SCP_PathResolution AgentCommandsRoot { get; }

        /// <summary>
        /// persona 信件庫根 —— **由宿主解析同一格設定**（<see cref="Paths.SCP_PathId.LettersRoot"/>）。
        /// <para>⚠ 同 <see cref="AgentCommandsRoot"/> 回 <see cref="Paths.SCP_PathResolution"/>：
        /// 三態不可同形，而且**它支援 `auto`** ⇒ 頁面拿到的必須是**解析後**的值。</para>
        /// </summary>
        /// <remarks>
        /// 🩸 為什麼補這一格（2026-09-05，basecamp）：「登入狀態」頁原本自己走
        /// <c>Prefs.Read(awakening.lettersRoot)</c> 拿**存起來的原始值**，
        /// 而這一格是 <c>[SCP_PathAuto]</c> 的 ⇒ 有人把它填成 <c>auto</c> 時，
        /// 那一頁會拿字面 <c>"auto"</c> 去掃目錄，掃不到 ⇒ 畫面說「這裡真的還沒有人」。
        /// **而 CLI 同時解得出真正的路徑** —— 跟 <see cref="AgentCommandsRoot"/> 那一筆同族：
        /// 頁面讀的是原始值，別人讀的是解析值，兩邊都沒報錯。
        /// ⇒ 判準：**支援 `auto` 的路徑，讀取端一律走解析器；原始值只屬於「路徑管理」頁的編輯框。**
        /// </remarks>
        Paths.SCP_PathResolution LettersRoot { get; }

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

        /// <summary>
        /// SCP_Core 自己的根（`Skills~/` 與 `AgentEntry/` 在它底下）。
        /// <para>⚠ 由宿主給 —— 各專案掛載位置不同（`Assets/Plugins/SCP_Core` / `&lt;repo&gt;/SCP_Core`…），
        /// 從 assembly location 反推跨消費端必壞，而且是靜默壞。</para>
        /// </summary>
        string CoreRoot { get; }

        /// <summary>
        /// **宿主自己**（它的 git root）—— skill 的**預設**安裝對象。
        /// <para>🩸 為什麼它是預設而不是 <see cref="ManagedProjects"/>：
        /// 我第一版照 UCL 那頁的模型假設「安裝對象＝被管理的專案」，
        /// 但那個模型的前提是**頁面住在它要裝的那個專案裡**。這裡不是 ——
        /// 這裡是外部工具，而在這裡跑的 agent 需要的是**這個 repo** 的 skill。
        /// ⇒ 預設裝自己，管理的專案是額外選項。</para>
        /// </summary>
        SCP_GuiProjectRef HostProject { get; }

        /// <summary>
        /// 這個宿主管得到的**其他**專案（也可以裝進去，但不是預設）。
        /// <para>Senate 管一批；Unity 那側就是它自己一個。
        /// ⚠ 空清單是合法狀態（還沒設定），畫面要說出來 —— 不要畫成「沒有東西可裝」。</para>
        /// </summary>
        IReadOnlyList<SCP_GuiProjectRef> ManagedProjects { get; }
    }
}
