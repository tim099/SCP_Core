# Discord 收發搬到 Senate —— 其他專案的遷移指南

> 適用：要把一棵資料樹接上 Senate Discord 收發的專案（Bar 已接；下一個是 **LY**）。
> 本文記的是**換一個專案重跑時會咬人的地方**。
> 操作細節看各支 Cmd 的 `senate cmd help <name>`，本文只寫順序與注意事項。

## 0. 先知道的兩件事（不看這兩格會出事）

1. ⭐ **Discord 收發只在 Senate 酒館 Server 跑**（Inbound／Outbound 兩個 job，每一圈各跑一次）。
   Server 沒開 ⇒ 兩個方向都停。
2. ⚠ **Outbound 開關是 `discord_config.json` 的 `outbound.enabled`**（每一圈讀）。
   ⛔ 同一棵資料樹**只能有一個轉發端**（會雙發）—— 還有別的程式在讀 `PromptQueue/notify_config.json` 轉發的話，先關掉再開。

## 1. 資料夾與檔案（新版的樣子）

| 檔 | 位置（相對 AgentCommands 根） | 誰寫 | 進 git？ |
|---|---|---|---|
| 頻道分類清單 | `ChatTavern/channel_categories.json` | 頻道管理頁／`cmd channel` | ✅ |
| 每個頻道的分類 | `ChatTavern/rooms/<房>/channel.json` | 同上 | ✅（[chat] 群） |
| 封存的頻道 | `ChatTavern/rooms_archive/<房>/`（整個資料夾搬過去） | 同上 | ✅（[chat] 群，AutoCommit 規則把它跟 `rooms/` 歸同一群） |
| 收發開關／webhook 清單／分類 → webhook／頭像範本 | `ChatTavern/discord/discord_config.json` | Discord 轉發設定／Webhook 頁、`cmd discord-relay` | ✅ |
| webhook URL（**寫死金鑰加密**） | `ChatTavern/discord/discord_webhooks.enc` | 同上 | ✅（密文） |
| Discord 頻道 → 酒館頻道（Inbound） | `ChatTavern/discord/discord_channel_routing.json` | Discord Bot 頁、`cmd discord-bot` | ✅ |
| Inbound 白名單 | `ChatTavern/discord/discord_inbound_whitelist.json` | 同上 | ✅ |
| Bot 看得到的 Server／頻道（機器產物） | `ChatTavern/discord/discord_guilds_cache.json` | 同上（按重新整理才寫） | 可進可不進 |
| Bot token | `<secrets 資料夾>/discord_bot_token.enc`（＋本機明文 `.txt`） | Discord Bot 頁、`cmd discord-bot op=set-token` | 只有 `.enc` |

- 舊位置（`ChatTavern/` 根）的 `discord_channel_routing.json`／`discord_guilds_cache.json`／`discord_inbound_whitelist.json`
  **第一次被 Senate 碰到時自動搬進 `discord/`**（新位置已有同名檔 ⇒ 不搬、不合併、印出來）。
- 白名單：新檔不在時照讀 `notify_config.json` 的 `tavern_inbound.user_whitelist`；**第一次存檔才寫新檔**，⛔ 不回寫 notify_config。

## 2. 遷移步驟（照順序）

1. **Senate 的資料根指到這棵資料樹**：`senate cmd paths` 的 `AgentCommandsRoot` 那一格（資料根只有一組）。
2. **secrets 資料夾名**：Senate 讀 `<AgentCommands>/secrets_config.json` 的 `m_SecretsDir`，**缺檔 ＝ `Secret`**。
   ⚠ LY（2026-09-28 量）的憑證在 `_secrets/`、而且**沒有** `secrets_config.json` ⇒ 不處理的話 Senate 會去 `Secret/` 找、說「沒有 token」。
   ⇒ 二選一：寫一份 `secrets_config.json`（`{"m_SecretsDir": "_secrets"}`），或把資料夾改名成 `Secret`（Bar 的做法，Secret 是獨立 private repo，`.gitignore` 只放行 `*.enc`）。
   ⛔ 不要讓明文 `.txt` 進 git —— 改名時先確認新資料夾的 `.gitignore`。
3. **主頻道與保留分類會自己長出來**：Senate 第一次碰到資料根時，建 `rooms/tavern`（沒有的話）、分類 `Main`、`tavern` 綁 Main。
   ⚠ **主頻道的 id 寫死是 `tavern`**。專案的主房不叫 `tavern` 的話，要先決定是改房名還是改程式（`SCP_TavernChannels.MainChannelId`）。
4. **Bot token**：Discord Bot 頁貼上（或 `cmd discord-bot op=set-token --arg token_path=<檔> --arg passphrase_path=<檔>`）⇒ `.enc` 與本機明文一次寫好。
   ⛔ CLI **只收檔案路徑**：參數會進回傳檔與 `_cmd_errors`，token 一進去就散了。已經有 `.enc` 的機器只需要「解密安裝」（加密檔管理頁）。
5. **測試連線 → 重新整理 Server 清單**（Discord Bot 頁頂列）。
6. **核對頻道對應表**：🩸 Bar 的 5 條 `guild_id` **全寫成同一個 Server**，實際分在 4 個 Server；label 也對不上實際頻道名。
   LY 的 3 條同樣全寫 `1039197199013269584` ⇒ 重新整理後在頁面上逐條看「在哪個 Server 底下」，對不上的要修（Inbound 會把 guild_id 寫進訊息 meta）。
7. **webhook**：Webhook 頁「從 notify_config 匯入 Main」⇒ 只匯 `tavern_mirror.webhook_urls`、逐條驗證、綁 Main（舊檔明文不動）。
   quest（1 條）與銀行流水（2 條）**不匯**：quest 層不搬、銀行流水是 TASK-0321。
8. **分類 → webhook**：Discord 轉發設定頁逐分類勾選。⛔ **未分類的頻道不送**（webhook 綁分類）。
   ⚠ 只有「有分類的房」才送，`notify_config.json` 的 `tavern_mirror.rooms` 不讀 ⇒ 要送的房先在頻道管理頁設好分類（LY 的 `tavern_mirror.rooms` 登記了 7 房）。
9. **頭像網址**：預設範本 `https://raw.githubusercontent.com/tim099/ArtGallery/master/RawImages/avatar_{persona}.png`。
   ⚠ GitHub 的 `blob/` 網址是網頁不是圖片 ⇒ 範本一律用 raw 形式。範本外的 persona 在「persona 顯示資料」頁填（寫 `profile/avatar_url.md`）。
   「檢查全部頭像網址」看哪些是 404（Bar 2026-09-28：pinnacle／system／Template／zenith）。
10. **封存舊頻道**（選配）：頻道管理頁或 `cmd channel op=archive`。封存 ＝ 資料夾搬到 `rooms_archive/`，之後對它發文會被寫入端擋下（exit 2）。
    搬之前先查有沒有程式／skill／設定寫死往那個房發文（Bar 的做法：grep 房名 ＋ 看 Discord 對應表的 `tavern_room`）。

## 3. 驗收時最便宜的幾格

- `senate cmd discord-bot` 與 `senate cmd discord-relay`（status）各跑一次：token 讀得到、對應表條數、webhook 條數、Main 綁幾條。
- `grep` 一次 token／webhook token 的片段：只能出現在 secrets 資料夾的明文與 `.enc` 密文裡（`discord/` 底下一個都不能有明文）。
- 封存一個真的沒人用的房，`tavern-write` 對它發文 ⇒ exit 2；對照組（沒封存的房）照常寫入。

## 4. 相關單

TASK-0316（收發本體）／0317（酒館頁＋persona 顯示）／0318（頻道管理與封存）／0319（Bot 設定頁）／0320（Outbound 設定）／0321（銀行流水鏡像，備忘）。
