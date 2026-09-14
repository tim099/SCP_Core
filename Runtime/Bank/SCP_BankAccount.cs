// 區塊職責：帳戶本體與帳戶檔的讀寫（TASK-0209 B2）——**開戶是顯式動作**。
// 物理意義：舊系統沒有開戶這件事：帳號在 ledger 裡**自己長出來**（寫一筆 credit 就有了）。
//           2026-09-14 實測的代價：51 個有餘額的帳號裡，**42 個沒有帳戶檔** ——
//           其中 20 個有錢，最大的 `claude-da-xiaojie` 有 4,636（全庫第二大持有者）。
//           ⇒ 「這是誰的帳號、誰開的、還在不在用」全部沒有答案，而帳面完全正常。
//           🩸 更毒的是打錯字：`zeta-da-xiaojie-bank` 與 `zeta-da-xiaojie` 這種，
//           在舊系統裡**打錯一個字就憑空多一個帳號**，而錢真的會進去。
//           ⇒ 新系統：**沒開戶的帳號不能收付**，打錯字會當場被擋，不會變成第 43 個幽靈。
// 數值影響：一帳戶一檔（`Bank/accounts/<id>.json`），JSON 走 SCP_Json（D25）。
//           開戶／銷戶會寫檔；其餘都是讀。⛔ 本層不碰 ledger。
//
// 🩸 判準：
//   ① **id 一律先過 `SCP_BankId`**（寫入端正規化，見那一檔判準②）。
//   ② **銷戶是狀態不是刪檔**：刪掉檔案之後，ledger 裡那些 entry 就沒有出處了，
//      而「查不到帳戶」與「這個帳號從來不存在」同形。⇒ `status: closed`，檔留著。
//   ③ **顯示名與 id 分開**：`FRS` 是顯示名，`frs` 是 id。
//      合成一個的話，改顯示名就等於換帳號，而錢會留在舊的那個上面。
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Bank
{
    /// <summary>帳戶狀態。⛔ 不用 bool：`closed` 與「檔案不存在」是兩件事（判準②）。</summary>
    public enum SCP_BankAccountStatus
    {
        Open,
        Closed,
    }

    public sealed class SCP_BankAccount
    {
        /// <summary>正規化後的 id（＝檔名）。</summary>
        public string Id = "";

        /// <summary>人看的名字（可以有大小寫與空白）。空 ＝ 就用 id 顯示。</summary>
        public string DisplayName = "";

        public SCP_BankAccountStatus Status = SCP_BankAccountStatus.Open;

        public string OpenedAtUtc = "";
        public string OpenedBy = "";
        public string ClosedAtUtc = "";

        /// <summary>銷戶理由 —— ⛔ 沒有理由的銷戶不准（下一個人要查得出為什麼那個帳號不能用了）。</summary>
        public string ClosedReason = "";

        /// <summary>
        /// 遷移來源：這個帳戶是從舊系統的哪個（些）原始 id 來的。
        /// <para>🩸 存在的理由：遷移會做兩種合併（大小寫、改名），而合併**不可逆**。
        /// 不記下來的話，日後看到 `frs` 的餘額沒有人答得出「`Federal Reserve System` 那筆去哪了」。</para>
        /// </summary>
        public List<string> MigratedFrom = new List<string>();

        public string Label => DisplayName.Length > 0 ? DisplayName : Id;

        public SCP_JsonData ToJson()
        {
            var aData = SCP_JsonData.NewObject();
            aData.Set("id", SCP_JsonData.NewString(Id));
            aData.Set("display_name", SCP_JsonData.NewString(DisplayName));
            aData.Set("status", SCP_JsonData.NewString(Status == SCP_BankAccountStatus.Closed ? "closed" : "open"));
            aData.Set("opened_at_utc", SCP_JsonData.NewString(OpenedAtUtc));
            aData.Set("opened_by", SCP_JsonData.NewString(OpenedBy));
            if (Status == SCP_BankAccountStatus.Closed)
            {
                aData.Set("closed_at_utc", SCP_JsonData.NewString(ClosedAtUtc));
                aData.Set("closed_reason", SCP_JsonData.NewString(ClosedReason));
            }
            if (MigratedFrom.Count > 0)
            {
                var aArr = SCP_JsonData.NewArray();
                foreach (string aFrom in MigratedFrom) aArr.Add(SCP_JsonData.NewString(aFrom));
                aData.Set("migrated_from", aArr);
            }
            aData.Set("schema_version", SCP_JsonData.NewNumber(1));
            return aData;
        }

        public static SCP_BankAccount FromJson(SCP_JsonData iData)
        {
            var aAcc = new SCP_BankAccount
            {
                Id = iData.GetString("id", ""),
                DisplayName = iData.GetString("display_name", ""),
                // ⚠ 認不得的狀態字 ⇒ 當成 closed（安全側）。
                //   反過來的話，一個壞掉的欄位會讓帳號**變成可以動錢的**。
                Status = iData.GetString("status", "open") == "open"
                         ? SCP_BankAccountStatus.Open : SCP_BankAccountStatus.Closed,
                OpenedAtUtc = iData.GetString("opened_at_utc", ""),
                OpenedBy = iData.GetString("opened_by", ""),
                ClosedAtUtc = iData.GetString("closed_at_utc", ""),
                ClosedReason = iData.GetString("closed_reason", ""),
            };
            SCP_JsonData aArr = iData["migrated_from"];
            if (aArr.Exists && aArr.IsArray)
                foreach (SCP_JsonData aItem in aArr) aAcc.MigratedFrom.Add(aItem.AsString());
            return aAcc;
        }
    }

    /// <summary>帳戶檔的讀寫。⛔ 不快取 —— 帳戶檔很少、很小，而快取要處理「別的 process 改了它」。</summary>
    public static class SCP_BankAccounts
    {
        public const string AccountsDirName = "accounts";

        public static string AccountsDir(string iBankRoot) => Path.Combine(iBankRoot, AccountsDirName);

        public static string AccountPath(string iBankRoot, string iCanonicalId)
            => Path.Combine(AccountsDir(iBankRoot), iCanonicalId + ".json");

        /// <summary>
        /// 讀一個帳戶。找不到回 null（⛔ 不丟例外：「沒開戶」是正常的查詢結果，不是錯誤）。
        /// <para><paramref name="oWhy"/> 在 id 不合法或檔案壞掉時有話說。</para>
        /// </summary>
        public static SCP_BankAccount? TryLoad(string iBankRoot, string iRawId, out string oWhy)
        {
            oWhy = "";
            SCP_BankIdResult aId = SCP_BankId.Normalize(iRawId);
            if (!aId.Ok) { oWhy = aId.Why; return null; }

            string aPath = AccountPath(iBankRoot, aId.Id);
            if (!File.Exists(aPath)) return null;
            try { return SCP_BankAccount.FromJson(SCP_JsonParser.Parse(File.ReadAllText(aPath))); }
            catch (Exception e)
            {
                // ⛔ 讀不了**不等於**沒開戶：回 null 但把原因說出來，
                //   否則一個壞掉的帳戶檔會讓那個帳號看起來像從來沒存在過。
                oWhy = $"帳戶檔讀不了（{aPath}）：{e.GetType().Name}: {e.Message}";
                return null;
            }
        }

        /// <summary>這個帳號現在**可以收付**嗎 —— 三態，⛔ 不是 bool。</summary>
        public static SCP_BankAccountCheck CheckUsable(string iBankRoot, string iRawId)
        {
            SCP_BankIdResult aId = SCP_BankId.Normalize(iRawId);
            if (!aId.Ok) return SCP_BankAccountCheck.Invalid(aId.Why);

            SCP_BankAccount? aAcc = TryLoad(iBankRoot, aId.Id, out string aWhy);
            if (aWhy.Length > 0) return SCP_BankAccountCheck.Invalid(aWhy);
            if (aAcc == null)
                return SCP_BankAccountCheck.NotOpened(
                    $"帳號 '{aId.Id}' 沒有開戶 ⇒ 不能收付。"
                    + "　⛔ 這一格刻意擋住：舊系統讓帳號在帳本裡自己長出來，於是打錯一個字就憑空多一個帳號而錢真的進去了"
                    + "（2026-09-14 實測：51 個有餘額的帳號裡 42 個沒開過戶）。"
                    + $"　⇒ 真要用它：先開戶。");
            if (aAcc.Status == SCP_BankAccountStatus.Closed)
                return SCP_BankAccountCheck.Closed(
                    $"帳號 '{aId.Id}' 已銷戶（{aAcc.ClosedAtUtc}）⇒ 不能收付。理由：{aAcc.ClosedReason}");
            return SCP_BankAccountCheck.Usable(aAcc);
        }

        /// <summary>開戶。已經有了就**不動它**並說明（⛔ 不覆寫 —— 覆寫會把 opened_at 與遷移來源洗掉）。</summary>
        public static bool TryOpen(string iBankRoot, string iRawId, string iDisplayName, string iOpenedBy,
                                   out SCP_BankAccount? oAccount, out string oWhy)
        {
            oAccount = null; oWhy = "";
            SCP_BankIdResult aId = SCP_BankId.Normalize(iRawId);
            if (!aId.Ok) { oWhy = aId.Why; return false; }

            SCP_BankAccount? aExisting = TryLoad(iBankRoot, aId.Id, out string aLoadWhy);
            if (aLoadWhy.Length > 0) { oWhy = aLoadWhy; return false; }
            if (aExisting != null)
            {
                oAccount = aExisting;
                // ⚠ 大小寫撞名要**說出來**（Tim 2026-09-14 補充）：使用者從沒開過 `Frs`，
                //   只回「已經開過戶了」會讓他去找一個他沒做過的動作。
                //   ⇒ 兩種拒絕長得不一樣：「你開過了」vs「這跟已存在的帳號只差大小寫」。
                string aCaseNote = string.Equals(iRawId?.Trim(), aId.Id, StringComparison.Ordinal)
                    ? ""
                    : $"　⚠ 你打的是 '{iRawId?.Trim()}'，而帳號 id 一律小寫 ⇒ 它指的就是 '{aId.Id}'"
                      + "（**禁止大小寫同名**：兩個只差大小寫的帳號在 Windows 上還會共用同一個檔）";
                oWhy = $"帳號 '{aId.Id}'"
                       + (aExisting.DisplayName.Length > 0 ? $"（顯示名 '{aExisting.DisplayName}'）" : "")
                       + $" 已經開過戶了（{aExisting.OpenedAtUtc} by {aExisting.OpenedBy}）⇒ 沒有動它"
                       + aCaseNote;
                return false;
            }

            var aAcc = new SCP_BankAccount
            {
                Id = aId.Id,
                DisplayName = (iDisplayName ?? "").Trim(),
                Status = SCP_BankAccountStatus.Open,
                OpenedAtUtc = DateTime.UtcNow.ToString("o"),
                OpenedBy = iOpenedBy ?? "",
            };
            if (!TrySave(iBankRoot, aAcc, out oWhy)) return false;
            oAccount = aAcc;
            return true;
        }

        /// <summary>寫回帳戶檔（原子）。⚠ 遷移那側要用它補 `MigratedFrom`。</summary>
        public static bool TrySave(string iBankRoot, SCP_BankAccount iAccount, out string oWhy)
        {
            oWhy = "";
            try
            {
                Directory.CreateDirectory(AccountsDir(iBankRoot));
                string aPath = AccountPath(iBankRoot, iAccount.Id);
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(iAccount.ToJson()) + "\n");
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
                return true;
            }
            catch (Exception e) { oWhy = $"{e.GetType().Name}: {e.Message}"; return false; }
        }

        /// <summary>列出所有帳戶（含已銷戶的 —— 呼叫端自己篩；藏起來會讓「錢在哪」少一塊）。</summary>
        public static List<SCP_BankAccount> LoadAll(string iBankRoot, List<string>? oProblems = null)
        {
            var aList = new List<SCP_BankAccount>();
            string aDir = AccountsDir(iBankRoot);
            if (!Directory.Exists(aDir)) return aList;
            foreach (string aPath in Directory.GetFiles(aDir, "*.json"))
            {
                try { aList.Add(SCP_BankAccount.FromJson(SCP_JsonParser.Parse(File.ReadAllText(aPath)))); }
                catch (Exception e) { oProblems?.Add($"{Path.GetFileName(aPath)}：{e.GetType().Name}: {e.Message}"); }
            }
            aList.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            return aList;
        }
    }

    /// <summary>「這個帳號能不能收付」的答案 —— 四態，每一態的處置都不一樣。</summary>
    public readonly struct SCP_BankAccountCheck
    {
        public enum Kind { Usable, NotOpened, Closed, Invalid }

        public readonly Kind Result;
        public readonly SCP_BankAccount? Account;
        public readonly string Why;

        SCP_BankAccountCheck(Kind iKind, SCP_BankAccount? iAcc, string iWhy)
        { Result = iKind; Account = iAcc; Why = iWhy; }

        public bool Ok => Result == Kind.Usable;

        public static SCP_BankAccountCheck Usable(SCP_BankAccount iAcc) => new SCP_BankAccountCheck(Kind.Usable, iAcc, "");
        public static SCP_BankAccountCheck NotOpened(string iWhy) => new SCP_BankAccountCheck(Kind.NotOpened, null, iWhy);
        public static SCP_BankAccountCheck Closed(string iWhy) => new SCP_BankAccountCheck(Kind.Closed, null, iWhy);
        public static SCP_BankAccountCheck Invalid(string iWhy) => new SCP_BankAccountCheck(Kind.Invalid, null, iWhy);
    }
}
