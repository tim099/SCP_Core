using System;
using System.IO;
using SCP.Core.Json;

// 區塊職責：**這棵資料樹的區域（貨幣）名** —— 讀寫 `<資料根>/Bank/bank_settings.json` 的 `currency_id`。
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
        /// <summary>設定檔在資料根底下的相對位置。⚠ 全系統只有這一份（⛔ 不另開第二份）。
        /// 2026-09-22（TASK-0274）從 `Treasury/` 搬進 `Bank/` —— 舊 `Treasury/` 已整包刪除，
        /// ⛔ 沒有 fallback：留著它就永遠不知道還有誰在讀舊路徑，而讀到舊的會拿到一個**合法的錯區域名**。</summary>
        public const string SettingsRelPath = "Bank/bank_settings.json";

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
            // 🚚 舊路徑自動搬（TASK-0275，冪等；已是新版時零成本）——⛔ 不是讀取端 fallback。
            //   🩸 沒有這一格的話，BTC 那棵樹的設定檔還在 `Treasury/` ⇒ 這裡讀不到 ⇒ 靜靜退回
            //     預設區域 `Ducat`，而那是一個**完全合法的字串** ⇒ 全員帳號一次解析不到，
            //     且畫面上跟「這棵樹真的就是 Ducat」一模一樣（2026-09-22 本區實地踩過一次）。
            SCP_BankMigration.EnsureOnce(iDataRoot);
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
