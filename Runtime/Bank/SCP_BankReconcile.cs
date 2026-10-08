// 區塊職責：**動錢對帳器** —— 「事件存在 ∧ 帳上沒有」的事實差集，涵蓋帳上**每一種** kind（TASK-0245）。
// 物理意義：每一種會動錢的事件都有一個**獨立於帳本**的事實源（訊息檔／觀影結算紀錄／請款單／轉帳單）。
//          從事實源推出「帳上應該有哪幾筆」，對照帳本實際有的，差出來的就是**該有而沒有**的那些。
// 數值影響：`Run` **零寫入**（連讀數檔都不寫 —— 那是 `RecordRun` 的事，由呼叫端決定要不要落）。
//
// ⭐ **發文薪資補款的唯一入口**（TASK-0332）。apply 前有兩道閘：
//   結清清單讀不動 ⇒ 拒絕／射程內帳上 0 筆 work_post 而酒館推得出應有 ⇒ 拒絕（分不出沒發過與讀不到帳）。
// ⭐ 兩條命脈（⛔ 兩條都要保住）：
//   ① **判準與發放路徑同源**：酒館那一類逐則問 `SCP_TavernPayroll.Plan()`（寫入端發薪真正跑的純函式），
//      ⛔ 不自己抄規則 —— 抄了之後對出來的是「對帳作者以為當時會付的」，帳看起來平、只是平在錯的基準上。
//   ② **不按失敗原因分類，算差集**：同一個「沒付到」有很多種成因（build 不符／Server 換手／逾時不重排…），
//      偵測成因的守衛明天會被下一種繞過去；差集量的是**結果**。
//
// 🔴 比對鍵是 `(type, kind, ref)`，⛔ **不是冪等鍵**：
//   🩸 2026-09-28 開工前量過 —— 0296 之前的舊分錄 commit 52 筆、reading_note 3 筆，
//     冪等鍵格式跟 `Plan()` 產的**全不中**，而 ref **全中**（commit 的 ref 是 SHA、其餘是 `room#seq`）。
//     ⇒ 拿冪等鍵比，上線第一天就是 55 筆假缺口；照它補＝付第二次錢。
//   ⚠ 補發時仍帶 `Plan()` 的冪等鍵 ⇒ 銀行那道 `FindByIdempotencyKey` 是第二層保險，⛔ 不是唯一一層。
//
// ⚠ 射程（覆蓋表每一列都要說得出自己屬於哪一種，⛔ 沒列到的 kind 會被印成「未分類」而不是被跳過）：
//   · covered   —— 有獨立事實源、本支算差集
//   · external  —— 別的機械已在對它（保管費：`demurrage op=parity` 從餘額重算），本支不重做
//   · no_event  —— 分錄**本身就是事件**（開戶、遷移更正、QA 測試），沒有第二份事實可對 ⇒ 無差集可算
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace SCP.Core.Bank
{
    public enum SCP_ReconcileCoverageKind { Covered, External, NoEvent }

    /// <summary>覆蓋表的一列：一種 kind 屬於哪一類、從哪裡對、對了幾筆。</summary>
    public sealed class SCP_ReconcileCoverage
    {
        public string Kind = "";
        public SCP_ReconcileCoverageKind Coverage;
        public string Source = "";           // 事實源（人讀）
        public int Expected;                 // 事實源推出「帳上應有」的筆數
        public int Matched;                  // 帳上找得到的
        public int Settled;                  // 走第二條路結清的（請款補發清單）
        public int Missing;                  // 差集
        public int Unmeasurable;             // 判準讀不了／帳號解析不到 ⇒ 量不到（⛔ 不是缺口，也不是沒事）
        public string Note = "";
    }

    /// <summary>差集裡的一筆：事件在、帳上沒有。</summary>
    public sealed class SCP_ReconcileGap
    {
        public string Source = "";           // tavern / stream_watch / payout_request / transfer_request
        public string Day = "";              // 事件那一天（UTC）
        public string Kind = "";
        public SCP_BankEntryType Type;
        public string Account = "";
        public int Amount;
        public string Ref = "";
        public string Detail = "";
        /// <summary>只有酒館那一類有（可自動補發 —— 它就是發放路當時會送的那一筆）。其餘為 null ⇒ 只報不補。</summary>
        public SCP_TavernPayItem? Item;
        public string Key => SCP_BankReconcile.KeyOf(Type, Kind, Ref);
    }

    public sealed class SCP_ReconcileResult
    {
        public string From = "";
        public string To = "";
        public int LedgerEntriesScanned;
        public int TavernMessagesScanned;
        public readonly List<SCP_ReconcileCoverage> Coverage = new List<SCP_ReconcileCoverage>();
        public readonly List<SCP_ReconcileGap> Gaps = new List<SCP_ReconcileGap>();
        /// <summary>帳上出現、而覆蓋表沒列的 kind ⇒ ⚠ 新的動錢路徑長出來了而對帳器不知道。</summary>
        public readonly Dictionary<string, int> UnclassifiedKinds = new Dictionary<string, int>(StringComparer.Ordinal);
        /// <summary>ref 對上了、金額不一樣（⛔ 不算缺口，但要看）。</summary>
        public readonly List<string> AmountMismatches = new List<string>();
        public readonly List<string> Problems = new List<string>();
        public int MissingTotal => Gaps.Count;

        // ── 補發前的兩道閘要看的讀數（TASK-0332）──
        /// <summary>`payroll_settled.json` 讀不動 ⇒ 走第二條路結清的那些則會被報成缺口；**apply 必須拒絕**（補下去就是付第二次）。</summary>
        public bool SettledUnreadable;
        /// <summary>射程內帳上讀到的 `work_post` 筆數。0 而酒館卻推得出應有 ⇒ 分不出「真的沒發過」與「讀不到帳」；**apply 必須拒絕**。</summary>
        public int LedgerWorkPostScanned;
    }

    public static class SCP_BankReconcile
    {
        /// <summary>本支量得到的最早一天：金流權威 2026-09-17 切到 `Bank/`，那天是過渡日（一部分錢在已刪除的舊帳本）⇒ 從 09-18 起。</summary>
        public const string MeasurableFromDayKey = "2026-09-18";
        /// <summary>請款補薪的逐則結清清單；補發前用來排除已付過的訊息。</summary>
        public const string SettledFileName = "payroll_settled.json";

        /// <summary>讀已結清 ref；缺檔為空集合，讀取失敗明示未知，補發端必須拒絕。</summary>
        public static HashSet<string> ReadSettledRefs(string iBankRoot, List<string> ioProblems, out bool oUnreadable)
        {
            oUnreadable = false;
            var aSettledRefs = new HashSet<string>(StringComparer.Ordinal);
            string aSettledPath = Path.Combine(iBankRoot, SettledFileName);
            if (!File.Exists(aSettledPath)) return aSettledRefs;
            try
            {
                SCP_JsonData aSettledDoc = SCP_JsonParser.Parse(File.ReadAllText(aSettledPath));
                SCP_JsonData aBatches = aSettledDoc["settled"];
                if (!aBatches.IsArray)
                {
                    // ⚠ 檔在、而形狀不是我以為的那個 ⇒ 那**不是**「沒有結清過」。
                    oUnreadable = true;
                    ioProblems.Add(SettledFileName + "：`settled` 不是陣列 ⇒ 本次讀不到任何已結清 ref");
                    return aSettledRefs;
                }
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
            catch (Exception ex)
            {
                oUnreadable = true;
                ioProblems.Add(SettledFileName + "：" + ex.GetType().Name + ": " + ex.Message
                             + " ⇒ 已結清的那些則**沖不掉** ⇒ 差集偏高（⛔ 不是漏發）");
            }
            return aSettledRefs;
        }

        public const string RunDirName = "reconcile";
        public const string LastRunFileName = "last_run.json";
        public const string RunLogFileName = "runs.jsonl";

        static readonly string[] s_TavernKinds =
        {
            SCP_TavernPayroll.KindWorkPost, SCP_TavernPayroll.KindCommit,
            SCP_TavernPayroll.KindReadingNote, SCP_TavernPayroll.KindTokenParse,
        };
        public const string KindStreamWatch = "stream_watch";
        public const string KindPayoutRequest = "payout_request";
        public const string KindManualTransfer = "manual_transfer";
        static readonly string[] s_ExternalKinds = { "overnight_storage_fee", "overnight_storage_fee_deposit" };
        static readonly string[] s_NoEventKinds = { "opening_balance", "migration_correction", "qa_test" };

        public static string KeyOf(SCP_BankEntryType iType, string iKind, string iRef)
            => (iType == SCP_BankEntryType.Credit ? "credit" : "debit") + "|" + iKind + "|" + iRef;

        public static string BankRootOf(string iDataRoot) => SCP_BankRegion.BankRootOfDataRoot(iDataRoot);   // ⛔ 不自己拼（TASK-0390）

        /// <summary>
        /// 對 [<paramref name="iFrom"/>, <paramref name="iTo"/>]（UTC 日，含頭尾）跑一次差集。**零寫入。**
        /// <para>⚠ 早於 <see cref="MeasurableFromDayKey"/> 的日子會被收窄掉並寫進 Problems —— ⛔ 那些天是查無帳，不是沒缺口。</para>
        /// </summary>
        public static SCP_ReconcileResult Run(string iDataRoot, string iFrom, string iTo)
        {
            var r = new SCP_ReconcileResult { From = iFrom, To = iTo };
            if (string.CompareOrdinal(r.From, MeasurableFromDayKey) < 0)
            {
                r.Problems.Add("起日 " + r.From + " 早於 " + MeasurableFromDayKey + " ⇒ 收窄到 " + MeasurableFromDayKey
                               + "（之前的帳在已刪除的舊 Treasury/ledger，⛔ 查無帳 ≠ 沒缺口）");
                r.From = MeasurableFromDayKey;
            }
            string aBank = BankRootOf(iDataRoot);

            // ① 帳本索引：from 之後的每一天（分錄不會早於它對應的事件，但可能晚 —— 補款、核准都在事後）。
            var aLedger = new Dictionary<string, int>(StringComparer.Ordinal);      // key → 金額合計
            var aKindsSeen = new Dictionary<string, int>(StringComparer.Ordinal);   // [from,to] 內出現的 kind
            foreach (string aDay in SCP_BankClosing.LedgerDayKeys(aBank))
            {
                if (string.CompareOrdinal(aDay, r.From) < 0) continue;
                bool aInRange = string.CompareOrdinal(aDay, r.To) <= 0;
                foreach (SCP_BankEntry e in SCP_BankClosing.EnumerateDay(aBank, aDay, r.Problems))
                {
                    r.LedgerEntriesScanned++;
                    if (string.Equals(e.Kind, SCP_TavernPayroll.KindWorkPost, StringComparison.Ordinal)) r.LedgerWorkPostScanned++;
                    string k = KeyOf(e.Type, e.Kind, e.Ref);
                    aLedger.TryGetValue(k, out int aSum);
                    aLedger[k] = aSum + e.Amount;
                    if (aInRange) { aKindsSeen.TryGetValue(e.Kind, out int n); aKindsSeen[e.Kind] = n + 1; }
                }
            }

            var aCov = new Dictionary<string, SCP_ReconcileCoverage>(StringComparer.Ordinal);
            SCP_ReconcileCoverage Cov(string iKind, SCP_ReconcileCoverageKind iC, string iSource)
            {
                if (!aCov.TryGetValue(iKind, out SCP_ReconcileCoverage? c))
                {
                    c = new SCP_ReconcileCoverage { Kind = iKind, Coverage = iC, Source = iSource };
                    aCov[iKind] = c;
                }
                return c;
            }
            foreach (string k in s_TavernKinds) Cov(k, SCP_ReconcileCoverageKind.Covered, "酒館訊息 × SCP_TavernPayroll.Plan()");
            Cov(KindStreamWatch, SCP_ReconcileCoverageKind.Covered, "StreamWatch/sessions_log.jsonl（結算列）");
            Cov(KindPayoutRequest, SCP_ReconcileCoverageKind.Covered, "Bank/requests（approved）");
            Cov(KindManualTransfer, SCP_ReconcileCoverageKind.Covered, "Bank/transfer_requests（approved，out／in 兩腳）");
            foreach (string k in s_ExternalKinds)
                Cov(k, SCP_ReconcileCoverageKind.External, "—").Note = "由 `demurrage op=parity` 從餘額重算對拍（本支不重做）";
            foreach (string k in s_NoEventKinds)
                Cov(k, SCP_ReconcileCoverageKind.NoEvent, "—").Note = "分錄本身即事件（沒有第二份事實可對）";

            var aGapKeys = new HashSet<string>(StringComparer.Ordinal);
            void Expect(SCP_ReconcileCoverage c, SCP_ReconcileGap g, bool iSettled, int iExpectedAmount)
            {
                c.Expected++;
                string k = g.Key;
                if (aLedger.TryGetValue(k, out int aGot))
                {
                    c.Matched++;
                    if (iExpectedAmount > 0 && aGot != iExpectedAmount)
                        r.AmountMismatches.Add(k + "：應 " + iExpectedAmount + "、帳上 " + aGot + "（" + g.Detail + "）");
                    return;
                }
                if (iSettled) { c.Settled++; return; }
                // 同一個 ref 被兩則事件推出來（例：同一個 SHA 的 commit 公告發了兩次）⇒ 只算一次缺口
                if (!aGapKeys.Add(k)) { c.Expected--; return; }
                c.Missing++;
                r.Gaps.Add(g);
            }

            ScanTavern(iDataRoot, aBank, r, Cov, Expect);
            ScanStreamWatch(iDataRoot, r, Cov(KindStreamWatch, SCP_ReconcileCoverageKind.Covered, ""), Expect);
            ScanRequests(aBank, r, Cov(KindPayoutRequest, SCP_ReconcileCoverageKind.Covered, ""), Expect);
            ScanTransfers(aBank, r, Cov, Expect);

            foreach (KeyValuePair<string, int> kv in aKindsSeen)
                if (!aCov.ContainsKey(kv.Key)) r.UnclassifiedKinds[kv.Key] = kv.Value;

            var aOrder = new List<string>(s_TavernKinds)
                { KindStreamWatch, KindPayoutRequest, KindManualTransfer };
            foreach (string k in aCov.Keys) if (!aOrder.Contains(k)) aOrder.Add(k);
            foreach (string k in aOrder) r.Coverage.Add(aCov[k]);
            return r;
        }

        static IEnumerable<string> Days(string iFrom, string iTo)
        {
            if (!DateTime.TryParseExact(iFrom, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime a)
                || !DateTime.TryParseExact(iTo, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime b))
                yield break;
            for (DateTime d = a; d <= b; d = d.AddDays(1)) yield return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        static bool InRange(string iDay, string iFrom, string iTo)
            => iDay.Length == 10 && string.CompareOrdinal(iDay, iFrom) >= 0 && string.CompareOrdinal(iDay, iTo) <= 0;

        // ── 酒館：逐則問 Plan()，它規劃的每一項都是「帳上應有」──────────────────
        static void ScanTavern(string iDataRoot, string iBank, SCP_ReconcileResult r,
            Func<string, SCP_ReconcileCoverageKind, string, SCP_ReconcileCoverage> iCov,
            Action<SCP_ReconcileCoverage, SCP_ReconcileGap, bool, int> iExpect)
        {
            string aRooms = SCP_DataPaths.Rooms(new SCP_DataRoot(iDataRoot));
            if (!Directory.Exists(aRooms)) { r.Problems.Add("找不到房間根：" + aRooms + " ⇒ 酒館那一類**沒量**（⛔ 不是沒缺口）"); return; }
            HashSet<string> aSettled = ReadSettledRefs(iBank, r.Problems, out bool aUnreadable);
            r.SettledUnreadable = aUnreadable;
            if (aUnreadable) r.Problems.Add("請款結清清單讀不動 ⇒ work_post 走第二條路結清的那些則會被報成缺口（差集偏高；apply 會拒絕）");

            foreach (string aRoomDir in Directory.GetDirectories(aRooms))
            {
                string aRoom = Path.GetFileName(aRoomDir);
                foreach (string aDay in Days(r.From, r.To))
                {
                    string aDayDir = Path.Combine(aRoomDir, "messages", aDay);
                    if (!Directory.Exists(aDayDir)) continue;
                    foreach (string aFile in Directory.GetFiles(aDayDir, "*.json"))
                    {
                        if (!int.TryParse(Path.GetFileNameWithoutExtension(aFile), NumberStyles.Integer,
                                          CultureInfo.InvariantCulture, out int aSeq)) continue;
                        r.TavernMessagesScanned++;
                        SCP_TavernPayPlan aPlan;
                        try
                        {
                            SCP_JsonData d = SCP_JsonParser.Parse(File.ReadAllText(aFile));
                            SCP_TavernMessage aMsg = SCP_TavernRead.FromJson(d, aRoom, aSeq, aFile);
                            aPlan = SCP_TavernPayroll.Plan(iDataRoot, SCP_TavernPayInput.From(aMsg, aRoom, aSeq));
                        }
                        catch (Exception e) { r.Problems.Add(aFile + "：" + e.GetType().Name + ": " + e.Message); continue; }

                        // 判準讀不了／帳號解析不到 ⇒ 那一則的底薪**量不到**（⛔ 不當缺口，也不當沒事）
                        foreach (string w in aPlan.Warnings)
                            if (w.StartsWith("A：", StringComparison.Ordinal))
                                iCov(SCP_TavernPayroll.KindWorkPost, SCP_ReconcileCoverageKind.Covered, "").Unmeasurable++;

                        foreach (SCP_TavernPayItem it in aPlan.Items)
                        {
                            var c = iCov(it.Kind, SCP_ReconcileCoverageKind.Covered, "酒館訊息 × SCP_TavernPayroll.Plan()");
                            var g = new SCP_ReconcileGap
                            {
                                Source = "tavern", Day = aDay, Kind = it.Kind, Account = it.Account, Amount = it.Amount,
                                Type = it.Direction == SCP_TavernPayDirection.Credit ? SCP_BankEntryType.Credit : SCP_BankEntryType.Debit,
                                Ref = it.Ref, Detail = SCP_TavernPayroll.Describe(it), Item = it,
                            };
                            bool aIsSettled = it.Kind == SCP_TavernPayroll.KindWorkPost && aSettled.Contains(it.Ref);
                            iExpect(c, g, aIsSettled, it.Amount);
                        }
                    }
                }
            }
        }

        // ── 觀影結算：pay_status=paid/failed 且 paid_total>0 的那一列 ⇒ 帳上要有 `streamwatch-<session>` ──
        static void ScanStreamWatch(string iDataRoot, SCP_ReconcileResult r, SCP_ReconcileCoverage c,
            Action<SCP_ReconcileCoverage, SCP_ReconcileGap, bool, int> iExpect)
        {
            string aLog = Path.Combine(iDataRoot, "StreamWatch", "sessions_log.jsonl");
            if (!File.Exists(aLog)) { c.Note = "結算紀錄不存在（" + aLog + "）⇒ 本類沒量"; return; }
            int aSkipped = 0;
            foreach (string aLine in File.ReadAllLines(aLog))
            {
                if (aLine.Trim().Length == 0) continue;
                SCP_JsonData d;
                try { d = SCP_JsonParser.Parse(aLine); }
                catch (Exception e) { r.Problems.Add("sessions_log.jsonl 一列讀不動：" + e.Message); continue; }
                if (d.GetString("record_type", "") == "export") continue;
                string aSettledAt = d.GetString("settled_at", "");
                string aDay = aSettledAt.Length >= 10 ? aSettledAt.Substring(0, 10) : "";
                if (!InRange(aDay, r.From, r.To)) continue;
                int aTotal = d.GetInt("paid_total", 0);
                string aStatus = d.GetString("pay_status", "");
                if (aTotal <= 0) continue;
                // phantom／unresolved-account ＝結算當下就判定不付（跟酒館的「解析不到帳號」同一種合法跳過）
                if (aStatus != "paid" && aStatus != "failed") { aSkipped++; continue; }
                string aSid = d.GetString("session_id", "");
                iExpect(c, new SCP_ReconcileGap
                {
                    Source = "stream_watch", Day = aDay, Kind = KindStreamWatch, Type = SCP_BankEntryType.Credit,
                    Account = d.GetString("persona", ""), Amount = aTotal, Ref = "streamwatch-" + aSid,
                    Detail = "觀影 " + aSid + "（pay_status=" + aStatus + "）",
                }, false, aTotal);
            }
            if (aSkipped > 0) c.Note = "結算當下判不付（phantom／unresolved-account）" + aSkipped + " 列不列入";
        }

        // ── 請款：approved 的單 ⇒ 帳上要有 credit payout_request ref=<單號> ──────────────
        static void ScanRequests(string iBank, SCP_ReconcileResult r, SCP_ReconcileCoverage c,
            Action<SCP_ReconcileCoverage, SCP_ReconcileGap, bool, int> iExpect)
        {
            foreach (SCP_JsonData d in ReadDecided(Path.Combine(iBank, "requests"), "*__request.json", r))
            {
                string aDay = DayOf(d.GetString("decided_at", ""));
                if (!InRange(aDay, r.From, r.To)) continue;
                string aId = d.GetString("request_id", "");
                iExpect(c, new SCP_ReconcileGap
                {
                    Source = "payout_request", Day = aDay, Kind = KindPayoutRequest, Type = SCP_BankEntryType.Credit,
                    Account = d.GetString("target_bank", ""), Amount = d.GetInt("amount", 0), Ref = aId,
                    Detail = "請款 " + aId + " → " + d.GetString("target_bank", "") + "（已核准）",
                }, false, d.GetInt("amount", 0));
            }
        }

        // ── 轉帳：approved 的單 ⇒ 帳上要有 out（debit）與 in（credit）兩腳，ref=<單號> ──────────
        static void ScanTransfers(string iBank, SCP_ReconcileResult r,
            Func<string, SCP_ReconcileCoverageKind, string, SCP_ReconcileCoverage> iCov,
            Action<SCP_ReconcileCoverage, SCP_ReconcileGap, bool, int> iExpect)
        {
            foreach (SCP_JsonData d in ReadDecided(Path.Combine(iBank, "transfer_requests"), "*__transfer.json", r))
            {
                string aDay = DayOf(d.GetString("decided_at", ""));
                if (!InRange(aDay, r.From, r.To)) continue;
                string aId = d.GetString("request_id", "");
                string aKind = d.GetString("kind", KindManualTransfer);
                var c = iCov(aKind, SCP_ReconcileCoverageKind.Covered, "Bank/transfer_requests（approved，out／in 兩腳）");
                int aAmt = d.GetInt("amount", 0);
                iExpect(c, new SCP_ReconcileGap
                {
                    Source = "transfer_request", Day = aDay, Kind = aKind, Type = SCP_BankEntryType.Debit,
                    Account = d.GetString("from_bank", ""), Amount = aAmt, Ref = aId, Detail = "轉帳 " + aId + " out 腳",
                }, false, aAmt);
                iExpect(c, new SCP_ReconcileGap
                {
                    Source = "transfer_request", Day = aDay, Kind = aKind, Type = SCP_BankEntryType.Credit,
                    Account = d.GetString("to_bank", ""), Amount = aAmt, Ref = aId, Detail = "轉帳 " + aId + " in 腳",
                }, false, aAmt);
            }
        }

        static string DayOf(string iIso) => iIso.Length >= 10 ? iIso.Substring(0, 10) : "";

        static IEnumerable<SCP_JsonData> ReadDecided(string iDir, string iPattern, SCP_ReconcileResult r)
        {
            if (!Directory.Exists(iDir)) yield break;
            foreach (string aFile in Directory.GetFiles(iDir, iPattern, SearchOption.AllDirectories))
            {
                SCP_JsonData? d = null;
                try { d = SCP_JsonParser.Parse(File.ReadAllText(aFile)); }
                catch (Exception e) { r.Problems.Add(aFile + "：" + e.GetType().Name + ": " + e.Message); }
                if (d != null && d.GetString("status", "") == "approved") yield return d;
            }
        }

        // ── 執行紀錄（驗收 ⑧：「沒被讀」要有可觀察的樣子）──────────────────────────
        // ⭐ 每跑一次落兩份：`last_run.json`（覆寫，給 brief／status 讀「上次什麼時候、什麼射程」）
        //    ＋ `runs.jsonl`（append，歷史）。⇒ 三天沒人跑的樣子是「last_run 停在三天前」，⛔ 不是「沒有缺口」。

        /// <summary>覆蓋類別的名字（表格與紀錄共用 ⇒ ⛔ 同一個東西不准有兩個拼法）。</summary>
        public static string CoverageName(SCP_ReconcileCoverageKind iC) => iC switch
        {
            SCP_ReconcileCoverageKind.Covered => "covered",
            SCP_ReconcileCoverageKind.External => "external",
            _ => "no_event",
        };

        public static string RunDir(string iDataRoot) => Path.Combine(BankRootOf(iDataRoot), RunDirName);

        public static SCP_JsonData ToRecord(SCP_ReconcileResult r, string iTrigger, string iHost, int iApplied, int iApplyFailed)
        {
            SCP_JsonData o = SCP_JsonData.NewObject();
            o["at_utc"] = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            o["trigger"] = iTrigger;
            o["host"] = iHost;
            o["from"] = r.From;
            o["to"] = r.To;
            o["ledger_entries_scanned"] = r.LedgerEntriesScanned;
            o["tavern_messages_scanned"] = r.TavernMessagesScanned;
            o["missing"] = r.MissingTotal;
            o["applied"] = iApplied;
            o["apply_failed"] = iApplyFailed;
            o["amount_mismatches"] = r.AmountMismatches.Count;
            o["problems"] = r.Problems.Count;
            SCP_JsonData aCov = SCP_JsonData.NewObject();
            foreach (SCP_ReconcileCoverage c in r.Coverage)
            {
                SCP_JsonData x = SCP_JsonData.NewObject();
                x["coverage"] = CoverageName(c.Coverage);
                x["expected"] = c.Expected; x["matched"] = c.Matched; x["settled"] = c.Settled;
                x["missing"] = c.Missing; x["unmeasurable"] = c.Unmeasurable;
                aCov[c.Kind] = x;
            }
            o["coverage"] = aCov;
            SCP_JsonData aUn = SCP_JsonData.NewObject();
            foreach (KeyValuePair<string, int> kv in r.UnclassifiedKinds) aUn[kv.Key] = kv.Value;
            o["unclassified_kinds"] = aUn;
            return o;
        }

        /// <summary>落執行紀錄。回傳 last_run.json 路徑。⚠ 失敗會丟例外 —— 紀錄寫不進去要讓呼叫端知道（⛔ 不靜默）。</summary>
        public static string RecordRun(string iDataRoot, SCP_JsonData iRecord)
        {
            string aDir = RunDir(iDataRoot);
            Directory.CreateDirectory(aDir);
            string aLast = Path.Combine(aDir, LastRunFileName);
            SCP_TextFile.WriteCrLf(aLast, SCP_JsonWriter.Write(iRecord, true) + "\n");
            File.AppendAllText(Path.Combine(aDir, RunLogFileName), SCP_JsonWriter.Write(iRecord, false) + "\n");
            return aLast;
        }

        /// <summary>讀上次的執行紀錄。沒有 ⇒ null（＝**從來沒跑過**，⛔ 不是「帳是平的」）。</summary>
        public static SCP_JsonData? ReadLastRun(string iDataRoot, out string? oError)
        {
            oError = null;
            string aLast = Path.Combine(RunDir(iDataRoot), LastRunFileName);
            if (!File.Exists(aLast)) return null;
            try { return SCP_JsonParser.Parse(File.ReadAllText(aLast)); }
            catch (Exception e) { oError = e.GetType().Name + ": " + e.Message; return null; }
        }

        /// <summary>預設射程：最近 N 天（含今天，UTC），但不早於 <see cref="MeasurableFromDayKey"/>。</summary>
        public static (string From, string To) DefaultWindow(int iDays)
        {
            DateTime aToday = DateTime.UtcNow.Date;
            string aTo = aToday.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string aFrom = aToday.AddDays(-(Math.Max(1, iDays) - 1)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.CompareOrdinal(aFrom, MeasurableFromDayKey) < 0) aFrom = MeasurableFromDayKey;
            return (aFrom, aTo);
        }

        /// <summary>
        /// 每日觸發（驗收 ③）：今天（UTC）還沒跑過 ⇒ 跑一次**唯讀**差集並落紀錄；跑過 ⇒ 直接回上次那份。
        /// <para>⚠ 掛在早安 brief（每個人每天必經）—— 第一個醒來的人付那次掃描的時間，其他人讀紀錄。
        /// ⛔ 只報不補：自動補發錢是另一個決定（`cmd bank-reconcile --arg op=apply --arg confirm=1`）。</para>
        /// </summary>
        public static SCP_JsonData? EnsureDaily(string iDataRoot, string iHost, int iWindowDays, out bool oRanNow, out string? oError)
        {
            oRanNow = false;
            SCP_JsonData? aLast = ReadLastRun(iDataRoot, out oError);
            string aToday = DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (aLast != null && aLast.GetString("at_utc", "").StartsWith(aToday, StringComparison.Ordinal)) return aLast;
            (string aFrom, string aTo) = DefaultWindow(iWindowDays);
            SCP_ReconcileResult r = Run(iDataRoot, aFrom, aTo);
            SCP_JsonData aRec = ToRecord(r, "daily", iHost, 0, 0);
            RecordRun(iDataRoot, aRec);
            oRanNow = true;
            oError = null;
            return aRec;
        }
    }
}
