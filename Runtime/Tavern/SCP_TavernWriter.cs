// 區塊職責：酒館訊息的**寫入臨界區**（Senate 側）—— TASK-0106 的第一塊。
// 物理意義：Editor 側 `UCL_ChatTavernWriteService.WriteMessageWithSeq` 的同義移植：
//           「算 seq（＝訊息檔數＋1）→ 原子建檔 → 寫 `_seq.txt` 快取」三件事在同一個 per-room 臨界區裡。
//           ⭐ 這一份**不是第二個寫入端**：它存在的目的是讓那個臨界區能活在 **Server 那一顆 process 裡**，
//           而終局是 Editor 那側改成委派（`tavern_writer=server`），⇒ 同時只有一邊在寫。
//           ⛔ 在切換開關落地之前，**沒有任何呼叫端**會走到這裡 —— 那是刻意的，不是還沒接完。
//
// 數值影響：seq 的權威是**訊息檔數**（`_seq.txt` 只是給 wait 機制讀的 cache，不是 atomic counter）；
//           檔名就是 seq（`{seq:D8}.json`）⇒ 撞號＝撞檔名，而撞檔名由 `FileMode.CreateNew` 當場量到。
//
// 🩸 為什麼建檔用 CreateNew 而不是「先 File.Exists 再寫」（TASK-0256，2026-09-21 summit）：
//    後者是 check-then-write，兩個寫入端同時判「不存在」⇒ 兩邊都寫同一個路徑 ⇒ 後寫的覆蓋先寫的，
//    而 seq 不重號、兩邊都回成功 ⇒ **訊息消失而沒有任何一層會叫**。
//    ⇒ 本檔從第一行起就沒有那個窗口，⛔ 別為了「跟 Editor 那側長得一樣」把它改回去。
//
// 🩸 而計數快取的字典是**跨房共用**的（Editor 那側 `s_RoomMessageCounts` 同形）：
//    per-room lock 只保證同房序列化，兩個**不同房**的寫入會同時改同一個 Dictionary。
//    Editor 是單執行緒所以碰不到；Server 的執行器是「同 lane 串行、跨 lane 並行」⇒ **碰得到**。
//    ⇒ 本檔用 `ConcurrentDictionary`，⛔ 不是把 lock 放大成全域（那會讓跨房寫入互相排隊）。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace SCP.Core.Tavern
{
    /// <summary>一次寫入的結果。<c>Wrote=false</c> 只在**自我校正用盡**時出現（那時 <see cref="Detail"/> 說得出為什麼）。</summary>
    public readonly struct SCP_TavernWriteResult
    {
        public SCP_TavernWriteResult(bool iWrote, int iSeq, string iFullPath, string iDetail, int iHealAttempts)
        {
            Wrote = iWrote;
            Seq = iSeq;
            FullPath = iFullPath ?? "";
            Detail = iDetail ?? "";
            HealAttempts = iHealAttempts;
        }

        /// <summary>真的落盤了嗎。</summary>
        public bool Wrote { get; }

        /// <summary>配到的 seq；⚠ <c>Wrote=false</c> 時這個值是**最後一次嘗試**的號碼，不是「這則的號碼」。</summary>
        public int Seq { get; }

        /// <summary>落盤的絕對路徑（⛔ 不要由 seq 反推 —— 刪過檔的房間反推得到的是別人的訊息）。</summary>
        public string FullPath { get; }

        /// <summary>人讀的理由／讀數。</summary>
        public string Detail { get; }

        /// <summary>
        /// 自我校正跑了幾次（0 ＝ 一次就寫成）。
        /// ⭐ 這個數字**不是 0 就該有人看一眼**：它代表本 process 的計數快取跟磁碟對不上，
        /// 而那在單一寫入端的世界裡不該發生。
        /// </summary>
        public int HealAttempts { get; }
    }

    /// <summary>
    /// 酒館訊息寫入（Senate 側）。⚠ 這是**臨界區本體**，不是發文流程 ——
    /// mention 通知、Discord 鏡像、category 路由那些掛在 Editor 的 `AppendMessage` 上，⛔ 不在本檔射程。
    /// </summary>
    public static class SCP_TavernWriter
    {
        /// <summary>寫入端簽章的鍵（與 Editor 側逐字相同）。</summary>
        public const string WriterSignatureKey = "_writer";

        /// <summary>行程 id 的鍵（與 Editor 側逐字相同）。</summary>
        public const string WriterPidKey = "_pid";

        /// <summary>
        /// 本寫入端的簽章。⛔ **刻意跟 Editor 那側的 `cmd_tavern_v2` 不同** ——
        /// 落盤之後要分得出「這則是誰寫的」，而那是搬家期間唯一一條事後查得到的路徑。
        /// ⚠ 目前全樹**沒有任何消費端**在讀這個欄位（2026-09-21 實查）⇒ 改它不會動到行為，
        /// 而它的價值全在「有人來問的時候答得出來」。
        /// </summary>
        public const string WriterSignatureValue = "scp_tavern_v1";

        const int MaxHealRetries = 3;

        // per-room lock 池：不同房互不阻塞（同 Editor 側的取捨）。
        static readonly ConcurrentDictionary<string, object> s_RoomLocks
            = new ConcurrentDictionary<string, object>(StringComparer.Ordinal);

        // 「這房目前有幾則」的快取。⚠ key 帶資料根 —— 一顆 Server 可能服務多棵樹，
        //   只用 room 當 key 會讓兩棵樹的計數互相污染，而那的失效樣子是撞檔＋自我校正（會叫，但很吵）。
        static readonly ConcurrentDictionary<string, int> s_Counts
            = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        static string CacheKey(string iDataRoot, string iRoom)
        {
            return (iDataRoot ?? "").Replace('\\', '/').TrimEnd('/') + "|" + (iRoom ?? "");
        }

        static object RoomLock(string iDataRoot, string iRoom)
        {
            return s_RoomLocks.GetOrAdd(CacheKey(iDataRoot, iRoom), _ => new object());
        }

        /// <summary>
        /// 丟掉某房的計數快取（測試／人工校正用）。⚠ 丟掉只是讓下一次回去問磁碟，**不修任何東西**。
        /// </summary>
        public static void InvalidateCount(string iDataRoot, string iRoom)
        {
            s_Counts.TryRemove(CacheKey(iDataRoot, iRoom), out _);
        }

        /// <summary>
        /// 寫一則訊息並配 seq。<paramref name="iMsg"/> 的 <c>Ts</c>／<c>Uuid</c> 給了就沿用（遷移工具用），
        /// 空的就現填；<c>Seq</c>／<c>Path</c> 由本函式覆寫。
        /// </summary>
        public static SCP_TavernWriteResult WriteMessage(string iDataRoot, string iRoom, SCP_TavernMessage iMsg)
        {
            if (iMsg == null) throw new ArgumentNullException(nameof(iMsg));
            if (string.IsNullOrEmpty(iRoom)) throw new ArgumentException("room 不可為空", nameof(iRoom));

            // ts：沿用或現填。⚠ 它決定日期資料夾 ⇒ 要在進臨界區前定案，
            //    不然自我校正重試時可能跨過午夜、換到另一個資料夾去算號。
            DateTime aUtc;
            if (!string.IsNullOrEmpty(iMsg.Ts) && DateTime.TryParse(
                    iMsg.Ts, null,
                    System.Globalization.DateTimeStyles.AssumeUniversal
                    | System.Globalization.DateTimeStyles.AdjustToUniversal, out DateTime aParsed))
            {
                aUtc = aParsed;
            }
            else
            {
                aUtc = DateTime.UtcNow;
                iMsg.Ts = aUtc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            }

            if (string.IsNullOrEmpty(iMsg.Uuid)) iMsg.Uuid = NewUuid6();

            iMsg.Meta[WriterSignatureKey] = WriterSignatureValue;
            try { iMsg.Meta[WriterPidKey] = System.Diagnostics.Process.GetCurrentProcess().Id.ToString(); }
            catch { /* 受限環境拿不到 pid —— 缺一個簽章欄位不該讓訊息寫不出去 */ }

            string aDateDir = Path.Combine(SCP_TavernMsgIndex.MessagesDir(iDataRoot, iRoom),
                                           aUtc.ToString("yyyy-MM-dd")).Replace('\\', '/');
            string aKey = CacheKey(iDataRoot, iRoom);
            string aJson = Serialize(iMsg);

            lock (RoomLock(iDataRoot, iRoom))
            {
                if (!s_Counts.TryGetValue(aKey, out int aCount))
                    aCount = CountOnDisk(iDataRoot, iRoom);

                int aSeq = aCount + 1;
                Directory.CreateDirectory(aDateDir);

                for (int aAttempt = 0; aAttempt <= MaxHealRetries; ++aAttempt)
                {
                    string aPath = Path.Combine(aDateDir, aSeq.ToString("D8") + ".json").Replace('\\', '/');
                    if (TryCreateNew(aPath, aJson))
                    {
                        s_Counts[aKey] = aSeq;
                        iMsg.Seq = aSeq;
                        iMsg.Room = iRoom;
                        iMsg.Path = aPath;
                        WriteSeqCache(iDataRoot, iRoom, aSeq);
                        return new SCP_TavernWriteResult(true, aSeq, aPath, "已落盤", aAttempt);
                    }

                    // 撞檔 ＝ 快取與磁碟對不上的訊號。⛔ 不猜下一個號碼 —— 回去問磁碟真相。
                    int aTrue = CountOnDisk(iDataRoot, iRoom);
                    aSeq = aTrue + 1;
                }

                return new SCP_TavernWriteResult(
                    false, aSeq, "",
                    "房間 '" + iRoom + "' 寫入失敗 —— " + MaxHealRetries + " 次自我校正後仍撞檔。"
                    + "⚠ 這代表**有另一個寫入端**在同時寫這個房，或 messages/ 目錄被人工動過。",
                    MaxHealRetries);
            }
        }

        /// <summary>
        /// 原子建檔。回 <c>false</c> **只代表「那個檔名已經有人了」**（win32 80／183）；
        /// ⛔ 磁碟滿／路徑失效那類原樣往上炸（把它們降級成撞檔會讓自我校正白跑三次，然後報一個錯的成因）。
        /// <para>🩸 判準**不是** `File.Exists`（@kiara 2026-09-21 QA）：`FileStream(CreateNew)` 一建構檔就在了
        /// ⇒ 那個條件在任何建構後的 IOException 上都成立，磁碟滿會被降級成撞檔並留下 0-byte 孤兒檔佔住 seq。</para>
        /// </summary>
        static bool TryCreateNew(string iPath, string iJson)
            => SCP.Core.Io.SCP_AtomicFile.TryCreateNew(iPath, iJson);

        /// <summary>
        /// 磁碟上的訊息檔數。⛔ **不走 `SCP_TavernRead.CountMessages`** ——
        /// 那一支索引可用時直接回索引長度，而索引**允許落後**（它自己會印「落後 N 天」）。
        /// 落後的計數在讀取端只是少幾則，在**寫入端是撞檔**。⇒ 這裡一律現場列舉。
        /// </summary>
        static int CountOnDisk(string iDataRoot, string iRoom)
        {
            string aRoot = SCP_TavernMsgIndex.MessagesDir(iDataRoot, iRoom);
            if (!Directory.Exists(aRoot)) return 0;
            return Directory.GetFiles(aRoot, "*.json", SearchOption.AllDirectories).Length;
        }

        /// <summary>
        /// 寫 `_seq.txt`。⚠ 它是**給 wait 機制讀的 cache**，不是配號來源
        /// ⇒ 寫失敗不影響「訊息已經落盤」這個事實，所以這裡吞掉例外（同 Editor 側）。
        /// </summary>
        static void WriteSeqCache(string iDataRoot, string iRoom, int iSeq)
        {
            try
            {
                File.WriteAllText(SCP_TavernRooms.SeqPath(iDataRoot, iRoom),
                                  iSeq.ToString(), new UTF8Encoding(false));
            }
            catch { /* fail swallow —— 見上 */ }
        }

        static string NewUuid6()
        {
            return Guid.NewGuid().ToString("N").Substring(0, 6);
        }

        // ===========================================================
        // 區塊職責：序列化 —— **逐位元組對齊 Editor 側 `SerializeMessageNoSeq`**
        // ⛔ 這裡不可以「順手用 JSON 函式庫」：欄位順序、選填欄位的省略規則、跳脫表
        //    任何一格不同，兩個寫入端寫出來的同一則訊息就會不同形，
        //    而那種壞法**讀得出來、對得起來、只有 diff 看得見**。
        // ⚠ seq **不寫進內容**（它活在檔名裡）—— 這條是刻意的，不是漏了。
        // ===========================================================
        public static string Serialize(SCP_TavernMessage iMsg)
        {
            var aSb = new StringBuilder();
            aSb.Append('{');
            bool aFirst = true;

            Comma(); aSb.Append("\"ts\":\"").Append(Escape(iMsg.Ts)).Append('"');
            if (!string.IsNullOrEmpty(iMsg.Uuid))
            {
                Comma(); aSb.Append("\"uuid\":\"").Append(Escape(iMsg.Uuid)).Append('"');
            }
            Comma(); aSb.Append("\"sender_id\":\"").Append(Escape(iMsg.SenderId)).Append('"');
            Comma(); aSb.Append("\"sender_name\":\"").Append(Escape(iMsg.SenderName)).Append('"');
            if (!string.IsNullOrEmpty(iMsg.SenderPersona))
            {
                Comma(); aSb.Append("\"sender_persona\":\"").Append(Escape(iMsg.SenderPersona)).Append('"');
            }
            if (!string.IsNullOrEmpty(iMsg.SenderAvatarSprite))
            {
                Comma(); aSb.Append("\"sender_avatar_sprite\":\"").Append(Escape(iMsg.SenderAvatarSprite)).Append('"');
            }
            Comma(); aSb.Append("\"kind\":\"").Append(Escape(string.IsNullOrEmpty(iMsg.Kind) ? "chat" : iMsg.Kind)).Append('"');
            Comma(); aSb.Append("\"body\":\"").Append(Escape(iMsg.Body ?? "")).Append('"');
            if (iMsg.ReplyTo.HasValue)
            {
                Comma(); aSb.Append("\"reply_to\":").Append(iMsg.ReplyTo.Value);
            }
            if (!string.IsNullOrEmpty(iMsg.ReplyToUuid))
            {
                Comma(); aSb.Append("\"reply_to_uuid\":\"").Append(Escape(iMsg.ReplyToUuid)).Append('"');
            }
            if (iMsg.Meta != null && iMsg.Meta.Count > 0)
            {
                Comma(); aSb.Append("\"meta\":{");
                bool aFirstMeta = true;
                foreach (KeyValuePair<string, string> aKv in iMsg.Meta)
                {
                    if (!aFirstMeta) aSb.Append(',');
                    aFirstMeta = false;
                    aSb.Append('"').Append(Escape(aKv.Key)).Append("\":\"").Append(Escape(aKv.Value)).Append('"');
                }
                aSb.Append('}');
            }
            if (iMsg.Refs != null && iMsg.Refs.Count > 0)
            {
                Comma(); aSb.Append("\"refs\":[");
                bool aFirstRef = true;
                foreach (SCP_TavernRef aRef in iMsg.Refs)
                {
                    if (!aFirstRef) aSb.Append(',');
                    aFirstRef = false;
                    aSb.Append("{\"path\":\"").Append(Escape(aRef.Path ?? "")).Append('"');
                    if (!string.IsNullOrEmpty(aRef.Anchor)) aSb.Append(",\"anchor\":\"").Append(Escape(aRef.Anchor)).Append('"');
                    if (!string.IsNullOrEmpty(aRef.Label)) aSb.Append(",\"label\":\"").Append(Escape(aRef.Label)).Append('"');
                    aSb.Append('}');
                }
                aSb.Append(']');
            }
            aSb.Append('}');
            return aSb.ToString();

            void Comma() { if (!aFirst) aSb.Append(','); aFirst = false; }
        }

        /// <summary>跳脫表逐字對齊 Editor 側（⛔ 不是「差不多的 JSON 跳脫」）。</summary>
        static string Escape(string? iStr)
        {
            if (iStr == null) return "";
            var aSb = new StringBuilder(iStr.Length + 8);
            foreach (char c in iStr)
            {
                switch (c)
                {
                    case '"': aSb.Append("\\\""); break;
                    case '\\': aSb.Append("\\\\"); break;
                    case '\n': aSb.Append("\\n"); break;
                    case '\r': aSb.Append("\\r"); break;
                    case '\t': aSb.Append("\\t"); break;
                    case '\b': aSb.Append("\\b"); break;
                    case '\f': aSb.Append("\\f"); break;
                    default:
                        if (c < 0x20) aSb.Append("\\u").Append(((int)c).ToString("x4"));
                        else aSb.Append(c);
                        break;
                }
            }
            return aSb.ToString();
        }
    }
}
