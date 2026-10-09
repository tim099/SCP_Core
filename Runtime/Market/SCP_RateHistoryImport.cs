// 區塊職責：**歷史匯率跨區匯入**（TASK-0475）—— 從另一條 git ref（別區分支，例 origin/LY）讀 `Market/history/`，
//           把本地缺的那一段寫成本地的歷史版本。
// 物理意義：只跑 git 唯讀指令（rev-parse／ls-tree／show），不 checkout、不 fetch ⇒ 讀的是上次 fetch 的快照。
//           每個幣種只取「比本地該幣種**第一個**歷史點更早」、而且「比本地**最新一版**更早」的版本：
//           ① 兩台機器同時都在抓的幣（BTC／GOLD）不會在走勢上交錯成兩條線；
//           ② 匯入的版本不會冒充最新一版 —— 刷新前的歸檔判斷（NeedsPreSyncArchive）拿最新一版比。
//           每版只留要匯入的幣種（＋USD），origin 標 `import:<ref>@<sha>` ⇒ 每日同步「那天抓過了沒」只數 sync，不受影響。
// 數值影響：Plan 零寫入；Apply 走 SCP_RateHistory.TryWriteVersion（同名檔拒絕覆寫）⇒ 重跑不重複。
// 失敗處置：本地歷史有壞檔 ⇒ 判不出各幣第一個點在哪，整份計畫拒絕（⛔ 不猜）；來源壞檔列出、跳過那一版。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Git;

namespace SCP.Core.Market
{
    public sealed class SCP_RateImportItem
    {
        public string VersionId = "";
        public DateTime FetchedAtUtc;
        public string SourcePath = "";
        /// <summary>這一版要匯入的幣種（空 ＝ 不匯入，看 <see cref="Skip"/>）。</summary>
        public List<string> Symbols = new List<string>();
        /// <summary>不匯入的原因（空字串 ＝ 要匯入）。</summary>
        public string Skip = "";
        public SCP_RateHistoryVersion? Source;
    }

    public sealed class SCP_RateImportPlan
    {
        public string Ref = "";
        public string Sha = "";
        public List<SCP_RateImportItem> Items = new List<SCP_RateImportItem>();
        /// <summary>擋下整份計畫的原因（有就不准 Apply）。</summary>
        public List<string> Errors = new List<string>();
        /// <summary>來源讀不了的版本（跳過，但列出）。</summary>
        public List<string> Unreadable = new List<string>();
        /// <summary>各幣本地第一個歷史點（沒有 ⇒ 不在表裡）。</summary>
        public Dictionary<string, DateTime> LocalFirst = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        public DateTime? LocalLatest;
        public string Origin => SCP_RateHistoryImport.OriginPrefix + Ref + "@" + Sha;
        public int ToWrite { get { int n = 0; foreach (var i in Items) if (i.Skip.Length == 0) n++; return n; } }
    }

    public static class SCP_RateHistoryImport
    {
        public const string OriginPrefix = "import:";
        const string HistoryRepoDir = SCP_MarketRateCache.MarketDirName + "/" + SCP_RateHistory.HistoryDirName + "/";

        /// <param name="iDataRoot">資料根 ＝ 那個 git repo 的根。</param>
        /// <param name="iRef">來源 ref（例 <c>origin/LY</c>）。</param>
        /// <param name="iSymbols">只匯入這幾個幣（null／空 ＝ 不限）。</param>
        public static SCP_RateImportPlan Plan(string iDataRoot, string iRef, ICollection<string>? iSymbols)
        {
            var aPlan = new SCP_RateImportPlan { Ref = iRef };
            SCP_GitResult aRev = SCP_Git.Run(iDataRoot, "rev-parse", "--verify", "--short", iRef + "^{commit}");
            if (!aRev.Ok) { aPlan.Errors.Add($"讀不到 ref `{iRef}`：{aRev.ReasonLine}"); return aPlan; }
            aPlan.Sha = aRev.FirstLine.Trim();

            // ── 本地：各幣第一個點、最新一版、已有的版本代號 ──
            var aLocalIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string f in SCP_RateHistory.ListVersionFiles(iDataRoot))
            {
                if (!SCP_RateHistory.TryReadVersion(f, out var v, out string? aErr))
                {
                    aPlan.Errors.Add($"本地歷史 {System.IO.Path.GetFileName(f)} 讀不了：{aErr} ⇒ 判不出各幣第一個點在哪，不匯入");
                    continue;
                }
                aLocalIds.Add(v.VersionId);
                if (!aPlan.LocalLatest.HasValue || v.FetchedAtUtc > aPlan.LocalLatest.Value) aPlan.LocalLatest = v.FetchedAtUtc;
                foreach (var kv in v.Config.Quotes)
                {
                    if (!Tradable(kv.Key, kv.Value)) continue;
                    if (!aPlan.LocalFirst.TryGetValue(kv.Key, out DateTime t) || v.FetchedAtUtc < t) aPlan.LocalFirst[kv.Key] = v.FetchedAtUtc;
                }
            }
            if (aPlan.Errors.Count > 0) return aPlan;

            // ── 來源：ref 上的 Market/history/ ──
            SCP_GitResult aLs = SCP_Git.Run(iDataRoot, "ls-tree", "--name-only", iRef, HistoryRepoDir);
            if (!aLs.Ok) { aPlan.Errors.Add($"列 `{iRef}:{HistoryRepoDir}` 失敗：{aLs.ReasonLine}"); return aPlan; }
            var aPaths = new List<string>();
            foreach (string aLine in aLs.OutLines())
            {
                string p = aLine.Trim();
                string aName = p.Substring(p.LastIndexOf('/') + 1);
                if (aName.StartsWith(SCP_RateHistory.FilePrefix, StringComparison.Ordinal) && aName.EndsWith(SCP_RateHistory.FileSuffix, StringComparison.Ordinal))
                    aPaths.Add(p);
            }
            aPaths.Sort(StringComparer.Ordinal);
            if (aPaths.Count == 0) { aPlan.Errors.Add($"`{iRef}` 上沒有任何歷史版本（{HistoryRepoDir}）"); return aPlan; }

            HashSet<string>? aWant = null;
            if (iSymbols != null && iSymbols.Count > 0)
            {
                aWant = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string s in iSymbols) if (s.Trim().Length > 0) aWant.Add(s.Trim());
            }

            foreach (string p in aPaths)
            {
                SCP_GitResult aShow = SCP_Git.Run(iDataRoot, "show", iRef + ":" + p);
                string? aErr = null;
                SCP_RateHistoryVersion? v = null;
                if (!aShow.Ok || !SCP_RateHistory.TryParseVersion(aShow.StdOut, iRef + ":" + p, out v, out aErr))
                {
                    aPlan.Unreadable.Add($"{p}：{(aShow.Ok ? aErr : aShow.ReasonLine)}");
                    continue;
                }
                v = v!;
                var aItem = new SCP_RateImportItem { VersionId = SCP_RateHistory.VersionIdOf(v.FetchedAtUtc), FetchedAtUtc = v.FetchedAtUtc, SourcePath = p, Source = v };
                aPlan.Items.Add(aItem);
                if (v.Origin.StartsWith(OriginPrefix, StringComparison.Ordinal)) { aItem.Skip = "來源本身就是匯入的版本（不轉手）"; continue; }
                if (aLocalIds.Contains(aItem.VersionId)) { aItem.Skip = "本地已有同一版"; continue; }
                if (aPlan.LocalLatest.HasValue && v.FetchedAtUtc >= aPlan.LocalLatest.Value) { aItem.Skip = "不早於本地最新一版（匯入會冒充最新版）"; continue; }
                foreach (var kv in v.Config.Quotes)
                {
                    if (!Tradable(kv.Key, kv.Value)) continue;
                    if (aWant != null && !aWant.Contains(kv.Key)) continue;
                    if (aPlan.LocalFirst.TryGetValue(kv.Key, out DateTime aFirst) && v.FetchedAtUtc >= aFirst) continue;
                    aItem.Symbols.Add(kv.Key.ToUpperInvariant());
                }
                aItem.Symbols.Sort(StringComparer.Ordinal);
                if (aItem.Symbols.Count == 0) aItem.Skip = "沒有本地缺的幣（本地都已從更早開始記錄）";
            }
            return aPlan;
        }

        /// <summary>照計畫寫入。回傳寫了幾版；<paramref name="oErrors"/> 收逐版失敗（已寫的不回滾 —— 每一版各自完整）。</summary>
        public static int Apply(string iDataRoot, SCP_RateImportPlan iPlan, out List<string> oErrors)
        {
            oErrors = new List<string>();
            if (iPlan.Errors.Count > 0) { oErrors.Add("計畫有錯，不寫入"); return 0; }
            int aWritten = 0;
            foreach (var aItem in iPlan.Items)
            {
                if (aItem.Skip.Length > 0 || aItem.Source == null) continue;
                var src = aItem.Source.Config;
                var aCfg = new SCP_MarketRateConfig
                {
                    BaseCurrency = src.BaseCurrency, UpdatedAtUtc = src.UpdatedAtUtc, FxSystemEnabled = src.FxSystemEnabled,
                    TakerFeePct = src.TakerFeePct, SyncTtlMinutes = src.SyncTtlMinutes,
                };
                foreach (var kv in src.Quotes)
                    if (string.Equals(kv.Key, "USD", StringComparison.OrdinalIgnoreCase) || aItem.Symbols.Contains(kv.Key.ToUpperInvariant()))
                        aCfg.Quotes[kv.Key] = kv.Value;
                if (SCP_RateHistory.TryWriteVersion(iDataRoot, aCfg, aItem.FetchedAtUtc, iPlan.Origin, out _, out string? aErr)) aWritten++;
                else oErrors.Add($"{aItem.VersionId}：{aErr}");
            }
            return aWritten;
        }

        static bool Tradable(string iSymbol, SCP_RateQuote iQ)
            => !string.Equals(iSymbol, "USD", StringComparison.OrdinalIgnoreCase) && iQ.Bid > 0 && iQ.Ask > 0;

        public static string Stamp(DateTime iUtc) => iUtc.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "Z";
    }
}
