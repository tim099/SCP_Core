// 區塊職責：讀 `AgentEntry/AgentTemplateManifest.json` —— 每個 target 的入口檔裝哪、用哪種模式。
// 物理意義：**來源與目的地只由 manifest 定義**，程式碼裡不寫死任何一組對應。
//           `mode` 是 v2 才有的欄位（Tim 2026-08-30 拍板）：
//             · `append` —— 目的地是**使用者的檔**（CLAUDE.md / AGENTS.md），只維護受管區塊
//             · `full`   —— 目的地是**我們自己的檔**（.agents/rules/…），整檔覆寫
//           ⚠ 缺 `mode` 一律視為 `full`：舊 manifest 讀進來行為不變。
//           📌 為什麼 antigravity 是 full：那個檔是為本機制生出來的、不會有使用者內容 ⇒
//             append 保護的是一個空集合，卻要為它多七種狀態，而那些狀態在沒人手改的檔上永遠是噪音。
// 數值影響：一次讀檔 ＋ 一次 parse。純讀。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Entry
{
    /// <summary>入口檔的維護模式。</summary>
    public enum SCP_EntryMode
    {
        /// <summary>整檔覆寫（目的地是我們自己的檔）。</summary>
        Full = 0,

        /// <summary>只維護受管區塊（目的地是使用者的檔）。</summary>
        Append = 1,
    }

    public sealed class SCP_EntrySpec
    {
        public string Target { get; internal set; } = "";
        public SCP_EntryMode Mode { get; internal set; }

        /// <summary>來源檔（相對 SCP_Core 根）—— append 模式是 fragment，full 模式是整份 template。</summary>
        public string SourceRelative { get; internal set; } = "";

        /// <summary>目的地（相對專案根）。</summary>
        public string Destination { get; internal set; } = "";

        public string SourcePath(string iCoreRoot) => iCoreRoot.Replace('\\', '/').TrimEnd('/') + "/" + SourceRelative;
        public string DestinationPath(string iProjectRoot) => iProjectRoot.Replace('\\', '/').TrimEnd('/') + "/" + Destination;
    }

    public sealed class SCP_EntryManifest
    {
        public List<SCP_EntrySpec> Entries { get; } = new List<SCP_EntrySpec>();

        /// <summary>讀不出來的原因（空 ＝ 沒問題）。⚠ 空 manifest 與讀失敗**不得同形**。</summary>
        public List<string> Problems { get; } = new List<string>();

        /// <summary>manifest 檔真的存在且解析成功。</summary>
        public bool Loaded { get; internal set; }

        public const string RelativePath = "AgentEntry/AgentTemplateManifest.json";

        /// <summary>讀 SCP_Core 根底下的 manifest。</summary>
        public static SCP_EntryManifest Load(string iCoreRoot)
        {
            var aOut = new SCP_EntryManifest();
            string aPath = iCoreRoot.Replace('\\', '/').TrimEnd('/') + "/" + RelativePath;
            if (!File.Exists(aPath))
            {
                aOut.Problems.Add($"找不到 manifest：{aPath}（**這不是「沒有 target」**，是讀不到）");
                return aOut;
            }

            SCP_JsonData aRoot;
            try { aRoot = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8)); }
            catch (Exception e)
            {
                aOut.Problems.Add($"manifest 壞了（沒有套用任何一筆）：{e.Message}");
                return aOut;
            }
            aOut.Loaded = true;

            if (!aRoot.Contains("entries"))
            {
                aOut.Problems.Add("manifest 裡沒有 `entries` —— 是鍵名寫錯還是真的空的？兩者不同形，這裡當錯誤看");
                return aOut;
            }

            SCP_JsonData aEntries = aRoot["entries"];
            for (int i = 0; i < aEntries.Count; i++)
            {
                SCP_JsonData e = aEntries[i];
                string aTarget = e.GetString("target", "");
                string aDest = e.GetString("destination", "");
                string aModeRaw = e.GetString("mode", "full");

                if (aTarget.Length == 0 || aDest.Length == 0)
                {
                    aOut.Problems.Add($"第 {i} 筆缺 target 或 destination —— 跳過（不猜）");
                    continue;
                }

                SCP_EntryMode aMode = string.Equals(aModeRaw, "append", StringComparison.OrdinalIgnoreCase)
                    ? SCP_EntryMode.Append : SCP_EntryMode.Full;

                // append 讀 fragment、full 讀 template。⚠ 拿錯欄位的症狀是「裝了一整份文件進去」，
                //   而那在畫面上看起來只是「區塊比較長」。所以缺欄位一律報錯不退回另一個。
                string aSrc = aMode == SCP_EntryMode.Append ? e.GetString("fragment", "") : e.GetString("template", "");
                if (aSrc.Length == 0)
                {
                    aOut.Problems.Add($"`{aTarget}` 是 {aModeRaw} 模式，但缺 "
                                      + (aMode == SCP_EntryMode.Append ? "`fragment`" : "`template`") + " —— 跳過");
                    continue;
                }

                aOut.Entries.Add(new SCP_EntrySpec
                {
                    Target = aTarget,
                    Mode = aMode,
                    SourceRelative = aSrc,
                    Destination = aDest,
                });
            }
            return aOut;
        }

        public SCP_EntrySpec? ForTarget(string iTarget)
        {
            foreach (SCP_EntrySpec s in Entries)
                if (string.Equals(s.Target, iTarget, StringComparison.OrdinalIgnoreCase)) return s;
            return null;
        }

        /// <summary>
        /// 讀來源內容並代入 <c>{{SCP_CORE_PATH}}</c>（消費端專案看到的 SCP_Core 相對路徑）。
        /// <para>⚠ 兩個宿主算出來的相對路徑必須一致，否則會**互相覆寫來回跳**，
        /// 每次同步都產生一筆 diff 而兩邊都自認正確。⇒ 這裡只吃呼叫端算好的值，不自己算。</para>
        /// </summary>
        public static string ReadSource(SCP_EntrySpec iSpec, string iCoreRoot, string iCoreRelativeToProject)
        {
            string aText = File.ReadAllText(iSpec.SourcePath(iCoreRoot), Encoding.UTF8);
            return SCP_EntryDoc.Normalize(aText).Replace("{{SCP_CORE_PATH}}", iCoreRelativeToProject);
        }
    }
}
