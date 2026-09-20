// 區塊職責：酒館訊息的**讀取層**（Senate 側）—— 「要哪一段」到「那一段的路徑」再到「訊息物件」。
// 物理意義：Editor 側查詢層只用了 IO 的 4 個方法（summit 2026-09-18 量：
//           `Tail` ×4／`Range` ×2／`EnumerateRoomIds` ×2／`CountMessages` ×1）
//           ⇒ 搬家的介面面積就是這 4 個，⛔ 不是 `UCL_ChatTavernIO`（1,587 行）
//           ＋ `UCL_ChatTavernIO_PerMsgFile`（899 行）那 2,486 行。
// 數值影響：**純讀**。⛔ 一個位元組都不寫進 `ChatTavern/`（TASK-0240 驗收⑥）——
//           包括索引：落後由 `oStaleDays` 回報，修它走 `senate cmd tavern-index --arg op=rebuild`。
//
// ⭐ 兩條路各有出口，而**降級一律出聲**：
//   · 索引可用 ⇒ 路徑是**算**出來的（O(天數) 的 stat）
//   · 索引不可用 ⇒ 退回 `Directory.GetFiles(AllDirectories)` 全量列舉，並把「這次退化了」寫進 warnings
//   ⛔ 沒有第三條路，也沒有「安靜地變慢」那條。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace SCP.Core.Tavern
{
    /// <summary>一次讀取的診斷讀數 —— ⛔ 不是「有沒有成功」，是「這次走了哪條路、付了多少」。</summary>
    public sealed class SCP_TavernReadStat
    {
        /// <summary>true ＝ 至少有一天的路徑是算出來的（省到了）。</summary>
        public bool UsedIndex;

        /// <summary>索引缺幾天（＝這次現場列舉的天數）。&gt;0 ⇒ 索引落後，修它走 `op=rebuild`。</summary>
        public int StaleDays;

        /// <summary>true ＝ 索引整份不可用，這次走了全量列舉。</summary>
        public bool FellBackToFullScan;

        /// <summary>該房訊息總筆數（**權威＝檔案數**，Tim 2026-09-18 拍板）。</summary>
        public int Total;

        /// <summary>讀檔／解析層的警告。⛔ 空集合不代表沒事，它代表「這次沒有讀不動的檔」。</summary>
        public List<string> Warnings = new List<string>();
    }

    public static class SCP_TavernRead
    {
        // ===========================================================
        // 區塊職責：一個訊息檔 → `SCP_TavernMessage`
        // 🩸 為什麼不是新模型：2026-09-18 我先造了一支 `SCP_TavernMessage`，
        //    而 `SCP_TavernRegion.cs:49` **早就有同名同義的那個**（跨區讀那條路在用）。
        //    ⇒ 撞名，編譯器當場擋下（CS0101）。⛔ 而我沒有先搜 Senate 這一側，只搜了 Editor 那側。
        //    📌 今天第三次差點造一個已經存在的東西，而這次擋住我的是**編譯器**。
        //    ⇒ 處置：用既有模型，只給它補一個 `Meta`（擴充既有資產，不是開第二個）。
        // ⚠ `Seq` 來自**檔名**（migration 之後 seq == 檔名），⛔ 別去 json 裡找它 ——
        //    找不到而回 0 跟「這則真的是第 0 則」在下游長得一樣。
        // ===========================================================
        static SCP_TavernMessage? TryLoadFile(string iFile, string iRoom, List<string>? oWarn)
        {
            string aName = Path.GetFileNameWithoutExtension(iFile);
            if (!int.TryParse(aName, System.Globalization.NumberStyles.None,
                              System.Globalization.CultureInfo.InvariantCulture, out int aSeq))
            {
                if (oWarn != null) oWarn.Add("⚠ 檔名不是 8 位 seq，本筆計入未收錄：" + Path.GetFileName(iFile));
                return null;
            }
            try
            {
                SCP.Core.Json.SCP_JsonData aJson = SCP.Core.Json.SCP_JsonParser.Parse(File.ReadAllText(iFile));
                var aMsg = new SCP_TavernMessage
                {
                    Room = iRoom,
                    Seq = aSeq,
                    Uuid = aJson.GetString("uuid", ""),
                    Ts = aJson.GetString("ts", ""),
                    SenderId = aJson.GetString("sender_id", ""),
                    SenderName = aJson.GetString("sender_name", ""),
                    SenderPersona = aJson.GetString("sender_persona", ""),
                    Kind = aJson.GetString("kind", ""),
                    Body = aJson.GetString("body", ""),
                    Path = iFile.Replace(BackSlash, '/'),
                };
                if (aJson.Contains("meta"))
                {
                    SCP.Core.Json.SCP_JsonData aMeta = aJson["meta"];
                    foreach (string aK in aMeta.Keys) aMsg.Meta[aK] = aMeta.GetString(aK, "");
                }
                // ⚠ TASK-0247：`reply_to` / `refs` 是**選填欄位**（多數訊息沒有）。
                //   ⛔ 沒有 ≠ 0／空 —— `ReplyTo` 刻意用 nullable，理由見模型那邊的註解。
                if (aJson.Contains("reply_to")) aMsg.ReplyTo = aJson.GetInt("reply_to", 0);
                if (aJson.Contains("refs"))
                {
                    SCP.Core.Json.SCP_JsonData aRefs = aJson["refs"];
                    for (int i = 0; i < aRefs.Count; ++i)
                    {
                        SCP.Core.Json.SCP_JsonData aRef = aRefs[i];
                        aMsg.Refs.Add(new SCP_TavernRef
                        {
                            Path = aRef.GetString("path", ""),
                            Label = aRef.GetString("label", ""),
                            Anchor = aRef.GetString("anchor", ""),
                        });
                    }
                }
                return aMsg;
            }
            catch (Exception e)
            {
                // ⛔ **不靜默跳過**（形狀取自 `SCP_WatchExport.IterMessages`）——
                //    呼叫端要看得到「這次有幾筆沒收錄」。
                if (oWarn != null)
                    oWarn.Add("⚠ 讀不動 " + Path.GetFileName(iFile) + "：" + e.Message
                              + "（本筆計入『未收錄』）");
                return null;
            }
        }

        static List<SCP_TavernMessage> LoadFiles(IReadOnlyList<string> iFiles, string iRoom, List<string>? oWarn)
        {
            var aOut = new List<SCP_TavernMessage>(iFiles.Count);
            foreach (string f in iFiles)
            {
                SCP_TavernMessage? aMsg = TryLoadFile(f, iRoom, oWarn);
                if (aMsg != null) aOut.Add(aMsg);
            }
            return aOut;
        }

        /// <summary>列出所有房間 id（有 `messages/` 目錄的才算）。</summary>
        public static List<string> EnumerateRoomIds(string iDataRoot)
        {
            var aOut = new List<string>();
            string aRoot = SCP_TavernMsgIndex.RoomsRoot(iDataRoot);
            if (!Directory.Exists(aRoot)) return aOut;
            foreach (string aDir in Directory.GetDirectories(aRoot))
                if (Directory.Exists(Path.Combine(aDir, "messages")))
                    aOut.Add(Path.GetFileName(aDir));
            aOut.Sort(StringComparer.Ordinal);
            return aOut;
        }

        /// <summary>
        /// 只「數」某房的訊息檔數 —— ⛔ 不 parse 內容。
        /// ⚠ 索引可用時連列舉都不必（O(天數)）；不可用才列舉路徑（仍不讀內容）。
        /// </summary>
        public static int CountMessages(string iDataRoot, string iRoom, SCP_TavernReadStat? oStat = null)
        {
            string[]? aPaths = SCP_TavernMsgIndex.TryGetOrderedPaths(
                iDataRoot, iRoom, out bool aUsed, out int aStale);
            if (aPaths != null)
            {
                Fill(oStat, aUsed, aStale, false, aPaths.Length);
                return aPaths.Length;
            }
            string[] aAll = FullScan(iDataRoot, iRoom);
            Fill(oStat, false, 0, true, aAll.Length);
            return aAll.Length;
        }

        /// <summary>某房最後 N 則（seq 升冪）。</summary>
        public static List<SCP_TavernMessage> Tail(string iDataRoot, string iRoom, int iCount,
                                                   SCP_TavernReadStat? oStat = null)
        {
            if (iCount <= 0) return new List<SCP_TavernMessage>();
            string[]? aPaths = SCP_TavernMsgIndex.TryGetTailPaths(
                iDataRoot, iRoom, iCount, out int aTotal, out int aStale);
            if (aPaths != null)
            {
                var aStat = Fill(oStat, true, aStale, false, aTotal);
                return LoadFiles(aPaths, iRoom, aStat?.Warnings);
            }
            // ── 退化路徑：全量列舉再切尾巴 ────────────────────────────────
            string[] aAll = FullScan(iDataRoot, iRoom);
            var aStat2 = Fill(oStat, false, 0, true, aAll.Length);
            int aFrom = Math.Max(0, aAll.Length - iCount);
            var aSlice = new List<string>(aAll.Length - aFrom);
            for (int i = aFrom; i < aAll.Length; i++) aSlice.Add(aAll[i]);
            return LoadFiles(aSlice, iRoom, aStat2?.Warnings);
        }

        /// <summary>
        /// seq 落在 [iFrom, iTo]（**含端點**）的那一段。
        /// ⭐ 本側走索引直接定址；Editor 側的 `Range` 至今仍是 `LoadAllMessages` 全房載入再過濾。
        /// ⚠ 空區間回**空清單**，⛔ 不是錯誤 —— 「這一段沒有訊息」與「查不了」在這裡不同形。
        /// </summary>
        public static List<SCP_TavernMessage> Range(string iDataRoot, string iRoom, int iFrom, int iTo,
                                                    SCP_TavernReadStat? oStat = null)
        {
            string[]? aPaths = SCP_TavernMsgIndex.TryGetRangePaths(
                iDataRoot, iRoom, iFrom, iTo, out int aTotal, out int aStale);
            if (aPaths != null)
            {
                var aStat = Fill(oStat, true, aStale, false, aTotal);
                return LoadFiles(aPaths, iRoom, aStat?.Warnings);
            }
            string[] aAll = FullScan(iDataRoot, iRoom);
            var aStat2 = Fill(oStat, false, 0, true, aAll.Length);
            var aSlice = new List<string>();
            foreach (string f in aAll)
            {
                string aName = Path.GetFileNameWithoutExtension(f);
                if (int.TryParse(aName, out int aSeq) && aSeq >= iFrom && aSeq <= iTo) aSlice.Add(f);
            }
            return LoadFiles(aSlice, iRoom, aStat2?.Warnings);
        }

        // ===========================================================
        // 退化路徑 —— ⚠ 規則必須與 `SCP_TavernMsgIndex.Verify` 的「真值」那一側**逐字相同**，
        //            否則兩條路會在不同的排序下給出不同答案，而 verify 會說它們一致。
        // ===========================================================
        static string[] FullScan(string iDataRoot, string iRoom)
        {
            string aMsgRoot = SCP_TavernMsgIndex.MessagesDir(iDataRoot, iRoom);
            if (!Directory.Exists(aMsgRoot)) return Array.Empty<string>();
            string[] aFiles = Directory.GetFiles(aMsgRoot, "*.json", SearchOption.AllDirectories);
            var aKeys = new string[aFiles.Length];
            for (int i = 0; i < aFiles.Length; i++)
                aKeys[i] = aFiles[i].Substring(aMsgRoot.Length).Replace(BackSlash, '/');
            Array.Sort(aKeys, aFiles, StringComparer.Ordinal);
            return aFiles;
        }

        static SCP_TavernReadStat? Fill(SCP_TavernReadStat? ioStat, bool iUsedIndex, int iStaleDays,
                                        bool iFellBack, int iTotal)
        {
            if (ioStat == null) return null;
            ioStat.UsedIndex = iUsedIndex;
            ioStat.StaleDays = iStaleDays;
            ioStat.FellBackToFullScan = iFellBack;
            ioStat.Total = iTotal;
            if (iFellBack)
                ioStat.Warnings.Add("🔻 索引這條路走不了 ⇒ 本次**退回全量列舉**（答案一樣，成本是整房）"
                                    + " —— 修它：`senate cmd tavern-index --arg op=rebuild`");
            else if (iStaleDays > 0)
                ioStat.Warnings.Add($"⚠ 索引落後 **{iStaleDays}** 天（那幾天是現場列舉的）"
                                    + " —— ⛔ 本側刻意不自動補寫（驗收⑥：純讀不寫）；修它：`op=rebuild`");
            return ioStat;
        }

        /// <summary>反斜線字元。⛔ 用數值碼不用字面，理由見 <see cref="SCP_TavernMsgIndex"/>。</summary>
        const char BackSlash = (char)92;
    }
}
