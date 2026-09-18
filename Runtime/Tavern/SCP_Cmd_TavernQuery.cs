// 區塊職責：酒館訊息查詢的 CLI 入口 —— `senate cmd tavern-query`。
// 物理意義：TASK-0240 第一刀的交付面。**Cmd 是入口不是實作**（Tim 2026-08-20 拍板）——
//           呈現在 `SCP_TavernQuery`、讀取在 `SCP_TavernRead`、索引在 `SCP_TavernMsgIndex`。
// 數值影響：**純讀**。⛔ 一個位元組都不寫進 `ChatTavern/`（TASK-0240 驗收⑥）。
//           ⚠ 包括索引 —— 索引落後只**回報**，修它走 `senate cmd tavern-index --arg op=rebuild`。
//
// 射程：**Editor 側 `op=query` 的 7 個 kind 全部到齊**（2026-09-18 兩刀完成）。
//   · 單房：`tail`（→ `TryGetTailPaths`）／`seq`（→ `TryGetRangePaths`）
//   · 跨房：`rooms`（O(房數)）／`search`／`by_sender`／`timeline`／`stats`（共用 `Collect()`）
//   ⛔ 仍不在射程內：`catchup`／`inbox_read`（會推游標＝寫入）、`note_*`／`task_*`（各自的 IO 層）、
//      `read`／`members`／`listrooms`／`events_since`（各自的 `Op_*`）。
//
// ⚠ `rooms` 刻意**不走 `Collect`** —— 它每房只要最後 1 則＋一個計數 ⇒ O(房數)，
//   ⛔ 別為了統一而讓它變成 O(房數 × 4000)。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using SCP.Core.Cmd;

namespace SCP.Core.Tavern
{
    public sealed class SCP_Cmd_TavernQuery : SCP_Cmd
    {
        public override string Name => "tavern-query";

        public override string Summary =>
            "酒館訊息查詢（7 個 kind：tail／seq／rooms／search／by_sender／timeline／stats）—— **本地跑，不需要 Editor、不需要 Server**；走每日 seq 索引";

        public override string Details =>
            "· `kind=tail`：單房最後 N 則。\n"
            + "· `kind=seq`：指定 `seq`、或 `from`+`to` 區間、或不給（＝最後 4000 則內）再套篩選\n"
            + "  （`sender_persona` / `sender` / `tag` / `grep` / `full`）。\n"
            + "⭐ 輸出格式**逐字對齊** Editor 側 `run Tavern --arg op=query`（驗收④要逐筆對拍）。\n"
            + "⚠ 唯一刻意差異：落盤沒有 `sender_name` 時本側降級印 `sender_id`，**並把降級筆數印在頁尾**\n"
            + "  （Editor 那側是靜默去查帳戶資料）。⛔ 那是降級不是等價。\n"
            + "⚠ 索引落後只回報、⛔ 不自動補寫（純讀不寫）⇒ 修它：`cmd tavern-index --arg op=rebuild`。\n"
            + "· 跨房那 5 支（`rooms`／`search`／`by_sender`／`timeline`／`stats`）：\n"
            + "  ⚠ 成本是 **O(房數 × 每房 4000 則)**，而索引救得了路徑列舉、**救不了 parse**\n"
            + "  ⇒ 標頭裡的「掃 N 則／M 房」就是這次付的錢，⛔ 別讓它只活在體感裡。\n"
            + "  ⚠ 某一房掃到上限會明說**這份清單不完整**（判準是「有沒有某一房掃到頂」，⛔ 不是總數超標）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("tavern-query --arg data_root=<AgentCommands> --arg kind=tail"
                                   + " --arg room=tavern --arg limit=10");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("kind", "tail / seq / rooms / search / by_sender / timeline / stats",
                               iRequired: true,
                               iChoices: new[] { "tail", "seq", "rooms", "search", "by_sender", "timeline", "stats" }),
            new SCP_CmdArgSpec("room",
                "房間 id。⚠ **空的意思依 kind 而不同**：`tail`／`seq` ⇒ 當 `tavern`；"
                + "`search` ⇒ **跨全部房**。⛔ 所以本參數刻意沒有預設值",
                iDefault: ""),
            new SCP_CmdArgSpec("limit", "`tail` 要幾則（≤0 ⇒ 20）", iDefault: "20"),
            new SCP_CmdArgSpec("clip", "正文截斷字數（0 ＝ 不截）", iDefault: "200"),
            new SCP_CmdArgSpec("seq", "`seq`：單一 seq", iDefault: "0"),
            new SCP_CmdArgSpec("from", "`seq`：區間起（含）", iDefault: "0"),
            new SCP_CmdArgSpec("to", "`seq`：區間迄（含）", iDefault: "0"),
            new SCP_CmdArgSpec("last", "`seq`：只列最後幾筆（**顯示上限，不是命中數**）", iDefault: "0"),
            new SCP_CmdArgSpec("sender_persona", "`seq`：精確比對 persona", iDefault: ""),
            new SCP_CmdArgSpec("sender", "`seq`：sender_id 子字串", iDefault: ""),
            new SCP_CmdArgSpec("tag", "`seq`：meta.tag 子字串", iDefault: ""),
            new SCP_CmdArgSpec("grep", "`seq`：正文 regex（不分大小寫）", iDefault: ""),
            new SCP_CmdArgSpec("full", "`seq`：1 ＝ 正文不截斷", iDefault: "0", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("since", "跨房那幾支的時間窗口（`90m` / `24h` / `7d`）。"
                + "⚠ 解析不了會**在輸出標明並不套用**，⛔ 不靜默用預設", iDefault: ""),
            new SCP_CmdArgSpec("keyword", "`search`：要找的字串", iDefault: ""),
            new SCP_CmdArgSpec("case_sensitive", "`search`：1 ＝ 大小寫敏感", iDefault: "0",
                               iChoices: new[] { "0", "1" }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root").Trim();
            string aKind = iArgs.Get("kind").Trim().ToLowerInvariant();
            string aRoom = iArgs.Get("room").Trim();
            if (aRoom.Length == 0) aRoom = "tavern";

            if (!Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aDataRoot);

            string aMsgDir = SCP_TavernMsgIndex.MessagesDir(aDataRoot, aRoom);
            if (!Directory.Exists(aMsgDir))
                // ⚠ 這一格與「這一房是空的」刻意不同形：房間不存在是**參數錯**，不是一個空結果。
                return SCP_CmdResult.Fail(1, "✗ 這一房沒有 messages 目錄：" + aMsgDir,
                                          "  ⛔ 這是「房間不存在」，不是「這一房沒有訊息」");

            var aWatch = Stopwatch.StartNew();
            string aBody;
            SCP_TavernReadStat aStat;
            switch (aKind)
            {
                case "tail":
                    aBody = SCP_TavernQuery.Tail(aDataRoot, aRoom,
                        ParseInt(iArgs, "limit", 20), ParseInt(iArgs, "clip", SCP_TavernQuery.DefaultBodyClip),
                        out aStat);
                    break;
                case "seq":
                    aBody = SCP_TavernQuery.Seq(aDataRoot, aRoom,
                        ParseInt(iArgs, "seq", 0), ParseInt(iArgs, "from", 0), ParseInt(iArgs, "to", 0),
                        ParseInt(iArgs, "last", 0),
                        iArgs.Get("sender_persona").Trim(), iArgs.Get("sender").Trim(),
                        iArgs.Get("tag").Trim(), iArgs.Get("grep"),
                        iArgs.Get("full").Trim() == "1",
                        out aStat);
                    break;
                // ── 跨房那 5 支：成本是 O(房數 × 每房上限)，⚠ 標頭裡的「掃 N 則／M 房」就是這次付的錢 ──
                case "rooms":
                    aBody = SCP_TavernQuery.Rooms(aDataRoot, iArgs.Get("since").Trim(), out aStat);
                    break;
                case "search":
                    // 🩸 這裡傳的是**原始** room（可能是空）而不是補過預設的 `aRoom` ——
                    //   Editor 側 `Search` 的 iRoom 來自 `GetArg(args,"room","")`，**空 ＝ 跨房**。
                    //   2026-09-18 出貨驗收當場抓到：`room` 的 ArgSpec 曾給預設 "tavern"
                    //   ⇒ search 的射程從「跨 52 房」靜默縮成「1 房」，而標頭印「掃 4000 則／**1 房**」
                    //   看起來完全正常。⇒ 又一次「一個正常的結果回答了另一個問題」。
                    aBody = SCP_TavernQuery.Search(aDataRoot, iArgs.Get("keyword"),
                        iArgs.Get("room").Trim(), iArgs.Get("since").Trim(),
                        iArgs.Get("case_sensitive").Trim() == "1",
                        ParseInt(iArgs, "limit", 30), ParseInt(iArgs, "clip", SCP_TavernQuery.DefaultBodyClip),
                        out aStat);
                    break;
                case "by_sender":
                    aBody = SCP_TavernQuery.BySender(aDataRoot, iArgs.Get("sender").Trim(),
                        iArgs.Get("since").Trim(), ParseInt(iArgs, "limit", 20),
                        ParseInt(iArgs, "clip", SCP_TavernQuery.DefaultBodyClip), out aStat);
                    break;
                case "timeline":
                    aBody = SCP_TavernQuery.Timeline(aDataRoot, iArgs.Get("since").Trim(),
                        ParseInt(iArgs, "limit", 30),
                        ParseInt(iArgs, "clip", SCP_TavernQuery.DefaultBodyClip), out aStat);
                    break;
                case "stats":
                    aBody = SCP_TavernQuery.Stats(aDataRoot, iArgs.Get("since").Trim(), out aStat);
                    break;
                default:
                    return SCP_CmdResult.Fail(2, "✗ 不認得的 kind：" + aKind,
                        "  合法值：tail / seq / rooms / search / by_sender / timeline / stats");
            }
            aWatch.Stop();

            var aResult = SCP_CmdResult.Success();
            // ⚠ 去掉尾端換行再切：不去的話 `Split` 會多生一個空 Line，
            //   而那一格就是與 Editor 端輸出唯一的差異（2026-09-18 逐行對拍實測：`23d22 < 空行`）。
            //   ⛔ 別用「過濾空行」去蓋掉它 —— 那會連正文裡刻意的空行一起吃掉，
            //   而那正是本專案的主軸病（我查了，然後把答案濾掉）。
            foreach (string aLine in aBody.TrimEnd('\n').Split('\n')) aResult.Lines.Add(aLine.TrimEnd());

            // 診斷讀數 —— ⛔ 不是裝飾：「這次走了哪條路」決定下一次要不要去 rebuild。
            aResult.AddValue("room", aRoom);
            aResult.AddValue("total", aStat.Total.ToString());
            aResult.AddValue("used_index", aStat.UsedIndex ? "1" : "0");
            aResult.AddValue("stale_days", aStat.StaleDays.ToString());
            aResult.AddValue("full_scan", aStat.FellBackToFullScan ? "1" : "0");
            aResult.AddValue("elapsed_ms", aWatch.ElapsedMilliseconds.ToString());
            return aResult;
        }

        static int ParseInt(SCP_CmdArgs iArgs, string iKey, int iDefault)
        {
            string aRaw = iArgs.Get(iKey).Trim();
            if (aRaw.Length == 0) return iDefault;
            return int.TryParse(aRaw, out int aVal) ? aVal : iDefault;
        }
    }
}
