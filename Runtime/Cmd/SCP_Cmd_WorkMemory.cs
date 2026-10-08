// 區塊職責：`cmd work-memory` —— 工作記憶區的入口（topics／init／add／read／supersede／link／index／tasks／archive／delete）。
// 物理意義：邏輯在 `SCP_WorkMemory`；本檔只做「參數 → 呼叫 → 印結果」。資料根由宿主給；related_docs 的具名根（senate:／scp_core:）也由宿主宣告（TASK-0390 起不再拿資料根上一層當 repo 根）。
// 數值影響：寫入只落在 `<資料根>/WorkMemory/`（read 另寫一份 briefing 到 `<資料根>/WorkMemoryReadBriefs/`）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.WorkMemory;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_WorkMemory : SCP_Cmd
    {
        public override string Name => "work-memory";
        public override string Category => SCP_CmdCategory.Memory;

        public override string Summary => "工作記憶（以工作主題為單位的 knowhow 庫）：查／建主題、寫 fragment、取代、關聯、反向索引、歸檔／刪除";

        public override string Details =>
            "資料：`<資料根>/WorkMemory/<topic>/`（`_topic.md` 主題卡＋`<type>_<slug>.md` fragment＋機械生成的 `_index.md`）。\n"
            + "⚠ fragment **寫一次不改寫正文** —— 要更新走 op=supersede（一步式：new_id／new_title／new_body）。\n"
            + "⚠ op=read 會另寫一份共讀 briefing（記憶摘要＋related_docs 原文前 100 行），📄 回傳檔就是它 —— 讀那份，不讀終端截斷的內容。\n"
            + "⚠ Task 側的 `memory_topic`／`memory_archived_commit` 由任務寫入端寫；本指令只寫主題卡的 `task_indices`／`status`。\n"
            + "⚠ archive／delete 前置 git 守衛：主題目錄的每個檔都要在版控裡且乾淨，否則 exit 3。delete 不帶 confirm=1 是 dry-run。";

        public override string Example =>
            SCP_CmdRegistry.InvokeOf<SCP_Cmd_WorkMemory>("--arg op=read --arg topic=<主題> --arg with_links=1");

        static readonly string[] s_Ops = { "topics", "init", "add", "read", "supersede", "link", "index", "tasks", "archive", "delete" };

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("op", "做什麼", iDefault: "topics", iChoices: s_Ops),
            new SCP_CmdArgSpec("topic", "主題 slug（topics／index 以外必填）"),
            new SCP_CmdArgSpec("title", "init／add：標題"),
            new SCP_CmdArgSpec("desc", "init：主題卡正文（省略＝用 title）"),
            new SCP_CmdArgSpec("type", "add：decision／knowhow／pitfall／state／pointer"),
            new SCP_CmdArgSpec("id", "add：fragment slug（自動加 `<type>_` 前綴）／supersede：要被取代的 fragment id"),
            new SCP_CmdArgSpec("body", "add：正文（長文走 --arg-file）"),
            new SCP_CmdArgSpec("links", "add：關聯 `<topic>/<id>`，逗號分隔"),
            new SCP_CmdArgSpec("docs", "add：related_docs（權威文件路徑，逗號分隔；可帶 `:行號`）"),
            new SCP_CmdArgSpec("by", "add：作者 persona／supersede：取代它的 fragment id／delete：誰刪的"),
            new SCP_CmdArgSpec("new_id", "supersede 一步式：新 fragment slug"),
            new SCP_CmdArgSpec("new_title", "supersede 一步式：新標題"),
            new SCP_CmdArgSpec("new_body", "supersede 一步式：新正文（長文走 --arg-file）"),
            new SCP_CmdArgSpec("new_by", "supersede 一步式：新 fragment 的作者（省略＝沿用舊的）"),
            new SCP_CmdArgSpec("from", "link：`<topic>/<id>`"),
            new SCP_CmdArgSpec("to", "link：`<topic>/<id>`（雙向）"),
            new SCP_CmdArgSpec("with_links", "read：1＝一起拉 1-hop 關聯 fragment", iDefault: "0", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("types", "read：只看這幾種 type（逗號分隔）"),
            new SCP_CmdArgSpec("set", "tasks：整組覆寫，如 `8,15`；顯式給空值＝清空"),
            new SCP_CmdArgSpec("add", "tasks：加單號（逗號分隔）"),
            new SCP_CmdArgSpec("remove", "tasks：移除單號（逗號分隔）"),
            new SCP_CmdArgSpec("commit", "archive／delete：記哪顆 commit（省略＝主題目錄所屬工作區的 HEAD）"),
            new SCP_CmdArgSpec("undo", "archive：1＝改回 active", iDefault: "0", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("confirm", "delete：1＝真的刪（省略＝dry-run）", iDefault: "0", iChoices: new[] { "0", "1" }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root").Trim();
            if (aDataRoot.Length == 0 || !Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(3, "✗ 資料根不存在：" + aDataRoot);
            // 具名根（senate:／scp_core:）由宿主宣告（SCP_WorkMemory.HostNamedRoots）；⛔ 不再拿「資料根的上一層」當 repo 根（TASK-0390）
            var wm = new SCP_WorkMemory(aDataRoot);

            string op = iArgs.Get("op").Trim();
            string topic = iArgs.Get("topic").Trim();
            if (op != "topics" && op != "index" && op != "link" && topic.Length == 0)
                return SCP_CmdResult.Fail(2, $"✗ op={op} 要 `--arg topic=<主題>`（清單：{SCP_CmdRegistry.InvokeOf<SCP_Cmd_WorkMemory>("--arg op=topics")}）");
            string taskHint(string iField) => SCP_CmdRegistry.InvokeNamed("task",
                $"--arg op=update --arg persona=<你> --arg index=<n> --arg {iField}");

            SCP_WorkMemoryResult r;
            switch (op)
            {
                case "topics": r = wm.Topics(); break;
                case "init":
                    if (iArgs.Get("title").Trim().Length == 0) return SCP_CmdResult.Fail(2, "✗ op=init 要 `--arg title=<標題>`");
                    r = wm.Init(topic, iArgs.Get("title").Trim(), iArgs.Get("desc").Trim()); break;
                case "add":
                    if (iArgs.Get("type").Trim().Length == 0 || iArgs.Get("id").Trim().Length == 0 || iArgs.Get("title").Trim().Length == 0)
                        return SCP_CmdResult.Fail(2, "✗ op=add 要 type／id／title／body");
                    r = wm.Add(topic, iArgs.Get("type").Trim(), iArgs.Get("id").Trim(), iArgs.Get("title").Trim(), iArgs.Get("body"),
                               iArgs.Get("links"), iArgs.Get("docs"), iArgs.Get("by").Trim()); break;
                case "read": r = wm.Read(topic, iArgs.Get("with_links") == "1", iArgs.Get("types").Trim()); break;
                case "supersede":
                    if (iArgs.Get("id").Trim().Length == 0) return SCP_CmdResult.Fail(2, "✗ op=supersede 要 `--arg id=<fragment id>`");
                    r = wm.Supersede(topic, iArgs.Get("id").Trim(), iArgs.Get("by").Trim(), iArgs.Get("new_id").Trim(),
                                     iArgs.Get("new_title").Trim(), iArgs.Get("new_body"), iArgs.Get("new_by").Trim()); break;
                case "link":
                    if (iArgs.Get("from").Trim().Length == 0 || iArgs.Get("to").Trim().Length == 0)
                        return SCP_CmdResult.Fail(2, "✗ op=link 要 `--arg from=<topic>/<id> --arg to=<topic>/<id>`");
                    r = wm.Link(iArgs.Get("from").Trim(), iArgs.Get("to").Trim()); break;
                case "index": r = wm.Index(topic); break;
                case "tasks":
                    r = wm.Tasks(topic, iArgs.IsExplicit("set") ? iArgs.Get("set") : null, iArgs.Get("add"), iArgs.Get("remove"),
                                 taskHint("memory_topic=" + topic)); break;
                case "archive":
                    r = wm.Archive(topic, iArgs.Get("commit"), iArgs.Get("undo") == "1", taskHint("memory_archived_commit=<sha>")); break;
                case "delete":
                    r = wm.Delete(topic, iArgs.Get("commit"), iArgs.Get("by").Trim(), iArgs.Get("confirm") == "1",
                                  taskHint("memory_archived_commit=<sha>")); break;
                default: return SCP_CmdResult.Fail(2, "✗ 不認得的 op：" + op);
            }

            var aResult = new SCP_CmdResult { ExitCode = r.Exit };
            aResult.Lines.AddRange(r.Lines);
            if (r.BriefPath.Length > 0)
            {
                aResult.Lines.Add("");
                aResult.Lines.Add("⇒ 共讀 briefing 就是下面的 📄 回傳檔 —— 開它讀，不要讀終端截斷的內容。");
                aResult.AddOutput(r.BriefPath);
            }
            aResult.AddValue("written", r.Written.Count.ToString());
            return aResult;
        }
    }
}
