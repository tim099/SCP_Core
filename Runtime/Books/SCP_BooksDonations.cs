// 區塊職責：共享圖書館的**捐贈簿與打賞簿** —— 檔案清單規則、聚合讀取、以及兩份人讀報表。
// 物理意義：`Books/<slug>/_donation.json`（per-book 檔即事實源）與 `Books/tips/*.json`。
// 數值影響：**純唯讀**。本檔不寫檔、不建目錄、不動錢。
//
// ⚠ 接縫切在哪：**檔案清單與排序規則住這裡一份**（`DonationFiles` / `TipFiles`），
//   而「用哪個 JSON 方言去 parse」由呼叫端自己決定 —— Editor 那側是 `UCL.Core.JsonLib.JsonData`
//   （它還要拿去改欄位），CLI 這側是 `SCP_JsonData`。
//   🩸 會漂的從來不是 parse，是**「掃哪個目錄、收哪些檔、依什麼排序」** ——
//   兩邊各掃一次就是兩個會各自漂的真相源，而漂掉的症狀是
//   「CLI 說 35 本、後台說 34 本，兩邊都不報錯」。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Books
{
    public static class SCP_BooksDonations
    {
        public const string Key_Book = "book";
        public const string Key_Title = "title";
        public const string Key_Donor = "donor";
        public const string Key_DonorPersona = "donor_persona";
        public const string Key_Tokens = "tokens";
        public const string Key_Chapters = "chapters";
        public const string Key_DonatedAt = "donated_at";
        public const string Key_PublishedAt = "published_at";
        public const string Key_Note = "note";
        public const string Key_TokensSpent = "tokens_spent";

        public const string DonationFileName = "_donation.json";
        public const string TipsDirName = "tips";

        public static string BooksRoot(string iDataRoot) => Path.Combine(iDataRoot, "Books");
        public static string BookDir(string iDataRoot, string iBook) => Path.Combine(BooksRoot(iDataRoot), iBook);
        public static string DonationPath(string iDataRoot, string iBook)
            => Path.Combine(BookDir(iDataRoot, iBook), DonationFileName);
        public static string TipsDir(string iDataRoot) => Path.Combine(BooksRoot(iDataRoot), TipsDirName);

        // ===========================================================
        // 區塊職責：**檔案清單規則** —— 誰算一本書、順序是什麼。
        // ⚠ 排序一律 `StringComparer.Ordinal`：culture-aware 排序在不同機器上會給不同順序，
        //   而報表的差異看起來會像「資料變了」。
        // ⛔ `tips` 那一夾**不是書** —— 它跟 `<slug>/` 住同一層，靠「有沒有 `_donation.json`」自然排除。
        // ===========================================================
        public static List<string> DonationDirs(string iDataRoot)
        {
            var aOut = new List<string>();
            string aRoot = BooksRoot(iDataRoot);
            if (!Directory.Exists(aRoot)) return aOut;
            var aDirs = new List<string>(Directory.GetDirectories(aRoot));
            aDirs.Sort(StringComparer.Ordinal);
            foreach (string aDir in aDirs)
                if (File.Exists(Path.Combine(aDir, DonationFileName))) aOut.Add(aDir);
            return aOut;
        }

        public static List<string> TipFiles(string iDataRoot)
        {
            var aOut = new List<string>();
            string aDir = TipsDir(iDataRoot);
            if (!Directory.Exists(aDir)) return aOut;
            aOut.AddRange(Directory.GetFiles(aDir, "*.json"));
            aOut.Sort(StringComparer.Ordinal);
            return aOut;
        }

        // ===========================================================
        // 區塊職責：聚合讀取。壞檔**略過但記名** —— 略過是韌性，不報是隱瞞。
        // ===========================================================
        public static List<SCP_JsonData> LoadDonations(string iDataRoot, List<string>? ioWarnings = null)
        {
            var aOut = new List<SCP_JsonData>();
            foreach (string aDir in DonationDirs(iDataRoot))
            {
                string aPath = Path.Combine(aDir, DonationFileName);
                var aData = TryParse(aPath, out string aErr);
                if (aData == null)
                {
                    ioWarnings?.Add($"`{Path.GetFileName(aDir)}/{DonationFileName}` 讀取失敗：{aErr}");
                    continue;
                }
                // 缺欄用資料夾名兜底 —— ⚠ 與 Editor 那側**逐字相同**的兜底，
                //   不然同一本書在兩個入口會顯示成不同的 slug。
                if (!aData.Contains(Key_Book)) aData[Key_Book] = Path.GetFileName(aDir);
                aOut.Add(aData);
            }
            return aOut;
        }

        public static List<SCP_JsonData> LoadTips(string iDataRoot, List<string>? ioWarnings = null)
        {
            var aOut = new List<SCP_JsonData>();
            foreach (string aFile in TipFiles(iDataRoot))
            {
                var aData = TryParse(aFile, out string aErr);
                if (aData == null)
                {
                    ioWarnings?.Add($"`{TipsDirName}/{Path.GetFileName(aFile)}` 讀取失敗：{aErr}");
                    continue;
                }
                aOut.Add(aData);
            }
            return aOut;
        }

        static SCP_JsonData? TryParse(string iPath, out string oError)
        {
            oError = "";
            try
            {
                var aData = SCP_JsonData.Parse(File.ReadAllText(iPath));
                if (aData == null || !aData.IsObject) { oError = "不是 JSON 物件"; return null; }
                return aData;
            }
            catch (Exception e) { oError = e.Message; return null; }
        }

        // ===========================================================
        // 區塊職責：報表（人讀輸出）—— 與 Editor 那側**同一份實作**（TASK-0234 ①）。
        // ⚠ 版面逐字不可改：它有兩個入口（Editor／Senate CLI），而驗收條件是**逐位元組對拍**。
        //   ⇒ 想改措辭的話要同時搬走那個對拍，不然下一個人會以為是資料變了。
        // ===========================================================
        public static string RenderDonations(string iDataRoot)
        {
            var aWarnings = new List<string>();
            var aDs = LoadDonations(iDataRoot, aWarnings);
            var sb = new StringBuilder();
            if (aDs.Count == 0)
            {
                sb.AppendLine("（圖書館尚無捐贈書）");
            }
            else
            {
                // 走 DeriveOrigin 而不是原始 source：新檔只有 origin，舊檔只有 source，
                // 讀原始欄位會讓新發表的書全部掉進「捐贈調入」那一組。
                var aAuthored = new List<SCP_JsonData>();
                var aDonated = new List<SCP_JsonData>();
                foreach (var d in aDs)
                {
                    if (OriginOf(d) == SCP_BookOrigin.Authored) aAuthored.Add(d);
                    else aDonated.Add(d);
                }
                // 壞檔數要出現在**數字旁邊**，不是只在文末 WARNING（Sirius 協測 2026-08-07）：
                // 「共 21 本」沒有標記時，只讀標頭的人會以為圖書館真的只有 21 本 ——
                // 計數靜默吸收被丟掉的列，跟「讀空目錄不報錯」同族。
                string aFailNote = aWarnings.Count > 0 ? $"，另有 {aWarnings.Count} 筆讀取失敗 ⚠ 見文末" : "";
                sb.AppendLine($"📚 共享圖書館（共 {aDs.Count} 本 — ✍ 原創 {aAuthored.Count} / 📖 捐贈調入 {aDonated.Count}{aFailNote}）");
                sb.AppendLine();
                if (aAuthored.Count > 0)
                {
                    sb.AppendLine("✍ 原創著作（作者署名，免費入庫）:");
                    foreach (var d in aAuthored)
                    {
                        sb.AppendLine($"- 《{d.GetString(Key_Title, d.GetString(Key_Book, "?"))}》 — 作者: " +
                                      $"{d.GetString(Key_DonorPersona, d.GetString(Key_Donor, "?"))} " +
                                      $"({d.GetInt(Key_Chapters, 0)} 章, {d.GetString(Key_PublishedAt, d.GetString(Key_DonatedAt, "?"))})");
                        string aNote = d.GetString(Key_Note, "");
                        if (!string.IsNullOrEmpty(aNote)) sb.AppendLine($"    note: {aNote}");
                    }
                    sb.AppendLine();
                }
                if (aDonated.Count > 0)
                {
                    sb.AppendLine("📖 捐贈調入（出資者付 token）:");
                    foreach (var d in aDonated)
                    {
                        sb.AppendLine($"- 《{d.GetString(Key_Title, d.GetString(Key_Book, "?"))}》 — 捐贈者: " +
                                      $"{d.GetString(Key_DonorPersona, d.GetString(Key_Donor, "?"))} " +
                                      $"({d.GetInt(Key_Tokens, 0)} token, {d.GetString(Key_DonatedAt, "?")})");
                        string aNote = d.GetString(Key_Note, "");
                        if (!string.IsNullOrEmpty(aNote)) sb.AppendLine($"    note: {aNote}");
                    }
                }
                // 打賞累計
                var aTotals = new Dictionary<string, KeyValuePair<int, int>>();
                var aOrder = new List<string>();
                foreach (var t in LoadTips(iDataRoot))
                {
                    string aSlug = t.GetString(Key_Book, "?");
                    if (!aTotals.TryGetValue(aSlug, out var aCur)) { aCur = new KeyValuePair<int, int>(0, 0); aOrder.Add(aSlug); }
                    aTotals[aSlug] = new KeyValuePair<int, int>(
                        aCur.Key + t.GetInt(Key_TokensSpent, 0), aCur.Value + 1);
                }
                if (aTotals.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine("💰 打賞累計:");
                    // ⚠ 用**首見順序**走訪，⛔ 不靠 Dictionary 的列舉順序：
                    //   那個順序沒有保證，而它變動的時候輸出會變、資料卻沒變 —— 對拍會紅在一個假的地方。
                    foreach (string aSlug in aOrder)
                    {
                        var aVal = aTotals[aSlug];
                        string aTitle = aSlug;
                        foreach (var d in aDs)
                            if (d.GetString(Key_Book, "") == aSlug) { aTitle = d.GetString(Key_Title, aSlug); break; }
                        sb.AppendLine($"- 《{aTitle}》: {aVal.Key} token ({aVal.Value} 筆)");
                    }
                }
            }
            AppendWarnings(sb, aWarnings);
            return sb.ToString();
        }

        public static string RenderTips(string iDataRoot, string? iBookFilter)
        {
            var aWarnings = new List<string>();
            var aTips = LoadTips(iDataRoot, aWarnings);
            if (!string.IsNullOrEmpty(iBookFilter))
            {
                var aKept = new List<SCP_JsonData>();
                foreach (var t in aTips) if (t.GetString(Key_Book, "") == iBookFilter) aKept.Add(t);
                aTips = aKept;
            }
            var sb = new StringBuilder();
            if (aTips.Count == 0)
            {
                sb.AppendLine("（尚無打賞紀錄；用 op=tip 打賞喜歡的書）");
            }
            else
            {
                int aTotal = 0;
                foreach (var t in aTips) aTotal += t.GetInt(Key_TokensSpent, 0);
                // 同 RenderDonations：壞檔數標在數字旁邊
                string aFailNote = aWarnings.Count > 0 ? $"，另有 {aWarnings.Count} 筆讀取失敗 ⚠ 見文末" : "";
                sb.AppendLine($"💰 打賞簿（{aTips.Count} 筆, 累計 {aTotal} token{aFailNote}）");
                sb.AppendLine();
                foreach (var t in aTips)
                {
                    string aStatus = t.GetString("voucher_status", "") == "issued"
                        ? "" : $"　⚠{t.GetString("voucher_status", "?")}";
                    var aV = t.Contains("vouchers") ? t["vouchers"] : null;
                    sb.AppendLine($"- {t.GetString("tipped_at", "?")}  {t.GetString("tipper_persona", "?")} → " +
                                  $"《{t.GetString(Key_Title, t.GetString(Key_Book, "?"))}》 {t.GetInt(Key_TokensSpent, 0)} token → " +
                                  $"{t.GetString("beneficiary_persona", "?")}" +
                                  $"（繪圖券×{(aV != null ? aV.GetInt("canvas", 0) : 0)} + 酒館券×{(aV != null ? aV.GetInt("tavern", 0) : 0)}）{aStatus}");
                    string aNote = t.GetString(Key_Note, "");
                    if (!string.IsNullOrEmpty(aNote)) sb.AppendLine($"    note: {aNote}");
                }
            }
            AppendWarnings(sb, aWarnings);
            return sb.ToString();
        }

        /// <summary>⤷ 分類三軸住 <see cref="SCP_BooksClassification"/>（TASK-0234 ① 第一批）。</summary>
        static SCP_BookOrigin OriginOf(SCP_JsonData d)
            => SCP_BooksClassification.DeriveOrigin(
                d.GetString(SCP_BooksClassification.Key_Origin, ""),
                d.GetString(SCP_BooksClassification.Key_Source, ""));

        static void AppendWarnings(StringBuilder ioSb, List<string> iWarnings)
        {
            // 壞檔不靜默吞掉 —— 略過是韌性，不報是隱瞞
            if (iWarnings == null || iWarnings.Count == 0) return;
            ioSb.AppendLine();
            ioSb.AppendLine("> [!WARNING]");
            foreach (string w in iWarnings) ioSb.AppendLine($"> {w}");
        }
    }
}
