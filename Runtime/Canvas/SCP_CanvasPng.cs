// 區塊職責：index-map buffer（＋ painted-mask）→ PNG bytes。零影像套件、零 NuGet。
// 物理意義：PNG ＝ 8 byte 簽章 ＋ IHDR ＋ IDAT（zlib 包的掃描線）＋ IEND；
//           每條掃描線前面一個 filter type byte（這裡一律 0 ＝ None）。
// 數值影響：兩種輸出 —— RGB（color type 2，禁透明，下游預覽相容）與
//           RGBA（color type 6，未繪製 alpha 0；3D 轉繪吃的是這一張）。
//           ⚠ 未繪製與「故意畫的白」在 RGB 上同值，只有 alpha 分得出來 ⇒ 3D 端不可吃 RGB 那張。
// 設計取捨：filter 一律 None、不做 per-line filter 選擇 —— 檔案大一點，但編碼器可以被一眼讀完。
//           🩸 而這一格是①階段的驗收尺被我自己寫錯的地方（2026-09-03 開單時）：
//           我寫「python 與 C# 各 render 一次 ⇒ PNG 位元組零差異」，
//           而對面那張是 PIL 存的 —— **濾波器選擇與壓縮級別不同，位元組本來就不會一樣**。
//           ⇒ 那格紅了不代表移植錯，綠了才代表我不小心用了同一顆編碼器。
//           對的尺量的是**解碼後的像素**：buffer/mask 的原始位元組，以及對方解得開我的 PNG。
using System;
using System.IO;

namespace SCP.Core.Canvas
{
    public static class SCP_CanvasPng
    {
        /// <summary>
        /// index-map → RGB PNG（不透明）。<paramref name="iStride"/> 是來源每列的格數，
        /// 供裁切區塊直接餵進來（不必先複製出一張小圖）。
        /// </summary>
        public static byte[] EncodeRgb(byte[] iBuffer, int iX, int iY, int iWidth, int iHeight,
                                       int iStride, int iScale = 1)
        {
            int aScale = iScale < 1 ? 1 : iScale;
            int aW = iWidth * aScale, aH = iHeight * aScale;
            var aRaw = new byte[(aW * 3 + 1) * aH];
            int aDst = 0;
            for (int y = 0; y < aH; y++)
            {
                aRaw[aDst++] = 0;                                  // filter: None
                int aSrcRow = (iY + y / aScale) * iStride;
                for (int x = 0; x < aW; x++)
                {
                    byte aIdx = iBuffer[aSrcRow + iX + x / aScale];
                    SCP_CanvasPalette.IndexToRgb(aIdx, out byte aR, out byte aG, out byte aB);
                    aRaw[aDst++] = aR; aRaw[aDst++] = aG; aRaw[aDst++] = aB;
                }
            }
            return Assemble(aRaw, aW, aH, 2);
        }

        /// <summary>
        /// index-map ＋ painted-mask → RGBA PNG。mask 0 ⇒ alpha 0（未繪製），
        /// mask 非 0 ⇒ 不透明（含故意畫的白）。
        /// <para>⚠ 放大一律最近鄰（整數複製）—— 任何插值都會生出 0 與 255 之間的半透明邊，
        /// 而「畫過／沒畫過」是二值判定 ⇒ 插值等於製造畫布上不存在的像素。</para>
        /// </summary>
        public static byte[] EncodeRgba(byte[] iBuffer, byte[] iMask, int iX, int iY,
                                        int iWidth, int iHeight, int iStride, int iScale,
                                        out int oOpaquePixels)
        {
            int aScale = iScale < 1 ? 1 : iScale;
            int aW = iWidth * aScale, aH = iHeight * aScale;
            var aRaw = new byte[(aW * 4 + 1) * aH];
            int aDst = 0;
            int aOpaque = 0;
            for (int y = 0; y < aH; y++)
            {
                aRaw[aDst++] = 0;
                int aSrcRow = (iY + y / aScale) * iStride;
                for (int x = 0; x < aW; x++)
                {
                    int aPos = aSrcRow + iX + x / aScale;
                    if (iMask[aPos] != 0)
                    {
                        SCP_CanvasPalette.IndexToRgb(iBuffer[aPos], out byte aR, out byte aG, out byte aB);
                        aRaw[aDst++] = aR; aRaw[aDst++] = aG; aRaw[aDst++] = aB; aRaw[aDst++] = 255;
                        aOpaque++;
                    }
                    else
                    {
                        aRaw[aDst++] = 0; aRaw[aDst++] = 0; aRaw[aDst++] = 0; aRaw[aDst++] = 0;
                    }
                }
            }
            // 數值影響：oOpaquePixels 是**裁切與放大之後**數出來的 —— 它描述這個檔案，不描述意圖。
            oOpaquePixels = aOpaque;
            return Assemble(aRaw, aW, aH, 6);
        }

        /// <summary>RGBA bytes（左上原點、每列無 filter byte）→ PNG。給非畫布來源用（例：畫面截圖）。</summary>
        public static byte[] EncodeRgbaRows(byte[] iRgbaTopDown, int iWidth, int iHeight)
        {
            var aRaw = new byte[(iWidth * 4 + 1) * iHeight];
            int aDst = 0;
            for (int y = 0; y < iHeight; y++)
            {
                aRaw[aDst++] = 0;
                Buffer.BlockCopy(iRgbaTopDown, y * iWidth * 4, aRaw, aDst, iWidth * 4);
                aDst += iWidth * 4;
            }
            return Assemble(aRaw, iWidth, iHeight, 6);
        }

        static byte[] Assemble(byte[] iRaw, int iWidth, int iHeight, byte iColorType)
        {
            byte[] aZlib = SCP_CanvasDeflate.ZlibCompress(iRaw);
            using var aOut = new MemoryStream();
            aOut.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);

            var aIhdr = new byte[13];
            SCP_CanvasDeflate.WriteBe(aIhdr, 0, (uint)iWidth);
            SCP_CanvasDeflate.WriteBe(aIhdr, 4, (uint)iHeight);
            aIhdr[8] = 8;                 // bit depth
            aIhdr[9] = iColorType;        // 2 = RGB, 6 = RGBA
            aIhdr[10] = 0; aIhdr[11] = 0; aIhdr[12] = 0;
            WriteChunk(aOut, "IHDR", aIhdr);
            WriteChunk(aOut, "IDAT", aZlib);
            WriteChunk(aOut, "IEND", Array.Empty<byte>());
            return aOut.ToArray();
        }

        static void WriteChunk(Stream oStream, string iType, byte[] iData)
        {
            var aLen = new byte[4];
            SCP_CanvasDeflate.WriteBe(aLen, 0, (uint)iData.Length);
            oStream.Write(aLen, 0, 4);
            var aType = new byte[4];
            for (int i = 0; i < 4; i++) aType[i] = (byte)iType[i];
            oStream.Write(aType, 0, 4);
            oStream.Write(iData, 0, iData.Length);
            var aCrc = new byte[4];
            SCP_CanvasDeflate.WriteBe(aCrc, 0, SCP_CanvasDeflate.Crc32(aType, iData));
            oStream.Write(aCrc, 0, 4);
        }
    }
}
