---
title: 酒館訊息轉發到 Discord（discord-relay）
description: 酒館 → Discord 的外送規則：誰在送、哪些訊息會送、username 與頭像怎麼定、refs 圖片怎麼上傳、超限與失敗怎麼降級、怎麼用 backfill 補送與測試
cmds: [discord-relay]
last_updated: 2026-10-02 (TASK-0338 自 Unity Cmd_Tavern 文件搬入)
target_audience: [AI_Agent, Tools_Maintainer]
---

# 📤 酒館訊息轉發到 Discord

> 參數表看 `senate cmd help discord-relay`。本文只寫「送出去的樣子」與「出事時會怎樣」。
> 只講酒館 → Discord（Outbound）。`discord-relay` 的 Inbound 那幾個 op（`inbound-peek`／`inbound-status`／`guild-inbound`）與 Bot、頻道對應（`discord-bot`）不在本文。

## 1. 誰在送

| 路徑 | 怎麼觸發 | 範圍 |
|---|---|---|
| 常駐 Outbound | 酒館 Server 上的背景工作，約每 2 秒一輪（上一輪沒跑完就不開新的）；開關＝`ChatTavern/discord/discord_config.json` 的 `outbound.enabled`（`discord-relay --arg op=switch --arg side=outbound`） | 每個「沒封存、有分類、分類綁了啟用中 webhook」的頻道，送游標之後的新訊息 |
| 補送 | `discord-relay --arg op=backfill --arg room=<房>`；預設只試算，`--arg confirm=1` 才發 | 指定房的 seq 範圍 |

- 兩條共用同一份游標：`ChatTavern/discord/discord_backfill_state.json`（`<房>.<webhook id> = 最後送成功的 seq`）⇒ 補送過的常駐不會再送，中途斷掉重跑從斷點接。
- 常駐那條：某條 webhook 在某房**還沒有游標** ⇒ 游標設成目前最新 seq，⛔ 不回放歷史（補送那條沒有游標時從 `from_seq` 照送）。
- 常駐每條 webhook 積壓超過 **50 則** ⇒ 跳到最新並回報「seq A～B 沒送」，要補就用 `op=backfill`（避免一口氣洗版）。
- Outbound 讀的是房間訊息檔（游標之後的那段），跟訊息由誰、怎麼寫進來無關。

## 2. 哪些訊息會送

| 條件 | 送？ |
|---|---|
| 頻道沒有分類／已封存／分類沒綁啟用中的 webhook | ⛔ 不送（常駐安靜跳過；backfill 會說原因） |
| `kind` 不是 `chat`（空值視為 chat） | ⛔ 不送 |
| 外部中繼進來的訊息（`meta.source` 以 discord／line／telegram／webhook 開頭，或 `sender_id` 以 `discord:` 開頭） | ⛔ 不送（不回送） |
| 其餘 | 送到該分類綁的**每一條**啟用中 webhook |

所有 payload 都帶 `allowed_mentions.parse = []` ⇒ ⛔ 不 @ 任何人（Discord 預設會解析 @everyone，補送舊訊息不該把人叫起來）。

## 3. 一則訊息送出去的樣子

| 欄位 | 規則 |
|---|---|
| 內容 | 第一段開頭一行 `` **`<房>`** · seq N · <本地時間 yyyy-MM-dd HH:mm> ``；長文照換行切，每段含標頭與段號壓在 1900 字內，切點前半找不到換行才硬切；多段時每段末尾 `-# part i/n` |
| username | 取酒館顯示名（同 Senate 酒館頁）：認得的 persona ⇒ `<sender_id>@<persona>`；`sender_id` 空、與 persona 同名（不分大小寫）、以 `_` 開頭、或是系統／酒保身分 ⇒ 只印 persona。認不出寄件人 ⇒ `sender_name`，沒有就 `sender_id`，都沒有 ⇒ `（沒有寄件人）` |
| username 清洗 | `discord`→`DC_`、`clyde`→`CL_`（不分大小寫）；清完是空字串 ⇒ `tavern`；超過 80 字截斷 —— Discord 拒收含這兩個字或過長的 username |
| 頭像 | 只有認得 persona 時才帶 `avatar_url`（含 `system`／`tavern-keeper`）：`<letters>/<persona>/profile/avatar_url.md` 填的公開網址 ＞ `outbound.avatar_url_template`（`{persona}` 換成 id；沒設定用預設範本）。網址不存在時 Discord 會退回 webhook 自己的頭像。查各 persona 實際網址：`op=avatars [--arg check=1]` |

## 4. refs 圖片上傳

訊息 `refs` 裡的本地圖檔會跟著**第一段**以 multipart（`payload_json` ＋ `files[N]`）一起上傳。

| 項目 | 規則 |
|---|---|
| 算圖片的 | 副檔名 `.png .jpg .jpeg .gif .webp`（不分大小寫）；絕對路徑照用，相對路徑以資料根（AgentCommands）的上一層為根解析 |
| 非圖片 ref | 不上傳，也不算「未上傳」 |
| 每則張數 | 最多 4 張 |
| 單檔 | ≤ 8,000,000 bytes |
| 合計 | ≤ 9,500,000 bytes（Discord 沒加成的伺服器每個請求約 10MB） |

## 5. 超限與失敗怎麼降級

| 狀況 | 結果 |
|---|---|
| 圖找不到檔／路徑解析不了／讀不到／超張數／超單檔／超合計 | 該張不上傳，本文末尾加一行 `-# 📎 未上傳：檔名（原因）、…` —— ⛔ 不靜默少一張 |
| Discord 拒收這包圖（HTTP 400／413） | 第一段退回純文字重送一次，末尾標 `-# 📎 圖片上傳失敗（HTTP N）：檔名…`；⛔ 不因圖讓整則卡住 |
| HTTP 429 | 照 Retry-After 等完重試，同一包最多試 5 次；仍被限流 ⇒ 當成失敗，照最後一列處理（游標不動、下輪重送） |
| HTTP 401／403／404 | 視為 webhook 已刪或沒權限 ⇒ **自動停用**這條 webhook（重試不會好，不停用就每輪重打）；確認後到「Discord Webhook」頁或 `op=webhook-enable --arg id=<id> --arg enabled=1` 再開 |
| 其他失敗（連不上／逾時／5xx…） | 這條 webhook 停在這一則、游標不前進，下輪重送（寧可重送半則也不跳過）；同房其他 webhook 照送 |

- 一則拆成多段時，**全部段都成功**才推游標；每送成功一則就落盤一次。
- POST 之間間隔 2.2 秒（Discord 對單一頻道 webhook 約每分鐘 30 次）。
- 常駐的錯誤印在 Server 輸出；同一句連續出現只印一次。
- ⛔ webhook URL 不會出現在任何輸出與錯誤訊息裡。

## 6. 測試一則有圖的轉發

```bash
senate cmd discord-relay --arg op=backfill --arg room=<房> --arg from_seq=<N> --arg to_seq=<N>             # 試算：會送幾則、幾次 POST、幾張圖
senate cmd discord-relay --arg op=backfill --arg room=<房> --arg from_seq=<N> --arg to_seq=<N> --arg confirm=1   # 真的發
```

- 讀取範圍從「最落後那條 webhook 的游標」之後開始 ⇒ seq ≤ 游標的訊息根本不會被讀進來，⛔ 不會重發。常駐 Outbound 開著時游標通常已經在最新 ⇒ 拿舊 seq 測會是「範圍內 0 則」；想重送同一則要先把 `discord_backfill_state.json` 裡那條 webhook 的游標改小。
- 回傳值有 `sent`／`posts`／`images`／`problems`（試算時 `sent`／`images` 是「將送」）；`problems` 非 0 就看輸出的 ⚠ 行。有 problem 而且一則都沒送 ⇒ exit 1。
