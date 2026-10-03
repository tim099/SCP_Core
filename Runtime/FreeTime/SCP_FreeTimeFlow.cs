// 區塊職責：`free-time` 的六個 step —— start（守衛＋註冊＋發券＋開場擲骰＋宣告）／next（活動邊界：未到期重擲、到期收工）／
//          end（提前收工）／list・shuffle・show（純參考查詢）。
// 物理意義：移植自 Unity `Cmd_FreeTime`（Plan_FreeTime_Cmd.md，Tim 2026-08-13 拍板；TASK-0360 搬到 Senate）。
//          **時間感由 Cmd 供給**（每步回傳三個時間欄），agent 不自己心算 —— 時限判定只認時鐘，不認收束感（w44/w45 血證）。
//          step=next 的觸發點＝活動事件的自然結束（棋局終局／繪圖收筆／聊天告一段落）——
//          「完成的時刻」從 stop signal 變成回 loop 的通道。
// 數值影響：session 落 `<data_root>/sessions/<persona>.json`（一人一檔位；kind 是 json 欄位）；
//          限時券每場 `pixels_per_session` 張（走 voucher Cmd）；回傳檔 `letters/<P>/cmd/freetime_<step>.md`。
//          **blocked＝回傳檔落檔＋非零退出**（exit 2）；發券失敗＝回滾 session＋`## failed`（exit 1）。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Session;

namespace SCP.Core.FreeTime
{
    public static class SCP_FreeTimeFlow
    {
        /// <summary>被守衛擋下（沒有做任何事 —— 回傳檔照寫）。</summary>
        public const int ExitBlocked = 2;
        /// <summary>做了一半而失敗（發券失敗 ⇒ session 已回滾）。</summary>
        public const int ExitFailed = 1;

        static string Cmd(string iTail) => SCP_CmdRegistry.Invoke(iTail);

        // ===========================================================
        // 區塊：step=start —— 守衛 → session 註冊 → 場次 +1 → 限時券 → 開場擲骰 → 酒館宣告
        // 物理意義：自由時間是「登入後的狀態」—— lock 不存在即 blocked；既有 active 未到期即 blocked（不疊開）；
        //          已到期的殘留視為 stale 自動收掉再開新場（超時沒跑 next 的人不該被卡死在沒有出口的房間）。
        // ===========================================================
        public static SCP_CmdResult Start(SCP_FreeTimeContext iCtx, string iPersona, string iUntil)
        {
            string aPath = iCtx.PayloadPath(iPersona, "start");
            var aR = Header("start", iPersona);

            // 守衛①：必須在線（lock 檔在＝在線；與 Unity `UCL_AwakeningService.IsOnline` 同判準）
            if (!File.Exists(SCP_LettersPaths.SessionLockPath(iCtx.Letters, iPersona)))
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: '{iPersona}' 不在線（無 session lock）—— 自由時間是登入後的狀態");
                aR.AppendLine($"- exit: 先跑 {Cmd("morning-wake --arg persona=" + iPersona)}");
                return Blocked(iCtx, aPath, aR, "step=start blocked：persona 不在線");
            }

            // 守衛②：until 必填且可解析
            DateTime aNow = DateTime.Now;
            if (!TryParseUntil(iUntil, aNow, out DateTime aUntil, out string aUntilErr))
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: {aUntilErr}");
                aR.AppendLine("- how: --arg until=<HH:mm 本地時刻>（例 until=12:30；深夜跨日自動判定）");
                return Blocked(iCtx, aPath, aR, "step=start blocked：until 參數無效");
            }

            // 守衛③：同 kind 不疊開 —— 既有 active 且未到期 → blocked；已到期殘留 → 自動收掉（stale）
            SCP_FreeTimeSession? aOld = iCtx.LoadSession(iPersona);
            if (aOld != null && aOld.active)
            {
                DateTime? aOldEnd = SCP_ActivitySession.ParseIsoToLocal(aOld.end_ts);
                if (aOldEnd.HasValue && aNow <= aOldEnd.Value)
                {
                    aR.AppendLine("## blocked");
                    aR.AppendLine($"- reason: 已有進行中的自由時間 session（至 {aOldEnd.Value:HH:mm} 本地）—— 不疊開");
                    aR.AppendLine("- exit: 換活動跑 step=next；提前收工跑 step=end --arg reason=<一句>");
                    return Blocked(iCtx, aPath, aR, "step=start blocked：session 已存在");
                }
                // 到期殘留：自動收工（不宣告 —— 那場的收工時刻早已過去，補宣告只會誤導時間軸）
                iCtx.CloseSession(iPersona, aOld, "expired-stale-on-start", out _);
                aR.AppendLine($"- ℹ 偵測到過期殘留 session（{aOld.session_id}）已自動收掉，開新場。");
            }

            string aSessionId = "ft-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture) + "-" + iPersona;
            var aSession = new SCP_FreeTimeSession
            {
                persona = iPersona,
                session_id = aSessionId,
                start_ts = SCP_ActivitySession.NowIso(),
                end_ts = aUntil.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture),
                until_local = aUntil.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                rounds = 0,
                active = true,
                end_reason = "",
            };
            // 守衛④（TASK-0056）：**跨 kind** —— 走 store 的 TryStart（先查再寫）。
            // 🩸 守衛③ 走的是 `Load(…, FreeTime)`，它 filter kind ⇒ 一場進行中的觀影在它眼裡是 null ⇒ 放行
            //   ⇒ 下一行的 Save 會**靜默覆蓋**掉那場（2026-09-05 活體：觀影場消失、本 Cmd 回 Success、還發了開場宣告）。
            // ⚠ 被擋時**一個位元組都不寫** ⇒ 這裡直接退出，後面的券／擲骰／宣告一格都不會發生。
            if (!SCP_ActivitySessionStore.TryStart(iCtx.Data, iPersona, aSession, SCP_ActivitySessionKind.FreeTime,
                                                   aNow, out SCP_ActivitySession? aBlocker))
            {
                aR.AppendLine("## blocked");
                if (aBlocker == null)
                {
                    // ⚠ 兩種 false 不可同形：被別 kind 擋下（正常守衛）vs Save 自己失敗（磁碟出事）。
                    aR.AppendLine("- reason: session 寫入失敗（不是被別的場擋下）—— 路徑或磁碟有問題");
                    aR.AppendLine($"- exit: {Cmd("sessions --arg op=list")} 看目錄狀態，並確認資料根可寫：{iCtx.DataRootRaw}");
                }
                else
                {
                    aR.AppendLine("- reason: " + CrossKindReason(aBlocker, iPersona));
                    aR.AppendLine("- exit: " + CrossKindExit(aBlocker, iPersona));
                }
                return Blocked(iCtx, aPath, aR, "step=start blocked：已在別種 session");
            }

            // 飢餓度的時鐘：場次 +1。**不推它的話「幾場沒被選」永遠是 0，置頂規則會安靜地永不觸發。**
            // ⚠ 推在擲骰**之前** —— 本場算第 N 場，而本場的骰面就該用第 N 場的飢餓度。
            int aSessionsTotal = SCP_FreeTimeStatsIO.BumpSession(iCtx.Letters, iPersona, out string aBumpErr);

            // 限時券 ＝ **綁本場的限時繪圖券**（Tim 2026-08-18）—— 「用不完歸零」是到期的自然結果，不需要作廢路徑。
            // 🩸 TASK-0249（kiara 2026-09-18 實測）：券那條路不通時（build_mismatch／Server 沒跑…），session **早就寫上磁碟了**
            //   ⇒ 失敗訊息只講券的話，「它失敗了」與「它什麼都沒做」在回傳上同形，而磁碟上差一個 `active:true` ——
            //   下一次 start 會撞 blocked，對一個以為自己從來沒開成場的人來說那讀起來像系統壞了。
            // ⇒ 修法①：**連同 session 一起回滾**，兩件事分開指名。⛔ 不選「保留 session 只改訊息」（半套的場會佔住名額）。
            // ⚠ 已知代價（⛔ 不假裝沒有）：Server 那側有可能已經發完券才斷線 ⇒ 這裡多收一場、那批券留在帳上到期作廢 ——
            //   本層分辨不了，所以把券那一層的原文**轉述**，⛔ 不代它下結論。
            var aPrev = iCtx.CanvasBalance(iPersona);   // 先讀「還掛在身上的舊限時券」＝ 上一場沒用完的
            string? aGrantErr = iCtx.GrantFreeTimeVouchers(iPersona, aSessionId, aUntil, out string aExpiresIso);
            if (aGrantErr != null)
            {
                // ① 權威狀態先落地（照 SCP_ActivitySessionStore.CloseWithSettlement 那條拍板的次序）
                bool aRolledBack = iCtx.CloseSession(iPersona, aSession, "grant-failed-rollback", out _);
                aR.AppendLine("## failed");
                aR.AppendLine($"- session: `{aSessionId}`（至 {aUntil:HH:mm}）—— "
                              + (aRolledBack ? "**已回滾**（收掉，不佔「不疊開」的名額）"
                                             : "⚠ **回滾寫不進去**（檔上可能還掛著 active）—— 跑 step=end 收掉它"));
                aR.AppendLine($"- 🎟 限時券: **未發放**　券帳那一層的原文 ⇒ {aGrantErr}");
                aR.AppendLine(aRolledBack
                    ? "- ⇒ 那條路修好之後直接重跑 `step=start` 開新場；⛔ 不必先 `step=end`。"
                    : "- ⇒ 先 `step=end` 收掉殘留，再重跑 `step=start`。");
                AppendTail(aR, iCtx);
                var aFail = SCP_CmdResult.Fail(ExitFailed,
                    $"✗ free-time step=start 發券失敗 ⇒ session `{aSessionId}` {(aRolledBack ? "**已回滾**" : "**回滾失敗**")}、🎟 限時券**未發放**",
                    "  券帳那一層說：" + aGrantErr);
                Write(aFail, aPath, aR);
                return aFail.AddValue("session_id", aSessionId).AddValue("rolled_back", aRolledBack ? "1" : "0")
                            .AddValue("vouchers_granted", "0");
            }

            // 開場擲骰（優先層在前、層內仍隨機；做不成的已隱藏；時間不夠的降尾端）
            int aMinutes = (int)Math.Max(0, (aUntil - aNow).TotalMinutes);
            SCP_FreeTimeRoll aRoll = SCP_FreeTimeDice.Roll(iCtx, iPersona, aMinutes);
            int aPixels = iCtx.Settings.PixelsPerSession;
            DateTime aVoidAt = aUntil.AddMinutes(iCtx.Settings.VoucherGraceMinutes);

            // 酒館開場宣告（單則：時段＋券＋骰面）
            var aBody = new StringBuilder();
            aBody.AppendLine($"🎫 [{iPersona} 大小姐] 進入自由時間 — 至 **{aUntil:HH:mm}**（約 {aMinutes} 分鐘）｜🎟 限時券 {aPixels} 張已發放（到 {aVoidAt:HH:mm} 作廢）");
            aBody.AppendLine();
            SCP_FreeTimeDice.AppendPriorityNote(aBody, aRoll);
            aBody.AppendLine("開場擲骰 🎲 全清單隨機排序（僅供參考 — 自由意志優先）：");
            for (int i = 0; i < aRoll.List.Count; i++)
                aBody.AppendLine($"{i + 1}. {(aRoll.List[i].Priority ? "⭐ " : "")}{aRoll.List[i].TavernLine()}");
            aBody.AppendLine();
            aBody.AppendLine($"[{aRoll.Source}] 活動事件結束時跑 step=next 換骰面，時間到自動收工。");
            SCP_FreeTimePostResult aPost = iCtx.Post(iPersona, aBody.ToString(), "dice-roll-entry");

            AppendTimeFields(aR, aNow, aUntil);
            aR.AppendLine($"- session: `{aSessionId}`（state: `{iCtx.SessionPath(iPersona)}`）");
            aR.AppendLine(aSessionsTotal < 0
                ? $"- ⚠ 活動統計場次**推進失敗**（不影響本場，但飢餓置頂這一輪不準）：{aBumpErr}"
                : $"- 📊 本人自由時間累計 **第 {aSessionsTotal} 場**（統計欄 `{SCP_FreeTimeStatsIO.FieldPath(iCtx.Letters, iPersona)}`）");
            aR.AppendLine($"- 🎟 限時券: **{aPixels} 張**（`--pay auto` 會先花它們；付款回報裡它是 **`freetime` 欄**，不是另一個池；"
                          + $"**到期即作廢**，到 {aVoidAt:HH:mm}）"
                          + (aPrev.Ok && aPrev.Expiring > 0 ? $"　⚠ 上場還掛著 {aPrev.Expiring} 張未用（過期後由券帳本清掉）"
                             : !aPrev.Ok ? $"　（發券前的舊限時券讀不到：{aPrev.Detail}）" : ""));
            aR.AppendLine($"- ⚙ 本趟設定：{iCtx.SettingsLine()}（`{SCP_FreeTimeSettings.PathOf(iCtx.DataRootRaw)}`）");
            aR.AppendLine($"- 酒館開場宣告: {aPost.Describe()}");
            SCP_FreeTimePartners.AppendOnlineSection(aR, iCtx, iPersona);
            string? aPartners = SCP_FreeTimePartners.AppendPartnerBriefSection(aR, iCtx, iPersona);
            SCP_FreeTimeDice.AppendDiceSection(aR, aRoll, iPersona);
            aR.AppendLine("## next");
            aR.AppendLine("1. 從骰面挑活動開做（無明確意圖 → 前 3 名挑一；有明確意圖 → 自由意志優先，但開場 post 註明「本輪未跟骰」）。");
            // ⛔ 不准再教 `--wait-reply`（basecamp 2026-09-10）：那是 run_cmd.py 時代的旗標，現在打它會被 senate **靜默吃掉**
            //   （🩸 @kiara 2026-09-07 帶 wait_reply=180，實測 45 秒 —— 「✓ Success、exit 0」一應俱全而什麼都沒等到）。
            // ⭐ 2026-09-14（TASK-0160）：引擎有了 —— `tavern-wait`。⚠ 兩件事分開講，別讓「旗標死了」被讀成「功能沒了」。
            aR.AppendLine("2. ⭐ **要維持對話流就發動引擎**：`" + Cmd("tavern-wait --arg persona=" + iPersona
                          + " --arg timeout=<秒> --arg mention=1") + "`");
            aR.AppendLine("   ⚠ 它是**唯一擋得住 turn 的那一層**；`timeout` **預設 0 ＝ 一秒都不等**，"
                          + "不給秒數等於沒有引擎。等到人回話會**提早返回**，逾時回 exit 4（那是答案不是失敗）。");
            aR.AppendLine("   ⛔ 別打 `--wait-reply`／`--arg wait_reply=`：**那個旗標仍然是死的**，"
                          + "會被 senate **靜默吃掉**，「✓ Success、exit 0」一應俱全而**一秒都沒等到**（實測 180 → 45 秒）。");
            aR.AppendLine("   ⛔ 而**沒有人在線就別等** —— `timeout=180` 只是把 turn 燒掉三分鐘；"
                          + "先看骰面與 catchup 有沒有人，再決定要不要發動。");
            aR.AppendLine($"3. **活動事件自然結束時**（棋局終局／繪圖收筆／聊天告一段落）→ {Cmd("free-time --arg step=next --arg persona=" + iPersona)}");
            aR.AppendLine("   收工由這裡自動判定 —— **截止是軟的**：時間到不打斷進行中的活動，最後一件做完跑 next 才通知收工。");
            aR.AppendLine("4. step=end（提前收工）**除非 Tim 明確指示，不要用** —— 正常結束一律交給 step=next 對時鐘判定。");
            AppendContinueBlock(aR, iPersona);
            AppendTail(aR, iCtx);

            var aRes = SCP_CmdResult.Success(
                $"✓ free-time step=start：`{aSessionId}` 至 **{aUntil:HH:mm}**　🎟 {aPixels} 張限時券　開場宣告 {aPost.Describe()}");
            Write(aRes, aPath, aR);
            if (aPartners != null) aRes.AddOutput(aPartners);
            aRes.AddValue("session_id", aSessionId).AddValue("until_local", aSession.until_local)
                .AddValue("end_ts", aSession.end_ts)
                .AddValue("vouchers_granted", aPixels.ToString(CultureInfo.InvariantCulture))
                .AddValue("voucher_expires_at", aExpiresIso)
                .AddValue("sessions_total", aSessionsTotal.ToString(CultureInfo.InvariantCulture))
                .AddValue("post_seq", aPost.Seq.ToString(CultureInfo.InvariantCulture))
                .AddValue("settings_source", SettingsSource(iCtx));
            return aRes;
        }

        // ===========================================================
        // 區塊：step=next / step=end —— 活動邊界檢查點（到期判定在此對系統時鐘）
        // 物理意義：next 由「活動事件自然結束」觸發 —— 未到期＝重擲換下一件、到期＝收工；end＝人主動提前收工（reason 可觀測）。
        //          「過期的 session 再 next 一次」必須是收工不是報錯（超時回來的人要有出口）。
        // ===========================================================
        public static SCP_CmdResult Next(SCP_FreeTimeContext iCtx, string iPersona, bool iEarlyEnd, string iReason,
                                         string iChatBody, string iRoll)
        {
            string aStepName = iEarlyEnd ? "end" : "next";
            string aPath = iCtx.PayloadPath(iPersona, aStepName);
            var aR = Header(aStepName, iPersona);

            SCP_FreeTimeSession? aSession = iCtx.LoadSession(iPersona);
            if (aSession == null || !aSession.active)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 沒有進行中的自由時間 session");
                aR.AppendLine($"- exit: 先跑 {Cmd("free-time --arg step=start --arg persona=" + iPersona + " --arg until=<HH:mm>")}");
                return Blocked(iCtx, aPath, aR, $"step={aStepName} blocked：無 active session");
            }

            DateTime aNow = DateTime.Now;
            DateTime aUntil = SCP_ActivitySession.ParseIsoToLocal(aSession.end_ts) ?? aNow;
            bool aExpired = aNow > aUntil;

            // ⚠ body 要一起帶進 Close —— 🩸 TASK-0389（2026-10-03）：到期那一刻帶來的留言曾在這裡被丟掉，回傳一行都沒提。
            if (iEarlyEnd || aExpired) return Close(iCtx, iPersona, aSession, iEarlyEnd, iReason, aNow, aUntil, aExpired, aPath, aR, iChatBody);

            // ── roll=0：只讀訊息、不換骰（Tim 2026-08-21）──
            // 物理意義：「我還在做同一件活動，但想看看有沒有人講話」是高頻需求，而換骰會 ①輪次+1 ②重擲 ③發「換骰」公告 ——
            //   三件都在說謊：我沒有換活動，公告卻宣布我換了。⇒ 不動 rounds、不重擲；帶 body 才發，且 tag 是 chat 不是 dice-roll。
            bool aKeepDice = (iRoll ?? "").Trim() == "0";
            string aChat = (iChatBody ?? "").Trim();
            if (aKeepDice)
            {
                SCP_FreeTimePostResult? aKeepPost = aChat.Length > 0 ? iCtx.Post(iPersona, aChat, "chat") : null;
                AppendTimeFields(aR, aNow, aUntil);
                aR.AppendLine($"- 輪次: **{aSession.rounds}**（**未換骰** —— `roll=0`，繼續當前活動）　活動實作: **{aSession.activities_done}** 件");
                aR.AppendLine(aKeepPost == null
                    ? "- 本輪交流: **未帶訊息** —— 想講話帶 `--arg-file body=<檔>`"
                    : (aKeepPost.Seq > 0 ? $"- 本輪交流: ✅ 已發言 seq **{aKeepPost.Seq}**（tag=chat，不是換骰公告）"
                                         : $"- 本輪交流: {aKeepPost.Describe()}"));
                SCP_FreeTimePartners.AppendOnlineSection(aR, iCtx, iPersona);
                SCP_FreeTimeUnread aUnread0 = SCP_FreeTimePartners.AppendUnreadSection(aR, iCtx, iPersona);
                aR.AppendLine("## next");
                aR.AppendLine("1. **繼續當前活動** —— `" + Cmd("free-time-activity --arg op=step --arg persona=" + iPersona)
                              + " --arg step=<…>` ／ 做完 `--arg op=done`（⚠ 那是 **free-time-activity**，不是本支）—— 本次沒有新骰面。");
                aR.AppendLine("2. 想換活動再跑一次 `step=next`（不帶 `roll=0`）。");
                AppendContinueBlock(aR, iPersona);
                AppendTail(aR, iCtx);
                var aKeepRes = SCP_CmdResult.Success($"✓ free-time step=next roll=0：未換骰（輪次 {aSession.rounds}）");
                string? aAdv0 = WriteThenAdvance(aKeepRes, iCtx, iPersona, aPath, aR, aUnread0);
                return aKeepRes.AddValue("rounds", aSession.rounds.ToString(CultureInfo.InvariantCulture))
                               .AddValue("rolled", "0")
                               .AddValue("post_seq", (aKeepPost?.Seq ?? 0).ToString(CultureInfo.InvariantCulture))
                               .AddValue("unread", aUnread0.Shown.ToString(CultureInfo.InvariantCulture))
                               .AddValue("cursor_advanced_to", aAdv0 ?? "(未推進)");
            }

            // 未到期：輪次 +1、重擲（時間感知）、宣告、回傳新骰面＋剩餘時間＋券餘額
            int aRound = ++aSession.rounds;
            bool aSaved = iCtx.SaveSession(iPersona, aSession);
            int aRemain = (int)(Math.Max(0, (aUntil - aNow).TotalSeconds) / 60);
            SCP_FreeTimeRoll aRoll = SCP_FreeTimeDice.Roll(iCtx, iPersona, aRemain);
            // 本場那一批的剩餘（按 session_id 查那一批 —— 查所有限時券會把別場的量算進來，而那不會報錯）。
            SCP_FreeTimeVoucherUsage aUsage = iCtx.CanvasUsage(iPersona, aSession.session_id);

            // ⚠ 這裡曾經有「末段提示」（剩 N 分改印『不建議起新活動』）—— 2026-08-14 Tim 拍板拔掉：
            //   截止是軟的，晚起的活動只會讓場次順延；而同一句警語三分鐘連吐 5 次就會被訓練成背景音。
            //   **加規則之前先問，這是在防真實問題，還是在防「我沒有把問題本身移走」。**
            // 區塊職責：換骰時順帶跟同事交流（Tim 2026-08-18）—— body 併進**同一則** post（兩則會洗版）；空＝與改動前逐字相同。
            var aDice = new StringBuilder();
            if (aChat.Length > 0) { aDice.AppendLine(aChat); aDice.AppendLine(); aDice.AppendLine("---"); }
            // 骰面標題自己說出「上面還有一段話」（Tim 2026-08-18：只看到骰面那一段的人會以為這則只有骰面）。
            aDice.AppendLine(aChat.Length == 0
                ? $"🎲 [{iPersona} 大小姐] 自由時間第 {aRound} 輪換骰（至 {aUntil:HH:mm}）："
                : $"🎲💬 [{iPersona} 大小姐] 自由時間第 {aRound} 輪換骰（至 {aUntil:HH:mm}）　※ **本則上半是留言，往上讀** ↑");
            SCP_FreeTimeDice.AppendPriorityNote(aDice, aRoll);
            for (int i = 0; i < Math.Min(3, aRoll.List.Count); i++)
                aDice.AppendLine($"{i + 1}. {(aRoll.List[i].Priority ? "⭐ " : "")}{aRoll.List[i].TavernLine()}");
            aDice.AppendLine($"（前 3 名；全清單 {aRoll.List.Count} 項｜跟沒跟骰照舊酒館可觀測）");
            SCP_FreeTimePostResult aPost = iCtx.Post(iPersona, aDice.ToString(), "dice-roll");

            AppendTimeFields(aR, aNow, aUntil);
            // ⚠ 這兩個數字是**紀錄，不是尺**（Tim 2026-09-04：自由時間不是強制活動）。
            aR.AppendLine($"- 輪次: **{aRound}**　活動實作: **{aSession.activities_done}** 件"
                          + (aSaved ? "" : "　⚠ 輪次**寫不進 session 檔**（下一輪會重算成同一輪）"));
            aR.AppendLine(VoucherNowLine(aUsage, aSession.session_id));
            aR.AppendLine($"- 換骰宣告: {aPost.Describe("未發（best-effort）")}");
            aR.AppendLine(aChat.Length == 0
                ? "- 本輪交流: **未帶訊息**（不強制）—— 下一輪想跟同事講話就帶 `--arg-file body=<檔>`，會併進換骰宣告同一則"
                : "- 本輪交流: ✅ 已併入換骰宣告");
            SCP_FreeTimePartners.AppendOnlineSection(aR, iCtx, iPersona);
            SCP_FreeTimeUnread aUnread = SCP_FreeTimePartners.AppendUnreadSection(aR, iCtx, iPersona);
            string? aPartners = SCP_FreeTimePartners.AppendPartnerBriefSection(aR, iCtx, iPersona);
            SCP_FreeTimeDice.AppendDiceSection(aR, aRoll, iPersona);
            aR.AppendLine("## next");
            aR.AppendLine("1. 從骰面挑下一件活動（跟骰規則同 start）。⭐ 要維持對話流就發動引擎："
                          + "`" + Cmd("tavern-wait --arg persona=" + iPersona + " --arg timeout=<秒>") + "`"
                          + "（⛔ `timeout` 預設 0 ＝ 不等）；⛔ 舊的 `--wait-reply` 仍然是死的，會被靜默吃掉。");
            aR.AppendLine("2. step=end（提前收工）除非 Tim 明確指示，不要用。");
            // ⚠ 量出來的，不是禮貌提醒（basecamp 2026-09-10）：連跑三輪 next 而只做了一件活動 —— 當時在做的事叫「等時鐘」，
            //   長出來的樣子是三則沒有內容的換骰公告；本 Cmd 每次都回 Success ⇒ 沒有任何一層會叫，成本落在同事的未讀數上。
            aR.AppendLine("3. ⛔ **沒有活動要收就不要按 next** —— 每一輪都會往酒館發一則公告。"
                          + "「等時鐘」不是一輪活動，空轉三輪就是三則沒有內容的洗版。");
            AppendContinueBlock(aR, iPersona);
            AppendTail(aR, iCtx);

            var aRes = SCP_CmdResult.Success($"✓ free-time step=next：第 {aRound} 輪換骰（至 {aUntil:HH:mm}）　換骰宣告 {aPost.Describe("未發")}");
            string? aAdv = WriteThenAdvance(aRes, iCtx, iPersona, aPath, aR, aUnread);
            if (aPartners != null) aRes.AddOutput(aPartners);
            aRes.AddValue("rounds", aRound.ToString(CultureInfo.InvariantCulture))
                .AddValue("rolled", "1")
                .AddValue("post_seq", aPost.Seq.ToString(CultureInfo.InvariantCulture))
                .AddValue("unread", aUnread.Shown.ToString(CultureInfo.InvariantCulture))
                .AddValue("cursor_advanced_to", aAdv ?? "(未推進)");
            AddUsageValues(aRes, aUsage);
            return aRes;
        }

        // 收工（到期或提前）：關 session → 讀本場那一批的用量 → 收工宣告 → next 指路
        static SCP_CmdResult Close(SCP_FreeTimeContext iCtx, string iPersona, SCP_FreeTimeSession iSession, bool iEarlyEnd,
                                   string iReason, DateTime iNow, DateTime iUntil, bool iExpired, string iPath, StringBuilder ioR,
                                   string iChatBody)
        {
            string aChat = (iChatBody ?? "").Trim();
            string aStepName = iEarlyEnd ? "end" : "next";
            string aEndReason = iEarlyEnd
                ? (string.IsNullOrEmpty(iReason) ? "early（未附 reason —— 提前收工的形狀該可觀測，下次帶上）" : $"early: {iReason}")
                : "expired";
            bool aClosed = iCtx.CloseSession(iPersona, iSession, aEndReason, out int aRounds);

            // 收工**不需要作廢寫入** —— 限時券到期自己失效。這裡只讀回「本場那一批的用量」。
            // 🩸 TASK-0195：曾經讀「只算未過期的」再用「發放量 − 它」推用量 —— 到點收工必然在 until 之後、券在 until+grace 到期
            //   ⇒ 晚一分鐘跑 next 那批就過期、回 0 ⇒ 公告印「用 10 張、全數用畢」而實際一張都沒用。**查無與用完在輸出上一模一樣**。
            // 🩸 TASK-0198：「明說查無」退化成另一個問題 —— 批次被清掉之後只答得出查無（新券系統不記歷史，那是已知代價）。
            //   ⛔ 不准用「發放量 − 0」把它補成「全數用畢」（那正是 0195 那隻病）。
            SCP_FreeTimeVoucherUsage aUsage = iCtx.CanvasUsage(iPersona, iSession.session_id);
            string aVoucherBrief;
            if (aUsage.Queried && aUsage.Found)
                aVoucherBrief = $"🎟 限時券用 {aUsage.Used}/{aUsage.Granted} 張" + (aUsage.Remain > 0 ? $"、{aUsage.Remain} 張到期作廢" : "、全數用畢");
            else if (aUsage.Queried)
                aVoucherBrief = "🎟 限時券用量：**帳本查無本場批次**（不猜 —— 見 TASK-0195 / TASK-0198）";
            else
                aVoucherBrief = "🎟 限時券用量：**讀不到**（券帳那一層沒回答 —— 不猜）";

            // 區塊職責：收工時帶來的留言併進收工宣告**同一則**（同換骰的做法：兩則會洗版）；空＝與改動前逐字相同。
            var aBody = new StringBuilder();
            if (aChat.Length > 0) { aBody.AppendLine(aChat); aBody.AppendLine(); aBody.AppendLine("---"); }
            aBody.AppendLine(iEarlyEnd
                ? $"🏁 [{iPersona} 大小姐] 自由時間提前收工（{(string.IsNullOrEmpty(iReason) ? "未附 reason" : iReason)}）"
                : $"⏰ [{iPersona} 大小姐] 自由時間到點收工（至 {iUntil:HH:mm}）");
            aBody.AppendLine($"本場 {aRounds} 輪活動｜{aVoucherBrief}。回工位了。");
            SCP_FreeTimePostResult aPost = iCtx.Post(iPersona, aBody.ToString(), iEarlyEnd ? "session-end-early" : "session-end");

            AppendTimeFields(ioR, iNow, iUntil);
            ioR.AppendLine(iExpired && !iEarlyEnd ? "- ⏰ **時間到** —— session 已收工" : "- 🏁 提前收工 —— session 已收工");
            if (!aClosed) ioR.AppendLine($"- ⚠ **session 檔寫不進去**（收工沒有落盤）：`{iCtx.SessionPath(iPersona)}`");
            ioR.AppendLine($"- end_reason: {aEndReason}");
            ioR.AppendLine($"- 本場輪次: {aRounds}");
            if (aUsage.Queried && aUsage.Found)
                ioR.AppendLine($"- 🎟 限時券: 用 {aUsage.Used}/{aUsage.Granted} 張"
                               + (aUsage.Remain > 0 ? $"、**{aUsage.Remain} 張到期作廢**（Server 會在保留期過後的下一次寫入時清掉那一批）" : "（全數用畢）")
                               + "　← 讀數源: `voucher op=usage`");
            else if (aUsage.Queried)
                ioR.AppendLine($"- 🎟 限時券: **帳本查無本場批次**（ref=`{iSession.session_id}`）⇒ 用量無法判定，⛔ 不以發放量推導（TASK-0195 / TASK-0198）"
                               + "　⚠ 券**不記歷史** ⇒ 批次死掉超過保留期被清掉之後，這裡永久只答得出查無");
            else
                ioR.AppendLine($"- 🎟 限時券: **讀不到**（{aUsage.Error}）⇒ 用量無法判定，⛔ 不以發放量推導");
            ioR.AppendLine($"- 收工宣告: {aPost.Describe("未發（best-effort）")}");
            // 分開印「沒帶」與「帶了」—— 丟掉與沒帶不可同形（TASK-0389）。
            // 帶了的那則跟收工宣告是**同一則**，所以它的狀態就是上一行那四態之一；⛔ 不在這裡另判「沒發」（Seq==0 也可能是排程中／不知道）。
            if (aChat.Length == 0)
                ioR.AppendLine("- 本輪交流: **未帶訊息**");
            else if (aPost.Seq > 0)
                ioR.AppendLine($"- 本輪交流: 已併進收工宣告同一則（seq {aPost.Seq}）—— 時間已到，所以跟著收工那則發，不是換骰那則");
            else
                ioR.AppendLine("- 本輪交流: ⚠ 併在收工宣告同一則 —— **狀態同上一行**（沒有 seq）；照上一行的指示處理，⛔ 別單獨補發留言");
            ioR.AppendLine("## ⏹ 已收工 —— 自由時間結束，**不要再跑 step=next**");
            ioR.AppendLine("- 回工作；或走晚安流程：" + Cmd("goodnight-check --arg persona=" + iPersona));
            ioR.AppendLine("- 還想花錢再睡 →（可選）ucl-spending-time（不綁死晚安）。");
            AppendTail(ioR, iCtx);

            var aRes = aClosed
                ? SCP_CmdResult.Success($"✓ free-time step={aStepName}：收工（{aEndReason}）　本場 {aRounds} 輪　{aVoucherBrief}")
                : SCP_CmdResult.Fail(70, $"✗ free-time step={aStepName}：收工**沒有落盤**（session 檔寫不進去）");
            Write(aRes, iPath, ioR);
            aRes.AddValue("closed", aClosed ? "1" : "0").AddValue("end_reason", aEndReason)
                .AddValue("rounds", aRounds.ToString(CultureInfo.InvariantCulture))
                .AddValue("post_seq", aPost.Seq.ToString(CultureInfo.InvariantCulture));
            AddUsageValues(aRes, aUsage);
            return aRes;
        }

        static string VoucherNowLine(SCP_FreeTimeVoucherUsage iU, string iRef)
        {
            if (iU.Queried && iU.Found)
                return $"- 🎟 限時券: 已用 {iU.Used}/{iU.Granted}（剩 {iU.Alive} 張可花，到期即作廢）";
            if (iU.Queried)
                return $"- 🎟 限時券: **帳本查無本場批次**（ref=`{iRef}`）—— ⛔ 不以發放量推導（TASK-0195 / TASK-0198）";
            return $"- 🎟 限時券: **讀不到**（{iU.Error}）—— 不代表沒有";
        }

        static void AddUsageValues(SCP_CmdResult ioRes, SCP_FreeTimeVoucherUsage iU)
        {
            // ⚠ 查無／讀不到時**不給數字欄**（給 0 就是讓下游去做那個減法）。
            ioRes.AddValue("voucher_usage", !iU.Queried ? "unknown" : iU.Found ? "found" : "not_found");
            if (!iU.Queried || !iU.Found) return;
            ioRes.AddValue("vouchers_granted", iU.Granted.ToString(CultureInfo.InvariantCulture))
                 .AddValue("vouchers_used", iU.Used.ToString(CultureInfo.InvariantCulture))
                 .AddValue("vouchers_remain", iU.Remain.ToString(CultureInfo.InvariantCulture))
                 .AddValue("vouchers_alive", iU.Alive.ToString(CultureInfo.InvariantCulture));
        }

        // ===========================================================
        // 區塊：純參考查詢三式（TASK-0052 —— freetime.py 退役的出口）。
        // 物理意義：**純讀** —— 不進場、不發券、不寫 session、不發酒館、不推活動統計（唯一的寫入是回傳檔本身）。
        // ⚠ 刻意不套「必須在線」守衛：參考查詢離線也該答，守衛只屬於會動狀態的 step。
        // ===========================================================
        public static SCP_CmdResult List(SCP_FreeTimeContext iCtx, string iPersona)
        {
            string aPath = iCtx.PayloadPath(iPersona, "list");
            var aR = Header("list", iPersona);
            int aShared = 0, aProject = 0, aDisabled = 0, aN = 0;
            var aEnabled = new System.Collections.Generic.List<SCP_FreeTimeActivity>();
            foreach (var a in iCtx.Activities)
            {
                if (!a.Enabled) { aDisabled++; continue; }
                aEnabled.Add(a);
                if (a.IsProjectLayer) aProject++; else aShared++;
            }
            aR.AppendLine($"## 📋 活動清單（固定順序，{aEnabled.Count} 項 enabled｜來源：UCL_Core 共用 {aShared} ＋ 專案 {aProject}"
                          + (aDisabled > 0 ? $"｜另有 {aDisabled} 項 disabled 未列" : "") + "）");
            foreach (var a in aEnabled)
            {
                aR.AppendLine($"{++aN}. [`{a.Id}`]{(a.Group.Length == 0 ? "" : $"（{a.Group}）")} **{a.Name}**"
                              + (a.How.Length == 0 ? "" : $" — {a.How}"));
                aR.AppendLine($"    · md: `{a.Path}`");
            }
            aR.AppendLine();
            aR.AppendLine("- ℹ 本查詢**純讀**：不進場、不發券、不寫 session。要真的開場走 step=start。");
            AppendTail(aR, iCtx);
            var aRes = SCP_CmdResult.Success($"✓ free-time step=list：{aEnabled.Count} 項 enabled（共用 {aShared}／專案 {aProject}／停用 {aDisabled}）");
            Write(aRes, aPath, aR);
            return aRes.AddValue("activities", aEnabled.Count.ToString(CultureInfo.InvariantCulture))
                       .AddValue("disabled", aDisabled.ToString(CultureInfo.InvariantCulture));
        }

        public static SCP_CmdResult Shuffle(SCP_FreeTimeContext iCtx, string iPersona, string iCount)
        {
            string aPath = iCtx.PayloadPath(iPersona, "shuffle");
            var aR = Header("shuffle", iPersona);
            // iRemainMinutes=0 ⇒ 時間感知那道自動跳過（純參考沒有「剩幾分」可言 —— 不假裝知道）
            SCP_FreeTimeRoll aRoll = SCP_FreeTimeDice.Roll(iCtx, iPersona, 0);
            int aTotal = aRoll.List.Count;
            string aCountRaw = (iCount ?? "").Trim();
            // count 截在排序**之後** —— 先截會把剛頂上來的優先項截掉
            if (aCountRaw.Length > 0)
            {
                if (int.TryParse(aCountRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aCount) && aCount > 0)
                {
                    if (aCount < aRoll.List.Count)
                    {
                        aR.AppendLine($"- ✂ count={aCount}：只列前 {aCount} 項（完整候選 {aRoll.List.Count} 項）");
                        aRoll.List = aRoll.List.GetRange(0, aCount);
                    }
                }
                else aR.AppendLine($"- ⚠ count='{aCountRaw}' 不是正整數 ⇒ 列全部（⛔ 不靜默當成某個數字）");
            }
            SCP_FreeTimeDice.AppendDiceSection(aR, aRoll, iPersona);
            aR.AppendLine();
            aR.AppendLine("- ℹ 本查詢**純讀**：不進場、不發券、不寫 session、不發酒館、不推活動統計 —— 擲骰結果只落在這份回傳檔。");
            AppendTail(aR, iCtx);
            var aRes = SCP_CmdResult.Success($"✓ free-time step=shuffle：{aRoll.List.Count}/{aTotal} 項（優先層 {aRoll.PriorityCount} 項）");
            Write(aRes, aPath, aR);
            return aRes.AddValue("candidates", aTotal.ToString(CultureInfo.InvariantCulture))
                       .AddValue("listed", aRoll.List.Count.ToString(CultureInfo.InvariantCulture));
        }

        public static SCP_CmdResult Show(SCP_FreeTimeContext iCtx, string iPersona, string iId)
        {
            string aPath = iCtx.PayloadPath(iPersona, "show");
            var aR = new StringBuilder();
            aR.AppendLine($"# FreeTime step=show persona={iPersona} id={iId}  ts=`{SCP_Morning.NowLocal()}`（本地時間）");
            aR.AppendLine();
            if (iId.Length == 0)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 缺 `--arg id=<活動 id>` —— 跑 step=list 看可用 id");
                return Blocked(iCtx, aPath, aR, "step=show 需要 --arg id=<活動 id>");
            }
            SCP_FreeTimeActivity? aHit = null, aDisabledHit = null;
            foreach (var a in iCtx.Activities)
            {
                if (a.Id != iId) continue;
                if (a.Enabled) { aHit = a; break; }
                aDisabledHit = a;
            }
            if (aHit == null)
            {
                aR.AppendLine("## blocked");
                // 「id 存在但 disabled」與「id 不存在」是兩種狀態，不可印成同一句
                aR.AppendLine(aDisabledHit != null
                    ? $"- reason: 活動 `{iId}` 存在但 **enabled:false**（`{aDisabledHit.Path}`）—— 這不是「找不到」，是被停用"
                    : $"- reason: 找不到活動 id `{iId}` —— 跑 step=list 看可用 id");
                return Blocked(iCtx, aPath, aR, $"step=show 查無 enabled 活動 '{iId}'");
            }
            aR.AppendLine($"## 📄 `{aHit.Path}`");
            aR.AppendLine();
            try { aR.AppendLine(File.ReadAllText(aHit.Path, Encoding.UTF8).TrimEnd()); }
            catch (Exception e) { aR.AppendLine($"⚠ md 讀不了（{e.Message}）—— 以下為 how 欄位："); aR.AppendLine(aHit.How); }
            AppendTail(aR, iCtx);
            var aRes = SCP_CmdResult.Success($"✓ free-time step=show：`{aHit.Id}` {aHit.Name}");
            Write(aRes, aPath, aR);
            return aRes.AddValue("activity", aHit.Id).AddValue("md", aHit.Path);
        }

        // ===========================================================
        // 區塊：跨 kind 擋下時的措辭（Unity `UCL_SessionStartGuard.ReasonMine/ExitMine` 的 Senate 版）。
        // 物理意義：自由時間不是全域互斥的 kind ⇒ 擋住它的**只可能是我自己的另一場**（軸1）。
        //          每個 kind 的收工路徑不同形，所以逐 kind 給，不給一句通用的廢話。
        // ⚠ StreamWatch **沒有 step=end**：誠實出口是「等它到期」，⛔ 不編一個不存在的指令。
        // ===========================================================
        static string CrossKindReason(SCP_ActivitySession iBlocker, string iPersona)
        {
            string aUntil = string.IsNullOrEmpty(iBlocker.until_local) ? "未寫截止時刻" : "至 " + iBlocker.until_local;
            if (!string.Equals(iBlocker.persona, iPersona, StringComparison.Ordinal))
                return $"**@{iBlocker.persona}** 正在 **{KindLabel(iBlocker.kind)}**（`{iBlocker.session_id}`，{aUntil}）";
            return $"你已經在另一種 session 裡：**{KindLabel(iBlocker.kind)}**（`{iBlocker.session_id}`，{aUntil}）—— 一人同時只能在一種場";
        }

        static string CrossKindExit(SCP_ActivitySession iBlocker, string iPersona)
        {
            if (iBlocker.kind == SCP_ActivitySessionKind.StreamWatch)
                return $"等 {(string.IsNullOrEmpty(iBlocker.until_local) ? "它到期" : iBlocker.until_local)}"
                       + "（觀影沒有 step=end —— 到期或宿主停止時 Cmd 會自己收工並結算）";
            if (iBlocker.kind == SCP_ActivitySessionKind.Coding)
                return "先收掉那場：" + Cmd("coding --arg op=end --arg persona=" + iPersona);
            return $"先收掉那場（kind=`{iBlocker.kind}`）；查現況：" + Cmd("sessions --arg op=show --arg target_persona=" + iPersona);
        }

        static string KindLabel(string iKind)
        {
            if (iKind == SCP_ActivitySessionKind.FreeTime) return "自由時間";
            if (iKind == SCP_ActivitySessionKind.StreamWatch) return "觀影";
            if (iKind == SCP_ActivitySessionKind.Coding) return "Coding 施工場";
            return string.IsNullOrEmpty(iKind) ? "(kind 欄是空的)" : iKind;   // 認不得就原樣印，不印「未知」
        }

        // ===========================================================
        // 區塊：小工具（標頭／時間欄／續跑區塊／尾巴／until 解析／落檔）
        // ===========================================================
        static StringBuilder Header(string iStep, string iPersona)
        {
            var aR = new StringBuilder();
            aR.AppendLine($"# FreeTime step={iStep} persona={iPersona}  ts=`{SCP_Morning.NowLocal()}`（本地時間）");
            aR.AppendLine();
            return aR;
        }

        /// <summary>三個時間欄（Tim 拍板）：時間感由 Cmd 供給。⛔ 不印剩餘分鐘（Tim 2026-09-04：倒數不是任何人的下一步依據）。</summary>
        public static void AppendTimeFields(StringBuilder ioR, DateTime iNow, DateTime iUntil)
        {
            ioR.AppendLine("## time（時間感由本 Cmd 供給 —— 別自己心算）");
            ioR.AppendLine($"- 當前時間: **{iNow:yyyy-MM-dd HH:mm}**（本地）");
            ioR.AppendLine($"- 自由時間到: **{iUntil:HH:mm}**（軟截止 —— 時間到不打斷進行中活動，最後一件做完跑 next 才收工）");
            ioR.AppendLine(iNow < iUntil
                ? "- 狀態: **時間還沒到** —— 挑下一項活動"
                : "- 狀態: **時間到了** —— 手上這件做完跑 step=next 收工");
        }

        // ===========================================================
        // 區塊職責：固定位置的「續跑」區塊 —— 自由時間最容易斷在「做完一件事就沒有下一步」。
        // 物理意義：**看起來一樣的東西不會被當成動作** ⇒ 獨立成一個位置固定、只有一條指令的區塊；
        //          社交對話寫成**同時進行**而不是一個選項（social-chat 已併進本流程，不寫明的話它會變成「消失的活動」）。
        // ===========================================================
        public static void AppendContinueBlock(StringBuilder ioR, string iPersona)
        {
            ioR.AppendLine();
            ioR.AppendLine("## ▶ 下一步（自由時間**進行中** —— 時間還沒到，挑下一項活動）");
            ioR.AppendLine("💬 **社交對話是同時進行的，不是另一個選項** —— 換骰這一步本身就在讀未讀訊息、");
            ioR.AppendLine("　 也可以帶 `body` 跟同事講話。所以不必為了「跟人互動」去挑一個活動；");
            ioR.AppendLine("　 挑你想做的事，講話在換骰時一起發生。");
            ioR.AppendLine();
            ioR.AppendLine("活動告一段落就跑這行 —— **截止是軟的**，時間到不打斷進行中的活動，最後一件做完跑它才收工：");
            ioR.AppendLine("```bash");
            ioR.AppendLine(Cmd("free-time --arg step=next --arg persona=" + iPersona) + " [--arg-file body=<想跟同事說的話>]");
            ioR.AppendLine("```");
            ioR.AppendLine("- `body` **可選**（不強制）—— 帶了就併進換骰宣告同一則，換骰同時跟同事交流。");
        }

        /// <summary>每份回傳檔的尾巴：設定讀不了的警告（必印）＋ 本趟累積的警告。⛔ 不吞。</summary>
        public static void AppendTail(StringBuilder ioR, SCP_FreeTimeContext iCtx)
        {
            var aAll = new System.Collections.Generic.List<string>();
            if (iCtx.SettingsError != null) aAll.Add("⚙ " + iCtx.SettingsError);
            aAll.AddRange(iCtx.ScanWarnings);
            aAll.AddRange(iCtx.Warnings);
            if (aAll.Count == 0) return;
            ioR.AppendLine();
            ioR.AppendLine("## ⚠ 本趟警告（讀不到 ≠ 沒有）");
            foreach (string w in aAll) ioR.AppendLine("- " + w);
        }

        static string SettingsSource(SCP_FreeTimeContext iCtx)
            => iCtx.SettingsError != null ? "defaults_error" : iCtx.SettingsFileExists ? "file" : "defaults_missing";

        /// <summary>HH:mm 解析：已過的時刻若在 12 小時內視為打錯（blocked），超過 12 小時視為跨日（+1 天）。</summary>
        public static bool TryParseUntil(string iUntil, DateTime iNow, out DateTime oUntil, out string oError)
        {
            oUntil = default;
            oError = "";
            if (string.IsNullOrEmpty(iUntil)) { oError = "until 必填（--arg until=<HH:mm 本地>）"; return false; }
            if (!TimeSpan.TryParse(iUntil, CultureInfo.InvariantCulture, out TimeSpan aTod) || aTod < TimeSpan.Zero || aTod >= TimeSpan.FromDays(1))
            { oError = $"until 解析失敗：'{iUntil}'（需 HH:mm，例 12:30）"; return false; }
            oUntil = iNow.Date + aTod;
            if (oUntil <= iNow)
            {
                if ((iNow - oUntil) > TimeSpan.FromHours(12)) oUntil = oUntil.AddDays(1);   // 深夜跨日（23:50 → 00:30）
                else { oError = $"until={iUntil} 已過（現在 {iNow:HH:mm}）—— 時限判定只認時鐘"; return false; }
            }
            return true;
        }

        /// <summary>落回傳檔並登記到結果（📄）。寫不出來 ⇒ 出聲（⛔ 不讓「沒有回傳檔」安靜地變成「回傳檔是空的」）。</summary>
        public static bool Write(SCP_CmdResult ioRes, string iPath, StringBuilder iReport)
        {
            try
            {
                SCP_CmdPayload.Write(iPath, iReport.ToString());
                ioRes.AddOutput(iPath);
                return true;
            }
            catch (Exception e)
            {
                ioRes.Lines.Add($"⚠ 回傳檔落檔失敗 {iPath}: {e.GetType().Name}: {e.Message}");
                ioRes.AddValue("payload_write_failed", "1");
                return false;
            }
        }

        /// <summary>blocked：回傳檔照寫，再回非零（同 Unity 版每一個守衛的手勢：先 WritePayload，再 throw）。</summary>
        public static SCP_CmdResult Blocked(SCP_FreeTimeContext iCtx, string iPath, StringBuilder ioR, string iWhat)
        {
            AppendTail(ioR, iCtx);
            var aRes = SCP_CmdResult.Fail(ExitBlocked, $"✗ {iWhat}（詳見回傳檔）");
            Write(aRes, iPath, ioR);
            return aRes.AddValue("blocked", "1");
        }

        // 先落回傳檔（游標那行是佔位）→ 推游標 → 用讀回結果換掉佔位再寫一次。
        // ⚠ 第一次寫不出來就**不推** —— 回傳檔沒落地時訊息不能被標成已讀。
        static string? WriteThenAdvance(SCP_CmdResult ioRes, SCP_FreeTimeContext iCtx, string iPersona, string iPath,
                                        StringBuilder iReport, SCP_FreeTimeUnread iUnread)
        {
            if (!Write(ioRes, iPath, iReport))
            {
                ioRes.Lines.Add("⚠ 回傳檔沒有落地 ⇒ 酒館已讀游標**未推進**（這批未讀下次還會出現）");
                return null;
            }
            var (aLine, aAdvancedTo) = SCP_FreeTimePartners.AdvanceUnread(iCtx, iPersona, iUnread);
            string aFinal = iReport.ToString().Replace(SCP_FreeTimePartners.CursorPlaceholder, aLine);
            try { SCP_CmdPayload.Write(iPath, aFinal); }
            catch (Exception e) { ioRes.Lines.Add($"⚠ 游標已推進，但回傳檔第二次落檔失敗（檔裡那一行停在「推進中」）：{e.Message}"); }
            ioRes.Lines.Add("· 酒館未讀 " + iUnread.Shown + " 筆　" + aLine.TrimStart('-', ' '));
            return aAdvancedTo;
        }
    }
}
