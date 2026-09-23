// 區塊職責：**請款單**與**轉帳單**的讀取與裁決（Senate 側）。
// 物理意義：單據住在新帳本底下（`Bank/requests/<日>/*.json`、`Bank/transfer_requests/<日>/*.json`）。
//           ⚠ 2026-09-22 之前它們留在舊 `Treasury/` —— 理由是「單據不是帳，搬它沒有人在等」，
//           而那個理由在半年後**變成兇器**：🩸 TASK-0273 收尾要刪凍結的舊帳本時，
//           量到同一個資料夾底下**還住著活的請款單**，兩者在 `ls` 底下長得一模一樣。
//           ⇒ Tim 2026-09-22 拍板一次收乾淨（TASK-0274）：⛔ 沒有舊路徑 fallback ——
//           留 fallback 就永遠不知道還有誰在讀舊的，而它不會叫。
// 數值影響：本層**一毛錢都不動** —— 只讀單子、只寫單子的裁決欄。
//           錢由呼叫端走 `cmd bank`（Server）動，兩件事**分開結算**。
//
// 🩸 判準：
//   ① **裁決欄寫在錢動完之後。** 反過來的話，中途失敗留下的是
//      「單子寫著 approved、而錢沒撥」——⚠ 那張單之後**不會再出現在待審清單裡**，
//      所以沒有人會回來看它。⇒ 寧可「錢撥了而單子還是 pending」（那個會被再看到一次）。
//   ② **`decided_by` / `decision_note` 不可省**：三個月後要答得出「誰批的、憑什麼」。
//   ③ ⚠ **每次裁決都先回讀狀態**：已經不是 pending 就拒絕。
//      ⇒ 這格防的是**同一張單被批第二次**，而重複撥款的代價跟「是誰批的第二次」無關。
//      📌 射程（2026-09-22 量的，⛔ 不是推的）：**裁決**這個動作今天只有本層做得到 ——
//        Unity 端的 `Approve` 全樹**零呼叫端**（唯一會按它的那一頁已退場，只剩兩處註解提到它）。
//        ⛔ 而那兩個資料夾**不是只有本層在寫**：Unity 端仍會 `Create`（開單）與 `Close`（取消）
//        ⇒ 「單一裁決者」成立，「單一寫入端」不成立，兩者別混。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Bank
{
    public sealed class SCP_PayoutRequest
    {
        public string Path = "";
        public string RequestId = "";
        public string RequestedAt = "";
        public string Status = "";
        public string TargetBank = "";
        public int Amount;
        public string Currency = "tavern_token";
        public string Reason = "";
        public string RequesterPersona = "";
        public string RequesterAgent = "";
        public string DecidedBy = "";
        public string DecisionNote = "";

        /// <summary>
        /// 錢從哪來：<c>central</c>＝央行撥款（公庫變少）／<c>mint</c>＝**增發**（憑空生出，不碰任何帳戶）。
        /// <para>空字串＝單子沒宣告 ⇒ 由裁決端決定（預設 <c>central</c>，維持舊行為）。</para>
        /// <para>🩸 為什麼要分（Tim 2026-09-22 拍板）：**補薪本來就該是增發** ——
        /// 那筆錢是勞動新產生的價值，不是從公庫搬過來的。
        /// 而 2026-09-22 的 114 token 補發走了央行撥款 ⇒ 公庫平白少了 114，
        /// 事後得再增發一筆補回去（`payout_shape_correction`）。
        /// ⛔ 兩種錢在單子上長得一模一樣（都是「請款 N token 給 X」），
        /// 而它們對公庫的影響相反 —— **審批的人要看得出自己在批哪一種**。</para>
        /// </summary>
        public string Funding = "";

        /// <summary>
        /// 這張單**為什麼**開：<c>work_post_backfill</c>＝補發文領薪的漏帳／空字串＝單子沒宣告。
        /// <para>⚠ 它與 <see cref="Funding"/> 是兩個不同的問題：Funding 是「錢從哪來」，
        /// 本欄是「這筆錢在補什麼」。⛔ 兩者不可互推 —— 補薪可以走央行，增發也可以不是補薪。</para>
        /// <para>🩸 為什麼稽核要看它（TASK-0290）：請款撥款**結構上不帶逐則 ref**
        /// ⇒ 補了哪幾則只記在 `payroll_settled.json`，而那份清單**全庫沒有寫入端**（人手寫的）。
        /// ⇒ 少登記一次，領薪稽核就會把已付的那些則報成缺口，而它跟真缺口逐字同形。
        /// ⛔ 而本欄**不能**用來反推「那批付了哪幾則」—— 它只說得出「有一批是在補發文領薪」，
        /// 逐則的憑據仍然只有那份清單。⇒ 所以稽核拿它**出聲**，不拿它沖銷。</para>
        /// </summary>
        public string SourceKind = "";
    }

    /// <summary>請款的資金來源。</summary>
    public static class SCP_PayoutFunding
    {
        /// <summary>央行撥款：公庫 → 目標戶，總量守恆。</summary>
        public const string Central = "central";

        /// <summary>增發：憑空生出給目標戶，⛔ 不碰任何帳戶（總量變多）。</summary>
        public const string Mint = "mint";

        /// <summary>單子沒宣告時用哪一種 —— **央行**（維持 2026-09-22 之前的行為，⛔ 不默默改成增發）。</summary>
        public const string Default = Central;

        public static bool IsValid(string iValue)
            => string.Equals(iValue, Central, StringComparison.Ordinal)
            || string.Equals(iValue, Mint, StringComparison.Ordinal);

        /// <summary>人讀的一句話（審批畫面與 CLI 都印它 —— ⛔ 兩種錢不可以長得一樣）。</summary>
        public static string Describe(string iValue)
            => string.Equals(iValue, Mint, StringComparison.Ordinal)
               ? "**增發**（憑空生出，⛔ 不碰公庫；總量變多）"
               : "**央行撥款**（公庫 → 目標戶，公庫變少）";
    }

    public sealed class SCP_TransferRequest
    {
        public string Path = "";
        public string RequestId = "";
        public string RequestedAt = "";
        public string Status = "";
        public string FromBank = "";
        public string ToBank = "";
        public int Amount;
        public string Currency = "tavern_token";
        public string Reason = "";
        public string Kind = "";
        public string RequesterPersona = "";
        public string DecidedBy = "";
        public string DecisionNote = "";
    }

    public static class SCP_TreasuryRequests
    {
        public const string PayoutDirName = "requests";
        public const string TransferDirName = "transfer_requests";
        public const string StatusPending = "pending";

        /// <summary>單據的家 —— `<資料根>/Bank`（TASK-0274 起；⛔ 不是凍結的 `Treasury/`）。</summary>
        public const string BankDirName = "Bank";

        public static string PayoutDir(string iDataRoot)
            => System.IO.Path.Combine(iDataRoot, BankDirName, PayoutDirName);

        public static string TransferDir(string iDataRoot)
            => System.IO.Path.Combine(iDataRoot, BankDirName, TransferDirName);

        /// <summary>央行帳號 —— 與舊系統**同一格設定**（`bank_settings.json`），⛔ 不另立一份。</summary>
        public const string DefaultCentralBank = "pacific-standard-public-deposit-bank";

        public static string ReadCentralBank(string iDataRoot)
        {
            string aPath = SCP_BankRegion.SettingsPath(iDataRoot);
            if (!File.Exists(aPath)) return DefaultCentralBank;
            try
            {
                SCP_JsonData? aJd = SCP_JsonData.Parse(File.ReadAllText(aPath));
                if (aJd == null || !aJd.IsObject) return DefaultCentralBank;
                string v = aJd.GetString("central_bank_account", "");
                return string.IsNullOrWhiteSpace(v) ? DefaultCentralBank : v.Trim();
            }
            catch (Exception) { return DefaultCentralBank; }
        }

        // ── 讀 ────────────────────────────────────────────────────

        public static List<SCP_PayoutRequest> LoadPendingPayouts(string iDataRoot, List<string>? oProblems = null)
        {
            // 🚚 舊路徑自動搬（TASK-0275）。⛔ 這一格特別要命：單據沒搬 ⇒ 待審清單回「0 張」，
            //   而「真的沒有待審」與「單子在另一個資料夾」在畫面上是**同一句話**。
            SCP_BankMigration.EnsureOnce(iDataRoot);
            var aOut = new List<SCP_PayoutRequest>();
            foreach (string aFile in ScanJson(PayoutDir(iDataRoot), oProblems))
            {
                SCP_JsonData? aJd = TryParse(aFile, oProblems);
                if (aJd == null) continue;
                if (!string.Equals(aJd.GetString("status", ""), StatusPending, StringComparison.Ordinal)) continue;
                aOut.Add(new SCP_PayoutRequest
                {
                    Path = aFile,
                    RequestId = aJd.GetString("request_id", ""),
                    RequestedAt = aJd.GetString("requested_at", ""),
                    Status = aJd.GetString("status", ""),
                    TargetBank = aJd.GetString("target_bank", ""),
                    Amount = aJd.GetInt("amount", 0),
                    Currency = aJd.GetString("currency", "tavern_token"),
                    Reason = aJd.GetString("reason", ""),
                    RequesterPersona = aJd.GetString("requester_persona", ""),
                    RequesterAgent = aJd.GetString("requester_agent", ""),
                    // ⚠ 缺席＝單子沒宣告，⛔ **不在這裡補預設** —— 補了的話「開單人選了央行」
                    //   與「開單人沒說」就同形，而裁決端要看得出後者（那代表它要自己決定）。
                    Funding = aJd.GetString("funding", ""),
                    SourceKind = aJd.GetString("source_kind", ""),
                });
            }
            aOut.Sort((a, b) => string.CompareOrdinal(a.RequestedAt, b.RequestedAt));
            return aOut;
        }

        /// <summary>`source_kind` 的已知值：補發文領薪的漏帳。</summary>
        public const string SourceKindWorkPostBackfill = "work_post_backfill";

        /// <summary>
        /// 讀某一個 UTC 日的請款單，回 <c>request_id → source_kind</c>。⛔ **不分 status**
        /// （稽核要看的是「那天撥出去的錢在補什麼」，而撥完的單早就不是 pending 了）。
        /// </summary>
        /// <param name="oDirMissing">
        /// 那一天的單據夾**不存在**。⚠ 這不是「那天沒有請款」—— 呼叫端若在帳上看到當天有撥款
        /// 卻拿到 <c>true</c>，那代表**分類不出來**，⛔ 不准當成「都不是補薪」。
        /// </param>
        /// <remarks>
        /// ⛔ 本支**刻意不呼叫** <c>SCP_BankMigration.EnsureOnce</c>（<c>LoadPendingPayouts</c> 會）——
        /// 那支會搬檔案，而本支唯一的呼叫端是自稱「一毛錢都不動、也不寫任何檔」的領薪稽核。
        /// 🩸 讓一個純讀路徑順手帶一個寫入，失效樣子是「稽核跑一次，磁碟就變了一次」，而沒有人會預期它。
        /// ⇒ 代價講明：**還沒遷移的樹在這裡讀不到單子**，而那會落進 <paramref name="oDirMissing"/> ⇒ 出聲，不是靜默。
        /// </remarks>
        public static Dictionary<string, string> LoadPayoutSourceKindsByDay(
            string iDataRoot, string iDayKey, out bool oDirMissing, List<string>? oProblems = null)
        {
            var aOut = new Dictionary<string, string>(StringComparer.Ordinal);
            string aDayDir = Path.Combine(PayoutDir(iDataRoot), iDayKey);
            oDirMissing = !Directory.Exists(aDayDir);
            if (oDirMissing) return aOut;
            foreach (string aFile in ScanJson(aDayDir, oProblems))
            {
                SCP_JsonData? aJd = TryParse(aFile, oProblems);
                if (aJd == null) continue;
                string aId = aJd.GetString("request_id", "");
                if (aId.Length == 0) continue;
                aOut[aId] = aJd.GetString("source_kind", "");
            }
            return aOut;
        }

        public static List<SCP_TransferRequest> LoadPendingTransfers(string iDataRoot, List<string>? oProblems = null)
        {
            SCP_BankMigration.EnsureOnce(iDataRoot);   // 🚚 同上（TASK-0275）
            var aOut = new List<SCP_TransferRequest>();
            foreach (string aFile in ScanJson(TransferDir(iDataRoot), oProblems))
            {
                SCP_JsonData? aJd = TryParse(aFile, oProblems);
                if (aJd == null) continue;
                if (!string.Equals(aJd.GetString("status", ""), StatusPending, StringComparison.Ordinal)) continue;
                aOut.Add(new SCP_TransferRequest
                {
                    Path = aFile,
                    RequestId = aJd.GetString("request_id", ""),
                    RequestedAt = aJd.GetString("requested_at", ""),
                    Status = aJd.GetString("status", ""),
                    FromBank = aJd.GetString("from_bank", ""),
                    ToBank = aJd.GetString("to_bank", ""),
                    Amount = aJd.GetInt("amount", 0),
                    Currency = aJd.GetString("currency", "tavern_token"),
                    Reason = aJd.GetString("reason", ""),
                    Kind = aJd.GetString("kind", ""),
                    RequesterPersona = aJd.GetString("requester_persona", ""),
                });
            }
            aOut.Sort((a, b) => string.CompareOrdinal(a.RequestedAt, b.RequestedAt));
            return aOut;
        }

        // ── 寫（只動裁決欄）────────────────────────────────────────

        /// <summary>
        /// 寫回裁決。⚠ **先回讀**：不是 `pending` 就拒絕 ——
        /// 讓「別處已經批過了」變成一句話，⛔ 而不是一次重複撥款。
        /// </summary>
        public static bool Decide(string iPath, string iStatus, string iDecidedBy, string iNote,
                                  IReadOnlyDictionary<string, string>? iExtraFields, out string oError)
        {
            oError = "";
            if (string.IsNullOrWhiteSpace(iDecidedBy)) { oError = "decided_by 必填 —— 沒有署名的裁決事後查不出是誰批的"; return false; }
            SCP_JsonData? aJd;
            try { aJd = SCP_JsonData.Parse(File.ReadAllText(iPath)); }
            catch (Exception e) { oError = $"單子讀不了（{iPath}）：{e.Message}"; return false; }
            if (aJd == null || !aJd.IsObject) { oError = $"單子不是物件（{iPath}）"; return false; }

            string aNow = aJd.GetString("status", "");
            if (!string.Equals(aNow, StatusPending, StringComparison.Ordinal))
            { oError = $"這張單現在是 `{aNow}` 不是 `pending` ⇒ **沒有動作**（可能剛剛被別處批過了）"; return false; }

            aJd["status"] = iStatus;
            aJd["decided_at"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
            aJd["decided_by"] = iDecidedBy;
            aJd["decision_note"] = iNote ?? "";
            if (iExtraFields != null)
                foreach (KeyValuePair<string, string> kv in iExtraFields) aJd[kv.Key] = kv.Value;

            try
            {
                string aTmp = iPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(aJd, SCP_JsonStyle.UclLegacy) + "\n");
                if (File.Exists(iPath)) File.Delete(iPath);
                File.Move(aTmp, iPath);
            }
            catch (Exception e) { oError = $"單子寫不回去（{iPath}）：{e.Message}"; return false; }
            return true;
        }

        // ── 共用 ──────────────────────────────────────────────────

        static List<string> ScanJson(string iDir, List<string>? oProblems)
        {
            var aOut = new List<string>();
            if (!Directory.Exists(iDir)) return aOut;
            try { aOut.AddRange(Directory.GetFiles(iDir, "*.json", SearchOption.AllDirectories)); }
            catch (Exception e)
            {
                // ⛔ 掃不到**不是**「沒有待審單」：後者是讀數，前者是「我不知道」。
                oProblems?.Add($"掃單據夾失敗（{iDir}）：{e.Message}");
            }
            aOut.Sort(StringComparer.Ordinal);
            return aOut;
        }

        static SCP_JsonData? TryParse(string iFile, List<string>? oProblems)
        {
            try
            {
                SCP_JsonData? aJd = SCP_JsonData.Parse(File.ReadAllText(iFile));
                return aJd != null && aJd.IsObject ? aJd : null;
            }
            catch (Exception e) { oProblems?.Add($"單子讀不了（{iFile}）：{e.Message}"); return null; }
        }
    }
}
