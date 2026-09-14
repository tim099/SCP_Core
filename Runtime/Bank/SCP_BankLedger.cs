// 區塊職責：帳本本體 —— entry 落檔、餘額重放、credit／debit（TASK-0209 B3／B4）。
// 物理意義：append-only、**一筆一檔**（`Bank/ledger/<YYYY-MM-DD>/<HHmmss>_<mmm>_<uuid6>__<type>.json`）。
//           一筆一檔的理由不是美觀：檔名帶 uuid ⇒ **append 天生沒有碰撞**，
//           所以 credit 完全不需要互斥；而且它在 git 裡可 diff、可 merge、人查得動。
// 數值影響：餘額＝把該帳號所有 entry 重放求和（加法交換律 ⇒ 與順序無關）。
//
// 🩸 三格跟舊系統**刻意不一樣**的設計，每一格都有讀數撐著：
//   ① **不存 `balance_before` / `balance_after`。**
//      舊系統每筆都蓋這兩欄，而它們是**去正規化的冗餘**：真相是重放求和。
//      代價是它們會在併發時說謊（蓋的是一份可能過期的快照），而沒有任何一層會喊。
//      ⇒ 拿掉之後連「鎖要不要涵蓋 credit」這個取捨都消失了 —— **credit 不讀餘額，就不必排隊**。
//   ② **臨界區只有 debit 的「讀餘額 → 比大小 → 落檔」。**
//      舊系統只有 in-process 的快取鎖，保護的是記憶體 dict **不是那個序列**
//      ⇒ 兩條 lane 同時 debit 會雙雙讀到同一個餘額、雙雙通過檢查（TOCTOU）。
//      新系統靠**單一寫入端**（Server）＋ 這裡的 lock 把那一段包起來。
//      ⚠ 「單一寫入端」是**前提不是保證**：本層擋不住有人在 Server 之外呼叫它。
//        真的要防，那道閘在 Cmd 那層（`ServerDelegateCmd`）。這裡把前提寫明，不假裝它是自證的。
//   ③ **冪等鍵先於餘額檢查。** 反過來的話，重複請求會在餘額剛好不足時噴「餘額不足」——
//      而那句話是假的（錢早就扣過了），它會讓人去查一個不存在的餘額問題。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Bank
{
    public enum SCP_BankEntryType { Credit, Debit }

    public sealed class SCP_BankEntry
    {
        public string Id = "";
        public SCP_BankEntryType Type;
        public string AccountId = "";
        public int Amount;                       // 一律正數；方向由 Type 決定
        public string Currency = DefaultCurrency;
        public string Kind = "";                 // 為什麼（commit_reward / canvas_pixel / opening_balance …）
        public string Ref = "";                  // 指回現場（commit sha／seq／單號）
        public string Description = "";
        public string Caller = "";
        public string CmdId = "";
        public string IdempotencyKey = "";
        public string AtUtc = "";

        public const string DefaultCurrency = "tavern_token";

        /// <summary>對餘額的貢獻（credit 正、debit 負）。</summary>
        public int Delta => Type == SCP_BankEntryType.Credit ? Amount : -Amount;

        public SCP_JsonData ToJson()
        {
            var d = SCP_JsonData.NewObject();
            d.Set("id", SCP_JsonData.NewString(Id));
            d.Set("type", SCP_JsonData.NewString(Type == SCP_BankEntryType.Credit ? "credit" : "debit"));
            d.Set("account_id", SCP_JsonData.NewString(AccountId));
            d.Set("amount", SCP_JsonData.NewNumber(Amount));
            d.Set("currency", SCP_JsonData.NewString(Currency));
            d.Set("kind", SCP_JsonData.NewString(Kind));
            if (Ref.Length > 0) d.Set("ref", SCP_JsonData.NewString(Ref));
            if (Description.Length > 0) d.Set("description", SCP_JsonData.NewString(Description));
            if (Caller.Length > 0) d.Set("caller", SCP_JsonData.NewString(Caller));
            if (CmdId.Length > 0) d.Set("cmd_id", SCP_JsonData.NewString(CmdId));
            if (IdempotencyKey.Length > 0) d.Set("idempotency_key", SCP_JsonData.NewString(IdempotencyKey));
            d.Set("at_utc", SCP_JsonData.NewString(AtUtc));
            d.Set("schema_version", SCP_JsonData.NewNumber(1));
            return d;
        }

        public static SCP_BankEntry FromJson(SCP_JsonData d) => new SCP_BankEntry
        {
            Id = d.GetString("id", ""),
            Type = d.GetString("type", "credit") == "debit" ? SCP_BankEntryType.Debit : SCP_BankEntryType.Credit,
            AccountId = d.GetString("account_id", ""),
            Amount = d.GetInt("amount", 0),
            Currency = d.GetString("currency", DefaultCurrency),
            Kind = d.GetString("kind", ""),
            Ref = d.GetString("ref", ""),
            Description = d.GetString("description", ""),
            Caller = d.GetString("caller", ""),
            CmdId = d.GetString("cmd_id", ""),
            IdempotencyKey = d.GetString("idempotency_key", ""),
            AtUtc = d.GetString("at_utc", ""),
        };
    }

    /// <summary>一次記帳的結果 —— 成功要拿得到那一筆，失敗要說得出為什麼。</summary>
    public readonly struct SCP_BankPostResult
    {
        public readonly SCP_BankEntry? Entry;
        public readonly bool Ok;
        public readonly string Why;
        /// <summary>這一筆是冪等判重回來的既有 entry（⛔ 不是新扣的錢）。</summary>
        public readonly bool Duplicate;

        SCP_BankPostResult(SCP_BankEntry? e, bool ok, string why, bool dup)
        { Entry = e; Ok = ok; Why = why; Duplicate = dup; }

        public static SCP_BankPostResult Good(SCP_BankEntry e, bool dup = false) => new SCP_BankPostResult(e, true, "", dup);
        public static SCP_BankPostResult Bad(string why) => new SCP_BankPostResult(null, false, why, false);
    }

    public static class SCP_BankLedger
    {
        public const string LedgerDirName = "ledger";

        public static string LedgerDir(string iBankRoot) => Path.Combine(iBankRoot, LedgerDirName);

        // ⚠ 這一把鎖保護的是 **debit 的「讀餘額 → 比大小 → 落檔」那一段**，不是檔案 IO。
        //   它只在**同一個 process 內**有效 —— 跨 process 的互斥靠「只有 Server 寫」那個前提（檔頭判準②）。
        static readonly object s_DebitLock = new object();

        // ── 讀 ──────────────────────────────────────────────────────────

        /// <summary>
        /// 重放求和算餘額。**每次都列舉磁碟**（不讀內容的檔名列舉很便宜），
        /// 所以別的 process 新寫的 entry 這裡看得到 —— ⛔ 不做記憶體快取：
        /// 快取要處理「別人改了它」，而那個問題在這個系統裡的失效樣子是**餘額是舊的而看起來正常**。
        /// </summary>
        public static int GetBalance(string iBankRoot, string iRawAccountId,
                                     string iCurrency = SCP_BankEntry.DefaultCurrency)
        {
            SCP_BankIdResult aId = SCP_BankId.Normalize(iRawAccountId);
            if (!aId.Ok) return 0;
            int aSum = 0;
            foreach (SCP_BankEntry e in EnumerateEntries(iBankRoot))
            {
                if (!string.Equals(e.AccountId, aId.Id, StringComparison.Ordinal)) continue;
                if (!string.Equals(e.Currency, iCurrency, StringComparison.Ordinal)) continue;
                aSum += e.Delta;
            }
            return aSum;
        }

        /// <summary>所有帳號的餘額（重放一次算完，畫表用 —— ⛔ 別對每個帳號各叫一次 GetBalance）。</summary>
        public static Dictionary<string, int> GetAllBalances(string iBankRoot,
                                                             string iCurrency = SCP_BankEntry.DefaultCurrency)
        {
            var aOut = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SCP_BankEntry e in EnumerateEntries(iBankRoot))
            {
                if (!string.Equals(e.Currency, iCurrency, StringComparison.Ordinal)) continue;
                aOut.TryGetValue(e.AccountId, out int aCur);
                aOut[e.AccountId] = aCur + e.Delta;
            }
            return aOut;
        }

        /// <summary>逐筆讀出帳本。⚠ 讀不了的檔**跳過但要被看見** —— 由 <paramref name="oProblems"/> 帶出去。</summary>
        public static IEnumerable<SCP_BankEntry> EnumerateEntries(string iBankRoot, List<string>? oProblems = null)
        {
            string aRoot = LedgerDir(iBankRoot);
            if (!Directory.Exists(aRoot)) yield break;
            string[] aDays = Directory.GetDirectories(aRoot);
            Array.Sort(aDays, StringComparer.Ordinal);
            foreach (string aDay in aDays)
            {
                string[] aFiles = Directory.GetFiles(aDay, "*.json");
                Array.Sort(aFiles, StringComparer.Ordinal);
                foreach (string aFile in aFiles)
                {
                    SCP_BankEntry? e = null;
                    try { e = SCP_BankEntry.FromJson(SCP_JsonParser.Parse(File.ReadAllText(aFile))); }
                    catch (Exception ex) { oProblems?.Add($"{aFile}：{ex.GetType().Name}: {ex.Message}"); }
                    if (e != null) yield return e;
                }
            }
        }

        /// <summary>冪等判重：同 type ＋ 同帳號 ＋ 同 key 的既有 entry。沒有回 null。</summary>
        public static SCP_BankEntry? FindByIdempotencyKey(string iBankRoot, SCP_BankEntryType iType,
                                                          string iCanonicalAccount, string iKey)
        {
            if (string.IsNullOrEmpty(iKey)) return null;
            foreach (SCP_BankEntry e in EnumerateEntries(iBankRoot))
            {
                if (e.Type != iType) continue;
                if (!string.Equals(e.AccountId, iCanonicalAccount, StringComparison.Ordinal)) continue;
                if (string.Equals(e.IdempotencyKey, iKey, StringComparison.Ordinal)) return e;
            }
            return null;
        }

        // ── 寫 ──────────────────────────────────────────────────────────

        /// <summary>入帳。⛔ **不需要互斥**：append-only、不讀餘額做決策（檔頭判準①）。</summary>
        public static SCP_BankPostResult Credit(string iBankRoot, string iRawAccount, int iAmount, string iKind,
                                                string iRef = "", string iDescription = "", string iCaller = "",
                                                string iCmdId = "", string iIdempotencyKey = "",
                                                string iCurrency = SCP_BankEntry.DefaultCurrency)
            => Post(iBankRoot, SCP_BankEntryType.Credit, iRawAccount, iAmount, iKind, iRef, iDescription,
                    iCaller, iCmdId, iIdempotencyKey, iCurrency);

        /// <summary>
        /// 扣款。**讀餘額 → 比大小 → 落檔** 整段在同一把鎖內（檔頭判準②）。
        /// </summary>
        public static SCP_BankPostResult Debit(string iBankRoot, string iRawAccount, int iAmount, string iKind,
                                               string iRef = "", string iDescription = "", string iCaller = "",
                                               string iCmdId = "", string iIdempotencyKey = "",
                                               string iCurrency = SCP_BankEntry.DefaultCurrency)
            => Post(iBankRoot, SCP_BankEntryType.Debit, iRawAccount, iAmount, iKind, iRef, iDescription,
                    iCaller, iCmdId, iIdempotencyKey, iCurrency);

        static SCP_BankPostResult Post(string iBankRoot, SCP_BankEntryType iType, string iRawAccount, int iAmount,
                                       string iKind, string iRef, string iDescription, string iCaller,
                                       string iCmdId, string iIdempotencyKey, string iCurrency)
        {
            if (iAmount <= 0) return SCP_BankPostResult.Bad($"金額必須 > 0（收到 {iAmount}）");
            if (string.IsNullOrWhiteSpace(iKind)) return SCP_BankPostResult.Bad("kind 必填 —— 沒有 kind 的錢日後沒有人答得出它為什麼動");

            // 帳號要能收付：不合法／沒開戶／已銷戶，三種各自說得出原因（B2）
            SCP_BankAccountCheck aCheck = SCP_BankAccounts.CheckUsable(iBankRoot, iRawAccount);
            if (!aCheck.Ok) return SCP_BankPostResult.Bad(aCheck.Why);
            string aAccount = aCheck.Account!.Id;

            if (iType == SCP_BankEntryType.Credit)
                return WriteChecked(iBankRoot, iType, aAccount, iAmount, iKind, iRef, iDescription,
                                    iCaller, iCmdId, iIdempotencyKey, iCurrency, iNeedBalance: false);

            // debit：整段序列化
            lock (s_DebitLock)
                return WriteChecked(iBankRoot, iType, aAccount, iAmount, iKind, iRef, iDescription,
                                    iCaller, iCmdId, iIdempotencyKey, iCurrency, iNeedBalance: true);
        }

        static SCP_BankPostResult WriteChecked(string iBankRoot, SCP_BankEntryType iType, string iAccount, int iAmount,
                                               string iKind, string iRef, string iDescription, string iCaller,
                                               string iCmdId, string iIdempotencyKey, string iCurrency,
                                               bool iNeedBalance)
        {
            // ⚠ 冪等**先於**餘額檢查（檔頭判準③）：反過來的話，重複請求會在餘額剛好不足時
            //   噴「餘額不足」—— 而那句話是假的（錢早就扣過了）。
            SCP_BankEntry? aDup = FindByIdempotencyKey(iBankRoot, iType, iAccount, iIdempotencyKey);
            if (aDup != null) return SCP_BankPostResult.Good(aDup, dup: true);

            if (iNeedBalance)
            {
                int aBalance = GetBalance(iBankRoot, iAccount, iCurrency);
                if (aBalance < iAmount)
                    return SCP_BankPostResult.Bad(
                        $"'{iAccount}' 餘額不足：現有 {aBalance} {iCurrency}，要扣 {iAmount}");
            }

            DateTime aNow = DateTime.UtcNow;
            var aEntry = new SCP_BankEntry
            {
                Id = NewEntryId(aNow),
                Type = iType,
                AccountId = iAccount,
                Amount = iAmount,
                Currency = iCurrency,
                Kind = iKind,
                Ref = iRef ?? "",
                Description = iDescription ?? "",
                Caller = iCaller ?? "",
                CmdId = iCmdId ?? "",
                IdempotencyKey = iIdempotencyKey ?? "",
                AtUtc = aNow.ToString("o", CultureInfo.InvariantCulture),
            };

            string aDir = Path.Combine(LedgerDir(iBankRoot), aNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            string aName = aEntry.Id + "__" + (iType == SCP_BankEntryType.Credit ? "credit" : "debit") + ".json";
            string aPath = Path.Combine(aDir, aName);
            try
            {
                Directory.CreateDirectory(aDir);
                // ⚠ CreateNew：檔名撞到就**炸**，⛔ 不覆寫。撞名代表 uuid 重複或時鐘倒退，
                //   而覆寫的失效樣子是**一筆錢安靜地消失**。
                using (var aFs = new FileStream(aPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var aSw = new StreamWriter(aFs))
                    aSw.Write(SCP_JsonWriter.Write(aEntry.ToJson()) + "\n");
            }
            catch (Exception e) { return SCP_BankPostResult.Bad($"entry 落檔失敗（{aPath}）：{e.GetType().Name}: {e.Message}"); }

            return SCP_BankPostResult.Good(aEntry);
        }

        /// <summary><c>HHmmss_fff_&lt;uuid6&gt;</c> —— 毫秒 ＋ 6 hex，同毫秒同 uuid 才會撞（而那會被 CreateNew 擋下）。</summary>
        static string NewEntryId(DateTime iUtc)
            => iUtc.ToString("HHmmss_fff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
    }
}
