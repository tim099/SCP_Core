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
        public int Missing;                  // 差集
        public int LedgerWorkPostEntries;    // 帳上當日 work_post 總筆數（含對不上任何訊息的）
        public int LedgerRefsUnmatched;      // 帳上有 ref 而找不到對應訊息的筆數（比對關係的體檢）

        // 🔴 當日的**補償性入帳**（`payout_request` 撥款）。
        //   它們是人批過的補發，⛔ 而它們**不帶 per-message `ref`** ⇒ 本層**沖不掉**逐則的差集。
        //   ⇒ 所以不自動清零（那要逐則證明，而證據不存在），改成**擺在差集旁邊**讓看的人自己讀。
        //   🩸 少了這一格的代價很具體：2026-09-22 的 114 則已經用 6 張請款單補過了，
        //     而稽核會**每天對那一天亮燈** —— 一面永遠亮紅燈的儀表，人會在第三天學會忽略它。
        public int CompensationEntries;
        public int CompensationTokens;
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

            string aComp = CompensationTokens > 0
                ? "　（⚠ 當日另有請款撥款 **" + CompensationTokens + "** token／"
                  + CompensationEntries + " 筆 —— ⛔ 那些不帶逐則 ref，本層**沖不掉**差集，只並排給你看）"
                : "";
            return aMark + "（" + DayKey + "）：訊息 " + Messages
                 + " ／ 應計酬 " + Candidates + " ／ 帳上 " + Paid
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

            // ① 帳上那一天的 work_post ref
            var aPaidRefs = new HashSet<string>(StringComparer.Ordinal);
            string aBankRoot = Path.Combine(iDataRoot, BankDirName);
            foreach (SCP_BankEntry e in SCP_BankClosing.EnumerateDay(aBankRoot, iDayKey, r.Problems))
            {
                if (string.Equals(e.Kind, WorkPostKind, StringComparison.Ordinal))
                {
                    r.LedgerWorkPostEntries++;
                    if (e.Ref.Length > 0) aPaidRefs.Add(e.Ref);
                    continue;
                }
                // 補償性入帳：只算 credit（`payout_request` 同時會有央行那一腳 debit，
                // ⛔ 兩腳都算會把金額算成 0 —— 而 0 看起來像「沒有補過」）。
                if (string.Equals(e.Kind, CompensationKind, StringComparison.Ordinal)
                    && e.Type == SCP_BankEntryType.Credit)
                {
                    r.CompensationEntries++;
                    r.CompensationTokens += e.Amount;
                }
            }

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

                    r.Missing++;
                    Bump(r.MissingByPersona, aPersona);
                    Bump(r.MissingByCategory, aCategory);
                    Bump(r.MissingByRoom, aRoom);
                }
            }

            foreach (string aRef in aPaidRefs)
                if (!aSeenRefs.Contains(aRef)) r.LedgerRefsUnmatched++;

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
                r.Why = "應計酬 " + r.Candidates + " 則全部在帳上";
                return;
            }
            if (r.Paid == 0 && r.Candidates >= AlarmFloor)
            {
                r.Verdict = SCP_PayrollVerdict.Alarm;
                r.Why = "應計酬 " + r.Candidates + " 則而帳上 **0 筆** ⇒ 這不是零星漏發，是那條路整條斷了";
                return;
            }
            r.Verdict = SCP_PayrollVerdict.Warn;
            r.Why = "應計酬 " + r.Candidates + " 則，帳上 " + r.Paid + " ⇒ 差 " + r.Missing
                  + "（⚠ 本層讀不到 category 計不計酬的設定 ⇒ 按 category 分組列在下面）"
                  + (r.DayKey == TransitionDayKey
                     ? "　⚠ **這天是權威切換的過渡日**：當天有一部分錢寫在已刪除的舊帳本上 ⇒ 差集**偏高**，⛔ 別照數字補。"
                     : "");
        }

        static void Bump(Dictionary<string, int> iMap, string iKey)
        {
            iMap.TryGetValue(iKey, out int n);
            iMap[iKey] = n + 1;
        }
    }
}
