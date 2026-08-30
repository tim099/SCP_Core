// 區塊職責：把不同 section 導到**不同的背書儲存**，對消費端仍然只是一個 ISCP_Prefs。
// 物理意義：宿主常常不是「一個設定檔」而是好幾個，各有各的政策：
//           Senate 就有兩份 —— `senate.local.json`（這台機器管哪些專案、有樣板、有型別）
//           與 `senate.pages.local.json`（各頁上次調成什麼樣）。
//           ⇒ 「哪個 section 住哪個檔」是**宿主的決定**，不該讓頁面知道。本類別就是那個決定的落點。
//
//           🩸 為什麼不乾脆讓兩邊都用 SCP_JsonPrefs 直接寫：那會讓
//           `senate.local.json` 有**兩個寫入端**（SenateConfig.Save 與 prefs），
//           兩邊的格式化與欄位保留規則各一套 ⇒ 誰後寫誰贏，而檔案兩次都合法、都讀得起來。
//           **一個檔只能有一個寫入端** —— 所以特殊的那個 section 由宿主給一個走既有寫入端的實作。
// 數值影響：純轉發，零 IO（IO 在被導到的那個實作裡）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Prefs
{
    /// <summary>
    /// 依 section 轉發的 prefs。
    /// <code>
    /// var aPrefs = new SCP_RoutedPrefs(aPagesStore);          // 預設落點
    /// aPrefs.Route("awakening", aConfigBackedStore);          // 這一個 section 走別條路
    /// </code>
    /// </summary>
    public sealed class SCP_RoutedPrefs : ISCP_Prefs
    {
        readonly ISCP_Prefs m_Default;
        readonly Dictionary<string, ISCP_Prefs> m_Routes =
            new Dictionary<string, ISCP_Prefs>(StringComparer.Ordinal);

        /// <param name="iDefault">沒被特別指定的 section 落在這裡。</param>
        public SCP_RoutedPrefs(ISCP_Prefs iDefault)
        {
            m_Default = iDefault ?? throw new ArgumentNullException(nameof(iDefault));
        }

        /// <summary>
        /// 指定某個 section 的背書儲存。
        /// <para>⚠ 同一個 section 導兩次 ⇒ 丟例外。那是程式錯誤：值會寫進哪個檔變成看登記順序，
        /// 而它「能跑」。</para>
        /// </summary>
        public SCP_RoutedPrefs Route(string iSection, ISCP_Prefs iPrefs)
        {
            if (string.IsNullOrEmpty(iSection)) throw new ArgumentException("section 不可以是空字串", nameof(iSection));
            if (iPrefs == null) throw new ArgumentNullException(nameof(iPrefs));
            if (m_Routes.ContainsKey(iSection)) throw new InvalidOperationException($"section 重複導向：{iSection}");
            m_Routes.Add(iSection, iPrefs);
            return this;
        }

        ISCP_Prefs For(string iSection)
        {
            ISCP_Prefs aP;
            return m_Routes.TryGetValue(iSection, out aP!) ? aP : m_Default;
        }

        public SCP_PrefRead<string> Read(SCP_PrefKey<string> iKey) { return For(iKey.Section).Read(iKey); }
        public SCP_PrefRead<long> Read(SCP_PrefKey<long> iKey) { return For(iKey.Section).Read(iKey); }
        public SCP_PrefRead<bool> Read(SCP_PrefKey<bool> iKey) { return For(iKey.Section).Read(iKey); }
        public SCP_PrefRead<double> Read(SCP_PrefKey<double> iKey) { return For(iKey.Section).Read(iKey); }

        public string Get(SCP_PrefKey<string> iKey) { return For(iKey.Section).Get(iKey); }
        public long Get(SCP_PrefKey<long> iKey) { return For(iKey.Section).Get(iKey); }
        public bool Get(SCP_PrefKey<bool> iKey) { return For(iKey.Section).Get(iKey); }
        public double Get(SCP_PrefKey<double> iKey) { return For(iKey.Section).Get(iKey); }

        public (bool Ok, string Message) Write(SCP_PrefKey<string> iKey, string iValue)
        { return For(iKey.Section).Write(iKey, iValue); }
        public (bool Ok, string Message) Write(SCP_PrefKey<long> iKey, long iValue)
        { return For(iKey.Section).Write(iKey, iValue); }
        public (bool Ok, string Message) Write(SCP_PrefKey<bool> iKey, bool iValue)
        { return For(iKey.Section).Write(iKey, iValue); }
        public (bool Ok, string Message) Write(SCP_PrefKey<double> iKey, double iValue)
        { return For(iKey.Section).Write(iKey, iValue); }

        public T? LoadSection<T>(string iSection, Action<string>? iWarn = null) where T : class
        { return For(iSection).LoadSection<T>(iSection, iWarn); }

        public (bool Ok, string Message) SaveSection(string iSection, object iSettings)
        { return For(iSection).SaveSection(iSection, iSettings); }
    }
}
