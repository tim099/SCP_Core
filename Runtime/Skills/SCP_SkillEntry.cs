// 區塊職責：**skill 入口檔**的解析與產生 —— 源檔宣告「內容由哪幾份文件組成」，安裝出去的只有入口。
// 物理意義：TASK-0406（Tim 2026-10-05）：「skill 本身只要提供入口 CLI」「兩邊 skill 都指向同一份 CLI 指令，
//           透過指令就能讀取到一樣的操作資訊」。
//           ⇒ 源檔 `Skills~/<name>/SKILL.md` 的 frontmatter 多一個 `docs:` 清單（文件名，可帶 `#章節`）；
//             安裝時產生的 SKILL.md ＝ frontmatter（去掉 `docs:`）＋ 一段固定的入口文字（只有那一行 CLI）。
//             內容本身由 `senate cmd skill --arg op=show` 在查詢時才現讀文件（SCP_SkillContent）。
//           沒有 `docs:` 的 skill 照舊整個目錄鏡像（相容舊形狀）。
//
// ⭐ Antigravity 的 `trigger:` 注入是**逐行移植** python `install_skills.py` 的
//   `_extract_trigger_words` / `derive_antigravity_trigger` / `transform_antigravity_frontmatter`。
//   判準是「同一份 SKILL.md 餵兩邊，產出逐位元組相同」—— ⛔ 不要「順手改得比較合理」：
//   兩套安裝器並存期，兩邊推出不同的 trigger ＝ 同一個 skill 在兩個專案裡被不同的話叫醒，而那不會報錯。
//   python 端的怪角落（`split("---", 2)` 不看行首、CRLF 檔的 trigger 行用 `\n`）也照抄，理由同上。
// 數值影響：純字串處理，零 IO（讀檔由呼叫端做）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SCP.Core.Skills
{
    /// <summary>skill 源檔的一筆內容宣告：文件名 ＋ 選填章節。</summary>
    public sealed class SCP_SkillDocRef
    {
        public string Doc { get; }
        /// <summary>章節標題的開頭（空字串 ＝ 整份）。</summary>
        public string Section { get; }
        public SCP_SkillDocRef(string iDoc, string iSection) { Doc = iDoc; Section = iSection; }
        public override string ToString() => Section.Length == 0 ? Doc : Doc + "#" + Section;
    }

    public static class SCP_SkillEntry
    {
        /// <summary>入口檔裡那一行指令（`<name>` 換成 skill 名）。UCL 那側手寫的入口檔要照抄同一行。</summary>
        public const string ShowCommandTemplate = "senate cmd skill --arg op=show --arg name=<name>";

        public static string ShowCommand(string iSkill) => ShowCommandTemplate.Replace("<name>", iSkill);

        // ── 源檔解析 ──────────────────────────────────────────────

        /// <summary>
        /// 取 frontmatter 的 `docs:` 清單。兩種寫法都收：`docs: [A, B#節]` 與逐行 `  - A`。
        /// <para>回 null ＝ **沒有宣告**（走鏡像模式）；回空清單 ＝ 宣告了但是空的（呼叫端要當錯誤）。
        /// ⚠ 兩者不得同形：「忘了寫」與「寫了零份」的處置不同。</para>
        /// </summary>
        public static List<SCP_SkillDocRef>? ParseDocs(string iContent)
        {
            string? aFront = Frontmatter(iContent);
            if (aFront == null) return null;
            string[] aLines = aFront.Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < aLines.Length; i++)
            {
                string aLine = aLines[i];
                if (!aLine.StartsWith("docs:", StringComparison.Ordinal)) continue;
                var aOut = new List<SCP_SkillDocRef>();
                string aInline = aLine.Substring(5).Trim();
                if (aInline.StartsWith("[", StringComparison.Ordinal))
                {
                    foreach (string aPart in aInline.Trim('[', ']').Split(','))
                        AddRef(aOut, aPart);
                    return aOut;
                }
                for (int j = i + 1; j < aLines.Length; j++)
                {
                    string t = aLines[j].Trim();
                    if (!t.StartsWith("- ", StringComparison.Ordinal) && t != "-") break;
                    AddRef(aOut, t.Substring(1));
                }
                return aOut;
            }
            return null;
        }

        static void AddRef(List<SCP_SkillDocRef> oOut, string iRaw)
        {
            string s = iRaw.Trim().Trim('"', '\'').Trim();
            if (s.Length == 0) return;
            int aHash = s.IndexOf('#');
            oOut.Add(aHash < 0 ? new SCP_SkillDocRef(s, "")
                               : new SCP_SkillDocRef(s.Substring(0, aHash).Trim(), s.Substring(aHash + 1).Trim()));
        }

        /// <summary>frontmatter 本文（不含包夾的 ---）；沒有回 null。只認檔首那一組。</summary>
        static string? Frontmatter(string iContent)
        {
            string s = iContent.Replace("\r\n", "\n");
            if (!s.StartsWith("---\n", StringComparison.Ordinal)) return null;
            int aEnd = s.IndexOf("\n---", 3, StringComparison.Ordinal);
            return aEnd < 0 ? null : s.Substring(4, aEnd - 4 + 1);
        }

        // ── 入口檔產生 ────────────────────────────────────────────

        /// <summary>
        /// 由源檔產生安裝出去的 SKILL.md（**LF**；行尾由呼叫端照源檔換）：
        /// 源檔的 frontmatter 去掉 `docs:` 區塊 ＋ 固定的入口文字。源檔 frontmatter 之後的內文**一律不帶**。
        /// </summary>
        public static string RenderEntry(string iSource, string iSkill)
        {
            string s = iSource.Replace("\r\n", "\n").Replace('\r', '\n');
            string? aFront = Frontmatter(s) ?? "";
            var aKept = new StringBuilder();
            string[] aLines = aFront.Split('\n');
            for (int i = 0; i < aLines.Length; i++)
            {
                string aLine = aLines[i];
                if (aLine.StartsWith("docs:", StringComparison.Ordinal))
                {
                    // 逐行清單一併跳過
                    while (i + 1 < aLines.Length && aLines[i + 1].TrimStart().StartsWith("-", StringComparison.Ordinal)) i++;
                    continue;
                }
                if (i == aLines.Length - 1 && aLine.Length == 0) break;   // split 尾巴的空字串
                aKept.Append(aLine).Append('\n');
            }
            string aCmd = ShowCommand(iSkill);
            return "---\n" + aKept + "---\n\n"
                 + "# " + iSkill + "\n\n"
                 + "這份 skill 只有入口。**動手之前先跑下面這一行，照它印出來的內容做：**\n\n"
                 + "```bash\n" + aCmd + "\n```\n\n"
                 + "- ⛔ 指令失敗（exit ≠ 0）就停下來回報，**不要憑印象或舊記憶照做** —— 內容只住在那一支指令後面。\n"
                 + "- 內容是查詢當下才從文件組出來的；同一個 skill 在哪個專案裡跑這一行，讀到的都是同一份。\n";
        }

        // ── 安裝時的位元組（行尾跟隨源檔）──────────────────────────

        /// <summary>
        /// 某個 target 該落地的 SKILL.md 位元組。入口模式先 <see cref="RenderEntry"/>；
        /// Antigravity 再注入 trigger；最後行尾照源檔（源檔含 CRLF ⇒ CRLF）。
        /// </summary>
        public static byte[] SkillFileBytes(byte[] iSourceBytes, string iSkill, bool iEntryMode, bool iAntigravity)
        {
            string aNl = ContainsCrLf(iSourceBytes) ? "\r\n" : "\n";
            // python read_text 的 universal newline：CRLF 與單獨的 CR 都收成 LF
            string aText = Encoding.UTF8.GetString(iSourceBytes).Replace("\r\n", "\n").Replace('\r', '\n');
            if (iEntryMode) aText = RenderEntry(aText, iSkill);
            if (iAntigravity) aText = TransformAntigravity(aText, iSkill);
            return Encoding.UTF8.GetBytes(aText.Replace("\r\n", "\n").Replace("\n", aNl));
        }

        static bool ContainsCrLf(byte[] b)
        {
            for (int i = 0; i + 1 < b.Length; i++) if (b[i] == '\r' && b[i + 1] == '\n') return true;
            return false;
        }

        // ── Antigravity trigger（python 逐行移植）─────────────────

        /// <summary>python `transform_antigravity_frontmatter`。</summary>
        public static string TransformAntigravity(string iContent, string iSkill)
        {
            if (iContent.StartsWith("---", StringComparison.Ordinal))
            {
                string[] aParts = PySplit(iContent, "---", 2);
                if (aParts.Length >= 3)
                {
                    string aFront = aParts[1];
                    if (aFront.Contains("trigger:")) return iContent;
                    string aVal = DeriveAntigravityTrigger(iContent, iSkill);
                    return "---\n" + "trigger: " + aVal + aFront + "---" + aParts[2];
                }
            }
            string aVal2 = DeriveAntigravityTrigger(iContent, iSkill);
            return "---\ntrigger: " + aVal2 + "\n---\n\n" + iContent;
        }

        /// <summary>python `derive_antigravity_trigger`（(A) on_intent → (B)「觸發詞」行 → (C) always_on）。</summary>
        public static string DeriveAntigravityTrigger(string iContent, string iSkill)
        {
            string aFront = "";
            if (iContent.StartsWith("---", StringComparison.Ordinal))
            {
                string[] aParts = PySplit(iContent, "---", 2);
                if (aParts.Length >= 3) aFront = aParts[1];
            }
            Match mf = Regex.Match(aFront, @"^\s*on_files\s*:\s*(\[.*\])\s*$", RegexOptions.Multiline);
            string? aOnFiles = mf.Success ? mf.Groups[1].Value.Trim() : null;

            string Wrap(string iIntent) => aOnFiles != null
                ? "{ on_files: " + aOnFiles + ", on_intent: " + iIntent + " }"
                : "{ on_intent: " + iIntent + " }";

            Match m = Regex.Match(aFront, @"^\s*on_intent\s*:\s*(\[.*\])\s*$", RegexOptions.Multiline);
            if (m.Success) return Wrap(m.Groups[1].Value.Trim());
            List<string> aWords = ExtractTriggerWords(aFront);
            if (aWords.Count > 0) return Wrap(JsonStrList(aWords));
            if (aOnFiles != null) return "{ on_files: " + aOnFiles + " }";
            LastWarning = iSkill + "：SKILL.md 無 on_intent/on_files 欄且抓不到「觸發詞」行 → trigger=always_on";
            return "\"always_on\"";
        }

        /// <summary>最近一次退到 always_on 的說明（python 印 stderr；這裡留給呼叫端印）。</summary>
        [ThreadStatic] public static string? LastWarning;

        static readonly string[] s_StopWords = { "跨 agent", "對應", "本 skill", "完整" };

        /// <summary>python `_extract_trigger_words`。</summary>
        public static List<string> ExtractTriggerWords(string iFront)
        {
            var aOut = new List<string>();
            Match m = Regex.Match(iFront, @"觸發詞[^\n:：]*[:：]\s*(.*)");
            if (!m.Success) return aOut;
            string aTail = iFront.Substring(m.Groups[1].Index);
            var aCollected = new List<string>();
            string[] aRaw = aTail.Split('\n');
            for (int i = 0; i < aRaw.Length; i++)
            {
                string s = PyStrip(aRaw[i]);
                if (i == 0) { aCollected.Add(s); continue; }
                if (s.Length == 0) break;
                bool aStop = false;
                foreach (string k in s_StopWords) if (s.Contains(k)) { aStop = true; break; }
                if (aStop) break;
                if (s.Contains("/") || s.Contains("／") || s.Contains("、") || s.StartsWith("-", StringComparison.Ordinal))
                    aCollected.Add(s);
                else break;
            }
            var aJoined = new List<string>();
            foreach (string ln in aCollected) aJoined.Add(Regex.Replace(ln, @"^\s*-\s+", ""));
            string aBlob = string.Join("/", aJoined);
            aBlob = aBlob.Split('。')[0];

            var aWords = new List<string>();
            foreach (string aPart in Regex.Split(aBlob, @"[/／、]|\s[-–—]\s"))
            {
                string p = Regex.Replace(aPart, @"（[^）]*）", "");
                p = Regex.Replace(p, @"\([^)]*\)", "");
                if ((p.Contains("**") || p.Contains("→")) && (p.Contains("：") || p.Contains(":")))
                {
                    string[] aSeg = Regex.Split(p, @"[:：]");
                    p = aSeg[aSeg.Length - 1];
                }
                p = p.Replace("**", "").Replace("→", " ");
                p = PyStrip(p);
                p = p.Trim('`');
                p = p.Trim('。', '，', ',', '、', ' ');
                p = PyStrip(p);
                p = p.TrimStart('-', '*');
                p = PyStrip(p);
                if (p.Length > 0 && CodePoints(p) <= 40 && !p.Contains("**")) aWords.Add(p);
            }
            var aSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string w in aWords)
                if (aSeen.Add(w)) aOut.Add(w);
            if (aOut.Count > 40) aOut.RemoveRange(40, aOut.Count - 40);
            return aOut;
        }

        // ── python 語意的小工具 ────────────────────────────────────

        /// <summary>python `str.split(sep, maxsplit)`：不看行首、從左切 maxsplit 次。</summary>
        static string[] PySplit(string s, string iSep, int iMax)
        {
            var aOut = new List<string>();
            int aPos = 0;
            for (int n = 0; n < iMax; n++)
            {
                int i = s.IndexOf(iSep, aPos, StringComparison.Ordinal);
                if (i < 0) break;
                aOut.Add(s.Substring(aPos, i - aPos));
                aPos = i + iSep.Length;
            }
            aOut.Add(s.Substring(aPos));
            return aOut.ToArray();
        }

        /// <summary>python `str.strip()`：去兩端 Unicode 空白。</summary>
        static string PyStrip(string s) => s.Trim();

        static int CodePoints(string s)
        {
            int n = 0;
            for (int i = 0; i < s.Length; i++) { if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++; n++; }
            return n;
        }

        /// <summary>python `"[" + ", ".join(json.dumps(w, ensure_ascii=False)) + "]"`。</summary>
        static string JsonStrList(List<string> iWords)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < iWords.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('"');
                foreach (char c in iWords[i])
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        case '\b': sb.Append("\\b"); break;
                        case '\f': sb.Append("\\f"); break;
                        default:
                            if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                            else sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
            }
            return sb.Append(']').ToString();
        }
    }
}
