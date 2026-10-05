// 區塊職責：**酒館顯示設定**的後台頁 —— 未讀訊息內文截斷（一般訊息／@ 到自己的訊息）。
// 物理意義：真相源是 `<資料根>/ChatTavern/render_settings.json`（SCP_TavernRenderSettings），本頁只是它的編輯器。
//          讀的人是叮 catchup 與自由時間的配對簡報；改完**下一次**它們跑的時候生效（每次呼叫讀一次，沒有快取）。
// 數值影響：畫面純讀；唯一的寫入是「儲存」（驗證 → 暫存檔換檔 → 回讀），寫完重讀磁碟 —— 印 ✓ 不算數，讀回來才算。
// ⚠ immediate mode 的陷阱：宿主會保留使用者打過的輸入 ⇒ 欄位 key 帶**世代號**，重讀／捨棄／寫完就換一代。
// ⚠ 讀不到要說讀不到：設定檔不存在 ＝「沒設過、用預設值」；讀不了 ＝「不知道設了什麼、此刻照預設跑」—— ⛔ 不畫成同一句。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace SCP.Core.Gui
{
    public sealed class SCP_GuiTavernSettingsPage : SCP_GuiToolPage
    {
        readonly ISCP_GuiAppContext m_Ctx;
        SCP_PathResolution m_Data = new SCP_PathResolution("", "?", "還沒讀");

        SCP_TavernRenderSettings? m_Disk;
        SCP_TavernRenderSettings? m_Draft;
        bool m_FileExists;
        string? m_ReadError;

        /// <summary>欄位 key 的世代號 —— 換一代，宿主保留的舊輸入就對不上（見檔頭 ⚠）。</summary>
        int m_Gen;
        string? m_Message;

        public SCP_GuiTavernSettingsPage(ISCP_GuiAppContext iCtx) : base() { m_Ctx = iCtx; }

        public const string PageKey = "tavern-settings";
        public override string Key { get { return PageKey; } }
        public override string Title { get { return "酒館顯示"; } }
        public override string? MenuGroup { get { return "設定"; } }

        public override void OnPush() { base.OnPush(); Reload(); }

        bool HasData => m_Data.Error == null && m_Data.Value.Length > 0;

        void Reload()
        {
            m_Gen++;
            m_Data = m_Ctx.AgentCommandsRoot;
            m_Disk = null; m_Draft = null; m_ReadError = null; m_FileExists = false;
            if (!HasData) return;
            m_Disk = SCP_TavernRenderSettings.Read(m_Data.Value, out m_FileExists, out m_ReadError);
            m_Draft = m_Disk.Clone();
        }

        bool IsDirty() => m_Disk != null && m_Draft != null && !m_Disk.SameAs(m_Draft);

        protected override void TopBarButtons(SCP_Ui g)
        {
            base.TopBarButtons(g);
            OpenFolderButton(g, HasData ? Path.GetDirectoryName(SCP_TavernRenderSettings.PathOf(m_Data.Value)) : null, "tavern-settings/open-dir");
            if (g.Button("重新讀取", "tavern-settings/reload"))
            {
                bool aDirty = IsDirty();
                Reload();
                m_Message = aDirty ? "・已重讀磁碟 ⇒ **未存的變更已丟掉**" : "・已重讀磁碟";
            }
        }

        protected override void DrawContent(SCP_Ui g)
        {
            if (m_Message != null) g.Note(m_Message);
            if (!HasData || m_Disk == null || m_Draft == null)
            {
                g.Note("⚠ **資料根量不到** ⇒ 這不是「沒有設定」：" + (m_Data.Error ?? "資料根是空的") + "　（資料根設在「路徑管理」頁）");
                return;
            }

            g.Note("設定檔：`" + SCP_TavernRenderSettings.PathOf(m_Data.Value) + "`");
            if (m_ReadError != null)
                g.Note("⚠ 設定檔**讀不了或有值不合法**：" + m_ReadError + "　—— 存一次會用畫面上的值覆寫這兩格。");
            else if (!m_FileExists)
                g.Note("・設定檔不存在 ⇒ 目前用預設值（一般 " + SCP_TavernRenderSettings.DefaultBodyClip
                       + "、@ 到自己 " + SCP_TavernRenderSettings.DefaultBodyClipMentioned + "）。存一次就會建立。");

            g.Note($"未讀訊息內文**顯示**到幾個字元（只截顯示、不改原文）。0 ＝ 不截斷；其餘 {SCP_TavernRenderSettings.MinBodyClip}–{SCP_TavernRenderSettings.MaxBodyClip}。"
                   + "用在叮 catchup 與自由時間的配對簡報；改完下一次它們跑的時候生效。");

            string k = $"tavern-settings/{m_Gen}";
            m_Draft.BodyClip = IntField(g, "一般未讀訊息", m_Draft.BodyClip, k + "/clip");
            m_Draft.BodyClipMentioned = IntField(g, "@ 到自己的訊息", m_Draft.BodyClipMentioned, k + "/clip-mention");

            List<string> aInvalid = SCP_TavernRenderSettings.Validate(m_Draft);
            foreach (string w in aInvalid) g.Note("✗ 不能存：" + w);
            bool aDirty = IsDirty();
            g.Note(aDirty ? "● **有未存的變更**（此刻仍照磁碟上的設定跑）" : "・畫面與磁碟一致");
            using (g.Row())
            {
                if (aDirty && aInvalid.Count == 0 && g.Button("儲存", "tavern-settings/save"))
                {
                    bool aOk = SCP_TavernRenderSettings.Write(m_Data.Value, m_Draft, out string? aErr);
                    Reload();
                    m_Message = aOk ? "✓ 已存檔並讀回 ⇒ 下一次 catchup／配對簡報起生效" : "✗ 存檔失敗：" + aErr;
                }
                if (aDirty && g.Button("捨棄變更", "tavern-settings/discard"))
                {
                    Reload();
                    m_Message = "・已捨棄未存的變更（重讀磁碟）";
                }
                if (g.Button("全部回預設", "tavern-settings/defaults"))
                {
                    m_Draft = new SCP_TavernRenderSettings();
                    m_Gen++;
                    m_Message = "・畫面已填回預設值（還沒存）";
                }
            }
        }

        // 沒有整數欄 ⇒ 文字欄＋解析；打錯字時**保留原值並說出來**，⛔ 不悄悄變成 0（0 在這裡是「不截斷」，會被當成真的設定）。
        int IntField(SCP_Ui g, string iLabel, int iValue, string iKey)
        {
            string aRaw = g.TextField(iLabel, iValue.ToString(CultureInfo.InvariantCulture), iKey).Trim();
            if (int.TryParse(aRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aParsed)) return aParsed;
            g.Note($"✗ `{aRaw}` 不是整數 ⇒ 這一格維持 {iValue}");
            return iValue;
        }
    }
}
