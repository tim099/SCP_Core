// 區塊職責：可繪製球面的**狀態層** —— 路徑、meta、事件（只能追加）、重播成格子、分塊快取、Undo。
// 物理意義：正本是 events/ 底下一筆一檔的事件；每筆繪製事件逐格記「新值、舊值」⇒ Undo ＝ 追加一筆反向事件，
//          ⛔ 不刪任何事件檔。快取（_cache/）只是重播的捷徑：對不上 ⇒ 從頭重播，⛔ 不拿快取當正本。
//          事件寫的是**絕對值**（不是增量）⇒ 快取分塊比 cells.json 記的 LastSeq 新也沒關係：補重播一段後綴，結果一樣。
// 數值影響：一格 24-bit RGB；0＝沒畫過＝顯示 meta 的底色（見 SCP_GlobeCells）。
//          Undo 只准退「最後一筆仍有效的繪製」（堆疊）—— 退中間那筆的話，它的舊值會蓋掉後面那筆畫的格子。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Globe
{
    public sealed class SCP_GlobePaths
    {
        public const string DirName = "Globe";
        public string Root { get; }

        public SCP_GlobePaths(SCP_DataRoot iDataRoot) { Root = Path.GetFullPath(Path.Combine(iDataRoot.Value, DirName)); }
        /// <summary>直接給球面根（測試隔離用；正路走 <see cref="SCP_DataRoot"/>）。</summary>
        public SCP_GlobePaths(string iGlobeRoot) { Root = Path.GetFullPath(iGlobeRoot); }

        public string Meta => Path.Combine(Root, "meta.json");
        public string Events => Path.Combine(Root, "events");
        public string CacheDir => Path.Combine(Root, "_cache");
        public string CacheTiles => Path.Combine(CacheDir, "tiles");
        public string CacheInfo => Path.Combine(CacheDir, "cells.json");
        public string CacheTile(int iKey) => Path.Combine(CacheTiles, iKey.ToString("D5", CultureInfo.InvariantCulture) + ".bin");
        /// <summary>寫入鎖的目標（鎖檔＝它＋.lock）。</summary>
        public string LockTarget => Path.Combine(Root, "globe");
        public string EventFile(int iSeq) => Path.Combine(Events, iSeq.ToString("D6", CultureInfo.InvariantCulture) + ".json");
    }

    public sealed class SCP_GlobeMeta
    {
        public const string MappingName = "equiangular-cube";
        public const string DefaultBase = "#0049AA";

        public int Version { get; set; } = 1;
        public string Mapping { get; set; } = MappingName;
        public int N { get; set; } = 2048;
        /// <summary>沒畫過的格子顯示的顏色（#RRGGBB）。改它不動任何格子。</summary>
        public string BaseColor { get; set; } = DefaultBase;
        /// <summary>格子值的編碼：24-bit RGB，0＝沒畫過。</summary>
        public string CellFormat { get; set; } = "rgb24";
        public List<SCP_GlobeFace> Faces { get; set; } = SCP_GlobeGrid.DefaultFaces();
    }

    public sealed class SCP_GlobeEvent
    {
        public int Seq { get; set; }
        /// <summary>point／line／polygon／fill／undo。</summary>
        public string Op { get; set; } = "";
        public string Persona { get; set; } = "";
        public string At { get; set; } = "";
        public string Note { get; set; } = "";
        /// <summary>繪製：扁平三元組 [index, 新值, 舊值] …（index＝face·N²＋j·N＋i；值＝0xRRGGBB，0＝沒畫過）。</summary>
        public List<int> Cells { get; set; } = new List<int>();
        /// <summary>undo：退的是哪一筆。</summary>
        public int Target { get; set; }
    }

    sealed class SCP_GlobeCacheInfo
    {
        public int N { get; set; }
        public int LastSeq { get; set; }
        public List<int> Stack { get; set; } = new List<int>();
        public List<int> Tiles { get; set; } = new List<int>();
    }

    /// <summary>重播後的狀態。</summary>
    public sealed class SCP_GlobeState
    {
        public SCP_GlobeMeta Meta = new SCP_GlobeMeta();
        public SCP_GlobeGrid Grid = null!;
        public SCP_GlobeCells Cells = null!;
        public int LastSeq;
        /// <summary>仍有效（沒被 undo）的繪製事件 seq，由舊到新。</summary>
        public List<int> Stack = new List<int>();
        public bool FromCache;
        /// <summary>底色（0xRRGGBB）。</summary>
        public int BaseRgb;
    }

    public sealed class SCP_GlobeException : Exception
    {
        public SCP_GlobeException(string iMsg) : base(iMsg) { }
    }

    public sealed class SCP_GlobeStore
    {
        public SCP_GlobePaths Paths { get; }
        public Func<DateTime> Clock = () => DateTime.UtcNow;

        public SCP_GlobeStore(SCP_GlobePaths iPaths) { Paths = iPaths; }

        public bool Exists => File.Exists(Paths.Meta);

        // ── 顏色 ──────────────────────────────────────────────
        /// <summary>「#RRGGBB」或「r,g,b」→ 0xRRGGBB（不重映純黑；畫進格子前走 <see cref="SCP_GlobeCells.FromRgb"/>）。</summary>
        public static bool TryParseColor(string? iText, out int r, out int g, out int b, out string oWhy)
        {
            r = g = b = 0; oWhy = "";
            string s = (iText ?? "").Trim();
            if (s.StartsWith("#", StringComparison.Ordinal))
            {
                if (s.Length == 7 && int.TryParse(s.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int v))
                { r = (v >> 16) & 255; g = (v >> 8) & 255; b = v & 255; return true; }
                oWhy = "顏色要 #RRGGBB：「" + s + "」"; return false;
            }
            string[] p = s.Split(',');
            if (p.Length == 3 && int.TryParse(p[0].Trim(), out r) && int.TryParse(p[1].Trim(), out g) && int.TryParse(p[2].Trim(), out b)
                && r >= 0 && r <= 255 && g >= 0 && g <= 255 && b >= 0 && b <= 255) return true;
            oWhy = "顏色要 #RRGGBB 或 r,g,b（0-255）：「" + s + "」";
            return false;
        }

        // ── meta ──────────────────────────────────────────────
        public SCP_GlobeMeta LoadMeta()
        {
            if (!File.Exists(Paths.Meta)) throw new SCP_GlobeException("球面還沒建立（找不到 " + Paths.Meta + "）⇒ 先 op=init");
            var aMeta = new SCP_GlobeMeta();
            SCP_JsonMapper.Populate(aMeta, SCP_JsonData.Parse(File.ReadAllText(Paths.Meta)));
            if (aMeta.Mapping != SCP_GlobeMeta.MappingName) throw new SCP_GlobeException("不認得的 mapping：" + aMeta.Mapping);
            if (aMeta.CellFormat != "rgb24") throw new SCP_GlobeException("不認得的 CellFormat：" + aMeta.CellFormat);
            if (!TryParseColor(aMeta.BaseColor, out _, out _, out _, out string w)) throw new SCP_GlobeException("meta 的 BaseColor 壞了：" + w);
            return aMeta;
        }

        public void WriteMeta(SCP_GlobeMeta iMeta)
        {
            if (!TryParseColor(iMeta.BaseColor, out _, out _, out _, out string w)) throw new SCP_GlobeException(w);
            new SCP_GlobeGrid(iMeta.N, iMeta.Faces);   // 寫之前先驗基底（壞的不落盤）
            Directory.CreateDirectory(Paths.Root);
            WriteAtomic(Paths.Meta, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(iMeta), true));
        }

        // ── 重播 ──────────────────────────────────────────────
        public SCP_GlobeState Load()
        {
            SCP_GlobeMeta aMeta = LoadMeta();
            TryParseColor(aMeta.BaseColor, out int br, out int bg, out int bb, out _);
            var s = new SCP_GlobeState
            {
                Meta = aMeta, Grid = new SCP_GlobeGrid(aMeta.N, aMeta.Faces), Cells = new SCP_GlobeCells(aMeta.N),
                BaseRgb = (br << 16) | (bg << 8) | bb,
            };
            List<int> aSeqs = ListSeqs();
            int aFrom = 0;
            if (TryLoadCache(s, aSeqs))
            {
                s.FromCache = true;
                aFrom = s.LastSeq;
            }
            else { s.Cells = new SCP_GlobeCells(aMeta.N); s.LastSeq = 0; s.Stack = new List<int>(); }
            foreach (int aSeq in aSeqs)
            {
                if (aSeq <= aFrom) continue;
                ApplyAny(s, ReadEvent(aSeq));
            }
            // ⚠ 不清髒分塊：補重播動到的分塊要跟著下一次 SaveCache 寫出去，不然 cells.json 的 LastSeq 會跑在分塊前面
            return s;
        }

        List<int> ListSeqs()
        {
            var a = new List<int>();
            if (!Directory.Exists(Paths.Events)) return a;
            foreach (string f in Directory.GetFiles(Paths.Events, "*.json"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.None, CultureInfo.InvariantCulture, out int n)) a.Add(n);
            a.Sort();
            for (int k = 0; k < a.Count; k++)
                if (a[k] != k + 1) throw new SCP_GlobeException($"事件序號斷了：第 {k + 1} 筆的檔名是 {a[k]:D6}（缺號或多出來的檔）");
            return a;
        }

        public SCP_GlobeEvent ReadEvent(int iSeq)
        {
            var e = new SCP_GlobeEvent();
            SCP_JsonMapper.Populate(e, SCP_JsonData.Parse(File.ReadAllText(Paths.EventFile(iSeq))));
            if (e.Seq != iSeq) throw new SCP_GlobeException($"事件 {iSeq:D6} 內文的 Seq 是 {e.Seq}");
            return e;
        }

        static void ApplyPaint(SCP_GlobeState s, SCP_GlobeEvent e)
        {
            if (e.Cells.Count % 3 != 0) throw new SCP_GlobeException($"事件 {e.Seq} 的 Cells 長度不是 3 的倍數");
            int aMax = s.Grid.CellCount;
            for (int k = 0; k < e.Cells.Count; k += 3)
            {
                int idx = e.Cells[k];
                if (idx < 0 || idx >= aMax) throw new SCP_GlobeException($"事件 {e.Seq} 有越界格子 {idx}");
                s.Cells.Set(idx, e.Cells[k + 1]);
            }
            s.Stack.Add(e.Seq);
            s.LastSeq = e.Seq;
        }

        void ApplyAny(SCP_GlobeState s, SCP_GlobeEvent e)
        {
            if (e.Op != "undo") { ApplyPaint(s, e); return; }
            if (s.Stack.Count == 0 || s.Stack[s.Stack.Count - 1] != e.Target)
                throw new SCP_GlobeException($"事件 {e.Seq} 要退 {e.Target}，但當時最後一筆有效繪製不是它");
            SCP_GlobeEvent t = ReadEvent(e.Target);
            for (int k = t.Cells.Count - 3; k >= 0; k -= 3) s.Cells.Set(t.Cells[k], t.Cells[k + 2]);
            s.Stack.RemoveAt(s.Stack.Count - 1);
            s.LastSeq = e.Seq;
        }

        // ── 寫入 ──────────────────────────────────────────────
        /// <summary>
        /// 一筆繪製：<paramref name="iCells"/> 是要塗的格子（可重複，會去重），值 <paramref name="iValue"/>（0xRRGGBB，0＝擦掉）。
        /// 跟現值一樣的格子不記；全部一樣 ⇒ 不寫事件、回 null。
        /// </summary>
        public SCP_GlobeEvent? Paint(string iOp, string iPersona, string iNote, IEnumerable<int> iCells, int iValue)
        {
            if (iValue < 0 || iValue > 0xFFFFFF) throw new SCP_GlobeException("格子值越界：" + iValue);
            using (SCP_FileLock.Acquire(LockTargetReady()))
            {
                SCP_GlobeState s = Load();
                var e = new SCP_GlobeEvent { Seq = s.LastSeq + 1, Op = iOp, Persona = iPersona, Note = iNote, At = Now() };
                var aSeen = new HashSet<int>();
                int aMax = s.Grid.CellCount;
                foreach (int idx in iCells)
                {
                    if (idx < 0 || idx >= aMax) throw new SCP_GlobeException("越界格子 " + idx);
                    if (!aSeen.Add(idx)) continue;
                    int aOld = s.Cells.Get(idx);
                    if (aOld == iValue) continue;
                    e.Cells.Add(idx); e.Cells.Add(iValue); e.Cells.Add(aOld);
                }
                if (e.Cells.Count == 0) return null;
                Commit(s, e);
                return e;
            }
        }

        /// <summary>退最後一筆仍有效的繪製；沒有可退的 ⇒ 回 null。</summary>
        public SCP_GlobeEvent? Undo(string iPersona)
        {
            using (SCP_FileLock.Acquire(LockTargetReady()))
            {
                SCP_GlobeState s = Load();
                if (s.Stack.Count == 0) return null;
                var e = new SCP_GlobeEvent { Seq = s.LastSeq + 1, Op = "undo", Persona = iPersona, At = Now(), Target = s.Stack[s.Stack.Count - 1] };
                Commit(s, e);
                return e;
            }
        }

        /// <summary>事件先落盤（正本），再更新快取；快取寫失敗不影響正本（下次重播會補）。</summary>
        void Commit(SCP_GlobeState s, SCP_GlobeEvent e)
        {
            Directory.CreateDirectory(Paths.Events);
            string aFile = Paths.EventFile(e.Seq);
            if (File.Exists(aFile)) throw new SCP_GlobeException("事件檔已存在（序號撞了）：" + aFile);
            WriteAtomic(aFile, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(e), false));
            ApplyAny(s, e);
            try { SaveCache(s); } catch (Exception) { /* 快取只是捷徑；下次 Load 對不上就重播 */ }
        }

        string LockTargetReady() { Directory.CreateDirectory(Paths.Root); return Paths.LockTarget; }

        public List<SCP_GlobeEvent> History(int iLast)
        {
            var a = new List<SCP_GlobeEvent>();
            List<int> aSeqs = ListSeqs();
            for (int k = Math.Max(0, aSeqs.Count - iLast); k < aSeqs.Count; k++) a.Add(ReadEvent(aSeqs[k]));
            return a;
        }

        // ── 快取（分塊）────────────────────────────────────────
        bool TryLoadCache(SCP_GlobeState s, List<int> iSeqs)
        {
            try
            {
                if (!File.Exists(Paths.CacheInfo)) return false;
                var aInfo = new SCP_GlobeCacheInfo();
                SCP_JsonMapper.Populate(aInfo, SCP_JsonData.Parse(File.ReadAllText(Paths.CacheInfo)));
                if (aInfo.N != s.Meta.N || aInfo.LastSeq > iSeqs.Count) return false;   // 事件比快取少 ⇒ 快取是別的歷史
                foreach (int k in aInfo.Tiles) s.Cells.LoadTile(k, File.ReadAllBytes(Paths.CacheTile(k)));
                s.LastSeq = aInfo.LastSeq;
                s.Stack = aInfo.Stack;
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>只寫髒分塊；cells.json 最後寫（它是提交點）。</summary>
        void SaveCache(SCP_GlobeState s)
        {
            Directory.CreateDirectory(Paths.CacheTiles);
            foreach (int k in s.Cells.DirtyTiles)
            {
                string aPath = Paths.CacheTile(k), aTmp = aPath + ".tmp";
                File.WriteAllBytes(aTmp, s.Cells.TileBytes(k));
                Replace(aTmp, aPath);
            }
            var aInfo = new SCP_GlobeCacheInfo { N = s.Meta.N, LastSeq = s.LastSeq, Stack = new List<int>(s.Stack), Tiles = new List<int>(s.Cells.TileKeys) };
            aInfo.Tiles.Sort();
            WriteAtomic(Paths.CacheInfo, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(aInfo), false));
            s.Cells.ClearDirty();
        }

        // ── 小工具 ────────────────────────────────────────────
        string Now() => Clock().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        static void WriteAtomic(string iPath, string iText)
        {
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iText.EndsWith("\n", StringComparison.Ordinal) ? iText : iText + "\n");
            Replace(aTmp, iPath);
        }

        static void Replace(string iTmp, string iPath)
        {
            if (File.Exists(iPath)) File.Replace(iTmp, iPath, null);
            else File.Move(iTmp, iPath);
        }
    }
}
