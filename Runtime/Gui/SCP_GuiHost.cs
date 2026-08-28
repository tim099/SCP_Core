// 區塊職責：**由宿主安裝的能力** —— 共用層想做、但共用層不准做的那些事（都會碰 IO／OS）。
// 物理意義：SCP_Core 的邊界是「純函式＋零依賴」，所以它不能開檔案總管、不能碰剪貼簿。
//           但頁面基底**知道自己想要那個按鈕**。⇒ 由宿主（Senate.Cli / Unity Editor）在啟動時
//           把實作掛進來；沒掛的環境**那顆按鈕根本不畫**（不是畫一顆按了沒事的鈕）。
// 數值影響：只是一組委派。本檔零 IO。
// ⚠ 這是 static，而這個 repo 對 singleton 是有戒心的（D13 把 UCL 的 Ins 拿掉）。
//   判準是同一條：**把「只有一個」縮到它真正只有一個的那一層。**
//   「這台機器怎麼開檔案總管」是**每個 process 一個**，不是每個視窗一個 ——
//   開兩個視窗不會讓 explorer 變成兩種。所以它可以是 static，而 page controller 不行。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;

namespace SCP.Core.Gui
{
    public static class SCP_GuiHost
    {
        /// <summary>
        /// 在檔案總管／Finder 裡顯示某個路徑（通常是「選取這個檔」）。
        /// <para>回傳值是**一行人可讀的結果** —— 成功也要有話說：
        /// 按下去之後如果什麼都沒發生（遠端桌面、headless、路徑不存在），
        /// 使用者需要知道是哪一種，而不是對著一顆「好像壞掉的鈕」。</para>
        /// <para><c>null</c> ＝ 這個宿主沒有這個能力 ⇒ 相關按鈕**不會被畫出來**。</para>
        /// </summary>
        public static Func<string, string>? RevealInFileManager;

        /// <summary>
        /// 把一段字放進剪貼簿。回傳一行人可讀的結果（同上：效果不在這個畫面上，所以一定要有話說）。
        /// <para><c>null</c> ＝ 這個宿主沒有這個能力。</para>
        /// <para>⭐ 它是 <see cref="RevealInFileManager"/> 的**退路**而不是附加品：
        /// 開不了檔案總管的環境（headless、遠端桌面、還沒接這條線的宿主）至少要能知道
        /// 「這一頁是哪個 class」，否則使用者手上只剩一個 page key，
        /// 而 page key **不等於**類別名（`home` ↔ `HomePage`）。</para>
        /// </summary>
        public static Func<string, string>? CopyToClipboard;

        /// <summary>
        /// 從剪貼簿讀一段字。<c>null</c> ＝ 這個宿主沒有這個能力 ⇒ 相關按鈕**不會被畫出來**。
        /// <para>⭐ 為什麼需要它：ImGui 的 <c>InputText</c> 在這個宿主上**吃不到 Ctrl+V**
        /// （ImGui 的剪貼簿 callback 沒有被接上），所以「貼一段路徑進來」只能手打。
        /// 一個要求使用者手打絕對路徑的欄位，實際上就是一個不會被用的欄位。</para>
        /// <para>⚠ 這是**讀**的方向，跟 <see cref="CopyToClipboard"/> 是兩件事，
        /// 所以刻意分成兩個委派 —— 一個宿主可能只做得到其中一邊
        /// （寫走 <c>clip.exe</c> 很容易，讀在 Windows 上要繞 PowerShell 或 Win32）。</para>
        /// </summary>
        public static Func<SCP_ClipboardRead>? ReadClipboard;
    }

    /// <summary>
    /// 讀剪貼簿的結果。
    /// <para>⚠ 三格刻意分開，因為「剪貼簿是空的」與「我讀不到剪貼簿」**不得同形** ——
    /// 壓成一個空字串之後，一個壞掉的能力會看起來像「使用者沒複製東西」，
    /// 而那會讓人一直重按那顆鈕。</para>
    /// </summary>
    public sealed class SCP_ClipboardRead
    {
        /// <summary>讀到了嗎（＝這次操作本身成功，內容可以是空的）。</summary>
        public bool Ok;

        /// <summary>讀到的字（<see cref="Ok"/> 為 false 時無意義）。</summary>
        public string Text = "";

        /// <summary>一行人可讀的結果 —— **成功也要有話說**（效果發生在別的地方，畫面要說得出發生了什麼）。</summary>
        public string Message = "";
    }
}
