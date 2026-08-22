// 區塊職責：JSON 文字 → 值樹（遞迴下降 parser）。
// 物理意義：概念沿用 UCL_Core 的 parser，但重寫且零 Unity 耦合。
//           ⭐ 錯誤訊息一律帶 **行/列** —— 這些 json 有一半是人手改的設定檔，
//             「第 12 行第 8 欄少一個逗號」跟「解析失敗」是兩種完全不同的可用度。
// 數值影響：預設**容忍註解與尾逗號**（手改設定檔的現實），要嚴格就傳 iStrict=true。
//           數字**保留原文**不轉型 —— 轉一趟 double 再轉回來會把 12345678901234567 磨掉尾數。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 也要編這份）—— 不用 raw string、不用 record。
#nullable enable
using System;
using System.Globalization;
using System.Text;

namespace SCP.Core.Json
{
    public class SCP_JsonParseException : Exception
    {
        public int Line { get; private set; }
        public int Column { get; private set; }

        public SCP_JsonParseException(string iMessage, int iLine, int iColumn)
            : base("JSON 解析失敗（第 " + iLine.ToString(CultureInfo.InvariantCulture)
                   + " 行第 " + iColumn.ToString(CultureInfo.InvariantCulture) + " 欄）：" + iMessage)
        {
            Line = iLine;
            Column = iColumn;
        }
    }

    public static class SCP_JsonParser
    {
        public static SCP_JsonData Parse(string iJson, bool iStrict = false)
        {
            if (iJson == null) throw new ArgumentNullException(nameof(iJson));
            var aState = new State(iJson, iStrict);
            aState.SkipTrivia();
            SCP_JsonData aRoot = aState.ReadValue();
            aState.SkipTrivia();
            if (!aState.AtEnd) aState.Fail("根值之後還有多餘內容");
            return aRoot;
        }

        sealed class State
        {
            readonly string m_Text;
            readonly bool m_Strict;
            int m_Pos;
            int m_Line = 1;
            int m_Col = 1;

            public State(string iText, bool iStrict)
            {
                m_Text = iText;
                m_Strict = iStrict;
                // BOM 不是內容 —— 留著會讓「第一個字元不是 {」這種假錯誤出現
                if (m_Text.Length > 0 && m_Text[0] == '﻿') m_Pos = 1;
            }

            public bool AtEnd { get { return m_Pos >= m_Text.Length; } }

            char Cur { get { return m_Text[m_Pos]; } }

            void Advance()
            {
                if (m_Text[m_Pos] == '\n') { m_Line++; m_Col = 1; }
                else m_Col++;
                m_Pos++;
            }

            public void Fail(string iMessage) { throw new SCP_JsonParseException(iMessage, m_Line, m_Col); }

            /// <summary>吃掉空白，以及（非嚴格模式下）`//` 與 `/* */` 註解。</summary>
            public void SkipTrivia()
            {
                while (!AtEnd)
                {
                    char c = Cur;
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { Advance(); continue; }
                    if (c == '/' && m_Pos + 1 < m_Text.Length)
                    {
                        if (m_Strict) Fail("嚴格模式不允許註解");
                        char n = m_Text[m_Pos + 1];
                        if (n == '/')
                        {
                            while (!AtEnd && Cur != '\n') Advance();
                            continue;
                        }
                        if (n == '*')
                        {
                            Advance(); Advance();
                            while (true)
                            {
                                if (AtEnd) Fail("區塊註解沒有結尾 */");
                                if (Cur == '*' && m_Pos + 1 < m_Text.Length && m_Text[m_Pos + 1] == '/')
                                { Advance(); Advance(); break; }
                                Advance();
                            }
                            continue;
                        }
                    }
                    return;
                }
            }

            public SCP_JsonData ReadValue()
            {
                if (AtEnd) Fail("預期一個值，但已到結尾");
                char c = Cur;
                if (c == '{') return ReadObject();
                if (c == '[') return ReadArray();
                if (c == '"') return SCP_JsonData.NewString(ReadString());
                if (c == 't') { Expect("true"); return SCP_JsonData.NewBool(true); }
                if (c == 'f') { Expect("false"); return SCP_JsonData.NewBool(false); }
                if (c == 'n') { Expect("null"); return SCP_JsonData.NewNull(); }
                if (c == '-' || (c >= '0' && c <= '9')) return SCP_JsonData.NewNumber(ReadNumberRaw());
                Fail("認不得的值起始字元 '" + c + "'");
                return SCP_JsonData.NewNull();   // 到不了
            }

            SCP_JsonData ReadObject()
            {
                var aObj = SCP_JsonData.NewObject();
                Advance();                       // '{'
                SkipTrivia();
                if (!AtEnd && Cur == '}') { Advance(); return aObj; }
                while (true)
                {
                    SkipTrivia();
                    if (AtEnd) Fail("物件沒有結尾 }");
                    if (Cur != '"') Fail("物件的 key 必須是字串");
                    string aKey = ReadString();
                    SkipTrivia();
                    if (AtEnd || Cur != ':') Fail("key 之後必須是 ':'");
                    Advance();
                    SkipTrivia();
                    SCP_JsonData aVal = ReadValue();
                    // 重複 key：後到的覆蓋，但**嚴格模式視為錯誤** ——
                    // 同一個檔裡同一個 key 出現兩次，「哪一個生效」不該由 parser 的實作細節決定。
                    if (aObj.Contains(aKey))
                    {
                        if (m_Strict) Fail("物件裡有重複的 key：" + aKey);
                    }
                    aObj.Set(aKey, aVal);
                    SkipTrivia();
                    if (AtEnd) Fail("物件沒有結尾 }");
                    if (Cur == ',')
                    {
                        Advance();
                        SkipTrivia();
                        if (!AtEnd && Cur == '}')
                        {
                            if (m_Strict) Fail("嚴格模式不允許尾逗號");
                            Advance();
                            return aObj;
                        }
                        continue;
                    }
                    if (Cur == '}') { Advance(); return aObj; }
                    Fail("物件裡預期 ',' 或 '}'，讀到 '" + Cur + "'");
                }
            }

            SCP_JsonData ReadArray()
            {
                var aArr = SCP_JsonData.NewArray();
                Advance();                       // '['
                SkipTrivia();
                if (!AtEnd && Cur == ']') { Advance(); return aArr; }
                while (true)
                {
                    SkipTrivia();
                    if (AtEnd) Fail("陣列沒有結尾 ]");
                    aArr.Add(ReadValue());
                    SkipTrivia();
                    if (AtEnd) Fail("陣列沒有結尾 ]");
                    if (Cur == ',')
                    {
                        Advance();
                        SkipTrivia();
                        if (!AtEnd && Cur == ']')
                        {
                            if (m_Strict) Fail("嚴格模式不允許尾逗號");
                            Advance();
                            return aArr;
                        }
                        continue;
                    }
                    if (Cur == ']') { Advance(); return aArr; }
                    Fail("陣列裡預期 ',' 或 ']'，讀到 '" + Cur + "'");
                }
            }

            string ReadString()
            {
                Advance();                       // 開頭的 '"'
                var sb = new StringBuilder();
                while (true)
                {
                    if (AtEnd) Fail("字串沒有結尾的雙引號");
                    char c = Cur;
                    if (c == '"') { Advance(); return sb.ToString(); }
                    if (c == '\\')
                    {
                        Advance();
                        if (AtEnd) Fail("反斜線之後沒有字元");
                        char e = Cur;
                        Advance();
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
                                sb.Append(ReadHex4());
                                break;
                            default:
                                Fail("認不得的轉義 \\" + e);
                                break;
                        }
                        continue;
                    }
                    if (c < ' ' && m_Strict) Fail("字串裡有未轉義的控制字元");
                    sb.Append(c);
                    Advance();
                }
            }

            char ReadHex4()
            {
                if (m_Pos + 4 > m_Text.Length) Fail("\\u 後面不足四碼");
                int v = 0;
                for (int i = 0; i < 4; i++)
                {
                    char c = Cur;
                    int d;
                    if (c >= '0' && c <= '9') d = c - '0';
                    else if (c >= 'a' && c <= 'f') d = c - 'a' + 10;
                    else if (c >= 'A' && c <= 'F') d = c - 'A' + 10;
                    else { Fail("\\u 後面不是十六進位：'" + c + "'"); d = 0; }
                    v = v * 16 + d;
                    Advance();
                }
                // 代理對（surrogate pair）不特別處理：兩個 😀 各自是一個 char，
                // 依序 Append 就是正確的 UTF-16 序列。
                return (char)v;
            }

            string ReadNumberRaw()
            {
                int aStart = m_Pos;
                if (Cur == '-') Advance();
                while (!AtEnd && Cur >= '0' && Cur <= '9') Advance();
                if (!AtEnd && Cur == '.')
                {
                    Advance();
                    while (!AtEnd && Cur >= '0' && Cur <= '9') Advance();
                }
                if (!AtEnd && (Cur == 'e' || Cur == 'E'))
                {
                    Advance();
                    if (!AtEnd && (Cur == '+' || Cur == '-')) Advance();
                    while (!AtEnd && Cur >= '0' && Cur <= '9') Advance();
                }
                string aRaw = m_Text.Substring(aStart, m_Pos - aStart);
                if (aRaw.Length == 0 || aRaw == "-") Fail("數字格式不對");
                return aRaw;
            }

            void Expect(string iWord)
            {
                for (int i = 0; i < iWord.Length; i++)
                {
                    if (AtEnd || Cur != iWord[i]) Fail("預期 '" + iWord + "'");
                    Advance();
                }
            }
        }
    }
}
