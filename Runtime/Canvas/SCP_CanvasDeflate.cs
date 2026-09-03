// 區塊職責：zlib 容器（deflate ＋ 2 byte header ＋ adler32）與 PNG 用的 CRC32／big-endian 寫入。
// 物理意義：zlib 就是 raw deflate 前面加兩個 byte、後面接一個 adler32 檢查碼。
//           .NET 的 DeflateStream 只做中間那段 raw deflate，所以外層自己包。
// 數值影響：壓縮率與 python zlib **不必相同**（那是編碼器的自由），但產物要能被對方解開 ——
//           兩端互讀的實體證據見 SCP_Cmd_Canvas 的 cache 對拍。
// 設計取捨：⛔ 不用 System.IO.Compression.ZLibStream —— 它是 .NET 6+，而本專案釘 netstandard2.1
//           （Unity 也要編這份碼）。ZLibStream 在 .NET 這側編得過、在 Unity 那側直接沒有這個型別，
//           而那種錯要等到有人在 Unity 開專案才會現形。
//           🩸 同族血證：SCP_Core.csproj 有一道 MSBuild Error 守著「零 PackageReference」，
//           理由一樣 —— Unity 不吃 NuGet，共用碼碰了就是搬不進去。
using System;
using System.IO;
using System.IO.Compression;

namespace SCP.Core.Canvas
{
    public static class SCP_CanvasDeflate
    {
        /// <summary>raw bytes → zlib 容器（header 0x78 0x01 ＋ deflate ＋ adler32）。</summary>
        public static byte[] ZlibCompress(byte[] iRaw)
        {
            byte[] aDeflate;
            using (var aMs = new MemoryStream())
            {
                using (var aDs = new DeflateStream(aMs, CompressionLevel.Optimal, true))
                    aDs.Write(iRaw, 0, iRaw.Length);
                aDeflate = aMs.ToArray();
            }
            var aOut = new byte[aDeflate.Length + 6];
            aOut[0] = 0x78; aOut[1] = 0x01;
            Buffer.BlockCopy(aDeflate, 0, aOut, 2, aDeflate.Length);
            uint aAdler = Adler32(iRaw);
            WriteBe(aOut, aOut.Length - 4, aAdler);
            return aOut;
        }

        /// <summary>
        /// zlib 容器 → raw bytes。
        /// <para>⚠ 不驗 adler32：呼叫端（快取）本來就把「讀不出來」與「讀出來不對」當成同一件事
        /// —— 都退回全重建。多驗一次不會改變動作，只會讓失敗多一種形狀。</para>
        /// </summary>
        public static byte[] ZlibDecompress(byte[] iZlib)
        {
            if (iZlib == null || iZlib.Length < 6) throw new InvalidDataException("zlib blob 太短");
            using var aIn = new MemoryStream(iZlib, 2, iZlib.Length - 6, false);
            using var aDs = new DeflateStream(aIn, CompressionMode.Decompress);
            using var aOut = new MemoryStream();
            aDs.CopyTo(aOut);
            return aOut.ToArray();
        }

        public static void WriteBe(byte[] oBuffer, int iOffset, uint iValue)
        {
            oBuffer[iOffset] = (byte)(iValue >> 24);
            oBuffer[iOffset + 1] = (byte)(iValue >> 16);
            oBuffer[iOffset + 2] = (byte)(iValue >> 8);
            oBuffer[iOffset + 3] = (byte)iValue;
        }

        public static uint Adler32(byte[] iData)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < iData.Length; i++)
            {
                a = (a + iData[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        static readonly uint[] s_CrcTable = BuildCrcTable();

        static uint[] BuildCrcTable()
        {
            var aTable = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                aTable[n] = c;
            }
            return aTable;
        }

        /// <summary>PNG chunk 的 CRC32（type 與 data 連續計算）。</summary>
        public static uint Crc32(byte[] iA, byte[] iB)
        {
            uint c = 0xFFFFFFFFu;
            for (int i = 0; i < iA.Length; i++) c = s_CrcTable[(c ^ iA[i]) & 0xFF] ^ (c >> 8);
            for (int i = 0; i < iB.Length; i++) c = s_CrcTable[(c ^ iB[i]) & 0xFF] ^ (c >> 8);
            return c ^ 0xFFFFFFFFu;
        }
    }
}
