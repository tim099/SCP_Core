// 區塊職責：雕刻的固定點與繩索（TASK-0502）—— 作品記「哪裡有一個綁得住的點」與「哪兩點之間拉一條繩」，觀測時才算形狀、才畫。
// 物理意義：兩份都是作者寫的作品資料（anchors.json／ropes.json，入版控），⛔ 不是 voxel、不寫 events/ —— 一格 voxel 都不動。
//          固定點：名字 → 格座標，點取**格心**（x+0.5, y+0.5, z+0.5）。
//          繩索：兩端各是「固定點」或「直接座標」；顏色、粗細（直徑，格）、鬆緊（slack ＝ 繩長 ÷ 兩端直線距離，1 ＝ 拉直）。
//          跨作品：端點寫關聯路徑 `主桅/main-yard-arm-port`（相對於**繩子所在那件作品**）；
//                  座標照那條路徑上每一層關聯的 mirror／turn／at 換到組裝件 —— 跟 voxel 用**同一個公式**（<see cref="SCP_SculptLinks.Transform"/> 的連續版）。
//          繩子可以掛在任何一件作品（通常掛在組裝件）；被關聯進來的作品自己的繩子也照它那條關聯換座標、一起畫。
//          形狀：只受重力（−z）⇒ 懸鏈線解析解；同樣輸入 ⇒ 同樣的點（沒有亂數、沒有迭代次數以外的狀態）。
// 數值影響：觀測時每條繩一個 <see cref="SCP_SculptRopeReading"/>：state ＝ ok（畫）／hidden（綁的關聯這次沒畫 ⇒ 跟著不畫）／
//          disabled（自己關著）／broken（綁定壞了：固定點或關聯路徑不存在）—— ⛔ 壞掉的不靜默消失，每次觀測都列出來。
// 失敗處置：anchors.json／ropes.json 讀不了 ⇒ InvalidOperationException（⛔ 不當成沒有 —— 寫回去會清光）。
// 帆（TASK-0503）：端點可以是帆角 `sail:[關聯/…/]帆名/角`（固定點名與關聯名都不准含 `:`，所以不會跟固定點撞名）；
//          帆角在哪由呼叫端注入（<see cref="SCP_SculptSailEndResolver"/>，帆的觀測 <see cref="SCP_SculptSails.Rig"/> 給）——
//          ⛔ 繩這一側不自己讀帆（端點邏輯只有一份）。沒注入 ⇒ broken 並說原因。其他 `kind:` ⇒ 明說不認得（⛔ 不猜）。
// 外部模型（TASK-0507）：端點可以是模型上的點 `model:[關聯/…/]模型名/點`（點 ＝ x,y,z 或來源作品的固定點名；解法在 <see cref="SCP_SculptModelSet.ResolvePoint"/>）。
//          繩的端點與帆的角**同一支**解：<see cref="SCP_SculptPointResolver"/>（x,y,z｜固定點路徑｜sail:｜model:）。
// 子繩（TASK-0511）：端點可以是另一條繩上的點 `rope:[關聯/…/]繩名/比例`（比例 0..1 ＝ 沿母繩**實際的曲線**量的長度比）—— 母繩動，子繩跟著動。
//          子繩本身不算物理：兩端之間拉直（slack 只能是 1）、另一端不能綁帆角；畫成 <see cref="ChildSides"/> 邊的細管（比一般繩省）。
//          母繩沒畫（hidden／disabled）⇒ 子繩跟著不畫；母繩壞了、繞成環 ⇒ 子繩 broken 並說是哪一條。帆角與模型不能綁在繩上。
// 分組（TASK-0512）：每條繩可以歸到一個組（`group`，`/` 分層）；ropes.json 的 `groups` 記每組平常開不開（關著 ⇒ 組裡的繩 disabled）。
//          觀測時用「全路徑」指組：繩所在那一層的關聯路徑＋`/`＋組路徑（組裝件自己的就是組路徑）；指一個組 ＝ 連同底下的子組。
//          有用到組或子繩的 ropes.json 寫 schema 2（同 links.json 的鏡像：沒更新的 senate.exe 讀到會大聲讀不了，⛔ 不會默默丟掉組再寫回去）。
// meta（TASK-0539）：每條繩可以帶一包 meta（同模型／關聯，<see cref="SCP_SculptMeta"/>；例 `kind=sheet, sail=fore-topsail`）—— 給下游（遊戲）認「這條繩是什麼、屬於哪面帆」，
//          引擎自己不讀它的意思。有任何一條帶 meta ⇒ schema 升 <see cref="SCP_SculptRopes.SchemaWithMeta"/>（3，含 2 的一切）；沒用到的檔逐位元組照舊。
// 橫繩組（TASK-0678）：ropes.json 的 `ratlines` 只記兩條母繩＋間距（<see cref="SCP_SculptRatlines"/>），觀測時才照母繩現在的形狀等高展開成子繩（`組名.NN`）——
//          展開後跟手寫的子繩走同一支 Bind／Settle ⇒ 畫法、匯出、開關都不另寫。climb ⇒ 每道 meta 帶繩梯的落腳點給遊戲。有橫繩組 ⇒ schema 4。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Canvas;
using SCP.Core.Json;
using SCP.Core.Reflect;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptAnchor
    {
        public string name = "";
        /// <summary>格座標（x,y,z）；點取格心。⚠ 用 List 不用 int[]（JSON 對映器讀陣列是「清空再逐項加」）。</summary>
        public List<int> at = new List<int> { 0, 0, 0 };
        public string note = "", persona = "", created_at = "";
    }

    public sealed class SCP_SculptAnchorFile
    {
        public int schema = 1;
        public List<SCP_SculptAnchor> anchors = new List<SCP_SculptAnchor>();
    }

    public sealed class SCP_SculptRope
    {
        public string name = "";
        /// <summary>端點：固定點路徑（`名字` 或 `關聯/…/名字`，相對於繩子所在的作品）或座標 `x,y,z`（格，可帶小數；整數 ＝ 那一格的格心）。</summary>
        public string from = "", to = "";
        /// <summary>顏色：調色盤 index（1..255）或 #RRGGBB；存正規化後的寫法。</summary>
        public string color = SCP_SculptRopes.DefaultColor;
        /// <summary>粗細 ＝ 直徑（格）。</summary>
        public double thickness = SCP_SculptRopes.DefaultThickness;
        /// <summary>鬆緊 ＝ 繩長 ÷ 兩端直線距離；1 ＝ 拉直，越大垂得越多。</summary>
        public double slack = 1.0;
        public bool enabled = true;
        /// <summary>組路徑（TASK-0512；`/` 分層，例 `standing/shrouds`）；空 ＝ 未分組。</summary>
        public string group = "";
        public string note = "", persona = "", created_at = "";
        /// <summary>meta（TASK-0539）：[SCP_Ignore]，由 <see cref="SCP_SculptRopes.ReadRopes"/>／<see cref="SCP_SculptRopes.SaveRopes"/> 手動讀寫（同模型）；空 ⇒ 不寫 key。</summary>
        [SCP_Ignore] public SortedDictionary<string, SCP_JsonData> meta = SCP_SculptMeta.New();
    }

    /// <summary>一個組平常開不開（TASK-0512）：關著 ⇒ 組裡（連同子組）的繩平常不畫（state disabled）。沒記的組 ＝ 開著。</summary>
    public sealed class SCP_SculptRopeGroup
    {
        /// <summary>組路徑（這件作品自己的，不含關聯路徑）。</summary>
        public string name = "";
        public bool enabled = true;
        public string note = "";
    }

    /// <summary>
    /// 橫繩組（TASK-0678）：兩條母繩之間每隔 <see cref="step"/> 格（沿 z，等高 ⇒ 水平）一道橫繩 —— 只記這一筆，觀測時才照母繩**現在的形狀**展開成子繩
    /// （名字 <c>組名.NN</c>，由下往上；跟手寫的 `rope:母繩/比例` 子繩同一支解、同一支畫）。母繩換端點、改 slack ⇒ 橫繩跟著重排。
    /// <para>繩梯：<see cref="climb"/> ⇒ 展開的每道橫繩帶 meta（kind=ratline、ratlines、rung、rungs、climb、foot、head）給遊戲認成一座梯子；
    /// foot／head ＝ 上下兩個落腳點（同繩的端點寫法：固定點、關聯/…/固定點、x,y,z、model:）。</para>
    /// </summary>
    public sealed class SCP_SculptRatlines
    {
        public string name = "";
        /// <summary>兩條母繩的路徑（相對於這組所在的作品；同 `rope:` 的寫法，不帶比例）。</summary>
        public string a = "", b = "";
        /// <summary>每幾格一道（沿 z）。</summary>
        public double step = 4;
        /// <summary>兩條母繩較高的那個下端往上 start 格起、較低的那個上端往下 stop 格止。</summary>
        public double start = 0, stop = 0;
        /// <summary>一道短於這個（格）就停（母繩往上收攏，最上面幾道只剩一兩格）；0 ＝ 不限。</summary>
        public double min_span = 0;
        public string color = SCP_SculptRopes.DefaultColor;
        public double thickness = SCP_SculptRopes.DefaultThickness;
        public bool enabled = true;
        public string group = "";
        /// <summary>繩梯（遊戲裡爬得上去）。</summary>
        public bool climb;
        /// <summary>繩梯的下、上落腳點（climb 時必填）。</summary>
        public string foot = "", head = "";
        public string note = "", persona = "", created_at = "";
    }

    public sealed class SCP_SculptRopeFile
    {
        public int schema = 1;
        public List<SCP_SculptRope> ropes = new List<SCP_SculptRope>();
        public List<SCP_SculptRopeGroup> groups = new List<SCP_SculptRopeGroup>();
        /// <summary>橫繩組（TASK-0678）；空 ⇒ 不寫 key（沒用到的檔逐位元組照舊）。</summary>
        public List<SCP_SculptRatlines> ratlines = new List<SCP_SculptRatlines>();
    }

    /// <summary>給渲染器／匯出的一條繩（已算好形狀、已換到組裝件座標）：折線點（世界座標，格）、半徑、顏色。</summary>
    public sealed class SCP_SculptRopeGeom
    {
        public string Path = "";
        /// <summary>xyz 攤平：點 i ＝ (Points[3i], Points[3i+1], Points[3i+2])。</summary>
        public double[] Points = new double[0];
        public double Radius;
        public byte R, G, B;
        /// <summary>管子一圈幾個頂點：0 ＝ 照粗細由呼叫端決定；≥ 3 ＝ 照這個（子繩 ＝ <see cref="SCP_SculptRopes.ChildSides"/>，TASK-0511）。</summary>
        public int Sides;
        public int PointCount => Points.Length / 3;
    }

    /// <summary>
    /// 帆角端點（TASK-0503）的解法，由帆的觀測注入：<paramref name="iFrame"/> ＝ 繩所在那一層的關聯路徑、<paramref name="iRef"/> ＝ `sail:` 後面那一段。
    /// 回 null ＝ 解得到：<paramref name="oPoint"/> ＝ 帆角的**設計位置**（組裝件座標）、<paramref name="oKey"/> ＝ 帆角的全路徑（帆路徑＋`#`＋角序號）、
    /// <paramref name="oHiddenWhy"/> 非空 ＝ 那面帆這次沒畫（繩跟著 hidden）；否則回壞掉的原因。
    /// </summary>
    public delegate string? SCP_SculptSailEndResolver(string iFrame, string iRef, out double[]? oPoint, out string oKey, out string oHiddenWhy);

    /// <summary>一條繩這次觀測的讀數。</summary>
    public sealed class SCP_SculptRopeReading
    {
        public const string Ok = "ok", Hidden = "hidden", Disabled = "disabled", Broken = "broken";
        /// <summary>繩的路徑：繩子所在作品的關聯路徑（組裝件自己 ⇒ 空）＋ `/` ＋ 繩名。</summary>
        public string Path = "";
        public string Owner = "";
        public SCP_SculptRope Rope = new SCP_SculptRope();
        public string State = Ok;
        public string Reason = "";
        /// <summary>兩端（組裝件座標）；broken 時可能只有一端有值。</summary>
        public double[]? From, To;
        public double Chord, Length, MaxSag;
        /// <summary>帆角端點（TASK-0503）：帆角全路徑（`帆路徑#角序號`）；不是帆角 ⇒ 空。帆模擬完，這一端換成帆角那一刻的位置、形狀重算。</summary>
        public string FromSail = "", ToSail = "";
        /// <summary>繩所在、或兩端綁的那一層（關聯／帆）這次沒組進來 ⇒ 這條繩在這次的場景裡**不存在**（帆的模擬不拿它拉帆角）；只被顯示開關關掉（ropes=0、hide_ropes、enabled=0）⇒ false。</summary>
        public bool LinkHidden;
        /// <summary>兩端幾乎在同一條鉛直線上（水平距離 ≈ 0）⇒ 畫直線（鬆度沒有往旁邊垂的方向）。</summary>
        public bool Vertical;
        public SCP_SculptRopeGeom? Geom;
        /// <summary>組的全路徑（TASK-0512）：繩所在那一層的關聯路徑＋`/`＋組路徑；沒分組 ⇒ 空。</summary>
        public string Group = "";
        /// <summary>折線（組裝件座標，xyz 攤平）—— 不管這次畫不畫，兩端都解得到就有（子繩的位置從這裡量，TASK-0511）。</summary>
        public double[]? Line;
        /// <summary>子繩（TASK-0511）：至少一端是 `rope:` 母繩上的點 ⇒ 兩端拉直、不算物理。</summary>
        public bool Child;
        /// <summary>位置會跟著帆動：自己綁帆角，或它掛的母繩（一路往上）綁帆角 ⇒ 帆模擬完（觀測頁每幀）要重算。</summary>
        public bool FollowsSail;
        /// <summary>子繩兩端掛的母繩與比例（不是 `rope:` 的那一端、或母繩不存在 ⇒ null；母繩壞了或繞成環也照記）。</summary>
        public SCP_SculptRopeReading? FromParent, ToParent;
        public double FromT, ToT;
        /// <summary>往上掛了幾層母繩（一般繩 0、掛在一般繩上 1…）—— 重算時由淺到深。</summary>
        public int Depth;
        /// <summary>這次沒畫的原因只是 rope_groups 沒點到它（TASK-0512）⇒ 掛在它上面的子繩**不跟著**藏（子繩照自己的組判；點名只畫 ratlines 就看得到 ratlines）。</summary>
        public bool OnlyFiltered;
        /// <summary>橫繩組展開出來的（TASK-0678）⇒ 那一組的路徑（關聯路徑＋`/`＋組名）；手寫的繩 ⇒ 空。整組壞掉時只有一筆、路徑就是組的路徑。</summary>
        public string Ratlines = "";
    }

    /// <summary>
    /// 觀測時選哪些繩：All ＝ 照每條 enabled；None ＝ 都不畫（ropes=0）；Hide ＝ 這幾條（繩路徑）不畫；
    /// OnlyGroups ＝ 只畫這幾組（組全路徑，連同子組；空 ＝ 不限）；HideGroups ＝ 這幾組不畫（TASK-0512）。
    /// </summary>
    public sealed class SCP_SculptRopeSelection
    {
        public bool None;
        public HashSet<string> Hide = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> OnlyGroups = new HashSet<string>(StringComparer.Ordinal);
        public HashSet<string> HideGroups = new HashSet<string>(StringComparer.Ordinal);

        public static SCP_SculptRopeSelection Parse(string iRopes, string iHide) => Parse(iRopes, iHide, "", "");

        public static SCP_SculptRopeSelection Parse(string iRopes, string iHide, string iOnlyGroups, string iHideGroups)
        {
            var aSel = new SCP_SculptRopeSelection { None = iRopes.Trim() == "0" };
            foreach (string p in iHide.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) aSel.Hide.Add(p.Trim().Trim('/'));
            foreach (string p in iOnlyGroups.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) aSel.OnlyGroups.Add(p.Trim().Trim('/'));
            foreach (string p in iHideGroups.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) aSel.HideGroups.Add(p.Trim().Trim('/'));
            return aSel;
        }

        /// <summary>組全路徑 <paramref name="iGroup"/> 在不在 <paramref name="iSet"/> 某一組底下（相等也算）；未分組（空）⇒ 不在任何組底下。</summary>
        public static bool Under(string iGroup, IEnumerable<string> iSet)
        {
            if (iGroup.Length == 0) return false;
            foreach (string g in iSet)
                if (iGroup == g || iGroup.StartsWith(g + "/", StringComparison.Ordinal)) return true;
            return false;
        }
    }

    /// <summary>
    /// 一個點（繩的端點、帆的角）解到組裝件座標 —— TASK-0507 把原本繩與帆各寫一份的解法合成一支：
    /// `x,y,z`（照所在那一層換座標）｜固定點路徑（`名字`、`關聯/…/名字`）｜`sail:…`（帆角，由帆的觀測注入）｜`model:…`（模型上的點，由模型的觀測給）。
    /// <para>回 null ＝ 解得到：<c>oSailKey</c> 非空 ＝ 帆角（全路徑＋`#`＋角序號）；<c>oHiddenWhy</c> 非空 ＝ 綁的那一層／那面帆／那個模型這次沒組進來
    /// （前面帶一個空白，接在「from／to／角 k」後面讀）。</para>
    /// <para>`rope:`（繩上的點，TASK-0511）不在這裡解：它要等母繩的形狀 ⇒ 由 <see cref="SCP_SculptRopes.Resolve"/> 第二趟解；帆角拿到它 ⇒ 擋。</para>
    /// </summary>
    public sealed class SCP_SculptPointResolver
    {
        readonly SCP_SculptWorks m_Works;
        readonly string m_Root;
        readonly Dictionary<string, SCP_SculptLinkNode> m_Index;
        readonly SCP_SculptLinkSelection m_Sel;
        readonly Dictionary<string, SCP_SculptAnchorFile> m_Anchors = new Dictionary<string, SCP_SculptAnchorFile>(StringComparer.Ordinal);
        /// <summary>帆角的解法（null ⇒ `sail:` 一律壞掉並說原因）。</summary>
        public SCP_SculptSailEndResolver? Sails;
        /// <summary>模型（null ⇒ `model:` 一律壞掉並說原因）。</summary>
        public SCP_SculptModelSet? Models;

        public SCP_SculptPointResolver(SCP_SculptWorks iWorks, string iRootId, Dictionary<string, SCP_SculptLinkNode> iIndex, SCP_SculptLinkSelection iSel)
        {
            m_Works = iWorks; m_Root = iRootId; m_Index = iIndex; m_Sel = iSel;
        }

        /// <param name="iAllowSail">帆角可不可以當這個點（繩 ⇒ 可以；帆自己的角 ⇒ 不行：帆角不能綁在另一面帆的角上）。</param>
        public string? Resolve(string iFrame, string iText, bool iAllowSail, out double[]? oPoint, out string oSailKey, out string oHiddenWhy)
        {
            oPoint = null; oSailKey = ""; oHiddenWhy = "";
            if (!SCP_SculptRopes.TryParseEndpoint(iText, out double[]? aPt, out string aRef, out string aBad)) return aBad;
            if (aPt != null) { oPoint = SCP_SculptRopes.ToRoot(iFrame, aPt, m_Index); return null; }
            if (aRef.StartsWith(SCP_SculptRopes.RopePrefix, StringComparison.Ordinal))
                return "`" + aRef + "`：繩上的點只能當繩的端點（帆角不能綁在繩上 —— 綁固定點、模型上的點或 x,y,z）";
            if (aRef.StartsWith(SCP_SculptRopes.SailPrefix, StringComparison.Ordinal))
            {
                // 帆角（TASK-0503）：位置由帆的觀測給（設計位置；模擬完由它換成那一刻的位置）
                if (!iAllowSail) return "`" + aRef + "`：帆的角不能綁在另一面帆的角上（綁固定點、模型上的點或 x,y,z）";
                if (Sails == null) return "帆角端點要在帆的觀測裡解（這次沒有帆的資料）";
                string? aSailBad = Sails(iFrame, aRef.Substring(SCP_SculptRopes.SailPrefix.Length), out oPoint, out oSailKey, out string aSailHidden);
                if (aSailBad != null) { oPoint = null; oSailKey = ""; return aSailBad; }
                if (aSailHidden.Length > 0) oHiddenWhy = " 綁的帆" + aSailHidden;
                return null;
            }
            if (aRef.StartsWith(SCP_SculptModels.Prefix, StringComparison.Ordinal))
            {
                if (Models == null) return "模型端點要在模型的觀測裡解（這次沒有模型的資料）";
                string? aModelBad = Models.ResolvePoint(iFrame, aRef.Substring(SCP_SculptModels.Prefix.Length), out oPoint, out _, out string aModelHidden);
                if (aModelBad != null) { oPoint = null; return aModelBad; }
                if (aModelHidden.Length > 0) oHiddenWhy = " 綁的模型" + aModelHidden;
                return null;
            }
            string? aMiss = SCP_SculptRopes.ResolveAnchor(m_Works, m_Root, iFrame, aRef, m_Index, m_Anchors, out oPoint, out string aAnchorFrame);
            if (aMiss != null) { oPoint = null; return aMiss; }
            if (!SCP_SculptRopes.PathDrawn(aAnchorFrame, m_Index, m_Sel)) oHiddenWhy = " 綁的 `" + aAnchorFrame + "` 這次沒畫";
            return null;
        }
    }

    public static class SCP_SculptRopes
    {
        public const string DefaultColor = "#8b6a3e";   // 麻繩色
        /// <summary>帆角端點的前綴（TASK-0503）。</summary>
        public const string SailPrefix = "sail:";
        /// <summary>繩上的點的前綴（子繩，TASK-0511）：`rope:[關聯/…/]繩名/比例`。</summary>
        public const string RopePrefix = "rope:";
        /// <summary>子繩的管子一圈幾個頂點（TASK-0511「子繩渲染盡量精簡」）：3 ⇒ 一段 3 個四邊形（一般細繩 8 個）。</summary>
        public const int ChildSides = 3;
        /// <summary>有用到組或子繩的 ropes.json 寫這個 schema（理由同 <see cref="SCP_SculptLinks.SchemaWithMirror"/>：舊版讀到要大聲讀不了，⛔ 不默默丟掉）。</summary>
        public const int SchemaWithGroups = 2;
        /// <summary>有任何一條繩帶 meta 的 ropes.json 寫這個 schema（TASK-0539；含 <see cref="SchemaWithGroups"/> 的一切）：舊版 senate.exe 讀到要大聲讀不了，⛔ 不默默丟掉 meta 再寫回去。</summary>
        public const int SchemaWithMeta = 3;
        /// <summary>有橫繩組的 ropes.json 寫這個 schema（TASK-0678；含 <see cref="SchemaWithMeta"/> 的一切）：舊版讀到要大聲讀不了，⛔ 不默默丟掉橫繩組再寫回去。</summary>
        public const int SchemaWithRatlines = 4;
        /// <summary>一組最多展開幾道（防 step 打錯變成幾萬條）。</summary>
        public const int MaxRungs = 400;
        public const double DefaultThickness = 0.5;
        public const double MaxThickness = 64;
        public const double MaxSlack = 10;
        /// <summary>綁帆角的繩的 slack 下限（TASK-0503；見 <see cref="CheckSlack"/>）。</summary>
        public const double MinSailSlack = 0.3;
        /// <summary>懸鏈線參數 u ＝ h/(2a) 的上限：sinh(u)/u 超過這裡（鬆到幾乎對折垂下）⇒ 照上限畫並明說。</summary>
        const double MaxU = 20;

        // ───────────────────────── 讀寫 ─────────────────────────

        public static SCP_SculptAnchorFile ReadAnchors(SCP_SculptPaths iPaths)
        {
            string aFile = iPaths.AnchorsFile;
            if (!File.Exists(aFile)) return new SCP_SculptAnchorFile();
            var aOut = new SCP_SculptAnchorFile { schema = 0 };
            Populate(aFile, aOut);
            if (aOut.schema != 1) throw new InvalidOperationException("anchors.json 讀不了（" + aFile + "：schema=" + aOut.schema + "）");
            var aNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var a in aOut.anchors)
            {
                string? aBad = ValidName(a.name, "固定點") ?? (aNames.Add(a.name) ? null : "名字重複");
                if (aBad == null && (a.at == null || a.at.Count != 3)) aBad = "at 要三個整數";
                if (aBad != null) throw new InvalidOperationException("anchors.json 讀不了（" + aFile + "：固定點 `" + a.name + "` " + aBad + "）");
            }
            return aOut;
        }

        public static SCP_SculptRopeFile ReadRopes(SCP_SculptPaths iPaths)
        {
            string aFile = iPaths.RopesFile;
            if (!File.Exists(aFile)) return new SCP_SculptRopeFile();
            var aOut = new SCP_SculptRopeFile { schema = 0 };
            SCP_JsonData aRoot = Populate(aFile, aOut);
            // meta（[SCP_Ignore]，對映器不碰）：照順序對回每一條（TASK-0539，同 models.json）
            SCP_JsonData aList = aRoot["ropes"];
            for (int i = 0; i < aOut.ropes.Count && aList.IsArray && i < aList.Count; i++)
                if (SCP_SculptMeta.ReadFrom(aList[i]["meta"], aOut.ropes[i].meta) is string aMetaBad)
                    throw new InvalidOperationException("ropes.json 讀不了（" + aFile + "：繩 `" + aOut.ropes[i].name + "` 的 " + aMetaBad + "）—— ⛔ 不當成沒有，修好再用");
            if (aOut.schema != 1 && aOut.schema != SchemaWithGroups && aOut.schema != SchemaWithMeta && aOut.schema != SchemaWithRatlines) throw new InvalidOperationException("ropes.json 讀不了（" + aFile + "：schema=" + aOut.schema + "）");
            if (aOut.schema < SchemaWithMeta && aOut.ropes.Exists(r => r.meta.Count > 0)) throw new InvalidOperationException("ropes.json 讀不了（" + aFile + "：有 meta 卻是 schema " + aOut.schema + " —— 手改的檔？）");
            if (aOut.groups == null) aOut.groups = new List<SCP_SculptRopeGroup>();
            if (aOut.ratlines == null) aOut.ratlines = new List<SCP_SculptRatlines>();
            if (aOut.schema != SchemaWithRatlines && aOut.ratlines.Count > 0) throw new InvalidOperationException("ropes.json 讀不了（" + aFile + "：有橫繩組卻是 schema " + aOut.schema + " —— 手改的檔？）");
            var aNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in aOut.ropes)
            {
                if (r.group == null) r.group = "";
                string? aBad = ValidName(r.name, "繩") ?? (aNames.Add(r.name) ? null : "名字重複")
                               ?? CheckEndpoint(r.from, "from") ?? CheckEndpoint(r.to, "to")
                               ?? (TryParseColor(r.color, out _, out _, out _, out string aNorm) is string aColorBad ? aColorBad : aNorm != r.color ? "color 不是正規化的寫法（`" + r.color + "`）" : null)
                               ?? CheckThickness(r.thickness) ?? CheckSlack(r.slack, OnSail(r)) ?? CheckChild(r) ?? CheckGroup(r.group);
                if (aBad != null) throw new InvalidOperationException("ropes.json 讀不了（" + aFile + "：繩 `" + r.name + "` " + aBad + "）");
            }
            var aGroups = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in aOut.groups)
            {
                string? aBad = (g.name ?? "").Length == 0 ? "組要有名字" : CheckGroup(g.name!) ?? (aGroups.Add(g.name!) ? null : "組重複");
                if (aBad != null) throw new InvalidOperationException("ropes.json 讀不了（" + aFile + "：組 `" + g.name + "` " + aBad + "）");
            }
            foreach (var s in aOut.ratlines)
                if (CheckRatlines(aOut, s) is string aBad) throw new InvalidOperationException("ropes.json 讀不了（" + aFile + "：橫繩組 `" + s.name + "` " + aBad + "）");
            return aOut;
        }

        /// <summary>
        /// 橫繩組的規矩（TASK-0678）：名字（不跟繩撞、展開的 `名字.NN` 也不跟繩撞、組名不重複）、兩條母繩的路徑、數值範圍、顏色、粗細、組、繩梯的兩個落腳點。null ＝ 合法。
        /// ⛔ 母繩在不在不在這裡判（可能在關聯裡）—— 那是解的時候的事（解不到 ⇒ broken；寫入前先解一次擋下）。
        /// </summary>
        public static string? CheckRatlines(SCP_SculptRopeFile iFile, SCP_SculptRatlines s)
        {
            if (ValidName(s.name, "橫繩組") is string aBadName) return aBadName;
            if (iFile.ratlines.FindAll(x => x.name == s.name).Count > 1) return "橫繩組名字重複";
            if (iFile.ropes.Exists(r => r.name == s.name || r.name.StartsWith(s.name + ".", StringComparison.Ordinal)))
                return "名字跟繩撞了（展開的橫繩叫 `" + s.name + ".01`…，本作品已經有 `" + iFile.ropes.Find(r => r.name == s.name || r.name.StartsWith(s.name + ".", StringComparison.Ordinal))!.name + "`）";
            foreach (var (aWhich, aPath) in new[] { ("a", s.a), ("b", s.b) })
            {
                if ((aPath ?? "").Length == 0) return aWhich + "（母繩）是空的 —— 給繩名或 關聯/…/繩名";
                foreach (string seg in aPath!.Split('/'))
                    if (SCP_SculptLinks.ValidName(seg) is string aSegBad) return aWhich + "（母繩）`" + aPath + "` 的 `" + seg + "`：" + aSegBad;
            }
            if (s.a == s.b) return "a 和 b 是同一條繩";
            if (double.IsNaN(s.step) || s.step < 0.5 || s.step > 1000) return "step（每幾格一道）要在 0.5..1000：" + s.step.ToString(CultureInfo.InvariantCulture);
            foreach (var (aWhich, v) in new[] { ("start", s.start), ("stop", s.stop), ("min_span", s.min_span) })
                if (double.IsNaN(v) || double.IsInfinity(v) || v < 0) return aWhich + " 要 ≥ 0：" + v.ToString(CultureInfo.InvariantCulture);
            if (TryParseColor(s.color, out _, out _, out _, out string aNorm) is string aColorBad) return aColorBad;
            if (aNorm != s.color) return "color 不是正規化的寫法（`" + s.color + "`）";
            if (CheckThickness(s.thickness) is string aT) return aT;
            if (CheckGroup(s.group ?? "") is string aG) return aG;
            if (s.climb && ((s.foot ?? "").Length == 0 || (s.head ?? "").Length == 0)) return "繩梯（climb）要給 foot 與 head 兩個落腳點";
            foreach (var (aWhich, e) in new[] { ("foot", s.foot ?? ""), ("head", s.head ?? "") })
            {
                if (e.Length == 0) continue;
                if (!TryParseEndpoint(e, out _, out _, out string aEndBad)) return aWhich + "：" + aEndBad;
                if (IsRopePoint(e) || e.Trim().StartsWith(SailPrefix, StringComparison.Ordinal)) return aWhich + "：落腳點要是固定點、模型上的點或 x,y,z（不是繩上、帆角）";
            }
            return null;
        }

        /// <summary>讀檔、對映進 <paramref name="ioTarget"/>，回原始的 JSON（給 [SCP_Ignore] 的欄位手動讀）。</summary>
        static SCP_JsonData Populate(string iFile, object ioTarget)
        {
            try
            {
                var aOpt = new SCP_JsonMapOptions();
                SCP_JsonData aRoot = SCP_JsonParser.Parse(SCP.Core.Io.SCP_SharedFile.ReadAllText(iFile), false);
                SCP_JsonMapper.Populate(ioTarget, aRoot, aOpt);
                if (aOpt.Diagnostics.Count > 0) throw new InvalidOperationException(string.Join("; ", aOpt.Diagnostics));
                return aRoot;
            }
            catch (Exception e) { throw new InvalidOperationException(Path.GetFileName(iFile) + " 讀不了（" + iFile + "：" + e.Message + "）—— ⛔ 不當成沒有，修好再用"); }
        }

        public static void SaveAnchors(SCP_SculptPaths iPaths, SCP_SculptAnchorFile iFile) { iFile.schema = 1; WriteAtomic(iPaths.AnchorsFile, SCP_JsonMapper.ToJson(iFile)); }

        public static void SaveRopes(SCP_SculptPaths iPaths, SCP_SculptRopeFile iFile)
        {
            iFile.schema = iFile.ratlines.Count > 0 ? SchemaWithRatlines
                         : iFile.ropes.Exists(r => r.meta.Count > 0) ? SchemaWithMeta
                         : iFile.groups.Count > 0 || iFile.ropes.Exists(r => r.group.Length > 0 || IsChild(r)) ? SchemaWithGroups : 1;
            SCP_JsonData aRoot = SCP_JsonMapper.ToJson(iFile);
            if (iFile.ratlines.Count == 0) aRoot.Remove("ratlines");   // TASK-0678：沒用到 ⇒ 檔逐位元組照舊
            SCP_JsonData aList = aRoot["ropes"];
            for (int i = 0; i < iFile.ropes.Count; i++) SCP_SculptMeta.WriteTo(aList[i], iFile.ropes[i].meta);   // 空的不寫 key ⇒ 沒用 meta 的檔位元組不變
            WriteAtomic(iPaths.RopesFile, aRoot);
        }

        static void WriteAtomic(string iFile, SCP_JsonData iJson)
        {
            string aTmp = iFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(iJson, true) + "\n", new UTF8Encoding(false));
                SCP.Core.Io.SCP_SharedFile.Replace(aTmp, iFile);
            }
            finally { try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch (Exception) { } }
        }

        // ───────────────────────── 驗證 ─────────────────────────

        /// <summary>固定點名／繩名：同關聯名的規矩（不含 : , | / \ 與換行、不是保留字）。null ＝ 合法。</summary>
        public static string? ValidName(string iName, string iWhat)
        {
            string? aBad = SCP_SculptLinks.ValidName(iName);
            return aBad == null ? null : aBad.Replace("關聯", iWhat);
        }

        /// <summary>組路徑（TASK-0512）：空 ＝ 未分組；否則每一段照名字的規矩（`/` 分層）。null ＝ 合法。</summary>
        public static string? CheckGroup(string iGroup)
        {
            if (iGroup.Length == 0) return null;
            foreach (string seg in iGroup.Split('/'))
                if (ValidName(seg, "組") is string aBad) return "組 `" + iGroup + "`：" + aBad;
            return null;
        }

        /// <summary>
        /// 組 <paramref name="iGroup"/>（這件作品自己的組路徑）平常是不是關著：它自己或任何一層上層組記成 enabled=false ⇒ 回那個組名；否則 null。
        /// </summary>
        public static string? GroupDisabled(SCP_SculptRopeFile iFile, string iGroup)
        {
            if (iGroup.Length == 0) return null;
            foreach (SCP_SculptRopeGroup g in iFile.groups)
                if (!g.enabled && (iGroup == g.name || iGroup.StartsWith(g.name + "/", StringComparison.Ordinal))) return g.name;
            return null;
        }

        public static string? CheckThickness(double iT)
            => double.IsNaN(iT) || iT <= 0 || iT > MaxThickness ? "thickness（直徑，格）要在 (0, " + MaxThickness + "]：" + iT.ToString(CultureInfo.InvariantCulture) : null;

        /// <summary>
        /// 鬆緊的範圍：一般的繩 1..MaxSlack（比兩端距離短的繩不存在）。
        /// <paramref name="iOnSail"/>（TASK-0503：有一端是帆角）⇒ 可以小到 <see cref="MinSailSlack"/>：繩長 ＝ slack × **設計時**兩端的距離，
        /// 小於 1 ＝ 繩比設計時短 ⇒ 把 free 的帆角拉過去（收帆腳索）；帆角拉不到那麼近時繩畫成拉直。
        /// </summary>
        public static string? CheckSlack(double iS, bool iOnSail = false)
        {
            double aMin = iOnSail ? MinSailSlack : 1;
            return double.IsNaN(iS) || iS < aMin || iS > MaxSlack
                ? "slack（繩長 ÷ 兩端直線距離）要在 " + aMin.ToString(CultureInfo.InvariantCulture) + ".." + MaxSlack + "（1 ＝ 拉直" + (iOnSail ? "；綁帆角的繩可以小於 1 ＝ 把帆角拉過去" : "；只有綁帆角的繩可以小於 1") + "）：" + iS.ToString(CultureInfo.InvariantCulture)
                : null;
        }

        /// <summary>這條繩有沒有一端是帆角（`sail:`）。</summary>
        public static bool OnSail(SCP_SculptRope r)
            => (r.from ?? "").Trim().StartsWith(SailPrefix, StringComparison.Ordinal) || (r.to ?? "").Trim().StartsWith(SailPrefix, StringComparison.Ordinal);

        /// <summary>這條繩是不是子繩（TASK-0511：有一端是 `rope:` 母繩上的點）。</summary>
        public static bool IsChild(SCP_SculptRope r) => IsRopePoint(r.from) || IsRopePoint(r.to);

        static bool IsRopePoint(string? iText) => (iText ?? "").Trim().StartsWith(RopePrefix, StringComparison.Ordinal);

        /// <summary>子繩的規矩（TASK-0511「子繩本身不用算物理」）：另一端不能是帆角、slack 只能是 1（兩端拉直）。null ＝ 合法（或不是子繩）。</summary>
        public static string? CheckChild(SCP_SculptRope r)
        {
            if (!IsChild(r)) return null;
            if (OnSail(r)) return "子繩（有一端是 rope:）不算物理 ⇒ 另一端不能綁帆角（綁母繩、固定點、模型上的點或 x,y,z）";
            if (r.slack != 1) return "子繩（有一端是 rope:）不算物理、兩端拉直 ⇒ slack 只能是 1：" + r.slack.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        static string? CheckEndpoint(string iText, string iWhich) => TryParseEndpoint(iText, out _, out _, out string aBad) ? null : iWhich + "：" + aBad;

        /// <summary>
        /// 端點：`x,y,z`（三個數，可帶小數；整數 ⇒ 格心）⇒ <paramref name="oPoint"/>；否則是固定點路徑（`名字` 或 `關聯/…/名字`）⇒ <paramref name="oAnchorPath"/>。
        /// 帆角 `sail:…`（TASK-0503）、模型上的點 `model:…`（TASK-0507）、繩上的點 `rope:…`（TASK-0511）⇒ 回 true、<paramref name="oAnchorPath"/> 帶著前綴原樣；
        /// 其他 `kind:` ⇒ 不認得、回 false（⛔ 不猜）。
        /// </summary>
        public static bool TryParseEndpoint(string iText, out double[]? oPoint, out string oAnchorPath, out string oBad)
        {
            oPoint = null; oAnchorPath = ""; oBad = "";
            string a = (iText ?? "").Trim();
            if (a.Length == 0) { oBad = "端點是空的（要 固定點名、關聯/…/固定點名，或 x,y,z）"; return false; }
            if (a.StartsWith(SailPrefix, StringComparison.Ordinal))
            {
                // 帆角：sail:[關聯/…/]帆名/角（角 ＝ 角的名字或序號 0..）—— 每一段都照名字的規矩；至少「帆名/角」兩段
                string aRest = a.Substring(SailPrefix.Length);
                string[] aSegs = aRest.Split('/');
                if (aSegs.Length < 2) { oBad = "帆角端點 `" + a + "` 要寫成 sail:帆名/角（角 ＝ 名字或序號；跨關聯 sail:關聯/…/帆名/角）"; return false; }
                foreach (string seg in aSegs)
                    if (SCP_SculptLinks.ValidName(seg) is string aSegBad) { oBad = "帆角端點 `" + a + "` 的 `" + seg + "`：" + aSegBad; return false; }
                oAnchorPath = a;
                return true;
            }
            if (a.StartsWith(RopePrefix, StringComparison.Ordinal))
            {
                if (!TryParseRopePoint(a, out _, out _, out oBad)) return false;
                oAnchorPath = a;
                return true;
            }
            if (a.StartsWith(SCP_SculptModels.Prefix, StringComparison.Ordinal))
            {
                // 模型上的點（TASK-0507）：model:[關聯/…/]模型名/點（點 ＝ x,y,z 或來源作品的固定點名）
                string[] aSegs = a.Substring(SCP_SculptModels.Prefix.Length).Split('/');
                if (aSegs.Length < 2) { oBad = "模型端點 `" + a + "` 要寫成 model:模型名/點（點 ＝ x,y,z 或來源作品的固定點名；跨關聯 model:關聯/…/模型名/點）"; return false; }
                for (int i = 0; i < aSegs.Length; i++)
                {
                    string seg = aSegs[i];
                    if (i == aSegs.Length - 1 && seg.Split(',').Length == 3)
                    {
                        if (!TryParseEndpoint(seg, out _, out _, out string aXyzBad)) { oBad = "模型端點 `" + a + "`：" + aXyzBad; return false; }
                        continue;
                    }
                    if (SCP_SculptLinks.ValidName(seg) is string aSegBad) { oBad = "模型端點 `" + a + "` 的 `" + seg + "`：" + aSegBad; return false; }
                }
                oAnchorPath = a;
                return true;
            }
            if (a.IndexOf(':') >= 0) { oBad = "端點 `" + a + "`：`種類:` 形狀的端點只認 `" + SailPrefix + "`（帆角）、`" + SCP_SculptModels.Prefix + "`（模型上的點）與 `" + RopePrefix + "`（繩上的點）；其他本版不認得"; return false; }
            string[] aParts = a.Split(',');
            if (aParts.Length == 3)
            {
                var aP = new double[3];
                for (int i = 0; i < 3; i++)
                {
                    string s = aParts[i].Trim();
                    if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) || double.IsNaN(v) || double.IsInfinity(v))
                    { oBad = "端點座標 `" + a + "` 不是三個數"; return false; }
                    // 整數 ⇒ 那一格的格心（跟固定點同一個意思）；有小數 ⇒ 照給的連續座標
                    aP[i] = s.IndexOf('.') < 0 && s.IndexOf('e') < 0 && s.IndexOf('E') < 0 ? v + 0.5 : v;
                }
                oPoint = aP;
                return true;
            }
            if (aParts.Length != 1) { oBad = "端點 `" + a + "`：座標要剛好三個數（x,y,z）；固定點名不可含 `,`"; return false; }
            foreach (string seg in a.Split('/'))
                if (SCP_SculptLinks.ValidName(seg) is string aSegBad) { oBad = "端點 `" + a + "` 的 `" + seg + "`：" + aSegBad; return false; }
            oAnchorPath = a;
            return true;
        }

        /// <summary>
        /// 繩上的點 `rope:[關聯/…/]繩名/比例`（TASK-0511）：<paramref name="oRopePath"/> ＝ 母繩路徑（相對於子繩所在那件作品）、<paramref name="oT"/> ＝ 比例 0..1。
        /// </summary>
        public static bool TryParseRopePoint(string iText, out string oRopePath, out double oT, out string oBad)
        {
            oRopePath = ""; oT = 0; oBad = "";
            string a = (iText ?? "").Trim();
            string[] aSegs = a.Substring(RopePrefix.Length).Split('/');
            if (aSegs.Length < 2) { oBad = "繩上的點 `" + a + "` 要寫成 rope:繩名/比例（比例 0..1 ＝ 沿母繩的長度比；跨關聯 rope:關聯/…/繩名/比例）"; return false; }
            string aNum = aSegs[aSegs.Length - 1].Trim();
            if (!double.TryParse(aNum, NumberStyles.Float, CultureInfo.InvariantCulture, out double t) || double.IsNaN(t) || t < 0 || t > 1)
            { oBad = "繩上的點 `" + a + "`：比例要是 0..1 的數（0 ＝ 母繩的 from、1 ＝ to；got '" + aNum + "'）"; return false; }
            for (int i = 0; i < aSegs.Length - 1; i++)
                if (SCP_SculptLinks.ValidName(aSegs[i]) is string aSegBad) { oBad = "繩上的點 `" + a + "` 的 `" + aSegs[i] + "`：" + aSegBad; return false; }
            oRopePath = string.Join("/", aSegs, 0, aSegs.Length - 1);
            oT = t;
            return true;
        }

        /// <summary>顏色：1..255 ＝ 調色盤 index；#RRGGBB。回 null ＝ 合法；<paramref name="oNorm"/> 是存檔用的寫法（index 原樣、hex 小寫）。</summary>
        public static string? TryParseColor(string iText, out byte oR, out byte oG, out byte oB, out string oNorm)
        {
            oR = oG = oB = 0; oNorm = "";
            string a = (iText ?? "").Trim();
            if (a.StartsWith("#", StringComparison.Ordinal))
            {
                if (a.Length != 7 || !int.TryParse(a.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int aHex))
                    return "color 要是 #RRGGBB 或調色盤 index 1..255：'" + iText + "'";
                oR = (byte)(aHex >> 16); oG = (byte)(aHex >> 8); oB = (byte)aHex;
                oNorm = a.ToLowerInvariant();
                return null;
            }
            if (!int.TryParse(a, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aIdx) || aIdx < 1 || aIdx > 255)
                return "color 要是 #RRGGBB 或調色盤 index 1..255：'" + iText + "'";
            SCP_CanvasPalette.IndexToRgb(aIdx, out oR, out oG, out oB);
            oNorm = aIdx.ToString(CultureInfo.InvariantCulture);
            return null;
        }

        // ───────────────────────── 形狀：懸鏈線 ─────────────────────────

        /// <summary>
        /// 兩點之間、繩長 ＝ slack × 直線距離、只受重力（−z）的形狀。回傳折線點（含兩端，xyz 攤平）、實際折線長、最大下垂量（中段離兩端連線往下最多幾格）。
        /// <para>拉直（slack ≈ 1）⇒ 直線；兩端幾乎在同一條鉛直線上 ⇒ 直線＋<paramref name="oVertical"/>（沒有往旁邊垂的方向）。</para>
        /// <para>懸鏈線：水平距離 h、高差 v、繩長 L ⇒ 解 2a·sinh(h/2a) ＝ √(L²−v²)（二分法，固定 200 次 ⇒ 決定性）；
        /// 頂點水平位置 s₀ ＝ h/2 − a·artanh(v/L)；z(s) ＝ a·cosh((s−s₀)/a) ＋ C。</para>
        /// </summary>
        public static double[] Solve(double[] iFrom, double[] iTo, double iSlack, out double oLength, out double oMaxSag, out bool oVertical, out bool oClamped)
        {
            oVertical = false; oClamped = false; oMaxSag = 0;
            double dx = iTo[0] - iFrom[0], dy = iTo[1] - iFrom[1], dz = iTo[2] - iFrom[2];
            double h = Math.Sqrt(dx * dx + dy * dy), chord = Math.Sqrt(h * h + dz * dz);
            if (chord < 1e-9) { oLength = 0; return new[] { iFrom[0], iFrom[1], iFrom[2], iTo[0], iTo[1], iTo[2] }; }
            if (iSlack <= 1 + 1e-9 || h < 1e-6 * chord)
            {
                oVertical = h < 1e-6 * chord && iSlack > 1 + 1e-9;
                oLength = chord;
                return new[] { iFrom[0], iFrom[1], iFrom[2], iTo[0], iTo[1], iTo[2] };
            }
            double L = iSlack * chord;
            double aTarget = Math.Sqrt(L * L - dz * dz) / h;   // sinh(u)/u 要等於它（> 1）
            double lo = 1e-9, hi = MaxU;
            if (Math.Sinh(hi) / hi < aTarget) { oClamped = true; lo = hi; }
            else
                for (int i = 0; i < 200; i++)
                {
                    double mid = 0.5 * (lo + hi);
                    if (Math.Sinh(mid) / mid < aTarget) lo = mid; else hi = mid;
                }
            double u = 0.5 * (lo + hi);
            if (oClamped) u = MaxU;
            double a = h / (2 * u);
            double s0 = h / 2 - a * Atanh(dz / L);
            double C = iFrom[2] - a * Math.Cosh(-s0 / a);
            int n = Math.Max(16, Math.Min(256, (int)Math.Ceiling(L)));
            var aPts = new double[(n + 1) * 3];
            oLength = 0;
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n, s = h * t;
                double z = i == 0 ? iFrom[2] : i == n ? iTo[2] : a * Math.Cosh((s - s0) / a) + C;   // 兩端釘死在端點（⛔ 不讓浮點誤差把端點挪開）
                aPts[i * 3] = iFrom[0] + dx * t;
                aPts[i * 3 + 1] = iFrom[1] + dy * t;
                aPts[i * 3 + 2] = z;
                double aSag = iFrom[2] + dz * t - z;
                if (aSag > oMaxSag) oMaxSag = aSag;
                if (i > 0)
                {
                    double ex = aPts[i * 3] - aPts[i * 3 - 3], ey = aPts[i * 3 + 1] - aPts[i * 3 - 2], ez = aPts[i * 3 + 2] - aPts[i * 3 - 1];
                    oLength += Math.Sqrt(ex * ex + ey * ey + ez * ez);
                }
            }
            return aPts;
        }

        static double Atanh(double x) => 0.5 * Math.Log((1 + x) / (1 - x));

        /// <summary>
        /// 折線上沿長度比 <paramref name="iT"/>（0..1）的那一點（TASK-0511 子繩的端點）：0 ＝ 第一點、1 ＝ 最後一點（逐位元，⛔ 不經過浮點累加）；
        /// 中間照每一段的長度累加、段內線性內插。長度 0 的折線 ⇒ 第一點。
        /// </summary>
        public static double[] PointAt(double[] iLine, double iT)
        {
            int n = iLine.Length / 3;
            if (n == 0) return new double[3];
            if (n == 1 || iT <= 0) return new[] { iLine[0], iLine[1], iLine[2] };
            if (iT >= 1) return new[] { iLine[(n - 1) * 3], iLine[(n - 1) * 3 + 1], iLine[(n - 1) * 3 + 2] };
            var aSeg = new double[n - 1];
            double aTotal = 0;
            for (int i = 0; i + 1 < n; i++)
            {
                double ex = iLine[i * 3 + 3] - iLine[i * 3], ey = iLine[i * 3 + 4] - iLine[i * 3 + 1], ez = iLine[i * 3 + 5] - iLine[i * 3 + 2];
                aSeg[i] = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                aTotal += aSeg[i];
            }
            if (aTotal < 1e-12) return new[] { iLine[0], iLine[1], iLine[2] };
            double aWant = iT * aTotal, aAcc = 0;
            for (int i = 0; i + 1 < n; i++)
            {
                if (aAcc + aSeg[i] >= aWant || i == n - 2)
                {
                    double f = aSeg[i] < 1e-12 ? 0 : Math.Max(0, Math.Min(1, (aWant - aAcc) / aSeg[i]));
                    return new[] { iLine[i * 3] + (iLine[i * 3 + 3] - iLine[i * 3]) * f, iLine[i * 3 + 1] + (iLine[i * 3 + 4] - iLine[i * 3 + 1]) * f, iLine[i * 3 + 2] + (iLine[i * 3 + 5] - iLine[i * 3 + 2]) * f };
                }
                aAcc += aSeg[i];
            }
            return new[] { iLine[(n - 1) * 3], iLine[(n - 1) * 3 + 1], iLine[(n - 1) * 3 + 2] };
        }

        // ───────────────────────── 座標：關聯路徑 → 組裝件 ─────────────────────────

        /// <summary>
        /// 一個連續點經過一條關聯換到上一層：鏡像（在零件自己的框 W×H×D 裡：x ⇒ W−x…）→ 繞 Z 轉（同 <see cref="SCP_SculptLinks.Turn"/>：90 ⇒ (H−y, x)…）→ ＋at。
        /// <para>⭐ 跟 voxel 的 <see cref="SCP_SculptLinks.Transform"/> 是同一個公式的連續版：格 k 的格心 k＋0.5 換過去，正好是那一格換過去之後的格心。</para>
        /// </summary>
        public static double[] ThroughLink(SCP_SculptLink iLink, double[] iP)
        {
            double W = iLink.pivot[0], H = iLink.pivot[1], D = iLink.pivot[2];
            double x = iP[0], y = iP[1], z = iP[2];
            string m = iLink.mirror ?? "";
            if (m.IndexOf('x') >= 0) x = W - x;
            if (m.IndexOf('y') >= 0) y = H - y;
            if (m.IndexOf('z') >= 0) z = D - z;
            double ox = x, oy = y;
            if (iLink.turn == 90) { ox = H - y; oy = x; }
            else if (iLink.turn == 180) { ox = W - x; oy = H - y; }
            else if (iLink.turn == 270) { ox = y; oy = W - x; }
            return new[] { ox + iLink.at[0], oy + iLink.at[1], z + iLink.at[2] };
        }

        /// <summary>關聯樹攤成「路徑 → 節點」。</summary>
        public static Dictionary<string, SCP_SculptLinkNode> IndexTree(List<SCP_SculptLinkNode>? iTree)
        {
            var aOut = new Dictionary<string, SCP_SculptLinkNode>(StringComparer.Ordinal);
            void Walk(List<SCP_SculptLinkNode> iNodes) { foreach (var n in iNodes) { aOut[n.Path] = n; Walk(n.Children); } }
            if (iTree != null) Walk(iTree);
            return aOut;
        }

        /// <summary>這條關聯路徑這次有沒有畫（每一層祖先都要被選到 —— 跟組裝器一層層往下走同一個判準）。根（空路徑）永遠畫。</summary>
        public static bool PathDrawn(string iPath, Dictionary<string, SCP_SculptLinkNode> iIndex, SCP_SculptLinkSelection iSel)
        {
            if (iPath.Length == 0) return true;
            string[] aSegs = iPath.Split('/');
            string aPrefix = "";
            foreach (string s in aSegs)
            {
                aPrefix = aPrefix.Length == 0 ? s : aPrefix + "/" + s;
                if (!iIndex.TryGetValue(aPrefix, out SCP_SculptLinkNode? n)) return false;
                if (!iSel.Includes(aPrefix, n.Link.enabled)) return false;
            }
            return true;
        }

        /// <summary>某一層（關聯路徑 <paramref name="iFrame"/>）的點換到組裝件座標：由內往外逐層過關聯。</summary>
        public static double[] ToRoot(string iFrame, double[] iP, Dictionary<string, SCP_SculptLinkNode> iIndex)
        {
            double[] p = iP;
            string f = iFrame;
            while (f.Length > 0)
            {
                p = ThroughLink(iIndex[f].Link, p);
                int k = f.LastIndexOf('/');
                f = k < 0 ? "" : f.Substring(0, k);
            }
            return p;
        }

        /// <summary>
        /// 固定點路徑（相對於 <paramref name="iFrame"/>）解到組裝件座標。
        /// 回 null ＝ 解得到；否則是壞掉的原因（固定點／關聯路徑不存在、檔讀不了）。<paramref name="oFrame"/> ＝ 固定點所在那一層的完整關聯路徑（判有沒有畫用）。
        /// </summary>
        public static string? ResolveAnchor(SCP_SculptWorks iWorks, string iRootId, string iFrame, string iAnchorPath,
                                            Dictionary<string, SCP_SculptLinkNode> iIndex, Dictionary<string, SCP_SculptAnchorFile> ioCache,
                                            out double[]? oPoint, out string oFrame)
        {
            oPoint = null; oFrame = "";
            int k = iAnchorPath.LastIndexOf('/');
            string aRel = k < 0 ? "" : iAnchorPath.Substring(0, k), aName = k < 0 ? iAnchorPath : iAnchorPath.Substring(k + 1);
            oFrame = aRel.Length == 0 ? iFrame : iFrame.Length == 0 ? aRel : iFrame + "/" + aRel;
            string aWork;
            if (oFrame.Length == 0) aWork = iRootId;
            else if (iIndex.TryGetValue(oFrame, out SCP_SculptLinkNode? n)) aWork = n.Card.id;
            else return "關聯路徑 `" + oFrame + "` 不存在（固定點 `" + iAnchorPath + "`）";
            SCP_SculptAnchorFile aFile;
            if (!ioCache.TryGetValue(aWork, out SCP_SculptAnchorFile? aHit))
            {
                try { aFile = ReadAnchors(iWorks.SpacePaths(aWork)); }
                catch (InvalidOperationException e) { return e.Message; }
                ioCache[aWork] = aFile;
            }
            else aFile = aHit;
            SCP_SculptAnchor? aAnchor = aFile.anchors.Find(a => a.name == aName);
            if (aAnchor == null)
                return "作品 " + aWork + (oFrame.Length > 0 ? "（`" + oFrame + "`）" : "") + " 沒有固定點 `" + aName + "`（現有：" + (aFile.anchors.Count == 0 ? "（沒有）" : string.Join("、", aFile.anchors.ConvertAll(a => a.name))) + "）";
            oPoint = ToRoot(oFrame, new[] { aAnchor.at[0] + 0.5, aAnchor.at[1] + 0.5, aAnchor.at[2] + 0.5 }, iIndex);
            return null;
        }

        /// <summary>一條繩在第一趟解完、還沒定狀態時要帶到第二趟的東西（子繩要等母繩）。</summary>
        sealed class Pending
        {
            public string Frame = "";
            public List<string> Why = new List<string>();
            /// <summary>平常關著的組（null ＝ 沒有）。</summary>
            public string? GroupOff;
            /// <summary>掛的母繩這次沒畫（子繩跟著不畫）的原因。</summary>
            public List<string> ParentOff = new List<string>();
        }

        /// <summary>
        /// 一次觀測的所有繩：組裝件自己的＋關聯樹上每一層的（照那一層換座標）。<paramref name="iTree"/> null ＝ 沒有關聯。
        /// 每條都有一筆讀數（ok／hidden／disabled／broken）；ok 的帶 <see cref="SCP_SculptRopeReading.Geom"/>。
        /// <paramref name="iSails"/>：帆角端點的解法（TASK-0503，帆的觀測注入）；null ⇒ 綁帆角的繩一律 broken 並說原因。
        /// 繩子所在那一層這次沒畫 ⇒ hidden（原因寫哪一層）；壞掉的綁定 ⇒ broken —— ⛔ 兩種都照列、不靜默消失。
        /// 子繩（TASK-0511）第二趟解：母繩先解（巢狀照深度）、位置 ＝ 母繩折線上的那一點；繞成環 ⇒ 環上每一條都 broken 並印出環。
        /// ropes.json 讀不了 ⇒ InvalidOperationException 照丟（呼叫端擋下）。
        /// </summary>
        public static List<SCP_SculptRopeReading> Resolve(SCP_SculptWorks iWorks, string iRootId, List<SCP_SculptLinkNode>? iTree,
                                                          SCP_SculptLinkSelection iLinkSel, SCP_SculptRopeSelection iRopeSel,
                                                          SCP_SculptRopeFile? iRootOverride = null, SCP_SculptSailEndResolver? iSails = null,
                                                          SCP_SculptModelSet? iModels = null)
        {
            var aIndex = IndexTree(iTree);
            var aPoints = new SCP_SculptPointResolver(iWorks, iRootId, aIndex, iLinkSel) { Sails = iSails, Models = iModels };
            var aRopeCache = new Dictionary<string, SCP_SculptRopeFile>(StringComparer.Ordinal);
            // 寫入前的驗證：組裝件自己那份改用「還沒存的那一份」（同 SCP_SculptLinks.Tree 的 override）—— ⛔ 不另寫一份端點邏輯
            if (iRootOverride != null) aRopeCache[iRootId] = iRootOverride;
            var aOut = new List<SCP_SculptRopeReading>();
            var aPend = new Dictionary<SCP_SculptRopeReading, Pending>();
            var aFrames = new List<string> { "" };
            var aPaths = new List<string>();
            SCP_SculptLinks.Paths(iTree ?? new List<SCP_SculptLinkNode>(), aPaths);
            aFrames.AddRange(aPaths);
            var aSets = new List<(string Frame, string Work, SCP_SculptRopeFile File, SCP_SculptRatlines Set)>();   // 橫繩組（TASK-0678）：母繩解完才展開
            // ① 第一趟：一般的端點（固定點、座標、帆角、模型）；子繩的 rope: 端點留到第二趟
            foreach (string aFrame in aFrames)
            {
                string aWork = aFrame.Length == 0 ? iRootId : aIndex[aFrame].Card.id;
                if (!aRopeCache.TryGetValue(aWork, out SCP_SculptRopeFile? aFile)) { aFile = ReadRopes(iWorks.SpacePaths(aWork)); aRopeCache[aWork] = aFile; }
                bool aFrameDrawn = PathDrawn(aFrame, aIndex, iLinkSel);
                foreach (SCP_SculptRatlines s in aFile.ratlines) aSets.Add((aFrame, aWork, aFile, s));
                foreach (SCP_SculptRope r in aFile.ropes)
                {
                    var aR = new SCP_SculptRopeReading
                    {
                        Path = aFrame.Length == 0 ? r.name : aFrame + "/" + r.name, Owner = aWork, Rope = r,
                        Group = r.group.Length == 0 ? "" : aFrame.Length == 0 ? r.group : aFrame + "/" + r.group,
                        Child = IsChild(r),
                    };
                    var aP = new Pending { Frame = aFrame, GroupOff = GroupDisabled(aFile, r.group) };
                    bool aHidden = !aFrameDrawn;
                    if (aHidden) aP.Why.Add("繩子所在的 `" + aFrame + "` 這次沒畫");
                    double[]? aEnds0 = null, aEnds1 = null;
                    for (int e = 0; e < 2; e++)
                    {
                        string aText = e == 0 ? r.from : r.to;
                        if (IsRopePoint(aText)) continue;   // 第二趟
                        string? aBad = aPoints.Resolve(aFrame, aText, true, out double[]? aPt, out string aSailKey, out string aHiddenWhy);
                        if (aBad != null) { aR.State = SCP_SculptRopeReading.Broken; aP.Why.Add((e == 0 ? "from：" : "to：") + aBad); continue; }
                        if (aSailKey.Length > 0) { if (e == 0) aR.FromSail = aSailKey; else aR.ToSail = aSailKey; }
                        if (aHiddenWhy.Length > 0) { aHidden = true; aP.Why.Add((e == 0 ? "from" : "to") + aHiddenWhy); }
                        if (e == 0) aEnds0 = aPt; else aEnds1 = aPt;
                    }
                    aR.From = aEnds0; aR.To = aEnds1;
                    aR.LinkHidden = aHidden;
                    aPend[aR] = aP;
                    if (!aR.Child) Settle(aR, aP, iRopeSel);
                    aOut.Add(aR);
                }
            }
            // ② 第二趟：子繩（TASK-0511）—— 母繩先解（母繩也是子繩 ⇒ 遞迴）；正在解的又被指到 ⇒ 繞成環
            var aByPath = new Dictionary<string, SCP_SculptRopeReading>(StringComparer.Ordinal);
            foreach (var x in aOut) aByPath[x.Path] = x;
            var aMark = new Dictionary<SCP_SculptRopeReading, int>();   // 1 ＝ 解到一半、2 ＝ 解完
            var aStack = new List<SCP_SculptRopeReading>();
            var aCycle = new Dictionary<SCP_SculptRopeReading, string>();
            void Bind(SCP_SculptRopeReading c)
            {
                if (aMark.TryGetValue(c, out int aDone) && aDone == 2) return;
                aMark[c] = 1;
                aStack.Add(c);
                Pending aP = aPend[c];
                for (int e = 0; e < 2; e++)
                {
                    string aText = e == 0 ? c.Rope.from : c.Rope.to;
                    if (!IsRopePoint(aText)) continue;
                    string aLabel = e == 0 ? "from" : "to";
                    TryParseRopePoint(aText, out string aRel, out double t, out _);
                    string aParentPath = aP.Frame.Length == 0 ? aRel : aP.Frame + "/" + aRel;
                    if (!aByPath.TryGetValue(aParentPath, out SCP_SculptRopeReading? p))
                    {
                        c.State = SCP_SculptRopeReading.Broken;
                        int k = aParentPath.LastIndexOf('/');
                        string aLayer = k < 0 ? "" : aParentPath.Substring(0, k);
                        var aHere = aOut.FindAll(x => aPend[x].Frame == aLayer).ConvertAll(x => x.Rope.name);
                        aP.Why.Add(aLabel + "：沒有繩 `" + aParentPath + "`（那一層現有：" + (aHere.Count == 0 ? "（沒有）" : string.Join("、", aHere)) + "）");
                        continue;
                    }
                    // 先記下掛在誰上面（壞掉、繞成環也記 —— 拿掉母繩之前要找得到掛在它上面的子繩）
                    if (e == 0) { c.FromParent = p; c.FromT = t; } else { c.ToParent = p; c.ToT = t; }
                    if (aMark.TryGetValue(p, out int aState) && aState == 1)
                    {
                        // 繞成環：從 p 到 c 那一段（含兩端）再回到 p
                        int k = aStack.IndexOf(p);
                        var aRing = aStack.GetRange(k, aStack.Count - k);
                        string aText2 = "繞成環：" + string.Join(" → ", aRing.ConvertAll(x => x.Path)) + " → " + p.Path;
                        foreach (var x in aRing) aCycle[x] = aText2;
                        continue;
                    }
                    if (p.Child) Bind(p);
                    if (p.State == SCP_SculptRopeReading.Broken)
                    {
                        c.State = SCP_SculptRopeReading.Broken;
                        aP.Why.Add(aLabel + "：掛的母繩 `" + p.Path + "` 壞了");
                        continue;
                    }
                    if (p.Line == null)
                    {
                        c.State = SCP_SculptRopeReading.Broken;
                        aP.Why.Add(aLabel + "：掛的母繩 `" + p.Path + "` 沒有形狀（兩端沒解出來）");
                        continue;
                    }
                    double[] q = PointAt(p.Line, t);
                    if (e == 0) c.From = q; else c.To = q;
                    c.Depth = Math.Max(c.Depth, p.Depth + 1);
                    if (p.FromSail.Length > 0 || p.ToSail.Length > 0 || p.FollowsSail) c.FollowsSail = true;
                    if (p.LinkHidden) { c.LinkHidden = true; aP.Why.Add(aLabel + " 掛的母繩 `" + p.Path + "` 這次沒組進來"); }
                    else if (p.State != SCP_SculptRopeReading.Ok && !p.OnlyFiltered) aP.ParentOff.Add(aLabel + " 掛的母繩 `" + p.Path + "` 這次沒畫（" + p.State + "）");
                }
                aStack.RemoveAt(aStack.Count - 1);
                aMark[c] = 2;
                if (aCycle.TryGetValue(c, out string? aRingWhy)) { c.State = SCP_SculptRopeReading.Broken; aP.Why.Add(aRingWhy); }
                Settle(c, aP, iRopeSel);
            }
            // ③ 橫繩組（TASK-0678）：照母繩現在的形狀展開成子繩（rope:母繩/比例），接著跟手寫的子繩一起 Bind —— ⛔ 不另寫一份解法與畫法
            foreach (var (aFrame, aWork, aFile, s) in aSets) ExpandRatlines(aFrame, aWork, aFile, s);
            foreach (var x in aOut.ToArray()) if (x.Child) Bind(x);
            return aOut;

            void ExpandRatlines(string aFrame, string aWork, SCP_SculptRopeFile aFile, SCP_SculptRatlines s)
            {
                string aSetPath = aFrame.Length == 0 ? s.name : aFrame + "/" + s.name;
                string aGroup = s.group.Length == 0 ? "" : aFrame.Length == 0 ? s.group : aFrame + "/" + s.group;
                string? aGroupOff = GroupDisabled(aFile, s.group);
                bool aFrameDrawn = PathDrawn(aFrame, aIndex, iLinkSel);
                string? aBad = null;
                var aPar = new SCP_SculptRopeReading[2];
                string[] aRel = { s.a, s.b };
                for (int e = 0; e < 2 && aBad == null; e++)
                {
                    string aLabel = e == 0 ? "a" : "b", aPP = aFrame.Length == 0 ? aRel[e] : aFrame + "/" + aRel[e];
                    if (!aByPath.TryGetValue(aPP, out SCP_SculptRopeReading? p) || p.Ratlines.Length > 0) { aBad = aLabel + "：沒有繩 `" + aPP + "`（母繩要是手寫的繩，不能是別組展開的橫繩）"; break; }
                    if (p.Child) Bind(p);
                    if (p.State == SCP_SculptRopeReading.Broken) aBad = aLabel + "：母繩 `" + p.Path + "` 壞了";
                    else if (p.Line == null) aBad = aLabel + "：母繩 `" + p.Path + "` 沒有形狀（兩端沒解出來）";
                    aPar[e] = p!;
                }
                double[]? aFoot = null, aHead = null;
                if (aBad == null && s.climb)
                {
                    if (aPoints.Resolve(aFrame, s.foot, false, out aFoot, out _, out _) is string aFootBad) aBad = "繩梯 foot：" + aFootBad;
                    else if (aPoints.Resolve(aFrame, s.head, false, out aHead, out _, out _) is string aHeadBad) aBad = "繩梯 head：" + aHeadBad;
                }
                List<double[]> aRungs = aBad == null ? Rungs(aPar[0].Line!, aPar[1].Line!, s, out aBad) : new List<double[]>();
                var aBase = new SCP_SculptRope { color = s.color, thickness = s.thickness, slack = 1, enabled = s.enabled, group = s.group, note = s.note, persona = s.persona, created_at = s.created_at };
                if (aBad != null)
                {
                    // 整組一筆 broken（⛔ 不靜默消失）：路徑 ＝ 組的路徑
                    var aR = new SCP_SculptRopeReading { Path = aSetPath, Owner = aWork, Group = aGroup, Ratlines = aSetPath, State = SCP_SculptRopeReading.Broken };
                    aR.Rope = Clone(aBase, s.name, RopePrefix + s.a + "/0", RopePrefix + s.b + "/0");
                    var aP = new Pending { Frame = aFrame, GroupOff = aGroupOff };
                    aP.Why.Add("橫繩組 `" + aSetPath + "`：" + aBad);
                    aPend[aR] = aP;
                    Settle(aR, aP, iRopeSel);
                    aOut.Add(aR);
                    return;
                }
                for (int k = 0; k < aRungs.Count; k++)
                {
                    string aName = s.name + "." + (k + 1).ToString(aRungs.Count >= 100 ? "000" : "00", CultureInfo.InvariantCulture);
                    SCP_SculptRope r = Clone(aBase, aName, RopePrefix + s.a + "/" + aRungs[k][0].ToString("R", CultureInfo.InvariantCulture),
                                                           RopePrefix + s.b + "/" + aRungs[k][1].ToString("R", CultureInfo.InvariantCulture));
                    // meta：給下游（遊戲）認「這是哪一組的第幾道、能不能爬、從哪裡上下」（座標 ＝ 組裝件座標，同繩的 pts）
                    r.meta["kind"] = "ratline";
                    r.meta["ratlines"] = aSetPath;
                    r.meta["rung"] = k + 1;
                    r.meta["rungs"] = aRungs.Count;
                    if (s.climb) { r.meta["climb"] = 1; r.meta["foot"] = Xyz(aFoot!); r.meta["head"] = Xyz(aHead!); }
                    var aR = new SCP_SculptRopeReading { Path = aFrame.Length == 0 ? aName : aFrame + "/" + aName, Owner = aWork, Rope = r, Group = aGroup, Child = true, Ratlines = aSetPath };
                    var aP = new Pending { Frame = aFrame, GroupOff = aGroupOff };
                    if (!aFrameDrawn) { aR.LinkHidden = true; aP.Why.Add("繩子所在的 `" + aFrame + "` 這次沒畫"); }
                    aPend[aR] = aP;
                    aOut.Add(aR);
                    if (!aByPath.ContainsKey(aR.Path)) aByPath[aR.Path] = aR;
                }
            }
        }

        static SCP_SculptRope Clone(SCP_SculptRope b, string iName, string iFrom, string iTo)
            => new SCP_SculptRope { name = iName, from = iFrom, to = iTo, color = b.color, thickness = b.thickness, slack = 1, enabled = b.enabled, group = b.group, note = b.note, persona = b.persona, created_at = b.created_at };

        static string Xyz(double[] p)
            => p[0].ToString("0.###", CultureInfo.InvariantCulture) + "," + p[1].ToString("0.###", CultureInfo.InvariantCulture) + "," + p[2].ToString("0.###", CultureInfo.InvariantCulture);

        /// <summary>
        /// 橫繩組的每一道（TASK-0678）：兩條母繩的折線（組裝件座標）之間，z 從「較高的下端＋start」到「較低的上端−stop」每 step 格一道（z0 ＋ k·step，⛔ 不累加）；
        /// 每道回 {a 上的比例, b 上的比例}（沿各自折線的長度比，同 `rope:` 的比例）。一道兩端距離短於 min_span ⇒ 停（往上收攏）。
        /// 一道都沒有、某一高度母繩碰不到、超過 <see cref="MaxRungs"/> 道 ⇒ <paramref name="oBad"/>。
        /// </summary>
        public static List<double[]> Rungs(double[] iA, double[] iB, SCP_SculptRatlines s, out string? oBad)
        {
            oBad = null;
            var aOut = new List<double[]>();
            ZRange(iA, out double aLoA, out double aHiA);
            ZRange(iB, out double aLoB, out double aHiB);
            double z0 = Math.Max(aLoA, aLoB) + s.start, z1 = Math.Min(aHiA, aHiB) - s.stop;
            if (z1 < z0) { oBad = "兩條母繩共同的高度留白之後是空的（z " + z0.ToString("0.##", CultureInfo.InvariantCulture) + " > " + z1.ToString("0.##", CultureInfo.InvariantCulture) + "；start／stop 調小，或母繩不夠直立）"; return aOut; }
            for (int k = 0; ; k++)
            {
                double z = z0 + k * s.step;
                if (z > z1 + 1e-9) break;
                double ta = RatioAtZ(iA, z), tb = RatioAtZ(iB, z);
                if (double.IsNaN(ta) || double.IsNaN(tb)) { oBad = "z " + z.ToString("0.##", CultureInfo.InvariantCulture) + " 碰不到母繩 " + (double.IsNaN(ta) ? "a" : "b"); return aOut; }
                double[] p = PointAt(iA, ta), q = PointAt(iB, tb);
                double dx = p[0] - q[0], dy = p[1] - q[1], dz = p[2] - q[2];
                if (s.min_span > 0 && Math.Sqrt(dx * dx + dy * dy + dz * dz) < s.min_span) break;
                if (aOut.Count >= MaxRungs) { oBad = "超過 " + MaxRungs + " 道（step 太小？）"; return aOut; }
                aOut.Add(new[] { ta, tb });
            }
            if (aOut.Count == 0) oBad = "一道都沒有（第一道就短於 min_span " + s.min_span.ToString("0.##", CultureInfo.InvariantCulture) + "）";
            return aOut;
        }

        static void ZRange(double[] iLine, out double oLo, out double oHi)
        {
            int n = iLine.Length / 3;
            double a = iLine[2], b = iLine[(n - 1) * 3 + 2];
            oLo = Math.Min(a, b); oHi = Math.Max(a, b);
        }

        /// <summary>
        /// 折線上高度 <paramref name="iZ"/> 那一點的長度比（0 ＝ 第一點、1 ＝ 最後一點）：從**低的那一端**往上找第一個穿過 z 的地方（垂下來的繩可能穿兩次 ⇒ 取靠下端那一次）；碰不到 ⇒ NaN。
        /// </summary>
        public static double RatioAtZ(double[] iLine, double iZ)
        {
            int n = iLine.Length / 3;
            if (n < 2) return double.NaN;
            var aSeg = new double[n - 1];
            double aTotal = 0;
            for (int i = 0; i + 1 < n; i++)
            {
                double ex = iLine[i * 3 + 3] - iLine[i * 3], ey = iLine[i * 3 + 4] - iLine[i * 3 + 1], ez = iLine[i * 3 + 5] - iLine[i * 3 + 2];
                aSeg[i] = Math.Sqrt(ex * ex + ey * ey + ez * ez);
                aTotal += aSeg[i];
            }
            if (aTotal < 1e-12) return double.NaN;
            bool aUp = iLine[2] <= iLine[(n - 1) * 3 + 2];
            double aAcc = 0, aHit = double.NaN;
            for (int i = 0; i + 1 < n; i++)
            {
                double za = iLine[i * 3 + 2], zb = iLine[i * 3 + 5];
                if ((za - iZ) * (zb - iZ) <= 0 && Math.Abs(zb - za) > 1e-12)
                {
                    aHit = (aAcc + aSeg[i] * (iZ - za) / (zb - za)) / aTotal;
                    if (aUp) return aHit;   // 從第一點（低端）往上：第一次就是
                }
                aAcc += aSeg[i];
            }
            return aHit;   // 低端在最後一點 ⇒ 最後一次穿過的（離低端最近）
        }

        /// <summary>
        /// 定一條繩這次的狀態（優先序：broken ＞ 自己關著 ＞ 組關著 ＞ ropes=0 ＞ hide_ropes ＞ hide_rope_groups ＞ rope_groups 沒點到 ＞ 綁的那一層沒組進來 ＞ 母繩沒畫），
        /// 再算形狀：一般繩 ⇒ 懸鏈線；子繩 ⇒ 兩端拉直。ok 的帶 <see cref="SCP_SculptRopeReading.Geom"/>（子繩 <see cref="ChildSides"/> 邊）。
        /// </summary>
        static void Settle(SCP_SculptRopeReading aR, Pending aP, SCP_SculptRopeSelection iRopeSel)
        {
            SCP_SculptRope r = aR.Rope;
            var aWhy = aP.Why;
            if (aR.State != SCP_SculptRopeReading.Broken)
            {
                // 繩子所在那件作品這次沒畫 ⇒ 照列、標 hidden（⛔ 不略過：hide_ropes 點名它、頁面的開關都要找得到它）
                if (!r.enabled) { aR.State = SCP_SculptRopeReading.Disabled; aWhy.Add("這條繩關著（enabled=0）"); }
                else if (aP.GroupOff != null) { aR.State = SCP_SculptRopeReading.Disabled; aWhy.Add("組 `" + aP.GroupOff + "` 關著（work sub=ropegroup enabled=0）"); }
                else if (iRopeSel.None) { aR.State = SCP_SculptRopeReading.Hidden; aWhy.Add("這次不畫繩（ropes=0）"); }
                else if (iRopeSel.Hide.Contains(aR.Path)) { aR.State = SCP_SculptRopeReading.Hidden; aWhy.Add("這次拿掉（hide_ropes）"); }
                else if (iRopeSel.HideGroups.Count > 0 && SCP_SculptRopeSelection.Under(aR.Group, iRopeSel.HideGroups)) { aR.State = SCP_SculptRopeReading.Hidden; aWhy.Add("這次拿掉這一組（hide_rope_groups）"); }
                else if (iRopeSel.OnlyGroups.Count > 0 && !SCP_SculptRopeSelection.Under(aR.Group, iRopeSel.OnlyGroups)) { aR.State = SCP_SculptRopeReading.Hidden; aR.OnlyFiltered = true; aWhy.Add("這次只畫 rope_groups 點到的組"); }
                else if (aR.LinkHidden) aR.State = SCP_SculptRopeReading.Hidden;
                else if (aP.ParentOff.Count > 0) { aR.State = SCP_SculptRopeReading.Hidden; aWhy.AddRange(aP.ParentOff); }
            }
            if (aR.From != null && aR.To != null)
            {
                double dx = aR.To[0] - aR.From[0], dy = aR.To[1] - aR.From[1], dz = aR.To[2] - aR.From[2];
                aR.Chord = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                double[] aPts;
                if (aR.Child)
                {
                    // 子繩不算物理：兩端拉直
                    aPts = new[] { aR.From[0], aR.From[1], aR.From[2], aR.To[0], aR.To[1], aR.To[2] };
                    aR.Length = aR.Chord; aR.MaxSag = 0; aR.Vertical = false;
                }
                else
                {
                    aPts = Solve(aR.From, aR.To, r.slack, out double aLen, out double aSag, out bool aVert, out bool aClamped);
                    aR.Length = aLen; aR.MaxSag = aSag; aR.Vertical = aVert;
                    if (aVert) aWhy.Add("兩端在同一條鉛直線上 ⇒ 畫直線（鬆度沒有往旁邊垂的方向）");
                    if (aClamped) aWhy.Add("鬆到幾乎對折 ⇒ 照上限的弧度畫（slack 調小）");
                }
                aR.Line = aPts;
                if (aR.State == SCP_SculptRopeReading.Ok)
                {
                    TryParseColor(r.color, out byte cr, out byte cg, out byte cb, out _);
                    aR.Geom = new SCP_SculptRopeGeom { Path = aR.Path, Points = aPts, Radius = r.thickness / 2, R = cr, G = cg, B = cb, Sides = aR.Child ? ChildSides : 0 };
                }
            }
            aR.Reason = string.Join("；", aWhy);
        }

        /// <summary>
        /// 母繩的形狀變了之後重放子繩的兩端（TASK-0511）：帆模擬完（<see cref="SCP_SculptSailRig.Refresh"/>）綁帆角的繩換了形狀 ⇒ 掛在它們上面的子繩跟著走。
        /// <paramref name="iOnlyFollowsSail"/> ＝ 只重算會跟著帆動的那幾條（觀測頁每幀）。由淺到深（母繩先、子繩後）；broken 的不動。
        /// </summary>
        public static void Rebind(List<SCP_SculptRopeReading> iReadings, bool iOnlyFollowsSail)
        {
            int aMax = 0;
            foreach (var r in iReadings) if (r.Child && r.Depth > aMax) aMax = r.Depth;
            for (int d = 1; d <= aMax; d++)
                foreach (SCP_SculptRopeReading c in iReadings)
                {
                    if (!c.Child || c.Depth != d || c.State == SCP_SculptRopeReading.Broken || (iOnlyFollowsSail && !c.FollowsSail)) continue;
                    if (c.FromParent?.Line != null) c.From = PointAt(c.FromParent.Line, c.FromT);
                    if (c.ToParent?.Line != null) c.To = PointAt(c.ToParent.Line, c.ToT);
                    if (c.From == null || c.To == null) continue;
                    double dx = c.To[0] - c.From[0], dy = c.To[1] - c.From[1], dz = c.To[2] - c.From[2];
                    c.Chord = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    c.Length = c.Chord;
                    c.Line = new[] { c.From[0], c.From[1], c.From[2], c.To[0], c.To[1], c.To[2] };
                    if (c.Geom != null) c.Geom = new SCP_SculptRopeGeom { Path = c.Geom.Path, Points = c.Line, Radius = c.Geom.Radius, R = c.Geom.R, G = c.Geom.G, B = c.Geom.B, Sides = c.Geom.Sides };
                }
        }

        /// <summary>讀數裡畫得出來的那幾條（給渲染器／匯出）。</summary>
        public static List<SCP_SculptRopeGeom> Drawable(List<SCP_SculptRopeReading> iReadings)
        {
            var aOut = new List<SCP_SculptRopeGeom>();
            foreach (var r in iReadings) if (r.Geom != null) aOut.Add(r.Geom);
            return aOut;
        }

        /// <summary>點（組裝件座標）給人讀：一位小數。</summary>
        public static string Fmt(double[]? iP)
            => iP == null ? "—" : iP[0].ToString("0.#", CultureInfo.InvariantCulture) + "," + iP[1].ToString("0.#", CultureInfo.InvariantCulture) + "," + iP[2].ToString("0.#", CultureInfo.InvariantCulture);

        // ───────────────────────── 管狀網格（渲染器與 obj 匯出共用）─────────────────────────

        /// <summary>
        /// 一條繩的管子：沿折線每個點一圈 <paramref name="iSides"/> 個頂點（世界座標＋外法線），相鄰兩圈之間接成四邊形。
        /// 繩自己指定了邊數（<see cref="SCP_SculptRopeGeom.Sides"/> ≥ 3，子繩 ＝ <see cref="ChildSides"/>）⇒ 照它，呼叫端照粗細給的不算 —— 每個畫繩的地方都走這一支，⛔ 不各自判。
        /// 圈的方向：切線 × 參考軸（切線接近鉛直時改用 x 軸），⇒ 決定性。兩端不封口（端點通常埋在固定點的格子裡）。
        /// <paramref name="oQuads"/>：每個四邊形 4 個頂點索引，照「從外面看逆時針（世界右手系）」的順序。
        /// </summary>
        public static void Tube(SCP_SculptRopeGeom iRope, int iSides, out List<double[]> oPos, out List<double[]> oNormal, out List<int[]> oQuads)
        {
            oPos = new List<double[]>(); oNormal = new List<double[]>(); oQuads = new List<int[]>();
            if (iRope.Sides >= 3) iSides = iRope.Sides;
            int n = iRope.PointCount;
            if (n < 2 || iRope.Radius <= 0) return;
            double[] P(int i) => new[] { iRope.Points[i * 3], iRope.Points[i * 3 + 1], iRope.Points[i * 3 + 2] };
            for (int i = 0; i < n; i++)
            {
                double[] a = P(Math.Max(0, i - 1)), b = P(Math.Min(n - 1, i + 1));
                double tx = b[0] - a[0], ty = b[1] - a[1], tz = b[2] - a[2];
                double tl = Math.Sqrt(tx * tx + ty * ty + tz * tz);
                if (tl < 1e-12) { tx = 1; ty = 0; tz = 0; tl = 1; }
                tx /= tl; ty /= tl; tz /= tl;
                double rx = 0, ry = 0, rz = 1;
                if (Math.Abs(tz) > 0.9) { rx = 1; rz = 0; }
                // u ＝ t × r、v ＝ t × u（都單位化）
                double ux = ty * rz - tz * ry, uy = tz * rx - tx * rz, uz = tx * ry - ty * rx;
                double ul = Math.Sqrt(ux * ux + uy * uy + uz * uz); ux /= ul; uy /= ul; uz /= ul;
                double vx = ty * uz - tz * uy, vy = tz * ux - tx * uz, vz = tx * uy - ty * ux;
                double[] c = P(i);
                for (int s = 0; s < iSides; s++)
                {
                    double th = 2 * Math.PI * s / iSides, cs = Math.Cos(th), sn = Math.Sin(th);
                    double nx = ux * cs + vx * sn, ny = uy * cs + vy * sn, nz = uz * cs + vz * sn;
                    oNormal.Add(new[] { nx, ny, nz });
                    oPos.Add(new[] { c[0] + nx * iRope.Radius, c[1] + ny * iRope.Radius, c[2] + nz * iRope.Radius });
                }
            }
            for (int i = 0; i + 1 < n; i++)
                for (int s = 0; s < iSides; s++)
                {
                    int s1 = (s + 1) % iSides;
                    int a = i * iSides + s, b = i * iSides + s1, c2 = (i + 1) * iSides + s1, d = (i + 1) * iSides + s;
                    // 方向用幾何判：(b−a)×(d−a) 要跟外法線同向 ⇒ 不同向就反過來（⛔ 不靠 u／v 的手性推論 —— 參考軸換過的那一段手性會翻）
                    double[] pa = oPos[a], pb = oPos[b], pd = oPos[d], na = oNormal[a];
                    double e1x = pb[0] - pa[0], e1y = pb[1] - pa[1], e1z = pb[2] - pa[2];
                    double e2x = pd[0] - pa[0], e2y = pd[1] - pa[1], e2z = pd[2] - pa[2];
                    double cx = e1y * e2z - e1z * e2y, cy = e1z * e2x - e1x * e2z, cz = e1x * e2y - e1y * e2x;
                    oQuads.Add(cx * na[0] + cy * na[1] + cz * na[2] >= 0 ? new[] { a, b, c2, d } : new[] { a, d, c2, b });
                }
        }
    }
}
