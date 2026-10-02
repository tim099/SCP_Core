// 區塊職責：雕刻資料層的 **python 相容層** —— 把 python 時代引擎「python 自己就會做」的幾件事逐格搬過來：
//          json.dump 的版面（indent=2／預設分隔符）、float 的 repr、int()/float() 的字串解析、
//          datetime.isoformat()、str(Path)、python 的真值判定與 str()。TASK-0377。
// 物理意義：既有的 events／sculpt_cache.json／exhibits 是 python 寫的，本引擎接著**寫同一批檔**，
//          新舊產物要「看起來一模一樣」—— 不只語意相等：events 是 append-only、進 git，
//          版面一不同就是整檔翻紅，而翻紅的內容逐鍵相同，沒有任何一層會喊。
// 數值影響：純函式，零 IO。
// 設計取捨：⛔ 不用 SCP_JsonWriter 寫事件／快取：
//          ① python 不縮排時的分隔符是 `", "` 與 `": "`（`{"a": 1, "b": 2}`），SCP 的 compact 模式是 `{"a":1}`；
//          ② float 要照 python repr（`0.4`、`1e-05`、`1e+16`、`100000.0`），SCP 存的是原文；
//          ③ 一刀百萬顆 voxel 的事件，若先建成 SCP_JsonData 樹，每個節點都會帶一條路徑字串（數百 MB）。
//          ⇒ 這裡吃「有序 key-value 清單」直接串流寫出；**讀**仍走 SCP.Core.Json（SCP_JsonData）。
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    /// <summary>
    /// 有序 JSON 物件（＝ python dict 的插入序）。值可以是 null／bool／int／long／double／string／
    /// <see cref="SCP_SculptPyObj"/>／<see cref="IList"/>（含 int[]）／<see cref="SCP_JsonData"/>。
    /// </summary>
    public sealed class SCP_SculptPyObj : List<KeyValuePair<string, object?>>
    {
        public SCP_SculptPyObj Put(string iKey, object? iValue)
        {
            // python dict 語意：既有 key 就地換值（位置不動），新 key 接在最後。
            for (int i = 0; i < Count; i++)
                if (this[i].Key == iKey) { this[i] = new KeyValuePair<string, object?>(iKey, iValue); return this; }
            Add(new KeyValuePair<string, object?>(iKey, iValue));
            return this;
        }

        public bool TryGet(string iKey, out object? oValue)
        {
            foreach (var a in this) if (a.Key == iKey) { oValue = a.Value; return true; }
            oValue = null;
            return false;
        }
    }

    public static class SCP_SculptPy
    {
        // ───────────────────────── json.dumps ─────────────────────────
        /// <summary>
        /// python <c>json.dumps(v, ensure_ascii=False, indent=2)</c>（<paramref name="iIndent2"/>=true）或
        /// <c>json.dumps(v, ensure_ascii=False)</c>（false）的逐字複刻。換行一律 <c>\n</c>（寫檔時再換成平台換行）。
        /// </summary>
        public static string Dumps(object? iValue, bool iIndent2)
        {
            var aSb = new StringBuilder();
            Write(aSb, iValue, iIndent2, 0);
            return aSb.ToString();
        }

        /// <summary>
        /// python 以文字模式寫檔（<c>open(p, "w", encoding="utf-8")</c>）：UTF-8 無 BOM，<c>\n</c> 換成平台換行
        /// （Windows ＝ CRLF —— 磁碟上既有的 events／exhibits 就是 CRLF、檔尾無換行）。
        /// </summary>
        public static void WriteTextFile(string iPath, string iText)
        {
            string aText = Environment.NewLine == "\n" ? iText : iText.Replace("\n", Environment.NewLine);
            File.WriteAllText(iPath, aText, new UTF8Encoding(false));
        }

        static void Write(StringBuilder oSb, object? iValue, bool iIndent, int iDepth)
        {
            switch (iValue)
            {
                case null: oSb.Append("null"); return;
                case bool b: oSb.Append(b ? "true" : "false"); return;
                case int n: oSb.Append(n.ToString(CultureInfo.InvariantCulture)); return;
                case long l: oSb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case double d: oSb.Append(JsonFloat(d)); return;
                case string s: WriteString(oSb, s); return;
                case SCP_JsonData j: WriteJsonData(oSb, j, iIndent, iDepth); return;
                case SCP_SculptPyObj o: WriteObject(oSb, o, iIndent, iDepth); return;
                case int[] ia:
                {
                    if (ia.Length == 0) { oSb.Append("[]"); return; }
                    oSb.Append('[');
                    for (int i = 0; i < ia.Length; i++)
                    {
                        if (i > 0) oSb.Append(iIndent ? "," : ", ");
                        NewLine(oSb, iIndent, iDepth + 1);
                        oSb.Append(ia[i].ToString(CultureInfo.InvariantCulture));
                    }
                    NewLine(oSb, iIndent, iDepth);
                    oSb.Append(']');
                    return;
                }
                case IList list:
                {
                    if (list.Count == 0) { oSb.Append("[]"); return; }
                    oSb.Append('[');
                    for (int i = 0; i < list.Count; i++)
                    {
                        if (i > 0) oSb.Append(iIndent ? "," : ", ");
                        NewLine(oSb, iIndent, iDepth + 1);
                        Write(oSb, list[i], iIndent, iDepth + 1);
                    }
                    NewLine(oSb, iIndent, iDepth);
                    oSb.Append(']');
                    return;
                }
                default:
                    throw new ArgumentException("SCP_SculptPy.Dumps 不認得的值型別：" + iValue.GetType().FullName);
            }
        }

        static void WriteObject(StringBuilder oSb, SCP_SculptPyObj iObj, bool iIndent, int iDepth)
        {
            if (iObj.Count == 0) { oSb.Append("{}"); return; }
            oSb.Append('{');
            for (int i = 0; i < iObj.Count; i++)
            {
                if (i > 0) oSb.Append(iIndent ? "," : ", ");
                NewLine(oSb, iIndent, iDepth + 1);
                WriteString(oSb, iObj[i].Key);
                oSb.Append(": ");
                Write(oSb, iObj[i].Value, iIndent, iDepth + 1);
            }
            NewLine(oSb, iIndent, iDepth);
            oSb.Append('}');
        }

        static void WriteJsonData(StringBuilder oSb, SCP_JsonData iData, bool iIndent, int iDepth)
        {
            switch (iData.Type)
            {
                case SCP_JsonType.Null: oSb.Append("null"); return;
                case SCP_JsonType.Bool: oSb.Append(iData.AsBool() ? "true" : "false"); return;
                case SCP_JsonType.Number: oSb.Append(NormalizeNumberRaw(iData.AsString())); return;
                case SCP_JsonType.String: WriteString(oSb, iData.AsString()); return;
                case SCP_JsonType.Array:
                {
                    if (iData.Count == 0) { oSb.Append("[]"); return; }
                    oSb.Append('[');
                    int i = 0;
                    foreach (var aItem in iData)
                    {
                        if (i++ > 0) oSb.Append(iIndent ? "," : ", ");
                        NewLine(oSb, iIndent, iDepth + 1);
                        WriteJsonData(oSb, aItem, iIndent, iDepth + 1);
                    }
                    NewLine(oSb, iIndent, iDepth);
                    oSb.Append(']');
                    return;
                }
                case SCP_JsonType.Object:
                {
                    IReadOnlyList<string> aKeys = iData.Keys;
                    if (aKeys.Count == 0) { oSb.Append("{}"); return; }
                    oSb.Append('{');
                    for (int i = 0; i < aKeys.Count; i++)
                    {
                        if (i > 0) oSb.Append(iIndent ? "," : ", ");
                        NewLine(oSb, iIndent, iDepth + 1);
                        WriteString(oSb, aKeys[i]);
                        oSb.Append(": ");
                        WriteJsonData(oSb, iData[aKeys[i]], iIndent, iDepth + 1);
                    }
                    NewLine(oSb, iIndent, iDepth);
                    oSb.Append('}');
                    return;
                }
                default:
                    throw new SCP_JsonTypeException(iData.Path, iData.Type, "serializable value");
            }
        }

        /// <summary>
        /// 讀進來的數字原文 → python 會寫回去的樣子：整數照抄、帶小數點／指數的走 float repr
        /// （python 讀 <c>1E5</c> 得到 float，寫回去是 <c>100000.0</c>）。
        /// </summary>
        public static string NormalizeNumberRaw(string iRaw)
        {
            if (iRaw.IndexOf('.') < 0 && iRaw.IndexOf('e') < 0 && iRaw.IndexOf('E') < 0)
            {
                // python int(…) 再 str(…)：-0 → 0、前導零在合法 JSON 裡不存在
                return long.TryParse(iRaw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long aL)
                    ? aL.ToString(CultureInfo.InvariantCulture) : iRaw;
            }
            return double.TryParse(iRaw, NumberStyles.Float, CultureInfo.InvariantCulture, out double aD)
                ? JsonFloat(aD) : iRaw;
        }

        static void NewLine(StringBuilder oSb, bool iIndent, int iDepth)
        {
            if (!iIndent) return;
            oSb.Append('\n');
            oSb.Append(' ', iDepth * 2);
        }

        /// <summary>python json 的字串轉義（ensure_ascii=False）：只轉 <c>" \ \b \f \n \r \t</c> 與其餘 &lt;0x20。</summary>
        public static void WriteString(StringBuilder oSb, string iValue)
        {
            oSb.Append('"');
            foreach (char c in iValue)
            {
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
                        if (c < ' ') oSb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else oSb.Append(c);
                        break;
                }
            }
            oSb.Append('"');
        }

        // ───────────────────────── float repr ─────────────────────────
        /// <summary>json.dumps 的 float：NaN／Infinity 照 python 寫成非標準字面值，其餘＝repr。</summary>
        public static string JsonFloat(double iValue)
        {
            if (double.IsNaN(iValue)) return "NaN";
            if (double.IsPositiveInfinity(iValue)) return "Infinity";
            if (double.IsNegativeInfinity(iValue)) return "-Infinity";
            return FloatRepr(iValue);
        }

        /// <summary>
        /// python <c>repr(float)</c>：最短往返位數；十進位指數 decpt（值 ＝ 0.d₁d₂… × 10^decpt）落在 (-4, 16] ⇒ 定點
        /// （整數補 <c>.0</c>），否則科學記號（<c>1e-05</c>、<c>1.5e+16</c>，指數至少兩位、必帶正負號）。
        /// <para>⚠ 最短位數靠 <c>ToString("R")</c>：.NET Core 3.0+ 保證最短往返；Unity（Mono）的 "R" 偶爾給 17 位 ——
        /// 只會影響「非 python 寫出來的怪 float」的位數，0.4／1.5 這類值兩邊一樣。</para>
        /// </summary>
        public static string FloatRepr(double iValue)
        {
            if (double.IsNaN(iValue)) return "nan";
            if (double.IsInfinity(iValue)) return iValue > 0 ? "inf" : "-inf";
            if (iValue == 0) return (1 / iValue) < 0 ? "-0.0" : "0.0";
            string aR = iValue.ToString("R", CultureInfo.InvariantCulture);
            bool aNeg = aR[0] == '-';
            if (aNeg) aR = aR.Substring(1);
            int aExp = 0;
            int aE = aR.IndexOfAny(new[] { 'E', 'e' });
            if (aE >= 0)
            {
                aExp = int.Parse(aR.Substring(aE + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                aR = aR.Substring(0, aE);
            }
            int aDot = aR.IndexOf('.');
            string aIntPart = aDot >= 0 ? aR.Substring(0, aDot) : aR;
            string aFrac = aDot >= 0 ? aR.Substring(aDot + 1) : "";
            string aDigits = aIntPart + aFrac;
            int aDecpt = aIntPart.Length + aExp;
            // 去前導零（0.0001 → digits "00001"）
            int aLead = 0;
            while (aLead < aDigits.Length - 1 && aDigits[aLead] == '0') { aLead++; aDecpt--; }
            aDigits = aDigits.Substring(aLead);
            aDigits = aDigits.TrimEnd('0');
            if (aDigits.Length == 0) aDigits = "0";

            var aSb = new StringBuilder();
            if (aNeg) aSb.Append('-');
            if (aDecpt > -4 && aDecpt <= 16)
            {
                if (aDecpt <= 0)
                {
                    aSb.Append("0.").Append('0', -aDecpt).Append(aDigits);
                }
                else if (aDigits.Length <= aDecpt)
                {
                    aSb.Append(aDigits).Append('0', aDecpt - aDigits.Length).Append(".0");
                }
                else
                {
                    aSb.Append(aDigits, 0, aDecpt).Append('.').Append(aDigits, aDecpt, aDigits.Length - aDecpt);
                }
            }
            else
            {
                aSb.Append(aDigits[0]);
                if (aDigits.Length > 1) aSb.Append('.').Append(aDigits, 1, aDigits.Length - 1);
                int aX = aDecpt - 1;
                aSb.Append('e').Append(aX < 0 ? '-' : '+');
                aSb.Append(Math.Abs(aX).ToString("00", CultureInfo.InvariantCulture));
            }
            return aSb.ToString();
        }

        // ───────────────────────── int() / float() ─────────────────────────
        /// <summary>
        /// python <c>int(str)</c>：去前後空白、可帶 +/-、數字之間可夾單一底線（<c>1_000</c>）。
        /// <para>⚠ 不支援 python 也接受的全形／其他 Unicode 數字（<c>int("１２")</c>）—— 雕刻參數不會長那樣。</para>
        /// </summary>
        public static bool TryInt(string? iText, out int oValue)
        {
            oValue = 0;
            if (!TryLong(iText, out long aL) || aL < int.MinValue || aL > int.MaxValue) return false;
            oValue = (int)aL;
            return true;
        }

        public static bool TryLong(string? iText, out long oValue)
        {
            oValue = 0;
            if (iText == null) return false;
            string a = iText.Trim();
            if (a.Length == 0) return false;
            int i = 0;
            bool aNeg = false;
            if (a[0] == '+' || a[0] == '-') { aNeg = a[0] == '-'; i = 1; }
            if (i >= a.Length) return false;
            var aDigits = new StringBuilder();
            bool aPrevDigit = false;
            for (; i < a.Length; i++)
            {
                char c = a[i];
                if (c >= '0' && c <= '9') { aDigits.Append(c); aPrevDigit = true; continue; }
                if (c == '_' && aPrevDigit && i + 1 < a.Length && a[i + 1] >= '0' && a[i + 1] <= '9') { aPrevDigit = false; continue; }
                return false;
            }
            if (aDigits.Length == 0) return false;
            if (!long.TryParse(aDigits.ToString(), NumberStyles.None, CultureInfo.InvariantCulture, out long aV)) return false;
            oValue = aNeg ? -aV : aV;
            return true;
        }

        /// <summary>python <c>float(str)</c>：去空白；接受 inf／infinity／nan（不分大小寫、可帶正負號）與底線分位。</summary>
        public static bool TryFloat(string? iText, out double oValue)
        {
            oValue = 0;
            if (iText == null) return false;
            string a = iText.Trim();
            if (a.Length == 0) return false;
            string aLow = a.ToLowerInvariant();
            string aBody = aLow.TrimStart('+', '-');
            bool aNeg = aLow.StartsWith("-", StringComparison.Ordinal);
            if (aLow.Length - aBody.Length > 1) return false;   // 多個正負號
            if (aBody == "inf" || aBody == "infinity") { oValue = aNeg ? double.NegativeInfinity : double.PositiveInfinity; return true; }
            if (aBody == "nan") { oValue = double.NaN; return true; }
            // 底線只准夾在兩個數字之間
            var aSb = new StringBuilder();
            for (int i = 0; i < a.Length; i++)
            {
                char c = a[i];
                if (c == '_')
                {
                    bool aOk = i > 0 && i + 1 < a.Length && char.IsDigit(a[i - 1]) && char.IsDigit(a[i + 1]);
                    if (!aOk) return false;
                    continue;
                }
                if (!((c >= '0' && c <= '9') || c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-')) return false;
                aSb.Append(c);
            }
            return double.TryParse(aSb.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out oValue);
        }

        // ───────────────────────── datetime / Path ─────────────────────────
        /// <summary>python <c>datetime.now().isoformat()</c>（naive 本地時間；微秒為 0 時**不印**小數部分，與 python 同）。</summary>
        public static string IsoFormat(DateTime iTime)
        {
            long aMicro = (iTime.Ticks % TimeSpan.TicksPerSecond) / 10;
            string aBase = iTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
            return aMicro == 0 ? aBase : aBase + "." + aMicro.ToString("000000", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// python <c>str(Path(s))</c>：分隔符換成平台的、重複分隔符與 <c>.</c> 段收掉、去尾分隔符。
        /// <para>⚠ 不做絕對化（python 的 <c>Path("a/b")</c> 印出來仍是相對的）。</para>
        /// </summary>
        public static string PathStr(string iPath)
        {
            char aSep = Path.DirectorySeparatorChar;
            string a = aSep == '\\' ? iPath.Replace('/', '\\') : iPath;
            if (a.Length == 0) return ".";
            string aPrefix = "";
            if (aSep == '\\' && a.StartsWith("\\\\", StringComparison.Ordinal)) { aPrefix = "\\\\"; a = a.Substring(2); }
            else if (a.Length >= 2 && a[1] == ':' && aSep == '\\')
            {
                aPrefix = a.Substring(0, 2);
                a = a.Substring(2);
                if (a.StartsWith("\\", StringComparison.Ordinal)) { aPrefix += "\\"; a = a.TrimStart('\\'); }
            }
            else if (a.Length > 0 && a[0] == aSep) { aPrefix = aSep.ToString(); a = a.TrimStart(aSep); }
            var aParts = new List<string>();
            foreach (string p in a.Split(aSep))
            {
                if (p.Length == 0 || p == ".") continue;
                aParts.Add(p);
            }
            string aOut = aPrefix + string.Join(aSep.ToString(), aParts);
            return aOut.Length == 0 ? "." : aOut;
        }

        /// <summary>python <c>str(Path(p).resolve())</c> 的近似（絕對化＋平台分隔符；⚠ 不展開 symlink）。</summary>
        public static string FullPathStr(string iPath) { return PathStr(Path.GetFullPath(iPath)); }

        /// <summary>pathlib 的 <c>with_suffix</c>：名稱裡最後一個「.」（不在開頭、不在結尾）之後換掉；沒有就接上。</summary>
        public static string WithSuffix(string iPath, string iSuffix)
        {
            string aDir = Path.GetDirectoryName(iPath) ?? "";
            string aName = Path.GetFileName(iPath);
            int i = aName.LastIndexOf('.');
            string aStem = (i > 0 && i < aName.Length - 1) ? aName.Substring(0, i) : aName;
            return aDir.Length > 0 ? Path.Combine(aDir, aStem + iSuffix) : aStem + iSuffix;
        }

        // ───────────────────────── 真值 / str() ─────────────────────────
        /// <summary>python 的真值：null／false／0／""／[]／{} 為假；Missing 也當假（＝ dict.get 回 None）。</summary>
        public static bool Truthy(SCP_JsonData? iData)
        {
            if (iData == null) return false;
            switch (iData.Type)
            {
                case SCP_JsonType.Missing:
                case SCP_JsonType.Null: return false;
                case SCP_JsonType.Bool: return iData.AsBool();
                case SCP_JsonType.Number:
                    return double.TryParse(iData.AsString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d) && d != 0;
                case SCP_JsonType.String: return iData.AsString().Length > 0;
                default: return iData.Count > 0;
            }
        }

        /// <summary>python 的 <c>str(value)</c>（給人讀的那幾行 print 用）。</summary>
        public static string Str(SCP_JsonData iData)
        {
            switch (iData.Type)
            {
                case SCP_JsonType.Null: return "None";
                case SCP_JsonType.Bool: return iData.AsBool() ? "True" : "False";
                case SCP_JsonType.Number:
                {
                    string aRaw = NormalizeNumberRaw(iData.AsString());
                    return aRaw == "NaN" ? "nan" : aRaw == "Infinity" ? "inf" : aRaw == "-Infinity" ? "-inf" : aRaw;
                }
                case SCP_JsonType.String: return iData.AsString();
                default: return Dumps(iData, false);   // ⚠ python 印的是 dict／list 的 repr（單引號），這裡近似
            }
        }

        /// <summary>python 的 list repr：<c>[1, 2, 3]</c>。</summary>
        public static string ListRepr(int[] iValues)
        {
            var aSb = new StringBuilder("[");
            for (int i = 0; i < iValues.Length; i++)
            {
                if (i > 0) aSb.Append(", ");
                aSb.Append(iValues[i].ToString(CultureInfo.InvariantCulture));
            }
            return aSb.Append(']').ToString();
        }

        /// <summary>小寫十六進位 sha256。</summary>
        public static string Sha256Hex(byte[] iBytes)
        {
            using var aSha = System.Security.Cryptography.SHA256.Create();
            byte[] aHash = aSha.ComputeHash(iBytes);
            var aSb = new StringBuilder(aHash.Length * 2);
            foreach (byte b in aHash) aSb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return aSb.ToString();
        }
    }
}
