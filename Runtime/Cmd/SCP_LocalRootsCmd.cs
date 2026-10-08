// 區塊職責：「解析專案 → 組三個根 → 執行 → 印回傳檔與下一步」的共用殼（早安／晚安／酒館發文都繼承它），
//          以及它要的**宿主能力**介面。
// 物理意義：TASK-0406（Tim 2026-10-05）：「下一步指令定義在 Senate.Core，是否可以遷移到 SCP_Core」。
//           原本這個殼叫 `MorningLocalCmd`、住在 Senate.Core —— SCP_Core 的 `SCP_Morning` 要提示下一步時，
//           參照不到 Senate 的型別，只能手寫指令名，而手寫的名字在指令改名後會繼續說舊話。
//           ⇒ 殼搬進 SCP_Core，**只有宿主才有的五件事**收成一個介面，由宿主在啟動時掛上：
//             選專案（含 `target_data_root`）／詞典根／執行環境標記／酒館寫入（含排隊）／**「在哪裡執行」那一句**。
//           ⚠ 最後那一句刻意交給宿主說：共用碼沒有資格替呼叫它的人宣稱「我在哪裡」（憲法⑤ 2026-09-26 那筆）。
//           名字也改了：舊名叫 Morning，卻被晚安與發文拿去用 —— 名字比用途窄。
// 數值影響：零 IO（IO 在子類與宿主）。宿主沒掛 ⇒ exit 70 並說是程式錯誤，⛔ 不猜一個根。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    /// <summary>宿主解析出來的「這一筆要做在哪個資料根」。</summary>
    public sealed class SCP_LocalTarget
    {
        public string ProjectName = "";
        public string DataRoot = "";
        public string ProjectRoot = "";
        /// <summary>人看的定語（例：`資料根 D:/Unity/Valhalla`）—— 第一行要帶著它。</summary>
        public string Describe = "";
        /// <summary>詞典根；null ⇒ 解不出來（原因在 <see cref="GlossaryError"/>），殼**說出來**、本次不附詞典（不猜根，TASK-0390）。</summary>
        public string? GlossaryRoot;
        public string? GlossaryError;
    }

    /// <summary>酒館寫入的結果：送出／排隊／沒送出（三態不得同形）。</summary>
    public sealed class SCP_LocalTavernWrite
    {
        public SCP_CmdResult Result = new SCP_CmdResult();
        /// <summary>宿主把它排進 queue（Server 不在且確定還沒送出）—— ⛔ 不是失敗，重跑＝兩則。</summary>
        public bool Queued;
    }

    /// <summary>殼要的宿主能力。Senate 在 Program 啟動時掛上；沒掛的宿主（例：Unity）這幾支 Cmd 回 70。</summary>
    public interface ISCP_LocalCmdHost
    {
        /// <summary>第一行的執行位置宣告（例：`⤷ Senate 就地執行`）。⛔ 共用層不寫死。</summary>
        string WhereLine { get; }

        /// <summary>記進回傳值 `delegate_host` 的那個字。</summary>
        string HostId { get; }

        /// <summary>解資料根。<paramref name="iTargetDataRoot"/> 非空 ⇒ 必須就是設定的那一組（比不到就擋）。</summary>
        bool TryResolve(string iTargetDataRoot, out SCP_LocalTarget oTarget, out string oError, out string oHint);

        /// <summary>執行環境標記（登入時記進 lock）。</summary>
        string DetectEnvMarker();

        /// <summary>寫一則酒館訊息；送不進且確定還沒送出 ⇒ 排隊（<see cref="SCP_LocalTavernWrite.Queued"/>）。</summary>
        SCP_LocalTavernWrite WriteTavern(IReadOnlyDictionary<string, string> iArgs);

        /// <summary>
        /// 「要回話就跑這一行」的發文指令（例：`senate cmd tavern-post --arg persona=X --arg-file body=<檔>`）。
        /// ⚠ 發文的 Cmd 住在宿主那一層 ⇒ 由宿主用它自己的型別動態組，⛔ 共用層不寫死指令名（TASK-0406）。
        /// </summary>
        string TavernPostHint(string iPersona);
    }

    /// <summary>本地跑、需要宿主給根的 Cmd 共用殼（舊名 `MorningLocalCmd`）。</summary>
    public abstract class SCP_LocalRootsCmd : SCP_Cmd
    {
        /// <summary>宿主在啟動時掛上。</summary>
        public static ISCP_LocalCmdHost? Host { get; set; }

        /// <summary>這一步做完之後照哪一行走（印在 `## next` 下面）。⚠ 指令名請用 <see cref="SCP_CmdRegistry.InvokeOf{T}"/> 組。</summary>
        protected abstract string CliNextHint { get; }

        protected static IEnumerable<SCP_CmdArgSpec> MorningSpecs()
        {
            yield return new SCP_CmdArgSpec("persona",
                "要對誰做這一步。⚠ **一律顯式**：猜錯的代價是動到別人的 session", iRequired: true);
        }

        /// <summary>本步的主體。回傳要附在結果最後的回傳檔路徑（可為 null）。</summary>
        protected abstract string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult);

        /// <summary>
        /// 子類宣告了 <see cref="TargetDataRootSpec"/> 時設 true：呼叫端給了 `target_data_root` ⇒ 以**資料根**選專案（TASK-0366）。
        /// <para>🩸 為什麼不用 `data_root`：CLI 會替宣告了它的 Cmd **自動補**設定檔那一格（而補進去的值被當成顯式給的），
        /// ⇒ 用它選專案時，「--project Bar 卻沒帶 data_root」會被補成 LY 的根、跟專案名對打。新名字沒有人會替它補。</para>
        /// </summary>
        protected virtual bool AcceptsTargetDataRoot => false;

        protected static SCP_CmdArgSpec TargetDataRootSpec() => new SCP_CmdArgSpec("target_data_root",
            "呼叫端宣告的資料根 —— 必須是設定的那一組，比不到就擋（⛔ 不服務第二棵樹）");

        public sealed override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            ISCP_LocalCmdHost? aHost = Host;
            if (aHost == null)
                return SCP_CmdResult.Fail(70, "✗ 宿主沒有裝上本地 Cmd 的宿主能力（SCP_LocalRootsCmd.Host）—— 程式錯誤，不是用法錯");
            string aTargetDr = AcceptsTargetDataRoot ? iArgs.Get("target_data_root").Trim() : "";
            if (!aHost.TryResolve(aTargetDr, out SCP_LocalTarget aWhere, out string aErr, out string aHint))
                return SCP_CmdResult.Fail(2, "✗ " + aErr, "  " + aHint);

            var aRoots = new SCP_MorningRoots
            {
                DataRoot = aWhere.DataRoot.Replace('\\', '/'),
                LettersRoot = SCP_DataPaths.Letters(new SCP_DataRoot(aWhere.DataRoot)).Value,
                ProjectRoot = aWhere.ProjectRoot.Replace('\\', '/'),
            };
            var aResult = new SCP_CmdResult();
            // 定語第一行 —— 在做任何事之前就印，失敗訊息也要帶著它。
            aResult.Lines.Add($"{aHost.WhereLine} @ {aWhere.Describe}");
            aResult.AddValue("delegate_host", aHost.HostId);
            aResult.AddValue("project", aWhere.ProjectName);
            aResult.AddValue("data_root", aRoots.DataRoot);
            // 詞典根解不出來 ⇒ 說出來、本次不附詞典（⛔ 不靜默、⛔ 不猜一個根 —— TASK-0390）。
            if (aWhere.GlossaryRoot != null) aRoots.GlossaryRoot = aWhere.GlossaryRoot;
            else aResult.Lines.Add($"⚠ 詞典根解不出來（{aWhere.GlossaryError}）—— 本次不附詞典附註、不讀出生證明");

            string? aPayload;
            try { aPayload = Run(aRoots, iArgs, aResult); }
            catch (Exception e)
            {
                aResult.ExitCode = 70;
                aResult.Lines.Add($"✗ {e.GetType().Name}: {e.Message}");
                return aResult;
            }
            if (aResult.Ok && CliNextHint.Length > 0)
            {
                aResult.Lines.Add("## next（照這行走）");
                aResult.Lines.Add("   " + CliNextHint);
            }
            if (aPayload != null) aResult.AddOutput(aPayload);   // 宿主會印「📄 回傳檔」—— 這裡不再印一次
            return aResult;
        }

        /// <summary>讀一個回傳值（沒有回空字串）。</summary>
        protected static string Value(SCP_CmdResult iR, string iKey)
        {
            foreach (KeyValuePair<string, string> kv in iR.Values) if (kv.Key == iKey) return kv.Value;
            return "";
        }
    }
}
