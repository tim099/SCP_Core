// 區塊職責：**顯示參數的單一來源** —— 元件尺寸、間距、字級、顏色、文字模式的排版寬度。
// 物理意義：中間層有了之後，「畫面長什麼樣」散在兩個 renderer 裡（ImGui 的 Vector4(0.65f…)、
//           文字 renderer 的 DefaultWidth=96、SenateWindow 的 FontSize=18f）——
//           那些數字各自都對，但**沒有任何一處知道另一處**，於是調一次尺寸要改三個檔，
//           而漏掉的那一個不會報錯，只會「有一半變大了」。
//           ⇒ 本類別把它們收成一份資料：呼叫端問 style 拿數字，renderer 只負責畫。
//           概念取自 Unity 端的 UCL_GUIStyle（全域 Scale ＋ GetScaledSize ＋ Small/Medium/Big/XL 四段），
//           但**刻意不照抄 GUIStyle 那層** —— 這裡沒有任何 UI 函式庫的型別，
//           所以同一份設定 ImGui、純文字、（未來的）HTML renderer 都吃得下。
// 數值影響：純資料，零 IO、零繪圖依賴。基準值（Base*）＝「scale 1.0 的樣子」，
//           實際值＝基準 × <see cref="Scale"/>。
//           ⭐ 預設 scale 的定案過程本身是判準：先照「太小到不想讀」的回報改成 2.0，
//           Tim 在真的視窗裡把四段都按過一輪之後定回 **1.0**（「小的剛好」）。
//           🩸 值得記的是那個順序 —— **改預設值的依據是實機上按過，不是任何一方的推測**
//           （我第一版的 2.0 也是推的，只是推對了方向、推錯了幅度）。
// ⚠ 文字模式的排版參數（<see cref="TextWidth"/> 等）**不吃 Scale** ——
//   終端機的一格是字元不是像素，把它乘 2 只會讓表格超出視窗。
//   （這正是「通則套在前提不成立的那群人身上會安靜地毀掉東西」的形狀，所以分開放。）
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）—— 不用 record、不用檔案級 namespace。
#nullable enable
using System;
using System.Globalization;
using SCP.Core.Json;

namespace SCP.Core.Gui
{
    /// <summary>使用者可選的四段尺寸（對應 UCL_GUIStyle 的 Small / Medium / Big / XL）。</summary>
    public enum SCP_GuiSize
    {
        Small,
        Medium,
        Big,
        XL,
    }

    /// <summary>
    /// 顯示參數的統合體。用法：**建一份、傳下去**（renderer 與頁面都只讀）。
    /// <code>
    /// var aStyle = new SCP_GuiStyle();                 // 預設 scale 2.0
    /// aStyle.SetPreset(SCP_GuiSize.Medium);            // 或使用者選的那一段
    /// Console.Write(SCP_GuiTextRenderer.Render(aUi.Root, aStyle));
    /// </code>
    /// <para>持久化由呼叫端做（<see cref="ToJson"/> / <see cref="FromJson"/>）——
    /// 本層不碰檔案，跟 <see cref="SCP_GuiState"/> 同一個規矩。</para>
    /// </summary>
    public sealed class SCP_GuiStyle
    {
        // ── 縮放 ──────────────────────────────────────────────────
        public const float MinScale = 0.5f;
        public const float MaxScale = 4f;

        /// <summary>
        /// 預設縮放。**1.0 是實測定案的值**（Tim 在視窗裡按過四段之後選的），
        /// 不是「還沒調過的出廠值」—— 想改它請先在真的視窗裡看過，見檔頭。
        /// </summary>
        public const float DefaultScale = 1f;

        float m_Scale = DefaultScale;

        /// <summary>全域縮放。改值走 <see cref="SetScale"/>（會 clamp）。</summary>
        public float Scale { get { return m_Scale; } }

        /// <summary>設定縮放，clamp 到 [<see cref="MinScale"/>, <see cref="MaxScale"/>]。回傳實際生效的值。</summary>
        public float SetScale(float iScale)
        {
            if (float.IsNaN(iScale)) return m_Scale;   // NaN 進來會讓後面每個尺寸都變 NaN，而版位不會報錯
            m_Scale = Math.Max(MinScale, Math.Min(MaxScale, iScale));
            return m_Scale;
        }

        /// <summary>四段預設值。</summary>
        public static float ScaleOf(SCP_GuiSize iSize)
        {
            switch (iSize)
            {
                case SCP_GuiSize.Small: return 1f;
                case SCP_GuiSize.Medium: return 1.5f;
                case SCP_GuiSize.Big: return 2f;
                case SCP_GuiSize.XL: return 2.5f;
                default: return DefaultScale;
            }
        }

        public void SetPreset(SCP_GuiSize iSize) { SetScale(ScaleOf(iSize)); }

        /// <summary>當前 scale 對應哪一段預設；不是整段值時回 null（⇒ 顯示成「自訂」，不要硬塞成最近的一段）。</summary>
        public SCP_GuiSize? Preset
        {
            get
            {
                foreach (SCP_GuiSize s in AllSizes)
                    if (Math.Abs(ScaleOf(s) - m_Scale) < 0.001f) return s;
                return null;
            }
        }

        public static readonly SCP_GuiSize[] AllSizes =
        {
            SCP_GuiSize.Small, SCP_GuiSize.Medium, SCP_GuiSize.Big, SCP_GuiSize.XL,
        };

        public static string NameOf(SCP_GuiSize iSize)
        {
            switch (iSize)
            {
                case SCP_GuiSize.Small: return "小";
                case SCP_GuiSize.Medium: return "中";
                case SCP_GuiSize.Big: return "大";
                case SCP_GuiSize.XL: return "特大";
                default: return iSize.ToString();
            }
        }

        // ── 基準值（scale 1.0 的樣子；刻意對齊 ImGui 出廠值，讓 scale=1 ≈ 未套用本類別前的畫面）──
        public const float BaseFontSize = 18f;
        public const float BaseItemSpacingX = 8f;
        public const float BaseItemSpacingY = 4f;
        public const float BaseFramePaddingX = 4f;
        public const float BaseFramePaddingY = 3f;
        public const float BaseCellPaddingX = 4f;
        public const float BaseCellPaddingY = 2f;
        public const float BaseWindowPaddingX = 8f;
        public const float BaseWindowPaddingY = 8f;
        public const float BaseIndentSpacing = 21f;
        public const float BaseScrollbarSize = 14f;
        public const float BaseGrabMinSize = 12f;
        public const float BaseButtonMinWidth = 88f;
        public const float BaseTextFieldWidth = 220f;

        /// <summary>
        /// 標籤欄寬 —— 欄位名稱畫在**左邊**時的對齊位置。
        /// 標籤比它長時就不對齊（寧可推開，也不要把名字裁掉：裁掉的字不會報錯）。
        /// </summary>
        public const float BaseLabelWidth = 150f;
        public const float BaseWindowRounding = 4f;
        public const float BaseFrameRounding = 3f;
        public const float BaseWindowWidth = 1280f;
        public const float BaseWindowHeight = 800f;

        /// <summary>標題字級相對本文的倍率（角色化字級 —— renderer 若只有一顆字型就退回本文字級）。</summary>
        public float TitleFontMul { get; set; } = 1.15f;

        // ── 縮放後的實際值 ────────────────────────────────────────
        /// <summary>把任何寫死的尺寸乘上當前 <see cref="Scale"/>。等同 UCL_GUIStyle.GetScaledSize。</summary>
        public float Scaled(float iBase) { return iBase * m_Scale; }

        /// <summary>同 <see cref="Scaled"/>，四捨五入成整數（字級／像素寬用）。</summary>
        public int ScaledInt(float iBase) { return (int)Math.Round(iBase * m_Scale, MidpointRounding.AwayFromZero); }

        public float FontSize { get { return Scaled(BaseFontSize); } }
        public float TitleFontSize { get { return Scaled(BaseFontSize) * TitleFontMul; } }
        public float ItemSpacingX { get { return Scaled(BaseItemSpacingX); } }
        public float ItemSpacingY { get { return Scaled(BaseItemSpacingY); } }
        public float FramePaddingX { get { return Scaled(BaseFramePaddingX); } }
        public float FramePaddingY { get { return Scaled(BaseFramePaddingY); } }
        public float CellPaddingX { get { return Scaled(BaseCellPaddingX); } }
        public float CellPaddingY { get { return Scaled(BaseCellPaddingY); } }
        public float WindowPaddingX { get { return Scaled(BaseWindowPaddingX); } }
        public float WindowPaddingY { get { return Scaled(BaseWindowPaddingY); } }
        public float IndentSpacing { get { return Scaled(BaseIndentSpacing); } }
        public float ScrollbarSize { get { return Scaled(BaseScrollbarSize); } }
        public float GrabMinSize { get { return Scaled(BaseGrabMinSize); } }
        public float ButtonMinWidth { get { return Scaled(BaseButtonMinWidth); } }
        public float TextFieldWidth { get { return Scaled(BaseTextFieldWidth); } }
        public float LabelWidth { get { return Scaled(BaseLabelWidth); } }
        public float WindowRounding { get { return Scaled(BaseWindowRounding); } }
        public float FrameRounding { get { return Scaled(BaseFrameRounding); } }

        /// <summary>視窗預設寬（呼叫端要自己夾在螢幕範圍內 —— 比桌面還大的視窗開起來就是壞的）。</summary>
        public int WindowWidth { get { return ScaledInt(BaseWindowWidth); } }
        public int WindowHeight { get { return ScaledInt(BaseWindowHeight); } }

        // ── 顏色 ──────────────────────────────────────────────────
        /// <summary>附註／警語的字色（文字 renderer 用「· 」前綴表達同一件事）。</summary>
        public SCP_Color NoteColor { get; set; } = new SCP_Color(0.65f, 0.65f, 0.68f);

        /// <summary>視窗底色（ImGui 之外的清空色 —— 那一格原本寫死在 SenateWindow 裡）。</summary>
        public SCP_Color BackgroundColor { get; set; } = new SCP_Color(0.09f, 0.09f, 0.11f);

        // ── 文字模式（⚠ 不吃 Scale：終端機的一格是字元不是像素）──
        /// <summary>純文字輸出的總寬（字元格）。</summary>
        public int TextWidth { get; set; } = 96;

        /// <summary>Box 內縮幾個空白。</summary>
        public int TextIndent { get; set; } = 2;

        /// <summary>表格欄與欄之間的空白數。</summary>
        public int TextColumnGap { get; set; } = 2;

        /// <summary>Row 裡 inline 元件之間的空白數。</summary>
        public int TextInlineGap { get; set; } = 3;

        // ── 讀數 ──────────────────────────────────────────────────
        /// <summary>
        /// 一行人可讀的當前設定。⭐ 存在的理由：尺寸這種東西「看起來變大了」不算讀數 ——
        /// 要能在 doctor 輸出／截圖旁邊看到**它以為自己是多大**，才對得起來。
        /// </summary>
        public string Describe()
        {
            SCP_GuiSize? aPreset = Preset;
            string aName = aPreset.HasValue ? NameOf(aPreset.Value) : "自訂";
            return string.Format(CultureInfo.InvariantCulture,
                "scale={0:0.##}（{1}）／字級 {2}（標題 {3}）／間距 {4}×{5}／表格 padding {6}×{7}／文字寬 {8} 格（不吃 scale）",
                m_Scale, aName, ScaledInt(BaseFontSize), (int)Math.Round(TitleFontSize),
                ScaledInt(BaseItemSpacingX), ScaledInt(BaseItemSpacingY),
                ScaledInt(BaseCellPaddingX), ScaledInt(BaseCellPaddingY), TextWidth);
        }

        // ── 尺寸選擇器（對應 UCL_GUIStyle.SetSizeOnGUI）────────────
        /// <summary>
        /// 畫一排尺寸按鈕，回傳這一輪被按下的那一段（沒人按 ⇒ null）。
        /// <para>⚠ 本方法**不改自己也不寫檔** —— 套用與持久化由呼叫端做。
        /// 理由：這一層沒有 IO，而「按了就生效但沒存下來」跟「存了但這一輪沒生效」是兩種 bug，
        /// 讓呼叫端顯式做完兩件事，比在這裡偷偷做一半安全。</para>
        /// </summary>
        public SCP_GuiSize? DrawPicker(SCP_Ui iUi, string iKeyPrefix = "style")
        {
            SCP_GuiSize? aClicked = null;
            SCP_GuiSize? aCur = Preset;
            using (iUi.Row())
            {
                iUi.Label("介面尺寸：");
                foreach (SCP_GuiSize s in AllSizes)
                {
                    bool aIsCur = aCur.HasValue && aCur.Value == s;
                    string aLabel = string.Format(CultureInfo.InvariantCulture,
                        "{0}{1}（{2:0.##}×）", aIsCur ? "● " : "", NameOf(s), ScaleOf(s));
                    if (iUi.Button(aLabel, iKeyPrefix + "/" + s.ToString().ToLowerInvariant())) aClicked = s;
                }
            }
            return aClicked;
        }

        // ── 持久化（資料進出，檔案不在這一層）────────────────────
        public SCP_JsonData ToJson()
        {
            SCP_JsonData aRoot = SCP_JsonData.NewObject();
            aRoot.Set("scale", (double)m_Scale);
            aRoot.Set("titleFontMul", (double)TitleFontMul);
            aRoot.Set("textWidth", TextWidth);
            return aRoot;
        }

        /// <summary>
        /// 從 JSON 復原。**缺欄位 ⇒ 用預設**（那是「沒設過」，不是 0）；
        /// 傳 null ⇒ 全預設（檔案不存在時由呼叫端這樣叫）。
        /// </summary>
        public static SCP_GuiStyle FromJson(SCP_JsonData? iData)
        {
            var aStyle = new SCP_GuiStyle();
            if (iData == null || !iData.Exists) return aStyle;

            if (iData["scale"].Exists) aStyle.SetScale((float)iData["scale"].AsDouble());
            if (iData["titleFontMul"].Exists) aStyle.TitleFontMul = (float)iData["titleFontMul"].AsDouble();
            if (iData["textWidth"].Exists) aStyle.TextWidth = Math.Max(40, iData["textWidth"].AsInt());
            return aStyle;
        }

        /// <summary>複製一份（renderer 想改自己那份時不要動到別人的）。</summary>
        public SCP_GuiStyle Clone()
        {
            var aCopy = new SCP_GuiStyle();
            aCopy.m_Scale = m_Scale;
            aCopy.TitleFontMul = TitleFontMul;
            aCopy.TextWidth = TextWidth;
            aCopy.TextIndent = TextIndent;
            aCopy.TextColumnGap = TextColumnGap;
            aCopy.TextInlineGap = TextInlineGap;
            aCopy.NoteColor = NoteColor;
            aCopy.BackgroundColor = BackgroundColor;
            return aCopy;
        }
    }
}
