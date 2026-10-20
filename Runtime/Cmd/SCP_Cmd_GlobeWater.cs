// 區塊職責：`cmd globe op=water` —— 水體層（湖、河、支流、瀑布）的新增／修改／刪除／Undo／列出／查看／水系檢查／瀑布門檻。
// 物理意義：寫入一律經 SCP_GlobeWaterStore（跟格子同一把鎖；整個水系重驗，這一筆新造成問題 ⇒ 整筆拒絕、零寫入）。
//          水體不寫格子事件：格子的 op=undo 退不到水體，水體的 sub=undo 也退不到格子。
// 數值影響：海拔、寬度一律公尺；壞參數 exit 2 且零寫入。
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Globe;

namespace SCP.Core.Cmd
{
    public sealed partial class SCP_Cmd_Globe
    {
        static SCP_CmdResult Water(SCP_GlobeStore iStore, SCP_CmdArgs iArgs)
        {
            iStore.LoadMeta();
            var ws = new SCP_GlobeWaterStore(iStore);
            string aSub = iArgs.Get("sub").Trim();
            if (aSub.Length == 0) aSub = "list";
            string aPersona = iArgs.Get("persona").Trim(), aId = iArgs.Get("id").Trim();
            switch (aSub)
            {
                case "list": return WaterList(iStore);
                case "check": return WaterCheck(iStore);
                case "history": return WaterHistory(ws, iArgs);
                case "show":
                    if (aId.Length == 0) return SCP_CmdResult.Fail(2, "✗ water show 要給 id");
                    return WaterShow(iStore, aId);
                case "lake": case "river": case "delete": case "undo": case "config":
                    break;
                default:
                    return SCP_CmdResult.Fail(2, "✗ water 的 sub 只能是 list｜show｜check｜history｜lake｜river｜delete｜undo｜config：" + aSub);
            }
            if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ water " + aSub + " 要給 persona（記進事件）");
            if (aSub == "undo")
            {
                SCP_GlobeWaterEvent? u = ws.Undo(aPersona, out SCP_GlobeWaterReport? ur);
                if (u == null) return SCP_CmdResult.Fail(1, "· 沒有可以退的水體事件（全部都退回了，或還沒有水體）").AddValue("seq", "0");
                string aWhat = u.Feature.Id.Length == 0 ? $"{u.Id} 拿掉了" : $"{u.Id} 回到上一版";
                return SCP_CmdResult.Success($"↶ 水體事件 #{u.Seq}：退回 #{u.Target}（{aWhat}）", "  " + NetLine(ur!))
                    .AddValue("seq", I(u.Seq)).AddValue("target", I(u.Target));
            }
            if (aSub == "config")
            {
                string aDrop = iArgs.Get("fall_drop").Trim(), aGrade = iArgs.Get("fall_grade").Trim();
                if (aDrop.Length + aGrade.Length == 0) return SCP_CmdResult.Fail(2, "✗ water config 要給 fall_drop（公尺）或 fall_grade（落差／水平距離）");
                if (!NumArg(iArgs, "fall_drop", -1, 0.01, 10000, out double d, out string w) || !NumArg(iArgs, "fall_grade", -1, 0, 1000, out double gr, out w))
                    return SCP_CmdResult.Fail(2, "✗ " + w);
                using (iStore.AcquireLock())
                {
                    SCP_GlobeWaterMeta m = ws.LoadMeta();
                    int aBefore = iStore.Load().WaterReport.Falls.Count;
                    double od = m.FallDrop, og = m.FallGrade;
                    if (aDrop.Length > 0) m.FallDrop = d;
                    if (aGrade.Length > 0) m.FallGrade = gr;
                    ws.SaveMeta(m);
                    int aAfter = iStore.Load().WaterReport.Falls.Count;
                    return SCP_CmdResult.Success($"✓ 瀑布門檻：落差 {F(od)} → {F(m.FallDrop)} m、坡度 {F(og)} → {F(m.FallGrade)}　瀑布 {aBefore} → {aAfter} 座（{aPersona}）")
                        .AddValue("fall_drop", F(m.FallDrop)).AddValue("fall_grade", F(m.FallGrade)).AddValue("falls", I(aAfter));
                }
            }
            if (aSub == "delete")
            {
                if (aId.Length == 0) return SCP_CmdResult.Fail(2, "✗ water delete 要給 id");
                SCP_GlobeWaterEvent de = ws.Delete(aPersona, aId, iArgs.Get("note").Trim(), out SCP_GlobeWaterReport dr);
                return SCP_CmdResult.Success($"✓ 水體事件 #{de.Seq}：刪掉 {aId}（{SCP_GlobeWaterStore.KindText(de.Prev.Kind)}「{de.Prev.Name}」）", "  " + NetLine(dr), "  ↶ 刪錯了：op=water --arg sub=undo")
                    .AddValue("seq", I(de.Seq)).AddOutput(ws.EventFile(de.Seq));
            }

            // lake／river：新增或修改
            SCP_GlobeWaterSet aSet = ws.Load();
            SCP_GlobeWaterFeature f;
            bool aNew;
            if (aId.Length > 0 && aSet.Features.TryGetValue(aId, out SCP_GlobeWaterFeature? aOld))
            {
                if (aOld.Kind != aSub) return SCP_CmdResult.Fail(2, $"✗ {aId} 是{SCP_GlobeWaterStore.KindText(aOld.Kind)}，不能用 sub={aSub} 改");
                f = aOld.Clone(); aNew = false;
            }
            else
            {
                if (aId.Length == 0) aId = aSet.NextId(aSub);
                else if (!SCP_GlobeWaterStore.ValidId(aId)) return SCP_CmdResult.Fail(2, "✗ 水體 id 只能用小寫英數、- 與 _（≤64 字）：" + aId);
                f = new SCP_GlobeWaterFeature { Id = aId, Kind = aSub }; aNew = true;
            }
            string aName = iArgs.Get("name").Trim(), aSource = iArgs.Get("source").Trim(), aZone = iArgs.Get("zone").Trim();
            if (aName.Length > 0) f.Name = aName;
            if (aSource.Length > 0) f.Source = aSource;
            if (aZone.Length > 0)
            {
                if (new SCP_GlobeZones(iStore.Paths).Find(aZone) == null) return SCP_CmdResult.Fail(2, "✗ 沒有這個施工區：" + aZone + "（先 op=zone --arg sub=list）");
                f.Zone = aZone;
            }
            string aPoints = iArgs.Get("points").Trim();
            if (aSub == "lake")
            {
                string aSurface = iArgs.Get("surface").Trim(), aHoles = iArgs.Get("holes").Trim();
                if (aSurface.Length > 0)
                {
                    if (!NumArg(iArgs, "surface", 0, -1000, 9000, out double sf, out string w)) return SCP_CmdResult.Fail(2, "✗ " + w);
                    f.Surface = sf;
                }
                else if (aNew) return SCP_CmdResult.Fail(2, "✗ 新的湖要給 surface（水面海拔，公尺；可以是負的）");
                if (aPoints.Length > 0)
                {
                    if (!ParseRing(aPoints, out List<double> ring, out string w)) return SCP_CmdResult.Fail(2, "✗ points：" + w);
                    f.Ring = ring;
                }
                else if (aNew) return SCP_CmdResult.Fail(2, "✗ 新的湖要給 points（外環 lat,lon;lat,lon;…）");
                if (aHoles.Length > 0)
                {
                    f.Holes = new List<SCP_GlobeWaterRing>();
                    if (aHoles != "none")
                        foreach (string h in aHoles.Split('|'))
                        {
                            if (!ParseRing(h, out List<double> hr, out string w)) return SCP_CmdResult.Fail(2, "✗ holes：" + w);
                            f.Holes.Add(new SCP_GlobeWaterRing { Pts = hr });
                        }
                }
            }
            else
            {
                string aFrom = iArgs.Get("from").Trim(), aTo = iArgs.Get("to").Trim();
                if (aPoints.Length > 0)
                {
                    if (!ParseRiver(aPoints, out List<double> pts, out string w)) return SCP_CmdResult.Fail(2, "✗ points：" + w);
                    f.Points = pts;
                }
                else if (aNew) return SCP_CmdResult.Fail(2, "✗ 新的河要給 points（上游→下游 lat,lon,水面海拔m,寬m;…）");
                if (aFrom.Length > 0) f.From = aFrom;
                if (aTo.Length > 0) f.To = aTo;
            }
            SCP_GlobeWaterEvent e = ws.Set(aPersona, f, iArgs.Get("note").Trim(), out SCP_GlobeWaterReport rep);
            SCP_GlobeState s = iStore.Load();
            int aCells = 0;
            foreach (string o in s.WaterOwner.Values) if (o == f.Id) aCells++;
            var r = new SCP_CmdResult();
            r.Lines.Add($"✓ 水體事件 #{e.Seq}：{(aNew ? "新增" : "修改")} {FeatureLine(f)}　佔 {aCells:N0} 格");
            foreach (SCP_GlobeWaterFall w in rep.Falls) if (w.River == f.Id) r.Lines.Add("  🌊 " + FallLine(w));
            r.Lines.Add("  " + NetLine(rep));
            r.Lines.Add("  ↶ 畫錯了：op=water --arg sub=undo（⛔ 不是格子的 op=undo）");
            return r.AddValue("seq", I(e.Seq)).AddValue("id", f.Id).AddValue("cells", I(aCells))
                    .AddValue("falls", I(rep.Falls.Count)).AddOutput(ws.EventFile(e.Seq));
        }

        static SCP_CmdResult WaterList(SCP_GlobeStore iStore)
        {
            SCP_GlobeState s = iStore.Load();
            var r = new SCP_CmdResult();
            var aCells = CellsPerFeature(s);
            foreach (SCP_GlobeWaterFeature f in s.Water.Features.Values)
                r.Lines.Add(FeatureLine(f) + $"　{(aCells.TryGetValue(f.Id, out int n) ? n : 0):N0} 格");
            if (s.Water.Count == 0) r.Lines.Add("（還沒有水體 —— 湖：sub=lake --arg surface=<m> --arg points=…；河：sub=river --arg points=lat,lon,海拔,寬;…）");
            else r.Lines.Add(NetLine(s.WaterReport));
            return r.AddValue("lakes", I(s.WaterReport.Lakes)).AddValue("rivers", I(s.WaterReport.Rivers));
        }

        static SCP_CmdResult WaterShow(SCP_GlobeStore iStore, string iId)
        {
            SCP_GlobeState s = iStore.Load();
            if (!s.Water.Features.TryGetValue(iId, out SCP_GlobeWaterFeature? f)) return SCP_CmdResult.Fail(1, "✗ 沒有這個水體：" + iId);
            var r = new SCP_CmdResult();
            int aCells = CellsPerFeature(s).TryGetValue(f.Id, out int n) ? n : 0;
            r.Lines.Add(FeatureLine(f) + $"　{aCells:N0} 格");
            r.Lines.Add($"  出處：{(f.Source.Length > 0 ? f.Source : "（沒寫）")}　施工區：{(f.Zone.Length > 0 ? f.Zone : "（沒有）")}　最後改：{f.UpdatedBy} {f.UpdatedAt}");
            if (f.IsLake)
            {
                r.Lines.Add($"  外環 {f.VertexCount} 點、湖中島 {f.Holes.Count} 個");
                var aOut = new List<string>(); var aIn = new List<string>();
                foreach (SCP_GlobeWaterFeature o in s.Water.Features.Values)
                {
                    if (o.From == "lake:" + f.Id) aOut.Add(o.Id);
                    if (o.To == "lake:" + f.Id) aIn.Add(o.Id);
                }
                r.Lines.Add("  流入：" + (aIn.Count > 0 ? string.Join("、", aIn) : "（沒有）") + "　流出：" + (aOut.Count > 0 ? string.Join("、", aOut) : "（沒有）"));
            }
            else
            {
                r.Lines.Add($"  {f.VertexCount} 點：水面 {F(f.H(0))} → {F(f.H(f.VertexCount - 1))} m、長 {Length(f) / 1000:0.#} km");
                var aTribs = new List<string>();
                foreach (SCP_GlobeWaterFeature o in s.Water.Features.Values) if (o.To == "river:" + f.Id) aTribs.Add(o.Id);
                r.Lines.Add("  支流：" + (aTribs.Count > 0 ? string.Join("、", aTribs) : "（沒有）"));
            }
            foreach (SCP_GlobeWaterFall w in s.WaterReport.Falls) if (w.River == f.Id) r.Lines.Add("  🌊 " + FallLine(w));
            foreach (string p in s.WaterReport.Problems) if (p.StartsWith(f.Id + "：", StringComparison.Ordinal)) r.Lines.Add("  ⚠ " + p);
            return r.AddValue("kind", f.Kind).AddValue("cells", I(aCells));
        }

        /// <summary>整個水系一次：數量、支流、瀑布逐座、問題、河口那格是不是海。</summary>
        static SCP_CmdResult WaterCheck(SCP_GlobeStore iStore)
        {
            SCP_GlobeState s = iStore.Load();
            SCP_GlobeWaterReport rep = s.WaterReport;
            var r = new SCP_CmdResult();
            r.Lines.Add("# 💧 水系　" + NetLine(rep));
            r.Lines.Add($"  源頭：泉 {rep.Springs}、湖的出口 {rep.LakeOutlets}　河口：入海 {rep.ToSea}、入湖 {rep.ToLake}、支流 {rep.Tributaries}");
            r.Lines.Add($"  瀑布門檻：落差 ≥ {F(s.Water.Meta.FallDrop)} m，河身那一段還要坡度 ≥ {F(s.Water.Meta.FallGrade)}（water/meta.json；改：sub=config）");
            foreach (SCP_GlobeWaterFall w in rep.Falls) r.Lines.Add("  🌊 " + FallLine(w));
            int aWarn = 0;
            foreach (SCP_GlobeWaterFeature f in s.Water.Features.Values)
            {
                if (!f.IsRiver || f.To != SCP_GlobeWaterFeature.ToSea || f.VertexCount < 2) continue;
                int idx = s.Grid.LatLonToCell(f.Lat(f.VertexCount - 1), f.Lon(f.VertexCount - 1));
                if (s.Cells.Get(idx) == SCP_GlobeCells.Empty) continue;
                bool aSeaNext = false;
                for (int dj = -1; dj <= 1 && !aSeaNext; dj++)
                    for (int di = -1; di <= 1; di++)
                        if (s.Cells.Get(s.Grid.Neighbor(idx, di, dj)) == SCP_GlobeCells.Empty) { aSeaNext = true; break; }
                if (aSeaNext) continue;
                aWarn++;
                r.Lines.Add($"  ⚠ {f.Id}：河口那一格與四周都是陸地顏色（不在海邊）—— 最後一點要伸到海岸");
            }
            foreach (string p in rep.Problems) r.Lines.Add("  ✗ " + p);
            if (rep.Problems.Count == 0 && aWarn == 0) r.Lines.Add("  ✓ 沒有問題");
            return r.AddValue("lakes", I(rep.Lakes)).AddValue("rivers", I(rep.Rivers)).AddValue("tributaries", I(rep.Tributaries))
                    .AddValue("falls", I(rep.Falls.Count)).AddValue("problems", I(rep.Problems.Count)).AddValue("warnings", I(aWarn));
        }

        static SCP_CmdResult WaterHistory(SCP_GlobeWaterStore ws, SCP_CmdArgs iArgs)
        {
            if (!NumArg(iArgs, "last", 10, 1, 1000, out double n, out string w)) return SCP_CmdResult.Fail(2, "✗ " + w);
            var r = new SCP_CmdResult();
            foreach (SCP_GlobeWaterEvent e in ws.History((int)n))
                r.Lines.Add(e.Op == "undo" ? $"#{e.Seq}  {e.At}  {e.Persona}  undo → 退 #{e.Target}（{e.Id}）"
                                           : $"#{e.Seq}  {e.At}  {e.Persona}  {e.Op} {e.Id}" + (e.Note.Length > 0 ? "　" + e.Note : ""));
            if (r.Lines.Count == 0) r.Lines.Add("（還沒有水體事件）");
            return r;
        }

        static Dictionary<string, int> CellsPerFeature(SCP_GlobeState s)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (string o in s.WaterOwner.Values) d[o] = (d.TryGetValue(o, out int n) ? n : 0) + 1;
            return d;
        }

        static string FeatureLine(SCP_GlobeWaterFeature f)
        {
            string aName = f.Name.Length > 0 ? "「" + f.Name + "」" : "";
            if (f.IsLake) return $"💧 {f.Id}{aName} 湖　水面 {F(f.Surface)} m";
            string aH = f.VertexCount > 0 && f.Points.Count % 4 == 0 ? $"　水面 {F(f.H(0))} → {F(f.H(f.VertexCount - 1))} m" : "";
            return $"〰 {f.Id}{aName} 河　{f.From} → {f.To}{aH}";
        }

        static string FallLine(SCP_GlobeWaterFall w)
        {
            string aKind = w.Kind == "segment" ? $"河身第 {w.Vertex + 1}→{w.Vertex + 2} 點（坡度 {w.Grade:0.###}）"
                         : w.Kind == "junction" ? "支流匯入 " + w.Into
                         : w.Kind == "lake" ? "入湖 " + w.Into : "入海";
            return $"瀑布 {w.River}：{aKind}　落差 {F(w.Drop)} m　@ {F(w.Lat)},{F(w.Lon)}";
        }

        static string NetLine(SCP_GlobeWaterReport r)
            => $"湖 {r.Lakes}、河 {r.Rivers}（支流 {r.Tributaries}）、瀑布 {r.Falls.Count}" + (r.Problems.Count > 0 ? $"、⚠ 問題 {r.Problems.Count}（sub=check）" : "");

        static double Length(SCP_GlobeWaterFeature f)
        {
            double d = 0;
            for (int k = 0; k + 1 < f.VertexCount; k++) d += SCP_GlobeWaterNet.Dist(f.Lat(k), f.Lon(k), f.Lat(k + 1), f.Lon(k + 1));
            return d;
        }

        /// <summary>「lat,lon;lat,lon;…」→ 扁平清單（至少 3 點）。</summary>
        static bool ParseRing(string iText, out List<double> oRing, out string oWhy)
        {
            oRing = new List<double>();
            if (!SCP_GlobeDraw.TryParsePoints(iText, out List<SCP_GlobeLatLon> pts, out oWhy)) return false;
            if (pts.Count < 3) { oWhy = "一圈至少 3 點：" + pts.Count; return false; }
            foreach (SCP_GlobeLatLon p in pts) { oRing.Add(p.Lat); oRing.Add(p.Lon); }
            return true;
        }

        /// <summary>「lat,lon,水面海拔m,寬m;…」→ 扁平清單（至少 2 點）。也接受一行一點。</summary>
        static bool ParseRiver(string iText, out List<double> oPts, out string oWhy)
        {
            oPts = new List<double>();
            oWhy = "";
            foreach (string aRaw in (iText ?? "").Split(new[] { ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string aItem = aRaw.Trim();
                if (aItem.Length == 0 || aItem.StartsWith("#", StringComparison.Ordinal)) continue;
                string[] a = aItem.Split(',');
                if (a.Length != 4) { oWhy = "河的每一點要寫成 lat,lon,水面海拔m,寬m：「" + aItem + "」"; return false; }
                if (!SCP_GlobeDraw.TryParseLatLon(a[0] + "," + a[1], out SCP_GlobeLatLon p, out oWhy)) return false;
                if (!double.TryParse(a[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double h) || double.IsNaN(h) || double.IsInfinity(h) || h < -1000 || h > 9000)
                { oWhy = "水面海拔要在 -1000..9000 m：「" + aItem + "」"; return false; }
                if (!double.TryParse(a[3].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double wd) || !(wd > 0) || wd > 100000)
                { oWhy = "寬度要在 (0, 100000] m：「" + aItem + "」"; return false; }
                oPts.Add(p.Lat); oPts.Add(p.Lon); oPts.Add(h); oPts.Add(wd);
            }
            if (oPts.Count < 8) { oWhy = "河至少 2 點"; return false; }
            return true;
        }

        static string I(int n) => n.ToString(CultureInfo.InvariantCulture);
    }
}
