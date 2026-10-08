// 區塊職責：球面繪圖的**柵格化** —— 點（含半徑）、大圓弧線、多邊形填色、油漆桶；只算「要塗哪些格子」，不寫檔。
// 物理意義：線沿大圓以「格子角度的 1/3」取樣 ⇒ 相鄰兩個取樣點最多跨一格，線不會斷；
//          多邊形在經緯度平面判內外（prototype：小範圍、不含極點、不跨 180°；超出就拒絕，不猜）。
// 數值影響：純函式。回傳格子 index 的集合（可能重複，由 SCP_GlobeStore.Paint 去重）。
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SCP.Core.Globe
{
    public struct SCP_GlobeLatLon
    {
        public double Lat, Lon;
        public SCP_GlobeLatLon(double iLat, double iLon) { Lat = iLat; Lon = iLon; }
    }

    public static class SCP_GlobeDraw
    {
        /// <summary>多邊形填色最多取樣幾次（防止一個打錯的大範圍把行程卡死）。</summary>
        public const long MaxPolygonSamples = 40_000_000;

        /// <summary>「lat,lon;lat,lon;…」→ 點列。也接受換行分隔；空白忽略。壞的整批拒絕。</summary>
        public static bool TryParsePoints(string iText, out List<SCP_GlobeLatLon> oPts, out string oWhy)
        {
            oPts = new List<SCP_GlobeLatLon>();
            oWhy = "";
            foreach (string aRaw in (iText ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string aItem = aRaw.Trim();
                if (aItem.Length == 0 || aItem.StartsWith("#", StringComparison.Ordinal)) continue;
                if (!TryParseLatLon(aItem, out SCP_GlobeLatLon p, out oWhy)) return false;
                oPts.Add(p);
            }
            if (oPts.Count == 0) { oWhy = "點列是空的（格式 lat,lon;lat,lon;…）"; return false; }
            return true;
        }

        public static bool TryParseLatLon(string iText, out SCP_GlobeLatLon oP, out string oWhy)
        {
            oP = default; oWhy = "";
            string[] a = iText.Split(',');
            if (a.Length != 2) { oWhy = "經緯度要寫成 lat,lon：「" + iText + "」"; return false; }
            if (!double.TryParse(a[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double la)
                || !double.TryParse(a[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double lo)
                || double.IsNaN(la) || double.IsNaN(lo) || double.IsInfinity(la) || double.IsInfinity(lo))
            { oWhy = "經緯度不是數字：「" + iText + "」"; return false; }
            if (la < -90 || la > 90) { oWhy = "緯度要在 -90..90：「" + iText + "」"; return false; }
            if (lo < -180 || lo > 360) { oWhy = "經度要在 -180..360：「" + iText + "」"; return false; }
            oP = new SCP_GlobeLatLon(la, lo);
            return true;
        }

        // ── 點 ────────────────────────────────────────────────
        /// <summary>中心那格＋角距離 ≤ 半徑（格數）的所有格。半徑 0 ＝ 一格。</summary>
        public static List<int> Point(SCP_GlobeGrid g, SCP_GlobeLatLon p, double iRadiusCells)
        {
            SCP_GlobeGrid.LatLonToDir(p.Lat, p.Lon, out double x, out double y, out double z);
            return Disk(g, x, y, z, iRadiusCells);
        }

        static List<int> Disk(SCP_GlobeGrid g, double x, double y, double z, double iRadiusCells)
        {
            int c = g.DirToCell(x, y, z);
            var aOut = new List<int> { c };
            if (iRadiusCells <= 0) return aOut;
            double aMaxAngle = iRadiusCells * g.CellAngle;
            double aCos = Math.Cos(aMaxAngle);
            var aSeen = new HashSet<int> { c };
            var q = new Queue<int>();
            q.Enqueue(c);
            while (q.Count > 0)
            {
                int k = q.Dequeue();
                for (int dj = -1; dj <= 1; dj++)
                    for (int di = -1; di <= 1; di++)
                    {
                        if (di == 0 && dj == 0) continue;
                        int nb = g.Neighbor(k, di, dj);
                        if (!aSeen.Add(nb)) continue;
                        g.CellCenter(nb, out double cx, out double cy, out double cz);
                        if (cx * x + cy * y + cz * z < aCos) continue;
                        aOut.Add(nb);
                        q.Enqueue(nb);
                    }
            }
            return aOut;
        }

        // ── 線 ────────────────────────────────────────────────
        /// <summary>依序連成大圓弧（不自動封口）。寬度＝每個取樣點用的半徑（格數），0＝一格寬。</summary>
        public static List<int> Line(SCP_GlobeGrid g, IReadOnlyList<SCP_GlobeLatLon> iPts, double iWidthRadius)
        {
            var aOut = new List<int>();
            var aSeen = new HashSet<int>();
            void Add(double x, double y, double z)
            {
                if (iWidthRadius <= 0) { int c = g.DirToCell(x, y, z); if (aSeen.Add(c)) aOut.Add(c); return; }
                foreach (int c in Disk(g, x, y, z, iWidthRadius)) if (aSeen.Add(c)) aOut.Add(c);
            }
            if (iPts.Count == 1)
            {
                SCP_GlobeGrid.LatLonToDir(iPts[0].Lat, iPts[0].Lon, out double x, out double y, out double z);
                Add(x, y, z);
                return aOut;
            }
            double aStep = g.CellAngle / 3;
            for (int s = 0; s + 1 < iPts.Count; s++)
            {
                SCP_GlobeGrid.LatLonToDir(iPts[s].Lat, iPts[s].Lon, out double ax, out double ay, out double az);
                SCP_GlobeGrid.LatLonToDir(iPts[s + 1].Lat, iPts[s + 1].Lon, out double bx, out double by, out double bz);
                double aDot = Math.Max(-1, Math.Min(1, ax * bx + ay * by + az * bz));
                double th = Math.Acos(aDot);
                if (th > Math.PI - 1e-6) throw new SCP_GlobeException($"第 {s + 1}→{s + 2} 點是對蹠點，大圓不唯一 —— 中間插一個點");
                int aSteps = Math.Max(1, (int)Math.Ceiling(th / aStep));
                double aSin = Math.Sin(th);
                for (int k = 0; k <= aSteps; k++)
                {
                    double t = (double)k / aSteps;
                    double wa, wb;
                    if (aSin < 1e-12) { wa = 1 - t; wb = t; }
                    else { wa = Math.Sin((1 - t) * th) / aSin; wb = Math.Sin(t * th) / aSin; }
                    Add(wa * ax + wb * bx, wa * ay + wb * by, wa * az + wb * bz);
                }
            }
            return aOut;
        }

        // ── 多邊形填色 ────────────────────────────────────────
        /// <summary>
        /// 多邊形內部＋輪廓。內外在經緯度平面判（經度以第一點為基準攤平，處理跨 180° 的小島）。
        /// ⛔ 包含極點、經度跨度 ≥ 180° 的多邊形拒絕 —— 那種要換判法，prototype 不猜。
        /// </summary>
        public static List<int> Polygon(SCP_GlobeGrid g, IReadOnlyList<SCP_GlobeLatLon> iPts)
        {
            if (iPts.Count < 3) throw new SCP_GlobeException("多邊形至少 3 個點：" + iPts.Count);
            var lat = new double[iPts.Count];
            var lon = new double[iPts.Count];
            double aLon0 = iPts[0].Lon;
            for (int k = 0; k < iPts.Count; k++)
            {
                lat[k] = iPts[k].Lat;
                double d = iPts[k].Lon - aLon0;
                while (d > 180) d -= 360;
                while (d < -180) d += 360;
                lon[k] = aLon0 + d;
            }
            double mnLat = double.MaxValue, mxLat = double.MinValue, mnLon = double.MaxValue, mxLon = double.MinValue;
            for (int k = 0; k < lat.Length; k++)
            {
                mnLat = Math.Min(mnLat, lat[k]); mxLat = Math.Max(mxLat, lat[k]);
                mnLon = Math.Min(mnLon, lon[k]); mxLon = Math.Max(mxLon, lon[k]);
            }
            if (mxLon - mnLon >= 180) throw new SCP_GlobeException("多邊形經度跨度 ≥ 180°（prototype 不支援；拆成幾塊）");
            if (mxLat >= 89.9 || mnLat <= -89.9) throw new SCP_GlobeException("多邊形碰到極點（prototype 不支援）");

            double aStepDeg = g.CellAngle * 180 / Math.PI * 0.5;
            double aCosMin = Math.Cos(Math.Max(Math.Abs(mnLat), Math.Abs(mxLat)) * Math.PI / 180);
            long aEst = (long)((mxLat - mnLat) / aStepDeg + 1) * (long)((mxLon - mnLon) / (aStepDeg / Math.Max(aCosMin, 0.01)) + 1);
            if (aEst > MaxPolygonSamples)
                throw new SCP_GlobeException($"多邊形太大：估計取樣 {aEst:N0} 次 > 上限 {MaxPolygonSamples:N0}（拆小一點）");

            var aSeen = new HashSet<int>();
            var aOut = new List<int>();
            for (double la = mnLat; la <= mxLat; la += aStepDeg)
            {
                double aLonStep = aStepDeg / Math.Max(Math.Cos(la * Math.PI / 180), 0.01);
                for (double lo = mnLon; lo <= mxLon; lo += aLonStep)
                {
                    if (!Inside(lat, lon, la, lo)) continue;
                    int c = g.LatLonToCell(la, lo);
                    if (aSeen.Add(c)) aOut.Add(c);
                }
            }
            var aRing = new List<SCP_GlobeLatLon>(iPts) { iPts[0] };   // 輪廓補滿（取樣格點可能漏掉細的邊）
            foreach (int c in Line(g, aRing, 0)) if (aSeen.Add(c)) aOut.Add(c);
            return aOut;
        }

        static bool Inside(double[] lat, double[] lon, double la, double lo)
        {
            bool aIn = false;
            for (int a = 0, b = lat.Length - 1; a < lat.Length; b = a++)
            {
                if ((lat[a] > la) != (lat[b] > la)
                    && lo < (lon[b] - lon[a]) * (la - lat[a]) / (lat[b] - lat[a]) + lon[a])
                    aIn = !aIn;
            }
            return aIn;
        }

        // ── 油漆桶 ────────────────────────────────────────────
        /// <summary>從起點那格開始，四鄰同值的連通區。超過上限 ⇒ 丟例外（整筆拒絕，⛔ 不畫一半）。</summary>
        public static List<int> Flood(SCP_GlobeGrid g, SCP_GlobeCells iCells, SCP_GlobeLatLon p, int iMaxCells)
        {
            int s = g.LatLonToCell(p.Lat, p.Lon);
            int v = iCells.Get(s);
            var aOut = new List<int> { s };
            var aSeen = new HashSet<int> { s };
            var q = new Queue<int>();
            q.Enqueue(s);
            while (q.Count > 0)
            {
                int k = q.Dequeue();
                for (int d = 0; d < 4; d++)
                {
                    int nb = d == 0 ? g.Neighbor(k, 1, 0) : d == 1 ? g.Neighbor(k, -1, 0) : d == 2 ? g.Neighbor(k, 0, 1) : g.Neighbor(k, 0, -1);
                    if (iCells.Get(nb) != v || !aSeen.Add(nb)) continue;
                    aOut.Add(nb);
                    if (aOut.Count > iMaxCells)
                        throw new SCP_GlobeException($"油漆桶超過上限 {iMaxCells:N0} 格就停了（區域沒封口？）⇒ 整筆不畫");
                    q.Enqueue(nb);
                }
            }
            return aOut;
        }
    }
}
