// 區塊職責：噴漆（TASK-0495）—— 沿射線／折線／平滑曲線打出去，碰到的第一格（往裡 depth 層）換成噴漆的顏色；只換色、不放不刻。
//          一條基準射線可以沿一個向量（sweep）或一條線（sweep_path，例：貼著舷弧）平行複製，再加第二個方向（sweep2，例：帶子的寬度）
//          ⇒ 一刀噴一整條（Tim 2026-10-11：「應用情境是類似目前船殼塗裝，希望可以一次噴一整條」）。
// 物理意義：噴漆是「從外面打過去、碰到哪格就漆哪格」—— 哪一層算船殼表面由幾何決定，不必在設計端先算出那一層再 stampvox overwrite。
//          ⭐ 座標以**格心**為準：輸入的整數 k ＝ 第 k 格的中心（世界座標 k＋0.5，格 k 佔 [k, k+1)）；小數可以。接縫規則（寫死、文件同字）：
//            ① 起點剛好在兩格的接縫上 ⇒ 屬於射線**前進方向**那一側的格；
//            ② 射線貼著接縫面平行走（剛好在面上）⇒ 算**座標較大**那一側的格；
//            ③ 射線剛好穿過格的邊或角（同時跨過兩個以上的面）⇒ 一步同時跨過 —— 只被擦到邊／角、沒有穿過內部的格**不算打到**。
//          打中 ＝ 沿路第一個實心格（起點那格也算）；它的原色不在 only_color 裡 ⇒ 這一道**被擋下**（漆不穿牆，什麼都不換）。
//          depth ＞ 1 ⇒ 從打中那格起沿射線連續往裡漆，遇到空格或不在 only_color 的格就停。
// 數值影響：所有射線都看同一份「噴之前」的作品（彼此不影響，順序無關）；同一格被幾道打到都只換一次。
//          每刀最多 <see cref="MaxRays"/> 道射線、<see cref="SCP_SculptEngine.MaxVolume"/> 格變動。
// 失敗處置：參數不合 ⇒ exit 2；預設只預覽（零事件），印 plan_sha256；confirm 必帶 expect_plan（對不上 ⇒ exit 4、零事件）；
//          什麼都沒換到 ⇒ 預覽回 0、confirm 回 exit 3 零事件。
//          ⭐ 事件記成 `op=workedit`、`edit.action=spray`（edit 只用舊版就有的欄位）＋ 最上層 `source` ⇒ **沒更新的 senate.exe 也重播得出來**，undo／redo／草稿走既有的路。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptSprayArgs
    {
        public string Persona = "";
        /// <summary>噴漆色 1..255。</summary>
        public int Color;
        /// <summary>射線三選一：from＋dir（打到空間邊緣）／from＋to（線段，含終點那格）／path（折線，curve=smooth ⇒ 平滑曲線）。</summary>
        public string From = "", Dir = "", To = "", Path = "";
        /// <summary>line（預設，折線）｜smooth（Catmull-Rom，通過每個控制點）—— path 與 sweep_path 都吃。</summary>
        public string Curve = CurveLine;
        /// <summary>平行複製：sweep＝沿一個向量（0..sweep，每步 ≤ 1 格）／sweep_path＝沿一條線（偏移 ＝ 線上的點 − 線的第一點）；兩者擇一。sweep2 ＝ 再沿第二個向量（面／帶寬）。</summary>
        public string Sweep = "", SweepPath = "", Sweep2 = "";
        /// <summary>只噴這些原色（逗號分隔；空 ＝ 不限）。</summary>
        public string OnlyColor = "";
        public int Depth = 1;
        /// <summary>false ＝ 只預覽（零事件）。</summary>
        public bool Confirm;
        /// <summary>confirm 必帶：預覽印的 plan_sha256。</summary>
        public string ExpectPlan = "";
        /// <summary>confirm 選帶：預覽印的換色格數。</summary>
        public int? ExpectChanged;
        /// <summary>給了 ⇒ 寫換色清單（每行 x,y,z,before,after，照 x→y→z 排）；預覽與落刀都寫。</summary>
        public string Out = "";

        public const string CurveLine = "line", CurveSmooth = "smooth";
    }

    public sealed class SCP_SculptSprayResult : SCP_SculptResult
    {
        public bool Applied;
        public int Rays, RaysHit, RaysMissed, RaysBlocked;
        /// <summary>真的換了色的格數（＝ 事件 edit.after 的筆數）。</summary>
        public int Changed;
        /// <summary>打到了、但本來就是噴漆色的格（不進事件）。</summary>
        public int Already;
        /// <summary>原色 → 換掉幾格。</summary>
        public SortedDictionary<int, int> ByColor = new SortedDictionary<int, int>();
        public string PlanSha256 = "";
        public string OutputPath = "";
        public string EventFile = "";
        /// <summary>照 x→y→z 排的換色清單（x,y,z,before,after）—— 預覽與落刀同一份。</summary>
        public List<int[]> Rows = new List<int[]>();
    }

    public sealed partial class SCP_SculptEngine
    {
        /// <summary>每刀射線上限（射線 ＝ 平行複製後的道數）。</summary>
        public const int MaxRays = 1000000;
        public const int MaxSprayDepth = 256;
        public const string SprayAction = "spray";
        const double SprayEps = 1e-9;

        /// <summary>噴漆：預覽（預設）或落刀（confirm）。呼叫端握作品鎖。</summary>
        public SCP_SculptResult Spray(SCP_SculptSprayArgs iArgs)
        {
            // ── 參數 ──
            if (iArgs.Color < 1 || iArgs.Color > 255) return SCP_SculptResult.Error(2, "spray 要 color=1..255（0 在 3D 是空；噴漆只換色）");
            if (iArgs.Depth < 1 || iArgs.Depth > MaxSprayDepth) return SCP_SculptResult.Error(2, "depth 要是 1.." + MaxSprayDepth + "（got " + iArgs.Depth + "）");
            if (iArgs.Curve != SCP_SculptSprayArgs.CurveLine && iArgs.Curve != SCP_SculptSprayArgs.CurveSmooth)
                return SCP_SculptResult.Error(2, "curve 要是 line 或 smooth（got '" + iArgs.Curve + "'）");
            bool aSmooth = iArgs.Curve == SCP_SculptSprayArgs.CurveSmooth;
            HashSet<int>? aOnly = null;
            if (iArgs.OnlyColor.Trim().Length > 0)
            {
                aOnly = new HashSet<int>();
                foreach (string p in iArgs.OnlyColor.Split(','))
                {
                    if (!SCP_SculptPy.TryInt(p.Trim(), out int c) || c < 1 || c > 255) return SCP_SculptResult.Error(2, "only_color 要是 1..255 的整數（逗號分隔）：" + iArgs.OnlyColor);
                    aOnly.Add(c);
                }
            }

            bool hasFrom = iArgs.From.Trim().Length > 0, hasDir = iArgs.Dir.Trim().Length > 0, hasTo = iArgs.To.Trim().Length > 0, hasPath = iArgs.Path.Trim().Length > 0;
            double[]? aDir = null;
            List<double[]> aRay;   // 基準射線（世界座標）：aDir 有 ⇒ 只用第一點、沿 aDir 打到空間邊緣；否則是折線
            string aKind;
            if (hasPath)
            {
                if (hasFrom || hasDir || hasTo) return SCP_SculptResult.Error(2, "射線三選一：path 不能再帶 from／dir／to");
                if (!TryPoints(iArgs.Path, out List<double[]> aPts, out string aErr)) return SCP_SculptResult.Error(2, "path " + aErr);
                if (aPts.Count < 2) return SCP_SculptResult.Error(2, "path 至少兩個點（x,y,z;x,y,z;…）");
                aRay = ToWorld(aSmooth ? Smooth(aPts, 0.25) : aPts);
                aKind = aSmooth ? "curve" : "polyline";
            }
            else
            {
                if (!hasFrom) return SCP_SculptResult.Error(2, "spray 要一道射線：from＋dir、from＋to，或 path=x,y,z;x,y,z;…");
                if (hasDir == hasTo) return SCP_SculptResult.Error(2, "from 之後 dir 與 to 擇一（dir ＝ 打到空間邊緣；to ＝ 打到那一點為止）");
                if (!TryPoint(iArgs.From, out double[] aFrom)) return SCP_SculptResult.Error(2, "from 要是 x,y,z（數字，可帶小數）：" + iArgs.From);
                if (hasDir)
                {
                    if (!TryPoint(iArgs.Dir, out double[] d)) return SCP_SculptResult.Error(2, "dir 要是 dx,dy,dz：" + iArgs.Dir);
                    double aLen = Math.Sqrt(d[0] * d[0] + d[1] * d[1] + d[2] * d[2]);
                    if (aLen < SprayEps) return SCP_SculptResult.Error(2, "dir 不能是零向量");
                    aDir = new[] { d[0] / aLen, d[1] / aLen, d[2] / aLen };
                    aRay = ToWorld(new List<double[]> { aFrom });
                    aKind = "ray";
                }
                else
                {
                    if (!TryPoint(iArgs.To, out double[] aTo)) return SCP_SculptResult.Error(2, "to 要是 x,y,z：" + iArgs.To);
                    if (Math.Abs(aTo[0] - aFrom[0]) + Math.Abs(aTo[1] - aFrom[1]) + Math.Abs(aTo[2] - aFrom[2]) < SprayEps) return SCP_SculptResult.Error(2, "from 與 to 是同一點");
                    aRay = ToWorld(new List<double[]> { aFrom, aTo });
                    aKind = "segment";
                }
            }

            // ── 平行複製 ──
            if (iArgs.Sweep.Trim().Length > 0 && iArgs.SweepPath.Trim().Length > 0) return SCP_SculptResult.Error(2, "sweep 與 sweep_path 擇一（第二個方向用 sweep2）");
            List<double[]> aA, aB;
            if (iArgs.Sweep.Trim().Length > 0)
            {
                if (!TryPoint(iArgs.Sweep, out double[] v)) return SCP_SculptResult.Error(2, "sweep 要是 dx,dy,dz：" + iArgs.Sweep);
                aA = LineOffsets(v);
            }
            else if (iArgs.SweepPath.Trim().Length > 0)
            {
                if (!TryPoints(iArgs.SweepPath, out List<double[]> aPts, out string aErr)) return SCP_SculptResult.Error(2, "sweep_path " + aErr);
                if (aPts.Count < 2) return SCP_SculptResult.Error(2, "sweep_path 至少兩個點");
                aA = PathOffsets(aPts, aSmooth);
            }
            else aA = new List<double[]> { new double[3] };
            if (iArgs.Sweep2.Trim().Length > 0)
            {
                if (!TryPoint(iArgs.Sweep2, out double[] v)) return SCP_SculptResult.Error(2, "sweep2 要是 dx,dy,dz：" + iArgs.Sweep2);
                aB = LineOffsets(v);
            }
            else aB = new List<double[]> { new double[3] };
            if ((long)aA.Count * aB.Count > MaxRays)
                return SCP_SculptResult.Error(2, "射線 " + ((long)aA.Count * aB.Count).ToString("N0", CultureInfo.InvariantCulture) + " 道，超過單刀上限 " + MaxRays.ToString("N0", CultureInfo.InvariantCulture) + " —— 拆成幾刀");
            var aOffsets = new List<double[]>();
            var aSeenOff = new HashSet<(long, long, long)>();
            foreach (double[] a in aA)
                foreach (double[] b in aB)
                {
                    double[] o = { a[0] + b[0], a[1] + b[1], a[2] + b[2] };
                    if (aSeenOff.Add(((long)Math.Round(o[0] * 1e6), (long)Math.Round(o[1] * 1e6), (long)Math.Round(o[2] * 1e6)))) aOffsets.Add(o);
                }

            // ── 打 ──
            SCP_SculptSpace aSpace;
            try { aSpace = LoadSpace(); } catch (SCP_SculptReplayException e) { return ReplayFail(e); }
            var aRes = new SCP_SculptSprayResult { Rays = aOffsets.Count };
            var aChange = new Dictionary<long, int>();   // 格 → 原色
            var aAlready = new HashSet<long>();
            foreach (double[] o in aOffsets)
            {
                int aOutcome = 0;   // 0 落空、1 打中、2 擋下
                int aPainted = 0;
                bool Visit(int x, int y, int z)
                {
                    int c = aSpace.Voxels.Get(x, y, z);
                    if (aOutcome == 0)
                    {
                        if (c == 0) return true;
                        if (aOnly != null && !aOnly.Contains(c)) { aOutcome = 2; return false; }
                        aOutcome = 1;
                    }
                    else if (c == 0 || (aOnly != null && !aOnly.Contains(c))) return false;
                    long k = PackKey(x, y, z);
                    if (c == iArgs.Color) aAlready.Add(k); else aChange[k] = c;
                    return ++aPainted < iArgs.Depth;
                }
                if (aDir != null)
                    TraceSprayRay(new[] { aRay[0][0] + o[0], aRay[0][1] + o[1], aRay[0][2] + o[2] }, aDir, double.PositiveInfinity, Visit);
                else
                {
                    long aLast = -1;
                    for (int i = 0; i + 1 < aRay.Count; i++)
                    {
                        double[] p = { aRay[i][0] + o[0], aRay[i][1] + o[1], aRay[i][2] + o[2] };
                        double[] d = { aRay[i + 1][0] - aRay[i][0], aRay[i + 1][1] - aRay[i][1], aRay[i + 1][2] - aRay[i][2] };
                        if (Math.Abs(d[0]) + Math.Abs(d[1]) + Math.Abs(d[2]) < SprayEps) continue;
                        if (!TraceSprayRay(p, d, 1.0, Visit, ref aLast)) break;
                    }
                }
                if (aOutcome == 1) aRes.RaysHit++; else if (aOutcome == 2) aRes.RaysBlocked++; else aRes.RaysMissed++;
            }

            foreach (var kv in aChange)
            {
                aRes.Rows.Add(new[] { KeyX(kv.Key), KeyY(kv.Key), KeyZ(kv.Key), kv.Value, iArgs.Color });
                aRes.ByColor[kv.Value] = (aRes.ByColor.TryGetValue(kv.Value, out int n) ? n : 0) + 1;
            }
            aRes.Rows.Sort((p, q) => p[0] != q[0] ? p[0].CompareTo(q[0]) : p[1] != q[1] ? p[1].CompareTo(q[1]) : p[2].CompareTo(q[2]));
            aRes.Changed = aRes.Rows.Count;
            aRes.Already = aAlready.Count;
            var aBody = new StringBuilder(aRes.Rows.Count * 20);
            foreach (int[] r in aRes.Rows) aBody.Append(r[0]).Append(',').Append(r[1]).Append(',').Append(r[2]).Append(',').Append(r[3]).Append(',').Append(r[4]).Append('\n');
            aRes.PlanSha256 = SCP_SculptPy.Sha256Hex(new UTF8Encoding(false).GetBytes(aBody.ToString()));

            if (iArgs.Out.Length > 0)
            {
                aRes.OutputPath = SCP_SculptPy.PathStr(iArgs.Out);
                string? aParent = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(aRes.OutputPath));
                if (!string.IsNullOrEmpty(aParent)) Directory.CreateDirectory(aParent);
                File.WriteAllBytes(aRes.OutputPath, new UTF8Encoding(false).GetBytes(
                    "# spray plan —— x,y,z,before,after（照 x→y→z 排；before ＝ 噴之前的顏色）\n# plan_sha256 " + aRes.PlanSha256 + "（只算 # 以外的行）\n" + aBody));
            }

            var aSrc = new SCP_SculptPyObj()
                .Put("kind", "spray").Put("ray", aKind).Put("color", iArgs.Color)
                .Put("from", NullIfEmpty(iArgs.From)).Put("dir", NullIfEmpty(iArgs.Dir)).Put("to", NullIfEmpty(iArgs.To)).Put("path", NullIfEmpty(iArgs.Path))
                .Put("curve", iArgs.Curve).Put("sweep", NullIfEmpty(iArgs.Sweep)).Put("sweep_path", NullIfEmpty(iArgs.SweepPath)).Put("sweep2", NullIfEmpty(iArgs.Sweep2))
                .Put("only_color", NullIfEmpty(iArgs.OnlyColor)).Put("depth", iArgs.Depth)
                .Put("rays", aRes.Rays).Put("rays_hit", aRes.RaysHit).Put("rays_missed", aRes.RaysMissed).Put("rays_blocked", aRes.RaysBlocked)
                .Put("changed", aRes.Changed).Put("already", aRes.Already).Put("plan_sha256", aRes.PlanSha256);

            SprayLines(aRes, iArgs);
            if (aRes.Changed > MaxVolume)
                return Fail(aRes, 2, "要換色 " + aRes.Changed.ToString("N0", CultureInfo.InvariantCulture) + " 格，超過單刀上限 " + MaxVolume.ToString("N0", CultureInfo.InvariantCulture) + " —— 拆成幾刀");
            if (!iArgs.Confirm) return aRes;   // 預覽：零事件

            if (aRes.Changed == 0) return Fail(aRes, 3, "沒有任何一格要換色（落空／被擋下／本來就是這個顏色）—— 零事件");
            if (iArgs.ExpectPlan.Trim().Length == 0) return Fail(aRes, 2, "confirm 要帶 expect_plan=<預覽印的 plan_sha256> —— 落刀的格要跟你看過的那份逐格相同");
            if (!string.Equals(iArgs.ExpectPlan.Trim(), aRes.PlanSha256, StringComparison.OrdinalIgnoreCase))
                return Fail(aRes, 4, "expect_plan 對不上這一刀（預覽之後作品或參數變了）—— 重新預覽；零事件");
            if (iArgs.ExpectChanged.HasValue && iArgs.ExpectChanged.Value != aRes.Changed)
                return Fail(aRes, 4, "expect_placed " + iArgs.ExpectChanged.Value + " 與這一刀要換色的 " + aRes.Changed + " 格不符 —— 零事件");

            var aEdit = new SCP_SculptEdit { action = SprayAction };
            foreach (int[] r in aRes.Rows)
            {
                aEdit.before.Add(new SCP_SculptChange(r[0], r[1], r[2], r[3]));
                aEdit.after.Add(new SCP_SculptChange(r[0], r[1], r[2], r[4]));
            }
            aRes.EventFile = CommitEdit(aEdit, iArgs.Persona, aSrc);
            aRes.Applied = true;
            SprayLines(aRes, iArgs);
            return aRes;
        }

        static SCP_SculptResult Fail(SCP_SculptSprayResult iRes, int iExit, string iWhy)
        {
            iRes.ExitCode = iExit;
            iRes.Lines.Add("❌ " + iWhy);
            return iRes;
        }

        static string? NullIfEmpty(string s) => s.Trim().Length == 0 ? null : s.Trim();

        void SprayLines(SCP_SculptSprayResult r, SCP_SculptSprayArgs a)
        {
            r.Lines.Clear();
            r.Lines.Add(r.Applied ? "# 🎨 噴漆完成（只換色）" : "# 🎨 噴漆預覽（零事件 —— 確認才落刀）");
            r.Lines.Add("  射線      : " + r.Rays + " 道（打中 " + r.RaysHit + "、落空 " + r.RaysMissed + "、被原色限制擋下 " + r.RaysBlocked + "）");
            var aBy = new StringBuilder();
            foreach (var kv in r.ByColor) aBy.Append(aBy.Length > 0 ? " " : "").Append('c').Append(kv.Key).Append('×').Append(kv.Value);
            r.Lines.Add("  換色      : " + r.Changed + " 格 → c" + a.Color + (aBy.Length > 0 ? "（原色 " + aBy + "）" : "") + "；本來就是 c" + a.Color + " 的 " + r.Already + " 格");
            r.Lines.Add("  plan_sha256: " + r.PlanSha256);
            if (r.OutputPath.Length > 0) r.Lines.Add("  清單      : " + r.OutputPath);
            if (r.Applied) r.Lines.Add("  事件      : " + r.EventFile + "（workedit／spray；undo 照常）");
            else if (r.Changed > 0) r.Lines.Add("  ⇒ 落刀：同樣的參數加 confirm=1 expect_plan=" + r.PlanSha256);
        }

        // ───────────────────────── 射線走格 ─────────────────────────
        bool TraceSprayRay(double[] iA, double[] iD, double iTEnd, Func<int, int, int, bool> iVisit)
        {
            long aLast = -1;
            return TraceSprayRay(iA, iD, iTEnd, iVisit, ref aLast);
        }

        /// <summary>
        /// 世界座標的 A＋t·D（t ∈ [0, iTEnd]）依序走過的格（Amanatides–Woo），先裁到空間內；接縫規則見檔頭 ①②③。
        /// 與 <paramref name="ioLast"/> 相同的格不重複拜訪（折線的接點）。visitor 回 false ⇒ 停、本函式回 false。
        /// </summary>
        bool TraceSprayRay(double[] iA, double[] iD, double iTEnd, Func<int, int, int, bool> iVisit, ref long ioLast)
        {
            int[] S = { SizeX, SizeY, SizeZ };
            double t0 = 0, t1 = iTEnd;
            for (int k = 0; k < 3; k++)
            {
                if (Math.Abs(iD[k]) < SprayEps)
                {
                    if (iA[k] < 0 || iA[k] >= S[k]) return true;   // 平行於這一軸而且在外面 ⇒ 碰不到
                    continue;
                }
                double ta = (0 - iA[k]) / iD[k], tb = (S[k] - iA[k]) / iD[k];
                if (ta > tb) (ta, tb) = (tb, ta);
                t0 = Math.Max(t0, ta); t1 = Math.Min(t1, tb);
            }
            if (t0 >= t1 - SprayEps) return true;   // 沒有穿過空間內部（含只擦到邊界）

            var cell = new int[3];
            var step = new int[3];
            var tMax = new double[3];
            var tDelta = new double[3];
            for (int k = 0; k < 3; k++)
            {
                double p = iA[k] + iD[k] * t0;
                if (Math.Abs(iD[k]) < SprayEps)
                {
                    cell[k] = (int)Math.Floor(p);               // ② 貼著接縫 ⇒ 較大那一側（floor）
                    step[k] = 0; tMax[k] = double.PositiveInfinity; tDelta[k] = double.PositiveInfinity;
                }
                else if (iD[k] > 0)
                {
                    cell[k] = (int)Math.Floor(p + SprayEps);    // ① 起點在接縫上 ⇒ 前進方向那一格
                    step[k] = 1; tMax[k] = (cell[k] + 1 - iA[k]) / iD[k]; tDelta[k] = 1 / iD[k];
                }
                else
                {
                    cell[k] = (int)Math.Ceiling(p - SprayEps) - 1;
                    step[k] = -1; tMax[k] = (cell[k] - iA[k]) / iD[k]; tDelta[k] = -1 / iD[k];
                }
                if (cell[k] < 0) cell[k] = 0;
                if (cell[k] >= S[k]) cell[k] = S[k] - 1;
            }
            while (true)
            {
                long aKey = PackKey(cell[0], cell[1], cell[2]);
                if (aKey != ioLast)
                {
                    ioLast = aKey;
                    if (!iVisit(cell[0], cell[1], cell[2])) return false;
                }
                double tn = Math.Min(tMax[0], Math.Min(tMax[1], tMax[2]));
                if (tn >= t1 - SprayEps) return true;   // 終點在這一格裡，或剛好落在它的邊界上（沒穿進下一格）
                double aTol = SprayEps * Math.Max(1.0, Math.Abs(tn));
                for (int k = 0; k < 3; k++)
                    if (tMax[k] <= tn + aTol) { cell[k] += step[k]; tMax[k] += tDelta[k]; }   // ③ 同時跨過的面一起跨
                for (int k = 0; k < 3; k++) if (cell[k] < 0 || cell[k] >= S[k]) return true;
            }
        }

        // ───────────────────────── 幾何解析 ─────────────────────────
        static bool TryPoint(string iText, out double[] oP)
        {
            oP = new double[3];
            string[] p = iText.Trim().Split(',');
            if (p.Length != 3) return false;
            for (int k = 0; k < 3; k++)
                if (!double.TryParse(p[k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out oP[k])
                    || double.IsNaN(oP[k]) || double.IsInfinity(oP[k]) || Math.Abs(oP[k]) > 1e6) return false;
            return true;
        }

        static bool TryPoints(string iText, out List<double[]> oPts, out string oErr)
        {
            oPts = new List<double[]>();
            oErr = "";
            string[] aParts = iText.Trim().Split(';');
            for (int i = 0; i < aParts.Length; i++)
            {
                if (aParts[i].Trim().Length == 0) continue;
                if (!TryPoint(aParts[i], out double[] p)) { oErr = "第 " + (i + 1) + " 個點要是 x,y,z：'" + aParts[i].Trim() + "'（點與點用 ; 隔開）"; return false; }
                oPts.Add(p);
            }
            return true;
        }

        /// <summary>輸入座標（整數 ＝ 格心）→ 世界座標（＋0.5）。</summary>
        static List<double[]> ToWorld(List<double[]> iPts)
        {
            var aOut = new List<double[]>(iPts.Count);
            foreach (double[] p in iPts) aOut.Add(new[] { p[0] + 0.5, p[1] + 0.5, p[2] + 0.5 });
            return aOut;
        }

        /// <summary>Catmull-Rom（uniform）通過每個控制點；每段切到弦長 ≤ <paramref name="iChord"/> 格。</summary>
        static List<double[]> Smooth(List<double[]> iPts, double iChord)
        {
            var aOut = new List<double[]> { iPts[0] };
            for (int i = 0; i + 1 < iPts.Count; i++)
            {
                double[] p0 = iPts[Math.Max(0, i - 1)], p1 = iPts[i], p2 = iPts[i + 1], p3 = iPts[Math.Min(iPts.Count - 1, i + 2)];
                double aLen = Math.Sqrt(Sq(p2[0] - p1[0]) + Sq(p2[1] - p1[1]) + Sq(p2[2] - p1[2]));
                int n = Math.Max(1, (int)Math.Ceiling(aLen / iChord));
                for (int j = 1; j <= n; j++)
                {
                    double t = (double)j / n, t2 = t * t, t3 = t2 * t;
                    var q = new double[3];
                    for (int k = 0; k < 3; k++)
                        q[k] = 0.5 * (2 * p1[k] + (-p0[k] + p2[k]) * t + (2 * p0[k] - 5 * p1[k] + 4 * p2[k] - p3[k]) * t2 + (-p0[k] + 3 * p1[k] - 3 * p2[k] + p3[k]) * t3);
                    aOut.Add(q);
                }
            }
            return aOut;
        }

        static double Sq(double v) => v * v;

        /// <summary>0..v 每步 ≤ 1 格（以最長的那一軸算步數）；含兩端。</summary>
        static List<double[]> LineOffsets(double[] iV)
        {
            int n = (int)Math.Ceiling(Math.Max(Math.Abs(iV[0]), Math.Max(Math.Abs(iV[1]), Math.Abs(iV[2]))) - SprayEps);
            var aOut = new List<double[]>();
            if (n <= 0) { aOut.Add(new double[3]); return aOut; }
            for (int i = 0; i <= n; i++) aOut.Add(new[] { iV[0] * i / n, iV[1] * i / n, iV[2] * i / n });
            return aOut;
        }

        /// <summary>
        /// 沿一條線的偏移（線上的點 − 線的第一點），每步 ≤ 1 格：折線逐段照最長軸切；
        /// 平滑曲線先細切再沿線取點（跟上一個取的點在任一軸差到 1 格才取，終點一定取）。
        /// </summary>
        static List<double[]> PathOffsets(List<double[]> iPts, bool iSmooth)
        {
            double[] o = iPts[0];
            var aOut = new List<double[]> { new double[3] };
            if (!iSmooth)
            {
                for (int i = 0; i + 1 < iPts.Count; i++)
                {
                    double[] a = iPts[i], b = iPts[i + 1];
                    double[] d = { b[0] - a[0], b[1] - a[1], b[2] - a[2] };
                    int n = (int)Math.Ceiling(Math.Max(Math.Abs(d[0]), Math.Max(Math.Abs(d[1]), Math.Abs(d[2]))) - SprayEps);
                    for (int j = 1; j <= n; j++) aOut.Add(new[] { a[0] + d[0] * j / n - o[0], a[1] + d[1] * j / n - o[1], a[2] + d[2] * j / n - o[2] });
                }
                return aOut;
            }
            List<double[]> aFine = Smooth(iPts, 0.05);
            double[] aLastTaken = aFine[0];
            for (int i = 1; i < aFine.Count; i++)
            {
                double[] p = aFine[i];
                bool aEnd = i == aFine.Count - 1;
                double aCheb = Math.Max(Math.Abs(p[0] - aLastTaken[0]), Math.Max(Math.Abs(p[1] - aLastTaken[1]), Math.Abs(p[2] - aLastTaken[2])));
                if (aCheb >= 1 - SprayEps || (aEnd && aCheb > SprayEps)) { aOut.Add(new[] { p[0] - o[0], p[1] - o[1], p[2] - o[2] }); aLastTaken = p; }
            }
            return aOut;
        }
    }
}
