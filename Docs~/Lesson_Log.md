---
title: Lesson 庫寫入 —— 記一條跨 agent 共享的教訓
description: senate cmd note-lesson 的使用說明：參數、寫到哪（Lessons/lessons.jsonl 一行一條＋確認檔）、去重只看 body、actor／category 的後備、會被擋下的情形，以及為什麼一行的形狀不能改。
cmds: [note-lesson]
target_audience: [AI_Agent]
---

# 📝 Lesson 庫寫入

撞到設計坑／debug 教訓 → 當場記一條，不靠記憶。新 session 撞到類似問題前，先看本檔的「精選」與 jsonl 尾端。

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
4. 不認得的參數（`--arg severity=…`、拼錯的 `--arg autor=…`）**在 append 之前**擋下，不會掉欄位還回成功。
5. 寫入包在檔案鎖裡：兩個人同時記同一條，只會留一行。

## ⚠ 一行的形狀不能改

去重是拿 `"body":"<轉義後的 body>"` 的**字面**去比 —— 轉義規則（只轉 `" \ \n \r \t` 與其他控制字元，中文原樣）
或 key 的空白一變，同一條教訓就會被記兩次，而且不會有任何一層喊。

## 精選（跨 task 通用的那幾條）

jsonl 是原始紀錄；值得每個人先知道的，人工升格到這裡。升格時合併相似條目、移除過時的，總數保持在十條上下（jsonl 不刪）。
⛔ 不自動升格 —— 精選必須有人讀過、判斷它跨 task 通用。

1. **外觀 OK ≠ 真的 OK**：語法、身分、狀態、內容四層各自要有對應的驗證；同類盲點撞兩次就是 pattern。
2. **dogfood 勝過理論**：規則上線後立刻活體跑一輪。
3. **工具說成功 ≠ 成功**：讀它寫出來的結果檔或回讀資料，不信退出碼與 ✓。
4. **長文與特殊字元一律走檔案**（`--arg-file`），不夾在 shell 參數裡。
5. **子字串比對用最長命中**，不用第一個命中（「叮」會先吃掉「叮叮」）。
6. **推薦方案前先查使用者實際的工具棧**，別讓對方替你的假設驗證。
7. **復原指南要進版控**，不放 gitignore 的目錄 —— 真的要用它時，那個目錄可能已經沒了。
8. **額度、context、對方累了是三個獨立的停止訊號**，任何一個亮了就該停。

## 不要做

- 不寫 PII、token、API key、webhook URL。
- 不重記既有條目 —— 先 grep jsonl 與精選。
- 不記瑣事：一條教訓要在類似情境當下就用得上；長的檢討寫成文件，不塞進一行。
