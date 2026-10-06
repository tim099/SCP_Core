// 區塊職責：把另一張畫布**平移後併進**本畫布（TASK-0444，Tim 2026-10-06：左右並排 4096×2048，Bar 座標為主、LY 平移到擴增的那一半）。
// 物理意義：事件是 append-only 的事實源 ⇒ 合併 ＝ 把來源的每一個事件**平移後另存一份**到本畫布的 events/，
//           ⛔ 不改任何既有事件、不改來源。claims／notes 跟著平移；券／自由時間／鎖**不搬**（那是金流與 session 狀態，屬於各自的區）。
//           ⭐ 可重跑：目標檔已存在且內容相同 ⇒ 跳過。來源之後新增的事件，重跑一次就只補新的。
// 數值影響：Plan 純讀；Apply 只寫 events/<日>/<原檔名>_<tag>.json、claims.json、notes/<p>.json。
// 🩸 為什麼另存新檔名：Bar 與 LY 有共同祖先（2026-10-06 實測 35 個檔名相同、逐位元組相同）——
//    沿用原檔名會讓平移版蓋掉（或被誤判成）Bar 自己那一份。
// ⚠ 三道寫前閘（任一不過 ⇒ Plan 帶 Problems，Apply 拒絕、零寫入）：
//    ① 目標畫布的**設定尺寸**蓋得住平移後的範圍（⛔ 不靠「實際尺寸被已畫範圍撐大」—— 那會讓設定檔說謊）
//    ② 平移後的格子不落在目標畫布**原本**已畫的格子上（不蓋別人的畫；同 tag 先前合併進來的不算）
//    ③ 目標檔名已存在但內容不同 ⇒ 不覆寫（那是另一份歷史）
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。JSON 一律走 SCP_Json。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Canvas
{
    public sealed class SCP_CanvasMergePlan
    {
        public string Tag = "";
        public int Dx, Dy;
        public int SourceEvents, ToWrite, AlreadyMerged, ShiftedPixels;
        public SCP_CanvasSize SourceExtent, ShiftedExtent, TargetConfigured;
        public int OverlapCells;
        public int ClaimsAdded, ClaimsSkipped, NotesAdded, NotesSkipped;
        public readonly List<string> RenamedTitles = new List<string>();
        public readonly List<string> Problems = new List<string>();

        internal readonly List<KeyValuePair<string, string>> EventWrites = new List<KeyValuePair<string, string>>();
        internal SCP_JsonData? NewClaims;
        internal readonly List<KeyValuePair<string, SCP_JsonData>> NoteWrites = new List<KeyValuePair<string, SCP_JsonData>>();

        public bool Ok => Problems.Count == 0;
    }

    public static class SCP_CanvasMerge
    {
        public const string KeyMergedFrom = "merged_from";
        public const string KeyMergedSrc = "merged_src";

        /// <summary>試算（純讀）。<paramref name="iTag"/> 進檔名與 merged_from（例：LY）。</summary>
        public static SCP_CanvasMergePlan Plan(SCP_CanvasPaths iSrc, SCP_CanvasPaths iDst, int iDx, int iDy, string iTag)
        {
            var p = new SCP_CanvasMergePlan { Tag = iTag, Dx = iDx, Dy = iDy };
            string aSuffix = "_" + iTag.ToLowerInvariant();
            if (iDx < 0 || iDy < 0) { p.Problems.Add("dx／dy 不可為負（平移到擴增的那一半只會往正方向）"); return p; }
            if (!Directory.Exists(iSrc.Events)) { p.Problems.Add("來源沒有 events/：" + iSrc.Events); return p; }

            // ── 事件：平移、另存 ──
            List<SCP_CanvasEventFile> aFiles = SCP_CanvasEvents.ScanManifest(iSrc);
            p.SourceEvents = aFiles.Count;
            var aSrcEvents = new List<SCP_JsonData>();
            foreach (SCP_CanvasEventFile f in aFiles)
            {
                string aSrcPath = iSrc.Events + "/" + f.Rel;
                SCP_JsonData aEv;
                try { aEv = SCP_JsonParser.Parse(File.ReadAllText(aSrcPath, Encoding.UTF8)); }
                catch (Exception e) { p.Problems.Add("來源事件讀不了：" + f.Rel + "（" + e.Message + "）"); continue; }
                aSrcEvents.Add(aEv);
                SCP_JsonData aPixels = aEv["pixels"];
                if (aPixels.Exists)
                    for (int i = 0; i < aPixels.Count; i++)
                    {
                        SCP_JsonData aPx = aPixels[i];
                        if (!SCP_CanvasEvents.TryCoord(aPx, out int x, out int y)) continue;
                        aPx["x"] = x + iDx;
                        aPx["y"] = y + iDy;
                        p.ShiftedPixels++;
                    }
                aEv[KeyMergedFrom] = iTag;
                aEv[KeyMergedSrc] = f.Rel;
                string aJson = SCP_JsonWriter.Write(aEv, true) + "\n";
                string aDstRel = f.Rel.Substring(0, f.Rel.Length - ".json".Length) + aSuffix + ".json";
                string aDstPath = iDst.Events + "/" + aDstRel;
                if (File.Exists(aDstPath))
                {
                    if (NormalizeNl(File.ReadAllText(aDstPath, Encoding.UTF8)) == NormalizeNl(aJson)) p.AlreadyMerged++;
                    else p.Problems.Add("目標事件檔已存在且內容不同（不覆寫）：" + aDstRel);
                    continue;
                }
                p.EventWrites.Add(new KeyValuePair<string, string>(aDstPath, aJson));
            }
            p.ToWrite = p.EventWrites.Count;

            // ── 閘①：尺寸 ──
            p.SourceExtent = SCP_CanvasSettings.Extent(ReadRaw(iSrc, aFiles));
            p.ShiftedExtent = p.SourceExtent.Area == 0 ? new SCP_CanvasSize(0, 0)
                : new SCP_CanvasSize(p.SourceExtent.Width + iDx, p.SourceExtent.Height + iDy);
            p.TargetConfigured = SCP_CanvasSettings.ReadConfigured(iDst, out _, out _);
            if (!p.TargetConfigured.Covers(p.ShiftedExtent))
                p.Problems.Add("目標畫布設定 " + p.TargetConfigured + " 蓋不住平移後的範圍 " + p.ShiftedExtent
                               + " ⇒ 先 `canvas op=size` 擴大（⛔ 不靠已畫範圍把實際尺寸撐大 —— 那會讓設定檔說謊）");

            // ── 閘②：不蓋目標畫布原本已畫的格子（同 tag 先前合併進來的不算）──
            List<SCP_JsonData> aDstOwn = new List<SCP_JsonData>();
            foreach (SCP_JsonData aEv in SCP_CanvasEvents.ReadAllEvents(iDst))
                if (aEv.GetString(KeyMergedFrom, "") != iTag) aDstOwn.Add(aEv);
            SCP_CanvasSize aDstExt = SCP_CanvasSettings.Extent(aDstOwn);
            if (aDstExt.Area > 0)
            {
                var aOwnMask = new byte[aDstExt.Area];
                var aOwnBuf = new byte[aDstExt.Area];
                SCP_CanvasEvents.Apply(aOwnBuf, aOwnMask, aDstOwn, aDstExt);
                foreach (SCP_JsonData aEv in aSrcEvents)
                {
                    SCP_JsonData aPixels = aEv["pixels"];
                    if (!aPixels.Exists) continue;
                    for (int i = 0; i < aPixels.Count; i++)
                    {
                        if (!SCP_CanvasEvents.TryCoord(aPixels[i], out int x, out int y)) continue;
                        if (aDstExt.InBounds(x, y) && aOwnMask[y * aDstExt.Width + x] != 0) p.OverlapCells++;
                    }
                }
                if (p.OverlapCells > 0)
                    p.Problems.Add("平移後有 " + p.OverlapCells + " 次落在目標畫布原本已畫的格子上 ⇒ 不合併（不蓋別人的畫；換 dx／dy）");
            }

            PlanClaims(iSrc, iDst, p);
            PlanNotes(iSrc, iDst, p);
            return p;
        }

        /// <summary>照 Plan 寫入。Plan 有問題 ⇒ 不寫、回 false。</summary>
        public static bool Apply(SCP_CanvasPaths iDst, SCP_CanvasMergePlan iPlan, out string oWhy)
        {
            oWhy = "";
            if (!iPlan.Ok) { oWhy = string.Join("；", iPlan.Problems); return false; }
            foreach (KeyValuePair<string, string> w in iPlan.EventWrites)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(w.Key)!);
                File.WriteAllText(w.Key, w.Value, new UTF8Encoding(false));
            }
            if (iPlan.NewClaims != null) WriteJson(iDst.Claims, iPlan.NewClaims);
            foreach (KeyValuePair<string, SCP_JsonData> n in iPlan.NoteWrites) WriteJson(n.Key, n.Value);
            return true;
        }

        /// <summary>
        /// 對拍：來源畫布自己重播出來的每一格，在目標畫布 (x+dx, y+dy) 的顏色與「畫過」都一致。回不一致的格數。
        /// （來源用 no-cache 重播 ⇒ ⛔ 不在來源目錄寫快取。）
        /// </summary>
        public static int VerifyShifted(SCP_CanvasPaths iSrc, SCP_CanvasPaths iDst, int iDx, int iDy, out int oChecked, out List<string> oSamples)
        {
            oChecked = 0;
            oSamples = new List<string>();
            SCP_CanvasSnapshot aS = SCP_CanvasBuffer.Build(iSrc, false);
            SCP_CanvasSnapshot aD = SCP_CanvasBuffer.Build(iDst);
            int aBad = 0;
            for (int y = 0; y < aS.Height; y++)
                for (int x = 0; x < aS.Width; x++)
                {
                    int s = y * aS.Width + x;
                    if (aS.Mask[s] == 0) continue;
                    oChecked++;
                    int tx = x + iDx, ty = y + iDy;
                    bool aOk = aD.Size.InBounds(tx, ty)
                               && aD.Mask[ty * aD.Width + tx] != 0 && aD.Buffer[ty * aD.Width + tx] == aS.Buffer[s];
                    if (aOk) continue;
                    aBad++;
                    if (oSamples.Count < 5) oSamples.Add("(" + x + "," + y + ")→(" + tx + "," + ty + ")");
                }
            // 反方向（Tim：「LY 畫面顯示的結果完全放在另一半邊」）：來源沒畫過的格子，平移後在目標也必須是空的
            for (int y = 0; y < aS.Height; y++)
                for (int x = 0; x < aS.Width; x++)
                {
                    if (aS.Mask[y * aS.Width + x] != 0) continue;
                    int tx = x + iDx, ty = y + iDy;
                    if (!aD.Size.InBounds(tx, ty) || aD.Mask[ty * aD.Width + tx] == 0) continue;
                    aBad++;
                    if (oSamples.Count < 5) oSamples.Add("目標多畫了 (" + tx + "," + ty + ")");
                }
            return aBad;
        }

        // ── claims ──
        static void PlanClaims(SCP_CanvasPaths iSrc, SCP_CanvasPaths iDst, SCP_CanvasMergePlan p)
        {
            if (!File.Exists(iSrc.Claims)) return;
            SCP_JsonData aSrc, aDst;
            try { aSrc = SCP_JsonParser.Parse(File.ReadAllText(iSrc.Claims, Encoding.UTF8)); }
            catch (Exception e) { p.Problems.Add("來源 claims.json 讀不了：" + e.Message); return; }
            try { aDst = File.Exists(iDst.Claims) ? SCP_JsonParser.Parse(File.ReadAllText(iDst.Claims, Encoding.UTF8)) : NewArrayDoc("claims"); }
            catch (Exception e) { p.Problems.Add("目標 claims.json 讀不了（不覆寫）：" + e.Message); return; }
            if (!aSrc["claims"].IsArray) return;
            if (!aDst["claims"].IsArray) { p.Problems.Add("目標 claims.json 沒有 claims 陣列（不覆寫）"); return; }

            var aIds = new HashSet<string>(StringComparer.Ordinal);
            var aOwnTitles = new HashSet<string>(StringComparer.Ordinal);   // 目標原本的標題（同 tag 合併進來的不算）
            SCP_JsonData aDstArr = aDst["claims"];
            for (int i = 0; i < aDstArr.Count; i++)
            {
                aIds.Add(aDstArr[i].GetString("id", ""));
                if (aDstArr[i].GetString(KeyMergedFrom, "") != p.Tag) aOwnTitles.Add(aDstArr[i].GetString("title", "").Trim());
            }
            string aIdSuffix = "-" + p.Tag.ToLowerInvariant();
            SCP_JsonData aSrcArr = aSrc["claims"];
            for (int i = 0; i < aSrcArr.Count; i++)
            {
                SCP_JsonData c = aSrcArr[i];
                string aNewId = c.GetString("id", "?") + aIdSuffix;
                if (aIds.Contains(aNewId)) { p.ClaimsSkipped++; continue; }
                ShiftRegion(c["region"], p.Dx, p.Dy);
                string aTitle = c.GetString("title", "").Trim();
                // 展品 id ＝ 標題 ⇒ 撞到目標原本的標題就會併成一件跨半邊的展品 ⇒ 加上來源標記
                if (aTitle.Length > 0 && aOwnTitles.Contains(aTitle))
                {
                    string aNew = aTitle + "（" + p.Tag + "）";
                    c["title"] = aNew;
                    p.RenamedTitles.Add(aTitle + " → " + aNew);
                }
                c["id"] = aNewId;
                c[KeyMergedFrom] = p.Tag;
                aDstArr.Add(c);
                aIds.Add(aNewId);
                p.ClaimsAdded++;
            }
            if (p.ClaimsAdded > 0) p.NewClaims = aDst;
        }

        // ── notes ──
        static void PlanNotes(SCP_CanvasPaths iSrc, SCP_CanvasPaths iDst, SCP_CanvasMergePlan p)
        {
            if (!Directory.Exists(iSrc.Notes)) return;
            string aIdSuffix = "-" + p.Tag.ToLowerInvariant();
            var aFiles = new List<string>(Directory.GetFiles(iSrc.Notes, "*.json"));
            aFiles.Sort(StringComparer.Ordinal);
            foreach (string f in aFiles)
            {
                string aPersona = Path.GetFileNameWithoutExtension(f);
                SCP_JsonData aSrc, aDst;
                string aDstPath = iDst.NoteFile(aPersona);
                try { aSrc = SCP_JsonParser.Parse(File.ReadAllText(f, Encoding.UTF8)); }
                catch (Exception e) { p.Problems.Add("來源 notes/" + aPersona + ".json 讀不了：" + e.Message); continue; }
                try
                {
                    if (File.Exists(aDstPath)) aDst = SCP_JsonParser.Parse(File.ReadAllText(aDstPath, Encoding.UTF8));
                    else { aDst = NewArrayDoc("notes"); aDst["persona"] = aPersona; }
                }
                catch (Exception e) { p.Problems.Add("目標 notes/" + aPersona + ".json 讀不了（不覆寫）：" + e.Message); continue; }
                if (!aSrc["notes"].IsArray || !aDst["notes"].IsArray) continue;
                var aIds = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < aDst["notes"].Count; i++) aIds.Add(aDst["notes"][i].GetString("id", ""));
                int aAdded = 0;
                for (int i = 0; i < aSrc["notes"].Count; i++)
                {
                    SCP_JsonData n = aSrc["notes"][i];
                    string aNewId = n.GetString("id", "?") + aIdSuffix;
                    if (aIds.Contains(aNewId)) { p.NotesSkipped++; continue; }
                    ShiftRegion(n["target_region"], p.Dx, p.Dy);
                    n["id"] = aNewId;
                    n[KeyMergedFrom] = p.Tag;
                    aDst["notes"].Add(n);
                    aIds.Add(aNewId);
                    aAdded++;
                }
                if (aAdded == 0) continue;
                p.NotesAdded += aAdded;
                p.NoteWrites.Add(new KeyValuePair<string, SCP_JsonData>(aDstPath, aDst));
            }
        }

        static void ShiftRegion(SCP_JsonData iRegion, int iDx, int iDy)
        {
            if (!iRegion.Exists || !iRegion.Contains("x") || !iRegion.Contains("y")) return;
            iRegion["x"] = iRegion.GetInt("x", 0) + iDx;
            iRegion["y"] = iRegion.GetInt("y", 0) + iDy;
        }

        static List<SCP_JsonData> ReadRaw(SCP_CanvasPaths iSrc, List<SCP_CanvasEventFile> iFiles)
        {
            var aRels = new List<string>();
            foreach (SCP_CanvasEventFile f in iFiles) aRels.Add(f.Rel);
            return SCP_CanvasEvents.ReadEvents(iSrc, aRels);
        }

        static SCP_JsonData NewArrayDoc(string iKey)
        {
            SCP_JsonData d = SCP_JsonData.NewObject();
            d[iKey] = SCP_JsonData.NewArray();
            return d;
        }

        static string NormalizeNl(string s) => s.Replace("\r\n", "\n");

        static void WriteJson(string iPath, SCP_JsonData iData)
        {
            string? aDir = Path.GetDirectoryName(iPath);
            if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, SCP_JsonWriter.Write(iData, true), new UTF8Encoding(false));
            if (File.Exists(iPath)) File.Delete(iPath);
            File.Move(aTmp, iPath);
        }
    }
}
