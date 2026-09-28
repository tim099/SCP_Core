// 區塊職責：**Discord 收發設定**（TASK-0320）—— `ChatTavern/discord/discord_config.json` 的讀寫，
//           以及 webhook 的管理（URL 加密存獨立檔、驗證、快取頻道名稱）與「頻道分類 → webhook」。唯一讀寫層。
// 物理意義：
//   · 開關：`inbound.enabled`／`outbound.enabled` —— 由酒館 Server 讀（Tim 2026-09-28：簡易開關，不處理啟動）。
//   · webhook 清單（id／label／快取的 webhook 名稱、Server、頻道名稱／開關／最後驗證結果）存在 json，**URL 不在 json**：
//     URL 以**寫死的金鑰**加密成 `discord_webhooks.enc`（Tim 2026-09-28：webhook 被破解影響不大，目的只是不裸存上 git ——
//     ⇒ 不必輸入密碼；密文與 json 同資料夾但不同檔）。
//   · 分類 → webhook：`outbound.category_webhooks`（`{"Main": ["<webhook id>", …]}`），一個分類可同步到多條；
//     ⛔ 未分類的頻道不送（Tim：webhook 綁分類，未分類無法綁定）。
//   · 頭像網址範本：`outbound.avatar_url_template`（`{persona}` 會被換掉）；persona 自己填了 `profile/avatar_url.md` 就用那個。
// 數值影響：讀零寫入；寫入走暫存檔再換檔、寫完回讀；驗證不過一律零寫入。⛔ URL 不出現在回傳值、錯誤訊息、log。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Market;
using SCP.Core.Secret;
using SCP.Core.Tavern;

namespace SCP.Core.Discord
{
    /// <summary>一條 webhook 的管理資料（⛔ 不含 URL）。</summary>
    public sealed class SCP_DiscordWebhookInfo
    {
        /// <summary>Discord 的 webhook id（URL 裡 `/webhooks/&lt;id&gt;/&lt;token&gt;` 的 id）。</summary>
        public string Id = "";
        public string Label = "";
        public bool Enabled = true;
        // ── 驗證時快取 ──
        public string WebhookName = "";
        public string GuildId = "";
        public string GuildName = "";
        public string ChannelId = "";
        public string ChannelName = "";
        public string VerifiedAt = "";
        /// <summary>最後一次驗證的結果：`ok`／`HTTP 404`…；空 ＝ 沒驗過。</summary>
        public string VerifyStatus = "";

        /// <summary>給人看的一行：`CreamTime / #cream-chat（webhook 名）`。</summary>
        public string Describe()
        {
            string aWhere = (GuildName.Length > 0 ? GuildName + " / " : "") + (ChannelName.Length > 0 ? "#" + ChannelName : (ChannelId.Length > 0 ? "頻道 " + ChannelId : "（頻道未知）"));
            string aName = Label.Length > 0 ? Label : WebhookName;
            return aWhere + (aName.Length > 0 ? "（" + aName + "）" : "");
        }
    }

    public sealed class SCP_DiscordConfig
    {
        public bool InboundEnabled;
        /// <summary>關掉 Inbound 的 Discord Server id（預設空 ＝ 全部都收；Tim 2026-09-28）。</summary>
        public List<string> InboundDisabledGuilds = new List<string>();
        public bool OutboundEnabled;
        public string AvatarUrlTemplate = SCP_DiscordConfigStore.DefaultAvatarUrlTemplate;
        public List<SCP_DiscordWebhookInfo> Webhooks = new List<SCP_DiscordWebhookInfo>();
        /// <summary>頻道分類 → webhook id 清單（分類名大小寫以分類清單為準）。</summary>
        public Dictionary<string, List<string>> CategoryWebhooks = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        /// <summary>設定檔讀不了（壞檔）⇒ 這裡有字；呼叫端 ⛔ 不可以拿預設值覆寫它。</summary>
        public string Error = "";
    }

    public static class SCP_DiscordConfigStore
    {
        public const int SchemaVersion = 1;
        public const string DefaultAvatarUrlTemplate = "https://raw.githubusercontent.com/tim099/ArtGallery/master/RawImages/avatar_{persona}.png";

        /// <summary>
        /// webhook 密文的金鑰 —— **刻意寫死**（Tim 2026-09-28）：目的只是「不裸存上 git」，不是防有心人。
        /// ⚠ 所以 bot token ⛔ 不走這把（那個走 Secret 頁、要密碼）。
        /// </summary>
        const string WebhookKey = "senate-discord-webhooks/v1/not-a-secret-just-not-plaintext";

        static readonly Regex s_WebhookUrl = new Regex(
            @"^https://(?:(?:canary|ptb)\.)?discord(?:app)?\.com/api/(?:v\d+/)?webhooks/(\d{5,25})/([A-Za-z0-9_\-]{20,200})/?$",
            RegexOptions.CultureInvariant);

        /// <summary>URL 是 Discord webhook 的樣子嗎；是 ⇒ 回 webhook id。</summary>
        public static bool TryParseWebhookUrl(string iUrl, out string oId)
        {
            oId = "";
            Match m = s_WebhookUrl.Match((iUrl ?? "").Trim());
            if (!m.Success) return false;
            oId = m.Groups[1].Value;
            return true;
        }

        // ── 讀／寫 discord_config.json ─────────────────────────────────

        public static SCP_DiscordConfig Load(string iDataRoot)
        {
            var c = new SCP_DiscordConfig();
            string aPath = SCP_DiscordPaths.ConfigPath(iDataRoot);
            if (!File.Exists(aPath)) return c;
            try
            {
                SCP_JsonData j = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                c.InboundEnabled = j["inbound"].GetBool("enabled", false);
                foreach (SCP_JsonData g in j["inbound"]["disabled_guilds"]) if (g.IsString) c.InboundDisabledGuilds.Add(g.AsString());
                SCP_JsonData o = j["outbound"];
                c.OutboundEnabled = o.GetBool("enabled", false);
                c.AvatarUrlTemplate = o.GetString("avatar_url_template", DefaultAvatarUrlTemplate);
                if (o["category_webhooks"].IsObject)
                    foreach (string aCat in o["category_webhooks"].Keys)
                    {
                        var aIds = new List<string>();
                        foreach (SCP_JsonData id in o["category_webhooks"][aCat]) if (id.IsString) aIds.Add(id.AsString());
                        c.CategoryWebhooks[aCat] = aIds;
                    }
                if (j["webhooks"].IsArray)
                    foreach (SCP_JsonData w in j["webhooks"])
                        c.Webhooks.Add(new SCP_DiscordWebhookInfo
                        {
                            Id = w.GetString("id", ""), Label = w.GetString("label", ""), Enabled = w.GetBool("enabled", true),
                            WebhookName = w.GetString("webhook_name", ""), GuildId = w.GetString("guild_id", ""), GuildName = w.GetString("guild_name", ""),
                            ChannelId = w.GetString("channel_id", ""), ChannelName = w.GetString("channel_name", ""),
                            VerifiedAt = w.GetString("verified_at", ""), VerifyStatus = w.GetString("verify_status", ""),
                        });
            }
            catch (Exception e) { c.Error = $"讀不了 {aPath}：{e.Message}"; }
            return c;
        }

        static bool Save(string iDataRoot, SCP_DiscordConfig c, out string? oError)
        {
            oError = null;
            if (c.Error.Length > 0) { oError = "設定檔讀不了，⛔ 不覆寫：" + c.Error; return false; }
            var j = SCP_JsonData.NewObject();
            j.Set("schema_version", SchemaVersion);
            j.Set("note", "Discord 收發設定（TASK-0320）。webhook URL 不在這裡 —— 密文在同資料夾的 " + SCP_DiscordPaths.WebhookSecretFileName + "。後台：senate ui --page discord-relay／discord-webhooks");
            var aIn = SCP_JsonData.NewObject(); aIn.Set("enabled", c.InboundEnabled);
            var aDis = SCP_JsonData.NewArray(); foreach (string g in c.InboundDisabledGuilds.Distinct(StringComparer.Ordinal)) aDis.Add(g);
            aIn.Set("disabled_guilds", aDis);
            j.Set("inbound", aIn);
            var aOut = SCP_JsonData.NewObject();
            aOut.Set("enabled", c.OutboundEnabled);
            aOut.Set("avatar_url_template", c.AvatarUrlTemplate);
            var aMap = SCP_JsonData.NewObject();
            foreach (var kv in c.CategoryWebhooks.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
            {
                var aArr = SCP_JsonData.NewArray();
                foreach (string id in kv.Value) aArr.Add(id);
                aMap.Set(kv.Key, aArr);
            }
            aOut.Set("category_webhooks", aMap);
            j.Set("outbound", aOut);
            var aHooks = SCP_JsonData.NewArray();
            foreach (SCP_DiscordWebhookInfo w in c.Webhooks)
            {
                var o = SCP_JsonData.NewObject();
                o.Set("id", w.Id); o.Set("label", w.Label); o.Set("enabled", w.Enabled);
                o.Set("webhook_name", w.WebhookName); o.Set("guild_id", w.GuildId); o.Set("guild_name", w.GuildName);
                o.Set("channel_id", w.ChannelId); o.Set("channel_name", w.ChannelName);
                o.Set("verified_at", w.VerifiedAt); o.Set("verify_status", w.VerifyStatus);
                aHooks.Add(o);
            }
            j.Set("webhooks", aHooks);
            try
            {
                WriteReplace(SCP_DiscordPaths.ConfigPath(iDataRoot), new UTF8Encoding(false).GetBytes(j.ToJson(true)));
                return true;
            }
            catch (Exception e) { oError = $"寫不進去：{e.Message}"; return false; }
        }

        // ── 開關與範本 ───────────────────────────────────────────────

        public static bool TrySetSwitch(string iDataRoot, bool iInbound, bool iEnabled, out string? oError)
        {
            SCP_DiscordConfig c = Load(iDataRoot);
            if (iInbound) c.InboundEnabled = iEnabled; else c.OutboundEnabled = iEnabled;
            if (!Save(iDataRoot, c, out oError)) return false;
            SCP_DiscordConfig b = Load(iDataRoot);
            if ((iInbound ? b.InboundEnabled : b.OutboundEnabled) != iEnabled) { oError = "寫完回讀對不上"; return false; }
            return true;
        }

        /// <summary>開關某個 Discord Server 的 Inbound（預設開）。</summary>
        public static bool TrySetGuildInbound(string iDataRoot, string iGuildId, bool iEnabled, out string? oError)
        {
            string aId = (iGuildId ?? "").Trim();
            if (!SCP_DiscordInboundConfig.IsSnowflake(aId)) { oError = $"Server id 要是 Discord 的數字 id（收到 '{aId}'）"; return false; }
            SCP_DiscordConfig c = Load(iDataRoot);
            c.InboundDisabledGuilds.RemoveAll(x => x == aId);
            if (!iEnabled) c.InboundDisabledGuilds.Add(aId);
            if (!Save(iDataRoot, c, out oError)) return false;
            if (Load(iDataRoot).InboundDisabledGuilds.Contains(aId) == iEnabled) { oError = "寫完回讀對不上"; return false; }
            return true;
        }

        public static bool TrySetAvatarTemplate(string iDataRoot, string iTemplate, out string? oError)
        {
            oError = null;
            string aT = (iTemplate ?? "").Trim();
            if (aT.Length == 0) aT = DefaultAvatarUrlTemplate;
            if (!aT.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || !aT.Contains("{persona}"))
            { oError = "範本要是 https 開頭、而且含 {persona}"; return false; }
            SCP_DiscordConfig c = Load(iDataRoot);
            c.AvatarUrlTemplate = aT;
            return Save(iDataRoot, c, out oError);
        }

        /// <summary>
        /// persona 在 Discord 上用的頭像網址：persona 自己填的 `avatar_url.md` ＞ 範本（`{persona}` 換成 id）。
        /// 系統身分與沒有信件夾的寄件人也照範本算（網址不存在時 Discord 會退回 webhook 自己的頭像）。
        /// </summary>
        public static string ResolveAvatarUrl(string iLettersRoot, string iPersona, string iTemplate, out bool oExplicit)
        {
            oExplicit = false;
            if (string.IsNullOrEmpty(iPersona)) return "";
            SCP_PersonaDisplayInfo aInfo = SCP_PersonaDisplay.Get(iLettersRoot ?? "", iPersona);
            if (aInfo.AvatarUrl.Length > 0) { oExplicit = true; return aInfo.AvatarUrl; }
            string aT = string.IsNullOrEmpty(iTemplate) ? DefaultAvatarUrlTemplate : iTemplate;
            return aT.Replace("{persona}", Uri.EscapeDataString(iPersona));
        }

        /// <summary>檢查一個公開網址拿不拿得到（GET，不帶標頭）。回傳 HTTP 狀態碼（0 ＝ 沒拿到回應）。</summary>
        public static int ProbeUrl(string iUrl, out string? oError)
        {
            oError = null;
            if (!(SCP_HttpFetch.Current is ISCP_HttpHeaderFetcher aFetch)) { oError = "本宿主沒有能回狀態碼的抓取器（要在 Senate 跑）"; return 0; }
            aFetch.TryGetText(iUrl, new Dictionary<string, string>(), 15, out _, out int aStatus, out string? aErr);
            if (aStatus == 0) oError = aErr;
            return aStatus;
        }

        // ── webhook 密文（URL 本體）──────────────────────────────────

        /// <summary>讀全部 webhook URL（id → URL）。⛔ 只給本檔與收發本體用；檔不在 ⇒ 空。壞檔 ⇒ <paramref name="oError"/>。</summary>
        public static Dictionary<string, string> LoadWebhookUrls(string iDataRoot, out string? oError)
        {
            oError = null;
            var aOut = new Dictionary<string, string>(StringComparer.Ordinal);
            string aPath = SCP_DiscordPaths.WebhookSecretPath(iDataRoot);
            if (!File.Exists(aPath)) return aOut;
            try
            {
                byte[] aPlain = SCP_SecretCrypto.Decrypt(File.ReadAllBytes(aPath), WebhookKey);
                SCP_JsonData j = SCP_JsonParser.Parse(new UTF8Encoding(false).GetString(aPlain));
                foreach (string aId in j.Keys) aOut[aId] = j[aId].AsString();
            }
            catch (Exception e) { oError = $"webhook 密文解不開（{Path.GetFileName(aPath)}）：{e.GetType().Name}"; }
            return aOut;
        }

        static bool SaveWebhookUrls(string iDataRoot, Dictionary<string, string> iUrls, out string? oError)
        {
            oError = null;
            var j = SCP_JsonData.NewObject();
            foreach (var kv in iUrls.OrderBy(k => k.Key, StringComparer.Ordinal)) j.Set(kv.Key, kv.Value);
            byte[] aPlain = new UTF8Encoding(false).GetBytes(j.ToJson(false));
            try
            {
                byte[] aEnc = SCP_SecretCrypto.Encrypt(aPlain, WebhookKey, "", "discord webhooks (TASK-0320)");
                if (!SCP_SecretCrypto.Decrypt(aEnc, WebhookKey).SequenceEqual(aPlain)) { oError = "加密後回解對不上"; return false; }
                WriteReplace(SCP_DiscordPaths.WebhookSecretPath(iDataRoot), aEnc);
                return true;
            }
            catch (Exception e) { oError = $"webhook 密文寫不進去：{e.GetType().Name}: {e.Message}"; return false; }
        }

        // ── webhook 管理 ─────────────────────────────────────────────

        /// <summary>
        /// 驗證一個 webhook URL：`GET <url>`（不必 Bot）⇒ webhook 名稱、guild_id、channel_id；
        /// 頻道名稱與 Server 名稱從 Bot 的 Server 快取（TASK-0319）查 —— 查不到就只留 id。
        /// </summary>
        static bool TryVerify(string iDataRoot, string iUrl, SCP_DiscordWebhookInfo ioInfo, out string? oError)
        {
            oError = null;
            if (!(SCP_HttpFetch.Current is ISCP_HttpHeaderFetcher aFetch))
            { oError = "本宿主沒有能帶標頭的抓取器（要在 Senate 跑）"; return false; }
            bool aOk = aFetch.TryGetText(iUrl, new Dictionary<string, string>(), 15, out string aBody, out int aStatus, out _);
            ioInfo.VerifiedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            if (!aOk)
            {
                // ⛔ 不把抓取器的錯誤原文帶出來：它可能含回應前綴，而我們只要狀態碼
                ioInfo.VerifyStatus = aStatus > 0 ? "HTTP " + aStatus : "連不上";
                oError = $"驗證失敗：{ioInfo.VerifyStatus}" + (aStatus == 401 || aStatus == 404 ? "（webhook 不存在或已被刪除）" : "");
                return false;
            }
            try
            {
                SCP_JsonData j = SCP_JsonParser.Parse(aBody);
                ioInfo.WebhookName = j.GetString("name", "");
                ioInfo.GuildId = j.GetString("guild_id", "");
                ioInfo.ChannelId = j.GetString("channel_id", "");
            }
            catch (Exception) { ioInfo.VerifyStatus = "回應解析不了"; oError = "驗證失敗：回應解析不了"; return false; }
            SCP_DiscordGuildCache aCache = SCP_DiscordBot.LoadCache(iDataRoot);
            SCP_DiscordGuild? g = aCache.Guilds.FirstOrDefault(x => x.Id == ioInfo.GuildId);
            ioInfo.GuildName = g?.Name ?? "";
            ioInfo.ChannelName = g?.Channels.FirstOrDefault(c => c.Id == ioInfo.ChannelId)?.Name ?? "";
            ioInfo.VerifyStatus = "ok";
            return true;
        }

        /// <summary>
        /// 新增 webhook：格式不對、驗證失敗、已經有同一條 ⇒ 擋下、零寫入。
        /// 成功 ⇒ URL 進密文檔、管理資料進 json（先寫密文，再寫 json —— json 裡有它才算「有」）。
        /// </summary>
        public static bool TryAddWebhook(string iDataRoot, string iUrl, string iLabel, out string oId, out string? oError)
        {
            string aUrl = (iUrl ?? "").Trim();
            if (!TryParseWebhookUrl(aUrl, out oId)) { oError = "這不是 Discord webhook 的網址（應該長得像 https://discord.com/api/webhooks/<數字>/<token>）"; return false; }
            SCP_DiscordConfig c = Load(iDataRoot);
            if (c.Error.Length > 0) { oError = "設定檔讀不了，⛔ 不覆寫：" + c.Error; return false; }
            string aId = oId;
            if (c.Webhooks.Any(w => w.Id == aId)) { oError = $"這條 webhook 已經在清單裡了（id {aId}）"; return false; }
            Dictionary<string, string> aUrls = LoadWebhookUrls(iDataRoot, out oError);
            if (oError != null) { oError += " ⇒ ⛔ 不覆寫"; return false; }

            var aInfo = new SCP_DiscordWebhookInfo { Id = aId, Label = (iLabel ?? "").Trim() };
            if (!TryVerify(iDataRoot, aUrl, aInfo, out oError)) return false;

            aUrls[aId] = aUrl;
            if (!SaveWebhookUrls(iDataRoot, aUrls, out oError)) return false;
            c.Webhooks.Add(aInfo);
            if (!Save(iDataRoot, c, out oError)) return false;
            if (!Load(iDataRoot).Webhooks.Any(w => w.Id == aId) || !LoadWebhookUrls(iDataRoot, out _).ContainsKey(aId))
            { oError = "寫完回讀對不上"; return false; }
            return true;
        }

        /// <summary>重新驗證（更新快取的名稱與狀態）。驗證失敗也會把狀態寫回（讓清單看得出它壞了）。</summary>
        public static bool TryReverify(string iDataRoot, string iId, out string? oError)
        {
            SCP_DiscordConfig c = Load(iDataRoot);
            SCP_DiscordWebhookInfo? w = c.Webhooks.FirstOrDefault(x => x.Id == iId);
            if (w == null) { oError = $"清單裡沒有 webhook {iId}"; return false; }
            Dictionary<string, string> aUrls = LoadWebhookUrls(iDataRoot, out oError);
            if (oError != null) return false;
            if (!aUrls.TryGetValue(iId, out string? aUrl)) { oError = $"密文檔裡沒有 webhook {iId} 的 URL"; return false; }
            bool aOk = TryVerify(iDataRoot, aUrl, w, out string? aVerifyErr);
            if (!Save(iDataRoot, c, out oError)) return false;
            oError = aVerifyErr;
            return aOk;
        }

        public static bool TrySetWebhookEnabled(string iDataRoot, string iId, bool iEnabled, out string? oError)
        {
            SCP_DiscordConfig c = Load(iDataRoot);
            SCP_DiscordWebhookInfo? w = c.Webhooks.FirstOrDefault(x => x.Id == iId);
            if (w == null) { oError = $"清單裡沒有 webhook {iId}"; return false; }
            w.Enabled = iEnabled;
            return Save(iDataRoot, c, out oError);
        }

        /// <summary>
        /// 送出時碰到 401／403／404 ⇒ **自動停用**這條 webhook 並把原因寫進驗證狀態（同 Unity 版「標成失效、等人處理」）。
        /// 🩸 不停用的話：一條已經被刪掉的 webhook 每 2 秒被重打一次、永遠不停（2026-09-28 scratch 實測）。
        /// </summary>
        public static void MarkDead(string iDataRoot, string iId, string iStatus)
        {
            SCP_DiscordConfig c = Load(iDataRoot);
            SCP_DiscordWebhookInfo? w = c.Webhooks.FirstOrDefault(x => x.Id == iId);
            if (w == null || c.Error.Length > 0) return;
            w.Enabled = false;
            w.VerifyStatus = iStatus + "（送出失敗 ⇒ 自動停用）";
            w.VerifiedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            Save(iDataRoot, c, out _);
        }

        /// <summary>刪除 webhook。還有分類綁著它 ⇒ 擋下並列出是哪些分類、零寫入。</summary>
        public static bool TryRemoveWebhook(string iDataRoot, string iId, out string? oError)
        {
            SCP_DiscordConfig c = Load(iDataRoot);
            if (c.Error.Length > 0) { oError = "設定檔讀不了，⛔ 不覆寫：" + c.Error; return false; }
            if (!c.Webhooks.Any(x => x.Id == iId)) { oError = $"清單裡沒有 webhook {iId}"; return false; }
            List<string> aUsers = c.CategoryWebhooks.Where(kv => kv.Value.Contains(iId)).Select(kv => kv.Key).ToList();
            if (aUsers.Count > 0) { oError = $"還有分類綁著它（{string.Join("、", aUsers)}）⇒ 先在分類那邊取消勾選"; return false; }
            Dictionary<string, string> aUrls = LoadWebhookUrls(iDataRoot, out oError);
            if (oError != null) { oError += " ⇒ ⛔ 不覆寫"; return false; }
            c.Webhooks.RemoveAll(x => x.Id == iId);
            if (!Save(iDataRoot, c, out oError)) return false;
            aUrls.Remove(iId);
            return SaveWebhookUrls(iDataRoot, aUrls, out oError);
        }

        /// <summary>設定一個頻道分類要同步到哪些 webhook（整份取代）。分類不在清單、webhook 不在清單 ⇒ 擋下、零寫入。</summary>
        public static bool TrySetCategoryWebhooks(string iDataRoot, string iCategory, IReadOnlyList<string> iIds, out string? oError)
        {
            oError = null;
            SCP_ChannelCategory? aCat = SCP_TavernChannels.LoadCategories(iDataRoot)
                .FirstOrDefault(x => string.Equals(x.Name, (iCategory ?? "").Trim(), StringComparison.OrdinalIgnoreCase));
            if (aCat == null) { oError = $"沒有這個頻道分類：'{iCategory}'"; return false; }
            SCP_DiscordConfig c = Load(iDataRoot);
            List<string> aIds = iIds.Select(x => x.Trim()).Where(x => x.Length > 0).Distinct(StringComparer.Ordinal).ToList();
            string? aBad = aIds.FirstOrDefault(id => !c.Webhooks.Any(w => w.Id == id));
            if (aBad != null) { oError = $"清單裡沒有 webhook {aBad}"; return false; }
            if (aIds.Count == 0) c.CategoryWebhooks.Remove(aCat.Name); else c.CategoryWebhooks[aCat.Name] = aIds;
            if (!Save(iDataRoot, c, out oError)) return false;
            List<string> aBack = Load(iDataRoot).CategoryWebhooks.TryGetValue(aCat.Name, out List<string>? b) ? b : new List<string>();
            if (!aBack.SequenceEqual(aIds)) { oError = "寫完回讀對不上"; return false; }
            return true;
        }

        /// <summary>
        /// 從 `PromptQueue/notify_config.json` 匯入 **main** 那組 webhook（`tavern_mirror.webhook_urls`），綁到 Main 分類。
        /// Tim 2026-09-28：只匯 Main；舊檔明文不動。已在清單的跳過；驗證失敗的不收（逐條回報）。
        /// </summary>
        public static List<string> ImportMainFromNotifyConfig(string iDataRoot, out bool oAnyAdded)
        {
            oAnyAdded = false;
            var aReport = new List<string>();
            string aPath = SCP_DiscordInboundConfig.NotifyConfigPath(iDataRoot);
            if (!File.Exists(aPath)) { aReport.Add("找不到 notify_config.json ⇒ 沒有可匯入的"); return aReport; }
            List<string> aUrls = new List<string>();
            try
            {
                SCP_JsonData n = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                foreach (SCP_JsonData u in n["tavern_mirror"]["webhook_urls"]) if (u.IsString) aUrls.Add(u.AsString().Trim());
            }
            catch (Exception e) { aReport.Add("notify_config.json 讀不了：" + e.Message); return aReport; }
            if (aUrls.Count == 0) { aReport.Add("notify_config.json 的 tavern_mirror.webhook_urls 是空的"); return aReport; }

            SCP_TavernChannels.EnsureMainCategory(iDataRoot, out _);
            var aMainIds = new List<string>(Load(iDataRoot).CategoryWebhooks.TryGetValue(SCP_TavernChannels.MainCategory, out List<string>? m) ? m : new List<string>());
            int i = 0;
            foreach (string aUrl in aUrls)
            {
                i++;
                if (!TryParseWebhookUrl(aUrl, out string aId)) { aReport.Add($"第 {i} 條：不是 webhook 網址的樣子 ⇒ 跳過"); continue; }
                if (Load(iDataRoot).Webhooks.Any(w => w.Id == aId)) { aReport.Add($"第 {i} 條（id {aId}）：已經在清單裡 ⇒ 跳過"); }
                else if (TryAddWebhook(iDataRoot, aUrl, "", out _, out string? aErr)) { aReport.Add($"第 {i} 條（id {aId}）：已匯入"); oAnyAdded = true; }
                else { aReport.Add($"第 {i} 條（id {aId}）：{aErr} ⇒ 沒有匯入"); continue; }
                if (!aMainIds.Contains(aId)) aMainIds.Add(aId);
            }
            if (!TrySetCategoryWebhooks(iDataRoot, SCP_TavernChannels.MainCategory, aMainIds, out string? aBindErr))
                aReport.Add("綁到 Main 失敗：" + aBindErr);
            else aReport.Add($"Main 現在同步到 {aMainIds.Count} 條 webhook");
            return aReport;
        }

        static void WriteReplace(string iPath, byte[] iBytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iPath) ?? ".");
            string aTmp = iPath + ".tmp";
            File.WriteAllBytes(aTmp, iBytes);
            SCP.Core.Io.SCP_TextFile.ReplaceOrMove(aTmp, iPath);
        }
    }
}
