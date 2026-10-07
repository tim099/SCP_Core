// 區塊職責：新詞辭典（glossary）的**唯一一份**實作 —— 解析 .md、偵測命中、附註、登記、查詢、列表。
// 物理意義：TASK-0313（Tim 2026-09-27「glossary 功能遷移到 Senate CLI」、2026-09-28「詞典根留在 senate.local.json，
//           Unity 端相關功能也遷到 Senate CLI」）。此前有兩份：Editor `Cmd_Glossary`（637 行）與
//           `SCP_TavernPostCompose` 裡「逐字對齊它」的移植版 —— 兩份都對，而改一份不會讓另一份知道。
//           ⇒ 行為逐格照 Editor 版搬（frontmatter 解析、longest-match-wins、slug 去重、附註格式、marker 字串）。
// 數值影響：只有 <see cref="Register"/> 寫檔（一個 .md）；其餘純讀。詞典根**由呼叫端給**，本層不推導、不 walk cwd
//           （詞典根的真相源是 Senate 的 `SCP_PathId.GlossaryRoot`，Tim 2026-09-27 拍板存 senate.local.json）。
//
// 🩸 顯示前綴為什麼是參數（不是寫死 `docs/Glossary`）：詞典根改成非預設值時，印一個指不到檔的相對路徑
//    跟印對的路徑在畫面上同形 ⇒ 由 <see cref="DisplayPrefix"/> 依實際根推導。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SCP.Core.Glossary
{
    /// <summary>一則詞條（frontmatter 裡本層在意的欄位）。</summary>
    public sealed class SCP_GlossaryEntry
    {
        public string Term = "";
        public string Slug = "";
        public List<string> Aliases = new List<string>();
        public string Category = "";
        public string OneLine = "";
        public string CreatedAt = "";
        public string CreatedBy = "";
        /// <summary>檔案的完整路徑。</summary>
        public string FilePath = "";
        /// <summary>相對詞典根的子路徑（forward-slash），例：`personas/basecamp.md`。</summary>
        public string Rel = "";
    }

    /// <summary>一次命中。</summary>
    public sealed class SCP_GlossaryHit
    {
        public SCP_GlossaryEntry Entry = new SCP_GlossaryEntry();
        /// <summary>實際命中的字（term 或某個 alias，原字形）。</summary>
        public string Matched = "";
        public int Start;
        public int Length;
    }

    /// <summary><see cref="SCP_Glossary.Register"/> 的結果。</summary>
    public sealed class SCP_GlossaryRegisterResult
    {
        public bool Ok;
        public string Error = "";
        /// <summary>寫到哪個檔（完整路徑）。</summary>
        public string FullPath = "";
        /// <summary>相對詞典根的子路徑。</summary>
        public string Rel = "";
        public bool Overwrote;
        public List<string> Aliases = new List<string>();
        public string CreatedBy = "";
    }

    public static class SCP_Glossary
    {
        /// <summary>附註區塊的標頭。⚠ 逐字沿用（讀取端的 StripAutoAttachedBlocks 與冪等判斷都靠它）。</summary>
        public const string AutoAttachMarker = "📖 **本回提到的新詞** (auto-attached by Cmd_Glossary):";

        /// <summary>預設詞典根的顯示前綴（Editor 版印的就是它）。</summary>
        public const string DefaultDisplayPrefix = "docs/Glossary";

        /// <summary>發文時自動附註的上限。</summary>
        public const int AutoAttachCap = 5;

        /// <summary>
        /// 發文端請寫入端補附註的**一次性** meta 鍵（TASK-0313）。寫入端看到它才附，附完（或判定不附）一律拿掉，⛔ 不落進訊息檔。
        /// <para>🩸 為什麼是「請求」而不是寫入端預設全附：Editor 有 20 處直接寫訊息（酒保回覆、Discord 進站、頁面按鈕…），
        /// 以前只有 `Op_Post` 會附 —— 預設全附會讓它們從某一天起突然長出附註，而那不會報錯。</para>
        /// </summary>
        public const string AttachRequestMetaKey = "glossary-attach-request";

        /// <summary>
        /// 附註裡印的路徑前綴（讀的人拿它去 Read 那個 .md）。
        /// <para>預設詞典根 ⇒ 逐字 `docs/Glossary`；在基準根底下 ⇒ 相對基準根（Senate 宿主給的是 Senate 專案根）；在專案外 ⇒ 絕對路徑
        /// （⛔ 不印一個指不到檔的相對路徑）。</para>
        /// </summary>
        public static string DisplayPrefix(string iProjectRoot, string iGlossaryRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(iProjectRoot)) return Path.GetFullPath(iGlossaryRoot).Replace('\\', '/').TrimEnd('/');
                string aProj = Path.GetFullPath(iProjectRoot).Replace('\\', '/').TrimEnd('/');
                string aGlo = Path.GetFullPath(iGlossaryRoot).Replace('\\', '/').TrimEnd('/');
                string aDefault = Path.GetFullPath(Path.Combine(iProjectRoot, DefaultDisplayPrefix)).Replace('\\', '/').TrimEnd('/');
                if (string.Equals(aGlo, aDefault, StringComparison.OrdinalIgnoreCase)) return DefaultDisplayPrefix;
                if (aGlo.StartsWith(aProj + "/", StringComparison.OrdinalIgnoreCase)) return aGlo.Substring(aProj.Length + 1);
                return aGlo;
            }
            catch (Exception) { return DefaultDisplayPrefix; }
        }

        // ===========================================================
        // 區塊職責：發文時要不要自動附註 —— 寫入端與組訊息端共用同一個判準。
        // 物理意義：三種刻意不附：系統元件（sender 底線開頭）、酒保 CLI 指令（`cli_cmd=true`／`tag=cli-cmd`）、
        //          顯式 `glossary-auto-attach=false`（不分大小寫）。
        // 🩸 2026-08-19：附註被當成 CLI 指令的一部分，群發把整本詞典打進別人輸入框並按 Enter。
        // ⚠ 已含 marker 的不在這裡判（那是 AppendRefs 的冪等閘），這裡只回答「意圖上該不該附」。
        // ===========================================================
        public static bool ShouldAutoAttach(string iSenderId, IReadOnlyDictionary<string, string>? iMeta)
        {
            if ((iSenderId ?? "").StartsWith("_", StringComparison.Ordinal)) return false;
            if (iMeta == null) return true;
            if (iMeta.TryGetValue("cli_cmd", out string? aCli) && string.Equals((aCli ?? "").Trim(), "true", StringComparison.OrdinalIgnoreCase))
                return false;
            if (iMeta.TryGetValue("tag", out string? aTag) && string.Equals((aTag ?? "").Trim(), "cli-cmd", StringComparison.OrdinalIgnoreCase))
                return false;
            if (iMeta.TryGetValue("glossary-auto-attach", out string? aGa) && string.Equals((aGa ?? "").Trim(), "false", StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        /// <summary>
        /// 在文字結尾附上命中詞條的解說。命中 0 ⇒ 原樣返回；已含 marker 且不是 <paramref name="iForce"/> ⇒ 原樣返回（冪等）。
        /// <para>⚠ 任何例外都回原文 —— 附註壞掉不可以擋住發文。</para>
        /// </summary>
        public static string AppendRefs(string iText, string iGlossaryRoot, string iDisplayPrefix, int iCap = AutoAttachCap,
                                        bool iForce = false)
        {
            if (string.IsNullOrEmpty(iText)) return iText;
            if (!iForce && iText.Contains(AutoAttachMarker)) return iText;
            try
            {
                List<SCP_GlossaryEntry> aEntries = LoadEntries(iGlossaryRoot);
                if (aEntries.Count == 0) return iText;
                List<SCP_GlossaryHit> aHits = DetectHits(iText, aEntries, Math.Max(1, iCap));
                if (aHits.Count == 0) return iText;
                var sb = new StringBuilder();
                sb.Append(iText.TrimEnd());
                sb.AppendLine();
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine(AutoAttachMarker);
                sb.AppendLine();
                foreach (SCP_GlossaryHit h in aHits)
                {
                    sb.AppendLine($"- **{h.Entry.Term}**: {h.Entry.OneLine}");
                    sb.AppendLine($"({iDisplayPrefix}/{h.Entry.Rel})");
                }
                return sb.ToString();
            }
            catch (Exception) { return iText; }
        }

        // ===========================================================
        // 區塊職責：載入詞典根底下所有詞條（遞迴子資料夾；跳過 README 與底線開頭的檔）。
        // 數值影響：格式壞的檔直接略過（不 crash）；根不存在 ⇒ 空清單。
        // ===========================================================
        public static List<SCP_GlossaryEntry> LoadEntries(string iGlossaryRoot)
        {
            var aList = new List<SCP_GlossaryEntry>();
            if (string.IsNullOrEmpty(iGlossaryRoot) || !Directory.Exists(iGlossaryRoot)) return aList;
            foreach (string f in Directory.GetFiles(iGlossaryRoot, "*.md", SearchOption.AllDirectories))
            {
                string aName = Path.GetFileNameWithoutExtension(f);
                if (aName.Equals("README", StringComparison.OrdinalIgnoreCase) || aName.StartsWith("_", StringComparison.Ordinal)) continue;
                SCP_GlossaryEntry? e = ParseEntry(f);
                if (e == null) continue;
                e.Rel = f.Substring(iGlossaryRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
                aList.Add(e);
            }
            return aList;
        }

        /// <summary>簡易 frontmatter parser —— 只取本層在意的欄位，⛔ 不做通用 YAML。壞格式回 null。</summary>
        public static SCP_GlossaryEntry? ParseEntry(string iPath)
        {
            try
            {
                string aContent = File.ReadAllText(iPath, Encoding.UTF8);
                if (!aContent.StartsWith("---", StringComparison.Ordinal)) return null;
                int aEnd = aContent.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (aEnd < 0) return null;
                var e = new SCP_GlossaryEntry { FilePath = iPath };
                string? aList = null;
                foreach (string aRaw in aContent.Substring(3, aEnd - 3).Split('\n'))
                {
                    string aLine = aRaw.TrimEnd('\r');
                    if (string.IsNullOrWhiteSpace(aLine)) continue;
                    if (aLine.StartsWith("  - ", StringComparison.Ordinal) && aList == "aliases") { e.Aliases.Add(Unescape(aLine.Substring(4).Trim())); continue; }
                    if (aLine.StartsWith("- ", StringComparison.Ordinal) && aList == "aliases") { e.Aliases.Add(Unescape(aLine.Substring(2).Trim())); continue; }
                    int c = aLine.IndexOf(':');
                    if (c < 0) continue;
                    string aKey = aLine.Substring(0, c).Trim();
                    string aVal = c < aLine.Length - 1 ? aLine.Substring(c + 1).Trim() : "";
                    if (aVal.Length == 0) { aList = aKey; continue; }
                    aList = null;
                    switch (aKey)
                    {
                        case "term": e.Term = Unescape(aVal); break;
                        case "slug": e.Slug = aVal; break;
                        case "category": e.Category = aVal; break;
                        case "one_line": e.OneLine = Unescape(aVal); break;
                        case "created_at": e.CreatedAt = aVal; break;
                        case "created_by": e.CreatedBy = aVal; break;
                    }
                }
                return e.Term.Length == 0 || e.Slug.Length == 0 ? null : e;
            }
            catch (Exception) { return null; }
        }

        /// <summary>term／slug／alias 都接受（不分大小寫；依序：term → slug → alias）。</summary>
        public static SCP_GlossaryEntry? Resolve(string iQuery, List<SCP_GlossaryEntry> iEntries)
        {
            foreach (var e in iEntries) if (string.Equals(e.Term, iQuery, StringComparison.OrdinalIgnoreCase)) return e;
            foreach (var e in iEntries) if (string.Equals(e.Slug, iQuery, StringComparison.OrdinalIgnoreCase)) return e;
            foreach (var e in iEntries)
                foreach (string a in e.Aliases)
                    if (string.Equals(a, iQuery, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        // ===========================================================
        // 區塊職責：掃文字命中詞條 —— substring（不分大小寫）、longest-match-wins、同 slug 只算一次。
        // 數值影響：O(文字長 × 詞條總數)；回傳上限 iCap。
        // ===========================================================
        public static List<SCP_GlossaryHit> DetectHits(string iText, List<SCP_GlossaryEntry> iEntries, int iCap)
        {
            var aRaw = new List<SCP_GlossaryHit>();
            string aLower = iText.ToLowerInvariant();
            foreach (var e in iEntries)
            {
                AddHits(e, e.Term, aLower, aRaw);
                foreach (string a in e.Aliases) AddHits(e, a, aLower, aRaw);
            }
            aRaw.Sort((a, b) => { int c = a.Start.CompareTo(b.Start); return c != 0 ? c : b.Length.CompareTo(a.Length); });
            var aSelected = new List<SCP_GlossaryHit>();
            int aLastEnd = -1;
            foreach (var h in aRaw)
            {
                if (h.Start < aLastEnd) continue;
                aSelected.Add(h);
                aLastEnd = h.Start + h.Length;
            }
            var aSeen = new HashSet<string>();
            var aResult = new List<SCP_GlossaryHit>();
            foreach (var h in aSelected)
            {
                if (!aSeen.Add(h.Entry.Slug)) continue;
                aResult.Add(h);
                if (aResult.Count >= iCap) break;
            }
            return aResult;
        }

        static void AddHits(SCP_GlossaryEntry e, string iTrigger, string iLowerText, List<SCP_GlossaryHit> oHits)
        {
            if (string.IsNullOrEmpty(iTrigger)) return;
            string t = iTrigger.ToLowerInvariant();
            int i = 0;
            while (i < iLowerText.Length)
            {
                int f = iLowerText.IndexOf(t, i, StringComparison.Ordinal);
                if (f < 0) break;
                oHits.Add(new SCP_GlossaryHit { Entry = e, Matched = iTrigger, Start = f, Length = t.Length });
                i = f + t.Length;
            }
        }

        // ===========================================================
        // 區塊職責：登記（新增／覆寫）一個詞條 —— 寫 `<詞典根>/<slug>.md`，或覆寫既有檔的原位置。
        // 物理意義：跨子資料夾查重（同 slug 在 personas/ 已存在就不在根另建一份）；
        //          created_at 不可變（覆寫時沿用、另寫 updated_at）。
        // 數值影響：已存在且沒給 overwrite ⇒ 不寫、回錯誤。
        // ===========================================================
        public static SCP_GlossaryRegisterResult Register(string iGlossaryRoot, string iTerm, string iSlug, string iAliasesCsv,
                                                          string iCategory, string iOneLine, string iBody, string iCreatedBy,
                                                          bool iOverwrite, DateTime iNowUtc)
        {
            var aOut = new SCP_GlossaryRegisterResult();
            if (string.IsNullOrEmpty(iTerm)) { aOut.Error = "register 缺少 term"; return aOut; }
            if (string.IsNullOrEmpty(iSlug)) { aOut.Error = "register 缺少 slug"; return aOut; }
            if (string.IsNullOrEmpty(iOneLine)) { aOut.Error = "register 缺少 one_line (建議 < 80 字 給 ref block 顯示用)"; return aOut; }
            if (string.IsNullOrEmpty(iGlossaryRoot)) { aOut.Error = "詞典根是空的（呼叫端沒給）"; return aOut; }

            Directory.CreateDirectory(iGlossaryRoot);
            SCP_GlossaryEntry? aExisting = LoadEntries(iGlossaryRoot).FirstOrDefault(e => e.Slug == iSlug);
            if (aExisting != null && !iOverwrite)
            {
                aOut.Error = $"glossary 已存在: {aExisting.Rel} (要覆寫加 overwrite=true)";
                aOut.Rel = aExisting.Rel;
                return aOut;
            }
            aOut.Overwrote = aExisting != null;
            aOut.FullPath = aExisting != null ? aExisting.FilePath : Path.Combine(iGlossaryRoot, iSlug + ".md");
            aOut.Rel = aExisting != null ? aExisting.Rel : iSlug + ".md";
            aOut.Aliases = string.IsNullOrEmpty(iAliasesCsv)
                ? new List<string>()
                : iAliasesCsv.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            aOut.CreatedBy = string.IsNullOrWhiteSpace(iCreatedBy) ? "unknown" : iCreatedBy;

            string aNow = iNowUtc.ToString("yyyy-MM-ddTHH:mm:ssZ");
            string aCreatedAt = aExisting != null && !string.IsNullOrWhiteSpace(aExisting.CreatedAt) ? aExisting.CreatedAt : aNow;

            var sb = new StringBuilder();
            sb.AppendLine("---");
            sb.AppendLine($"term: {Escape(iTerm)}");
            sb.AppendLine($"slug: {iSlug}");
            if (aOut.Aliases.Count > 0)
            {
                sb.AppendLine("aliases:");
                foreach (string a in aOut.Aliases) sb.AppendLine($"  - {Escape(a)}");
            }
            sb.AppendLine($"category: {iCategory}");
            sb.AppendLine($"created_at: {aCreatedAt}");
            if (aOut.Overwrote) sb.AppendLine($"updated_at: {aNow}");
            sb.AppendLine($"created_by: {aOut.CreatedBy}");
            sb.AppendLine($"one_line: {Escape(iOneLine)}");
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine($"# {iTerm}");
            sb.AppendLine();
            if (!string.IsNullOrEmpty(iBody))
            {
                if (!iBody.TrimStart().StartsWith(">", StringComparison.Ordinal))
                {
                    sb.AppendLine($"> {iOneLine}");
                    sb.AppendLine();
                }
                sb.AppendLine(iBody);
            }
            else
            {
                sb.AppendLine($"> {iOneLine}");
                sb.AppendLine();
                sb.AppendLine("_(detailed explanation TBD)_");
            }
            File.WriteAllText(aOut.FullPath, sb.ToString(), new UTF8Encoding(false));
            aOut.Ok = true;
            return aOut;
        }

        /// <summary>YAML inline escape —— 含 `:`／`#`／引號、或以 `-` 開頭時用雙引號包起來。</summary>
        public static string Escape(string s)
        {
            if (string.IsNullOrEmpty(s)) return "\"\"";
            if (s.Contains(":") || s.Contains("#") || s.StartsWith("-", StringComparison.Ordinal) || s.Contains("\""))
                return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            return s;
        }

        public static string Unescape(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = s.Trim();
            if (s.Length >= 2 && s.StartsWith("\"", StringComparison.Ordinal) && s.EndsWith("\"", StringComparison.Ordinal))
                s = s.Substring(1, s.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
            return s;
        }
    }
}
