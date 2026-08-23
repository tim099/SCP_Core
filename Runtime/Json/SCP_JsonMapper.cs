// 區塊職責：物件 ↔ SCP_JsonData 的自動對應（吃 SCP_TypeSchema 那份分類）。
// 物理意義：手寫 ToJson/FromJson 的東西每加一個欄位就要改兩處，而漏掉的那一處不會報錯 ——
//           症狀是「我改了設定，重開就不見了」。⇒ 讓成員清單只有一份（schema），兩個方向都吃它。
//           自動繪製（SCP_GuiInspector）吃的是同一份，所以「畫得出來」與「存得進去」不會分岔。
// 數值影響：純資料轉換，零 IO。
// 🩸 三條設計判準，每一條都是為了讓「資料悄悄消失」不可能發生：
//   ① **讀取端：JSON 裡沒有那個 key ⇒ 保留物件現有的值**，不是寫 0／null。
//      （「沒設過」與「設成 0」不得同形 —— 這也是 SCP_JsonData 對 Missing 的態度。）
//   ② **不支援的成員不靜默跳過** ⇒ 進 Diagnostics。沒有人看的警告至少查得到，
//      靜默略過的欄位連查都無從查起。
//   ③ **型別不合就不寫**（記一筆），不做「盡力而為」的轉換 ——
//      把 "abc" 塞進 int 變成 0，比整筆失敗難查十倍。
// ⚠ 不做多型：宣告成 interface／abstract 的成員在 Classify 就是 Unsupported。
//   要多型得寫型別標記，而那是另一個決定（猜錯的症狀是「存進去的是另一個型別的資料」）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Reflect;

namespace SCP.Core.Json
{
    /// <summary>對應過程的設定與紀錄。**Diagnostics 是產物不是副作用** —— 呼叫端該把它印出來。</summary>
    public sealed class SCP_JsonMapOptions
    {
        /// <summary>遞迴上限。超過就停手並記一筆（循環參考另外用 visited 擋，這是「太深」的那道）。</summary>
        public int MaxDepth { get; set; } = 8;

        /// <summary>沒被處理的成員、型別不合、寫不進去 …… 都記在這裡。</summary>
        public List<string> Diagnostics { get; } = new List<string>();

        public void Note(string iPath, string iWhat) { Diagnostics.Add($"{iPath}：{iWhat}"); }
    }

    public static class SCP_JsonMapper
    {
        // ── 物件 → JSON ───────────────────────────────────────────
        /// <summary>把物件的公開成員寫成 JSON 物件。null ⇒ 回 JSON null。</summary>
        public static SCP_JsonData ToJson(object? iObj, SCP_JsonMapOptions? iOptions = null)
        {
            var aOpt = iOptions ?? new SCP_JsonMapOptions();
            return WriteValue(iObj, iObj?.GetType(), "$", aOpt, 0, new List<object>());
        }

        static SCP_JsonData WriteValue(object? iValue, Type? iDeclaredType, string iPath,
                                       SCP_JsonMapOptions iOpt, int iDepth, List<object> iStack)
        {
            if (iValue == null) return SCP_JsonData.NewNull();

            Type aType = iValue.GetType();
            SCP_ValueKind aKind = SCP_TypeSchema.Classify(aType, out Type? aElement, out string aReason);

            switch (aKind)
            {
                case SCP_ValueKind.Bool: return SCP_JsonData.NewBool((bool)iValue);
                case SCP_ValueKind.Text: return SCP_JsonData.NewString((string)iValue);
                case SCP_ValueKind.Choice: return SCP_JsonData.NewString(iValue.ToString() ?? "");
                case SCP_ValueKind.Integer:
                    return SCP_JsonData.NewNumber(Convert.ToInt64(iValue, CultureInfo.InvariantCulture));
                case SCP_ValueKind.Decimal:
                    return SCP_JsonData.NewNumber(Convert.ToDouble(iValue, CultureInfo.InvariantCulture));

                case SCP_ValueKind.ListOf:
                {
                    var aArr = SCP_JsonData.NewArray();
                    var aList = (IList)iValue;
                    for (int i = 0; i < aList.Count; i++)
                        aArr.Add(WriteValue(aList[i], aElement, $"{iPath}[{i}]", iOpt, iDepth + 1, iStack));
                    return aArr;
                }

                case SCP_ValueKind.MapOf:
                {
                    var aObj = SCP_JsonData.NewObject();
                    var aMap = (IDictionary)iValue;
                    foreach (object? aKey in aMap.Keys)
                    {
                        string k = aKey as string ?? "";
                        aObj.Set(k, WriteValue(aMap[aKey!], aElement, $"{iPath}.{k}", iOpt, iDepth + 1, iStack));
                    }
                    return aObj;
                }

                case SCP_ValueKind.Nested:
                {
                    if (iDepth >= iOpt.MaxDepth)
                    {
                        iOpt.Note(iPath, $"超過遞迴上限 {iOpt.MaxDepth} 層，這一段沒有寫出去");
                        return SCP_JsonData.NewNull();
                    }
                    // 循環參考：同一個實例在自己的祖先鏈上出現過 ⇒ 停手並記一筆。
                    // 不擋的話是 stack overflow —— 而那個崩潰的訊息不會告訴你是哪個欄位。
                    if (!aType.IsValueType)
                    {
                        for (int i = 0; i < iStack.Count; i++)
                            if (ReferenceEquals(iStack[i], iValue))
                            {
                                iOpt.Note(iPath, $"循環參考（{aType.Name} 指回自己的祖先），這一段沒有寫出去");
                                return SCP_JsonData.NewNull();
                            }
                        iStack.Add(iValue);
                    }

                    var aNode = SCP_JsonData.NewObject();
                    SCP_TypeSchema aSchema = SCP_Reflect.SchemaOf(aType);
                    foreach (SCP_MemberSchema m in aSchema.Members)
                    {
                        string aMemberPath = $"{iPath}.{m.Name}";
                        if (m.Kind == SCP_ValueKind.Unsupported)
                        {
                            iOpt.Note(aMemberPath, $"沒有寫出去（{m.UnsupportedReason}）");
                            continue;
                        }
                        object? aMemberValue;
                        try { aMemberValue = m.Get(iValue); }
                        catch (Exception e)
                        {
                            iOpt.Note(aMemberPath, $"讀取失敗：{e.GetType().Name}");
                            continue;
                        }
                        aNode.Set(m.Name, WriteValue(aMemberValue, m.Type, aMemberPath, iOpt, iDepth + 1, iStack));
                    }

                    if (!aType.IsValueType) iStack.RemoveAt(iStack.Count - 1);
                    return aNode;
                }

                default:
                    iOpt.Note(iPath, $"沒有寫出去（{aReason}）");
                    return SCP_JsonData.NewNull();
            }
        }

        // ── JSON → 物件 ───────────────────────────────────────────
        /// <summary>
        /// 把 JSON 填進**既有實例**（不 new 根物件 —— 呼叫端手上那個就是要被改的那個）。
        /// <para>⚠ JSON 缺的 key **保留原值**。這是刻意的：那是「沒設過」，不是「設成 0」。</para>
        /// </summary>
        public static void Populate(object iTarget, SCP_JsonData? iData, SCP_JsonMapOptions? iOptions = null)
        {
            if (iTarget == null) throw new ArgumentNullException(nameof(iTarget));
            var aOpt = iOptions ?? new SCP_JsonMapOptions();
            if (iData == null || !iData.Exists) { aOpt.Note("$", "資料不存在，物件沒有被改動"); return; }
            PopulateObject(iTarget, iData, "$", aOpt, 0);
        }

        static void PopulateObject(object iTarget, SCP_JsonData iData, string iPath,
                                   SCP_JsonMapOptions iOpt, int iDepth)
        {
            Type aType = iTarget.GetType();
            SCP_TypeSchema aSchema = SCP_Reflect.SchemaOf(aType);

            foreach (SCP_MemberSchema m in aSchema.Members)
            {
                string aPath = $"{iPath}.{m.Name}";

                if (m.Kind == SCP_ValueKind.Unsupported)
                {
                    if (iData[m.Name].Exists) iOpt.Note(aPath, $"JSON 裡有這個 key 但沒有讀進來（{m.UnsupportedReason}）");
                    continue;
                }

                SCP_JsonData aNode = iData[m.Name];
                if (!aNode.Exists) continue;      // ⭐ 缺 key ⇒ 保留原值（不是寫 0）

                ReadInto(iTarget, m, aNode, aPath, iOpt, iDepth);
            }
        }

        static void ReadInto(object iOwner, SCP_MemberSchema iMember, SCP_JsonData iNode,
                             string iPath, SCP_JsonMapOptions iOpt, int iDepth)
        {
            if (iNode.IsNull)
            {
                if (!iMember.CanWrite) { iOpt.Note(iPath, "JSON 是 null 但這個成員唯讀"); return; }
                if (iMember.Type.IsValueType && !iMember.IsNullable)
                {
                    iOpt.Note(iPath, $"JSON 是 null 但 {iMember.Type.Name} 不接受 null ⇒ 保留原值");
                    return;
                }
                if (!iMember.TrySet(iOwner, null, out string aErr0)) iOpt.Note(iPath, aErr0);
                return;
            }

            switch (iMember.Kind)
            {
                case SCP_ValueKind.Bool:
                case SCP_ValueKind.Integer:
                case SCP_ValueKind.Decimal:
                case SCP_ValueKind.Text:
                case SCP_ValueKind.Choice:
                {
                    if (!TryReadScalar(iMember.Type, iNode, out object? aValue, out string aErr))
                    {
                        iOpt.Note(iPath, $"{aErr} ⇒ 保留原值");
                        return;
                    }
                    if (!iMember.CanWrite) { iOpt.Note(iPath, "唯讀，沒有寫入"); return; }
                    if (!iMember.TrySet(iOwner, aValue, out string aSetErr)) iOpt.Note(iPath, aSetErr);
                    return;
                }

                case SCP_ValueKind.ListOf:
                {
                    if (iNode.Type != SCP_JsonType.Array) { iOpt.Note(iPath, "JSON 不是陣列 ⇒ 保留原值"); return; }

                    object? aCurrent = iMember.Get(iOwner);
                    if (aCurrent == null)
                    {
                        aCurrent = SCP_Reflect.TryCreate(iMember.Type, out string aCErr);
                        if (aCurrent == null) { iOpt.Note(iPath, $"清單建不起來（{aCErr}）"); return; }
                        if (!iMember.TrySet(iOwner, aCurrent, out string aSErr)) { iOpt.Note(iPath, aSErr); return; }
                    }

                    var aList = (IList)aCurrent;
                    aList.Clear();          // 清單是**整份取代**（不是逐項合併）—— 合併語意在 JSON 陣列上表達不出來
                    Type aElem = iMember.ElementType!;
                    for (int i = 0; i < iNode.Count; i++)
                    {
                        if (!TryReadElement(aElem, iNode[i], $"{iPath}[{i}]", iOpt, iDepth, out object? aItem)) continue;
                        aList.Add(aItem);
                    }
                    return;
                }

                case SCP_ValueKind.MapOf:
                {
                    if (iNode.Type != SCP_JsonType.Object) { iOpt.Note(iPath, "JSON 不是物件 ⇒ 保留原值"); return; }

                    object? aCurrent = iMember.Get(iOwner);
                    if (aCurrent == null)
                    {
                        aCurrent = SCP_Reflect.TryCreate(iMember.Type, out string aCErr);
                        if (aCurrent == null) { iOpt.Note(iPath, $"字典建不起來（{aCErr}）"); return; }
                        if (!iMember.TrySet(iOwner, aCurrent, out string aSErr)) { iOpt.Note(iPath, aSErr); return; }
                    }

                    var aMap = (IDictionary)aCurrent;
                    aMap.Clear();
                    Type aVal = iMember.ElementType!;
                    foreach (string k in iNode.Keys)
                    {
                        if (!TryReadElement(aVal, iNode[k], $"{iPath}.{k}", iOpt, iDepth, out object? aItem)) continue;
                        aMap[k] = aItem;
                    }
                    return;
                }

                case SCP_ValueKind.Nested:
                {
                    if (iNode.Type != SCP_JsonType.Object) { iOpt.Note(iPath, "JSON 不是物件 ⇒ 保留原值"); return; }
                    if (iDepth >= iOpt.MaxDepth) { iOpt.Note(iPath, $"超過遞迴上限 {iOpt.MaxDepth} 層，沒有讀進來"); return; }

                    object? aChild = iMember.Get(iOwner);
                    if (aChild == null)
                    {
                        aChild = SCP_Reflect.TryCreate(iMember.Type, out string aCErr);
                        if (aChild == null) { iOpt.Note(iPath, $"建不起來（{aCErr}）"); return; }
                    }

                    PopulateObject(aChild, iNode, iPath, iOpt, iDepth + 1);

                    // 🩸 struct 是**值**：上面改的是 box 出來的那份複本，不寫回去就等於沒改，
                    //    而它不會報錯 —— 「我改了巢狀 struct，存檔後沒變」就是這一格。
                    if (iMember.Type.IsValueType || !ReferenceEquals(aChild, iMember.Get(iOwner)))
                    {
                        if (!iMember.CanWrite) { iOpt.Note(iPath, "唯讀，巢狀值寫不回去（struct 需要寫回）"); return; }
                        if (!iMember.TrySet(iOwner, aChild, out string aSetErr)) iOpt.Note(iPath, aSetErr);
                    }
                    return;
                }

                default:
                    iOpt.Note(iPath, $"沒有讀進來（{iMember.UnsupportedReason}）");
                    return;
            }
        }

        static bool TryReadElement(Type iType, SCP_JsonData iNode, string iPath,
                                   SCP_JsonMapOptions iOpt, int iDepth, out object? oValue)
        {
            oValue = null;
            SCP_ValueKind aKind = SCP_TypeSchema.Classify(iType, out _, out string aReason);
            switch (aKind)
            {
                case SCP_ValueKind.Bool:
                case SCP_ValueKind.Integer:
                case SCP_ValueKind.Decimal:
                case SCP_ValueKind.Text:
                case SCP_ValueKind.Choice:
                {
                    if (iNode.IsNull)
                    {
                        if (iType.IsValueType) { iOpt.Note(iPath, $"null 進不了 {iType.Name}，這一項跳過"); return false; }
                        return true;   // 參考型別 ⇒ null 是合法值
                    }
                    if (!TryReadScalar(iType, iNode, out oValue, out string aErr))
                    {
                        iOpt.Note(iPath, $"{aErr}，這一項跳過");
                        return false;
                    }
                    return true;
                }
                case SCP_ValueKind.Nested:
                {
                    if (iNode.Type != SCP_JsonType.Object) { iOpt.Note(iPath, "不是物件，這一項跳過"); return false; }
                    if (iDepth >= iOpt.MaxDepth) { iOpt.Note(iPath, "超過遞迴上限，這一項跳過"); return false; }
                    object? aItem = SCP_Reflect.TryCreate(iType, out string aCErr);
                    if (aItem == null) { iOpt.Note(iPath, $"建不起來（{aCErr}），這一項跳過"); return false; }
                    PopulateObject(aItem, iNode, iPath, iOpt, iDepth + 1);
                    oValue = aItem;
                    return true;
                }
                default:
                    iOpt.Note(iPath, $"這一項跳過（{aReason}）");
                    return false;
            }
        }

        /// <summary>純量讀取。⚠ 型別不合就失敗，**不做盡力而為的轉換**（"abc" → 0 比整筆失敗難查十倍）。</summary>
        static bool TryReadScalar(Type iType, SCP_JsonData iNode, out object? oValue, out string oError)
        {
            oValue = null;
            oError = "";
            try
            {
                if (iType == typeof(string))
                {
                    if (iNode.Type != SCP_JsonType.String) { oError = $"JSON 是 {iNode.Type} 不是字串"; return false; }
                    oValue = iNode.AsString();
                    return true;
                }
                if (iType == typeof(bool))
                {
                    if (iNode.Type != SCP_JsonType.Bool) { oError = $"JSON 是 {iNode.Type} 不是 bool"; return false; }
                    oValue = iNode.AsBool();
                    return true;
                }
                if (iType.IsEnum)
                {
                    // enum 存成名字（存數字的話，enum 改順序就會靜默變成另一個選項）
                    if (iNode.Type != SCP_JsonType.String) { oError = $"JSON 是 {iNode.Type} 不是 enum 名字"; return false; }
                    return SCP_Reflect.TryParse(iType, iNode.AsString(), false, out oValue, out oError);
                }
                if (iNode.Type != SCP_JsonType.Number) { oError = $"JSON 是 {iNode.Type} 不是數字"; return false; }
                oValue = Convert.ChangeType(iNode.AsDouble(), iType, CultureInfo.InvariantCulture);
                return true;
            }
            catch (Exception e)
            {
                oError = $"讀成 {iType.Name} 失敗：{e.GetType().Name}";
                return false;
            }
        }

        /// <summary>建一份新的並填進去（做不到回 null 並在 Diagnostics 說原因）。</summary>
        public static object? Create(Type iType, SCP_JsonData? iData, SCP_JsonMapOptions? iOptions = null)
        {
            var aOpt = iOptions ?? new SCP_JsonMapOptions();
            object? aObj = SCP_Reflect.TryCreate(iType, out string aErr);
            if (aObj == null) { aOpt.Note("$", aErr); return null; }
            Populate(aObj, iData, aOpt);
            return aObj;
        }
    }
}
