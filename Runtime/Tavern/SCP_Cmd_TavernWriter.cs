// 區塊職責：`tavern-writer` —— 看／切酒館寫入端的開關（TASK-0106 / D10）。
// 物理意義：開關住在資料根的 `agent_settings.json`（`tavern.writer`）。本支**只動那一格**，
//           ⛔ 不寫任何一則訊息、不碰 `ChatTavern/` 底下任何東西 ⇒ 它不是第二個寫入端。
// 數值影響：`op=show` 純讀；`op=set` 一次整檔 parse ＋ atomic replace（含回讀確認，由 SCP_JsonPrefs 負責）。
//
// ⚠ 切過去（`set=server`）**不會**檢查 Server 在不在 —— 那是刻意的：
//   「開關指向誰」與「那一顆活著沒」是兩個讀數，混成一句話的話，
//   「我還沒切」與「我切了但它掛了」會長得一樣，而兩者的處置相反。
//   ⇒ 本支只負責前者，並在輸出裡明說它沒量後者。
#nullable enable
using System.Collections.Generic;
using SCP.Core.Cmd;

namespace SCP.Core.Tavern
{
    public sealed class SCP_Cmd_TavernWriter : SCP_Cmd
    {
        public override string Name => "tavern-writer";

        public override string Summary =>
            "看／切酒館寫入端開關（`tavern.writer` = editor | server）—— ⛔ 沒有自動降級，切換是人下的決定";

        public override string Details =>
            "落點：`<資料根>/agent_settings.json` 的 `tavern.writer`。\n"
            + "· 沒設定過 ⇒ 走 `editor`（今天的行為），而輸出會說它是預設值不是有人選的。\n"
            + "· 設成 `server` ⇒ 發文改由 Senate Server 單一寫入端處理；**Server 沒跑就整筆失敗**，\n"
            + "  ⛔ 不會退回 Editor 直寫（D10 丙，Tim 2026-09-20 拍板）。\n"
            + "· 認不得的值 ⇒ **報錯**，⛔ 不悄悄回預設（那會讓打錯字的人以為自己切過去了）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("tavern-writer --arg data_root=<資料根> --arg set=server");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("set", "要切成哪一邊；**不給＝只看不改**", iDefault: "",
                               iChoices: new[] { "", SCP_TavernWriteMode.ValueEditor, SCP_TavernWriteMode.ValueServer }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root").Trim();
            string aSet = iArgs.Get("set").Trim();
            string aPath = SCP_TavernWriteMode.SettingsPath(aDataRoot);

            SCP_TavernWriteModeRead aBefore = SCP_TavernWriteMode.Read(aDataRoot);

            if (aSet.Length == 0)
            {
                var aShow = aBefore.Ok
                    ? SCP_CmdResult.Success(aBefore.Describe())
                    : SCP_CmdResult.Fail(2, "✗ " + aBefore.Describe());
                aShow.Lines.Add("設定檔：" + aPath);
                aShow.Lines.Add("⚠ 本支**沒有量**「Server 在不在」—— 那一格走 `senate server status`。");
                aShow.AddValue("tavern_writer", aBefore.Ok
                    ? (aBefore.Host == SCP_TavernWriteHost.Server ? SCP_TavernWriteMode.ValueServer
                                                                  : SCP_TavernWriteMode.ValueEditor)
                    : "(讀不出來)");
                aShow.AddValue("explicit", aBefore.Ok && aBefore.IsExplicit ? "1" : "0");
                return aShow;
            }

            SCP_TavernWriteHost aTarget = aSet == SCP_TavernWriteMode.ValueServer
                ? SCP_TavernWriteHost.Server : SCP_TavernWriteHost.Editor;
            (bool aOk, string aMsg) = SCP_TavernWriteMode.Write(aDataRoot, aTarget);
            if (!aOk) return SCP_CmdResult.Fail(1, "✗ 寫不進去：" + aMsg, "設定檔：" + aPath);

            // 回讀 —— ⛔ 不拿「Write 回 true」當落盤的證據（那只是寫入端自己說的）。
            SCP_TavernWriteModeRead aAfter = SCP_TavernWriteMode.Read(aDataRoot);
            if (!aAfter.Ok || aAfter.Host != aTarget)
                return SCP_CmdResult.Fail(1,
                    "✗ 寫完回讀對不上：" + aAfter.Describe(), "設定檔：" + aPath);

            var aResult = SCP_CmdResult.Success(
                "✓ 已切換：" + (aBefore.Ok ? aBefore.Describe() : "(之前讀不出來)") + "　⇒　" + aAfter.Describe(),
                "設定檔：" + aPath);
            if (aTarget == SCP_TavernWriteHost.Server)
                aResult.Lines.Add("⚠ 從現在起發文由 Server 寫；**它沒跑的話發文會整筆失敗**（⛔ 不降級）"
                                  + " —— 先確認：`senate server status`／啟動：`senate server start --id "
                                  + "tavern`。");
            aResult.AddValue("tavern_writer", aSet);
            return aResult;
        }
    }
}
