// 區塊職責：**Plurk 帳號管理頁**（Senate 版，TASK-0362）—— 共用帳號指到哪一份 secret、每個 persona 用個人還是共用、產生憑證 .enc。
// 物理意義：三格：共用帳號／產生憑證／persona 對照。
//          解析邏輯**不在本頁**，在 `SCP_PlurkAccounts.Resolve`（單一解析點）—— 頁面只顯示與寫入，判準留在解析器裡。
// 數值影響：
//   · **本頁不顯示、不讀取任何 token**：只處理 secret **id**；憑證本體在「加密檔管理」頁（`senate ui --page secrets`）。
//   · 掃描與讀檔只在 `Load()`（進頁／重新整理／寫入後）—— Draw 裡零 IO。
//   · persona 寫入走 `SCP_PlurkAccounts.SetPersonaAccount` → `SCP_PersonaProfileWrite.SetField`（actor/reason 必填、有審計）。
//   · 產生憑證：JSON 在記憶體組好**直接加密**（明文 `.txt` 不落地）；`.enc` 已存在要勾「覆蓋」；寫之前自己回解一次；
//     成功後四個憑證欄與密碼全部清空（換一代欄位 key，同 SCP_GuiSecretPage）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。視窗文字不放 emoji（字型沒有那些字 ⇒ 方框）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Paths;
using SCP.Core.Plurk;
using SCP.Core.Secret;

namespace SCP.Core.Gui
{
    public sealed class SCP_GuiPlurkPage : SCP_GuiToolPage
    {
        const string NoneOption = "(未設定)";

        sealed class Row
        {
            public string Persona = "";
            public SCP_PlurkAccountResolution Res = new SCP_PlurkAccountResolution();
            public SCP_SecretInfo? Secret;   // null ＝ 解析到的 id 沒有對應的 .enc
        }

        readonly ISCP_GuiAppContext m_Ctx;
        SCP_PlurkContext? m_Plurk;
        string? m_RootProblem;
        SCP_PlurkAccountsConfig m_Config = new SCP_PlurkAccountsConfig();
        string m_ConfigError = "";
        readonly List<string> m_Options = new List<string>();   // [0] = NoneOption，其後為 secret id
        readonly List<Row> m_Rows = new List<Row>();
        string? m_Message;
        /// <summary>欄位 key 帶世代號：清空輸入時換一代，避開 immediate mode 的「舊值留在 host 裡」。</summary>
        int m_Gen;
        /// <summary>下拉選單的世代號：存檔／放棄／寫入嘗試之後換一代，選單回到「磁碟上的現況」。
        /// ⚠ 不換的話，寫入失敗時選單仍停在新值 ≠ 現況 ⇒ **每一格都會再觸發一次寫入**。</summary>
        int m_PickGen;

        public SCP_GuiPlurkPage(ISCP_GuiAppContext iCtx) : base() { m_Ctx = iCtx; }

        public const string PageKey = "plurk";
        public override string Key { get { return PageKey; } }
        public override string Title { get { return "Plurk 帳號"; } }
        public override string? MenuGroup { get { return "設定"; } }

        public override void OnPush() { base.OnPush(); Load(); }

        void Load()
        {
            SCP_PathResolution aData = m_Ctx.AgentCommandsRoot, aLetters = m_Ctx.LettersRoot;
            m_RootProblem = aData.Error ?? aLetters.Error;
            if (m_RootProblem == null && (aData.Value.Length == 0 || aLetters.Value.Length == 0)) m_RootProblem = "資料根或信件夾根是空的";
            m_Rows.Clear();
            m_Options.Clear();
            m_Options.Add(NoneOption);
            if (m_RootProblem != null) { m_Plurk = null; return; }
            m_Plurk = new SCP_PlurkContext(aData.Value, aLetters.Value);

            m_ConfigError = "";
            try { m_Config = SCP_PlurkAccounts.Load(m_Plurk); }
            catch (Exception e)
            {
                // 「registry 壞了」與「還沒設共用帳號」必須可分辨 —— 所以壞掉要說出來。
                m_Config = new SCP_PlurkAccountsConfig();
                m_ConfigError = e.Message;
            }
            m_Options.AddRange(SCP_PlurkAccounts.ListSecretIds(m_Plurk));

            var aPersonas = new List<string>(SCP_PersonaProfilePool(m_Plurk));
            aPersonas.Sort(StringComparer.Ordinal);
            foreach (string p in aPersonas)
            {
                SCP_PlurkAccountResolution aRes = SCP_PlurkAccounts.Resolve(m_Plurk, p);
                m_Rows.Add(new Row { Persona = p, Res = aRes, Secret = aRes.Resolved ? SCP_PlurkAccounts.FindSecret(m_Plurk, aRes.SecretId) : null });
            }
        }

        static List<string> SCP_PersonaProfilePool(SCP_PlurkContext iCtx)
            => SCP.Core.Letters.SCP_PersonaProfile.PoolNames(iCtx.LettersRoot);

        protected override void TopBarButtons(SCP_Ui g)
        {
            base.TopBarButtons(g);
            if (g.Button("重新整理", "plurk/reload")) { m_Message = null; Load(); }
            // 設完帳號的下一個動作十之八九是去裝／查那份憑證 —— 兩頁互跳，不必繞回選單
            if (g.Button("加密檔管理", "plurk/goto-secrets")) Controller?.Push(new SCP_GuiSecretPage(m_Ctx));
        }

        protected override void DrawContent(SCP_Ui g)
        {
            g.Note("只分兩種：**共用（公用帳號）** 與 **個人**。帳號憑證（consumer key/secret ＋ access token/secret）存在加密檔，"
                   + "本頁只處理「誰用哪一份」的 id 對應，不顯示也不讀取任何 token。persona 沒設個人帳號 ⇒ 回落共用；"
                   + "**多人共用的帳號發文末行必須署名**（Tim 2026-08-16 硬規則 —— 時間軸上讀者只看得到帳號）。");
            if (m_RootProblem != null || m_Plurk == null)
            {
                g.Note("⚠ 本頁沒有資料來源 ⇒ 這不是「沒有帳號」，是「量不到」。原因：" + (m_RootProblem ?? "?"));
                g.Note("資料根／信件夾根設在「路徑管理」頁（`senate ui --page paths`）。");
                return;
            }
            if (m_Plurk.SecretsDirWarning != null) g.Note("⚠ " + m_Plurk.SecretsDirWarning);
            if (m_ConfigError.Length > 0)
                g.Note($"⛔ 設定檔讀取失敗：{m_ConfigError}　路徑：`{SCP_PlurkAccounts.RegistryPath(m_Plurk)}`　"
                       + "**不會自動覆蓋壞檔** —— 覆蓋掉的是別人寫的設定，而那筆改動沒有地方留得住。");
            if (m_Message != null) g.Note(m_Message);

            DrawShared(g, m_Plurk);
            g.Separator();
            DrawCredentialPanel(g, m_Plurk);
            g.Separator();
            DrawPersonas(g, m_Plurk);
        }

        // ── 共用（公用）帳號 ─────────────────────────────────────
        void DrawShared(SCP_Ui g, SCP_PlurkContext iCtx)
        {
            g.Title("共用帳號（公用）");
            g.Label($"設定檔：`{SCP_PlurkAccounts.RegistryPath(iCtx)}`");
            string aCur = (m_Config.SharedSecretId ?? "").Trim();
            string aCurOpt = aCur.Length == 0 ? NoneOption : aCur;
            if (!m_Options.Contains(aCurOpt)) m_Options.Add(aCurOpt);   // 設定檔指到一份已不存在的 .enc ⇒ 照樣列出（下面會說它不存在）
            string aPick = g.Dropdown("secret id", m_Options, aCurOpt, $"plurk/shared/pick/{m_PickGen}");
            if (m_Options.Count <= 1)
                g.Note($"⚠ secrets 資料夾底下沒有任何 `{SCP_PlurkAccounts.SecretPrefix}*.enc`。先在下面「產生憑證」或「加密檔管理」頁做出 "
                       + $"`{SCP_PlurkAccounts.SecretPrefix}<名字>.enc` —— 本頁的清單來源就是那些檔名。");
            DrawSecretState(g, iCtx, aPick == NoneOption ? "" : aPick);
            using (g.Row())
            {
                if (g.Button("存檔", "plurk/shared/save"))
                {
                    try
                    {
                        m_Config.SharedSecretId = aPick == NoneOption ? "" : aPick;
                        SCP_PlurkAccounts.Save(iCtx, m_Config);
                        m_Message = "✓ 已寫入共用帳號：" + (m_Config.SharedSecretId.Length == 0 ? NoneOption : m_Config.SharedSecretId);
                    }
                    catch (Exception e) { m_Message = "✗ 寫入失敗：" + e.Message; }
                    m_PickGen++;
                    Load();
                }
                if (g.Button("放棄改動", "plurk/shared/revert")) { m_PickGen++; Load(); }
            }
        }

        // 「有 .enc」跟「明文已安裝」是兩件事 —— 只有後者才真的能發文，所以分開講，不合併成一個綠燈。
        void DrawSecretState(SCP_Ui g, SCP_PlurkContext iCtx, string iSecretId)
        {
            if (iSecretId.Length == 0) { g.Label("（未指定 secret —— 所有沒設個人帳號的 persona 都會解析成 unset）"); return; }
            SCP_SecretInfo? aInfo = SCP_PlurkAccounts.FindSecret(iCtx, iSecretId);
            if (aInfo == null) { g.Label($"⛔ 找不到 `{iSecretId}.enc` —— 這個 id 指向一份不存在的憑證"); return; }
            g.Label($".enc：有　｜　明文：{(aInfo.PlainExists ? "已安裝" : "⚠ 未安裝（到加密檔管理解密）")}"
                    + $"　｜　label：{(aInfo.Label.Length == 0 ? "(無 label)" : aInfo.Label)}");
            if (aInfo.Error.Length > 0) g.Label("⚠ metadata 讀取異常：" + aInfo.Error);
        }

        // ── 產生憑證（填欄位 → 直接產出 .enc，明文不落地）──────────────
        void DrawCredentialPanel(SCP_Ui g, SCP_PlurkContext iCtx)
        {
            g.Title("產生憑證（填欄位 → 直接產出 .enc）");
            g.Label("OAuth 1.0a 四個值：前兩個認 app、後兩個認「以哪個帳號發文」。四欄到齊才發得出去 —— 只有 consumer key/secret 是不夠的。");
            string aId = g.TextField("secret id", SCP_PlurkAccounts.SecretPrefix + "shared", "plurk/cred/id").Trim();
            if (aId.Length > 0 && !aId.StartsWith(SCP_PlurkAccounts.SecretPrefix, StringComparison.Ordinal))
                g.Note($"⚠ id 必須以 `{SCP_PlurkAccounts.SecretPrefix}` 開頭，否則本頁的帳號清單掃不到它（產出成功但選單裡沒有）。");
            string aCk = g.PasswordField("consumer key", $"plurk/{m_Gen}/ck");
            string aCs = g.PasswordField("consumer secret", $"plurk/{m_Gen}/cs");
            string aAt = g.PasswordField("access token", $"plurk/{m_Gen}/at");
            string aAs = g.PasswordField("access token secret", $"plurk/{m_Gen}/as");
            string aNote = g.TextField("備註（note，選填）", "", "plurk/cred/note");
            string aPass = g.PasswordField("Passphrase", $"plurk/{m_Gen}/pass");
            string aPass2 = g.PasswordField("再次確認", $"plurk/{m_Gen}/pass2");
            string aHint = g.TextField("提示（hint，選填；不參與加密）", "", "plurk/cred/hint");
            string aLabel = g.TextField("標籤（label，選填）", "", "plurk/cred/label");

            string aEnc = aId.Length == 0 ? "" : iCtx.SecretsDir + "/" + aId + ".enc";
            bool aExists = aEnc.Length > 0 && File.Exists(aEnc);
            bool aOverwrite = false;
            if (aExists)
            {
                g.Note($"⚠ `{Path.GetFileName(aEnc)}` 已存在。覆蓋掉的憑證**拿不回來**（passphrase 不可反推），而覆蓋成功跟第一次建立看起來一樣。");
                aOverwrite = g.Toggle("我確定要覆蓋它", false, $"plurk/{m_Gen}/overwrite");
            }
            g.Label("產出的 `.enc` 可 commit。**但它還不能用** —— 要到「加密檔管理」做一次解密安裝產生明文，token 狀態才會變成已安裝。");
            if (!g.Button(aExists ? "產出 .enc 並覆蓋" : "產出 .enc（明文不落地）", "plurk/cred/go")) return;

            string? aWhy =
                !aId.StartsWith(SCP_PlurkAccounts.SecretPrefix, StringComparison.Ordinal) ? $"id 必須以 `{SCP_PlurkAccounts.SecretPrefix}` 開頭"
                : aId.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 ? "id 不可含 `/` `\\` `:`"
                : (aCk.Trim().Length == 0 || aCs.Trim().Length == 0 || aAt.Trim().Length == 0 || aAs.Trim().Length == 0)
                    ? "四個憑證欄都要填 —— 缺 access token 的症狀是「看起來設好了但發不出去」，而那時分不出缺的是哪一半"
                : aPass.Length == 0 ? "沒有輸入 passphrase"
                : aPass != aPass2 ? "兩次 passphrase 不一致"
                : (aExists && !aOverwrite) ? ".enc 已存在而沒勾「覆蓋」"
                : null;
            if (aWhy != null) { m_Message = "✗ " + aWhy + " —— 沒有寫任何東西"; ClearInputs(g); return; }
            try
            {
                // JSON 用結構組不用字串串接 —— 憑證裡若含引號或反斜線，串接會產出「寫成功了但讀不回來」的檔
                var aJson = new SCP_UclLegacyObject();
                aJson.Set("account", aId);
                aJson.Set("note", aNote);
                aJson.Set("consumer_key", aCk.Trim());
                aJson.Set("consumer_secret", aCs.Trim());
                aJson.Set("access_token", aAt.Trim());
                aJson.Set("access_token_secret", aAs.Trim());
                byte[] aPlain = new UTF8Encoding(false).GetBytes(SCP_UclLegacyJson.ToJsonBeautify(aJson));
                byte[] aBytes = SCP_SecretCrypto.Encrypt(aPlain, aPass, aHint, aLabel);
                SCP_SecretCrypto.Decrypt(aBytes, aPass);   // 寫之前自己解一次：寫出一顆自己解不開的檔，比不寫更糟
                Directory.CreateDirectory(iCtx.SecretsDir);
                File.WriteAllBytes(aEnc, aBytes);
                // ⚠ 只報檔名與長度，**不報任何值**
                m_Message = $"✓ 已產出 `{Path.GetFileName(aEnc)}`（{aBytes.Length} bytes；已先回解驗證）。明文全程沒有落地；"
                            + "要真的能用還需到「加密檔管理」做一次解密安裝。⚠ 這顆 .enc 要自己提交";
            }
            catch (Exception e) { m_Message = $"✗ 產出失敗：{e.GetType().Name}: {e.Message} —— 沒有寫任何東西"; }
            ClearInputs(g);
            Load();
        }

        /// <summary>清空所有秘密欄（四個憑證值＋兩個密碼）：換一代 key，舊值留在 host 裡也不會再被讀到。</summary>
        void ClearInputs(SCP_Ui g)
        {
            foreach (string k in new[] { "ck", "cs", "at", "as", "pass", "pass2" })
                g.SetField($"plurk/{m_Gen}/{k}{SCP_Ui.MaskedIdSuffix}", "");
            m_Gen++;
        }

        // ── persona 對照（個人帳號 override）──────────────────────────
        void DrawPersonas(SCP_Ui g, SCP_PlurkContext iCtx)
        {
            g.Title($"persona 對照（{m_Rows.Count} 位）");
            g.Label($"個人帳號寫在該 persona 的 profile（欄名 `{SCP_PlurkAccounts.PersonaField}`），走 persona 設定的唯一寫入端（actor/reason 必填、有審計）。"
                    + $"設成「{NoneOption}」＝清掉 override、回落共用。");
            // ⚠ 寫入**等迴圈跑完才做**：SetPersona 會 Load() 重建 m_Rows，在迭代中途重建 ⇒ InvalidOperationException
            //   （實測：寫入其實成功了、頁面卻炸掉 —— 「做了」與「沒做」在畫面上同形）。
            (string Persona, string SecretId)? aPending = null;
            foreach (Row r in m_Rows)
            {
                using (g.Row())
                {
                    string aKind = r.Res.Source == SCP_PlurkAccounts.SourcePersona ? "個人"
                                 : r.Res.Source == SCP_PlurkAccounts.SourceShared ? "共用" : "—";
                    string aToken = !r.Res.Resolved ? "" : r.Secret == null ? "⛔ 無 .enc" : r.Secret.PlainExists ? "已安裝" : "⚠ 未安裝";
                    // 解析結果一律連 Source 一起顯示 —— 「用哪個」跟「憑什麼」要一起看得到
                    g.Label($"{r.Persona}　{aKind}　{r.Res.Describe()}　{aToken}");
                    string aCurOpt = r.Res.Source == SCP_PlurkAccounts.SourcePersona && r.Res.SecretId.Length > 0 ? r.Res.SecretId : NoneOption;
                    if (!m_Options.Contains(aCurOpt)) m_Options.Add(aCurOpt);
                    // ⚠ key 帶 persona 名：explicit key 不吃 IdScope，同名 key 只靠出現順序加 #n 區分 —— 名單一變，選單就對到別人那一列
                    string aPick = g.Dropdown("個人帳號", m_Options, aCurOpt, $"plurk/p/{r.Persona}/pick/{m_PickGen}");
                    if (aPick != aCurOpt && aPending == null) aPending = (r.Persona, aPick == NoneOption ? "" : aPick);
                }
            }
            if (aPending != null) SetPersona(iCtx, aPending.Value.Persona, aPending.Value.SecretId);
        }

        void SetPersona(SCP_PlurkContext iCtx, string iPersona, string iSecretId)
        {
            string aReason = iSecretId.Length == 0 ? "清除個人 Plurk 帳號 override（回落共用）" : "設定個人 Plurk 帳號＝" + iSecretId;
            bool aOk = SCP_PlurkAccounts.SetPersonaAccount(iCtx, iPersona, iSecretId, nameof(SCP_GuiPlurkPage), aReason,
                                                           out string aErr, out string aAuditWarn);
            m_Message = aOk ? $"✓ {iPersona}：{aReason}" + (aAuditWarn.Length > 0 ? "　⚠ " + aAuditWarn : "")
                            : $"✗ {iPersona} 寫入失敗：{aErr}";
            m_PickGen++;   // 不論成敗都換一代：選單回到現況，⛔ 不會每一格重送
            Load();
        }
    }
}
