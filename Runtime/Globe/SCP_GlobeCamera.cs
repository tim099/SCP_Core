// 區塊職責：球面的**透視鏡頭**（TASK-0470）—— GPU 預覽與 CPU 參考解共用的唯一一份鏡頭數學（double）。
// 物理意義：鏡頭在「中心經緯度」方向、離球心 Distance 個球半徑處，看向球心；右＝東、上＝北（與 ortho 預覽同一組基底）。
//          Zoom 1 ＝ 球的輪廓剛好切到垂直視角的上下緣；Zoom 越大離地表越近：Distance ＝ 1 ＋ (Distance₁ − 1)／Zoom。
//          像素 (px,py) 由上到下、由左到右；像素中心在 +0.5。射線與單位球的交點方向 ＝ 那個像素看到的地表。
// 數值影響：GPU（shader 用 float）照這裡的公式算；CPU 這邊用 double 算出**每個像素的格子索引**當參考解，
//          selftest 拿兩邊逐像素比對 ⇒ 公式只有這一份，shader 那份是它的翻譯，⛔ 不是另一個真相。
//          近／遠平面給未來的建築（深度緩衝）用：近 ＝ 鏡頭到地表距離的一半（夾在 1e-5 以上），遠 ＝ 球的背面再外推一點。
using System;

namespace SCP.Core.Globe
{
    public sealed class SCP_GlobeCamera
    {
        public const double DefaultFovDeg = 40;
        public const double MinZoom = 0.5, MaxZoom = 400;

        public double CenterLat = 23.7, CenterLon = 121.0;
        public double Zoom = 1;
        /// <summary>垂直視角（度）。</summary>
        public double FovDeg = DefaultFovDeg;
        public int Width = 720, Height = 720;

        /// <summary>Zoom 1 時鏡頭離球心幾個球半徑（球的輪廓剛好切到垂直視角）。</summary>
        public double DistanceAtZoom1 => 1.0 / Math.Sin(FovDeg * Math.PI / 360);

        /// <summary>鏡頭離球心幾個球半徑（&gt; 1）。</summary>
        public double Distance => 1 + (DistanceAtZoom1 - 1) / Zoom;

        public double TanHalfY => Math.Tan(FovDeg * Math.PI / 360);
        public double TanHalfX => TanHalfY * Width / Height;

        /// <summary>參數不合法 ⇒ 丟 SCP_GlobeException（⛔ 不夾成看起來合理的值）。</summary>
        public void Validate()
        {
            if (Width < 16 || Height < 16 || Width > SCP_GlobeRender.MaxOrthoSize || Height > SCP_GlobeRender.MaxOrthoSize)
                throw new SCP_GlobeException($"鏡頭畫面要在 16..{SCP_GlobeRender.MaxOrthoSize}：{Width}×{Height}");
            if (!(Zoom >= MinZoom && Zoom <= MaxZoom)) throw new SCP_GlobeException($"zoom 要在 {MinZoom}..{MaxZoom}：{Zoom}");
            if (!(FovDeg >= 10 && FovDeg <= 90)) throw new SCP_GlobeException("視角要在 10..90 度：" + FovDeg);
            if (double.IsNaN(CenterLat) || double.IsNaN(CenterLon) || double.IsInfinity(CenterLat) || double.IsInfinity(CenterLon))
                throw new SCP_GlobeException("中心經緯度不是數字");
        }

        /// <summary>鏡頭位置（世界座標，球心在原點）與三軸：右（東）、上（北）、前（看向球心）。</summary>
        public void Basis(double[] oEye, double[] oRight, double[] oUp, double[] oForward)
        {
            double la = Math.Max(-90, Math.Min(90, CenterLat)) * Math.PI / 180, lo = CenterLon * Math.PI / 180;
            double cx = Math.Cos(la) * Math.Cos(lo), cy = Math.Cos(la) * Math.Sin(lo), cz = Math.Sin(la);
            double d = Distance;
            oEye[0] = cx * d; oEye[1] = cy * d; oEye[2] = cz * d;
            oRight[0] = -Math.Sin(lo); oRight[1] = Math.Cos(lo); oRight[2] = 0;
            oUp[0] = -Math.Sin(la) * Math.Cos(lo); oUp[1] = -Math.Sin(la) * Math.Sin(lo); oUp[2] = Math.Cos(la);
            oForward[0] = -cx; oForward[1] = -cy; oForward[2] = -cz;
        }

        /// <summary>近／遠平面（鏡頭空間的距離）。</summary>
        public void NearFar(out double oNear, out double oFar)
        {
            double d = Distance;
            oNear = Math.Max(1e-5, (d - 1) * 0.5);
            oFar = d + 1.5;
        }

        /// <summary>
        /// 像素 (px,py)（整數索引，由上到下）看到的地表方向。沒打到球 ⇒ false。
        /// 交點用穩定式 t ＝ c／(−b＋√(b²−c))：鏡頭貼近地表時 −b−√… 會相消（GPU 那份同一式）。
        /// </summary>
        public bool PixelHit(int px, int py, double[] iEye, double[] iRight, double[] iUp, double[] iForward,
                             out double oX, out double oY, out double oZ)
        {
            double nx = 2.0 * (px + 0.5) / Width - 1, ny = 1 - 2.0 * (py + 0.5) / Height;
            double tx = TanHalfX, ty = TanHalfY;
            double dx = iForward[0] + iRight[0] * nx * tx + iUp[0] * ny * ty;
            double dy = iForward[1] + iRight[1] * nx * tx + iUp[1] * ny * ty;
            double dz = iForward[2] + iRight[2] * nx * tx + iUp[2] * ny * ty;
            double l = Math.Sqrt(dx * dx + dy * dy + dz * dz);
            dx /= l; dy /= l; dz /= l;
            double b = iEye[0] * dx + iEye[1] * dy + iEye[2] * dz;
            double c = iEye[0] * iEye[0] + iEye[1] * iEye[1] + iEye[2] * iEye[2] - 1;
            double disc = b * b - c;
            if (disc < 0 || b >= 0) { oX = oY = oZ = 0; return false; }
            double t = c / (-b + Math.Sqrt(disc));
            oX = iEye[0] + t * dx; oY = iEye[1] + t * dy; oZ = iEye[2] + t * dz;
            return true;
        }

        /// <summary>
        /// CPU 參考解：每個像素的格子索引（由上到下；沒打到球 ＝ −1）。GPU 對拍以它為準。
        /// </summary>
        public int[] CellIndices(SCP_GlobeGrid iGrid)
        {
            Validate();
            var e = new double[3]; var r = new double[3]; var u = new double[3]; var f = new double[3];
            Basis(e, r, u, f);
            var a = new int[Width * Height];
            for (int py = 0; py < Height; py++)
                for (int px = 0; px < Width; px++)
                    a[py * Width + px] = PixelHit(px, py, e, r, u, f, out double x, out double y, out double z) ? iGrid.DirToCell(x, y, z) : -1;
            return a;
        }
    }
}
