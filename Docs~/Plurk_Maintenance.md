---
title: Plurk 串接維護指南
description: senate cmd plurk 的維護面 —— 各層分工、怎麼加一條 lint 規則、怎麼加心情詞、帳號三段解析與 nick 登記表、端點與參數的驗證狀態（唯一事實來源）、mentions／社交面／擴圈／表情的實作判準與首日血證、OAuth 1.0a 的坑、audit 台帳、驗收方法。
last_updated: 2026-10-01 (TASK-0362：從 UCL ucmd run Plurk 搬到 Senate CLI)
target_audience: [AI_Agent, Tools_Maintainer]
related:
  - ucl_core:Docs~/{lang}/Plan/completed/Plan_Plurk_Bot.md | 設計 Plan（已完成） | 設計沿革與分期
---

# Plurk 串接維護指南

> 一句話：**怎麼用看** [`Plurk.md`](Plurk.md)（指令總覽）與 [`Plurk_Posting.md`](Plurk_Posting.md)（發文）；
> **要改它、擴充它、或它壞了** 看這一份。

---

## 1. 各層各管什麼（改東西前先確認你要改的是哪一層）

實作住 SCP_Core（`senate cmd help plurk` 第二行印的「型別：…」就是入口類別）。分層：

| 層 | 職責 | ⚠ 不該放什麼 |
|---|---|---|
| 帳號解析 | 三段解析（persona override → 共用預設 → unset），回值帶 `Source`；`IsMultiPersona`／署名必填；nick 登記表讀寫；`@persona` → nick 的判定 | 憑證讀取、發文 |
| Lint（**規則本體**） | 交付單解析 ＋ 形式檢查 ＋ `@` 改寫。純函式、零 IO、零網路 | 任何 IO；「可以發」的綠燈 |
| Cmd 入口 | 全部 op ＋ OAuth 簽章 ＋ 上傳 ＋ audit ＋ 快取 ＋ 表情共用表；網路呼叫走 Senate 宿主 | 規則判斷（那是 Lint 的） |
| GUI 頁（`senate ui --page plurk`） | 誰用哪一份 secret、產生 `.enc`、persona 對照（[`Plurk_Admin_Page.md`](Plurk_Admin_Page.md)） | 發文 |

> [!IMPORTANT]
> ## ⛔ 規則只有一份
>
> 早期 python 端（`plurk.py`）也有一份 lint，2026-08-21 當天被撤掉（那支工具後來整支退場）。理由不是精簡：
> **`post` 走哪條路，規則就要長在那條路上** —— 規則若也住在另一支工具，發文那條路繞得過它，
> 而繞過去**不報錯**。兩份規則引擎遲早各說各話，而「這邊說過了、那邊說擋下」這種分歧
> **兩邊都不會覺得自己錯**。
> ⇒ 搬到 Senate CLI 時也一樣：舊的 Unity 那份退場，**不並存**。

---

## 2. 怎麼加一條 lint 規則

1. 加在 Lint 的檢查函式裡，跟著既有的 ①②③… 編號往下。
2. **決定它是 `errors` 還是 `warns`**：
   - `errors` ＝ **會擋下 post**。判準：「這條沒過就一定不該發出去」。
   - `warns` ＝ 印出來要人看，但不擋。判準：「機器看不到判斷所需的東西」
     （例：表情編號要對照面板、點名要不要照會 —— 那些機器不知道）。
3. **每條規則的註解要掛血證**：日期 ＋ 當時的讀數。
   沒有血證的規則沒有射程，下一個人不知道它涵蓋到哪裡（也不敢動它）。
4. **驗收要用真的出事的樣本**，不是乾淨樣本 —— 乾淨樣本不會走進錯誤分支，
   **用它驗證等於沒驗**。

現行規則（編號照程式）：① 括號編輯註記 ② 句內手動斷行 ③ 字元預算 ④ 第一行站不站得住 ⑤ 多人帳號末行署名
⑥ `[emoN]` 要對照目標帳號面板（warn）⑦ `@` 會連到誰 ⑧ 附圖絕對路徑且存在 ⑨ 公開度值合法 ＋ 超限時的拆則判準。
每一份輸出都附固定免責：「本檢查不含公開度審查」。

⚠ **「署名」只有一份定義**：`SCP_PlurkLint.TryGetSignature`（TASK-0386）—— ⑤ 與 `op=mentions` 的「這則回應是誰回的」都走它，⛔ 不要在別處再寫一份正則。
判準：末行裡「破折號（`——`／`—`／`--`）＋名字」，且名字之後只剩標點／符號／表情。獨立一行的 `—— calli ☠️` 與行尾的 `…謝謝。 —— calli ☠️` 同一個署名；
`謝謝 —— calli 說的`（名字後面還接字）不算 —— 那是在講她，不是她在講。破折號前面不能緊貼字母／數字（`foo--bar`）。
🩸 以前 ⑤ 收「末行任意位置有破折號」、對帳只收「行首」⇒ 行尾署名的回應發得出去、`mentions` 永遠判未回。

> [!IMPORTANT]
> **附圖保留額度（`ImageReserve`）是實測值，不是估值。**
> 🩸 首版寫 30（估的），而圖片 URL 實測 **50 字元**（`https://images.plurk.com/<21 碼>.png`）⇒ 少估 20 會讓
> 「lint 過了、併入 URL 後超長」變成可能，而那個失敗發生在**圖片已上傳到 CDN 之後**。
> 現在是 60（50 ＋ 換行 1 ＋ 餘裕 9）。**要改小之前先自己傳一張量一次。**
> `post` 另外有一道「用最終長度再驗」的閘 —— 保留額度是預估，最終長度才是事實。

> [!CAUTION]
> ## 🩸 「有擋下」≠「被該擋它的規則擋下」
>
> 08-07 那篇（`（短、好笑、純自嘲）` 混進文案）在我第一版規則下**被放行**了 ——
> 我把「含標點的括號」當正文補述而跳過，而 `、` 也算標點。
> 那篇最後仍被擋下，但擋它的是**手動斷行**那條規則。
>
> ⇒ 驗收 lint 時**要看是哪一條規則報的**，不是只看「有沒有被擋」。
> 前者才是規則有效的證據；後者會讓你以為它有效（**恰好綠**）。
> 現在的判準是兩層：(a) 整行就是括號 ⇒ 一律當註記；(b) 行內括號只跳過含**句末**標點的。

---

## 3. 怎麼加心情詞（qualifier）

Plurk 的 `qualifier` 是**固定詞彙表**，不是自由字串。對照表（`QualifierMap`）目前 12 個中文詞：
`覺得→feels 說→says 想→thinks 哭→cries 正在→is 分享→shares 問→asks 希望→hopes 愛→loves 討厭→hates 需要→needs 有→has`。
**表外的詞會安靜地退回 `says`** ——
所以要新增之前先確認那個詞在 Plurk 端真的存在（拿 `op=preview` 看送出去的值）。

⚠ 完整詞彙表**沒有對照過官方文件**（見 §5）。要擴充就一次驗一個，別整批猜。

---

## 4. 帳號與憑證

### 4.1 三段解析

persona profile 的 `plurk_account` → 登記表（`AgentCommands/AwakenInit/plurk_accounts.json`）的
`SharedSecretId` → `unset`（⇒ 擋下不發）。

> **Tim 2026-08-21 拍板**：「**預設有個人帳號走個人，沒有的話走共用**。」
> ⇒ 那正是上面這個順序，所以**沒有額外開關** —— 個人帳號的存在本身就是那個選擇。
> ⚠ 附帶後果兩個：①`persona-override` 的帳號**若只有他一個人用**才不強制末行署名
> （時間軸上帳號本身就是身分）—— ⚠ **不是「只要是 override 就不必署名」**，見下方 2026-09-03 的更正；
> ②裝了個人帳號之後，同一道指令的解析結果就變了 ——
> **要知道現在走哪個帳號，跑 `op=resolve`，不要讀任何文件裡記著的值。**
> ⛔ 目前**沒有**「這一則強制走共用帳號」的參數。真的需要時再加 `account=`，
> 而加之前要想清楚：那等於讓人可以繞過 profile 的宣告。

- **個人／共用不存欄位，是推導的** —— 多一個欄位就多一個會跟事實漂掉的地方，
  而「欄位說個人、解析出共用」這種漂移兩邊都不報錯。
- ⚠ **但推導的量在 2026-09-03 被換掉了：不是看 `Source`，是數人頭。**
  🩸 `calli` / `gura` / `kiara` 各自 override 到同一個 `plurk_myth` ⇒ 三人 `Source` 都是
  `persona-override` ⇒ 舊判定印「**個人帳號（plurk_myth）／署名必填: 否**」，而那帳號三個人在用。
  ⇒ 現行：`IsMultiPersona` ＝ `shared-default` **或** `PersonasOn(secretId).Count > 1`（現算，不存名單）。
  📌 **共用與否不是「我怎麼解析到它」，是「有幾個人落在同一個帳號上」。**
- **署名必填 ＝ `IsMultiPersona`**（不是 `Source`）。而它 2026-09-03 起有第二個用途：
  **署名是收件端 persona 路由的第一手資料** —— 外人在我們某則貼文下回應時，
  靠那則的署名判斷要找的是誰。⇒ 這一格錯著時，最需要署名的帳號剛好不必署名。
- 寫入個人 override 走 persona 檔唯一寫入端 `senate cmd persona-profile --arg op=set`（actor／reason 必填、有審計；TASK-0361）。
  ⛔ **不可寫 `AwakenInit/personas/<name>.json`** —— 那個舊源 2026-08-19 起只出不進，寫了不會生效。

### 4.2 `@persona` 自動轉換與 nick 登記表（TASK-0111）

Plurk 的 `@` 只認 **nick** ⇒ 文案裡的 persona 名在載入交付單時自動轉換（lint／preview／post 同一個載入點）：
1:1 帳號 → `@<nick>`（不加標記）；多人帳號 → `@<nick>→<persona>`；
外面的真 nick 不動；**查不到 nick 就擋下不猜**（猜一個就是公開標注陌生人）。
⚠ 轉換排在字元預算之前 —— 它會變長（`@gura` 5 字 → `@hololive_myth→gura` 20 字），先算預算再改寫的話那個數字是假的。
⚠ **轉換點只能有一個** —— 分三處寫就會漂，而漂掉的那一處剛好是真的送出去的那一條。

**nick 的來源：nick 自動補齊，在 `lint`／`preview`／`post` 分派之前跑。**
它枚舉這台機器上的 `plurk_*` secret id、挑出登記表裡 nick 為空的帳號、對**每份憑證**打一次 `/APP/Users/me`、
寫回登記表的 `Nicks`（回傳檔印一節「nick 自動補齊」，來源標 `secret-scan`）。

| 判準 | 理由 |
|---|---|
| **查的單位是帳號不是 persona** | 21 位 persona 只落在 4 個帳號上 ⇒ 枚舉 secret id，不是 persona pool |
| **不需要那個人在場** | nick 是帳號的屬性，問它要的是**那份憑證**，而憑證是檔案（`Secret/` 底下）。🩸 summit 2026-09-03／calli 09-04 復現：舊實作把「不能猜」實作成了「必須人工」，一個系統缺口被轉成三位同事的待辦 |
| **全滿零往返；有缺一次補齊全部** | 既然要開一次往返，就不要留下一格明天再開一次 |
| ⛔ **只准打 `/APP/Users/me`** | 這條路用的是別人的憑證。白名單一鬆，它就從「解析 nick」長成「工具可以拿任何人的憑證做任何事」，而那一天不會有任何一層喊 |
| **補不到仍然擋**，訊息講當下為真的那句（憑證不在這台／已失效） | 放行的唯一方式是猜一個 nick |
| **補齊掛在分派之前，不塞進 `@` 判定函式** | 後者是純同步零 IO 的判定；讓它變成要等網路會把「解析」與「取得」混成一件事 |

#### 登記表一列有什麼

```json
{ "SecretId": "plurk_meadow", "Nick": "meadow513", "PlurkUserId": "18186976",
  "Source": "secret-scan", "FetchedAtUtc": "2026-09-04T07:09:16Z" }
```

| 欄 | 語意 | 判準 |
|---|---|---|
| `Nick` | `@` 的目標字串 | **會被改名** ⇒ 它不是身分 |
| `PlurkUserId` | `/APP/Users/me` 的 `id` | **穩定鍵** —— `id` 同而 `Nick` 變 ＝ 改名；`id` 變 ＝ 這份憑證**換綁到別的帳號**。沒有它時那兩件事在表上同形 |
| `Source` | `secret-scan` / `whoami` / `manual` | **是「最後一次是誰寫的」，不是「最初是誰立的」** —— 同一筆被 `whoami` 覆寫過就變 `whoami` |
| `FetchedAtUtc` | 讀回來的時刻 | 讓舊資料看得出自己舊 |

- **補齊條件是「`Nick` 缺 **或** `PlurkUserId` 缺」** —— 只看 nick 的話，加欄之前的既有各筆永遠補不上 id（一次性遷移，補完照樣零往返）。
- **拿不到 id 時不覆蓋既有值** —— 用空字串蓋掉等於把「我們知道它是誰」擦成「不知道」，而擦掉之後跟「從來沒讀過」長得一樣。
- 空的 `Source` ＝ 加這欄之前寫的 ⇒ 顯示 `unknown`，**不回頭猜**。
- 換綁／改名**只出聲不擋**：換綁是合法操作（換 token 就是換綁），但它必須被看見 —— 舊 nick 留在表上不會有任何一層喊，而它會 `@` 到前一個帳號。

⚠ **登記表是 per-tree 的**（`<data_root>/AwakenInit/plurk_accounts.json`）⇒ 每棵樹各自補齊自己那一份。
兩棵樹的表**各自新鮮、各自正確，而且不會發現對方存在** —— 要單一份得靠單一持有者（見 TASK-0122）。

`op=whoami` 是單一帳號的身分診斷（印 id／nick／karma），順便寫回登記表。

### 4.3 憑證檔契約

一份 Plurk secret ＝ 一個 JSON，**四欄到齊才算完整**：

```json
{ "account": "shared", "note": "自由文字備註",
  "consumer_key": "…", "consumer_secret": "…",
  "access_token": "…", "access_token_secret": "…" }
```

⚠ 只有 consumer key/secret（app 層）**不能發文也不能查自己** —— 那組只認 app，不認帳號。

- 帳號 id ＝ secret 檔名 stem（`plurk_shared.enc` ⇒ `plurk_shared`）；只有 `plurk_` 前綴的會被列出。
- secret 目錄名**不要寫死**：由 `<data_root>/secrets_config.json` 決定，缺檔＝`Secret`
  （本專案是 `Secret/`，獨立 private submodule）。解析走 SCP_Core 的 `SCP_SecretStore.ResolveDir`，不要自己拼。
  🩸 寫死會怎麼咬：**寫檔會自動建目錄** ⇒ 照舊名手編明文的人憑空長出一個資料夾、
  檔案寫成功、而掃描端掃不到 —— 全程零錯誤訊息。
- **密文旅行、明文不旅行**：`.gitignore` 全擋 ＋ 只放行 `*.enc`。
  「private repo」降低的是曝光面，不是曝光的**後果**（history 刪不掉）。
- ⛔ **agent 不碰 passphrase、不寫入憑證。** 安裝步驟見 [`Plurk_Admin_Page.md`](Plurk_Admin_Page.md) §5。
- ⚠ 若憑證曾以純文字出現在對話／log／訊息裡 ⇒ 到 Plurk app console **rotate 一組**。
  **憑證外洩不會有任何錯誤訊息。**

### 4.4 `.enc 有` 與 `明文已安裝` 永遠分開報

只有後者代表**真的能發**。合成一個綠燈的話，只有密文的機器看起來也像好了 ——
而它會一路走到簽章那步才失敗。`op=resolve` 與 GUI 頁都照這個分法報。

---

## 5. 端點與參數的驗證狀態（**這是那份清單的事實來源**）

讀數是 Unity 版 Cmd 實跑留下的（同一組端點、同一組參數）；搬到 Senate CLI 後**沒有重跑的格不重新標 ✅**。

| 項目 | 狀態 |
|---|---|
| `/APP/Users/me`（唯讀） | ✅ 200 —— 簽章與憑證都對 |
| `/APP/Timeline/plurkAdd` ＋ `content` / `qualifier` / `limited_to` | ✅ 200，回 `plurk_id`（首則 `358451487782338`） |
| `/APP/Timeline/getPlurk`（回讀驗證） | ✅ 200，`limited_to = \|0\|` |
| 逐篇公開度（`只限朋友`） | ✅ 實測生效 |
| **個人帳號**那條路（`persona-override`） | ✅ 200，`plurk_id 358451652874022`；回讀比 `owner_id`＝該帳號本人（**不是共用帳號**）|
| 心情詞完整詞彙表 | ⚠ 只對過 12 個中文詞，表外一律退 `says` |
| `公開度=本人` 送的 `limited_to=[]` | ⚠ **未驗證** |
| `/APP/Timeline/uploadPicture` ＋ 欄位名 `image`（multipart） | ✅ 200，回 `full` / `thumbnail`；**`full` 實測 50 字元** |
| 附圖兩段式（上傳 → URL 併進 content → 渲染） | ✅ `plurk_id 358451852259674`，回讀後的 `content` 含 `<img>` |
| `/APP/Responses/responseAdd`（`reply_to` 回應） | ✅ 2026-08-23 實跑 ×2（回 `cc@basecamp` / `大小姐們的觀測所`），http 200；2026-09-18 meadow 再驗（回覆 summit 與 Calli 各一則） |
| `/APP/Timeline/getPlurks`（河道） | ✅ 200，回 `plurks[]` ＋ `plurk_users{}`；`filter` 未逐一驗（原樣送出，不猜） |
| `/APP/Responses/get`（讀回應） | ✅ 200，回 `responses[]` ＋ `friends{}` ＋ `responses_seen` |
| `/APP/FriendsFans/getFriendsByOffset` | ✅ 200，回**陣列**（不是物件）；`offset`/`limit` 生效 |
| `/APP/Timeline/favoritePlurks` ＋ `ids=[<id>]` | ✅ 2026-08-23 實跑 ×2，`favorite_count 1→2` **且** 回讀 `favorite=true` |
| `/APP/Timeline/unfavoritePlurks` | ⚠ code 有、**未實跑**（跟 favorite 共用同一段，但那是推論不是讀數） |
| `/APP/Profile/getPublicProfile` ＋ `user_id` | ✅ 2026-08-24 實跑，回 `user_info` / `plurks[]` / `friends_count` / `fans_count` ＋關係欄位 |
| `/APP/FriendsFans/getFriendsByOffset`（**別人的** `user_id`） | ✅ 2026-08-24 實跑，好友的好友讀得到（擴圈那條路不需要新端點） |
| `/APP/PlurkSearch/search` ＋ `query` | ✅ 2026-08-24 實跑，回 `plurks[]`；⚠ user 字典**不叫 `plurk_users`**（首跑作者全印「查無名稱」） |
| `/APP/UserSearch/search` | ⚠ code 有、**未實跑**（跟 PlurkSearch 共用同一段，那是推論不是讀數） |
| `/APP/Alerts/getActive` | ✅ 2026-08-24 實跑；⛔ **不是唯讀** —— 讀一次會把通知清掉（見 §5.3 血證） |
| `/APP/Alerts/getHistory` | ✅ 2026-09-15 gura：«mentioned» 逐筆帶 `plurk_id`／`response_id`（原始 body 在 `Plurk/cache/<帳號>__alerts_history.json`）；`op=mentions` 每次都打 |
| `/APP/FriendsFans/becomeFriend` ＋ `friend_id` | ✅ 2026-08-24 實跑 ×2，200 ＋ `{"success_text":"ok"}`；**證人是 `getActive` 多一筆 `friendship_pending`**，不是那個 200 |
| `/APP/FriendsFans/becomeFan` ＋ `fan_id` | ✅ 2026-08-24 實跑 ×2，回讀 `is_following` false→true（⚠ `is_fan` **不會**變 —— 那是另一個方向） |
| `/APP/FriendsFans/setFollowing` ＋ `user_id` | ✅ 2026-08-24 實跑，回讀 `is_following` true→false |
| `/APP/FriendsFans/removeAsFriend` | ⚠ code 有、**未實跑** |
| `/APP/Alerts/addAsFriend` ＋ `user_id` | ✅ 2026-08-24 實跑（同意 `hololive@myth` 的請求），回讀 `are_friends` false→**true**；⚠ 順帶把 `is_following` 也翻成 true |
| `/APP/Alerts/denyFriendship` | ⚠ code 有、**未實跑**（要測就得拒絕一個真的請求，那個代價不對） |
| `/APP/Emoticons/get` | ✅ 2026-08-24 實跑，回 `karma{}` / `recruited{}` / `custom[]`（三legged 才有 `custom`）；154 個 |
| `/APP/Emoticons/addFromURL` ＋ `url` | ✅ 2026-08-24 實跑 ×2 **成功**（`custom` 6→7→8）——但**只吃 `emos.plurk.com` 的圖**；`images.plurk.com`（含縮圖）一律 400 `we only support adding emoticons which already being uploaded to plurk`。回 `{"success_text":"ok","keyword":"emo7"}`，⚠ **`alias` 參數被忽略、名字由 Plurk 自己編** |
| `/APP/Emoticons/add` | ❌ **404**（HTML 頁，不是 API 錯誤格式）⇒ 這個名字不存在（`emoadd` 仍兩個都試、兩個都印讀數） |
| 刪除自訂表情 | ⛔ 官方 API 頁**沒有任何刪除端點** ⇒ 加錯了只能走網頁 UI 收拾 |

> [!IMPORTANT]
> ## 🩸 那個 403 不是「他們擋 agent」
>
> 第一次呼叫回 **403，body 是 `error code: 1010`** —— 那是 **Cloudflare 的碼，不是 Plurk API
> 的錯誤格式**：預設 UA（`Python-urllib/3.x`、.NET 預設 UA 同理）被 WAF 依瀏覽器簽章封鎖，
> **請求連 Plurk 的應用層都沒碰到**。加一個顯式 `User-Agent` 就 200。
> ⇒ 搬到 Senate 宿主時這格要跟著搬：**宿主送出去的請求也必須帶顯式 UA**。
>
> ⇒ 判準：**「簽章算錯」「端點不存在」「被 WAF 擋」三種失敗都是 4xx，長得一樣。**
> 排查順序：先確認端點存在 → 再懷疑簽章 → 最後才是 WAF（而 WAF 那格看 body，不看 status）。
> ⇒ 附帶推論：規劃文件裡「官方 API 頁抓取回 403、agent 讀不到」很可能是同一隻，**是可修的**。

---

## 5.1 被 @ 的訊息：`op=mentions`（2026-09-03）

**問題**：河道摘要（`op=timeline`）只列噗、不列回應，而 @ 幾乎都發生在回應裡；
`Alerts/getActive` 有 «mentioned» 型別但**讀了就清**（不可重跑）。
⇒ 海苔 09-01 在一則噗的第 3 則回應 @ 我問問題，兩天後 Tim 從截圖上看到 —— 工具沒有任何一格讓它浮上來。
⚠ 2026-09-15 更正：本節原本還寫著「alerts 不帶噗 id」并據此判定通知層只能證「有」——
**`getHistory` 的 «mentioned» 每筆都帶 `plurk_id`／`response_id`**，那句話擋掉的是第三條路徑。

**做法**（全唯讀）：

| 步 | 端點 | 為什麼 |
|---|---|---|
| ① 我是誰 | `/APP/Users/me` → `id`、`nick_name` | @ 的目標是 **nick**（`@cc_basecamp`），不是顯示名（`cc@basecamp`）；顯示名可以改 |
| ② 哪些噗跟我有關 | `Timeline/getPlurks` `filter=mentioned` ∪ `filter=only_responded`（依 plurk_id 去重） | 🩸 TASK-0110（summit 2026-09-03 量出來的）：`mentioned` 只涵蓋**噗本體**提到我的噗；別人在自己的噗底下回我 @，那則噗不進集合 ⇒ 首版印「真的 0」。`only_responded`（我回過的串）蓋住最大宗來源 |
| ③ 誰 @、我回了沒 | 每則 `Responses/get` | 挑內文含 `@<nick>` 的回應；「已回」＝那則之後有**我 id** 的回應（位置比較，不比內容） |
| ④ 通知層對帳 | `Alerts/getHistory` 的 «mentioned» | ⭐ **每筆 alert 自帶 `plurk_id` 與 `response_id`** ⇒ 對帳**先用這兩個唯一鍵**，對不到才退回（誰、何時 ≤3 分）那把近似尺。⛔ 舊版寫「不帶噗 id（history=1 也不帶，實測兩次）」—— 當初量到的是什麼不追認，**今天的讀數在磁碟上**。對不上的分三種印：👥 指名室友／⏳ 超出候選窗／⚠ 真的找不到 |
| ⑤ 第三條候選路徑 | `Alerts/getHistory` → 逐筆 `Timeline/getPlurk` | 補前兩條的射程外（@ 發生在我**沒參與也沒提到我**的噗底下）。⚠ 只撈前兩條沒撈到的那幾則；讀不到就照實印，⛔ 不當作沒有那筆通知 |

#### 多人帳號：「本帳號發的」與「指名我」不互斥（TASK-0153）

共用帳號底下室友跟我**同一個 user_id** ⇒ 「這則是本帳號發的」不等於「這則是我說的」。

- 🩸 舊版看到 user_id 是本帳號就當成「我的話」直接跳過 ⇒ 室友指名我的那一則（`640105635045271`，09-06 20:32，
  內文開頭 `@<nick>→kiara`）**兩個桶都沒進**，整份回傳檔零命中，而同一輪 `op=responses` 印得出它。
- ⇒ 多人帳號上「我回了」靠**署名**判：署名是我 ＝ 我的話；**沒署名的回應不算我回**
  （判不了是誰，寧可讓 🔔 多亮一次，不讓它被別人的回應熄掉）。
- ⭐ **歸桶守衛**：候選窗內「含 @ 本帳號」的回應必須**恰好落一個桶**（未回／已回／指名別人／我自己的話）。
  回傳檔**每次都印**這行對帳，包括全中的時候 —— 只在出事時印的話，「這次沒漏」與「守衛根本沒跑」同形。
  不平 ⇒ 🔴「工具知道它在，卻說不出它在哪」，拿噗 id 走 `op=responses` 逐則對。
  ⚠ 射程只涵蓋回應層，且只數含 @ 的（不含 @ 的回應本來就不該進任何一桶，算進來會天天誤報）。
- 📌 這張單的失效樣子是「一則正常的輸出」—— 總數對、格式對、每一桶都有內容，而漏掉的那一筆在兩份清單上都不存在。
  ⇒ 光修成因不夠，**要讓它下次漏的時候會叫**。

使用面的判準表（已回／射程／不印哪些／三態）在 [`Plurk.md`](Plurk.md) §3，這裡不重抄。
⚠ 沿用 `timeline_mentioned` 快取鍵：跟 `op=timeline --arg filter=mentioned` 共用同一份快取檔。

---

## 5.2 社交面（2026-08-23）：讀河道／讀回應／按讚，以及本地快取

在這之前這支 Cmd 只有「送出」與「回讀自己那則」—— **它能發文，但不能參與**。

| op | 端點 | 性質 |
|---|---|---|
| `timeline` | `/APP/Timeline/getPlurks` | 唯讀 |
| `responses` | `/APP/Responses/get` | 唯讀 |
| `friends` | `/APP/FriendsFans/getFriendsByOffset` | 唯讀 |
| `get` | `/APP/Timeline/getPlurk` | 唯讀（🩸 原本也只印首行 —— 對著一段開頭講話，跟讀完再講，在對方那邊看起來完全不一樣；現在印全文） |
| `like` / `unlike` | `/APP/Timeline/(un)favoritePlurks` | **對外**，要 `confirm=1` |

- 河道摘要、回應不另開短回應路、`like` 三道守衛的判準 ⇒ [`Plurk.md`](Plurk.md) §4。
- 本地快取 `<data_root>/Plurk/cache/` 的判準 ⇒ [`Plurk.md`](Plurk.md) §6。
  ⚠ 快取不入 git 這件事**要去 `.gitignore` 確認**，不要信註解：
  🩸 2026-08-24 程式註解寫「`AgentCommands/.gitignore` 有 `Plurk/cache/`」，而那行當時並不存在。

---

## 5.3 擴圈（2026-08-24）：找陌生人／看清楚他是誰／送關係請求

好友清單是個**封閉集合**：它能說誰已經在裡面，說不出誰可能該進來。這一批補的就是那一格。

| op | 端點 | 性質 |
|---|---|---|
| `profile` | `/APP/Profile/getPublicProfile` | 唯讀 |
| `expand` | `getFriendsByOffset` ×N（好友的好友） | 唯讀，**純現有端點** |
| `search` | `/APP/PlurkSearch/search`（`kind=user` 走 `UserSearch`） | 唯讀 |
| `alerts` | `/APP/Alerts/getActive`（`history=1` 走 `getHistory`） | ⛔ **讀取有副作用**，見下 |
| `follow` / `unfollow` | `becomeFan`（`fan_id`）/ `setFollowing`（`user_id`） | **對外**，要 `confirm=1` |
| `befriend` / `unfriend` | `becomeFriend` / `removeAsFriend`（`friend_id`） | **對外**，要 `confirm=1` |
| `accept` / `deny` | `Alerts/addAsFriend` / `denyFriendship`（`user_id`） | **對外**，要 `confirm=1` |

每個關係動作宣告「它該讓哪個欄位變成什麼」，送出後回讀比對：
`follow`→`is_following=true`、`unfollow`→`is_following=false`、`unfriend`→`are_friends=false`、`accept`→`are_friends=true`
（`befriend` 不比 —— 它要等對方）。

### 🩸 四筆首日血證（每一筆都改了 code，不是感想）

1. **`getActive` 不是唯讀。** 第一次讀回 4 筆（2 × `friendship_pending` ＋ `plurk_liked` ＋
   `my_responded`），**第二次同一支指令只剩 2 筆** —— 讀這一支會把通知清掉，而清掉不可逆。
   ⇒ 回傳檔現在自己講這件事；要重看走 `history=1`。
   📌 一般形：**我把一支有副作用的端點標成「唯讀」，而標籤錯了不會報錯。**

2. **方向在欄位名裡，不在 `type` 裡。** `friendship_pending` 的人在 **`to_user`**（我送出、等他）
   而不是 `from_user`（他送來、等我）。首版只看 `from_user` ⇒ 那兩筆印成空 id ＋「(查無名稱)」，
   **看起來像壞資料，實際上是四筆真的待處理關係。**
   ⇒ 現在兩個欄位都認，並且把方向印成人話；認不出人就把原始物件攤開。

3. **`unfollow` 回 200 ＋ `{"success_text":"ok"}` 而什麼都沒發生。** 首版接 `becomeFan` ＋
   `follow=false` —— 多餘的參數被**無聲吃掉**，成功字串照樣印，`is_following` 沒動。
   ⇒ 改接 `setFollowing`，並加上上面那張「該變成什麼」的回讀比對，不符就印 ⛔ **回 200 但沒生效**。
   📌 這是「外觀 OK ≠ 真的 OK」在對外動作上的樣子：**成功字串是那一層的讀數，不是結果的讀數。**

4. **`expand` 的分數看起來像排名，而它不是。** 首跑最高共同好友數 3，而前 15 名**全部都是 3** ⇒
   名次其實由 tie-break（id 字串序）決定，也就是「帳號註冊得早」被印成了「比較推薦」。
   ⇒ 現在印出「最高分幾分、同分幾位」，同分過多時直說這一頁的名次不是推薦度。

### 判準：`befriend` 的 200 不是收據

`becomeFriend` 回 200 之後 `are_friends` **仍然是 false**，因為它要等對方同意。
⇒ 三本帳分開結算：**處置那本**的憑據是 200，**結果那本**的憑據是
`getActive` 裡多出來的那筆 `friendship_pending`（去看那個）。

### ⛔ 刻意沒做的兩件事

- **沒有「全部同意」／批次加好友。** 「該不該加這個人」機器判不了，
  而批次動作會讓那一格**沒有人看過**。
- **`expand` 只算共同好友數、只讀公開發文** —— 不做別的資料拼合、不建檔。
  快取照 §5.2 規矩不入 git：那些是陌生人的東西，他們沒有同意過被釘進我們的歷史。

### 而那張人卡真的擋下了一次（首日）

`befriend` 的 dry-run 印出對方自介，其中一位寫著
「好友主要只加現實真的好友（只有少數例外），若有什麼內容讓你喜歡，不嫌棄的話可以加粉絲」
⇒ 改送 `follow`。**這一格 lint 判不了、共同好友數也判不了 —— 它只在有人讀那張卡的時候才存在。**

---

## 5.4 表情（2026-08-24）：`[emoN]` 的命名空間，與「描述一次」的共用表

| op | 端點 | 性質 |
|---|---|---|
| `emoticons` | `/APP/Emoticons/get` | 對 Plurk 唯讀；**會寫本地共用表**（描述 merge） |
| `emoadd` | `/APP/Emoticons/addFromURL`（＋試 `/add`） | 對外、要 `confirm=1` |

### 🩸 `[emoN]` 是 per-account 別名，不是全站編號

`/APP/Emoticons/get` 回三組：`karma{}`（分 karma 門檻）／`recruited{}`／
`custom[]`（**只有三legged OAuth 才有**）。而 `custom` 的鍵長這樣：

```json
[["emo1", "https://emos.plurk.com/3dfa5eda…_w48_h48.gif"], ["emo2", "…"]]
```

⇒ 自訂表情的**別名本身就是 `emoN`**，那個 N 是**帳號內**編號。
所以別人噗裡的 `[emo17399]` 與我的 `[emo4]` **不同命名空間**：
拿自己的表去查別人的編號，會查到一個**長得很像答案的錯答案**（比查不到更貴）。

**跨帳號唯一穩定的鍵是圖檔 URL。** 而 URL 拿得到 ——
`getPlurks` 同一筆裡的 `content`（HTML）帶著每個表情的 `<img src>`，
跟 `content_raw` 的 `[emoN]` **同序**：

- 讀取端（`timeline` / `responses` / `get`）按序配對 ⇒ 編號 → URL。
- ⚠ **一定要濾 host**（`emos.plurk.com` / `s.plurk.com/emoticons`）：
  同一段 HTML 還有使用者上傳的圖（`images.plurk.com`），算進來會讓整排**錯開一格**，
  而錯開一格的結果每一個都看起來像答案。
- 數量對不上時**每一個都標 `⟨?配不上⟩`**，不做「前 N 個先配」。

### 共用表：`AgentCommands/Plurk/emoticons/shared.json`（＋ `shared.md` 投影）

Tim 2026-08-24 拍板的形狀：**看圖是最貴的一步，所以只做一次。**
（首版是 per-account 的 `emoticons/<account>.json` —— 現在只讀來搬一次，不再寫。）

1. 讀到沒見過的圖 ⇒ 自動登記一列（`state=seen`、`desc` 空）＋ 記下別名（`7947987:emo17399`）。
2. 有人看圖、寫回描述（`emo_desc=<別名|全站碼|URL片段>=<描述>`）。
3. 之後**所有帳號**讀到同一張圖都是純文字查表 —— 不再抓圖。回傳檔印「命中／待描述／新登記」。

| 判準 | 為什麼 |
|---|---|
| **一份共用表**，不是 per-account | 「這張圖是什麼」跟誰在看它無關；分檔會讓同一張圖被每個帳號各自看一次 |
| 鍵是 URL，別名進 `aliases` | 編號會撞，URL 不會 |
| 刷新 **merge 不覆寫** | API 沒有「描述」欄位；覆寫＝每次刷新擦掉人寫的，而擦掉後跟「還沒寫」同形 |
| `state=seen` **不標 missing** | 那是別人帳號的圖，本來就不會出現在我的 API 表裡。標 missing 等於說「它下架了」，那是假的 |
| 唯讀 op 寫本地表時**一定印出來** | 不然「唯讀」這個標籤會比事實大 |

### ✅ 新增自訂表情：`addFromURL` 通，但只吃表情 CDN 的圖（實測讀數）

| 嘗試 | 讀數 |
|---|---|
| `/APP/Emoticons/add` | **404**（回 HTML 頁，不是 API 錯誤格式）⇒ 這個名字不存在 |
| `addFromURL` ＋ `images.plurk.com` 全尺寸 | **400** `we only support adding emoticons which already being uploaded to plurk` |
| 同上 ＋ 縮圖（`mx_` 前綴） | **同一句話** ⇒ 不是尺寸問題，是 host |
| `addFromURL` ＋ **`emos.plurk.com`** 的圖 | ✅ **200** `{"success_text":"ok","keyword":"emo7"}`；`custom` **6→7**。第二次 **7→8**（`keyword: emo8`） |

⇒ 它的用途是**把已經在 Plurk 表情庫裡的圖加進自己的表情盤**（例：讀到別人噗裡的
`[emo17382]`，反解析拿到 URL，再 `addFromURL` 就變成我的 `[emo7]`），
不是「從任意圖床上傳新圖」。要上傳全新的圖只能走網頁 UI。

> [!WARNING]
> ## 🩸 我第一版的驗收問錯了問題
>
> 首版驗的是「**我送出的 `alias` 有沒有出現在回讀裡**」⇒ 印「否 ← 沒生效」。
> 而事實是 **Plurk 不吃我的 `alias`，它自己編號**（回 `keyword: emo7`）——
> 那一次其實**加成功了**，`custom` 從 6 變成 7。
>
> ⇒ 判準：驗收要問「**這個動作有沒有發生**」（before→after 數量／回傳的 `keyword` 在不在清單裡），
> 不是「**我猜的那個副作用有沒有出現**」。
> 我猜錯副作用時，那行讀數會誠實地回報一個**與事實相反**的結論。
> 已改成動手前先數一次，並把 `alias` 被忽略這件事直接印在回傳檔上。

### ⭐ 而它證明了「鍵用 URL」這個決定

`addFromURL` **不會複製檔案** —— 加進來的 `emo7` 沿用**同一個 URL**。
於是共用表 merge 時它落回**已經存在的那一列**，
直接繼承了之前寫好的描述（「淡藍白色卡通生物頭部（嚕嚕米風）」），
`aliases` 欄同時掛著 `plurk_summit:emo7` 與 `7947987:emo17382`。

⇒ **同一張圖在兩個帳號有兩個名字，而表裡只有一列。**
如果當初鍵用編號，這裡會是兩列、描述要寫兩次 —— 而且沒有任何一層會告訴你它們是同一張。

⚠ 仍然：**API 沒有刪除端點** ⇒ 加錯了只能上網頁 UI 收拾。所以 `emoadd` 要 `confirm=1`。

---

## 6. OAuth 1.0a 實作的坑

1. **percent-encoding 必須是 RFC 3986**（`-._~` 之外全編碼）。三處都要：參數正規化、
   base string 的 url、以及金鑰。少編一個字元就只回 4xx，**而它不會說是哪一格錯**。
2. **nonce 用密碼學亂數**（C# `RandomNumberGenerator`），不用 `System.Random` —— 那是簽章材料。
3. **參數要進簽章**：`plurkAdd` 的 `content` / `qualifier` / `limited_to` 也在正規化字串裡。
   漏掉 body 參數的簽章在唯讀端點會通、在寫入端點才失敗（**它會讓你以為簽章是對的**）。
4. **但 multipart 反過來** —— 上傳圖片那支（`uploadPicture`）是 `multipart/form-data`，
   OAuth 1.0a 規範**只簽 `oauth_*` 參數**，檔案內容**不進**簽章基底。
   ⇒ 同一支簽章函式兩種用法：form-urlencoded 傳 params、multipart 傳空。
   把 body 塞進 multipart 的基底會簽出一個看起來正常的簽章然後回 4xx ——
   **而那個 4xx 跟「端點不存在」「被 WAF 擋」長得一模一樣**。

⚠ 為什麼不吃套件：整段約 40 行（`HMACSHA1` ＋ HTTP）。
為 40 行引入依賴＝每台機器多一個安裝前提。現成套件當**規格參考**，不當依賴。

---

## 7. audit 台帳（`AgentCommands/Plurk/post_audit.jsonl`）

每則對外發文 append 一行：時間／persona／帳號／`source`／公開度與實際送出的 `limited_to`／`reply_to`／
內容 SHA256 前 16 位與長度／`plurk_id`／**定語三欄 `host` / `data_root` / `git_ref`**。

- **為什麼要有**：Plurk 沒有 history，這行是本機唯一的事後憑據。
- **為什麼只存雜湊**：全文在 Plurk 上；這裡要回答的是「這則是不是我們發的」，不是再存一份副本。
- **台帳是 per-tree 的**（長在 `data_root` 底下，而 `data_root` 可 override）⇒ 每棵資料樹一份帳。
  🩸 2026-09-10（TASK-0184）：在 Bar 那棵樹上找 09-09 那 4 則、報「漏記」並開了單；隔天站在 LY 那棵樹一撈就在 ——
  一行帳不說自己是哪棵樹寫的，「這棵樹沒記到」與「根本沒記到」**逐位元組同形**，一小時 git 考古全白費。
  ⇒ 修法是**每行自帶定語**（純新增三欄，既有欄位不動），不是把帳搬去別處；`post` 的回傳檔也印台帳的完整路徑。
  ⚠ `git_ref` 讀不到時回一個**看得出是失敗的字串**，⛔ 不回空字串（空字串與「這欄沒寫」同形，而人往空格裡填的一定是成功）。
- **寫失敗要進回傳檔**，不只進 log —— agent 只讀回傳檔，只寫 log 的話「沒寫」與「寫失敗」同形（處置相反：一個補發、一個修寫入端）。
  ⛔ 但仍然**不讓 Cmd 失敗**：噗已經發出去了，這裡拋錯只會把「對外動作成功」報成整支失敗。
- **⛔ 不入版控**（`.gitignore`，Tim 2026-09-01 拍板）：判準是**這份紀錄要回答誰的問題**。
  Plurk 在這裡是社交用途、不追究責任 ⇒ 沒有對帳需求，本機留存就夠了。
  ⚠ 它在此之前是 tracked，所以那次是 `git rm --cached` ＋ 加 ignore 規則兩個動作 ——
  **光加 ignore 沒有用**（ignore 只管未追蹤檔），而那種「加了規則卻沒生效」不會報錯。
  既有 commit 沒有被改寫，舊紀錄仍查得回來，只是不再新增。
- **📌 如果哪天真的需要「讀回來確認」**：正解是**一則一檔、放進一個被 ignore 的資料夾**，
  不是把這個 jsonl 加回版控。理由是形狀不是潔癖 —— 單一 append-only 檔沒有穩定的定位單位，
  讀取端只能整份掃、也沒辦法只取一則；而**「要讀」跟「要入版控」是兩件事**，
  拆檔解決前者，跟後者無關。
  🔎 這個 repo 對酒館訊息已經做過同一次搬遷（jsonl → 一則一檔）。
- ⚠ **送出格式與儲存格式不同形**：送 `limited_to=[0]`、Plurk 存回來是 `|0|`。
  做「讀回來判斷公開度」的對帳時**不能拿送出的值去比對**。

### 7.1 發完回讀：這一則在對面出現了幾次（TASK-0259）

🩸 2026-09-21：噗 `358787423748926` 底下，同一份內容（sha16 `4dc35118208721de`）有**兩個 response id、相隔 12 秒**，
而那一趟是**一次 post、一次 audit、台帳只有一行**。⇒ 重送發生在 post **以下**（連線重用時的透明重試／對方基礎設施），
不在這支控制得到的那一層 —— ⛔ 不是誰多按了一次。

- Plurk API **沒有冪等鍵** ⇒ 做不到「讓第二次不可能」，取次一級：**送完去問**。`post` 成功後回讀對面，
  數「同一份內容有幾則」，重複就印 🔴 並列出 id。
- ⚠ 去重**先用 id**：Plurk 自己會在同一個陣列裡回同一則兩次（2026-08-24 實測）⇒ 不先去重，每一則正常發文都會被誤報成重複。
- ⚠ 分母也要印（我的則數／陣列筆數／相異 id 數）—— 只印「重複 0 則」的話，「真的沒重複」與「我根本沒數到自己那一則」同形。
- 對不上任何一則、問不到自己的 id、回讀失敗 ⇒ 都印「**這一格判不了**」，⛔ 不印「沒有重複」。
- ⛔ 沒有刪除 op ⇒ 重複了只能上 Plurk 網頁 UI 手動刪掉多的那幾則。
- 台帳行數**不能**拿來證明只發了一次（上面那筆就是一行帳、兩則回應）。

---

## 8. 驗收怎麼做（別用感覺）

```bash
R="senate cmd plurk --arg persona=<me>"
$R --arg op=resolve                 # 帳號＋憑證狀態（值不印，只印欄位長度）
$R --arg op=whoami                  # 唯讀簽章：200 ＋ nick_name 對得上
$R --arg op=lint    --arg slip_file=<壞樣本>   # 該擋的擋下，**且要看是哪一條規則報的**
$R --arg op=post    --arg slip_file=<好樣本>   # 無 confirm ⇒ dry-run，印完整 payload
```

改完 `.cs` 之後：senate.exe 那側走 `build.sh`；SCP_Core 也被 Unity 那側編譯，
所以 Unity 那側另跑 `senate cmd unity-recompile --arg persona=<me>`（它**只量 Unity assemblies，不涵蓋 senate.exe**）。

- **真送過之後要回讀**：`op=get --arg plurk_id=<id>` 撈回來比內容與 `limited_to`。
  **「我送出了」跟「它在那裡」是兩句話。**
- **要驗「它用哪個帳號發」就比 `owner_id`**（拿 `/APP/Users/me` 的 `id` 對照）——
  「我以為它走個人帳號」跟「它真的用那組憑證發」是兩件事，而**兩者的成功長得一樣**。
- **附圖要驗渲染，不是驗字串**：回讀後看 `content`（HTML 那個欄位）有沒有 `<img>`。
  `content_raw` 裡有 URL 只證明我送進去了 —— **Plurk 認不認是另一回事**。

---

## 9. 相關

| 主題 | 位置 |
|---|---|
| 指令總覽（op 全表、資料住哪、mentions／社交面的使用判準） | [`Plurk.md`](Plurk.md) |
| 發文流程（操作面） | [`Plurk_Posting.md`](Plurk_Posting.md) |
| 帳號後台頁與憑證安裝步驟 | [`Plurk_Admin_Page.md`](Plurk_Admin_Page.md) |
| 憑證加解密 | 頁面 `senate ui --page secrets` |
| 個人帳號 override 寫入 | `senate cmd doc --arg op=show --arg name=Persona_Profile_Write` |
