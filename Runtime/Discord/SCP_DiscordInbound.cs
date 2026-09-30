// 區塊職責：**Discord → 酒館（Inbound）的一輪輪詢**（TASK-0316 ④）—— 抓新訊息、過濾、轉成酒館訊息、附件落地、游標。
// 物理意義：
//   · REST 輪詢：`GET /channels/{id}/messages?after=<游標>&limit=50`（Discord 由新到舊回，這裡反轉成舊到新）。
//     沒有游標 ⇒ **baseline**：只記最新一則的 id、⛔ 不回放歷史（同 Unity 版）。Gateway 即時推送是之後的事。
//   · 欄位語意對齊 Unity 版（下游零改動）：sender_id=`discord:<uid>`、sender_name＝顯示名、kind=chat、
//     meta.source=`discord`（⇒ `SCP_TavernMentions.IsExternalRelay` 認得、Outbound ⛔ 不回送）、discord_msg_id／channel_id／guild_id、
//     source_class／channel_label、`relay=senate`（區分 Unity 的 `native`）、`discord_whitelisted`（true／false）。
//   · 過濾：bot 發的、webhook 發的（⛔ 否則 Outbound 送出去的會被收回來，無限迴圈）、空內容沒附件的 —— 逐筆記原因。
//   · 白名單**不擋人，只標記**（Tim 2026-09-30）：白名單外的照收，顯示名後綴「（白名單外）」＋ meta `discord_whitelisted=false`。
//     ⇒ 標在顯示名上是刻意的：catchup／query／酒館頁／inbox 都印顯示名，**一個點就全部看得到**（⛔ 不在四個顯示端各抄一份判準）；
//        也讓「暱稱取成 Tim 的非白名單使用者」一眼可辨。
//   · 顯示名：白名單填的 display_name ＞ 伺服器暱稱 ＞ global_name ＞ username ＞ uid（白名單外再加後綴）。
//   · 附件（TASK-0323）：**下載落地**到 `ChatTavern/media/discord/<日期>/`（`SCP_DiscordMedia`），訊息 `refs` 帶 repo 相對路徑
//     ⇒ agent 讀完訊息可以直接開圖。本文末尾照舊列一行 `[Discord 附件 N 個] …`（沒落地的那幾個標明原因：過大／下載失敗）；
//     meta 記 `attachments`（總數）與 `attachments_saved`（落地數）。⛔ 附件失敗不擋文字（fail-soft）。偷看模式不下載。
//   · 游標：`ChatTavern/discord/discord_inbound_state.json`。第一次接手時讀 Unity 的 `PromptQueue/_tavern_state.json`
//     —— 那份 **24 小時內更新過才沿用**（接得上 Unity 停掉後的空窗）；更舊的 ⇒ baseline（🩸 Bar 有一條停在 08-01，沿用會灌兩個月）。
// 數值影響：本檔**不寫酒館** —— 回傳要寫的訊息，由宿主交給 `tavern-write`（單一寫入端；@ 通知／詞典／封存閘都在那裡）。
//           游標由宿主在**寫成功之後**才推（`CommitCursor`）⇒ 寫入失敗的那則下一輪重抓。⛔ token 不出現在任何輸出。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。會連網 ⇒ 只給 Senate 宿主呼叫。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Market;
using SCP.Core.Tavern;

namespace SCP.Core.Discord
{
    /// <summary>一則要寫進酒館的訊息（Discord 那則的 id 用來推游標）。</summary>
    public sealed class SCP_DiscordInboundItem
    {
        public string DiscordMsgId = "";
        public string Room = "";
        public SCP_JsonData MsgJson = SCP_JsonData.NewObject();
        public string Preview = "";
    }

    public sealed class SCP_DiscordPollResult
    {
        public string ChannelId = "";
        public string Label = "";
        public bool Ok;
        public string Error = "";
        public bool Baseline;
        public string NewestId = "";
        public List<SCP_DiscordInboundItem> Items = new List<SCP_DiscordInboundItem>();
        public List<string> Skipped = new List<string>();
        public string CursorNote = "";
    }

    public static class SCP_DiscordInbound
    {
        public const string StateFileName = "discord_inbound_state.json";
        public const int FetchLimit = 50;
        /// <summary>白名單外的發言者，顯示名後面接這一段（見檔頭「白名單不擋人，只標記」）。</summary>
        public const string NotWhitelistedSuffix = "（白名單外）";
        static readonly TimeSpan UnityCursorMaxAge = TimeSpan.FromHours(24);

        static string StatePath(string iDataRoot) => SCP_DiscordPaths.Dir(iDataRoot) + "/" + StateFileName;
        static readonly object s_StateLock = new object();

        // ── 游標 ─────────────────────────────────────────────────────

        static SCP_JsonData LoadState(string iDataRoot)
        {
            try { if (File.Exists(StatePath(iDataRoot))) return SCP_JsonParser.Parse(File.ReadAllText(StatePath(iDataRoot), Encoding.UTF8)); }
            catch (Exception) { }
            var j = SCP_JsonData.NewObject();
            j.Set("channels", SCP_JsonData.NewObject());
            return j;
        }

        static void SaveState(string iDataRoot, SCP_JsonData iState)
        {
            string p = StatePath(iDataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(p) ?? ".");
            File.WriteAllText(p + ".tmp", iState.ToJson(true), new UTF8Encoding(false));
            SCP.Core.Io.SCP_TextFile.ReplaceOrMove(p + ".tmp", p);
        }

        /// <summary>這個頻道的游標；沒有 ⇒ 試著接手 Unity 的（24 小時內才算）；都沒有 ⇒ 空（baseline）。</summary>
        public static string GetCursor(string iDataRoot, string iChannelId, out string oNote)
        {
            oNote = "";
            lock (s_StateLock)
            {
                SCP_JsonData s = LoadState(iDataRoot);
                string aHave = s["channels"][iChannelId].GetString("last_message_id", "");
                if (aHave.Length > 0) return aHave;
                // 第一次：看 Unity 留下的
                try
                {
                    string aUnity = Path.Combine(iDataRoot, "PromptQueue", "_tavern_state.json");
                    if (File.Exists(aUnity))
                    {
                        SCP_JsonData u = SCP_JsonParser.Parse(File.ReadAllText(aUnity, Encoding.UTF8))["inbound"]["channels"][iChannelId];
                        string aId = u.GetString("last_message_id", ""), aAt = u.GetString("updated_at", "");
                        if (aId.Length > 0 && DateTime.TryParse(aAt, CultureInfo.InvariantCulture,
                                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime aUtc))
                        {
                            if (DateTime.UtcNow - aUtc <= UnityCursorMaxAge) { oNote = $"接手 Unity 的游標（{aAt}）"; return aId; }
                            oNote = $"Unity 的游標太舊（{aAt}）⇒ 從現在開始、不補歷史";
                        }
                    }
                }
                catch (Exception) { /* 讀不到 ⇒ baseline */ }
                return "";
            }
        }

        /// <summary>寫成功之後推游標（只會往前）。</summary>
        public static void CommitCursor(string iDataRoot, string iChannelId, string iMsgId, int iRelayed, string iNote)
        {
            if (string.IsNullOrEmpty(iMsgId)) return;
            lock (s_StateLock)
            {
                SCP_JsonData s = LoadState(iDataRoot);
                if (!s["channels"].IsObject) s.Set("channels", SCP_JsonData.NewObject());
                SCP_JsonData c = s["channels"][iChannelId].IsObject ? s["channels"][iChannelId] : SCP_JsonData.NewObject();
                string aOld = c.GetString("last_message_id", "");
                if (aOld.Length == 0 || CompareSnowflake(iMsgId, aOld) > 0) c.Set("last_message_id", iMsgId);
                c.Set("updated_at", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
                c.Set("relayed_total", c.GetLong("relayed_total", 0) + iRelayed);
                if (iNote.Length > 0) c.Set("note", iNote);
                s["channels"].Set(iChannelId, c);
                SaveState(iDataRoot, s);
            }
        }

        /// <summary>記最後一次輪詢的狀態（後台顯示用）：時間、錯誤。</summary>
        public static void NoteStatus(string iDataRoot, string iChannelId, string iError)
        {
            lock (s_StateLock)
            {
                SCP_JsonData s = LoadState(iDataRoot);
                if (!s["channels"].IsObject) s.Set("channels", SCP_JsonData.NewObject());
                SCP_JsonData c = s["channels"][iChannelId].IsObject ? s["channels"][iChannelId] : SCP_JsonData.NewObject();
                c.Set("last_poll_at", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                c.Set("last_error", iError ?? "");
                s["channels"].Set(iChannelId, c);
                SaveState(iDataRoot, s);
            }
        }

        public static SCP_JsonData ReadState(string iDataRoot) { lock (s_StateLock) return LoadState(iDataRoot); }

        static int CompareSnowflake(string a, string b)
        {
            if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
            return string.CompareOrdinal(a, b);
        }

        // ── 一輪輪詢 ─────────────────────────────────────────────────

        /// <summary>
        /// 輪詢一個頻道一次：抓 → 過濾 → 轉換。⛔ 不寫酒館、⛔ 不推游標（baseline 除外；<paramref name="iPeek"/>=true 時連它也不落）。
        /// </summary>
        public static SCP_DiscordPollResult PollOnce(string iDataRoot, string iRepoRoot, SCP_DiscordRoute iRoute, SCP_DiscordWhitelist iWhitelist,
                                                     bool iPeek = false)
        {
            var r = new SCP_DiscordPollResult { ChannelId = iRoute.ChannelId, Label = iRoute.Label };
            string aCursor = GetCursor(iDataRoot, iRoute.ChannelId, out r.CursorNote);
            r.Baseline = aCursor.Length == 0;
            string aPath = r.Baseline
                ? $"/channels/{iRoute.ChannelId}/messages?limit=1"
                : $"/channels/{iRoute.ChannelId}/messages?after={aCursor}&limit={FetchLimit}";
            if (!SCP_DiscordBot.TryGetJson(iDataRoot, aPath, out SCP_JsonData aArr, out _, out string? aErr))
            { r.Error = aErr ?? "抓不到"; return r; }
            if (!aArr.IsArray) { r.Error = "回應不是陣列"; return r; }
            r.Ok = true;
            if (aArr.Count == 0) return r;
            r.NewestId = aArr[0].GetString("id", "");
            if (r.Baseline)
            {
                if (iPeek) return r;   // 偷看模式：⛔ 連 baseline 都不落
                CommitCursor(iDataRoot, iRoute.ChannelId, r.NewestId, 0, r.CursorNote.Length > 0 ? r.CursorNote : "baseline（歷史不回放）");
                return r;
            }
            for (int i = aArr.Count - 1; i >= 0; i--)   // 舊 → 新
            {
                SCP_JsonData m = aArr[i];
                string aWhy = Convert(iDataRoot, iRepoRoot, iRoute, iWhitelist, m, out SCP_DiscordInboundItem? aItem, !iPeek);
                if (aItem != null) r.Items.Add(aItem);
                else r.Skipped.Add(m.GetString("id", "") + ":" + aWhy);
            }
            return r;
        }

        /// <summary>一則 Discord 訊息 ⇒ 酒館訊息；不收的回原因（<paramref name="oItem"/>＝null）。</summary>
        public static string Convert(string iDataRoot, string iRepoRoot, SCP_DiscordRoute iRoute, SCP_DiscordWhitelist iWhitelist,
                                     SCP_JsonData iMsg, out SCP_DiscordInboundItem? oItem, bool iDownload = true)
        {
            oItem = null;
            if (!iMsg.IsObject) return "malformed";
            SCP_JsonData aAuthor = iMsg["author"];
            if (!aAuthor.IsObject) return "no-author";
            if (aAuthor.GetBool("bot", false)) return "bot-author";
            if (iMsg.GetString("webhook_id", "").Length > 0) return "webhook-msg";   // ⛔ 我們自己 Outbound 送出去的
            string aUid = aAuthor.GetString("id", "");
            if (aUid.Length == 0) return "no-author-id";
            SCP_DiscordWhitelistUser? aWl = iWhitelist.Users.FirstOrDefault(u => u.UserId == aUid);

            string aMsgId = iMsg.GetString("id", "");
            string aContent = iMsg.GetString("content", "").Trim();
            int aAttCount = iMsg["attachments"].IsArray ? iMsg["attachments"].Count : 0;
            if (aContent.Length == 0 && aAttCount == 0)
                return iMsg.Contains("content") ? "empty-content(檢查 MESSAGE_CONTENT intent)" : "no-content-field";
            // ⚠ 過濾全部通過之後才下載 ⇒ 被略過的訊息（bot／webhook／空內容）⛔ 不會落任何檔
            List<SCP_DiscordMedia.InboundAttachment> aAtts = aAttCount > 0
                ? SCP_DiscordMedia.DownloadAttachments(iDataRoot, iRepoRoot, aMsgId, iMsg["attachments"], iDownload)
                : new List<SCP_DiscordMedia.InboundAttachment>();
            if (aAtts.Count > 0)
            {
                string aLine = SCP_DiscordMedia.DescribeLine(aAtts);
                aContent = aContent.Length == 0 ? aLine : aContent + "\n" + aLine;
            }
            int aSaved = aAtts.Count(a => a.Ref != null);

            string aDisplay = iMsg["member"].GetString("nick", "");
            if (aDisplay.Length == 0) aDisplay = aAuthor.GetString("global_name", "");
            if (aDisplay.Length == 0) aDisplay = aAuthor.GetString("username", "");
            if (aDisplay.Length == 0) aDisplay = aUid;
            if (aWl != null && aWl.DisplayName.Length > 0) aDisplay = aWl.DisplayName;
            if (aWl == null) aDisplay += NotWhitelistedSuffix;

            var aMeta = SCP_JsonData.NewObject();
            aMeta.Set("source", "discord");
            aMeta.Set("discord_msg_id", aMsgId);
            aMeta.Set("discord_channel_id", iRoute.ChannelId);
            if (iRoute.GuildId.Length > 0) aMeta.Set("discord_guild_id", iRoute.GuildId);
            if (iRoute.Label.Length > 0) aMeta.Set("channel_label", iRoute.Label);
            aMeta.Set("source_class", iRoute.SourceClass);
            aMeta.Set("discord_whitelisted", aWl != null ? "true" : "false");
            aMeta.Set("relay", "senate");
            if (aWl != null && aWl.Profile.Length > 0) aMeta.Set("discord_user_profile", aWl.Profile);
            if (aAtts.Count > 0)
            {
                aMeta.Set("attachments", aAtts.Count.ToString(CultureInfo.InvariantCulture));
                aMeta.Set("attachments_saved", aSaved.ToString(CultureInfo.InvariantCulture));
            }
            if (SCP_TavernCli.LooksLikeCliCommand(iDataRoot, aContent)) { aMeta.Set("tag", "cli-cmd"); aMeta.Set("cli_cmd", "true"); }

            var j = SCP_JsonData.NewObject();
            j.Set("sender_id", "discord:" + aUid);
            j.Set("sender_name", aDisplay);
            j.Set("kind", "chat");
            j.Set("body", aContent);
            j.Set("meta", aMeta);
            if (aSaved > 0)
            {
                var aRefs = SCP_JsonData.NewArray();
                foreach (SCP_DiscordMedia.InboundAttachment a in aAtts)
                {
                    if (a.Ref == null) continue;
                    var r = SCP_JsonData.NewObject();
                    r.Set("path", a.Ref.Path);
                    if (a.Ref.Label.Length > 0) r.Set("label", a.Ref.Label);
                    aRefs.Add(r);
                }
                j.Set("refs", aRefs);
            }
            oItem = new SCP_DiscordInboundItem
            {
                DiscordMsgId = aMsgId, Room = iRoute.TavernRoom, MsgJson = j,
                Preview = aDisplay + "：" + (aContent.Length > 40 ? aContent.Substring(0, 40) + "…" : aContent),
            };
            return "";
        }
    }
}
