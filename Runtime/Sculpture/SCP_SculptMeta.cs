// 區塊職責：外部模型與關聯的 meta（TASK-0517）—— 作者給部件貼的一小包「這是什麼」：平的 鍵 → 字串或數字（例 kind=gun、gun=24l、powder_lb=9000）。
// 物理意義：給下游（遊戲、匯出）認部件用；引擎自己不讀它的意思 —— 不影響組裝、觀測、繩、帆。
//          存在 models.json／links.json 每一筆的 `meta` 物件裡（值的型別原樣保存：數字就是 JSON 數字，⛔ 不轉成字串）。
//          ⚠ JSON 對映器沒有「原樣保留一段 JSON」的型別 ⇒ 成員掛 [SCP_Ignore]，由 models.json／links.json 的讀寫那一層自己讀寫（本檔的 ReadFrom／WriteTo）。
//          ⚠ 用到 meta 的檔要升 schema（models 2、links 3）：舊版 senate.exe 沒有這個成員，讀到會**默默丟掉**、下一次存檔就清光 —— 升號讓它大聲讀不了。
// 數值影響：只多一個欄位；沒有 meta 的檔照舊（schema 不變、逐位元組一樣）。
// 失敗處置：檔上的 meta 不是物件／值不是字串或數字／鍵不合法 ⇒ InvalidOperationException（⛔ 不當成沒有：寫回去會清光）；CLI 給的壞掉 ⇒ 一句人話（呼叫端轉 exit 2、未寫入）。
// 方言：C# 9 / netstandard2.1。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    public static class SCP_SculptMeta
    {
        public const int MaxEntries = 64, MaxKeyLength = 64, MaxStringLength = 1024;
        /// <summary>CLI 的 meta=none ＝ 全清。</summary>
        public const string ClearAll = "none";

        public static SortedDictionary<string, SCP_JsonData> New() => new SortedDictionary<string, SCP_JsonData>(StringComparer.Ordinal);

        /// <summary>鍵：非空、≤ 64 字、不含空白與控制字元、不含 `:` `,` `|` `=` `"`（CLI 與讀數用得到的分隔字元）。null ＝ 合法。</summary>
        public static string? CheckKey(string iKey)
        {
            if (iKey.Length == 0) return "meta 的鍵不能是空的";
            if (iKey.Length > MaxKeyLength) return "meta 的鍵太長（`" + iKey.Substring(0, 16) + "…`，上限 " + MaxKeyLength + " 字）";
            foreach (char ch in iKey)
                if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ":,|=\"".IndexOf(ch) >= 0)
                    return "meta 的鍵 `" + iKey + "` 不可含空白、控制字元或 : , | = \"";
            return null;
        }

        /// <summary>值：字串（≤ 1024 字）或有限的數字。null ＝ 合法。</summary>
        public static string? CheckValue(string iKey, SCP_JsonData iValue)
        {
            if (iValue.Type == SCP_JsonType.String)
                return iValue.AsString().Length > MaxStringLength ? "meta `" + iKey + "` 的字串太長（上限 " + MaxStringLength + " 字）" : null;
            if (iValue.Type == SCP_JsonType.Number)
            {
                double d;
                try { d = iValue.AsDouble(); }
                catch (Exception) { return "meta `" + iKey + "` 的數字讀不了"; }
                return double.IsNaN(d) || double.IsInfinity(d) ? "meta `" + iKey + "` 不是有限的數" : null;
            }
            return "meta `" + iKey + "` 的值要是字串或數字（got " + iValue.Type + "）—— ⛔ 不收巢狀、陣列、布林、null";
        }

        /// <summary>檔上的 `meta` 節點讀進 <paramref name="ioMeta"/>（先清空）。節點不存在 ⇒ 空。回 null ＝ 成功；否則是原因。</summary>
        public static string? ReadFrom(SCP_JsonData iNode, SortedDictionary<string, SCP_JsonData> ioMeta)
        {
            ioMeta.Clear();
            if (!iNode.Exists) return null;
            if (!iNode.IsObject) return "meta 要是物件 {鍵: 字串或數字}";
            if (iNode.Count > MaxEntries) return "meta 超過 " + MaxEntries + " 個鍵";
            foreach (string k in iNode.Keys)
            {
                if (CheckKey(k) is string aBadKey) return aBadKey;
                if (CheckValue(k, iNode[k]) is string aBadVal) return aBadVal;
                ioMeta[k] = Copy(iNode[k]);
            }
            return null;
        }

        /// <summary>寫回檔：有 meta 才寫（空的不留 `meta: {}` ⇒ 沒用到的檔逐位元組跟以前一樣）。</summary>
        public static void WriteTo(SCP_JsonData ioOwner, SortedDictionary<string, SCP_JsonData> iMeta)
        {
            if (iMeta.Count == 0) { ioOwner.Remove("meta"); return; }
            ioOwner.Set("meta", ToNode(iMeta));
        }

        /// <summary>依鍵排序的一個 JSON 物件（值原樣：字串就是字串、數字就是數字）。</summary>
        public static SCP_JsonData ToNode(SortedDictionary<string, SCP_JsonData> iMeta)
        {
            var aObj = SCP_JsonData.NewObject();
            foreach (var kv in iMeta) aObj.Set(kv.Key, Copy(kv.Value));
            return aObj;
        }

        /// <summary>
        /// CLI 的 meta=：一個 JSON 物件，**合併**進現有的（給的鍵覆寫、值給 null ⇒ 刪那個鍵）；`none` ⇒ 全清。
        /// 回 null ＝ 合法（<paramref name="ioMeta"/> 已改好）；否則是一句人話、<paramref name="ioMeta"/> 不動。
        /// </summary>
        public static string? Merge(SortedDictionary<string, SCP_JsonData> ioMeta, string iArg)
        {
            string a = (iArg ?? "").Trim();
            if (a == ClearAll) { ioMeta.Clear(); return null; }
            SCP_JsonData aNode;
            try { aNode = SCP_JsonParser.Parse(a, false); }
            catch (Exception e) { return "meta 要是一個 JSON 物件（例 {\"kind\":\"gun\",\"powder_lb\":9000}；全清 meta=none）—— 讀不了：" + e.Message; }
            if (!aNode.IsObject) return "meta 要是一個 JSON 物件（例 {\"kind\":\"gun\"}）；got " + aNode.Type;
            var aNext = New();
            foreach (var kv in ioMeta) aNext[kv.Key] = kv.Value;
            foreach (string k in aNode.Keys)
            {
                if (CheckKey(k) is string aBadKey) return aBadKey;
                SCP_JsonData v = aNode[k];
                if (v.IsNull) { aNext.Remove(k); continue; }
                if (CheckValue(k, v) is string aBadVal) return aBadVal;
                aNext[k] = Copy(v);
            }
            if (aNext.Count > MaxEntries) return "meta 超過 " + MaxEntries + " 個鍵";
            ioMeta.Clear();
            foreach (var kv in aNext) ioMeta[kv.Key] = kv.Value;
            return null;
        }

        /// <summary>讀數一行：`kind=gun, powder_lb=9000`（字串加引號只在含逗號或空白時）。空 ⇒ ""。</summary>
        public static string Describe(SortedDictionary<string, SCP_JsonData> iMeta)
        {
            if (iMeta.Count == 0) return "";
            var sb = new StringBuilder();
            foreach (var kv in iMeta)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(kv.Key).Append('=');
                if (kv.Value.Type == SCP_JsonType.String)
                {
                    string s = kv.Value.AsString();
                    sb.Append(s.IndexOfAny(new[] { ',', ' ' }) >= 0 || s.Length == 0 ? "\"" + s + "\"" : s);
                }
                else sb.Append(kv.Value.ToJson(false));
            }
            return sb.ToString();
        }

        static SCP_JsonData Copy(SCP_JsonData v) =>
            v.Type == SCP_JsonType.String ? SCP_JsonData.NewString(v.AsString()) : SCP_JsonData.NewNumber(v.ToJson(false));

        /// <summary>點的 JSON（[x,y,z]，0.0001 格）。</summary>
        public static SCP_JsonData Point(double[] p)
        {
            var aArr = SCP_JsonData.NewArray();
            foreach (double d in p) aArr.Add(SCP_JsonData.NewNumber(Num(d)));
            return aArr;
        }

        internal static string Num(double d)
        {
            string s = Math.Round(d, 4).ToString("0.####", CultureInfo.InvariantCulture);
            return s == "-0" ? "0" : s;
        }
    }

    // 區塊職責：部件清單（TASK-0517，`export format=parts`）—— 給下游（遊戲、VoxelKit）一份機器讀的「這件作品由哪些部件組成、每個部件是什麼、關鍵點在哪」。
    // 物理意義：每條關聯（整棵樹、含巢狀）與每個外部模型一筆：路徑、來源、組裝件座標的外框、meta、points（來源作品的固定點換到組裝件座標）。
    //          座標一律是組裝件的連續座標（格 k 佔 [k, k+1]；固定點取格心 k＋0.5）—— 跟繩的端點同一個公式（關聯：<see cref="SCP_SculptRopes.ToRoot"/>；模型：它的 3×4 矩陣）。
    //          ⇒ 「砲軸」＝ muzzle 與 breech 兩點連線，下游不用再從外框猜朝向。
    // 數值影響：只讀 links.json／models.json／anchors.json 與書卡（模型外框要網格 ⇒ 呼叫端給的模型組要讀過網格）；不讀 voxel、不改任何檔。
    // 失敗處置：某個部件的固定點檔讀不了 ⇒ 那一筆 points_error 寫原因（⛔ 不靜默給空的 points）。
    public static class SCP_SculptPartsExport
    {
        public const string Format = "sculpt-parts";
        public const int Version = 1;

        public static SCP_JsonData Build(SCP_SculptWorks iWorks, SCP_SculptWork iCard, List<SCP_SculptLinkNode> iTree,
                                         SCP_SculptLinkSelection iSel, SCP_SculptModelSet iModels)
        {
            var aRoot = SCP_JsonData.NewObject();
            aRoot.Set("format", Format);
            aRoot.Set("version", Version);
            aRoot.Set("work", iCard.id);
            aRoot.Set("title", iCard.title);
            aRoot.Set("owner", iCard.owner);
            var aSize = SCP_JsonData.NewArray();
            aSize.Add(iCard.SizeX); aSize.Add(iCard.SizeY); aSize.Add(iCard.SizeZ);
            aRoot.Set("size", aSize);
            aRoot.Set("meters_per_voxel", SCP_JsonData.NewNumber(SCP_SculptMeta.Num(iCard.MetersPerVoxel)));
            var aCache = new Dictionary<string, SCP_SculptAnchorFile>(StringComparer.Ordinal);
            SetPoints(aRoot, iWorks, iCard.id, aCache, p => p);

            var aIndex = SCP_SculptRopes.IndexTree(iTree);
            var aLinks = SCP_JsonData.NewArray();
            void Walk(List<SCP_SculptLinkNode> iNodes)
            {
                foreach (var n in iNodes)
                {
                    var o = SCP_JsonData.NewObject();
                    o.Set("path", n.Path);
                    o.Set("work", n.Card.id);
                    o.Set("title", n.Card.title);
                    o.Set("owner", n.Card.owner);
                    o.Set("turn", n.Link.turn);
                    o.Set("mirror", n.Link.mirror ?? "");
                    o.Set("enabled", n.Link.enabled);
                    o.Set("drawn", SCP_SculptRopes.PathDrawn(n.Path, aIndex, iSel));
                    // 外框：那件作品自己的框（現在的尺寸）八個角換過去取極值
                    double[] aMin = { double.MaxValue, double.MaxValue, double.MaxValue }, aMax = { double.MinValue, double.MinValue, double.MinValue };
                    for (int k = 0; k < 8; k++)
                    {
                        double[] p = SCP_SculptRopes.ToRoot(n.Path, new double[] { (k & 1) != 0 ? n.Card.SizeX : 0, (k & 2) != 0 ? n.Card.SizeY : 0, (k & 4) != 0 ? n.Card.SizeZ : 0 }, aIndex);
                        for (int i = 0; i < 3; i++) { aMin[i] = Math.Min(aMin[i], p[i]); aMax[i] = Math.Max(aMax[i], p[i]); }
                    }
                    o.Set("box", Box(aMin, aMax));
                    o.Set("meta", SCP_SculptMeta.ToNode(n.Link.meta));
                    string aPath = n.Path;
                    SetPoints(o, iWorks, n.Card.id, aCache, p => SCP_SculptRopes.ToRoot(aPath, p, aIndex));
                    aLinks.Add(o);
                    Walk(n.Children);
                }
            }
            Walk(iTree);
            aRoot.Set("links", aLinks);

            var aModels = SCP_JsonData.NewArray();
            foreach (var m in iModels.Models)
            {
                var o = SCP_JsonData.NewObject();
                o.Set("path", m.Path);
                o.Set("frame", m.Frame);
                o.Set("name", m.Model.name);
                o.Set("source", m.Model.source);
                if (m.Model.source == SCP_SculptModels.SourceWork) o.Set("work", m.Model.work); else o.Set("file", m.Model.file);
                o.Set("state", m.State);
                if (m.Reason.Length > 0) o.Set("reason", m.Reason);
                if (m.Min != null && m.Max != null) o.Set("box", Box(m.Min, m.Max));
                o.Set("meta", SCP_SculptMeta.ToNode(m.Model.meta));
                double[]? aToRoot = m.ToRoot;
                if (m.Model.source == SCP_SculptModels.SourceWork && aToRoot != null)
                    SetPoints(o, iWorks, m.Model.work, aCache, p => SCP_SculptModels.Apply(aToRoot, p));
                else o.Set("points", SCP_JsonData.NewObject());
                aModels.Add(o);
            }
            aRoot.Set("models", aModels);
            return aRoot;
        }

        static SCP_JsonData Box(double[] iMin, double[] iMax)
        {
            var a = SCP_JsonData.NewArray();
            for (int i = 0; i < 3; i++) a.Add(SCP_JsonData.NewNumber(SCP_SculptMeta.Num(iMin[i])));
            for (int i = 0; i < 3; i++) a.Add(SCP_JsonData.NewNumber(SCP_SculptMeta.Num(iMax[i])));
            return a;
        }

        /// <summary>作品 <paramref name="iWorkId"/> 的固定點（格心）照 <paramref name="iToRoot"/> 換到組裝件，寫進 <paramref name="ioOwner"/> 的 points（依名字排序）。</summary>
        static void SetPoints(SCP_JsonData ioOwner, SCP_SculptWorks iWorks, string iWorkId, Dictionary<string, SCP_SculptAnchorFile> ioCache, Func<double[], double[]> iToRoot)
        {
            var aPts = SCP_JsonData.NewObject();
            if (!ioCache.TryGetValue(iWorkId, out SCP_SculptAnchorFile? aFile))
            {
                try { aFile = SCP_SculptRopes.ReadAnchors(iWorks.SpacePaths(iWorkId)); }
                catch (InvalidOperationException e) { ioOwner.Set("points", aPts); ioOwner.Set("points_error", e.Message); return; }
                ioCache[iWorkId] = aFile;
            }
            var aSorted = new List<SCP_SculptAnchor>(aFile.anchors);
            aSorted.Sort((a, b) => string.CompareOrdinal(a.name, b.name));
            foreach (var a in aSorted)
                aPts.Set(a.name, SCP_SculptMeta.Point(iToRoot(new[] { a.at[0] + 0.5, a.at[1] + 0.5, a.at[2] + 0.5 })));
            ioOwner.Set("points", aPts);
        }
    }
}
