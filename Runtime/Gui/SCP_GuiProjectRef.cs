// 區塊職責：「一個可以被安裝的專案」的最小描述 —— 名字 ＋ 根目錄。
// 物理意義：共用頁面不該認得宿主的專案型別 ⇒ 只暴露安裝真的需要的兩格。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
namespace SCP.Core.Gui
{
    public sealed class SCP_GuiProjectRef
    {
        public SCP_GuiProjectRef(string iName, string iRoot)
        {
            Name = iName;
            Root = (iRoot ?? "").Replace('\\', '/').TrimEnd('/');
        }

        public string Name { get; }
        public string Root { get; }
    }
}
