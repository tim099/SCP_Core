// 區塊職責：persona → 信箱的**三段解析**（共用層唯一實作點）—— Unity 與 senate.exe 走同一份。
// 物理意義：這個位址會被寫進 `Co-Authored-By:`，也就是寫進**改不掉的 git history**。
//           所以任何一條失敗路徑都不猜：寧可回哨兵讓呼叫端擋下，也不要讓一個假位址落地。
// 數值影響：純唯讀。不寫任何檔、不動 lock、不碰帳。
//
// 三段（順序即優先序，與 python `agent_email.resolve_email` 逐段對齊）：
//   ① persona-override ── `letters/<p>/profile/email.md`（那個人自己的位址）
//   ② agent-default    ── `<data_root>/AwakenInit/agent_emails.json` 的 `defaults[actual_agent]`
//   ③ fallback         ── 同一個檔的 `fallback`
//   ⇒ 三段都空 ⇒ 哨兵 `unset@invalid`（**不是空字串** —— 空字串會被下游當成「有值但短」）
//
// 🩸 ⚠ 已知缺陷，本層**治不好**，別以為搬過來就沒事了（TASK-0187，2026-09-10 量的）：
//   `agent_emails.json` 是**專案級**的，而 `UCL_Core` 掛在多棵樹底下 ⇒ 三棵樹三份，內容**互相矛盾**：
//       LY  ： Codex→tim19941125@gmail.com   ／ ClaudeCode→basecamp05122026@gmail.com
//       Bar ： Codex→basecamp05122026@gmail.com ／ ClaudeCode→tim19941125@gmail.com   ← 對調
//   可證後果：`Sirius`（actual_agent=ClaudeCode、無自己的 email ⇒ 走第②段）
//   在 LY 解析成 `basecamp05122026@`，而 `UCL_Core` 的 history 裡寫的是 `tim19941125@`（＝Bar 那張表）。
//   ⇒ **同一個人、同一個 submodule，信箱取決於提交的人站在哪棵樹。**
//   ⛔ 這是**資料**的病不是**解析器**的病 —— 把解析器搬到共用層不會治好它，只是讓兩個宿主一起錯得一致。
//   出口在單子上（取消第②段／收成單一來源／把 defaults 也寫死），⚠ 那要拍板，本檔不自己選。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。JSON 一律走 SCP_Json。
using System;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Letters
{
    /// <summary>信箱解析結果。<see cref="Source"/> 與 <see cref="DataSource"/> 是**兩把尺**，見各自註解。</summary>
    public sealed class SCP_AgentEmailInfo
    {
        /// <summary>解析出來的位址；三段都空時是 <see cref="SCP_AgentEmail.UnsetSentinel"/>。</summary>
        public string Email = "";

        /// <summary>解析**規則**的出身：persona-override / agent-default / fallback / unset。</summary>
        public string Source = "";

        /// <summary>profile 寫的 actual_agent（第②段的查表鍵）。</summary>
        public string ActualAgent = "";

        // ⚠ 這一欄與 Source 不可互推：
        //   Source 只說「這人有沒有自己的信箱」，**完全不說那個值是不是現在的值**。
        //   本層直接讀 `profile/` 的檔案，所以它永遠是現場值 ⇒ 固定 "profile-direct"。
        // 📌 python 那側有 live／snapshot／local-parse 三種，因為它會退到快照；
        //   **本層沒有快照那一段**，所以刻意不沿用那組字彙 —— 借一個看起來一樣的詞，
        //   會讓讀的人以為兩邊在講同一件事。
        /// <summary>這份 persona 資料是哪一段給的。本層固定 <c>profile-direct</c>（沒有快照層）。</summary>
        public string DataSource = "profile-direct";
    }

    public static class SCP_AgentEmail
    {
        // 物理意義：一個**故意無效**的位址 —— 它進不了任何信箱，所以就算漏擋也不會寄到別人那裡。
        // ⛔ 不要改成空字串：空字串會被下游的「有沒有值」判斷當成「沒填」，
        //   而「沒填」與「解析失敗」的處置不同（前者可能是還沒設，後者是設定壞了）。
        /// <summary>三段都解析不到時的哨兵位址。</summary>
        public const string UnsetSentinel = "unset@invalid";

        /// <summary>`<data_root>/AwakenInit/agent_emails.json` —— 第②③段的資料源。</summary>
        public static string RegistryPath(string iDataRoot)
        {
            return Path.Combine(iDataRoot ?? "", "AwakenInit", "agent_emails.json").Replace('\\', '/');
        }

        // 區塊職責：粗篩位址形狀。
        // 物理意義：只擋「明顯不是位址」的東西（缺 @／多個 @／網域沒有點／含空白）。
        // 數值影響：⛔ **不做 RFC 級驗證** —— 過嚴會擋掉合法的怪位址，而那會逼人繞過工具，
        //          繞過工具的代價比放進一個怪位址大。
        public static bool LooksLikeEmail(string iValue)
        {
            string aV = (iValue ?? "").Trim();
            if (aV.Length == 0 || aV.IndexOf(' ') >= 0) return false;
            int aAt = aV.IndexOf('@');
            if (aAt < 0 || aV.IndexOf('@', aAt + 1) >= 0) return false;
            string aLocal = aV.Substring(0, aAt);
            string aDomain = aV.Substring(aAt + 1);
            if (aLocal.Length == 0) return false;
            return aDomain.IndexOf('.') >= 0 && !aDomain.StartsWith(".") && !aDomain.EndsWith(".");
        }

        // 區塊職責：三段解析。
        // 數值影響：純唯讀。讀不到 persona／讀不到表都**不丟例外** —— 走到後面的段，最差落到哨兵。
        //          fail-soft 的理由：解析失敗要讓呼叫端**看得見並自己決定擋不擋**，
        //          在這一層丟例外會讓「沒設定」跟「工具壞了」同形。
        /// <param name="iCurrencyId">本專案的央行區域 ID。**由宿主傳進來，本層不推導。**</param>
        /// <param name="iDataRoot">AgentCommands 資料根 —— 第②③段要用。給空字串＝只走第①段。</param>
        public static SCP_AgentEmailInfo Resolve(string iLettersRoot, string iPersona, string iCurrencyId,
                                                 string iDataRoot, Action<string>? iWarn = null)
        {
            SCP_AgentEmailInfo aInfo = new SCP_AgentEmailInfo();
            string aOwn = "";
            try
            {
                SCP_JsonData? aRaw = SCP_PersonaProfile.GetRaw(iLettersRoot, iPersona, iCurrencyId, iWarn);
                if (aRaw != null)
                {
                    aOwn = (aRaw.GetString("email", "") ?? "").Trim();
                    aInfo.ActualAgent = (aRaw.GetString("actual_agent", "") ?? "").Trim();
                }
            }
            catch (Exception e)
            {
                // 出聲之後往下走 —— 讀不到 persona 不代表沒有 agent 預設可用。
                if (iWarn != null) iWarn("[AgentEmail] persona '" + iPersona + "' 讀取失敗：" + e.Message);
            }

            // ① persona-override
            if (aOwn.Length > 0)
            {
                aInfo.Email = aOwn;
                aInfo.Source = "persona-override";
                return aInfo;
            }

            SCP_JsonData? aReg = LoadRegistry(iDataRoot, iWarn);

            // ② agent-default
            if (aReg != null && aInfo.ActualAgent.Length > 0 && aReg.Contains("defaults"))
            {
                string aByAgent = (aReg["defaults"].GetString(aInfo.ActualAgent, "") ?? "").Trim();
                if (aByAgent.Length > 0)
                {
                    aInfo.Email = aByAgent;
                    aInfo.Source = "agent-default";
                    return aInfo;
                }
            }

            // ③ fallback
            string aFallback = aReg == null ? "" : (aReg.GetString("fallback", "") ?? "").Trim();
            if (aFallback.Length > 0)
            {
                aInfo.Email = aFallback;
                aInfo.Source = "fallback";
                return aInfo;
            }

            aInfo.Email = UnsetSentinel;
            aInfo.Source = "unset";
            return aInfo;
        }

        /// <summary>讀 `agent_emails.json`。檔案缺／壞掉都回 <c>null</c>（fail-soft，解析仍走得完）。</summary>
        static SCP_JsonData? LoadRegistry(string iDataRoot, Action<string>? iWarn)
        {
            if (string.IsNullOrEmpty(iDataRoot)) return null;
            string aPath = RegistryPath(iDataRoot);
            try
            {
                if (!File.Exists(aPath)) return null;
                return SCP_JsonData.Parse(File.ReadAllText(aPath));
            }
            catch (Exception e)
            {
                // ⚠ 出聲 —— 表讀不到會讓沒有自己信箱的人靜默落到哨兵，而那長得像「他還沒設」。
                if (iWarn != null) iWarn("[AgentEmail] 讀 " + aPath + " 失敗（視為空表）：" + e.Message);
                return null;
            }
        }

        // 區塊職責：組 `Co-Authored-By:` 那一行。
        // 物理意義：身分／型號／信箱三欄**全部推導自檔案，一個字都不手打** ——
        //          trailer 以前是手打的，於是它會漂：同一位同事出現過 (GPT)/(GPT-5)/(GPT-5.6)
        //          與兩種 domain。手不碰就不會漂。
        // 數值影響：純組字串。⛔ 本函式**不擋** —— 哨兵位址照樣組得出來，
        //          擋不擋是呼叫端的政策（那是要拍板的事，不是這一層順手決定的）。
        /// <param name="iDataRoot">AgentCommands 資料根 —— 信箱第②③段要用。</param>
        public static string BuildTrailer(string iLettersRoot, string iPersona, string iCurrencyId,
                                          string iDataRoot, Action<string>? iWarn = null)
        {
            string aAgent = "";
            try
            {
                SCP_JsonData? aRaw = SCP_PersonaProfile.GetRaw(iLettersRoot, iPersona, iCurrencyId, iWarn);
                if (aRaw != null) aAgent = (aRaw.GetString("agent", "") ?? "").Trim();
            }
            catch (Exception e)
            {
                if (iWarn != null) iWarn("[AgentEmail] 讀 agent 欄失敗：" + e.Message);
            }
            string aModel = SCP_AgentModelRegistry.FormatTrailerModel(iLettersRoot, iPersona, iCurrencyId, iWarn);
            SCP_AgentEmailInfo aInfo = Resolve(iLettersRoot, iPersona, iCurrencyId, iDataRoot, iWarn);
            return "Co-Authored-By: " + (aAgent.Length > 0 ? aAgent : "?")
                   + "@" + iPersona
                   + "(" + (aModel.Length > 0 ? aModel : "?") + ")"
                   + " <" + aInfo.Email + ">";
        }
    }
}
