// 區塊職責：**投資組合帳**（TASK-0371）—— 交易事件簿、開帳快照、以加權平均成本重算持倉報酬。
// 物理意義：券簿只存狀態不存事件（2026-09-18 拍板「券不記歷史」），兌換的成交率也從不落盤 ⇒
//           現有資料推不出任何人的買入成本。本檔在券簿**之外**另開一本只增不改的事件簿，⛔ 不動券簿 schema。
//           事件簿跟券簿一樣**住在那個人的信件夾**（券跨專案走，成本來源也要跟著走）：
//             · `letters/<P>/portfolio/events/<yyyy-MM-dd>/<stamp>_<persona>_<kind>_<hex>.json` —— 一筆一檔，同名拒寫
//             · `letters/<P>/portfolio/opening.json` —— 這個人的開帳快照，**只寫一次**：開帳那一刻的現值就是既有持倉的成本
//               （Tim 2026-10-01：「既有持倉可以根據新系統上線時的現值計算（假如無法推算原成本）」）
//           報酬一律由「快照 ＋ 之後的事件」**每次重算**，不另存一份持倉狀態 —— 存了就是第二份真相，兩份會漂。
//           資料根的 `Market/portfolio/` 是舊落點：⛔ 不再讀寫，只由 `MigrateLegacy` 搬進信件夾。
// 數值影響：只寫 `letters/<P>/portfolio/`；⛔ 不碰券簿、不碰銀行帳。金額一律 decimal、以原文數字落盤（不經 double）。
// 🩸 守衛：
//   ① **只記有報價的券**：沒有報價就沒有成本可言，而酒館券這類每則發文都會動 —— 全記的話一則訊息一個檔。
//      代價已知：哪天某券補上報價，之前的進出沒有紀錄 ⇒ 會出現在「帳上與紀錄不符」那一格（見③），不會被當成 0 成本。
//   ② **寫事件失敗不回頭推翻已成立的券異動**（券已落盤，回捲比漏記更危險）—— 但回傳的錯誤字串呼叫端必須印出來。
//   ③ **帳上數量 ≠ 紀錄數量時，差額單獨列出、不計入報酬**：差額的來源與成本都不知道，
//      ⛔ 把它算成 0 成本的話報酬率會無限大，看起來像賺翻了。
// @doc-sync: <SCP_Core>/Docs~/Portfolio.md（事件檔格式、成本規則、部署順序）
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Voucher;

namespace SCP.Core.Market
{
    /// <summary>事件簿裡的一筆。<see cref="Kind"/> 決定哪些欄位有意義。</summary>
    public sealed class SCP_PortfolioEvent
    {
        public const string KindSwap = "swap";
        public const string KindFlow = "flow";

        public string Kind = "";
        public DateTime AtUtc;
        public string Persona = "";

        // flow：某券增減（發券／花用／遷入）
        public string Symbol = "";
        public long DeltaE8;

        // swap：賣 From 買 To
        public string FromSymbol = "";
        public long FromE8;
        public string ToSymbol = "";
        public long ToE8;

        /// <summary>成交當下的 USD 單價（flow＝該券 Bid；swap＝From 的 Bid）。</summary>
        public decimal PriceUsd;

        /// <summary>swap：To 的 Ask。flow 不用。</summary>
        public decimal ToAskUsd;

        /// <summary>swap：兩腿手續費乘數（1 ＝ 免費）。</summary>
        public decimal FeeFactor = 1m;

        /// <summary>這筆的 USD 價值：flow＝|Δ|×Bid；swap＝賣出量×From Bid（＝放棄掉的市值，也就是買進那一側的成本）。</summary>
        public decimal ValueUsd;

        public string Source = "";
        public string Ref = "";

        /// <summary>讀回時填：事件檔名（排序的第二鍵，同一毫秒的兩筆才分得出先後）。</summary>
        public string FileName = "";
    }

    /// <summary>開帳快照裡的一個持倉。</summary>
    public sealed class SCP_PortfolioOpeningPosition
    {
        public string Persona = "";
        public string Symbol = "";
        public long UnitsE8;
        public decimal BidUsd;
        public decimal ValueUsd;
    }

    public sealed class SCP_PortfolioOpening
    {
        public DateTime AtUtc;
        public List<SCP_PortfolioOpeningPosition> Positions = new List<SCP_PortfolioOpeningPosition>();

        /// <summary>沒有報價、因此沒進快照的持倉（「persona/券」），給人看的 —— 不參與計算。</summary>
        public List<string> Unquoted = new List<string>();

        /// <summary>這次不開帳的人（「persona（理由）」）：已經開過帳、或已經有交易紀錄。給人看的。</summary>
        public List<string> Skipped = new List<string>();

        /// <summary>成本基準的說明（落盤）。</summary>
        public string Basis = "開帳時現值（Tim 2026-10-01 拍板：無法推算原成本的既有持倉以此為成本）";
    }

    /// <summary>某 persona 某券的重算結果。</summary>
    public sealed class SCP_PortfolioPosition
    {
        public string Symbol = "";

        /// <summary>券簿上實際有多少（永久＋未過期限時＋零頭），單位 1e-8。</summary>
        public long ActualE8;

        /// <summary>依「快照＋事件」推出來應該有多少。</summary>
        public long TrackedE8;

        /// <summary>手上這些（TrackedE8）的總成本，USD。</summary>
        public decimal CostUsd;

        /// <summary>已實現損益（賣出時：成交市值 − 平均成本），USD。</summary>
        public decimal RealizedUsd;

        public bool HasOpeningBasis;
        public bool HasActualBasis;

        /// <summary>賣出／花用時，有一部分超出紀錄數量（那部分的成本不知道，沒有算進已實現）。</summary>
        public long UntrackedDisposedE8;

        public bool Quoted;
        public decimal BidUsd;

        public long HeldTrackedE8 => TrackedE8 <= 0 ? 0 : Math.Min(TrackedE8, Math.Max(ActualE8, 0));
        public long DriftE8 => ActualE8 - TrackedE8;

        public decimal CostHeldUsd
            => TrackedE8 <= 0 ? 0m : CostUsd * HeldTrackedE8 / TrackedE8;

        public decimal ValueHeldUsd => (decimal)HeldTrackedE8 / SCP_VoucherBook.FractionScale * BidUsd;

        public decimal ActualValueUsd => (decimal)Math.Max(ActualE8, 0) / SCP_VoucherBook.FractionScale * BidUsd;

        public decimal UnrealizedUsd => ValueHeldUsd - CostHeldUsd;

        /// <summary>未實現報酬率；成本為 0（沒有可算的持倉）時是 null —— ⛔ 不回 0，0% 是一個讀數。</summary>
        public decimal? Roi => CostHeldUsd > 0m ? UnrealizedUsd / CostHeldUsd : (decimal?)null;

        /// <summary>成本來源標籤（給人看）。</summary>
        public string BasisLabel
            => HasOpeningBasis && HasActualBasis ? "混合（含上線時估值）"
             : HasOpeningBasis ? "上線時估值"
             : HasActualBasis ? "實際成本"
             : "無紀錄";
    }

    public sealed class SCP_PortfolioView
    {
        public string Persona = "";
        public bool OpeningExists;
        public DateTime OpeningAtUtc;
        public List<SCP_PortfolioPosition> Positions = new List<SCP_PortfolioPosition>();

        /// <summary>沒有報價的持倉：（券名, 數量 1e-8）。</summary>
        public List<KeyValuePair<string, long>> Unquoted = new List<KeyValuePair<string, long>>();

        /// <summary>該 persona 的事件（新 → 舊）。</summary>
        public List<SCP_PortfolioEvent> Events = new List<SCP_PortfolioEvent>();

        public List<string> Problems = new List<string>();

        public decimal TotalCostHeldUsd { get { decimal s = 0m; foreach (var p in Positions) s += p.CostHeldUsd; return s; } }
        public decimal TotalValueHeldUsd { get { decimal s = 0m; foreach (var p in Positions) s += p.ValueHeldUsd; return s; } }
        public decimal TotalRealizedUsd { get { decimal s = 0m; foreach (var p in Positions) s += p.RealizedUsd; return s; } }
    }

    /// <summary><see cref="SCP_Portfolio.MigrateLegacy"/> 的結果（試算與實搬同形）。</summary>
    public sealed class SCP_PortfolioMigration
    {
        public bool Applied;
        public string LegacyDir = "";
        public bool LegacyExists;
        public bool AlreadyMarked;
        public int EventsCopied;
        public int EventsAlready;
        public Dictionary<string, int> EventsByPersona = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        public List<string> OpeningsWritten = new List<string>();
        public List<string> OpeningsAlready = new List<string>();
        /// <summary>信件夾裡沒有這個人 ⇒ 沒搬（舊檔還在）。不擋標記檔。</summary>
        public List<string> SkippedNoPersona = new List<string>();
        /// <summary>衝突／讀不了／寫失敗 —— 有任何一筆就不寫標記檔。</summary>
        public List<string> Problems = new List<string>();
        public bool MarkerWritten;
    }

    public static class SCP_Portfolio
    {
        public const string EventsDirName = "events";
        public const string OpeningFileName = "opening.json";
        /// <summary>舊落點搬完之後留在資料根的標記檔（記搬了什麼、什麼時候）。</summary>
        public const string LegacyMigratedMarker = "_migrated_to_letters.json";
        const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

        public static string PortfolioDir(SCP_LettersRoot iLetters, string iPersona) => SCP_LettersPaths.PortfolioDir(iLetters, iPersona);
        public static string EventsDir(SCP_LettersRoot iLetters, string iPersona) => Path.Combine(PortfolioDir(iLetters, iPersona), EventsDirName);
        public static string OpeningPath(SCP_LettersRoot iLetters, string iPersona) => Path.Combine(PortfolioDir(iLetters, iPersona), OpeningFileName);

        /// <summary>舊落點（資料根 `Market/portfolio/`）—— ⛔ 只給 <see cref="MigrateLegacy"/> 與「還沒搬」的提示用。</summary>
        public static string LegacyPortfolioDir(string iDataRoot) => Path.Combine(SCP_MarketRateCache.MarketDir(iDataRoot), "portfolio");

        /// <summary>資料根裡還有沒搬的舊紀錄嗎（舊目錄在、而且沒有搬完的標記）。</summary>
        public static bool HasUnmigratedLegacy(string iDataRoot)
        {
            string aDir = LegacyPortfolioDir(iDataRoot);
            return Directory.Exists(aDir) && !File.Exists(Path.Combine(aDir, LegacyMigratedMarker));
        }

        // ⛔ 2026-10-07（TASK-0390）刪掉 `DataRootOfLetters`（letters 往上三層＝資料根）：信件根是可獨立設定的一格，
        //   反推只在它剛好等於慣例值時成立。資料根一律由宿主照設定給。

        /// <summary>券簿上「有多少」：永久＋未過期限時＋零頭，單位 1e-8。</summary>
        public static long UnitsE8Of(SCP_VoucherBook iBook, DateTime iNowUtc)
            => (long)(iBook.Permanent + iBook.ExpiringAlive(iNowUtc)) * SCP_VoucherBook.FractionScale + iBook.FractionalE8;

        static string Dec(decimal iV) => iV.ToString(CultureInfo.InvariantCulture);

        static decimal ReadDec(SCP_JsonData iJd, string iKey)
            => decimal.TryParse(iJd.GetString(iKey, "0"), NumberStyles.Float, CultureInfo.InvariantCulture, out decimal v) ? v : 0m;

        // ===========================================================
        // 區塊職責：寫事件。
        // ===========================================================

        /// <summary>記一筆兌換。回 null ＝ 已記；回字串 ＝ 沒記成（呼叫端要印出來，見守衛②）。</summary>
        public static string? RecordSwap(SCP_LettersRoot iLetters, string iPersona,
                                         string iFromSymbol, long iFromE8, decimal iFromBidUsd,
                                         string iToSymbol, long iToE8, decimal iToAskUsd, decimal iFeeFactor,
                                         DateTime iNowUtc, string iSource)
        {
            // 券名照原樣存（顯示用實際 ID）；比對一律不分大小寫。
            var e = new SCP_PortfolioEvent
            {
                Kind = SCP_PortfolioEvent.KindSwap,
                AtUtc = iNowUtc,
                Persona = iPersona,
                FromSymbol = iFromSymbol.Trim(),
                FromE8 = iFromE8,
                ToSymbol = iToSymbol.Trim(),
                ToE8 = iToE8,
                PriceUsd = iFromBidUsd,
                ToAskUsd = iToAskUsd,
                FeeFactor = iFeeFactor,
                ValueUsd = (decimal)iFromE8 / SCP_VoucherBook.FractionScale * iFromBidUsd,
                Source = iSource,
            };
            return Write(iLetters, e);
        }

        /// <summary>
        /// 記一筆某券的增減（發券／花用／遷入）。該券沒有報價 ⇒ **不記**、回 null（守衛①）。
        /// <paramref name="oRecorded"/> 分得出「記了」與「因為沒報價而跳過」。
        /// <para><paramref name="iDataRoot"/> 只用來讀匯率快取（報價是專案的設定）；事件寫進 <paramref name="iLetters"/>。</para>
        /// </summary>
        public static string? RecordFlow(string iDataRoot, SCP_LettersRoot iLetters, string iPersona, string iVoucher, long iDeltaE8,
                                         string iSource, string iRef, DateTime iNowUtc, out bool oRecorded)
        {
            oRecorded = false;
            if (iDeltaE8 == 0) return null;
            var aConfig = SCP_MarketRateCache.Load(iDataRoot, out string? aErr);
            if (aErr != null) return $"交易紀錄沒寫：讀不了匯率快取（{aErr}）";
            string aSym = iVoucher.Trim();
            if (!SCP_MarketRateCache.TryGetQuote(aConfig, aSym, out var aQ)) return null;

            var e = new SCP_PortfolioEvent
            {
                Kind = SCP_PortfolioEvent.KindFlow,
                AtUtc = iNowUtc,
                Persona = iPersona,
                Symbol = aSym,
                DeltaE8 = iDeltaE8,
                PriceUsd = aQ!.Bid,
                ValueUsd = (decimal)Math.Abs(iDeltaE8) / SCP_VoucherBook.FractionScale * aQ.Bid,
                Source = iSource,
                Ref = iRef,
            };
            string? aWriteErr = Write(iLetters, e);
            oRecorded = aWriteErr == null;
            return aWriteErr;
        }

        static string? Write(SCP_LettersRoot iLetters, SCP_PortfolioEvent e)
        {
            string aPath = "";
            try
            {
                DateTime aT = e.AtUtc.ToUniversalTime();
                string aDir = Path.Combine(EventsDir(iLetters, e.Persona), aT.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                if (!Directory.Exists(aDir)) Directory.CreateDirectory(aDir);
                // 檔名帶隨機尾碼：同一毫秒、同一人的兩筆（例：發券迴圈）不會撞名 —— 撞名時拒寫，不覆蓋。
                string aName = aT.ToString(StampFormat, CultureInfo.InvariantCulture) + "_" + SafeName(e.Persona) + "_" + e.Kind
                               + "_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json";
                aPath = Path.Combine(aDir, aName);
                if (File.Exists(aPath)) return $"交易紀錄沒寫：事件檔已存在（拒絕覆寫）：{aPath}";

                var j = SCP_JsonData.NewObject();
                j.Set("schema_version", SCP_JsonData.NewNumber(1));
                j.Set("kind", SCP_JsonData.NewString(e.Kind));
                j.Set("at_utc", SCP_JsonData.NewString(aT.ToString("o", CultureInfo.InvariantCulture)));
                j.Set("persona", SCP_JsonData.NewString(e.Persona));
                if (e.Kind == SCP_PortfolioEvent.KindSwap)
                {
                    j.Set("from", SCP_JsonData.NewString(e.FromSymbol));
                    j.Set("from_units_e8", SCP_JsonData.NewNumber(e.FromE8));
                    j.Set("from_bid_usd", SCP_JsonData.NewNumber(Dec(e.PriceUsd)));
                    j.Set("to", SCP_JsonData.NewString(e.ToSymbol));
                    j.Set("to_units_e8", SCP_JsonData.NewNumber(e.ToE8));
                    j.Set("to_ask_usd", SCP_JsonData.NewNumber(Dec(e.ToAskUsd)));
                    j.Set("fee_factor", SCP_JsonData.NewNumber(Dec(e.FeeFactor)));
                }
                else
                {
                    j.Set("symbol", SCP_JsonData.NewString(e.Symbol));
                    j.Set("delta_e8", SCP_JsonData.NewNumber(e.DeltaE8));
                    j.Set("bid_usd", SCP_JsonData.NewNumber(Dec(e.PriceUsd)));
                }
                j.Set("value_usd", SCP_JsonData.NewNumber(Dec(e.ValueUsd)));
                j.Set("source", SCP_JsonData.NewString(e.Source));
                if (e.Ref.Length > 0) j.Set("ref", SCP_JsonData.NewString(e.Ref));

                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(j) + "\n");
                File.Move(aTmp, aPath);
                return null;
            }
            catch (Exception ex)
            {
                return $"交易紀錄沒寫（{aPath}）：{ex.GetType().Name}: {ex.Message}";
            }
        }

        static string SafeName(string iPersona)
        {
            var aChars = iPersona.ToCharArray();
            for (int i = 0; i < aChars.Length; i++)
                if (!char.IsLetterOrDigit(aChars[i]) && aChars[i] != '-') aChars[i] = '-';
            return new string(aChars);
        }

        // ===========================================================
        // 區塊職責：讀事件。壞檔不吞：記進 problems（「讀不了」不等於「沒有這筆」）。
        // ===========================================================
        /// <summary>
        /// 讀事件。<paramref name="iPersona"/> ＝ null ⇒ 所有人（逐一掃 letters 根下每個人的 `portfolio/events/`）。
        /// </summary>
        public static List<SCP_PortfolioEvent> ReadEvents(SCP_LettersRoot iLetters, string? iPersona, List<string> oProblems)
        {
            var aOut = new List<SCP_PortfolioEvent>();
            var aPersonas = new List<string>();
            if (iPersona != null) aPersonas.Add(iPersona);
            else if (Directory.Exists(iLetters.Value))
                foreach (string d in Directory.GetDirectories(iLetters.Value)) aPersonas.Add(Path.GetFileName(d));

            foreach (string aPersona in aPersonas)
            {
                string aRoot = EventsDir(iLetters, aPersona);
                if (!Directory.Exists(aRoot)) continue;
                foreach (string aFile in Directory.GetFiles(aRoot, "*.json", SearchOption.AllDirectories))
                {
                    var e = ParseEventFile(aFile, oProblems);
                    if (e == null) continue;
                    // 檔在某人的信件夾、內容卻寫別人 ⇒ 喊出來，⛔ 不算進任何一邊（搬錯家的檔）
                    if (!string.Equals(e.Persona, aPersona, StringComparison.OrdinalIgnoreCase))
                    { oProblems.Add($"事件檔放在 `{aPersona}` 底下、內容卻是 `{e.Persona}`（略過）：{aFile}"); continue; }
                    aOut.Add(e);
                }
            }
            aOut.Sort((a, b) =>
            {
                int c = a.AtUtc.CompareTo(b.AtUtc);
                return c != 0 ? c : string.CompareOrdinal(a.FileName, b.FileName);
            });
            return aOut;
        }

        /// <summary>讀一個事件檔；讀不了或認不得 ⇒ 記進 problems、回 null（「讀不了」不等於「沒有這筆」）。</summary>
        static SCP_PortfolioEvent? ParseEventFile(string iFile, List<string> oProblems)
        {
            try
            {
                var j = SCP_JsonParser.Parse(File.ReadAllText(iFile));
                var e = new SCP_PortfolioEvent
                {
                    Kind = j.GetString("kind", ""),
                    Persona = j.GetString("persona", ""),
                    Source = j.GetString("source", ""),
                    Ref = j.GetString("ref", ""),
                    ValueUsd = ReadDec(j, "value_usd"),
                    FileName = Path.GetFileName(iFile),
                };
                if (!DateTime.TryParse(j.GetString("at_utc", ""), CultureInfo.InvariantCulture,
                                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out e.AtUtc))
                { oProblems.Add($"事件時間讀不出來（略過）：{iFile}"); return null; }
                if (e.Kind == SCP_PortfolioEvent.KindSwap)
                {
                    e.FromSymbol = j.GetString("from", "");
                    e.FromE8 = j.GetLong("from_units_e8", 0);
                    e.PriceUsd = ReadDec(j, "from_bid_usd");
                    e.ToSymbol = j.GetString("to", "");
                    e.ToE8 = j.GetLong("to_units_e8", 0);
                    e.ToAskUsd = ReadDec(j, "to_ask_usd");
                    e.FeeFactor = ReadDec(j, "fee_factor");
                }
                else if (e.Kind == SCP_PortfolioEvent.KindFlow)
                {
                    e.Symbol = j.GetString("symbol", "");
                    e.DeltaE8 = j.GetLong("delta_e8", 0);
                    e.PriceUsd = ReadDec(j, "bid_usd");
                }
                else { oProblems.Add($"認不得的事件種類 '{e.Kind}'（略過）：{iFile}"); return null; }
                return e;
            }
            catch (Exception ex)
            {
                oProblems.Add($"事件檔讀不了（略過，⚠ 這筆沒有算進報酬）：{iFile}：{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        // ===========================================================
        // 區塊職責：開帳快照 —— 開帳那一刻可估值持倉的現值，當作既有持倉的成本。**一人一份**（住在那個人的信件夾）。
        // ⚠ **每人只寫一次**：重拍等於把成本基準改到今天，之前的漲跌從帳上消失，而那不會報錯。
        // ⚠ 已經有交易紀錄的人**不開帳**：他的持倉從第一筆紀錄起就有實際成本，開帳會把那些成本換成今天的現值。
        // ===========================================================

        /// <summary>
        /// 算出開帳快照（純讀，零寫入）；寫入走 <see cref="TryWriteOpening"/>。
        /// 已有快照或已有交易紀錄的人列進 <see cref="SCP_PortfolioOpening.Skipped"/>、不進快照。
        /// </summary>
        public static SCP_PortfolioOpening BuildOpening(string iDataRoot, SCP_LettersRoot iLetters, DateTime iNowUtc,
                                                        List<string> oProblems)
        {
            var aOut = new SCP_PortfolioOpening { AtUtc = iNowUtc };
            var aConfig = SCP_MarketRateCache.Load(iDataRoot, out string? aErr);
            if (aErr != null) { oProblems.Add($"讀不了匯率快取：{aErr}"); return aOut; }

            foreach (string aPersona in SCP_PersonaProfile.PoolNames(iLetters.Value, w => oProblems.Add(w)))
            {
                if (File.Exists(OpeningPath(iLetters, aPersona))) { aOut.Skipped.Add(aPersona + "（已經開過帳）"); continue; }
                if (Directory.Exists(EventsDir(iLetters, aPersona))
                    && Directory.GetFiles(EventsDir(iLetters, aPersona), "*.json", SearchOption.AllDirectories).Length > 0)
                { aOut.Skipped.Add(aPersona + "（已有交易紀錄，持倉有實際成本）"); continue; }
                foreach (var kv in CollectHoldings(iLetters, aPersona, iNowUtc, oProblems))
                {
                    if (kv.Value <= 0) continue;
                    if (!SCP_MarketRateCache.TryGetQuote(aConfig, kv.Key, out var aQ))
                    { aOut.Unquoted.Add(aPersona + "/" + kv.Key); continue; }
                    aOut.Positions.Add(new SCP_PortfolioOpeningPosition
                    {
                        Persona = aPersona,
                        Symbol = kv.Key,
                        UnitsE8 = kv.Value,
                        BidUsd = aQ!.Bid,
                        ValueUsd = (decimal)kv.Value / SCP_VoucherBook.FractionScale * aQ.Bid,
                    });
                }
            }
            return aOut;
        }

        /// <summary>
        /// 把快照**按人拆開**寫進各自的信件夾。某人已經有快照 ⇒ 那一位跳過（⛔ 不重拍），其餘照寫。
        /// 回 false ＝ 至少一位寫失敗（<paramref name="oErrors"/> 列出是誰）。
        /// </summary>
        public static bool TryWriteOpening(SCP_LettersRoot iLetters, SCP_PortfolioOpening iOpening,
                                           out List<string> oWritten, out List<string> oErrors)
        {
            oWritten = new List<string>();
            oErrors = new List<string>();
            var aByPersona = new Dictionary<string, List<SCP_PortfolioOpeningPosition>>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in iOpening.Positions)
            {
                if (!aByPersona.TryGetValue(p.Persona, out var aList)) aByPersona[p.Persona] = aList = new List<SCP_PortfolioOpeningPosition>();
                aList.Add(p);
            }
            foreach (var kv in aByPersona)
            {
                string? aErr = WriteOneOpening(iLetters, kv.Key, iOpening.AtUtc, kv.Value, iOpening.Basis);
                if (aErr == null) oWritten.Add(kv.Key); else oErrors.Add(aErr);
            }
            return oErrors.Count == 0;
        }

        static string? WriteOneOpening(SCP_LettersRoot iLetters, string iPersona, DateTime iAtUtc,
                                       List<SCP_PortfolioOpeningPosition> iPositions, string iBasis)
        {
            string aPath = OpeningPath(iLetters, iPersona);
            try
            {
                if (File.Exists(aPath))
                    return $"`{iPersona}` 已經有開帳快照 ⇒ 拒絕重拍（重拍會把他的成本基準改到今天）：{aPath}";
                string aDir = PortfolioDir(iLetters, iPersona);
                if (!Directory.Exists(aDir)) Directory.CreateDirectory(aDir);

                var j = SCP_JsonData.NewObject();
                j.Set("schema_version", SCP_JsonData.NewNumber(1));
                j.Set("persona", SCP_JsonData.NewString(iPersona));
                j.Set("at_utc", SCP_JsonData.NewString(iAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
                j.Set("basis", SCP_JsonData.NewString(iBasis));
                var aArr = SCP_JsonData.NewArray();
                foreach (var p in iPositions)
                {
                    var o = SCP_JsonData.NewObject();
                    o.Set("persona", SCP_JsonData.NewString(p.Persona));
                    o.Set("symbol", SCP_JsonData.NewString(p.Symbol));
                    o.Set("units_e8", SCP_JsonData.NewNumber(p.UnitsE8));
                    o.Set("bid_usd", SCP_JsonData.NewNumber(Dec(p.BidUsd)));
                    o.Set("value_usd", SCP_JsonData.NewNumber(Dec(p.ValueUsd)));
                    aArr.Add(o);
                }
                j.Set("positions", aArr);

                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(j) + "\n");
                File.Move(aTmp, aPath);
                return null;
            }
            catch (Exception ex)
            {
                return $"`{iPersona}` 開帳快照寫入失敗（{aPath}）：{ex.GetType().Name}: {ex.Message}";
            }
        }

        /// <summary>讀某人的開帳快照。沒有檔 ⇒ 回 null 且 oError 為 null（「還沒開帳」不是錯）。</summary>
        public static SCP_PortfolioOpening? LoadOpening(SCP_LettersRoot iLetters, string iPersona, out string? oError)
            => LoadOpeningFile(OpeningPath(iLetters, iPersona), out oError);

        static SCP_PortfolioOpening? LoadOpeningFile(string iPath, out string? oError)
        {
            oError = null;
            string aPath = iPath;
            if (!File.Exists(aPath)) return null;
            try
            {
                var j = SCP_JsonParser.Parse(File.ReadAllText(aPath));
                var aOut = new SCP_PortfolioOpening();
                if (!DateTime.TryParse(j.GetString("at_utc", ""), CultureInfo.InvariantCulture,
                                       DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out aOut.AtUtc))
                { oError = $"開帳快照的時間讀不出來：{aPath}"; return null; }
                var aArr = j["positions"];
                for (int i = 0; i < aArr.Count; i++)
                {
                    var o = aArr[i];
                    aOut.Positions.Add(new SCP_PortfolioOpeningPosition
                    {
                        Persona = o.GetString("persona", ""),
                        Symbol = o.GetString("symbol", ""),
                        UnitsE8 = o.GetLong("units_e8", 0),
                        BidUsd = ReadDec(o, "bid_usd"),
                        ValueUsd = ReadDec(o, "value_usd"),
                    });
                }
                aOut.Basis = j.GetString("basis", aOut.Basis);
                // 舊落點的全員快照才有 unquoted（給人看的）；一人一份的快照沒有這一欄
                if (j.Contains("unquoted"))
                {
                    var aUnq = j["unquoted"];
                    for (int i = 0; i < aUnq.Count; i++) aOut.Unquoted.Add(aUnq[i].AsString());
                }
                return aOut;
            }
            catch (Exception ex)
            {
                oError = $"開帳快照讀不了（{aPath}）：{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        // ===========================================================
        // 區塊職責：把資料根的舊落點（`Market/portfolio/`）搬進各人的信件夾。
        // 物理意義：舊落點是**專案**的，每台機器／每個專案各有一份 ⇒ 每個資料根都要各搬一次，
        //          搬進同一個人的信件夾後由 git 會合（事件檔名帶時間戳＋隨機尾碼，跨機器不撞名）。
        // 數值影響：只**複製**，⛔ 不刪舊檔（舊檔在 AgentCommands 的版控裡，刪不刪由人決定）。
        //          目標已存在：內容相同 ⇒ 算已搬；內容不同 ⇒ 衝突，⛔ 不覆寫。
        //          開帳快照按人拆：那個人已經有快照 ⇒ 時間相同算已搬，不同算衝突（⛔ 不重拍）。
        //          信件夾裡沒有這個人 ⇒ 跳過並列出（舊檔還在，沒有東西遺失）。
        //          全部沒有衝突、沒有讀不了的檔 ⇒ 才寫標記檔；有標記的舊落點不再提示。
        // ===========================================================
        public static SCP_PortfolioMigration MigrateLegacy(string iDataRoot, SCP_LettersRoot iLetters, bool iApply, DateTime iNowUtc)
        {
            var m = new SCP_PortfolioMigration { Applied = iApply };
            string aLegacy = LegacyPortfolioDir(iDataRoot);
            m.LegacyDir = aLegacy;
            if (!Directory.Exists(aLegacy)) return m;
            m.LegacyExists = true;
            m.AlreadyMarked = File.Exists(Path.Combine(aLegacy, LegacyMigratedMarker));

            string aEvRoot = Path.Combine(aLegacy, EventsDirName);
            if (Directory.Exists(aEvRoot))
            {
                foreach (string aFile in Directory.GetFiles(aEvRoot, "*.json", SearchOption.AllDirectories))
                {
                    var e = ParseEventFile(aFile, m.Problems);
                    if (e == null) continue;
                    if (!Directory.Exists(SCP_LettersPaths.PersonaDir(iLetters, e.Persona)))
                    { m.SkippedNoPersona.Add($"{e.Persona}：{e.FileName}"); continue; }
                    string aDay = e.AtUtc.ToUniversalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                    string aTarget = Path.Combine(EventsDir(iLetters, e.Persona), aDay, e.FileName);
                    if (File.Exists(aTarget))
                    {
                        if (SameBytes(aFile, aTarget)) m.EventsAlready++;
                        else m.Problems.Add($"衝突：目標已有同名、內容不同的事件檔（⛔ 沒覆寫）：{aTarget}");
                        continue;
                    }
                    if (iApply)
                    {
                        try
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(aTarget)!);
                            File.Copy(aFile, aTarget, false);
                        }
                        catch (Exception ex) { m.Problems.Add($"複製失敗：{aFile} → {aTarget}：{ex.GetType().Name}: {ex.Message}"); continue; }
                    }
                    m.EventsCopied++;
                    m.EventsByPersona.TryGetValue(e.Persona, out int n);
                    m.EventsByPersona[e.Persona] = n + 1;
                }
            }

            var aOpen = LoadOpeningFile(Path.Combine(aLegacy, OpeningFileName), out string? aOpenErr);
            if (aOpenErr != null) m.Problems.Add(aOpenErr);
            if (aOpen != null)
            {
                var aByPersona = new Dictionary<string, List<SCP_PortfolioOpeningPosition>>(StringComparer.OrdinalIgnoreCase);
                foreach (var p in aOpen.Positions)
                {
                    if (!aByPersona.TryGetValue(p.Persona, out var aList)) aByPersona[p.Persona] = aList = new List<SCP_PortfolioOpeningPosition>();
                    aList.Add(p);
                }
                foreach (var kv in aByPersona)
                {
                    if (!Directory.Exists(SCP_LettersPaths.PersonaDir(iLetters, kv.Key)))
                    { m.SkippedNoPersona.Add($"{kv.Key}：開帳快照"); continue; }
                    var aExisting = LoadOpening(iLetters, kv.Key, out string? aExErr);
                    if (aExErr != null) { m.Problems.Add(aExErr); continue; }
                    if (aExisting != null)
                    {
                        if (aExisting.AtUtc == aOpen.AtUtc) m.OpeningsAlready.Add(kv.Key);
                        else m.Problems.Add($"衝突：`{kv.Key}` 已經有開帳快照（{aExisting.AtUtc:o}），舊落點的是 {aOpen.AtUtc:o} ⇒ 保留既有的，⛔ 沒重拍");
                        continue;
                    }
                    if (iApply)
                    {
                        string? aErr = WriteOneOpening(iLetters, kv.Key, aOpen.AtUtc, kv.Value, aOpen.Basis);
                        if (aErr != null) { m.Problems.Add(aErr); continue; }
                    }
                    m.OpeningsWritten.Add(kv.Key);
                }
            }

            if (iApply && m.Problems.Count == 0)
            {
                try
                {
                    var j = SCP_JsonData.NewObject();
                    j.Set("schema_version", SCP_JsonData.NewNumber(1));
                    j.Set("migrated_at_utc", SCP_JsonData.NewString(iNowUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
                    j.Set("letters_root", SCP_JsonData.NewString(iLetters.Value));
                    j.Set("events_copied", SCP_JsonData.NewNumber(m.EventsCopied));
                    j.Set("events_already", SCP_JsonData.NewNumber(m.EventsAlready));
                    j.Set("openings_written", SCP_JsonData.NewNumber(m.OpeningsWritten.Count));
                    j.Set("skipped_no_persona", SCP_JsonData.NewNumber(m.SkippedNoPersona.Count));
                    j.Set("note", SCP_JsonData.NewString("本目錄已搬進 letters/<P>/portfolio/，不再讀寫；舊檔留著當存檔。"));
                    File.WriteAllText(Path.Combine(aLegacy, LegacyMigratedMarker), SCP_JsonWriter.Write(j) + "\n");
                    m.MarkerWritten = true;
                }
                catch (Exception ex) { m.Problems.Add($"標記檔寫入失敗：{ex.GetType().Name}: {ex.Message}"); }
            }
            return m;
        }

        static bool SameBytes(string iA, string iB)
        {
            try
            {
                byte[] a = File.ReadAllBytes(iA), b = File.ReadAllBytes(iB);
                if (a.Length != b.Length) return false;
                for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 某人手上每一種券。鍵＝**券檔的實際檔名**（顯示用，例 `Gold`）；合併不分大小寫 ——
        /// 券檔名大小寫曾經漂過（Gold.json／gold.json），兩本算同一種、鍵取先看到的那個寫法。
        /// </summary>
        public static Dictionary<string, long> CollectHoldings(SCP_LettersRoot iLetters, string iPersona, DateTime iNowUtc,
                                                               List<string> oProblems)
        {
            var aOut = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string aName in SCP_VoucherStore.ListVouchers(iLetters, iPersona))
            {
                var aBook = SCP_VoucherStore.Load(iLetters, iPersona, aName, out string? aProblem);
                if (aProblem != null) { oProblems.Add($"`{iPersona}` 的 `{aName}` 讀不了（⚠ 沒有算進來）：{aProblem}"); continue; }
                aOut.TryGetValue(aName, out long aPrev);
                aOut[aName] = aPrev + UnitsE8Of(aBook, iNowUtc);   // 不分大小寫的字典：已有 `gold` 時沿用原鍵
            }
            return aOut;
        }

        // ===========================================================
        // 區塊職責：重算某 persona 的持倉與報酬（加權平均成本）。純讀。
        // 規則：
        //   · 起點＝開帳快照裡這個人的持倉（成本＝當時現值，標「上線時估值」）；早於快照時刻的事件不算（已含在快照裡）。
        //   · 增加（發券／買進）：數量＋、成本＋當時市值。
        //   · 賣出（兌換的來源側）：依平均成本扣成本，已實現＝成交市值 − 平均成本。
        //   · 花用（consume）：依平均成本扣成本，⛔ 不算已實現（那不是賣掉）。
        //   · 賣／花超過紀錄數量的部分：成本不知道 ⇒ 只扣到紀錄數量，超出的記在 UntrackedDisposedE8。
        // ===========================================================
        /// <param name="iDataRoot">只用來提示「資料根還有沒搬的舊紀錄」；帳本身全部從 <paramref name="iLetters"/> 讀。</param>
        public static SCP_PortfolioView Build(string iDataRoot, SCP_LettersRoot iLetters, string iPersona,
                                              SCP_MarketRateConfig iConfig, DateTime iNowUtc)
        {
            var v = new SCP_PortfolioView { Persona = iPersona };
            // ⚠ 這一格要喊在最前面：沒搬的舊紀錄會讓下面每一格「與紀錄不符」都看起來像真的來源不明
            if (HasUnmigratedLegacy(iDataRoot))
                v.Problems.Add($"資料根還有沒搬的舊交易紀錄（`{LegacyPortfolioDir(iDataRoot)}`）⇒ 那些紀錄現在**沒有算進來**，"
                               + "差額會被列成「與紀錄不符」。搬家：`portfolio --arg op=migrate`（先試算，`confirm=1` 才搬）");

            // 鍵不分大小寫；顯示名先取事件／快照裡的寫法，券簿上有的話改用券檔的實際檔名（例 `Gold`）
            var aPos = new Dictionary<string, SCP_PortfolioPosition>(StringComparer.OrdinalIgnoreCase);
            SCP_PortfolioPosition P(string iSym)
            {
                string k = iSym.Trim();
                if (!aPos.TryGetValue(k, out var p)) { p = new SCP_PortfolioPosition { Symbol = k }; aPos[k] = p; }
                return p;
            }

            var aOpening = LoadOpening(iLetters, iPersona, out string? aOpenErr);
            if (aOpenErr != null) v.Problems.Add(aOpenErr);
            DateTime aSince = DateTime.MinValue;
            if (aOpening != null)
            {
                v.OpeningExists = true;
                v.OpeningAtUtc = aOpening.AtUtc;
                aSince = aOpening.AtUtc;
                foreach (var op in aOpening.Positions)
                {
                    if (!string.Equals(op.Persona, iPersona, StringComparison.OrdinalIgnoreCase)) continue;
                    var p = P(op.Symbol);
                    p.TrackedE8 += op.UnitsE8;
                    p.CostUsd += op.ValueUsd;
                    p.HasOpeningBasis = true;
                }
            }

            var aEvents = ReadEvents(iLetters, iPersona, v.Problems);
            foreach (var e in aEvents)
            {
                if (e.AtUtc < aSince) continue;
                if (e.Kind == SCP_PortfolioEvent.KindFlow)
                {
                    if (e.DeltaE8 > 0) Add(P(e.Symbol), e.DeltaE8, e.ValueUsd);
                    else Remove(P(e.Symbol), -e.DeltaE8, null);
                }
                else
                {
                    Remove(P(e.FromSymbol), e.FromE8, e.ValueUsd);
                    Add(P(e.ToSymbol), e.ToE8, e.ValueUsd);
                }
            }
            for (int i = aEvents.Count - 1; i >= 0; i--) v.Events.Add(aEvents[i]);

            // 券簿上的實際數量 ＋ 現價
            foreach (var kv in CollectHoldings(iLetters, iPersona, iNowUtc, v.Problems))
            {
                if (SCP_MarketRateCache.TryGetQuote(iConfig, kv.Key, out _))
                {
                    var p = P(kv.Key);
                    p.ActualE8 += kv.Value;
                    p.Symbol = kv.Key;   // 券簿上的實際 ID 優先（舊紀錄裡的券名是全大寫）
                }
                else if (kv.Value > 0) v.Unquoted.Add(new KeyValuePair<string, long>(kv.Key, kv.Value));
            }
            foreach (var p in aPos.Values)
            {
                if (SCP_MarketRateCache.TryGetQuote(iConfig, p.Symbol, out var q)) { p.Quoted = true; p.BidUsd = q!.Bid; }
                if (p.ActualE8 == 0 && p.TrackedE8 == 0 && p.RealizedUsd == 0m) continue;
                v.Positions.Add(p);
            }
            v.Positions.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Symbol, b.Symbol));

            // 交易紀錄的券名也顯示成實際 ID（舊紀錄落盤時是全大寫）—— 只改這份讀出來的副本，⛔ 不動事件檔
            string Display(string iSym) => aPos.TryGetValue(iSym, out var p) ? p.Symbol : iSym;
            foreach (var e in v.Events)
            {
                if (e.Symbol.Length > 0) e.Symbol = Display(e.Symbol);
                if (e.FromSymbol.Length > 0) e.FromSymbol = Display(e.FromSymbol);
                if (e.ToSymbol.Length > 0) e.ToSymbol = Display(e.ToSymbol);
            }
            return v;
        }

        static void Add(SCP_PortfolioPosition p, long iE8, decimal iCostUsd)
        {
            if (iE8 <= 0) return;
            p.TrackedE8 += iE8;
            p.CostUsd += iCostUsd;
            p.HasActualBasis = true;
        }

        /// <param name="iProceedsUsd">賣出的成交市值；花用（不是賣）傳 null ⇒ 不算已實現。</param>
        static void Remove(SCP_PortfolioPosition p, long iE8, decimal? iProceedsUsd)
        {
            if (iE8 <= 0) return;
            long aTracked = Math.Min(iE8, Math.Max(p.TrackedE8, 0));
            if (aTracked < iE8) p.UntrackedDisposedE8 += iE8 - aTracked;
            if (aTracked <= 0) return;
            decimal aAvgCostPart = p.CostUsd * aTracked / p.TrackedE8;
            if (iProceedsUsd.HasValue)
                p.RealizedUsd += iProceedsUsd.Value * aTracked / iE8 - aAvgCostPart;
            p.CostUsd -= aAvgCostPart;
            p.TrackedE8 -= aTracked;
        }
    }
}
