// 區塊職責：**非 UI 的操控介面** —— 查詢畫面上有哪些可互動元件、保存跨次操作的狀態。
// 物理意義：中間層讓「畫面」變成一棵可讀的樹；這一支讓「操作」也變成可寫的資料。
//           ⇒ 於是這套 UI 有三種驅動方式，而三者共用同一份頁面碼：
//             ① 人用 ImGui 視窗點      ② 文字輸出看畫面      ③ **程式／agent 下指令操作**
//           第三種是本檔存在的理由：agent 沒有眼睛也沒有滑鼠，但它有 id 和讀數。
// 數值影響：純資料（零 IO）—— 存檔／讀檔由呼叫端做，本層只負責「狀態 ↔ JSON」與「樹 → 可互動清單」。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 也要編這份）。
#nullable enable
using System.Collections.Generic;
using SCP.Core.Json;

namespace SCP.Core.Gui
{
    /// <summary>畫面上一個可互動元件的描述（給「看不見畫面的人」用的目錄）。</summary>
    public sealed class SCP_GuiElement
    {
        public string Id = "";
        public SCP_GuiNodeKind Kind;
        public string Label = "";
        public string Value = "";
        public bool On;

        /// <summary>怎麼操作它 —— 直接可以照抄的指令片段。</summary>
        public string HowTo
        {
            get
            {
                switch (Kind)
                {
                    case SCP_GuiNodeKind.Button: return "--click " + Id;
                    case SCP_GuiNodeKind.Toggle: return "--toggle " + Id;
                    case SCP_GuiNodeKind.TextField: return "--set " + Id + "=<值>";
                    default: return "";
                }
            }
        }
    }

    /// <summary>跨次操作要記住的東西（欄位值與勾選）。點擊是事件，不進狀態。</summary>
    public sealed class SCP_GuiState
    {
        public Dictionary<string, string> Fields = new Dictionary<string, string>();
        public Dictionary<string, bool> Toggles = new Dictionary<string, bool>();

        /// <summary>組成下一次 Draw 的輸入。iClickedId 是**這一次**的一次性事件。</summary>
        public SCP_GuiInput ToInput(string? iClickedId)
        {
            var aInput = new SCP_GuiInput { ClickedId = iClickedId };
            foreach (var kv in Fields) aInput.Fields[kv.Key] = kv.Value;
            foreach (var kv in Toggles) aInput.Toggles[kv.Key] = kv.Value;
            return aInput;
        }

        public SCP_JsonData ToJson()
        {
            var aRoot = SCP_JsonData.NewObject();
            var aFields = SCP_JsonData.NewObject();
            foreach (var kv in Fields) aFields.Set(kv.Key, kv.Value);
            var aToggles = SCP_JsonData.NewObject();
            foreach (var kv in Toggles) aToggles.Set(kv.Key, kv.Value);
            aRoot.Set("fields", aFields);
            aRoot.Set("toggles", aToggles);
            return aRoot;
        }

        /// <summary>從 JSON 復原。**檔案不存在時由呼叫端傳 null 進來拿一份空的**（不要在這裡碰檔案）。</summary>
        public static SCP_GuiState FromJson(SCP_JsonData? iData)
        {
            var aState = new SCP_GuiState();
            if (iData == null || !iData.Exists) return aState;

            var aFields = iData["fields"];
            if (aFields.Exists)
                foreach (string k in aFields.Keys) aState.Fields[k] = aFields[k].AsString();

            var aToggles = iData["toggles"];
            if (aToggles.Exists)
                foreach (string k in aToggles.Keys) aState.Toggles[k] = aToggles[k].AsBool();

            return aState;
        }
    }

    public static class SCP_GuiQuery
    {
        /// <summary>把樹裡所有可互動元件攤平成清單（順序＝畫面上的順序）。</summary>
        public static List<SCP_GuiElement> Interactive(SCP_GuiNode iRoot)
        {
            var aList = new List<SCP_GuiElement>();
            Walk(iRoot, aList);
            return aList;
        }

        static void Walk(SCP_GuiNode iNode, List<SCP_GuiElement> oList)
        {
            if (iNode.Kind == SCP_GuiNodeKind.Button
                || iNode.Kind == SCP_GuiNodeKind.Toggle
                || iNode.Kind == SCP_GuiNodeKind.TextField)
            {
                oList.Add(new SCP_GuiElement
                {
                    Id = iNode.Id,
                    Kind = iNode.Kind,
                    Label = iNode.Text,
                    Value = iNode.Value,
                    On = iNode.On,
                });
            }
            foreach (var c in iNode.Children) Walk(c, oList);
        }

        /// <summary>
        /// 這個 id 存在嗎。⚠ 呼叫端**必須**檢查：對一個不存在的 id 下指令若靜默成功，
        /// 「我按了但沒反應」與「我按錯了」就同形，而那是最難查的一種。
        /// </summary>
        public static SCP_GuiElement? Find(SCP_GuiNode iRoot, string iId)
        {
            foreach (var e in Interactive(iRoot)) if (e.Id == iId) return e;
            return null;
        }

        /// <summary>整棵樹轉 JSON（給程式讀畫面用；文字輸出是給人看的）。</summary>
        public static SCP_JsonData ToJson(SCP_GuiNode iNode)
        {
            var aObj = SCP_JsonData.NewObject();
            aObj.Set("kind", iNode.Kind.ToString());
            if (iNode.Id.Length > 0) aObj.Set("id", iNode.Id);
            if (iNode.Text.Length > 0) aObj.Set("text", iNode.Text);
            if (iNode.Value.Length > 0) aObj.Set("value", iNode.Value);
            if (iNode.Kind == SCP_GuiNodeKind.Toggle) aObj.Set("on", iNode.On);
            if (iNode.Headers.Count > 0)
            {
                var aHeaders = SCP_JsonData.NewArray();
                foreach (string h in iNode.Headers) aHeaders.Add(h);
                aObj.Set("headers", aHeaders);
            }
            if (iNode.Children.Count > 0)
            {
                var aKids = SCP_JsonData.NewArray();
                foreach (var c in iNode.Children) aKids.Add(ToJson(c));
                aObj.Set("children", aKids);
            }
            return aObj;
        }
    }
}
