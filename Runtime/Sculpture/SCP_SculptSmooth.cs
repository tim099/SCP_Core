// 區塊職責：雕刻預覽的**平滑渲染＋材質反光**（TASK-0645）—— 關聯 meta render=smooth 的格畫成平滑面（surface nets），
//          meta material=<ID> 照雕刻自己的材質表給金屬度、粗糙度（著色器在平滑面上加天空反射與太陽高光）。
// 物理意義：Tim 2026-10-16：「是否可讓 SculptureViewerPage 也支援（平滑渲染）」「然後還有材質（可以參考兩艘鐵甲艦）」；
//          材質表「雕刻自己一份」⇒ Sculpture/materials.json，⛔ 不讀遊戲的 game.json（兩邊各管各的，數值要對齊時手動抄）。
//          平滑面的算法跟 Blockside 15_smooth.js 同一套（那邊是遊戲裡的正本）：
//            · 平滑格自己的方塊面不畫，但照樣擋住旁邊格子的面（⇒ 一般格貼著平滑格的那一面也不畫）；
//            · 每一個「平滑格貼著空氣」的面 ⇒ 四個頂點（那一面的四個格角）；格角往表面拉：
//              ① 周圍 8 格裡、有格與沒格那幾條邊的中點平均（naive surface nets，落在格角 ±0.5）；
//              ② 再跟周圍 3×3×3 同一面的格角平均一次（f ＝ 0.5）、夾回 ±0.5；
//            · 周圍 8 格有一般格（不是平滑的實心）⇒ 釘在格角上 ⇒ 跟方塊的角對齊、不露縫。
//          材質只畫在平滑面（跟遊戲一樣：方塊面不反光）。
// 數值影響：沒有任何格要平滑 ⇒ 這支不被呼叫、方塊網格與著色結果逐位元不變（Surface ＝ null）。
// 頂點格式：平滑網格每頂點 8 個 float 槽 —— 位置 3f、法線 3f、4ub（R,G,B,255）、4ub（金屬度,粗糙度,0,255）；GL 座標（世界 x, −y, z）。
// 方言：C# 9 / netstandard2.1。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using SCP.Core.Canvas;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    /// <summary>雕刻的材質表（<c>Sculpture/materials.json</c>）：ID ⇒ 名字、金屬度、粗糙度。</summary>
    public sealed class SCP_SculptMaterials
    {
        public const string FileName = "materials.json";

        public sealed class Entry
        {
            public string Name = "";
            /// <summary>0..1（0 ＝ 不反光）。</summary>
            public float Metal;
            /// <summary>0..1（越大高光越散）；沒寫 ＝ 0.5。</summary>
            public float Rough = 0.5f;
        }

        public readonly SortedDictionary<string, Entry> List = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        /// <summary>讀的是哪個檔（沒有檔 ⇒ 空字串）。</summary>
        public string Source = "";

        /// <summary>
        /// 讀 <paramref name="iSculptRoot"/>/materials.json。沒有檔 ⇒ 空表（所有材質都不反光，呼叫端照 ID 報「表裡沒有」）。
        /// 壞檔（不是 JSON、schema 不對、值越界、不認得的鍵）⇒ <see cref="InvalidDataException"/>（⛔ 不默默當成沒有表）。
        /// </summary>
        public static SCP_SculptMaterials Load(string iSculptRoot)
        {
            var t = new SCP_SculptMaterials();
            string aPath = Path.Combine(iSculptRoot, FileName);
            if (!File.Exists(aPath)) return t;
            t.Source = aPath;
            SCP_JsonData j;
            try { j = SCP_JsonData.Parse(File.ReadAllText(aPath, Encoding.UTF8)); }
            catch (Exception e) when (e is SCP_JsonParseException || e is SCP_JsonTypeException) { throw new InvalidDataException("材質表不是合法的 JSON（" + aPath + "）：" + e.Message); }
            return Parse(j, aPath);
        }

        /// <summary>驗證並讀一份已解析的材質表（selftest 也走這支）。</summary>
        public static SCP_SculptMaterials Parse(SCP_JsonData j, string iWhere)
        {
            var t = new SCP_SculptMaterials { Source = iWhere };
            string Bad(string s) => "材質表 " + iWhere + "：" + s;
            if (!j.IsObject) throw new InvalidDataException(Bad("最外層要是物件"));
            foreach (string k in j.Keys) if (k != "schema" && k != "list" && k != "note") throw new InvalidDataException(Bad("不認得的鍵 `" + k + "`（只收 schema、list、note）"));
            if (!j["schema"].Exists || j["schema"].Type != SCP_JsonType.Number || j["schema"].AsDouble() != 1) throw new InvalidDataException(Bad("schema 要是 1"));
            SCP_JsonData l = j["list"];
            if (!l.IsObject) throw new InvalidDataException(Bad("list 要是物件（ID ⇒ { name, metal, rough }）"));
            foreach (string id in l.Keys)
            {
                if (id.Length == 0 || id.Length > 32 || !IsId(id)) throw new InvalidDataException(Bad("材質 ID `" + id + "` 只能是小寫英數、底線、連字號（1–32 字）"));
                SCP_JsonData e = l[id];
                if (!e.IsObject) throw new InvalidDataException(Bad("`" + id + "` 要是物件"));
                var aE = new Entry();
                foreach (string k in e.Keys)
                {
                    SCP_JsonData v = e[k];
                    switch (k)
                    {
                        case "name":
                            if (!v.IsString) throw new InvalidDataException(Bad("`" + id + "`.name 要是字串"));
                            aE.Name = v.AsString(); break;
                        case "metal":
                        case "rough":
                            if (v.Type != SCP_JsonType.Number) throw new InvalidDataException(Bad("`" + id + "`." + k + " 要是數字"));
                            double d = v.AsDouble();
                            if (double.IsNaN(d) || d < 0 || d > 1) throw new InvalidDataException(Bad("`" + id + "`." + k + " 要在 0..1：" + d.ToString(CultureInfo.InvariantCulture)));
                            if (k == "metal") aE.Metal = (float)d; else aE.Rough = (float)d;
                            break;
                        case "note":
                            break;
                        default:
                            throw new InvalidDataException(Bad("`" + id + "` 不認得的鍵 `" + k + "`（只收 name、metal、rough、note）"));
                    }
                }
                t.List[id] = aE;
            }
            return t;
        }

        static bool IsId(string s)
        {
            foreach (char c in s) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_' || c == '-')) return false;
            return true;
        }

        public bool TryGet(string iId, out float oMetal, out float oRough)
        {
            if (List.TryGetValue(iId, out Entry? e)) { oMetal = e.Metal; oRough = e.Rough; return true; }
            oMetal = 0; oRough = 0.5f; return false;
        }
    }

    /// <summary>
    /// 一份場景裡哪幾格畫成平滑面、各自的金屬度與粗糙度（合併路徑：組裝器逐格記的 render／material；子模型路徑：整份來源同一個）。
    /// ⚠ 交給渲染器之後不准再改（場景不可變）。
    /// </summary>
    public sealed class SCP_SculptSurface
    {
        /// <summary>整份來源都平滑（子模型路徑）⇒ 不看 <see cref="Smooth"/>，材質 ＝ <see cref="AllMetal"/>／<see cref="AllRough"/>。</summary>
        public bool All;
        public float AllMetal, AllRough = 0.5f;
        /// <summary>要平滑的格（<see cref="Key"/>）。</summary>
        public readonly HashSet<long> Smooth = new HashSet<long>();
        /// <summary>平滑格的材質（沒有 ⇒ 金屬度 0）。</summary>
        public readonly Dictionary<long, (float Metal, float Rough)> Mat = new Dictionary<long, (float Metal, float Rough)>();
        /// <summary>讀數：材質 ID ⇒ 幾格平滑格用它；表裡沒有的 ID。</summary>
        public readonly SortedDictionary<string, int> MaterialCounts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        public readonly SortedSet<string> Unknown = new SortedSet<string>(StringComparer.Ordinal);
        /// <summary>有 material 但不是平滑格的（只畫在平滑面 ⇒ 不反光）。</summary>
        public int MaterialOnVoxel;

        public static long Key(int x, int y, int z) => SCP_SculptLinkComposer.MatKey(x, y, z);

        public bool IsSmooth(int x, int y, int z) => All || Smooth.Contains(Key(x, y, z));

        public void MaterialAt(int x, int y, int z, out float oMetal, out float oRough)
        {
            if (All) { oMetal = AllMetal; oRough = AllRough; return; }
            if (Mat.TryGetValue(Key(x, y, z), out var m)) { oMetal = m.Metal; oRough = m.Rough; return; }
            oMetal = 0; oRough = 0.5f;
        }

        /// <summary>整份來源都平滑（子模型路徑）。</summary>
        public static SCP_SculptSurface AllSmooth(float iMetal, float iRough) => new SCP_SculptSurface { All = true, AllMetal = iMetal, AllRough = iRough };

        ulong? m_Hash;

        /// <summary>網格快取用：內容雜湊（平滑格、材質逐位元）。第一次算完就記住（場景不可變）。</summary>
        public ulong Hash()
        {
            if (m_Hash.HasValue) return m_Hash.Value;
            m_Hash = ComputeHash();
            return m_Hash.Value;
        }

        ulong ComputeHash()
        {
            ulong h = 1469598103934665603UL;
            void Mix(ulong v) { h = (h ^ v) * 1099511628211UL; }
            Mix(All ? 1UL : 0UL); Mix((uint)BitConverter.SingleToInt32Bits(AllMetal)); Mix((uint)BitConverter.SingleToInt32Bits(AllRough));
            var aKeys = new List<long>(Smooth); aKeys.Sort();
            foreach (long k in aKeys)
            {
                Mix((ulong)k);
                if (Mat.TryGetValue(k, out var m)) { Mix((uint)BitConverter.SingleToInt32Bits(m.Metal)); Mix((uint)BitConverter.SingleToInt32Bits(m.Rough)); }
            }
            return h;
        }

        /// <summary>
        /// 合併路徑（CLI view）：組裝器逐格記的 render／material（<see cref="SCP_SculptLinkComposer.LastRender"/>／LastMaterial）⇒ 這一份可見格的平滑面描述。
        /// 沒有任何可見格要平滑 ⇒ null（渲染器整條不走）。材質表沒有的 ID ⇒ 不反光、記進 <see cref="Unknown"/>。
        /// </summary>
        public static SCP_SculptSurface? FromCompose(IReadOnlyList<SCP_SculptVoxel> iVisible, Dictionary<long, string>? iRender, Dictionary<long, string>? iMaterial, SCP_SculptMaterials iMats)
        {
            if (iRender == null || iRender.Count == 0) return null;
            var s = new SCP_SculptSurface();
            foreach (SCP_SculptVoxel v in iVisible)
            {
                long k = Key(v.X, v.Y, v.Z);
                string? aMat = null;
                if (iMaterial != null) iMaterial.TryGetValue(k, out aMat);
                if (!iRender.TryGetValue(k, out string? r) || r != "smooth") { if (aMat != null) s.MaterialOnVoxel++; continue; }
                s.Smooth.Add(k);
                if (aMat == null) continue;
                s.MaterialCounts[aMat] = s.MaterialCounts.TryGetValue(aMat, out int n) ? n + 1 : 1;
                if (iMats.TryGet(aMat, out float m, out float ro)) { if (m > 0) s.Mat[k] = (m, ro); }
                else s.Unknown.Add(aMat);
            }
            return s.Smooth.Count > 0 ? s : null;
        }

        /// <summary>給人讀的一行（CLI 輸出／頁面讀數）。</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append(All ? "整份平滑" : "平滑 " + Smooth.Count.ToString("N0", CultureInfo.InvariantCulture) + " 格");
            if (MaterialCounts.Count > 0)
            {
                var a = new List<string>();
                foreach (var kv in MaterialCounts) a.Add(kv.Key + " " + kv.Value.ToString("N0", CultureInfo.InvariantCulture));
                sb.Append("｜材質 ").Append(string.Join("、", a));
            }
            if (Unknown.Count > 0) sb.Append("｜⚠ 材質表沒有：").Append(string.Join("、", Unknown)).Append("（照不反光畫）");
            if (MaterialOnVoxel > 0) sb.Append("｜").Append(MaterialOnVoxel.ToString("N0", CultureInfo.InvariantCulture)).Append(" 格有材質但不是平滑格（材質只畫在平滑面）");
            return sb.ToString();
        }
    }

    /// <summary>平滑面網格（surface nets；算法見檔頭）。純函式、決定性。</summary>
    public static class SCP_SculptSmoothMesh
    {
        /// <summary>每頂點 8 個 float 槽（見檔頭）。</summary>
        public const int FloatsPerVertex = 8;
        const int Bias = 1 << 20;

        static readonly int[,] s_C8 = { { 0, 0, 0 }, { 1, 0, 0 }, { 0, 1, 0 }, { 1, 1, 0 }, { 0, 0, 1 }, { 1, 0, 1 }, { 0, 1, 1 }, { 1, 1, 1 } };
        static readonly int[,] s_E12 = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
        static readonly int[] s_UAx = { 1, 2, 0 }, s_VAx = { 2, 0, 1 };
        static readonly int[,] s_Corn = { { 0, 0 }, { 1, 0 }, { 1, 1 }, { 0, 1 } };

        struct Corner
        {
            public bool Srf, Pin;
            public float Ox, Oy, Oz, Gx, Gy, Gz;
        }

        static long CKey(int x, int y, int z) => ((long)(x + Bias) << 42) | ((long)(y + Bias) << 21) | (long)(z + Bias);

        static float Packed(int r, int g, int b, int a) => BitConverter.Int32BitsToSingle(unchecked((int)((uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24))));

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

        /// <summary>
        /// <paramref name="iVoxels"/> 是整份場景的格（平滑的與一般的 —— 一般格用來擋面、釘格角）；<paramref name="iSurf"/> 說哪幾格平滑。
        /// 回傳的網格只有平滑面（VoxelCount ＝ 平滑格數；FaceCount ＝ 平滑格的外露面數）。沒有平滑格 ⇒ 空網格。
        /// </summary>
        public static SCP_SculptMesh Build(IReadOnlyList<SCP_SculptVoxel> iVoxels, SCP_SculptSurface iSurf)
        {
            var aMesh = new SCP_SculptMesh();
            var aGrid = new SCP_SculptMeshBuilder.SparseGrid();
            var aSm = new List<long>();
            foreach (SCP_SculptVoxel v in iVoxels)
            {
                if (v.Color == 0) continue;
                if (aGrid.Get(v.X, v.Y, v.Z) == 0 && iSurf.IsSmooth(v.X, v.Y, v.Z)) aSm.Add(CKey(v.X, v.Y, v.Z));
                aGrid.Set(v.X, v.Y, v.Z, v.Color);
            }
            if (aSm.Count == 0) return aMesh;
            aSm.Sort();   // 決定性：與輸入順序無關
            bool Solid(int x, int y, int z) => aGrid.Get(x, y, z) != 0;

            var aInfo = new Dictionary<long, Corner>();
            Corner GetCorner(int X, int Y, int Z)
            {
                long k = CKey(X, Y, Z);
                if (aInfo.TryGetValue(k, out Corner r)) return r;
                Span<bool> occ = stackalloc bool[8];
                bool pin = false; float gx = 0, gy = 0, gz = 0; int n = 0;
                for (int i = 0; i < 8; i++)
                {
                    int vx = X - 1 + s_C8[i, 0], vy = Y - 1 + s_C8[i, 1], vz = Z - 1 + s_C8[i, 2];
                    bool s = Solid(vx, vy, vz);
                    occ[i] = s;
                    if (!s) continue;
                    if (!iSurf.IsSmooth(vx, vy, vz)) pin = true;
                    gx += s_C8[i, 0] - 0.5f; gy += s_C8[i, 1] - 0.5f; gz += s_C8[i, 2] - 0.5f; n++;
                }
                float ox = 0, oy = 0, oz = 0; int ne = 0;
                for (int i = 0; i < 12; i++)
                {
                    int a = s_E12[i, 0], b = s_E12[i, 1];
                    if (occ[a] == occ[b]) continue;
                    ox += (s_C8[a, 0] + s_C8[b, 0]) * 0.5f - 0.5f; oy += (s_C8[a, 1] + s_C8[b, 1]) * 0.5f - 0.5f; oz += (s_C8[a, 2] + s_C8[b, 2]) * 0.5f - 0.5f; ne++;
                }
                r = new Corner { Srf = n > 0 && n < 8, Pin = pin, Gx = gx, Gy = gy, Gz = gz };
                if (ne > 0) { r.Ox = ox / ne; r.Oy = oy / ne; r.Oz = oz / ne; }
                aInfo[k] = r;
                return r;
            }

            // 頂點（格角）：累加顏色與材質，最後取平均
            var aVIdx = new Dictionary<long, int>();
            var aVX = new List<int>(); var aVY = new List<int>(); var aVZ = new List<int>();
            var aAcc = new List<float>();   // 每頂點 6 槽：r, g, b, 面數, 金屬度, 粗糙度
            var aFaces = new List<int>();   // 每面 4 個頂點編號
            int Vert(int X, int Y, int Z, byte iR, byte iG, byte iB, float iM, float iRo)
            {
                long k = CKey(X, Y, Z);
                if (!aVIdx.TryGetValue(k, out int i))
                {
                    i = aVX.Count; aVIdx[k] = i; aVX.Add(X); aVY.Add(Y); aVZ.Add(Z);
                    for (int q = 0; q < 6; q++) aAcc.Add(0);
                }
                int o = i * 6;
                aAcc[o] += iR; aAcc[o + 1] += iG; aAcc[o + 2] += iB; aAcc[o + 3] += 1; aAcc[o + 4] += iM; aAcc[o + 5] += iRo;
                return i;
            }
            Span<int> P = stackalloc int[3];
            Span<int> L = stackalloc int[3];
            Span<int> Q = stackalloc int[4];
            int aMinX = int.MaxValue, aMinY = int.MaxValue, aMinZ = int.MaxValue, aMaxX = int.MinValue, aMaxY = int.MinValue, aMaxZ = int.MinValue;
            var aCenters = new List<Vector3>();
            foreach (long key in aSm)
            {
                int x = (int)(key >> 42) - Bias, y = (int)((key >> 21) & 0x1FFFFF) - Bias, z = (int)(key & 0x1FFFFF) - Bias;
                byte col = aGrid.Get(x, y, z);
                SCP_CanvasPalette.IndexToRgb(col, out byte cr, out byte cg, out byte cb);
                iSurf.MaterialAt(x, y, z, out float mm, out float mr);
                bool aExposed = false;
                for (int ax = 0; ax < 3; ax++)
                    for (int sg = -1; sg <= 1; sg += 2)
                    {
                        P[0] = x; P[1] = y; P[2] = z; P[ax] += sg;
                        if (Solid(P[0], P[1], P[2])) continue;   // 旁邊有格 ⇒ 不是表面
                        aExposed = true;
                        int ua = s_UAx[ax], va = s_VAx[ax];
                        for (int e = 0; e < 4; e++)
                        {
                            int ci = sg > 0 ? e : 3 - e;
                            L[0] = x; L[1] = y; L[2] = z;
                            L[ax] += sg > 0 ? 1 : 0; L[ua] += s_Corn[ci, 0]; L[va] += s_Corn[ci, 1];
                            Q[e] = Vert(L[0], L[1], L[2], cr, cg, cb, mm, mr);
                        }
                        aFaces.Add(Q[0]); aFaces.Add(Q[1]); aFaces.Add(Q[2]); aFaces.Add(Q[3]);
                        aMesh.FaceCount++;
                    }
                if (aExposed) aCenters.Add(new Vector3(x + 0.5f, y + 0.5f, z + 0.5f));
                if (x < aMinX) aMinX = x; if (x > aMaxX) aMaxX = x;
                if (y < aMinY) aMinY = y; if (y > aMaxY) aMaxY = y;
                if (z < aMinZ) aMinZ = z; if (z > aMaxZ) aMaxZ = z;
            }
            aMesh.VoxelCount = aSm.Count;
            aMesh.Centers = aCenters.ToArray();
            aMesh.Min = new Vector3(aMinX, aMinY, aMinZ);
            aMesh.Max = new Vector3(aMaxX + 1, aMaxY + 1, aMaxZ + 1);
            if (aFaces.Count == 0) { aMesh.Digest = SCP_SculptMesh.DigestOf(aMesh.Vertices, aMesh.Indices); return aMesh; }

            // 位置與法線：① 自己的偏移 ② 周圍同一面的格角平均 ⇒ 夾回 ±0.5；釘住的 ＝ 格角本身（15_smooth.js 同式，f ＝ 0.5）
            int nv = aVX.Count;
            var aVerts = new float[nv * FloatsPerVertex];
            for (int i = 0; i < nv; i++)
            {
                int X = aVX[i], Y = aVY[i], Z = aVZ[i];
                Corner me = GetCorner(X, Y, Z);
                float px = X, py = Y, pz = Z, nx = 0, ny = 0, nz = 0, sx = 0, sy = 0, sz = 0; int sn = 0;
                for (int dz = -1; dz <= 1; dz++)
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            Corner o = GetCorner(X + dx, Y + dy, Z + dz);
                            if (!o.Srf) continue;
                            if (o.Gx * me.Gx + o.Gy * me.Gy + o.Gz * me.Gz <= 0) continue;   // 另一面（薄板的背面）⇒ 不混
                            nx -= o.Gx; ny -= o.Gy; nz -= o.Gz;
                            if (o.Pin) { sx += dx; sy += dy; sz += dz; } else { sx += dx + o.Ox; sy += dy + o.Oy; sz += dz + o.Oz; }
                            sn++;
                        }
                if (!me.Pin && sn > 0)
                {
                    const float f = 0.5f;
                    px = X + Clamp((1 - f) * me.Ox + f * sx / sn, -0.5f, 0.5f);
                    py = Y + Clamp((1 - f) * me.Oy + f * sy / sn, -0.5f, 0.5f);
                    pz = Z + Clamp((1 - f) * me.Oz + f * sz / sn, -0.5f, 0.5f);
                }
                float nl = MathF.Sqrt(nx * nx + ny * ny + nz * nz);
                if (nl < 1e-9f) nl = 1;
                int o6 = i * 6, o8 = i * FloatsPerVertex;
                float cnt = aAcc[o6 + 3];
                aVerts[o8] = px; aVerts[o8 + 1] = -py; aVerts[o8 + 2] = pz;            // 世界 → GL（y 鏡像）
                aVerts[o8 + 3] = nx / nl; aVerts[o8 + 4] = -ny / nl; aVerts[o8 + 5] = nz / nl;
                aVerts[o8 + 6] = Packed((int)MathF.Round(aAcc[o6] / cnt), (int)MathF.Round(aAcc[o6 + 1] / cnt), (int)MathF.Round(aAcc[o6 + 2] / cnt), 255);
                aVerts[o8 + 7] = Packed((int)MathF.Round(Clamp(aAcc[o6 + 4] / cnt, 0, 1) * 255), (int)MathF.Round(Clamp(aAcc[o6 + 5] / cnt, 0, 1) * 255), 0, 255);
            }
            // 三角形：世界右手系裡四角是從面外看逆時針（15_smooth.js 的 0,1,2／0,2,3）；y 鏡像讓繞序反轉 ⇒ 反過來寫
            var aIdx = new uint[aFaces.Count / 4 * 6];
            for (int fI = 0, w = 0; fI < aFaces.Count; fI += 4)
            {
                uint q0 = (uint)aFaces[fI], q1 = (uint)aFaces[fI + 1], q2 = (uint)aFaces[fI + 2], q3 = (uint)aFaces[fI + 3];
                aIdx[w++] = q0; aIdx[w++] = q2; aIdx[w++] = q1;
                aIdx[w++] = q0; aIdx[w++] = q3; aIdx[w++] = q2;
            }
            aMesh.Vertices = aVerts;
            aMesh.Indices = aIdx;
            aMesh.VertexCount = nv;
            aMesh.IndexCount = aIdx.Length;
            aMesh.QuadCount = aFaces.Count / 4;
            aMesh.Digest = SCP_SculptMesh.DigestOf(aVerts, aIdx);
            return aMesh;
        }
    }
}
