// 區塊職責：訊息檔清單的**落盤索引**（Senate 側）— 讓**每一次** CLI 呼叫都不必列舉整房的訊息檔。
// 物理意義：檔名 migration 之後 `seq == 檔名`，而每個日期目錄裝的是一段**連續**的 seq。
//           於是「排序後的完整路徑清單」可以由一張每日範圍表**算出來**，不必列舉：
//               路徑 = <messages>/<date>/<seq:00000000>.json
//           索引一天一行 ⇒ 大小跟**天數**成正比，不是跟訊息數成正比。
// 數值影響：純加速層。任何一致性檢查不過就退回全量列舉（慢但正確），**永不給錯清單**。
//
// 🩸 為什麼 Senate 這側**更需要**它，而不只是「跟 Editor 對齊」（summit 2026-09-18 量）：
//   Editor 那側的快有一半靠 `GetSortedMessageFiles` 的 static 記憶體快取攤提
//   （`LoadMessagesAfterSeq` 檔頭逐字：「即使 domain reload 後 cache 冷，第一 tick 也只 parse
//   自游標起的新檔」）。而 **Senate CLI 是短命 process —— 跑完就退，沒有任何 cache 可以攤提**，
//   ⇒ 每一次呼叫都是冷的。所以在這一側索引**不是優化，是必需品**。
//   讀數（2026-09-18，LY 這棵樹）：全庫訊息檔 20,022／房間 52／tavern 日期目錄 91。
//   ⚠ 同一天我自己用 `find` 去數那些檔案，工具**逾時 >120 秒** —— 那不是論證，是被擋了一次。
//
// 一致性怎麼保證（形狀原樣取自 Editor 側 `UCL_ChatTavernMessageIndex`，TASK-0162）：
//   索引記每個日期目錄的 (起始 seq, 檔數, 目錄 mtime)。載入時**只 stat 目錄**：
//     · mtime 相符 → 該日內容沒動過 ⇒ 直接算出路徑，不列舉
//     · mtime 不符 / 不在索引 → **只列舉那一天**
//   最後再驗一次全域 seq 連續性；有斷點就整份丟掉走全量列舉。
//   > **把快取失效降級成「變慢」，而不是「算錯」** —— 算錯的後果很具體：
//   > 清單少一筆 → seq 全體位移 → 所有游標指到錯的訊息，而外觀完全正常。
//
// ⛔ 本檔是**照抄**，不是新設計。Editor 側 2026-09-08（TASK-0162）就做完了這件事，
//   連降級語意（「走不了就回 null，退回全量列舉」）都已經寫在那邊。
//   summit 2026-09-18 差一步就重新設計一個已經存在的東西，擋下它的是自己寫在 TASK-0240 上的
//   一行「動工第一件事是讀 Range，⛔ 不照名字猜」。
//
// ⚠ 索引放**房間目錄**而不是 messages/ 底下：寫在 messages/ 內會改動它的 mtime，
//   而那正是判斷「有沒有變」的依據 —— 每寫一次索引就讓自己失效一次。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SCP.Core.Tavern
{
    /// <summary>
    /// 每日 seq 範圍索引。⛔ 任何不一致一律回 <c>null</c>，呼叫端退回全量列舉。
    /// </summary>
    public static class SCP_TavernMsgIndex
    {
        public const string IndexFileName = "_msgindex.txt";

        /// <summary>索引檔頭。⚠ 與 Editor 側同字串 —— 兩邊讀同一份檔，版本字不能分岔。</summary>
        const string Header = "ucl_msgindex_v1";

        /// <summary>新格式檔名：8 位補零 seq。字典序 == 數值序。</summary>
        const string SeqFormat = "00000000";

        /// <summary>反斜線字元。⛔ 刻意用 <c>(char)92</c> 而不是字面 ——
        /// 本檔是經 shell heredoc 與 python 兩層搬過來的，而**那兩層都會解釋轉義**：
        /// heredoc 把字面吃掉一層（15 個編譯錯誤），改用轉義碼之後 python 又把它解釋成那個字元本身（3 個）。
        /// ⇒ 同一個字元咬兩次 ⇒ 用數值碼讓它**不經過任何一層的手**（summit 2026-09-18）。</summary>
        const char BackSlash = (char)92;
        /// <summary>
        /// 警告出口。**預設印 stderr，⛔ 不是 no-op** ——
        /// 靜默降級會讓「索引壞了」變成永遠沒人發現的慢，而那正是本專案反覆付錢的那一族。
        /// 呼叫端要收進回傳檔就把它換掉。
        /// </summary>
        public static Action<string> Warn = s => Console.Error.WriteLine(s);

        // ===========================================================
        // 路徑 —— ⛔ 不重造一套：messages 目錄的算法只有 SCP_WatchExport 那一份
        // ===========================================================
        /// <summary><c>&lt;dataRoot&gt;/ChatTavern/rooms/&lt;room&gt;/messages</c>（委派既有解析器）。</summary>
        public static string MessagesDir(string iDataRoot, string iRoom)
            => SCP.Core.Watch.SCP_WatchExport.MessagesDir(iDataRoot, iRoom);

        /// <summary>房間目錄 ＝ messages 的上一層。索引落在這裡（⛔ 不落在 messages/ 內，見檔頭）。</summary>
        public static string RoomDir(string iDataRoot, string iRoom)
        {
            string aMsg = MessagesDir(iDataRoot, iRoom);
            string? aParent = Path.GetDirectoryName(aMsg);
            return (aParent ?? aMsg).Replace(BackSlash, '/');
        }

        /// <summary><c>&lt;dataRoot&gt;/ChatTavern/rooms</c> —— 逐房掃描用。</summary>
        public static string RoomsRoot(string iDataRoot)
            => Path.Combine(iDataRoot, "ChatTavern", "rooms").Replace(BackSlash, '/');

        /// <summary>索引檔的完整路徑（已 normalize 成 /）。診斷面要印它 ⇒ 曝露出去，⛔ 別讓呼叫端自己 Combine 一份。</summary>
        public static string IndexPath(string iDataRoot, string iRoom)
            => Path.Combine(RoomDir(iDataRoot, iRoom), IndexFileName).Replace(BackSlash, '/');

        sealed class DayEntry
        {
            public string Date = "";    // yyyy-MM-dd（＝目錄名）
            public int FirstSeq;        // 該日第一筆的 seq（1-based）；Count==0 時無意義
            public int Count;
            public long MtimeTicks;     // 目錄的 LastWriteTimeUtc.Ticks —— 「有沒有變」的判準
        }

        // ===========================================================
        // 讀 / 寫
        // ===========================================================
        static Dictionary<string, DayEntry>? Load(string iDataRoot, string iRoom)
        {
            string aPath = IndexPath(iDataRoot, iRoom);
            if (!File.Exists(aPath)) return null;
            try
            {
                string[] aLines = File.ReadAllLines(aPath, Encoding.UTF8);
                if (aLines.Length == 0 || aLines[0] != Header) return null;   // 版本不合 → 當沒有
                var aMap = new Dictionary<string, DayEntry>(StringComparer.Ordinal);
                for (int i = 1; i < aLines.Length; i++)
                {
                    if (string.IsNullOrWhiteSpace(aLines[i])) continue;
                    string[] p = aLines[i].Split('\t');
                    if (p.Length != 4) return null;                           // 壞行 → 整份不信
                    aMap[p[0]] = new DayEntry
                    {
                        Date = p[0],
                        FirstSeq = int.Parse(p[1], CultureInfo.InvariantCulture),
                        Count = int.Parse(p[2], CultureInfo.InvariantCulture),
                        MtimeTicks = long.Parse(p[3], CultureInfo.InvariantCulture),
                    };
                }
                return aMap;
            }
            catch (Exception e)
            {
                // 壞索引**不可當成「沒有索引」以外的任何東西** —— 一律回 null 走全量列舉。
                // 出聲是必要的：靜默降級會讓「索引壞了」變成永遠沒人發現的慢。
                Warn($"[TavernMsgIndex] 索引解析失敗（{iRoom}），本次走全量列舉：{e.Message}");
                return null;
            }
        }

        static void Save(string iDataRoot, string iRoom, List<DayEntry> iDays)
        {
            try
            {
                var aSb = new StringBuilder().Append(Header).Append('\n');
                foreach (var d in iDays)
                    aSb.Append(d.Date).Append('\t').Append(d.FirstSeq).Append('\t')
                       .Append(d.Count).Append('\t').Append(d.MtimeTicks).Append('\n');
                string aPath = IndexPath(iDataRoot, iRoom);
                string? aDir = Path.GetDirectoryName(aPath);
                if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
                File.WriteAllText(aPath, aSb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                // 寫不出來只是「下次還是慢」，不影響正確性 —— 但仍要出聲，
                // 否則會出現「明明做了索引卻永遠沒生效」而沒人知道。
                Warn($"[TavernMsgIndex] 索引寫入失敗（{iRoom}）：{e.Message}");
            }
        }

        /// <summary>刪掉某房的索引（下次讀取自動以全量列舉重建）。</summary>
        public static void Delete(string iDataRoot, string iRoom)
        {
            try
            {
                string aPath = IndexPath(iDataRoot, iRoom);
                if (File.Exists(aPath)) File.Delete(aPath);
            }
            catch (Exception e) { Warn($"[TavernMsgIndex] 索引刪除失敗（{iRoom}）：{e.Message}"); }
        }

        // ===========================================================
        // 主入口
        // ===========================================================
        /// <summary>
        /// 取該房排序後的**完整**訊息檔路徑清單，能用索引就不列舉。
        /// 邊界：任何不一致 → 回 <c>null</c>，呼叫端退回全量列舉（本檔絕不回「可能錯的清單」）。
        /// </summary>
        public static string[]? TryGetOrderedPaths(string iDataRoot, string iRoom, out bool oUsedIndex)
            => TryGetOrderedPaths(iDataRoot, iRoom, out oUsedIndex, out _);

        /// <summary>
        /// 同上，外加回報**這次有幾天是現場列舉的**（不在索引裡／目錄動過）。
        /// 物理意義：這個數字就是「索引還缺幾天」。
        ///   🩸 2026-08-19 Editor 側實測：索引停在 08-06 而資料到 08-19，落後 10 天。
        ///     成因不是寫壞，是**成功就 early return，而重建只掛在全量列舉那條路的尾巴**
        ///     ⇒ 索引一旦存在就再也不會被擴充。⇒ 所以下面那幾支「缺天就補寫回去」。
        /// </summary>
        public static string[]? TryGetOrderedPaths(string iDataRoot, string iRoom,
            out bool oUsedIndex, out int oEnumeratedDays)
        {
            var aSpans = TryGetValidatedSpans(iDataRoot, iRoom, out oUsedIndex, out oEnumeratedDays, out int aTotal);
            if (aSpans == null) return null;
            var aResult = new List<string>(aTotal);
            foreach (var sp in aSpans) sp.AppendRange(aResult, sp.FirstSeq, sp.FirstSeq + sp.Count - 1);
            return aResult.ToArray();
        }

        // ===========================================================
        // 區塊職責：`Tail(n)`／`Range(from,to)`／「游標之後」的**直接定址**入口
        // 物理意義：有了連號 seq ＋ 每日範圍表，任何一段的檔名都是**算得出來**的。
        // 數值影響：`tail=6` 由 O(訊息數) 降為 O(天數) 的 stat ＋ 6 個字串。
        // 邊界：回 null ＝ 這條路走不了（呼叫端退回全量列舉），**不是**「沒有訊息」。
        // ⛔ **本側刻意不自動補寫索引**（與 Editor 側的取捨相反，理由是宿主不同）：
        //   · Editor 是常駐 process，而**索引現在的維護者就是那一側的讀取端**
        //     （`UCL_ChatTavernIO_PerMsgFile.cs:429`：`enumeratedDays > 0` 時順手 Rebuild）
        //     ⇒ Editor 開著時索引一直新鮮，補寫成本攤提得掉。
        //   · Senate CLI 是**短命 process**：每次呼叫都可能「缺幾天」⇒ 自動補寫等於**每次讀都寫檔**；
        //     而 TASK-0240 驗收⑥要求這條路**一個位元組都不寫進 `ChatTavern/`**。
        //   ⇒ 本側把落後**回報出來**（`oStaleDays` ＝ 這次現場列舉了幾天），
        //     修它走顯式入口 `senate cmd tavern-index --arg op=rebuild`。
        //   📌 判準：**讓落後看得見**比讓它自動消失好 —— 自動補寫會讓「Editor 關了很久」
        //     這件事沒有任何痕跡，而那正是本專案反覆付錢的那一族。
        //   ⚠ 代價要講明：沒有人跑 rebuild 的話索引會一直落後，而落後的樣子是「變慢」不是「算錯」。
        // ⭐ `TryGetRangePaths` 是本側**比 Editor 多出來的一支** —— 那邊的 `Range` 至今仍走
        //   `LoadAllMessages`（全房載入再過濾）。⛔ 而它不改任何行為，只改成本。
        // ===========================================================
        public static string[]? TryGetTailPaths(string iDataRoot, string iRoom, int iCount,
            out int oTotal, out int oStaleDays)
        {
            oTotal = 0;
            oStaleDays = 0;
            if (iCount <= 0) return null;
            var aSpans = TryGetValidatedSpans(iDataRoot, iRoom, out _, out oStaleDays, out oTotal);
            if (aSpans == null) return null;
            return SliceSpans(aSpans, oTotal - iCount + 1, oTotal, oTotal);
        }

        /// <summary>回傳 seq 落在 [iFrom, iTo]（**含端點**）的那一段路徑。</summary>
        public static string[]? TryGetRangePaths(string iDataRoot, string iRoom, int iFrom, int iTo,
            out int oTotal, out int oStaleDays)
        {
            oTotal = 0;
            oStaleDays = 0;
            var aSpans = TryGetValidatedSpans(iDataRoot, iRoom, out _, out oStaleDays, out oTotal);
            if (aSpans == null) return null;
            return SliceSpans(aSpans, iFrom, iTo, oTotal);
        }

        /// <summary>回傳「絕對序位 &gt; iAfterSeq 的那一段」路徑（序位 1-based ＝ seq）。</summary>
        public static string[]? TryGetPathsAfter(string iDataRoot, string iRoom, int iAfterSeq,
            out int oTotal, out int oStaleDays)
        {
            oTotal = 0;
            oStaleDays = 0;
            var aSpans = TryGetValidatedSpans(iDataRoot, iRoom, out _, out oStaleDays, out oTotal);
            if (aSpans == null) return null;
            return SliceSpans(aSpans, iAfterSeq + 1, oTotal, oTotal);
        }

        static string[] SliceSpans(List<DaySpan> iSpans, int iFromSeq, int iToSeq, int iTotal)
        {
            int aFrom = Math.Max(1, iFromSeq);
            int aTo = Math.Min(iTotal, iToSeq);
            if (aTo < aFrom) return Array.Empty<string>();
            var aResult = new List<string>(aTo - aFrom + 1);
            foreach (var sp in iSpans)
            {
                int aLo = Math.Max(aFrom, sp.FirstSeq);
                int aHi = Math.Min(aTo, sp.FirstSeq + sp.Count - 1);
                if (aHi < aLo) continue;      // 這一天整段落在區間外 ⇒ 一個字串都不建
                sp.AppendRange(aResult, aLo, aHi);
            }
            return aResult.ToArray();
        }


        // ===========================================================
        // 區塊職責：索引的**唯一**一份一致性驗證 —— 產出「每日 seq 區段」清單
        // 物理意義：一天一個 span（目錄 ＋ 起始 seq ＋ 筆數）。乾淨的那幾天路徑用算的，
        //           動過的那幾天現場列舉並把實際路徑帶在身上。
        // 邊界：任何一格對不上（跨日不連續 / 有洞 / 重號 / 舊格式檔名 / IO 失敗）一律回 null
        //       ⇒ 呼叫端退回全量列舉。**把失效降級成「變慢」，不是「算錯」**（見檔頭）。
        // ===========================================================
        sealed class DaySpan
        {
            public string Dir = "";
            public int FirstSeq;
            public int Count;
            public string[]? Files;   // 非 null ＝ 那天是現場列舉的，路徑照實帶（不重算）

            /// <summary>把 [iLo, iHi]（含端點、絕對 seq）這一段的路徑追加進去。</summary>
            public void AppendRange(List<string> ioTo, int iLo, int iHi)
            {
                for (int aSeq = iLo; aSeq <= iHi; aSeq++)
                {
                    int aOff = aSeq - FirstSeq;
                    if (Files != null) { ioTo.Add(Files[aOff]); continue; }
                    string aName = aSeq.ToString(SeqFormat, CultureInfo.InvariantCulture) + ".json";
                    ioTo.Add(Path.Combine(Dir, aName).Replace('\\', '/'));
                }
            }
        }

        static List<DaySpan>? TryGetValidatedSpans(string iDataRoot, string iRoom,
            out bool oUsedIndex, out int oEnumeratedDays, out int oTotal)
        {
            oUsedIndex = false;
            oEnumeratedDays = 0;
            oTotal = 0;

            var aIdx = Load(iDataRoot, iRoom);
            if (aIdx == null) return null;

            string aMsgRoot = MessagesDir(iDataRoot, iRoom);
            string[] aDirs;
            try { aDirs = Directory.GetDirectories(aMsgRoot); }
            catch { return null; }
            Array.Sort(aDirs, StringComparer.Ordinal);

            var aSpans = new List<DaySpan>(aDirs.Length);
            int aExpectedNextSeq = 1;
            bool aAnyFromIndex = false;

            foreach (string aDir in aDirs)
            {
                string aDate = Path.GetFileName(aDir);
                long aMtime;
                try { aMtime = Directory.GetLastWriteTimeUtc(aDir).Ticks; }
                catch { return null; }

                if (aIdx.TryGetValue(aDate, out var e) && e.MtimeTicks == aMtime)
                {
                    // 目錄沒動過 ⇒ 內容不變 ⇒ 只記區段，**不列舉也不建路徑**（本設計的收益就在這裡）
                    if (e.Count == 0) continue;                        // 空目錄
                    if (e.FirstSeq != aExpectedNextSeq) return null;   // 跨日不連續 → 整份不信
                    aSpans.Add(new DaySpan { Dir = aDir, FirstSeq = e.FirstSeq, Count = e.Count });
                    aExpectedNextSeq = e.FirstSeq + e.Count;
                    aAnyFromIndex = true;
                }
                else
                {
                    // 只列舉「動過的那一天」。索引的價值不是全有全無，
                    // 而是把成本從「全部訊息」壓到「今天的訊息」。
                    oEnumeratedDays++;
                    string[] aFiles;
                    try { aFiles = Directory.GetFiles(aDir, "*.json"); }
                    catch { return null; }
                    Array.Sort(aFiles, StringComparer.Ordinal);
                    if (aFiles.Length == 0) continue;
                    int aFirst = aExpectedNextSeq;
                    foreach (string f in aFiles)
                    {
                        if (!TryParseSeq(Path.GetFileName(f), out int aSeq)) return null;  // 還有舊格式 → 不用索引
                        if (aSeq != aExpectedNextSeq) return null;                         // 有洞 / 重號
                        aExpectedNextSeq++;
                    }
                    aSpans.Add(new DaySpan { Dir = aDir, FirstSeq = aFirst, Count = aFiles.Length, Files = aFiles });
                }
            }

            oUsedIndex = aAnyFromIndex;
            oTotal = aExpectedNextSeq - 1;
            return aSpans;
        }

        static bool TryParseSeq(string iFileName, out int oSeq)
        {
            oSeq = 0;
            if (iFileName.Length != 13 || !iFileName.EndsWith(".json", StringComparison.Ordinal)) return false;
            return int.TryParse(iFileName.Substring(0, 8), NumberStyles.None,
                                CultureInfo.InvariantCulture, out oSeq) && oSeq > 0;
        }

        /// <summary>
        /// 區塊職責：由**已經排序好的完整清單**重建索引並落盤。
        /// 物理意義：呼叫端（全量列舉那條路）算完之後順手存一份，下次就不必再算。
        /// 邊界：清單裡只要有一個檔名不是新格式，就**不建索引**（舊格式房不適用本機制）。
        /// </summary>
        public static void Rebuild(string iDataRoot, string iRoom, string[] iOrderedPaths)
        {
            try
            {
                string aMsgRoot = MessagesDir(iDataRoot, iRoom);
                var aByDate = new List<DayEntry>();
                var aSeen = new Dictionary<string, DayEntry>(StringComparer.Ordinal);
                for (int i = 0; i < iOrderedPaths.Length; i++)
                {
                    string aName = Path.GetFileName(iOrderedPaths[i]);
                    if (!TryParseSeq(aName, out int aSeq) || aSeq != i + 1) return;   // 尚未 migrate → 放棄
                    string? aDirName = Path.GetDirectoryName(iOrderedPaths[i]);
                    string aDate = Path.GetFileName(aDirName ?? "");
                    if (!aSeen.TryGetValue(aDate, out var e))
                    {
                        e = new DayEntry { Date = aDate, FirstSeq = aSeq, Count = 0 };
                        aSeen[aDate] = e; aByDate.Add(e);
                    }
                    e.Count++;
                }
                // 空目錄也要入索引 —— 否則下次它會被當成「不在索引」而被列舉，
                // 而列舉一個空目錄雖然便宜，卻會讓「有沒有命中索引」這個診斷訊號變髒。
                foreach (string aDir in Directory.GetDirectories(aMsgRoot))
                {
                    string aDate = Path.GetFileName(aDir);
                    if (!aSeen.ContainsKey(aDate))
                    {
                        var e = new DayEntry { Date = aDate, FirstSeq = 0, Count = 0 };
                        aSeen[aDate] = e; aByDate.Add(e);
                    }
                }
                foreach (var e in aByDate)
                    e.MtimeTicks = Directory.GetLastWriteTimeUtc(Path.Combine(aMsgRoot, e.Date)).Ticks;
                aByDate.Sort((a, b) => string.CompareOrdinal(a.Date, b.Date));
                Save(iDataRoot, iRoom, aByDate);
            }
            catch (Exception e)
            {
                Warn($"[TavernMsgIndex] 索引重建失敗（{iRoom}）：{e.Message}");
            }
        }

        /// <summary>
        /// 區塊職責：驗證「索引算出來的清單」與「全量列舉算出來的清單」**逐筆**相同。
        /// 物理意義：索引是加速層，而加速層唯一該被問的問題是**它有沒有改變答案**。
        ///           這裡把兩條路各跑一次直接對撞 —— 不是抽樣、不是看數量，是逐筆比路徑。
        /// 數值影響：純讀，兩邊都不寫索引。慢（等於付一次全量），所以是顯式觸發不是自動。
        /// </summary>
        public static string Verify(string iDataRoot)
        {
            var aSb = new StringBuilder();
            string aRoomsRoot = RoomsRoot(iDataRoot);
            if (!Directory.Exists(aRoomsRoot)) return "找不到 rooms 目錄：" + aRoomsRoot;
            int aRooms = 0, aWithIndex = 0, aMismatch = 0, aNoIndex = 0;
            long aTotalFiles = 0;

            foreach (string aRoomDir in Directory.GetDirectories(aRoomsRoot))
            {
                string aRoom = Path.GetFileName(aRoomDir);
                string aRoot = Path.Combine(aRoomDir, "messages");
                if (!Directory.Exists(aRoot)) continue;
                aRooms++;

                // ① 全量列舉（真值）—— 與 fallback 路徑同一套規則
                string[] aTruth = Directory.GetFiles(aRoot, "*.json", SearchOption.AllDirectories);
                var aKeys = new string[aTruth.Length];
                for (int i = 0; i < aTruth.Length; i++)
                    aKeys[i] = aTruth[i].Substring(aRoot.Length).Replace('\\', '/');
                Array.Sort(aKeys, aTruth, StringComparer.Ordinal);
                aTotalFiles += aTruth.Length;

                // ② 索引路徑
                string[]? aFromIndex = TryGetOrderedPaths(iDataRoot, aRoom, out _);
                if (aFromIndex == null) { aNoIndex++; continue; }
                aWithIndex++;

                if (aFromIndex.Length != aTruth.Length)
                {
                    aMismatch++;
                    aSb.AppendLine($"  ✗ [{aRoom}] 筆數不同：索引 {aFromIndex.Length} vs 實際 {aTruth.Length}");
                    continue;
                }
                for (int i = 0; i < aTruth.Length; i++)
                {
                    string aL = aFromIndex[i].Replace('\\', '/');
                    string aR = aTruth[i].Replace('\\', '/');
                    if (!string.Equals(aL, aR, StringComparison.OrdinalIgnoreCase))
                    {
                        aMismatch++;
                        aSb.AppendLine($"  ✗ [{aRoom}] seq {i + 1} 路徑不同");
                        aSb.AppendLine($"      索引 {aL}");
                        aSb.AppendLine($"      實際 {aR}");
                        break;
                    }
                }
            }

            var aHead = new StringBuilder();
            aHead.AppendLine($"房間 {aRooms} / 訊息檔 {aTotalFiles}");
            aHead.AppendLine($"  走索引 {aWithIndex} 房 / 無索引（走全量） {aNoIndex} 房");
            aHead.AppendLine(aMismatch == 0
                ? "  ✅ 索引與全量列舉**逐筆相同**（路徑逐一比對，非抽樣）"
                : $"  🚨 有 {aMismatch} 房不符 —— 索引不可信，請重建：");
            return aHead.ToString() + aSb.ToString();
        }
    }
}
