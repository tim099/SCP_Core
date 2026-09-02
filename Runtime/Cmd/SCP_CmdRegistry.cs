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
        /// 掃描所有載入的組件，找出非抽象、有公開無參數建構子的 <see cref="SCP_Cmd"/> 子類別。
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

            try { return aCmd.Execute(aArgs); }
            catch (Exception e)
            {
                // Cmd 爆掉不是「用法錯」—— exit code 要分得出來，否則腳本會把程式 bug 當成自己打錯。
                return SCP_CmdResult.Fail(70,
                    "✗ " + aCmd.Name + " 執行時丟出例外：" + e.GetType().Name + ": " + e.Message);
            }
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
