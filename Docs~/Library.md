---
title: 閱讀庫（senate cmd library）
description: 依設定與 persona 解析讀寫落點，管理作品、媒材、讀者、章節 round、角色看法與投影。
cmds: [library]
last_updated: 2026-10-02
target_audience: [AI_Agent, Tools_Maintainer]
---

# 閱讀庫

`senate cmd library` 在本機執行，不需要 Unity Editor。宿主每次讀取設定，使用 `SCP_PathRegistry` 與 `SenatePathBinding` 解析資料根與信件庫根；指令不接受根目錄參數。

| 根 | 設定 | 落點 |
|---|---|---|
| 資料根 | 唯一啟用專案的 `agentCommandsRoot` | `BookNotes/Library/works`、`media` |
| 信件庫根 | `awakening.lettersRoot` | `<persona>/bookshelf/<media-id>.md`、`<persona>/cmd/reading_recall_<media-id>.md` |
| 外部漫畫庫 | 唯一啟用專案的 `.comic_root.local`，由閱讀心得管理頁輸出 | `op=comics` 掃描來源 |

資料根與信件庫根可獨立設定；`auto` 由共同 registry 推導。根必須是已存在的絕對路徑，persona 必須有 `profile/`。設定缺失、無法解析或根不存在回 exit 3；參數或 persona 不合法回 exit 2。檢查不建立目錄，錯誤在寫入前返回。漫畫庫只在 `comics` 操作驗證。

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
`share_body` 帶 persona、media_id、chapter_id 與選填 round（預設最新），純讀組出分享正文；發布與分享回執由發文入口處理。

`scan` 產出審計報告，疑似同作品由人確認；`show_migrated=1` 包含已遷移項目。
`authored_diff` 帶 book 與 work_id 對拍寫書資料；`authored_migrate` 預設 dry-run，帶 `confirm=1` 才寫入。

完整參數：`senate cmd library --help`。閱讀步驟使用 `reading-library`／`reading-manga` skill；日常內容以 Library 為來源，Archive 僅作人工遷移。
