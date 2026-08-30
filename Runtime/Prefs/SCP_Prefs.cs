// 區塊職責：**專案層設定的唯一落點** —— 一個 JSON 檔，各消費端各佔一個頂層 section。
// 物理意義：概念取自 Unity 的 PlayerPrefs（per-project scope 的 key-value），但**刻意不抄它的語意**。
//           PlayerPrefs 有三個病，而這三個正好是本系統最貴的錯誤形狀：
//             ① key 打錯回預設值 ⇒「打錯」與「沒設定過」同形，兩者都不報錯
//             ② 型別靠呼叫端記得 ⇒ 同一 key 兩處讀不同型別，兩邊都不報錯
//             ③ 沒有「未設定」態 ⇒「沒設定」與「設定成剛好等於預設值」壓成一態
//           ⇒ 本層的 scalar 讀取一律回 <see cref="SCP_PrefRead{T}"/> 三態；
//             想要「讀不到就用預設」必須**顯式**呼叫 Get（那是一個決定，不是預設行為）。
// 數值影響：一次讀寫是一次整檔 parse ＋ atomic replace。⚠ 檔案路徑由呼叫端**傳進來**，
//           本層不推導 —— 路徑不該被推導，該被傳遞（推導出來的路徑會靜默命中另一棵資料樹）。
//           寫入保留**未知 section 與未知欄位**，並在寫完**回讀驗證**。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）——
//   所以這裡**不能用** `Environment.ProcessId`（.NET 5+）。暫存檔後綴走 Guid。
// 規範：<SCP_Core>/Docs~/Coding_Standards.md §3。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Prefs
{
    /// <summary>一次讀取的三種結果 —— **不得壓成兩態**。</summary>
    public enum SCP_PrefState
    {
        /// <summary>檔在、section 在，但沒有這個 key（或整份還沒存過）。**這不是錯誤。**</summary>
        Missing = 0,

        /// <summary>讀到了，<c>Value</c> 有意義。</summary>
        Present = 1,

        /// <summary>檔壞了／型別不符／IO 失敗。<c>Error</c> 有話說。**跟 Missing 不同形。**</summary>
        ReadError = 2,
    }

    /// <summary>一次 scalar 讀取的結果。<c>Value</c> 只有 <see cref="SCP_PrefState.Present"/> 時有意義。</summary>
    public readonly struct SCP_PrefRead<T>
    {
        public SCP_PrefRead(SCP_PrefState iState, T iValue, string? iError)
        {
            State = iState;
            Value = iValue;
            Error = iError;
        }

        public SCP_PrefState State { get; }

        /// <summary>⚠ 只有 <c>State == Present</c> 才可以用；其餘情況它是型別預設值，**不是設定值**。</summary>
        public T Value { get; }

        /// <summary>只有 <c>State == ReadError</c> 才非 null。</summary>
        public string? Error { get; }

        public bool IsPresent { get { return State == SCP_PrefState.Present; } }

        public static SCP_PrefRead<T> Present(T iValue) { return new SCP_PrefRead<T>(SCP_PrefState.Present, iValue, null); }
        public static SCP_PrefRead<T> Missing() { return new SCP_PrefRead<T>(SCP_PrefState.Missing, default!, null); }
        public static SCP_PrefRead<T> Failed(string iWhy) { return new SCP_PrefRead<T>(SCP_PrefState.ReadError, default!, iWhy); }
    }

    /// <summary>
    /// 一把有型別的 key。
    /// <para>⚠ **不要在呼叫點打字串**。key 要宣告成 <c>static readonly</c> 常數並共用 ——
    /// 打錯字的症狀是「永遠讀回預設值」，而那長得跟「這個設定沒設過」一模一樣。</para>
    /// <code>
    /// static readonly SCP_PrefKey&lt;string&gt; LettersRoot = SCP_PrefKey.String("awakening", "lettersRoot", "");
    /// </code>
    /// </summary>
    public sealed class SCP_PrefKey<T>
    {
        internal SCP_PrefKey(string iSection, string iName, T iDefault)
        {
            if (string.IsNullOrEmpty(iSection)) throw new ArgumentException("section 不可以是空字串", nameof(iSection));
            if (string.IsNullOrEmpty(iName)) throw new ArgumentException("key 名不可以是空字串", nameof(iName));
            Section = iSection;
            Name = iName;
            Default = iDefault;
        }

        /// <summary>頂層區塊名（一個子系統／一頁佔一個）。</summary>
        public string Section { get; }

        /// <summary>區塊內的 key 名。</summary>
        public string Name { get; }

        /// <summary>
        /// 讀不到時的落點。⚠ 它**只在顯式呼叫 <c>Get</c> 時**生效 ——
        /// <c>Read</c> 不會偷偷用它（那會把三態壓回兩態）。
        /// </summary>
        public T Default { get; }

        /// <summary>診斷用的完整路徑（錯誤訊息一律帶它，否則「哪個 key 錯了」要用猜的）。</summary>
        public string Path { get { return Section + "." + Name; } }

        public override string ToString() { return Path; }
    }

    /// <summary>typed key 的建構入口（只開放本層支援的四種 scalar）。</summary>
    public static class SCP_PrefKey
    {
        public static SCP_PrefKey<string> String(string iSection, string iName, string iDefault = "")
        { return new SCP_PrefKey<string>(iSection, iName, iDefault); }

        public static SCP_PrefKey<long> Long(string iSection, string iName, long iDefault = 0)
        { return new SCP_PrefKey<long>(iSection, iName, iDefault); }

        public static SCP_PrefKey<bool> Bool(string iSection, string iName, bool iDefault = false)
        { return new SCP_PrefKey<bool>(iSection, iName, iDefault); }

        public static SCP_PrefKey<double> Double(string iSection, string iName, double iDefault = 0d)
        { return new SCP_PrefKey<double>(iSection, iName, iDefault); }
    }

    /// <summary>
    /// 消費端看到的介面 —— **功能碼只認得它，不認得設定檔的檔名、路徑與結構**。
    /// <para>這是「頁面搬得動」的前提：頁面一旦知道自己讀的是 <c>senate.local.json</c>，
    /// 它就只能活在有那個檔的宿主裡。</para>
    /// </summary>
    public interface ISCP_Prefs
    {
        SCP_PrefRead<string> Read(SCP_PrefKey<string> iKey);
        SCP_PrefRead<long> Read(SCP_PrefKey<long> iKey);
        SCP_PrefRead<bool> Read(SCP_PrefKey<bool> iKey);
        SCP_PrefRead<double> Read(SCP_PrefKey<double> iKey);

        /// <summary>讀不到／讀壞就用 <see cref="SCP_PrefKey{T}.Default"/>。**顯式接受預設值**時才用它。</summary>
        string Get(SCP_PrefKey<string> iKey);
        long Get(SCP_PrefKey<long> iKey);
        bool Get(SCP_PrefKey<bool> iKey);
        double Get(SCP_PrefKey<double> iKey);

        /// <summary>寫一個 key。回（成功, 人可讀的說法）—— **失敗一定有話說**。</summary>
        (bool Ok, string Message) Write(SCP_PrefKey<string> iKey, string iValue);
        (bool Ok, string Message) Write(SCP_PrefKey<long> iKey, long iValue);
        (bool Ok, string Message) Write(SCP_PrefKey<bool> iKey, bool iValue);
        (bool Ok, string Message) Write(SCP_PrefKey<double> iKey, double iValue);

        /// <summary>讀整個 section 成 typed model。回 null ＝ **沒存過**（不是錯誤）；壞掉會走 <c>iWarn</c>。</summary>
        T? LoadSection<T>(string iSection, Action<string>? iWarn = null) where T : class;

        /// <summary>寫整個 section（**其他 section 原樣保留**）。</summary>
        (bool Ok, string Message) SaveSection(string iSection, object iSettings);
    }

    /// <summary>
    /// 檔案背書的實作：一個 JSON 檔、各 section 一個頂層 key。
    /// <para>⚠ 路徑由呼叫端傳入。本類別**不推導路徑**，也不知道自己叫什麼名字。</para>
    /// <para>🩸 為什麼寫入端要這麼囉唆（整份讀回 → 換一格 → atomic replace → 回讀）：
    /// 直接 <c>WriteAllText</c> 覆蓋會把**別人的 section** 一起帶走，而那個檔案還是合法 JSON、
    /// 還是讀得起來 —— 沒有任何一層會報錯。</para>
    /// </summary>
    public sealed class SCP_JsonPrefs : ISCP_Prefs
    {
        readonly string m_Path;

        /// <param name="iPath">設定檔絕對路徑（由宿主決定；本層不推導）。</param>
        public SCP_JsonPrefs(string iPath)
        {
            if (string.IsNullOrEmpty(iPath)) throw new ArgumentException("prefs 路徑不可以是空字串", nameof(iPath));
            m_Path = iPath;
        }

        /// <summary>設定檔在哪（診斷用；錯誤訊息與後台頁要顯示它）。</summary>
        public string FilePath { get { return m_Path; } }

        // ---------------------------------------------------------------- 讀

        public SCP_PrefRead<string> Read(SCP_PrefKey<string> iKey)
        { return ReadScalar(iKey, aNode => aNode.AsString()); }

        public SCP_PrefRead<long> Read(SCP_PrefKey<long> iKey)
        { return ReadScalar(iKey, aNode => aNode.AsLong()); }

        public SCP_PrefRead<bool> Read(SCP_PrefKey<bool> iKey)
        { return ReadScalar(iKey, aNode => aNode.AsBool()); }

        public SCP_PrefRead<double> Read(SCP_PrefKey<double> iKey)
        { return ReadScalar(iKey, aNode => aNode.AsDouble()); }

        public string Get(SCP_PrefKey<string> iKey) { var r = Read(iKey); return r.IsPresent ? r.Value : iKey.Default; }
        public long Get(SCP_PrefKey<long> iKey) { var r = Read(iKey); return r.IsPresent ? r.Value : iKey.Default; }
        public bool Get(SCP_PrefKey<bool> iKey) { var r = Read(iKey); return r.IsPresent ? r.Value : iKey.Default; }
        public double Get(SCP_PrefKey<double> iKey) { var r = Read(iKey); return r.IsPresent ? r.Value : iKey.Default; }

        // 區塊職責：scalar 讀取的共用骨架 —— 三態全部在這裡決定，四個型別不各判一次。
        // 物理意義: 型別不符走 ReadError **不走 Missing** —— 「這個 key 存的是別的型別」是設定寫錯了，
        //          而把它讀成「沒設定」會讓人以為只要補上就好，實際上補上會被舊值蓋掉。
        SCP_PrefRead<T> ReadScalar<T>(SCP_PrefKey<T> iKey, Func<SCP_JsonData, T> iConvert)
        {
            if (!File.Exists(m_Path)) return SCP_PrefRead<T>.Missing();

            SCP_JsonData aRoot;
            try { aRoot = SCP_JsonParser.Parse(File.ReadAllText(m_Path, Encoding.UTF8)); }
            catch (Exception e)
            { return SCP_PrefRead<T>.Failed($"{System.IO.Path.GetFileName(m_Path)} 讀不了（{iKey.Path}）：{e.Message}"); }

            if (!aRoot.Contains(iKey.Section)) return SCP_PrefRead<T>.Missing();
            SCP_JsonData aSection = aRoot[iKey.Section];
            if (!aSection.Contains(iKey.Name)) return SCP_PrefRead<T>.Missing();

            try { return SCP_PrefRead<T>.Present(iConvert(aSection[iKey.Name])); }
            catch (Exception e)
            { return SCP_PrefRead<T>.Failed($"{iKey.Path} 的型別不是 {typeof(T).Name}：{e.Message}"); }
        }

        public T? LoadSection<T>(string iSection, Action<string>? iWarn = null) where T : class
        {
            if (string.IsNullOrEmpty(iSection)) throw new ArgumentException("section 不可以是空字串", nameof(iSection));
            if (!File.Exists(m_Path)) return null;

            SCP_JsonData aRoot;
            try { aRoot = SCP_JsonParser.Parse(File.ReadAllText(m_Path, Encoding.UTF8)); }
            catch (Exception e)
            {
                if (iWarn != null) iWarn($"{System.IO.Path.GetFileName(m_Path)} 讀不了（沒有被覆寫）：{e.Message}");
                return null;
            }
            if (!aRoot.Contains(iSection)) return null;
            try { return SCP_JsonMapper.Create(typeof(T), aRoot[iSection]) as T; }
            catch (Exception e)
            {
                if (iWarn != null) iWarn($"'{iSection}' 區塊對不上 {typeof(T).Name}（沒有被覆寫）：{e.Message}");
                return null;
            }
        }

        // ---------------------------------------------------------------- 寫

        public (bool Ok, string Message) Write(SCP_PrefKey<string> iKey, string iValue)
        { return WriteScalar(iKey, SCP_JsonData.NewString(iValue ?? "")); }

        public (bool Ok, string Message) Write(SCP_PrefKey<long> iKey, long iValue)
        { return WriteScalar(iKey, SCP_JsonData.NewNumber(iValue)); }

        public (bool Ok, string Message) Write(SCP_PrefKey<bool> iKey, bool iValue)
        { return WriteScalar(iKey, SCP_JsonData.NewBool(iValue)); }

        public (bool Ok, string Message) Write(SCP_PrefKey<double> iKey, double iValue)
        { return WriteScalar(iKey, SCP_JsonData.NewNumber(iValue)); }

        (bool Ok, string Message) WriteScalar<T>(SCP_PrefKey<T> iKey, SCP_JsonData iValue)
        {
            return Mutate(aRoot =>
            {
                SCP_JsonData aSection = aRoot.Contains(iKey.Section) ? aRoot[iKey.Section] : SCP_JsonData.NewObject();
                aSection.Set(iKey.Name, iValue);
                aRoot.Set(iKey.Section, aSection);
                return iKey.Section;
            }, iKey.Path);
        }

        public (bool Ok, string Message) SaveSection(string iSection, object iSettings)
        {
            if (string.IsNullOrEmpty(iSection)) throw new ArgumentException("section 不可以是空字串", nameof(iSection));
            return Mutate(aRoot =>
            {
                aRoot.Set(iSection, SCP_JsonMapper.ToJson(iSettings));
                return iSection;
            }, iSection);
        }

        // 區塊職責：所有寫入的唯一通道 —— 讀整份 → 交給呼叫端換一格 → atomic replace → 回讀。
        // 物理意義: 「整份壞掉時不硬寫」是刻意的：蓋掉會把**別人的 section** 一起帶走，
        //          而那不是本次要救的錯。⇒ 停手並說出來，讓人自己決定修還是刪。
        // 數值影響: 暫存檔後綴用 Guid（不是 pid）—— `Environment.ProcessId` 是 .NET 5+，
        //          netstandard2.1 沒有，Unity 那側會編不過。
        (bool Ok, string Message) Mutate(Func<SCP_JsonData, string> iMutate, string iWhatForMessage)
        {
            SCP_JsonData aRoot = SCP_JsonData.NewObject();
            if (File.Exists(m_Path))
            {
                try { aRoot = SCP_JsonParser.Parse(File.ReadAllText(m_Path, Encoding.UTF8)); }
                catch (Exception e)
                { return (false, $"{System.IO.Path.GetFileName(m_Path)} 壞了，沒有覆寫（先修它或刪掉重存）：{e.Message}"); }
            }

            string aSectionName;
            try { aSectionName = iMutate(aRoot); }
            catch (Exception e) { return (false, $"組不出要寫的內容（{iWhatForMessage}）：{e.Message}"); }

            string aTmp = m_Path + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                string? aDir = System.IO.Path.GetDirectoryName(m_Path);
                if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir!);
                File.WriteAllText(aTmp, aRoot.ToJson(iIndented: true) + "\n", new UTF8Encoding(false));
                // ⚠ 方言：`File.Move(src, dst, overwrite)` 是 .NET Core 3.0+，**netstandard2.1 沒有**
                //   （2026-08-30 實測 `CS1501: No overload for method 'Move' takes 3 arguments`）。
                //   Senate.Core 那側原本能這樣寫是因為它 target net10.0 —— 搬進共用碼就編不過了。
                //   ⇒ 目標存在走 File.Replace（同目錄、原子替換）；不存在才走兩參數 Move。
                //   ⛔ 不可以退化成「先 Delete 再 Move」—— 那中間有一格「檔案不存在」，
                //     而那一格長得跟「這個設定還沒存過」一模一樣。
                if (File.Exists(m_Path)) File.Replace(aTmp, m_Path, null);
                else File.Move(aTmp, m_Path);
            }
            catch (Exception e)
            {
                try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch { /* 殘檔清不掉不蓋真錯 */ }
                return (false, $"寫檔失敗（{iWhatForMessage}）：{e.GetType().Name}: {e.Message}");
            }

            // 回讀驗證 —— **寫入端會替自己說謊**。section 在就算數；逐欄對拍歸 selftest。
            try
            {
                SCP_JsonData aBack = SCP_JsonParser.Parse(File.ReadAllText(m_Path, Encoding.UTF8));
                if (!aBack.Contains(aSectionName))
                    return (false, $"寫進去了但回讀找不到 '{aSectionName}' 區塊 —— 有第二個寫入者？");
            }
            catch (Exception e) { return (false, $"寫完之後回讀失敗（檔案可能壞了）：{e.Message}"); }

            return (true, $"✓ 已存進 {System.IO.Path.GetFileName(m_Path)}（{iWhatForMessage}）");
        }

        /// <summary>目前檔裡有哪些 section（診斷／後台頁用）。檔不在或壞掉回空清單並走 <paramref name="iWarn"/>。</summary>
        public IReadOnlyList<string> Sections(Action<string>? iWarn = null)
        {
            var aList = new List<string>();
            if (!File.Exists(m_Path)) return aList;
            try
            {
                SCP_JsonData aRoot = SCP_JsonParser.Parse(File.ReadAllText(m_Path, Encoding.UTF8));
                foreach (string aKey in aRoot.Keys) aList.Add(aKey);   // Keys 保留插入順序
            }
            catch (Exception e) { if (iWarn != null) iWarn($"列 section 失敗：{e.Message}"); }
            return aList;
        }
    }
}
