// 區塊職責：**頻道（酒館房間）的管理資料** —— 頻道分類清單、每個頻道的分類與封存狀態（TASK-0318）。唯一讀寫層。
// 物理意義：
//   · 分類清單：`ChatTavern/channel_categories.json`（全域一份，例如 Main／TRPG）。**要先新增分類，頻道才能選它。**
//   · 頻道設定：`ChatTavern/rooms/<room>/channel.json`（category／archived／archived_at）。
//     ⛔ **不寫進房間的 `meta.json`**：Unity `UCL_ChatTavernIO.SaveRoomMeta` 用 JsonUtility 整份重寫那個檔，
//       不認得的欄位會被靜默丟掉（createroom 補 owner_agent／mirror_kinds 時就會觸發）。
//   · 頻道分類 ≠ 訊息分類：`tavern_routing.json` 看的是**每則訊息**的 category；這裡是**頻道本身**的分類，
//     給 TASK-0316 Outbound 依頻道路由用（Tim 2026-09-28）。
//   · 封存只是一個旗標：**不刪、不搬**任何訊息；顯示端預設不列出封存的頻道。
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

    /// <summary>一個頻道的管理設定（`channel.json`）。沒有檔 ＝ 未分類、未封存。</summary>
    public sealed class SCP_ChannelSettings
    {
        /// <summary>頻道分類名；空 ＝ 未分類。</summary>
        public string Category = "";
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
    }

    public static class SCP_TavernChannels
    {
        public const string CategoriesFileName = "channel_categories.json";
        public const string SettingsFileName = "channel.json";
        public const int SchemaVersion = 1;
        public const int MaxCategoryNameLength = 32;

        public static string CategoriesPath(string iDataRoot)
            => (Path.GetDirectoryName(SCP_TavernMsgIndex.RoomsRoot(iDataRoot)) ?? iDataRoot).Replace('\\', '/')
               + "/" + CategoriesFileName;

        public static string SettingsPath(string iDataRoot, string iRoom)
            => SCP_TavernRooms.RoomDir(iDataRoot, iRoom) + "/" + SettingsFileName;

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

        /// <summary>
        /// 所有頻道（房間）id，照名稱排序。判準：`rooms/` 底下有 `meta.json` **或** `messages/` 的資料夾
        /// （兩種房間都存在：只有訊息沒有 meta 的舊房，也要能被管理）。
        /// </summary>
        public static List<string> EnumerateChannelIds(string iDataRoot)
        {
            var aOut = new List<string>();
            string aRoot = SCP_TavernMsgIndex.RoomsRoot(iDataRoot);
            if (!Directory.Exists(aRoot)) return aOut;
            foreach (string aDir in Directory.GetDirectories(aRoot))
            {
                string aId = Path.GetFileName(aDir);
                if (aId.StartsWith("_", StringComparison.Ordinal) || aId.StartsWith(".", StringComparison.Ordinal)) continue;
                if (File.Exists(Path.Combine(aDir, "meta.json")) || Directory.Exists(Path.Combine(aDir, "messages")))
                    aOut.Add(aId);
            }
            aOut.Sort(StringComparer.OrdinalIgnoreCase);
            return aOut;
        }

        public static bool ChannelExists(string iDataRoot, string iRoom)
            => IsSafeRoomId(iRoom) && EnumerateChannelIds(iDataRoot).Contains(iRoom);

        /// <summary>一個頻道的設定。檔不在或讀不了 ⇒ 預設（未分類、未封存）。</summary>
        public static SCP_ChannelSettings LoadSettings(string iDataRoot, string iRoom)
        {
            var aOut = new SCP_ChannelSettings();
            if (!IsSafeRoomId(iRoom)) return aOut;
            string aPath = SettingsPath(iDataRoot, iRoom);
            if (!File.Exists(aPath)) return aOut;
            try
            {
                SCP_JsonData aJson = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                aOut.Category = aJson.GetString("category", "").Trim();
                aOut.Archived = aJson.GetBool("archived", false);
                aOut.ArchivedAt = aJson.GetString("archived_at", "");
            }
            catch (Exception) { /* 壞檔 ⇒ 當作沒設；下一次寫入會蓋回合法內容 */ }
            return aOut;
        }

        public static bool IsArchived(string iDataRoot, string iRoom) => LoadSettings(iDataRoot, iRoom).Archived;

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

        /// <summary>封存／取消封存。只動旗標，⛔ 不碰任何訊息。</summary>
        public static bool TrySetArchived(string iDataRoot, string iRoom, bool iArchived, out string? oError)
        {
            oError = null;
            if (!ChannelExists(iDataRoot, iRoom)) { oError = $"沒有這個頻道：'{iRoom}'"; return false; }
            SCP_ChannelSettings aSet = LoadSettings(iDataRoot, iRoom);
            if (aSet.Archived == iArchived) { oError = iArchived ? $"'{iRoom}' 已經是封存的" : $"'{iRoom}' 本來就沒有封存"; return false; }
            aSet.Archived = iArchived;
            aSet.ArchivedAt = iArchived ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) : "";
            return WriteSettings(iDataRoot, iRoom, aSet, out oError);
        }

        static bool WriteSettings(string iDataRoot, string iRoom, SCP_ChannelSettings iSet, out string? oError)
        {
            var aRoot = SCP_JsonData.NewObject();
            aRoot.Set("schema_version", SchemaVersion);
            aRoot.Set("category", iSet.Category);
            aRoot.Set("archived", iSet.Archived);
            aRoot.Set("archived_at", iSet.ArchivedAt);
            string aPath = SettingsPath(iDataRoot, iRoom);
            if (!WriteJson(aPath, aRoot, out oError)) return false;
            SCP_ChannelSettings aBack = LoadSettings(iDataRoot, iRoom);
            if (aBack.Category != iSet.Category || aBack.Archived != iSet.Archived)
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
                var aInfo = new SCP_ChannelInfo
                {
                    Room = aRoom,
                    Name = SCP_TavernRooms.LoadRoomMeta(iDataRoot, aRoom)?.Name ?? "",
                    Settings = aSet,
                    CategoryMissing = aSet.Category.Length > 0 && !aCatNames.Contains(aSet.Category),
                };
                // ⚠ 最新 seq 取**最後一則訊息本身**，⛔ 不讀 `_seq.txt` —— 2026-09-28 實測 59 房只有 5 房有那個檔
                //   （其餘由索引／訊息檔決定），讀它的話 54 房全印 0，跟「沒有訊息」同形。
                try
                {
                    List<SCP_TavernMessage> aLast = SCP_TavernRead.Tail(iDataRoot, aRoom, 1);
                    if (aLast.Count > 0) { aInfo.LastSeq = aLast[aLast.Count - 1].Seq; aInfo.LastTs = aLast[aLast.Count - 1].Ts; }
                }
                catch (Exception) { /* 讀不到最後一則 ⇒ seq 0、時間留空（顯示端印「—」），不擋整張清單 */ }
                aOut.Add(aInfo);
            }
            return aOut;
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
