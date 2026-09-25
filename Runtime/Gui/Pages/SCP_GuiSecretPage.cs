// 區塊職責：**加密檔管理頁**（Senate 版，TASK-0300）—— 列 secrets 資料夾的 .enc、一鍵解密全部、從明文加密。
// 物理意義：移植自 Unity 端 `UCL_SecretManagerPage`（全面改用 Senate 端的方向）；邏輯全在 `SCP_SecretStore`／`SCP_SecretCrypto`，
//          本頁只畫與收輸入 ⇒ 不需要 Unity Editor。
//          「一鍵解密」＝**同一組密碼套到每一顆，明文已在的跳過**（Tim 2026-09-25）；結果逐顆分五種印。
// 數值影響：
//   · 密碼欄走 `SCP_Ui.PasswordField`：畫面遮罩、文字 renderer 不印、⛔ 不進落盤的 state；按完即清空。
//   · 提示（hint）預設不顯示 —— 它是給「忘記密碼的自己」的線索，不該開頁就攤在畫面上（UCL 版同一格：顯示提示是一顆鈕）。
//   · 加密覆寫既有 .enc 要兩段式（勾「覆寫」才會寫）—— 覆寫之後舊密碼就解不開新檔，回不來。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。視窗文字不放 emoji（字型沒有那些字 ⇒ 方框）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SCP.Core.Paths;
using SCP.Core.Secret;

namespace SCP.Core.Gui
{
    public sealed class SCP_GuiSecretPage : SCP_GuiToolPage
    {
        readonly ISCP_GuiAppContext m_Ctx;
        SCP_PathResolution m_Root = new SCP_PathResolution("", "?", "還沒讀");
        string m_Dir = "";
        string? m_DirWarning;
        List<SCP_SecretInfo> m_List = new List<SCP_SecretInfo>();
        List<SCP_SecretDecryptItem>? m_LastDecrypt;
        string? m_Message;
        /// <summary>欄位 key 帶世代號：清空輸入時換一代，避開 immediate mode 的「舊值留在 host 裡」（同 tavern-routing 頁）。</summary>
        int m_Gen;

        public SCP_GuiSecretPage(ISCP_GuiAppContext iCtx) : base() { m_Ctx = iCtx; }

        public const string PageKey = "secrets";
        public override string Key { get { return PageKey; } }
        public override string Title { get { return "加密檔管理"; } }
        public override string? MenuGroup { get { return "設定"; } }

        public override void OnPush() { base.OnPush(); Load(); }

        bool HasRoot => m_Root.Error == null && m_Root.Value.Length > 0;

        void Load()
        {
            m_Root = m_Ctx.AgentCommandsRoot;
            if (!HasRoot) { m_Dir = ""; m_List = new List<SCP_SecretInfo>(); return; }
            m_Dir = SCP_SecretStore.ResolveDir(m_Root.Value, out m_DirWarning);
            try { m_List = SCP_SecretStore.Scan(m_Dir); }
            catch (Exception e) { m_List = new List<SCP_SecretInfo>(); m_Message = $"✗ 掃描失敗：{e.GetType().Name}: {e.Message}"; }
        }

        protected override void TopBarButtons(SCP_Ui g)
        {
            base.TopBarButtons(g);
            if (g.Button("重新掃描", "secrets/reload")) { m_Message = null; Load(); }
        }

        protected override void DrawContent(SCP_Ui g)
        {
            if (!HasRoot)
            {
                g.Note("⚠ 本頁沒有資料來源 ⇒ 這不是「沒有任何加密檔」，是「量不到」。原因："
                       + (string.IsNullOrEmpty(m_Root.Error) ? "資料根是空的（沒有人填過）" : m_Root.Error));
                g.Note("資料根設在「路徑管理」頁（`senate ui --page paths`）。");
                return;
            }

            g.Note($"掃描資料夾：`{m_Dir}`（名稱來自 `{SCP_SecretStore.ConfigFileName}` 的 `{SCP_SecretStore.ConfigKey}`，缺檔＝`{SCP_SecretStore.DefaultDirName}`；與 Unity 端讀同一個檔）");
            if (m_DirWarning != null) g.Note("⚠ " + m_DirWarning);
            if (!Directory.Exists(m_Dir)) { g.Note("⚠ 資料夾不存在 ⇒ 這不是「沒有加密檔」，是位置不對或還沒建"); return; }
            if (m_Message != null) g.Note(m_Message);

            DrawList(g);
            g.Separator();
            DrawDecryptAll(g);
            g.Separator();
            DrawEncrypt(g);
        }

        void DrawList(SCP_Ui g)
        {
            int aPlain = m_List.Count(x => x.PlainExists);
            int aBad = m_List.Count(x => x.Error.Length > 0);
            g.Title($"加密檔（{m_List.Count} 顆：明文已在 {aPlain}／待解密 {m_List.Count - aPlain - aBad}／讀不了 {aBad}）");
            if (m_List.Count == 0) { g.Note("・資料夾裡沒有 .enc"); return; }
            bool aShowHint = g.Toggle("顯示提示（hint）", false, "secrets/showhint");
            string[] aHead = aShowHint
                ? new[] { "名稱", "label", "提示", "建立", "明文" }
                : new[] { "名稱", "label", "建立", "明文" };
            using (g.Table(aHead))
            {
                foreach (SCP_SecretInfo s in m_List)
                {
                    string aState = s.Error.Length > 0 ? "讀不了：" + s.Error : s.PlainExists ? "已在" : "待解密";
                    if (aShowHint) g.TableRow(s.Name, s.Label, s.Hint.Length > 0 ? s.Hint : "—", s.CreatedAt, aState);
                    else g.TableRow(s.Name, s.Label, s.CreatedAt, aState);
                }
            }
        }

        void DrawDecryptAll(SCP_Ui g)
        {
            int aTodo = m_List.Count(x => !x.PlainExists && x.Error.Length == 0);
            g.Title("一鍵解密全部（同一組密碼；明文已在的跳過，⛔ 不覆寫）");
            string aPass = g.PasswordField("密碼", $"secrets/{m_Gen}/decpass");
            if (g.Button($"解密 {aTodo} 顆待解密的", "secrets/decrypt-all"))
            {
                if (aPass.Length == 0) m_Message = "✗ 沒有輸入密碼 —— ⛔ 不拿空字串去試每一顆";
                else
                {
                    try { m_LastDecrypt = SCP_SecretStore.DecryptAll(m_Dir, aPass); m_Message = null; }
                    catch (Exception e) { m_Message = $"✗ 一鍵解密中斷：{e.GetType().Name}: {e.Message}"; }
                    ClearInputs(g);
                    Load();
                }
            }
            if (m_LastDecrypt == null) return;

            var aBy = m_LastDecrypt.GroupBy(x => x.Outcome).ToDictionary(k => k.Key, v => v.Count());
            int C(SCP_SecretDecryptOutcome o) => aBy.TryGetValue(o, out int n) ? n : 0;
            g.Note($"上一次：解成功 {C(SCP_SecretDecryptOutcome.Decrypted)}／已有明文跳過 {C(SCP_SecretDecryptOutcome.SkippedPlainExists)}"
                   + $"／密碼不對 {C(SCP_SecretDecryptOutcome.WrongPassword)}／檔案壞了 {C(SCP_SecretDecryptOutcome.Broken)}"
                   + $"／寫不出去 {C(SCP_SecretDecryptOutcome.WriteFailed)}");
            if (C(SCP_SecretDecryptOutcome.WrongPassword) > 0)
                g.Note("・「密碼不對」的那幾顆多半是用另一組密碼加的 ⇒ 換那組再按一次（已解開的會被跳過）");
            using (g.Table("名稱", "結果", "說明"))
                foreach (SCP_SecretDecryptItem r in m_LastDecrypt)
                    g.TableRow(r.Name, OutcomeText(r.Outcome), r.Detail);
        }

        static string OutcomeText(SCP_SecretDecryptOutcome o) => o switch
        {
            SCP_SecretDecryptOutcome.Decrypted => "解成功",
            SCP_SecretDecryptOutcome.SkippedPlainExists => "跳過（已有明文）",
            SCP_SecretDecryptOutcome.WrongPassword => "密碼不對",
            SCP_SecretDecryptOutcome.Broken => "檔案壞了",
            SCP_SecretDecryptOutcome.WriteFailed => "寫不出去",
            _ => o.ToString(),
        };

        void DrawEncrypt(SCP_Ui g)
        {
            g.Title("從明文加密（產出同名 .enc）");
            List<string> aTxt;
            try
            {
                aTxt = Directory.GetFiles(m_Dir, "*.txt", SearchOption.AllDirectories)
                    .Select(p => p.Replace('\\', '/').Substring(m_Dir.Length).TrimStart('/'))
                    .OrderBy(p => p, StringComparer.Ordinal).ToList();
            }
            catch (Exception e) { g.Note($"✗ 列明文失敗：{e.Message}"); return; }
            if (aTxt.Count == 0) { g.Note("・資料夾裡沒有 .txt 明文"); return; }

            string aPick = g.Dropdown("明文檔", aTxt, aTxt[0], "secrets/enc/pick");
            string aPass = g.PasswordField("密碼", $"secrets/{m_Gen}/encpass");
            string aPass2 = g.PasswordField("再次確認", $"secrets/{m_Gen}/encpass2");
            string aHint = g.TextField("提示（hint，選填；不參與加密）", "", $"secrets/{m_Gen}/enchint");
            string aLabel = g.TextField("label（選填）", "", $"secrets/{m_Gen}/enclabel");
            string aTxtAbs = m_Dir + "/" + aPick;
            string aEncAbs = aTxtAbs.EndsWith(".txt", StringComparison.OrdinalIgnoreCase) ? aTxtAbs.Substring(0, aTxtAbs.Length - 4) + ".enc" : aTxtAbs + ".enc";
            bool aExists = File.Exists(aEncAbs);
            bool aOverwrite = false;
            if (aExists)
            {
                g.Note($"⚠ `{Path.GetFileName(aEncAbs)}` 已存在 —— 覆寫之後舊密碼就解不開它，而舊檔回不來");
                aOverwrite = g.Toggle("我要覆寫既有 .enc", false, $"secrets/{m_Gen}/encoverwrite");
            }
            if (!g.Button(aExists ? "加密並覆寫" : "加密", "secrets/encrypt")) return;

            if (aPass.Length == 0) { m_Message = "✗ 沒有輸入密碼"; return; }
            if (aPass != aPass2) { m_Message = "✗ 兩次密碼不一致 —— 沒有寫任何東西"; ClearInputs(g); return; }
            if (aExists && !aOverwrite) { m_Message = "✗ .enc 已存在而沒勾「覆寫」 —— 沒有寫任何東西"; return; }
            try
            {
                byte[] aEnc = SCP_SecretCrypto.Encrypt(File.ReadAllBytes(aTxtAbs), aPass, aHint, aLabel);
                SCP_SecretCrypto.Decrypt(aEnc, aPass);   // 寫之前自己解一次：寫出一顆自己解不開的檔，比不寫更糟
                File.WriteAllBytes(aEncAbs, aEnc);
                m_Message = $"✓ 已加密：`{aEncAbs}`（{aEnc.Length} bytes；已先回解驗證）。⚠ 這顆 .enc 要自己提交";
            }
            catch (Exception e) { m_Message = $"✗ 加密失敗：{e.GetType().Name}: {e.Message} —— 沒有寫任何東西"; }
            ClearInputs(g);
            Load();
        }

        /// <summary>清空所有輸入欄（密碼、提示、label）：換一代 key，舊值留在 host 裡也不會再被讀到。</summary>
        void ClearInputs(SCP_Ui g)
        {
            foreach (string k in new[] { "decpass", "encpass", "encpass2" })
                g.SetField($"secrets/{m_Gen}/{k}{SCP_Ui.MaskedIdSuffix}", "");
            m_Gen++;
        }
    }
}
