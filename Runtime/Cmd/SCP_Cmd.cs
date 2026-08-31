// 區塊職責：**SCP_CMD 的指令基底與回傳型別** —— 一支 Cmd 是什麼、它回什麼。
// 物理意義：這套系統跟 UCL_Core 的 AgentCommand 是同一個概念，但**沒有 queue**：
//           CLI 直接呼叫 C#，同一個 process 同步跑完回來。
//           ⇒ 沒有 trigger 檔、沒有 Watcher、沒有「從 queue 消失代表結束」那套推論，
//             也就沒有那套推論會漂的那些坑。回傳值就是回傳值。
// 數值影響：本檔零 IO。
//
// 📌 與 UCL_Core AgentCommand 的對照（有意保留的相同 / 有意不同）：
//   相同：宣告式的參數規格、機器可讀的回報（📄 產出檔 / 🔢 純量）、help 由系統產生不是手寫。
//   不同：① 無 queue（見上）② 未宣告的參數名一律擋（UCL 是靜默取預設 ⇒ BUG-14）
//        ③ 不依賴 Unity —— 本組檔案只用 netstandard2.1 的東西，任何宿主都跑得動。
using System;
using System.Collections.Generic;

namespace SCP.Core.Cmd
{
    /// <summary>
    /// 這支 Cmd 的工作**實際在哪裡發生** —— 移植進度的機器可讀欄位。
    /// <para>⚠ 存在的理由是《無定語的成功》：一支委派出去的 Cmd 跑完之後，輸出長得跟原生的
    /// 一模一樣，於是「我在 CLI 上跑完了」與「Editor 替我跑完了」變成同一句話 ——
    /// 而後者在 Editor 沒開時會失敗，且失敗訊息看起來像 CLI 自己的 bug。</para>
    /// <para>📌 它同時是**待移植清單的唯一落點**：清單由 <c>help</c> 從這個欄位印出來，
    /// 不另外維護一份 md —— 兩份清單遲早各說各話，而且兩邊都不報錯。</para>
    /// </summary>
    public enum SCP_CmdPortStatus
    {
        /// <summary>本 process 自己跑完，不需要任何外部宿主。</summary>
        Native = 0,

        /// <summary>
        /// 委派給 Unity Editor 執行（走 AgentCommand 檔案協議）。
        /// <para>⚠ 這代表 **Editor 沒開就跑不完** —— 宣告成這個值的 Cmd 有義務在輸出裡
        /// 講出「這一步是誰跑的、在哪個資料根」。</para>
        /// </summary>
        DelegatedToUnity = 1,

        /// <summary>還沒有實作 —— **登記在案的缺口**，不是「打錯名字」。</summary>
        NotPorted = 2,
    }

    /// <summary>一支 Cmd 的執行結果。</summary>
    public sealed class SCP_CmdResult
    {
        /// <summary>0 ＝ 成功。非 0 ＝ 失敗（呼叫端可直接當 process exit code）。</summary>
        public int ExitCode;

        /// <summary>人可讀的輸出行。</summary>
        public List<string> Lines = new List<string>();

        /// <summary>產出檔的路徑（對應 run_cmd 的 `📄 回傳檔`）。**印出來的路徑才是真的**，不要背路徑。</summary>
        public List<string> Outputs = new List<string>();

        /// <summary>
        /// 純量回報（對應 run_cmd 的 `🔢 key = value`）。
        /// <para>⚠ 跟路徑**分開放**：混在一起會讓 seq 這種數字被當成路徑去開（UCL 端的血證）。</para>
        /// </summary>
        public List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();

        public bool Ok => ExitCode == 0;

        public static SCP_CmdResult Success(params string[] iLines)
        {
            var aResult = new SCP_CmdResult();
            aResult.Lines.AddRange(iLines);
            return aResult;
        }

        /// <summary>失敗。⚠ 訊息要說「哪一格不成立」，不是「失敗了」。</summary>
        public static SCP_CmdResult Fail(int iExitCode, params string[] iLines)
        {
            var aResult = new SCP_CmdResult { ExitCode = iExitCode };
            aResult.Lines.AddRange(iLines);
            return aResult;
        }

        public SCP_CmdResult AddValue(string iKey, string iValue)
        {
            Values.Add(new KeyValuePair<string, string>(iKey, iValue));
            return this;
        }

        public SCP_CmdResult AddOutput(string iPath) { Outputs.Add(iPath); return this; }
    }

    /// <summary>
    /// 一支 Cmd。子類別只要有**公開無參數建構子**就會被 <see cref="SCP_CmdRegistry"/> 找到。
    /// </summary>
    public abstract class SCP_Cmd
    {
        /// <summary>指令名（呼叫端打的字）。⚠ 這是**契約**：進了別人的腳本就不能隨便改。</summary>
        public abstract string Name { get; }

        /// <summary>一句話說明這支 Cmd 做什麼（印在 help 清單）。</summary>
        public abstract string Summary { get; }

        /// <summary>參數規格。沒有參數就回空陣列（那也是一份宣告，不是「還沒寫」）。</summary>
        public virtual IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => Array.Empty<SCP_CmdArgSpec>();

        /// <summary>比 Summary 長的說明（印在 `help &lt;name&gt;`）。可留空。</summary>
        public virtual string Details => "";

        /// <summary>一行可以照抄的範例。可留空。</summary>
        public virtual string Example => "";

        /// <summary>
        /// 這支的工作實際在哪裡發生（預設 <see cref="SCP_CmdPortStatus.Native"/>）。
        /// <para>⚠ 預設值是 Native 而不是「未宣告」——因為絕大多數 Cmd 真的是本地跑完；
        /// 而委派或缺口是**特例**，特例才該被要求開口。</para>
        /// </summary>
        public virtual SCP_CmdPortStatus PortStatus => SCP_CmdPortStatus.Native;

        /// <summary>
        /// 非 Native 時：**還差哪一塊才能原生化**（印在 help）。一句話，講的是缺口不是願望。
        /// <para>例：「profile 接縫 → email registry → lock/token/memo」。</para>
        /// <para>⚠ Native 的 Cmd 留空 —— 有值代表「這裡還欠著東西」，
        /// 而一個永遠有值的欄位等於沒有欄位。</para>
        /// </summary>
        public virtual string PortNote => "";

        /// <summary>
        /// 執行。**參數已經驗過**（未宣告的名字、必填、Choices 都在 Registry 擋掉了）。
        /// <para>⚠ 丟例外不是罪：Registry 會接住並轉成 exit code ＋ 型別名稱 ＋ 訊息。
        /// 但**能講清楚的失敗請自己回 Fail** —— 例外的訊息是給維護者的，Fail 的訊息是給使用者的。</para>
        /// </summary>
        public abstract SCP_CmdResult Execute(SCP_CmdArgs iArgs);
    }
}
