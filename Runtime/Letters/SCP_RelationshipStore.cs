// 區塊職責：relationship（好感度）的**寫入**本體 —— 8 軸定義、重算、target 資料夾解析、事件／看法／投影的落檔。
// 物理意義：TASK-0354。移植自 UCL `UCL_RelationshipModels` ＋ `UCL_RelationshipIO`（2026-10-01），
//           讓好感度的唯一寫入通道（`senate cmd relationship`）不再需要 Unity Editor。
//           讀取端（`SCP_Relationship`：brief／portrait-next 讀 `_current.md`）不動。
//           ⭐ 寫出來的三種檔與 Editor 版**逐位元組同形**：事件檔名＝時刻、看法檔名＝內容雜湊、
//           投影可刪除重建 —— 兩個寫入端只要形狀不分岔，就不會在同一個 target 底下長出兩種帳。
// 數值影響：純檔案 IO（UTF-8 無 BOM、`\n`）。重算是 float 逐筆累加、最後 clamp 一次再四捨五入到 4 位
//           （與 Editor 版同一順序：事件檔的磁碟列舉序 —— float 加法不滿足結合律，順序要一樣）。
//
// ⚠ 照抄 Editor 版的兩個怪行為（改了就不再同形；要修另開單）：
//   ① 事件檔的 `surface_score_after` 永遠寫 0：Editor 版在**寫檔之後**才回填分數（磁碟實測 live 事件全為 0）。
//   ② 資料夾存在但沒有 `_target.txt` ⇒ 走 `__<hash4>` 備用夾（註解寫「被接管」，程式碼不是）。
// ⚠ 與 Editor 版**刻意不同**的一格（修 bug）：rebuild 全部時，target 名取資料夾裡 `_target.txt` 的主人，
//   沒有才用資料夾名 —— Editor 版直接拿資料夾名（`Kaguya__b557`）當 target，於是每跑一次就再疊一層後綴夾
//   （calli 的 `Kaguya__b557__84d3` 就是它長出來的）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    // ===========================================================
    // 區塊職責：8 軸的名稱、權重、值域 —— 舊 affinity 原樣搬過來。
    // ⚠ 順序不可重排（重排不會報錯，只會讓每個人的好感度悄悄變成別的數字）。
    // ===========================================================
    public static class SCP_RelationshipAxes
    {
        public static readonly string[] Names =
        {
            "trust", "affection", "respect", "interest",
            "irritation", "dependence", "admiration", "loyalty",
        };

        /// <summary>加權和用的權重（irritation 是負權重）。順序與 <see cref="Names"/> 同。</summary>
        public static readonly float[] WeightsInOrder = { 2.0f, 2.0f, 1.5f, 1.0f, -2.0f, 0.5f, 1.0f, 1.5f };

        public const float Min = -1.0f;
        public const float Max = 1.0f;

        public static float Clamp(float v) => v < Min ? Min : (v > Max ? Max : v);

        /// <summary>
        /// 加權和 → [-100, 100]。分母是**權重絕對值的總和**（11.5）—— Editor 版拿 108 筆既有資料回歸過，不要動。
        /// </summary>
        public static int SurfaceScore(IReadOnlyDictionary<string, float> iVec)
        {
            float aSum = 0f, aWAbs = 0f;
            for (int i = 0; i < Names.Length; i++)
            {
                iVec.TryGetValue(Names[i], out float v);
                aSum += v * WeightsInOrder[i];
                aWAbs += Math.Abs(WeightsInOrder[i]);
            }
            if (aWAbs <= 0f) return 0;
            float aNorm = aSum / aWAbs * 100f;
            if (aNorm > 100f) aNorm = 100f;
            if (aNorm < -100f) aNorm = -100f;
            return (int)Math.Round(aNorm, MidpointRounding.AwayFromZero);
        }

        /// <summary>分段 51 / 11 / -9 / -49（Editor 版實測 108/108 相符）。</summary>
        public static string Tier(int iScore)
        {
            if (iScore >= 51) return "信任";
            if (iScore >= 11) return "在意";
            if (iScore >= -9) return "普通";
            if (iScore >= -49) return "冷淡";
            return "厭惡";
        }

        /// <summary>數值字面：`0.####`（Invariant）—— 事件、投影、回傳檔共用。</summary>
        public static string Fmt(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    }

    /// <summary>一筆好感事件。檔名＝<see cref="At"/> 去掉 `-:.`（事件的身分就是它發生的時刻）。</summary>
    public sealed class SCP_RelationshipEvent
    {
        public string At = "";
        public string Persona = "";
        public string Target = "";
        public string Source = "live";
        /// <summary>插入順序即落檔順序（Editor 版是 Dictionary 的插入序；呼叫端照軸的正規順序放）。</summary>
        public List<KeyValuePair<string, float>> AxisDeltas = new List<KeyValuePair<string, float>>();
        public int SurfaceScoreAfter;
        public string Reason = "";

        public static string FileNameOf(string iAt) => (iAt ?? "").Replace("-", "").Replace(":", "").Replace(".", "") + ".md";
    }

    /// <summary>某個對象的當前總值（`_current.md` 的內容）。</summary>
    public sealed class SCP_RelationshipCurrent
    {
        public string Target = "";
        public Dictionary<string, float> EmotionVector = new Dictionary<string, float>();
        public int SurfaceScore;
        public string Tier = "";
        public int EventCount;
        public int OpinionCount;
        public string LastUpdated = "";
    }

    public static class SCP_RelationshipStore
    {
        public const string EventsDirName = "events";
        public const string OpinionsDirName = "opinions";
        public const string OwnerFileName = "_target.txt";

        public static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        public static string PersonaRelDir(string iLettersRoot, string iPersona)
            => Path.Combine(SCP_LettersPaths.PersonaDir(new SCP_LettersRoot(iLettersRoot), iPersona), SCP_Relationship.RelationshipDirName);

        // ── target 名正規化 ─────────────────────────────────────────
        /// <summary>
        /// ① 有同名 persona（大小寫不論）⇒ 以 persona 的寫法為準（多個時取 Ordinal 最小）；② 沒有 ⇒ 大寫開頭。
        /// <para>已知名字 ＝ letters 根下所有資料夾名 ＋ persona pool。⚠ 每次呼叫都重掃（Editor 版快取到 domain reload）。</para>
        /// </summary>
        public static string CanonicalTarget(string iLettersRoot, string? iTarget)
        {
            string t = (iTarget ?? "").Trim();
            if (t.Length == 0) return t;
            var aKnown = KnownNames(iLettersRoot);
            if (aKnown.Contains(t)) return t;
            string? aHit = null;
            foreach (string k in aKnown)
            {
                if (!string.Equals(k, t, StringComparison.OrdinalIgnoreCase)) continue;
                if (aHit == null || string.CompareOrdinal(k, aHit) < 0) aHit = k;
            }
            if (aHit != null) return aHit;
            return char.ToUpperInvariant(t[0]) + t.Substring(1);
        }

        static HashSet<string> KnownNames(string iLettersRoot)
        {
            var aOut = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists(iLettersRoot))
                foreach (string d in Directory.GetDirectories(iLettersRoot)) aOut.Add(Path.GetFileName(d));
            foreach (string n in SCP_PersonaProfile.PoolNames(iLettersRoot)) aOut.Add(n);
            return aOut;
        }

        // ── target 資料夾（`_target.txt` 釘住主人；大小寫只差的名字分開存）──────────
        /// <summary>
        /// target 的資料夾。<paramref name="iExact"/> 必須已正規化。<paramref name="iWrite"/>＝false 時零寫入（只回路徑）。
        /// <para>⚠ Editor 版的「dry run」其實會寫 `_target.txt`；本層把它收成真正的零寫入 ——
        /// 寫入路徑（事件／看法／投影）都會帶 <c>iWrite=true</c> 再呼叫一次，落盤結果相同。</para>
        /// </summary>
        public static string TargetDir(string iLettersRoot, string iPersona, string iExact, bool iWrite)
        {
            string aBase = Path.Combine(PersonaRelDir(iLettersRoot, iPersona), Sanitize(iExact));
            string? aOwner = ReadOwner(aBase);
            if (aOwner == null)
            {
                if (iWrite) WriteOwner(aBase, iExact);
                return aBase;
            }
            if (string.Equals(aOwner, iExact, StringComparison.Ordinal)) return aBase;
            string aAlt = aBase + "__" + Sha1Hex(iExact, 4);
            if (iWrite) WriteOwner(aAlt, iExact);
            return aAlt;
        }

        /// <summary>null ＝ 資料夾不存在；"" ＝ 資料夾在但沒有 `_target.txt`（⇒ 走備用夾，與 Editor 版同）。</summary>
        static string? ReadOwner(string iDir)
        {
            string f = Path.Combine(iDir, OwnerFileName);
            if (!File.Exists(f)) return Directory.Exists(iDir) ? "" : null;
            try { return File.ReadAllText(f, Encoding.UTF8).Trim(); } catch (Exception) { return ""; }
        }

        /// <summary>資料夾的主人（`_target.txt`）；沒有／讀不到 ⇒ null。給 rebuild 全部用。</summary>
        public static string? OwnerOf(string iDir)
        {
            string f = Path.Combine(iDir, OwnerFileName);
            if (!File.Exists(f)) return null;
            try { string s = File.ReadAllText(f, Encoding.UTF8).Trim(); return s.Length > 0 ? s : null; }
            catch (Exception) { return null; }
        }

        static void WriteOwner(string iDir, string iExact)
        {
            Directory.CreateDirectory(iDir);
            File.WriteAllText(Path.Combine(iDir, OwnerFileName), iExact + "\n", new UTF8Encoding(false));
        }

        static string Sanitize(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_unknown";
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s.Trim();
        }

        public static string Sha1Hex(string? iText, int iLen)
        {
            using (var aSha = System.Security.Cryptography.SHA1.Create())
            {
                byte[] aBytes = aSha.ComputeHash(Encoding.UTF8.GetBytes(iText ?? ""));
                var sb = new StringBuilder();
                foreach (byte b in aBytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
                return sb.ToString().Substring(0, iLen);
            }
        }

        // ── 事件 ─────────────────────────────────────────────────
        /// <summary>
        /// 寫一筆事件。回 true ＝ 真的寫了；false ＝ 同名檔已存在且 reason 相同（重複）。
        /// 同名但 reason 不同 ⇒ 另存 `-b`、<paramref name="oWarning"/> 說明（兩筆都留著讓人判斷）。
        /// </summary>
        public static bool WriteEvent(string iLettersRoot, SCP_RelationshipEvent e, out string oPath, out string oWarning)
        {
            oWarning = "";
            string aDir = Path.Combine(TargetDir(iLettersRoot, e.Persona, e.Target, true), EventsDirName);
            oPath = Path.Combine(aDir, SCP_RelationshipEvent.FileNameOf(e.At));
            if (File.Exists(oPath))
            {
                if (string.Equals(ReadBody(oPath), (e.Reason ?? "").Trim(), StringComparison.Ordinal)) return false;
                oPath = oPath.Substring(0, oPath.Length - 3) + "-b.md";
                oWarning = $"⚠ 同時戳但內容不同：{e.Persona}→{e.Target} @ {e.At} ⇒ 另存 {Path.GetFileName(oPath)}，兩筆都保留，請人工判斷";
                if (File.Exists(oPath)) return false;
            }
            Directory.CreateDirectory(aDir);
            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append("at: ").Append(e.At).Append('\n');
            sb.Append("persona: ").Append(e.Persona).Append('\n');
            sb.Append("target: ").Append(e.Target).Append('\n');
            sb.Append("source: ").Append(e.Source).Append('\n');
            sb.Append("axis_deltas:\n");
            foreach (var kv in e.AxisDeltas) sb.Append("  ").Append(kv.Key).Append(": ").Append(SCP_RelationshipAxes.Fmt(kv.Value)).Append('\n');
            sb.Append("surface_score_after: ").Append(e.SurfaceScoreAfter.ToString(CultureInfo.InvariantCulture)).Append("   # 歷史註記，不是事實來源\n");
            sb.Append("---\n\n");
            sb.Append(e.Reason).Append('\n');
            File.WriteAllText(oPath, sb.ToString(), new UTF8Encoding(false));
            return true;
        }

        // ── 看法 ─────────────────────────────────────────────────
        public static string OpinionFileName(string iText) => "op-" + Sha1Hex((iText ?? "").Trim(), 12) + ".md";

        /// <summary>寫一則看法（`origin: [live]`、帶時戳）。回 false ＝ 內容雜湊相同的那一則已存在（去重）。</summary>
        public static bool WriteOpinion(string iLettersRoot, string iPersona, string iTarget, string iText, string iAt, out string oPath)
        {
            string aDir = Path.Combine(TargetDir(iLettersRoot, iPersona, iTarget, true), OpinionsDirName);
            oPath = Path.Combine(aDir, OpinionFileName(iText));
            if (File.Exists(oPath)) return false;
            Directory.CreateDirectory(aDir);
            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append("at: ").Append(string.IsNullOrEmpty(iAt) ? "null   # 舊資料沒有時戳，不是漏填" : iAt).Append('\n');
            sb.Append("origin: [live]\n");
            sb.Append("---\n\n");
            sb.Append(iText).Append('\n');
            File.WriteAllText(oPath, sb.ToString(), new UTF8Encoding(false));
            return true;
        }

        // ── 投影 ─────────────────────────────────────────────────
        /// <summary>
        /// 由磁碟上的事件重算 <paramref name="iTarget"/> 的投影；<paramref name="iWrite"/> 時寫出 `_current.md`。
        /// <para>⚠ <paramref name="iWrite"/>＝false 時**零寫入**（不建資料夾、不釘 `_target.txt`）。</para>
        /// </summary>
        public static SCP_RelationshipCurrent RebuildCurrent(string iLettersRoot, string iPersona, string iTarget, bool iWrite)
        {
            string aDir = TargetDir(iLettersRoot, iPersona, iTarget, iWrite);
            var aEvents = LoadEventDeltas(Path.Combine(aDir, EventsDirName));
            var aVec = Recompute(aEvents);
            string aOpDir = Path.Combine(aDir, OpinionsDirName);
            var c = new SCP_RelationshipCurrent
            {
                Target = iTarget,
                EmotionVector = aVec,
                SurfaceScore = SCP_RelationshipAxes.SurfaceScore(aVec),
                EventCount = aEvents.Count,
                OpinionCount = Directory.Exists(aOpDir) ? Directory.GetFiles(aOpDir, "*.md").Length : 0,
                LastUpdated = NowIso(),
            };
            c.Tier = SCP_RelationshipAxes.Tier(c.SurfaceScore);
            if (!iWrite) return c;

            Directory.CreateDirectory(aDir);
            var sb = new StringBuilder();
            sb.Append("---\n");
            sb.Append("target: ").Append(c.Target).Append('\n');
            sb.Append("emotion_vector:\n");
            foreach (string a in SCP_RelationshipAxes.Names) sb.Append("  ").Append(a).Append(": ").Append(SCP_RelationshipAxes.Fmt(c.EmotionVector[a])).Append('\n');
            sb.Append("surface_score: ").Append(c.SurfaceScore.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("tier: ").Append(c.Tier).Append('\n');
            sb.Append("event_count: ").Append(c.EventCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("opinion_count: ").Append(c.OpinionCount.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("last_updated: ").Append(c.LastUpdated).Append('\n');
            sb.Append("recomputable: true\n");
            sb.Append("opening_balance: null\n");
            sb.Append("generated: mechanical   # 事實來源是 events/；本檔可刪除重建\n");
            sb.Append("---\n\n");
            sb.Append("# ").Append(iPersona).Append(" → ").Append(iTarget).Append("\n\n");
            sb.Append('`').Append(c.Tier).Append("`　surface_score **").Append(c.SurfaceScore.ToString(CultureInfo.InvariantCulture))
              .Append("**　事件 ").Append(c.EventCount.ToString(CultureInfo.InvariantCulture))
              .Append(" 筆　看法 ").Append(c.OpinionCount.ToString(CultureInfo.InvariantCulture)).Append(" 則\n");
            File.WriteAllText(Path.Combine(aDir, SCP_Relationship.CurrentFileName), sb.ToString(), new UTF8Encoding(false));
            return c;
        }

        /// <summary>逐筆累加（float）→ clamp → Round(4)。事件順序＝磁碟列舉序（與 Editor 版同）。</summary>
        public static Dictionary<string, float> Recompute(List<List<KeyValuePair<string, float>>> iEvents)
        {
            var aAcc = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (string a in SCP_RelationshipAxes.Names) aAcc[a] = 0f;
            foreach (var e in iEvents)
                foreach (var kv in e) if (aAcc.ContainsKey(kv.Key)) aAcc[kv.Key] += kv.Value;
            var aOut = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (string a in SCP_RelationshipAxes.Names) aOut[a] = (float)Math.Round(SCP_RelationshipAxes.Clamp(aAcc[a]), 4);
            return aOut;
        }

        /// <summary>讀一個 events 夾裡每個事件檔的 axis_deltas（與 Editor 版 LoadEvents 同一套解析）。</summary>
        public static List<List<KeyValuePair<string, float>>> LoadEventDeltas(string iEventsDir)
        {
            var aOut = new List<List<KeyValuePair<string, float>>>();
            if (!Directory.Exists(iEventsDir)) return aOut;
            foreach (string f in Directory.GetFiles(iEventsDir, "*.md"))
            {
                var aDeltas = new List<KeyValuePair<string, float>>();
                bool aIn = false, aInDeltas = false, aPastFm = false;
                foreach (string ln in File.ReadAllLines(f, Encoding.UTF8))
                {
                    if (!aPastFm && ln.StartsWith("---", StringComparison.Ordinal))
                    {
                        if (!aIn) { aIn = true; continue; }
                        aPastFm = true; continue;
                    }
                    if (aPastFm) break;
                    if (ln.StartsWith("  ", StringComparison.Ordinal) && aInDeltas)
                    {
                        int ci = ln.IndexOf(':');
                        if (ci > 0 && float.TryParse(ln.Substring(ci + 1).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float dv))
                        {
                            string k = ln.Substring(0, ci).Trim();
                            // Editor 版是 dictionary 指派（同一軸寫兩次取後者）
                            int aIdx = aDeltas.FindIndex(p => p.Key == k);
                            if (aIdx >= 0) aDeltas[aIdx] = new KeyValuePair<string, float>(k, dv);
                            else aDeltas.Add(new KeyValuePair<string, float>(k, dv));
                        }
                        continue;
                    }
                    aInDeltas = false;
                    int c = ln.IndexOf(':');
                    if (c <= 0) continue;
                    if (ln.Substring(0, c).Trim() == "axis_deltas") aInDeltas = true;
                }
                aOut.Add(aDeltas);
            }
            return aOut;
        }

        /// <summary>frontmatter 之後的正文（trim）。讀不到 ⇒ ""。</summary>
        public static string ReadBody(string iPath)
        {
            try
            {
                var sb = new StringBuilder();
                int aDash = 0;
                foreach (string ln in File.ReadAllLines(iPath, Encoding.UTF8))
                {
                    if (aDash < 2 && ln.StartsWith("---", StringComparison.Ordinal)) { aDash++; continue; }
                    if (aDash >= 2) sb.Append(ln).Append('\n');
                }
                return sb.ToString().Trim();
            }
            catch (Exception) { return ""; }
        }
    }
}
