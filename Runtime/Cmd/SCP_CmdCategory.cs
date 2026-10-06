// 區塊職責：指令**分類**的唯一一份清單（TASK-0427）—— `help` 的分組順序、`help --arg category=` 的合法值都從這裡來。
// 物理意義：每支外層 Cmd 用 <see cref="SCP_Cmd.Category"/> 指向這裡的常數；內層跟著父指令的分類。
//          加一個分類 ＝ 加一個常數並排進 <see cref="All"/>（排序即 help 上的順序）。
// 數值影響：純資料。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System.Collections.Generic;

namespace SCP.Core.Cmd
{
    public static class SCP_CmdCategory
    {
        public const string Routine = "作息";
        public const string Tavern = "酒館";
        public const string Task = "任務與提交";
        public const string Memory = "記憶與信件";
        public const string Reading = "閱讀與創作";
        public const string Game = "遊戲";
        public const string Bank = "銀行與券";
        public const string Persona = "身分";
        public const string System = "系統";

        /// <summary>全部分類與一句話說明。順序 ＝ help 上的順序。</summary>
        public static readonly IReadOnlyList<KeyValuePair<string, string>> All = new[]
        {
            new KeyValuePair<string, string>(Routine, "早安／晚安／小歇／自由時間／消費"),
            new KeyValuePair<string, string>(Tavern, "發文、讀訊息、等人回話、頻道與外部轉發"),
            new KeyValuePair<string, string>(Task, "任務單、提交、改 C# 的施工場與 Unity 編譯"),
            new KeyValuePair<string, string>(Memory, "見林見森、見叢、記憶檢索、工作記憶、信、好感與畫像"),
            new KeyValuePair<string, string>(Reading, "閱讀庫、寫書、觀影實錄、畫展、新詞、噗浪"),
            new KeyValuePair<string, string>(Game, "畫布、西洋棋、3D 雕刻"),
            new KeyValuePair<string, string>(Bank, "帳戶、請款、券、保管費、匯率與對帳"),
            new KeyValuePair<string, string>(Persona, "persona 身分欄、顯示資料、設定寫入"),
            new KeyValuePair<string, string>(System, "help、文件、skill、路徑、session、安裝與除錯"),
        };

        public static bool IsKnown(string iCategory)
        {
            foreach (KeyValuePair<string, string> kv in All) if (kv.Key == iCategory) return true;
            return false;
        }

        /// <summary>
        /// 一支 Cmd 實際的分類：外層看自己；內層沿父指令往上找到外層為止。
        /// 父指令不存在或繞成圈 ⇒ 回空字串（自測會紅，help 列在「未分類」）。
        /// </summary>
        public static string Of(SCP_Cmd iCmd)
        {
            SCP_Cmd aCur = iCmd;
            for (int i = 0; i < 16 && aCur.IsInner; i++)
            {
                SCP_Cmd? aParent = SCP_CmdRegistry.Find(aCur.Parent);
                if (aParent == null) return "";
                aCur = aParent;
            }
            return aCur.IsInner ? "" : aCur.Category;
        }

        /// <summary>流程起點：內層沿父指令往上的那支外層名（外層回自己）；斷鏈回空字串。</summary>
        public static string RootOf(SCP_Cmd iCmd)
        {
            SCP_Cmd aCur = iCmd;
            for (int i = 0; i < 16 && aCur.IsInner; i++)
            {
                SCP_Cmd? aParent = SCP_CmdRegistry.Find(aCur.Parent);
                if (aParent == null) return "";
                aCur = aParent;
            }
            return aCur.IsInner ? "" : aCur.Name;
        }
    }
}
