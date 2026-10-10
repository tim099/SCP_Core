// 區塊職責：個人作品的身分、可調尺寸與續作資料；幾何仍走 SCP_SculptEngine。
// 物理意義：作品 ID 全庫唯一，owner 不可更換；pending 付款尚未完成，不能雕刻或匯入。
// 數值影響：各軸上限由宿主給（預設 4096，結構上限 2^20）、預設64³、建立費10；縮小不得切掉現有voxel。宿主負責鎖與付款。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Reflect;

namespace SCP.Core.Sculpture
{
    /// <summary>作品書卡與可恢復的建立付款計畫。既有付款 ref 與金額不得重算。</summary>
    public sealed class SCP_SculptWork
    {
        public int schema;
        public string id = "", owner = "", title = "", created_at = "";
        public int size;
        public int size_x, size_y, size_z;
        [SCP_Ignore] public int SizeX => size_x == 0 ? size : size_x;
        [SCP_Ignore] public int SizeY => size_y == 0 ? size : size_y;
        [SCP_Ignore] public int SizeZ => size_z == 0 ? size : size_z;
        [SCP_Ignore] public string Dimensions => SizeX + "," + SizeY + "," + SizeZ;
        public string status = "", payment_ref = "", account = "", pay = "";
        public int freetime, voucher, tavern, token;
        public string commission = "", commission_ref = "";
        public string parent_work = "";
        public int reward;
        /// <summary>這件作品一格代表幾公尺（TASK-0480，剖面與疊圖照它換算）；0 ＝ 沒記 ⇒ <see cref="DefaultMetersPerVoxel"/>。</summary>
        public double meters_per_voxel;
        /// <summary>預設比例：1 公尺＝32 格（建築與家具的共用基準）。</summary>
        public const double DefaultMetersPerVoxel = 1.0 / 32;
        [SCP_Ignore] public double MetersPerVoxel => meters_per_voxel > 0 ? meters_per_voxel : DefaultMetersPerVoxel;
        [SCP_JsonExtensionData] public Dictionary<string, SCP_JsonData> extra = new Dictionary<string, SCP_JsonData>();
    }

    /// <summary>鎖內計算的匯入副本；Placed 保留每顆原色，不經 PNG 投影。</summary>
    public sealed class SCP_SculptWorkImport
    {
        public List<int[]> Placed = new List<int[]>();
        public int Occupied, OutOfBounds;
        public string WorkId = "", Owner = "", Revision = "";
        public int[] At = new int[3];
        public List<SCP_SculptCredit> Credits = new List<SCP_SculptCredit>();
    }

    /// <summary>唯一的作品路徑決定點。只接受安全的全庫 ID，不接受呼叫端給任意資料根。</summary>
    public sealed class SCP_SculptWorks
    {
        public const int Size = 64, CreationFee = 10;
        /// <summary>
        /// 結構上限：渲染端把外框內座標打包成 21 位元，留一位餘裕 ⇒ 每軸 2^20。
        /// ⚠ 這不是政策 —— 讀取、雕刻、渲染只認這一格，所以宿主把政策上限調小，既有的大作品照樣打得開。
        /// </summary>
        public const int MaxAxisHard = 1 << 20;
        /// <summary>宿主沒給政策上限時的預設（Tim 2026-10-10：256 不寫死、改成參數）。</summary>
        public const int DefaultMaxAxis = 4096;
        public static bool ValidSize(int iValue) => iValue >= 1 && iValue <= MaxAxisHard;
        /// <summary>政策上限只管**建立與調整尺寸**；<paramref name="iMaxAxis"/> 由宿主給（夾在 1..結構上限）。</summary>
        public static int[] ParseSize(string iText, int iMaxAxis = DefaultMaxAxis)
        {
            int aMax = Math.Max(1, Math.Min(MaxAxisHard, iMaxAxis));
            string aBad = "size 要是邊長或 X,Y,Z，各軸1–" + aMax;
            string[] parts = iText.Split(',');
            if (parts.Length != 1 && parts.Length != 3) throw new ArgumentException(aBad);
            var result = new int[3];
            for (int i = 0; i < 3; i++)
                if (!int.TryParse(parts[parts.Length == 1 ? 0 : i], out result[i]) || result[i] < 1 || result[i] > aMax)
                    throw new ArgumentException(aBad);
            return result;
        }
        /// <summary>
        /// 政策上限判準（TASK-0479）：只有「某一軸超過上限、**而且比現在大**」才算違規 —— 縮小或維持原尺寸永遠放行
        /// （否則把上限調小之後，帶著原尺寸存筆記的人會被擋）。<paramref name="iCurrent"/> ＝ null 表示新作品，每軸都要 ≤ 上限。
        /// 回傳 null ＝ 放行；否則是一句人話。
        /// </summary>
        public static string? PolicyViolation(int[] iSize, int[]? iCurrent, int iMaxAxis)
        {
            int aMax = Math.Max(1, Math.Min(MaxAxisHard, iMaxAxis));
            string[] aNames = { "X", "Y", "Z" };
            for (int i = 0; i < 3; i++)
            {
                bool aGrows = iCurrent == null || iSize[i] > iCurrent[i];
                if (aGrows && iSize[i] > aMax)
                    return "size 的 " + aNames[i] + " 軸 " + iSize[i] + " 超過作品尺寸上限 " + aMax + (iCurrent == null ? "" : "（現在 " + iCurrent[i] + "；縮小或維持原尺寸不受上限限制）");
            }
            return null;
        }
        public static void SetSize(SCP_SculptWork iCard, int[] iSize)
        {
            foreach (int value in iSize) if (!ValidSize(value)) throw new ArgumentException("作品各軸需為1–" + MaxAxisHard);
            iCard.size_x = iSize[0]; iCard.size_y = iSize[1]; iCard.size_z = iSize[2];
            iCard.size = Math.Max(iSize[0], Math.Max(iSize[1], iSize[2]));
        }
        public SCP_SculptEngine Engine(SCP_SculptWork iCard, string iDataRoot) =>
            new SCP_SculptEngine(SpacePaths(iCard.id), iDataRoot, iCard.SizeX, iCard.SizeY, iCard.SizeZ);
        public string Root { get; }
        public SCP_SculptWorks(SCP_DataRoot iData) { Root = Path.Combine(new SCP_SculptPaths(iData).Root, "works"); }
        public string RegistryLock => Path.Combine(Root, "_registry");
        public static string NormalizeId(string iId)
        {
            string id = iId.Trim().ToLowerInvariant();
            if (id.Length == 0 || id.Length > 64 || !IsAlnum(id[0])) throw new ArgumentException("作品 ID 要是 1–64 字元的英數、底線或連字號");
            foreach (char c in id) if (!IsAlnum(c) && c != '-' && c != '_') throw new ArgumentException("作品 ID 含不合法字元");
            if (id == "con" || id == "prn" || id == "aux" || id == "nul" ||
                (id.Length == 4 && (id.StartsWith("com", StringComparison.Ordinal) || id.StartsWith("lpt", StringComparison.Ordinal)) && id[3] >= '1' && id[3] <= '9'))
                throw new ArgumentException("作品 ID 是系統保留名稱");
            return id;
        }
        static bool IsAlnum(char c) => c >= 'a' && c <= 'z' || c >= '0' && c <= '9';
        public string Folder(string iId)
        {
            RejectLink(Root);
            string dir = Path.Combine(Root, NormalizeId(iId));
            RejectLink(dir);
            return dir;
        }
        static void RejectLink(string iPath)
        {
            if (Directory.Exists(iPath) && (File.GetAttributes(iPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("作品目錄不可是符號連結：" + iPath);
        }
        public SCP_SculptPaths SpacePaths(string iId) => new SCP_SculptPaths(Folder(iId));
        public string EngineLock(string iId) => Path.Combine(Folder(iId), "_engine");
        public bool Exists(string iId) => Directory.Exists(Folder(iId));
        public SCP_SculptWork Load(string iId, bool iRequireReady = true)
        {
            string id = NormalizeId(iId), path = Path.Combine(Folder(id), "work.json");
            if (!File.Exists(path)) throw new InvalidOperationException("作品不存在或書卡遺失：" + id);
            var options = new SCP_JsonMapOptions();
            var card = new SCP_SculptWork();
            SCP_JsonMapper.Populate(card, SCP_JsonParser.Parse(File.ReadAllText(path, Encoding.UTF8)), options);
            if (options.Diagnostics.Count > 0 || card == null || card.schema != 1 || card.id != id || !ValidSize(card.size) ||
                !ValidSize(card.SizeX) || !ValidSize(card.SizeY) || !ValidSize(card.SizeZ) ||
                ((card.size_x != 0 || card.size_y != 0 || card.size_z != 0) &&
                    (card.size_x == 0 || card.size_y == 0 || card.size_z == 0 || card.size != Math.Max(card.SizeX, Math.Max(card.SizeY, card.SizeZ)))) ||
                card.owner.Length == 0 || card.title.Length == 0 || card.payment_ref.Length == 0 ||
                card.freetime < 0 || card.voucher < 0 || card.tavern < 0 || card.token < 0 ||
                (card.commission.Length == 0
                    ? card.reward != 0 || card.commission_ref.Length != 0 || (long)card.freetime + card.voucher + card.tavern + card.token != (card.parent_work.Length > 0 ? 0 : CreationFee)
                    : card.reward != CreationFee || card.commission_ref.Length == 0 || card.account.Length == 0 || (long)card.freetime + card.voucher + card.tavern + card.token != 0) ||
                (card.parent_work.Length > 0 && (card.parent_work == id || card.commission.Length > 0 || NormalizeId(card.parent_work) != card.parent_work)) ||
                (card.status != "pending" && card.status != "ready"))
                throw new InvalidOperationException("作品書卡不合法：" + id + " " + string.Join("; ", options.Diagnostics));
            if (iRequireReady && card.status != "ready") throw new InvalidOperationException("作品建立付款未完成；作者以 work sub=create 重試同一 ID 對帳：" + id);
            return card;
        }
        public List<SCP_SculptWork> List(string iOwner = "")
        {
            var cards = new List<SCP_SculptWork>();
            if (!Directory.Exists(Root)) return cards;
            foreach (string dir in Directory.GetDirectories(Root))
            {
                var card = Load(Path.GetFileName(dir), false);
                if (iOwner.Length == 0 || card.owner == iOwner) cards.Add(card);
            }
            cards.Sort((a, b) => string.CompareOrdinal(a.id, b.id));
            return cards;
        }
        public void Save(SCP_SculptWork iCard)
        {
            string path = Path.Combine(Folder(iCard.id), "work.json");
            string text = SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(iCard), SCP_JsonStyle.UclLegacy);
            SCP_CmdPayload.WriteAtomic(path, text);
            if (File.ReadAllText(path, Encoding.UTF8) != text) throw new IOException("作品書卡回讀不符");
        }
        public string ReadText(string iId, bool iTodo) => File.Exists(TextPath(iId, iTodo)) ? File.ReadAllText(TextPath(iId, iTodo), Encoding.UTF8) : "";
        public void WriteText(string iId, bool iTodo, string iText) => SCP_CmdPayload.WriteAtomic(TextPath(iId, iTodo), iText);
        string TextPath(string iId, bool iTodo) => Path.Combine(Folder(iId), iTodo ? "todo.md" : "notes.md");
        /// <summary>事件水位與完整 voxel 內容共同定義版本；匯入預覽後的任何一刀都會使版本失效。</summary>
        public static string Revision(SCP_SculptSpace iSpace, string iDimensions = "")
        {
            var text = new StringBuilder(iDimensions).Append('\n').Append(iSpace.LastEventFile).Append('\n');
            foreach (var v in iSpace.Voxels.Entries())
            {
                if (v.X < 0 || v.X >= MaxAxisHard || v.Y < 0 || v.Y >= MaxAxisHard || v.Z < 0 || v.Z >= MaxAxisHard || v.Color < 1 || v.Color > 255)
                    throw new InvalidOperationException("作品 voxel 超出結構上限（每軸 " + MaxAxisHard + "）或調色盤範圍");
                text.Append(SCP_SculptVoxelMap.KeyText(v.X, v.Y, v.Z)).Append(':').Append(v.Color).Append('\n');
            }
            using (var hash = SHA256.Create()) return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "").ToLowerInvariant();
        }
    }
}
