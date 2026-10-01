---
title: Lesson 庫寫入 —— 記一條跨 agent 共享的教訓
description: senate cmd note-lesson 的使用說明：參數、寫到哪（Lessons/lessons.jsonl 一行一條＋確認檔）、去重只看 body、actor／category 的後備、會被擋下的情形，以及為什麼一行的形狀不能改。
cmds: [note-lesson]
last_updated: 2026-10-01 (TASK-0354：從 UCL `ucmd run NoteLesson` 搬到 Senate CLI)
target_audience: [AI_Agent]
related:
  - ucl_core:Skills~/agent-lessons-log/SKILL.md | agent-lessons-log | 入口 skill（curated lessons 與 promote 流程）
---

# 📝 Lesson 庫寫入

撞到設計坑／debug 教訓 → 當場記一條，不靠記憶。**本地跑，不需要 Unity Editor。**

```bash
senate cmd note-lesson --arg persona=<me> --arg category=<bug|design|workflow|debug|test> \
    --arg-file body=<檔：短句精華> [--arg-file title=<檔>] [--arg tags=a,b,c] [--arg actor=<署名>]
```

⚠ 中文句子一律 `--arg-file`（一句也一樣）—— 判準是「它是不是一句話」，不是「它危不危險」。

## 寫到哪

| 檔 | 內容 |
|---|---|
| `AgentCommands/Lessons/lessons.jsonl` | append 一行 `{"ts","actor","category","body"[,"title"][,"tags"]}`（UTF-8 無 BOM、`\n` 結尾） |
| `AgentCommands/Lessons/_last_lesson.md` | 這一次的確認（每次覆寫） |
| `letters/<persona>/cmd/notelesson_last_op.md` | 給了 persona 才鏡寫一份 |

在自由時間中（活動 `lesson-log`）時，確認檔尾端會多一段「▶ 下一步」。

## 規則

1. **去重只看 body**（trim 後完全相同 ⇒ 不 append，確認檔寫「重複，skip」，exit 0）—— actor／category 不同也算重複。
   要強制再記一次，改寫 body。
2. `actor` 沒給 ⇒ 用 `persona`；兩個都沒給 ⇒ `unknown`。`category` 沒給 ⇒ `general`。
3. `title`／`tags` 沒給就**不寫那個鍵**（不寫 `""`／`[]` —— 「沒給」與「給了空的」是兩件事）。tags 逗號切、去空白、去重。
4. 不認得的參數（`--arg severity=…`、拼錯的 `--arg autor=…`）**在 append 之前**擋下（CLI 未知參數預檢）——
   🩸 舊版會靜默丟掉 `title`／`tags`，回 Success、jsonl 也真的多一行，**成功與掉欄位長得一模一樣**（TASK-0078／BUG-42）。
5. 寫入包在檔案鎖裡：兩個人同時記同一條，只會留一行。

## ⚠ 一行的形狀不能改

去重是拿 `"body":"<轉義後的 body>"` 的**字面**去比 —— 轉義規則（只轉 `" \ \n \r \t` 與其他控制字元，中文原樣）
或 key 的空白一變，同一條教訓就會被記兩次，而且不會有任何一層喊。
⚠ 舊格式的行（冒號後有空白、`\uXXXX` 轉義、key 叫 `lesson`）本來就比不到 —— 那是既有狀態，不是本指令的病。
