// 區塊職責：撰寫端 API —— 一頁一個方法、從上往下寫、按鈕的回傳值就是事件（GUILayout 手感）。
// 物理意義：每個呼叫**只做一件事：往中間層樹上掛一個節點**，並（對互動節點）回報這一輪的輸入。
//           繪圖／排版一行都不在這裡 —— 那是 renderer 的事。
// 數值影響：零 IO。同樣的頁面碼＋同樣的 SCP_GuiInput ⇒ 同樣的樹（可快照、可 diff）。

#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
namespace SCP.Core.Gui
{
    /// <summary>
    /// 一次繪製的上下文。用法：
    /// <code>
    /// var ui = new SCP_Ui(input);
    /// page.Draw(ui);
    /// string text = SCP_GuiTextRenderer.Render(ui.Root);
    /// </code>
    /// </summary>
    public sealed class SCP_Ui
    {
        readonly SCP_GuiInput m_Input;
        readonly Stack<SCP_GuiNode> m_Stack = new();
        readonly SCP_GuiIdScope m_Ids = new();

        /// <summary>id 組成過程中「只能靠出現順序」的節點 —— 那是會漂的東西，所以要看得見。</summary>
        public IReadOnlyList<string> Diagnostics => m_Ids.Diagnostics;

        public SCP_GuiNode Root { get; }

        public SCP_Ui(SCP_GuiInput? iInput = null)
        {
            m_Input = iInput ?? SCP_GuiInput.None;
            Root = new SCP_GuiNode { Kind = SCP_GuiNodeKind.Root };
            m_Stack.Push(Root);
        }

        SCP_GuiNode Current => m_Stack.Peek();

        SCP_GuiNode Push(SCP_GuiNode iNode) { Current.Add(iNode); m_Stack.Push(iNode); return iNode; }
        void Pop() { if (m_Stack.Count > 1) m_Stack.Pop(); }

        // ── 純顯示 ────────────────────────────────────────────────
        public void Title(string iText) => Current.Add(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Title, Text = iText });
        public void Label(string iText) => Current.Add(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Label, Text = iText });
        public void Note(string iText) => Current.Add(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Note, Text = iText });
        public void Separator() => Current.Add(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Separator });
        public void Space() => Current.Add(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Space });

        // ── 群組（using scope）─────────────────────────────────────
        public Scope Column() { m_Ids.PushLevel("col"); Push(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Column }); return new Scope(this); }
        public Scope Row() { m_Ids.PushLevel("row"); Push(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Row }); return new Scope(this); }

        public Scope Box(string iTitle = "", string? iKey = null)
        {
            m_Ids.PushLevel(iKey ?? iTitle);
            Push(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Box, Text = iTitle });
            return new Scope(this);
        }

        /// <summary>
        /// **只開 id 命名空間，版面上完全透明**（不畫框、不縮排）。
        /// <para>用途：同一棵樹裡有兩個結構相同的區塊（兩頁、兩個清單項）時，
        /// 沒傳顯式 key 的欄位會撞名 —— 撞了不會報錯，只會共用 session 值，
        /// 於是「另一頁的欄位莫名有值」。⇒ 讓區塊自己帶一層命名空間。</para>
        /// <para>⚠ 顯式 key（<c>Button(label, key)</c>）**不受本 scope 影響**（逐字採用是契約）。</para>
        /// </summary>
        public Scope IdScope(string iKey)
        {
            m_Ids.PushLevel(iKey);
            Push(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Column });
            return new Scope(this);
        }

        // ── 表格 ──────────────────────────────────────────────────
        public Scope Table(params string[] iHeaders)
        {
            m_Ids.PushLevel("table");
            Push(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Table, Headers = iHeaders });
            return new Scope(this);
        }

        /// <summary>表格的一列。cells 少於表頭時補空、多於表頭時 renderer 照樣印（不吞資料）。</summary>
        public void TableRow(params string[] iCells)
        {
            var aRow = new SCP_GuiNode { Kind = SCP_GuiNodeKind.TableRow };
            foreach (string c in iCells) aRow.Add(new SCP_GuiNode { Kind = SCP_GuiNodeKind.TableCell, Text = c });
            Current.Add(aRow);
        }

        // ── 互動 ──────────────────────────────────────────────────
        /// <summary>按鈕。**這一輪被按下就回 true**（GUILayout 語意）。</summary>
        public bool Button(string iLabel, string? iKey = null)
        {
            string aId = iKey != null ? m_Ids.MakeExplicit(iKey) : m_Ids.Make(iLabel);
            Current.Add(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Button, Id = aId, Text = iLabel });
            return m_Input.ClickedId == aId;
        }

        /// <summary>勾選。回傳**這一輪之後**的狀態（沒有輸入覆寫時 ＝ 傳進來的值）。</summary>
        public bool Toggle(string iLabel, bool iValue, string? iKey = null)
        {
            string aId = iKey != null ? m_Ids.MakeExplicit(iKey) : m_Ids.Make(iLabel);
            bool aOn = m_Input.Toggles.TryGetValue(aId, out bool v) ? v : iValue;
            Current.Add(new SCP_GuiNode { Kind = SCP_GuiNodeKind.Toggle, Id = aId, Text = iLabel, On = aOn });
            return aOn;
        }

        /// <summary>單行輸入。回傳這一輪之後的值。</summary>
        public string TextField(string iLabel, string iValue, string? iKey = null)
        {
            string aId = iKey != null ? m_Ids.MakeExplicit(iKey) : m_Ids.Make(iLabel);
            string aVal = m_Input.Fields.TryGetValue(aId, out string? v) ? v : iValue;
            Current.Add(new SCP_GuiNode { Kind = SCP_GuiNodeKind.TextField, Id = aId, Text = iLabel, Value = aVal });
            return aVal;
        }

        /// <summary>群組的 using scope。</summary>
        public readonly struct Scope : IDisposable
        {
            readonly SCP_Ui m_Ui;
            internal Scope(SCP_Ui iUi) { m_Ui = iUi; }
            public void Dispose() { m_Ui.Pop(); m_Ui.m_Ids.PopLevel(); }
        }
    }

    /// <summary>
    /// id 產生器 —— 路徑式（父層鍵 ＋ 本節點鍵）。
    ///
    /// <para>🩸 為什麼不用「呼叫順序」當 id：清單增刪一筆，後面每一個 id 都位移，
    /// 於是滾動位置／勾選狀態／focus 全部跑到別人身上，**而且不報錯**。
    /// ⇒ 一律用資料本身的鍵（`iKey`）；退回用文字當鍵時同名會撞，撞了就補序號
    /// **並且把這件事記進 Diagnostics** —— 會漂的東西必須看得見，不可以安靜地能跑。</para>
    /// </summary>
    sealed class SCP_GuiIdScope
    {
        readonly List<string> m_Levels = new();
        readonly Dictionary<string, int> m_Used = new();
        readonly List<string> m_Diagnostics = new();

        public IReadOnlyList<string> Diagnostics => m_Diagnostics;

        public void PushLevel(string iKey) => m_Levels.Add(Slug(iKey));
        public void PopLevel() { if (m_Levels.Count > 0) m_Levels.RemoveAt(m_Levels.Count - 1); }

        /// <summary>
        /// 顯式 key —— **逐字採用**：不加 layout 前綴、不做 slug。
        /// 🩸 第一版把顯式 key 也丟進路徑推導，於是 `Button("重新取讀數", "doctor/refresh")`
        ///   放在 `using (g.Row())` 裡就變成 `row/doctor-refresh`
        ///   ⇒ 把按鈕搬進／搬出一個 Row 就會換 id，而那正是傳顯式 key 要防的漂移。
        ///   顯式 key 是**契約**：呼叫端寫什麼，操作端（agent／腳本）就用什麼。
        /// </summary>
        public string MakeExplicit(string iKey) => Unique(iKey);

        /// <summary>沒給 key ⇒ 用「layout 路徑 ＋ 文字」推導。⚠ 這種 id 會隨版面改動而漂。</summary>
        public string Make(string iKey)
        {
            var sb = new StringBuilder();
            foreach (string lv in m_Levels) { if (lv.Length == 0) continue; sb.Append(lv).Append('/'); }
            sb.Append(Slug(iKey));
            string aBase = sb.ToString();
            return Unique(aBase);
        }

        string Unique(string aBase)
        {
            if (!m_Used.TryGetValue(aBase, out int n))
            {
                m_Used[aBase] = 1;
                return aBase;
            }
            m_Used[aBase] = n + 1;
            string aId = $"{aBase}#{n + 1}";
            m_Diagnostics.Add($"id 撞名，退回序號：{aId} —— 這個 id 會隨清單增刪而漂，請傳唯一的顯式 key");
            return aId;
        }

        static string Slug(string iText)
        {
            if (string.IsNullOrEmpty(iText)) return "";
            var sb = new StringBuilder(iText.Length);
            foreach (char c in iText)
            {
                if (char.IsWhiteSpace(c) || c == '/') sb.Append('-');
                else sb.Append(char.ToLowerInvariant(c));
            }
            return sb.ToString();
        }
    }
}
