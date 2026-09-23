// 區塊職責：**SCP_CMD 的目錄與派遣** —— 有哪些 Cmd、名字對應誰、參數驗完再跑。
// 物理意義：沒有 queue ⇒ 派遣就是一次同步呼叫。本檔是「字串 → 執行」中間**唯一**那一層，
//           所以驗證放這裡而不是每支 Cmd 自己驗：放在必經路上的機械，才不需要每個人記得。
// 數值影響：Discover() 會掃一次載入的型別（走 SCP_Reflect.AllTypes —— 不重造第二套掃描器），
//           結果快取；之後純查表。
//
// ⚠ 認不得的名字回**錯誤 ＋ did-you-mean**，不回一支預設 Cmd：
//   回預設會讓「你要的那支不存在」長得像「它跑了但沒做事」。
using System;
using System.Collections.Generic;
using SCP.Core.Reflect;

namespace SCP.Core.Cmd
{
    public static class SCP_CmdRegistry
    {
        /// <summary>
        /// 宿主怎麼呼叫這套系統（印在錯誤訊息與範例裡）。預設是裸的 <c>cmd</c>；
        /// Senate 這種宿主在啟動時設成 <c>"senate cmd"</c>。
        /// <para>⚠ 存在的理由：**SCP_Core 不准知道任何宿主的動詞**。
        /// 第一版把 `scmd` 寫死在訊息裡，於是 CLI 改個動詞就會讓共用層對使用者說一個
        /// **打了不會動**的指令 —— 而那種錯不會編譯失敗、不會有人回報，
        /// 只會讓照著訊息打的人以為自己打錯。</para>
        /// </summary>
        public static string InvocationHint = "cmd";

        /// <summary>
        /// Discover／Register 的鎖。
        /// <para>🩸 2026-09-02：Senate Server 三條 lane 同時第一次 <see cref="Find"/> ⇒ 三個 thread 同時進
        /// <see cref="Discover"/> 清空再填同一個字典 ⇒ <c>InvalidOperationException: Operations that change
        /// non-concurrent collections must have exclusive access</c>，兩條 lane 整批失敗。
        /// Editor 端單執行緒從沒撞過 —— **Server 是這套 registry 第一個多執行緒的消費者**，
        /// 所以這格在 Editor 那側永遠不會現形。讀（Find／All）在 Discover 完成後是純讀，不鎖。</para>
        /// </summary>
        static readonly object s_Lock = new object();

        /// <summary>組一句「照著打就會動」的指令（自動帶上宿主動詞）。</summary>
        public static string Invoke(string iTail)
            => (string.IsNullOrWhiteSpace(InvocationHint) ? "" : InvocationHint + " ") + iTail;

        static readonly Dictionary<string, SCP_Cmd> s_Commands =
            new Dictionary<string, SCP_Cmd>(StringComparer.OrdinalIgnoreCase);
        static bool s_Discovered;

        /// <summary>掃描期間遇到的問題（型別建不起來之類）。⚠ 有內容時 help 要印出來 —— 少了一支 Cmd 不該安靜。</summary>
        public static readonly List<string> DiscoveryWarnings = new List<string>();

        /// <summary>
        /// 掃描所有載入的組件，找出 <b>top-level public</b>、非抽象、有公開無參數建構子的 <see cref="SCP_Cmd"/> 子類別。
        /// <para>⛔ 巢狀與非 public 的子類**不收**（TASK-0266）—— 測試探針繼承產品 Cmd 時會連指令名一起繼承，
        /// 那條路上「說明」與「派遣目標」會分家而沒有任何一層在執行時說出來。</para>
        /// <para>⚠ 同名衝突不覆蓋、不靜默 —— 兩支同名的 Cmd 代表有人搬檔案時忘了改名字，
        /// 而後贏的那支是隨機的（型別列舉順序）。留第一支並把衝突記進 <see cref="DiscoveryWarnings"/>。</para>
        /// </summary>
        public static void Discover(bool iForce = false)
        {
            if (s_Discovered && !iForce) return;
            lock (s_Lock)
            {
                if (s_Discovered && !iForce) return;   // 等鎖的那幾個 thread 進來時多半已經有人做完了
                DiscoverUnlocked();
            }
        }

        static void DiscoverUnlocked()
        {
            s_Commands.Clear();
            DiscoveryWarnings.Clear();

            foreach (Type aType in SCP_Reflect.AllTypes(w => DiscoveryWarnings.Add(w)))
            {
                if (aType.IsAbstract || !typeof(SCP_Cmd).IsAssignableFrom(aType)) continue;

                // ── 產品指令一律是 top-level public（TASK-0266）────────────────────
                // 🩸 為什麼這道閘存在：`Senate.Cli.SelfTest+TavernLaneProbe` 是為了讀 protected
                //    `Lane()` 而開的**測試用子類**，它連指令名一起繼承 ⇒ 兩支同名進同一張表，
                //    而掃描順序讓探針先進去、真正的 `Cmd_TavernWrite` 被下面那格 continue 掉。
                //    ⚠ 那天它沒咬到人**不是運氣，是繼承**：探針沒 override 任何成員，
                //    行為與母類逐位元相同。⇒ 失效條件在未來：哪天探針 override 了什麼，
                //    產品指令就靜默換成探針行為，而可觀測面（同一行警告、同一份 help）**一模一樣**。
                // ⛔ 所以這裡治的不是「那一隻」（改名只治一隻），是讓它**結構上進不來**。
                if (aType.IsNested)
                {
                    // 巢狀型別是宿主型別的實作細節 —— 測試探針、內部 helper 都長這樣。
                    // ⚠ 這格**刻意不出聲**：它不是「少了一支 Cmd」，是一條宣告過的收錄條件。
                    //   出聲的話每次 `cmd` 都會印一行永遠不會有人去修的雜訊。
                    continue;
                }
                if (!aType.IsPublic)
                {
                    // top-level 卻不是 public ⇒ 這比較像**有人忘了寫 public**，不是刻意的實作細節。
                    // ⇒ 這格要出聲：靜默少一支的症狀是「它明明在那裡卻叫不到」。
                    DiscoveryWarnings.Add("略過 " + aType.FullName + "：Cmd 必須是 public（top-level）");
                    continue;
                }
                if (aType.GetConstructor(Type.EmptyTypes) == null)
                {
                    DiscoveryWarnings.Add("略過 " + aType.FullName + "：沒有公開無參數建構子");
                    continue;
                }

                SCP_Cmd aCmd;
                try { aCmd = (SCP_Cmd)Activator.CreateInstance(aType)!; }
                catch (Exception e)
                {
                    DiscoveryWarnings.Add("略過 " + aType.FullName + "：建構失敗 " + e.GetType().Name + ": " + e.Message);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(aCmd.Name))
                {
                    DiscoveryWarnings.Add("略過 " + aType.FullName + "：Name 是空的");
                    continue;
                }
                if (s_Commands.TryGetValue(aCmd.Name, out SCP_Cmd? aExisting))
                {
                    DiscoveryWarnings.Add("指令名撞名 '" + aCmd.Name + "'："
                                          + aExisting.GetType().FullName + " 與 " + aType.FullName
                                          + " —— 留前者，後者不會被派遣到");
                    continue;
                }
                s_Commands[aCmd.Name] = aCmd;
            }
            s_Discovered = true;
        }

        /// <summary>所有 Cmd，依名字排序。</summary>
        public static IReadOnlyList<SCP_Cmd> All()
        {
            Discover();
            var aList = new List<SCP_Cmd>(s_Commands.Values);
            aList.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            return aList;
        }

        public static SCP_Cmd? Find(string iName)
        {
            Discover();
            return s_Commands.TryGetValue(iName ?? "", out SCP_Cmd? aCmd) ? aCmd : null;
        }

        /// <summary>顯式登記（給不想被反射掃到、或動態產生的 Cmd）。同名一樣不覆蓋。</summary>
        public static bool Register(SCP_Cmd iCmd)
        {
            Discover();
            if (iCmd == null || string.IsNullOrWhiteSpace(iCmd.Name)) return false;
            lock (s_Lock)
            {
                if (s_Commands.ContainsKey(iCmd.Name)) return false;
                s_Commands[iCmd.Name] = iCmd;
                return true;
            }
        }

        /// <summary>
        /// 派遣：查表 → 驗參數 → 執行。**這三步的失敗各自有自己的 exit code**，
        /// 因為呼叫端要分得出「打錯指令」「參數不對」「Cmd 自己爆了」。
        /// </summary>
        /// <returns>exit code：0 成功／2 用法錯（找不到指令或參數不合）／1 Cmd 回報失敗／70 Cmd 丟例外。</returns>
        public static SCP_CmdResult Dispatch(string iName, IReadOnlyDictionary<string, string> iRawArgs)
        {
            SCP_Cmd? aCmd = Find(iName);
            if (aCmd == null)
            {
                var aResult = SCP_CmdResult.Fail(2, "✗ 認不得的指令 '" + iName + "'");
                List<string> aNear = NearNames(iName);
                if (aNear.Count > 0) aResult.Lines.Add("  你是不是要打：" + string.Join(" / ", aNear));
                aResult.Lines.Add("  全部可用指令：" + Invoke("help"));
                return aResult;
            }

            (SCP_CmdArgs? aArgs, List<string> aErrors) =
                SCP_CmdArgs.Bind(aCmd.ArgSpecs, iRawArgs ?? new Dictionary<string, string>());
            if (aArgs == null)
            {
                var aResult = SCP_CmdResult.Fail(2, "✗ " + aCmd.Name + " 的參數不合：");
                foreach (string aError in aErrors) aResult.Lines.Add("  · " + aError);
                aResult.Lines.Add("  這支 Cmd 的參數說明：" + Invoke("help " + aCmd.Name));
                return aResult;
            }

            try
            {
                SCP_CmdResult aOut = aCmd.Execute(aArgs);
                WarnUnreadArgs(aCmd, aArgs, aOut);
                return aOut;
            }
            catch (Exception e)
            {
                // Cmd 爆掉不是「用法錯」—— exit code 要分得出來，否則腳本會把程式 bug 當成自己打錯。
                var aResult = SCP_CmdResult.Fail(70,
                    "✗ " + aCmd.Name + " 執行時丟出例外：" + e.GetType().Name + ": " + e.Message);
                aResult.Exception = e;   // 原始現場留給錯誤報告；這一行訊息留給使用者
                return aResult;
            }
        }

        // ===========================================================
        // 區塊職責：**兜底出聲** —— 使用者顯式給了、而這一趟從來沒被讀的參數（TASK-0289）。
        // 物理意義：ArgSpec 預檢的射程是「這支 Cmd 的參數集合」不是「這個 op 的」
        //           ⇒ 跨 op 的參數名靜默通過並被忽略。已量到兩個樣本（見 SCP_CmdArgs 的註解）。
        // 數值影響：⛔ **不改 exit code、不改任何狀態** —— 只加訊息行與一格機器讀數。
        //
        // ⛔ 為什麼不擋（不把它變成失敗）：
        //   ① 這是**執行後**才知道的事 —— 副作用已經發生，改 exit code 只會讓呼叫端
        //      拿到一個跟事實不符的「失敗」（事情其實做了一半）。
        //   ② 擋要在執行前，而那需要「這個參數屬於哪些 op」的**宣告**；本層刻意不要求那個宣告
        //      —— 要靠作者記得去維護的防護等於沒有防護。⇒ 兩者是互補的兩層，⛔ 不是替代。
        // ⚠ 所以這一層的承諾只有一個：**它不會安靜**。⛔ 別把它讀成「打錯參數會被擋下來」。
        // ===========================================================
        static void WarnUnreadArgs(SCP_Cmd iCmd, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
        {
            if (iArgs == null || ioResult == null) return;

            // ── 失敗閘：這一趟沒成功就**一個字都不說** ───────────────────────
            // 🩸 2026-09-23 QA @kiara 判本單 ③ 不通過，兩個活體（我先交了兩個、她又補一個更難看的）：
            //   `tavern-read kind=task_next`（缺 agent_id ⇒ exit 2）報「top 沒被讀」——
            //   而 `top` **正是 task_next 的正牌參數**（補上 agent_id 後同一支就讀了它，對照組量過）；
            //   `canvas op=place`（缺 persona ⇒ exit 2）把 `color`／`pay` 這兩個最核心的參數報成沒被讀。
            // ⇒ 成因不是「屬於另一個 op」，是**失敗路徑提早 return，後面的 Get 根本來不及跑**。
            // 🔴 而更難看的是下面那句「上面的結果**照常發生了**」：在一趟被擋在門外、
            //   什麼都沒做的執行後面印它，是**語意自相矛盾** —— 它會讓讀的人以為副作用發生了。
            // ⇒ 判準：**本層的整段語意建立在「事情已經做了」之上** ⇒ 沒做成就不適用，⛔ 不是「少報一點」。
            // ⚠ 代價寫明：失敗路徑上**真的**打錯參數這件事，本層不再報 —— 那一格歸 ArgSpec 預檢（執行前）。
            // 📌 `Ok` 的定義逐字是 `=> ExitCode == 0` ⇒ 這裡**只查一次**：
            //   把衍生屬性跟它的來源並排檢查，會讓讀的人以為它們是兩個獨立的事實。
            if (!ioResult.Ok) return;

            // ── 射程閘：只對**有 op 語意**的 Cmd 檢查 ─────────────────────────
            // 🩸 為什麼收窄（2026-09-23 實測，⛔ 不是為了少寫）：不收窄的話 `server-ping` 會亮
            //   「persona 沒被讀」—— 而**那個 persona 不是使用者打的**。
            // ⭐ 根因 2026-09-23 由 QA @kiara 查明（我當天 grep `Senate.Cli` 沒找到，標了「未查明」）：
            //   `ServerDelegateCmd.cs:179-181` 逐字 `foreach (aSpec in ArgSpecs) if (name != "timeout")
            //   aSend[name] = iArgs.Get(name)` ⇒ **把這支宣告過的每一個參數都打包送給 Server**，
            //   而 `persona` 在 `Cmd_ServerPing.cs:24` 就是宣告過的（它是**客戶端路由用**的，決定發射分道）。
            //   Server 端 `ExecuteOnServer` 只 `Get` 了 `echo`／`fail`／`sleep` ⇒ persona 在 iRaw 裡、從未被讀。
            // ⇒ 它是**轉發端把客戶端參數塞進執行端 payload** 造成的落差，⛔ 不是使用者打錯。
            // ⚠ 而本閘是**繞過它，不是解決它** —— 真正的解要嘛轉發端別送路由參數、
            //   要嘛讓執行端分得出「使用者打的」與「轉發端補的」。那是另一個修法，⛔ 不在本單射程。
            // ⛔ 一盞會誤報的燈第三天就沒人看 —— 那比沒有燈更糟（本單驗收④ 自己寫的那條）。
            // ⇒ 本單的症狀是「**跨 op** 的參數被靜默吃掉」⇒ 射程就收到有 op 語意的那些：
            //   宣告了 `op` 或 `kind` 的 Cmd。兩個已知樣本（`canvas op=`／`coding op=`）都在內。
            // ⚠ 代價要寫明：**沒有 op/kind 的 Cmd 不受本層保護** —— ⛔ 別把它讀成「全庫都擋住了」。
            bool aHasOpSemantics = false;
            foreach (SCP_CmdArgSpec aSpec in iCmd.ArgSpecs)
            {
                if (aSpec.Name != "op" && aSpec.Name != "kind") continue;
                aHasOpSemantics = true;
                break;
            }
            if (!aHasOpSemantics) return;

            List<string> aUnread = iArgs.UnreadExplicitArgs();
            if (aUnread.Count == 0) return;

            ioResult.Lines.Add("⚠ 這一趟有 " + aUnread.Count + " 個參數**給了而從來沒被讀**："
                               + string.Join(" , ", aUnread));
            ioResult.Lines.Add("  ⛔ 它們是這支 Cmd 的合法參數（所以預檢放行），"
                               + "而**這一條執行路徑沒有用到它們** —— 多半是它屬於另一個 op。");
            ioResult.Lines.Add("  ⚠ 上面的結果**照常發生了** —— 這一行只是告訴你：你以為會生效的那一格，沒有。");
            ioResult.Lines.Add("  合法參數與各自屬於哪個 op：" + Invoke("help " + iCmd.Name));
            ioResult.AddValue("unread_args", string.Join(",", aUnread));
        }

        /// <summary>粗略的 did-you-mean：前綴或包含。刻意不做編輯距離 —— 那需要一顆調得動的門檻。</summary>
        static List<string> NearNames(string iName)
        {
            var aOut = new List<string>();
            string aKey = (iName ?? "").ToLowerInvariant();
            if (aKey.Length == 0) return aOut;
            foreach (SCP_Cmd aCmd in All())
            {
                string aCandidate = aCmd.Name.ToLowerInvariant();
                if (aCandidate.StartsWith(aKey, StringComparison.Ordinal)
                    || aCandidate.Contains(aKey)
                    || aKey.Contains(aCandidate))
                {
                    aOut.Add(aCmd.Name);
                }
            }
            return aOut;
        }
    }
}
