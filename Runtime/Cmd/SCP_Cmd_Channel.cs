// 區塊職責：`cmd channel` —— 頻道（酒館房間）管理的 CLI 出口：分類清單、頻道分類、封存（TASK-0318）。
// 物理意義：讀寫全走 `SCP_TavernChannels`（與 Senate 後台「頻道管理」頁同一份）；本 Cmd 只是薄殼。
//           **分類要先新增，頻道才能選它**（Tim 2026-09-28）。完全不需要 Unity。
// 數值影響：`op=list`／`op=categories` 零寫入；其餘 op 寫 `channel_categories.json` 或 `rooms/<room>/channel.json`，
//           驗證不過一律零寫入。封存 ＝ 把整個房間資料夾搬到 `ChatTavern/rooms_archive/`（取消封存搬回來），⛔ 不刪任何訊息。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using SCP.Core.Tavern;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Channel : SCP_Cmd
    {
        public override string Name => "channel";

        public override string Summary => "頻道（酒館房間）管理：頻道分類清單（先新增才能選）、設定頻道分類、封存／取消封存 —— **本地跑，不需要 Editor**";

        public override string Details =>
            "資料：分類清單 `ChatTavern/channel_categories.json`；頻道設定 `ChatTavern/rooms/<room>/channel.json`（⛔ 不寫 meta.json）。\n"
            + "· `op=list`（預設）：全部頻道的分類／封存／最後活動；`include_archived=0` 不列封存的。\n"
            + "· `op=categories`：分類清單與各自被幾個頻道使用。\n"
            + "· `op=add-category --arg name=<分類> [--arg description=...]`：新增分類（同名不分大小寫會被擋）。\n"
            + "· `op=remove-category --arg name=<分類>`：刪除分類 —— 還有頻道在用就擋下並列出是哪些。\n"
            + "· `op=set-category --arg room=<房> --arg category=<分類>`：設頻道分類；`category` 留空 ＝ 未分類。分類要已在清單裡。\n"
            + "· `op=archive`／`op=unarchive --arg room=<房>`：封存／取消封存 ＝ 把房間資料夾搬到 `rooms_archive/`／搬回 `rooms/`（目的地有同名資料夾就擋下）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("channel --arg op=set-category --arg room=tavern --arg category=Main");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("op", "做什麼", iDefault: "list",
                iChoices: new[] { "list", "categories", "add-category", "remove-category", "set-category", "archive", "unarchive" }),
            new SCP_CmdArgSpec("room", "頻道（房間 id）：set-category／archive／unarchive 必填", iDefault: ""),
            new SCP_CmdArgSpec("category", "op=set-category：分類名（要已在分類清單裡）；留空＝未分類", iDefault: ""),
            new SCP_CmdArgSpec("name", "op=add-category／remove-category：分類名", iDefault: ""),
            new SCP_CmdArgSpec("description", "op=add-category：分類說明", iDefault: ""),
            new SCP_CmdArgSpec("include_archived", "op=list：1＝也列封存的", iDefault: "1", iChoices: new[] { "0", "1" }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aRoot = iArgs.Get("data_root").Trim();
            if (!System.IO.Directory.Exists(aRoot)) return SCP_CmdResult.Fail(1, $"✗ 資料根不存在：{aRoot}");
            string aOp = iArgs.Get("op").Trim();
            string aRoom = iArgs.Get("room").Trim();

            switch (aOp)
            {
                case "list": return List(aRoot, iArgs.Get("include_archived").Trim() == "1");
                case "categories": return Categories(aRoot);
                case "add-category":
                {
                    string aName = iArgs.Get("name").Trim();
                    if (!SCP_TavernChannels.TryAddCategory(aRoot, aName, iArgs.Get("description"), out string? aErr))
                        return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    var aR = SCP_CmdResult.Success($"✅ 已新增分類 **{aName}**（回讀）");
                    aR.AddValue("categories", SCP_TavernChannels.LoadCategories(aRoot).Count.ToString(CultureInfo.InvariantCulture));
                    return aR;
                }
                case "remove-category":
                {
                    string aName = iArgs.Get("name").Trim();
                    if (!SCP_TavernChannels.TryRemoveCategory(aRoot, aName, out string? aErr))
                        return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                    var aR = SCP_CmdResult.Success($"✅ 已刪除分類 **{aName}**（回讀）");
                    aR.AddValue("categories", SCP_TavernChannels.LoadCategories(aRoot).Count.ToString(CultureInfo.InvariantCulture));
                    return aR;
                }
            }

            if (aRoom.Length == 0) return SCP_CmdResult.Fail(2, $"✗ op={aOp} 要 `--arg room=<房間 id>`");

            if (aOp == "set-category")
            {
                if (!SCP_TavernChannels.TrySetCategory(aRoot, aRoom, iArgs.Get("category"), out string? aErr))
                    return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aErr);
                string aNow = SCP_TavernChannels.LoadSettings(aRoot, aRoom).Category;   // 回讀
                var aR = SCP_CmdResult.Success(aNow.Length == 0 ? $"✅ `{aRoom}` 改成未分類（回讀）" : $"✅ `{aRoom}` 的分類 ＝ **{aNow}**（回讀）");
                aR.AddValue("category", aNow);
                return aR;
            }

            // archive / unarchive
            bool aWant = aOp == "archive";
            if (!SCP_TavernChannels.TrySetArchived(aRoot, aRoom, aWant, out string? aArcErr))
                return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aArcErr);
            bool aIs = SCP_TavernChannels.IsArchived(aRoot, aRoom);
            var aOk = SCP_CmdResult.Success(aIs ? $"✅ `{aRoom}` 已封存（資料夾已搬到 rooms_archive/；回讀）" : $"✅ `{aRoom}` 已取消封存（回讀）");
            aOk.AddValue("archived", aIs ? "1" : "0");
            return aOk;
        }

        static SCP_CmdResult List(string iRoot, bool iIncludeArchived)
        {
            List<SCP_ChannelInfo> aAll = SCP_TavernChannels.ListChannels(iRoot, iIncludeArchived);
            var aR = SCP_CmdResult.Success($"# 頻道（{aAll.Count}{(iIncludeArchived ? "，含封存" : "，不含封存")}）");
            aR.Lines.Add("| 頻道 | 分類 | 封存 | 最新 seq | 最後活動 |");
            aR.Lines.Add("|---|---|---|---:|---|");
            foreach (SCP_ChannelInfo c in aAll)
            {
                string aCat = c.Settings.Category.Length == 0 ? "（未分類）"
                    : c.CategoryMissing ? $"**{c.Settings.Category}**（⚠ 不在分類清單）" : c.Settings.Category;
                aR.Lines.Add($"| `{c.Room}` | {aCat} | {(c.ArchiveConflict ? "**撞名**（rooms/ 與 rooms_archive/ 都有）" : c.Settings.Archived ? "封存" : "")} | {c.LastSeq} | {(c.LastTs.Length > 0 ? c.LastTs : "—")} |");
            }
            aR.AddValue("channels", aAll.Count.ToString(CultureInfo.InvariantCulture));
            aR.AddValue("archived", aAll.Count(c => c.Settings.Archived).ToString(CultureInfo.InvariantCulture));
            return aR;
        }

        static SCP_CmdResult Categories(string iRoot)
        {
            List<SCP_ChannelCategory> aCats = SCP_TavernChannels.LoadCategories(iRoot, out string? aErr);
            if (aErr != null) return SCP_CmdResult.Fail(1, "✗ " + aErr);
            var aR = SCP_CmdResult.Success($"# 頻道分類（{aCats.Count}）");
            if (aCats.Count == 0) aR.Lines.Add("（還沒有任何分類 ⇒ `op=add-category --arg name=<分類>` 新增）");
            foreach (SCP_ChannelCategory c in aCats)
            {
                int aUsed = SCP_TavernChannels.RoomsUsingCategory(iRoot, c.Name).Count;
                aR.Lines.Add($"- **{c.Name}**　{aUsed} 個頻道{(c.Description.Length > 0 ? "　— " + c.Description : "")}");
            }
            aR.AddValue("categories", aCats.Count.ToString(CultureInfo.InvariantCulture));
            return aR;
        }
    }
}
