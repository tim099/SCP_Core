// 區塊職責：StreamWatch 兩本**台帳**的讀 / 回填 —— 實錄台帳（`sessions_log.jsonl`）與段台帳（`segments.jsonl`）。
// 物理意義：實錄台帳是 **append-only** —— 它是為了「開下一場就被覆寫的 start_seq/end_seq 不會丟」
//           而長出來的。⇒ 回填 `exported_chapter` **不就地改行**，而是 append 一筆
//           `record_type=export` 的修訂事件，讀取端取同一個 session_id 的**最後一筆**為準。
//           改寫既有行會破壞它唯一的保證。
// 數值影響：讀是純讀；`AppendExportEvents` 每場 append 一行（**永不覆寫**）。
//
// 🩸 BUG-9：`exported_chapter` 欄位存在、註解寫「匯出後由人/工具回填」，而**沒有任何一端回填** ——
//    於是「已匯出」與「還沒匯出」在台帳上同形（實測 5/5 全空，而 Books/ 底下已有 7 章）。
//    假陰性不會叫，下一個人讀到空值只會重複匯出。
//
// ⚠ **移植自 `library.py`（TASK-0143 ⑤）**，逐行對照 @summit 2026-09-06 `84617ee8` 落地的那一版。
//   移植不改行為 —— 這一層有兩個寫者（本層／Editor 的 `Cmd_StreamWatch.AppendSessionLog`），
//   而它們不互相蓋**只因為兩邊都只 append**。⛔ 哪天有人想「整理一下台帳」就會把這個前提拆掉。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Watch
{
    /// <summary>台帳的一列 —— **保留原始 JSON**，⛔ 不收斂成窄型別。</summary>
    /// <remarks>
    /// 🩸 wake 88 的血證：「讀成一個比它窄的東西 → 改幾格 → 寫回去」預設就是在吃資料，
    /// 而被吃掉的那部分**不會有任何一層替它喊**。本層只讀＋append，所以更沒有理由先窄化。
    /// </remarks>
    public sealed class SCP_WatchSessionRow
    {
        public SCP_WatchSessionRow(SCP_JsonData iRaw) { Raw = iRaw; }

        /// <summary>原始那一行（未經收斂）。</summary>
        public SCP_JsonData Raw { get; }

        public string Get(string iKey) => Raw.GetString(iKey, "");
        public long GetLong(string iKey) => Raw.GetLong(iKey, 0);

        // ── 以下四欄由 export 修訂事件覆寫（取最後一筆），其餘欄位一律讀 Raw ──
        /// <summary>這一場最後一次被匯進哪一章。**空字串＝台帳沒有這個讀數**，跟「章號是空的」同形，呼叫端要分。</summary>
        public string ExportedChapter = "";
        public string ExportedBook = "";
        /// <summary>這一章上次匯出時叫什麼（TASK-0142 延伸）。⚠ 舊事件沒有這個鍵 ⇒ 讀回 ""。</summary>
        public string ExportedTitle = "";
        public string ExportedWorkTitle = "";
    }

    public static class SCP_WatchLedger
    {
        /// <summary>修訂事件的 `record_type`。⚠ 這個字串是**跨端契約**（python 端同字面），改它＝改磁碟格式。</summary>
        public const string RecordTypeExport = "export";

        public static string SessionsLogPath(string iDataRoot)
            => Path.Combine(iDataRoot, "StreamWatch", "sessions_log.jsonl").Replace('\\', '/');

        public static string SegmentsLogPath(string iDataRoot)
            => Path.Combine(iDataRoot, "StreamWatch", "segments.jsonl").Replace('\\', '/');

        // ── 實錄台帳 ────────────────────────────────────────────────

        /// <summary>
        /// 讀出台帳的每一行（行號從 1 起）。**壞行不靜默跳過** —— 出聲並保留位置感。
        /// </summary>
        /// <param name="oWarnings">讀不動的行 ⇒ 一行一則說明。⛔ 不可以吞掉：
        /// 「這一行壞了」與「台帳沒有這一場」處置完全不同。</param>
        public static List<KeyValuePair<int, SCP_JsonData>> ReadSessionsLog(string iDataRoot, List<string> oWarnings)
        {
            var aOut = new List<KeyValuePair<int, SCP_JsonData>>();
            string aPath = SessionsLogPath(iDataRoot);
            if (!File.Exists(aPath)) return aOut;          // 沒有台帳是合法的（舊資料根）
            string[] aLines = File.ReadAllText(aPath, Encoding.UTF8).Replace("\r\n", "\n").Split('\n');
            for (int i = 0; i < aLines.Length; ++i)
            {
                string aLine = aLines[i].Trim();
                if (aLine.Length == 0) continue;
                try { aOut.Add(new KeyValuePair<int, SCP_JsonData>(i + 1, SCP_JsonData.Parse(aLine))); }
                catch (Exception e)
                {
                    oWarnings.Add($"⚠ sessions_log 第 {i + 1} 行讀不動：{e.GetType().Name}: {e.Message}"
                                  + "（略過該行，**不當成沒有場次**）");
                }
            }
            return aOut;
        }

        /// <summary>
        /// 收斂成 <c>{session_id → 場次列}</c>，其中 <c>ExportedChapter</c> 等四欄
        /// **以最後一筆 export 修訂事件為準**。
        /// <para>⚠ export 事件只在該 session 已經有場次列時才套用 —— 孤兒事件不會憑空造出一場
        /// （那會讓「有這場」與「有人替它記過一筆匯出」同形）。</para>
        /// </summary>
        public static Dictionary<string, SCP_WatchSessionRow> SessionsLogState(string iDataRoot, List<string> oWarnings)
        {
            var aState = new Dictionary<string, SCP_WatchSessionRow>(StringComparer.Ordinal);
            foreach (var kv in ReadSessionsLog(iDataRoot, oWarnings))
            {
                SCP_JsonData aRec = kv.Value;
                string aSid = aRec.GetString("session_id", "");
                if (aSid.Length == 0) continue;
                if (string.Equals(aRec.GetString("record_type", ""), RecordTypeExport, StringComparison.Ordinal))
                {
                    if (!aState.TryGetValue(aSid, out SCP_WatchSessionRow? aRow)) continue;
                    aRow.ExportedChapter = aRec.GetString("exported_chapter", "");
                    aRow.ExportedBook = aRec.GetString("book", "");
                    // 章名／作品名也以最後一筆 export 事件為準（TASK-0142 延伸）——
                    // ⚠ 舊事件沒有這兩個鍵 ⇒ 讀回 ""，而 "" 與「這一章真的沒有名字」同形。
                    //   呼叫端據此退回哨兵，那是**看得見的缺**，不是安靜的錯。
                    aRow.ExportedTitle = aRec.GetString("chapter_title", "");
                    aRow.ExportedWorkTitle = aRec.GetString("work_title", "");
                }
                else aState[aSid] = new SCP_WatchSessionRow(aRec);
            }
            return aState;
        }

        /// <summary>
        /// 把「這幾場進了哪一章」append 進台帳（BUG-9）。回傳**實際寫入**的 session id。
        /// <para>⚠ 也記 <c>chapter_title</c> / <c>work_title</c>（2026-09-06，TASK-0142 延伸）——
        /// 🩸 在此之前**章名沒有任何持久儲存**：它只活在 per-media 單槽的 <c>prepared/</c> 裡
        /// （下一話就被覆寫）與產物 <c>.txt</c> 裡（而那是機械產物，不能當真相源）。
        /// ⇒ 重出任何一個舊章都會掉章名。實撞兩次：`watch-sluha-narodu/001` 被寫成第 2 集的章名
        /// （**安靜的錯**）；`watch-apocalypse-hotel/010` 退成哨兵（**看得見的缺**）。
        /// 兩者是同一個洞的兩面 —— 修掉洞本身，不是選一個比較好看的失敗樣子。</para>
        /// <para>⛔ **只 append，永不改既有行** —— 那是這本台帳唯一的保證，也是它能有兩個寫者的原因。</para>
        /// </summary>
        public static List<string> AppendExportEvents(string iDataRoot, IEnumerable<string> iSessionIds,
                                                      string iChapter, string iBook,
                                                      string iChapterTitle, string iWorkTitle,
                                                      List<string> oWarnings, DateTime? iNowUtc = null)
        {
            // 去重且保序（python `dict.fromkeys` 的語意）—— 同一場寫兩筆會讓「重出過幾次」失真。
            var aSeen = new HashSet<string>(StringComparer.Ordinal);
            var aIds = new List<string>();
            foreach (string aSid in iSessionIds)
            {
                if (string.IsNullOrWhiteSpace(aSid)) continue;
                if (aSeen.Add(aSid)) aIds.Add(aSid);
            }
            if (aIds.Count == 0) return aIds;

            string aPath = SessionsLogPath(iDataRoot);
            if (!File.Exists(aPath))
            {
                // ⛔ 不自己造一本台帳：台帳的產生端是 Editor 的收工流程。
                //   這裡憑空 create 會讓「台帳不存在」變成「台帳只有匯出事件」——後者更難查。
                oWarnings.Add($"⚠ 找不到 sessions_log.jsonl —— 本次**未回填** exported_chapter（{aPath}）");
                return new List<string>();
            }

            string aTs = (iNowUtc ?? DateTime.UtcNow).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
            var aText = new StringBuilder();
            foreach (string aSid in aIds)
            {
                SCP_JsonData aRec = SCP_JsonData.NewObject();
                aRec["record_type"] = SCP_JsonData.NewString(RecordTypeExport);
                aRec["session_id"] = SCP_JsonData.NewString(aSid);
                aRec["exported_chapter"] = SCP_JsonData.NewString(iChapter ?? "");
                aRec["book"] = SCP_JsonData.NewString(iBook ?? "");
                aRec["chapter_title"] = SCP_JsonData.NewString(iChapterTitle ?? "");
                aRec["work_title"] = SCP_JsonData.NewString(iWorkTitle ?? "");
                aRec["exported_at"] = SCP_JsonData.NewString(aTs);
                // ⚠ 一行一筆（jsonl）⇒ **不縮排**。縮排會讓每一筆佔多行，而讀取端是逐行 parse 的。
                aText.Append(aRec.ToJson(iIndented: false)).Append('\n');
            }
            // ⚠ `AppendAllText` 而不是讀回來重寫 —— 這一層有兩個寫者（另一個是 Editor），
            //   而它們不互相蓋的唯一理由就是雙方都只 append。
            File.AppendAllText(aPath, aText.ToString(), new UTF8Encoding(false));
            return aIds;
        }

        // ── 段台帳（TASK-0060 的產物）—— 匯出**排序**的真相源 ──────────────
        // 物理意義：河道的 tavern seq 是「誰先按下送出」，段序是「這段素材在片子裡的先後」。
        //          書是實錄 ⇒ 它要的是後者。
        // ⛔ 不解析訊息本文、不以 message meta 為主（Tim 2026-08-26 拍板）：
        //    meta 是每則各自帶的，漏寫會長出「一則沒有段號的觀察」而看起來完全正常；
        //    台帳的缺漏會顯示成「這段沒有 seq」——**讀不到與沒有，在輸出上可分**。

        /// <summary>
        /// 回 <c>{tavern_seq → seg_index}</c>。台帳不存在 ⇒ 回空（**合法**：舊章沒有台帳）。
        /// <para>⚠ 「回空」與「讀不到」必須可分：讀檔失敗時出聲，⛔ 不靜默回空 ——
        /// 靜默回空會讓「沒有台帳」與「台帳壞了」同形，而前者是合法的舊章。</para>
        /// </summary>
        public static Dictionary<long, int> LoadSegmentOrder(string iDataRoot, List<string> oWarnings)
        {
            var aOut = new Dictionary<long, int>();
            string aPath = SegmentsLogPath(iDataRoot);
            if (!File.Exists(aPath)) return aOut;
            int aBad = 0;
            string[] aLines = File.ReadAllText(aPath, Encoding.UTF8).Replace("\r\n", "\n").Split('\n');
            foreach (string aRawLine in aLines)
            {
                string aLine = aRawLine.Trim();
                if (aLine.Length == 0) continue;
                SCP_JsonData aRec;
                try { aRec = SCP_JsonData.Parse(aLine); }
                catch { ++aBad; continue; }
                if (!string.Equals(aRec.GetString("record_type", ""), "observe", StringComparison.Ordinal)) continue;
                // ⚠ python 那側要求 seq 與 seg_index **都是 int** 才收；缺一個就整筆不收。
                //   照抄那個門檻：半筆資料進了排序鍵，錯的位置會比沒有段號更難查。
                if (!aRec.Contains("seq") || !aRec.Contains("seg_index")) continue;
                long aSeq = aRec.GetLong("seq", long.MinValue);
                int aIdx = aRec.GetInt("seg_index", int.MinValue);
                if (aSeq == long.MinValue || aIdx == int.MinValue) continue;
                aOut[aSeq] = aIdx;
            }
            if (aBad > 0)
                oWarnings.Add($"⚠ segments.jsonl 有 {aBad} 行讀不動（略過該行，**不當成沒有段台帳**）");
            return aOut;
        }

        /// <summary>一則被收進章裡的訊息 —— 排序只認 <see cref="Seq"/>，其餘由呼叫端持有。</summary>
        public interface ISeqItem { long Seq { get; } }

        /// <summary>
        /// 依**段序**重排（輸入是 tavern seq 昇冪）。回排序後清單，並回報有／無段號各幾則。
        /// <para>段序是實錄主序；tavern seq 是對話錨點 —— ⛔ **不把兩者假裝成同一條序**
        /// （@meadow 投票，TASK-0061）。</para>
        /// <para>有段號的照段號排；**無段號的（公告漏過濾／舊訊息／同場閒聊）不丟掉**，
        /// 穩定合併到「它前面最近一則有段號的那一段」之後，保持原本的 tavern seq 相對位置。
        /// ⛔ 不把無號的全塞章末 —— 那看起來乾淨，但會把對話拆散，而實錄的價值正是對話。</para>
        /// <para>排序鍵 <c>(anchor_seg, 0|1, seq)</c> 是**全序**（seq 在單一房間內唯一）
        /// ⇒ 同一份輸入永遠得到同一份輸出，可用位元組對拍驗證。</para>
        /// <para>⚠ 出現在**第一則有段號的訊息之前**的無號訊息，anchor 取 -1 ⇒ 排在最前，位置一樣穩定。</para>
        /// </summary>
        public static List<T> OrderBySegment<T>(IReadOnlyList<T> iKept, IReadOnlyDictionary<long, int> iSegOfSeq,
                                                out int oWithSeg, out int oWithoutSeg) where T : ISeqItem
        {
            var aKeyed = new List<(int Anchor, int HasNo, long Seq, T Item)>(iKept.Count);
            int aAnchor = -1;
            oWithSeg = 0;
            foreach (T aItem in iKept)
            {
                if (iSegOfSeq.TryGetValue(aItem.Seq, out int aIdx))
                {
                    aAnchor = aIdx;
                    ++oWithSeg;
                    aKeyed.Add((aIdx, 0, aItem.Seq, aItem));
                }
                else aKeyed.Add((aAnchor, 1, aItem.Seq, aItem));
            }
            aKeyed.Sort((x, y) =>
            {
                int c = x.Anchor.CompareTo(y.Anchor); if (c != 0) return c;
                c = x.HasNo.CompareTo(y.HasNo); if (c != 0) return c;
                return x.Seq.CompareTo(y.Seq);
            });
            oWithoutSeg = iKept.Count - oWithSeg;
            var aOut = new List<T>(aKeyed.Count);
            foreach (var t in aKeyed) aOut.Add(t.Item);
            return aOut;
        }
    }
}
