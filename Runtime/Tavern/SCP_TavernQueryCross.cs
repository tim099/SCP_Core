// 區塊職責：**跨房**查詢的呈現層 —— `rooms` / `search` / `by_sender` / `timeline` / `stats`。
// 物理意義：`SCP_TavernQuery` 的 partial 另一半。單房那兩支（tail／seq）的成本是 O(要的那幾則)，
//           而本檔這 5 支是 **O(所有房 × 每房上限)** —— 兩者該分開讀、分開量。
// 數值影響：**純讀**。⛔ 不寫任何檔（含索引）。
//
// ⚠ 索引救得了**路徑列舉**，救不了 **parse 內容** ——
//   `Collect()` 對每一房要 `Tail(room, 4000)`，而那 4000 則是真的要讀進來 parse 的。
//   ⇒ 本檔的成本主項不是列目錄，是 JSON 反序列化。
//   📌 所以每一支都把 `掃 N 則／M 房` 印在標頭裡（Editor 側原本就這樣）——
//      **那個數字就是這次付的錢**，⛔ 別讓它只活在體感裡。
//
// ⛔ 輸出格式原樣照搬 Editor 側 `UCL_TavernQueryService`（見 `SCP_TavernQuery.cs` 檔頭的鐵則）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace SCP.Core.Tavern
{
    public static partial class SCP_TavernQuery
    {
        // ===========================================================
        // 區塊職責：時間窗口字串（`24h` / `7d` / `90m`）→ UTC 起點。
        // 數值影響：回傳 null 代表「不限時間」；解析失敗一律當 null **並在輸出標明**，
        //           ⛔ 不靜默套用預設值 —— 那會讓人以為自己的 `--since` 生效了。
        // ===========================================================
        public static DateTime? ParseSince(string iSince, out string? oNote)
        {
            oNote = null;
            string s = (iSince ?? "").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(s)) return null;
            var m = Regex.Match(s, SincePattern);
            if (!m.Success)
            {
                oNote = $"⚠ `since={iSince}` 解析不了（格式：`90m` / `24h` / `7d`）—— **本次未套用時間窗口**";
                return null;
            }
            int n = int.Parse(m.Groups[1].Value);
            switch (m.Groups[2].Value)
            {
                case "s": return DateTime.UtcNow.AddSeconds(-n);
                case "m": return DateTime.UtcNow.AddMinutes(-n);
                case "h": return DateTime.UtcNow.AddHours(-n);
                default: return DateTime.UtcNow.AddDays(-n);
            }
        }

        /// <summary>
        /// `since` 的樣式。⛔ 刻意用 <c>char</c> 拼接而不寫字面 regex ——
        /// 本檔是經 shell／python 搬進來的，而那兩層都會把反斜線吃掉一層
        /// （2026-09-18 實測：同一個字元咬了兩次，共 18 個編譯錯誤）。
        /// ⇒ `^(\d+)\s*([smhd])$`，由數值碼組出來。
        /// </summary>
        static readonly string SincePattern =
            "^(" + (char)92 + "d+)" + (char)92 + "s*([smhd])$";

        static bool InWindow(SCP_TavernMessage m, DateTime? iSince)
            => iSince == null || ParseTs(m.Ts).ToUniversalTime() >= iSince.Value;

        // ===========================================================
        // 區塊職責：取一批要參與查詢的訊息（跨房或單房）。
        // 數值影響：oScanned 是**實際讀進來的則數** —— 呈報口徑用，⛔ 別省。
        // ⚠ 判準是「**有沒有某一房掃到頂**」，不是「總則數有沒有超過上限」。
        //   🩸 Editor 側首航踩過：59 房加總 5014 則就觸發警告，而沒有任何一房接近 4000 ——
        //   一個永遠會亮的警告等於沒有警告，而它宣稱的「清單不完整」是假的（判準⑤：名字比事實大）。
        // ===========================================================
        static List<KeyValuePair<string, SCP_TavernMessage>> Collect(
            string iDataRoot, string? iRoom, DateTime? iSince,
            out int oScanned, out int oRoomCount, out List<string> oCapped,
            SCP_TavernReadStat ioStat)
        {
            var aResult = new List<KeyValuePair<string, SCP_TavernMessage>>();
            oScanned = 0;
            oCapped = new List<string>();
            var aRooms = new List<string>();
            if (!string.IsNullOrEmpty(iRoom)) aRooms.Add(iRoom!);
            else aRooms.AddRange(SCP_TavernRead.EnumerateRoomIds(iDataRoot));
            oRoomCount = aRooms.Count;

            int aFellBack = 0, aStaleRooms = 0;
            foreach (string r in aRooms)
            {
                List<SCP_TavernMessage> aBatch;
                var aPerRoom = new SCP_TavernReadStat();
                try { aBatch = SCP_TavernRead.Tail(iDataRoot, r, SCAN_PER_ROOM, aPerRoom); }
                catch (Exception e)
                {
                    // ⛔ 讀不動一房**不靜默跳過** —— 收進 warnings，呼叫端會把它印在頁尾。
                    ioStat.Warnings.Add($"⚠ 讀房間 `{r}` 失敗：{e.Message}（本房未參與本次查詢）");
                    continue;
                }
                if (aPerRoom.FellBackToFullScan) aFellBack++;
                else if (aPerRoom.StaleDays > 0) aStaleRooms++;

                oScanned += aBatch.Count;
                if (aBatch.Count >= SCAN_PER_ROOM) oCapped.Add($"`{r}`");
                foreach (var m in aBatch)
                    if (InWindow(m, iSince))
                        aResult.Add(new KeyValuePair<string, SCP_TavernMessage>(r, m));
            }
            aResult.Sort((a, b) => string.CompareOrdinal(a.Value.Ts, b.Value.Ts));

            // ⭐ 跨房時「哪幾房退化了」要匯總說出來 —— 逐房印會淹掉正文，完全不印就變成靜默。
            if (aFellBack > 0)
                ioStat.Warnings.Add($"🔻 **{aFellBack}／{oRoomCount}** 房的索引走不了，那幾房走了全量列舉"
                                    + " —— 修它：`senate cmd tavern-index --arg op=rebuild`");
            if (aStaleRooms > 0)
                ioStat.Warnings.Add($"⚠ **{aStaleRooms}／{oRoomCount}** 房的索引落後（那幾天現場列舉）"
                                    + " —— ⛔ 本側不自動補寫（純讀不寫）；修它：`op=rebuild`");
            ioStat.Total = oScanned;
            return aResult;
        }

        static string TruncationNote(List<string> iCappedRooms)
        {
            if (iCappedRooms == null || iCappedRooms.Count == 0) return "";
            var aSb = new StringBuilder();
            aSb.AppendLine();
            aSb.AppendLine($"⚠ **這些房間掃到上限（每房 {SCAN_PER_ROOM} 則）**："
                + string.Join("、", iCappedRooms.ToArray())
                + " —— 更舊的訊息沒有進入本次比對。這份清單**不完整**，縮小 `since` 或指定 `room` 再查一次。");
            return aSb.ToString();
        }

        // ===========================================================
        // kind=rooms —— 房間清單 ＋ 最後活動
        // ⚠ 本支**不走 Collect**：它每房只要最後 1 則 ＋ 一個計數
        //   ⇒ 成本是 O(房數)，⛔ 不是 O(房數 × 4000)。別為了統一而讓它變貴。
        // ===========================================================
        public static string Rooms(string iDataRoot, string iSince, out SCP_TavernReadStat oStat)
        {
            oStat = new SCP_TavernReadStat();
            var aSince = ParseSince(iSince, out string? aNote);
            var aSb = new StringBuilder();
            aSb.AppendLine($"# 🍻 酒館房間　窗口 `{(string.IsNullOrEmpty(iSince) ? "(不限)" : iSince)}`");
            if (aNote != null) aSb.AppendLine(aNote);
            aSb.AppendLine();
            var aIds = SCP_TavernRead.EnumerateRoomIds(iDataRoot);
            aSb.AppendLine($"共 **{aIds.Count}** 房：");
            int aDeg = 0;
            foreach (string r in aIds)
            {
                var aTail = SCP_TavernRead.Tail(iDataRoot, r, 1, oStat);
                SCP_TavernMessage? aLast = aTail.Count > 0 ? aTail[0] : null;
                int aTotal = SCP_TavernRead.CountMessages(iDataRoot, r);
                bool aActive = aLast != null && InWindow(aLast, aSince);
                if (aLast == null)
                {
                    aSb.AppendLine($"- `{r}`　{aTotal} 則　（無訊息）");
                    continue;
                }
                string aWho = SenderLabel(aLast, out bool aD);
                if (aD) aDeg++;
                aSb.AppendLine($"- {(aActive ? "🟢" : "⚪")} `{r}`　{aTotal} 則　最後 {LocalHm(aLast.Ts)}　{aWho}");
            }
            AppendFooter(aSb, oStat, aDeg);
            return aSb.ToString();
        }

        // ===========================================================
        // kind=search —— 關鍵字（預設不分大小寫）
        // ===========================================================
        public static string Search(string iDataRoot, string iKeyword, string iRoom, string iSince,
                                    bool iCaseSensitive, int iLimit, int iClip, out SCP_TavernReadStat oStat)
        {
            oStat = new SCP_TavernReadStat();
            if (string.IsNullOrEmpty(iKeyword)) return "❌ search 需要 `keyword=`";
            var aSince = ParseSince(iSince, out string? aNote);
            var aAll = Collect(iDataRoot, iRoom, aSince, out int aScanned, out int aRoomCount,
                               out var aCapped, oStat);
            var aCmp = iCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var aHits = new List<KeyValuePair<string, SCP_TavernMessage>>();
            foreach (var kv in aAll)
                if (kv.Value.Body.IndexOf(iKeyword, aCmp) >= 0) aHits.Add(kv);

            int aLimit = iLimit <= 0 ? 30 : iLimit;
            var aSb = new StringBuilder();
            aSb.AppendLine($"# 🔍 `{iKeyword}`　命中 **{aHits.Count}**　"
                        + $"（掃 {aScanned} 則／{aRoomCount} 房／窗口 `{(string.IsNullOrEmpty(iSince) ? "不限" : iSince)}`"
                        + $"／{(iCaseSensitive ? "大小寫敏感" : "不分大小寫")}）");
            if (aNote != null) aSb.AppendLine(aNote);
            aSb.Append(TruncationNote(aCapped));
            if (aHits.Count > aLimit)
                aSb.AppendLine($"\n⚠ 只列最新 {aLimit} 筆（命中 {aHits.Count}）—— 這是**顯示上限，不是命中數**。\n");
            aSb.AppendLine();
            int aDeg = 0;
            for (int i = Math.Max(0, aHits.Count - aLimit); i < aHits.Count; i++)
                AppendMsgLine(aSb, aHits[i].Value, aHits[i].Key, iClip, ref aDeg);
            AppendFooter(aSb, oStat, aDeg);
            return aSb.ToString();
        }

        // ===========================================================
        // kind=by_sender
        // ===========================================================
        public static string BySender(string iDataRoot, string iSenderId, string iSince, int iLimit,
                                      int iClip, out SCP_TavernReadStat oStat)
        {
            oStat = new SCP_TavernReadStat();
            if (string.IsNullOrEmpty(iSenderId)) return "❌ by_sender 需要 `sender=`";
            var aSince = ParseSince(iSince, out string? aNote);
            var aAll = Collect(iDataRoot, null, aSince, out int aScanned, out int aRoomCount,
                               out var aCapped, oStat);
            var aHits = new List<KeyValuePair<string, SCP_TavernMessage>>();
            foreach (var kv in aAll)
            {
                var m = kv.Value;
                bool aHit = string.Equals(m.SenderId, iSenderId, StringComparison.OrdinalIgnoreCase)
                         || string.Equals(m.SenderPersona, iSenderId, StringComparison.OrdinalIgnoreCase);
                if (aHit) aHits.Add(kv);
            }
            int aLimit = iLimit <= 0 ? 20 : iLimit;
            var aSb = new StringBuilder();
            aSb.AppendLine($"# 👤 `{iSenderId}`　{aHits.Count} 則"
                        + $"（掃 {aScanned} 則／{aRoomCount} 房／窗口 `{(string.IsNullOrEmpty(iSince) ? "不限" : iSince)}`）");
            aSb.AppendLine("　比對 `sender_id` 與 `sender_persona` 兩欄 —— 合一之後兩者是不同的名字。");
            if (aNote != null) aSb.AppendLine(aNote);
            aSb.Append(TruncationNote(aCapped));
            aSb.AppendLine();
            int aDeg = 0;
            for (int i = Math.Max(0, aHits.Count - aLimit); i < aHits.Count; i++)
                AppendMsgLine(aSb, aHits[i].Value, aHits[i].Key, iClip, ref aDeg);
            AppendFooter(aSb, oStat, aDeg);
            return aSb.ToString();
        }

        // ===========================================================
        // kind=timeline —— 跨房時序流
        // ===========================================================
        public static string Timeline(string iDataRoot, string iSince, int iLimit, int iClip,
                                      out SCP_TavernReadStat oStat)
        {
            oStat = new SCP_TavernReadStat();
            var aSince = ParseSince(iSince, out string? aNote);
            var aAll = Collect(iDataRoot, null, aSince, out int aScanned, out int aRoomCount,
                               out var aCapped, oStat);
            int aLimit = iLimit <= 0 ? 30 : iLimit;
            var aSb = new StringBuilder();
            aSb.AppendLine($"# ⏱ 跨房時序　{aAll.Count} 則（掃 {aScanned}／{aRoomCount} 房／窗口 "
                        + $"`{(string.IsNullOrEmpty(iSince) ? "不限" : iSince)}`）");
            if (aNote != null) aSb.AppendLine(aNote);
            aSb.Append(TruncationNote(aCapped));
            aSb.AppendLine();
            int aDeg = 0;
            for (int i = Math.Max(0, aAll.Count - aLimit); i < aAll.Count; i++)
                AppendMsgLine(aSb, aAll[i].Value, aAll[i].Key, iClip, ref aDeg);
            AppendFooter(aSb, oStat, aDeg);
            return aSb.ToString();
        }

        // ===========================================================
        // kind=stats —— 訊息數統計（依房、依 sender）
        // ===========================================================
        public static string Stats(string iDataRoot, string iSince, out SCP_TavernReadStat oStat)
        {
            oStat = new SCP_TavernReadStat();
            var aSince = ParseSince(iSince, out string? aNote);
            var aAll = Collect(iDataRoot, null, aSince, out int aScanned, out int aRoomCount,
                               out var aCapped, oStat);
            var aByRoom = new Dictionary<string, int>(StringComparer.Ordinal);
            var aBySender = new Dictionary<string, int>(StringComparer.Ordinal);
            int aDeg = 0;
            foreach (var kv in aAll)
            {
                aByRoom.TryGetValue(kv.Key, out int rc); aByRoom[kv.Key] = rc + 1;
                string s = SenderLabel(kv.Value, out bool aD);
                if (aD) aDeg++;
                aBySender.TryGetValue(s, out int sc); aBySender[s] = sc + 1;
            }
            var aSb = new StringBuilder();
            aSb.AppendLine($"# 📊 統計　窗口 `{(string.IsNullOrEmpty(iSince) ? "不限" : iSince)}`　"
                        + $"共 **{aAll.Count}** 則（掃 {aScanned} 則／{aRoomCount} 房）");
            if (aNote != null) aSb.AppendLine(aNote);
            aSb.Append(TruncationNote(aCapped));
            aSb.AppendLine("\n## 依房間");
            var aRooms = new List<KeyValuePair<string, int>>(aByRoom);
            aRooms.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in aRooms) aSb.AppendLine($"- `{kv.Key}`　**{kv.Value}**");
            aSb.AppendLine("\n## 依發話者");
            var aSenders = new List<KeyValuePair<string, int>>(aBySender);
            aSenders.Sort((a, b) => b.Value.CompareTo(a.Value));
            foreach (var kv in aSenders) aSb.AppendLine($"- {kv.Key}　**{kv.Value}**");
            AppendFooter(aSb, oStat, aDeg);
            return aSb.ToString();
        }

        // 🩸 這裡原本有一個 `const string BackSlash = "\";` —— **它自己就是第四次**：
        //    C# 的字串字面會把 `\` 解釋成反斜線字元 ⇒ 編譯器看到的是一個沒關起來的字串（CS1010）。
        //    ⇒ 同一個字元今天咬了四次，四次都是**某一層解釋了我要它原樣傳遞的東西**：
        //      ① bash heredoc（連 `<<'EOF'` 也一樣）② python heredoc ③ C# 字串字面
        //      ④ 以及最早那次「用轉義碼躲開 heredoc」，結果被下一層解釋。
        //    📌 一般化：**要傳遞一個轉義字元，就不要用任何會解釋轉義的表示法** ——
        //      用數值碼（`(char)92`）讓它在每一層都只是一個數字。
    }
}
