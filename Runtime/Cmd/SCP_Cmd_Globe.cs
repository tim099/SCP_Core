// 區塊職責：`cmd globe` —— 可繪製球面（prototype）：建立、底色、用經緯度畫點／線／多邊形、油漆桶、Undo、預覽渲染、歷史。
// 物理意義：寫入一律經 SCP_GlobeStore（鎖＋事件先落盤）；本層只做參數驗證與把經緯度柵格化成格子。
// 數值影響：繪製不收費（prototype）。壞參數 exit 2 且零寫入；沒有實際改到任何格子 ⇒ 不寫事件。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Globe;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Globe : SCP_Cmd
    {
        public override string Name => "globe";
        public override string Category => SCP_CmdCategory.Game;

        public override string Summary => "可繪製球面（prototype）：經緯度畫點／線／多邊形填色／油漆桶、Undo、預覽渲染";

        public override string Details =>
            "等角立方體球：6 面 × N×N 格（預設 N=2048，約 4.9 km／格），一格 24-bit RGB（全彩）；沒畫過＝顯示 meta 的底色。\n"
            + "op：status（預設）｜init｜base｜cell｜point｜line｜polygon｜fill｜erase｜undo｜zone｜render｜history\n"
            + "· 橡皮擦：op=erase --arg shape=point|line|polygon|fill（參數同那一種畫法）⇒ 擦回底色（大海）。\n"
            + "· 施工區：op=zone --arg sub=add|list|show|join|update —— 標記「誰在這塊畫什麼」，可重疊、⛔ 不擋任何人下筆。\n"
            + "· 下筆前先 op=cell 查那一格現在是什麼 —— 別人畫的會被你蓋掉，而覆蓋不會報錯。\n"
            + "· 經緯度一律 lat,lon（度；北緯／東經為正）。點列：points=\"lat,lon;lat,lon;…\"（長的走 --arg-file）。\n"
            + "· color：#RRGGBB 或 r,g,b（全彩）；color=empty ＝ 擦回底色。\n"
            + "· undo 一次退最後一筆仍有效的繪製；事件只追加不刪。\n"
            + "⚠ polygon 是 prototype：不能含極點、經度跨度要 < 180°。fill（油漆桶）超過 max_cells 整筆拒絕。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("globe --arg op=line --arg persona=<你> --arg color=#FFFFFF --arg points=\"25.3,121.5;22.0,120.8\"");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "信件夾根（Senate CLI 從設定自動補）；render 給了 persona 時圖寫進 <persona>/cmd/"),
            new SCP_CmdArgSpec("op", "status｜init｜base｜cell｜point｜line｜polygon｜fill｜erase｜undo｜zone｜render｜history",
                               iChoices: new[] { "status", "init", "base", "cell", "point", "line", "polygon", "fill", "erase", "undo", "zone", "render", "history" }),
            new SCP_CmdArgSpec("shape", "erase：point｜line｜polygon｜fill（預設 point）", iChoices: new[] { "point", "line", "polygon", "fill" }),
            new SCP_CmdArgSpec("sub", "zone：add｜list｜show｜join｜update", iChoices: new[] { "add", "list", "show", "join", "update" }),
            new SCP_CmdArgSpec("id", "zone：施工區 id（小寫英數、-、_）"),
            new SCP_CmdArgSpec("title", "zone add／update：名稱（例：創造日本）"),
            new SCP_CmdArgSpec("bbox", "zone add／update：範圍 南,西,北,東（度；西 > 東 ＝ 跨 180°）"),
            new SCP_CmdArgSpec("status", "zone update：active｜paused｜done", iChoices: new[] { "active", "paused", "done" }),
            new SCP_CmdArgSpec("zones", "render：1＝疊施工區框線（黃＝施工中、橘＝暫停、灰＝完成）"),
            new SCP_CmdArgSpec("persona", "誰畫的（point／line／polygon／fill／undo 必填，記進事件）"),
            new SCP_CmdArgSpec("n", "init：每面邊長格數（預設 2048）"),
            new SCP_CmdArgSpec("color", "point／line／polygon／fill：#RRGGBB｜r,g,b｜empty；base／init：底色"),
            new SCP_CmdArgSpec("lat", "cell／point／fill：緯度（度）"),
            new SCP_CmdArgSpec("lon", "cell／point／fill：經度（度）"),
            new SCP_CmdArgSpec("radius", "point：半徑（格數，預設 0＝一格）"),
            new SCP_CmdArgSpec("width", "line：筆寬半徑（格數，預設 0＝一格寬）"),
            new SCP_CmdArgSpec("points", "line／polygon：lat,lon;lat,lon;…"),
            new SCP_CmdArgSpec("max_cells", "fill：最多塗幾格（預設 200000；超過整筆拒絕）"),
            new SCP_CmdArgSpec("note", "寫進事件的一句話"),
            new SCP_CmdArgSpec("center", "render：畫面中心 lat,lon（預設 23.7,121）"),
            new SCP_CmdArgSpec("zoom", "render：放大倍率（1＝整個半球）"),
            new SCP_CmdArgSpec("size", "render：邊長 px（預設 720）"),
            new SCP_CmdArgSpec("graticule", "render：經緯線間隔（度，0＝不畫；預設 10）"),
            new SCP_CmdArgSpec("seams", "render：1＝疊面接縫"),
            new SCP_CmdArgSpec("out", "render：輸出 PNG 絕對路徑（預設：有 persona ⇒ <letters>/<persona>/cmd/globe_view.png；沒有 ⇒ <球面根>/_cache/view.png）"),
            new SCP_CmdArgSpec("last", "history：列最後幾筆（預設 10）"),
        };

        public const int DefaultMaxFill = 200000;

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root").Trim();
            if (aDataRoot.Length == 0 || !Directory.Exists(aDataRoot)) return SCP_CmdResult.Fail(2, "✗ data_root 不存在：" + aDataRoot);
            var aStore = new SCP_GlobeStore(new SCP_GlobePaths(new SCP_DataRoot(aDataRoot)));
            string aOp = iArgs.Get("op").Trim();
            if (aOp.Length == 0) aOp = "status";
            try
            {
                switch (aOp)
                {
                    case "status": return Status(aStore);
                    case "init": return Init(aStore, iArgs);
                    case "base": return Base(aStore, iArgs);
                    case "cell": return Cell(aStore, iArgs);
                    case "point": case "line": case "polygon": case "fill": return Paint(aStore, aOp, iArgs, false);
                    case "erase":
                    {
                        string aShape = iArgs.Get("shape").Trim();
                        if (aShape.Length == 0) aShape = "point";
                        if (aShape != "point" && aShape != "line" && aShape != "polygon" && aShape != "fill")
                            return SCP_CmdResult.Fail(2, "✗ shape 只能是 point｜line｜polygon｜fill：" + aShape);
                        return Paint(aStore, aShape, iArgs, true);
                    }
                    case "zone": return Zone(aStore, iArgs);
                    case "undo": return Undo(aStore, iArgs);
                    case "render": return Render(aStore, iArgs);
                    case "history": return History(aStore, iArgs);
                    default: return SCP_CmdResult.Fail(2, "✗ 不認得的 op：" + aOp);
                }
            }
            catch (SCP_GlobeException e) { return SCP_CmdResult.Fail(2, "✗ " + e.Message); }
        }

        static SCP_CmdResult Status(SCP_GlobeStore iStore)
        {
            if (!iStore.Exists)
                return SCP_CmdResult.Fail(1, "✗ 球面還沒建立：" + iStore.Paths.Meta, "  先跑 op=init（預設 N=2048、底色海水藍）")
                    .AddValue("initialized", "0");
            SCP_GlobeState s = iStore.Load();
            int aPainted = s.Cells.PaintedCount();
            var r = new SCP_CmdResult();
            r.Lines.Add($"# 🌍 球面　N={s.Meta.N}（6×{s.Meta.N}² = {s.Grid.CellCount:N0} 格，每格約 {40075.0 / 4 / s.Meta.N:0.0} km）");
            r.Lines.Add($"  底色：{s.Meta.BaseColor}　格子：rgb24 全彩（{s.Cells.Tile}² 分塊、只存畫過的）");
            r.Lines.Add($"  事件：{s.LastSeq} 筆（仍有效的繪製 {s.Stack.Count} 筆）　已畫格子：{aPainted:N0}");
            r.Lines.Add($"  根：{iStore.Paths.Root}" + (s.FromCache ? "（讀快取＋補重播）" : "（從頭重播）"));
            return r.AddValue("initialized", "1").AddValue("n", s.Meta.N.ToString(CultureInfo.InvariantCulture))
                    .AddValue("base_color", s.Meta.BaseColor)
                    .AddValue("events", s.LastSeq.ToString(CultureInfo.InvariantCulture))
                    .AddValue("undoable", s.Stack.Count.ToString(CultureInfo.InvariantCulture))
                    .AddValue("painted", aPainted.ToString(CultureInfo.InvariantCulture));
        }

        static SCP_CmdResult Init(SCP_GlobeStore iStore, SCP_CmdArgs iArgs)
        {
            if (iStore.Exists) return SCP_CmdResult.Fail(1, "✗ 球面已經建立過了：" + iStore.Paths.Meta, "  （改底色走 op=base；⛔ init 不覆寫）");
            int n = 2048;
            string aN = iArgs.Get("n").Trim();
            if (aN.Length > 0 && (!int.TryParse(aN, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < 1 || n > 8192 || (n & (n - 1)) != 0))
                return SCP_CmdResult.Fail(2, "✗ n 要是 1..8192 的 2 的冪次：" + aN);
            string aBase = SCP_GlobeMeta.DefaultBase;
            string aColor = iArgs.Get("color").Trim();
            if (aColor.Length > 0)
            {
                if (!SCP_GlobeStore.TryParseColor(aColor, out int cr, out int cg, out int cb, out string aWhy)) return SCP_CmdResult.Fail(2, "✗ " + aWhy);
                aBase = SCP_GlobeCells.ToHex((cr << 16) | (cg << 8) | cb);
            }
            iStore.WriteMeta(new SCP_GlobeMeta { N = n, BaseColor = aBase });
            return Status(iStore);
        }

        static SCP_CmdResult Base(SCP_GlobeStore iStore, SCP_CmdArgs iArgs)
        {
            if (!SCP_GlobeStore.TryParseColor(iArgs.Get("color"), out int cr, out int cg, out int cb, out string aWhy)) return SCP_CmdResult.Fail(2, "✗ " + aWhy);
            string aBase = SCP_GlobeCells.ToHex((cr << 16) | (cg << 8) | cb);
            SCP_GlobeMeta m = iStore.LoadMeta();
            string aOld = m.BaseColor;
            m.BaseColor = aBase;
            iStore.WriteMeta(m);
            return SCP_CmdResult.Success($"✓ 底色 {aOld} → {aBase}；格子一格都沒動").AddValue("base_color", aBase);
        }

        static SCP_CmdResult Zone(SCP_GlobeStore iStore, SCP_CmdArgs iArgs)
        {
            iStore.LoadMeta();
            var zs = new SCP_GlobeZones(iStore.Paths);
            string aSub = iArgs.Get("sub").Trim();
            if (aSub.Length == 0) aSub = "list";
            string aPersona = iArgs.Get("persona").Trim(), aId = iArgs.Get("id").Trim();
            string Now() => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

            if (aSub == "list")
            {
                var r = new SCP_CmdResult();
                List<SCP_GlobeZone> all = zs.List();
                foreach (SCP_GlobeZone z in all) r.Lines.Add(ZoneLine(z));
                if (all.Count == 0) r.Lines.Add("（還沒有施工區 —— 開一個：op=zone --arg sub=add --arg id=<id> --arg title=<名稱> --arg bbox=南,西,北,東）");
                return r.AddValue("zones", all.Count.ToString(CultureInfo.InvariantCulture));
            }
            if (aId.Length == 0) return SCP_CmdResult.Fail(2, "✗ zone " + aSub + " 要給 id");
            if (aSub == "show")
            {
                SCP_GlobeZone? z = zs.Find(aId);
                if (z == null) return SCP_CmdResult.Fail(1, "✗ 沒有這個施工區：" + aId);
                return SCP_CmdResult.Success(ZoneLine(z), "  成員：" + (z.Members.Count > 0 ? string.Join("、", z.Members) : "（只有負責人）"),
                    "  建立 " + z.CreatedAt + "　更新 " + z.UpdatedAt + (z.Note.Length > 0 ? "\n  備註：" + z.Note : ""));
            }
            if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ zone " + aSub + " 要給 persona");

            if (aSub == "add")
            {
                string aTitle = iArgs.Get("title").Trim();
                if (aTitle.Length == 0) return SCP_CmdResult.Fail(2, "✗ zone add 要給 title（例：創造日本）");
                if (!SCP_GlobeZones.TryParseBbox(iArgs.Get("bbox"), out double s, out double w, out double n, out double e, out string why))
                    return SCP_CmdResult.Fail(2, "✗ " + why);
                var z = new SCP_GlobeZone
                {
                    Id = aId, Title = aTitle, Owner = aPersona, South = s, West = w, North = n, East = e,
                    Note = iArgs.Get("note").Trim(), CreatedAt = Now(), UpdatedAt = Now(),
                };
                using (iStore.AcquireLock()) zs.Create(z);
                return SCP_CmdResult.Success("✓ 開了施工區 " + ZoneLine(z), "  ⚠ 施工區只是公告，⛔ 不擋別人下筆；可以跟別的區重疊")
                    .AddValue("id", aId).AddOutput(zs.FileOf(aId));
            }

            using (iStore.AcquireLock())
            {
                SCP_GlobeZone? z = zs.Find(aId);
                if (z == null) return SCP_CmdResult.Fail(1, "✗ 沒有這個施工區：" + aId);
                if (aSub == "join")
                {
                    if (z.CanEdit(aPersona)) return SCP_CmdResult.Success("· 你已經在 " + aId + " 裡了");
                    z.Members.Add(aPersona);
                    z.UpdatedAt = Now();
                    zs.Save(z);
                    return SCP_CmdResult.Success("✓ 加入施工區 " + ZoneLine(z));
                }
                // update
                if (!z.CanEdit(aPersona)) return SCP_CmdResult.Fail(2, $"✗ 只有負責人或成員能改 {aId}（先 sub=join）");
                string aTitle = iArgs.Get("title").Trim(), aBbox = iArgs.Get("bbox").Trim(), aStatus = iArgs.Get("status").Trim(), aNote = iArgs.Get("note").Trim();
                if (aTitle.Length + aBbox.Length + aStatus.Length + aNote.Length == 0)
                    return SCP_CmdResult.Fail(2, "✗ zone update 沒給要改什麼（title／bbox／status／note）");
                if (aStatus.Length > 0 && Array.IndexOf(SCP_GlobeZones.Statuses, aStatus) < 0)
                    return SCP_CmdResult.Fail(2, "✗ status 只能是 active｜paused｜done：" + aStatus);
                if (aBbox.Length > 0)
                {
                    if (!SCP_GlobeZones.TryParseBbox(aBbox, out double s, out double w, out double n, out double e, out string why))
                        return SCP_CmdResult.Fail(2, "✗ " + why);
                    z.South = s; z.West = w; z.North = n; z.East = e;
                }
                if (aTitle.Length > 0) z.Title = aTitle;
                if (aStatus.Length > 0) z.Status = aStatus;
                if (aNote.Length > 0) z.Note = aNote;
                z.UpdatedAt = Now();
                zs.Save(z);
                return SCP_CmdResult.Success("✓ 更新施工區 " + ZoneLine(z));
            }
        }

        static string ZoneLine(SCP_GlobeZone z)
        {
            string aMark = z.Status == "done" ? "✅" : z.Status == "paused" ? "⏸" : "🚧";
            return $"{aMark} {z.Id}「{z.Title}」{z.Status}　負責 {z.Owner}" + (z.Members.Count > 0 ? "＋" + z.Members.Count + " 人" : "")
                   + $"　範圍 {z.BboxText}";
        }

        static SCP_CmdResult Cell(SCP_GlobeStore iStore, SCP_CmdArgs iArgs)
        {
            if (!LatLonArg(iArgs, out SCP_GlobeLatLon p, out string w)) return SCP_CmdResult.Fail(2, "✗ " + w);
            SCP_GlobeState s = iStore.Load();
            int idx = s.Grid.LatLonToCell(p.Lat, p.Lon);
            s.Grid.Unpack(idx, out int f, out int i, out int j);
            s.Grid.CellToLatLon(idx, out double cla, out double clo);
            int v = s.Cells.Get(idx);
            string aColor = v == SCP_GlobeCells.Empty ? "empty（顯示底色 " + s.Meta.BaseColor + "）" : SCP_GlobeCells.ToHex(v);
            var aIn = new List<string>();
            foreach (SCP_GlobeZone z in new SCP_GlobeZones(iStore.Paths).List())
                if (z.Contains(p.Lat, p.Lon)) aIn.Add($"{z.Id}「{z.Title}」({z.Status}，{z.Owner})");
            return SCP_CmdResult.Success(
                    $"· ({F(p.Lat)},{F(p.Lon)}) → 面 {s.Meta.Faces[f].Name} i={i} j={j}（index {idx}，格心 {F(cla)},{F(clo)}）",
                    "  顏色：" + aColor,
                    "  施工區：" + (aIn.Count > 0 ? string.Join("；", aIn) : "（沒有）"))
                .AddValue("zones", string.Join(",", aIn.ConvertAll(x => x.Substring(0, x.IndexOf('「')))))
                .AddValue("index", idx.ToString(CultureInfo.InvariantCulture)).AddValue("face", s.Meta.Faces[f].Name)
                .AddValue("i", i.ToString(CultureInfo.InvariantCulture)).AddValue("j", j.ToString(CultureInfo.InvariantCulture))
                .AddValue("color", v == SCP_GlobeCells.Empty ? "empty" : SCP_GlobeCells.ToHex(v));
        }

        static SCP_CmdResult Paint(SCP_GlobeStore iStore, string iOp, SCP_CmdArgs iArgs, bool iErase)
        {
            string aPersona = iArgs.Get("persona").Trim();
            if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ " + iOp + " 要給 persona（記進事件）");
            string aColor = iErase ? "empty" : iArgs.Get("color").Trim();
            int aValue;
            if (aColor.Equals("empty", StringComparison.OrdinalIgnoreCase)) aValue = SCP_GlobeCells.Empty;
            else if (SCP_GlobeStore.TryParseColor(aColor, out int cr, out int cg, out int cb, out string aWhy)) aValue = SCP_GlobeCells.FromRgb(cr, cg, cb);
            else return SCP_CmdResult.Fail(2, "✗ " + aWhy);

            SCP_GlobeMeta aMeta = iStore.LoadMeta();
            var g = new SCP_GlobeGrid(aMeta.N, aMeta.Faces);
            List<int> aCells;
            string aDesc;
            switch (iOp)
            {
                case "point":
                {
                    if (!LatLonArg(iArgs, out SCP_GlobeLatLon p, out string w)) return SCP_CmdResult.Fail(2, "✗ " + w);
                    if (!NumArg(iArgs, "radius", 0, 0, 2000, out double rad, out w)) return SCP_CmdResult.Fail(2, "✗ " + w);
                    aCells = SCP_GlobeDraw.Point(g, p, rad);
                    aDesc = $"點 ({F(p.Lat)},{F(p.Lon)}) 半徑 {F(rad)} 格";
                    break;
                }
                case "line":
                case "polygon":
                {
                    if (!SCP_GlobeDraw.TryParsePoints(iArgs.Get("points"), out List<SCP_GlobeLatLon> pts, out string w)) return SCP_CmdResult.Fail(2, "✗ " + w);
                    if (iOp == "line")
                    {
                        if (!NumArg(iArgs, "width", 0, 0, 200, out double wd, out w)) return SCP_CmdResult.Fail(2, "✗ " + w);
                        aCells = SCP_GlobeDraw.Line(g, pts, wd);
                        aDesc = $"線 {pts.Count} 點 筆寬半徑 {F(wd)} 格";
                    }
                    else
                    {
                        aCells = SCP_GlobeDraw.Polygon(g, pts);
                        aDesc = $"多邊形 {pts.Count} 點";
                    }
                    break;
                }
                default:   // fill
                {
                    if (!LatLonArg(iArgs, out SCP_GlobeLatLon p, out string w)) return SCP_CmdResult.Fail(2, "✗ " + w);
                    if (!NumArg(iArgs, "max_cells", DefaultMaxFill, 1, 20_000_000, out double mx, out w)) return SCP_CmdResult.Fail(2, "✗ " + w);
                    SCP_GlobeState s = iStore.Load();
                    aCells = SCP_GlobeDraw.Flood(s.Grid, s.Cells, p, (int)mx);
                    aDesc = $"油漆桶 ({F(p.Lat)},{F(p.Lon)})";
                    break;
                }
            }
            SCP_GlobeEvent? e = iStore.Paint(iErase ? "erase-" + iOp : iOp, aPersona, iArgs.Get("note").Trim(), aCells, aValue);
            var r = new SCP_CmdResult();
            string aColorText = aValue == SCP_GlobeCells.Empty ? "empty（擦回底色）" : SCP_GlobeCells.ToHex(aValue);
            if (e == null)
            {
                r.Lines.Add($"· {aDesc}：涵蓋 {aCells.Count:N0} 格，全部已經是 {aColorText} ⇒ 沒有寫事件");
                return r.AddValue("changed", "0").AddValue("covered", aCells.Count.ToString(CultureInfo.InvariantCulture));
            }
            int aChanged = e.Cells.Count / 3;
            r.Lines.Add($"✓ 事件 #{e.Seq} {aDesc}：涵蓋 {aCells.Count:N0} 格、實際改了 {aChanged:N0} 格 → {aColorText}");
            r.Lines.Add("  ↶ 畫錯了：op=undo");
            return r.AddValue("seq", e.Seq.ToString(CultureInfo.InvariantCulture))
                    .AddValue("changed", aChanged.ToString(CultureInfo.InvariantCulture))
                    .AddValue("covered", aCells.Count.ToString(CultureInfo.InvariantCulture))
                    .AddOutput(iStore.Paths.EventFile(e.Seq));
        }

        static SCP_CmdResult Undo(SCP_GlobeStore iStore, SCP_CmdArgs iArgs)
        {
            string aPersona = iArgs.Get("persona").Trim();
            if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ undo 要給 persona（記進事件）");
            iStore.LoadMeta();
            SCP_GlobeEvent? e = iStore.Undo(aPersona);
            if (e == null) return SCP_CmdResult.Fail(1, "· 沒有可以退的繪製（全部都已經退回了，或還沒畫過）").AddValue("seq", "0");
            SCP_GlobeEvent t = iStore.ReadEvent(e.Target);
            return SCP_CmdResult.Success($"↶ 事件 #{e.Seq}：退回 #{t.Seq}（{t.Op}，{t.Persona}，{t.Cells.Count / 3:N0} 格）")
                .AddValue("seq", e.Seq.ToString(CultureInfo.InvariantCulture))
                .AddValue("target", t.Seq.ToString(CultureInfo.InvariantCulture))
                .AddValue("restored", (t.Cells.Count / 3).ToString(CultureInfo.InvariantCulture));
        }

        static SCP_CmdResult Render(SCP_GlobeStore iStore, SCP_CmdArgs iArgs)
        {
            var v = new SCP_GlobeView();
            string aCenter = iArgs.Get("center").Trim();
            if (aCenter.Length > 0)
            {
                if (!SCP_GlobeDraw.TryParseLatLon(aCenter, out SCP_GlobeLatLon c, out string w)) return SCP_CmdResult.Fail(2, "✗ center：" + w);
                v.CenterLat = c.Lat; v.CenterLon = c.Lon;
            }
            if (!NumArg(iArgs, "zoom", 1, 0.01, 1000, out v.Zoom, out string w2)) return SCP_CmdResult.Fail(2, "✗ " + w2);
            if (!NumArg(iArgs, "size", 720, 16, 4096, out double sz, out w2)) return SCP_CmdResult.Fail(2, "✗ " + w2);
            v.Size = (int)sz;
            if (!NumArg(iArgs, "graticule", 10, 0, 90, out v.Graticule, out w2)) return SCP_CmdResult.Fail(2, "✗ " + w2);
            v.Seams = iArgs.Get("seams").Trim() == "1";
            if (iArgs.Get("zones").Trim() == "1") v.Zones = new SCP_GlobeZones(iStore.Paths).List();
            string aOut = iArgs.Get("out").Trim();
            string aPersona = iArgs.Get("persona").Trim(), aLetters = iArgs.Get("letters_root").Trim();
            if (aOut.Length > 0 && !Path.IsPathRooted(aOut)) return SCP_CmdResult.Fail(2, "✗ out 要絕對路徑：" + aOut);
            if (aOut.Length == 0 && aPersona.Length > 0)
            {
                // 每人一張：共用同一個檔的話，兩個人同時看會互相蓋掉對方的圖
                if (aLetters.Length == 0) return SCP_CmdResult.Fail(2, "✗ 給了 persona 但沒有 letters_root —— 不知道圖要寫進誰的 cmd 夾（或直接給 out）");
                var aRoot = new SCP_LettersRoot(aLetters);
                if (!Directory.Exists(SCP_LettersPaths.PersonaDir(aRoot, aPersona)))
                    return SCP_CmdResult.Fail(2, "✗ 找不到 persona 的信件夾：" + SCP_LettersPaths.PersonaDir(aRoot, aPersona));
                aOut = Path.Combine(SCP_LettersPaths.CmdDir(aRoot, aPersona), "globe_view.png");
            }
            if (aOut.Length == 0) aOut = Path.Combine(iStore.Paths.CacheDir, "view.png");
            SCP_GlobeState s = iStore.Load();
            byte[] aPng = SCP_GlobeRender.RenderPng(s, v);
            string? aDir = Path.GetDirectoryName(aOut);
            if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
            File.WriteAllBytes(aOut, aPng);
            string aPath = aOut.Replace('\\', '/');
            return SCP_CmdResult.Success($"🖼 {aPath}　中心 ({F(v.CenterLat)},{F(v.CenterLon)})　zoom {F(v.Zoom)}　{v.Size}px")
                .AddValue("path", aPath).AddOutput(aPath);
        }

        static SCP_CmdResult History(SCP_GlobeStore iStore, SCP_CmdArgs iArgs)
        {
            if (!NumArg(iArgs, "last", 10, 1, 1000, out double n, out string w)) return SCP_CmdResult.Fail(2, "✗ " + w);
            iStore.LoadMeta();
            var r = new SCP_CmdResult();
            foreach (SCP_GlobeEvent e in iStore.History((int)n))
                r.Lines.Add(e.Op == "undo"
                    ? $"#{e.Seq}  {e.At}  {e.Persona}  undo → 退 #{e.Target}"
                    : $"#{e.Seq}  {e.At}  {e.Persona}  {e.Op}  {e.Cells.Count / 3:N0} 格" + (e.Note.Length > 0 ? "　" + e.Note : ""));
            if (r.Lines.Count == 0) r.Lines.Add("（還沒有事件）");
            return r;
        }

        // ── 參數 ──────────────────────────────────────────────
        static bool LatLonArg(SCP_CmdArgs iArgs, out SCP_GlobeLatLon oP, out string oWhy)
            => SCP_GlobeDraw.TryParseLatLon(iArgs.Get("lat").Trim() + "," + iArgs.Get("lon").Trim(), out oP, out oWhy);

        static bool NumArg(SCP_CmdArgs iArgs, string iKey, double iDefault, double iMin, double iMax, out double oV, out string oWhy)
        {
            oWhy = "";
            string t = iArgs.Get(iKey).Trim();
            if (t.Length == 0) { oV = iDefault; return true; }
            if (!double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out oV) || double.IsNaN(oV) || oV < iMin || oV > iMax)
            { oWhy = $"{iKey} 要在 {F(iMin)}..{F(iMax)}：{t}"; return false; }
            return true;
        }

        static string F(double d) => d.ToString("0.###", CultureInfo.InvariantCulture);
    }
}
