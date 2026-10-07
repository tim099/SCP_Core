// 區塊職責：Plurk 移植層的共用小零件 —— UCL `JsonData` 形狀的薄殼、回應取值、報告文字、失敗型別、排序器。
// 物理意義：本檔讀 API 回應的方式**依賴 UCL `JsonData` 的兩個怪癖**：
//            ① `ToJson()` 把非 ASCII 轉成 `\uXXXX` ⇒ `JsonScalar` 拿到的是**轉義過的**字串，
//               呼叫端再走 `UnescapeJson` 還原（沒走的那幾格就印轉義形 —— 那也是輸出的一部分）
//            ② 數字照 UCL parser 分型（無小數點 ⇒ int／long 原樣；有小數點 ⇒ double `R`）
//          ⇒ 換成 `SCP_JsonData`（原文保存、字串不轉義）的話，同一份回應會印出**不同的報告**，
//            而差異只在少數欄位，看起來像資料變了。⇒ 本檔把樹建在 `SCP_UclLegacyJson`
//            （為 UCL 位元組相容而建的那一份）上，取值語意逐格對齊 Unity 版。
// 數值影響：純記憶體，零 IO。⛔ 本檔不給新格式用 —— 新檔一律 `SCP_JsonWriter`。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Plurk
{
    /// <summary>
    /// UCL `JsonData` 形狀的節點薄殼（底下是 <see cref="SCP_UclLegacyJson"/> 的值樹）。
    /// <para>語意照 UCL：取不到的 key／index **與值是 JSON null 的回 C# null**（呼叫端一律先判 null）。</para>
    /// </summary>
    public sealed class SCP_PlurkNode
    {
        public object? Value;

        public SCP_PlurkNode(object? iValue) { Value = iValue; }

        public static SCP_PlurkNode NewObject() => new SCP_PlurkNode(new SCP_UclLegacyObject());

        public bool IsArray => Value is List<object?>;
        public bool IsObject => Value is SCP_UclLegacyObject;

        public int Count
        {
            get
            {
                if (Value is List<object?> aList) return aList.Count;
                if (Value is SCP_UclLegacyObject aObj) return aObj.Count;
                return 0;
            }
        }

        public SCP_PlurkNode? this[int iIndex]
        {
            get
            {
                if (Value is List<object?> aList && iIndex >= 0 && iIndex < aList.Count && aList[iIndex] != null)
                    return new SCP_PlurkNode(aList[iIndex]);
                return null;
            }
        }

        public bool Contains(string iKey)
            => Value is SCP_UclLegacyObject aObj && aObj.FindIndex(p => p.Key == iKey) >= 0;

        public SCP_PlurkNode? this[string iKey]
        {
            get
            {
                if (!(Value is SCP_UclLegacyObject aObj)) return null;
                int i = aObj.FindIndex(p => p.Key == iKey);
                if (i < 0 || aObj[i].Value == null) return null;
                return new SCP_PlurkNode(aObj[i].Value);
            }
            set
            {
                if (Value is SCP_UclLegacyObject aObj) aObj.Set(iKey, value?.Value);
            }
        }

        public List<string> Keys
        {
            get
            {
                var aOut = new List<string>();
                if (Value is SCP_UclLegacyObject aObj) foreach (var kv in aObj) aOut.Add(kv.Key);
                return aOut;
            }
        }

        /// <summary>UCL `ToJson()`：compact、非 ASCII 一律 `\uXXXX`。</summary>
        public string ToJson() => SCP_UclLegacyJson.ToJson(Value);

        /// <summary>UCL `GetString()`：字串節點回值本身（未轉義）。</summary>
        public string GetString()
            => Value is string s ? s : (Value == null ? "" : Convert.ToString(Value, CultureInfo.InvariantCulture) ?? "");
    }

    /// <summary>
    /// 一支 op 的失敗。<see cref="ExitCode"/>：2 ＝ 被擋／用法錯（沒有對外動作發生）；1 ＝ API／連線失敗。
    /// <para>⚠ 存在的理由：Unity 版一律 `throw new Exception` 交給 runner 判失敗；搬到 SCP 之後
    /// 「被規則擋下」與「對方回 4xx」要給不同的 exit code —— 兩者的處置相反（一個改文案、一個查連線）。</para>
    /// </summary>
    public sealed class SCP_PlurkFailure : Exception
    {
        public readonly int ExitCode;
        public SCP_PlurkFailure(int iExitCode, string iMessage) : base(iMessage) { ExitCode = iExitCode; }

        public static SCP_PlurkFailure Blocked(string iMessage) => new SCP_PlurkFailure(2, iMessage);
        public static SCP_PlurkFailure Api(string iMessage) => new SCP_PlurkFailure(1, iMessage);
    }

    /// <summary>
    /// 報告文字。換行字元由建構子決定 —— 回傳檔走 SCP 慣例 `\n`；
    /// `shared.md` 這種 Unity 版用 `AppendLine` 寫過、而人與 git 都在讀的檔走 `\r\n`（位元組相容）。
    /// </summary>
    public sealed class SCP_PlurkText
    {
        readonly StringBuilder m_Sb = new StringBuilder();
        readonly string m_NewLine;

        public SCP_PlurkText(string iNewLine = "\n") { m_NewLine = iNewLine; }

        public SCP_PlurkText AppendLine(string iLine = "") { m_Sb.Append(iLine).Append(m_NewLine); return this; }
        public SCP_PlurkText Append(string iText) { m_Sb.Append(iText); return this; }
        public SCP_PlurkText Append(SCP_PlurkText iOther) { m_Sb.Append(iOther.ToString()); return this; }
        public override string ToString() => m_Sb.ToString();
    }

    /// <summary>
    /// Unity 版 `OrderBy(r => r.Tier)`（預設比較器＝文化相依）在表情表上的**實測**排序規則的模擬。
    /// <para>🩸 為什麼不用 Ordinal：Senate 開 `InvariantGlobalization` ⇒ 文化比較退化成 ordinal，
    /// 而 `shared.json` 現有 273 列是 Mono 的文化比較排的（不分大小寫、`-` 與 `'` 不計權重）——
    /// 用 ordinal 重寫一次，整份表的列序翻動、git diff 一大片，而內容一個字都沒變。</para>
    /// <para>📐 2026-10-01 對現有 273 列實測：主鍵＝小寫並去掉 `-` `'` 後 ordinal ⇒ **0 個逆序**。
    /// 同鍵時：去符號前的小寫 ordinal（`:'-(` 先於 `:-(`，與現檔一致）→ 小寫先於大寫。</para>
    /// ⚠ 這是**模擬**不是 Mono 的排序表：未出現在現檔的符號組合可能排得不一樣（只影響列序，不影響內容）。
    /// </summary>
    public sealed class SCP_PlurkCultureLikeComparer : IComparer<string>
    {
        public static readonly SCP_PlurkCultureLikeComparer Instance = new SCP_PlurkCultureLikeComparer();

        static string Primary(string s) => s.ToLowerInvariant().Replace("-", "").Replace("'", "");

        static string SwapCase(string s)
        {
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append(char.IsUpper(c) ? char.ToLowerInvariant(c) : char.IsLower(c) ? char.ToUpperInvariant(c) : c);
            return sb.ToString();
        }

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x == null) return -1;
            if (y == null) return 1;
            int c = string.CompareOrdinal(Primary(x), Primary(y));
            if (c != 0) return c;
            c = string.CompareOrdinal(x.ToLowerInvariant(), y.ToLowerInvariant());
            if (c != 0) return c;
            return string.CompareOrdinal(SwapCase(x), SwapCase(y));
        }
    }

    /// <summary>回應取值小工具。</summary>
    public static class SCP_PlurkJson
    {
        public static SCP_PlurkNode? SafeParse(string? iJson)
        {
            if (iJson == null) return null;
            try
            {
                object? v = SCP_UclLegacyJson.Parse(iJson);
                return v == null ? null : new SCP_PlurkNode(v);
            }
            catch { return null; }
        }

        /// <summary>取純量欄位；字串或數字都拿得到（同 <see cref="PickJsonValue"/> 那條血證）。</summary>
        public static string JsonScalar(SCP_PlurkNode? iNode, string iKey)
        {
            if (iNode == null || !iNode.Contains(iKey)) return "";
            // 🩸 2026-09-15 gura：`Contains` 為真**不代表值不是 null** —— Plurk 的 alert 在
            //   「@ 在噗本體」那種通知上會給 `"response_id": null`，這裡就 NullRef。
            //   ⚠ 守了「這個鍵在不在」，沒守「值是不是 null」：兩者在呼叫端長得一樣。
            var aNode = iNode[iKey];
            if (aNode == null) return "";
            string aRaw = (aNode.ToJson() ?? "").Trim();
            if (aRaw.Length >= 2 && aRaw[0] == '"' && aRaw[aRaw.Length - 1] == '"')
                aRaw = aRaw.Substring(1, aRaw.Length - 2);
            return aRaw == "null" ? "" : aRaw;
        }

        // 極簡取值：只為了從回應撈幾個純量欄位，不值得為它引入完整反序列化。
        // 🩸 首版用 `GetString()`，而 Plurk 的 `id` / `karma` / `plurk_id` 是**數字** ⇒ 一律回空字串。
        //   whoami 印成 `id:` 空白時只是難看，但同一支函式也負責撈 **post 回傳的 plurk_id** ——
        //   那會讓 audit 記成 `?`：**噗發出去了，而帳上找不到它是哪一則**（不可回復動作最不該的失敗）。
        //   ⇒ 改成走 `ToJson()` 再剝引號：型別不論字串或數字都拿得到值。
        public static string? PickJsonValue(string? iJson, string iKey)
        {
            if (string.IsNullOrEmpty(iJson)) return null;
            try
            {
                var aJd = SafeParse(iJson);
                if (aJd == null || !aJd.Contains(iKey)) return null;
                var aNode = aJd[iKey];
                if (aNode == null) return null;
                string aRaw = (aNode.ToJson() ?? "").Trim();
                if (aRaw.Length >= 2 && aRaw[0] == '"' && aRaw[aRaw.Length - 1] == '"')
                    aRaw = aRaw.Substring(1, aRaw.Length - 2);
                return aRaw.Length == 0 || aRaw == "null" ? null : aRaw;
            }
            catch { return null; }
        }

        // 從 `{"plurk":{...},"user":{...}}` 取出內層物件的原始 JSON，讓 PickJsonValue 能繼續用。
        // 極簡實作：只服務本檔這一個回應形狀，不做通用 JSON 導覽。
        public static string ExtractObject(string iJson, string iKey)
        {
            try
            {
                var aJd = SafeParse(iJson);
                if (aJd == null || !aJd.Contains(iKey)) return iJson;
                var aNode = aJd[iKey];
                return aNode == null ? iJson : aNode.ToJson();
            }
            catch { return iJson; }
        }

        // 區塊職責：把 JSON 的 \uXXXX 轉義還原成字元
        // 物理意義：`JsonData.ToJson()` 會轉義非 ASCII ⇒ 回讀中文內容印出來是一串 引用…，
        //          人看不出那是不是自己發的那段。
        // 🩸 2026-08-21 op=get 首跑就撞到：**驗證輸出讀不懂，等於只驗了一半** ——
        //   我能證明「有東西在那裡」，但不能證明「在那裡的是我那段」。
        // 數值影響：只影響顯示；解析不出來就原樣留著（不吞掉）。
        // 🩸 2026-08-23 同族第二隻：本函式原本**只**處理 \uXXXX，於是 `content_raw` 裡的換行
        //   回來是**字面兩個字元**（反斜線 ＋ n）。timeline 那張表的欄位叫「內容首行」，
        //   而 FirstLine 找的是真正的換行字元 ⇒ 切不到 ⇒ 那一格印的其實是整段的前 40 字。
        //   **欄位名說「首行」而內容不是首行** —— 名字比事實大，而且不會報錯。
        //   ⇒ 修在這一層而不是修 timeline：`op=get` 的「content_raw 首行」是同一個病，
        //     只修看得見的那半邊就是又留一隻同族的下一個。
        public static string UnescapeJson(string? iText)
        {
            if (string.IsNullOrEmpty(iText)) return iText ?? "";
            var sb = new StringBuilder(iText.Length);
            for (int i = 0; i < iText.Length; i++)
            {
                if (iText[i] != '\\' || i + 1 >= iText.Length) { sb.Append(iText[i]); continue; }
                char aNext = iText[i + 1];
                if (aNext == 'u' && i + 5 < iText.Length
                    && int.TryParse(iText.Substring(i + 2, 4),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out int aCode))
                {
                    sb.Append((char)aCode);
                    i += 5;
                    continue;
                }
                switch (aNext)
                {
                    case 'n': sb.Append('\n'); i++; break;
                    case 'r': sb.Append('\r'); i++; break;
                    case 't': sb.Append('\t'); i++; break;
                    case '"': sb.Append('"'); i++; break;
                    case '/': sb.Append('/'); i++; break;
                    case '\\': sb.Append('\\'); i++; break;
                    // 認不得的轉義**原樣留著**（含那個反斜線）—— 吞掉的話它會變成一個
                    // 「看起來正常但少了一個字元」的字串，而那比看得見的怪符號難查十倍
                    default: sb.Append(iText[i]); break;
                }
            }
            return sb.ToString();
        }

        public static string Trunc(string? iText, int iLen)
            => string.IsNullOrEmpty(iText) ? "" : (iText!.Length <= iLen ? iText : iText.Substring(0, iLen) + "…");

        public static string FirstLine(string? iText)
        {
            if (string.IsNullOrEmpty(iText)) return "";
            int i = iText!.IndexOf('\n');
            return i < 0 ? iText : iText.Substring(0, i);
        }

        public static string OneLine(string? iText)
            => string.IsNullOrEmpty(iText) ? "" : iText!.Replace("\r", " ").Replace("\n", " ");
    }
}
