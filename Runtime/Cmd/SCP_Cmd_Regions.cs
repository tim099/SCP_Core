// 區塊職責：`cmd regions` —— 印出「有哪幾個區、各自對應哪條 ref、那條 ref 有多新」（**原生純讀**）。
// 物理意義：酒館 seq 有兩個軸，而**沒有任何一則訊息在自己身上寫著它屬於哪一區**。
//           這支是 `cmd msg` 的前置：要引用另一區的號，得先知道區叫什麼、它讀的是哪個 ref 的哪一刻。
// 數值影響：零寫入。只跑 git 的唯讀指令（for-each-ref / show / log），不 fetch、不 checkout。
//
// ⚠ 清單為空**不等於**「這個 repo 沒有分區」—— 也可能是掃描範圍裡沒有 remote ref（沒 fetch 過的 clone）。
//   兩種意思不合成一句，掃描範圍一律印出來。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
// @doc-sync: ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md（§2.2.0 跨區讀一則）
// @doc-sync: ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Internals/Cmd_Tavern_Internals.md（§1.2.1 seq 每條分支一套）
// @doc-sync: ucl_core:Docs~/{lang}/Workflows/Work_Memory_Workflow.md（常見坑 6：記憶裡的酒館引用要帶定語）
// @doc-sync: <Senate>/Docs/API/Cli_Reference.md（`cmd` 節「跨區讀酒館訊息」）
#nullable enable
using System.Collections.Generic;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Regions : SCP_Cmd
    {
        public override string Name => "regions";

        public override string Summary => "列出酒館的「區」（seq 軸）：區名 → ref → 那條 ref 的 tip 有多新";

        public override string Details =>
            "⭐ **哪些分支是區由分支自報**，不另外維護對照表：該 ref 的 `Treasury/bank_settings.json`\n"
            + "   有 `currency_id` 才算一區（已量：`origin/main → BTC`、`origin/LY → Florin`；\n"
            + "   `Bar` / `Dev` / `RingWorld` 沒有那個檔 ⇒ 自動不在清單裡）。\n"
            + "⚠ tip 是**上次 fetch 的快照**，不是遠端此刻 —— 本 Cmd **不自動 fetch**（讀取工具不偷連網）。\n"
            + "⚠ 清單為空不等於「沒有分區」：也可能是這個 clone 沒有 remote ref。掃描範圍一律印出來。";

        public override string Example => SCP_CmdRegistry.Invoke("regions");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑，＝那個 git repo 的根）"
                + "—— senate CLI 沒給時用「路徑管理」頁那一格補上並印出來", iRequired: true),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            var aRepo = new SCP_DataRoot(iArgs.Get("data_root"));
            var aProblems = new List<string>();
            List<SCP_TavernRegionInfo> aRegions = SCP_TavernRegion.ListRegions(aRepo, aProblems);

            var aResult = SCP_CmdResult.Success();
            aResult.Lines.Add("## 酒館的區（seq 軸）　repo=" + aRepo.Value);
            if (aRegions.Count == 0)
            {
                aResult.Lines.Add("・**一個都沒掃到** —— 這不等於「沒有分區」，見下面的掃描範圍");
            }
            for (int i = 0; i < aRegions.Count; ++i)
            {
                SCP_TavernRegionInfo aInfo = aRegions[i];
                aResult.Lines.Add("・" + Pad(aInfo.Region, 10) + " → " + Pad(aInfo.Ref, 16)
                                  + "　tip " + aInfo.TipSha + " · " + aInfo.TipTime);
            }
            for (int i = 0; i < aProblems.Count; ++i) aResult.Lines.Add("⚠ " + aProblems[i]);

            aResult.Lines.Add("· 掃描範圍：" + SCP_TavernRegion.RemotePrefix
                              + "　判準＝該 ref 有 `Treasury/bank_settings.json` 的 `currency_id`");
            aResult.Lines.Add("· ⛔ 不自動 fetch ⇒ 上面的 tip 是上次 fetch 的快照，不是遠端此刻");
            aResult.Lines.Add("· 讀某一區的訊息：" + SCP_CmdRegistry.Invoke(
                "msg --arg region=<區名> --arg seq=<號>"));
            aResult.AddValue("regions", aRegions.Count.ToString());
            aResult.AddValue("problems", aProblems.Count.ToString());
            return aResult;
        }

        static string Pad(string iText, int iWidth)
            => iText.Length >= iWidth ? iText : iText + new string(' ', iWidth - iText.Length);
    }
}
