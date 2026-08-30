// 區塊職責：**Skill 與入口檔的安裝管理頁** —— 把「裝 skill 給 AI 用」做成看得見的一頁。
// 物理意義：概念取自 Unity 端的 UCL_AgentSkillManagerPage，但三個地方不一樣，都不是風格問題：
//           ① **安裝流程全是 C#**（不 spawn python）—— 這邊沒有 Unity 也沒有 queue
//           ② **安裝對象是一個選擇**：預設是宿主自己（在這裡跑的 agent 需要的是這個 repo 的 skill），
//              被管理的專案與自訂路徑是額外選項
//           ③ **一次只看一家 agent 的細節**：三家全展開時，畫面上八成的字跟當下要做的事無關
// 數值影響：狀態列全部是**現讀**（列目錄 ＋ 逐檔比 bytes），按鈕才會寫。
//
// ⚠ 這一頁可能寫進**別人的工作區** ⇒ 兩格護欄：
//   · Unity Editor 在跑的專案 → 畫面警告（動它的 index 會撞 AssetDatabase import）
//   · 入口檔的異常狀態（Duplicated / MarkerBroken）→ 只顯示、不給按鈕（force 也不放行）
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Entry;
using SCP.Core.Prefs;
using SCP.Core.Skills;

namespace SCP.Core.Gui
{
    public sealed class SCP_GuiSkillManagerPage : SCP_GuiToolPage
    {
        readonly ISCP_GuiAppContext m_Ctx;

        /// <summary>上一次動作的結果（成功也要有話說 —— 按了沒事與按了成功不得同形）。</summary>
        string? m_Message;

        public SCP_GuiSkillManagerPage(ISCP_GuiAppContext iCtx) : base() { m_Ctx = iCtx; }

        public override string Key { get { return PageKey; } }
        public const string PageKey = "skills";

        public override string Title { get { return "Skill 安裝管理"; } }
        public override string? MenuGroup { get { return "設定"; } }

        string SkillsRoot { get { return Norm(m_Ctx.CoreRoot) + "/Skills~"; } }

        /// <summary>下拉裡代表「自訂路徑」的哨兵值。⚠ 它不是路徑，選到它要另外讀輸入框。</summary>
        const string kCustomSentinel = "__custom__";

        static readonly SCP_PrefKey<string> KeyInstallRoot = SCP_PrefKey.String("skills", "installRoot", "");
        static readonly SCP_PrefKey<string> KeyCustomRoot = SCP_PrefKey.String("skills", "customRoot", "");
        static readonly SCP_PrefKey<string> KeyAgent = SCP_PrefKey.String("skills", "agent", "");

        // ── 主體 ──────────────────────────────────────────────────

        protected override void DrawContent(SCP_Ui g)
        {
            DrawSource(g);
            g.Separator();

            SCP_GuiProjectRef? aProj = PickInstallRoot(g);
            if (aProj == null) { Tail(g); return; }

            g.Space();
            DrawInstallAll(g, aProj);

            g.Space();
            DrawEntryDocs(g, aProj);

            g.Space();
            DrawAgentSection(g, aProj);

            Tail(g);
        }

        void Tail(SCP_Ui g) { if (m_Message != null) { g.Space(); g.Note(m_Message); } }

        // ── 來源 ──────────────────────────────────────────────────

        void DrawSource(SCP_Ui g)
        {
            using (g.Box("來源"))
            {
                g.Label($"SCP_Core：{m_Ctx.CoreRoot}");
                if (!SCP_SkillSource.RootExists(SkillsRoot))
                {
                    // 根不存在 ≠ 根在但沒有 skill —— 兩者不得同形
                    g.Note($"⚠ 找不到 `Skills~`：{SkillsRoot} —— **這不是「沒有 skill」，是讀不到來源**。");
                    return;
                }
                List<string> aSkills = SCP_SkillSource.Discover(SkillsRoot);
                g.Label($"Skills~：{aSkills.Count} 個（{string.Join(" / ", aSkills)}）");
                if (aSkills.Count == 0)
                    g.Note("・目錄在，但一個算數的 skill 都沒有（判準：不以 `_` `.` 開頭、不以 `~` 結尾、且含 SKILL.md）");
            }
        }

        // ── 安裝對象 ──────────────────────────────────────────────
        // 🩸 第一版沒有這一段 —— 我照 UCL 那頁的模型假設「安裝對象＝被管理的專案」，
        //   於是預設裝到 Bar 去了（Tim 2026-08-30 按下安裝之後才現形）。
        //   那個模型的前提是**頁面住在它要裝的那個專案裡**，而這裡是外部工具：
        //   在這裡跑的 agent 需要的是**這個 repo** 的 skill。
        //   ⇒ 預設是宿主自己；被管理的專案與自訂路徑是額外選項。

        SCP_GuiProjectRef? PickInstallRoot(SCP_Ui g)
        {
            SCP_GuiProjectRef aHost = m_Ctx.HostProject;

            var aOptions = new List<SCP_GuiOption>();
            aOptions.Add(new SCP_GuiOption(aHost.Root, "★ " + aHost.Name + "（" + aHost.Root + "）"));
            foreach (SCP_GuiProjectRef p in m_Ctx.ManagedProjects)
            {
                if (p.Root == aHost.Root) continue;              // 同一個不列兩次
                aOptions.Add(new SCP_GuiOption(p.Root, p.Name + "（" + p.Root + "）"));
            }
            aOptions.Add(new SCP_GuiOption(kCustomSentinel, "（自訂路徑…）"));

            string aSaved = m_Ctx.Prefs.Get(KeyInstallRoot);
            string aDefault = aSaved.Length > 0 ? aSaved : aHost.Root;
            string aPick = g.Dropdown("安裝到", aOptions, aDefault, "skills/root");

            // 換了才寫 —— 每幀寫會把設定檔攪成每秒一次的 diff
            string aWant = aPick == aHost.Root ? "" : aPick;
            if (aWant != aSaved) m_Ctx.Prefs.Write(KeyInstallRoot, aWant);

            if (aPick == kCustomSentinel) return PickCustom(g);

            SCP_GuiProjectRef? aFound = aPick == aHost.Root ? aHost : Find(m_Ctx.ManagedProjects, aPick);
            if (aFound == null)
            {
                // 上次選的專案可能已從清單移除 ⇒ 說出來，不要靜靜換一個
                g.Note($"⚠ 上次選的「{aPick}」已經不在清單裡 —— 請重選。");
                return null;
            }

            // Editor 心跳只有被管理的 Unity 專案量得到；宿主自己不是 Unity 專案 ⇒ 不畫那行
            if (aFound.EditorRunning == true)
                g.Note("⚠ 這個專案的 Unity Editor **正在跑** —— 現在寫檔會跟它的 AssetDatabase import 撞上。"
                       + "建議關掉 Editor 再裝，或裝完在 Editor 裡 Reimport。");
            return aFound;
        }

        SCP_GuiProjectRef? PickCustom(SCP_Ui g)
        {
            string aOld = m_Ctx.Prefs.Get(KeyCustomRoot);
            string aNew = g.TextField("自訂路徑（該 repo 的 git root）", aOld, "skills/customroot");
            if (aNew != aOld) m_Ctx.Prefs.Write(KeyCustomRoot, aNew);

            string aRoot = Norm(aNew.Trim());
            // 「沒填」「填了但不存在」「填了而且在」是三態，不可以壓成兩態
            if (aRoot.Length == 0) { g.Note("・還沒填路徑。"); return null; }
            if (!Directory.Exists(aRoot)) { g.Note($"⚠ 這個目錄不存在：{aRoot}"); return null; }
            if (!Directory.Exists(Path.Combine(aRoot, ".git")) && !File.Exists(Path.Combine(aRoot, ".git")))
                g.Note("・看起來不是 git root（`.git` 不在）—— 還是可以裝，但那些檔不會進版控。");
            return new SCP_GuiProjectRef("(自訂)", aRoot);
        }

        // ── 一鍵全裝 ──────────────────────────────────────────────
        // 區塊職責：三家 agent 的 skill ＋ 全部入口檔，一次做完。
        // ⚠ 它**不是 force**：需要人決定的狀態（被手改／重複區塊／marker 壞掉）一律跳過並列出來。
        //   一鍵最容易長成的壞形狀就是「它替你決定了那些你該自己決定的事」。

        void DrawInstallAll(SCP_Ui g, SCP_GuiProjectRef iProj)
        {
            using (g.Box("一鍵"))
            {
                if (g.Button("🚀 安裝全部（三家 agent 的 skill ＋ 入口檔 .md）", "skills/all"))
                    m_Message = InstallEverything(iProj);
                g.Note("・需要人決定的狀態（被手改／重複區塊／marker 壞掉）會被**跳過並列出來** —— 一鍵不是強制覆寫。");
            }
        }

        string InstallEverything(SCP_GuiProjectRef iProj)
        {
            var aSb = new StringBuilder();
            int aOk = 0, aSkip = 0, aFail = 0;
            var aNotes = new List<string>();

            // ① 三家 agent 的 skill
            List<string> aSkills = SCP_SkillSource.Discover(SkillsRoot);
            foreach (SCP_SkillTarget t in SCP_SkillTarget.All)
                foreach (string s in aSkills)
                {
                    SCP_SkillSyncResult r = SCP_SkillInstall.Sync(SkillsRoot, t, iProj.Root, s);
                    if (r.Ok) aOk++; else { aFail++; aNotes.Add($"{t.Id}/{s}：{r.Message}"); }
                }

            // ② 入口檔
            SCP_EntryManifest aMan = SCP_EntryManifest.Load(m_Ctx.CoreRoot);
            string aRel = CoreRelativeTo(iProj.Root);
            if (!aMan.Loaded) { aFail++; aNotes.Add("入口檔：manifest 讀不到"); }
            else if (aRel.Length == 0) { aFail++; aNotes.Add("入口檔：跨磁碟，算不出可攜的相對路徑"); }
            else
            {
                foreach (SCP_EntrySpec spec in aMan.Entries)
                {
                    string aDst = spec.DestinationPath(iProj.Root);
                    string aManaged;
                    try { aManaged = SCP_EntryManifest.ReadSource(spec, m_Ctx.CoreRoot, aRel); }
                    catch (Exception e) { aFail++; aNotes.Add($"{spec.Target}：讀不到來源（{e.Message}）"); continue; }

                    if (spec.Mode == SCP_EntryMode.Full)
                    {
                        string aMsg = WriteFull(aDst, aManaged);
                        if (aMsg.StartsWith("✓", StringComparison.Ordinal)) aOk++;
                        else { aFail++; aNotes.Add(aMsg); }
                        continue;
                    }

                    SCP_EntryInstallResult r = SCP_EntryDocInstaller.Install(aDst, aManaged, spec.Target,
                                                                             spec.SourceRelative);
                    if (r.Ok) aOk++;
                    else { aSkip++; aNotes.Add($"{spec.Target}（{r.StateBefore}）：{r.Message}"); }
                }
            }

            // 「成功 N」旁邊一定要有「沒做 N」—— 只報成功數是這族最常見的說謊法
            aSb.Append($"一鍵安裝到 {iProj.Root}：成功 {aOk}／需人決定 {aSkip}／失敗 {aFail}");
            foreach (string s in aNotes) aSb.Append("\n　・").Append(s);
            return aSb.ToString();
        }

        // ── 入口檔 ────────────────────────────────────────────────

        void DrawEntryDocs(SCP_Ui g, SCP_GuiProjectRef iProj)
        {
            using (var aFold = g.Fold("Agent 入口檔（.md）", "skills/entry"))
            {
                if (!aFold.Open) return;

                SCP_EntryManifest aMan = SCP_EntryManifest.Load(m_Ctx.CoreRoot);
                foreach (string p in aMan.Problems) g.Note($"⚠ manifest：{p}");
                if (!aMan.Loaded) return;

                string aRel = CoreRelativeTo(iProj.Root);
                if (aRel.Length == 0)
                {
                    g.Note("⚠ SCP_Core 與這個目標不在同一個磁碟 —— 算不出可攜的相對路徑，本區塊停用。");
                    return;
                }

                foreach (SCP_EntrySpec aSpec in aMan.Entries)
                {
                    string aDst = aSpec.DestinationPath(iProj.Root);
                    string aManaged;
                    try { aManaged = SCP_EntryManifest.ReadSource(aSpec, m_Ctx.CoreRoot, aRel); }
                    catch (Exception e) { g.Note($"⚠ `{aSpec.Target}` 讀不到來源：{e.Message}"); continue; }

                    if (aSpec.Mode == SCP_EntryMode.Full) { DrawEntryFull(g, iProj, aSpec, aDst, aManaged); continue; }

                    SCP_EntryParse aParse = SCP_EntryDocInstaller.Inspect(aDst, aManaged);
                    using (g.Row())
                    {
                        g.Label($"{aSpec.Target}　{StateMark(aParse.State)}　{aSpec.Destination}");

                        // ⛔ 這兩態連 force 都不給按鈕：那是「我不知道該動哪裡」，
                        //    硬做會留下另一個還在生效的區塊，而畫面會顯示成功。
                        if (aParse.State == SCP_EntryState.Duplicated || aParse.State == SCP_EntryState.MarkerBroken)
                            g.Label("（要人工處理）");
                        else if (aParse.State == SCP_EntryState.LocalEdit)
                        {
                            if (g.Button("強制覆寫", "skills/entry/force/" + aSpec.Target))
                                m_Message = At(iProj, SCP_EntryDocInstaller.Install(aDst, aManaged, aSpec.Target,
                                                                          aSpec.SourceRelative, iForce: true).Message);
                        }
                        else if (aParse.State != SCP_EntryState.Synced)
                        {
                            string aVerb = aParse.State == SCP_EntryState.NeedsMigration ? "遷移" : "安裝";
                            if (g.Button(aVerb, "skills/entry/install/" + aSpec.Target))
                                m_Message = At(iProj, SCP_EntryDocInstaller.Install(aDst, aManaged, aSpec.Target,
                                                                          aSpec.SourceRelative).Message);
                        }
                        else if (g.Button("移除區塊", "skills/entry/remove/" + aSpec.Target))
                            m_Message = At(iProj, SCP_EntryDocInstaller.Uninstall(aDst, aManaged).Message);
                    }
                    g.Note("　・" + aParse.Detail);
                }
            }
        }

        // full 模式：整檔比對（那個檔是我們自己的，沒有使用者區要保護）
        void DrawEntryFull(SCP_Ui g, SCP_GuiProjectRef iProj, SCP_EntrySpec iSpec, string iDst, string iManaged)
        {
            bool aExists = File.Exists(iDst);
            bool aSame = aExists && SCP_EntryDoc.Normalize(SafeRead(iDst)) == SCP_EntryDoc.Normalize(iManaged);
            using (g.Row())
            {
                g.Label($"{iSpec.Target}　{(aSame ? "● 同步" : aExists ? "◐ 有差" : "○ 未安裝")}"
                        + $"　{iSpec.Destination}　〔整檔模式〕");
                if (!aSame && g.Button(aExists ? "覆寫" : "安裝", "skills/entry/full/" + iSpec.Target))
                    m_Message = At(iProj, WriteFull(iDst, iManaged));
            }
        }

        string WriteFull(string iPath, string iContent)
        {
            try
            {
                string? aDir = Path.GetDirectoryName(iPath);
                if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir!);
                string aTmp = iPath + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.WriteAllText(aTmp, iContent, new UTF8Encoding(false));
                if (File.Exists(iPath)) File.Replace(aTmp, iPath, null); else File.Move(aTmp, iPath);
                // 回讀 —— 寫入端會替自己說謊
                return SCP_EntryDoc.Normalize(SafeRead(iPath)) == SCP_EntryDoc.Normalize(iContent)
                    ? $"✓ 已寫入 {Path.GetFileName(iPath)}（回讀確認）"
                    : $"⚠ 寫進去了但回讀對不上：{iPath}";
            }
            catch (Exception e) { return $"⚠ 寫檔失敗：{e.GetType().Name}: {e.Message}"; }
        }

        // ── 單一 agent 的細節 ─────────────────────────────────────
        // 區塊職責：一次只看一家（Tim 2026-08-30）。
        // 物理意義: 三家全展開時，畫面上八成的字跟當下要做的事無關 —— 而那些字不會消失，
        //          它們只是變成背景音，然後把真的需要處理的那一行一起蓋掉。

        void DrawAgentSection(SCP_Ui g, SCP_GuiProjectRef iProj)
        {
            var aOptions = new List<SCP_GuiOption>();
            foreach (SCP_SkillTarget t in SCP_SkillTarget.All)
                aOptions.Add(new SCP_GuiOption(t.Id, t.Display + "（" + t.SkillsRelative + "）"));

            string aSaved = m_Ctx.Prefs.Get(KeyAgent);
            string aDefault = aSaved.Length > 0 ? aSaved : SCP_SkillTarget.All[0].Id;
            string aPick = g.Dropdown("Agent", aOptions, aDefault, "skills/agent");
            if (aPick != aSaved) m_Ctx.Prefs.Write(KeyAgent, aPick);

            SCP_SkillTarget? aTarget = SCP_SkillTarget.ById(aPick);
            if (aTarget == null) { g.Note($"⚠ 認不得的 agent：{aPick} —— 請重選。"); return; }

            using (g.Box($"{aTarget.Display}　{iProj.Root}/{aTarget.SkillsRelative}"))
            {
                List<SCP_SkillStatus> aAll = SCP_SkillInstall.Status(SkillsRoot, aTarget, iProj.Root);
                if (aAll.Count == 0) { g.Note("・沒有任何 skill（來源是空的，安裝端也沒有殘留）"); return; }

                // 📌 別套裝的收成**一行摘要**，不逐列印。它們既沒有按鈕也不需要動作，
                //    三十行同樣的字就是背景音 —— 而背景音會讓旁邊真的要處理的那一行也被略過。
                //    ⚠ 但不可以完全不顯示：agent 載入 skill 時只掃目錄不看標記，
                //      **看不見 ＋ 仍生效 ＝ 靜默僵屍**。所以要有數字與名字。
                var aRows = new List<SCP_SkillStatus>();
                var aForeign = new List<string>();
                foreach (SCP_SkillStatus r0 in aAll)
                {
                    if (r0.State == SCP_SkillState.Foreign) aForeign.Add(r0.Name); else aRows.Add(r0);
                }
                if (aForeign.Count > 0)
                    g.Note($"◇ 另有 {aForeign.Count} 個是 UCL 那套安裝器裝的（本頁不動它們，但它們仍會被 agent 載入）："
                           + string.Join(" / ", aForeign));

                foreach (SCP_SkillStatus r in aRows)
                {
                    using (g.Row())
                    {
                        g.Label($"{SkillMark(r.State)}　{r.Name}");
                        string aId = "skills/" + aTarget.Id + "/" + r.Name;
                        switch (r.State)
                        {
                            case SCP_SkillState.NotInstalled:
                            case SCP_SkillState.Stale:
                                if (g.Button(r.State == SCP_SkillState.NotInstalled ? "安裝" : "同步", aId + "/sync"))
                                    m_Message = At(iProj, SCP_SkillInstall.Sync(SkillsRoot, aTarget, iProj.Root, r.Name).Message);
                                break;
                            case SCP_SkillState.Synced:
                                if (g.Button("移除", aId + "/rm"))
                                    m_Message = At(iProj, SCP_SkillInstall.Remove(aTarget, iProj.Root, r.Name).Message);
                                break;
                            case SCP_SkillState.Orphan:
                                if (g.Button("移除殘留", aId + "/rm"))
                                    m_Message = At(iProj, SCP_SkillInstall.Remove(aTarget, iProj.Root, r.Name).Message);
                                break;
                            case SCP_SkillState.Unmanaged:
                                // 自動流程不碰來源不明的目錄 —— 但**顯示**它：不顯示比不刪更糟
                                g.Label("（你自己放的，不動它）");
                                break;
                        }
                    }
                    if (r.State != SCP_SkillState.Synced) g.Note("　・" + r.Detail);
                }

                g.Space();
                using (g.Row())
                {
                    if (g.Button("↻ 同步這家全部", "skills/" + aTarget.Id + "/all"))
                    {
                        int aOk = 0, aFail = 0;
                        foreach (string s in SCP_SkillSource.Discover(SkillsRoot))
                            if (SCP_SkillInstall.Sync(SkillsRoot, aTarget, iProj.Root, s).Ok) aOk++; else aFail++;
                        m_Message = At(iProj, $"{aTarget.Display}：成功 {aOk} 個／失敗 {aFail} 個");
                    }
                    if (g.Button("🗑 移除這家全部（只動本工具裝的）", "skills/" + aTarget.Id + "/rmall"))
                    {
                        int aOk = 0, aSkip = 0;
                        foreach (SCP_SkillStatus r in aAll)
                        {
                            if (r.State == SCP_SkillState.NotInstalled) continue;
                            if (r.State == SCP_SkillState.Foreign || r.State == SCP_SkillState.Unmanaged) { aSkip++; continue; }
                            if (SCP_SkillInstall.Remove(aTarget, iProj.Root, r.Name).Ok) aOk++; else aSkip++;
                        }
                        m_Message = At(iProj, $"{aTarget.Display}：移除 {aOk} 個／沒動 {aSkip} 個（別套裝的與你自己放的一律不碰）");
                    }
                }
            }
        }

        // ── 小工具 ────────────────────────────────────────────────

        // 區塊職責：每一句動作訊息都要帶**安裝對象**。
        // 物理意義: 🩸 2026-08-30 —— 我以為安裝對象是 Bar、實際是 Senate，按下「移除這家全部」
        //          之後訊息只說「Claude Code：移除 1 個」，我拿那句當成功就往下跑，
        //          於是**刪掉了三分鐘前才裝好的那一份**。
        //          訊息裡少了那個定語，「成功」與「成功地做在錯的地方」在畫面上一模一樣。
        //          ⇒ 這是讓那格失敗**當場喊**的最小修法（修法優先序第二階）。
        static string At(SCP_GuiProjectRef iProj, string iMsg) { return iMsg + "　@ " + iProj.Root; }

        static string Norm(string? iPath) => (iPath ?? "").Replace('\\', '/').TrimEnd('/');

        static string StateMark(SCP_EntryState iState)
        {
            switch (iState)
            {
                case SCP_EntryState.Synced: return "● 同步";
                case SCP_EntryState.Stale: return "◐ 有更新";
                case SCP_EntryState.NotInstalled: return "○ 未安裝";
                case SCP_EntryState.LocalEdit: return "✎ 被手改過";
                case SCP_EntryState.NeedsMigration: return "⇪ 待遷移";
                case SCP_EntryState.Duplicated: return "🟥 重複區塊";
                default: return "🟥 marker 壞掉";
            }
        }

        static string SkillMark(SCP_SkillState iState)
        {
            switch (iState)
            {
                case SCP_SkillState.Synced: return "●";
                case SCP_SkillState.Stale: return "◐";
                case SCP_SkillState.NotInstalled: return "○";
                case SCP_SkillState.Orphan: return "🟥";
                case SCP_SkillState.Foreign: return "◇";
                default: return "・";
            }
        }

        static SCP_GuiProjectRef? Find(IReadOnlyList<SCP_GuiProjectRef> iList, string iRoot)
        {
            foreach (SCP_GuiProjectRef p in iList) if (p.Root == iRoot) return p;
            return null;
        }

        static string SafeRead(string iPath)
        {
            try { return File.ReadAllText(iPath, Encoding.UTF8); } catch { return ""; }
        }

        // 區塊職責：算出「從安裝對象看過去，SCP_Core 在哪」。
        // 物理意義: 這個字串會被寫進使用者的 CLAUDE.md（`@<相對路徑>/AgentEntry/…`）。
        //          ⚠ 兩個宿主算出來的必須一致，否則會互相覆寫來回跳，每次同步一筆 diff
        //            而兩邊都自認正確。⇒ 只用 Uri 的相對化，不自己拼 `../`。
        // 數值影響: 跨磁碟時回空字串（呼叫端要顯示「本區塊停用」，不是靜靜跳過）。
        string CoreRelativeTo(string iProjectRoot)
        {
            try
            {
                string a = Path.GetFullPath(iProjectRoot).Replace('\\', '/').TrimEnd('/') + "/";
                string b = Path.GetFullPath(m_Ctx.CoreRoot).Replace('\\', '/').TrimEnd('/');
                if (!string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase))
                    return "";
                return Uri.UnescapeDataString(new Uri(a).MakeRelativeUri(new Uri(b)).ToString()).Replace('\\', '/');
            }
            catch { return ""; }
        }
    }
}
