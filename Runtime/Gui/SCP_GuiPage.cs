// 區塊職責：一頁後台的基底 —— 一個 page 負責畫自己，並知道自己被誰管著。
// 物理意義：概念取自 Unity 端的 UCL_GUIPage（stack、只顯示最上方那頁、Pause/Resume/Close 生命週期），
//           但撰寫端從 OnGUI() 換成 Draw(SCP_Ui) —— 於是同一頁在四種驅動方式下都是同一份碼：
//           ImGui 視窗 / 純文字 / 指令操作 / 截圖。
// 數值影響：本層零 IO、零繪圖依賴。頁面的資料（讀數、model）由子類自己持有，
//           **不要在這裡放跨頁共用的靜態欄位** —— 那會讓兩個視窗互相蓋。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;

namespace SCP.Core.Gui
{
    /// <summary>
    /// 一頁。用法：繼承它、實作 <see cref="Draw"/>，push 進 <see cref="SCP_GuiPageController"/>。
    /// <code>
    /// sealed class MyPage : SCP_GuiPage
    /// {
    ///     public override string Key => "my";           // 導覽鍵（契約，見下）
    ///     public override string Title => "我的頁面";
    ///     public override void Draw(SCP_Ui g)
    ///     {
    ///         if (g.Button("看細節", "my/detail")) Controller?.Push(new DetailPage());
    ///     }
    /// }
    /// </code>
    /// </summary>
    public abstract class SCP_GuiPage
    {
        /// <summary>
        /// 導覽鍵 —— **這是契約，不是顯示字串**。
        /// <para>它會被寫進 session（`nav`）用來在下一個 process 重建同一疊頁面，
        /// 也是 agent／腳本指名「我要哪一頁」的名字。⇒ 一律用**資料本身的鍵**
        /// （例：`project/LY`），⛔ 不要用序號或畫面順序 —— 清單增刪一筆，
        /// 所有 id 就位移到別人身上，而那不會報錯。</para>
        /// </summary>
        public abstract string Key { get; }

        /// <summary>標題（顯示用；空字串 ⇒ 呼叫端自己決定要不要留白）。</summary>
        public virtual string Title => "";

        /// <summary>管著這一頁的 controller。push 進去時由 controller 設定。</summary>
        public SCP_GuiPageController? Controller { get; internal set; }

        /// <summary>這一頁是不是 controller 的最上方那頁（＝現在真的畫得出來的那頁）。</summary>
        public bool IsTop => Controller != null && ReferenceEquals(Controller.TopPage, this);

        /// <summary>畫這一頁。⚠ 只有最上方那頁會被呼叫。</summary>
        public abstract void Draw(SCP_Ui iUi);

        /// <summary>
        /// 每輪（每幀／每次 CLI 呼叫）給最上方那頁一次跑邏輯的機會。
        /// ⚠ **不要在這裡取讀數**（每幀跑 git／IO 會炸）—— 那是按鈕與 model 的事。
        /// </summary>
        public virtual void Tick() { }

        // ── 生命週期（對應 UCL_GUIPage 的 Init / OnPause / OnResume / OnClose）──
        /// <summary>被 push 進 controller 之後。</summary>
        public virtual void OnPush() { }

        /// <summary>有新的一頁蓋在自己上面（自己不再是 TopPage）。</summary>
        public virtual void OnPause() { }

        /// <summary>上面那頁關掉了，自己重新成為 TopPage。</summary>
        public virtual void OnResume() { }

        /// <summary>自己被關掉（pop 出 stack）。</summary>
        public virtual void OnClose() { CloseEvent?.Invoke(this); }

        /// <summary>關頁事件（呼叫端想在頁面關掉時刷新讀數就掛這裡）。</summary>
        public event Action<SCP_GuiPage>? CloseEvent;

        /// <summary>
        /// 關掉自己。⚠ 只有在自己是 TopPage 時才等於「返回上一頁」；
        /// 不是 TopPage 時是把自己從 stack 中間抽掉（<see cref="SCP_GuiPageController.Remove"/>）。
        /// </summary>
        public void Close() { Controller?.Remove(this); }

        public override string ToString() => $"{GetType().Name}({Key})";
    }
}
