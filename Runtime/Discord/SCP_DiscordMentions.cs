// 區塊職責：**酒館內文的 `@名字` → Discord 的 `<@使用者id>`**（TASK-0380）—— Discord 只認後者才會真的通知那個人。
// 物理意義：舊版轉發（TASK-0316 刪除）在送出前做這一步；
//           Senate 版搬家時漏了，酒館裡 @真人 到 Discord 只剩一串字。規則照舊版：
//           · 名字對照來源 ① `PromptQueue/notify_config.json` 的 `tavern_mirror.discord_user_mentions`（明確對照，**優先**）
//                         ② 白名單使用者的顯示名稱與別名（`SCP_DiscordInboundConfig.LoadWhitelist`）—— **只補 ① 沒登記的名字**，不覆蓋。
//           · 比對 `@([\w.\-]+)`，名字大小寫照原樣比（同舊版）；沒登記的名字**原樣保留**。
// 數值影響：純函式＋讀檔，不寫任何東西。讀不了設定 ⇒ 回空對照與錯誤字串，呼叫端照送原文（⛔ 不因通知讓訊息卡住）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Json;

namespace SCP.Core.Discord
{
    public static class SCP_DiscordMentions
    {
        static readonly Regex s_Re = new Regex(@"@([\w.\-]+)", RegexOptions.Compiled);

        /// <summary>名字 → Discord 使用者 id。<paramref name="oError"/> 非 null ＝ 有一個來源讀不了（另一個照用）。</summary>
        public static Dictionary<string, string> LoadMap(string iDataRoot, out string? oError)
        {
            oError = null;
            var aMap = new Dictionary<string, string>();
            string aPath = SCP_DiscordInboundConfig.NotifyConfigPath(iDataRoot);
            try
            {
                if (File.Exists(aPath))
                {
                    SCP_JsonData n = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                    SCP_JsonData m = n["tavern_mirror"]["discord_user_mentions"];
                    if (m.IsObject)
                        foreach (string k in m.Keys)
                            Add(aMap, k, m.GetString(k, ""));
                }
            }
            catch (Exception e) { oError = "notify_config.json 讀不了：" + e.Message; }

            SCP_DiscordWhitelist w = SCP_DiscordInboundConfig.LoadWhitelist(iDataRoot);
            if (w.Error.Length > 0) oError = (oError == null ? "" : oError + "；") + "白名單讀不了：" + w.Error;
            foreach (SCP_DiscordWhitelistUser u in w.Users)
            {
                Add(aMap, u.DisplayName, u.UserId);
                foreach (string a in u.Aliases) Add(aMap, a, u.UserId);
            }
            return aMap;
        }

        // 只補沒登記的名字 ⇒ 先加入的（明確對照）贏。
        static void Add(Dictionary<string, string> ioMap, string iName, string iUserId)
        {
            string aName = (iName ?? "").Trim();
            string aId = (iUserId ?? "").Trim();
            if (aName.Length > 0 && aId.Length > 0 && !ioMap.ContainsKey(aName)) ioMap[aName] = aId;
        }

        /// <summary>
        /// 把 <paramref name="iBody"/> 裡登記過的 `@名字` 換成 `&lt;@id&gt;`；換到的 id（去重）加進 <paramref name="ioUserIds"/>。
        /// 沒登記的原樣保留；沒有 `@` ⇒ 原字串直接回傳。
        /// </summary>
        public static string Rewrite(string iBody, IReadOnlyDictionary<string, string> iMap, List<string> ioUserIds)
        {
            if (string.IsNullOrEmpty(iBody) || iBody.IndexOf('@') < 0 || iMap.Count == 0) return iBody;
            return s_Re.Replace(iBody, m =>
            {
                if (!iMap.TryGetValue(m.Groups[1].Value, out string? aId)) return m.Value;
                if (!ioUserIds.Contains(aId)) ioUserIds.Add(aId);
                return "<@" + aId + ">";
            });
        }
    }
}
