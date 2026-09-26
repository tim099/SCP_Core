// 區塊職責：**券**的資料本體與讀寫（TASK-0243）—— 一個 persona、一種券、一個檔。
// 物理意義：`letters/<persona>/vouchers/<券名>.json`。券**跟著人走、跨區共用**，
//           ⛔ 沒有區的維度（跟隔壁 `bank/<區>.md` 刻意相反，理由見 `SCP_LettersPaths` 那一節）。
// 數值影響：一次寫入 ＝ 整個檔重寫（原子寫：tmp → replace）。
//
// 🩸 三格判準，每一格都有一個「不這樣做會怎樣」：
//
//   ① **券不記歷史**（Tim 2026-09-18 拍板）⇒ 存的是**狀態**不是事件。
//      ⚠ 這跟新銀行**正好相反**（那邊 append-only、餘額靠重放求和）。
//      代價要寫在這裡而不是埋著：**扣錯了無法稽核、無法回推** —— 只剩一個數字。
//      ⇒ 所以「**只有一個寫入端**」不是架構偏好，是這個設計成立的唯一前提。
//      本層擋不住有人在 Server 之外呼叫它；那道閘在 Cmd 那層（`ServerDelegateCmd`）。
//      📌 同一句話 `SCP_BankLedger` 檔頭也寫過 —— 兩邊的前提相同，而**券沒有歷史可以事後對帳**，
//        所以它比銀行更依賴那個前提。
//
//   ② **到期在讀取端過濾，⛔ 不在寫入端刪。**
//      判準：**寫入端省略不可逆，讀取端過濾可逆**。時鐘／時區／邊界判錯的時候，
//      過濾錯了改判準就回來；刪錯了那批券就真的沒了。
//      ⇒ 清理只發生在「本來就要寫這個檔」的時候，而且**回報清掉幾張**（⛔ 不靜默消失）。
//
//   ③ **消費先花最早到期的，再花永久券。**
//      反過來的話，限時券會在使用者「還有券」的感覺下默默過期 ——
//      而那個損失沒有任何一層會喊（它跟「他沒有花」在資料上同形）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Voucher
{
    /// <summary>一批券。<see cref="ExpiresAtUtc"/> 空 ＝ 永久（⛔ 永久券不走這個型別，見 <see cref="SCP_VoucherBook.Permanent"/>）。</summary>
    public sealed class SCP_VoucherBatch
    {
        /// <summary>**還剩幾張**（會被消費扣減）。</summary>
        public int Amount;

        // ===========================================================
        // 區塊職責：這一批**當初發了幾張** —— 發放後永不變動。
        // 物理意義：券**不記歷史**（Tim 2026-09-18 拍板）⇒ 「本場那 10 張用了幾張」
        //          在只有 `Amount` 的檔上**結構上答不出來**：剩 3 張時，
        //          「發 10 用 7」與「發 3 用 0」逐位元組相同。
        // 數值影響：只被 `op=usage` 讀。⛔ 不進入任何餘額算式 ——
        //          它是**發放量**不是餘額，混用會把已花掉的券算回去。
        // ⚠ 舊檔沒有這一欄 ⇒ 讀成 `Amount`（＝「一張都沒花」），
        //   那是**唯一不會憑空生出用量**的預設；⛔ 不預設 0，否則 used 會變成負值再被夾成 0，
        //   而那個 0 跟「真的沒花」同形。
        // ===========================================================
        public int Granted;

        public string ExpiresAtUtc = "";
        public string GrantedAtUtc = "";
        public string Source = "";
        public string Ref = "";

        public SCP_JsonData ToJson()
        {
            var aData = SCP_JsonData.NewObject();
            aData.Set("amount", SCP_JsonData.NewNumber(Amount));
            aData.Set("granted", SCP_JsonData.NewNumber(Granted > 0 ? Granted : Amount));
            aData.Set("expires_at_utc", SCP_JsonData.NewString(ExpiresAtUtc));
            aData.Set("granted_at_utc", SCP_JsonData.NewString(GrantedAtUtc));
            if (Source.Length > 0) aData.Set("source", SCP_JsonData.NewString(Source));
            if (Ref.Length > 0) aData.Set("ref", SCP_JsonData.NewString(Ref));
            return aData;
        }

        public static SCP_VoucherBatch FromJson(SCP_JsonData iData) => new SCP_VoucherBatch
        {
            Amount = iData.GetInt("amount", 0),
            // ⚠ 預設回 `amount`（不是 0）—— 見欄位註解：0 會讓 used 假裝成「沒花」。
            Granted = iData.GetInt("granted", iData.GetInt("amount", 0)),
            ExpiresAtUtc = iData.GetString("expires_at_utc", ""),
            GrantedAtUtc = iData.GetString("granted_at_utc", ""),
            Source = iData.GetString("source", ""),
            Ref = iData.GetString("ref", ""),
        };

        /// <summary>到期時刻解不出來時回 null —— ⛔ 那**不是**「永不過期」，呼叫端要自己決定怎麼辦。</summary>
        public DateTime? ExpiresAt()
        {
            if (string.IsNullOrWhiteSpace(ExpiresAtUtc)) return null;
            return DateTime.TryParse(ExpiresAtUtc, CultureInfo.InvariantCulture,
                                     DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                                     out DateTime aWhen)
                ? aWhen : (DateTime?)null;
        }
    }

    public sealed class SCP_VoucherBook
    {
        public string Persona = "";
        public string Voucher = "";

        /// <summary>永久券（不會過期）。**可用的就是這個**。</summary>
        public int Permanent;

        // ===========================================================
        // 區塊職責：**不足一張的零頭**（TASK-0271）。
        // 物理意義：均分除不盡時每人會分到帶小數的量（2900 ÷ 3 ＝ 966.666…）。
        //          零頭**不能用**，它只累積；⇒ 加總滿 1 才進位成一張可用的券。
        // 數值影響：⛔ **完全不進 `Spendable`**。使用端看不到它，也不該看到。
        //
        // 🩸 為什麼是**定點整數**而不是 double —— 而這條理由我第一次寫錯過，所以附實測：
        //   我原本舉的例子是「`0.5 + 0.3 != 0.8`」，⛔ **那是錯的**（IEEE754 上它剛好相等，
        //   我把經典的 `0.1 + 0.2 != 0.3` 記成別的了）。對照組當場打到我自己。
        //   ⭐ 而真正咬本功能的那一個更難看，量出來是這樣：
        //     `0.1` 累加 **10 次 ⇒ 0.9999999999999999**，`== 1.0` 是 **false**。
        //   ⇒ double 之下「十次 0.1」會停在 0.9999999999999999，差一點點進不了位。
        //   ⚠ 而**這個誤差 Tim 2026-09-22 判定可接受** —— ⛔ 所以我不拿它當「不做會出事」來賣。
        //   📌 選定點的實際理由只有一條，而它跟風險無關：**它沒有比較貴，而進位變成精確的**
        //     （`AddE8` 是兩個整數運算）。⇒ 同樣的效果，選前提少的那一個。
        //   （同一條判準 @gura 自己寫過，seq 19960：「以聰為整數單位，避免浮點漂移」。）
        //
        // ⚠ 欄位名帶單位（`fractional_e8`）**不是囉唆**：
        //   `"fractional": 50000000` 這個字面，讀成「0.5」與讀成「五千萬張」形狀完全一樣。
        //   ⛔ 而我們不另外寫一個好看的 `fractional: 0.5` —— 同一個事實兩個欄位會漂。
        // ===========================================================
        /// <summary>零頭，單位 1e-8（＝聰那一級）。恆為 <c>0 ≤ x &lt; Scale</c>。</summary>
        public long FractionalE8;

        /// <summary>零頭的刻度：1 張券 ＝ 1e8 個最小單位。</summary>
        public const long FractionScale = 100_000_000L;

        /// <summary>零頭的十進位值（**唯讀投影**，給人看的 —— ⛔ 不落盤）。</summary>
        public decimal FractionalValue => (decimal)FractionalE8 / FractionScale;

        // ===========================================================
        // 區塊職責：加一筆**帶零頭**的量，滿 1 就進位成可用券。
        // 數值影響：`Permanent` 只會被整數部分推進；剩下的留在零頭池。
        // ⚠ 只收非負 —— 「用掉零頭」這件事**不存在**（零頭不能用），
        //   所以這裡不提供減法入口，⛔ 免得有人拿它當扣款用。
        // ===========================================================
        public void AddE8(long iUnitsE8)
        {
            if (iUnitsE8 <= 0) return;
            long aTotal = FractionalE8 + iUnitsE8;
            Permanent += (int)(aTotal / FractionScale);
            FractionalE8 = aTotal % FractionScale;
        }

        /// <summary>限時券，**一次發放一批**、各自帶到期時刻（⛔ 不合併：兩批的到期時間不同）。</summary>
        public List<SCP_VoucherBatch> Expiring = new List<SCP_VoucherBatch>();

        /// <summary>最後一次寫入的時刻與**區** —— 券沒有歷史，這兩欄是唯一的「誰動過它」線索。</summary>
        public string UpdatedAtUtc = "";
        public string UpdatedRegion = "";

        /// <summary>已經跑過遷移的區（⇒ 同一區不會再加總第二次）。</summary>
        public List<string> MigratedRegions = new List<string>();

        public SCP_JsonData ToJson()
        {
            var aData = SCP_JsonData.NewObject();
            aData.Set("persona", SCP_JsonData.NewString(Persona));
            aData.Set("voucher", SCP_JsonData.NewString(Voucher));
            aData.Set("permanent", SCP_JsonData.NewNumber(Permanent));
            // ⚠ 零頭是 0 時**不落盤** —— 舊檔沒有這一欄，而「沒有這一欄」與「零頭是 0」同義。
            //   ⇒ 不寫它，舊檔與新檔在零頭上長得一樣，⛔ 不製造一個只是格式差異的 diff。
            if (FractionalE8 > 0) aData.Set("fractional_e8", SCP_JsonData.NewNumber(FractionalE8));
            var aArr = SCP_JsonData.NewArray();
            foreach (SCP_VoucherBatch aBatch in Expiring) aArr.Add(aBatch.ToJson());
            aData.Set("expiring", aArr);
            aData.Set("updated_at_utc", SCP_JsonData.NewString(UpdatedAtUtc));
            aData.Set("updated_region", SCP_JsonData.NewString(UpdatedRegion));
            var aMig = SCP_JsonData.NewArray();
            foreach (string aRegion in MigratedRegions) aMig.Add(SCP_JsonData.NewString(aRegion));
            aData.Set("migrated_regions", aMig);
            aData.Set("schema_version", SCP_JsonData.NewNumber(1));
            return aData;
        }

        public static SCP_VoucherBook FromJson(SCP_JsonData iData)
        {
            var aBook = new SCP_VoucherBook
            {
                Persona = iData.GetString("persona", ""),
                Voucher = iData.GetString("voucher", ""),
                Permanent = iData.GetInt("permanent", 0),
                // 舊檔沒有這一欄 ⇒ 0（＝沒有零頭）。這一格「缺席」與「0」**本來就同義**，
                // ⛔ 不需要分辨（與 `granted` 那格不同：那一格的 0 會憑空生出用量）。
                FractionalE8 = (long)iData.GetInt("fractional_e8", 0),
                UpdatedAtUtc = iData.GetString("updated_at_utc", ""),
                UpdatedRegion = iData.GetString("updated_region", ""),
            };
            SCP_JsonData aArr = iData["expiring"];
            if (aArr.Exists && aArr.IsArray)
                foreach (SCP_JsonData aItem in aArr) aBook.Expiring.Add(SCP_VoucherBatch.FromJson(aItem));
            SCP_JsonData aMig = iData["migrated_regions"];
            if (aMig.Exists && aMig.IsArray)
                foreach (SCP_JsonData aItem in aMig) aBook.MigratedRegions.Add(aItem.AsString());
            return aBook;
        }

        // ── 讀數（純函式，⛔ 不改自己）──────────────────────────────

        /// <summary>還沒過期的限時券張數（<paramref name="iNow"/> 一律 UTC）。</summary>
        public int ExpiringAlive(DateTime iNow)
        {
            int aSum = 0;
            foreach (SCP_VoucherBatch aBatch in Expiring)
                if (IsAlive(aBatch, iNow)) aSum += aBatch.Amount;
            return aSum;
        }

        /// <summary>
        /// 可花總額 ＝ 永久 ＋ 未過期限時。
        /// <para>⛔ **零頭不算在內**（TASK-0271 ③）—— 不足一張的東西不能花，
        /// 而把它加進來會讓「可花 3」實際上只花得出 2 張。</para>
        /// </summary>
        public int Spendable(DateTime iNow) => Permanent + ExpiringAlive(iNow);

        /// <summary>
        /// 這一批還活著嗎。
        /// <para>⚠ 到期時刻**解不出來**時視為**還活著**：那是資料壞了，
        /// 而把壞資料當成「已過期」＝ 靜靜沒收別人的券。⇒ 往不沒收的那一側倒。</para>
        /// </summary>
        public static bool IsAlive(SCP_VoucherBatch iBatch, DateTime iNow)
        {
            if (iBatch.Amount <= 0) return false;
            DateTime? aWhen = iBatch.ExpiresAt();
            if (aWhen == null) return true;
            return aWhen.Value > iNow;
        }

        // ===========================================================
        // 區塊職責：死掉的批次（花完／過期）在檔上**再留多久**才清。
        // 物理意義：`op=usage`（自由時間收工結算）答「本場那一批用了幾張」要讀批次自己的
        //          Granted／Amount。🩸 TASK-0302：以前批次一死就在**同一次寫入**被清掉 ——
        //          而「花完」本身就是一次寫入 ⇒ 最常見的「10 張全用完」那一場，
        //          收工時永遠只答得出「查無」。
        // 數值影響：**零**。死批次不進任何餘額算式（`ExpiringAlive`／`TryConsume` 都走 `IsAlive`），
        //          只是檔上多躺一筆 amount 0（或已過期）的紀錄。
        // ⚠ 這**不是**歷史（09-18「券不記歷史」不動）：留的是那一批自己的狀態，
        //   一批一筆、不記事件，到期滿 retention 照樣清掉 —— 之後 `op=usage` 仍然只答得出查無。
        // ⚠ 到期時刻解不出來的死批次（只可能是花完的）⇒ 沒有時間軸可以量「留多久」⇒ 照舊當場清。
        // ===========================================================
        public static readonly TimeSpan DeadBatchRetention = TimeSpan.FromHours(24);

        /// <summary>
        /// 某一批（按 <paramref name="iRef"/>）的用量：發了幾張／還剩幾張／其中還花得掉幾張。
        /// <para>🩸 回 <c>false</c> ＝ **「我不知道」**，三個 out 一律 0 ——
        /// 讓「發放量 − 0」這個減法在物理上拿不到數字（TASK-0195）。</para>
        /// </summary>
        public bool TryUsageByRef(string iRef, DateTime iNow, out int oGranted, out int oRemain, out int oAlive)
        {
            oGranted = 0; oRemain = 0; oAlive = 0;
            bool aFound = false;
            foreach (SCP_VoucherBatch aBatch in Expiring)
            {
                if (!string.Equals(aBatch.Ref, iRef, StringComparison.Ordinal)) continue;
                aFound = true;
                oGranted += aBatch.Granted > 0 ? aBatch.Granted : aBatch.Amount;
                oRemain += aBatch.Amount;
                if (IsAlive(aBatch, iNow)) oAlive += aBatch.Amount;
            }
            if (!aFound) { oGranted = 0; oRemain = 0; oAlive = 0; }
            return aFound;
        }

        /// <summary>這一批已經死了（花完或過期），但還在保留期內 ⇒ 寫入時先不清。</summary>
        public static bool IsRetainedDead(SCP_VoucherBatch iBatch, DateTime iNow)
        {
            if (IsAlive(iBatch, iNow)) return false;
            DateTime? aWhen = iBatch.ExpiresAt();
            if (aWhen == null) return false;
            return aWhen.Value + DeadBatchRetention > iNow;
        }
    }

    /// <summary>
    /// 券檔的讀寫。⛔ 不快取 —— 券檔很小，而快取要處理「別的 process 改了它」，
    /// 那正是這個設計唯一不能有的東西（見 <see cref="SCP_VoucherBook"/> 判準①）。
    /// </summary>
    public static class SCP_VoucherStore
    {
        public static string PathOf(SCP_LettersRoot iRoot, string iPersona, string iVoucher)
            => SCP_LettersPaths.VoucherPath(iRoot, iPersona, iVoucher);

        /// <summary>讀一本券；檔不在回一本**空的**（⛔ 不 throw —— 「沒有這種券」與「零張」在語意上同值）。</summary>
        public static SCP_VoucherBook Load(SCP_LettersRoot iRoot, string iPersona, string iVoucher, out string? oProblem)
        {
            oProblem = null;
            string aPath = PathOf(iRoot, iPersona, iVoucher);
            if (!File.Exists(aPath))
                return new SCP_VoucherBook { Persona = iPersona, Voucher = iVoucher };
            try
            {
                SCP_VoucherBook aBook = SCP_VoucherBook.FromJson(SCP_JsonParser.Parse(File.ReadAllText(aPath)));
                aBook.Persona = iPersona;
                aBook.Voucher = iVoucher;
                return aBook;
            }
            catch (Exception e)
            {
                // ⛔ 讀不了**不是**「零張」：後者是一個讀數，前者是「我不知道」。
                //   壓成同一個回傳值的話，一個壞掉的檔會讓人以為券花完了 —— 而下一步是去補發。
                oProblem = $"券檔讀不了（{aPath}）：{e.GetType().Name}: {e.Message}";
                return new SCP_VoucherBook { Persona = iPersona, Voucher = iVoucher };
            }
        }

        /// <summary>
        /// 寫回（原子）。順手清掉**已經死掉且過了保留期**的批次並回報清掉幾張
        /// （保留期見 <see cref="SCP_VoucherBook.DeadBatchRetention"/>，TASK-0302）。
        /// <para>⚠ 清理只在這裡發生 —— ⛔ 沒有另一支會刪東西的定時工。</para>
        /// </summary>
        public static bool Save(SCP_LettersRoot iRoot, SCP_VoucherBook iBook, DateTime iNow,
                                string iRegion, out int oDroppedAmount, out string? oError)
        {
            oError = null;
            oDroppedAmount = 0;

            var aKeep = new List<SCP_VoucherBatch>(iBook.Expiring.Count);
            foreach (SCP_VoucherBatch aBatch in iBook.Expiring)
            {
                if (SCP_VoucherBook.IsAlive(aBatch, iNow) || SCP_VoucherBook.IsRetainedDead(aBatch, iNow))
                { aKeep.Add(aBatch); continue; }
                if (aBatch.Amount > 0) oDroppedAmount += aBatch.Amount;   // 張數已歸零的不算「過期損失」
            }
            iBook.Expiring = aKeep;
            iBook.UpdatedAtUtc = iNow.ToString("o", CultureInfo.InvariantCulture);
            iBook.UpdatedRegion = iRegion ?? "";

            string aPath = PathOf(iRoot, iBook.Persona, iBook.Voucher);
            try
            {
                string? aDir = Path.GetDirectoryName(aPath);
                if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
                // 原子寫：tmp → replace。⚠ 直接覆寫的話，寫到一半斷電留下的是**半個 JSON**，
                //   而下一次讀它會走進「讀不了」那條 —— 而券沒有歷史可以回推。
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(iBook.ToJson()) + "\n");
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
            }
            catch (Exception e) { oError = $"券檔寫不進去（{aPath}）：{e.Message}"; return false; }
            return true;
        }

        /// <summary>這個人有哪幾種券（＝ `vouchers/` 底下的檔名）。⛔ 不建清單檔，目錄自己就是清單。</summary>
        public static List<string> ListVouchers(SCP_LettersRoot iRoot, string iPersona)
        {
            var aOut = new List<string>();
            string aDir = SCP_LettersPaths.VouchersDir(iRoot, iPersona);
            if (!Directory.Exists(aDir)) return aOut;
            foreach (string aFile in Directory.GetFiles(aDir, "*.json"))
                aOut.Add(Path.GetFileNameWithoutExtension(aFile));
            aOut.Sort(StringComparer.Ordinal);
            return aOut;
        }

        // ===========================================================
        // 區塊職責：消費 —— **先花最早到期的限時券，再花永久券**。
        // 🩸 反過來的話，限時券會在使用者「還有券」的感覺下默默過期，
        //   而那個損失跟「他沒有花」在資料上同形 —— 沒有任何一層會喊。
        // 數值影響：不足就**整筆不扣**（⛔ 不部分扣：部分扣之後呼叫端拿到的是
        //   「失敗」，而錢已經少了一半，那是最難查的一種）。
        // ===========================================================
        public static bool TryConsume(SCP_VoucherBook ioBook, int iAmount, DateTime iNow, out string? oWhy)
        {
            oWhy = null;
            if (iAmount <= 0) { oWhy = $"張數必須 > 0（收到 {iAmount}）"; return false; }
            int aSpendable = ioBook.Spendable(iNow);
            if (aSpendable < iAmount)
            {
                oWhy = $"券不足：可花 {aSpendable}（永久 {ioBook.Permanent} ＋ 未過期限時 "
                       + $"{ioBook.ExpiringAlive(iNow)}），要花 {iAmount}";
                return false;
            }

            var aAlive = new List<SCP_VoucherBatch>();
            foreach (SCP_VoucherBatch aBatch in ioBook.Expiring)
                if (SCP_VoucherBook.IsAlive(aBatch, iNow)) aAlive.Add(aBatch);
            // 最早到期的排前面；解不出到期時刻的排最後（它們被當成還活著，但不該優先被花掉）
            aAlive.Sort((a, b) =>
            {
                DateTime? aA = a.ExpiresAt(), aB = b.ExpiresAt();
                if (aA == null && aB == null) return 0;
                if (aA == null) return 1;
                if (aB == null) return -1;
                return aA.Value.CompareTo(aB.Value);
            });

            int aLeft = iAmount;
            foreach (SCP_VoucherBatch aBatch in aAlive)
            {
                if (aLeft <= 0) break;
                int aTake = Math.Min(aBatch.Amount, aLeft);
                aBatch.Amount -= aTake;
                aLeft -= aTake;
            }
            if (aLeft > 0) { ioBook.Permanent -= aLeft; aLeft = 0; }
            return true;
        }

        // ===========================================================
        // 區塊職責：遷移 —— 把**某一區**的舊券數量加總進來，並記下那一區已經遷過。
        // 🩸 血證（TASK-0238，2026-09-17，銀行那側同一族）：開帳快照與鏡像游標沒對齊
        //   ⇒ **2 筆錢被算兩次**，而兩邊都合法、`idem_key` 也不重複 ⇒ 沒有任何一層會喊。
        //   券遷移是「數量**加總**」⇒ 加兩次與加一次都是合法數字。
        // ⇒ 所以冪等鍵是**區名**，而且判斷寫在這裡（⛔ 不靠呼叫端記得）。
        // ⚠ 而標記要在**加完之後**跟著同一次寫入落盤 —— 先寫標記再加總的話，
        //   中途失敗留下的是「標記說遷過了、而券一張都沒加」，
        //   **而它之後永遠不會再跑一次**。
        // ===========================================================
        public static bool AlreadyMigrated(SCP_VoucherBook iBook, string iRegion)
        {
            foreach (string aRegion in iBook.MigratedRegions)
                if (string.Equals(aRegion, iRegion, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        public static void ApplyMigration(SCP_VoucherBook ioBook, string iRegion,
                                          int iPermanent, List<SCP_VoucherBatch> iExpiring)
        {
            if (iPermanent > 0) ioBook.Permanent += iPermanent;
            foreach (SCP_VoucherBatch aBatch in iExpiring)
                if (aBatch.Amount > 0) ioBook.Expiring.Add(aBatch);
            ioBook.MigratedRegions.Add(iRegion);
        }
    }
}
