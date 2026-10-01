// 區塊職責：「你現在在自由時間中」的流程提示 —— 活動類 Cmd 在自己的回傳值尾端掛一段。
// 物理意義：移植自 UCL `UCL_FreeTimeHint`（TASK-0354：NoteLesson 搬到 Senate 時一起帶過來；
//           Editor 那份仍掛在 Sculpture／Glossary 上，兩份文字**逐字相同**，改一份要改另一份）。
//           判準與掛載條件（活動入口、有 markdown 回傳面、拿得到 persona）見 Editor 那份檔頭，⛔ 這裡不重抄。
// 數值影響：純輸出。不在自由時間時一個字都不印（無關的 Cmd 每次多一段噪音，會讓人開始略過整個區塊）。
//           ⚠ 與 Editor 版唯一的差別：換行一律 `\n`（Editor 版用 AppendLine ⇒ Windows 上是 `\r\n`）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Text;
using SCP.Core.Paths;

namespace SCP.Core.Session
{
    public static class SCP_FreeTimeHint
    {
        /// <summary>
        /// 若 <paramref name="iPersona"/> 此刻在自由時間中，往 <paramref name="ioReport"/> 尾端附一段「▶ 下一步」。
        /// 回傳是否有附；查詢失敗回 false 並把原因放進 <paramref name="oWarning"/>（⛔ 不靜默：「沒印」與「查不到」不同形）。
        /// </summary>
        public static bool Append(StringBuilder ioReport, SCP_DataRoot iRoot, string? iPersona, out string oWarning)
        {
            oWarning = "";
            if (ioReport == null || string.IsNullOrEmpty(iPersona)) return false;
            try
            {
                var aS = SCP_ActivitySessionStore.FindRunning(iRoot, iPersona, DateTime.Now);
                if (aS == null || aS.kind != SCP_ActivitySessionKind.FreeTime) return false;
                ioReport.Append('\n');
                ioReport.Append("## ▶ 你在自由時間中（到 ").Append(aS.until_local).Append(" —— 時間還沒到，挑下一項活動）\n");
                ioReport.Append("- 這件活動還要再走一步 → 再跑一次同一支 Cmd（活動是一步一步的，不必一次做完）。\n");
                ioReport.Append("- 這件活動告一段落 → `run FreeTimeActivity --arg op=done --arg persona=").Append(iPersona)
                        .Append(" [--arg-file body=<一句心得>]`\n");
                ioReport.Append("- 之後換骰（**順便讀未讀訊息、順便跟同事講話**）→ `run FreeTime --arg step=next --arg persona=")
                        .Append(iPersona).Append(" [--arg-file body=<想說的話>]`\n");
                ioReport.Append("- **截止是軟的**：時間到不打斷進行中的活動；到期時換骰那一步會自己宣布收工並結算。\n");
                return true;
            }
            catch (Exception e)
            {
                oWarning = "[FreeTimeHint] 附掛失敗（" + iPersona + "）：" + e.Message;
                return false;
            }
        }
    }
}
