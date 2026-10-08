// 區塊職責：可繪製球面的**幾何層** —— 等角立方體球（equi-angular cube sphere）的格子 ↔ 方向 ↔ 經緯度換算、跨面鄰居。
// 物理意義：6 面 × N×N 格；每面沿軸向每格佔 90°/N。面內參數 a,b ∈ [-1,1] 經 tan(a·π/4) 才投到切平面 ——
//          少了這一步就是直接投影（gnomonic），每格面積最大／最小 ≈ 5.1；有這一步 ≈ 1.41。
// 數值影響：純函式，不碰磁碟。面基底一律從 meta 來（SCP_GlobeMeta.Faces），⛔ 不在呼叫點另寫一份常數 ——
//          兩份基底各自理解「哪一面朝哪、i/j 往哪增加」，症狀是接縫錯位或鏡像，而且不會報錯。
using System;
using System.Collections.Generic;

namespace SCP.Core.Globe
{
    /// <summary>一面：法線 n、面內兩軸 u／v（u×v＝n）、在 3×2 圖集裡的欄列。</summary>
    public sealed class SCP_GlobeFace
    {
        public string Name { get; set; } = "";
        public List<int> Normal { get; set; } = new List<int>();
        public List<int> U { get; set; } = new List<int>();
        public List<int> V { get; set; } = new List<int>();
        public int AtlasCol { get; set; }
        public int AtlasRow { get; set; }
    }

    public sealed class SCP_GlobeGrid
    {
        public int N { get; }
        /// <summary>一格沿軸向的角度（弧度）＝ (π/2)/N。</summary>
        public double CellAngle { get; }
        public int CellCount => 6 * N * N;

        readonly double[][] m_N = new double[6][], m_U = new double[6][], m_V = new double[6][];
        readonly IReadOnlyList<SCP_GlobeFace> m_Faces;

        public SCP_GlobeGrid(int iN, IReadOnlyList<SCP_GlobeFace> iFaces)
        {
            if (iN < 1) throw new ArgumentOutOfRangeException(nameof(iN), "N 至少 1：" + iN);
            if (iFaces.Count != 6) throw new ArgumentException("面要剛好 6 個：" + iFaces.Count);
            N = iN;
            CellAngle = Math.PI / 2 / iN;
            m_Faces = iFaces;
            for (int f = 0; f < 6; f++)
            {
                m_N[f] = Vec(iFaces[f].Normal, iFaces[f].Name, "Normal");
                m_U[f] = Vec(iFaces[f].U, iFaces[f].Name, "U");
                m_V[f] = Vec(iFaces[f].V, iFaces[f].Name, "V");
                double[] c = Cross(m_U[f], m_V[f]);
                if (Dot(c, m_N[f]) < 0.999)
                    throw new ArgumentException($"面 {iFaces[f].Name} 的基底不是右手系（U×V≠Normal）");
            }
        }

        public IReadOnlyList<SCP_GlobeFace> Faces => m_Faces;

        static double[] Vec(List<int> iV, string iFace, string iWhat)
        {
            if (iV == null || iV.Count != 3) throw new ArgumentException($"面 {iFace} 的 {iWhat} 要 3 個分量");
            double l = Math.Sqrt(iV[0] * iV[0] + iV[1] * iV[1] + iV[2] * iV[2]);
            if (Math.Abs(l - 1) > 1e-9) throw new ArgumentException($"面 {iFace} 的 {iWhat} 不是單位軸向量");
            return new double[] { iV[0], iV[1], iV[2] };
        }

        /// <summary>預設 6 面：+X +Y +Z 在圖集上排、−X −Y −Z 在下排；赤道四面 V＝北、U＝東；兩極面 V 指向 +X 那一側的反方向（北極）／同方向（南極），讓接縫與赤道面對齊。</summary>
        public static List<SCP_GlobeFace> DefaultFaces()
        {
            SCP_GlobeFace F(string iName, int[] n, int[] u, int[] v, int col, int row) => new SCP_GlobeFace
            {
                Name = iName, Normal = new List<int>(n), U = new List<int>(u), V = new List<int>(v), AtlasCol = col, AtlasRow = row,
            };
            return new List<SCP_GlobeFace>
            {
                F("+X", new[] { 1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, 0, 1 }, 0, 0),
                F("+Y", new[] { 0, 1, 0 }, new[] { -1, 0, 0 }, new[] { 0, 0, 1 }, 1, 0),
                F("+Z", new[] { 0, 0, 1 }, new[] { 0, 1, 0 }, new[] { -1, 0, 0 }, 2, 0),
                F("-X", new[] { -1, 0, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 }, 0, 1),
                F("-Y", new[] { 0, -1, 0 }, new[] { 1, 0, 0 }, new[] { 0, 0, 1 }, 1, 1),
                F("-Z", new[] { 0, 0, -1 }, new[] { 0, 1, 0 }, new[] { 1, 0, 0 }, 2, 1),
            };
        }

        // ── 索引 ──────────────────────────────────────────────
        public int Index(int iFace, int i, int j) => iFace * N * N + j * N + i;
        public void Unpack(int iIndex, out int oFace, out int oI, out int oJ)
        {
            int nn = N * N;
            oFace = iIndex / nn;
            int r = iIndex - oFace * nn;
            oJ = r / N;
            oI = r - oJ * N;
        }

        // ── 方向 ↔ 格子 ───────────────────────────────────────
        /// <summary>任意非零方向 → 格子索引。面取 n·d 最大的那一面（同分取面序在前）。</summary>
        public int DirToCell(double x, double y, double z)
        {
            int aFace = 0;
            double aBest = double.NegativeInfinity;
            for (int f = 0; f < 6; f++)
            {
                double d = m_N[f][0] * x + m_N[f][1] * y + m_N[f][2] * z;
                if (d > aBest) { aBest = d; aFace = f; }
            }
            double px = (m_U[aFace][0] * x + m_U[aFace][1] * y + m_U[aFace][2] * z) / aBest;
            double py = (m_V[aFace][0] * x + m_V[aFace][1] * y + m_V[aFace][2] * z) / aBest;
            return Index(aFace, ParamToCell(px), ParamToCell(py));
        }

        int ParamToCell(double iTangent)
        {
            double a = Math.Atan(iTangent) / (Math.PI / 4);   // [-1,1]
            int k = (int)Math.Floor((a + 1) * 0.5 * N);
            return k < 0 ? 0 : k >= N ? N - 1 : k;
        }

        /// <summary>格子中心的單位方向。</summary>
        public void CellCenter(int iIndex, out double x, out double y, out double z)
        {
            Unpack(iIndex, out int f, out int i, out int j);
            FaceParamToDir(f, -1 + (2.0 * i + 1) / N, -1 + (2.0 * j + 1) / N, out x, out y, out z);
        }

        /// <summary>面參數（a,b，等角座標，可略超出 [-1,1]）→ 單位方向。</summary>
        public void FaceParamToDir(int iFace, double a, double b, out double x, out double y, out double z)
        {
            double tu = Math.Tan(a * Math.PI / 4), tv = Math.Tan(b * Math.PI / 4);
            x = m_N[iFace][0] + tu * m_U[iFace][0] + tv * m_V[iFace][0];
            y = m_N[iFace][1] + tu * m_U[iFace][1] + tv * m_V[iFace][1];
            z = m_N[iFace][2] + tu * m_U[iFace][2] + tv * m_V[iFace][2];
            double l = Math.Sqrt(x * x + y * y + z * z);
            x /= l; y /= l; z /= l;
        }

        /// <summary>
        /// 鄰格（di,dj ∈ {-1,0,1}）。面內直接算；越過面邊時把參數往外推一格再走 <see cref="DirToCell"/> ——
        /// 跨面的旋轉／翻轉由幾何自己決定，⛔ 不另維護一張棱的對照表（兩份真相會漂）。
        /// </summary>
        public int Neighbor(int iIndex, int di, int dj)
        {
            Unpack(iIndex, out int f, out int i, out int j);
            int ni = i + di, nj = j + dj;
            if (ni >= 0 && ni < N && nj >= 0 && nj < N) return Index(f, ni, nj);
            FaceParamToDir(f, -1 + (2.0 * ni + 1) / N, -1 + (2.0 * nj + 1) / N, out double x, out double y, out double z);
            return DirToCell(x, y, z);
        }

        // ── 經緯度 ─────────────────────────────────────────────
        // 世界座標：+Z＝北極；經度 0 在 +X，東經 90 在 +Y。
        public static void LatLonToDir(double iLatDeg, double iLonDeg, out double x, out double y, out double z)
        {
            double la = iLatDeg * Math.PI / 180, lo = iLonDeg * Math.PI / 180;
            x = Math.Cos(la) * Math.Cos(lo);
            y = Math.Cos(la) * Math.Sin(lo);
            z = Math.Sin(la);
        }

        public static void DirToLatLon(double x, double y, double z, out double oLatDeg, out double oLonDeg)
        {
            double l = Math.Sqrt(x * x + y * y + z * z);
            oLatDeg = Math.Asin(Math.Max(-1, Math.Min(1, z / l))) * 180 / Math.PI;
            oLonDeg = Math.Atan2(y, x) * 180 / Math.PI;
        }

        public int LatLonToCell(double iLatDeg, double iLonDeg)
        {
            LatLonToDir(iLatDeg, iLonDeg, out double x, out double y, out double z);
            return DirToCell(x, y, z);
        }

        public void CellToLatLon(int iIndex, out double oLatDeg, out double oLonDeg)
        {
            CellCenter(iIndex, out double x, out double y, out double z);
            DirToLatLon(x, y, z, out oLatDeg, out oLonDeg);
        }

        /// <summary>格子在 3×2 圖集上的像素（x 往右、y 往下；面內 j 越大越上面）。</summary>
        public void AtlasPixel(int iIndex, out int oX, out int oY)
        {
            Unpack(iIndex, out int f, out int i, out int j);
            oX = m_Faces[f].AtlasCol * N + i;
            oY = m_Faces[f].AtlasRow * N + (N - 1 - j);
        }

        static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
        static double[] Cross(double[] a, double[] b) =>
            new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };
    }
}
