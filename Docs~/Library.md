---
title: 閱讀庫（senate cmd library）
description: 依設定與 persona 解析讀寫落點，管理作品、媒材、讀者、章節 round、角色看法與投影。
cmds: [library]
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

模型是 `work → media → reader`：同一作品的漫畫、小說、動畫是各自的 media，進度與心得分開存；`reader.json` 是進度與看法的正本，閱讀卡與追回檔是可重建的投影。建檔前先確認作品身分 —— media_id 與 persona 不猜、不借別人的 reader root。

媒材為 `comic`、`anim`、`film`、`series`、`stream` 或 `book`，media_id 使用相同前綴。`register_reader` 登記既有媒材的新讀者。章號四位數，`0000` 為序章；重讀建立新 round，同章分場續寫用 `append=1`，選填 `append_round=N`（預設最新）。

`bookmark` 更新進度、看法與狀態；`add_character` 登記 facts 與 view，`revise_view` 新增觀點版本與 change_reason。長文字走 UTF-8 檔案與 `--arg-file`。

資料版面（`<閱讀庫根>`）：

```
works/<work-id>/work.json
media/<media-id>/readers/<persona>/
  reader.json  bookshelf.md
  chapters/<四位章號>/chapter.json  r<round>_<YYYY-MM-DD>.md
  characters/<id>/profile.json  v<N>_<YYYY-MM-DD>.md
```

讀寫前校驗 `reader.json` 的 `reader_persona`／`media_id` 與路徑一致，不符即擋（防代筆）。

共同服務在 `SCP.Core.Library`。原始資料在 reader root，寫入同步該媒材閱讀卡、信件庫副本與追回檔。`sync_shelf` 重建閱讀卡；投影不可手改作為原始資料來源。

## 查來源與組稿

`comics` 掃設定的外部漫畫庫，列已同步、來源失聯與未登記系列。`scan` 的報告寫到 `<資料根>/BookNotes/_migration/scan_report.md`（每次覆寫）。

`comic_pages` 只要 `media_id`（`comic-<slug>`，不需要 persona）：
- 不帶 `chapter_id` ⇒ 列這部有哪些話。
- 帶 `chapter_id`（四位數）⇒ 列該話的**頁檔絕對路徑**。內部漫畫（`ArtGallery/Comic/<slug>`，優先）回分鏡稿 `Chapters/NNN.md` 與從稿內圖片連結解出的 `RawImages/NNN_pNN.png`；外部漫畫回 `<漫畫庫根>/<作品 卷>/<話>/` 底下依檔名排序的圖。頁在磁碟上不存在會標「缺檔」並計入 `missing_pages`，不靜默略過；找不到該話時錯誤訊息帶上可用的話範圍。
漫畫閱讀用這支取頁路徑，不要自己拼路徑。

`share_body` 帶 persona、media_id、chapter_id 與選填 round（預設最新），純讀組出分享正文，**不發文**。

## 讀漫畫

一次讀**一話**，讀完當場落心得，再決定要不要讀下一話。

1. **挑作品**：有進度的優先接續 —— 跨 session 先 `recall`，從書籤指的下一話開始（同 session 連讀免 recall）。
   新作品：外部漫畫先 `comics` 看清單，未建檔的走 `media_init`（`media_id=comic-<slug>`、`media_kind=comic`），或在 `senate ui` 漫畫庫頁「初始化」。從 `0001`（有序章則 `0000`）開始。
2. **取頁**：`comic_pages --arg media_id=comic-<slug> --arg chapter_id=<四位數>` 列出頁檔絕對路徑。不自己拼路徑、不寫死漫畫庫位置。標「缺檔」的頁要回報，不略過。
3. **逐頁看圖**：用讀圖工具打開每一頁，看過分鏡、神態與台詞才寫。⛔ 沒看圖不寫心得。
4. **落心得**：`note_chapter`，一話一個 `chapter_id`（⛔ 不把多話併進同一話）；重讀自動開新 round，同一話分場讀完用 `append=1`。書籤與目前看法用 `bookmark`。

- 漫畫是獨立媒材：動畫、電影等改編各用自己的 media，進度不共用。
- 人物 facts 與主觀 view 分開；未確認的名字或猜測不寫進 facts。
- **內部漫畫**（同事畫的，`<資料根>/ArtGallery/Comic/<slug>/`）：先讀 `README.md`（話數表、鐵則、人設索引）；`comic_pages` 回分鏡稿 `Chapters/NNN.md` 與每張畫稿路徑，圖文對讀；人物 facts 以 `Characters/` 文字人設為準、外型以圖版為準。可寫的角度：分鏡與成品的落差、鐵則兌現度、形象一致性。心得照常寫進 Library，⛔ 不寫回 `ArtGallery/Comic/`。
- 不讀寫 Archive 當日常流程。

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

`senate ui`，群組「閱讀」底下三頁，資料都與對應的指令同源（頁面不存路徑、不自己解析檔案欄位）：

| 頁 | key | 做什麼 |
|---|---|---|
| 閱讀心得 | `reading` | 全庫瀏覽（媒材 → 作品 → persona）、作品入口搜尋（正本／Archive 分開計數）、追回檢視（可切全文）與「產生追回檔」 |
| 漫畫庫 | `comics` | 外部漫畫庫作品清單（🟢已建檔／🟡來源失聯／⚪未建檔）、卷話明細、開資料夾；未建檔的可「初始化 Library media」（**先預覽、確認才寫**；署名 persona 由工具列明確選，期待度固定 3）。路徑設定只住路徑管理頁 |
| 書店 | `bookshop` | 藏書架（依系列，可依 kind 篩選）、全文書庫（含「編輯書籍」→ `bookedit` 頁）、捐贈簿、捐贈表單（**先預覽、確認才扣款**）、推薦書單 |

## 書店（`senate cmd book`）

藏書架、系列、分類與 `_donation.json` 正典版面見 `senate cmd doc --arg op=show --arg name=Book`。

## Archive（舊格式，只供人工遷移）

`<資料根>/BookNotes/Archive/<slug>/`（`book.json`、`chapters/chNN_*.md`、`characters/<id>/`）是舊閱讀紀錄，**唯讀**：⛔ 不寫入、不改名、不加標記，日常閱讀不讀它。
遷移由原讀者人工搬進 Library；裁決只記在 `BookNotes/_migration/registry.json`（`migrated`＝已進正本、`kept_archive`＝刻意不遷、`born_new`＝新流程直接建），`scan` 依它隱藏已遷移項（`show_migrated=1` 連同列出；`kept_archive` 不隱藏、標已裁決），疑似同作品由人確認。
舊 `book.json` 缺 `reader_persona` 就記 `unknown`，不從內容猜；舊 `chapter: N` 可能重複，不能直接當 `chapter_id`。

`authored_diff` 帶 book 與 work_id 對拍寫書資料；`authored_migrate` 預設 dry-run，帶 `confirm=1` 才寫入。
完整參數：`senate cmd help library`。
