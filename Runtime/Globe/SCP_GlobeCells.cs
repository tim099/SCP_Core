// 區塊職責：球面格子的**記憶體容器** —— 每格 24-bit RGB，按 256×256 分塊、只有畫過的分塊才配置。
// 物理意義：值 0（0x000000）＝沒畫過＝顯示底色；純黑由 SCP_GlobeCells.FromRgb 存成 0x000001（肉眼無差）。
//          整顆球 6×2048² 格若全配置要 75 MB；海水是底色、不是塗出來的 ⇒ 沒畫的分塊一個 byte 都不佔。
// 數值影響：Set 會記下髒分塊，快取只寫髒的那幾塊。
using System;
using System.Collections.Generic;

namespace SCP.Core.Globe
{
    public sealed class SCP_GlobeCells
    {
        public const int Empty = 0;
        public int N { get; }
        /// <summary>分塊邊長（N 小於 256 時就是 N）。</summary>
        public int Tile { get; }
        public int TilesPerSide => N / Tile;

        readonly Dictionary<int, byte[]> m_Tiles = new Dictionary<int, byte[]>();
        readonly HashSet<int> m_Dirty = new HashSet<int>();

        public SCP_GlobeCells(int iN)
        {
            N = iN;
            Tile = Math.Min(256, iN);
        }

        /// <summary>使用者給的 RGB → 格子值（純黑重映成 0x000001，好跟「沒畫過」分開）。</summary>
        public static int FromRgb(int r, int g, int b)
        {
            int v = (r << 16) | (g << 8) | b;
            return v == Empty ? 1 : v;
        }

        public static string ToHex(int iValue) => "#" + iValue.ToString("X6");

        void Locate(int iIndex, out int oKey, out int oOffset)
        {
            int nn = N * N;
            int f = iIndex / nn, r = iIndex - f * nn, j = r / N, i = r - j * N;
            int tps = TilesPerSide;
            oKey = (f * tps + j / Tile) * tps + i / Tile;
            oOffset = ((j % Tile) * Tile + (i % Tile)) * 3;
        }

        public int Get(int iIndex)
        {
            Locate(iIndex, out int k, out int o);
            if (!m_Tiles.TryGetValue(k, out byte[]? t)) return Empty;
            return (t[o] << 16) | (t[o + 1] << 8) | t[o + 2];
        }

        public void Set(int iIndex, int iValue)
        {
            Locate(iIndex, out int k, out int o);
            if (!m_Tiles.TryGetValue(k, out byte[]? t))
            {
                if (iValue == Empty) return;
                t = new byte[Tile * Tile * 3];
                m_Tiles[k] = t;
            }
            t[o] = (byte)(iValue >> 16); t[o + 1] = (byte)(iValue >> 8); t[o + 2] = (byte)iValue;
            m_Dirty.Add(k);
        }

        public int CellCount => 6 * N * N;
        public IEnumerable<int> TileKeys => m_Tiles.Keys;
        public byte[] TileBytes(int iKey) => m_Tiles[iKey];
        public IReadOnlyCollection<int> DirtyTiles => m_Dirty;
        public void ClearDirty() => m_Dirty.Clear();

        public void LoadTile(int iKey, byte[] iBytes)
        {
            if (iBytes.Length != Tile * Tile * 3) throw new SCP_GlobeException($"分塊 {iKey} 大小不對：{iBytes.Length}");
            m_Tiles[iKey] = iBytes;
        }

        public int PaintedCount()
        {
            int n = 0;
            foreach (byte[] t in m_Tiles.Values)
                for (int o = 0; o < t.Length; o += 3)
                    if (t[o] != 0 || t[o + 1] != 0 || t[o + 2] != 0) n++;
            return n;
        }

        /// <summary>逐格相同（沒配置的分塊當全空）。</summary>
        public bool ContentEquals(SCP_GlobeCells o)
        {
            if (o.N != N) return false;
            var aKeys = new HashSet<int>(m_Tiles.Keys);
            aKeys.UnionWith(o.m_Tiles.Keys);
            foreach (int k in aKeys)
            {
                m_Tiles.TryGetValue(k, out byte[]? a);
                o.m_Tiles.TryGetValue(k, out byte[]? b);
                int len = Tile * Tile * 3;
                for (int x = 0; x < len; x++)
                    if ((a == null ? 0 : a[x]) != (b == null ? 0 : b[x])) return false;
            }
            return true;
        }

        public SCP_GlobeCells Clone()
        {
            var c = new SCP_GlobeCells(N);
            foreach (var kv in m_Tiles) c.m_Tiles[kv.Key] = (byte[])kv.Value.Clone();
            return c;
        }
    }
}
