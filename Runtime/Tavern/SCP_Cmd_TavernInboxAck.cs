// 區塊職責：inbox 歸檔的 CLI 入口 —— `senate cmd tavern-inbox-ack`（TASK-0409：取代 UCL_Core 的 python `inbox_ack.py`）。
// 物理意義：**Cmd 是入口不是實作** —— 歸檔本體在 `SCP_TavernInbox.Ack`，跟 Append／trim 同一把跨 process 鎖。
//           ack ＝「已處理」，不是「已看過」：歸檔之後這些條目不會再出現在 catchup 的 inbox 區。
// 數值影響：每一房各自歸檔（一房失敗不影響其他房）；寫 archive 失敗就不動 inbox（不漏存）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Cmd;

namespace SCP.Core.Tavern
{
    public sealed class SCP_Cmd_TavernInboxAck : SCP_Cmd
    {
        public override string Name => "tavern-inbox-ack";

        public override string Summary =>
            "inbox 歸檔（ack ＝ **已處理**，不是已看過）：把 `rooms/<room>/inbox/<owner>.md` 整份移進 `<owner>_archive.md` —— 本地跑，不需要 Editor";

        public override string Details =>
            "inbox 是被 @ 時寫入端附加的待辦清單（catchup 的「📥 inbox」區讀它）。處理完跑本支，下次只會看到新的。\n"
            + "· 順序：先 append 到 `<owner>_archive.md`、再把 inbox 清成只剩一行檔頭 —— 寫 archive 失敗就不動 inbox。\n"
            + "· 跟寫入端（被 @ 時附加）同一把跨 process 鎖 ⇒ 歸檔途中進來的那一筆不會被清掉。\n"
            + "· `all_rooms=1` 掃**每一房**裡這位的 inbox（不是只有 tavern／hideout）。\n"
            + "⚠ owner 是收件匣擁有者 id：persona（如 summit）或 agent（如 Zeta）都可以；打錯名字會回「沒有這份 inbox」，⛔ 不會說成「已清空」。";

        public override string Example => SCP_CmdRegistry.InvokeOf<SCP_Cmd_TavernInboxAck>("--arg owner=Template");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("owner", "收件匣擁有者 id —— persona 或 agent 皆可，對應 `rooms/<room>/inbox/<owner>.md`", iRequired: true),
            new SCP_CmdArgSpec("room", "只歸檔這一房（預設 tavern）", iDefault: "tavern"),
            new SCP_CmdArgSpec("all_rooms", "1 ⇒ 每一房裡這位的 inbox 都歸檔（給了就不看 room）", iChoices: new[] { "0", "1" }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root").Trim();
            string aOwner = iArgs.Get("owner").Trim();
            string aRoom = iArgs.Get("room").Trim();
            if (aRoom.Length == 0) aRoom = "tavern";
            bool aAll = iArgs.Get("all_rooms") == "1";

            if (!Directory.Exists(aDataRoot)) return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aDataRoot);
            if (aOwner.Length == 0 || aOwner.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return SCP_CmdResult.Fail(2, "✗ owner 不合法：「" + aOwner + "」");

            List<string> aRooms = aAll ? SCP_TavernInbox.RoomsWithInbox(aDataRoot, aOwner) : new List<string> { aRoom };
            var aRes = new SCP_CmdResult();
            int aTotal = 0, aFails = 0, aFound = 0;
            foreach (string r in aRooms)
            {
                SCP_InboxAckResult a = SCP_TavernInbox.Ack(aDataRoot, r, aOwner);
                if (a.Error != null) { aFails++; aRes.Lines.Add($"✗ [{r}] {a.Error}"); continue; }
                if (!a.Existed) { aRes.Lines.Add($"· [{r}] 沒有這份 inbox：`{a.InboxPath}`（⛔ 不是「已清空」）"); continue; }
                aFound++;
                aTotal += a.Archived;
                aRes.Lines.Add(a.Archived > 0
                    ? $"✓ [{r}] 歸檔 {a.Archived} 筆 → `{a.ArchivePath}`（讀回：inbox 剩 {a.RemainingAfter} 筆）"
                    : $"✓ [{r}] inbox 本來就沒有條目（沒動任何檔）");
            }
            if (aAll && aRooms.Count == 0) aRes.Lines.Add($"· 沒有任何一房有 `{aOwner}` 的 inbox");

            aRes.ExitCode = aFails > 0 ? 1 : 0;
            aRes.AddValue("owner", aOwner);
            aRes.AddValue("rooms", aRooms.Count.ToString());
            aRes.AddValue("inbox_found", aFound.ToString());
            aRes.AddValue("archived", aTotal.ToString());
            aRes.AddValue("failed_rooms", aFails.ToString());
            return aRes;
        }
    }
}
