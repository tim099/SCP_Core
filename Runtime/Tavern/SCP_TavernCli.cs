// 區塊職責：「這則發言是不是一句酒保 CLI 指令」的判準 —— 寫入端據此打 `tag=cli-cmd`／`cli_cmd=true` 並跳過詞典附註。
// 物理意義：移植自 UCL_Core `UCL_BartenderCliService.LooksLikeCliCommand`（TASK-0312，epic 0295 ③ 第三刀）。
//          之前 Editor 一份、Senate 一份（`Cmd_TavernPost.CliPrefix`，只拿來擋下），而兩份在「設定檔不存在」那格已經分岔：
//          Editor 當成「總開關開著、前綴 cmd」，Senate 當成「不是指令」—— 後者倒向放行，正是 2026-08-19 那隻
//          （附註被當成指令的一部分，群發把整本詞典打進別人輸入框並按 Enter）會回來的方向。⇒ 收成這一份，兩個宿主共用。
// 判準（逐格照 Editor 版）：
//   · 設定檔 `ChatTavern/bartender/cli_settings.json`：不存在／讀壞 ⇒ 視為 enabled、前綴 cmd（Editor 的預設與 fail-closed）
//   · `enabled=false` ⇒ 不是指令；`prefix` 空白 ⇒ cmd
//   · body trim 後的第一個 token（分隔字元：半形空白／tab／CR／LF／全形空白）與前綴**不分大小寫**相等 ⇒ 是指令
//   ⚠ 只看前綴，不驗白名單 —— 白名單是「能不能執行」，這裡問的是「這句話是不是指令」。
// 數值影響：只讀那一個設定檔；⛔ 不寫（Editor 在檔案不存在時會補寫預設檔，那是它的設定頁的責任，不是判準的）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Tavern
{
    public static class SCP_TavernCli
    {
        public const string DefaultPrefix = "cmd";

        /// <summary>判為 CLI 指令時寫入端打的 tag（已有 tag 時不覆蓋，改靠 <see cref="MetaKey"/>）。</summary>
        public const string Tag = "cli-cmd";

        /// <summary>分流訊號的獨立鍵 —— 發話端顯式給了別的 tag 時，靠它保證訊號不丟。</summary>
        public const string MetaKey = "cli_cmd";

        static readonly char[] s_Sep = { ' ', '\t', '\r', '\n', '　' };

        public static string SettingsPath(string iDataRoot)
            => Path.Combine(iDataRoot, "ChatTavern", "bartender", "cli_settings.json").Replace('\\', '/');

        /// <summary>現在生效的前綴；總開關關著回 null。設定不存在／讀壞 ⇒ <see cref="DefaultPrefix"/>（fail-closed，照 Editor）。</summary>
        public static string? ActivePrefix(string iDataRoot)
        {
            string aPath = SettingsPath(iDataRoot);
            if (!File.Exists(aPath)) return DefaultPrefix;
            try
            {
                SCP_JsonData aSettings = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                if (!aSettings.GetBool("enabled", true)) return null;
                string aPrefix = aSettings.GetString("prefix", DefaultPrefix).Trim();
                return aPrefix.Length > 0 ? aPrefix : DefaultPrefix;
            }
            catch (Exception) { return DefaultPrefix; }
        }

        /// <summary>這段文字是不是一句 CLI 指令（前綴命中且總開關開著）。</summary>
        public static bool LooksLikeCliCommand(string iDataRoot, string? iBody)
        {
            string aBody = (iBody ?? "").Trim();
            if (aBody.Length == 0) return false;
            string? aPrefix = ActivePrefix(iDataRoot);
            return aPrefix != null && FirstTokenIs(aBody, aPrefix);
        }

        /// <summary>純字串那一半（不讀設定）—— 給已經自己載過設定的呼叫端。</summary>
        public static bool FirstTokenIs(string iTrimmedBody, string iPrefix)
        {
            int aSpace = iTrimmedBody.IndexOfAny(s_Sep);
            string aFirst = aSpace < 0 ? iTrimmedBody : iTrimmedBody.Substring(0, aSpace);
            return string.Equals(aFirst, string.IsNullOrWhiteSpace(iPrefix) ? DefaultPrefix : iPrefix,
                                 StringComparison.OrdinalIgnoreCase);
        }
    }
}
