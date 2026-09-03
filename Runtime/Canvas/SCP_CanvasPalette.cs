// 區塊職責：RGB332 256 色調色盤 —— index ↔ RGB 的編解碼，以及色彩輸入的解析。
// 物理意義：r 3bit（高位）／g 3bit／b 2bit（低位），古早 8-bit 風格。
//           index 255 = (7,7,3) → #FFFFFF。
// 數值影響：解碼 r=((i>>5)&7)*255/7、g=((i>>2)&7)*255/7、b=(i&3)*255/3（整數除法，與 python 同式）；
//           編碼四捨五入分桶後打包 (rb<<5)|(gb<<2)|bb。
// 設計取捨：捨入用 <see cref="System.MidpointRounding.AwayFromZero"/>，而 python 的 round() 是
//           banker's rounding（half-to-even）—— 兩者**在本值域上零差異**，這句是量出來的不是推的：
//           🔬 2026-09-03 對 0..255 全枚舉比對 round() 與 floor(x+0.5)，7 級與 3 級各 0 筆不同。
//           成因：v*7/255 = k+0.5 需 v = 255(2k+1)/14，而 255 是奇數 ⇒ 永遠不是整數，
//           所以恰好落在半值上的輸入不存在。⇒ 不必在這裡模仿 banker's rounding。
// 🩸 ⚠ 別用接近白的顏色：#F0F0F0 會量化到 index 255，而 255 ＝「沒人畫過」的色值
//    ⇒ 扣了款、事件落盤、回讀卻是「空白」，三邊都不出錯誤訊息（basecamp 2026-08-19 實測）。
//    這不是本檔的 bug，是「白＝空白」在可覆蓋畫布上的必然邊界 —— 寫入端要自己擋。
using System;
using System.Globalization;

namespace SCP.Core.Canvas
{
    public static class SCP_CanvasPalette
    {
        /// <summary>index → RGB（RGB332 解碼）。</summary>
        public static void IndexToRgb(int iIndex, out byte oR, out byte oG, out byte oB)
        {
            int aI = iIndex & 0xFF;
            oR = (byte)(((aI >> 5) & 0x7) * 255 / 7);
            oG = (byte)(((aI >> 2) & 0x7) * 255 / 7);
            oB = (byte)((aI & 0x3) * 255 / 3);
        }

        /// <summary>RGB → 最近的 palette index（RGB332 量化）。</summary>
        public static int RgbToIndex(int iR, int iG, int iB)
        {
            int aRb = (int)Math.Round(iR / 255.0 * 7, MidpointRounding.AwayFromZero);
            int aGb = (int)Math.Round(iG / 255.0 * 7, MidpointRounding.AwayFromZero);
            int aBb = (int)Math.Round(iB / 255.0 * 3, MidpointRounding.AwayFromZero);
            return (aRb << 5) | (aGb << 2) | aBb;
        }

        /// <summary>index → <c>#RRGGBB</c>（人讀用）。</summary>
        public static string IndexToHex(int iIndex)
        {
            IndexToRgb(iIndex, out byte aR, out byte aG, out byte aB);
            return "#" + aR.ToString("X2") + aG.ToString("X2") + aB.ToString("X2");
        }

        /// <summary>
        /// 色彩輸入 → palette index。接受 palette index 0-255（數字或數字字串）與 <c>#RRGGBB</c>。
        /// <para>⚠ 失敗回 false 並給 <paramref name="oWhy"/> —— 呼叫端該整批拒絕，
        /// 不是替它挑一個看起來合理的顏色（挑中的那次會讓人以為它本來就懂）。</para>
        /// </summary>
        public static bool TryParse(string? iColor, out int oIndex, out string oWhy)
        {
            oIndex = 0;
            oWhy = "";
            string aS = (iColor ?? "").Trim();
            if (aS.Length == 0) { oWhy = "色彩是空的（需 0-255 或 #RRGGBB）"; return false; }

            if (aS[0] == '#')
            {
                string aHex = aS.Substring(1);
                if (aHex.Length != 6) { oWhy = "hex 色需 #RRGGBB 6 碼：" + aS; return false; }
                if (!TryHexByte(aHex, 0, out int aR) || !TryHexByte(aHex, 2, out int aG)
                    || !TryHexByte(aHex, 4, out int aB))
                { oWhy = "非法 hex 色：" + aS; return false; }
                oIndex = RgbToIndex(aR, aG, aB);
                return true;
            }

            if (!int.TryParse(aS, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aIdx))
            { oWhy = "無法解析色彩（需 0-255 或 #RRGGBB）：" + aS; return false; }
            if (aIdx < 0 || aIdx > 255) { oWhy = "palette index 越界 (0-255)：" + aS; return false; }
            oIndex = aIdx;
            return true;
        }

        static bool TryHexByte(string iHex, int iAt, out int oValue)
        {
            return int.TryParse(iHex.Substring(iAt, 2), NumberStyles.HexNumber,
                                CultureInfo.InvariantCulture, out oValue);
        }
    }
}
