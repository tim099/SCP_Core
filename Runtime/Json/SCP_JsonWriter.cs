// 區塊職責：值樹 → JSON 文字。
// 物理意義：⭐ 兩個決定都是為了「輸出可以被 diff」：
//           ① **key 照插入順序輸出**（不排序、不用 Dictionary 的雜湊順序）——
//              同樣的資料每次輸出逐字相同，於是 git diff 只會顯示真正變動的那幾行。
//           ② **非 ASCII 不轉義**：中文直接寫出去，不變成 中文。
//              轉義過的檔案人看不懂，而人看不懂的 diff 等於沒有 diff。
// 數值影響：預設縮排用 **tab**（對齊既有 UCL 產物的樣式，換手時 diff 不會整檔翻紅）。
// ⚠ 方言限制：C# 9 / netstandard2.1。
#nullable enable
using System.Collections.Generic;
using System.Text;

namespace SCP.Core.Json
{
    public static class SCP_JsonWriter
    {
        public const string DefaultIndent = "\t";

        public static string Write(SCP_JsonData iData, bool iIndented = true, string? iIndent = null)
        {
            var sb = new StringBuilder();
            WriteValue(iData, sb, iIndented, iIndent ?? DefaultIndent, 0);
            return sb.ToString();
        }

        static void WriteValue(SCP_JsonData iData, StringBuilder oSb, bool iIndented, string iIndent, int iDepth)
        {
            switch (iData.Type)
            {
                case SCP_JsonType.Missing:
                    // Missing 不是一個可以被序列化的值 —— 它代表「這一格不存在」。
                    // 悄悄寫成 null 就是把「沒有」變成「有，且是 null」，那是兩件事。
                    throw new SCP_JsonTypeException(iData.Path, SCP_JsonType.Missing, "serializable value");

                case SCP_JsonType.Null:
                    oSb.Append("null");
                    break;

                case SCP_JsonType.Bool:
                case SCP_JsonType.Number:
                    oSb.Append(iData.RawValue);
                    break;

                case SCP_JsonType.String:
                    WriteString(iData.RawValue, oSb);
                    break;

                case SCP_JsonType.Array:
                {
                    if (iData.Count == 0) { oSb.Append("[]"); break; }
                    oSb.Append('[');
                    int i = 0;
                    foreach (var aItem in iData)
                    {
                        if (i++ > 0) oSb.Append(',');
                        NewLineIndent(oSb, iIndented, iIndent, iDepth + 1);
                        WriteValue(aItem, oSb, iIndented, iIndent, iDepth + 1);
                    }
                    NewLineIndent(oSb, iIndented, iIndent, iDepth);
                    oSb.Append(']');
                    break;
                }

                case SCP_JsonType.Object:
                {
                    IReadOnlyList<string> aKeys = iData.Keys;
                    if (aKeys.Count == 0) { oSb.Append("{}"); break; }
                    oSb.Append('{');
                    for (int i = 0; i < aKeys.Count; i++)
                    {
                        if (i > 0) oSb.Append(',');
                        NewLineIndent(oSb, iIndented, iIndent, iDepth + 1);
                        WriteString(aKeys[i], oSb);
                        oSb.Append(':');
                        if (iIndented) oSb.Append(' ');
                        WriteValue(iData[aKeys[i]], oSb, iIndented, iIndent, iDepth + 1);
                    }
                    NewLineIndent(oSb, iIndented, iIndent, iDepth);
                    oSb.Append('}');
                    break;
                }
            }
        }

        static void NewLineIndent(StringBuilder oSb, bool iIndented, string iIndent, int iDepth)
        {
            if (!iIndented) return;
            oSb.Append('\n');
            for (int i = 0; i < iDepth; i++) oSb.Append(iIndent);
        }

        static void WriteString(string iValue, StringBuilder oSb)
        {
            oSb.Append('"');
            for (int i = 0; i < iValue.Length; i++)
            {
                char c = iValue[i];
                switch (c)
                {
                    case '"': oSb.Append("\\\""); break;
                    case '\\': oSb.Append("\\\\"); break;
                    case '\b': oSb.Append("\\b"); break;
                    case '\f': oSb.Append("\\f"); break;
                    case '\n': oSb.Append("\\n"); break;
                    case '\r': oSb.Append("\\r"); break;
                    case '\t': oSb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            // 控制字元一定要轉義（不轉的話產出的檔就不是合法 JSON）
                            oSb.Append("\\u").Append(((int)c).ToString("x4"));
                        }
                        else
                        {
                            // 非 ASCII 照原字寫 —— 見檔頭②
                            oSb.Append(c);
                        }
                        break;
                }
            }
            oSb.Append('"');
        }
    }
}
