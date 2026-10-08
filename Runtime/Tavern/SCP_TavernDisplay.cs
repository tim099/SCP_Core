// 區塊職責：**一則酒館訊息要怎麼顯示**的唯一判準（TASK-0317 ②）—— Senate 酒館頁與之後的 Discord 轉發共用。
// 物理意義：訊息 → 顯示列：寄件人是誰（persona／系統／外部／不明）、名字、頭像、顏色、時間、本文、附件、meta。
//           寄件人判定（由上往下，第一個成立的算數）：
//             ① `sender_persona` 有值                     ⇒ 那個 persona
//             ② `sender_id = tavern-keeper`                ⇒ persona `tavern-keeper`（酒保）
//             ③ `sender_id` 以 `discord:` 開頭             ⇒ **外部**（Discord 使用者）：名字用 sender_name，沒有頭像
//             ④ kind=system，或 sender_id 是 system／以 `_` 開頭 ⇒ persona `system`
//             ⑤ `sender_id` 剛好是一個信件夾名            ⇒ 那個 persona
//             ⑥ 其餘                                        ⇒ **不明**：名字用 sender_name（沒有就 sender_id），沒有頭像
//           顯示名（Tim 2026-09-28）：persona 的訊息印 **`Agent@persona`** —— Agent 取自訊息自己的 `sender_id`
//             （寫入端填的是銀行帳號 id：Myth／Zeta／claude-code…）；sender_id 空、等於 persona 本身、
//             或是系統身分（tavern-keeper／system）⇒ 只印 persona。⛔ 不去別處查 agent（訊息沒寫就是沒有）。
//           頭像與顏色**只看那個 persona 自己的資料夾**（`SCP_PersonaDisplay`）—— ⛔ 不借 agent 的。
// 數值影響：純讀。頭像路徑由顯示端自己載（本檔不碰圖檔內容）。
// ⚠ 方言限制：C# 9 / netstandard2.1。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SCP.Core.Letters;

namespace SCP.Core.Tavern
{
    public enum SCP_TavernSenderKind
    {
        /// <summary>pool 裡的 persona（含系統身分 tavern-keeper／system）。</summary>
        Persona,
        /// <summary>外部中繼來的人（Discord）。</summary>
        External,
        /// <summary>認不出是誰 —— 顯示端要讓它看得出來是「不明」，⛔ 不假裝是某個 persona。</summary>
        Unknown,
    }

    /// <summary>一則訊息的顯示列。</summary>
    public sealed class SCP_TavernDisplayRow
    {
        public SCP_TavernMessage Message = new SCP_TavernMessage();
        public SCP_TavernSenderKind SenderKind;
        /// <summary>判成的 persona（External／Unknown 時是空字串）。</summary>
        public string Persona = "";
        /// <summary>發文的 Agent（取自訊息的 `sender_id`）；系統身分或訊息沒寫 ⇒ 空。</summary>
        public string Agent = "";
        /// <summary>顯示名：persona 訊息是 `Agent@persona`（沒有 Agent 時就是 persona）；外部／不明見檔頭。</summary>
        public string Name = "";
        /// <summary>頭像絕對路徑；空 ＝ 畫預設圖。</summary>
        public string AvatarPath = "";
        public string ColorHex = "";
        /// <summary>本地時間 `yyyy-MM-dd HH:mm`；ts 讀不出來 ⇒ 原文。</summary>
        public string TimeLocal = "";
        /// <summary>meta 的 `k=v` 串（⛔ 不含 `_` 開頭的寫入端內部鍵）；沒有 ⇒ 空。</summary>
        public string MetaText = "";
        /// <summary>寄件人判定走的是哪一條（除錯用，頁面可以印在滑鼠提示裡）。</summary>
        public string Why = "";
    }

    public static class SCP_TavernDisplay
    {
        public static SCP_TavernDisplayRow Resolve(string iLettersRoot, SCP_TavernMessage iMsg)
        {
            var aRow = new SCP_TavernDisplayRow { Message = iMsg };
            string aId = iMsg.SenderId ?? "";

            string? aPersona = null;
            if (!string.IsNullOrEmpty(iMsg.SenderPersona)) { aPersona = iMsg.SenderPersona; aRow.Why = "sender_persona"; }
            else if (aId == SCP_PersonaDisplay.TavernKeeperPersona) { aPersona = SCP_PersonaDisplay.TavernKeeperPersona; aRow.Why = "sender_id=tavern-keeper"; }
            else if (aId.StartsWith("discord:", StringComparison.Ordinal))
            {
                aRow.SenderKind = SCP_TavernSenderKind.External;
                aRow.Name = iMsg.HasSenderName ? iMsg.SenderName : aId;
                aRow.Why = "discord 外部使用者";
            }
            else if (iMsg.Kind == "system" || aId == "system" || aId.StartsWith("_", StringComparison.Ordinal))
            { aPersona = SCP_PersonaDisplay.SystemPersona; aRow.Why = "系統訊息"; }
            else if (aId.Length > 0 && System.IO.Directory.Exists(System.IO.Path.Combine(iLettersRoot ?? "", aId)))
            { aPersona = aId; aRow.Why = "sender_id 是信件夾名"; }

            if (aPersona != null)
            {
                SCP_PersonaDisplayInfo aInfo = SCP_PersonaDisplay.Get(iLettersRoot ?? "", aPersona);
                aRow.SenderKind = SCP_TavernSenderKind.Persona;
                aRow.Persona = aPersona;
                aRow.Agent = AgentOf(aId, aPersona);
                aRow.Name = aRow.Agent.Length > 0 ? aRow.Agent + "@" + aInfo.DisplayName : aInfo.DisplayName;
                aRow.AvatarPath = aInfo.AvatarPath;
                aRow.ColorHex = aInfo.ColorHex;
            }
            else if (aRow.Why.Length == 0)
            {
                aRow.SenderKind = SCP_TavernSenderKind.Unknown;
                aRow.Name = iMsg.HasSenderName ? iMsg.SenderName : (aId.Length > 0 ? aId : "（沒有寄件人）");
                aRow.Why = "認不出寄件人";
            }

            aRow.TimeLocal = FormatLocal(iMsg.Ts);
            aRow.MetaText = string.Join("  ", iMsg.Meta
                .Where(kv => !kv.Key.StartsWith("_", StringComparison.Ordinal))
                .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => kv.Key + "=" + kv.Value));
            return aRow;
        }

        public static List<SCP_TavernDisplayRow> ResolveAll(string iLettersRoot, IEnumerable<SCP_TavernMessage> iMsgs)
            => iMsgs.Select(m => Resolve(iLettersRoot, m)).ToList();

        /// <summary>sender_id 能不能當 Agent 印：空、跟 persona 同名、系統身分、內部 id ⇒ 不印。</summary>
        static string AgentOf(string iSenderId, string iPersona)
        {
            if (iSenderId.Length == 0) return "";
            if (string.Equals(iSenderId, iPersona, StringComparison.OrdinalIgnoreCase)) return "";
            if (iPersona == SCP_PersonaDisplay.TavernKeeperPersona || iPersona == SCP_PersonaDisplay.SystemPersona) return "";
            if (iSenderId == SCP_PersonaDisplay.TavernKeeperPersona || iSenderId == SCP_PersonaDisplay.SystemPersona
                || iSenderId.StartsWith("_", StringComparison.Ordinal)) return "";
            return iSenderId;
        }

        static string FormatLocal(string iTs)
        {
            if (DateTime.TryParse(iTs, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime aUtc))
                return aUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return iTs ?? "";
        }
    }
}
