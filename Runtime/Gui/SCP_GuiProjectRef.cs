// 區塊職責：「一個可以被安裝的專案」的最小描述 —— 名字 ＋ 根目錄。
// 物理意義：Senate 管一批專案、Unity 那側只有自己，而共用頁面不該認得任何一邊的專案型別
//           （Senate 的 ProjectReading 帶著 git 狀態與心跳，那些跟安裝無關）。
//           ⇒ 只暴露安裝真的需要的兩格。
// ⚠ EditorRunning 是**唯一**的例外欄位，而它有具體理由：安裝會寫進那個專案的工作區，
//   而 Unity Editor 正在跑的時候動它的 index 會撞上 AssetDatabase import。
//   宿主量不到就填 false，但那時畫面上要說「量不到」不是「沒在跑」——
//   ⇒ 用可空 bool，三態不得同形。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
namespace SCP.Core.Gui
{
    public sealed class SCP_GuiProjectRef
    {
        public SCP_GuiProjectRef(string iName, string iRoot, bool? iEditorRunning = null)
        {
            Name = iName;
            Root = (iRoot ?? "").Replace('\\', '/').TrimEnd('/');
            EditorRunning = iEditorRunning;
        }

        public string Name { get; }
        public string Root { get; }

        /// <summary>Unity Editor 是不是正在跑。<c>null</c> ＝ **量不到**（不是「沒在跑」）。</summary>
        public bool? EditorRunning { get; }
    }
}
