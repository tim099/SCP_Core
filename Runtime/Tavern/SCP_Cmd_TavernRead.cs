// 區塊職責：酒館「非查詢」那批純讀 op 的 CLI 入口 —— `senate cmd tavern-read`。
// 物理意義：TASK-0247 的交付面。**Cmd 是入口不是實作**（Tim 2026-08-20 拍板）——
//           訊息讀取在 `SCP_TavernRead`、其餘資料在 `SCP_TavernRooms`、quest 事件在 `SCP_TavernQuestRead`。
// 數值影響：**純讀**。⛔ 一個位元組都不寫進 `ChatTavern/`（TASK-0247 驗收⑤）。
//
// 射程：Editor 側那 6 支**純讀** op —— `read`／`members`／`listrooms`／`note_read`／`note_list`／`events_since`。
//   🔴 ⛔ **不含 `task_list`／`task_next`／`task_state`**：量過（2026-09-20），
//      它們呼叫 `UCL_ChatTavernQuestIO.AutoRecoverStaleLeases`，而那支會 `AppendEvent`
//      ⇒ 它們是「讀為主、寫一格」，與 `catchup`／`inbox_read` 同一類，歸 TASK-0106 那一側。
//      📌 失效樣子很難看：平常沒有過期租約時它們**表現得像純讀**，
//         所以「把它們當純讀」這個錯誤會在剛好有一張單過期的那天才第一次出事。
//   ⛔ 也不含 `op=query` 那 7 個 kind —— 那是 `senate cmd tavern-query`（TASK-0240）。
//
// ⭐ 輸出逐字對齊 Editor 側 `run Tavern --arg op=<那支>`（驗收④要原樣 diff）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using SCP.Core.Cmd;

namespace SCP.Core.Tavern
{
    public sealed class SCP_Cmd_TavernRead : SCP_Cmd
    {
        public override string Name => "tavern-read";

        public override string Summary =>
            "酒館純讀（6 支：read／members／listrooms／note_read／note_list／events_since）"
            + "—— **本地跑，不需要 Editor、不需要 Server**";

        public override string Details =>
            "· `kind=read`：一房的訊息（`search` > `since_seq` > `from`/`to` > 尾讀，四選一，順序同 Editor 側）。\n"
            + "· `kind=members`／`listrooms`／`note_read`／`note_list`／`events_since`：同名 op 的等價入口。\n"
            + "⭐ 輸出格式**逐字對齊** Editor 側 `run Tavern --arg op=<那支>`。\n"
            + "🔴 ⛔ **沒有 `task_list`／`task_next`／`task_state`** —— 它們會 `AppendEvent`"
            + "（回收過期租約）⇒ 不是純讀，歸 TASK-0106 那一側。\n"
            + "⚠ 房間不存在 ⇒ **出聲**（exit 1）；房間存在而結果是空的 ⇒ exit 0 並印「空」。"
            + "⛔ 兩者不同形。\n"
            + "⚠ 訊息讀取走索引；索引落後只**回報**（`stale_days`），⛔ 不自動補寫 ——"
            + "修它走 `senate cmd tavern-index --arg op=rebuild`。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("tavern-read --arg data_root=<AgentCommands> --arg kind=listrooms");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("kind",
                "read / members / listrooms / note_read / note_list / events_since"
                + " / task_list / task_state / task_next",
                               iRequired: true,
                               iChoices: new[] { "read", "members", "listrooms", "note_read", "note_list",
                                                 "events_since", "task_list", "task_state", "task_next" }),
            new SCP_CmdArgSpec("room",
                "房間 id。⚠ `listrooms` 不吃它；其餘五支**必填**"
                + "（⛔ 刻意沒有預設值 —— 預設成 `tavern` 會讓打錯房名的人拿到一個看起來正常的答案）",
                iDefault: ""),
            new SCP_CmdArgSpec("key", "`note_read`：note 的 key（不含 `.md`）", iDefault: ""),
            new SCP_CmdArgSpec("search", "`read`：正文子字串（給了就走搜尋分支）", iDefault: ""),
            new SCP_CmdArgSpec("tail", "`read`：尾讀幾則", iDefault: "0"),
            new SCP_CmdArgSpec("from", "`read`：seq 區間起（含）", iDefault: "0"),
            new SCP_CmdArgSpec("to", "`read`：seq 區間迄（含）", iDefault: "0"),
            new SCP_CmdArgSpec("since_seq",
                "`read`：只看這個 seq 之後的（⛔ -1 ＝ 不套用，0 是合法值）／`events_since`：同名語意",
                iDefault: "-1"),
            new SCP_CmdArgSpec("limit", "`read`／`events_since`：筆數上限", iDefault: "0"),
            new SCP_CmdArgSpec("filter_type", "`events_since`：只看這些 type（逗號分隔）", iDefault: ""),
            // ── TASK-0287：TRPG 任務投影層的三支（⚠ 本側是**純讀版**，⛔ 不回收過期租約）──
            new SCP_CmdArgSpec("task_id", "`task_state`：要看哪一個 task", iDefault: ""),
            new SCP_CmdArgSpec("agent_id", "`task_next`：替誰挑任務（影響 suggested_owner 那一層排序）",
                               iDefault: ""),
            new SCP_CmdArgSpec("top", "`task_next`：回幾筆", iDefault: "1"),
            new SCP_CmdArgSpec("owner", "`task_list`：只看這個 owner", iDefault: ""),
            new SCP_CmdArgSpec("role", "`task_list`：只看這個 role", iDefault: ""),
            new SCP_CmdArgSpec("status",
                "`task_list`：只看這些狀態（逗號分隔）。⚠ `stale` 是**並行旗標**不是狀態 —— "
                + "它跟底下的 claimed／in_progress 同時成立",
                iDefault: ""),
        };

        // Editor 側 `UCL_ChatTavernSettings` 的三個預設值。⚠ 抄值不抄來源 ⇒ 那邊改了這邊不會知道，
        //   所以它們在下面的輸出上**看得見**（筆數印在標題裡），⛔ 不是靜默生效。
        const int ReadTailCount = 30;
        const int SearchLimit = 50;
        const int SinceLimit = 100;

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root").Trim();
            string aKind = iArgs.Get("kind").Trim().ToLowerInvariant();
            string aRoom = iArgs.Get("room").Trim();

            if (!Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aDataRoot);

            if (aKind != "listrooms" && aRoom.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ `kind=" + aKind + "` 缺少 room",
                    "  ⛔ 本層不替你補 `tavern`：補了的話打錯房名的人會拿到一個看起來正常的答案");

            // ── 房間存在性閘：**所有帶 room 的 kind 都過這一關** ──────────────
            // 🔴 這是本支與 Editor 端**刻意不同**的唯一一格，而它是 TASK-0247 驗收⑥要求的：
            //   Editor 側只有 `Op_Read` 與 `Op_NoteList` 會擋，`members`／`note_read`／`events_since`
            //   **打錯房名時回一個空清單、exit 0** ⇒ 「這房沒人」與「沒有這一房」同形。
            //   ⇒ 本側一律擋，⛔ 不為了逐位元組相同而把那個洞一起搬過來。
            //   📌 驗收④的對拍因此**只涵蓋房間存在的情況** —— 不存在那格兩端本來就該不一樣。
            SCP_TavernRoomMeta? aRoomMeta = null;
            if (aRoom.Length > 0)
            {
                aRoomMeta = SCP_TavernRooms.LoadRoomMeta(aDataRoot, aRoom);
                if (aRoomMeta == null)
                    return SCP_CmdResult.Fail(1, "✗ 房間不存在：" + aRoom,
                        "  判準是 `rooms/" + aRoom + "/meta.json` 在不在。",
                        "  ⛔ 這是「沒有這一房」，不是「這一房是空的」—— 後者會回 0 筆並 exit 0。");
            }

            var aWatch = Stopwatch.StartNew();
            var aStat = new SCP_TavernReadStat();
            string aBody;
            switch (aKind)
            {
                case "listrooms": aBody = ListRooms(aDataRoot); break;
                case "members": aBody = Members(aDataRoot, aRoom); break;
                case "note_list": aBody = NoteList(aDataRoot, aRoom); break;
                case "note_read":
                {
                    string aKey = iArgs.Get("key").Trim();
                    if (aKey.Length == 0) return SCP_CmdResult.Fail(2, "✗ `kind=note_read` 缺少 key");
                    string? aNote = NoteRead(aDataRoot, aRoom, aKey);
                    if (aNote == null)
                        return SCP_CmdResult.Fail(1, "✗ note 不存在：" + aRoom + "/" + aKey,
                            "  ⛔ 這是「這個 note 不在」，不是「它是空的」");
                    aBody = aNote;
                    break;
                }
                case "events_since":
                    aBody = EventsSince(aDataRoot, aRoom, ParseInt(iArgs, "since_seq", 0),
                                        iArgs.Get("filter_type").Trim(), ParseInt(iArgs, "limit", 50),
                                        aStat.Warnings);
                    break;
                case "read":
                    // aRoomMeta 在上面那道閘裡已經拿到（room 非空 ⇒ 一定不是 null）
                    aBody = Read(aDataRoot, aRoom, aRoomMeta!, iArgs, aStat);
                    break;
                // ── TASK-0287：TRPG 任務投影（純讀版）──────────────────────
                // 🔴 這三支在 **Editor 側不是純讀** —— 它們開頭跑 `AutoRecoverStaleLeases`，而它會
                //   `AppendEvent`。本側**刻意不做那一步**（理由與後果見 `SCP_TavernQuestState` 檔頭）。
                //   ⛔ 別為了「跟 Editor 一樣」把回收搬過來：那會讓本支成為第二個寫入端。
                case "task_list":
                    aBody = SCP_TavernQuestRender.TaskList(
                        aRoom, SCP_TavernQuestState.Compute(aDataRoot, aRoom, aStat.Warnings),
                        iArgs.Get("owner").Trim(), iArgs.Get("role").Trim(), iArgs.Get("status").Trim());
                    break;
                case "task_state":
                {
                    string aTaskId = iArgs.Get("task_id").Trim();
                    if (aTaskId.Length == 0)
                        return SCP_CmdResult.Fail(2, "✗ `kind=task_state` 缺少 task_id");
                    var aAll = SCP_TavernQuestState.Compute(aDataRoot, aRoom, aStat.Warnings);
                    if (!aAll.TryGetValue(aTaskId, out SCP_QuestTaskState? aOne))
                        return SCP_CmdResult.Fail(1, "✗ task 不存在：" + aTaskId,
                            "  判準是這一房的事件流裡有沒有它的 `task_create`。",
                            "  ⛔ 這是「沒有這個 task」，不是「它沒有事件」。");
                    aBody = SCP_TavernQuestRender.TaskState(aOne, aAll);
                    break;
                }
                case "task_next":
                {
                    string aAgent = iArgs.Get("agent_id").Trim();
                    if (aAgent.Length == 0)
                        return SCP_CmdResult.Fail(2, "✗ `kind=task_next` 缺少 agent_id",
                            "  ⛔ 本層不替你猜是誰要接：猜錯時 suggested_owner 那一層排序會靜默給出"
                            + "一份**看起來很合理**的建議。");
                    aBody = SCP_TavernQuestRender.TaskNext(
                        aRoom, aAgent, ParseInt(iArgs, "top", 1),
                        SCP_TavernQuestState.Compute(aDataRoot, aRoom, aStat.Warnings));
                    break;
                }
                default:
                    return SCP_CmdResult.Fail(2, "✗ 不認得的 kind：" + aKind,
                        "  合法值：read / members / listrooms / note_read / note_list / events_since"
                        + " / task_list / task_state / task_next",
                        "  ⚠ 那三支 task_* 是 **TASK-0287 的純讀投影**（⛔ 不回收過期租約 —— "
                        + "Editor 側那三支會）；查詢那 7 個 kind 走 `tavern-query`");
            }
            aWatch.Stop();

            var aResult = SCP_CmdResult.Success();
            // ⚠ 去掉尾端換行再切 —— 不去的話會多生一個空 Line，而那一格就是與 Editor 端輸出
            //   唯一的差異（TASK-0240 逐行對拍實測過同一格）。⛔ 別改成「過濾空行」：
            //   那會連正文裡刻意的空行一起吃掉。
            // 🔴 而**逐行不 TrimEnd**：Editor 側 `listrooms` 在 description 是空的時候會印出
            //   結尾帶一個空白的 `— `，那一格是輸出的一部分。
            //   🩸 第一版照抄了 `tavern-query` 的 `.TrimEnd()` ⇒ 對拍 96 行全紅，
            //     而每一行單獨看都只是「少一個看不見的空白」。
            foreach (string aLine in aBody.TrimEnd('\n').Split('\n')) aResult.Lines.Add(aLine);

            aResult.AddValue("kind", aKind);
            if (aRoom.Length > 0) aResult.AddValue("room", aRoom);
            aResult.AddValue("elapsed_ms", aWatch.ElapsedMilliseconds.ToString());
            if (aStat.Total > 0) aResult.AddValue("total", aStat.Total.ToString());
            if (aStat.StaleDays > 0) aResult.AddValue("stale_days", aStat.StaleDays.ToString());
            if (aStat.FellBackToFullScan) aResult.AddValue("full_scan", "1");
            // ⚠ 警告要進輸出，⛔ 不只進讀數：讀不動的檔會讓筆數少，而少的那幾筆不會自己說話。
            foreach (string aWarn in aStat.Warnings) aResult.Lines.Add(aWarn);
            return aResult;
        }

        // ── listrooms ─────────────────────────────────────────────────
        static string ListRooms(string iDataRoot)
        {
            var aSb = new StringBuilder();
            aSb.Append("# 🍺 Rooms\n\n");
            List<SCP_TavernRoomMeta> aRooms = SCP_TavernRooms.LoadRooms(iDataRoot);
            if (aRooms.Count == 0) aSb.Append("_(尚無房間)_\n");
            else
                foreach (SCP_TavernRoomMeta r in aRooms)
                    aSb.Append("- `").Append(r.Id).Append("` — ").Append(r.Name)
                       .Append(" (seq=").Append(SCP_TavernRooms.ReadCurrentSeq(iDataRoot, r.Id))
                       .Append(") — ").Append(r.Description).Append('\n');
            return aSb.ToString();
        }

        // ── members ───────────────────────────────────────────────────
        static string Members(string iDataRoot, string iRoom)
        {
            List<string> aIds = SCP_TavernRooms.LoadMemberIds(iDataRoot, iRoom);
            Dictionary<string, SCP_TavernIdentity> aIdents = SCP_TavernRooms.LoadIdentities(iDataRoot);
            var aSb = new StringBuilder();
            aSb.Append("# 👥 Members of `").Append(iRoom).Append("` (").Append(aIds.Count).Append(")\n\n");
            foreach (string aId in aIds)
            {
                if (aIdents.TryGetValue(aId, out SCP_TavernIdentity aIdent))
                    aSb.Append("- `").Append(aIdent.Id).Append("` — **").Append(aIdent.DisplayName)
                       .Append("** (").Append(aIdent.Kind).Append(")\n");
                else
                    aSb.Append("- `").Append(aId).Append("` _(no identity record)_\n");
            }
            return aSb.ToString();
        }

        // ── note_list / note_read ─────────────────────────────────────
        static string NoteList(string iDataRoot, string iRoom)
        {
            List<string> aKeys = SCP_TavernRooms.ListNoteKeys(iDataRoot, iRoom);
            var aSb = new StringBuilder();
            aSb.Append("# 📚 Notes of `").Append(iRoom).Append("` (").Append(aKeys.Count).Append(")\n\n");
            if (aKeys.Count == 0) aSb.Append("_(此房間尚無 note)_\n");
            else
                foreach (string aKey in aKeys)
                    aSb.Append("- `").Append(aKey).Append("` — `")
                       .Append(RepoRelative(iDataRoot, SCP_TavernRooms.NotePath(iDataRoot, iRoom, aKey)))
                       .Append("`\n");
            return aSb.ToString();
        }

        static string? NoteRead(string iDataRoot, string iRoom, string iKey)
        {
            string? aContent = SCP_TavernRooms.ReadNote(iDataRoot, iRoom, iKey);
            if (aContent == null) return null;
            var aSb = new StringBuilder();
            aSb.Append("# 📖 Note: `").Append(iRoom).Append('/').Append(iKey).Append("`\n\n");
            aSb.Append("- path: `")
               .Append(RepoRelative(iDataRoot, SCP_TavernRooms.NotePath(iDataRoot, iRoom, iKey)))
               .Append("`\n\n");
            aSb.Append("---\n\n");
            aSb.Append(aContent);
            return aSb.ToString();
        }

        // ── read ──────────────────────────────────────────────────────
        static string Read(string iDataRoot, string iRoom, SCP_TavernRoomMeta iMeta,
                           SCP_CmdArgs iArgs, SCP_TavernReadStat ioStat)
        {
            string aSearch = iArgs.Get("search");
            int aTail = ParseInt(iArgs, "tail", 0);
            int aFrom = ParseInt(iArgs, "from", 0);
            int aTo = ParseInt(iArgs, "to", 0);
            int aSince = ParseInt(iArgs, "since_seq", -1);
            int aLimit = ParseInt(iArgs, "limit", 0);

            List<SCP_TavernMessage> aMessages;
            string aTitle;
            // ⚠ 分支順序逐字照 Editor 側 `Op_Read`：search > since_seq > from/to > 尾讀。
            //   ⛔ 換順序的話同時給兩個參數時兩端會走不同分支，而各自的輸出都合理。
            if (aSearch.Length > 0)
            {
                aMessages = SearchMessages(iDataRoot, iRoom, aSearch,
                                           aLimit > 0 ? aLimit : SearchLimit, ioStat);
                aTitle = "🔍 " + iMeta.Name + " — 搜尋 \"" + aSearch + "\"（命中 " + aMessages.Count + "）";
            }
            else if (aSince >= 0)
            {
                aMessages = SCP_TavernRead.Range(iDataRoot, iRoom, aSince + 1, int.MaxValue, ioStat);
                int aN = aLimit > 0 ? aLimit : SinceLimit;
                if (aMessages.Count > aN) aMessages = aMessages.GetRange(0, aN);
                aTitle = "📥 " + iMeta.Name + " — since_seq=" + aSince + "（" + aMessages.Count + " 筆）";
            }
            else if (aFrom > 0 || aTo > 0)
            {
                aMessages = SCP_TavernRead.Range(iDataRoot, iRoom, aFrom, aTo > 0 ? aTo : int.MaxValue, ioStat);
                aTitle = "📐 " + iMeta.Name + " — seq " + aFrom + ".."
                         + (aTo > 0 ? aTo.ToString() : "end") + "（" + aMessages.Count + " 筆）";
            }
            else
            {
                // Editor 側：`tail > limit > 後台預設`，而 `limit` 被當成 tail 用時**要出聲**
                //（Tim 2026-07-31 拍板 —— 靜默丟掉那格的代價實測是 66k token）。
                int aN = aTail > 0 ? aTail : (aLimit > 0 ? aLimit : ReadTailCount);
                aMessages = SCP_TavernRead.Tail(iDataRoot, iRoom, aN, ioStat);
                aTitle = "🍺 " + iMeta.Name + " — 最新 " + aMessages.Count + " 筆"
                         + (aTail <= 0 && aLimit > 0 ? "（`limit=" + aLimit + "` 已當成 tail 用）" : "");
            }
            return RenderMessages(aTitle, aMessages);
        }

        /// <summary>本側搜尋掃多少則（同查詢層 `SCAN_PER_ROOM`）。⚠ 抄值不抄來源 ⇒ 它印在輸出的頁尾上。</summary>
        const int SearchScanCap = 4000;

        /// <summary>
        /// 正文子字串搜尋。⚠ 射程是**最後 <see cref="SearchScanCap"/> 則**，⛔ 不是全房 ——
        /// 而 Editor 側 `IO.Search` 走的是**全房載入再過濾**，兩者**不是同一個集合**。
        /// 📌 所以這一格在對拍時是**已知可能不符**的那一格：命中數落在 4000 則之外時會少。
        /// ⛔ 要單獨報，不混進「移植壞了」——「射程不同」與「搬錯了」的 diff 長得一樣。
        /// </summary>
        static List<SCP_TavernMessage> SearchMessages(string iDataRoot, string iRoom, string iNeedle,
                                                      int iLimit, SCP_TavernReadStat ioStat)
        {
            List<SCP_TavernMessage> aAll = SCP_TavernRead.Tail(iDataRoot, iRoom, SearchScanCap, ioStat);
            var aOut = new List<SCP_TavernMessage>();
            foreach (SCP_TavernMessage m in aAll)
            {
                if (m.Body.IndexOf(iNeedle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                aOut.Add(m);
                if (aOut.Count >= iLimit) break;
            }
            return aOut;
        }

        /// <summary>⭐ 逐字照 Editor 側 `UCL_ChatTavernRender.RenderMessages`（驗收④要原樣 diff）。</summary>
        static string RenderMessages(string iTitle, List<SCP_TavernMessage> iMessages)
        {
            var aSb = new StringBuilder();
            aSb.Append("# ").Append(iTitle).Append("\n\n");
            if (iMessages.Count == 0) { aSb.Append("_(尚無訊息)_\n"); return aSb.ToString(); }
            foreach (SCP_TavernMessage m in iMessages)
            {
                aSb.Append("[seq ").Append(m.Seq).Append("] ");
                aSb.Append(ShortTime(m.Ts)).Append(' ');
                if (m.Kind.Length > 0 && m.Kind != "chat") aSb.Append('(').Append(m.Kind).Append(") ");
                aSb.Append(DisplayName(m)).Append(": ");
                aSb.Append(m.Body);
                if (m.ReplyTo.HasValue) aSb.Append(" _(↩ ").Append(m.ReplyTo.Value).Append(")_");
                if (m.Meta.Count > 0)
                {
                    aSb.Append("\n  - meta:");
                    foreach (KeyValuePair<string, string> kv in m.Meta)
                        aSb.Append(" `").Append(kv.Key).Append('=').Append(kv.Value).Append('`');
                }
                if (m.Refs.Count > 0)
                {
                    aSb.Append("\n  - refs:");
                    foreach (SCP_TavernRef r in m.Refs)
                    {
                        aSb.Append(" [").Append(r.Label.Length > 0 ? r.Label : r.Path).Append("](").Append(r.Path);
                        if (r.Anchor.Length > 0) aSb.Append('#').Append(r.Anchor);
                        aSb.Append(')');
                    }
                }
                aSb.Append('\n');
            }
            return aSb.ToString();
        }

        /// <summary>
        /// 顯示名，逐字照 Editor 側 `UCL_AgentIdParser.Display(sender_id, sender_persona, sender_name)`：
        /// 底名 ＝ `sender_name`，空的降級成 `sender_id`，兩個都空 ⇒ `?`；
        /// **有 persona 就接 `@&lt;persona&gt;`**。
        /// <para>🩸 第一版只回 `sender_name` ⇒ 對拍時每一則都少了 `@summit` 那半，
        /// 而那一行讀起來完全正常 —— 它就是一個人的名字。</para>
        /// </summary>
        static string DisplayName(SCP_TavernMessage iMsg)
        {
            string aBase = iMsg.SenderName.Length > 0 ? iMsg.SenderName : iMsg.SenderId;
            if (aBase.Length == 0) aBase = "?";
            return iMsg.SenderPersona.Length > 0 ? aBase + "@" + iMsg.SenderPersona : aBase;
        }

        /// <summary>
        /// 絕對路徑 → repo 相對（同 Editor 側 `UCL_ChatTavernIO.ToRepoRelative`）。
        /// <para>⚠ 本側沒有 `UCL_RepoPath.RepoRoot` ⇒ **repo 根取 `data_root` 的上一層**
        /// （版面是 `&lt;repo&gt;/AgentCommands/`）。⛔ 那是一個假設不是讀數：
        /// 資料根若不掛在 repo 底下，本函式**回絕對路徑** —— 而那正是它該回的（對不上就別裁）。</para>
        /// </summary>
        static string RepoRelative(string iDataRoot, string iAbs)
        {
            try
            {
                string aParent = Path.GetDirectoryName(Path.GetFullPath(iDataRoot)) ?? "";
                if (aParent.Length == 0) return iAbs.Replace('\\', '/');
                string aRoot = aParent.Replace('\\', '/').TrimEnd('/') + "/";
                string aFull = Path.GetFullPath(iAbs).Replace('\\', '/');
                return aFull.StartsWith(aRoot, StringComparison.OrdinalIgnoreCase)
                       ? aFull.Substring(aRoot.Length) : aFull;
            }
            catch (Exception) { return iAbs.Replace('\\', '/'); }
        }

        /// <summary>
        /// 逐字照 Editor 側 `UCL_ChatTavernRender.ShortTime`：**切 ISO 字串**的 `T` 之後 8 個字元。
        /// <para>🔴 ⛔ 這裡**不做時區轉換**，而那是刻意的：落盤的 `ts` 是 UTC，Editor 側印的就是 UTC 的
        /// `HH:mm:ss`。第一版我寫成 `ToLocalTime().ToString("HH:mm")` —— 那會**每一行都差 8 小時、
        /// 而且少印秒數**，⇒ 逐筆對拍全紅，而每一行單獨看都像一個合理的時間。</para>
        /// <para>空字串回 `??:??:??`、切不出來回原字串 —— 兩者都照抄，⛔ 不「改良」。</para>
        /// </summary>
        static string ShortTime(string iTs)
        {
            if (iTs.Length == 0) return "??:??:??";
            int aT = iTs.IndexOf('T');
            if (aT < 0 || aT + 9 > iTs.Length) return iTs;
            return iTs.Substring(aT + 1, 8);
        }

        // ── events_since ──────────────────────────────────────────────
        static string EventsSince(string iDataRoot, string iRoom, int iSinceSeq, string iFilterCsv,
                                  int iLimit, List<string> ioWarn)
        {
            if (iSinceSeq < 0) iSinceSeq = 0;
            if (iLimit <= 0) iLimit = 50;

            var aFilter = new HashSet<string>(StringComparer.Ordinal);
            if (iFilterCsv.Length > 0)
                foreach (string t in iFilterCsv.Split(','))
                {
                    string s = t.Trim();
                    if (s.Length > 0) aFilter.Add(s);
                }

            List<SCP_TavernQuestEvent> aAll = SCP_TavernQuestRead.LoadAll(iDataRoot, iRoom, ioWarn);
            var aDeltas = new List<SCP_TavernQuestEvent>();
            foreach (SCP_TavernQuestEvent ev in aAll)
            {
                if (ev.Seq <= iSinceSeq) continue;
                if (aFilter.Count > 0 && !aFilter.Contains(ev.Type)) continue;
                aDeltas.Add(ev);
            }
            aDeltas.Sort((a, b) => a.Seq.CompareTo(b.Seq));

            int aTotalAfter = aDeltas.Count;
            bool aTruncated = aTotalAfter > iLimit;
            if (aTruncated) aDeltas = aDeltas.GetRange(0, iLimit);
            int aLatest = aAll.Count > 0 ? aAll[aAll.Count - 1].Seq : 0;

            var aSb = new StringBuilder();
            aSb.Append("# 🕒 events_since — `").Append(iRoom).Append("`\n");
            aSb.Append('\n');
            aSb.Append("- since_seq: **").Append(iSinceSeq).Append("** → latest_seq: **").Append(aLatest).Append("**\n");
            aSb.Append("- delta count: **").Append(aTotalAfter).Append("**")
               .Append(aTruncated ? " (顯示前 " + iLimit + " 筆，請拉大 limit 或縮窄 filter_type)" : "").Append('\n');
            if (aFilter.Count > 0) aSb.Append("- filter_type: ").Append(string.Join(",", aFilter)).Append('\n');
            aSb.Append('\n');

            if (aDeltas.Count == 0)
            {
                aSb.Append("_(無新事件 — 自上次離開後本房安靜如雞)_\n");
                return aSb.ToString();
            }

            aSb.Append("| seq | ts | type | actor | task | summary |\n");
            aSb.Append("|---|---|---|---|---|---|\n");
            foreach (SCP_TavernQuestEvent ev in aDeltas)
            {
                string aDetail = "";
                if (ev.Data.Count > 0)
                {
                    var aPieces = new List<string>();
                    // 偏好順序逐字照 Editor 側：summary > reason > lease_until > 其它頭兩個
                    if (ev.Data.TryGetValue("summary", out string aS)) aPieces.Add(Truncate(aS, 40));
                    else if (ev.Data.TryGetValue("reason", out string aR)) aPieces.Add("reason: " + Truncate(aR, 40));
                    else if (ev.Data.TryGetValue("lease_until", out string aLu)) aPieces.Add("lease→" + aLu);
                    else
                    {
                        int aTaken = 0;
                        foreach (KeyValuePair<string, string> kv in ev.Data)
                        {
                            aPieces.Add(kv.Key + "=" + Truncate(kv.Value, 20));
                            if (++aTaken >= 2) break;
                        }
                    }
                    aDetail = string.Join("; ", aPieces);
                }
                aSb.Append("| ").Append(ev.Seq).Append(" | ").Append(ev.Ts).Append(" | `").Append(ev.Type)
                   .Append("` | ").Append(ev.Actor).Append(" | ")
                   .Append(ev.TaskId.Length > 0 ? ev.TaskId : "-").Append(" | ").Append(aDetail).Append(" |\n");
            }
            aSb.Append('\n');
            aSb.Append("_提示：下次 re-enter 用 `since_seq=").Append(aLatest)
               .Append("` 看新增 delta；單 task 完整 timeline 走 `task_state task_id=...`_\n");
            return aSb.ToString();
        }

        /// <summary>
        /// ⚠ 尾巴是**三個 ASCII 點** `...`，⛔ 不是 `…`（單字元省略號）——
        /// Editor 側 `Cmd_Tavern.Truncate` 逐字就是那三個字元。換一個的話每一行都不符，
        /// 而 diff 只會說「不一樣」，不會說「差在省略號」。
        /// </summary>
        static string Truncate(string iText, int iMax)
        {
            if (iText.Length == 0) return "";
            return iText.Length <= iMax ? iText : iText.Substring(0, iMax) + "...";
        }

        static int ParseInt(SCP_CmdArgs iArgs, string iKey, int iDefault)
        {
            string aRaw = iArgs.Get(iKey).Trim();
            if (aRaw.Length == 0) return iDefault;
            return int.TryParse(aRaw, out int aVal) ? aVal : iDefault;
        }
    }
}
