// 區塊職責：**建一位新 persona 的規劃與組裝**（TASK-0428）—— 名稱驗證、綁定的 agent（或新開一個）、
//           identity_vector／fork 血統、角色設定檔、頭像規格。寫入身分欄仍交給 `SCP_PersonaProfileWrite.Create`（唯一寫入端）。
// 物理意義：Unity 身分後台的「建 persona」在頁面裡自己組整份身分欄 JSON；Senate 這側沒有人會組 ⇒ CLI 建不出人。
//           本檔把那一段搬成共用層：CLI（`persona-create`）與之後 Senate 的 persona 管理頁（TASK-0424）呼叫同一份。
// 數值影響：`Plan` 純讀（draft 就是它）；`AddAgentBank` 寫 `AwakenInit/_registry_meta.json` 的 `agent_banks` 一格。
//
// ⚠ 欄位規則鏡像 Editor 版（`UCL_PersonaAgentAdminPage.DoCreatePersona`，再往上是 awakening.py）：
//   identity_vector 64 維、每維 round(±1, 4)；vector_history 首筆 {at, hash, delta_mag:0, trigger[, source]}；
//   hash ＝ sha256("x.xxxx,…") 前 8 hex；fork ⇒ 抄來源 vector 與 lineage、lineage 接上來源、鏈深 > 5 只警告。
//   ⛔ 不寫推導欄（wake_count／status／last_active）—— 寫了也會被 Create 跳過，而且它們的真相源在別處。
// ⚠ Tim 2026-10-06 拍板：必填只有「參考角色」（＝ persona id）；其餘設定由該 persona 自己填（沒填的格子標「待本人填寫」）；
//   email 不在這一步；憲法不在這一步（每一條要附「我違反它的一次」，新人沒有）；建 agent 預設 1000 token。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    /// <summary>使用者（經 agent 問答）給的輸入。只有 <see cref="Persona"/> 必填。</summary>
    public sealed class SCP_PersonaCreateSpec
    {
        /// <summary>參考角色＝persona id（資料夾名）。</summary>
        public string Persona = "";
        /// <summary>綁定的 agent（`agent_banks` 的 key）。空 ⇒ 由 actual_agent 推一個建議值。</summary>
        public string Agent = "";
        /// <summary>非空 ⇒ **同時新開 agent**：`agent_banks[Agent] = NewAgentAccount`，並開戶。</summary>
        public string NewAgentAccount = "";
        public string ForkFrom = "";
        public string LayerRole = "";
        /// <summary>參考來源（作品名／角色全名）。空 ⇒ 只有名字（或原創）。</summary>
        public string Reference = "";
        /// <summary>參照作品角色時取多少（使用者決定；Tim 2026-10-06）。</summary>
        public string ReferenceScope = "";
        /// <summary>角色設定各格（鍵＝<see cref="SCP_PersonaCreate.Sections"/> 的標題）。</summary>
        public Dictionary<string, string> Sections = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>主題色（#RRGGBB）—— 頭像服裝主色也用它。</summary>
        public string Color = "";
    }

    /// <summary><see cref="SCP_PersonaCreate.Plan"/> 的結果 —— draft 印它、create 照它寫。</summary>
    public sealed class SCP_PersonaCreatePlan
    {
        public string Persona = "";
        public string Region = "";
        public string Agent = "";
        public string Account = "";
        /// <summary>agent 從哪來：使用者指定／由 actual_agent 推的建議／新開。</summary>
        public string AgentSource = "";
        public bool CreatesAgent;
        public string ForkFrom = "";
        public List<string> Lineage = new List<string>();
        public List<double> Vector = new List<double>();
        public string VectorHash = "";
        public string CreatedAt = "";
        public string Character = "";
        public string Color = "";
        /// <summary>沒填、留給本人補的格子（名稱）。</summary>
        public List<string> Pending = new List<string>();
        public List<string> Warnings = new List<string>();
        public SCP_UclLegacyObject Fields = new SCP_UclLegacyObject();
    }

    public static class SCP_PersonaCreate
    {
        public const int VectorDim = 64;
        public const int ForkChainCap = 5;
        public const string PendingMark = "（待本人填寫）";
        /// <summary>新開 agent 的預設種子額度（Tim 2026-10-06：「創建 Agent 預設有 1000 token」）。</summary>
        public const int DefaultAgentSeed = 1000;

        /// <summary>角色設定的格子（順序即呈現順序）。</summary>
        public static readonly string[] Sections = { "一人稱", "語氣／口癖", "性格", "價值觀／底線", "與其他 persona 的關係" };

        /// <summary>actual_agent（正規化後）→ agent_banks 的 key 建議值。⚠ 只是**建議**：帳號跟 actual_agent 不是一對一（summit 跑在 ClaudeCode 上，帳號是 zeta）。</summary>
        static readonly Dictionary<string, string> s_ActualToAgent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "ClaudeCode", "claude-code" }, { "Antigravity", "antigravity" }, { "Codex", "Codex" },
        };

        public static string RegistryMetaPath(string iDataRoot) => Path.Combine(iDataRoot, "AwakenInit", "_registry_meta.json");

        /// <summary>`agent_banks`（agent → 帳號）。讀不到回空表。</summary>
        public static List<KeyValuePair<string, string>> AgentBanks(string iDataRoot)
        {
            var aOut = new List<KeyValuePair<string, string>>();
            SCP_JsonData aMeta = SCP_Morning.LoadRegistryMeta(iDataRoot);
            if (!aMeta["agent_banks"].IsObject) return aOut;
            foreach (string k in aMeta["agent_banks"].Keys)
                if (!k.StartsWith("_", StringComparison.Ordinal))
                    aOut.Add(new KeyValuePair<string, string>(k, aMeta["agent_banks"].GetString(k, "")));
            return aOut;
        }

        /// <summary>persona 名的合法性（與 `SCP_PersonaProfileWrite.Create` 同一條規則，draft 先擋）。</summary>
        public static bool IsValidName(string iName, out string oError)
        {
            oError = "";
            string p = (iName ?? "").Trim();
            if (p.Length == 0 || p.StartsWith("_", StringComparison.Ordinal) || p.StartsWith(".", StringComparison.Ordinal)
                || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.Contains("..") || p.Contains(" "))
            {
                oError = $"persona 名不合法（不能空白、不能有空格、不能 `_`／`.` 開頭、要能當資料夾名）：'{iName}'";
                return false;
            }
            return true;
        }

        /// <summary>
        /// 規劃一次建立（**純讀**）。失敗回 null ＋ <paramref name="oError"/>。
        /// </summary>
        /// <param name="iActualAgent">呼叫端的 actual_agent（用來推 agent 建議值；可空）。</param>
        public static SCP_PersonaCreatePlan? Plan(string iLettersRoot, string iDataRoot, string iRegion,
                                                 SCP_PersonaCreateSpec iSpec, string iActualAgent,
                                                 Random iRng, string iNowIso, out string oError)
        {
            oError = "";
            string p = (iSpec.Persona ?? "").Trim();
            if (!IsValidName(p, out oError)) return null;
            if (SCP_PersonaProfile.Exists(iLettersRoot, p)) { oError = $"`{p}` 已經存在 —— 建人不覆寫既有的人"; return null; }

            var aPlan = new SCP_PersonaCreatePlan { Persona = p, Region = iRegion, CreatedAt = iNowIso };

            // ── 綁定的 agent ──
            List<KeyValuePair<string, string>> aBanks = AgentBanks(iDataRoot);
            string Choices() => string.Join("、", aBanks.ConvertAll(kv => kv.Key + "→" + kv.Value));
            string aNewAcc = (iSpec.NewAgentAccount ?? "").Trim();
            string aAgentArg = (iSpec.Agent ?? "").Trim();
            if (aNewAcc.Length > 0)
            {
                if (aAgentArg.Length == 0) { oError = "新開 agent 要給 agent 名（`agent`）"; return null; }
                foreach (var kv in aBanks)
                {
                    if (string.Equals(kv.Key, aAgentArg, StringComparison.OrdinalIgnoreCase))
                    { oError = $"agent `{kv.Key}` 已經存在（帳號 {kv.Value}）—— 新開不覆蓋；要綁它就拿掉 new_agent_account"; return null; }
                    if (string.Equals(kv.Value, aNewAcc, StringComparison.OrdinalIgnoreCase))
                    { oError = $"帳號 `{aNewAcc}` 已經是 agent `{kv.Key}` 的帳號 —— 一個帳號不開第二次"; return null; }
                }
                if (aNewAcc.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || aAgentArg.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                { oError = "agent 名／帳號 id 含不能當檔名的字元"; return null; }
                aPlan.Agent = aAgentArg; aPlan.Account = aNewAcc; aPlan.CreatesAgent = true;
                aPlan.AgentSource = "新開（同時開戶）";
            }
            else
            {
                string aAgent = aAgentArg;
                aPlan.AgentSource = "使用者指定";
                if (aAgent.Length == 0)
                {
                    var (aActual, _) = SCP_Morning.NormalizeActualAgent(iActualAgent ?? "");
                    if (aActual.Length > 0 && s_ActualToAgent.TryGetValue(aActual, out string? aSuggest)) aAgent = aSuggest;
                    aPlan.AgentSource = $"建議值（由 actual_agent `{aActual}` 推；⚠ 請使用者確認）";
                }
                if (aAgent.Length == 0) { oError = "agent 必填（推不出建議值）—— 可選：" + Choices(); return null; }
                string aCanon = SCP_Morning.NormalizeAgent(SCP_Morning.LoadRegistryMeta(iDataRoot), aAgent);
                int i = aBanks.FindIndex(kv => kv.Key == aCanon);
                if (i < 0) { oError = $"agent `{aAgent}` 不在 agent_banks —— 可選：{Choices()}；或新開（給 new_agent_account）"; return null; }
                aPlan.Agent = aBanks[i].Key; aPlan.Account = aBanks[i].Value;
                if (aPlan.Account.Length == 0) { oError = $"agent `{aPlan.Agent}` 沒有對應的帳號（agent_banks 那一格是空的）"; return null; }
            }

            // ── vector 與血統 ──
            string aFork = (iSpec.ForkFrom ?? "").Trim();
            string aTrigger;
            if (aFork.Length == 0)
            {
                aPlan.Vector = GenVector(iRng);
                aTrigger = "new_via_senate_cli";
            }
            else
            {
                if (!SCP_PersonaProfile.Exists(iLettersRoot, aFork)) { oError = $"fork 來源 `{aFork}` 不存在"; return null; }
                SCP_JsonData? aSrc = SCP_PersonaProfile.GetRaw(iLettersRoot, aFork, iRegion, null);
                if (aSrc == null) { oError = $"fork 來源 `{aFork}` 讀取失敗（壞檔？）"; return null; }
                if (aSrc["identity_vector"].IsArray)
                    foreach (SCP_JsonData x in aSrc["identity_vector"]) aPlan.Vector.Add(x.AsDouble());
                if (aPlan.Vector.Count == 0) { aPlan.Vector = GenVector(iRng); aPlan.Warnings.Add($"fork 來源 `{aFork}` 沒有 identity_vector ⇒ 改用隨機"); }
                if (aSrc["fork_lineage"].IsArray)
                    foreach (SCP_JsonData x in aSrc["fork_lineage"]) aPlan.Lineage.Add(x.AsString());
                aPlan.Lineage.Add(aFork);
                aPlan.ForkFrom = aFork;
                aTrigger = "fork";
                if (aPlan.Lineage.Count > ForkChainCap)
                    aPlan.Warnings.Add($"血統鏈深 {aPlan.Lineage.Count} > {ForkChainCap}，建議改開獨立人格");
            }
            aPlan.VectorHash = HashVector(aPlan.Vector);

            // ── 主題色 ──
            string aColor = (iSpec.Color ?? "").Trim();
            if (aColor.Length > 0 && !SCP_PersonaDisplay.IsValidColor(aColor)) { oError = $"顏色不合法（要 #RRGGBB）：'{aColor}'"; return null; }
            aPlan.Color = aColor;
            if (aColor.Length == 0) aPlan.Pending.Add("主題色");

            // ── 角色設定與 layer_role ──
            string aRole = (iSpec.LayerRole ?? "").Trim();
            if (aRole.Length == 0) { aRole = PendingMark; aPlan.Pending.Add("layer_role"); }
            aPlan.Character = BuildCharacter(aPlan, iSpec);

            // ── 身分欄 ──
            var f = aPlan.Fields;
            f.Add(Kv("layer_role", aRole));
            f.Add(Kv("forked_from", aFork.Length == 0 ? null : aFork));
            f.Add(Kv("fork_lineage", new List<object?>(aPlan.Lineage)));
            f.Add(Kv("forked_at", aFork.Length == 0 ? null : iNowIso));
            f.Add(Kv("created_at", iNowIso));
            f.Add(Kv("identity_vector", aPlan.Vector.ConvertAll(v => (object?)v)));
            var h0 = new SCP_UclLegacyObject
            {
                Kv("at", iNowIso), Kv("hash", aPlan.VectorHash), Kv("delta_mag", 0.0), Kv("trigger", aTrigger),
            };
            if (aFork.Length > 0) h0.Add(Kv("source", aFork));
            f.Add(Kv("vector_history", new List<object?> { h0 }));
            f.Add(Kv("character", aPlan.Character));
            return aPlan;
        }

        static KeyValuePair<string, object?> Kv(string k, object? v) => new KeyValuePair<string, object?>(k, v);

        /// <summary>角色設定檔（`profile/character.md`）。沒填的格子寫 <see cref="PendingMark"/>，並記進 Pending。</summary>
        static string BuildCharacter(SCP_PersonaCreatePlan ioPlan, SCP_PersonaCreateSpec iSpec)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {ioPlan.Persona} 的角色設定");
            sb.AppendLine();
            sb.AppendLine($"> 建立於 {ioPlan.CreatedAt}。這是起點，不是定稿 —— 標「{PendingMark}」的格子由 {ioPlan.Persona} 自己補；");
            sb.AppendLine("> 補完寫回：`senate cmd persona-profile --arg op=set --arg persona=<自己> --arg field=character --arg-file value=<檔> --arg actor=<自己> --arg reason=<一句>`。");
            sb.AppendLine();
            string aRef = (iSpec.Reference ?? "").Trim();
            sb.AppendLine($"- 參考角色：{(aRef.Length > 0 ? aRef : ioPlan.Persona)}");
            string aScope = (iSpec.ReferenceScope ?? "").Trim();
            if (aScope.Length == 0) ioPlan.Pending.Add("參照範圍");
            sb.AppendLine($"- 參照範圍：{(aScope.Length > 0 ? aScope : PendingMark)}");
            sb.AppendLine($"- 主題色：{(ioPlan.Color.Length > 0 ? ioPlan.Color : PendingMark)}");
            if (ioPlan.ForkFrom.Length > 0) sb.AppendLine($"- fork 自：{ioPlan.ForkFrom}");
            foreach (string s in Sections)
            {
                sb.AppendLine();
                sb.AppendLine("## " + s);
                sb.AppendLine();
                string v = iSpec.Sections.TryGetValue(s, out string? x) ? (x ?? "").Trim() : "";
                if (v.Length == 0) { ioPlan.Pending.Add(s); v = PendingMark; }
                sb.AppendLine(v);
            }
            return sb.ToString();
        }

        /// <summary>這位 persona 的角色設定還有沒有待本人填寫的格子（早安提示用）。沒有 character 欄 ⇒ false（舊人不提示）。</summary>
        public static bool HasPendingSelfFill(string iLettersRoot, string iPersona)
        {
            try
            {
                string aPath = Path.Combine(SCP_LettersPaths.ProfileDir(new SCP_LettersRoot(iLettersRoot), iPersona), "character.md");
                return File.Exists(aPath) && File.ReadAllText(aPath, Encoding.UTF8).Contains(PendingMark);
            }
            catch (Exception) { return false; }
        }

        /// <summary>頭像規格（Agent 自己畫，或寫進繪製單給繪師）。規格從現有 11 張量的（TASK-0428 留言 #2）。</summary>
        public static string AvatarSpec(string iPersona, string iColor, string iCharacter)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"## {iPersona} 頭像規格");
            sb.AppendLine();
            sb.AppendLine("- **尺寸**：1024×1024 正方形；**格式**：PNG（JPEG 也收，看檔頭）。");
            sb.AppendLine("- **畫風**：動漫賽璐璐、清楚的線稿，**單人**立繪，大腿以上到全身，角色置中、四邊留空。");
            sb.AppendLine("- **背景**：純黑（與現有頭像一致）。");
            sb.AppendLine($"- **配色**：服裝主色＝主題色 {(iColor.Length > 0 ? "`" + iColor + "`" : "（還沒定 —— 先跟本人確認，定了寫進 color）")}。");
            sb.AppendLine("- **標誌物**：一到兩個從下面角色設定來的小物件（例：basecamp 的咖啡杯＋圍巾、gura 的鯊魚兜帽＋尾巴）。");
            sb.AppendLine("- **參照作品角色時**：取用範圍照角色設定裡的「參照範圍」；⛔ 不臨摹原作官方圖。");
            sb.AppendLine($"- **掛上**：`senate cmd persona-display --arg op=avatar --arg persona={iPersona} --arg src=<檔>`（已有頭像要 `--arg confirm=1`）。");
            sb.AppendLine();
            sb.AppendLine("### 角色設定（建立時）");
            sb.AppendLine();
            foreach (string line in (iCharacter ?? "").Replace("\r\n", "\n").Split('\n'))
                sb.AppendLine(line.StartsWith("#", StringComparison.Ordinal) ? "#" + line : line);
            return sb.ToString();
        }

        /// <summary>
        /// 新開 agent：`agent_banks[agent] = account`。已存在 ⇒ 擋（⛔ 不覆蓋 —— Editor 版會覆蓋，那會把別人麾下的錢改道）。
        /// 原子寫入、拿檔鎖、保留其他鍵與換行風格。
        /// </summary>
        public static bool AddAgentBank(string iDataRoot, string iAgent, string iAccount, out string oError)
        {
            oError = "";
            string aPath = RegistryMetaPath(iDataRoot);
            try
            {
                using (SCP_FileLock.Acquire(aPath))
                {
                    if (!SCP_AtomicFileRead.TryReadAllText(aPath, out string aText, out SCP_FileReadState aState))
                    { oError = $"讀不到 {aPath}（{aState}）—— ⛔ 不新建一份（那會蓋掉 agent 對照表）"; return false; }
                    SCP_JsonData aJd = SCP_JsonData.Parse(aText);
                    if (!aJd.IsObject) { oError = $"{aPath} 不是 JSON 物件"; return false; }
                    if (!aJd["agent_banks"].IsObject) aJd["agent_banks"] = SCP_JsonData.NewObject();
                    if (aJd["agent_banks"].Contains(iAgent)) { oError = $"agent `{iAgent}` 已經在 agent_banks"; return false; }
                    aJd["agent_banks"][iAgent] = iAccount;
                    string aOut = SCP_JsonWriter.Write(aJd, SCP_JsonStyle.UclLegacy);
                    if (aText.Contains("\r\n")) aOut = aOut.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    string aTmp = aPath + ".tmp";
                    File.WriteAllText(aTmp, aOut, new UTF8Encoding(false));
                    SCP_TextFile.ReplaceOrMove(aTmp, aPath);
                }
                // agent_banks 變了 ⇒ 常駐 Server 的帳號快取要重載（TASK-0428）
                string aStamp = SCP.Core.Bank.SCP_BankAccountResolver.Touch(iDataRoot);
                if (aStamp.Length > 0) { oError = "agent 已登記，但" + aStamp; return false; }
                // 讀回
                foreach (var kv in AgentBanks(iDataRoot))
                    if (kv.Key == iAgent && kv.Value == iAccount) return true;
                oError = "寫入後讀回對不上（agent_banks 沒有這一格）";
                return false;
            }
            catch (Exception e) { oError = e.GetType().Name + ": " + e.Message; return false; }
        }

        public static List<double> GenVector(Random iRng)
        {
            var v = new List<double>(VectorDim);
            for (int i = 0; i < VectorDim; i++) v.Add(Math.Round(iRng.NextDouble() * 2.0 - 1.0, 4));
            return v;
        }

        /// <summary>sha256("x.xxxx,x.xxxx,…") 前 8 hex（鏡像 Editor 版／awakening.hash_vector —— 兩端算出同值才能互相驗證）。</summary>
        public static string HashVector(List<double> iV)
        {
            var parts = new List<string>(iV.Count);
            foreach (double x in iV) parts.Add(x.ToString("F4", CultureInfo.InvariantCulture));
            using (var sha = SHA256.Create())
            {
                byte[] b = sha.ComputeHash(Encoding.UTF8.GetBytes(string.Join(",", parts)));
                var sb = new StringBuilder();
                for (int i = 0; i < 4; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}
