// 區塊職責：雕刻的外部模型（TASK-0507）—— 作品記「哪裡擺一個三角網格模型、怎麼轉、怎麼縮」，觀測時才讀檔、才畫。
// 物理意義：models.json（作品資料，入版控）⛔ 不是 voxel、不寫 events/ —— 一格 voxel 都不動。兩種來源：
//          ・file：一個 obj 檔，匯入時**複製進作品**（`models/<名字>.obj`＋它的 mtl），之後作品自己帶著它（⛔ 不指向作品外的路徑 —— 外面的檔會被搬走、被改）。
//          ・work：另一件雕刻作品 —— 不匯入 voxel，而是把那件作品**匯出成 obj**（含它的關聯、繩、帆、模型）快取在本地
//            （`<雕刻根>/model_cache/<作品>/<鍵>.obj`，⛔ 不入版控、可隨時重生）；鍵 ＝ 來源那一整串作品的事件水位與資料檔雜湊 ⇒
//            來源一改，鍵就變，下一次觀測自動重出。快取由宿主注入的 <see cref="SCP_SculptModelCacheProducer"/> 產生（匯出要拿那件作品的鎖、
//            要算它的繩與帆 —— 那一段住在宿主的指令層）。
//          座標（三層，由內往外）：
//          ① 檔案座標（raw）：obj 檔自己的 x,y,z。`axes` 決定怎麼換到 ② ——
//             yup（預設，Blender／Maya 的 obj：y 朝上）⇒ (x, −z, y)；zup ⇒ 原樣；sculpt（本引擎 export 出來的 obj）⇒ (x, z, y)（export 的反函數）。
//             work 來源的快取一律是 sculpt ⇒ ② 正好是那件作品的格座標。
//          ② 模型座標（z 朝上）：p_work ＝ at ＋ R·(scale ⊙ mirror(p − pivot))；R ＝ Rz·Ry·Rx（rotate=rx,ry,rz 度，先繞 X、再 Y、再 Z，都繞世界軸）。
//          ③ 作品座標 → 組裝件：照關聯路徑每一層的 mirror／turn／at（同繩：<see cref="SCP_SculptRopes.ThroughLink"/>）。
//          全部是仿射 ⇒ 每個模型一個 3×4 矩陣；行列式 < 0（奇數次鏡像）⇒ 三角形繞序反過來寫，法線用逆轉置。
//          繩／帆角綁模型：端點 `model:[關聯/…/]模型名/點` —— 點 ＝ 檔案座標 `x,y,z`（file 來源）；
//          work 來源 ＝ 那件作品的格座標（整數 ⇒ 格心，同繩的端點）或那件作品的固定點名。模型搬、轉、縮 ⇒ 端點跟著走。
// 數值影響：每個模型一筆 <see cref="SCP_SculptModelReading"/>：ok／hidden（所在那一層這次沒畫，或顯示開關關掉）／disabled（自己關著）／
//          broken（檔不見、讀不了、來源作品不在、成環、快取產生失敗）—— ⛔ 壞掉的不靜默消失。
// 失敗處置：models.json 讀不了 ⇒ InvalidOperationException（⛔ 不當成沒有 —— 寫回去會清光）。obj 讀不了 ⇒ InvalidDataException（含行號）。
// ⚠ 只讀幾何與顏色（v／vn／f／usemtl／mtllib＋Kd）；貼圖、平滑群組、曲面、線段本版不讀（Tim「先只支援 obj」）。
// 方言：C# 9 / netstandard2.1。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Reflect;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptModel
    {
        public string name = "";
        /// <summary>file ｜ work。</summary>
        public string source = SCP_SculptModels.SourceFile;
        /// <summary>file 來源：相對作品資料夾的路徑（`models/<名字>.obj`）。</summary>
        public string file = "";
        /// <summary>file 來源：匯入時的 obj 位元組數與 SHA256（檔被換掉要看得出來）。</summary>
        public long bytes;
        public string sha256 = "";
        /// <summary>work 來源：來源作品 ID。</summary>
        public string work = "";
        /// <summary>檔案座標 → 模型座標：yup｜zup｜sculpt（work 來源固定 sculpt）。</summary>
        public string axes = SCP_SculptModels.AxesYUp;
        /// <summary>模型的 pivot 放在作品的哪裡（格，連續座標）。⚠ List 不用陣列（JSON 對映器讀陣列是「清空再逐項加」）。</summary>
        public List<double> at = new List<double> { 0, 0, 0 };
        /// <summary>旋轉與縮放繞的那一點（模型座標）。</summary>
        public List<double> pivot = new List<double> { 0, 0, 0 };
        /// <summary>度：先繞 X、再 Y、再 Z（世界軸）。</summary>
        public List<double> rotate = new List<double> { 0, 0, 0 };
        public List<double> scale = new List<double> { 1, 1, 1 };
        /// <summary>在模型座標裡翻面（繞 pivot）：x／y／z 或組合；空 ＝ 不翻。</summary>
        public string mirror = "";
        /// <summary>空 ＝ 用 mtl 的 Kd（沒有 ⇒ 預設灰）；給了 ⇒ 整個模型一個色（調色盤 index 或 #RRGGBB，同繩）。</summary>
        public string color = "";
        public bool enabled = true;
        public string note = "", persona = "", created_at = "";
        /// <summary>給下游認部件的 鍵 → 字串或數字（TASK-0517，<see cref="SCP_SculptMeta"/>）。⚠ 對映器不碰它：由 ReadModels／SaveModels 自己讀寫。</summary>
        [SCP_Ignore] public SortedDictionary<string, SCP_JsonData> meta = SCP_SculptMeta.New();
    }

    public sealed class SCP_SculptModelFile
    {
        /// <summary>1；有任何一個模型用到 meta ⇒ 2（<see cref="SCP_SculptModels.SchemaWithMeta"/>）。</summary>
        public int schema = 1;
        public List<SCP_SculptModel> models = new List<SCP_SculptModel>();
    }

    /// <summary>讀進來的 obj（檔案座標）：三角形（多邊形已扇形切好）、每個角的頂點／法線索引、材質。</summary>
    public sealed class SCP_SculptObjMesh
    {
        public double[] Positions = new double[0];
        /// <summary>vn（可能沒有 ⇒ 空）。</summary>
        public double[] Normals = new double[0];
        /// <summary>每個三角形 3 個角：位置索引（0 起）。</summary>
        public int[] CornerPos = new int[0];
        /// <summary>每個角的法線索引（−1 ＝ 沒有 ⇒ 用面法線）。</summary>
        public int[] CornerNrm = new int[0];
        /// <summary>每個三角形的材質索引（−1 ＝ 沒有 usemtl）。</summary>
        public int[] TriMaterial = new int[0];
        public List<string> MaterialNames = new List<string>();
        /// <summary>材質的 Kd（0..255）；mtl 裡沒有那個名字 ⇒ null。</summary>
        public List<byte[]?> MaterialColors = new List<byte[]?>();
        public int TriangleCount => TriMaterial.Length;
        public int VertexCount => Positions.Length / 3;
        public double[] Min = new double[3], Max = new double[3];
        public List<string> Warnings = new List<string>();
    }

    /// <summary>給渲染器／匯出的一個模型（已換到組裝件座標、從外面看逆時針）。</summary>
    public sealed class SCP_SculptModelGeom
    {
        public string Path = "";
        /// <summary>xyz 攤平。</summary>
        public double[] Points = new double[0];
        public double[] Normals = new double[0];
        /// <summary>每頂點 RGB。</summary>
        public byte[] Colors = new byte[0];
        /// <summary>每 3 個一個三角形。</summary>
        public int[] Triangles = new int[0];
        public int PointCount => Points.Length / 3;
    }

    /// <summary>一個模型這次觀測的讀數。</summary>
    public sealed class SCP_SculptModelReading
    {
        public const string Ok = "ok", Hidden = "hidden", Disabled = "disabled", Broken = "broken";
        /// <summary>模型的路徑：所在作品的關聯路徑（組裝件自己 ⇒ 空）＋ `/` ＋ 名字。</summary>
        public string Path = "";
        public string Owner = "";
        public string Frame = "";
        public SCP_SculptModel Model = new SCP_SculptModel();
        public string State = Ok;
        public string Reason = "";
        /// <summary>模型所在那一層這次沒組進來（或自己關著）⇒ 綁在它上面的繩跟著 hidden；只被顯示開關關掉 ⇒ false。</summary>
        public bool LinkHidden;
        /// <summary>模型座標 → 組裝件（3×4，列優先）；檔案座標 → 模型座標（3×3）。broken 時可能沒有。</summary>
        public double[]? ToRoot;
        public double[] Axes = SCP_SculptModels.AxesMatrix(SCP_SculptModels.AxesYUp);
        /// <summary>讀進來的網格（檔案座標）；沒讀（hidden／disabled／只要讀數）⇒ null。</summary>
        public SCP_SculptObjMesh? Mesh;
        /// <summary>實際讀的 obj（file ⇒ 作品裡那一份；work ⇒ 快取檔）。</summary>
        public string ObjPath = "";
        /// <summary>work 來源：這次是不是剛產生快取（false ＝ 沿用）。</summary>
        public bool CacheProduced;
        public string CacheKey = "";
        /// <summary>換到組裝件之後的外框（有讀網格才有）。</summary>
        public double[]? Min, Max;
        public SCP_SculptModelGeom? Geom;
    }

    /// <summary>觀測時選哪些模型：None ＝ 都不畫（models=0）；Hide ＝ 這幾個（模型路徑）不畫。只管畫不畫 —— 綁在上面的繩照樣在。</summary>
    public sealed class SCP_SculptModelSelection
    {
        public bool None;
        public HashSet<string> Hide = new HashSet<string>(StringComparer.Ordinal);

        public static SCP_SculptModelSelection Parse(string iModels, string iHide)
        {
            var aSel = new SCP_SculptModelSelection { None = (iModels ?? "").Trim() == "0" };
            foreach (string p in (iHide ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) aSel.Hide.Add(p.Trim().Trim('/'));
            return aSel;
        }
    }

    /// <summary>
    /// 產生 work 來源模型的快取（宿主注入）：把作品 <paramref name="iWorkId"/> 匯出成 obj 寫到 <paramref name="iObjPath"/>（mtl 放旁邊）。
    /// 回 null ＝ 成功；否則是原因。⚠ 會拿那件作品的鎖 —— 呼叫端不可以正拿著別的作品鎖（全系統只有一種鎖序）。
    /// </summary>
    public delegate string? SCP_SculptModelCacheProducer(string iWorkId, string iObjPath);

    /// <summary>一次觀測的全部模型＋「模型上的點」的解法（繩的端點、帆角）。</summary>
    public sealed class SCP_SculptModelSet
    {
        public readonly List<SCP_SculptModelReading> Models = new List<SCP_SculptModelReading>();
        internal SCP_SculptWorks? Works;
        internal readonly Dictionary<string, SCP_SculptModelReading> ByPath = new Dictionary<string, SCP_SculptModelReading>(StringComparer.Ordinal);
        readonly Dictionary<string, SCP_SculptAnchorFile> m_Anchors = new Dictionary<string, SCP_SculptAnchorFile>(StringComparer.Ordinal);

        /// <summary>畫得出來的模型（state ok、有網格）。</summary>
        public List<SCP_SculptModelGeom> Geoms()
        {
            var aOut = new List<SCP_SculptModelGeom>();
            foreach (var m in Models) if (m.Geom != null) aOut.Add(m.Geom);
            return aOut;
        }

        /// <summary>
        /// 模型上的點（`model:` 後面那一段：`[關聯/…/]模型名/點`，相對於 <paramref name="iFrame"/>）解到組裝件座標。
        /// 回 null ＝ 解得到（<paramref name="oHiddenWhy"/> 非空 ＝ 模型這次沒組進來）；否則是壞掉的原因。<paramref name="oKey"/> ＝ 模型全路徑。
        /// </summary>
        public string? ResolvePoint(string iFrame, string iRef, out double[]? oPoint, out string oKey, out string oHiddenWhy)
        {
            oPoint = null; oKey = ""; oHiddenWhy = "";
            string[] aSegs = iRef.Split('/');
            if (aSegs.Length < 2) return "模型端點 `model:" + iRef + "` 要寫成 model:模型名/點（點 ＝ x,y,z 或來源作品的固定點名）";
            string aPoint = aSegs[aSegs.Length - 1], aName = aSegs[aSegs.Length - 2];
            string aRel = string.Join("/", aSegs, 0, aSegs.Length - 2);
            string aFrame = aRel.Length == 0 ? iFrame : iFrame.Length == 0 ? aRel : iFrame + "/" + aRel;
            string aPath = aFrame.Length == 0 ? aName : aFrame + "/" + aName;
            if (!ByPath.TryGetValue(aPath, out SCP_SculptModelReading? m))
            {
                var aHere = new List<string>();
                foreach (var x in Models) if (x.Frame == aFrame) aHere.Add(x.Model.name);
                return "沒有模型 `" + aPath + "`（那一層現有：" + (aHere.Count == 0 ? "（沒有）" : string.Join("、", aHere)) + "）";
            }
            if (m.ToRoot == null) return "模型 `" + aPath + "` 本身壞了（" + m.Reason + "）";
            oKey = aPath;
            double[] p;
            string[] aXyz = aPoint.Split(',');
            if (aXyz.Length == 3)
            {
                // 座標：file ＝ 檔案座標（照 axes 換）；work ＝ 那件作品的格座標（整數 ⇒ 格心，同繩的端點）
                if (!SCP_SculptRopes.TryParseEndpoint(aPoint, out double[]? aCell, out _, out string aBad) || aCell == null) return "模型端點 `model:" + iRef + "`：" + aBad;
                if (m.Model.source == SCP_SculptModels.SourceWork) p = aCell;
                else
                {
                    var raw = new double[3];
                    for (int i = 0; i < 3; i++) raw[i] = double.Parse(aXyz[i].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
                    p = SCP_SculptModels.Mul3(m.Axes, raw);
                }
            }
            else
            {
                if (m.Model.source != SCP_SculptModels.SourceWork)
                    return "模型 `" + aPath + "` 是 obj 檔 —— 點要寫檔案座標 x,y,z（固定點名只給作品來源的模型）";
                if (Works == null) return "模型端點要在模型的觀測裡解（沒有作品庫）";
                if (!m_Anchors.TryGetValue(m.Model.work, out SCP_SculptAnchorFile? aFile))
                {
                    try { aFile = SCP_SculptRopes.ReadAnchors(Works.SpacePaths(m.Model.work)); }
                    catch (InvalidOperationException e) { return e.Message; }
                    m_Anchors[m.Model.work] = aFile;
                }
                SCP_SculptAnchor? a = aFile.anchors.Find(x => x.name == aPoint);
                if (a == null)
                    return "模型 `" + aPath + "` 的來源作品 " + m.Model.work + " 沒有固定點 `" + aPoint + "`（現有：" + (aFile.anchors.Count == 0 ? "（沒有）" : string.Join("、", aFile.anchors.ConvertAll(x => x.name))) + "）";
                p = new[] { a.at[0] + 0.5, a.at[1] + 0.5, a.at[2] + 0.5 };
            }
            oPoint = SCP_SculptModels.Apply(m.ToRoot, p);
            if (m.LinkHidden) oHiddenWhy = " `" + aPath + "` 這次沒組進來（" + m.State + "）";
            return null;
        }
    }

    public static class SCP_SculptModels
    {
        public const string SourceFile = "file", SourceWork = "work";
        public const string AxesYUp = "yup", AxesZUp = "zup", AxesSculpt = "sculpt";
        public const string Prefix = "model:";
        public const string DefaultColor = "#a8a8a8";
        /// <summary>匯入的 obj 上限（位元組）與三角形上限 —— 超過就擋（⛔ 不讓一個檔把觀測拖垮）。</summary>
        public const long MaxObjBytes = 256L * 1024 * 1024;
        public const int MaxTriangles = 8_000_000;
        public const double MaxScale = 1e4;
        /// <summary>快取格式版本：匯出那一側的輸出改了就加一（舊快取的鍵就對不上）。</summary>
        public const string CacheVersion = "sculpt-model-cache 1";
        /// <summary>
        /// 有 meta 的 models.json 寫這個 schema（TASK-0517）。
        /// 🩸 為什麼要升號：舊版 senate.exe 的 <see cref="SCP_SculptModel"/> 沒有 meta ⇒ 讀到會**默默丟掉**、下一次寫回去就清光，而檔看起來完全正常。
        /// 舊版只認 schema 1 ⇒ 升號讓它大聲讀不了；沒用 meta 的檔維持 1。
        /// </summary>
        public const int SchemaWithMeta = 2;

        // ───────────────────────── 讀寫 ─────────────────────────

        public static SCP_SculptModelFile ReadModels(SCP_SculptPaths iPaths)
        {
            string aFile = iPaths.ModelsFile;
            if (!File.Exists(aFile)) return new SCP_SculptModelFile();
            var aOut = new SCP_SculptModelFile { schema = 0 };
            try
            {
                var aOpt = new SCP_JsonMapOptions();
                SCP_JsonData aRoot = SCP_JsonParser.Parse(File.ReadAllText(aFile, new UTF8Encoding(false)), false);
                SCP_JsonMapper.Populate(aOut, aRoot, aOpt);
                if (aOpt.Diagnostics.Count > 0) throw new InvalidOperationException(string.Join("; ", aOpt.Diagnostics));
                // meta（[SCP_Ignore]，對映器不碰）：照順序對回每一筆
                SCP_JsonData aList = aRoot["models"];
                for (int i = 0; i < aOut.models.Count && aList.IsArray && i < aList.Count; i++)
                    if (SCP_SculptMeta.ReadFrom(aList[i]["meta"], aOut.models[i].meta) is string aMetaBad)
                        throw new InvalidOperationException("模型 `" + aOut.models[i].name + "` 的 " + aMetaBad);
            }
            catch (Exception e) { throw new InvalidOperationException("models.json 讀不了（" + aFile + "：" + e.Message + "）—— ⛔ 不當成沒有，修好再用"); }
            if (aOut.schema != 1 && aOut.schema != SchemaWithMeta) throw new InvalidOperationException("models.json 讀不了（" + aFile + "：schema=" + aOut.schema + "）");
            if (aOut.schema == 1 && aOut.models.Exists(m => m.meta.Count > 0)) throw new InvalidOperationException("models.json 讀不了（" + aFile + "：有 meta 卻是 schema 1 —— 手改的檔？）");
            var aNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var m in aOut.models)
            {
                string? aBad = SCP_SculptRopes.ValidName(m.name, "模型") ?? (aNames.Add(m.name) ? null : "名字重複") ?? Check(m);
                if (aBad != null) throw new InvalidOperationException("models.json 讀不了（" + aFile + "：模型 `" + m.name + "` " + aBad + "）");
            }
            return aOut;
        }

        public static void SaveModels(SCP_SculptPaths iPaths, SCP_SculptModelFile iFile)
        {
            iFile.schema = iFile.models.Exists(m => m.meta.Count > 0) ? SchemaWithMeta : 1;
            string aPath = iPaths.ModelsFile, aTmp = aPath + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                SCP_JsonData aRoot = SCP_JsonMapper.ToJson(iFile);
                SCP_JsonData aList = aRoot["models"];
                for (int i = 0; i < iFile.models.Count; i++) SCP_SculptMeta.WriteTo(aList[i], iFile.models[i].meta);
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(aRoot, true) + "\n", new UTF8Encoding(false));
                if (File.Exists(aPath)) File.Replace(aTmp, aPath, null);
                else File.Move(aTmp, aPath);
            }
            finally { try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch (Exception) { } }
        }

        /// <summary>一個模型的欄位合不合法（不碰檔案）。null ＝ 合法。</summary>
        public static string? Check(SCP_SculptModel m)
        {
            if (m.source == SourceFile)
            {
                if (m.file.Length == 0) return "file 來源要有 file（相對作品資料夾）";
                if (Path.IsPathRooted(m.file) || m.file.Contains("..")) return "file 要是作品資料夾裡的相對路徑（got `" + m.file + "`）";
                if (m.work.Length > 0) return "file 來源不該有 work";
                if (AxesMatrixOrNull(m.axes) == null) return "axes 要是 yup｜zup｜sculpt（got `" + m.axes + "`）";
            }
            else if (m.source == SourceWork)
            {
                if (m.work.Length == 0) return "work 來源要有 work（來源作品 ID）";
                if (m.file.Length > 0) return "work 來源不該有 file";
                if (m.axes != AxesSculpt) return "work 來源的 axes 固定是 sculpt（快取是本引擎匯出的）";
            }
            else return "source 要是 file 或 work（got `" + m.source + "`）";
            foreach (var (aList, aWhat) in new[] { (m.at, "at"), (m.pivot, "pivot"), (m.rotate, "rotate"), (m.scale, "scale") })
            {
                if (aList == null || aList.Count != 3) return aWhat + " 要三個數";
                foreach (double v in aList) if (double.IsNaN(v) || double.IsInfinity(v)) return aWhat + " 不是有限的數";
            }
            foreach (double s in m.scale) if (s <= 0 || s > MaxScale) return "scale 每軸要在 (0, " + MaxScale + "]（要翻面用 mirror）";
            if (CheckMirror(m.mirror) is string aMirBad) return aMirBad;
            if (m.color.Length > 0 && SCP_SculptRopes.TryParseColor(m.color, out _, out _, out _, out string aNorm) is string aColorBad) return aColorBad;
            return null;
        }

        public static string? CheckMirror(string iMirror)
        {
            foreach (char ch in iMirror ?? "") if (ch != 'x' && ch != 'y' && ch != 'z') return "mirror 只能是 x／y／z 的組合（got `" + iMirror + "`）";
            return null;
        }

        // ───────────────────────── 座標 ─────────────────────────

        /// <summary>檔案座標 → 模型座標（3×3，列優先）；不認得 ⇒ null。</summary>
        public static double[]? AxesMatrixOrNull(string iAxes)
        {
            switch (iAxes)
            {
                case AxesYUp: return new double[] { 1, 0, 0, 0, 0, -1, 0, 1, 0 };     // (x, −z, y)
                case AxesZUp: return new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 };
                case AxesSculpt: return new double[] { 1, 0, 0, 0, 0, 1, 0, 1, 0 };   // (x, z, y)：export 寫 (wx, wz, wy) 的反函數
                default: return null;
            }
        }

        public static double[] AxesMatrix(string iAxes) => AxesMatrixOrNull(iAxes) ?? throw new ArgumentException("axes 不認得：" + iAxes);

        public static double[] Mul3(double[] iM, double[] iP)
            => new[] { iM[0] * iP[0] + iM[1] * iP[1] + iM[2] * iP[2], iM[3] * iP[0] + iM[4] * iP[1] + iM[5] * iP[2], iM[6] * iP[0] + iM[7] * iP[1] + iM[8] * iP[2] };

        /// <summary>3×4 仿射套到一點。</summary>
        public static double[] Apply(double[] iA, double[] iP)
            => new[] { iA[0] * iP[0] + iA[1] * iP[1] + iA[2] * iP[2] + iA[3], iA[4] * iP[0] + iA[5] * iP[1] + iA[6] * iP[2] + iA[7], iA[8] * iP[0] + iA[9] * iP[1] + iA[10] * iP[2] + iA[11] };

        /// <summary>一個點函式（仿射）的 3×4 矩陣：拿原點與三個單位向量量出來。</summary>
        public static double[] AffineOf(Func<double[], double[]> iF)
        {
            double[] o = iF(new double[] { 0, 0, 0 }), ex = iF(new double[] { 1, 0, 0 }), ey = iF(new double[] { 0, 1, 0 }), ez = iF(new double[] { 0, 0, 1 });
            return new[]
            {
                ex[0] - o[0], ey[0] - o[0], ez[0] - o[0], o[0],
                ex[1] - o[1], ey[1] - o[1], ez[1] - o[1], o[1],
                ex[2] - o[2], ey[2] - o[2], ez[2] - o[2], o[2],
            };
        }

        /// <summary>模型座標 → 作品座標：at ＋ R·(scale ⊙ mirror(p − pivot))，R ＝ Rz·Ry·Rx。</summary>
        public static double[] ToWork(SCP_SculptModel m, double[] iP)
        {
            double x = iP[0] - m.pivot[0], y = iP[1] - m.pivot[1], z = iP[2] - m.pivot[2];
            string mi = m.mirror ?? "";
            if (mi.IndexOf('x') >= 0) x = -x;
            if (mi.IndexOf('y') >= 0) y = -y;
            if (mi.IndexOf('z') >= 0) z = -z;
            x *= m.scale[0]; y *= m.scale[1]; z *= m.scale[2];
            double ax = m.rotate[0] * Math.PI / 180, ay = m.rotate[1] * Math.PI / 180, az = m.rotate[2] * Math.PI / 180;
            double c, s, t;
            c = Math.Cos(ax); s = Math.Sin(ax); t = y * c - z * s; z = y * s + z * c; y = t;    // 繞 X
            c = Math.Cos(ay); s = Math.Sin(ay); t = x * c + z * s; z = -x * s + z * c; x = t;   // 繞 Y
            c = Math.Cos(az); s = Math.Sin(az); t = x * c - y * s; y = x * s + y * c; x = t;    // 繞 Z
            return new[] { x + m.at[0], y + m.at[1], z + m.at[2] };
        }

        static double Det(double[] a) => a[0] * (a[5] * a[10] - a[6] * a[9]) - a[1] * (a[4] * a[10] - a[6] * a[8]) + a[2] * (a[4] * a[9] - a[5] * a[8]);

        // ───────────────────────── obj ─────────────────────────

        static readonly object s_CacheLock = new object();
        static readonly Dictionary<string, (long Len, long Ticks, SCP_SculptObjMesh Mesh)> s_Parsed = new Dictionary<string, (long, long, SCP_SculptObjMesh)>(StringComparer.OrdinalIgnoreCase);

        /// <summary>讀 obj（行程內快取：同路徑＋同大小＋同修改時間 ⇒ 沿用；觀測頁一直重組場景時不重讀）。</summary>
        public static SCP_SculptObjMesh LoadObj(string iPath)
        {
            var fi = new FileInfo(iPath);
            if (!fi.Exists) throw new InvalidDataException("obj 不存在：" + iPath);
            long aLen = fi.Length, aTicks = fi.LastWriteTimeUtc.Ticks;
            lock (s_CacheLock)
                if (s_Parsed.TryGetValue(fi.FullName, out var hit) && hit.Len == aLen && hit.Ticks == aTicks) return hit.Mesh;
            SCP_SculptObjMesh aMesh = ParseObj(iPath);
            lock (s_CacheLock)
            {
                if (s_Parsed.Count >= 32) s_Parsed.Clear();
                s_Parsed[fi.FullName] = (aLen, aTicks, aMesh);
            }
            return aMesh;
        }

        /// <summary>
        /// 解析 obj：v／vn／f（多邊形扇形切三角形；索引 1 起或負數相對）／usemtl／mtllib（Kd）。
        /// 讀不了、沒有面、索引越界、超過上限 ⇒ InvalidDataException（含行號）。缺 mtl 檔或材質 ⇒ 警告、用預設色。
        /// </summary>
        public static SCP_SculptObjMesh ParseObj(string iPath)
        {
            var fi = new FileInfo(iPath);
            if (!fi.Exists) throw new InvalidDataException("obj 不存在：" + iPath);
            if (fi.Length > MaxObjBytes) throw new InvalidDataException("obj 太大（" + fi.Length.ToString("N0") + " bytes，上限 " + MaxObjBytes.ToString("N0") + "）：" + iPath);
            var aPos = new List<double>(); var aNrm = new List<double>();
            var aCp = new List<int>(); var aCn = new List<int>(); var aTm = new List<int>();
            var aMatIndex = new Dictionary<string, int>(StringComparer.Ordinal);
            var aMesh = new SCP_SculptObjMesh();
            var aMtl = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            int aCurMat = -1, aLineNo = 0, aSkipped = 0;
            var aFace = new List<int>(); var aFaceN = new List<int>();
            string aDir = Path.GetDirectoryName(Path.GetFullPath(iPath)) ?? ".";
            using (var r = new StreamReader(iPath, Encoding.UTF8))
            {
                string? line;
                while ((line = r.ReadLine()) != null)
                {
                    aLineNo++;
                    string t = line.Trim();
                    if (t.Length == 0 || t[0] == '#') continue;
                    int sp = t.IndexOfAny(new[] { ' ', '\t' });
                    string key = sp < 0 ? t : t.Substring(0, sp), rest = sp < 0 ? "" : t.Substring(sp + 1).Trim();
                    switch (key)
                    {
                        case "v":
                        case "vn":
                        {
                            string[] p = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (p.Length < 3) throw Bad(iPath, aLineNo, key + " 要至少三個數");
                            var dst = key == "v" ? aPos : aNrm;
                            for (int i = 0; i < 3; i++)
                            {
                                if (!double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) || double.IsNaN(d) || double.IsInfinity(d))
                                    throw Bad(iPath, aLineNo, key + " 的 `" + p[i] + "` 不是數");
                                dst.Add(d);
                            }
                            break;
                        }
                        case "f":
                        {
                            string[] p = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                            if (p.Length < 3) throw Bad(iPath, aLineNo, "面要至少三個角");
                            aFace.Clear(); aFaceN.Clear();
                            foreach (string corner in p)
                            {
                                string[] parts = corner.Split('/');
                                aFace.Add(ObjIndex(parts[0], aPos.Count / 3, iPath, aLineNo, "頂點"));
                                aFaceN.Add(parts.Length >= 3 && parts[2].Length > 0 ? ObjIndex(parts[2], aNrm.Count / 3, iPath, aLineNo, "法線") : -1);
                            }
                            for (int k = 1; k + 1 < aFace.Count; k++)
                            {
                                aCp.Add(aFace[0]); aCp.Add(aFace[k]); aCp.Add(aFace[k + 1]);
                                aCn.Add(aFaceN[0]); aCn.Add(aFaceN[k]); aCn.Add(aFaceN[k + 1]);
                                aTm.Add(aCurMat);
                            }
                            if (aTm.Count > MaxTriangles) throw Bad(iPath, aLineNo, "三角形超過上限 " + MaxTriangles.ToString("N0"));
                            break;
                        }
                        case "usemtl":
                            if (!aMatIndex.TryGetValue(rest, out aCurMat)) { aCurMat = aMesh.MaterialNames.Count; aMatIndex[rest] = aCurMat; aMesh.MaterialNames.Add(rest); }
                            break;
                        case "mtllib":
                            foreach (string lib in rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                            {
                                string aLibPath = Path.Combine(aDir, lib);
                                if (!File.Exists(aLibPath)) { aMesh.Warnings.Add("mtl 不在：" + lib + "（用預設色）"); continue; }
                                ParseMtl(aLibPath, aMtl, aMesh.Warnings);
                            }
                            break;
                        case "vt": case "o": case "g": case "s": break;
                        default: aSkipped++; break;
                    }
                }
            }
            if (aTm.Count == 0) throw new InvalidDataException("obj 裡沒有面（f）：" + iPath);
            if (aSkipped > 0) aMesh.Warnings.Add("略過 " + aSkipped + " 行不讀的關鍵字（線段、曲面、貼圖…）");
            aMesh.Positions = aPos.ToArray(); aMesh.Normals = aNrm.ToArray();
            aMesh.CornerPos = aCp.ToArray(); aMesh.CornerNrm = aCn.ToArray(); aMesh.TriMaterial = aTm.ToArray();
            foreach (string n in aMesh.MaterialNames)
            {
                aMtl.TryGetValue(n, out byte[]? c);
                if (c == null) aMesh.Warnings.Add("材質 `" + n + "` 沒有 Kd（用預設色）");
                aMesh.MaterialColors.Add(c);
            }
            for (int i = 0; i < 3; i++) { aMesh.Min[i] = double.MaxValue; aMesh.Max[i] = double.MinValue; }
            for (int v = 0; v < aMesh.Positions.Length; v += 3)
                for (int i = 0; i < 3; i++) { aMesh.Min[i] = Math.Min(aMesh.Min[i], aMesh.Positions[v + i]); aMesh.Max[i] = Math.Max(aMesh.Max[i], aMesh.Positions[v + i]); }
            return aMesh;
        }

        static InvalidDataException Bad(string iPath, int iLine, string iWhy) => new InvalidDataException("obj 讀不了（" + Path.GetFileName(iPath) + " 第 " + iLine + " 行）：" + iWhy);

        static int ObjIndex(string iText, int iCount, string iPath, int iLine, string iWhat)
        {
            if (!int.TryParse(iText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int k) || k == 0) throw Bad(iPath, iLine, iWhat + "索引 `" + iText + "` 不是非零整數");
            int i = k > 0 ? k - 1 : iCount + k;
            if (i < 0 || i >= iCount) throw Bad(iPath, iLine, iWhat + "索引 " + k + " 越界（目前只有 " + iCount + " 個）");
            return i;
        }

        static void ParseMtl(string iPath, Dictionary<string, byte[]> ioOut, List<string> ioWarn)
        {
            string aCur = "";
            int aLine = 0;
            foreach (string raw in File.ReadAllLines(iPath, Encoding.UTF8))
            {
                aLine++;
                string t = raw.Trim();
                if (t.StartsWith("newmtl ", StringComparison.Ordinal)) { aCur = t.Substring(7).Trim(); continue; }
                if (!t.StartsWith("Kd ", StringComparison.Ordinal) || aCur.Length == 0) continue;
                string[] p = t.Substring(3).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var c = new byte[3];
                bool ok = p.Length >= 3;
                for (int i = 0; ok && i < 3; i++)
                {
                    ok = double.TryParse(p[i], NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && !double.IsNaN(d);
                    if (ok) c[i] = (byte)Math.Round(Math.Max(0, Math.Min(1, d)) * 255);
                }
                if (ok) ioOut[aCur] = c;
                else ioWarn.Add(Path.GetFileName(iPath) + " 第 " + aLine + " 行的 Kd 讀不了（用預設色）");
            }
        }

        /// <summary>檔案的 SHA256（小寫十六進位）。</summary>
        public static string FileSha256(string iPath)
        {
            using (var s = File.OpenRead(iPath))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>obj 引用的 mtl 檔（同資料夾、存在的那幾個；匯入時一起複製）。</summary>
        public static List<string> MtlLibs(string iObjPath)
        {
            var aOut = new List<string>();
            string aDir = Path.GetDirectoryName(Path.GetFullPath(iObjPath)) ?? ".";
            foreach (string line in File.ReadLines(iObjPath, Encoding.UTF8))
            {
                string t = line.Trim();
                if (!t.StartsWith("mtllib", StringComparison.Ordinal)) continue;
                foreach (string lib in t.Substring(6).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                    if (File.Exists(Path.Combine(aDir, lib)) && !aOut.Contains(lib)) aOut.Add(lib);
            }
            return aOut;
        }

        // ───────────────────────── 網格 → 組裝件座標 ─────────────────────────

        /// <summary>
        /// 一個模型的幾何（組裝件座標、從外面看逆時針）：<paramref name="iRawToRoot"/> ＝ 檔案座標 → 組裝件（3×4）。
        /// 有 vn 的角共用頂點（同位置＋同法線＋同材質）；沒有 vn 的角用面法線（各自一份）。行列式 &lt; 0 ⇒ 繞序反過來。
        /// </summary>
        public static SCP_SculptModelGeom BuildGeom(string iPath, SCP_SculptObjMesh iMesh, double[] iRawToRoot, string iColorOverride)
        {
            double[] A = iRawToRoot;
            bool aFlip = Det(A) < 0;
            // 法線：線性部分的逆轉置（再單位化）
            double[] L = { A[0], A[1], A[2], A[4], A[5], A[6], A[8], A[9], A[10] };
            double[] N = InverseTranspose(L);
            byte[] aOverride = new byte[0];
            if (iColorOverride.Length > 0 && SCP_SculptRopes.TryParseColor(iColorOverride, out byte or, out byte og, out byte ob, out _) == null) aOverride = new[] { or, og, ob };
            SCP_SculptRopes.TryParseColor(DefaultColor, out byte dr, out byte dg, out byte db, out _);
            int nv = iMesh.VertexCount;
            var aWorld = new double[nv * 3];
            for (int v = 0; v < nv; v++)
            {
                double x = iMesh.Positions[v * 3], y = iMesh.Positions[v * 3 + 1], z = iMesh.Positions[v * 3 + 2];
                aWorld[v * 3] = A[0] * x + A[1] * y + A[2] * z + A[3];
                aWorld[v * 3 + 1] = A[4] * x + A[5] * y + A[6] * z + A[7];
                aWorld[v * 3 + 2] = A[8] * x + A[9] * y + A[10] * z + A[11];
            }
            var aPts = new List<double>(); var aNrm = new List<double>(); var aCol = new List<byte>(); var aTri = new List<int>();
            var aShared = new Dictionary<(int Pos, int Nrm, int Mat), int>();
            int tc = iMesh.TriangleCount;
            for (int t = 0; t < tc; t++)
            {
                int mat = iMesh.TriMaterial[t];
                byte[] c = aOverride.Length > 0 ? aOverride : mat >= 0 && iMesh.MaterialColors[mat] != null ? iMesh.MaterialColors[mat]! : new[] { dr, dg, db };
                int i0 = iMesh.CornerPos[t * 3], i1 = iMesh.CornerPos[t * 3 + 1], i2 = iMesh.CornerPos[t * 3 + 2];
                // 面法線（世界）：照原繞序算；翻面時繞序反過來，法線跟著反
                double e1x = aWorld[i1 * 3] - aWorld[i0 * 3], e1y = aWorld[i1 * 3 + 1] - aWorld[i0 * 3 + 1], e1z = aWorld[i1 * 3 + 2] - aWorld[i0 * 3 + 2];
                double e2x = aWorld[i2 * 3] - aWorld[i0 * 3], e2y = aWorld[i2 * 3 + 1] - aWorld[i0 * 3 + 1], e2z = aWorld[i2 * 3 + 2] - aWorld[i0 * 3 + 2];
                double fx = e1y * e2z - e1z * e2y, fy = e1z * e2x - e1x * e2z, fz = e1x * e2y - e1y * e2x;
                double fl = Math.Sqrt(fx * fx + fy * fy + fz * fz);
                if (fl < 1e-18) continue;   // 退化的三角形（三點共線）不畫
                if (aFlip) { fx = -fx; fy = -fy; fz = -fz; }
                fx /= fl; fy /= fl; fz /= fl;
                int[] aCorner = new int[3];
                for (int k = 0; k < 3; k++)
                {
                    int pi = iMesh.CornerPos[t * 3 + k], ni = iMesh.CornerNrm[t * 3 + k];
                    if (ni >= 0)
                    {
                        var aKey = (pi, ni, aOverride.Length > 0 ? 0 : mat);
                        if (aShared.TryGetValue(aKey, out int idx)) { aCorner[k] = idx; continue; }
                        double nx = iMesh.Normals[ni * 3], ny = iMesh.Normals[ni * 3 + 1], nz = iMesh.Normals[ni * 3 + 2];
                        double wx = N[0] * nx + N[1] * ny + N[2] * nz, wy = N[3] * nx + N[4] * ny + N[5] * nz, wz = N[6] * nx + N[7] * ny + N[8] * nz;
                        double wl = Math.Sqrt(wx * wx + wy * wy + wz * wz);
                        if (wl < 1e-18) { wx = fx; wy = fy; wz = fz; } else { wx /= wl; wy /= wl; wz /= wl; }
                        int aNew = aPts.Count / 3;
                        aPts.Add(aWorld[pi * 3]); aPts.Add(aWorld[pi * 3 + 1]); aPts.Add(aWorld[pi * 3 + 2]);
                        aNrm.Add(wx); aNrm.Add(wy); aNrm.Add(wz);
                        aCol.Add(c[0]); aCol.Add(c[1]); aCol.Add(c[2]);
                        aShared[aKey] = aNew;
                        aCorner[k] = aNew;
                    }
                    else
                    {
                        int aNew = aPts.Count / 3;
                        aPts.Add(aWorld[pi * 3]); aPts.Add(aWorld[pi * 3 + 1]); aPts.Add(aWorld[pi * 3 + 2]);
                        aNrm.Add(fx); aNrm.Add(fy); aNrm.Add(fz);
                        aCol.Add(c[0]); aCol.Add(c[1]); aCol.Add(c[2]);
                        aCorner[k] = aNew;
                    }
                }
                if (aFlip) { aTri.Add(aCorner[0]); aTri.Add(aCorner[2]); aTri.Add(aCorner[1]); }
                else { aTri.Add(aCorner[0]); aTri.Add(aCorner[1]); aTri.Add(aCorner[2]); }
            }
            return new SCP_SculptModelGeom { Path = iPath, Points = aPts.ToArray(), Normals = aNrm.ToArray(), Colors = aCol.ToArray(), Triangles = aTri.ToArray() };
        }

        static double[] InverseTranspose(double[] m)
        {
            // 餘因子矩陣 ÷ 行列式 ＝ 逆矩陣的轉置
            double a = m[0], b = m[1], c = m[2], d = m[3], e = m[4], f = m[5], g = m[6], h = m[7], i = m[8];
            double A = e * i - f * h, B = -(d * i - f * g), C = d * h - e * g;
            double D = -(b * i - c * h), E = a * i - c * g, F = -(a * h - b * g);
            double G = b * f - c * e, H = -(a * f - c * d), I = a * e - b * d;
            double det = a * A + b * B + c * C;
            if (Math.Abs(det) < 1e-300) det = 1;
            return new[] { A / det, B / det, C / det, D / det, E / det, F / det, G / det, H / det, I / det };
        }

        // ───────────────────────── work 來源：快取 ─────────────────────────

        /// <summary>
        /// 從 <paramref name="iWorkId"/> 出發，沿關聯與 work 來源的模型走到的所有作品（含自己）。
        /// 走到 <paramref name="iTarget"/> ⇒ 回那一條路（成環的證據）；否則 null。作品讀不了 ⇒ 照丟（呼叫端說原因）。
        /// </summary>
        public static List<string>? PathTo(SCP_SculptWorks iWorks, string iWorkId, string iTarget, HashSet<string>? ioSeen = null, List<string>? ioStack = null)
        {
            ioSeen ??= new HashSet<string>(StringComparer.Ordinal);
            ioStack ??= new List<string>();
            ioStack.Add(iWorkId);
            try
            {
                if (iWorkId == iTarget && ioStack.Count > 1) return new List<string>(ioStack);
                if (!ioSeen.Add(iWorkId)) return null;
                foreach (string n in Neighbors(iWorks, iWorkId))
                {
                    if (n == iTarget) { var aHit = new List<string>(ioStack) { n }; return aHit; }
                    var aPath = PathTo(iWorks, n, iTarget, ioSeen, ioStack);
                    if (aPath != null) return aPath;
                }
                return null;
            }
            finally { ioStack.RemoveAt(ioStack.Count - 1); }
        }

        /// <summary>一件作品直接用到的作品：關聯（含停用的）＋ work 來源的模型。</summary>
        public static List<string> Neighbors(SCP_SculptWorks iWorks, string iWorkId)
        {
            var aOut = new List<string>();
            var p = iWorks.SpacePaths(iWorkId);
            foreach (var l in SCP_SculptLinks.Read(p).links) if (!aOut.Contains(l.work)) aOut.Add(l.work);
            foreach (var m in ReadModels(p).models) if (m.source == SourceWork && !aOut.Contains(m.work)) aOut.Add(m.work);
            return aOut;
        }

        /// <summary>
        /// work 來源快取的鍵：從來源出發走到的每一件作品（關聯＋模型來源）的事件水位（件數＋最後一個事件檔）、書卡尺寸與比例、
        /// links／anchors／ropes／sails／models.json 的位元組雜湊、file 來源模型檔的大小與修改時間 ＋ 快取格式版本。
        /// 成環 ⇒ ArgumentException。
        /// </summary>
        public static string CacheKey(SCP_SculptWorks iWorks, string iWorkId)
        {
            var aAll = new SortedSet<string>(StringComparer.Ordinal);
            void Walk(string id, List<string> stack)
            {
                if (stack.Contains(id)) throw new ArgumentException("模型來源成環：" + string.Join(" → ", stack) + " → " + id);
                if (!aAll.Add(id)) return;
                stack.Add(id);
                foreach (string n in Neighbors(iWorks, id)) Walk(n, stack);
                stack.RemoveAt(stack.Count - 1);
            }
            Walk(iWorkId, new List<string>());
            var sb = new StringBuilder(CacheVersion).Append('\n').Append(iWorkId).Append('\n');
            foreach (string id in aAll)
            {
                SCP_SculptWork aCard = iWorks.Load(id);
                var p = iWorks.SpacePaths(id);
                var aEvents = SCP_SculptStore.ListEvents(p);
                sb.Append(id).Append('|').Append(aCard.Dimensions).Append('|').Append(aCard.MetersPerVoxel.ToString("R", CultureInfo.InvariantCulture))
                  .Append('|').Append(aEvents.Count).Append('|').Append(aEvents.Count > 0 ? aEvents[aEvents.Count - 1].Rel : "").Append('\n');
                foreach (string f in new[] { p.LinksFile, p.AnchorsFile, p.RopesFile, p.SailsFile, p.ModelsFile })
                    sb.Append(Path.GetFileName(f)).Append('=').Append(File.Exists(f) ? FileSha256(f) : "-").Append('\n');
                foreach (var m in ReadModels(p).models)
                    if (m.source == SourceFile)
                    {
                        var fi = new FileInfo(Path.Combine(iWorks.Folder(id), m.file));
                        sb.Append("  ").Append(m.file).Append('=').Append(fi.Exists ? fi.Length + "@" + fi.LastWriteTimeUtc.Ticks : "-").Append('\n');
                    }
            }
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()))).Replace("-", "").ToLowerInvariant().Substring(0, 24);
        }

        public static string CachePath(SCP_SculptWorks iWorks, string iWorkId, string iKey) => Path.Combine(iWorks.ModelCacheRoot, iWorkId, iKey + ".obj");

        [ThreadStatic] static HashSet<string>? t_Producing;

        /// <summary>
        /// work 來源的快取：鍵對得上的檔在 ⇒ 沿用；不在 ⇒ 叫宿主產生（產生完把同一件作品的舊快取清掉）。回 null ＝ 成功。
        /// 同一個執行緒裡正在產生同一件 ⇒ 成環（⛔ 不無限遞迴）。
        /// </summary>
        public static string? EnsureCache(SCP_SculptWorks iWorks, string iWorkId, SCP_SculptModelCacheProducer? iProducer, out string oPath, out string oKey, out bool oProduced)
        {
            oPath = ""; oKey = ""; oProduced = false;
            try { oKey = CacheKey(iWorks, iWorkId); }
            catch (Exception e) when (e is ArgumentException || e is InvalidOperationException || e is IOException) { return e.Message; }
            oPath = CachePath(iWorks, iWorkId, oKey);
            if (File.Exists(oPath)) return null;
            if (iProducer == null) return "快取還沒產生，而這個宿主不會產生作品模型的快取（" + oPath + "）";
            t_Producing ??= new HashSet<string>(StringComparer.Ordinal);
            if (!t_Producing.Add(iWorkId)) return "模型來源成環：產生 " + iWorkId + " 的快取時又用到它自己";
            try
            {
                string aDir = Path.GetDirectoryName(oPath)!;
                Directory.CreateDirectory(aDir);
                string? aBad = iProducer(iWorkId, oPath);
                if (aBad != null) return "產生快取失敗：" + aBad;
                if (!File.Exists(oPath)) return "產生快取的那一步說成功了，可是檔不在：" + oPath;
                oProduced = true;
                string aKeep = oKey;
                foreach (string f in Directory.GetFiles(aDir))
                    if (!Path.GetFileName(f).StartsWith(aKeep + ".", StringComparison.Ordinal)) { try { File.Delete(f); } catch (IOException) { } }
                return null;
            }
            finally { t_Producing.Remove(iWorkId); }
        }

        // ───────────────────────── 觀測 ─────────────────────────

        /// <summary>
        /// 一次觀測的所有模型：組裝件自己的＋關聯樹上每一層的（照那一層換座標）。每個都有一筆讀數。
        /// <paramref name="iGeometry"/> false ⇒ 只算矩陣與狀態、不讀網格（列清單、寫入前驗證、繩端點）。
        /// <paramref name="iProducer"/>：work 來源的快取不在時用它產生（null ⇒ 那幾個 broken 並說原因）。
        /// <paramref name="iRootOverride"/>：組裝件自己那份換成還沒存的那一份（寫入前驗證）。models.json 讀不了 ⇒ InvalidOperationException 照丟。
        /// <paramref name="iBake"/> false ⇒ 讀網格但**不烘**成組裝件座標（Geom／Min／Max 留空）—— 子模型分開畫（TASK-0509）自己套矩陣、同一個 obj 共用一份。
        /// </summary>
        public static SCP_SculptModelSet Resolve(SCP_SculptWorks iWorks, string iRootId, List<SCP_SculptLinkNode>? iTree,
                                                 SCP_SculptLinkSelection iLinkSel, SCP_SculptModelSelection iSel, bool iGeometry,
                                                 SCP_SculptModelCacheProducer? iProducer = null, SCP_SculptModelFile? iRootOverride = null,
                                                 bool iBake = true)
        {
            var aSet = new SCP_SculptModelSet { Works = iWorks };
            var aIndex = SCP_SculptRopes.IndexTree(iTree);
            var aFrames = new List<string> { "" };
            SCP_SculptLinks.Paths(iTree ?? new List<SCP_SculptLinkNode>(), aFrames);
            var aFiles = new Dictionary<string, SCP_SculptModelFile>(StringComparer.Ordinal);
            if (iRootOverride != null) aFiles[iRootId] = iRootOverride;
            foreach (string aFrame in aFrames)
            {
                string aWork = aFrame.Length == 0 ? iRootId : aIndex[aFrame].Card.id;
                if (!aFiles.TryGetValue(aWork, out SCP_SculptModelFile? aFile)) { aFile = ReadModels(iWorks.SpacePaths(aWork)); aFiles[aWork] = aFile; }
                bool aFrameDrawn = SCP_SculptRopes.PathDrawn(aFrame, aIndex, iLinkSel);
                foreach (SCP_SculptModel m in aFile.models)
                {
                    var r = new SCP_SculptModelReading { Path = aFrame.Length == 0 ? m.name : aFrame + "/" + m.name, Owner = aWork, Frame = aFrame, Model = m };
                    var aWhy = new List<string>();
                    r.Axes = AxesMatrixOrNull(m.axes) ?? AxesMatrix(AxesYUp);
                    // 矩陣：模型座標 → 作品 → 組裝件（每一層關聯照 ThroughLink；仿射 ⇒ 量一次就好）
                    string aFrameCopy = aFrame;
                    SCP_SculptModel aModel = m;
                    r.ToRoot = AffineOf(p => SCP_SculptRopes.ToRoot(aFrameCopy, ToWork(aModel, p), aIndex));
                    if (!aFrameDrawn) { r.LinkHidden = true; aWhy.Add("模型所在的 `" + aFrame + "` 這次沒畫"); }
                    // 來源
                    string? aBroken = null;
                    if (m.source == SourceFile)
                    {
                        r.ObjPath = Path.Combine(iWorks.Folder(aWork), m.file);
                        if (!File.Exists(r.ObjPath)) aBroken = "obj 檔不在（" + m.file + "）";
                    }
                    else
                    {
                        if (!iWorks.Exists(m.work)) aBroken = "來源作品 `" + m.work + "` 不存在";
                        else
                        {
                            List<string>? aLoop = null;
                            try { aLoop = PathTo(iWorks, m.work, aWork); }
                            catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IOException) { aBroken = "來源作品讀不了：" + e.Message; }
                            if (aLoop != null) aBroken = "模型來源成環：" + aWork + " → " + string.Join(" → ", aLoop);
                        }
                    }
                    if (aBroken != null) { r.State = SCP_SculptModelReading.Broken; aWhy.Add(aBroken); }
                    else if (!m.enabled) { r.State = SCP_SculptModelReading.Disabled; r.LinkHidden = true; aWhy.Add("這個模型關著（enabled=0）"); }
                    else if (r.LinkHidden) r.State = SCP_SculptModelReading.Hidden;
                    else if (iSel.None) { r.State = SCP_SculptModelReading.Hidden; aWhy.Add("這次不畫模型（models=0）"); }
                    else if (iSel.Hide.Contains(r.Path)) { r.State = SCP_SculptModelReading.Hidden; aWhy.Add("這次拿掉（hide_models）"); }
                    // 網格：只有要畫的才讀（work 來源的快取可能要現產生 —— 不畫的不去產生）
                    if (iGeometry && r.State == SCP_SculptModelReading.Ok)
                    {
                        string? aMeshBad = null;
                        if (m.source == SourceWork)
                        {
                            aMeshBad = EnsureCache(iWorks, m.work, iProducer, out string aCachePath, out string aKey, out bool aProduced);
                            r.ObjPath = aCachePath; r.CacheKey = aKey; r.CacheProduced = aProduced;
                        }
                        if (aMeshBad == null)
                        {
                            try { r.Mesh = LoadObj(r.ObjPath); }
                            catch (Exception e) when (e is InvalidDataException || e is IOException) { aMeshBad = e.Message; }
                        }
                        if (aMeshBad != null) { r.State = SCP_SculptModelReading.Broken; aWhy.Add(aMeshBad); }
                        else if (!iBake) { if (r.Mesh!.Warnings.Count > 0) aWhy.Add("⚠ " + string.Join("；", r.Mesh.Warnings)); }
                        else
                        {
                            double[] aRawToRoot = Compose(r.ToRoot, r.Axes);
                            r.Geom = BuildGeom(r.Path, r.Mesh!, aRawToRoot, m.color);
                            r.Min = new[] { double.MaxValue, double.MaxValue, double.MaxValue };
                            r.Max = new[] { double.MinValue, double.MinValue, double.MinValue };
                            for (int i = 0; i < r.Geom.Points.Length; i++)
                            {
                                r.Min[i % 3] = Math.Min(r.Min[i % 3], r.Geom.Points[i]);
                                r.Max[i % 3] = Math.Max(r.Max[i % 3], r.Geom.Points[i]);
                            }
                            if (r.Mesh!.Warnings.Count > 0) aWhy.Add("⚠ " + string.Join("；", r.Mesh.Warnings));
                        }
                    }
                    r.Reason = string.Join("；", aWhy);
                    aSet.Models.Add(r);
                    aSet.ByPath[r.Path] = r;
                }
            }
            return aSet;
        }

        /// <summary>3×4 仿射 ∘ 3×3 線性（先套 <paramref name="iLin"/>）。</summary>
        public static double[] Compose(double[] iAff, double[] iLin)
        {
            var o = new double[12];
            for (int r = 0; r < 3; r++)
            {
                for (int c = 0; c < 3; c++) o[r * 4 + c] = iAff[r * 4] * iLin[c] + iAff[r * 4 + 1] * iLin[3 + c] + iAff[r * 4 + 2] * iLin[6 + c];
                o[r * 4 + 3] = iAff[r * 4 + 3];
            }
            return o;
        }

        /// <summary>給人讀的變換：at／pivot／rotate／scale／mirror（只列不是預設的）。</summary>
        public static string DescribeTransform(SCP_SculptModel m)
        {
            var a = new List<string> { "at " + Fmt(m.at) };
            if (m.pivot[0] != 0 || m.pivot[1] != 0 || m.pivot[2] != 0) a.Add("pivot " + Fmt(m.pivot));
            if (m.rotate[0] != 0 || m.rotate[1] != 0 || m.rotate[2] != 0) a.Add("rotate " + Fmt(m.rotate) + "°");
            if (m.scale[0] != 1 || m.scale[1] != 1 || m.scale[2] != 1) a.Add("scale " + (m.scale[0] == m.scale[1] && m.scale[1] == m.scale[2] ? G(m.scale[0]) : Fmt(m.scale)));
            if ((m.mirror ?? "").Length > 0) a.Add("mirror " + m.mirror);
            return string.Join("、", a);
        }

        public static string Fmt(IList<double> v) => G(v[0]) + "," + G(v[1]) + "," + G(v[2]);
        public static string G(double d) => d.ToString("0.####", CultureInfo.InvariantCulture);
    }
}
