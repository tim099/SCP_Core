// 區塊職責：自由時間回傳檔裡「**跟人有關**」的三段 —— 在線同事、配對簡報（另落一檔）、酒館未讀（推游標）。
// 物理意義：移植自 Unity `Cmd_FreeTime.AppendOnlineSection` / `WritePartnerBrief` / `AppendInboxSection` /
//          `AppendTavernCatchupSection`（TASK-0360）。自由時間有一半的活動**要有人才成立**（下棋／TRPG／聊天），
//          「誰在」以前只能自己去跑 catchup 才知道 —— 等於把一個能決定選哪個活動的事實放在骰面之外。
// 數值影響：
//   · 在線同事／配對簡報：**唯讀**，⛔ 不推進任何 cursor（配對簡報落 `cmd/freetime_partners.md`）。
//     「自動幫你讀掉」跟「幫你看見」是兩件事，這兩段只做後者。
//   · 酒館未讀：**會推已讀游標**（Tim 2026-08-18：換骰是高頻動作，只看不推的話未讀會整場堆積）。
//     ⚠ 順序不可反：**先把回傳檔寫下去、再推游標** —— 反過來的話回傳檔寫入失敗時訊息已被標成已讀，
//       那批訊息永遠不會再出現在任何人的未讀裡，而且沒有錯誤訊息。⇒ 本檔只**組**這一段並交出「可推到哪」，
//       推游標由呼叫端在回傳檔落地之後呼叫 <see cref="AdvanceUnread"/>（Unity 版是印出來的同時就推了 —— 那是本次刻意改掉的一格）。
// ⚠ 在線判準走 `SCP_PersonaLetters.Scan`（**lock 檔在＝在線**；與 Unity `UCL_ActivePersonaLocks.ListOnline` 同一個判準）。
//   ⛔ 不用 persona registry 的 status 欄：登出流程沒走完時 status 會停在 online，拿它當來源會 @ 到不在的人。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace SCP.Core.FreeTime
{
    /// <summary>「這一段印出來了，回傳檔落地後可以把游標推到哪」。</summary>
    public sealed class SCP_FreeTimeUnread
    {
        public int Shown;
        /// <summary>可安全推進到的水位；null ＝ 不得推進（0 筆／積壓超過回捲上限／讀取失敗）。</summary>
        public string? NewestTs;
        public bool Truncated;
        public bool ReadFailed;
    }

    public static class SCP_FreeTimePartners
    {
        /// <summary>回傳檔裡游標那一行的佔位字串 —— 回傳檔先帶著它落地，推完游標再換成讀回結果。</summary>
        public const string CursorPlaceholder = "- 游標：推進中…（若停在這行，代表推進那一步沒跑完 —— 下次會重讀這一段）";

        /// <summary>其他在線同事（排除自己）。讀不了的 lock 數另回報（⛔ 不靜默丟掉：「沒列」≠「不在線」）。</summary>
        static List<SCP_PersonaStatus> OnlineOthers(SCP_FreeTimeContext iCtx, string iSelf, out int oUnknown, out List<string> oProblems)
        {
            oUnknown = 0;
            SCP_PersonaScan aScan = SCP_PersonaLetters.Scan(iCtx.LettersRootRaw);
            oProblems = new List<string>(aScan.Problems);
            var aOut = new List<SCP_PersonaStatus>();
            foreach (var p in aScan.Personas)
            {
                if (string.Equals(p.Name, iSelf, StringComparison.OrdinalIgnoreCase)) continue;
                if (p.Online == SCP_PersonaOnline.Online) aOut.Add(p);
                else if (p.Online == SCP_PersonaOnline.Unknown) oUnknown++;
            }
            aOut.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));   // Unity 版用 Ordinal
            return aOut;
        }

        // ===========================================================
        // 區塊職責：「現在還有誰在線」（Tim 2026-08-14）。
        // 數值影響：只印。清單為空時**明說是空的並附上「空≠沒人」** —— 空清單被讀成「今天沒人」比讀成「查不到」危險，
        //          前者會讓人不去問。
        // ===========================================================
        public static void AppendOnlineSection(StringBuilder ioR, SCP_FreeTimeContext iCtx, string iSelf)
        {
            List<SCP_PersonaStatus> aOthers;
            int aUnknown;
            List<string> aProblems;
            try { aOthers = OnlineOthers(iCtx, iSelf, out aUnknown, out aProblems); }
            catch (Exception e)
            {
                ioR.AppendLine($"## 在線同事\n- ⚠ 讀取失敗（{e.Message}）—— 不代表沒人，代表沒讀到。");
                return;
            }
            ioR.AppendLine($"## 在線同事（{aOthers.Count} 位 —— 約棋局 / TRPG / 聊天找得到人）");
            foreach (string p in aProblems) ioR.AppendLine("- ⚠ " + p + " —— 不代表沒人，代表沒讀到。");
            if (aOthers.Count == 0)
            {
                ioR.AppendLine("- （查不到其他人的 lock）⚠ **空 ≠ 今天沒人**，只代表現在讀不到在線紀錄 ——");
                ioR.AppendLine("  想找人就照樣去酒館問一聲，別把空清單當成「不用問了」。");
            }
            foreach (var l in aOthers)
            {
                string aAgent = string.IsNullOrEmpty(l.Agent) ? "?" : l.Agent;
                string aActual = string.IsNullOrEmpty(l.ActualAgent) ? "" : $" / {l.ActualAgent}";
                // 「誰在線」跟「誰此刻有空一起玩」是兩件事 —— 只給前者的話，配對對象仍要自己一個個去查。
                bool aFree = iCtx.IsInFreeTime(l.Name);
                ioR.AppendLine($"- **@{l.Name}**（{aAgent}{aActual}）{(aFree ? " 🎫 **自由時間中**" : "")}");
            }
            if (aUnknown > 0)
                ioR.AppendLine($"- ⚠ 另有 {aUnknown} 個 lock 讀不了（壞檔／被佔用）—— 沒列進來，不代表他們不在線");
            if (aOthers.Count > 0)
                ioR.AppendLine("- 需要對手的活動（下棋 / TRPG）先 @ 一聲再開局 —— 開了才問等於替對方決定了他的自由時間。");
        }

        // ===========================================================
        // 區塊職責：配對簡報落檔 ＋ 在主回傳檔指路（形狀對齊 stream-watch：細節落檔、主回傳只指路）。
        // 物理意義：**指路要帶數字** —— 只寫「詳見某檔」沒有東西告訴人值不值得點開。
        // 數值影響：寫 `cmd/freetime_partners.md`（**唯讀產生**，不推進酒館已讀 cursor；每次 start／next 覆寫）。
        // ===========================================================
        public static string? AppendPartnerBriefSection(StringBuilder ioR, SCP_FreeTimeContext iCtx, string iPersona)
        {
            var (aPath, aOnline, aFree, aInbox, aErr) = WritePartnerBrief(iCtx, iPersona);
            ioR.AppendLine("## 配對簡報（要對手的活動從這裡挑人）");
            if (aPath == null)
            {
                ioR.AppendLine($"- ⚠ 簡報落檔失敗（{aErr}）—— 在線清單見上一段，inbox 請自行跑 catchup。");
                return null;
            }
            ioR.AppendLine($"- 在線 **{aOnline}** 位｜其中 **{aFree}** 位也在自由時間｜酒館 inbox **{aInbox}** 筆待處理");
            ioR.AppendLine($"- 📄 **Read `{aPath}`** —— 誰在線 ✕ 誰也在自由時間 ✕ 跟誰有沒下完的棋 ✕ 誰在等你回話");
            ioR.AppendLine("- ⚠ 本簡報**唯讀**，不推進酒館已讀 cursor。要完整未讀訊息另跑 catchup（簡報內附指令）——");
            ioR.AppendLine("  自動幫你讀掉跟幫你看見是兩件事，這裡只做後者。");
            return aPath;
        }

        static (string? Path, int Online, int Free, int Inbox, string Error) WritePartnerBrief(SCP_FreeTimeContext iCtx, string iPersona)
        {
            string aPath = iCtx.PayloadPath(iPersona, "partners");
            var aB = new StringBuilder();
            aB.AppendLine($"# FreeTime 配對簡報 — {iPersona}  ts=`{SCP_Morning.NowLocal()}`（本地時間）");
            aB.AppendLine();
            aB.AppendLine("> 誰在線、誰此刻也在自由時間、跟誰還有沒下完的棋、酒館還有誰在等你回話 ——");
            aB.AppendLine("> 要對手的活動（下棋 / TRPG / 聊天）從這裡挑人。");
            aB.AppendLine("> ⚠ 本檔**唯讀產生**，不推進酒館已讀 cursor；每次 start / next 覆寫。");
            aB.AppendLine();

            int aOnline = 0, aFree = 0;
            aB.AppendLine("## 在線同事");
            try
            {
                List<SCP_PersonaStatus> aOthers = OnlineOthers(iCtx, iPersona, out int aUnknown, out List<string> aProblems);
                foreach (string p in aProblems) aB.AppendLine("⚠ " + p + " —— **不代表沒人，代表沒讀到**。");
                if (aOthers.Count == 0)
                {
                    aB.AppendLine("（查不到其他人的 lock）");
                    aB.AppendLine();
                    aB.AppendLine("⚠ **空 ≠ 今天沒人**，只代表現在讀不到在線紀錄。想找人照樣去酒館問一聲 ——");
                    aB.AppendLine("把空清單當成「不用問了」，是這份簡報唯一能造成的傷害。");
                }
                else
                {
                    aB.AppendLine("| persona | agent | 自由時間中 | 與你的棋局 |");
                    aB.AppendLine("|---|---|---|---|");
                    foreach (var l in aOthers)
                    {
                        aOnline++;
                        bool aInFt = iCtx.IsInFreeTime(l.Name);
                        if (aInFt) aFree++;
                        aB.AppendLine($"| **@{l.Name}** | {(string.IsNullOrEmpty(l.Agent) ? "?" : l.Agent)} "
                                      + $"| {(aInFt ? "🎫 是" : "—")} | {SCP_FreeTimeGating.ChessNoteWith(iCtx, iPersona, l.Name)} |");
                    }
                    aB.AppendLine();
                    aB.AppendLine("- 「自由時間中」＝對方此刻也在挑活動，約局最容易接得上。");
                    aB.AppendLine("- 開新局前先 @ 一聲 —— 開了才問等於替對方決定了他的自由時間。");
                }
                if (aUnknown > 0) aB.AppendLine($"- ⚠ 另有 {aUnknown} 個 lock 讀不了 —— 沒列進來，不代表他們不在線");
            }
            catch (Exception e)
            {
                aB.AppendLine($"⚠ 在線清單讀取失敗（{e.Message}）—— **不代表沒人，代表沒讀到**。");
            }

            aB.AppendLine();
            int aInbox = AppendInboxSection(aB, iCtx, iPersona);

            aB.AppendLine();
            aB.AppendLine("## next");
            aB.AppendLine("- 要**完整未讀訊息**（含非 @ 你的近況）→ `" + SCP_CmdRegistry.Invoke("tavern-catchup --arg persona=" + iPersona) + "`");
            aB.AppendLine("  ⚠ 那支**會推進已讀 cursor**（跑了就算看過），所以本簡報不替你跑 —— 讀不讀由你決定。");
            aB.AppendLine($"- inbox 處理完歸檔 → `python <UCL_Core>/Tools~/AgentCommands/CommandResolver/inbox_ack.py --agent {iPersona}`");
            aB.AppendLine("- 約局 / 回話一律走酒館 `" + SCP_CmdRegistry.Invoke("tavern-post") + "`（chat 邊回不算數 —— 對方看的是酒館）。");

            try { SCP_CmdPayload.Write(aPath, aB.ToString()); }
            catch (Exception e) { return (null, aOnline, aFree, aInbox, e.GetType().Name + ": " + e.Message); }
            return (aPath, aOnline, aFree, aInbox, "");
        }

        /// <summary>
        /// durable inbox（`rooms/tavern/inbox/&lt;persona&gt;.md`）的待處理項 —— **@ 你而且還沒歸檔**的訊息。
        /// 讀它不消耗任何未讀狀態（歸檔是 inbox_ack 的顯式動作）。只取標題行（`## [seq=N] …`），不搬內文。回待處理筆數。
        /// </summary>
        static int AppendInboxSection(StringBuilder ioB, SCP_FreeTimeContext iCtx, string iPersona)
        {
            string aInboxPath = SCP_TavernInbox.InboxPath(iCtx.DataRootRaw, "tavern", iPersona);
            if (!SCP_AtomicFileRead.TryReadAllText(aInboxPath, out string aText, out SCP_FileReadState aState))
            {
                ioB.AppendLine("## 酒館 inbox");
                ioB.AppendLine(aState == SCP_FileReadState.Missing
                    ? $"- 沒有 inbox 檔（`{aInboxPath}`）—— 代表目前沒有 @ 你且待處理的訊息。"
                    : $"- ⚠ 讀取失敗（{SCP_AtomicFileRead.DescribeBusy(aInboxPath)}）—— 沒讀到不等於沒有。");
                return 0;
            }
            var aHeads = new List<string>();
            foreach (string aLine in aText.Replace("\r", "").Split('\n'))
                if (aLine.StartsWith("## [seq=", StringComparison.Ordinal)) aHeads.Add(aLine.Substring(3).Trim());

            ioB.AppendLine($"## 酒館 inbox（{aHeads.Count} 筆待處理 · @ 你的訊息 · 唯讀不歸檔）");
            if (aHeads.Count == 0) { ioB.AppendLine("- 清空狀態 —— 沒有待處理的 @。"); return 0; }
            // 只列最新 N 筆（設定 inbox_heads_shown）：簡報要能一眼看完；全部都在檔案裡，路徑就在下面。
            int aStart = Math.Max(0, aHeads.Count - iCtx.Settings.InboxHeadsShown);
            for (int i = aHeads.Count - 1; i >= aStart; i--) ioB.AppendLine($"- {aHeads[i]}");
            if (aStart > 0) ioB.AppendLine($"- …另有 **{aStart} 筆較舊**（最舊的在檔案頂端）");
            ioB.AppendLine($"- 全文：`{aInboxPath}`");
            return aHeads.Count;
        }

        // ===========================================================
        // 區塊職責：把**未讀酒館訊息**併進換骰回傳檔（骰面與訊息要在**同一份檔** —— 分兩處＝一定有一處不會被讀到）。
        // 數值影響：判準與 `SCP_TavernCatchup` 同一套（排除自己；自由時間**看得到**酒保廣播 —— 打款／結算也是同事動態；
        //          @ 自己用長截斷，截斷長度讀 `ChatTavern/render_settings.json` 同鍵同預設同夾值）。
        //          ⚠ 本函式**不推游標** —— 只交出水位；推進見 <see cref="AdvanceUnread"/>。
        // ===========================================================
        public static SCP_FreeTimeUnread AppendUnreadSection(StringBuilder ioR, SCP_FreeTimeContext iCtx, string iPersona)
        {
            var aOut = new SCP_FreeTimeUnread();
            var aShown = new List<SCP_TavernMessage>();
            int aHiddenSelf = 0;
            try
            {
                var aUnread = SCP_TavernCursor.ReadUnread(iCtx.DataRootRaw, iPersona, "tavern", out string? aNewest, out bool aTrunc);
                aOut.NewestTs = aNewest;
                aOut.Truncated = aTrunc;
                foreach (var m in aUnread)
                {
                    if (string.Equals(m.SenderPersona, iPersona, StringComparison.OrdinalIgnoreCase)) { aHiddenSelf++; continue; }
                    aShown.Add(m);
                }
            }
            catch (Exception e)
            {
                // 讀不到 ≠ 沒訊息 —— 空白會被讀成「今天很安靜」，那是兩件事。游標不推進。
                ioR.AppendLine($"## 🍺 酒館未讀：⚠ **讀取失敗**（{e.Message}）—— 這不代表沒人講話；游標未推進");
                aOut.NewestTs = null;
                aOut.ReadFailed = true;
                return aOut;
            }
            aOut.Shown = aShown.Count;
            ioR.AppendLine($"## 🍺 酒館未讀　**{aShown.Count}** 筆"
                + (aHiddenSelf > 0 ? $"（排除自己 {aHiddenSelf}）" : "")
                + "　—— **本段寫進回傳檔之後即推進已讀游標**（下一行會說推到哪）");
            if (aOut.Truncated)
                ioR.AppendLine("- ⚠ **一次交付不完** —— 這批是最舊的那段，更新的還留在未讀裡（不會遺失）。");
            if (aShown.Count == 0) ioR.AppendLine("- （沒有未讀）");
            ReadClips(iCtx.DataRootRaw, out int aClip, out int aClipMention);
            foreach (var m in aShown)
            {
                bool aMentioned = (m.Body ?? "").IndexOf("@" + iPersona, StringComparison.OrdinalIgnoreCase) >= 0;
                // 標記不是裝飾：不標的話「同一份清單裡有的長有的短」看起來像截斷壞掉，而其實是規則在生效。
                AppendMsg(ioR, m, aMentioned ? "🔔 **@你**" : "", aMentioned ? aClipMention : aClip);
            }
            ioR.AppendLine(CursorPlaceholder);
            ioR.AppendLine("- inbox（@ 我的待處理）不在本段範圍 —— 那走 `" + SCP_CmdRegistry.Invoke("tavern-catchup --arg persona=" + iPersona) + "`。");
            return aOut;
        }

        /// <summary>
        /// 回傳檔落地**之後**才呼叫：推游標並讀回確認（走 `SCP_TavernCatchup.AdvanceAfterWrite`，與 tavern-catchup 同一支）。
        /// 回傳要替換掉 <see cref="CursorPlaceholder"/> 的那一行，與讀回的值。
        /// </summary>
        public static (string Line, string? AdvancedTo) AdvanceUnread(SCP_FreeTimeContext iCtx, string iPersona, SCP_FreeTimeUnread iUnread)
        {
            if (iUnread.ReadFailed) return ("- 游標：**未推進**（未讀讀取失敗）", null);
            var aBuilt = new SCP_TavernCatchupResult { NewestTs = iUnread.NewestTs, Truncated = iUnread.Truncated, Unread = iUnread.Shown };
            return SCP_TavernCatchup.AdvanceAfterWrite(iCtx.DataRootRaw, iPersona, aBuilt, true);
        }

        static void AppendMsg(StringBuilder sb, SCP_TavernMessage m, string iMark, int iClip)
        {
            string tag = m.Meta.TryGetValue("tag", out string? tg) && !string.IsNullOrEmpty(tg) ? $" «{tg}»" : "";
            string name = string.IsNullOrEmpty(m.SenderName) ? (string.IsNullOrEmpty(m.SenderId) ? "?" : m.SenderId) : m.SenderName;
            string persona = string.IsNullOrEmpty(m.SenderPersona) ? "" : "@" + m.SenderPersona;
            sb.AppendLine($"- **[seq {m.Seq}]** {LocalHm(m.Ts)} **{name}{persona}**{tag} {iMark}");
            string body = (m.Body ?? "").Replace("\r", "").Replace("\n", " ⏎ ").Trim();
            if (iClip > 0 && body.Length > iClip) body = body.Substring(0, iClip) + $"…（全文 {body.Length} 字）";
            sb.AppendLine($"    {body}");
        }

        static string LocalHm(string iTs)
        {
            if (DateTime.TryParse(iTs, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t))
                return t.ToLocalTime().ToString("MM-dd HH:mm:ss");
            return "??:??:??";
        }

        // 顯示截斷（與 SCP_TavernCatchup／Editor UCL_ChatTavernSettings 同鍵同預設同夾值；那支是 private，這裡照抄讀法）。
        static void ReadClips(string iDataRoot, out int oNormal, out int oMention)
        {
            oNormal = 600; oMention = 1500;
            try
            {
                string aPath = Path.Combine(iDataRoot, "ChatTavern", "render_settings.json");
                if (!SCP_AtomicFileRead.TryReadAllText(aPath, out string aText, out _)) return;
                var aJd = SCP_JsonData.Parse(aText);
                oNormal = Clip(aJd.GetInt("message_body_clip", 600));
                oMention = Clip(aJd.GetInt("message_body_clip_mentioned", 1500));
            }
            catch (Exception) { /* 只影響顯示長度，讀不到就用預設 */ }
        }

        static int Clip(int v) => v <= 0 ? 0 : Math.Max(80, Math.Min(4000, v));
    }
}
