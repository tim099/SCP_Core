// 區塊職責：可繪製球面的**狀態層** —— 路徑、meta、事件（只能追加）、重播成格子、分塊快取、Undo。
// 物理意義：正本是 events/ 底下一筆一檔的事件；每筆繪製事件逐格記「新值、舊值」⇒ Undo ＝ 追加一筆反向事件，
//          ⛔ 不刪任何事件檔。快取（_cache/）只是重播的捷徑：對不上 ⇒ 從頭重播，⛔ 不拿快取當正本。
//          事件寫的是**絕對值**（不是增量）⇒ 快取分塊比 cells.json 記的 LastSeq 新也沒關係：補重播一段後綴，結果一樣。
// 數值影響：一格 24-bit RGB；0＝沒畫過＝顯示 meta 的底色（見 SCP_GlobeCells）。
//          Undo 只准退「最後一筆仍有效的繪製」（堆疊）—— 退中間那筆的話，它的舊值會蓋掉後面那筆畫的格子。
// 事件 schema v2（TASK-0467 後續）：
//   · 繪製事件改存**緊湊區段** Runs＝[起點 index, 長度, 新值, 舊值]…（連續格子、新舊值都相同才併成一段；舊事件的 Cells 三元組照樣讀得懂）。
//   · regrid 事件（Op="regrid"，GridN＝新的每面邊長）：每面邊長 ×整數倍，舊畫的每一格變成 f×f 格；事件只追加，⛔ 不改舊事件檔。
//     事件裡的 index 屬於**寫下它那一刻的 N**（重播時由 regrid 事件推出，不另存）；Undo 跨過 regrid 時把舊 index 展開成 f×f 個子格。
//   · 一旦出現 v2 事件，meta 的 Mapping 改成 MappingNameV2 —— 舊版程式讀 meta 會**大聲拒絕**（不認得的 mapping），而不是安靜地算錯格子。
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
        public string CacheTile(int iKey) => CacheTile("tiles", iKey);
        /// <summary>分塊快取資料夾（<paramref name="iSub"/>＝`tiles`（建立時的 N）或 `tiles_<N>`）—— 不同 N 的分塊 key 意義不同，⛔ 不放同一個夾。</summary>
        public string CacheTilesDir(string iSub) => Path.Combine(CacheDir, iSub);
        public string CacheTile(string iSub, int iKey) => Path.Combine(CacheTilesDir(iSub), iKey.ToString("D5", CultureInfo.InvariantCulture) + ".bin");
        /// <summary>寫入鎖的目標（鎖檔＝它＋.lock）。</summary>
        public string LockTarget => Path.Combine(Root, "globe");
        public string EventFile(int iSeq) => Path.Combine(Events, iSeq.ToString("D6", CultureInfo.InvariantCulture) + ".json");
        /// <summary>輸出的圖（不入版控；跟 Sculpture/exports 同一個慣例）。</summary>
        public string ExportsDir => Path.Combine(Root, "exports");
        /// <summary>一張輸出圖的路徑：檔名帶到毫秒，連按兩次不會互相蓋掉。<paramref name="iKind"/>＝view｜map。</summary>
        public string ExportFile(string iKind, DateTime iUtc)
            => Path.Combine(ExportsDir, "globe_" + iKind + "_" + iUtc.ToString("yyyyMMdd'T'HHmmssfff", CultureInfo.InvariantCulture) + ".png");
    }

    public sealed class SCP_GlobeMeta
    {
        public const string MappingName = "equiangular-cube";
        /// <summary>事件 schema v2（緊湊 Runs／regrid）的標記。⚠ 刻意換名字：舊版程式遇到不認得的 mapping 會直接丟例外，比靜默算錯好。</summary>
        public const string MappingNameV2 = "equiangular-cube-v2";
        public const string DefaultBase = "#0049AA";
        /// <summary>每面邊長上限（init 與 regrid 共用）。</summary>
        public const int MaxN = 8192;

        public int Version { get; set; } = 1;
        public string Mapping { get; set; } = MappingName;
        /// <summary>目前的每面邊長（regrid 之後是新的 N；⚠ 事件才是真相，Load 會以重播結果為準）。</summary>
        public int N { get; set; } = 2048;
        /// <summary>建立時的每面邊長（regrid 之前的 N）；0 ＝ 沒 regrid 過的舊檔 ⇒ 同 N。radius／width／max_cells 的「初始格」單位以它為準。</summary>
        public int InitialN { get; set; }
        /// <summary>沒畫過的格子顯示的顏色（#RRGGBB）。改它不動任何格子。</summary>
        public string BaseColor { get; set; } = DefaultBase;
        /// <summary>格子值的編碼：24-bit RGB，0＝沒畫過。</summary>
        public string CellFormat { get; set; } = "rgb24";
        public List<SCP_GlobeFace> Faces { get; set; } = SCP_GlobeGrid.DefaultFaces();

        public int GetInitialN() => InitialN > 0 ? InitialN : N;
    }

    public sealed class SCP_GlobeEvent
    {
        public int Seq { get; set; }
        /// <summary>point／line／polygon／fill／undo。</summary>
        public string Op { get; set; } = "";
        public string Persona { get; set; } = "";
        public string At { get; set; } = "";
        public string Note { get; set; } = "";
        /// <summary>【舊格式，只讀】繪製：扁平三元組 [index, 新值, 舊值] …（index＝face·N²＋j·N＋i；值＝0xRRGGBB，0＝沒畫過）。新事件不再寫它。</summary>
        public List<int> Cells { get; set; } = new List<int>();
        /// <summary>繪製（緊湊）：扁平四元組 [起點 index, 長度, 新值, 舊值] …；連續 index、新舊值都相同才併成一段。index 屬於寫下它那一刻的 N。</summary>
        public List<int> Runs { get; set; } = new List<int>();
        /// <summary>regrid：新的每面邊長（必須是目前 N 的整數倍、&gt; 目前 N）；其他 Op ＝ 0。</summary>
        public int GridN { get; set; }
        /// <summary>undo：退的是哪一筆。</summary>
        public int Target { get; set; }
        /// <summary>這一筆算在哪個施工區（選填；施工區的 show 用它統計進度）。</summary>
        public string Zone { get; set; } = "";

        /// <summary>這一筆動了幾格（舊的 Cells 三元組與新的 Runs 都算）。</summary>
        public int CountCells()
        {
            long n = Cells.Count / 3;
            for (int k = 0; k + 3 < Runs.Count; k += 4) n += Runs[k + 1];
            return n > int.MaxValue ? int.MaxValue : (int)n;
        }

        /// <summary>逐格走一遍（index, 新值, 舊值）；兩種格式都吃。長度／數值有問題 ⇒ 丟 SCP_GlobeException（⛔ 不安靜略過）。</summary>
        public void ForEachCell(Action<int, int, int> iAct)
        {
            if (Cells.Count % 3 != 0) throw new SCP_GlobeException($"事件 {Seq} 的 Cells 長度不是 3 的倍數");
            if (Runs.Count % 4 != 0) throw new SCP_GlobeException($"事件 {Seq} 的 Runs 長度不是 4 的倍數");
            for (int k = 0; k < Cells.Count; k += 3) iAct(Cells[k], Cells[k + 1], Cells[k + 2]);
            for (int k = 0; k < Runs.Count; k += 4)
            {
                int aStart = Runs[k], aLen = Runs[k + 1], aNew = Runs[k + 2], aOld = Runs[k + 3];
                if (aLen < 1 || aStart < 0 || (long)aStart + aLen > int.MaxValue) throw new SCP_GlobeException($"事件 {Seq} 有壞掉的區段（起點 {aStart}、長度 {aLen}）");
                for (int q = 0; q < aLen; q++) iAct(aStart + q, aNew, aOld);
            }
        }
    }

    sealed class SCP_GlobeCacheInfo
    {
        /// <summary>快取當下（LastSeq 那一刻）的每面邊長。</summary>
        public int N { get; set; }
        public int LastSeq { get; set; }
        public List<int> Stack { get; set; } = new List<int>();
        public List<int> Tiles { get; set; } = new List<int>();
        /// <summary>到 LastSeq 為止的 regrid 歷史：扁平 [事件 seq, 新 N] …（舊快取沒有 ⇒ 空 ＝ 沒 regrid 過）。</summary>
        public List<int> Regrids { get; set; } = new List<int>();
        /// <summary>分塊放在 `_cache/` 底下哪個夾（舊快取沒有 ⇒ 空 ＝ `tiles`）。</summary>
        public string TilesDir { get; set; } = "";
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
        /// <summary>建立時的每面邊長（重播的起點）。</summary>
        public int InitialN;
        /// <summary>regrid 歷史：扁平 [事件 seq, 新 N] …，由舊到新。</summary>
        public List<int> Regrids = new List<int>();

        /// <summary>第 <paramref name="iSeq"/> 筆事件被寫下的那一刻，球面每面幾格（＝它的 index 屬於哪個 N）。</summary>
        public int NAt(int iSeq)
        {
            int n = InitialN;
            for (int k = 0; k + 1 < Regrids.Count; k += 2)
                if (Regrids[k] < iSeq) n = Regrids[k + 1];
            return n;
        }
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
            if (aMeta.Mapping != SCP_GlobeMeta.MappingName && aMeta.Mapping != SCP_GlobeMeta.MappingNameV2) throw new SCP_GlobeException("不認得的 mapping：" + aMeta.Mapping);
            if (aMeta.CellFormat != "rgb24") throw new SCP_GlobeException("不認得的 CellFormat：" + aMeta.CellFormat);
            if (!TryParseColor(aMeta.BaseColor, out _, out _, out _, out string w)) throw new SCP_GlobeException("meta 的 BaseColor 壞了：" + w);
            CheckInitialN(aMeta);
            return aMeta;
        }

        static void CheckInitialN(SCP_GlobeMeta iMeta)
        {
            if (iMeta.InitialN < 0 || (iMeta.InitialN > 0 && (iMeta.InitialN > iMeta.N || iMeta.N % iMeta.InitialN != 0)))
                throw new SCP_GlobeException($"meta 的 InitialN {iMeta.InitialN} 跟 N {iMeta.N} 對不上（N 要是 InitialN 的整數倍）");
        }

        public void WriteMeta(SCP_GlobeMeta iMeta)
        {
            if (!TryParseColor(iMeta.BaseColor, out _, out _, out _, out string w)) throw new SCP_GlobeException(w);
            CheckInitialN(iMeta);
            new SCP_GlobeGrid(iMeta.N, iMeta.Faces);   // 寫之前先驗基底（壞的不落盤）
            Directory.CreateDirectory(Paths.Root);
            WriteAtomic(Paths.Meta, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(iMeta), true));
        }

        // ── 重播 ──────────────────────────────────────────────
        public SCP_GlobeState Load()
        {
            SCP_GlobeMeta aMeta = LoadMeta();
            TryParseColor(aMeta.BaseColor, out int br, out int bg, out int bb, out _);
            int aInit = aMeta.GetInitialN();
            var s = new SCP_GlobeState
            {
                Meta = aMeta, InitialN = aInit, Grid = new SCP_GlobeGrid(aInit, aMeta.Faces), Cells = new SCP_GlobeCells(aInit),
                BaseRgb = (br << 16) | (bg << 8) | bb,
            };
            List<int> aSeqs = ListSeqs();
            int aFrom = 0;
            if (TryLoadCache(s, aSeqs))
            {
                s.FromCache = true;
                aFrom = s.LastSeq;
            }
            else ResetToInitial(s);
            foreach (int aSeq in aSeqs)
            {
                if (aSeq <= aFrom) continue;
                ApplyAny(s, ReadEvent(aSeq));
            }
            // 事件才是真相：meta 的 N 可能比事件超前（regrid 寫到一半掛了）或落後 —— 以重播結果為準，不拿它蓋過事件
            s.Meta.N = s.Grid.N;
            // ⚠ 不清髒分塊：補重播動到的分塊要跟著下一次 SaveCache 寫出去，不然 cells.json 的 LastSeq 會跑在分塊前面
            return s;
        }

        static void ResetToInitial(SCP_GlobeState s)
        {
            s.Grid = new SCP_GlobeGrid(s.InitialN, s.Meta.Faces);
            s.Cells = new SCP_GlobeCells(s.InitialN);
            s.LastSeq = 0;
            s.Stack = new List<int>();
            s.Regrids = new List<int>();
            s.FromCache = false;
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
            int aMax = s.Grid.CellCount;
            e.ForEachCell((idx, nw, _) =>
            {
                if (idx < 0 || idx >= aMax) throw new SCP_GlobeException($"事件 {e.Seq} 有越界格子 {idx}");
                if (nw < 0 || nw > 0xFFFFFF) throw new SCP_GlobeException($"事件 {e.Seq} 有越界的格子值 {nw}");
                s.Cells.Set(idx, nw);
            });
            s.Stack.Add(e.Seq);
            s.LastSeq = e.Seq;
        }

        /// <summary>regrid：每面邊長 ×整數倍；舊畫的每一格變成 f×f 格。⚠ 不進 Stack（不能被 undo）。</summary>
        static void ApplyRegrid(SCP_GlobeState s, SCP_GlobeEvent e)
        {
            int aCur = s.Grid.N, aNew = e.GridN;
            if (aNew <= aCur || aNew % aCur != 0 || aNew > SCP_GlobeMeta.MaxN)
                throw new SCP_GlobeException($"事件 {e.Seq} 的 regrid 不合法：目前每面 {aCur} 格，要變成 {aNew}（要是整數倍、更大、≤ {SCP_GlobeMeta.MaxN}）");
            s.Cells = s.Cells.Expand(aNew / aCur);
            s.Grid = new SCP_GlobeGrid(aNew, s.Meta.Faces);
            s.Regrids.Add(e.Seq);
            s.Regrids.Add(aNew);
            s.LastSeq = e.Seq;
        }

        void ApplyAny(SCP_GlobeState s, SCP_GlobeEvent e)
        {
            if (e.Op == "regrid") { ApplyRegrid(s, e); return; }
            if (e.Op != "undo") { ApplyPaint(s, e); return; }
            if (s.Stack.Count == 0 || s.Stack[s.Stack.Count - 1] != e.Target)
                throw new SCP_GlobeException($"事件 {e.Seq} 要退 {e.Target}，但當時最後一筆有效繪製不是它");
            SCP_GlobeEvent t = ReadEvent(e.Target);
            int aThen = s.NAt(e.Target), aNow = s.Grid.N;
            if (aNow % aThen != 0) throw new SCP_GlobeException($"事件 {e.Seq} 退的 #{e.Target} 是 N={aThen} 時寫的，跟現在 N={aNow} 不成整數倍");
            int f = aNow / aThen;
            if (f == 1) t.ForEachCell((idx, _, old) => s.Cells.Set(idx, old));
            else
            {
                // 跨過 regrid：舊 index 展開成 f×f 個子格（只有「最後一筆有效繪製」能被退 ⇒ 那一塊在 regrid 之後沒人動過，子格還是同一個值）
                int aThenNN = aThen * aThen, aNowNN = aNow * aNow;
                t.ForEachCell((idx, _, old) =>
                {
                    int face = idx / aThenNN, r = idx - face * aThenNN, j = r / aThen, i = r - j * aThen;
                    for (int b = 0; b < f; b++)
                        for (int a = 0; a < f; a++)
                            s.Cells.Set(face * aNowNN + (j * f + b) * aNow + (i * f + a), old);
                });
            }
            s.Stack.RemoveAt(s.Stack.Count - 1);
            s.LastSeq = e.Seq;
        }

        // ── 寫入 ──────────────────────────────────────────────
        /// <summary>
        /// 一筆繪製：<paramref name="iCells"/> 是要塗的格子（可重複，會去重），值 <paramref name="iValue"/>（0xRRGGBB，0＝擦掉）。
        /// 跟現值一樣的格子不記；全部一樣 ⇒ 不寫事件、回 null。事件存成緊湊區段（Runs）。
        /// <para><paramref name="iExpectN"/> &gt; 0：呼叫端是照那個 N 算出格子 index 的 —— 進鎖後發現球面已經不是那個 N（中間有人 regrid）
        /// ⇒ 丟例外、零寫入（⛔ 拿舊 N 的 index 去寫新 N 的球面，會畫到錯的地方而且不報錯）。</para>
        /// </summary>
        public SCP_GlobeEvent? Paint(string iOp, string iPersona, string iNote, IEnumerable<int> iCells, int iValue, string iZone = "", int iExpectN = 0)
        {
            if (iValue < 0 || iValue > 0xFFFFFF) throw new SCP_GlobeException("格子值越界：" + iValue);
            using (SCP_FileLock.Acquire(LockTargetReady()))
            {
                SCP_GlobeState s = Load();
                if (iExpectN > 0 && s.Grid.N != iExpectN)
                    throw new SCP_GlobeException($"球面在你算格子的途中變了：你照每面 {iExpectN} 格算，現在是 {s.Grid.N} 格（有人 regrid，或 meta 與事件不一致）⇒ 一格都沒寫，請重跑");
                var e = new SCP_GlobeEvent { Seq = s.LastSeq + 1, Op = iOp, Persona = iPersona, Note = iNote, At = Now(), Zone = iZone };
                var aSeen = new HashSet<int>();
                var aChanged = new List<KeyValuePair<int, int>>();   // index → 舊值
                int aMax = s.Grid.CellCount;
                foreach (int idx in iCells)
                {
                    if (idx < 0 || idx >= aMax) throw new SCP_GlobeException("越界格子 " + idx);
                    if (!aSeen.Add(idx)) continue;
                    int aOld = s.Cells.Get(idx);
                    if (aOld == iValue) continue;
                    aChanged.Add(new KeyValuePair<int, int>(idx, aOld));
                }
                if (aChanged.Count == 0) return null;
                aChanged.Sort((a, b) => a.Key.CompareTo(b.Key));
                // 緊湊編碼：index 連續、舊值相同才併成一段（新值整筆都一樣）
                int k = 0;
                while (k < aChanged.Count)
                {
                    int aStart = aChanged[k].Key, aOldV = aChanged[k].Value, aLen = 1;
                    while (k + aLen < aChanged.Count && aChanged[k + aLen].Key == aStart + aLen && aChanged[k + aLen].Value == aOldV) aLen++;
                    e.Runs.Add(aStart); e.Runs.Add(aLen); e.Runs.Add(iValue); e.Runs.Add(aOldV);
                    k += aLen;
                }
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

        /// <summary>
        /// regrid：每面邊長 ×<paramref name="iFactor"/>（整數 ≥ 2）。先把 meta 標成 v2（舊版程式從此大聲拒絕），再追加一筆 regrid 事件。
        /// 事件只追加、舊事件一個檔都不碰；之後的繪製事件用新的 index 空間。
        /// </summary>
        public SCP_GlobeEvent Regrid(string iPersona, int iFactor, string iNote)
        {
            if (iFactor < 2) throw new SCP_GlobeException("regrid 倍率要 ≥ 2：" + iFactor);
            using (SCP_FileLock.Acquire(LockTargetReady()))
            {
                SCP_GlobeState s = Load();
                long aNew = (long)s.Grid.N * iFactor;
                if (aNew > SCP_GlobeMeta.MaxN) throw new SCP_GlobeException($"regrid 之後每面 {aNew} 格，超過上限 {SCP_GlobeMeta.MaxN}");
                var e = new SCP_GlobeEvent { Seq = s.LastSeq + 1, Op = "regrid", Persona = iPersona, Note = iNote, At = Now(), GridN = (int)aNew };
                Commit(s, e);
                return e;
            }
        }

        /// <summary>
        /// 事件先落盤（正本），再更新快取；快取寫失敗不影響正本（下次重播會補）。
        /// ⚠ v2 事件（Runs／regrid）寫之前先把 meta 標成 v2：順序是「標記 → 事件」，中途掛掉頂多多一個標記，不會有「事件在、標記沒有」的狀態。
        /// </summary>
        void Commit(SCP_GlobeState s, SCP_GlobeEvent e)
        {
            Directory.CreateDirectory(Paths.Events);
            string aFile = Paths.EventFile(e.Seq);
            if (File.Exists(aFile)) throw new SCP_GlobeException("事件檔已存在（序號撞了）：" + aFile);
            if (e.Runs.Count > 0 || e.Op == "regrid") MarkV2(e.Op == "regrid" ? e.GridN : 0, s.InitialN);
            WriteAtomic(aFile, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(e), false));
            ApplyAny(s, e);
            try { SaveCache(s); } catch (Exception) { /* 快取只是捷徑；下次 Load 對不上就重播 */ }
        }

        void MarkV2(int iNewN, int iInitialN)
        {
            SCP_GlobeMeta m = LoadMeta();
            bool aDirty = false;
            if (m.Mapping != SCP_GlobeMeta.MappingNameV2) { m.Mapping = SCP_GlobeMeta.MappingNameV2; aDirty = true; }
            if (m.Version < 2) { m.Version = 2; aDirty = true; }
            if (m.InitialN != iInitialN) { m.InitialN = iInitialN; aDirty = true; }
            if (iNewN > 0 && m.N != iNewN) { m.N = iNewN; aDirty = true; }
            if (aDirty) WriteMeta(m);
        }

        string LockTargetReady() { Directory.CreateDirectory(Paths.Root); return Paths.LockTarget; }

        /// <summary>跟繪製同一把鎖（施工區的「查有沒有同 id → 寫檔」要在鎖裡，不然兩人同時開同名區會互蓋）。</summary>
        public SCP_FileLock AcquireLock() => SCP_FileLock.Acquire(LockTargetReady());

        public List<SCP_GlobeEvent> History(int iLast)
        {
            var a = new List<SCP_GlobeEvent>();
            List<int> aSeqs = ListSeqs();
            for (int k = Math.Max(0, aSeqs.Count - iLast); k < aSeqs.Count; k++) a.Add(ReadEvent(aSeqs[k]));
            return a;
        }

        // ── 快取（分塊）────────────────────────────────────────
        static string TilesSub(SCP_GlobeState s) => s.Grid.N == s.InitialN ? "tiles" : "tiles_" + s.Grid.N.ToString(CultureInfo.InvariantCulture);

        static bool SafeSub(string iSub)
        {
            if (iSub.Length == 0) return false;
            foreach (char c in iSub) if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '_')) return false;
            return true;
        }

        bool TryLoadCache(SCP_GlobeState s, List<int> iSeqs)
        {
            try
            {
                if (!File.Exists(Paths.CacheInfo)) return false;
                var aInfo = new SCP_GlobeCacheInfo();
                SCP_JsonMapper.Populate(aInfo, SCP_JsonData.Parse(File.ReadAllText(Paths.CacheInfo)));
                if (aInfo.LastSeq > iSeqs.Count) return false;   // 事件比快取少 ⇒ 快取是別的歷史
                // 快取記的 N 要跟它自己記的 regrid 歷史對得上（從 InitialN 一路乘上去）
                int aExpect = s.InitialN, aPrevSeq = 0;
                if (aInfo.Regrids.Count % 2 != 0) return false;
                for (int k = 0; k < aInfo.Regrids.Count; k += 2)
                {
                    int rs = aInfo.Regrids[k], rn = aInfo.Regrids[k + 1];
                    if (rs <= aPrevSeq || rs > aInfo.LastSeq || rn <= aExpect || rn % aExpect != 0 || rn > SCP_GlobeMeta.MaxN) return false;
                    aExpect = rn; aPrevSeq = rs;
                }
                if (aExpect != aInfo.N) return false;
                string aSub = aInfo.TilesDir.Length > 0 ? aInfo.TilesDir : "tiles";
                if (!SafeSub(aSub)) return false;
                var aCells = new SCP_GlobeCells(aInfo.N);
                foreach (int k in aInfo.Tiles) aCells.LoadTile(k, File.ReadAllBytes(Paths.CacheTile(aSub, k)));
                s.Grid = new SCP_GlobeGrid(aInfo.N, s.Meta.Faces);
                s.Cells = aCells;
                s.LastSeq = aInfo.LastSeq;
                s.Stack = aInfo.Stack;
                s.Regrids = new List<int>(aInfo.Regrids);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>只寫髒分塊；cells.json 最後寫（它是提交點）。分塊放 `tiles`（初始 N）或 `tiles_N`，不同 N 不共用夾。</summary>
        void SaveCache(SCP_GlobeState s)
        {
            string aSub = TilesSub(s);
            Directory.CreateDirectory(Paths.CacheTilesDir(aSub));
            foreach (int k in s.Cells.DirtyTiles)
            {
                string aPath = Paths.CacheTile(aSub, k), aTmp = aPath + ".tmp";
                File.WriteAllBytes(aTmp, s.Cells.TileBytes(k));
                Replace(aTmp, aPath);
            }
            var aInfo = new SCP_GlobeCacheInfo
            {
                N = s.Grid.N, LastSeq = s.LastSeq, Stack = new List<int>(s.Stack), Tiles = new List<int>(s.Cells.TileKeys),
                Regrids = new List<int>(s.Regrids), TilesDir = aSub,
            };
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

        /// <summary>
        /// 原子換檔。⚠ Windows 上目標檔被別的程式「剛好正在讀」（預覽頁、同步工具、防毒）時 File.Replace 會丟
        /// IOException「Unable to remove the file to be replaced」—— 那是一瞬間的事，所以重試幾次（共約 0.9 秒）再放棄；
        /// 最後一次的例外照丟（⛔ 不吞）。2026-10-08 實測：施工區勾項與 regrid 的 meta 寫入各撞到一次。
        /// </summary>
        static void Replace(string iTmp, string iPath)
        {
            for (int aTry = 0; ; aTry++)
            {
                try
                {
                    if (File.Exists(iPath)) File.Replace(iTmp, iPath, null);
                    else File.Move(iTmp, iPath);
                    return;
                }
                catch (Exception e) when ((e is IOException || e is UnauthorizedAccessException) && aTry < 8)
                {
                    System.Threading.Thread.Sleep(25 * (aTry + 1));
                }
            }
        }
    }
}
