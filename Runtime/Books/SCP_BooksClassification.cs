// 區塊職責：共享圖書館的**分類三軸**（來源 origin／種類 kind／系列 series）—— 純推導與顯示字串。
// 物理意義：把「舊檔沒有新欄位時該算成什麼」這組規則收在**一份**實作裡。
//          散到各消費端就會長出各自的推導，而它們不一致時**兩邊都不會報錯**
//          （這正是 `UCL_BooksClassification` 開頭那段一符二役血證的下游）。
// 數值影響：origin 決定權限與帳務標籤（publish 能不能覆寫、打賞的受益人叫作者還是捐贈者）；
//          kind 與 series 只影響展示與檢索，不動錢。
//
// ⚠ **本檔一個位元組都不寫磁碟，也不讀磁碟。** 介面收的是**字串與清單，不是 JsonData**：
//   Editor 那側用 `UCL.Core.JsonLib.JsonData`、CLI 那側用 `SCP_JsonData`，
//   讓型別跨過接縫等於把兩個 JSON 方言綁進同一個簽章裡，而那會逼下一個人二選一。
//   ⇒ 接縫切在**原始欄位值**上（`origin` / `source` / `slug` …），兩邊各自負責把它們讀出來。
//
// 🩸 為什麼 `_series.json` 的讀寫**沒有**跟著搬（TASK-0234 ①，basecamp 2026-09-17 拍板並寫在單上）：
//   寫入端會改變**檔案的形狀**（非 ASCII 逃脫與否、縮排版面），而同族前科是 BUG-6 ——
//   registry 家族被兩個序列化器輪流整檔重寫，逐鍵相同、逐位元組不同，整批翻紅時沒有一層會喊。
//   ⇒ 那一格要自己帶「寫出來的檔逐位元組對拍」才准動，不搭這一批的便車。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Books
{
    // ===========================================================
    // 區塊職責：來源 —— **誰把這本弄進圖書館的**（權限與帳務軸）。
    // 物理意義：只有兩種，因為只有兩條入庫路徑：自己寫（publish）或付錢調入（donate）。
    // 數值影響：publish 只准覆寫 Authored 的書；tip 的受益人標籤由它決定。
    //          ⚠ 這一軸**不准**再長出第三個值 —— 想加的東西應該加在 kind。
    // ===========================================================
    public enum SCP_BookOrigin
    {
        /// <summary>館內自己寫的（含觀影實錄、酒館史等編纂產物 —— 它們也是自己產的）。</summary>
        Authored,
        /// <summary>付 token 調入的外部作品。</summary>
        Donated,
    }

    // ===========================================================
    // 區塊職責：種類 —— **這是什麼書**（展示與檢索軸）。
    // 物理意義：純分類，可以長。新增種類時只要補這裡與 KindLabel，不影響任何權限判斷。
    // ===========================================================
    public enum SCP_BookKind
    {
        /// <summary>原創著作（心得書、小說、散文集）。</summary>
        Original,
        /// <summary>外部作品（實體書 / 既有作品，付 token 調入）。</summary>
        External,
        /// <summary>觀影實錄（StreamWatch 收工匯出，酒館 seq 原文照收）。</summary>
        WatchLog,
        /// <summary>酒館史（某一天的酒館，Phase A 匯出 + Phase B 人工編纂）。</summary>
        TavernHistory,
    }

    // ===========================================================
    // 區塊職責：系列註冊表的一筆 —— **計算用的 typed view**，不是 JSON model。
    // 物理意義：`Books/_series.json` 的一個元素投影過來的四格。
    //          ⛔ 它刻意**沒有**序列化能力：本檔不寫檔，讀寫仍住呼叫端（見檔頭血證）。
    // ===========================================================
    public sealed class SCP_BookSeriesEntry
    {
        public string Id = "";
        public string Title = "";
        /// <summary>上位系列 id；空＝頂層。例：`farseer-trilogy` 的 parent 是 `realm-of-the-elderlings`。</summary>
        public string Parent = "";
        public string Note = "";
    }

    public static class SCP_BooksClassification
    {
        public const string Key_Origin = "origin";
        public const string Key_Kind = "kind";
        public const string Key_Series = "series";
        public const string Key_Volume = "volume";

        /// <summary>legacy 欄位：舊檔用它同時扛「這是什麼書」與「能不能被覆寫」兩役。</summary>
        public const string Key_Source = "source";

        /// <summary>酒館史的 slug 前綴（`history-&lt;date&gt;-&lt;slug&gt;`）。</summary>
        public const string HistorySlugPrefix = "history-";
        /// <summary>酒館史一律屬於同一個系列（Tim 2026-08-19：歷史書可以當成一整系列）。</summary>
        public const string SeriesTavernHistory = "tavern-history";
        /// <summary>觀影實錄的 slug 前綴（`senate cmd watch --arg op=export` 的產物；⚠ 舊入口 `library.py export-watch` 已整支退場）。</summary>
        public const string WatchSlugPrefix = "watch-";

        // -------- 字串 <-> 列舉（列舉一律用字串進出：JSON 有 python 讀取端，序號跨語言沒有意義）--------

        public static string ToKey(SCP_BookOrigin iValue)
        {
            return iValue == SCP_BookOrigin.Donated ? "donated" : "authored";
        }

        public static string ToKey(SCP_BookKind iValue)
        {
            switch (iValue)
            {
                case SCP_BookKind.External: return "external";
                case SCP_BookKind.WatchLog: return "watch-log";
                case SCP_BookKind.TavernHistory: return "tavern-history";
                default: return "original";
            }
        }

        /// <summary>
        /// 認不得時回 false **且** oKind 仍給 Original ——
        /// ⚠ 這不是「猜一個」：呼叫端用回傳值決定要不要報錯，用 oKind 決定顯示，兩件事分開。
        /// </summary>
        public static bool TryParseKind(string? iRaw, out SCP_BookKind oKind)
        {
            switch ((iRaw ?? "").Trim().ToLowerInvariant())
            {
                case "original": oKind = SCP_BookKind.Original; return true;
                case "external": oKind = SCP_BookKind.External; return true;
                case "watch-log": case "watchlog": oKind = SCP_BookKind.WatchLog; return true;
                case "tavern-history": case "history": oKind = SCP_BookKind.TavernHistory; return true;
                default: oKind = SCP_BookKind.Original; return false;
            }
        }

        public static string AllKindKeys => "original|external|watch-log|tavern-history";

        public static string KindLabel(SCP_BookKind iKind)
        {
            switch (iKind)
            {
                case SCP_BookKind.External: return "📖 外部作品（付 token 調入）";
                case SCP_BookKind.WatchLog: return "📺 觀影實錄";
                case SCP_BookKind.TavernHistory: return "🏛 酒館史";
                default: return "✍ 原創著作";
            }
        }

        // -------- read-through 推導（接縫收原始欄位值，⛔ 不收 JSON 物件）--------

        /// <summary>
        /// 取這本書的 origin。有 `origin` 欄就用它；沒有才由 legacy `source` 推導。
        /// 物理意義：舊檔的 `source` 只有 "authored" 代表館內自產，其餘（含空字串、watch-log）語意混雜 ——
        ///          而 watch-log 明明是自產的，舊邏輯把它算成捐贈，這裡修正。
        /// </summary>
        /// <param name="iOriginRaw">`origin` 欄的原值；沒有這個欄位時給空字串。</param>
        /// <param name="iSourceRaw">legacy `source` 欄的原值；沒有時給空字串。</param>
        public static SCP_BookOrigin DeriveOrigin(string? iOriginRaw, string? iSourceRaw)
        {
            string aOrigin = iOriginRaw ?? "";
            if (aOrigin.Length > 0)
                return aOrigin == "donated" ? SCP_BookOrigin.Donated : SCP_BookOrigin.Authored;

            string aSource = iSourceRaw ?? "";
            // 空 source ＝ 舊的捐贈登記（donate 從來不寫 source）。
            if (aSource.Length == 0) return SCP_BookOrigin.Donated;
            // authored / watch-log / 任何館內流程產出的標記 ⇒ 都是自產。
            return SCP_BookOrigin.Authored;
        }

        /// <summary>
        /// 取這本書的 kind。有 `kind` 欄就用它；沒有才依序由 legacy `source` → slug 前綴 → origin 推導。
        /// 數值影響：slug 前綴是**最後一道**，因為它是慣例不是宣告 —— 但沒有它的話，
        ///          已經發表的酒館史與觀影實錄要等到有人手動 classify 才會歸位。
        /// </summary>
        public static SCP_BookKind DeriveKind(string? iKindRaw, string? iSourceRaw,
                                              string? iSlug, string? iOriginRaw)
        {
            if (TryParseKind(iKindRaw, out var aKind)) return aKind;

            string aSource = iSourceRaw ?? "";
            if (aSource == "watch-log") return SCP_BookKind.WatchLog;
            if (aSource == "tavern-history") return SCP_BookKind.TavernHistory;

            string aSlug = iSlug ?? "";
            if (aSlug.StartsWith(HistorySlugPrefix, StringComparison.Ordinal)) return SCP_BookKind.TavernHistory;
            if (aSlug.StartsWith(WatchSlugPrefix, StringComparison.Ordinal)) return SCP_BookKind.WatchLog;

            return DeriveOrigin(iOriginRaw, iSourceRaw) == SCP_BookOrigin.Donated
                ? SCP_BookKind.External
                : SCP_BookKind.Original;
        }

        /// <summary>系列 id：有 `series` 欄就用它；沒有時酒館史自動歸 `tavern-history`（那是全系列的定義）。</summary>
        public static string DeriveSeries(string? iSeriesRaw, string? iSlug)
        {
            string aSeries = (iSeriesRaw ?? "").Trim();
            if (aSeries.Length > 0) return aSeries;
            if ((iSlug ?? "").StartsWith(HistorySlugPrefix, StringComparison.Ordinal)) return SeriesTavernHistory;
            return "";
        }

        // -------- 系列註冊表上的查詢（唯讀；表本身由呼叫端讀進來）--------

        public static SCP_BookSeriesEntry? FindSeries(IReadOnlyList<SCP_BookSeriesEntry>? iSeries, string? iId)
        {
            if (iSeries == null || string.IsNullOrEmpty(iId)) return null;
            for (int i = 0; i < iSeries.Count; i++)
                if (iSeries[i] != null && iSeries[i].Id == iId) return iSeries[i];
            return null;
        }

        /// <summary>
        /// 系列的顯示路徑：`世界觀 › 三部曲`（巢狀時逐層往上串）。
        /// ⚠ 最多 4 層 —— 那是**防環**不是深度上限：`parent` 指回自己或互指時，
        ///   沒有這一格就是無窮迴圈，而迴圈的樣子是整支 Cmd 不回來，不是報錯。
        /// ⛔ 未註冊的 id **印 id 本身**，不假裝它有名字（空字串會讓「沒註冊」與「沒系列」同形）。
        /// </summary>
        public static string SeriesPath(IReadOnlyList<SCP_BookSeriesEntry>? iSeries, string? iId)
        {
            var aNames = new List<string>();
            string aCur = iId ?? "";
            for (int i = 0; i < 4 && !string.IsNullOrEmpty(aCur); i++)
            {
                var aEntry = FindSeries(iSeries, aCur);
                if (aEntry == null) { aNames.Insert(0, aCur); break; }
                aNames.Insert(0, string.IsNullOrEmpty(aEntry.Title) ? aEntry.Id : aEntry.Title);
                aCur = aEntry.Parent;
            }
            return string.Join(" › ", aNames);
        }
    }
}
