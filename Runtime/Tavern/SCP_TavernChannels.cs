// 區塊職責：**頻道（酒館房間）的管理資料** —— 頻道分類清單、每個頻道的分類與封存狀態（TASK-0318）。唯一讀寫層。
// 物理意義：
//   · 分類清單：`ChatTavern/channel_categories.json`（全域一份，例如 Main／TRPG）。**要先新增分類，頻道才能選它。**
//   · 頻道設定：`<房間資料夾>/channel.json`（category／archived_at）。
//     ⛔ **不寫進房間的 `meta.json`**：Unity `UCL_ChatTavernIO.SaveRoomMeta` 用 JsonUtility 整份重寫那個檔，
//       不認得的欄位會被靜默丟掉（createroom 補 owner_agent／mirror_kinds 時就會觸發）。
//   · 頻道分類 ≠ 訊息分類：`tavern_routing.json` 看的是**每則訊息**的 category；這裡是**頻道本身**的分類，
//     給 TASK-0316 Outbound 依頻道路由用（Tim 2026-09-28）。
//   · 封存 ＝ 把整個房間資料夾搬到 `ChatTavern/rooms_archive/<room>/`（Tim 2026-09-28）；**不刪**任何訊息，
//     取消封存就搬回 `rooms/`。封存狀態＝**資料夾在哪一邊**（⛔ 不另存旗標：兩份真相會漂）。
//     搬走之後 Unity 與 `SCP_TavernRead` 都看不到它（兩者都只列 `rooms/`）；寫入端（`tavern-write`）會擋下對封存房的發文
//     —— 否則寫入端會在 `rooms/` 自己建一個同名新房、seq 還接著舊號。
//   · 完全不依賴 Unity（Tim 2026-09-28：酒館之後全面遷移到 Senate）。
// 數值影響：讀取零寫入。寫入走暫存檔再換檔，寫完回讀；驗證不過一律零寫入。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Tavern
{
    /// <summary>一個頻道分類（例如 Main、TRPG）。</summary>
    public sealed class SCP_ChannelCategory
    {
        public string Name = "";
        public string Description = "";
    }

    /// <summary>一個頻道的管理設定（`channel.json`）。沒有檔 ＝ 未分類。</summary>
    public sealed class SCP_ChannelSettings
    {
        /// <summary>頻道分類名；空 ＝ 未分類。</summary>
        public string Category = "";
        /// <summary>封存了嗎 ＝ 資料夾在 `rooms_archive/`（讀取時由位置決定，⛔ 不存進檔）。</summary>
        public bool Archived;
        /// <summary>封存時間（UTC ISO）；沒封存 ⇒ 空。</summary>
        public string ArchivedAt = "";
    }

    /// <summary>頻道管理頁的一列：房間本身＋活動＋設定。</summary>
    public sealed class SCP_ChannelInfo
    {
        public string Room = "";
        /// <summary>`meta.json` 的 name（沒有就是空）。</summary>
        public string Name = "";
        public int LastSeq;
        /// <summary>最後一則的 ts（原文）；沒有訊息 ⇒ 空。</summary>
        public string LastTs = "";
        public SCP_ChannelSettings Settings = new SCP_ChannelSettings();
        /// <summary>分類有填、但不在分類清單裡（手改過檔或分類被改名）⇒ 顯示端要出聲。</summary>
        public bool CategoryMissing;
        /// <summary>使用中與封存區都有同名房（取消封存會撞名）。</summary>
        public bool ArchiveConflict;
    }

    public static class SCP_TavernChannels
    {
        public const string CategoriesFileName = "channel_categories.json";
        public const string SettingsFileName = "channel.json";
        public const string ArchiveDirName = "rooms_archive";

        /// <summary>
        /// **主頻道**（Tim 2026-09-28）：程式裡有特殊地位 —— 不存在就自動建立（<see cref="EnsureMainChannel"/>）、
        /// ⛔ 不能被封存；新接的 Discord 頻道預設接到它。
        /// </summary>
        public const string MainChannelId = "tavern";
        public const string MainChannelName = "酒館主廳 (Tavern)";

        /// <summary>
        /// **保留分類**（Tim 2026-09-28，TASK-0320）：必須存在（沒有就建）、⛔ 不能刪；主頻道沒有分類時預設綁它。
        /// Outbound 的 webhook 綁在分類上 ⇒ 至少要有一個分類，主頻道才有地方去。
        /// </summary>
        public const string MainCategory = "Main";

        /// <summary>確保分類清單裡有 <see cref="MainCategory"/>。清單讀不了（壞檔）⇒ 不動它、回 false。</summary>
        public static bool EnsureMainCategory(string iDataRoot, out bool oCreated)
        {
            oCreated = false;
            List<SCP_ChannelCategory> aCats = LoadCategories(iDataRoot, out string? aErr);
            if (aErr != null) return false;
            if (aCats.Any(c => string.Equals(c.Name, MainCategory, StringComparison.OrdinalIgnoreCase))) return true;
            aCats.Insert(0, new SCP_ChannelCategory { Name = MainCategory, Description = "主分類（保留，不能刪）" });
            if (!WriteCategories(iDataRoot, aCats, out _)) return false;
            oCreated = true;
            return true;
        }

        /// <summary>
        /// 確保主頻道存在：使用中 ⇒ 什麼都不做；被人搬進封存區 ⇒ 搬回來；都沒有 ⇒ 建 `rooms/tavern/meta.json`。
        /// <paramref name="oCreated"/>＝這一次有沒有動到磁碟。
        /// </summary>
        public static bool EnsureMainChannel(string iDataRoot, out bool oCreated)
        {
            bool aOk = EnsureMainChannelDir(iDataRoot, out oCreated);
            // 保留分類＋主頻道預設綁 Main（TASK-0320）—— 只在主頻道「沒有分類」時補，⛔ 不蓋掉人選過的分類
            if (EnsureMainCategory(iDataRoot, out bool aCatCreated) && aCatCreated) oCreated = true;
            if (aOk && LoadSettings(iDataRoot, MainChannelId).Category.Length == 0
                && TrySetCategory(iDataRoot, MainChannelId, MainCategory, out _)) oCreated = true;
            return aOk;
        }

        static bool EnsureMainChannelDir(string iDataRoot, out bool oCreated)
        {
            oCreated = false;
            string aActive = ActiveDir(iDataRoot, MainChannelId);
            if (IsChannelDir(aActive)) return true;
            try
            {
                if (IsChannelDir(ArchivedDir(iDataRoot, MainChannelId)) && !Directory.Exists(aActive))
                {
                    Directory.Move(ArchivedDir(iDataRoot, MainChannelId), aActive);   // 主頻道不該在封存區（手動搬的）
                    oCreated = true;
                    return true;
                }
                Directory.CreateDirectory(aActive);
                var j = SCP_JsonData.NewObject();
                j.Set("id", MainChannelId);
                j.Set("name", MainChannelName);
                j.Set("description", "主頻道（Senate 自動建立）。沒指定主題的對話都進這裡。");
                j.Set("created_at", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
                j.Set("disable_quest_mirror", false);

                if (!WriteJson(aActive + "/meta.json", j, out _)) return false;
                oCreated = true;
                return IsChannelDir(aActive);
            }
            catch (Exception) { return false; }
        }
        public const int SchemaVersion = 1;
        public const int MaxCategoryNameLength = 32;

        public static string CategoriesPath(string iDataRoot)
            => (Path.GetDirectoryName(SCP_TavernMsgIndex.RoomsRoot(iDataRoot)) ?? iDataRoot).Replace('\\', '/')
               + "/" + CategoriesFileName;

        /// <summary>`ChatTavern/rooms_archive/`（與 `rooms/` 同層 —— ⛔ 不放進 `rooms/` 底下：Unity 會把它當成一個房）。</summary>
        public static string ArchiveRoot(string iDataRoot)
            => (Path.GetDirectoryName(SCP_TavernMsgIndex.RoomsRoot(iDataRoot)) ?? iDataRoot).Replace('\\', '/')
               + "/" + ArchiveDirName;

        static string ActiveDir(string iDataRoot, string iRoom) => SCP_TavernRooms.RoomDir(iDataRoot, iRoom);
        static string ArchivedDir(string iDataRoot, string iRoom) => ArchiveRoot(iDataRoot) + "/" + iRoom;

        /// <summary>這個頻道現在住的資料夾：使用中優先，否則封存區。</summary>
        public static string ChannelDir(string iDataRoot, string iRoom)
            => IsArchived(iDataRoot, iRoom) ? ArchivedDir(iDataRoot, iRoom) : ActiveDir(iDataRoot, iRoom);

        public static string SettingsPath(string iDataRoot, string iRoom)
            => ChannelDir(iDataRoot, iRoom) + "/" + SettingsFileName;

        // ── 分類清單 ─────────────────────────────────────────────────

        /// <summary>讀分類清單（照檔案順序）。檔不在 ⇒ 空清單；壞檔 ⇒ 空清單＋<paramref name="oError"/>。</summary>
        public static List<SCP_ChannelCategory> LoadCategories(string iDataRoot, out string? oError)
        {
            oError = null;
            var aOut = new List<SCP_ChannelCategory>();
            string aPath = CategoriesPath(iDataRoot);
            if (!File.Exists(aPath)) return aOut;
            try
            {
                SCP_JsonData aJson = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                SCP_JsonData aArr = aJson["categories"];
                if (!aArr.IsArray) { oError = $"{aPath} 沒有 categories 陣列"; return aOut; }
                foreach (SCP_JsonData c in aArr)
                {
                    string aName = c.GetString("name", "").Trim();
                    if (aName.Length == 0) continue;
                    aOut.Add(new SCP_ChannelCategory { Name = aName, Description = c.GetString("description", "") });
                }
            }
            catch (Exception e) { oError = $"讀不了 {aPath}：{e.Message}"; }
            return aOut;
        }

        public static List<SCP_ChannelCategory> LoadCategories(string iDataRoot) => LoadCategories(iDataRoot, out _);

        /// <summary>分類名合法嗎：去頭尾空白後 1～32 字、沒有控制字元與斜線。</summary>
        public static string? ValidateCategoryName(string iName)
        {
            string aName = (iName ?? "").Trim();
            if (aName.Length == 0) return "分類名不能是空的";
            if (aName.Length > MaxCategoryNameLength) return $"分類名最多 {MaxCategoryNameLength} 字（收到 {aName.Length} 字）";
            if (aName.Any(ch => char.IsControl(ch) || ch == '/' || ch == '\\')) return $"分類名不能有控制字元或斜線：'{aName}'";
            return null;
        }

        /// <summary>新增分類。同名（不分大小寫）已存在 ⇒ 拒絕、零寫入。</summary>
        public static bool TryAddCategory(string iDataRoot, string iName, string iDescription, out string? oError)
        {
            oError = ValidateCategoryName(iName);
            if (oError != null) return false;
            string aName = iName.Trim();
            List<SCP_ChannelCategory> aCats = LoadCategories(iDataRoot, out string? aReadErr);
            if (aReadErr != null) { oError = "分類清單讀不了，⛔ 不覆寫：" + aReadErr; return false; }
            SCP_ChannelCategory? aDup = aCats.FirstOrDefault(c => string.Equals(c.Name, aName, StringComparison.OrdinalIgnoreCase));
            if (aDup != null) { oError = $"已經有這個分類了：'{aDup.Name}'"; return false; }
            aCats.Add(new SCP_ChannelCategory { Name = aName, Description = (iDescription ?? "").Trim() });
            return WriteCategories(iDataRoot, aCats, out oError);
        }

        /// <summary>刪除分類。還有頻道在用（含已封存的）⇒ 拒絕並列出是哪些頻道、零寫入。</summary>
        public static bool TryRemoveCategory(string iDataRoot, string iName, out string? oError)
        {
            oError = null;
            string aName = (iName ?? "").Trim();
            List<SCP_ChannelCategory> aCats = LoadCategories(iDataRoot, out string? aReadErr);
            if (aReadErr != null) { oError = "分類清單讀不了，⛔ 不覆寫：" + aReadErr; return false; }
            int aIdx = aCats.FindIndex(c => string.Equals(c.Name, aName, StringComparison.OrdinalIgnoreCase));
            if (aIdx < 0) { oError = $"沒有這個分類：'{aName}'"; return false; }
            if (string.Equals(aCats[aIdx].Name, MainCategory, StringComparison.OrdinalIgnoreCase))
            { oError = $"'{MainCategory}' 是保留分類 ⇒ ⛔ 不能刪"; return false; }
            List<string> aUsers = RoomsUsingCategory(iDataRoot, aCats[aIdx].Name);
            if (aUsers.Count > 0)
            {
                oError = $"還有 {aUsers.Count} 個頻道在用 '{aCats[aIdx].Name}'（{string.Join("、", aUsers)}）⇒ 先把它們改成別的分類";
                return false;
            }
            aCats.RemoveAt(aIdx);
            return WriteCategories(iDataRoot, aCats, out oError);
        }

        /// <summary>用這個分類的頻道（含已封存的，照名稱排序）。</summary>
        public static List<string> RoomsUsingCategory(string iDataRoot, string iCategory)
            => EnumerateChannelIds(iDataRoot)
                .Where(r => string.Equals(LoadSettings(iDataRoot, r).Category, iCategory, StringComparison.OrdinalIgnoreCase))
                .ToList();

        static bool WriteCategories(string iDataRoot, List<SCP_ChannelCategory> iCats, out string? oError)
        {
            oError = null;
            var aRoot = SCP_JsonData.NewObject();
            aRoot.Set("schema_version", SchemaVersion);
            aRoot.Set("note", "頻道分類清單（TASK-0318）。頻道要先有分類在這裡，才能選它。後台：senate ui --page channels");
            var aArr = SCP_JsonData.NewArray();
            foreach (SCP_ChannelCategory c in iCats)
            {
                var o = SCP_JsonData.NewObject();
                o.Set("name", c.Name);
                o.Set("description", c.Description);
                aArr.Add(o);
            }
            aRoot.Set("categories", aArr);
            string aPath = CategoriesPath(iDataRoot);
            if (!WriteJson(aPath, aRoot, out oError)) return false;

            List<SCP_ChannelCategory> aBack = LoadCategories(iDataRoot, out string? aBackErr);
            if (aBackErr != null || aBack.Count != iCats.Count
                || !aBack.Select(c => c.Name).SequenceEqual(iCats.Select(c => c.Name)))
            { oError = $"寫完回讀對不上（{aPath}）：{aBackErr ?? "分類清單不同"}"; return false; }
            return true;
        }

        // ── 頻道設定 ─────────────────────────────────────────────────

        static bool IsChannelDir(string iDir)
            => File.Exists(Path.Combine(iDir, "meta.json")) || Directory.Exists(Path.Combine(iDir, "messages"));

        static IEnumerable<string> ChannelDirsUnder(string iRoot)
        {
            if (!Directory.Exists(iRoot)) yield break;
            foreach (string aDir in Directory.GetDirectories(iRoot))
            {
                string aId = Path.GetFileName(aDir);
                if (aId.StartsWith("_", StringComparison.Ordinal) || aId.StartsWith(".", StringComparison.Ordinal)) continue;
                if (IsChannelDir(aDir)) yield return aId;
            }
        }

        /// <summary>
        /// 所有頻道 id（使用中＋封存），照名稱排序。判準：資料夾裡有 `meta.json` **或** `messages/`
        /// （兩種房間都存在：只有訊息沒有 meta 的舊房，也要能被管理）。
        /// </summary>
        public static List<string> EnumerateChannelIds(string iDataRoot)
        {
            var aSet = new HashSet<string>(ChannelDirsUnder(SCP_TavernMsgIndex.RoomsRoot(iDataRoot)), StringComparer.Ordinal);
            foreach (string a in ChannelDirsUnder(ArchiveRoot(iDataRoot))) aSet.Add(a);
            var aOut = aSet.ToList();
            aOut.Sort(StringComparer.OrdinalIgnoreCase);
            return aOut;
        }

        public static bool ChannelExists(string iDataRoot, string iRoom)
            => IsSafeRoomId(iRoom) && (IsChannelDir(ActiveDir(iDataRoot, iRoom)) || IsChannelDir(ArchivedDir(iDataRoot, iRoom)));

        /// <summary>
        /// 封存了嗎 ＝ **它住在 `rooms_archive/`、而 `rooms/` 底下沒有同名房**。
        /// ⚠ 兩邊都有（封存後又有人在 rooms/ 長出同名房）⇒ 算使用中，並由 <see cref="HasArchiveConflict"/> 出聲。
        /// </summary>
        public static bool IsArchived(string iDataRoot, string iRoom)
            => IsSafeRoomId(iRoom) && !IsChannelDir(ActiveDir(iDataRoot, iRoom)) && IsChannelDir(ArchivedDir(iDataRoot, iRoom));

        /// <summary>使用中與封存區都有同名房 ⇒ 取消封存會撞名，顯示端要出聲。</summary>
        public static bool HasArchiveConflict(string iDataRoot, string iRoom)
            => IsSafeRoomId(iRoom) && IsChannelDir(ActiveDir(iDataRoot, iRoom)) && IsChannelDir(ArchivedDir(iDataRoot, iRoom));

        /// <summary>一個頻道的設定。檔不在或讀不了 ⇒ 預設（未分類）。`Archived` 看資料夾位置，⛔ 不看檔內欄位。</summary>
        public static SCP_ChannelSettings LoadSettings(string iDataRoot, string iRoom)
        {
            var aOut = new SCP_ChannelSettings();
            if (!IsSafeRoomId(iRoom)) return aOut;
            aOut.Archived = IsArchived(iDataRoot, iRoom);
            string aPath = SettingsPath(iDataRoot, iRoom);
            if (!File.Exists(aPath)) return aOut;
            try
            {
                SCP_JsonData aJson = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                aOut.Category = aJson.GetString("category", "").Trim();
                aOut.ArchivedAt = aOut.Archived ? aJson.GetString("archived_at", "") : "";
            }
            catch (Exception) { /* 壞檔 ⇒ 當作沒設；下一次寫入會蓋回合法內容 */ }
            return aOut;
        }

        /// <summary>設頻道分類。空字串 ⇒ 未分類。分類不在清單裡、或頻道不存在 ⇒ 拒絕、零寫入。</summary>
        public static bool TrySetCategory(string iDataRoot, string iRoom, string iCategory, out string? oError)
        {
            oError = null;
            if (!ChannelExists(iDataRoot, iRoom)) { oError = $"沒有這個頻道：'{iRoom}'"; return false; }
            string aCat = (iCategory ?? "").Trim();
            if (aCat.Length > 0)
            {
                SCP_ChannelCategory? aHit = LoadCategories(iDataRoot)
                    .FirstOrDefault(c => string.Equals(c.Name, aCat, StringComparison.OrdinalIgnoreCase));
                if (aHit == null) { oError = $"沒有這個分類：'{aCat}' ⇒ 先到分類清單新增它"; return false; }
                aCat = aHit.Name;   // 存清單裡的寫法（大小寫以清單為準）
            }
            SCP_ChannelSettings aSet = LoadSettings(iDataRoot, iRoom);
            aSet.Category = aCat;
            return WriteSettings(iDataRoot, iRoom, aSet, out oError);
        }

        /// <summary>
        /// 封存 ＝ 把整個房間資料夾 `rooms/&lt;room&gt;/` **搬到** `rooms_archive/&lt;room&gt;/`（Tim 2026-09-28）；取消封存搬回來。
        /// ⚠ 目的地已有同名資料夾 ⇒ 拒絕、零搬動（⛔ 不合併兩份訊息）。搬動是同一顆磁碟上的 rename，失敗就是原封不動。
        /// ⚠ 有檔案被別的行程開著時 Windows 會拒絕搬 ⇒ 回報、零搬動。
        /// </summary>
        public static bool TrySetArchived(string iDataRoot, string iRoom, bool iArchived, out string? oError)
        {
            oError = null;
            if (!ChannelExists(iDataRoot, iRoom)) { oError = $"沒有這個頻道：'{iRoom}'"; return false; }
            if (iArchived && iRoom == MainChannelId) { oError = $"'{MainChannelId}' 是主頻道 ⇒ ⛔ 不能封存"; return false; }
            string aActive = ActiveDir(iDataRoot, iRoom);
            string aArchived = ArchivedDir(iDataRoot, iRoom);
            string aFrom = iArchived ? aActive : aArchived;
            string aTo = iArchived ? aArchived : aActive;
            if (!IsChannelDir(aFrom))
            { oError = iArchived ? $"'{iRoom}' 已經是封存的" : $"'{iRoom}' 本來就沒有封存"; return false; }
            if (Directory.Exists(aTo))
            { oError = $"目的地已經有同名資料夾（{aTo}）⇒ ⛔ 不合併，請先人工處理"; return false; }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aTo) ?? ".");
                Directory.Move(aFrom, aTo);
            }
            catch (Exception e) { oError = $"搬不動（{aFrom} → {aTo}）：{e.Message}（原封不動）"; return false; }

            if (IsArchived(iDataRoot, iRoom) != iArchived) { oError = $"搬完回讀對不上：'{iRoom}' 的位置不是預期的那一邊"; return false; }
            // 記封存時間（寫在搬過去的那份 channel.json；寫不進去不回捲搬動 —— 位置才是真相，時間只是附註）
            SCP_ChannelSettings aSet = LoadSettings(iDataRoot, iRoom);
            aSet.ArchivedAt = iArchived ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : "";
            if (!WriteSettings(iDataRoot, iRoom, aSet, out string? aNoteErr))
                oError = "已搬動，但封存時間沒記上：" + aNoteErr;
            return true;
        }

        static bool WriteSettings(string iDataRoot, string iRoom, SCP_ChannelSettings iSet, out string? oError)
        {
            var aRoot = SCP_JsonData.NewObject();
            aRoot.Set("schema_version", SchemaVersion);
            aRoot.Set("category", iSet.Category);
            aRoot.Set("archived_at", iSet.ArchivedAt);
            string aPath = SettingsPath(iDataRoot, iRoom);
            if (!WriteJson(aPath, aRoot, out oError)) return false;
            SCP_ChannelSettings aBack = LoadSettings(iDataRoot, iRoom);
            if (aBack.Category != iSet.Category)
            { oError = $"寫完回讀對不上（{aPath}）"; return false; }
            return true;
        }

        // ── 頻道清單（管理頁用）──────────────────────────────────────

        /// <summary>全部頻道的管理資料。<paramref name="iIncludeArchived"/>=false ⇒ 略過封存的。</summary>
        public static List<SCP_ChannelInfo> ListChannels(string iDataRoot, bool iIncludeArchived = true)
        {
            var aCatNames = new HashSet<string>(LoadCategories(iDataRoot).Select(c => c.Name), StringComparer.OrdinalIgnoreCase);
            var aOut = new List<SCP_ChannelInfo>();
            foreach (string aRoom in EnumerateChannelIds(iDataRoot))
            {
                SCP_ChannelSettings aSet = LoadSettings(iDataRoot, aRoom);
                if (aSet.Archived && !iIncludeArchived) continue;
                string aDir = ChannelDir(iDataRoot, aRoom);
                var aInfo = new SCP_ChannelInfo
                {
                    Room = aRoom,
                    Name = ReadMetaName(aDir),
                    Settings = aSet,
                    CategoryMissing = aSet.Category.Length > 0 && !aCatNames.Contains(aSet.Category),
                    ArchiveConflict = HasArchiveConflict(iDataRoot, aRoom),
                };
                ReadLast(aDir, out aInfo.LastSeq, out aInfo.LastTs);
                aOut.Add(aInfo);
            }
            return aOut;
        }

        /// <summary>
        /// 最後一則的 seq 與 ts：`messages/` 下最新日期夾裡最大的 `&lt;seq&gt;.json`。
        /// ⚠ 取訊息檔本身，⛔ 不讀 `_seq.txt` —— 2026-09-28 實測 59 房只有 5 房有那個檔，
        ///   讀它的話 54 房全印 0，跟「沒有訊息」同形。
        /// ⚠ 自己掃目錄而不走 `SCP_TavernRead`：那一層只認 `rooms/`，封存區的房它讀不到。
        /// </summary>
        static void ReadLast(string iRoomDir, out int oSeq, out string oTs)
        {
            oSeq = 0; oTs = "";
            string aMsgs = Path.Combine(iRoomDir, "messages");
            if (!Directory.Exists(aMsgs)) return;
            try
            {
                foreach (string aDay in Directory.GetDirectories(aMsgs).OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal))
                {
                    string? aFile = Directory.GetFiles(aDay, "*.json")
                        .OrderByDescending(f => Path.GetFileNameWithoutExtension(f), StringComparer.Ordinal).FirstOrDefault();
                    if (aFile == null) continue;
                    int.TryParse(Path.GetFileNameWithoutExtension(aFile), NumberStyles.Integer, CultureInfo.InvariantCulture, out oSeq);
                    oTs = SCP_JsonParser.Parse(File.ReadAllText(aFile, Encoding.UTF8)).GetString("ts", "");
                    return;
                }
            }
            catch (Exception) { /* 讀不到最後一則 ⇒ 時間留空（顯示端印「—」），不擋整張清單 */ }
        }

        static string ReadMetaName(string iRoomDir)
        {
            string aPath = Path.Combine(iRoomDir, "meta.json");
            if (!File.Exists(aPath)) return "";
            try { return SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8)).GetString("name", ""); }
            catch (Exception) { return ""; }
        }

        // ── 小工具 ───────────────────────────────────────────────────

        static bool WriteJson(string iPath, SCP_JsonData iJson, out string? oError)
        {
            oError = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iPath) ?? ".");
                string aTmp = iPath + ".tmp";
                File.WriteAllText(aTmp, iJson.ToJson(true), new UTF8Encoding(false));
                SCP.Core.Io.SCP_TextFile.ReplaceOrMove(aTmp, iPath);
                return true;
            }
            catch (Exception e) { oError = $"寫不進去（{iPath}）：{e.Message}"; return false; }
        }

        /// <summary>房間 id 只能是一層資料夾名 —— ⛔ 不讓參數把讀寫帶出 `rooms/`。</summary>
        static bool IsSafeRoomId(string iRoom)
            => !string.IsNullOrWhiteSpace(iRoom)
               && iRoom.IndexOfAny(new[] { '/', '\\', ':' }) < 0
               && iRoom != "." && iRoom != ".."
               && iRoom.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}
