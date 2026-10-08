// 區塊職責：酒館訊息附件路徑（refs）的**存法與解法** —— 唯一一處（TASK-0390，Tim 2026-10-07）。
// 物理意義：refs 存「**資料根相對**」的 `x/y/z`；讀的時候一律接上**當下設定的資料根**（AgentCommandsRoot）⇒ `<資料根>/x/y/z`。
//           資料根搬家（後台改設定）時舊訊息跟著走，⛔ 訊息裡不烘進任何機器路徑。
//   · 舊 refs 存成 `AgentCommands/x/y/z`（2026-10-07 實測：全部 refs 裡
//     ChatTavern 398、Canvas 240、Sculpture 6 筆都是這個形狀）。讀取時去掉這個前綴再接資料根 —— 不用改任何舊檔。
//   🩸 ⛔ 不用「資料根的上一層」當基準：資料根一搬家，那一層就跟著變，
//     refs 全部解到不存在的路徑，Discord 轉發的附圖安靜地被跳過。
// 數值影響：純字串＋Path 運算，零 IO。
// ⚠ 資料根外的絕對路徑照原樣存（呼叫端要說出來）；相對路徑一律當資料根相對，⛔ 沒有別的基準。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.IO;

namespace SCP.Core.Tavern
{
    public static class SCP_TavernRefPath
    {
        /// <summary>舊慣例的前綴（既有 refs 裡的 `AgentCommands/…`）。</summary>
        public const string LegacyPrefix = "AgentCommands/";

        /// <summary>
        /// 寫入：把呼叫端給的路徑轉成要存的形狀。資料根底下的絕對路徑 ⇒ 相對；相對路徑 ⇒ 去掉舊前綴後原樣；
        /// 資料根外的絕對路徑 ⇒ 原樣，<paramref name="oOutside"/>＝true（呼叫端要印警告）。
        /// </summary>
        public static string ToStored(string iDataRoot, string iRaw, out bool oOutside)
        {
            oOutside = false;
            string p = (iRaw ?? "").Trim().Replace('\\', '/');
            if (p.Length == 0) return "";
            if (!Path.IsPathRooted(p)) return StripLegacy(p);
            string aFull = Path.GetFullPath(p).Replace('\\', '/');
            string aRoot = NormRoot(iDataRoot);
            if (aRoot.Length > 0 && aFull.StartsWith(aRoot + "/", StringComparison.OrdinalIgnoreCase))
                return aFull.Substring(aRoot.Length + 1);
            oOutside = true;
            return aFull;
        }

        /// <summary>讀取：存的形狀 ⇒ 絕對路徑（`/` 分隔）。絕對路徑原樣；其餘去掉舊前綴後接資料根。</summary>
        public static string Resolve(string iDataRoot, string iStored)
        {
            string p = (iStored ?? "").Trim().Replace('\\', '/');
            if (p.Length == 0 || Path.IsPathRooted(p)) return p;
            string aRoot = NormRoot(iDataRoot);
            return aRoot.Length == 0 ? p : aRoot + "/" + StripLegacy(p);
        }

        static string StripLegacy(string iRel)
            => iRel.StartsWith(LegacyPrefix, StringComparison.OrdinalIgnoreCase) ? iRel.Substring(LegacyPrefix.Length) : iRel;

        static string NormRoot(string? iDataRoot)
        {
            if (string.IsNullOrWhiteSpace(iDataRoot)) return "";
            try { return Path.GetFullPath(iDataRoot!.Trim()).Replace('\\', '/').TrimEnd('/'); }
            catch (Exception) { return iDataRoot!.Trim().Replace('\\', '/').TrimEnd('/'); }
        }
    }
}
