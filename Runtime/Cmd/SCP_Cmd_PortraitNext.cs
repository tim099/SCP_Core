// 區塊職責：`cmd portrait-next` —— 見人折人的**分步驅動**（原生，不需要 Unity）。
// 物理意義：形狀照早安四步（Tim 2026-09-01）：**每次跑一位，回傳檔自己指出下一步** ——
//           Cmd 挑下一個要折的對象、把折那一版需要的材料合併成一份可讀的檔（brief 的概念），
//           人只負責寫親筆內文。還有下一位就在結尾提示續跑；沒有了就提示完成。
//           ⇒ 「折完了嗎」不靠記得：清單空掉才會印完成，而那一行是機器印的。
// 數值影響：**純讀 ＋ 寫一份回傳檔**（`cmd/portrait_next.md`）。不動 sketchbook 一個位元組。
//
// ⚠ 為什麼材料要「合併輸出」而不是叫人自己去讀那幾個檔：
//   rolling fold 的輸入是「前一版 ＋ 這期全部未歸檔畫像」，少讀一幅就是憑印象補那一格 ——
//   而憑印象補出來的濃縮，跟讀完材料寫出來的，從檔案上看**一模一樣**。
//   ⇒ 把材料端到面前是唯一擋得住那件事的做法（同 wake brief 的存在理由）。
// ⚠ 挑人順序：**未歸檔幅數降冪、同數按名字** —— 刻意不是隨機也不是時間序：
//   幅數多的那位最花力氣，留到最後會撞上「我已經折了五個、這個下次再說」。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_PortraitNext : SCP_Cmd
    {
        public override string Name => "portrait-next";

        public override string Summary => "見人折人分步：挑下一位、把材料合併成一份檔、指出下一步（跑到清單空為止）";

        public override string Details =>
            "每次跑一位：挑出未歸檔幅數最多的對象 → 把「前一版濃縮 ＋ 這期全部未歸檔畫像（全文）」\n"
            + "合併寫進 `cmd/portrait_next.md` → 妳讀那份、寫親筆內文 → 跑 `portrait-fold`。\n"
            + "折完那位之後再跑本 Cmd 一次；**還有下一位就提示續跑，沒有了就提示完成**。\n"
            + "⚠ 本 Cmd 不寫 sketchbook、不折任何東西 —— 它只是把材料端到面前並算出還剩幾位。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("portrait-next --arg letters_root=<root> --arg persona=Template"
                                   + " --arg wake_range=33-49");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("persona", "誰在折（讀誰的 sketchbook）", iRequired: true),
            new SCP_CmdArgSpec("wake_range", "這一輪折的時點區間（例如 `33-49`）—— 會照抄進下一步的指令列"),
            new SCP_CmdArgSpec("target", "指定要折誰（不給＝由本 Cmd 挑未歸檔最多的那位）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aPersona = iArgs.Get("persona");
            string aRange = iArgs.Get("wake_range").Trim();
            string aWanted = iArgs.Get("target").Trim();

            var aRoot = new SCP_LettersRoot(aLettersRoot);
            string aPersonaDir = SCP_LettersPaths.PersonaDir(aRoot, aPersona);
            if (!Directory.Exists(aPersonaDir))
                return SCP_CmdResult.Fail(1,
                    "✗ 找不到 persona 的信件夾：" + aPersonaDir,
                    "  （信件夾根：" + aLettersRoot + "）");

            // ── 待折清單（與 `cmd people --arg pending=1` 同一個算法）──
            var aPending = new List<SCP_PortraitTargetView>();
            foreach (string aOne in SCP_PortraitView.Targets(aLettersRoot, aPersona))
            {
                SCP_PortraitTargetView aView = SCP_PortraitView.Build(aLettersRoot, aPersona, aOne);
                if (aView.UnarchivedPaths.Count > 0) aPending.Add(aView);
            }
            aPending.Sort((a, b) =>
            {
                int aCmp = b.UnarchivedPaths.Count.CompareTo(a.UnarchivedPaths.Count);   // 幅數降冪
                return aCmp != 0 ? aCmp : string.Compare(a.Target, b.Target, StringComparison.OrdinalIgnoreCase);
            });

            int aTotalPortraits = 0;
            foreach (SCP_PortraitTargetView aView in aPending) aTotalPortraits += aView.UnarchivedPaths.Count;

            var aResult = new SCP_CmdResult();
            string aOutPath = SCP_LettersPaths.CmdPayload(aRoot, aPersona, "portrait", "next");

            // ── 沒有下一位 ⇒ 完成（這一行是機器印的，不是我宣告的）──
            if (aPending.Count == 0)
            {
                var aDone = new List<string>
                {
                    "# ✅ 折人完成 — " + aPersona,
                    "",
                    "待折清單已空：sketchbook 根層一幅未歸檔畫像都沒有。",
                    "",
                    "## next",
                    "（沒有下一位 —— 這一輪折人結束。要覆核就跑 `cmd people --arg pending=1`，"
                    + "它應該回 `pending_targets = 0`。）",
                    "",
                };
                WritePayload(aOutPath, aPersona, aDone, aResult);
                aResult.Lines.Add("✅ 沒有待折的對象了 —— 這一輪折人完成。");
                aResult.AddValue("remaining_targets", "0");
                aResult.AddValue("remaining_portraits", "0");
                aResult.AddValue("done", "1");
                return aResult;
            }

            // ── 挑人 ──
            SCP_PortraitTargetView? aPick = null;
            if (aWanted.Length > 0)
            {
                foreach (SCP_PortraitTargetView aView in aPending)
                    if (string.Equals(aView.Target, aWanted, StringComparison.OrdinalIgnoreCase)) aPick = aView;
                if (aPick == null)
                    return SCP_CmdResult.Fail(2,
                        "✗ `" + aWanted + "` 不在待折清單上（可能已經折完，或根層沒有他的未歸檔畫像）。",
                        "  ⇒ 不指定 target 就讓本 Cmd 挑，或先看 `cmd people --arg pending=1`。");
            }
            else
            {
                aPick = aPending[0];
            }

            var aLines = BuildMaterial(aLettersRoot, aPersona, aPick!, aPending, aTotalPortraits, aRange);
            WritePayload(aOutPath, aPersona, aLines, aResult);

            aResult.Lines.Add("🪵 這一位：**" + aPick!.Target + "**（未歸檔 " + aPick.UnarchivedPaths.Count
                              + " 幅"
                              + (aPick.Latest != null ? "・前一版 v" + aPick.Latest.Version : "・尚無前一版")
                              + "）");
            aResult.Lines.Add("· 材料已合併 → `" + aOutPath + "`（"
                              + "剩 " + aPending.Count + " 位／" + aTotalPortraits + " 幅）");
            aResult.Lines.Add("· 下一步：Read 那份檔 → 寫親筆內文 → `cmd portrait-fold`（指令列在檔尾）");
            aResult.AddValue("target", aPick.Target);
            aResult.AddValue("target_portraits", aPick.UnarchivedPaths.Count.ToString());
            aResult.AddValue("remaining_targets", aPending.Count.ToString());
            aResult.AddValue("remaining_portraits", aTotalPortraits.ToString());
            aResult.AddValue("done", "0");
            aResult.AddOutput(aOutPath);
            return aResult;
        }

        static List<string> BuildMaterial(string iLettersRoot, string iPersona,
                                          SCP_PortraitTargetView iPick,
                                          List<SCP_PortraitTargetView> iPending,
                                          int iTotalPortraits, string iWakeRange)
        {
            string aTarget = iPick.Target;
            var aOut = new List<string>
            {
                "# 🪵 折人材料 — " + iPersona + " → **" + aTarget + "**",
                "",
                "> 讀這一份就夠寫這一版：**前一版濃縮 ＋ 這期全部未歸檔畫像（全文）**。",
                "> 🩸 少讀一幅就是憑印象補那一格 —— 而憑印象補出來的濃縮，"
                + "跟讀完材料寫出來的，在檔案上長得一模一樣。",
                "",
                "- 這一位：**" + aTarget + "**　未歸檔 **" + iPick.UnarchivedPaths.Count + " 幅**"
                + (iPick.Latest != null
                   ? "　前一版 **v" + iPick.Latest.Version + "**（"
                     + (iPick.Latest.WakeRange.Length > 0 ? iPick.Latest.WakeRange : "區間不明")
                     + "）⇒ 這一版是 **v" + (iPick.Latest.Version + 1) + "**"
                   : "　**尚無前一版** ⇒ 這一版是 **v1**"),
                "- 這一輪還剩：**" + iPending.Count + " 位 / " + iTotalPortraits + " 幅**"
                + "（含這一位；折完再跑一次 `portrait-next`）",
                "",
            };

            // 關係（指路用，⛔ 不要抄進濃縮檔 —— 分數由事件重算，抄一份就是第二個真相源）
            SCP_RelationshipSet aRel = SCP_Relationship.Load(iLettersRoot, iPersona);
            SCP_RelationshipEntry? aEntry = aRel.Find(aTarget);
            aOut.Add("## 🤝 關係現況（指路用，⛔ 不要抄進濃縮檔）");
            aOut.Add("");
            if (aEntry == null)
            {
                aOut.Add(aRel.LoadError != null
                         ? "> ⚠ 關係讀取失敗（" + aRel.LoadError + "）—— 這不代表沒有關係紀錄。"
                         : "> （還沒有 " + aTarget + " 的關係紀錄）");
            }
            else
            {
                aOut.Add("- 好感 **" + (aEntry.ScoreParsed ? aEntry.SurfaceScore.ToString() : "?") + "**"
                         + (aEntry.Tier.Length > 0 ? "（" + aEntry.Tier + "）" : "")
                         + "　事件帳本：`relationship/" + aTarget + "/`");
                int aFrom = Math.Max(0, aEntry.Opinions.Count - 3);
                for (int i = aFrom; i < aEntry.Opinions.Count; i++)
                    aOut.Add("    · " + aEntry.Opinions[i].Replace("\r\n", " ").Replace("\n", " ").Trim());
            }
            aOut.Add("");

            // 前一版全文（rolling fold 的另一半輸入）
            aOut.Add("## ⚓ 前一版濃縮"
                     + (iPick.Latest != null ? "（`" + Path.GetFileName(iPick.Latest.Path) + "`，全文）" : ""));
            aOut.Add("");
            if (iPick.Latest == null)
            {
                aOut.Add("（沒有前一版 —— 這是第一版，輸入只有下面那幾幅畫像）");
            }
            else
            {
                aOut.AddRange(SCP_LetterText.DemoteHeadings(ReadBody(iPick.Latest.Path)));
            }
            aOut.Add("");

            // 這期未歸檔畫像全文（新 → 舊）
            aOut.Add("## 🖼 這期未歸檔畫像 " + iPick.UnarchivedPaths.Count + " 幅（新 → 舊・全文）");
            aOut.Add("");
            foreach (string aPath in iPick.UnarchivedPaths)
            {
                string aAt = SCP_LetterText.ReadFrontmatterField(aPath, "at");
                string aHeadline = SCP_LetterText.ReadFrontmatterField(aPath, "headline");
                aOut.Add("### 📅 " + (aAt.Length >= 10 ? aAt.Substring(0, 10) : "日期不明")
                         + "　`" + Path.GetFileName(aPath) + "`"
                         + (aHeadline.Length > 0 ? "　" + aHeadline : ""));
                aOut.Add("");
                aOut.AddRange(SCP_LetterText.DemoteHeadings(ReadBody(aPath)));
                aOut.Add("");
            }

            // 下一步 —— 還有下一位就提示續跑，沒有就提示完成（Tim 2026-09-01 指定的形狀）
            string aRangeArg = iWakeRange.Length > 0 ? iWakeRange : "<折的時點區間>";
            aOut.Add("## next");
            aOut.Add("");
            aOut.Add("1. **required** — 寫親筆內文（新版 ＝ 前一版 ＋ 上面那幾幅；"
                     + "工具代筆的看法不是妳的），存成一個檔");
            aOut.Add("2. **required** — 折這一版：");
            aOut.Add("   ```");
            aOut.Add("   senate cmd portrait-fold --arg letters_root=<root> --arg persona=" + iPersona);
            aOut.Add("       --arg target=" + aTarget + " --arg wake_range=" + aRangeArg
                     + " --arg by=" + iPersona + " --arg-file body=<妳寫的那個檔>");
            aOut.Add("   ```");
            aOut.Add("3. **required** — 回讀確認（不要信回傳的 ✓）：");
            aOut.Add("   `senate cmd people --arg letters_root=<root> --arg persona=" + iPersona
                     + " --arg target=" + aTarget + "`");
            if (iPending.Count > 1)
            {
                var aRest = new List<string>();
                foreach (SCP_PortraitTargetView aView in iPending)
                {
                    if (string.Equals(aView.Target, aTarget, StringComparison.OrdinalIgnoreCase)) continue;
                    aRest.Add(aView.Target + "（" + aView.UnarchivedPaths.Count + " 幅）");
                }
                aOut.Add("4. **required** — **還有 " + aRest.Count + " 位**："
                         + string.Join(" / ", aRest));
                aOut.Add("   ⇒ 折完這位就再跑一次：`senate cmd portrait-next --arg letters_root=<root>"
                         + " --arg persona=" + iPersona
                         + (iWakeRange.Length > 0 ? " --arg wake_range=" + iWakeRange : "") + "`");
                aOut.Add("   ⚠ **一幅也折**（Tim 2026-09-01 拍板）—— 清單空掉才算折完，"
                         + "不是「我覺得重要的都折了」。");
            }
            else
            {
                aOut.Add("4. **這是最後一位** —— 折完再跑一次 `portrait-next`，"
                         + "它會印「折人完成」（那一行是機器印的，不是妳宣告的）。");
            }
            aOut.Add("");
            return aOut;
        }

        static List<string> ReadBody(string iPath)
        {
            try
            {
                string aText = SCP_LetterText.StripFrontmatter(File.ReadAllText(iPath)).Trim();
                return new List<string>(aText.Replace("\r\n", "\n").Split('\n'));
            }
            catch (Exception e)
            {
                // 讀不到就把原因寫成內容 —— 一段空白會被讀成「這幅沒什麼可說的」。
                return new List<string> { "（讀不到：" + e.GetType().Name + ": " + e.Message + "）" };
            }
        }

        static void WritePayload(string iPath, string iPersona, List<string> iLines, SCP_CmdResult iResult)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iPath)!);
                var aSb = new StringBuilder();
                aSb.Append("---\ntype: portrait_next\npersona: ").Append(iPersona)
                   .Append("\ngenerated: mechanical   # 每次跑都重生成 — 手改會被覆寫\n---\n\n");
                foreach (string aLine in iLines) aSb.Append(aLine).Append('\n');
                File.WriteAllText(iPath, aSb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                iResult.Lines.Add("✗ 回傳檔寫不進去：" + e.GetType().Name + ": " + e.Message);
                iResult.ExitCode = 1;
            }
        }
    }
}
