// 區塊職責：CLI 叫人去讀一個檔時，順手把**它有多長、讀到哪才算完**講出來（TASK-0419）。
// 物理意義：各家 agent 的讀檔工具一次吐得出的量不同（Claude Code Read 約 25k token、Antigravity view_file 800 行／45 KB、
//          Codex exec 依當次輸出預算，而且截的是**中段**）—— CLI 不知道讀的是哪一家，也不替它決定怎麼切。
//          ⇒ 只給兩個事實：總行數與大小；超過門檻再提醒「用你的工具分段讀到第 N 行」。
//          門檻來自 SCP_WakeBriefSettings（後台「早安 brief」頁的「回傳檔大檔提示」）。
// 數值影響：每次呼叫讀一次檔（只數 `\n`，不解析）。行數規則與 brief 的結尾標記同一條：
//          以 `\n` 切開的段數；檔尾正好是換行時不多算那一段空行。讀不到 ⇒ 明說量不到，⛔ 不印 0。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Letters;

namespace SCP.Core.Cmd
{
    public static class SCP_ReadHint
    {
        /// <summary>量一個檔的行數與位元組數。讀不到回 false 並給原因。</summary>
        public static bool TryMeasure(string iPath, out int oLines, out long oBytes, out string? oError)
        {
            oLines = 0; oBytes = 0; oError = null;
            try
            {
                byte[] aBytes = File.ReadAllBytes(iPath);
                oBytes = aBytes.LongLength;
                if (aBytes.Length == 0) return true;
                int aNewlines = 0;
                foreach (byte b in aBytes) if (b == (byte)'\n') aNewlines++;
                oLines = aBytes[aBytes.Length - 1] == (byte)'\n' ? aNewlines : aNewlines + 1;
                return true;
            }
            catch (Exception e) { oError = e.GetType().Name + ": " + e.Message; return false; }
        }

        /// <summary>`（N 行／K KB）`；讀不到時說量不到。</summary>
        public static string SizeText(int iLines, long iBytes) =>
            "（" + iLines.ToString(CultureInfo.InvariantCulture) + " 行／"
            + (iBytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB）";

        /// <summary>
        /// 一個檔的提示：第一個元素接在路徑後面（大小），之後的元素各自成行（大檔時才有）。
        /// </summary>
        public static List<string> Describe(string iPath, SCP_WakeBriefSettings iSettings)
        {
            var aOut = new List<string>();
            if (!TryMeasure(iPath, out int aLines, out long aBytes, out string? aErr))
            {
                aOut.Add("（大小量不到：" + aErr + "）");
                return aOut;
            }
            aOut.Add(SizeText(aLines, aBytes));
            if (aLines > iSettings.BigFileLines || aBytes > iSettings.BigFileBytes)
                aOut.Add("⚠ 大檔（門檻 " + iSettings.BigFileLines.ToString(CultureInfo.InvariantCulture) + " 行／"
                         + iSettings.Get(SCP_WakeBriefSettings.KeyBigFileKb).ToString(CultureInfo.InvariantCulture)
                         + " KB）⇒ 用你的工具讀大檔的方法**分段讀到第 " + aLines.ToString(CultureInfo.InvariantCulture)
                         + " 行**；工具說截斷（truncated／PARTIAL／只顯示部分）就縮小範圍補讀那一段");
            return aOut;
        }

        /// <summary>
        /// 組好可以直接印的行：`<前綴><路徑>（N 行／K KB）`，大檔再多一行（縮排對齊前綴）。
        /// <paramref name="iDataRoot"/> 用來讀門檻設定；沒有就用預設門檻。
        /// </summary>
        public static List<string> Lines(string iPrefix, string iPath, string? iDataRoot)
        {
            List<string> aParts = Describe(iPath, SCP_WakeBriefSettings.ReadOrDefault(iDataRoot));
            var aOut = new List<string> { iPrefix + iPath + aParts[0] };
            string aIndent = new string(' ', LeadingSpaces(iPrefix) + 3);
            for (int i = 1; i < aParts.Count; i++) aOut.Add(aIndent + aParts[i]);
            return aOut;
        }

        static int LeadingSpaces(string s)
        {
            int n = 0;
            while (n < s.Length && s[n] == ' ') n++;
            return n;
        }
    }
}
