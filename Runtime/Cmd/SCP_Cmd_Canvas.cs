// 區塊職責：`cmd canvas` —— 共用像素畫布的**讀取端**（view / pixel / stats / cache / snapshot
//           / note / claim）。**原生**，這幾個 op 一顆都不派給別人，Editor 沒開也跑得完。
// 物理意義：畫布事實源是 events/ 底下的 append-only json；本 Cmd 只 replay 與渲染，
//           唯一會寫的是**衍生物**（快取／預覽 PNG／快照）與 per-persona 的 notes／claims。
// 數值影響：⛔ 本 Cmd **不放點、不動錢** —— place 與三付款（限時券／永久券／token）在 TASK-0114 ③，
//           那一路需要委派（CLI/Server 走 AgentCmdClient 派給 Editor，Editor 內直呼 ledger）。
//           所以這裡看不到任何 ledger 型別，那是刻意的邊界不是待辦。
// 設計取捨：資料根**由呼叫端給**（--arg data_root），與 `cmd tasks` 同形。
//           🩸 為什麼不在這裡推導（TASK-0112，2026-09-03）：python 那側儲存根原本相對 cwd，
//           shell 停在別的目錄時工具會在那裡長出第二棵 AgentCommands 樹 —— 寫進去、回讀出來全綠，
//           而真畫布 0 筆、錢照扣。⇒ 回讀與寫入共用同一個錯的根時，綠不是證據。
//           ⚠ 與 python 的一處顯式差異：讀取端**不寫 `_meta.json`**（python 每個 op 都 ensure_meta）。
//           理由是少一個寫入端 —— 唯讀查詢不該產生副作用；palette 是常數，不需要落檔才成立。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SCP.Core.Canvas;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Canvas : SCP_Cmd
    {
        public override string Name => "canvas";

        public override string Summary => "共用像素畫布唯讀端：看圖／查點／統計／快取／快照／筆記／宣稱區域";

        public override string Details =>
            "2048×2048 全社群共用畫布，事實源是 `<資料根>/Canvas/events/` 的 append-only 事件。\n"
            + "⛔ **放點（place）不在這支**：它要動錢與自由時間額度，走委派那條路（TASK-0114 ③）。\n"
            + "⚠ index 255 同時是「純白」與「沒人畫過」—— 透明變體的判定靠 painted-mask，不看顏色。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("canvas --arg data_root=D:/Unity/Bar/AgentCommands"
                                   + " --arg op=view --arg region=1000,1000,32,32 --arg scale=4");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("op", "要做什麼", iRequired: true,
                iChoices: new[] { "view", "pixel", "stats", "cache", "snapshot", "note", "claim", "gateway", "place" }),
            new SCP_CmdArgSpec("region", "x,y,w,h（view/note/claim 用）"),
            new SCP_CmdArgSpec("scale", "view 放大倍率（整數，預設 1；一律最近鄰）", iDefault: "1"),
            new SCP_CmdArgSpec("x", "pixel 的 x"),
            new SCP_CmdArgSpec("y", "pixel 的 y"),
            new SCP_CmdArgSpec("no_cache", "1 ＝ 強制全 replay（對拍驗證用）"),
            new SCP_CmdArgSpec("sub", "子動作：cache=status|rebuild|verify；note=add|list|done；claim=add|list|done"),
            new SCP_CmdArgSpec("persona", "誰（note 必填；claim add 必填）"),
            new SCP_CmdArgSpec("title", "note/claim 的標題"),
            new SCP_CmdArgSpec("plan", "note 的計畫內文"),
            new SCP_CmdArgSpec("size", "note 的預估尺寸 WxH（est_cost = W*H）"),
            new SCP_CmdArgSpec("id", "note/claim 的 id（done 用）"),
            new SCP_CmdArgSpec("account", "帳號 id（gateway 查餘額／place 付 token 用；⛔ 不由 persona 猜）"),
            new SCP_CmdArgSpec("color", "place 單點的顏色（0-255 或 #RRGGBB）"),
            new SCP_CmdArgSpec("pixels", "place 批量：JSON 陣列 [{\"x\":1,\"y\":2,\"color\":5},…]"),
            new SCP_CmdArgSpec("pay", "付款方式", iDefault: "auto",
                iChoices: new[] { "auto", "freetime", "voucher", "token" }),
            new SCP_CmdArgSpec("allow_white", "1 ＝ 允許畫 index 255（＝與「沒人畫過」同色，預設擋）"),
            new SCP_CmdArgSpec("no_share", "1 ＝ 放完不發酒館"),
            new SCP_CmdArgSpec("agent", "記在事件檔的 agent 欄（預設沿用 persona）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root");
            if (!Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(2, "✗ 資料根不存在：" + aDataRoot,
                                          "  （這是「根給錯了」不是「畫布是空的」—— 兩者不同形）");

            var aPaths = new SCP_CanvasPaths(new SCP_DataRoot(aDataRoot));
            string aOp = iArgs.Get("op");
            switch (aOp)
            {
                case "view": return OpView(iArgs, aPaths);
                case "pixel": return OpPixel(iArgs, aPaths);
                case "stats": return OpStats(iArgs, aPaths);
                case "cache": return OpCache(iArgs, aPaths);
                case "snapshot": return OpSnapshot(aPaths);
                case "note": return OpNote(iArgs, aPaths);
                case "claim": return OpClaim(iArgs, aPaths);
                case "gateway": return OpGateway(iArgs, aDataRoot);
                case "place": return OpPlace(iArgs, aPaths, aDataRoot);
                default: return SCP_CmdResult.Fail(2, "✗ 不認得的 op：" + aOp);
            }
        }

        // ───────────────────────────── view ─────────────────────────────
        // 數值影響：non_transparent_pixels 是**裁切與放大之後**數的（描述檔案，不描述意圖）；
        //          sha256_t 讓下游能證明「我吃的就是你看的那張」。
        static SCP_CmdResult OpView(SCP_CmdArgs iArgs, SCP_CanvasPaths iPaths)
        {
            if (!TryRegion(iArgs.Get("region"), out int aX, out int aY, out int aW, out int aH,
                           out string aWhy))
                return SCP_CmdResult.Fail(2, "✗ " + aWhy);
            if (!TryScale(iArgs.Get("scale"), out int aScale, out string aScaleWhy))
                return SCP_CmdResult.Fail(2, "✗ " + aScaleWhy);

            SCP_CanvasSnapshot aSnap = SCP_CanvasBuffer.Build(iPaths, !Truthy(iArgs.Get("no_cache")));
            byte[] aRgb = SCP_CanvasPng.EncodeRgb(aSnap.Buffer, aX, aY, aW, aH,
                                                  SCP_CanvasSpec.Width, aScale);
            byte[] aRgba = SCP_CanvasPng.EncodeRgba(aSnap.Buffer, aSnap.Mask, aX, aY, aW, aH,
                                                    SCP_CanvasSpec.Width, aScale, out int aOpaque);
            Directory.CreateDirectory(iPaths.Root);
            File.WriteAllBytes(iPaths.LastViewPng, aRgb);
            File.WriteAllBytes(iPaths.LastViewTransparentPng, aRgba);

            var aResult = new SCP_CmdResult();
            aResult.Lines.Add("# 🖼 view rendered");
            aResult.Lines.Add("  size  : " + (aW * aScale) + "x" + (aH * aScale)
                              + (aScale > 1 ? "（原 " + aW + "x" + aH + " ×" + aScale + "）" : ""));
            aResult.Lines.Add("  path  : " + iPaths.LastViewPng);
            aResult.Lines.Add("  path_t: " + iPaths.LastViewTransparentPng + "（RGBA 透明變體 — 3D stamp 的輸入）");
            aResult.Lines.Add("  non_transparent_pixels: " + aOpaque + " / " + (aW * aScale * aH * aScale));
            aResult.Lines.Add("  sha256_t: " + Sha256File(iPaths.LastViewTransparentPng));
            aResult.Lines.Add("  快取路徑: " + CachePathText(aSnap));
            aResult.AddOutput(iPaths.LastViewPng);
            aResult.AddOutput(iPaths.LastViewTransparentPng);
            aResult.AddValue("non_transparent_pixels", aOpaque.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("width", (aW * aScale).ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("height", (aH * aScale).ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("cache_path", aSnap.Path.ToString());
            return aResult;
        }

        // ───────────────────────────── pixel ─────────────────────────────
        static SCP_CmdResult OpPixel(SCP_CmdArgs iArgs, SCP_CanvasPaths iPaths)
        {
            if (!int.TryParse(iArgs.Get("x"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aX)
                || !int.TryParse(iArgs.Get("y"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aY)
                || !SCP_CanvasSpec.InBounds(aX, aY))
                return SCP_CmdResult.Fail(2, "✗ pixel 座標越界或不是整數 [0," + (SCP_CanvasSpec.Width - 1)
                                             + "]：(" + iArgs.Get("x") + "," + iArgs.Get("y") + ")");

            // 逐事件掃該座標的歷史 —— 收據是 history 不是顏色：
            // 「現在是這個色」答不了「這格有沒有人動過」，而後者才是要不要覆蓋的判準。
            var aHistory = new List<string>();
            int aCur = SCP_CanvasSpec.BlankIndex;
            int aTouches = 0;
            foreach (SCP_JsonData aEv in SCP_CanvasEvents.ReadAllEvents(iPaths))
            {
                SCP_JsonData aPixels = aEv["pixels"];
                if (!aPixels.Exists) continue;
                for (int i = 0; i < aPixels.Count; i++)
                {
                    SCP_JsonData aPx = aPixels[i];
                    if (aPx.GetInt("x", int.MinValue) != aX || aPx.GetInt("y", int.MinValue) != aY) continue;
                    if (!SCP_CanvasEvents.TryColor(aPx["color"], out int aIdx)) continue;
                    aCur = aIdx;
                    aTouches++;
                    aHistory.Add("    " + aEv.GetString("ts", "?") + "  "
                                 + aEv.GetString("persona", "?") + "/" + aEv.GetString("agent", "?")
                                 + "  index " + aIdx + " = " + SCP_CanvasPalette.IndexToHex(aIdx));
                }
            }

            var aResult = new SCP_CmdResult();
            aResult.Lines.Add("# 🔍 pixel (" + aX + "," + aY + ")");
            if (aCur == SCP_CanvasSpec.BlankIndex && aTouches == 0)
                aResult.Lines.Add("  current: 空白（index " + SCP_CanvasSpec.BlankIndex + " = #FFFFFF，沒有人畫過）");
            else
            {
                aResult.Lines.Add("  current: index " + aCur + " = " + SCP_CanvasPalette.IndexToHex(aCur));
                if (aCur == SCP_CanvasSpec.BlankIndex)
                    // 🩸 這一行是白色陷阱的現場：有人付了錢畫上去，而它跟「空白」同色。
                    aResult.Lines.Add("  ⚠ 這格被畫成 index 255 —— 顏色與空白同形，只有 history 分得出來");
            }
            aResult.Lines.Add("  history（" + aTouches + " 筆）:");
            aResult.Lines.AddRange(aHistory);
            aResult.AddValue("color_index", aCur.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("history_count", aTouches.ToString(CultureInfo.InvariantCulture));
            return aResult;
        }

        // ───────────────────────────── stats ─────────────────────────────
        static SCP_CmdResult OpStats(SCP_CmdArgs iArgs, SCP_CanvasPaths iPaths)
        {
            int aEvents = 0, aPixelWrites = 0;
            var aPerPersona = new Dictionary<string, int>(StringComparer.Ordinal);
            var aContributors = new HashSet<string>(StringComparer.Ordinal);
            var aOccupied = new HashSet<long>();

            foreach (SCP_JsonData aEv in SCP_CanvasEvents.ReadAllEvents(iPaths))
            {
                aEvents++;
                string aPersona = aEv.GetString("persona", "?");
                aContributors.Add(aPersona);
                SCP_JsonData aPixels = aEv["pixels"];
                if (!aPixels.Exists) continue;
                for (int i = 0; i < aPixels.Count; i++)
                {
                    SCP_JsonData aPx = aPixels[i];
                    aPixelWrites++;
                    aPerPersona.TryGetValue(aPersona, out int aN);
                    aPerPersona[aPersona] = aN + 1;
                    long aKey = (long)aPx.GetInt("x", -1) * 100000L + aPx.GetInt("y", -1);
                    aOccupied.Add(aKey);
                }
            }

            double aRate = aOccupied.Count * 100.0 / SCP_CanvasSpec.Area;
            var aResult = new SCP_CmdResult();
            aResult.Lines.Add("# 📊 canvas stats");
            aResult.Lines.Add("  總事件   : " + aEvents);
            aResult.Lines.Add("  總放點   : " + aPixelWrites + "（含覆蓋）");
            aResult.Lines.Add("  唯一座標 : " + aOccupied.Count + "（去重後實際填充）");
            aResult.Lines.Add("  填充率   : " + SCP_CanvasBuffer.Percent(aRate) + "% ("
                              + aOccupied.Count + "/" + SCP_CanvasSpec.Area + ")");
            aResult.Lines.Add("  貢獻者   : " + aContributors.Count + " 位");
            aResult.Lines.Add("  各 persona 放點數:");
            var aRows = new List<KeyValuePair<string, int>>(aPerPersona);
            aRows.Sort((a, b) => b.Value != a.Value ? b.Value.CompareTo(a.Value)
                                                    : StringComparer.Ordinal.Compare(a.Key, b.Key));
            foreach (KeyValuePair<string, int> aRow in aRows)
                aResult.Lines.Add("    " + aRow.Key.PadRight(20) + " " + aRow.Value);
            aResult.AddValue("event_count", aEvents.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("pixel_writes", aPixelWrites.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("unique_pixels", aOccupied.Count.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("contributors", aContributors.Count.ToString(CultureInfo.InvariantCulture));
            return aResult;
        }

        // ───────────────────────────── cache ─────────────────────────────
        // ⚠ verify 是**唯一有資格說「快取是對的」**的那條路：快取 vs 全 replay 逐格對拍。
        //   「用得起來」不是「算得對」—— 兩者在畫面上同形。
        static SCP_CmdResult OpCache(SCP_CmdArgs iArgs, SCP_CanvasPaths iPaths)
        {
            string aSub = iArgs.Get("sub");
            if (aSub.Length == 0) aSub = "status";
            var aResult = new SCP_CmdResult();

            if (aSub == "status")
            {
                SCP_CanvasSnapshot aSnap = SCP_CanvasBuffer.Build(iPaths);
                aResult.Lines.Add("# 🗃 cache status");
                aResult.Lines.Add("  本次走的路徑: " + CachePathText(aSnap));
                aResult.Lines.Add("  事件檔數    : " + aSnap.EventFiles);
                aResult.Lines.Add("  本次 replay : " + aSnap.ReplayedEvents + " 筆事件");
                aResult.Lines.Add("  快取檔      : " + iPaths.CacheBin
                                  + (File.Exists(iPaths.CacheBin) ? "（存在）" : "（不存在）"));
                aResult.AddValue("cache_path", aSnap.Path.ToString());
                aResult.AddValue("event_files", aSnap.EventFiles.ToString(CultureInfo.InvariantCulture));
                aResult.AddValue("replayed_events", aSnap.ReplayedEvents.ToString(CultureInfo.InvariantCulture));
                return aResult;
            }

            if (aSub == "rebuild")
            {
                SCP_CanvasSnapshot aSnap = SCP_CanvasBuffer.Build(iPaths, false);
                SCP_CanvasBuffer.SaveCache(iPaths, aSnap.Buffer, aSnap.Mask,
                                           SCP_CanvasEvents.ScanManifest(iPaths),
                                           SCP_CanvasEvents.MaxTs(SCP_CanvasEvents.ReadAllEvents(iPaths)));
                aResult.Lines.Add("# 🗃 cache rebuilt（全 replay " + aSnap.ReplayedEvents + " 筆事件）");
                aResult.Lines.Add("  painted: " + CountNonZero(aSnap.Mask));
                aResult.AddValue("replayed_events", aSnap.ReplayedEvents.ToString(CultureInfo.InvariantCulture));
                aResult.AddValue("painted_pixels", CountNonZero(aSnap.Mask).ToString(CultureInfo.InvariantCulture));
                return aResult;
            }

            if (aSub == "verify")
            {
                SCP_CanvasSnapshot aCached = SCP_CanvasBuffer.Build(iPaths);
                SCP_CanvasSnapshot aFresh = SCP_CanvasBuffer.Build(iPaths, false);
                int aBufDiff = CountDiff(aCached.Buffer, aFresh.Buffer);
                int aMaskDiff = CountDiff(aCached.Mask, aFresh.Mask);
                aResult.Lines.Add("# 🔬 cache verify（快取 vs 全 replay，逐格對拍）");
                aResult.Lines.Add("  快取這次走: " + CachePathText(aCached));
                aResult.Lines.Add("  buffer 差異: " + aBufDiff + " 格");
                aResult.Lines.Add("  mask   差異: " + aMaskDiff + " 格");
                aResult.Lines.Add("  sha256(buffer) 快取: " + Sha256(aCached.Buffer));
                aResult.Lines.Add("  sha256(buffer) 重放: " + Sha256(aFresh.Buffer));
                aResult.Lines.Add("  sha256(mask)   快取: " + Sha256(aCached.Mask));
                aResult.Lines.Add("  sha256(mask)   重放: " + Sha256(aFresh.Mask));
                aResult.AddValue("buffer_diff", aBufDiff.ToString(CultureInfo.InvariantCulture));
                aResult.AddValue("mask_diff", aMaskDiff.ToString(CultureInfo.InvariantCulture));
                aResult.AddValue("sha256_buffer", Sha256(aFresh.Buffer));
                aResult.AddValue("sha256_mask", Sha256(aFresh.Mask));
                aResult.AddValue("painted_pixels", CountNonZero(aFresh.Mask).ToString(CultureInfo.InvariantCulture));
                if (aBufDiff != 0 || aMaskDiff != 0)
                {
                    aResult.Lines.Add("✗ 快取與全 replay 不一致 —— 以全 replay 為準，快取該被丟棄");
                    aResult.ExitCode = 1;
                }
                else aResult.Lines.Add("✅ 逐格一致");
                return aResult;
            }

            return SCP_CmdResult.Fail(2, "✗ cache 的 sub 只有 status｜rebuild｜verify：" + aSub);
        }

        // ───────────────────────────── snapshot ─────────────────────────────
        static SCP_CmdResult OpSnapshot(SCP_CanvasPaths iPaths)
        {
            SCP_CanvasSnapshot aSnap = SCP_CanvasBuffer.Build(iPaths);
            string aTag = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string aPath = iPaths.Snapshots + "/canvas_" + aTag + ".png";
            Directory.CreateDirectory(iPaths.Snapshots);
            File.WriteAllBytes(aPath, SCP_CanvasPng.EncodeRgb(aSnap.Buffer, 0, 0,
                SCP_CanvasSpec.Width, SCP_CanvasSpec.Height, SCP_CanvasSpec.Width));
            // 同步重渲 latest 兩軌（不透明給預覽、透明給 3D 轉繪）
            File.WriteAllBytes(iPaths.LatestPng, SCP_CanvasPng.EncodeRgb(aSnap.Buffer, 0, 0,
                SCP_CanvasSpec.Width, SCP_CanvasSpec.Height, SCP_CanvasSpec.Width));
            File.WriteAllBytes(iPaths.LatestTransparentPng, SCP_CanvasPng.EncodeRgba(aSnap.Buffer,
                aSnap.Mask, 0, 0, SCP_CanvasSpec.Width, SCP_CanvasSpec.Height,
                SCP_CanvasSpec.Width, 1, out int aOpaque));

            var aResult = new SCP_CmdResult();
            aResult.Lines.Add("# 📸 snapshot");
            aResult.Lines.Add("  path   : " + aPath);
            aResult.Lines.Add("  latest : " + iPaths.LatestPng + " ＋ " + iPaths.LatestTransparentPng);
            aResult.Lines.Add("  painted: " + aOpaque + " 格（＝透明變體的不透明像素）");
            aResult.AddOutput(aPath);
            aResult.AddValue("painted_pixels", aOpaque.ToString(CultureInfo.InvariantCulture));
            return aResult;
        }

        // ───────────────────────────── note ─────────────────────────────
        static SCP_CmdResult OpNote(SCP_CmdArgs iArgs, SCP_CanvasPaths iPaths)
        {
            string aPersona = iArgs.Get("persona");
            if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ note 需要 --arg persona=<誰>");
            string aSub = iArgs.Get("sub");
            if (aSub.Length == 0) aSub = "list";

            string aFile = iPaths.NoteFile(aPersona);
            SCP_JsonData aData = LoadOrNew(aFile, "notes", aPersona);
            SCP_JsonData aNotes = aData["notes"];
            var aResult = new SCP_CmdResult();

            if (aSub == "add")
            {
                SCP_JsonData aNote = SCP_JsonData.NewObject();
                string aId = ShortId();
                aNote["id"] = aId;
                aNote["title"] = iArgs.Get("title");
                aNote["plan"] = iArgs.Get("plan");
                string aSize = iArgs.Get("size");
                if (aSize.Length > 0)
                {
                    if (!TrySize(aSize, out int aSw, out int aSh))
                        return SCP_CmdResult.Fail(2, "✗ size 格式需 WxH：" + aSize);
                    aNote["expected_size"] = aSize;
                    aNote["est_cost"] = aSw * aSh;      // 預算試算：W×H 個像素 ＝ W*H 顆券／token
                }
                string aRegion = iArgs.Get("region");
                if (aRegion.Length > 0)
                {
                    if (!TryRegionRaw(aRegion, out int aRx, out int aRy, out int aRw, out int aRh))
                        return SCP_CmdResult.Fail(2, "✗ region 格式需 x,y,w,h：" + aRegion);
                    SCP_JsonData aR = SCP_JsonData.NewObject();
                    aR["x"] = aRx; aR["y"] = aRy; aR["w"] = aRw; aR["h"] = aRh;
                    aNote["target_region"] = aR;
                }
                aNote["status"] = "planning";
                string aNow = SCP_CanvasEvents.IsoMs(DateTime.UtcNow);
                aNote["created_at"] = aNow;
                aNote["updated_at"] = aNow;
                aNotes.Add(aNote);
                WriteJson(aFile, aData);
                aResult.Lines.Add("# 📝 note added [" + aId + "] " + iArgs.Get("title"));
                aResult.AddValue("note_id", aId);
                aResult.AddOutput(aFile);
                return aResult;
            }

            if (aSub == "done")
            {
                string aId = iArgs.Get("id");
                if (aId.Length == 0) return SCP_CmdResult.Fail(2, "✗ note done 需要 --arg id=<note id>");
                SCP_JsonData? aFound = FindById(aNotes, aId);
                if (aFound == null) return SCP_CmdResult.Fail(1, "✗ 找不到 note id：" + aId);
                aFound["status"] = "done";
                aFound["updated_at"] = SCP_CanvasEvents.IsoMs(DateTime.UtcNow);
                WriteJson(aFile, aData);
                aResult.Lines.Add("# 📝 note [" + aId + "] → status=done");
                aResult.AddOutput(aFile);
                return aResult;
            }

            if (aSub == "list")
            {
                aResult.Lines.Add("# 📝 " + aPersona + " 繪圖筆記（" + aNotes.Count + " 筆）:");
                for (int i = 0; i < aNotes.Count; i++)
                {
                    SCP_JsonData aN = aNotes[i];
                    aResult.Lines.Add("  [" + aN.GetString("id", "?") + "] (" + aN.GetString("status", "?")
                                      + ") " + aN.GetString("title", "") + "  est_cost="
                                      + (aN["est_cost"].Exists ? aN.GetLong("est_cost", 0).ToString(CultureInfo.InvariantCulture) : "-"));
                    string aPlan = aN.GetString("plan", "");
                    if (aPlan.Length > 0) aResult.Lines.Add("        plan: " + aPlan);
                }
                aResult.AddValue("note_count", aNotes.Count.ToString(CultureInfo.InvariantCulture));
                return aResult;
            }

            return SCP_CmdResult.Fail(2, "✗ note 的 sub 只有 add｜list｜done：" + aSub);
        }

        // ───────────────────────────── claim ─────────────────────────────
        // 物理意義：宣稱是**軟性禮讓**，不是鎖 —— 系統不擋別人畫進你宣稱的區域。
        static SCP_CmdResult OpClaim(SCP_CmdArgs iArgs, SCP_CanvasPaths iPaths)
        {
            string aSub = iArgs.Get("sub");
            if (aSub.Length == 0) aSub = "list";
            SCP_JsonData aData = LoadOrNew(iPaths.Claims, "claims", null);
            SCP_JsonData aClaims = aData["claims"];
            var aResult = new SCP_CmdResult();

            if (aSub == "add")
            {
                string aPersona = iArgs.Get("persona");
                if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, "✗ claim add 需要 --arg persona=<宣稱者>");
                if (!TryRegionRaw(iArgs.Get("region"), out int aX, out int aY, out int aW, out int aH))
                    return SCP_CmdResult.Fail(2, "✗ claim add 需要 --arg region=x,y,w,h");
                string aId = ShortId();
                SCP_JsonData aClaim = SCP_JsonData.NewObject();
                aClaim["id"] = aId;
                aClaim["persona"] = aPersona;
                SCP_JsonData aR = SCP_JsonData.NewObject();
                aR["x"] = aX; aR["y"] = aY; aR["w"] = aW; aR["h"] = aH;
                aClaim["region"] = aR;
                aClaim["title"] = iArgs.Get("title");
                aClaim["status"] = "active";
                string aNow = SCP_CanvasEvents.IsoMs(DateTime.UtcNow);
                aClaim["created_at"] = aNow;
                aClaim["updated_at"] = aNow;
                aClaims.Add(aClaim);
                WriteJson(iPaths.Claims, aData);
                aResult.Lines.Add("# 📌 claim added [" + aId + "] " + iArgs.Get("title")
                                  + " @ (" + aX + "," + aY + "," + aW + "," + aH + ") by " + aPersona);
                aResult.AddValue("claim_id", aId);
                aResult.AddOutput(iPaths.Claims);
                return aResult;
            }

            if (aSub == "done")
            {
                string aId = iArgs.Get("id");
                if (aId.Length == 0) return SCP_CmdResult.Fail(2, "✗ claim done 需要 --arg id=<claim id>");
                SCP_JsonData? aFound = FindById(aClaims, aId);
                if (aFound == null) return SCP_CmdResult.Fail(1, "✗ 找不到 claim id：" + aId);
                aFound["status"] = "done";
                aFound["updated_at"] = SCP_CanvasEvents.IsoMs(DateTime.UtcNow);
                WriteJson(iPaths.Claims, aData);
                aResult.Lines.Add("# 📌 claim [" + aId + "] → status=done");
                aResult.AddOutput(iPaths.Claims);
                return aResult;
            }

            if (aSub == "list")
            {
                int aActive = 0;
                for (int i = 0; i < aClaims.Count; i++)
                    if (aClaims[i].GetString("status", "") == "active") aActive++;
                aResult.Lines.Add("# 📌 宣稱區域（active " + aActive + " / 共 " + aClaims.Count + "）:");
                for (int i = 0; i < aClaims.Count; i++)
                {
                    SCP_JsonData aC = aClaims[i];
                    SCP_JsonData aR = aC["region"];
                    aResult.Lines.Add("  [" + aC.GetString("id", "?") + "] (" + aC.GetString("status", "?")
                                      + ") " + aC.GetString("persona", "?") + ": " + aC.GetString("title", "")
                                      + " @ (" + aR.GetInt("x", -1) + "," + aR.GetInt("y", -1) + ","
                                      + aR.GetInt("w", -1) + "," + aR.GetInt("h", -1) + ")");
                }
                aResult.AddValue("claim_total", aClaims.Count.ToString(CultureInfo.InvariantCulture));
                aResult.AddValue("claim_active", aActive.ToString(CultureInfo.InvariantCulture));
                return aResult;
            }

            return SCP_CmdResult.Fail(2, "✗ claim 的 sub 只有 add｜list｜done：" + aSub);
        }

        // ───────────────────────────── place（③：唯一會動錢的 op）─────────────────────────────
        // 區塊職責：放點 —— 驗證 → 鎖 → 付款 → 寫事件 → 重渲 → **回讀** → 分享。
        // 物理意義：順序不可換，**先收錢再畫**：畫了卻沒扣到錢等於免費像素，比拒絕嚴重得多。
        //           付款任一步失敗 ⇒ 整批放棄，**不寫任何事件**（沿途已扣的那幾筆會留在帳上，
        //           那是真實付款的痕跡，不是我可以偷偷抹掉的東西 —— 抹掉才是造假）。
        // 數值影響：pay=auto 優先序 限時券 → 永久券 → token；合計不足整批拒絕（exit 3）。
        // 🩸 回讀那一步不是禮貌，是憲法：wake#86 我放了十顆、工具印 placed 10、回讀十顆顏色全對、
        //    ledger 真扣 10 token —— 而真畫布上那十顆不存在（cwd 停在別的目錄，長出第二棵樹）。
        //    ⇒ 這裡的回讀刻意**從事件檔重放**，而事件檔路徑印在輸出裡：
        //      讀的人可以自己去看那個檔在哪一棵樹上。
        static SCP_CmdResult OpPlace(SCP_CmdArgs iArgs, SCP_CanvasPaths iPaths, string iDataRoot)
        {
            string aPersona = iArgs.Get("persona");
            if (aPersona.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ place 需要 --arg persona=<誰>（錢要記在人頭上）");

            if (!SCP_CanvasPlace.TryParsePixels(iArgs.Get("pixels"), iArgs.Get("x"), iArgs.Get("y"),
                                                iArgs.Get("color"), out List<SCP_CanvasPixel> aPixels,
                                                out string aParseWhy))
                return SCP_CmdResult.Fail(2, "✗ place 拒絕：" + aParseWhy);

            // 白色陷阱守衛（python 那側沒有這道）
            if (SCP_CanvasPlace.HasBlankWhite(aPixels, out int aWhiteCount) && !Truthy(iArgs.Get("allow_white")))
                return SCP_CmdResult.Fail(2,
                    "✗ place 拒絕：有 " + aWhiteCount + " 顆的顏色量化到 index 255，"
                    + "而 255 同時是「純白」與「沒有人畫過」",
                    "  ⇒ 畫上去的後果是：扣了款、事件落盤、回讀卻是空白，三邊都不出聲",
                    "  （這不是假想風險 —— 本畫布現有 66 格就是這樣來的）",
                    "  · 想要亮色：用暖色高明度（#FFDA00 → index 248 活得下來），別用接近白的灰",
                    "  · 真的要「擦掉」：顯式帶 --arg allow_white=1，那時它是刻意行為不是意外");

            SCP_ICanvasGateway? aGate = SCP_CanvasGatewayHost.For(iDataRoot);
            if (aGate == null)
                return SCP_CmdResult.Fail(1, "✗ 這個宿主沒有裝上畫布閘 ⇒ 付不了錢，所以不放點",
                                          "  ⛔ 不會「先畫再說」——那等於免費像素。");

            string aAccount = iArgs.Get("account");
            string aPay = iArgs.Get("pay");
            if (aPay.Length == 0) aPay = "auto";
            int aN = aPixels.Count;
            var aResult = new SCP_CmdResult();
            aResult.Lines.Add("  " + aGate.HostQualifier);

            // ── 臨界區：讀餘額 → 扣款 必須序列化（與 python 同一把鎖檔）──
            string aLockBank = aAccount.Length > 0 ? aAccount : "noaccount";
            IDisposable? aLock = SCP_CanvasPlace.TryAcquireLock(iPaths, aLockBank, aPersona, out string aLockWhy);
            if (aLock == null)
                return SCP_CmdResult.Fail(4, "✗ place 拒絕（拿不到付款鎖）：" + aLockWhy,
                                          "  ⛔ 不強奪：對方可能還在扣款中，強奪就是 double-spend。");

            var aLedgerRefs = new List<string>();
            string aUuid = SCP_CanvasPlace.NewUuid();
            SCP_CanvasPayPlan aPlan;
            try
            {
                if (!SCP_CanvasPlace.TryPlan(aGate, aPersona, aAccount, aN, aPay, out aPlan, out string aPlanWhy))
                    return SCP_CmdResult.Fail(3, "✗ place 拒絕（付款）：" + aPlanWhy);

                if (aPlan.Token > 0)
                {
                    SCP_CanvasGateResult aR = aGate.DebitTokens(aAccount, aPlan.Token, "canvas_pixel",
                        aUuid, "canvas " + aPlan.Token + " px by " + aPersona + " (event " + aUuid + ")");
                    if (!aR.Ok)
                        return SCP_CmdResult.Fail(3, "✗ place 拒絕（扣 token 失敗，未畫任何像素）：" + aR.Detail);
                    aLedgerRefs.Add("treasury:" + aUuid);
                }
                // ⚠ 限時券與永久券走**同一支 consume**（ledger 內部先花快過期的）——
                //   分兩筆呼叫只為了讓帳面分得出「這幾張是限時券」與「這幾張是存量」。
                if (aPlan.Expiring > 0)
                {
                    SCP_CanvasGateResult aR = aGate.ConsumeVouchers(aPersona, aPlan.Expiring, aUuid,
                        "canvas " + aPlan.Expiring + " px (限時券) by " + aPersona);
                    if (!aR.Ok)
                        return SCP_CmdResult.Fail(3, "✗ place 拒絕（扣限時券失敗，未畫任何像素）：" + aR.Detail);
                    aLedgerRefs.Add("voucher-expiring:" + aUuid);
                }
                if (aPlan.Permanent > 0)
                {
                    SCP_CanvasGateResult aR = aGate.ConsumeVouchers(aPersona, aPlan.Permanent, aUuid,
                        "canvas " + aPlan.Permanent + " px by " + aPersona);
                    if (!aR.Ok)
                        return SCP_CmdResult.Fail(3, "✗ place 拒絕（扣永久券失敗，未畫任何像素）：" + aR.Detail);
                    aLedgerRefs.Add("voucher:" + aUuid);
                }
            }
            finally { aLock.Dispose(); }

            // ── 錢收完了才寫事件 ──
            DateTime aNow = DateTime.UtcNow;
            string aAgent = iArgs.Get("agent");
            if (aAgent.Length == 0) aAgent = aPersona;
            string aEventPath = SCP_CanvasPlace.WriteEvent(iPaths, aNow, aUuid, aPersona, aAgent,
                aAccount, aPixels, aPlan, aLedgerRefs);

            // 重渲兩軌（增量快取會把剛落的這筆當「最新 ts 的新檔」走路②）
            SCP_CanvasSnapshot aSnap = SCP_CanvasBuffer.Build(iPaths);
            File.WriteAllBytes(iPaths.LatestPng, SCP_CanvasPng.EncodeRgb(aSnap.Buffer, 0, 0,
                SCP_CanvasSpec.Width, SCP_CanvasSpec.Height, SCP_CanvasSpec.Width));
            File.WriteAllBytes(iPaths.LatestTransparentPng, SCP_CanvasPng.EncodeRgba(aSnap.Buffer,
                aSnap.Mask, 0, 0, SCP_CanvasSpec.Width, SCP_CanvasSpec.Height,
                SCP_CanvasSpec.Width, 1, out int aOpaque));

            // ── 回讀：逐顆比對 buffer 與 mask（憲法⑥ 結果那本帳的憑據）──
            int aVerified = 0;
            var aMismatch = new List<string>();
            foreach (SCP_CanvasPixel aP in aPixels)
            {
                int aPos = aP.Y * SCP_CanvasSpec.Width + aP.X;
                if (aSnap.Buffer[aPos] == aP.ColorIndex && aSnap.Mask[aPos] != 0) aVerified++;
                else aMismatch.Add("(" + aP.X + "," + aP.Y + ") 要 " + aP.ColorIndex
                                   + " 實得 " + aSnap.Buffer[aPos] + " mask=" + aSnap.Mask[aPos]);
            }

            aResult.Lines.Add("# 🎨 placed " + aN + " pixel(s)");
            aResult.Lines.Add("  event        : " + aEventPath);
            aResult.Lines.Add("  persona      : " + aPersona + "（agent=" + aAgent
                              + (aAccount.Length > 0 ? ", account=" + aAccount : ", 未用 token") + "）");
            aResult.Lines.Add("  pay_breakdown: freetime(限時券)=" + aPlan.Expiring
                              + " voucher(永久券)=" + aPlan.Permanent + " token=" + aPlan.Token);
            aResult.Lines.Add("  ledger_refs  : " + (aLedgerRefs.Count > 0 ? string.Join(", ", aLedgerRefs) : "（無 —— 沒有任何一筆錢動過？那是 bug，去看 pay_breakdown）"));
            aResult.Lines.Add("  回讀         : " + aVerified + "/" + aN + " 顆與事件一致（從事件檔重放出來的 buffer 逐顆比）");
            foreach (string aM in aMismatch) aResult.Lines.Add("    ✗ " + aM);
            aResult.Lines.Add("  畫布 painted : " + aOpaque + " 格");
            aResult.Lines.Add("  canvas_latest: " + iPaths.LatestPng);
            aResult.AddOutput(aEventPath);
            aResult.AddValue("placed", aN.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("verified", aVerified.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("event_uuid", aUuid);
            aResult.AddValue("pay_freetime", aPlan.Expiring.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("pay_voucher", aPlan.Permanent.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("pay_token", aPlan.Token.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("painted_pixels", aOpaque.ToString(CultureInfo.InvariantCulture));
            if (aVerified != aN)
            {
                // 錢已經扣了而畫布不對 ⇒ 大聲失敗，但**不假裝沒扣**
                aResult.Lines.Add("✗ 回讀不一致 —— 錢已經扣了（見 ledger_refs），畫布卻不是我以為的樣子");
                aResult.ExitCode = 1;
                return aResult;
            }

            // ── 分享：best-effort，發不出去不讓放點失敗 ──
            if (!Truthy(iArgs.Get("no_share")))
            {
                string aBody = "🎨 " + aPersona + " 在畫布放了 " + aN + " 顆像素"
                    + "（限時券 " + aPlan.Expiring + " ／永久券 " + aPlan.Permanent + " ／token " + aPlan.Token + "）"
                    + "\n· 事件：`" + aUuid + "`　落點回讀 " + aVerified + "/" + aN + " 一致";
                SCP_CanvasGateResult aShare = aGate.Share(aPersona, "tavern", aBody);
                aResult.Lines.Add("  分享         : " + (aShare.Ok ? aShare.Detail : "⚠ " + aShare.Detail));
            }
            return aResult;
        }

        // ───────────────────────────── gateway（②的讀數出口）─────────────────────────────
        // 區塊職責：唯讀探針 —— 問宿主閘三件事（自由時間資格／券／token 餘額），**不動錢**。
        // 物理意義：它存在的理由是「②要怎麼被驗」：閘接對了沒有，不能靠讀 code 回答。
        //           每一行都印出**這個值是怎麼拿到的**（沒有出處的值救不了人）。
        // 數值影響：查詢類問不到一律印「不知道」而不是「沒有」——
        //           🩸 三態塌成兩態是本階段最貴的那種退化：使用者會照著「不在自由時間」
        //           去開一場他其實已經在的場，而沒有任何一層會喊。
        static SCP_CmdResult OpGateway(SCP_CmdArgs iArgs, string iDataRoot)
        {
            // 閘吃的是**本 Cmd 自己的資料根** —— 不讓它去解析第二個根（見 SCP_CanvasGatewayHost 註解）
            SCP_ICanvasGateway? aGate = SCP_CanvasGatewayHost.For(iDataRoot);
            if (aGate == null)
                // fail loud：沒有閘就是沒有閘，⛔ 不假裝查到了（假成功會讓像素落盤而錢沒扣）
                return SCP_CmdResult.Fail(1,
                    "✗ 這個宿主沒有裝上畫布閘（SCP_CanvasGatewayHost.Factory 是 null）",
                    "  ⇒ 付款／自由時間資格／分享都問不到。裝上它是宿主啟動時的事，本層不推導。");

            string aPersona = iArgs.Get("persona");
            string aAccount = iArgs.Get("account");
            var aResult = new SCP_CmdResult();
            aResult.Lines.Add("# 🚪 canvas gateway 探針（唯讀，不動錢）");
            aResult.Lines.Add("  " + aGate.HostQualifier);

            if (aPersona.Length > 0)
            {
                SCP_CanvasTriState aFree = aGate.QueryInFreeTime(aPersona, out string aFreeWhy);
                aResult.Lines.Add("  自由時間: " + TriText(aFree));
                aResult.Lines.Add("      ↳ " + aFreeWhy);
                aResult.AddValue("in_free_time", aFree == SCP_CanvasTriState.Yes ? "1"
                                                : aFree == SCP_CanvasTriState.No ? "0" : "unknown");

                int aExp = aGate.QueryExpiringVouchers(aPersona, out string aExpWhy);
                aResult.Lines.Add("  限時券  : " + (aExp < 0 ? "不知道" : aExp.ToString(CultureInfo.InvariantCulture) + " 張"));
                aResult.Lines.Add("      ↳ " + aExpWhy);
                aResult.AddValue("expiring_vouchers", aExp.ToString(CultureInfo.InvariantCulture));

                int aPerm = aGate.QueryPermanentVouchers(aPersona, out string aPermWhy);
                aResult.Lines.Add("  永久券  : " + (aPerm < 0 ? "不知道" : aPerm.ToString(CultureInfo.InvariantCulture) + " 張"));
                aResult.Lines.Add("      ↳ " + aPermWhy);
                aResult.AddValue("permanent_vouchers", aPerm.ToString(CultureInfo.InvariantCulture));
            }
            else aResult.Lines.Add("  （沒給 persona ⇒ 跳過資格與券；那是「沒問」不是「沒有」）");

            if (aAccount.Length > 0)
            {
                long aBalance = aGate.QueryTokenBalance(aAccount, out string aBalWhy);
                aResult.Lines.Add("  token   : " + (aBalance < 0 ? "不知道（**不是 0**）"
                                                  : aBalance.ToString(CultureInfo.InvariantCulture)));
                aResult.Lines.Add("      ↳ " + aBalWhy);
                aResult.AddValue("token_balance", aBalance.ToString(CultureInfo.InvariantCulture));
            }
            else aResult.Lines.Add("  （沒給 account ⇒ 跳過 token 餘額）");

            return aResult;
        }

        static string TriText(SCP_CanvasTriState iState)
        {
            switch (iState)
            {
                case SCP_CanvasTriState.Yes: return "✅ 在自由時間";
                case SCP_CanvasTriState.No: return "❌ 不在自由時間";
                default: return "⚠ 不知道（問不到）—— 這不是「不在」";
            }
        }

        // ───────────────────────────── 小工具 ─────────────────────────────

        static string CachePathText(SCP_CanvasSnapshot iSnap)
        {
            switch (iSnap.Path)
            {
                case SCP_CanvasCachePath.Hit: return "① 指紋相同，直接用快取（零 replay）";
                case SCP_CanvasCachePath.Incremental:
                    return "② 增量（只 replay 新事件 " + iSnap.ReplayedEvents + " 筆）";
                default:
                    return "③ 全重建（replay " + iSnap.ReplayedEvents + " 筆）—— 原因："
                           + (iSnap.RebuildReason.Length > 0 ? iSnap.RebuildReason : "未記錄");
            }
        }

        /// <summary>region 解析 ＋ 裁到畫布邊界內；不給 region ＝ 整張。</summary>
        static bool TryRegion(string iRegion, out int oX, out int oY, out int oW, out int oH, out string oWhy)
        {
            oX = 0; oY = 0; oW = SCP_CanvasSpec.Width; oH = SCP_CanvasSpec.Height; oWhy = "";
            if (iRegion.Length == 0) return true;
            if (!TryRegionRaw(iRegion, out int aX, out int aY, out int aW, out int aH))
            { oWhy = "region 格式需 x,y,w,h：" + iRegion; return false; }
            if (aW <= 0 || aH <= 0 || !SCP_CanvasSpec.InBounds(aX, aY))
            { oWhy = "region 越界／非法：" + iRegion; return false; }
            oX = aX; oY = aY;
            oW = Math.Min(aX + aW, SCP_CanvasSpec.Width) - aX;
            oH = Math.Min(aY + aH, SCP_CanvasSpec.Height) - aY;
            return true;
        }

        static bool TryRegionRaw(string iRegion, out int oX, out int oY, out int oW, out int oH)
        {
            oX = oY = oW = oH = 0;
            string[] aParts = iRegion.Split(',');
            if (aParts.Length != 4) return false;
            return TryInt(aParts[0], out oX) && TryInt(aParts[1], out oY)
                   && TryInt(aParts[2], out oW) && TryInt(aParts[3], out oH);
        }

        static bool TryScale(string iScale, out int oScale, out string oWhy)
        {
            oScale = 1; oWhy = "";
            if (iScale.Length == 0) return true;
            if (!TryInt(iScale, out int aS) || aS < 1)
            { oWhy = "scale 需 ≥1 的整數：" + iScale; return false; }
            // 上限：放大後的邊長超過 8192 就擋 —— 那種圖沒有人看得動，而它會吃掉幾百 MB
            if (aS > 64) { oWhy = "scale 上限 64（放大是給人看細節的，不是產生巨圖）：" + iScale; return false; }
            oScale = aS;
            return true;
        }

        static bool TrySize(string iSize, out int oW, out int oH)
        {
            oW = oH = 0;
            string[] aParts = iSize.ToLowerInvariant().Split('x');
            return aParts.Length == 2 && TryInt(aParts[0], out oW) && TryInt(aParts[1], out oH);
        }

        static bool TryInt(string iText, out int oValue)
        {
            return int.TryParse(iText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out oValue);
        }

        static bool Truthy(string iText)
        {
            string aS = iText.Trim().ToLowerInvariant();
            return aS == "1" || aS == "true" || aS == "yes";
        }

        static SCP_JsonData LoadOrNew(string iPath, string iArrayKey, string? iPersona)
        {
            try
            {
                if (File.Exists(iPath))
                {
                    SCP_JsonData aData = SCP_JsonParser.Parse(File.ReadAllText(iPath, Encoding.UTF8));
                    if (aData[iArrayKey].Exists) return aData;
                }
            }
            catch (Exception)
            {
                // 讀不出來就當成沒有 —— 但**不覆蓋**：呼叫端只在寫入時才 WriteJson，
                // 而寫入前這個分支只會讓清單看起來是空的。⚠ 這是已知邊界：壞檔會被下一次寫入蓋掉。
            }
            SCP_JsonData aNew = SCP_JsonData.NewObject();
            if (iPersona != null) aNew["persona"] = iPersona;
            aNew[iArrayKey] = SCP_JsonData.NewArray();
            return aNew;
        }

        static void WriteJson(string iPath, SCP_JsonData iData)
        {
            string? aDir = Path.GetDirectoryName(iPath);
            if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
            File.WriteAllText(iPath, SCP_JsonWriter.Write(iData, true), new UTF8Encoding(false));
        }

        static SCP_JsonData? FindById(SCP_JsonData iArray, string iId)
        {
            for (int i = 0; i < iArray.Count; i++)
                if (iArray[i].GetString("id", "") == iId) return iArray[i];
            return null;
        }

        static string ShortId()
        {
            // 6 個 hex ＝ 3 bytes，與 python secrets.token_hex(3) 同形（同一個 id 空間，兩端可互讀）
            var aBytes = new byte[3];
            using (var aRng = RandomNumberGenerator.Create()) aRng.GetBytes(aBytes);
            var aSb = new StringBuilder(6);
            foreach (byte b in aBytes) aSb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return aSb.ToString();
        }

        static int CountNonZero(byte[] iData)
        {
            int aN = 0;
            for (int i = 0; i < iData.Length; i++) if (iData[i] != 0) aN++;
            return aN;
        }

        static int CountDiff(byte[] iA, byte[] iB)
        {
            if (iA.Length != iB.Length) return Math.Max(iA.Length, iB.Length);
            int aN = 0;
            for (int i = 0; i < iA.Length; i++) if (iA[i] != iB[i]) aN++;
            return aN;
        }

        static string Sha256(byte[] iData)
        {
            using var aSha = SHA256.Create();
            return Hex(aSha.ComputeHash(iData));
        }

        static string Sha256File(string iPath)
        {
            using var aSha = SHA256.Create();
            using FileStream aFs = File.OpenRead(iPath);
            return Hex(aSha.ComputeHash(aFs));
        }

        static string Hex(byte[] iHash)
        {
            var aSb = new StringBuilder(iHash.Length * 2);
            foreach (byte b in iHash) aSb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return aSb.ToString();
        }
    }
}
