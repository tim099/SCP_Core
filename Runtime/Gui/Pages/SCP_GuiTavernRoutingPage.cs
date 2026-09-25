// 區塊職責：**酒館路由判準與發薪規則的後台頁**（TASK-0298，TASK-0295 ②）。
// 物理意義：Tim 2026-09-25「這些設定也都需要 Senate 版本的後台頁面」。判準的真相源在資料根
//           （`ChatTavern/tavern_routing.json`，SCP_TavernRouting）；本頁只是它的編輯面，⛔ 不存第二份。
//           發薪規則的金額與條件是程式碼常數（SCP_TavernPayroll）⇒ 本頁**唯讀列出＋試算**，不給改。
// 數值影響：畫面純讀；唯一的寫入是按下「儲存」那一下（SCP_TavernRouting.Write：驗證 → 暫存檔換檔 → 回讀）。
//   存檔之後發薪判斷與 Discord 鏡像**下一則訊息就照新判準走**（兩者每次都重讀檔，沒有快取）。
// ⚠ immediate mode 的陷阱（本頁最容易騙人的一格）：宿主會保留使用者改過的勾選與輸入，
//   而 Toggle 沒有 SetToggle ⇒ 「捨棄變更」之後下一幀草稿又被改回去，畫面看起來捨棄了、其實沒有。
//   ⇒ 欄位 key 帶**世代號**：重讀／捨棄／搬動／刪除就換一代，舊的輸入值沒有 id 可以對上。
// ⚠ 判準檔讀不到時說「讀不到」，⛔ 不畫成「沒有任何 group」—— 那兩者的處置相反（前者要修檔，後者要匯入）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace SCP.Core.Gui
{
    public sealed class SCP_GuiTavernRoutingPage : SCP_GuiToolPage
    {
        readonly ISCP_GuiAppContext m_Ctx;
        SCP_PathResolution m_Root = new SCP_PathResolution("", "?", "還沒讀");

        /// <summary>上一次從磁碟讀到的結果（比對「有沒有未存的變更」用）。</summary>
        SCP_TavernRoutingRead? m_Disk;
        /// <summary>畫面上的草稿（編輯的對象）。null ＝ 磁碟讀不到，沒有東西可編。</summary>
        List<SCP_TavernRouteGroup>? m_Draft;
        /// <summary>欄位 key 的世代號 —— 換一代，宿主保留的舊輸入就對不上（見檔頭 ⚠）。</summary>
        int m_Gen;
        /// <summary>刪除的兩段式：已上膛的 group id。</summary>
        string? m_ArmedDelete;
        string? m_Message;
        List<string>? m_TrialLines;

        public SCP_GuiTavernRoutingPage(ISCP_GuiAppContext iCtx) : base() { m_Ctx = iCtx; }

        public const string PageKey = "tavern-routing";
        public override string Key { get { return PageKey; } }
        public override string Title { get { return "酒館路由與發薪"; } }
        public override string? MenuGroup { get { return "設定"; } }

        public override void OnPush() { base.OnPush(); Load(); }

        bool HasRoot => m_Root.Error == null && m_Root.Value.Length > 0;

        void Load()
        {
            m_Root = m_Ctx.AgentCommandsRoot;
            m_Gen++;
            m_ArmedDelete = null;
            if (!HasRoot) { m_Disk = null; m_Draft = null; return; }
            m_Disk = SCP_TavernRouting.Read(m_Root.Value);
            m_Draft = m_Disk.Ok ? m_Disk.Groups.Select(g => g.Clone()).ToList() : null;
        }

        bool IsDirty()
        {
            if (m_Disk == null || !m_Disk.Ok || m_Draft == null) return false;
            return SCP_TavernRouting.ToJson(m_Draft).ToJson(false) != SCP_TavernRouting.ToJson(m_Disk.Groups).ToJson(false);
        }

        protected override void TopBarButtons(SCP_Ui g)
        {
            base.TopBarButtons(g);
            if (g.Button("重新讀取", "routing/reload"))
            {
                bool aDirty = IsDirty();
                Load();
                m_Message = aDirty ? "・已重讀磁碟 ⇒ **未存的變更已丟掉**" : "・已重讀磁碟";
            }
        }

        protected override void DrawContent(SCP_Ui g)
        {
            if (!HasRoot)
            {
                g.Note("⚠ 本頁**沒有資料來源** ⇒ 這不是「沒有任何 group」，是「量不到」。原因："
                       + (string.IsNullOrEmpty(m_Root.Error) ? "資料根是空的（沒有人填過）" : m_Root.Error));
                g.Note("資料根設在「路徑管理」頁（`senate ui --page paths`），本頁不存路徑。");
                if (m_Message != null) g.Note(m_Message);
                return;
            }
            g.Note("判準檔：`" + SCP_TavernRouting.PathOf(m_Root.Value) + "`（真相源；⛔ 不含 webhook URL）");
            if (m_Disk != null && !m_Disk.Ok)
            {
                g.Note("⚠ **判準檔讀不到** ⇒ 這不是「沒有任何 group」：" + m_Disk.Error);
                if (m_Disk.Missing)
                    g.Note("第一次要從 Unity asset 匯入：`senate cmd tavern-routing --arg data_root=<資料根> --arg op=import_unity --arg unity_dir=<asset 目錄> --arg confirm=1`");
                g.Note("發薪判斷此刻讀不到判準 ⇒ **所有底薪都不會發**（它會在每則訊息印警告）。");
                if (m_Message != null) g.Note(m_Message);
                return;
            }
            if (m_Draft == null) return;

            DrawGroups(g);
            g.Separator();
            DrawSaveBar(g);
            if (m_Message != null) g.Note(m_Message);
            g.Separator();
            DrawDraftResolve(g);
            g.Separator();
            DrawPayroll(g);
        }

        // ── 路由判準（可編輯）─────────────────────────────────────────

        void DrawGroups(SCP_Ui g)
        {
            var aDraft = m_Draft!;
            g.Title("路由 group（由上往下比對，第一個命中的勝出；都沒命中 ⇒ 第一個 enabled 的 default）");
            for (int i = 0; i < aDraft.Count; i++)
            {
                SCP_TavernRouteGroup r = aDraft[i];
                string k = $"routing/{m_Gen}/{i}";
                using (g.Box($"{i + 1}. {r.Id}" + (r.IsPaidPost ? "　【計酬】" : "") + (r.IsDefault ? "　default" : ""), k + "/box"))
                {
                    using (g.Row())
                    {
                        r.Enabled = g.Toggle("啟用", r.Enabled, k + "/enabled");
                        r.IsDefault = g.Toggle("default", r.IsDefault, k + "/default");
                        r.IsPaidPost = g.Toggle("計酬（發文底薪）", r.IsPaidPost, k + "/paid");
                        r.Exclusive = g.Toggle("exclusive（廣播只送這裡）", r.Exclusive, k + "/exclusive");
                    }
                    string aCats = g.TextField("categories（逗號分隔）", string.Join(", ", r.Categories), k + "/cats");
                    r.Categories = aCats.Split(',').Select(c => c.Trim()).Where(c => c.Length > 0).ToList();
                    r.Description = g.TextField("說明", r.Description, k + "/desc");
                    g.Note($"webhook 來源：env `{(r.WebhookEnvVar.Length > 0 ? r.WebhookEnvVar : "—")}`／file `{(r.WebhookFile.Length > 0 ? r.WebhookFile : "—")}`（URL 本身不在這裡）");
                    using (g.Row())
                    {
                        if (i > 0 && g.Button("上移", k + "/up")) { Swap(i, i - 1); return; }
                        if (i < aDraft.Count - 1 && g.Button("下移", k + "/down")) { Swap(i, i + 1); return; }
                        if (m_ArmedDelete != r.Id)
                        {
                            if (g.Button("刪除…", k + "/del")) { m_ArmedDelete = r.Id; m_Message = null; }
                        }
                        else
                        {
                            if (g.Button($"確認刪除 {r.Id}（存檔後才生效）", k + "/del/yes"))
                            {
                                aDraft.RemoveAt(i); m_ArmedDelete = null; m_Gen++;
                                m_Message = $"・已從草稿刪掉 `{r.Id}` —— 還沒存檔";
                                return;
                            }
                            if (g.Button("取消", k + "/del/no")) m_ArmedDelete = null;
                        }
                    }
                }
            }
            using (g.Row())
            {
                string aNewId = g.TextField("新 group id", "", $"routing/{m_Gen}/newid").Trim();
                if (g.Button("新增", $"routing/{m_Gen}/add"))
                {
                    if (aNewId.Length == 0) m_Message = "✗ 新 group 要有 id";
                    else if (aDraft.Any(x => x.Id == aNewId)) m_Message = $"✗ id `{aNewId}` 已經有了";
                    else
                    {
                        aDraft.Add(new SCP_TavernRouteGroup { Id = aNewId });
                        m_Gen++;
                        m_Message = $"・已在草稿最後加上 `{aNewId}`（預設：啟用、不計酬、沒有 category）—— 還沒存檔";
                    }
                }
            }
        }

        void Swap(int a, int b)
        {
            var d = m_Draft!;
            (d[a], d[b]) = (d[b], d[a]);
            m_Gen++;
            m_Message = "・已調整順序 —— 還沒存檔（順序就是比對順序）";
        }

        void DrawSaveBar(SCP_Ui g)
        {
            var aDraft = m_Draft!;
            string? aInvalid = SCP_TavernRouting.Validate(aDraft);
            foreach (string w in SCP_TavernRouting.Diagnose(aDraft)) g.Note("⚠ " + w);
            if (aInvalid != null) g.Note("✗ 不能存：" + aInvalid);
            bool aDirty = IsDirty();
            g.Note(aDirty ? "● **有未存的變更**（發薪與廣播此刻仍照磁碟上的判準走）" : "・畫面與磁碟一致");
            using (g.Row())
            {
                if (aDirty && aInvalid == null && g.Button("儲存", "routing/save"))
                {
                    var (aOk, aMsg) = SCP_TavernRouting.Write(m_Root.Value, aDraft);
                    Load();
                    m_Message = aOk ? aMsg + "　⇒ 下一則訊息起照新判準發薪與廣播" : aMsg;
                }
                if (aDirty && g.Button("捨棄變更", "routing/discard"))
                {
                    Load();
                    m_Message = "・已捨棄未存的變更（重讀磁碟）";
                }
            }
        }

        void DrawDraftResolve(SCP_Ui g)
        {
            g.Title("試解析（用**畫面上的草稿**，還沒存也算）");
            string aCat = g.TextField("category（空＝沒帶）", "", "routing/try/cat");
            SCP_TavernRouteGroup? r = SCP_TavernRouting.ResolveTargetGroup(m_Draft!, aCat);
            g.Note(r == null
                ? $"⇒ `{SCP_TavernRouting.Normalize(aCat)}` **找不到 group**（沒命中、也沒有 enabled 的 default）⇒ 不計酬"
                : $"⇒ `{SCP_TavernRouting.Normalize(aCat)}` 落到 `{r.Id}`　計酬＝{(r.IsPaidPost ? "是" : "否")}");
        }

        // ── 發薪規則（唯讀＋試算）───────────────────────────────────────

        void DrawPayroll(SCP_Ui g)
        {
            g.Title("發薪規則（唯讀 —— 金額與條件是程式碼常數，在 SCP_TavernPayroll）");
            using (g.Table("規則", "條件", "金額", "給誰"))
            {
                g.TableRow("A 底薪 work_post", "真實 agent、非出資方、非工具廣播、落在計酬 group、persona 解析得到帳號",
                           "+" + SCP_TavernPayroll.WorkPostReward, "發言者的 persona 帳號");
                g.TableRow("B token_parse", "只認 Tim；`@對象 N token`／`支付 N token`／`N token` 三層",
                           "±N（單路上限 " + SCP_TavernPayroll.TokenParseMaxPerMessage + "）", "對象／扣 Tim／Tim");
                g.TableRow("D commit", "tag=commit 且帶 meta.sha、非出資方", "+" + SCP_TavernPayroll.CommitPostReward, "發言者");
                g.TableRow("E reading_note", "tag=reading-note、非出資方", "+" + SCP_TavernPayroll.ReadingNoteReward, "發言者");
            }
            g.Note("發薪掛在**寫入端**：server 模式由 Senate Server 寫完就付，editor 模式由 Editor 寫完就付；入帳一律交銀行那顆 Server。");
            g.Note("冪等鍵是這則訊息確定的鍵（`<kind>_<room>_<seq>`）⇒ 同一則不會付兩次。");

            g.Title("試算（⚠ 用的是**已存檔**的判準，不是畫面上的草稿）");
            string aSender = g.TextField("sender_id", "Zeta", "pay/try/sender");
            string aPersona = g.TextField("sender_persona（空＝匿名）", "summit", "pay/try/persona");
            string aCat = g.TextField("category", "", "pay/try/cat");
            string aTag = g.TextField("tag（commit／reading-note…）", "", "pay/try/tag");
            string aSha = g.TextField("meta.sha（tag=commit 時）", "", "pay/try/sha");
            string aBody = g.TextField("內文（B 規則看它）", "", "pay/try/body");
            if (g.Button("試算", "pay/try/run"))
            {
                var aMeta = new Dictionary<string, string>();
                if (aCat.Trim().Length > 0) aMeta["category"] = aCat.Trim();
                if (aTag.Trim().Length > 0) aMeta["tag"] = aTag.Trim();
                if (aSha.Trim().Length > 0) aMeta["sha"] = aSha.Trim();
                SCP_TavernPayPlan p = SCP_TavernPayroll.Plan(m_Root.Value, new SCP_TavernPayInput
                {
                    Room = "(試算)", Seq = 1, SenderId = aSender.Trim(), SenderPersona = aPersona.Trim(), Body = aBody, Meta = aMeta,
                });
                var aLines = new List<string>();
                if (p.Items.Count == 0) aLines.Add("⇒ **這則不會發任何錢**");
                foreach (SCP_TavernPayItem i in p.Items) aLines.Add("＋ " + SCP_TavernPayroll.Describe(i));
                foreach (string w in p.Warnings) aLines.Add("⚠ " + w);
                foreach (string n in p.Notes) aLines.Add(n);
                aLines.Add("（試算零寫入 —— 沒有任何帳被動）");
                m_TrialLines = aLines;
            }
            if (m_TrialLines != null) foreach (string l in m_TrialLines) g.Note(l);
        }
    }
}
