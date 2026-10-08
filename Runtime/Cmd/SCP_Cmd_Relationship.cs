// 區塊職責：`senate cmd relationship` —— 好感度的**唯一寫入通道**：update 寫事件／add-opinion 加看法／show／list／rebuild。
// 物理意義：本體在 `SCP_RelationshipStore`（TASK-0354）。
//           跟「錢一律走 Cmd」同一個理由：重算與落檔的規則只有這一份，直寫檔案會繞過它而且不會報錯。
// 數值影響：寫 `letters/<persona>/relationship/` 底下的檔；回傳檔 `letters/<persona>/cmd/relationship_<op>.md`。
//           不動錢、不發酒館訊息。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Relationship : SCP_Cmd
    {
        public override string Name => "relationship";
        public override string Category => SCP_CmdCategory.Memory;

        public override string Summary =>
            "好感度（relationship）寫入：update 寫一筆事件／add-opinion 加看法／show／list／rebuild —— **本地跑**";

        public override string Details =>
            "update：`target`＋`reason`＋至少一軸 delta（[-1,1]，建議一次 2~4 軸）；選填 `opinion` 順手寫一則看法。\n"
            + "add-opinion：`target`＋`opinion`（看法與向量解耦，不動任何軸）。\n"
            + "show `target`／list：讀投影。rebuild [`target`]：由 events/ 重建 `_current.md`（不給 target ＝ 全部）。\n"
            + "⚠ 事件是事實來源、`_current.md` 是投影；被上限截斷的軸會在回傳檔逐軸列出（事件檔裡仍逐字留著那筆 delta）。\n"
            + "⚠ 中文長句（reason／opinion）一律 `--arg-file`。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("relationship --arg op=update --arg persona=calli --arg target=Tim"
                                   + " --arg-file reason=D:/tmp/r.md --arg trust=0.05 --arg respect=0.03");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
        {
            get
            {
                var a = new List<SCP_CmdArgSpec>
                {
                    new SCP_CmdArgSpec("letters_root", "persona 信件夾根（絕對路徑）", iRequired: true),
                    new SCP_CmdArgSpec("op", "做什麼", iDefault: "update", iChoices: new[] { "update", "add-opinion", "show", "list", "rebuild" }),
                    new SCP_CmdArgSpec("persona", "誰的感受（⛔ 不猜身分）", iRequired: true),
                    new SCP_CmdArgSpec("target", "對誰（update／add-opinion／show 必填）", iDefault: ""),
                    new SCP_CmdArgSpec("reason", "這件事是什麼（update 必填）。走 --arg-file", iDefault: ""),
                    new SCP_CmdArgSpec("opinion", "內心戲短句（update 選填；add-opinion 必填）。走 --arg-file", iDefault: ""),
                };
                foreach (string ax in SCP_RelationshipAxes.Names)
                    a.Add(new SCP_CmdArgSpec(ax, "軸 delta（[-1,1]）", iDefault: ""));
                return a;
            }
        }

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aRoot = iArgs.Get("letters_root").Trim();
            string aOp = iArgs.Get("op").Trim().ToLowerInvariant();
            string aPersona = iArgs.Get("persona").Trim();
            if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ 需要 --arg persona=<誰的感受>（不猜身分）");
            if (!Directory.Exists(aRoot)) return SCP_CmdResult.Fail(1, "✗ 信件夾根不存在：" + aRoot);
            if (!Directory.Exists(SCP_LettersPaths.PersonaDir(new SCP_LettersRoot(aRoot), aPersona)))
                return SCP_CmdResult.Fail(1, "✗ 找不到 persona 的信件夾：" + aPersona + "（⛔ 不替打錯的名字開新資料夾）");

            var aR = new StringBuilder();
            aR.Append("# Relationship op=").Append(aOp).Append(" persona=").Append(aPersona)
              .Append("  ts=`").Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture)).Append("`（本地時間）\n\n");
            var aResult = new SCP_CmdResult();

            SCP_CmdResult? aFail;
            try
            {
                switch (aOp)
                {
                    case "update": aFail = OpUpdate(iArgs, aRoot, aPersona, aR, aResult); break;
                    case "add-opinion": aFail = OpAddOpinion(iArgs, aRoot, aPersona, aR, aResult); break;
                    case "show": aFail = OpShow(iArgs, aRoot, aPersona, aR); break;
                    case "list": aFail = OpList(aRoot, aPersona, aR); break;
                    default: aFail = OpRebuild(iArgs, aRoot, aPersona, aR); break;
                }
            }
            catch (Exception e) { return SCP_CmdResult.Fail(1, "✗ " + e.GetType().Name + ": " + e.Message); }
            if (aFail != null) return aFail;

            string aPath = SCP_LettersPaths.CmdPayload(new SCP_LettersRoot(aRoot), aPersona, "relationship", aOp);
            SCP_CmdPayload.Write(aPath, aR.ToString());
            aResult.AddOutput(aPath);
            foreach (string ln in aR.ToString().TrimEnd('\n').Split('\n')) aResult.Lines.Add(ln);
            return aResult;
        }

        static string RequireTarget(SCP_CmdArgs iArgs, out SCP_CmdResult? oFail)
        {
            string t = iArgs.Get("target").Trim();
            oFail = t.Length == 0 ? SCP_CmdResult.Fail(2, "✗ 需要 --arg target=<對誰>") : null;
            return t;
        }

        // ── update：寫一筆事件 ＋ 重建投影 ─────────────────────────────
        static SCP_CmdResult? OpUpdate(SCP_CmdArgs iArgs, string iRoot, string iPersona, StringBuilder ioR, SCP_CmdResult ioRes)
        {
            string aTargetRaw = RequireTarget(iArgs, out SCP_CmdResult? aFail);
            if (aFail != null) return aFail;
            string aReason = iArgs.Get("reason").Trim();
            if (aReason.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ update 需要 reason（走 --arg-file）—— 沒有理由的 delta，三個月後沒有人看得懂它為什麼發生");

            var aDeltas = new List<KeyValuePair<string, float>>();
            foreach (string aAxis in SCP_RelationshipAxes.Names)
            {
                string v = iArgs.Get(aAxis).Trim();
                if (v.Length == 0) continue;
                if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out float d))
                    return SCP_CmdResult.Fail(2, $"✗ 軸 {aAxis} 的值 '{v}' 不是數字");
                if (d < SCP_RelationshipAxes.Min || d > SCP_RelationshipAxes.Max)
                    return SCP_CmdResult.Fail(2, $"✗ 軸 {aAxis} 的 delta {v} 超出 [-1,1]（打錯一個小數點會讓量級差十倍）");
                if (Math.Abs(d) > 0.0001f) aDeltas.Add(new KeyValuePair<string, float>(aAxis, d));
            }
            if (aDeltas.Count == 0)
                return SCP_CmdResult.Fail(2, "✗ 至少要給一個軸的 delta（" + string.Join(" / ", SCP_RelationshipAxes.Names) + "）");

            var e = new SCP_RelationshipEvent
            {
                At = SCP_RelationshipStore.NowIso(),
                Persona = iPersona,
                Target = SCP_RelationshipStore.CanonicalTarget(iRoot, aTargetRaw),
                Source = "live",
                AxisDeltas = aDeltas,
                Reason = aReason,
            };
            // 寫之前先取一次投影（零寫入）⇒ 前後相減＝這一筆真正生效的量（TASK-0291）
            var aBefore = SCP_RelationshipStore.RebuildCurrent(iRoot, iPersona, e.Target, false);
            SCP_RelationshipStore.WriteEvent(iRoot, e, out string aEvPath, out string aWarn);
            if (aWarn.Length > 0) ioRes.Lines.Add(aWarn);

            string aOpinion = iArgs.Get("opinion").Trim();
            if (aOpinion.Length > 0) WriteOpinion(iRoot, iPersona, e.Target, aOpinion, ioR);

            var aCur = SCP_RelationshipStore.RebuildCurrent(iRoot, iPersona, e.Target, true);

            ioR.Append("## ✅ ").Append(iPersona).Append(" → ").Append(e.Target).Append('\n');
            ioR.Append("- 事件：`").Append(Path.GetFileName(aEvPath)).Append("`\n");
            var aTruncated = new List<string>();
            foreach (var kv in aDeltas)
            {
                float aB = aBefore.EmotionVector.TryGetValue(kv.Key, out float b) ? b : 0f;
                float aA = aCur.EmotionVector.TryGetValue(kv.Key, out float a) ? a : 0f;
                float aEff = (float)Math.Round(aA - aB, 4);
                if (Math.Abs(aEff - kv.Value) <= 0.0001f) continue;
                float aLost = (float)Math.Round(kv.Value - aEff, 4);
                aTruncated.Add("    · " + kv.Key + "：要求 " + Signed(kv.Value) + " ／ 實際 " + Signed(aEff)
                    + "　⇒ **有 " + SCP_RelationshipAxes.Fmt(Math.Abs(aLost)) + " 沒進投影**（該軸現值 "
                    + SCP_RelationshipAxes.Fmt(aA) + "，上限 "
                    + (kv.Value >= 0 ? SCP_RelationshipAxes.Max : SCP_RelationshipAxes.Min).ToString(CultureInfo.InvariantCulture) + "）");
            }
            string aDeltaText = string.Join("　", aDeltas.Select(kv => kv.Key + " " + Signed(kv.Value)));
            if (aTruncated.Count == 0)
                ioR.Append("- 動了 ").Append(aDeltas.Count).Append(" 軸：").Append(aDeltaText).Append('\n');
            else
            {
                ioR.Append("- 要求 ").Append(aDeltas.Count).Append(" 軸：").Append(aDeltaText).Append('\n');
                ioR.Append("- ⚠ 🔴 **有 ").Append(aTruncated.Count).Append(" 軸被上限截斷**（要求的量沒有全部進到投影裡）：\n");
                foreach (string t in aTruncated) ioR.Append(t).Append('\n');
                ioR.Append("  ⛔ 這**不是「沒記到」** —— 事件檔裡逐字留著那筆 delta，只有投影停在上限。⚠ 而反方向同樣安靜：那幾軸累積出來的餘裕會先吃掉未來的負向事件。\n");
                ioR.Append("  📌 ⇒ 若這一筆的 surface_score 沒漲甚至**下降**，成因就在上面那幾行，⛔ 不是你記錯了方向。\n");
            }
            ioR.Append("- 現值：**").Append(aCur.SurfaceScore).Append("**（").Append(aCur.Tier).Append("）　累計事件 ")
               .Append(aCur.EventCount).Append(" 筆 / 看法 ").Append(aCur.OpinionCount).Append(" 則\n");
            ioR.Append("- reason: ").Append(aReason).Append('\n');
            if (aDeltas.Count == 1) ioR.Append("- ℹ 只動了一軸 —— 真實情緒通常多軸並存，建議一次 2~4 軸。\n");

            ioRes.AddOutput(aEvPath);
            ioRes.AddValue("target", e.Target);
            ioRes.AddValue("surface_score", aCur.SurfaceScore.ToString(CultureInfo.InvariantCulture));
            ioRes.AddValue("tier", aCur.Tier);
            ioRes.AddValue("truncated_axes", aTruncated.Count.ToString(CultureInfo.InvariantCulture));
            return null;
        }

        static string Signed(float v) => (v >= 0 ? "+" : "") + SCP_RelationshipAxes.Fmt(v);

        static void WriteOpinion(string iRoot, string iPersona, string iTarget, string iText, StringBuilder ioR)
        {
            bool aNew = SCP_RelationshipStore.WriteOpinion(iRoot, iPersona, iTarget, iText, SCP_RelationshipStore.NowIso(), out string p);
            ioR.Append(aNew
                ? "- 看法：`" + Path.GetFileName(p) + "`\n"
                : "- 看法：**內容與既有的一則完全相同，未重複寫入**（去重靠內容雜湊）\n");
        }

        static SCP_CmdResult? OpAddOpinion(SCP_CmdArgs iArgs, string iRoot, string iPersona, StringBuilder ioR, SCP_CmdResult ioRes)
        {
            string aRaw = RequireTarget(iArgs, out SCP_CmdResult? aFail);
            if (aFail != null) return aFail;
            string aText = iArgs.Get("opinion").Trim();
            if (aText.Length == 0) return SCP_CmdResult.Fail(2, "✗ add-opinion 需要 opinion（走 --arg-file）");
            string aTarget = SCP_RelationshipStore.CanonicalTarget(iRoot, aRaw);
            WriteOpinion(iRoot, iPersona, aTarget, aText, ioR);
            var c = SCP_RelationshipStore.RebuildCurrent(iRoot, iPersona, aTarget, true);
            ioRes.AddValue("target", aTarget);
            ioRes.AddValue("opinion_count", c.OpinionCount.ToString(CultureInfo.InvariantCulture));
            return null;
        }

        static SCP_CmdResult? OpShow(SCP_CmdArgs iArgs, string iRoot, string iPersona, StringBuilder ioR)
        {
            string aRaw = RequireTarget(iArgs, out SCP_CmdResult? aFail);
            if (aFail != null) return aFail;
            string aTarget = SCP_RelationshipStore.CanonicalTarget(iRoot, aRaw);
            // ⚠ show 是純讀：不釘 `_target.txt`
            string aDir = SCP_RelationshipStore.TargetDir(iRoot, iPersona, aTarget, false);
            string aCur = Path.Combine(aDir, SCP_Relationship.CurrentFileName);
            if (!File.Exists(aCur)) { ioR.Append("## （").Append(iPersona).Append(" 對 ").Append(aTarget).Append(" 還沒有任何紀錄）\n"); return null; }
            ioR.Append(File.ReadAllText(aCur, Encoding.UTF8)).Append('\n');
            string aOpDir = Path.Combine(aDir, SCP_RelationshipStore.OpinionsDirName);
            if (!Directory.Exists(aOpDir)) return null;
            string[] aFiles = Directory.GetFiles(aOpDir, "*.md");
            Array.Sort(aFiles, StringComparer.Ordinal);
            ioR.Append('\n').Append("## 看法（").Append(aFiles.Length).Append(" 則）\n");
            foreach (string f in aFiles)
            {
                string b = SCP_RelationshipStore.ReadBody(f);
                if (b.Length > 0) ioR.Append("- ").Append(b.Replace("\r\n", " ").Replace("\n", " ")).Append('\n');
            }
            return null;
        }

        static SCP_CmdResult? OpList(string iRoot, string iPersona, StringBuilder ioR)
        {
            string d = SCP_RelationshipStore.PersonaRelDir(iRoot, iPersona);
            if (!Directory.Exists(d)) { ioR.Append("（這位還沒有任何 relationship 資料）\n"); return null; }
            ioR.Append("| 對象 | 分數 | tier | 事件 | 看法 |\n|---|---|---|---|---|\n");
            string[] aDirs = Directory.GetDirectories(d);
            Array.Sort(aDirs, StringComparer.Ordinal);
            foreach (string t in aDirs)
            {
                (int s, string tier) = ReadCurrentBrief(Path.Combine(t, SCP_Relationship.CurrentFileName));
                ioR.Append("| ").Append(Path.GetFileName(t)).Append(" | ").Append(s).Append(" | ").Append(tier).Append(" | ")
                   .Append(CountMd(Path.Combine(t, SCP_RelationshipStore.EventsDirName))).Append(" | ")
                   .Append(CountMd(Path.Combine(t, SCP_RelationshipStore.OpinionsDirName))).Append(" |\n");
            }
            return null;
        }

        static SCP_CmdResult? OpRebuild(SCP_CmdArgs iArgs, string iRoot, string iPersona, StringBuilder ioR)
        {
            string aTarget = iArgs.Get("target").Trim();
            var aList = new List<string>();
            if (aTarget.Length > 0) aList.Add(SCP_RelationshipStore.CanonicalTarget(iRoot, aTarget));
            else
            {
                string d = SCP_RelationshipStore.PersonaRelDir(iRoot, iPersona);
                if (Directory.Exists(d))
                {
                    string[] aDirs = Directory.GetDirectories(d);
                    Array.Sort(aDirs, StringComparer.Ordinal);
                    // ⚠ target 名取 `_target.txt` 的主人（拿資料夾名的話，帶後綴的夾每跑一次就再疊一層）
                    foreach (string t in aDirs) aList.Add(SCP_RelationshipStore.OwnerOf(t) ?? Path.GetFileName(t));
                }
            }
            foreach (string t in aList)
            {
                var c = SCP_RelationshipStore.RebuildCurrent(iRoot, iPersona, t, true);
                ioR.Append("- ").Append(t).Append("：").Append(c.SurfaceScore).Append("（").Append(c.Tier).Append("）事件 ").Append(c.EventCount).Append(" 筆\n");
            }
            ioR.Append('\n').Append("共重建 ").Append(aList.Count).Append(" 份 `_current.md`。\n");
            return null;
        }

        static int CountMd(string d) => Directory.Exists(d) ? Directory.GetFiles(d, "*.md").Length : 0;

        static (int score, string tier) ReadCurrentBrief(string iPath)
        {
            if (!File.Exists(iPath)) return (0, "—");
            int s = 0; string t = "—";
            foreach (string ln in File.ReadAllLines(iPath, Encoding.UTF8))
            {
                if (ln.StartsWith("surface_score:", StringComparison.Ordinal)) int.TryParse(ln.Substring(14).Trim(), out s);
                else if (ln.StartsWith("tier:", StringComparison.Ordinal)) t = ln.Substring(5).Trim();
                else if (ln.StartsWith("event_count:", StringComparison.Ordinal)) break;
            }
            return (s, t);
        }
    }
}
