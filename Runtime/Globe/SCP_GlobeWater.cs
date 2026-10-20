// 區塊職責：球面的**水體層** —— 海平面以上（或以下）的內陸水：湖（外環＋湖中島＋水面海拔）與河（上游→下游的點列，每點水面海拔與寬度），
//          河的源頭（泉／湖的出口）與河口（海／湖／另一條河 ＝ 支流），落差判成瀑布，再柵格化成疊在格子顏色上的一層。
// 物理意義：水體是**向量**，⛔ 不寫進格子顏色（塗成藍色的陸地，程式分不出它是水）。正本是 `water/events/` 一筆一檔、只追加；
//          每筆 set／delete 帶「新版本」與「舊版本」整份 ⇒ Undo ＝ 追加一筆還原事件、只能從最後一筆退（堆疊），跟格子的 Undo 各退各的。
//          每次寫入都對**整個水系**重驗：往上游流、接到不存在的湖或河、端點離接點太遠、繞成環 ⇒ 整筆拒絕、零寫入。
//          重驗只擋「這一筆新造成的問題」：regrid 讓容許距離變小而冒出的舊問題不會把無關的寫入卡死（check 照樣印出來）。
// 數值影響：海拔一律公尺（水面，可以是負的：低於海平面的湖）；寬度公尺；經緯度度。
//          瀑布門檻住 `water/meta.json`（FallDrop 公尺、FallGrade 落差／水平距離），全球共用一份 ⇒ 大家看到同一組瀑布。
//          舊版程式不讀 `water/` ⇒ 照舊只看格子顏色、看不到水（不會算錯格子）。
// ⚠ 方言：C# 9／netstandard2.1（見 Coding_Standards §1）—— 沒有 record、沒有檔案級 namespace。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Globe
{
    /// <summary>一圈點（扁平 lat,lon,lat,lon…）；湖中島用。</summary>
    public sealed class SCP_GlobeWaterRing
    {
        public List<double> Pts { get; set; } = new List<double>();
    }

    /// <summary>一個水體（湖或河）。Id 空字串 ＝「沒有這個水體」（事件裡表示新增前／刪除後）。</summary>
    public sealed class SCP_GlobeWaterFeature
    {
        public const string KindLake = "lake", KindRiver = "river";
        public const string FromSpring = "spring", ToSea = "sea";

        public string Id { get; set; } = "";
        /// <summary>lake｜river。</summary>
        public string Kind { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>湖：水面海拔（公尺；可以是負的）。河不用（每點自己帶）。</summary>
        public double Surface { get; set; }
        /// <summary>湖：外環，扁平 lat,lon,…（自動封口）。</summary>
        public List<double> Ring { get; set; } = new List<double>();
        /// <summary>湖：湖中島（每圈自動封口）。</summary>
        public List<SCP_GlobeWaterRing> Holes { get; set; } = new List<SCP_GlobeWaterRing>();
        /// <summary>河：上游 → 下游，扁平 lat,lon,水面海拔（m）,寬（m）,…</summary>
        public List<double> Points { get; set; } = new List<double>();
        /// <summary>河的源頭：spring｜lake:&lt;id&gt;（那個湖的出口）。</summary>
        public string From { get; set; } = FromSpring;
        /// <summary>河口：sea｜lake:&lt;id&gt;｜river:&lt;id&gt;（支流）。</summary>
        public string To { get; set; } = ToSea;
        /// <summary>資料與海拔的出處（例：Natural Earth 10m lakes；水面 Wikipedia）。</summary>
        public string Source { get; set; } = "";
        /// <summary>算在哪個施工區（選填）。</summary>
        public string Zone { get; set; } = "";
        public string UpdatedBy { get; set; } = "";
        public string UpdatedAt { get; set; } = "";

        public bool IsLake => Kind == KindLake;
        public bool IsRiver => Kind == KindRiver;
        public int VertexCount => IsLake ? Ring.Count / 2 : Points.Count / 4;
        public double Lat(int k) => IsLake ? Ring[2 * k] : Points[4 * k];
        public double Lon(int k) => IsLake ? Ring[2 * k + 1] : Points[4 * k + 1];
        public double H(int k) => Points[4 * k + 2];
        public double W(int k) => Points[4 * k + 3];

        public SCP_GlobeWaterFeature Clone()
        {
            var c = new SCP_GlobeWaterFeature();
            SCP_JsonMapper.Populate(c, SCP_JsonData.Parse(SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(this), false)));
            return c;
        }
    }

    public sealed class SCP_GlobeWaterEvent
    {
        public int Seq { get; set; }
        /// <summary>set（新增或修改）｜delete｜undo。</summary>
        public string Op { get; set; } = "";
        public string Persona { get; set; } = "";
        public string At { get; set; } = "";
        public string Note { get; set; } = "";
        public string Id { get; set; } = "";
        /// <summary>這一筆之後的版本（delete ⇒ Id 空）。</summary>
        public SCP_GlobeWaterFeature Feature { get; set; } = new SCP_GlobeWaterFeature();
        /// <summary>這一筆之前的版本（新增 ⇒ Id 空）。Undo 就是把它寫回去。</summary>
        public SCP_GlobeWaterFeature Prev { get; set; } = new SCP_GlobeWaterFeature();
        /// <summary>undo：退的是哪一筆。</summary>
        public int Target { get; set; }
    }

    /// <summary>`water/meta.json`：瀑布門檻（全球一份）。</summary>
    public sealed class SCP_GlobeWaterMeta
    {
        public const double DefaultFallDrop = 10, DefaultFallGrade = 0.1;
        public int Version { get; set; } = 1;
        /// <summary>落差至少幾公尺才算瀑布（河身一段、匯入支流、入湖、入海都用它）。</summary>
        public double FallDrop { get; set; } = DefaultFallDrop;
        /// <summary>河身一段還要夠陡：落差／水平距離 ≥ 它（0.1 ＝ 每 100 m 掉 10 m）。匯入、入湖、入海是垂直落下，不看它。</summary>
        public double FallGrade { get; set; } = DefaultFallGrade;
    }

    /// <summary>一座瀑布（由落差推出來的讀數，⛔ 不存檔）。</summary>
    public sealed class SCP_GlobeWaterFall
    {
        public string River = "";
        /// <summary>segment（河身一段）｜junction（支流匯入）｜lake（入湖）｜sea（入海）。</summary>
        public string Kind = "";
        /// <summary>瀑布頂（崖口）的位置。</summary>
        public double Lat, Lon;
        public double Drop;
        /// <summary>河身一段：落差／水平距離；垂直落下 ⇒ +∞。</summary>
        public double Grade;
        /// <summary>segment：第幾點 → 下一點（0 起算）；其他 ＝ 最後一點。</summary>
        public int Vertex;
        /// <summary>落到哪裡：下一點、支流匯入的那條河、湖、海。</summary>
        public string Into = "";
    }

    public sealed class SCP_GlobeWaterReport
    {
        public int Lakes, Rivers, Tributaries, Springs, LakeOutlets, ToSea, ToLake;
        public List<SCP_GlobeWaterFall> Falls = new List<SCP_GlobeWaterFall>();
        public List<string> Problems = new List<string>();
    }

    /// <summary>重播後的水體層：目前的水體＋可退的堆疊。</summary>
    public sealed class SCP_GlobeWaterSet
    {
        public SortedDictionary<string, SCP_GlobeWaterFeature> Features = new SortedDictionary<string, SCP_GlobeWaterFeature>(StringComparer.Ordinal);
        public List<int> Stack = new List<int>();
        public int LastSeq;
        public SCP_GlobeWaterMeta Meta = new SCP_GlobeWaterMeta();
        /// <summary>曾經用過的 id（刪掉的也算）⇒ 自動編號不重用。</summary>
        public HashSet<string> UsedIds = new HashSet<string>(StringComparer.Ordinal);

        public int Count => Features.Count;

        public string NextId(string iKind)
        {
            int aMax = 0;
            string aPrefix = iKind + "-";
            foreach (string id in UsedIds)
                if (id.StartsWith(aPrefix, StringComparison.Ordinal)
                    && int.TryParse(id.Substring(aPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int n) && n > aMax) aMax = n;
            return aPrefix + (aMax + 1).ToString(CultureInfo.InvariantCulture);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 水系：驗證、支流、瀑布
    // ════════════════════════════════════════════════════════════════════════════
    public static class SCP_GlobeWaterNet
    {
        /// <summary>湖面、出口、匯入時，兩個水面差多少公尺以內算「同一個水面」。</summary>
        public const double LevelTol = 0.5;
        const double EarthR = 6371000;

        /// <summary>
        /// 整個水系的讀數。<paramref name="iTolRad"/>＝端點離它要接的湖岸／河道最遠幾弧度（呼叫端給「兩格」）。
        /// 每一條問題都寫成「哪一個水體、哪一點、為什麼」。
        /// </summary>
        public static SCP_GlobeWaterReport Analyze(IReadOnlyDictionary<string, SCP_GlobeWaterFeature> iAll, SCP_GlobeWaterMeta iMeta, double iTolRad)
        {
            var r = new SCP_GlobeWaterReport();
            var aNext = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // 流向圖：節點 → 下游節點
            void Edge(string a, string b)
            {
                if (!aNext.TryGetValue(a, out List<string>? l)) aNext[a] = l = new List<string>();
                l.Add(b);
            }
            foreach (KeyValuePair<string, SCP_GlobeWaterFeature> kv in iAll)
            {
                SCP_GlobeWaterFeature f = kv.Value;
                if (f.IsLake) { r.Lakes++; CheckLake(f, r.Problems); continue; }
                if (!f.IsRiver) { r.Problems.Add($"{f.Id}：不認得的種類「{f.Kind}」（lake｜river）"); continue; }
                r.Rivers++;
                if (!CheckRiverShape(f, r.Problems)) continue;
                int n = f.VertexCount;

                // 河身：只准往下流；夠高又夠陡的一段 ⇒ 瀑布
                for (int k = 0; k + 1 < n; k++)
                {
                    double drop = f.H(k) - f.H(k + 1);
                    if (drop < -1e-9) { r.Problems.Add($"{f.Id}：第 {k + 1}→{k + 2} 點往上游流（水面 {M(f.H(k))} → {M(f.H(k + 1))} m）"); continue; }
                    double d = Dist(f.Lat(k), f.Lon(k), f.Lat(k + 1), f.Lon(k + 1));
                    double grade = d > 0 ? drop / d : double.PositiveInfinity;
                    if (drop >= iMeta.FallDrop && grade >= iMeta.FallGrade)
                        r.Falls.Add(new SCP_GlobeWaterFall { River = f.Id, Kind = "segment", Lat = f.Lat(k), Lon = f.Lon(k), Drop = drop, Grade = grade, Vertex = k, Into = $"第 {k + 2} 點" });
                }

                // 源頭
                if (f.From == SCP_GlobeWaterFeature.FromSpring) r.Springs++;
                else if (TryRef(f.From, "lake", out string aLakeId))
                {
                    if (!iAll.TryGetValue(aLakeId, out SCP_GlobeWaterFeature? lake) || !lake.IsLake) r.Problems.Add($"{f.Id}：源頭接的湖 {aLakeId} 不存在");
                    else
                    {
                        r.LakeOutlets++;
                        Edge("lake:" + aLakeId, "river:" + f.Id);
                        if (!NearLake(lake, f.Lat(0), f.Lon(0), iTolRad)) r.Problems.Add($"{f.Id}：第 1 點離 {aLakeId} 的湖岸太遠（源頭要在湖裡或湖岸上）");
                        if (Math.Abs(f.H(0) - lake.Surface) > LevelTol) r.Problems.Add($"{f.Id}：湖的出口水面要等於湖面（第 1 點 {M(f.H(0))} m，{aLakeId} 湖面 {M(lake.Surface)} m）");
                    }
                }
                else r.Problems.Add($"{f.Id}：源頭要寫 spring 或 lake:<id>：「{f.From}」");

                // 河口
                int e = n - 1;
                double aEndH = f.H(e);
                if (f.To == SCP_GlobeWaterFeature.ToSea)
                {
                    r.ToSea++;
                    if (aEndH < -LevelTol) r.Problems.Add($"{f.Id}：河口水面 {M(aEndH)} m 低於海平面，流不進海");
                    else if (aEndH >= iMeta.FallDrop)
                        r.Falls.Add(new SCP_GlobeWaterFall { River = f.Id, Kind = "sea", Lat = f.Lat(e), Lon = f.Lon(e), Drop = aEndH, Grade = double.PositiveInfinity, Vertex = e, Into = "海" });
                }
                else if (TryRef(f.To, "lake", out string aInto))
                {
                    if (!iAll.TryGetValue(aInto, out SCP_GlobeWaterFeature? lake) || !lake.IsLake) r.Problems.Add($"{f.Id}：河口接的湖 {aInto} 不存在");
                    else
                    {
                        r.ToLake++;
                        Edge("river:" + f.Id, "lake:" + aInto);
                        if (!NearLake(lake, f.Lat(e), f.Lon(e), iTolRad)) r.Problems.Add($"{f.Id}：最後一點離 {aInto} 的湖岸太遠（河口要在湖裡或湖岸上）");
                        double drop = aEndH - lake.Surface;
                        if (drop < -LevelTol) r.Problems.Add($"{f.Id}：河口水面 {M(aEndH)} m 比 {aInto} 湖面 {M(lake.Surface)} m 低，流不進去");
                        else if (drop >= iMeta.FallDrop)
                            r.Falls.Add(new SCP_GlobeWaterFall { River = f.Id, Kind = "lake", Lat = f.Lat(e), Lon = f.Lon(e), Drop = drop, Grade = double.PositiveInfinity, Vertex = e, Into = aInto });
                    }
                }
                else if (TryRef(f.To, "river", out string aMain))
                {
                    if (aMain == f.Id) r.Problems.Add($"{f.Id}：河口不能接自己");
                    else if (!iAll.TryGetValue(aMain, out SCP_GlobeWaterFeature? main) || !main.IsRiver) r.Problems.Add($"{f.Id}：河口接的河 {aMain} 不存在");
                    else if (main.VertexCount >= 2)
                    {
                        r.Tributaries++;
                        Edge("river:" + f.Id, "river:" + aMain);
                        Junction(main, f.Lat(e), f.Lon(e), out double jLat, out double jLon, out double jH, out double jDist);
                        if (jDist > iTolRad) r.Problems.Add($"{f.Id}：最後一點離 {aMain} 的河道 {jDist * EarthR / 1000:0.#} km，太遠（支流要接到河道上）");
                        double drop = aEndH - jH;
                        if (drop < -LevelTol) r.Problems.Add($"{f.Id}：匯入處水面 {M(aEndH)} m 比 {aMain} 在那裡的水面 {M(jH)} m 低，流不進去");
                        else if (drop >= iMeta.FallDrop)
                            r.Falls.Add(new SCP_GlobeWaterFall { River = f.Id, Kind = "junction", Lat = f.Lat(e), Lon = f.Lon(e), Drop = drop, Grade = double.PositiveInfinity, Vertex = e, Into = aMain });
                    }
                }
                else r.Problems.Add($"{f.Id}：河口要寫 sea、lake:<id> 或 river:<id>：「{f.To}」");
            }

            // 繞成環：照流向走，回到自己 ⇒ 環
            var aState = new Dictionary<string, int>(StringComparer.Ordinal);   // 1＝走訪中、2＝走完
            var aPath = new List<string>();
            void Visit(string u)
            {
                aState[u] = 1; aPath.Add(u);
                if (aNext.TryGetValue(u, out List<string>? l))
                    foreach (string v in l)
                    {
                        aState.TryGetValue(v, out int st);
                        if (st == 1)
                        {
                            int at = aPath.IndexOf(v);
                            var aCycle = aPath.GetRange(at, aPath.Count - at);
                            aCycle.Add(v);
                            r.Problems.Add("水系繞成環：" + string.Join(" → ", aCycle));
                        }
                        else if (st == 0) Visit(v);
                    }
                aPath.RemoveAt(aPath.Count - 1);
                aState[u] = 2;
            }
            var aNodes = new List<string>(aNext.Keys);
            aNodes.Sort(StringComparer.Ordinal);
            foreach (string u in aNodes) if (!aState.ContainsKey(u)) Visit(u);
            return r;
        }

        static void CheckLake(SCP_GlobeWaterFeature f, List<string> iProblems)
        {
            if (f.Ring.Count % 2 != 0 || f.Ring.Count < 6) iProblems.Add($"{f.Id}：湖的外環至少 3 點");
            if (double.IsNaN(f.Surface) || double.IsInfinity(f.Surface)) iProblems.Add($"{f.Id}：湖面海拔不是數字");
            for (int k = 0; k < f.Holes.Count; k++)
                if (f.Holes[k].Pts.Count % 2 != 0 || f.Holes[k].Pts.Count < 6) iProblems.Add($"{f.Id}：第 {k + 1} 個湖中島至少 3 點");
        }

        static bool CheckRiverShape(SCP_GlobeWaterFeature f, List<string> iProblems)
        {
            if (f.Points.Count % 4 != 0 || f.Points.Count < 8) { iProblems.Add($"{f.Id}：河至少 2 點（每點 lat,lon,水面海拔,寬）"); return false; }
            bool ok = true;
            for (int k = 0; k < f.VertexCount; k++)
            {
                if (double.IsNaN(f.H(k)) || double.IsInfinity(f.H(k))) { iProblems.Add($"{f.Id}：第 {k + 1} 點的水面海拔不是數字"); ok = false; }
                if (!(f.W(k) > 0)) { iProblems.Add($"{f.Id}：第 {k + 1} 點的寬度要 > 0 m"); ok = false; }
            }
            return ok;
        }

        /// <summary>「lake:abc」→ abc。</summary>
        public static bool TryRef(string iText, string iKind, out string oId)
        {
            oId = "";
            string p = iKind + ":";
            if (iText == null || !iText.StartsWith(p, StringComparison.Ordinal) || iText.Length == p.Length) return false;
            oId = iText.Substring(p.Length);
            return true;
        }

        /// <summary>點在湖裡（外環內、島外），或離湖岸（外環與島的邊）≤ 容許距離。</summary>
        public static bool NearLake(SCP_GlobeWaterFeature iLake, double iLat, double iLon, double iTolRad)
        {
            if (InsideLake(iLake, iLat, iLon)) return true;
            if (RingDist(iLake.Ring, iLat, iLon) <= iTolRad) return true;
            foreach (SCP_GlobeWaterRing h in iLake.Holes) if (RingDist(h.Pts, iLat, iLon) <= iTolRad) return true;
            return false;
        }

        public static bool InsideLake(SCP_GlobeWaterFeature iLake, double iLat, double iLon)
        {
            if (!SCP_GlobeDraw.InsideRing(iLake.Ring, iLat, iLon)) return false;
            foreach (SCP_GlobeWaterRing h in iLake.Holes) if (SCP_GlobeDraw.InsideRing(h.Pts, iLat, iLon)) return false;
            return true;
        }

        static double RingDist(List<double> iRing, double iLat, double iLon)
        {
            int n = iRing.Count / 2;
            double best = double.MaxValue;
            for (int a = 0, b = n - 1; a < n; b = a++)
                best = Math.Min(best, SegDist(iRing[2 * b], iRing[2 * b + 1], iRing[2 * a], iRing[2 * a + 1], iLat, iLon, out _));
            return best;
        }

        /// <summary>支流的最後一點落在主流哪裡：主流河道上最近的一點、那裡的水面（照兩點之間線性內插）、距離（弧度）。</summary>
        public static void Junction(SCP_GlobeWaterFeature iMain, double iLat, double iLon, out double oLat, out double oLon, out double oH, out double oDist)
        {
            oLat = iMain.Lat(0); oLon = iMain.Lon(0); oH = iMain.H(0); oDist = double.MaxValue;
            for (int k = 0; k + 1 < iMain.VertexCount; k++)
            {
                double d = SegDist(iMain.Lat(k), iMain.Lon(k), iMain.Lat(k + 1), iMain.Lon(k + 1), iLat, iLon, out double t);
                if (d >= oDist) continue;
                oDist = d;
                oLat = iMain.Lat(k) + (iMain.Lat(k + 1) - iMain.Lat(k)) * t;
                oLon = iMain.Lon(k) + Wrap(iMain.Lon(k + 1) - iMain.Lon(k)) * t;
                oH = iMain.H(k) + (iMain.H(k + 1) - iMain.H(k)) * t;
            }
        }

        /// <summary>點到線段的距離（弧度）：在線段中點攤平成局部平面（東西 × cos 緯度）量 —— 水體的一段只有幾公里，誤差可忽略。</summary>
        static double SegDist(double aLat, double aLon, double bLat, double bLon, double pLat, double pLon, out double oT)
        {
            double c = Math.Cos((aLat + bLat) * 0.5 * Math.PI / 180);
            double bx = Wrap(bLon - aLon) * c, by = bLat - aLat;
            double px = Wrap(pLon - aLon) * c, py = pLat - aLat;
            double len2 = bx * bx + by * by;
            oT = len2 > 0 ? Math.Max(0, Math.Min(1, (px * bx + py * by) / len2)) : 0;
            double dx = px - bx * oT, dy = py - by * oT;
            return Math.Sqrt(dx * dx + dy * dy) * Math.PI / 180;
        }

        /// <summary>兩點的大圓距離（公尺）。</summary>
        public static double Dist(double aLat, double aLon, double bLat, double bLon)
        {
            SCP_GlobeGrid.LatLonToDir(aLat, aLon, out double ax, out double ay, out double az);
            SCP_GlobeGrid.LatLonToDir(bLat, bLon, out double bx, out double by, out double bz);
            return Math.Acos(Math.Max(-1, Math.Min(1, ax * bx + ay * by + az * bz))) * EarthR;
        }

        static double Wrap(double d)
        {
            while (d > 180) d -= 360;
            while (d < -180) d += 360;
            return d;
        }

        static string M(double d) => d.ToString("0.##", CultureInfo.InvariantCulture);
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 柵格化：水體 → 疊在格子顏色上的一層
    // ════════════════════════════════════════════════════════════════════════════
    public static class SCP_GlobeWaterRaster
    {
        public const int LakeColor = 0x3B7FC4;    // 沿用大家畫湖的色（erina 琵琶湖、summit 五大湖）
        public const int RiverColor = 0x5AA9E6;
        public const int FallColor = 0xFFFFFF;
        const double EarthR = 6371000;

        /// <summary>
        /// 疊圖與「哪一格是哪個水體」。順序：湖 → 河 → 瀑布（後蓋前；河不蓋湖 —— 河口伸進湖裡那幾格仍是湖）。
        /// 每個湖至少一格（小到沒有取樣點落在裡面的湖，給外環頂點平均那一格）。
        /// </summary>
        public static SCP_GlobeCells Build(SCP_GlobeGrid g, SCP_GlobeWaterSet iSet, SCP_GlobeWaterReport iReport, Dictionary<int, string> oOwner)
        {
            var c = new SCP_GlobeCells(g.N);
            double aCellM = g.CellAngle * EarthR;
            foreach (SCP_GlobeWaterFeature f in iSet.Features.Values)
            {
                if (!f.IsLake || f.Ring.Count < 6) continue;
                var aHoles = new List<List<double>>();
                foreach (SCP_GlobeWaterRing h in f.Holes) if (h.Pts.Count >= 6) aHoles.Add(h.Pts);
                List<int> aCells = SCP_GlobeDraw.Area(g, f.Ring, aHoles);
                if (aCells.Count == 0)
                {
                    double la = 0, lo = 0; int n = f.Ring.Count / 2;
                    for (int k = 0; k < n; k++) { la += f.Ring[2 * k]; lo += f.Ring[2 * k + 1]; }
                    aCells.Add(g.LatLonToCell(la / n, lo / n));
                }
                foreach (int idx in aCells) { c.Set(idx, LakeColor); oOwner[idx] = f.Id; }
            }
            foreach (SCP_GlobeWaterFeature f in iSet.Features.Values)
            {
                if (!f.IsRiver || f.Points.Count < 8 || f.Points.Count % 4 != 0) continue;
                var aSegs = new List<(SCP_GlobeLatLon A, SCP_GlobeLatLon B, double W)>();
                for (int k = 0; k + 1 < f.VertexCount; k++)
                    aSegs.Add((new SCP_GlobeLatLon(f.Lat(k), f.Lon(k)), new SCP_GlobeLatLon(f.Lat(k + 1), f.Lon(k + 1)), (f.W(k) + f.W(k + 1)) * 0.5));
                // 支流：最後一點接到主流河道上的那一點（端點可能差在容許距離內，畫上去才看得到接起來）
                if (SCP_GlobeWaterNet.TryRef(f.To, "river", out string aMain) && iSet.Features.TryGetValue(aMain, out SCP_GlobeWaterFeature? main) && main.IsRiver && main.VertexCount >= 2)
                {
                    SCP_GlobeWaterNet.Junction(main, f.Lat(f.VertexCount - 1), f.Lon(f.VertexCount - 1), out double jLat, out double jLon, out _, out _);
                    aSegs.Add((new SCP_GlobeLatLon(f.Lat(f.VertexCount - 1), f.Lon(f.VertexCount - 1)), new SCP_GlobeLatLon(jLat, jLon), f.W(f.VertexCount - 1)));
                }
                foreach (var s in aSegs)
                {
                    double aRadius = Math.Max(0, s.W * 0.5 / aCellM - 0.5);   // 比一格窄的河 ⇒ 一格寬
                    foreach (int idx in SCP_GlobeDraw.Line(g, new[] { s.A, s.B }, aRadius))
                    {
                        if (c.Get(idx) == LakeColor) continue;
                        c.Set(idx, RiverColor);
                        oOwner[idx] = f.Id;
                    }
                }
            }
            foreach (SCP_GlobeWaterFall w in iReport.Falls)
            {
                int idx = g.LatLonToCell(w.Lat, w.Lon);
                c.Set(idx, FallColor);
                oOwner[idx] = w.River;
            }
            c.ClearDirty();
            return c;
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    // 儲存：water/events（只追加）＋ water/meta.json
    // ════════════════════════════════════════════════════════════════════════════
    public sealed class SCP_GlobeWaterStore
    {
        readonly SCP_GlobeStore m_Globe;
        public SCP_GlobeWaterStore(SCP_GlobeStore iGlobe) { m_Globe = iGlobe; }

        public string Dir => Path.Combine(m_Globe.Paths.Root, "water");
        public string EventsDir => Path.Combine(Dir, "events");
        public string MetaFile => Path.Combine(Dir, "meta.json");
        public string EventFile(int iSeq) => Path.Combine(EventsDir, iSeq.ToString("D6", CultureInfo.InvariantCulture) + ".json");

        public static bool ValidId(string iId) => SCP_GlobeZones.ValidId(iId);

        /// <summary>端點容許距離：兩格（弧度）。</summary>
        public static double TolRad(int iN) => 2 * (Math.PI / 2 / iN);

        public SCP_GlobeWaterMeta LoadMeta()
        {
            var m = new SCP_GlobeWaterMeta();
            if (File.Exists(MetaFile)) SCP_JsonMapper.Populate(m, SCP_JsonData.Parse(File.ReadAllText(MetaFile)));
            if (!(m.FallDrop > 0) || !(m.FallGrade >= 0)) throw new SCP_GlobeException($"water/meta.json 的瀑布門檻壞了：FallDrop={m.FallDrop}、FallGrade={m.FallGrade}");
            return m;
        }

        public void SaveMeta(SCP_GlobeWaterMeta iMeta)
        {
            if (!(iMeta.FallDrop > 0) || !(iMeta.FallGrade >= 0)) throw new SCP_GlobeException($"瀑布門檻要 FallDrop > 0、FallGrade ≥ 0：{iMeta.FallDrop}、{iMeta.FallGrade}");
            Directory.CreateDirectory(Dir);
            WriteAtomic(MetaFile, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(iMeta), true));
        }

        public SCP_GlobeWaterSet Load()
        {
            var s = new SCP_GlobeWaterSet { Meta = LoadMeta() };
            List<int> aSeqs = ListSeqs();
            foreach (int q in aSeqs) Apply(s, ReadEvent(q));
            return s;
        }

        public SCP_GlobeWaterEvent ReadEvent(int iSeq)
        {
            var e = new SCP_GlobeWaterEvent();
            SCP_JsonMapper.Populate(e, SCP_JsonData.Parse(File.ReadAllText(EventFile(iSeq))));
            if (e.Seq != iSeq) throw new SCP_GlobeException($"水體事件 {iSeq:D6} 內文的 Seq 是 {e.Seq}");
            return e;
        }

        public List<SCP_GlobeWaterEvent> History(int iLast)
        {
            var a = new List<SCP_GlobeWaterEvent>();
            List<int> aSeqs = ListSeqs();
            for (int k = Math.Max(0, aSeqs.Count - iLast); k < aSeqs.Count; k++) a.Add(ReadEvent(aSeqs[k]));
            return a;
        }

        List<int> ListSeqs()
        {
            var a = new List<int>();
            if (!Directory.Exists(EventsDir)) return a;
            foreach (string f in Directory.GetFiles(EventsDir, "*.json"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(f), NumberStyles.None, CultureInfo.InvariantCulture, out int n)) a.Add(n);
            a.Sort();
            for (int k = 0; k < a.Count; k++)
                if (a[k] != k + 1) throw new SCP_GlobeException($"水體事件序號斷了：第 {k + 1} 筆的檔名是 {a[k]:D6}");
            return a;
        }

        void Apply(SCP_GlobeWaterSet s, SCP_GlobeWaterEvent e)
        {
            if (e.Op == "undo")
            {
                if (s.Stack.Count == 0 || s.Stack[s.Stack.Count - 1] != e.Target)
                    throw new SCP_GlobeException($"水體事件 {e.Seq} 要退 {e.Target}，但當時最後一筆有效的水體事件不是它");
                SCP_GlobeWaterEvent t = ReadEvent(e.Target);
                Put(s, t.Id, t.Prev);
                s.Stack.RemoveAt(s.Stack.Count - 1);
            }
            else if (e.Op == "set" || e.Op == "delete")
            {
                if (!ValidId(e.Id)) throw new SCP_GlobeException($"水體事件 {e.Seq} 的 id 不合法：{e.Id}");
                Put(s, e.Id, e.Feature);
                s.UsedIds.Add(e.Id);
                s.Stack.Add(e.Seq);
            }
            else throw new SCP_GlobeException($"水體事件 {e.Seq} 的 Op 不認得：{e.Op}");
            s.LastSeq = e.Seq;
        }

        static void Put(SCP_GlobeWaterSet s, string iId, SCP_GlobeWaterFeature iVersion)
        {
            if (iVersion.Id.Length == 0) s.Features.Remove(iId);
            else
            {
                if (iVersion.Id != iId) throw new SCP_GlobeException($"水體事件的 id {iId} 跟內容的 id {iVersion.Id} 對不上");
                s.Features[iId] = iVersion;
            }
        }

        /// <summary>
        /// 新增或修改一個水體（<paramref name="iFeature"/>.Id 必須已給）。整個水系重驗；這一筆新造成問題 ⇒ 丟例外、零寫入。
        /// 回傳事件與寫入後的讀數。
        /// </summary>
        public SCP_GlobeWaterEvent Set(string iPersona, SCP_GlobeWaterFeature iFeature, string iNote, out SCP_GlobeWaterReport oReport)
        {
            if (!ValidId(iFeature.Id)) throw new SCP_GlobeException("水體 id 只能用小寫英數、- 與 _（≤64 字）：" + iFeature.Id);
            using (m_Globe.AcquireLock())
            {
                SCP_GlobeWaterSet s = Load();
                s.Features.TryGetValue(iFeature.Id, out SCP_GlobeWaterFeature? aPrev);
                if (aPrev != null && aPrev.Kind != iFeature.Kind) throw new SCP_GlobeException($"{iFeature.Id} 是{KindText(aPrev.Kind)}，不能改成{KindText(iFeature.Kind)}");
                var e = new SCP_GlobeWaterEvent { Op = "set", Id = iFeature.Id, Feature = iFeature, Prev = aPrev ?? new SCP_GlobeWaterFeature() };
                return Commit(s, e, iPersona, iNote, out oReport);
            }
        }

        /// <summary>刪除；被別的河接著的湖或河刪不掉（重驗會擋）。</summary>
        public SCP_GlobeWaterEvent Delete(string iPersona, string iId, string iNote, out SCP_GlobeWaterReport oReport)
        {
            using (m_Globe.AcquireLock())
            {
                SCP_GlobeWaterSet s = Load();
                if (!s.Features.TryGetValue(iId, out SCP_GlobeWaterFeature? aPrev)) throw new SCP_GlobeException("沒有這個水體：" + iId);
                var e = new SCP_GlobeWaterEvent { Op = "delete", Id = iId, Feature = new SCP_GlobeWaterFeature(), Prev = aPrev };
                return Commit(s, e, iPersona, iNote, out oReport);
            }
        }

        /// <summary>退最後一筆仍有效的水體事件；沒有 ⇒ 回 null。⛔ 不碰格子的 Undo 堆疊。</summary>
        public SCP_GlobeWaterEvent? Undo(string iPersona, out SCP_GlobeWaterReport? oReport)
        {
            oReport = null;
            using (m_Globe.AcquireLock())
            {
                SCP_GlobeWaterSet s = Load();
                if (s.Stack.Count == 0) return null;
                int aTarget = s.Stack[s.Stack.Count - 1];
                SCP_GlobeWaterEvent t = ReadEvent(aTarget);
                var e = new SCP_GlobeWaterEvent { Op = "undo", Id = t.Id, Target = aTarget, Feature = t.Prev, Prev = t.Feature };
                SCP_GlobeWaterEvent r = Commit(s, e, iPersona, "", out SCP_GlobeWaterReport rep);
                oReport = rep;
                return r;
            }
        }

        SCP_GlobeWaterEvent Commit(SCP_GlobeWaterSet s, SCP_GlobeWaterEvent e, string iPersona, string iNote, out SCP_GlobeWaterReport oReport)
        {
            double aTol = TolRad(m_Globe.LoadMeta().N);
            SCP_GlobeWaterReport aBefore = SCP_GlobeWaterNet.Analyze(s.Features, s.Meta, aTol);
            var aAfterSet = new SortedDictionary<string, SCP_GlobeWaterFeature>(s.Features, StringComparer.Ordinal);
            if (e.Feature.Id.Length == 0) aAfterSet.Remove(e.Id); else aAfterSet[e.Id] = e.Feature;
            oReport = SCP_GlobeWaterNet.Analyze(aAfterSet, s.Meta, aTol);
            var aOld = new HashSet<string>(aBefore.Problems, StringComparer.Ordinal);
            var aNew = oReport.Problems.FindAll(p => !aOld.Contains(p));
            if (aNew.Count > 0)
                throw new SCP_GlobeException("水系不成立 ⇒ 一筆都沒寫：" + string.Join("；", aNew.GetRange(0, Math.Min(aNew.Count, 8)))
                                             + (aNew.Count > 8 ? $"…（共 {aNew.Count} 條）" : ""));
            e.Seq = s.LastSeq + 1;
            e.Persona = iPersona;
            e.Note = iNote;
            e.At = m_Globe.Clock().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
            if (e.Op == "set") { e.Feature.UpdatedBy = iPersona; e.Feature.UpdatedAt = e.At; }
            Directory.CreateDirectory(EventsDir);
            string aFile = EventFile(e.Seq);
            if (File.Exists(aFile)) throw new SCP_GlobeException("水體事件檔已存在（序號撞了）：" + aFile);
            WriteAtomic(aFile, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(e), false));
            return e;
        }

        public static string KindText(string iKind) => iKind == SCP_GlobeWaterFeature.KindLake ? "湖" : iKind == SCP_GlobeWaterFeature.KindRiver ? "河" : iKind;

        static void WriteAtomic(string iPath, string iText)
        {
            string aTmp = iPath + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(aTmp, iText.EndsWith("\n", StringComparison.Ordinal) ? iText : iText + "\n");
            SCP.Core.Io.SCP_TextFile.ReplaceOrMove(aTmp, iPath);
        }
    }
}
