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

        public override string Summary => "Coding 施工場（改 C# 前進場／場中更新 status 兼續期／退場過編譯閘）—— **不需要 Editor**";

        public override string Details =>
            "⛔ **射程**：本 Cmd 是 **Senate 側**的入口（TASK-0058 A2）。Unity 那側走 `ucmd run Coding`。\n"
            + "兩邊寫的是**同一個檔位**（`<data_root>/sessions/<persona>.json`）⇒ 互相擋得到。\n"
            + "⭐ 全域獨佔（同時至多一人）由 `SCP_ActivitySessionStore.TryStart` 那一層保證，本 Cmd 不自己判 ——\n"
            + "   自己判就是第三份判準，而它會跟前兩份不一致且**不報錯**。\n"
            + "⚠ `op=start` 一律帶租期（預設 " + DefaultLeaseHours + " 小時）：沒有 `end_ts` 的場永遠不會變成殘留，\n"
            + "   而那代表**持有者掉線之後沒有人能回收它**。續期走 `op=status`（那一步本來就要跑）。\n"
            + "⚠ `op=end` 的編譯閘**由宿主注入**：沒登記時明說「未驗編譯」——「沒有量」不是「綠燈」。\n"
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
                                             iArgs.Get("tasks").Trim());
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

        static SCP_CmdResult OpShow(SCP_DataRoot iRoot)
        {
            SCP_ActivitySession? aHolder = SCP_ActivitySessionStore.FindRunningGlobal(
                iRoot, SCP_ActivitySessionKind.Coding, DateTime.Now);
            if (aHolder == null)
            {
                var aFree = SCP_CmdResult.Success("· Coding 場：**沒有人持有**（掃全體 session 檔）",
                    "⚠ 「沒查到」的射程：**只涵蓋走過 `TryStart` 的那些場** —— 直接 `Save` 開的場不在裡面。");
                return aFree.AddValue("held", "0");
            }
            var aS = SCP_ActivitySessionStore.Load<SCP_CodingSession>(iRoot, aHolder.persona,
                SCP_ActivitySessionKind.Coding);
            var aOut = SCP_CmdResult.Success(
                "· Coding 場持有者：**" + aHolder.persona + "**　`" + aHolder.session_id + "`",
                "· 在改：" + (aS != null && aS.status.Length > 0 ? aS.status : "（沒寫 status）"),
                "· 租期至：" + (aHolder.until_local.Length > 0 ? aHolder.until_local : "（無截止 —— 這種場回收不了，見 Session_Kinds.md §5.5）"));
            return aOut.AddValue("held", "1").AddValue("holder", aHolder.persona);
        }

        // ── start ─────────────────────────────────────────────────

        static SCP_CmdResult OpStart(SCP_DataRoot iRoot, string iPersona, string iStatus, int iHours,
                                     string iTasks)
        {
            if (iPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=start 需要 --arg persona=<你>（不猜身分）");
            if (iStatus.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ op=start 需要 --arg status=<正在改哪一部分，一句話>",
                    "  ⚠ 它不是文書工作：**別人被擋下時看到的就是這一句**。沒有它，擋人的訊息說不出你在做什麼。");
            if (iHours < 0) return SCP_CmdResult.Fail(2, "✗ --arg hours 要是正整數（小時）");

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
            };

            if (!SCP_ActivitySessionStore.TryStart(iRoot, iPersona, aSession, SCP_ActivitySessionKind.Coding,
                                                   aNow, out SCP_ActivitySession? aBlocker))
            {
                if (aBlocker == null)
                    return SCP_CmdResult.Fail(70, "✗ session 寫入失敗（不是被擋）—— 確認資料根可寫：" + iRoot.Value);
                return Blocked(iRoot, iPersona, aBlocker);
            }

            var aOk = SCP_CmdResult.Success(
                "✓ 進場：**" + iPersona + "** 的 Coding 場　`" + aSession.session_id + "`",
                "· 在改：" + iStatus,
                "· 租期至 **" + aSession.until_local + "**（" + iHours + " 小時）—— 續期跑 "
                    + SCP_CmdRegistry.Invoke("coding --arg op=status --arg persona=" + iPersona + " --arg status=<一句>"),
                "⚠ 租期到期**不會自動釋放**，只是落回「殘留」；別人要搶場得顯式 "
                    + SCP_CmdRegistry.Invoke("sessions --arg op=close --arg target_persona=" + iPersona + " --arg confirm=1"),
                ScopeCaveat);
            // ⚠ 沒綁單就明說「不會自動收」 —— 這一格如果安靜，人會以為自動收場對所有場都成立。
            aOk.Lines.Add(aSession.tasks.Length > 0
                ? "· 綁定單：**" + aSession.tasks + "** ⇒ 它們**全部**離開施工狀態（in_review／done）時本場會自動收"
                : "· ⚠ **沒有綁單** ⇒ 本場**不會自動收**（只能手動 op=end）。要綁："
                    + SCP_CmdRegistry.Invoke("coding --arg op=bind --arg persona=" + iPersona + " --arg tasks=<單號>"));
            return aOk.AddValue("session_id", aSession.session_id).AddValue("until_local", aSession.until_local)
                      .AddValue("tasks", aSession.tasks);
        }

        static SCP_CmdResult Blocked(SCP_DataRoot iRoot, string iPersona, SCP_ActivitySession iBlocker)
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
            return SCP_CmdResult.Fail(2,
                "✗ **@" + iBlocker.persona + "** 正在 Coding（`" + iBlocker.session_id + "`）—— 這種場全域同時只能一個人",
                "  · 他在改：" + (aHeld != null && aHeld.status.Length > 0 ? aHeld.status : "（沒寫 status）"),
                "  · 租期至：" + (iBlocker.until_local.Length > 0 ? iBlocker.until_local : "（無截止）"),
                "  處理方式：等他到期，或去酒館問他還要多久；查現況 " + SCP_CmdRegistry.Invoke("coding"),
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

        // 區塊職責：單號清單正規化 —— 去空白、去重、去前導零、保序。
        // 物理意義：`TASK-0129` / `129` / `0129` 是**同一張單**，而它們當字串比不相等。
        //           不正規化的話「綁的是 0129、推進的是 129」會讓自動收場永遠不成立，
        //           而那個失效的樣子是「場就是不會自己收」—— 沒有人會知道是比對沒對上。
        // 數值影響：純字串處理，不碰檔案。
        static string NormalizeTasks(string iRaw)
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
            aS.tasks = NormalizeTasks(aS.tasks + "," + iTasks);
            SCP_ActivitySessionStore.Save(iRoot, iPersona, aS);
            // ⛔ 不信寫入端的回傳 —— 回讀那個欄位。
            var aBack = SCP_ActivitySessionStore.Load<SCP_CodingSession>(iRoot, iPersona,
                                                                        SCP_ActivitySessionKind.Coding);
            string aRead = aBack == null ? "" : aBack.tasks;
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
            SCP_GitResult aSt = SCP_Git.Run(aProj, "status", "--porcelain=v1", "--untracked-files=all");
            if (!aSt.Ok) return aOut;
            foreach (string aLine in aSt.OutLines())
            {
                string aL = aLine.TrimEnd();
                if (aL.Length < 4) continue;
                string aPath = aL.Substring(3).Trim();
                int aArrow = aPath.IndexOf(" -> ", StringComparison.Ordinal);
                if (aArrow >= 0) aPath = aPath.Substring(aArrow + 4);
                aPath = aPath.Trim('"');
                if (aPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)
                    && aPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                    aOut.Add(aPath);
            }
            return aOut;
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

            SCP_CodingExitVerdict? aVerdict = SCP_CodingExitGateHost.Run();
            var aLines = new List<string>();
            if (aVerdict == null)
            {
                // ⚠ 沒登記閘 ≠ 綠燈。這兩件事印同一句話，就是這個專案反覆咬人的那個形狀。
                aLines.Add("- 🔒 編譯閘：**本宿主沒有登記退出閘 ⇒ 未驗編譯**（這不是綠燈，是沒有量）");
            }
            else if (!aVerdict.Value.Green && !iForce)
            {
                return SCP_CmdResult.Fail(2,
                    "✗ 編譯閘**紅燈** —— 不放行：" + aVerdict.Value.Summary,
                    "  射程：" + aVerdict.Value.Scope,
                    "  處理方式（擇一）：",
                    "    ① 修完再退（**建議**）",
                    "    ② 顯式硬退：" + SCP_CmdRegistry.Invoke("coding --arg op=end --arg persona=" + iPersona + " --arg force=1 --arg force_reason=<為什麼帶著紅燈退場>"),
                    "  ⚠ ② 的理由會寫進 session 檔 —— 事後查得到是誰、為什麼。");
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
