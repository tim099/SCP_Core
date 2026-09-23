// 區塊職責：**SCP_CMD 的參數規格與參數包** —— 一支 Cmd 宣告它吃哪些參數，呼叫端給的值走這裡驗。
// 物理意義：Cmd 從 CLI（或任何宿主）拿到的永遠是一疊字串 `k=v`。規格存在的理由只有一個：
//           **讓「打錯的參數名」在跑起來之前就被擋下**，而不是靜默取預設值。
// 數值影響：純資料 ＋ 驗證，零 IO。
//
// 🩸 這裡的形狀是抄 UCL_Core 兩張同一天開的 bug 單（BUG-14 / BUG-15，2026-08-19）：
//   · BUG-14：沒宣告規格時 `value` 打錯名（`val=`）⇒ **靜默取空字串** ⇒ 欄位被清空。
//   · BUG-15：把它放進「必填」之後，**合法的空值進不來**（清空欄位本來就是 `value=`）。
//   兩張是**同一個表達力缺口的兩面**：修 14 當天長出 15。
//   ⇒ 所以這裡有兩種必填：<see cref="SCP_CmdArgSpec.Required"/>（要有值）與
//     <see cref="SCP_CmdArgSpec.PresenceRequired"/>（在場即可，空值合法）。
//     **「沒給」與「給了空的」是兩件事，把它們壓成一件的驗證器會擋掉一半的合法輸入。**
//
// ⭐ 而本系統比 UCL 多做一格（因為是重新設計，沒有既有呼叫端要相容）：
//   **沒宣告的參數名一律擋下**。BUG-14 的根因不是「required 沒宣告」，是
//   「打錯的名字沒有人反對」—— 只要未知參數會出聲，那一整族錯就不存在了。
using System;
using System.Collections.Generic;

namespace SCP.Core.Cmd
{
    /// <summary>一個參數的規格。</summary>
    public sealed class SCP_CmdArgSpec
    {
        /// <summary>canonical 名（呼叫端要打的字）。</summary>
        public string Name = "";

        /// <summary>一句話說明「這個參數是什麼意思」（給人讀，會印在 help）。</summary>
        public string Description = "";

        /// <summary>必填且**要有值**。空字串算沒給。</summary>
        public bool Required;

        /// <summary>必須**在場**，但值可以是空字串（例：把某個欄位清空）。</summary>
        public bool PresenceRequired;

        /// <summary>沒給時的值。⚠ 只有非必填的參數才有意義。</summary>
        public string Default = "";

        /// <summary>合法值清單（空 ＝ 不限）。給了就會 enforce，並印在 help。</summary>
        public string[] Choices = Array.Empty<string>();

        public SCP_CmdArgSpec() { }

        public SCP_CmdArgSpec(string iName, string iDescription, bool iRequired = false,
            string iDefault = "", string[]? iChoices = null, bool iPresenceRequired = false)
        {
            Name = iName;
            Description = iDescription;
            Required = iRequired;
            Default = iDefault;
            Choices = iChoices ?? Array.Empty<string>();
            PresenceRequired = iPresenceRequired;
        }

        /// <summary>help 用的一行摘要。</summary>
        public string HelpLine()
        {
            string aFlag = Required ? "必填" : PresenceRequired ? "必須在場（可空）" : "選填";
            string aExtra = "";
            if (Choices.Length > 0) aExtra += "　可選：" + string.Join(" / ", Choices);
            if (!Required && !PresenceRequired && Default.Length > 0) aExtra += "　預設：" + Default;
            return "  " + Name + "　(" + aFlag + ")" + aExtra + "\n      " + Description;
        }
    }

    /// <summary>
    /// 驗過的參數包。**只能透過 <see cref="Bind"/> 取得** —— 沒驗過的參數包不存在，
    /// 這樣「忘了驗」就不是一個可能的狀態，而不是靠每支 Cmd 記得自己驗。
    /// </summary>
    public sealed class SCP_CmdArgs
    {
        readonly Dictionary<string, string> m_Values;

        // ── TASK-0289：兜底偵測「使用者給了、而這一趟從來沒被讀」的參數 ──────────────
        // 🩸 為什麼要它：ArgSpec 預檢的射程是「**這支 Cmd** 的參數集合」不是「**這個 op** 的」
        //   ⇒ 跨 op 的參數名**靜默通過並被忽略**，而失效樣子是「它回了一個合理的東西」。
        //   已量到兩個樣本：`canvas op=view --arg size=12x12` ⇒ exit 0 並回整張 2048（2026-09-20）／
        //   `coding op=status --arg scope=<新路徑>` ⇒ exit 0 印「已更新」而範圍原封不動（2026-09-23）。
        // ⭐ 判準：**作者什麼都不必宣告** —— 靠「有沒有被 Get 過」這個事實，
        //   而不是靠有人記得去維護一張表。要靠記得的防護等於沒有防護。
        readonly HashSet<string> m_Explicit;
        readonly HashSet<string> m_Read = new HashSet<string>(StringComparer.Ordinal);

        SCP_CmdArgs(Dictionary<string, string> iValues, HashSet<string> iExplicit)
        {
            m_Values = iValues;
            m_Explicit = iExplicit;
        }

        /// <summary>
        /// 使用者**顯式給了值**、而這一趟**從來沒有被讀取**的參數名（排序後）。
        /// <para>⚠ 這是**寧可漏報不誤報**的保守判準：存取過 <see cref="All"/> 就視同全部讀過
        /// （那條路繞過 <see cref="Get"/>，分不出誰真的被用到）。
        /// ⛔ 理由：一盞會誤報的燈，第三天就會被所有人學會忽略 —— 那比沒有燈更糟。</para>
        /// </summary>
        public List<string> UnreadExplicitArgs()
        {
            var aOut = new List<string>();
            foreach (string aKey in m_Explicit)
                if (!m_Read.Contains(aKey)) aOut.Add(aKey);
            aOut.Sort(StringComparer.Ordinal);
            return aOut;
        }

        /// <summary>
        /// 對照規格驗一疊原始參數。回 (參數包, 錯誤清單)；有錯時參數包是 null。
        /// <para>驗三件事：未宣告的名字、必填缺值、不在 Choices 裡的值。
        /// **一次回報全部** —— 一次修一個錯要跑三趟，而每一趟都是一次重新犯錯的機會。</para>
        /// </summary>
        public static (SCP_CmdArgs? Args, List<string> Errors) Bind(
            IReadOnlyList<SCP_CmdArgSpec> iSpecs, IReadOnlyDictionary<string, string> iRaw)
        {
            var aErrors = new List<string>();
            var aBySpec = new Dictionary<string, SCP_CmdArgSpec>(StringComparer.Ordinal);
            foreach (SCP_CmdArgSpec aSpec in iSpecs) aBySpec[aSpec.Name] = aSpec;

            // ① 未宣告的名字 —— 這一格是 BUG-14 那一整族的根治。
            foreach (string aKey in iRaw.Keys)
            {
                if (aBySpec.ContainsKey(aKey)) continue;
                var aKnown = new List<string>();
                foreach (SCP_CmdArgSpec aSpec in iSpecs) aKnown.Add(aSpec.Name);
                aErrors.Add("不認得的參數 '" + aKey + "'　（這支 Cmd 吃的是："
                            + (aKnown.Count > 0 ? string.Join(" , ", aKnown) : "（沒有參數）") + "）");
            }

            var aValues = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SCP_CmdArgSpec aSpec in iSpecs)
            {
                bool aPresent = iRaw.TryGetValue(aSpec.Name, out string? aValue);
                string aResolved = aPresent ? (aValue ?? "") : aSpec.Default;

                if (aSpec.Required && aResolved.Length == 0)
                    aErrors.Add("缺必填參數 '" + aSpec.Name + "'：" + aSpec.Description);
                else if (aSpec.PresenceRequired && !aPresent)
                    aErrors.Add("參數 '" + aSpec.Name + "' 必須在場（值可以是空的）：" + aSpec.Description);

                if (aSpec.Choices.Length > 0 && aResolved.Length > 0
                    && Array.IndexOf(aSpec.Choices, aResolved) < 0)
                {
                    aErrors.Add("參數 '" + aSpec.Name + "' 的值 '" + aResolved
                                + "' 不在可選清單裡：" + string.Join(" / ", aSpec.Choices));
                }
                aValues[aSpec.Name] = aResolved;
            }

            // TASK-0289：記下「使用者**顯式**給的」是哪幾個（⛔ 不含吃預設值的那些 ——
            //   預設值沒被讀是常態，把它們一起報會讓這盞燈每天亮一整排）。
            var aExplicit = new HashSet<string>(StringComparer.Ordinal);
            foreach (string aKey in iRaw.Keys)
                if (aBySpec.ContainsKey(aKey)) aExplicit.Add(aKey);

            return aErrors.Count > 0 ? (null, aErrors) : (new SCP_CmdArgs(aValues, aExplicit), aErrors);
        }

        /// <summary>
        /// 取值。⚠ **取一個沒宣告的名字會丟例外** —— 那是程式錯誤（Cmd 自己跟自己的規格不同步），
        /// 不是使用者輸入問題，所以不該回空字串讓它繼續跑。
        /// </summary>
        public string Get(string iName)
        {
            if (!m_Values.TryGetValue(iName, out string? v))
                throw new InvalidOperationException(
                    "Cmd 取了一個自己沒宣告的參數 '" + iName + "' —— 規格與實作不同步（這是程式錯誤）");
            m_Read.Add(iName);   // TASK-0289：記下「這一趟真的讀過它」
            return v;
        }

        /// <summary>取整數。轉不動就回 <paramref name="iFallback"/> 並把原因寫進 oWhy。</summary>
        public int GetInt(string iName, int iFallback, out string oWhy)
        {
            string aRaw = Get(iName);
            if (aRaw.Length == 0) { oWhy = ""; return iFallback; }
            if (int.TryParse(aRaw, out int aValue)) { oWhy = ""; return aValue; }
            oWhy = "參數 '" + iName + "' 不是整數（收到 '" + aRaw + "'）⇒ 用 " + iFallback;
            return iFallback;
        }

        /// <summary>
        /// 全部參數值。⚠ 存取它會讓 <see cref="UnreadExplicitArgs"/> **視同全部讀過**（TASK-0289）——
        /// 這條路繞過 <see cref="Get"/>，分不出誰真的被用到，⇒ 保守處理，⛔ 不製造假警告。
        /// </summary>
        public IReadOnlyDictionary<string, string> All
        {
            get
            {
                foreach (string aKey in m_Values.Keys) m_Read.Add(aKey);
                return m_Values;
            }
        }
    }
}
