// 區塊職責：trailer 的型號欄 `(vendor / version)` 組法 —— **共用層唯一實作點**，Unity 與 senate.exe 走同一份。
// 物理意義：`vendor` 是「這個工具是誰家的」（ClaudeCode → Claude），`version` 是「當時跑的是哪一版」。
//           前者是工具身分的性質，後者是那一天的事實 —— 兩者一起寫進 git trailer，而 history 改不掉。
// 數值影響：純唯讀、不讀任何設定檔。輸出只餵 trailer 字串，不影響帳、不影響 lock。
//
// 🩸 為什麼兩張表寫死在 code 裡（TASK-0187，Tim 2026-09-10 拍板）：
//   舊實作把 key 寫死（`UCL_ActualAgent` 列舉）而 value 放 `AwakenInit/agent_models.json`。
//   ⇒ 那個切法本身就是漂移的來源：**`UCL_Core` 是掛在多棵樹底下的 submodule，而那個檔是專案級的。**
//   實測（2026-09-10，同一個 submodule 的 history，同一位同事、同一天）：
//       Zeta@summit(Claude / claude-opus-5)   ← 從有那個檔的樹提交
//       zeta@summit(claude-opus-5)            ← 從 LY 提交（LY 底下**沒有**那個檔 ⇒ vendors 全空 ⇒ 沿用原值）
//   兩種形狀在 09-07／08／09／10 每一天都同時出現。⇒ **一個實作點不夠，還要一個輸入來源。**
// ⛔ 所以刻意**不做**「寫死當預設 ＋ 檔案可覆寫」：那會把上面那個分裂原封不動留著，
//   而它的失效樣子是「兩棵樹各自都很正常」—— 沒有任何一層會喊。
//
// ⚠ 支撐這個決定的三個讀數（2026-09-10 量的，日後要翻案請先重量）：
//   ① `agent_models.json` 自 2026-08-03 建立後**改過 0 次**，兩棵樹的副本**逐字相同**
//   ② 實際在用的 `actual_agent` 只有下面這三個 —— LY 與 Bar 都沒有第四種
//   ③ `models` 那張表的用途（agent 名被誤填進 model 欄 ⇒ 翻譯）在 40 個 persona 裡**命中 0**
// ⚠ 代價（顯式承認，不是忽略）：`models` 的值是**會過期的版本字串**，改它要動 code ＋ 兩個宿主重編。
//   用「38 天零編輯」換「不會分裂」，那是拍板時算過的帳，不是沒想到。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
using System;
using System.Collections.Generic;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Letters
{
    /// <summary>`model` 欄的解析結果。<see cref="Source"/> 說明這個值是怎麼來的。</summary>
    public sealed class SCP_AgentModelResolution
    {
        /// <summary>解析後的型號。原值為空時是 <c>"?"</c>（⛔ 不是空字串 —— 空與「沒填」要分得出來）。</summary>
        public string Model = "";

        /// <summary>profile 裡原本寫的字，未經翻譯。</summary>
        public string Raw = "";

        /// <summary>as-written / agent-translated / agent-unmapped / empty。</summary>
        public string Source = "";

        /// <summary>辨識出來的正規 actual_agent（辨識不出時是 profile 裡的原值）。</summary>
        public string AgentKey = "";

        // ⚠ 這一欄與 AgentKey 是**兩把尺**，不可互相代用：
        //   AgentKey 會在「model 欄被填成 agent 名」時被 IdentifyAgent 覆寫成那個名字；
        //   本欄永遠是 profile 寫的 actual_agent。
        // 🩸 vendor 一定要查**本欄** —— 有人 model 填 `Codex` 而 actual_agent 是 `ClaudeCode` 時，
        //   查 AgentKey 會得到 GPT、查本欄得到 Claude，而**後者才是那台機器真正的廠牌**。
        //   （2026-09-10 移植時差點用錯這一格 —— 兩欄在多數 persona 上剛好相同，所以測不出來。）
        /// <summary>profile 寫的 actual_agent 原值，**不受 model 欄翻譯影響**。</summary>
        public string ProfileActualAgent = "";

        public bool WasTranslated { get { return Source == "agent-translated"; } }
    }

    public static class SCP_AgentModelRegistry
    {
        // 區塊職責：actual_agent → 廠牌名。
        // 物理意義：vendor 是**可驗的必填身分** —— 由 actual_agent 推導，不靠人填。
        // 數值影響：查不到 ⇒ 整段沿用原值（見 FormatTrailerModel），⛔ 不印假精確的 `?`。
        static readonly Dictionary<string, string> s_Vendors =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Codex", "GPT" },
                { "ClaudeCode", "Claude" },
                { "Antigravity", "Gemini" },
            };

        // 區塊職責：actual_agent → 預設型號（只在「model 欄被填成 agent 名」時才用得到）。
        // 物理意義：那是一條**修資料輸入錯誤**的路，不是主要來源 —— 主要來源永遠是 profile 的 model 欄。
        // 數值影響：只影響 Source == "agent-translated" 那條分支；今天全庫命中 0。
        static readonly Dictionary<string, string> s_Models =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Codex", "GPT 6" },
                { "ClaudeCode", "Claude mythos 5" },
                { "Antigravity", "Gemini 4" },
            };

        // 區塊職責：已知會被填進 model 欄的 agent 別名 → 正規 actual_agent。
        // 物理意義：實測發現**提示反而讓人填錯** —— apex-one 的 system prompt 第一句是 "You are Antigravity"
        //          所以他把 Antigravity 填進 model；kaguya 填 Codex。兩人都是誠實作答，
        //          錯的是我們要求他們回答一個他們讀起來意思不同的問題（Tim 2026-08-03 拍板：改在底層翻譯）。
        // 數值影響：收的是**人真的會寫出來的字**，不是理論上的正確值；漏一個就翻不出來，多一個沒有代價。
        //          value 為空字串 ＝ 有歧義（Claude / Gemini 也可能是誠實給的模糊型號）⇒ 一律當型號不翻。
        static readonly Dictionary<string, string> s_Aliases =
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                { "codex", "Codex" },
                { "openai", "Codex" },
                { "chatgpt", "Codex" },
                { "claudecode", "ClaudeCode" },
                { "anthropic", "ClaudeCode" },
                { "antigravity", "Antigravity" },
                { "claude", "" },
                { "gemini", "" },
            };

        /// <summary>正規 actual_agent 清單（唯讀）—— 後台頁顯示用，⛔ 不是給人改的。</summary>
        public static IReadOnlyDictionary<string, string> Vendors { get { return s_Vendors; } }

        /// <summary>正規 actual_agent → 預設型號（唯讀）—— 後台頁顯示用，⛔ 不是給人改的。</summary>
        public static IReadOnlyDictionary<string, string> Models { get { return s_Models; } }

        /// <summary>辨識用正規化 —— 無視大小寫、空白、連字號、底線。</summary>
        public static string Normalize(string iValue)
        {
            if (string.IsNullOrEmpty(iValue)) return "";
            StringBuilder aSb = new StringBuilder(iValue.Length);
            foreach (char aC in iValue)
                if (char.IsLetterOrDigit(aC)) aSb.Append(char.ToLowerInvariant(aC));
            return aSb.ToString();
        }

        /// <summary>這個字串是不是 agent 名？是的話回正規 actual_agent；不是（或有歧義）回空字串。</summary>
        public static string IdentifyAgent(string iValue)
        {
            string aN = Normalize(iValue);
            if (aN.Length == 0) return "";
            foreach (string aKey in s_Vendors.Keys)
                if (aN == Normalize(aKey)) return aKey;
            return s_Aliases.TryGetValue(aN, out string aMapped) ? aMapped : "";
        }

        // 區塊職責：persona.model → 是 agent 名就翻成該 agent 的預設型號；翻不出來**保留原值**。
        // 物理意義：原值至少是某人真的寫下的資訊，空白什麼都不是 —— 所以任何一條失敗路徑都不擦掉它。
        // 數值影響：純唯讀。讀不到 persona ⇒ Source="empty"、Model="?"（⛔ 與「填了空字串」同一格，
        //          因為 trailer 那一側對兩者的處置相同：都印 `?`）。
        /// <param name="iCurrencyId">本專案的央行區域 ID。**由宿主傳進來，本層不推導。**</param>
        public static SCP_AgentModelResolution Resolve(string iLettersRoot, string iPersona,
                                                       string iCurrencyId, Action<string>? iWarn = null)
        {
            SCP_AgentModelResolution aResult = new SCP_AgentModelResolution();
            string aRaw = "";
            string aActualAgent = "";
            try
            {
                SCP_JsonData? aData = SCP_PersonaProfile.GetRaw(iLettersRoot, iPersona, iCurrencyId, iWarn);
                if (aData != null)
                {
                    aRaw = (aData.GetString("model", "") ?? "").Trim();
                    aActualAgent = (aData.GetString("actual_agent", "") ?? "").Trim();
                }
            }
            catch (Exception e)
            {
                // ⛔ 讀不到**不猜** —— 出聲之後走 empty 那條路，讓 trailer 印 `?`。
                //   靜默回空會讓「這人沒填型號」與「我讀不到這個人」同形。
                if (iWarn != null) iWarn("[AgentModel] 讀 persona " + iPersona + " 失敗：" + e.Message);
            }

            aResult.Raw = aRaw;
            aResult.AgentKey = aActualAgent;
            aResult.ProfileActualAgent = aActualAgent;
            if (aRaw.Length == 0) { aResult.Model = "?"; aResult.Source = "empty"; return aResult; }

            string aKey = IdentifyAgent(aRaw);
            if (aKey.Length == 0) { aResult.Model = aRaw; aResult.Source = "as-written"; return aResult; }

            aResult.AgentKey = aKey;
            if (s_Models.TryGetValue(aKey, out string aMapped) && aMapped.Trim().Length > 0)
            {
                aResult.Model = aMapped.Trim();
                aResult.Source = "agent-translated";
                return aResult;
            }
            // 認得出是 agent 名但表裡沒有 → 別把資訊擦掉。
            aResult.Model = aRaw;
            aResult.Source = "agent-unmapped";
            return aResult;
        }

        // 區塊職責：組 trailer 的型號欄字串。
        // 物理意義：這個字串會進 `Co-Authored-By:` 寫死在 git history 裡 —— **改不掉**，所以寧可難看也不猜。
        // 數值影響：純唯讀。三條規則（2026-08-03 三票拍板）：
        //   ① vendor 推不出來 ⇒ **整段沿用原值**，不印假精確的 `?`
        //   ② version 與 vendor 相同 ⇒ 只印 vendor（那代表這人只知道廠牌，沒有版本）
        //   ③ **不剝 version 開頭的 vendor 前綴**：`GPT-5.6 Luna` 照印成 `GPT / GPT-5.6 Luna`
        //      —— 冗餘只是難看，剝字串是猜測；兩位同事當時都選了難看那個。
        /// <param name="iCurrencyId">本專案的央行區域 ID。**由宿主傳進來，本層不推導。**</param>
        public static string FormatTrailerModel(string iLettersRoot, string iPersona,
                                                string iCurrencyId, Action<string>? iWarn = null)
        {
            SCP_AgentModelResolution aResolved = Resolve(iLettersRoot, iPersona, iCurrencyId, iWarn);
            string aRaw = aResolved.Model ?? "";
            // ⚠ 查 vendor 一律用 ProfileActualAgent，**不是 AgentKey** —— 理由見那兩欄的註解。
            string aActualAgent = aResolved.ProfileActualAgent ?? "";
            if (aActualAgent.Length == 0) return aRaw;
            if (!s_Vendors.TryGetValue(aActualAgent, out string aVendor) || aVendor.Trim().Length == 0)
                return aRaw;
            aVendor = aVendor.Trim();
            if (aRaw.Length == 0 || aRaw == "?" || Normalize(aRaw) == Normalize(aVendor)) return aVendor;
            return aVendor + " / " + aRaw;
        }
    }
}
