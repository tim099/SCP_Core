// 區塊職責：**「有哪些 skill」的唯一判準** —— 枚舉 `Skills~/` 底下算數的目錄。
// 物理意義：這個判準看起來很小，但它被三處消費（清單／安裝／孤兒偵測）。
//           🩸 UCL 那頁原本有**三份各自略有差異的副本**，而那正是「頁面說已同步、實際沒裝」
//           這類靜默不一致的來源 —— 後來才收斂成一支。這裡從第一天就只有一支。
//           規則與 python `install_skills.py` 的 `discover_skills()` 逐條對齊：
//             跳過 `_` 前綴、`~` 結尾、`.` 前綴，且**目錄內必須有 SKILL.md**。
//           ⚠ 最後那一條最容易漏：源端掉了 SKILL.md，安裝端會被當成孤兒掃掉 ——
//             兩邊要看到同一件事，否則一邊在裝、一邊在刪。
// 數值影響：一次列目錄 ＋ 每個目錄一次 File.Exists。純讀。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace SCP.Core.Skills
{
    public static class SCP_SkillSource
    {
        /// <summary>一個 skill 目錄要算數，裡面必須有這個檔。</summary>
        public const string SkillFileName = "SKILL.md";

        /// <summary>安裝端的來源標記檔名 —— 「這個目錄是本工具裝的」的唯一憑證。</summary>
        public const string MarkerFileName = ".scp_source";

        /// <summary>
        /// 列出 <paramref name="iSkillsRoot"/> 底下算數的 skill 名（排序、去重）。
        /// <para>目錄不存在回空清單 —— 那是「還沒有」不是錯誤。
        /// ⚠ 但呼叫端要能分辨「根不存在」與「根在但一個都沒有」，所以另有 <see cref="RootExists"/>。</para>
        /// </summary>
        public static List<string> Discover(string? iSkillsRoot)
        {
            var aList = new List<string>();
            if (string.IsNullOrEmpty(iSkillsRoot) || !Directory.Exists(iSkillsRoot)) return aList;

            string[] aDirs;
            try { aDirs = Directory.GetDirectories(iSkillsRoot!); }
            catch { return aList; }

            foreach (string aDir in aDirs)
            {
                string aName = Path.GetFileName(aDir);
                if (!IsSkillName(aName)) continue;
                if (!File.Exists(Path.Combine(aDir, SkillFileName))) continue;
                aList.Add(aName);
            }
            aList.Sort(StringComparer.OrdinalIgnoreCase);
            return aList;
        }

        public static bool RootExists(string? iSkillsRoot)
            => !string.IsNullOrEmpty(iSkillsRoot) && Directory.Exists(iSkillsRoot);

        /// <summary>名字本身合不合格（不看內容）。與 python 端逐條對齊。</summary>
        public static bool IsSkillName(string? iName)
        {
            if (string.IsNullOrEmpty(iName)) return false;
            string n = iName!;
            if (n.StartsWith("_", StringComparison.Ordinal)) return false;
            if (n.StartsWith(".", StringComparison.Ordinal)) return false;
            if (n.EndsWith("~", StringComparison.Ordinal)) return false;
            return true;
        }
    }
}
