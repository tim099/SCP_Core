// 區塊職責：**中間層** —— 一次繪製產生的介面節點樹（display list）。
// 物理意義：撰寫端用 GUILayout 那種 immediate-mode 手感寫頁面，但那些呼叫**不直接畫像素**，
//           而是長出這棵樹；再由 renderer 決定畫成什麼（ImGui 視窗／純文字／未來 HTML）。
//           ⇒ 兩個好處，第二個才是關鍵：
//             ① 換畫布不動頁面碼
//             ② **介面可以在沒有視窗的環境被輸出成文字** ⇒ 於是它可以被 diff、被快照測試、
//                被貼進聊天室給人看。UI 從「只能用眼睛驗」變成「有讀數可以對」。
// 數值影響：純資料容器，零 IO、零繪圖依賴（本組件刻意不參照任何 UI 函式庫）。
#nullable enable
using System;
using System.Collections.Generic;
namespace SCP.Core.Gui
{
    public enum SCP_GuiNodeKind
    {
        Root,
        Column,       // 垂直堆疊
        Row,          // 水平排列
        Box,          // 有框（可帶標題）的群組
        Title,        // 頁面／區塊標題
        Label,        // 一般文字
        Note,         // 附註／警語（renderer 可畫得暗一點或加前綴）
        Separator,
        Space,
        Button,
        Toggle,
        TextField,
        Table,        // 子節點必為 TableRow
        TableRow,     // 子節點必為 TableCell
        TableCell,
    }

    /// <summary>
    /// 中間層節點。**一次繪製建一棵、用完丟**（immediate mode 的語意），
    /// 所以這裡不放任何跨幀狀態 —— 跨幀的東西住在 <see cref="SCP_GuiInput"/> 與呼叫端自己的欄位裡。
    /// </summary>
    public sealed class SCP_GuiNode
    {
        public SCP_GuiNodeKind Kind { get; init; }

        /// <summary>穩定識別鍵（互動節點才有意義）。組法與踩過的坑見 <see cref="SCP_GuiIdScope"/>。</summary>
        public string Id { get; init; } = "";

        /// <summary>顯示文字（Label／Button 的字、Box 的標題、TableCell 的內容）。</summary>
        public string Text { get; init; } = "";

        /// <summary>TextField 的當前值。</summary>
        public string Value { get; init; } = "";

        /// <summary>Toggle 的當前狀態。</summary>
        public bool On { get; init; }

        /// <summary>
        /// 這個 Box 可以摺疊嗎。⚠ 摺疊狀態**不住在這裡**（節點用完就丟）——
        /// 它是跨幀狀態，住在 <see cref="SCP_GuiInput.Folds"/> 與呼叫端的 session 裡。
        /// </summary>
        public bool Collapsible { get; init; }

        /// <summary>展開中嗎（Collapsible 才有意義）。收合時**子節點根本沒有被建出來**。</summary>
        public bool Open { get; init; } = true;

        /// <summary>
        /// 這個群組的**直接子節點要不要等寬**（Box／Column 才有意義）。
        /// <para>是**意圖**不是尺寸 —— 共用層不講像素，由 renderer 自己決定怎麼達成。
        /// 用途：下拉選單的選項一排下來寬度不一時，眼睛沒有一條可以掃的直線。</para>
        /// <para>⚠ 文字 renderer **刻意忽略它**：終端機的一格是字元，等寬只會補出一堆尾隨空白，
        /// 而那會弄髒 diff（而「輸出可以 diff」正是文字 renderer 存在的理由）。</para>
        /// </summary>
        public bool UniformWidth { get; init; }

        /// <summary>Table 的表頭；其他 Kind 不使用。</summary>
        public IReadOnlyList<string> Headers { get; init; } = Array.Empty<string>();

        public List<SCP_GuiNode> Children { get; } = new();

        public SCP_GuiNode Add(SCP_GuiNode iChild) { Children.Add(iChild); return iChild; }

        /// <summary>
        /// 這種節點排得進「一行」嗎（Row 用來決定誰跟誰同排）。
        /// <para>⚠ 這份分類**只能有一份**：兩個 renderer 各判斷一次的話，遲早出現
        /// 「文字模式換行、視窗模式硬排在一起」—— 而硬排的結果是**互相蓋掉**，
        /// 那不會報錯，只會在畫面上疊成一團看不懂的字。
        /// （同 D14「分類只有一份，兩個消費端都吃它」的判準。）</para>
        /// <para>群組類（Row／Column／Box／Table）**不是** inline：它們會長出好幾行，
        /// 而「好幾行的東西」沒有辦法排在別人的右邊。</para>
        /// </summary>
        public static bool IsInline(SCP_GuiNodeKind iKind)
        {
            switch (iKind)
            {
                case SCP_GuiNodeKind.Label:
                case SCP_GuiNodeKind.Note:
                case SCP_GuiNodeKind.Button:
                case SCP_GuiNodeKind.Toggle:
                case SCP_GuiNodeKind.TextField:
                    return true;
                default:
                    return false;
            }
        }
    }

    /// <summary>
    /// 這一輪繪製的輸入：使用者按了哪顆鈕、欄位現在是什麼值。
    ///
    /// <para>物理意義：immediate mode 的「按鈕回傳 true」需要有人告訴它「這一輪誰被按了」。
    /// 真 UI 時由 renderer 填；**文字模式時由呼叫端／測試填** ——
    /// 於是「按下重新載入」在沒有視窗的環境也是一個可執行、可驗收的動作。</para>
    /// </summary>
    public sealed class SCP_GuiInput
    {
        /// <summary>這一輪被按下的按鈕 id（null ＝ 沒人按，只是重畫）。</summary>
        public string? ClickedId { get; set; }

        /// <summary>欄位覆寫值：id → 使用者輸入的字。沒有的欄位沿用呼叫端傳進來的值。</summary>
        public Dictionary<string, string> Fields { get; } = new();

        /// <summary>勾選覆寫：id → 狀態。</summary>
        public Dictionary<string, bool> Toggles { get; } = new();

        /// <summary>
        /// 摺疊狀態：Box 的 id → 展開中嗎。沒有的沿用呼叫端給的預設。
        /// <para>⚠ 刻意跟 <see cref="Toggles"/> 分開：摺疊是**看畫面的人的偏好**，
        /// 勾選是**資料**。混在一起的話「我把區塊收起來」會被存成一筆資料修改，
        /// 而那會出現在 diff 裡（然後沒有人知道那是誰改的）。</para>
        /// </summary>
        public Dictionary<string, bool> Folds { get; } = new();

        public static SCP_GuiInput None => new();
    }
}
