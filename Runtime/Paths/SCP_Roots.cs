// 區塊職責：**有型別的「根」** —— 專案根 / 資料根 / 信件夾根各自一個型別。
// 物理意義：路徑解析器一律吃「根 ＋ 相對版面」。如果三種根都是裸 `string`，
//           **傳錯根是編譯得過的**，而算出來的路徑看起來完全正常
//           （`<lettersRoot>/queues/basecamp` 這種東西不會有任何一層喊）。
//           ⇒ 包成不同型別，讓「傳錯根」變成編譯錯 —— 修法優先序第一階
//             （讓那格失敗**不可能發生**），而不是第三階（記得傳對）。
// 數值影響：純值型別，零 IO、零配置壓力（readonly struct）。建構時做正規化：
//           反斜線換成 `/`、去尾斜線 —— 讓「同一個目錄的兩種寫法」不會變成兩個 key。
// 🩸 為什麼值得一個型別：2026-08-17 UCL 那側一天三撞，最貴的一筆是
//   `dataPath/../..` 跳出去**剛好命中一棵舊資料樹** ⇒ 餘額回報 453、真實帳本 1330，
//   差 877 而連錯誤訊息都沒有。**最壞的失敗不是找不到檔，是找到了另一個宇宙的檔。**
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
// 規範：<SCP_Core>/Docs~/Coding_Standards.md。
#nullable enable
using System;

namespace SCP.Core.Paths
{
    /// <summary>三種根共用的正規化（唯一一份 —— 各根自己做一次就會有三種寫法）。</summary>
    internal static class SCP_RootText
    {
        internal static string Clean(string? iRaw, string iWhat)
        {
            string a = (iRaw ?? "").Trim().Replace('\\', '/');
            while (a.Length > 1 && a.EndsWith("/", StringComparison.Ordinal)) a = a.Substring(0, a.Length - 1);
            if (a.Length == 0) throw new ArgumentException($"{iWhat} 不可以是空字串（根要由宿主傳進來，本層不推導）", iWhat);
            return a;
        }
    }

    /// <summary>
    /// 專案 repo 根（例：<c>D:/Unity/Bar</c>）。
    /// <para>⚠ 誰決定它：**宿主**。Senate 是 <c>senate.local.json</c> 的 <c>projects[].root</c>，
    /// Unity 那側是既有的 repo 解析器。本層不找它。</para>
    /// </summary>
    public readonly struct SCP_ProjectRoot
    {
        public SCP_ProjectRoot(string iPath) { Value = SCP_RootText.Clean(iPath, nameof(iPath)); }
        public string Value { get; }
        public override string ToString() { return Value; }
    }

    /// <summary>
    /// AgentCommands 資料根（例：<c>D:/Unity/Bar/AgentCommands</c>）。
    /// <para>⚠ 它**不一定**是 <c>&lt;專案根&gt;/AgentCommands</c> —— 可以被 pointer 檔搬走
    /// （見 <see cref="SCP_ProjectPaths.ResolveDataRoot"/>）。所以它是獨立的一種根，不是專案根的衍生。</para>
    /// </summary>
    public readonly struct SCP_DataRoot
    {
        public SCP_DataRoot(string iPath) { Value = SCP_RootText.Clean(iPath, nameof(iPath)); }
        public string Value { get; }
        public override string ToString() { return Value; }
    }

    /// <summary>
    /// persona 信件夾根（例：<c>&lt;資料根&gt;/ChatTavern/baton/letters</c>）。
    /// <para>⚠ 它通常由資料根算出來（<see cref="SCP_DataPaths.Letters"/>），
    /// 但也可以被設定覆寫成別的地方 ⇒ 仍然是獨立型別，不可以拿 <see cref="SCP_DataRoot"/> 硬轉。</para>
    /// </summary>
    public readonly struct SCP_LettersRoot
    {
        public SCP_LettersRoot(string iPath) { Value = SCP_RootText.Clean(iPath, nameof(iPath)); }
        public string Value { get; }
        public override string ToString() { return Value; }
    }
}
