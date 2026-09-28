// 區塊職責：**Discord Bot 的憑證與 API**（TASK-0319）—— token 一步加密＋安裝、狀態、測試連線、Server／頻道清單快取。
// 物理意義：
//   · token 住 secrets 資料夾（`SCP_SecretStore.ResolveDir`，現況 `AgentCommands/Secret/`）：
//     `discord_bot_token.enc`（密文，入 git）＋ `discord_bot_token.txt`（明文，**本機自己的**，Secret repo 的 .gitignore 擋掉）。
//     Tim 2026-09-28：「本機設定的話直接輸出一份解密後檔案（不用跑兩遍）」⇒ `TrySetToken` 一次寫兩份。
//   · 讀 token 的順序：環境變數 `DISCORD_INBOUND_BOT_TOKEN` ＞ 明文檔（同舊 Unity daemon，⇒ 同一台機器行為不變）。
//   · API 呼叫走宿主注入的 `ISCP_HttpHeaderFetcher`（SCP_Core 不引入網路，見 `SCP_RateSource.cs` 守衛①）。
//   · Server／頻道清單存 `ChatTavern/discord_guilds_cache.json`：**按了重新整理才打 API**，頁面平常只讀快取。
// 數值影響：
//   · ⛔ token **不離開本檔**：沒有任何公開成員回傳它；錯誤訊息與快取檔都不含它。
//   · 寫 token：先加密、**自己回解驗證**、再寫 .enc 與 .txt（暫存檔再換檔）；驗證不過 ⇒ 一個 byte 都不寫。
//   · 已有 .enc 時要 `iOverwrite=true` —— 覆寫之後舊密碼解不開新檔，舊檔回不來（除非在 git 裡）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Market;
using SCP.Core.Secret;
using SCP.Core.Tavern;

namespace SCP.Core.Discord
{
    /// <summary>憑證狀態（⛔ 不含 token 本身）。</summary>
    public sealed class SCP_DiscordTokenStatus
    {
        public string SecretsDir = "";
        public string DirWarning = "";
        public string EncPath = "";
        public string PlainPath = "";
        public bool EncExists;
        public bool PlainExists;
        /// <summary>環境變數有值 ⇒ 它蓋過明文檔（讀 token 時先看它）。</summary>
        public bool EnvOverride;
        public string EncCreatedAt = "";
        public string EncHint = "";
        /// <summary>現在讀得到一份 token 嗎（環境變數或明文）。</summary>
        public bool Ready => EnvOverride || PlainExists;
    }

    public sealed class SCP_DiscordChannel
    {
        public string Id = "";
        public string Name = "";
        /// <summary>0＝文字、5＝公告、4＝分類（其餘型別不收）。</summary>
        public int Type;
        /// <summary>所屬的 Discord 分類名（沒有 ⇒ 空）。</summary>
        public string ParentName = "";
        public int Position;
    }

    public sealed class SCP_DiscordGuild
    {
        public string Id = "";
        public string Name = "";
        public List<SCP_DiscordChannel> Channels = new List<SCP_DiscordChannel>();
        /// <summary>列頻道失敗的原因（例如 403：Bot 在這個 Server 沒有 View Channel）；空 ＝ 成功。</summary>
        public string Error = "";
    }

    public sealed class SCP_DiscordGuildCache
    {
        public string FetchedAt = "";
        public string BotId = "";
        public string BotName = "";
        public List<SCP_DiscordGuild> Guilds = new List<SCP_DiscordGuild>();
        public bool Exists;
    }

    public static class SCP_DiscordBot
    {
        public const string TokenSecretName = "discord_bot_token";
        public const string TokenEnvVar = "DISCORD_INBOUND_BOT_TOKEN";
        public const string CacheFileName = "discord_guilds_cache.json";
        const string ApiBase = "https://discord.com/api/v10";
        const int TimeoutSec = 15;

        // ── 憑證 ─────────────────────────────────────────────────────

        public static SCP_DiscordTokenStatus Status(string iDataRoot)
        {
            var s = new SCP_DiscordTokenStatus();
            s.SecretsDir = SCP_SecretStore.ResolveDir(iDataRoot, out string? aWarn);
            s.DirWarning = aWarn ?? "";
            s.EncPath = s.SecretsDir + "/" + TokenSecretName + ".enc";
            s.PlainPath = SCP_SecretStore.PlainPathOf(s.EncPath);
            s.EncExists = File.Exists(s.EncPath);
            s.PlainExists = File.Exists(s.PlainPath) && ReadPlain(s.PlainPath).Length > 0;
            s.EnvOverride = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TokenEnvVar));
            if (s.EncExists)
            {
                try
                {
                    SCP_SecretMeta m = SCP_SecretCrypto.ReadMetadata(File.ReadAllBytes(s.EncPath));
                    s.EncCreatedAt = m.CreatedAt;
                    s.EncHint = m.Hint;
                }
                catch (Exception) { /* 壞檔：狀態頁照樣印「.enc 有」，解不開會在寫入／解密時出聲 */ }
            }
            return s;
        }

        /// <summary>
        /// 一步寫好：把 <paramref name="iToken"/> 用 <paramref name="iPassphrase"/> 加密成 `.enc`，**同時**寫出明文 `.txt`。
        /// ⛔ token／密碼空、token 含空白 ⇒ 擋下、零寫入；已有 .enc 而 <paramref name="iOverwrite"/>=false ⇒ 擋下、零寫入。
        /// </summary>
        public static bool TrySetToken(string iDataRoot, string iToken, string iPassphrase, string iHint, bool iOverwrite, out string? oError)
        {
            oError = null;
            string aToken = (iToken ?? "").Trim();
            if (aToken.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase)) aToken = aToken.Substring(4).Trim();
            if (aToken.Length == 0) { oError = "token 是空的"; return false; }
            if (aToken.Any(char.IsWhiteSpace)) { oError = "token 中間有空白 —— 多半是貼錯了（⛔ 不猜哪一段才是）"; return false; }
            if (string.IsNullOrEmpty(iPassphrase)) { oError = "沒有輸入密碼"; return false; }

            SCP_DiscordTokenStatus s = Status(iDataRoot);
            if (!Directory.Exists(s.SecretsDir)) { oError = $"secrets 資料夾不存在：{s.SecretsDir}"; return false; }
            if (s.EncExists && !iOverwrite) { oError = $"`{Path.GetFileName(s.EncPath)}` 已存在 ⇒ 要覆寫請確認（覆寫之後舊密碼解不開新檔）"; return false; }

            byte[] aPlain = Encoding.UTF8.GetBytes(aToken);
            byte[] aEnc;
            try
            {
                aEnc = SCP_SecretCrypto.Encrypt(aPlain, iPassphrase, iHint ?? "", "discord bot token");
                byte[] aBack = SCP_SecretCrypto.Decrypt(aEnc, iPassphrase);   // 寫之前自己解一次
                if (!aBack.SequenceEqual(aPlain)) { oError = "加密後回解對不上 —— 沒有寫任何東西"; return false; }
            }
            catch (Exception e) { oError = $"加密失敗：{e.GetType().Name} —— 沒有寫任何東西"; return false; }

            try
            {
                WriteBytesReplace(s.EncPath, aEnc);
                WriteBytesReplace(s.PlainPath, aPlain);
            }
            catch (Exception e) { oError = $"寫不進去：{e.Message}"; return false; }

            if (ReadPlain(s.PlainPath) != aToken) { oError = "寫完回讀明文對不上"; return false; }
            return true;
        }

        /// <summary>讀 token：環境變數 ＞ 明文檔。⛔ 只給本檔的 API 呼叫用（不公開）。</summary>
        static string ResolveToken(string iDataRoot)
        {
            string? aEnv = Environment.GetEnvironmentVariable(TokenEnvVar);
            if (!string.IsNullOrWhiteSpace(aEnv)) return aEnv!.Trim();
            SCP_DiscordTokenStatus s = Status(iDataRoot);
            return File.Exists(s.PlainPath) ? ReadPlain(s.PlainPath) : "";
        }

        static string ReadPlain(string iPath)
        {
            try { return File.ReadAllText(iPath, Encoding.UTF8).Trim().TrimStart('﻿'); }
            catch (Exception) { return ""; }
        }

        // ── API ──────────────────────────────────────────────────────

        /// <summary>
        /// 打一支 Discord API。失敗時 <paramref name="oError"/> 帶狀態碼與「該怎麼辦」（401 ⇒ token 錯、403 ⇒ 權限）。
        /// ⛔ token 不會出現在任何錯誤訊息裡。
        /// </summary>
        static bool TryApi(string iDataRoot, string iPath, out SCP_JsonData oJson, out int oStatus, out string? oError)
        {
            oJson = SCP_JsonData.NewNull();
            oStatus = 0;
            if (!(SCP_HttpFetch.Current is ISCP_HttpHeaderFetcher aFetch))
            {
                oError = SCP_HttpFetch.Current == null
                    ? "本宿主沒有註冊抓取器（Unity 那側刻意不註冊）⇒ 這支要在 Senate 跑"
                    : $"本宿主的抓取器（{SCP_HttpFetch.Current.FetcherName}）不能帶標頭 ⇒ 打不了 Discord API";
                return false;
            }
            string aToken = ResolveToken(iDataRoot);
            if (aToken.Length == 0) { oError = "還沒有 Bot token（環境變數與明文檔都沒有）⇒ 先在「Discord Bot」頁設定"; return false; }

            var aHeaders = new Dictionary<string, string>
            {
                ["Authorization"] = "Bot " + aToken,
                ["User-Agent"] = "DiscordBot (senate, 1.0)",
            };
            if (!aFetch.TryGetText(ApiBase + iPath, aHeaders, TimeoutSec, out string aBody, out oStatus, out string? aErr))
            {
                string aWhat = oStatus == 401 ? " ⇒ token 不對（或已被重設）"
                             : oStatus == 403 ? " ⇒ Bot 沒有這項權限（例如 View Channel）"
                             : oStatus == 429 ? " ⇒ 被限流，稍等再按"
                             : "";
                oError = $"GET {iPath} 失敗：{Scrub(aErr ?? "", aToken)}{aWhat}";
                return false;
            }
            try { oJson = SCP_JsonParser.Parse(aBody); }
            catch (Exception e) { oError = $"GET {iPath} 回應解析不了：{e.Message}"; return false; }
            oError = null;
            return true;
        }

        /// <summary>保險：萬一宿主把回應原文帶進錯誤訊息，也把 token 塗掉。</summary>
        static string Scrub(string iText, string iToken)
            => iToken.Length > 0 ? iText.Replace(iToken, "***") : iText;

        /// <summary>測試連線：`GET /users/@me` ⇒ Bot 的名字與 id。</summary>
        public static bool TryTestConnection(string iDataRoot, out string oBotName, out string oBotId, out int oStatus, out string? oError)
        {
            oBotName = ""; oBotId = "";
            if (!TryApi(iDataRoot, "/users/@me", out SCP_JsonData aMe, out oStatus, out oError)) return false;
            oBotId = aMe.GetString("id", "");
            oBotName = aMe.GetString("username", "");
            return true;
        }

        /// <summary>
        /// 重新整理 Server／頻道清單：`/users/@me` ＋ `/users/@me/guilds` ＋ 每個 Server 的 `/guilds/{id}/channels`，寫進快取檔。
        /// 單一 Server 列頻道失敗（403 之類）⇒ 記在那個 Server 上、其餘照做；Bot 身分或 Server 清單拿不到 ⇒ 整次失敗、快取不動。
        /// </summary>
        public static bool TryRefreshGuilds(string iDataRoot, out SCP_DiscordGuildCache oCache, out string? oError)
        {
            oCache = new SCP_DiscordGuildCache();
            if (!TryTestConnection(iDataRoot, out oCache.BotName, out oCache.BotId, out _, out oError)) return false;
            if (!TryApi(iDataRoot, "/users/@me/guilds", out SCP_JsonData aGuilds, out _, out oError)) return false;
            if (!aGuilds.IsArray) { oError = "Server 清單不是陣列"; return false; }

            foreach (SCP_JsonData g in aGuilds)
            {
                var aGuild = new SCP_DiscordGuild { Id = g.GetString("id", ""), Name = g.GetString("name", "") };
                if (aGuild.Id.Length == 0) continue;
                if (TryApi(iDataRoot, "/guilds/" + aGuild.Id + "/channels", out SCP_JsonData aChs, out _, out string? aChErr) && aChs.IsArray)
                {
                    var aCatNames = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (SCP_JsonData c in aChs)
                        if (c.GetInt("type", -1) == 4) aCatNames[c.GetString("id", "")] = c.GetString("name", "");
                    foreach (SCP_JsonData c in aChs)
                    {
                        int aType = c.GetInt("type", -1);
                        if (aType != 0 && aType != 5) continue;   // 只收文字與公告頻道（語音、論壇、分類本身不收）
                        string aParent = c.GetString("parent_id", "");
                        aGuild.Channels.Add(new SCP_DiscordChannel
                        {
                            Id = c.GetString("id", ""),
                            Name = c.GetString("name", ""),
                            Type = aType,
                            ParentName = aCatNames.TryGetValue(aParent, out string? pn) ? pn : "",
                            Position = c.GetInt("position", 0),
                        });
                    }
                    aGuild.Channels = aGuild.Channels.OrderBy(c => c.ParentName, StringComparer.Ordinal).ThenBy(c => c.Position).ToList();
                }
                else aGuild.Error = aChErr ?? "頻道清單不是陣列";
                oCache.Guilds.Add(aGuild);
            }
            oCache.FetchedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            oCache.Exists = true;
            if (!WriteCache(iDataRoot, oCache, out oError)) return false;
            return true;
        }

        public static string CachePath(string iDataRoot)
            => (Path.GetDirectoryName(SCP_TavernMsgIndex.RoomsRoot(iDataRoot)) ?? iDataRoot).Replace('\\', '/') + "/" + CacheFileName;

        public static SCP_DiscordGuildCache LoadCache(string iDataRoot)
        {
            var c = new SCP_DiscordGuildCache();
            string aPath = CachePath(iDataRoot);
            if (!File.Exists(aPath)) return c;
            try
            {
                SCP_JsonData j = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                c.Exists = true;
                c.FetchedAt = j.GetString("fetched_at", "");
                c.BotId = j.GetString("bot_id", "");
                c.BotName = j.GetString("bot_name", "");
                if (j["guilds"].IsArray)
                    foreach (SCP_JsonData g in j["guilds"])
                    {
                        var aG = new SCP_DiscordGuild { Id = g.GetString("id", ""), Name = g.GetString("name", ""), Error = g.GetString("error", "") };
                        if (g["channels"].IsArray)
                            foreach (SCP_JsonData ch in g["channels"])
                                aG.Channels.Add(new SCP_DiscordChannel
                                {
                                    Id = ch.GetString("id", ""), Name = ch.GetString("name", ""), Type = ch.GetInt("type", 0),
                                    ParentName = ch.GetString("parent", ""), Position = ch.GetInt("position", 0),
                                });
                        c.Guilds.Add(aG);
                    }
            }
            catch (Exception) { c.Exists = false; }
            return c;
        }

        static bool WriteCache(string iDataRoot, SCP_DiscordGuildCache iCache, out string? oError)
        {
            oError = null;
            var j = SCP_JsonData.NewObject();
            j.Set("schema_version", 1);
            j.Set("note", "Discord Bot 看得到的 Server／頻道（TASK-0319）。機器產物：後台「Discord Bot」頁按重新整理時覆寫。⛔ 不含 token。");
            j.Set("fetched_at", iCache.FetchedAt);
            j.Set("bot_id", iCache.BotId);
            j.Set("bot_name", iCache.BotName);
            var aArr = SCP_JsonData.NewArray();
            foreach (SCP_DiscordGuild g in iCache.Guilds)
            {
                var o = SCP_JsonData.NewObject();
                o.Set("id", g.Id);
                o.Set("name", g.Name);
                if (g.Error.Length > 0) o.Set("error", g.Error);
                var aChs = SCP_JsonData.NewArray();
                foreach (SCP_DiscordChannel c in g.Channels)
                {
                    var co = SCP_JsonData.NewObject();
                    co.Set("id", c.Id); co.Set("name", c.Name); co.Set("type", c.Type);
                    co.Set("parent", c.ParentName); co.Set("position", c.Position);
                    aChs.Add(co);
                }
                o.Set("channels", aChs);
                aArr.Add(o);
            }
            j.Set("guilds", aArr);
            try { WriteBytesReplace(CachePath(iDataRoot), new UTF8Encoding(false).GetBytes(j.ToJson(true))); return true; }
            catch (Exception e) { oError = $"快取寫不進去：{e.Message}"; return false; }
        }

        static void WriteBytesReplace(string iPath, byte[] iBytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iPath) ?? ".");
            string aTmp = iPath + ".tmp";
            File.WriteAllBytes(aTmp, iBytes);
            SCP.Core.Io.SCP_TextFile.ReplaceOrMove(aTmp, iPath);
        }
    }
}
