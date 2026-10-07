// 區塊職責：一筆自由時間活動的**資料形狀**（來源＝一份活動 md 的 frontmatter）＋ 特殊邏輯標記 enum。
// 物理意義：活動的事實來源**只有 md 一處** —— v1 的 `AgentCommands/FreeTime/activities.json` 正是因為
//          「雙源同步漂移」被廢止，所以這裡只是 md 的讀數，不是第二份設定。
// 數值影響：純資料 ＋ 查表，零 IO（IO 在 SCP_FreeTimeCatalog）。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.FreeTime
{
    /// <summary>
    /// 活動的**特殊邏輯標記**（frontmatter `kind`）。
    /// <para>為什麼是 enum 而不是自由字串：管理頁要能給下拉選單。字串打錯（`live-strem`）會**安靜地什麼都不做**，
    /// 而下拉選單根本打不出那個值。</para>
    /// <para>新增一種 kind ＝ 改這個 enum ＋ 在 <see cref="SCP_FreeTimeGating"/> 補對應邏輯。
    /// 兩邊都要動是刻意的：**沒有實作的標記不該存在**。</para>
    /// </summary>
    public enum SCP_FreeTimeActivityKind
    {
        /// <summary>一般活動 —— 永遠可選、永遠在普通層。</summary>
        Default = 0,
        /// <summary>觀看直播：**沒開播就隱藏**；開播時進最優先層並附節目名。</summary>
        StreamWatch = 1,
        /// <summary>下棋：輪到你走且對手也在自由時間／有人開了一局在等 ⇒ 最優先。</summary>
        Chess = 2,
        /// <summary>繪圖：永久繪圖券存量超過門檻 ⇒ 最優先並提示「請多多使用」。</summary>
        CanvasVoucherFull = 3,
    }

    /// <summary>一筆自由時間活動（frontmatter 的讀數；正文是給人讀的說明，不進本型別）。</summary>
    public sealed class SCP_FreeTimeActivity
    {
        public string Id = "";
        public string Name = "";
        public string How = "";
        /// <summary>md 絕對路徑（路徑不該被推導，該被傳遞）。</summary>
        public string Path = "";
        /// <summary>建議所需分鐘；0＝未設定（不做時間感知排序）。</summary>
        public int MinMinutes;
        public bool Enabled = true;
        /// <summary>true＝專案層（同 id 會覆蓋共用層）。</summary>
        public bool IsProjectLayer;

        /// <summary>
        /// 這件活動是否**必須在自由時間裡**才做得成（frontmatter `needs_session`，預設 true）。
        /// <para>下棋每一步都落盤、一局跨好幾次醒來 —— 綁在場次上會讓「想走一步」變成「要先開一場」（Tim 2026-09-11）。</para>
        /// <para>⚠ 宣告式，⛔ 不在 Cmd 裡寫 `if (id == "chess")` —— 下一個該豁免的活動不會有人想起來改那一行。</para>
        /// </summary>
        public bool NeedsSession = true;

        /// <summary>所屬分組（frontmatter `group`）。空＝未分組。純顯示用，**不參與**可用性／優先層／時間感知。</summary>
        public string Group = "";

        /// <summary>
        /// python 工具檔名（frontmatter `tool`）。
        /// <para>⛔ **這條路已經死了**（TASK-0360：沒有任何 md 宣告 `tool:`）—— 本欄只為了讓讀到它的 Cmd
        /// 說得出「python 工具步驟已不支援 —— 改成 cmd_steps」，⛔ 不會被拿去 spawn 任何東西。</para>
        /// </summary>
        public string Tool = "";

        /// <summary>允許代跑的子命令白名單（frontmatter `steps`）。空＝一律拒跑（fail-closed，不是 fail-open）。</summary>
        public List<string> Steps = new List<string>();

        /// <summary>python 旗標形式的身分旗標（frontmatter `persona_flag`）—— 只留給管理頁顯示，cmd 路線用 <see cref="CmdPersonaArg"/>。</summary>
        public string PersonaFlag = "";

        /// <summary>需要自動補身分的子命令（frontmatter `steps_need_persona`；支援 <c>step=--flag</c> 覆寫單一 step）。</summary>
        public List<string> StepsNeedPersona = new List<string>();

        /// <summary>
        /// 改走 SCP cmd 的路由表（frontmatter <c>cmd_steps</c>：<c>&lt;step&gt;=&lt;cmd&gt;:&lt;op&gt;</c>；省略 op ⇒ op 用 step 名）。
        /// <para>⚠ **路由的粒度是 step，不是活動** —— 那 35 支子命令是一支一支移植進 C# 的。</para>
        /// </summary>
        public List<string> CmdSteps = new List<string>();

        /// <summary>被路由的 step 的身分**參數名**（frontmatter <c>cmd_persona_arg</c>，例 <c>reader</c>）——是名字不是旗標。</summary>
        public string CmdPersonaArg = "";

        /// <summary>被路由的 step 用哪個參數選子命令（frontmatter <c>cmd_step_arg</c>；空＝<c>op</c>）。</summary>
        public string CmdStepArg = "";

        /// <summary>特殊邏輯標記（frontmatter `kind`；缺欄位＝Default）。</summary>
        public SCP_FreeTimeActivityKind Kind = SCP_FreeTimeActivityKind.Default;

        /// <summary>
        /// `kind` 的原始字串 —— **只在解析失敗時非空**。存它是為了讓打錯的標記在骰面上顯形：
        /// 靜默退回 Default 的症狀是「我明明標了直播，它卻還是照常出現」，而沒有任何地方會喊。
        /// </summary>
        public string KindParseError = "";

        /// <summary>這一步被路由到 cmd 時該補哪個**參數名**（空＝不補）。旗標寫法的前導 `-` 會剝掉（同一份宣告兩條路都成立）。</summary>
        public string PersonaArgForStep(string iStep) => LookupNeedPersona(iStep, CmdPersonaArg).TrimStart('-');

        // `steps_need_persona` 的查表：先找 step 專屬覆寫（`shelf=--persona`），沒有才回退 iFallback。
        string LookupNeedPersona(string iStep, string iFallback)
        {
            foreach (string aEntry in StepsNeedPersona)
            {
                int aEq = aEntry.IndexOf('=');
                string aName = aEq < 0 ? aEntry : aEntry.Substring(0, aEq);
                if (!string.Equals(aName.Trim(), iStep, StringComparison.OrdinalIgnoreCase)) continue;
                string aOverride = aEq < 0 ? "" : aEntry.Substring(aEq + 1).Trim();
                return aOverride.Length > 0 ? aOverride : iFallback;
            }
            return "";
        }

        /// <summary>
        /// 某個 step 有沒有被路由到 SCP cmd。
        /// <para>routed=false 且 error 空 ＝ 沒宣告路由。⛔ **宣告了但寫壞不當成「沒路由」** —— 寫壞就回 error，
        /// 由呼叫端擋下並把那一行原文印出來。</para>
        /// </summary>
        public (bool Routed, string Cmd, string Op, string Error) CmdRouteForStep(string iStep)
        {
            foreach (string aEntry in CmdSteps)
            {
                int aEq = aEntry.IndexOf('=');
                string aName = (aEq < 0 ? aEntry : aEntry.Substring(0, aEq)).Trim();
                if (!string.Equals(aName, iStep, StringComparison.OrdinalIgnoreCase)) continue;

                string aTarget = aEq < 0 ? "" : aEntry.Substring(aEq + 1).Trim();
                if (aTarget.Length == 0)
                    return (false, "", "", $"`cmd_steps` 的 `{aEntry}` 沒寫目標（要 `<step>=<cmd>:<op>`）");
                int aColon = aTarget.IndexOf(':');
                string aCmd = (aColon < 0 ? aTarget : aTarget.Substring(0, aColon)).Trim();
                string aOp = aColon < 0 ? "" : aTarget.Substring(aColon + 1).Trim();
                if (aCmd.Length == 0) return (false, "", "", $"`cmd_steps` 的 `{aEntry}` 沒寫 cmd 名");
                return (true, aCmd, aOp.Length > 0 ? aOp : aName, "");
            }
            return (false, "", "", "");
        }

        /// <summary>這件活動有沒有任何 step 被路由到 cmd（op=step 代跑的能力判準）。</summary>
        public bool HasCmdRunner => CmdSteps.Count > 0 && Steps.Count > 0;
    }
}
