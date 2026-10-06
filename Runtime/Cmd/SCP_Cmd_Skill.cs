// 區塊職責：**skill 指令** —— 印出一個 skill 的內容（op=show），以及從 CLI 安裝 skill（status／sync／remove）。
// 物理意義：TASK-0406（Tim 2026-10-05）：
//           ① 「skill 本身只要提供入口 CLI」「兩邊 skill 都指向同一份 CLI 指令」⇒ op=show 是那個入口；
//              內容由 SCP_SkillContent 依源檔 `docs:` 現讀文件組出來，呼叫端不給任何路徑。
//           ② 「Senate 的 skill 是否可以透過 Senate CLI 安裝」⇒ op=status／sync／remove，
//              跟後台頁「Skill 安裝管理」走**同一支** SCP_SkillInstall（⛔ 不另寫一套）。
//           ③ 「Senate 的 skill 目前只要安裝到 D:\Unity\Senate 內」⇒ 安裝對象固定是宿主 repo（RootsProvider 給）。
// 數值影響：show／list／status 純讀；sync／remove **不帶 confirm=1 只印計畫、一個檔都不動**。
//
// ⚠ 退出碼：0 成功／2 你要的東西不存在或內容組不完整（⛔ 不印半份）／3 宿主沒裝上根／1 寫入時有失敗。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using SCP.Core.Skills;

namespace SCP.Core.Cmd
{
    /// <summary>宿主給的兩個根：skill 源（`<SCP_Core>/Skills~`）與安裝對象（宿主 repo 根）。</summary>
    public sealed class SCP_SkillRoots
    {
        public string SkillsRoot { get; }
        public string InstallRoot { get; }
        public SCP_SkillRoots(string iSkillsRoot, string iInstallRoot) { SkillsRoot = iSkillsRoot; InstallRoot = iInstallRoot; }
    }

    public sealed class SCP_Cmd_Skill : SCP_Cmd
    {
        /// <summary>宿主在啟動時掛上（共用層不推導路徑）。沒掛 ⇒ exit 3。</summary>
        public static Func<SCP_SkillRoots>? RootsProvider { get; set; }

        public override string Name => "skill";
        public override string Summary =>
            "skill 入口與安裝：op=show 印出一個 skill 的完整內容（依源檔宣告現讀文件合併）／list／"
            + "status／sync／remove（裝到宿主 repo；不帶 confirm=1 只印計畫）—— **不需要 Editor**";

        public override string Example => SCP_CmdRegistry.Invoke("skill --arg op=show --arg name=scp-morning");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("op", "做什麼", iDefault: "list", iChoices: new[] { "list", "show", "status", "sync", "remove" }),
            new SCP_CmdArgSpec("name", "skill 名（show／remove 必填；sync 省略＝全部）", iDefault: ""),
            new SCP_CmdArgSpec("agent", "status／sync／remove：哪一家（省略＝三家全部）", iDefault: "all",
                               iChoices: new[] { "all", "claude", "codex", "antigravity" }),
            new SCP_CmdArgSpec("confirm", "sync／remove：1 ＝ 真的寫；省略 ＝ 只印計畫", iDefault: "0", iChoices: new[] { "0", "1" }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            SCP_SkillRoots aRoots;
            try
            {
                if (RootsProvider == null) return SCP_CmdResult.Fail(3, "✗ 宿主沒有裝上 skill 根（SCP_Cmd_Skill.RootsProvider）");
                aRoots = RootsProvider();
            }
            catch (Exception e) { return SCP_CmdResult.Fail(3, "✗ 讀取 skill 根失敗：" + e.Message); }
            if (!SCP_SkillSource.RootExists(aRoots.SkillsRoot))
                return SCP_CmdResult.Fail(3, $"✗ 找不到 skill 源目錄：{aRoots.SkillsRoot} —— 這不是「沒有 skill」，是讀不到來源");

            string aOp = iArgs.Get("op").Trim();
            string aName = iArgs.Get("name").Trim();
            switch (aOp)
            {
                case "show": return Show(aRoots, aName);
                case "status": return Status(aRoots, Targets(iArgs));
                case "sync": return Sync(aRoots, aName, Targets(iArgs), iArgs.Get("confirm") == "1");
                case "remove": return Remove(aRoots, aName, Targets(iArgs), iArgs.Get("confirm") == "1");
                default: return List(aRoots);
            }
        }

        static List<SCP_SkillTarget> Targets(SCP_CmdArgs iArgs)
        {
            string a = iArgs.Get("agent").Trim();
            if (a.Length == 0 || a == "all") return new List<SCP_SkillTarget>(SCP_SkillTarget.All);
            SCP_SkillTarget? t = SCP_SkillTarget.ById(a);
            return t == null ? new List<SCP_SkillTarget>() : new List<SCP_SkillTarget> { t };
        }

        // ── show ─────────────────────────────────────────────────

        static SCP_CmdResult Show(SCP_SkillRoots iRoots, string iName)
        {
            if (iName.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=show 要 `--arg name=<skill 名>`（清單：" + SCP_CmdRegistry.Invoke("skill") + "）");
            if (!SCP_SkillContent.TryCompose(iRoots.SkillsRoot, iName, out List<SCP_SkillPart> aParts, out List<string> aErrors))
            {
                // ⛔ 一個字的內容都不給：照著半份說明做事比沒有說明更糟
                var aFail = SCP_CmdResult.Fail(2, $"✗ skill `{iName}` 的內容組不出來（{aErrors.Count} 格）—— **這次沒有印任何內容，不要憑印象照做**");
                foreach (string e in aErrors) aFail.Lines.Add("  · " + e);
                return aFail;
            }

            var aResult = SCP_CmdResult.Success(
                $"# 🧭 skill `{iName}` —— 由 {aParts.Count} 份文件組成（查詢當下現讀）", "");
            var aSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SCP_SkillPart p in aParts)
            {
                aResult.Lines.Add($"<!-- ── 📖 {p.Ref}（{p.Doc.RootLabel}/{p.Doc.RelativePath}）── -->");
                aResult.Lines.Add("");
                aResult.Lines.AddRange(p.Body.Split('\n'));
                aResult.Lines.Add("");
                // 同一份文件引用好幾節 ⇒ 輸出路徑只列一次（列三次同一個檔不是三份資訊）
                if (aSeen.Add(p.Doc.FullPath)) aResult.AddOutput(p.Doc.FullPath);
            }
            aResult.AddValue("parts", aParts.Count.ToString());
            return aResult;
        }

        // ── list ─────────────────────────────────────────────────

        static SCP_CmdResult List(SCP_SkillRoots iRoots)
        {
            List<string> aSkills = SCP_SkillSource.Discover(iRoots.SkillsRoot);
            var aResult = SCP_CmdResult.Success($"# skill {aSkills.Count} 份（源：{iRoots.SkillsRoot}）", "");
            foreach (string s in aSkills)
            {
                string aText = System.IO.File.ReadAllText(System.IO.Path.Combine(iRoots.SkillsRoot, s, SCP_SkillSource.SkillFileName));
                List<SCP_SkillDocRef>? aRefs = SCP_SkillEntry.ParseDocs(aText);
                aResult.Lines.Add(aRefs == null ? $"  {s}　〔鏡像模式〕"
                                                : $"  {s}　〔入口模式〕 docs: {string.Join(", ", aRefs)}");
            }
            aResult.Lines.Add("");
            aResult.Lines.Add("看內容：" + SCP_CmdRegistry.Invoke("skill --arg op=show --arg name=<名字>"));
            aResult.AddValue("skill_count", aSkills.Count.ToString());
            return aResult;
        }

        // ── status ───────────────────────────────────────────────

        static SCP_CmdResult Status(SCP_SkillRoots iRoots, List<SCP_SkillTarget> iTargets)
        {
            if (iTargets.Count == 0) return SCP_CmdResult.Fail(2, "✗ 認不得的 agent");
            var aResult = SCP_CmdResult.Success($"# skill 安裝狀態　@ {iRoots.InstallRoot}", "");
            int aNotSynced = 0;
            foreach (SCP_SkillTarget t in iTargets)
            {
                aResult.Lines.Add($"## {t.Display}（{t.SkillsRelative}）");
                foreach (SCP_SkillStatus r in SCP_SkillInstall.Status(iRoots.SkillsRoot, t, iRoots.InstallRoot))
                {
                    aResult.Lines.Add($"  {Mark(r.State)} {r.Name}　{r.State}　{r.Detail}");
                    if (r.State != SCP_SkillState.Synced && r.State != SCP_SkillState.Foreign && r.State != SCP_SkillState.Unmanaged) aNotSynced++;
                }
            }
            aResult.AddValue("not_synced", aNotSynced.ToString());
            return aResult;
        }

        // ── sync／remove ─────────────────────────────────────────

        static SCP_CmdResult Sync(SCP_SkillRoots iRoots, string iName, List<SCP_SkillTarget> iTargets, bool iConfirm)
        {
            if (iTargets.Count == 0) return SCP_CmdResult.Fail(2, "✗ 認不得的 agent");
            List<string> aAll = SCP_SkillSource.Discover(iRoots.SkillsRoot);
            List<string> aSkills = iName.Length == 0 ? aAll : new List<string> { iName };
            if (iName.Length > 0 && !aAll.Exists(s => string.Equals(s, iName, StringComparison.OrdinalIgnoreCase)))
                return SCP_CmdResult.Fail(2, $"✗ 源端沒有 `{iName}`（清單：{string.Join(", ", aAll)}）");

            var aResult = SCP_CmdResult.Success(iConfirm ? $"# 同步 skill　@ {iRoots.InstallRoot}"
                                                         : $"# 計畫（⛔ 沒有寫任何檔；要真的做加 --arg confirm=1）　@ {iRoots.InstallRoot}", "");
            int aOk = 0, aFail = 0, aTodo = 0;
            foreach (SCP_SkillTarget t in iTargets)
            {
                var aState = new Dictionary<string, SCP_SkillStatus>(StringComparer.OrdinalIgnoreCase);
                foreach (SCP_SkillStatus r in SCP_SkillInstall.Status(iRoots.SkillsRoot, t, iRoots.InstallRoot)) aState[r.Name] = r;
                foreach (string s in aSkills)
                {
                    aState.TryGetValue(s, out SCP_SkillStatus? aBefore);
                    if (aBefore != null && aBefore.State == SCP_SkillState.Synced) { aResult.Lines.Add($"  ● {t.Id}/{s}：已同步，不動"); continue; }
                    aTodo++;
                    if (!iConfirm) { aResult.Lines.Add($"  → {t.Id}/{s}：會{(aBefore == null || aBefore.State == SCP_SkillState.NotInstalled ? "安裝" : "覆蓋")}（{aBefore?.Detail ?? "還沒安裝"}）"); continue; }
                    SCP_SkillSyncResult r = SCP_SkillInstall.Sync(iRoots.SkillsRoot, t, iRoots.InstallRoot, s);
                    if (r.Ok) aOk++; else aFail++;
                    aResult.Lines.Add($"  {(r.Ok ? "✓" : "✗")} {t.Id}/{s}：{r.Message}");
                }
            }
            if (iConfirm)
            {
                // 回讀 —— 寫入端會替自己說謊
                int aAfter = 0;
                foreach (SCP_SkillTarget t in iTargets)
                    foreach (SCP_SkillStatus r in SCP_SkillInstall.Status(iRoots.SkillsRoot, t, iRoots.InstallRoot))
                        if (aSkills.Exists(s => string.Equals(s, r.Name, StringComparison.OrdinalIgnoreCase)) && r.State != SCP_SkillState.Synced) aAfter++;
                aResult.Lines.Add($"⇒ 寫入成功 {aOk}／失敗 {aFail}；回讀後仍未同步 {aAfter}");
                aResult.AddValue("not_synced_after", aAfter.ToString());
                if (aFail > 0 || aAfter > 0) aResult.ExitCode = 1;
            }
            aResult.AddValue("planned", aTodo.ToString());
            return aResult;
        }

        static SCP_CmdResult Remove(SCP_SkillRoots iRoots, string iName, List<SCP_SkillTarget> iTargets, bool iConfirm)
        {
            if (iName.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=remove 要 `--arg name=<skill 名>`（不提供一次全刪）");
            if (iTargets.Count == 0) return SCP_CmdResult.Fail(2, "✗ 認不得的 agent");
            var aResult = SCP_CmdResult.Success(iConfirm ? $"# 移除 `{iName}`　@ {iRoots.InstallRoot}"
                                                         : $"# 計畫（⛔ 沒有刪任何檔；要真的做加 --arg confirm=1）　@ {iRoots.InstallRoot}", "");
            int aFail = 0;
            foreach (SCP_SkillTarget t in iTargets)
            {
                if (!iConfirm) { aResult.Lines.Add($"  → {t.Id}/{iName}：會移除（只動本工具裝的；別套裝的與沒標記的不碰）"); continue; }
                SCP_SkillSyncResult r = SCP_SkillInstall.Remove(t, iRoots.InstallRoot, iName);
                if (!r.Ok) aFail++;
                aResult.Lines.Add($"  {(r.Ok ? "✓" : "✗")} {t.Id}：{r.Message}");
            }
            if (aFail > 0) aResult.ExitCode = 1;
            return aResult;
        }

        static string Mark(SCP_SkillState iState)
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
    }
}
