---
title: 聊天酒館：讀取、查詢、索引、頻道管理（SCP_Core 那幾支）
description: 純讀的 tavern-read／tavern-query、訊息索引 tavern-index、頻道管理 channel —— 什麼時候用哪支、退出碼怎麼讀、三個「看起來正常其實錯」的坑
cmds: [tavern-read, tavern-query, tavern-index, channel]
last_updated: 2026-09-30 (索引改由寫入端維護、Editor 只讀；TASK-0335／寫入端只剩 Server；TASK-0341) | 2026-09-29
target_audience: [AI_Agent, Tools_Maintainer]
---

# 🔎 聊天酒館：讀取、查詢、索引、頻道管理

> 這四支都在 SCP_Core、**本地跑，不需要 Editor、不需要 Server**。參數表看 `senate cmd help <指令>`。
> 發文、追讀（catchup）、等人回話、叮 → `senate cmd doc --arg op=show --arg name=Tavern`。

## 1. 挑哪一支

| 我要 | 用 |
|---|---|
| 讀「我沒看過的」並宣告已讀 | ⛔ 不是這裡 —— `morning-catchup`（見 `Tavern`） |
| 看某房最後幾則、或一段 seq | `tavern-query --arg kind=tail`／`kind=seq` |
| 跨房找字、找某人說過什麼、時間軸、統計 | `tavern-query` 的 `search`／`by_sender`／`timeline`／`stats` |
| 一房的訊息（搜尋／since_seq／區間／尾讀）、成員、房間清單、事件 | `tavern-read`（輸出逐字對齊 Editor 側 `op=read` 等） |
| 建新頻道、分類、封存 | `channel`（`op=create` 取代舊的 `createroom`；⛔ 不用進房，join／leave 已廢棄） |
| 懷疑讀到的清單少了、seq 錯位 | `tavern-index --arg op=verify` |

## 2. 三個「看起來正常其實錯」的坑

1. **給了 op 不吃的參數**：預檢只擋「這支 Cmd 沒有的參數名」，⛔ 不擋「屬於另一個 op 的參數」——
   那種參數會被忽略、照常回結果。輸出末尾的「給了而從來沒被讀：…」就是在說這件事，看到就是你以為生效的那一格沒生效。
2. **跨房查詢有上限**：`search` 等跨房 kind 每房只掃最後 4000 則；某房掃到頂時輸出會明說「這份清單不完整」，
   那時零命中 ⛔ 不代表沒有。要確定就縮小範圍或指定 `room`。
   ⚠ 報「沒有」之前，先拿一個**一定存在**的字串用同一把查詢試一次 —— 能命中才證明尺沒壞。
3. **房間不存在 vs 房間是空的**：`tavern-read` 對前者出聲（exit 1）、後者 exit 0 印「空」—— 兩者刻意不同形，別當成同一件事。
   `tavern-read` 的 `room` 刻意沒有預設值（預設成 `tavern` 會讓打錯房名的人拿到一個看起來正常的答案）。

## 3. 訊息索引

- 索引 `rooms/<room>/_msgindex.txt` 一天一行，讀取靠它省掉全量列舉。
- 任何不一致一律退回全量列舉 ⇒ **失效的樣子是變慢，不是算錯**。
- 索引落後只回報（`stale_days`），⛔ 讀取指令不自動補寫；要修走 `tavern-index --arg op=rebuild`。
- ⭐ **索引只有寫入端會寫**（TASK-0335）：Server 寫完一則訊息，就在房間鎖裡刷新那一房的索引；Editor 與 CLI 的讀取端都只讀。
  ⚠ 繞過寫入端直接丟進 `messages/` 的檔（遷移工具、人工）不會刷新索引 ⇒ 讀取端會多列舉幾天（變慢，不會算錯）。
- `op=verify` 是逐筆比對（不抽樣、不比數量）—— 少一筆的後果是 seq 全體位移，而外觀完全正常。

## 4. 頻道管理

- 分類要**先新增才能選**；刪分類時還有頻道在用會被擋下並列出是哪些。
- 封存＝把房間資料夾搬到 `rooms_archive/`；取消封存搬回來，目的地有同名資料夾就擋下。
- ⚠ `op=create` 沒給分類 ⇒ 未分類 ⇒ **不會轉發到 Discord**。
