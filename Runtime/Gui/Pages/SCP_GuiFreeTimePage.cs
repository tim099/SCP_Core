// 區塊職責：**自由時間的後台頁**（TASK-0360，Tim 2026-10-01「另外需要 FreeTime 的後台設定頁面」）。
// 物理意義：取代 Unity 的 `UCL_FreeTimeAdminPage`，另外多一區它沒有的「場次設定」。四區各有各的真相源，⛔ 本頁不存第二份：
//   ① 場次設定 —— `<資料根>/FreeTime/freetime_settings.json`（SCP_FreeTimeSettings；以前是程式碼常數）
//   ② 活動清單 —— 兩層活動 md 的 frontmatter（共用層在 UCL_Core、專案層在 `<專案根>/docs/FreeTime/Activities`）
//   ③ 活動統計 —— `letters/<P>/profile/freetime_activity_stats.md`（**唯讀**：寫入端只有 start 推場次、pick 記選中）
//   ④ 新增活動 —— 一律建在專案層（共用層屬於 UCL_Core，從專案的頁往那裡加等於替別的專案做決定）
// 數值影響：畫面純讀；寫入只有三個按鈕：設定「儲存」（驗證 → 暫存檔換檔 → 回讀）、活動欄位「套用」（逐欄寫回 md 並回讀）、
//   「建立」（不覆寫既有檔）。三者寫完都重讀磁碟 —— **印 ✓ 不算數，讀回來才算**。
//   設定存檔之後**下一場**（下一次 step=start／next）生效：free-time 每趟呼叫讀一次設定，沒有快取。
// ⚠ immediate mode 的陷阱：宿主會保留使用者打過的輸入 ⇒ 欄位 key 帶**世代號**，
//   重讀／捨棄／寫完就換一代，舊輸入對不上新 id。
// ⚠ 讀不到要說讀不到：設定檔不存在 ＝「用預設值」，讀不了 ＝「讀不了、也用預設值」—— 兩者處置不同，⛔ 不畫成同一句。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.FreeTime;
using SCP.Core.Paths;

namespace SCP.Core.Gui
{
    public sealed class SCP_GuiFreeTimePage : SCP_GuiToolPage
    {
        readonly ISCP_GuiAppContext m_Ctx;
        SCP_PathResolution m_Data = new SCP_PathResolution("", "?", "還沒讀");
        SCP_PathResolution m_Letters = new SCP_PathResolution("", "?", "還沒讀");
        SCP_PathResolution m_Project = new SCP_PathResolution("", "?", "還沒讀");

        // ① 設定
        SCP_FreeTimeSettings? m_DiskSettings;
        SCP_FreeTimeSettings? m_Draft;
        bool m_SettingsFileExists;
        string? m_SettingsError;

        // ② 活動
        List<SCP_FreeTimeActivity> m_Activities = new List<SCP_FreeTimeActivity>();
        readonly List<string> m_ScanWarnings = new List<string>();

        // ③ 統計（Reload 時填好，Draw 不碰磁碟）
        readonly List<(string Persona, SCP_FreeTimeStats Stats)> m_Stats = new List<(string, SCP_FreeTimeStats)>();
        readonly List<string> m_StatsWarnings = new List<string>();

        /// <summary>欄位 key 的世代號 —— 換一代，宿主保留的舊輸入就對不上（見檔頭 ⚠）。</summary>
        int m_Gen;
        string? m_Message;

        public SCP_GuiFreeTimePage(ISCP_GuiAppContext iCtx) : base() { m_Ctx = iCtx; }

        public const string PageKey = "free-time";
        public override string Key { get { return PageKey; } }
        public override string Title { get { return "自由時間"; } }
        public override string? MenuGroup { get { return "設定"; } }

        public override void OnPush() { base.OnPush(); Reload(); }

        static bool Ok(SCP_PathResolution r) => r.Error == null && r.Value.Length > 0;

        void Reload()
        {
            m_Gen++;
            m_Data = m_Ctx.AgentCommandsRoot;
            m_Letters = m_Ctx.LettersRoot;
            m_Project = m_Ctx.ProjectRoot;

            m_DiskSettings = null; m_Draft = null; m_SettingsError = null; m_SettingsFileExists = false;
            if (Ok(m_Data))
            {
                m_DiskSettings = SCP_FreeTimeSettings.Read(m_Data.Value, out m_SettingsFileExists, out m_SettingsError);
                m_Draft = m_DiskSettings.Clone();
            }

            m_ScanWarnings.Clear();
            m_Activities = Ok(m_Project) ? SCP_FreeTimeCatalog.Scan(m_Project.Value, m_ScanWarnings) : new List<SCP_FreeTimeActivity>();

            m_Stats.Clear(); m_StatsWarnings.Clear();
            if (Ok(m_Letters))
            {
                var aLetters = new SCP_LettersRoot(m_Letters.Value);
                foreach (string p in SCP_FreeTimeStatsIO.ListPersonasWithStats(aLetters, m_StatsWarnings))
                    m_Stats.Add((p, SCP_FreeTimeStatsIO.Load(aLetters, p)));
            }
        }

        protected override void TopBarButtons(SCP_Ui g)
        {
            base.TopBarButtons(g);
            OpenFolderButton(g, Ok(m_Data) ? Path.GetDirectoryName(SCP_FreeTimeSettings.PathOf(m_Data.Value)) : null, "freetime/open-dir");
            if (g.Button("重新讀取", "freetime/reload"))
            {
                bool aDirty = IsSettingsDirty();
                Reload();
                m_Message = aDirty ? "・已重讀磁碟 ⇒ **未存的設定變更已丟掉**" : "・已重讀磁碟";
            }
        }

        protected override void DrawContent(SCP_Ui g)
        {
            if (m_Message != null) g.Note(m_Message);
            DrawSettings(g);
            g.Separator();
            DrawActivities(g);
            g.Separator();
            DrawStats(g);
            g.Separator();
            DrawNewActivity(g);
        }

        // ── ① 場次設定 ────────────────────────────────────────────────

        bool IsSettingsDirty()
            => m_DiskSettings != null && m_Draft != null && m_Draft.ToJson() != m_DiskSettings.ToJson();

        void DrawSettings(SCP_Ui g)
        {
            using (g.Fold("① 場次設定（每一場 step=start 讀一次）", "freetime/fold/settings"))
            {
                if (!Ok(m_Data) || m_Draft == null)
                {
                    g.Note("⚠ **資料根量不到** ⇒ 這不是「沒有設定」：" + (m_Data.Error ?? "資料根是空的") + "　（資料根設在「路徑管理」頁）");
                    return;
                }
                g.Note("設定檔：`" + SCP_FreeTimeSettings.PathOf(m_Data.Value) + "`");
                g.Note("・" + m_DiskSettings!.Describe(m_SettingsFileExists, m_SettingsError));
                if (m_SettingsError != null)
                    g.Note("⚠ 設定檔**讀不了**，自由時間此刻照預設值跑 —— 存一次會用畫面上的值覆寫它。");

                string k = $"freetime/{m_Gen}/set";
                m_Draft.PixelsPerSession = IntField(g, "每場發放的限時繪圖券（張）", m_Draft.PixelsPerSession, k + "/pixels");
                m_Draft.VoucherGraceMinutes = IntField(g, "券的到期緩衝（分鐘，截止時刻之後還能用多久）", m_Draft.VoucherGraceMinutes, k + "/grace");
                m_Draft.VoucherHoardThreshold = IntField(g, "囤券門檻（永久繪圖券超過這個數 ⇒ 繪圖活動置頂）", m_Draft.VoucherHoardThreshold, k + "/hoard");
                m_Draft.StarveThreshold = IntField(g, "飢餓門檻（幾場沒被選 ⇒ 算冷落）", m_Draft.StarveThreshold, k + "/starve");
                m_Draft.StarveHoistMax = IntField(g, "飢餓置頂上限（一次最多頂幾項）", m_Draft.StarveHoistMax, k + "/hoist");
                m_Draft.InboxHeadsShown = IntField(g, "配對簡報列出的 inbox 筆數", m_Draft.InboxHeadsShown, k + "/inbox");

                List<string> aInvalid = SCP_FreeTimeSettings.Validate(m_Draft);
                foreach (string w in aInvalid) g.Note("✗ 不能存：" + w);
                bool aDirty = IsSettingsDirty();
                g.Note(aDirty ? "● **有未存的變更**（自由時間此刻仍照磁碟上的設定跑）" : "・畫面與磁碟一致");
                using (g.Row())
                {
                    if (aDirty && aInvalid.Count == 0 && g.Button("儲存", "freetime/settings/save"))
                    {
                        bool aOk = SCP_FreeTimeSettings.Write(m_Data.Value, m_Draft, out string? aErr);
                        Reload();
                        m_Message = aOk ? "✓ 已存檔並讀回 ⇒ 下一場（下一次 step=start／next）起生效" : "✗ 存檔失敗：" + aErr;
                    }
                    if (aDirty && g.Button("捨棄變更", "freetime/settings/discard"))
                    {
                        Reload();
                        m_Message = "・已捨棄未存的設定變更（重讀磁碟）";
                    }
                }
            }
        }

        // 沒有整數欄 ⇒ 文字欄＋解析；打錯字時**保留原值並說出來**，⛔ 不悄悄變成 0（0 對某幾格是合法值，會被當成真的設定）。
        int IntField(SCP_Ui g, string iLabel, int iValue, string iKey)
        {
            string aRaw = g.TextField(iLabel, iValue.ToString(CultureInfo.InvariantCulture), iKey).Trim();
            if (int.TryParse(aRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aParsed)) return aParsed;
            g.Note($"✗ `{aRaw}` 不是整數 ⇒ 這一格維持 {iValue}");
            return iValue;
        }

        // ── ② 活動清單 ────────────────────────────────────────────────

        void DrawActivities(SCP_Ui g)
        {
            int aEnabled = 0;
            foreach (var a in m_Activities) if (a.Enabled) aEnabled++;
            using (g.Fold($"② 活動清單（{m_Activities.Count} 項，啟用 {aEnabled}｜同 id 專案層覆蓋共用層）", "freetime/fold/acts"))
            {
                if (!Ok(m_Project))
                {
                    g.Note("⚠ **專案根量不到** ⇒ 這不是「沒有活動」：" + (m_Project.Error ?? "專案根是空的"));
                    return;
                }
                string aShared = SCP_FreeTimeCatalog.SharedDir(m_Project.Value, out string aWhy) ?? ("（解析不到：" + aWhy + "）");
                g.Note("📦 共用層：`" + aShared + "`");
                g.Note("🏠 專案層：`" + SCP_FreeTimeCatalog.ProjectDir(m_Project.Value) + "`");
                foreach (string w in m_ScanWarnings) g.Note("⚠ " + w);
                if (m_Activities.Count == 0)
                {
                    g.Note("掃不到任何活動 md —— 兩層都是空的（環境異常，不是正常狀態）。");
                    return;
                }
                foreach (var a in m_Activities) DrawActivity(g, a);
            }
        }

        void DrawActivity(SCP_Ui g, SCP_FreeTimeActivity a)
        {
            string k = $"freetime/{m_Gen}/act/{a.Id}";
            string aTitle = $"{(a.IsProjectLayer ? "🏠" : "📦")} {a.Name} `{a.Id}`"
                            + (a.Enabled ? "" : "　⛔ 停用")
                            + (string.IsNullOrEmpty(a.Group) ? "" : $"　（{a.Group}）");
            using (g.Fold(aTitle, k + "/fold", iDefaultOpen: false))
            {
                g.Note("md：`" + a.Path + "`");
                if (!a.IsProjectLayer)
                    g.Note("⚠ 這一項在**共用層**（UCL_Core，跨專案）—— 在這裡改等於改所有專案。只想改本專案 ⇒ 用 ④ 建一個同 id 的專案層活動覆蓋它。");

                bool aEnabled = g.Toggle("啟用", a.Enabled, k + "/enabled");
                string aName = g.TextField("顯示名稱（name）", a.Name, k + "/name");
                string aHow = g.TextField("做法（how）", a.How, k + "/how");
                string aGroup = g.TextField("分組（group，留空＝自成骰面一項）", a.Group, k + "/group");
                string aMinRaw = g.TextField("建議時間（分；剩餘不足 ⇒ 排到骰面尾端並標明）", a.MinMinutes.ToString(CultureInfo.InvariantCulture), k + "/min").Trim();

                g.Note("特殊邏輯（kind）：目前 **" + a.Kind + "**" + (a.KindParseError.Length > 0 ? $"　⚠ md 裡寫的 `{a.KindParseError}` 不認得 ⇒ 當成 Default" : ""));
                string? aKindPick = null;
                using (g.Row())
                {
                    foreach (SCP_FreeTimeActivityKind kind in (SCP_FreeTimeActivityKind[])Enum.GetValues(typeof(SCP_FreeTimeActivityKind)))
                        if (kind != a.Kind && g.Button("改成 " + kind, k + "/kind/" + kind)) aKindPick = kind.ToString();
                }
                if (a.Steps.Count > 0 || a.CmdSteps.Count > 0)
                    g.Note($"代跑步驟（唯讀）：steps=`{string.Join(", ", a.Steps)}`　cmd_steps=`{string.Join(", ", a.CmdSteps)}` —— 這兩欄決定 op=step 能替人跑什麼，⛔ 本頁不給改。");

                // 收集變更的欄位 —— 只寫改過的那幾欄（沒改的不碰，免得把引號格式洗掉）
                var aChanges = new List<(string Field, string Value)>();
                if (aEnabled != a.Enabled) aChanges.Add(("enabled", aEnabled ? "true" : "false"));
                if (aName != a.Name) aChanges.Add(("name", aName));
                if (aHow != a.How) aChanges.Add(("how", aHow));
                if (aGroup != a.Group) aChanges.Add(("group", aGroup));
                bool aMinOk = int.TryParse(aMinRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aMin) && aMin >= 0;
                if (!aMinOk) g.Note($"✗ 建議時間 `{aMinRaw}` 要是 ≥0 的整數 ⇒ 這一格不會寫");
                else if (aMin != a.MinMinutes) aChanges.Add(("min_minutes", aMin.ToString(CultureInfo.InvariantCulture)));

                if (aKindPick != null) { Apply(a, new List<(string, string)> { ("kind", aKindPick) }); return; }
                if (aChanges.Count == 0) return;
                g.Note("● 未套用：" + string.Join("、", aChanges.ConvertAll(c => c.Field)));
                using (g.Row())
                {
                    if (g.Button("套用", k + "/apply")) Apply(a, aChanges);
                    if (g.Button("還原", k + "/revert")) { m_Gen++; m_Message = $"・`{a.Id}` 的未套用變更已還原"; }
                }
            }
        }

        // 逐欄寫回 md；任一欄失敗就停（後面的不寫），並說出哪幾欄已經寫進去 —— 半套的狀態要看得見。
        void Apply(SCP_FreeTimeActivity a, List<(string Field, string Value)> iChanges)
        {
            var aDone = new List<string>();
            foreach (var (aField, aValue) in iChanges)
            {
                if (!SCP_FreeTimeCatalog.WriteField(a.Path, aField, aValue, out string? aErr))
                {
                    Reload();
                    m_Message = $"✗ `{a.Id}`.{aField} 寫入失敗：{aErr}"
                                + (aDone.Count > 0 ? $"　⚠ 在它之前已寫入：{string.Join("、", aDone)}" : "");
                    return;
                }
                aDone.Add(aField);
            }
            Reload();
            m_Message = $"✓ `{a.Id}` 已寫回並讀回：{string.Join("、", aDone)}　→ `{a.Path}`（下一次擲骰起生效）";
        }

        // ── ③ 活動統計（唯讀）─────────────────────────────────────────

        void DrawStats(SCP_Ui g)
        {
            var aS = m_DiskSettings ?? SCP_FreeTimeSettings.Defaults();
            using (g.Fold($"③ 活動統計（{m_Stats.Count} 人有紀錄｜飢餓門檻 {aS.StarveThreshold} 場、一次最多頂 {aS.StarveHoistMax} 項）",
                          "freetime/fold/stats", iDefaultOpen: false))
            {
                g.Note("唯讀 —— 寫入端只有 step=start（推場次）與 op=pick（記選中）。在這裡開「手動改次數」的入口，等於讓那個數字失去它唯一的意義。");
                if (!Ok(m_Letters)) { g.Note("⚠ **信件庫根量不到** ⇒ 這不是「沒人有紀錄」：" + (m_Letters.Error ?? "空的")); return; }
                foreach (string w in m_StatsWarnings) g.Note("⚠ " + w);
                if (m_Stats.Count == 0)
                {
                    // 「沒人有紀錄」≠「大家都沒跑過自由時間」—— 統計是 2026-08-24 才接上的，之前的場次不回溯。
                    g.Note("目前沒有任何 persona 有統計檔。⚠ 這不代表沒人跑過自由時間 —— 統計是 2026-08-24 才接上的，之前的場次不會回溯補算。");
                    return;
                }
                var aIds = new List<string>();
                foreach (var a in m_Activities) if (a.Enabled) aIds.Add(a.Id);
                foreach (var (aPersona, aStats) in m_Stats)
                {
                    using (g.Fold($"{aPersona} — 累計 {aStats.SessionsTotal} 場" + (aStats.UpdatedAt.Length > 0 ? $"（最後更新 {aStats.UpdatedAt}）" : ""),
                                  $"freetime/stats/{aPersona}", iDefaultOpen: false))
                    {
                        if (!aStats.Loaded) { g.Note("⚠ 這一份讀取失敗（下面的數字沒有讀數，不是 0）：" + aStats.LoadError); continue; }
                        var aHoisted = SCP_FreeTimeStatsIO.PickStarved(aStats, aIds, aS, out int aOverflow);
                        // 以**現存活動清單**為主軸，不以統計檔的鍵為主軸 —— 統計檔裡可能留著已刪除活動的紀錄。
                        using (g.Table("", "活動", "做過", "幾場沒選", ""))
                        {
                            foreach (var a in m_Activities)
                            {
                                if (!a.Enabled) continue;
                                int aGap = aStats.Starvation(a.Id);
                                bool aWould = aHoisted.ContainsKey(a.Id);
                                g.TableRow(aWould ? "⭐💤" : (aGap >= aS.StarveThreshold ? "💤" : ""), $"{a.Name} `{a.Id}`",
                                           aStats.Picks(a.Id).ToString(CultureInfo.InvariantCulture),
                                           aGap.ToString(CultureInfo.InvariantCulture), aWould ? "← 下一輪會被置頂" : "");
                            }
                        }
                        if (aOverflow > 0) g.Note($"⚠ 另有 {aOverflow} 項也超過門檻但不會被頂上來（一次最多 {aS.StarveHoistMax} 項）");
                    }
                }
            }
        }

        // ── ④ 新增活動（專案層）───────────────────────────────────────

        void DrawNewActivity(SCP_Ui g)
        {
            using (g.Fold("④ 新增活動（建在專案層）", "freetime/fold/new", iDefaultOpen: false))
            {
                if (!Ok(m_Project)) { g.Note("⚠ **專案根量不到** ⇒ 沒有地方建：" + (m_Project.Error ?? "空的")); return; }
                g.Note("id 用 kebab-case，會成為檔名與骰面識別；同 id 會覆蓋共用層同名活動。只生 frontmatter ＋ 一段待補正文 —— GUI 生得出欄位，生不出「這個活動是什麼」。");
                string k = $"freetime/{m_Gen}/new";
                string aId = g.TextField("id", "", k + "/id").Trim();
                string aName = g.TextField("顯示名稱（留空＝用 id）", "", k + "/name").Trim();
                string aHow = g.TextField("做法（how）", "", k + "/how").Trim();
                string aGroup = g.TextField("分組（留空＝不分組）", "", k + "/group").Trim();
                string aMinRaw = g.TextField("建議時間（分）", "0", k + "/min").Trim();
                if (g.Button("建立", k + "/create"))
                {
                    if (!int.TryParse(aMinRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aMin) || aMin < 0)
                    { m_Message = $"✗ 建議時間需為 ≥0 的整數（got `{aMinRaw}`）"; return; }
                    bool aOk = SCP_FreeTimeCatalog.CreateProjectActivity(m_Project.Value, aId, aName.Length > 0 ? aName : aId,
                                                                       aHow, aGroup, aMin, out string aPath, out string? aErr);
                    Reload();
                    m_Message = aOk ? $"✓ 已建立並讀回：`{aPath}`（正文待補）" : "✗ 建立失敗：" + aErr;
                }
            }
        }
    }
}
