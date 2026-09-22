// 區塊職責：**帳號解析的唯一實作** —— 任何字串（persona／agent／別名／帳號）→ 正式帳號 id，
//           外加銷戶名單的讀與寫。
// 物理意義：權威是 `letters/<persona>/bank/<region>.md`（Tim 2026-09-07 拍板）；
//           `_registry_meta.json` 只補三件事：system_accounts、closed_accounts，
//           與**已退居 legacy** 的 `agent_banks`（agent 名 → 帳號）。
// 數值影響：解析結果決定 ledger entry 的 `account_id` ⇒ **錯一格就是錢進錯帳戶**。
//           本類純讀，唯一的寫入是 <see cref="CloseAccount"/>（closed_accounts 的唯一寫入端）。
//
// ⛔ 為什麼這支住在 SCP_Core 而不是 Unity 那側（TASK-0269）：
//   同一條規則在 2026-09-22 之前有**三份實作**（Unity 的 645 行 resolver／python 的 292 行／
//   這裡）。三份讀同一個權威，⇒ 差異不會在當下報錯，只會在其中一份先過期的那天現形。
//   🩸 已經現形過一次：2026-08-20 `Sirius` 的帳戶改名，反向表沒跟著改 ⇒ **錯了 18 天沒有人喊**。
//
// ⚠ 路徑一律由呼叫端傳進來（`iLettersRoot` / `iDataRoot`）——
//   SCP_Core 不准知道任何宿主的安裝位置，寫死就會跨專案漂。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Letters;

namespace SCP.Core.Bank
{
    /// <summary>一次解析的結果 —— **輸入、輸出、走了哪一段**三件事一起回。</summary>
    /// <remarks>
    /// ⚠ 只回帳號字串是不夠的：「權威綁定命中」與「legacy 猜出來的」在字串上同形，
    /// 而它們的可信度差很多。⇒ <see cref="Kind"/> 與 <see cref="Trace"/> 讓呼叫端分得出來。
    /// </remarks>
    public sealed class SCP_BankResolution
    {
        public string Input = "";
        public string AccountId = "";
        public SCP_BankResolveKind Kind = SCP_BankResolveKind.Unresolved;
        public string Trace = "";

        /// <summary>輸出與輸入不同 ⇒ 這一跳真的換了東西。</summary>
        public bool Changed => !string.Equals(Input, AccountId, StringComparison.Ordinal);

        /// <summary>沒有解析到任何已知帳號 —— ⛔ 呼叫端**不要**拿它去記帳。</summary>
        public bool IsUnresolved => Kind == SCP_BankResolveKind.Unresolved;
    }

    public enum SCP_BankResolveKind
    {
        /// <summary>查無對應。**不 derive、不 mint**。</summary>
        Unresolved = 0,
        /// <summary>輸入本身就是註冊在案的正式帳號。</summary>
        AlreadyCanonical,
        /// <summary>只差大小寫／拼法。</summary>
        CaseNormalized,
        /// <summary>經 persona 的權威綁定檔解出來的（合一模式，一跳）。</summary>
        ViaPersona,
        /// <summary>經 legacy `agent_banks` 解出來的。⚠ 那張表沒有寫入端。</summary>
        ViaAgent,
    }

    /// <summary>
    /// 帳號解析器。⚠ 有快取 —— 改過 registry 或綁定檔之後要 <see cref="Invalidate"/>。
    /// </summary>
    public static class SCP_BankAccountResolver
    {
        const string ClosedAccountsKey = "closed_accounts";

        static readonly object s_Lock = new object();
        static bool s_Loaded;
        static string s_LoadedKey = "";

        // 權威：persona（小寫）→ 帳號
        static readonly Dictionary<string, string> s_PersonaToAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        // 反向：帳號 → 綁在它底下的 persona（**由正向導出，不是另一張表**）
        // ⚠ 這張表比對用 **OrdinalIgnoreCase**，⛔ 不是 Ordinal（TASK-0270，2026-09-22）：
        //   帳本寫的 `account_id` 是小寫（`sirius` / `myth`），綁定檔存的是原拼法（`Sirius` / `Myth`）。
        //   Ordinal 之下 `sirius` 查到的是**空名單**，而空名單跟「這個帳戶底下真的沒有人」同形。
        //   🩸 上一版的補法是先拿 `account_id` 去跑 `Resolve` 當大小寫歸一 —— 而那是 **persona → 帳號**
        //     的表：`sirius`（persona）解出來是 `Spectre`，⇒ 帳戶 `Sirius` 的錢，券會發給 Spectre 底下三位。
        //     ⇒ 大小寫要在**帳號自己這條軸**上歸一，⛔ 不借道人到帳號的那一跳。
        static readonly Dictionary<string, List<string>> s_AccountToPersonas = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        // legacy：agent（小寫）→ 帳號
        static readonly Dictionary<string, string> s_AgentToAccount = new Dictionary<string, string>(StringComparer.Ordinal);
        // 別名（小寫）→ agent
        static readonly Dictionary<string, string> s_AliasToAgent = new Dictionary<string, string>(StringComparer.Ordinal);
        // 帳號宇宙（原拼法）與它的小寫索引
        static readonly HashSet<string> s_Canonical = new HashSet<string>(StringComparer.Ordinal);
        static readonly Dictionary<string, string> s_CanonicalByLower = new Dictionary<string, string>(StringComparer.Ordinal);
        // 已銷戶：帳號（原拼法）→ 理由
        static readonly Dictionary<string, string> s_Closed = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>丟掉快取。改過 registry／綁定檔之後**必須**叫一次。</summary>
        public static void Invalidate()
        {
            lock (s_Lock) { s_Loaded = false; s_LoadedKey = ""; }
        }

        static string RegistryPath(string iDataRoot)
            => Path.Combine(iDataRoot, "AwakenInit", "_registry_meta.json");

        // ==========================================================
        // 區塊職責：把四個資料源讀成查表。
        // ⚠ 快取鍵含三個根 —— 同一個 process 換一棵樹來問時，
        //   ⛔ 不可以回上一棵樹的答案（那種錯的樣子是「帳號突然不存在」）。
        // ==========================================================
        static void EnsureLoaded_NoLock(string iLettersRoot, string iDataRoot, string iRegion)
        {
            string aKey = iLettersRoot + "|" + iDataRoot + "|" + iRegion;
            if (s_Loaded && string.Equals(s_LoadedKey, aKey, StringComparison.Ordinal)) return;

            s_PersonaToAccount.Clear(); s_AccountToPersonas.Clear();
            s_AgentToAccount.Clear(); s_AliasToAgent.Clear();
            s_Canonical.Clear(); s_CanonicalByLower.Clear(); s_Closed.Clear();

            // ── ① registry：system_accounts / agent_banks（legacy） / agent_aliases / closed_accounts ──
            string aRegistry = RegistryPath(iDataRoot);
            if (File.Exists(aRegistry))
            {
                SCP_JsonData? aMeta = null;
                try { aMeta = SCP_JsonParser.Parse(File.ReadAllText(aRegistry)); }
                catch (Exception) { aMeta = null; }   // 壞 JSON ⇒ 當成沒有 registry，權威那層照樣成立
                if (aMeta != null)
                {
                    if (aMeta.Contains("system_accounts"))
                        foreach (string aK in aMeta["system_accounts"].Keys)
                            if (!aK.StartsWith("_", StringComparison.Ordinal)) AddCanonical(aK);

                    if (aMeta.Contains("agent_banks"))
                        foreach (string aK in aMeta["agent_banks"].Keys)
                        {
                            if (aK.StartsWith("_", StringComparison.Ordinal)) continue;
                            string aBank = aMeta["agent_banks"].GetString(aK, "");
                            if (aBank.Length == 0) continue;
                            s_AgentToAccount[aK.ToLowerInvariant()] = aBank;
                            AddCanonical(aBank);
                        }

                    if (aMeta.Contains("agent_aliases"))
                        foreach (string aK in aMeta["agent_aliases"].Keys)
                        {
                            if (aK.StartsWith("_", StringComparison.Ordinal)) continue;
                            string aTo = aMeta["agent_aliases"].GetString(aK, "");
                            if (aTo.Length > 0) s_AliasToAgent[aK.ToLowerInvariant()] = aTo;
                        }

                    if (aMeta.Contains(ClosedAccountsKey))
                        foreach (string aK in aMeta[ClosedAccountsKey].Keys)
                            if (!aK.StartsWith("_", StringComparison.Ordinal))
                                s_Closed[aK] = aMeta[ClosedAccountsKey].GetString(aK, "");
                }
            }

            // ── ② ⛔ **刻意不掃 `Treasury/accounts/`** ──
            //    🩸 我第一版掃了它（形狀抄 `SCP_Cmd_BankAudit`），對拍當場紅一筆：
            //      `Tim` 有帳戶檔而沒有任何綁定／registry 宣告 ⇒ 舊實作回「查無」、我的回 `Tim`。
            //    ⇒ 兩者的語意不一樣，而差別在**誰有資格當解析的終點**：
            //      帳戶檔是「後台開過戶」的產物，⛔ 不是「這個名字可以收錢」的宣告。
            //    📌 `bank-audit` 掃它是對的（它要算**帳號宇宙**）；解析器不掃也是對的
            //      （它要的是**權威**）。同一個目錄，兩個問題 —— ⛔ 別因為手邊有就順手加進來。

            // ── ③ 權威：逐位 persona 讀 `bank/<region>.md` ──
            //    ⭐ 反向表（帳號 → personas）**在這裡由正向導出** ——
            //      而不是去讀 registry 的 `bank_personas`（那張表沒有寫入端，已退出解析）。
            foreach (string aName in SCP_PersonaProfile.PoolNames(iLettersRoot))
            {
                string aAcc = SCP_PersonaProfile.GetBankAccount(iLettersRoot, aName, iRegion, out string _, out string _);
                if (aAcc.Length == 0) continue;
                s_PersonaToAccount[aName.ToLowerInvariant()] = aAcc;
                AddCanonical(aAcc);   // 合一：綁定值本身就是正式帳號
                if (!s_AccountToPersonas.TryGetValue(aAcc, out List<string>? aList))
                { aList = new List<string>(); s_AccountToPersonas[aAcc] = aList; }
                aList.Add(aName);
            }
            foreach (KeyValuePair<string, List<string>> aKv in s_AccountToPersonas) aKv.Value.Sort(StringComparer.Ordinal);

            s_Loaded = true;
            s_LoadedKey = aKey;
        }

        static void AddCanonical(string iAccount)
        {
            if (string.IsNullOrEmpty(iAccount)) return;
            s_Canonical.Add(iAccount);
            string aLower = iAccount.ToLowerInvariant();
            if (!s_CanonicalByLower.ContainsKey(aLower)) s_CanonicalByLower[aLower] = iAccount;
        }

        // ==========================================================
        // 區塊職責：任何字串 → 正式帳號。
        // ⚠ 段落順序就是**可信度順序**，⛔ 不要為了「多命中一個」調換它：
        //   ⓪ 權威綁定 ＞ ① 已是帳號 ＞ ② legacy agent ＞ ③ 大小寫 ＞ ④ alias→legacy ＞ ⑤ 查無。
        // ⛔ 查無時**原樣回傳並標記 Unresolved** —— 不 derive、不 mint 一個看起來合理的帳號名。
        //   （derive 出來的帳號會被 ledger 收下，而它跟真的帳號逐字同形。）
        // ==========================================================
        public static SCP_BankResolution Resolve(string iLettersRoot, string iDataRoot, string iRegion, string iInput)
        {
            var aR = new SCP_BankResolution { Input = iInput ?? "", AccountId = iInput ?? "" };
            if (string.IsNullOrEmpty(iInput))
            { aR.Kind = SCP_BankResolveKind.Unresolved; aR.Trace = "空字串"; return aR; }

            lock (s_Lock)
            {
                EnsureLoaded_NoLock(iLettersRoot, iDataRoot, iRegion);
                string aLower = iInput.ToLowerInvariant();

                // ⓪ 權威：persona 的綁定檔（合一模式，一跳到底）
                if (s_PersonaToAccount.TryGetValue(aLower, out string? aByPersona))
                {
                    aR.AccountId = aByPersona;
                    aR.Kind = aR.Changed ? SCP_BankResolveKind.ViaPersona : SCP_BankResolveKind.AlreadyCanonical;
                    aR.Trace = "【合一模式】persona `" + iInput + "` → 帳號 `" + aByPersona + "`（一跳；agent_banks 未參與）";
                    return aR;
                }

                // ① 輸入本身就是註冊帳號（精確拼法）
                if (s_Canonical.Contains(iInput))
                { aR.Kind = SCP_BankResolveKind.AlreadyCanonical; aR.Trace = "已是註冊帳號"; return aR; }

                // ② agent 名 → 帳號（⚠ legacy `agent_banks`，那張表沒有寫入端）
                if (s_AgentToAccount.TryGetValue(aLower, out string? aByAgent))
                {
                    aR.AccountId = aByAgent;
                    aR.Kind = aR.Changed ? SCP_BankResolveKind.ViaAgent : SCP_BankResolveKind.AlreadyCanonical;
                    aR.Trace = "agent `" + iInput + "` → 帳號 `" + aByAgent + "`（⚠ legacy agent_banks 跳）";
                    return aR;
                }

                // ③ 大小寫／拼法變體
                if (s_CanonicalByLower.TryGetValue(aLower, out string? aSpelling))
                {
                    aR.AccountId = aSpelling;
                    aR.Kind = SCP_BankResolveKind.CaseNormalized;
                    aR.Trace = "大小寫歸一 `" + iInput + "` → `" + aSpelling + "`";
                    return aR;
                }

                // ④ alias → agent → 帳號（⚠ 同樣踩 legacy 表）
                if (s_AliasToAgent.TryGetValue(aLower, out string? aAgent)
                    && s_AgentToAccount.TryGetValue(aAgent.ToLowerInvariant(), out string? aViaAlias))
                {
                    aR.AccountId = aViaAlias;
                    aR.Kind = SCP_BankResolveKind.ViaAgent;
                    aR.Trace = "alias `" + iInput + "` → agent `" + aAgent + "` → 帳號 `" + aViaAlias + "`（⚠ legacy agent_banks 跳）";
                    return aR;
                }

                // ⑤ 查無 —— 原樣回傳並標記
                aR.Kind = SCP_BankResolveKind.Unresolved;
                aR.Trace = "查無對應（未歸一，將產生／沿用孤兒帳戶）";
                return aR;
            }
        }

        /// <summary>
        /// **persona → 帳戶** 的唯一入口。查不到回空字串 —— ⛔ 不 derive、不 mint。
        /// <para>⚠ 它就是 <see cref="Resolve"/> 加一層「查無 ⇒ 空字串」——
        /// ⛔ **刻意不只認 persona**：輸入本身是帳號名時照樣回它。
        /// 🩸 我第一版把它收窄成「只認 persona」，理由聽起來很好（兩個問題不該同形），
        /// 而對拍當場紅了 4 筆（`Altair` / `cc` / `zeta` / `tavern-keeper`）——
        /// 呼叫端餵進來的**不保證是 persona**。⇒ 收窄射程是另一張單的事，不是搬家能順手做的。</para>
        /// </summary>
        public static string ResolvePersonaAccount(string iLettersRoot, string iDataRoot, string iRegion,
                                                   string iPersona, out string oTrace)
        {
            oTrace = "";
            if (string.IsNullOrWhiteSpace(iPersona)) { oTrace = "persona 為空"; return ""; }
            SCP_BankResolution aR = Resolve(iLettersRoot, iDataRoot, iRegion, iPersona);
            oTrace = aR.Trace;
            return aR.IsUnresolved ? "" : (aR.AccountId ?? "");
        }

        /// <summary>該帳號是否已銷戶（以正式拼法比對；呼叫端請先 <see cref="Resolve"/>）。</summary>
        public static bool IsClosed(string iLettersRoot, string iDataRoot, string iRegion,
                                    string iAccountId, out string oReason)
        {
            oReason = "";
            if (string.IsNullOrEmpty(iAccountId)) return false;
            lock (s_Lock)
            {
                EnsureLoaded_NoLock(iLettersRoot, iDataRoot, iRegion);
                return s_Closed.TryGetValue(iAccountId, out oReason!);
            }
        }

        /// <summary>已銷戶名單快照（原拼法 → 理由）。</summary>
        public static Dictionary<string, string> GetClosedAccounts(string iLettersRoot, string iDataRoot, string iRegion)
        {
            lock (s_Lock)
            {
                EnsureLoaded_NoLock(iLettersRoot, iDataRoot, iRegion);
                return new Dictionary<string, string>(s_Closed, StringComparer.Ordinal);
            }
        }

        /// <summary>該名字是否為註冊在案的正式帳號（帳戶檔／system_accounts／agent_banks 值／合一綁定值）。</summary>
        public static bool IsCanonicalAccount(string iLettersRoot, string iDataRoot, string iRegion, string iAccountId)
        {
            if (string.IsNullOrEmpty(iAccountId)) return false;
            lock (s_Lock)
            {
                EnsureLoaded_NoLock(iLettersRoot, iDataRoot, iRegion);
                return s_Canonical.Contains(iAccountId);
            }
        }

        /// <summary>
        /// 綁在某帳號底下的 persona 清單（排序過）。
        /// <para>⭐ **由正向綁定檔導出** —— ⛔ 不讀 registry 的 `bank_personas`
        /// （那張表沒有寫入端，已於 2026-09-07 退出解析；它 2026-08-20 錯了 18 天沒有人喊）。</para>
        /// <para>⭐ 帳號**大小寫不敏感**（TASK-0270）⇒ 呼叫端可以直接餵帳本的 `account_id`，
        /// ⛔ **不要**先拿它去跑 <see cref="Resolve"/> 歸一：那是 persona → 帳號的表，
        /// 撞名時會把一個帳戶的錢算到另一個帳戶的人頭上。</para>
        /// </summary>
        public static List<string> GetBoundPersonas(string iLettersRoot, string iDataRoot, string iRegion, string iAccountId)
        {
            lock (s_Lock)
            {
                EnsureLoaded_NoLock(iLettersRoot, iDataRoot, iRegion);
                return s_AccountToPersonas.TryGetValue(iAccountId ?? "", out List<string>? aList)
                    ? new List<string>(aList) : new List<string>();
            }
        }

        // ==========================================================
        // 區塊職責：把一個帳號寫進 `closed_accounts` —— 該欄位的**唯一寫入端**。
        // 物理意義：銷戶＝宣告「這個名字不再接受任何金流」。
        //          `iRenamedTo` 非空＝併入另一個帳號，那句話是給未來查帳的人看的：錢去哪了。
        // 數值影響：**不動 ledger、不搬任何一分錢。** 搬錢是 transfer，另一個入口。
        //          ⚠ 本函式不檢查餘額 ——「餘額 0 才能銷戶」是流程的判準不是這一層的。
        // ==========================================================
        public static bool CloseAccount(string iLettersRoot, string iDataRoot, string iRegion,
                                        string iAccountId, string iReason, string iRenamedTo,
                                        string iActor, out string oError)
        {
            oError = "";
            if (string.IsNullOrWhiteSpace(iAccountId)) { oError = "accountId 必填"; return false; }
            if (string.IsNullOrWhiteSpace(iActor)) { oError = "actor 必填 —— 匿名寫入不收"; return false; }

            string aRegistry = RegistryPath(iDataRoot);
            try
            {
                if (!File.Exists(aRegistry)) { oError = "registry 不存在：" + aRegistry; return false; }
                SCP_JsonData aReg = SCP_JsonParser.Parse(File.ReadAllText(aRegistry));
                if (!aReg.Contains(ClosedAccountsKey)) aReg[ClosedAccountsKey] = SCP_JsonData.NewObject();

                string aNote = DateTime.UtcNow.ToString("yyyy-MM-dd") + " " + iReason + "（by " + iActor + "）";
                if (!string.IsNullOrWhiteSpace(iRenamedTo)) aNote += " renamed_to=" + iRenamedTo;
                aReg[ClosedAccountsKey][iAccountId] = aNote;

                // ⚠ **不可以寫 BOM** —— 本檔的讀取端包含 python（`json.load` 撞 BOM 是直接拋例外）。
                // 🩸 2026-08-20：某次寫入帶了 BOM，所有走 registry 的 CLI 全掛 ——
                //    BOM 是寫入端加的、症狀出在讀取端，中間沒有任何一格會叫。
                string aTmp = aRegistry + ".tmp";
                File.WriteAllText(aTmp, aReg.ToJson(true), new UTF8Encoding(false));
                if (File.Exists(aRegistry)) File.Delete(aRegistry);
                File.Move(aTmp, aRegistry);

                Invalidate();
                // 讀回複驗 —— **寫入成功不等於解析器看得到它**。
                if (!IsClosed(iLettersRoot, iDataRoot, iRegion, iAccountId, out string _))
                { oError = "寫入後讀回不符：該帳號仍未被判定為已銷戶"; return false; }
                return true;
            }
            catch (Exception e) { oError = e.Message; return false; }
        }
    }
}
