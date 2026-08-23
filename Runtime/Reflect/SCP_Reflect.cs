// 區塊職責：反射結果的**快取層** —— 型別掃描、名稱 → 型別、型別 → 成員描述、字串 → 值。
// 物理意義：反射本身不貴，貴的是**每次都重做一遍**：`GetTypes()` 掃全部 assembly、
//           `GetFields()` 每幀跑一次（自動繪製是 immediate mode，每幀都要成員清單）。
//           ⇒ 算一次、記起來。這一層是唯一的入口，別的地方不要自己 new SCP_TypeSchema。
// 數值影響：純記憶體快取，零 IO。第一次呼叫掃描 assembly（數十毫秒等級），之後 O(1)。
// ⚠ 快取要能清（<see cref="SCP_Reflect.ClearCache"/>）—— Unity 那側熱重載後舊 Type 物件會失效，
//   而症狀不是崩潰，是「我改了欄位，畫面上沒有」。
// ⚠ 名稱撞名時**回全部並讓呼叫端決定**：短名在跨 assembly 一定會撞，
//   自動挑第一個的症狀是「它讀寫的是另一個同名型別」而且不報錯。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace SCP.Core.Reflect
{
    public static class SCP_Reflect
    {
        static readonly object s_Lock = new object();
        static readonly Dictionary<Type, SCP_TypeSchema> s_Schemas = new Dictionary<Type, SCP_TypeSchema>();

        static List<Type>? s_AllTypes;
        static Dictionary<string, Type>? s_ByFullName;
        static Dictionary<string, List<Type>>? s_ByShortName;

        // ── 成員描述 ──────────────────────────────────────────────
        /// <summary>型別的成員描述（快取）。同一型別只算一次。</summary>
        public static SCP_TypeSchema SchemaOf(Type iType)
        {
            if (iType == null) throw new ArgumentNullException(nameof(iType));
            lock (s_Lock)
            {
                if (s_Schemas.TryGetValue(iType, out SCP_TypeSchema? aHit)) return aHit;
                var aSchema = new SCP_TypeSchema(iType);
                s_Schemas[iType] = aSchema;
                return aSchema;
            }
        }

        public static SCP_TypeSchema SchemaOf(object iObj)
            => SchemaOf((iObj ?? throw new ArgumentNullException(nameof(iObj))).GetType());

        /// <summary>種類判定（不含成員展開；內部走 <see cref="SCP_TypeSchema.Classify"/>）。</summary>
        public static SCP_ValueKind KindOf(Type iType) => SCP_TypeSchema.Classify(iType, out _, out _);

        // ── 型別掃描 ──────────────────────────────────────────────
        /// <summary>目前載入的所有型別（快取）。⚠ 有些 assembly 會部分載入失敗 —— 拿得到的先拿，並且不吞掉那件事。</summary>
        public static IReadOnlyList<Type> AllTypes(Action<string>? iWarn = null)
        {
            lock (s_Lock)
            {
                if (s_AllTypes != null) return s_AllTypes;

                var aTypes = new List<Type>();
                foreach (Assembly aAsm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try { aTypes.AddRange(aAsm.GetTypes()); }
                    catch (ReflectionTypeLoadException e)
                    {
                        // 部分型別載不起來時**照樣收得到的那些**，但要說出來：
                        // 靜默少幾個型別會變成「那個型別明明存在卻找不到」
                        foreach (Type? t in e.Types) if (t != null) aTypes.Add(t);
                        iWarn?.Invoke($"assembly {aAsm.GetName().Name} 有型別載入失敗（收到 {aTypes.Count} 個）：{e.Message}");
                    }
                    catch (Exception e)
                    {
                        iWarn?.Invoke($"assembly {aAsm.GetName().Name} 掃描失敗：{e.GetType().Name}: {e.Message}");
                    }
                }
                s_AllTypes = aTypes;
                return s_AllTypes;
            }
        }

        /// <summary>FullName 精準命中；找不到回 null（**不要退到短名** —— 那是另一個問題，走 ResolveTypes）。</summary>
        public static Type? TypeByFullName(string iFullName)
        {
            if (string.IsNullOrEmpty(iFullName)) return null;
            EnsureNameMaps();
            lock (s_Lock)
            {
                return s_ByFullName!.TryGetValue(iFullName, out Type? aHit) ? aHit : null;
            }
        }

        /// <summary>
        /// 依名稱（FullName 或短名）找型別 —— **回全部命中**。
        /// <para>短名跨 assembly 撞名是常態；自動挑第一個的症狀是「它讀寫的是另一個同名型別」，
        /// 而那不會報錯。⇒ 撞名時由呼叫端決定（或請使用者給 FullName）。</para>
        /// </summary>
        public static IReadOnlyList<Type> ResolveTypes(string iName)
        {
            if (string.IsNullOrEmpty(iName)) return Array.Empty<Type>();

            Type? aExact = TypeByFullName(iName);
            if (aExact != null) return new[] { aExact };

            EnsureNameMaps();
            lock (s_Lock)
            {
                return s_ByShortName!.TryGetValue(iName, out List<Type>? aList)
                    ? (IReadOnlyList<Type>)aList
                    : Array.Empty<Type>();
            }
        }

        static void EnsureNameMaps()
        {
            lock (s_Lock)
            {
                if (s_ByFullName != null && s_ByShortName != null) return;
            }
            IReadOnlyList<Type> aAll = AllTypes();
            lock (s_Lock)
            {
                if (s_ByFullName != null && s_ByShortName != null) return;
                var aFull = new Dictionary<string, Type>();
                var aShort = new Dictionary<string, List<Type>>();
                foreach (Type t in aAll)
                {
                    string? aFullName = t.FullName;
                    // 同一個 FullName 出現兩次（同名 assembly 載兩份）⇒ 保留先來的，不覆寫
                    if (aFullName != null && !aFull.ContainsKey(aFullName)) aFull[aFullName] = t;

                    if (!aShort.TryGetValue(t.Name, out List<Type>? aBucket))
                    {
                        aBucket = new List<Type>();
                        aShort[t.Name] = aBucket;
                    }
                    aBucket.Add(t);
                }
                s_ByFullName = aFull;
                s_ByShortName = aShort;
            }
        }

        /// <summary>清掉所有快取（Unity 熱重載後、或動態載入 assembly 之後要叫）。</summary>
        public static void ClearCache()
        {
            lock (s_Lock)
            {
                s_Schemas.Clear();
                s_AllTypes = null;
                s_ByFullName = null;
                s_ByShortName = null;
            }
        }

        /// <summary>快取現況（給 doctor／診斷頁印出來 —— 快取這種東西沒有讀數就只能靠感覺）。</summary>
        public static string Describe()
        {
            lock (s_Lock)
            {
                return string.Format(CultureInfo.InvariantCulture,
                    "型別快取：schema {0} 筆／型別 {1}（{2}）",
                    s_Schemas.Count,
                    s_AllTypes == null ? 0 : s_AllTypes.Count,
                    s_AllTypes == null ? "尚未掃描" : "已掃描");
            }
        }

        // ── 字串 → 值 ─────────────────────────────────────────────
        /// <summary>
        /// 把使用者／設定檔給的字串轉成目標型別的值。
        /// <para>⚠ 失敗回 false ＋ 人可讀原因，**不丟例外也不回預設值** ——
        /// 「打錯字」與「他就是要 0」不得同形。</para>
        /// <para>iAllowNullText：空字串／`null` 字面值視為 null（Nullable 與參考型別用）。</para>
        /// </summary>
        public static bool TryParse(Type iType, string? iText, bool iAllowNullText,
                                    out object? oValue, out string oError)
        {
            oValue = null;
            oError = "";
            if (iType == null) { oError = "目標型別是 null"; return false; }

            bool aLooksNull = iText == null || iText.Length == 0
                              || string.Equals(iText, "null", StringComparison.OrdinalIgnoreCase);
            if (aLooksNull)
            {
                if (iAllowNullText) return true;                  // oValue = null
                oError = "空值 —— 這個欄位不接受 null（給 0 或明確的值）";
                return false;
            }

            string aText = iText!.Trim();
            try
            {
                if (iType == typeof(string)) { oValue = iText; return true; }

                if (iType == typeof(bool))
                {
                    if (bool.TryParse(aText, out bool b)) { oValue = b; return true; }
                    if (aText == "1") { oValue = true; return true; }
                    if (aText == "0") { oValue = false; return true; }
                    oError = $"'{aText}' 不是 true／false";
                    return false;
                }

                if (iType.IsEnum)
                {
                    foreach (string aName in Enum.GetNames(iType))
                        if (string.Equals(aName, aText, StringComparison.OrdinalIgnoreCase))
                        {
                            oValue = Enum.Parse(iType, aName);
                            return true;
                        }
                    oError = $"'{aText}' 不是 {iType.Name} 的選項（可用：{string.Join("／", Enum.GetNames(iType))}）";
                    return false;
                }

                SCP_ValueKind aKind = KindOf(iType);
                if (aKind == SCP_ValueKind.Integer || aKind == SCP_ValueKind.Decimal)
                {
                    // 走 invariant —— 千分位逗號與地區小數點是「同一份設定在別台機器讀不回來」的經典來源
                    oValue = Convert.ChangeType(aText, iType, CultureInfo.InvariantCulture);
                    return true;
                }

                oError = $"字串轉不成 {iType.Name}（本層只轉 bool／數字／字串／enum）";
                return false;
            }
            catch (Exception e)
            {
                oError = $"'{aText}' 轉成 {iType.Name} 失敗：{e.GetType().Name}";
                return false;
            }
        }

        /// <summary>建一份實例（無參數建構子／值型別）。做不到回 null 並說原因 —— 不要回一個假的空物件。</summary>
        public static object? TryCreate(Type iType, out string oError)
        {
            oError = "";
            if (iType == null) { oError = "型別是 null"; return null; }
            try
            {
                if (iType == typeof(string)) return "";
                return Activator.CreateInstance(iType);
            }
            catch (Exception e)
            {
                oError = $"{iType.Name} 建不起來（要有公開無參數建構子）：{e.GetType().Name}";
                return null;
            }
        }
    }
}
