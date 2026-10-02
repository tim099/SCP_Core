// 區塊職責：宿主提供閱讀庫操作使用的設定解析結果。
// 物理意義：獨立資料根與信件庫根保留各自來源；外部漫畫庫只在掃漫畫時驗證。
// 數值影響：只攜帶值，不讀檔、不推導 persona、不接受指令路徑覆寫。
#nullable enable
using SCP.Core.Paths;

namespace SCP.Core.Library
{
    /// <summary>一次指令使用同一份設定快照解析出的閱讀庫根目錄。</summary>
    public sealed class SCP_LibraryRoots
    {
        public SCP_LibraryRoots(SCP_PathResolution iDataRoot, SCP_PathResolution iLettersRoot,
                                SCP_PathResolution iComicRoot)
        { DataRoot = iDataRoot; LettersRoot = iLettersRoot; ComicRoot = iComicRoot; }

        public SCP_PathResolution DataRoot { get; }
        public SCP_PathResolution LettersRoot { get; }
        public SCP_PathResolution ComicRoot { get; }
    }
}
