// 區塊職責：`senate cmd tavern-routing` —— 看／解析／匯入酒館路由判準（TASK-0296）。
// 物理意義：判準的真相源在資料根（SCP_TavernRouting）。本支是它的 CLI 面；後台頁吃同一組 API。
// 數值影響：show／resolve 純讀；import_unity 不帶 confirm=1 只試算（零寫入），
//           目標檔已存在時還要 overwrite=1 —— 匯入是一次性搬家，⛔ 不該在有人用後台改過之後被重跑蓋掉。
// ⚠ 方言限制：C# 9 / netstandard2.1。
#nullable enable
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SCP.Core.Cmd;

namespace SCP.Core.Tavern
{
    public sealed class SCP_Cmd_TavernRouting : SCP_Cmd
    {
        public override string Name => "tavern-routing";
        public override string Summary =>
            "酒館路由判準（哪個 category 走哪個 group、計不計酬）：看／解析／從 Unity asset 匯入 —— 本地跑，不需要 Editor";
        public override string Details =>
            "落點：`<資料根>/ChatTavern/tavern_routing.json`（⛔ 不含 webhook URL）。\n"
            + "· `op=show`：列出 group 與可疑設定。\n"
            + "· `op=resolve --arg category=<c>`：這個 category 會落到哪個 group、付不付發文底薪（空字串＝沒帶 category）。\n"
            + "· `op=import_unity --arg unity_dir=<UCL_TavernCategoryRoutingAsset 目錄>`：一次性搬家；"
            + "不帶 `confirm=1` 只試算。目標檔已存在要再加 `overwrite=1`。webhook URL 一律剝掉並回報條數。";
        public override string Example =>
            SCP_CmdRegistry.Invoke("tavern-routing --arg data_root=<資料根> --arg op=resolve --arg category=work");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("op", "show | resolve | import_unity", iDefault: "show",
                               iChoices: new[] { "show", "resolve", "import_unity" }),
            new SCP_CmdArgSpec("category", "op=resolve：要解析的 category（可空）", iDefault: ""),
            new SCP_CmdArgSpec("unity_dir", "op=import_unity：Unity 端 asset 目錄", iDefault: ""),
            new SCP_CmdArgSpec("confirm", "op=import_unity：1 ＝ 真的寫檔", iDefault: ""),
            new SCP_CmdArgSpec("overwrite", "op=import_unity：目標檔已存在時要 1 才覆寫", iDefault: ""),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root").Trim();
            switch (iArgs.Get("op").Trim())
            {
                case "resolve": return Resolve(aDataRoot, iArgs.Get("category"));
                case "import_unity":
                    return Import(aDataRoot, iArgs.Get("unity_dir").Trim(),
                                  iArgs.Get("confirm").Trim() == "1", iArgs.Get("overwrite").Trim() == "1");
                default: return Show(aDataRoot);
            }
        }

        static SCP_CmdResult Show(string iDataRoot)
        {
            SCP_TavernRoutingRead r = SCP_TavernRouting.Read(iDataRoot);
            if (!r.Ok)
            {
                var aFail = SCP_CmdResult.Fail(2, "✗ " + r.Error);
                aFail.AddValue("missing", r.Missing ? "1" : "0");
                return aFail;
            }
            var aRes = SCP_CmdResult.Success($"酒館路由判準：{r.Groups.Count} 個 group（陣列順序＝比對順序）", "檔案：" + r.Path);
            foreach (SCP_TavernRouteGroup g in r.Groups)
                aRes.Lines.Add($"· `{g.Id}`　{(g.Enabled ? "on " : "OFF")}"
                               + $"{(g.IsDefault ? "　default" : "")}{(g.IsPaidPost ? "　💰計酬" : "")}{(g.Exclusive ? "　exclusive" : "")}"
                               + $"　categories=[{string.Join(", ", g.Categories)}]");
            foreach (string w in r.Warnings) aRes.Lines.Add("⚠ " + w);
            aRes.AddValue("groups", r.Groups.Count.ToString());
            aRes.AddValue("warnings", r.Warnings.Count.ToString());
            return aRes;
        }

        static SCP_CmdResult Resolve(string iDataRoot, string iCategory)
        {
            SCP_TavernRoutingRead r = SCP_TavernRouting.Read(iDataRoot);
            if (!r.Ok) return SCP_CmdResult.Fail(2, "✗ " + r.Error);
            SCP_TavernRouteGroup? g = SCP_TavernRouting.ResolveTargetGroup(r.Groups, iCategory);
            string aCat = SCP_TavernRouting.Normalize(iCategory);
            var aRes = g == null
                ? SCP_CmdResult.Success($"category `{aCat}` ⇒ **沒有 group**（沒命中、也沒有 enabled 的 default）⇒ 不計酬")
                : SCP_CmdResult.Success($"category `{aCat}` ⇒ `{g.Id}`　計酬={(g.IsPaidPost ? "是" : "否")}");
            aRes.AddValue("group", g?.Id ?? "");
            aRes.AddValue("paid", g != null && g.IsPaidPost ? "1" : "0");
            return aRes;
        }

        static SCP_CmdResult Import(string iDataRoot, string iUnityDir, bool iConfirm, bool iOverwrite)
        {
            if (iUnityDir.Length == 0 || !Directory.Exists(iUnityDir))
                return SCP_CmdResult.Fail(2, "✗ `unity_dir` 不存在：`" + iUnityDir + "`（要指到 UCL_TavernCategoryRoutingAsset 那個目錄）");
            var (aGroups, aStripped, aProblems) = SCP_TavernRouting.ImportUnityAssetDir(iUnityDir);
            if (aProblems.Count > 0)
            {
                var aBad = SCP_CmdResult.Fail(1, $"✗ 有 {aProblems.Count} 個 asset 讀不了 ⇒ **整批不寫**（⛔ 不帶著缺角的判準上線）");
                foreach (string p in aProblems) aBad.Lines.Add("· " + p);
                return aBad;
            }
            string aPath = SCP_TavernRouting.PathOf(iDataRoot);
            bool aExists = File.Exists(aPath);
            var aLines = aGroups.Select(g => $"· `{g.Id}`　{(g.Enabled ? "on" : "OFF")}{(g.IsDefault ? "　default" : "")}"
                                             + $"{(g.IsPaidPost ? "　💰計酬" : "")}{(g.Exclusive ? "　exclusive" : "")}"
                                             + $"　categories=[{string.Join(", ", g.Categories)}]").ToList();
            aLines.Add($"· webhook URL 剝掉 {aStripped} 條（⛔ 不進資料根）");
            foreach (string w in SCP_TavernRouting.Diagnose(aGroups)) aLines.Add("⚠ " + w);

            if (!iConfirm)
            {
                var aDry = SCP_CmdResult.Success($"（試算，零寫入）會匯入 {aGroups.Count} 個 group ⇒ {aPath}"
                                                 + (aExists ? "　⚠ 目標檔**已存在**，真的寫要再加 overwrite=1" : ""));
                aDry.Lines.AddRange(aLines);
                aDry.AddValue("written", "0");
                return aDry;
            }
            if (aExists && !iOverwrite)
                return SCP_CmdResult.Fail(2, "✗ 目標檔已存在：" + aPath + " —— 可能有人已經在後台改過。確定要蓋掉就加 `overwrite=1`");

            var (aOk, aMsg) = SCP_TavernRouting.Write(iDataRoot, aGroups);
            if (!aOk) return SCP_CmdResult.Fail(1, aMsg);
            var aRes = SCP_CmdResult.Success(aMsg);
            aRes.Lines.AddRange(aLines);
            aRes.AddValue("written", "1");
            aRes.AddValue("groups", aGroups.Count.ToString());
            aRes.AddValue("stripped_urls", aStripped.ToString());
            return aRes;
        }
    }
}
