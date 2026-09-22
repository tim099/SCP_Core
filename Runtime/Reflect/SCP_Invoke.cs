// 區塊職責：反射**調用**層 —— 把字串描述（type / member / args）變成一次真的呼叫。
//           型別解析、字串→值、成員描述一律走 `SCP_Reflect`，⛔ 本檔不自己再做一套
//           （那會變成第二套會漂移的解析器，而漂移的症狀是「同一個字串在兩個入口解出不同型別」）。
// 物理意義：`SCP_Reflect` 有「找得到型別」與「字串變得成值」，缺的是「挑對多載並按下去」——
//           本檔補的就是那一段，外加一條**一次呼叫內**的變數鏈（`store_as` / `$name`）。
// 數值影響：副作用**完全由被呼叫的 API 決定**。本檔不做白名單（一份永遠不夠用的清單，
//           而它給人一種「已經擋住了」的錯覺）；能不能呼叫由宿主決定要不要註冊那支 Cmd。
//
// ⚠ 方言限制：C# 9 / netstandard2.1 —— **Unity 那側也要編這份**。
//   ⛔ 不要用 file-scoped namespace、list pattern 那些 C#10+ 的寫法（`src/` 底下可以，這裡不行）。
//
// 🔴 **與 Unity 端 `UCL_ReflectionInvoker` 的結構性差異，寫在最前面因為它會咬人**：
//   那邊的 `Variables` 是一張 **static 字典 + 常駐 Editor process** ⇒ 跨 Cmd 呼叫存活
//   （`storeAs` 存在第 1 次呼叫，`$var` 在第 2 次呼叫拿得到）。
//   ⛔ **CLI 每一次呼叫都是一個新的 process** ⇒ 照抄那個形狀的話，`$var` 永遠找不到，
//   而失敗訊息會長得像「變數名打錯了」。⇒ 本檔的變數表是**每次 Run 一張、跑完就沒**，
//   鏈式呼叫改由**一次呼叫內的多步**（`steps`）表達。
//   📌 那不只是將就：它同時拿掉了 Unity 那側「domain reload 會讓變數消失」那個坑。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace SCP.Core.Reflect
{
    /// <summary>一步呼叫的描述（字串層，還沒解析成 Type／MemberInfo）。</summary>
    public sealed class SCP_InvokeStep
    {
        /// <summary>完整型別名。有 <see cref="Target"/> 時可省略（改用該物件的實際型別）。</summary>
        public string TypeName = "";

        /// <summary>成員名。大小寫敏感。</summary>
        public string MemberName = "";

        /// <summary>method（預設）／property／field。</summary>
        public string Kind = "method";

        /// <summary>多載消歧用的參數型別全名。</summary>
        public List<string> ParamTypes = new List<string>();

        /// <summary>引數字串；`$name` 取本次執行的變數。</summary>
        public List<string> Args = new List<string>();

        /// <summary>property／field：true 讀（預設），false 寫（此時 Args[0] 是要寫入的值）。</summary>
        public bool IsGetter = true;

        /// <summary>也搜 internal／private 成員。</summary>
        public bool IncludeNonPublic;

        /// <summary>`$name`：把這一步變成 instance 呼叫，實例取自本次執行的變數表。</summary>
        public string Target = "";

        /// <summary>成功時把回傳值存進本次執行的變數表，供後續步驟 `$name` 引用。</summary>
        public string StoreAs = "";

        /// <summary>原始那一行（只為了錯誤訊息指得回去）。</summary>
        public string SourceLine = "";
    }

    /// <summary>一步的結果。⚠ 三態要兩兩可分，見 <see cref="ValueKind"/>。</summary>
    public sealed class SCP_InvokeResult
    {
        public bool Success;
        public string Error = "";
        public object? Value;
        public string ValueAsString = "";
        public Type? ValueType;

        /// <summary>
        /// `void`（沒有回傳值）／`null`（有回傳值而它是 null）／`value`（有值，**可能是空字串**）。
        /// 🩸 判準是這一欄不是「`value` 有沒有出現」——空字串與 null 在這支上都是常見的真實回答，
        /// 讓它們同形的話，呼叫端分不出「那個欄位是空的」與「那支 API 回 null」。
        /// </summary>
        public string ValueKind = "void";

        /// <summary>
        /// 失敗是**被呼叫的程式自己丟了例外**，⛔ 不是「我找錯了成員／參數給錯了」。
        /// 🩸 兩者的處置相反（去修那支 API ／ 改這一行指令），⛔ 而它們預設都只是「失敗」——
        /// 所以這一格是旗標，不讓呼叫端去比對錯誤訊息的字串（字串會被改，旗標不會）。
        /// </summary>
        public bool TargetThrew;

        public static SCP_InvokeResult Fail(string iError)
            => new SCP_InvokeResult { Success = false, Error = iError };

        public static SCP_InvokeResult FailFromTarget(string iError)
            => new SCP_InvokeResult { Success = false, Error = iError, TargetThrew = true };

        public static SCP_InvokeResult Ok(object? iValue, Type? iValueType)
        {
            bool aIsVoid = iValueType == typeof(void);
            bool aIsNull = !aIsVoid && iValue == null;
            return new SCP_InvokeResult
            {
                Success = true,
                Value = iValue,
                ValueType = iValueType,
                ValueKind = aIsVoid ? "void" : aIsNull ? "null" : "value",
                ValueAsString = aIsVoid || aIsNull ? "" : Stringify(iValue),
            };
        }

        /// <summary>
        /// 把回傳值變成一行字。集合印出**筆數**與前幾筆 —— ⚠ 一個 `System.Object[]` 的 ToString()
        /// 對呼叫端等於零讀數，而它看起來像成功。
        /// </summary>
        static string Stringify(object? iValue)
        {
            if (iValue == null) return "";
            if (iValue is string s) return s;

            if (iValue is System.Collections.IEnumerable aSeq && !(iValue is string))
            {
                var aItems = new List<string>();
                int aCount = 0;
                foreach (object? o in aSeq)
                {
                    aCount++;
                    if (aItems.Count < 20) aItems.Add(o?.ToString() ?? "null");
                }
                string aHead = string.Join(", ", aItems);
                // ⚠ 截斷要說出來 —— 「只有 20 筆」與「我只印了 20 筆」不可同形。
                string aTail = aCount > aItems.Count
                    ? "  … ⚠ 共 " + aCount.ToString(CultureInfo.InvariantCulture) + " 筆，這裡只印前 " + aItems.Count.ToString(CultureInfo.InvariantCulture)
                    : "";
                return "[" + aCount.ToString(CultureInfo.InvariantCulture) + "] " + aHead + aTail;
            }

            return iValue.ToString() ?? "";
        }
    }

    /// <summary>
    /// 字串描述 → 一次真的反射呼叫。型別解析與字串→值走 <see cref="SCP_Reflect"/>。
    /// <para>變數表**每次 <see cref="Run"/> 一張**（見檔頭：CLI 是一次性 process）。</para>
    /// </summary>
    public static class SCP_Invoker
    {
        // ── 解析 ──────────────────────────────────────────────────

        /// <summary>
        /// 解析一行 `k=v|k=v`。
        /// <para>⚠ 分隔符是 <b>`|`</b> 而不是 `;` —— 因為 `args` 自己用 `;` 分隔多個引數，
        /// 兩者用同一個符號的話 `args=a;b` 會被切成一個 `args=a` 加一個認不得的 `b`，
        /// 而那個 `b` 會被靜默丟掉（TASK-0109 那族：**未知鍵靜默取預設**）。</para>
        /// <para>⛔ 認不得的鍵**不靜默吞掉** —— 回 false ＋ 指名那個鍵。</para>
        /// </summary>
        public static bool TryParseStep(string iLine, out SCP_InvokeStep? oStep, out string oError)
        {
            oStep = null;
            oError = "";
            if (iLine == null) { oError = "空行"; return false; }

            string aTrim = iLine.Trim();
            if (aTrim.Length == 0 || aTrim.StartsWith("#", StringComparison.Ordinal))
            { oError = "(comment)"; return false; }

            var aPairs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string aSeg in aTrim.Split('|'))
            {
                string a = aSeg.Trim();
                if (a.Length == 0) continue;
                int aEq = a.IndexOf('=');
                if (aEq <= 0) { oError = "認不得的片段（缺 `=`）：" + a; return false; }
                aPairs[a.Substring(0, aEq).Trim()] = a.Substring(aEq + 1).Trim();
            }

            return TryBuildStep(aPairs, aTrim, out oStep, out oError);
        }

        /// <summary>
        /// 從已拆好的鍵值build 一步。
        /// <para>⚠ **camelCase 與 snake_case 兩種都收**（`paramTypes` / `param_types`）——
        /// Unity 端那支用 camelCase，而 Senate CLI 的慣例是 snake_case。
        /// 🩸 只收一種的代價是 09-17 那隻活體：兩個入口參數名不同 ⇒ 另一邊**靜默取預設值**，
        /// 而一棵樹上永遠看不出來。</para>
        /// </summary>
        public static bool TryBuildStep(IDictionary<string, string> iPairs, string iSourceLine,
                                        out SCP_InvokeStep? oStep, out string oError)
        {
            oStep = null;
            oError = "";
            if (iPairs == null) { oError = "沒有任何參數"; return false; }

            var aKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "type", "member", "kind",
                "paramTypes", "param_types",
                "args", "getter",
                "nonPublic", "non_public",
                "target",
                "storeAs", "store_as",
            };

            foreach (string aKey in iPairs.Keys)
            {
                if (!aKnown.Contains(aKey))
                {
                    oError = "認不得的參數 '" + aKey + "' —— 這一步吃的是："
                             + string.Join(" , ", new[] { "type", "member", "kind", "param_types", "args", "getter", "non_public", "target", "store_as" })
                             + "（**這一層** camelCase 也收；⛔ 而單步的 `--arg` 那條路有 ArgSpec 預檢，只收 snake_case）";
                    return false;
                }
            }

            string Pick(string iA, string iB)
            {
                if (iPairs.TryGetValue(iA, out string? v1) && v1 != null && v1.Length > 0) return v1;
                if (iPairs.TryGetValue(iB, out string? v2) && v2 != null) return v2;
                return "";
            }
            string Get(string iKey) => iPairs.TryGetValue(iKey, out string? v) && v != null ? v : "";

            var aStep = new SCP_InvokeStep
            {
                TypeName = Get("type").Trim(),
                MemberName = Get("member").Trim(),
                Target = Get("target").Trim().TrimStart('$'),
                StoreAs = Pick("storeAs", "store_as").Trim().TrimStart('$'),
                SourceLine = iSourceLine ?? "",
            };

            string aKind = Get("kind").Trim();
            aStep.Kind = aKind.Length == 0 ? "method" : aKind.ToLowerInvariant();
            if (aStep.Kind != "method" && aStep.Kind != "property" && aStep.Kind != "field")
            { oError = "kind 只能是 method / property / field，拿到：" + aKind; return false; }

            if (aStep.MemberName.Length == 0) { oError = "缺 member（成員名，大小寫敏感）"; return false; }
            if (aStep.TypeName.Length == 0 && aStep.Target.Length == 0)
            { oError = "缺 type —— 沒有 target 時一定要給完整型別名"; return false; }

            foreach (string p in SplitList(Pick("paramTypes", "param_types"))) aStep.ParamTypes.Add(p);
            foreach (string a in SplitList(Get("args"))) aStep.Args.Add(a);

            aStep.IsGetter = !IsFalse(Get("getter"));
            aStep.IncludeNonPublic = IsTrue(Pick("nonPublic", "non_public"));

            // 🩸 這一行漏過一次（2026-09-22 首跑）：開頭那句 `oStep = null` 已經讓編譯器滿意，
            //   ⇒ **「out 參數已指派」與「指派了對的值」在編譯期同形**，0 錯 0 警告。
            //   失效的樣子是呼叫端拿到 `true` ＋ 一個 null，然後印出一行沒有內容的 `✗`。
            //   ⛔ 抓到它的不是編譯器也不是警覺，是第一次真的跑它。
            oStep = aStep;
            return true;
        }

        static IEnumerable<string> SplitList(string iRaw)
        {
            if (string.IsNullOrEmpty(iRaw)) yield break;
            foreach (string s in iRaw.Split(';'))
            {
                string a = s.Trim();
                if (a.Length > 0) yield return a;
            }
        }

        static bool IsTrue(string iRaw)
            => string.Equals(iRaw, "1", StringComparison.Ordinal)
               || string.Equals(iRaw, "true", StringComparison.OrdinalIgnoreCase)
               || string.Equals(iRaw, "yes", StringComparison.OrdinalIgnoreCase);

        static bool IsFalse(string iRaw)
            => string.Equals(iRaw, "0", StringComparison.Ordinal)
               || string.Equals(iRaw, "false", StringComparison.OrdinalIgnoreCase)
               || string.Equals(iRaw, "no", StringComparison.OrdinalIgnoreCase);

        // ── 執行 ──────────────────────────────────────────────────

        /// <summary>
        /// 跑一串步驟。**變數表是這一次執行專屬的**，跑完就沒。
        /// <para>⛔ 任一步失敗就停 —— 後面的步驟通常依賴前一步 `store_as` 的值，
        /// 繼續跑只會產生一串「變數不存在」的雜訊，把真正的第一個成因埋掉。</para>
        /// </summary>
        public static IReadOnlyList<SCP_InvokeResult> Run(IReadOnlyList<SCP_InvokeStep> iSteps,
                                                          out IReadOnlyDictionary<string, object?> oVariables)
        {
            var aVars = new Dictionary<string, object?>(StringComparer.Ordinal);
            oVariables = aVars;
            var aResults = new List<SCP_InvokeResult>();
            if (iSteps == null) return aResults;

            foreach (SCP_InvokeStep aStep in iSteps)
            {
                SCP_InvokeResult aRes = InvokeOne(aStep, aVars);
                aResults.Add(aRes);
                if (!aRes.Success) break;
                if (aStep.StoreAs.Length > 0) aVars[aStep.StoreAs] = aRes.Value;
            }
            return aResults;
        }

        /// <summary>單步呼叫。<paramref name="ioVars"/> 供 `$name` 取值，可為空字典。</summary>
        public static SCP_InvokeResult InvokeOne(SCP_InvokeStep iStep, IDictionary<string, object?> ioVars)
        {
            if (iStep == null) return SCP_InvokeResult.Fail("step is null");
            if (ioVars == null) ioVars = new Dictionary<string, object?>(StringComparer.Ordinal);

            // 1) instance target
            object? aTarget = null;
            bool aIsInstance = iStep.Target.Length > 0;
            if (aIsInstance)
            {
                if (!ioVars.TryGetValue(iStep.Target, out aTarget) || aTarget == null)
                {
                    return SCP_InvokeResult.Fail(
                        "target '$" + iStep.Target + "' 不在本次執行的變數表裡"
                        + (ioVars.Count == 0
                            ? " —— 目前一個變數都沒有。⚠ 變數只活在**同一次呼叫**內（CLI 每次都是新 process）"
                              + "⇒ 要鏈式呼叫請用 steps 把前一步寫在同一份輸入裡，⛔ 不是分兩次跑。"
                            : " —— 目前有：" + string.Join(", ", ioVars.Keys.Select(k => "$" + k))));
                }
            }

            // 2) 型別（走 SCP_Reflect，⛔ 不自己解析）
            Type? aType;
            if (iStep.TypeName.Length > 0)
            {
                aType = ResolveType(iStep.TypeName, out string aTypeErr);
                if (aType == null) return SCP_InvokeResult.Fail(aTypeErr);
            }
            else if (aIsInstance)
            {
                aType = aTarget!.GetType();
            }
            else
            {
                return SCP_InvokeResult.Fail("缺 type —— 沒有 target 時一定要給完整型別名");
            }

            BindingFlags aFlags = BindingFlags.Public
                                  | (aIsInstance ? BindingFlags.Instance
                                                 : (BindingFlags.Static | BindingFlags.FlattenHierarchy));
            if (iStep.IncludeNonPublic) aFlags |= BindingFlags.NonPublic;

            try
            {
                switch (iStep.Kind)
                {
                    case "method": return InvokeMethod(aType, aTarget, iStep, aFlags, ioVars);
                    case "property": return InvokeProperty(aType, aTarget, iStep, aFlags, ioVars);
                    case "field": return InvokeField(aType, aTarget, iStep, aFlags, ioVars);
                    default: return SCP_InvokeResult.Fail("認不得的 kind：" + iStep.Kind);
                }
            }
            catch (TargetInvocationException ti)
            {
                // ⚠ 把 InnerException 攤平 —— 被反射層包住的話，錯誤訊息會變成
                //   「Exception has been thrown by the target of an invocation」，那句話零讀數。
                Exception aInner = ti.InnerException ?? ti;
                return SCP_InvokeResult.FailFromTarget("被呼叫的程式丟出 " + aInner.GetType().Name + "：" + aInner.Message
                                                       + (aInner.StackTrace != null ? "\n" + aInner.StackTrace : ""));
            }
            catch (Exception e)
            {
                return SCP_InvokeResult.Fail(e.GetType().Name + "：" + e.Message);
            }
        }

        /// <summary>
        /// 型別解析。<b>失敗訊息要分得出兩種成因</b>：
        /// <para>① 這個名字不存在　② 它所屬的組件**還沒被載入**（.NET 是 lazy load，
        /// 而 <see cref="SCP_Reflect.AllTypes"/> 掃的是 `AppDomain.CurrentDomain.GetAssemblies()`）。</para>
        /// 🩸 兩者的處置相反（改名字／先載入組件），⛔ 而它們預設同形（都是「找不到」）。
        /// </summary>
        public static Type? ResolveType(string iName, out string oError)
        {
            oError = "";
            if (string.IsNullOrWhiteSpace(iName)) { oError = "型別名是空的"; return null; }

            Type? aHit = SCP_Reflect.TypeByFullName(iName);
            if (aHit != null) return aHit;

            // 短名：撞名時**回全部讓呼叫端決定**（SCP_Reflect 的既有語意），⛔ 不自動挑第一個
            IReadOnlyList<Type> aCandidates = SCP_Reflect.ResolveTypes(iName);
            if (aCandidates.Count == 1) return aCandidates[0];
            if (aCandidates.Count > 1)
            {
                oError = "型別名 '" + iName + "' 在已載入的組件裡有 " + aCandidates.Count.ToString(CultureInfo.InvariantCulture)
                         + " 個同名的 ⇒ 請改用完整 FullName：\n  "
                         + string.Join("\n  ", aCandidates.Take(10).Select(t => t.FullName));
                return null;
            }

            int aAsmCount = AppDomain.CurrentDomain.GetAssemblies().Length;
            oError = "找不到型別：" + iName
                     + "\n  ⚠ 這句話有兩個成因，而它們的處置相反："
                     + "\n    ① 名字不對（FullName 大小寫敏感）"
                     + "\n    ② 它所屬的組件**還沒被載入** —— 目前只載入了 "
                     + aAsmCount.ToString(CultureInfo.InvariantCulture) + " 個組件，"
                     + "而 .NET 是用到才載。⇒ 用 `--arg assembly=<組件名>` 先把它載進來再試。";
            return null;
        }

        // ── 子流程 ────────────────────────────────────────────────

        static SCP_InvokeResult InvokeMethod(Type iType, object? iTarget, SCP_InvokeStep iStep,
                                             BindingFlags iFlags, IDictionary<string, object?> iVars)
        {
            string aScope = iTarget != null ? "instance method" : "static method";
            MethodInfo? aMethod;

            if (iStep.ParamTypes.Count > 0)
            {
                var aParamTypes = new Type[iStep.ParamTypes.Count];
                for (int i = 0; i < aParamTypes.Length; i++)
                {
                    Type? t = ResolveType(iStep.ParamTypes[i], out string aErr);
                    if (t == null) return SCP_InvokeResult.Fail("param_types[" + i.ToString(CultureInfo.InvariantCulture) + "] " + aErr);
                    aParamTypes[i] = t;
                }
                aMethod = FindMethodByTypes(iType, iStep.MemberName, iFlags, aParamTypes, walkHierarchy: iTarget == null);
                if (aMethod == null)
                    return SCP_InvokeResult.Fail(aScope + " 找不到：" + iType.FullName + "." + iStep.MemberName
                                                 + "(" + string.Join(",", iStep.ParamTypes) + ")"
                                                 + (iStep.IncludeNonPublic ? "" : " —— 試試 non_public=1"));
            }
            else
            {
                var aCandidates = new List<MethodInfo>();
                Type? t2 = iType;
                while (t2 != null && t2 != typeof(object))
                {
                    aCandidates.AddRange(t2.GetMethods(iFlags).Where(m => m.Name == iStep.MemberName));
                    if (iTarget != null) break;   // instance：GetMethods 已沿 hierarchy
                    t2 = t2.BaseType;
                }
                if (aCandidates.Count == 0)
                    return SCP_InvokeResult.Fail(aScope + " 找不到：" + iType.FullName + "." + iStep.MemberName
                                                 + (iStep.IncludeNonPublic ? "" : " —— 試試 non_public=1"));

                MethodInfo? aNoArg = aCandidates.FirstOrDefault(m => m.GetParameters().Length == 0);
                if (aNoArg != null) aMethod = aNoArg;
                else if (aCandidates.Count == 1) aMethod = aCandidates[0];
                else
                {
                    // ⛔ 多載撞到時**不替人挑** —— 挑錯的症狀是「它呼叫了另一支同名的」而且不報錯。
                    return SCP_InvokeResult.Fail(
                        "多載無法判定（需要 param_types）：" + iType.FullName + "." + iStep.MemberName
                        + "\n  候選：\n  " + string.Join("\n  ", aCandidates.Select(FormatSignature)));
                }
            }

            ParameterInfo[] aPs = aMethod.GetParameters();
            if (iStep.Args.Count > aPs.Length)
                return SCP_InvokeResult.Fail("引數太多：這支最多吃 " + aPs.Length.ToString(CultureInfo.InvariantCulture)
                                             + " 個，給了 " + iStep.Args.Count.ToString(CultureInfo.InvariantCulture));

            var aArgv = new object?[aPs.Length];
            if (!TryBuildArgs(aPs, iStep.Args, aArgv, iVars, out string aArgErr))
                return SCP_InvokeResult.Fail(aArgErr);

            // 尾巴沒給的用預設值補；沒有預設值就說出來（⛔ 不塞 null 混過去）
            for (int i = iStep.Args.Count; i < aPs.Length; i++)
            {
                if (!aPs[i].HasDefaultValue)
                    return SCP_InvokeResult.Fail("引數 [" + i.ToString(CultureInfo.InvariantCulture) + "] '" + aPs[i].Name
                                                 + "' 沒有預設值而你沒給（這支要 "
                                                 + aPs.Length.ToString(CultureInfo.InvariantCulture) + " 個，給了 "
                                                 + iStep.Args.Count.ToString(CultureInfo.InvariantCulture) + "）");
                aArgv[i] = aPs[i].DefaultValue;
            }

            object? aRet = aMethod.Invoke(iTarget, aArgv);
            return SCP_InvokeResult.Ok(aRet, aMethod.ReturnType);
        }

        static SCP_InvokeResult InvokeProperty(Type iType, object? iTarget, SCP_InvokeStep iStep,
                                               BindingFlags iFlags, IDictionary<string, object?> iVars)
        {
            PropertyInfo? aProp = FindProperty(iType, iStep.MemberName, iFlags, walkHierarchy: iTarget == null);
            if (aProp == null)
                return SCP_InvokeResult.Fail("property 找不到：" + iType.FullName + "." + iStep.MemberName
                                             + (iStep.IncludeNonPublic ? "" : " —— 試試 non_public=1"));

            if (iStep.IsGetter)
            {
                if (!aProp.CanRead) return SCP_InvokeResult.Fail("property 沒有 getter：" + iType.FullName + "." + iStep.MemberName);
                return SCP_InvokeResult.Ok(aProp.GetValue(iTarget), aProp.PropertyType);
            }

            if (!aProp.CanWrite) return SCP_InvokeResult.Fail("property 沒有 setter：" + iType.FullName + "." + iStep.MemberName);
            if (iStep.Args.Count != 1) return SCP_InvokeResult.Fail("getter=0（寫入）需要剛好一個 args 當要寫進去的值");
            if (!TryCoerce(aProp.PropertyType, iStep.Args[0], iVars, out object? aVal, out string aErr))
                return SCP_InvokeResult.Fail(aErr);
            aProp.SetValue(iTarget, aVal);
            return SCP_InvokeResult.Ok(null, typeof(void));
        }

        static SCP_InvokeResult InvokeField(Type iType, object? iTarget, SCP_InvokeStep iStep,
                                            BindingFlags iFlags, IDictionary<string, object?> iVars)
        {
            FieldInfo? aField = FindField(iType, iStep.MemberName, iFlags, walkHierarchy: iTarget == null);
            if (aField == null)
                return SCP_InvokeResult.Fail("field 找不到：" + iType.FullName + "." + iStep.MemberName
                                             + (iStep.IncludeNonPublic ? "" : " —— 試試 non_public=1"));

            if (iStep.IsGetter) return SCP_InvokeResult.Ok(aField.GetValue(iTarget), aField.FieldType);

            if (iStep.Args.Count != 1) return SCP_InvokeResult.Fail("getter=0（寫入）需要剛好一個 args 當要寫進去的值");
            if (aField.IsInitOnly || aField.IsLiteral)
                return SCP_InvokeResult.Fail("這個 field 是 readonly／const，寫不進去：" + iType.FullName + "." + iStep.MemberName);
            if (!TryCoerce(aField.FieldType, iStep.Args[0], iVars, out object? aVal, out string aErr))
                return SCP_InvokeResult.Fail(aErr);
            aField.SetValue(iTarget, aVal);
            return SCP_InvokeResult.Ok(null, typeof(void));
        }

        // ── 小工具 ────────────────────────────────────────────────

        static bool TryBuildArgs(ParameterInfo[] iPs, List<string> iRaw, object?[] ioValues,
                                 IDictionary<string, object?> iVars, out string oError)
        {
            oError = "";
            int n = Math.Min(iRaw.Count, iPs.Length);
            for (int i = 0; i < n; i++)
            {
                if (!TryCoerce(iPs[i].ParameterType, iRaw[i], iVars, out object? aVal, out string aErr))
                { oError = "引數 [" + i.ToString(CultureInfo.InvariantCulture) + "] '" + iPs[i].Name + "'：" + aErr; return false; }
                ioValues[i] = aVal;
            }
            return true;
        }

        /// <summary>`$name` 取變數（不轉型，原物件），其餘走 <see cref="SCP_Reflect.TryParse"/>。</summary>
        static bool TryCoerce(Type iTarget, string iRaw, IDictionary<string, object?> iVars,
                              out object? oValue, out string oError)
        {
            oValue = null;
            oError = "";
            if (!string.IsNullOrEmpty(iRaw) && iRaw[0] == '$')
            {
                string aName = iRaw.Substring(1);
                if (!iVars.TryGetValue(aName, out oValue))
                {
                    oError = "'$" + aName + "' 不在本次執行的變數表裡"
                             + (iVars.Count == 0
                                 ? "（目前一個變數都沒有 —— ⚠ 變數只活在同一次呼叫內）"
                                 : "，目前有：" + string.Join(", ", iVars.Keys.Select(k => "$" + k)));
                    return false;
                }
                return true;
            }
            return SCP_Reflect.TryParse(iTarget, iRaw, iAllowNullText: true, out oValue, out oError);
        }

        static MethodInfo? FindMethodByTypes(Type iType, string iName, BindingFlags iFlags, Type[] iParamTypes, bool walkHierarchy)
        {
            Type? t = iType;
            while (t != null)
            {
                MethodInfo? m = t.GetMethod(iName, iFlags, binder: null, types: iParamTypes, modifiers: null);
                if (m != null) return m;
                if (!walkHierarchy) break;
                t = t.BaseType;
                if (t == typeof(object)) break;
            }
            return null;
        }

        static PropertyInfo? FindProperty(Type iType, string iName, BindingFlags iFlags, bool walkHierarchy)
        {
            Type? t = iType;
            while (t != null)
            {
                PropertyInfo? p = t.GetProperty(iName, iFlags);
                if (p != null) return p;
                if (!walkHierarchy) break;
                t = t.BaseType;
                if (t == typeof(object)) break;
            }
            return null;
        }

        static FieldInfo? FindField(Type iType, string iName, BindingFlags iFlags, bool walkHierarchy)
        {
            Type? t = iType;
            while (t != null)
            {
                FieldInfo? f = t.GetField(iName, iFlags);
                if (f != null) return f;
                if (!walkHierarchy) break;
                t = t.BaseType;
                if (t == typeof(object)) break;
            }
            return null;
        }

        static string FormatSignature(MethodInfo iM)
            => iM.ReturnType.Name + " " + iM.Name + "("
               + string.Join(", ", iM.GetParameters().Select(p => (p.ParameterType.FullName ?? p.ParameterType.Name) + " " + p.Name))
               + ")";
    }
}
