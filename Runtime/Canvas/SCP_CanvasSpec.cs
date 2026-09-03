// 區塊職責：共用像素畫布的**幾何與空白約定** —— 這幾個數字是跨語言契約，不是實作細節。
// 物理意義：2048×2048 一格一 byte 的 palette index（index-map），底色 255。
//           ⚠ 255 同時是「純白」與「沒有人畫過」 —— 兩者在色值上同形，
//           分得出來的只有 painted-mask。所以任何「畫過沒有」的判定一律問 mask，不看顏色。
// 數值影響：Width*Height = 4,194,304 ⇒ buffer 與 mask 各 4 MiB。
// 設計取捨：這三個常數與 python `canvas.py` 的 CANVAS_W / CANVAS_H / BLANK_INDEX **必須逐字同值**；
//           兩端並存期間任何一邊改動＝畫布資料立刻分岔（events 是 append-only，改不回來）。
//           ⇒ 要改的話是先改契約文件、兩端一起動，不是在這裡調一個數字。
namespace SCP.Core.Canvas
{
    public static class SCP_CanvasSpec
    {
        /// <summary>畫布寬（像素）。</summary>
        public const int Width = 2048;

        /// <summary>畫布高（像素）。</summary>
        public const int Height = 2048;

        /// <summary>總格數 ＝ <see cref="Width"/> * <see cref="Height"/>。</summary>
        public const int Area = Width * Height;

        /// <summary>空白底色的 palette index（＝純白；也是「沒畫過」的色值，靠 mask 分辨）。</summary>
        public const byte BlankIndex = 255;

        /// <summary>座標是否落在畫布內。</summary>
        public static bool InBounds(int iX, int iY)
        {
            return iX >= 0 && iX < Width && iY >= 0 && iY < Height;
        }
    }
}
