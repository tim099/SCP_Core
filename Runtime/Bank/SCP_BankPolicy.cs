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

        // ── 保管費轉券（TASK-0270）────────────────────────────────────────────
        /// <summary>
        /// 保管費要轉成哪一種券（＝券名＝檔名）。
        /// <para>⭐ **這一格沒有寫在設定檔裡時，預設＝本區的區域 id**（Tim 2026-09-22：
        /// 「BTC 區域預設轉換為 1 BTC 券」）⇒ 每一區的儲備券**天生跟著那一區的貨幣走**，
        /// 不必逐區去後台設一次。</para>
        /// <para>⛔ 關閉的手段是**顯式寫一格**（`voucher_type: ""` 或 `ratio_per_token: 0`），
        /// ⚠ 不再是「什麼都不寫」—— 兩者從今天起語意相反，見 <see cref="Reading.VoucherTrace"/>。</para>
        /// </summary>
        public const string KeyVoucherType = "voucher_type";
        /// <summary>1 Token 換幾張券。**缺這一格＝預設 1**；顯式 `0`＝不轉券。</summary>
        public const string KeyVoucherRatio = "ratio_per_token";

        /// <summary>沒有設定時每 Token 換幾張券（Tim 2026-09-22：單位預設 1）。</summary>
        public const int DefaultVoucherRatio = 1;

        public const int DefaultThreshold = 1000;
        /// <summary>千分比整數（50 ＝ 5.0%）。</summary>
        public const int DefaultFeePermille = 50;
        public const int DefaultMailFee = 5;

        /// <summary>費率下限 0 ＝ **停收**（那是合法的關閉手段，不必改 code）。</summary>
        public const int MinFeePermille = 0;
        /// <summary>費率上限 500‰ ＝ 50%。再高一晚就砍半，那不是保管費是沒收。</summary>
        public const int MaxFeePermille = 500;
        public const int MinThreshold = 0;

        /// <summary>
        /// 每 Token 兌換券數的上限。
        /// <para>⚠ 這一格是**鑄幣參數**：它乘上去的是扣繳額，而扣繳額每天都在變。
        /// 把 20 打成 2000 不會有任何一層叫 —— 券發出去就在別人手上了，而**收不回來**。
        /// ⇒ 上限不是潔癖，是「打錯一個零」與「真的想發那麼多」在輸入框裡同形的那道閘。
        /// ⛔ 真的要超過就改這個常數（要過 review），不是在後台輸入框裡打。</para>
        /// </summary>
        public const int MaxVoucherRatio = 10000;
        public const int MinVoucherRatio = 0;

        public static int ClampVoucherRatio(int iValue)
            => iValue < MinVoucherRatio ? MinVoucherRatio : (iValue > MaxVoucherRatio ? MaxVoucherRatio : iValue);

        /// <summary>
        /// 券名合法性 ＝ 能安全當檔名（券是一券一檔，與 TASK-0243 ③ 同一條規則）。
        /// <para>⛔ 空字串**不是**非法 —— 它是「不轉券」這個明確的關閉手段。呼叫端自己先判空。</para>
        /// </summary>
        public static bool IsValidVoucherType(string? iId) => IsValidAccountId(iId);

        public static int ClampPermille(int iValue)
            => iValue < MinFeePermille ? MinFeePermille : (iValue > MaxFeePermille ? MaxFeePermille : iValue);

        public sealed class Reading
        {
            public string CentralBank = SCP_TreasuryRequests.DefaultCentralBank;
            public int Threshold = DefaultThreshold;
            public int FeePermille = DefaultFeePermille;
            public bool ExemptCentral = true;
            public int MailFee = DefaultMailFee;

            /// <summary>保管費轉成哪一種券。空＝不轉券。缺設定時＝區域 id。</summary>
            public string VoucherType = "";
            /// <summary>1 Token 換幾張券。0＝不轉券。缺設定時＝<see cref="DefaultVoucherRatio"/>。</summary>
            public int VoucherRatio = 0;

            /// <summary>
            /// 這兩格**各自**是哪裡來的：`區域預設`／`設定檔`／`設定檔(不合法⇒關)`。
            /// <para>🩸 為什麼要留這一欄：預設值改成「會發券」之後，
            /// **「沒有人設定過」與「有人決定要發」在 `VoucherEnabled` 上同形** ——
            /// 而它乘上去的是扣繳額。⇒ 至少要讓讀報告的人一眼看出那個券種是誰決定的。</para>
            /// </summary>
            public string VoucherTrace = "";

            /// <summary>
            /// 這份設定會不會發券。
            /// <para>⚠ 判準是**兩個條件都要成立**：券種非空 ＋ 比例 &gt; 0。
            /// ⭐ 2026-09-22 起兩格都有預設值（區域 id ／ 1）⇒ **預設是「會發」**（Tim 拍板）；
            /// 關掉要顯式寫 `voucher_type: ""` 或 `ratio_per_token: 0`。</para>
            /// </summary>
            public bool VoucherEnabled => VoucherType.Length > 0 && VoucherRatio > 0;

            /// <summary>費率的顯示字串：50‰ → "5"、25‰ → "2.5"。</summary>
            public string FeeRateDisplay => (FeePermille % 10 == 0)
                ? (FeePermille / 10).ToString()
                : (FeePermille / 10.0).ToString("0.#");
        }


        // ==========================================================
        // 區塊職責：券那兩格的**預設值**（Tim 2026-09-22：券種＝區域 id、比例＝1）。
        // ⚠ 它被三條早退路徑與正常路徑**共用** —— 規則只有一份，⛔ 不在呼叫點各寫一次。
        // ==========================================================
        static void ApplyVoucherDefaults(string iDataRoot, Reading oOut, string iWhy)
        {
            string aRegion = SCP_BankRegion.Read(iDataRoot, out _);
            oOut.VoucherType = IsValidVoucherType(aRegion) ? aRegion : "";
            oOut.VoucherRatio = DefaultVoucherRatio;
            oOut.VoucherTrace = $"券種＝**區域預設**（`{aRegion}`；{iWhy}）／比例＝**預設 {DefaultVoucherRatio}**";
        }

        /// <summary>把 trace 的「券種＝…」或「比例＝…」那一半換掉（另一半原樣留著）。</summary>
        static string ReplaceTrace(string iTrace, string? iTypePart = null, string? iRatioPart = null)
        {
            string[] aParts = iTrace.Split('／');
            string aType = aParts.Length > 0 ? aParts[0] : "";
            string aRatio = aParts.Length > 1 ? aParts[1] : "";
            if (iTypePart != null) aType = iTypePart;
            if (iRatioPart != null) aRatio = iRatioPart;
            return aType + "／" + aRatio;
        }

        public static Reading Read(string iDataRoot, out string? oWhy)
        {
            oWhy = null;
            var aOut = new Reading();
            // ⚠ 三條「讀不到設定檔」的早退也要帶**同一組**券預設值 ——
            //   ⛔ 不可以只在成功那條路上補：那樣「設定檔壞掉」會安靜地變成「不發券」，
            //     而那是一個完全合法、看不出是壞掉的狀態（判準②：夾值兩側都做，同一族）。
            ApplyVoucherDefaults(iDataRoot, aOut, "設定檔沒讀到");
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

            // ── 保管費轉券（Tim 2026-09-22 改判：缺省＝**區域 id × 1**，不再是「不轉券」）──
            //   🩸 前一版的判準是「預設必須是那個不會造成任何後果的值」，理由是
            //     『沒有人設定過』與『有人決定要發』會在讀數上同形，而後果是憑空鑄券。
            //     ⇒ 那個顧慮**沒有消失**，只是 Tim 選了另一邊（每一區的儲備券跟著該區貨幣走，
            //       不必逐區設定）。⇒ 代價由 `VoucherTrace` 扛：它明說這個券種是**誰決定的**，
            //       而報告與廣播都印它。⛔ 別把 trace 當裝飾拿掉 —— 它是這次改判的對價。
            //   ⚠ 判「有沒有這一格」用 `Contains`，⛔ 不是拿 `GetString` 的回傳值比空字串：
            //     顯式寫 `""`（我要關掉）與整格不存在（沒人設定過）**必須分得出來**，
            //     而它們在 `GetString` 的回傳值上同形。
            if (aJd.Contains(KeyVoucherType))
            {
                string aVt = aJd.GetString(KeyVoucherType, "").Trim();
                // 非法券名（含路徑分隔字元之類）⇒ 當成關掉。券名就是檔名，這條與發券端同一把尺。
                bool aOk = IsValidVoucherType(aVt);
                aOut.VoucherType = aOk ? aVt : "";
                aOut.VoucherTrace = ReplaceTrace(aOut.VoucherTrace,
                    iTypePart: aOk ? "券種＝設定檔" : $"券種＝設定檔的值不合法（`{aVt}`）⇒ **關**");
            }
            // 沒有這一格 ⇒ 早退前塞的區域預設**原樣留著**（⛔ 不在這裡再算一次，那是第二份規則）

            if (aJd.Contains(KeyVoucherRatio))
            {
                aOut.VoucherRatio = ClampVoucherRatio(aJd.GetInt(KeyVoucherRatio, DefaultVoucherRatio));
                aOut.VoucherTrace = ReplaceTrace(aOut.VoucherTrace, iRatioPart: "比例＝設定檔");
            }
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
