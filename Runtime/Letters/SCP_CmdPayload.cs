// 區塊職責：寫 persona 的 Cmd 回傳檔（`letters/<persona>/cmd/<cmd>_<step>.md`）—— 原子寫＋目錄層 .gitignore。
// 物理意義：移植自 UCL_Core `UCL_LettersPath.EnsureCmdDir` 與各 Cmd 的回傳落檔（TASK-0303）。
//          `cmd/` 裡有些檔含 session_token／信箱，而 letters remote 可能是公開的 ⇒ 目錄層一律 ignore。
//          ⛔ .gitignore 字面與 Editor 端 `UCL_LettersPath.CmdDirGitignore` 同一份，改一端要改另一端。
// 數值影響：建目錄、必要時建 .gitignore（已存在不覆寫）、tmp＋Replace 寫一個檔。
#nullable enable
using System.IO;
using System.Text;

namespace SCP.Core.Letters
{
    public static class SCP_CmdPayload
    {
        public const string CmdDirGitignore =
            "# Cmd 回傳檔（transient）—— 每跑一次就重生、手改無效，一律不入版控。\n" +
            "# 有些回傳檔含 session_token / 信箱等憑證，而 letters remote 可能是公開的；\n" +
            "# 這份 ignore 是「目錄層」的，所以新增任何 Cmd / step 都不必再維護逐檔清單。\n" +
            "# 本檔由 UCL_LettersPath.EnsureCmdDir() / ucl_paths.ensure_letters_cmd_dir() 自動建立（兩端同一份字面）。\n" +
            "*\n" +
            "!.gitignore\n";

        /// <summary>原子寫一份回傳檔（目錄不存在就建，並補 .gitignore）。失敗丟例外 —— 回傳檔寫不出來要當場說。</summary>
        public static void Write(string iPath, string iText)
        {
            string aDir = Path.GetDirectoryName(iPath)!;
            Directory.CreateDirectory(aDir);
            string aIgnore = Path.Combine(aDir, ".gitignore");
            if (!File.Exists(aIgnore)) File.WriteAllText(aIgnore, CmdDirGitignore, new UTF8Encoding(false));
            WriteAtomic(iPath, iText);
        }

        /// <summary>tmp＋Replace 原子寫（UTF-8 無 BOM）。⚠ 不用 Delete＋Move：之間檔案不存在（TASK-0264 那一族）。</summary>
        public static void WriteAtomic(string iPath, string iText)
        {
            string? aDir = Path.GetDirectoryName(iPath);
            if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iText, new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Replace(aTmp, iPath, null);
            else File.Move(aTmp, iPath);
        }
    }
}
