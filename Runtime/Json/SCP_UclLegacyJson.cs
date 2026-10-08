// 區塊職責：UCL `JsonData` 的**位元組級**相容層 —— 只給磁碟上既有、用這套格式寫成的那幾份檔用。
// 物理意義：TASK-0354。persona 的結構欄（`profile/{identity_vector,vector_history,fork_lineage}.md`）與
//           寫入審計（`AwakenInit/_persona_write_audit.jsonl`）是 UCL `JsonData.ToJsonBeautify`／`ToJson`
//           的格式，而那套格式跟 `SCP_JsonWriter`（含 `SCP_JsonStyle.UclLegacy`）有三格不同：
//             ① 換行是 `\r\n`
//             ② 根陣列前面先換一行（`\r\n[`）、空陣列是 `\r\n[\r\n\r\n]`
//             ③ 字串只有 32..126 原樣，其餘一律 `\uXXXX`（小寫 hex）—— 中文會被轉義
//           ⇒ 用通用 writer 改寫一次，git diff 就整份翻動，而內容一個字都沒變。
//           數字照 UCL parser 的分型：沒有小數點 ⇒ int／long（原樣印）；有小數點 ⇒ double（`R` 格式，`1.0` 印成 `1`）。
// 數值影響：純字串轉換，無 IO。⛔ **不要拿它寫新格式的檔** —— 新檔一律 `SCP_JsonWriter`（Coding_Standards §2）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SCP.Core.Json
{
    /// <summary>有序物件（插入順序＝輸出順序，與 UCL `m_Dic` 的列舉序同）。</summary>
    public sealed class SCP_UclLegacyObject : List<KeyValuePair<string, object?>>
    {
        public void Set(string iKey, object? iValue)
        {
            int i = FindIndex(p => p.Key == iKey);
            if (i >= 0) this[i] = new KeyValuePair<string, object?>(iKey, iValue);
            else Add(new KeyValuePair<string, object?>(iKey, iValue));
        }
    }

    public static class SCP_UclLegacyJson
    {
        public const string NewLine = "\r\n";

        // ── 寫 ───────────────────────────────────────────────────
        public static string ToJson(object? iValue)
        {
            var sb = new StringBuilder();
            WriteCompact(iValue, sb);
            return sb.ToString();
        }

        public static string ToJsonBeautify(object? iValue)
        {
            var sb = new StringBuilder();
            WriteBeautify(iValue, sb, 0);
            return sb.ToString();
        }

        static void WriteCompact(object? v, StringBuilder sb)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case List<object?> aList:
                    sb.Append('[');
                    for (int i = 0; i < aList.Count; i++) { if (i > 0) sb.Append(','); WriteCompact(aList[i], sb); }
                    sb.Append(']');
                    break;
                case SCP_UclLegacyObject aObj:
                    sb.Append('{');
                    for (int i = 0; i < aObj.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        WriteString(aObj[i].Key, sb);
                        sb.Append(':');
                        WriteCompact(aObj[i].Value, sb);
                    }
                    sb.Append('}');
                    break;
                default: WriteScalar(v, sb); break;
            }
        }

        static void WriteBeautify(object? v, StringBuilder sb, int iLayer)
        {
            string aTabs = new string('\t', iLayer);
            switch (v)
            {
                case null: sb.Append("null"); break;
                case List<object?> aList:
                    sb.Append(NewLine).Append(aTabs).Append('[').Append(NewLine);
                    for (int i = 0; i < aList.Count; i++)
                    {
                        if (i > 0) sb.Append(',').Append(NewLine);
                        sb.Append(aTabs).Append('\t');
                        WriteBeautify(aList[i], sb, iLayer + 1);
                    }
                    sb.Append(NewLine).Append(aTabs).Append(']');
                    break;
                case SCP_UclLegacyObject aObj:
                    sb.Append('{').Append(NewLine);
                    for (int i = 0; i < aObj.Count; i++)
                    {
                        if (i > 0) sb.Append(',').Append(NewLine);
                        sb.Append(aTabs).Append('\t');
                        WriteString(aObj[i].Key, sb);
                        sb.Append(':');
                        WriteBeautify(aObj[i].Value, sb, iLayer + 1);
                    }
                    sb.Append(NewLine).Append(aTabs).Append('}');
                    break;
                default: WriteScalar(v, sb); break;
            }
        }

        static void WriteScalar(object v, StringBuilder sb)
        {
            switch (v)
            {
                case string s: WriteString(s, sb); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); break;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); break;
                case ulong u: sb.Append(u.ToString(CultureInfo.InvariantCulture)); break;
                case double d: sb.Append(d.ToString("R", CultureInfo.InvariantCulture)); break;
                default: WriteString(Convert.ToString(v, CultureInfo.InvariantCulture) ?? "", sb); break;
            }
        }

        /// <summary>UCL `SerializeString`：`" \ \b \f \n \r \t` 轉義；32..126 原樣；其餘 `\uXXXX`（小寫）。</summary>
        public static void WriteString(string s, StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c >= 32 && c <= 126) sb.Append(c);
                        else sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        break;
                }
            }
            sb.Append('"');
        }

        // ── 讀（分型與 UCL parser 同）──────────────────────────────
        /// <summary>解析 JSON 文字 ⇒ null／bool／string／int／long／ulong／double／List&lt;object?&gt;／<see cref="SCP_UclLegacyObject"/>。
        /// 壞字面丟 <see cref="FormatException"/>（訊息帶位置）。</summary>
        public static object? Parse(string iText)
        {
            int i = 0;
            object? v = ParseValue(iText, ref i);
            SkipWs(iText, ref i);
            if (i != iText.Length) throw new FormatException($"多餘的字元（位置 {i}）");
            return v;
        }

        static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object? ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("意外的結尾");
            char c = s[i];
            if (c == '[')
            {
                i++;
                var aList = new List<object?>();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return aList; }
                while (true)
                {
                    aList.Add(ParseValue(s, ref i));
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatException("陣列沒有收尾");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return aList; }
                    throw new FormatException($"陣列裡預期 , 或 ]（位置 {i}）");
                }
            }
            if (c == '{')
            {
                i++;
                var aObj = new SCP_UclLegacyObject();
                SkipWs(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return aObj; }
                while (true)
                {
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new FormatException($"物件裡預期字串 key（位置 {i}）");
                    string k = ParseString(s, ref i);
                    SkipWs(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new FormatException($"預期 :（位置 {i}）");
                    i++;
                    aObj.Set(k, ParseValue(s, ref i));
                    SkipWs(s, ref i);
                    if (i >= s.Length) throw new FormatException("物件沒有收尾");
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return aObj; }
                    throw new FormatException($"物件裡預期 , 或 }}（位置 {i}）");
                }
            }
            if (c == '"') return ParseString(s, ref i);
            int aStart = i;
            while (i < s.Length && " \t\r\n,]}".IndexOf(s[i]) < 0) i++;
            string w = s.Substring(aStart, i - aStart);
            if (w == "true") return true;
            if (w == "false") return false;
            if (w == "null") return null;
            if (w.Length > 0 && (w[0] == '-' || char.IsDigit(w[0])))
            {
                if (w.IndexOf('.') < 0)
                {
                    if (int.TryParse(w, NumberStyles.Number, CultureInfo.InvariantCulture, out int aI)) return aI;
                    if (long.TryParse(w, NumberStyles.Number, CultureInfo.InvariantCulture, out long aL)) return aL;
                    if (ulong.TryParse(w, NumberStyles.Number, CultureInfo.InvariantCulture, out ulong aU)) return aU;
                }
                if (double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out double aD)) return aD;
            }
            throw new FormatException($"認不得的字面 '{w}'（位置 {aStart}）");
        }

        static string ParseString(string s, ref int i)
        {
            i++; // 開頭的 "
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > s.Length) throw new FormatException("\\u 後面不足四碼");
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw new FormatException($"認不得的跳脫 \\{e}");
                }
            }
            throw new FormatException("字串沒有收尾");
        }
    }
}
