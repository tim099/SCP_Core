// 區塊職責：介面尺寸設定頁 —— 從入口頁 push 進來（頁面堆疊的第一個真消費者）。
// 物理意義：⭐ 它同時是兩件事：一個真的設定頁，也是「頁面系統在四種驅動方式下都成立」的讀數 ——
//           人在視窗裡按、agent 用 `--click home/open/style` 進來再 `--click style/big`、
//           文字模式看得到現在停在哪一頁（麵包屑）、截圖證明它真的畫出來了。
//
//           ⭐ 2026-08-30 從 Senate.Cli/Pages/StylePage.cs 搬進 SCP_Core（六步的第 3 步）。
//           它是第一個**只吃 ISCP_GuiAppContext、不吃宿主 model** 的頁 ——
//           也就是這一步真正要證明的那件事：接縫切對了，頁面就跟宿主脫鉤。
// 數值影響：按下尺寸會**寫回宿主的設定檔**（走 ISCP_GuiAppContext.ApplyStyle，
//           存不存得起來由宿主決定，但結果一定落到 StyleMessage）。除此之外唯讀。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
namespace SCP.Core.Gui
{
    public sealed class SCP_GuiStylePage : SCP_GuiToolPage
    {
        readonly ISCP_GuiAppContext m_Ctx;

        /// <summary>`: base()` 讓 [CallerFilePath] 填 SourceFilePath（隱式 base() 會是 null）。</summary>
        public SCP_GuiStylePage(ISCP_GuiAppContext iCtx) : base() { m_Ctx = iCtx; }

        public override string Key { get { return PageKey; } }
        public const string PageKey = "style";

        public override string Title { get { return "介面尺寸"; } }

        /// <summary>列進入口頁的「設定」組。</summary>
        public override string? MenuGroup { get { return "設定"; } }

        protected override void DrawContent(SCP_Ui g)
        {
            // ⭐ 把「它以為自己多大」印出來 —— 尺寸這種東西「看起來變大了」不算讀數，
            //    截圖旁邊沒有數字就對不起來。
            g.Label(m_Ctx.Style.Describe());

            SCP_GuiSize? aPick = m_Ctx.Style.DrawPicker(g, "style");
            if (aPick.HasValue) m_Ctx.ApplyStyle(aPick.Value);

            if (m_Ctx.StyleMessage != null) g.Note(m_Ctx.StyleMessage);

            g.Space();
            g.Note("字級要重開視窗才會換（字級綁在載入時建好的 atlas）；間距與版位即時生效。");

            // 宿主專屬的註腳由宿主給 —— 寫死在這裡的話，另一個宿主會讀到一句假話。
            foreach (string aNote in m_Ctx.StyleNotes) g.Note(aNote);
        }
    }
}
