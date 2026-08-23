// 區塊職責：page key → 頁面工廠 ＋ 選單用的中繼資料（標題／分組）。
// 物理意義：入口頁要問三個問題 —— 有哪些頁、它們分幾組、選了之後怎麼生出來。
//           UCL 那側用**反射掃 assembly** 找 ShowInPageMenu==true 的子類；這裡改成**顯式登記**：
//           ① 這裡的頁面建構要吃 model（沒有無參 ctor），反射 Activator 生不出來
//           ② 反射掃出來的清單會隨「哪些 assembly 剛好載入」而變，而那個差異不會報錯 ——
//              症狀是「同一份程式在別台機器少了兩頁」
//           ⇒ 顯式登記多打一行，換到的是「清單就是清單，不會因為環境而變形」。
// 數值影響：純資料 ＋ 一次性的中繼資料探測（見下），零 IO。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Gui
{
    /// <summary>目錄裡的一筆（給入口頁列清單用）。</summary>
    public sealed class SCP_GuiPageEntry
    {
        public SCP_GuiPageEntry(string iKey, string iTitle, string? iGroup)
        {
            Key = iKey;
            Title = iTitle;
            Group = iGroup;
        }

        /// <summary>page key —— 契約（<c>--page</c>／session <c>nav</c>／麵包屑都是它）。</summary>
        public string Key { get; }

        /// <summary>顯示標題（空的話呼叫端自己退回用 Key）。</summary>
        public string Title { get; }

        /// <summary>分組名（<see cref="SCP_GuiToolPage.MenuGroup"/>）。null ＝ 不列進選單。</summary>
        public string? Group { get; }

        /// <summary>清單上要顯示的字（標題 ＋ key，因為 key 才是拿去下指令的那個）。</summary>
        public string Label => Title.Length == 0 || Title == Key ? Key : Title + "（" + Key + "）";
    }

    /// <summary>
    /// 頁面目錄。用法：
    /// <code>
    /// var aCatalog = new SCP_GuiPageCatalog();
    /// aCatalog.Register(HomePage.PageKey, () => new HomePage(aModel, aCatalog));
    /// aCatalog.Register(DoctorPage.PageKey, () => new DoctorPage(aModel));
    /// SCP_GuiPage? aPage = aCatalog.Create("doctor");     // 認不得回 null
    /// </code>
    /// </summary>
    public sealed class SCP_GuiPageCatalog
    {
        readonly List<KeyValuePair<string, Func<SCP_GuiPage>>> m_Factories =
            new List<KeyValuePair<string, Func<SCP_GuiPage>>>();

        readonly List<string> m_Diagnostics = new List<string>();

        List<SCP_GuiPageEntry>? m_Entries;   // 中繼資料快取（探測過一次就不再建實例）

        /// <summary>
        /// 登記一頁。
        /// <para>⚠ 同一個 key 登記兩次 ⇒ 丟例外。那是程式錯誤：<c>Create</c> 要回哪一個、
        /// 清單要列哪一個會變成看運氣的事，而它「能跑」。</para>
        /// </summary>
        public void Register(string iKey, Func<SCP_GuiPage> iFactory)
        {
            if (string.IsNullOrEmpty(iKey)) throw new ArgumentException("page key 不可以是空字串", nameof(iKey));
            if (iFactory == null) throw new ArgumentNullException(nameof(iFactory));
            foreach (var kv in m_Factories)
                if (kv.Key == iKey)
                    throw new InvalidOperationException($"page key 重複登記：{iKey}");

            m_Factories.Add(new KeyValuePair<string, Func<SCP_GuiPage>>(iKey, iFactory));
            m_Entries = null;
        }

        /// <summary>
        /// 依 key 造一頁；**認不得就回 null**（不要猜、不要退回根頁 ——
        /// 退回根頁會讓「你要的那頁不存在」長得像「你本來就在首頁」）。
        /// </summary>
        public SCP_GuiPage? Create(string iKey)
        {
            foreach (var kv in m_Factories)
                if (kv.Key == iKey) return kv.Value();
            return null;
        }

        public bool Has(string iKey)
        {
            foreach (var kv in m_Factories) if (kv.Key == iKey) return true;
            return false;
        }

        /// <summary>登記過的所有 key（含 MenuGroup 為 null 的那些）—— 給錯誤訊息列「現有：…」用。</summary>
        public List<string> AllKeys
        {
            get
            {
                var aKeys = new List<string>(m_Factories.Count);
                foreach (var kv in m_Factories) aKeys.Add(kv.Key);
                return aKeys;
            }
        }

        /// <summary>
        /// 探測時遇到的問題（建不出來的頁、key 對不上的頁…）。
        /// <para>⚠ 呼叫端**要把它畫出來** —— 一頁悄悄從清單消失，跟「本來就沒有那頁」同形。</para>
        /// </summary>
        public IReadOnlyList<string> Diagnostics { get { EnsureEntries(); return m_Diagnostics; } }

        /// <summary>列進選單的所有頁（<see cref="SCP_GuiToolPage.MenuGroup"/> 非 null），依分組、標題排序。</summary>
        public IReadOnlyList<SCP_GuiPageEntry> Entries { get { EnsureEntries(); return m_Entries!; } }

        /// <summary>出現過的分組名（排序、去重）。</summary>
        public List<string> Groups
        {
            get
            {
                EnsureEntries();
                var aGroups = new List<string>();
                foreach (SCP_GuiPageEntry e in m_Entries!)
                {
                    string g = e.Group ?? "";
                    if (!aGroups.Contains(g)) aGroups.Add(g);
                }
                aGroups.Sort(StringComparer.OrdinalIgnoreCase);
                return aGroups;
            }
        }

        /// <summary>某一組的頁。iGroup 為 null 或空字串 ⇒ **全部**（「不篩」與「篩空分組」在這裡刻意同義，見下）。</summary>
        public List<SCP_GuiPageEntry> InGroup(string? iGroup)
        {
            EnsureEntries();
            var aList = new List<SCP_GuiPageEntry>();
            foreach (SCP_GuiPageEntry e in m_Entries!)
            {
                if (!string.IsNullOrEmpty(iGroup) && (e.Group ?? "") != iGroup) continue;
                aList.Add(e);
            }
            return aList;
        }

        /// <summary>丟掉中繼資料快取（對應 UCL 選單上那顆「↻」）。下次要用時重新探測。</summary>
        public void Invalidate() { m_Entries = null; }

        /// <summary>
        /// 建一次實例讀中繼資料（標題／分組），然後丟掉。
        /// <para>⚠ 這條路的前提是**頁面的建構子很便宜**（不碰檔案、不跑 git）。
        /// 那本來就該成立 —— 建構是「做一個物件」，取讀數是 <c>OnPush</c>／按鈕的事 ——
        /// 但它現在變成了目錄的隱含要求，所以寫在這裡而不是只寫在心裡。</para>
        /// <para>建不出來的頁**記一筆並跳過**，不讓一頁壞掉的頁擋住整個清單。</para>
        /// </summary>
        void EnsureEntries()
        {
            if (m_Entries != null) return;

            var aList = new List<SCP_GuiPageEntry>();
            m_Diagnostics.Clear();

            foreach (var kv in m_Factories)
            {
                SCP_GuiPage aProbe;
                try { aProbe = kv.Value(); }
                catch (Exception e)
                {
                    m_Diagnostics.Add($"'{kv.Key}' 建不出來，已跳過：{e.GetType().Name}: {e.Message}");
                    continue;
                }

                // 登記用的 key 與頁面自己回的 Key 對不上 ⇒ 那是兩份真相。
                // session 的 nav 存的是**頁面自己的 Key**，所以以它為準，並且把差異說出來。
                string aKey = aProbe.Key;
                if (aKey != kv.Key)
                    m_Diagnostics.Add(
                        $"登記的 key '{kv.Key}' 與頁面自己的 Key '{aKey}' 不一致 —— "
                        + "清單以頁面為準（session 存的是它），但 Create('" + kv.Key + "') 仍然查得到，請把兩邊改成同一個字");

                string? aGroup = (aProbe as SCP_GuiToolPage)?.MenuGroup;
                if (aGroup == null) continue;   // opt-in：沒宣告分組就不列

                aList.Add(new SCP_GuiPageEntry(aKey, aProbe.Title, aGroup));
            }

            aList.Sort((a, b) =>
            {
                int c = string.Compare(a.Group, b.Group, StringComparison.OrdinalIgnoreCase);
                if (c != 0) return c;
                return string.Compare(a.Label, b.Label, StringComparison.OrdinalIgnoreCase);
            });
            m_Entries = aList;
        }
    }
}
