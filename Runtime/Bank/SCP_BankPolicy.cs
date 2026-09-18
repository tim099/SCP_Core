// 區塊職責：**央行政策參數**的讀寫 —— 跟區域名同一個檔（`Treasury/bank_settings.json`）。
// 物理意義：這幾個數字原本寫死在 `UCL_BartenderDaemon` 的 const 裡（Tim 2026-08-01 搬進後台）。
//           它們是**經濟政策參數不是實作細節** —— 決定權該在後台，不在 C# 檔裡。
// 數值影響：改完**立刻生效**（收保管費那支每輪重讀），不必重編、不必重啟。
//           ⚠ 保管費是**每日一次的跨日事件** ⇒ 這一層**刻意沒有「立刻結算一次」** ——
//           手動觸發會讓人以為扣了兩次而去查一個不存在的 bug。
//
// 🩸 判準：
//   ① **費率存千分比整數**（50 ＝ 5.0%）。⛔ 不存 double：
//      這個 repo 的 JSON 讀取只有 `GetInt` / `GetString` 被驗過，
//      為了一個小數點賭一個沒人用過的多載，換來的是執行期靜默拿到 0 的費率。
//   ② **夾值在讀與寫兩側都做**：只在寫入端夾的話，手改過的檔會讓讀取端拿到一個界外值，
//      而它長得跟合法值一模一樣。
//   ③ **央行帳戶與費率分開**：前者決定**錢從哪來**（換掉它，所有撥款換一個來源），
//      後者只改數字 ⇒ 呼叫端該給前者二段確認，⛔ 不必給後者。
//
// ⚠ **現在有兩份，而那是過渡狀態**（Tim 2026-09-18：「UCL 那邊之後要廢棄，
//   相關功能遷移到 Senate 後台」）：Unity 那側 `UCL_CentralBankSettings` 自己也有一份
//   同名常數與夾值規則，⇒ 本檔是**接手的那一份**。
//   🩸 在它退場之前，兩份夾值規則漂掉時**兩邊都會讀出一個合法的數字**，
//     而「誰是權威」在畫面上看不出來 ⇒ 過渡期改參數**只從一邊改**（這一邊）。
//   📌 退場本身排在 TASK-0242（廢棄 Unity 端舊銀行流程，整段移除不留墓碑）。
#nullable enable
using System;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Bank
{
    public static class SCP_BankPolicy
    {
        public const string KeyCentralBank = "central_bank_account";
        public const string KeyThreshold = "overnight_threshold";
        public const string KeyFeePermille = "overnight_fee_permille";
        public const string KeyExemptCentral = "exempt_central_bank";
        public const string KeyMailFee = "registered_mail_fee";

        public const int DefaultThreshold = 1000;
        /// <summary>千分比整數（50 ＝ 5.0%）。</summary>
        public const int DefaultFeePermille = 50;
        public const int DefaultMailFee = 5;

        /// <summary>費率下限 0 ＝ **停收**（那是合法的關閉手段，不必改 code）。</summary>
        public const int MinFeePermille = 0;
        /// <summary>費率上限 500‰ ＝ 50%。再高一晚就砍半，那不是保管費是沒收。</summary>
        public const int MaxFeePermille = 500;
        public const int MinThreshold = 0;

        public static int ClampPermille(int iValue)
            => iValue < MinFeePermille ? MinFeePermille : (iValue > MaxFeePermille ? MaxFeePermille : iValue);

        public sealed class Reading
        {
            public string CentralBank = SCP_TreasuryRequests.DefaultCentralBank;
            public int Threshold = DefaultThreshold;
            public int FeePermille = DefaultFeePermille;
            public bool ExemptCentral = true;
            public int MailFee = DefaultMailFee;

            /// <summary>費率的顯示字串：50‰ → "5"、25‰ → "2.5"。</summary>
            public string FeeRateDisplay => (FeePermille % 10 == 0)
                ? (FeePermille / 10).ToString()
                : (FeePermille / 10.0).ToString("0.#");
        }

        public static Reading Read(string iDataRoot, out string? oWhy)
        {
            oWhy = null;
            var aOut = new Reading();
            string aPath = SCP_BankRegion.SettingsPath(iDataRoot);
            if (!File.Exists(aPath)) { oWhy = $"設定檔不在（{aPath}）⇒ 全部用預設"; return aOut; }
            SCP_JsonData? aJd;
            try { aJd = SCP_JsonData.Parse(File.ReadAllText(aPath)); }
            catch (Exception e) { oWhy = $"設定檔讀不了（{e.GetType().Name}）⇒ 全部用預設"; return aOut; }
            if (aJd == null || !aJd.IsObject) { oWhy = "設定檔不是物件 ⇒ 全部用預設"; return aOut; }

            string aCb = aJd.GetString(KeyCentralBank, "");
            if (!string.IsNullOrWhiteSpace(aCb)) aOut.CentralBank = aCb.Trim();

            // ⚠ 夾值在**讀**這一側也做（判準②）：手改過的檔會給出界外值，而它長得跟合法值一樣。
            int aTh = aJd.GetInt(KeyThreshold, DefaultThreshold);
            aOut.Threshold = aTh < MinThreshold ? MinThreshold : aTh;
            aOut.FeePermille = ClampPermille(aJd.GetInt(KeyFeePermille, DefaultFeePermille));
            aOut.ExemptCentral = aJd.GetInt(KeyExemptCentral, 1) != 0;
            int aMail = aJd.GetInt(KeyMailFee, DefaultMailFee);
            aOut.MailFee = aMail < 0 ? 0 : aMail;
            return aOut;
        }

        /// <summary>
        /// 寫幾格政策參數（**read-modify-write**）。
        /// <para>⚠ 整份覆寫會把 `currency_id` 那些格吃掉 —— 它們住同一個檔。</para>
        /// </summary>
        public static bool Write(string iDataRoot, Action<SCP_JsonData> iMutate, out string? oError)
        {
            oError = null;
            string aPath = SCP_BankRegion.SettingsPath(iDataRoot);
            SCP_JsonData? aJd = null;
            try { if (File.Exists(aPath)) aJd = SCP_JsonData.Parse(File.ReadAllText(aPath)); }
            // ⛔ 舊設定讀不了就**不覆寫**：覆寫會把讀不出來的那幾格一起清掉，
            //   而清掉之後讀取端會拿到「預設值」——一個完全合法、而且不是任何人設定過的值。
            catch (Exception e) { oError = $"舊設定讀不了，⛔ 不覆寫（{e.Message}）"; return false; }
            if (aJd == null || !aJd.IsObject) aJd = SCP_JsonData.Parse("{}");

            iMutate(aJd!);
            try
            {
                string? aDir = Path.GetDirectoryName(aPath);
                if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(aJd!, SCP_JsonStyle.UclLegacy) + "\n");
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
            }
            catch (Exception e) { oError = $"寫不進去（{aPath}）：{e.Message}"; return false; }
            return true;
        }

        /// <summary>帳號合法性 ＝ 能安全當檔名（帳戶是一帳一檔）。</summary>
        public static bool IsValidAccountId(string? iId)
        {
            if (string.IsNullOrWhiteSpace(iId)) return false;
            string v = iId!.Trim();
            if (v == "." || v == "..") return false;
            if (v.IndexOf('/') >= 0 || v.IndexOf('\\') >= 0) return false;
            foreach (char c in Path.GetInvalidFileNameChars())
                if (v.IndexOf(c) >= 0) return false;
            return true;
        }
    }
}
