using System;
using System.IO;
using SCP.Core.Json;

// 區塊職責：**這棵資料樹的區域（貨幣）名** —— 讀寫 `<資料根>/Treasury/bank_settings.json` 的 `currency_id`。
// 物理意義：Tim 2026-09-17：「新銀行不用多區，參考原本的，只處理本區資料（Florin），一樣要有設定區域名稱功能。」
//           ⇒ 新銀行**不自己發明區域設定**，直接讀舊系統那一格 —— 同一個檔、同一個鍵、同一條合法性規則。
//           🩸 另立一份的話，兩邊會對「這裡是哪一區」給出不同答案，而兩邊都讀得出一個完全合法的字串。
// 數值影響：這個值是**檔名**：`letters/<persona>/bank/<區域>.md`。改它等於把全體 persona 的綁定檔重新定鍵
//           ⇒ 沒有同批改名的話，全員的帳號會一次解析不到（舊系統那側的註解記著「全員一次落央行」）。
//           ⇒ 呼叫端**必須**用二段確認，⛔ 不可以做成一個按下去就生效的輸入框。
// ⚠ 本層**不改名任何綁定檔**，只讀寫那一格設定。搬家是另一件事，而它不該藏在一個 setter 裡。
namespace SCP.Core.Bank
{
    public static class SCP_BankRegion
    {
        /// <summary>設定檔在資料根底下的相對位置 —— 與舊系統**同一個檔**（⛔ 不另開一份）。</summary>
        public const string SettingsRelPath = "Treasury/bank_settings.json";

        /// <summary>`currency_id` 缺值／壞值時的預設。⚠ 與 Unity 那側的 `DefaultCurrencyId` 同值。</summary>
        public const string DefaultRegion = "Ducat";

        public static string SettingsPath(string iDataRoot)
            => Path.Combine(iDataRoot, SettingsRelPath.Replace('/', Path.DirectorySeparatorChar));

        /// <summary>
        /// 讀這棵樹的區域名。<paramref name="oWhy"/> 在**回預設**時說明為什麼
        /// （檔不在／沒有那一格／值不合法）—— ⛔ 三者不可同形：只有最後一個是「狀態壞了」。
        /// </summary>
        public static string Read(string iDataRoot, out string? oWhy)
        {
            oWhy = null;
            string aPath = SettingsPath(iDataRoot);
            if (!File.Exists(aPath)) { oWhy = $"設定檔不在（{aPath}）⇒ 用預設"; return DefaultRegion; }

            SCP_JsonData? aJd;
            try { aJd = SCP_JsonData.Parse(File.ReadAllText(aPath)); }
            catch (Exception e) { oWhy = $"設定檔讀不了（{e.GetType().Name}）⇒ 用預設"; return DefaultRegion; }
            if (aJd == null || !aJd.IsObject) { oWhy = "設定檔不是物件 ⇒ 用預設"; return DefaultRegion; }
            if (!aJd.Contains("currency_id")) { oWhy = "設定檔沒有 `currency_id` 這一格 ⇒ 用預設"; return DefaultRegion; }

            string aValue = aJd.GetString("currency_id", "");
            if (!IsValid(aValue))
            {
                // ⚠ 這一支要出聲：靜默回預設會讓兩棵樹都變成 `Ducat`，
                //   而那正是「一區一檔」要防的對撞 —— 症狀是「另一個專案的帳號」。
                oWhy = $"`currency_id` 落盤值不合法（`{aValue}`）⇒ 用預設，**這是狀態壞了，不是沒設定**";
                return DefaultRegion;
            }
            return aValue.Trim();
        }

        /// <summary>
        /// 寫回區域名。⚠ **read-modify-write**：設定檔裡還有手續費那幾格，整份覆寫會把它們吃掉。
        /// </summary>
        public static bool Write(string iDataRoot, string iRegion, out string? oError)
        {
            oError = null;
            if (!IsValid(iRegion)) { oError = $"區域名不合法（`{iRegion}`）—— **沒有寫入**"; return false; }

            string aPath = SettingsPath(iDataRoot);
            SCP_JsonData? aJd = null;
            try { if (File.Exists(aPath)) aJd = SCP_JsonData.Parse(File.ReadAllText(aPath)); }
            catch (Exception e) { oError = $"舊設定讀不了，⛔ 不覆寫（{e.Message}）"; return false; }
            if (aJd == null || !aJd.IsObject) aJd = SCP_JsonData.Parse("{}");

            aJd!["currency_id"] = iRegion.Trim();
            try
            {
                string? aDir = Path.GetDirectoryName(aPath);
                if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
                File.WriteAllText(aPath, SCP_JsonWriter.Write(aJd, SCP_JsonStyle.UclLegacy) + "\n");
            }
            catch (Exception e) { oError = $"寫不進去：{e.Message}"; return false; }
            return true;
        }

        // ===========================================================
        // 區塊職責：**金流權威** —— 這棵樹的錢現在記在哪一本帳上（同一個設定檔的 `money_authority`）。
        // 物理意義：TASK-0216 ⑨（Tim 2026-09-18：「全面改用 Senate 銀行作為實際金流」）。
        //          `legacy` ＝ 舊 `Treasury/` 仍是權威；`senate_bank` ＝ **這本帳就是權威**。
        // 🩸 為什麼要讀它：`Cmd_Bank` 每一次輸出都附一句定語（「這不是那本帳」）。
        //   那句話在切換之後**整句是假的**，而**過期不會叫** —— 一句過期的真話比沒有話貴，
        //   因為它看起來是對的。⇒ 定語改成**推導**，而不是「記得回來改」。
        // ⛔ 認不得的值一律當 `legacy`：一個打錯字就靜默宣布權威的設定，比沒有設定危險。
        // ===========================================================
        public const string AuthorityKey = "money_authority";
        public const string AuthorityLegacy = "legacy";
        public const string AuthoritySenateBank = "senate_bank";

        /// <summary>這棵樹的金流權威（回 <see cref="AuthorityLegacy"/> 或 <see cref="AuthoritySenateBank"/>）。</summary>
        public static string ReadAuthority(string iDataRoot)
        {
            string aPath = SettingsPath(iDataRoot);
            if (!File.Exists(aPath)) return AuthorityLegacy;
            try
            {
                SCP_JsonData? aJd = SCP_JsonData.Parse(File.ReadAllText(aPath));
                if (aJd == null || !aJd.IsObject || !aJd.Contains(AuthorityKey)) return AuthorityLegacy;
                string aValue = (aJd.GetString(AuthorityKey, "") ?? "").Trim().ToLowerInvariant();
                return aValue == AuthoritySenateBank ? AuthoritySenateBank : AuthorityLegacy;
            }
            catch { return AuthorityLegacy; }
        }

        /// <summary>
        /// 同上，但入口是**銀行帳本根**（`&lt;資料根&gt;/Bank`）。
        /// ⚠ 「Bank 就住在資料根底下」這個關係**只有這裡知道** —— ⛔ 別在呼叫端各自 `GetParent`：
        /// 那會變成同一條路徑規則的第二份，而兩份漂掉時兩邊都讀得出一個存在的目錄。
        /// </summary>
        public static string AuthorityFromBankRoot(string iBankRoot)
        {
            string aDataRoot = DataRootOfBankRoot(iBankRoot);
            return aDataRoot.Length == 0 ? AuthorityLegacy : ReadAuthority(aDataRoot);
        }

        /// <summary>
        /// 銀行帳本根 → **資料根**。⚠ 上面那條「Bank 住在資料根底下」的關係只有本檔知道，
        /// 這支就是它唯一的出口 —— ⛔ 呼叫端不要各自 `GetParent`。
        /// </summary>
        /// <returns>推不出來時回**空字串**（⛔ 不回一個看起來合理的路徑）。</returns>
        public static string DataRootOfBankRoot(string iBankRoot)
        {
            try
            {
                var aParent = Directory.GetParent(iBankRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                return aParent == null ? "" : aParent.FullName;
            }
            catch { return ""; }
        }

        /// <summary>合法性 ＝ **能安全當檔名**。⚠ 與舊系統 `IsValidCurrencyId` 同一條規則。</summary>
        /// <remarks>含 `/` 或 `..` 就是寫到別的地方去，而寫檔會自動建目錄 ⇒ 症狀是憑空長出一個資料夾，不是錯誤。</remarks>
        public static bool IsValid(string? iRegion)
        {
            if (string.IsNullOrWhiteSpace(iRegion)) return false;
            string aValue = iRegion!.Trim();
            if (aValue == "." || aValue == "..") return false;
            if (aValue.IndexOf('/') >= 0 || aValue.IndexOf('\\') >= 0) return false;
            foreach (char c in Path.GetInvalidFileNameChars())
                if (aValue.IndexOf(c) >= 0) return false;
            return true;
        }
    }
}
