// 區塊職責：由 **session id** 反查「這一場屬於哪一章、要收哪幾段 seq、章名叫什麼」。
// 物理意義：**章 ≠ 場** —— 一章可能由主場＋陪同場（＋同章的其它場次）組成。
//           回傳的區間已含主場、其 companions、以及**已經匯進同一章**的舊場次
//           ⇒ 一話跨數場時是「併區間重匯」，不是另開一章、也不是靜默漏掉第二場。
// 數值影響：純讀（台帳 ＋ 準備檔），一個位元組都不寫。
//
// ⚠ **移植自 `library.py::_resolve_from_session`（TASK-0143 ⑤ 第二刀）**，
//   逐行對照 @summit 2026-09-06 `84617ee8` 落地的那一版 —— 移植不改行為，血證註解一條不刪。
//
// 🩸 本檔存在的理由（TASK-0142）：章號的真相源一度是 `prepared/<media_id>.json`，
//   而它是 per-media **單槽** ⇒ 下一話 prepare 會覆寫它。跨集之後拿它解舊場，
//   解出來的是**別集的章號**，而產物 `exit 0`、印匯出成功、**行數還變多**。
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
    /// <summary>一段 seq 區間（閉區間）。</summary>
    public readonly struct SCP_SeqRange
    {
        public SCP_SeqRange(long iLo, long iHi) { Lo = iLo; Hi = iHi; }
        public long Lo { get; }
        public long Hi { get; }
        public override string ToString() => Lo.ToString(CultureInfo.InvariantCulture) + "-"
                                             + Hi.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 反查結果。<see cref="Error"/> 非空 ＝ **沒有結果**（呼叫端不准把其餘欄位當成有效值）。
    /// <para>⛔ 刻意用「回一個帶錯誤的結果」而不是丟例外：兩種失敗（查不到場次／沒有可用區間）
    /// 的處置不同，而例外會讓呼叫端只看得到「失敗了」。</para>
    /// </summary>
    public sealed class SCP_WatchResolveResult
    {
        public string Error = "";
        public string Media = "";
        public string LibraryMediaId = "";
        public List<string> Sessions = new List<string>();
        public List<SCP_SeqRange> Ranges = new List<SCP_SeqRange>();
        /// <summary>採用的準備檔內容。**空物件＝刻意不採用**（幽靈檔／跨集），不是「檔不存在」。</summary>
        public SCP_JsonData Prepared = SCP_JsonData.NewObject();
        public string Chapter = "";
        /// <summary>台帳記過的章名。⚠ 哨兵值視同「沒有名字」⇒ 這裡回空字串。</summary>
        public string LedgerTitle = "";
        public string LedgerWorkTitle = "";
    }

    public static class SCP_WatchResolve
    {
        public static string PreparedPath(string iDataRoot, string iLibraryMediaId)
            => Path.Combine(iDataRoot, "StreamWatch", "prepared", iLibraryMediaId + ".json").Replace('\\', '/');

        /// <summary>三位數正規化；非數字原樣回（章號允許非數字）。</summary>
        static string NormChapter(string iChapter)
        {
            string a = (iChapter ?? "").Trim();
            if (a.Length == 0) return "";
            return long.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n)
                ? n.ToString("000", CultureInfo.InvariantCulture) : a;
        }

        static bool SameChapter(string iA, string iB)
        {
            if (string.Equals(iA, iB, StringComparison.Ordinal)) return true;
            return long.TryParse(iA, NumberStyles.Integer, CultureInfo.InvariantCulture, out long a)
                   && long.TryParse(iB, NumberStyles.Integer, CultureInfo.InvariantCulture, out long b)
                   && a == b;
        }

        /// <summary>
        /// 反查。<paramref name="iExplicitChapter"/> 給了就以它為準（人明示 &gt; 任何推導）。
        /// </summary>
        /// <param name="iUntitledMarker">章名哨兵值（由宿主從 `StreamWatch/settings.json` 供給，
        /// **本層不自己去讀設定** —— 同 `SCP_WakeBrief` 對 region 的契約）。</param>
        /// <param name="oLines">過程說明（不一致的兩個值、降級、警告）——⛔ 一句都不要吞。</param>
        public static SCP_WatchResolveResult FromSession(string iDataRoot, string iSessionId,
                                                         string? iExplicitChapter,
                                                         string iUntitledMarker,
                                                         List<string> oLines)
        {
            var aOut = new SCP_WatchResolveResult();
            Dictionary<string, SCP_WatchSessionRow> aState = SCP_WatchLedger.SessionsLogState(iDataRoot, oLines);
            // ⚠ 台帳的列舉一律照 Order（＝台帳出現序）—— 見 SCP_WatchSessionRow.Order 的血證：
            //   .NET 字典的列舉序不是契約，而 `sessions` 的順序會進章的表頭。
            var aOrdered = new List<KeyValuePair<string, SCP_WatchSessionRow>>(aState);
            aOrdered.Sort((x, y) => x.Value.Order.CompareTo(y.Value.Order));
            if (!aState.TryGetValue(iSessionId, out SCP_WatchSessionRow? aMe))
            {
                aOut.Error = $"❌ sessions_log 裡查不到場次 {iSessionId} —— **不猜區間**。";
                return aOut;
            }

            // ⚠ 錨定到主場（TASK-0060 家族）：本函式原本**假設傳進來的就是 primary**
            //   （只收 parent_session_id == session_id 的場次）。而「最後收工的人觸發匯出」之後，
            //   觸發者很可能是 companion ⇒ 沒有任何場次的 parent 指向他 ⇒ 只會匯出他自己那一段。
            //   🩸 徵狀會長得像「章匯出成功」（有檔、有行數），只是內容少了主場與其他陪看者。
            string aSid = iSessionId;
            string aParent = aMe.Get("parent_session_id");
            if (aParent.Length > 0)
            {
                if (aState.TryGetValue(aParent, out SCP_WatchSessionRow? aParentRow))
                {
                    aSid = aParent;
                    aMe = aParentRow;
                }
                else
                    oLines.Add($"⚠ 觸發者 {iSessionId} 的主場 {aParent} 還不在台帳上（主場尚未結算？）"
                               + "⇒ 本次只能以觸發者自己的區間匯出，**可能不完整**。");
            }

            string aMedia = aMe.Get("media_id");
            string aLibMedia = aMe.Get("library_media_id");
            var aSessions = new List<string> { aSid };
            var aRanges = new List<SCP_SeqRange> { new SCP_SeqRange(aMe.GetLong("start_seq"), aMe.GetLong("end_seq")) };
            foreach (var kv in aOrdered)
            {
                if (string.Equals(kv.Key, aSid, StringComparison.Ordinal)) continue;
                if (!string.Equals(kv.Value.Get("parent_session_id"), aSid, StringComparison.Ordinal)) continue;
                aSessions.Add(kv.Key);
                aRanges.Add(new SCP_SeqRange(kv.Value.GetLong("start_seq"), kv.Value.GetLong("end_seq")));
            }

            // ── 準備檔（開場前的意圖）────────────────────────────────
            SCP_JsonData aPrepared = SCP_JsonData.NewObject();
            if (aLibMedia.Length > 0)
            {
                string aPp = PreparedPath(iDataRoot, aLibMedia);
                if (File.Exists(aPp))
                {
                    try { aPrepared = SCP_JsonData.Parse(File.ReadAllText(aPp, Encoding.UTF8)); }
                    catch (Exception e) { oLines.Add($"⚠ 準備檔讀不動 {Path.GetFileName(aPp)}: {e.Message}"); }

                    // ── TASK-0076：準備檔的「檔名」與「內容 media_id」交叉對帳 ──
                    // 🩸 側門：C# Editor 那側的守衛掛在 `LoadPrepared`，而**本路徑是直接讀檔** ——
                    //    守衛沒蓋到這條路時，它的失效樣子跟「沒有守衛」一模一樣（kiara 指出的射程洞）。
                    // ⛔ **不挑一邊、不自動修**：矛盾就把 prepared 清空，讓章號／章名退回「要人明示」，
                    //    而不是拿一份不知道自己是誰的檔去決定這一章叫什麼、編號幾號。
                    string aInner = aPrepared.GetString("media_id", "");
                    if (aInner.Length > 0 && !string.Equals(aInner, aLibMedia, StringComparison.Ordinal))
                    {
                        oLines.Add($"⚠ 準備檔 {Path.GetFileName(aPp)} **自己的兩個鍵對不上** —— "
                                   + $"檔名說 `{aLibMedia}`，內容 media_id 說 `{aInner}`"
                                   + (string.Equals(aPrepared.GetString("work_id", ""), aLibMedia, StringComparison.Ordinal)
                                      ? "（檔名剛好等於它自己的 work_id ⇒ 用 work slug 落的舊檔）" : "")
                                   + " ⇒ ⛔ **不採用這份準備檔**（章號與章名請以 --chapter / --title 明示）。");
                        aPrepared = SCP_JsonData.NewObject();
                    }
                }
            }

            // ── 章號的真相源順序（TASK-0142）────────────────────────
            // 物理意義：`prepared/<media_id>.json` 是**開場前的意圖**，per-media **單槽**
            //          ⇒ 下一話 prepare 會覆寫它 ⇒ 跨集之後拿它解舊場，解出來的是**別集的章號**。
            //          `sessions_log` 的 export 事件是**已經發生的事實**（append-only，每場最後一筆為準）。
            // ⚠ prepared 不能拿掉：第一次匯出時台帳上還沒有 exported_chapter，那時它是唯一來源。
            //   ⇒ 修的是**順序**（台帳優先、prepared 當 fallback），不是刪掉一邊。
            string aLedgerCh = (aMe.ExportedChapter ?? "").Trim();
            string aPrepCh = aPrepared.GetString("export_chapter", "").Trim();
            if (aPrepCh.Length == 0) aPrepCh = aPrepared.GetString("chapter_id", "").Trim();

            string aChapter;
            if (!string.IsNullOrWhiteSpace(iExplicitChapter)) aChapter = iExplicitChapter!.Trim();
            else if (aLedgerCh.Length > 0)
            {
                // ⚠ 台帳贏，但**不安靜地贏**：兩邊不一致是「已經跨集」的正常樣子（準備檔被下一話覆寫），
                //   而它同時也可能是別的東西壞了 ⇒ 採用台帳，並把兩個值與各自出處一起印出來。
                // ⛔ 這裡刻意**不擋** —— 擋下來會讓 TASK-0064 的補名路徑（它印的指令不帶 --chapter）
                //   在跨集後每次都失敗，而那正是那張單要修的那條路。
                //   ⇒ 選了能讓消費端活著的那條，差異寫在輸出上，**不靜默**。
                if (aPrepCh.Length > 0 && !SameChapter(NormChapter(aLedgerCh), NormChapter(aPrepCh)))
                {
                    oLines.Add($"⚠ 章號兩個來源不一致 —— 採用**台帳**：{NormChapter(aLedgerCh)}"
                               + "（sessions_log export 事件最後一筆＝已發生的事實）。");
                    oLines.Add($"   準備檔 prepared/{aLibMedia}.json 說 {NormChapter(aPrepCh)}"
                               + "（開場前的意圖，per-media 單槽、會被下一話覆寫）⇒ 這通常表示**已經跨集了**。");
                    // 🩸 而漂掉的不只章號：`chapter_title` / `export_work_title` 住在**同一份單槽準備檔**，
                    //   所以它們也是下一話的。實撞（2026-09-06）：`watch-sluha-narodu/001.txt` 的章名
                    //   被寫成第 2 集的〈瓦夏的故事〉、作品欄從 `[01]` 變成 `[02]` ——
                    //   而**章號那一格已經修對了**，於是產物看起來完全正常：
                    //   第 1 章、第 1 集的區間、第 2 集的名字。
                    // ⇒ 只修章號會把「整章錯」換成「章名錯」，而後者更難發現。
                    // ⛔ 一致的處置（同 TASK-0076）：整份不採用，章名退回哨兵／要人 --title 明示。
                    //   **「看得見的缺」勝過「安靜的錯」**（TASK-0064 的既有拍板）。
                    oLines.Add("   ⚠ 同一份準備檔的 chapter_title / export_work_title **也是下一話的** "
                               + "⇒ 一併不採用（章名請用 --title 明示，否則出哨兵值）。");
                    aPrepared = SCP_JsonData.NewObject();
                }
                aChapter = aLedgerCh;
            }
            else aChapter = aPrepCh;

            // 同一章的舊場次也要一起收（重播／殘場／一話跨數場）
            if (aChapter.Length > 0)
            {
                string aWant = NormChapter(aChapter);
                foreach (var kv in aOrdered)
                {
                    if (aSessions.Contains(kv.Key)) continue;
                    string aPrev = kv.Value.ExportedChapter ?? "";
                    if (aPrev.Length == 0) continue;
                    if (!SameChapter(aPrev, aWant)) continue;
                    if (!string.Equals(kv.Value.Get("media_id"), aMedia, StringComparison.Ordinal)) continue;
                    aSessions.Add(kv.Key);
                    aRanges.Add(new SCP_SeqRange(kv.Value.GetLong("start_seq"), kv.Value.GetLong("end_seq")));
                }
            }

            // ⚠ `lo && hi` —— python 的真值判斷，0 也會被濾掉（缺 start/end 的場次列）。
            var aValid = new List<SCP_SeqRange>();
            foreach (SCP_SeqRange r in aRanges) if (r.Lo != 0 && r.Hi != 0 && r.Hi >= r.Lo) aValid.Add(r);
            if (aValid.Count == 0)
            {
                aOut.Error = $"❌ 場次 {aSid} 沒有可用的 seq 區間（start/end 缺）。";
                return aOut;
            }

            // ── **合併重疊/相接的區間** ────────────────────────────
            // 🩸 2026-08-19 首次自動匯出實測：主場 16014-16025 與陪同場 16015-16023（子集）各掃一次
            //   ⇒ 同 9 則訊息在章裡出現兩次，而**收錄筆數 21、未收錄 0、回讀驗證全綠** —— 沒有一個讀數會叫。
            //   陪同場區間幾乎必然與主場重疊（他在同一場裡），所以這不是邊角案例，是預設路徑。
            // ⚠ 修的是這裡不是掃描端：掃描端去重會讓「同一段給兩次」變成靜默容忍，
            //   而區間是不是該合併，是**呼叫端知道的事實**。
            aValid.Sort((x, y) => x.Lo != y.Lo ? x.Lo.CompareTo(y.Lo) : x.Hi.CompareTo(y.Hi));
            var aMerged = new List<SCP_SeqRange>();
            foreach (SCP_SeqRange r in aValid)
            {
                if (aMerged.Count > 0 && r.Lo <= aMerged[aMerged.Count - 1].Hi + 1)
                {
                    SCP_SeqRange aLast = aMerged[aMerged.Count - 1];
                    aMerged[aMerged.Count - 1] = new SCP_SeqRange(aLast.Lo, Math.Max(aLast.Hi, r.Hi));
                }
                // ⚠ 原文是 `sorted(set(ranges))` ＋ 單純 else append。
                //   重複區間會在上面那條 `lo <= last.hi + 1` 被吸收 ⇒ 去重不影響結果。
                //   ⛔ 我一度在這裡多寫了一個去重條件 —— 移植**不准長出原文沒有的分支**，
                //   因為那種分支永遠沒有對應的血證，而下一個人會以為它擋著什麼。
                else aMerged.Add(r);
            }

            // 台帳記下來的章名（TASK-0142 延伸）—— ⚠ **哨兵值視同「沒有名字」**，
            // 否則 TASK-0064 的補名路徑（章名本來就是哨兵）會被自己擋住。
            string aLedTitle = (aMe.ExportedTitle ?? "").Trim();
            if (string.Equals(aLedTitle, iUntitledMarker, StringComparison.Ordinal)) aLedTitle = "";

            aOut.Media = aMedia;
            aOut.LibraryMediaId = aLibMedia;
            aOut.Sessions = aSessions;
            aOut.Ranges = aMerged;
            aOut.Prepared = aPrepared;
            aOut.Chapter = aChapter;
            aOut.LedgerTitle = aLedTitle;
            aOut.LedgerWorkTitle = (aMe.ExportedWorkTitle ?? "").Trim();
            return aOut;
        }
    }
}
