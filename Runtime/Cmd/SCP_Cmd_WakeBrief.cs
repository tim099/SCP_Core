// 區塊職責：**早安信件讀取的 Cmd 介面** —— 把 SCP_WakeBrief 掛上 SCP_CMD。
// 物理意義：組裝邏輯在 SCP_WakeBrief（Runtime/Letters），本檔只做「參數 → 呼叫 → 回報」。
//           ⇒ 換宿主（CLI / 視窗 / 別的 process）不必動邏輯；換邏輯不必動每個宿主。
// 數值影響：不給 out_dir ＝ 純讀（只回行數摘要）；給了才寫檔。
//
// ⚠ 這支**不替人推導 wake**：實測 basecamp 的 `wakes/` 有 78 封而 wake_count 是 79
//   （這一次的信還沒寫）。推導出來的數字會每天差一，而且一路正常地印在標題上。
//   ⇒ 沒給就是 0，而 0 印在標題上一眼看得出「這格沒人給」。
using System.Collections.Generic;
using System.IO;
using SCP.Core.Letters;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_WakeBrief : SCP_Cmd
    {
        public override string Name => "wake-brief";

        public override string Summary => "讀 persona 信件庫組一份 wake brief（憲法／見叢／見森／見林／見樹）";

        public override string Details =>
            "⚠ 射程：只含**信件讀取層**。python `wake_brief.py` 還有見根／回憶／記憶維護狀態／\n"
            + "見人／見書／今日動作清單，那些依賴信件庫以外的子系統，**沒有移植**。\n"
            + "⇒ 這份輸出與 python 那份不是同一份東西，不要拿其中一份當另一份的驗收。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("wake-brief --arg letters_root=D:/Unity/Bar/AgentCommands/ChatTavern/baton/letters"
                                   + " --arg persona=Template --arg wake=4");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("persona", "要讀誰的信件庫", iRequired: true),
            new SCP_CmdArgSpec("wake", "這次是第幾次醒來（印在標題上）。不給＝0，本 Cmd 不替你推導"),
            new SCP_CmdArgSpec("data_root", "資料根（給了才數缺陷單）。不給＝§6 那行寫「未量」，不印 0"),
            new SCP_CmdArgSpec("out_dir", "落檔目錄（wake_brief.md / wake_brief_part2.md）。不給＝只回摘要不寫檔"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aPersona = iArgs.Get("persona");
            string aOutDir = iArgs.Get("out_dir");
            string aDataRoot = iArgs.Get("data_root");
            int aWake = iArgs.GetInt("wake", 0, out string aWakeWhy);

            string aPersonaDir = SCP_WakeLetters.PersonaDir(aLettersRoot, aPersona);
            if (!Directory.Exists(aPersonaDir))
            {
                // 「這個人不存在」與「信件夾根設錯」是兩件事 —— 兩條路徑都印出來讓人自己分辨。
                return SCP_CmdResult.Fail(1,
                    "✗ 找不到 persona 的信件夾：" + aPersonaDir,
                    "  （信件夾根：" + aLettersRoot + "）");
            }

            SCP_WakeBriefResult aBrief;
            string? aWrittenTo = null;
            if (aOutDir.Length > 0)
            {
                (string aPath, SCP_WakeBriefResult aRes) =
                    SCP_WakeBrief.Write(aLettersRoot, aPersona, aWake, aOutDir,
                                        aDataRoot.Length > 0 ? aDataRoot : null);
                aBrief = aRes;
                aWrittenTo = aPath;
            }
            else
            {
                aBrief = SCP_WakeBrief.Build(aLettersRoot, aPersona, aWake,
                                             aDataRoot.Length > 0 ? aDataRoot : null);
            }

            var aResult = new SCP_CmdResult();
            if (aWakeWhy.Length > 0) aResult.Lines.Add("⚠ " + aWakeWhy);
            // 自癒可以安靜地做，但不能安靜地發生。
            if (aBrief.LatestPointerHealed)
                aResult.Lines.Add("🔧 _latest.md 落後，已校正為目錄內最新的自寫 letter（persona=" + aPersona + "）");

            aResult.Lines.Add("· 信件夾：" + aPersonaDir);
            aResult.Lines.Add("· 主檔 " + aBrief.MainLineCount + " 行 / 上限 " + SCP_WakeBrief.BriefLineCap
                              + (aBrief.Part2 != null ? "　（有續讀檔）" : ""));
            if (aBrief.MovedSections.Count > 0)
                aResult.Lines.Add("· 移進續讀檔：" + string.Join(" , ", aBrief.MovedSections));
            if (aWrittenTo == null)
                aResult.Lines.Add("（沒給 out_dir ⇒ 只回摘要不落檔）");

            aResult.AddValue("main_lines", aBrief.MainLineCount.ToString());
            aResult.AddValue("has_part2", aBrief.Part2 != null ? "1" : "0");
            aResult.AddValue("latest_pointer_healed", aBrief.LatestPointerHealed ? "1" : "0");
            if (aWrittenTo != null) aResult.AddOutput(aWrittenTo);
            return aResult;
        }
    }
}
