// 區塊職責：`cmd consolidate` —— 見林（longterm digest）與見森（forest fold）。**原生**，不需要 Unity。
// 物理意義：兩段式，跟寫信同一個分工：**工具負責持久化與算狀態，反思的內容 agent 自己寫**。
//           不給 body ＝ inspect（印狀態＋列本段待濃縮的信）；給了 body ＝ 寫檔。
//           ⇒ 工具代筆的見林不是那個人的記憶，它只是一份摘要（憲法⑥）。
// 數值影響：linzi 寫 `longterm/wake_XXX-YYY.md` ＋ 重建 `_index.md` ＋ 歸檔見叢；
//           forest 寫 `longterm/forest/gen_NNN_*.md` ＋ 重建見根索引。
//           **不碰 registry／profile 的任何欄位**（理由見 SCP_Consolidate 檔頭的血證）。
using System.Collections.Generic;
using System.IO;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Consolidate : SCP_Cmd
    {
        public override string Name => "consolidate";

        public override string Summary => "見林／見森：不給 digest_body ＝ 只列狀態與待濃縮信件；給了才寫檔";

        public override string Details =>
            "兩段式（工具持久化、反思由 agent 親筆）：\n"
            + "  ① inspect —— 不給 digest_body：印 gap／建議 span／本段待濃縮的信件清單\n"
            + "  ② write   —— 給 digest_body：寫見林 digest ＋ 重建 _index ＋ 歸檔當期見叢\n"
            + "level=forest 時折見森（門檻 " + SCP_WakeLetters.ForestDigestThreshold + " 份見林；"
            + "rolling fold 只讀上代森 ＋ 最新見林）。\n"
            + "⚠ 長內文一律走 --arg-file digest_body=<檔>：見林 body 動輒上萬字，不該經過 shell。\n"
            + "⛔ 本 Cmd **不寫任何 registry／profile 欄位** —— 書籤是掃磁碟算出來的（最大 span_end）。\n"
            + "   python 那支會順手存 registry，而那正是它會「檔寫成功卻 exit=1」的原因。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("consolidate --arg letters_root=D:/Unity/LY/AgentCommands/ChatTavern/baton/letters"
                                   + " --arg persona=Template");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("persona", "誰的記憶", iRequired: true),
            new SCP_CmdArgSpec("level", "linzi ＝ 見林（預設）／forest ＝ 見森",
                               iDefault: "linzi", iChoices: new[] { "linzi", "forest" }),
            new SCP_CmdArgSpec("digest_body", "濃縮本文（不給＝只列狀態）。長內文走 --arg-file"),
            // wake 不推導的理由跟 wake-brief 同一條：推導值線上／離線差一號，
            // 而差錯的那份見林**看起來完全正常**。不給就用推導值，但會把用了哪一條印出來。
            new SCP_CmdArgSpec("wake", "現在是第幾次醒來。不給＝推導（wakes/ 信數 + 1），並印出用了哪一條"),
            new SCP_CmdArgSpec("span_start", "見林起 wake#（不給＝上次濃縮的下一號）"),
            new SCP_CmdArgSpec("span_end", "見林迄 wake#（不給＝現在的 wake）"),
            new SCP_CmdArgSpec("threshold", "overdue 門檻",
                               iDefault: SCP_Consolidate.DefaultGapThreshold.ToString()),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aPersona = iArgs.Get("persona");
            string aBody = iArgs.Get("digest_body");
            bool aForest = iArgs.Get("level") == "forest";

            var aRoot = new SCP_LettersRoot(aLettersRoot);
            string aPersonaDir = SCP_LettersPaths.PersonaDir(aRoot, aPersona);
            if (!Directory.Exists(aPersonaDir))
                return SCP_CmdResult.Fail(1,
                    "✗ 找不到 persona 的信件夾：" + aPersonaDir,
                    "  （信件夾根：" + aLettersRoot + "）");

            return aForest
                ? RunForest(aLettersRoot, aPersona, aBody)
                : RunLinzi(iArgs, aLettersRoot, aPersona, aBody);
        }

        // ── 見林 ────────────────────────────────────────────────────

        SCP_CmdResult RunLinzi(SCP_CmdArgs iArgs, string iLettersRoot, string iPersona, string iBody)
        {
            int aWake = ParseInt(iArgs.Get("wake"), 0);
            int aThreshold = ParseInt(iArgs.Get("threshold"), SCP_Consolidate.DefaultGapThreshold);
            SCP_ConsolidateStatus aStatus = SCP_Consolidate.Status(iLettersRoot, iPersona, aWake, aThreshold);

            var aResult = new SCP_CmdResult();
            if (iBody.Length == 0)
            {
                aResult.Lines.Add("# 🧠 長期記憶整理狀態 — " + iPersona);
                aResult.Lines.Add("- wake_count: " + aStatus.WakeCount + "（" + aStatus.WakeCountSource + "）");
                aResult.Lines.Add("- last_consolidated_wake: " + aStatus.LastConsolidatedWake
                                  + " (@ " + (aStatus.LastConsolidatedAt.Length > 0
                                              ? aStatus.LastConsolidatedAt : "從未整理") + ")");
                aResult.Lines.Add("  ↳ 來源：掃 longterm/ 取最大 span_end（磁碟即事實，本 Cmd 不讀快取欄位）");
                aResult.Lines.Add("- gap: " + aStatus.Gap + " (門檻 " + aStatus.Threshold + ") → "
                                  + (aStatus.Overdue ? "⚠ OVERDUE 該整理" : "ok 尚未到門檻"));
                aResult.Lines.Add("- 建議 span: wake " + aStatus.SpanStart + "-" + aStatus.SpanEnd);
                aResult.Lines.Add("- 本段待濃縮 episodic letters (" + aStatus.PendingLetters.Count + " 封):");
                foreach (string aLetter in aStatus.PendingLetters) aResult.Lines.Add("  - " + aLetter);
                aResult.Lines.Add("");
                aResult.Lines.Add("→ 讀完上列信件後，反思濃縮成 digest body 寫回（長內文走檔案）：");
                aResult.Lines.Add("  " + SCP_CmdRegistry.Invoke(
                    "consolidate --arg letters_root=" + iLettersRoot + " --arg persona=" + iPersona
                    + " --arg-file digest_body=<檔> --arg span_start=" + aStatus.SpanStart
                    + " --arg span_end=" + aStatus.SpanEnd));
                aResult.AddValue("gap", aStatus.Gap.ToString());
                aResult.AddValue("overdue", aStatus.Overdue ? "1" : "0");
                aResult.AddValue("pending_letters", aStatus.PendingLetters.Count.ToString());
                return aResult;
            }

            int aSpanStart = ParseInt(iArgs.Get("span_start"), aStatus.SpanStart);
            int aSpanEnd = ParseInt(iArgs.Get("span_end"), aStatus.SpanEnd);
            if (aSpanEnd < aSpanStart)
                return SCP_CmdResult.Fail(2, "✗ span_end(" + aSpanEnd + ") < span_start(" + aSpanStart + ")",
                                          "  兩個都要給，或兩個都不給（不給＝用上面算出來的建議 span）");

            (string aPath, string aAt) = SCP_Consolidate.WriteDigest(
                iLettersRoot, iPersona, iBody, aSpanStart, aSpanEnd);
            aResult.Lines.Add("✅ 見林 digest 寫入: " + aPath);
            aResult.Lines.Add("   span: wake " + aSpanStart + "-" + aSpanEnd + "　consolidated_at: " + aAt);
            aResult.AddOutput(aPath);

            // 回讀：「我寫了」不是「它在裡面」。
            SCP_ConsolidateStatus aAfter = SCP_Consolidate.Status(iLettersRoot, iPersona, aWake, aThreshold);
            aResult.Lines.Add("   ↳ 回讀：last_consolidated_wake=" + aAfter.LastConsolidatedWake
                              + "　gap=" + aAfter.Gap);
            aResult.AddValue("gap", aAfter.Gap.ToString());

            string? aArchived = SCP_Consolidate.ArchiveKeys(iLettersRoot, iPersona, aSpanStart, aSpanEnd);
            aResult.Lines.Add(aArchived != null
                ? "   🌿 見叢已歸檔: " + aArchived + "（當期檔已重置）"
                : "   🌿 當期見叢沒有檔案 ⇒ 沒有東西可歸檔（不是錯誤）");
            if (aArchived != null) aResult.AddOutput(aArchived);

            SCP_ForestStatus aForest = SCP_Consolidate.ForestStatus(iLettersRoot, iPersona);
            aResult.Lines.Add("");
            aResult.Lines.Add(aForest.Overdue
                ? "   🌲 見森: 見林 " + aForest.DigestCount + " 份，已折到第 "
                  + aForest.FoldedDigestCount + " 份 → **有新見林待折**（--arg level=forest）"
                : "   🌲 見森: 見林 " + aForest.DigestCount + "/" + aForest.Threshold
                  + " 份　" + (aForest.Eligible ? "✓ 已是最新" : "未達折疊門檻"));
            aResult.AddValue("forest_overdue", aForest.Overdue ? "1" : "0");
            return aResult;
        }

        // ── 見森 ────────────────────────────────────────────────────

        SCP_CmdResult RunForest(string iLettersRoot, string iPersona, string iBody)
        {
            SCP_ForestStatus aStatus = SCP_Consolidate.ForestStatus(iLettersRoot, iPersona);
            var aResult = new SCP_CmdResult();

            if (iBody.Length == 0)
            {
                aResult.Lines.Add("# 🌲 見森狀態 — " + iPersona);
                aResult.Lines.Add("- 見林份數: " + aStatus.DigestCount + " (門檻 " + aStatus.Threshold + " 份)");
                aResult.Lines.Add("- 已折世代: gen" + aStatus.ForestCount
                                  + " (折到第 " + aStatus.FoldedDigestCount + " 份見林)");
                if (!aStatus.Eligible)
                {
                    aResult.Lines.Add("- 狀態: ○ 未達門檻，還差 "
                                      + (aStatus.Threshold - aStatus.DigestCount) + " 份見林");
                    aResult.AddValue("eligible", "0");
                    return aResult;
                }
                aResult.Lines.Add("- 狀態: " + (aStatus.Overdue
                    ? "⚠ 有 " + aStatus.Pending + " 份新見林待折" : "✓ 已是最新"));
                if (aStatus.ForestCount == 0)
                {
                    // 首折是唯一的多輸入折疊；之後恆為 2 份 ⇒ 成本不隨壽命成長。
                    aResult.Lines.Add("- **首折**（唯一的多輸入折疊）→ 讀下列全部見林:");
                    foreach (string aDigest in aStatus.Digests) aResult.Lines.Add("    - " + aDigest);
                }
                else
                {
                    aResult.Lines.Add("- rolling fold → 只讀 2 份輸入:");
                    aResult.Lines.Add("    - 上代森: " + aStatus.LatestForest);
                    aResult.Lines.Add("    - 新見林: " + aStatus.Digests[aStatus.Digests.Count - 1]);
                }
                aResult.Lines.Add("");
                aResult.Lines.Add("→ 讀完後寫回（森是**縱向敘事 + fragment 索引指標**，不是見林的串接）:");
                aResult.Lines.Add("  " + SCP_CmdRegistry.Invoke(
                    "consolidate --arg letters_root=" + iLettersRoot + " --arg persona=" + iPersona
                    + " --arg level=forest --arg-file digest_body=<檔>"));
                aResult.AddValue("eligible", "1");
                aResult.AddValue("forest_overdue", aStatus.Overdue ? "1" : "0");
                return aResult;
            }

            if (!aStatus.Eligible)
                return SCP_CmdResult.Fail(2,
                    "✗ 見林只有 " + aStatus.DigestCount + " 份，未達見森門檻 " + aStatus.Threshold + " 份",
                    "  先把見林折滿門檻 —— 森是折林的產物，沒有林就沒有森");

            string aPath = SCP_Consolidate.WriteForest(iLettersRoot, iPersona, iBody);
            aResult.Lines.Add("✅ 見森 gen" + aStatus.NextGen + " 寫入: " + aPath);
            aResult.Lines.Add("   folded_digest_count: " + aStatus.DigestCount + "（舊世代全保留，append-only）");
            aResult.AddOutput(aPath);
            aResult.AddValue("generation", aStatus.NextGen.ToString());

            // 見森之後重建見根索引（python 同一條連動）——碎片可能在折林時被抽過。
            string? aIndex = SCP_Fragments.WriteRootIndex(iLettersRoot, iPersona);
            if (aIndex != null)
            {
                aResult.Lines.Add("   見根索引已重建: " + aIndex);
                aResult.AddOutput(aIndex);
            }
            else
            {
                aResult.Lines.Add("   （沒有 fragment ⇒ 未建見根索引）");
            }
            return aResult;
        }

        static int ParseInt(string iRaw, int iFallback)
            => int.TryParse(iRaw, out int aValue) ? aValue : iFallback;
    }
}
