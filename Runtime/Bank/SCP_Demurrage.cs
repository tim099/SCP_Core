// 區塊職責：**跨日存款保管費**的計算與落帳（TASK-0278 從 `UCL_BartenderDaemon` 整段搬過來）。
// 物理意義：超過門檻的部分，跨日時收保管費。例：balance=1100、門檻 1000、費率 5%
//           → excess=100 → fee=5（`floor(100 × 0.05)`）。扣下來的錢**不蒸發**，等額 credit 進央行。
// 數值影響：`Apply` 會真的動錢（debit 繳費者 ＋ credit 央行）；`Plan` 與 `iDryRun=true` **零寫入**。
//
// ⚠ **本層不管「今天是不是跨日」，也不推進任何 state** —— 那是觸發端的事（Unity 那側的 daemon tick）。
//   ⇒ 本層拿到一個日期就照那個日期算，重跑由 `idem_key` 擋。
//   📌 TASK-0278 ⑧ 的射程就落在這一句：搬的是**扣繳**，⛔ 不含「誰來觸發」。
//
// 🩸 判準（每一條底下都有一次它咬過人的紀錄，出處在 `UCL_BartenderDaemon` 的原註解與 git 史）：
//   ① **費率的算式逐字照搬 `floor(excess × permille/1000.0)`。**
//      ⛔ 不「順手」改成整數運算 —— 那是**行為改動**偽裝成清理：
//      搬家單的處置範圍 ≤ 它開單時的症狀，而四捨五入換掉之後，
//      「搬對了」與「搬的時候順手改了數字」在帳面上逐字同形（TASK-0278 ② 的對拍正是為了擋這個）。
//   ② **央行豁免要先結算、先快照。** 豁免帳戶的餘額若在扣費迴圈中途才讀，
//      廣播裡那三個數字（結算前／本次入庫／結算後）**加不起來** ——
//      數字沒有錯，錯的是它沒有時點，而對不起來的帳看久了會被當成雜訊。
//   ③ **debit 與 credit 的冪等鍵分開。** 共用一把鑰匙時，debit 成功而 credit 失敗
//      ⇒ 那筆錢「從使用者扣走了而沒進央行」，且**再也補不回來**（永久漏水且無聲）。
//      分開之後，下一輪原樣再送一次：送過的是 no-op，沒送過的才真的補上。
//   ④ **「這次動了多少」用 `Duplicate` 旗標判，⛔ 不用我算出來的 fee。**
//      冪等命中時錢一毛沒動，而拿 fee 去印 before/after 會印出一段**根本沒發生**的餘額變化
//      —— 那是一句每一欄都合法的假話（2026-09-18 實測，同一把 key 送兩次都印 `146 → 151`）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace SCP.Core.Bank
{
    /// <summary>一個帳戶在這一輪裡的處置類別。</summary>
    public enum SCP_DemurrageBillRowKind
    {
        /// <summary>豁免（央行自己）—— 不收費，但**要列出來**：它是一條 audit 聲明，不是雜訊。</summary>
        Exempt,
        /// <summary>餘額 ≤ 0 —— 純雜訊帳號，連列都不列。</summary>
        Noise,
        /// <summary>餘額 ≤ 門檻 —— 安全。</summary>
        BelowThreshold,
        /// <summary>有超額，而 `floor` 取整之後費用是 0 —— 免費，⛔ 不產生 0 元 entry。</summary>
        FloorZero,
        /// <summary>要收費。</summary>
        Charge,
    }

    public sealed class SCP_DemurrageBillRow
    {
        public string AccountId = "";
        public int BalanceBefore;
        public int Excess;
        /// <summary>**算出來**的費用（⛔ 不是實際扣掉的 —— 那一格在 <see cref="SCP_DemurrageCharge"/>）。</summary>
        public int Fee;
        public SCP_DemurrageBillRowKind Kind;
        /// <summary>
        /// 這筆錢**真正會扣在哪個帳戶**（帳號歸一之後）。
        /// <para>🩸 它跟 <see cref="AccountId"/> 常常不同，而那不是 bug 是既有行為：
        /// 餘額表的 key 是帳本上的 `account_id`，而寫入端會再把它歸一一次
        /// （`sirius` → `Spectre`，實測 2026-09-20 那一輪）。⛔ 不要拿 `AccountId` 去對帳本 ——
        /// 那會把「歸一到別人身上」讀成「算錯了」。</para>
        /// </summary>
        public string ChargeAccountId = "";
        /// <summary>歸一的出處（`r.Trace`）—— 空＝沒換。</summary>
        public string ResolveTrace = "";
    }

    /// <summary>實際落帳的結果 —— 一個帳戶一筆。</summary>
    public sealed class SCP_DemurrageCharge
    {
        /// <summary>餘額表上的 key（＝算費用用的那一格）。</summary>
        public string AccountId = "";
        /// <summary>錢**真的**扣在哪個帳戶（歸一後）。⚠ 對帳本要用這一格。</summary>
        public string ChargeAccountId = "";
        public int BalanceBefore;
        public int Excess;
        public int PlannedFee;
        /// <summary>這一次**真的**動了多少（冪等命中＝0）。</summary>
        public int Moved;
        /// <summary>debit 是冪等命中（今天已經扣過了）。</summary>
        public bool Duplicate;
        /// <summary>入庫那一腳成功了沒（含冪等命中 —— 那也算「錢在央行」）。</summary>
        public bool Deposited;
        public string Problem = "";
    }

    /// <summary>零寫入的一份帳單 —— 誰要繳、繳多少、誰被豁免。</summary>
    public sealed class SCP_DemurrageBill
    {
        public string Date = "";
        public string CentralBank = "";
        public string CentralBankDisplayName = "";
        public int Threshold;
        public int FeePermille;
        public bool ExemptCentral;
        public string FeeRateDisplay = "";
        public readonly List<SCP_DemurrageBillRow> Rows = new List<SCP_DemurrageBillRow>();
        public readonly List<string> Problems = new List<string>();

        public IEnumerable<SCP_DemurrageBillRow> Charges
        {
            get { foreach (SCP_DemurrageBillRow r in Rows) if (r.Kind == SCP_DemurrageBillRowKind.Charge) yield return r; }
        }

        /// <summary>帳單上的費用總額（⛔ 不是實收 —— 實收要等 <see cref="SCP_DemurrageOutcome.TotalMoved"/>）。</summary>
        public int PlannedTotal
        {
            get { int s = 0; foreach (SCP_DemurrageBillRow r in Charges) s += r.Fee; return s; }
        }
    }

    public sealed class SCP_DemurrageOutcome
    {
        public SCP_DemurrageBill Plan = new SCP_DemurrageBill();
        public bool DryRun;
        public readonly List<SCP_DemurrageCharge> Charges = new List<SCP_DemurrageCharge>();
        public readonly List<string> Problems = new List<string>();
        /// <summary>本輪實收（＝各帳戶 `Moved` 之和）。</summary>
        public int TotalMoved;
        /// <summary>本輪央行實收（⚠ 與 <see cref="TotalMoved"/> 不符時要在廣播裡說出來）。</summary>
        public int CentralIncome;
        /// <summary>央行餘額（結算後）—— 讀不到時是 -1，而廣播要說它讀不到，⛔ 不印 0。</summary>
        public int CentralBalanceAfter = -1;
        public string BroadcastBody = "";
        public string Subtag = "";
        public readonly Dictionary<string, string> BroadcastMeta = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public static class SCP_Demurrage
    {
        public const string FeeKind = "overnight_storage_fee";
        public const string DepositKind = "overnight_storage_fee_deposit";
        public const string Caller = "system";

        public static string FeeRef(string iDate, string iAccount) => $"overnight-fee-{iDate}-{iAccount}";
        public static string DepositRef(string iDate, string iPayer) => $"overnight-fee-credit-{iDate}-{iPayer}";

        /// <summary>今天（**UTC**）。⚠ 曆一律 UTC —— 兩套曆並存時結帳邊界會跟檔案位置對不上。</summary>
        public static string TodayUtc() => DateTime.UtcNow.ToString("yyyy-MM-dd");

        // ===========================================================
        // 區塊職責：把一份餘額快照算成一張帳單。**純函式：不讀檔（政策除外）、不寫檔。**
        // 物理意義：餘額快照由呼叫端給 ⇒ 對拍（TASK-0278 ②）才有辦法拿**歷史上那一天**的
        //          餘額重跑一次，而不必偽造一次扣款。
        // ⚠ 排序用 `OrdinalIgnoreCase` 之外的一切都照舊：帳號字典序，跟舊實作同一把尺。
        // ===========================================================
        public static SCP_DemurrageBill BuildPlan(string iDataRoot, string iBankRoot, string iLettersRoot,
                                                  string iDate, IReadOnlyDictionary<string, int> iBalances)
        {
            var aPlan = new SCP_DemurrageBill { Date = iDate };

            SCP_BankPolicy.Reading aPolicy = SCP_BankPolicy.Read(iDataRoot, out string? aWhy);
            if (!string.IsNullOrEmpty(aWhy)) aPlan.Problems.Add(aWhy!);
            aPlan.CentralBank = aPolicy.CentralBank;
            aPlan.Threshold = aPolicy.Threshold;
            aPlan.FeePermille = aPolicy.FeePermille;
            aPlan.ExemptCentral = aPolicy.ExemptCentral;
            aPlan.FeeRateDisplay = aPolicy.FeeRateDisplay;
            aPlan.CentralBankDisplayName = ReadDisplayName(iBankRoot, aPolicy.CentralBank);

            // 判準②：豁免先結算、先快照 —— 此刻尚未有任何資金移動。
            var aExempt = new HashSet<string>(StringComparer.Ordinal);
            if (aPolicy.ExemptCentral && aPolicy.CentralBank.Length > 0 && iBalances.ContainsKey(aPolicy.CentralBank))
                aExempt.Add(aPolicy.CentralBank);

            var aIds = new List<string>(iBalances.Keys);
            aIds.Sort(StringComparer.Ordinal);

            // 判準①：費率換算逐字照搬舊實作 —— `permille/1000.0` 之後 `Math.Floor`。
            double aRate = aPolicy.FeePermille / 1000.0;

            // 區域（貨幣）ID —— 帳號歸一要它。讀不到就不歸一，而那一格**要出聲**（見下面迴圈）。
            string aRegion = SCP_BankRegion.Read(iDataRoot, out string? aRegionWhy);
            if (!string.IsNullOrEmpty(aRegionWhy)) aPlan.Problems.Add(aRegionWhy!);

            foreach (string aId in aIds)
            {
                int aBalance = iBalances[aId];
                var aRow = new SCP_DemurrageBillRow { AccountId = aId, BalanceBefore = aBalance };
                if (aExempt.Contains(aId))
                {
                    // ⚠ 豁免帳戶**餘額 0 也列** —— 底下那個 `<= 0 continue` 是為了濾雜訊，
                    //   而豁免是一條聲明，不是雜訊。
                    aRow.Kind = SCP_DemurrageBillRowKind.Exempt;
                    aPlan.Rows.Add(aRow);
                    continue;
                }
                if (aBalance <= 0) { aRow.Kind = SCP_DemurrageBillRowKind.Noise; aPlan.Rows.Add(aRow); continue; }
                if (aBalance <= aPolicy.Threshold)
                {
                    aRow.Kind = SCP_DemurrageBillRowKind.BelowThreshold;
                    aPlan.Rows.Add(aRow);
                    continue;
                }
                aRow.Excess = aBalance - aPolicy.Threshold;
                aRow.Fee = (int)Math.Floor(aRow.Excess * aRate);
                aRow.Kind = aRow.Fee <= 0 ? SCP_DemurrageBillRowKind.FloorZero : SCP_DemurrageBillRowKind.Charge;
                // ⚠ 歸一**只對要收費的那幾格**做（它要讀信件庫，不便宜），而且**在扣款之前** ——
                //   舊實作的歸一發生在寫入端（`UCL_TreasuryLedger.ResolveAccountOrThrow`），
                //   ⇒ 搬進 in-process 之後這一跳沒有人做，錢就會落在**另一個帳戶**上。
                //   🩸 那正是 TASK-0278 ② 的對拍第一次跑出來的那兩格（sirius／spectre）。
                if (aRow.Kind == SCP_DemurrageBillRowKind.Charge)
                {
                    aRow.ChargeAccountId = aId;
                    if (iLettersRoot.Length > 0 && aRegion.Length > 0)
                    {
                        SCP_BankResolution aRes = SCP_BankAccountResolver.Resolve(iLettersRoot, iDataRoot, aRegion, aId);
                        if (aRes.Changed) { aRow.ChargeAccountId = aRes.AccountId; aRow.ResolveTrace = aRes.Trace; }
                        // ⛔ 查無對應**照樣扣原帳號**（與舊實作同語意）：丟棄會讓一筆真的費用無聲消失，
                        //   而那比記在孤兒帳戶上更難查。只是要出聲。
                        else if (aRes.IsUnresolved)
                            aPlan.Problems.Add($"帳號 `{aId}` 查無對應（孤兒帳戶）—— 本筆仍照原帳號扣");
                    }
                    else aPlan.Problems.Add($"沒有 letters_root／region ⇒ `{aId}` **沒有做帳號歸一**"
                                            + "（⛔ 這與舊實作不同，錢可能落在不同帳戶）");
                }
                aPlan.Rows.Add(aRow);
            }
            return aPlan;
        }

        /// <summary>讀央行的顯示名。真相源是帳戶資料，⛔ 不是常數（換了央行還顯示舊名字是「看起來正常的錯」）。</summary>
        public static string ReadDisplayName(string iBankRoot, string iAccountId)
        {
            if (string.IsNullOrEmpty(iAccountId)) return "";
            SCP_BankAccount? aAcc = SCP_BankAccounts.TryLoad(iBankRoot, iAccountId, out _);
            if (aAcc != null && aAcc.DisplayName.Length > 0) return aAcc.DisplayName;
            return iAccountId;
        }

        // ===========================================================
        // 區塊職責：把帳單執行掉 —— debit 繳費者、credit 央行、組廣播。
        // 數值影響：`iDryRun=true` ⇒ **一毛錢都不動**，而且照樣組出完整的帳單與廣播
        //          （那就是 preview 與對拍走的路）。
        // ⚠ 失敗**不中斷整輪**：一個帳戶扣不動不該讓其他人今天不用繳。
        //   ⇒ 每一筆的失敗落在自己那一列的 `Problem` 上，並被 `Problems` 收走。
        // ===========================================================
        public static SCP_DemurrageOutcome Apply(string iDataRoot, string iBankRoot, string iLettersRoot,
                                                 string iDate, bool iDryRun)
        {
            var aProblems = new List<string>();
            Dictionary<string, int> aBalances;
            try { aBalances = SCP_BankLedger.GetAllBalances(iBankRoot, SCP_BankEntry.DefaultCurrency, out _, out _, aProblems); }
            catch (Exception e)
            {
                var aFail = new SCP_DemurrageOutcome { DryRun = iDryRun };
                aFail.Problems.Add($"讀不到餘額（{e.GetType().Name}: {e.Message}）⇒ **這一輪什麼都沒做**");
                return aFail;
            }
            return Apply(iDataRoot, iBankRoot, iLettersRoot, iDate, iDryRun, aBalances, aProblems);
        }

        /// <summary>同上，而餘額快照由呼叫端給（對拍用 —— 拿歷史上那一天的餘額重跑）。</summary>
        public static SCP_DemurrageOutcome Apply(string iDataRoot, string iBankRoot, string iLettersRoot,
                                                 string iDate, bool iDryRun,
                                                 IReadOnlyDictionary<string, int> iBalances,
                                                 List<string>? iProblems = null)
        {
            var aOut = new SCP_DemurrageOutcome { DryRun = iDryRun };
            if (iProblems != null) aOut.Problems.AddRange(iProblems);
            aOut.Plan = BuildPlan(iDataRoot, iBankRoot, iLettersRoot, iDate, iBalances);
            aOut.Problems.AddRange(aOut.Plan.Problems);

            foreach (SCP_DemurrageBillRow aRow in aOut.Plan.Charges)
            {
                var aCharge = new SCP_DemurrageCharge
                {
                    AccountId = aRow.AccountId,
                    ChargeAccountId = aRow.ChargeAccountId,
                    BalanceBefore = aRow.BalanceBefore,
                    Excess = aRow.Excess,
                    PlannedFee = aRow.Fee,
                };
                if (iDryRun)
                {
                    // ⚠ dry-run 的 `Moved` 填的是**帳單上的數**，而它前面永遠帶著 `DryRun` 這個定語。
                    //   ⛔ 別把 dry-run 的輸出當成「這次收了多少」——那是「這次會收多少」。
                    aCharge.Moved = aRow.Fee;
                    aOut.Charges.Add(aCharge);
                    aOut.TotalMoved += aRow.Fee;
                    aOut.CentralIncome += aRow.Fee;
                    continue;
                }

                string aFeeRef = FeeRef(iDate, aRow.AccountId);
                // ⚠ 扣在歸一後的帳戶，而 `ref`／`idem_key` 用**原始** id ——
                //   兩者不同是刻意的：鑰匙要跟舊實作逐字一樣，否則同一天重跑會再扣一次。
                SCP_BankPostResult aDebit = SCP_BankLedger.Debit(
                    iBankRoot, aRow.ChargeAccountId, aRow.Fee, FeeKind,
                    iRef: aFeeRef,
                    iDescription: $"跨日 {iDate} 存款保管費 {aOut.Plan.FeeRateDisplay}% "
                                  + $"(超過 {aOut.Plan.Threshold} 的 {aRow.Excess} × {aOut.Plan.FeeRateDisplay}% = {aRow.Fee})"
                                  + $" → 存入 {aOut.Plan.CentralBank}",
                    iCaller: Caller,
                    iIdempotencyKey: aFeeRef);

                if (!aDebit.Ok)
                {
                    aCharge.Problem = aDebit.Why;
                    aOut.Problems.Add($"@{aRow.AccountId} 扣款失敗：{aDebit.Why}");
                    aOut.Charges.Add(aCharge);
                    continue;
                }

                // 判準④：這次動了多少看 `Duplicate`，⛔ 不看我算出來的 fee。
                aCharge.Duplicate = aDebit.Duplicate;
                aCharge.Moved = aDebit.Duplicate ? 0 : aRow.Fee;
                aOut.TotalMoved += aCharge.Moved;

                // 判準③：入庫那一腳有自己的鑰匙 —— ⚠ 而它**只在這次真的扣到錢時才送**。
                //   🩸 為什麼不是「照送、讓冪等去擋」（我原本寫的那版）：舊實作的入庫金額是
                //     「回讀餘額」算的，於是同一個帳戶上的兩筆扣款會被**併成一筆 credit**，
                //     掛在後面那個繳費者的 ref 下（實測 2026-09-20：`credit-...-spectre` ＝ 42
                //     ＝ sirius 的 12 ＋ spectre 的 30，而 `credit-...-sirius` 這把鑰匙**不存在**）。
                //   ⇒ 對那些日子照送的話，那把不存在的鑰匙會通過冪等檢查、**憑空 credit 進央行**。
                //   ⛔ 所以這一格跟著 debit 走：這次沒扣到錢，就沒有錢要入庫。
                //   ⚠ 代價照實寫：debit 成功而 credit 失敗那一種「已扣未存」，
                //     下一輪**不會**自己補（舊實作也不會 —— 它的 `aMoved > 0` 是同一道門）。
                //     ⇒ 那一格由廣播裡「入庫 X 與扣費 Y 不符」那一行負責被看見。
                if (aCharge.Duplicate) { aOut.Charges.Add(aCharge); continue; }
                string aDepRef = DepositRef(iDate, aRow.AccountId);
                SCP_BankPostResult aCredit = SCP_BankLedger.Credit(
                    iBankRoot, aOut.Plan.CentralBank, aRow.Fee, DepositKind,
                    iRef: aDepRef,
                    iDescription: $"跨日 {iDate} 存款保管費入庫（繳費者 @{aRow.AccountId}）",
                    iCaller: Caller,
                    iIdempotencyKey: aDepRef);
                if (aCredit.Ok)
                {
                    aCharge.Deposited = true;
                    if (!aCredit.Duplicate) aOut.CentralIncome += aRow.Fee;
                }
                else
                {
                    aCharge.Problem = aCredit.Why;
                    aOut.Problems.Add($"@{aRow.AccountId} 的 {aRow.Fee} 扣了而**沒進央行**：{aCredit.Why}"
                                      + " —— 下一輪會原樣再送一次補上");
                }
                aOut.Charges.Add(aCharge);
            }

            if (!iDryRun)
            {
                try { aOut.CentralBalanceAfter = SCP_BankLedger.GetBalance(iBankRoot, aOut.Plan.CentralBank); }
                catch (Exception e) { aOut.Problems.Add($"央行餘額讀不到：{e.GetType().Name}: {e.Message}"); }
            }
            BuildBroadcast(aOut);
            return aOut;
        }

        // ===========================================================
        // 區塊職責：組廣播本文（Tim 2026-05-13 拍板：每次跨日檢查都要有 audit 訊息）。
        // 物理意義：這是全系統最大的一條資金流，它流去哪**不該只有 code 知道**。
        // ⚠ 「沒扣費但有餘額」那一節是 Tim 2026-05-14 特地補的**全透明**要求 ——
        //   ⛔ 不可以因為版面而拿掉（TASK-0278 ⑥ 把這一句寫成驗收）。
        // ⚠ 廣播**在這裡組、由觸發端去貼**：酒館寫入端現在是 Editor（`tavern.writer=editor`），
        //   ⇒ Server 這一側沒有資格寫酒館。⛔ 不在這裡偷開第二個酒館寫入端。
        // ===========================================================
        static void BuildBroadcast(SCP_DemurrageOutcome ioOut)
        {
            SCP_DemurrageBill aPlan = ioOut.Plan;
            var aSb = new StringBuilder();
            aSb.AppendLine($"🏦 **跨日存款保管費結算** ({aPlan.Date}) — 超過 {aPlan.Threshold} token 部分收 "
                           + $"{aPlan.FeeRateDisplay}%，全數存入 {aPlan.CentralBankDisplayName}");
            aSb.AppendLine();
            if (ioOut.DryRun)
            {
                aSb.AppendLine("> ⚠ **這是 dry-run 的帳單，⛔ 一毛錢都沒有動。**");
                aSb.AppendLine();
            }

            // 豁免段排在最前面（Tim 2026-08-04）：它是「這輪誰不參與扣費」的前提宣告，
            // 排在結果後面會讀成事後補充，而它其實是這輪的起始狀態。
            var aExempt = new List<string>();
            foreach (SCP_DemurrageBillRow r in aPlan.Rows)
                if (r.Kind == SCP_DemurrageBillRowKind.Exempt)
                    aExempt.Add($"- 🏦 @{r.AccountId}: **結算前** balance {r.BalanceBefore} "
                                + "(**央行豁免** — 對自己收費會讓 debit/credit 落在同一帳號)");
            if (aExempt.Count > 0)
            {
                aSb.AppendLine($"### 🏦 豁免帳戶 ({aExempt.Count} 個, 結算前餘額)");
                aSb.AppendLine(string.Join("\n", aExempt));
                aSb.AppendLine();
            }

            var aFeeLines = new List<string>();
            var aSafeLines = new List<string>();
            foreach (SCP_DemurrageCharge c in ioOut.Charges)
            {
                if (c.Problem.Length > 0 && c.Moved <= 0 && !c.Duplicate)
                {
                    aSafeLines.Add($"- @{c.AccountId}: balance {c.BalanceBefore} (🔴 扣款失敗 — {c.Problem})");
                    continue;
                }
                if (c.Duplicate)
                {
                    aSafeLines.Add($"- @{c.AccountId}: balance {c.BalanceBefore} "
                                   + "(今日已扣過 — 冪等命中，**這次沒有動錢**)");
                    continue;
                }
                aFeeLines.Add($"- @{c.AccountId}: balance {c.BalanceBefore} → **-{c.Moved} token** "
                              + $"(excess {c.Excess} × {aPlan.FeeRateDisplay}%)");
            }
            foreach (SCP_DemurrageBillRow r in aPlan.Rows)
            {
                if (r.Kind == SCP_DemurrageBillRowKind.BelowThreshold)
                    aSafeLines.Add($"- @{r.AccountId}: balance {r.BalanceBefore} (≤ {aPlan.Threshold}, 安全)");
                else if (r.Kind == SCP_DemurrageBillRowKind.FloorZero)
                    aSafeLines.Add($"- @{r.AccountId}: balance {r.BalanceBefore} "
                                   + $"(excess {r.Excess} × {aPlan.FeeRateDisplay}% = 0, floor 取整免費)");
            }

            if (aFeeLines.Count > 0)
            {
                aSb.AppendLine($"### 💸 扣費帳戶 ({aFeeLines.Count} 個)");
                aSb.AppendLine(string.Join("\n", aFeeLines));
                aSb.AppendLine();
                aSb.AppendLine($"累計回收: **-{ioOut.TotalMoved} token**");
                aSb.AppendLine();
                ioOut.Subtag = "overnight-deposit-fee";
            }
            else
            {
                aSb.AppendLine("### ✅ 無扣費 — 全 account 餘額皆 ≤ threshold");
                aSb.AppendLine();
                ioOut.Subtag = "overnight-deposit-fee-clean";
            }

            if (aSafeLines.Count > 0)
            {
                aSb.AppendLine($"### 🟢 安全帳戶 ({aSafeLines.Count} 個, 餘額顯示)");
                aSb.AppendLine(string.Join("\n", aSafeLines));
                aSb.AppendLine();
            }

            aSb.AppendLine($"### 🏦 {aPlan.CentralBankDisplayName}");
            aSb.AppendLine($"- 本次入庫: **+{ioOut.CentralIncome} token**");
            // 「結算後」三個字是這段能不能被對帳的關鍵：上面豁免段標了「結算前」，
            // 兩個時點都寫明，讀的人才能自己驗「結算前 ＋ 本次入庫 ＝ 結算後」。
            if (ioOut.CentralBalanceAfter >= 0)
                aSb.AppendLine($"- 央行餘額: **{ioOut.CentralBalanceAfter} token**（結算後）");
            else if (!ioOut.DryRun)
                aSb.AppendLine("- ⚠ 央行餘額**讀不到**（⛔ 不是 0 —— 那兩件事在這一行上同形）");
            if (ioOut.CentralIncome != ioOut.TotalMoved)
                aSb.AppendLine($"- ⚠ 入庫 {ioOut.CentralIncome} 與扣費 {ioOut.TotalMoved} 不符 "
                               + "— 有帳戶扣了但沒入庫，下一輪會偵測並補存");
            aSb.AppendLine();
            aSb.Append($"_保管費不再蒸發 — 集中到公庫，之後由活動再分配。{aPlan.Threshold} 以下不收費_");

            ioOut.BroadcastBody = aSb.ToString();
            ioOut.BroadcastMeta["check_date"] = aPlan.Date;
            ioOut.BroadcastMeta["total_fee"] = ioOut.TotalMoved.ToString();
            ioOut.BroadcastMeta["central_bank"] = aPlan.CentralBank;
            ioOut.BroadcastMeta["central_bank_income"] = ioOut.CentralIncome.ToString();
            ioOut.BroadcastMeta["accounts_charged"] = aFeeLines.Count.ToString();
            ioOut.BroadcastMeta["accounts_safe"] = aSafeLines.Count.ToString();
        }
    }
}
