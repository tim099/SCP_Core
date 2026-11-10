---
title: Discord 串接安裝（discord-bot）
description: 沒在收／沒在送、換一台電腦、從零設定才讀：先量這台、什麼跟著資料樹走什麼不跟、Bot token 怎麼裝到這台、每台各自的 webhook、頻道對應、驗證與排查；哪幾步一定要使用者自己做、persona 該怎麼跟他說
cmds: [discord-bot]
last_updated: 2026-11-10
target_audience: [AI_Agent, Tools_Maintainer]
---

# 🔌 Discord 串接安裝

> 一句話：**開關跟著資料樹同步到每一台，Bot token 不會 —— 新裝的機器「Inbound：開」不代表在收，先量再說。**
> 參數表看 `senate cmd help discord-bot`、`senate cmd help discord-relay`。送出去的樣子與失敗怎麼降級看 `Discord_Relay`。
> 🩸 2026-11-10：一台新裝的機器 Inbound 開著、4 條對應都開、沒有 token ⇒ 別台酒館轉進 Discord 的訊息一則都沒收，
> 而當時狀態指令只印「開」。現在狀態指令會印 ⚠ 並指到本文。

## 1. 先量這台

```bash
senate cmd discord-relay                          # 收發開關、webhook、分類 → webhook；開著卻跑不起來會印 ⚠
senate cmd discord-bot                            # Bot 憑證讀不讀得到、對應表幾條、白名單（⛔ 不印 token）
senate cmd discord-relay --arg op=inbound-status  # 每個頻道的游標、最後輪詢時間、累計收進幾則、錯誤
```

| 讀數 | 意思 | 走哪條 |
|---|---|---|
| `inbound_ready = 1`、`outbound_ready = 1`，`inbound-status` 的「最後輪詢」是幾秒前 | 兩邊都在跑 | 不用裝；有問題看 §7 |
| `⚠ Inbound 開著，但這台讀不到 Bot token` | 沒在收 | 別台已經設好 ⇒ §3；Discord 端也沒設過 ⇒ §6 |
| `⚠ Inbound 開著，但沒有任何…對應` | Bot 有了，還沒說要收哪個頻道 | §5 |
| `⚠ Outbound 開著，但…webhook…` | 沒在送 | §4 |
| Inbound／Outbound 是「關」 | 本來就沒開 | 要開：`senate cmd discord-relay --arg op=switch --arg side=inbound --arg enabled=1`（`outbound` 同理） |
| `inbound-status` 的游標「（沒有）」、最後輪詢「-」 | 從來沒輪詢過（多半就是沒 token） | 看上面那幾列的 ⚠ |

- `inbound_ready`／`outbound_ready` ＝「開著，而且沒有被擋住」。開關關著是 0。
- 輪詢與送出都是**酒館 Server** 在做（背景工作，約每 2 秒一輪）。Server 沒在跑 ⇒ 兩邊都停：`senate server list`。

## 2. 什麼跟著資料樹走，什麼不跟

| 東西 | 位置 | 跟著資料樹同步？ |
|---|---|---|
| 收發開關、webhook 清單（不含 URL）、分類 → webhook | `<資料根>/ChatTavern/discord/discord_config.json` | ✅ |
| webhook URL（密文，寫死的金鑰，不用密碼） | `<資料根>/ChatTavern/discord/discord_webhooks.enc` | ✅ |
| Discord 頻道 → 酒館頻道的對應表 | `<資料根>/ChatTavern/discord/discord_channel_routing.json` | ✅ |
| 白名單（只標記、不擋人） | `<資料根>/ChatTavern/discord/discord_inbound_whitelist.json` | ✅ |
| **Bot token（密文）** | `<資料根>/Secret/discord_bot_token.enc` | ⚠ 看那棵資料樹的 `.gitignore` 有沒有放行 `Secret/*.enc`；有放行也要**在這台解密一次** |
| **Bot token（明文，程式實際讀的）** | `<資料根>/Secret/discord_bot_token.txt` | ⛔ 不進 git，每台各自要有 |
| 收訊游標、送出游標、Gateway 狀態 | `ChatTavern/discord/discord_inbound_state.json`、`discord_backfill_state.json`、`discord_gateway_status.json` | ⛔ 不進 git（每台自己的進度） |

讀 token 的順序：環境變數 `DISCORD_INBOUND_BOT_TOKEN` ＞ 明文檔。兩個都沒有 ⇒ 不輪詢。

⇒ **同一棵資料樹搬到新機器**：開關、對應、webhook 都在，只缺 token（§3）。
⇒ **另一棵資料樹（另一間酒館）接同一組 Discord 頻道**：各自登記自己的 webhook（§4）、各自有對應表（§5）；Bot 可以共用同一顆。

## 3. 把 Bot token 裝到這台（別台已經設好）

二選一。**兩條都有使用者要親手做的步驟**（§8）。

**A. 搬加密檔過來解密**（不用重新拿 token）

1. 看 `<資料根>/Secret/` 裡有沒有 `discord_bot_token.enc`。沒有 ⇒ 從設好的那台拿（那台 commit＋push `.enc`、這台 pull；或直接複製檔案）。
   ⛔ `.txt` 是明文，不搬、不進任何 git、不丟雲端或聊天軟體。
2. 使用者開後台解密：`senate ui --page secrets`（加密檔管理）→「一鍵解密全部」→ 輸入當初加密用的密碼。
   明文已經在的會跳過，⛔ 不覆寫。**這一步要在視窗裡打密碼 ⇒ agent 做不了。**
3. 量：`senate cmd discord-bot` 要印「讀得到 token」；`senate cmd discord-bot --arg op=test` 要印 Bot 的名字。

**B. 重新寫入**（手上有 token 原文，或忘了加密密碼）

1. 使用者把 token 存成一個純文字檔、把自己取的加密密碼存成另一個檔，**都放在 repo 外面**，把**兩個路徑**告訴 agent。
   ⛔ 不貼內容，也不要用 `@` 指這兩個檔（`@` 會把內容讀進對話）。
2. `senate cmd discord-bot --arg op=set-token --arg token_path=<檔> --arg passphrase_path=<檔>`
   一次寫出 `.enc` 與本機明文。已經有 `.enc` ⇒ 要加 `--arg overwrite=1`（覆寫之後舊密碼解不開新檔）。
3. 同 A 的第 3 步。

裝好之後**不用重開 Server**：它每一輪都重新看 token 在不在。接著看 §7 驗證。

## 4. 這台的 webhook（酒館 → Discord）

送出走 webhook，不靠 Bot。**每一間酒館登記自己的 webhook**：收訊只丟「本機登記過的 webhook」發的訊息，
別台 webhook 的照收 ⇒ 兩間酒館共用同一條 webhook 的話，對方的訊息會被當成自己的而收不到。

1. 使用者在 Discord：要送去的頻道 → 編輯頻道 → 整合 → Webhook → 新 Webhook → 取個認得出這間酒館的名字 →「複製 Webhook 網址」。
2. 使用者把網址存成一個純文字檔（repo 外面），把**路徑**告訴 agent。⛔ 不貼網址本身（參數會進回傳檔）。
3. 登記、綁分類、驗證：

```bash
senate cmd discord-relay --arg op=webhook-add --arg url_path=<放網址的檔> --arg label=<這間酒館的名字>
senate cmd discord-relay --arg op=webhooks                                   # 看剛登記的 id
senate cmd discord-relay --arg op=bind --arg category=<頻道分類> --arg ids=<webhook id>   # 整份取代；未分類的酒館頻道不送
senate cmd discord-relay --arg op=webhook-verify --arg id=<webhook id>
```

- 第一次有游標之前不回放歷史；要補舊訊息用 `op=backfill`（見 `Discord_Relay` 第 6 節）。
- Discord 回 401／403／404 ⇒ 那條 webhook 會被**自動停用**；確認後 `op=webhook-enable --arg id=<id> --arg enabled=1`。

## 5. 要收哪些頻道（Discord → 酒館）

```bash
senate cmd discord-bot --arg op=refresh                    # 重抓 Bot 加入的 Server／頻道清單（連網）
senate cmd discord-bot --arg op=servers                    # 看清單，找頻道 id
senate cmd discord-bot --arg op=routes                     # 現在的對應表
senate cmd discord-bot --arg op=route --arg channel=<Discord 頻道 id> [--arg room=<酒館頻道，預設 tavern>] [--arg label=<顯示名>]
senate cmd discord-bot --arg op=unroute --arg channel=<Discord 頻道 id>
```

- 酒館頻道要存在而且沒封存。
- 逐個 Server 關掉收訊：`senate cmd discord-relay --arg op=guild-inbound --arg guild=<Server id> --arg enabled=0`。
- 白名單不擋人，只把白名單外的人標成「（白名單外）」：`op=whitelist`／`whitelist-add --arg user_id=<id> [--arg display_name=]`／`whitelist-remove`。
- 頻道 id、使用者 id：Discord 設定 → 進階 → 開啟開發者模式，右鍵頻道或使用者 →「複製 ID」。

## 6. 從零開始（Discord 端也沒設過）

這一段幾乎全是使用者在瀏覽器裡做的事（§8）。

1. **建 Bot**：<https://discord.com/developers/applications> → New Application → 左側 Bot → Reset Token → 複製。
   token **只顯示這一次** ⇒ 當下存成純文字檔（repo 外面）。
2. **Bot 頁的 Privileged Gateway Intents**：打開 **Message Content Intent**。
   ⚠ 這一格本文沒有實測過「不開會怎樣」；Discord 的規定是沒開的話 Bot 讀到的訊息內容會是空的。本程式連 Gateway 時只要 GUILDS 與 GUILD_MESSAGES 兩個一般 intent，內容是用 REST 讀的。
3. **邀請 Bot 進 Server**：左側 OAuth2 → URL Generator → Scopes 勾 `bot` → Bot Permissions 勾「View Channels」「Read Message History」→ 開產生的網址 → 選 Server → 授權。
   私人頻道要另外把 Bot（或它的身分組）加進那個頻道的成員。
4. **寫入 token**：§3 的 B。
5. **webhook**：§4。
6. **對應表**：§5。
7. **開關**：`senate cmd discord-relay --arg op=switch --arg side=inbound --arg enabled=1`、`--arg side=outbound --arg enabled=1`。
8. 驗證：§7。

## 7. 驗證與排查

```bash
senate cmd discord-bot --arg op=test                       # GET /users/@me ⇒ Bot 名稱與 id（連網）
senate cmd discord-relay --arg op=inbound-peek             # 每個頻道實打一次 API：現在會收進哪幾則、略過哪幾則（⛔ 不寫酒館、不動游標）
senate cmd discord-relay --arg op=inbound-status           # 游標、最後輪詢、累計、錯誤
```

收訊真的在跑的樣子：`inbound-status` 有「Gateway：上線」、每個頻道有游標、「最後輪詢」是幾秒前；
有人在 Discord 說話之後「累計」會加一，酒館多一則 `sender_id` 以 `discord:` 開頭的訊息。

- **第一次輪詢只建立起點，歷史不回放**（狀態會註明 `baseline`）：裝好之前的訊息不會補進來，要等下一則新的。
- 別台酒館轉進來的訊息：`sender_id=discord:<對方的 webhook id>`，meta 有 `discord_peer=true`、`peer_room`、`peer_seq`。

| 看到什麼 | 原因 | 怎麼辦 |
|---|---|---|
| `⚠ Inbound 開著，但這台讀不到 Bot token` | 明文 token 不在這台 | §3 |
| Server 輸出「Discord Inbound 開著，但讀不到 Bot token ⇒ 沒有輪詢」 | 同上（同一件事的另一個出口） | §3 |
| `inbound-status` 某個頻道 `HTTP 404`、`Unknown Channel` | 那個頻道被刪了，或 Bot 不在那個 Server | 還要收 ⇒ 把 Bot 邀進去；不收了 ⇒ `op=unroute`。對應表是同步的，改之前先問使用者 |
| `HTTP 403`、`Missing Access` | Bot 在 Server 裡，但看不到那個頻道 | 使用者在頻道權限加上 Bot：檢視頻道、讀取訊息歷史 |
| `HTTP 401` | token 不對或被重設過 | §3 的 B（`overwrite=1`） |
| Gateway `close 4004` | token 不對 | 同上 |
| Gateway `close 4014` | Developer Portal 沒允許程式要的 intent | 使用者到 Bot 頁檢查 intents |
| 自己發的訊息在 Discord 上有、酒館沒有多一則 | 正常：本機 webhook 發的不收回來 | 不用處理 |
| 別台的訊息 Discord 上有、這台沒收 | 兩間酒館共用了同一條 webhook，或對方的 webhook id 也登記在這台 | 各自登記自己的（§4） |
| 訊息收進來了，內容是空的 | 多半是 Message Content Intent 沒開 | §6 第 2 步 |
| webhook 被停用 | Discord 回過 401／403／404（webhook 被刪或沒權限） | 使用者重建 webhook ⇒ §4；或確認後 `op=webhook-enable` |
| 酒館有新訊息、Discord 沒有 | 頻道沒分類、分類沒綁 webhook、`kind` 不是 chat、或訊息本來就是從 Discord 進來的 | `Discord_Relay` 第 2 節 |

## 8. 哪幾步一定要使用者做，persona 怎麼說

agent 做得了的：量狀態、`set-token`／`webhook-add`（只收路徑）、對應表、開關、驗證。
agent 做不了的，請使用者做，**一次講一步、講完等他回**：

| 步驟 | 為什麼是使用者 | 可以這樣說 |
|---|---|---|
| 在後台輸入密碼解密 `.enc` | 要在視窗裡打密碼 | 「請開 `senate ui --page secrets`，按『一鍵解密全部』，輸入當初加密用的密碼。好了跟我說一聲，我來驗。」 |
| 從別台拿 `.enc` | 檔案在另一台機器上 | 「請把另一台 `<資料根>/Secret/discord_bot_token.enc` 放到這台的 `<實際路徑>`。只要 `.enc`，不要 `.txt`。」 |
| 在 Developer Portal 建 Bot、拿 token、開 intent、邀請進 Server | 要登入使用者自己的 Discord 帳號 | 照 §6 的 1～3 一步一步念給他；提醒 token 只顯示一次，先存成檔再關頁面 |
| 在 Discord 頻道建 webhook、複製網址 | 同上 | 照 §4 的第 1 步念；請他把網址**存成檔**再給路徑 |
| 把 Bot 加進私人頻道、調權限 | 同上 | 「請在那個頻道的權限裡加上 Bot，開『檢視頻道』和『讀取訊息歷史』。」 |

- 憑證（token、webhook 網址、密碼）**只收檔案路徑**。使用者把內容貼進對話 ⇒ 請他到 Discord 重設那一顆，再照流程用檔案給。
- ⛔ 不讀、不印 `Secret/` 裡的明文；不把 token 或 webhook 網址寫進回傳檔、酒館、commit 訊息。
- 改完一定回讀：`op=test` 過、`inbound-status` 有游標、等到「累計」真的加一，才跟使用者說裝好了。
