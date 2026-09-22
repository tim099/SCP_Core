// 區塊職責：把一個頁面型別**明確排除**在「頁面發現」之外（見 SCP_GuiPageCatalog.Discover）。
// 物理意義：測試用的探針頁繼承 SCP_GuiToolPage，但它們本來就不該被登記 ——
//           如果讓它們每次都出現在警示清單裡，那條警示會被訓練成背景音，
//           而**背景音是這套系統最不想要的東西**：真的漏登記那天，沒有人會多看一眼。
//           ⇒ 排除必須是**顯式的**（打在型別上、看得見、進 git diff），不是靠命名慣例猜。
// ⚠ 判準：只有「這個型別**根本不是頁**」才可以打（測試探針那種）。
//   「這頁還沒做完」不算 —— 那要嘛讓它被收錄、要嘛先不要繼承 SCP_GuiToolPage。
//
// 🔴 **界線：不想被列在入口頁清單上的頁，不要貼這個** —— 那一格走 `MenuGroup => null`。
//   四種常被混在一起的類別，各有各的現成機制（Tim 2026-09-22 點名；⛔ 別再發明第五套）：
//
//   | 類別 | 用什麼 | 收錄 | 入口頁列 | `Create`／`--page` |
//   |---|---|---|---|---|
//   | **根本不是頁**（測試探針） | **本標記** | ❌ | ❌ | ❌ |
//   | **彈窗／選項頁**（內容由母頁決定） | `MenuGroup => null` | ✅ | ❌ | ✅ |
//   | **入口頁自己**／只從工具列進的頁 | `MenuGroup => null` | ✅ | ❌ | ✅ |
//   | **給人繼承的基底頁** | **`abstract`**（語言自帶，編譯期擋 `new`；掃描本來就跳過） | ❌ | ❌ | ❌ |
//
//   🩸 貼錯的代價（2026-09-22 實測，⛔ 不是風格問題）：彈窗頁貼成本標記 ⇒ 不收錄
//   ⇒ `Create` 回 null ⇒ CLI 下一次呼叫走 `RestorePath` 時**停在彈窗那一層並回報**
//   ⇒ 在彈窗裡按第二下，人其實已經掉回母頁。⚠ 而那是主路不是邊角。
//   📌 反過來也要知道：探針頁改用 `MenuGroup => null` 也不對 ——
//   它會被收錄、ctor 會被呼叫、`--page` 叫得到，而它本來就不該存在於那個命名空間裡。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;

namespace SCP.Core.Gui
{
    /// <summary>
    /// 這個型別不是真的頁（測試探針之類）—— **連收錄都不會發生**。
    /// <para>⚠ 2026-09-22（TASK-0276）語意**變重了**：此前它只是「不列進『未登記的頁』警示」，
    /// 而目錄改成自動收頁之後，它擋的是 <see cref="SCP_GuiPageCatalog.AutoRegister"/> 的收錄本身
    /// ⇒ 貼了它的型別不會被建、不會進清單、也不會被 `--page` 叫到。</para>
    /// <para>⇒ 所以貼它的門檻也跟著變高：以前貼錯只是少一行警示，現在貼錯是**那頁消失**。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class SCP_PageIgnoreAttribute : Attribute
    {
        /// <param name="iWhy">為什麼不是頁。**必填** —— 沒有理由的排除，下一個人不知道能不能拿掉。</param>
        public SCP_PageIgnoreAttribute(string iWhy) { Why = iWhy; }

        public string Why { get; }
    }
}
