// 區塊職責：手動收尾信複製遷移入口；根由宿主提供，不登入目標 persona。
// 數值影響：預設唯讀；confirm=1 才寫 wakes/，原檔不動。
#nullable enable
using System.Collections.Generic;
using SCP.Core.Letters;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_LettersMigrate : SCP_LocalRootsCmd
    {
        public override string Name => "letters-migrate";
        public override string Category => SCP_CmdCategory.Memory;
        public override string Summary => "舊收尾信複製到 wakes/ 並加六位數序號；預設只試算，原檔不動";
        public override string Details =>
            "只掃 persona 頂層非底線開頭的 *.md，第一層 frontmatter 的 trigger 必須恰為 cmd_goodnight。\n"
            + "按原檔名排序，複製為 wakes/000001_<原檔名>；重跑驗 SHA-256，同名內容不同就擋、不覆寫。\n"
            + "confirm=1 才執行；在線 persona 不可遷移。這是手動指令，早安不自動觸發。";
        public override string Example => SCP_CmdRegistry.InvokeOf<SCP_Cmd_LettersMigrate>("--arg persona=pinnacle --arg confirm=1");
        protected override string CliNextHint => "";
        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new List<SCP_CmdArgSpec>
        {
            new SCP_CmdArgSpec("persona", "要遷移的 persona（顯式指定）", iRequired: true),
            new SCP_CmdArgSpec("confirm", "1 才複製；預設只試算", iDefault: "0", iChoices: new[] { "0", "1" }),
        };

        protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
        {
            SCP_LettersMigrationResult aResult = SCP_LettersMigration.Run(iRoots.Letters,
                iArgs.Get("persona").Trim(), iArgs.Get("confirm") == "1");
            ioResult.Lines.AddRange(aResult.Lines);
            ioResult.ExitCode = aResult.Ok ? 0 : 1;
            ioResult.AddValue("candidates", aResult.Candidates.ToString());
            ioResult.AddValue("copied", aResult.Copied.ToString());
            ioResult.AddValue("existing", aResult.Existing.ToString());
            return null;
        }
    }
}
