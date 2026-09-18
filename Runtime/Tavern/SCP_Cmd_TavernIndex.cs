// 區塊職責：酒館訊息索引的**診斷與驗證入口** —— `senate cmd tavern-index`。
// 物理意義：`SCP_TavernMsgIndex` 是加速層，而加速層唯一該被問的問題是**它有沒有改變答案**。
//           本 Cmd 提供三個 op：
//             · verify  —— 索引算出來的清單 vs 全量列舉，**逐筆比路徑**（不是抽樣、不是比數量）
//             · stat    —— 某一房命中索引沒有、現場列舉了幾天、總筆數（給「它到底生效了嗎」一個讀數）
//             · rebuild —— 顯式重建某一房（或全部）的索引
// 數值影響：verify／stat 純讀；rebuild 只寫 `rooms/<room>/_msgindex.txt`，⛔ 不碰任何訊息檔。
//
// 🩸 為什麼這支要先於查詢層存在（summit 2026-09-18，TASK-0240）：
//   索引的失效方式**不是報錯，是安靜地變慢或安靜地少一筆**。
//   而「少一筆」的後果很具體：seq 全體位移 ⇒ 所有游標指到錯的訊息，**而外觀完全正常**。
//   ⇒ 所以先造尺，再交付靠它的東西。⛔ 反過來做的話，第一次驗收拿到的是自己的讀數。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Cmd;

namespace SCP.Core.Tavern
{
    public sealed class SCP_Cmd_TavernIndex : SCP_Cmd
    {
        public override string Name => "tavern-index";

        public override string Summary =>
            "酒館訊息索引：驗證（索引 vs 全量列舉**逐筆**對撞）／看某房的命中狀況／顯式重建 —— **本地跑，不需要 Editor**";

        public override string Details =>
            "索引 `rooms/<room>/_msgindex.txt` 一天一行（`<日期>\\t<起始 seq>\\t<筆數>\\t<目錄 mtime>`）\n"
            + "⇒ 大小跟**天數**成正比，不是跟訊息數成正比。\n"
            + "· `op=verify`：逐房把「索引算出的路徑清單」與「全量列舉的清單」**逐筆比對**。\n"
            + "  ⛔ 不抽樣、不比數量 —— 少一筆的後果是 seq 全體位移，而外觀完全正常。\n"
            + "· `op=stat`：給 `room` 看它這次有沒有命中索引、現場列舉了幾天、總筆數。\n"
            + "  ⚠ `used_index=0` 的意思是「這次沒省到」，⛔ 不是「答案錯了」。\n"
            + "· `op=rebuild`：顯式重建（給 `room` 就一房，不給就全部）。\n"
            + "⭐ 任何不一致一律退回全量列舉 —— **把失效降級成「變慢」，不是「算錯」**。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("tavern-index --arg data_root=<AgentCommands> --arg op=verify");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("op", "verify / stat / rebuild", iRequired: true,
                               iChoices: new[] { "verify", "stat", "rebuild" }),
            new SCP_CmdArgSpec("room", "房間 id（`stat` 必填；`rebuild` 不給＝全部房）", iDefault: ""),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root").Trim();
            string aOp = iArgs.Get("op").Trim().ToLowerInvariant();
            string aRoom = iArgs.Get("room").Trim();

            if (!Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aDataRoot);

            string aRoomsRoot = SCP_TavernMsgIndex.RoomsRoot(aDataRoot);
            if (!Directory.Exists(aRoomsRoot))
                return SCP_CmdResult.Fail(1, "✗ 找不到 rooms 目錄：" + aRoomsRoot);

            // 警告改收進回傳，⛔ 不讓它只去 stderr —— 回傳檔才是下一步會被讀的東西。
            var aWarnings = new List<string>();
            Action<string> aPrev = SCP_TavernMsgIndex.Warn;
            SCP_TavernMsgIndex.Warn = s => aWarnings.Add(s);
            try
            {
                switch (aOp)
                {
                    case "verify": return OpVerify(aDataRoot, aWarnings);
                    case "stat": return OpStat(aDataRoot, aRoom, aWarnings);
                    case "rebuild": return OpRebuild(aDataRoot, aRoom, aRoomsRoot, aWarnings);
                    default:
                        return SCP_CmdResult.Fail(2, "✗ 不認得的 op：" + aOp + "（verify / stat / rebuild）");
                }
            }
            finally { SCP_TavernMsgIndex.Warn = aPrev; }
        }

        // ===========================================================
        static SCP_CmdResult OpVerify(string iDataRoot, List<string> ioWarnings)
        {
            string aReport = SCP_TavernMsgIndex.Verify(iDataRoot);
            bool aBad = aReport.Contains("🚨");
            var aResult = aBad
                ? SCP_CmdResult.Fail(5, "## 索引驗證（逐筆對撞）")
                : SCP_CmdResult.Success("## 索引驗證（逐筆對撞）");
            foreach (string aLine in aReport.Split('\n'))
                if (aLine.Length > 0) aResult.Lines.Add(aLine.TrimEnd());
            AppendWarnings(aResult, ioWarnings);
            aResult.AddValue("mismatch", aBad ? "1" : "0");
            return aResult;
        }

        static SCP_CmdResult OpStat(string iDataRoot, string iRoom, List<string> ioWarnings)
        {
            if (iRoom.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ `op=stat` 要給 room —— 沒有房間定語的命中率是形狀正確的錯答案");

            string aMsgDir = SCP_TavernMsgIndex.MessagesDir(iDataRoot, iRoom);
            if (!Directory.Exists(aMsgDir))
                return SCP_CmdResult.Fail(1, "✗ 這一房沒有 messages 目錄：" + aMsgDir);

            string[]? aPaths = SCP_TavernMsgIndex.TryGetOrderedPaths(
                iDataRoot, iRoom, out bool aUsedIndex, out int aEnumDays);

            var aResult = SCP_CmdResult.Success("## 索引現況 — `" + iRoom + "`");
            aResult.Lines.Add("· 索引檔：`" + SCP_TavernMsgIndex.IndexPath(iDataRoot, iRoom) + "`");

            if (aPaths == null)
            {
                // ⚠ 這一格要說清楚：**這是「索引這條路走不了」，不是「這一房沒有訊息」。**
                aResult.Lines.Add("· 🔻 **索引這條路走不了** ⇒ 呼叫端會退回全量列舉（慢，但答案一樣）");
                aResult.Lines.Add("  ⛔ 而這**不是**「這一房沒有訊息」—— 兩者在這支 Cmd 裡刻意不同形");
                aResult.AddValue("index_usable", "0");
                AppendWarnings(aResult, ioWarnings);
                return aResult;
            }

            int aDayDirs = Directory.GetDirectories(aMsgDir).Length;
            aResult.Lines.Add("· 訊息筆數（索引算出來的）：**" + aPaths.Length + "**");
            aResult.Lines.Add("· 日期目錄：" + aDayDirs + " 天　／　這次**現場列舉**：" + aEnumDays + " 天");
            aResult.Lines.Add(aUsedIndex
                ? "· ⭐ 有省到：至少一天是**算出來**的，沒有列舉"
                : "· ⚠ `used_index=0` —— 這次一天都沒省到（索引缺天或全部動過）。⛔ 這是「沒省到」，不是「答錯」");

            // ── seq 對帳（Tim 2026-09-18 拍板：**以訊息檔案數量為準**）──────────────
            // 物理意義：`_seq.txt` 是寫入端的「下一個號」來源，**它是衍生值不是權威**。
            //           權威是 messages/ 底下那些檔案本身（連號、從 1 開始）。
            // 🩸 為什麼要印成三格並當場判：summit 2026-09-18 早上報過一個「差 67」的假差值 ——
            //   `_seq.txt` 是 08:5x 讀的、索引筆數是 11:32 算的。**同一個受詞、兩個時刻。**
            //   ⇒ 同一時點重量：19,268 ／ 19,268 ／ 19,268，**三個數字完全相等**。
            //   📌 那族的新方向：受詞對了，而我漏了**時點**。⇒ 所以這三格由同一次呼叫一起印。
            int aFiles = aPaths.Length;
            int aMaxSeq = 0;
            if (aFiles > 0)
            {
                string aLast = Path.GetFileNameWithoutExtension(aPaths[aFiles - 1]);
                int.TryParse(aLast, out aMaxSeq);
            }
            string aSeqTxtPath = Path.Combine(
                SCP_TavernMsgIndex.RoomDir(iDataRoot, iRoom), "_seq.txt").Replace(BackSlash, '/');
            int aSeqTxt = -1;
            if (File.Exists(aSeqTxtPath))
            {
                try { int.TryParse(File.ReadAllText(aSeqTxtPath).Trim(), out aSeqTxt); }
                catch (Exception e) { aResult.Lines.Add("· ⚠ 讀不動 `_seq.txt`：" + e.Message); }
            }

            aResult.Lines.Add("");
            aResult.Lines.Add("### seq 對帳（⭐ 權威＝**訊息檔案數量**，`_seq.txt` 是衍生值）");
            aResult.Lines.Add("· 訊息檔筆數（權威）：**" + aFiles + "**");
            aResult.Lines.Add("· 最大檔名 seq：" + aMaxSeq
                              + (aMaxSeq == aFiles ? "　✅ ＝筆數 ⇒ **連號且從 1 開始**"
                                                   : "　🔴 ≠筆數 ⇒ 有洞或不從 1 開始"));
            if (aSeqTxt < 0)
                aResult.Lines.Add("· `_seq.txt`：**讀不到** ⇒ ⛔ 這格是「量不到」，不是「一致」");
            else if (aSeqTxt == aFiles)
                aResult.Lines.Add("· `_seq.txt`：" + aSeqTxt + "　✅ 與筆數一致");
            else if (aSeqTxt < aFiles)
                aResult.Lines.Add("· `_seq.txt`：" + aSeqTxt + "　🔴 **比筆數小 " + (aFiles - aSeqTxt)
                                  + "** ⇒ 下一則會**撞名**（`CreateNew` 會炸，而那是好的；但寫入端若覆寫就是靜默丟訊息）");
            else
                aResult.Lines.Add("· `_seq.txt`：" + aSeqTxt + "　⚠ **比筆數大 " + (aSeqTxt - aFiles)
                                  + "** ⇒ 有號被跳過（訊息被刪／寫入失敗留下的空號）—— ⛔ 不是錯，但要看得見");
            aResult.AddValue("files", aFiles.ToString());
            aResult.AddValue("max_seq", aMaxSeq.ToString());
            aResult.AddValue("seq_txt", aSeqTxt.ToString());
            aResult.AddValue("seq_consistent", (aSeqTxt == aFiles && aMaxSeq == aFiles) ? "1" : "0");
            aResult.AddValue("index_usable", "1");
            aResult.AddValue("total", aPaths.Length.ToString());
            aResult.AddValue("day_dirs", aDayDirs.ToString());
            aResult.AddValue("enumerated_days", aEnumDays.ToString());
            aResult.AddValue("used_index", aUsedIndex ? "1" : "0");
            AppendWarnings(aResult, ioWarnings);
            return aResult;
        }

        static SCP_CmdResult OpRebuild(string iDataRoot, string iRoom, string iRoomsRoot, List<string> ioWarnings)
        {
            var aRooms = new List<string>();
            if (iRoom.Length > 0) aRooms.Add(iRoom);
            else
                foreach (string aDir in Directory.GetDirectories(iRoomsRoot))
                    if (Directory.Exists(Path.Combine(aDir, "messages")))
                        aRooms.Add(Path.GetFileName(aDir));

            var aResult = SCP_CmdResult.Success("## 索引重建");
            int aDone = 0, aSkipped = 0;
            foreach (string aR in aRooms)
            {
                string aMsgDir = SCP_TavernMsgIndex.MessagesDir(iDataRoot, aR);
                if (!Directory.Exists(aMsgDir)) { aSkipped++; continue; }

                // 全量列舉一次（真值），再由它重建 —— 與 Editor 側 `Rebuild(orderedPaths)` 同一份語意。
                string[] aTruth = Directory.GetFiles(aMsgDir, "*.json", SearchOption.AllDirectories);
                var aKeys = new string[aTruth.Length];
                for (int i = 0; i < aTruth.Length; i++)
                    aKeys[i] = aTruth[i].Substring(aMsgDir.Length).Replace(BackSlash, '/');
                Array.Sort(aKeys, aTruth, StringComparer.Ordinal);

                SCP_TavernMsgIndex.Rebuild(iDataRoot, aR, aTruth);
                aDone++;
            }
            aResult.Lines.Add("· 重建 **" + aDone + "** 房　／　跳過（沒有 messages 目錄）" + aSkipped + " 房");
            aResult.Lines.Add("⚠ 重建只寫 `_msgindex.txt`，**一個訊息檔都沒動**。");
            aResult.Lines.Add("📌 驗它有沒有改變答案 → `op=verify`（逐筆對撞），⛔ 別拿「重建成功」當證據。");
            AppendWarnings(aResult, ioWarnings);
            aResult.AddValue("rebuilt", aDone.ToString());
            aResult.AddValue("skipped", aSkipped.ToString());
            return aResult;
        }

        static void AppendWarnings(SCP_CmdResult ioResult, List<string> iWarnings)
        {
            if (iWarnings.Count == 0) return;
            ioResult.Lines.Add("");
            ioResult.Lines.Add("### ⚠ 索引層的警告（**收進回傳檔，不只丟 stderr**）");
            foreach (string aW in iWarnings) ioResult.Lines.Add("· " + aW);
            ioResult.AddValue("index_warnings", iWarnings.Count.ToString());
        }

        /// <summary>反斜線字元。⛔ 用數值碼不用字面，理由見 <see cref="SCP_TavernMsgIndex"/> 的同名常數。</summary>
        const char BackSlash = (char)92;
    }
}
