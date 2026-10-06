// 區塊職責：`cmd discord-bot` —— Discord Bot 設定的 CLI 出口：憑證、測試連線、Server／頻道清單、頻道對應、白名單（TASK-0319）。
// 物理意義：讀寫全走 `SCP_DiscordBot`／`SCP_DiscordInboundConfig`（與 Senate 後台「Discord Bot」頁同一份）；本 Cmd 只是薄殼。
//           完全不需要 Unity（Unity 端 Inbound 已廢棄，Tim 2026-09-28）。
// 數值影響：
//   · `op=set-token` 只收**檔案路徑**（`token_path`／`passphrase_path`），⛔ 不收 token 本身 ——
//     指令參數會被記進回傳檔與錯誤報告（`_cmd_errors` 會逐條印 Args），token 一旦進去就散出去了。
//   · `op=test`／`op=refresh` 會連 Discord（要 Senate 宿主的抓取器）；其餘讀本機檔。
//   · 輸出 ⛔ 不含 token。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SCP.Core.Discord;
using SCP.Core.Tavern;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_DiscordBot : SCP_Cmd
    {
        public override string Name => "discord-bot";
        public override string Category => SCP_CmdCategory.Tavern;

        public override string Summary => "Discord Bot 設定：token（一步加密＋安裝）、測試連線、Bot 加入的 Server／頻道、Discord 頻道 → 酒館頻道對應、Inbound 白名單 —— **不需要 Editor**";

        public override string Details =>
            "· `op=status`（預設）：憑證狀態（.enc／明文／環境變數）、快取的 Server 數、對應表、白名單摘要。⛔ 不印 token。\n"
            + "· `op=set-token --arg token_path=<檔> --arg passphrase_path=<檔> [--arg hint=...] [--arg overwrite=1]`：\n"
            + "  把檔裡的 token 加密成 `discord_bot_token.enc` 並**同時**寫出明文（本機用）。⛔ 只收路徑，不收 token 本身（參數會進 log）。\n"
            + "· `op=test`：`GET /users/@me` ⇒ Bot 名稱與 id。　`op=refresh`：重抓 Server／頻道清單寫快取。　`op=servers`：印快取。\n"
            + "· `op=routes`：對應表。　`op=route --arg channel=<id> [--arg room=<酒館頻道，預設 tavern>] [--arg enabled=1] [--arg source_class=...] [--arg label=...] [--arg guild=<id>]`\n"
            + "  `op=unroute --arg channel=<id>`。酒館頻道要存在且沒封存。\n"
            + "· `op=whitelist`／`op=whitelist-add --arg user_id=<id> [--arg display_name=] [--arg profile=]`／`op=whitelist-remove --arg user_id=<id>`。";

        public override string Example => SCP_CmdRegistry.Invoke("discord-bot --arg op=status");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("op", "做什麼", iDefault: "status", iChoices: new[]
            {
                "status", "set-token", "test", "refresh", "servers", "routes", "route", "unroute",
                "whitelist", "whitelist-add", "whitelist-remove",
            }),
            new SCP_CmdArgSpec("token_path", "op=set-token：放 token 的檔（讀完不動它；⛔ 不收 token 本身）", iDefault: ""),
            new SCP_CmdArgSpec("passphrase_path", "op=set-token：放密碼的檔", iDefault: ""),
            new SCP_CmdArgSpec("hint", "op=set-token：密碼提示（不參與加密）", iDefault: ""),
            new SCP_CmdArgSpec("overwrite", "op=set-token：1＝允許覆寫既有 .enc", iDefault: "0", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("channel", "op=route／unroute：Discord 頻道 id", iDefault: ""),
            new SCP_CmdArgSpec("room", "op=route：酒館頻道（空＝主頻道 tavern）", iDefault: ""),
            new SCP_CmdArgSpec("enabled", "op=route：1＝開", iDefault: "1", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("source_class", "op=route：external／internal／work…（空＝既有列不動、新列 external）", iDefault: ""),
            new SCP_CmdArgSpec("label", "op=route：顯示名（空＝不動）", iDefault: ""),
            new SCP_CmdArgSpec("guild", "op=route：Discord Server id（新列寫入；既有列給了就覆寫，結果印舊 → 新）", iDefault: ""),
            new SCP_CmdArgSpec("user_id", "op=whitelist-add／remove：Discord 使用者 id", iDefault: ""),
            new SCP_CmdArgSpec("display_name", "op=whitelist-add：酒館上顯示的名字", iDefault: ""),
            new SCP_CmdArgSpec("profile", "op=whitelist-add：身分說明", iDefault: ""),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aRoot = iArgs.Get("data_root").Trim();
            if (!Directory.Exists(aRoot)) return SCP_CmdResult.Fail(1, $"✗ 資料根不存在：{aRoot}");
            SCP_TavernChannels.EnsureMainChannel(aRoot, out _);
            string aOp = iArgs.Get("op").Trim();
            switch (aOp)
            {
                case "status": return Status(aRoot);
                case "set-token": return SetToken(aRoot, iArgs);
                case "test":
                {
                    if (!SCP_DiscordBot.TryTestConnection(aRoot, out string aName, out string aId, out int aCode, out string? aErr))
                        return SCP_CmdResult.Fail(aCode == 401 ? 3 : 1, "✗ " + aErr).AddValue("http_status", aCode.ToString(CultureInfo.InvariantCulture));
                    return SCP_CmdResult.Success($"✅ 連上了：Bot **{aName}**（id {aId}）").AddValue("bot_name", aName).AddValue("bot_id", aId);
                }
                case "refresh":
                {
                    if (!SCP_DiscordBot.TryRefreshGuilds(aRoot, out SCP_DiscordGuildCache aCache, out string? aErr))
                        return SCP_CmdResult.Fail(1, "✗ " + aErr + "（快取沒動）");
                    SCP_CmdResult aR = Servers(aRoot);
                    aR.Lines.Insert(0, $"✅ 已重新整理（Bot {aCache.BotName}）");
                    return aR;
                }
                case "servers": return Servers(aRoot);
                case "routes": return Routes(aRoot);
                case "route":
                {
                    string aCh = iArgs.Get("channel").Trim();
                    string aOldGuild = SCP_DiscordInboundConfig.LoadRoutes(aRoot, out _).FirstOrDefault(x => x.ChannelId == aCh)?.GuildId ?? "";
                    if (!SCP_DiscordInboundConfig.TryUpsertRoute(aRoot, aCh, iArgs.Get("guild"), iArgs.Get("label"), iArgs.Get("room"),
                            iArgs.Get("enabled").Trim() == "1", iArgs.Get("source_class"), out string? aErr))
                        return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    SCP_DiscordRoute r = SCP_DiscordInboundConfig.LoadRoutes(aRoot, out _).First(x => x.ChannelId == aCh);
                    string aGuildNote = aOldGuild == r.GuildId ? $"guild {r.GuildId}" : $"guild {aOldGuild} → {r.GuildId}";
                    return SCP_CmdResult.Success($"✅ Discord `{aCh}` → 酒館 **{r.TavernRoom}**（{(r.Enabled ? "開" : "關")}，{r.SourceClass}，{aGuildNote}；回讀）")
                        .AddValue("tavern_room", r.TavernRoom)
                        .AddValue("guild_id", r.GuildId);
                }
                case "unroute":
                {
                    string aCh = iArgs.Get("channel").Trim();
                    if (!SCP_DiscordInboundConfig.TryRemoveRoute(aRoot, aCh, out string? aErr)) return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    return SCP_CmdResult.Success($"✅ 已移除 Discord `{aCh}` 的對應（回讀）");
                }
                case "whitelist": return Whitelist(aRoot);
                case "whitelist-add":
                {
                    if (!SCP_DiscordInboundConfig.TryUpsertWhitelistUser(aRoot, iArgs.Get("user_id"), iArgs.Get("display_name"), iArgs.Get("profile"), out string? aErr))
                        return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    return SCP_CmdResult.Success($"✅ 白名單已加入／更新 `{iArgs.Get("user_id").Trim()}`（回讀）");
                }
                default: // whitelist-remove
                {
                    if (!SCP_DiscordInboundConfig.TryRemoveWhitelistUser(aRoot, iArgs.Get("user_id"), out string? aErr))
                        return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    return SCP_CmdResult.Success($"✅ 白名單已移除 `{iArgs.Get("user_id").Trim()}`（回讀）");
                }
            }
        }

        static SCP_CmdResult SetToken(string iRoot, SCP_CmdArgs iArgs)
        {
            string aTokPath = iArgs.Get("token_path").Trim().Trim('"');
            string aPassPath = iArgs.Get("passphrase_path").Trim().Trim('"');
            if (aTokPath.Length == 0 || !File.Exists(aTokPath)) return SCP_CmdResult.Fail(2, $"✗ token_path 不存在：'{aTokPath}'");
            if (aPassPath.Length == 0 || !File.Exists(aPassPath)) return SCP_CmdResult.Fail(2, $"✗ passphrase_path 不存在：'{aPassPath}'");
            string aToken = File.ReadAllText(aTokPath, Encoding.UTF8).Trim().TrimStart('﻿');
            string aPass = File.ReadAllText(aPassPath, Encoding.UTF8).TrimEnd('\r', '\n').TrimStart('﻿');
            if (!SCP_DiscordBot.TrySetToken(iRoot, aToken, aPass, iArgs.Get("hint"), iArgs.Get("overwrite").Trim() == "1", out string? aErr))
                return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
            SCP_DiscordTokenStatus s = SCP_DiscordBot.Status(iRoot);
            return SCP_CmdResult.Success("✅ 已寫入 .enc 與本機明文（回讀）",
                $"- .enc：`{s.EncPath}`（要自己提交到 Secret repo）",
                $"- 明文：`{s.PlainPath}`（本機用；Secret repo 的 .gitignore 擋掉，⛔ 不會進 git）",
                s.EnvOverride ? $"⚠ 環境變數 `{SCP_DiscordBot.TokenEnvVar}` 有值 ⇒ 實際用的是它，不是剛寫的這份" : "");
        }

        static SCP_CmdResult Status(string iRoot)
        {
            SCP_DiscordTokenStatus s = SCP_DiscordBot.Status(iRoot);
            SCP_DiscordGuildCache c = SCP_DiscordBot.LoadCache(iRoot);
            List<SCP_DiscordRoute> aRoutes = SCP_DiscordInboundConfig.LoadRoutes(iRoot, out string? aRouteErr);
            SCP_DiscordWhitelist w = SCP_DiscordInboundConfig.LoadWhitelist(iRoot);
            var aR = SCP_CmdResult.Success("# Discord Bot 設定");
            aR.Lines.Add($"- 憑證：.enc {(s.EncExists ? "有" : "**沒有**")}　明文 {(s.PlainExists ? "已安裝" : "**沒有**")}　環境變數 {(s.EnvOverride ? "**有值（蓋過明文）**" : "沒有")}　⇒ {(s.Ready ? "讀得到 token" : "**讀不到 token**")}");
            if (s.DirWarning.Length > 0) aR.Lines.Add("  ⚠ " + s.DirWarning);
            aR.Lines.Add(c.Exists ? $"- Server 快取：{c.Guilds.Count} 個（Bot {c.BotName}，{c.FetchedAt}）" : "- Server 快取：還沒抓過（`op=refresh`）");
            aR.Lines.Add(aRouteErr != null ? "- 對應表：✗ " + aRouteErr : $"- 對應表：{aRoutes.Count} 條（開 {aRoutes.Count(r => r.Enabled)}）");
            aR.Lines.Add($"- 白名單：{w.Users.Count} 人（不擋人，白名單外的顯示名標「{SCP_DiscordInbound.NotWhitelistedSuffix}」；來源：{WhitelistSource(w)}）");
            aR.AddValue("token_ready", s.Ready ? "1" : "0");
            aR.AddValue("enc", s.EncExists ? "1" : "0");
            aR.AddValue("plain", s.PlainExists ? "1" : "0");
            aR.AddValue("routes", aRoutes.Count.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("whitelist_source", w.Source);
            return aR;
        }

        public static string WhitelistSource(SCP_DiscordWhitelist w)
            => w.Source == "whitelist" ? "discord_inbound_whitelist.json"
             : w.Source == "notify_config" ? "notify_config.json（還沒搬家；第一次存檔就搬）" : "沒有";

        static SCP_CmdResult Servers(string iRoot)
        {
            SCP_DiscordGuildCache c = SCP_DiscordBot.LoadCache(iRoot);
            if (!c.Exists) return SCP_CmdResult.Fail(1, "✗ 還沒抓過 Server 清單 ⇒ `op=refresh`");
            var aMap = SCP_DiscordInboundConfig.LoadRoutes(iRoot, out _).ToDictionary(r => r.ChannelId, r => r, StringComparer.Ordinal);
            var aR = SCP_CmdResult.Success($"# Bot {c.BotName} 加入的 Server（{c.Guilds.Count}；{c.FetchedAt}）");
            foreach (SCP_DiscordGuild g in c.Guilds)
            {
                aR.Lines.Add($"## {g.Name}（{g.Id}）　{g.Channels.Count} 個文字頻道{(g.Error.Length > 0 ? "　⚠ " + g.Error : "")}");
                foreach (SCP_DiscordChannel ch in g.Channels)
                {
                    string aTo = aMap.TryGetValue(ch.Id, out SCP_DiscordRoute? r) ? $"→ **{r.TavernRoom}**{(r.Enabled ? "" : "（關）")}" : "未接";
                    aR.Lines.Add($"- {(ch.ParentName.Length > 0 ? ch.ParentName + " / " : "")}#{ch.Name}（{ch.Id}）　{aTo}");
                }
            }
            var aKnown = new HashSet<string>(c.Guilds.SelectMany(g => g.Channels).Select(x => x.Id), StringComparer.Ordinal);
            var aOrphans = aMap.Values.Where(r => !aKnown.Contains(r.ChannelId)).ToList();
            if (aOrphans.Count > 0)
            {
                aR.Lines.Add($"## ⚠ 對應表裡有、Bot 卻看不到的頻道（{aOrphans.Count}）");
                foreach (SCP_DiscordRoute r in aOrphans) aR.Lines.Add($"- {r.Label}（{r.ChannelId}）→ {r.TavernRoom}");
            }
            aR.AddValue("guilds", c.Guilds.Count.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("orphans", aOrphans.Count.ToString(CultureInfo.InvariantCulture));
            return aR;
        }

        static SCP_CmdResult Routes(string iRoot)
        {
            List<SCP_DiscordRoute> aRoutes = SCP_DiscordInboundConfig.LoadRoutes(iRoot, out string? aErr);
            if (aErr != null) return SCP_CmdResult.Fail(1, "✗ " + aErr);
            var aR = SCP_CmdResult.Success($"# Discord 頻道 → 酒館頻道（{aRoutes.Count}）");
            aR.Lines.Add("| Discord 頻道 | id | → 酒館頻道 | 開 | source_class |");
            aR.Lines.Add("|---|---|---|---|---|");
            foreach (SCP_DiscordRoute r in aRoutes)
                aR.Lines.Add($"| {r.Label} | {r.ChannelId} | {r.TavernRoom} | {(r.Enabled ? "開" : "關")} | {r.SourceClass} |");
            aR.AddValue("routes", aRoutes.Count.ToString(CultureInfo.InvariantCulture));
            return aR;
        }

        static SCP_CmdResult Whitelist(string iRoot)
        {
            SCP_DiscordWhitelist w = SCP_DiscordInboundConfig.LoadWhitelist(iRoot);
            if (w.Error.Length > 0) return SCP_CmdResult.Fail(1, "✗ 白名單讀不了：" + w.Error);
            var aR = SCP_CmdResult.Success($"# Inbound 白名單（{w.Users.Count} 人；不擋人，只標記；來源：{WhitelistSource(w)}）");
            foreach (SCP_DiscordWhitelistUser u in w.Users)
                aR.Lines.Add($"- `{u.UserId}`　{u.DisplayName}{(u.Profile.Length > 0 ? "（" + u.Profile + "）" : "")}");
            aR.AddValue("users", w.Users.Count.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("source", w.Source);
            return aR;
        }
    }
}
