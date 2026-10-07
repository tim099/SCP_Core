// 區塊職責：`senate cmd note-lesson` —— 記一條跨 agent 共享的 lesson（`Lessons/lessons.jsonl`）。**原生**，不需要 Unity。
// 物理意義：TASK-0354（Unity → Senate 遷移第一批 ④）。取代 UCL `ucmd run NoteLesson`（已退場）。
//           參數與 Editor 版同名同義（body／actor／category／title／tags／persona），寫出來的檔逐位元組同形
//           —— 本體在 `SCP_LessonLog`，本檔只做參數→輸入、落確認檔、附自由時間提示。
// 數值影響：jsonl append 0 或 1 行；覆寫 `Lessons/_last_lesson.md`；給了 persona 再鏡寫一份到
//           `letters/<persona>/cmd/notelesson_last_op.md`。不發酒館、不動錢。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Letters;
using SCP.Core.Lessons;
using SCP.Core.Paths;
using SCP.Core.Session;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_NoteLesson : SCP_Cmd
    {
        public override string Name => "note-lesson";
        public override string Category => SCP_CmdCategory.Memory;

        public override string Summary => "記一條跨 agent 共享的 lesson（去重＋append `Lessons/lessons.jsonl`）—— **本地跑，不需要 Editor**";

        public override string Details =>
            "去重只看 body（trim 後完全相同 ⇒ 不 append，回傳檔寫「重複，skip」；⛔ 不是失敗）。\n"
            + "actor 沒給 ⇒ 用 persona；兩個都沒給 ⇒ `unknown`。category 沒給 ⇒ `general`。tags 逗號分隔。\n"
            + "⚠ 中文長句一律 `--arg-file body=<檔>`（不經過 shell）。\n"
            + "📄 確認檔：`Lessons/_last_lesson.md`（給了 persona 再鏡寫 `letters/<persona>/cmd/notelesson_last_op.md`）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("note-lesson --arg persona=Template --arg category=debug --arg-file body=D:/tmp/lesson.md");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("body", "lesson 本體（短句精華）。走 --arg-file", iRequired: true),
            new SCP_CmdArgSpec("persona", "誰記的（actor 的後備、per-persona 確認檔的落點）", iDefault: ""),
            new SCP_CmdArgSpec("actor", "署名（沒給 ⇒ persona）", iDefault: ""),
            new SCP_CmdArgSpec("category", "分類（沒給 ⇒ general）", iDefault: ""),
            new SCP_CmdArgSpec("title", "標題（選填）", iDefault: ""),
            new SCP_CmdArgSpec("tags", "逗號分隔（選填）", iDefault: ""),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aData = iArgs.Get("data_root").Trim();
            if (!Directory.Exists(aData)) return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aData);

            string aBody = iArgs.Get("body").Trim();
            if (aBody.Length == 0) return SCP_CmdResult.Fail(2, "✗ body 必填（傳 --arg-file body=<短句精華>）");
            string aPersona = iArgs.Get("persona").Trim();
            string aActor = iArgs.Get("actor").Trim();
            if (aActor.Length == 0) aActor = aPersona;
            if (aActor.Length == 0) aActor = "unknown";
            // ⚠ 與 Editor 版同：沒給（或給空字串）⇒ general；只給空白 ⇒ trim 成空字串照寫（不替它補預設）
            string aRawCat = iArgs.Get("category");
            string aCategory = aRawCat.Length == 0 ? "general" : aRawCat.Trim();

            var aIn = new SCP_LessonInput
            {
                Body = aBody, Actor = aActor, Category = aCategory,
                Title = iArgs.Get("title").Trim(), Tags = SCP_LessonLog.ParseTags(iArgs.Get("tags")),
            };
            // 確認檔印「相對資料根」的路徑（TASK-0390：同 refs 慣例）—— 🩸 舊版把資料根上一層當 repo 根，印成 `Valhalla/Lessons/...`
            string aRel = SCP_LessonLog.LessonsDirName + "/" + SCP_LessonLog.JsonlFileName;

            SCP_LessonNoteResult r;
            try { r = SCP_LessonLog.Note(aData, aIn, aRel); }
            catch (Exception e) { return SCP_CmdResult.Fail(1, "✗ 寫不進 lesson 庫：" + e.GetType().Name + ": " + e.Message); }

            var aResult = new SCP_CmdResult();
            if (r.Warning.Length > 0) aResult.Lines.Add("⚠ " + r.Warning);
            string aText = r.ConfirmText;
            if (!r.Duplicate)
            {
                var sb = new StringBuilder(aText);
                // 自由時間提示查的是 **actor**（Editor 版同）
                SCP_FreeTimeHint.Append(sb, new SCP_DataRoot(aData), aActor, out string aHintWarn);
                if (aHintWarn.Length > 0) aResult.Lines.Add("⚠ " + aHintWarn);
                aText = sb.ToString();
            }

            // ── 確認檔：重複時寫不進去是失敗（那是唯一的回報面）；新增時寫不進去只是警告（jsonl 已經 append 了）──
            string aConfirm = SCP_LessonLog.LastLessonPath(aData);
            try { SCP_CmdPayload.WriteAtomic(aConfirm, aText); aResult.AddOutput(aConfirm); }
            catch (Exception e)
            {
                if (r.Duplicate) return SCP_CmdResult.Fail(1, "✗ 確認檔寫不進去：" + e.Message);
                aResult.Lines.Add("⚠ 確認檔寫不進去（jsonl 已 append 不受影響）：" + e.Message);
            }
            if (aPersona.Length > 0)
            {
                string aMirror = SCP_LettersPaths.CmdPayload(SCP_DataPaths.Letters(new SCP_DataRoot(aData)), aPersona, "notelesson", "last_op");
                try { SCP_CmdPayload.Write(aMirror, aText); aResult.AddOutput(aMirror); }
                catch (Exception e) { aResult.Lines.Add("⚠ per-persona 鏡寫失敗（全域確認檔已寫）：" + e.Message); }
            }

            aResult.Lines.Add(r.Duplicate
                ? "🔁 重複，skip（同一個 body 已在 lesson 庫裡）—— 零 append"
                : "✅ lesson 已 append → " + r.JsonlPath);
            aResult.AddValue("duplicate", r.Duplicate ? "1" : "0");
            aResult.AddValue("ts", r.Ts);
            aResult.AddValue("actor", aActor);
            aResult.AddValue("category", aCategory);
            return aResult;
        }
    }
}
