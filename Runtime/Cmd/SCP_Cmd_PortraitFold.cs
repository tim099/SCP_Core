// 區塊職責：`cmd portrait-fold` —— 見人濃縮的 Cmd 介面（**原生**，不需要 Unity）。
// 物理意義：邏輯在 SCP_PortraitConsolidate，本檔只做「參數 → 呼叫 → 回報」。
// 數值影響：寫一個版本檔 ＋ 搬 N 幅畫像進 `raw/`（只搬不刪）。被守衛擋下時一個字都不寫。
//
// ⚠ 為什麼是**獨立一支**而不是掛在 `cmd consolidate`（見林）順手做：
//   見林那支至今有「寫入成功卻 exit=1」的活體（awakening.py consolidate --level linzi），
//   掛進去會讓「濃縮到底有沒有寫」多一層混淆。
//   📌 這一格是 basecamp 的判斷，**Tim 尚未拍板** —— 要改就改，改完同步 TASK-0097 條文。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System.Collections.Generic;
using System.IO;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_PortraitFold : SCP_Cmd
    {
        public override string Name => "portrait-fold";

        public override string Summary => "見人濃縮：折一版 `<target>_vNNN.md`，並把逐幅畫像搬進 `raw/`（只搬不刪）";

        public override string Details =>
            "新的一版 ＝ 前一版 ＋ 這段期間的新畫像（rolling fold）。`body` **必須親筆** ——\n"
            + "見人是判斷不是統計，工具代筆的看法不是妳的。\n"
            + "三道守衛都是擋下來不是幫你修：①目錄名大小寫變體 ②同一個 wake_range 想再寫一版\n"
            + "③根層沒有未歸檔畫像（沒有輸入）。\n"
            + "⚠ **一幅也折**（Tim 2026-09-01 拍板：見林時把根層未歸檔的全折完，複製沒錯）——\n"
            + "   舊的 `allow_single` 旗標已移除，帶了會被參數預檢擋下（大聲失敗優於靜默忽略）。\n"
            + "⚠ 順序：**先寫成功、才搬檔** —— 反過來的話寫入失敗時那個人會從 §6.5 消失而且沒有紅燈。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("portrait-fold --arg letters_root=<root> --arg persona=Template"
                                   + " --arg target=summit --arg wake_range=33-49 --arg-file body=<檔>");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("persona", "誰的 sketchbook（＝誰的看法）", iRequired: true),
            new SCP_CmdArgSpec("target", "折對誰的看法。⚠ 用 canonical id；大小寫變體會被擋", iRequired: true),
            new SCP_CmdArgSpec("wake_range", "本版**在哪個 wake 區間折的**，例如 `33-49`（不是素材產出區間）", iRequired: true),
            new SCP_CmdArgSpec("body", "濃縮內文（**親筆**）。長文走 --arg-file", iRequired: true),
            new SCP_CmdArgSpec("by", "wake 編號的主體（不給＝persona 自己）—— 數字要帶著定語走"),
            new SCP_CmdArgSpec("no_archive", "1 ＝ 只寫版本檔、**不搬** raw（讀取端還沒上線時的過渡用法）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aPersona = iArgs.Get("persona");
            string aTarget = iArgs.Get("target");
            string aRange = iArgs.Get("wake_range");
            string aBody = iArgs.Get("body");
            string aBy = iArgs.Get("by");
            bool aArchive = iArgs.Get("no_archive") != "1";

            string aPersonaDir = SCP_LettersPaths.PersonaDir(new SCP_LettersRoot(aLettersRoot), aPersona);
            if (!Directory.Exists(aPersonaDir))
                return SCP_CmdResult.Fail(1,
                    "✗ 找不到 persona 的信件夾：" + aPersonaDir,
                    "  （信件夾根：" + aLettersRoot + "）");

            SCP_ConsolidateResult aRes = SCP_PortraitConsolidate.Run(
                aLettersRoot, aPersona, aTarget, aRange, aBody,
                aBy.Length > 0 ? aBy : aPersona, aArchive);

            if (aRes.Blocked != null)
            {
                // 被擋是**正常出口**不是崩潰：exit 2（用法／狀態不合），訊息自己帶怎麼解。
                var aBlocked = SCP_CmdResult.Fail(2, aRes.Blocked);
                aBlocked.AddValue("blocked", "1");
                return aBlocked;
            }

            var aResult = new SCP_CmdResult();
            aResult.Lines.Add("✅ 折出 v" + aRes.Version + "：`" + aRes.Path + "`（回讀 " + aRes.WrittenLines + " 行）");
            aResult.Lines.Add("· 輸入 " + aRes.Inputs.Count + " 幅：" + string.Join(" / ", aRes.Inputs));
            if (!aArchive)
            {
                aResult.Lines.Add("· ⚠ 帶了 `no_archive=1` ⇒ 畫像**留在根層沒搬**（版本檔已寫）");
            }
            else
            {
                aResult.Lines.Add("· 搬進 raw/：" + aRes.Archived.Count + " 幅");
                foreach (string aFail in aRes.ArchiveFailures)
                    aResult.Lines.Add("  ⚠ 未搬：" + aFail);
            }
            if (aRes.WrittenLines == 0)
            {
                // 回讀 0 行＝檔案在但讀不到；不准當成功收工。
                aResult.Lines.Add("✗ 回讀不到內容（0 行）—— 寫入回報成功但檔案讀不出來，去看那個路徑。");
                aResult.ExitCode = 1;
            }
            aResult.AddValue("version", aRes.Version.ToString());
            aResult.AddValue("inputs", aRes.Inputs.Count.ToString());
            aResult.AddValue("archived", aRes.Archived.Count.ToString());
            aResult.AddValue("archive_failures", aRes.ArchiveFailures.Count.ToString());
            aResult.AddOutput(aRes.Path);
            return aResult;
        }
    }
}
