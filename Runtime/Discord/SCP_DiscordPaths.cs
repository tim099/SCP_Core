// 區塊職責：**Discord 設定資料夾** `ChatTavern/discord/` 的路徑（TASK-0320）—— 所有 Discord 相關檔的唯一落點。
// 物理意義：Tim 2026-09-28：webhook 密文「放 discord_config.json 同資料夾，但不直接放 json」⇒ 需要一個資料夾；
//           TASK-0319 先放在 `ChatTavern/` 根的三個檔（頻道對應表／白名單／Server 快取）一起收進來。
//           · `discord_config.json`            —— 開關、頭像網址範本、webhook 清單（不含 URL）、分類 → webhook
//           · `discord_webhooks.enc`           —— webhook URL 的密文（寫死的金鑰，`SCP_DiscordWebhooks`）
//           · `discord_channel_routing.json`   —— Discord 頻道 → 酒館頻道（Inbound）
//           · `discord_inbound_whitelist.json` —— Inbound 使用者白名單
//           · `discord_guilds_cache.json`      —— Bot 看得到的 Server／頻道（機器產物）
// 數值影響：`EnsureMigrated` 第一次呼叫時把舊位置的檔**搬**進來（新位置已有同名檔 ⇒ 不搬、留著舊檔並回報）；其餘純路徑運算。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Tavern;

namespace SCP.Core.Discord
{
    public static class SCP_DiscordPaths
    {
        public const string DirName = "discord";
        public const string ConfigFileName = "discord_config.json";
        public const string WebhookSecretFileName = "discord_webhooks.enc";

        static string TavernDir(string iDataRoot)
            => SCP.Core.Paths.SCP_DataPaths.ChatTavern(new SCP.Core.Paths.SCP_DataRoot(iDataRoot)).Replace('\\', '/');

        public static string Dir(string iDataRoot) => TavernDir(iDataRoot) + "/" + DirName;
        // ⚠ 每一條路徑都先觸發搬家 —— 🩸 只掛在其中三條上時，只讀 config 的那條路（op=status）永遠不會搬（2026-09-28 實測）
        public static string ConfigPath(string iDataRoot) { EnsureMigrated(iDataRoot); return Dir(iDataRoot) + "/" + ConfigFileName; }
        public static string WebhookSecretPath(string iDataRoot) { EnsureMigrated(iDataRoot); return Dir(iDataRoot) + "/" + WebhookSecretFileName; }
        public static string RoutingPath(string iDataRoot) { EnsureMigrated(iDataRoot); return Dir(iDataRoot) + "/" + SCP_DiscordInboundConfig.RoutingFileName; }
        public static string WhitelistPath(string iDataRoot) { EnsureMigrated(iDataRoot); return Dir(iDataRoot) + "/" + SCP_DiscordInboundConfig.WhitelistFileName; }
        public static string CachePath(string iDataRoot) { EnsureMigrated(iDataRoot); return Dir(iDataRoot) + "/" + SCP_DiscordBot.CacheFileName; }

        static readonly HashSet<string> s_Migrated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 把 TASK-0319 放在 `ChatTavern/` 根的三個檔搬進 `ChatTavern/discord/`（一個資料根只做一次）。
        /// 回傳搬動的說明（空 ＝ 沒事可做）。⚠ 新位置已有同名檔 ⇒ 不搬、⛔ 不合併，把舊檔留著並回報。
        /// </summary>
        public static List<string> EnsureMigrated(string iDataRoot)
        {
            var aNotes = new List<string>();
            lock (s_Migrated)
            {
                if (!s_Migrated.Add(iDataRoot)) return aNotes;
            }
            string aOldDir = TavernDir(iDataRoot);
            string aNewDir = Dir(iDataRoot);
            foreach (string aName in new[] { SCP_DiscordInboundConfig.RoutingFileName, SCP_DiscordInboundConfig.WhitelistFileName, SCP_DiscordBot.CacheFileName })
            {
                string aOld = aOldDir + "/" + aName, aNew = aNewDir + "/" + aName;
                if (!File.Exists(aOld)) continue;
                try
                {
                    if (File.Exists(aNew)) { aNotes.Add($"⚠ {aName}：新舊兩處都有 ⇒ 用新的（{aNew}），舊的留在 {aOld} 待人工處理"); continue; }
                    Directory.CreateDirectory(aNewDir);
                    File.Move(aOld, aNew);
                    aNotes.Add($"已搬 {aName} → {aNewDir}/");
                }
                catch (Exception e) { aNotes.Add($"⚠ {aName} 搬不動：{e.Message}"); }
            }
            return aNotes;
        }
    }
}
