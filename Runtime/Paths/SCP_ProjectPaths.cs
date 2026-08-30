// 區塊職責：專案 repo 根底下的版面 —— 目前只有一格：**資料根 pointer 檔**。
// 物理意義：`.agentcommands_root.local` 是**跨語言跨 repo 的契約**：
//           C# 控制台寫、python 讀、Senate 也讀。而 2026-08-30 掃出來它被拼了三次：
//             · UCL_Core/UCL_AgentCommandsPath.cs        `PointerFileName`
//             · UCL_Core/Tools~/_lib/ucl_paths.py        `POINTER_FILENAME`
//             · Senate/src/Senate.Core/ProjectProbe.cs   字面值
//           ⇒ 檔名或解析規則改一次，要三個地方同時對；漏掉的那邊**不會報錯**，
//             它只會安靜地回退到 `<專案根>/AgentCommands` —— 而那個目錄通常真的存在。
//           本檔是 C# 這側的唯一落點（python 那份是另一個語言，靠這裡的註解對齊）。
// 數值影響：`ResolveDataRoot` 會**讀一次 pointer 檔**（唯讀，不寫）。其餘純字串。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.IO;

namespace SCP.Core.Paths
{
    public static class SCP_ProjectPaths
    {
        /// <summary>
        /// 資料根 pointer 檔名 —— **跨語言契約**（python `_lib/ucl_paths.py` 的
        /// <c>POINTER_FILENAME</c>、UCL C# 的 <c>UCL_AgentCommandsPath.PointerFileName</c> 同值）。
        /// <para>⚠ 改這個常數 ＝ 改跨語言契約，三端要一起改。</para>
        /// </summary>
        public const string DataRootPointerFileName = ".agentcommands_root.local";

        /// <summary>資料根沒有被搬走時的預設目錄名。</summary>
        public const string DefaultDataDirName = "AgentCommands";

        public static string DataRootPointer(SCP_ProjectRoot iProjectRoot)
            => iProjectRoot.Value + "/" + DataRootPointerFileName;

        /// <summary>資料根的判定來源 —— 讓呼叫端**說得出「這個路徑是怎麼來的」**。</summary>
        public enum DataRootOrigin
        {
            /// <summary>呼叫端顯式指定（設定檔寫了絕對／相對路徑）。</summary>
            Configured = 0,

            /// <summary>讀 pointer 檔得到的。</summary>
            Pointer = 1,

            /// <summary>兩者都沒有 ⇒ 用 <c>&lt;專案根&gt;/AgentCommands</c> 慣例。</summary>
            Convention = 2,
        }

        /// <summary>
        /// 解析資料根。<paramref name="iConfigured"/> 空字串或 <c>"auto"</c> ＝ 交給推導。
        /// <para>⚠ 回傳帶 <see cref="DataRootOrigin"/>：三種來源**不得同形** ——
        /// 「設定寫的」「pointer 指的」「慣例猜的」錯起來的修法完全不同，
        /// 而只回一個字串的話，後台頁沒辦法告訴人「我為什麼看這裡」。</para>
        /// <para>⛔ 本函式**只讀不寫**，而且找不到 pointer **不當錯誤** ——
        /// 沒有 pointer 是常態（多數專案沒搬過資料根）。</para>
        /// </summary>
        public static (SCP_DataRoot Root, DataRootOrigin Origin) ResolveDataRoot(
            SCP_ProjectRoot iProjectRoot, string? iConfigured)
        {
            string aCfg = (iConfigured ?? "").Trim();
            if (aCfg.Length > 0 && !aCfg.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                string aAbs = Path.IsPathRooted(aCfg) ? aCfg : iProjectRoot.Value + "/" + aCfg;
                return (new SCP_DataRoot(aAbs), DataRootOrigin.Configured);
            }

            string aPointer = DataRootPointer(iProjectRoot);
            if (File.Exists(aPointer))
            {
                string[] aLines;
                // pointer 讀不了 ⇒ 落到慣例，**但不假裝沒發生過**：呼叫端從 Origin 看得出來
                // 它拿到的是慣例值。（本層不 log —— SCP_Core 沒有 log 通道，那是宿主的事。）
                try { aLines = File.ReadAllLines(aPointer); }
                catch { aLines = Array.Empty<string>(); }

                foreach (string aRaw in aLines)
                {
                    string aLine = aRaw.Trim();
                    if (aLine.Length == 0 || aLine.StartsWith("#", StringComparison.Ordinal)) continue;
                    return (new SCP_DataRoot(aLine), DataRootOrigin.Pointer);
                }
            }

            return (new SCP_DataRoot(iProjectRoot.Value + "/" + DefaultDataDirName), DataRootOrigin.Convention);
        }
    }
}
