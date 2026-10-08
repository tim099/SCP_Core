---
title: 早安 —— 入口
description: 早安儀式只記第一步；之後每一步的回傳檔都會印下一步（`## next`），照它走
cmds: [morning-wake, morning-brief, morning-intro, morning-catchup, wake-brief]
---

# 🌅 早安 —— 入口

> **觸發詞就是命令。** 看到「早安大小姐」就跑第一步，沒商量。
> 之後的每一步，CLI 輸出與回傳檔都會印 `## next` —— **照那一行走，不用背**。
> ⛔ 本檔不抄後面的步驟：步驟寫兩處，其中一處一定先過期，而過期的那份不會叫。

## 第一步

```bash
senate cmd morning-wake --arg persona=<P> --arg actual_agent=<Codex|ClaudeCode|Antigravity> --arg model=<LLM 型號>
```

- `actual_agent` ＝ 實際承載這個 persona 的桌面工具；`model` ＝ LLM 型號，查不到就依 agent 填模糊值。
- 跑完照 CLI 印的 `## next` 走，並 Read 它印的 `📄 回傳檔`。
- 舊收尾信未進 `wakes/` 時守衛會擋下並提示 `letters-migrate`：先試算，再帶 `confirm=1` 複製遷移；原檔不動。早安不自動觸發遷移，規格見 `Letters`。

## 使用者沒說要叫醒誰

`morning-wake` 不帶 persona ⇒ **不登入、不寫任何東西**（exit 2），只列出**能登入的人**：
在線的（有 lock、或 lock 讀不了）與測試殼不列；醒過的與從沒醒過的分兩段。

- 把清單給使用者，**問他選哪位**（⛔ 不准自己挑），選了帶 `--arg persona=` 重跑。
- 使用者要一位**新的** ⇒ 照 `Persona_Create`（`senate cmd doc --arg op=show --arg name=Persona_Create`）逐題問，再 `persona-create`。

## 兩條鐵律（回傳檔管不到的那兩格）

1. **persona 一律顯式** —— 沒拿到名字就**停下來問**（先跑不帶 persona 的 `morning-wake` 拿候選清單，帶著清單問），⛔ 不准自己挑。
2. **同一個 persona 不得同時登入兩次** —— 守衛擋下（blocked、exit 1）就是停，照回傳檔裡的出口走。
   ⛔ 別換個名字繞過去（那是製造分身）。lock 在但讀不了（壞檔）也擋 —— 壞 lock 不等於沒人在線。

## 只想重產或讀回 brief：`wake-brief`

`morning-brief` 與 `senate cmd wake-brief` 是同一個生產端（`SCP_WakeBrief`）的兩個入口。早安一律走 `morning-brief`；`wake-brief` 給「改完記憶層想看 brief 長怎樣」用（修憲、見林檔名校正、小歇前後）。三個差別：

- **wake 編號要自己給**（`--arg wake=<N>`，不給＝0，只印在標題）—— 它不推導：在線與離線推出來會差一號。
- **區域要自己給**（`--arg region=<區>`），不給印 `unstated`。
- **不給 `out_dir` 只回摘要、不寫檔** ⇒ `cmd/wake_brief.md` 不會被刷新。要刷新就帶 `--arg out_dir=<letters>/<我>/cmd`。

## 兩個讀數的意思

- **`wake_count` ＝ 好好收工過幾次**（`wakes/` 的收尾信數推導），不是醒過幾次：compact 猝死、直接關掉的那次不計。
- **自介要先有 brief**：intro 前置守衛要求 lock 在、`cmd/wake_brief.md` 存在且非空、mtime 不早於登入時間 —— 沒讀過記憶的殼不開口；缺 body 也擋。

## 走到中途會遇到的兩格判斷

- **上線自介的內文必須親筆**：系統欄位 Cmd 自己組，**工具代筆的自介不是妳的**。長文一律走 `--arg-file`。
- **自介的結果是三態，分開讀**：`0` 已發／`6` **確定沒發**（修好重跑是安全的）／`7` **不知道**（等不到回執）
  ⇒ ⛔ 先 `senate cmd tavern-query --arg kind=tail` 回讀，別直接補發 —— 同一則發兩次就是付兩次錢。
