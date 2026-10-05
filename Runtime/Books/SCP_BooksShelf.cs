// 區塊職責：藏書架 —— 依**系列**呈現圖書館（`op=shelf`／`op=series`），以及設定某本書的分類（`op=classify`）。
// 物理意義：`UCL_BooksShelf` 的移植（TASK-0403，epic 0398）。`shelf` 是總覽（系列一行、幾冊）、
//          `series` 是某一系列的書單（含 **book id 供閱讀**）、`classify` 是唯一的分類寫入通道。
//          分類三軸的規則本體仍在 `SCP_BooksClassification`，讀寫 `_donation.json` 走 `SCP_BooksOps` 的同一支 writer。
// 數值影響：shelf／series 唯讀；classify 只改 `_donation.json` 的三個分類欄位（＋補寫 origin）與 `_series.json`，**不動錢**。
//
// 設計決策（Tim 2026-08-19，隨 Unity 版原樣搬來）：
//   · **沒有系列的書單獨列出 —— 等於一本一系列。** 總覽只有一種列。
//   · 系列可巢狀（世界觀 › 三部曲 › 冊），巢狀路徑由 `_series.json` 的 parent 串出來。
//   · 酒館史（`history-*`）天生同屬 `tavern-history` —— 不必逐本 classify 就會歸位。
//
// 🩸 寫入端的兩個版面（TASK-0234 basecamp 2026-09-17 的警告：「registry 被兩個序列化器輪流整檔重寫，
//   逐鍵相同、逐位元組不同，而整批翻紅時沒有一層會喊」）：
//   · `_donation.json`：與 `SCP_BooksOps` 同一支 `SaveJson`（2 空格／冒號後有空格／CRLF）—— 不在這裡再寫第二份。
//   · `_series.json`：舊 writer 是 tab 縮排／冒號後無空格／陣列 `[` 自己佔一行（＝ `SCP_JsonStyle.UclLegacy`）。
//   · 改寫 `_series.json` 時**改的是解析出來的原樹**（不是 typed 重建）⇒ 未知鍵與鍵序原樣保留（SCP_Json 規則③）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Io;
using SCP.Core.Json;

namespace SCP.Core.Books
{
    /// <summary>一本書在架上的樣子（唯讀投影）：由 `_donation.json` 讀出＋read-through 推導三軸，**不寫回**。</summary>
    public sealed class SCP_ShelfBook
    {
        public string Book = "";       // slug ＝ 閱讀用的 id
        public string Title = "";
        public string Persona = "";    // 作者或捐贈者
        public int Chapters;
        public string Date = "";
        public SCP_BookOrigin Origin;
        public SCP_BookKind Kind;
        public string Series = "";     // 空＝沒有系列（自成一系列）
        public int Volume;             // 0＝未指定，排序退回 slug
    }

    public static class SCP_BooksShelf
    {
        public const string SeriesRegistryFileName = "_series.json";

        public static string SeriesRegistryPath(string iDataRoot)
            => Path.Combine(SCP_BooksDonations.BooksRoot(iDataRoot), SeriesRegistryFileName);

        // ── 系列註冊表（讀）────────────────────────────────────────────────
        /// <summary>讀 `Books/_series.json` 成唯讀清單；不存在回空表，壞檔回空表**並回報原因**（fail-soft 要出聲）。</summary>
        public static List<SCP_BookSeriesEntry> LoadSeries(string iDataRoot, out string? oError)
        {
            oError = null;
            var aOut = new List<SCP_BookSeriesEntry>();
            SCP_JsonData? aRoot = LoadRegistryTree(iDataRoot, out oError);
            if (aRoot == null || !aRoot.Contains("series")) return aOut;
            SCP_JsonData aArr = aRoot["series"];
            if (!aArr.IsArray) { oError = "_series.json 的 series 不是陣列"; return new List<SCP_BookSeriesEntry>(); }
            for (int i = 0; i < aArr.Count; i++)
            {
                SCP_JsonData aE = aArr[i];
                aOut.Add(new SCP_BookSeriesEntry
                {
                    Id = aE.GetString("id", ""),
                    Title = aE.GetString("title", ""),
                    Parent = aE.GetString("parent", ""),
                    Note = aE.GetString("note", ""),
                });
            }
            return aOut;
        }

        /// <summary>讀原樹（寫入用）。檔案不存在 ⇒ null 且無錯誤；壞檔 ⇒ null 且有錯誤。</summary>
        static SCP_JsonData? LoadRegistryTree(string iDataRoot, out string? oError)
        {
            oError = null;
            string aPath = SeriesRegistryPath(iDataRoot);
            if (!File.Exists(aPath)) return null;
            try
            {
                SCP_JsonData aData = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                if (!aData.IsObject) { oError = "_series.json 讀取失敗：不是 JSON 物件"; return null; }
                return aData;
            }
            catch (Exception e) { oError = $"_series.json 讀取失敗：{e.Message}"; return null; }
        }

        // ── 架上投影 ───────────────────────────────────────────────────────
        /// <summary>讀出全部藏書的架上投影（壞檔列進 warnings，不靜默吞）。</summary>
        public static List<SCP_ShelfBook> LoadShelf(string iDataRoot, List<string>? ioWarnings = null)
        {
            var aOut = new List<SCP_ShelfBook>();
            foreach (SCP_JsonData d in SCP_BooksDonations.LoadDonations(iDataRoot, ioWarnings))
            {
                string aSlug = d.GetString(SCP_BooksDonations.Key_Book, "?");
                aOut.Add(new SCP_ShelfBook
                {
                    Book = aSlug,
                    Title = d.GetString(SCP_BooksDonations.Key_Title, aSlug),
                    Persona = d.GetString(SCP_BooksDonations.Key_DonorPersona,
                                          d.GetString(SCP_BooksDonations.Key_Donor, "?")),
                    Chapters = d.GetInt(SCP_BooksDonations.Key_Chapters, 0),
                    Date = d.GetString(SCP_BooksDonations.Key_PublishedAt,
                                       d.GetString(SCP_BooksDonations.Key_DonatedAt, "?")),
                    Origin = SCP_BooksOps.OriginOf(d, aSlug),
                    Kind = SCP_BooksOps.KindOf(d, aSlug),
                    Series = SCP_BooksOps.SeriesOf(d, aSlug),
                    Volume = SCP_BooksOps.VolumeOf(d),
                });
            }
            return aOut;
        }

        /// <summary>同一系列內的排序：先 volume（0 排最後），再 slug —— `history-YYYY-MM-DD` 天生就排得對。</summary>
        static int CompareInSeries(SCP_ShelfBook a, SCP_ShelfBook b)
        {
            int va = a.Volume <= 0 ? int.MaxValue : a.Volume;
            int vb = b.Volume <= 0 ? int.MaxValue : b.Volume;
            if (va != vb) return va.CompareTo(vb);
            return string.CompareOrdinal(a.Book, b.Book);
        }

        /// <summary>把書分組成「系列」—— **沒有系列的書自成一組**（Tim：等於一本一系列）。</summary>
        public static List<KeyValuePair<string, List<SCP_ShelfBook>>> GroupBySeries(List<SCP_ShelfBook> iBooks)
        {
            var aMap = new Dictionary<string, List<SCP_ShelfBook>>(StringComparer.Ordinal);
            var aOrder = new List<string>();
            foreach (SCP_ShelfBook b in iBooks)
            {
                // 單本系列用 `#<slug>` 當內部鍵，跟真正的 series id 分得開（不會撞名）
                string aKey = string.IsNullOrEmpty(b.Series) ? "#" + b.Book : b.Series;
                if (!aMap.TryGetValue(aKey, out List<SCP_ShelfBook>? aList))
                {
                    aList = new List<SCP_ShelfBook>();
                    aMap[aKey] = aList;
                    aOrder.Add(aKey);
                }
                aList.Add(b);
            }
            var aOut = new List<KeyValuePair<string, List<SCP_ShelfBook>>>();
            foreach (string k in aOrder)
            {
                aMap[k].Sort(CompareInSeries);
                aOut.Add(new KeyValuePair<string, List<SCP_ShelfBook>>(k, aMap[k]));
            }
            // 多冊的系列排前面（那是「系列」這個概念真正有用的地方），其次照名稱
            aOut.Sort((x, y) =>
            {
                bool mx = x.Value.Count > 1, my = y.Value.Count > 1;
                if (mx != my) return my.CompareTo(mx);
                if (mx && x.Value.Count != y.Value.Count) return y.Value.Count.CompareTo(x.Value.Count);
                return string.CompareOrdinal(x.Key, y.Key);
            });
            return aOut;
        }

        // ===========================================================
        // op=shelf —— 藏書總覽：一列一個系列（單書亦然），標明幾冊
        // ⚠ 版面逐字沿用 Unity 版（驗收是逐行對拍）；唯一刻意的差異是「查書單」那行指令改成 `senate cmd book`。
        // ===========================================================
        public static string RenderShelf(string iDataRoot, string? iKindFilter)
        {
            var aWarnings = new List<string>();
            List<SCP_ShelfBook> aBooks = LoadShelf(iDataRoot, aWarnings);
            List<SCP_BookSeriesEntry> aReg = LoadSeries(iDataRoot, out string? aRegErr);

            if (!string.IsNullOrEmpty(iKindFilter))
            {
                if (!SCP_BooksClassification.TryParseKind(iKindFilter, out SCP_BookKind aKf))
                    return $"❌ 未知 kind：{iKindFilter}（可用：{SCP_BooksClassification.AllKindKeys}）";
                aBooks = aBooks.FindAll(b => b.Kind == aKf);
            }

            var aSb = new StringBuilder();
            if (aBooks.Count == 0)
            {
                aSb.AppendLine("（架上沒有符合條件的書）");
                return aSb.ToString();
            }
            var aGroups = GroupBySeries(aBooks);
            int aMulti = aGroups.FindAll(g => g.Value.Count > 1).Count;
            // 壞檔數要出現在數字旁邊 —— 「共 N 本」靜默吸收讀不到的列，跟「讀空目錄不報錯」同族
            string aFailNote = aWarnings.Count > 0 ? $"，另有 {aWarnings.Count} 筆讀取失敗 ⚠ 見文末" : "";
            aSb.AppendLine($"📚 藏書架 — 共 {aBooks.Count} 本／{aGroups.Count} 個系列"
                           + $"（其中 {aMulti} 個是多冊系列，其餘為單本自成一系列）{aFailNote}");
            aSb.AppendLine();

            foreach (var g in aGroups)
            {
                SCP_ShelfBook aFirst = g.Value[0];
                bool aSingle = g.Value.Count == 1 && string.IsNullOrEmpty(aFirst.Series);
                string aName = aSingle ? $"《{aFirst.Title}》" : SCP_BooksClassification.SeriesPath(aReg, g.Key);
                aSb.AppendLine($"- {SCP_BooksClassification.KindLabel(aFirst.Kind)}　**{aName}**（目前共 {g.Value.Count} 冊）");
                if (!aSingle)
                    aSb.AppendLine($"    查書單：`{SCP_CmdRegistry.Invoke("book --arg op=series --arg series=" + g.Key)}`");
                else
                    aSb.AppendLine($"    id `{aFirst.Book}`　{aFirst.Persona}／{aFirst.Chapters} 章／{aFirst.Date}");
            }

            if (!string.IsNullOrEmpty(aRegErr)) aSb.AppendLine($"\n⚠ {aRegErr}");
            if (aWarnings.Count > 0)
            {
                aSb.AppendLine();
                aSb.AppendLine("⚠ 讀取失敗：");
                foreach (string w in aWarnings) aSb.AppendLine($"- {w}");
            }
            return aSb.ToString();
        }

        // ===========================================================
        // op=series —— 不帶 series：列所有已註冊系列；帶 series：列該系列的書單（含閱讀用 id）
        // ===========================================================
        public static string RenderSeries(string iDataRoot, string? iSeriesId)
        {
            var aWarnings = new List<string>();
            List<SCP_ShelfBook> aBooks = LoadShelf(iDataRoot, aWarnings);
            List<SCP_BookSeriesEntry> aReg = LoadSeries(iDataRoot, out string? aRegErr);
            var aSb = new StringBuilder();

            if (string.IsNullOrEmpty(iSeriesId))
            {
                var aGroups = GroupBySeries(aBooks).FindAll(g => !g.Key.StartsWith("#", StringComparison.Ordinal));
                aSb.AppendLine($"📚 已成系列的書（{aGroups.Count} 個）— 單本自成一系列的請走 `{SCP_CmdRegistry.Invoke("book --arg op=shelf")}`");
                aSb.AppendLine();
                foreach (var g in aGroups)
                    aSb.AppendLine($"- `{g.Key}` **{SCP_BooksClassification.SeriesPath(aReg, g.Key)}**（目前共 {g.Value.Count} 冊）");
                // 註冊了但架上一本都沒有的系列也要列 —— 否則它跟「不存在」長得一樣
                var aEmpty = aReg.FindAll(e => aGroups.FindIndex(g => g.Key == e.Id) < 0);
                if (aEmpty.Count > 0)
                {
                    aSb.AppendLine();
                    aSb.AppendLine("（已註冊但架上尚無書的系列）");
                    foreach (SCP_BookSeriesEntry e in aEmpty) aSb.AppendLine($"- `{e.Id}` {e.Title}（0 冊）");
                }
                if (!string.IsNullOrEmpty(aRegErr)) aSb.AppendLine($"\n⚠ {aRegErr}");
                return aSb.ToString();
            }

            var aMine = aBooks.FindAll(b => b.Series == iSeriesId);
            aMine.Sort(CompareInSeries);
            SCP_BookSeriesEntry? aEntry = SCP_BooksClassification.FindSeries(aReg, iSeriesId);
            if (aMine.Count == 0 && aEntry == null)
                return $"❌ 沒有這個系列：`{iSeriesId}` —— 用 `{SCP_CmdRegistry.Invoke("book --arg op=series")}`（不帶 series）看有哪些。";

            aSb.AppendLine($"📚 **{SCP_BooksClassification.SeriesPath(aReg, iSeriesId)}**（`{iSeriesId}`，目前共 {aMine.Count} 冊）");
            if (aEntry != null && !string.IsNullOrEmpty(aEntry.Note)) aSb.AppendLine($"> {aEntry.Note}");
            aSb.AppendLine();
            if (aMine.Count == 0)
            {
                // 空表格讀起來像「這個系列沒有書」—— 而上位系列的書其實掛在子系列上。明講「本層 0 冊」。
                aSb.AppendLine("（本層直接掛 0 冊 —— 書可能掛在下面的子系列上）");
            }
            else
            {
                aSb.AppendLine("| 冊 | id（閱讀用） | 書名 | 作者／捐贈者 | 章 | 日期 |");
                aSb.AppendLine("|---|---|---|---|---|---|");
                foreach (SCP_ShelfBook b in aMine)
                    aSb.AppendLine($"| {(b.Volume > 0 ? b.Volume.ToString() : "—")} | `{b.Book}` | 《{b.Title}》 "
                                   + $"| {b.Persona} | {b.Chapters} | {b.Date} |");
                aSb.AppendLine();
                aSb.AppendLine("全文在 `AgentCommands/Books/<id>/`。");
            }

            // 子系列（巢狀）：列出來，否則上位系列看起來是空的
            var aChildren = aReg.FindAll(e => e.Parent == iSeriesId);
            if (aChildren.Count > 0)
            {
                aSb.AppendLine();
                aSb.AppendLine("子系列：");
                foreach (SCP_BookSeriesEntry c in aChildren)
                    aSb.AppendLine($"- `{c.Id}` {c.Title}（{aBooks.FindAll(b => b.Series == c.Id).Count} 冊）");
            }
            if (!string.IsNullOrEmpty(aRegErr)) aSb.AppendLine($"\n⚠ {aRegErr}");
            return aSb.ToString();
        }

        // ===========================================================
        // op=classify —— 唯一的分類寫入通道
        // 數值影響：只改 _donation.json 的 kind/series/volume（＋補寫 origin）與 _series.json；不動錢。
        // ⚠ iSeries／iParentSeries 為 null ＝ 「沒傳」；空字串 ＝ 顯式「脫離系列／脫離上位」—— 兩件事。
        // ===========================================================
        public static string? Classify(string iDataRoot, string iBook, string iKindRaw, string? iSeries, string iVolumeRaw,
                                       string iSeriesTitle, string? iParentSeries, string iParentSeriesTitle,
                                       string iSeriesNote, out string? oError)
        {
            oError = null;
            string aDPath = SCP_BooksDonations.DonationPath(iDataRoot, iBook);
            if (!File.Exists(aDPath))
            {
                oError = $"《{iBook}》不在登記簿（{aDPath} 不存在）—— 先 publish / donate 才能分類";
                return null;
            }
            string aOrigText = SafeReadText(aDPath);
            SCP_JsonData? aD = SCP_BooksOps.LoadJson(aDPath, out string? aErr);
            if (aD == null) { oError = $"讀取 _donation.json 失敗：{aErr}"; return null; }

            SCP_BookOrigin aOrigin = SCP_BooksOps.OriginOf(aD, iBook);
            SCP_BookKind aKind = SCP_BooksOps.KindOf(aD, iBook);
            if (!string.IsNullOrEmpty(iKindRaw) && !SCP_BooksClassification.TryParseKind(iKindRaw, out aKind))
            {
                oError = $"未知 kind：{iKindRaw}（可用：{SCP_BooksClassification.AllKindKeys}）";
                return null;
            }

            string aNewSeries = SCP_BooksOps.SeriesOf(aD, iBook);
            if (iSeries != null) aNewSeries = iSeries.Trim();   // 顯式傳空字串＝脫離系列

            int aVolume = SCP_BooksOps.VolumeOf(aD);
            if (!string.IsNullOrEmpty(iVolumeRaw))
            {
                if (!int.TryParse(iVolumeRaw, out aVolume) || aVolume < 0)
                {
                    oError = $"volume 須為 ≥0 的整數（傳入 {iVolumeRaw}）";
                    return null;
                }
            }

            // 系列註冊：沒註冊過就必須給 title —— 不給就擋。
            // 🩸 理由：自動用 id 當 title 的話，打錯字會長出一個「看起來正常的新系列」，
            //   而它跟真正的新系列在畫面上一模一樣。
            SCP_JsonData? aRegTree = LoadRegistryTree(iDataRoot, out string? aRegErr);
            if (!string.IsNullOrEmpty(aRegErr)) { oError = aRegErr; return null; }
            string aRegNote = "";
            bool aRegChanged = false;
            if (!string.IsNullOrEmpty(aNewSeries))
            {
                aRegTree ??= SCP_JsonData.NewObject();
                if (!aRegTree.Contains("series")) aRegTree.Set("series", SCP_JsonData.NewArray());
                SCP_JsonData aArr = aRegTree["series"];

                SCP_JsonData? aE = FindEntry(aArr, aNewSeries);
                if (aE == null)
                {
                    if (string.IsNullOrEmpty(iSeriesTitle))
                    {
                        oError = $"系列 `{aNewSeries}` 尚未註冊 —— 首次使用要帶 --arg series_title=<系列顯示名>"
                                 + "（不自動用 id 當名字：打錯字會長出一個看起來正常的新系列）";
                        return null;
                    }
                    aE = NewEntry(aNewSeries, iSeriesTitle);
                    aArr.Add(aE);
                    aRegChanged = true;
                    aRegNote = $"（新註冊系列 `{aNewSeries}` = {iSeriesTitle}）";
                }
                else if (!string.IsNullOrEmpty(iSeriesTitle) && aE.GetString("title", "") != iSeriesTitle)
                {
                    aE.Set("title", SCP_JsonData.NewString(iSeriesTitle));
                    aRegChanged = true;
                    aRegNote = $"（系列 `{aNewSeries}` 更名為 {iSeriesTitle}）";
                }
                if (iParentSeries != null)
                {
                    string aPid = iParentSeries.Trim();
                    // 上位系列同樣要有名字才准掛 —— 否則巢狀路徑會印出一個裸 id，
                    // 而那跟「這個系列真的叫這個名字」在畫面上分不出來。
                    if (!string.IsNullOrEmpty(aPid) && FindEntry(aArr, aPid) == null)
                    {
                        if (string.IsNullOrEmpty(iParentSeriesTitle))
                        {
                            oError = $"上位系列 `{aPid}` 尚未註冊 —— 要帶 --arg parent_series_title=<顯示名>";
                            return null;
                        }
                        aArr.Add(NewEntry(aPid, iParentSeriesTitle));
                    }
                    if (aE.GetString("parent", "") != aPid || !aE.Contains("parent"))
                    {
                        aE.Set("parent", SCP_JsonData.NewString(aPid));
                        aRegChanged = true;
                    }
                }
                if (!string.IsNullOrEmpty(iSeriesNote) && aE.GetString("note", "") != iSeriesNote)
                {
                    aE.Set("note", SCP_JsonData.NewString(iSeriesNote));
                    aRegChanged = true;
                }
                // 只有真的改了才寫：沒改就不碰檔 ⇒ 重複 classify 不會整檔重寫（BUG-6 那族）
                if (aRegChanged || !File.Exists(SeriesRegistryPath(iDataRoot)))
                    SaveRegistry(iDataRoot, aRegTree);
            }

            SCP_BooksOps.Stamp(aD, aOrigin, aKind, aNewSeries, aVolume);
            SaveDonationKeepingStyle(aDPath, aD, aOrigText);

            // 印 ✓ 不算數 —— 回讀落地的檔案再報
            SCP_JsonData? aBack = SCP_BooksOps.LoadJson(aDPath, out _);
            List<SCP_BookSeriesEntry> aRegAfter = LoadSeries(iDataRoot, out _);
            string aTxt = string.IsNullOrEmpty(aNewSeries)
                ? "（無系列，自成一系列）"
                : $"{SCP_BooksClassification.SeriesPath(aRegAfter, aNewSeries)}　第 {(aVolume > 0 ? aVolume.ToString() : "—")} 冊";
            return $"✅ 《{aD.GetString(SCP_BooksDonations.Key_Title, iBook)}》分類完成 {aRegNote}\n"
                   + $"- id：`{iBook}`\n"
                   + $"- origin：`{aBack?.GetString(SCP_BooksClassification.Key_Origin, "?") ?? "?"}`"
                   + $"　kind：`{aBack?.GetString(SCP_BooksClassification.Key_Kind, "?") ?? "?"}`\n"
                   + $"- 系列：{aTxt}\n"
                   + "（回讀 `_donation.json` 確認，非記憶體值）";
        }

        static string SafeReadText(string iPath)
        {
            try { return File.ReadAllText(iPath, Encoding.UTF8); }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        // 區塊職責：把 `_donation.json` 寫回去，**版面照原檔**。
        // 物理意義：磁碟上同時有兩種 writer 的沉積 —— tab 縮排／冒號後無空格／結尾無換行（Unity 舊 writer，
        //          =`SCP_JsonStyle.UclLegacy`，量到 14 份）與 2 空格／冒號後有空格（Senate／python，量到 29 份），
        //          結尾換行有無也不一致。classify 只該改四個分類欄位，⛔ 不該順手把整份檔換一種版面。
        // 🩸 血證（2026-10-05，TASK-0403 第一次實跑）：對 `farseer-trilogy_01` 做「不改任何值」的 classify，
        //   `_donation.json` 被 2 空格版面整檔重寫 ⇒ 內容逐鍵相同、位元組不同；這正是 TASK-0234 警告的 BUG-6 形狀。
        // 數值影響：原檔含 tab 縮排的鍵行 ⇒ 用 UclLegacy；否則沿用 `SCP_BooksOps.SaveJson` 的 2 空格。
        //          結尾換行有無照原檔；原檔讀不到（空字串）⇒ 走 `SCP_BooksOps.SaveJson` 預設。
        static void SaveDonationKeepingStyle(string iPath, SCP_JsonData iData, string iOrigText)
        {
            if (iOrigText.Length == 0) { SCP_BooksOps.SaveJson(iPath, iData); return; }
            bool aLegacy = iOrigText.Contains("\n\t\"");
            bool aTrailingNewline = iOrigText.EndsWith("\n", StringComparison.Ordinal);
            string aBody = aLegacy
                ? SCP_JsonWriter.Write(iData, SCP_JsonStyle.UclLegacy)
                : SCP_JsonWriter.Write(iData, iIndented: true, iIndent: "  ");
            SCP_TextFile.WriteCrLf(iPath, aBody + (aTrailingNewline ? "\n" : ""));
        }

        static SCP_JsonData? FindEntry(SCP_JsonData iArr, string iId)
        {
            for (int i = 0; i < iArr.Count; i++)
                if (iArr[i].GetString("id", "") == iId) return iArr[i];
            return null;
        }

        /// <summary>新的一筆：四鍵齊全且鍵序固定（id／title／parent／note），與舊 writer 序列化 typed model 的產物同形。</summary>
        static SCP_JsonData NewEntry(string iId, string iTitle)
        {
            SCP_JsonData aE = SCP_JsonData.NewObject();
            aE.Set("id", SCP_JsonData.NewString(iId));
            aE.Set("title", SCP_JsonData.NewString(iTitle));
            aE.Set("parent", SCP_JsonData.NewString(""));
            aE.Set("note", SCP_JsonData.NewString(""));
            return aE;
        }

        /// <summary>`_series.json` 的 writer：版面走 <see cref="SCP_JsonStyle.UclLegacy"/>（舊 writer 的形狀，CRLF）。</summary>
        /// <remarks>結尾換行**照原檔**（量到的現況是沒有）；檔還不存在 ⇒ 沿用舊 writer 的無結尾換行。</remarks>
        static void SaveRegistry(string iDataRoot, SCP_JsonData iTree)
        {
            string aPath = SeriesRegistryPath(iDataRoot);
            bool aTrailingNewline = SafeReadText(aPath).EndsWith("\n", StringComparison.Ordinal);
            SCP_TextFile.WriteCrLf(aPath, SCP_JsonWriter.Write(iTree, SCP_JsonStyle.UclLegacy) + (aTrailingNewline ? "\n" : ""));
        }
    }
}
