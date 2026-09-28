// 區塊職責：**酒館訊息 → Discord webhook 的發送層**（TASK-0316 ③ 的地基；TASK-0320 先拿它補發舊訊息）。
// 物理意義：一則酒館訊息 ⇒ 一到數個 webhook payload（長文拆段）⇒ 依序 POST 到「這個頻道的分類」綁的每一條 webhook。
//           · 名字：`SCP_TavernDisplay` 的顯示名（`Agent@persona`，跟 Senate 酒館頁同源 —— TASK-0316 ⑨）。
//           · 頭像：`SCP_DiscordConfigStore.ResolveAvatarUrl`（persona 自填 ＞ 範本）。
//           · ⛔ 不 @ 任何人：`allowed_mentions.parse = []` —— 補發舊訊息不該把人叫起來，而 Discord 預設會解析 @everyone。
//           · ⛔ 不回送：從 Discord 轉進來的訊息（`SCP_TavernMentions.IsExternalRelay`）一律跳過。
//           · 只送 `kind=chat`（同 Unity 版預設）。
// 數值影響：
//   · 補發（`Backfill`）記游標：`ChatTavern/discord/discord_backfill_state.json` → `<room>.<webhook id> = 最後送成功的 seq`
//     ⇒ 中途斷掉重跑會從斷點接著送，⛔ 不重發。一則拆成多段時，**全部段都成功**才推游標。
//   · 節流：每次 POST 之間等 `PaceMs`（Discord 對單一頻道的 webhook 約每分鐘 30 次）；429 照 Retry-After 等完重試（最多 5 次）。
//   · ⛔ webhook URL 不出現在任何回傳值與錯誤訊息裡。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。本檔會 Thread.Sleep —— 只給 Senate 的 CLI／Server 呼叫。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SCP.Core.Json;
using SCP.Core.Market;
using SCP.Core.Tavern;

namespace SCP.Core.Discord
{
    public sealed class SCP_DiscordBackfillReport
    {
        public int Messages;       // 範圍內的訊息數
        public int Eligible;       // 其中要送的（chat、非外部中繼）
        public int Sent;           // 這一趟送成功的訊息數（每條 webhook 分開算）
        public int Posts;          // 實際 POST 次數（含拆段）
        public int SkippedAlready; // 游標說已經送過的
        public List<string> Problems = new List<string>();
        public List<string> Targets = new List<string>();
    }

    public static class SCP_DiscordOutbound
    {
        public const int MaxContent = 1900;   // Discord 上限 2000；留標頭與段號的空間
        public const int PaceMs = 2200;
        public const string BackfillStateFileName = "discord_backfill_state.json";

        /// <summary>Discord 不收的 username（含 "discord"／"clyde"、超過 80 字）⇒ 修成收得下的樣子。</summary>
        public static string SanitizeUsername(string iName)
        {
            string s = (iName ?? "").Trim();
            s = System.Text.RegularExpressions.Regex.Replace(s, "discord", "DC_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            s = System.Text.RegularExpressions.Regex.Replace(s, "clyde", "CL_", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (s.Length == 0) s = "tavern";
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }

        /// <summary>要送嗎：只送 chat、⛔ 不回送外部中繼。</summary>
        public static bool IsEligible(SCP_TavernMessage iMsg)
        {
            string aKind = string.IsNullOrEmpty(iMsg.Kind) ? "chat" : iMsg.Kind;
            if (aKind != "chat") return false;
            return !SCP_TavernMentions.IsExternalRelay(iMsg.Meta, iMsg.SenderId);
        }

        /// <summary>一則訊息 ⇒ 依序要送的 payload（JSON 字串）。長文照換行切，切不開才硬切。</summary>
        public static List<string> BuildPayloads(string iLettersRoot, string iAvatarTemplate, string iRoom, SCP_TavernMessage iMsg)
        {
            SCP_TavernDisplayRow aRow = SCP_TavernDisplay.Resolve(iLettersRoot, iMsg);
            string aName = SanitizeUsername(aRow.Name);
            string aAvatar = aRow.Persona.Length > 0 ? SCP_DiscordConfigStore.ResolveAvatarUrl(iLettersRoot, aRow.Persona, iAvatarTemplate, out _) : "";
            string aHeader = $"**`{iRoom}`** · seq {iMsg.Seq} · {aRow.TimeLocal}";
            List<string> aParts = Split(iMsg.Body ?? "", MaxContent - aHeader.Length - 16);
            var aOut = new List<string>();
            for (int i = 0; i < aParts.Count; i++)
            {
                string aContent = (i == 0 ? aHeader + "\n" : "") + aParts[i] + (aParts.Count > 1 ? $"\n-# part {i + 1}/{aParts.Count}" : "");
                var j = SCP_JsonData.NewObject();
                j.Set("username", aName);
                if (aAvatar.Length > 0) j.Set("avatar_url", aAvatar);
                j.Set("content", aContent);
                var aMentions = SCP_JsonData.NewObject();
                aMentions.Set("parse", SCP_JsonData.NewArray());
                j.Set("allowed_mentions", aMentions);
                aOut.Add(j.ToJson(false));
            }
            return aOut;
        }

        static List<string> Split(string iText, int iMax)
        {
            var aOut = new List<string>();
            string s = iText.Replace("\r\n", "\n");
            if (iMax < 200) iMax = 200;
            while (s.Length > iMax)
            {
                int aCut = s.LastIndexOf('\n', iMax);
                if (aCut < iMax / 2) aCut = iMax;   // 附近沒有換行 ⇒ 硬切
                aOut.Add(s.Substring(0, aCut).TrimEnd());
                s = s.Substring(aCut).TrimStart('\n');
            }
            if (s.Length > 0 || aOut.Count == 0) aOut.Add(s);
            return aOut;
        }

        /// <summary>POST 一個 payload；429 照 Retry-After 等完重試（最多 5 次）。⛔ 錯誤訊息不含 URL。</summary>
        public static bool TryPost(string iUrl, string iPayload, out string? oError)
        {
            oError = null;
            if (!(SCP_HttpFetch.Current is ISCP_HttpPoster aPost)) { oError = "本宿主沒有能 POST 的抓取器（要在 Senate 跑）"; return false; }
            for (int aTry = 0; aTry < 5; aTry++)
            {
                if (aPost.TryPostJson(iUrl + (iUrl.Contains("?") ? "&" : "?") + "wait=true", iPayload, 20, out string aBody, out int aStatus, out double aRetry, out string? aErr))
                    return true;
                if (aStatus == 429)
                {
                    double aSec = aRetry > 0 ? aRetry : ReadRetryAfter(aBody);
                    Thread.Sleep((int)Math.Ceiling(Math.Max(aSec, 1.0) * 1000) + 250);
                    continue;
                }
                oError = aStatus > 0 ? $"HTTP {aStatus}：{Trim(aBody)}" : (aErr ?? "連不上");
                return false;
            }
            oError = "連續 429（被限流）5 次 ⇒ 放棄這一則";
            return false;
        }

        static double ReadRetryAfter(string iBody)
        {
            try { return SCP_JsonParser.Parse(iBody).GetDouble("retry_after", 2.0); } catch (Exception) { return 2.0; }
        }

        static string Trim(string iBody)
        {
            string a = (iBody ?? "").Replace('\n', ' ');
            return a.Length <= 160 ? a : a.Substring(0, 160) + "…";
        }

        // ── 補發 ─────────────────────────────────────────────────────

        static string StatePath(string iDataRoot) => SCP_DiscordPaths.Dir(iDataRoot) + "/" + BackfillStateFileName;

        static SCP_JsonData LoadState(string iDataRoot)
        {
            string p = StatePath(iDataRoot);
            try { if (File.Exists(p)) return SCP_JsonParser.Parse(File.ReadAllText(p, Encoding.UTF8)); } catch (Exception) { }
            return SCP_JsonData.NewObject();
        }

        static void SaveState(string iDataRoot, SCP_JsonData iState)
        {
            string p = StatePath(iDataRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(p) ?? ".");
            File.WriteAllText(p + ".tmp", iState.ToJson(true), new UTF8Encoding(false));
            SCP.Core.Io.SCP_TextFile.ReplaceOrMove(p + ".tmp", p);
        }

        /// <summary>
        /// **常駐 Outbound 的一輪**（TASK-0316 ③）：把一個頻道「游標之後」的新訊息送到它的分類綁的每一條 webhook。
        /// · 游標與補發共用（`discord_backfill_state.json`）⇒ 補發送過的不會再送。
        /// · 某條 webhook 在這個房**還沒有游標** ⇒ 設成目前最新 seq、⛔ 不回放歷史（tavern 有兩萬多則）。
        /// · 積壓超過 <paramref name="iMaxBacklog"/> 則 ⇒ 跳到最新並回報（爆量保護：⛔ 不一口氣洗版）。
        /// · 只讀游標之後的訊息（⛔ 不每輪從 seq 1 掃起）。
        /// </summary>
        public static SCP_DiscordBackfillReport SendNew(string iDataRoot, string iLettersRoot, string iRoom, int iMaxBacklog, Action<string>? iProgress = null)
        {
            var r = new SCP_DiscordBackfillReport();
            SCP_ChannelSettings aSet = SCP_TavernChannels.LoadSettings(iDataRoot, iRoom);
            if (aSet.Archived || aSet.Category.Length == 0) return r;   // 未分類／封存 ⇒ 不送（安靜：這是常態，不是錯）
            SCP_DiscordConfig c = SCP_DiscordConfigStore.Load(iDataRoot);
            if (c.Error.Length > 0) { r.Problems.Add(c.Error); return r; }
            List<string> aIds = c.CategoryWebhooks.TryGetValue(aSet.Category, out List<string>? l) ? l : new List<string>();
            List<SCP_DiscordWebhookInfo> aHooks = c.Webhooks.Where(w => aIds.Contains(w.Id) && w.Enabled).ToList();
            if (aHooks.Count == 0) return r;
            List<SCP_TavernMessage> aTail = SCP_TavernRead.Tail(iDataRoot, iRoom, 1);
            int aLast = aTail.Count > 0 ? aTail[0].Seq : 0;
            if (aLast == 0) return r;

            SCP_JsonData aState = LoadState(iDataRoot);
            if (!aState[iRoom].IsObject) aState.Set(iRoom, SCP_JsonData.NewObject());
            bool aDirty = false;
            int aMin = int.MaxValue;
            foreach (SCP_DiscordWebhookInfo w in aHooks)
            {
                if (!aState[iRoom].Contains(w.Id)) { aState[iRoom].Set(w.Id, aLast); aDirty = true; iProgress?.Invoke($"{iRoom} → {w.Describe()}：第一次接上，從 seq {aLast} 之後開始（不回放歷史）"); }
                int aDone = (int)aState[iRoom].GetLong(w.Id, aLast);
                if (aLast - aDone > iMaxBacklog)
                {
                    r.Problems.Add($"{iRoom} → {w.Describe()}：積壓 {aLast - aDone} 則（上限 {iMaxBacklog}）⇒ 跳到 seq {aLast}，seq {aDone + 1}～{aLast} 沒送（要補用 op=backfill）");
                    aState[iRoom].Set(w.Id, aLast); aDirty = true; aDone = aLast;
                }
                aMin = Math.Min(aMin, aDone);
            }
            if (aDirty) SaveState(iDataRoot, aState);
            if (aMin >= aLast) return r;
            return Backfill(iDataRoot, iLettersRoot, iRoom, aMin + 1, aLast, false, iProgress);
        }

        /// <summary>
        /// 把一個頻道 seq 範圍內的舊訊息補送到「它的分類」綁的每一條啟用中的 webhook。
        /// <paramref name="iDryRun"/>=true ⇒ 只算要送幾則，⛔ 不發。游標見檔頭。
        /// </summary>
        public static SCP_DiscordBackfillReport Backfill(string iDataRoot, string iLettersRoot, string iRoom, int iFromSeq, int iToSeq,
                                                         bool iDryRun, Action<string>? iProgress = null)
        {
            var r = new SCP_DiscordBackfillReport();
            SCP_ChannelSettings aSet = SCP_TavernChannels.LoadSettings(iDataRoot, iRoom);
            if (!SCP_TavernChannels.ChannelExists(iDataRoot, iRoom)) { r.Problems.Add($"沒有這個頻道：'{iRoom}'"); return r; }
            if (aSet.Archived) { r.Problems.Add($"'{iRoom}' 已封存 ⇒ 不送"); return r; }
            if (aSet.Category.Length == 0) { r.Problems.Add($"'{iRoom}' 沒有分類 ⇒ 不送（webhook 綁在分類上）"); return r; }

            SCP_DiscordConfig c = SCP_DiscordConfigStore.Load(iDataRoot);
            if (c.Error.Length > 0) { r.Problems.Add(c.Error); return r; }
            List<string> aIds = c.CategoryWebhooks.TryGetValue(aSet.Category, out List<string>? l) ? l : new List<string>();
            List<SCP_DiscordWebhookInfo> aHooks = c.Webhooks.Where(w => aIds.Contains(w.Id) && w.Enabled).ToList();
            if (aHooks.Count == 0) { r.Problems.Add($"分類 {aSet.Category} 沒有綁任何啟用中的 webhook ⇒ 不送"); return r; }
            r.Targets.AddRange(aHooks.Select(w => w.Describe()));
            Dictionary<string, string> aUrls = SCP_DiscordConfigStore.LoadWebhookUrls(iDataRoot, out string? aUrlErr);
            if (aUrlErr != null) { r.Problems.Add(aUrlErr); return r; }

            List<SCP_TavernMessage> aTail = SCP_TavernRead.Tail(iDataRoot, iRoom, 1);
            int aLast = aTail.Count > 0 ? aTail[0].Seq : 0;
            int aFrom = Math.Max(1, iFromSeq), aTo = iToSeq > 0 ? Math.Min(iToSeq, aLast) : aLast;
            // 範圍從「最落後的那條 webhook 的游標」之後開始讀就夠了 —— ⛔ 不為了跳過已送的去讀整個房
            SCP_JsonData aPeek = LoadState(iDataRoot);
            int aMinDone = aHooks.Select(w => (int)aPeek[iRoom].GetLong(w.Id, 0)).DefaultIfEmpty(0).Min();
            aFrom = Math.Max(aFrom, aMinDone + 1);
            List<SCP_TavernMessage> aMsgs = aTo >= aFrom ? SCP_TavernRead.Range(iDataRoot, iRoom, aFrom, aTo) : new List<SCP_TavernMessage>();
            r.Messages = aMsgs.Count;
            List<SCP_TavernMessage> aEligible = aMsgs.Where(IsEligible).OrderBy(m => m.Seq).ToList();
            r.Eligible = aEligible.Count;

            SCP_JsonData aState = LoadState(iDataRoot);
            if (!aState[iRoom].IsObject) aState.Set(iRoom, SCP_JsonData.NewObject());
            bool aFirstPost = true;
            foreach (SCP_DiscordWebhookInfo w in aHooks)
            {
                if (!aUrls.TryGetValue(w.Id, out string? aUrl)) { r.Problems.Add($"{w.Describe()}：密文裡沒有它的 URL"); continue; }
                int aDone = (int)aState[iRoom].GetLong(w.Id, 0);
                foreach (SCP_TavernMessage m in aEligible)
                {
                    if (m.Seq <= aDone) { r.SkippedAlready++; continue; }
                    List<string> aPayloads = BuildPayloads(iLettersRoot, c.AvatarUrlTemplate, iRoom, m);
                    if (iDryRun) { r.Posts += aPayloads.Count; r.Sent++; continue; }
                    bool aAllOk = true;
                    foreach (string p in aPayloads)
                    {
                        if (!aFirstPost) Thread.Sleep(PaceMs);
                        aFirstPost = false;
                        if (!TryPost(aUrl, p, out string? aErr))
                        {
                            r.Problems.Add($"{w.Describe()} seq {m.Seq}：{aErr}");
                            aAllOk = false;
                            // 401／403／404 ＝ webhook 被刪了或權限沒了 ⇒ 重試不會好 ⇒ 停用它（⛔ 否則每 2 秒重打一次）
                            string aE = aErr ?? "";
                            if (aE.StartsWith("HTTP 401") || aE.StartsWith("HTTP 403") || aE.StartsWith("HTTP 404"))
                            {
                                SCP_DiscordConfigStore.MarkDead(iDataRoot, w.Id, aE.Substring(0, 8));
                                r.Problems.Add($"{w.Describe()}：{aE.Substring(0, 8)} ⇒ 已自動停用這條 webhook（到「Discord Webhook」頁確認後再啟用）");
                            }
                            break;
                        }
                        r.Posts++;
                    }
                    if (!aAllOk) break;   // ⛔ 失敗就停在這一則：游標不前進，下次從這則重送（寧可重送半則，也不跳過）
                    r.Sent++;
                    aState[iRoom].Set(w.Id, m.Seq);
                    SaveState(iDataRoot, aState);   // 每一則都落盤 ⇒ 中途被砍也接得回來
                    iProgress?.Invoke($"{iRoom} seq {m.Seq} → {w.Describe()}（{r.Sent}/{aEligible.Count}）");
                }
            }
            return r;
        }
    }
}
