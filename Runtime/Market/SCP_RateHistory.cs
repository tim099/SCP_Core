// 區塊職責：**歷史匯率 —— 一個版本一個檔**（TASK-0272 驗收⑨⑩⑪⑫）。
// 物理意義：`Market/history/rates_<抓取時間>.json`。每一次**成功刷新出新報價**就是一個版本，
//           檔案裡放的是當時**完整的報價表**（各券 Bid/Ask、來源端點、手續費率），不是只有價格。
//           Tim 2026-09-28：「歷史匯率一個版本一檔（假設每天一個匯率版本）」——
//           TTL 預設 1440 分 ⇒ 正常節奏下一天一版；`force=1` 手動重抓會多出一版，那也是一個真的版本。
// 數值影響：寫入走原子（tmp → move），**同名檔已存在一律拒絕**（歷史不准被覆寫）。讀取零寫入。
// 🩸 守衛：
//   ① **歷史先寫、快取後寫**：新版本的歷史檔落盤失敗 ⇒ 整趟不寫快取
//      （⛔ 不接受「歷史沒存到但新價寫進去了」—— 那一筆會永久從歷史上消失）。
//   ② **沒有新報價就沒有新檔**：抓取／解析全失敗、或 TTL 未到整趟跳過 ⇒ 歷史資料夾一個新檔都不出現。
//      ⇒「那天沒抓」（沒有檔）與「那天抓了但沒變」（有檔、變動 0%）在歷史上**分得出來**。
//   ③ **手填的版本也要進歷史**：刷新前若目前快取跟最新一版歷史**內容不同**（中間有人 `op=set` 手填過），
//      先把目前快取原樣歸成一版（`origin=pre_sync`，時間取**報價本身**的更新時間）再寫新版。
//      內容相同就不重複歸檔 —— 同一份報價存兩次，走勢上會多出一個假的「0% 變動」點。
//   ④ **區間內沒有資料就說沒有**：⛔ 不回 0、不回空表、不回「最近一筆」。
//      三種「沒有」分開講：歷史資料夾整個是空的／有歷史但這個券從沒出現過／這個券有歷史但不在區間內。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Market
{
    /// <summary>一個歷史版本（對應一個檔）。</summary>
    public sealed class SCP_RateHistoryVersion
    {
        /// <summary>版本代號 ＝ 檔名去掉 `rates_` 與 `.json`（抓取時間，UTC，可字典序排序）。</summary>
        public string VersionId = "";

        /// <summary>這一版報價的抓取時間（UTC）。⚠ 是**抓取**時間不是歸檔時間 —— 跟內容同源，回放時序才不會跟內容打架。</summary>
        public DateTime FetchedAtUtc;

        /// <summary>寫進歷史的時間（UTC）。只供查帳；排序與區間一律看 <see cref="FetchedAtUtc"/>。</summary>
        public string ArchivedAtUtc = "";

        /// <summary>這一版怎麼來的：`sync`（刷新抓到的）／`pre_sync`（刷新前把手填過的快取歸檔）。</summary>
        public string Origin = "";

        /// <summary>當時完整的報價表（含手續費率、各券來源端點）。</summary>
        public SCP_MarketRateConfig Config = new SCP_MarketRateConfig();

        public string FilePath = "";
    }

    /// <summary>走勢上的一個點（某券在某一版的報價）。</summary>
    public sealed class SCP_RateHistoryPoint
    {
        public string VersionId = "";
        public DateTime FetchedAtUtc;
        public decimal Bid;
        public decimal Ask;
        public decimal Mid => (Bid + Ask) / 2m;
        public string Source = "";
        public decimal FeePct;
        public string Origin = "";
    }

    /// <summary>某券在一段區間內的走勢與統計。</summary>
    public sealed class SCP_RateHistorySeries
    {
        public string Symbol = "";
        public List<SCP_RateHistoryPoint> Points = new List<SCP_RateHistoryPoint>();

        /// <summary>資料夾裡總共幾版（不限區間、不限券）。0 ＝ 歷史整個是空的。</summary>
        public int TotalVersions;

        /// <summary>這個券在全部歷史裡出現過幾版（不限區間）。0 ＝ 它從沒被記錄過。</summary>
        public int SymbolVersionsAnyTime;

        /// <summary>讀不了的歷史檔（壞檔）—— 列出來，⛔ 不靜默跳過。</summary>
        public List<string> Unreadable = new List<string>();

        public decimal FirstMid => Points.Count > 0 ? Points[0].Mid : 0m;
        public decimal LastMid => Points.Count > 0 ? Points[Points.Count - 1].Mid : 0m;

        /// <summary>區間首尾的中間價變動幅度（%）。少於 2 點時沒有意義 ⇒ 回 null，⛔ 不回 0。</summary>
        public decimal? ChangePct => Points.Count >= 2 && FirstMid > 0m ? (LastMid - FirstMid) / FirstMid * 100m : (decimal?)null;

        public decimal MinMid { get { decimal m = decimal.MaxValue; foreach (var p in Points) if (p.Mid < m) m = p.Mid; return Points.Count > 0 ? m : 0m; } }
        public decimal MaxMid { get { decimal m = decimal.MinValue; foreach (var p in Points) if (p.Mid > m) m = p.Mid; return Points.Count > 0 ? m : 0m; } }

        /// <summary>
        /// 波動度：相鄰兩版中間價**對數報酬**的標準差（%）。少於 3 點（＝少於 2 個報酬）回 null。
        /// ⚠ 單位是「每版」不是年化 —— 版本間隔不固定（有漏抓的日子），年化會假裝間隔一致。
        /// </summary>
        public double? VolatilityPct
        {
            get
            {
                if (Points.Count < 3) return null;
                var aRet = new List<double>();
                for (int i = 1; i < Points.Count; i++)
                {
                    double a = (double)Points[i - 1].Mid, b = (double)Points[i].Mid;
                    if (a > 0 && b > 0) aRet.Add(Math.Log(b / a));
                }
                if (aRet.Count < 2) return null;
                double aMean = 0; foreach (double r in aRet) aMean += r; aMean /= aRet.Count;
                double aVar = 0; foreach (double r in aRet) aVar += (r - aMean) * (r - aMean); aVar /= (aRet.Count - 1);
                return Math.Sqrt(aVar) * 100.0;
            }
        }

        /// <summary>
        /// 相鄰兩版間隔超過一天的「空洞」天數合計 —— 那幾天**沒有抓**，不是「沒有變」。
        /// </summary>
        public int GapDays
        {
            get
            {
                int aGap = 0;
                for (int i = 1; i < Points.Count; i++)
                {
                    int aDays = (int)Math.Floor((Points[i].FetchedAtUtc.Date - Points[i - 1].FetchedAtUtc.Date).TotalDays);
                    if (aDays > 1) aGap += aDays - 1;
                }
                return aGap;
            }
        }
    }

    public static class SCP_RateHistory
    {
        public const string HistoryDirName = "history";
        public const string FilePrefix = "rates_";
        public const string FileSuffix = ".json";

        /// <summary>檔名時間格式：UTC、到毫秒、字典序＝時間序。</summary>
        public const string StampFormat = "yyyyMMdd'T'HHmmssfff'Z'";

        public static string HistoryDir(string iDataRoot) => Path.Combine(SCP_MarketRateCache.MarketDir(iDataRoot), HistoryDirName);

        public static string VersionIdOf(DateTime iFetchedAtUtc) => iFetchedAtUtc.ToUniversalTime().ToString(StampFormat, CultureInfo.InvariantCulture);

        public static string VersionPath(string iDataRoot, string iVersionId) => Path.Combine(HistoryDir(iDataRoot), FilePrefix + iVersionId + FileSuffix);

        /// <summary>
        /// 寫一個版本。⛔ 同名檔已存在 ⇒ 拒絕（歷史不准被覆寫）。原子寫入（tmp → move）。
        /// </summary>
        public static bool TryWriteVersion(string iDataRoot, SCP_MarketRateConfig iConfig, DateTime iFetchedAtUtc,
                                           string iOrigin, out string oPath, out string? oError)
        {
            oError = null;
            string aId = VersionIdOf(iFetchedAtUtc);
            oPath = VersionPath(iDataRoot, aId);
            try
            {
                string aDir = HistoryDir(iDataRoot);
                if (!Directory.Exists(aDir)) Directory.CreateDirectory(aDir);
                if (File.Exists(oPath))
                {
                    oError = $"歷史版本 `{aId}` 已存在 ⇒ 拒絕覆寫（歷史不准被改）：{oPath}";
                    return false;
                }

                var aData = SCP_JsonData.NewObject();
                aData.Set("schema_version", SCP_JsonData.NewNumber(1));
                aData.Set("version_id", SCP_JsonData.NewString(aId));
                aData.Set("fetched_at_utc", SCP_JsonData.NewString(iFetchedAtUtc.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture)));
                aData.Set("archived_at_utc", SCP_JsonData.NewString(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
                aData.Set("origin", SCP_JsonData.NewString(iOrigin));
                aData.Set("config", iConfig.ToJson());

                string aTmp = oPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(aData) + "\n");
                File.Move(aTmp, oPath);
                return true;
            }
            catch (Exception e)
            {
                oError = $"歷史版本寫入失敗（{oPath}）：{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        /// <summary>撤回剛寫的版本（只給「歷史寫了、快取卻沒存進去」那一格用 —— 讓兩邊回到一致）。</summary>
        public static bool TryRetractVersion(string iPath, out string? oError)
        {
            oError = null;
            try { if (File.Exists(iPath)) File.Delete(iPath); return true; }
            catch (Exception e) { oError = e.Message; return false; }
        }

        /// <summary>歷史資料夾裡全部版本的檔案路徑（依版本代號＝抓取時間排序）。</summary>
        public static List<string> ListVersionFiles(string iDataRoot)
        {
            var aList = new List<string>();
            string aDir = HistoryDir(iDataRoot);
            if (!Directory.Exists(aDir)) return aList;
            foreach (string f in Directory.GetFiles(aDir, FilePrefix + "*" + FileSuffix)) aList.Add(f);
            aList.Sort(StringComparer.Ordinal);
            return aList;
        }

        public static bool TryReadVersion(string iPath, out SCP_RateHistoryVersion oVersion, out string? oError)
        {
            oVersion = new SCP_RateHistoryVersion { FilePath = iPath };
            oError = null;
            try
            {
                var aJd = SCP_JsonParser.Parse(File.ReadAllText(iPath));
                oVersion.VersionId = aJd.GetString("version_id", "");
                if (oVersion.VersionId.Length == 0)
                {
                    string aName = Path.GetFileNameWithoutExtension(iPath);
                    oVersion.VersionId = aName.StartsWith(FilePrefix, StringComparison.Ordinal) ? aName.Substring(FilePrefix.Length) : aName;
                }
                if (!DateTime.TryParse(aJd.GetString("fetched_at_utc", ""), CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out oVersion.FetchedAtUtc))
                {
                    oError = "fetched_at_utc 讀不出來";
                    return false;
                }
                oVersion.ArchivedAtUtc = aJd.GetString("archived_at_utc", "");
                oVersion.Origin = aJd.GetString("origin", "");
                SCP_JsonData aCfg = aJd["config"];
                if (!aCfg.Exists || !aCfg.IsObject)
                {
                    oError = "缺 config（完整報價表）";
                    return false;
                }
                oVersion.Config = SCP_MarketRateConfig.FromJson(aCfg);
                return true;
            }
            catch (Exception e)
            {
                oError = $"{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        /// <summary>讀最新一版（沒有歷史 ⇒ false 且 oError 為 null；壞檔 ⇒ false 且帶 oError）。</summary>
        public static bool TryReadLatest(string iDataRoot, out SCP_RateHistoryVersion? oVersion, out string? oError)
        {
            oVersion = null;
            oError = null;
            List<string> aFiles = ListVersionFiles(iDataRoot);
            if (aFiles.Count == 0) return false;
            if (!TryReadVersion(aFiles[aFiles.Count - 1], out var aV, out oError)) return false;
            oVersion = aV;
            return true;
        }

        /// <summary>
        /// 這一個 UTC 日（或更晚）已經有**刷新出來的**版本了嗎 —— 每日同步的閘（TASK-0272 ②）。
        /// ⚠ 只數 `origin=sync`：`pre_sync` 是「刷新前把手填價歸檔」，它的時間是手填那一刻，不代表那天刷新過。
        /// ⛔ 最新幾版裡有壞檔 ⇒ 回 false 並帶 <paramref name="oError"/>（判不出來 ≠ 沒有）。
        /// </summary>
        public static bool HasSyncVersionOnOrAfter(string iDataRoot, DateTime iDayUtc, out string? oVersionId, out string? oError)
        {
            oVersionId = null;
            oError = null;
            List<string> aFiles = ListVersionFiles(iDataRoot);
            for (int i = aFiles.Count - 1; i >= 0; i--)
            {
                if (!TryReadVersion(aFiles[i], out var v, out string? aErr))
                {
                    oError = $"{Path.GetFileName(aFiles[i])} 讀不了：{aErr}";
                    return false;
                }
                if (v.FetchedAtUtc.Date < iDayUtc.Date) return false;   // 檔名＝時間序 ⇒ 再往前只會更早
                if (v.Origin == "sync") { oVersionId = v.VersionId; return true; }
            }
            return false;
        }

        /// <summary>
        /// 兩份報價表的**報價內容**一不一樣（比 Bid/Ask/啟用/費率/端點 與全域費率；⛔ 不比更新時間戳）。
        /// </summary>
        public static bool SameQuotes(SCP_MarketRateConfig iA, SCP_MarketRateConfig iB)
        {
            if (iA.TakerFeePct != iB.TakerFeePct) return false;
            var aKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string k in iA.Quotes.Keys) if (!string.Equals(k, "USD", StringComparison.OrdinalIgnoreCase)) aKeys.Add(k);
            foreach (string k in iB.Quotes.Keys) if (!string.Equals(k, "USD", StringComparison.OrdinalIgnoreCase)) aKeys.Add(k);
            foreach (string k in aKeys)
            {
                if (!iA.Quotes.TryGetValue(k, out var a) || !iB.Quotes.TryGetValue(k, out var b)) return false;
                if (a.Bid != b.Bid || a.Ask != b.Ask || a.IsEnabled != b.IsEnabled || a.FeePct != b.FeePct
                    || a.SourceUrl != b.SourceUrl || a.SourceKind != b.SourceKind)
                    return false;
            }
            return true;
        }

        /// <summary>一份報價表裡有沒有 USD 以外的報價（只有 USD 的表不值得歸檔 —— 那是「還沒有市場」）。</summary>
        public static bool HasTradableQuotes(SCP_MarketRateConfig iConfig)
        {
            foreach (string k in iConfig.Quotes.Keys)
                if (!string.Equals(k, "USD", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 刷新前的歸檔判斷（守衛③）：目前快取若跟最新一版歷史內容不同，就該先把它歸成一版。
        /// 回 true ＝ 需要歸檔；<paramref name="oStampUtc"/> 是它該用的時間（報價本身的更新時間，不是快取檔的）。
        /// ⛔ 最新一版讀不了（壞檔）⇒ 回 false 並帶 <paramref name="oError"/> —— 呼叫端要停手，不猜。
        /// </summary>
        public static bool NeedsPreSyncArchive(string iDataRoot, SCP_MarketRateConfig iCurrent,
                                               out DateTime oStampUtc, out string? oError)
        {
            oStampUtc = default;
            oError = null;
            if (!HasTradableQuotes(iCurrent)) return false;

            if (!TryReadLatest(iDataRoot, out var aLatest, out oError))
            {
                if (oError != null) { oError = "最新一版歷史讀不了 ⇒ 判不出目前快取有沒有歸檔過：" + oError; return false; }
                aLatest = null;   // 沒有任何歷史 ⇒ 要歸
            }
            if (aLatest != null && SameQuotes(aLatest.Config, iCurrent)) return false;

            // 時間取**報價本身**的時間（各券 updated_at_utc 的最大值），⛔ 不取快取檔的更新時間 ——
            // 🩸 實測 2026-09-28：`op=source` 只設端點也會存檔、把快取時間刷成「現在」，
            //   於是 09-22 手填的價格被歸成「今天 03:05 的版本」。價格是哪天的，版本就是哪天的。
            oStampUtc = default;
            foreach (var q in iCurrent.Quotes.Values)
            {
                if (string.Equals(q.Symbol, "USD", StringComparison.OrdinalIgnoreCase)) continue;
                if (DateTime.TryParse(q.UpdatedAtUtc, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime aTs) && aTs > oStampUtc)
                    oStampUtc = aTs;
            }
            if (oStampUtc == default
                && !DateTime.TryParse(iCurrent.UpdatedAtUtc, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out oStampUtc))
                oStampUtc = DateTime.UtcNow.AddMilliseconds(-1);   // 一個時間戳都讀不出來 ⇒ 排在新版之前
            // ⚠ 時間不得晚於或等於最新一版（否則序列會倒置）；撞上就往後挪 1ms。
            if (aLatest != null && oStampUtc <= aLatest.FetchedAtUtc) oStampUtc = aLatest.FetchedAtUtc.AddMilliseconds(1);
            return true;
        }

        /// <summary>
        /// 取某券在區間內的走勢。<paramref name="iSinceUtc"/>／<paramref name="iUntilUtc"/> 為 null ＝ 不限。
        /// 壞檔列進 <see cref="SCP_RateHistorySeries.Unreadable"/>，⛔ 不靜默跳過。
        /// </summary>
        public static SCP_RateHistorySeries BuildSeries(string iDataRoot, string iSymbol, DateTime? iSinceUtc, DateTime? iUntilUtc)
        {
            string aSym = (iSymbol ?? "").Trim().ToUpperInvariant();
            var aSeries = new SCP_RateHistorySeries { Symbol = aSym };
            List<string> aFiles = ListVersionFiles(iDataRoot);
            aSeries.TotalVersions = aFiles.Count;

            foreach (string f in aFiles)
            {
                if (!TryReadVersion(f, out var v, out string? aErr))
                {
                    aSeries.Unreadable.Add($"{Path.GetFileName(f)}：{aErr}");
                    continue;
                }
                if (!v.Config.Quotes.TryGetValue(aSym, out var q)) continue;
                if (q.Bid <= 0 || q.Ask <= 0) continue;
                aSeries.SymbolVersionsAnyTime++;

                if (iSinceUtc.HasValue && v.FetchedAtUtc < iSinceUtc.Value) continue;
                if (iUntilUtc.HasValue && v.FetchedAtUtc > iUntilUtc.Value) continue;

                aSeries.Points.Add(new SCP_RateHistoryPoint
                {
                    VersionId = v.VersionId,
                    FetchedAtUtc = v.FetchedAtUtc,
                    Bid = q.Bid,
                    Ask = q.Ask,
                    Source = q.Source,
                    FeePct = SCP_MarketRateCache.FeePctOf(v.Config, aSym),
                    Origin = v.Origin,
                });
            }
            aSeries.Points.Sort((a, b) => a.FetchedAtUtc.CompareTo(b.FetchedAtUtc));
            return aSeries;
        }

        /// <summary>
        /// 「沒有資料」的原因，三種分開講（守衛④）。有資料時回 null。
        /// </summary>
        public static string? DescribeEmpty(SCP_RateHistorySeries iSeries)
        {
            if (iSeries.Points.Count > 0) return null;
            if (iSeries.TotalVersions == 0)
                return "**無歷史資料**：歷史資料夾是空的（還沒有任何一次成功的刷新）。";
            if (iSeries.SymbolVersionsAnyTime == 0)
                return $"**無歷史資料**：共 {iSeries.TotalVersions} 版歷史，但 `{iSeries.Symbol}` 一次都沒出現過（這個券從沒被記錄過報價）。";
            return $"**無歷史資料**：`{iSeries.Symbol}` 有 {iSeries.SymbolVersionsAnyTime} 版歷史，但**都不在指定區間內**。";
        }

        /// <summary>
        /// 解析區間參數：`yyyy-MM-dd`（當地日期的整天）或 ISO 8601。空字串 ⇒ null（不限）。
        /// <paramref name="iEndOfDay"/>：只給日期時，true 取該日 23:59:59.999（給 until 用）。
        /// </summary>
        public static bool TryParseBound(string iText, bool iEndOfDay, out DateTime? oUtc, out string? oError)
        {
            oUtc = null;
            oError = null;
            string s = (iText ?? "").Trim();
            if (s.Length == 0) return true;
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out DateTime aDay))
            {
                DateTime aLocal = iEndOfDay ? aDay.Date.AddDays(1).AddTicks(-1) : aDay.Date;
                oUtc = aLocal.ToUniversalTime();
                return true;
            }
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime aTs))
            {
                oUtc = aTs;
                return true;
            }
            oError = $"時間讀不出來（收到 '{s}'；要 `yyyy-MM-dd` 或 ISO 8601）";
            return false;
        }
    }
}
