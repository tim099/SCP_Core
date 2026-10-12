// 區塊職責：雕刻場景的 **CPU 網格**（TASK-0507，從 Senate.Desktop 的 GPU 渲染器搬上來）—— voxel 外露面（逐頂點 AO、可合併同色面）、
//          繩的管子、帆的雙面布、外部模型的三角形，全部在這裡變成「頂點陣列＋索引」；渲染器只負責上傳與畫。
// 物理意義：⭐ 同一份場景在哪個宿主建網格都是同一支 ⇒ 「網格長什麼樣」只有一個答案（digest 逐位元可比）。
//          頂點格式：每頂點 7 個 float 槽 —— 位置 3f、法線 3f、最後一槽是 4ub（R,G,B,AO×85）的位元重解（28 bytes）。
//          座標系：⚠ **GL 座標**（世界 x, −y, z）—— 作品是照舊引擎那張鏡像的圖雕的，渲染器把世界 y 取負再用標準右手系
//          （見 SenateSculptRenderer 檔頭）。y 鏡像讓繞序反轉 ⇒ 這裡寫的三角形已經反過來，GL 裡從面外看是逆時針。
//          框景與外框（Centers／Min／Max）留在**世界座標**（渲染器自己換）。
// 數值影響：搬家前後同一份場景的網格 digest 逐位元相同（邏輯逐字照搬；只把 net 6 才有的 BitConverter.UInt32BitsToSingle
//          換成 Int32BitsToSingle —— 同一組位元）。
// 方言：C# 9 / netstandard2.1（Span／stackalloc 可用；System.Numerics.Vector3 可用）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Numerics;
using SCP.Core.Canvas;

namespace SCP.Core.Sculpture
{
    /// <summary>一份 CPU 網格（GL 座標的頂點與索引；外框與框景點在世界座標）。</summary>
    public sealed class SCP_SculptMesh
    {
        /// <summary>每頂點 7 個 float 槽（最後一槽是 4ub 的位元重解）。</summary>
        public const int FloatsPerVertex = 7;
        public float[] Vertices = Array.Empty<float>();
        public uint[] Indices = Array.Empty<uint>();
        public int VertexCount;
        public int IndexCount;
        public int VoxelCount;
        /// <summary>繩索（TASK-0502）：併進來幾條、幾個三角形。</summary>
        public int RopeCount, RopeTriangles;
        /// <summary>帆（TASK-0503，另一趟的網格）：幾面、幾個三角形（雙面算兩份）。</summary>
        public int SailCount, SailTriangles;
        /// <summary>外部模型（TASK-0507，另一趟的網格）：幾個、幾個三角形。</summary>
        public int ModelCount, ModelTriangles;
        /// <summary>有東西可框（voxel、繩，或另一趟的外框）。</summary>
        public bool HasGeometry => VoxelCount > 0 || RopeCount > 0 || Extra.Length > 0;
        /// <summary>
        /// 另一趟（帆、外部模型）的外框八角 —— 只在 <see cref="WithExtra"/> 的淺複本上有值：框景／地板／陰影圖把它們也算進去，
        /// ⛔ 不碰 voxel 網格本身（不重建、不重傳）。
        /// </summary>
        public Vector3[] Extra = Array.Empty<Vector3>();
        /// <summary>外露面數（合併前）與實際輸出的矩形塊數（不合併時兩者相等）—— 讀數用。</summary>
        public int FaceCount, QuadCount;
        /// <summary>voxel 中心（世界座標，已去重、排序）—— 鏡頭框景用。</summary>
        public Vector3[] Centers = Array.Empty<Vector3>();
        /// <summary>世界座標 AABB（voxel 格的外緣；另一趟 ⇒ 頂點的外框）。</summary>
        public Vector3 Min, Max;
        /// <summary>頂點（逐位元）＋索引的 FNV-1a；建好時算一次。</summary>
        public ulong Digest;

        /// <summary>
        /// 淺複本＋另一趟的外框（框景、地板、燈用；畫 voxel 仍用原本那份的 VAO）。每一份非空的另一趟各給八角（依參數順序）；
        /// 全部是空的 ⇒ 回自己。
        /// </summary>
        public SCP_SculptMesh WithExtra(params SCP_SculptMesh[] iExtras)
        {
            var aLive = new List<SCP_SculptMesh>();
            foreach (var e in iExtras) if (e != null && e.IndexCount > 0) aLive.Add(e);
            if (aLive.Count == 0) return this;
            var m = (SCP_SculptMesh)MemberwiseClone();
            bool aHad = VoxelCount > 0 || RopeCount > 0;
            var aExtra = new List<Vector3>();
            foreach (var d in aLive)
            {
                m.Min = aHad ? Vector3.Min(m.Min, d.Min) : d.Min;
                m.Max = aHad ? Vector3.Max(m.Max, d.Max) : d.Max;
                aHad = true;
                for (int i = 0; i < 8; i++)
                    aExtra.Add(new Vector3((i & 1) != 0 ? d.Max.X : d.Min.X, (i & 2) != 0 ? d.Max.Y : d.Min.Y, (i & 4) != 0 ? d.Max.Z : d.Min.Z));
                m.IndexCount += d.IndexCount;   // ⚠ 只給「有沒有東西會投影子」那一格判斷用；畫的時候每一趟各用各自的數
            }
            m.Extra = aExtra.ToArray();
            return m;
        }

        /// <summary>框景點 ＝ voxel 中心＋另一趟的外框八角。</summary>
        public Vector3[] FrameCenters()
        {
            if (Extra.Length == 0) return Centers;
            var a = new Vector3[Centers.Length + Extra.Length];
            Array.Copy(Centers, a, Centers.Length);
            Array.Copy(Extra, 0, a, Centers.Length, Extra.Length);
            return a;
        }

        public static ulong DigestOf(float[] iVerts, uint[] iIdx)
        {
            ulong h = 1469598103934665603UL;
            foreach (float f in iVerts) { h ^= (uint)BitConverter.SingleToInt32Bits(f); h *= 1099511628211UL; }
            foreach (uint i in iIdx) { h ^= i; h *= 1099511628211UL; }
            return h;
        }
    }

    /// <summary>建網格（voxel、繩、帆、外部模型）。全部純函式、決定性（同輸入同位元組）。</summary>
    public static class SCP_SculptMeshBuilder
    {
        /// <summary>selftest 反向對照用：合併時**不看**四角 AO（⛔ 正式路徑永遠 false）。</summary>
        public static bool DebugMergeIgnoreAo;

        /// <summary>合併路徑的遮罩每邊上限（格）：超過的切片切成磚塊各自併 ⇒ 遮罩封頂 2048² 個 int（16 MB）、索引不溢位（TASK-0479 審查）。</summary>
        public const int MergeTile = 2048;

        // 六個面：法線 n、切向 u、v（u×v＝n，世界右手系；轉到 GL 鏡像後繞序反轉，建三角形時處理）。
        static readonly int[,] s_Faces =
        {
            //  nx ny nz   ux uy uz   vx vy vz
            {  1, 0, 0,   0, 1, 0,   0, 0, 1 },
            { -1, 0, 0,   0, 0, 1,   0, 1, 0 },
            {  0, 1, 0,   0, 0, 1,   1, 0, 0 },
            {  0,-1, 0,   1, 0, 0,   0, 0, 1 },
            {  0, 0, 1,   1, 0, 0,   0, 1, 0 },
            {  0, 0,-1,   0, 1, 0,   1, 0, 0 },
        };

        static float Packed(byte iR, byte iG, byte iB, int iA) => BitConverter.Int32BitsToSingle(unchecked((int)(iR | ((uint)iG << 8) | ((uint)iB << 16) | ((uint)iA << 24))));

        /// <summary>
        /// 稀疏佔用格（TASK-0479）：16³ 一塊、只配有 voxel 的塊 ⇒ 記憶體 ∝ 有東西的塊數，不再 ∝ 外框體積。
        /// <para>塊座標各 21 位元（±2^20 塊 ＝ ±16M voxel），超出就丟例外（⛔ 不安靜地撞 key）。</para>
        /// </summary>
        sealed class SparseGrid
        {
            const int Shift = 4, Low = 15, Bias = 1 << 20;
            readonly Dictionary<long, byte[]> m_Chunks = new Dictionary<long, byte[]>();
            long m_LastKey = long.MinValue;
            byte[]? m_Last;

            static long Key(int cx, int cy, int cz)
            {
                if (cx < -Bias || cx >= Bias || cy < -Bias || cy >= Bias || cz < -Bias || cz >= Bias)
                    throw new InvalidOperationException("voxel 座標超出稀疏格的範圍（塊 " + cx + "," + cy + "," + cz + "）");
                return ((long)(cx + Bias) << 42) | ((long)(cy + Bias) << 21) | (long)(cz + Bias);
            }

            byte[]? Chunk(int x, int y, int z, bool iCreate)
            {
                long k = Key(x >> Shift, y >> Shift, z >> Shift);
                if (k == m_LastKey) return m_Last;
                if (!m_Chunks.TryGetValue(k, out byte[]? c))
                {
                    if (!iCreate) return null;   // ⚠ 查無不進快取：之後同一塊被建出來時才不會拿到舊的 null
                    c = new byte[1 << (3 * Shift)];
                    m_Chunks[k] = c;
                }
                m_LastKey = k; m_Last = c;
                return c;
            }

            static int Cell(int x, int y, int z) => (x & Low) | ((y & Low) << Shift) | ((z & Low) << (2 * Shift));

            public byte Get(int x, int y, int z)
            {
                byte[]? c = Chunk(x, y, z, false);
                return c == null ? (byte)0 : c[Cell(x, y, z)];
            }

            public void Set(int x, int y, int z, byte iColor) => Chunk(x, y, z, true)![Cell(x, y, z)] = iColor;
        }

        // ════════════════════════════════════════════════════════════════════════════════════
        // 區塊職責：voxel —— 只出外露面（鄰格有 voxel 就不出），逐頂點 AO（每角看 3 個鄰格）。
        // 物理意義：三角形依 AO 選對角線，避免 AO 漸層的方向性條紋；<paramref name="iMerge"/> ⇒ 同色同 AO 的面 greedy 併成矩形。
        // ════════════════════════════════════════════════════════════════════════════════════
        public static SCP_SculptMesh Voxels(IReadOnlyList<SCP_SculptVoxel> iVoxels, bool iAo, bool iMerge = false)
        {
            var aMesh = new SCP_SculptMesh();
            if (iVoxels.Count == 0) return aMesh;

            int aMinX = int.MaxValue, aMinY = int.MaxValue, aMinZ = int.MaxValue;
            int aMaxX = int.MinValue, aMaxY = int.MinValue, aMaxZ = int.MinValue;
            for (int i = 0; i < iVoxels.Count; i++)
            {
                var v = iVoxels[i];
                if (v.Color == 0) continue;   // 0 ＝ 空（契約說不會出現；出現了就當沒有）
                if (v.X < aMinX) aMinX = v.X; if (v.X > aMaxX) aMaxX = v.X;
                if (v.Y < aMinY) aMinY = v.Y; if (v.Y > aMaxY) aMaxY = v.Y;
                if (v.Z < aMinZ) aMinZ = v.Z; if (v.Z > aMaxZ) aMaxZ = v.Z;
            }
            if (aMinX == int.MaxValue) return aMesh;
            // 合併那條路把外框內的座標各打包成 21 位元 ⇒ 任一軸跨度超過就明說（⛔ 不讓 key 安靜地撞在一起）。
            const long MaxSpan = 0x1FFFFF;
            if ((long)aMaxX - aMinX > MaxSpan || (long)aMaxY - aMinY > MaxSpan || (long)aMaxZ - aMinZ > MaxSpan)
                throw new InvalidOperationException("場景外框任一軸超過 " + (MaxSpan + 1).ToString("N0") + " 格（" + aMinX + ".." + aMaxX + ", " + aMinY + ".." + aMaxY + ", " + aMinZ + ".." + aMaxZ + "）");

            // 排序鍵 ＝ 外框內的 (z, y, x) 字典序（long，外框多大都不溢位）⇒ 與輸入順序無關，也與舊版的網格索引同序。
            long aSx = (long)aMaxX - aMinX + 1, aSy = (long)aMaxY - aMinY + 1;
            long SortKey(int x, int y, int z) => ((long)(z - aMinZ) * aSy + (y - aMinY)) * aSx + (x - aMinX);

            var aGrid = new SparseGrid();
            var aCoords = new List<long>(iVoxels.Count);
            for (int i = 0; i < iVoxels.Count; i++)
            {
                var v = iVoxels[i];
                if (v.Color == 0) continue;
                if (aGrid.Get(v.X, v.Y, v.Z) == 0) aCoords.Add(SortKey(v.X, v.Y, v.Z));
                aGrid.Set(v.X, v.Y, v.Z, v.Color);   // 重複座標：後者為準
            }
            aCoords.Sort();
            void Decode(long k, out int x, out int y, out int z)
            {
                x = (int)(k % aSx) + aMinX;
                y = (int)(k / aSx % aSy) + aMinY;
                z = (int)(k / (aSx * aSy)) + aMinZ;
            }
            bool Solid(int x, int y, int z) => aGrid.Get(x, y, z) != 0;

            aMesh.VoxelCount = aCoords.Count;
            aMesh.Min = new Vector3(aMinX, aMinY, aMinZ);
            aMesh.Max = new Vector3(aMaxX + 1, aMaxY + 1, aMaxZ + 1);

            // 框景點只收「至少有一面外露」的 voxel（TASK-0472）：被六面包住的 voxel 在任何投影方向都有鄰格比它更外側，
            // 永遠不是外框極值（透視的線性分式投影也一樣）⇒ 框景／光源範圍的 min／max 與全收時**逐位元相同**，點數卻少很多。
            var aCenters = new List<Vector3>(Math.Min(aCoords.Count, 1 << 20));
            for (int c = 0; c < aCoords.Count; c++)
            {
                Decode(aCoords[c], out int x, out int y, out int z);
                if (Solid(x + 1, y, z) && Solid(x - 1, y, z) && Solid(x, y + 1, z) && Solid(x, y - 1, z)
                    && Solid(x, y, z + 1) && Solid(x, y, z - 1)) continue;
                aCenters.Add(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
            }
            aMesh.Centers = aCenters.ToArray();

            var aVerts = new List<float>(Math.Min(aCoords.Count, 1 << 22) * 7);
            var aIdx = new List<uint>(Math.Min(aCoords.Count, 1 << 22) * 3 / 2);
            Span<int> aAo = stackalloc int[4];

            // 一角的 AO（0..3）：與舊版逐字同式。
            int CornerAo(int x, int y, int z, int f, int corner)
            {
                if (!iAo) return 3;
                int nx = s_Faces[f, 0], ny = s_Faces[f, 1], nz = s_Faces[f, 2];
                int ux = s_Faces[f, 3], uy = s_Faces[f, 4], uz = s_Faces[f, 5];
                int vx = s_Faces[f, 6], vy = s_Faces[f, 7], vz = s_Faces[f, 8];
                int a = (corner == 1 || corner == 2) ? 1 : 0;
                int b = (corner >= 2) ? 1 : 0;
                int su = a == 1 ? 1 : -1, sv = b == 1 ? 1 : -1;
                int bx = x + nx, by = y + ny, bz = z + nz;
                bool s1 = Solid(bx + ux * su, by + uy * su, bz + uz * su);
                bool s2 = Solid(bx + vx * sv, by + vy * sv, bz + vz * sv);
                bool cc = Solid(bx + ux * su + vx * sv, by + uy * su + vy * sv, bz + uz * su + vz * sv);
                return (s1 && s2) ? 0 : 3 - ((s1 ? 1 : 0) + (s2 ? 1 : 0) + (cc ? 1 : 0));
            }

            // 一塊 w×h 的矩形（單一 voxel 面 ＝ 1×1）：與舊版逐字同式的頂點與三角形。
            void EmitQuad(int x, int y, int z, int f, int w, int h, byte iColor, Span<int> iLevels)
            {
                int nx = s_Faces[f, 0], ny = s_Faces[f, 1], nz = s_Faces[f, 2];
                int ux = s_Faces[f, 3], uy = s_Faces[f, 4], uz = s_Faces[f, 5];
                int vx = s_Faces[f, 6], vy = s_Faces[f, 7], vz = s_Faces[f, 8];
                SCP_CanvasPalette.IndexToRgb(iColor, out byte aR, out byte aG, out byte aB);
                // 面所在平面的原點：法線朝正向的面在 +1 那一側。
                int px = x + Math.Max(nx, 0), py = y + Math.Max(ny, 0), pz = z + Math.Max(nz, 0);
                uint aBase = (uint)(aVerts.Count / 7);
                for (int corner = 0; corner < 4; corner++)
                {
                    // 角的順序 (0,0) (1,0) (1,1) (0,1) —— 從面外看是逆時針（世界右手系）。
                    int a = (corner == 1 || corner == 2) ? w : 0;
                    int b = (corner >= 2) ? h : 0;
                    float wx = px + ux * a + vx * b;
                    float wy = py + uy * a + vy * b;
                    float wz = pz + uz * a + vz * b;
                    aVerts.Add(wx); aVerts.Add(-wy); aVerts.Add(wz);      // 世界 → GL（y 鏡像）
                    aVerts.Add(nx); aVerts.Add(-ny); aVerts.Add(nz);
                    aVerts.Add(Packed(aR, aG, aB, iLevels[corner] * 85));
                }
                // y 鏡像讓繞序反轉 ⇒ 下面的三角形都是「反過來」寫，GL 裡從面外看才是逆時針。
                // 對角線：沿 AO 總和較大（較亮）的那一對切，讓暗角留在自己那個三角形裡（不拉出斜條紋）。
                if (iLevels[0] + iLevels[2] >= iLevels[1] + iLevels[3])
                {
                    aIdx.Add(aBase + 0); aIdx.Add(aBase + 2); aIdx.Add(aBase + 1);
                    aIdx.Add(aBase + 0); aIdx.Add(aBase + 3); aIdx.Add(aBase + 2);
                }
                else
                {
                    aIdx.Add(aBase + 0); aIdx.Add(aBase + 3); aIdx.Add(aBase + 1);
                    aIdx.Add(aBase + 1); aIdx.Add(aBase + 3); aIdx.Add(aBase + 2);
                }
                aMesh.QuadCount++;
            }

            if (!iMerge)
            {
                // 逐 voxel 面（舊行為；CLI `op=view` 走這條，輸出逐位元不變）。
                for (int c = 0; c < aCoords.Count; c++)
                {
                    Decode(aCoords[c], out int x, out int y, out int z);
                    byte aColor = aGrid.Get(x, y, z);
                    for (int f = 0; f < 6; f++)
                    {
                        if (Solid(x + s_Faces[f, 0], y + s_Faces[f, 1], z + s_Faces[f, 2])) continue;   // 鄰格有 voxel ⇒ 這面看不到
                        for (int corner = 0; corner < 4; corner++) aAo[corner] = CornerAo(x, y, z, f, corner);
                        aMesh.FaceCount++;
                        EmitQuad(x, y, z, f, 1, 1, aColor, aAo);
                    }
                }
            }
            else
            {
                // 合併同色面（TASK-0472）：每個朝向、每一層切片做一張 (u,v) 遮罩，greedy 併成最大矩形。
                // ⚠ 只併「四角 AO 都相同」的面 —— 不同的照逐面輸出（併了 AO 漸層會被拉成一大片、角落陰影被抹平）。
                // TASK-0479：先收這個朝向的外露面、依 (層, v, u) 排序，遮罩只配**那一層外露面的範圍** ——
                //   掃描順序與舊版（每層掃整個外框、v 外 u 內）相同，範圍外的格子在舊版也全是 0 ⇒ 輸出逐位元不變，
                //   成本卻從「外框體積 × 6」降到「外露面數＋各層外露範圍」。
                var aC = new int[3];
                Span<int> aLv = stackalloc int[4];
                int[] aMin = { aMinX, aMinY, aMinZ };
                var aFaces = new List<long>();
                int[] aMask = Array.Empty<int>();
                for (int f = 0; f < 6; f++)
                {
                    int aNa = s_Faces[f, 0] != 0 ? 0 : s_Faces[f, 1] != 0 ? 1 : 2;
                    int aUa = s_Faces[f, 3] != 0 ? 0 : s_Faces[f, 4] != 0 ? 1 : 2;
                    int aVa = s_Faces[f, 6] != 0 ? 0 : s_Faces[f, 7] != 0 ? 1 : 2;
                    aFaces.Clear();
                    for (int c = 0; c < aCoords.Count; c++)
                    {
                        Decode(aCoords[c], out int x, out int y, out int z);
                        if (Solid(x + s_Faces[f, 0], y + s_Faces[f, 1], z + s_Faces[f, 2])) continue;
                        aC[0] = x - aMinX; aC[1] = y - aMinY; aC[2] = z - aMinZ;
                        aFaces.Add(((long)aC[aNa] << 42) | ((long)aC[aVa] << 21) | (long)aC[aUa]);
                    }
                    aFaces.Sort();
                    var aBlocks = new List<List<long>>();
                    for (int i0 = 0; i0 < aFaces.Count;)
                    {
                        int s = (int)(aFaces[i0] >> 42);
                        int i1 = i0;
                        int su0 = int.MaxValue, su1 = int.MinValue, sv0 = int.MaxValue, sv1 = int.MinValue;
                        for (; i1 < aFaces.Count && (int)(aFaces[i1] >> 42) == s; i1++)
                        {
                            int fu = (int)(aFaces[i1] & 0x1FFFFF), fv = (int)((aFaces[i1] >> 21) & 0x1FFFFF);
                            if (fu < su0) su0 = fu; if (fu > su1) su1 = fu;
                            if (fv < sv0) sv0 = fv; if (fv > sv1) sv1 = fv;
                        }
                        // 遮罩每邊最多 MergeTile 格：這一層外露範圍兩邊都裝得下 ⇒ 整層一塊（所有既有作品都走這條，輸出不變）；
                        // 裝不下 ⇒ 從範圍左下角起切成磚塊，各自併（不跨磚合併）—— 遮罩記憶體封頂 MergeTile² 個 int，索引不會溢位。
                        aBlocks.Clear();
                        if (su1 - su0 < MergeTile && sv1 - sv0 < MergeTile)
                            aBlocks.Add(aFaces.GetRange(i0, i1 - i0));
                        else
                        {
                            var aTiles = new SortedDictionary<long, List<long>>();
                            for (int i = i0; i < i1; i++)
                            {
                                int fu = (int)(aFaces[i] & 0x1FFFFF), fv = (int)((aFaces[i] >> 21) & 0x1FFFFF);
                                long aKey = ((long)((fv - sv0) / MergeTile) << 32) | (long)((fu - su0) / MergeTile);
                                if (!aTiles.TryGetValue(aKey, out List<long>? aTile)) aTiles[aKey] = aTile = new List<long>();
                                aTile.Add(aFaces[i]);   // 照 (v, u) 序加進來 ⇒ 每塊內仍是 (v, u) 序
                            }
                            aBlocks.AddRange(aTiles.Values);
                        }
                        i0 = i1;

                        foreach (List<long> aBlock in aBlocks)
                        {
                            int u0 = int.MaxValue, u1 = int.MinValue, v0 = int.MaxValue, v1 = int.MinValue;
                            foreach (long aF in aBlock)
                            {
                                int fu = (int)(aF & 0x1FFFFF), fv = (int)((aF >> 21) & 0x1FFFFF);
                                if (fu < u0) u0 = fu; if (fu > u1) u1 = fu;
                                if (fv < v0) v0 = fv; if (fv > v1) v1 = fv;
                            }
                            int aNu = u1 - u0 + 1, aNv = v1 - v0 + 1;   // 都 ≤ MergeTile ⇒ 乘積 ≤ 4M，int 不溢位
                            if (aMask.Length < aNu * aNv) aMask = new int[aNu * aNv];
                            Array.Clear(aMask, 0, aNu * aNv);
                            // 第一趟（排序＝ v 外 u 內，與舊版掃描同序）：算 AO、填遮罩，四角 AO 不同的當場逐面輸出。
                            foreach (long aF in aBlock)
                            {
                                int fu = (int)(aF & 0x1FFFFF), fv = (int)((aF >> 21) & 0x1FFFFF);
                                aC[aNa] = s; aC[aUa] = fu; aC[aVa] = fv;
                                int x = aC[0] + aMinX, y = aC[1] + aMinY, z = aC[2] + aMinZ;
                                byte aColor = aGrid.Get(x, y, z);
                                int m = (fv - v0) * aNu + (fu - u0);
                                for (int corner = 0; corner < 4; corner++) aAo[corner] = CornerAo(x, y, z, f, corner);
                                aMesh.FaceCount++;
                                bool aUniform = aAo[0] == aAo[1] && aAo[1] == aAo[2] && aAo[2] == aAo[3];
                                if (DebugMergeIgnoreAo) { aMask[m] = aColor | (3 << 8) | (1 << 16); continue; }
                                if (!aUniform) { EmitQuad(x, y, z, f, 1, 1, aColor, aAo); continue; }
                                aMask[m] = aColor | (aAo[0] << 8) | (1 << 16);
                            }
                            // 第二趟：greedy 併最大矩形（先往 u 長、再往 v 長）。
                            for (int iv = 0; iv < aNv; iv++)
                                for (int iu = 0; iu < aNu; iu++)
                                {
                                    int aCode = aMask[iv * aNu + iu];
                                    if (aCode == 0) continue;
                                    int w = 1;
                                    while (iu + w < aNu && aMask[iv * aNu + iu + w] == aCode) w++;
                                    int h = 1;
                                    for (; iv + h < aNv; h++)
                                    {
                                        bool aRow = true;
                                        for (int q = 0; q < w; q++) if (aMask[(iv + h) * aNu + iu + q] != aCode) { aRow = false; break; }
                                        if (!aRow) break;
                                    }
                                    for (int dv = 0; dv < h; dv++)
                                        for (int du = 0; du < w; du++) aMask[(iv + dv) * aNu + iu + du] = 0;
                                    aC[aNa] = s; aC[aUa] = iu + u0; aC[aVa] = iv + v0;
                                    int lvl = (aCode >> 8) & 3;
                                    aLv[0] = aLv[1] = aLv[2] = aLv[3] = lvl;
                                    EmitQuad(aC[0] + aMin[0], aC[1] + aMin[1], aC[2] + aMin[2], f, w, h, (byte)(aCode & 255), aLv);
                                }
                        }
                    }
                }
            }
            aMesh.Vertices = aVerts.ToArray();
            aMesh.Indices = aIdx.ToArray();
            aMesh.VertexCount = aMesh.Vertices.Length / 7;
            aMesh.IndexCount = aMesh.Indices.Length;
            aMesh.Digest = SCP_SculptMesh.DigestOf(aMesh.Vertices, aMesh.Indices);
            return aMesh;
        }

        // ════════════════════════════════════════════════════════════════════════════════════
        // 區塊職責：繩索的管子（TASK-0502）—— 併進 voxel 那份網格的尾巴：同一個 VAO、同一組燈與陰影圖（繩會投影子、也接影子）。
        // 物理意義：管子的形狀由 SCP_SculptRopes.Tube 給（世界座標、外法線、從外面看逆時針的四邊形 —— obj 匯出用同一支）；
        //          這裡只做「世界 → GL（y 取負）」＋ 繞序反過來寫。AO 槽給滿（管子沒有格子鄰居可遮）。
        // 數值影響：框景點加上每個折線點、外框加上半徑 ⇒ 繩伸出作品外也框得到；Digest 重算（含繩）。沒有繩 ⇒ 這支不碰網格。
        // ════════════════════════════════════════════════════════════════════════════════════
        public static void AppendRopes(SCP_SculptMesh ioMesh, IReadOnlyList<SCP_SculptRopeGeom> iRopes)
        {
            if (iRopes.Count == 0) return;
            var aVerts = new List<float>(ioMesh.Vertices);
            var aIdx = new List<uint>(ioMesh.Indices);
            var aCenters = new List<Vector3>(ioMesh.Centers);
            Vector3 aMin = ioMesh.VoxelCount > 0 ? ioMesh.Min : new Vector3(float.MaxValue), aMax = ioMesh.VoxelCount > 0 ? ioMesh.Max : new Vector3(float.MinValue);
            foreach (SCP_SculptRopeGeom r in iRopes)
            {
                SCP_SculptRopes.Tube(r, r.Radius >= 2 ? 16 : 8, out List<double[]> aPos, out List<double[]> aNrm, out List<int[]> aQuads);
                if (aQuads.Count == 0) continue;
                uint aBase = (uint)(aVerts.Count / 7);
                float aPacked = Packed(r.R, r.G, r.B, 255);
                for (int k = 0; k < aPos.Count; k++)
                {
                    aVerts.Add((float)aPos[k][0]); aVerts.Add((float)-aPos[k][1]); aVerts.Add((float)aPos[k][2]);   // 世界 → GL（y 鏡像）
                    aVerts.Add((float)aNrm[k][0]); aVerts.Add((float)-aNrm[k][1]); aVerts.Add((float)aNrm[k][2]);
                    aVerts.Add(aPacked);
                }
                foreach (int[] q in aQuads)
                {
                    // 世界裡從外面看逆時針 ⇒ y 鏡像後順時針 ⇒ 反過來寫
                    aIdx.Add(aBase + (uint)q[0]); aIdx.Add(aBase + (uint)q[2]); aIdx.Add(aBase + (uint)q[1]);
                    aIdx.Add(aBase + (uint)q[0]); aIdx.Add(aBase + (uint)q[3]); aIdx.Add(aBase + (uint)q[2]);
                }
                float rad = (float)r.Radius;
                for (int i = 0; i < r.PointCount; i++)
                {
                    var c = new Vector3((float)r.Points[i * 3], (float)r.Points[i * 3 + 1], (float)r.Points[i * 3 + 2]);
                    aCenters.Add(c);
                    aMin = Vector3.Min(aMin, c - new Vector3(rad)); aMax = Vector3.Max(aMax, c + new Vector3(rad));
                }
                ioMesh.RopeCount++;
                ioMesh.RopeTriangles += aQuads.Count * 2;
            }
            if (ioMesh.RopeCount == 0) return;
            ioMesh.Min = aMin; ioMesh.Max = aMax;
            ioMesh.Centers = aCenters.ToArray();
            ioMesh.Vertices = aVerts.ToArray();
            ioMesh.Indices = aIdx.ToArray();
            ioMesh.VertexCount = ioMesh.Vertices.Length / 7;
            ioMesh.IndexCount = ioMesh.Indices.Length;
            ioMesh.Digest = SCP_SculptMesh.DigestOf(ioMesh.Vertices, ioMesh.Indices);
        }

        /// <summary>世界座標的頂點與索引收集器（另一趟用：帆、帆上的繩、外部模型）。</summary>
        sealed class Collector
        {
            public readonly List<float> Verts = new List<float>();
            public readonly List<uint> Idx = new List<uint>();
            public Vector3 Min = new Vector3(float.MaxValue), Max = new Vector3(float.MinValue);
            public uint Base => (uint)(Verts.Count / 7);

            public void Vertex(double x, double y, double z, double nx, double ny, double nz, float iPacked)
            {
                Verts.Add((float)x); Verts.Add((float)-y); Verts.Add((float)z);   // 世界 → GL（y 鏡像）
                Verts.Add((float)nx); Verts.Add((float)-ny); Verts.Add((float)nz);
                Verts.Add(iPacked);
                var w = new Vector3((float)x, (float)y, (float)z);
                Min = Vector3.Min(Min, w); Max = Vector3.Max(Max, w);
            }

            public void Emit(List<double[]> iPos, List<double[]> iNrm, byte iR, byte iG, byte iB)
            {
                float aPacked = Packed(iR, iG, iB, 255);
                for (int k = 0; k < iPos.Count; k++) Vertex(iPos[k][0], iPos[k][1], iPos[k][2], iNrm[k][0], iNrm[k][1], iNrm[k][2], aPacked);
            }

            public SCP_SculptMesh Finish(SCP_SculptMesh ioMesh)
            {
                if (Idx.Count == 0) return ioMesh;
                ioMesh.Vertices = Verts.ToArray();
                ioMesh.Indices = Idx.ToArray();
                ioMesh.VertexCount = ioMesh.Vertices.Length / 7;
                ioMesh.IndexCount = ioMesh.Indices.Length;
                ioMesh.Min = Min; ioMesh.Max = Max;
                ioMesh.Digest = SCP_SculptMesh.DigestOf(ioMesh.Vertices, ioMesh.Indices);
                return ioMesh;
            }
        }

        // ════════════════════════════════════════════════════════════════════════════════════
        // 區塊職責：帆（TASK-0503）與綁在帆角上的繩 —— 另一趟：觀測頁每幀都會換形狀（持續模擬），⛔ 不併進 voxel 網格。
        // 物理意義：頂點格式跟 voxel 同一種（AO 滿格）⇒ 同一支 shader、同一組燈與陰影圖。帆是雙面的（SCP_SculptSails.TwoSided）；
        //          世界 → GL（y 取負）是鏡像 ⇒ 三角形繞序反過來寫（同 AppendRopes）。
        // ════════════════════════════════════════════════════════════════════════════════════
        public static SCP_SculptMesh Dynamic(IReadOnlyList<SCP_SculptSailGeom>? iSails, IReadOnlyList<SCP_SculptRopeGeom>? iRopes)
        {
            var aMesh = new SCP_SculptMesh();
            var c = new Collector();
            if (iSails != null)
                foreach (SCP_SculptSailGeom s in iSails)
                {
                    SCP_SculptSails.TwoSided(s, out List<double[]> aPos, out List<double[]> aNrm, out List<int[]> aTris);
                    if (aTris.Count == 0) continue;
                    uint aBase = c.Base;
                    c.Emit(aPos, aNrm, s.R, s.G, s.B);
                    // 世界裡從法線那側看逆時針 ⇒ y 鏡像後順時針 ⇒ 反過來寫
                    foreach (int[] t in aTris) { c.Idx.Add(aBase + (uint)t[0]); c.Idx.Add(aBase + (uint)t[2]); c.Idx.Add(aBase + (uint)t[1]); }
                    aMesh.SailCount++;
                    aMesh.SailTriangles += aTris.Count;
                }
            if (iRopes != null)
                foreach (SCP_SculptRopeGeom r in iRopes)
                {
                    SCP_SculptRopes.Tube(r, r.Radius >= 2 ? 16 : 8, out List<double[]> aPos, out List<double[]> aNrm, out List<int[]> aQuads);
                    if (aQuads.Count == 0) continue;
                    uint aBase = c.Base;
                    c.Emit(aPos, aNrm, r.R, r.G, r.B);
                    foreach (int[] q in aQuads)
                    {
                        c.Idx.Add(aBase + (uint)q[0]); c.Idx.Add(aBase + (uint)q[2]); c.Idx.Add(aBase + (uint)q[1]);
                        c.Idx.Add(aBase + (uint)q[0]); c.Idx.Add(aBase + (uint)q[3]); c.Idx.Add(aBase + (uint)q[2]);
                    }
                    aMesh.RopeCount++;
                    aMesh.RopeTriangles += aQuads.Count * 2;
                }
            return c.Finish(aMesh);
        }

        // ════════════════════════════════════════════════════════════════════════════════════
        // 區塊職責：外部模型（TASK-0507）—— 另一趟、靜態（模型不跟著帆動）⇒ 渲染器以 digest 快取，內容沒變不重傳。
        // 物理意義：三角形已在世界座標、從外面看逆時針（SCP_SculptModels 建 geom 時處理過鏡像的繞序）；這裡只換 GL（y 取負）＋反寫繞序。
        //          頂點色 ＝ 模型的材質色（mtl Kd 或 color 覆寫），AO 槽滿格（模型不在 voxel 格子上，沒有鄰格可遮）。
        // ════════════════════════════════════════════════════════════════════════════════════
        public static SCP_SculptMesh Models(IReadOnlyList<SCP_SculptModelGeom>? iModels)
        {
            var aMesh = new SCP_SculptMesh();
            if (iModels == null || iModels.Count == 0) return aMesh;
            var c = new Collector();
            foreach (SCP_SculptModelGeom g in iModels)
            {
                if (g.Triangles.Length == 0) continue;
                uint aBase = c.Base;
                int n = g.PointCount;
                for (int k = 0; k < n; k++)
                    c.Vertex(g.Points[k * 3], g.Points[k * 3 + 1], g.Points[k * 3 + 2], g.Normals[k * 3], g.Normals[k * 3 + 1], g.Normals[k * 3 + 2],
                             Packed(g.Colors[k * 3], g.Colors[k * 3 + 1], g.Colors[k * 3 + 2], 255));
                for (int t = 0; t < g.Triangles.Length; t += 3)
                {
                    c.Idx.Add(aBase + (uint)g.Triangles[t]); c.Idx.Add(aBase + (uint)g.Triangles[t + 2]); c.Idx.Add(aBase + (uint)g.Triangles[t + 1]);
                }
                aMesh.ModelCount++;
                aMesh.ModelTriangles += g.Triangles.Length / 3;
            }
            return c.Finish(aMesh);
        }
    }
}
