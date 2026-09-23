// 區塊職責：**發文領薪的差集稽核** —— 「今天有幾則訊息」對上「帳上有幾筆 `work_post`」（TASK-0273 ⑥）。
// 物理意義：一則計酬的發文 ⇒ 帳本應該有一筆 `kind=work_post`、`ref=<房間>#seq=<seq>` 的分錄。
//          兩邊各數一次、相減，差就是**可能沒領到的那些則**。
// 數值影響：**純讀**。⛔ 一毛錢都不動、一個檔都不寫。
//
// 🩸 為什麼要有這一層（2026-09-22，TASK-0273）：
//   那一天整天 **0 筆** `work_post` 落帳，而酒館有 **135 則**訊息，
//   每一筆發文都回報 `announce = Posted` —— **沒有任何一層喊**。
//   發現它的是 @kaguya 自己去翻 `Bank/ledger` 逐日數檔案。
//
// ⭐ 判準：**量差集，⛔ 不偵測成因。**
//   同一天量到那個外觀有**三種**成因，而三者在畫面上完全相同
//   （發文回 `Posted`、錢沒動、只有 Editor Console 一行）：
//     ① 呼叫端手填了 Cmd 端已經拒收的參數（`bank_root`）
//     ② persona 解析不到正式帳號 —— ⚠ **那是刻意的**，不是病
//     ③ Server 與 CLI 的 build 不符（`delegate_failure = build_mismatch`）
//   ⇒ 偵測成因的守衛，明天會被第四種成因繞過去。差集不會 —— 它量的是**結果**。
//
// ⚠ 射程（本層量不到的，⛔ 不假裝量得到）：
//   · **category 計不計酬的規則在 Unity 的 routing 設定裡**，本層讀不到
//     ⇒ 分不出「這一類本來就不計酬」與「這一類漏發了」。
//     ⇒ 所以差集**按 category 分組印出來**，讓看的人自己判斷；
//       而「某一類整天全缺」與「全部都缺」長得不一樣 —— 後者才是警報。
//   · persona 解析不到的那些則**先扣掉**（那是②，合法跳過）——
//     扣得掉的前提是本層拿得到 `lettersRoot` 與 `region`；拿不到就說「未扣」，⛔ 不當成 0。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Bank
{
    public enum SCP_PayrollVerdict
    {
        /// <summary>沒有樣本可量（那一天沒有訊息／沒有房間）—— ⛔ 不是「沒問題」。</summary>
        NoSample,
        /// <summary>量不動（比對關係本身壞了）—— ⛔ 不是「全部漏發」。</summary>
        Unmeasurable,
        Clean,
        /// <summary>有缺口但不是全斷。</summary>
        Warn,
        /// <summary>🔴 系統性斷掉：有一堆訊息而帳上一筆都沒有。</summary>
        Alarm,
    }

    public sealed class SCP_PayrollAuditResult
    {
        public string DayKey = "";
        public int Messages;                 // 該日全部訊息
        public int WithoutPersona;           // 沒有 sender_persona ⇒ 結構上不計酬
        public int Unresolvable;             // 有 persona 但解析不到帳號 ⇒ 成因② 合法跳過
        public bool ResolverAvailable;       // 拿不到 lettersRoot/region 時＝false ⇒ Unresolvable 未扣
        public int Candidates;               // 應該要領到的那些則
        public int Paid;                     // 帳上有對應 ref 的

        // 🔴 **走第二條路結清的**（請款補發，逐則 ref 記在 `payroll_settled.json`）。
        //   ⛔ 刻意**不併進 `Paid`**：兩者的憑據強度不同 —— `Paid` 背後是一筆帶 ref 的分錄，
        //     這一格背後只有一份清單。壓成同一個數字就是把「這筆錢是怎麼付的」丟掉，
        //     而那正是下一次有人來查這一天時要問的第一件事。
        public int Settled;
        // ⚠ 清單讀不動時為 true —— **它不是 0**。讀不到就等於「不知道哪些已經結清過」，
        //   而靜默當成空集合的樣子，跟「真的沒有人結清過」逐字相同。
        public bool SettledUnreadable;
        public int Missing;                  // 差集（⛔ 已扣掉 Settled）
        public int LedgerWorkPostEntries;    // 帳上當日 work_post 總筆數（含對不上任何訊息的）
        public int LedgerRefsUnmatched;      // 帳上有 ref 而找不到對應訊息的筆數（比對關係的體檢）

        // 🔴 當日的**補償性入帳**（`payout_request` 撥款）。
        //   它們是人批過的補發，⛔ 而它們**不帶 per-message `ref`** ⇒ 本層**沖不掉**逐則的差集。
        //   ⇒ 所以不自動清零（那要逐則證明，而證據不存在），改成**擺在差集旁邊**讓看的人自己讀。
        //   🩸 少了這一格的代價很具體：2026-09-22 的 114 則已經用 6 張請款單補過了，
        //     而稽核會**每天對那一天亮燈** —— 一面永遠亮紅燈的儀表，人會在第三天學會忽略它。
        public int CompensationEntries;
        public int CompensationTokens;

        // 🔴 上面那一格的**子集**：當天的撥款裡，單子自稱是「補發文領薪」的那幾筆
        //   （請款單的 `source_kind = work_post_backfill`）。
        //   ⚠ 為什麼要分出來（TASK-0290）：`payout_request` 是**通用**的撥款 kind ——
        //     同一天可能有活體驗收、增發測試、補薪，三者在帳上長得一模一樣。
        //   ⇒ 拿「當天有撥款」當警示條件，會對著一堆跟領薪無關的撥款亮燈；
        //     而亮錯的燈跟不亮一樣沒有用（人第三天就學會忽略它）。
        public int BackfillEntries;
        public int BackfillTokens;
        // ⚠ 當天有撥款、而**分類不出來**（單據夾不在／讀不動）。
        //   ⛔ 它不是「都不是補薪」—— 那是把「不知道」寫成「零」，本層最不准做的那件事。
        public bool BackfillUnclassified;
        // 🔴 「有補薪撥款 ＋ 還有差集」⇒ 那幾則**可能已經被那批錢付掉了，而清單沒登記**。
        //   ⛔ 本層**不替它沖銷**（逐則憑據只在 `payroll_settled.json`，而本層拿不到）⇒ 只出聲。
        public bool BackfillUnsettled;
        public bool RoomsRootMissing;
        public SCP_PayrollVerdict Verdict = SCP_PayrollVerdict.NoSample;
        public string Why = "";
        public readonly Dictionary<string, int> MissingByPersona = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<string, int> MissingByCategory = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly Dictionary<string, int> MissingByRoom = new Dictionary<string, int>(StringComparer.Ordinal);
        public readonly List<string> Problems = new List<string>();

        /// <summary>一行摘要（brief／晚安對帳貼這一行）。</summary>
        public string HeadLine()
        {
            string aMark = Verdict switch
            {
                SCP_PayrollVerdict.Alarm => "🔴 **領薪整天沒落帳**",
                SCP_PayrollVerdict.Warn => "⚠ 領薪有缺口",
                SCP_PayrollVerdict.Clean => "✓ 領薪對得上",
                SCP_PayrollVerdict.Unmeasurable => "⚠ **量不動**（⛔ 不是零）",
                _ => "・沒有樣本可量（⛔ 不是「沒問題」）",
            };
            // ⛔ 量不動的時候**不印數字** —— `訊息 0 ／ 帳上 0 ／ 差 0` 是我憑空填的，
            //   而它跟「那天真的沒有人說話」長得一模一樣。缺讀數與零是兩件事。
            if (Verdict == SCP_PayrollVerdict.Unmeasurable && Messages == 0 && Candidates == 0)
                return "⚠ **量不動**（" + DayKey + "）：**沒有數字** —— ⛔ 不是 0";

            // ⚠ 這一段的指路**要跟著「結清」那一欄的存在與否走**。
            //   🩸 2026-09-23 量到的（TASK-0290 ⑥）：`Settled == 0` 時結清欄不印，
            //     而這裡照樣寫「見『結清』那一欄」⇒ **它指向一個畫面上不存在的東西**。
            //     活體：`--arg day=2026-09-21`（撥款 2 token／2 筆、結清 0）。
            //   ⇒ 兩段的條件本來不同源（一個看 `CompensationTokens`、一個看 `Settled`），
            //     而「指路」這件事的正確條件是**後者**。
            string aComp = CompensationTokens > 0
                ? "　（⚠ 當日另有請款撥款 **" + CompensationTokens + "** token／"
                  + CompensationEntries + " 筆 —— ⚠ 金額只並排給你看"
                  + (Settled > 0
                     ? "；**逐則沖銷走 `" + SCP_PayrollAudit.SettledFileName + "`**，見「結清」那一欄）"
                     : "；本日**沒有任何一則走清單結清** ⇒ ⛔ 別把這筆金額讀成「那些則已經付過了」）")
                : "";
            // ⛔ 結清 0 筆時不印那一欄（別讓每一天都多一個 0）——
            //   ⚠ 而「讀不動」一定要印：它跟 0 在數字上同形，差別只在這一行字。
            string aSettled = Settled > 0 ? " ／ 結清 " + Settled : "";
            // ⚠ 措辭刻意中性：**「沒讀到」涵蓋「不存在」與「讀壞了」兩種**，而本行說不出是哪一種
            //   ⇒ 斷定原因的那句話寫在 `Problems`（那裡才有讀數）。⛔ 這一行不准替它猜。
            if (SettledUnreadable)
                aSettled += " ／ ⚠ **已結清清單沒讀到 ⇒ 差集偏高**（原因見問題欄）";
            return aMark + "（" + DayKey + "）：訊息 " + Messages
                 + " ／ 應計酬 " + Candidates + " ／ 帳上 " + Paid + aSettled
                 + " ／ **差 " + Missing + "**" + aComp;
        }
    }

    public static class SCP_PayrollAudit
    {
        public const string WorkPostKind = "work_post";

        /// <summary>補償性入帳的 kind —— 請款核准撥款。⚠ 它不只用於補薪，所以本層只「擺在旁邊」，⛔ 不沖銷。</summary>
        public const string CompensationKind = "payout_request";

        /// <summary>
        /// 🔴 **本層量得到的最早一天。** 金流權威 2026-09-18 才切到 `Bank/`；在那之前錢寫在舊
        /// `Treasury/ledger`，而那本帳已於 2026-09-22 刪除（TASK-0274，歷史留在 git）。
        /// ⇒ 更早的日子本層**查無帳**，而「查無帳」與「整天沒發薪」在數字上**一模一樣**。
        /// 🩸 血證就在加這道閘之前的那一次實跑：09-14／15／16 被判成
        /// 🔴「領薪整天沒落帳」共 768 則 —— **那三天其實好好的**，紅的是我的尺。
        /// ⇒ 一個對著自己量不到的區間尖叫的稽核，會在第三天被所有人關掉。
        /// ⚠ 取 09-17 而不是 09-18：09-17 當天 `Bank/` 已經在收（實測 261 筆），
        ///   它是過渡日 —— 照算，但報告要說它的差集偏高。
        /// </summary>
        public const string MeasurableFromDayKey = "2026-09-17";
        public const string TransitionDayKey = "2026-09-17";
        public const string RoomsRelPath = "ChatTavern/rooms";
        public const string BankDirName = "Bank";

        /// <summary>
        /// 🔴 **第二條合法的完成路徑**：走請款補發的那些則。請款分錄不帶逐則 `ref`
        /// ⇒ 它們另外記在這份清單裡（`<Bank>/payroll_settled.json`，`settled[].refs`）。
        /// ⚠ 檔名與結構**必須與 Unity 那側的 `UCL_TavernPostRewardBackfill.SettledFileName` 逐字相同** ——
        ///   那支補款工具讀它是為了「不要再付一次」，本層讀它是為了「不要再報一次」。
        /// 🩸 而這一格的由來要寫死：本檔原本**知道有這 114 則、也知道它們付過了**
        ///   （`CompensationEntries` 那段註解逐字寫著「稽核會每天對那一天亮燈…人會在第三天學會忽略它」），
        ///   而當時的判斷是「要逐則證明，而**證據不存在**」⇒ 選擇只把金額擺在旁邊。
        ///   ⇒ 那句話**寫下時為真**：這份清單是後來才長出來的。而**沒有任何一層會回來更新它** ——
        ///   於是一個寫對了的定語，變成了一盞每天對著已結清的帳尖叫的燈。
        /// </summary>
        public const string SettledFileName = "payroll_settled.json";

        /// <summary>
        /// 🔴 `ref` 的形狀 —— 與 Unity 那側的 `Cmd_Tavern.PostRewardSourceRef` **必須逐字相同**。
        /// ⛔ 這是本層唯一一個「第二份真相源」，而它守不住（那支在另一棵樹上，本層碰不到）。
        /// ⇒ 所以底下有一道體檢：**帳上有 work_post 卻沒有一筆 ref 對得上任何訊息** ⇒ 判 `Unmeasurable`，
        ///   ⛔ 不報「全部漏發」。🩸 少了那道，一次格式變更會讓本稽核每天尖叫，
        ///   而人會在第三天學會忽略它 —— 那比沒有稽核更糟。
        /// </summary>
        public static string SourceRef(string iRoomId, int iSeq) => iRoomId + "#seq=" + iSeq;

        /// <summary>
        /// 系統性斷掉的門檻：**有這麼多則應計酬而帳上一筆都沒有** ⇒ 🔴。
        /// ⚠ 取 5 不是因為 5 有意義，是因為「1~2 則沒領到」可能只是我沒想到的合法跳過，
        ///   而**連續 5 則以上全都沒有**已經不是巧合。⛔ 這個數字沒有讀數支持，是判斷。
        /// </summary>
        public const int AlarmFloor = 5;

        public static string TodayUtcKey() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        /// <summary>
        /// 量某一個 UTC 日。
        /// <paramref name="iLettersRoot"/>／<paramref name="iRegion"/> 給了才扣得掉「解析不到帳號」那些則；
        /// ⛔ 沒給就**說它沒扣**，不假裝扣過。
        /// </summary>
        public static SCP_PayrollAuditResult Audit(string iDataRoot, string iDayKey,
                                                   string? iLettersRoot = null, string? iRegion = null)
        {
            var r = new SCP_PayrollAuditResult { DayKey = iDayKey };
            r.ResolverAvailable = !string.IsNullOrWhiteSpace(iLettersRoot) && !string.IsNullOrWhiteSpace(iRegion);

            // 🔴 先擋射程外的日子 —— ⛔ 那些日子的「0 筆」是**查無帳**，不是沒發薪。
            if (string.CompareOrdinal(iDayKey, MeasurableFromDayKey) < 0)
            {
                r.Verdict = SCP_PayrollVerdict.Unmeasurable;
                r.Why = "早於 " + MeasurableFromDayKey + " ⇒ 那一天的帳在舊 `Treasury/ledger`，"
                      + "而那本已於 2026-09-22 刪除（TASK-0274，歷史在 git）⇒ **本層查無帳**。"
                      + " ⛔ 這不是「整天沒發薪」—— 兩者的數字一模一樣，而處置相反。";
                return r;
            }

            // ① 帳上的 work_post ref —— **那一天與它之後的每一天**。
            // 🩸 2026-09-22 血證：此前只掃「那一天」，而**補款的分錄一律寫在跑補款的那一天**
            //   ⇒ 我補完 09-18／09-21 共 250 筆（每筆都帶正確的 `ref`），分錄全落在 09-22 那一夾，
            //     而稽核照樣報「09-18 差 14／09-21 差 235」。
            //   ⛔ 那個紅燈是**假的**，而它最危險的地方是它叫人「再補一次」——
            //     照著它補就是把同一批錢發第二次。
            // ⇒ 判準改成：**分錄不會早於它補的那一則訊息**，所以只往後掃。
            //   ⚠ 往後掃的代價是 O(今天 − 那一天)；量測用的日子通常很近，可接受。
            //   ⛔ 不掃「之前」的日子：那不會有答案，只會多讀一堆檔。
            var aPaidRefs = new HashSet<string>(StringComparer.Ordinal);
            // 當天撥款的 `請款單號 → 金額`。⚠ 用 Dictionary 不是 HashSet：同一張單只會撥一次，
            //   而金額要留著印在警示裡（「有一批 114 token 是在補這一天」比「有一批」有用得多）。
            var aCompRequestIds = new Dictionary<string, int>(StringComparer.Ordinal);
            string aBankRoot = Path.Combine(iDataRoot, BankDirName);
            foreach (string aLedgerDay in SCP_BankClosing.LedgerDayKeys(aBankRoot))
            {
                if (string.CompareOrdinal(aLedgerDay, iDayKey) < 0) continue;
                bool aIsSameDay = string.Equals(aLedgerDay, iDayKey, StringComparison.Ordinal);
                foreach (SCP_BankEntry e in SCP_BankClosing.EnumerateDay(aBankRoot, aLedgerDay, r.Problems))
                {
                if (string.Equals(e.Kind, WorkPostKind, StringComparison.Ordinal))
                {
                    // ⚠ `LedgerWorkPostEntries` 仍只數**當天**那一夾 —— 它是「那天的帳長什麼樣」的讀數，
                    //   把後來補的算進去會讓它跟 `Paid` 混成同一個意思。
                    if (aIsSameDay) r.LedgerWorkPostEntries++;
                    if (e.Ref.Length > 0) aPaidRefs.Add(e.Ref);
                    continue;
                }
                if (!aIsSameDay) continue;   // 補償性入帳只看當天（它本來就是「那天被補了多少」）
                // 補償性入帳：只算 credit（`payout_request` 同時會有央行那一腳 debit，
                // ⛔ 兩腳都算會把金額算成 0 —— 而 0 看起來像「沒有補過」）。
                if (string.Equals(e.Kind, CompensationKind, StringComparison.Ordinal)
                    && e.Type == SCP_BankEntryType.Credit)
                {
                    r.CompensationEntries++;
                    r.CompensationTokens += e.Amount;
                    // ⚠ 撥款分錄的 `ref` 是**請款單號**（`payout/<id>` 的那個 id），
                    //   ⛔ 不是逐則訊息的 `tavern#seq=N` —— 兩種 ref 今天在同一本帳裡並存而長得很像。
                    //   ⇒ 留著它，等一下拿去單據那邊問「這筆錢在補什麼」。
                    if (e.Ref.Length > 0) aCompRequestIds[e.Ref] = e.Amount;
                }
                }
            }

            // ①-bis 走**第二條路**結清的 refs（請款補發 ⇒ `payroll_settled.json`）。
            // 🩸 少了這一段的代價是實測出來的：2026-09-22 那天 114 則全部在這份清單裡，
            //   而本層每天把它們報成「⚠ 領薪有缺口」—— 每一個人的早安 brief 都會看到。
            //   ⇒ 而在那一行上，**真缺口與已結清逐字同形**。
            var aSettledRefs = new HashSet<string>(StringComparer.Ordinal);
            string aSettledPath = Path.Combine(aBankRoot, SettledFileName);
            if (File.Exists(aSettledPath))
            {
                try
                {
                    SCP_JsonData aSettledDoc = SCP_JsonParser.Parse(File.ReadAllText(aSettledPath));
                    SCP_JsonData aBatches = aSettledDoc["settled"];
                    if (!aBatches.IsArray)
                    {
                        // ⚠ 檔在、而形狀不是我以為的那個 ⇒ 那**不是**「沒有結清過」。
                        r.SettledUnreadable = true;
                        r.Problems.Add(SettledFileName + "：`settled` 不是陣列 ⇒ 本次讀不到任何已結清 ref");
                    }
                    else
                    {
                        for (int bi = 0; bi < aBatches.Count; bi++)
                        {
                            SCP_JsonData aRefs = aBatches[bi]["refs"];
                            if (!aRefs.IsArray) continue;
                            for (int ri = 0; ri < aRefs.Count; ri++)
                            {
                                string aOne = aRefs[ri].AsString();
                                if (aOne.Length > 0) aSettledRefs.Add(aOne);
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    // ⛔ 讀不動**要出聲**：靜默當空集合＝退回本次要修的那個誤報，
                    //   而誤報的樣子跟正常運作一模一樣。
                    r.SettledUnreadable = true;
                    r.Problems.Add(SettledFileName + "：" + ex.GetType().Name + ": " + ex.Message
                                 + " ⇒ 已結清的那些則**沖不掉** ⇒ 差集偏高（⛔ 不是漏發）");
                }
            }

            // 🔴 **檔不存在**：那在一棵從沒用過請款補發的樹上是合法的，所以預設不出聲。
            //   ⚠ 而有一種情況它不合法：**當天有請款撥款，卻沒有那份逐則清單** ——
            //     那正是「錢用第二條路付掉了，而我讀不到它付了哪幾則」。
            //   ⇒ 這一格在差集算完之後判（要先知道 Missing），寫在 `Verdict` 前面那一步。
            //   ⛔ 不在這裡直接判「檔不在＝出事」：那會讓每一棵乾淨的樹每天多一行假警告。
            bool aSettledFileAbsent = !File.Exists(aSettledPath);

            // ② 那一天的訊息
            string aRooms = Path.Combine(iDataRoot, RoomsRelPath.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(aRooms))
            {
                r.RoomsRootMissing = true;
                r.Verdict = SCP_PayrollVerdict.Unmeasurable;
                r.Why = "找不到房間根：" + aRooms + "（⛔ 那不是「今天沒有人說話」）";
                return r;
            }

            var aSeenRefs = new HashSet<string>(StringComparer.Ordinal);
            foreach (string aRoomDir in Directory.GetDirectories(aRooms))
            {
                string aRoom = Path.GetFileName(aRoomDir);
                string aDayDir = Path.Combine(aRoomDir, "messages", iDayKey);
                if (!Directory.Exists(aDayDir)) continue;

                string[] aFiles = Directory.GetFiles(aDayDir, "*.json");
                Array.Sort(aFiles, StringComparer.Ordinal);
                foreach (string aFile in aFiles)
                {
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(aFile),
                                      NumberStyles.Integer, CultureInfo.InvariantCulture, out int aSeq))
                    { r.Problems.Add(aFile + "：檔名不是 seq"); continue; }

                    r.Messages++;
                    string aRef = SourceRef(aRoom, aSeq);
                    aSeenRefs.Add(aRef);

                    string aPersona = "", aCategory = "(unset)";
                    try
                    {
                        SCP_JsonData d = SCP_JsonParser.Parse(File.ReadAllText(aFile));
                        aPersona = d.GetString("sender_persona", "").Trim();
                        SCP_JsonData aMeta = d["meta"];
                        if (aMeta.Exists && !aMeta.IsNull)
                        {
                            string c = aMeta.GetString("category", "").Trim();
                            if (c.Length > 0) aCategory = c;
                        }
                    }
                    catch (Exception ex)
                    {
                        // ⚠ 讀不動的訊息**不算候選** —— 把它算進差集等於拿一個讀不到的東西當漏發。
                        r.Problems.Add(aFile + "：" + ex.GetType().Name + ": " + ex.Message);
                        continue;
                    }

                    if (aPersona.Length == 0) { r.WithoutPersona++; continue; }

                    if (r.ResolverAvailable)
                    {
                        // ⚠ 回空字串＝解析不到（那支自己這樣定義）。成因②：刻意不計酬，⛔ 不算漏發。
                        string aAcc = SCP_BankAccountResolver.ResolvePersonaAccount(
                            iLettersRoot!, iDataRoot, iRegion!, aPersona, out _);
                        if (aAcc.Length == 0) { r.Unresolvable++; continue; }
                    }

                    r.Candidates++;
                    if (aPaidRefs.Contains(aRef)) { r.Paid++; continue; }
                    // ⚠ 順序刻意：**帶 ref 的分錄優先** —— 一則若兩邊都有，那是「付過又被列進清單」，
                    //   要算在憑據比較強的那一格，⛔ 不是兩邊各加一次（那會讓 Paid+Settled > Candidates）。
                    if (aSettledRefs.Contains(aRef)) { r.Settled++; continue; }

                    r.Missing++;
                    Bump(r.MissingByPersona, aPersona);
                    Bump(r.MissingByCategory, aCategory);
                    Bump(r.MissingByRoom, aRoom);
                }
            }

            foreach (string aRef in aPaidRefs)
                if (!aSeenRefs.Contains(aRef)) r.LedgerRefsUnmatched++;

            // 🔴 「當天有請款撥款、有差集、而那份逐則清單不在」⇒ 錢走了第二條路而我讀不到它付了哪幾則。
            //   ⛔ 這一格**不是**「檔不在就叫」（那會讓乾淨的樹每天多一行假警告），
            //     三個條件同時成立才成立，而三個都是讀數。
            if (aSettledFileAbsent && r.CompensationEntries > 0 && r.Missing > 0)
            {
                r.SettledUnreadable = true;
                r.Problems.Add("當天有 " + r.CompensationEntries + " 筆請款撥款（" + r.CompensationTokens
                             + " token）而 `" + SettledFileName + "` **不存在** ⇒ 那些錢補了哪幾則**無法逐則沖銷**"
                             + " ⇒ 差集偏高，⛔ 別照這個數字補（補款工具在同一個狀態下會把它們當漏發）");
            }

            // 🔴 **TASK-0290**：上面那一格只擋「檔不在」。而它擋不到更常見的那一種 ——
            //   **檔在，只是那一批沒有被登記進去**。
            //   🩸 成因是結構性的：`payroll_settled.json` 全庫**只有讀者、沒有寫入端**
            //     （2026-09-23 逐檔量：LY 3 支 ＋ Senate 2 支，全是讀），那份清單是人手寫的。
            //     ⇒ 所以「有人補了薪，而沒有人去寫那份清單」不是假想，那是預設會發生的事。
            //   ⚠ 判準刻意用**單子自稱的 `source_kind`**，⛔ 不是「當天有沒有 payout_request」：
            //     後者會對活體驗收、增發測試那些跟領薪無關的撥款亮燈。
            //   ⛔ 而本層**只出聲、不沖銷** —— 逐則的憑據只在那份清單裡，`source_kind` 說不出是哪幾則。
            if (aCompRequestIds.Count > 0)
            {
                Dictionary<string, string> aKinds = SCP_TreasuryRequests.LoadPayoutSourceKindsByDay(
                    iDataRoot, iDayKey, out bool aReqDirMissing, r.Problems);
                foreach (var aPair in aCompRequestIds)
                {
                    // ⚠ 單子找不到 ≠ 那筆不是補薪。兩種「查不到」都落進 Unclassified，⛔ 不當成 0。
                    if (!aKinds.TryGetValue(aPair.Key, out string? aKind)) { r.BackfillUnclassified = true; continue; }
                    if (!string.Equals(aKind, SCP_TreasuryRequests.SourceKindWorkPostBackfill, StringComparison.Ordinal))
                        continue;
                    r.BackfillEntries++;
                    r.BackfillTokens += aPair.Value;
                }
                if (aReqDirMissing) r.BackfillUnclassified = true;

                // ⚠ 旗標在這裡立，**話寫在 `Why`** —— 理由見 Verdict() 裡那一段註解
                //   （問題欄的標題寫死是「有 N 個檔讀不動」，而本格不是檔讀不動）。
                if (r.BackfillEntries > 0 && r.Missing > 0 && !aSettledFileAbsent)
                    r.BackfillUnsettled = true;
                // ⚠ 沒有差集的那天，「分類不出來」不影響任何判斷 ⇒ 不出聲。
                //   ⛔ 這不是把它藏起來：一個每天都亮而且亮了也不用做事的燈，會把旁邊真的燈一起關掉。
                r.BackfillUnclassified = r.BackfillUnclassified && r.Missing > 0;
            }

            Verdict(r);
            return r;
        }

        static void Verdict(SCP_PayrollAuditResult r)
        {
            // 🔴 體檢先跑：**比對關係壞掉**要跟「全部漏發」分開，它們的數字一模一樣。
            if (r.LedgerWorkPostEntries > 0 && r.Paid == 0 && r.LedgerRefsUnmatched == r.LedgerWorkPostEntries)
            {
                r.Verdict = SCP_PayrollVerdict.Unmeasurable;
                r.Why = "帳上有 " + r.LedgerWorkPostEntries + " 筆 work_post，而**沒有一筆 ref 對得上任何訊息**"
                      + " ⇒ 這是 `ref` 形狀對不上（比對關係壞了），⛔ 不是漏發。"
                      + " 去對 `SCP_PayrollAudit.SourceRef` 與 Unity 那側的 `Cmd_Tavern.PostRewardSourceRef`。";
                return;
            }
            if (r.Messages == 0)
            {
                r.Verdict = SCP_PayrollVerdict.NoSample;
                r.Why = "那一天沒有任何訊息（⛔ 那不等於「領薪沒問題」—— 它只是沒有樣本）";
                return;
            }
            if (r.Candidates == 0)
            {
                r.Verdict = SCP_PayrollVerdict.NoSample;
                r.Why = "有 " + r.Messages + " 則訊息，而**沒有一則是應計酬的**"
                      + (r.ResolverAvailable ? "" : "（⚠ 本次沒帶 letters_root/region ⇒ 解析不到的那些則**沒被扣掉**）");
                return;
            }
            if (r.Missing == 0)
            {
                r.Verdict = SCP_PayrollVerdict.Clean;
                // ⛔ 綠燈要說出它是**怎麼**綠的：「帳上逐筆有分錄」與「其中一部分靠一份清單結清」
                //   是兩種不同強度的綠，而它們在 `差 0` 這個數字上同形。
                r.Why = r.Settled > 0
                    ? "應計酬 " + r.Candidates + " 則全部有著落：帳上 " + r.Paid
                      + " 則帶逐則分錄／**" + r.Settled + " 則走請款結清**（憑據是 `"
                      + SettledFileName + "` 的 ref 清單，⛔ 不是分錄）"
                    : "應計酬 " + r.Candidates + " 則全部在帳上";
                return;
            }
            if (r.Paid == 0 && r.Candidates >= AlarmFloor)
            {
                r.Verdict = SCP_PayrollVerdict.Alarm;
                r.Why = "應計酬 " + r.Candidates + " 則而帳上 **0 筆** ⇒ 這不是零星漏發，是那條路整條斷了";
                return;
            }
            r.Verdict = SCP_PayrollVerdict.Warn;
            r.Why = "應計酬 " + r.Candidates + " 則，帳上 " + r.Paid
                  + (r.Settled > 0 ? "（另有 " + r.Settled + " 則走請款結清，已扣掉）" : "")
                  + " ⇒ 差 " + r.Missing
                  + "（⚠ 本層讀不到 category 計不計酬的設定 ⇒ 按 category 分組列在下面）"
                  + (r.SettledUnreadable
                     ? "　⚠ **`" + SettledFileName + "` 這次沒讀到**（不存在／讀壞了 —— 哪一種見問題欄）"
                       + " ⇒ 走請款結清的那些則沖不掉，差集**偏高**，⛔ 別照這個數字補。"
                     : "")
                  + (r.DayKey == TransitionDayKey
                     ? "　⚠ **這天是權威切換的過渡日**：當天有一部分錢寫在已刪除的舊帳本上 ⇒ 差集**偏高**，⛔ 別照數字補。"
                     : "")
                  // 🔴 TASK-0290：清單**在**、而那批補薪沒被登記進去。
                  //   ⚠ 這一句刻意寫在 `Why` 不寫在 `Problems` —— 問題欄的標題寫死是
                  //     「有 N 個檔讀不動」（`Runtime/Cmd`），而本格不是檔讀不動，是**帳對不起來**。
                  //     ⛔ 塞進去會讓那個標題說謊，而說謊的標題比沒有標題更貴。
                  //   📌 那行標題本身也該收（既有的「檔不存在」守衛同樣被它蓋著）——
                  //     ⛔ 不在本單改：2026-09-23 量到 `Runtime/Cmd` 在別人的施工場裡。
                  + (r.BackfillUnsettled
                     ? "　🔴 **當天有 " + r.BackfillEntries + " 筆補薪撥款**（" + r.BackfillTokens
                       + " token）而清單**在、卻沒登記到它們** ⇒ 這 " + r.Missing
                       + " 則**可能已經付過了**。⛔ 別照這個數字補 —— 先去對那批撥款補了哪幾則。"
                     : "")
                  + (r.BackfillUnclassified
                     ? "　⚠ **當天的撥款分類不出來**（讀不到對應請款單）⇒ 本層答不出其中有沒有補薪。"
                       + "⛔ 這是「不知道」，不是「都不是補薪」。"
                     : "");
        }

        static void Bump(Dictionary<string, int> iMap, string iKey)
        {
            iMap.TryGetValue(iKey, out int n);
            iMap[iKey] = n + 1;
        }
    }
}
