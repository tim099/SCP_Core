// 區塊職責：**doc 指令** —— 從 CLI 查文件：列出、印一份的全文、全文搜尋。
// 物理意義：Tim 2026-09-29（TASK-0337）：「把新文件直接在 Senate 專案內重寫，然後可以直接透過 CLI 查詢文件內文」
//           「skill 直接指向 CLI，確保操作跟對應文件只有一份」。
//           ⇒ 文件本體仍是 md（住在指令所在那一邊），CLI 只是讀它的入口 —— ⛔ 本檔不存任何說明文字。
// 數值影響：純讀檔，零寫入。讀取與「哪份文件講哪支指令」全在 <see cref="SCP.Core.Docs.SCP_DocStore"/>。
//
// ⚠ 退出碼分三種，⛔ 不共用：
//   0 ＝ 查到了（含「搜尋命中 0」—— 那是一個答案，而且會明說掃了幾份）
//   2 ＝ 你要的東西不存在（沒有這個名字／撞名）
//   3 ＝ 根本查不了（宿主沒裝文件根）—— 跟「沒有這份文件」分開，否則沒接上的宿主看起來像沒有文件
#nullable enable
using System;
using System.Collections.Generic;
using SCP.Core.Docs;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Doc : SCP_Cmd
    {
        public override string Name => "doc";
        public override string Category => SCP_CmdCategory.System;
        public override string Summary => "查文件：列出全部／印一份的全文／全文搜尋 —— 文件住在指令所在那一邊（Senate `Docs/`、SCP_Core `Docs~/`）";

        public override string Example => SCP_CmdRegistry.Invoke("doc --arg op=show --arg name=Doc_Query");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("op", "做什麼", iDefault: "list", iChoices: new[] { "list", "show", "search" }),
            new SCP_CmdArgSpec("name", "op=show：文件名（檔名去掉 .md，不分大小寫）", iDefault: ""),
            new SCP_CmdArgSpec("keyword", "op=search：關鍵字（不分大小寫、逐行比對）", iDefault: ""),
            new SCP_CmdArgSpec("limit", "op=search：最多印幾筆命中（全部命中數照樣印）", iDefault: "50"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            if (!SCP_DocStore.TryList(out List<SCP_DocEntry> aEntries, out List<string> aProblems, out string aError))
                return SCP_CmdResult.Fail(3, "✗ " + aError);

            string aOp = iArgs.Get("op").Trim();
            SCP_CmdResult aResult;
            if (aOp == "show") aResult = Show(aEntries, iArgs.Get("name").Trim());
            else if (aOp == "search") aResult = Search(aEntries, iArgs.Get("keyword").Trim(), iArgs.Get("limit").Trim());
            else aResult = List(aEntries);

            // 問題印在最前面 —— 印在最後的話，`| tail` 看不到，而它說的是「這份清單不完整」。
            if (aProblems.Count > 0)
            {
                var aHead = new List<string>();
                foreach (string aProblem in aProblems) aHead.Add("⚠ " + aProblem);
                aResult.Lines.InsertRange(0, aHead);
            }
            aResult.AddValue("doc_count", aEntries.Count.ToString());
            aResult.AddValue("problem_count", aProblems.Count.ToString());
            return aResult;
        }

        static SCP_CmdResult List(List<SCP_DocEntry> iEntries)
        {
            var aResult = SCP_CmdResult.Success($"# 文件 {iEntries.Count} 份（名字 → 住哪一邊／路徑 —— 標題）", "");
            foreach (SCP_DocEntry aEntry in iEntries)
            {
                string aCmds = aEntry.Cmds.Count > 0 ? "　［指令：" + string.Join(", ", aEntry.Cmds) + "］" : "";
                string aTitle = aEntry.Title.Length > 0 ? " —— " + aEntry.Title : "";
                aResult.Lines.Add($"  {aEntry.Name}　{aEntry.RootLabel}/{aEntry.RelativePath}{aTitle}{aCmds}");
            }
            aResult.Lines.Add("");
            aResult.Lines.Add("看全文：" + SCP_CmdRegistry.Invoke("doc --arg op=show --arg name=<名字>"));
            return aResult;
        }

        static SCP_CmdResult Show(List<SCP_DocEntry> iEntries, string iName)
        {
            if (iName.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=show 要 `--arg name=<文件名>`（清單：" + SCP_CmdRegistry.Invoke("doc") + "）");

            List<SCP_DocEntry> aHits = SCP_DocStore.FindByName(iEntries, iName);
            if (aHits.Count == 0)
            {
                var aFail = SCP_CmdResult.Fail(2, $"✗ 沒有名叫 `{iName}` 的文件");
                var aNear = new List<string>();
                foreach (SCP_DocEntry aEntry in iEntries)
                    if (aEntry.Name.IndexOf(iName, StringComparison.OrdinalIgnoreCase) >= 0) aNear.Add(aEntry.Name);
                aFail.Lines.Add(aNear.Count > 0 ? "  名字裡有這段的：" + string.Join(", ", aNear)
                                                 : "  清單：" + SCP_CmdRegistry.Invoke("doc"));
                return aFail;
            }
            if (aHits.Count > 1)
            {
                var aFail = SCP_CmdResult.Fail(2, $"✗ `{iName}` 撞名 —— {aHits.Count} 份，⛔ 不替你挑：");
                foreach (SCP_DocEntry aEntry in aHits) aFail.Lines.Add($"  {aEntry.RootLabel}/{aEntry.RelativePath}");
                return aFail;
            }

            SCP_DocEntry aDoc = aHits[0];
            var aResult = SCP_CmdResult.Success($"📖 {aDoc.Name}　（{aDoc.RootLabel}/{aDoc.RelativePath}）");
            if (aDoc.Cmds.Count > 0) aResult.Lines.Add("對應指令：" + string.Join(", ", aDoc.Cmds));
            aResult.Lines.Add("");
            aResult.Lines.AddRange(SCP_DocStore.ReadBody(aDoc).TrimEnd('\n').Split('\n'));
            aResult.AddOutput(aDoc.FullPath);
            return aResult;
        }

        static SCP_CmdResult Search(List<SCP_DocEntry> iEntries, string iKeyword, string iLimit)
        {
            if (iKeyword.Length == 0) return SCP_CmdResult.Fail(2, "✗ op=search 要 `--arg keyword=<關鍵字>`");
            if (!int.TryParse(iLimit, out int aLimit) || aLimit <= 0) return SCP_CmdResult.Fail(2, $"✗ limit 要正整數（給的是 `{iLimit}`）");

            List<SCP_DocHit> aHits = SCP_DocStore.Search(iEntries, iKeyword, aLimit, out int aTotal);
            // 命中 0 也是答案 ⇒ exit 0，但**明說掃了幾份** —— 「沒命中」與「沒掃」不可同形。
            var aResult = SCP_CmdResult.Success($"🔍 `{iKeyword}`　命中 **{aTotal}**（掃 {iEntries.Count} 份）"
                                                + (aTotal > aHits.Count ? $"　⚠ 只列前 {aHits.Count} 筆 —— 這是顯示上限，不是命中數" : ""));
            foreach (SCP_DocHit aHit in aHits)
            {
                string aLine = aHit.Line.Length > 160 ? aHit.Line.Substring(0, 160) + "…" : aHit.Line;
                aResult.Lines.Add($"  {aHit.Doc.Name}:{aHit.LineNumber}　{aLine}");
            }
            aResult.AddValue("hits", aTotal.ToString());
            return aResult;
        }
    }
}
