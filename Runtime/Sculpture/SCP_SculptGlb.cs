// 區塊職責：雕刻匯出 glTF 2.0 二進位（.glb）（TASK-0510）—— 子模型各自成物件、同一份來源只存一份 mesh。
// 物理意義：Tim 2026-10-12「匯出時子模型（關聯、外部模型）各自是獨立的物件、不合併進主模型；多個同樣的子模型共用一份 mesh」；
//          格式 2026-10-16 拍板 glb（obj 沒有「同一份 mesh 套不同變換」的寫法）。
//          場景直接用觀測頁 GPU 分開畫那一份（<see cref="SCP_SculptParts"/>，TASK-0509）：
//          主作品只帶自己的格；每個關聯節點、每個外部模型一個 node，指向來源的 mesh＋自己的矩陣 ⇒ 同一份來源的 node 共用同一個 mesh index。
//          ⇒ 跟合併版（obj／vxk）刻意不同：子模型畫自己的全部格子，貼合處不互相剔除、重疊處兩份都在（同 0509 的「各自完整顯示」）。
// 座標：網格一律寫在來源自己的座標（格 k 佔 [k, k+1]，跟 ToWorld 同一套）；最上面一個根 node 帶
//       「雕刻世界 (x, y, z) → glTF (x, z, y) × 一格幾公尺」—— 跟 obj 匯出同一個換軸（y-up），單位換成公尺。
//       這一換是鏡像（行列式 < 0）⇒ glTF 規定整體矩陣行列式為負時繞序視為順時針 ⇒ 網格照「世界裡從外面看逆時針」寫就對
//       （obj 是在換軸時手動反轉繞序；這裡交給矩陣）。模型實例自己的鏡像也照同一條規則。
// 數值影響：voxel 網格 ＝ 同色、同方向、同一層的露出面合成矩形（同 obj 的 greedy）；每個來源只看自己的格判露出（不看別的實例）。
//          ⚠ 不省略封閉空腔的面（obj 的 sealed）：分開的來源各自判不出整艘船的空腔。
//          材質：voxel 一色一個 `c<index>`（跟 obj 的 mtl 同名）；外部模型用頂點色（COLOR_0）＋白底材質；繩一條一個、帆雙面。
//          顏色是 sRGB ⇒ 寫進 glTF（線性）之前先換成線性值。
// 方言：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Canvas;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    /// <summary>一次 glb 匯出的輸入（呼叫端在鎖內準備好；寫檔不碰作品）。</summary>
    public sealed class SCP_SculptGlbInput
    {
        public string WorkId = "", Title = "";
        /// <summary>一格幾公尺（根 node 的縮放）。</summary>
        public double MetersPerVoxel = 1;
        /// <summary>主作品自己的格（不含關聯）。</summary>
        public List<(int X, int Y, int Z, int Color)> Own = new List<(int, int, int, int)>();
        /// <summary>hide_links=self ⇒ false：主作品的格不出。</summary>
        public bool DrawOwn = true;
        /// <summary>子模型（關聯節點＋外部模型）；null ＝ 沒有。</summary>
        public SCP_SculptParts? Parts;
        /// <summary>每個實例這次出不出（<see cref="SCP_SculptParts.Visibility"/>）；null ＝ 全出。</summary>
        public bool[]? Visible;
        public List<SCP_SculptRopeGeom> Ropes = new List<SCP_SculptRopeGeom>();
        public List<SCP_SculptSailGeom> Sails = new List<SCP_SculptSailGeom>();
    }

    /// <summary>寫出來的讀數（給回報與淨室）。</summary>
    public sealed class SCP_SculptGlbStats
    {
        public int Nodes, Meshes, Instances, Materials, Triangles, Ropes, Sails;
        /// <summary>每個 mesh 被幾個 node 引用（mesh 名 ⇒ 次數）。</summary>
        public SortedDictionary<string, int> Uses = new SortedDictionary<string, int>(StringComparer.Ordinal);
    }

    public static class SCP_SculptGlb
    {
        public const string Format = "glb";

        /// <summary>組出整個 .glb 位元組（決定性：同輸入同 bytes）。沒有任何東西可寫 ⇒ null、<paramref name="oError"/> 說原因。</summary>
        public static byte[]? Build(SCP_SculptGlbInput iIn, out SCP_SculptGlbStats oStats, out string oError)
        {
            oStats = new SCP_SculptGlbStats(); oError = "";
            var w = new Writer(oStats);

            var aRootChildren = new List<int>();
            // ── 主作品自己的格 ──
            if (iIn.DrawOwn && iIn.Own.Count > 0)
            {
                int m = w.VoxelMesh("own:" + iIn.WorkId, iIn.Own);
                if (m >= 0) aRootChildren.Add(w.Node(iIn.WorkId.Length > 0 ? iIn.WorkId : "work", m, null, Extras("own", iIn.WorkId, "")));
            }
            // ── 子模型：每個來源最多一份 mesh（第一次有可見實例才建），每個可見實例一個 node ──
            if (iIn.Parts != null)
            {
                var aMeshOf = new Dictionary<int, int>();
                for (int k = 0; k < iIn.Parts.Instances.Count; k++)
                {
                    if (iIn.Visible != null && (k >= iIn.Visible.Length || !iIn.Visible[k])) continue;
                    SCP_SculptPartInstance aInst = iIn.Parts.Instances[k];
                    if (!aMeshOf.TryGetValue(aInst.Source, out int aMesh))
                    {
                        SCP_SculptPartSource s = iIn.Parts.Sources[aInst.Source];
                        if (s.Voxels != null)
                        {
                            var aVox = new List<(int, int, int, int)>(s.Voxels.Count);
                            foreach (SCP_SculptVoxel v in s.Voxels) aVox.Add((v.X, v.Y, v.Z, v.Color));
                            aMesh = w.VoxelMesh(s.Key, aVox);
                        }
                        else aMesh = s.Model != null ? w.ModelMesh(s.Key, s.Model) : -1;
                        aMeshOf[aInst.Source] = aMesh;
                    }
                    if (aMesh < 0) continue;
                    aRootChildren.Add(w.Node(aInst.Path, aMesh, aInst.ToWorld, Extras(aInst.Kind, iIn.Parts.Sources[aInst.Source].Label, aInst.Path)));
                    oStats.Instances++;
                }
            }
            // ── 繩、帆：世界座標的網格，各一個 node（不共用：每條形狀都不一樣）──
            for (int i = 0; i < iIn.Ropes.Count; i++)
            {
                int m = w.RopeMesh("rope:" + iIn.Ropes[i].Path, iIn.Ropes[i]);
                if (m >= 0) { aRootChildren.Add(w.Node("rope/" + iIn.Ropes[i].Path, m, null, Extras("rope", "", iIn.Ropes[i].Path))); oStats.Ropes++; }
            }
            for (int i = 0; i < iIn.Sails.Count; i++)
            {
                int m = w.SailMesh("sail:" + iIn.Sails[i].Path + "#" + i.ToString(CultureInfo.InvariantCulture), iIn.Sails[i]);
                if (m >= 0) { aRootChildren.Add(w.Node("sail/" + iIn.Sails[i].Path, m, null, Extras("sail", "", iIn.Sails[i].Path))); oStats.Sails++; }
            }
            if (aRootChildren.Count == 0) { oError = "沒有任何東西可匯出（主作品沒有格、子模型都沒出、也沒有繩帆）"; return null; }

            // 根：雕刻世界 → glTF y-up（x, z, y）× 一格幾公尺（行列式 < 0 ⇒ 繞序由 glTF 自己翻）
            double s1 = iIn.MetersPerVoxel > 0 ? iIn.MetersPerVoxel : 1;
            int aRoot = w.Group(iIn.Title.Length > 0 ? iIn.Title : iIn.WorkId, aRootChildren,
                new[] { s1, 0, 0, 0, 0, 0, s1, 0, 0, s1, 0, 0, 0, 0, 0, 1 }, Extras("root", iIn.WorkId, ""));
            byte[] aGlb = w.Finish(aRoot);
            oStats.Nodes = w.NodeCount; oStats.Meshes = w.MeshCount; oStats.Materials = w.MaterialCount;
            return aGlb;
        }

        static SCP_JsonData Extras(string iKind, string iSource, string iPath)
        {
            var o = SCP_JsonData.NewObject();
            o.Set("kind", iKind);
            if (iSource.Length > 0) o.Set("source", iSource);
            if (iPath.Length > 0) o.Set("path", iPath);
            var e = SCP_JsonData.NewObject();
            e.Set("sculpt", o);
            return e;
        }

        // ───────────────────────── 網格（來源自己的座標）─────────────────────────
        static readonly int[][] s_Dir =
        {
            new[] { 1, 0, 0 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 }, new[] { 0, 0, -1 },
        };

        /// <summary>
        /// 一份 voxel 來源 ⇒ 每個顏色一組 (位置, 法線, 索引)：同色同方向同一層的露出面合成矩形（同 obj greedy 的掃法：先往 u 長、再往 v 長）。
        /// 繞序：世界裡從外面看逆時針（叉積對法線）。
        /// </summary>
        internal static SortedDictionary<int, (List<float> Pos, List<float> Nrm, List<uint> Idx)> GreedyQuads(List<(int X, int Y, int Z, int Color)> iVox)
        {
            var aSet = new HashSet<(int, int, int)>();
            foreach (var e in iVox) aSet.Add((e.X, e.Y, e.Z));
            var aMasks = new SortedDictionary<(int Color, int Face, int Layer), HashSet<(int U, int V)>>();
            foreach (var e in iVox)
            {
                int[] p = { e.X, e.Y, e.Z };
                for (int f = 0; f < 6; f++)
                {
                    int[] d = s_Dir[f];
                    if (aSet.Contains((e.X + d[0], e.Y + d[1], e.Z + d[2]))) continue;
                    int a = f / 2, au = a == 0 ? 1 : 0, av = a == 2 ? 1 : 2;
                    var k = (e.Color, f, p[a]);
                    if (!aMasks.TryGetValue(k, out var c)) aMasks[k] = c = new HashSet<(int, int)>();
                    c.Add((p[au], p[av]));
                }
            }
            var aOut = new SortedDictionary<int, (List<float>, List<float>, List<uint>)>();
            foreach (var kv in aMasks)
            {
                int f = kv.Key.Face, a = f / 2, au = a == 0 ? 1 : 0, av = a == 2 ? 1 : 2;
                int[] d = s_Dir[f];
                int aPlane = kv.Key.Layer + (d[a] > 0 ? 1 : 0);
                if (!aOut.TryGetValue(kv.Key.Color, out var g)) aOut[kv.Key.Color] = g = (new List<float>(), new List<float>(), new List<uint>());
                var aCells = kv.Value;
                var aOrder = new List<(int U, int V)>(aCells);
                aOrder.Sort((p, q) => p.V != q.V ? p.V.CompareTo(q.V) : p.U.CompareTo(q.U));
                foreach (var c in aOrder)
                {
                    if (!aCells.Contains(c)) continue;
                    int wd = 1; while (aCells.Contains((c.U + wd, c.V))) wd++;
                    int ht = 1;
                    while (true)
                    {
                        bool aRow = true;
                        for (int i = 0; i < wd && aRow; i++) aRow = aCells.Contains((c.U + i, c.V + ht));
                        if (!aRow) break;
                        ht++;
                    }
                    for (int i = 0; i < wd; i++) for (int j = 0; j < ht; j++) aCells.Remove((c.U + i, c.V + j));
                    int[][] uv = { new[] { c.U, c.V }, new[] { c.U + wd, c.V }, new[] { c.U + wd, c.V + ht }, new[] { c.U, c.V + ht } };
                    var q4 = new int[4][];
                    for (int k = 0; k < 4; k++) { var pt = new int[3]; pt[a] = aPlane; pt[au] = uv[k][0]; pt[av] = uv[k][1]; q4[k] = pt; }
                    // 叉積跟法線同向 ⇒ 從外面看逆時針；反了就倒過來
                    long ux = q4[1][0] - q4[0][0], uy = q4[1][1] - q4[0][1], uz = q4[1][2] - q4[0][2];
                    long vx = q4[2][0] - q4[0][0], vy = q4[2][1] - q4[0][1], vz = q4[2][2] - q4[0][2];
                    long cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                    if (cx * d[0] + cy * d[1] + cz * d[2] < 0) Array.Reverse(q4);
                    uint b = (uint)(g.Item1.Count / 3);
                    foreach (int[] pt in q4)
                    {
                        g.Item1.Add(pt[0]); g.Item1.Add(pt[1]); g.Item1.Add(pt[2]);
                        g.Item2.Add(d[0]); g.Item2.Add(d[1]); g.Item2.Add(d[2]);
                    }
                    g.Item3.Add(b); g.Item3.Add(b + 1); g.Item3.Add(b + 2);
                    g.Item3.Add(b); g.Item3.Add(b + 2); g.Item3.Add(b + 3);
                }
            }
            return aOut;
        }

        /// <summary>sRGB 0..255 ⇒ 線性 0..1（glTF 的 baseColorFactor／COLOR_0 是線性）。</summary>
        internal static double Linear(byte c)
        {
            double v = c / 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }

        static double R6(double v) => Math.Round(v, 6, MidpointRounding.AwayFromZero);

        // ───────────────────────── glTF 組裝 ─────────────────────────
        sealed class Writer
        {
            readonly SCP_SculptGlbStats m_Stats;
            readonly MemoryStream m_Bin = new MemoryStream();
            readonly SCP_JsonData m_Views = SCP_JsonData.NewArray(), m_Accessors = SCP_JsonData.NewArray(), m_Meshes = SCP_JsonData.NewArray(),
                                  m_Nodes = SCP_JsonData.NewArray(), m_Materials = SCP_JsonData.NewArray();
            readonly Dictionary<string, int> m_MatIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            readonly List<string> m_MeshNames = new List<string>();

            public Writer(SCP_SculptGlbStats iStats) { m_Stats = iStats; }
            public int NodeCount => m_Nodes.Count;
            public int MeshCount => m_Meshes.Count;
            public int MaterialCount => m_Materials.Count;

            int View(byte[] iBytes, int iTarget)
            {
                while (m_Bin.Length % 4 != 0) m_Bin.WriteByte(0);
                long aOff = m_Bin.Length;
                m_Bin.Write(iBytes, 0, iBytes.Length);
                var v = SCP_JsonData.NewObject();
                v.Set("buffer", 0); v.Set("byteOffset", aOff); v.Set("byteLength", iBytes.Length); v.Set("target", iTarget);
                m_Views.Add(v);
                return m_Views.Count - 1;
            }

            int FloatAccessor(List<float> iData, int iComps, bool iMinMax)
            {
                var b = new byte[iData.Count * 4];
                for (int i = 0; i < iData.Count; i++) Array.Copy(BitConverter.GetBytes(iData[i]), 0, b, i * 4, 4);
                if (!BitConverter.IsLittleEndian) for (int i = 0; i < b.Length; i += 4) Array.Reverse(b, i, 4);
                var a = SCP_JsonData.NewObject();
                a.Set("bufferView", View(b, 34962)); a.Set("componentType", 5126); a.Set("count", iData.Count / iComps);
                a.Set("type", iComps == 3 ? "VEC3" : iComps == 4 ? "VEC4" : "SCALAR");
                if (iMinMax)
                {
                    var mn = SCP_JsonData.NewArray(); var mx = SCP_JsonData.NewArray();
                    for (int c = 0; c < iComps; c++)
                    {
                        float lo = float.MaxValue, hi = float.MinValue;
                        for (int i = c; i < iData.Count; i += iComps) { lo = Math.Min(lo, iData[i]); hi = Math.Max(hi, iData[i]); }
                        mn.Add(R6(lo)); mx.Add(R6(hi));
                    }
                    a.Set("min", mn); a.Set("max", mx);
                }
                m_Accessors.Add(a);
                return m_Accessors.Count - 1;
            }

            int IndexAccessor(List<uint> iIdx)
            {
                var b = new byte[iIdx.Count * 4];
                for (int i = 0; i < iIdx.Count; i++) Array.Copy(BitConverter.GetBytes(iIdx[i]), 0, b, i * 4, 4);
                if (!BitConverter.IsLittleEndian) for (int i = 0; i < b.Length; i += 4) Array.Reverse(b, i, 4);
                var a = SCP_JsonData.NewObject();
                a.Set("bufferView", View(b, 34963)); a.Set("componentType", 5125); a.Set("count", iIdx.Count); a.Set("type", "SCALAR");
                m_Accessors.Add(a);
                return m_Accessors.Count - 1;
            }

            int Material(string iName, double r, double g, double b, bool iDoubleSided)
            {
                if (m_MatIndex.TryGetValue(iName, out int i)) return i;
                var pbr = SCP_JsonData.NewObject();
                var col = SCP_JsonData.NewArray(); col.Add(R6(r)); col.Add(R6(g)); col.Add(R6(b)); col.Add(1.0);
                pbr.Set("baseColorFactor", col); pbr.Set("metallicFactor", 0.0); pbr.Set("roughnessFactor", 1.0);
                var m = SCP_JsonData.NewObject();
                m.Set("name", iName); m.Set("pbrMetallicRoughness", pbr);
                if (iDoubleSided) m.Set("doubleSided", true);
                m_Materials.Add(m);
                m_MatIndex[iName] = i = m_Materials.Count - 1;
                return i;
            }

            int PaletteMaterial(int iColor)
            {
                SCP_CanvasPalette.IndexToRgb(iColor < 0 || iColor > 255 ? 0 : iColor, out byte r, out byte g, out byte b);
                return Material("c" + iColor.ToString(CultureInfo.InvariantCulture), Linear(r), Linear(g), Linear(b), false);
            }

            int Mesh(string iName, SCP_JsonData iPrims, int iTris)
            {
                var m = SCP_JsonData.NewObject();
                m.Set("name", iName); m.Set("primitives", iPrims);
                m_Meshes.Add(m); m_MeshNames.Add(iName);
                m_Stats.Triangles += iTris;
                return m_Meshes.Count - 1;
            }

            static SCP_JsonData Prim(int iPos, int iNrm, int iIdx, int iMat, int iColor = -1)
            {
                var at = SCP_JsonData.NewObject();
                at.Set("POSITION", iPos); at.Set("NORMAL", iNrm);
                if (iColor >= 0) at.Set("COLOR_0", iColor);
                var p = SCP_JsonData.NewObject();
                p.Set("attributes", at); p.Set("indices", iIdx); p.Set("material", iMat); p.Set("mode", 4);
                return p;
            }

            public int VoxelMesh(string iName, List<(int X, int Y, int Z, int Color)> iVox)
            {
                var aGroups = GreedyQuads(iVox);
                if (aGroups.Count == 0) return -1;
                var aPrims = SCP_JsonData.NewArray();
                int aTris = 0;
                foreach (var kv in aGroups)
                {
                    aPrims.Add(Prim(FloatAccessor(kv.Value.Pos, 3, true), FloatAccessor(kv.Value.Nrm, 3, false), IndexAccessor(kv.Value.Idx), PaletteMaterial(kv.Key)));
                    aTris += kv.Value.Idx.Count / 3;
                }
                return Mesh(iName, aPrims, aTris);
            }

            public int ModelMesh(string iName, SCP_SculptModelGeom g)
            {
                if (g.Triangles.Length == 0 || g.PointCount == 0) return -1;
                var aPos = new List<float>(g.Points.Length); var aNrm = new List<float>(g.Normals.Length); var aCol = new List<float>(g.PointCount * 4);
                foreach (double v in g.Points) aPos.Add((float)v);
                for (int i = 0; i < g.PointCount * 3; i++) aNrm.Add(i < g.Normals.Length ? (float)g.Normals[i] : 0f);
                for (int i = 0; i < g.PointCount; i++)
                {
                    byte r = i * 3 + 2 < g.Colors.Length ? g.Colors[i * 3] : (byte)255, gg = i * 3 + 2 < g.Colors.Length ? g.Colors[i * 3 + 1] : (byte)255, b = i * 3 + 2 < g.Colors.Length ? g.Colors[i * 3 + 2] : (byte)255;
                    aCol.Add((float)Linear(r)); aCol.Add((float)Linear(gg)); aCol.Add((float)Linear(b)); aCol.Add(1f);
                }
                var aIdx = new List<uint>(g.Triangles.Length);
                foreach (int t in g.Triangles) aIdx.Add((uint)t);
                var aPrims = SCP_JsonData.NewArray();
                aPrims.Add(Prim(FloatAccessor(aPos, 3, true), FloatAccessor(aNrm, 3, false), IndexAccessor(aIdx), Material("vertex-color", 1, 1, 1, false), FloatAccessor(aCol, 4, false)));
                return Mesh(iName, aPrims, aIdx.Count / 3);
            }

            public int RopeMesh(string iName, SCP_SculptRopeGeom r)
            {
                SCP_SculptRopes.Tube(r, r.Radius >= 2 ? 16 : 8, out List<double[]> aP, out List<double[]> aN, out List<int[]> aQ);
                if (aQ.Count == 0) return -1;
                var aPos = new List<float>(); var aNrm = new List<float>(); var aIdx = new List<uint>();
                foreach (double[] p in aP) { aPos.Add((float)p[0]); aPos.Add((float)p[1]); aPos.Add((float)p[2]); }
                foreach (double[] n in aN) { aNrm.Add((float)n[0]); aNrm.Add((float)n[1]); aNrm.Add((float)n[2]); }
                foreach (int[] q in aQ) { aIdx.Add((uint)q[0]); aIdx.Add((uint)q[1]); aIdx.Add((uint)q[2]); aIdx.Add((uint)q[0]); aIdx.Add((uint)q[2]); aIdx.Add((uint)q[3]); }
                var aPrims = SCP_JsonData.NewArray();
                aPrims.Add(Prim(FloatAccessor(aPos, 3, true), FloatAccessor(aNrm, 3, false), IndexAccessor(aIdx),
                    Material("rope_" + r.R + "_" + r.G + "_" + r.B, Linear(r.R), Linear(r.G), Linear(r.B), false)));
                return Mesh(iName, aPrims, aIdx.Count / 3);
            }

            public int SailMesh(string iName, SCP_SculptSailGeom s)
            {
                SCP_SculptSails.TwoSided(s, out List<double[]> aP, out List<double[]> aN, out List<int[]> aT);
                if (aT.Count == 0) return -1;
                var aPos = new List<float>(); var aNrm = new List<float>(); var aIdx = new List<uint>();
                foreach (double[] p in aP) { aPos.Add((float)p[0]); aPos.Add((float)p[1]); aPos.Add((float)p[2]); }
                foreach (double[] n in aN) { aNrm.Add((float)n[0]); aNrm.Add((float)n[1]); aNrm.Add((float)n[2]); }
                foreach (int[] t in aT) { aIdx.Add((uint)t[0]); aIdx.Add((uint)t[1]); aIdx.Add((uint)t[2]); }
                var aPrims = SCP_JsonData.NewArray();
                aPrims.Add(Prim(FloatAccessor(aPos, 3, true), FloatAccessor(aNrm, 3, false), IndexAccessor(aIdx),
                    Material("sail_" + s.R + "_" + s.G + "_" + s.B, Linear(s.R), Linear(s.G), Linear(s.B), false)));
                return Mesh(iName, aPrims, aIdx.Count / 3);
            }

            public int Node(string iName, int iMesh, double[]? iToWorld3x4, SCP_JsonData iExtras)
            {
                var n = SCP_JsonData.NewObject();
                n.Set("name", iName); n.Set("mesh", iMesh);
                if (iToWorld3x4 != null)
                {
                    double[] a = iToWorld3x4;   // 3×4 列優先 ⇒ glTF 4×4 行優先
                    var m = SCP_JsonData.NewArray();
                    foreach (double v in new[] { a[0], a[4], a[8], 0, a[1], a[5], a[9], 0, a[2], a[6], a[10], 0, a[3], a[7], a[11], 1 }) m.Add(R6(v));
                    n.Set("matrix", m);
                }
                n.Set("extras", iExtras);
                m_Nodes.Add(n);
                string aMeshName = m_MeshNames[iMesh];
                m_Stats.Uses[aMeshName] = m_Stats.Uses.TryGetValue(aMeshName, out int u) ? u + 1 : 1;
                return m_Nodes.Count - 1;
            }

            public int Group(string iName, List<int> iChildren, double[] iMatrix4ColMajor, SCP_JsonData iExtras)
            {
                var n = SCP_JsonData.NewObject();
                n.Set("name", iName);
                var ch = SCP_JsonData.NewArray(); foreach (int c in iChildren) ch.Add(c);
                n.Set("children", ch);
                var m = SCP_JsonData.NewArray(); foreach (double v in iMatrix4ColMajor) m.Add(R6(v));
                n.Set("matrix", m);
                n.Set("extras", iExtras);
                m_Nodes.Add(n);
                return m_Nodes.Count - 1;
            }

            public byte[] Finish(int iRoot)
            {
                while (m_Bin.Length % 4 != 0) m_Bin.WriteByte(0);
                var root = SCP_JsonData.NewObject();
                var asset = SCP_JsonData.NewObject(); asset.Set("version", "2.0"); asset.Set("generator", "Senate sculpture export (TASK-0510)");
                root.Set("asset", asset);
                root.Set("scene", 0);
                var scene = SCP_JsonData.NewObject(); var sn = SCP_JsonData.NewArray(); sn.Add(iRoot); scene.Set("nodes", sn);
                var scenes = SCP_JsonData.NewArray(); scenes.Add(scene); root.Set("scenes", scenes);
                root.Set("nodes", m_Nodes); root.Set("meshes", m_Meshes); root.Set("materials", m_Materials);
                root.Set("accessors", m_Accessors); root.Set("bufferViews", m_Views);
                var buf = SCP_JsonData.NewObject(); buf.Set("byteLength", m_Bin.Length);
                var bufs = SCP_JsonData.NewArray(); bufs.Add(buf); root.Set("buffers", bufs);

                byte[] aJson = new UTF8Encoding(false).GetBytes(SCP_JsonWriter.Write(root, false));
                int aJsonPad = (4 - aJson.Length % 4) % 4;
                byte[] aBin = m_Bin.ToArray();
                int aTotal = 12 + 8 + aJson.Length + aJsonPad + 8 + aBin.Length;
                var o = new MemoryStream(aTotal);
                void U32(uint v) { var b = BitConverter.GetBytes(v); if (!BitConverter.IsLittleEndian) Array.Reverse(b); o.Write(b, 0, 4); }
                U32(0x46546C67); U32(2); U32((uint)aTotal);                                  // "glTF", version 2
                U32((uint)(aJson.Length + aJsonPad)); U32(0x4E4F534A);                         // JSON
                o.Write(aJson, 0, aJson.Length); for (int i = 0; i < aJsonPad; i++) o.WriteByte(0x20);
                U32((uint)aBin.Length); U32(0x004E4942);                                        // BIN
                o.Write(aBin, 0, aBin.Length);
                return o.ToArray();
            }
        }
    }
}
