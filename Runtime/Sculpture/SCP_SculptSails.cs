// 區塊職責：雕刻的帆（TASK-0503）—— 作品記「幾個角綁在哪、哪條邊整條釘住、什麼顏色」，觀測時才用布料模擬算形狀、才畫。
// 物理意義：帆是作者寫的作品資料（sails.json，入版控），⛔ 不是 voxel、不寫 events/ —— 一格 voxel 都不動。
//          角：3 個（三角帆）或 4 個（四邊形：方帆、梯形的縱帆），依序繞一圈；每個角的位置跟繩的端點同一種寫法
//              （固定點名｜關聯/…/固定點名｜x,y,z｜model:模型上的點 —— 解法也是同一支 <see cref="SCP_SculptPointResolver"/>，TASK-0507）。
//              角預設釘住（pin）；`free` 的角跟著布動 —— 繩綁在它上面（端點 `sail:帆/角`）⇒ 那條繩變成牽繩（最長 L ＝ slack × 設計時兩端的直線距離）。
//          邊：bind 的邊（k ＝ 角 k → 角 k+1）**整條**釘在兩角之間的線段上（方帆上緣沿 yard），不是只釘兩個角。
//          模擬：PBD 布料（距離約束：結構／剪切／彎曲）＋重力＋風（只算法線分量的壓力，含陣風）；固定 dt、固定迭代次數、單執行緒依序走
//              ⇒ 同樣的輸入＋同樣的秒數 ⇒ 逐位元同樣的形狀。重力與風照組裝件的 meters_per_voxel 換成格（風 m/s、重力 9.81 m/s²）。
//          座標：全部在組裝件的世界座標裡算（被關聯進來的作品自己的帆，角照那條關聯的 mirror／turn／at 換過去 —— 跟繩同一個公式）。
// 數值影響：每面帆一筆 <see cref="SCP_SculptSailReading"/>：ok（畫）／hidden（所在或綁的那一層沒組進來、或這次的顯示開關關掉）／disabled（自己關著）／
//          broken（角解不到、或資料不合）—— ⛔ 壞掉的不靜默消失。讀數：最鼓那一點離綁點平面幾格（帶正負號與法線）、平均位移、模擬了幾秒幾步。
// 失敗處置：sails.json 讀不了 ⇒ InvalidOperationException（⛔ 不當成沒有 —— 寫回去會清光）。
// ⚠ 沒有碰撞：帆會穿過格子與繩（同繩）。⚠ 貼圖、收帆、縮帆本版不做（Tim 2026-10-11「先可以設定顏色就好」）—— 欄位留給之後加。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptSailCorner
    {
        /// <summary>角的名字（選填；繩的端點可以寫名字或序號）。</summary>
        public string name = "";
        /// <summary>位置：固定點路徑或 x,y,z（同繩的端點）。</summary>
        public string at = "";
        /// <summary>釘住（預設）；false ＝ 跟著布動（可以被綁在帆角的繩拉住）。</summary>
        public bool pin = true;
    }

    public sealed class SCP_SculptSail
    {
        public string name = "";
        /// <summary>3 個（三角帆）或 4 個（四邊形），依序繞一圈。</summary>
        public List<SCP_SculptSailCorner> corners = new List<SCP_SculptSailCorner>();
        /// <summary>整條釘住的邊：k ＝ 角 k → 角 (k+1) mod 角數。</summary>
        public List<int> bind = new List<int>();
        /// <summary>顏色：調色盤 index（1..255）或 #RRGGBB（存正規化後的寫法，同繩）。</summary>
        public string color = SCP_SculptSails.DefaultColor;
        /// <summary>布料解析度：每邊幾格（0 ＝ 照大小自動）。</summary>
        public int res;
        /// <summary>布比角圍出來的框多出幾成（1 ＝ 剛好繃平、鼓不起來；預設 1.06）。</summary>
        public double fullness = SCP_SculptSails.DefaultFullness;
        public bool enabled = true;
        public string note = "", persona = "", created_at = "";
    }

    public sealed class SCP_SculptSailFile
    {
        public int schema = 1;
        /// <summary>這件作品當組裝件觀測時的預設風（世界座標，m/s；⚠ 用 List：JSON 對映器讀陣列是「清空再逐項加」）。</summary>
        public List<double> wind = new List<double> { 0, 0, 0 };
        /// <summary>預設模擬幾秒（觀測頁的持續模擬不看它）。</summary>
        public double seconds = SCP_SculptSails.DefaultSeconds;
        public List<SCP_SculptSail> sails = new List<SCP_SculptSail>();
    }

    /// <summary>這次觀測的環境：風（m/s，世界座標）、模擬秒數、一格幾公尺（組裝件的比例）。</summary>
    public sealed class SCP_SculptSailEnv
    {
        public double[] Wind = new double[3];
        public double Seconds = SCP_SculptSails.DefaultSeconds;
        public double MetersPerVoxel = SCP_SculptWork.DefaultMetersPerVoxel;
        /// <summary>這幾格的來源（給人讀：作品預設／CLI）。</summary>
        public string Source = "";
    }

    /// <summary>觀測時選哪些帆：None ＝ 都不畫（sails=0）；Hide ＝ 這幾面（帆路徑）不畫 —— ⚠ 只是不畫，照樣模擬（綁在上面的繩照樣拉）。</summary>
    public sealed class SCP_SculptSailSelection
    {
        public bool None;
        public HashSet<string> Hide = new HashSet<string>(StringComparer.Ordinal);

        public static SCP_SculptSailSelection Parse(string iSails, string iHide)
        {
            var aSel = new SCP_SculptSailSelection { None = iSails.Trim() == "0" };
            foreach (string p in iHide.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) aSel.Hide.Add(p.Trim().Trim('/'));
            return aSel;
        }
    }

    /// <summary>給渲染器／匯出的一面帆（已模擬、已在世界座標）：頂點、每頂點法線（照三角形繞序的那一面）、三角形、顏色。</summary>
    public sealed class SCP_SculptSailGeom
    {
        public string Path = "";
        /// <summary>xyz 攤平。</summary>
        public double[] Points = new double[0];
        public double[] Normals = new double[0];
        /// <summary>每 3 個一個三角形（頂點索引）。</summary>
        public int[] Triangles = new int[0];
        public byte R, G, B;
        public int PointCount => Points.Length / 3;
    }

    /// <summary>一面帆這次觀測的讀數。</summary>
    public sealed class SCP_SculptSailReading
    {
        public const string Ok = "ok", Hidden = "hidden", Disabled = "disabled", Broken = "broken";
        /// <summary>帆的路徑：帆所在作品的關聯路徑（組裝件自己 ⇒ 空）＋ `/` ＋ 帆名。</summary>
        public string Path = "";
        public string Owner = "";
        /// <summary>帆所在那一層的關聯路徑。</summary>
        public string Frame = "";
        public SCP_SculptSail Sail = new SCP_SculptSail();
        public string State = Ok;
        public string Reason = "";
        /// <summary>每個角的設計位置（組裝件座標）；broken 時解不到的那幾個是 null。</summary>
        public double[]?[] Corners = new double[0][];
        /// <summary>這次有沒有進模擬（所在或綁的那一層沒組進來、自己關著、壞掉 ⇒ 沒有）。</summary>
        public bool Simulated;
        public int Particles, Triangles, Resolution;
        /// <summary>最鼓那一點離綁點平面幾格（帶正負號：正 ＝ 往 <see cref="PlaneNormal"/> 那側）。</summary>
        public double Billow;
        /// <summary>綁點平面的法線（角依序繞一圈、右手定則；世界座標）。</summary>
        public double[] PlaneNormal = new double[3];
        /// <summary>全部布點離設計位置的平均位移（世界座標，格）—— 沒風 ⇒ 幾乎只往 −z。</summary>
        public double[] MeanShift = new double[3];
        /// <summary>綁住的繩數（牽繩：綁在 free 角上、而且這次有組進來的）。</summary>
        public int Tethers;
        public SCP_SculptSailGeom? Geom;
    }

    /// <summary>
    /// 一次觀測的帆與繩（<see cref="SCP_SculptSails.Rig"/> 建）：帆的讀數、繩的讀數（綁帆角的那幾條模擬完會換成那一刻的位置），以及模擬狀態。
    /// <para>CLI：<see cref="Simulate"/>(秒) 一次跑到那一刻。觀測頁：<see cref="Advance"/>(這一幀過了幾秒) 持續跑（帆會動）。</para>
    /// <para>⚠ 不是執行緒安全：同一時間只給一個人推（觀測頁在 UI 執行緒推）。</para>
    /// </summary>
    public sealed class SCP_SculptSailRig
    {
        public readonly List<SCP_SculptSailReading> Sails = new List<SCP_SculptSailReading>();
        public List<SCP_SculptRopeReading> Ropes = new List<SCP_SculptRopeReading>();
        public SCP_SculptSailEnv Env = new SCP_SculptSailEnv();
        /// <summary>這次觀測的外部模型（TASK-0507；繩端點與帆角用過的那一份）—— null ＝ 沒算模型。</summary>
        public SCP_SculptModelSet? Models;
        /// <summary>已經模擬了幾秒、幾步。</summary>
        public double Time => m_Steps * SCP_SculptSails.Dt;
        public int Steps => m_Steps;
        /// <summary>有沒有任何一面帆在模擬（沒有 ⇒ 觀測頁不必每幀重畫）。</summary>
        public bool Animated => m_N > 0;
        /// <summary>每一步花幾毫秒（最後一次 Simulate／Advance 的平均；讀數用）。</summary>
        public double LastStepMs { get; private set; }

        // ── 布點（全部帆一起：牽繩可以跨帆）──
        internal int m_N;
        internal double[] m_X = new double[0], m_P = new double[0], m_V = new double[0], m_W = new double[0], m_Pin = new double[0], m_Mass = new double[0], m_Design = new double[0];
        internal int[] m_CA = new int[0], m_CB = new int[0];
        internal double[] m_CR = new double[0], m_CK = new double[0];
        internal int[] m_Tri = new int[0];
        internal readonly List<int[]> m_Tethers = new List<int[]>();       // { 布點 a, 布點 b（−1 ＝ 固定點）, 繩索引 }
        internal readonly List<double[]> m_TetherFix = new List<double[]>(); // 固定點（b ＝ −1 時）＋長度 L：{x,y,z,L}
        /// <summary>每面帆在布點陣列裡的範圍與三角形範圍：{ 布點起, 布點數, 三角形起（×3 之前）, 三角形數 }；沒模擬 ⇒ null。</summary>
        internal readonly List<int[]?> m_Range = new List<int[]?>();
        /// <summary>帆角全路徑（`帆路徑#k`）→ 布點索引。</summary>
        internal readonly Dictionary<string, int> m_CornerParticle = new Dictionary<string, int>(StringComparer.Ordinal);
        internal readonly Dictionary<string, double[]> m_CornerDesign = new Dictionary<string, double[]>(StringComparer.Ordinal);
        internal double m_G, m_K;
        internal double[] m_U = new double[3];
        double[] m_F = new double[0];
        int m_Steps;
        double m_Carry;

        /// <summary>從現在再模擬 <paramref name="iSeconds"/> 秒（照固定 dt 取整步）；完成後刷新讀數與幾何。</summary>
        public void Simulate(double iSeconds)
        {
            int aSteps = (int)Math.Round(Math.Max(0, iSeconds) / SCP_SculptSails.Dt);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < aSteps; i++) Step();
            if (aSteps > 0) LastStepMs = sw.Elapsed.TotalMilliseconds / aSteps;
            Refresh();
        }

        /// <summary>
        /// 觀測頁：這一幀過了 <paramref name="iRealSeconds"/> 秒 ⇒ 照固定 dt 補步（零頭留到下一幀；一幀最多 <paramref name="iMaxSteps"/> 步，跟不上就慢動作，⛔ 不改 dt）。
        /// 回 true ＝ 有走（要重畫）。
        /// </summary>
        public bool Advance(double iRealSeconds, int iMaxSteps = 4)
        {
            if (m_N == 0) return false;
            m_Carry += Math.Max(0, Math.Min(iRealSeconds, 0.25));
            int aSteps = 0;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (m_Carry >= SCP_SculptSails.Dt && aSteps < iMaxSteps) { Step(); m_Carry -= SCP_SculptSails.Dt; aSteps++; }
            if (aSteps >= iMaxSteps) m_Carry = 0;
            if (aSteps == 0) return false;
            LastStepMs = sw.Elapsed.TotalMilliseconds / aSteps;
            Refresh();
            return true;
        }

        /// <summary>畫得出來的帆（state ok）。</summary>
        public List<SCP_SculptSailGeom> SailGeoms()
        {
            var aOut = new List<SCP_SculptSailGeom>();
            foreach (var s in Sails) if (s.Geom != null) aOut.Add(s.Geom);
            return aOut;
        }

        /// <summary>畫得出來的繩：<paramref name="iOnSails"/> true ＝ 有一端綁帆角的（會跟著帆動）；false ＝ 其他（靜態）。</summary>
        public List<SCP_SculptRopeGeom> RopeGeoms(bool iOnSails)
        {
            var aOut = new List<SCP_SculptRopeGeom>();
            foreach (var r in Ropes)
                if (r.Geom != null && (r.FromSail.Length > 0 || r.ToSail.Length > 0) == iOnSails) aOut.Add(r.Geom);
            return aOut;
        }

        // ───────────────────────── 一步 ─────────────────────────

        void Step()
        {
            double dt = SCP_SculptSails.Dt;
            double t = m_Steps * dt;
            // 陣風：固定的兩個正弦（決定性）—— 讓觀測頁裡的帆會動；平均值 ＝ 設定的風
            double aGust = 1 + 0.15 * Math.Sin(2 * Math.PI * t / 3.1) + 0.10 * Math.Sin(2 * Math.PI * t / 1.7 + 1.3);
            double ux = m_U[0] * aGust, uy = m_U[1] * aGust, uz = m_U[2] * aGust;
            int n = m_N;
            // 風力累加陣列重用（⛔ 不每步配置：二十幾面帆一步一萬多個布點）
            if (m_F.Length != n * 3) m_F = new double[n * 3]; else Array.Clear(m_F, 0, m_F.Length);
            double[] aF = m_F;
            // 風：每個三角形照法線分量受壓（F ＝ K·(v_rel·n)|v_rel·n|·n·面積），平分給三個角
            if (ux != 0 || uy != 0 || uz != 0)
                for (int k = 0; k < m_Tri.Length; k += 3)
                {
                    int a = m_Tri[k], b = m_Tri[k + 1], c = m_Tri[k + 2];
                    double e1x = m_X[b * 3] - m_X[a * 3], e1y = m_X[b * 3 + 1] - m_X[a * 3 + 1], e1z = m_X[b * 3 + 2] - m_X[a * 3 + 2];
                    double e2x = m_X[c * 3] - m_X[a * 3], e2y = m_X[c * 3 + 1] - m_X[a * 3 + 1], e2z = m_X[c * 3 + 2] - m_X[a * 3 + 2];
                    double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                    double nl = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                    if (nl < 1e-12) continue;
                    double area = nl / 2;
                    nx /= nl; ny /= nl; nz /= nl;
                    double vx = (m_V[a * 3] + m_V[b * 3] + m_V[c * 3]) / 3, vy = (m_V[a * 3 + 1] + m_V[b * 3 + 1] + m_V[c * 3 + 1]) / 3, vz = (m_V[a * 3 + 2] + m_V[b * 3 + 2] + m_V[c * 3 + 2]) / 3;
                    double vn = (ux - vx) * nx + (uy - vy) * ny + (uz - vz) * nz;
                    double f = m_K * vn * Math.Abs(vn) * area / 3;
                    aF[a * 3] += f * nx; aF[a * 3 + 1] += f * ny; aF[a * 3 + 2] += f * nz;
                    aF[b * 3] += f * nx; aF[b * 3 + 1] += f * ny; aF[b * 3 + 2] += f * nz;
                    aF[c * 3] += f * nx; aF[c * 3 + 1] += f * ny; aF[c * 3 + 2] += f * nz;
                }
            double aDamp = Math.Exp(-SCP_SculptSails.Damping * dt);
            for (int i = 0; i < n; i++)
            {
                if (m_W[i] == 0)
                {
                    m_P[i * 3] = m_Pin[i * 3]; m_P[i * 3 + 1] = m_Pin[i * 3 + 1]; m_P[i * 3 + 2] = m_Pin[i * 3 + 2];
                    continue;
                }
                double m = m_Mass[i];
                m_V[i * 3] = (m_V[i * 3] + dt * aF[i * 3] / m) * aDamp;
                m_V[i * 3 + 1] = (m_V[i * 3 + 1] + dt * aF[i * 3 + 1] / m) * aDamp;
                m_V[i * 3 + 2] = (m_V[i * 3 + 2] + dt * (aF[i * 3 + 2] / m - m_G)) * aDamp;
                m_P[i * 3] = m_X[i * 3] + dt * m_V[i * 3];
                m_P[i * 3 + 1] = m_X[i * 3 + 1] + dt * m_V[i * 3 + 1];
                m_P[i * 3 + 2] = m_X[i * 3 + 2] + dt * m_V[i * 3 + 2];
            }
            for (int it = 0; it < SCP_SculptSails.Iterations; it++)
            {
                for (int k = 0; k < m_CA.Length; k++) Project(m_CA[k], m_CB[k], m_CR[k], m_CK[k], false);
                for (int k = 0; k < m_Tethers.Count; k++)
                {
                    int[] tt = m_Tethers[k];
                    if (tt[1] >= 0) Project(tt[0], tt[1], m_TetherFix[k][3], 1, true);
                    else ProjectFixed(tt[0], m_TetherFix[k]);
                }
            }
            for (int i = 0; i < n * 3; i++)
            {
                m_V[i] = (m_P[i] - m_X[i]) / dt;
                m_X[i] = m_P[i];
            }
            m_Steps++;
        }

        /// <summary>距離約束（<paramref name="iMaxOnly"/> ＝ 牽繩：只在超過長度時拉回）。</summary>
        void Project(int a, int b, double iRest, double iK, bool iMaxOnly)
        {
            double wa = m_W[a], wb = m_W[b];
            if (wa + wb == 0) return;
            double dx = m_P[b * 3] - m_P[a * 3], dy = m_P[b * 3 + 1] - m_P[a * 3 + 1], dz = m_P[b * 3 + 2] - m_P[a * 3 + 2];
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len < 1e-12) return;
            if (iMaxOnly && len <= iRest) return;
            double s = iK * (len - iRest) / (len * (wa + wb));
            m_P[a * 3] += wa * s * dx; m_P[a * 3 + 1] += wa * s * dy; m_P[a * 3 + 2] += wa * s * dz;
            m_P[b * 3] -= wb * s * dx; m_P[b * 3 + 1] -= wb * s * dy; m_P[b * 3 + 2] -= wb * s * dz;
        }

        /// <summary>牽繩的另一端是固定點：布點離它超過 L ⇒ 拉回到 L。</summary>
        void ProjectFixed(int a, double[] iFix)
        {
            if (m_W[a] == 0) return;
            double dx = m_P[a * 3] - iFix[0], dy = m_P[a * 3 + 1] - iFix[1], dz = m_P[a * 3 + 2] - iFix[2];
            double len = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            if (len <= iFix[3] || len < 1e-12) return;
            double k = iFix[3] / len;
            m_P[a * 3] = iFix[0] + dx * k; m_P[a * 3 + 1] = iFix[1] + dy * k; m_P[a * 3 + 2] = iFix[2] + dz * k;
        }

        // ───────────────────────── 讀數與幾何 ─────────────────────────

        /// <summary>照現在的布點重算：每面帆的幾何與讀數、綁帆角的繩（兩端換成帆角現在的位置，長度不變、形狀重算）。</summary>
        public void Refresh()
        {
            for (int s = 0; s < Sails.Count; s++)
            {
                SCP_SculptSailReading r = Sails[s];
                int[]? rg = m_Range[s];
                if (rg == null) continue;
                int p0 = rg[0], pn = rg[1], t0 = rg[2], tn = rg[3];
                var aPts = new double[pn * 3];
                Array.Copy(m_X, p0 * 3, aPts, 0, pn * 3);
                var aTri = new int[tn * 3];
                for (int k = 0; k < tn * 3; k++) aTri[k] = m_Tri[t0 * 3 + k] - p0;
                double[] aNrm = SCP_SculptSails.VertexNormals(aPts, aTri);
                // 讀數：綁點平面（角的設計位置）上的有號距離，取絕對值最大那一點；平均位移
                double[] c0 = SCP_SculptSails.Centroid(r.Corners), pn3 = r.PlaneNormal;
                double aBest = 0, sx = 0, sy = 0, sz = 0;
                for (int i = 0; i < pn; i++)
                {
                    double d = (aPts[i * 3] - c0[0]) * pn3[0] + (aPts[i * 3 + 1] - c0[1]) * pn3[1] + (aPts[i * 3 + 2] - c0[2]) * pn3[2];
                    if (Math.Abs(d) > Math.Abs(aBest)) aBest = d;
                    sx += aPts[i * 3] - m_Design[(p0 + i) * 3]; sy += aPts[i * 3 + 1] - m_Design[(p0 + i) * 3 + 1]; sz += aPts[i * 3 + 2] - m_Design[(p0 + i) * 3 + 2];
                }
                r.Billow = aBest;
                r.MeanShift = new[] { sx / pn, sy / pn, sz / pn };
                if (r.State == SCP_SculptSailReading.Ok)
                {
                    SCP_SculptRopes.TryParseColor(r.Sail.color, out byte cr, out byte cg, out byte cb, out _);
                    r.Geom = new SCP_SculptSailGeom { Path = r.Path, Points = aPts, Normals = aNrm, Triangles = aTri, R = cr, G = cg, B = cb };
                }
            }
            // 綁帆角的繩：兩端換成那一刻的位置；繩長 ＝ slack × 設計時兩端的直線距離（不變），形狀照新的兩端重算
            foreach (SCP_SculptRopeReading r in Ropes)
            {
                if (r.FromSail.Length == 0 && r.ToSail.Length == 0) continue;
                if (r.From == null || r.To == null) continue;
                double[] aFrom0 = DesignEnd(r, true), aTo0 = DesignEnd(r, false);
                double aL = r.Rope.slack * Dist(aFrom0, aTo0);
                double[] aFrom = CurrentEnd(r.FromSail, r.From), aTo = CurrentEnd(r.ToSail, r.To);
                double aChord = Dist(aFrom, aTo);
                double aSlack = aChord < 1e-9 ? 1 : Math.Max(1, Math.Min(SCP_SculptRopes.MaxSlack, aL / aChord));
                double[] aPts = SCP_SculptRopes.Solve(aFrom, aTo, aSlack, out double aLen, out double aSag, out bool aVert, out _);
                r.From = aFrom; r.To = aTo; r.Chord = aChord; r.Length = aLen; r.MaxSag = aSag; r.Vertical = aVert;
                if (r.Geom != null) r.Geom = new SCP_SculptRopeGeom { Path = r.Geom.Path, Points = aPts, Radius = r.Geom.Radius, R = r.Geom.R, G = r.Geom.G, B = r.Geom.B };
            }
        }

        /// <summary>繩的某一端的設計位置（帆角 ⇒ 角的設計位置；其他 ⇒ 解出來的那一點，它不會動）。</summary>
        double[] DesignEnd(SCP_SculptRopeReading r, bool iFrom)
        {
            string aKey = iFrom ? r.FromSail : r.ToSail;
            if (aKey.Length > 0 && m_CornerDesign.TryGetValue(aKey, out double[]? d)) return d;
            return (iFrom ? r.From : r.To)!;
        }

        double[] CurrentEnd(string iKey, double[] iFallback)
        {
            if (iKey.Length > 0 && m_CornerParticle.TryGetValue(iKey, out int p)) return new[] { m_X[p * 3], m_X[p * 3 + 1], m_X[p * 3 + 2] };
            if (iKey.Length > 0 && m_CornerDesign.TryGetValue(iKey, out double[]? d)) return d;
            return iFallback;
        }

        static double Dist(double[] a, double[] b)
        {
            double dx = b[0] - a[0], dy = b[1] - a[1], dz = b[2] - a[2];
            return Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    public static class SCP_SculptSails
    {
        public const string DefaultColor = "#f2ead8";   // 帆布色
        public const double DefaultFullness = 1.06;
        public const double MaxFullness = 1.5;
        public const double DefaultSeconds = 3;
        public const double MaxSeconds = 60;
        public const double MaxWind = 60;    // m/s
        public const int MinRes = 3, MaxRes = 48;
        /// <summary>模擬的固定時間步（秒）、每步的約束迭代次數、速度阻尼（每秒）—— 三個都是常數 ⇒ 決定性。</summary>
        public const double Dt = 1.0 / 60;
        public const int Iterations = 8;
        public const double Damping = 1.5;
        /// <summary>風壓係數：½·ρ空氣·Cd ÷ 帆布面密度（ρ 1.2 kg/m³、Cd 1.2、帆布 0.4 kg/m²）⇒ 1.8 /m；換成格要乘 meters_per_voxel。</summary>
        public const double WindPressurePerMeter = 0.5 * 1.2 * 1.2 / 0.4;
        /// <summary>雙面畫時兩面各往法線推多少格（避免正反兩面同一個深度，陰影自遮蔽）。</summary>
        public const double HalfThickness = 0.05;

        // ───────────────────────── 讀寫 ─────────────────────────

        public static SCP_SculptSailFile ReadSails(SCP_SculptPaths iPaths)
        {
            string aFile = iPaths.SailsFile;
            if (!File.Exists(aFile)) return new SCP_SculptSailFile();
            var aOut = new SCP_SculptSailFile { schema = 0 };
            try
            {
                var aOpt = new SCP_JsonMapOptions();
                SCP_JsonMapper.Populate(aOut, SCP_JsonParser.Parse(File.ReadAllText(aFile, new UTF8Encoding(false)), false), aOpt);
                if (aOpt.Diagnostics.Count > 0) throw new InvalidOperationException(string.Join("; ", aOpt.Diagnostics));
            }
            catch (Exception e) { throw new InvalidOperationException("sails.json 讀不了（" + aFile + "：" + e.Message + "）—— ⛔ 不當成沒有，修好再用"); }
            if (aOut.schema != 1) throw new InvalidOperationException("sails.json 讀不了（" + aFile + "：schema=" + aOut.schema + "）");
            if (CheckWind(aOut.wind) is string aWindBad) throw new InvalidOperationException("sails.json 讀不了（" + aFile + "：wind " + aWindBad + "）");
            if (CheckSeconds(aOut.seconds) is string aSecBad) throw new InvalidOperationException("sails.json 讀不了（" + aFile + "：" + aSecBad + "）");
            var aNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var s in aOut.sails)
            {
                string? aBad = SCP_SculptRopes.ValidName(s.name, "帆") ?? (aNames.Add(s.name) ? null : "名字重複") ?? Check(s);
                if (aBad != null) throw new InvalidOperationException("sails.json 讀不了（" + aFile + "：帆 `" + s.name + "` " + aBad + "）");
            }
            return aOut;
        }

        public static void SaveSails(SCP_SculptPaths iPaths, SCP_SculptSailFile iFile)
        {
            iFile.schema = 1;
            string aPath = iPaths.SailsFile, aTmp = aPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(iFile), true) + "\n", new UTF8Encoding(false));
                if (File.Exists(aPath)) File.Replace(aTmp, aPath, null);
                else File.Move(aTmp, aPath);
            }
            finally { try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch (Exception) { } }
        }

        // ───────────────────────── 驗證 ─────────────────────────

        /// <summary>一面帆的資料合不合（不解端點）。null ＝ 合法。</summary>
        public static string? Check(SCP_SculptSail s)
        {
            int n = s.corners.Count;
            if (n != 3 && n != 4) return "角要 3 個（三角帆）或 4 個（四邊形），依序繞一圈：got " + n;
            var aCornerNames = new HashSet<string>(StringComparer.Ordinal);
            for (int k = 0; k < n; k++)
            {
                SCP_SculptSailCorner c = s.corners[k];
                if (c.name.Length > 0)
                {
                    if (SCP_SculptRopes.ValidName(c.name, "帆角") is string aBadName) return "角 " + k + "：" + aBadName;
                    if (int.TryParse(c.name, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) return "角 " + k + "：名字不可以是純數字（`" + c.name + "` 會跟序號撞）";
                    if (!aCornerNames.Add(c.name)) return "角的名字重複：`" + c.name + "`";
                }
                if (!SCP_SculptRopes.TryParseEndpoint(c.at, out _, out string aPath, out string aBad)) return "角 " + k + "：" + aBad;
                if (aPath.StartsWith(SCP_SculptRopes.SailPrefix, StringComparison.Ordinal)) return "角 " + k + "：帆角不能綁在另一面帆的角上（`" + c.at + "`）—— 用 free 角＋繩";
            }
            var aEdges = new HashSet<int>();
            foreach (int e in s.bind)
                if (e < 0 || e >= n) return "bind 的邊 " + e + " 不在 0.." + (n - 1) + "（邊 k ＝ 角 k → 角 k+1）";
                else if (!aEdges.Add(e)) return "bind 的邊重複：" + e;
            if (TryParseColorStored(s.color) is string aColorBad) return aColorBad;
            if (s.res != 0 && (s.res < MinRes || s.res > MaxRes)) return "res（每邊幾格）要是 0（自動）或 " + MinRes + ".." + MaxRes + "：" + s.res;
            if (double.IsNaN(s.fullness) || s.fullness < 1 || s.fullness > MaxFullness) return "fullness（布比框多幾成）要在 1.." + MaxFullness.ToString(CultureInfo.InvariantCulture) + "：" + s.fullness.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        static string? TryParseColorStored(string iColor)
        {
            if (SCP_SculptRopes.TryParseColor(iColor, out _, out _, out _, out string aNorm) is string aBad) return aBad;
            return aNorm != iColor ? "color 不是正規化的寫法（`" + iColor + "`）" : null;
        }

        public static string? CheckWind(IList<double> iWind)
        {
            if (iWind == null || iWind.Count != 3) return "要三個數（x,y,z，m/s）";
            foreach (double d in iWind)
                if (double.IsNaN(d) || double.IsInfinity(d) || Math.Abs(d) > MaxWind) return "每個分量要在 ±" + MaxWind + " m/s：" + string.Join(",", iWind);
            return null;
        }

        public static string? CheckSeconds(double iS)
            => double.IsNaN(iS) || iS < 0 || iS > MaxSeconds ? "seconds（模擬幾秒）要在 0.." + MaxSeconds + "：" + iS.ToString(CultureInfo.InvariantCulture) : null;

        /// <summary>`x,y,z`（m/s）⇒ 三個數。</summary>
        public static bool TryParseWind(string iText, out double[] oWind, out string oBad)
        {
            oWind = new double[3]; oBad = "";
            string[] a = (iText ?? "").Split(',');
            if (a.Length != 3) { oBad = "wind 要是 x,y,z（m/s，世界座標；例 0,-8,0 ＝ 往 −y 吹的 8 m/s 風）：'" + iText + "'"; return false; }
            for (int i = 0; i < 3; i++)
                if (!double.TryParse(a[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out oWind[i])) { oBad = "wind 不是三個數：'" + iText + "'"; return false; }
            string? aBad = CheckWind(oWind);
            if (aBad != null) { oBad = "wind " + aBad; return false; }
            return true;
        }

        /// <summary>角的序號（名字或數字）；找不到 ⇒ −1。</summary>
        public static int CornerIndex(SCP_SculptSail s, string iToken)
        {
            for (int k = 0; k < s.corners.Count; k++) if (s.corners[k].name.Length > 0 && s.corners[k].name == iToken) return k;
            if (int.TryParse(iToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) && i >= 0 && i < s.corners.Count) return i;
            return -1;
        }

        static string CornerList(SCP_SculptSail s)
        {
            var a = new List<string>();
            for (int k = 0; k < s.corners.Count; k++) a.Add(s.corners[k].name.Length > 0 ? k + "=" + s.corners[k].name : k.ToString(CultureInfo.InvariantCulture));
            return string.Join("、", a);
        }

        /// <summary>這個角實際上釘不釘：pin，或在 bind 的邊上（整條邊釘住 ⇒ 兩端的角也釘住）。</summary>
        public static bool CornerPinned(SCP_SculptSail s, int k)
        {
            int n = s.corners.Count;
            return s.corners[k].pin || s.bind.Contains(k) || s.bind.Contains((k + n - 1) % n);
        }

        // ───────────────────────── 觀測：建 rig ─────────────────────────

        /// <summary>
        /// 一次觀測的帆與繩：組裝件＋關聯樹上每一層的帆（角照那一層換座標）、繩（綁帆角的端點由這裡注入位置），以及布料模擬的初始狀態（設計位置、速度 0）。
        /// ⛔ 還沒模擬 —— 呼叫端 <see cref="SCP_SculptSailRig.Simulate"/>（CLI）或 <see cref="SCP_SculptSailRig.Advance"/>（觀測頁）。只要讀數不要形狀（列清單、寫入前驗證）⇒ 照樣可以直接讀。
        /// <paramref name="iSailOverride"/>／<paramref name="iRopeOverride"/>：組裝件自己那份換成還沒存的那一份（寫入前驗證，同 SCP_SculptLinks.Tree 的 override）。
        /// <paramref name="iModels"/>：這次觀測的外部模型（TASK-0507）—— 繩的端點與帆的角可以是 `model:` 上的點；null ⇒ 那些點 broken 並說原因。
        /// sails.json／ropes.json 讀不了 ⇒ InvalidOperationException 照丟。
        /// </summary>
        public static SCP_SculptSailRig Rig(SCP_SculptWorks iWorks, string iRootId, List<SCP_SculptLinkNode>? iTree,
                                            SCP_SculptLinkSelection iLinkSel, SCP_SculptRopeSelection iRopeSel, SCP_SculptSailSelection iSailSel,
                                            SCP_SculptSailEnv iEnv, SCP_SculptSailFile? iSailOverride = null, SCP_SculptRopeFile? iRopeOverride = null,
                                            SCP_SculptModelSet? iModels = null)
        {
            var aRig = new SCP_SculptSailRig { Env = iEnv, Models = iModels };
            var aIndex = SCP_SculptRopes.IndexTree(iTree);
            var aPoints = new SCP_SculptPointResolver(iWorks, iRootId, aIndex, iLinkSel) { Models = iModels };
            var aSailCache = new Dictionary<string, SCP_SculptSailFile>(StringComparer.Ordinal);
            if (iSailOverride != null) aSailCache[iRootId] = iSailOverride;
            var aFrames = new List<string> { "" };
            SCP_SculptLinks.Paths(iTree ?? new List<SCP_SculptLinkNode>(), aFrames);
            var aByPath = new Dictionary<string, SCP_SculptSailReading>(StringComparer.Ordinal);

            // ① 帆：解角（設計位置）、定狀態
            foreach (string aFrame in aFrames)
            {
                string aWork = aFrame.Length == 0 ? iRootId : aIndex[aFrame].Card.id;
                if (!aSailCache.TryGetValue(aWork, out SCP_SculptSailFile? aFile)) { aFile = ReadSails(iWorks.SpacePaths(aWork)); aSailCache[aWork] = aFile; }
                bool aFrameDrawn = SCP_SculptRopes.PathDrawn(aFrame, aIndex, iLinkSel);
                foreach (SCP_SculptSail s in aFile.sails)
                {
                    var r = new SCP_SculptSailReading { Path = aFrame.Length == 0 ? s.name : aFrame + "/" + s.name, Owner = aWork, Frame = aFrame, Sail = s, Corners = new double[s.corners.Count][] };
                    var aWhy = new List<string>();
                    bool aLinkHidden = !aFrameDrawn;
                    if (aLinkHidden) aWhy.Add("帆所在的 `" + aFrame + "` 這次沒畫");
                    for (int k = 0; k < s.corners.Count; k++)
                    {
                        string aLabel = "角 " + k + (s.corners[k].name.Length > 0 ? "（" + s.corners[k].name + "）" : "");
                        string? aBad = aPoints.Resolve(aFrame, s.corners[k].at, false, out double[]? aPt, out _, out string aHiddenWhy);
                        if (aBad != null) { r.State = SCP_SculptSailReading.Broken; aWhy.Add(aLabel + "：" + aBad); continue; }
                        if (aHiddenWhy.Length > 0) { aLinkHidden = true; aWhy.Add(aLabel + aHiddenWhy); }
                        r.Corners[k] = aPt;
                    }
                    if (r.State != SCP_SculptSailReading.Broken)
                    {
                        r.PlaneNormal = PlaneNormal(r.Corners);
                        if (Norm(r.PlaneNormal) < 0.5) { r.State = SCP_SculptSailReading.Broken; aWhy.Add("角幾乎在同一條線上（圍不出面）"); }
                    }
                    if (r.State != SCP_SculptSailReading.Broken)
                    {
                        r.Simulated = s.enabled && !aLinkHidden;
                        if (!s.enabled) { r.State = SCP_SculptSailReading.Disabled; aWhy.Add("這面帆關著（enabled=0）"); }
                        else if (aLinkHidden) r.State = SCP_SculptSailReading.Hidden;
                        else if (iSailSel.None) { r.State = SCP_SculptSailReading.Hidden; aWhy.Add("這次不畫帆（sails=0；照樣模擬）"); }
                        else if (iSailSel.Hide.Contains(r.Path)) { r.State = SCP_SculptSailReading.Hidden; aWhy.Add("這次拿掉（hide_sails；照樣模擬）"); }
                        bool aAnyPin = false;
                        for (int k = 0; k < s.corners.Count; k++) aAnyPin |= CornerPinned(s, k);
                        if (!aAnyPin) aWhy.Add("⚠ 沒有釘住的角或邊 ⇒ 只靠綁在角上的繩拉著");
                    }
                    r.Reason = string.Join("；", aWhy);
                    aRig.Sails.Add(r);
                    aByPath[r.Path] = r;
                }
            }

            // ② 繩：帆角端點由這裡解（設計位置）
            SCP_SculptSailEndResolver aResolver = (string iFrame, string iRef, out double[]? oPoint, out string oKey, out string oHiddenWhy) =>
            {
                oPoint = null; oKey = ""; oHiddenWhy = "";
                string[] aSegs = iRef.Split('/');
                string aCorner = aSegs[aSegs.Length - 1], aName = aSegs[aSegs.Length - 2];
                string aRel = string.Join("/", aSegs, 0, aSegs.Length - 2);
                string aSailFrame = aRel.Length == 0 ? iFrame : iFrame.Length == 0 ? aRel : iFrame + "/" + aRel;
                if (aSailFrame.Length > 0 && !aIndex.ContainsKey(aSailFrame)) return "關聯路徑 `" + aSailFrame + "` 不存在（帆角 `sail:" + iRef + "`）";
                string aSailPath = aSailFrame.Length == 0 ? aName : aSailFrame + "/" + aName;
                if (!aByPath.TryGetValue(aSailPath, out SCP_SculptSailReading? aSail))
                {
                    var aHere = new List<string>();
                    foreach (var x in aRig.Sails) if (x.Frame == aSailFrame) aHere.Add(x.Sail.name);
                    return "沒有帆 `" + aSailPath + "`（那一層現有：" + (aHere.Count == 0 ? "（沒有）" : string.Join("、", aHere)) + "）";
                }
                if (aSail.State == SCP_SculptSailReading.Broken) return "帆 `" + aSailPath + "` 本身壞了（" + aSail.Reason + "）";
                int k = CornerIndex(aSail.Sail, aCorner);
                if (k < 0) return "帆 `" + aSailPath + "` 沒有角 `" + aCorner + "`（有：" + CornerList(aSail.Sail) + "）";
                oPoint = aSail.Corners[k];
                oKey = aSailPath + "#" + k.ToString(CultureInfo.InvariantCulture);
                if (!aSail.Simulated) oHiddenWhy = " `" + aSailPath + "` 這次沒組進來（" + aSail.State + "）";
                return null;
            };
            aRig.Ropes = SCP_SculptRopes.Resolve(iWorks, iRootId, iTree, iLinkSel, iRopeSel, iRopeOverride, aResolver, iModels);

            // ③ 布點：模擬的帆依序排進同一個陣列
            BuildParticles(aRig);
            double mpv = iEnv.MetersPerVoxel > 0 ? iEnv.MetersPerVoxel : SCP_SculptWork.DefaultMetersPerVoxel;
            aRig.m_G = 9.81 / mpv;
            aRig.m_K = WindPressurePerMeter * mpv;
            aRig.m_U = new[] { iEnv.Wind[0] / mpv, iEnv.Wind[1] / mpv, iEnv.Wind[2] / mpv };

            // ④ 牽繩：綁在 free 帆角上、而且這次有組進來的繩（只被顯示開關關掉的照樣拉）
            for (int i = 0; i < aRig.Ropes.Count; i++)
            {
                SCP_SculptRopeReading r = aRig.Ropes[i];
                if (r.State == SCP_SculptRopeReading.Broken || r.LinkHidden || r.From == null || r.To == null) continue;
                if (r.FromSail.Length == 0 && r.ToSail.Length == 0) continue;
                double aL = r.Rope.slack * Dist(r.From, r.To);
                int a = FreeParticle(aRig, r.FromSail), b = FreeParticle(aRig, r.ToSail);
                if (a < 0 && b < 0) continue;   // 兩端都釘著 ⇒ 繩只是畫出來，不拉任何東西
                if (a < 0) { a = b; b = -1; aRig.m_TetherFix.Add(new[] { r.From[0], r.From[1], r.From[2], aL }); }
                else if (b < 0) aRig.m_TetherFix.Add(new[] { r.To[0], r.To[1], r.To[2], aL });
                else aRig.m_TetherFix.Add(new[] { 0, 0, 0, aL });
                aRig.m_Tethers.Add(new[] { a, b, i });
                foreach (string aKey in new[] { r.FromSail, r.ToSail })
                    if (aKey.Length > 0 && aByPath.TryGetValue(aKey.Substring(0, aKey.LastIndexOf('#')), out SCP_SculptSailReading? aS)) aS.Tethers++;
            }
            aRig.Refresh();
            return aRig;
        }

        /// <summary>帆角全路徑 → 會動的布點（釘住的角 ⇒ −1）。</summary>
        static int FreeParticle(SCP_SculptSailRig iRig, string iKey)
        {
            if (iKey.Length == 0 || !iRig.m_CornerParticle.TryGetValue(iKey, out int p)) return -1;
            return iRig.m_W[p] > 0 ? p : -1;
        }

        static void BuildParticles(SCP_SculptSailRig ioRig)
        {
            var X = new List<double>(); var W = new List<double>(); var Pin = new List<double>(); var Mass = new List<double>();
            var CA = new List<int>(); var CB = new List<int>(); var CR = new List<double>(); var CK = new List<double>(); var Tri = new List<int>();
            foreach (SCP_SculptSailReading r in ioRig.Sails)
            {
                if (!r.Simulated) { ioRig.m_Range.Add(null); continue; }
                SCP_SculptSail s = r.Sail;
                int n = s.corners.Count;
                double[][] C = new double[n][];
                for (int k = 0; k < n; k++) C[k] = r.Corners[k]!;
                double aMaxEdge = 0;
                for (int k = 0; k < n; k++) aMaxEdge = Math.Max(aMaxEdge, Dist(C[k], C[(k + 1) % n]));
                int res = s.res > 0 ? s.res : Math.Max(6, Math.Min(24, (int)Math.Ceiling(aMaxEdge / 4)));
                int p0 = W.Count, t0 = Tri.Count / 3;
                // 格點：(i,j) → 局部索引；位置 ＝ 四邊形雙線性／三角形重心
                var aLocal = new Dictionary<long, int>();
                int Id(int i, int j) => aLocal.TryGetValue(((long)i << 32) | (uint)j, out int v) ? v : -1;
                for (int j = 0; j <= res; j++)
                    for (int i = 0; i <= res; i++)
                    {
                        if (n == 3 && i + j > res) continue;
                        double u = (double)i / res, v = (double)j / res;
                        double[] P = n == 4
                            ? Lerp4(C, u, v)
                            : new[] { C[0][0] + (C[1][0] - C[0][0]) * u + (C[2][0] - C[0][0]) * v, C[0][1] + (C[1][1] - C[0][1]) * u + (C[2][1] - C[0][1]) * v, C[0][2] + (C[1][2] - C[0][2]) * u + (C[2][2] - C[0][2]) * v };
                        aLocal[((long)i << 32) | (uint)j] = W.Count - p0;
                        X.Add(P[0]); X.Add(P[1]); X.Add(P[2]);
                        W.Add(1); Pin.Add(P[0]); Pin.Add(P[1]); Pin.Add(P[2]); Mass.Add(0);
                    }
                int Gl(int i, int j) { int l = Id(i, j); return l < 0 ? -1 : p0 + l; }
                // 三角形（繞序一致：法線 ＝ 角依序繞一圈的右手定則）
                for (int j = 0; j < res; j++)
                    for (int i = 0; i < res; i++)
                    {
                        if (n == 4)
                        {
                            AddTri(Tri, Gl(i, j), Gl(i + 1, j), Gl(i + 1, j + 1));
                            AddTri(Tri, Gl(i, j), Gl(i + 1, j + 1), Gl(i, j + 1));
                        }
                        else
                        {
                            if (i + j < res) AddTri(Tri, Gl(i, j), Gl(i + 1, j), Gl(i, j + 1));
                            if (i + j < res - 1) AddTri(Tri, Gl(i + 1, j), Gl(i + 1, j + 1), Gl(i, j + 1));
                        }
                    }
                // 距離約束：結構（相鄰）、剪切（對角）、彎曲（隔一格）；靜止長度 ＝ 設計距離 × fullness
                void Con(int a, int b, double k)
                {
                    if (a < 0 || b < 0) return;
                    CA.Add(a); CB.Add(b); CK.Add(k);
                    CR.Add(Math.Sqrt(Sq(X[a * 3] - X[b * 3]) + Sq(X[a * 3 + 1] - X[b * 3 + 1]) + Sq(X[a * 3 + 2] - X[b * 3 + 2])) * s.fullness);
                }
                for (int j = 0; j <= res; j++)
                    for (int i = 0; i <= res; i++)
                    {
                        int a = Gl(i, j);
                        if (a < 0) continue;
                        Con(a, Gl(i + 1, j), 1); Con(a, Gl(i, j + 1), 1);
                        if (n == 4) { Con(a, Gl(i + 1, j + 1), 1); Con(Gl(i + 1, j), Gl(i, j + 1), 1); }
                        else Con(Gl(i + 1, j), Gl(i, j + 1), 1);
                        Con(a, Gl(i + 2, j), 0.2); Con(a, Gl(i, j + 2), 0.2);
                        if (n == 3) Con(Gl(i + 2, j), Gl(i, j + 2), 0.2);
                    }
                // 釘住：角（pin）與 bind 的邊（整條沿線段）
                var aCornerIj = n == 4 ? new[] { new[] { 0, 0 }, new[] { res, 0 }, new[] { res, res }, new[] { 0, res } } : new[] { new[] { 0, 0 }, new[] { res, 0 }, new[] { 0, res } };
                for (int k = 0; k < n; k++)
                {
                    int p = Gl(aCornerIj[k][0], aCornerIj[k][1]);
                    ioRig.m_CornerParticle[r.Path + "#" + k.ToString(CultureInfo.InvariantCulture)] = p;
                    ioRig.m_CornerDesign[r.Path + "#" + k.ToString(CultureInfo.InvariantCulture)] = C[k];
                    if (CornerPinned(s, k)) { W[p] = 0; SetPin(Pin, p, C[k]); }
                }
                foreach (int e in s.bind)
                {
                    int[] a0 = aCornerIj[e], a1 = aCornerIj[(e + 1) % n];
                    for (int t = 0; t <= res; t++)
                    {
                        int i = a0[0] + (a1[0] - a0[0]) * t / res, j = a0[1] + (a1[1] - a0[1]) * t / res;
                        int p = Gl(i, j);
                        double f = (double)t / res;
                        W[p] = 0;
                        SetPin(Pin, p, new[] { C[e][0] + (C[(e + 1) % n][0] - C[e][0]) * f, C[e][1] + (C[(e + 1) % n][1] - C[e][1]) * f, C[e][2] + (C[(e + 1) % n][2] - C[e][2]) * f });
                    }
                }
                // 質量 ＝ 相鄰三角形面積的三分之一（風是面積力，除以質量才是加速度 ⇒ 解析度變了，同樣的風吹出差不多的形狀）
                for (int k = t0 * 3; k < Tri.Count; k += 3)
                {
                    int a = Tri[k], b = Tri[k + 1], c = Tri[k + 2];
                    double ar = TriArea(X, a, b, c) / 3;
                    Mass[a] += ar; Mass[b] += ar; Mass[c] += ar;
                }
                ioRig.m_Range.Add(new[] { p0, W.Count - p0, t0, Tri.Count / 3 - t0 });
                r.Particles = W.Count - p0; r.Triangles = Tri.Count / 3 - t0; r.Resolution = res;
            }
            ioRig.m_N = W.Count;
            ioRig.m_X = X.ToArray(); ioRig.m_P = X.ToArray(); ioRig.m_V = new double[X.Count]; ioRig.m_Design = X.ToArray();
            ioRig.m_W = W.ToArray(); ioRig.m_Pin = Pin.ToArray(); ioRig.m_Mass = Mass.ToArray();
            for (int i = 0; i < ioRig.m_Mass.Length; i++) if (ioRig.m_Mass[i] < 1e-9) ioRig.m_Mass[i] = 1e-9;
            ioRig.m_CA = CA.ToArray(); ioRig.m_CB = CB.ToArray(); ioRig.m_CR = CR.ToArray(); ioRig.m_CK = CK.ToArray();
            ioRig.m_Tri = Tri.ToArray();
        }

        static void AddTri(List<int> ioTri, int a, int b, int c) { if (a >= 0 && b >= 0 && c >= 0) { ioTri.Add(a); ioTri.Add(b); ioTri.Add(c); } }
        static void SetPin(List<double> ioPin, int p, double[] q) { ioPin[p * 3] = q[0]; ioPin[p * 3 + 1] = q[1]; ioPin[p * 3 + 2] = q[2]; }
        static double Sq(double x) => x * x;

        static double TriArea(List<double> X, int a, int b, int c)
        {
            double e1x = X[b * 3] - X[a * 3], e1y = X[b * 3 + 1] - X[a * 3 + 1], e1z = X[b * 3 + 2] - X[a * 3 + 2];
            double e2x = X[c * 3] - X[a * 3], e2y = X[c * 3 + 1] - X[a * 3 + 1], e2z = X[c * 3 + 2] - X[a * 3 + 2];
            double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
            return Math.Sqrt(nx * nx + ny * ny + nz * nz) / 2;
        }

        /// <summary>四邊形雙線性：(0,0)＝角0、(1,0)＝角1、(1,1)＝角2、(0,1)＝角3。</summary>
        static double[] Lerp4(double[][] C, double u, double v)
        {
            var o = new double[3];
            for (int d = 0; d < 3; d++) o[d] = (1 - u) * (1 - v) * C[0][d] + u * (1 - v) * C[1][d] + u * v * C[2][d] + (1 - u) * v * C[3][d];
            return o;
        }

        static double Dist(double[] a, double[] b) => Math.Sqrt(Sq(b[0] - a[0]) + Sq(b[1] - a[1]) + Sq(b[2] - a[2]));
        static double Norm(double[] v) => Math.Sqrt(v[0] * v[0] + v[1] * v[1] + v[2] * v[2]);

        /// <summary>角依序繞一圈的平面法線（Newell 法，單位化；圍不出面 ⇒ 零向量）。</summary>
        public static double[] PlaneNormal(double[]?[] iCorners)
        {
            double nx = 0, ny = 0, nz = 0;
            int n = iCorners.Length;
            for (int k = 0; k < n; k++)
            {
                double[] a = iCorners[k]!, b = iCorners[(k + 1) % n]!;
                nx += (a[1] - b[1]) * (a[2] + b[2]);
                ny += (a[2] - b[2]) * (a[0] + b[0]);
                nz += (a[0] - b[0]) * (a[1] + b[1]);
            }
            double l = Math.Sqrt(nx * nx + ny * ny + nz * nz);
            return l < 1e-9 ? new double[3] : new[] { nx / l, ny / l, nz / l };
        }

        public static double[] Centroid(double[]?[] iCorners)
        {
            var c = new double[3];
            foreach (double[]? p in iCorners) { c[0] += p![0]; c[1] += p[1]; c[2] += p[2]; }
            c[0] /= iCorners.Length; c[1] /= iCorners.Length; c[2] /= iCorners.Length;
            return c;
        }

        /// <summary>每頂點法線：相鄰三角形（照繞序）的面積加權平均、單位化。</summary>
        public static double[] VertexNormals(double[] iPts, int[] iTri)
        {
            var aN = new double[iPts.Length];
            for (int k = 0; k < iTri.Length; k += 3)
            {
                int a = iTri[k], b = iTri[k + 1], c = iTri[k + 2];
                double e1x = iPts[b * 3] - iPts[a * 3], e1y = iPts[b * 3 + 1] - iPts[a * 3 + 1], e1z = iPts[b * 3 + 2] - iPts[a * 3 + 2];
                double e2x = iPts[c * 3] - iPts[a * 3], e2y = iPts[c * 3 + 1] - iPts[a * 3 + 1], e2z = iPts[c * 3 + 2] - iPts[a * 3 + 2];
                double nx = e1y * e2z - e1z * e2y, ny = e1z * e2x - e1x * e2z, nz = e1x * e2y - e1y * e2x;
                aN[a * 3] += nx; aN[a * 3 + 1] += ny; aN[a * 3 + 2] += nz;
                aN[b * 3] += nx; aN[b * 3 + 1] += ny; aN[b * 3 + 2] += nz;
                aN[c * 3] += nx; aN[c * 3 + 1] += ny; aN[c * 3 + 2] += nz;
            }
            for (int i = 0; i < aN.Length; i += 3)
            {
                double l = Math.Sqrt(aN[i] * aN[i] + aN[i + 1] * aN[i + 1] + aN[i + 2] * aN[i + 2]);
                if (l > 1e-12) { aN[i] /= l; aN[i + 1] /= l; aN[i + 2] /= l; } else aN[i + 2] = 1;
            }
            return aN;
        }

        // ───────────────────────── 雙面網格（渲染器與 obj 匯出共用）─────────────────────────

        /// <summary>
        /// 布是薄的、兩面都要看得到 ⇒ 每個頂點兩份：正面往法線推 <see cref="HalfThickness"/>、法線不變；背面往反方向推、法線取反、三角形繞序反過來。
        /// 輸出的三角形一律「從它的法線那側看是逆時針（世界右手系）」—— 跟 <see cref="SCP_SculptRopes.Tube"/> 的四邊形同一個約定。
        /// </summary>
        public static void TwoSided(SCP_SculptSailGeom iSail, out List<double[]> oPos, out List<double[]> oNormal, out List<int[]> oTris)
        {
            oPos = new List<double[]>(); oNormal = new List<double[]>(); oTris = new List<int[]>();
            int n = iSail.PointCount;
            for (int side = 0; side < 2; side++)
            {
                double sg = side == 0 ? 1 : -1;
                for (int i = 0; i < n; i++)
                {
                    double nx = iSail.Normals[i * 3] * sg, ny = iSail.Normals[i * 3 + 1] * sg, nz = iSail.Normals[i * 3 + 2] * sg;
                    oNormal.Add(new[] { nx, ny, nz });
                    oPos.Add(new[] { iSail.Points[i * 3] + nx * HalfThickness, iSail.Points[i * 3 + 1] + ny * HalfThickness, iSail.Points[i * 3 + 2] + nz * HalfThickness });
                }
            }
            for (int k = 0; k < iSail.Triangles.Length; k += 3)
            {
                int a = iSail.Triangles[k], b = iSail.Triangles[k + 1], c = iSail.Triangles[k + 2];
                oTris.Add(new[] { a, b, c });
                oTris.Add(new[] { n + a, n + c, n + b });
            }
        }

        /// <summary>點（組裝件座標）或向量給人讀：兩位小數。</summary>
        public static string Fmt2(double[]? iP)
            => iP == null ? "—" : iP[0].ToString("0.##", CultureInfo.InvariantCulture) + "," + iP[1].ToString("0.##", CultureInfo.InvariantCulture) + "," + iP[2].ToString("0.##", CultureInfo.InvariantCulture);
    }
}
