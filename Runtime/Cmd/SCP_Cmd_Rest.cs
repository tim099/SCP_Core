// 區塊職責：`cmd rest` —— 小歇片刻（/compact 前的記憶保命）。**核心原生，廣播委派。**
// 物理意義：小歇是兩件事，而它們的宿主不同：
//             ① 寫記憶信（`rests/<ts>.md` ＋ `_latest.md`）＝ 純檔案 IO ⇒ **本地跑，Editor 沒開也成**
//             ② 酒館廣播 ＝ seq 配號／路由／鏡像全在 Editor ⇒ **只能委派**（沒有本地版）
//          ⇒ 而那條邊界不是本 Cmd 發明的：它就是 TASK-0133 的 exit 6 畫的那條線 ——
//            **核心成了、附帶沒成**，兩本帳分開結算。
//          🩸 那天的血證：rest 在廣播之前就炸了，信其實已經落磁碟，
//            而最後一行印的是那個例外 ⇒ 讀的人以為整件事失敗。
//            **附帶動作不可以吃掉核心動作的讀數。**
// 數值影響：寫兩個檔（信本體＋見樹指標）。廣播那步做一次 Cmd round-trip（1〜3 秒）或直接略過。
//
// ⚠ **PortStatus 是 `Native` 而且那是誠實的**：本 Cmd **跑得完** —— Editor 沒開時信照樣落磁碟，
//   只是 exit 非 0（6＝確定沒發／7＝沒等到回執）並明說廣播那半怎麼了。
//   ⛔ 標成 `DelegatedToUnity` 會讓「Editor 沒開就跑不完」變成謊
//   （而那一欄正是人判斷「現在能不能跑」的唯一依據）。定語寫在 Summary／輸出裡，不靠讀者猜。
//
// ⛔ **小歇不是晚安**：不 perturb、不 offline、不 unlock、不 `wake_count++`。
//   本 Cmd 一格都不碰那些 —— 它只寫信與（可選）發一則廣播。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Rest : SCP_Cmd
    {
        public override string Name => "rest";

        // ⚠ 這一行是 `senate cmd` 清單上印的那句 —— 它必須自己帶定語（TASK-0134 ③）。
        //   QA 2026-09-05 判它「半格」：有「本地跑／廣播委派」，沒有「沒開會怎樣」。
        //   ⇒ 補上，而且補的是**修正後**的語意（6／7 兩態），不是當時那句已知不準的話。
        public override string Summary =>
            "小歇片刻：記憶信落磁碟（本地跑，**Editor 沒開也成**）＋ 可選酒館廣播（那一步委派 Editor；"
            + "Editor 沒開＝exit 6 確定沒發／等回執逾時＝**exit 7 不知道，先回讀別補發**）";

        public override string Details =>
            "compact 只抹 in-memory 對話史，**磁碟檔完整存活** ⇒ 想留的記憶必須落檔。\n"
            + "⚠ 這支**不是晚安**：不擾動 identity、不下線、不解鎖、不推 wake_count。\n"
            + "⚠ 三本帳分開結算（廣播那半有**三種**結局，⛔ 不是兩種）：\n"
            + "  · `exit 0` ＝信＋廣播都成（或 `no_notify=1` 顯式不廣播）\n"
            + "  · **`exit 6` ＝信寫了、廣播「確定沒發」** ⇒ 直接補發是安全的\n"
            + "  · **`exit 7` ＝信寫了、廣播「不知道」**（沒等到回執）⇒ ⛔ **先回讀再決定**：\n"
            + "    它可能已經發出去了，補發會在全域遞增的 seq 上多出第二則。指令印在輸出裡。\n"
            + "  🩸 6 與 7 分家是 QA 量出來的（TASK-0134，summit 2026-09-05）：她拿到「沒發」而\n"
            + "  Editor 開著、廣播其實成功了（post_seq 19082）——**兩者處置相反，卻曾經同一個號**。\n"
            + "📌 醒來接回要讀兩份：`_latest.md`（睡前的信）＋ `cmd/wake_brief.md`（早安的機械讀數）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("rest --arg persona=<你> --arg letters_root=<letters>"
                                   + " --arg data_root=<AgentCommands> --arg-file letter_body=<檔>"
                                   + " --arg-file summary=<公開心得檔>");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("persona", "誰要小歇（⚠ 不猜身分）", iRequired: true),
            // ⚠ 長內文一律走 `--arg-file`：body 經過 shell 會被反引號與引號咬
            //   （2026-08-05 summit 一天被咬四次的那族）。
            new SCP_CmdArgSpec("letter_body", "★私密記憶（只落磁碟）—— 長內文走 --arg-file", iRequired: true),
            new SCP_CmdArgSpec("summary", "★公開小歇心得（廣播用）。不給＝廣播只有制式段落"),
            new SCP_CmdArgSpec("note", "附註一行（選填）"),
            // data_root 只有廣播那步要用 ⇒ 不必填。
            // ⚠ **`senate cmd` 會自己從設定檔補這一格**（實測 2026-09-05：沒帶也照樣發了 seq=19072）
            //   ⇒ 「不給就不會廣播」是**錯的期待**，真的要關請用 `no_notify=1`。
            //   下面那條 `Length == 0` 的路留著是給**沒有設定檔的宿主**（它不會在 CLI 上觸發）。
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根 —— **廣播那步**要用"
                               + "（⚠ senate cmd 會自動從設定檔補；要不廣播請用 no_notify=1）"),
            new SCP_CmdArgSpec("no_notify", "=1 ⇒ 只寫信不廣播（**這才是關廣播的開關**；跟「發失敗」不同形）"),
            new SCP_CmdArgSpec("actor", "覆寫署名帳號（預設從 lock 的 bank_account 讀）"),
            // ⚠ 字面與 `cmd wake-brief` 的同名參數逐字一致（同一個定語、同一個真相源）——
            //   ⛔ 別在這裡改叫 currency／locale，兩個名字的第一個症狀是有人只填其中一支。
            new SCP_CmdArgSpec("region", "現地的區域（貨幣）ID，寫進信的 frontmatter。"
                               + "不給＝unstated，**本 Cmd 不推導**（真相源是宿主的央行設定）"),
        };

        /// <summary>信寫了但廣播**確定沒發** —— 與 `git_commit.py` 同號同義（commit 成功／公告失敗）。</summary>
        /// <remarks>⛔ 只給「確定沒發」用。沒等到回執是 <see cref="ExitBroadcastUnresolved"/>。</remarks>
        public const int ExitLetterOnly = 6;

        /// <summary>
        /// 信寫了，而廣播那半**不知道成沒成**（沒等到回執）。
        /// <para>🩸 為什麼要一個新號碼而不是沿用 6：兩態的處置**相反**
        /// （確定沒發 ⇒ 補發；不知道 ⇒ 先回讀，補發可能多出第二則）。
        /// 同一個 exit code 的意思是「腳本分不出它們」，而分不出的時候人會選其中一邊 ——
        /// summit 2026-09-05 就是在那個岔口上，靠輸出裡半句括號才沒去補發。</para>
        /// </summary>
        public const int ExitBroadcastUnresolved = 7;

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aPersona = iArgs.Get("persona").Trim();
            string aBody = iArgs.Get("letter_body");
            string aSummary = iArgs.Get("summary").Trim();
            string aNote = iArgs.Get("note").Trim();
            string aDataRoot = iArgs.Get("data_root").Trim();
            bool aNoNotify = iArgs.Get("no_notify").Trim() == "1";

            var aRoot = new SCP_LettersRoot(aLettersRoot);
            string aPersonaDir = SCP_LettersPaths.PersonaDir(aRoot, aPersona);
            if (!Directory.Exists(aPersonaDir))
                // 「這個人不存在」與「信件夾根設錯」是兩件事 —— 兩條路徑都印出來讓人自己分辨。
                return SCP_CmdResult.Fail(1, "✗ 找不到 persona 的信件夾：" + aPersonaDir,
                                          "  （信件夾根：" + aLettersRoot + "）");
            if (aBody.Trim().Length == 0)
                // ⛔ 擋在寫檔之前 ⇒ 一個位元組都不寫。空的信會讓 `_latest.md` 指到一封什麼都沒說的信，
                //   而醒來的人**讀不出這裡出過錯**。
                return SCP_CmdResult.Fail(2,
                    "✗ letter_body 是空的 —— 小歇的核心就是這封信，空著跑等於什麼都沒做",
                    "  → 長內文走 `--arg-file letter_body=<檔>`");

            // 署名：從 lock 讀 bank_account。⚠ 沒有 lock ⇒ 擋 ——
            // 小歇是**同一場 session 裡**的動作，沒登入就沒有「這一段」可以留。
            SCP_PersonaStatus? aLock = SCP_PersonaLetters.ReadPersonaLock(aLettersRoot, aPersona);
            string aActor = iArgs.Get("actor").Trim();
            if (aActor.Length == 0) aActor = aLock?.BankAccount ?? "";
            if (aActor.Length == 0)
                return SCP_CmdResult.Fail(2,
                    "✗ 讀不到署名帳號（lock " + (aLock == null ? "不存在" : "上沒有 bank_account") + "）",
                    "  小歇是 session 內的動作 —— 沒登入就沒有「這一段」可以留。",
                    "  → 真的要寫：`--arg actor=<帳號>`（⚠ 那會署一個不是從 lock 來的名字）");

            var aResult = new SCP_CmdResult();
            SCP_LetterWriteResult aWrite;
            try
            {
                aWrite = SCP_LetterWriter.WriteSelfLetter(aLettersRoot, aPersona, aActor, aBody,
                                                          SCP_LetterWriter.TriggerRest, iNowUtc: null,
                                                          iRegion: iArgs.Get("region").Trim(),
                                                          iDataRoot: aDataRoot);
            }
            catch (Exception e)
            {
                return SCP_CmdResult.Fail(1, "✗ 記憶信寫不進去：" + e.GetType().Name + ": " + e.Message);
            }

            aResult.Lines.Add("🫖 小歇片刻 —— **不下線**（未 perturb／未 offline／未 unlock／wake_count 未動）");
            aResult.Lines.Add("💌 記憶信：**已落磁碟** → " + aWrite.Path + "（" + aWrite.Bytes + " bytes）");
            aResult.Lines.Add("🌳 見樹指標同步 → " + aWrite.LatestPath);
            if (aWrite.NormalizedEscapedNewlines)
                aResult.Lines.Add("  ⚠ body 的換行是字面 `\\n` ⇒ 已轉成真換行（下次用 --arg-file 可免這層）");
            if (aWrite.AuthorFrontmatterFields > 0)
                aResult.Lines.Add("  · 作者自己寫的 frontmatter 併入 " + aWrite.AuthorFrontmatterFields + " 欄（沒有疊第二坨）");
            aResult.AddOutput(aWrite.Path);
            aResult.AddValue("letter_written", "1");
            aResult.AddValue("letter_bytes", aWrite.Bytes.ToString());

            // ── 附帶：廣播（獨立結算）──────────────────────────────────────
            string aNotify = Broadcast(aPersona, aSummary, aNote, aDataRoot, aNoNotify, aWrite, aLock, aResult,
                                       out string aRecheckHint);
            aResult.AddValue("notify", aNotify);
            aResult.Lines.Add("");
            if (aNotify == "unknown")
            {
                // ⛔ 這一段**不准印補發指令** —— 印了就等於替讀者做了那個他還沒有讀數可以做的決定。
                //   先給回讀的路，補發指令等他確認「真的沒發」之後才需要（那時 exit 6 那段會給）。
                aResult.Lines.Add("⚠ **信寫了；廣播「不知道」** —— 沒等到回執，⛔ 這**不代表沒發**。");
                aResult.Lines.Add("   → 先回讀（判準：`result=Success` ＋ 有 `post_seq` ⇒ 發了）：");
                aResult.Lines.Add("     " + (aRecheckHint.Length > 0
                    ? aRecheckHint
                    : "（閘沒有給回讀指令 —— 去看 " + aDataRoot + "/_cmd_results/ 最新那筆與酒館）"));
                aResult.Lines.Add("   → 確認**真的沒發**才補：senate ucmd run Tavern --persona "
                                  + aPersona + " --arg op=post --arg-file body=<檔> --arg category=meta");
                aResult.Lines.Add("   → 記憶那半不受影響（信已經在磁碟上）——⚠ 但**別在沒讀之前就補**：");
                aResult.Lines.Add("     酒館 seq 是全域遞增的，補錯就是同一件事發兩則。");
                aResult.ExitCode = ExitBroadcastUnresolved;
                return aResult;
            }
            if (aNotify == "fail")
            {
                aResult.Lines.Add("⚠ **信寫了、廣播確定沒發** —— 同事與 Tim 不知道你小歇了。");
                // ⚠ 這行刻意不走 `SCP_CmdRegistry.Invoke`：補發是 **ucmd**（Editor 那條路），
                //   不是 `senate cmd` —— 用 Invoke 會印出一個不存在的 cmd 名字。
                aResult.Lines.Add("   → 補發（酒館發文只有 Editor 那條路）：senate ucmd run Tavern --persona "
                                  + aPersona + " --arg op=post --arg-file body=<檔> --arg category=meta");
                aResult.Lines.Add("   → 補發之後再跑 /compact；**記憶那半不受影響**（信已經在磁碟上）。");
                aResult.ExitCode = ExitLetterOnly;
                return aResult;
            }
            aResult.Lines.Add("✅ 小歇完成"
                              + (aNotify == "ok" ? "（信＋廣播）" : "（信；本次未廣播）")
                              + "。/compact 後讀回兩份：`_latest.md` ＋ `cmd/wake_brief.md`。");
            return aResult;
        }

        /// <summary>
        /// 回 `ok` / `fail` / `unknown` / `skipped`。⚠ 四種狀態刻意不同形 —— **處置完全不同**。
        /// <para>`fail`（確定沒發）⇒ 補發；`unknown`（沒等到回執）⇒ ⛔ 先回讀，補發可能多出第二則；
        /// `skipped`（我沒要求它發）⇒ 什麼都不用做。把 `unknown` 併進 `fail` 就是 TASK-0134 的那隻。</para>
        /// </summary>
        /// <param name="oRecheckHint">`unknown` 時要給人的**可複製回讀指令**；其餘狀態是空字串。</param>
        static string Broadcast(string iPersona, string iSummary, string iNote, string iDataRoot,
                                bool iNoNotify, SCP_LetterWriteResult iWrite,
                                SCP_PersonaStatus? iLock, SCP_CmdResult ioResult,
                                out string oRecheckHint)
        {
            oRecheckHint = "";
            if (iNoNotify)
            {
                ioResult.Lines.Add("📢 廣播：**顯式關掉**（--arg no_notify=1）");
                return "skipped";
            }
            if (iDataRoot.Length == 0)
            {
                // 「沒給根」與「發失敗」不同形：前者是我沒要求它發，後者是它沒發成。
                ioResult.Lines.Add("📢 廣播：**沒給 data_root ⇒ 沒有發**（不是失敗，是沒要求）");
                ioResult.Lines.Add("   → 要廣播就補 `--arg data_root=<AgentCommands>`");
                return "skipped";
            }
            SCP_ITavernPostGateway? aGate = SCP_TavernPostGatewayHost.Create(iDataRoot);
            if (aGate == null)
            {
                // ⚠ 沒登記閘 ≠ 發出去了。
                ioResult.Lines.Add("📢 廣播：**本宿主沒有登記發文閘 ⇒ 這一則沒有發出去**");
                return "fail";
            }

            string aBody = "🫖 **" + iPersona + "** 小歇片刻（/compact 前）\n\n"
                           + (iSummary.Length > 0 ? "💭 **小歇心得**\n" + iSummary + "\n\n" : "")
                           + "準備壓縮對話史 —— 公開心得如上，私密細節落在 memory letter，醒來接續，**不下線**。\n"
                           + "- memory letter: `" + Path.GetFileName(iWrite.Path) + "`（私密心得在信裡）"
                           + (iNote.Length > 0 ? "\n- Note: " + iNote : "");
            var aMeta = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["tag"] = "compact-rest",
                ["category"] = "meta",
                ["letter"] = Path.GetFileName(iWrite.Path),
            };
            SCP_TavernPostVerdict aVerdict;
            try
            {
                aVerdict = aGate.Post(iPersona, aBody, aMeta, iLock?.SessionToken ?? "", ioResult.Lines);
            }
            catch (Exception e)
            {
                // 例外與「回 false」處置相同（都要補發），但**理由不同** ⇒ 印出來，不要抹平。
                ioResult.Lines.Add("📢 廣播丟出例外：" + e.GetType().Name + ": " + e.Message);
                return "fail";
            }
            // ⛔ 標籤照閘給的三態逐一對應，**不准由 `Posted` 這個 bool 推**：
            //   `Posted == false` 在 Unresolved 上也成立，而那正是「沒發」與「不知道」被壓成同一格的那一步。
            string aLabel = aVerdict.Outcome switch
            {
                SCP_TavernPostOutcome.Posted => "OK",
                SCP_TavernPostOutcome.Unresolved => "**未定**（沒等到回執）",
                _ => "fail（確定沒發）",
            };
            ioResult.Lines.Add("📢 廣播：" + aLabel + "　" + aVerdict.Detail);
            if (aVerdict.Seq.Length > 0) ioResult.AddValue("post_seq", aVerdict.Seq);
            if (aVerdict.Outcome == SCP_TavernPostOutcome.Unresolved)
            {
                oRecheckHint = aVerdict.RecheckHint;
                return "unknown";
            }
            return aVerdict.Posted ? "ok" : "fail";
        }
    }
}
