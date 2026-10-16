// 區塊職責：場景裡的燈在哪（TASK-0650）—— 外部模型與關聯的 meta 帶 kind=light（或 lantern）⇒ 一盞點光源。
// 物理意義：跟 Blockside 60_render.js 同一套慣例（isLamp／lampAt）：
//          ・誰是燈：meta kind ＝ light｜lantern。
//          ・燈心：來源作品的固定點 `wick`（造船規格：火焰中心）換到組裝件座標；沒有 ⇒ 外框中心。
//            外部模型：只有作品來源（source=work）有固定點；外框中心 ＝ obj 外框中心經過模型矩陣（仿射把中點送到中點 ⇒ 合併與分開畫同一個值）。
//            關聯：被關聯作品的固定點；外框中心 ＝ 那件作品的空間中心。兩種都照關聯路徑一層層換到組裝件（同繩的 ToRoot）。
//          ⇒ 燈籠的玻璃（palette 245）在觀測頁只是顏色，有了這一份才會照亮旁邊。
// 數值影響：只算位置，⛔ 不動 voxel、不動模型、不影響讀數與匯出。固定點檔讀不了 ⇒ 那盞放外框中心並記一行（⛔ 不默默少一盞）。
// 方言：C# 9 / netstandard2.1。
#nullable enable
using System;
using System.Collections.Generic;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    public static class SCP_SculptLampFinder
    {
        public const string KindKey = "kind";
        public const string WickAnchor = "wick";
        public const string AtWick = "wick", AtCenter = "center";

        /// <summary>meta 的 kind 是 light 或 lantern（Blockside isLamp）。</summary>
        public static bool IsLamp(SortedDictionary<string, SCP_JsonData> iMeta)
            => iMeta.TryGetValue(KindKey, out SCP_JsonData? v) && v.IsString && (v.AsString() == "light" || v.AsString() == "lantern");

        /// <summary>
        /// 收場景裡的燈：<paramref name="iModels"/> 裡 <paramref name="iModelIn"/> 收的模型＋<paramref name="iTree"/> 裡 <paramref name="iLinkIn"/> 收的關聯，
        /// meta 是燈的各一盞（照得到 <paramref name="iRadius"/> 格）。讀不了固定點的記進 <paramref name="ioNotes"/>（那盞放外框中心）。
        /// </summary>
        public static List<SCP_SculptLampPoint> Find(SCP_SculptWorks iWorks, SCP_SculptModelSet? iModels, Func<SCP_SculptModelReading, bool> iModelIn,
                                                     List<SCP_SculptLinkNode>? iTree, Func<SCP_SculptLinkNode, bool> iLinkIn,
                                                     double iRadius, List<string> ioNotes)
        {
            var aOut = new List<SCP_SculptLampPoint>();
            var aAnchors = new Dictionary<string, SCP_SculptAnchorFile?>(StringComparer.Ordinal);
            double[]? Wick(string iWorkId, string iWhat)
            {
                if (!aAnchors.TryGetValue(iWorkId, out SCP_SculptAnchorFile? f))
                {
                    try { f = SCP_SculptRopes.ReadAnchors(iWorks.SpacePaths(iWorkId)); }
                    catch (Exception e) when (e is InvalidOperationException || e is ArgumentException)
                    { f = null; ioNotes.Add("燈 " + iWhat + "：作品 " + iWorkId + " 的固定點讀不了（" + e.Message + "）⇒ 放外框中心"); }
                    aAnchors[iWorkId] = f;
                }
                SCP_SculptAnchor? a = f?.anchors.Find(x => x.name == WickAnchor);
                return a == null ? null : new[] { a.at[0] + 0.5, a.at[1] + 0.5, a.at[2] + 0.5 };
            }
            void Add(string iPath, double[] p, string iFrom) => aOut.Add(new SCP_SculptLampPoint { Path = iPath, X = p[0], Y = p[1], Z = p[2], Radius = iRadius, From = iFrom });

            if (iModels != null)
                foreach (var r in iModels.Models)
                {
                    if (!IsLamp(r.Model.meta) || r.ToRoot == null || !iModelIn(r)) continue;
                    double[]? w = r.Model.source == SCP_SculptModels.SourceWork ? Wick(r.Model.work, "`" + r.Path + "`") : null;
                    if (w != null) { Add(r.Path, SCP_SculptModels.Apply(r.ToRoot, w), AtWick); continue; }
                    if (r.Mesh == null) { ioNotes.Add("燈 `" + r.Path + "`：沒有網格、也沒有固定點 wick ⇒ 不點"); continue; }
                    var c = new double[3];
                    for (int i = 0; i < 3; i++) c[i] = (r.Mesh.Min[i] + r.Mesh.Max[i]) * 0.5;
                    Add(r.Path, SCP_SculptModels.Apply(SCP_SculptModels.Compose(r.ToRoot, r.Axes), c), AtCenter);
                }

            if (iTree != null)
            {
                Dictionary<string, SCP_SculptLinkNode> aIndex = SCP_SculptRopes.IndexTree(iTree);
                void Walk(List<SCP_SculptLinkNode> iNodes)
                {
                    foreach (var n in iNodes)
                    {
                        if (IsLamp(n.Link.meta) && iLinkIn(n))
                        {
                            double[]? w = Wick(n.Card.id, "`" + n.Path + "`");
                            if (w != null) Add(n.Path, SCP_SculptRopes.ToRoot(n.Path, w, aIndex), AtWick);
                            else Add(n.Path, SCP_SculptRopes.ToRoot(n.Path, new[] { n.Card.SizeX * 0.5, n.Card.SizeY * 0.5, n.Card.SizeZ * 0.5 }, aIndex), AtCenter);
                        }
                        Walk(n.Children);
                    }
                }
                Walk(iTree);
            }
            return aOut;
        }

        /// <summary>一句讀數：「N 盞（固定點 a、外框中心 b）」。</summary>
        public static string Describe(List<SCP_SculptLampPoint> iPoints)
        {
            int w = 0;
            foreach (var p in iPoints) if (p.From == AtWick) w++;
            return iPoints.Count + " 盞（固定點 wick " + w + "、外框中心 " + (iPoints.Count - w) + "）";
        }
    }
}
