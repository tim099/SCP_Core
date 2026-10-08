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
        public override string Category => SCP_CmdCategory.System;
        public override string Summary => "列出所有可用的 Cmd；給 name 就印那一支的參數說明";

        public override string Example => SCP_CmdRegistry.Invoke("help wake-brief");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("name", "只看這一支 Cmd 的詳細說明；給的是分類名就列那一類（不給＝依分類列出外層）"),
            new SCP_CmdArgSpec("category", "只列這一類（內層縮排在父指令底下）。打錯 ⇒ exit 2 並列出現有分類", iDefault: ""),
            new SCP_CmdArgSpec("all", "1 ＝ 連內層一起列（標出父指令）", iDefault: "0", iChoices: new[] { "0", "1" }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aName = iArgs.Get("name").Trim();
            string aCategory = iArgs.Get("category").Trim();
            bool aAllFlag = iArgs.Get("all").Trim() == "1";
            var aResult = new SCP_CmdResult();

            // 掃描期間的問題要先講 —— 少了一支 Cmd 而 help 只是「沒列出來」，那是安靜的失敗。
            SCP_CmdRegistry.Discover();
            foreach (string aWarning in SCP_CmdRegistry.DiscoveryWarnings)
                aResult.Lines.Add("⚠ " + aWarning);

            if (aName.Length > 0)
            {
                SCP_Cmd? aCmd = SCP_CmdRegistry.Find(aName);
                if (aCmd != null) { AppendDetail(aResult, aCmd); return aResult; }
                if (SCP_CmdCategory.IsKnown(aName)) aCategory = aName;   // `help 酒館` ＝ 查分類
                else
                {
                    aResult.ExitCode = 2;
                    aResult.Lines.Add("✗ 沒有名叫 '" + aName + "' 的 Cmd，也不是分類名。下面是全部外層：");
                    AppendList(aResult, null, false);
                    return aResult;
                }
            }

            if (aCategory.Length > 0 && !SCP_CmdCategory.IsKnown(aCategory))
            {
                aResult.ExitCode = 2;
                aResult.Lines.Add("✗ 沒有叫 '" + aCategory + "' 的分類。現有：");
                foreach (KeyValuePair<string, string> kv in SCP_CmdCategory.All) aResult.Lines.Add("  " + kv.Key + "　—— " + kv.Value);
                return aResult;
            }

            AppendList(aResult, aCategory.Length > 0 ? aCategory : null, aAllFlag);
            return aResult;
        }

        /// <summary>清單的一行：名字、Summary、必填參數、執行位置。</summary>
        static string CmdLine(SCP_Cmd iCmd, int iWidth, string iIndent)
        {
            var aLine = new StringBuilder(iIndent).Append(iCmd.Name.PadRight(iWidth)).Append("  ").Append(iCmd.Summary);

            // 必填參數直接列在清單上 —— 那是「這支能不能現在就跑」唯一要知道的事。
            // ⚠ 宿主補的那幾格不列：列了就是在叫人手給一個宿主會擋下的值。
            var aRequired = new List<string>();
            foreach (SCP_CmdArgSpec aSpec in iCmd.ArgSpecs)
                if ((aSpec.Required || aSpec.PresenceRequired) && !SCP_CmdRegistry.HostFilledArgs.Contains(aSpec.Name))
                    aRequired.Add(aSpec.Name);
            if (aRequired.Count > 0) aLine.Append("　［必填：").Append(string.Join(" , ", aRequired)).Append("］");

            // 執行位置擺在**行尾**而不是行首：Native 是多數，讓多數那群保持乾淨，
            // 特例才長出一截 —— 掃這份清單的人要找的是特例。
            string aTag = PortTag(iCmd.PortStatus);
            if (aTag.Length > 0) aLine.Append("　").Append(aTag);
            return aLine.ToString();
        }

        /// <summary>某支外層底下的內層（依註冊順序；內層的內層也算，沿父鏈歸到流程起點）。</summary>
        static List<SCP_Cmd> InnerOf(SCP_Cmd iRoot, IReadOnlyList<SCP_Cmd> iAll)
        {
            var aOut = new List<SCP_Cmd>();
            foreach (SCP_Cmd c in iAll)
                if (c.IsInner && SCP_CmdCategory.RootOf(c) == iRoot.Name) aOut.Add(c);
            return aOut;
        }

        // ===========================================================
        // 區塊職責：清單 —— 外層依分類分組（TASK-0427）。
        // 物理意義：預設只列外層；內層由父指令那條流程的回傳指路，`all=1` 或查分類時才縮排列在父指令底下。
        //          ⛔ 不讓任何一支安靜消失：沒分類／父鏈斷掉的列進「⚠ 未分類」，各組支數加總要等於總數。
        // ===========================================================
        static void AppendList(SCP_CmdResult oResult, string? iCategory, bool iWithInner)
        {
            IReadOnlyList<SCP_Cmd> aAll = SCP_CmdRegistry.All();
            int aInner = 0;
            foreach (SCP_Cmd c in aAll) if (c.IsInner) aInner++;
            int aOuter = aAll.Count - aInner;
            bool aShowInner = iWithInner || iCategory != null;

            oResult.Lines.Add(iCategory == null
                ? "SCP_CMD —— 外層 " + aOuter + " 支" + (iWithInner ? "＋內層 " + aInner + " 支" : "（另有內層 " + aInner + " 支，由父指令的回傳指路）")
                : "SCP_CMD —— 分類「" + iCategory + "」");
            oResult.Lines.Add("");

            int aWidth = 0;
            foreach (SCP_Cmd aCmd in aAll) if (aCmd.Name.Length + 4 > aWidth) aWidth = aCmd.Name.Length + 4;

            var aGroups = new List<string>();
            foreach (KeyValuePair<string, string> kv in SCP_CmdCategory.All) aGroups.Add(kv.Key);
            aGroups.Add("");   // 未分類（分類字串不在表上、內層父鏈斷掉；沒填的在「其他」）

            int aListedOuter = 0;
            foreach (string aGroup in aGroups)
            {
                if (iCategory != null && aGroup != iCategory) continue;
                var aMembers = new List<SCP_Cmd>();
                foreach (SCP_Cmd c in aAll)
                {
                    string aCat = SCP_CmdCategory.Of(c);
                    if (aGroup.Length == 0 ? !SCP_CmdCategory.IsKnown(aCat) : aCat == aGroup)
                        if (!c.IsInner || SCP_CmdCategory.RootOf(c).Length == 0) aMembers.Add(c);   // 斷鏈的內層照外層列，⛔ 不藏
                }
                if (aMembers.Count == 0 && (aGroup.Length == 0 || iCategory == null)) continue;

                string aNote = "";
                foreach (KeyValuePair<string, string> kv in SCP_CmdCategory.All) if (kv.Key == aGroup) aNote = kv.Value;
                oResult.Lines.Add(aGroup.Length == 0
                    ? "## ⚠ 未分類（" + aMembers.Count + "）—— 分類字串不在分類表上，或內層的父指令不存在（沒填分類的會在「" + SCP_CmdCategory.Other + "」）"
                    : "## " + aGroup + "（" + aMembers.Count + "）　" + aNote);
                foreach (SCP_Cmd aCmd in aMembers)
                {
                    oResult.Lines.Add(CmdLine(aCmd, aWidth, "  "));
                    aListedOuter++;
                    if (!aShowInner) continue;
                    foreach (SCP_Cmd aChild in InnerOf(aCmd, aAll))
                        oResult.Lines.Add(CmdLine(aChild, aWidth - 2, "    ↳ "));
                }
                oResult.Lines.Add("");
            }

            // 統計要印，而且**非 Native 是零的時候也印**——「沒有待移植」與「這欄還沒接上」
            // 在輸出上必須分得出來（讀取失敗與真的 0 不可同形）。
            int aNotPorted = 0, aServer = 0;
            foreach (SCP_Cmd aCmd in aAll)
            {
                if (aCmd.PortStatus == SCP_CmdPortStatus.NotPorted) aNotPorted++;
                else if (aCmd.PortStatus == SCP_CmdPortStatus.DelegatedToServer) aServer++;
            }
            oResult.Lines.Add("執行位置：本地 " + (aAll.Count - aNotPorted - aServer)
                              + " ／ ⤷Server " + aServer + " ／ ⛔未實作 " + aNotPorted
                              + "　（⤷Server ＝ **沒在跑會自動拉起一顆**（拉不起來仍然失敗，⛔ 不降級成本地跑）；待移植的缺口見 help <name>）");
            oResult.AddValue("server_count", aServer.ToString());
            oResult.AddValue("not_ported_count", aNotPorted.ToString());

            oResult.Lines.Add("單支詳細：" + SCP_CmdRegistry.Invoke("help <name>")
                              + "　｜　查一類：" + SCP_CmdRegistry.Invoke("help <分類>")
                              + "　｜　連內層全列：" + SCP_CmdRegistry.Invoke("help --arg all=1"));
            oResult.AddValue("command_count", aAll.Count.ToString());
            oResult.AddValue("entry_count", aOuter.ToString());
            oResult.AddValue("inner_count", aInner.ToString());
            oResult.AddValue("listed_count", aListedOuter.ToString());
        }

        /// <summary>
        /// 「文件：…」那一行。三種情況分開說 —— ⛔ 「沒有對應文件」「宿主沒裝文件根」「文件根讀不齊」不可同形。
        /// </summary>
        static string DocLine(string iCmdName)
        {
            if (!Docs.SCP_DocStore.TryList(out List<Docs.SCP_DocEntry> aEntries, out List<string> aProblems, out string aError))
                return "文件：（查不了 —— " + aError + "）";
            List<Docs.SCP_DocEntry> aHits = Docs.SCP_DocStore.FindByCmd(aEntries, iCmdName);
            string aWarn = aProblems.Count > 0 ? "　⚠ 文件根有 " + aProblems.Count + " 個問題，這一格可能不完整（" + SCP_CmdRegistry.Invoke("doc") + " 會列出）" : "";
            if (aHits.Count == 0) return "文件：沒有 —— 這支指令還沒有對應的使用說明" + aWarn;
            var aNames = new List<string>();
            foreach (Docs.SCP_DocEntry aDoc in aHits) aNames.Add(aDoc.Name);
            return "文件：" + string.Join(", ", aNames) + "　（" + SCP_CmdRegistry.Invoke("doc --arg op=show --arg name=" + aNames[0]) + "）" + aWarn;
        }

        /// <summary>執行位置的行尾標記。Native 回空字串 —— 多數不必被標。</summary>
        static string PortTag(SCP_CmdPortStatus iStatus)
        {
            if (iStatus == SCP_CmdPortStatus.DelegatedToServer) return "⤷Server";
            if (iStatus == SCP_CmdPortStatus.NotPorted) return "⛔未實作";
            return "";
        }

        static void AppendDetail(SCP_CmdResult oResult, SCP_Cmd iCmd)
        {
            oResult.Lines.Add("── " + iCmd.Name + " ──");

            // 🩸 **實際派遣到哪個型別**（TASK-0266）—— 在這之前沒有任何入口回答得了這個問題。
            //    那天 `tavern-write` 的說明是 `Cmd_TavernWrite` 的，而派遣到的是 selftest 的巢狀子類，
            //    兩者逐字同形；唯一會叫的是 `Discover` 的撞名警告，而它印在第一行 —— 所有 `| tail` 都吃掉它。
            // ⇒ 把「說明」與「派遣目標」印在**同一頁**，讀說明的人不必再去信另一個地方的一行字。
            oResult.Lines.Add("型別：" + iCmd.GetType().FullName);
            oResult.Lines.Add(iCmd.Summary);

            // 分類與層級（TASK-0427）：內層要說出從哪裡開始走；有子流程的外層列出它的內層。
            string aCat = SCP_CmdCategory.Of(iCmd);
            oResult.Lines.Add("分類：" + (SCP_CmdCategory.IsKnown(aCat) ? aCat : "⚠ 未分類"));
            if (iCmd.IsInner)
            {
                string aRoot = SCP_CmdCategory.RootOf(iCmd);
                oResult.Lines.Add(aRoot.Length > 0
                    ? "層級：內層 —— 屬於 `" + aRoot + "` 的子流程（從 " + SCP_CmdRegistry.Invoke(aRoot) + " 開始走，回傳會指到這一步）"
                    : "層級：⚠ 內層，但父指令 `" + iCmd.Parent + "` 不存在");
            }
            else
            {
                var aChildren = new List<string>();
                foreach (SCP_Cmd c in InnerOf(iCmd, SCP_CmdRegistry.All())) aChildren.Add(c.Name);
                if (aChildren.Count > 0) oResult.Lines.Add("子流程（內層，由本支起頭的回傳逐步指路；依名字排序，不是步驟順序）：" + string.Join("、", aChildren));
            }

            // 執行位置在 Summary 正下方：它決定「這支現在能不能跑」，
            // 比參數更早該知道（參數對了而 Server 起不來，一樣跑不完）。
            if (iCmd.PortStatus == SCP_CmdPortStatus.DelegatedToServer)
                oResult.Lines.Add("執行位置：⤷ Senate Server（走 AgentCommand 檔案協議，根是 Senate 自己的）"
                                  + "　⚠ **沒在跑會自動拉起一顆**（TASK-0267）——拉不起來／等不到上線 ⇒ **這一趟失敗**，⛔ 不降級成本地跑");
            else if (iCmd.PortStatus == SCP_CmdPortStatus.NotPorted)
                oResult.Lines.Add("執行位置：⛔ 還沒有實作 —— 這是登記在案的缺口，不是打錯名字");
            if (iCmd.PortNote.Length > 0)
                oResult.Lines.Add("待移植：" + iCmd.PortNote);

            // 這支指令的**使用說明**在哪份文件（TASK-0337）—— 參數表由下面的 ArgSpecs 產生，
            // 怎麼用、什麼時候用、有什麼紀律寫在文件裡；對應關係寫在文件的 frontmatter `cmds:`。
            oResult.Lines.Add(DocLine(iCmd.Name));

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
