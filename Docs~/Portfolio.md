---
title: 投資組合 —— 交易事件簿、開帳快照與報酬率
description: senate cmd portfolio 與 Senate「投資組合」頁共用的規則：哪些券異動會記事件、開帳快照（上線時現值＝既有持倉成本）、加權平均成本與已實現損益、帳上與紀錄不符的處理、法幣報價來源 fx_rates_per_usd。
cmds: [portfolio]
last_updated: 2026-10-07
target_audience: [AI_Agent, Tools_Maintainer]
related:
  - Coding_Standards.md | SCP 撰寫規範 | SCP_Json／原子寫入
  - <Senate>/Docs/Architecture/Voucher_System.md | 券系統 | 券簿只存狀態不存事件；本帳在券簿之外
---

# 💼 投資組合（TASK-0371）

> **券簿只存狀態、不存事件**（2026-09-18 拍板「券不記歷史」），兌換的成交率也從不落盤 ⇒ 現有資料推不出買入成本。
> 本帳在券簿**之外**另開一本只增不改的事件簿，⛔ 不改券簿 schema。
> 拍板（Tim 2026-10-01）：「沒有紀錄的話在系統層上增加紀錄（不管舊的資料）」「既有持倉可以根據新系統上線時的現值計算」。

## 1. 資料落點：住在那個人的信件夾

券跟著人跨專案走（`letters/<P>/vouchers/`），成本來源也要跟著走 ⇒ 帳住在 `letters/<P>/portfolio/`：

| 檔 | 內容 | 規則 |
|---|---|---|
| `letters/<P>/portfolio/opening.json` | 這個人的開帳快照：每種**有報價**的券的數量、當時 Bid、當時現值 | **每人只寫一次**，第二次拒絕 |
| `letters/<P>/portfolio/events/<yyyy-MM-dd>/<stamp>_<persona>_<kind>_<hex>.json` | 一筆交易一檔 | 同名拒寫、不覆蓋 |

- 金額一律 decimal、以原文數字落盤（不經 double）。
- 券名照原樣存、顯示券檔的實際 ID（例 `Gold`）；比對一律不分大小寫。
- 進 persona 信件庫的版控，由 auto-commit 的 `portfolio` 群收（見 `AutoCommit`）。

### 舊落點（資料根 `Market/portfolio/`）

不再讀寫。還有沒搬的舊紀錄時，`op=show` 與頁面會**明說**（那些紀錄沒算進來，差額會被列成「與紀錄不符」）。
搬家走 `op=migrate`（第 5 節）：只**複製**、舊檔不刪；全部搬完沒有衝突才寫標記檔 `_migrated_to_letters.json`。
⚠ 舊落點是**專案**的 ⇒ **每台機器、每個專案的資料根都要各搬一次**，各搬進**自己那個專案**的信件夾。
⛔ 不要把 A 專案的舊落點搬進 B 專案的信件夾：信件夾裡的 persona 有兩種 ——
有自己 git repo 的（submodule）跨專案共用，由 git 會合；**直接放在 AgentCommands 裡的**跟著專案分支走、券簿每個專案各一份，
收了別的專案的紀錄就會多出對不上券簿的差額（2026-10-07 實測：ame BTC −98.5）。

## 2. 誰會寫事件

| 寫入端 | 事件 | 價值 |
|---|---|---|
| `SCP_VoucherSwap`（`voucher op=swap` 與 `voucher-swap` 兩個入口都經過它） | `swap`：賣 From 買 To | 賣出量 × From 的 Bid（＝放棄的市值＝買進那一側的成本） |
| 保管費轉券（`SCP_DemurrageVoucher.Issue`） | `flow` ＋ | 數量 × 當下 Bid |
| `voucher op=grant／consume／migrate` | `flow` ＋／－ | 同上 |

- **只記有報價的券**（沒有報價就沒有成本；酒館券每則發文都會動，全記的話一則訊息一個檔）。
- 事件在券**落盤之後**才寫；沒寫成**不推翻**已成立的券異動，回傳裡會有一行 ⚠，兌換結果另有機讀值 `portfolio_logged=0`。
- ⚠ `bank op=pay` 扣酒館券不記（酒館券沒有報價）。哪天某券補上報價，之前的進出沒有紀錄 ⇒ 落在第 4 節的「與紀錄不符」。

## 3. 成本規則（加權平均成本）

- 起點＝這個人的開帳快照，成本＝當時現值（標「上線時估值」）；**早於快照時刻的事件不算**（已含在快照裡）。
- 已經有交易紀錄的人**不開帳**：他的持倉從第一筆紀錄起就有實際成本，開帳會把那些成本換成當下現值。
- 增加（獲配、買進）：數量＋、成本＋當下市值（標「實際成本」）。
- 賣出（兌換的來源側）：依平均成本扣成本；已實現＝成交市值 − 平均成本。
- 花用（consume）：依平均成本扣成本，⛔ 不算已實現（那不是賣掉）。
- 現價取 **Bid**（賣出可得）；報酬率＝未實現 ÷ 持有成本。成本為 0 時顯示「—」，⛔ 不顯示 0%。

## 4. 帳上與紀錄不符

券簿數量 ≠「快照＋事件」推出的數量時，差額**單獨列出、不計入報酬**——來源與成本都不知道，⛔ 把它當 0 成本會讓報酬率無限大。
賣出／花用超過紀錄數量的部分同理（「賣出超出紀錄」，不計已實現）。

## 5. 指令與頁面

```bash
senate cmd portfolio --arg op=show --arg persona=<P> [--arg ccy=TWD]   # 持倉與報酬
senate cmd portfolio --arg op=open                                      # 開帳試算（零寫入）
senate cmd portfolio --arg op=open --arg confirm=1                      # 開帳（每人只能一次）
senate cmd portfolio --arg op=events [--arg persona=<P>] [--arg limit=N]
senate cmd portfolio --arg op=migrate                                   # 舊落點搬家試算（零寫入）
senate cmd portfolio --arg op=migrate --arg confirm=1                   # 搬家（只複製；重跑不重複）
```

- 資料根還有沒搬的舊紀錄時，`op=open` 擋下（先 `op=migrate`，否則會替已經有紀錄的人重拍成本基準）。
- `op=migrate`：目標已有同名檔 ⇒ 內容相同算已搬、不同算衝突（⛔ 不覆寫）；某人已有快照 ⇒ 時間相同算已搬、不同算衝突（⛔ 不重拍）；信件夾裡沒有這個人 ⇒ 跳過並列出。

Senate「銀行 › 投資組合」頁（`senate ui --page portfolio`）：工具列選 persona（預設清單第一位）與顯示幣別；「開啟資料夾」開那個人的 `portfolio/`；
問題（例：還有沒搬的舊紀錄）列在表格上方。純讀，⛔ 沒有開帳鈕（只能做一次的事不放在每天會開的頁面上）。

⚠ **部署順序**：開帳要在**新版 Server 上線之後**才拍。舊版 Server 處理的兌換／發券不寫事件，快照之後就會出現不符的差額。

## 6. 法幣報價（`fx_rates_per_usd`）

報價模型本來就支援任意幣別（Symbol ＋ Bid/Ask ＝ 1 單位值多少 USD）；本單只補自動刷新的解析器：

- 回應形如 `{"result":"success","base_code":"USD","rates":{"TWD":32.1,"JPY":149.3,…}}`（例：`https://open.er-api.com/v6/latest/USD`，免金鑰）。
- 「1 USD 兌多少」⇒ 解析時**取倒數**；取哪一格由報價自己的 Symbol 決定（一個 URL 餵所有法幣）。
- 只有中間價 ⇒ Bid ＝ Ask、`two_sided=false`，成本由手續費承擔。
- 拒收：底幣不是 USD（倒數單位會錯，而數字看起來合理）、表裡沒這個幣、`result` 不是 success、非正數。

```bash
senate cmd rate --arg op=set --arg symbol=JPY --arg bid=<USD/JPY> --arg ask=<同>     # 先建報價
senate cmd rate --arg op=source --arg symbol=JPY --arg url=https://open.er-api.com/v6/latest/USD --arg kind=fx_rates_per_usd
senate cmd rate --arg op=sync --arg symbol=JPY --arg force=1
```

- 1 張法幣券＝1 單位貨幣。單位很小 ⇒ 兌換結果若讓目標券簿永久券**超過 int 上限**（2,147,483,647）會被拒絕（例：200 BTC → KRW）。
- `rate op=set` 與匯率頁手動改價時**保留**既有的抓取端點（以前會清掉，自動刷新從此靜默停止）。
