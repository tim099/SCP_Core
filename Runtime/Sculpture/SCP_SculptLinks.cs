// 區塊職責：作品的關聯匯入（TASK-0494）—— 作品只記「哪一件作品放在我的哪裡、轉幾度、翻不翻面（TASK-0497）、開不開」，觀測時才把它們組進來。
// 物理意義：關聯存在作品資料夾的 links.json（作者寫的、入版控），⛔ 不寫進 events/ —— 一格 voxel 都不複製，
//          被關聯的作品改了，下一次觀測就是新樣子；沒更新的 senate.exe 看到的是沒有子件的作品（不會讀壞）。
//          「子作品其實跟獨立作品一樣，只是創作時切分出來的作品」（Tim 2026-10-11）⇒ 任何作品都能被關聯，別人的也行（Credit 照 assemble）。
//          巢狀：被關聯的作品自己也有 links.json ⇒ 一路組進來；選擇用路徑（甲板2/船長室）指到任一層。
// 數值影響：每件被關聯的作品讀它自己的 LoadSpace（＝ 它那份不入 git 的 sculpt_cache.json；沒變就不重播），讀的時候拿它的 _engine 鎖。
//          同一次組裝裡同一件作品只讀一次（菱形關聯不重讀）。
//          疊的順序：每一層都是「自己的格優先，關聯照清單順序、先到的贏」；被蓋掉的格數、越界的格數都計數＋附位置（⛔ 不靜默吃掉）。
// 失敗處置：links.json 讀不了 ⇒ 擋下（⛔ 不當成沒有關聯 —— 寫回去會清光）；成環（含隔好幾層）、被關聯的作品不存在、
//          選擇的路徑不存在 ⇒ ArgumentException／InvalidOperationException，呼叫端轉 exit 2。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Reflect;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptLink
    {
        public string name = "", work = "";
        /// <summary>放在哪（x,y,z）。⚠ 用 List 不用 int[]：JSON 對映器讀陣列是「清空再逐項加」，固定長度的陣列加不進去。</summary>
        public List<int> at = new List<int> { 0, 0, 0 };
        /// <summary>繞 Z 轉幾度（0／90／180／270），轉法與 assemble 相同（轉完原點放在 at）。</summary>
        public int turn;
        /// <summary>
        /// 鏡像（TASK-0497）：沿哪幾軸翻面 ——「」＝不翻、"x"／"y"／"z" 或組合（"xz"，正規化成 xyz 順序）。
        /// 先在零件**自己的框**（pivot）裡翻面、再 turn、再放到 at ⇒ 外框不變，放不放得進跟沒翻一樣。
        /// ⚠ 有任何一條用到鏡像，links.json 就寫 schema 2（見 <see cref="SCP_SculptLinks.Save"/>）。
        /// </summary>
        public string mirror = "";
        /// <summary>加關聯當下被關聯作品的尺寸（x,y,z）—— 轉向的軸心固定用它（⛔ 不用那件作品現在的尺寸：它長大了整件會平移）。</summary>
        public List<int> pivot = new List<int> { 1, 1, 1 };
        public bool enabled = true;
        public string created_at = "", persona = "", note = "";
        /// <summary>給下游認部件的 鍵 → 字串或數字（TASK-0517，<see cref="SCP_SculptMeta"/>）。⚠ 對映器不碰它：由 Read／Save 自己讀寫；用到就寫 schema 3。</summary>
        [SCP_Ignore] public SortedDictionary<string, SCP_JsonData> meta = SCP_SculptMeta.New();
    }

    public sealed class SCP_SculptLinkFile
    {
        public int schema = 1;
        public List<SCP_SculptLink> links = new List<SCP_SculptLink>();
    }

    /// <summary>觀測時選哪幾個關聯：預設 ＝ 每一層 enabled 的；All ＝ 不管 enabled；None ＝ 都不要；Only ＝ 只要這幾條路徑（＋它們的祖先＋它們底下 enabled 的）；Hide ＝ 這幾條路徑（連同底下）拿掉。</summary>
    public sealed class SCP_SculptLinkSelection
    {
        public bool All, None;
        /// <summary>hide_links=self：不畫本作品自己的格（只看關聯進來的，例：只看某一層甲板）。</summary>
        public bool HideSelf;
        public HashSet<string>? Only;
        public HashSet<string> Hide = new HashSet<string>(StringComparer.Ordinal);
        public bool IsDefault => !All && !None && !HideSelf && Only == null && Hide.Count == 0;

        static bool IsUnder(string iPath, string iAncestor) => iPath.Length > iAncestor.Length && iPath.StartsWith(iAncestor + "/", StringComparison.Ordinal);

        public bool Includes(string iPath, bool iEnabled)
        {
            foreach (string h in Hide) if (iPath == h || IsUnder(iPath, h)) return false;
            if (None) return false;
            if (All) return true;
            if (Only == null) return iEnabled;
            // ⚠ 要把整份點名看完才決定（⛔ 不在第一個對到的就回）：links=deck,deck/cabin 跟 links=deck/cabin,deck 必須一樣
            bool aUnderNamed = false;
            foreach (string o in Only)
            {
                if (iPath == o || IsUnder(o, iPath)) return true;           // 點名的，或點名那條的祖先（不經過它到不了）
                if (IsUnder(iPath, o)) aUnderNamed = true;                   // 點名那條底下的：照它自己的開關
            }
            return aUnderNamed && iEnabled;
        }

        /// <summary>CLI 的 links=none|all|a,b 與 hide_links=a,b（路徑用 / 分層）。</summary>
        public static SCP_SculptLinkSelection Parse(string iLinks, string iHide)
        {
            var aSel = new SCP_SculptLinkSelection();
            string aLinks = iLinks.Trim();
            if (aLinks == "none") aSel.None = true;
            else if (aLinks == "all") aSel.All = true;
            else if (aLinks.Length > 0)
            {
                aSel.Only = new HashSet<string>(StringComparer.Ordinal);
                foreach (string p in aLinks.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) aSel.Only.Add(NormPath(p));
            }
            foreach (string p in iHide.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string q = NormPath(p);
                if (q == SCP_SculptLinks.Self) aSel.HideSelf = true;
                else aSel.Hide.Add(q);
            }
            return aSel;
        }

        static string NormPath(string iPath) => iPath.Trim().Trim('/');
    }

    /// <summary>關聯樹的一個節點（只讀 links.json，不讀 voxel）。</summary>
    public sealed class SCP_SculptLinkNode
    {
        public string Path = "";
        public SCP_SculptLink Link = new SCP_SculptLink();
        public SCP_SculptWork Card = new SCP_SculptWork();
        public List<SCP_SculptLinkNode> Children = new List<SCP_SculptLinkNode>();
        /// <summary>被關聯作品現在的尺寸 ≠ 加關聯當下記的軸心（它被改過尺寸）—— 照樣用舊軸心轉，只是要讓人知道。</summary>
        public bool PivotDrift => Card.SizeX != Link.pivot[0] || Card.SizeY != Link.pivot[1] || Card.SizeZ != Link.pivot[2];
    }

    /// <summary>一次組裝的讀數：每條關聯放了幾格、被蓋掉幾格（重疊）、越界幾格；總數與樣本位置；用到的作品（攤平時記 Credit／版本）。</summary>
    public sealed class SCP_SculptComposeReport
    {
        public sealed class Row
        {
            public string Path = "", Work = "", Revision = "";
            public int Placed, Overlap, Clipped;
            /// <summary>這件用的是它開著的草稿（觀測 draft=1，TASK-0493×0494）。</summary>
            public bool Draft;
            public bool PivotDrift;
        }
        public List<Row> Rows = new List<Row>();
        public int Placed, Overlap, Clipped;
        public bool HideSelf;
        public List<string> Samples = new List<string>();
        /// <summary>有組進來的作品（去重）：id → (書卡, 它那份空間的 revision, 是不是草稿)。</summary>
        public Dictionary<string, (SCP_SculptWork Card, string Revision, bool Draft)> Works = new Dictionary<string, (SCP_SculptWork, string, bool)>(StringComparer.Ordinal);
    }

    public static class SCP_SculptLinks
    {
        public const int MaxDepth = 16;
        /// <summary>hide_links=self ＝ 不畫本作品自己的格。⇒ 也是保留字，不能當關聯名。</summary>
        public const string Self = "self";

        public static SCP_SculptLinkFile Read(SCP_SculptPaths iPaths)
        {
            string aFile = iPaths.LinksFile;
            if (!File.Exists(aFile)) return new SCP_SculptLinkFile();
            SCP_SculptLinkFile aOut;
            try
            {
                aOut = new SCP_SculptLinkFile { schema = 0 };
                var aOpt = new SCP_JsonMapOptions();
                SCP_JsonData aRoot = SCP_JsonParser.Parse(SCP.Core.Io.SCP_SharedFile.ReadAllText(aFile), false);
                SCP_JsonMapper.Populate(aOut, aRoot, aOpt);
                if (aOpt.Diagnostics.Count > 0) throw new InvalidOperationException(string.Join("; ", aOpt.Diagnostics));
                // meta（[SCP_Ignore]，對映器不碰）：照順序對回每一筆
                SCP_JsonData aList = aRoot["links"];
                for (int i = 0; i < aOut.links.Count && aList.IsArray && i < aList.Count; i++)
                    if (SCP_SculptMeta.ReadFrom(aList[i]["meta"], aOut.links[i].meta) is string aMetaBad)
                        throw new InvalidOperationException("關聯 `" + aOut.links[i].name + "` 的 " + aMetaBad);
            }
            catch (Exception e) { throw new InvalidOperationException("links.json 讀不了（" + aFile + "：" + e.Message + "）—— ⛔ 不當成沒有關聯，修好再用"); }
            if (aOut.schema != 1 && aOut.schema != SchemaWithMirror && aOut.schema != SchemaWithMeta) throw new InvalidOperationException("links.json 讀不了（" + aFile + "：schema=" + aOut.schema + "）");
            if (aOut.schema != SchemaWithMeta && aOut.links.Exists(l => l.meta.Count > 0)) throw new InvalidOperationException("links.json 讀不了（" + aFile + "：有 meta 卻是 schema " + aOut.schema + " —— 手改的檔？）");
            var aNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var l in aOut.links)
            {
                string? aBad = ValidName(l.name) ?? (aNames.Add(l.name) ? null : "名字重複");
                if (aBad == null && (l.at == null || l.at.Count != 3 || l.pivot == null || l.pivot.Count != 3 || l.pivot[0] < 1 || l.pivot[1] < 1 || l.pivot[2] < 1
                                     || (l.turn != 0 && l.turn != 90 && l.turn != 180 && l.turn != 270))) aBad = "at／pivot／turn 不合法";
                // ⛔ 檔上寫的就要是正規化的樣子（xyz 順序、不重複）—— 寫入端只會寫這種；別的樣子是手改的，猜它的意思等於猜幾何
                if (aBad == null && (NormalizeMirror(l.mirror ?? "", out string aNorm) != null || aNorm != l.mirror)) aBad = "mirror 不合法（`" + l.mirror + "`；要是 x／y／z 或組合，依 xyz 順序）";
                if (aBad != null) throw new InvalidOperationException("links.json 讀不了（" + aFile + "：關聯 `" + l.name + "` " + aBad + "）");
            }
            return aOut;
        }

        /// <summary>
        /// 有鏡像的 links.json 寫這個 schema（TASK-0497）。
        /// 🩸 為什麼要升號：<see cref="SCP_SculptLink"/> 沒有收容所 ⇒ 沒更新的 senate.exe 讀到 `mirror` 會**默默丟掉**，
        /// 組出來的是沒翻面的零件，而畫面看起來完全正常（同一件畫兩次在同一邊）。
        /// 舊版只認 schema 1 ⇒ 升號讓它**大聲讀不了**；沒用鏡像的檔維持 1，舊版照樣讀。
        /// </summary>
        public const int SchemaWithMirror = 2;

        /// <summary>
        /// 有 meta 的 links.json 寫這個 schema（TASK-0517；有沒有鏡像都一樣）。同 <see cref="SchemaWithMirror"/> 的理由：
        /// 認得 2 的舊版沒有 meta 成員 ⇒ 會默默丟掉、寫回去就清光 ⇒ 再升一號讓它大聲讀不了。
        /// </summary>
        public const int SchemaWithMeta = 3;

        /// <summary>
        /// 鏡像參數正規化：""／"none"／"0" ＝ 不翻；否則由 x／y／z 組成（可用逗號隔開、大小寫不拘），每軸至多一次 ⇒ 依 xyz 順序。
        /// 回 null ＝ 合法；否則是一句人話。
        /// </summary>
        public static string? NormalizeMirror(string iText, out string oNorm)
        {
            oNorm = "";
            string a = iText.Trim().ToLowerInvariant();
            if (a.Length == 0 || a == "none" || a == "0") return null;
            var aSeen = new HashSet<char>();
            foreach (char ch in a)
            {
                if (ch == ',' || ch == ' ') continue;
                if (ch != 'x' && ch != 'y' && ch != 'z') return "mirror 只能是 x／y／z 或它們的組合（例 x、xz）；got '" + iText + "'";
                if (!aSeen.Add(ch)) return "mirror 同一軸寫了兩次（'" + iText + "'）—— 翻兩次等於沒翻，⛔ 不猜你要哪個";
            }
            oNorm = (aSeen.Contains('x') ? "x" : "") + (aSeen.Contains('y') ? "y" : "") + (aSeen.Contains('z') ? "z" : "");
            return null;
        }

        public static void Save(SCP_SculptPaths iPaths, SCP_SculptLinkFile iFile)
        {
            iFile.schema = iFile.links.Exists(l => l.meta.Count > 0) ? SchemaWithMeta
                         : iFile.links.Exists(l => !string.IsNullOrEmpty(l.mirror)) ? SchemaWithMirror : 1;
            string aFile = iPaths.LinksFile;
            string aTmp = aFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                SCP_JsonData aRoot = SCP_JsonMapper.ToJson(iFile);
                SCP_JsonData aList = aRoot["links"];
                for (int i = 0; i < iFile.links.Count; i++) SCP_SculptMeta.WriteTo(aList[i], iFile.links[i].meta);
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(aRoot, true) + "\n", new UTF8Encoding(false));
                SCP.Core.Io.SCP_SharedFile.Replace(aTmp, aFile);
            }
            finally { try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch (Exception) { } }
        }

        /// <summary>關聯名：非空、不含 : , | / \ 與換行（/ 留給巢狀路徑）、不是保留字。null ＝ 合法。</summary>
        public static string? ValidName(string iName)
        {
            string a = iName.Trim();
            if (a.Length == 0) return "關聯要有名字（name=）";
            if (a != iName) return "關聯名字前後不可有空白";
            if (a.IndexOfAny(new[] { ':', ',', '|', '/', '\\', '\n', '\r' }) >= 0) return "關聯名字不可含 `:` `,` `|` `/` `\\` 或換行（`/` 留給巢狀路徑：甲板2/船長室）";
            if (a == "none" || a == "all" || a == Self || a == SCP_SculptMarks.Start || a == SCP_SculptMarks.Now) return "`" + a + "` 是保留字（links=none|all、hide_links=self…），不能當關聯名";
            return null;
        }

        /// <summary>
        /// 讀整棵關聯樹（只讀 links.json 與書卡，不讀 voxel、不拿鎖）。同一件作品可以出現很多次（例：28 門砲都關聯同一件 cannon、菱形）；
        /// 成環只看**祖先**（遞迴進出 push／pop）—— 含隔好幾層才繞回來、也含停用（enabled=0）的關聯（links=all 會走到它）。
        /// <paramref name="iOverride"/>：某一件作品改用這份 links（加關聯時「還沒存的那一份」一起驗）。
        /// 成環、作品不存在或沒建好、超過 <see cref="MaxDepth"/> 層 ⇒ ArgumentException；links.json 壞了 ⇒ InvalidOperationException。
        /// </summary>
        public static List<SCP_SculptLinkNode> Tree(SCP_SculptWorks iWorks, string iRootId, (string WorkId, SCP_SculptLinkFile Links)? iOverride = null)
        {
            var aStack = new List<string> { iRootId };
            return Children(iWorks, iRootId, "", aStack, iOverride);
        }

        static List<SCP_SculptLinkNode> Children(SCP_SculptWorks iWorks, string iWorkId, string iPrefix, List<string> ioStack, (string WorkId, SCP_SculptLinkFile Links)? iOverride)
        {
            if (ioStack.Count > MaxDepth) throw new ArgumentException("關聯超過 " + MaxDepth + " 層（" + string.Join(" → ", ioStack) + "）");
            var aOut = new List<SCP_SculptLinkNode>();
            SCP_SculptLinkFile aFile = iOverride != null && iOverride.Value.WorkId == iWorkId ? iOverride.Value.Links : Read(iWorks.SpacePaths(iWorkId));
            foreach (var l in aFile.links)
            {
                string aPath = iPrefix + l.name;
                if (ioStack.Contains(l.work)) throw new ArgumentException("關聯成環：" + string.Join(" → ", ioStack) + " → " + l.work + "（`" + aPath + "`）");
                SCP_SculptWork aCard;
                try { aCard = iWorks.Load(l.work); }
                catch (Exception e) when (e is InvalidOperationException || e is ArgumentException || e is IOException)
                { throw new ArgumentException("關聯 `" + aPath + "` 指的作品 `" + l.work + "` 讀不了：" + e.Message); }
                var aNode = new SCP_SculptLinkNode { Path = aPath, Link = l, Card = aCard };
                ioStack.Add(l.work);
                aNode.Children = Children(iWorks, l.work, aPath + "/", ioStack, iOverride);
                ioStack.RemoveAt(ioStack.Count - 1);
                aOut.Add(aNode);
            }
            return aOut;
        }

        /// <summary>樹上所有路徑（驗選擇的名字用）。</summary>
        public static void Paths(List<SCP_SculptLinkNode> iNodes, List<string> ioOut)
        {
            foreach (var n in iNodes) { ioOut.Add(n.Path); Paths(n.Children, ioOut); }
        }

        /// <summary>樹上出現的作品（去重，不含根）—— 拿鎖用（依 ID 排序一次拿齊）。</summary>
        public static void Closure(List<SCP_SculptLinkNode> iNodes, SortedSet<string> ioOut)
        {
            foreach (var n in iNodes) { ioOut.Add(n.Card.id); Closure(n.Children, ioOut); }
        }

        /// <summary>選擇裡點到的路徑都要存在；不存在 ⇒ ArgumentException（⛔ 打錯字不會變成「什麼都沒關」）。</summary>
        public static void ValidateSelection(List<SCP_SculptLinkNode> iTree, SCP_SculptLinkSelection iSel)
        {
            var aAll = new List<string>();
            Paths(iTree, aAll);
            var aSet = new HashSet<string>(aAll, StringComparer.Ordinal);
            var aNamed = new List<string>(iSel.Hide);
            if (iSel.Only != null) aNamed.AddRange(iSel.Only);
            foreach (string p in aNamed)
                if (!aSet.Contains(p)) throw new ArgumentException("沒有關聯 `" + p + "`（現有：" + (aAll.Count == 0 ? "（沒有）" : string.Join("、", aAll)) + "；hide_links=self ＝ 不畫本作品自己的格）");
        }

        /// <summary>轉向（同 assemble 的公式）：turn 90 ⇒ (H−1−y, x)；180 ⇒ (W−1−x, H−1−y)；270 ⇒ (y, W−1−x)。W／H ＝ 關聯記的軸心。</summary>
        public static void Turn(int iTurn, int iW, int iH, int x, int y, out int oX, out int oY)
        {
            oX = x; oY = y;
            if (iTurn == 90) { oX = iH - 1 - y; oY = x; }
            else if (iTurn == 180) { oX = iW - 1 - x; oY = iH - 1 - y; }
            else if (iTurn == 270) { oX = y; oY = iW - 1 - x; }
        }

        /// <summary>
        /// 零件一格的落點（關聯與 assemble 共用這一支 —— ⛔ 別在別處再寫一份公式）：
        /// ① 鏡像：在零件自己的框（W×H×D）裡翻面 —— x ⇒ W−1−x、y ⇒ H−1−y、z ⇒ D−1−z
        /// ② 繞 Z 轉（<see cref="Turn"/>，用同一個 W／H）③ 呼叫端再加 at。
        /// 先翻再轉：外框不變 ⇒ 放不放得進（<see cref="Fits"/>）跟沒翻一樣。
        /// </summary>
        public static void Transform(string iMirror, int iTurn, int iW, int iH, int iD, int x, int y, int z, out int oX, out int oY, out int oZ)
        {
            if (iMirror.Length > 0)
            {
                if (iMirror.IndexOf('x') >= 0) x = iW - 1 - x;
                if (iMirror.IndexOf('y') >= 0) y = iH - 1 - y;
                if (iMirror.IndexOf('z') >= 0) z = iD - 1 - z;
            }
            Turn(iTurn, iW, iH, x, y, out oX, out oY);
            oZ = z;
        }

        /// <summary>尺寸 <paramref name="iDims"/>（x,y,z）的作品轉向之後放在 at，放不放得進 <paramref name="iSizeX"/>…；放不下 ⇒ 一句人話，放得下 ⇒ null。</summary>
        public static string? Fits(string iWork, int[] iDims, int[] iAt, int iTurn, int iSizeX, int iSizeY, int iSizeZ)
        {
            int w = iTurn == 90 || iTurn == 270 ? iDims[1] : iDims[0], h = iTurn == 90 || iTurn == 270 ? iDims[0] : iDims[1];
            if (iAt[0] < 0 || iAt[1] < 0 || iAt[2] < 0 || (long)iAt[0] + w > iSizeX || (long)iAt[1] + h > iSizeY || (long)iAt[2] + iDims[2] > iSizeZ)
                return "作品 " + iWork + "（" + iDims[0] + "," + iDims[1] + "," + iDims[2] + "，轉 " + iTurn + "° ⇒ " + w + "," + h + "," + iDims[2] + "）放在 at="
                       + iAt[0] + "," + iAt[1] + "," + iAt[2] + " 超出 " + iSizeX + "," + iSizeY + "," + iSizeZ;
            return null;
        }
    }

    /// <summary>
    /// 觀測時的組裝器（引擎的 <see cref="SCP_SculptEngine.Links"/>）：本作品的空間 ＋ 選到的關聯（巢狀）。
    /// ⛔ 只給觀測（與攤平）用 —— 寫入類 op 一律 LoadSpace（只有本作品），落子／刻／undo 父作品碰不到被關聯的作品。
    /// ⚠ 鎖由呼叫端拿：樹上所有作品（<see cref="SCP_SculptLinks.Closure"/>）連同本作品依 ID 排序一次拿齊 —— 全系統只有一種鎖序。
    /// </summary>
    public sealed class SCP_SculptLinkComposer
    {
        readonly SCP_SculptWorks m_Works;
        readonly string m_DataRoot;
        readonly List<SCP_SculptLinkNode> m_Tree;
        readonly SCP_SculptLinkSelection m_Sel;
        /// <summary>看草稿（觀測 draft=1）：被關聯的作品有開著的草稿 ⇒ 用草稿。</summary>
        public bool UseDrafts;
        /// <summary>上一次 <see cref="Compose"/> 的讀數。</summary>
        public SCP_SculptComposeReport? Last;
        /// <summary>
        /// 記材質（TASK-0616）：組裝時順便記每一格的材質 ＝ 它來自的那條關聯、往上第一個有 meta <c>material</c> 的（自己沒寫 ⇒ 照上一層；都沒寫 ⇒ 不記 ＝ 船的預設材質）。
        /// 結果在 <see cref="LastMaterial"/>（組好之後的座標）；被蓋掉、越界的格不記（跟格子同一個「已有的格贏」）。
        /// </summary>
        public bool TrackMaterial;
        /// <summary>上一次 <see cref="Compose"/> 的材質（<see cref="TrackMaterial"/> 才有）：<see cref="MatKey"/> ⇒ 材質 ID；沒有任何關聯寫 material ⇒ 空的。</summary>
        public Dictionary<long, string>? LastMaterial;
        public const string MaterialKey = "material";
        public static long MatKey(int x, int y, int z) => ((long)x << 42) | ((long)y << 21) | (long)z;
        static string? MaterialOf(SCP_SculptLink iLink) => iLink.meta.TryGetValue(MaterialKey, out SCP_JsonData? v) && v.IsString && v.AsString().Length > 0 ? v.AsString() : null;

        public SCP_SculptLinkComposer(SCP_SculptWorks iWorks, string iDataRoot, List<SCP_SculptLinkNode> iTree, SCP_SculptLinkSelection iSel)
        {
            m_Works = iWorks; m_DataRoot = iDataRoot; m_Tree = iTree; m_Sel = iSel;
            SCP_SculptLinks.ValidateSelection(m_Tree, iSel);
        }

        public List<SCP_SculptLinkNode> Tree => m_Tree;
        public SCP_SculptLinkSelection Selection => m_Sel;

        /// <summary>把選到的關聯組進 <paramref name="ioOwn"/>（就地改；呼叫端給的是剛讀出來的一份）。hide_links=self ⇒ 從空的開始。回傳組好的空間。</summary>
        public SCP_SculptSpace Compose(SCP_SculptSpace ioOwn, int iSizeX, int iSizeY, int iSizeZ)
        {
            var aReport = new SCP_SculptComposeReport { HideSelf = m_Sel.HideSelf };
            SCP_SculptSpace aInto = m_Sel.HideSelf ? new SCP_SculptSpace { LastEventFile = ioOwn.LastEventFile } : ioOwn;
            var aOwnCache = new Dictionary<string, SCP_SculptSpace>(StringComparer.Ordinal);
            var aMat = TrackMaterial ? new Dictionary<long, string>() : null;
            Place(aInto, m_Tree, iSizeX, iSizeY, iSizeZ, aReport, aOwnCache, 0, "", aMat, null);
            Last = aReport;
            LastMaterial = aMat;
            return aInto;
        }

        /// <summary>一層：每個選到的節點 ⇒ 它自己的空間＋它底下選到的（遞迴，在它自己的座標裡、用它自己的尺寸組好）⇒ 轉向、平移、疊進 <paramref name="ioInto"/>（已有的格贏）。</summary>
        void Place(SCP_SculptSpace ioInto, List<SCP_SculptLinkNode> iNodes, int iSizeX, int iSizeY, int iSizeZ,
                   SCP_SculptComposeReport ioReport, Dictionary<string, SCP_SculptSpace> ioOwnCache, int iDepth, string iFrame,
                   Dictionary<long, string>? ioMat, string? iInherit)
        {
            foreach (var n in iNodes)
            {
                if (!m_Sel.Includes(n.Path, n.Link.enabled)) continue;
                SCP_SculptSpace aChild = Copy(LoadOwn(n.Card, ioOwnCache, ioReport));
                string? aEff = ioMat != null ? MaterialOf(n.Link) ?? iInherit : null;   // 這條關聯的格（含它底下沒寫 material 的）是什麼材質
                var aChildMat = ioMat != null ? new Dictionary<long, string>() : null;    // 它底下的關聯自己寫了 material 的格（它自己的座標）
                Place(aChild, n.Children, n.Card.SizeX, n.Card.SizeY, n.Card.SizeZ, ioReport, ioOwnCache, iDepth + 1, n.Card.id, aChildMat, aEff);
                var aWorkInfo = ioReport.Works[n.Card.id];
                var aRow = new SCP_SculptComposeReport.Row { Path = n.Path, Work = n.Card.id, Revision = aWorkInfo.Revision, Draft = aWorkInfo.Draft, PivotDrift = n.PivotDrift };
                foreach (var v in aChild.Voxels.Entries())
                {
                    SCP_SculptLinks.Transform(n.Link.mirror ?? "", n.Link.turn, n.Link.pivot[0], n.Link.pivot[1], n.Link.pivot[2], v.X, v.Y, v.Z, out int tx, out int ty, out int tz);
                    long x = (long)tx + n.Link.at[0], y = (long)ty + n.Link.at[1], z = (long)tz + n.Link.at[2];
                    if (x < 0 || y < 0 || z < 0 || x >= iSizeX || y >= iSizeY || z >= iSizeZ)
                    {
                        aRow.Clipped++;   // ⛔ 不放進組好的空間（負座標／超出父作品 ⇒ 下游的 revision、assemble、export 都會出事）
                        if (ioReport.Samples.Count < SCP_SculptEngine.StructureSamples) ioReport.Samples.Add("`" + n.Path + "` 越界 (" + x + "," + y + "," + z + ")" + FrameNote(iFrame));
                        continue;
                    }
                    if (ioInto.Voxels.Get((int)x, (int)y, (int)z) != 0)
                    {
                        aRow.Overlap++;
                        if (ioReport.Samples.Count < SCP_SculptEngine.StructureSamples) ioReport.Samples.Add("`" + n.Path + "` 被蓋掉 (" + x + "," + y + "," + z + ")" + FrameNote(iFrame));
                        continue;
                    }
                    ioInto.Voxels.Set((int)x, (int)y, (int)z, v.Color);
                    aRow.Placed++;
                    if (ioMat != null)
                    {
                        string? m = aChildMat!.TryGetValue(MatKey(v.X, v.Y, v.Z), out string? cm) ? cm : aEff;
                        if (m != null) ioMat[MatKey((int)x, (int)y, (int)z)] = m;
                    }
                }
                ioReport.Rows.Add(aRow);
                // 放了幾格只算進最上層（巢狀那幾格已經算在它父節點那一列裡）；被蓋掉／越界每一層都是真的少了格 ⇒ 全加
                if (iDepth == 0) ioReport.Placed += aRow.Placed;
                ioReport.Overlap += aRow.Overlap; ioReport.Clipped += aRow.Clipped;
            }
        }

        /// <summary>
        /// 被關聯作品自己的空間（只讀：⛔ 不存它的快取 —— 那件作品的快取讓它自己的 op 去更新）；同一次組裝裡同一件只讀一次。
        /// 看草稿且它有開著的草稿 ⇒ 用草稿。讀不了 ⇒ ArgumentException（呼叫端轉 exit 2）。
        /// </summary>
        SCP_SculptSpace LoadOwn(SCP_SculptWork iCard, Dictionary<string, SCP_SculptSpace> ioCache, SCP_SculptComposeReport ioReport)
        {
            if (ioCache.TryGetValue(iCard.id, out SCP_SculptSpace? aHit)) return aHit;
            SCP_SculptSpace aSpace;
            bool aDraft = false;
            try
            {
                SCP_SculptPaths aPaths = m_Works.SpacePaths(iCard.id);
                SCP_SculptDraftInfo? aInfo = UseDrafts ? SCP_SculptDraft.Read(aPaths) : null;
                if (aInfo != null) { aSpace = SCP_SculptStore.Load(SCP_SculptDraft.Paths(aPaths, aInfo, iCard.Dimensions), false); aDraft = true; }
                else aSpace = SCP_SculptStore.Load(aPaths, false);
            }
            catch (Exception e) when (e is SCP_SculptReplayException || e is InvalidOperationException || e is IOException)
            {
                throw new ArgumentException("關聯的作品 `" + iCard.id + "` 讀不了：" + e.Message);
            }
            ioCache[iCard.id] = aSpace;
            ioReport.Works[iCard.id] = (iCard, SCP_SculptWorks.Revision(aSpace, iCard.Dimensions), aDraft);
            return aSpace;
        }

        /// <summary>巢狀那一層的位置是那件作品自己的座標（⛔ 不讓它看起來像觀測的作品的座標 —— 照著去找會找錯格）。</summary>
        static string FrameNote(string iFrame) => iFrame.Length == 0 ? "" : "（在 " + iFrame + " 自己的座標）";

        static SCP_SculptSpace Copy(SCP_SculptSpace iSpace)
        {
            var aOut = new SCP_SculptSpace { LastEventFile = iSpace.LastEventFile };
            foreach (var v in iSpace.Voxels.Entries()) aOut.Voxels.Set(v.X, v.Y, v.Z, v.Color);
            return aOut;
        }
    }
}
