// 區塊職責：renderer 無關的顏色（0..1 的 RGBA）。
// 物理意義：共用層刻意**不認識**任何繪圖庫的顏色型別 ——
//           碰了 `UnityEngine.Color` 就搬不進 .NET 端，碰了 `System.Numerics.Vector4`
//           在 Unity 那側又要多一層轉換。⇒ 自己一顆四個 float，兩邊各自在邊界轉。
//           取名 SCP_Color（不叫 SCP_GuiColor）是因為顏色不只 UI 會用到：
//           log 上色、資料視覺化、匯出格式都吃同一個概念，綁在 Gui 名字上會逼下一個人再造一顆。
// 數值影響：純值型別，零 IO。分量**不 clamp** —— 顏色的合法範圍隨用途不同
//           （HDR／相加混色會超過 1），要夾由消費端依自己的規則夾。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）—— 不用 record struct。
#nullable enable
using System.Globalization;

namespace SCP.Core
{
    /// <summary>RGBA 顏色（各分量 0..1）。</summary>
    public struct SCP_Color
    {
        public float R;
        public float G;
        public float B;
        public float A;

        public SCP_Color(float iR, float iG, float iB, float iA = 1f)
        {
            R = iR; G = iG; B = iB; A = iA;
        }

        /// <summary>灰階捷徑。</summary>
        public static SCP_Color Gray(float iValue, float iAlpha = 1f)
            => new SCP_Color(iValue, iValue, iValue, iAlpha);

        public static SCP_Color White => new SCP_Color(1f, 1f, 1f);
        public static SCP_Color Black => new SCP_Color(0f, 0f, 0f);

        /// <summary>
        /// 8-bit 十六進位（<c>#RRGGBB</c> / <c>#RRGGBBAA</c>，`#` 可省）。
        /// <para>⚠ 解析不了時回 false 並把 oColor 設成 <paramref name="iFallback"/> ——
        /// **不要靜默回黑色**：「解析失敗」與「使用者真的選了黑」不得同形。</para>
        /// </summary>
        public static bool TryParseHex(string? iHex, out SCP_Color oColor, SCP_Color iFallback = default)
        {
            oColor = iFallback;
            if (string.IsNullOrEmpty(iHex)) return false;

            string s = iHex!.Trim();
            if (s.Length > 0 && s[0] == '#') s = s.Substring(1);
            if (s.Length != 6 && s.Length != 8) return false;

            byte[] aBytes = new byte[s.Length / 2];
            for (int i = 0; i < aBytes.Length; i++)
            {
                if (!byte.TryParse(s.Substring(i * 2, 2), NumberStyles.HexNumber,
                                   CultureInfo.InvariantCulture, out byte b)) return false;
                aBytes[i] = b;
            }

            oColor = new SCP_Color(aBytes[0] / 255f, aBytes[1] / 255f, aBytes[2] / 255f,
                                   aBytes.Length == 4 ? aBytes[3] / 255f : 1f);
            return true;
        }

        /// <summary><c>#RRGGBB</c>（alpha ＜ 1 時輸出 <c>#RRGGBBAA</c>）。</summary>
        public string ToHex()
        {
            int r = To255(R), g = To255(G), b = To255(B), a = To255(A);
            return a >= 255
                ? string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", r, g, b)
                : string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}{3:X2}", r, g, b, a);
        }

        static int To255(float iValue)
        {
            int v = (int)System.Math.Round(iValue * 255f, System.MidpointRounding.AwayFromZero);
            return v < 0 ? 0 : (v > 255 ? 255 : v);
        }

        public override string ToString()
            => string.Format(CultureInfo.InvariantCulture, "rgba({0:0.##},{1:0.##},{2:0.##},{3:0.##})", R, G, B, A);
    }
}
