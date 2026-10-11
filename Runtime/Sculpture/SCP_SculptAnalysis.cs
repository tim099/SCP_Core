// 區塊職責：通用的檢查搬進引擎（TASK-0492）—— 兩個時間點的差異（diff）、stats 的結構讀數（元件／封閉空腔／鏡像差）、
//          照清單刻（carvevox，stampvox 的反向）。純清單匯出在 Export 的 format=list。
// 物理意義：這幾樣原本是每件作品在 design/tools/ 各寫一份的 python（ledger／struct_check／check_ports…）；
//          搬進來的是跟作品無關的那一半 —— 哪一格算船殼、哪裡該白，仍是作品自己的設計。
//          ⭐ 定義照 python 那幾支（scipy.ndimage 的預設）：元件與空腔都是 **6 連通**（面相鄰）；
//            空腔 ＝ 外框外擴一格之後，從角落灌不進去的空格。
// 數值影響：diff 從頭重播到「後面那一刻」一次（不讀不寫主快取），前面那一刻的值由 BeforeChange 第一次碰到時記下。
//          空腔要一張外框大小的格子圖（兩張 bitset）⇒ 外框超過 <see cref="MaxStructureCells"/> 格就不算、明說沒算（⛔ 不印 0）。
// 失敗處置：參數不合 ⇒ exit 2；carvevox 的閘門與 stampvox 同型（expect 對不上 4、空 3、越界 5），清單的顏色對不上現況 ⇒ 4、零事件。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    public sealed partial class SCP_SculptEngine
    {
        /// <summary>空腔要攤開成格子圖；外框（含外擴一格）超過這個格數就不算（兩張 bitset 各 32 MB）。</summary>
        public const long MaxStructureCells = 256L * 1024 * 1024;
        /// <summary>結構讀數列幾塊出來（最小的元件、最大的空腔、鏡像差的樣本）。</summary>
        public const int StructureSamples = 5;

        static readonly int[][] s_Nb6 =
        {
            new[] { 1, 0, 0 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 }, new[] { 0, 0, -1 },
        };

        /// <summary>座標 → 雜湊鍵（各軸 21 bits；只收 0..2^21−1，呼叫端先擋負數）。</summary>
        static long PackKey(int x, int y, int z) => ((long)x << 42) | ((long)y << 21) | (long)z;
        static int KeyX(long k) => (int)(k >> 42);
        static int KeyY(long k) => (int)((k >> 21) & 0x1FFFFF);
        static int KeyZ(long k) => (int)(k & 0x1FFFFF);

        static int CompareXyz(int[] a, int[] b) => a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1] != b[1] ? a[1].CompareTo(b[1]) : a[2].CompareTo(b[2]);

        // ═════════════════════════════ diff ═════════════════════════════
        /// <summary>
        /// 兩個時間點之間的差異：放了幾格（空 → 有）、刻了幾格（有 → 空）、換色幾格（有 → 另一色），分顏色列；
        /// 給 <see cref="SCP_SculptDiffArgs.Out"/> ⇒ 寫差異清單（每行 <c>x,y,z,before,after</c>，0 ＝ 空，照 x→y→z 排）。
        /// <para>中間改過又改回原樣的格不算差異（只比兩端）。⛔ 不讀不寫主快取。</para>
        /// </summary>
        public SCP_SculptResult Diff(SCP_SculptDiffArgs iArgs)
        {
            if (iArgs.From.Trim().Length == 0)
                return SCP_SculptResult.Error(2, "diff 要 from=（標記 name／name:begin／name:end、start，或事件檔 *.json）");
            int[]? aRegion = null;
            if (iArgs.Region.Trim().Length > 0)
            {
                if (!TryParseRegion(iArgs.Region.Trim(), out int[] r)) return SCP_SculptResult.Error(2, "--region 需為 'x1..x2,y1..y2,z1..z2': " + iArgs.Region);
                aRegion = r;
            }
            List<SCP_SculptStore.EventFile> aAll = SCP_SculptStore.ListEvents(Paths);
            int a, b;
            try
            {
                a = SCP_SculptMarks.ResolveIndex(Paths, aAll, iArgs.From, "from");
                b = SCP_SculptMarks.ResolveIndex(Paths, aAll, iArgs.To, "to");
            }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException) { return SCP_SculptResult.Error(2, e.Message); }
            string Shown(int i) => i < 0 ? "start" : aAll[i].Rel.Replace('\\', '/');
            if (a > b)
                return SCP_SculptResult.Error(2, "from（第 " + (a + 1) + " 個事件）晚於 to（第 " + (b + 1) + " 個）—— 差異只往後算；要反過來看就把兩端對調");

            var aRes = new SCP_SculptDiffResult { FromIndex = a, ToIndex = b, FromEvent = Shown(a), ToEvent = Shown(b) };
            var aPrior = new Dictionary<long, int>();
            var aSpace = new SCP_SculptSpace();
            int aCur = -1;
            aSpace.Voxels.BeforeChange = (x, y, z) =>
            {
                if (aCur <= a) return;
                if (aRegion != null && (x < aRegion[0] || x > aRegion[1] || y < aRegion[2] || y > aRegion[3] || z < aRegion[4] || z > aRegion[5])) return;
                long k = PackKey(x, y, z);
                if (!aPrior.ContainsKey(k)) aPrior[k] = aSpace.Voxels.Get(x, y, z);
            };
            try
            {
                for (int i = 0; i <= b && i < aAll.Count; i++)
                {
                    SCP_JsonData? aEv = SCP_SculptStore.ReadEvent(aAll[i].Full);
                    if (i > a)
                    {
                        aRes.EventCount++;
                        string aOp = aEv == null ? "(讀不出來)" : aEv["op"].IsString ? aEv["op"].AsString() : "(沒有 op)";
                        aRes.EventOps[aOp] = (aRes.EventOps.TryGetValue(aOp, out int n) ? n : 0) + 1;
                    }
                    if (aEv == null) continue;
                    aCur = i;
                    SCP_SculptStore.ApplyEvent(aSpace, aEv, aAll[i].Rel);
                }
            }
            catch (SCP_SculptReplayException e) { return ReplayFail(e); }
            finally { aSpace.Voxels.BeforeChange = null; }

            var aRows = new List<int[]>();
            foreach (var kv in aPrior)
            {
                int x = KeyX(kv.Key), y = KeyY(kv.Key), z = KeyZ(kv.Key);
                int before = kv.Value, after = aSpace.Voxels.Get(x, y, z);
                if (before == after) continue;
                aRows.Add(new[] { x, y, z, before, after });
                if (before == 0) { aRes.Placed++; Bump(aRes.PlacedByColor, after); }
                else if (after == 0) { aRes.Carved++; Bump(aRes.CarvedByColor, before); }
                else
                {
                    aRes.Recolored++;
                    var p = (before, after);
                    aRes.RecoloredByPair[p] = (aRes.RecoloredByPair.TryGetValue(p, out int n) ? n : 0) + 1;
                }
            }
            aRows.Sort(CompareXyz);

            aRes.Lines.Add("# 🔀 Sculpture Diff:");
            aRes.Lines.Add("  from      : " + iArgs.From.Trim() + " → " + aRes.FromEvent + (a >= 0 ? "（第 " + (a + 1) + " 個事件）" : "（第一個事件之前）"));
            aRes.Lines.Add("  to        : " + (iArgs.To.Trim().Length == 0 ? SCP_SculptMarks.Now : iArgs.To.Trim()) + " → " + aRes.ToEvent
                           + (b >= 0 ? "（第 " + (b + 1) + " 個事件）" : "（第一個事件之前）"));
            if (aRegion != null) aRes.Lines.Add("  region    : " + iArgs.Region.Trim());
            var aOps = new StringBuilder();
            foreach (var kv in aRes.EventOps) aOps.Append(aOps.Length > 0 ? " " : "").Append(kv.Key).Append('×').Append(kv.Value);
            aRes.Lines.Add("  events    : " + aRes.EventCount + (aOps.Length > 0 ? "（" + aOps + "）" : ""));
            aRes.Lines.Add("  placed    : " + aRes.Placed + ByColor(aRes.PlacedByColor));
            aRes.Lines.Add("  carved    : " + aRes.Carved + ByColor(aRes.CarvedByColor));
            var aPairs = new StringBuilder();
            foreach (var kv in aRes.RecoloredByPair) aPairs.Append(aPairs.Length > 0 ? " " : "").Append(kv.Key.From).Append('→').Append(kv.Key.To).Append('×').Append(kv.Value);
            aRes.Lines.Add("  recolored : " + aRes.Recolored + (aPairs.Length > 0 ? "（" + aPairs + "）" : ""));

            if (iArgs.Out.Trim().Length > 0)
            {
                string aOut = SCP_SculptPy.PathStr(iArgs.Out.Trim());
                string? aDir = Path.GetDirectoryName(Path.GetFullPath(aOut));
                if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
                var aSb = new StringBuilder();
                aSb.Append("# sculpture diff —— x,y,z,before,after（0 ＝ 空；照 x→y→z 排）\n");
                aSb.Append("# from ").Append(aRes.FromEvent).Append("  to ").Append(aRes.ToEvent).Append(aRegion != null ? "  region " + iArgs.Region.Trim() : "").Append('\n');
                aSb.Append("# placed ").Append(aRes.Placed).Append("  carved ").Append(aRes.Carved).Append("  recolored ").Append(aRes.Recolored).Append('\n');
                foreach (int[] r in aRows)
                    aSb.Append(r[0]).Append(',').Append(r[1]).Append(',').Append(r[2]).Append(',').Append(r[3]).Append(',').Append(r[4]).Append('\n');
                byte[] aBytes = new UTF8Encoding(false).GetBytes(aSb.ToString());
                File.WriteAllBytes(aOut, aBytes);
                aRes.OutputPath = aOut;
                aRes.Sha256 = SCP_SculptPy.Sha256Hex(aBytes);
                aRes.Lines.Add("  list      : " + aOut + "（" + aRows.Count + " 行，sha256 " + aRes.Sha256 + "）");
            }
            return aRes;
        }

        static void Bump(SortedDictionary<int, int> iMap, int iKey) => iMap[iKey] = (iMap.TryGetValue(iKey, out int n) ? n : 0) + 1;

        static string ByColor(SortedDictionary<int, int> iMap)
        {
            if (iMap.Count == 0) return "";
            var aSb = new StringBuilder("（");
            foreach (var kv in iMap) aSb.Append(aSb.Length > 1 ? " " : "").Append('c').Append(kv.Key).Append('×').Append(kv.Value);
            return aSb.Append('）').ToString();
        }

        // ═════════════════════════════ stats 的結構讀數 ═════════════════════════════
        /// <summary>
        /// stats：什麼都沒給 ⇒ 與 python 時代逐字相同（總數＋使用率）。
        /// 給了 structure／mirror／region／exclude_color ⇒ 另算範圍內的結構讀數（元件、封閉空腔、鏡像差）。
        /// <para>吃 <see cref="ViewFilter"/>（標記那一刻、撤掉／只看某一段）—— 例：量某個階段結束時有沒有封閉空腔。</para>
        /// </summary>
        public SCP_SculptResult Stats(SCP_SculptStatsArgs iArgs)
        {
            int[] aBox = { 0, SizeX - 1, 0, SizeY - 1, 0, SizeZ - 1 };
            bool aHasRegion = iArgs.Region.Trim().Length > 0;
            if (aHasRegion && !TryParseRegion(iArgs.Region.Trim(), out aBox))
                return SCP_SculptResult.Error(2, "--region 需為 'x1..x2,y1..y2,z1..z2': " + iArgs.Region);
            var aExclude = new HashSet<int>();
            foreach (string p in iArgs.ExcludeColor.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (!SCP_SculptPy.TryInt(p.Trim(), out int c) || c < 1 || c > 255) return SCP_SculptResult.Error(2, "exclude_color 要是 1..255 的整數（逗號分隔）：" + iArgs.ExcludeColor);
                aExclude.Add(c);
            }
            string aMirAxis = "";
            int aMirTwice = 0;
            if (iArgs.Mirror.Trim().Length > 0)
            {
                string[] m = iArgs.Mirror.Trim().Split(':');
                if (m.Length != 2 || (m[0] != "x" && m[0] != "y" && m[0] != "z")
                    || !double.TryParse(m[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double aPivot)
                    || Math.Abs(aPivot * 2 - Math.Round(aPivot * 2)) > 1e-9 || aPivot < 0 || aPivot > SCP_SculptWorks.MaxAxisHard)
                    return SCP_SculptResult.Error(2, "mirror 要是 軸:中心（x|y|z，中心是整數或 .5 —— y:160 ＝ 以第 160 格的中心為鏡面、y:159.5 ＝ 159 與 160 兩格之間）：" + iArgs.Mirror);
                aMirAxis = m[0];
                aMirTwice = (int)Math.Round(aPivot * 2);
            }

            SCP_SculptSpace aSpace;
            try { aSpace = LoadView(); } catch (SCP_SculptReplayException e) { return ReplayFail(e); } catch (ArgumentException e) { return SCP_SculptResult.Error(2, e.Message); }
            int n = aSpace.Voxels.Count;
            var aRes = new SCP_SculptStatsResult { TotalVoxels = n };
            aRes.Lines.Add("# 📊 3D Sculpture Stats:");
            aRes.Lines.Add("  總非空 Voxels 數 : " + n.ToString(CultureInfo.InvariantCulture));
            aRes.Lines.Add("  空間使用率       : " + ((double)n / ((long)SizeX * SizeY * SizeZ) * 100).ToString("F6", CultureInfo.InvariantCulture) + "%");
            if (!iArgs.Scoped) return aRes;

            var aCells = new Dictionary<long, int>();
            foreach (var e in aSpace.Voxels.Entries())
            {
                if (e.Color == 0 || aExclude.Contains(e.Color)) continue;
                if (e.X < aBox[0] || e.X > aBox[1] || e.Y < aBox[2] || e.Y > aBox[3] || e.Z < aBox[4] || e.Z > aBox[5]) continue;
                aCells[PackKey(e.X, e.Y, e.Z)] = e.Color;
            }
            aRes.ScopedVoxels = aCells.Count;
            aRes.Lines.Add("  結構讀數範圍     : " + (aHasRegion ? "region " + iArgs.Region.Trim() : "整件")
                           + (aExclude.Count > 0 ? "，不算顏色 " + string.Join(",", new SortedSet<int>(aExclude)) : "") + " ⇒ " + aCells.Count + " 格");

            if (iArgs.Structure)
            {
                Components(aCells, aRes);
                aRes.Lines.Add("  元件（6 連通）   : " + aRes.Components + " 塊" + (aRes.Components > 0 ? "，最大 " + aRes.LargestComponent + " 格" : ""));
                if (aRes.Components > 1)
                    foreach (SCP_SculptBlob s in aRes.SmallestComponents) aRes.Lines.Add("    · 小塊 " + s);
                Cavities(aCells, aRes);
                if (aRes.Cavities < 0) aRes.Lines.Add("  封閉空腔         : 沒算 —— " + aRes.CavitySkipped);
                else
                {
                    aRes.Lines.Add("  封閉空腔（6 連通）: " + aRes.Cavities + " 個，共 " + aRes.CavityCells + " 格");
                    foreach (SCP_SculptBlob s in aRes.LargestCavities) aRes.Lines.Add("    · 空腔 " + s);
                }
            }
            if (aMirAxis.Length > 0)
            {
                Mirror(aCells, aRes, aMirAxis, aMirTwice, aHasRegion ? aBox : null);
                aRes.Lines.Add("  鏡像差 " + aMirAxis + ":" + aRes.MirrorPivot.ToString("0.#", CultureInfo.InvariantCulture)
                               + "     : 低側有而高側空 " + aRes.MirrorMissingLow + "、高側有而低側空 " + aRes.MirrorMissingHigh
                               + "、兩邊都有但顏色不同 " + aRes.MirrorColorDiff + " 對"
                               + (aRes.MirrorSkipped > 0 ? "（鏡像落在 region 外沒比 " + aRes.MirrorSkipped + " 格）" : ""));
                foreach (string s in aRes.MirrorSamples) aRes.Lines.Add("    · " + s);
            }
            return aRes;
        }

        /// <summary>python 時代的無參數版（輸出逐字相同）。</summary>
        public SCP_SculptResult Stats() => Stats(new SCP_SculptStatsArgs());

        static SCP_SculptBlob NewBlob(int x, int y, int z)
            => new SCP_SculptBlob { Min = new[] { x, y, z }, Max = new[] { x, y, z }, Cell = new[] { x, y, z } };

        static void Grow(SCP_SculptBlob b, int x, int y, int z)
        {
            b.Size++;
            if (x < b.Min[0]) b.Min[0] = x; if (x > b.Max[0]) b.Max[0] = x;
            if (y < b.Min[1]) b.Min[1] = y; if (y > b.Max[1]) b.Max[1] = y;
            if (z < b.Min[2]) b.Min[2] = z; if (z > b.Max[2]) b.Max[2] = z;
            int[] c = { x, y, z };
            if (CompareXyz(c, b.Cell) < 0) b.Cell = c;
        }

        /// <summary>6 連通元件：雜湊表上 BFS（不需要格子圖，座標再散也算得動）。</summary>
        static void Components(Dictionary<long, int> iCells, SCP_SculptStatsResult ioRes)
        {
            var aSeen = new HashSet<long>();
            var aBlobs = new List<SCP_SculptBlob>();
            var aStack = new Stack<long>();
            foreach (long k0 in iCells.Keys)
            {
                if (!aSeen.Add(k0)) continue;
                var aBlob = NewBlob(KeyX(k0), KeyY(k0), KeyZ(k0));
                aStack.Push(k0);
                while (aStack.Count > 0)
                {
                    long k = aStack.Pop();
                    int x = KeyX(k), y = KeyY(k), z = KeyZ(k);
                    Grow(aBlob, x, y, z);
                    foreach (int[] d in s_Nb6)
                    {
                        int nx = x + d[0], ny = y + d[1], nz = z + d[2];
                        if (nx < 0 || ny < 0 || nz < 0) continue;
                        long nk = PackKey(nx, ny, nz);
                        if (iCells.ContainsKey(nk) && aSeen.Add(nk)) aStack.Push(nk);
                    }
                }
                aBlobs.Add(aBlob);
            }
            aBlobs.Sort((p, q) => p.Size != q.Size ? p.Size.CompareTo(q.Size) : CompareXyz(p.Cell, q.Cell));
            ioRes.Components = aBlobs.Count;
            ioRes.LargestComponent = aBlobs.Count > 0 ? aBlobs[aBlobs.Count - 1].Size : 0;
            for (int i = 0; i < aBlobs.Count - 1 && i < StructureSamples; i++) ioRes.SmallestComponents.Add(aBlobs[i]);
        }

        /// <summary>
        /// 封閉空腔：外框外擴一格攤成格子圖，從角落（一定是空的）6 連通灌外面的空氣；灌不到的空格 ＝ 空腔，再逐塊數。
        /// ＝ python 的 <c>ndimage.label(~A)</c> 取角落那一塊以外的空格（check_ports.py 的 cavities()）。
        /// </summary>
        static void Cavities(Dictionary<long, int> iCells, SCP_SculptStatsResult ioRes)
        {
            if (iCells.Count == 0) { ioRes.Cavities = 0; return; }
            int[] lo = { int.MaxValue, int.MaxValue, int.MaxValue }, hi = { int.MinValue, int.MinValue, int.MinValue };
            foreach (long k in iCells.Keys)
            {
                int x = KeyX(k), y = KeyY(k), z = KeyZ(k);
                if (x < lo[0]) lo[0] = x; if (x > hi[0]) hi[0] = x;
                if (y < lo[1]) lo[1] = y; if (y > hi[1]) hi[1] = y;
                if (z < lo[2]) lo[2] = z; if (z > hi[2]) hi[2] = z;
            }
            // 外擴一格（座標可以是 −1：格子圖用相對 lo 的索引，不碰雜湊鍵）
            long nx = hi[0] - lo[0] + 3, ny = hi[1] - lo[1] + 3, nz = hi[2] - lo[2] + 3;
            long aTotal = nx * ny * nz;
            if (aTotal > MaxStructureCells)
            {
                ioRes.CavitySkipped = "外框（含外擴一格）" + nx + "×" + ny + "×" + nz + " ＝ " + aTotal.ToString("N0", CultureInfo.InvariantCulture)
                                      + " 格，超過上限 " + MaxStructureCells.ToString("N0", CultureInfo.InvariantCulture) + " —— 給 region 縮小再量";
                return;
            }
            var aOcc = new ulong[(aTotal + 63) / 64];
            var aSeen = new ulong[(aTotal + 63) / 64];
            long Idx(long x, long y, long z) => (x * ny + y) * nz + z;
            bool Get(ulong[] b, long i) => (b[i >> 6] & (1UL << (int)(i & 63))) != 0;
            void Set(ulong[] b, long i) => b[i >> 6] |= 1UL << (int)(i & 63);
            foreach (long k in iCells.Keys) Set(aOcc, Idx(KeyX(k) - lo[0] + 1, KeyY(k) - lo[1] + 1, KeyZ(k) - lo[2] + 1));

            var aQueue = new Queue<long>();
            SCP_SculptBlob? Flood(long iStart, bool iTrack)
            {
                SCP_SculptBlob? aBlob = null;
                Set(aSeen, iStart);
                aQueue.Enqueue(iStart);
                while (aQueue.Count > 0)
                {
                    long i = aQueue.Dequeue();
                    long z = i % nz, y = (i / nz) % ny, x = i / (nz * ny);
                    if (iTrack)
                    {
                        int wx = (int)x + lo[0] - 1, wy = (int)y + lo[1] - 1, wz = (int)z + lo[2] - 1;
                        if (aBlob == null) { aBlob = NewBlob(wx, wy, wz); }
                        Grow(aBlob, wx, wy, wz);
                    }
                    if (x + 1 < nx) Visit(i + nz * ny);
                    if (x > 0) Visit(i - nz * ny);
                    if (y + 1 < ny) Visit(i + nz);
                    if (y > 0) Visit(i - nz);
                    if (z + 1 < nz) Visit(i + 1);
                    if (z > 0) Visit(i - 1);
                }
                return aBlob;
            }
            void Visit(long j)
            {
                if (Get(aOcc, j) || Get(aSeen, j)) return;
                Set(aSeen, j);
                aQueue.Enqueue(j);
            }

            Flood(0, false);   // 外擴那一圈全是空的、彼此相連 ⇒ 從角落灌就是「外面」
            var aBlobs = new List<SCP_SculptBlob>();
            int aCells = 0;
            for (long w = 0; w < aOcc.Length; w++)
            {
                ulong aFree = ~(aOcc[w] | aSeen[w]);
                if (aFree == 0) continue;
                for (int bit = 0; bit < 64; bit++)
                {
                    long i = (w << 6) + bit;
                    if (i >= aTotal) break;
                    if ((aFree & (1UL << bit)) == 0 || Get(aSeen, i)) continue;
                    SCP_SculptBlob aBlob = Flood(i, true)!;
                    aBlobs.Add(aBlob);
                    aCells += aBlob.Size;
                }
            }
            aBlobs.Sort((p, q) => p.Size != q.Size ? q.Size.CompareTo(p.Size) : CompareXyz(p.Cell, q.Cell));
            ioRes.Cavities = aBlobs.Count;
            ioRes.CavityCells = aCells;
            for (int i = 0; i < aBlobs.Count && i < StructureSamples; i++) ioRes.LargestCavities.Add(aBlobs[i]);
        }

        /// <summary>
        /// 鏡像差：每一格跟它對 <paramref name="iAxis"/> 鏡面的那一格比（c → 2·中心 − c）。鏡面上的格對到自己。
        /// 給了 region ⇒ 鏡像落在 region 外的格不比（計 skipped）；沒給 ⇒ 鏡像在空間外就是空。
        /// </summary>
        static void Mirror(Dictionary<long, int> iCells, SCP_SculptStatsResult ioRes, string iAxis, int iTwice, int[]? iRegion)
        {
            int a = iAxis == "x" ? 0 : iAxis == "y" ? 1 : 2;
            ioRes.MirrorAxis = iAxis;
            ioRes.MirrorPivot = iTwice / 2.0;
            ioRes.MirrorMissingLow = 0;
            var aSamples = new List<int[]>();   // x,y,z,kind（0 低側缺鏡像、1 高側缺鏡像、2 顏色不同）
            foreach (var kv in iCells)
            {
                int[] p = { KeyX(kv.Key), KeyY(kv.Key), KeyZ(kv.Key) };
                long m = (long)iTwice - p[a];
                if (m == p[a]) continue;   // 鏡面上的格：鏡像就是自己
                if (iRegion != null && (m < iRegion[a * 2] || m > iRegion[a * 2 + 1])) { ioRes.MirrorSkipped++; continue; }
                int aOther = 0;
                if (m >= 0 && m < SCP_SculptWorks.MaxAxisHard)
                {
                    int[] q = { p[0], p[1], p[2] };
                    q[a] = (int)m;
                    iCells.TryGetValue(PackKey(q[0], q[1], q[2]), out aOther);
                }
                bool aLow = p[a] * 2 < iTwice;
                if (aOther == 0)
                {
                    if (aLow) ioRes.MirrorMissingLow++; else ioRes.MirrorMissingHigh++;
                    aSamples.Add(new[] { p[0], p[1], p[2], aLow ? 0 : 1, kv.Value });
                }
                else if (aOther != kv.Value && aLow)
                {
                    ioRes.MirrorColorDiff++;
                    aSamples.Add(new[] { p[0], p[1], p[2], 2, kv.Value, aOther });
                }
            }
            aSamples.Sort(CompareXyz);
            for (int i = 0; i < aSamples.Count && i < StructureSamples; i++)
            {
                int[] s = aSamples[i];
                string at = "(" + s[0] + "," + s[1] + "," + s[2] + ")";
                ioRes.MirrorSamples.Add(s[3] == 2 ? at + " c" + s[4] + " ↔ 鏡像 c" + s[5] : at + " c" + s[4] + " 的鏡像是空的");
            }
        }

        // ═════════════════════════════ carvevox ═════════════════════════════
        /// <summary>
        /// 照清單刻（stampvox 的反向）：只刻清單上的格 —— 清單外一格都不動；刻掉數 ＝ 清單裡現在有東西的格數。
        /// <para>閘門與 stampvox 同型：清單錯 2 → at 2 → ①expect_pixels 4 → 空 3 → ③越界 5 → 顏色守門 4 → 全都已經是空的 3 → 落事件。</para>
        /// <para>事件記成 <c>op=carve</c>（逐格 <c>carved_voxels</c>，外加 mode=list 與清單 sha256）⇒ 沒更新的 senate.exe 也重播得出來、undo 照常。</para>
        /// </summary>
        public SCP_SculptResult CarveVox(SCP_SculptCarveVoxArgs iArgs)
        {
            string aShown = SCP_SculptPy.PathStr(iArgs.Voxels);
            if (!File.Exists(iArgs.Voxels)) return SCP_SculptResult.Error(2, "格子清單不存在: " + aShown);
            byte[] aBytes;
            try { aBytes = File.ReadAllBytes(iArgs.Voxels); }
            catch (Exception e) { return SCP_SculptResult.Error(2, "讀清單失敗 " + aShown + ": " + e.Message); }
            if (!TryParseCarveList(aBytes, out List<int[]> aVox, out string aErr))
                return SCP_SculptResult.Error(2, "格子清單 " + aShown + "：" + aErr + " —— 未刻");
            var aSrc = new SCP_SculptPyObj()
                .Put("kind", "voxel_file")
                .Put("file", SCP_SculptPy.FullPathStr(iArgs.Voxels))
                .Put("sha256", SCP_SculptPy.Sha256Hex(aBytes))
                .Put("voxel_count", aVox.Count);

            SCP_SculptSpace aSpace;
            try { aSpace = LoadSpace(); } catch (SCP_SculptReplayException e) { return ReplayFail(e); }
            string[] p = (iArgs.At ?? "None").Split(',');
            var aAt = new int[3];
            if (p.Length != 3 || !SCP_SculptPy.TryInt(p[0], out aAt[0]) || !SCP_SculptPy.TryInt(p[1], out aAt[1]) || !SCP_SculptPy.TryInt(p[2], out aAt[2]))
                return SCP_SculptResult.Error(2, "--at 需為 'x,y,z': " + (iArgs.At ?? "None"));

            const string Op = "carvevox";
            var aRes = new SCP_SculptCarveResult { Persona = iArgs.Persona, ListCount = aVox.Count, Source = aSrc };
            SCP_SculptResult Refuse(int iExit, string iStatus, string iReason)
            {
                aRes.ExitCode = iExit;
                aRes.Reason = iReason;
                aRes.Json = new SCP_SculptPyObj().Put("status", iStatus).Put("op", Op).Put("reason", iReason)
                    .Put("list_count", aVox.Count).Put("already_empty", aRes.AlreadyEmpty).Put("out_of_bounds", aRes.OutOfBounds)
                    .Put("color_mismatch", aRes.ColorMismatch).Put("source", aSrc);
                return aRes;
            }
            if (iArgs.ExpectPixels.HasValue && iArgs.ExpectPixels.Value != aVox.Count)
                return Refuse(4, "mismatch", "--expect-pixels " + iArgs.ExpectPixels.Value + " 與清單格數 " + aVox.Count + " 不符 —— 清單已變動或不是寫的那份；未刻");
            if (aVox.Count == 0) return Refuse(3, "empty", "清單沒有任何格子（空行與 # 註解不算）—— 無可刻內容");

            var aCarved = new List<int[]>();
            var aWrong = new List<string>();
            foreach (int[] v in aVox)
            {
                long x = (long)aAt[0] + v[0], y = (long)aAt[1] + v[1], z = (long)aAt[2] + v[2];
                if (x < 0 || x >= SizeX || y < 0 || y >= SizeY || z < 0 || z >= SizeZ) { aRes.OutOfBounds++; continue; }
                int aNow = aSpace.Voxels.Get((int)x, (int)y, (int)z);
                if (aNow == 0) { aRes.AlreadyEmpty++; continue; }
                if (v[3] != 0 && aNow != v[3])
                {
                    aRes.ColorMismatch++;
                    if (aWrong.Count < StructureSamples) aWrong.Add("(" + x + "," + y + "," + z + ") 清單 c" + v[3] + "／現在 c" + aNow);
                    continue;
                }
                aCarved.Add(new[] { (int)x, (int)y, (int)z });
            }
            if (aRes.OutOfBounds > 0 && !iArgs.AllowClip)
                return Refuse(5, "out_of_bounds", aRes.OutOfBounds + " 格落在 " + SizeX + "x" + SizeY + "x" + SizeZ + " 空間之外 —— 未刻。改 --at，或顯式 --allow-clip 只刻界內的");
            if (aRes.ColorMismatch > 0)
                return Refuse(4, "color_mismatch", aRes.ColorMismatch + " 格現在的顏色跟清單寫的不一樣 —— 清單跟作品對不上，未刻（⛔ 不猜哪一邊對）：" + string.Join("；", aWrong));
            if (aCarved.Count == 0) return Refuse(3, "empty", "清單上的格現在全是空的（" + aRes.AlreadyEmpty + " 格）—— 沒有東西可刻");

            aCarved.Sort(CompareXyz);
            int[] lo = { int.MaxValue, int.MaxValue, int.MaxValue }, hi = { int.MinValue, int.MinValue, int.MinValue };
            foreach (int[] v in aCarved)
                for (int k = 0; k < 3; k++) { lo[k] = Math.Min(lo[k], v[k]); hi[k] = Math.Max(hi[k], v[k]); }
            var aEv = new SCP_SculptPyObj()
                .Put("op", "carve").Put("persona", iArgs.Persona).Put("mode", "list").Put("source", aSrc).Put("at", aAt)
                .Put("x1", lo[0]).Put("x2", hi[0]).Put("y1", lo[1]).Put("y2", hi[1]).Put("z1", lo[2]).Put("z2", hi[2])
                .Put("list_count", aVox.Count).Put("already_empty", aRes.AlreadyEmpty).Put("out_of_bounds", aRes.OutOfBounds)
                .Put("carved_count", aCarved.Count).Put("carved_voxels", aCarved)
                .Put("timestamp", SCP_SculptPy.IsoFormat(Clock()));
            string aFile = SCP_SculptStore.RecordEvent(Paths, aEv, Clock(), out string aRel);
            // 清單非空 ⇒ ApplyCarve 只刪清單上的格（⛔ 不會退回 AABB —— 空清單在上面已經擋掉）
            SCP_SculptStore.ApplyCarve(aSpace, aCarved, lo[0], hi[0], lo[1], hi[1], lo[2], hi[2]);
            aSpace.LastEventFile = aRel;
            SCP_SculptStore.SaveCache(Paths, aSpace);

            aRes.CarvedCount = aCarved.Count;
            aRes.EventFile = aFile;
            aRes.Json = new SCP_SculptPyObj()
                .Put("status", "success").Put("op", Op).Put("persona", iArgs.Persona)
                .Put("list_count", aVox.Count).Put("carved_count", aCarved.Count).Put("already_empty", aRes.AlreadyEmpty)
                .Put("out_of_bounds", aRes.OutOfBounds).Put("at", aAt).Put("source", aSrc).Put("event_file", aFile);
            return aRes;
        }
    }
}
