// 區塊職責：`.vxk` 封裝（TASK-0518）—— 雕刻作品 → 瀏覽器端 voxelkit.js 吃的那一份；生產端從 ArtGallery/VoxelKit/voxel_export.py 搬進 senate。
// 物理意義：格式照 VoxelKit/README.md、⛔ 一個位元都不改：deflate-raw（'VXK1'＋uint32 LE header 長度＋header（UTF-8 JSON）
//          ＋非空 16³ 區塊座標 nchunks×(uint16 cx,cy,cz)＋區塊內容 nchunks×4096 bytes（索引 x＋16y＋256z；0 ＝ 空））。
//          區塊照 (cx,cy,cz) 字典序（同 numpy unique）。header 的欄位與 Python 那支一一對應；
//          繩／帆／模型照 Python「從 obj 拿」的算法重做，但不經過 obj 檔：obj 寫出的頂點是 4 位小數（F4），這裡先照 F4 取一次再算，
//          ⇒ 中線、外框、網格去重跟 Python 那支同一份輸入。剩下的差別只在浮點的最後一位與 0.01 取整的邊界（對拍給容差，ame 2026-10-12 seq 24096）。
//          ⇒ 繩中線 ＝ 管子每一圈的平均（不是直接拿折線：那會跟 Python 差一個 F4 的量）；模型網格存相對外框最小角取整、0.01 格。
// 數值影響：只讀；寫出之前把封裝解回來、跟格子逐格逐色比、checksum 也比，對不上就不寫（同 Python）。
// 方言：C# 9 / netstandard2.1。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    public static class SCP_SculptVxk
    {
        public const int Chunk = 16;
        public const string Format = "vxk";
        public const int Version = 1;
        static readonly byte[] s_Magic = { (byte)'V', (byte)'X', (byte)'K', (byte)'1' };
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        // ───────────────────────── 格子 ─────────────────────────

        /// <summary>每格雜湊的和（mod 2³²，跟順序無關）—— voxel_export.py／voxelkit.js 的同一式。</summary>
        public static uint Checksum(IEnumerable<(int X, int Y, int Z, int Color)> iVox)
        {
            uint s = 0;
            unchecked
            {
                foreach (var v in iVox)
                    s += ((uint)v.X * 73856093u) ^ ((uint)v.Y * 19349663u) ^ ((uint)v.Z * 83492791u) ^ ((uint)v.Color * 2654435761u);
            }
            return s;
        }

        /// <summary>
        /// 封裝：<paramref name="ioHeader"/> 尾巴補 nchunks（同 Python 的 dict(header, nchunks=…)）。色值要在 1..255、座標 0..1048575（uint16 區塊座標）。
        /// 回傳 deflate-raw 的位元組。
        /// </summary>
        public static byte[] Pack(List<(int X, int Y, int Z, int Color)> iVox, SCP_JsonData ioHeader)
        {
            var aChunks = new Dictionary<long, byte[]>();
            foreach (var v in iVox)
            {
                if (v.Color < 1 || v.Color > 255) throw new InvalidDataException("(" + v.X + "," + v.Y + "," + v.Z + ") 的色值 " + v.Color + " —— .vxk 用 0 當「空」、一格一個位元組");
                if (v.X < 0 || v.Y < 0 || v.Z < 0) throw new InvalidDataException("(" + v.X + "," + v.Y + "," + v.Z + ") 座標是負的");
                int cx = v.X / Chunk, cy = v.Y / Chunk, cz = v.Z / Chunk;
                if (cx > 65535 || cy > 65535 || cz > 65535) throw new InvalidDataException("(" + v.X + "," + v.Y + "," + v.Z + ") 超過 uint16 區塊座標");
                long k = ((long)cx << 32) | ((long)cy << 16) | (long)cz;
                if (!aChunks.TryGetValue(k, out byte[]? d)) { d = new byte[Chunk * Chunk * Chunk]; aChunks[k] = d; }
                d[(v.X - cx * Chunk) + Chunk * (v.Y - cy * Chunk) + Chunk * Chunk * (v.Z - cz * Chunk)] = (byte)v.Color;
            }
            var aKeys = new List<long>(aChunks.Keys);
            aKeys.Sort();                                   // (cx,cy,cz) 字典序 ＝ numpy unique(axis=0) 的順序
            ioHeader.Set("nchunks", aKeys.Count);
            byte[] aHead = new UTF8Encoding(false).GetBytes(SCP_JsonWriter.Write(ioHeader, false));
            using var aRaw = new MemoryStream();
            aRaw.Write(s_Magic, 0, 4);
            int aLen = aHead.Length;
            aRaw.WriteByte((byte)aLen); aRaw.WriteByte((byte)(aLen >> 8)); aRaw.WriteByte((byte)(aLen >> 16)); aRaw.WriteByte((byte)(aLen >> 24));   // uint32 LE
            aRaw.Write(aHead, 0, aHead.Length);
            foreach (long k in aKeys)
            {
                foreach (int c in new[] { (int)(k >> 32), (int)((k >> 16) & 0xFFFF), (int)(k & 0xFFFF) })
                {
                    aRaw.WriteByte((byte)c); aRaw.WriteByte((byte)(c >> 8));
                }
            }
            foreach (long k in aKeys) aRaw.Write(aChunks[k], 0, aChunks[k].Length);
            using var aOut = new MemoryStream();
            using (var z = new DeflateStream(aOut, CompressionLevel.Optimal, true)) { aRaw.Position = 0; aRaw.CopyTo(z); }
            return aOut.ToArray();
        }

        /// <summary>解封（對拍與測試用）：header ＋ 每一格（區塊順序，不排序）。</summary>
        public static (SCP_JsonData Header, List<(int X, int Y, int Z, int Color)> Voxels) Unpack(byte[] iBlob)
        {
            byte[] raw;
            using (var aIn = new MemoryStream(iBlob))
            using (var z = new DeflateStream(aIn, CompressionMode.Decompress))
            using (var aOut = new MemoryStream()) { z.CopyTo(aOut); raw = aOut.ToArray(); }
            if (raw.Length < 8 || raw[0] != 'V' || raw[1] != 'X' || raw[2] != 'K' || raw[3] != '1') throw new InvalidDataException("不是 VXK1");
            int n = raw[4] | (raw[5] << 8) | (raw[6] << 16) | (raw[7] << 24);
            SCP_JsonData h = SCP_JsonParser.Parse(Encoding.UTF8.GetString(raw, 8, n), false);
            int nc = (int)h["nchunks"].AsLong(), p = 8 + n;
            var aKeys = new (int, int, int)[nc];
            for (int i = 0; i < nc; i++, p += 6)
                aKeys[i] = (raw[p] | (raw[p + 1] << 8), raw[p + 2] | (raw[p + 3] << 8), raw[p + 4] | (raw[p + 5] << 8));
            var aVox = new List<(int, int, int, int)>();
            for (int i = 0; i < nc; i++)
                for (int li = 0; li < Chunk * Chunk * Chunk; li++)
                {
                    byte c = raw[p + i * Chunk * Chunk * Chunk + li];
                    if (c == 0) continue;
                    aVox.Add((aKeys[i].Item1 * Chunk + li % Chunk, aKeys[i].Item2 * Chunk + (li / Chunk) % Chunk, aKeys[i].Item3 * Chunk + li / (Chunk * Chunk), c));
                }
            return (h, aVox);
        }

        /// <summary>兩份格子逐格逐色相同（不管順序）。</summary>
        public static bool SameVoxels(List<(int X, int Y, int Z, int Color)> a, List<(int X, int Y, int Z, int Color)> b)
        {
            if (a.Count != b.Count) return false;
            var sa = new List<(int, int, int, int)>(a); var sb = new List<(int, int, int, int)>(b);
            sa.Sort(); sb.Sort();
            for (int i = 0; i < sa.Count; i++) if (sa[i] != sb[i]) return false;
            return true;
        }

        /// <summary>
        /// 檔頭的格子那一段（Python 的 bbox／chunk／voxels／checksum／palette／colors）。<paramref name="ioHeader"/> 照 Python 的欄位順序接上去。
        /// </summary>
        public static void AppendVoxelFields(SCP_JsonData ioHeader, List<(int X, int Y, int Z, int Color)> iVox)
        {
            int[] mn = { int.MaxValue, int.MaxValue, int.MaxValue }, mx = { int.MinValue, int.MinValue, int.MinValue };
            var aColors = new SortedDictionary<int, int>();
            foreach (var v in iVox)
            {
                mn[0] = Math.Min(mn[0], v.X); mn[1] = Math.Min(mn[1], v.Y); mn[2] = Math.Min(mn[2], v.Z);
                mx[0] = Math.Max(mx[0], v.X); mx[1] = Math.Max(mx[1], v.Y); mx[2] = Math.Max(mx[2], v.Z);
                aColors[v.Color] = aColors.TryGetValue(v.Color, out int k) ? k + 1 : 1;
            }
            var aBox = SCP_JsonData.NewArray();
            foreach (int v in mn) aBox.Add(v);
            foreach (int v in mx) aBox.Add(v);
            ioHeader.Set("bbox", aBox);
            ioHeader.Set("chunk", Chunk);
            ioHeader.Set("voxels", iVox.Count);
            ioHeader.Set("checksum", (long)Checksum(iVox));
            ioHeader.Set("palette", "rgb332");
            var aCol = SCP_JsonData.NewObject();
            foreach (var kv in aColors) aCol.Set(kv.Key.ToString(Inv), kv.Value);
            ioHeader.Set("colors", aCol);
        }

        /// <summary>
        /// 材質層（TASK-0616）：每一格的材質 ⇒ 檔頭 <c>materials</c> ＝ { ids：材質 ID（照字典序；索引 1 起，0 ＝ 船的預設材質）、voxels：每種幾格、
        /// index：線性索引的算法、runs：[跟上一段結尾差幾、長度、材質索引] × n }。照線性索引 x＋sx·(y＋sy·z) 排、線性索引相連、同材質的格併成一段（⛔ 不跨空格：跨了讀的那側要掃一大片空的 —— 測試鐵甲船 13 萬格掃 2,500 萬，載入 1 秒）；
        /// 只寫非預設的段。格子那一段一位元都不動（遊戲端與成品壓縮照舊）。<paramref name="iMat"/> 空 ⇒ null（⇒ 不寫這個欄位）。
        /// </summary>
        public static SCP_JsonData? Materials(List<(int X, int Y, int Z, int Color)> iVox, Dictionary<long, string>? iMat, int iSizeX, int iSizeY)
        {
            if (iMat == null || iMat.Count == 0) return null;
            var aIds = new List<string>(new SortedSet<string>(iMat.Values, StringComparer.Ordinal));
            var aIdx = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < aIds.Count; i++) aIdx[aIds[i]] = i + 1;
            var aLin = new List<(long L, int M)>(iVox.Count);
            var aCount = new int[aIds.Count + 1];
            foreach (var v in iVox)
            {
                int m = iMat.TryGetValue(SCP_SculptLinkComposer.MatKey(v.X, v.Y, v.Z), out string? id) ? aIdx[id] : 0;
                aCount[m]++;
                aLin.Add((v.X + (long)iSizeX * (v.Y + (long)iSizeY * v.Z), m));
            }
            aLin.Sort((p, q) => p.L.CompareTo(q.L));
            var aRuns = SCP_JsonData.NewArray();
            long aPrevEnd = 0, s = 0, e = 0;
            int aCur = 0;
            void Emit() { if (aCur == 0) return; aRuns.Add(s - aPrevEnd); aRuns.Add(e - s + 1); aRuns.Add(aCur); aPrevEnd = e + 1; }
            foreach (var p in aLin)
            {
                if (p.M == aCur && (aCur == 0 || p.L == e + 1)) { e = p.L; continue; }
                Emit();
                aCur = p.M; s = e = p.L;
            }
            Emit();
            var o = SCP_JsonData.NewObject();
            var aIdsJ = SCP_JsonData.NewArray();
            foreach (string id in aIds) aIdsJ.Add(id);
            o.Set("ids", aIdsJ);
            var aVoxJ = SCP_JsonData.NewObject();
            for (int i = 0; i < aIds.Count; i++) aVoxJ.Set(aIds[i], aCount[i + 1]);
            o.Set("voxels", aVoxJ);
            o.Set("index", "x+sx*(y+sy*z)");
            o.Set("runs", aRuns);
            return o;
        }

        // ───────────────────────── 繩／帆／模型（照 voxel_export.py 的 read_rig）─────────────────────────

        /// <summary>obj 寫出的那一個數（F4）讀回來 —— Python 那支看到的就是它。</summary>
        public static double F4(double v) => double.Parse(v.ToString("F4", Inv), NumberStyles.Float, Inv);
        /// <summary>numpy.round(x, 2) ＝ rint(x·100)／100（半數取偶）。</summary>
        public static double R2(double v) => Math.Round(v * 100.0, MidpointRounding.ToEven) / 100.0;
        static SCP_JsonData Num(double v) => SCP_JsonData.NewNumber(v);
        static SCP_JsonData Rgb(byte r, byte g, byte b)
        {
            var a = SCP_JsonData.NewArray();
            a.Add(Num(F4(r / 255.0))); a.Add(Num(F4(g / 255.0))); a.Add(Num(F4(b / 255.0)));
            return a;
        }

        /// <summary>繩：管子（同 obj 的 Tube）每一圈的平均 ＝ 中線（0.01）、第一圈到中心的平均距離 ＝ 半徑（0.001）、色（Kd，0.0001）。</summary>
        public static SCP_JsonData Ropes(List<SCP_SculptRopeGeom> iRopes)
        {
            var aOut = SCP_JsonData.NewArray();
            foreach (SCP_SculptRopeGeom r in iRopes)
            {
                SCP_SculptRopes.Tube(r, r.Radius >= 2 ? 16 : 8, out List<double[]> aPos, out _, out List<int[]> aQuads);
                if (aQuads.Count == 0) continue;
                int S = aPos.Count - aQuads.Count;
                if (S < 3 || aPos.Count % S != 0) throw new InvalidDataException("繩 " + r.Path + "：頂點 " + aPos.Count + "、面 " + aQuads.Count + " —— 不是「每圈 S 點、側面四邊形、沒有封口」");
                var aPts = SCP_JsonData.NewArray();
                double aRad = 0;
                for (int ring = 0; ring < aPos.Count / S; ring++)
                {
                    double cx = 0, cy = 0, cz = 0;
                    for (int j = 0; j < S; j++) { double[] p = aPos[ring * S + j]; cx += F4(p[0]); cy += F4(p[1]); cz += F4(p[2]); }
                    cx /= S; cy /= S; cz /= S;
                    if (ring == 0)
                        for (int j = 0; j < S; j++)
                        {
                            double[] p = aPos[j];
                            double dx = F4(p[0]) - cx, dy = F4(p[1]) - cy, dz = F4(p[2]) - cz;
                            aRad += Math.Sqrt(dx * dx + dy * dy + dz * dz) / S;
                        }
                    aPts.Add(Num(R2(cx))); aPts.Add(Num(R2(cy))); aPts.Add(Num(R2(cz)));
                }
                var o = SCP_JsonData.NewObject();
                o.Set("name", r.Path);
                o.Set("color", Rgb(r.R, r.G, r.B));
                o.Set("radius", Num(Math.Round(aRad, 3, MidpointRounding.ToEven)));
                o.Set("pts", aPts);
                aOut.Add(o);
            }
            return aOut;
        }

        /// <summary>
        /// 帆的貼圖（TASK-0530）：帆仍是一整塊布（遊戲拿它跑布料物理），這裡只**多給**畫圖要的東西 ——
        /// <c>uv</c>：跟 <c>verts</c> 一一對應（雙面 ⇒ 兩面同一組）、每頂點 2 個（u ＝ 角 0→角 1、v ＝ 角 0→角 3／三角帆角 0→角 2；圖的左上角 ＝ (0, 0)）、0.0001；
        /// <c>w</c>／<c>h</c>／<c>rgba</c>：RGBA8 列優先（第 0 列是圖的最上面）、base64；<c>alpha_min</c>：低於它的像素不畫；<c>file</c>：作品裡的相對路徑。
        /// </summary>
        static SCP_JsonData Texture(SCP_SculptSailGeom s)
        {
            var aUv = SCP_JsonData.NewArray();
            for (int side = 0; side < 2; side++)
                for (int i = 0; i < s.PointCount; i++) { aUv.Add(Num(F4(s.Uv![i * 2]))); aUv.Add(Num(F4(s.Uv[i * 2 + 1]))); }
            SCP_SculptSailTexture t = s.Texture!;
            var o = SCP_JsonData.NewObject();
            o.Set("file", t.File);
            o.Set("w", SCP_JsonData.NewNumber((long)t.Width));
            o.Set("h", SCP_JsonData.NewNumber((long)t.Height));
            o.Set("alpha_min", SCP_JsonData.NewNumber((long)SCP_SculptSails.TextureAlpha));
            o.Set("rgba", Convert.ToBase64String(t.Rgba));
            o.Set("uv", aUv);
            return o;
        }

        /// <summary>帆：雙面三角網格（同 obj 的 TwoSided）—— 頂點 0.01、三角形照 obj 寫的順序（反過來寫的那一份）、色。</summary>
        public static SCP_JsonData Sails(List<SCP_SculptSailGeom> iSails)
        {
            var aOut = SCP_JsonData.NewArray();
            foreach (SCP_SculptSailGeom s in iSails)
            {
                SCP_SculptSails.TwoSided(s, out List<double[]> aPos, out _, out List<int[]> aTris);
                if (aTris.Count == 0) continue;
                var aV = SCP_JsonData.NewArray();
                foreach (double[] p in aPos) { aV.Add(Num(R2(F4(p[0])))); aV.Add(Num(R2(F4(p[1])))); aV.Add(Num(R2(F4(p[2])))); }
                var aT = SCP_JsonData.NewArray();
                foreach (int[] t in aTris) { aT.Add(t[2]); aT.Add(t[1]); aT.Add(t[0]); }
                var o = SCP_JsonData.NewObject();
                o.Set("name", s.Path);
                o.Set("color", Rgb(s.R, s.G, s.B));
                o.Set("verts", aV);
                o.Set("tris", aT);
                if (s.Uv != null && s.Texture != null) o.Set("texture", Texture(s));   // TASK-0530：沒貼圖的帆位元組不變
                aOut.Add(o);
            }
            return aOut;
        }

        /// <summary>
        /// 模型：每個模型一份網格（頂點相對外框最小角取整、0.01；三角形照 obj 的「同色一組」順序、反過來寫；每個三角形的材質 tmat → mats 的 Kd），
        /// 幾何＋材質完全一樣的只存一份（同 Python 拿整份網格當鍵）。回 (models, meshes)。
        /// </summary>
        public static (SCP_JsonData Models, SCP_JsonData Meshes) Models(List<SCP_SculptModelGeom> iModels)
        {
            var aModels = SCP_JsonData.NewArray();
            var aMeshes = SCP_JsonData.NewArray();
            var aKeys = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (SCP_SculptModelGeom g in iModels)
            {
                if (g.Triangles.Length == 0) continue;
                int n = g.PointCount;
                var P = new double[n * 3];
                for (int i = 0; i < n * 3; i++) P[i] = F4(g.Points[i]);
                double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue }, mx = { double.MinValue, double.MinValue, double.MinValue };
                for (int v = 0; v < n; v++)
                    for (int a = 0; a < 3; a++) { mn[a] = Math.Min(mn[a], P[v * 3 + a]); mx[a] = Math.Max(mx[a], P[v * 3 + a]); }
                double[] org = { Math.Floor(mn[0]), Math.Floor(mn[1]), Math.Floor(mn[2]) };
                // 同色一組（顏色照頂點第一次出現的順序）—— obj 的 usemtl model_n_k
                var aCols = new List<(byte, byte, byte)>();
                var aSeen = new HashSet<(byte, byte, byte)>();
                for (int k = 0; k + 2 < g.Colors.Length; k += 3) { var c = (g.Colors[k], g.Colors[k + 1], g.Colors[k + 2]); if (aSeen.Add(c)) aCols.Add(c); }
                var aTris = new List<int>(); var aTm = new List<int>(); var aMats = new List<(byte, byte, byte)>();
                foreach (var c in aCols)
                    for (int t = 0; t < g.Triangles.Length; t += 3)
                    {
                        int a0 = g.Triangles[t];
                        if ((g.Colors[a0 * 3], g.Colors[a0 * 3 + 1], g.Colors[a0 * 3 + 2]) != c) continue;
                        int mi = aMats.IndexOf(c);
                        if (mi < 0) { mi = aMats.Count; aMats.Add(c); }
                        aTris.Add(g.Triangles[t + 2]); aTris.Add(g.Triangles[t + 1]); aTris.Add(g.Triangles[t]);
                        aTm.Add(mi);
                    }
                var aKey = new StringBuilder();
                var aV = SCP_JsonData.NewArray();
                for (int v = 0; v < n; v++)
                    for (int a = 0; a < 3; a++)
                    {
                        double x = R2(P[v * 3 + a] - org[a]);
                        aV.Add(Num(x)); aKey.Append(x.ToString("R", Inv)).Append(',');
                    }
                aKey.Append('|');
                var aT = SCP_JsonData.NewArray(); foreach (int i in aTris) { aT.Add(i); aKey.Append(i).Append(','); }
                aKey.Append('|');
                var aTmJ = SCP_JsonData.NewArray(); foreach (int i in aTm) { aTmJ.Add(i); aKey.Append(i).Append(','); }
                aKey.Append('|');
                var aMatsJ = SCP_JsonData.NewArray(); foreach (var m in aMats) { aMatsJ.Add(Rgb(m.Item1, m.Item2, m.Item3)); aKey.Append(m.Item1).Append('.').Append(m.Item2).Append('.').Append(m.Item3).Append(','); }
                string k2 = aKey.ToString();
                if (!aKeys.TryGetValue(k2, out int aIdx))
                {
                    aIdx = aMeshes.Count;
                    aKeys[k2] = aIdx;
                    var mesh = SCP_JsonData.NewObject();
                    mesh.Set("verts", aV); mesh.Set("tris", aT); mesh.Set("tmat", aTmJ); mesh.Set("mats", aMatsJ);
                    aMeshes.Add(mesh);
                }
                var o = SCP_JsonData.NewObject();
                o.Set("name", g.Path);
                o.Set("mesh", aIdx);
                var aOrg = SCP_JsonData.NewArray(); foreach (double d in org) aOrg.Add((long)d);
                o.Set("origin", aOrg);
                var aBox = SCP_JsonData.NewArray();
                foreach (double d in mn) aBox.Add(Num(Math.Round(d, 2, MidpointRounding.ToEven)));
                foreach (double d in mx) aBox.Add(Num(Math.Round(d, 2, MidpointRounding.ToEven)));
                o.Set("bbox", aBox);
                aModels.Add(o);
            }
            return (aModels, aMeshes);
        }

        /// <summary>
        /// 關聯外框（Python 的 --parts）：整棵樹（前序，同 sub=links 的順序）每條一筆 path／src／turn／mirror／box（含兩端的格，組裝件座標）。
        /// box ＝ 那件作品現在的框八個角照關聯路徑換過去（跟繩端點同一個公式）—— Python 那支只把 at 一層層加起來（不管上一層的轉向與鏡像），
        /// 兩者只在「上一層有轉向或鏡像」時不一樣。
        /// </summary>
        public static SCP_JsonData Parts(List<SCP_SculptLinkNode> iTree)
        {
            var aIndex = SCP_SculptRopes.IndexTree(iTree);
            var aOut = SCP_JsonData.NewArray();
            void Walk(List<SCP_SculptLinkNode> iNodes)
            {
                foreach (var n in iNodes)
                {
                    double[] mn = { double.MaxValue, double.MaxValue, double.MaxValue }, mx = { double.MinValue, double.MinValue, double.MinValue };
                    for (int k = 0; k < 8; k++)
                    {
                        double[] p = SCP_SculptRopes.ToRoot(n.Path, new double[] { (k & 1) != 0 ? n.Card.SizeX : 0, (k & 2) != 0 ? n.Card.SizeY : 0, (k & 4) != 0 ? n.Card.SizeZ : 0 }, aIndex);
                        for (int i = 0; i < 3; i++) { mn[i] = Math.Min(mn[i], p[i]); mx[i] = Math.Max(mx[i], p[i]); }
                    }
                    var o = SCP_JsonData.NewObject();
                    o.Set("path", n.Path);
                    o.Set("src", n.Card.id);
                    o.Set("turn", n.Link.turn);
                    o.Set("mirror", n.Link.mirror ?? "");
                    var aBox = SCP_JsonData.NewArray();
                    for (int i = 0; i < 3; i++) aBox.Add((long)Math.Round(mn[i]));
                    for (int i = 0; i < 3; i++) aBox.Add((long)Math.Round(mx[i]) - 1);
                    o.Set("box", aBox);
                    aOut.Add(o);
                    Walk(n.Children);
                }
            }
            Walk(iTree);
            return aOut;
        }
    }
}
