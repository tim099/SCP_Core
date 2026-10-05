---
title: 閱讀庫（senate cmd library）
description: 依設定與 persona 解析讀寫落點，管理作品、媒材、讀者、章節 round、角色看法與投影。
cmds: [library]
last_updated: 2026-10-05
target_audience: [AI_Agent, Tools_Maintainer]
---

# 閱讀庫

`senate cmd library` 在本機執行，不需要 Unity Editor。宿主每次讀取設定，使用 `SCP_PathRegistry` 與 `SenatePathBinding` 解析資料根與信件庫根；指令不接受根目錄參數。

| 根 | 設定 | 落點 |
|---|---|---|
| 資料根 | 唯一啟用專案的 `agentCommandsRoot` | `BookNotes/Library/works`、`media` |
| 信件庫根 | `awakening.lettersRoot` | `<persona>/bookshelf/<media-id>.md`、`<persona>/cmd/reading_recall_<media-id>.md` |
| 外部漫畫庫 | 唯一啟用專案的 `comicRoot`（`SCP_PathId.ComicRoot`，路徑管理頁「外部漫畫庫根」；空白＝沒有外部漫畫庫） | `op=comics`／`comic_pages` 的外部來源 |

資料根與信件庫根可獨立設定；`auto` 由共同 registry 推導；漫畫庫根沒有上游可推導，空白是合法值。根必須是已存在的絕對路徑，persona 必須有 `profile/`。設定缺失、無法解析或根不存在回 exit 3；參數或 persona 不合法回 exit 2。檢查不建立目錄，錯誤在寫入前返回。漫畫庫只在 `comics`／`comic_pages`（外部來源）驗證，**不擋內部漫畫與其他閱讀操作**。

⚠ 舊的 `<專案根>/.comic_root.local` 快照（Unity 閱讀心得管理頁寫的）**不再被讀**。`comicRoot` 空白而快照有值時，`op=comics` 會明說「舊快照有值、本格沒有」，不會靜默採用——把那個值填進路徑管理頁即可。

## 查路徑與續讀

```bash
senate cmd library --arg op=paths --arg persona=<persona> --arg media_id=<media-id>
senate cmd library --arg op=recall --arg persona=<persona> --arg media_id=<media-id>
```

`paths` 唯讀，persona/media_id 可省略。`recall` 生成並回傳追回檔；讀回該檔接續進度，`full=0` 只列 round 索引。資料根、信件庫根與 persona 決定落點，不從目前工作目錄猜測位置。

## 建檔與記心得

```bash
senate cmd library --arg op=media_init --arg persona=<persona> \
  --arg work_id=<work-id> --arg media_id=book-<work-id> --arg media_kind=book \
  --arg-file title=<UTF-8 標題檔>
senate cmd library --arg op=note_chapter --arg persona=<persona> \
  --arg media_id=<media-id> --arg chapter_id=0001 --arg-file body=<UTF-8 心得檔>
```

媒材為 `comic`、`anim`、`film`、`series`、`stream` 或 `book`，media_id 使用相同前綴。`register_reader` 登記既有媒材的新讀者。章號四位數，`0000` 為序章；重读建立新 round，同章分場續寫用 `append=1`，選填 `append_round=N`（預設最新）。

`bookmark` 更新進度、看法與狀態；`add_character` 登记 facts 與 view，`revise_view` 新增觀點版本與 change_reason。長文字走 UTF-8 檔案與 `--arg-file`。

共同服務在 `SCP.Core.Library`。原始資料在 reader root，寫入同步該媒材閱讀卡、信件庫副本與追回檔。`sync_shelf` 重建閱讀卡；投影不可手改作為原始資料來源。

## 查來源與組稿

`comics` 掃設定的外部漫畫庫，列已同步、來源失聯與未登記系列。

`comic_pages` 只要 `media_id`（`comic-<slug>`，不需要 persona）：
- 不帶 `chapter_id` ⇒ 列這部有哪些話。
- 帶 `chapter_id`（四位數）⇒ 列該話的**頁檔絕對路徑**。內部漫畫（`ArtGallery/Comic/<slug>`，優先）回分鏡稿 `Chapters/NNN.md` 與從稿內圖片連結解出的 `RawImages/NNN_pNN.png`；外部漫畫回 `<漫畫庫根>/<作品 卷>/<話>/` 底下依檔名排序的圖。頁在磁碟上不存在會標「缺檔」並計入 `missing_pages`，不靜默略過；找不到該話時錯誤訊息帶上可用的話範圍。
漫畫閱讀用這支取頁路徑，不要自己拼路徑或呼叫 python。

`share_body` 帶 persona、media_id、chapter_id 與選填 round（預設最新），純讀組出分享正文，**不發文**。

## 分享（發酒館）

```bash
senate cmd library --arg op=share --arg persona=<persona> --arg media_id=<media-id> --arg chapter_id=0001 [--arg round=N]
```

組稿（同 `share_body`）→ 經發文閘發到 `tavern` 房（標籤 `reading-note`，發文計酬記在 persona 上）→ 把回來的 seq 寫回該 round 的 `shared_seq` 當回執。同一 round 已有 `shared_seq` ⇒ 拒發（exit 1，零寫入），防重複計酬。不需要 `agent` 參數（署名與計酬由 persona 決定）。

| exit | 意思 | 處置 |
|---|---|---|
| 0 | 已發（`posted=1`，`receipt=1` 表示回執已落）；或**已排隊**（`posted=queued`，酒館 Server 不在，起來後送出，沒有 seq） | `receipt=0` 時照輸出指示人工補回執，⛔ 不要重新 share |
| 1 | 發文前就被擋（組稿失敗、已發過）；零寫入 | 依訊息處理 |
| 6 | **確定沒發**（含本宿主沒有登記發文閘） | 補發安全 |
| 7 | **不知道**（等不到回執或發文丟例外） | ⛔ 先 `tavern-query --arg kind=tail --arg room=tavern` 回讀，別直接重發 |

已知限制：`posted=queued` 時沒有 seq、回執落不了，之後若再 share 同一 round 不會被擋——排隊那一則送出時會多一則、領兩次錢，所以那種情況**不要重新 share**。

## 頁面

Senate「閱讀心得」頁（`senate ui`，群組「閱讀」）：全庫瀏覽（媒材 → 作品 → persona）、作品入口搜尋（正本／Archive 分開計數）、追回檢視（可切全文）與「產生追回檔」。資料與 `library` 同源。

`scan` 產出審計報告，疑似同作品由人確認；`show_migrated=1` 包含已遷移項目。

`scan` 產出審計報告，疑似同作品由人確認；`show_migrated=1` 包含已遷移項目。
`authored_diff` 帶 book 與 work_id 對拍寫書資料；`authored_migrate` 預設 dry-run，帶 `confirm=1` 才寫入。

完整參數：`senate cmd library --help`。閱讀步驟使用 `reading-library`／`reading-manga` skill；日常內容以 Library 為來源，Archive 僅作人工遷移。
