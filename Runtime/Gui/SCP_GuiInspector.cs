// 區塊職責：把任何物件**自動畫成可編輯的介面**（反射 ＋ 同一份 SCP_TypeSchema）。
// 物理意義：概念取自 Unity 端那套 GUILayout 自動 inspector，但簡化到只做一件事：
//           「schema 說這個成員是 Bool ⇒ 畫 Toggle；是 Integer ⇒ 畫可解析的輸入框」。
//           ⭐ 值錢的地方不是省下手寫頁面碼，是**它與序列化吃同一份分類**
//           （SCP_JsonMapper 也吃 schema）⇒ 「畫得出來」與「存得進去」不會分岔。
//           分岔的症狀是某個欄位改了之後回不來，而沒有任何一層會報錯。
// 數值影響：直接改傳進來的物件（immediate mode：每輪讀現值畫出來、把輸入寫回去）。零 IO。
// 🩸 四條判準：
//   ① **不支援的成員照樣畫一行**（灰字 ＋ 原因）—— 消失的欄位讓人以為資料沒有那一格。
//   ② **解析失敗不寫入、不清空使用者打的字**，畫一行說「沒有寫入，現值還是 X」。
//      靜默還原是「我打了字它自己跳回去」那種找不到人問的 bug。
//   ③ **struct 成員改完要寫回**（值型別是複本）—— 不寫回等於沒改，而且不報錯。
//   ④ 清單用索引當 id ⇒ **長度變了 id 會位移**。這件事本層擋不住（沒有穩定的項目鍵），
//      所以長度一變就畫一行警告，而不是假裝沒事。
//   ⑤ 巢狀／清單／字典一律走 SCP_Ui.Fold ⇒ **收合時子節點不建**（不是畫了再隱藏）：
//      深層物件收起來就真的不用付那棵樹的錢，而且四種驅動方式（視窗點標題／CLI --fold／
//      程式塞 Folds）都摺得起來。多層巢狀本來就支援（MaxDepth 預設 4，到底了會畫一行說明）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Reflect;

namespace SCP.Core.Gui
{
    public sealed class SCP_InspectorOptions
    {
        /// <summary>巢狀展開幾層（超過只畫一行說明，不是靜默停住）。</summary>
        public int MaxDepth { get; set; } = 4;

        /// <summary>只看不改（唯讀模式下不畫按鈕、不寫值）。</summary>
        public bool ReadOnly { get; set; }

        /// <summary>不支援的成員要不要畫出來。⚠ 關掉它就會讓欄位「消失」——預設開著。</summary>
        public bool ShowUnsupported { get; set; } = true;

        /// <summary>清單最多畫幾項（超過畫一行說「還有 N 項沒畫」——不是靜默截斷）。</summary>
        public int MaxListItems { get; set; } = 50;

        /// <summary>enum 選項少於等於這個數就畫成一排按鈕，多的話畫輸入框。</summary>
        public int ChoiceButtonLimit { get; set; } = 6;
    }

    /// <summary>這一輪自動繪製的結果。</summary>
    public sealed class SCP_InspectorResult
    {
        /// <summary>有沒有真的改到物件（呼叫端據此決定要不要存檔）。</summary>
        public bool Changed { get; internal set; }

        /// <summary>畫不出來／寫不進去的東西（畫面上也會有，這裡是給程式與 log 用的）。</summary>
        public List<string> Notes { get; } = new List<string>();
    }

    public static class SCP_GuiInspector
    {
        /// <summary>
        /// 自動畫一個物件。<paramref name="iKey"/> 是 id 前綴（**契約** —— agent 靠它下指令，
        /// 例：`--set cfg/scale=1.5`），所以請傳穩定的字串，不要傳畫面順序。
        /// </summary>
        public static SCP_InspectorResult Draw(SCP_Ui iUi, object iTarget, string iKey,
                                               SCP_InspectorOptions? iOptions = null)
        {
            if (iUi == null) throw new ArgumentNullException(nameof(iUi));
            var aResult = new SCP_InspectorResult();
            if (iTarget == null)
            {
                iUi.Note("(null) —— 沒有物件可畫");
                return aResult;
            }
            var aOpt = iOptions ?? new SCP_InspectorOptions();
            DrawObject(iUi, iTarget, iKey, aOpt, aResult, 0);
            return aResult;
        }

        static void DrawObject(SCP_Ui iUi, object iTarget, string iPath,
                               SCP_InspectorOptions iOpt, SCP_InspectorResult oResult, int iDepth)
        {
            SCP_TypeSchema aSchema = SCP_Reflect.SchemaOf(iTarget.GetType());
            if (aSchema.Members.Count == 0)
            {
                iUi.Note($"{iTarget.GetType().Name} 沒有公開成員可畫（private 成員本層刻意不收）");
                return;
            }

            foreach (SCP_MemberSchema m in aSchema.Members)
            {
                string aId = iPath + "/" + m.Name;
                switch (m.Kind)
                {
                    case SCP_ValueKind.Unsupported:
                        if (iOpt.ShowUnsupported)
                        {
                            iUi.Note($"⚠ {m.Name}（{m.Type.Name}）不支援：{m.UnsupportedReason}");
                            oResult.Notes.Add($"{aId}：{m.UnsupportedReason}");
                        }
                        break;

                    case SCP_ValueKind.Bool: DrawBool(iUi, iTarget, m, aId, iOpt, oResult); break;
                    case SCP_ValueKind.Integer:
                    case SCP_ValueKind.Decimal:
                    case SCP_ValueKind.Text: DrawScalarText(iUi, iTarget, m, aId, iOpt, oResult); break;
                    case SCP_ValueKind.Choice: DrawChoice(iUi, iTarget, m, aId, iOpt, oResult); break;
                    case SCP_ValueKind.ListOf: DrawList(iUi, iTarget, m, aId, iOpt, oResult, iDepth); break;
                    case SCP_ValueKind.MapOf: DrawMap(iUi, iTarget, m, aId, iOpt, oResult, iDepth); break;
                    case SCP_ValueKind.Nested: DrawNested(iUi, iTarget, m, aId, iOpt, oResult, iDepth); break;
                }
            }
        }

        // ── 純量 ──────────────────────────────────────────────────
        static void DrawBool(SCP_Ui iUi, object iOwner, SCP_MemberSchema iMember, string iId,
                             SCP_InspectorOptions iOpt, SCP_InspectorResult oResult)
        {
            object? aRaw = iMember.Get(iOwner);
            bool aOld = aRaw is bool b && b;
            bool aNew = iUi.Toggle(Label(iMember), aOld, iId);
            if (aNew == aOld || iOpt.ReadOnly || !iMember.CanWrite) return;
            Write(iOwner, iMember, aNew, iId, iUi, oResult);
        }

        static void DrawScalarText(SCP_Ui iUi, object iOwner, SCP_MemberSchema iMember, string iId,
                                   SCP_InspectorOptions iOpt, SCP_InspectorResult oResult)
        {
            object? aCurrent = iMember.Get(iOwner);
            string aOldText = ToText(aCurrent);
            string aNewText = iUi.TextField(Label(iMember), aOldText, iId);
            if (aNewText == aOldText || iOpt.ReadOnly || !iMember.CanWrite) return;

            bool aAllowNull = iMember.IsNullable || !iMember.Type.IsValueType;
            if (!SCP_Reflect.TryParse(iMember.Type, aNewText, aAllowNull, out object? aValue, out string aErr))
            {
                // ⭐ 不寫入、不清掉使用者打的字，但要說清楚現在的值還是舊的
                iUi.Note($"{iMember.Name}：{aErr} ⇒ 沒有寫入（現值還是 {aOldText}）");
                oResult.Notes.Add($"{iId}：{aErr}");
                return;
            }
            Write(iOwner, iMember, aValue, iId, iUi, oResult);
        }

        static void DrawChoice(SCP_Ui iUi, object iOwner, SCP_MemberSchema iMember, string iId,
                               SCP_InspectorOptions iOpt, SCP_InspectorResult oResult)
        {
            object? aCurrent = iMember.Get(iOwner);
            string aCurName = aCurrent?.ToString() ?? "";
            string[] aNames = Enum.GetNames(iMember.Type);

            if (aNames.Length <= iOpt.ChoiceButtonLimit)
            {
                using (iUi.Row())
                {
                    iUi.Label(Label(iMember) + "：");
                    foreach (string aName in aNames)
                    {
                        bool aIsCur = aName == aCurName;
                        // 現值標 ●（文字模式沒有選中狀態可看，所以要畫在字上）
                        if (iUi.Button((aIsCur ? "● " : "") + aName, iId + "=" + aName)
                            && !aIsCur && !iOpt.ReadOnly && iMember.CanWrite)
                        {
                            Write(iOwner, iMember, Enum.Parse(iMember.Type, aName), iId, iUi, oResult);
                        }
                    }
                }
                return;
            }

            string aNew = iUi.TextField(Label(iMember), aCurName, iId);
            if (aNew == aCurName || iOpt.ReadOnly || !iMember.CanWrite) return;
            if (!SCP_Reflect.TryParse(iMember.Type, aNew, false, out object? aVal, out string aErr))
            {
                iUi.Note($"{iMember.Name}：{aErr} ⇒ 沒有寫入（現值還是 {aCurName}）");
                oResult.Notes.Add($"{iId}：{aErr}");
                return;
            }
            Write(iOwner, iMember, aVal, iId, iUi, oResult);
        }

        // ── 巢狀 ──────────────────────────────────────────────────
        static void DrawNested(SCP_Ui iUi, object iOwner, SCP_MemberSchema iMember, string iId,
                               SCP_InspectorOptions iOpt, SCP_InspectorResult oResult, int iDepth)
        {
            object? aChild = iMember.Get(iOwner);

            using (SCP_Ui.FoldScope aFold = iUi.Fold(Label(iMember), iId))
            {
                if (!aFold.Open)
                {
                    // 收合 ⇒ **子節點根本不建**（畫了才隱藏等於沒摺，深樹照樣付整棵的錢）
                    return;
                }
                if (aChild == null)
                {
                    iUi.Note($"(null) —— {iMember.Type.Name}");
                    if (!iOpt.ReadOnly && iMember.CanWrite && SCP_Reflect.SchemaOf(iMember.Type).CanCreate
                        && iUi.Button("建立", iId + "/create"))
                    {
                        object? aNew = SCP_Reflect.TryCreate(iMember.Type, out string aErr);
                        if (aNew == null) { iUi.Note($"建不起來：{aErr}"); oResult.Notes.Add($"{iId}：{aErr}"); }
                        else Write(iOwner, iMember, aNew, iId, iUi, oResult);
                    }
                    return;
                }

                if (iDepth + 1 >= iOpt.MaxDepth)
                {
                    iUi.Note($"到達展開上限 {iOpt.MaxDepth} 層 —— 這一層以下沒有畫（不是沒有資料）");
                    oResult.Notes.Add($"{iId}：超過展開上限");
                    return;
                }

                var aInner = new SCP_InspectorResult();
                DrawObject(iUi, aChild, iId, iOpt, aInner, iDepth + 1);
                oResult.Notes.AddRange(aInner.Notes);

                if (!aInner.Changed) return;
                oResult.Changed = true;

                // 🩸 struct 是值 ⇒ 上面改的是複本，不寫回去等於沒改（而且不報錯）
                if (iMember.Type.IsValueType)
                {
                    if (!iMember.CanWrite)
                    {
                        iUi.Note($"{iMember.Name} 是唯讀的 struct —— 剛才的修改寫不回去");
                        oResult.Notes.Add($"{iId}：唯讀 struct，修改沒有生效");
                        return;
                    }
                    if (!iMember.TrySet(iOwner, aChild, out string aSetErr))
                    {
                        iUi.Note(aSetErr);
                        oResult.Notes.Add($"{iId}：{aSetErr}");
                    }
                }
            }
        }

        static void DrawList(SCP_Ui iUi, object iOwner, SCP_MemberSchema iMember, string iId,
                             SCP_InspectorOptions iOpt, SCP_InspectorResult oResult, int iDepth)
        {
            object? aRaw = iMember.Get(iOwner);
            var aList = aRaw as IList;

            using (SCP_Ui.FoldScope aFold = iUi.Fold(
                $"{Label(iMember)}（{(aList == null ? "null" : aList.Count.ToString(CultureInfo.InvariantCulture))}）", iId))
            {
                if (!aFold.Open) return;
                if (aList == null)
                {
                    iUi.Note($"(null) —— {iMember.Type.Name}");
                    if (!iOpt.ReadOnly && iMember.CanWrite && iUi.Button("建立", iId + "/create"))
                    {
                        object? aNew = SCP_Reflect.TryCreate(iMember.Type, out string aErr);
                        if (aNew == null) { iUi.Note($"建不起來：{aErr}"); oResult.Notes.Add($"{iId}：{aErr}"); }
                        else Write(iOwner, iMember, aNew, iId, iUi, oResult);
                    }
                    return;
                }

                Type aElem = iMember.ElementType!;
                SCP_ValueKind aElemKind = SCP_TypeSchema.Classify(aElem, out _, out string aElemReason);
                if (aElemKind == SCP_ValueKind.Unsupported)
                {
                    iUi.Note($"⚠ 元素型別不支援：{aElemReason}");
                    oResult.Notes.Add($"{iId}：元素 {aElemReason}");
                    return;
                }

                // ⚠ 索引式 id 的代價要講在畫面上：增刪之後同一個 id 指到的是別人
                iUi.Note("清單項目的 id 是索引 ⇒ 增刪之後後面每一項的 id 都位移（欄位值可能跟到隔壁；--reset 可清）");

                int aShow = Math.Min(aList.Count, iOpt.MaxListItems);
                for (int i = 0; i < aShow; i++)
                {
                    string aItemId = $"{iId}/{i}";
                    if (aElemKind == SCP_ValueKind.Nested)
                    {
                        object? aItem = aList[i];
                        using (SCP_Ui.FoldScope aItemFold = iUi.Fold($"[{i}]", aItemId))
                        {
                            if (!aItemFold.Open) continue;
                            if (aItem == null) { iUi.Note("(null)"); }
                            else if (iDepth + 1 >= iOpt.MaxDepth)
                            {
                                iUi.Note($"到達展開上限 {iOpt.MaxDepth} 層 —— 這一項沒有畫（不是空的）");
                            }
                            else
                            {
                                var aInner = new SCP_InspectorResult();
                                DrawObject(iUi, aItem, aItemId, iOpt, aInner, iDepth + 1);
                                oResult.Notes.AddRange(aInner.Notes);
                                if (aInner.Changed)
                                {
                                    oResult.Changed = true;
                                    if (aElem.IsValueType) aList[i] = aItem;   // struct 元素同樣要寫回
                                }
                            }
                            if (!iOpt.ReadOnly && iUi.Button("✕ 移除", aItemId + "/remove"))
                            {
                                aList.RemoveAt(i);
                                oResult.Changed = true;
                                return;      // 結構變了 ⇒ 這一輪不再往下畫（後面的 index 已經全部位移）
                            }
                        }
                        continue;
                    }

                    using (iUi.Row())
                    {
                        string aOldText = ToText(aList[i]);
                        string aNewText = iUi.TextField($"[{i}]", aOldText, aItemId);
                        if (aNewText != aOldText && !iOpt.ReadOnly)
                        {
                            if (SCP_Reflect.TryParse(aElem, aNewText, !aElem.IsValueType, out object? aVal, out string aErr))
                            {
                                aList[i] = aVal;
                                oResult.Changed = true;
                            }
                            else
                            {
                                iUi.Note($"[{i}]：{aErr} ⇒ 沒有寫入（現值還是 {aOldText}）");
                                oResult.Notes.Add($"{aItemId}：{aErr}");
                            }
                        }
                        if (!iOpt.ReadOnly && iUi.Button("✕", aItemId + "/remove"))
                        {
                            aList.RemoveAt(i);
                            oResult.Changed = true;
                            return;
                        }
                    }
                }

                if (aList.Count > aShow)
                    iUi.Note($"還有 {aList.Count - aShow} 項沒有畫（上限 {iOpt.MaxListItems}）—— 不是清單只有這麼多");

                if (!iOpt.ReadOnly && iUi.Button("＋ 新增", iId + "/add"))
                {
                    object? aNewItem = aElem == typeof(string)
                        ? ""
                        : SCP_Reflect.TryCreate(aElem, out string aErr) ?? Fail(iUi, oResult, iId, aErr);
                    if (aNewItem != null || !aElem.IsValueType)
                    {
                        aList.Add(aNewItem);
                        oResult.Changed = true;
                    }
                }
            }
        }

        static void DrawMap(SCP_Ui iUi, object iOwner, SCP_MemberSchema iMember, string iId,
                            SCP_InspectorOptions iOpt, SCP_InspectorResult oResult, int iDepth)
        {
            object? aRaw = iMember.Get(iOwner);
            var aMap = aRaw as IDictionary;

            using (SCP_Ui.FoldScope aFold = iUi.Fold(
                $"{Label(iMember)}（{(aMap == null ? "null" : aMap.Count.ToString(CultureInfo.InvariantCulture))}）", iId))
            {
                if (!aFold.Open) return;
                if (aMap == null) { iUi.Note($"(null) —— {iMember.Type.Name}"); return; }

                Type aVal = iMember.ElementType!;
                SCP_ValueKind aValKind = SCP_TypeSchema.Classify(aVal, out _, out string aValReason);
                if (aValKind == SCP_ValueKind.Unsupported)
                {
                    iUi.Note($"⚠ 值型別不支援：{aValReason}");
                    oResult.Notes.Add($"{iId}：值 {aValReason}");
                    return;
                }

                // key 用**資料本身的鍵**（不是索引）⇒ 增刪不會讓 id 位移。這是清單做不到的那一格。
                var aKeys = new List<string>();
                foreach (object? k in aMap.Keys) aKeys.Add(k as string ?? "");

                foreach (string k in aKeys)
                {
                    string aItemId = $"{iId}/{k}";
                    if (aValKind == SCP_ValueKind.Nested)
                    {
                        object? aItem = aMap[k];
                        using (SCP_Ui.FoldScope aItemFold = iUi.Fold(k, aItemId))
                        {
                            if (!aItemFold.Open) continue;
                            if (aItem == null) { iUi.Note("(null)"); continue; }
                            if (iDepth + 1 >= iOpt.MaxDepth)
                            {
                                iUi.Note($"到達展開上限 {iOpt.MaxDepth} 層 —— 這一項沒有畫（不是空的）");
                                continue;
                            }
                            var aInner = new SCP_InspectorResult();
                            DrawObject(iUi, aItem, aItemId, iOpt, aInner, iDepth + 1);
                            oResult.Notes.AddRange(aInner.Notes);
                            if (aInner.Changed)
                            {
                                oResult.Changed = true;
                                if (aVal.IsValueType) aMap[k] = aItem;
                            }
                        }
                        continue;
                    }

                    string aOldText = ToText(aMap[k]);
                    string aNewText = iUi.TextField(k, aOldText, aItemId);
                    if (aNewText == aOldText || iOpt.ReadOnly) continue;
                    if (SCP_Reflect.TryParse(aVal, aNewText, !aVal.IsValueType, out object? aParsed, out string aErr))
                    {
                        aMap[k] = aParsed;
                        oResult.Changed = true;
                    }
                    else
                    {
                        iUi.Note($"{k}：{aErr} ⇒ 沒有寫入（現值還是 {aOldText}）");
                        oResult.Notes.Add($"{aItemId}：{aErr}");
                    }
                }

                if (aKeys.Count == 0) iUi.Note("（空的）—— 本層不支援新增 key（要新增請走程式或 JSON）");
            }
        }

        // ── 雜項 ──────────────────────────────────────────────────
        static void Write(object iOwner, SCP_MemberSchema iMember, object? iValue, string iId,
                          SCP_Ui iUi, SCP_InspectorResult oResult)
        {
            if (!iMember.TrySet(iOwner, iValue, out string aErr))
            {
                iUi.Note(aErr);
                oResult.Notes.Add($"{iId}：{aErr}");
                return;
            }
            oResult.Changed = true;
        }

        static object? Fail(SCP_Ui iUi, SCP_InspectorResult oResult, string iId, string iErr)
        {
            iUi.Note($"新增失敗：{iErr}");
            oResult.Notes.Add($"{iId}：{iErr}");
            return null;
        }

        static string Label(SCP_MemberSchema iMember)
            => iMember.CanWrite ? iMember.Name : iMember.Name + "（唯讀）";

        /// <summary>值 → 顯示字串。⚠ null 印成 `(null)` 而不是空字串 —— 空字串會被當成「使用者清空了」。</summary>
        static string ToText(object? iValue)
        {
            if (iValue == null) return "";
            if (iValue is float f) return f.ToString("R", CultureInfo.InvariantCulture);
            if (iValue is double d) return d.ToString("R", CultureInfo.InvariantCulture);
            if (iValue is decimal m) return m.ToString(CultureInfo.InvariantCulture);
            return Convert.ToString(iValue, CultureInfo.InvariantCulture) ?? "";
        }
    }
}
