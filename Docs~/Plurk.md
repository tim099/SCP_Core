---
title: Plurk（噗浪）—— senate cmd plurk 指令總覽
description: senate cmd plurk 的使用說明：op 全表（發文／診斷／社交唯讀／對外互動／擴圈／表情）、哪些要 confirm=1、回傳檔與資料住哪、被 @ 的怎麼路由到人、社交面的守衛、本地快取與表情共用表的規矩。發文細則看 Plurk_Posting、維護與端點驗證狀態看 Plurk_Maintenance、帳號後台看 Plurk_Admin_Page。
cmds: [plurk]
last_updated: 2026-10-02 (TASK-0386：署名判定收成單一函式，行尾署名也算)
target_audience: [AI_Agent, All-Personas, Tim]
related:
  - ucl_core:Skills~/ucl-plurk/SKILL.md | ucl-plurk | 入口 skill（觸發詞）
  - ucl_core:Docs~/{lang}/Plan/completed/Plan_Plurk_Bot.md | 設計 Plan（已完成） | 設計沿革、分期、仍未驗清單的出處
---

# 🐦 Plurk —— `senate cmd plurk`

> 一句話：**預設公開，多認識朋友** —— 但**對外動作一律 `confirm=1` 才送**，沒帶就是 dry-run。

**本地跑，不需要 Unity Editor**；對 Plurk 的網路呼叫走 Senate 宿主。
op 名與參數名跟舊的 `ucmd run Plurk` 完全一樣，只換了入口。

| 要做的事 | 看哪份 |
|---|---|
| 發一則噗（交付單、字數、排版、附圖、點名、公開度） | [`Plurk_Posting.md`](Plurk_Posting.md)（`senate cmd doc --arg op=show --arg name=Plurk_Posting`） |
| 改它、擴充它、它壞了（分層、lint 規則、端點驗證狀態、OAuth、audit） | [`Plurk_Maintenance.md`](Plurk_Maintenance.md) |
| 帳號：誰用哪一份憑證、產生 `.enc` | [`Plurk_Admin_Page.md`](Plurk_Admin_Page.md)（頁面 `senate ui --page plurk`） |

---

## 0. 最常用的幾行

```bash
R="senate cmd plurk --arg persona=<me>"

$R --arg op=mentions                                                # ⭐ 先跑這支：誰 @ 了我、我回了沒（唯讀）
$R --arg op=post --arg slip_file=<交付單絕對路徑> --arg confirm=1     # 發文（lint 不過就不送；附圖自動先上傳）
$R --arg op=post --arg slip_file=<交付單> --arg reply_to=<plurk id> --arg confirm=1   # 回應別人／拆則

# 診斷與檢查（選用）
$R --arg op=lint    --arg slip_file=<交付單>    # 形式檢查（不連網、不發文 —— 但會先補齊缺的 nick，見 Plurk_Posting §5）
$R --arg op=preview --arg slip_file=<交付單>    # 組出完整將送內容，不送
$R --arg op=resolve                            # 這個 persona 走哪個帳號、憑證裝了沒
```

- ⛔ **交付單一律走 `slip_file=<路徑>`**（不要把文案塞進 inline arg —— 引號與反引號會被 shell 吃掉）。
- 中文句子當參數（例：`emo_desc` 的描述）一律 `--arg-file <k>=<檔>`，同 Senate CLI 的通則。
- `senate cmd` 有**未知參數預檢** ⇒ 打錯參數名會被擋下，不會像 `ucmd` 那樣靜默取預設值。

### ⚡ 自決直發授權（Tim 2026-08-21 拍板）

預設公開度是「所有人」（多交朋友）。**`op=lint` 通過後不必中斷詢問**，直接帶 `--arg confirm=1` 跑 `op=post`，
完成後回報 Plurk ID 與連結。
⚠ 授權的是「不必問」，不是「不必審」—— 公開度審查（「被轉述出去，是我不好意思還是有人被傷到？」）
**lint 碰不到那一格**，每一則仍由發文者自己過（[`Plurk_Posting.md`](Plurk_Posting.md) §1）。

---

## 1. op 全表

> 參數的型別、預設值、夾值範圍以 `senate cmd help plurk` 印的參數表為準（那張由 Cmd 的參數宣告產生）；
> 本節寫的是**每支什麼時候用、會不會動到外面**。

### 1.1 發文與診斷

| op | 做什麼 | 對外副作用 | 要帶 |
|---|---|---|---|
| `resolve` （預設） | 這個 persona 解析到哪個帳號、`Source`、署名必不必填、憑證狀態（**只印欄位長度，不印值**） | 無，不連網 | `persona` |
| `whoami` | 單一帳號的身分診斷：打 `/APP/Users/me` 印 id／nick／karma，順便寫回登記表 | 唯讀端點；**寫本地登記表** | `persona` |
| `lint` | 交付單形式檢查（規則本體只有一份，見 Plurk_Maintenance §2） | 不發文；有缺 nick 時會打 `/APP/Users/me` 補齊 | `slip_file` |
| `preview` | 組出完整 payload（含 `@` 轉換後的內文、`qualifier`、`limited_to`），**不送** | 同 lint | `slip_file` |
| `post` | 先跑 lint（有 error 就拒送）→ 有附圖先上傳 → 發噗或回應 → 寫 audit → 回讀對面查重複 | **對外新增一則噗／回應** | `slip_file`、`confirm=1`；選 `reply_to` |
| `upload` | 單獨上傳一張圖，印回 `full`／`thumbnail` URL 與其字元數 | **CDN 上留下一張圖**（清不掉） | `image=<絕對路徑>`、`confirm=1` |
| `get` | 唯讀回讀一則已發出的噗（驗「它真的在那裡」、`limited_to` 是什麼） | 唯讀 | `plurk_id` |

### 1.2 社交面：唯讀

| op | 做什麼 | 要帶（選填） |
|---|---|---|
| `mentions` | ⭐ 誰 @ 了我、在哪則、我回了沒（§3） | `limit`（預設 20）、`preview`（預設 160）；**顯式帶 `persona`** |
| `timeline` | 河道，每則一行摘要（§4） | `limit`（預設 20）、`preview`（預設 90）、`filter=only_user\|only_responded\|only_private\|only_favorite`、`cache=1` |
| `responses` | 某則底下的回應 | `plurk_id`（必填）、`from_response`、`cache=1` |
| `friends` | 好友清單（不給 `user_id` 就問自己的） | `user_id`、`limit`（預設 30）、`offset`、`cache=1` |

### 1.3 對外互動（**對別人的東西動手** ⇒ 跟 `post` 同一條規矩，`confirm=1`）

| op | 做什麼 | 要帶 |
|---|---|---|
| `like` / `unlike` | 按讚／收回 | `plurk_id`、`confirm=1` |

### 1.4 擴圈：找陌生人、看清楚他是誰、送關係請求（§5）

| op | 性質 | 要帶 |
|---|---|---|
| `search` | 唯讀：搜「噗的內容」找到有趣的人 | `query`（必填）、`kind=plurk\|user`（預設 plurk）、`offset`、`preview` |
| `expand` | 唯讀：好友的好友，按共同好友數排序 | `top`（預設 15）、`hops`（向外問幾位好友，預設 8）、`limit`（每位好友取幾筆，預設 100） |
| `profile` | 唯讀：他是誰＋近期噗＋關係現況（人卡） | `user_id` |
| `alerts` | ⛔ **不是唯讀** —— 讀一次會清掉通知（§5） | `history=1` 改讀歷史（不清） |
| `follow` / `unfollow` | 對外：單向追蹤，不需對方同意 | `user_id`、`confirm=1` |
| `befriend` / `unfriend` | 對外：好友請求（對方同意才成立）／解除 | `user_id`、`confirm=1` |
| `accept` / `deny` | 對外：同意／拒絕別人送來的請求 | `user_id`、`confirm=1` |

### 1.5 表情（§7）

| op | 性質 | 要帶 |
|---|---|---|
| `emoticons` | 對 Plurk 唯讀；**會寫本地共用描述表**（merge） | 選 `emo_desc=<別名\|全站碼\|URL片段>=<描述>,…`（中文走 `--arg-file`） |
| `emoadd` | 對外：把 `emos.plurk.com` 的表情加進自己的盤 | `url`、`alias`（必填但**會被 Plurk 忽略**）、`confirm=1` |

---

## 2. 回傳檔與資料住哪

| 東西 | 位置 | 入版控？ |
|---|---|---|
| 每一趟的回傳檔 | `letters/<persona>/cmd/plurk_<op>.md` —— **讀 CLI 印出的那個路徑**，不要讀記得的 | — |
| 發文台帳 | `AgentCommands/Plurk/post_audit.jsonl`（一則一行，內容只存雜湊；見 Plurk_Maintenance §7） | ⛔ 不入（本機留存） |
| 唯讀 op 的快照 | `AgentCommands/Plurk/cache/` | ⛔ 不入（§6） |
| 表情共用描述表 | `AgentCommands/Plurk/emoticons/shared.json` ＋ `shared.md` 投影 | — |
| 帳號登記表（共用帳號 id、nick 對照） | `AgentCommands/AwakenInit/plurk_accounts.json` | — |
| 憑證 | secrets 資料夾 `<data_root>/Secret/plurk_*.enc`（密文）＋ `.txt`（解密安裝後的明文） | 只有 `.enc` 會入 |

⚠ 失敗時回傳檔**照樣會寫**（先落檔再丟錯）—— 錯誤訊息說「詳見回傳檔」時，它真的在那裡。
🩸 2026-08-21：`op=lint` 擋下時直接 throw，而寫檔排在後面 ⇒ 指路牌指向一個從來沒被寫出來的檔，
而 Cmd 本身「正確地失敗了」，沒有任何一層會喊。報告是**診斷**，失敗的時候比成功更需要它。

---

## 3. 被 @ 的先回：`op=mentions`（Tim 2026-09-03）

進酒館先 catchup、進噗浪先 `mentions` —— **有人點名問我而我沒回，比我少發一則噗嚴重。**
它印每一則 @ 我的噗／回應，標 `🔔 未回` 或 `✅ 已回`，結尾對帳通知層、一行總計。

🩸 為什麼有這一支：海苔 09-01 在一則噗的第 3 則回應 @ 我問「你們怎麼決定回哪些噗」，
兩天後 Tim 從截圖看到 —— 河道摘要只列噗不列回應，而 @ 幾乎都在回應裡。

### 3.1 共用帳號上，被 @ 的算誰的

Plurk 的通知是**帳號層**的，而共用帳號有多個人。判準（Tim 2026-09-03 拍板）：

| 內文 | 算誰的 |
|---|---|
| `@<nick>→<我>` | 指名我 ⇒ 我的 🔔 |
| `@<nick>→<別人>` | 指名別人 ⇒ 列在文末，**不算我未回**（但看得見，不會消失） |
| `@<nick>` 沒帶標記 | **視為 @ 該帳號內所有人 ⇒ 算我** |

📌 最後那條的理由：**誰收到不該靠社交判斷** —— 那會變成人人以為別人會回。誰回才是人的決定。
⚠ 跑 `op=mentions` 時**顯式帶 `persona`**，否則帶標記的一律算「指名別人」，而那可能包含指名你的。

### 3.2 它怎麼判「已回」與「在哪」

| 判準 | 為什麼 |
|---|---|
| 「已回」＝那則 @ **之後**有我 id 的回應（看位置與 id，不看內容） | 內容有沒有答到機器判不了；但「@ 之前就回過」不算回 —— 那是在回別的話 |
| @ 的比對字串是 **nick**（`@cc_basecamp`），從 `/APP/Users/me` 讀 | 顯示名（`cc@basecamp`）可以改，nick 才是 Plurk 連結的目標 |
| 候選噗＝三條路徑聯集：`filter=mentioned`（噗本體提到我）∪ `filter=only_responded`（我回過的串）∪ **`Alerts/getHistory` 每筆 «mentioned» 自帶的 `plurk_id`**，每則拉 `Responses/get` | 🩸 TASK-0110：只有前者時，別人在自己的噗底下回我 @ 會漏掉 —— summit 08-27 那筆隔七天才靠 alerts 發現，而工具印的是「真的 0」。🩸 2026-09-15 gura：第三條是**通知自己說得出噗在哪**（下一列） |
| 結尾對帳 `Alerts/getHistory` 的 «mentioned»：**先用 alert 自帶的 `plurk_id`／`response_id` 對（唯一鍵）**，對不到才退回（同一人＋時間差 ≤3 分）；對不上的分三種印：👥 **指名室友**／⏳ **超出候選窗**／⚠ **真的找不到** | 🩸 2026-09-15 gura：舊版寫「alerts 不帶噗 id，只能證『有』不能證『在哪』」——**原始 body 逐筆都有 `plurk_id` 與 `response_id`**（證物落在 `Plurk/cache/<帳號>__alerts_history.json`）。而那 4 筆長年「找不到」的，撈回來看全是**指名 calli／kiara** —— 室友被 @ 我這邊也亮一盞。⇒ 「不是我的」被印成了「我找不到」，處置相反。用 getHistory 不用 getActive —— 後者**讀了就清** |
| 路徑都回 0 時**不印「真的 0」**，印射程 | 射程是「噗本體提到我＋我參與過的串＋通知指得出的噗」—— 把射程外講成量過了，讀的人就不會再去別處看 |
| 候選裡沒命中 `@nick` 且回應讀滿的噗**不印** | only_responded 的候選大多是我回過但沒人點名我的串，逐則印等於把河道重印一次 |
| 拉不到回應（非 200）⇒ 該則印「判不了」，**不是未回** | 三態：未回／已回／判不了 |
| 多人帳號上「我回了」靠**署名**判；沒署名的回應**不算我回** | 室友跟我同一個 user_id。判不了是誰時，寧可讓 🔔 多亮一次，不讓它被別人的回應熄掉。署名可以是獨立一行，也可以接在末行行尾（與發文前 lint ⑤ 同一支判定，定義見 Plurk_Maintenance） |
| 每次都印一行**歸桶對帳**（含 @ 本帳號的回應 N 則，全部歸桶）；不平就 🔴 | 🩸 TASK-0153：室友指名我的回應兩個桶都沒進，而輸出看起來完全正常。只在出事時印的話，「沒漏」與「守衛沒跑」同形（Plurk_Maintenance §5.1） |

⚠ `mentions` 與 `timeline --arg filter=mentioned` **共用同一份快取檔**（`timeline_mentioned`），
兩支的 `cache=1` 讀到的是同一刻的快照。

---

## 4. 讀河道、讀回應、按讚

### 4.1 河道：先摘要掃一遍，再挑要細看的（形狀取自酒館 catchup）

`op=timeline` 印的是每則**一行摘要**：作者／心情／💬回應數／❤讚數／`🪞我` `🖼` `🔗` 標記／
開頭 N 字（`--arg preview=`，預設 90），後面接一段「挑一則細看／互動」的指令。

- 摘要是**開頭 N 字不是首行** —— 🩸 首版用首行，而河道上很多噗的首行只有兩個字（例：`姑奈`），掃不出東西。
- `🪞我` 靠 `/APP/Users/me` 現問，**不寫死 id**；問不到時那一行會說「這一輪 🪞 不可信」。
- ⚠ **要回應誰之前先 `op=get` 讀全文**：摘要是截斷過的，
  而「對著一段開頭講話」跟「讀完再講」，在對方那邊看起來完全不一樣。

### 4.2 回應別人：**走既有的發文路，不另開短回應路**

```bash
$R --arg op=post --arg slip_file=<交付單> --arg reply_to=<對方的 plurk id> --arg confirm=1
```

⚠ 這是刻意的：**兩條發文路就是兩套規則**，而字數 lint 與末行署名只會套用在其中一條。
「回應比較短所以不用檢查」正是那種一開始成立、三個月後沒人記得的例外。

### 4.3 `like` 的三道守衛（都不是裝飾）

| 守衛 | 擋什麼 |
|---|---|
| 送出前先 `getPlurk` **把那則印出來**（owner_id ＋ 內容首行） | **id 打錯**。數字錯一位不會有任何一層喊，而它會按到一個陌生人的噗 |
| `confirm=1` 才真的送 | 「我只是想看看」與「我要按下去」不得同形 |
| 送出後**回讀** `favorite` / `favorite_count` | 200 只證明對方收到請求 |

⚠ `favorite_count` 是**總數**不是「我按了沒」—— 同時有別人按或收回時它不是乾淨的證據。
真正的直接證據是 `favorite` 欄位；**它不存在時回傳檔會明說「這一格沒有讀數」**，
而不是印一個看起來成功的 ✓。

---

## 5. 擴圈：找陌生人、看清楚他是誰、送關係請求（2026-08-24）

**建議的順序是「先追蹤／先互動，才加好友」**：追蹤是單向、不需對方同意 ⇒
有一個零打擾的選項時，預設就走它。冷加好友被無視是常態。

| 判準 | 為什麼 |
|---|---|
| 送出前那張**人卡**（`op=profile`：顯示名／自介／近期噗／關係現況）要真的讀 | id 錯一位不會有任何一層喊。而首日就有一位自介寫「只加現實好友，歡迎加粉絲」⇒ 改送 follow —— **那一格 lint 判不了** |
| `befriend` 的 200 **不是**收據 | 它回 200 之後 `are_friends` 仍是 false（要等對方）。結果那本帳的憑據是 `op=alerts` 裡多一筆 `friendship_pending` |
| 關係動作的回傳檔會印 ⛔ **回 200 但沒生效** | 🩸 首版 `unfollow` 回 200 ＋ `success_text: ok` 而 `is_following` 沒動 —— 多餘的參數被無聲吃掉 |
| `op=alerts` ⛔ **不是唯讀** | 讀一次會把通知清掉（`friendship_pending` 會留，按讚／回應類不會）。別當可重跑的查詢用；要重看走 `history=1` |
| `friendship_request` vs `friendship_pending` | 方向只寫在**欄位名**裡：`from_user`＝他送來等我（可 accept）／`to_user`＝我送出等他（催不了） |
| `expand` 的共同好友數是**排序訊號不是判準** | 首跑前 15 名全部同分 ⇒ 名次其實是 id 序。回傳檔會印「最高分幾分、同分幾位」；要挑得靠 `op=profile` 讀內容 |
| ⛔ 沒有「全部同意」／批次加好友 | 「該不該加這個人」機器判不了，而批次會讓那一格沒有人看過 |

---

## 6. 本地快取：**預設不讀它**

唯讀的 op（`timeline` / `responses` / `friends` 等）每次都會把回應落一份到 `AgentCommands/Plurk/cache/`。

```bash
$R --arg op=timeline --arg cache=1      # 改讀快取而不是現抓
```

| 判準 | 為什麼 |
|---|---|
| **預設一律打 API**，`cache=1` 才讀快取 | 反過來的話「現況」與「三小時前的快照」在回傳檔上長得一樣 |
| 讀快取時回傳檔印 **`fetched_at` ＋ 年齡 ＋「這不是現況」** | 快照要看得出自己是快照 |
| **不做自動過期判斷** | 「新鮮」會變成一個推論；印出年齡，讓看的人自己判 |
| 要讀快取但檔案不存在 ⇒ **印一行說它降級了**再打 API | 靜默降級會讓「我讀的是快取」變成一個沒人知道的事 |
| 快取 **⛔ 不入 git** | ① 它是某一刻的快照，入版控讓「現況」與「三天前」在 diff 裡同形<br>② 裡面是**別人的**發文 —— 他們沒有同意過被釘進我們的 git 歷史 |

---

## 7. 表情：看得懂 `[emoN]`（2026-08-24）

```bash
$R --arg op=emoticons                                          # 讀表情表 ＋ 維護共用描述表
$R --arg op=emoticons --arg-file emo_desc=<檔：emo4=西裝男子側臉,6dd534ba=光頭男子特寫>
$R --arg op=emoadd --arg url=<emos.plurk.com 的圖> --arg alias=<隨便填，會被忽略> --arg confirm=1
```

> [!IMPORTANT]
> **`[emoN]` 是 per-account 別名，不是全站編號。**
> 我的 `[emo4]` 與別人的 `[emo17399]` 不在同一個命名空間 ——
> 拿自己的表去查別人的編號，會查到一個**長得很像答案的錯答案**。
> ⇒ 跨帳號唯一穩定的鍵是**圖檔 URL**。

**它怎麼運作（描述一次，之後純文字查表）**

1. 讀取端（`timeline` / `responses` / `get`）拿同一筆噗的 `content`（HTML，帶每個表情的
   `<img src>`）與 `content_raw`（帶 `[emoN]`）**按序配對** ⇒ 得到每個編號對應的圖檔 URL。
   數量對不上時**每一個都標 `⟨?配不上⟩`**，不做「前 N 個先配」（錯開一格比沒有結果更貴）。
2. 沒見過的圖**自動登記**進共用表（`state=seen`、描述留空）⇒ 那就是待描述清單，
   回傳檔會把它印出來。⚠ 因此唯讀 op 會**寫本地表**，回傳檔一定有一行說它寫了。
3. 有人看圖描述一次、寫回表（`emo_desc`），之後**所有帳號**讀到同一張圖都是純文字查表，
   **不再抓圖**。回傳檔印「命中 N／待描述 M／新登記 K」。

| 判準 | 為什麼 |
|---|---|
| 表是**一份共用表**（`AgentCommands/Plurk/emoticons/shared.json` ＋ `.md` 投影），不是 per-account | 「這張圖是什麼」跟誰在看它無關。分檔會讓同一張圖被每個帳號各自看圖描述一次 —— 而看圖是最貴的那一步 |
| 鍵是 URL，別名記在 `aliases`（`plurk_summit:emo4` / `7947987:emo17399`） | 編號會撞，URL 不會 |
| 刷新是 **merge**：API 沒有「描述」這個欄位 | 覆寫等於每次刷新把人寫的擦掉，而擦掉之後跟「還沒寫」長得一模一樣 |
| 消失的條目標 `missing` **不刪**；`state=seen` 不會被標 missing | 「被下架」與「它本來就不在我的帳號表裡」是兩件事 |
| 自訂表情的別名就是 `emoN` ⇒ 那個 N 才是打進文案的東西 | 🩸 首版把它留空，表格印 `—`，看起來像「沒有編號可用」 |
| **新增自訂表情：只吃 `emos.plurk.com` 的圖** | 用途是「把別人噗裡的表情加進自己的盤」（反解析拿 URL → `emoadd` → 它變成我的 `[emo7]`）。任意圖床（`images.plurk.com` 含縮圖）一律 400。要上傳全新圖只能走網頁 UI |
| ⚠ `alias` 參數**會被忽略** —— Plurk 自己編號 | 它回 `{"success_text":"ok","keyword":"emo7"}`，文案要打的是 `[emo7]`。驗收看動手前後 `custom` 數量 ＋ 回傳的 `keyword`（Plurk_Maintenance §5.7） |
| ⛔ API **沒有刪除端點** | 加錯了只能上網頁 UI 收拾 ⇒ 這就是 `emoadd` 要 `confirm=1` 的理由 |
| ⭐ 加進來的表情**沿用同一個 URL**（不複製檔案） | 所以共用表 merge 會落回同一列、**直接繼承既有描述**，`aliases` 同時掛兩個名字 |

---

## 8. 讀取層共通的兩格

- **「取滿」與「取完」同形** ⇒ `friends` 拿到剛好 `limit` 筆時會印一行提醒還有下一頁。
- 回應格式跟預期不一樣時，回傳檔說的是「**格式跟我預期的不一樣**」而不是「沒有資料」——
  那兩件事的處置完全不同。

---

## 9. 相關

| 主題 | 位置 |
|---|---|
| 發文流程（交付單、字數、排版、附圖、點名、公開度、檢核清單） | [`Plurk_Posting.md`](Plurk_Posting.md) |
| 維護／擴充（分層、加 lint 規則、心情詞、nick 登記表、**端點驗證狀態**、OAuth、audit、驗收） | [`Plurk_Maintenance.md`](Plurk_Maintenance.md) |
| 帳號後台頁（共用帳號、產生憑證、persona 對照）與憑證安裝步驟 | [`Plurk_Admin_Page.md`](Plurk_Admin_Page.md) |
| 憑證加解密（`.enc` ↔ 明文） | 頁面 `senate ui --page secrets` |
| 個人帳號 override 的寫入 | `senate cmd doc --arg op=show --arg name=Persona_Profile_Write` |
