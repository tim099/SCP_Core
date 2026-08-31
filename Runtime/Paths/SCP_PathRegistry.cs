// 區塊職責：**所有動態路徑的唯一描述來源** —— 描述用 attribute 黏在 enum 成員上，
//           本檔只負責「讀那些 attribute、解析值、回報誰決定的」。
// 物理意義：擴充一條路徑＝**在 enum 加一個成員並掛上 attribute**。沒有第二份清單要同步
//           （頁面與 CLI 都 foreach `All`）。
// 數值影響：反射一次、快取起來（enum 的 attribute 在執行期不會變）。純推導，不碰 IO、不讀設定檔
//           —— **誰去讀存起來的值是呼叫端的事**，本層只回答「這條路徑是誰決定的、怎麼算出來的」。
//
// ⚠ **資料根只有一組**（Tim 2026-08-31）：`AgentCommandsRoot` 是 **Global** 不是每專案。
//   理由不是簡化，是那些機制**本來就假設只有一棵資料樹**：
//   酒館 `_seq.txt`、任務 `_index.txt`、`_session` lock ——
//   兩個資料根就是兩份序號、兩份計數、persona 被切成兩半，而**沒有任何一層會喊**。
//   ⇒ 「有兩個啟用專案」在本層是**解析錯誤**，不是「替你挑一個」。
//   📌 而它之後會搬到 Unity 專案之外 ⇒ 現在 `auto`（由專案根推導）是**過渡形**，不是終局。
//
// ⚠ Stored / Derived 的分野是本檔的核心：
//   🩸 現場（2026-08-31）：`sessionDir` 曾經可填（`auto` ＝ 從**信件庫根**往上找 `_session`），
//     而信件庫根是手填的 ⇒ 改了專案 root，lock 靜默指著舊樹，「誰在線」跟真實脫鉤，
//     而每一頁看起來都正常。
//   ⇒ 判準：**能被推導的路徑不准被儲存。** 存了就是給漂移一個住的地方。
#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;

namespace SCP.Core.Paths
{
    /// <summary>這條路徑的值是**存起來的**還是**算出來的**。</summary>
    public enum SCP_PathKind
    {
        /// <summary>存在設定檔裡（人填，或掛了 <c>SCP_PathAuto</c> 後可填 <c>auto</c>）。頁面上可編輯。</summary>
        Stored,

        /// <summary>**永遠由上游算出來，不存**。頁面上唯讀，並且要把算式印出來。</summary>
        Derived,
    }

    /// <summary>作用域。</summary>
    public enum SCP_PathScope
    {
        /// <summary>綁在那個唯一的專案上（例：專案 git repo 根）。</summary>
        Project,

        /// <summary>全域一份，跨專案。⚠ 資料根與信件庫根都在這一類 —— **它們只有一組**。</summary>
        Global,
    }

    /// <summary>
    /// 一條動態路徑的身分。**描述掛在成員上的 attribute 裡**（加成員就看得到空位）。
    /// <para>⚠ 成員名只給 code 用；儲存鍵是 <c>SCP_PathStored</c> 的 <c>JsonKey</c>。</para>
    /// </summary>
    public enum SCP_PathId
    {
        [SCP_PathInfo("專案根（git repo 根）",
            "唯一那個 Unity 專案的 git repo 根。**沒有上游可以推導它** —— 這是唯一必須有人說的那一格。"
            + " ⚠ 只允許一個啟用專案：資料根只有一組，兩棵資料樹會把 seq／單號／lock 切成兩份而不報錯。")]
        [SCP_PathStored("root", SCP_PathScope.Project)]
        ProjectRoot,

        [SCP_PathInfo("AgentCommands 資料根",
            "**Global —— 只有一組**（Tim 2026-08-31）。酒館 seq／任務單號／session lock 全都假設只有一棵樹。"
            + " Stored 的理由：它可以不在專案裡（pointer 檔 `.agentcommands_root.local` 就是為此存在），"
            + "而且**之後會搬到 Unity 專案之外** ⇒ 現在的 `auto`（由專案根推導）是過渡形不是終局。")]
        [SCP_PathStored("agentCommandsRoot", SCP_PathScope.Global)]
        [SCP_PathAuto(SCP_PathId.ProjectRoot, "AgentCommands")]
        AgentCommandsRoot,

        [SCP_PathInfo("persona 信件庫根",
            "⚠ **Global 且刻意獨立**（Tim 2026-08-31：之後要搬到更外層，獨立於所有專案）——"
            + " 它不是「還沒接上推導」，是**故意不接**。支援 `auto` 只是為了「還沒搬走之前，不必手抄一次上游」。")]
        [SCP_PathStored("lettersRoot", SCP_PathScope.Global)]
        [SCP_PathAuto(SCP_PathId.AgentCommandsRoot, "ChatTavern/baton/letters")]
        LettersRoot,

        [SCP_PathInfo("session lock 目錄",
            "⚠ 舊設計是 `auto` **從信件庫根往上找** `_session` —— 那讓「lock 在哪」跟著一個手填值漂。"
            + "改成由資料根直接推導。")]
        [SCP_PathDerived(SCP_PathId.AgentCommandsRoot, "_session", SCP_PathScope.Global)]
        SessionDir,

        [SCP_PathInfo("酒館根",
            "訊息、seq、inbox 都在這下面。**寫入端只有 Editor**（`_seq.txt` 沒有跨 process lock）。")]
        [SCP_PathDerived(SCP_PathId.AgentCommandsRoot, "ChatTavern", SCP_PathScope.Global)]
        ChatTavern,

        [SCP_PathInfo("酒館 baton", "各 persona 的 cmd 回傳檔住這下面。")]
        [SCP_PathDerived(SCP_PathId.ChatTavern, "baton", SCP_PathScope.Global)]
        Baton,

        [SCP_PathInfo("AgentCommand queue 根",
            "一個 persona 一個分道。掉進 `anonymous` ＝ 有人沒帶 `--persona`（全員會互相阻塞）。")]
        [SCP_PathDerived(SCP_PathId.AgentCommandsRoot, "queues", SCP_PathScope.Global)]
        Queues,

        [SCP_PathInfo("任務單根",
            "讀取層已在 SCP_Core（`SCP_TaskIO`）；**配號與寫入仍只有 Editor 一個寫者**"
            + "（`_index.txt` 是沒有跨 process lock 的 read-modify-write）。")]
        [SCP_PathDerived(SCP_PathId.AgentCommandsRoot, "Tasks", SCP_PathScope.Global)]
        TasksRoot,

        [SCP_PathInfo("Cmd 判定檔",
            "`<cmd_id>.json` ——「這一筆是哪個 client 送的」現在記在這裡（`client` 欄）。")]
        [SCP_PathDerived(SCP_PathId.AgentCommandsRoot, "_cmd_results", SCP_PathScope.Global)]
        CmdResults,
    }

    /// <summary>一格 Stored 的原始值 ＋ 取不到的原因（例：有兩個啟用專案 ⇒ 資料根不唯一）。</summary>
    public readonly struct SCP_PathStoredValue
    {
        public SCP_PathStoredValue(string iRaw, string? iError) { Raw = iRaw ?? ""; Error = iError; }

        public static SCP_PathStoredValue Of(string iRaw) => new SCP_PathStoredValue(iRaw, null);
        public static SCP_PathStoredValue Unavailable(string iError) => new SCP_PathStoredValue("", iError);

        public string Raw { get; }

        /// <summary>取不到的原因。⚠ 跟「沒設定過」**不可同形** —— 前者是狀態壞了，後者不是錯。</summary>
        public string? Error { get; }
    }

    /// <summary>一條路徑的描述（由 attribute 讀出來 —— 不是另一份手維護的清單）。</summary>
    public sealed class SCP_PathDescriptor
    {
        public SCP_PathId Id;
        public string Label = "";
        public string Note = "";
        public SCP_PathKind Kind;
        public SCP_PathScope Scope;
        public string JsonKey = "";
        public SCP_PathId? DeriveFrom;
        public string DeriveSuffix = "";
        public bool SupportsAuto;
        public SCP_PathId? AutoFrom;
        public string AutoSuffix = "";
    }

    /// <summary>值解析結果 —— **值與「誰決定的」一起回**，因為看不出來源的路徑沒辦法被質疑。</summary>
    public readonly struct SCP_PathResolution
    {
        public SCP_PathResolution(string iValue, string iOrigin, string? iError)
        { Value = iValue; Origin = iOrigin; Error = iError; }

        public string Value { get; }
        public string Origin { get; }
        public string? Error { get; }
    }

    public static class SCP_PathRegistry
    {
        /// <summary>`auto` 的字面 —— 與 senate.local.json 既有慣例同形（`agentCommandsRoot: "auto"`）。</summary>
        public const string AutoLiteral = "auto";

        static SCP_PathDescriptor[]? s_Cache;

        /// <summary>
        /// 所有路徑，**順序＝enum 宣告順序**（宣告順序即顯示順序，只有一個真相）。
        /// </summary>
        public static IReadOnlyList<SCP_PathDescriptor> All => s_Cache ??= BuildAll();

        static SCP_PathDescriptor[] BuildAll()
        {
            var aOut = new List<SCP_PathDescriptor>();
            foreach (SCP_PathId aId in (SCP_PathId[])Enum.GetValues(typeof(SCP_PathId)))
                aOut.Add(Describe(aId));
            return aOut.ToArray();
        }

        static SCP_PathDescriptor Describe(SCP_PathId iId)
        {
            FieldInfo? aField = typeof(SCP_PathId).GetField(iId.ToString());
            if (aField == null)
                throw new InvalidOperationException($"[SCP_PathRegistry] 反射拿不到 enum 成員 {iId}");

            var aInfo = aField.GetCustomAttribute<SCP_PathInfoAttribute>();
            var aStored = aField.GetCustomAttribute<SCP_PathStoredAttribute>();
            var aDerived = aField.GetCustomAttribute<SCP_PathDerivedAttribute>();
            var aAuto = aField.GetCustomAttribute<SCP_PathAutoAttribute>();

            var aD = new SCP_PathDescriptor
            {
                Id = iId,
                Label = aInfo?.Label ?? iId.ToString(),
                Note = aInfo?.Note ?? "",
            };
            if (aStored != null)
            {
                aD.Kind = SCP_PathKind.Stored;
                aD.Scope = aStored.Scope;
                aD.JsonKey = aStored.JsonKey;
                if (aAuto != null)
                {
                    aD.SupportsAuto = true;
                    aD.AutoFrom = aAuto.From;
                    aD.AutoSuffix = aAuto.Suffix;
                }
            }
            else if (aDerived != null)
            {
                aD.Kind = SCP_PathKind.Derived;
                aD.Scope = aDerived.Scope;
                aD.DeriveFrom = aDerived.From;
                aD.DeriveSuffix = aDerived.Suffix;
            }
            else
            {
                // 沒掛任何一種 ⇒ 這是**寫程式的人漏了**，不是使用者輸入錯。
                // Validate() 會在出廠驗收擋下；這裡仍然丟，因為靜默的預設值會讓它一路活到頁面上。
                throw new InvalidOperationException(
                    $"[SCP_PathRegistry] enum 成員 {iId} 沒掛 [SCP_PathStored] 也沒掛 [SCP_PathDerived]"
                    + " —— 加了成員就要掛一個（描述黏在成員上，本層沒有第二份清單可以補）");
            }
            return aD;
        }

        public static SCP_PathDescriptor Get(SCP_PathId iId)
        {
            foreach (SCP_PathDescriptor aD in All) if (aD.Id == iId) return aD;
            throw new InvalidOperationException($"[SCP_PathRegistry] 找不到 {iId}");
        }

        // ===========================================================
        // 區塊職責：描述表自身的合法性 —— 掛在 `senate selftest` 上。
        // 物理意義：「漏掛 attribute」「Auto 掛在 Derived 上」「上游成環」都是**寫的時候的錯**，
        //          該在出廠驗收擋下，不是執行到那一格才炸（那時症狀是頁面打不開／CLI 少一列）。
        // 數值影響：純檢查。回問題清單，空 ＝ 沒問題。
        // ===========================================================
        public static List<string> Validate()
        {
            var aProblems = new List<string>();
            foreach (SCP_PathId aId in (SCP_PathId[])Enum.GetValues(typeof(SCP_PathId)))
            {
                FieldInfo? aField = typeof(SCP_PathId).GetField(aId.ToString());
                if (aField == null) { aProblems.Add($"{aId}：反射拿不到成員"); continue; }
                var aStored = aField.GetCustomAttribute<SCP_PathStoredAttribute>();
                var aDerived = aField.GetCustomAttribute<SCP_PathDerivedAttribute>();
                var aAuto = aField.GetCustomAttribute<SCP_PathAutoAttribute>();
                var aInfo = aField.GetCustomAttribute<SCP_PathInfoAttribute>();

                if (aStored == null && aDerived == null)
                    aProblems.Add($"{aId}：沒掛 [SCP_PathStored] 也沒掛 [SCP_PathDerived]");
                if (aStored != null && aDerived != null)
                    aProblems.Add($"{aId}：同時掛了 Stored 與 Derived（一格只能是其中一種）");
                if (aAuto != null && aStored == null)
                    aProblems.Add($"{aId}：掛了 [SCP_PathAuto] 卻不是 Stored（算出來的東西不需要 auto）");
                if (aStored != null && aStored.JsonKey.Trim().Length == 0)
                    aProblems.Add($"{aId}：Stored 的 JsonKey 是空的（那是 wire name，不能空）");
                if (aInfo == null)
                    aProblems.Add($"{aId}：沒掛 [SCP_PathInfo]（頁面與 CLI 會把 Note 印出來）");
                else if (aInfo.Note.Trim().Length == 0)
                    aProblems.Add($"{aId}：Note 是空的 —— 「刻意如此」與「還沒做」要分得出來");
                if (aAuto != null && aAuto.From == aId)
                    aProblems.Add($"{aId}：auto 的上游是自己");
                if (aDerived != null && aDerived.From == aId)
                    aProblems.Add($"{aId}：derived 的上游是自己");
            }
            // 成環：從每一格往上走，超過總格數就是有環
            int aCount = All.Count;
            foreach (SCP_PathDescriptor aD in All)
            {
                SCP_PathId? aCursor = aD.Kind == SCP_PathKind.Derived ? aD.DeriveFrom : aD.AutoFrom;
                int aSteps = 0;
                while (aCursor != null && aSteps++ <= aCount)
                {
                    SCP_PathDescriptor aUp = Get(aCursor.Value);
                    aCursor = aUp.Kind == SCP_PathKind.Derived ? aUp.DeriveFrom : aUp.AutoFrom;
                }
                if (aSteps > aCount) aProblems.Add($"{aD.Id}：上游鏈成環");
            }
            return aProblems;
        }

        /// <summary>解析一條路徑。<paramref name="iStored"/> 回傳某個 Id 存起來的原始值＋取不到的原因。</summary>
        public static SCP_PathResolution Resolve(SCP_PathId iId, Func<SCP_PathId, SCP_PathStoredValue> iStored)
            => Resolve(iId, iStored, 0);

        static SCP_PathResolution Resolve(SCP_PathId iId, Func<SCP_PathId, SCP_PathStoredValue> iStored, int iDepth)
        {
            if (iDepth > All.Count)
                return new SCP_PathResolution("", "?", $"推導鏈成環或過深（起點 {iId}）");

            SCP_PathDescriptor aD = Get(iId);
            if (aD.Kind == SCP_PathKind.Stored)
            {
                SCP_PathStoredValue aStored = iStored(iId);
                // 「取不到」與「沒設定過」不可同形 —— 前者是狀態壞了（例：兩個啟用專案），後者不是錯。
                if (aStored.Error != null)
                    return new SCP_PathResolution("", "取不到", aStored.Error);
                string aRaw = aStored.Raw.Trim();
                if (aRaw.Length > 0 && !string.Equals(aRaw, AutoLiteral, StringComparison.OrdinalIgnoreCase))
                    return new SCP_PathResolution(Clean(aRaw), "手填", null);
                if (!aD.SupportsAuto || aD.AutoFrom == null)
                    return new SCP_PathResolution("", aRaw.Length == 0 ? "未設定" : AutoLiteral,
                        aRaw.Length == 0
                            ? "這一格沒有人填過，而它**沒有上游可以推導**"
                            : $"填了 `{AutoLiteral}` 但本格不支援 auto");
                SCP_PathResolution aUp = Resolve(aD.AutoFrom.Value, iStored, iDepth + 1);
                string aOrigin = $"{AutoLiteral} ⇒ 由 {aD.AutoFrom} 推導";
                return aUp.Error != null
                    ? new SCP_PathResolution("", aOrigin, $"上游解不出來：{aUp.Error}")
                    : new SCP_PathResolution(Join(aUp.Value, aD.AutoSuffix), aOrigin, null);
            }

            SCP_PathResolution aFrom = Resolve(aD.DeriveFrom!.Value, iStored, iDepth + 1);
            string aDerivedOrigin = $"derived ⇒ {aD.DeriveFrom}/{aD.DeriveSuffix}";
            return aFrom.Error != null
                ? new SCP_PathResolution("", aDerivedOrigin, $"上游解不出來：{aFrom.Error}")
                : new SCP_PathResolution(Join(aFrom.Value, aD.DeriveSuffix), aDerivedOrigin, null);
        }

        /// <summary>算式的可讀形式（印在格子旁邊 —— 讓人看得出它是算出來的還是填的）。</summary>
        public static string Formula(SCP_PathId iId)
        {
            SCP_PathDescriptor aD = Get(iId);
            if (aD.Kind == SCP_PathKind.Stored)
                return aD.SupportsAuto && aD.AutoFrom != null
                    ? $"手填，或 `{AutoLiteral}` ⇒ <{aD.AutoFrom}>/{aD.AutoSuffix}"
                    : "手填（無上游）";
            return $"<{aD.DeriveFrom}>/{aD.DeriveSuffix}";
        }

        static string Clean(string iPath) => iPath.Replace('\\', '/').TrimEnd('/');

        static string Join(string iBase, string iSuffix)
        {
            if (iBase.Length == 0) return "";
            if (iSuffix.Length == 0) return Clean(iBase);
            return Clean(iBase) + "/" + iSuffix.Replace('\\', '/').Trim('/');
        }
    }
}
