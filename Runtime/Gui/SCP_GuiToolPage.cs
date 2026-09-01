// 區塊職責：**後台頁的標準骨架** —— 一排工具列（返回／首頁／自訂鈕）＋ 下面的內容。
// 物理意義：概念取自 Unity 端的 UCL_EditorPage（TopBar：Back/Close/Help ＋ TopBarButtons ＋ ContentOnGUI）
//           與 UCL_CommonEditorPage（ShowInPageMenu 決定要不要列進選單）。
//           ⭐ 這裡把那兩層**合成一層**：UCL 分兩層是歷史（通用層與「加類名＋Copy」層），
//           而這裡兩層都只有同一批消費端 —— 分兩層只會多一個「該繼承哪一個」的問題。
// 數值影響：本層零 IO、零繪圖依賴，只往 SCP_Ui 掛節點。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Runtime.CompilerServices;

namespace SCP.Core.Gui
{
    /// <summary>
    /// 帶工具列的一頁。子類實作 <see cref="DrawContent"/>，要在工具列加鈕就覆寫 <see cref="ToolBarButtons"/>。
    /// <code>
    /// sealed class MyPage : SCP_GuiToolPage
    /// {
    ///     public override string Key => "my";
    ///     public override string Title => "我的頁面";
    ///     public override string? MenuGroup => "工具";        // null ＝ 不列進入口頁的清單
    ///     protected override void ToolBarButtons(SCP_Ui g)
    ///     {
    ///         if (g.Button("重新載入", "my/reload")) Reload();
    ///     }
    ///     protected override void DrawContent(SCP_Ui g) { … }
    /// }
    /// </code>
    /// </summary>
    public abstract class SCP_GuiToolPage : SCP_GuiPage
    {
        /// <summary>「回首頁」鈕的固定 id —— 跟 <see cref="SCP_GuiPageController.BackButtonId"/> 一樣是契約。</summary>
        public const string HomeButtonId = "page/home";

        /// <summary>「開啟原始碼位置」鈕的固定 id。</summary>
        public const string SourceButtonId = "page/source";

        /// <summary>「複製類別名」鈕的固定 id（開不了檔案總管時的退路）。</summary>
        public const string CopyClassButtonId = "page/copy-class";

        /// <summary>把上一則訊息關掉的鈕的固定 id。</summary>
        public const string DismissMessageButtonId = "page/message-dismiss";

        /// <summary>
        /// 上一次按「原始碼」／「複製類別名」的結果。**null ＝ 沒有話要說**。
        /// <para>⚠ 空字串與 null 在這裡是同一件事，一律經 <see cref="SetMessage"/> 正規化 ——
        /// 🩸 2026-09-01：宿主改成「成功時回空字串」，而顯示條件寫的是 <c>!= null</c>
        /// ⇒ 空字串照樣過關，畫面上多出一條**內容為空的 Note**（一條看不出來源的空行）。
        /// 「沒有話要說」與「有一句空話」在型別上不同形，在畫面上卻同形。</para>
        /// </summary>
        string? m_SourceMessage;

        /// <summary>
        /// 設定訊息，**空白一律收斂成 null**（＝沒有話要說）。
        /// 呼叫端不要直接寫 <c>m_SourceMessage</c> —— 那就是上面那隻空行的入口。
        /// </summary>
        void SetMessage(string? iText)
            => m_SourceMessage = string.IsNullOrWhiteSpace(iText) ? null : iText;

        /// <summary>
        /// 這一頁的原始碼檔**精確路徑** —— 編譯時由 <see cref="CallerFilePathAttribute"/> 烤進來。
        /// <para>⚠ 只有子類的 ctor **顯式寫 <c>: base()</c>** 才拿得到；沒寫就是 null，
        /// 這時退回 <see cref="SourceFileName"/>（用類別名去 repo 裡找）。</para>
        /// <para>🩸 實測（2026-08-23，本機 .NET 10）—— 這件事只能量不能推：</para>
        /// <code>
        /// class Implicit : B { public Implicit(int x) { } }          // F = null
        /// class Explicit : B { public Explicit(int x) : base() { } } // F = "…\p.cs"  ✓
        /// class NoCtor   : B { }                                     // F = null
        /// </code>
        /// <para>⇒ 所以它**不能**是唯一的來源：忘了寫 <c>: base()</c> 的症狀會是
        /// 「那顆鈕安靜地不見」，而那正是這個 repo 最不想要的失敗形狀。</para>
        /// </summary>
        public string? SourceFilePath { get; }

        /// <summary>這一頁的類別名（<c>HomePage</c>）。⚠ 它**不等於** page key（<c>home</c>）。</summary>
        public string SourceClassName => GetType().Name;

        /// <summary>
        /// 退而求其次的線索：<c>類別名 + ".cs"</c>，由宿主去 repo 裡找。
        /// <para>⚠ 這是**猜**，所以宿主找到多個同名檔時要停手並說出來，不可以挑第一個 ——
        /// 「開到另一個同名檔」跟「開對了」在畫面上長得一樣。</para>
        /// </summary>
        public virtual string SourceFileName => SourceClassName + ".cs";

        /// <summary>
        /// ⚠ 參數**不要自己傳值**，它靠編譯器填；而且**只有顯式 <c>: base()</c> 才會被填**
        /// （見 <see cref="SourceFilePath"/> 的血證）。
        /// </summary>
        protected SCP_GuiToolPage([CallerFilePath] string? iSourceFile = null)
        {
            SourceFilePath = iSourceFile;
        }

        /// <summary>
        /// 這一頁要不要列進入口頁的清單，以及列在哪一組。
        /// <para><c>null</c> ＝ 不列（預設，opt-in）；非 null ＝ 列進去，而**字串本身就是分組名**。</para>
        /// <para>⭐ 取代 UCL 的 <c>ShowInPageMenu</c>（bool）。差別不只是型別：
        /// bool 只能回答「要不要出現」，清單一長就變成一坨沒有結構的按鈕；
        /// 字串同時回答「要不要出現」與「跟誰一國」，於是入口頁可以先篩分組再選頁。
        /// ⚠ 空字串**不等於** null —— 空字串是「列進去、但沒有分組名」，
        /// 那跟「不要列」是兩件事，不可以同形。</para>
        /// </summary>
        public virtual string? MenuGroup => null;

        /// <summary>工具列上要不要「◀ 返回」。預設：堆疊深度 > 1 時才有意義（在最底層按返回沒有去處）。</summary>
        protected virtual bool ShowBackButton => Controller != null && Controller.Count > 1;

        /// <summary>
        /// 工具列上要不要「⌂ 首頁」。預設：深度 > 2 才畫 ——
        /// 深度剛好 2 時「返回」與「首頁」是同一個動作，兩顆長得不一樣卻做同一件事只會讓人猶豫。
        /// </summary>
        protected virtual bool ShowHomeButton => Controller != null && Controller.Count > 2;

        /// <summary>
        /// 工具列尾巴要不要印 page key。預設 true。
        /// <para>⭐ 這是 UCL「類名 ＋ Copy 鈕」那一格的對應物，但印的是 **page key 而不是類名** ——
        /// 類名對使用者沒有用途，page key 才是 <c>--page</c> / session <c>nav</c> / 麵包屑共用的那個字。
        /// （沒有 Copy 鈕：共用層碰不到剪貼簿，而「有一顆按了沒事的鈕」比沒有那顆鈕糟。）</para>
        /// </summary>
        protected virtual bool ShowKeyHint => true;

        /// <summary>
        /// 工具列上要不要「原始碼」鈕。條件只有一個：**宿主裝了開檔案總管的能力**
        /// （<see cref="SCP_GuiHost.RevealInFileManager"/>）。
        /// <para>⚠ 沒裝就不畫 —— 畫一顆按了不會有事的鈕，比沒有那顆鈕糟
        /// （這是 UCL 那顆 Help 鈕的同一格：沒有 HelpURL 就不畫）。</para>
        /// <para>路徑本身一定有東西可以交（精確路徑或類別名），所以不列入條件。</para>
        /// </summary>
        protected virtual bool ShowSourceButton => SCP_GuiHost.RevealInFileManager != null;

        /// <summary>
        /// 開不了檔案總管時的**退路**：一顆「複製類別名」。
        /// <para>⚠ 是退路不是附加品 —— 兩顆都畫只是讓工具列變長，
        /// 而它們回答的是同一個問題（「這一頁的碼在哪」）。</para>
        /// <para>兩種能力都沒有時這顆也不畫，改由 <see cref="ShowKeyHint"/> 那行**直接把類別名印出來** ——
        /// 「至少知道是哪個 class」這件事不可以有任何一條路徑掉在地上。</para>
        /// </summary>
        protected virtual bool ShowCopyClassButton
            => SCP_GuiHost.RevealInFileManager == null && SCP_GuiHost.CopyToClipboard != null;

        /// <summary>兩種能力都沒有 ⇒ page key 後面補上類別名（見上）。</summary>
        bool NeedsClassInHint
            => SCP_GuiHost.RevealInFileManager == null && SCP_GuiHost.CopyToClipboard == null;

        /// <summary>自己畫工具列 ⇒ controller 不要再畫一顆返回鈕（見 <see cref="SCP_GuiPage.OwnsNavBar"/>）。</summary>
        public override bool OwnsNavBar => true;

        /// <summary>返回鈕按下去做什麼（預設回上一頁）。</summary>
        protected virtual void BackButtonClicked() { Controller?.Pop(); }

        /// <summary>
        /// 首頁鈕按下去做什麼（預設 pop 回最底層那頁）。
        /// ⚠ 刻意不是 <c>PopAll</c> —— 那會清空堆疊，畫面變空白而不是回到入口頁。
        /// </summary>
        protected virtual void HomeButtonClicked() { Controller?.PopToRoot(); }

        /// <summary>
        /// 工具列。⚠ **先收集動作、離開 Row 之後才執行** ——
        /// handler 裡的 push／pop 會改變 <see cref="ShowBackButton"/> 的答案，
        /// 在同一輪的 Row 中途改變版面會讓後面幾顆鈕的 id 跟著漂。
        /// </summary>
        protected virtual void DrawToolBar(SCP_Ui iUi)
        {
            int aAction = 0;   // 0 none / 1 back / 2 home
            using (iUi.Row())
            {
                if (ShowBackButton && iUi.Button("◀ 返回", SCP_GuiPageController.BackButtonId)) aAction = 1;
                if (ShowHomeButton && iUi.Button("⌂ 首頁", HomeButtonId)) aAction = 2;

                // 「這一頁的碼在哪」—— UCL 那顆 Help 鈕的同一格（位置也一樣：導覽鈕之後、自訂鈕之前）。
                // ⚠ 標籤刻意是純文字不是 📁：那顆 emoji 在不在字型的 glyph 範圍內是另一回事，
                //   而缺字**不報錯**，只會變成一個方塊（SenateFonts 的血證就是這一族）。
                if (ShowSourceButton && iUi.Button("原始碼", SourceButtonId)) aAction = 3;
                else if (ShowCopyClassButton && iUi.Button("複製類別名", CopyClassButtonId)) aAction = 4;

                // ⚠ 這裡刻意**不 try/catch**：工具列的按鈕炸掉是程式錯誤，
                //   吞掉它只會讓「那顆鈕沒反應」變成沒有人查得到的事（UCL 那側有 Debug.LogException
                //   可以吞得起來，共用層沒有 logger —— 吞了就是真的沒有讀數）。
                ToolBarButtons(iUi);

                if (ShowKeyHint)
                    iUi.Label(NeedsClassInHint
                        ? $"｜page key: {Key}（{SourceClassName}）"
                        : "｜page key: " + Key);
            }

            if (aAction == 1) BackButtonClicked();
            else if (aAction == 2) HomeButtonClicked();
            else if (aAction == 3) SourceButtonClicked();
            else if (aAction == 4) CopyClassButtonClicked();

            // ⚠ 條件是「有沒有話要說」不是「欄位是不是 null」（見 m_SourceMessage 的血證）。
            if (m_SourceMessage != null)
            {
                // 關閉鈕：訊息會一直留到下一次按鈕為止，而**失敗訊息通常比它的原因活得久** ——
                // 使用者修好問題之後那行字還掛在那裡，看起來像「還是失敗」。
                // ⇒ 給一個把它收掉的動作。⚠ 沿用工具列的規矩：先收集、離開 Row 再執行。
                bool aDismiss = false;
                using (iUi.Row())
                {
                    iUi.Note(m_SourceMessage);
                    // ⚠ 標籤刻意是純文字不是 ✕：缺字不報錯，只會變成一個方塊（同工具列那條）。
                    if (iUi.Button("關閉", DismissMessageButtonId)) aDismiss = true;
                }
                if (aDismiss) SetMessage(null);
            }
        }

        /// <summary>
        /// 原始碼鈕按下去做什麼（預設：請宿主在檔案總管裡顯示這一頁的 .cs）。
        /// <para>⚠ **失敗**一定要落到畫面上：這個動作的效果發生在別的視窗，
        /// 沒有那一行字的話，「開不起來」與「什麼都沒發生」在這裡完全同形。</para>
        /// <para>成功要不要說話**由宿主決定**（回空字串＝不說）。原本這裡硬性要求成功也留一行，
        /// Tim 2026-09-01 拍板改掉：在有視窗的宿主上，**跳出來的檔案總管本身就是那個讀數**，
        /// 再補一行字只是每按一次就多一條沒有人會讀的訊息。
        /// ⚠ 代價要說出來：在沒有人看得到桌面的宿主（headless／自動化）上，
        /// 成功這一格從此**沒有讀數** —— selftest 目前也只覆蓋失敗那條路。</para>
        /// </summary>
        protected virtual void SourceButtonClicked()
        {
            Func<string, string>? aReveal = SCP_GuiHost.RevealInFileManager;
            if (aReveal == null)
            {
                // ShowSourceButton 擋過一次了，會走到這裡代表狀態在兩次繪製之間變了
                SetMessage("⚠ 這個環境開不了檔案總管");
                return;
            }
            // 精確路徑優先；沒有就交類別名讓宿主去找（找到多個要由宿主停手，不是這裡猜）
            string aResult = aReveal(SourceFilePath ?? SourceFileName);

            if (!aResult.StartsWith("⚠", StringComparison.Ordinal))
            {
                // 成功。宿主回空字串 ⇒ 什麼都不畫（連空行都不要）。
                SetMessage(aResult);
                return;
            }

            // ⭐ 開不起來（headless／遠端桌面／路徑不在這台機器上）⇒ **自動退到複製類別名**。
            //   為什麼是自動而不是「請按另一顆鈕」：那顆鈕在這個宿主上不會被畫出來
            //   （ShowCopyClassButton 的條件是「連 reveal 都沒裝」），
            //   而「reveal 裝了但這次失敗」才是實際會發生的那一種。
            //   ⚠ 不管退到哪一步，訊息裡**一定看得到類別名** —— 這條路存在的唯一理由就是那個。
            Func<string, string>? aCopy = SCP_GuiHost.CopyToClipboard;
            if (aCopy == null)
            {
                SetMessage(aResult + $"（類別：{SourceClassName}）");
                return;
            }
            string aCopied = aCopy(SourceClassName);
            SetMessage(aCopied.StartsWith("⚠", StringComparison.Ordinal)
                ? aResult + $"／也複製不了 —— 類別是 {SourceClassName}"
                : aResult + $"／已改為複製類別名：{SourceClassName}");
        }

        /// <summary>
        /// 複製類別名（開不了檔案總管時的退路）。
        /// ⚠ 連剪貼簿都失敗時，宿主的訊息裡**也要帶著那個名字** —— 那是這條路存在的唯一理由。
        /// </summary>
        protected virtual void CopyClassButtonClicked()
        {
            Func<string, string>? aCopy = SCP_GuiHost.CopyToClipboard;
            SetMessage(aCopy == null
                ? $"⚠ 這個環境也沒有剪貼簿 —— 類別是 {SourceClassName}"
                : aCopy(SourceClassName));
        }

        /// <summary>子類在工具列上加自己的鈕（對應 UCL 的 <c>TopBarButtons</c>）。</summary>
        protected virtual void ToolBarButtons(SCP_Ui iUi) { }

        /// <summary>頁面內容（對應 UCL 的 <c>ContentOnGUI</c>）。</summary>
        protected abstract void DrawContent(SCP_Ui iUi);

        /// <summary>
        /// ⚠ <c>sealed</c>：本基底的整個重點就是「工具列一定會被畫」。
        /// 開放覆寫的話會出現「我覆寫了 Draw，結果返回鈕不見了」——
        /// 而那個症狀看起來像框架壞掉，不像自己少呼叫了一行。
        /// </summary>
        public sealed override void Draw(SCP_Ui iUi)
        {
            DrawToolBar(iUi);
            DrawContent(iUi);
        }
    }
}
