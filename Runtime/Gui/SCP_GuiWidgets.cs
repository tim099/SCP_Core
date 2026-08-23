// 區塊職責：**複合元件** —— 用既有的基本節點（Button／TextField／Box）組出來的東西。
// 物理意義：概念取自 Unity 端的 UCL_GUILayout.PopupSearch（一顆顯示現值的鈕 → 點開 → 搜尋框 ＋ 分頁的選項列）。
//           ⭐ 但這裡**刻意不新增節點型別**：新增一種 Kind 要同時改 5 個地方
//           （enum／撰寫端／文字 renderer／ImGui renderer／可互動元件清單），
//           而漏掉的那一處不會報錯，只會「某個 renderer 少畫一塊」。
//           ⇒ 用既有節點組出來的元件，四種驅動方式（視窗／文字／指令／截圖）**天生就會**。
// 數值影響：零 IO。跨輪狀態（開闔／搜尋字／第幾頁／選了誰）全部住在 SCP_GuiInput.Fields，
//           所以它們自動進 session、可以被 diff、`--reset` 也清得掉。
//           ⚠ 但**不是全部都 --set 得動**：`--set` 會驗「這個 id 在畫面上嗎」，
//           而開闔／選中值是內部狀態、不是畫面元件 ⇒ 那條路會被守衛擋下（那是對的）。
//           畫面上有的（搜尋框、每個選項的鈕）才操作得到，而它們展開時才存在。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SCP.Core.Gui
{
    /// <summary>下拉選單的一個選項：<see cref="Value"/> 是契約（進 id、進 session），<see cref="Label"/> 給人看。</summary>
    public readonly struct SCP_GuiOption
    {
        public SCP_GuiOption(string iValue, string iLabel)
        {
            Value = iValue;
            Label = string.IsNullOrEmpty(iLabel) ? iValue : iLabel;
        }

        public SCP_GuiOption(string iValue) : this(iValue, iValue) { }

        /// <summary>回傳值 —— **不要用序號**（清單增刪一筆，序號就指到別人身上，而那不會報錯）。</summary>
        public string Value { get; }

        /// <summary>顯示文字。</summary>
        public string Label { get; }
    }

    public static class SCP_GuiWidgets
    {
        /// <summary>選項多於這個數才畫搜尋框（少量選項時搜尋框只是多一行雜訊）。</summary>
        public const int DefaultSearchThreshold = 8;

        /// <summary>展開後一頁列幾個。</summary>
        public const int DefaultRowsPerPage = 12;

        /// <summary>字串清單的簡便版（value ＝ label）。</summary>
        public static string Dropdown(this SCP_Ui iUi, string iLabel, IReadOnlyList<string> iOptions,
            string iCurrent, string iKey,
            int iSearchThreshold = DefaultSearchThreshold, int iRowsPerPage = DefaultRowsPerPage,
            bool iDefaultOpen = false)
        {
            var aOptions = new List<SCP_GuiOption>(iOptions.Count);
            foreach (string s in iOptions) aOptions.Add(new SCP_GuiOption(s));
            return Dropdown(iUi, iLabel, aOptions, iCurrent, iKey, iSearchThreshold, iRowsPerPage, iDefaultOpen);
        }

        /// <summary>
        /// 可搜尋的下拉選單。回傳**這一輪之後**選中的 value（沒人動 ⇒ 現值）。
        /// <code>
        /// string aPick = g.Dropdown("頁面", aOptions, aDefaultKey, "home/page");
        /// if (g.Button("開啟", "home/open")) Open(aPick);
        /// </code>
        /// <para>用到的 id（全部是 <c>iKey</c> 的前綴，可以直接拿去下指令）：</para>
        /// <list type="bullet">
        ///   <item><c>iKey</c> —— 展開／收合那顆鈕（<c>--click</c>）</item>
        ///   <item><c>iKey/value</c> —— 選中的值（**內部狀態**：它不是畫面上的元件，
        ///   所以 <c>--set</c> 會被「畫面上沒有這個 id」擋下 —— 要選就先 <c>--click iKey</c> 點開再選）</item>
        ///   <item><c>iKey/search</c> —— 搜尋字　<c>iKey/page</c> —— 第幾頁（0 起算）</item>
        ///   <item><c>iKey/pick/&lt;value&gt;</c> —— 每一個選項的鈕</item>
        /// </list>
        /// </summary>
        public static string Dropdown(this SCP_Ui iUi, string iLabel, IReadOnlyList<SCP_GuiOption> iOptions,
            string iCurrent, string iKey,
            int iSearchThreshold = DefaultSearchThreshold, int iRowsPerPage = DefaultRowsPerPage,
            bool iDefaultOpen = false)
        {
            if (iUi == null) throw new ArgumentNullException(nameof(iUi));
            if (string.IsNullOrEmpty(iKey)) throw new ArgumentException("下拉選單一定要有顯式 key（那是 id 契約）", nameof(iKey));
            if (iRowsPerPage < 1) iRowsPerPage = DefaultRowsPerPage;

            string aValueKey = iKey + "/value";
            string aOpenKey = iKey + "/open";
            string aSearchKey = iKey + "/search";
            string aPageKey = iKey + "/page";

            string aCurrent = iUi.FieldValue(aValueKey, iCurrent);

            if (iOptions == null || iOptions.Count == 0)
            {
                // 「沒有選項」與「元件沒畫出來」不得同形 —— 所以照樣說一句話。
                iUi.Note(iLabel + "：(沒有可選的項目)");
                return aCurrent;
            }

            int aCurIdx = IndexOfValue(iOptions, aCurrent);
            string aCurText = aCurIdx >= 0
                ? iOptions[aCurIdx].Label
                : (aCurrent.Length == 0 ? "(未選)" : aCurrent + " ⚠(不在清單裡)");

            // ⚠ **預設摺疊**：一個一進來就攤開的下拉，等於把整份清單塞在版面上，
            //   而使用者還沒表示他想選東西。要改的話顯式傳 iDefaultOpen: true。
            bool aOpen = iUi.FieldValue(aOpenKey, iDefaultOpen ? "1" : "0") == "1";

            // ⭐ 展開時把「頭 ＋ 展開的那一塊」包成**一個群組**再交出去。
            //    理由是版位：這個元件常常被放在一個 Row 裡（旁邊有「開啟」之類的鈕），
            //    而展開的清單要對齊**自己那顆頭**、不是對齊視窗左緣。
            //    包成群組之後 ImGui 的 BeginGroup 會把群組起點當成新的左緣 ⇒ 自動對齊。
            //    ⚠ 收合時**不包**：只有一顆鈕的話包群組只會讓文字模式多換一行
            //    （文字模式沒有水平版位，群組一律換行）。
            if (!aOpen)
            {
                if (iUi.Button(HeaderText(iLabel, aCurText, false), iKey)) iUi.SetField(aOpenKey, "1");
                return aCurrent;
            }

            // ⭐ **頭也在這個等寬群組裡面**（不是頭在外、清單在內）：
            //    ① 對齊 —— 清單的左緣就是頭的左緣，不必去猜別人的位置
            //    ② 等寬 —— 頭通常是最長的那一條，於是整塊有一條可以往下掃的直線
            //    （形狀取自 Unity 端：Open 鈕在左，右邊整塊 vertical scope 自己對齊自己）
            using (iUi.Box("", null, iUniformWidth: true))
            {
                if (iUi.Button(HeaderText(iLabel, aCurText, true), iKey))
                {
                    // 這一輪照樣把清單畫完（結構由**存下來的**狀態決定）——
                    // 收起來會在下一輪發生，跟按鈕事件同一個節奏。
                    iUi.SetField(aOpenKey, "0");
                }

                {
                    string aQuery = iOptions.Count >= iSearchThreshold
                        ? iUi.TextField("搜尋", "", aSearchKey)
                        : "";

                    List<SCP_GuiOption> aHits = Filter(iOptions, aQuery);

                    if (aHits.Count == 0)
                    {
                        iUi.Note($"沒有符合「{aQuery}」的項目（{iOptions.Count} 個選項全部被篩掉了）");
                        if (iUi.Button("清除搜尋", iKey + "/clear")) iUi.SetField(aSearchKey, "");
                        return aCurrent;
                    }

                    int aPages = (aHits.Count + iRowsPerPage - 1) / iRowsPerPage;
                    int aPage = ParsePage(iUi.FieldValue(aPageKey, "0"));
                    int aClamped = aPage < 0 ? 0 : (aPage >= aPages ? aPages - 1 : aPage);
                    // 夾過就寫回去 —— 不寫的話「搜尋把清單變短了」會讓頁碼永遠停在一個不存在的頁，
                    // 而畫面上只會看到空清單（那跟「沒有符合的項目」同形）。
                    if (aClamped != aPage) iUi.SetField(aPageKey, aClamped.ToString(CultureInfo.InvariantCulture));

                    int aStart = aClamped * iRowsPerPage;
                    int aEnd = Math.Min(aHits.Count, aStart + iRowsPerPage);

                    for (int i = aStart; i < aEnd; i++)
                    {
                        SCP_GuiOption o = aHits[i];
                        // id 用 **value 本身**，不用序號 —— 搜尋／翻頁都會改變序號，而 id 不可以跟著漂
                        string aId = iKey + "/pick/" + o.Value;
                        string aMark = o.Value == aCurrent ? "● " : "";
                        if (iUi.Button(aMark + o.Label, aId))
                        {
                            aCurrent = o.Value;
                            iUi.SetField(aValueKey, o.Value);
                            iUi.SetField(aOpenKey, "0");   // 選完收起來（不收的話下一輪整份清單還攤在那）
                        }
                    }

                    if (aPages > 1)
                    {
                        using (iUi.Row())
                        {
                            // 邊界上就不畫那顆鈕（而不是畫一顆按了沒事的）——
                            // 按了沒事的鈕會讓人以為是壞的
                            if (aClamped > 0 && iUi.Button("◀ 上一頁", iKey + "/prev"))
                                iUi.SetField(aPageKey, (aClamped - 1).ToString(CultureInfo.InvariantCulture));

                            iUi.Label($"第 {aClamped + 1}/{aPages} 頁"
                                + (aQuery.Length > 0
                                    ? $"（符合 {aHits.Count}／共 {iOptions.Count}）"
                                    : $"（共 {aHits.Count}）"));

                            if (aClamped < aPages - 1 && iUi.Button("下一頁 ▶", iKey + "/next"))
                                iUi.SetField(aPageKey, (aClamped + 1).ToString(CultureInfo.InvariantCulture));
                        }
                    }
                }
            }

            return aCurrent;
        }

        /// <summary>
        /// 篩選：**空白分隔的關鍵字，每一個都要命中**（比對 Label 與 Value，忽略大小寫）。
        /// <para>⚠ 刻意不是 regex。UCL 那側用 regex 並在編譯失敗時退回「不篩」——
        /// 於是打一個 <c>(</c> 會讓清單看起來「全部都符合」，而使用者以為自己在搜尋。
        /// 使用者打進搜尋框的是**關鍵字不是樣式**，所以這裡用子字串比對：它永遠不會丟例外，
        /// 也不會有「打了字卻沒在篩」的狀態。</para>
        /// </summary>
        public static List<SCP_GuiOption> Filter(IReadOnlyList<SCP_GuiOption> iOptions, string iQuery)
        {
            var aHits = new List<SCP_GuiOption>();
            string[] aTerms = (iQuery ?? "").Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (SCP_GuiOption o in iOptions)
            {
                bool aAll = true;
                foreach (string t in aTerms)
                {
                    if (o.Label.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    if (o.Value.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    aAll = false;
                    break;
                }
                if (aAll) aHits.Add(o);
            }
            return aHits;
        }

        /// <summary>頭上那顆鈕的字。▼／▲ 是唯一看得出「現在是收還是開」的記號，文字模式也吃這一格。</summary>
        static string HeaderText(string iLabel, string iCurText, bool iOpen)
            => iLabel + "：" + iCurText + (iOpen ? "  ▲" : "  ▼");

        static int IndexOfValue(IReadOnlyList<SCP_GuiOption> iOptions, string iValue)
        {
            for (int i = 0; i < iOptions.Count; i++) if (iOptions[i].Value == iValue) return i;
            return -1;
        }

        /// <summary>頁碼壞掉（有人手動 --set 了一個 "abc"）⇒ 回第 0 頁，不丟例外也不當成沒設。</summary>
        static int ParsePage(string iText)
            => int.TryParse(iText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
    }
}
