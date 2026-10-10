// 區塊職責：雕刻的**狀態層** —— 路徑、稀疏 voxel 表、事件落盤、快取 `sculpt_cache.json`、事件重播（load_space_state）。
//          語意沿用 python 時代引擎的 SparseVoxelSpace／load_all_events／load_space_state／apply_event_to_space／save_cache／record_event
//          （TASK-0377 搬成 C#；既有資料是它寫的）。
// 物理意義：事實源是 `<資料根>/Sculpture/events/<YYYY-MM-DD>/<HHMMSS_mmm>_<uuid6>.json`（append-only）；
//          快取只是「重播到某個水位的結果」＋水位（last_event_file），可隨時丟掉重建。
//          ⭐ voxel 表的**順序**是契約的一部分：python dict 的插入序（刪掉再加回 ⇒ 移到最後）決定了
//            快取檔的 key 順序、obj/vox 匯出的面序與 byte 內容 —— 所以這裡不用 HashSet，用「有序表」。
// 數值影響：快取存在且水位對得上 ⇒ 只重播水位之後的事件（python 每次都把**全部**事件檔解析一遍，
//          這裡只解析水位那一檔與它之後的 —— 結果相同，因為水位之前的檔只在重建時才用得到）。
//          水位對不上（檔不見／解析失敗）⇒ 清空重建；水位是空字串 ⇒ 把**全部**事件疊在快取的 voxel 上（python 同）。
// 失敗處置：事件檔讀不出來（BOM／非 UTF-8／非 JSON／不是物件）⇒ 跳過（python 的 `except Exception: pass`）；
//          事件的 op 不認得 ⇒ **丟例外**、不存快取（python 同 —— 新增 op 卻忘了擴充重播，是最會隱身的那種壞法）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Sculpture
{
    /// <summary>雕刻的所有子路徑。⚠ 一律是**平台原生分隔符的絕對路徑**（＝ python <c>str(Path)</c> 印出來的樣子）。</summary>
    public sealed class SCP_SculptPaths
    {
        public const string DirName = "Sculpture";

        /// <summary><c>&lt;資料根&gt;/Sculpture</c>。根由宿主給（SCP_DataRoot），⛔ 不 walk 任何 __file__。</summary>
        public string Root { get; }

        public SCP_SculptPaths(SCP_DataRoot iDataRoot)
        {
            Root = SCP_SculptPy.FullPathStr(Path.Combine(iDataRoot.Value, DirName));
        }

        /// <summary>直接給雕刻根（測試隔離用；正路走 <see cref="SCP_DataRoot"/>）。</summary>
        public SCP_SculptPaths(string iSculptRoot)
        {
            Root = SCP_SculptPy.FullPathStr(iSculptRoot);
        }

        public string Events => Path.Combine(Root, "events");
        public string CacheFile => Path.Combine(Root, "sculpt_cache.json");
        /// <summary>歷史索引（<see cref="SCP_SculptHistoryIndex"/>）：本機快取、不入 git。</summary>
        public string HistoryCacheFile => Path.Combine(Root, "sculpt_history_cache.json");
        public string Exhibits => Path.Combine(Root, "exhibits");
        public string Exports => Path.Combine(Root, "exports");
        public string LastSlice => Path.Combine(Root, "_last_slice.png");
        public string StampSrc => Path.Combine(Root, "_stamp_src.png");
        public string ExhibitJson(string iId) => Path.Combine(Exhibits, iId + ".json");
        public string ExhibitPng(string iId) => Path.Combine(Exhibits, iId + ".png");

        /// <summary>python <c>get_sculpt_dir()</c> 的副作用：每次取根都確保根與 events/ 存在。</summary>
        public void EnsureBase()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Events);
        }
    }

    /// <summary>
    /// 稀疏 voxel 表，**行為等同 python dict**：更新既有 key 位置不動、刪掉再加回移到最後、列舉照插入序。
    /// 值 0 ＝ 空（<see cref="Set"/> 寫 0 ＝ 刪除）；但快取檔裡若本來就存著 0，<see cref="PutRaw"/> 照收（python 同）。
    /// </summary>
    public sealed class SCP_SculptVoxelMap
    {
        readonly struct Key : IEquatable<Key>
        {
            public readonly int X, Y, Z;
            public Key(int iX, int iY, int iZ) { X = iX; Y = iY; Z = iZ; }
            public bool Equals(Key o) => X == o.X && Y == o.Y && Z == o.Z;
            public override bool Equals(object? o) => o is Key k && Equals(k);
            public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791));
        }

        readonly Dictionary<Key, int> m_Index = new Dictionary<Key, int>();
        Key[] m_Keys = new Key[256];
        int[] m_Colors = new int[256];
        bool[] m_Alive = new bool[256];
        int m_Used;
        int m_Live;

        /// <summary>key 數（＝ python <c>len(space.voxels)</c>，含快取裡值為 0 的怪 key）。</summary>
        public int Count => m_Live;

        public int Get(int iX, int iY, int iZ)
            => m_Index.TryGetValue(new Key(iX, iY, iZ), out int i) ? m_Colors[i] : 0;

        public bool ContainsKey(int iX, int iY, int iZ) => m_Index.ContainsKey(new Key(iX, iY, iZ));

        /// <summary>python <c>set_voxel</c>：0 ⇒ 刪 key；非 0 ⇒ 既有就地換值、新的接在最後。</summary>
        public void Set(int iX, int iY, int iZ, int iColor)
        {
            if (iColor == 0) { Remove(iX, iY, iZ); return; }
            PutRaw(iX, iY, iZ, iColor);
        }

        /// <summary>python <c>voxels[key] = v</c>（不看值是不是 0）。</summary>
        public void PutRaw(int iX, int iY, int iZ, int iColor)
        {
            var k = new Key(iX, iY, iZ);
            if (m_Index.TryGetValue(k, out int i)) { m_Colors[i] = iColor; return; }
            if (m_Used == m_Keys.Length) Grow();
            m_Keys[m_Used] = k; m_Colors[m_Used] = iColor; m_Alive[m_Used] = true;
            m_Index[k] = m_Used;
            m_Used++; m_Live++;
        }

        public void Remove(int iX, int iY, int iZ)
        {
            var k = new Key(iX, iY, iZ);
            if (!m_Index.TryGetValue(k, out int i)) return;
            m_Index.Remove(k);
            m_Alive[i] = false;
            m_Live--;
            if (m_Used > 4096 && m_Live * 2 < m_Used) Compact();
        }

        void Grow()
        {
            if (m_Live * 2 < m_Used) { Compact(); if (m_Used < m_Keys.Length) return; }
            int aN = m_Keys.Length * 2;
            Array.Resize(ref m_Keys, aN);
            Array.Resize(ref m_Colors, aN);
            Array.Resize(ref m_Alive, aN);
        }

        void Compact()
        {
            int w = 0;
            for (int r = 0; r < m_Used; r++)
            {
                if (!m_Alive[r]) continue;
                m_Keys[w] = m_Keys[r]; m_Colors[w] = m_Colors[r]; m_Alive[w] = true;
                m_Index[m_Keys[w]] = w;
                w++;
            }
            for (int i = w; i < m_Used; i++) m_Alive[i] = false;
            m_Used = w;
        }

        /// <summary>照插入序列舉 (x, y, z, color)。⚠ 列舉期間不可改表。</summary>
        public IEnumerable<(int X, int Y, int Z, int Color)> Entries()
        {
            for (int i = 0; i < m_Used; i++)
                if (m_Alive[i]) yield return (m_Keys[i].X, m_Keys[i].Y, m_Keys[i].Z, m_Colors[i]);
        }

        /// <summary>快照成清單（之後改表不受影響）。</summary>
        public List<(int X, int Y, int Z, int Color)> ToList()
        {
            var a = new List<(int, int, int, int)>(m_Live);
            foreach (var e in Entries()) a.Add(e);
            return a;
        }

        public static string KeyText(int iX, int iY, int iZ)
            => iX.ToString(CultureInfo.InvariantCulture) + "," + iY.ToString(CultureInfo.InvariantCulture)
               + "," + iZ.ToString(CultureInfo.InvariantCulture);

        /// <summary>解析 python 寫的 key（<c>f"{x},{y},{z}"</c> 的正規形）；不是正規形 ⇒ false。</summary>
        public static bool TryParseKey(string iKey, out int oX, out int oY, out int oZ)
        {
            oX = oY = oZ = 0;
            string[] p = iKey.Split(',');
            if (p.Length != 3) return false;
            if (!int.TryParse(p[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out oX)
                || !int.TryParse(p[1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out oY)
                || !int.TryParse(p[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out oZ)) return false;
            return KeyText(oX, oY, oZ) == iKey;   // 「01,2,3」在 python 是另一個 key —— 不正規就不收
        }
    }

    /// <summary>python <c>SparseVoxelSpace</c>：voxel 表＋重播水位。</summary>
    public sealed class SCP_SculptSpace
    {
        public SCP_SculptVoxelMap Voxels = new SCP_SculptVoxelMap();

        /// <summary>最後一個已套用的事件（相對 events/ 的路徑，平台分隔符；空字串＝沒有水位）。</summary>
        public string LastEventFile = "";
    }

    /// <summary>重播時遇到不認得的 op／壞掉的欄位 —— 不吞（見檔頭）。</summary>
    public sealed class SCP_SculptReplayException : Exception
    {
        public SCP_SculptReplayException(string iMessage) : base(iMessage) { }
    }

    public static class SCP_SculptStore
    {
        /// <summary>貼圖類 op —— 事件 shape 相同（placed_colored 逐 voxel 帶色），重播共用一個分支。stampvox：TASK-0487。</summary>
        public static readonly string[] StampOps = { "stamp2d", "stampimg", "importwork", "stampvox" };

        static bool IsStampOp(string? iOp) => iOp == "stamp2d" || iOp == "stampimg" || iOp == "importwork" || iOp == "stampvox";

        static bool IsWindows => Path.DirectorySeparatorChar == '\\';

        // ───────────────────────── 事件列舉 ─────────────────────────
        /// <summary>一個事件檔：相對 events/ 的路徑（平台分隔符，＝ python 的 <c>_file</c>）＋完整路徑。</summary>
        public readonly struct EventFile
        {
            public EventFile(string iRel, string iFull) { Rel = iRel; Full = iFull; }
            public string Rel { get; }
            public string Full { get; }
        }

        /// <summary>
        /// python <c>sorted(events_dir.glob("**/*.json"))</c>：遞迴、含 events/ 本層、含隱藏檔；
        /// 排序＝**逐段**比、每段先轉小寫再比碼位（Windows 的 PureWindowsPath 規則）。
        /// <para>⚠ 不是整串字串排序：<c>a-b/x</c> 與 <c>a/x</c> 逐段比是 "a-b" vs "a"（a 在前），整串比是 '-' vs '/'。</para>
        /// </summary>
        public static List<EventFile> ListEvents(SCP_SculptPaths iPaths)
        {
            var aOut = new List<EventFile>();
            string aRoot = iPaths.Events;
            if (!Directory.Exists(aRoot)) return aOut;
            StringComparison aCmp = IsWindows ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (string aFull in Directory.EnumerateFiles(aRoot, "*", SearchOption.AllDirectories))
            {
                if (!Path.GetFileName(aFull).EndsWith(".json", aCmp)) continue;
                string aNative = SCP_SculptPy.PathStr(aFull);
                string aRel = aNative.Substring(aRoot.Length).TrimStart(Path.DirectorySeparatorChar);
                aOut.Add(new EventFile(aRel, aNative));
            }
            aOut.Sort((a, b) => ComparePathParts(a.Rel, b.Rel));
            return aOut;
        }

        /// <summary>pathlib 的路徑比較（Windows：各段 lower() 後逐段比）。</summary>
        public static int ComparePathParts(string iA, string iB)
        {
            char aSep = Path.DirectorySeparatorChar;
            string[] a = (IsWindows ? iA.ToLowerInvariant() : iA).Split(aSep);
            string[] b = (IsWindows ? iB.ToLowerInvariant() : iB).Split(aSep);
            int n = Math.Min(a.Length, b.Length);
            for (int i = 0; i < n; i++)
            {
                int c = string.CompareOrdinal(a[i], b[i]);
                if (c != 0) return c;
            }
            return a.Length.CompareTo(b.Length);
        }

        /// <summary>
        /// 讀一個事件檔；讀不出來 ⇒ null（python 的 <c>except Exception: pass</c>）。
        /// <para>python 會拒的：UTF-8 BOM（json.loads 明確拒絕）、非法 UTF-8、非 JSON、根不是物件（<c>data["_file"]=…</c> 會炸）。</para>
        /// <para>⚠ 小差異：SCP parser 容忍註解與尾逗號（python 拒）、不收 NaN／Infinity（python 收）—— 機器寫的事件不會長那樣。</para>
        /// </summary>
        public static SCP_JsonData? ReadEvent(string iFullPath)
        {
            try
            {
                byte[] aBytes = File.ReadAllBytes(iFullPath);
                if (aBytes.Length >= 3 && aBytes[0] == 0xEF && aBytes[1] == 0xBB && aBytes[2] == 0xBF) return null;
                string aText = new UTF8Encoding(false, true).GetString(aBytes);
                SCP_JsonData aData = SCP_JsonParser.Parse(aText, false);
                return aData.IsObject ? aData : null;
            }
            catch (Exception) { return null; }
        }

        // ───────────────────────── load_space_state ─────────────────────────
        /// <summary>
        /// python <c>load_space_state()</c>：讀快取 → 水位之後的事件重播 → 存快取。
        /// <para>⚠ 會**寫**快取（python 每次都寫，即使沒有新事件）。呼叫端要握雕刻鎖。</para>
        /// </summary>
        /// <exception cref="SCP_SculptReplayException">事件的 op 不認得或欄位壞掉（不存快取）。</exception>
        public static SCP_SculptSpace Load(SCP_SculptPaths iPaths)
        {
            iPaths.EnsureBase();
            SCP_SculptSpace aSpace = TryReadCache(iPaths.CacheFile) ?? new SCP_SculptSpace();

            List<EventFile> aAll = ListEvents(iPaths);
            int aStart = 0;
            if (aSpace.LastEventFile.Length > 0)
            {
                int aFound = -1;
                string aWant = NormSep(aSpace.LastEventFile);
                for (int i = 0; i < aAll.Count; i++)
                {
                    if (aAll[i].Rel != aWant) continue;
                    // python 只拿「解析成功的事件」比水位 ⇒ 水位那一檔壞了＝找不到
                    // 歷史索引記得它讀得出來（且檔沒改過）⇒ 不必為了確認而整份解析（TASK-0473：水位檔可能 35 MB）
                    bool? aKnown = SCP_SculptHistoryIndex.KnownReadable(iPaths, aAll[i]);
                    if (aKnown ?? ReadEvent(aAll[i].Full) != null) aFound = i;
                    break;
                }
                if (aFound < 0) { aSpace = new SCP_SculptSpace(); aStart = 0; }   // 快取壞了／過期 ⇒ 重建
                else aStart = aFound + 1;
            }

            for (int i = aStart; i < aAll.Count; i++)
            {
                SCP_JsonData? aEv = ReadEvent(aAll[i].Full);
                if (aEv == null) continue;
                ApplyEvent(aSpace, aEv, aAll[i].Rel);
                aSpace.LastEventFile = aAll[i].Rel;
            }

            SaveCache(iPaths, aSpace);
            return aSpace;
        }

        /// <summary>水位比對時接受兩種分隔符（python 在 Windows 寫反斜線；誰寫了正斜線也認得）。</summary>
        static string NormSep(string iRel)
            => IsWindows ? iRel.Replace('/', '\\') : iRel;

        /// <summary>
        /// 讀快取；壞了 ⇒ null（＝ python 的「讀失敗就從空的開始」）。
        /// <para>⚠ 比 python 嚴一格：值不是整數、key 不是 <c>x,y,z</c> 正規形 ⇒ 也當壞掉（python 會照收那種怪值）。
        /// 當壞掉的後果是**全重播**，結果與正常快取相同 —— 只有手改過的快取會分岔。</para>
        /// </summary>
        static SCP_SculptSpace? TryReadCache(string iCacheFile)
        {
            if (!File.Exists(iCacheFile)) return null;
            try
            {
                byte[] aBytes = File.ReadAllBytes(iCacheFile);
                if (aBytes.Length >= 3 && aBytes[0] == 0xEF && aBytes[1] == 0xBB && aBytes[2] == 0xBF) return null;
                SCP_JsonData aData = SCP_JsonParser.Parse(new UTF8Encoding(false, true).GetString(aBytes), false);
                if (!aData.IsObject) return null;
                var aSpace = new SCP_SculptSpace();
                SCP_JsonData aLast = aData["last_event_file"];
                if (aLast.IsString) aSpace.LastEventFile = aLast.AsString();
                else if (aLast.Exists && !aLast.IsNull) return null;
                SCP_JsonData aVox = aData["voxels"];
                if (aVox.Exists)
                {
                    if (!aVox.IsObject) return null;
                    foreach (string k in aVox.Keys)
                    {
                        SCP_JsonData v = aVox[k];
                        if (v.Type != SCP_JsonType.Number) return null;
                        if (!SCP_SculptVoxelMap.TryParseKey(k, out int x, out int y, out int z)) return null;
                        if (!int.TryParse(v.AsString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int c))
                            return null;
                        aSpace.Voxels.PutRaw(x, y, z, c);
                    }
                }
                return aSpace;
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// python <c>save_cache()</c>：<c>json.dump(…, ensure_ascii=False)</c>（無縮排、<c>", "</c>／<c>": "</c> 分隔），
        /// 寫失敗靜默（python 同 —— 快取是衍生物）。
        /// </summary>
        public static void SaveCache(SCP_SculptPaths iPaths, SCP_SculptSpace iSpace)
        {
            try { File.WriteAllText(iPaths.CacheFile, CacheText(iSpace), new UTF8Encoding(false)); }
            catch (Exception) { }
        }

        /// <summary>快取檔的全文（與 python 逐字相同）。</summary>
        public static string CacheText(SCP_SculptSpace iSpace)
        {
            var aSb = new StringBuilder(32 + iSpace.Voxels.Count * 16);
            aSb.Append("{\"last_event_file\": ");
            SCP_SculptPy.WriteString(aSb, iSpace.LastEventFile);
            aSb.Append(", \"voxels\": {");
            bool aFirst = true;
            foreach (var e in iSpace.Voxels.Entries())
            {
                if (!aFirst) aSb.Append(", ");
                aFirst = false;
                aSb.Append('"').Append(SCP_SculptVoxelMap.KeyText(e.X, e.Y, e.Z)).Append("\": ")
                   .Append(e.Color.ToString(CultureInfo.InvariantCulture));
            }
            aSb.Append("}}");
            return aSb.ToString();
        }

        // ───────────────────────── record_event ─────────────────────────
        /// <summary>
        /// python <c>record_event()</c>：落 <c>events/&lt;YYYY-MM-DD&gt;/&lt;HHMMSS_mmm&gt;_&lt;uuid6&gt;.json</c>（indent=2、平台換行）。
        /// <para>🩸 python 取了**兩次** <c>datetime.now()</c>（一次給資料夾、一次給檔名）⇒ 跨午夜那一刀會落進前一天的資料夾、
        /// 檔名卻是隔天的時間，排序就錯位。這裡只取**一次** <paramref name="iNow"/>，兩者同源。</para>
        /// </summary>
        /// <returns>完整路徑（平台分隔符）；<paramref name="oRel"/> ＝ 相對 events/ 的路徑（＝新水位）。</returns>
        public static string RecordEvent(SCP_SculptPaths iPaths, SCP_SculptPyObj iEvent, DateTime iNow, out string oRel)
        {
            // 同毫秒或時鐘倒退仍保持追加順序，否則Undo與刪快取後的重播會交換兩刀。
            var previous = ListEvents(iPaths);
            if (previous.Count > 0)
            {
                string rel = previous[previous.Count - 1].Rel.Replace('\\', '/');
                string[] parts = rel.Split('/');
                string name = parts[parts.Length - 1];
                if (parts.Length == 2 && name.Length >= 10 && DateTime.TryParseExact(parts[0] + " " + name.Substring(0, 10),
                    "yyyy-MM-dd HHmmss_fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime last) && iNow.Ticks / TimeSpan.TicksPerMillisecond <= last.Ticks / TimeSpan.TicksPerMillisecond)
                    iNow = last.AddMilliseconds(1);
            }
            string aDay = iNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string aDir = Path.Combine(iPaths.Events, aDay);
            Directory.CreateDirectory(aDir);
            int aMs = (int)((iNow.Ticks % TimeSpan.TicksPerSecond) / 10 / 1000);
            string aName = iNow.ToString("HHmmss", CultureInfo.InvariantCulture) + "_"
                           + aMs.ToString("000", CultureInfo.InvariantCulture) + "_"
                           + Guid.NewGuid().ToString("D").Substring(0, 6) + ".json";
            string aFull = Path.Combine(aDir, aName);
            SCP_SculptPy.WriteTextFile(aFull, SCP_SculptPy.Dumps(iEvent, true));
            oRel = aDay + Path.DirectorySeparatorChar + aName;
            return aFull;
        }

        // ───────────────────────── apply_event_to_space ─────────────────────────
        /// <summary>python <c>apply_event_to_space()</c>（重播用；欄位讀法與 python 的 <c>ev.get(k, 預設)</c> 同）。</summary>
        public static void ApplyEvent(SCP_SculptSpace iSpace, SCP_JsonData iEv, string iFile)
        {
            SCP_JsonData aOpNode = iEv["op"];
            string? aOp = aOpNode.IsString ? aOpNode.AsString() : null;
            if (aOp == "box" || aOp == "fill")
            {
                int aColor = IntField(iEv, "color", 19, iFile);
                SCP_JsonData aPlaced = iEv["placed_voxels"];
                ApplyBox(iSpace, aColor, ReadTriples(aPlaced, iFile, "placed_voxels"),
                         IntField(iEv, "x1", 0, iFile), IntField(iEv, "x2", 0, iFile),
                         IntField(iEv, "y1", 0, iFile), IntField(iEv, "y2", 0, iFile),
                         IntField(iEv, "z1", 0, iFile), IntField(iEv, "z2", 0, iFile));
            }
            else if (aOp == "carve")
            {
                ApplyCarve(iSpace, ReadTriples(iEv["carved_voxels"], iFile, "carved_voxels"),
                           IntField(iEv, "x1", 0, iFile), IntField(iEv, "x2", 0, iFile),
                           IntField(iEv, "y1", 0, iFile), IntField(iEv, "y2", 0, iFile),
                           IntField(iEv, "z1", 0, iFile), IntField(iEv, "z2", 0, iFile));
            }
            else if (aOp == "point")
            {
                iSpace.Voxels.Set(IntField(iEv, "x", 0, iFile), IntField(iEv, "y", 0, iFile),
                                  IntField(iEv, "z", 0, iFile), IntField(iEv, "color", 19, iFile));
            }
            else if (aOp == "workedit")
            {
                var options = new SCP_JsonMapOptions();
                var edit = new SCP_SculptEdit();
                SCP_JsonMapper.Populate(edit, iEv["edit"], options);
                if (options.Diagnostics.Count > 0) throw Bad(iFile, string.Join("; ", options.Diagnostics));
                foreach (var v in edit.after)
                {
                    // 座標只認結構上限（TASK-0479：作品每軸可超過 256；寫入端 CommitEdit 已照作品尺寸擋過）；顏色仍是 0..255
                    if (v[0] < 0 || v[0] >= SCP_SculptWorks.MaxAxisHard || v[1] < 0 || v[1] >= SCP_SculptWorks.MaxAxisHard
                        || v[2] < 0 || v[2] >= SCP_SculptWorks.MaxAxisHard || v[3] < 0 || v[3] > 255)
                        throw Bad(iFile, "workedit.after格式或座標不合法");
                    iSpace.Voxels.Set(v[0], v[1], v[2], v[3]);
                }
            }
            else if (IsStampOp(aOp))
            {
                SCP_JsonData aList = iEv["placed_colored"];
                if (aList.Exists && !aList.IsArray) throw Bad(iFile, "placed_colored 不是陣列");
                foreach (SCP_JsonData v in aList)
                    iSpace.Voxels.Set(Elem(v, 0, iFile), Elem(v, 1, iFile), Elem(v, 2, iFile), Elem(v, 3, iFile));
            }
            else
            {
                string aShown = aOpNode.IsMissing || aOpNode.IsNull ? "None" : SCP_SculptPy.Str(aOpNode);
                throw new SCP_SculptReplayException(
                    "apply_event_to_space 不認得 op='" + aShown + "'（事件 " + iFile + "）—— "
                    + "新增 op 必須同時擴充本函式，否則重播時該事件會靜默消失。已知：box/fill/carve/point/"
                    + string.Join("/", StampOps));
            }
        }

        /// <summary>
        /// box／fill 的語意核心（重播與落子**共用**）：清單非空 ⇒ 逐顆寫色；
        /// 清單是空的 ⇒ 退回舊式 AABB（min..max）只填空格 —— ⚠ python 的現況是「0 顆落地的 box 事件也走這條」，照搬。
        /// </summary>
        public static void ApplyBox(SCP_SculptSpace iSpace, int iColor, List<int[]>? iPlaced,
                                    int iX1, int iX2, int iY1, int iY2, int iZ1, int iZ2)
        {
            if (iPlaced != null && iPlaced.Count > 0)
            {
                foreach (int[] v in iPlaced) iSpace.Voxels.Set(v[0], v[1], v[2], iColor);
                return;
            }
            for (long x = Math.Min(iX1, iX2); x <= Math.Max(iX1, iX2); x++)
                for (long y = Math.Min(iY1, iY2); y <= Math.Max(iY1, iY2); y++)
                    for (long z = Math.Min(iZ1, iZ2); z <= Math.Max(iZ1, iZ2); z++)
                        if (iSpace.Voxels.Get((int)x, (int)y, (int)z) == 0)
                            iSpace.Voxels.Set((int)x, (int)y, (int)z, iColor);
        }

        /// <summary>carve 的語意核心：清單非空 ⇒ 逐顆刪；空 ⇒ 舊式 AABB 全刪（等價做法：刪掉表裡落在框內的 key）。</summary>
        public static void ApplyCarve(SCP_SculptSpace iSpace, List<int[]>? iCarved,
                                      int iX1, int iX2, int iY1, int iY2, int iZ1, int iZ2)
        {
            if (iCarved != null && iCarved.Count > 0)
            {
                foreach (int[] v in iCarved) iSpace.Voxels.Set(v[0], v[1], v[2], 0);
                return;
            }
            int x1 = Math.Min(iX1, iX2), x2 = Math.Max(iX1, iX2);
            int y1 = Math.Min(iY1, iY2), y2 = Math.Max(iY1, iY2);
            int z1 = Math.Min(iZ1, iZ2), z2 = Math.Max(iZ1, iZ2);
            // set_voxel(…, 0) 對不存在的 key 是 no-op ⇒ 只需處理表裡已有的 key（框可以大到 python 自己都跑不完）
            var aDoomed = new List<(int, int, int)>();
            foreach (var e in iSpace.Voxels.Entries())
                if (e.X >= x1 && e.X <= x2 && e.Y >= y1 && e.Y <= y2 && e.Z >= z1 && e.Z <= z2) aDoomed.Add((e.X, e.Y, e.Z));
            foreach (var d in aDoomed) iSpace.Voxels.Remove(d.Item1, d.Item2, d.Item3);
        }

        static List<int[]>? ReadTriples(SCP_JsonData iNode, string iFile, string iField)
        {
            if (!iNode.Exists || iNode.IsNull) return null;
            if (!iNode.IsArray)
            {
                if (!SCP_SculptPy.Truthy(iNode)) return null;
                throw Bad(iFile, iField + " 不是陣列");
            }
            var aOut = new List<int[]>(iNode.Count);
            foreach (SCP_JsonData v in iNode) aOut.Add(new[] { Elem(v, 0, iFile), Elem(v, 1, iFile), Elem(v, 2, iFile) });
            return aOut;
        }

        static int IntField(SCP_JsonData iEv, string iKey, int iDefault, string iFile)
        {
            SCP_JsonData a = iEv[iKey];
            if (!a.Exists) return iDefault;
            return AsInt(a, iFile, iKey);
        }

        static int Elem(SCP_JsonData iV, int iIdx, string iFile)
        {
            SCP_JsonData a = iV[iIdx];
            if (!a.Exists) throw Bad(iFile, "voxel 元素不足（" + iV.Path + "）");
            return AsInt(a, iFile, iV.Path);
        }

        static int AsInt(SCP_JsonData a, string iFile, string iWhat)
        {
            // ⚠ 只收整數：python 遇到 19.0 會把 float 當顏色存進去、key 變成 "1.0,2,3" —— 那是壞資料，這裡大聲擋
            if (a.Type == SCP_JsonType.Number
                && int.TryParse(a.AsString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int v))
                return v;
            throw Bad(iFile, iWhat + " 不是整數：" + SCP_SculptPy.Str(a));
        }

        static SCP_SculptReplayException Bad(string iFile, string iWhy)
            => new SCP_SculptReplayException("事件 " + iFile + " 欄位壞掉：" + iWhy);
    }
}
