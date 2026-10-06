// 區塊職責：共用像素畫布的**預設幾何與空白約定**。
// 物理意義：一格一 byte 的 palette index（index-map），底色 255；預設 2048×2048。
//           ⭐ TASK-0445：尺寸改成參數化（`Canvas/canvas_settings.json`，見 SCP_CanvasSize）——
//           這裡只剩**預設值**與上限；實際尺寸一律問 snapshot／SCP_CanvasSettings.Resolve，⛔ 不再有全域的 Width／Height。
//           ⚠ 255 同時是「純白」與「沒有人畫過」 —— 兩者在色值上同形，
//           分得出來的只有 painted-mask。所以任何「畫過沒有」的判定一律問 mask，不看顏色。
// 數值影響：預設 2048×2048 ⇒ buffer 與 mask 各 4 MiB；上限 MaxSide² ⇒ 各 64 MiB。
// 設計取捨：python `canvas.py` 已於 2026-09-07 刪除，「兩端逐字同值」那條限制已經沒有對象。
//           BlankIndex 仍是磁碟格式的一部分（事件與快取都靠它），⛔ 不能改。
namespace SCP.Core.Canvas
{
    public static class SCP_CanvasSpec
    {
        /// <summary>沒有設定檔時的寬（像素）。</summary>
        public const int DefaultWidth = 2048;

        /// <summary>沒有設定檔時的高（像素）。</summary>
        public const int DefaultHeight = 2048;

        /// <summary>單邊上限（buffer＋mask 各 MaxSide² bytes；也擋壞事件把已畫範圍撐到記憶體爆掉）。</summary>
        public const int MaxSide = 8192;

        /// <summary>空白底色的 palette index（＝純白；也是「沒畫過」的色值，靠 mask 分辨）。</summary>
        public const byte BlankIndex = 255;
    }
}
