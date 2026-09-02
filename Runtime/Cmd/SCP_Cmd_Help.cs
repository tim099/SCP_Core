// 區塊職責：**help 指令** —— 列出所有 Cmd 與它們的參數。
// 物理意義：help 自己也是一支 Cmd，不是宿主的特例。
//           ⇒ 任何接上 SCP_CMD 的宿主（CLI / 視窗 / 未來的 Senate）都**自動**有 help，
//             不必各自抄一份清單；而抄一份清單就是抄一份會漂的清單。
// 數值影響：純讀 registry，零 IO。
//
// ⚠ 內容全部由 **ArgSpecs 產生**，沒有一個字是手寫的說明表。
//   手寫的參數表跟實作是兩份宣告 —— 兩份現在一致不代表明天一致，而漂掉時
//   **help 會很有自信地說一個不存在的參數**。
using System.Collections.Generic;
using System.Text;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Help : SCP_Cmd
    {
        public override string Name => "help";
        public override string Summary => "列出所有可用的 Cmd；給 name 就印那一支的參數說明";

        public override string Example => SCP_CmdRegistry.Invoke("help wake-brief");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("name", "只看這一支 Cmd 的詳細說明（不給＝列出全部）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aName = iArgs.Get("name");
            var aResult = new SCP_CmdResult();

            // 掃描期間的問題要先講 —— 少了一支 Cmd 而 help 只是「沒列出來」，那是安靜的失敗。
            SCP_CmdRegistry.Discover();
            foreach (string aWarning in SCP_CmdRegistry.DiscoveryWarnings)
                aResult.Lines.Add("⚠ " + aWarning);

            if (aName.Length > 0)
            {
                SCP_Cmd? aCmd = SCP_CmdRegistry.Find(aName);
                if (aCmd == null)
                {
                    aResult.ExitCode = 2;
                    aResult.Lines.Add("✗ 沒有名叫 '" + aName + "' 的 Cmd。下面是全部：");
                    AppendList(aResult);
                    return aResult;
                }
                AppendDetail(aResult, aCmd);
                return aResult;
            }

            AppendList(aResult);
            aResult.AddValue("command_count", SCP_CmdRegistry.All().Count.ToString());
            return aResult;
        }

        static void AppendList(SCP_CmdResult oResult)
        {
            IReadOnlyList<SCP_Cmd> aAll = SCP_CmdRegistry.All();
            oResult.Lines.Add("SCP_CMD —— " + aAll.Count + " 支指令");
            oResult.Lines.Add("");

            int aWidth = 0;
            foreach (SCP_Cmd aCmd in aAll) if (aCmd.Name.Length > aWidth) aWidth = aCmd.Name.Length;

            foreach (SCP_Cmd aCmd in aAll)
            {
                var aLine = new StringBuilder("  ").Append(aCmd.Name.PadRight(aWidth)).Append("  ").Append(aCmd.Summary);

                // 必填參數直接列在清單上 —— 那是「這支能不能現在就跑」唯一要知道的事。
                var aRequired = new List<string>();
                foreach (SCP_CmdArgSpec aSpec in aCmd.ArgSpecs)
                    if (aSpec.Required || aSpec.PresenceRequired) aRequired.Add(aSpec.Name);
                if (aRequired.Count > 0) aLine.Append("　［必填：").Append(string.Join(" , ", aRequired)).Append("］");

                // 執行位置擺在**行尾**而不是行首：Native 是多數，讓多數那群保持乾淨，
                // 特例才長出一截 —— 掃這份清單的人要找的是特例。
                string aTag = PortTag(aCmd.PortStatus);
                if (aTag.Length > 0) aLine.Append("　").Append(aTag);

                oResult.Lines.Add(aLine.ToString());
            }

            // 統計要印，而且**非 Native 是零的時候也印**——「沒有待移植」與「這欄還沒接上」
            // 在輸出上必須分得出來（讀取失敗與真的 0 不可同形）。
            int aDelegated = 0, aNotPorted = 0, aServer = 0;
            foreach (SCP_Cmd aCmd in aAll)
            {
                if (aCmd.PortStatus == SCP_CmdPortStatus.DelegatedToUnity) aDelegated++;
                else if (aCmd.PortStatus == SCP_CmdPortStatus.NotPorted) aNotPorted++;
                else if (aCmd.PortStatus == SCP_CmdPortStatus.DelegatedToServer) aServer++;
            }
            oResult.Lines.Add("");
            oResult.Lines.Add("執行位置：本地 " + (aAll.Count - aDelegated - aNotPorted - aServer)
                              + " ／ ⤷Unity " + aDelegated + " ／ ⤷Server " + aServer + " ／ ⛔未實作 " + aNotPorted
                              + "　（⤷Unity ＝ **Editor 沒開就跑不完**；⤷Server ＝ **`senate server start` 沒跑就跑不完**；待移植的缺口見 help <name>）");
            oResult.AddValue("delegated_count", aDelegated.ToString());
            oResult.AddValue("server_count", aServer.ToString());
            oResult.AddValue("not_ported_count", aNotPorted.ToString());

            oResult.Lines.Add("單支詳細：" + SCP_CmdRegistry.Invoke("help <name>"));
        }

        /// <summary>執行位置的行尾標記。Native 回空字串 —— 多數不必被標。</summary>
        static string PortTag(SCP_CmdPortStatus iStatus)
        {
            if (iStatus == SCP_CmdPortStatus.DelegatedToUnity) return "⤷Unity";
            if (iStatus == SCP_CmdPortStatus.DelegatedToServer) return "⤷Server";
            if (iStatus == SCP_CmdPortStatus.NotPorted) return "⛔未實作";
            return "";
        }

        static void AppendDetail(SCP_CmdResult oResult, SCP_Cmd iCmd)
        {
            oResult.Lines.Add("── " + iCmd.Name + " ──");
            oResult.Lines.Add(iCmd.Summary);

            // 執行位置在 Summary 正下方：它決定「這支現在能不能跑」，
            // 比參數更早該知道（參數對了而 Editor 沒開，一樣跑不完）。
            if (iCmd.PortStatus == SCP_CmdPortStatus.DelegatedToUnity)
                oResult.Lines.Add("執行位置：⤷ Unity Editor（走 AgentCommand 檔案協議）"
                                  + "　⚠ **Editor 沒開就跑不完**");
            else if (iCmd.PortStatus == SCP_CmdPortStatus.DelegatedToServer)
                oResult.Lines.Add("執行位置：⤷ Senate Server（走 AgentCommand 檔案協議，根是 Senate 自己的）"
                                  + "　⚠ **`senate server start` 沒跑就跑不完，且不降級成本地跑**");
            else if (iCmd.PortStatus == SCP_CmdPortStatus.NotPorted)
                oResult.Lines.Add("執行位置：⛔ 還沒有實作 —— 這是登記在案的缺口，不是打錯名字");
            if (iCmd.PortNote.Length > 0)
                oResult.Lines.Add("待移植：" + iCmd.PortNote);

            if (iCmd.Details.Length > 0) { oResult.Lines.Add(""); oResult.Lines.Add(iCmd.Details); }

            oResult.Lines.Add("");
            if (iCmd.ArgSpecs.Count == 0)
            {
                oResult.Lines.Add("參數：（這支沒有參數）");
            }
            else
            {
                oResult.Lines.Add("參數：");
                foreach (SCP_CmdArgSpec aSpec in iCmd.ArgSpecs) oResult.Lines.Add(aSpec.HelpLine());
            }
            if (iCmd.Example.Length > 0)
            {
                oResult.Lines.Add("");
                oResult.Lines.Add("範例：" + iCmd.Example);
            }
        }
    }
}
