// 區塊職責：一個型別「有哪些成員、每個成員是什麼種類、怎麼讀寫」的**描述**（不含反射掃描本身）。
// 物理意義：⭐ 這一層存在的理由是**兩個消費端共用同一份分類**：
//             · 自動序列化（SCP_JsonMapper）：物件 ↔ SCP_JsonData
//             · 自動繪製（SCP_GuiInspector）：物件 ↔ 畫面
//           兩邊各自判斷「這個成員是數字還是清單」的話，遲早出現「畫得出來但存不進去」
//           （或反過來）—— 而那不會報錯，只會有一個欄位改了之後回不來。
// 數值影響：純描述 ＋ 反射讀寫。零 IO。建構成本由 SCP_Reflect 快取，同一型別只算一次。
// ⚠ **不支援的成員一律留著並帶原因**（Kind = Unsupported），不是從清單裡消失 ——
//   「這個欄位不支援」與「這個欄位不存在」不得同形：後者會讓人以為資料本來就沒有那一格。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）—— 不用 record、不用 expression tree
//   （IL2CPP 上 compiled expression 不可靠，一律走 FieldInfo/PropertyInfo）。
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace SCP.Core.Reflect
{
    /// <summary>
    /// 成員的種類 —— **兩個消費端（JSON／UI）共用的唯一分類**。
    /// 新增一種要同時想「它怎麼存」與「它怎麼畫」，少一邊就是上面那個「畫得出來存不進去」。
    /// </summary>
    public enum SCP_ValueKind
    {
        /// <summary>本層處理不了 —— 帶著原因留在清單裡，不消失。</summary>
        Unsupported = 0,
        Bool,
        /// <summary>整數家族（sbyte…ulong）。</summary>
        Integer,
        /// <summary>浮點／decimal。</summary>
        Decimal,
        Text,
        /// <summary>enum（有限選項）。</summary>
        Choice,
        /// <summary>其他 class / struct ⇒ 往下遞迴。</summary>
        Nested,
        /// <summary>List&lt;T&gt;。</summary>
        ListOf,
        /// <summary>Dictionary&lt;string, T&gt;（key 只支援 string —— JSON 的 key 就是字串）。</summary>
        MapOf,
    }

    /// <summary>掛在欄位／屬性上 ⇒ 兩個消費端都跳過它（不存、不畫）。</summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class SCP_IgnoreAttribute : Attribute { }

    // 區塊職責：**未知鍵的收容所** —— 反序列化時本版不認得的 key 存這裡，序列化時原樣寫回去。
    // 物理意義：沒有這一格的話，「舊版讀了新版寫的檔、再存回去」＝ 新版的欄位**被舊版刪掉**，
    //          而症狀是「一切正常」（我認得的欄位都還在）。
    //          🩸 那不是格式化差異，是**寫入端省略不可逆**：反序列化丟掉的東西，序列化就再也寫不回來。
    //          （血證原文在 <Senate>/src/Senate.Core/SenateConfig.cs —— 本能力就是為了接它，見 D25。）
    // 數值影響：純資料搬運，零 IO。
    // 設計取捨：① 型別**只收 `Dictionary<string, SCP_JsonData>`** —— 收 object 會讓「寫回去的是不是
    //            原來那個東西」變成要靠 mapper 猜，而猜錯不報錯。
    //          ② 收容所**不進 `Members`**（照掛 [SCP_Ignore]，GUI 不畫它）；mapper 另外用
    //            `SCP_TypeSchema.ExtensionData` 取。⇒ 「存得回去」與「畫得出來」刻意分開。
    //          ③ 一個型別最多一個 —— 兩個的話「未知鍵進哪一個」沒有答案，
    //            而任何一種挑法都會在某一次靜默丟掉另一邊。
    /// <summary>
    /// 掛在 <c>Dictionary&lt;string, SCP_JsonData&gt;</c> 的欄位／屬性上 ⇒
    /// 反序列化時**沒有對應成員的 key** 收進來，序列化時再寫回去（本版不認得的欄位不會被吃掉）。
    /// <para>⚠ 一個型別最多一個；型別不符或掛超過一個 ⇒ **不啟用**，並由 mapper 在 Diagnostics 記一筆
    /// （⛔ 不靜默降級 —— 靜默的失效樣子就是「使用者的設定檔少一塊」）。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property, Inherited = true)]
    public sealed class SCP_JsonExtensionDataAttribute : Attribute { }

    /// <summary>一個成員的描述。</summary>
    public sealed class SCP_MemberSchema
    {
        readonly FieldInfo? m_Field;
        readonly PropertyInfo? m_Property;

        public string Name { get; }

        /// <summary>宣告型別（Nullable&lt;T&gt; 會是 T ——見 <see cref="IsNullable"/>）。</summary>
        public Type Type { get; }

        public SCP_ValueKind Kind { get; }

        /// <summary>ListOf 的元素型別／MapOf 的值型別；其餘為 null。</summary>
        public Type? ElementType { get; }

        /// <summary>宣告成 <c>T?</c>（Nullable value type）⇒ 空字串等於 null，而不是 0。</summary>
        public bool IsNullable { get; }

        /// <summary>寫得進去嗎（readonly 欄位／沒有 setter 的屬性 ⇒ false，但**照樣讀得出來**）。</summary>
        public bool CanWrite { get; }

        /// <summary>Kind == Unsupported 時的原因（人可讀，會被畫在畫面上）。</summary>
        public string UnsupportedReason { get; }

        internal SCP_MemberSchema(FieldInfo? iField, PropertyInfo? iProperty, Type iRawType)
        {
            m_Field = iField;
            m_Property = iProperty;
            Name = iField != null ? iField.Name : iProperty!.Name;

            Type? aUnderlying = Nullable.GetUnderlyingType(iRawType);
            IsNullable = aUnderlying != null;
            Type = aUnderlying ?? iRawType;

            CanWrite = iField != null
                ? !iField.IsInitOnly && !iField.IsLiteral
                : iProperty!.CanWrite && iProperty.SetMethod != null && iProperty.SetMethod.IsPublic;

            Kind = SCP_TypeSchema.Classify(Type, out Type? aElement, out string aReason);
            ElementType = aElement;
            UnsupportedReason = aReason;
        }

        public object? Get(object iOwner)
        {
            if (iOwner == null) throw new ArgumentNullException(nameof(iOwner));
            return m_Field != null ? m_Field.GetValue(iOwner) : m_Property!.GetValue(iOwner);
        }

        /// <summary>
        /// 寫值。回傳有沒有真的寫進去 —— **寫不進去要讓呼叫端知道**，
        /// 靜默失敗會變成「我改了，重新載入又變回來」這種最難查的一族。
        /// </summary>
        public bool TrySet(object iOwner, object? iValue, out string oError)
        {
            oError = "";
            if (!CanWrite) { oError = $"{Name} 是唯讀的（readonly 欄位或沒有公開 setter）"; return false; }
            try
            {
                if (m_Field != null) m_Field.SetValue(iOwner, iValue);
                else m_Property!.SetValue(iOwner, iValue);
                return true;
            }
            catch (Exception e)
            {
                oError = $"{Name} 寫入失敗：{e.GetType().Name}: {e.Message}";
                return false;
            }
        }

        public override string ToString() => $"{Name}:{Kind}({Type.Name})";
    }

    /// <summary>
    /// 一個型別的成員清單。**別自己 new** —— 走 <see cref="SCP_Reflect.SchemaOf"/> 拿快取好的那份。
    /// </summary>
    public sealed class SCP_TypeSchema
    {
        public Type Type { get; }
        public IReadOnlyList<SCP_MemberSchema> Members { get; }

        /// <summary>有無公開無參數建構子（null 的巢狀成員能不能「建立」一份）。</summary>
        public bool CanCreate { get; }

        /// <summary>
        /// 掛了 <see cref="SCP_JsonExtensionDataAttribute"/> 的那個成員（未知鍵的收容所），沒有就是 null。
        /// <para>⚠ 它**不在 <see cref="Members"/> 裡** —— 收容所配 [SCP_Ignore] 用，不該被自動繪製。
        /// 型別是否合法（<c>Dictionary&lt;string, SCP_JsonData&gt;</c>）**由 mapper 判**，
        /// 因為 Reflect 這一層刻意不認識 Json 的型別（不製造 Reflect → Json 的反向相依）。</para>
        /// </summary>
        public SCP_MemberSchema? ExtensionData { get; }

        /// <summary>收容所有問題時的原因（掛超過一個）。null ＝ 沒問題。⛔ 有值時 mapper 必須記 Diagnostics，不得靜默。</summary>
        public string? ExtensionDataProblem { get; }

        internal SCP_TypeSchema(Type iType)
        {
            Type = iType ?? throw new ArgumentNullException(nameof(iType));

            var aList = new List<SCP_MemberSchema>();
            var aExt = new List<SCP_MemberSchema>();

            // 只收**公開實例成員**：private 欄位要不要進來是個政策問題，
            // 而「預設把別人的內部狀態攤到畫面上並存進 JSON」是不可逆的決定 ⇒ 預設不收。
            const BindingFlags aFlags = BindingFlags.Instance | BindingFlags.Public;

            foreach (FieldInfo f in iType.GetFields(aFlags))
            {
                if (f.IsStatic || f.IsLiteral) continue;
                // ⚠ 收容所的判定在 IsIgnored **之前** —— 它正常情況下就掛著 [SCP_Ignore]
                //    （不該被畫出來），先被 ignore 濾掉的話這個能力永遠不會生效。
                if (IsExtensionData(f)) { aExt.Add(new SCP_MemberSchema(f, null, f.FieldType)); continue; }
                if (IsIgnored(f)) continue;
                aList.Add(new SCP_MemberSchema(f, null, f.FieldType));
            }

            foreach (PropertyInfo p in iType.GetProperties(aFlags))
            {
                if (p.GetIndexParameters().Length > 0) continue;         // 索引子沒有「一個值」可畫
                if (p.GetMethod == null || !p.GetMethod.IsPublic) continue;
                if (IsExtensionData(p)) { aExt.Add(new SCP_MemberSchema(null, p, p.PropertyType)); continue; }
                if (IsIgnored(p)) continue;
                aList.Add(new SCP_MemberSchema(null, p, p.PropertyType));
            }

            Members = aList;

            // 兩個以上 ⇒ 「未知鍵進哪一個」沒有答案。任何一種挑法都會在某一次靜默丟掉另一邊
            // ⇒ 整個能力不啟用，並留下原因讓 mapper 印出來。⛔ 不挑第一個湊合。
            if (aExt.Count == 1) { ExtensionData = aExt[0]; }
            else if (aExt.Count > 1)
            {
                var aNames = new List<string>();
                foreach (SCP_MemberSchema m in aExt) aNames.Add(m.Name);
                ExtensionDataProblem =
                    $"{iType.Name} 掛了 {aExt.Count} 個 [SCP_JsonExtensionData]（{string.Join(", ", aNames)}）"
                    + " ⇒ 未知鍵沒有唯一去處，本型別的收容所**不啟用**";
            }

            CanCreate = !iType.IsAbstract
                        && (iType.IsValueType || iType.GetConstructor(System.Type.EmptyTypes) != null);
        }

        static bool IsIgnored(MemberInfo iMember)
            => iMember.GetCustomAttribute<SCP_IgnoreAttribute>() != null;

        static bool IsExtensionData(MemberInfo iMember)
            => iMember.GetCustomAttribute<SCP_JsonExtensionDataAttribute>() != null;

        public SCP_MemberSchema? Find(string iName)
        {
            foreach (SCP_MemberSchema m in Members) if (m.Name == iName) return m;
            return null;
        }

        /// <summary>
        /// 型別 → 種類。<paramref name="oElementType"/> 只有 ListOf／MapOf 會填；
        /// 不支援時 <paramref name="oReason"/> 一定有話說（空字串是 bug）。
        /// </summary>
        public static SCP_ValueKind Classify(Type iType, out Type? oElementType, out string oReason)
        {
            oElementType = null;
            oReason = "";
            if (iType == null) { oReason = "型別是 null"; return SCP_ValueKind.Unsupported; }

            if (iType == typeof(string)) return SCP_ValueKind.Text;
            if (iType == typeof(bool)) return SCP_ValueKind.Bool;
            if (iType.IsEnum) return SCP_ValueKind.Choice;

            if (iType == typeof(byte) || iType == typeof(sbyte) || iType == typeof(short)
                || iType == typeof(ushort) || iType == typeof(int) || iType == typeof(uint)
                || iType == typeof(long) || iType == typeof(ulong))
                return SCP_ValueKind.Integer;

            if (iType == typeof(float) || iType == typeof(double) || iType == typeof(decimal))
                return SCP_ValueKind.Decimal;

            if (iType.IsArray)
            {
                // 陣列要改長度就得換掉整個實例（不像 List 可以原地加減）⇒ 簡易版不吃，但要說出來
                oReason = "陣列尚未支援（長度變更要重建整個實例）—— 改用 List<T>";
                return SCP_ValueKind.Unsupported;
            }

            if (iType.IsGenericType)
            {
                Type aDef = iType.GetGenericTypeDefinition();
                if (aDef == typeof(List<>))
                {
                    oElementType = iType.GetGenericArguments()[0];
                    return SCP_ValueKind.ListOf;
                }
                if (aDef == typeof(Dictionary<,>))
                {
                    Type[] aArgs = iType.GetGenericArguments();
                    if (aArgs[0] != typeof(string))
                    {
                        oReason = $"字典的 key 只支援 string（這個是 {aArgs[0].Name}）—— JSON 的 key 本來就是字串";
                        return SCP_ValueKind.Unsupported;
                    }
                    oElementType = aArgs[1];
                    return SCP_ValueKind.MapOf;
                }
            }

            if (typeof(IDictionary).IsAssignableFrom(iType))
            {
                oReason = "非泛型字典（IDictionary）尚未支援 —— 元素型別問不出來";
                return SCP_ValueKind.Unsupported;
            }
            if (typeof(IEnumerable).IsAssignableFrom(iType))
            {
                oReason = $"{iType.Name} 是序列但不是 List<T> —— 沒有一致的「原地增刪」語意";
                return SCP_ValueKind.Unsupported;
            }

            if (iType.IsInterface || iType.IsAbstract)
            {
                // 這裡不猜實作型別：猜錯的症狀是「存進去的是另一個型別的資料」而且不報錯
                oReason = $"{iType.Name} 是介面／抽象型別 —— 本層不做多型（沒有型別標記，反序列化猜不出來）";
                return SCP_ValueKind.Unsupported;
            }

            if (iType.IsClass || (iType.IsValueType && !iType.IsPrimitive)) return SCP_ValueKind.Nested;

            oReason = $"認不得的型別 {iType.FullName}";
            return SCP_ValueKind.Unsupported;
        }
    }
}
