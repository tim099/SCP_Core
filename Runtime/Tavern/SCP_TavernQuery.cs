// 區塊職責：酒館訊息的**查詢與呈現**層（Senate 側）—— 「篩哪些、排怎樣、印成什麼」。
// 物理意義：底下的 `SCP_TavernRead` 負責「訊息在哪、怎麼讀」，本層只負責呈現。
//           兩層刻意分開：讀取層有索引與退化路徑的責任，而呈現會常改，
//           混在一起會讓改版面要動到讀檔路徑。
// 數值影響：**純讀**。不寫任何檔、不動游標、不碰金流。
//
// ⛔ **輸出格式原樣照搬 Editor 側 `UCL_TavernQueryService`**（TASK-0240 鐵則）——
//   引 UCL_Core skill 瘦身鐵則⑤：「先『原樣搬移』再談精修，保 diff 可審計，別搬移同時改語意」。
//   ⇒ 驗收④的「逐筆對拍」要有基準可比；改了格式那一格就簽不掉。
//
// ⭐ 唯一刻意的差異（而它是加嚴，不是改格式）：
//   Editor 側 `SenderLabel` 在落盤 `sender_name` 為空時**靜默**去查 `UCL_BankAccountProfileIO`。
//   本側沒有那個查詢器 ⇒ 降級成印 `sender_id`，**並且把降級筆數印在頁尾**。
//   🩸 理由：靜默降級會讓「這則沒有署名」變成看不出來的事。
//   ⚠ 射程：抽樣三天（2026-05-08／07-16／09-18，共 309 則）**缺 sender_name 0 則**
//      ⇒ 降級的實際影響接近零，⛔ 但那是 1.6% 的抽樣，不是全庫讀數。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SCP.Core.Tavern
{
    /// <summary>
    /// 呈現層。**partial** —— 單房那幾支（<c>tail</c>／<c>seq</c>）在本檔，
    /// 跨房那 5 支（<c>rooms</c>／<c>search</c>／<c>by_sender</c>／<c>timeline</c>／<c>stats</c>）
    /// 在 <c>SCP_TavernQueryCross.cs</c>。
    /// ⛔ 拆檔的理由不是行數，是**成本模型不同**：單房那兩支的成本是 O(要的那幾則)，
    ///    跨房那 5 支是 O(所有房 × 上限) —— 兩者該分開讀、分開量。
    /// </summary>
    public static partial class SCP_TavernQuery
    {
        /// <summary>單房掃描上限（`kind=seq` 不給區間時用）。⚠ 與 Editor 側同值。</summary>
        const int SCAN_PER_ROOM = 4000;

        public const int DefaultBodyClip = 200;

        // ===========================================================
        // 呈現（⛔ 逐字對齊 Editor 側，見檔頭）
        // ===========================================================
        static DateTime ParseTs(string iTs)
        {
            if (DateTime.TryParse(iTs, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)) return t;
            return DateTime.MinValue;
        }

        static string LocalHm(string iTs)
        {
            var t = ParseTs(iTs);
            return t == DateTime.MinValue ? "??:??:??" : t.ToLocalTime().ToString("MM-dd HH:mm:ss");
        }

        static string Clip(string iBody, int iMax)
        {
            string b = (iBody ?? "").Replace("\r", "").Replace("\n", " ⏎ ").Trim();
            if (iMax <= 0 || b.Length <= iMax) return b;
            return b.Substring(0, iMax) + $"…（截斷，全文 {b.Length} 字）";
        }

        /// <summary>
        /// 「誰說的」。⚠ 落盤沒有署名時降級成 id，並回報有沒有降級 ——
        /// ⛔ 不靜默：呼叫端要把降級筆數印出來（見檔頭）。
        /// </summary>
        static string SenderLabel(SCP_TavernMessage m, out bool oDegraded)
        {
            oDegraded = !m.HasSenderName;
            string aName = m.HasSenderName ? m.SenderName
                                           : (m.SenderId.Length > 0 ? m.SenderId : "?");
            return m.SenderPersona.Length > 0 ? aName + "@" + m.SenderPersona : aName;
        }

        static void AppendMsgLine(StringBuilder ioSb, SCP_TavernMessage m, string? iRoom, int iClip,
                                  ref int ioDegraded)
        {
            string aTag = (m.Meta.TryGetValue("tag", out var tg) && !string.IsNullOrEmpty(tg))
                ? $" «{tg}»" : "";
            string aRoom = string.IsNullOrEmpty(iRoom) ? "" : $" [{iRoom}]";
            string aWho = SenderLabel(m, out bool aDeg);
            if (aDeg) ioDegraded++;
            ioSb.AppendLine($"- **[seq {m.Seq}]** {LocalHm(m.Ts)}{aRoom} **{aWho}**{aTag}");
            ioSb.AppendLine($"    {Clip(m.Body, iClip)}");
        }

        /// <summary>把讀取層的診斷與降級筆數收在頁尾 —— ⛔ 那些不進正文，但也不准消失。</summary>
        static void AppendFooter(StringBuilder ioSb, SCP_TavernReadStat iStat, int iDegraded)
        {
            if (iDegraded > 0)
            {
                ioSb.AppendLine();
                ioSb.AppendLine($"⚠ **{iDegraded}** 則落盤沒有 `sender_name` ⇒ 顯示降級成 `sender_id`。"
                    + "⛔ 本側沒有顯示名查詢器（Editor 那側有），所以這一格是**降級**不是等價。");
            }
            foreach (string aW in iStat.Warnings)
            {
                ioSb.AppendLine();
                ioSb.AppendLine(aW);
            }
        }

        // ===========================================================
        // kind=tail —— 單房最後 N 則
        // ===========================================================
        public static string Tail(string iDataRoot, string iRoom, int iLimit, int iClip,
                                  out SCP_TavernReadStat oStat)
        {
            string aRoom = string.IsNullOrEmpty(iRoom) ? "tavern" : iRoom;
            int aLimit = iLimit <= 0 ? 20 : iLimit;
            oStat = new SCP_TavernReadStat();
            var aMsgs = SCP_TavernRead.Tail(iDataRoot, aRoom, aLimit, oStat);
            var aSb = new StringBuilder();
            aSb.AppendLine($"# 🍻 `{aRoom}` 最後 {aMsgs.Count} 則（要求 {aLimit}）");
            aSb.AppendLine();
            int aDeg = 0;
            foreach (var m in aMsgs) AppendMsgLine(aSb, m, null, iClip, ref aDeg);
            AppendFooter(aSb, oStat, aDeg);
            return aSb.ToString();
        }

        // ===========================================================
        // kind=seq —— 指定 seq／區間／最後一批，再套 persona／sender／tag／grep 篩選
        // ===========================================================
        public static string Seq(string iDataRoot, string iRoom, int iSeq, int iFrom, int iTo, int iLast,
                                 string iPersona, string iSender, string iTag, string iGrep, bool iFull,
                                 out SCP_TavernReadStat oStat)
        {
            string aRoom = string.IsNullOrEmpty(iRoom) ? "tavern" : iRoom;
            oStat = new SCP_TavernReadStat();

            List<SCP_TavernMessage> aPool;
            string aScope;
            if (iSeq > 0)
            {
                aPool = SCP_TavernRead.Range(iDataRoot, aRoom, iSeq, iSeq, oStat);
                aScope = $"seq {iSeq}";
            }
            else if (iFrom > 0 && iTo >= iFrom)
            {
                aPool = SCP_TavernRead.Range(iDataRoot, aRoom, iFrom, iTo, oStat);
                aScope = $"seq {iFrom}-{iTo}";
            }
            else
            {
                aPool = SCP_TavernRead.Tail(iDataRoot, aRoom, SCAN_PER_ROOM, oStat);
                aScope = $"最後 {SCAN_PER_ROOM} 則內";
            }

            Regex? aGrep = null;
            if (!string.IsNullOrEmpty(iGrep))
            {
                try { aGrep = new Regex(iGrep, RegexOptions.IgnoreCase); }
                catch (Exception e) { return $"❌ grep regex 不合法：{e.Message}"; }
            }

            var aHits = new List<SCP_TavernMessage>();
            foreach (var m in aPool)
            {
                if (!string.IsNullOrEmpty(iPersona)
                    && !string.Equals(m.SenderPersona, iPersona, StringComparison.OrdinalIgnoreCase)) continue;
                if (!string.IsNullOrEmpty(iSender)
                    && m.SenderId.IndexOf(iSender, StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (!string.IsNullOrEmpty(iTag))
                {
                    string aTg = m.Meta.TryGetValue("tag", out var t) ? (t ?? "") : "";
                    if (aTg.IndexOf(iTag, StringComparison.OrdinalIgnoreCase) < 0) continue;
                }
                if (aGrep != null && !aGrep.IsMatch(m.Body)) continue;
                aHits.Add(m);
            }

            var aSb = new StringBuilder();
            aSb.AppendLine($"# 🔢 `{aRoom}` {aScope}　篩後 **{aHits.Count}** 則");
            var aFilters = new List<string>();
            if (!string.IsNullOrEmpty(iPersona)) aFilters.Add($"persona=`{iPersona}`");
            if (!string.IsNullOrEmpty(iSender)) aFilters.Add($"sender~`{iSender}`");
            if (!string.IsNullOrEmpty(iTag)) aFilters.Add($"tag~`{iTag}`");
            if (aGrep != null) aFilters.Add($"grep=`{iGrep}`");
            if (aFilters.Count > 0) aSb.AppendLine("　篩選：" + string.Join("　", aFilters.ToArray()));
            int aTake = iLast > 0 ? iLast : aHits.Count;
            if (aTake < aHits.Count) aSb.AppendLine($"　只列最後 {aTake} 筆（**顯示上限，不是命中數**）");
            aSb.AppendLine();
            int aDeg = 0;
            for (int i = Math.Max(0, aHits.Count - aTake); i < aHits.Count; i++)
                AppendMsgLine(aSb, aHits[i], null, iFull ? 0 : DefaultBodyClip, ref aDeg);
            AppendFooter(aSb, oStat, aDeg);
            return aSb.ToString();
        }
    }
}
