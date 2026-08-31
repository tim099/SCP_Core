// 區塊職責：**見根** —— 關鍵記憶碎片（fragments/）的讀取、排序與索引生成。
// 物理意義：索引是**視圖不是真相源**。事實永遠在 fragment 檔自己的 frontmatter 裡，
//           `_root_index.md` 隨時可以整份重建 ⇒ 零漂移、可 diff 驗證。
//           ⇒ 所以本層只做「掃 frontmatter → 排序 → 渲染」，一個字都不從索引檔讀回來。
// 數值影響：只 parse frontmatter、不讀正文 ⇒ 成本 O(檔數) 且極輕。寫入是整份覆寫。
//
// ⚠ **與 python memory.render_root_index 逐字同形**（含表格欄位、警語措辭、顯示上限 12）。
//   兩端目前並存，而索引是整份覆寫 ⇒ 形狀分岔的症狀是**每次換工具跑就整份翻動**，
//   git diff 看起來像有人改了記憶，實際上只是換了一支工具。
//
// 📌 已知限制（basecamp 2026-08-16 點出，兩端都沒修）：`howto` 型碎片 recurrence 天生 ＝ 1，
//   **出生就在顯示線以下** ⇒ 見根索引不是它的回流路徑，它要走執行入口（recall）。
//   這一行留著，是為了讓下一個人知道「它沒出現在索引裡」是預期而不是壞了。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    /// <summary>一個 fragment 的 frontmatter 投影（只有索引需要的欄位 ＋ 原始鍵值）。</summary>
    public sealed class SCP_Fragment
    {
        /// <summary>frontmatter 的原始鍵值（未知欄位也留著 —— 這一層不決定誰有用）。</summary>
        public Dictionary<string, string> Fields = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>檔名（不含副檔名）。frontmatter 沒有 id 時就用它。</summary>
        public string Id = "";

        public string Path = "";

        /// <summary>frontmatter 裡 `- { by: ...` 的筆數 —— 沒有 recurrence 欄時的替代讀數。</summary>
        public int OriginCount;

        public string Get(string iKey, string iDefault = "")
            => Fields.TryGetValue(iKey, out string aValue) ? aValue : iDefault;

        public bool Has(string iKey) => Fields.ContainsKey(iKey);

        /// <summary>踩過次數。⚠ 有 recurrence 欄就用它（即使值怪），沒有才退回 OriginCount。</summary>
        public string RecurrenceText => Has("recurrence") ? Get("recurrence") : OriginCount.ToString();

        public int RecurrenceValue
        {
            get
            {
                string aRaw = RecurrenceText;
                if (int.TryParse(aRaw, out int aNum)) return aNum;
                // 解析不出來就當 1 —— 跟 python 的 except 分支同形。
                return 1;
            }
        }

        /// <summary>
        /// 分組用的型別。
        /// <para>🩸 python 那側註解記著：用 <c>type</c> 的話每筆都是 "fragment" ⇒ 排序恆為 99，
        /// 「type 群組」那一層**從來沒生效過**，而且不會報錯。所以 <c>fragment_type</c> 優先。</para>
        /// </summary>
        public string TypeName => Has("fragment_type") ? Get("fragment_type") : Get("type");
    }

    public static class SCP_Fragments
    {
        /// <summary>見根索引「必讀」的顯示上限；其餘**明說隱藏筆數**（禁靜默截斷）。</summary>
        public const int RootIndexShowLimit = 12;

        /// <summary>
        /// 型別排序，同時是「這個系統認得哪幾種記憶」的宣告。
        /// <para>順序與 python <c>FRAG_TYPE_ORDER</c> 一致 —— 兩邊不同會讓同分的列互換位置，
        /// 而那在 diff 上長得像有人改了內容。</para>
        /// </summary>
        public static readonly string[] TypeOrder =
            { "lesson", "unsolved", "relation", "identity", "philosophy", "howto" };

        static readonly Regex s_Frontmatter = new Regex(@"^---\r?\n(.*?)\r?\n---", RegexOptions.Singleline);
        static readonly Regex s_Field = new Regex(@"^(\w+):\s*(.*)$");
        static readonly Regex s_Origin = new Regex(@"^\s*-\s*\{\s*by:", RegexOptions.Multiline);

        /// <summary>讀單一 fragment 的 frontmatter。讀不到／沒有 frontmatter ⇒ null（不是空物件）。</summary>
        public static SCP_Fragment? Parse(string iPath)
        {
            string aText;
            try { aText = File.ReadAllText(iPath); }
            catch (Exception) { return null; }

            Match aMatch = s_Frontmatter.Match(aText);
            if (!aMatch.Success) return null;

            var aFrag = new SCP_Fragment
            {
                Path = iPath,
                Id = System.IO.Path.GetFileNameWithoutExtension(iPath),
            };
            string aBlock = aMatch.Groups[1].Value;
            foreach (string aRaw in aBlock.Split('\n'))
            {
                Match aField = s_Field.Match(aRaw.TrimEnd('\r'));
                if (aField.Success) aFrag.Fields[aField.Groups[1].Value] = aField.Groups[2].Value.Trim();
            }
            aFrag.OriginCount = s_Origin.Matches(aBlock).Count;
            if (aFrag.Has("id") && aFrag.Get("id").Length > 0) aFrag.Id = aFrag.Get("id");
            return aFrag;
        }

        /// <summary>
        /// 列該 persona 全部 fragment。
        /// <para>⚠ 跳過底線開頭的檔：那些是**產物**（`_root_index.md`），
        /// 不跳的話索引每重建一次就把自己多算一筆，而數字會一路合理地長大。</para>
        /// </summary>
        public static List<SCP_Fragment> Load(string iLettersRoot, string iPersona)
        {
            var aOut = new List<SCP_Fragment>();
            string aDir = SCP_LettersPaths.FragmentsDir(new SCP_LettersRoot(iLettersRoot), iPersona);
            if (!Directory.Exists(aDir)) return aOut;

            var aFiles = new List<string>(Directory.GetFiles(aDir, "*.md"));
            aFiles.Sort(StringComparer.Ordinal);
            foreach (string aFile in aFiles)
            {
                if (System.IO.Path.GetFileName(aFile).StartsWith("_", StringComparison.Ordinal)) continue;
                SCP_Fragment? aFrag = Parse(aFile);
                if (aFrag != null) aOut.Add(aFrag);
            }
            return aOut;
        }

        /// <summary>排序：踩過次數降冪 → type 群組 → id（穩定）。次數本身就是資訊。</summary>
        public static void SortForIndex(List<SCP_Fragment> ioFrags)
        {
            ioFrags.Sort((a, b) =>
            {
                int aCmp = b.RecurrenceValue.CompareTo(a.RecurrenceValue);   // 降冪
                if (aCmp != 0) return aCmp;
                aCmp = TypeIndex(a).CompareTo(TypeIndex(b));
                if (aCmp != 0) return aCmp;
                return string.CompareOrdinal(a.Id, b.Id);
            });
        }

        static int TypeIndex(SCP_Fragment iFrag)
        {
            string aType = iFrag.TypeName;
            for (int i = 0; i < TypeOrder.Length; i++)
                if (string.Equals(TypeOrder[i], aType, StringComparison.Ordinal)) return i;
            return 99;
        }

        /// <summary>見根索引全文（純機械生成）。</summary>
        public static string RenderRootIndex(string iLettersRoot, string iPersona,
                                             int iShowLimit = RootIndexShowLimit)
        {
            List<SCP_Fragment> aFrags = Load(iLettersRoot, iPersona);
            var aOpen = new List<SCP_Fragment>();
            var aInternalized = new List<SCP_Fragment>();
            int aShared = 0;
            foreach (SCP_Fragment aFrag in aFrags)
            {
                string aStatus = aFrag.Get("status");
                if (aStatus == "open") aOpen.Add(aFrag);
                else if (aStatus == "internalized") aInternalized.Add(aFrag);
                if (aFrag.Get("visibility") == "shared") aShared++;
            }
            SortForIndex(aOpen);
            SortForIndex(aInternalized);
            int aHidden = Math.Max(0, aOpen.Count - iShowLimit);

            var L = new List<string>
            {
                "---", "type: root_index", "persona: " + iPersona,
                "generated: mechanical   # 掃 fragments/ frontmatter 產生 — 手改會被下次生成覆寫",
                "fragment_total: " + aFrags.Count, "---", "",
                "# 🌱 見根 — " + iPersona + " 必讀關鍵記憶索引", "",
                "> 機械生成 → 零漂移、可隨時重建、可 diff 驗證。事實來源永遠是 fragment 檔本身；",
                "> 見根/樹/叢/林/森都只是視圖。排序＝踩過次數降冪。closed 不列但不刪檔。", "",
                "## 必讀（status: open，" + aOpen.Count + " 筆）", "",
                "| 次數 | 類型 | 關鍵記憶 | 涉及層 | 檔案 |", "|---|---|---|---|---|",
            };
            for (int i = 0; i < aOpen.Count && i < iShowLimit; i++)
            {
                SCP_Fragment f = aOpen[i];
                string aType = f.TypeName.Length > 0 ? f.TypeName : "?";
                string aTitle = f.Has("title") ? f.Get("title") : f.Id;
                string aLayers = f.Get("layers").Length > 0 ? f.Get("layers") : "—";
                L.Add("| **" + f.RecurrenceText + "** | " + aType + " | " + aTitle + " | "
                      + aLayers + " | [" + f.Id + "](" + f.Id + ".md) |");
            }
            // 截斷要**說出筆數** —— 「收錄 N 筆」旁邊一定要有「未收錄 N 筆」。
            if (aHidden > 0)
                L.AddRange(new[] { "", "⚠ **另有 " + aHidden + " 筆 open 未顯示**（顯示上限 "
                                       + iShowLimit + "）— 全清單見本目錄。" });

            L.AddRange(new[] { "", "## 已內化（status: internalized，取踩過次數最多的 3 筆）", "" });
            for (int i = 0; i < aInternalized.Count && i < 3; i++)
            {
                SCP_Fragment f = aInternalized[i];
                string aTitle = f.Has("title") ? f.Get("title") : f.Id;
                L.Add("- ✅ " + aTitle + "（踩過 " + f.RecurrenceText + " 次）→ ["
                      + f.Id + "](" + f.Id + ".md)");
            }
            if (aInternalized.Count > 3)
                L.Add("- …另有 " + (aInternalized.Count - 3) + " 筆已內化（不列，避免洗版；見本目錄）");

            L.AddRange(new[]
            {
                "", "## 共享狀態", "",
                "- shared（可被其他 persona / 外部 reference）：" + aShared + " 筆",
                "- private：" + (aFrags.Count - aShared) + " 筆",
            });

            // ⚠ 行尾用平台預設 —— python 那端是 `write_text()`（文字模式），
            //   在 Windows 生 CRLF、在 Linux 生 LF。索引是**整份覆寫**，
            //   兩端行尾不一致的症狀是「換一支工具跑就整份翻動」，diff 上長得像有人改了記憶。
            return string.Join(Environment.NewLine, L) + Environment.NewLine;
        }

        /// <summary>
        /// 生成／覆寫見根索引。**無 fragment 時不建檔**（回 null）——
        /// 一份「0 筆」的索引跟「這個人還沒開始留碎片」長得一樣，而後者不該長出檔案。
        /// </summary>
        public static string? WriteRootIndex(string iLettersRoot, string iPersona,
                                             int iShowLimit = RootIndexShowLimit)
        {
            if (Load(iLettersRoot, iPersona).Count == 0) return null;
            string aPath = SCP_LettersPaths.RootIndexPath(new SCP_LettersRoot(iLettersRoot), iPersona);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(aPath)!);
            File.WriteAllText(aPath, RenderRootIndex(iLettersRoot, iPersona, iShowLimit),
                              new UTF8Encoding(false));
            return aPath;
        }
    }
}
