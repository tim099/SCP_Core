// 區塊職責：子模型各自一份網格、同件共用（TASK-0509）—— 觀測頁 GPU 預覽的「分開」場景：
//          主作品只帶自己的格；每個關聯節點、每個外部模型是一個**實例**（指向一份共用的來源＋一個 3×4 矩陣）。
// 物理意義：Tim 2026-10-12：子模型不合併到主模型、同樣的子模型共用 mesh、開關只切那份 mesh 的顯示（不重建）。
//          拍板：關聯與外部模型兩種都算；只做觀測頁 GPU 預覽（export／CLI view 不動）；**各自完整顯示**
//          —— 子模型畫它自己的全部格子，跟主作品貼合處不互相剔除、重疊處兩份都在（同一件才真的能共用一份網格）。
//          ⇒ 跟合併版（SCP_SculptLinkComposer）刻意不同：沒有「自己的格優先、先到的贏」、沒有越界丟掉、沒有跨件的面剔除與 AO。
// 數值影響：來源的 Key 是渲染器快取網格的唯一依據 —— 關聯 ＝ 作品 id＋它那份空間的 revision（＋草稿）；
//          外部模型 ＝ obj 檔（路徑＋長度＋修改時間）＋軸向＋改色。Key 一樣 ⇒ 渲染器不重建、不重傳。
//          顯示（<see cref="Visibility"/>）是每幀的純函數 ⇒ 開關只改 bool，⛔ 不動來源、不動矩陣。
// 座標：矩陣把**來源自己的座標**換到組裝件座標（世界 x,y 地面、z 朝上；跟繩的 ToRoot 同一套連續座標：格 k 佔 [k, k+1]）。
// 方言：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SCP.Core.Sculpture
{
    /// <summary>一份共用的網格來源（關聯：那件作品自己的格；外部模型：軸向換過、還沒擺的三角形）。</summary>
    public sealed class SCP_SculptPartSource
    {
        /// <summary>網格快取鍵 —— 內容一樣 ⇔ Key 一樣（渲染器只認它）。</summary>
        public string Key = "";
        /// <summary>給人讀（作品 id／obj 檔名）。</summary>
        public string Label = "";
        /// <summary>關聯：那件作品自己的格（它自己的座標），顏色已夾在 1..255。</summary>
        public IReadOnlyList<SCP_SculptVoxel>? Voxels;
        /// <summary>外部模型：模型座標（軸向已換）的三角形。</summary>
        public SCP_SculptModelGeom? Model;
    }

    /// <summary>一個實例：用哪一份來源、擺在哪（3×4，列優先）。</summary>
    public sealed class SCP_SculptPartInstance
    {
        public const string KindLink = "link", KindModel = "model";
        public string Kind = KindLink;
        /// <summary>關聯：關聯路徑（甲板2/船長室）；外部模型：模型路徑（所在關聯路徑／名字）。</summary>
        public string Path = "";
        /// <summary>外部模型所在那一層的關聯路徑（組裝件自己 ＝ 空）；關聯 ＝ 自己的路徑。</summary>
        public string Frame = "";
        /// <summary>關聯：links.json 的 enabled（顯示的預設）；外部模型：永遠 true（作品裡設為關的不列進來）。</summary>
        public bool Enabled = true;
        public int Source;
        /// <summary>來源座標 → 組裝件座標（3×4，列優先）。</summary>
        public double[] ToWorld = SCP_SculptParts.Identity();
    }

    /// <summary>一次觀測的子模型：共用來源＋實例（＋讀數用的說明）。⚠ 交給渲染器之後不准再改（場景不可變）。</summary>
    public sealed class SCP_SculptParts
    {
        public readonly List<SCP_SculptPartSource> Sources = new List<SCP_SculptPartSource>();
        public readonly List<SCP_SculptPartInstance> Instances = new List<SCP_SculptPartInstance>();
        /// <summary>沒列進來的（壞掉的模型、讀不了的關聯）—— 一行一個原因。</summary>
        public readonly List<string> Notes = new List<string>();

        public static double[] Identity() => new double[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };

        /// <summary>3×4 的線性部分行列式（&lt; 0 ＝ 鏡像：繞序反了）。</summary>
        public static double Det3(double[] a) => a[0] * (a[5] * a[10] - a[6] * a[9]) - a[1] * (a[4] * a[10] - a[6] * a[8]) + a[2] * (a[4] * a[9] - a[5] * a[8]);

        /// <summary>某一個關聯節點的矩陣：它自己的座標 → 組裝件（逐層過 <see cref="SCP_SculptRopes.ThroughLink"/>，跟繩、模型同一支）。</summary>
        public static double[] LinkMatrix(string iPath, Dictionary<string, SCP_SculptLinkNode> iIndex)
            => SCP_SculptModels.AffineOf(p => SCP_SculptRopes.ToRoot(iPath, p, iIndex));

        // ───────────────────────── 顯示（每幀，純函數）─────────────────────────

        /// <summary>這一幀的顯示：主作品自己的格、每個實例。⛔ 不動來源與矩陣 —— 開關只切顯示（Tim 2026-10-12）。</summary>
        public sealed class Shown
        {
            public bool Own = true;
            public bool[] Visible = new bool[0];
            /// <summary>選擇裡點名、但這次沒有的路徑（⛔ 打錯字不會變成「什麼都沒關」—— 呼叫端要說出來）。</summary>
            public List<string> Unknown = new List<string>();
        }

        /// <summary>
        /// 選擇 → 顯示。關聯：每一層祖先都要被選到（跟組裝器一層層往下走、<see cref="SCP_SculptRopes.PathDrawn"/> 同一個判準）；
        /// 外部模型：所在那一層有畫、沒有 models=0、沒被 hide_models 點名；主作品：沒有 hide_links=self。
        /// </summary>
        public static Shown Visibility(SCP_SculptParts iParts, SCP_SculptLinkSelection iLinks, SCP_SculptModelSelection iModels)
        {
            var aOut = new Shown { Own = !iLinks.HideSelf, Visible = new bool[iParts.Instances.Count] };
            var aEnabled = new Dictionary<string, bool>(StringComparer.Ordinal);
            var aModelPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var i in iParts.Instances)
            {
                if (i.Kind == SCP_SculptPartInstance.KindLink) aEnabled[i.Path] = i.Enabled;
                else aModelPaths.Add(i.Path);
            }
            bool Drawn(string iPath)
            {
                if (iPath.Length == 0) return true;
                string aPrefix = "";
                foreach (string s in iPath.Split('/'))
                {
                    aPrefix = aPrefix.Length == 0 ? s : aPrefix + "/" + s;
                    if (!aEnabled.TryGetValue(aPrefix, out bool en) || !iLinks.Includes(aPrefix, en)) return false;
                }
                return true;
            }
            for (int k = 0; k < iParts.Instances.Count; k++)
            {
                var i = iParts.Instances[k];
                aOut.Visible[k] = i.Kind == SCP_SculptPartInstance.KindLink
                    ? Drawn(i.Path)
                    : Drawn(i.Frame) && !iModels.None && !iModels.Hide.Contains(i.Path);
            }
            var aNamed = new List<string>(iLinks.Hide);
            if (iLinks.Only != null) aNamed.AddRange(iLinks.Only);
            foreach (string p in aNamed) if (!aEnabled.ContainsKey(p)) aOut.Unknown.Add("關聯 `" + p + "`");
            foreach (string p in iModels.Hide) if (!aModelPaths.Contains(p)) aOut.Unknown.Add("模型 `" + p + "`");
            return aOut;
        }

        // ───────────────────────── 建（觀測準備時；呼叫端握關聯樹上所有作品的鎖）─────────────────────────

        /// <summary>
        /// 關聯樹上**每一個**節點（含作品裡設為關的 —— 頁面可以這次把它打開）各一個實例；同一件作品只讀一次、只一份來源。
        /// 看草稿（<paramref name="iUseDrafts"/>）且那件有開著的草稿 ⇒ 用草稿（Key 會不同）。讀不了 ⇒ ArgumentException（呼叫端轉 exit 2）。
        /// </summary>
        public void AddLinks(SCP_SculptWorks iWorks, List<SCP_SculptLinkNode> iTree, bool iUseDrafts)
        {
            var aIndex = SCP_SculptRopes.IndexTree(iTree);
            var aByWork = new Dictionary<string, int>(StringComparer.Ordinal);
            void Walk(List<SCP_SculptLinkNode> iNodes)
            {
                foreach (var n in iNodes)
                {
                    if (!aByWork.TryGetValue(n.Card.id, out int aSrc))
                    {
                        aSrc = Sources.Count;
                        Sources.Add(LoadLinkSource(iWorks, n.Card, iUseDrafts));
                        aByWork[n.Card.id] = aSrc;
                    }
                    Instances.Add(new SCP_SculptPartInstance
                    {
                        Kind = SCP_SculptPartInstance.KindLink, Path = n.Path, Frame = n.Path, Enabled = n.Link.enabled,
                        Source = aSrc, ToWorld = LinkMatrix(n.Path, aIndex),
                    });
                    Walk(n.Children);
                }
            }
            Walk(iTree);
        }

        static SCP_SculptPartSource LoadLinkSource(SCP_SculptWorks iWorks, SCP_SculptWork iCard, bool iUseDrafts)
        {
            SCP_SculptSpace aSpace;
            bool aDraft = false;
            try
            {
                SCP_SculptPaths aPaths = iWorks.SpacePaths(iCard.id);
                SCP_SculptDraftInfo? aInfo = iUseDrafts ? SCP_SculptDraft.Read(aPaths) : null;
                if (aInfo != null) { aSpace = SCP_SculptStore.Load(SCP_SculptDraft.Paths(aPaths, aInfo, iCard.Dimensions), false); aDraft = true; }
                else aSpace = SCP_SculptStore.Load(aPaths, false);
            }
            catch (Exception e) when (e is SCP_SculptReplayException || e is InvalidOperationException || e is IOException)
            {
                throw new ArgumentException("關聯的作品 `" + iCard.id + "` 讀不了：" + e.Message);
            }
            var aVox = new List<SCP_SculptVoxel>(aSpace.Voxels.Count);
            foreach (var v in aSpace.Voxels.Entries())
            {
                int c = v.Color;
                if (c < 1 || c > 255) c = 4;   // 同 BuildPlan 的 OutOfRangeColors：場景裡 0 ＝ 空 ⇒ 映到近黑
                aVox.Add(new SCP_SculptVoxel(v.X, v.Y, v.Z, (byte)c));
            }
            string aRev = SCP_SculptWorks.Revision(aSpace, iCard.Dimensions);
            return new SCP_SculptPartSource { Key = "link:" + iCard.id + "@" + aRev + (aDraft ? "~draft" : ""), Label = iCard.id + (aDraft ? "（草稿）" : ""), Voxels = aVox };
        }

        static readonly object s_GeomLock = new object();
        static readonly Dictionary<string, SCP_SculptModelGeom> s_Geoms = new Dictionary<string, SCP_SculptModelGeom>(StringComparer.Ordinal);
        const int MaxCachedGeoms = 64;

        /// <summary>
        /// 外部模型：每一個讀到網格的（<see cref="SCP_SculptModels.Resolve"/> 帶 bake=false）各一個實例；同一個 obj＋軸向＋改色只一份來源。
        /// 壞掉的、沒讀網格的 ⇒ 不列，原因進 <see cref="Notes"/>。
        /// </summary>
        public void AddModels(SCP_SculptModelSet iSet)
        {
            var aByKey = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in iSet.Models)
            {
                if (r.State == SCP_SculptModelReading.Disabled) continue;   // 作品裡設為關（長期開關）⇒ 頁面也不給開
                if (r.Mesh == null || r.ToRoot == null)
                {
                    Notes.Add("模型 `" + r.Path + "` 沒畫：" + (r.Reason.Length > 0 ? r.Reason : r.State));
                    continue;
                }
                string aKey = ModelKey(r);
                if (!aByKey.TryGetValue(aKey, out int aSrc))
                {
                    aSrc = Sources.Count;
                    Sources.Add(new SCP_SculptPartSource { Key = aKey, Label = Path.GetFileName(r.ObjPath), Model = LocalGeom(aKey, r) });
                    aByKey[aKey] = aSrc;
                }
                Instances.Add(new SCP_SculptPartInstance
                {
                    Kind = SCP_SculptPartInstance.KindModel, Path = r.Path, Frame = r.Frame, Enabled = true,
                    Source = aSrc, ToWorld = (double[])r.ToRoot.Clone(),
                });
            }
        }

        /// <summary>
        /// 模型來源的 Key（＋軸向＋改色）：
        /// file 來源 ⇒ 匯入時記下的 sha256＋位元組數 —— 同一個 obj 用兩個名字加進來會被複製成兩份檔，內容一樣就共用一份網格；
        ///   ⚠ 檔案現在的大小跟記錄對不上（被手改過）⇒ 退回「路徑＋長度＋修改時間」（⛔ 不拿舊的 sha 去認新的內容）。
        /// work 來源 ⇒ 快取檔（檔名已含來源作品整條相依鏈的 key）＋長度＋修改時間。
        /// </summary>
        public static string ModelKey(SCP_SculptModelReading r)
        {
            var fi = new FileInfo(r.ObjPath);
            long aLen = fi.Exists ? fi.Length : -1, aTicks = fi.Exists ? fi.LastWriteTimeUtc.Ticks : 0;
            var a = new List<string>();
            foreach (double d in r.Axes) a.Add(d.ToString("R", CultureInfo.InvariantCulture));
            string aTail = "|" + string.Join(",", a) + "|" + (r.Model.color ?? "");
            if (r.Model.source == SCP_SculptModels.SourceFile && r.Model.sha256.Length > 0 && r.Model.bytes == aLen)
                return "model:sha:" + r.Model.sha256 + "|" + aLen + aTail;
            return "model:" + Path.GetFullPath(r.ObjPath).Replace('\\', '/') + "|" + aLen + "|" + aTicks + aTail;
        }

        /// <summary>模型座標的三角形（只套軸向，⛔ 不擺）—— 同一個 Key 在行程內只建一次。</summary>
        static SCP_SculptModelGeom LocalGeom(string iKey, SCP_SculptModelReading r)
        {
            lock (s_GeomLock) { if (s_Geoms.TryGetValue(iKey, out SCP_SculptModelGeom? aHit)) return aHit; }
            double[] A = r.Axes;
            var aAxes = new double[] { A[0], A[1], A[2], 0, A[3], A[4], A[5], 0, A[6], A[7], A[8], 0 };
            SCP_SculptModelGeom g = SCP_SculptModels.BuildGeom(Path.GetFileName(r.ObjPath), r.Mesh!, aAxes, r.Model.color ?? "");
            lock (s_GeomLock)
            {
                if (s_Geoms.Count >= MaxCachedGeoms) s_Geoms.Clear();
                s_Geoms[iKey] = g;
            }
            return g;
        }
    }
}
