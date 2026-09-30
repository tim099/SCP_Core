// 區塊職責：**消費時間**的清單與擲骰（TASK-0333，移植自 `spend_menu.py`；Tim 2026-09-30：python 入口完全廢除）。
// 物理意義（為什麼有這個東西）：這個經濟體不缺花錢的地方，缺的是花錢的主體性 ——
//          gura 2026-08-01 掃 ledger：agent 主動消費只佔出帳 2.8%，而且掛零 33 天。
//          解法跟自由時間同一形：掛在必經節點（晚安可選一步）＋ 降低選擇成本（骰子）＋ 給誘因（折扣）。
// 設計取捨：
//   · **骰面只列有可執行工具的通道**（gura 硬要求）：清單來源是 md 檔本身，每個 md 的指令要實測過才寫進去。
//   · 兩層清單：共用層 `SCP_Core/Docs~/Spending/Items`（文件住在指令所在那一邊，TASK-0337 的規則）＋
//     專案層 `<資料根>/Spending/Items`。同 id 專案層覆蓋（含 `enabled: false` 跨層停用）⇒ **enabled 過濾在合併之後**。
//     ⚠ 位置跟 py 版不同：py 版共用層在 UCL_Core `Docs~/zh-Hant/Spending/Items`、專案層在 `<repo>/docs/Spending/Items`。
//       Senate 沒有 UCL_Core 路徑解析器（也不該再造一套）；共用層跟著指令搬、專案層錨在確定知道的資料根。
//   · 折扣按**骰出位置**遞減 50／20／10%（Tim 2026-08-01），第 4 項起原價；**不自動退**，走請款單（Tim 拍板）。
//   · 額度上限 ＝ 當前餘額 × 10%，向下取整（跟保管費同一個基數）。
// 數值影響：**本檔不動任何錢** —— 只擲清單、算額度。花錢由各通道自己的指令負責，退費走請款單。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SCP.Core.Letters
{
    public sealed class SCP_SpendItem
    {
        public string Id = "";
        public string Name = "";
        public string Kind = "";
        public string UnitCost = "";
        public bool Enabled = true;
        public string Body = "";
        public string Path = "";
        /// <summary>shared ／ project</summary>
        public string Layer = "";
    }

    public static class SCP_SpendMenu
    {
        /// <summary>index 0 ＝ 骰出清單的第 1 項。超出長度的項目無折扣。</summary>
        public static readonly double[] DiscountLadder = { 0.50, 0.20, 0.10 };
        public const double SpendCapRate = 0.10;
        public const int DefaultRollCount = 3;
        public const string SharedSubdir = "Spending/Items";

        public static string ProjectItemsDir(string iDataRoot) => System.IO.Path.Combine(iDataRoot, "Spending", "Items").Replace('\\', '/');

        public static double DiscountAt(int iIndex) => iIndex >= 0 && iIndex < DiscountLadder.Length ? DiscountLadder[iIndex] : 0.0;

        /// <summary>額度上限（向下取整）。餘額不知道 ⇒ -1（⛔ 不回 0：0 是「查到了沒錢」）。</summary>
        public static long CapOf(long iBalance) => iBalance < 0 ? -1 : (long)Math.Floor(iBalance * SpendCapRate);

        /// <summary>雙層合併後、過濾停用的可用清單（依 id 排序）。</summary>
        public static List<SCP_SpendItem> Load(string? iSharedDir, string iProjectDir, out int oProjectCount, List<string> ioWarnings)
        {
            var aShared = ScanDir(iSharedDir, "shared", ioWarnings);
            var aProject = ScanDir(iProjectDir, "project", ioWarnings);
            var aMerged = new Dictionary<string, SCP_SpendItem>(aShared, StringComparer.Ordinal);
            foreach (var kv in aProject) aMerged[kv.Key] = kv.Value;
            var aItems = aMerged.Values.Where(i => i.Enabled).OrderBy(i => i.Id, StringComparer.Ordinal).ToList();
            oProjectCount = aItems.Count(i => i.Layer == "project");
            return aItems;
        }

        /// <summary>不放回地抽 <paramref name="iCount"/> 項（順序即折扣位置）。</summary>
        public static List<SCP_SpendItem> Roll(List<SCP_SpendItem> iItems, int iCount, Random iRng)
        {
            var aPool = new List<SCP_SpendItem>(iItems);
            var aOut = new List<SCP_SpendItem>();
            int n = Math.Min(Math.Max(iCount, 0), aPool.Count);
            for (int i = 0; i < n; i++)
            {
                int k = iRng.Next(aPool.Count);
                aOut.Add(aPool[k]);
                aPool.RemoveAt(k);
            }
            return aOut;
        }

        /// <summary>掃一個資料夾；`_` 開頭是說明檔不算項目；壞檔跳過（記 warning）不炸整份清單。</summary>
        static Dictionary<string, SCP_SpendItem> ScanDir(string? iDir, string iLayer, List<string> ioWarnings)
        {
            var aOut = new Dictionary<string, SCP_SpendItem>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(iDir) || !Directory.Exists(iDir)) return aOut;
            string[] aFiles = Directory.GetFiles(iDir, "*.md");
            Array.Sort(aFiles, StringComparer.Ordinal);
            foreach (string f in aFiles)
            {
                string aStem = System.IO.Path.GetFileNameWithoutExtension(f);
                if (aStem.StartsWith("_", StringComparison.Ordinal)) continue;
                try
                {
                    var (aMeta, aBody) = ParseFrontmatter(File.ReadAllText(f, Encoding.UTF8));
                    string aId = aMeta.TryGetValue("id", out string? v) && v.Length > 0 ? v : aStem;
                    aOut[aId] = new SCP_SpendItem
                    {
                        Id = aId,
                        Name = aMeta.TryGetValue("name", out string? n) && n.Length > 0 ? n : aStem,
                        Kind = aMeta.TryGetValue("kind", out string? k) ? k : "",
                        UnitCost = aMeta.TryGetValue("unit_cost", out string? u) ? u : "",
                        Enabled = !(aMeta.TryGetValue("enabled", out string? e) && e.Trim().Equals("false", StringComparison.OrdinalIgnoreCase)),
                        Body = aBody.Trim(),
                        Path = f.Replace('\\', '/'),
                        Layer = iLayer,
                    };
                }
                catch (Exception ex) { ioWarnings.Add($"消費項目 md 讀取失敗，跳過：{System.IO.Path.GetFileName(f)}（{ex.Message}）"); }
            }
            return aOut;
        }

        static (Dictionary<string, string> Meta, string Body) ParseFrontmatter(string iText)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            string t = iText.TrimStart('﻿').TrimStart();
            if (!t.StartsWith("---", StringComparison.Ordinal)) return (d, iText);
            int aEnd = t.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (aEnd < 0) return (d, iText);
            foreach (string raw in t.Substring(3, aEnd - 3).Split('\n'))
            {
                int c = raw.IndexOf(':');
                if (c <= 0) continue;
                string key = raw.Substring(0, c).Trim();
                if (key.Length > 0) d[key] = raw.Substring(c + 1).Trim();
            }
            string aBody = t.Substring(aEnd + 4).TrimStart('\r', '\n');
            return (d, aBody);
        }
    }
}
