// 區塊職責：球面的 **CPU 預覽渲染** —— 正交投影看一個半球：每個螢幕像素解析求交 → 方向 → 格子 → 取色（最近鄰）。
// 物理意義：沒有 mesh、沒有貼圖取樣；格子的方塊邊緣是「最近鄰」自然留下的。光照只是看得出球面的 Lambert，
//          疊圖（經緯線、面接縫）是除錯用的讀數，不寫回格子。
// 數值影響：純函式 → RGBA8（由上到下）。決定性：同樣的格子＋參數 ⇒ 同樣的 bytes。
using System;
using SCP.Core.Canvas;

namespace SCP.Core.Globe
{
    public sealed class SCP_GlobeView
    {
        public double CenterLat = 23.7, CenterLon = 121.0;
        /// <summary>1 ＝ 整個半球剛好塞滿；2 ＝ 放大兩倍。</summary>
        public double Zoom = 1;
        public int Size = 720;
        /// <summary>經緯線間隔（度）；0 ＝ 不畫。</summary>
        public double Graticule = 10;
        public bool Seams;
    }

    public static class SCP_GlobeRender
    {
        public static byte[] RenderRgba(SCP_GlobeState s, SCP_GlobeView v)
        {
            int W = v.Size, H = v.Size;
            if (W < 16 || W > 4096) throw new SCP_GlobeException("size 要在 16..4096：" + W);
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
                    if (v.Graticule > 0)
                    {
                        SCP_GlobeGrid.DirToLatLon(dx, dy, dz, out double aLat, out double aLon);
                        double tol = aTolDeg / Math.Max(sz, 0.15);
                        double eLat = Math.Abs(aLat - Math.Round(aLat / v.Graticule) * v.Graticule);
                        double eLon = Math.Abs(aLon - Math.Round(aLon / v.Graticule) * v.Graticule) * Math.Cos(aLat * Math.PI / 180);
                        if (eLat < tol || (eLon < tol && Math.Abs(aLat) < 89)) { R = R * 0.55 + 255 * 0.45; G = G * 0.55 + 255 * 0.45; B = B * 0.55 + 255 * 0.45; }
                    }
                    rgba[o] = Clamp(R); rgba[o + 1] = Clamp(G); rgba[o + 2] = Clamp(B); rgba[o + 3] = 255;
                }

            if (v.Seams)
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
            return rgba;
        }

        public static byte[] RenderPng(SCP_GlobeState s, SCP_GlobeView v)
            => SCP_CanvasPng.EncodeRgbaRows(RenderRgba(s, v), v.Size, v.Size);

        static byte Clamp(double d) => d <= 0 ? (byte)0 : d >= 255 ? (byte)255 : (byte)(d + 0.5);
    }
}
