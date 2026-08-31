// 區塊職責：**見林（longterm digest）與見森（forest fold）** —— 狀態計算與落檔。
// 物理意義：digest 檔（`longterm/wake_<start>-<end>.md`）是**既成事實** ——
//           檔在那裡就代表那段濃縮真的發生過。任何「書籤」都只是它的快取。
//           ⇒ 本層**只認磁碟**，不讀也不寫任何 registry／profile 欄位。
// 數值影響：寫 `longterm/wake_XXX-YYY.md` ＋ 整份重建 `longterm/_index.md`；
//           見森寫 `longterm/forest/gen_NNN_wake_001-YYY.md`（append-only，舊代全留）；
//           見叢歸檔搬 `_keys_open.md` → `keys/wake_N-M.md` 並重置當期檔。
//
// 🩸 **為什麼不寫書籤**（2026-08-31，移植時查到的真因）：
//   python 那側的 `awakening.py.write_longterm_digest` 是個 wrapper，寫完記憶檔之後會
//   `save_registry(reg)` —— 而 registry 存檔對 identity 欄有守衛，於是**別的 persona**
//   （Sirius）身上一筆舊資料就讓整支 exit=1，**而見林檔明明已經寫成功了**。
//   那是「處置成功、回報失敗」，靠 exit code 判成敗的呼叫端會重跑一次見林。
//   ⇒ 本層砍掉那條路：書籤 ＝ 掃磁碟取**最大 span_end**，沒有第二個地方需要對帳。
//
// ⚠ 與 python memory.py 的檔案格式逐字同形（frontmatter 欄位、檔名零填充三位、index 行格式）。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    /// <summary>見林狀態（全部由磁碟算出來，沒有快取欄位）。</summary>
    public sealed class SCP_ConsolidateStatus
    {
        public int WakeCount;
        public string WakeCountSource = "";
        public int LastConsolidatedWake;
        public string LastConsolidatedAt = "";
        public int Gap;
        public int Threshold;
        public bool Overdue;
        public int SpanStart;
        public int SpanEnd;
        /// <summary>本段待濃縮的 episodic letters（written_at 升冪）。</summary>
        public List<string> PendingLetters = new List<string>();
    }

    /// <summary>見森狀態。</summary>
    public sealed class SCP_ForestStatus
    {
        public int DigestCount;
        public int ForestCount;
        public int Threshold;
        public bool Eligible;
        public int FoldedDigestCount;
        public int Pending;
        public bool Overdue;
        public int NextGen;
        public List<string> Digests = new List<string>();
        public string LatestForest = "";
    }

    public static class SCP_Consolidate
    {
        /// <summary>見林門檻：距上次濃縮幾個 wake 就算 overdue。</summary>
        public const int DefaultGapThreshold = 10;

        static readonly Regex s_SpanInName = new Regex(@"wake_(\d+)-(\d+)");

        // ── 見林 ────────────────────────────────────────────────────

        /// <summary>
        /// 磁碟上最大的 digest span_end 與該檔的 consolidated_at；沒有 digest ⇒ (0, "")。
        /// <para>⚠ 取**最大 span_end** 而不是「檔名排序的最後一個」—— 檔名零填充三位，
        /// wake 破百之後 `wake_099-105` 會排在 `wake_100-110` 後面，那時排序與大小不再等價。
        /// 現在等價，所以這個坑**不會叫**。</para>
        /// </summary>
        public static (int End, string At) LatestDigestSpan(string iLettersRoot, string iPersona)
        {
            int aBestEnd = 0;
            string aBestPath = "";
            foreach (string aPath in SCP_WakeLetters.ListDigests(iLettersRoot, iPersona))
            {
                Match m = s_SpanInName.Match(Path.GetFileName(aPath));
                if (!m.Success) continue;
                if (!int.TryParse(m.Groups[2].Value, out int aEnd)) continue;
                if (aEnd > aBestEnd) { aBestEnd = aEnd; aBestPath = aPath; }
            }
            if (aBestPath.Length == 0) return (0, "");
            // 欄名是 consolidated_at（不是 written_at）—— 用錯欄名會讓時戳留空，
            // 待濃縮清單就退化成「列出全部信」。
            return (aBestEnd, SCP_LetterText.ReadFrontmatterField(aBestPath, "consolidated_at"));
        }

        /// <summary>
        /// 算見林狀態。
        /// <para>⚠ <paramref name="iWakeCount"/> ≤ 0 時退回**推導值＝ wakes/ 信數 + 1**
        /// （＝線上時的本次醒來編號，與 lock 的 wake_expected 同一條規則），
        /// 並在 <see cref="SCP_ConsolidateStatus.WakeCountSource"/> 說明用的是哪一條 ——
        /// 差一號會讓 span 少一段，而那份見林看起來完全正常。</para>
        /// </summary>
        public static SCP_ConsolidateStatus Status(string iLettersRoot, string iPersona,
                                                   int iWakeCount = 0,
                                                   int iThreshold = DefaultGapThreshold)
        {
            var aStatus = new SCP_ConsolidateStatus { Threshold = iThreshold };
            int aLetters = WakeLetterCount(iLettersRoot, iPersona);
            if (iWakeCount > 0)
            {
                aStatus.WakeCount = iWakeCount;
                aStatus.WakeCountSource = "呼叫端給的";
            }
            else
            {
                aStatus.WakeCount = aLetters + 1;
                aStatus.WakeCountSource = "推導：wakes/ " + aLetters + " 封 + 1（＝線上時的本次醒來編號）";
            }

            (int aEnd, string aAt) = LatestDigestSpan(iLettersRoot, iPersona);
            aStatus.LastConsolidatedWake = aEnd;
            aStatus.LastConsolidatedAt = aAt;
            aStatus.Gap = aStatus.WakeCount - aEnd;
            aStatus.Overdue = aStatus.Gap >= iThreshold;
            aStatus.SpanStart = aEnd + 1;
            aStatus.SpanEnd = aStatus.WakeCount;
            aStatus.PendingLetters = PendingLetters(iLettersRoot, iPersona, aAt);
            return aStatus;
        }

        /// <summary>
        /// 本段待濃縮的 episodic letters（written_at 升冪；<paramref name="iSinceIso"/> 之後的）。
        /// <para>⚠ frontmatter 讀不到 written_at 的信**照樣列出來** —— 用「沒有時戳就跳過」
        /// 會讓 frontmatter 壞掉的真信靜默消失，而見林少讀幾封不會有任何一層喊。</para>
        /// </summary>
        public static List<string> PendingLetters(string iLettersRoot, string iPersona, string iSinceIso)
        {
            var aRows = new List<(string At, string Path)>();
            foreach (string aPath in EpisodicLetterPaths(iLettersRoot, iPersona))
            {
                string aAt = SCP_LetterText.ReadFrontmatterField(aPath, "written_at");
                if (iSinceIso.Length > 0 && aAt.Length > 0
                    && string.CompareOrdinal(aAt, iSinceIso) <= 0) continue;
                aRows.Add((aAt, aPath));
            }
            // 升冪：時戳空的排前面（它們是「不知道什麼時候」，讀的人該先看到）。
            aRows.Sort((a, b) =>
            {
                int c = string.CompareOrdinal(a.At, b.At);
                return c != 0 ? c : string.CompareOrdinal(a.Path, b.Path);
            });
            var aOut = new List<string>();
            foreach (var aRow in aRows) aOut.Add(aRow.Path);
            return aOut;
        }

        /// <summary>
        /// episodic letters 的**檔案清單**（頂層 ＋ <c>wakes/</c> ＋ <c>rests/</c>，去重）。
        /// <para>⛔ 這裡**刻意不用** <see cref="SCP_WakeLetters.RecentSelfLetters"/>：那支會要求
        /// frontmatter 的 <c>type: letter_to_future_self</c>，對「見樹」是對的，對「待濃縮」是錯的。
        /// </para>
        /// <para>🩸 2026-08-31 對拍讀數：basecamp 這一段 python 列 **2 封**、第一版的我列 **0 封** ——
        /// 差的兩封（<c>20260816T075838Z_wake59_four_same_shapes.md</c> 與
        /// <c>letter_to_future_self_20260828.md</c>）**根本沒有 frontmatter**。
        /// 用 frontmatter 當入場條件 ＝ frontmatter 壞掉的真信會**靜默消失**，
        /// 而見林少讀幾封不會有任何一層喊（python 那側的註解早就寫了這句，我照抄了錯的那一半）。</para>
        /// <para>⇒ 判準：**用檔名擋（`_` 開頭的機械產物、README），不用內容擋。**</para>
        /// </summary>
        public static List<string> EpisodicLetterPaths(string iLettersRoot, string iPersona)
        {
            var aRoot = new SCP_LettersRoot(iLettersRoot);
            string aPersonaDir = SCP_LettersPaths.PersonaDir(aRoot, iPersona);
            var aOut = new List<string>();
            if (!Directory.Exists(aPersonaDir)) return aOut;

            var aTopNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (string aFile in SafeFiles(aPersonaDir)) aTopNames.Add(Path.GetFileName(aFile));

            var aCandidates = new List<string>(SafeFiles(aPersonaDir));
            aCandidates.AddRange(SafeFiles(SCP_LettersPaths.WakesDir(aRoot, iPersona)));
            aCandidates.AddRange(SafeFiles(SCP_LettersPaths.RestsDir(aRoot, iPersona)));

            foreach (string aPath in aCandidates)
            {
                string aName = Path.GetFileName(aPath);
                // 常駐檔／機械產物（_latest / _index / _constitution / _keys_open / README）不是 episodic 信。
                if (aName.StartsWith("_", StringComparison.Ordinal)) continue;
                if (string.Equals(aName, "README.md", StringComparison.Ordinal)) continue;

                // wakes/ 是**複製**語意（原檔保留）⇒ 同一封會在兩處各出現一次。
                // 檔名是 `<6位序號>_<原檔名>`，去掉序號前綴即可認出同一封；
                // 不去重的話見林濃縮會把每封收尾信讀兩遍。rests/ 是搬移語意，天然無重複。
                string aParent = Path.GetFileName(Path.GetDirectoryName(aPath) ?? "");
                if (string.Equals(aParent, SCP_LettersPaths.WakesDirName, StringComparison.Ordinal))
                {
                    int aUnderscore = aName.IndexOf('_');
                    string aTail = aUnderscore >= 0 ? aName.Substring(aUnderscore + 1) : aName;
                    if (aTopNames.Contains(aTail)) continue;
                }
                aOut.Add(aPath);
            }
            return aOut;
        }

        static string[] SafeFiles(string iDir)
        {
            if (!Directory.Exists(iDir)) return new string[0];
            try { return Directory.GetFiles(iDir, "*.md"); }
            catch (Exception) { return new string[0]; }
        }

        /// <summary>寫見林 digest ＋ 整份重建 `_index.md`。回 (路徑, consolidated_at)。</summary>
        public static (string Path, string At) WriteDigest(string iLettersRoot, string iPersona,
                                                           string iBody, int iSpanStart, int iSpanEnd)
        {
            var aRoot = new SCP_LettersRoot(iLettersRoot);
            string aDir = SCP_LettersPaths.LongtermDir(aRoot, iPersona);
            Directory.CreateDirectory(aDir);
            string aTs = UtcNowIso();
            string aPath = aDir + "/wake_" + iSpanStart.ToString("D3") + "-" + iSpanEnd.ToString("D3") + ".md";

            var aText = new StringBuilder();
            aText.Append("---\ntype: longterm_memory_digest\npersona: ").Append(iPersona)
                 .Append("\nspan_wake: ").Append(iSpanStart).Append("-").Append(iSpanEnd)
                 .Append("\nconsolidated_at: ").Append(aTs).Append("\n---\n\n")
                 .Append(iBody).Append("\n");
            WriteText(aPath, aText.ToString());

            RebuildIndex(iLettersRoot, iPersona);
            return (aPath, aTs);
        }

        /// <summary>整份重建 `longterm/_index.md`（掃全部 digest）。</summary>
        public static string RebuildIndex(string iLettersRoot, string iPersona)
        {
            var aRoot = new SCP_LettersRoot(iLettersRoot);
            string aDir = SCP_LettersPaths.LongtermDir(aRoot, iPersona);
            var L = new List<string> { "# Long-term memory index — " + iPersona, "" };
            foreach (string aPath in SCP_WakeLetters.ListDigests(iLettersRoot, iPersona))
            {
                string aName = Path.GetFileName(aPath);
                L.Add("- [" + aName + "](" + aName + ") — wake "
                      + SCP_LetterText.ReadFrontmatterField(aPath, "span_wake") + " @ "
                      + SCP_LetterText.ReadFrontmatterField(aPath, "consolidated_at"));
            }
            string aIndexPath = aDir + "/_index.md";
            WriteText(aIndexPath, string.Join("\n", L) + "\n");
            return aIndexPath;
        }

        /// <summary>
        /// 見叢歸檔：當期交棒清單搬進 `keys/wake_<N>-<M>.md`，當期檔刪除（下次 append 會重建）。
        /// <para>物理意義：叢的窗口與見林窗口同步開關 → 天然不會無限長。</para>
        /// <para>沒有當期檔 ⇒ 回 null（不是錯誤：這一段本來就可能沒人丟過交棒事項）。</para>
        /// </summary>
        public static string? ArchiveKeys(string iLettersRoot, string iPersona,
                                          int iSpanStart, int iSpanEnd)
        {
            var aRoot = new SCP_LettersRoot(iLettersRoot);
            string aOpen = SCP_LettersPaths.KeysOpenPath(aRoot, iPersona);
            if (!File.Exists(aOpen)) return null;
            string aDir = SCP_LettersPaths.PersonaDir(aRoot, iPersona) + "/keys";
            Directory.CreateDirectory(aDir);
            string aDest = aDir + "/wake_" + iSpanStart.ToString("D3") + "-" + iSpanEnd.ToString("D3") + ".md";
            // 先寫目的地、確認在，才刪來源 —— 反過來的話中間那一格是「兩邊都沒有」。
            File.Copy(aOpen, aDest, true);
            if (!File.Exists(aDest)) return null;
            File.Delete(aOpen);
            return aDest;
        }

        // ── 見森 ────────────────────────────────────────────────────

        public static SCP_ForestStatus ForestStatus(string iLettersRoot, string iPersona)
        {
            List<string> aDigests = SCP_WakeLetters.ListDigests(iLettersRoot, iPersona);
            List<string> aForests = SCP_WakeLetters.ListForests(iLettersRoot, iPersona);
            int aFoldedUpto = 0;
            if (aForests.Count > 0)
            {
                string aRaw = SCP_LetterText.ReadFrontmatterField(
                    aForests[aForests.Count - 1], "folded_digest_count");
                int.TryParse(aRaw, out aFoldedUpto);
            }
            var aStatus = new SCP_ForestStatus
            {
                DigestCount = aDigests.Count,
                ForestCount = aForests.Count,
                Threshold = SCP_WakeLetters.ForestDigestThreshold,
                Eligible = aDigests.Count >= SCP_WakeLetters.ForestDigestThreshold,
                FoldedDigestCount = aFoldedUpto,
                NextGen = aForests.Count + 1,
                Digests = aDigests,
                LatestForest = aForests.Count > 0 ? aForests[aForests.Count - 1] : "",
            };
            aStatus.Pending = aStatus.Eligible ? Math.Max(0, aDigests.Count - aFoldedUpto) : 0;
            aStatus.Overdue = aStatus.Eligible && aFoldedUpto < aDigests.Count;
            return aStatus;
        }

        /// <summary>寫新一代見森（append-only：舊代全保留）。</summary>
        public static string WriteForest(string iLettersRoot, string iPersona, string iBody)
        {
            SCP_ForestStatus aStatus = ForestStatus(iLettersRoot, iPersona);
            var aRoot = new SCP_LettersRoot(iLettersRoot);
            string aDir = SCP_LettersPaths.ForestDir(aRoot, iPersona);
            Directory.CreateDirectory(aDir);

            int aSpanEnd = 0;
            string aNewestDigest = "-";
            if (aStatus.Digests.Count > 0)
            {
                aNewestDigest = Path.GetFileName(aStatus.Digests[aStatus.Digests.Count - 1]);
                Match m = s_SpanInName.Match(aNewestDigest);
                if (m.Success) int.TryParse(m.Groups[2].Value, out aSpanEnd);
            }
            string aPrev = aStatus.LatestForest.Length > 0
                ? Path.GetFileName(aStatus.LatestForest) : "(首折)";
            string aPath = aDir + "/gen_" + aStatus.NextGen.ToString("D3")
                           + "_wake_001-" + aSpanEnd.ToString("D3") + ".md";

            var aText = new StringBuilder();
            aText.Append("---\ntype: forest_digest\npersona: ").Append(iPersona)
                 .Append("\ngeneration: ").Append(aStatus.NextGen)
                 .Append("\nspan_wake: 1-").Append(aSpanEnd)
                 .Append("\nfolded_digest_count: ").Append(aStatus.DigestCount)
                 .Append("\nfolded_from: ").Append(aPrev).Append(" + ").Append(aNewestDigest)
                 .Append("\nconsolidated_at: ").Append(UtcNowIso()).Append("\n---\n\n")
                 .Append(iBody.Trim()).Append("\n");
            WriteText(aPath, aText.ToString());
            return aPath;
        }

        // ── 共用 ────────────────────────────────────────────────────

        /// <summary>
        /// `wakes/` 底下的收尾信數 —— 「這個人活過幾次」的**磁碟既成事實**。
        /// <para>⚠ 它不等於 wake_count：線上時 wake_count ＝ 這個數 + 1（本次的信還沒寫）。
        /// 兩者差一號，而差錯的那份見林看起來完全正常。</para>
        /// </summary>
        public static int WakeLetterCount(string iLettersRoot, string iPersona)
        {
            string aDir = SCP_LettersPaths.WakesDir(new SCP_LettersRoot(iLettersRoot), iPersona);
            if (!Directory.Exists(aDir)) return 0;
            return Directory.GetFiles(aDir, "*.md").Length;
        }

        /// <summary>與 python `utcnow_iso()` 同形：微秒 ＋ 尾綴 Z。</summary>
        public static string UtcNowIso()
            => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                                        System.Globalization.CultureInfo.InvariantCulture);

        /// <summary>
        /// 寫檔。⚠ 行尾用平台預設 —— python 那端是 `write_text()`（文字模式），
        /// Windows 生 CRLF、Linux 生 LF。這些檔都是**整份覆寫**，兩端行尾不一致的症狀是
        /// 「換一支工具跑就整份翻動」，而 diff 上長得像有人改了記憶內容。
        /// BOM 一律不寫：python 讀 frontmatter 時第一行會變成 "﻿---"，判定就此失效。
        /// </summary>
        static void WriteText(string iPath, string iText)
            => File.WriteAllText(iPath, iText.Replace("\n", Environment.NewLine), new UTF8Encoding(false));
    }
}
