// 區塊職責：`senate cmd invoke` —— 用反射呼叫**這個 process 裡**的 public static／instance 成員，
//           不必為每一支內部 API 都寫一支專用 Cmd。**原生**，不需要 Unity。
// 物理意義：本檔是 args dispatch 的薄層；解析與呼叫全在 `SCP_Invoker`（`Runtime/Reflect/`），
//           而型別解析與字串→值又再往下走 `SCP_Reflect` ⇒ **三層各一個職責，沒有第二套解析器**。
// 數值影響：副作用**完全由被呼叫的那支 API 決定** —— 它寫檔就寫檔、它扣錢就扣錢。
//           ⛔ 本 Cmd 不做白名單：一份「安全成員」清單永遠不夠用，
//           而它最大的作用是讓人以為已經擋住了。
//
// 🔴 **射程，先說，因為它跟 Unity 端那支同名而受詞不同**：
//   Unity 端的 `Cmd_Invoke` 反射的是 **Unity Editor 的 API**（`CompilationPipeline` 那些）。
//   ⛔ 本支反射的是 **`senate.exe` 這個 process 已載入的組件**（`Senate.Core` / `SCP_Core` / BCL）——
//   在這裡打 `UnityEditor.*` 一定找不到，而那**不是 bug**。
//
// @doc-sync: <Senate>/Docs/API/Cli_Reference.md（`cmd` 節「反射呼叫本 process 的成員：invoke」＋ exit code 表下方那段 5 vs 70）
//
// ⚠ 第二個結構性差異（會咬人，寫在 Details 裡給使用者看）：
//   Unity 那側的變數表是 static ＋ 常駐 Editor ⇒ 跨 Cmd 呼叫存活。
//   **CLI 每次呼叫都是新 process** ⇒ 變數只活在**同一次呼叫**內 ⇒ 鏈式呼叫走 `steps`。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using SCP.Core.Json;
using SCP.Core.Reflect;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Invoke : SCP_Cmd
    {
        public override string Name => "invoke";

        public override string Summary => "反射呼叫本 process 已載入組件的成員（讀一個值／按一下一支 API）";

        public override string Details =>
            "把「型別名＋成員名＋引數」變成一次真的呼叫 —— 取一個內部讀數、或觸發一支沒有專用 Cmd 的 API。\n"
            + "\n"
            + "🔴 **射程**：反射的是 **`senate.exe` 這個 process 已載入的組件**（`Senate.Core` / `SCP_Core` / BCL）。\n"
            + "　 ⛔ 打 `UnityEditor.*` / `UnityEngine.*` 在這裡一定找不到 —— 那要走 Unity 端的 `ucmd run Invoke`，\n"
            + "　 兩支同名而**受詞不同**。\n"
            + "\n"
            + "⚠ **變數只活在同一次呼叫內**。Unity 那側是常駐 process 所以 `storeAs` 跨呼叫存活；\n"
            + "　 CLI 每次都是新 process ⇒ 分兩次跑的話 `$var` 永遠找不到。\n"
            + "　 ⇒ 要鏈式呼叫（拿到物件再呼叫它的成員）**把每一步寫在同一份 `steps` 裡**。\n"
            + "\n"
            + "**steps 格式**：一行一步，`k=v|k=v`。⚠ 分隔符是 `|` 不是 `;` —— `args` 自己用 `;` 分隔多個引數。\n"
            + "　 ⭐ **steps 內的鍵 camelCase 也收**（`paramTypes` ＝ `param_types`）⇒ Unity 端的範例可以直接貼進來。\n"
            + "　 ⛔ 而**單步的 `--arg` 只收 snake_case** —— 那條路上有 ArgSpec 預檢，打 `paramTypes` 會被擋下並印出合法清單。\n"
            + "　 📌 兩條路不同**不是疏漏**：被擋下來比靜默取預設好（TASK-0109 那族），所以這裡不加別名去消掉那道閘。\n"
            + "　 `#` 開頭與空行略過。任一步失敗就停（後面多半依賴前一步的值，硬跑只會蓋掉真正的第一個成因）。\n"
            + "\n"
            + "**回傳三態**（`value_kind`）：`void`（沒有回傳值）／`null`（有回傳值而它是 null）／`value`（有值，**可能是空字串**）。\n"
            + "　 🩸 判準是這一欄，⛔ 不是「`value` 有沒有出現」—— 空字串與 null 在這支上都是常見的真實回答。\n"
            + "\n"
            + "**離開碼**：`2` ＝ 用法錯（參數／找不到型別或成員／多載無法判定）；\n"
            + "　 `5` ＝ **被呼叫的程式自己丟了例外**。⚠ 兩者刻意分開：前者改這一行指令，後者去修那支 API。\n"
            + "\n"
            + "⛔ **沒有白名單**：能呼叫什麼由「這個 process 載入了什麼」決定。它寫檔就會真的寫檔。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("invoke --arg type=SCP.Core.Reflect.SCP_Reflect --arg member=Describe");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("type", "完整型別名（FullName，大小寫敏感）。短名唯一命中也收；撞名會列出候選要你改用 FullName。有 target 時可省略"),
            new SCP_CmdArgSpec("member", "成員名，大小寫敏感。單步模式必填（用 steps 時寫在每一行裡）"),
            new SCP_CmdArgSpec("kind", "method（預設）／property／field", false, "method", new[] { "method", "property", "field" }),
            new SCP_CmdArgSpec("param_types", "多載消歧用的參數型別全名，`;` 分隔"),
            new SCP_CmdArgSpec("args", "引數，`;` 分隔。`$name` 取前一步 store_as 存的值。尾巴沒給的用該參數的預設值補"),
            new SCP_CmdArgSpec("getter", "property/field：1 讀（預設）／0 寫（此時 args 要剛好一個，就是要寫進去的值）"),
            new SCP_CmdArgSpec("non_public", "1 ＝ 也搜 internal／private 成員（預設只搜 public）"),
            new SCP_CmdArgSpec("target", "$name ＝ 改成 instance 呼叫，實例取自本次執行的變數表"),
            new SCP_CmdArgSpec("store_as", "把回傳值存進本次執行的變數表，供後面的步驟 `$name` 引用"),
            new SCP_CmdArgSpec("steps", "多步：一行一步、`k=v|k=v`。給了它就**忽略上面的單步參數**（走長文請用 --arg-file）"),
            new SCP_CmdArgSpec("assembly", "先載入這些組件再解析型別，`;` 分隔。⚠ .NET 是用到才載 ⇒「找不到型別」有時只是它還沒被碰過"),
            new SCP_CmdArgSpec("out_json", "把每一步的完整結果落成 JSON 的路徑（巢狀資料走檔案，不進 values）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            var aResult = new SCP_CmdResult();

            // ── 1) 先把要求的組件載進來 ────────────────────────────────
            // ⚠ 載完一定要清 SCP_Reflect 的快取：`AllTypes()` 第一次呼叫就把清單記起來了，
            //   不清的話新載入的組件**在這一輪永遠看不到**，而症狀是「我明明載了它還是說找不到」。
            string aAsmArg = iArgs.Get("assembly");
            var aLoaded = new List<string>();
            if (aAsmArg.Length > 0)
            {
                foreach (string aName in aAsmArg.Split(';'))
                {
                    string a = aName.Trim();
                    if (a.Length == 0) continue;
                    try
                    {
                        Assembly aAsm = Assembly.Load(a);
                        aLoaded.Add(aAsm.GetName().Name ?? a);
                    }
                    catch (Exception e)
                    {
                        return SCP_CmdResult.Fail(2,
                            "✗ 載不進組件 '" + a + "'：" + e.GetType().Name + "：" + e.Message,
                            "  ⚠ 這裡要的是**組件名**（如 `Senate.Core`），⛔ 不是檔案路徑、也不是型別名。");
                    }
                }
                if (aLoaded.Count > 0)
                {
                    SCP_Reflect.ClearCache();
                    aResult.Lines.Add("· 已載入組件：" + string.Join(", ", aLoaded) + "（並清掉型別快取）");
                }
            }

            // ── 2) 組步驟 ──────────────────────────────────────────────
            var aSteps = new List<SCP_InvokeStep>();
            string aStepsRaw = iArgs.Get("steps");

            if (aStepsRaw.Length > 0)
            {
                int aLineNo = 0;
                foreach (string aLine in aStepsRaw.Replace("\r\n", "\n").Split('\n'))
                {
                    aLineNo++;
                    if (!SCP_Invoker.TryParseStep(aLine, out SCP_InvokeStep? aStep, out string aErr))
                    {
                        if (aErr == "(comment)") continue;   // 空行／註解：這是宣告過的收錄條件，不出聲
                        return SCP_CmdResult.Fail(2,
                            "✗ steps 第 " + aLineNo.ToString(CultureInfo.InvariantCulture) + " 行解析失敗：" + aErr,
                            "  那一行：" + aLine.Trim(),
                            "  ⚠ 一行一步、`k=v|k=v`。分隔符是 `|` 不是 `;`（`;` 是 args 自己在用的）。");
                    }
                    if (aStep != null) aSteps.Add(aStep);
                }
                if (aSteps.Count == 0)
                    return SCP_CmdResult.Fail(2, "✗ steps 裡一步都沒有（整份都是空行或註解？）");
            }
            else
            {
                var aPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string aKey in new[] { "type", "member", "kind", "param_types", "args", "getter", "non_public", "target", "store_as" })
                {
                    string v = iArgs.Get(aKey);
                    if (v.Length > 0) aPairs[aKey] = v;
                }
                if (!SCP_Invoker.TryBuildStep(aPairs, "(單步)", out SCP_InvokeStep? aOne, out string aErr) || aOne == null)
                    return SCP_CmdResult.Fail(2, "✗ " + aErr);
                aSteps.Add(aOne);
            }

            // ── 3) 跑 ─────────────────────────────────────────────────
            IReadOnlyList<SCP_InvokeResult> aResults = SCP_Invoker.Run(aSteps, out IReadOnlyDictionary<string, object?> aVars);

            for (int i = 0; i < aResults.Count; i++)
            {
                SCP_InvokeStep aStep = aSteps[i];
                SCP_InvokeResult aRes = aResults[i];
                string aHead = aSteps.Count > 1
                    ? "[" + (i + 1).ToString(CultureInfo.InvariantCulture) + "/" + aSteps.Count.ToString(CultureInfo.InvariantCulture) + "] "
                    : "";
                string aWhat = (aStep.TypeName.Length > 0 ? aStep.TypeName : "$" + aStep.Target) + "." + aStep.MemberName;

                if (!aRes.Success)
                {
                    aResult.Lines.Add(aHead + "✗ " + aWhat);
                    foreach (string aL in aRes.Error.Replace("\r\n", "\n").Split('\n')) aResult.Lines.Add("  " + aL);
                    continue;
                }

                string aTail = aRes.ValueKind == "value"
                    ? " = " + aRes.ValueAsString
                    : aRes.ValueKind == "null" ? " ⇒ null" : " ⇒ (void)";
                aResult.Lines.Add(aHead + "✓ " + aWhat + "  〔" + (aRes.ValueType?.FullName ?? "(unknown)") + "〕" + aTail);
                if (aStep.StoreAs.Length > 0) aResult.Lines.Add("  ↳ 存成 $" + aStep.StoreAs + "（只活到這次呼叫結束）");
            }

            // ── 4) 讀數：最後一步的三態（⛔ 不把中間步驟壓成一個值）────────
            SCP_InvokeResult aLast = aResults.Count > 0 ? aResults[aResults.Count - 1] : SCP_InvokeResult.Fail("沒有任何步驟");
            aResult.AddValue("steps", aSteps.Count.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("ran", aResults.Count.ToString(CultureInfo.InvariantCulture));
            aResult.AddValue("value_kind", aLast.Success ? aLast.ValueKind : "(failed)");
            aResult.AddValue("value_type", aLast.ValueType?.FullName ?? "(unknown)");
            aResult.AddValue("is_null", aLast.Success && aLast.ValueKind == "null" ? "1" : "0");
            if (aLast.Success && aLast.ValueKind == "value")
                aResult.AddValue("value", aLast.ValueAsString);

            // ── 5) out_json（巢狀走檔案）────────────────────────────────
            string aOutJson = iArgs.Get("out_json");
            if (aOutJson.Length > 0)
            {
                try
                {
                    SCP_JsonData aDoc = BuildJson(aSteps, aResults, aVars, aLoaded);
                    string? aDir = Path.GetDirectoryName(Path.GetFullPath(aOutJson));
                    if (!string.IsNullOrEmpty(aDir) && !Directory.Exists(aDir)) Directory.CreateDirectory(aDir!);
                    File.WriteAllText(aOutJson, SCP_JsonWriter.Write(aDoc));
                    aResult.AddOutput(Path.GetFullPath(aOutJson));
                }
                catch (Exception e)
                {
                    // ⚠ 寫不出 JSON **不改變呼叫本身的結果** —— 那一步已經真的跑過了（可能已經有副作用）。
                    //   ⛔ 不能因為報告寫不出來就把它報成「沒跑」。
                    aResult.Lines.Add("⚠ out_json 寫入失敗（⛔ 而上面那些呼叫**已經執行過了**）："
                                      + e.GetType().Name + "：" + e.Message);
                }
            }

            // ── 6) 離開碼 ─────────────────────────────────────────────
            if (aLast.Success && aResults.Count == aSteps.Count) return aResult;

            aResult.ExitCode = aLast.TargetThrew ? 5 : 2;
            if (aResults.Count < aSteps.Count)
                aResult.Lines.Add("⛔ 第 " + aResults.Count.ToString(CultureInfo.InvariantCulture) + " 步失敗後停下 —— 還有 "
                                  + (aSteps.Count - aResults.Count).ToString(CultureInfo.InvariantCulture)
                                  + " 步**沒有執行**（⚠ 不是執行了沒成功）。");
            if (aLast.TargetThrew)
                aResult.Lines.Add("📌 離開碼 5 ＝ 我找對了成員也按下去了，是**那支 API 自己丟了例外** ⇒ 要修的在那邊，不在這一行指令。");
            return aResult;
        }

        static SCP_JsonData BuildJson(IReadOnlyList<SCP_InvokeStep> iSteps,
                                      IReadOnlyList<SCP_InvokeResult> iResults,
                                      IReadOnlyDictionary<string, object?> iVars,
                                      IReadOnlyList<string> iLoaded)
        {
            SCP_JsonData aDoc = SCP_JsonData.NewObject();
            aDoc.Set("step_count", SCP_JsonData.NewNumber(iSteps.Count));
            aDoc.Set("ran_count", SCP_JsonData.NewNumber(iResults.Count));

            SCP_JsonData aAsm = SCP_JsonData.NewArray();
            foreach (string s in iLoaded) aAsm.Add(SCP_JsonData.NewString(s));
            aDoc.Set("loaded_assemblies", aAsm);

            SCP_JsonData aArr = SCP_JsonData.NewArray();
            for (int i = 0; i < iSteps.Count; i++)
            {
                SCP_InvokeStep s = iSteps[i];
                SCP_JsonData o = SCP_JsonData.NewObject();
                o.Set("index", SCP_JsonData.NewNumber(i + 1));
                o.Set("type", SCP_JsonData.NewString(s.TypeName));
                o.Set("member", SCP_JsonData.NewString(s.MemberName));
                o.Set("kind", SCP_JsonData.NewString(s.Kind));
                o.Set("target", SCP_JsonData.NewString(s.Target));
                o.Set("store_as", SCP_JsonData.NewString(s.StoreAs));
                o.Set("source_line", SCP_JsonData.NewString(s.SourceLine));

                if (i < iResults.Count)
                {
                    SCP_InvokeResult r = iResults[i];
                    o.Set("executed", SCP_JsonData.NewBool(true));
                    o.Set("success", SCP_JsonData.NewBool(r.Success));
                    o.Set("value_kind", SCP_JsonData.NewString(r.Success ? r.ValueKind : "(failed)"));
                    o.Set("value_type", SCP_JsonData.NewString(r.ValueType?.FullName ?? ""));
                    o.Set("target_threw", SCP_JsonData.NewBool(r.TargetThrew));
                    if (r.Success && r.ValueKind == "value") o.Set("value", SCP_JsonData.NewString(r.ValueAsString));
                    if (!r.Success) o.Set("error", SCP_JsonData.NewString(r.Error));
                }
                else
                {
                    // ⚠ 「沒跑」與「跑了而失敗」不可同形 —— 前面那一步停下之後，這些是**沒有執行**的。
                    o.Set("executed", SCP_JsonData.NewBool(false));
                }
                aArr.Add(o);
            }
            aDoc.Set("steps", aArr);

            SCP_JsonData aV = SCP_JsonData.NewArray();
            foreach (string k in iVars.Keys) aV.Add(SCP_JsonData.NewString(k));
            aDoc.Set("variables", aV);
            return aDoc;
        }
    }
}
