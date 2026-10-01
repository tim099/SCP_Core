// 區塊職責：**投資組合帳**（TASK-0371）—— 交易事件簿、開帳快照、以加權平均成本重算持倉報酬。
// 物理意義：券簿只存狀態不存事件（2026-09-18 拍板「券不記歷史」），兌換的成交率也從不落盤 ⇒
//           現有資料推不出任何人的買入成本。本檔在券簿**之外**另開一本只增不改的事件簿，⛔ 不動券簿 schema：
//             · `Market/portfolio/events/<yyyy-MM-dd>/<stamp>_<persona>_<kind>_<hex>.json` —— 一筆一檔，同名拒寫
//             · `Market/portfolio/opening.json` —— 開帳快照，**只寫一次**：上線那一刻的現值就是既有持倉的成本
//               （Tim 2026-10-01：「既有持倉可以根據新系統上線時的現值計算（假如無法推算原成本）」）
//           報酬一律由「快照 ＋ 之後的事件」**每次重算**，不另存一份持倉狀態 —— 存了就是第二份真相，兩份會漂。
// 數值影響：只寫 `Market/portfolio/`；⛔ 不碰券簿、不碰銀行帳。金額一律 decimal、以原文數字落盤（不經 double）。
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

    public static class SCP_Portfolio
    {
        public const string PortfolioDirName = "portfolio";
        public const string EventsDirName = "events";
        public const string OpeningFileName = "opening.json";
        const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

        public static string PortfolioDir(string iDataRoot) => Path.Combine(SCP_MarketRateCache.MarketDir(iDataRoot), PortfolioDirName);
        public static string EventsDir(string iDataRoot) => Path.Combine(PortfolioDir(iDataRoot), EventsDirName);
        public static string OpeningPath(string iDataRoot) => Path.Combine(PortfolioDir(iDataRoot), OpeningFileName);

        /// <summary>
        /// 由 letters 根推資料根（letters ＝ `<資料根>/ChatTavern/baton/letters`）。
        /// ⚠ 這條推導原本只有 `Cmd_Voucher op=swap` 自己算一次；搬到這裡讓券的寫入端共用同一份，⛔ 不在各處各推一次。
        /// </summary>
        public static string DataRootOfLetters(SCP_LettersRoot iLetters)
            => Path.GetFullPath(Path.Combine(iLetters.Value, "../../..")).Replace('\\', '/');

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
        public static string? RecordSwap(string iDataRoot, string iPersona,
                                         string iFromSymbol, long iFromE8, decimal iFromBidUsd,
                                         string iToSymbol, long iToE8, decimal iToAskUsd, decimal iFeeFactor,
                                         DateTime iNowUtc, string iSource)
        {
            var e = new SCP_PortfolioEvent
            {
                Kind = SCP_PortfolioEvent.KindSwap,
                AtUtc = iNowUtc,
                Persona = iPersona,
                FromSymbol = iFromSymbol.Trim().ToUpperInvariant(),
                FromE8 = iFromE8,
                ToSymbol = iToSymbol.Trim().ToUpperInvariant(),
                ToE8 = iToE8,
                PriceUsd = iFromBidUsd,
                ToAskUsd = iToAskUsd,
                FeeFactor = iFeeFactor,
                ValueUsd = (decimal)iFromE8 / SCP_VoucherBook.FractionScale * iFromBidUsd,
                Source = iSource,
            };
            return Write(iDataRoot, e);
        }

        /// <summary>
        /// 記一筆某券的增減（發券／花用／遷入）。該券沒有報價 ⇒ **不記**、回 null（守衛①）。
        /// <paramref name="oRecorded"/> 分得出「記了」與「因為沒報價而跳過」。
        /// </summary>
        public static string? RecordFlow(string iDataRoot, string iPersona, string iVoucher, long iDeltaE8,
                                         string iSource, string iRef, DateTime iNowUtc, out bool oRecorded)
        {
            oRecorded = false;
            if (iDeltaE8 == 0) return null;
            var aConfig = SCP_MarketRateCache.Load(iDataRoot, out string? aErr);
            if (aErr != null) return $"交易紀錄沒寫：讀不了匯率快取（{aErr}）";
            string aSym = iVoucher.Trim().ToUpperInvariant();
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
            string? aWriteErr = Write(iDataRoot, e);
            oRecorded = aWriteErr == null;
            return aWriteErr;
        }

        static string? Write(string iDataRoot, SCP_PortfolioEvent e)
        {
            string aPath = "";
            try
            {
                DateTime aT = e.AtUtc.ToUniversalTime();
                string aDir = Path.Combine(EventsDir(iDataRoot), aT.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
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
        public static List<SCP_PortfolioEvent> ReadEvents(string iDataRoot, string? iPersona, List<string> oProblems)
        {
            var aOut = new List<SCP_PortfolioEvent>();
            string aRoot = EventsDir(iDataRoot);
            if (!Directory.Exists(aRoot)) return aOut;
            foreach (string aFile in Directory.GetFiles(aRoot, "*.json", SearchOption.AllDirectories))
            {
                try
                {
                    var j = SCP_JsonParser.Parse(File.ReadAllText(aFile));
                    var e = new SCP_PortfolioEvent
                    {
                        Kind = j.GetString("kind", ""),
                        Persona = j.GetString("persona", ""),
                        Source = j.GetString("source", ""),
                        Ref = j.GetString("ref", ""),
                        ValueUsd = ReadDec(j, "value_usd"),
                        FileName = Path.GetFileName(aFile),
                    };
                    if (iPersona != null && !string.Equals(e.Persona, iPersona, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!DateTime.TryParse(j.GetString("at_utc", ""), CultureInfo.InvariantCulture,
                                           DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out e.AtUtc))
                    { oProblems.Add($"事件時間讀不出來（略過）：{aFile}"); continue; }
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
                    else { oProblems.Add($"認不得的事件種類 '{e.Kind}'（略過）：{aFile}"); continue; }
                    aOut.Add(e);
                }
                catch (Exception ex)
                {
                    oProblems.Add($"事件檔讀不了（略過，⚠ 這筆沒有算進報酬）：{aFile}：{ex.GetType().Name}: {ex.Message}");
                }
            }
            aOut.Sort((a, b) =>
            {
                int c = a.AtUtc.CompareTo(b.AtUtc);
                return c != 0 ? c : string.CompareOrdinal(a.FileName, b.FileName);
            });
            return aOut;
        }

        // ===========================================================
        // 區塊職責：開帳快照 —— 上線那一刻所有可估值持倉的現值，當作既有持倉的成本。
        // ⚠ **只寫一次**：重拍等於把成本基準改到今天，之前的漲跌從帳上消失，而那不會報錯。
        // ===========================================================

        /// <summary>算出開帳快照（純讀，零寫入）；寫入走 <see cref="TryWriteOpening"/>。</summary>
        public static SCP_PortfolioOpening BuildOpening(string iDataRoot, SCP_LettersRoot iLetters, DateTime iNowUtc,
                                                        List<string> oProblems)
        {
            var aOut = new SCP_PortfolioOpening { AtUtc = iNowUtc };
            var aConfig = SCP_MarketRateCache.Load(iDataRoot, out string? aErr);
            if (aErr != null) { oProblems.Add($"讀不了匯率快取：{aErr}"); return aOut; }

            foreach (string aPersona in SCP_PersonaProfile.PoolNames(iLetters.Value, w => oProblems.Add(w)))
            {
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

        public static bool TryWriteOpening(string iDataRoot, SCP_PortfolioOpening iOpening, out string? oError)
        {
            oError = null;
            string aPath = OpeningPath(iDataRoot);
            try
            {
                if (File.Exists(aPath))
                {
                    oError = $"開帳快照已經存在 ⇒ 拒絕重拍（重拍會把所有人的成本基準改到今天）：{aPath}";
                    return false;
                }
                string aDir = PortfolioDir(iDataRoot);
                if (!Directory.Exists(aDir)) Directory.CreateDirectory(aDir);

                var j = SCP_JsonData.NewObject();
                j.Set("schema_version", SCP_JsonData.NewNumber(1));
                j.Set("at_utc", SCP_JsonData.NewString(iOpening.AtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
                j.Set("basis", SCP_JsonData.NewString("上線時現值（Tim 2026-10-01 拍板：無法推算原成本的既有持倉以此為成本）"));
                var aArr = SCP_JsonData.NewArray();
                foreach (var p in iOpening.Positions)
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
                var aUnq = SCP_JsonData.NewArray();
                foreach (string s in iOpening.Unquoted) aUnq.Add(SCP_JsonData.NewString(s));
                j.Set("unquoted", aUnq);

                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(j) + "\n");
                File.Move(aTmp, aPath);
                return true;
            }
            catch (Exception ex)
            {
                oError = $"開帳快照寫入失敗（{aPath}）：{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>讀開帳快照。沒有檔 ⇒ 回 null 且 oError 為 null（「還沒開帳」不是錯）。</summary>
        public static SCP_PortfolioOpening? LoadOpening(string iDataRoot, out string? oError)
        {
            oError = null;
            string aPath = OpeningPath(iDataRoot);
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
                        Symbol = o.GetString("symbol", "").ToUpperInvariant(),
                        UnitsE8 = o.GetLong("units_e8", 0),
                        BidUsd = ReadDec(o, "bid_usd"),
                        ValueUsd = ReadDec(o, "value_usd"),
                    });
                }
                var aUnq = j["unquoted"];
                for (int i = 0; i < aUnq.Count; i++) aOut.Unquoted.Add(aUnq[i].AsString());
                return aOut;
            }
            catch (Exception ex)
            {
                oError = $"開帳快照讀不了（{aPath}）：{ex.GetType().Name}: {ex.Message}";
                return null;
            }
        }

        /// <summary>某人手上每一種券（名稱轉大寫合併 —— 券檔名大小寫曾經漂過：Gold.json／gold.json）。</summary>
        public static Dictionary<string, long> CollectHoldings(SCP_LettersRoot iLetters, string iPersona, DateTime iNowUtc,
                                                               List<string> oProblems)
        {
            var aOut = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string aName in SCP_VoucherStore.ListVouchers(iLetters, iPersona))
            {
                var aBook = SCP_VoucherStore.Load(iLetters, iPersona, aName, out string? aProblem);
                if (aProblem != null) { oProblems.Add($"`{iPersona}` 的 `{aName}` 讀不了（⚠ 沒有算進來）：{aProblem}"); continue; }
                string aKey = aName.ToUpperInvariant();
                aOut.TryGetValue(aKey, out long aPrev);
                aOut[aKey] = aPrev + UnitsE8Of(aBook, iNowUtc);
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
        public static SCP_PortfolioView Build(string iDataRoot, SCP_LettersRoot iLetters, string iPersona,
                                              SCP_MarketRateConfig iConfig, DateTime iNowUtc)
        {
            var v = new SCP_PortfolioView { Persona = iPersona };
            var aPos = new Dictionary<string, SCP_PortfolioPosition>(StringComparer.OrdinalIgnoreCase);
            SCP_PortfolioPosition P(string iSym)
            {
                string k = iSym.ToUpperInvariant();
                if (!aPos.TryGetValue(k, out var p)) { p = new SCP_PortfolioPosition { Symbol = k }; aPos[k] = p; }
                return p;
            }

            var aOpening = LoadOpening(iDataRoot, out string? aOpenErr);
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

            var aEvents = ReadEvents(iDataRoot, iPersona, v.Problems);
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
                if (SCP_MarketRateCache.TryGetQuote(iConfig, kv.Key, out _)) P(kv.Key).ActualE8 += kv.Value;
                else if (kv.Value > 0) v.Unquoted.Add(new KeyValuePair<string, long>(kv.Key, kv.Value));
            }
            foreach (var p in aPos.Values)
            {
                if (SCP_MarketRateCache.TryGetQuote(iConfig, p.Symbol, out var q)) { p.Quoted = true; p.BidUsd = q!.Bid; }
                if (p.ActualE8 == 0 && p.TrackedE8 == 0 && p.RealizedUsd == 0m) continue;
                v.Positions.Add(p);
            }
            v.Positions.Sort((a, b) => string.CompareOrdinal(a.Symbol, b.Symbol));
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
