// 區塊職責：**把一個 skill 的內容組出來** —— 讀源檔的 `docs:` 清單，逐份向文件庫（SCP_DocStore）現讀、合併。
// 物理意義：TASK-0406：skill 本身只有入口，內容在**查詢當下**才從文件組出來 ⇒ 改文件不必重裝 skill、不必重 build，
//           而兩邊（Senate 的 scp-*、UCL 的 ucl-* 入口）跑同一行指令讀到的是同一份。
//           路徑一律交給文件庫解析（根錨在宿主 repo）—— 呼叫端只給 skill 名。
// 數值影響：純讀檔。
// ⚠ **缺一塊就整份失敗**：宣告的文件不存在／撞名／章節找不到 ⇒ 回錯誤清單、**一個字的內容都不給**。
//   印出「缺一塊的內容」當成功，是這支最不能有的失敗形狀 —— agent 會照著那份不完整的說明做事，而沒有一層會喊。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Docs;

namespace SCP.Core.Skills
{
    public sealed class SCP_SkillPart
    {
        public SCP_SkillDocRef Ref { get; internal set; } = new SCP_SkillDocRef("", "");
        public SCP_DocEntry Doc { get; internal set; } = new SCP_DocEntry();
        public string Body { get; internal set; } = "";
    }

    public static class SCP_SkillContent
    {
        /// <summary>
        /// 組出 skill 的全部片段。成功 ⇒ <paramref name="oParts"/> 依宣告順序、<paramref name="oErrors"/> 空；
        /// 任何一格失敗 ⇒ <paramref name="oParts"/> **清空**、錯誤逐條列在 <paramref name="oErrors"/>。
        /// </summary>
        public static bool TryCompose(string iSkillsRoot, string iSkill, out List<SCP_SkillPart> oParts, out List<string> oErrors)
        {
            oParts = new List<SCP_SkillPart>();
            oErrors = new List<string>();
            string aFile = Path.Combine(iSkillsRoot, iSkill, SCP_SkillSource.SkillFileName);
            if (!File.Exists(aFile)) { oErrors.Add($"沒有這個 skill：`{iSkill}`（找的是 {aFile}）"); return false; }

            List<SCP_SkillDocRef>? aRefs = SCP_SkillEntry.ParseDocs(File.ReadAllText(aFile, Encoding.UTF8));
            if (aRefs == null) { oErrors.Add($"`{iSkill}` 沒有宣告 `docs:` —— 它是鏡像模式的舊形狀，內容就在安裝出去的 SKILL.md 裡"); return false; }
            if (aRefs.Count == 0) { oErrors.Add($"`{iSkill}` 的 `docs:` 是空的 —— 宣告了零份文件"); return false; }

            if (!SCP_DocStore.TryList(out List<SCP_DocEntry> aEntries, out List<string> aProblems, out string aListError))
            {
                oErrors.Add("文件庫查不了：" + aListError);
                return false;
            }
            foreach (string p in aProblems) oErrors.Add("文件庫有問題（清單不完整）：" + p);

            foreach (SCP_SkillDocRef r in aRefs)
            {
                List<SCP_DocEntry> aHits = SCP_DocStore.FindByName(aEntries, r.Doc);
                if (aHits.Count == 0) { oErrors.Add($"`{r}`：沒有名叫 `{r.Doc}` 的文件"); continue; }
                if (aHits.Count > 1) { oErrors.Add($"`{r}`：`{r.Doc}` 撞名 {aHits.Count} 份 —— 不替你挑"); continue; }
                string aBody = SCP_DocStore.ReadBody(aHits[0]).Replace("\r\n", "\n");
                if (r.Section.Length > 0)
                {
                    string? aSec = ExtractSection(aBody, r.Section, out int aMatches);
                    if (aSec == null)
                    {
                        oErrors.Add(aMatches == 0 ? $"`{r}`：`{r.Doc}` 裡沒有以「{r.Section}」開頭的標題"
                                                  : $"`{r}`：`{r.Doc}` 裡有 {aMatches} 個標題以「{r.Section}」開頭 —— 不替你挑，請寫長一點");
                        continue;
                    }
                    aBody = aSec;
                }
                oParts.Add(new SCP_SkillPart { Ref = r, Doc = aHits[0], Body = aBody.Trim('\n') });
            }
            if (oErrors.Count > 0) { oParts.Clear(); return false; }
            return true;
        }

        /// <summary>
        /// 取一節：標題文字（去掉 `#`）以 <paramref name="iPrefix"/> 開頭的那一個，到下一個同級或更高級標題為止。
        /// ⚠ 程式碼區塊（```）裡的 `#` 是註解不是標題 —— 不跳過的話 bash 範例會把一節切斷。
        /// </summary>
        public static string? ExtractSection(string iBody, string iPrefix, out int oMatches)
        {
            oMatches = 0;
            string[] aLines = iBody.Split('\n');
            int aStart = -1, aLevel = 0;
            bool aFence = false;
            for (int i = 0; i < aLines.Length; i++)
            {
                if (aLines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) { aFence = !aFence; continue; }
                if (aFence || !TryHeading(aLines[i], out int aLv, out string aText)) continue;
                if (aText.StartsWith(iPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    oMatches++;
                    if (aStart < 0) { aStart = i; aLevel = aLv; }
                }
            }
            if (oMatches != 1) return null;

            var sb = new StringBuilder();
            aFence = false;
            for (int i = aStart; i < aLines.Length; i++)
            {
                if (aLines[i].TrimStart().StartsWith("```", StringComparison.Ordinal)) aFence = !aFence;
                else if (!aFence && i > aStart && TryHeading(aLines[i], out int aLv, out _) && aLv <= aLevel) break;
                sb.Append(aLines[i]).Append('\n');
            }
            return sb.ToString();
        }

        static bool TryHeading(string iLine, out int oLevel, out string oText)
        {
            oLevel = 0; oText = "";
            int n = 0;
            while (n < iLine.Length && iLine[n] == '#') n++;
            if (n == 0 || n > 6 || n >= iLine.Length || iLine[n] != ' ') return false;
            oLevel = n;
            oText = iLine.Substring(n + 1).Trim();
            return true;
        }
    }
}
