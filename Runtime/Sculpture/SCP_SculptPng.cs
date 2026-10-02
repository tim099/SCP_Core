// 區塊職責：PNG → RGBA8 解碼器（stampimg 的入口）＋ Pillow 相容的最近鄰縮放。零影像套件、零 NuGet。TASK-0377。
// 物理意義：python 那側是 `Image.open(p).convert("RGBA")`（Pillow）＋ `resize(…, NEAREST)`。
//          ⭐ 對拍的對象是 **Pillow 的行為**，不是 PNG 規格書 —— 兩者有三處不同，這裡一律照 Pillow：
//            ① 16-bit 彩色／LA／RGBA：取**高位元組**（截斷，不是四捨五入）。
//            ② 16-bit 灰階：Pillow 開成 I;16，轉 RGBA 時**夾到 255**（不是縮放）⇒ 亮於 255 的全變白。
//            ③ tRNS 色鍵：Pillow 存的是**原始取樣值**，卻拿去比**轉成 8-bit 之後**的像素
//               ⇒ 1/2/4-bit 灰階與 16-bit 的色鍵幾乎永遠對不上（實際上不透明）。
//          🔬 量出來的不是推的（TASK-0377，2026-10-02，Pillow 12.2）：44 張 PNG（五種 color type × 全部合法 bit depth、
//            有／無 tRNS、隨機 filter 0-4、PIL 自存的 P/L/LA/1/RGB+key、真實雕刻檔）解碼與 PIL `convert("RGBA")` 逐位元組相同 44/44；
//            最近鄰縮放 220/220 相同（含放大、縮小、非整數比、1x1）。
// 數值影響：支援 color type 0/2/3/4/6、bit depth 1/2/4/8/16、filter 0-4；
//          ⛔ 不支援 Adam7 交錯（Pillow 支援）—— 大聲拒絕，不靜默解成錯的圖。
//          非 IDAT 的 chunk 驗 CRC（Pillow 也驗；IDAT 它不驗，這裡也不驗）。未知 chunk 略過。
// 失敗處置：一律丟 <see cref="SCP_SculptPngException"/>（訊息是人話），呼叫端轉成「❌ 讀圖失敗」。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Canvas;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptPngException : Exception
    {
        public SCP_SculptPngException(string iMessage) : base(iMessage) { }
    }

    public static class SCP_SculptPng
    {
        /// <summary>Pillow 的 decompression-bomb 硬上限（MAX_IMAGE_PIXELS × 2）—— 超過它 PIL 直接拒絕。</summary>
        public const long BombPixels = 2L * 89478485;

        /// <summary>PNG bytes → 由上到下逐列的 RGBA8（語意＝ Pillow <c>convert("RGBA")</c>）。</summary>
        public static byte[] DecodeRgba(byte[] iPng, out int oWidth, out int oHeight)
        {
            oWidth = oHeight = 0;
            if (iPng == null || iPng.Length < 8 || iPng[0] != 0x89 || iPng[1] != 0x50 || iPng[2] != 0x4E || iPng[3] != 0x47
                || iPng[4] != 0x0D || iPng[5] != 0x0A || iPng[6] != 0x1A || iPng[7] != 0x0A)
                throw new SCP_SculptPngException("不是 PNG（簽章不符）");

            int aPos = 8;
            int aW = 0, aH = 0, aDepth = 0, aCt = -1, aInterlace = 0;
            byte[]? aPlte = null, aTrns = null;
            var aIdat = new MemoryStream();
            bool aSeenIhdr = false, aSeenIend = false;
            while (aPos + 8 <= iPng.Length)
            {
                uint aLen = ReadBe(iPng, aPos);
                string aType = new string(new[] { (char)iPng[aPos + 4], (char)iPng[aPos + 5], (char)iPng[aPos + 6], (char)iPng[aPos + 7] });
                if (aLen > int.MaxValue || aPos + 12 + (long)aLen > iPng.Length)
                    throw new SCP_SculptPngException("PNG 被截斷（chunk " + aType + " 超出檔尾）");
                int aData = aPos + 8;
                int aL = (int)aLen;
                if (aType != "IDAT")
                {
                    var aTypeBytes = new byte[4];
                    Buffer.BlockCopy(iPng, aPos + 4, aTypeBytes, 0, 4);
                    var aBody = new byte[aL];
                    Buffer.BlockCopy(iPng, aData, aBody, 0, aL);
                    if (SCP_CanvasDeflate.Crc32(aTypeBytes, aBody) != ReadBe(iPng, aData + aL))
                        throw new SCP_SculptPngException("PNG 損毀（chunk " + aType + " 的 CRC 不符）");
                }
                if (!aSeenIhdr && aType != "IHDR") throw new SCP_SculptPngException("PNG 第一個 chunk 不是 IHDR");
                switch (aType)
                {
                    case "IHDR":
                        if (aL != 13) throw new SCP_SculptPngException("IHDR 長度不是 13");
                        aW = (int)Math.Min(int.MaxValue, ReadBe(iPng, aData));
                        aH = (int)Math.Min(int.MaxValue, ReadBe(iPng, aData + 4));
                        aDepth = iPng[aData + 8];
                        aCt = iPng[aData + 9];
                        if (iPng[aData + 10] != 0 || iPng[aData + 11] != 0)
                            throw new SCP_SculptPngException("不支援的壓縮／濾波方法");
                        aInterlace = iPng[aData + 12];
                        aSeenIhdr = true;
                        break;
                    case "PLTE":
                        aPlte = new byte[aL];
                        Buffer.BlockCopy(iPng, aData, aPlte, 0, aL);
                        break;
                    case "tRNS":
                        aTrns = new byte[aL];
                        Buffer.BlockCopy(iPng, aData, aTrns, 0, aL);
                        break;
                    case "IDAT":
                        aIdat.Write(iPng, aData, aL);
                        break;
                    case "IEND":
                        aSeenIend = true;
                        break;
                }
                aPos = aData + aL + 4;
                if (aSeenIend) break;
            }
            if (!aSeenIhdr) throw new SCP_SculptPngException("PNG 沒有 IHDR");
            if (aW <= 0 || aH <= 0) throw new SCP_SculptPngException("PNG 寬高非法：" + aW + "x" + aH);
            if (aInterlace != 0)
                throw new SCP_SculptPngException("不支援 Adam7 交錯 PNG（interlace=1）—— 先用影像工具另存成非交錯再貼");
            int aChannels;
            switch (aCt)
            {
                case 0: aChannels = 1; if (aDepth != 1 && aDepth != 2 && aDepth != 4 && aDepth != 8 && aDepth != 16) goto badDepth; break;
                case 2: aChannels = 3; if (aDepth != 8 && aDepth != 16) goto badDepth; break;
                case 3: aChannels = 1; if (aDepth != 1 && aDepth != 2 && aDepth != 4 && aDepth != 8) goto badDepth; break;
                case 4: aChannels = 2; if (aDepth != 8 && aDepth != 16) goto badDepth; break;
                case 6: aChannels = 4; if (aDepth != 8 && aDepth != 16) goto badDepth; break;
                default: throw new SCP_SculptPngException("不支援的 color type " + aCt);
            }
            if ((long)aW * aH > BombPixels)
                throw new SCP_SculptPngException("圖片像素數 " + ((long)aW * aH) + " 超過上限（decompression bomb 保護）");
            if (aCt == 3 && aPlte == null) throw new SCP_SculptPngException("palette PNG 缺 PLTE");
            if (aIdat.Length == 0) throw new SCP_SculptPngException("PNG 沒有 IDAT");

            int aBitsPerPixel = aChannels * aDepth;
            long aStrideL = ((long)aW * aBitsPerPixel + 7) / 8;
            if ((aStrideL + 1) * aH > int.MaxValue) throw new SCP_SculptPngException("PNG 太大");
            int aStride = (int)aStrideL;
            int aBpp = Math.Max(1, aBitsPerPixel / 8);

            byte[] aRaw;
            try { aRaw = SCP_CanvasDeflate.ZlibDecompress(aIdat.ToArray()); }
            catch (Exception e) { throw new SCP_SculptPngException("IDAT 解壓失敗：" + e.Message); }
            if (aRaw.Length < (aStride + 1) * aH)
                throw new SCP_SculptPngException("PNG 被截斷（像素資料不足：" + aRaw.Length + " < " + ((aStride + 1) * aH) + "）");

            // ── 反濾波（就地）──
            var aPrev = new byte[aStride];
            var aCur = new byte[aStride];
            var aRgba = new byte[(long)aW * aH * 4];
            // tRNS 的色鍵（Pillow 存原始取樣值；與「轉成 8-bit 後」的像素比 —— 見檔頭③）
            bool aHasKey = false;
            int aKr = 0, aKg = 0, aKb = 0;
            if (aTrns != null && (aCt == 0 || aCt == 2))
            {
                if (aCt == 0 && aTrns.Length >= 2) { aHasKey = true; aKr = aKg = aKb = (aTrns[0] << 8) | aTrns[1]; }
                if (aCt == 2 && aTrns.Length >= 6)
                {
                    aHasKey = true;
                    aKr = (aTrns[0] << 8) | aTrns[1]; aKg = (aTrns[2] << 8) | aTrns[3]; aKb = (aTrns[4] << 8) | aTrns[5];
                }
            }
            for (int y = 0; y < aH; y++)
            {
                int aRow = y * (aStride + 1);
                byte aFilter = aRaw[aRow];
                Buffer.BlockCopy(aRaw, aRow + 1, aCur, 0, aStride);
                Unfilter(aFilter, aCur, aPrev, aBpp, y);
                ExpandRow(aCur, aRgba, y * aW * 4, aW, aCt, aDepth, aPlte, aTrns, aHasKey, aKr, aKg, aKb);
                var t = aPrev; aPrev = aCur; aCur = t;
            }
            oWidth = aW;
            oHeight = aH;
            return aRgba;

        badDepth:
            throw new SCP_SculptPngException("color type " + aCt + " 不支援 bit depth " + aDepth);
        }

        static void Unfilter(byte iFilter, byte[] ioCur, byte[] iPrev, int iBpp, int iRow)
        {
            int n = ioCur.Length;
            switch (iFilter)
            {
                case 0: return;
                case 1:
                    for (int i = iBpp; i < n; i++) ioCur[i] = (byte)(ioCur[i] + ioCur[i - iBpp]);
                    return;
                case 2:
                    for (int i = 0; i < n; i++) ioCur[i] = (byte)(ioCur[i] + iPrev[i]);
                    return;
                case 3:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= iBpp ? ioCur[i - iBpp] : 0;
                        ioCur[i] = (byte)(ioCur[i] + ((a + iPrev[i]) >> 1));
                    }
                    return;
                case 4:
                    for (int i = 0; i < n; i++)
                    {
                        int a = i >= iBpp ? ioCur[i - iBpp] : 0;
                        int b = iPrev[i];
                        int c = i >= iBpp ? iPrev[i - iBpp] : 0;
                        int p = a + b - c;
                        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        int aPred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                        ioCur[i] = (byte)(ioCur[i] + aPred);
                    }
                    return;
                default:
                    throw new SCP_SculptPngException("第 " + iRow + " 列的 filter type " + iFilter + " 非法");
            }
        }

        static int Sample(byte[] iRow, int iIndex, int iDepth)
        {
            switch (iDepth)
            {
                case 8: return iRow[iIndex];
                case 16: return (iRow[iIndex * 2] << 8) | iRow[iIndex * 2 + 1];
                default:
                {
                    int aBit = iIndex * iDepth;
                    int aByte = iRow[aBit >> 3];
                    int aShift = 8 - iDepth - (aBit & 7);
                    return (aByte >> aShift) & ((1 << iDepth) - 1);
                }
            }
        }

        static void ExpandRow(byte[] iRow, byte[] oRgba, int iOut, int iW, int iCt, int iDepth,
                              byte[]? iPlte, byte[]? iTrns, bool iHasKey, int iKr, int iKg, int iKb)
        {
            for (int x = 0; x < iW; x++)
            {
                int r, g, b, a = 255;
                switch (iCt)
                {
                    case 0:
                    {
                        int v = Sample(iRow, x, iDepth);
                        int l;
                        if (iDepth == 16) l = Math.Min(v, 255);          // Pillow I;16 → RGB：夾，不縮放
                        else if (iDepth == 8) l = v;
                        else l = v * 255 / ((1 << iDepth) - 1);           // 1→×255、2→×85、4→×17
                        r = g = b = l;
                        break;
                    }
                    case 2:
                        if (iDepth == 16) { r = iRow[x * 6]; g = iRow[x * 6 + 2]; b = iRow[x * 6 + 4]; }   // 高位元組
                        else { r = iRow[x * 3]; g = iRow[x * 3 + 1]; b = iRow[x * 3 + 2]; }
                        break;
                    case 3:
                    {
                        int i = Sample(iRow, x, iDepth);
                        if (iPlte != null && i * 3 + 2 < iPlte.Length) { r = iPlte[i * 3]; g = iPlte[i * 3 + 1]; b = iPlte[i * 3 + 2]; }
                        else { r = g = b = 0; }
                        if (iTrns != null && i < iTrns.Length) a = iTrns[i];
                        break;
                    }
                    case 4:
                        if (iDepth == 16) { r = g = b = iRow[x * 4]; a = iRow[x * 4 + 2]; }
                        else { r = g = b = iRow[x * 2]; a = iRow[x * 2 + 1]; }
                        break;
                    default: // 6
                        if (iDepth == 16) { r = iRow[x * 8]; g = iRow[x * 8 + 2]; b = iRow[x * 8 + 4]; a = iRow[x * 8 + 6]; }
                        else { r = iRow[x * 4]; g = iRow[x * 4 + 1]; b = iRow[x * 4 + 2]; a = iRow[x * 4 + 3]; }
                        break;
                }
                if (iHasKey && r == iKr && g == iKg && b == iKb) a = 0;
                int o = iOut + x * 4;
                oRgba[o] = (byte)r; oRgba[o + 1] = (byte)g; oRgba[o + 2] = (byte)b; oRgba[o + 3] = (byte)a;
            }
        }

        /// <summary>
        /// Pillow <c>resize((w, h), NEAREST)</c> 的逐位複刻（ImagingScaleAffine）：
        /// 取樣點 <c>xo = a·0.5</c> 起、每格**累加** <c>a = 原寬/新寬</c>（double），取 <c>(int)xo</c>；y 同理。
        /// <para>⚠ 是累加不是 <c>(x+0.5)·a</c> 直接算 —— 兩者在某些比例下差一格（浮點誤差累積的方向不同）。</para>
        /// </summary>
        public static byte[] ResizeNearest(byte[] iRgba, int iW, int iH, int iNewW, int iNewH)
        {
            if (iNewW == iW && iNewH == iH) return (byte[])iRgba.Clone();
            var aOut = new byte[(long)iNewW * iNewH * 4];
            double aSx = (double)iW / iNewW, aSy = (double)iH / iNewH;
            var aXin = new int[iNewW];
            int aXmin = iNewW, aXmax = 0;
            double xo = aSx * 0.5;
            for (int x = 0; x < iNewW; x++)
            {
                int xin = xo < 0.0 ? -1 : (int)xo;
                if (xin >= 0 && xin < iW) { aXmax = x + 1; if (x < aXmin) aXmin = x; aXin[x] = xin; }
                xo += aSx;
            }
            double yo = aSy * 0.5;
            for (int y = 0; y < iNewH; y++)
            {
                int yi = yo < 0.0 ? -1 : (int)yo;
                if (yi >= 0 && yi < iH)
                {
                    long aIn = (long)yi * iW * 4, aDst = (long)y * iNewW * 4;
                    for (int x = aXmin; x < aXmax; x++)
                        Buffer.BlockCopy(iRgba, (int)(aIn + aXin[x] * 4L), aOut, (int)(aDst + x * 4L), 4);
                }
                yo += aSy;
            }
            return aOut;
        }

        static uint ReadBe(byte[] iB, int iAt)
            => ((uint)iB[iAt] << 24) | ((uint)iB[iAt + 1] << 16) | ((uint)iB[iAt + 2] << 8) | iB[iAt + 3];

        /// <summary>
        /// python <c>png_to_painted()</c>：解碼 →（可選）最近鄰縮放 → 16M 像素上限 → 逐像素 alpha 門檻 → RGB332 量化。
        /// <para>回傳 (u, v, color_index)，u/v 相對左上角；alpha &lt; 門檻＝沒畫過＝不放。</para>
        /// </summary>
        public static List<(int U, int V, int Color)> ToPainted(byte[] iPng, int iAlphaThreshold, int iResizeW, int iResizeH,
                                                                 out int oWidth, out int oHeight)
        {
            byte[] aRgba = DecodeRgba(iPng, out int aW, out int aH);
            if (iResizeW > 0 && iResizeH > 0)
            {
                aRgba = ResizeNearest(aRgba, aW, aH, iResizeW, iResizeH);
                aW = iResizeW; aH = iResizeH;
            }
            if ((long)aW * aH > 16000000)
                throw new SCP_SculptPngException("圖片過大 " + aW + "x" + aH + "（上限 16,000,000 像素）—— 先用 --resize 縮小");
            var aOut = new List<(int, int, int)>();
            int aN = aW * aH;
            for (int px = 0; px < aN; px++)
            {
                int i = px * 4;
                if (aRgba[i + 3] < iAlphaThreshold) continue;   // 透明 = 沒畫過 = 不存在（不是「白色」）
                aOut.Add((px % aW, px / aW, SCP_CanvasPalette.RgbToIndex(aRgba[i], aRgba[i + 1], aRgba[i + 2])));
            }
            oWidth = aW;
            oHeight = aH;
            return aOut;
        }
    }
}
