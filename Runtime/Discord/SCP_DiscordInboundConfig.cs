// 區塊職責：**Discord Inbound 的設定** —— 「Discord 頻道 → 酒館頻道」對應表與使用者白名單（TASK-0319）。唯一讀寫層。
// 物理意義：
//   · 對應表：`ChatTavern/discord/discord_channel_routing.json` 的 `mappings`（沿用既有檔與格式，⇒ Senate 版 Inbound 直接讀它）。
//     寫回時**每一列的其他欄位原樣保留**（priority／tags／_note…），頂層的 `_description` 之類也保留；
//     後台只露出 酒館頻道／開關／source_class 三格（Tim 2026-09-28：照建議）。
//     酒館頻道只收**沒封存**的（`SCP_TavernChannels`）；新接的 Discord 頻道預設接主頻道 `tavern`（Tim 2026-09-28）。
//   · 白名單：從 `PromptQueue/notify_config.json` 的 `tavern_inbound.user_whitelist` **搬到**
//     `ChatTavern/discord/discord_inbound_whitelist.json`（Tim 2026-09-28：「白名單一起搬」）。
//     新檔不在 ⇒ 照舊讀 notify_config（`Source` 會說）；第一次寫入就寫新檔，⛔ 不回寫 notify_config
//     （那個檔裡有明文 webhook，不為了白名單整份重寫它）。
//   · Unity 端 Inbound 已廢棄（Tim 2026-09-28）⇒ 本檔只顧 Senate 這一側。
// 數值影響：讀取零寫入；寫入走暫存檔再換檔、寫完回讀；驗證不過一律零寫入。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Tavern;

namespace SCP.Core.Discord
{
    public sealed class SCP_DiscordRoute
    {
        public string ChannelId = "";
        public string GuildId = "";
        public string Label = "";
        public string TavernRoom = "";
        public bool Enabled;
        public string SourceClass = "";
        public int Priority;
    }

    public sealed class SCP_DiscordWhitelistUser
    {
        public string UserId = "";
        public string DisplayName = "";
        public string Profile = "";
    }

    public sealed class SCP_DiscordWhitelist
    {
        public bool Enabled;
        public List<SCP_DiscordWhitelistUser> Users = new List<SCP_DiscordWhitelistUser>();
        /// <summary>從哪裡讀到的：`whitelist`（新檔）／`notify_config`（還沒搬家）／`none`。</summary>
        public string Source = "none";
        public string Error = "";
    }

    public static class SCP_DiscordInboundConfig
    {
        public const string RoutingFileName = "discord_channel_routing.json";
        public const string WhitelistFileName = "discord_inbound_whitelist.json";
        /// <summary>source_class 的慣用值（Tim 2026-05-15：freeform，這三個是慣例）。</summary>
        public static readonly string[] KnownSourceClasses = { "external", "internal", "work" };

        // 落點：`ChatTavern/discord/`（TASK-0320 從 `ChatTavern/` 根搬進去；搬家在 SCP_DiscordPaths.EnsureMigrated）
        public static string RoutingPath(string iDataRoot) => SCP_DiscordPaths.RoutingPath(iDataRoot);
        public static string WhitelistPath(string iDataRoot) => SCP_DiscordPaths.WhitelistPath(iDataRoot);
        public static string NotifyConfigPath(string iDataRoot) => Path.Combine(iDataRoot, "PromptQueue", "notify_config.json").Replace('\\', '/');

        // ── 對應表 ───────────────────────────────────────────────────

        static SCP_JsonData LoadRoutingJson(string iDataRoot, out string? oError)
        {
            oError = null;
            string aPath = RoutingPath(iDataRoot);
            if (!File.Exists(aPath))
            {
                var j = SCP_JsonData.NewObject();
                j.Set("_schema_version", 1);
                j.Set("mappings", SCP_JsonData.NewArray());
                return j;
            }
            try
            {
                SCP_JsonData j = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                if (!j["mappings"].IsArray) { oError = $"{aPath} 沒有 mappings 陣列"; }
                return j;
            }
            catch (Exception e) { oError = $"讀不了 {aPath}：{e.Message}"; return SCP_JsonData.NewNull(); }
        }

        public static List<SCP_DiscordRoute> LoadRoutes(string iDataRoot, out string? oError)
        {
            var aOut = new List<SCP_DiscordRoute>();
            SCP_JsonData j = LoadRoutingJson(iDataRoot, out oError);
            if (oError != null) return aOut;
            foreach (SCP_JsonData m in j["mappings"]) aOut.Add(ToRoute(m));
            return aOut;
        }

        static SCP_DiscordRoute ToRoute(SCP_JsonData m) => new SCP_DiscordRoute
        {
            ChannelId = m.GetString("channel_id", ""),
            GuildId = m.GetString("guild_id", ""),
            Label = m.GetString("label", ""),
            TavernRoom = m.GetString("tavern_room", ""),
            Enabled = m.GetBool("enabled", false),
            SourceClass = m.GetString("source_class", ""),
            Priority = m.GetInt("priority", 0),
        };

        /// <summary>
        /// 新增或更新一列（以 channel_id 為鍵）。<paramref name="iRoom"/> 空 ⇒ 主頻道 `tavern`。
        /// 酒館頻道不存在或已封存 ⇒ 擋下、零寫入。既有列的其他欄位原樣保留；新列補 priority=10、tags=[]。
        /// </summary>
        public static bool TryUpsertRoute(string iDataRoot, string iChannelId, string iGuildId, string iLabel,
                                          string iRoom, bool iEnabled, string iSourceClass, out string? oError)
        {
            oError = null;
            string aCh = (iChannelId ?? "").Trim();
            if (!IsSnowflake(aCh)) { oError = $"channel_id 要是 Discord 的數字 id（收到 '{aCh}'）"; return false; }
            string aRoom = string.IsNullOrWhiteSpace(iRoom) ? SCP_TavernChannels.MainChannelId : iRoom.Trim();
            string aSrc = (iSourceClass ?? "").Trim(), aLabel = (iLabel ?? "").Trim(), aGuild = (iGuildId ?? "").Trim();
            if (aRoom == SCP_TavernChannels.MainChannelId) SCP_TavernChannels.EnsureMainChannel(iDataRoot, out _);
            if (!SCP_TavernChannels.ChannelExists(iDataRoot, aRoom)) { oError = $"沒有這個酒館頻道：'{aRoom}'"; return false; }
            if (SCP_TavernChannels.IsArchived(iDataRoot, aRoom)) { oError = $"酒館頻道 '{aRoom}' 已封存 ⇒ 先取消封存才能接"; return false; }

            SCP_JsonData j = LoadRoutingJson(iDataRoot, out oError);
            if (oError != null) { oError = "對應表讀不了，⛔ 不覆寫：" + oError; return false; }
            SCP_JsonData aArr = j["mappings"];
            SCP_JsonData? aHit = null;
            foreach (SCP_JsonData m in aArr) if (m.GetString("channel_id", "") == aCh) { aHit = m; break; }
            if (aHit == null)
            {
                aHit = SCP_JsonData.NewObject();
                aHit.Set("channel_id", aCh);
                aHit.Set("tavern_room", aRoom);
                aHit.Set("label", aLabel);
                aHit.Set("source_class", aSrc.Length > 0 ? aSrc : "external");
                aHit.Set("priority", 10);
                aHit.Set("enabled", iEnabled);
                aHit.Set("guild_id", aGuild);
                aHit.Set("tags", SCP_JsonData.NewArray());
                aArr.Add(aHit);
            }
            else
            {
                aHit.Set("tavern_room", aRoom);
                aHit.Set("enabled", iEnabled);
                if (aSrc.Length > 0) aHit.Set("source_class", aSrc);
                if (aLabel.Length > 0) aHit.Set("label", aLabel);
                // 🩸 TASK-0322：原本是「既有列的 guild 空的才寫」—— 錯的 guild 永遠改不掉，而且不說話
                //   （CLI 印 ✅、GUI 從快取選頻道傳的是 Discord 真值，照樣被擋）。遷移指南第 6 步「對不上的要修」因此無路可走。
                //   ⇒ 呼叫端給了 guild 就是意圖：跟現值不同就覆寫。孤兒那條路傳的是現值 ⇒ 不變。
                if (aGuild.Length > 0) aHit.Set("guild_id", aGuild);
            }
            if (!WriteJson(RoutingPath(iDataRoot), j, out oError)) return false;

            SCP_DiscordRoute? aBack = LoadRoutes(iDataRoot, out string? aBackErr).FirstOrDefault(r => r.ChannelId == aCh);
            if (aBackErr != null || aBack == null || aBack.TavernRoom != aRoom || aBack.Enabled != iEnabled
                || (aGuild.Length > 0 && aBack.GuildId != aGuild))
            { oError = "寫完回讀對不上：" + (aBackErr ?? "那一列不是剛寫的值"); return false; }
            return true;
        }

        /// <summary>移除一列（以 channel_id 為鍵）。沒有這一列 ⇒ 擋下、零寫入。</summary>
        public static bool TryRemoveRoute(string iDataRoot, string iChannelId, out string? oError)
        {
            SCP_JsonData j = LoadRoutingJson(iDataRoot, out oError);
            if (oError != null) { oError = "對應表讀不了，⛔ 不覆寫：" + oError; return false; }
            string aCh = (iChannelId ?? "").Trim();
            var aKeep = SCP_JsonData.NewArray();
            int aRemoved = 0;
            foreach (SCP_JsonData m in j["mappings"])
            {
                if (m.GetString("channel_id", "") == aCh) { aRemoved++; continue; }
                aKeep.Add(m);
            }
            if (aRemoved == 0) { oError = $"對應表裡沒有 channel_id={aCh}"; return false; }
            j.Set("mappings", aKeep);
            if (!WriteJson(RoutingPath(iDataRoot), j, out oError)) return false;
            if (LoadRoutes(iDataRoot, out _).Any(r => r.ChannelId == aCh)) { oError = "寫完回讀那一列還在"; return false; }
            return true;
        }

        /// <summary>
        /// 這個頻道**實際**在哪個 Server：先查 Bot 的 Server 快取，查不到才用對應表上寫的 `guild_id`。
        /// 🩸 2026-09-28 Bar 實測：對應表 5 條的 guild_id 全寫同一個 Server，實際分在 4 個 —— 只看表的話，關掉一個 Server 會關錯頻道。
        /// </summary>
        public static string ActualGuildOf(SCP_DiscordGuildCache iCache, SCP_DiscordRoute iRoute)
            => iCache.Guilds.FirstOrDefault(g => g.Channels.Any(c => c.Id == iRoute.ChannelId))?.Id ?? iRoute.GuildId;

        /// <summary>要輪的對應：啟用中、而且所在的 Server 沒被關掉 Inbound。</summary>
        public static List<SCP_DiscordRoute> ActiveRoutes(string iDataRoot, out string? oError)
        {
            List<SCP_DiscordRoute> aRoutes = LoadRoutes(iDataRoot, out oError).Where(r => r.Enabled).ToList();
            List<string> aOff = SCP_DiscordConfigStore.Load(iDataRoot).InboundDisabledGuilds;
            if (aOff.Count == 0) return aRoutes;
            SCP_DiscordGuildCache aCache = SCP_DiscordBot.LoadCache(iDataRoot);
            return aRoutes.Where(r => !aOff.Contains(ActualGuildOf(aCache, r))).ToList();
        }

        // ── 白名單 ───────────────────────────────────────────────────

        public static SCP_DiscordWhitelist LoadWhitelist(string iDataRoot)
        {
            var w = new SCP_DiscordWhitelist();
            string aNew = WhitelistPath(iDataRoot);
            try
            {
                SCP_JsonData? aNode = null;
                if (File.Exists(aNew))
                {
                    aNode = SCP_JsonParser.Parse(File.ReadAllText(aNew, Encoding.UTF8));
                    w.Source = "whitelist";
                }
                else if (File.Exists(NotifyConfigPath(iDataRoot)))
                {
                    SCP_JsonData n = SCP_JsonParser.Parse(File.ReadAllText(NotifyConfigPath(iDataRoot), Encoding.UTF8));
                    SCP_JsonData aOld = n["tavern_inbound"]["user_whitelist"];
                    if (aOld.IsObject) { aNode = aOld; w.Source = "notify_config"; }
                }
                if (aNode == null) return w;
                w.Enabled = aNode.GetBool("enabled", false);
                if (aNode["users"].IsArray)
                    foreach (SCP_JsonData u in aNode["users"])
                        w.Users.Add(new SCP_DiscordWhitelistUser
                        {
                            UserId = u.GetString("user_id", ""),
                            DisplayName = u.GetString("display_name", ""),
                            Profile = u.GetString("profile", ""),
                        });
            }
            catch (Exception e) { w.Error = e.Message; }
            return w;
        }

        /// <summary>讀出白名單的原始節點（保留 aliases 之類的欄位），給寫入用。</summary>
        static SCP_JsonData? LoadWhitelistNode(string iDataRoot, out string? oError)
        {
            oError = null;
            try
            {
                if (File.Exists(WhitelistPath(iDataRoot)))
                    return SCP_JsonParser.Parse(File.ReadAllText(WhitelistPath(iDataRoot), Encoding.UTF8));
                var j = SCP_JsonData.NewObject();
                j.Set("schema_version", 1);
                j.Set("note", "Discord Inbound 使用者白名單（TASK-0319，自 notify_config.json 的 tavern_inbound.user_whitelist 搬來）。enabled=true 時只收 users 裡的人。後台：senate ui --page discord-bot");
                j.Set("enabled", false);
                j.Set("users", SCP_JsonData.NewArray());
                if (File.Exists(NotifyConfigPath(iDataRoot)))
                {
                    SCP_JsonData aOld = SCP_JsonParser.Parse(File.ReadAllText(NotifyConfigPath(iDataRoot), Encoding.UTF8))["tavern_inbound"]["user_whitelist"];
                    if (aOld.IsObject)
                    {
                        j.Set("enabled", aOld.GetBool("enabled", false));
                        if (aOld["users"].IsArray) j.Set("users", aOld["users"]);
                    }
                }
                return j;
            }
            catch (Exception e) { oError = e.Message; return null; }
        }

        public static bool TrySetWhitelistEnabled(string iDataRoot, bool iEnabled, out string? oError)
        {
            SCP_JsonData? j = LoadWhitelistNode(iDataRoot, out oError);
            if (j == null) { oError = "白名單讀不了，⛔ 不覆寫：" + oError; return false; }
            j.Set("enabled", iEnabled);
            if (!WriteJson(WhitelistPath(iDataRoot), j, out oError)) return false;
            SCP_DiscordWhitelist aBack = LoadWhitelist(iDataRoot);
            if (aBack.Source != "whitelist" || aBack.Enabled != iEnabled) { oError = "寫完回讀對不上"; return false; }
            return true;
        }

        /// <summary>新增或更新一人（以 user_id 為鍵；既有欄位如 aliases 原樣保留）。</summary>
        public static bool TryUpsertWhitelistUser(string iDataRoot, string iUserId, string iDisplayName, string iProfile, out string? oError)
        {
            string aId = (iUserId ?? "").Trim();
            if (!IsSnowflake(aId)) { oError = $"user_id 要是 Discord 的數字 id（收到 '{aId}'）"; return false; }
            SCP_JsonData? j = LoadWhitelistNode(iDataRoot, out oError);
            if (j == null) { oError = "白名單讀不了，⛔ 不覆寫：" + oError; return false; }
            if (!j["users"].IsArray) j.Set("users", SCP_JsonData.NewArray());
            SCP_JsonData? aHit = null;
            foreach (SCP_JsonData u in j["users"]) if (u.GetString("user_id", "") == aId) { aHit = u; break; }
            if (aHit == null)
            {
                aHit = SCP_JsonData.NewObject();
                aHit.Set("user_id", aId);
                aHit.Set("aliases", SCP_JsonData.NewArray());
                j["users"].Add(aHit);
            }
            aHit.Set("display_name", (iDisplayName ?? "").Trim());
            aHit.Set("profile", (iProfile ?? "").Trim());
            if (!WriteJson(WhitelistPath(iDataRoot), j, out oError)) return false;
            if (!LoadWhitelist(iDataRoot).Users.Any(u => u.UserId == aId)) { oError = "寫完回讀找不到這個人"; return false; }
            return true;
        }

        public static bool TryRemoveWhitelistUser(string iDataRoot, string iUserId, out string? oError)
        {
            string aId = (iUserId ?? "").Trim();
            SCP_JsonData? j = LoadWhitelistNode(iDataRoot, out oError);
            if (j == null) { oError = "白名單讀不了，⛔ 不覆寫：" + oError; return false; }
            var aKeep = SCP_JsonData.NewArray();
            int aRemoved = 0;
            if (j["users"].IsArray)
                foreach (SCP_JsonData u in j["users"]) { if (u.GetString("user_id", "") == aId) aRemoved++; else aKeep.Add(u); }
            if (aRemoved == 0) { oError = $"白名單裡沒有 user_id={aId}"; return false; }
            j.Set("users", aKeep);
            if (!WriteJson(WhitelistPath(iDataRoot), j, out oError)) return false;
            if (LoadWhitelist(iDataRoot).Users.Any(u => u.UserId == aId)) { oError = "寫完回讀那個人還在"; return false; }
            return true;
        }

        // ── 小工具 ───────────────────────────────────────────────────

        public static bool IsSnowflake(string iId) => iId.Length >= 5 && iId.Length <= 25 && iId.All(char.IsDigit);

        static bool WriteJson(string iPath, SCP_JsonData iJson, out string? oError)
        {
            oError = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iPath) ?? ".");
                string aTmp = iPath + ".tmp";
                File.WriteAllText(aTmp, iJson.ToJson(true), new UTF8Encoding(false));
                SCP.Core.Io.SCP_TextFile.ReplaceOrMove(aTmp, iPath);
                return true;
            }
            catch (Exception e) { oError = $"寫不進去（{iPath}）：{e.Message}"; return false; }
        }
    }
}
