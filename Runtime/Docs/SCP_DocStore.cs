// 區塊職責：**文件的唯一讀取層** —— 列出、找名字、找「哪一份在講這支指令」、全文搜尋。
// 物理意義：文件住在指令所在那一邊（TASK-0337 ⑤）：Cmd 在 SCP_Core ⇒ `SCP_Core/Docs~/`；
//           在 Senate ⇒ Senate 專案 `Docs/`。本層**不知道任何一個根在哪** —— 根由宿主裝上
//           （<see cref="RootsProvider"/>，同 `SCP_CmdRegistry.InvocationHint` 的形狀：能力由宿主宣告，不由下層去找）。
// 數值影響：純讀檔，零寫入。
//
// ⚠ 三個「不會叫」的失效，本層都要讓它叫出來：
//   ① 宿主沒裝根 ⇒ 列表會是空的，而「沒有文件」與「沒接上」同形 ⇒ 回 error，⛔ 不回空清單。
//   ② 某個根不存在（路徑推錯、repo 沒 clone）⇒ 那一邊的文件整批消失而其餘照常 ⇒ 記進 problems。
//   ③ 兩邊有同名文件 ⇒ 「查名字」會安靜地挑一份 ⇒ 記進 problems，查名字時兩份都不給、明說撞名。
//      ⚠ 撞名只擋「查那個名字」的人（TASK-0999；Tim「文件庫整份組不起來 是否有更好的處理方法防呆」）：
//        problems 分種類（<see cref="SCP_DocProblem.Kind"/>）⇒ 呼叫端照自己用到的名字判斷要不要擋（SCP_SkillContent）；
//        撞名時用**全名**（根/相對路徑，去 .md；<see cref="QualifiedName"/>）指定要哪一份 —— 查名字收全名，撞名的錯誤列出每一份的全名＋路徑供挑。
//
// 📌 「這支指令對應哪份文件」寫在**文件自己的 frontmatter**（`cmds: [a, b]`），⛔ 不寫在 Cmd 的 C# 裡：
//   對應關係只能有一份，而文件是最常被改的那一邊 —— 改文件的人順手就改得到。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Letters;

namespace SCP.Core.Docs
{
    /// <summary>一個文件根：標籤（印給人看，說明文件住哪一邊）＋ 絕對路徑。</summary>
    public sealed class SCP_DocRoot
    {
        public readonly string Label;
        public readonly string Path;
        public SCP_DocRoot(string iLabel, string iPath) { Label = iLabel; Path = iPath; }
    }

    /// <summary>一份文件。<see cref="Name"/> ＝ 檔名去掉 `.md`，是查詢用的鍵。</summary>
    public sealed class SCP_DocEntry
    {
        public string Name = "";
        public string RootLabel = "";
        public string FullPath = "";
        /// <summary>相對於它所在的根（印給人看）。</summary>
        public string RelativePath = "";
        public string Title = "";
        public string Description = "";
        public List<string> Cmds = new List<string>();
    }

    /// <summary>文件庫的一個問題：哪一種、跟哪個名字有關（撞名 ⇒ 那個名字；根不存在 ⇒ 根的標籤）、印給人看的那一句。</summary>
    public sealed class SCP_DocProblem
    {
        public enum ProblemKind { RootMissing, Collision }
        public ProblemKind Kind;
        public string Name = "";
        public string Text = "";
        public override string ToString() => Text;
    }

    /// <summary>一筆搜尋命中。</summary>
    public sealed class SCP_DocHit
    {
        public SCP_DocEntry Doc = new SCP_DocEntry();
        public int LineNumber;
        public string Line = "";
    }

    public static class SCP_DocStore
    {
        /// <summary>
        /// 宿主裝上的文件根。⚠ null ＝ **這個宿主沒有裝** —— 查詢一律回 error，⛔ 不當成「沒有文件」。
        /// </summary>
        public static Func<IReadOnlyList<SCP_DocRoot>>? RootsProvider;

        /// <summary>
        /// 列出全部文件。
        /// <para>回 false ＝ 根本列不了（宿主沒裝根）；回 true 時 <paramref name="oProblems"/> 可能仍有內容（某個根不存在、撞名）——
        /// 呼叫端要把它印出來，⛔ 不准吞。</para>
        /// </summary>
        public static bool TryList(out List<SCP_DocEntry> oEntries, out List<string> oProblems, out string oError)
        {
            bool aOk = TryList(out oEntries, out List<SCP_DocProblem> aProblems, out oError);
            oProblems = aProblems.ConvertAll(p => p.Text);
            return aOk;
        }

        /// <summary>同上，問題分種類（撞名／根不存在）—— 呼叫端照自己用到的名字判斷要不要擋（TASK-0999）。</summary>
        public static bool TryList(out List<SCP_DocEntry> oEntries, out List<SCP_DocProblem> oProblems, out string oError)
        {
            oEntries = new List<SCP_DocEntry>();
            oProblems = new List<SCP_DocProblem>();
            oError = "";
            IReadOnlyList<SCP_DocRoot>? aRoots = RootsProvider?.Invoke();
            if (aRoots == null || aRoots.Count == 0)
            {
                oError = "這個宿主沒有裝上文件根（SCP_DocStore.RootsProvider）—— 查不到不代表沒有文件";
                return false;
            }

            int aExisting = 0;
            foreach (SCP_DocRoot aRoot in aRoots)
            {
                if (!Directory.Exists(aRoot.Path))
                {
                    oProblems.Add(new SCP_DocProblem { Kind = SCP_DocProblem.ProblemKind.RootMissing, Name = aRoot.Label, Text = $"文件根不存在：{aRoot.Label} → {aRoot.Path}（這一邊的文件整批沒列進來）" });
                    continue;
                }
                aExisting++;
                string[] aFiles = Directory.GetFiles(aRoot.Path, "*.md", SearchOption.AllDirectories);
                Array.Sort(aFiles, StringComparer.Ordinal);
                foreach (string aFile in aFiles)
                    oEntries.Add(Load(aRoot, aFile));
            }

            // 一個根都不存在 ⇒ 查不了，⛔ 不回「0 份文件」—— 那跟「真的沒有文件」同形。
            // （實測：dev build 的 exe 不在 repo 底下，根錨到 cwd，兩個根都不存在而清單回 exit 0。）
            if (aExisting == 0)
            {
                oError = "裝上的文件根一個都不存在：" + string.Join("；", oProblems.ConvertAll(p => p.Text));
                return false;
            }

            foreach (List<SCP_DocEntry> aSame in FindCollisions(oEntries))
                oProblems.Add(new SCP_DocProblem
                {
                    Kind = SCP_DocProblem.ProblemKind.Collision,
                    Name = aSame[0].Name,
                    Text = $"文件撞名：`{aSame[0].Name}` 有 {aSame.Count} 份 —— 查這個名字會被擋下；用全名指定（{string.Join("、", aSame.ConvertAll(QualifiedName))}），或改掉其中一份的檔名",
                });
            return true;
        }

        /// <summary>全名 ＝ 根的標籤/相對路徑，去掉 `.md`（例 `blockside/README`）—— 撞名時用它指定要哪一份（TASK-0999）。</summary>
        public static string QualifiedName(SCP_DocEntry iEntry)
        {
            string aRel = iEntry.RelativePath.EndsWith(".md", StringComparison.OrdinalIgnoreCase) ? iEntry.RelativePath.Substring(0, iEntry.RelativePath.Length - 3) : iEntry.RelativePath;
            return iEntry.RootLabel + "/" + aRel;
        }

        /// <summary>同名（不分大小寫）的文件，一組一份清單（照列出的順序）；沒有撞名 ⇒ 空。</summary>
        public static List<List<SCP_DocEntry>> FindCollisions(IEnumerable<SCP_DocEntry> iEntries)
        {
            var aByName = new Dictionary<string, List<SCP_DocEntry>>(StringComparer.OrdinalIgnoreCase);
            var aOrder = new List<string>();
            foreach (SCP_DocEntry aEntry in iEntries)
            {
                if (!aByName.TryGetValue(aEntry.Name, out List<SCP_DocEntry>? aList)) { aList = new List<SCP_DocEntry>(); aByName[aEntry.Name] = aList; aOrder.Add(aEntry.Name); }
                aList.Add(aEntry);
            }
            var aOut = new List<List<SCP_DocEntry>>();
            foreach (string aName in aOrder) if (aByName[aName].Count > 1) aOut.Add(aByName[aName]);
            return aOut;
        }

        /// <summary>
        /// 依名字找（不分大小寫）。撞名時回空清單以外的**全部**命中，由呼叫端判斷（⛔ 不替它挑一份）。
        /// <para>名字帶 `/` ⇒ 當成全名（<see cref="QualifiedName"/>；結尾的 `.md` 可寫可不寫）只比全名 —— 撞名時用它指定一份。</para>
        /// </summary>
        public static List<SCP_DocEntry> FindByName(IEnumerable<SCP_DocEntry> iEntries, string iName)
        {
            var aHits = new List<SCP_DocEntry>();
            string aName = iName.Replace('\\', '/').Trim();
            bool aQualified = aName.IndexOf('/') >= 0;
            if (aQualified && aName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) aName = aName.Substring(0, aName.Length - 3);
            foreach (SCP_DocEntry aEntry in iEntries)
                if (string.Equals(aQualified ? QualifiedName(aEntry) : aEntry.Name, aName, StringComparison.OrdinalIgnoreCase)) aHits.Add(aEntry);
            return aHits;
        }

        /// <summary>撞名時給人挑的那幾行：每一份一行「全名　（實際路徑）」。</summary>
        public static List<string> ChoiceLines(IEnumerable<SCP_DocEntry> iHits)
        {
            var aOut = new List<string>();
            foreach (SCP_DocEntry aEntry in iHits) aOut.Add($"{QualifiedName(aEntry)}　（{aEntry.FullPath.Replace('\\', '/')}）");
            return aOut;
        }

        /// <summary>frontmatter 的 `cmds` 裡有這支指令的文件。</summary>
        public static List<SCP_DocEntry> FindByCmd(IEnumerable<SCP_DocEntry> iEntries, string iCmdName)
        {
            var aHits = new List<SCP_DocEntry>();
            foreach (SCP_DocEntry aEntry in iEntries)
                foreach (string aCmd in aEntry.Cmds)
                    if (string.Equals(aCmd, iCmdName, StringComparison.OrdinalIgnoreCase)) { aHits.Add(aEntry); break; }
            return aHits;
        }

        /// <summary>全文搜尋（不分大小寫、逐行）。<paramref name="oTotal"/> 是全部命中數，回傳的清單最多 <paramref name="iLimit"/> 筆。</summary>
        public static List<SCP_DocHit> Search(IEnumerable<SCP_DocEntry> iEntries, string iKeyword, int iLimit, out int oTotal)
        {
            var aHits = new List<SCP_DocHit>();
            oTotal = 0;
            foreach (SCP_DocEntry aEntry in iEntries)
            {
                string[] aLines = SCP_LetterText.NormalizeNewlines(File.ReadAllText(aEntry.FullPath)).Split('\n');
                for (int i = 0; i < aLines.Length; i++)
                {
                    if (aLines[i].IndexOf(iKeyword, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    oTotal++;
                    if (aHits.Count < iLimit)
                        aHits.Add(new SCP_DocHit { Doc = aEntry, LineNumber = i + 1, Line = aLines[i].Trim() });
                }
            }
            return aHits;
        }

        /// <summary>讀全文（剝掉 frontmatter —— 那些欄位已經印在標頭了）。</summary>
        public static string ReadBody(SCP_DocEntry iEntry)
            => SCP_LetterText.StripFrontmatter(File.ReadAllText(iEntry.FullPath));

        static SCP_DocEntry Load(SCP_DocRoot iRoot, string iFile)
        {
            var aEntry = new SCP_DocEntry
            {
                Name = System.IO.Path.GetFileNameWithoutExtension(iFile),
                RootLabel = iRoot.Label,
                FullPath = iFile,
                RelativePath = iFile.Substring(iRoot.Path.Length).TrimStart('/', '\\').Replace('\\', '/'),
            };
            Dictionary<string, string> aFields = ParseFrontmatter(File.ReadAllText(iFile));
            if (aFields.TryGetValue("title", out string? aTitle)) aEntry.Title = aTitle;
            if (aFields.TryGetValue("description", out string? aDesc)) aEntry.Description = aDesc;
            if (aFields.TryGetValue("cmds", out string? aCmds)) aEntry.Cmds = ParseList(aCmds);
            return aEntry;
        }

        /// <summary>
        /// 最小的 frontmatter 解析：只認開頭那一層 `---` 之間的 `key: value` 單行欄位。
        /// ⚠ 多行值（YAML 區塊清單）不認 —— 本層只需要 title／description／cmds 三個單行欄位。
        /// </summary>
        static Dictionary<string, string> ParseFrontmatter(string iText)
        {
            var aFields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string[] aLines = SCP_LetterText.NormalizeNewlines(iText).Split('\n');
            if (aLines.Length == 0 || aLines[0].Trim() != "---") return aFields;
            for (int i = 1; i < aLines.Length; i++)
            {
                string aLine = aLines[i];
                if (aLine.Trim() == "---") break;
                int aColon = aLine.IndexOf(':');
                if (aColon <= 0 || char.IsWhiteSpace(aLine[0])) continue;
                aFields[aLine.Substring(0, aColon).Trim()] = aLine.Substring(aColon + 1).Trim();
            }
            return aFields;
        }

        /// <summary>`[a, b]` 或 `a, b` ⇒ 清單。</summary>
        static List<string> ParseList(string iValue)
        {
            var aOut = new List<string>();
            foreach (string aPart in iValue.Trim().TrimStart('[').TrimEnd(']').Split(','))
            {
                string aItem = aPart.Trim().Trim('"', '\'');
                if (aItem.Length > 0) aOut.Add(aItem);
            }
            return aOut;
        }
    }
}
