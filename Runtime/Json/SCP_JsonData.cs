// 區塊職責：JSON 值樹 —— 一個節點可以是「字面值 / 陣列 / 物件」，外加一個第四態：**不存在**。
// 物理意義：概念沿用 UCL_Core 的 JsonData（一顆節點打通讀寫、下標取值、隱式轉換），
//           但**完全重寫、零 Unity 耦合** —— 不繼承任何介面、不 log、不碰 UI。
//           理由：共用碼要在 Unity（無 System.Text.Json）與 .NET 兩邊都能編，
//           而逐字搬會把上游的 UI / CopyPaste 相依鏈一起拖過來。
// 數值影響：⭐ 最重要的設計決定是 **Missing 是一種型別，不是空值**：
//           `data["不存在的key"]` 回一個 Missing 節點，而**從 Missing 讀值會丟例外並附路徑**。
//           🩸 為什麼要這樣：查不到卻回 0／空字串，是「不存在被印成一個看起來正常的值」——
//             那種錯不會叫，只會讓人拿著假數字往下走（LY 專案 2026-08-21：查無帳戶被回成餘額 0）。
//             要「沒有就給我預設值」是**顯式**的事：走 GetString(key, fallback) / TryGet。
// ⚠ 方言限制：本組件必須能被 Unity 編（C# 9 / netstandard2.1）——
//   不用檔案級 namespace、不用 record / init、不用 raw string literal、不碰任何第三方套件。
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;

namespace SCP.Core.Json
{
    public enum SCP_JsonType
    {
        /// <summary>這個 key／index 不存在。**不是 null、不是空值**。</summary>
        Missing = 0,
        Null,
        Bool,
        Number,
        String,
        Array,
        Object,
    }

    /// <summary>從 Missing 節點讀值時丟出 —— 訊息一律帶路徑，讓人知道是「哪一格」不存在。</summary>
    public class SCP_JsonMissingException : Exception
    {
        public SCP_JsonMissingException(string iPath)
            : base("JSON 路徑不存在：" + iPath + "（要允許不存在請用 TryGet… 或 Get…(key, fallback)）") { }
    }

    /// <summary>型別不符時丟出（例：把物件當數字讀）。同樣帶路徑。</summary>
    public class SCP_JsonTypeException : Exception
    {
        public SCP_JsonTypeException(string iPath, SCP_JsonType iActual, string iWanted)
            : base("JSON 型別不符：" + iPath + " 是 " + iActual + "，但被當成 " + iWanted + " 讀") { }
    }

    /// <summary>JSON 值樹的節點。</summary>
    public sealed class SCP_JsonData : IEnumerable<SCP_JsonData>
    {
        // ── 狀態 ──────────────────────────────────────────────
        SCP_JsonType m_Type;
        string m_Raw = "";                                   // Number/String/Bool 的原文
        List<SCP_JsonData>? m_Array;
        List<string>? m_Keys;                                // ⭐ 保留插入順序：輸出要可 diff
        Dictionary<string, SCP_JsonData>? m_Dic;

        /// <summary>這個節點在整棵樹裡的路徑（純為錯誤訊息服務；根是 "$"）。</summary>
        public string Path { get; private set; } = "$";

        public SCP_JsonType Type { get { return m_Type; } }
        public bool IsMissing { get { return m_Type == SCP_JsonType.Missing; } }
        public bool IsNull { get { return m_Type == SCP_JsonType.Null; } }
        public bool Exists { get { return m_Type != SCP_JsonType.Missing; } }

        // ── 建構 ──────────────────────────────────────────────
        SCP_JsonData(SCP_JsonType iType) { m_Type = iType; }

        public SCP_JsonData() : this(SCP_JsonType.Object)
        {
            m_Keys = new List<string>();
            m_Dic = new Dictionary<string, SCP_JsonData>();
        }

        public static SCP_JsonData NewObject() { return new SCP_JsonData(); }

        public static SCP_JsonData NewArray()
        {
            var a = new SCP_JsonData(SCP_JsonType.Array);
            a.m_Array = new List<SCP_JsonData>();
            return a;
        }

        public static SCP_JsonData NewNull() { return new SCP_JsonData(SCP_JsonType.Null); }

        public static SCP_JsonData NewString(string iValue)
        {
            var a = new SCP_JsonData(SCP_JsonType.String);
            a.m_Raw = iValue ?? "";
            return a;
        }

        public static SCP_JsonData NewBool(bool iValue)
        {
            var a = new SCP_JsonData(SCP_JsonType.Bool);
            a.m_Raw = iValue ? "true" : "false";
            return a;
        }

        /// <summary>數字一律**存原文**、讀取時才轉 —— 避免 double 來回一趟把 int 磨成 1.0000000001。</summary>
        public static SCP_JsonData NewNumber(string iRaw)
        {
            var a = new SCP_JsonData(SCP_JsonType.Number);
            a.m_Raw = iRaw;
            return a;
        }

        public static SCP_JsonData NewNumber(long iValue)
        { return NewNumber(iValue.ToString(CultureInfo.InvariantCulture)); }

        public static SCP_JsonData NewNumber(double iValue)
        { return NewNumber(iValue.ToString("R", CultureInfo.InvariantCulture)); }

        /// <summary>parser 專用：直接指定型別與原文。</summary>
        internal static SCP_JsonData Raw(SCP_JsonType iType, string iRaw)
        {
            var a = new SCP_JsonData(iType);
            a.m_Raw = iRaw;
            return a;
        }

        internal static SCP_JsonData MissingAt(string iPath)
        {
            var a = new SCP_JsonData(SCP_JsonType.Missing);
            a.Path = iPath;
            return a;
        }

        public static implicit operator SCP_JsonData(string iValue) { return NewString(iValue); }
        public static implicit operator SCP_JsonData(bool iValue) { return NewBool(iValue); }
        public static implicit operator SCP_JsonData(long iValue) { return NewNumber(iValue); }
        public static implicit operator SCP_JsonData(int iValue) { return NewNumber((long)iValue); }
        public static implicit operator SCP_JsonData(double iValue) { return NewNumber(iValue); }

        // ── 物件存取 ──────────────────────────────────────────
        /// <summary>取子節點。**key 不存在時回 Missing 節點，不丟例外也不建立**（讀不該有副作用）。</summary>
        public SCP_JsonData this[string iKey]
        {
            get
            {
                if (m_Type == SCP_JsonType.Object && m_Dic != null
                    && m_Dic.TryGetValue(iKey, out SCP_JsonData? aChild)) return aChild;
                return MissingAt(Path + "." + iKey);
            }
            set { Set(iKey, value); }
        }

        public SCP_JsonData this[int iIndex]
        {
            get
            {
                if (m_Type == SCP_JsonType.Array && m_Array != null && iIndex >= 0 && iIndex < m_Array.Count)
                    return m_Array[iIndex];
                return MissingAt(Path + "[" + iIndex.ToString(CultureInfo.InvariantCulture) + "]");
            }
        }

        public bool Contains(string iKey)
        { return m_Type == SCP_JsonType.Object && m_Dic != null && m_Dic.ContainsKey(iKey); }

        public IReadOnlyList<string> Keys
        { get { return m_Keys != null ? (IReadOnlyList<string>)m_Keys : Array.Empty<string>(); } }

        public int Count
        {
            get
            {
                if (m_Type == SCP_JsonType.Array) return m_Array != null ? m_Array.Count : 0;
                if (m_Type == SCP_JsonType.Object) return m_Keys != null ? m_Keys.Count : 0;
                return 0;
            }
        }

        public SCP_JsonData Set(string iKey, SCP_JsonData iValue)
        {
            RequireObject("Set");
            if (iValue == null) iValue = NewNull();
            if (!m_Dic!.ContainsKey(iKey)) m_Keys!.Add(iKey);
            m_Dic[iKey] = iValue;
            iValue.Reparent(Path + "." + iKey);
            return this;
        }

        public bool Remove(string iKey)
        {
            if (m_Type != SCP_JsonType.Object || m_Dic == null) return false;
            if (!m_Dic.Remove(iKey)) return false;
            m_Keys!.Remove(iKey);
            return true;
        }

        public SCP_JsonData Add(SCP_JsonData iValue)
        {
            RequireArray("Add");
            if (iValue == null) iValue = NewNull();
            iValue.Reparent(Path + "[" + m_Array!.Count.ToString(CultureInfo.InvariantCulture) + "]");
            m_Array.Add(iValue);
            return this;
        }

        void Reparent(string iPath)
        {
            Path = iPath;
            if (m_Type == SCP_JsonType.Object && m_Keys != null && m_Dic != null)
                foreach (string k in m_Keys) m_Dic[k].Reparent(iPath + "." + k);
            else if (m_Type == SCP_JsonType.Array && m_Array != null)
                for (int i = 0; i < m_Array.Count; i++)
                    m_Array[i].Reparent(iPath + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
        }

        // ── 取值（嚴格）：不存在或型別不符 ⇒ 丟例外並說是哪一格 ──
        public string AsString()
        {
            RequireExists();
            if (m_Type == SCP_JsonType.String) return m_Raw;
            if (m_Type == SCP_JsonType.Number || m_Type == SCP_JsonType.Bool) return m_Raw;
            throw new SCP_JsonTypeException(Path, m_Type, "string");
        }

        public long AsLong()
        {
            RequireExists();
            if (m_Type != SCP_JsonType.Number && m_Type != SCP_JsonType.String)
                throw new SCP_JsonTypeException(Path, m_Type, "number");
            long v;
            if (!long.TryParse(m_Raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v))
                throw new SCP_JsonTypeException(Path, m_Type, "integer(" + m_Raw + ")");
            return v;
        }

        public int AsInt() { return checked((int)AsLong()); }

        public double AsDouble()
        {
            RequireExists();
            if (m_Type != SCP_JsonType.Number && m_Type != SCP_JsonType.String)
                throw new SCP_JsonTypeException(Path, m_Type, "number");
            double v;
            if (!double.TryParse(m_Raw, NumberStyles.Float, CultureInfo.InvariantCulture, out v))
                throw new SCP_JsonTypeException(Path, m_Type, "number(" + m_Raw + ")");
            return v;
        }

        public bool AsBool()
        {
            RequireExists();
            if (m_Type == SCP_JsonType.Bool) return m_Raw == "true";
            throw new SCP_JsonTypeException(Path, m_Type, "bool");
        }

        // ── 取值（寬鬆）：不存在就給 fallback。**要寬鬆必須寫出來** ──
        public string GetString(string iKey, string iFallback)
        { var c = this[iKey]; return c.Exists && !c.IsNull ? c.AsString() : iFallback; }

        public long GetLong(string iKey, long iFallback)
        { var c = this[iKey]; return c.Exists && !c.IsNull ? c.AsLong() : iFallback; }

        public int GetInt(string iKey, int iFallback)
        { var c = this[iKey]; return c.Exists && !c.IsNull ? c.AsInt() : iFallback; }

        public bool GetBool(string iKey, bool iFallback)
        { var c = this[iKey]; return c.Exists && !c.IsNull ? c.AsBool() : iFallback; }

        public bool TryGetString(string iKey, out string oValue)
        {
            var c = this[iKey];
            if (c.Exists && !c.IsNull) { oValue = c.AsString(); return true; }
            oValue = "";
            return false;
        }

        // ── 列舉 ──────────────────────────────────────────────
        /// <summary>陣列 → 逐元素；物件 → 逐值（key 請走 <see cref="Keys"/>）；其餘 → 空。</summary>
        public IEnumerator<SCP_JsonData> GetEnumerator()
        {
            if (m_Type == SCP_JsonType.Array && m_Array != null)
            {
                foreach (var a in m_Array) yield return a;
            }
            else if (m_Type == SCP_JsonType.Object && m_Keys != null && m_Dic != null)
            {
                foreach (string k in m_Keys) yield return m_Dic[k];
            }
        }

        IEnumerator IEnumerable.GetEnumerator() { return GetEnumerator(); }

        // ── 輸出 ──────────────────────────────────────────────
        public string ToJson(bool iIndented = true) { return SCP_JsonWriter.Write(this, iIndented); }
        public override string ToString() { return ToJson(false); }

        /// <summary>parser 專用：把已解析好的 raw 值塞進來。</summary>
        internal string RawValue { get { return m_Raw; } }

        void RequireExists() { if (m_Type == SCP_JsonType.Missing) throw new SCP_JsonMissingException(Path); }

        void RequireObject(string iOp)
        {
            if (m_Type != SCP_JsonType.Object) throw new SCP_JsonTypeException(Path, m_Type, "object(" + iOp + ")");
        }

        void RequireArray(string iOp)
        {
            if (m_Type != SCP_JsonType.Array) throw new SCP_JsonTypeException(Path, m_Type, "array(" + iOp + ")");
        }

        public static SCP_JsonData Parse(string iJson) { return SCP_JsonParser.Parse(iJson); }
    }
}
