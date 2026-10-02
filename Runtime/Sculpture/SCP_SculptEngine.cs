// 區塊職責：雕刻引擎的**資料層** —— box／carve／stamp2d／stampimg／slice／stats／export／exhibit 與 view 的「場景＋參數」準備。
//          語意（檢查順序、status、JSON 欄位與順序、exit code）沿用 python 時代的引擎（gura 2026-08-13 起），TASK-0377 搬成 C#。
// 物理意義：⭐ 本檔**不畫圖**：等角／GPU 渲染是宿主的事（<see cref="ISCP_SculptRenderer"/>，Senate.Desktop 註冊）。
//          view 只交出「過濾後的可見 voxel ＋ 渲染參數」；展品照也是同一條路 —— 沒有渲染器 ⇒ 大聲回報，⛔ 不出空白圖。
//          既有的事件／快取／展品檔是 python 時代寫下的、而且進 git ⇒ 本檔寫出的版面與它們逐字相同（見 SCP_SculptPy）。
// 數值影響：單次 box／stamp 上限 1,000,000 voxels（Cmd_Sculpture 的預授權用同一個常數 <see cref="MaxVolume"/>）；空間 0..255³。
//          每個 op 進來都先 <see cref="SCP_SculptStore.Load"/>（會重寫快取）—— 呼叫端要握雕刻鎖（Cmd_Sculpture 的 `_engine.lock`）。
// 失敗處置：參數不合 ⇒ 回 exit 2 的結果；python 會丟 traceback 的那幾格（重播遇到未知 op、canvas view 失敗…）⇒ exit 1 ＋ 一句人話。
//          ⛔ 不丟例外給呼叫端吞 —— 只有 <see cref="SCP_SculptReplayException"/> 會被各 op 轉成 exit 1。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Canvas;
using SCP.Core.Cmd;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Sculpture
{
    /// <summary>view 的準備結果：要畫什麼（<see cref="Voxels"/>）、怎麼畫（<see cref="Params"/>）。</summary>
    public sealed class SCP_SculptViewPlan
    {
        /// <summary>0 ＝ 可以畫；非 0 ＝ 參數不合（原因在 <see cref="Error"/>）。</summary>
        public int ExitCode;
        public string Error = "";
        /// <summary>渲染前 python 會印的行（例：「🏛️ 正在一鍵載入展品 …」）。</summary>
        public List<string> Lines = new List<string>();
        /// <summary>過濾後的可見 voxel，照 python 的深度序（z*10000+x+y，穩定排序）。</summary>
        public List<SCP_SculptVoxel> Voxels = new List<SCP_SculptVoxel>();
        public SCP_SculptRenderParams Params = new SCP_SculptRenderParams();
        public int TotalVoxels;
        /// <summary>python 印在 summary 的 Smooth= 值（GPU 渲染器不吃這個旗標，只為了輸出相同）。</summary>
        public string SmoothText = "False";

        /// <summary>
        /// 顏色不在 1..255 的可見 voxel 數（例：真實快取裡有 125 顆 color=8379391）。
        /// <para>python 的 get_rgb332_color 把越界 index 當 0 ⇒ 畫成**黑色**（它們仍是實心 voxel，不是空）。
        /// 場景契約裡 0 ＝ 空 ⇒ 這裡映到 index 4（與 stamp 的「純黑重映」同一個近黑色），並計數讓呼叫端大聲說。
        /// ⚠ 只影響交給渲染器的場景；voxel 表／快取／事件裡的原值一律不動。</para>
        /// </summary>
        public int OutOfRangeColors;

        /// <summary>python <c>cmd_view</c> 渲染完印的那幾行。</summary>
        public List<string> SummaryLines(string iOutputPath)
        {
            return new List<string>
            {
                "# 🎨 3D Isometric View Rendered (Exact Grid, Smooth=" + SmoothText + ")!",
                "  total_voxels   : " + TotalVoxels.ToString(CultureInfo.InvariantCulture),
                "  visible_rendered: " + Voxels.Count.ToString(CultureInfo.InvariantCulture),
                "  output_path     : " + iOutputPath,
            };
        }
    }

    public sealed class SCP_SculptEngine
    {
        public const long MaxVolume = 1000000;

        public SCP_SculptPaths Paths { get; }

        /// <summary>AgentCommands 資料根（stamp2d 叫 canvas view 時要傳的 data_root）。</summary>
        public string DataRoot { get; }

        /// <summary>時鐘（測試可換）。⚠ naive 本地時間 —— 與 python <c>datetime.now()</c> 同。</summary>
        public Func<DateTime> Clock = () => DateTime.Now;

        /// <summary>
        /// 展品照（exhibit register／stamp 的 exhibit_id）的渲染底。null ＝ <see cref="PythonDefaults"/>。
        /// <para>宿主給（Cmd_Sculpture 給「共用作用中的渲染設定」）；**每次出照都呼叫一次**（BuildPlan 會就地改它，⛔ 不共用同一份）。
        /// 丟例外 ⇒ 展品照沒出、轉成 warning（⛔ 不推翻已登錄的 preset 或已落的 voxel）。</para>
        /// </summary>
        public Func<SCP_SculptRenderParams>? PhotoBase;

        public SCP_SculptEngine(SCP_DataRoot iDataRoot)
        {
            DataRoot = iDataRoot.Value;
            Paths = new SCP_SculptPaths(iDataRoot);
        }

        /// <summary>測試隔離：雕刻根與畫布資料根分開給。</summary>
        public SCP_SculptEngine(SCP_SculptPaths iPaths, string iCanvasDataRoot)
        {
            Paths = iPaths;
            DataRoot = iCanvasDataRoot;
        }

        /// <summary>讀現況（＝ python <c>load_space_state()</c>；會重寫快取）。</summary>
        public SCP_SculptSpace LoadSpace() => SCP_SculptStore.Load(Paths);

        // ═════════════════════════════ box ═════════════════════════════
        public SCP_SculptResult Box(SCP_SculptBoxArgs iArgs)
        {
            SCP_SculptSpace aSpace;
            try { aSpace = LoadSpace(); } catch (SCP_SculptReplayException e) { return ReplayFail(e); }
            int x1 = Math.Min(iArgs.X1, iArgs.X2), x2 = Math.Max(iArgs.X1, iArgs.X2);
            int y1 = Math.Min(iArgs.Y1, iArgs.Y2), y2 = Math.Max(iArgs.Y1, iArgs.Y2);
            int z1 = Math.Min(iArgs.Z1, iArgs.Z2), z2 = Math.Max(iArgs.Z1, iArgs.Z2);
            x1 = Math.Max(0, x1); x2 = Math.Min(255, x2);
            y1 = Math.Max(0, y1); y2 = Math.Min(255, y2);
            z1 = Math.Max(0, z1); z2 = Math.Min(255, z2);
            // ⚠ 照 python 算（不夾 0）：整段在界外時兩軸各為負、乘起來可以是正的 —— 事件裡的 total_volume 就是這個數
            long aTotal = (long)(x2 - x1 + 1) * (y2 - y1 + 1) * (z2 - z1 + 1);
            if (aTotal > MaxVolume)
                return SCP_SculptResult.Error(1, "體積過大警告：單次 box 最大允許 1,000,000 voxels，當前為 " + aTotal + " voxels。");

            var aPlaced = new List<int[]>();
            int aSkipped = 0;
            for (int x = x1; x <= x2; x++)
                for (int y = y1; y <= y2; y++)
                    for (int z = z1; z <= z2; z++)
                    {
                        if (aSpace.Voxels.Get(x, y, z) == 0) aPlaced.Add(new[] { x, y, z });
                        else aSkipped++;
                    }

            var aEv = new SCP_SculptPyObj()
                .Put("op", "box").Put("persona", iArgs.Persona)
                .Put("x1", x1).Put("x2", x2).Put("y1", y1).Put("y2", y2).Put("z1", z1).Put("z2", z2)
                .Put("color", iArgs.Color).Put("total_volume", aTotal)
                .Put("placed_count", aPlaced.Count).Put("skipped_count", aSkipped)
                .Put("placed_voxels", aPlaced)
                .Put("timestamp", SCP_SculptPy.IsoFormat(Clock()));
            string aFile = SCP_SculptStore.RecordEvent(Paths, aEv, Clock(), out string aRel);
            // 與重播共用同一個語意核心（⚠ 0 顆落地 ⇒ placed_voxels 是空的 ⇒ 走舊式 AABB —— python 現況，照搬）
            SCP_SculptStore.ApplyBox(aSpace, iArgs.Color, aPlaced, x1, x2, y1, y2, z1, z2);
            aSpace.LastEventFile = aRel;
            SCP_SculptStore.SaveCache(Paths, aSpace);

            var aRes = new SCP_SculptBoxResult
            {
                Persona = iArgs.Persona, TotalVolume = aTotal, PlacedCount = aPlaced.Count,
                SkippedCount = aSkipped, EventFile = aFile,
            };
            aRes.Json = new SCP_SculptPyObj()
                .Put("status", "success").Put("op", "box").Put("persona", iArgs.Persona)
                .Put("total_volume", aTotal).Put("placed_count", aPlaced.Count).Put("skipped_count", aSkipped)
                .Put("event_file", aFile);
            return aRes;
        }

        // ═════════════════════════════ carve ═════════════════════════════
        public SCP_SculptResult Carve(SCP_SculptCarveArgs iArgs)
        {
            SCP_SculptSpace aSpace;
            try { aSpace = LoadSpace(); } catch (SCP_SculptReplayException e) { return ReplayFail(e); }
            // ⚠ python 的 carve **不 clamp**（與 box 不同）—— 照搬
            int x1 = Math.Min(iArgs.X1, iArgs.X2), x2 = Math.Max(iArgs.X1, iArgs.X2);
            int y1 = Math.Min(iArgs.Y1, iArgs.Y2), y2 = Math.Max(iArgs.Y1, iArgs.Y2);
            int z1 = Math.Min(iArgs.Z1, iArgs.Z2), z2 = Math.Max(iArgs.Z1, iArgs.Z2);

            var aCarved = new List<int[]>();
            long aVol = (long)(x2 - x1 + 1) * (y2 - y1 + 1) * (z2 - z1 + 1);
            if (aVol > aSpace.Voxels.Count)
            {
                // 框比整張表還大 ⇒ 掃表再依 (x,y,z) 排序 —— 與 python 的三層迴圈產出**同一個序列**，但不必跑完 2^24 格
                foreach (var e in aSpace.Voxels.Entries())
                    if (e.Color != 0 && e.X >= x1 && e.X <= x2 && e.Y >= y1 && e.Y <= y2 && e.Z >= z1 && e.Z <= z2)
                        aCarved.Add(new[] { e.X, e.Y, e.Z });
                aCarved.Sort((a, b) => a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1] != b[1] ? a[1].CompareTo(b[1]) : a[2].CompareTo(b[2]));
            }
            else
            {
                for (int x = x1; x <= x2; x++)
                    for (int y = y1; y <= y2; y++)
                        for (int z = z1; z <= z2; z++)
                            if (aSpace.Voxels.Get(x, y, z) != 0) aCarved.Add(new[] { x, y, z });
            }

            var aEv = new SCP_SculptPyObj()
                .Put("op", "carve").Put("persona", iArgs.Persona)
                .Put("x1", x1).Put("x2", x2).Put("y1", y1).Put("y2", y2).Put("z1", z1).Put("z2", z2)
                .Put("carved_count", aCarved.Count).Put("carved_voxels", aCarved)
                .Put("timestamp", SCP_SculptPy.IsoFormat(Clock()));
            string aFile = SCP_SculptStore.RecordEvent(Paths, aEv, Clock(), out string aRel);
            SCP_SculptStore.ApplyCarve(aSpace, aCarved, x1, x2, y1, y2, z1, z2);
            aSpace.LastEventFile = aRel;
            SCP_SculptStore.SaveCache(Paths, aSpace);

            var aRes = new SCP_SculptCarveResult { Persona = iArgs.Persona, CarvedCount = aCarved.Count, EventFile = aFile };
            aRes.Json = new SCP_SculptPyObj()
                .Put("status", "success").Put("op", "carve").Put("persona", iArgs.Persona)
                .Put("carved_count", aCarved.Count).Put("event_file", aFile);
            return aRes;
        }

        // ═════════════════════════════ stamp 共用 ═════════════════════════════
        // facing 是貼片的法線。±Z → u→X、v→Y（v 反向）；±Y → u→X、v→Z；±X → u→Z、v→Y（v 反向）。
        // 2D 的 y 往下、3D 的 Y 往上 ⇒ 垂直軸翻轉，否則貼上去的山會倒過來（而它只會「怪」，不會報錯）。
        static bool TryAxis(string iFacing, out string oU, out string oV, out string oN, out bool oFlip)
        {
            oU = oV = oN = ""; oFlip = false;
            switch (iFacing)
            {
                case "z+": case "z-": oU = "x"; oV = "y"; oN = "z"; oFlip = true; return true;
                case "y+": case "y-": oU = "x"; oV = "z"; oN = "y"; oFlip = false; return true;
                case "x+": case "x-": oU = "z"; oV = "y"; oN = "x"; oFlip = true; return true;
                default: return false;
            }
        }

        static int AxisIndex(string iAxis) => iAxis == "x" ? 0 : iAxis == "y" ? 1 : 2;

        /// <summary>python <c>(s or "z+").lower().replace("+z","z+").replace("-z","z-")</c>。</summary>
        static string NormFacing(string? iFacing)
        {
            string a = string.IsNullOrEmpty(iFacing) ? "z+" : iFacing!;
            return a.ToLowerInvariant().Replace("+z", "z+").Replace("-z", "z-");
        }

        /// <summary>python <c>parse_stamp_geometry</c>：失敗回錯誤訊息（不含 ❌）。</summary>
        static string? ParseGeometry(SCP_SculptStampArgs iArgs, out string oFacing, out int[] oAt, out int oThickness)
        {
            oFacing = NormFacing(iArgs.Facing);
            oAt = new int[3];
            oThickness = Math.Max(1, iArgs.Thickness);
            if (!TryAxis(oFacing, out _, out _, out _, out _))
                return "facing 非法: " + (iArgs.Facing ?? "None") + "（可用 x+ x- y+ y- z+ z-）";
            string[] p = (iArgs.At ?? "None").Split(',');
            if (p.Length != 3 || !SCP_SculptPy.TryInt(p[0], out oAt[0]) || !SCP_SculptPy.TryInt(p[1], out oAt[1])
                || !SCP_SculptPy.TryInt(p[2], out oAt[2]))
                return "--at 需為 'x,y,z': " + (iArgs.At ?? "None");
            return null;
        }

        /// <summary>python <c>stamp_pixels</c>：已繪像素 → 要落的 voxel（含越界／佔用計數與純黑重映）。</summary>
        static List<int[]> StampPixels(SCP_SculptSpace iSpace, List<(int U, int V, int Color)> iPainted, int iRegionH,
                                       int[] iAt, string iFacing, int iThickness, bool iOverwrite,
                                       out int oSkipped, out int oOob, out int oBlack, out SCP_SculptAxisMap oAxis)
        {
            TryAxis(iFacing, out string u, out string v, out string n, out bool aFlip);
            int aNDir = iFacing.EndsWith("+", StringComparison.Ordinal) ? 1 : -1;
            int iu = AxisIndex(u), iv = AxisIndex(v), inn = AxisIndex(n);
            var aPlaced = new List<int[]>();
            oSkipped = oOob = oBlack = 0;
            var p = new long[3];
            foreach (var px in iPainted)
            {
                int c = px.Color;
                if (c == 0) { c = 4; oBlack++; }   // 3D 的 0 = 空 → 純黑重映到最近的非零暗色 (0,36,0)
                int vv = aFlip ? (iRegionH - 1 - px.V) : px.V;
                for (int t = 0; t < iThickness; t++)
                {
                    p[0] = iAt[0]; p[1] = iAt[1]; p[2] = iAt[2];
                    p[iu] += px.U;
                    p[iv] += vv;
                    p[inn] += (long)aNDir * t;
                    if (p[0] < 0 || p[0] > 255 || p[1] < 0 || p[1] > 255 || p[2] < 0 || p[2] > 255) { oOob++; continue; }
                    int x = (int)p[0], y = (int)p[1], z = (int)p[2];
                    if (!iOverwrite && iSpace.Voxels.Get(x, y, z) != 0) { oSkipped++; continue; }
                    aPlaced.Add(new[] { x, y, z, c });
                }
            }
            oAxis = new SCP_SculptAxisMap { U = u, V = v, Normal = n, VFlipped = aFlip };
            return aPlaced;
        }

        /// <summary>
        /// python <c>run_stamp</c>：閘門 → 投影 → 落事件 → 回報。檢查順序與 python 相同：
        /// 讀狀態 → 幾何參數 → ①expect_pixels → 沒有像素 → ②體積上限 → 投影 → ③越界 → 全被佔用 → 落事件 → 展品。
        /// </summary>
        SCP_SculptResult RunStamp(SCP_SculptStampArgs iArgs, string iOp, List<(int U, int V, int Color)> iPainted,
                                  int iRegionW, int iRegionH, SCP_SculptPyObj iSrcMeta)
        {
            SCP_SculptSpace aSpace;
            try { aSpace = LoadSpace(); } catch (SCP_SculptReplayException e) { return ReplayFail(e); }

            string? aErr = ParseGeometry(iArgs, out string aFacing, out int[] aAt, out int aThickness);
            if (aErr != null) return SCP_SculptResult.Error(2, aErr);

            var aRes = new SCP_SculptStampResult
            {
                Op = iOp, Persona = iArgs.Persona, ExpectPixels = iArgs.ExpectPixels, PaintedSourcePixels = iPainted.Count,
                RegionW = iRegionW, RegionH = iRegionH, At = aAt, Facing = aFacing, Thickness = aThickness, Source = iSrcMeta,
            };

            // 閘門①：期望值 —— 「你看的那張」與「我吃的這張」是不是同一批 bytes
            if (iArgs.ExpectPixels.HasValue && iArgs.ExpectPixels.Value != iPainted.Count)
            {
                aRes.ExitCode = 4;
                aRes.Reason = "--expect-pixels " + iArgs.ExpectPixels.Value + " 與實際非透明像素 " + iPainted.Count
                              + " 不符 —— 來源已變動或不是預覽的那張圖；未貼、未扣費";
                aRes.Json = new SCP_SculptPyObj()
                    .Put("status", "mismatch").Put("op", iOp).Put("reason", aRes.Reason)
                    .Put("expect_pixels", iArgs.ExpectPixels.Value).Put("actual_painted_pixels", iPainted.Count)
                    .Put("source", iSrcMeta);
                return aRes;
            }

            if (iPainted.Count == 0)
            {
                aRes.ExitCode = 3;
                aRes.Reason = "來源沒有任何非透明像素 —— 透明視為未繪製，無可貼內容";
                aRes.Json = new SCP_SculptPyObj()
                    .Put("status", "empty").Put("op", iOp).Put("reason", aRes.Reason)
                    .Put("region_w", iRegionW).Put("region_h", iRegionH).Put("source", iSrcMeta);
                return aRes;
            }

            // 閘門②：體積上限
            long aTotal = (long)iPainted.Count * aThickness;
            if (aTotal > MaxVolume)
                return SCP_SculptResult.Error(1, "體積過大：" + aTotal + " voxels（上限 1,000,000）—— 縮小圖或降 thickness");

            List<int[]> aPlaced = StampPixels(aSpace, iPainted, iRegionH, aAt, aFacing, aThickness, iArgs.Overwrite,
                                              out int aSkipped, out int aOob, out int aBlack, out SCP_SculptAxisMap aAxis);
            aRes.AxisMap = aAxis;
            aRes.SkippedOccupied = aSkipped;
            aRes.OutOfBounds = aOob;
            aRes.RemappedBlack = aBlack;
            aRes.WouldPlace = aPlaced.Count;

            // 閘門③：越界 —— 預設不靜默裁切
            if (aOob > 0 && !iArgs.AllowClip)
            {
                aRes.ExitCode = 5;
                aRes.Reason = aOob + " 個 voxel 落在 256³ 空間之外 —— 未貼、未扣費。圖 " + iRegionW + "x" + iRegionH
                              + " @ at=" + SCP_SculptPy.ListRepr(aAt) + " facing=" + aFacing
                              + " 放不下；改小 --at、用 --resize 縮圖，或顯式 --allow-clip 接受裁切";
                aRes.Json = new SCP_SculptPyObj()
                    .Put("status", "out_of_bounds").Put("op", iOp).Put("reason", aRes.Reason)
                    .Put("out_of_bounds", aOob).Put("would_place", aPlaced.Count)
                    .Put("image_size", iRegionW + "x" + iRegionH).Put("at", aAt).Put("facing", aFacing)
                    .Put("axis_map", aAxis.ToPy()).Put("source", iSrcMeta);
                return aRes;
            }

            if (aPlaced.Count == 0)
            {
                aRes.ExitCode = 3;
                aRes.Reason = "所有目標格都越界或已被佔用（--overwrite 可覆蓋）";
                aRes.Json = new SCP_SculptPyObj()
                    .Put("status", "empty").Put("op", iOp).Put("reason", aRes.Reason)
                    .Put("out_of_bounds", aOob).Put("skipped_occupied", aSkipped).Put("source", iSrcMeta);
                return aRes;
            }

            var aEv = new SCP_SculptPyObj()
                .Put("op", iOp).Put("persona", iArgs.Persona).Put("source", iSrcMeta)
                .Put("at", aAt).Put("facing", aFacing).Put("thickness", aThickness)
                .Put("axis_map", aAxis.ToPy())
                .Put("region_w", iRegionW).Put("region_h", iRegionH)
                .Put("painted_source_pixels", iPainted.Count)
                .Put("placed_count", aPlaced.Count)
                .Put("skipped_occupied", aSkipped)
                .Put("out_of_bounds", aOob)
                .Put("remapped_black", aBlack)
                .Put("placed_colored", aPlaced)
                .Put("timestamp", SCP_SculptPy.IsoFormat(Clock()));
            string aFile = SCP_SculptStore.RecordEvent(Paths, aEv, Clock(), out string aRel);
            foreach (int[] v in aPlaced) aSpace.Voxels.Set(v[0], v[1], v[2], v[3]);
            aSpace.LastEventFile = aRel;
            SCP_SculptStore.SaveCache(Paths, aSpace);

            // 展品登錄放在 voxel 落地與快取之後 —— 展品是作品的「框」，框只能框已經存在的東西
            SCP_SculptPyObj? aExhibit = AutoExhibit(iArgs, aPlaced, aSpace);

            aRes.PlacedCount = aPlaced.Count;
            aRes.Exhibit = aExhibit;
            aRes.EventFile = aFile;
            aRes.Json = new SCP_SculptPyObj()
                .Put("status", "success").Put("op", iOp).Put("persona", iArgs.Persona)
                .Put("exhibit", aExhibit)
                .Put("region", iRegionW + "x" + iRegionH)
                .Put("painted_source_pixels", iPainted.Count)
                .Put("placed_count", aPlaced.Count)
                .Put("skipped_occupied", aSkipped)
                .Put("out_of_bounds", aOob)
                .Put("remapped_black", aBlack)
                .Put("at", aAt).Put("facing", aFacing).Put("thickness", aThickness)
                .Put("axis_map", aAxis.ToPy())
                .Put("source", iSrcMeta)
                .Put("event_file", aFile);
            return aRes;
        }

        // ═════════════════════════════ stamp2d ═════════════════════════════
        /// <summary>
        /// 2D 共用畫布的一塊 → 預覽 PNG（落檔 <c>Sculpture/_stamp_src.png</c>，sha256 進事件）→ 3D。
        /// <para>⭐ 預覽走 in-process 的 <c>canvas op=view</c>（帶 persona），讀它回的 <c>path_t</c> ——
        /// ⛔ 不自己組路徑（view 的落點是 per-persona 的，猜錯就是吃到別人的圖）。</para>
        /// </summary>
        public SCP_SculptResult Stamp2d(SCP_SculptStamp2dArgs iArgs)
        {
            int x1 = Math.Min(iArgs.SrcX1, iArgs.SrcX2), x2 = Math.Max(iArgs.SrcX1, iArgs.SrcX2);
            int y1 = Math.Min(iArgs.SrcY1, iArgs.SrcY2), y2 = Math.Max(iArgs.SrcY1, iArgs.SrcY2);
            x1 = Math.Max(0, x1); y1 = Math.Max(0, y1);
            x2 = Math.Min(SCP_CanvasSpec.Width - 1, x2); y2 = Math.Min(SCP_CanvasSpec.Height - 1, y2);

            var aRaw = new Dictionary<string, string>
            {
                ["data_root"] = DataRoot,
                ["op"] = "view",
                ["persona"] = iArgs.Persona,
                ["region"] = x1 + "," + y1 + "," + (x2 - x1 + 1) + "," + (y2 - y1 + 1),
            };
            if (iArgs.LettersRoot.Trim().Length > 0) aRaw["letters_root"] = iArgs.LettersRoot.Trim();
            SCP_CmdResult aView = SCP_CmdRegistry.Dispatch("canvas", aRaw);
            if (aView.ExitCode != 0)
            {
                string aTail = string.Join("\n", aView.Lines);
                if (aTail.Length > 500) aTail = aTail.Substring(aTail.Length - 500);
                return SCP_SculptResult.Error(1, "canvas view 失敗（exit " + aView.ExitCode + "）：" + aTail);
            }
            string aPathT = "";
            foreach (var kv in aView.Values) if (kv.Key == "path_t") aPathT = kv.Value;
            if (aPathT.Length == 0 || !File.Exists(aPathT))
                // 「Cmd 說成功」與「產物在」是兩個讀數 —— 這一格不可以靜默往下走
                return SCP_SculptResult.Error(1, "canvas view 回報成功、但透明變體沒出現：" + (aPathT.Length > 0 ? aPathT : "（沒回 path_t）"));

            byte[] aBytes;
            List<(int U, int V, int Color)> aPainted;
            int aW, aH, aOpaque = 0;
            try
            {
                aBytes = File.ReadAllBytes(aPathT);
                Directory.CreateDirectory(Paths.Root);
                File.WriteAllBytes(Paths.StampSrc, aBytes);
                byte[] aRgba = SCP_SculptPng.DecodeRgba(aBytes, out int aSw, out int aSh);
                for (int i = 3; i < aRgba.Length; i += 4) if (aRgba[i] != 0) aOpaque++;
                aPainted = SCP_SculptPng.ToPainted(aBytes, iArgs.AlphaThreshold, 0, 0, out aW, out aH);
            }
            catch (Exception e)
            {
                return SCP_SculptResult.Error(1, "預覽 PNG 讀取失敗 " + Paths.StampSrc + ": " + e.Message);
            }

            var aSrc = new SCP_SculptPyObj()
                .Put("kind", "canvas_region")
                .Put("src", new SCP_SculptPyObj().Put("x1", iArgs.SrcX1).Put("y1", iArgs.SrcY1)
                                                 .Put("x2", iArgs.SrcX2).Put("y2", iArgs.SrcY2))
                .Put("preview_png", Paths.StampSrc)
                .Put("sha256", SCP_SculptPy.Sha256Hex(aBytes))
                .Put("non_transparent_pixels", aOpaque)
                .Put("alpha_threshold", iArgs.AlphaThreshold);
            return RunStamp(iArgs, "stamp2d", aPainted, aW, aH, aSrc);
        }

        // ═════════════════════════════ stampimg ═════════════════════════════
        public SCP_SculptResult StampImg(SCP_SculptStampImgArgs iArgs)
        {
            string aShown = SCP_SculptPy.PathStr(iArgs.Png);
            if (!File.Exists(iArgs.Png) && !Directory.Exists(iArgs.Png))
                return SCP_SculptResult.Error(2, "圖檔不存在: " + aShown);

            int aRw = 0, aRh = 0;
            if (iArgs.Resize.Length > 0)
            {
                string[] p = iArgs.Resize.Split(',');
                if (p.Length != 2 || !SCP_SculptPy.TryInt(p[0], out aRw) || !SCP_SculptPy.TryInt(p[1], out aRh) || aRw <= 0 || aRh <= 0)
                    return SCP_SculptResult.Error(2, "--resize 需為 'W,H' 且為正整數: " + iArgs.Resize);
            }

            byte[] aBytes;
            List<(int U, int V, int Color)> aPainted;
            int aW, aH;
            try
            {
                aBytes = File.ReadAllBytes(iArgs.Png);
                aPainted = SCP_SculptPng.ToPainted(aBytes, iArgs.AlphaThreshold, aRw, aRh, out aW, out aH);
            }
            catch (Exception e)
            {
                return SCP_SculptResult.Error(2, "讀圖失敗 " + aShown + ": " + e.Message);
            }

            var aSrc = new SCP_SculptPyObj()
                .Put("kind", "image_file")
                .Put("png", SCP_SculptPy.FullPathStr(iArgs.Png))
                .Put("sha256", SCP_SculptPy.Sha256Hex(aBytes))
                .Put("image_size", aW + "x" + aH)
                .Put("resized", aRw > 0 ? aRw + "," + aRh : null)
                .Put("alpha_threshold", iArgs.AlphaThreshold);
            return RunStamp(iArgs, "stampimg", aPainted, aW, aH, aSrc);
        }

        // ═════════════════════════════ auto exhibit ═════════════════════════════
        /// <summary>
        /// python <c>_auto_exhibit</c>：依「這次實際貼到哪」反推 bbox，與既有展品的 bbox **union**（不覆蓋），外擴 margin 成 region。
        /// <para>⚠ union 對**未加 margin 的 bbox** 做（對 region 做會每刀往外爬一個 margin）。舊展品沒有 bbox ⇒ 退回讀 region。</para>
        /// <para>失敗只回報（warning），不推翻已成功的貼圖。</para>
        /// </summary>
        SCP_SculptPyObj? AutoExhibit(SCP_SculptStampArgs iArgs, List<int[]> iPlaced, SCP_SculptSpace iSpace)
        {
            string? aId = iArgs.ExhibitId;
            if (string.IsNullOrEmpty(aId)) return null;
            int aMargin = Math.Max(0, iArgs.ExhibitMargin ?? 0);
            int x1 = int.MaxValue, x2 = int.MinValue, y1 = int.MaxValue, y2 = int.MinValue, z1 = int.MaxValue, z2 = int.MinValue;
            foreach (int[] v in iPlaced)
            {
                x1 = Math.Min(x1, v[0]); x2 = Math.Max(x2, v[0]);
                y1 = Math.Min(y1, v[1]); y2 = Math.Max(y2, v[1]);
                z1 = Math.Min(z1, v[2]); z2 = Math.Max(z2, v[2]);
            }

            List<KeyValuePair<string, SCP_JsonData>> aAll = LoadExhibits();
            SCP_JsonData? aOld = null;
            foreach (var kv in aAll) if (kv.Key == aId) aOld = kv.Value;
            bool aHasOld = SCP_SculptPy.Truthy(aOld);
            if (aHasOld)
            {
                SCP_JsonData aB = aOld!["bbox"];
                if (!SCP_SculptPy.Truthy(aB)) aB = aOld["region"];
                string aPrevText = SCP_SculptPy.Truthy(aB) ? SCP_SculptPy.Str(aB) : "";
                if (TryParseRegion(aPrevText, out int[] pr))
                {
                    x1 = Math.Min(x1, pr[0]); x2 = Math.Max(x2, pr[1]);
                    y1 = Math.Min(y1, pr[2]); y2 = Math.Max(y2, pr[3]);
                    z1 = Math.Min(z1, pr[4]); z2 = Math.Max(z2, pr[5]);
                }
            }
            string aBbox = x1 + ".." + x2 + "," + y1 + ".." + y2 + "," + z1 + ".." + z2;
            string aRegion = Clamp255(x1 - aMargin) + ".." + Clamp255(x2 + aMargin) + ","
                             + Clamp255(y1 - aMargin) + ".." + Clamp255(y2 + aMargin) + ","
                             + Clamp255(z1 - aMargin) + ".." + Clamp255(z2 + aMargin);

            var aPreset = new SCP_SculptPyObj();
            if (aHasOld) foreach (string k in aOld!.Keys) aPreset.Put(k, aOld[k]);
            object? Get(string iKey, object? iDefault) => aPreset.TryGet(iKey, out object? v) ? v : iDefault;
            object? Or(object? a, object? b) => TruthyObj(a) ? a : b;

            string aNow1 = SCP_SculptPy.IsoFormat(Clock());
            object? aTitle = Or(Or(string.IsNullOrEmpty(iArgs.ExhibitTitle) ? null : iArgs.ExhibitTitle, Get("title", null)), aId);
            object? aAuthor = Or(Get("author", null), iArgs.Persona);
            object? aDesc = string.IsNullOrEmpty(iArgs.ExhibitDesc) ? Get("description", "") : iArgs.ExhibitDesc;
            object? aExclude = Get("exclude_color", "");
            object? aBg = Get("bg_color", "");
            object? aSky = Get("skybox", "");
            object? aLight = Get("light_dir", "-1,-1,-1");
            object? aAmb = Get("ambient", 0.4);
            object? aSmooth = Get("smooth", false);
            object? aShadow = Get("shadow", false);
            object? aZoom = Get("zoom", null);
            object? aCreated = Or(Get("created_at", null), aNow1);
            aPreset.Put("id", aId).Put("title", aTitle).Put("author", aAuthor).Put("description", aDesc)
                   .Put("bbox", aBbox).Put("region", aRegion)
                   .Put("exclude_color", aExclude).Put("bg_color", aBg).Put("skybox", aSky)
                   .Put("light_dir", aLight).Put("ambient", aAmb).Put("smooth", aSmooth).Put("shadow", aShadow)
                   .Put("zoom", aZoom).Put("created_at", aCreated)
                   .Put("updated_at", SCP_SculptPy.IsoFormat(Clock()));

            var aInfo = new SCP_SculptPyObj().Put("id", aId).Put("region", aRegion)
                                             .Put("mode", aHasOld ? "updated" : "created")
                                             .Put("title", aTitle).Put("author", aAuthor);
            try
            {
                aInfo.Put("file", SaveExhibit(aId!, aPreset));
                aInfo.Put("photo", RenderExhibitPhoto(iSpace, aPreset));
            }
            catch (Exception e)
            {
                aInfo.Put("warning", "展品登錄／出圖失敗（貼圖本身已成功、已結算）: " + e.Message);
            }
            return aInfo;
        }

        static int Clamp255(int v) => Math.Max(0, Math.Min(255, v));

        static bool TruthyObj(object? a)
        {
            switch (a)
            {
                case null: return false;
                case string s: return s.Length > 0;
                case bool b: return b;
                case int i: return i != 0;
                case long l: return l != 0;
                case double d: return d != 0;
                case SCP_JsonData j: return SCP_SculptPy.Truthy(j);
                case System.Collections.ICollection c: return c.Count > 0;
                default: return true;
            }
        }

        /// <summary>python <c>_parse_region</c>：'x1..x2,y1..y2,z1..z2'（各軸 min/max 正規化）；不合格 ⇒ false（不猜、不吞成 0）。</summary>
        public static bool TryParseRegion(string? iText, out int[] oBounds)
        {
            oBounds = new int[6];
            string[] parts = (iText ?? "None").Split(',');
            if (parts.Length != 3) return false;
            for (int i = 0; i < 3; i++)
            {
                string[] ab = parts[i].Split(new[] { ".." }, StringSplitOptions.None);
                if (ab.Length != 2 || !SCP_SculptPy.TryInt(ab[0], out int lo) || !SCP_SculptPy.TryInt(ab[1], out int hi)) return false;
                oBounds[i * 2] = Math.Min(lo, hi);
                oBounds[i * 2 + 1] = Math.Max(lo, hi);
            }
            return true;
        }

        // ═════════════════════════════ slice ═════════════════════════════
        /// <summary>
        /// python <c>cmd_slice</c>：region 沿 axis 壓成 RGBA PNG（空＝alpha 0；厚度 &gt; 1 前覆蓋後）。與 stamp 共用軸映射 ⇒ 可往返。
        /// <para>⚠ PNG 的 bytes 與 PIL 存的不同（編碼器不同），解碼後的像素相同 —— sha256 描述的是本檔寫出的那張。</para>
        /// </summary>
        public SCP_SculptResult Slice(SCP_SculptSliceArgs iArgs)
        {
            SCP_SculptSpace aSpace;
            try { aSpace = LoadSpace(); } catch (SCP_SculptReplayException e) { return ReplayFail(e); }

            string aAxis = NormFacing(iArgs.Axis);
            if (!TryAxis(aAxis, out string u, out string v, out string n, out bool aFlip))
                return SCP_SculptResult.Error(2, "axis 非法: " + iArgs.Axis + "（可用 x+ x- y+ y- z+ z-）");
            if (!TryParseRegion(iArgs.Region, out int[] rg))
                return SCP_SculptResult.Error(2, "--region 需為 'x1..x2,y1..y2,z1..z2': " + iArgs.Region);

            int iu = AxisIndex(u), iv = AxisIndex(v), inn = AxisIndex(n);
            int u1 = rg[iu * 2], u2 = rg[iu * 2 + 1], v1 = rg[iv * 2], v2 = rg[iv * 2 + 1], n1 = rg[inn * 2], n2 = rg[inn * 2 + 1];
            int w = u2 - u1 + 1, h = v2 - v1 + 1;
            int aThickness = n2 - n1 + 1;
            if ((long)w * h > SCP_SculptPng.BombPixels)
                return SCP_SculptResult.Error(2, "slice 輸出 " + w + "x" + h + " 太大");
            bool aPlus = aAxis.EndsWith("+", StringComparison.Ordinal);

            var aRgba = new byte[(long)w * h * 4];
            int aHit = 0;
            var c = new int[3];
            for (int vi = 0; vi < h; vi++)
                for (int ui = 0; ui < w; ui++)
                {
                    c[iu] = u1 + ui;
                    c[iv] = aFlip ? v1 + (h - 1 - vi) : v1 + vi;
                    for (int k = 0; k < aThickness; k++)        // 前覆蓋後：第一顆非空就停
                    {
                        c[inn] = aPlus ? n1 + k : n2 - k;
                        int aIdx = aSpace.Voxels.Get(c[0], c[1], c[2]);
                        if (aIdx == 0) continue;
                        Rgb332(aIdx, out byte r, out byte g, out byte b);
                        long o = ((long)vi * w + ui) * 4;
                        aRgba[o] = r; aRgba[o + 1] = g; aRgba[o + 2] = b; aRgba[o + 3] = 255;
                        aHit++;
                        break;
                    }
                }

            var aRes = new SCP_SculptSliceResult
            {
                Region = iArgs.Region, Axis = aAxis, Size = w + "x" + h, Thickness = aThickness,
                AxisMap = new SCP_SculptAxisMap { U = u, V = v, Normal = n, VFlipped = aFlip }, NonTransparentPixels = aHit,
            };
            if (aHit == 0)
            {
                aRes.ExitCode = 3;
                aRes.Json = new SCP_SculptPyObj()
                    .Put("status", "empty").Put("op", "slice")
                    .Put("reason", "region " + iArgs.Region + " 沿 " + aAxis + " 切完整張全空 —— 未落檔")
                    .Put("size", aRes.Size).Put("thickness", aThickness);
                return aRes;
            }

            string aOut = iArgs.Out.Length > 0 ? SCP_SculptPy.PathStr(iArgs.Out) : Paths.LastSlice;
            string? aDir = Path.GetDirectoryName(Path.GetFullPath(aOut));
            if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
            byte[] aPng = SCP_CanvasPng.EncodeRgbaRows(aRgba, w, h);
            File.WriteAllBytes(aOut, aPng);
            aRes.Sha256 = SCP_SculptPy.Sha256Hex(File.ReadAllBytes(aOut));
            aRes.OutputPath = aOut;
            aRes.Json = new SCP_SculptPyObj()
                .Put("status", "success").Put("op", "slice")
                .Put("region", iArgs.Region).Put("axis", aAxis)
                .Put("size", aRes.Size).Put("thickness", aThickness)
                .Put("axis_map", aRes.AxisMap.ToPy())
                .Put("non_transparent_pixels", aHit)
                .Put("sha256", aRes.Sha256)
                .Put("output_path", aOut)
                .Put("note", "貼回 3D：stampimg --png \"" + aOut + "\" --expect-pixels " + aHit
                             + " --at <近端那一層的 at> --facing " + aAxis);
            return aRes;
        }

        /// <summary>python <c>get_rgb332_color</c>：越界 index 當 0。</summary>
        static void Rgb332(int iIdx, out byte r, out byte g, out byte b)
        {
            SCP_CanvasPalette.IndexToRgb(iIdx < 0 || iIdx > 255 ? 0 : iIdx, out r, out g, out b);
        }

        // ═════════════════════════════ stats ═════════════════════════════
        public SCP_SculptResult Stats()
        {
            SCP_SculptSpace aSpace;
            try { aSpace = LoadSpace(); } catch (SCP_SculptReplayException e) { return ReplayFail(e); }
            int n = aSpace.Voxels.Count;
            var aRes = new SCP_SculptStatsResult { TotalVoxels = n };
            aRes.Lines.Add("# 📊 3D Sculpture Stats:");
            aRes.Lines.Add("  總非空 Voxels 數 : " + n.ToString(CultureInfo.InvariantCulture));
            aRes.Lines.Add("  空間使用率       : " + ((double)n / 16777216 * 100).ToString("F6", CultureInfo.InvariantCulture) + "%");
            return aRes;
        }

        // ═════════════════════════════ 過濾（view／export 共用） ═════════════════════════════
        /// <summary>
        /// python 的 region 字串解析（view／export／展品照共用的那一份，**不是** _parse_region）：
        /// 去空白、逐軸**依序賦值**、任何一步失敗就停在那裡（前面已賦值的軸保留）—— python 的 try 包著整段，照搬。
        /// ⚠ 不做 min/max 正規化：寫反的軸 ⇒ 什麼都不可見（python 同）。
        /// </summary>
        static void ParseViewRegion(string? iRegion, int[] ioR)
        {
            ioR[0] = 0; ioR[1] = 255; ioR[2] = 0; ioR[3] = 255; ioR[4] = 0; ioR[5] = 255;
            if (string.IsNullOrEmpty(iRegion)) return;
            string[] parts = iRegion!.Replace(" ", "").Split(',');
            for (int axis = 0; axis < 3; axis++)
            {
                if (axis >= parts.Length) return;
                string[] ab = parts[axis].Split(new[] { ".." }, StringSplitOptions.None);
                if (ab.Length != 2 || !SCP_SculptPy.TryInt(ab[0], out int a) || !SCP_SculptPy.TryInt(ab[1], out int b)) return;
                ioR[axis * 2] = a; ioR[axis * 2 + 1] = b;
            }
        }

        /// <summary>python 的 exclude-color 解析：全對才收、任何一個不是整數 ⇒ 空集合。</summary>
        static HashSet<int> ParseExclude(string? iText)
        {
            var aSet = new HashSet<int>();
            if (string.IsNullOrEmpty(iText)) return aSet;
            foreach (string p in iText!.Split(','))
            {
                if (!SCP_SculptPy.TryInt(p, out int c)) return new HashSet<int>();
                aSet.Add(c);
            }
            return aSet;
        }

        /// <summary>python <c>_filtered_voxels</c>：region 內、未被排除色濾掉的 voxel，照表序。</summary>
        static List<(int X, int Y, int Z, int Color)> Filter(SCP_SculptSpace iSpace, string? iRegion, string? iExclude)
        {
            var r = new int[6];
            ParseViewRegion(iRegion, r);
            HashSet<int> aEx = ParseExclude(iExclude);
            var aOut = new List<(int, int, int, int)>();
            foreach (var e in iSpace.Voxels.Entries())
            {
                if (aEx.Contains(e.Color)) continue;
                if (r[0] <= e.X && e.X <= r[1] && r[2] <= e.Y && e.Y <= r[3] && r[4] <= e.Z && e.Z <= r[5]) aOut.Add(e);
            }
            return aOut;
        }

        // ═════════════════════════════ export ═════════════════════════════
        static readonly int[][] s_FaceDir =
        {
            new[] { 1, 0, 0 }, new[] { -1, 0, 0 }, new[] { 0, 1, 0 }, new[] { 0, -1, 0 }, new[] { 0, 0, 1 }, new[] { 0, 0, -1 },
        };

        static readonly int[][][] s_FaceCorners =
        {
            new[] { new[] { 1, 0, 0 }, new[] { 1, 1, 0 }, new[] { 1, 1, 1 }, new[] { 1, 0, 1 } },
            new[] { new[] { 0, 0, 1 }, new[] { 0, 1, 1 }, new[] { 0, 1, 0 }, new[] { 0, 0, 0 } },
            new[] { new[] { 1, 1, 0 }, new[] { 0, 1, 0 }, new[] { 0, 1, 1 }, new[] { 1, 1, 1 } },
            new[] { new[] { 0, 0, 0 }, new[] { 1, 0, 0 }, new[] { 1, 0, 1 }, new[] { 0, 0, 1 } },
            new[] { new[] { 0, 0, 1 }, new[] { 1, 0, 1 }, new[] { 1, 1, 1 }, new[] { 0, 1, 1 } },
            new[] { new[] { 0, 1, 0 }, new[] { 1, 1, 0 }, new[] { 1, 0, 0 }, new[] { 0, 0, 0 } },
        };

        /// <summary>
        /// python <c>cmd_export</c>：.obj(+.mtl)／MagicaVoxel .vox。六方向鄰居存在即不出面；obj 是 Y-up（世界 z 寫到 obj y），
        /// 每面用叉積驗外向、不合就反轉頂點序。輸出 bytes 與 python 相同（文字檔用平台換行，與 python 文字模式同）。
        /// </summary>
        public SCP_SculptResult Export(SCP_SculptExportArgs iArgs)
        {
            if (iArgs.Format != "obj" && iArgs.Format != "vox")
                return SCP_SculptResult.Error(2, "--format 只能是 obj 或 vox：" + iArgs.Format);
            SCP_SculptSpace aSpace;
            try { aSpace = LoadSpace(); } catch (SCP_SculptReplayException e) { return ReplayFail(e); }
            List<(int X, int Y, int Z, int Color)> aVox = Filter(aSpace, iArgs.Region, iArgs.ExcludeColor);
            if (aVox.Count == 0) return SCP_SculptResult.Text(1, "⚠ 觀測區域內沒有任何 voxel — 沒東西可匯出");

            string aOutDir = iArgs.OutDir.Length > 0 ? SCP_SculptPy.PathStr(iArgs.OutDir) : Paths.Exports;
            Directory.CreateDirectory(aOutDir);
            string aStamp = Clock().ToString("yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture);
            string aOutPath = iArgs.Out.Length > 0
                ? SCP_SculptPy.PathStr(iArgs.Out)
                : SCP_SculptPy.PathStr(Path.Combine(aOutDir, "sculpt_" + aStamp + "." + iArgs.Format));
            string? aParent = Path.GetDirectoryName(Path.GetFullPath(aOutPath));
            if (!string.IsNullOrEmpty(aParent)) Directory.CreateDirectory(aParent);

            var aRes = new SCP_SculptExportResult { Format = iArgs.Format, VoxelCount = aVox.Count, OutputPath = aOutPath };
            if (iArgs.Format == "obj")
            {
                var aSet = new HashSet<(int, int, int)>();
                var aColors = new SortedSet<int>();
                foreach (var e in aVox) { aSet.Add((e.X, e.Y, e.Z)); aColors.Add(e.Color); }
                string aMtl = SCP_SculptPy.WithSuffix(aOutPath, ".mtl");
                var aM = new StringBuilder();
                foreach (int c in aColors)
                {
                    Rgb332(c, out byte r, out byte g, out byte b);
                    aM.Append("newmtl c").Append(c.ToString(CultureInfo.InvariantCulture)).Append('\n')
                      .Append("Kd ").Append(F4(r / 255.0)).Append(' ').Append(F4(g / 255.0)).Append(' ').Append(F4(b / 255.0)).Append('\n');
                }
                SCP_SculptPy.WriteTextFile(aMtl, aM.ToString());

                var aO = new StringBuilder();
                aO.Append("mtllib ").Append(Path.GetFileName(aMtl)).Append('\n');
                int vi = 1, ni = 1, aFaces = 0;
                var pts = new int[4][];
                foreach (int c in aColors)
                {
                    aO.Append("usemtl c").Append(c.ToString(CultureInfo.InvariantCulture)).Append('\n');
                    foreach (var e in aVox)
                    {
                        if (e.Color != c) continue;
                        for (int f = 0; f < 6; f++)
                        {
                            int[] d = s_FaceDir[f];
                            if (aSet.Contains((e.X + d[0], e.Y + d[1], e.Z + d[2]))) continue;   // 被鄰居遮住的面不出
                            for (int k = 0; k < 4; k++)
                            {
                                int[] o = s_FaceCorners[f][k];
                                pts[k] = new[] { e.X + o[0], e.Z + o[2], e.Y + o[1] };   // OBJ y-up：(wx, wz, wy)
                            }
                            int[] nrm = { d[0], d[2], d[1] };
                            long ux = pts[1][0] - pts[0][0], uy = pts[1][1] - pts[0][1], uz = pts[1][2] - pts[0][2];
                            long vx = pts[2][0] - pts[0][0], vy = pts[2][1] - pts[0][1], vz = pts[2][2] - pts[0][2];
                            long cx = uy * vz - uz * vy, cy = uz * vx - ux * vz, cz = ux * vy - uy * vx;
                            if (cx * nrm[0] + cy * nrm[1] + cz * nrm[2] < 0) Array.Reverse(pts);
                            foreach (int[] p in pts)
                                aO.Append("v ").Append(p[0]).Append(' ').Append(p[1]).Append(' ').Append(p[2]).Append('\n');
                            aO.Append("vn ").Append(nrm[0]).Append(' ').Append(nrm[1]).Append(' ').Append(nrm[2]).Append('\n');
                            aO.Append("f ").Append(vi).Append("//").Append(ni).Append(' ').Append(vi + 1).Append("//").Append(ni)
                              .Append(' ').Append(vi + 2).Append("//").Append(ni).Append(' ').Append(vi + 3).Append("//").Append(ni).Append('\n');
                            vi += 4; ni += 1; aFaces++;
                        }
                    }
                }
                SCP_SculptPy.WriteTextFile(aOutPath, aO.ToString());
                aRes.FaceCount = aFaces;
                aRes.MtlPath = aMtl;
                aRes.Lines.Add("# 📦 OBJ 匯出完成");
                aRes.Lines.Add("  voxels    : " + aVox.Count);
                aRes.Lines.Add("  faces     : " + aFaces + " (culled)");
                aRes.Lines.Add("  obj       : " + aOutPath);
                aRes.Lines.Add("  mtl       : " + aMtl);
                return aRes;
            }

            // vox
            int mnx = int.MaxValue, mny = int.MaxValue, mnz = int.MaxValue, mxx = int.MinValue, mxy = int.MinValue, mxz = int.MinValue;
            foreach (var e in aVox)
            {
                mnx = Math.Min(mnx, e.X); mny = Math.Min(mny, e.Y); mnz = Math.Min(mnz, e.Z);
                mxx = Math.Max(mxx, e.X); mxy = Math.Max(mxy, e.Y); mxz = Math.Max(mxz, e.Z);
            }
            int sx = mxx - mnx + 1, sy = mxy - mny + 1, sz = mxz - mnz + 1;
            aRes.SizeX = sx; aRes.SizeY = sy; aRes.SizeZ = sz;
            if (Math.Max(sx, Math.Max(sy, sz)) > 256)
                return SCP_SculptResult.Text(1, "⚠ region 尺寸 " + sx + "x" + sy + "x" + sz + " 超過 MagicaVoxel 單模型上限 256 — 縮小觀測區域再匯");
            var aXyzi = new byte[aVox.Count * 4];
            for (int i = 0; i < aVox.Count; i++)
            {
                var e = aVox[i];
                int c = e.Color > 0 ? e.Color : 1;
                if (c > 255) return SCP_SculptResult.Error(1, "vox 匯出：顏色 " + c + " 超出 0..255（python 的 struct.pack 同樣會失敗）");
                aXyzi[i * 4] = (byte)(e.X - mnx); aXyzi[i * 4 + 1] = (byte)(e.Y - mny);
                aXyzi[i * 4 + 2] = (byte)(e.Z - mnz); aXyzi[i * 4 + 3] = (byte)c;
            }
            byte[] aSize = Chunk("SIZE", Concat(I32(sx), I32(sy), I32(sz)), Array.Empty<byte>());
            byte[] aXyziC = Chunk("XYZI", Concat(I32(aVox.Count), aXyzi), Array.Empty<byte>());
            var aPal = new byte[256 * 4];
            for (int i = 1; i <= 256; i++)
            {
                Rgb332(i % 256, out byte r, out byte g, out byte b);
                aPal[(i - 1) * 4] = r; aPal[(i - 1) * 4 + 1] = g; aPal[(i - 1) * 4 + 2] = b; aPal[(i - 1) * 4 + 3] = 255;
            }
            byte[] aRgba = Chunk("RGBA", aPal, Array.Empty<byte>());
            byte[] aMain = Chunk("MAIN", Array.Empty<byte>(), Concat(aSize, aXyziC, aRgba));
            File.WriteAllBytes(aOutPath, Concat(Encoding.ASCII.GetBytes("VOX "), I32(150), aMain));
            aRes.Lines.Add("# 📦 VOX 匯出完成 (MagicaVoxel)");
            aRes.Lines.Add("  voxels    : " + aVox.Count + "  size: " + sx + "x" + sy + "x" + sz);
            aRes.Lines.Add("  vox       : " + aOutPath);
            return aRes;
        }

        static string F4(double v) => v.ToString("F4", CultureInfo.InvariantCulture);

        static byte[] I32(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

        static byte[] Chunk(string iId, byte[] iContent, byte[] iChildren)
            => Concat(Encoding.ASCII.GetBytes(iId), I32(iContent.Length), I32(iChildren.Length), iContent, iChildren);

        static byte[] Concat(params byte[][] iParts)
        {
            int n = 0;
            foreach (byte[] p in iParts) n += p.Length;
            var aOut = new byte[n];
            int o = 0;
            foreach (byte[] p in iParts) { Buffer.BlockCopy(p, 0, aOut, o, p.Length); o += p.Length; }
            return aOut;
        }

        // ═════════════════════════════ exhibit ═════════════════════════════
        /// <summary>
        /// python <c>load_exhibits</c>：<c>exhibits/*.json</c>（目錄列舉序 —— 與 python os.scandir 同一個系統呼叫），
        /// key ＝ data.id（假值 ⇒ 檔名 stem）；讀不出來的檔跳過；重複 id ⇒ 後到的值、先到的位置。
        /// </summary>
        public List<KeyValuePair<string, SCP_JsonData>> LoadExhibits()
        {
            Directory.CreateDirectory(Paths.Exhibits);
            var aOut = new List<KeyValuePair<string, SCP_JsonData>>();
            StringComparison aCmp = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            foreach (string f in Directory.EnumerateFiles(Paths.Exhibits))
            {
                if (!f.EndsWith(".json", aCmp)) continue;
                SCP_JsonData? d = SCP_SculptStore.ReadEvent(f);
                if (d == null) continue;
                SCP_JsonData aIdNode = d["id"];
                string aId = SCP_SculptPy.Truthy(aIdNode) ? SCP_SculptPy.Str(aIdNode) : Path.GetFileNameWithoutExtension(f);
                int aAt = aOut.FindIndex(kv => kv.Key == aId);
                if (aAt >= 0) aOut[aAt] = new KeyValuePair<string, SCP_JsonData>(aId, d);
                else aOut.Add(new KeyValuePair<string, SCP_JsonData>(aId, d));
            }
            return aOut;
        }

        string SaveExhibit(string iId, SCP_SculptPyObj iPreset)
        {
            Directory.CreateDirectory(Paths.Exhibits);
            string aFile = Paths.ExhibitJson(iId);
            SCP_SculptPy.WriteTextFile(aFile, SCP_SculptPy.Dumps(iPreset, true));
            return aFile;
        }

        /// <summary>
        /// python <c>exhibit register</c>：寫 preset（欄位與順序同 python）→ 出展品照（走 <see cref="SCP_SculptRenderers.Current"/>）。
        /// <para>⭐ 與 python 不同的一格（刻意）：照片出不來（沒有渲染器／渲染失敗）⇒ preset 照樣登錄、回 warning、exit 0；
        /// python 在那一格會直接 traceback（preset 已寫、照片沒出、exit 1）。</para>
        /// </summary>
        public SCP_SculptResult ExhibitRegister(SCP_SculptExhibitRegisterArgs iArgs)
        {
            LoadExhibits();   // python 的 cmd_exhibit 一進來就讀（＝ 建 exhibits/ 夾）
            var aPreset = new SCP_SculptPyObj()
                .Put("id", iArgs.Id).Put("title", iArgs.Title).Put("author", iArgs.Author)
                .Put("description", iArgs.Desc ?? "").Put("region", iArgs.Region ?? "")
                .Put("exclude_color", iArgs.ExcludeColor ?? "").Put("bg_color", iArgs.BgColor ?? "")
                .Put("skybox", iArgs.Skybox ?? "")
                .Put("light_dir", string.IsNullOrEmpty(iArgs.LightDir) ? "-1,-1,-1" : iArgs.LightDir)
                .Put("ambient", iArgs.Ambient).Put("smooth", iArgs.Smooth).Put("shadow", iArgs.Shadow)
                .Put("zoom", iArgs.Zoom.HasValue ? (object)iArgs.Zoom.Value : null);
            foreach (string k in SCP_SculptCamera.Keys)
            {
                string v = iArgs.Camera.Get(k).Trim();
                if (v.Length > 0) aPreset.Put(k, v);   // 鏡頭 key 只在給了時才寫（沒給 ⇒ 與 python 版 preset 逐字相同）
            }
            aPreset.Put("created_at", SCP_SculptPy.IsoFormat(Clock()));

            var aRes = new SCP_SculptExhibitResult { Id = iArgs.Id };
            aRes.ExhibitFile = SaveExhibit(iArgs.Id, aPreset);
            SCP_SculptSpace aSpace;
            try { aSpace = LoadSpace(); } catch (SCP_SculptReplayException e) { return ReplayFail(e); }
            try { aRes.PhotoPath = RenderExhibitPhoto(aSpace, aPreset); }
            catch (Exception e) { aRes.Warning = e.Message; }
            if (aRes.PhotoPath.Length > 0)
                aRes.Lines.Add("✅ 展品標記與典藏寫真登錄成功！ID: " + iArgs.Id + " | 標題: 《" + iArgs.Title + "》 | 創作者: "
                               + iArgs.Author + " | 照片: " + Path.GetFileName(aRes.PhotoPath));
            else
            {
                aRes.Lines.Add("✅ 展品標記登錄成功（典藏寫真未出）！ID: " + iArgs.Id + " | 標題: 《" + iArgs.Title + "》 | 創作者: " + iArgs.Author);
                aRes.Lines.Add("⚠ 展品照沒有出：" + aRes.Warning);
            }
            return aRes;
        }

        /// <summary>python <c>exhibit list</c>。⚠ 缺 title／author／region 的展品 python 會 KeyError ⇒ 這裡 exit 1 並說是哪一件。</summary>
        public SCP_SculptResult ExhibitList()
        {
            var aRes = new SCP_SculptExhibitResult { Exhibits = LoadExhibits() };
            aRes.Lines.Add("# 🏛️ 3D 雕刻展覽館 展品目錄 (共 " + aRes.Exhibits.Count + " 件):");
            foreach (var kv in aRes.Exhibits)
            {
                SCP_JsonData t = kv.Value["title"], a = kv.Value["author"], r = kv.Value["region"];
                if (!t.Exists || !a.Exists || !r.Exists)
                {
                    aRes.ExitCode = 1;
                    aRes.Lines.Add("❌ 展品 [" + kv.Key + "] 缺 title／author／region 欄（python 版在這裡會 KeyError）");
                    return aRes;
                }
                aRes.Lines.Add("  - [" + kv.Key + "] 《" + SCP_SculptPy.Str(t) + "》 by " + SCP_SculptPy.Str(a)
                               + " | region: " + SCP_SculptPy.Str(r) + " | photo: exhibits/" + kv.Key + ".png");
            }
            return aRes;
        }

        /// <summary>
        /// python <c>render_exhibit_photo</c>：preset 的觀測與打光 → 渲染器 → <c>exhibits/&lt;id&gt;.png</c>。
        /// 失敗（含沒有渲染器）⇒ 丟例外（呼叫端轉 warning）。⛔ 不寫空白圖。
        /// </summary>
        string RenderExhibitPhoto(SCP_SculptSpace iSpace, SCP_SculptPyObj iPreset)
        {
            SCP_JsonData aP = SCP_JsonData.Parse(SCP_SculptPy.Dumps(iPreset, false));
            string aId = SCP_SculptPy.Str(aP["id"]);
            // python 的 render_exhibit_photo 一律從 preset 取值、缺的給預設 ⇒ 每一格都算「有給」
            var aIn = new ViewInputs
            {
                Region = StrOrBad(aP, "region", ""),
                Exclude = StrOrBad(aP, "exclude_color", ""),
                LightDir = StrOrBad(aP, "light_dir", "-1,-1,-1"),
                Ambient = aP.Contains("ambient") ? aP["ambient"] : SCP_JsonData.NewNumber(0.4),
                AmbientGiven = true,
                Shadow = SCP_SculptPy.Truthy(aP["shadow"]),
                ShadowGiven = true,
                Zoom = aP["zoom"],
                ZoomGiven = true,
                SmoothText = aP.Contains("smooth") ? SCP_SculptPy.Str(aP["smooth"]) : "True",
            };
            SCP_SculptViewPlan aPlan = BuildPlan(iSpace, aIn, new SCP_SculptCamera(), aP, PhotoBase != null ? PhotoBase() : PythonDefaults());
            if (aPlan.ExitCode != 0) throw new InvalidOperationException(aPlan.Error);
            // 展品照一律讓主體填滿畫面（自動框住時可以放大超過 24 px／voxel；preset 指定了 zoom 就照 zoom）—— Tim 2026-10-02
            aPlan.Params.FitUpscale = true;
            if (!TryRenderPng(aPlan, out byte[] aPng, out string aWhy)) throw new InvalidOperationException(aWhy);
            Directory.CreateDirectory(Paths.Exhibits);
            string aOut = Paths.ExhibitPng(aId);
            File.WriteAllBytes(aOut, aPng);
            return aOut;
        }

        /// <summary>
        /// 用目前註冊的渲染器畫 <paramref name="iPlan"/> 並編成 PNG。沒有渲染器 ⇒ false ＋ 人話（⛔ 不退成空白圖或舊圖）。
        /// </summary>
        public static bool TryRenderPng(SCP_SculptViewPlan iPlan, out byte[] oPng, out string oError)
        {
            oPng = Array.Empty<byte>();
            ISCP_SculptRenderer? aR = SCP_SculptRenderers.Current;
            if (aR == null)
            {
                oError = "這個宿主沒有註冊 3D 渲染器（SCP_SculptRenderers.Current 是 null）⇒ 出不了圖";
                return false;
            }
            if (!aR.TryRender(iPlan.Voxels, iPlan.Params, out byte[] aRgba, out string aErr))
            {
                oError = "渲染器 " + aR.Name + " 失敗：" + aErr;
                return false;
            }
            if (aRgba == null || aRgba.Length != (long)iPlan.Params.Width * iPlan.Params.Height * 4)
            {
                oError = "渲染器 " + aR.Name + " 回的像素長度不對（" + (aRgba?.Length ?? 0) + "）";
                return false;
            }
            oPng = SCP_CanvasPng.EncodeRgbaRows(aRgba, iPlan.Params.Width, iPlan.Params.Height);
            oError = "";
            return true;
        }

        // ═════════════════════════════ view ═════════════════════════════
        /// <summary>preset／CLI 合併後、交給 <see cref="BuildPlan"/> 的值（null 字串／Given=false ＝ 沒給 ⇒ 保留 base 的值）。</summary>
        sealed class ViewInputs
        {
            public string? Region;
            public string? Exclude;
            /// <summary>null ＝ 沒給。</summary>
            public string? LightDir;
            public SCP_JsonData Ambient = SCP_JsonData.NewNumber(0.4);
            public bool AmbientGiven;
            public bool Shadow;
            public bool ShadowGiven;
            public SCP_JsonData Zoom = SCP_JsonData.NewNull();
            public bool ZoomGiven;
            public string SmoothText = "False";
        }

        /// <summary>非字串的 preset 值在 python 會在 <c>.split</c>／<c>.replace</c> 炸、被 except 吞成預設 ⇒ 用一個必定解析失敗的哨兵表示。</summary>
        const string BadText = "\u0000not-a-string";

        static string StrOrBad(SCP_JsonData iP, string iKey, string iDefault)
        {
            SCP_JsonData a = iP[iKey];
            if (!a.Exists) return iDefault;
            return a.IsString ? a.AsString() : BadText;
        }

        /// <summary>
        /// 沒有 base 時的起點 ＝ python 舊引擎的預設：一盞 (-1,-1,-1) 白光、ambient 0.4、**陰影關**、自動縮放。
        /// <para>⚠ 與 <see cref="SCP_SculptRenderParams"/> 的建構預設（新引擎「陰影開」）不同 —— 那是渲染器的預設，
        /// 這裡是「python 版 view 不帶任何旗標時長什麼樣」。要新引擎預設就自己傳 base 進來。</para>
        /// </summary>
        public static SCP_SculptRenderParams PythonDefaults()
        {
            var p = new SCP_SculptRenderParams();
            p.Lights = new List<SCP_SculptLight> { new SCP_SculptLight { DirX = -1, DirY = -1, DirZ = -1, CastShadow = false } };
            p.Ambient = 0.4;
            p.Shadow = false;
            p.Zoom = null;
            return p;
        }

        /// <summary>
        /// python <c>cmd_view</c> 的「場景＋參數」那一半（不畫圖）。合併規則與 python 相同：
        /// region／exclude ＝ CLI 或 preset；light_dir／ambient ＝ **preset 優先**（python 的現況）；
        /// shadow ＝ CLI 開了就開、否則看 preset；zoom ＝ CLI 沒給才吃 preset（preset 的值要為真）。
        /// 鏡頭（projection／yaw／pitch／roll／target／eye／distance／fov）與 skybox ＝ CLI 沒給的那幾格才吃 preset。
        /// <para>⭐ <paramref name="iBase"/>：參數疊在**這一份**上（render profile 當底用）——**會被就地修改**，要保留原件請先複製。
        /// CLI 與 preset 都沒提到的欄位（燈／ambient／shadow／zoom／鏡頭／skybox）一律保留 base 的值。
        /// null ＝ 從 <see cref="PythonDefaults"/> 起算（＝ python 版 view 的預設樣子）。</para>
        /// <para>燈的映射：light_dir（＋shadow）有給 ⇒ <c>Lights</c> 換成**一盞**白光（強度 1、CastShadow ＝ shadow）；
        /// 只給 shadow ⇒ 只動總開關 <c>Shadow</c> 與現有每盞燈的 CastShadow。</para>
        /// <para>⚠ 會 <see cref="LoadSpace"/>（重寫快取）—— 呼叫端握雕刻鎖。輸出路徑由呼叫端決定（⛔ 不提供共用的固定檔名 —— 會被別人的 view 換掉而不報錯）。</para>
        /// </summary>
        public SCP_SculptViewPlan PrepareView(SCP_SculptViewArgs iArgs, SCP_SculptRenderParams? iBase = null)
        {
            var aFail = new SCP_SculptViewPlan();
            SCP_SculptSpace aSpace;
            try { aSpace = LoadSpace(); }
            catch (SCP_SculptReplayException e) { aFail.ExitCode = 1; aFail.Error = e.Message; return aFail; }

            bool aNoBase = iBase == null;
            var aIn = new ViewInputs
            {
                Region = iArgs.Region.Length > 0 ? iArgs.Region : null,
                Exclude = iArgs.ExcludeColor.Length > 0 ? iArgs.ExcludeColor : null,
                // python：`args.light_dir or "-1,-1,-1"` —— 沒有 base 時「沒給」就是預設方向
                LightDir = iArgs.LightDir.Length > 0 ? iArgs.LightDir : (aNoBase ? "-1,-1,-1" : null),
                SmoothText = iArgs.Smooth ? "True" : "False",
                AmbientGiven = aNoBase,
                ShadowGiven = aNoBase,
                ZoomGiven = aNoBase,
            };
            if (iArgs.Ambient.Trim().Length > 0)
            {
                if (!SCP_SculptPy.TryFloat(iArgs.Ambient, out double aAmb))
                { aFail.ExitCode = 2; aFail.Error = "--ambient 不是數字：" + iArgs.Ambient; return aFail; }
                aIn.Ambient = SCP_JsonData.NewNumber(SCP_SculptPy.FloatRepr(aAmb));
                aIn.AmbientGiven = true;
            }
            bool aShadowCli = iArgs.Shadow.Trim().Length > 0;
            if (aShadowCli)
            {
                string s = iArgs.Shadow.Trim().ToLowerInvariant();
                if (s == "1" || s == "true" || s == "yes" || s == "on") aIn.Shadow = true;
                else if (s == "0" || s == "false" || s == "no" || s == "off") aIn.Shadow = false;
                else { aFail.ExitCode = 2; aFail.Error = "shadow 要是 1／0：" + iArgs.Shadow; return aFail; }
                aIn.ShadowGiven = true;
            }
            if (iArgs.Zoom.Trim().Length > 0)
            {
                if (!SCP_SculptPy.TryFloat(iArgs.Zoom, out double aZ))
                { aFail.ExitCode = 2; aFail.Error = "--zoom 不是數字：" + iArgs.Zoom; return aFail; }
                aIn.Zoom = SCP_JsonData.NewNumber(SCP_SculptPy.FloatRepr(aZ));
                aIn.ZoomGiven = true;
            }

            SCP_JsonData? aPreset = null;
            var aLines = new List<string>();
            if (iArgs.Exhibit.Length > 0)
            {
                foreach (var kv in LoadExhibits()) if (kv.Key == iArgs.Exhibit) aPreset = kv.Value;
                if (aPreset != null)
                {
                    if (aIn.Region == null && aPreset.Contains("region") && SCP_SculptPy.Truthy(aPreset["region"]))
                        aIn.Region = aPreset["region"].IsString ? aPreset["region"].AsString() : BadText;
                    if (aIn.Exclude == null && aPreset.Contains("exclude_color") && SCP_SculptPy.Truthy(aPreset["exclude_color"]))
                        aIn.Exclude = aPreset["exclude_color"].IsString ? aPreset["exclude_color"].AsString() : BadText;
                    if (aPreset.Contains("light_dir"))
                        aIn.LightDir = aPreset["light_dir"].IsString ? aPreset["light_dir"].AsString() : BadText;
                    if (aPreset.Contains("ambient")) { aIn.Ambient = aPreset["ambient"]; aIn.AmbientGiven = true; }
                    if (aPreset.Contains("smooth")) aIn.SmoothText = SCP_SculptPy.Str(aPreset["smooth"]);
                    // python：`if "shadow" in preset and not shadow_mode` —— CLI 開了就不看 preset；
                    // ⚠ CLI 顯式關（shadow=0）是 C# 這側多的一格（python 的旗標表達不了「關」），一樣不看 preset
                    if (!aShadowCli && aPreset.Contains("shadow"))
                    {
                        // python 的 shadow_mode 起點是 False：沒給 CLI 時 `not shadow_mode` 為真 ⇒ 吃 preset
                        aIn.Shadow = SCP_SculptPy.Truthy(aPreset["shadow"]);
                        aIn.ShadowGiven = true;
                    }
                    if (SCP_SculptPy.Truthy(aPreset["zoom"]) && !SCP_SculptPy.Truthy(aIn.Zoom))
                    { aIn.Zoom = aPreset["zoom"]; aIn.ZoomGiven = true; }
                    SCP_JsonData t = aPreset["title"], a = aPreset["author"];
                    if (!t.Exists || !a.Exists)
                    { aFail.ExitCode = 1; aFail.Error = "展品 [" + iArgs.Exhibit + "] 缺 title／author 欄（python 版在這裡會 KeyError）"; return aFail; }
                    aLines.Add("🏛️ 正在一鍵載入展品 [" + iArgs.Exhibit + "] 《" + SCP_SculptPy.Str(t)
                               + "》 觀測與打光 Preset (創作者: " + SCP_SculptPy.Str(a) + ")...");
                }
            }
            SCP_SculptViewPlan aPlan = BuildPlan(aSpace, aIn, iArgs.Camera, aPreset, iBase ?? PythonDefaults());
            aPlan.Lines.InsertRange(0, aLines);
            return aPlan;
        }

        /// <summary>合併好的輸入 → 過濾＋深度排序＋疊到 <paramref name="iBase"/> 上。</summary>
        SCP_SculptViewPlan BuildPlan(SCP_SculptSpace iSpace, ViewInputs iIn, SCP_SculptCamera iCli, SCP_JsonData? iPreset,
                                     SCP_SculptRenderParams iBase)
        {
            var aPlan = new SCP_SculptViewPlan { TotalVoxels = iSpace.Voxels.Count, SmoothText = iIn.SmoothText, Params = iBase };
            List<(int X, int Y, int Z, int Color)> aVis = Filter(iSpace, iIn.Region, iIn.Exclude);
            // python：visible_points.sort(key=depth) —— 穩定排序（List.Sort 不穩定 ⇒ 帶原序號當第二鍵）
            var aIdx = new List<(long Depth, int Ord, (int X, int Y, int Z, int Color) V)>(aVis.Count);
            for (int i = 0; i < aVis.Count; i++)
                aIdx.Add(((long)aVis[i].Z * 10000 + aVis[i].X + aVis[i].Y, i, aVis[i]));
            aIdx.Sort((a, b) => a.Depth != b.Depth ? a.Depth.CompareTo(b.Depth) : a.Ord.CompareTo(b.Ord));
            foreach (var e in aIdx)
            {
                int c = e.V.Color;
                if (c < 1 || c > 255) { c = 4; aPlan.OutOfRangeColors++; }   // 見 OutOfRangeColors
                aPlan.Voxels.Add(new SCP_SculptVoxel(e.V.X, e.V.Y, e.V.Z, (byte)c));
            }
            bool aAny = aPlan.Voxels.Count > 0;
            SCP_SculptRenderParams p = iBase;

            // shadow 先定（燈的 CastShadow 要跟著它）
            if (iIn.ShadowGiven) p.Shadow = iIn.Shadow;

            // light_dir：解析失敗 ⇒ (-1,-1,-1)（python 的 except）；分量數不是 3 ⇒ python 在畫第一顆時炸 ⇒ 有可見 voxel 才算錯
            if (iIn.LightDir != null)
            {
                double[] aLight = { -1, -1, -1 };
                var aParsed = new List<double>();
                bool aLightOk = true;
                foreach (string s in iIn.LightDir.Split(','))
                {
                    if (!SCP_SculptPy.TryFloat(s, out double d)) { aLightOk = false; break; }
                    aParsed.Add(d);
                }
                if (aLightOk)
                {
                    if (aParsed.Count != 3)
                    {
                        if (aAny) { aPlan.ExitCode = 1; aPlan.Error = "light_dir 需要 3 個分量 x,y,z：" + iIn.LightDir; return aPlan; }
                    }
                    else aLight = aParsed.ToArray();
                }
                p.Lights = new List<SCP_SculptLight>
                {
                    new SCP_SculptLight { DirX = aLight[0], DirY = aLight[1], DirZ = aLight[2], Intensity = 1, CastShadow = p.Shadow },
                };
            }
            else if (iIn.ShadowGiven)
            {
                foreach (SCP_SculptLight l in p.Lights) l.CastShadow = p.Shadow;
            }

            if (iIn.AmbientGiven)
            {
                if (!TryNumber(iIn.Ambient, out double aAmbient))
                {
                    if (aAny) { aPlan.ExitCode = 1; aPlan.Error = "ambient 不是數字：" + SCP_SculptPy.Str(iIn.Ambient); return aPlan; }
                    aAmbient = 0.4;
                }
                p.Ambient = aAmbient;
            }

            // zoom：為真且 > 0 ⇒ 指定倍率；否則自動框住（null）
            if (iIn.ZoomGiven)
            {
                p.Zoom = null;
                if (SCP_SculptPy.Truthy(iIn.Zoom))
                {
                    if (!TryNumber(iIn.Zoom, out double aZoom))
                    {
                        if (aAny) { aPlan.ExitCode = 1; aPlan.Error = "zoom 不是數字：" + SCP_SculptPy.Str(iIn.Zoom); return aPlan; }
                    }
                    else if (aZoom > 0) p.Zoom = aZoom;
                }
            }

            // skybox：python 存了但從沒用過；preset 有非空字串才映射（相對路徑先找雕刻根、再找 skyboxes/）
            if (iPreset != null && iPreset["skybox"].IsString && iPreset["skybox"].AsString().Trim().Length > 0)
                p.Skybox = ResolveSkybox(iPreset["skybox"].AsString().Trim());

            string? aCamErr = ApplyCamera(p, iCli, iPreset);
            if (aCamErr != null) { aPlan.ExitCode = 2; aPlan.Error = aCamErr; }
            return aPlan;
        }

        string ResolveSkybox(string iSkybox)
        {
            if (iSkybox == SCP_SculptRenderParams.SkyboxNone) return iSkybox;
            if (Path.IsPathRooted(iSkybox)) return SCP_SculptPy.FullPathStr(iSkybox);
            string a = Path.Combine(Paths.Root, iSkybox);
            if (File.Exists(a)) return SCP_SculptPy.FullPathStr(a);
            string b = Path.Combine(Paths.Root, "skyboxes", iSkybox);
            if (File.Exists(b)) return SCP_SculptPy.FullPathStr(b);
            return SCP_SculptPy.FullPathStr(a);   // 找不到也照給 —— 渲染器讀不了會大聲失敗（⛔ 不默默退回預設天空）
        }

        static bool TryNumber(SCP_JsonData iNode, out double oValue)
        {
            oValue = 0;
            if (iNode.Type == SCP_JsonType.Bool) { oValue = iNode.AsBool() ? 1 : 0; return true; }   // python：True + 0.6 ＝ 1.6
            if (iNode.Type != SCP_JsonType.Number) return false;
            return double.TryParse(iNode.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out oValue);
        }

        /// <summary>鏡頭欄位：CLI 給了用 CLI、否則 preset 有就用 preset、都沒有 ⇒ 不動（渲染器預設＝舊引擎框法）。</summary>
        static string? ApplyCamera(SCP_SculptRenderParams p, SCP_SculptCamera iCli, SCP_JsonData? iPreset)
        {
            foreach (string k in SCP_SculptCamera.Keys)
            {
                string aText = iCli.Get(k).Trim();
                SCP_JsonData? aNode = null;
                if (aText.Length == 0 && iPreset != null && iPreset.Contains(k) && !iPreset[k].IsNull)
                {
                    aNode = iPreset[k];
                    if (aNode.IsString) { aText = aNode.AsString().Trim(); aNode = null; }
                    else if (aNode.IsArray)
                    {
                        var aParts = new List<string>();
                        foreach (SCP_JsonData e in aNode) aParts.Add(e.Type == SCP_JsonType.Number ? e.AsString() : "?");
                        aText = string.Join(",", aParts);
                        aNode = null;
                    }
                    else if (aNode.Type == SCP_JsonType.Number) { aText = aNode.AsString(); aNode = null; }
                    else return "展品 preset 的 " + k + " 型別不對：" + SCP_SculptPy.Str(aNode);
                }
                if (aText.Length == 0) continue;
                switch (k)
                {
                    case "projection":
                    {
                        string a = aText.ToLowerInvariant();
                        if (a == "orthographic" || a == "ortho") p.Projection = SCP_SculptProjection.Orthographic;
                        else if (a == "perspective" || a == "persp") p.Projection = SCP_SculptProjection.Perspective;
                        else return "projection 只能是 orthographic／perspective：" + aText;
                        break;
                    }
                    case "target":
                    case "eye":
                    {
                        string[] parts = aText.Split(',');
                        var v = new double[3];
                        if (parts.Length != 3 || !SCP_SculptPy.TryFloat(parts[0], out v[0]) || !SCP_SculptPy.TryFloat(parts[1], out v[1])
                            || !SCP_SculptPy.TryFloat(parts[2], out v[2]))
                            return k + " 要是 x,y,z 三個數字（⛔ 不猜缺的那一軸）：" + aText;
                        if (k == "target") { p.TargetX = v[0]; p.TargetY = v[1]; p.TargetZ = v[2]; }
                        else { p.EyeX = v[0]; p.EyeY = v[1]; p.EyeZ = v[2]; }
                        break;
                    }
                    default:
                    {
                        if (!SCP_SculptPy.TryFloat(aText, out double d) || double.IsNaN(d) || double.IsInfinity(d))
                            return k + " 不是數字：" + aText;
                        if (k == "yaw") p.YawDeg = d;
                        else if (k == "pitch") p.PitchDeg = d;
                        else if (k == "roll") p.RollDeg = d;
                        else if (k == "distance") { if (d <= 0) return "distance 要 > 0：" + aText; p.Distance = d; }
                        else if (k == "fov") { if (d <= 0 || d >= 180) return "fov 要在 (0,180) 度：" + aText; p.FovDeg = d; }
                        break;
                    }
                }
            }
            return null;
        }

        static SCP_SculptResult ReplayFail(SCP_SculptReplayException e) => SCP_SculptResult.Error(1, e.Message);
    }
}
