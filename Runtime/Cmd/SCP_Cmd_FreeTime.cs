// 區塊職責：`cmd free-time` —— 自由時間流程的 Senate 入口（TASK-0360，**不需要 Editor**）。
// 物理意義：同一支 Cmd 以 step 分步：start（註冊 until＋發限時券＋開場擲骰＋宣告）→ [做活動] →
//          next（活動事件自然結束時跑：未到期重擲、到期收工）→ end（提前收工，附 reason）；
//          list／shuffle／show 是純參考查詢（不進場、不發券、不寫 session、不發酒館）。
//          邏輯本體在 `SCP.Core.FreeTime`；本檔只做「參數 → 現場 → 分派」。
// 數值影響：見 SCP_FreeTimeFlow 檔頭（session 檔／券（經 voucher Cmd）／酒館（經 tavern-post Cmd）／回傳檔）。
// ⚠ 與 Unity 版刻意的差異：
//   ① `body` / `roll` **宣告成參數**（Unity 靜默接受未宣告的參數；本系統未宣告的名字一律擋）。
//   ② 數值常數改讀 `<data_root>/FreeTime/freetime_settings.json`（SCP_FreeTimeSettings；檔不存在就說「使用預設值」）。
//   ③ 酒館未讀段改成**先落回傳檔、再推游標**（Unity 版是組段落的同時就推了）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System.Collections.Generic;
using SCP.Core.FreeTime;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_FreeTime : SCP_Cmd
    {
        public override string Name => "free-time";
        public override string Category => SCP_CmdCategory.Routine;

        public override string Summary => "自由時間流程（step=start/next/end ＋ 純參考查詢 list/shuffle/show）—— **不需要 Editor**";

        public override string Details =>
            "正常流程（agent 視角）：\n"
            + "  ① " + SCP_CmdRegistry.Invoke("free-time --arg step=start --arg persona=<P> --arg until=<HH:mm>") + "\n"
            + "  ② （做活動：" + SCP_CmdRegistry.Invoke("free-time-activity --arg op=pick|step|done …") + "）\n"
            + "  ③ 活動事件自然結束時：" + SCP_CmdRegistry.Invoke("free-time --arg step=next --arg persona=<P>") + "（未到期重擲／到期收工）\n"
            + "  ④ 提前收工（除非 Tim 明確指示，不要用）：" + SCP_CmdRegistry.Invoke("free-time --arg step=end --arg persona=<P> --arg reason=<一句>") + "\n"
            + "· 時間感由 Cmd 供給（每步回傳三個時間欄）—— **截止是軟的**：時間到不打斷進行中的活動，最後一件做完跑 next 才收工。\n"
            + "· step=next 可帶 `body`（併進換骰宣告同一則，長內文走 `--arg-file body=<檔>`）；`roll=0` ＝ 只讀訊息、不換骰。\n"
            + "· step=next 的回傳檔含酒館未讀，**落檔之後**推進已讀游標（回傳檔寫不出來就不推）。\n"
            + "· 限時券（每場 `pixels_per_session` 張，到 until＋緩衝作廢）只經 `voucher` Cmd（Server 單一寫入端）；\n"
            + "  發券失敗 ⇒ session **回滾**並回 `## failed`。收工時用量查無就印查無（⛔ 不以發放量推導，TASK-0195／0198）。\n"
            + "· 設定檔：`<data_root>/FreeTime/freetime_settings.json`（不存在 ⇒ 用預設值並在回傳檔說出來）。\n"
            + "· 回傳檔：`letters/<P>/cmd/freetime_<step>.md`（＋ start／next 的配對簡報 `freetime_partners.md`）。\n"
            + "exit：0 成功／2 被守衛擋下（回傳檔有 reason 與出口；什麼都沒做）／1 發券失敗（session 已回滾）／70 例外或收工沒落盤。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("free-time --arg step=start --arg persona=Template --arg until=23:59");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("step", "start | next | end | list | shuffle | show", iRequired: true,
                               iChoices: new[] { "start", "next", "end", "list", "shuffle", "show" }),
            new SCP_CmdArgSpec("persona", "誰的自由時間（全步驟必填 —— ⚠ 不猜身分：多租戶環境，預設值是裝填好的槍）", iRequired: true),
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）—— senate CLI 沒給時用「路徑管理」頁那一格補上並印出來", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（絕對路徑）—— senate CLI 沒給時用設定檔補上", iRequired: true),
            new SCP_CmdArgSpec("activities_root", "自由時間活動 md 目錄（絕對路徑）—— 宿主照描述表的 FreeTimeActivitiesRoot 自動填（TASK-0390：搬進 Senate）", iRequired: true),
            new SCP_CmdArgSpec("until", "step=start 必填：截止時刻 HH:mm（本地；已過且超過 12 小時視為跨日）"),
            new SCP_CmdArgSpec("reason", "step=end 選填：提前收工的理由（一句話 —— 形狀要可觀測）"),
            new SCP_CmdArgSpec("id", "step=show 必填：活動 id"),
            new SCP_CmdArgSpec("count", "step=shuffle 選填：只列前 N 項（截在排序之後）"),
            new SCP_CmdArgSpec("body", "step=next 選填：想跟同事說的話（併進換骰宣告同一則；roll=0 時單獨發 tag=chat）。長內文走 --arg-file"),
            new SCP_CmdArgSpec("roll", "step=next 選填：0 ＝ 只讀訊息、不換骰（不加輪次、不重擲、不發換骰公告）", iDefault: "1",
                               iChoices: new[] { "0", "1" }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aStep = iArgs.Get("step").Trim().ToLowerInvariant();
            string aPersona = iArgs.Get("persona").Trim();
            if (!SCP_Cmd_FreeTimeActivity.IsSafePersona(aPersona))
                return SCP_CmdResult.Fail(2, "✗ persona 不合法（不可含 `/` `\\` `:` 或是 `.` / `..`）：'" + aPersona + "'");

            var aCtx = new SCP_FreeTimeContext(iArgs.Get("data_root").Trim(), iArgs.Get("letters_root").Trim(),
                                               iArgs.Get("activities_root").Trim());
            SCP_CmdResult aRes;
            switch (aStep)
            {
                case "start": aRes = SCP_FreeTimeFlow.Start(aCtx, aPersona, iArgs.Get("until").Trim()); break;
                case "next":
                    aRes = SCP_FreeTimeFlow.Next(aCtx, aPersona, false, "", iArgs.Get("body"), iArgs.Get("roll"));
                    break;
                case "end": aRes = SCP_FreeTimeFlow.Next(aCtx, aPersona, true, iArgs.Get("reason").Trim(), "", "1"); break;
                case "list": aRes = SCP_FreeTimeFlow.List(aCtx, aPersona); break;
                case "shuffle": aRes = SCP_FreeTimeFlow.Shuffle(aCtx, aPersona, iArgs.Get("count")); break;
                case "show": aRes = SCP_FreeTimeFlow.Show(aCtx, aPersona, iArgs.Get("id").Trim()); break;
                default:
                    return SCP_CmdResult.Fail(2, "✗ step 必為 start|next|end|list|shuffle|show（got '" + aStep + "'）");
            }
            // 設定來源印在 CLI 輸出（回傳檔裡也有）—— ⛔「我用的是預設」與「我用的是你設的」不准安靜。
            if (aCtx.SettingsError != null) aRes.Lines.Add("⚠ 自由時間設定：" + aCtx.SettingsError);
            return aRes;
        }
    }
}
