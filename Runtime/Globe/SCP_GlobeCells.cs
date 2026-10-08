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

        /// <summary>
        /// regrid：每面邊長 ×<paramref name="iFactor"/>，舊的每一格變成 factor×factor 格、值照抄（等角網格是巢狀的 —— 舊格邊界一定落在新格邊界上，
        /// 所以舊畫的圖一格都不會位移）。回傳新容器（所有有畫的分塊都標髒，下一次存快取會整批寫出）；原容器不動。
        /// </summary>
        public SCP_GlobeCells Expand(int iFactor)
        {
            if (iFactor < 2) throw new SCP_GlobeException("regrid 倍率要 ≥ 2：" + iFactor);
            long aNewN = (long)N * iFactor;
            if (aNewN > SCP_GlobeMeta.MaxN) throw new SCP_GlobeException($"regrid 之後每面 {aNewN} 格，超過上限 {SCP_GlobeMeta.MaxN}");
            int aF = iFactor, aNN = (int)aNewN, tps = TilesPerSide;
            var c = new SCP_GlobeCells(aNN);
            foreach (KeyValuePair<int, byte[]> kv in m_Tiles)
            {
                int tx = kv.Key % tps, ty = (kv.Key / tps) % tps, face = kv.Key / (tps * tps);
                byte[] t = kv.Value;
                for (int jj = 0; jj < Tile; jj++)
                    for (int ii = 0; ii < Tile; ii++)
                    {
                        int o = (jj * Tile + ii) * 3;
                        int v = (t[o] << 16) | (t[o + 1] << 8) | t[o + 2];
                        if (v == Empty) continue;
                        int i = tx * Tile + ii, j = ty * Tile + jj;
                        for (int b = 0; b < aF; b++)
                            for (int a = 0; a < aF; a++)
                                c.Set(face * aNN * aNN + (j * aF + b) * aNN + (i * aF + a), v);
                    }
            }
            return c;
        }

        public SCP_GlobeCells Clone()
        {
            var c = new SCP_GlobeCells(N);
            foreach (var kv in m_Tiles) c.m_Tiles[kv.Key] = (byte[])kv.Value.Clone();
            return c;
        }
    }
}
