// 區塊職責：`cmd coding` —— **Senate 側的 Coding 場入口**（TASK-0058 **A2**）。
// 物理意義：A1 只做了 Unity 那側 ⇒ 在 `Senate` / `SCP_Core` 改 `.cs` 的人（每天都有）
//           **既不會被本場擋下、也擋不下別人**，而畫面上看起來一切正常。
//           ⭐ A2 能便宜的理由：session 檔是 `<DataRoot>/sessions/<persona>.json`，
//           而兩個宿主的 DataRoot 是**同一個** ⇒ 不需要第二把鎖，只需要第二個入口。
// 數值影響：`op=show` 一個位元組都不寫；`start`／`status`／`end` 各寫一次那個檔。
//           **不發薪、不廣播** —— Coding 沒有金流，而公告是宿主的事。
//
// ⚠ **本 Cmd 不依賴 Editor**（`sessions` 那支也是）：Editor 沒開時照樣進得了場、退得了場。
//   🩸 這是 A2 相對 A1 的**淨增量**：Editor 沒開時我照樣在改 SCP_Core 的 `.cs`
//   （2026-09-05 我自己整天都是這樣），而 A1 那條路那時完全看不到我。
//
// ⚠ 退場的**編譯閘由宿主注入**（`SCP_CodingExitGateHost`）—— 兩個宿主的尺不同形，
//   ⛔ 不可以合成一把。沒登記閘時**明說「未驗編譯」**，那跟「量過了是綠的」必須不同形。
#nullable enable
using System;
using System.Collections.Generic;
using SCP.Core.Paths;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SCP.Core.Git;
using SCP.Core.Session;
using SCP.Core.Tasks;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Coding : SCP_Cmd
    {
        /// <summary>
        /// 進場的預設租期（小時）。
        /// <para>🩸 為什麼一定要有一個 `end_ts`（PM 2026-09-05 拍 (A)）：沒有它的場
        /// `IsRunningAt` 恆真 ⇒ **永遠是「進行中」、永遠不會落進補收工那條路**
        /// ⇒ 持有者掉線就永遠擋住所有人（見 `Docs~/Session_Kinds.md` §5.5）。</para>
        /// <para>⚠ 到期**不等於自動釋放** —— 到期只是落回「殘留」，別人要搶場仍得顯式跑
        /// `sessions --arg op=close --arg confirm=1`（寫別人的檔、留痕跡）。</para>
        /// </summary>
        public const int DefaultLeaseHours = 2;

        public override string Name => "coding";

        public override string Summary => "Coding 施工場（改 C# 前進場／場中更新 status 兼續期／"
            + "**綁單後單子進 in_review 就自動收場**／退場過編譯閘）—— **不需要 Editor**";

        public override string Details =>
            "⛔ **射程**：本 Cmd 是 **Senate 側**的入口（TASK-0058 A2）。Unity 那側走 `ucmd run Coding`。\n"
            + "兩邊寫的是**同一個檔位**（`<data_root>/sessions/<persona>.json`）⇒ 互相擋得到。\n"
            + "⭐ 全域互斥由 `SCP_ActivitySessionStore.TryStart` 那一層保證，本 Cmd 不自己判 ——\n"
            + "   自己判就是第三份判準，而它會跟前兩份不一致且**不報錯**。\n"
            + "📐 **施工範圍**（TASK-0201）：`op=start --arg scope=<絕對路徑>` 宣告這一場要動哪一塊，\n"
            + "   **範圍不重疊的人可以同時開場**；重疊才擋（重疊＝路徑包含，\n"
            + "   `…/Assets/Scripts` 與 `…/Assets/Scripts/Conditions` 重疊）。\n"
            + "   ⚠ 宣告要取**最大範圍** —— 宣告得比實際改的窄，擋不住真正會撞的人，\n"
            + "     而失效樣子是兩個人都進場了然後改到同一支檔，**這道閘不會叫**。\n"
            + "   ⛔ **不宣告 ⇒ 退化成舊行為（整個 kind 全域獨佔，誰都擋）** —— 那是安全側，\n"
            + "     不是「還沒做的那一半」。\n"
            + "   ⚠ 判準是**純路徑**（Tim 2026-09-11 拍板）：同一個 repo 的兩份工作副本\n"
            + "     （`Senate/SCP_Core` 與 `LY/Assets/Plugins/SCP_Core`）**不算衝突**。\n"
            + "     代價已知：兩人各改一份副本的同一支檔時這道閘不叫，要到 push 分叉才現形。\n"
            + "⚠ `op=start` 一律帶租期（預設 " + DefaultLeaseHours + " 小時）：沒有 `end_ts` 的場永遠不會變成殘留，\n"
            + "   而那代表**持有者掉線之後沒有人能回收它**。續期走 `op=status`（那一步本來就要跑）。\n"
            + "⚠ `op=end` 的編譯閘**由宿主注入**：沒登記時明說「未驗編譯」——「沒有量」不是「綠燈」。\n"
            + "🔗 **綁單與自動收場**（TASK-0193，Tim 2026-09-10：目的是**縮短持有**）：\n"
            + "   · `op=start --arg tasks=129,193` 開場即綁；**`op=bind`** 事後補綁（先進場才認領是常態）\n"
            + "   · **`op=autoclose`**：綁定單**全部**離開施工狀態就收場。\n"
            + "     判準是 **`in_review` 不是 `done`** —— 不等全部驗完；\n"
            + "     `in_review` 被退回就**下次動工開新的場**，⛔ 不把舊場接回來。\n"
            + "   · 它掛在 `senate cmd commit` 推單之後 ⇒ **不必記得收場**\n"
            + "     （同 `Fixes` 掛在 commit 上的理由：掛在他一定會走的那條路上）。\n"
            + "   · 五種「不收」各自說得出理由：`no-session`／`unbound`／\n"
            + "     `task-missing`（**查無 ≠ 做完**）／`still-working`／\n"
            + "     **`compile-red`（⛔ 紅燈不收，場還是你的）**。\n"
            + "   · ⚠ 收場時工作區還有未提交的 Unity C# ⇒ **照收**，只把清單記進 session 檔並印出來\n"
            + "     —— **那是資訊不是閘**：擋下會讓場握得更久，而縮短持有正是它存在的理由。\n"
            + "   · ⛔ 射程只到 **Unity 端 C#**（含 `Assets/` 底下的 submodule）：\n"
            + "     Senate 是獨立 repo、獨立編譯，可以同步改，不被這道閘排隊。\n"
            + "⛔ Senate 這一側的閘量的是**編譯**（`dotnet build`）；`build.sh` 出廠驗收**不在射程內**\n"
            + "   （它會覆寫正在執行的 `senate.exe`，從 CLI 裡面跑不了）—— 那一格是人要另外跑的。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("coding --arg op=start --arg persona=<你> --arg status=\"在改 SCP_Cmd_Coding\"");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）"
                + "—— senate CLI 沒給時用「路徑管理」頁那一格補上並印出來", iRequired: true),
            new SCP_CmdArgSpec("op", "show（預設）| start | status | bind | autoclose | end"),
            new SCP_CmdArgSpec("persona", "誰的場（start／status／end 必填 —— ⚠ 不猜身分）"),
            new SCP_CmdArgSpec("status", "正在改哪一部分，一句話（start 必填；status 用它更新）"),
            new SCP_CmdArgSpec("hours", "租期小時數（start 選填，預設 " + DefaultLeaseHours + "；status 會用它續期）"),
            new SCP_CmdArgSpec("scope", "這一場的**施工範圍**（絕對路徑，取施工的最大範圍）。start 選填。"
                               + "範圍不重疊的人可以同時開場；⛔ **不給＝整個 kind 全域獨佔**（舊行為）"),
            new SCP_CmdArgSpec("force", "end 用：編譯紅燈時顯式硬退（要同時給 force_reason）"),
            new SCP_CmdArgSpec("force_reason", "force 退場的理由 —— 會寫進 session 檔，事後查得到"),
            // ⚠ 綁定是**多對多**：一場多單、一單多場都成立 ⇒ 這裡收的是清單不是單一值。
            //   `op=bind` 是**事後補綁**的入口 —— 先進場才認領是常態（實測：gura 2026-09-10 兩次都是）。
            new SCP_CmdArgSpec("tasks", "這一場綁哪幾張單（逗號分隔，例 `129,193`）。"
                               + "start 選填／bind 必填。**綁了才會自動收場**"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            var aRoot = new SCP_DataRoot(iArgs.Get("data_root"));
            string aOp = iArgs.Get("op").Trim();
            if (aOp.Length == 0) aOp = "show";
            string aPersona = iArgs.Get("persona").Trim();

            switch (aOp)
            {
                case "show": return OpShow(aRoot);
                case "start": return OpStart(aRoot, aPersona, iArgs.Get("status").Trim(), ParseHours(iArgs),
                                             iArgs.Get("tasks").Trim(), iArgs.Get("scope").Trim());
                case "bind": return OpBind(aRoot, aPersona, iArgs.Get("tasks").Trim());
                case "autoclose": return OpAutoClose(aRoot, aPersona);
                case "status": return OpStatus(aRoot, aPersona, iArgs.Get("status").Trim(), ParseHours(iArgs));
                case "end": return OpEnd(aRoot, aPersona, iArgs.Get("force") == "1", iArgs.Get("force_reason").Trim());
                default:
                    return SCP_CmdResult.Fail(2, "✗ 不認得的 op：" + aOp + "（show | start | status | end）");
            }
        }

        static int ParseHours(SCP_CmdArgs iArgs)
        {
            string aRaw = iArgs.Get("hours").Trim();
            if (aRaw.Length == 0) return DefaultLeaseHours;
            // ⚠ 解析不出來時**用預設值並不報錯**是錯的做法 —— 那會讓打錯的人拿到一個他沒要的租期。
            //   這裡回 -1，呼叫端擋下並說原因。
            return int.TryParse(aRaw, out int aHours) && aHours > 0 ? aHours : -1;
        }

        // ── show ──────────────────────────────────────────────────

        // ⚠ 範圍判準上路之後**場上可以同時有好幾個人**（TASK-0201）⇒ 這裡一律列**全部**。
        //   🩸 只印第一場的話，「只有一個人在場上」與「有三個人但我只看得到一個」在輸出上同形，
        //     而讀的人會拿它去推「我撞不撞得到」—— 那個推論會錯得很安靜。
        static SCP_CmdResult OpShow(SCP_DataRoot iRoot)
        {
            var aHolders = SCP_ActivitySessionStore.ListRunningGlobal(
                iRoot, SCP_ActivitySessionKind.Coding, DateTime.Now);
            if (aHolders.Count == 0)
            {
                var aFree = SCP_CmdResult.Success("· Coding 場：**沒有人持有**（掃全體 session 檔）",
                    "⚠ 「沒查到」的射程：**只涵蓋走過 `TryStart` 的那些場** —— 直接 `Save` 開的場不在裡面。");
                return aFree.AddValue("held", "0").AddValue("holders", "0");
            }
            var aLines = new List<string>
            {
                "· Coding 場：**" + aHolders.Count + " 人在場**（範圍不重疊可同時開場）",
            };
            int aNoScope = 0;
            for (int i = 0; i < aHolders.Count; ++i)
            {
                SCP_ActivitySession aH = aHolders[i];
                var aS = SCP_ActivitySessionStore.Load<SCP_CodingSession>(iRoot, aH.persona,
                    SCP_ActivitySessionKind.Coding);
                string aScope = aS != null ? aS.scope : "";
                if (aScope.Length == 0) ++aNoScope;
                aLines.Add("  · **" + aH.persona + "**　`" + aH.session_id + "`");
                aLines.Add("    在改：" + (aS != null && aS.status.Length > 0 ? aS.status : "（沒寫 status）"));
                aLines.Add("    範圍：" + (aScope.Length > 0 ? "`" + aScope + "`"
                                            : "**（沒宣告 ⇒ 視同整棵樹，會擋所有人）**"));
                aLines.Add("    租期至：" + (aH.until_local.Length > 0 ? aH.until_local
                    : "（無截止 —— 這種場回收不了，見 Session_Kinds.md §5.5）"));
            }
            if (aNoScope > 0)
                aLines.Add("⚠ 其中 **" + aNoScope + " 場沒宣告範圍** ⇒ 那幾場會擋下所有人，不論你宣告什麼範圍。");
            var aOut = SCP_CmdResult.Success(aLines.ToArray());
            return aOut.AddValue("held", "1").AddValue("holders", aHolders.Count.ToString())
                       .AddValue("holder", aHolders[0].persona)
                       .AddValue("no_scope", aNoScope.ToString());
        }

        // ── start ─────────────────────────────────────────────────

        static SCP_CmdResult OpStart(SCP_DataRoot iRoot, string iPersona, string iStatus, int iHours,
                                     string iTasks, string iScope)
        {
            if (iPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=start 需要 --arg persona=<你>（不猜身分）");
            if (iStatus.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ op=start 需要 --arg status=<正在改哪一部分，一句話>",
                    "  ⚠ 它不是文書工作：**別人被擋下時看到的就是這一句**。沒有它，擋人的訊息說不出你在做什麼。");
            if (iHours < 0) return SCP_CmdResult.Fail(2, "✗ --arg hours 要是正整數（小時）");

            // ⚠ 範圍**解不開**時當場擋下，⛔ 不要靜默退化成「沒宣告」——
            //   那會讓打錯路徑的人拿到一個他沒要的全域鎖，而輸出上跟「我刻意不宣告」一模一樣。
            string aScope = "";
            if (iScope.Length > 0)
            {
                if (!SCP_SessionScope.TryNormalize(iScope, out aScope, out string aScopeErr))
                    return SCP_CmdResult.Fail(2,
                        "✗ --arg scope 解析不了：`" + iScope + "`"
                            + (aScopeErr.Length > 0 ? "（" + aScopeErr + "）" : ""),
                        "  要的是**絕對路徑**，例：`D:/Unity/LY/Assets/Plugins/UCL_Core`");
            }

            DateTime aNow = DateTime.Now;

            // ===========================================================
            // 同 kind 守衛 —— ⚠ 這一格**不在** `TryStart` 裡，而那是刻意的：
            //   共用層明寫「同 kind 疊開由各 kind 自己的守衛管」⇒ **每個入口少寫這一段就等於沒有守衛**，
            //   而它不會報錯。@summit 2026-09-05 在 Unity 那個入口補了同樣一段（`0d9eae1c`）。
            // 🩸 而本入口的活體（basecamp QA 自己量的，2026-09-05 23:15）：
            //   Template 已持有一場 Coding（`…151531Z`，租期至 01:15）⇒ 從本入口再 `op=start`
            //   ⇒ **exit 0、輸出寫「✓ 進場」**，回讀那個檔：session_id 換掉、status 換掉、租期重設，
            //   md5 `3f67bd61` → `b1cb5bfe`。**同一份輸出還印著「兩邊互相擋得到」** ——
            //   那句話對**跨人**成立，對**同一個人**不成立。
            // 📌 教訓寫在這裡而不是單子上：**補一個入口不等於補好那個洞**。
            //   ⇒ 之後每新增一個 Coding 進場入口，這一段就要再寫一次（或改走共用判定）。
            // ===========================================================
            var aMine = SCP_ActivitySessionStore.Load<SCP_CodingSession>(iRoot, iPersona,
                                                                        SCP_ActivitySessionKind.Coding);
            if (aMine != null && aMine.active)
            {
                bool aRunning = aMine.IsRunningAt(aNow, out _);
                var aBlockedMine = SCP_CmdResult.Fail(2,
                    "⛔ 進場被擋 —— **沒有開場**（你已經有一場 Coding）",
                    "  · 你的場：`" + aMine.session_id + "`　在改：**"
                        + (aMine.status.Length > 0 ? aMine.status : "（沒寫 status）") + "**",
                    "  · 租期至：" + (aMine.until_local.Length > 0 ? aMine.until_local : "（沒寫截止時刻）")
                        + "　⇒ " + (aRunning ? "**未到期**" : "**已到期**（落回殘留）"));
                // ⚠ 兩態的處置**相反**：未到期 ⇒ 改狀態就好；已到期 ⇒ 要人決定續期還是收掉。
                if (aRunning)
                {
                    aBlockedMine.Lines.Add("  改狀態就好，不必重開（順手續期）："
                        + SCP_CmdRegistry.Invoke("coding --arg op=status --arg persona=" + iPersona
                                                 + " --arg status=<一句話>"));
                    aBlockedMine.Lines.Add("  ⛔ 重開一場**不是**改狀態 —— 它會換掉 session_id 並重設租期。");
                }
                else
                {
                    aBlockedMine.Lines.Add("  那一場**已經到期**（落回殘留）。二選一，都要顯式：");
                    aBlockedMine.Lines.Add("  · 還在改 ⇒ 續期："
                        + SCP_CmdRegistry.Invoke("coding --arg op=status --arg persona=" + iPersona
                                                 + " --arg status=<一句話>"));
                    aBlockedMine.Lines.Add("  · 不改了 ⇒ 先收掉再開新的："
                        + SCP_CmdRegistry.Invoke("coding --arg op=end --arg persona=" + iPersona));
                    aBlockedMine.Lines.Add("  ⛔ 本 Cmd **不替你自動續期也不自動收** —— 那兩件事的差別只有你知道。");
                }
                return aBlockedMine.AddValue("started", "0");
            }

            DateTime aUntil = aNow.AddHours(iHours);
            var aSession = new SCP_CodingSession
            {
                persona = iPersona,
                session_id = "coding-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") + "-" + iPersona,
                start_ts = SCP_ActivitySession.NowIso(),
                end_ts = aUntil.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                until_local = aUntil.ToString("yyyy-MM-dd HH:mm"),
                active = true,
                status = iStatus,
                status_updated = SCP_ActivitySession.NowIso(),
                tasks = NormalizeTasks(iTasks),
                scope = aScope,
            };

            if (!SCP_ActivitySessionStore.TryStart(iRoot, iPersona, aSession, SCP_ActivitySessionKind.Coding,
                                                   aNow, out SCP_ActivitySession? aBlocker, aScope))
            {
                if (aBlocker == null)
                    return SCP_CmdResult.Fail(70, "✗ session 寫入失敗（不是被擋）—— 確認資料根可寫：" + iRoot.Value);
                return Blocked(iRoot, iPersona, aBlocker, aScope);
            }

            var aOk = SCP_CmdResult.Success(
                "✓ 進場：**" + iPersona + "** 的 Coding 場　`" + aSession.session_id + "`",
                "· 在改：" + iStatus,
                "· 租期至 **" + aSession.until_local + "**（" + iHours + " 小時）—— 續期跑 "
                    + SCP_CmdRegistry.Invoke("coding --arg op=status --arg persona=" + iPersona + " --arg status=<一句>"),
                "⚠ 租期到期**不會自動釋放**，只是落回「殘留」；別人要搶場得顯式 "
                    + SCP_CmdRegistry.Invoke("sessions --arg op=close --arg target_persona=" + iPersona + " --arg confirm=1"),
                ScopeCaveat);
            // ⚠ 沒宣告範圍要**明說它的後果**，不要安靜 —— 安靜的話「我沒宣告」會被讀成「我宣告了全部」，
            //   而那兩句話在擋人的結果上一樣、在使用者的預期上相反。
            aOk.Lines.Add(aScope.Length > 0
                ? "· 施工範圍：`" + aScope + "` ⇒ **範圍不重疊的人可以同時開場**"
                : "· ⚠ **沒宣告施工範圍** ⇒ 本場退化成**整個 kind 全域獨佔**（誰都進不來）。要宣告："
                    + SCP_CmdRegistry.Invoke("coding --arg op=start --arg persona=" + iPersona
                                             + " --arg status=<一句> --arg scope=<絕對路徑>"));
            // ⚠ 沒綁單就明說「不會自動收」 —— 這一格如果安靜，人會以為自動收場對所有場都成立。
            aOk.Lines.Add(aSession.tasks.Length > 0
                ? "· 綁定單：**" + aSession.tasks + "** ⇒ 它們**全部**離開施工狀態（in_review／done）時本場會自動收"
                : "· ⚠ **沒有綁單** ⇒ 本場**不會自動收**（只能手動 op=end）。要綁："
                    + SCP_CmdRegistry.Invoke("coding --arg op=bind --arg persona=" + iPersona + " --arg tasks=<單號>"));
            return aOk.AddValue("session_id", aSession.session_id).AddValue("until_local", aSession.until_local)
                      .AddValue("tasks", aSession.tasks).AddValue("scope", aScope);
        }

        static SCP_CmdResult Blocked(SCP_DataRoot iRoot, string iPersona, SCP_ActivitySession iBlocker,
                                     string iScope)
        {
            bool aMine = string.Equals(iBlocker.persona, iPersona, StringComparison.Ordinal);
            // ⚠ 兩條軸擋下來的東西**不同形**，處理方式相反：
            //   我自己在別的場 ⇒ 去收自己的場；別人持有 Coding ⇒ 等或去問他。
            if (aMine)
            {
                return SCP_CmdResult.Fail(2,
                    "✗ 你已經在另一種 session 裡：**" + iBlocker.kind + "**（`" + iBlocker.session_id + "`）",
                    "  處理方式：先收掉那場（該 kind 自己的收工步驟）");
            }
            var aHeld = SCP_ActivitySessionStore.Load<SCP_CodingSession>(iRoot, iBlocker.persona,
                SCP_ActivitySessionKind.Coding);
            string aTheirScope = aHeld != null ? aHeld.scope : "";
            // ⚠ **擋下的理由有三種，處置不同** —— 壓成一句「場被佔了」會讓人去做錯的那件事。
            //   ⛔ 判定與措辭**不寫在這裡**：同一段話 Unity 那側也要印，寫兩份就是 TASK-0203 那隻。
            SCP_SessionScope.BlockKind aBlockKind = SCP_SessionScope.Classify(iScope, aTheirScope);
            string aWhy = "  · " + SCP_SessionScope.ReasonOf(aBlockKind, iScope, aTheirScope);
            string aScopeExit = SCP_SessionScope.ExitOf(aBlockKind,
                SCP_CmdRegistry.Invoke("coding --arg op=start --arg persona=" + iPersona
                                       + " --arg status=<一句> --arg scope=<絕對路徑>"));
            return SCP_CmdResult.Fail(2,
                "✗ **@" + iBlocker.persona + "** 正在 Coding（`" + iBlocker.session_id + "`）—— 而他的範圍擋到你",
                "  · 他在改：" + (aHeld != null && aHeld.status.Length > 0 ? aHeld.status : "（沒寫 status）"),
                "  · 他的範圍：" + (aTheirScope.Length > 0 ? "`" + aTheirScope + "`" : "**（沒宣告 ⇒ 整棵樹）**"),
                "  · 你的範圍：" + (iScope.Length > 0 ? "`" + iScope + "`" : "**（沒宣告 ⇒ 整棵樹）**"),
                aWhy,
                "  · 租期至：" + (iBlocker.until_local.Length > 0 ? iBlocker.until_local : "（無截止）"),
                // ⚠ 只有「我自己沒宣告」那一種有自己補得了的出口 —— 它要排在「等他」前面，
                //   不然讀的人會照著「等」去等一件他本來不必等的事。
                aScopeExit.Length > 0 ? "  處理方式：" + aScopeExit
                    : "  處理方式：等他到期，或去酒館問他還要多久；查現況 " + SCP_CmdRegistry.Invoke("coding"),
                "  ⛔ 不要直接關別人的場 —— 那要顯式走 "
                    + SCP_CmdRegistry.Invoke("sessions --arg op=close --arg target_persona=" + iBlocker.persona + " --arg confirm=1"));
        }

        // ── status（更新 ＋ 續期）─────────────────────────────────

        static SCP_CmdResult OpStatus(SCP_DataRoot iRoot, string iPersona, string iStatus, int iHours)
        {
            if (iPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=status 需要 --arg persona=<你>");
            if (iStatus.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=status 需要 --arg status=<改成哪一句>");
            if (iHours < 0) return SCP_CmdResult.Fail(2, "✗ --arg hours 要是正整數（小時）");

            var aS = Mine(iRoot, iPersona, out SCP_CmdResult? aErr);
            if (aS == null) return aErr!;

            DateTime aUntil = DateTime.Now.AddHours(iHours);
            aS.status = iStatus;
            aS.status_updated = SCP_ActivitySession.NowIso();
            // ⭐ 續期掛在**本來就要跑的那一步**上，不掛在「記得去續」——
            //   那是 PM 2026-09-05 拍 (A) 時附的判準。
            aS.end_ts = aUntil.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            aS.until_local = aUntil.ToString("yyyy-MM-dd HH:mm");
            if (!SCP_ActivitySessionStore.Save(iRoot, iPersona, aS, SCP_ActivitySessionKind.Coding))
                return SCP_CmdResult.Fail(70, "✗ 寫不進去：" + SCP_ActivitySessionStore.PathOf(iRoot, iPersona));

            return SCP_CmdResult.Success(
                "✓ status 已更新：" + iStatus,
                "· 租期順手續到 **" + aS.until_local + "**（" + iHours + " 小時）",
                ScopeCaveat).AddValue("until_local", aS.until_local);
        }

        // ── end（過閘才放行）─────────────────────────────────────

        // 區塊職責：把幾張單**補綁**到某人現有的 Coding 場上（load → 合併 → 落檔 → 回讀）。
        // 物理意義：這一段有**兩個消費端** —— 本檔的 `op=bind`，與 Unity 側 `Cmd_Task` 的
        //           「認領一張單順手綁到我的場上」（TASK-0202）。
        //           ⛔ 兩邊各寫一次的話，「去前導零／去重」那層正規化遲早只有一邊有，
        //           而失效樣子是**自動收場永遠不成立**（綁的是 `0202`、推進的是 `202`，比不到）
        //           —— 沒有人會知道是比對沒對上。
        // 數值影響：一次 Save ＋ 一次回讀。⛔ 回傳的是**回讀那一份**，不是寫入端的回傳值。
        /// <summary>把 <paramref name="iTasks"/> 併進這個人現有 Coding 場的 `tasks`；回讀後的值。沒有場／落不了檔回 null。</summary>
        public static string? BindTasks(SCP_DataRoot iRoot, string iPersona, string iTasks)
        {
            var aS = SCP_ActivitySessionStore.Load<SCP_CodingSession>(iRoot, iPersona,
                                                                     SCP_ActivitySessionKind.Coding);
            if (aS == null || !aS.active) return null;
            aS.tasks = NormalizeTasks(aS.tasks + "," + iTasks);
            SCP_ActivitySessionStore.Save(iRoot, iPersona, aS);
            var aBack = SCP_ActivitySessionStore.Load<SCP_CodingSession>(iRoot, iPersona,
                                                                        SCP_ActivitySessionKind.Coding);
            return aBack == null ? null : aBack.tasks;
        }

        // 區塊職責：單號清單正規化 —— 去空白、去重、去前導零、保序。
        // 物理意義：`TASK-0129` / `129` / `0129` 是**同一張單**，而它們當字串比不相等。
        //           不正規化的話「綁的是 0129、推進的是 129」會讓自動收場永遠不成立，
        //           而那個失效的樣子是「場就是不會自己收」—— 沒有人會知道是比對沒對上。
        // 數值影響：純字串處理，不碰檔案。
        /// <summary>單號清單正規化。**公開**是因為 Unity 側 `Cmd_Task` 也要用同一份判準（TASK-0202）。</summary>
        public static string NormalizeTasks(string iRaw)
        {
            var aOut = new List<string>();
            var aSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (string aOne in (iRaw ?? "").Split(','))
            {
                string aT = aOne.Trim();
                if (aT.StartsWith("TASK-", StringComparison.OrdinalIgnoreCase)) aT = aT.Substring(5);
                aT = aT.TrimStart('0');
                if (aT.Length == 0 || !aSeen.Add(aT)) continue;
                aOut.Add(aT);
            }
            return string.Join(",", aOut.ToArray());
        }

        // 區塊職責：事後補綁單號（合併，不覆蓋）。
        // 物理意義：先進場才認領是常態 —— 綁定不會只發生在開場那一刻。
        // 數值影響：只動 session 檔的 `tasks` 欄；不碰租期、不碰狀態、不觸發收場。
        static SCP_CmdResult OpBind(SCP_DataRoot iRoot, string iPersona, string iTasks)
        {
            if (iPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=bind 需要 --arg persona=<你>");
            if (iTasks.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=bind 需要 --arg tasks=<單號，逗號分隔>");
            var aS = Mine(iRoot, iPersona, out SCP_CmdResult? aErr);
            if (aS == null) return aErr!;
            string? aRead = BindTasks(iRoot, iPersona, iTasks);
            if (aRead == null)
                return SCP_CmdResult.Fail(70, "✗ 綁定沒有落檔 —— 回讀不到那一場："
                    + SCP_ActivitySessionStore.PathOf(iRoot, iPersona));
            return SCP_CmdResult.Success(
                "✓ 綁定更新：**" + iPersona + "** 的 Coding 場 `" + aS.session_id + "`",
                "· 回讀 tasks = **" + aRead + "**（回讀單檔，不是寫入端的回傳值）",
                "· 這些單**全部**離開施工狀態（in_review／done）時本場會自動收")
                .AddValue("tasks", aRead);
        }

        // 區塊職責：列出工作區還沒提交的 `Assets/**/*.cs`。
        // 物理意義：射程**只到 Unity 端 C#**（Tim 2026-09-10：Senate 那邊可以同步改）——
        //           Senate 是獨立 repo、獨立編譯，不共用 Unity 的組件，不該被這道閘排隊。
        // 數值影響：純唯讀。**這是資訊不是閘** —— 有東西也照收，只記進 session 檔並印出來。
        static List<string> DirtyUnityCs(SCP_DataRoot iRoot)
        {
            var aOut = new List<string>();
            // data_root 是 `<專案>/AgentCommands` ⇒ 專案根是它的上一層。
            // ⛔ 不假設：推導完**驗它真的是 git 工作目錄**，不是就回空（呼叫端會說「沒量到」）。
            string aProj = Path.GetDirectoryName(iRoot.Value.TrimEnd('/', '\\')) ?? "";
            if (aProj.Length == 0 || !SCP_Git.IsRepo(aProj)) return aOut;
            CollectDirtyCs(aProj, "", aOut);
            // 🩸 **根層的 git status 看不見 submodule 裡的檔** —— 它只報「這個 submodule 變了」。
            //   而本專案的 Unity C# 幾乎全住在 `Assets/Plugins/SCP_Core` 與 `Assets/Plugins/UCL_Core`
            //   這兩顆 submodule 裡 ⇒ 只掃根層的話，`left_dirty_cs` 會在**真的有髒檔時回 0**。
            //   （2026-09-10 探針實測：改髒 `Assets/Plugins/SCP_Core/**/*.cs` ⇒ 讀數 0。
            //     那不是「範圍小」，那是**錯的讀數** —— 而它看起來跟乾淨一模一樣。）
            foreach (string aSub in SubmodulesUnderAssets(aProj))
                CollectDirtyCs(Path.Combine(aProj, aSub), aSub + "/", aOut);
            return aOut;
        }

        /// <summary>`.gitmodules` 裡路徑以 `Assets/` 開頭的 submodule —— 射程只到 Unity 那側。</summary>
        static List<string> SubmodulesUnderAssets(string iProjRoot)
        {
            var aOut = new List<string>();
            SCP_GitResult aR = SCP_Git.Run(iProjRoot, "config", "--file", ".gitmodules",
                                           "--get-regexp", "path");
            if (!aR.Ok) return aOut;
            foreach (string aLine in aR.OutLines())
            {
                int aSp = aLine.IndexOf(' ');
                if (aSp <= 0 || aSp + 1 >= aLine.Length) continue;
                string aPath = aLine.Substring(aSp + 1).Trim();
                if (aPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) aOut.Add(aPath);
            }
            return aOut;
        }

        /// <summary>porcelain 行首的狀態欄（1~2 個狀態字元 ＋ 空白）—— 見 CollectDirtyCs 的血證。</summary>
        static readonly Regex s_PorcelainHead = new Regex(@"^[ MADRCU?!]{1,2}\s+");

        /// <summary>單一 repo 的髒 `.cs`；<paramref name="iPrefix"/> 讓 submodule 內的路徑印得出全貌。</summary>
        static void CollectDirtyCs(string iRepo, string iPrefix, List<string> oOut)
        {
            var aOut = oOut;
            if (!SCP_Git.IsRepo(iRepo)) return;
            SCP_GitResult aSt = SCP_Git.Run(iRepo, "status", "--porcelain=v1", "--untracked-files=all");
            if (!aSt.Ok) return;
            foreach (string aLine in aSt.OutLines())
            {
                string aL = aLine.TrimEnd();
                if (aL.Length < 4) continue;
                // 🩸 ⛔ 不要用 `Substring(3)` —— porcelain 原始行是 `XY path`（前 3 格固定），
                //   但 `SCP_Git.OutLines()` **會把左邊的空白去掉** ⇒ ` M path` 進來時已經是 `M path`，
                //   固定切 3 個字會**多吃掉路徑的第一個字元**。
                //   （2026-09-10 探針實測：印出 `UCL_Core/CL_Core_Scripts/…`，開頭的 U 不見了。
                //     那種壞法不會報錯，只會產出一條**看起來很像真的**的假路徑。）
                //   ⇒ 改成把狀態欄整段吃掉，不假設它有幾個字。
                Match aM = s_PorcelainHead.Match(aL);
                if (!aM.Success) continue;
                string aPath = aL.Substring(aM.Length).Trim();
                int aArrow = aPath.IndexOf(" -> ", StringComparison.Ordinal);
                if (aArrow >= 0) aPath = aPath.Substring(aArrow + 4);
                aPath = aPath.Trim('"');
                if (!aPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                string aFull = iPrefix + aPath;
                // ⚠ 射程守衛：只收 `Assets/` 底下的 —— submodule 那條已經由 iPrefix 保證，
                //   根層那條要靠這一行（`Senate/` 不在這棵樹裡，本來就進不來）。
                if (aFull.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase)) aOut.Add(aFull);
            }
        }

        // 區塊職責：綁定單全部離開施工狀態 ⇒ 自動收場。
        // 物理意義：目的是**縮短持有**（Tim 2026-09-10）—— 判準是 `in_review` **不是** `done`，
        //           不等全部驗完。`in_review` 被退回就下次動工開新的場，⛔ 不把舊場接回來。
        // 數值影響：條件不成立時**什麼都不做**（場仍是持有者的），並說得出是哪一格擋的。
        //          ⚠ 編譯閘紅燈**不收** —— 收場成功若不再代表「這棵樹編得過」，
        //            它就跟「有人結了單」同形，而同形之後沒有一層會說出差別。
        static SCP_CmdResult OpAutoClose(SCP_DataRoot iRoot, string iPersona)
        {
            if (iPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=autoclose 需要 --arg persona=<你>");
            var aS = SCP_ActivitySessionStore.Load<SCP_CodingSession>(iRoot, iPersona,
                                                                      SCP_ActivitySessionKind.Coding);
            // ⚠ 沒有場**不是失敗** —— 這一支會被掛在 commit 的必經路上，那裡多數時候本來就沒有場。
            if (aS == null || !aS.active)
                return SCP_CmdResult.Success("· 沒有進行中的 Coding 場 ⇒ 不必收")
                    .AddValue("autoclose", "no-session");
            if (aS.tasks.Length == 0)
                return SCP_CmdResult.Success("· 本場**沒有綁單** ⇒ 不自動收（綁定是自動收場唯一的判準）")
                    .AddValue("autoclose", "unbound");

            var aWarn = new List<string>();
            List<SCP_TaskEntry> aAll = SCP_TaskIO.LoadAll(iRoot, aWarn.Add);
            var aStillWorking = new List<string>();
            var aMissing = new List<string>();
            foreach (string aIdx in aS.tasks.Split(','))
            {
                string aWant = aIdx.Trim();
                if (aWant.Length == 0) continue;
                // ⚠ 這裡要寫 `SCP_TaskEntry?`：Unity 那側 nullable 沒開，漏了它照樣綠燈；
                //   而 senate 側 `dotnet build` 會 CS8600 紅燈。**兩側各自量才看得見**（TASK-0193 現場）。
                SCP_TaskEntry? aHit = null;
                foreach (var aT in aAll)
                    if (aT.index.ToString(CultureInfo.InvariantCulture) == aWant) { aHit = aT; break; }
                if (aHit == null) { aMissing.Add(aWant); continue; }
                // ⚠ 判準是 **離開施工狀態**，不是「做完」——`in_review` 就算（Tim 2026-09-10：縮短持有）。
                //   `cancelled` 也算：它不會再有人動，繼續佔著場沒有意義。
                SCP_TaskStatus aSt = aHit.status;
                if (aSt != SCP_TaskStatus.in_review && aSt != SCP_TaskStatus.done
                    && aSt != SCP_TaskStatus.cancelled)
                    aStillWorking.Add(aWant + "(" + aSt + ")");
            }
            // ⛔ 查無此單**不當成「做完了」** —— 那會讓打錯的單號變成一張自動放行的通行證。
            if (aMissing.Count > 0)
                return SCP_CmdResult.Success("· 不自動收：綁定單裡有**查無此單**的號 —— "
                        + string.Join("／", aMissing.ToArray()) + "（查無 ≠ 做完）")
                    .AddValue("autoclose", "task-missing");
            if (aStillWorking.Count > 0)
                return SCP_CmdResult.Success("· 不自動收：還有單在施工狀態 —— "
                        + string.Join("／", aStillWorking.ToArray()))
                    .AddValue("autoclose", "still-working");

            SCP_CodingExitVerdict? aVerdict = SCP_CodingExitGateHost.Run();
            if (aVerdict != null && !aVerdict.Value.Green)
                return SCP_CmdResult.Success(
                        "· ⛔ **不自動收：編譯閘紅燈** —— " + aVerdict.Value.Summary,
                        "  場**還是你的**。修完再收，或顯式硬退："
                            + SCP_CmdRegistry.Invoke("coding --arg op=end --arg persona=" + iPersona
                                                     + " --arg force=1 --arg force_reason=<為什麼帶著紅燈退場>"))
                    .AddValue("autoclose", "compile-red");

            List<string> aDirty = DirtyUnityCs(iRoot);
            aS.left_dirty_cs = string.Join(",", aDirty.ToArray());
            SCP_ActivitySessionStore.Close(iRoot, iPersona, aS, "coding-autoclose");
            var aBack = SCP_ActivitySessionStore.Load(iRoot, iPersona);
            bool aClosed = aBack != null && !aBack.active;

            var aOk = SCP_CmdResult.Success(
                "✓ **自動收場**：`" + aS.session_id + "`　**回讀確認=" + aClosed + "**",
                "· 綁定單 **" + aS.tasks + "** 全部離開施工狀態（判準是 `in_review`，⛔ 不等 `done`）",
                "- 🔒 編譯閘：" + (aVerdict == null
                    ? "**本宿主沒有登記退出閘 ⇒ 未驗編譯**（這不是綠燈，是沒有量）"
                    : "**綠燈** —— " + aVerdict.Value.Summary));
            if (aDirty.Count > 0)
            {
                // ⚠ 這一段是**資訊不是閘**：擋下會讓場握得更久，而縮短持有正是它存在的理由。
                aOk.Lines.Add("⚠ 收場時工作區還有 **" + aDirty.Count + " 個未提交的 Unity C#**"
                              + "（已記進 session 檔，下一個進場的人看得到）：");
                foreach (string aF in aDirty) aOk.Lines.Add("     - " + aF);
                aOk.Lines.Add("  ⛔ 這**不擋收場** —— 但它們還沒進版控，別忘了。");
            }
            aOk.Lines.Add("· `in_review` 被退回時 ⇒ **下次動工開新的場**（⛔ 不把這一場接回來）。");
            return aOk.AddValue("autoclose", "closed")
                      .AddValue("left_dirty_cs", aDirty.Count.ToString(CultureInfo.InvariantCulture));
        }

        static SCP_CmdResult OpEnd(SCP_DataRoot iRoot, string iPersona, bool iForce, string iForceReason)
        {
            if (iPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=end 需要 --arg persona=<你>");
            var aS = Mine(iRoot, iPersona, out SCP_CmdResult? aErr);
            if (aS == null) return aErr!;

            // ⚠ TASK-0201：多場並行之後，**編譯閘量的是整棵樹，不是我那一塊** ——
            //   它讀不出「這個紅字是誰造的」。⇒ 這裡把「同時還有誰在場」印出來，讓讀的人自己判。
            //   ⛔ **不拿它去放寬紅燈**（那會讓我自己弄壞的也一起被放過）；也⛔ 不擋 ——
            //     只是把一個沒有定語的讀數補上定語。
            var aOthers = SCP_ActivitySessionStore.ListRunningGlobal(
                iRoot, SCP_ActivitySessionKind.Coding, DateTime.Now);
            var aOtherNames = new List<string>();
            for (int i = 0; i < aOthers.Count; ++i)
                if (!string.Equals(aOthers[i].persona, iPersona, StringComparison.Ordinal))
                    aOtherNames.Add("@" + aOthers[i].persona);
            string aConcurrentNote = aOtherNames.Count == 0
                ? ""
                : "  ⚠ **同時在場的還有 " + string.Join("、", aOtherNames.ToArray())
                  + "** ⇒ 這個編譯讀數涵蓋整棵樹，**它分不出紅字是誰造的**。";

            SCP_CodingExitVerdict? aVerdict = SCP_CodingExitGateHost.Run();
            var aLines = new List<string>();
            if (aVerdict == null)
            {
                // ⚠ 沒登記閘 ≠ 綠燈。這兩件事印同一句話，就是這個專案反覆咬人的那個形狀。
                aLines.Add("- 🔒 編譯閘：**本宿主沒有登記退出閘 ⇒ 未驗編譯**（這不是綠燈，是沒有量）");
            }
            else if (!aVerdict.Value.Green && !iForce)
            {
                var aRed = SCP_CmdResult.Fail(2,
                    "✗ 編譯閘**紅燈** —— 不放行：" + aVerdict.Value.Summary,
                    "  射程：" + aVerdict.Value.Scope);
                if (aConcurrentNote.Length > 0)
                {
                    aRed.Lines.Add(aConcurrentNote);
                    aRed.Lines.Add("    ⇒ 先看紅的是不是你範圍內的檔；不是的話去問他，"
                                   + "⛔ 別替別人改（也別拿它當 force 的理由）。");
                }
                aRed.Lines.AddRange(new[]
                {
                    "  處理方式（擇一）：",
                    "    ① 修完再退（**建議**）",
                    "    ② 顯式硬退：" + SCP_CmdRegistry.Invoke("coding --arg op=end --arg persona=" + iPersona + " --arg force=1 --arg force_reason=<為什麼帶著紅燈退場>"),
                    "  ⚠ ② 的理由會寫進 session 檔 —— 事後查得到是誰、為什麼。",
                });
                return aRed;
            }
            else if (!aVerdict.Value.Green)
            {
                aLines.Add("- 🔒 編譯閘：**紅燈，而你顯式 force 了** —— " + aVerdict.Value.Summary);
                aLines.Add("  · 理由（已落檔）：" + (iForceReason.Length > 0 ? iForceReason : "（沒寫 —— 下次請寫）"));
                aS.force_reason = iForceReason;
            }
            else
            {
                aLines.Add("- 🔒 編譯閘：**綠燈** —— " + aVerdict.Value.Summary);
                aLines.Add("  · 射程：" + aVerdict.Value.Scope);
            }
            // ⚠ 綠燈也要這句：綠的射程同樣是整棵樹 ⇒ 它**不是**「我那一塊是對的」的證據，
            //   而是「此刻整棵樹編得過」。兩者在同一個字上，差別只有定語。
            if (aConcurrentNote.Length > 0) aLines.Add(aConcurrentNote);

            // Coding 沒有金流 ⇒ base close（翻三欄）。⚠ 這是**顯式的**，不是「還沒接結算」。
            SCP_ActivitySessionStore.Close(iRoot, iPersona, aS, iForce ? "coding-end-forced" : "coding-end");
            var aBack = SCP_ActivitySessionStore.Load(iRoot, iPersona);
            bool aClosed = aBack != null && !aBack.active;
            aLines.Insert(0, "✓ 退場：**" + iPersona + "**　`" + aS.session_id + "`　**回讀確認=" + aClosed + "**");
            aLines.Add("- 💰 結算：Coding **沒有金流**（顯式，不是漏接）⇒ 只翻三欄");
            aLines.Add(ScopeCaveat);
            var aResult = aClosed ? SCP_CmdResult.Success(aLines.ToArray())
                                  : SCP_CmdResult.Fail(70, aLines.ToArray());
            return aResult.AddValue("closed", aClosed ? "1" : "0");
        }

        // ── 共用 ──────────────────────────────────────────────────

        /// <summary>取「我自己那場進行中的 Coding」；不是的話回 null 並把原因放進 <paramref name="oErr"/>。</summary>
        static SCP_CodingSession? Mine(SCP_DataRoot iRoot, string iPersona, out SCP_CmdResult? oErr)
        {
            oErr = null;
            var aS = SCP_ActivitySessionStore.Load<SCP_CodingSession>(iRoot, iPersona, SCP_ActivitySessionKind.Coding);
            if (aS == null)
            {
                // ⚠ 三態不同形：沒有檔／有檔但不是 Coding／是 Coding 但已收工。
                var aAny = SCP_ActivitySessionStore.Load(iRoot, iPersona);
                oErr = aAny == null
                    ? SCP_CmdResult.Fail(2, "✗ `" + iPersona + "` 沒有任何 session 檔 —— 先 op=start")
                    : SCP_CmdResult.Fail(2, "✗ `" + iPersona + "` 現在的場是 **" + aAny.kind + "** 不是 Coding"
                        + "（`" + aAny.session_id + "`）");
                return null;
            }
            if (!aS.active)
            {
                oErr = SCP_CmdResult.Fail(2, "✗ 這場已經收過工（end_reason=`" + aS.end_reason + "`）—— 先 op=start 開新的");
                return null;
            }
            return aS;
        }

        /// <summary>射程定語 —— A2 落地後這句話要跟著改，所以它只有一份。</summary>
        internal const string ScopeCaveat =
            "⚠ 射程：本入口是 **Senate 側**（不需要 Editor）；Unity 那側走 `ucmd run Coding`。"
            + "兩邊同一個檔位 ⇒ 互相擋得到。";
    }
}
