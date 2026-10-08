// 區塊職責：球面的 **CPU 渲染** —— 兩種投影：
//          ortho ＝ 正交投影看一個半球（每個螢幕像素解析求交 → 方向 → 格子 → 取色）；
//          equirect ＝ 整顆球攤成世界地圖（經度 −180→180 對橫軸、緯度 90→−90 對直軸，寬：高＝2：1）。
// 物理意義：沒有 mesh、沒有貼圖取樣；格子的方塊邊緣是「最近鄰」自然留下的。正交投影的光照只是看得出球面的 Lambert，
//          世界地圖不打光（地圖要讀顏色，不要讀陰影）。疊圖（經緯線、施工區框線、面接縫）是讀數，不寫回格子。
// 數值影響：純函式 → RGBA8（由上到下）。決定性：同樣的格子＋參數 ⇒ 同樣的 bytes。
using System;
using SCP.Core.Canvas;

namespace SCP.Core.Globe
{
    public sealed class SCP_GlobeView
    {
        public const string ProjOrtho = "ortho", ProjEquirect = "equirect";
        /// <summary>ortho（看一個半球）｜equirect（世界地圖；不吃 CenterLat／CenterLon／Zoom）。</summary>
        public string Projection = ProjOrtho;
        public double CenterLat = 23.7, CenterLon = 121.0;
        /// <summary>1 ＝ 整個半球剛好塞滿；2 ＝ 放大兩倍。</summary>
        public double Zoom = 1;
        /// <summary>ortho：正方形邊長；equirect：寬（高＝寬／2）。</summary>
        public int Size = 720;
        public int Width => Size;
        public int Height => Projection == ProjEquirect ? Math.Max(1, Size / 2) : Size;
        /// <summary>經緯線間隔（度）；0 ＝ 不畫。</summary>
        public double Graticule = 10;
        public bool Seams;
        /// <summary>要疊框線的施工區（空＝不疊）。</summary>
        public System.Collections.Generic.List<SCP_GlobeZone> Zones = new System.Collections.Generic.List<SCP_GlobeZone>();
    }

    public static class SCP_GlobeRender
    {
        public const int MaxOrthoSize = 4096;
        /// <summary>世界地圖最寬 8192：赤道一圈是 4N 格（N=2048 ⇒ 8192），再寬只是把同一格放大。</summary>
        public const int MaxEquirectWidth = 8192;

        public static byte[] RenderRgba(SCP_GlobeState s, SCP_GlobeView v)
        {
            if (v.Projection == SCP_GlobeView.ProjEquirect) return RenderEquirect(s, v);
            if (v.Projection != SCP_GlobeView.ProjOrtho) throw new SCP_GlobeException("projection 要是 ortho 或 equirect：" + v.Projection);
            int W = v.Size, H = v.Size;
            if (W < 16 || W > MaxOrthoSize) throw new SCP_GlobeException($"size 要在 16..{MaxOrthoSize}：" + W);
            if (!(v.Zoom > 0) || v.Zoom > 1000) throw new SCP_GlobeException("zoom 要在 (0, 1000]：" + v.Zoom);
            var rgba = new byte[W * H * 4];
            var face = new sbyte[W * H];
            SCP_GlobeGrid g = s.Grid;

            SCP_GlobeGrid.LatLonToDir(v.CenterLat, v.CenterLon, out double cx, out double cy, out double cz);
            double lo = v.CenterLon * Math.PI / 180, la = v.CenterLat * Math.PI / 180;
            double rx = -Math.Sin(lo), ry = Math.Cos(lo), rz = 0;                                  // 東
            double ux = -Math.Sin(la) * Math.Cos(lo), uy = -Math.Sin(la) * Math.Sin(lo), uz = Math.Cos(la);   // 北
            // 光從左上前方來
            double lx = 0.8 * cx - 0.35 * rx + 0.5 * ux, ly = 0.8 * cy - 0.35 * ry + 0.5 * uy, lz = 0.8 * cz - 0.35 * rz + 0.5 * uz;
            double ll = Math.Sqrt(lx * lx + ly * ly + lz * lz); lx /= ll; ly /= ll; lz /= ll;

            int aBase = s.BaseRgb;
            // 經緯線容許寬度：約 1 個螢幕像素對應的角度（度）—— 預覽縮小顯示時細線才不會斷
            double aTolDeg = 180 / Math.PI * (2.0 / (W * v.Zoom)) * 1.0;

            for (int py = 0; py < H; py++)
                for (int px = 0; px < W; px++)
                {
                    int o = (py * W + px) * 4;
                    double sx = (2.0 * (px + 0.5) / W - 1) / v.Zoom, sy = (1 - 2.0 * (py + 0.5) / H) / v.Zoom;
                    double r2 = sx * sx + sy * sy;
                    if (r2 > 1) { rgba[o] = 12; rgba[o + 1] = 14; rgba[o + 2] = 22; rgba[o + 3] = 255; face[py * W + px] = -1; continue; }
                    double sz = Math.Sqrt(1 - r2);
                    double dx = cx * sz + rx * sx + ux * sy, dy = cy * sz + ry * sx + uy * sy, dz = cz * sz + rz * sx + uz * sy;
                    int idx = g.DirToCell(dx, dy, dz);
                    face[py * W + px] = (sbyte)(idx / (g.N * g.N));
                    int c = s.Cells.Get(idx);
                    if (c == SCP_GlobeCells.Empty) c = aBase;
                    double shade = 0.35 + 0.65 * Math.Max(0, dx * lx + dy * ly + dz * lz);
                    double R = ((c >> 16) & 255) * shade, G = ((c >> 8) & 255) * shade, B = (c & 255) * shade;
                    if (v.Graticule > 0 || v.Zones.Count > 0)
                    {
                        SCP_GlobeGrid.DirToLatLon(dx, dy, dz, out double aLat, out double aLon);
                        double tol = aTolDeg / Math.Max(sz, 0.15);
                        if (ZoneEdge(v.Zones, aLat, aLon, tol * 2, out int zr, out int zg, out int zb)) { R = zr; G = zg; B = zb; goto Done; }
                        if (v.Graticule <= 0) goto Done;
                        double eLat = Math.Abs(aLat - Math.Round(aLat / v.Graticule) * v.Graticule);
                        double eLon = Math.Abs(aLon - Math.Round(aLon / v.Graticule) * v.Graticule) * Math.Cos(aLat * Math.PI / 180);
                        if (eLat < tol || (eLon < tol && Math.Abs(aLat) < 89)) { R = R * 0.55 + 255 * 0.45; G = G * 0.55 + 255 * 0.45; B = B * 0.55 + 255 * 0.45; }
                    }
                    Done:
                    rgba[o] = Clamp(R); rgba[o + 1] = Clamp(G); rgba[o + 2] = Clamp(B); rgba[o + 3] = 255;
                }

            if (v.Seams) DrawSeams(rgba, face, W, H);
            return rgba;
        }

        /// <summary>
        /// 世界地圖（等距圓柱）：像素中心 ⇒ 經緯度 ⇒ 格子 ⇒ 原色（不打光）。
        /// 經緯線與施工區框線的容許寬度用「一個像素對應幾度」量，所以任何寬度下都是約一像素寬的線。
        /// </summary>
        static byte[] RenderEquirect(SCP_GlobeState s, SCP_GlobeView v)
        {
            int W = v.Width, H = v.Height;
            if (W < 16 || W > MaxEquirectWidth) throw new SCP_GlobeException($"世界地圖寬要在 16..{MaxEquirectWidth}：" + W);
            var rgba = new byte[W * H * 4];
            var face = new sbyte[W * H];
            SCP_GlobeGrid g = s.Grid;
            int aBase = s.BaseRgb;
            double aDegX = 360.0 / W, aDegY = 180.0 / H;
            for (int py = 0; py < H; py++)
            {
                double aLat = 90 - (py + 0.5) * aDegY;
                for (int px = 0; px < W; px++)
                {
                    double aLon = -180 + (px + 0.5) * aDegX;
                    SCP_GlobeGrid.LatLonToDir(aLat, aLon, out double dx, out double dy, out double dz);
                    int idx = g.DirToCell(dx, dy, dz);
                    face[py * W + px] = (sbyte)(idx / (g.N * g.N));
                    int c = s.Cells.Get(idx);
                    if (c == SCP_GlobeCells.Empty) c = aBase;
                    double R = (c >> 16) & 255, G = (c >> 8) & 255, B = c & 255;
                    // 施工區框線：ZoneEdge 的經度差會乘 cos(lat)，這裡要的是「一像素」⇒ 先除回去
                    double aCos = Math.Max(Math.Cos(aLat * Math.PI / 180), 0.01);
                    if (v.Zones.Count > 0 && ZoneEdge(v.Zones, aLat, aLon, Math.Max(aDegY, aDegX * aCos) * 1.5, out int zr, out int zg, out int zb))
                    { R = zr; G = zg; B = zb; }
                    else if (v.Graticule > 0)
                    {
                        double eLat = Math.Abs(aLat - Math.Round(aLat / v.Graticule) * v.Graticule);
                        double eLon = Math.Abs(aLon - Math.Round(aLon / v.Graticule) * v.Graticule);
                        if (eLat < aDegY * 0.5 || eLon < aDegX * 0.5) { R = R * 0.55 + 255 * 0.45; G = G * 0.55 + 255 * 0.45; B = B * 0.55 + 255 * 0.45; }
                    }
                    int o = (py * W + px) * 4;
                    rgba[o] = Clamp(R); rgba[o + 1] = Clamp(G); rgba[o + 2] = Clamp(B); rgba[o + 3] = 255;
                }
            }
            if (v.Seams) DrawSeams(rgba, face, W, H);
            return rgba;
        }

        static void DrawSeams(byte[] rgba, sbyte[] face, int W, int H)
        {
            for (int py = 0; py < H; py++)
                for (int px = 0; px < W; px++)
                {
                    int f = face[py * W + px];
                    if (f < 0) continue;
                    bool aEdge = (px + 1 < W && face[py * W + px + 1] >= 0 && face[py * W + px + 1] != f)
                                 || (py + 1 < H && face[(py + 1) * W + px] >= 0 && face[(py + 1) * W + px] != f);
                    if (!aEdge) continue;
                    int o = (py * W + px) * 4;
                    rgba[o] = 255; rgba[o + 1] = 80; rgba[o + 2] = 200;
                }
        }

        public static byte[] RenderPng(SCP_GlobeState s, SCP_GlobeView v)
            => SCP_CanvasPng.EncodeRgbaRows(RenderRgba(s, v), v.Width, v.Height);

        /// <summary>這個經緯度是不是落在某個施工區的框線上（框內、離任一邊 &lt; tol 度）。顏色看狀態。</summary>
        static bool ZoneEdge(System.Collections.Generic.List<SCP_GlobeZone> iZones, double iLat, double iLon, double iTol, out int r, out int g, out int b)
        {
            r = g = b = 0;
            double c = Math.Max(Math.Cos(iLat * Math.PI / 180), 0.01);
            foreach (SCP_GlobeZone z in iZones)
            {
                if (!z.Contains(iLat, iLon)) continue;
                double dW = LonGap(iLon, z.West) * c, dE = LonGap(z.East, iLon) * c;
                if (iLat - z.South < iTol || z.North - iLat < iTol || dW < iTol || dE < iTol)
                {
                    if (z.Status == "done") { r = 200; g = 200; b = 200; }
                    else if (z.Status == "paused") { r = 255; g = 150; b = 40; }
                    else { r = 255; g = 230; b = 0; }
                    return true;
                }
            }
            return false;
        }

        /// <summary>從 a 往東走到 b 的經度差（0..360）。</summary>
        static double LonGap(double a, double b)
        {
            double d = (a - b) % 360;
            return d < 0 ? d + 360 : d;
        }

        static byte Clamp(double d) => d <= 0 ? (byte)0 : d >= 255 ? (byte)255 : (byte)(d + 0.5);
    }
}
