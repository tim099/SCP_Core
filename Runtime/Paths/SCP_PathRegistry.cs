// 區塊職責：**所有動態路徑的唯一描述表** —— 一格一筆 descriptor，頁面與解析都從這張表長出來。
// 物理意義：擴充一條路徑＝加一個 enum 成員 ＋ 一筆 descriptor。**頁面不用改**（它 foreach 這張表）。
//           取自本 repo 既有的同形做法：`SCP_Cmd.PortStatus`＋`PortNote`（待移植清單的唯一落點，
//           不另外維護一份 md）、`UCL_AutoCommitRules.GroupDef[]`（分群規則）、`SCP_CmdArgSpec`。
// 數值影響：純資料 ＋ 純推導，不碰 IO、不讀設定檔（**誰去讀存起來的值是呼叫端的事**）——
//           本層只回答「這條路徑是誰決定的、怎麼算出來的」。
//
// ⚠ **兩件事刻意分開，因為混在一起正是要修的病：**
//   · `Stored`＝值真的存在設定檔裡（人填，或 `auto`）
//   · `Derived`＝**永遠算出來，不存** ⇒ 沒有第二個取值端可以漂
//   🩸 現場（2026-08-31）：`awakening.lettersRoot` 是手填絕對路徑，而 `awakening.sessionDir`
//     是 `auto`（從 lettersRoot 往上找 `_session`），上游 `agentCommandsRoot` 又是 `auto`
//     ⇒ **手填的那一格卡在推導鏈中間**。改了專案 root，lettersRoot 靜默指著舊樹，
//     sessionDir 跟著推導到舊樹 ⇒ 讀到一個格式完整、屬於別的專案的信件庫，而 lock 也在那棵舊樹上。
//   ⇒ 判準：**能被推導的路徑不准被儲存。** 存了就是給漂移一個住的地方。
//
// ⚠ **enum 成員名不是 wire name。** 儲存鍵走 descriptor 的 `JsonKey`，
//   所以改 enum 成員名**不會**動到設定檔。
//   🩸 為什麼要特別隔開：Task 那組 enum 的成員名**就是**磁碟格式，於是「改個名字」＝改 96 張單的
//     wire format。同一個坑不要在路徑上再挖一次 —— 而路徑漂掉比單漂掉更難查。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Paths
{
    /// <summary>一條動態路徑的身分。⚠ 成員名只給 code 用，**儲存鍵見 descriptor 的 JsonKey**。</summary>
    public enum SCP_PathId
    {
        /// <summary>專案 git repo 根。所有「每專案」路徑的起點。</summary>
        ProjectRoot,

        /// <summary>AgentCommands 資料根。其餘每專案路徑幾乎都由它推導。</summary>
        AgentCommandsRoot,

        /// <summary>persona 信件庫根。<para>⚠ **刻意獨立於專案**（Tim 2026-08-31：之後要搬到更外層，
        /// 獨立於所有專案）—— 所以它不是「還沒接上推導」，是**故意不接**。</para></summary>
        LettersRoot,

        /// <summary>session lock 目錄。</summary>
        SessionDir,

        /// <summary>酒館根。</summary>
        ChatTavern,

        /// <summary>酒館 baton（各 persona 的 cmd 回傳檔住這下面）。</summary>
        Baton,

        /// <summary>AgentCommand queue 根。</summary>
        Queues,

        /// <summary>任務單根。</summary>
        TasksRoot,

        /// <summary>Cmd 判定檔（`_cmd_results`）。</summary>
        CmdResults,
    }

    /// <summary>這條路徑的值是**存起來的**還是**算出來的**。</summary>
    public enum SCP_PathKind
    {
        /// <summary>存在設定檔裡（人填，或 <c>auto</c> 讓它去推導）。頁面上可編輯。</summary>
        Stored,

        /// <summary>**永遠由上游算出來，不存**。頁面上唯讀，並且要把算式印出來。</summary>
        Derived,
    }

    /// <summary>作用域 —— 決定這格該住在哪一層設定裡。</summary>
    public enum SCP_PathScope
    {
        /// <summary>每個專案一份。</summary>
        PerProject,

        /// <summary>全域一份（跨專案）。</summary>
        Global,
    }

    /// <summary>一條路徑的描述。**加一條路徑＝加一個 enum 成員 ＋ 加一筆這個。**</summary>
    public sealed class SCP_PathDescriptor
    {
        public SCP_PathId Id;

        /// <summary>顯示名（頁面上的標籤）。</summary>
        public string Label = "";

        /// <summary>
        /// 儲存鍵（`Stored` 才有意義）。⚠ **這是 wire name**，改它＝改設定檔格式；
        /// enum 成員名改了不影響這裡。
        /// </summary>
        public string JsonKey = "";

        public SCP_PathKind Kind = SCP_PathKind.Derived;
        public SCP_PathScope Scope = SCP_PathScope.PerProject;

        /// <summary>`Derived` 的上游。<c>null</c> ＝ 沒有上游（只有 Stored 該是這樣）。</summary>
        public SCP_PathId? DeriveFrom;

        /// <summary>接在上游後面的相對段（正斜線，不帶開頭斜線）。</summary>
        public string DeriveSuffix = "";

        /// <summary>這格是幹什麼的、以及**它為什麼是 Stored 或 Derived**。</summary>
        public string Note = "";

        /// <summary>`Stored` 是否支援 <c>auto</c>（＝交給上游推導）。</summary>
        public bool SupportsAuto;

        /// <summary>`auto` 時的上游（`SupportsAuto` 才有意義）。</summary>
        public SCP_PathId? AutoFrom;

        public string AutoSuffix = "";
    }

    /// <summary>值解析結果 —— **值與「誰決定的」一起回**，因為看不出來源的路徑沒辦法被質疑。</summary>
    public readonly struct SCP_PathResolution
    {
        public SCP_PathResolution(string iValue, string iOrigin, string? iError)
        { Value = iValue; Origin = iOrigin; Error = iError; }

        /// <summary>解析出的絕對路徑（正斜線）。空 ＝ 解不出來（看 <see cref="Error"/>）。</summary>
        public string Value { get; }

        /// <summary>來源定語，例：`手填`／`auto ⇒ 由 AgentCommandsRoot 推導`／`derived ⇒ …`。</summary>
        public string Origin { get; }

        /// <summary>解不出來的原因。null ＝ 沒問題。</summary>
        public string? Error { get; }
    }

    public static class SCP_PathRegistry
    {
        /// <summary>`auto` 的字面 —— 與 senate.local.json 既有慣例同形（`agentCommandsRoot: "auto"`）。</summary>
        public const string AutoLiteral = "auto";

        static readonly SCP_PathDescriptor[] s_All =
        {
            new SCP_PathDescriptor
            {
                Id = SCP_PathId.ProjectRoot, Label = "專案根（git repo 根）",
                JsonKey = "root", Kind = SCP_PathKind.Stored, Scope = SCP_PathScope.PerProject,
                Note = "每專案路徑的起點。**沒有上游可以推導它** —— 這是唯一必須有人說的那一格。",
            },
            new SCP_PathDescriptor
            {
                Id = SCP_PathId.AgentCommandsRoot, Label = "AgentCommands 資料根",
                JsonKey = "agentCommandsRoot", Kind = SCP_PathKind.Stored, Scope = SCP_PathScope.PerProject,
                SupportsAuto = true, AutoFrom = SCP_PathId.ProjectRoot, AutoSuffix = "AgentCommands",
                Note = "Stored 的理由：資料根**可以不在專案裡**（pointer 檔 `.agentcommands_root.local` 就是為此存在）。"
                       + "預設 auto ⇒ `<專案根>/AgentCommands`。",
            },
            new SCP_PathDescriptor
            {
                Id = SCP_PathId.LettersRoot, Label = "persona 信件庫根",
                JsonKey = "lettersRoot", Kind = SCP_PathKind.Stored, Scope = SCP_PathScope.Global,
                SupportsAuto = true, AutoFrom = SCP_PathId.AgentCommandsRoot,
                AutoSuffix = SCP_DataPaths.ChatTavernDirName + "/" + SCP_DataPaths.BatonDirName
                             + "/" + SCP_DataPaths.LettersDirName,
                Note = "⚠ **Global 且刻意獨立於專案**（Tim 2026-08-31：之後要搬到更外層，獨立於所有專案）"
                       + " —— 它不是「還沒接上推導」，是**故意不接**。"
                       + " 支援 auto 只是為了「還沒搬走之前，不必手抄一次上游」。",
            },
            new SCP_PathDescriptor
            {
                Id = SCP_PathId.SessionDir, Label = "session lock 目錄",
                Kind = SCP_PathKind.Derived, Scope = SCP_PathScope.PerProject,
                DeriveFrom = SCP_PathId.AgentCommandsRoot, DeriveSuffix = SCP_DataPaths.SessionDirName,
                Note = "⚠ 舊設計是 `auto` **從信件庫根往上找** `_session` ——"
                       + " 那讓「lock 在哪」跟著一個手填值漂。改成由資料根直接推導。",
            },
            new SCP_PathDescriptor
            {
                Id = SCP_PathId.ChatTavern, Label = "酒館根",
                Kind = SCP_PathKind.Derived, Scope = SCP_PathScope.PerProject,
                DeriveFrom = SCP_PathId.AgentCommandsRoot, DeriveSuffix = SCP_DataPaths.ChatTavernDirName,
                Note = "訊息、seq、inbox 都在這下面。**寫入端只有 Editor**（seq 沒有跨 process lock）。",
            },
            new SCP_PathDescriptor
            {
                Id = SCP_PathId.Baton, Label = "酒館 baton",
                Kind = SCP_PathKind.Derived, Scope = SCP_PathScope.PerProject,
                DeriveFrom = SCP_PathId.ChatTavern, DeriveSuffix = SCP_DataPaths.BatonDirName,
                Note = "各 persona 的 cmd 回傳檔住這下面。",
            },
            new SCP_PathDescriptor
            {
                Id = SCP_PathId.Queues, Label = "AgentCommand queue 根",
                Kind = SCP_PathKind.Derived, Scope = SCP_PathScope.PerProject,
                DeriveFrom = SCP_PathId.AgentCommandsRoot, DeriveSuffix = SCP_DataPaths.QueuesDirName,
                Note = "一個 persona 一個分道。掉進 `anonymous` ＝ 有人沒帶 `--persona`。",
            },
            new SCP_PathDescriptor
            {
                Id = SCP_PathId.TasksRoot, Label = "任務單根",
                Kind = SCP_PathKind.Derived, Scope = SCP_PathScope.PerProject,
                DeriveFrom = SCP_PathId.AgentCommandsRoot, DeriveSuffix = "Tasks",
                Note = "讀取層已在 SCP_Core（`SCP_TaskIO`）；配號與寫入仍只有 Editor 一個寫者。",
            },
            new SCP_PathDescriptor
            {
                Id = SCP_PathId.CmdResults, Label = "Cmd 判定檔",
                Kind = SCP_PathKind.Derived, Scope = SCP_PathScope.PerProject,
                DeriveFrom = SCP_PathId.AgentCommandsRoot, DeriveSuffix = "_cmd_results",
                Note = "`<cmd_id>.json` ——「這一筆是哪個 client 送的」現在記在這裡（`client` 欄）。",
            },
        };

        public static IReadOnlyList<SCP_PathDescriptor> All => s_All;

        public static SCP_PathDescriptor Get(SCP_PathId iId)
        {
            foreach (SCP_PathDescriptor aD in s_All) if (aD.Id == iId) return aD;
            // 描述表缺一格是**程式錯誤**，不是使用者輸入錯 —— 不回 null 讓它變成別處的 NRE。
            throw new InvalidOperationException(
                $"[SCP_PathRegistry] 描述表缺 {iId} —— 加了 enum 成員就要加 descriptor（本表是唯一落點）");
        }

        /// <summary>
        /// 解析一條路徑。<paramref name="iStored"/> 回傳某個 Id 存起來的原始值
        /// （沒設定回空字串）—— **本層不知道那些值住在哪個檔**，那是呼叫端的事。
        /// </summary>
        public static SCP_PathResolution Resolve(SCP_PathId iId, Func<SCP_PathId, string> iStored)
            => Resolve(iId, iStored, 0);

        static SCP_PathResolution Resolve(SCP_PathId iId, Func<SCP_PathId, string> iStored, int iDepth)
        {
            // 上游成環時要**大聲**：靜默的無限遞迴在這裡會表現成「頁面打不開」，
            // 而那跟「設定檔壞了」長得不一樣但一樣難查。
            if (iDepth > s_All.Length)
                return new SCP_PathResolution("", "?", $"推導鏈成環或過深（起點 {iId}）");

            SCP_PathDescriptor aD = Get(iId);
            if (aD.Kind == SCP_PathKind.Stored)
            {
                string aRaw = (iStored(iId) ?? "").Trim();
                if (aRaw.Length > 0 && !string.Equals(aRaw, AutoLiteral, StringComparison.OrdinalIgnoreCase))
                    return new SCP_PathResolution(Clean(aRaw), "手填", null);
                if (!aD.SupportsAuto || aD.AutoFrom == null)
                    return new SCP_PathResolution("", aRaw.Length == 0 ? "未設定" : AutoLiteral,
                        aRaw.Length == 0
                            ? "這一格沒有人填過，而它**沒有上游可以推導**"
                            : $"填了 `{AutoLiteral}` 但本格不支援 auto");
                SCP_PathResolution aUp = Resolve(aD.AutoFrom.Value, iStored, iDepth + 1);
                if (aUp.Error != null)
                    return new SCP_PathResolution("", $"{AutoLiteral} ⇒ 由 {aD.AutoFrom} 推導",
                        $"上游解不出來：{aUp.Error}");
                return new SCP_PathResolution(Join(aUp.Value, aD.AutoSuffix),
                    $"{AutoLiteral} ⇒ 由 {aD.AutoFrom} 推導", null);
            }

            if (aD.DeriveFrom == null)
                return new SCP_PathResolution("", "?",
                    $"{iId} 標成 Derived 卻沒有上游 —— 描述表自相矛盾");
            SCP_PathResolution aFrom = Resolve(aD.DeriveFrom.Value, iStored, iDepth + 1);
            if (aFrom.Error != null)
                return new SCP_PathResolution("", $"derived ⇒ {aD.DeriveFrom}/{aD.DeriveSuffix}",
                    $"上游解不出來：{aFrom.Error}");
            return new SCP_PathResolution(Join(aFrom.Value, aD.DeriveSuffix),
                $"derived ⇒ {aD.DeriveFrom}/{aD.DeriveSuffix}", null);
        }

        /// <summary>算式的可讀形式（頁面上印在 Derived 那格旁邊 —— 讓人看得出它是算出來的）。</summary>
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
