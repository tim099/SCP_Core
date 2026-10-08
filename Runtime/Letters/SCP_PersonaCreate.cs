// 區塊職責：**建一位新 persona 的規劃與組裝**（TASK-0428）—— 名稱驗證、綁定的 agent（或新開一個）、
//           identity_vector／fork 血統、角色設定檔、頭像規格。寫入身分欄仍交給 `SCP_PersonaProfileWrite.Create`（唯一寫入端）。
// 物理意義：整份身分欄 JSON 由本檔組 —— CLI（`persona-create`）與 Senate 的 persona 管理頁（TASK-0424）呼叫同一份。
// 數值影響：`Plan` 純讀（draft 就是它）；`AddAgentBank` 寫 `AwakenInit/_registry_meta.json` 的 `agent_banks` 一格。
//
// ⚠ 欄位規則：
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
        /// 新開 agent：`agent_banks[agent] = account`。已存在 ⇒ 擋（⛔ 不覆蓋 —— 覆蓋會把別人麾下的錢改道）。
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

        // ==========================================================
        // 區塊職責：persona 的 letters 變成獨立 git repo（TASK-0428，Tim 2026-10-06 選 (a)；remote 由 Tim 處理）。
        // 物理意義：其他 persona 的 letters 都是 AgentCommands 底下的 submodule（各自一個 GitHub repo）；
        //   🩸 erina（第一個真的 create）只是一個未追蹤目錄 ⇒ 晚安提交收尾信沒有 repo 可提交。
        //   ⇒ 本地：git init（master）＋ 同步格式的 .gitignore ＋ 第一筆提交。
        //     有 remote_url（Tim 開好遠端之後）：設 origin、在父層 submodule add、只提交 .gitmodules 與那一格指向。⛔ 不 push。
        // ⚠ .gitignore 的格式從現有檔反推（python 版的同步工具已隨 python 退場刪除）：
        //   檔頭 4 行 ＋ 基線（`letters/Template/.gitignore`）全文 ＋ END 標記 ＋ 本 persona 自訂區；
        //   `baseline_sha256` ＝ 基線**換行正規化成 LF** 後的 sha256（對 kotoko 現有檔驗過：d3e78f72…）。
        // ==========================================================
        public const string GitignoreBaselinePersona = "Template";

        /// <summary>照基線組一份新 persona 的 .gitignore。基線讀不到回 null。</summary>
        public static string? BuildLettersGitignore(string iLettersRoot, out string oError)
        {
            oError = "";
            string aTpl = Path.Combine(iLettersRoot, GitignoreBaselinePersona, ".gitignore");
            if (!File.Exists(aTpl)) { oError = "基線 .gitignore 不在：" + aTpl; return null; }
            string aBase = File.ReadAllText(aTpl, Encoding.UTF8).Replace("\r\n", "\n");
            string aSha;
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (byte b in sha.ComputeHash(new UTF8Encoding(false).GetBytes(aBase))) sb.Append(b.ToString("x2"));
                aSha = sb.ToString();
            }
            var aOut = new StringBuilder();
            aOut.Append("# ╔═══ BASELINE — 由 letters/Template/.gitignore 同步，勿在本區編輯 ═══╗\n");
            aOut.Append("# 要改共用規則：改基線檔，再把本區照抄成新基線（同步工具已退場、目前沒有替代；baseline_sha256 對得上＝已同步）。\n");
            aOut.Append("# 自訂規則寫在檔尾「本 persona 自訂」區 —— 同步基線時不要動那一區。\n");
            aOut.Append("# baseline_sha256: " + aSha + "\n");
            aOut.Append(aBase.TrimEnd('\n') + "\n");
            aOut.Append("# ╚═══ BASELINE END ═══╝\n\n");
            aOut.Append("### 本 persona 自訂\n");
            return aOut.ToString().Replace("\n", "\r\n");
        }

        // 區塊職責：`git add -A` 之前的外洩防線（TASK-0432）。
        // 物理意義：已有 .gitignore 的舊 persona 不會被重建 ⇒ 缺了這幾格，回傳檔（含憑證）、在線 lock（session token）、密封信會一起進第一筆提交，
        //   而 letters remote 是公開的 —— push 之後刪不掉。判準是「這一格有沒有被某一行蓋到」，不比對整份基線（各人自訂區合法地不同）。
        static readonly (string Need, string[] AnyOf)[] s_PrivateIgnores =
        {
            ("sealed/",                new[] { "sealed/", "/sealed/", "sealed" }),
            ("/profile/_session.json", new[] { "/profile/_session.json", "profile/_session.json", "_session.json" }),
            ("/cmd/*",                 new[] { "/cmd/*", "/cmd/", "cmd/", "/cmd" }),
        };

        /// <summary>這份 .gitignore 缺了哪幾格私密規則（空清單＝齊全）。只看非註解、非否定行的字面。</summary>
        public static List<string> MissingPrivateIgnores(string iGitignore)
        {
            var aLines = new HashSet<string>(StringComparer.Ordinal);
            foreach (string aRaw in iGitignore.Replace("\r\n", "\n").Split('\n'))
            {
                string t = aRaw.Trim();
                if (t.Length == 0 || t.StartsWith("#", StringComparison.Ordinal) || t.StartsWith("!", StringComparison.Ordinal)) continue;
                aLines.Add(t);
            }
            var aMissing = new List<string>();
            foreach (var (aNeed, aAnyOf) in s_PrivateIgnores)
            {
                bool aHit = false;
                foreach (string c in aAnyOf) if (aLines.Contains(c)) { aHit = true; break; }
                if (!aHit) aMissing.Add(aNeed);
            }
            return aMissing;
        }

        public static bool IsLettersRepo(string iLettersRoot, string iPersona)
        {
            string d = SCP_LettersPaths.PersonaDir(new SCP_LettersRoot(iLettersRoot), iPersona);
            return Directory.Exists(Path.Combine(d, ".git")) || File.Exists(Path.Combine(d, ".git"));
        }

        /// <summary>
        /// 本地初始化：git init（master）＋ .gitignore（沒有才寫）＋ 第一筆提交。已經是 repo ⇒ 什麼都不做、回 true。
        /// </summary>
        public static bool InitLettersRepo(string iLettersRoot, string iPersona, List<string> ioLines, out string oError)
        {
            oError = "";
            string d = SCP_LettersPaths.PersonaDir(new SCP_LettersRoot(iLettersRoot), iPersona);
            if (!Directory.Exists(d)) { oError = "letters 資料夾不存在：" + d; return false; }
            if (IsLettersRepo(iLettersRoot, iPersona)) { ioLines.Add($"· letters/{iPersona} 已經是 git repo（沒有重新 init）"); return true; }
            string aIgnore = Path.Combine(d, ".gitignore");
            bool aExisting = File.Exists(aIgnore);
            string? g = aExisting ? File.ReadAllText(aIgnore, Encoding.UTF8) : BuildLettersGitignore(iLettersRoot, out oError);
            if (g == null) return false;
            // ⛔ 先驗再寫、再 init：擋下時磁碟零變動（沒有 .gitignore 落檔、沒有 .git）。
            List<string> aMissing = MissingPrivateIgnores(g);
            if (aMissing.Count > 0)
            {
                oError = (aExisting ? $"letters/{iPersona}/.gitignore" : $"基線 letters/{GitignoreBaselinePersona}/.gitignore")
                         + " 缺私密規則：" + string.Join("、", aMissing)
                         + " —— `git add -A` 會把它們一起提交（letters remote 是公開的）。補上這幾行再重跑；什麼都沒寫。";
                return false;
            }
            if (!aExisting) File.WriteAllText(aIgnore, g, new UTF8Encoding(false));
            SCP.Core.Git.SCP_GitResult r = SCP.Core.Git.SCP_Git.Run(d, "init");
            if (!r.Ok) { oError = "git init 失敗：" + r.FirstLine; return false; }
            r = SCP.Core.Git.SCP_Git.Run(d, "symbolic-ref", "HEAD", "refs/heads/master");
            if (!r.Ok) { oError = "設 master 失敗：" + r.FirstLine; return false; }
            r = SCP.Core.Git.SCP_Git.Run(d, "add", "-A");
            if (!r.Ok) { oError = "git add 失敗：" + r.FirstLine; return false; }
            r = SCP.Core.Git.SCP_Git.Run(d, "commit", "-m", "Init（persona-create，TASK-0428）");
            if (!r.Ok) { oError = "第一筆提交失敗：" + r.FirstLine; return false; }
            SCP.Core.Git.SCP_GitResult aHead = SCP.Core.Git.SCP_Git.Run(d, "rev-parse", "--short", "HEAD");
            ioLines.Add($"✓ letters/{iPersona} 變成 git repo（master，第一筆 {aHead.StdOut.Trim()}；.gitignore 照 Template 基線）");
            return true;
        }

        /// <summary>
        /// 接上遠端（Tim 開好之後）：設 origin、在父層 submodule add、**只**提交 .gitmodules 與那一格指向。⛔ 不 push。
        /// </summary>
        public static bool AttachLettersRemote(string iLettersRoot, string iPersona, string iRemoteUrl, List<string> ioLines, out string oError)
        {
            oError = "";
            string d = SCP_LettersPaths.PersonaDir(new SCP_LettersRoot(iLettersRoot), iPersona);
            if (!IsLettersRepo(iLettersRoot, iPersona)) { oError = "還不是 git repo —— 先跑一次不帶 remote_url 的 op=repo"; return false; }
            SCP.Core.Git.SCP_GitResult r = SCP.Core.Git.SCP_Git.Run(d, "remote", "get-url", "origin");
            if (r.Ok && r.StdOut.Trim() != iRemoteUrl)
            { oError = $"origin 已經是 `{r.StdOut.Trim()}`，跟給的 `{iRemoteUrl}` 不同 —— ⛔ 不覆蓋，先確認哪個對"; return false; }
            if (!r.Ok)
            {
                r = SCP.Core.Git.SCP_Git.Run(d, "remote", "add", "origin", iRemoteUrl);
                if (!r.Ok) { oError = "設 origin 失敗：" + r.FirstLine; return false; }
            }
            ioLines.Add($"✓ origin ＝ {iRemoteUrl}");
            // 父層＝包著 letters 根的那個 repo（AgentCommands）
            SCP.Core.Git.SCP_GitResult aTop = SCP.Core.Git.SCP_Git.Run(iLettersRoot, "rev-parse", "--show-toplevel");
            if (!aTop.Ok) { oError = "找不到父層 repo：" + aTop.FirstLine; return false; }
            string aParent = aTop.StdOut.Trim();
            string aRel = Path.GetFullPath(d).Replace('\\', '/').Substring(Path.GetFullPath(aParent).Replace('\\', '/').TrimEnd('/').Length + 1);
            SCP.Core.Git.SCP_GitResult aKnown = SCP.Core.Git.SCP_Git.Run(aParent, "config", "-f", ".gitmodules", "--get", "submodule." + aRel + ".url");
            if (aKnown.Ok)
            {
                ioLines.Add($"· 父層已經登記這個 submodule（{aKnown.StdOut.Trim()}）—— 沒有重複登記");
                return AbsorbGitDir(aParent, aRel, d, ioLines, out oError);
            }
            r = SCP.Core.Git.SCP_Git.Run(aParent, "submodule", "add", iRemoteUrl, aRel);
            if (!r.Ok) { oError = "父層 submodule add 失敗：" + r.FirstLine; return false; }
            r = SCP.Core.Git.SCP_Git.Run(aParent, "commit", "-m", $"letters: 掛上 {iPersona} 的信件庫（persona-create，TASK-0428）", "--", ".gitmodules", aRel);
            if (!r.Ok) { oError = "父層提交失敗（.gitmodules 與指向已 stage，沒提交）：" + r.FirstLine; return false; }
            SCP.Core.Git.SCP_GitResult aHead = SCP.Core.Git.SCP_Git.Run(aParent, "rev-parse", "--short", "HEAD");
            ioLines.Add($"✓ 父層登記 submodule `{aRel}`，提交 {aHead.StdOut.Trim()}（只含 .gitmodules 與這一格指向；⛔ 沒有 push）");
            return AbsorbGitDir(aParent, aRel, d, ioLines, out oError);
        }

        // 區塊職責：把信件庫的 git 目錄收進父層 `.git/modules/…`，工作樹留指標檔 —— 跟其他 persona 同形（TASK-0432）。
        // 物理意義：本地 init 在先、submodule add 在後 ⇒ git 走「Adding existing repo」，`.git` 留在工作樹是**目錄**；
        //   那份工作副本上 `git clean -ffdx` 或刪掉資料夾，歷史就跟著消失（其他 persona 的還在 modules 裡）。🩸 erina 2026-10-06 現場。
        // ⚠ Windows：fsmonitor daemon 與開著這個 repo 的 GUI（Fork…）會鎖住 `.git`，搬移失敗 ⇒ 先停 daemon；
        //   還是失敗就回報、要人關掉 GUI 後重跑 op=repo（走「已經登記」那條，只補這一步）。
        static bool AbsorbGitDir(string iParent, string iRel, string iDir, List<string> ioLines, out string oError)
        {
            oError = "";
            if (File.Exists(Path.Combine(iDir, ".git"))) { ioLines.Add("· git 目錄已在父層 modules（`.git` 是指標檔）"); return true; }
            SCP.Core.Git.SCP_Git.Run(iDir, "fsmonitor--daemon", "stop");   // 沒在跑也無妨
            SCP.Core.Git.SCP_GitResult r = SCP.Core.Git.SCP_Git.Run(iParent, "submodule", "absorbgitdirs", "--", iRel);
            if (!r.Ok || !File.Exists(Path.Combine(iDir, ".git")))
            {
                oError = "已登記 submodule，但 git 目錄沒收進父層（" + r.FirstLine + "）—— 關掉開著這個 repo 的工具（Fork 等）後重跑 op=repo，只會補這一步";
                return false;
            }
            ioLines.Add("✓ git 目錄收進父層 modules，工作樹的 `.git` 改成指標檔");
            return true;
        }

        public static List<double> GenVector(Random iRng)
        {
            var v = new List<double>(VectorDim);
            for (int i = 0; i < VectorDim; i++) v.Add(Math.Round(iRng.NextDouble() * 2.0 - 1.0, 4));
            return v;
        }

        /// <summary>sha256("x.xxxx,x.xxxx,…") 前 8 hex（鏡像 awakening.hash_vector —— 兩端算出同值才能互相驗證）。</summary>
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
