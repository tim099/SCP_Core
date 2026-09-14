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
            return Write(iData, SCP_JsonStyle.Default.WithIndent(iIndent ?? DefaultIndent), iIndented);
        }

        /// <summary>指定版面寫出（縮排／冒號後空格／開括號位置）。</summary>
        /// <remarks>
        /// ⚠ 要寫進**既有資料夾**時請帶 <see cref="SCP_JsonStyle.UclLegacy"/> ——
        /// 版面不合的產物仍是合法 JSON、逐鍵相同，而整批翻紅時沒有任何一層會喊。
        /// </remarks>
        public static string Write(SCP_JsonData iData, SCP_JsonStyle iStyle, bool iIndented = true)
        {
            var sb = new StringBuilder();
            WriteValue(iData, sb, iIndented, iStyle, 0);
            return sb.ToString();
        }

        static void WriteValue(SCP_JsonData iData, StringBuilder oSb, bool iIndented, SCP_JsonStyle iStyle, int iDepth)
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
                    if (iData.Count == 0 && !iStyle.EmptyContainerExpanded) { oSb.Append("[]"); break; }
                    oSb.Append('[');
                    // 空陣列展開成 `[`＋**兩個**換行＋`]` —— 中間那個空行是
                    // UCL_JsonData.SerializeValueBeautify 的「開括號後換一行、關括號前再換一行」
                    // 兩段各自成立、而中間沒有元素的結果。⛔ 不是筆誤，磁碟上就是長這樣。
                    if (iData.Count == 0)
                    {
                        NewLineIndent(oSb, iIndented, iStyle, 0);
                        NewLineIndent(oSb, iIndented, iStyle, iDepth);
                        oSb.Append(']');
                        break;
                    }
                    int i = 0;
                    foreach (var aItem in iData)
                    {
                        if (i++ > 0) oSb.Append(',');
                        NewLineIndent(oSb, iIndented, iStyle, iDepth + 1);
                        WriteValue(aItem, oSb, iIndented, iStyle, iDepth + 1);
                    }
                    NewLineIndent(oSb, iIndented, iStyle, iDepth);
                    oSb.Append(']');
                    break;
                }

                case SCP_JsonType.Object:
                {
                    IReadOnlyList<string> aKeys = iData.Keys;
                    if (aKeys.Count == 0 && !iStyle.EmptyContainerExpanded) { oSb.Append("{}"); break; }
                    oSb.Append('{');
                    if (aKeys.Count == 0)
                    {
                        NewLineIndent(oSb, iIndented, iStyle, 0);
                        NewLineIndent(oSb, iIndented, iStyle, iDepth);
                        oSb.Append('}');
                        break;
                    }
                    for (int i = 0; i < aKeys.Count; i++)
                    {
                        if (i > 0) oSb.Append(',');
                        NewLineIndent(oSb, iIndented, iStyle, iDepth + 1);
                        WriteString(aKeys[i], oSb);
                        oSb.Append(':');
                        SCP_JsonData aValue = iData[aKeys[i]];
                        // Allman 只對**陣列**成立（既有產物是 "k":<換行><縮排>[ ）——
                        // 物件的 { 貼在冒號後（"progress":{），純量也貼著。
                        // ⚠ 空陣列**照樣**走 Allman（"facts":<換行><縮排>[<換行><換行><縮排>]），
                        //   那是 UCL_JsonData.SerializeValueBeautify 的形狀，不是例外。
                        bool aAllman = iIndented && iStyle.ArrayBracketOnOwnLine
                                       && aValue.Type == SCP_JsonType.Array;
                        if (aAllman) NewLineIndent(oSb, iIndented, iStyle, iDepth + 1);
                        else if (iIndented && iStyle.SpaceAfterColon) oSb.Append(' ');
                        WriteValue(aValue, oSb, iIndented, iStyle, iDepth + 1);
                    }
                    NewLineIndent(oSb, iIndented, iStyle, iDepth);
                    oSb.Append('}');
                    break;
                }
            }
        }

        static void NewLineIndent(StringBuilder oSb, bool iIndented, SCP_JsonStyle iStyle, int iDepth)
        {
            if (!iIndented) return;
            oSb.Append('\n');
            for (int i = 0; i < iDepth; i++) oSb.Append(iStyle.Indent);
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
