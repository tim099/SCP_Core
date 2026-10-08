// 區塊職責：中間層樹 → **純文字排版**（第一個 renderer）。
// 物理意義：這個 renderer 存在的理由不是「沒有視窗時的降級」，是**驗收手段**：
//           UI 有了文字輸出，就能 diff、能快照測試、能貼進聊天室給人看、能在 CI 跑。
//           ⇒ 「介面看起來對」變成「介面的讀數對」。
// 數值影響：純函式（樹進、字串出），零 IO、零全域狀態。
// ⚠ 寬度計算必須認得**全角字**：中文一個字佔兩格，用 string.Length 對齊表格會歪，
//   而歪掉的表格不會報錯，只會讓人不想讀 —— 不想讀就等於這些字沒寫。

#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
namespace SCP.Core.Gui
{
    public static class SCP_GuiTextRenderer
    {
        public const int DefaultWidth = 96;

        /// <summary>寬度直接給的版本（其餘排版參數用 <see cref="SCP_GuiStyle"/> 的預設）。</summary>
        public static string Render(SCP_GuiNode iRoot, int iWidth = DefaultWidth)
        {
            var aStyle = new SCP_GuiStyle();
            aStyle.TextWidth = iWidth;
            return Render(iRoot, aStyle);
        }

        /// <summary>
        /// 吃 <see cref="SCP_GuiStyle"/> 的版本 —— 寬度／縮排／欄距全從那一份統一設定來。
        /// <para>⚠ 這裡只讀 style 的 <c>Text*</c> 欄位，**刻意不讀 Scale**：
        /// 終端機的一格是字元不是像素，把它乘 2 只會讓表格超出視窗。</para>
        /// </summary>
        public static string Render(SCP_GuiNode iRoot, SCP_GuiStyle iStyle)
        {
            var sb = new StringBuilder();
            foreach (var child in iRoot.Children) RenderNode(child, sb, 0, iStyle);
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        static void RenderNode(SCP_GuiNode iNode, StringBuilder oSb, int iIndent, SCP_GuiStyle iStyle)
        {
            int iWidth = iStyle.TextWidth;
            string pad = new string(' ', iIndent);
            int inner = Math.Max(20, iWidth - iIndent);

            switch (iNode.Kind)
            {
                case SCP_GuiNodeKind.Title:
                    oSb.Append(pad).Append("── ").Append(iNode.Text).Append(' ')
                       .Append(new string('─', Math.Max(0, inner - Width(iNode.Text) - 4))).Append('\n');
                    break;

                case SCP_GuiNodeKind.Label:
                    oSb.Append(pad).Append(iNode.Text).Append('\n');
                    break;

                case SCP_GuiNodeKind.Note:
                    oSb.Append(pad).Append("· ").Append(iNode.Text).Append('\n');
                    break;

                case SCP_GuiNodeKind.Separator:
                    oSb.Append(pad).Append(new string('─', inner)).Append('\n');
                    break;

                case SCP_GuiNodeKind.Space:
                    oSb.Append('\n');
                    break;

                case SCP_GuiNodeKind.Button:
                case SCP_GuiNodeKind.Image:
                    oSb.Append(pad).Append(Inline(iNode)).Append('\n');
                    break;

                case SCP_GuiNodeKind.TextField when iNode.Lines > 0 && !iNode.Masked:
                {
                    // 多行（SCP_Ui.TextArea）：逐行加 `│` 前綴印全文 —— 擠進一個 ⟨…⟩ 的話換行會把版面打散，
                    // 而且「最後一行是空的」跟「沒有最後一行」看不出差別。
                    string[] aLines = iNode.Value.Split('\n');
                    oSb.Append(pad).Append(iNode.Text.Length > 0 ? iNode.Text : "編輯區").Append(": （多行，").Append(aLines.Length).Append(" 行）\n");
                    foreach (string l in aLines) oSb.Append(pad).Append("  │ ").Append(l.TrimEnd('\r')).Append('\n');
                    break;
                }

                case SCP_GuiNodeKind.Toggle:
                case SCP_GuiNodeKind.TextField:
                case SCP_GuiNodeKind.Slider:
                    oSb.Append(pad).Append(Inline(iNode)).Append('\n');
                    break;

                case SCP_GuiNodeKind.Row:
                {
                    // 一列 = **連續的 inline 子節點**串成一行；遇到群組（Box／Table／巢狀 Row）就換行。
                    // ⚠ 舊版是「全部 inline 才併，否則整列逐項換行」——
                    //   那讓「一顆鈕 ＋ 一個展開的下拉」連鈕都各自佔一行。
                    //   分類走 SCP_GuiNode.IsInline（跟 ImGui renderer 同一份，不各判一次）。
                    var aRun = new List<SCP_GuiNode>();
                    bool aEmitted = false;
                    foreach (var c in iNode.Children)
                    {
                        if (SCP_GuiNode.IsInline(c.Kind)) { aRun.Add(c); continue; }
                        if (FlushInlineRun(aRun, oSb, pad, iStyle)) aEmitted = true;
                        // ⚠ 這一行是**誠實的註記不是模擬**：文字模式沒有水平版位，只能把群組換行印；
                        //    但視窗那側（ImGui 的 BeginGroup）會把它排在前一項的右邊。
                        //    不講的話，讀文字輸出的人會以為版面真的是上下排的 ——
                        //    而我就是這樣漏掉了那次重疊（文字看起來正常，視窗疊成一團）。
                        if (aEmitted) oSb.Append(pad).Append("· ⟨視窗模式：下面這塊排在上一行的右邊⟩").Append('\n');
                        RenderNode(c, oSb, iIndent, iStyle);
                        aEmitted = true;
                    }
                    FlushInlineRun(aRun, oSb, pad, iStyle);
                    break;
                }

                case SCP_GuiNodeKind.Column:
                    foreach (var c in iNode.Children) RenderNode(c, oSb, iIndent, iStyle);
                    break;

                case SCP_GuiNodeKind.Box:
                {
                    // 可摺疊的框把狀態畫在標題上 —— 文字模式沒有滑鼠，
                    // 沒有 ▼／▶ 的話「收起來了」與「裡面是空的」長得一模一樣
                    string aMark = iNode.Collapsible ? (iNode.Open ? "▼ " : "▶ ") : "";
                    string aTitle = string.IsNullOrEmpty(iNode.Text) && aMark.Length == 0
                        ? "" : $" {aMark}{iNode.Text} ";
                    oSb.Append(pad).Append('┌').Append(aTitle)
                       .Append(new string('─', Math.Max(0, inner - Width(aTitle) - 2))).Append('┐').Append('\n');
                    foreach (var c in iNode.Children) RenderNode(c, oSb, iIndent + Math.Max(0, iStyle.TextIndent), iStyle);
                    oSb.Append(pad).Append('└').Append(new string('─', Math.Max(0, inner - 2))).Append('┘').Append('\n');
                    break;
                }

                case SCP_GuiNodeKind.Table:
                    RenderTable(iNode, oSb, iIndent, iStyle);
                    break;

                case SCP_GuiNodeKind.Plot:
                    oSb.Append(pad).Append(iNode.Text).Append("：")
                       .Append(SCP_GuiSparkline.Render(iNode.Series, Math.Max(8, inner - Width(iNode.Text) - 40)))
                       .Append("　").Append(SCP_GuiSparkline.Describe(iNode.Series)).Append('\n');
                    break;

                default:
                    foreach (var c in iNode.Children) RenderNode(c, oSb, iIndent, iStyle);
                    break;
            }
        }

        /// <summary>把累積到的 inline 子節點吐成一行並清空。回傳有沒有真的吐出東西。</summary>
        static bool FlushInlineRun(List<SCP_GuiNode> ioRun, StringBuilder oSb, string iPad, SCP_GuiStyle iStyle)
        {
            if (ioRun.Count == 0) return false;
            oSb.Append(iPad)
               .Append(string.Join(new string(' ', Math.Max(1, iStyle.TextInlineGap)), ioRun.Select(Inline)))
               .Append('\n');
            ioRun.Clear();
            return true;
        }

        /// <summary>密碼欄的顯示字：只說有沒有填，⛔ 不印內容也不印長度。</summary>
        public static string MaskedText(string iValue) => string.IsNullOrEmpty(iValue) ? "（未輸入）" : "（已輸入，不顯示）";

        /// <summary>有圖嗎：路徑非空；記憶體影像（`mem:`）則要登記處真的有那一張（key 在、圖不在 ＝ 還沒渲染 ＝ 無圖）。</summary>
        static bool HasImage(string iValue)
            => iValue.Length > 0 && (!SCP_GuiImageStore.IsMemory(iValue) || SCP_GuiImageStore.Has(SCP_GuiImageStore.KeyOf(iValue)!));

        static string Inline(SCP_GuiNode iNode) => iNode.Kind switch
        {
            SCP_GuiNodeKind.Button => $"[ {iNode.Text} ]",
            SCP_GuiNodeKind.Toggle => $"[{(iNode.On ? "x" : " ")}] {iNode.Text}",
            SCP_GuiNodeKind.TextField => $"{iNode.Text}: ⟨{(iNode.Masked ? MaskedText(iNode.Value) : iNode.Value)}⟩",
            SCP_GuiNodeKind.Slider => SliderText(iNode),
            SCP_GuiNodeKind.Note => $"· {iNode.Text}",
            SCP_GuiNodeKind.Image => HasImage(iNode.Value) ? $"[圖：{iNode.Text}]" : $"[無圖：{iNode.Text}]",
            _ => iNode.Text,
        };

        /// <summary>
        /// 滑桿：`標籤: 值 (min..max)`。欄位空白 ⇒ 註明「預設」；欄位有字但不是數字 ⇒ **照實說**
        /// （畫面顯示的是預設，頁面讀到的卻是那串字 —— 不講的話兩邊不一致看不出來）。
        /// </summary>
        static string SliderText(SCP_GuiNode iNode)
        {
            string aVal = SCP_Ui.FormatSlider(iNode.SliderValue, iNode.SliderFormat);
            string aRange = SCP_Ui.FormatSlider(iNode.SliderMin, iNode.SliderFormat) + ".." + SCP_Ui.FormatSlider(iNode.SliderMax, iNode.SliderFormat);
            string aTail = "";
            if (string.IsNullOrWhiteSpace(iNode.Value)) aTail = "（未設 ＝ 預設）";
            else if (!SCP_Ui.TryParseSlider(iNode.Value, iNode.SliderMin, iNode.SliderMax, out _)) aTail = $"（⚠ 欄位值「{iNode.Value}」不是數字 ⇒ 顯示預設）";
            return $"{iNode.Text}: {aVal} ({aRange}){aTail}";
        }

        static void RenderTable(SCP_GuiNode iTable, StringBuilder oSb, int iIndent, SCP_GuiStyle iStyle)
        {
            var rows = new List<List<string>>();
            if (iTable.Headers.Count > 0) rows.Add(iTable.Headers.ToList());
            foreach (var r in iTable.Children)
            {
                if (r.Kind != SCP_GuiNodeKind.TableRow) continue;
                rows.Add(r.Children.Select(c => c.Kind == SCP_GuiNodeKind.TableCell ? c.Text : Inline(c)).ToList());
            }
            if (rows.Count == 0) return;

            int cols = rows.Max(r => r.Count);
            var w = new int[cols];
            foreach (var r in rows)
                for (int i = 0; i < r.Count; i++) w[i] = Math.Max(w[i], Width(r[i]));

            string pad = new string(' ', iIndent);
            string aGap = new string(' ', Math.Max(1, iStyle.TextColumnGap));
            for (int ri = 0; ri < rows.Count; ri++)
            {
                var cells = new List<string>();
                for (int ci = 0; ci < cols; ci++)
                {
                    string cell = ci < rows[ri].Count ? rows[ri][ci] : "";
                    cells.Add(cell + new string(' ', Math.Max(0, w[ci] - Width(cell))));
                }
                oSb.Append(pad).Append(string.Join(aGap, cells).TrimEnd()).Append('\n');
                if (ri == 0 && iTable.Headers.Count > 0)
                    oSb.Append(pad)
                       .Append(string.Join(aGap, w.Select(x => new string('─', Math.Max(1, x)))))
                       .Append('\n');
            }
        }

        /// <summary>顯示寬度（全角字算 2 格）。</summary>
        public static int Width(string iText)
        {
            int n = 0;
            foreach (char c in iText) n += IsWide(c) ? 2 : 1;
            return n;
        }

        static bool IsWide(char c) =>
            (c >= 0x1100 && c <= 0x115F) ||    // Hangul Jamo
            (c >= 0x2E80 && c <= 0xA4CF) ||    // CJK radicals … Yi（含中日韓漢字、假名、注音）
            (c >= 0xAC00 && c <= 0xD7A3) ||    // Hangul syllables
            (c >= 0xF900 && c <= 0xFAFF) ||    // CJK compatibility ideographs
            (c >= 0xFE30 && c <= 0xFE6F) ||    // CJK compatibility forms
            (c >= 0xFF00 && c <= 0xFF60) ||    // 全角 ASCII
            (c >= 0xFFE0 && c <= 0xFFE6);
    }
    /// <summary>
    /// 迷你走勢（▁▂▃▄▅▆▇█）。文字 renderer 的 Plot 與 `rate op=history` 的 CLI 輸出**共用這一支**——
    /// 各寫一份的話，兩邊對同一串數字會畫出不同的形狀，而那不會報錯。
    /// </summary>
    public static class SCP_GuiSparkline
    {
        const string Levels = "▁▂▃▄▅▆▇█";

        /// <summary>
        /// 畫成最多 <paramref name="iMaxWidth"/> 格。點數多於格數時**等距取樣**（保留首尾）。
        /// ⚠ 全部相同的值畫成一整排中間高度 —— 畫成一排最低格的話，「沒有變動」會長得像「跌到谷底」。
        /// </summary>
        public static string Render(IReadOnlyList<double> iValues, int iMaxWidth)
        {
            if (iValues == null || iValues.Count == 0) return "（無資料）";
            int n = iValues.Count;
            int w = Math.Max(1, Math.Min(n, iMaxWidth));
            var aPick = new double[w];
            for (int i = 0; i < w; i++)
            {
                int aIdx = w == 1 ? n - 1 : (int)Math.Round((double)i * (n - 1) / (w - 1));
                aPick[i] = iValues[aIdx];
            }
            double aMin = double.MaxValue, aMax = double.MinValue;
            foreach (double v in aPick) { if (v < aMin) aMin = v; if (v > aMax) aMax = v; }
            var sb = new StringBuilder(w);
            foreach (double v in aPick)
            {
                int aLv = aMax > aMin ? (int)Math.Round((v - aMin) / (aMax - aMin) * (Levels.Length - 1)) : Levels.Length / 2;
                sb.Append(Levels[Math.Max(0, Math.Min(Levels.Length - 1, aLv))]);
            }
            return sb.ToString();
        }

        /// <summary>刻度說明（最低／最高／點數）—— 一條線沒有刻度的話，「平」與「量尺太粗」同形。</summary>
        public static string Describe(IReadOnlyList<double> iValues)
        {
            if (iValues == null || iValues.Count == 0) return "";
            double aMin = double.MaxValue, aMax = double.MinValue;
            foreach (double v in iValues) { if (v < aMin) aMin = v; if (v > aMax) aMax = v; }
            return string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "（低 {0:0.########}／高 {1:0.########}，{2} 點）", aMin, aMax, iValues.Count);
        }
    }
}
