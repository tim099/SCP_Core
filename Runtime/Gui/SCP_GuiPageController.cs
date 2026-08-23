// 區塊職責：一疊頁面 —— **只畫最上方那一頁**，push／pop 就是導覽。
// 物理意義：概念取自 Unity 端的 UCL_GUIPageController（stack、TopPage、Pause/Resume、PopUntil/PopAll）。
//           ⭐ 但**刻意沒有 `Ins` 單例**：UCL 那份是「一個遊戲一個 controller」的前提，
//           這裡的前提是「**一個 Window 一套 controller**」。
//           把 singleton 留著的症狀不是崩潰，是第二個視窗開起來之後兩邊互相蓋 ——
//           而畫面看起來只是「我按的那頁跑到另一個窗去了」。
//           ⇒ 這是「把『只有一個』縮到它真正只有一個的那一層」的同一條判準。
// 數值影響：純資料結構，零 IO、零繪圖依賴。
// ⚠ 導覽在 retained 畫布上**慢一幀**：頁面的 handler 在這一輪 push 了新頁，
//   但這一輪的樹已經是舊頁畫出來的 —— 下一輪才會看到新頁。
//   （跟 renderer 的「按鈕回傳值慢一幀」同一個成因，不是 bug，但要知道。）
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace SCP.Core.Gui
{
    /// <summary>
    /// 頁面堆疊。用法：
    /// <code>
    /// var aCtrl = new SCP_GuiPageController();
    /// aCtrl.Push(new DoctorPage(aModel));
    /// // 每輪：
    /// var aUi = new SCP_Ui(aInput);
    /// aCtrl.Draw(aUi);                     // 只畫 TopPage（含返回列）
    /// </code>
    /// </summary>
    public sealed class SCP_GuiPageController
    {
        readonly List<SCP_GuiPage> m_Pages = new List<SCP_GuiPage>();

        public IReadOnlyList<SCP_GuiPage> Pages { get { return m_Pages; } }
        public int Count { get { return m_Pages.Count; } }
        public bool IsEmpty { get { return m_Pages.Count == 0; } }

        /// <summary>最上方那頁（＝現在畫得出來的那頁）。空的時候是 null，**不要回一個假的空頁**。</summary>
        public SCP_GuiPage? TopPage { get { return m_Pages.Count == 0 ? null : m_Pages[m_Pages.Count - 1]; } }

        /// <summary>Count > 1 時自動畫一顆返回鈕（id 固定 <c>page/back</c>，agent 也按得到）。</summary>
        public bool ShowBackBar { get; set; } = true;

        /// <summary>返回鈕上方是否畫麵包屑（`A ▸ B ▸ C`）。</summary>
        public bool ShowBreadcrumb { get; set; } = true;

        // ── 導覽 ──────────────────────────────────────────────────
        /// <summary>
        /// 推一頁上去。原本的 TopPage 收到 <see cref="SCP_GuiPage.OnPause"/>。
        /// <para>⛔ **同一個 page 實例不准 push 兩次** —— stack 裡有兩個相同引用時，
        /// `Pop` 移掉的是哪一個、`Remove` 移掉的又是哪一個，會變成看運氣的事，
        /// 而它「能跑」。⇒ 這是程式錯誤，當場丟例外，不安靜地接受。</para>
        /// </summary>
        public void Push(SCP_GuiPage iPage)
        {
            if (iPage == null) throw new ArgumentNullException(nameof(iPage));
            if (m_Pages.Contains(iPage))
                throw new InvalidOperationException(
                    $"同一個 page 實例已經在 stack 裡了：{iPage}。要回到那一頁請用 PopUntil(page)，"
                    + "要開第二份請 new 一個新實例（同一個實例在 stack 裡出現兩次會讓 Pop/Remove 的行為變成看運氣）。");

            SCP_GuiPage? aOld = TopPage;
            iPage.Controller = this;
            m_Pages.Add(iPage);
            iPage.OnPush();
            if (aOld != null) aOld.OnPause();
        }

        /// <summary>關掉最上方那頁。回傳有沒有真的關掉（空的時候回 false，不是丟例外 —— 使用者連按兩次返回很正常）。</summary>
        public bool Pop()
        {
            SCP_GuiPage? aTop = TopPage;
            if (aTop == null) return false;

            m_Pages.RemoveAt(m_Pages.Count - 1);
            aTop.Controller = null;
            aTop.OnClose();

            SCP_GuiPage? aNew = TopPage;
            if (aNew != null) aNew.OnResume();
            return true;
        }

        /// <summary>一直 pop 到 iTarget 成為 TopPage。⚠ iTarget 不在 stack 裡會**清空**，所以先檢查。</summary>
        public bool PopUntil(SCP_GuiPage iTarget)
        {
            if (!m_Pages.Contains(iTarget)) return false;   // 不在裡面就什麼都不做（清空是災難不是「盡力而為」）
            while (m_Pages.Count > 0 && !ReferenceEquals(TopPage, iTarget)) Pop();
            return true;
        }

        /// <summary>依 <see cref="SCP_GuiPage.Key"/> pop 回去（agent／腳本用得到的那條路）。</summary>
        public bool PopUntilKey(string iKey)
        {
            for (int i = m_Pages.Count - 1; i >= 0; i--)
                if (m_Pages[i].Key == iKey) return PopUntil(m_Pages[i]);
            return false;
        }

        public void PopAll() { while (m_Pages.Count > 0) Pop(); }

        /// <summary>
        /// 回到最底層那一頁（＝「回首頁」）。
        /// <para>⚠ 這不是 <see cref="PopAll"/>。UCL 那側的「Close」是 PopAll，因為它的前提是
        /// 「關掉整組面板」；這裡的前提是**最底層那頁就是入口頁**，PopAll 會清空堆疊 ⇒
        /// 畫面變成 controller 的「頁面堆疊是空的」那一行。那不是關閉，那是空白。</para>
        /// </summary>
        /// <returns>pop 掉幾頁（本來就在最底層 ⇒ 0）。</returns>
        public int PopToRoot()
        {
            int aCount = 0;
            while (m_Pages.Count > 1) { Pop(); aCount++; }
            return aCount;
        }

        /// <summary>
        /// 把某一頁從 stack 裡抽掉（可能在中間）。
        /// 抽掉的正好是 TopPage ⇒ 等同 <see cref="Pop"/>（新的 TopPage 會收到 OnResume）。
        /// </summary>
        public bool Remove(SCP_GuiPage iPage)
        {
            int aIdx = m_Pages.IndexOf(iPage);
            if (aIdx < 0) return false;
            if (aIdx == m_Pages.Count - 1) return Pop();

            m_Pages.RemoveAt(aIdx);
            iPage.Controller = null;
            iPage.OnClose();
            return true;   // 不在最上面 ⇒ TopPage 沒變，誰也不必 OnResume
        }

        /// <summary>換掉最上方那頁（等於 Pop 再 Push，但只在真的有東西可換時才動）。</summary>
        public void Replace(SCP_GuiPage iPage)
        {
            Pop();
            Push(iPage);
        }

        // ── 繪製 ──────────────────────────────────────────────────
        /// <summary>
        /// 畫最上方那頁。回傳有沒有東西可畫。
        /// <para>空堆疊時**畫一行說明而不是留白** —— 空白畫面沒辦法分辨
        /// 「沒有頁面」與「頁面畫不出來」，而那兩件事的處置完全不同。</para>
        /// </summary>
        public bool Draw(SCP_Ui iUi)
        {
            SCP_GuiPage? aTop = TopPage;
            if (aTop == null)
            {
                iUi.Note("頁面堆疊是空的（沒有任何頁面被 push）—— 這不是畫面壞了。");
                return false;
            }

            if (ShowBreadcrumb && m_Pages.Count > 1) iUi.Note(PathText);

            // 頁面自己畫工具列時 controller 就不要再畫一顆 —— 兩顆返回鈕不會報錯，
            // 只會讓第二顆變成 `page/back#2`，然後照清單抄指令的人按到另一顆。
            if (ShowBackBar && m_Pages.Count > 1 && !aTop.OwnsNavBar)
            {
                using (iUi.Row())
                {
                    // id 固定 —— 這是契約：文字模式／agent 都靠 `--click page/back` 返回
                    if (iUi.Button("◀ 返回", BackButtonId)) Pop();
                }
            }

            // 頁面自己的 id 命名空間：兩頁各有一個沒傳 key 的「篩選」欄位時不會撞
            // （撞了會共用 session 值 —— 那不會報錯，只會讓另一頁的欄位莫名有值）
            using (iUi.IdScope(aTop.Key))
            {
                if (!string.IsNullOrEmpty(aTop.Title)) iUi.Title(aTop.Title);
                aTop.Draw(iUi);
            }
            return true;
        }

        /// <summary>返回鈕的固定 id（呼叫端要組指令時別自己拼字串）。</summary>
        public const string BackButtonId = "page/back";

        /// <summary>給最上方那頁一次跑邏輯的機會（每幀／每次 CLI 呼叫一次）。</summary>
        public bool Tick()
        {
            SCP_GuiPage? aTop = TopPage;
            if (aTop == null) return false;
            aTop.Tick();
            return true;
        }

        // ── 讀數 ──────────────────────────────────────────────────
        /// <summary>由下往上的導覽路徑（`根 ▸ 中間 ▸ 現在這頁`）。</summary>
        public string PathText
        {
            get
            {
                var sb = new StringBuilder();
                for (int i = 0; i < m_Pages.Count; i++)
                {
                    if (i > 0) sb.Append(" ▸ ");
                    SCP_GuiPage p = m_Pages[i];
                    sb.Append(string.IsNullOrEmpty(p.Title) ? p.Key : p.Title);
                }
                return sb.ToString();
            }
        }

        /// <summary>
        /// 由下往上的 <see cref="SCP_GuiPage.Key"/> 清單 —— **這就是可以被存起來的導覽狀態**。
        /// <para>每次 CLI 呼叫都是新 process ⇒ 不存這個，多步導覽（進到細節頁再按東西）不可能成立。
        /// 復原走 <see cref="RestorePath"/>。</para>
        /// </summary>
        public List<string> PathKeys
        {
            get
            {
                var aKeys = new List<string>(m_Pages.Count);
                foreach (SCP_GuiPage p in m_Pages) aKeys.Add(p.Key);
                return aKeys;
            }
        }

        /// <summary>
        /// 依 key 清單重建這一疊（第一個 key 應該是根頁；根頁已經在 stack 裡就從第二個開始接）。
        /// <para>iFactory 回 null ＝ 這個 key 現在做不出頁面（資料被刪了／版本不認得）——
        /// **停在那裡並回報**，不要跳過它繼續往上疊：跳過會讓使用者回到一個
        /// 「看起來是我剛剛那頁、其實是別頁」的畫面。</para>
        /// </summary>
        /// <returns>沒能重建的第一個 key（全部成功則為 null）。</returns>
        public string? RestorePath(IReadOnlyList<string> iKeys, Func<string, SCP_GuiPage?> iFactory)
        {
            if (iKeys == null || iFactory == null) return null;
            for (int i = 0; i < iKeys.Count; i++)
            {
                string aKey = iKeys[i];
                if (i < m_Pages.Count)
                {
                    // 已經在 stack 裡的那幾層要對得上；對不上就停手（別硬接）
                    if (m_Pages[i].Key == aKey) continue;
                    return aKey;
                }
                SCP_GuiPage? aPage = iFactory(aKey);
                if (aPage == null) return aKey;
                Push(aPage);
            }
            return null;
        }
    }
}
