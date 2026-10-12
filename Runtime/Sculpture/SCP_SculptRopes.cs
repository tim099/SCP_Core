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
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Canvas;
using SCP.Core.Json;

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
        public string note = "", persona = "", created_at = "";
    }

    public sealed class SCP_SculptRopeFile
    {
        public int schema = 1;
        public List<SCP_SculptRope> ropes = new List<SCP_SculptRope>();
    }

    /// <summary>給渲染器／匯出的一條繩（已算好形狀、已換到組裝件座標）：折線點（世界座標，格）、半徑、顏色。</summary>
    public sealed class SCP_SculptRopeGeom
    {
        public string Path = "";
        /// <summary>xyz 攤平：點 i ＝ (Points[3i], Points[3i+1], Points[3i+2])。</summary>
        public double[] Points = new double[0];
        public double Radius;
        public byte R, G, B;
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
    }

    /// <summary>觀測時選哪些繩：All ＝ 照每條 enabled；None ＝ 都不畫（ropes=0）；Hide ＝ 這幾條（繩路徑）不畫。</summary>
    public sealed class SCP_SculptRopeSelection
    {
        public bool None;
        public HashSet<string> Hide = new HashSet<string>(StringComparer.Ordinal);

        public static SCP_SculptRopeSelection Parse(string iRopes, string iHide)
        {
            var aSel = new SCP_SculptRopeSelection { None = iRopes.Trim() == "0" };
            foreach (string p in iHide.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)) aSel.Hide.Add(p.Trim().Trim('/'));
            return aSel;
        }
    }

    /// <summary>
    /// 一個點（繩的端點、帆的角）解到組裝件座標 —— TASK-0507 把原本繩與帆各寫一份的解法合成一支：
    /// `x,y,z`（照所在那一層換座標）｜固定點路徑（`名字`、`關聯/…/名字`）｜`sail:…`（帆角，由帆的觀測注入）｜`model:…`（模型上的點，由模型的觀測給）。
    /// <para>回 null ＝ 解得到：<c>oSailKey</c> 非空 ＝ 帆角（全路徑＋`#`＋角序號）；<c>oHiddenWhy</c> 非空 ＝ 綁的那一層／那面帆／那個模型這次沒組進來
    /// （前面帶一個空白，接在「from／to／角 k」後面讀）。</para>
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
            Populate(aFile, aOut);
            if (aOut.schema != 1) throw new InvalidOperationException("ropes.json 讀不了（" + aFile + "：schema=" + aOut.schema + "）");
            var aNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var r in aOut.ropes)
            {
                string? aBad = ValidName(r.name, "繩") ?? (aNames.Add(r.name) ? null : "名字重複")
                               ?? CheckEndpoint(r.from, "from") ?? CheckEndpoint(r.to, "to")
                               ?? (TryParseColor(r.color, out _, out _, out _, out string aNorm) is string aColorBad ? aColorBad : aNorm != r.color ? "color 不是正規化的寫法（`" + r.color + "`）" : null)
                               ?? CheckThickness(r.thickness) ?? CheckSlack(r.slack, OnSail(r));
                if (aBad != null) throw new InvalidOperationException("ropes.json 讀不了（" + aFile + "：繩 `" + r.name + "` " + aBad + "）");
            }
            return aOut;
        }

        static void Populate(string iFile, object ioTarget)
        {
            try
            {
                var aOpt = new SCP_JsonMapOptions();
                SCP_JsonMapper.Populate(ioTarget, SCP_JsonParser.Parse(File.ReadAllText(iFile, new UTF8Encoding(false)), false), aOpt);
                if (aOpt.Diagnostics.Count > 0) throw new InvalidOperationException(string.Join("; ", aOpt.Diagnostics));
            }
            catch (Exception e) { throw new InvalidOperationException(Path.GetFileName(iFile) + " 讀不了（" + iFile + "：" + e.Message + "）—— ⛔ 不當成沒有，修好再用"); }
        }

        public static void SaveAnchors(SCP_SculptPaths iPaths, SCP_SculptAnchorFile iFile) { iFile.schema = 1; WriteAtomic(iPaths.AnchorsFile, SCP_JsonMapper.ToJson(iFile)); }
        public static void SaveRopes(SCP_SculptPaths iPaths, SCP_SculptRopeFile iFile) { iFile.schema = 1; WriteAtomic(iPaths.RopesFile, SCP_JsonMapper.ToJson(iFile)); }

        static void WriteAtomic(string iFile, SCP_JsonData iJson)
        {
            string aTmp = iFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(iJson, true) + "\n", new UTF8Encoding(false));
                if (File.Exists(iFile)) File.Replace(aTmp, iFile, null);
                else File.Move(aTmp, iFile);
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

        static string? CheckEndpoint(string iText, string iWhich) => TryParseEndpoint(iText, out _, out _, out string aBad) ? null : iWhich + "：" + aBad;

        /// <summary>
        /// 端點：`x,y,z`（三個數，可帶小數；整數 ⇒ 格心）⇒ <paramref name="oPoint"/>；否則是固定點路徑（`名字` 或 `關聯/…/名字`）⇒ <paramref name="oAnchorPath"/>。
        /// 帆角 `sail:…`（TASK-0503）⇒ 回 true、<paramref name="oAnchorPath"/> 帶著 `sail:` 前綴原樣（解法見 <see cref="SCP_SculptSailEndResolver"/>）；其他 `kind:` ⇒ 不認得、回 false（⛔ 不猜）。
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
            if (a.IndexOf(':') >= 0) { oBad = "端點 `" + a + "`：`種類:` 形狀的端點只認 `" + SailPrefix + "`（帆角）與 `" + SCP_SculptModels.Prefix + "`（模型上的點）；其他本版不認得"; return false; }
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

        /// <summary>
        /// 一次觀測的所有繩：組裝件自己的＋關聯樹上每一層的（照那一層換座標）。<paramref name="iTree"/> null ＝ 沒有關聯。
        /// 每條都有一筆讀數（ok／hidden／disabled／broken）；ok 的帶 <see cref="SCP_SculptRopeReading.Geom"/>。
        /// <paramref name="iSails"/>：帆角端點的解法（TASK-0503，帆的觀測注入）；null ⇒ 綁帆角的繩一律 broken 並說原因。
        /// 繩子所在那一層這次沒畫 ⇒ hidden（原因寫哪一層）；壞掉的綁定 ⇒ broken —— ⛔ 兩種都照列、不靜默消失。
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
            var aFrames = new List<string> { "" };
            var aPaths = new List<string>();
            SCP_SculptLinks.Paths(iTree ?? new List<SCP_SculptLinkNode>(), aPaths);
            aFrames.AddRange(aPaths);
            foreach (string aFrame in aFrames)
            {
                string aWork = aFrame.Length == 0 ? iRootId : aIndex[aFrame].Card.id;
                if (!aRopeCache.TryGetValue(aWork, out SCP_SculptRopeFile? aFile)) { aFile = ReadRopes(iWorks.SpacePaths(aWork)); aRopeCache[aWork] = aFile; }
                bool aFrameDrawn = PathDrawn(aFrame, aIndex, iLinkSel);
                foreach (SCP_SculptRope r in aFile.ropes)
                {
                    var aR = new SCP_SculptRopeReading { Path = aFrame.Length == 0 ? r.name : aFrame + "/" + r.name, Owner = aWork, Rope = r };
                    var aWhy = new List<string>();
                    bool aHidden = !aFrameDrawn;
                    if (aHidden) aWhy.Add("繩子所在的 `" + aFrame + "` 這次沒畫");
                    double[]? aEnds0 = null, aEnds1 = null;
                    for (int e = 0; e < 2; e++)
                    {
                        string aText = e == 0 ? r.from : r.to;
                        string? aBad = aPoints.Resolve(aFrame, aText, true, out double[]? aPt, out string aSailKey, out string aHiddenWhy);
                        if (aBad != null) { aR.State = SCP_SculptRopeReading.Broken; aWhy.Add((e == 0 ? "from：" : "to：") + aBad); continue; }
                        if (aSailKey.Length > 0) { if (e == 0) aR.FromSail = aSailKey; else aR.ToSail = aSailKey; }
                        if (aHiddenWhy.Length > 0) { aHidden = true; aWhy.Add((e == 0 ? "from" : "to") + aHiddenWhy); }
                        if (e == 0) aEnds0 = aPt; else aEnds1 = aPt;
                    }
                    aR.From = aEnds0; aR.To = aEnds1;
                    aR.LinkHidden = aHidden;
                    if (aR.State != SCP_SculptRopeReading.Broken)
                    {
                        // 繩子所在那件作品這次沒畫 ⇒ 照列、標 hidden（⛔ 不略過：hide_ropes 點名它、頁面的開關都要找得到它）
                        if (!r.enabled) { aR.State = SCP_SculptRopeReading.Disabled; aWhy.Add("這條繩關著（enabled=0）"); }
                        else if (iRopeSel.None) { aR.State = SCP_SculptRopeReading.Hidden; aWhy.Add("這次不畫繩（ropes=0）"); }
                        else if (iRopeSel.Hide.Contains(aR.Path)) { aR.State = SCP_SculptRopeReading.Hidden; aWhy.Add("這次拿掉（hide_ropes）"); }
                        else if (aHidden) aR.State = SCP_SculptRopeReading.Hidden;
                    }
                    if (aR.From != null && aR.To != null)
                    {
                        double[] aPts = Solve(aR.From, aR.To, r.slack, out double aLen, out double aSag, out bool aVert, out bool aClamped);
                        double dx = aR.To[0] - aR.From[0], dy = aR.To[1] - aR.From[1], dz = aR.To[2] - aR.From[2];
                        aR.Chord = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        aR.Length = aLen; aR.MaxSag = aSag; aR.Vertical = aVert;
                        if (aVert) aWhy.Add("兩端在同一條鉛直線上 ⇒ 畫直線（鬆度沒有往旁邊垂的方向）");
                        if (aClamped) aWhy.Add("鬆到幾乎對折 ⇒ 照上限的弧度畫（slack 調小）");
                        if (aR.State == SCP_SculptRopeReading.Ok)
                        {
                            TryParseColor(r.color, out byte cr, out byte cg, out byte cb, out _);
                            aR.Geom = new SCP_SculptRopeGeom { Path = aR.Path, Points = aPts, Radius = r.thickness / 2, R = cr, G = cg, B = cb };
                        }
                    }
                    aR.Reason = string.Join("；", aWhy);
                    aOut.Add(aR);
                }
            }
            return aOut;
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
        /// 圈的方向：切線 × 參考軸（切線接近鉛直時改用 x 軸），⇒ 決定性。兩端不封口（端點通常埋在固定點的格子裡）。
        /// <paramref name="oQuads"/>：每個四邊形 4 個頂點索引，照「從外面看逆時針（世界右手系）」的順序。
        /// </summary>
        public static void Tube(SCP_SculptRopeGeom iRope, int iSides, out List<double[]> oPos, out List<double[]> oNormal, out List<int[]> oQuads)
        {
            oPos = new List<double[]>(); oNormal = new List<double[]>(); oQuads = new List<int[]>();
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
