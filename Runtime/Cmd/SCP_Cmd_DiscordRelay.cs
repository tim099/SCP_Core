// 區塊職責：`cmd discord-relay` —— Discord 收發設定的 CLI 出口：In／Out 開關、webhook 管理、分類 → webhook、頭像網址（TASK-0320）。
// 物理意義：讀寫全走 `SCP_DiscordConfigStore`（與 Senate 後台「Discord 轉發設定」「Discord Webhook」頁同一份）；本 Cmd 只是薄殼。
// 數值影響：
//   · `op=webhook-add` 只收**檔案路徑**（`url_path`）⛔ 不收 URL 本身 —— 參數會被記進回傳檔與錯誤報告。
//   · 輸出 ⛔ 不含 webhook URL（只印 id 與快取的 Server／頻道名稱）。
//   · `op=webhook-add／webhook-verify／import-main／avatars check=1` 會連網（要 Senate 宿主的抓取器）；其餘讀本機檔。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SCP.Core.Discord;
using SCP.Core.Letters;
using SCP.Core.Tavern;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_DiscordRelay : SCP_Cmd
    {
        public override string Name => "discord-relay";

        public override string Summary => "Discord 收發設定：In／Out 開關、webhook 管理（加密存檔＋驗證）、頻道分類 → webhook、persona 頭像網址 —— **不需要 Editor**";

        public override string Details =>
            "設定在 `ChatTavern/discord/`（`discord_config.json`＋webhook 密文 `discord_webhooks.enc`）。\n"
            + "· `op=status`（預設）：開關、webhook 清單、分類 → webhook。　`op=switch --arg side=inbound|outbound --arg enabled=0|1`\n"
            + "· `op=webhooks`｜`op=webhook-add --arg url_path=<放 URL 的檔> [--arg label=]`（⛔ 不收 URL 本身）｜`op=webhook-verify --arg id=`\n"
            + "  `op=webhook-enable --arg id= --arg enabled=0|1`｜`op=webhook-remove --arg id=`（還有分類綁著就擋）\n"
            + "· `op=bind --arg category=<分類> --arg ids=<id1,id2…>`（整份取代；空＝不送）｜`op=import-main`（notify_config 的 main ⇒ 綁 Main）\n"
            + "· `op=avatar-template --arg template=<含 {persona} 的 https 網址>`（空＝預設）｜`op=avatars [--arg check=1]`（各 persona 的頭像網址；check=1 逐一 GET）\n"
            + "· `op=backfill --arg room=<房> [--arg from_seq=1] [--arg to_seq=0] [--arg confirm=1]`：把舊訊息補送到這個房的分類綁的 webhook。\n"
            + "  預設只試算；`confirm=1` 才發。只送 chat、不回送 Discord 轉進來的、⛔ 不 @ 任何人；游標在 `discord/discord_backfill_state.json`，重跑從斷點接。\n"
            + "· `op=inbound-peek`：每個接了的 Discord 頻道實打一次 API，列出「現在開 Inbound 會收進哪幾則／略過哪幾則」——⛔ 不寫酒館、不動游標。\n"
            + "· `op=inbound-status`：每個頻道的游標、最後輪詢時間、錯誤、累計收進幾則（Inbound 真的在跑時看這個）。\n"
            + "· `op=guild-inbound --arg guild=<Server id> --arg enabled=0|1`：逐個 Server 開關 Inbound 來源（預設開）。";

        public override string Example => SCP_CmdRegistry.Invoke("discord-relay --arg op=status");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（op=avatars 用）", iDefault: ""),
            new SCP_CmdArgSpec("op", "做什麼", iDefault: "status", iChoices: new[]
            {
                "status", "switch", "webhooks", "webhook-add", "webhook-verify", "webhook-enable", "webhook-remove",
                "bind", "import-main", "avatar-template", "avatars", "backfill", "inbound-peek", "inbound-status", "guild-inbound",
            }),
            new SCP_CmdArgSpec("side", "op=switch：inbound／outbound", iDefault: "", iChoices: new[] { "", "inbound", "outbound" }),
            new SCP_CmdArgSpec("enabled", "op=switch／webhook-enable：1＝開", iDefault: "1", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("url_path", "op=webhook-add：放 webhook URL 的檔（⛔ 不收 URL 本身）", iDefault: ""),
            new SCP_CmdArgSpec("label", "op=webhook-add：自訂名稱（選填）", iDefault: ""),
            new SCP_CmdArgSpec("id", "webhook id", iDefault: ""),
            new SCP_CmdArgSpec("category", "op=bind：頻道分類", iDefault: ""),
            new SCP_CmdArgSpec("ids", "op=bind：webhook id，逗號分隔（空＝這個分類不送）", iDefault: ""),
            new SCP_CmdArgSpec("template", "op=avatar-template：頭像網址範本", iDefault: ""),
            new SCP_CmdArgSpec("check", "op=avatars：1＝逐一 GET 看網址在不在", iDefault: "0", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("room", "op=backfill：要補發的酒館頻道", iDefault: ""),
            new SCP_CmdArgSpec("guild", "op=guild-inbound：Discord Server id", iDefault: ""),
            new SCP_CmdArgSpec("from_seq", "op=backfill：從哪個 seq 起（含）", iDefault: "1"),
            new SCP_CmdArgSpec("to_seq", "op=backfill：到哪個 seq（含；0＝到最新）", iDefault: "0"),
            new SCP_CmdArgSpec("confirm", "op=backfill：1＝真的發（預設只試算）", iDefault: "0", iChoices: new[] { "0", "1" }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aRoot = iArgs.Get("data_root").Trim();
            if (!Directory.Exists(aRoot)) return SCP_CmdResult.Fail(1, $"✗ 資料根不存在：{aRoot}");
            SCP_TavernChannels.EnsureMainChannel(aRoot, out _);   // 主頻道＋保留分類 Main
            string aLetters = iArgs.Get("letters_root").Trim();  // 只有 op=avatars 用；先讀掉，⛔ 別讓其他 op 報「給了沒讀」（它是 senate 自動補的）
            string aId = iArgs.Get("id").Trim();
            switch (iArgs.Get("op").Trim())
            {
                case "switch":
                {
                    string aSide = iArgs.Get("side").Trim();
                    if (aSide.Length == 0) return SCP_CmdResult.Fail(2, "✗ 要 `--arg side=inbound|outbound`");
                    bool aOn = iArgs.Get("enabled").Trim() == "1";
                    if (!SCP_DiscordConfigStore.TrySetSwitch(aRoot, aSide == "inbound", aOn, out string? aErr)) return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    return SCP_CmdResult.Success($"✅ {aSide} ＝ {(aOn ? "開" : "關")}（回讀）");
                }
                case "webhooks": return Webhooks(aRoot);
                case "webhook-add":
                {
                    string aPath = iArgs.Get("url_path").Trim().Trim('"');
                    if (aPath.Length == 0 || !File.Exists(aPath)) return SCP_CmdResult.Fail(2, $"✗ url_path 不存在：'{aPath}'");
                    string aUrl = File.ReadAllText(aPath, Encoding.UTF8).Trim().TrimStart('﻿');
                    if (!SCP_DiscordConfigStore.TryAddWebhook(aRoot, aUrl, iArgs.Get("label"), out string aNewId, out string? aErr))
                        return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    SCP_DiscordWebhookInfo w = SCP_DiscordConfigStore.Load(aRoot).Webhooks.First(x => x.Id == aNewId);
                    return SCP_CmdResult.Success($"✅ 已加入 webhook {aNewId}：{w.Describe()}（URL 已加密存檔；回讀）").AddValue("id", aNewId);
                }
                case "webhook-verify":
                {
                    bool aOk = SCP_DiscordConfigStore.TryReverify(aRoot, aId, out string? aErr);
                    SCP_DiscordWebhookInfo? w = SCP_DiscordConfigStore.Load(aRoot).Webhooks.FirstOrDefault(x => x.Id == aId);
                    return aOk ? SCP_CmdResult.Success($"✅ {aId}：{w?.Describe()}（驗證 ok）")
                               : SCP_CmdResult.Fail(1, $"✗ {aId}：{aErr}");
                }
                case "webhook-enable":
                {
                    bool aOn = iArgs.Get("enabled").Trim() == "1";
                    if (!SCP_DiscordConfigStore.TrySetWebhookEnabled(aRoot, aId, aOn, out string? aErr)) return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    return SCP_CmdResult.Success($"✅ webhook {aId} ＝ {(aOn ? "開" : "停用")}");
                }
                case "webhook-remove":
                {
                    if (!SCP_DiscordConfigStore.TryRemoveWebhook(aRoot, aId, out string? aErr)) return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    return SCP_CmdResult.Success($"✅ 已刪除 webhook {aId}（清單與密文都拿掉了）");
                }
                case "bind":
                {
                    string[] aIds = iArgs.Get("ids").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
                    if (!SCP_DiscordConfigStore.TrySetCategoryWebhooks(aRoot, iArgs.Get("category"), aIds, out string? aErr)) return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    return SCP_CmdResult.Success($"✅ 分類 {iArgs.Get("category").Trim()} ⇒ {(aIds.Length == 0 ? "不送" : aIds.Length + " 條 webhook")}（回讀）");
                }
                case "import-main":
                {
                    List<string> aReport = SCP_DiscordConfigStore.ImportMainFromNotifyConfig(aRoot, out bool aAny);
                    var aR = SCP_CmdResult.Success(aAny ? "✅ 匯入完成" : "（沒有新匯入的）");
                    aR.Lines.AddRange(aReport.Select(x => "- " + x));
                    return aR;
                }
                case "avatar-template":
                {
                    if (!SCP_DiscordConfigStore.TrySetAvatarTemplate(aRoot, iArgs.Get("template"), out string? aErr)) return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    return SCP_CmdResult.Success("✅ 頭像網址範本 ＝ " + SCP_DiscordConfigStore.Load(aRoot).AvatarUrlTemplate);
                }
                case "avatars": return Avatars(aRoot, aLetters, iArgs.Get("check").Trim() == "1");
                case "inbound-peek": return InboundPeek(aRoot);
                case "inbound-status": return InboundStatus(aRoot);
                case "guild-inbound":
                {
                    bool aOn = iArgs.Get("enabled").Trim() == "1";
                    if (!SCP_DiscordConfigStore.TrySetGuildInbound(aRoot, iArgs.Get("guild"), aOn, out string? aErr)) return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    return SCP_CmdResult.Success($"✅ Server {iArgs.Get("guild").Trim()} 的 Inbound ＝ {(aOn ? "收" : "不收")}（回讀）");
                }
                case "backfill":
                {
                    string aRoom = iArgs.Get("room").Trim();
                    if (aRoom.Length == 0) return SCP_CmdResult.Fail(2, "✗ 要 `--arg room=<酒館頻道>`");
                    bool aSend = iArgs.Get("confirm").Trim() == "1";
                    int.TryParse(iArgs.Get("from_seq"), out int aFrom);
                    int.TryParse(iArgs.Get("to_seq"), out int aTo);
                    SCP_DiscordBackfillReport r = SCP_DiscordOutbound.Backfill(aRoot, aLetters, aRoom, aFrom, aTo, !aSend,
                        s => Console.Error.WriteLine("· " + s));
                    var aR = r.Problems.Count > 0 && r.Sent == 0 ? SCP_CmdResult.Fail(1, $"✗ {aRoom}：沒有送出") : SCP_CmdResult.Success(aSend ? $"✅ {aRoom} 補發" : $"（試算）{aRoom} 補發 —— 加 `--arg confirm=1` 才會真的發");
                    aR.Lines.Add($"- 目標：{(r.Targets.Count > 0 ? string.Join("、", r.Targets) : "（沒有）")}");
                    aR.Lines.Add($"- 範圍內 {r.Messages} 則，要送 {r.Eligible} 則；已送過跳過 {r.SkippedAlready}；{(aSend ? "送出" : "將送")} {r.Sent} 則（{r.Posts} 次 POST，含拆段；圖 {r.Images} 張）");
                    foreach (string p in r.Problems) aR.Lines.Add("- ⚠ " + p);
                    aR.AddValue("sent", r.Sent.ToString(CultureInfo.InvariantCulture));
                    aR.AddValue("posts", r.Posts.ToString(CultureInfo.InvariantCulture));
                    aR.AddValue("images", r.Images.ToString(CultureInfo.InvariantCulture));
                    aR.AddValue("problems", r.Problems.Count.ToString(CultureInfo.InvariantCulture));
                    return aR;
                }
                default: return Status(aRoot);
            }
        }

        static SCP_CmdResult InboundPeek(string iRoot)
        {
            List<SCP_DiscordRoute> aRoutes = SCP_DiscordInboundConfig.LoadRoutes(iRoot, out string? aErr).Where(r => r.Enabled).ToList();
            if (aErr != null) return SCP_CmdResult.Fail(1, "✗ 對應表讀不了：" + aErr);
            SCP_DiscordWhitelist aWl = SCP_DiscordInboundConfig.LoadWhitelist(iRoot);
            var aR = SCP_CmdResult.Success($"# Inbound 偷看（{aRoutes.Count} 個頻道；白名單 {aWl.Users.Count} 人（只標記不擋））—— ⛔ 不寫、不動游標");
            int aTotal = 0;
            foreach (SCP_DiscordRoute rt in aRoutes)
            {
                SCP_DiscordPollResult p = SCP_DiscordInbound.PollOnce(iRoot, "", rt, aWl, iPeek: true);
                aR.Lines.Add($"## {rt.Label}（{rt.ChannelId}）→ {rt.TavernRoom}");
                if (!p.Ok) { aR.Lines.Add("- ✗ " + p.Error); continue; }
                if (p.CursorNote.Length > 0) aR.Lines.Add("- 游標：" + p.CursorNote);
                if (p.Baseline) { aR.Lines.Add("- 沒有游標 ⇒ 開了之後從現在開始（歷史不回放）"); continue; }
                aR.Lines.Add($"- 會收進 {p.Items.Count} 則、略過 {p.Skipped.Count} 則");
                foreach (SCP_DiscordInboundItem it in p.Items) aR.Lines.Add("  - 收：" + it.Preview);
                foreach (var g in p.Skipped.GroupBy(s => s.Substring(s.IndexOf(':') + 1))) aR.Lines.Add($"  - 略過：{g.Key} ×{g.Count()}");
                aTotal += p.Items.Count;
            }
            aR.AddValue("would_relay", aTotal.ToString(CultureInfo.InvariantCulture));
            return aR;
        }

        static SCP_CmdResult InboundStatus(string iRoot)
        {
            SCP.Core.Json.SCP_JsonData s = SCP_DiscordInbound.ReadState(iRoot);
            SCP_DiscordConfig c = SCP_DiscordConfigStore.Load(iRoot);
            var aR = SCP_CmdResult.Success($"# Inbound 狀態（開關：{(c.InboundEnabled ? "開" : "關")}）");
            SCP_DiscordGuildCache aCache = SCP_DiscordBot.LoadCache(iRoot);
            var aActive = new HashSet<string>(SCP_DiscordInboundConfig.ActiveRoutes(iRoot, out _).Select(x => x.ChannelId));
            string aGw = SCP_DiscordPaths.Dir(iRoot) + "/discord_gateway_status.json";
            if (File.Exists(aGw))
            {
                SCP.Core.Json.SCP_JsonData gw = SCP.Core.Json.SCP_JsonParser.Parse(File.ReadAllText(aGw, Encoding.UTF8));
                aR.Lines.Add($"- Gateway：{(gw.GetBool("connected", false) ? "上線" : "離線")}　Bot {gw.GetString("bot_name", "?")}　狀態欄「{gw.GetString("presence", "")}」"
                             + (gw.GetString("last_error", "").Length > 0 ? "　⚠ " + gw.GetString("last_error", "") : "") + $"（{gw.GetString("updated_at", "")}）");
            }
            foreach (SCP_DiscordRoute rt in SCP_DiscordInboundConfig.LoadRoutes(iRoot, out _))
            {
                SCP.Core.Json.SCP_JsonData ch = s["channels"][rt.ChannelId];
                string aG = SCP_DiscordInboundConfig.ActualGuildOf(aCache, rt);
                string aGName = aCache.Guilds.FirstOrDefault(x => x.Id == aG)?.Name ?? aG;
                aR.Lines.Add($"- {(aActive.Contains(rt.ChannelId) ? "▶" : "⏸")} [{aGName}{(c.InboundDisabledGuilds.Contains(aG) ? "（Server 關）" : "")}] {rt.Label}（{rt.ChannelId}）→ {rt.TavernRoom}{(rt.Enabled ? "" : "（關）")}：游標 {ch.GetString("last_message_id", "（沒有）")}"
                             + $"　最後輪詢 {ch.GetString("last_poll_at", "-")}　累計 {ch.GetLong("relayed_total", 0)} 則"
                             + (ch.GetString("last_error", "").Length > 0 ? "　⚠ " + ch.GetString("last_error", "") : "")
                             + (ch.GetString("note", "").Length > 0 ? "　（" + ch.GetString("note", "") + "）" : ""));
            }
            return aR;
        }

        static SCP_CmdResult Status(string iRoot)
        {
            SCP_DiscordConfig c = SCP_DiscordConfigStore.Load(iRoot);
            if (c.Error.Length > 0) return SCP_CmdResult.Fail(1, "✗ " + c.Error);
            var aR = SCP_CmdResult.Success("# Discord 收發設定");
            aR.Lines.Add($"- Inbound：{(c.InboundEnabled ? "開" : "關")}　Outbound：{(c.OutboundEnabled ? "開" : "關")}　（由酒館 Server 讀）");
            aR.Lines.Add($"- 頭像網址範本：{c.AvatarUrlTemplate}");
            aR.Lines.Add($"- webhook：{c.Webhooks.Count} 條（停用 {c.Webhooks.Count(w => !w.Enabled)}）");
            aR.Lines.Add("## 頻道分類 → webhook（未分類的頻道不送）");
            foreach (SCP_ChannelCategory cat in SCP_TavernChannels.LoadCategories(iRoot))
            {
                List<string> aIds = c.CategoryWebhooks.TryGetValue(cat.Name, out List<string>? l) ? l : new List<string>();
                aR.Lines.Add($"- **{cat.Name}** ⇒ " + (aIds.Count == 0 ? "（不送）" : string.Join("、", aIds.Select(id =>
                    c.Webhooks.FirstOrDefault(w => w.Id == id)?.Describe() ?? id + "（⚠ 不在清單）"))));
            }
            foreach (string aOrphan in c.CategoryWebhooks.Keys.Where(k => !SCP_TavernChannels.LoadCategories(iRoot).Any(x => string.Equals(x.Name, k, StringComparison.OrdinalIgnoreCase))))
                aR.Lines.Add($"- ⚠ `{aOrphan}` 已經不是頻道分類，但還綁著 webhook（`op=bind --arg category=... --arg ids=` 清掉，或重新建這個分類）");
            aR.AddValue("inbound", c.InboundEnabled ? "1" : "0");
            aR.AddValue("outbound", c.OutboundEnabled ? "1" : "0");
            aR.AddValue("webhooks", c.Webhooks.Count.ToString(CultureInfo.InvariantCulture));
            return aR;
        }

        static SCP_CmdResult Webhooks(string iRoot)
        {
            SCP_DiscordConfig c = SCP_DiscordConfigStore.Load(iRoot);
            var aR = SCP_CmdResult.Success($"# webhook（{c.Webhooks.Count}；URL 只存密文）");
            foreach (SCP_DiscordWebhookInfo w in c.Webhooks)
            {
                List<string> aCats = c.CategoryWebhooks.Where(kv => kv.Value.Contains(w.Id)).Select(kv => kv.Key).ToList();
                aR.Lines.Add($"- `{w.Id}`　{w.Describe()}　{(w.Enabled ? "" : "**停用**　")}驗證：{(w.VerifyStatus.Length > 0 ? w.VerifyStatus : "沒驗過")}（{w.VerifiedAt}）　分類：{(aCats.Count > 0 ? string.Join("、", aCats) : "—")}");
            }
            aR.AddValue("webhooks", c.Webhooks.Count.ToString(CultureInfo.InvariantCulture));
            return aR;
        }

        static SCP_CmdResult Avatars(string iRoot, string iLetters, bool iCheck)
        {
            if (iLetters.Length == 0 || !Directory.Exists(iLetters)) return SCP_CmdResult.Fail(2, $"✗ 要 `--arg letters_root=<persona 信件夾根>`（收到 '{iLetters}'）");
            string aT = SCP_DiscordConfigStore.Load(iRoot).AvatarUrlTemplate;
            var aR = SCP_CmdResult.Success($"# persona 的 Discord 頭像網址（範本：{aT}）");
            int aBad = 0;
            foreach (string p in SCP_PersonaDisplay.ListPersonas(iLetters))
            {
                string aUrl = SCP_DiscordConfigStore.ResolveAvatarUrl(iLetters, p, aT, out bool aExplicit);
                string aState = "";
                if (iCheck)
                {
                    int aCode = SCP_DiscordConfigStore.ProbeUrl(aUrl, out string? aErr);
                    aState = aCode == 200 ? "　✓ 200" : $"　**✗ {(aCode > 0 ? "HTTP " + aCode : aErr)}**";
                    if (aCode != 200) aBad++;
                }
                aR.Lines.Add($"- `{p}`　{(aExplicit ? "（profile 自填）" : "（範本）")}{aUrl}{aState}");
            }
            if (iCheck) aR.AddValue("bad", aBad.ToString(CultureInfo.InvariantCulture));
            return aR;
        }
    }
}
