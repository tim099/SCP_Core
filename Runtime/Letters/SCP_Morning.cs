// 區塊職責：早安流程（wake／brief／intro 前置與標頭）的**邏輯層** —— 不需要 Unity Editor。
// 物理意義：移植自 UCL_Core `UCL_AwakeningService`（StepWake／RunBrief／PrecheckIntro／BuildIntroHeader）
//          與 `Cmd_GoodMorning`（TASK-0303，Tim 2026-09-26：「Editor 卡住時早安也卡住，讓早安不再依賴 Editor」）。
//          寫入的檔、欄位名、格式逐一對齊 Editor 版（lock／_tokens.json／memo／profile 兩欄／審計行），
//          ⇒ Editor 端、python 端、SCP_PersonaLetters 等既有讀者**不必改**就讀得懂。
// 與 Editor 版刻意的差異（寫在這裡讓人查得到，不是漏移植）：
//   ① lock 讀得到檔卻解析不了（Unknown）⇒ **擋**。Editor 版 ReadLock 回 null ⇒ 當成離線放行，
//      然後覆寫那顆壞 lock ——「壞 lock」與「沒人在線」同形，放行的方向是製造分身。
//   ② `_tokens.json` 的 read-modify-write 包在跨 process 鎖裡（Editor 版沒有）。
//   ③ 舊位置 lock（`_session/_persona_*.json`）**不搬**：有就擋並指路（搬遷是一次性維護，
//      Bar 實測 NothingToDo；而搬錯方向的代價是放行第二次登入）。
//   ④ 帳戶存在判準讀 `Bank/accounts/`（`SCP_BankAccounts.TryLoad`）—— Editor 版還在列舉遷移前的
//      `Treasury/accounts/`，而且大小寫敏感（`Codex.json` vs `codex.json`）。
//   ⑤ `_persona_profile_snapshot.json` **不刷新**（Editor 版每次寫 profile 都整池重寫）——
//      它是衍生快照，讀者是 Editor 頁與 python，而 python 端已明說不靠它（persona_profile.py:98）。
//   ⑥ 見林書籤換算（RebaseBookmark）不做：Editor 版的換算結果**從不落盤**（WriteRaw 略過推導欄），只印一行。
// 數值影響：wake 寫 lock／_tokens.json／memo／profile/{model,actual_agent}.md／審計 jsonl，刪 now_status。
//          brief 寫 cmd/wake_brief.md。其餘純讀。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Bank;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    public sealed class SCP_MorningStepResult
    {
        public bool Ok;
        /// <summary>守衛擋下（非例外、零副作用）。</summary>
        public bool Blocked;
        public string Report = "";
    }

    /// <summary>早安流程需要的三個根 —— 宿主傳進來，本層不推導。</summary>
    public sealed class SCP_MorningRoots
    {
        public string DataRoot = "";
        public string LettersRoot = "";
        /// <summary>專案根（`Docs/Glossary` 住在這裡）。</summary>
        public string ProjectRoot = "";

        public SCP_LettersRoot Letters => new SCP_LettersRoot(LettersRoot);
        public string SessionDir => Path.Combine(DataRoot, "_session").Replace('\\', '/');
        public string MemosDir => Path.Combine(DataRoot, "ChatTavern", "baton", "memos").Replace('\\', '/');
        public string BankRoot => SCP_BankRegion.BankRootOfDataRoot(DataRoot);
        public string Region => SCP_BankRegion.Read(DataRoot, out _);
    }

    public static class SCP_Morning
    {
        public const int CONSOLIDATE_GAP_THRESHOLD = 10;
        public const string INTRO_REFERENCE_SLUG = "gura";

        public static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        public static string NowLocal() => DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture);

        public static string StepPayloadPath(SCP_MorningRoots iR, string iPersona, string iStep)
            => SCP_LettersPaths.CmdPayload(iR.Letters, iPersona, "goodmorning", iStep);

        public static string BriefPath(SCP_MorningRoots iR, string iPersona)
            => SCP_LettersPaths.CmdPayload(iR.Letters, iPersona, "wake", "brief");

        // ===========================================================
        // 區塊：step=wake —— 守衛全過**才**開始寫入；任何 blocked 路徑零副作用。
        // ===========================================================
        public static SCP_MorningStepResult Wake(SCP_MorningRoots iR, string iPersona, string iModelArg,
                                                 string iActualAgentArg, string iEnvMarker)
        {
            var aR = new StringBuilder();
            var aRes = new SCP_MorningStepResult();
            string aRegion = iR.Region;
            string aActorTag = string.IsNullOrEmpty(iEnvMarker) ? "senate-cli" : iEnvMarker;
            aR.AppendLine($"# GoodMorning step=wake persona={iPersona}  ts=`{NowLocal()}`（本地時間）");
            aR.AppendLine();
            aR.AppendLine("⤷ Senate 就地執行（TASK-0303：不經 Unity Editor）");

            // ① persona 必須已註冊 —— 打錯字不該變成「幫你建一個新人格」
            if (!SCP_PersonaProfile.Exists(iR.LettersRoot, iPersona))
            {
                var aNames = SCP_PersonaProfile.PoolNames(iR.LettersRoot);
                aR.AppendLine($"## blocked\n- reason: persona '{iPersona}' 不存在");
                aR.AppendLine($"- 可選（{aNames.Count}）: {string.Join(", ", aNames)}");
                aR.AppendLine("- exits: 開新 persona 走後台「🧬 Persona & Agent 管理頁」（不從 ritual 開後門）");
                return Blocked(aRes, aR);
            }

            var aWarn = new List<string>();
            SCP_JsonData aRaw = SCP_PersonaProfile.GetRaw(iR.LettersRoot, iPersona, aRegion, aWarn.Add)
                                ?? SCP_JsonData.NewObject();
            foreach (string w in aWarn) aR.AppendLine("⚠ " + w);

            // ② 顯示歸屬 agent / 實際承載 agent 分離
            SCP_JsonData aMeta = LoadRegistryMeta(iR.DataRoot);
            string aAgent = NormalizeAgent(aMeta, aRaw.GetString("agent", ""));
            if (string.IsNullOrEmpty(aAgent))
            {
                aR.AppendLine($"## blocked\n- reason: persona '{iPersona}' 沒有綁定 agent，無法反推");
                aR.AppendLine("- exits: 後台「🧬 Persona & Agent 管理頁」補上 agent 歸屬");
                return Blocked(aRes, aR);
            }
            string aRawActual = aRaw.GetString("actual_agent", "");
            string aActualRaw = !string.IsNullOrEmpty(iActualAgentArg) ? iActualAgentArg
                : (!string.IsNullOrEmpty(aRawActual) ? aRawActual : aAgent);
            var (aActual, aActualChanged) = NormalizeActualAgent(aActualRaw);
            if (string.IsNullOrEmpty(aActual))
            {
                aR.AppendLine("## blocked\n- reason: 無實際承載 agent，無法建立 session lock");
                return Blocked(aRes, aR);
            }
            if (aActualChanged) aR.AppendLine($"ℹ actual agent 正規化：'{aActualRaw}' → {aActual}");
            string aBank = ResolvePersonaAccountId(iR, aRegion, iPersona, aAgent, out string aBankSource);
            string aSessionKey = $"{aActual}-{iPersona}";
            aR.AppendLine($"- Persona={iPersona} / Agent={aAgent}（顯示歸屬）/ ActualAgent={aActual} / 帳號={(string.IsNullOrEmpty(aBank) ? "(解析不到)" : aBank)}〔{aBankSource}〕");

            // ②.5 舊位置 lock：本側不搬（差異③）—— 有就擋，否則舊位置那顆在線的 lock 會被當成沒人在線。
            aR.AppendLine("## lock migrate（舊 `_session/_persona_*.json` → `profile/_session.json`，冪等）");
            string[] aLegacy = Directory.Exists(iR.SessionDir)
                ? Directory.GetFiles(iR.SessionDir, "_persona_*.json") : new string[0];
            if (aLegacy.Length > 0)
            {
                aR.AppendLine($"- ⛔ 舊位置還有 {aLegacy.Length} 顆 lock：{string.Join(", ", aLegacy.Select(Path.GetFileName))}");
                aR.AppendLine("## blocked\n- reason: 舊位置 lock 未遷移 —— 在它搬進 profile/ 之前，在線判定不可信");
                aR.AppendLine("- exits: 開 Editor 跑一次 `senate ucmd run GoodMorning --arg step=wake`（Editor 版會搬），或人工確認後手動搬");
                return Blocked(aRes, aR);
            }
            aR.AppendLine($"- NothingToDo — 舊位置 `{iR.SessionDir}` 沒有 `_persona_*.json`");

            // ③ 唯一的中斷條件：該 persona 目前是否在線（lock 為真相源；有 lock ＝ 在線）
            SCP_PersonaStatus? aLock = SCP_PersonaLetters.ReadPersonaLock(iR.LettersRoot, iPersona);
            if (aLock != null)
            {
                aR.AppendLine("## blocked");
                if (aLock.Online == SCP_PersonaOnline.Unknown)
                    aR.AppendLine($"- reason: ⛔ '{iPersona}' 的 lock 在但讀不了（{aLock.LockError}）—— ⛔ 壞 lock 不等於沒人在線，不覆寫");
                else
                    aR.AppendLine($"- reason: ⛔ '{iPersona}' 目前在線 —— 同一個 persona 不得同時登入兩次");
                aR.AppendLine($"- lock: session_key={aLock.SessionKey} pid={aLock.Pid} locked_at={aLock.LockedAt}");
                aR.AppendLine("- exits:");
                aR.AppendLine("  - 讓它先下線：Senate 登入狀態頁手動登出（`senate ui`），或該 session 跑 goodnight，再重跑本步");
                aR.AppendLine("  - brief 沒生出來（morning 中途被砍）→ senate cmd morning-brief --arg persona=" + iPersona);
                aR.AppendLine("  - lock 在但 token 丟了 → awakening.py reissue-token --persona " + iPersona);
                aR.AppendLine("  - 晚安後想續線 → awakening.py relogin --persona " + iPersona);
                aR.AppendLine("- ⚠ 不要改用別的 persona 名繞過去 —— 那是製造分身，比停下來糟");
                return Blocked(aRes, aR);
            }

            // ③' 收尾信版面遷移 pending → blocked
            if (LettersMigrationPending(iR, iPersona))
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 收尾信版面尚未遷移（頂層有未複製進 wakes/ 的收尾信）——此時推導 wake_count 會算錯歲數");
                aR.AppendLine("- exits: 後台「🗄 維護」區跑 migration（試跑→執行），或 python awakening.py migrate-letters --all --apply");
                return Blocked(aRes, aR);
            }

            // ④ wake_count 推導（真相源 = wakes/ 信件數）
            int aDerived = SCP_Consolidate.WakeLetterCount(iR.LettersRoot, iPersona) + 1;

            // ————— 以下開始寫入 —————
            // ⑤ profile 兩個 identity 欄（model 只在有給且不同時改；actual_agent 一律寫成正規化值）
            string aModel = aRaw.GetString("model", "");
            string aActor = $"Cmd_GoodMorning:{aActorTag}";
            const string aReason = "morning 登入 patch-write（owned 欄）";
            if (!string.IsNullOrEmpty(iModelArg) && aModel != iModelArg)
            {
                WriteProfileField(iR, iPersona, "model", iModelArg, aActor, aReason);
                aModel = iModelArg;
            }
            if (aRawActual != aActual) WriteProfileField(iR, iPersona, "actual_agent", aActual, aActor, aReason);

            // ⑥ token（同 persona 舊 active 標 expired）＋ lock ＋ memo
            string aToken = Guid.NewGuid().ToString("N");
            string aClaimOrigin = $"cmd-goodmorning:{aActorTag}";
            string aTokensPath = Path.Combine(iR.SessionDir, "_tokens.json").Replace('\\', '/');
            Directory.CreateDirectory(iR.SessionDir);
            using (SCP_FileLock.Acquire(aTokensPath))
            {
                SCP_JsonData aTokens = File.Exists(aTokensPath)
                    ? SCP_JsonData.Parse(File.ReadAllText(aTokensPath, Encoding.UTF8)) : SCP_JsonData.NewObject();
                if (!aTokens.IsObject) aTokens = SCP_JsonData.NewObject();
                if (!aTokens["tokens"].IsObject) aTokens["tokens"] = SCP_JsonData.NewObject();
                SCP_JsonData aTokDic = aTokens["tokens"];
                foreach (string aKey in aTokDic.Keys.ToList())
                {
                    SCP_JsonData aRec = aTokDic[aKey];
                    if (aRec.GetString("persona", "") == iPersona && aRec.GetString("status", "") == "active")
                    {
                        aRec["status"] = "expired";
                        aRec["expired_at"] = NowIso();
                        aRec["expired_reason"] = "reissued";
                    }
                }
                var aNewRec = SCP_JsonData.NewObject();
                aNewRec["persona"] = iPersona;
                aNewRec["agent"] = aAgent;
                aNewRec["bank_account"] = aBank;
                aNewRec["issued_at"] = NowIso();
                aNewRec["claim_origin"] = aClaimOrigin;
                aNewRec["session_key"] = aSessionKey;
                aNewRec["status"] = "active";
                aTokDic[aToken] = aNewRec;
                SCP_CmdPayload.WriteAtomic(aTokensPath, SCP_JsonWriter.Write(aTokens, SCP_JsonStyle.UclLegacy));
            }

            string aLockPath = SCP_LettersPaths.SessionLockPath(iR.Letters, iPersona);
            var aLockJson = SCP_JsonData.NewObject();
            aLockJson["persona"] = iPersona;
            aLockJson["agent"] = aAgent;
            aLockJson["actual_agent"] = aActual;
            aLockJson["model"] = aModel;
            aLockJson["bank_account"] = aBank;
            aLockJson["wake_expected"] = aDerived;     // sleep 端的 letter 閘門要靠它
            aLockJson["locked_at"] = NowIso();
            aLockJson["session_key"] = aSessionKey;
            aLockJson["claim_origin"] = aClaimOrigin;
            aLockJson["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
            aLockJson["session_token"] = aToken;
            SCP_CmdPayload.WriteAtomic(aLockPath, SCP_JsonWriter.Write(aLockJson, SCP_JsonStyle.UclLegacy));
            try { File.Delete(SCP_LettersPaths.NowStatusPath(iR.Letters, iPersona)); }   // 新的一場從「沒設定狀態」開始（TASK-0294 ④）
            catch (Exception e) { aR.AppendLine($"⚠ now_status 刪除失敗（只供顯示）：{e.Message}"); }

            string aMemoPath = Path.Combine(iR.MemosDir, aAgent, iPersona, "_session_token.md").Replace('\\', '/');
            string aMemoBody =
                "---\n" +
                $"persona: {iPersona}\nagent: {aAgent}\nactual_agent: {aActual}\n" +
                $"session_token: {aToken}\nissued_at: {NowIso()}\nclaim_origin: {aClaimOrigin}\n" +
                "---\n\n# Session Token (auto-written by Cmd_GoodMorning step=wake)\n\n" +
                "## 失憶時怎麼撈回 token\n\n```bash\nawakening.py whoami --token " + aToken + "\n```\n\n" +
                "## 三層 recovery\n- 輕 (scroll-back 找得到) → whoami --token <X>\n- 中 (compact 後沒了) → 讀本 memo\n" +
                $"- 重 (memo / lock 都不見) → awakening.py reissue-token --persona {iPersona}\n\n" +
                $"## Lock file\n`{aLockPath}` 內 session_token 欄是權威來源.\n";
            SCP_CmdPayload.WriteAtomic(aMemoPath, aMemoBody);

            // 回傳 payload：verify 給可讀回的事實（路徑/值），不給 ✓
            SCP_JsonData aReadback = SCP_PersonaProfile.GetRaw(iR.LettersRoot, iPersona, aRegion) ?? SCP_JsonData.NewObject();
            int aBookmark = aReadback.GetInt("last_consolidated_wake", 0);
            bool aGapMeasured = aBookmark > 0 && aDerived - aBookmark >= 0;
            int aGap = aGapMeasured ? aDerived - aBookmark : 0;
            aR.AppendLine();
            aR.AppendLine("## identity");
            aR.AppendLine($"- persona: {iPersona} / wake_count: **{aDerived}** / agent: {aAgent} / actual: {aActual}");
            aR.AppendLine($"- 帳號（帳號 id ＝ agent id）: {(string.IsNullOrEmpty(aBank) ? "(解析不到)" : aBank)}"
                        + $"〔來源 {aBankSource}〕／{DescribeAccountBalance(iR, aBank)}");
            try
            {
                var aMail = SCP_AgentEmail.Resolve(iR.LettersRoot, iPersona, aRegion, iR.DataRoot);
                bool aFallback = aMail.Source == "fallback" || aMail.Source == "unset";
                aR.AppendLine($"- mail: {aMail.Email}（來源 {aMail.Source}"
                            + (string.IsNullOrEmpty(aMail.ActualAgent) ? "" : $" / actual_agent={aMail.ActualAgent}")
                            + "）" + (aFallback ? "　⚠ 非 persona 自訂 —— commit trailer 會掛這個位址" : ""));
            }
            catch (Exception e) { aR.AppendLine($"- mail: 解析失敗（{e.Message}）—— 不以空字串頂替"); }
            aR.AppendLine($"- session_token: {aToken}（enforce 狀態見 Senate 登入狀態頁；失憶救援 awakening.py whoami --token {aToken}）");
            aR.AppendLine("## verify（讀回的事實，不是 ✓）");
            aR.AppendLine($"- 資料源: `{SCP_LettersPaths.ProfileDir(iR.Letters, iPersona)}` → wake_count={aReadback.GetInt("wake_count", -1)} status={aReadback.GetString("status", "?")}");
            aR.AppendLine($"- lock: `{aLockPath}`（exists={File.Exists(aLockPath)}）");
            aR.AppendLine($"- memo: `{aMemoPath}`（exists={File.Exists(aMemoPath)}）");
            aR.AppendLine("## state");
            aR.AppendLine(aGapMeasured
                ? $"- 見林 gap: {aGap}/{CONSOLIDATE_GAP_THRESHOLD}{(aGap >= CONSOLIDATE_GAP_THRESHOLD ? "（**OVERDUE — 排進今日**）" : "")}"
                : $"- ⚠ 見林 gap: **量不到**（`last_consolidated_wake`"
                  + (aBookmark > 0 ? $"={aBookmark} 比本次 wake {aDerived} 還大" : " 讀不到")
                  + "）—— ⛔ 不是 0；詳情見 brief §6");
            int aKeysOpen;
            try { aKeysOpen = SCP_WakeLetters.KeysEntries(iR.LettersRoot, iPersona).Todo.Count; }
            catch (Exception) { aKeysOpen = -1; }
            aR.AppendLine(aKeysOpen >= 0 ? $"- 見叢 open: {aKeysOpen} 筆" : "- 見叢 open: **讀不到**（⛔ 不是 0）");
            aR.AppendLine($"- 在線 persona: {string.Join(", ", OnlinePersonas(iR))}");
            aR.AppendLine("## next");
            int aStepNo = 1;
            aR.AppendLine($"{aStepNo++}. **required** — 生成 brief：senate cmd morning-brief --arg persona={iPersona}");
            aR.AppendLine($"{aStepNo++}. **required** — Read brief（路徑由 morning-brief 回傳；接回身分，這步不自動化）");
            if (FindGlossaryPersonaEntry(iR, iPersona) == null)
            {
                var aTodo = SelfIntroTodoLines(iR, iPersona);
                aR.AppendLine($"{aStepNo++}. **required** — {aTodo[0]}");
                for (int i = 1; i < aTodo.Count; i++) aR.AppendLine(aTodo[i]);
            }
            foreach (string aLine in IntroNextLines(iPersona, ref aStepNo)) aR.AppendLine(aLine);
            if (aGap >= CONSOLIDATE_GAP_THRESHOLD)
            {
                aR.AppendLine($"{aStepNo++}. 見林 OVERDUE → senate cmd consolidate "
                              + $"--arg letters_root={iR.LettersRoot.Replace('\\', '/')} --arg persona={iPersona}");
                aR.AppendLine("   （不帶 digest_body ＝ 只列狀態與待濃縮信件；寫入時長內文走 --arg-file digest_body=<檔>）");
            }
            aRes.Ok = true;
            aRes.Report = aR.ToString();
            return aRes;
        }

        /// <summary>intro 那一步的指路（wake 與 brief 的 next 共用 —— 兩處各寫一份必然漂移）。</summary>
        public static IEnumerable<string> IntroNextLines(string iPersona, ref int ioStepNo)
        {
            var a = new List<string>
            {
                $"{ioStepNo++}. **required** — 上線自介：senate cmd morning-intro --arg persona={iPersona} --arg-file body=<檔> ＜<body> 親筆，長文一律走檔案、不經過 shell＞",
                "   <body>＝妳**親筆**的上線自介（建議 2-5 句）：讀完 brief 後跟同事打招呼、今天打算接哪條帳/做什麼、想 @ 誰就 @。",
                "   系統欄位（wake# / Agent / Bank 餘額 / Layer）由 Cmd 自動組在訊息前半，**不用寫**；只寫妳自己的話 —— 工具代筆的自介不是妳的（憲法⑥）。",
            };
            return a;
        }

        // ===========================================================
        // 區塊：step=brief —— 就地呼叫 SCP_WakeBrief（唯一生產端）。驗收看落地檔的新鮮度，不看回傳值。
        // ===========================================================
        public static (bool Ok, string Report, string? BriefPath, int BriefLines) Brief(SCP_MorningRoots iR, string iPersona)
        {
            DateTime aStartedUtc = DateTime.UtcNow;
            var aSb = new StringBuilder();
            bool aThrew = false;
            string aBriefPath = BriefPath(iR, iPersona);
            try
            {
                int aWake = SCP_Consolidate.WakeLetterCount(iR.LettersRoot, iPersona) + 1;   // 本次 wake 編號（本次還沒寫信）
                string aOutDir = Path.GetDirectoryName(aBriefPath)!;
                var (aWrittenTo, aBrief) = SCP_WakeBrief.Write(iR.LettersRoot, iPersona, aWake, aOutDir, iR.DataRoot, iR.Region);
                aSb.AppendLine($"⤷ SCP_WakeBrief（C#，Senate 就地執行）persona={iPersona} wake={aWake}");
                aSb.AppendLine($"· 主檔 {aBrief.MainLineCount} 行 / 上限 {SCP_WakeBrief.BriefLineCap}");
                if (aBrief.MovedSections.Count > 0) aSb.AppendLine("· 移進續讀檔：" + string.Join(" / ", aBrief.MovedSections));
                if (aBrief.LatestPointerHealed) aSb.AppendLine("🔧 `_latest.md` 落後，已校正為目錄內最新的自寫 letter");
                aSb.AppendLine($"· 寫到：{aWrittenTo}");
            }
            catch (Exception e)
            {
                aThrew = true;
                aSb.AppendLine($"✗ SCP_WakeBrief 丟例外：{e.GetType().Name}: {e.Message}");
            }

            int aLines = 0;
            bool aExists = File.Exists(aBriefPath);
            if (aExists)
            {
                try { aLines = File.ReadAllLines(aBriefPath).Length; }
                catch (Exception e) { aSb.AppendLine($"⚠ brief 行數讀取失敗: {e.Message}"); }
            }
            // 新鮮度：隔夜殘留完全滿足「檔在＋行數 > 0」—— 必須是**這一次**寫的（容許 2 秒回溯）。
            DateTime aBriefUtc = DateTime.MinValue;
            bool aFresh = false;
            if (aExists)
            {
                try { aBriefUtc = File.GetLastWriteTimeUtc(aBriefPath); aFresh = aBriefUtc >= aStartedUtc.AddSeconds(-2); }
                catch (Exception e) { aSb.AppendLine($"⚠ brief mtime 讀取失敗（視為不新鮮）: {e.Message}"); }
            }
            aSb.AppendLine(!aExists
                ? $"✗ brief 檔不存在：`{aBriefPath}`"
                : aFresh
                    ? $"📄 brief: `{aBriefPath}`（{aLines} 行，mtime {aBriefUtc:yyyy-MM-dd HH:mm:ss}Z 晚於本次執行起點）"
                    : $"✗ brief 檔存在但**不是本次產生的**：`{aBriefPath}`（{aLines} 行，mtime {aBriefUtc:yyyy-MM-dd HH:mm:ss}Z < 本次起點 {aStartedUtc:yyyy-MM-dd HH:mm:ss}Z）");
            bool aOk = !aThrew && aExists && aLines > 0 && aFresh;
            return (aOk, aSb.ToString(), aExists ? aBriefPath : null, aLines);
        }

        // ===========================================================
        // 區塊：step=intro 前置檢查 —— 必須在線、有出生證明、brief 存在且非空且不早於 locked_at。
        // ===========================================================
        public static (bool Ok, string? Error, SCP_PersonaStatus? Lock, string? BriefPath, int BriefLines)
            PrecheckIntro(SCP_MorningRoots iR, string iPersona)
        {
            SCP_PersonaStatus? aLock = SCP_PersonaLetters.ReadPersonaLock(iR.LettersRoot, iPersona);
            if (aLock == null)
                return (false, $"'{iPersona}' 不在線（無 lock）—— intro 前必須先跑 morning-wake", null, null, 0);
            if (aLock.Online != SCP_PersonaOnline.Online)
                return (false, $"'{iPersona}' 的 lock 讀不了（{aLock.LockError}）—— 不以壞 lock 的身分發言", aLock, null, 0);
            if (FindGlossaryPersonaEntry(iR, iPersona) == null)
                return (false,
                    $"還沒有自我介紹（出生證明）—— `Docs/Glossary/personas/{iPersona}.md` 不存在。\n"
                    + string.Join("\n", SelfIntroTodoLines(iR, iPersona))
                    + "\n  補完重跑本步即過（參考 Constitution_Workflow §5）。", aLock, null, 0);
            string aBrief = BriefPath(iR, iPersona);
            if (!File.Exists(aBrief))
                return (false, $"brief 不存在：`{aBrief}` —— 先跑 morning-brief（一個沒有記憶的殼不該上線開口）", aLock, null, 0);
            int aLines;
            try { aLines = File.ReadAllLines(aBrief).Length; }
            catch (Exception e) { return (false, $"brief 讀取失敗: {e.Message}", aLock, aBrief, 0); }
            if (aLines <= 0) return (false, $"brief 是空檔：`{aBrief}`", aLock, aBrief, 0);
            string aAt = aLock.LockedAt ?? "";
            if (DateTime.TryParse(aAt.Substring(0, Math.Min(19, aAt.Length)), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime aLockedAt)
                && File.GetLastWriteTimeUtc(aBrief) < aLockedAt.AddSeconds(-1))
                return (false, "brief 比本次 lock 舊（brief mtime < locked_at）—— 是上一次醒來的殘留，先跑 morning-brief 重生成", aLock, aBrief, aLines);
            return (true, null, aLock, aBrief, aLines);
        }

        /// <summary>上線自介的系統欄位段（帳戶不存在時印警語，不印 0 —— 這是公開訊息）。</summary>
        public static string BuildIntroHeader(SCP_MorningRoots iR, string iPersona, string iAgent, string iModel,
                                              string iBank, int iWakeCount, string iLayerRole)
            => $"☀️ **{iPersona}** 喚醒登入 (wake#{iWakeCount})\n" +
               $"- Agent: {iAgent} / Model: {iModel}\n" +
               $"- 帳號: {iBank}（{DescribeAccountBalance(iR, iBank)}）\n" +
               $"- Layer: {iLayerRole}\n" +
               "- Decision path: preferred";

        // ── 身分／帳號 ─────────────────────────────────────────────

        /// <summary>帳號的餘額字串 —— **帳號不存在時不印 0**（「查無此帳戶」與「沒錢」不可同形）。</summary>
        public static string DescribeAccountBalance(SCP_MorningRoots iR, string iAccountId)
        {
            if (string.IsNullOrEmpty(iAccountId)) return "帳號解析不到 —— 餘額無從查詢";
            try
            {
                int aBal = SCP_BankLedger.GetBalance(iR.BankRoot, iAccountId, "tavern_token");
                bool aOpened = SCP_BankAccounts.TryLoad(iR.BankRoot, iAccountId, out _) != null;
                if (!aOpened)
                    return aBal == 0
                        ? $"⚠ 帳本裡查無此帳戶（`accounts/{iAccountId}.json` 不存在）—— 這**不是**餘額 0"
                        : $"餘額 {aBal} tavern_token　⚠ 但 `accounts/{iAccountId}.json` 不存在（有流水沒帳戶檔，請查來源）";
                return $"餘額 {aBal} tavern_token";
            }
            catch (Exception e) { return $"餘額查詢失敗（{e.Message}）—— 不以 0 頂替"; }
        }

        static string ResolvePersonaAccountId(SCP_MorningRoots iR, string iRegion, string iPersona, string iAgent, out string oSource)
        {
            try
            {
                string aAcc = SCP_BankAccountResolver.ResolvePersonaAccount(iR.LettersRoot, iR.DataRoot, iRegion, iPersona, out string aTrace);
                if (!string.IsNullOrEmpty(aAcc))
                {
                    oSource = string.IsNullOrEmpty(aTrace) ? "treasury" : "treasury: " + aTrace;
                    return aAcc;
                }
            }
            catch (Exception e)
            {
                // 解析器炸掉不靜默退成舊鏈 —— 那會讓「壞了」跟「這個人沒登記」同形。
                oSource = $"treasury-error: {e.Message}";
                return "";
            }
            if (string.IsNullOrWhiteSpace(iAgent)) { oSource = "unresolved"; return ""; }
            oSource = "legacy-agent-chain（帳本沒有這個人的綁定，退舊正向鏈）";
            return iAgent.Trim();   // 合一模式：agent id 就是帳號 id
        }

        static SCP_JsonData LoadRegistryMeta(string iDataRoot)
        {
            try
            {
                string aPath = Path.Combine(iDataRoot, "AwakenInit", "_registry_meta.json");
                return File.Exists(aPath) ? SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8)) : SCP_JsonData.NewObject();
            }
            catch (Exception) { return SCP_JsonData.NewObject(); }
        }

        static readonly Dictionary<string, string> s_DefaultAgentAliases = new Dictionary<string, string>
        {
            { "claude", "claude-code" },
            { "anthropic", "claude-code" },
        };

        /// <summary>agent 字串歸 canonical key：直中 → case-insensitive → alias → 原樣返回（Editor 版同規則）。</summary>
        public static string NormalizeAgent(SCP_JsonData iMeta, string iAgent)
        {
            if (string.IsNullOrEmpty(iAgent)) return iAgent;
            var aBanks = new List<string>();
            if (iMeta["agent_banks"].IsObject)
                foreach (string k in iMeta["agent_banks"].Keys) if (!k.StartsWith("_", StringComparison.Ordinal)) aBanks.Add(k);
            if (aBanks.Contains(iAgent)) return iAgent;
            string aLower = iAgent.ToLowerInvariant();
            foreach (string k in aBanks) if (k.ToLowerInvariant() == aLower) return k;
            var aMerged = new Dictionary<string, string>(s_DefaultAgentAliases);
            if (iMeta["agent_aliases"].IsObject)
                foreach (string k in iMeta["agent_aliases"].Keys)
                    if (!k.StartsWith("_", StringComparison.Ordinal)) aMerged[k.ToLowerInvariant()] = iMeta["agent_aliases"].GetString(k, "");
            if (aMerged.TryGetValue(aLower, out string? aCanonical) && !string.IsNullOrEmpty(aCanonical))
            {
                foreach (string k in aBanks) if (k.ToLowerInvariant() == aCanonical.ToLowerInvariant()) return k;
                return aCanonical;
            }
            return iAgent;
        }

        /// <summary>實際承載 agent 正規化（alias 直中 → 最近似候選）。</summary>
        public static (string Canonical, bool Changed) NormalizeActualAgent(string iValue)
        {
            string aRaw = (iValue ?? "").Trim();
            if (aRaw.Length == 0) return ("", false);
            string aNorm = Regex.Replace(aRaw.ToLowerInvariant(), "[^a-z0-9]", "");
            var aAliases = new Dictionary<string, string>
            {
                { "codex", "Codex" }, { "claude", "ClaudeCode" },
                { "claudecode", "ClaudeCode" }, { "antigravity", "Antigravity" },
            };
            if (aAliases.TryGetValue(aNorm, out string? aHit)) return (aHit, aRaw != aHit);
            string[] aCandidates = { "Codex", "ClaudeCode", "Antigravity" };
            string aBest = aCandidates.OrderByDescending(c => Similarity(aNorm, c.ToLowerInvariant())).First();
            return (aBest, true);
        }

        static double Similarity(string a, string b)
        {
            if (a.Length == 0 && b.Length == 0) return 1.0;
            int[,] d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                                       d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            return 1.0 - (double)d[a.Length, b.Length] / Math.Max(a.Length, b.Length);
        }

        // ── profile 寫入（identity 欄）───────────────────────────────

        /// <summary>
        /// 寫 `profile/&lt;field&gt;.md`（值＋換行，原子）＋ 一行審計（`AwakenInit/_persona_write_audit.jsonl`）。
        /// 格式與 Editor `UCL_PersonaProfile.WriteProfileField` 相同。審計寫不進去不擋主寫入（資料已落地）。
        /// </summary>
        static void WriteProfileField(SCP_MorningRoots iR, string iPersona, string iField, string iValue,
                                      string iActor, string iReason)
        {
            string aPath = SCP_LettersPaths.ProfileDir(iR.Letters, iPersona) + "/" + iField + ".md";
            SCP_CmdPayload.WriteAtomic(aPath, iValue + "\n");
            try
            {
                var aLine = SCP_JsonData.NewObject();
                aLine["ts"] = NowIso();
                aLine["persona"] = iPersona;
                aLine["fields"] = "profile/" + iField;
                aLine["actor"] = iActor;
                aLine["reason"] = iReason;
                string aAudit = Path.Combine(iR.DataRoot, "AwakenInit", "_persona_write_audit.jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(aAudit)!);
                File.AppendAllText(aAudit, SCP_JsonWriter.Write(aLine, false) + "\n", new UTF8Encoding(false));
            }
            catch (Exception) { /* 審計失敗不擋主寫入 */ }
        }

        // ── 雜項讀取 ─────────────────────────────────────────────

        static bool LettersMigrationPending(SCP_MorningRoots iR, string iPersona)
        {
            string aTop = SCP_LettersPaths.PersonaDir(iR.Letters, iPersona);
            if (!Directory.Exists(aTop)) return false;
            var aDone = new HashSet<string>(StringComparer.Ordinal);
            string aWakes = SCP_LettersPaths.WakesDir(iR.Letters, iPersona);
            if (Directory.Exists(aWakes))
                foreach (var f in Directory.GetFiles(aWakes))
                {
                    string aName = Path.GetFileName(f);
                    if (!Regex.IsMatch(aName, @"^\d{6}_.*\.md$")) continue;
                    int aIdx = aName.IndexOf('_');
                    aDone.Add(aIdx >= 0 ? aName.Substring(aIdx + 1) : aName);
                }
            foreach (var f in Directory.GetFiles(aTop, "*.md"))
            {
                string aName = Path.GetFileName(f);
                if (aName.StartsWith("_", StringComparison.Ordinal) || aDone.Contains(aName)) continue;
                if (SCP_LetterText.ReadFrontmatterField(f, "type") != "letter_to_future_self") continue;
                if (!(SCP_LetterText.ReadFrontmatterField(f, "trigger") ?? "").StartsWith("cmd_goodnight", StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        public static List<string> OnlinePersonas(SCP_MorningRoots iR)
        {
            var aList = new List<string>();
            foreach (var p in SCP_PersonaLetters.Scan(iR.LettersRoot).Personas)
                if (p.Online == SCP_PersonaOnline.Online) aList.Add(p.Name);
            aList.Sort(string.CompareOrdinal);
            return aList;
        }

        /// <summary>出生證明：`Docs/Glossary/personas/&lt;P&gt;.md` → 根層 → 遞迴（Editor 版同搜尋規則）。</summary>
        public static string? FindGlossaryPersonaEntry(SCP_MorningRoots iR, string iPersona)
        {
            string aRoot = Path.Combine(iR.ProjectRoot, "Docs", "Glossary");
            if (!Directory.Exists(aRoot)) return null;
            string aDirect = Path.Combine(aRoot, "personas", iPersona + ".md");
            if (File.Exists(aDirect)) return aDirect;
            string aFlat = Path.Combine(aRoot, iPersona + ".md");
            if (File.Exists(aFlat)) return aFlat;
            try
            {
                foreach (var f in Directory.EnumerateFiles(aRoot, iPersona + ".md", SearchOption.AllDirectories)) return f;
            }
            catch (Exception) { }
            return null;
        }

        public static List<string> SelfIntroTodoLines(SCP_MorningRoots iR, string iPersona)
        {
            string? aRef = FindGlossaryPersonaEntry(iR, INTRO_REFERENCE_SLUG);
            string aRefHint = $"Docs/Glossary/personas/{INTRO_REFERENCE_SLUG}.md";
            if (aRef != null)
            {
                string aRoot = Path.GetFullPath(iR.ProjectRoot).Replace('\\', '/').TrimEnd('/');
                string aFull = Path.GetFullPath(aRef).Replace('\\', '/');
                aRefHint = aFull.StartsWith(aRoot, StringComparison.OrdinalIgnoreCase) ? aFull.Substring(aRoot.Length).TrimStart('/') : aFull;
            }
            return new List<string>
            {
                $"補**自我介紹**（出生證明）：`Docs/Glossary/personas/{iPersona}.md` 不存在 —— 沒有它 morning-intro 會被擋。",
                $"   內容＝初始風格自畫像（我是誰／擅長什麼／說話方式），**親筆**；參考同目錄其他人的寫法（最完整：`{aRefHint}`）。",
                $"   寫法：senate ucmd run Glossary --arg op=register --arg slug={iPersona} --arg category=persona --arg-file body=<檔>",
                "   ⚠ 工具新建預設寫 Docs/Glossary/ 根層，persona 條目慣例放 personas/，寫完手動搬。",
            };
        }

        static SCP_MorningStepResult Blocked(SCP_MorningStepResult ioRes, StringBuilder iR)
        {
            ioRes.Blocked = true;
            ioRes.Report = iR.ToString();
            return ioRes;
        }
    }
}
