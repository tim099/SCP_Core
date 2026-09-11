using System;
using System.IO;
using System.Text;

// 區塊職責：文字檔的**落檔手勢** —— 換行正規化成 CRLF、UTF-8 無 BOM、近 atomic 取代。
// 物理意義：這是 `SCP_Cmd_Book` 與 `SCP_LibraryIO` 各自私有的 `WriteTextCrLf` 提取出來的共用層。
//          提取的時機不是「看起來重複」，是**第三個使用者出現**（`UCL_BooksIO`，TASK-0200）——
//          在那之前兩份是兩個巧合，不是共用邏輯。
// 數值影響：產物逐位元組等同舊的兩份私有複本（同樣 CRLF／無 BOM／不加尾換行），
//          ⛔ **取代機制不同** —— 見 `WriteCrLf` 的註解，那一格是刻意的行為改動。
namespace SCP.Core.Io
{
    public static class SCP_TextFile
    {
        /// <summary>
        /// 寫文字檔：換行一律正規化成 CRLF、UTF-8 **無 BOM**、temp → 取代（近 atomic）。父目錄自動建立。
        /// </summary>
        /// <remarks>
        /// 🩸 **CRLF 不是風格選擇**：既有簿冊是 python 文字模式寫的，在 Windows 上就是 CRLF。
        ///   寫成 LF 的失敗樣子是「**內容一樣而逐位元組不同**」——整批檔在下一次寫入時翻紅，
        ///   而那件事沒有任何一層會喊（TASK-0143 第五刀血證）。
        /// ⚠ 本函式只管**換行**這一根軸。縮排與「冒號後有沒有空格」由序列化器決定，
        ///   而那兩根軸一樣會讓逐位元組對拍整批翻紅（TASK-0200 血證：359 份 `chapter.json` 裡
        ///   只有 4 份跟 `SCP_JsonWriter` 的預設同形）。⇒ 呼叫端要自己顧那兩根。
        /// </remarks>
        public static void WriteCrLf(string iPath, string iText)
        {
            string aDir = Path.GetDirectoryName(iPath) ?? "";
            if (aDir.Length > 0) Directory.CreateDirectory(aDir);
            string aNormalized = iText.Replace("\r\n", "\n").Replace("\n", "\r\n");

            // temp 尾碼用 GUID：netstandard2.1 沒有 `Environment.ProcessId`（那是 .NET 5+）。
            // 它只影響 temp 檔名、不落在產物裡 ⇒ 不破壞逐位元組對拍。
            string aTmp = iPath + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(aTmp, aNormalized, new UTF8Encoding(false));

            // ⛔ **不做「先 Delete 再 Move」** —— 被提取的那兩份私有複本原本是那樣寫的，
            //   而 `Docs~/Coding_Standards.md §1.1` 明文點名它是「更貴的錯誤選項」：
            //   Delete 與 Move 之間有一格**檔案不存在**，而那一格長得跟
            //   「這份資料還沒被寫過」一模一樣 ⇒ 崩在那一瞬間的話，使用者的資料不是壞掉，
            //   是**看起來從來不存在**（而讀取端多半對「不存在」是 fail-soft 的）。
            // ⚠ `File.Move(src, dst, overwrite)` 那個三參數多載是 .NET Core 3.0 才有，
            //   netstandard2.1 編不過 ⇒ 目標存在時走 `File.Replace`，不存在才 `File.Move`。
            if (File.Exists(iPath)) File.Replace(aTmp, iPath, null);
            else File.Move(aTmp, iPath);
        }
    }
}
