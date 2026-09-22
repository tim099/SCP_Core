// 區塊職責：page key → 頁面工廠 ＋ 選單用的中繼資料（標題／分組）。
// 物理意義：入口頁要問三個問題 —— 有哪些頁、它們分幾組、選了之後怎麼生出來。
//           UCL 那側用**反射掃 assembly** 找 ShowInPageMenu==true 的子類；這裡改成**顯式登記**：
//           ① 這裡的頁面建構要吃 model（沒有無參 ctor），反射 Activator 生不出來
//           ② 反射掃出來的清單會隨「哪些 assembly 剛好載入」而變，而那個差異不會報錯 ——
//              症狀是「同一份程式在別台機器少了兩頁」
//           ⇒ 顯式登記多打一行，換到的是「清單就是清單，不會因為環境而變形」。
//
//           ⭐ 2026-08-30 Tim 拍板補上**混合形狀**（不是推翻上面那兩條，是補它們的洞）：
//           顯式登記治得了「清單會變形」，治不了「**我忘了登記**」—— 而忘記登記的症狀
//           跟「本來就沒那頁」一模一樣。⇒ 加一支 <see cref="SCP_GuiPageCatalog.Discover"/>：
//             · **反射只負責發現**（掃 SCP_GuiToolPage 的非抽象子類）
//             · **建構仍然只走登記過的 factory**（反射建不出吃 context 的 ctor）
//             · 掃到了卻沒有 factory ⇒ **列在畫面上標紅**，不是 log 一行然後跳過
//           📌 最後一條是重點：UCL 那版是 LogWarning + continue，而 log 沒人讀、
//             清單上少一行也沒人會發現。**要讓「少一頁」變成看得見的字。**
// 數值影響：純資料 ＋ 一次性的中繼資料探測（見下），零 IO。Discover 另外吃一次反射成本。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SCP.Core.Gui
{
    /// <summary>目錄裡的一筆（給入口頁列清單用）。</summary>
    public sealed class SCP_GuiPageEntry
    {
        public SCP_GuiPageEntry(string iKey, string iTitle, string? iGroup,
                                string iTypeName = "", string iTypeFullName = "")
        {
            Key = iKey;
            Title = iTitle;
            Group = iGroup;
            TypeName = iTypeName;
            TypeFullName = iTypeFullName;
        }

        /// <summary>page key —— 契約（<c>--page</c>／session <c>nav</c>／麵包屑都是它）。</summary>
        public string Key { get; }

        /// <summary>顯示標題（空的話呼叫端自己退回用 Key）。</summary>
        public string Title { get; }

        /// <summary>分組名（<see cref="SCP_GuiToolPage.MenuGroup"/>）。null ＝ 不列進選單。</summary>
        public string? Group { get; }

        /// <summary>型別的簡名（`DoctorPage`）—— 下拉標籤用它，⛔ 不是身分。</summary>
        public string TypeName { get; }

        /// <summary>
        /// 型別的**完整名稱** —— 這是一筆頁的**身分**（Tim 2026-09-22 拍板）。
        /// <para>⭐ 它天生不會重複，所以「兩筆是不是同一頁」不必再靠字串 key 去猜；
        /// 而 key 撞名變成一件**可以被點名**的事（撞的雙方各有一個不同的 FullName）。</para>
        /// </summary>
        public string TypeFullName { get; }

        /// <summary>
        /// 下拉要顯示的字 ＝ **`Key(TypeName)`**（Tim 2026-09-22 拍板）；
        /// `Key` 與 `TypeName` 相等時只印 `TypeName`。
        /// <para>⭐ 判準是「**看到的字就是能打進指令的字**」—— key 才是 `--page` 與 session `nav` 吃的那個，
        /// 所以它排在前面；型別名跟在後面，因為那是「去哪個檔案找它」。</para>
        /// <para>⚠ 標題（`Title`）因此**不在標籤裡** —— 那是刻意的取捨：標題好讀，
        /// 而好讀的字打不進指令。標題仍然在麵包屑與頁面自己的抬頭上。</para>
        /// <para>⚠ 2026-09-22 實測：現有 13 支頁的 key（`doctor`／`bank`／`style`…）
        /// 與型別名（`DoctorPage`…）**13/13 全不相等** ⇒ **相等那一支今天沒有受詞**，
        /// 它是留給未來的。⛔ 所以它必須被合成的受測體驗過，不能只靠這段註解。</para>
        /// </summary>
        public string Label
        {
            get
            {
                if (TypeName.Length == 0) return Key;                      // 探測不到型別（理論上不會）
                return string.Equals(Key, TypeName, StringComparison.Ordinal) ? TypeName
                                                                              : Key + "(" + TypeName + ")";
            }
        }
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
        public IReadOnlyList<string> Diagnostics
        {
            get
            {
                EnsureEntries();
                if (m_AutoDefects.Count == 0) return m_Diagnostics;
                var aAll = new List<string>(m_AutoDefects.Count + m_Diagnostics.Count);
                aAll.AddRange(m_AutoDefects);
                aAll.AddRange(m_Diagnostics);
                return aAll;
            }
        }

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

        // 區塊職責：**自動收頁** —— 掃到的頁直接登記（發現＋建構都走反射）。
        // 物理意義：TASK-0276（Tim 2026-09-22 拍板 C）。此前是「反射只負責發現、建構走顯式登記」，
        //          而那個形狀的代價是**跨 repo 稅**：13 支頁裡 6 支住 SCP_Core，
        //          每加一支都要跑去宿主（Senate.Cli）補一行 Register、再 bump 一次父層指標。
        // 🔴 判準是**繼承 <see cref="SCP_GuiPage"/>**（Tim 逐字：「不應該是看建構子」）——
        //   ⛔ 建構子形狀不是**篩選**條件，它是**建構**的事。兩個問題分開：
        //     「要收誰」＝繼承 ＋ 沒貼 <see cref="SCP_PageIgnoreAttribute"/>；
        //     「怎麼生」＝下面那條 ctor 解析。
        //
        // ⚠ 為什麼這裡要把 context 遞進去，而 UCL 那套不必：
        //   UCL 的 `UCL_EditorMenuPage` 走 `Activator.CreateInstance(t)`（**無參**）——
        //   它的頁面出生時不需要人餵東西，因為資料自己去全域拿。
        //   而本層刻意**沒有**那個全域（D13 ①-1 逐字：「沒有 `Ins` 單例…留著 singleton 的症狀
        //   不是崩潰，是開第二個視窗之後兩邊互相蓋」）⇒ 同一套反射搬過來會缺一塊。
        //   ⇒ 補法是**生的時候遞給它**，⛔ 不是把頁面改成「先生出來再塞」（那會長出
        //     一個新的失效態：還沒塞就被畫，而它的症狀是空白頁或 NullRef，不是紅字）。
        //
        // 🩸 Tim 對「漏一頁／開不起來」的判準（2026-09-22 逐字）：
        //   「**後台是我在使用的操作介面，有漏頁面或開不起來我會發現。**」
        //   ⇒ 所以本層**不另造**紅字機制、不為探針誤入選單加第二道防線 ——
        //     偵測器在場，而它是零延遲的那種。缺陷落進既有的 <see cref="SCP_GuiPageCatalog.Diagnostics"/>
        //     由入口頁畫出來就夠。
        //   ⚠ 而**這個前提會過期**：哪天後台多一個使用者、或它被塞進 CI，這一段要重新評估。
        // 數值影響：純反射 ＋ 一次 ctor 選擇，零 IO。assembly 清單由**呼叫端傳進來** ——
        //          本層不呼叫 `AppDomain.CurrentDomain.GetAssemblies()`：.NET 是用到才載，
        //          「現在載了哪些」會隨執行路徑變，而那個差異不報錯。
        /// <summary>
        /// 掃 <paramref name="iAssemblies"/> 裡所有非抽象的 <see cref="SCP_GuiPage"/> 子類並登記。
        /// <para>⛔ 貼了 <see cref="SCP_PageIgnoreAttribute"/> 的**連建構都不會發生**
        /// （那個標記此前只擋「漏登記」警示，2026-09-22 起也擋收錄）。</para>
        /// <para>已經被 <see cref="Register"/> 顯式登記過的 key 會被跳過（首頁那種吃 catalog 的雞生蛋頁）。</para>
        /// </summary>
        /// <param name="iContext">頁面建構子要吃的東西（宿主的 model／context）。<c>null</c> ＝ 只收無參 ctor 的頁。</param>
        /// <returns>
        /// **缺陷清單**（每一筆都該被看見）：沒有 `PageKey`／key 撞名／ctor 形狀不符／assembly 掃不全。
        /// 空清單 ＝ 這幾顆 assembly 裡的頁全部收得進來。
        /// </returns>
        public List<string> AutoRegister(IEnumerable<Assembly> iAssemblies, object? iContext)
        {
            var aDefects = new List<string>();
            if (iAssemblies == null) return aDefects;

            var aTypeList = new List<Type?>();
            foreach (Assembly aAsm in iAssemblies)
            {
                // 抓不到 type 是常態（dynamic／缺相依）—— 但**要說出來**：
                //   「這個 assembly 我沒掃到」與「這個 assembly 裡沒有頁」是兩件事。
                try { aTypeList.AddRange(aAsm.GetTypes()); }
                catch (ReflectionTypeLoadException e)
                {
                    aDefects.Add($"assembly `{aAsm.GetName().Name}` 只掃得到一部分（{e.Message}）—— 這不是「裡面沒有頁」");
                    if (e.Types != null) aTypeList.AddRange(e.Types);
                }
                catch (Exception e)
                {
                    aDefects.Add($"assembly `{aAsm.GetName().Name}` 掃不了（{e.GetType().Name}）—— 這不是「裡面沒有頁」");
                }
            }

            aDefects.AddRange(AutoRegisterTypes(aTypeList, iContext));
            aDefects.Sort(StringComparer.Ordinal);
            m_AutoDefects.Clear();
            m_AutoDefects.AddRange(aDefects);
            return aDefects;
        }

        /// <summary>
        /// <see cref="AutoRegister"/> 的本體 —— 吃**型別清單**而不是 assembly。
        /// <para>🩸 為什麼要有這一層：上面那支的受測體是「整顆 assembly」，
        /// 而「key 撞名」「ctor 形狀不符」「`Key == TypeName` 的標籤」這三格
        /// **在產品型別上造不出來**（造得出來就代表產品壞了）⇒ 對拍需要合成的受測體。
        /// ⇒ 所以它收 <paramref name="iIncludeIgnored"/>：對拍用的探針頁都貼著
        /// <see cref="SCP_PageIgnoreAttribute"/>（產品掃描因此看不到它們），
        /// 而對拍要**顯式**把它們放進來。</para>
        /// <para>⚠ 那個參數只有自我對拍會給 <c>true</c>；產品路徑永遠是預設的 <c>false</c>。
        /// ⛔ 它不是「彈性」，它是**讓那三格量得到**的唯一辦法 ——
        /// 一個量不到的不變量跟一個壞掉的不變量在計數上同形。</para>
        /// </summary>
        public List<string> AutoRegisterTypes(IEnumerable<Type?> iTypes, object? iContext,
                                              bool iIncludeIgnored = false)
        {
            var aDefects = new List<string>();
            if (iTypes == null) return aDefects;

            // key → 已經佔住它的 TypeFullName。⭐ 身分是 FullName，所以撞名時兩邊都點得出名字
            //   （⛔ 舊形狀只知道「這個 key 重複了」，說不出是誰跟誰）。
            var aOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, Func<SCP_GuiPage>> kv in m_Factories)
                aOwner[kv.Key] = ExplicitOwner;

            Type aBase = typeof(SCP_GuiPage);
            {
                foreach (Type? aType in iTypes)
                {
                    if (aType == null || aType.IsAbstract || !aBase.IsAssignableFrom(aType)) continue;
                    // 顯式排除（測試探針之類）—— 排除要看得見，⛔ 不靠命名慣例猜。
                    //   ⭐ 2026-09-22 起這個標記**連建構都擋**（此前只擋「漏登記」警示）。
                    if (!iIncludeIgnored && aType.IsDefined(typeof(SCP_PageIgnoreAttribute), false)) continue;

                    string aFull = aType.FullName ?? aType.Name;

                    FieldInfo? aField = aType.GetField("PageKey", BindingFlags.Public | BindingFlags.Static);
                    if (aField == null || aField.FieldType != typeof(string) || !aField.IsLiteral)
                    {
                        aDefects.Add($"`{aFull}` 沒有 `public const string PageKey` —— 收不進目錄"
                                     + "（請補上，或貼 `[SCP_PageIgnore(\"理由\")]` 表態它不是給人開的頁）");
                        continue;
                    }
                    string? aKey = aField.GetRawConstantValue() as string;
                    if (string.IsNullOrEmpty(aKey))
                    {
                        aDefects.Add($"`{aFull}` 的 `PageKey` 是空字串");
                        continue;
                    }

                    // 🔴 key 撞名：身分是 FullName ⇒ 兩邊都點名。⛔ 不靠掃描順序決定誰贏 ——
                    //   那會讓 `--page <key>` 與 session 的 `nav` 變成「有時候開到另一頁」。
                    if (aOwner.TryGetValue(aKey!, out string aPrev))
                    {
                        if (aPrev == ExplicitOwner) continue;   // 顯式的優先（首頁那種），不是缺陷
                        aDefects.Add($"page key `{aKey}` 被兩個型別宣告：`{aPrev}` 與 `{aFull}`"
                                     + " —— key 是 `--page` 與 session `nav` 吃的那個字，"
                                     + "重複的症狀是**同一行指令有時開到另一頁**。改掉其中一個。");
                        continue;
                    }

                    ConstructorInfo? aCtor = PickCtor(aType, iContext);
                    if (aCtor == null)
                    {
                        aDefects.Add($"`{aFull}`（key `{aKey}`）的建構子形狀不符 —— "
                                     + "要嘛無參、要嘛吃一個宿主 context"
                                     + (iContext == null ? "（本次沒有給 context）" : $"（可給的是 `{iContext.GetType().Name}`）")
                                     + "；其餘形狀請自己 `Register` 一行。");
                        continue;
                    }

                    object?[] aArgs = aCtor.GetParameters().Length == 0
                        ? new object?[0] : new object?[] { iContext };
                    ConstructorInfo aC = aCtor;   // 閉包抓區域副本，⛔ 不抓迴圈變數
                    aOwner[aKey!] = aFull;
                    Register(aKey!, () => (SCP_GuiPage)aC.Invoke(aArgs));
                }
            }

            aDefects.Sort(StringComparer.Ordinal);
            return aDefects;
        }

        /// <summary>顯式登記過的 key 在擁有者表裡的標記 —— ⛔ 不可能是任何型別的 FullName。</summary>
        const string ExplicitOwner = "(顯式登記)";

        // ⭐ 缺陷併進 Diagnostics ⇒ 入口頁**已經在畫它**，本層不另開第二個落點。
        readonly List<string> m_AutoDefects = new List<string>();

        // 區塊職責：挑一個建得出來的 ctor。⚠ 只認兩種形狀，**刻意不做通用注入**：
        //   多認一種形狀，「形狀不符」就少一格擋人的能力，而那格是本支唯一的守衛。
        static ConstructorInfo? PickCtor(Type iType, object? iContext)
        {
            ConstructorInfo? aNoArg = null;
            if (iContext != null)
            {
                foreach (ConstructorInfo c in iType.GetConstructors())
                {
                    ParameterInfo[] ps = c.GetParameters();
                    if (ps.Length == 1 && ps[0].ParameterType.IsInstanceOfType(iContext)) return c;
                    if (ps.Length == 0) aNoArg = c;
                }
                return aNoArg;
            }
            foreach (ConstructorInfo c in iType.GetConstructors())
                if (c.GetParameters().Length == 0) return c;
            return null;
        }

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

                Type aType = aProbe.GetType();
                aList.Add(new SCP_GuiPageEntry(aKey, aProbe.Title, aGroup, aType.Name, aType.FullName ?? aType.Name));
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
