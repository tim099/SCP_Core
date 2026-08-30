// 區塊職責：把一個頁面型別**明確排除**在「頁面發現」之外（見 SCP_GuiPageCatalog.Discover）。
// 物理意義：測試用的探針頁繼承 SCP_GuiToolPage，但它們本來就不該被登記 ——
//           如果讓它們每次都出現在警示清單裡，那條警示會被訓練成背景音，
//           而**背景音是這套系統最不想要的東西**：真的漏登記那天，沒有人會多看一眼。
//           ⇒ 排除必須是**顯式的**（打在型別上、看得見、進 git diff），不是靠命名慣例猜。
// ⚠ 判準：只有「這個型別根本不是要給人開的頁」才可以打。
//   「這頁還沒做完」不算 —— 那要嘛登記它、要嘛先不要繼承 SCP_GuiToolPage。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;

namespace SCP.Core.Gui
{
    /// <summary>這個型別不是真的頁（測試探針之類）—— 不列進「未登記的頁」警示。</summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class SCP_PageIgnoreAttribute : Attribute
    {
        /// <param name="iWhy">為什麼不是頁。**必填** —— 沒有理由的排除，下一個人不知道能不能拿掉。</param>
        public SCP_PageIgnoreAttribute(string iWhy) { Why = iWhy; }

        public string Why { get; }
    }
}
