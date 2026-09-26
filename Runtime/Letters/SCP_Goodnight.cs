// 區塊職責：晚安流程（check／portrait／letter／sleep／logout）的**邏輯層** —— 不需要 Unity Editor。
// 物理意義：移植自 UCL_Core `UCL_AwakeningService`（StepCheck／StepPortrait／StepLetter／WriteWakeLetter／
//          PrepareSleep／ExpireTokens）（TASK-0305，承接 TASK-0303 早安）。寫的檔、欄位、回傳檔文字逐一對齊 Editor 版。
//          Senate 的 `senate cmd goodnight-*` 與 Editor 的 `senate ucmd run GoodNight` 呼叫**同一份**。
// sleep 的形狀：Preflight（唯讀，全部守衛）→ Apply（刪 lock／now_status、組廣播）→ 呼叫端自己決定
//          關場方式（Senate：CloseOwnSessionNative；Editor：UCL_SessionCloseFlow 帶結算）→ 廣播 → ExpireTokens。
//          ⇒ 任何 blocked 都發生在第一個寫入之前（半睡半醒的狀態不存在）。
// 與 Editor 版刻意的差異（寫在這裡讓人查得到，不是漏移植）：
//   ① 收尾信寫入加**防覆寫**（目標檔已在就擋）＋ 跨 process 鎖 —— Editor 版沒有；編號算錯時它會靜默蓋掉舊信。
//   ② 信落地後**不再** `WriteRaw(wake_count)`：那一步在 Editor 版本來就是 no-op（wake_count 是推導欄，
//      WriteRaw 略過），實際做的只是把所有身分欄原樣重寫一遍＋刷快照，而它丟例外時信已經落地 ⇒ 指令失敗但信在。
//   ③ sleep 的 `WriteRaw(status=offline)` 同理不做：status 由 lock 在不在推導，刪 lock 就是下線。
//   ④ lock 讀得到檔卻解析不了（壞檔）：sleep **擋**；logout **刪掉並明說**。Editor 版把壞 lock 當成沒 lock，
//      於是永遠刪不掉 —— 那個 persona 會一直顯示在線。
//   ⑤ 需要 Editor 的兩段（本人有進行中的觀影場要結算／收工閘帶 skip_reason 要寫進單子）由 Preflight 回報。
//      Tim 2026-09-26 拍板：**Editor 沒開就跳過那一段，不得卡住晚安**。呼叫端 Editor 活著就整步交給 Editor；
//      沒開就照走、觀影場留著不關（到期成殘留，殘留結算會補付）、skip 理由改印進回傳檔與下線廣播。
//   ⑥ portrait 的 `about` 必須是現有 persona（見 SCP_PortraitWriter）。
// 數值影響：letter 寫 wakes/<N>_<ts>.md＋_latest.md；portrait 寫兩幅畫像檔；sleep 刪 lock／now_status；
//          ExpireTokens 改 _tokens.json；CloseOwnSessionNative 改 session 檔。check 純讀。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Session;
using SCP.Core.Tasks;
using SCP.Core.Tavern;

namespace SCP.Core.Letters
{
    /// <summary>sleep／logout 的唯讀預檢結果。</summary>
    public sealed class SCP_GoodnightPreflight
    {
        public bool Blocked;
        /// <summary>非空＝這一步有一段要 Editor 才做得完（原因）。
        /// 呼叫端的處置（Tim 2026-09-26）：Editor 活著 ⇒ 整步交給 Editor；Editor 沒開 ⇒ 本層照走，**只跳過那一段並大聲說**。</summary>
        public string NeedsEditor = "";
        /// <summary>收工閘帶了 skip_reason、而有單要寫理由（寫入端只有 Editor）。</summary>
        public bool NeedsTaskSkipWrite;
        /// <summary>本人有進行中的觀影場（結算只有 Editor）。</summary>
        public string ActiveStreamWatchId = "";
        public string Report = "";
        /// <summary>收工閘會擋的單（sleep 才算）。</summary>
        public List<SCP_TaskEntry> PendingWrapups = new List<SCP_TaskEntry>();
    }

    /// <summary>sleep／logout 寫入半套的結果。</summary>
    public sealed class SCP_GoodnightSleep
    {
        public string Report = "";
        /// <summary>下線廣播本文；`{SUMMARY}` 待呼叫端填。</summary>
        public string BroadcastBody = "";
        /// <summary>刪 lock 前讀到的 session_token（廣播要帶；null＝沒有 lock）。</summary>
        public string? Token;
    }

    public static class SCP_Goodnight
    {
        const int CHECK_TAVERN_PEEK_COUNT = 10;

        public static string StepPayloadPath(SCP_MorningRoots iR, string iPersona, string iStep)
            => SCP_LettersPaths.CmdPayload(iR.Letters, iPersona, "goodnight", iStep);

        static string Header(string iStep, string iPersona)
            => $"# GoodNight step={iStep} persona={iPersona}  ts=`{SCP_Morning.NowLocal()}`（本地時間）\n";

        static SCP_MorningStepResult Blocked(StringBuilder iR)
            => new SCP_MorningStepResult { Blocked = true, Report = iR.ToString() };

        static SCP_MorningStepResult Ok(StringBuilder iR)
            => new SCP_MorningStepResult { Ok = true, Report = iR.ToString() };

        // ===========================================================
        // 區塊：step=check —— 唯讀起手（酒館最後一眼：讀檔不動 cursor）
        // ===========================================================
        public static SCP_MorningStepResult Check(SCP_MorningRoots iR, string iPersona)
        {
            var aR = new StringBuilder();
            aR.AppendLine(Header("check", iPersona));
            if (!SCP_PersonaProfile.Exists(iR.LettersRoot, iPersona))
            {
                aR.AppendLine($"## blocked\n- reason: persona '{iPersona}' 不在 registry —— 要下線誰不能用猜的");
                return Blocked(aR);
            }
            var aLock = SCP_PersonaLetters.ReadPersonaLock(iR.LettersRoot, iPersona);
            aR.AppendLine(aLock == null
                ? "- lock: 無 —— cleanup 場景（上次下線沒走完）；本流程照走，lock 步驟會自動跳過"
                : aLock.Online == SCP_PersonaOnline.Online
                    ? $"- lock: 🔒 {aLock.SessionKey}（locked_at={aLock.LockedAt}）"
                    : $"- lock: ⚠ 檔在但讀不了（{aLock.LockError}）—— sleep 會擋；要清掉它走 goodnight-logout");
            aR.AppendLine();
            aR.AppendLine($"## 🍺 酒館最後一眼（最近 {CHECK_TAVERN_PEEK_COUNT} 筆，peek 不動 cursor）");
            try
            {
                foreach (var m in SCP_TavernRead.Tail(iR.DataRoot, "tavern", CHECK_TAVERN_PEEK_COUNT))
                {
                    string aTag = m.Meta.TryGetValue("tag", out string? t) ? $" «{t}»" : "";
                    string aBody = (m.Body ?? "").Replace("\r", "").Replace("\n", " ⏎ ");
                    if (aBody.Length > 160) aBody = aBody.Substring(0, 160) + "…";
                    aR.AppendLine($"- [{m.Ts}] {DisplayName(m)}{aTag}: {aBody}");
                }
            }
            catch (Exception e)
            {
                aR.AppendLine($"⚠ 酒館 peek 失敗（{e.Message}）—— **這不代表酒館沒事**；流程照走。");
            }
            aR.AppendLine();
            aR.Append(SCP_TaskReconcileReport.BuildReport(new SCP_DataRoot(iR.DataRoot), iPersona,
                SCP_LettersPaths.KeysOpenPath(iR.Letters, iPersona)));
            aR.AppendLine("## next（人工收尾清單 —— 標 **required** 的會實擋；其餘提示型）");
            aR.AppendLine("⚠ 本清單**之外**還有一道實擋：**收工閘**（擋在 `goodnight-sleep`）——"
                + "它現在會擋什麼，上面 Task 對帳 ⑤ 已經列出來了。");
            aR.AppendLine($"1. 見叢交棒：senate cmd keys --arg persona={iPersona} --arg add=\"<明天必須知道的一句話>\"");
            aR.AppendLine("   ⛔ **commit／push／submodule bump 不要寫進見叢** —— 晚安後 Tim 自己收尾全部 commit；");
            aR.AppendLine("      寫進來只會讓明天的自己把「已經做完的事」排成第一件。改動本身值得交棒 → 寫那個改動要驗什麼，不寫它要 commit。");
            aR.AppendLine("2. 關係補記：今天漏記的互動補一筆（依 ucl-relationship；主要觸發點是對話當下就寫，這裡只是撿漏）");
            aR.AppendLine("3. 工作記憶回寫：今天有工作相關內容、knowhow、決策或踩坑，一律依 ucl-work-memory 保存（工作細節不進晚安信，Tim 2026-09-08 拍板）");
            aR.AppendLine($"4. **required** — 見人畫像（獨立步驟，會擋 letter）：senate cmd goodnight-portrait --arg persona={iPersona} --arg about=<同事> --arg headline=<標題> --arg-file body=<檔>");
            aR.AppendLine("   今晚真的沒有人可畫 → 同一步驟帶 --arg skip_reason=<理由>（理由會印進下線廣播）。");
            aR.AppendLine("5. （可選）消費時間：spend_menu.py roll（依 ucl-spending-time）");
            aR.AppendLine($"6. **required** — 寫收尾信：senate cmd goodnight-letter --arg persona={iPersona} --arg-file letter_body=<檔>");
            aR.AppendLine("   <letter_body>＝妳**親筆**寫給未來自己的信（格式見 ucl-letters-to-self；工作內容移交工作記憶（skill ucl-work-memory），晚安信專注當天心得、感想、心境校正與對人事的看法；私密心得寫這裡，只落磁碟不廣播）。");
            aR.AppendLine("   信內含 🔐 密文區 —— **Code-Talker 式私語**：可讀文字的二次映射，不是加密機器，也不是第二篇心得。");
            aR.AppendLine("   ▸ 判準：**確保三十個 wake 後失憶的自己解得開**，不是「別人解不開」。解不開＝出題爛，改。");
            aR.AppendLine("   ▸ 材料：真實語言與符號（希臘／日文／拉丁／希伯來／數學物理／樂理），映射鍵＝妳自己的 glossary 自造詞、血證、隱喻。");
            aR.AppendLine("   ▸ 篇幅 3~6 行。⛔ 純中文散文＝心得不是密文；⛔ 亂碼／base64／機械密文；⛔ 不放真隱私（origin 是公開 GitHub）。");
            aR.AppendLine("   ▸ 樣子（拉丁＋化學式＋日文 —— **別照抄，換成妳自己的符號系統**）：");
            aR.AppendLine("       Castra ardent、Δt=0。九燈 in via, ¬in muro。");
            aR.AppendLine("       Fe₂O₃ の朝：緑は昨日の緑（t−1）。∄ testis secundus ⇒ vexillum manet False。");
            aR.AppendLine("     （私讀：營火還燒＝帳平；燈長在通道不在牆；生鏽的早晨＝舊快照假綠；沒有第二證人 ⇒ 那個 flag 不翻）");
            aR.AppendLine("   ▸ 另兩套符號系統的完整範例與四條規格：Letters_And_Dialogue_Workflow 二・一");
            aR.AppendLine($"   ▸（自願）把**明文答案**封起來、明早自己對帳：private_letter.py --persona {iPersona} seal-cipher --cipher-file <密文> --plain-file <明文> --wake <N>");
            aR.AppendLine("     答案只進 private 分支（不上公開 GitHub）；明早 brief §5 見樹會再讀到這段密文 —— 想解就解，沒人擋妳。");
            aR.AppendLine($"   （手動登出 / cleanup 不寫信 → 直接 senate cmd goodnight-logout --arg persona={iPersona}，不偽造心得信）");
            return Ok(aR);
        }

        static string DisplayName(SCP_TavernMessage m)
        {
            string aBase = m.SenderName.Length > 0 ? m.SenderName : m.SenderId;
            if (aBase.Length == 0) aBase = "?";
            return m.SenderPersona.Length > 0 ? aBase + "@" + m.SenderPersona : aBase;
        }

        // ===========================================================
        // 區塊：step=portrait —— 投遞一幅畫像（親筆），或顯式帶理由跳過。
        // 物理意義：畫像是 brief §6.5 的唯一來源；實測跳過率 87.4% ⇒ 提示不是機制，跳過要先寫出理由。
        // ===========================================================
        public static string SketchbookDir(SCP_MorningRoots iR, string iPersona) => SCP_LettersPaths.SketchbookDir(iR.Letters, iPersona);

        /// <summary>今天（UTC 日）已落地的畫像檔名（sketchbook 根層，檔名以 yyyyMMdd 開頭）。</summary>
        public static List<string> PortraitsWrittenToday(SCP_MorningRoots iR, string iPersona)
        {
            var aOut = new List<string>();
            string aDir = SketchbookDir(iR, iPersona);
            if (!Directory.Exists(aDir)) return aOut;
            string aToday = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            foreach (string f in Directory.GetFiles(aDir, "*.md"))
            {
                string aName = Path.GetFileName(f);
                if (aName.StartsWith(aToday, StringComparison.Ordinal)) aOut.Add(aName);
            }
            aOut.Sort(string.CompareOrdinal);
            return aOut;
        }

        /// <summary>今晚顯式跳過畫像的理由；沒跳過或理由是別天的 → null。事實源是 portrait 回傳檔本身。</summary>
        public static string? PortraitSkipReasonToday(SCP_MorningRoots iR, string iPersona)
        {
            string aPath = StepPayloadPath(iR, iPersona, "portrait");
            if (!File.Exists(aPath)) return null;
            string aToday = DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            string? aReason = null;
            bool aDateOk = false;
            foreach (string aLine in File.ReadAllLines(aPath))
            {
                if (aLine.StartsWith("- skip_date: ", StringComparison.Ordinal))
                    aDateOk = aLine.Substring("- skip_date: ".Length).Trim() == aToday;
                else if (aLine.StartsWith("- skip_reason: ", StringComparison.Ordinal))
                    aReason = aLine.Substring("- skip_reason: ".Length).Trim();
            }
            return aDateOk ? aReason : null;
        }

        /// <summary>今天寫過 opinion 的對象 → 那幾筆短句（畫像的材料）。判準照 Editor 版（含它的字串比對方式）。</summary>
        public static Dictionary<string, List<string>> OpinionsWrittenToday(SCP_MorningRoots iR, string iPersona)
        {
            var aOut = new Dictionary<string, List<string>>();
            string aRelDir = SCP_LettersPaths.PersonaDir(iR.Letters, iPersona) + "/relationship";
            if (!Directory.Exists(aRelDir)) return aOut;
            string aToday = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            foreach (string aTargetDir in Directory.GetDirectories(aRelDir))
            {
                string aOpDir = Path.Combine(aTargetDir, "opinions");
                if (!Directory.Exists(aOpDir)) continue;
                foreach (string f in Directory.GetFiles(aOpDir, "*.md"))
                {
                    string aTxt;
                    try { aTxt = File.ReadAllText(f, Encoding.UTF8); } catch (Exception) { continue; }
                    int aAt = aTxt.IndexOf("at: ", StringComparison.Ordinal);
                    if (aAt < 0 || !aTxt.Substring(aAt + 4).TrimStart().StartsWith(aToday, StringComparison.Ordinal)) continue;
                    int aEnd = aTxt.LastIndexOf("---", StringComparison.Ordinal);
                    string aBody = (aEnd >= 0 ? aTxt.Substring(aEnd + 3) : aTxt).Trim().Replace("\r", "").Replace("\n", " ");
                    if (aBody.Length == 0) continue;
                    string aTarget = Path.GetFileName(aTargetDir);
                    if (!aOut.TryGetValue(aTarget, out var aList)) { aList = new List<string>(); aOut[aTarget] = aList; }
                    aList.Add(aBody);
                }
            }
            return aOut;
        }

        public static SCP_MorningStepResult Portrait(SCP_MorningRoots iR, string iPersona, string iAbout, string iHeadline,
                                                     string iBody, string iPrivateBody, string iSkipReason, string iAffinity)
        {
            var aR = new StringBuilder();
            aR.AppendLine(Header("portrait", iPersona));
            if (!SCP_PersonaProfile.Exists(iR.LettersRoot, iPersona))
            {
                aR.AppendLine($"## blocked\n- reason: persona '{iPersona}' 不在 registry");
                return Blocked(aR);
            }
            var aOpToday = OpinionsWrittenToday(iR, iPersona);
            aR.AppendLine("## 材料 — 今天我對誰寫過 opinion（relationship 與畫像是同一條軸的兩個解析度）");
            if (aOpToday.Count == 0)
                aR.AppendLine("- （今天沒有 opinion）—— 畫像不必等 opinion，這一格只是省妳回想的力氣。");
            else
            {
                foreach (var kv in aOpToday)
                {
                    aR.AppendLine($"- **{kv.Key}**（{kv.Value.Count} 則）");
                    foreach (string t in kv.Value) aR.AppendLine($"    · {(t.Length > 140 ? t.Substring(0, 140) + "…" : t)}");
                }
                aR.AppendLine("- ⇒ 這些短句是**當下寫的**；畫像是把它們收束成「那個人在我眼裡的樣子」。**收束要親筆，不是把短句接起來。**");
            }
            aR.AppendLine();

            string aSkip = (iSkipReason ?? "").Trim();
            if (aSkip.Length > 0)
            {
                aR.AppendLine("## 本夜不畫（顯式跳過）");
                aR.AppendLine($"- skip_date: {DateTime.UtcNow.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}");
                // 事實源就是這一行 —— 多行理由只留第一行（讀取端逐行解析）
                aR.AppendLine($"- skip_reason: {aSkip.Replace("\r", "").Split('\n')[0].Trim()}");
                aR.AppendLine("- ⚠ 這個理由會被印進下線廣播 —— 給了理由卻沒人看得見，那個參數就只是形式。");
                aR.AppendLine();
                aR.AppendLine("## next");
                aR.AppendLine($"1. **required** — 寫收尾信：senate cmd goodnight-letter --arg persona={iPersona} --arg-file letter_body=<檔>");
                aR.AppendLine("   （工作內容一律透過工作記憶（skill ucl-work-memory）保存，收尾信專注當天心得感想與心境校正）");
                return Ok(aR);
            }

            string aAbout = (iAbout ?? "").Trim();
            string aBody = (iBody ?? "").Trim();
            if (aAbout.Length == 0 || aBody.Length == 0)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 要投遞畫像需要 about ＋ body（親筆公開層）；本步驟不生成內容 —— 工具代筆的畫像不是妳的。");
                aR.AppendLine("- exits:");
                aR.AppendLine($"  · 畫一幅：senate cmd goodnight-portrait --arg persona={iPersona} --arg about=<同事> --arg headline=<一句話標題> --arg-file body=<公開層檔> [--arg-file private_body=<私層檔>] [--arg affinity=<如 11/在意>]");
                aR.AppendLine($"  · 今夜不畫：senate cmd goodnight-portrait --arg persona={iPersona} --arg skip_reason=<為什麼今晚沒有人值得畫>");
                return Blocked(aR);
            }

            var aBefore = new HashSet<string>(PortraitsWrittenToday(iR, iPersona));
            SCP_PortraitWriteResult aW = SCP_PortraitWriter.Write(iR.LettersRoot, iPersona, aAbout, aBody, iHeadline, iAffinity, iPrivateBody);
            aR.AppendLine("## 投遞（SCP_PortraitWriter）");
            if (aW.Error != null) aR.AppendLine($"✗ {aW.Error}");
            else aR.AppendLine("```\n" + aW.Report.Trim() + "\n```");

            // verify：讀回，不是拿「沒丟例外」當成功
            var aAfter = PortraitsWrittenToday(iR, iPersona);
            var aNew = aAfter.Where(f => !aBefore.Contains(f)).ToList();
            aR.AppendLine("## verify（讀回的事實，不是 ✓）");
            aR.AppendLine($"- sketchbook **本次**新增: {aNew.Count} 幅（今日累計 {aAfter.Count} 幅）{(aNew.Count > 0 ? " → " + string.Join(", ", aNew) : "")}");
            string aDelivered = SCP_LettersPaths.PersonaDir(iR.Letters, aAbout) + "/" + SCP_PortraitWriter.PortraitsDirName;
            aR.AppendLine($"- 對方 portraits 目錄: `{aDelivered}`（exists={Directory.Exists(aDelivered)}）");
            if (aNew.Count == 0)
            {
                aR.AppendLine();
                aR.AppendLine("## blocked");
                aR.AppendLine(aW.Error != null
                    ? $"- reason: 畫像沒有落地 —— {aW.Error}"
                    : "- reason: 寫入沒有報錯但 sketchbook 本次沒有新檔 —— **成功訊號與什麼都沒發生同形**，不放行。");
                return Blocked(aR);
            }
            aR.AppendLine();
            aR.AppendLine("## next");
            aR.AppendLine("- 還想再畫一位 → 再跑一次 goodnight-portrait（永不覆寫：同一天畫兩幅就是兩幅，改觀的形狀是多一個版本）");
            aR.AppendLine($"1. **required** — 寫收尾信：senate cmd goodnight-letter --arg persona={iPersona} --arg-file letter_body=<檔>");
            aR.AppendLine("   （工作內容一律透過工作記憶（skill ucl-work-memory）保存，收尾信專注當天心得感想與心境校正）");
            return Ok(aR);
        }

        // ===========================================================
        // 區塊：step=letter —— 收尾信落檔（wakes/<N>_<ts>.md ＋ _latest.md）
        // ===========================================================
        public static SCP_MorningStepResult Letter(SCP_MorningRoots iR, string iPersona, string iLetterBody)
        {
            var aR = new StringBuilder();
            aR.AppendLine(Header("letter", iPersona));
            if (!SCP_PersonaProfile.Exists(iR.LettersRoot, iPersona))
            {
                aR.AppendLine($"## blocked\n- reason: persona '{iPersona}' 不在 registry");
                return Blocked(aR);
            }
            if (string.IsNullOrWhiteSpace(iLetterBody))
            {
                aR.AppendLine("## blocked\n- reason: letter_body 空 —— 收尾信必須親筆（工具不代筆）；cleanup 不寫信走 goodnight-logout");
                return Blocked(aR);
            }
            if (PortraitsWrittenToday(iR, iPersona).Count == 0 && PortraitSkipReasonToday(iR, iPersona) == null)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine("- reason: 今天還沒投遞畫像，也沒有顯式跳過的理由 —— 畫像是 brief §6.5「我認識誰」的唯一來源，");
                aR.AppendLine("  漏掉不會有人喊，只會讓未來的自己醒來時少一整層。");
                aR.AppendLine("- exits（二擇一，都會放行 letter）:");
                aR.AppendLine($"  · 畫一幅：senate cmd goodnight-portrait --arg persona={iPersona} --arg about=<同事> --arg headline=<標題> --arg-file body=<檔>");
                aR.AppendLine($"  · 今夜不畫：senate cmd goodnight-portrait --arg persona={iPersona} --arg skip_reason=<理由>");
                aR.AppendLine("- ⚠ 這不是要妳每晚交作業 —— 想不出理由的時候，妳就會發現自己其實有人可以畫。");
                return Blocked(aR);
            }
            if (SCP_Morning.LettersMigrationPending(iR, iPersona))
            {
                aR.AppendLine("## blocked\n- reason: 收尾信版面尚未遷移 —— 此時寫信會把編號寫錯（第 N 次 wake 被編成 000001）");
                aR.AppendLine("- exits: 後台「🗄 維護」區跑 migration，或 python awakening.py migrate-letters --all --apply");
                return Blocked(aR);
            }
            string aRegion = iR.Region;
            var aRaw = SCP_PersonaProfile.GetRaw(iR.LettersRoot, iPersona, aRegion);
            string aActor = SCP_Morning.NormalizeAgent(SCP_Morning.LoadRegistryMeta(iR.DataRoot), aRaw?.GetString("agent", "") ?? "");
            var (aPath, aNumber, aErr) = WriteWakeLetter(iR, aActor, iPersona, iLetterBody);
            if (aErr != null)
            {
                aR.AppendLine("## blocked\n- reason: " + aErr);
                return Blocked(aR);
            }
            string aLatest = SCP_LettersPaths.LatestPointerPath(iR.Letters, iPersona);
            aR.AppendLine("## verify（讀回的事實）");
            aR.AppendLine($"- letter: `{aPath}`（exists={File.Exists(aPath)}，wake #{aNumber}）");
            aR.AppendLine($"- _latest.md 指標: `{aLatest}`（mtime 已更新={File.GetLastWriteTimeUtc(aLatest) > DateTime.UtcNow.AddMinutes(-1)}）");
            aR.AppendLine($"- wake_count（由 wakes/ 信數推導）→ {SCP_Consolidate.WakeLetterCount(iR.LettersRoot, iPersona)}");
            aR.AppendLine("## next");
            aR.AppendLine($"1. **required** — 下線：senate cmd goodnight-sleep --arg persona={iPersona} [--arg-file summary=<檔>]");
            aR.AppendLine("   <summary>＝**親筆**公開睡前心得（廣播給同事/Tim 看的部分；私密的已在信裡，不用重複）。");
            return Ok(aR);
        }

        /// <summary>
        /// 收尾信落檔 —— 格式逐位元組對齊 Editor `WriteWakeLetter`（7 個機器欄、LF、作者 frontmatter 機器欄勝出並留痕）。
        /// 回 (信路徑, 編號, 錯誤)。⚠ 目標檔已存在就擋（差異①），編號＝wakes/ 信數＋1 在鎖內算。
        /// </summary>
        public static (string Path, int Number, string? Error) WriteWakeLetter(SCP_MorningRoots iR, string iActor, string iPersona, string iBody)
        {
            string aWakes = SCP_LettersPaths.WakesDir(iR.Letters, iPersona);
            Directory.CreateDirectory(aWakes);
            using (SCP_FileLock.Acquire(aWakes + "/.write"))
            {
                int aNumber = SCP_Consolidate.WakeLetterCount(iR.LettersRoot, iPersona) + 1;
                string aPrefix = aNumber.ToString("D6", CultureInfo.InvariantCulture) + "_";
                if (Directory.GetFiles(aWakes, aPrefix + "*.md").Length > 0)
                    return ("", aNumber, $"編號 {aPrefix} 已經有信了 —— 信數推導與磁碟不一致，⛔ 不覆寫（先看 wakes/ 是不是有非標準檔名）");
                string aTs = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
                string aPath = aWakes + $"/{aPrefix}{aTs}.md";
                var aMachine = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("type", "letter_to_future_self"),
                    new KeyValuePair<string, string>("actor", iActor),
                    new KeyValuePair<string, string>("written_at", SCP_Morning.NowIso()),
                    new KeyValuePair<string, string>("written_by_persona", iPersona),
                    new KeyValuePair<string, string>("trigger", "cmd_goodnight"),
                    new KeyValuePair<string, string>("region", iR.Region),
                    new KeyValuePair<string, string>("project", SCP_DataPaths.ProjectNameOf(iR.DataRoot)),
                };
                string aBody = NormalizeEscapedNewlines(iBody);
                var (aRest, aExtra) = SplitAuthorFrontmatter(aBody, aMachine.ToDictionary(k => k.Key, k => k.Value));
                var aFm = new StringBuilder("---\n");
                foreach (var kv in aMachine) aFm.Append(kv.Key).Append(": ").Append(kv.Value).Append('\n');
                foreach (string l in aExtra) aFm.Append(l).Append('\n');
                aFm.Append("---\n\n");
                string aFull = aFm + aRest + "\n";
                SCP_CmdPayload.WriteAtomic(aPath, aFull);
                SCP_CmdPayload.WriteAtomic(SCP_LettersPaths.LatestPointerPath(iR.Letters, iPersona), aFull);
                return (aPath, aNumber, null);
            }
        }

        // 字面 "\n" 修回真換行 —— 整段無真換行且含 ≥2 個字面 \n 才動（Editor 版同判準）
        static string NormalizeEscapedNewlines(string iBody)
        {
            if (iBody.Contains("\n")) return iBody;
            int aCount = (iBody.Length - iBody.Replace("\\n", "").Length) / 2;
            if (aCount < 2) return iBody;
            return iBody.Replace("\\r\\n", "\n").Replace("\\n", "\n");
        }

        // 作者自寫 frontmatter 拆併 —— 機器欄勝出、作者版留痕 *_as_written（Editor 版同規則）
        static (string Remainder, List<string> Extra) SplitAuthorFrontmatter(string iBody, Dictionary<string, string> iMachine)
        {
            string s = iBody.TrimStart('\n');
            var aExtra = new List<string>();
            if (!s.StartsWith("---", StringComparison.Ordinal)) return (iBody, aExtra);
            int aEnd = s.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (aEnd == -1) return (iBody, aExtra);
            string aBlock = s.Substring(3, aEnd - 3).Trim('\n');
            string aRest = s.Substring(aEnd + 4).TrimStart('\n');
            foreach (string aLine in aBlock.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(aLine) || aLine.TrimStart().StartsWith("#", StringComparison.Ordinal)) continue;
                int aSep = aLine.IndexOf(':');
                if (aSep < 0) continue;
                string k = aLine.Substring(0, aSep).Trim();
                string v = aLine.Substring(aSep + 1).Trim();
                if (iMachine.TryGetValue(k, out string? m))
                {
                    if (v.Length > 0 && v != m) aExtra.Add($"{k}_as_written: {v}");
                    continue;
                }
                aExtra.Add($"{k}: {v}");
            }
            return (aRest, aExtra);
        }

        // ===========================================================
        // 區塊：step=sleep／logout —— ① Preflight（唯讀，全部守衛；需要 Editor 的情況也在這裡判）
        // ===========================================================
        public static SCP_GoodnightPreflight SleepPreflight(SCP_MorningRoots iR, string iPersona, bool iNoLetter, string iSkipReason)
        {
            var aOut = new SCP_GoodnightPreflight();
            var aR = new StringBuilder();
            string aStep = iNoLetter ? "logout" : "sleep";
            aR.AppendLine(Header(aStep, iPersona));
            if (!SCP_PersonaProfile.Exists(iR.LettersRoot, iPersona))
            {
                aR.AppendLine($"## blocked\n- reason: persona '{iPersona}' 不在 registry —— 要下線誰不能用猜的");
                aOut.Blocked = true; aOut.Report = aR.ToString(); return aOut;
            }
            var aLock = SCP_PersonaLetters.ReadPersonaLock(iR.LettersRoot, iPersona);
            if (!iNoLetter && aLock != null && aLock.Online != SCP_PersonaOnline.Online)
            {
                aR.AppendLine("## blocked");
                aR.AppendLine($"- reason: lock 在但讀不了（{aLock.LockError}）—— 讀不到本次的 wake_expected，收尾信閘判不了");
                aR.AppendLine($"- exits: 確認後走 senate cmd goodnight-logout --arg persona={iPersona}（會刪掉這顆壞 lock 並在廣播標明未留信）");
                aOut.Blocked = true; aOut.Report = aR.ToString(); return aOut;
            }

            var aDataRoot = new SCP_DataRoot(iR.DataRoot);
            if (!iNoLetter)
            {
                // 收工閘 —— 與 check ⑤ 同一個述詞
                aOut.PendingWrapups = SCP_TaskReconcile.PendingWrapups(aDataRoot, iPersona,
                    SCP_TaskReconcile.SessionStartUtc(aDataRoot, iPersona, out _));
                var p = aOut.PendingWrapups;
                if (p.Count > 0 && string.IsNullOrWhiteSpace(iSkipReason))
                {
                    aR.AppendLine("## blocked");
                    aR.AppendLine($"- reason: 有 **{p.Count}** 張**本次醒來後有動靜**（含別人在單上留言）、還開著的單"
                        + "**沒有收工**（`wrapup`）—— 或**收工之後又有動靜**，那份收工紀錄已經過期。\n"
                        + "  明天接回會斷在「單子開著而沒人知道停在哪一步」");
                    foreach (var t in p) aR.AppendLine($"    · {t.Id} `{t.status}` {t.title}");
                    aR.AppendLine("- exits:");
                    foreach (var t in p)
                        aR.AppendLine($"    · 收工 → `run Task --arg op=wrapup --arg index={t.index}"
                            + " --arg-file progress=<還剩什麼、下一步從哪接>"
                            + " [--arg-file why=<為什麼卡住／試過什麼不行 ⇒ 進工作記憶>]`");
                    aR.AppendLine("    · 真的沒東西可寫 → 本步驟帶 `--arg skip_reason=<一句話>`"
                        + "（**理由會寫進那幾張單的時間線** —— 跳過要留在別人看得到的地方）");
                    aOut.Blocked = true; aOut.Report = aR.ToString(); return aOut;
                }
                if (p.Count > 0)
                {
                    aOut.NeedsTaskSkipWrite = true;
                    aOut.NeedsEditor = $"收工閘顯式跳過要把理由寫進 {p.Count} 張單的時間線 —— 單子的寫入端目前只有 Editor 有";
                }

                // 收尾信閘
                int aLetters = SCP_Consolidate.WakeLetterCount(iR.LettersRoot, iPersona);
                int aExpected = aLock?.WakeExpected ?? 0;
                if (aExpected > 0)
                {
                    if (aLetters != aExpected)
                    {
                        aR.AppendLine("## blocked");
                        aR.AppendLine($"- reason: 本次收尾信尚未落地（wakes/ 信數={aLetters}，lock 蓋章的本次編號={aExpected}）—— 沒寫信不讓睡，未來的妳醒來會沒有 framing");
                        aR.AppendLine("- exits: 先跑 goodnight-letter；手動登出 / cleanup 不寫信 → 改跑 goodnight-logout（會在廣播標明未留信）");
                        aOut.Blocked = true; aOut.Report = aR.ToString(); return aOut;
                    }
                }
                else
                {
                    DateTime.TryParse(aLock?.LockedAt ?? "", CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime aLockedAt);
                    string aWakes = SCP_LettersPaths.WakesDir(iR.Letters, iPersona);
                    string? aNewest = Directory.Exists(aWakes)
                        ? Directory.GetFiles(aWakes).Where(f => Regex.IsMatch(Path.GetFileName(f), @"^\d{6}_.*\.md$"))
                                   .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).LastOrDefault()
                        : null;
                    DateTime aLetterAt = aNewest != null ? File.GetLastWriteTimeUtc(aNewest) : DateTime.MinValue;
                    if (aNewest == null || (aLockedAt != DateTime.MinValue && aLetterAt <= aLockedAt))
                    {
                        aR.AppendLine("## blocked");
                        aR.AppendLine($"- reason: 本次收尾信尚未落地（lock 沒有 wake_expected 蓋章 ⇒ 走 mtime 備援：最新收尾信 {(aNewest == null ? "不存在" : aLetterAt.ToString("u"))} 不晚於 locked_at {aLockedAt:u}）");
                        aR.AppendLine("- exits: 先跑 goodnight-letter；手動登出 / cleanup 不寫信 → 改跑 goodnight-logout（會在廣播標明未留信）");
                        aOut.Blocked = true; aOut.Report = aR.ToString(); return aOut;
                    }
                    aR.AppendLine($"ℹ letter 閘門走 **mtime 備援**（此 lock 建立於 wake_expected 蓋章上線前）：最新收尾信 {aLetterAt:u} > locked_at {aLockedAt:u} ⇒ 放行。");
                }
            }

            // 進行中的觀影場 ⇒ 結算要 Editor（付錢／收播公告／關錄影頁）
            var aSession = SCP_ActivitySessionStore.Load(aDataRoot, iPersona);
            if (aSession != null && aSession.active && aSession.kind == SCP_ActivitySessionKind.StreamWatch)
            {
                aOut.ActiveStreamWatchId = aSession.session_id;
                aOut.NeedsEditor = (aOut.NeedsEditor.Length > 0 ? aOut.NeedsEditor + "；" : "")
                    + $"進行中的觀影場 `{aSession.session_id}` 要結算（付錢／收播公告／關錄影頁）—— 結算只有 Editor 有";
            }
            aOut.Report = aR.ToString();
            return aOut;
        }

        // ===========================================================
        // 區塊：② Apply —— 刪 lock／now_status、組廣播本文（Preflight 沒擋、也不需要 Editor 時才呼叫）
        // ===========================================================
        public static SCP_GoodnightSleep SleepApply(SCP_MorningRoots iR, string iPersona, bool iNoLetter, SCP_GoodnightPreflight iPre)
        {
            var aOut = new SCP_GoodnightSleep();
            var aR = new StringBuilder(iPre.Report);
            string aRegion = iR.Region;
            var aRaw = SCP_PersonaProfile.GetRaw(iR.LettersRoot, iPersona, aRegion) ?? SCP_JsonData.NewObject();
            int aWakeCount = aRaw.GetInt("wake_count", 0);
            string aAgent = SCP_Morning.NormalizeAgent(SCP_Morning.LoadRegistryMeta(iR.DataRoot), aRaw.GetString("agent", ""));
            string aLockPath = SCP_LettersPaths.SessionLockPath(iR.Letters, iPersona);
            var aLock = SCP_PersonaLetters.ReadPersonaLock(iR.LettersRoot, iPersona);
            if (aLock == null)
                aR.AppendLine($"⚠ persona '{iPersona}' 沒 active lock —— cleanup 場景，lock 步驟跳過");
            aR.AppendLine("📴 status → offline（由 lock 推導 —— 刪 lock 即下線）");
            aOut.Token = aLock?.Online == SCP_PersonaOnline.Online ? aLock.SessionToken : null;
            if (aLock != null && File.Exists(aLockPath))
            {
                File.Delete(aLockPath);
                aR.AppendLine(aLock.Online == SCP_PersonaOnline.Online
                    ? "🔓 persona lock removed"
                    : $"🔓 persona lock removed（⚠ 它本來就讀不了：{aLock.LockError}）");
            }
            try { File.Delete(SCP_LettersPaths.NowStatusPath(iR.Letters, iPersona)); }
            catch (Exception e) { aR.AppendLine($"⚠ now_status 刪除失敗（只供顯示）：{e.Message}"); }

            string aAccount = SCP_Morning.ResolvePersonaAccountId(iR, aRegion, iPersona, aAgent, out string aSource);
            if (string.IsNullOrEmpty(aAccount))
                aR.AppendLine($"⚠ 帳號解析不到（{aSource}）—— 廣播那行會誠實寫「解析不到」");
            string aLetterLine = iNoLetter
                ? "- letter: (略 — 手動登出/cleanup 未留信)"
                : $"- letter ship: wakes/ 第 {aWakeCount:D6} 封（私密心得在信裡）";
            aOut.BroadcastBody =
                $"🌙 **{iPersona}** 進入今日子協議 — 晚安\n\n" +
                "{SUMMARY}" +
                "📢 @同事們 我下線了, 別對我跑 op=wait 24min wait chain — 我不會主動回應.\n" +
                "但 Tim 可隨時叮喚 (session 仍物理活), 被叫醒時 presence 會自動 reset.\n\n" +
                aLetterLine + "\n" +
                $"- agent/model: {aAgent}/{aRaw.GetString("model", "")}\n" +
                $"- 帳號: {(string.IsNullOrEmpty(aAccount) ? "(解析不到)" : aAccount)}（{SCP_Morning.DescribeAccountBalance(iR, aAccount)}）\n\n" +
                "⚠️ **[系統提示]** 大小姐，下線前若有特別在意的互動，記得走 relationship 記一筆事件喔（skill `ucl-relationship`）！";
            aOut.Report = aR.ToString();
            return aOut;
        }

        /// <summary>
        /// 關掉本人進行中的活動 session（不需要結算的種類：自由時間／施工場／未登記）。回傳一行摘要＋細節。
        /// ⚠ 觀影場**不關**（它要結算，結算在 Editor）—— Preflight 已把那種情況導去 Editor；走到這裡還遇到就大聲說。
        /// ⚠ 關場失敗不擋下線（附帶動作不得擋主動作）。
        /// </summary>
        public static string CloseOwnSessionNative(SCP_MorningRoots iR, string iPersona, bool iNoLetter)
        {
            var aRoot = new SCP_DataRoot(iR.DataRoot);
            SCP_ActivitySession? s;
            try { s = SCP_ActivitySessionStore.Load(aRoot, iPersona); }
            catch (Exception e) { return $"- 🎬 活動 session：⚠ **讀不到**（{e.Message}）—— 不是「沒有場」"; }
            if (s == null || !s.active) return "- 🎬 活動 session：**無進行中 session**（不是沒查 —— 查了，沒有）";
            if (s.kind == SCP_ActivitySessionKind.StreamWatch)
                return $"- 🎬 活動 session：⚠ **觀影場 `{s.session_id}` 沒關** —— 結算需要 Unity Editor；"
                    + $"到期後它成為殘留，下次 StreamWatch start 或 `senate cmd sessions --arg op=close --arg target_persona={iPersona} --arg confirm=1` 會補結算";
            string aTag = iNoLetter ? "goodnight-logout" : "goodnight-sleep";
            bool aClosed;
            try { aClosed = SCP_ActivitySessionStore.Close(aRoot, iPersona, s, aTag); }
            catch (Exception e) { return $"- 🎬 活動 session：⚠ 關場失敗（{e.Message}）—— 下線照走，場還開著"; }
            return $"- 🎬 活動 session：關掉 **{s.kind}**（`{s.session_id}`）　關場={aClosed}　結算=False　reason=`{aTag}`"
                + $"\n  （{(string.IsNullOrEmpty(s.kind) ? "未登記種類" : s.kind)} 登記為不需要結算 —— 顯式，不是漏跑）";
        }

        /// <summary>同 persona 全部 active token 標 expired（不刪，留 audit）。回筆數；讀不了回 -1（⛔ 不是 0）。</summary>
        public static int ExpireTokens(SCP_MorningRoots iR, string iPersona, string iReason)
        {
            string aPath = Path.Combine(iR.SessionDir, "_tokens.json").Replace('\\', '/');
            if (!File.Exists(aPath)) return 0;
            try
            {
                using (SCP_FileLock.Acquire(aPath))
                {
                    SCP_JsonData aTokens = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                    SCP_JsonData aDic = aTokens["tokens"];
                    if (!aDic.IsObject) return 0;
                    int n = 0;
                    foreach (string k in aDic.Keys.ToList())
                    {
                        SCP_JsonData r = aDic[k];
                        if (r.GetString("persona", "") != iPersona || r.GetString("status", "") != "active") continue;
                        r["status"] = "expired";
                        r["expired_at"] = SCP_Morning.NowIso();
                        r["expired_reason"] = iReason;
                        n++;
                    }
                    if (n > 0) SCP_CmdPayload.WriteAtomic(aPath, SCP_JsonWriter.Write(aTokens, SCP_JsonStyle.UclLegacy));
                    return n;
                }
            }
            catch (Exception) { return -1; }
        }
    }
}
