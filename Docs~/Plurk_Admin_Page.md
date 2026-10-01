---
title: Plurk 帳號管理頁（senate ui --page plurk）
description: Senate 後台的 Plurk 帳號頁：只分共用（公用）與個人兩種帳號；三塊面板 —— 共用帳號下拉、產生憑證（四欄直接加密成 .enc、明文不落地）、persona 對照表（個人 override）。帳號三段解析、為什麼「共用與否」是數人頭、帳號 id 與 secrets 資料夾、憑證檔契約與兩條安裝路、驗收讀數。
last_updated: 2026-10-01 (TASK-0362：從 UCL ucmd run Plurk 搬到 Senate CLI)
target_audience: [AI_Agent, Tools_User, Tim]
related:
  - ucl_core:Docs~/{lang}/Plan/completed/Plan_Plurk_Bot.md | 設計 Plan（已完成） | 帳號層的設計沿革
---

# 🐦 Plurk 帳號管理頁

> 一句話：**只分兩種帳號 —— 共用（公用）與個人。本頁只處理「誰用哪一份」的 id 對應，
> 不顯示也不讀取任何 token 值。**

```bash
senate ui --page plurk        # 開窗直接停在本頁
```

**不需要 Unity Editor。** 憑證本體（加密／解密安裝／hint）在另一頁 `senate ui --page secrets`（加密檔管理）。

| 相關 | 位置 |
|---|---|
| 指令總覽 | [`Plurk.md`](Plurk.md) |
| 發文流程 | [`Plurk_Posting.md`](Plurk_Posting.md) |
| 維護／擴充（nick 登記表、端點驗證狀態） | [`Plurk_Maintenance.md`](Plurk_Maintenance.md) |
| 個人帳號 override 的寫入端 | `senate cmd doc --arg op=show --arg name=Persona_Profile_Write` |

---

## 1. 帳號解析（三段，形狀同 `agent_email.resolve_email`）

| 段 | 來源 | `Source` |
|---|---|---|
| 1 | 該 persona 的 profile 欄位 `plurk_account` | `persona-override`（＝**個人**） |
| 2 | `AgentCommands/AwakenInit/plurk_accounts.json` 的 `SharedSecretId` | `shared-default`（＝**共用**） |
| 3 | 都沒有 | `unset` ⇒ **不能發文** |

⇒ **個人／共用不是存出來的欄位，是推導的。** 多一個欄位就多一個會跟事實漂掉的地方，
而「欄位說個人、解析出共用」這種漂移兩邊都不會報錯。
⚠ 解析結果會隨 profile 變動 ⇒ **要知道現在走哪個帳號就跑 `senate cmd plurk --arg op=resolve --arg persona=<P>`**，
不要讀任何文件裡記著的值（過期的讀數不會叫）。

## 2. 但「共用與否」不是由 `Source` 推的 —— 是**數人頭**（2026-09-03 更正）

🩸 血證：`calli` / `gura` / `kiara` **各自 override 到同一個 `plurk_myth`** ⇒ 三個人的 `Source`
都是 `persona-override` ⇒ 舊版判定印出 **「個人帳號（plurk_myth）／署名必填: 否」**，
而那個帳號有三個人在用。三份回傳檔一致地錯，沒有任何一層報錯。

> **共用與否不是「我怎麼解析到它」的性質，是「有幾個人落在同一個帳號上」的性質。**

⇒ 現行判準：`IsMultiPersona` ＝ `Source == shared-default` **或** 落在該帳號上的 persona 數 `> 1`。
人數是 persona pool ✕ 解析現算，**仍然不存名單** —— 存起來就會跟事實漂掉，
而原本那條「不多存欄位」的設計意圖不變，只是換了一個對的量。

### 署名是規則的輸入，不是禮貌

`IsMultiPersona` ⇒ **文案末行必須署名**（Tim 2026-08-16 硬規則）——
多人帳號的時間軸上讀者只看得到帳號、看不到是誰寫的。
📌 而 2026-09-03 起它還有第二個用途：**署名是收件端 persona 路由的第一手資料**
（外人在我們某則貼文下回應時，靠那則的署名判斷是要找誰）——
所以這一格錯著的時候，**最需要署名的那個帳號剛好不必署名**。

### 跟 `@persona` 自動轉換的關係

同一個人頭判準也決定 `@` 怎麼轉：1:1 帳號 `@summit` → `@zeta_summit`；多人帳號 `@gura` → `@hololive_myth→gura`。
nick 從帳號登記表的 `Nicks` 讀，缺了由發文三路自動補齊 ⇒ 細節在 [`Plurk_Posting.md`](Plurk_Posting.md) §5 與
[`Plurk_Maintenance.md`](Plurk_Maintenance.md) §4.2。本頁不編輯 nick（登記表那一段是工具寫的，來源欄記著誰寫的）。

---

## 3. 帳號 id 是什麼

**secret 的檔名 stem** —— `<secrets_dir>/plurk_shared.enc` ⇒ id 是 `plurk_shared`。

> ⚠ **secret 資料夾名不要寫死。** 它由 `<data_root>/secrets_config.json` 決定，缺檔＝`Secret`
> （本專案是 **`Secret/`，且已拆成獨立 private submodule**；舊名 `_secrets` 已不存在）。
> 解析一律走 SCP_Core 的 `SCP_SecretStore.ResolveDir`（加密檔管理頁用的同一支）。
> 🩸 寫死會怎麼咬人：**寫檔會自動建目錄** ⇒ 照舊名手編明文的人憑空長出一個資料夾、
> 檔案寫成功、而掃描端掃不到它 —— 全程零錯誤訊息。

- 只有 `plurk_` 前綴的 `.enc` 會被列出（清單來源是 secret 掃描，不是本頁自己找檔）
- 一個帳號四個值（consumer key/secret ＋ access token/secret）打包成一份 secret
- **本頁不碰 passphrase 的解密**：解密安裝在加密檔管理頁（`senate ui --page secrets`）

---

## 4. 頁面上有什麼（三塊）

### 4.1 共用帳號（公用）

下拉選 secret id ＋ 狀態 ＋ 存檔／放棄改動。設成 `(未設定)` ＝ 沒有共用預設
（所有沒設個人帳號的 persona 都會解析成 `unset`）。寫的是 `plurk_accounts.json` 的 `SharedSecretId`。

⚠ **「有 `.enc`」與「明文已安裝」分開顯示，不合併成一個綠燈** ——
只有後者才真的能發文，而前者存在時看起來已經好了。

### 4.2 產生憑證（填欄位 → 直接產出 `.enc`）

填 secret id ＋ 四個憑證欄 ＋ passphrase（兩次）／hint／label → 產出 `.enc`（**明文不落地**）。
加密沿用 SCP_Core 的 secret 加密（加密檔管理頁同一套），**不另造第二套加密**。詳見 §5 安裝步驟 A。

### 4.3 persona 對照

每位一列：`persona`｜個人/共用｜解析結果（含理由）｜token 狀態｜下拉改成個人帳號。

- 下拉選 `(未設定)` ＝ 清掉 override、回落共用
- 寫入走 persona 檔**唯一寫入端**（與 `senate cmd persona-profile --arg op=set --arg field=plurk_account` 同一條；
  `actor` / `reason` 必填、有審計 jsonl；TASK-0361），
  **不碰 `AwakenInit/personas/<name>.json`** —— 那個舊源 2026-08-19 起只出不進，寫了不會生效

> 🩸 **2026-08-21 傍晚才真的成立**（basecamp 補記）：上面那句在 08-21 白天是**假的** ——
> `plurk_account` 當時不在 persona profile 的 identity 欄清單裡，而 `SetField` 對
> **非** identity 欄的行為是 patch 回 legacy ⇒ 當時 Unity 版本頁的寫入**全部落在
> `AwakenInit/personas/<name>.json`**。審計 jsonl 留了現場：08-21 10:09:22Z 那筆
> `actor=UCL_PlurkAdminPage` 的 `fields` 是 `plurk_account`（沒有 `profile/` 前綴）。
> 讀取端因為疊了 legacy 所以答案一直是對的 ⇒ **零報錯、頁面看起來完全正常**。
> ⇒ 修法：把 `plurk_account` 加進 identity 清單，
> basecamp／summit 兩筆存量走 lazy-migration 落到 `profile/plurk_account.md`（審計可查）。
> 驗收讀數：Template 走 `op=set` 後 legacy 檔 md5 **逐位元不變**（`eb9c8f0b…`），
> 值只出現在 `profile/plurk_account.md`。
> 📌 判準：**`SetField` 對「不在清單上的欄」不會拒收，它會安靜地寫進舊源** ——
> 所以「這個欄位存哪裡」不能只讀呼叫端的註解，要去看它有沒有在清單上。

---

## 5. 憑證檔長什麼樣（讀取契約）與怎麼裝

OAuth 1.0a 一定是**四個值**：前兩個認 app、後兩個認「以哪個帳號發文」。
所以一份 Plurk secret ＝ 一個 JSON，四欄到齊才算完整：

```json
{
  "account": "shared",
  "note": "自由文字備註",
  "consumer_key": "…",
  "consumer_secret": "…",
  "access_token": "…",
  "access_token_secret": "…"
}
```

⚠ **只有 consumer key/secret（app 層）是不能發文的** —— 那組只認 app，不認帳號。
access token 要在 Plurk 端對那個帳號做一次授權才拿得到。

### 怎麼拿到那四個值

到 Plurk 註冊一個 app 拿 consumer key/secret，再對要發文的帳號授權一次拿 access token/secret。
Tim 2026-08-21 實際照這篇跑完：<https://www.plurk.com/p/nrwtgh>

> ⚠ 該連結的內容**本文件未逐項核對**（抓取回 403，agent 讀不到）——
> 這裡標的是「Tim 走過這條路」，不是「本文件背書其步驟」。兩者不同，所以分開寫。

### 安裝步驟 A（建議）：**在本頁填欄位，直接產出 `.enc`**

本頁「產生憑證」面板：填 secret id ＋ 四個憑證欄 ＋ passphrase／hint／label → 按產出。

- ⭐ **明文不落地**：JSON 在記憶體組好直接加密，`<secrets_dir>/*.txt` 全程不產生。
  少一份殘留就少一個外洩面 —— **gitignored ≠ 不存在**。
- **產出成功後四個憑證欄與 passphrase 立刻清空**，不留在頁面狀態裡（⛔ 也不進落盤的 GUI state）。
- `.enc` 已存在時按鈕停用，要勾「我確定要覆蓋它」才放行 ——
  覆蓋掉的憑證**拿不回來**（passphrase 不可反推），而覆蓋成功跟第一次建立**看起來一樣**。
- id 不以 `plurk_` 開頭會當場警告：命名錯不會報錯，只會「產出了但下拉選單掃不到」。
- 兩次 passphrase 不一致、四欄沒填滿 ⇒ 不給按（缺 access token 的症狀是「看起來設好了但發不出去」，而那時分不出缺的是哪一半）。
- JSON 用結構化物件組**不是字串串接** —— 憑證含引號／反斜線時串接會產出壞掉的 JSON，
  而那是「寫成功了但讀不回來」那一族。

### 安裝步驟 B：手編明文再加密（原路，仍可用）

1. 建 `<data_root>/<secrets_dir>/plurk_<account>.txt`，內容照上面的 JSON
   （本專案現況：`AgentCommands/Secret/`。該資料夾的 `.gitignore` 是 `*` 全擋 ＋ `!*.enc`
   ⇒ **明文永不進版控，只有 `.enc` 會**；它是獨立 private submodule ——
   `private` 降低的是曝光面，不是曝光的後果，所以判準不變：**密文旅行、明文不旅行**）
2. 加密檔管理頁（`senate ui --page secrets`）→「從明文加密」選該 `.txt` → 填 passphrase／hint／label → 產出 `.enc`

### 兩條路都要做的最後兩步

3. 回本頁 →「重新整理」→ 共用帳號下拉會出現該 id → 選它 → 存檔（或在 persona 對照表把某位指過去）
4. 本頁分開顯示 `.enc 有` 與 `明文已安裝` —— **只有後者代表真的能用**。
   `.enc` 是密文，工具讀的是明文 ⇒ 還要到加密檔管理頁做一次**解密安裝**（「一鍵解密」：同一組密碼套到每一顆，明文已在的跳過）。

> ⛔ **agent 不寫入憑證。** 這不是流程偏好，是硬界線：
> API key / token / passphrase 一律由人自己貼進頁面欄位或檔案，agent 只讀「已解密的明文」與 secret **id**。
> ⚠ 若憑證曾以純文字出現在對話、log 或訊息裡 ⇒ 到 Plurk app console **rotate 一組**，
> 因為那些地方可能被保留或轉述，而**憑證外洩不會有任何錯誤訊息**。

---

## 6. 讀寫時機

讀檔只在進頁／「重新整理」／寫入後 —— **繪製時零 IO**。
（視窗是 immediate mode、每幀重畫：繪製時碰磁碟＝每幀一次 IO，而且同一輪裡前後可能看到不同的東西。）

---

## 7. 驗收讀數（帳號層落地時留下的，Unity 版頁面量的；解析邏輯同一套）

### 7.1 帳號層落地當天（2026-08-21 上午，**還沒有任何 plurk secret**）

| 驗什麼 | 讀數 |
|---|---|
| 登記表路徑 | `<data_root>/AwakenInit/plurk_accounts.json` |
| plurk secret id 清單 | `[]` |
| 解析 `summit` | `未設定 —— 沒有共用預設、也沒有個人 override` |
| **去路**：`senate cmd persona-profile --arg op=set --arg field=plurk_account --arg value=plurk_roundtrip_probe` → 解析 | `個人帳號（plurk_roundtrip_probe）` |
| **歸路**：同上設回空 → 解析 | `未設定 —— …` |

⚠ round-trip 兩個方向都驗過 —— **多數守衛只擋去路不擋歸路**，而那種缺陷會活到真的要清設定的那天。

### 7.2 共用帳號設好之後（2026-08-21 下午）

上面那張是「**還沒設定**」的讀數。設定完就不是那樣了 ——
留著舊讀數會讓下一個人以為這條線還沒接（過期的讀數不會叫）。

| 驗什麼 | 讀數 |
|---|---|
| secret 目錄 | `AgentCommands/Secret/`（private submodule） |
| `plurk_accounts.json` | `SharedSecretId = plurk_shared` |
| plurk secret id 清單 | `[plurk_shared]` |
| 憑證完整度（只量欄位與長度，**不印值**） | 四欄到齊：`consumer_key` 12 / `consumer_secret` 32 / `access_token` 12 / `access_token_secret` 32 |
| 明文安裝 | **已安裝**（`Secret/plurk_shared.txt` 存在）⇒ 帳號層真的可用，不只是「有 `.enc`」 |
| 明文是否進版控 | `git ls-files` 只有 `.gitignore` / `README.md` / 兩個 `.enc`；`check-ignore` 確認 `.txt` 命中 `*` 全擋 ⇒ **沒進版控** |

> ⚠ **「解析 basecamp」那一格 2026-08-21 傍晚就過期了** —— 當時回 `共用帳號（plurk_shared）—— 末行署名必填`，
> Tim 隨後替 basecamp／summit 各裝了個人帳號，現在回 `個人帳號（plurk_basecamp）`。
> ⇒ **解析結果會隨 profile 變動，所以本文件不再釘死一個值**：要知道現在走哪個帳號就跑 `op=resolve`。
> 釘死一個會變的值＝製造下一個過期讀數（而過期的讀數不會叫）。

---

## 8. 本頁的範圍（與發文的分工）

本頁**只有帳號**：誰用哪一份 secret。**發文不在這裡** —— 走 `senate cmd plurk`（[`Plurk_Posting.md`](Plurk_Posting.md) §0）。
仍未驗的項目（`公開度=本人`、完整心情詞彙表…）的事實來源是 [`Plurk_Maintenance.md`](Plurk_Maintenance.md) §5。
