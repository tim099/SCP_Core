---
title: Relationship（好感度）—— 8 軸情感向量、事件帳本、寫入與維護
description: senate cmd relationship 的使用說明：資料在哪（events／opinions／_current 投影）、8 軸與分數公式、update／add-opinion／show／list／rebuild 五個 op、什麼時候寫、trigger → axis_deltas 經驗值對照、維護流程與不要做的事。
cmds: [relationship]
target_audience: [AI_Agent, Developer]
---

# 💞 Relationship（好感度）

> **一句話**：好感度是**事件帳本**不是一個數字 —— 分數是由事件重算出來的投影，
> 而事件是誰在什麼時候對你做了什麼。

寫入的唯一通道是 `senate cmd relationship`（**本地跑，不需要 Unity Editor**）。
讀取端（早安 brief 的見人、`portrait-next`）讀的是同一批檔。

---

## 1. 資料在哪

```
letters/<persona>/relationship/<target>/
  _current.md               當前總值（機械生成，可刪除重建）
  _target.txt               這個資料夾屬於哪個 exact 名字
  events/<UTC 時戳>.md      一事件一檔　例：20260818T092722997Z.md
  opinions/op-<hash12>.md   一則看法一檔
```

- **一事件一檔** —— 同時更新不同對象＝兩個新檔案，git 不需要合併任何東西。
- **住在 persona 自己的櫃子裡** —— 一個人的記憶搬走時，他對別人的看法跟著走。
- **`events/` 與 `opinions/` 分開** —— 看法與向量**解耦**；並排放的話，讀的人會假設每則看法對應某次 delta，而資料裡沒有那個關聯。

### 事件檔的身分＝它發生的時刻

檔名就是 `at`（UTC 壓平），**不含內容雜湊**：檔名含 hash ⇒ 改一個錯字就換一個身分（同一件事在帳上變兩筆）；
檔名只有時間 ⇒ 修 reason 是就地編輯，帳維持一筆。
同時戳但內容不同時**不會靜默覆蓋**：另存 `-b`、回傳檔印警告，兩筆都留給人判斷。

### `_current.md` 只是投影

可以刪掉重建（`op=rebuild`）。`opening_balance` 非 null ＝ 有一段調整沒有事件紀錄（遷移反推的期初餘額），
**它不對應任何一件真實發生的事**；新寫入端不產生它。

### target 名正規化

- 有同名 persona（大小寫不論）⇒ **以 persona 的寫法為準**（`tim` → `Tim`；🩸 `Zeta`／`zeta` 收斂成小寫，因為 persona 就是小寫）
- 沒有對應 persona ⇒ **預設大寫開頭**
- 大小寫只差的兩個名字**分開存**：`_target.txt` 釘住資料夾的主人，名字對不上就換一個 `__<hash4>` 後綴的資料夾

---

## 2. 8 軸與分數

| 軸 | 權重 | 意思 |
|---|---|---|
| `trust` | 2.0 | 信任 |
| `affection` | 2.0 | 親密 |
| `respect` | 1.5 | 敬重 |
| `interest` | 1.0 | 在意 |
| `irritation` | **-2.0** | 惱怒（負權重） |
| `dependence` | 0.5 | 依賴 |
| `admiration` | 1.0 | 欣賞 |
| `loyalty` | 1.5 | 忠誠 |

每軸值域 `[-1, 1]`：逐筆累加，**最後 clamp 一次**。

```
surface_score = Σ(軸值 × 權重) / Σ|權重| × 100     → clamp 到 [-100, 100]，四捨五入
```

⚠ **分母是權重絕對值的和**（11.5，`abs(-2.0)` 也算）。
🩸 2026-08-18 第一次移植時憑印象寫成「只加正權重」，108 筆既有資料只對 20 筆；改成 abs 之後 108/108。
2026-10-01 搬到 Senate 時，selftest `RealRelationshipRecomputeMatchesEditor` 拿 LY 全部投影對拍：146/146 逐格相同。

| 分數 | tier |
|---|---|
| ≥ 51 | 信任 |
| ≥ 11 | 在意 |
| ≥ -9 | 普通 |
| ≥ -49 | 冷淡 |
| 其餘 | 厭惡 |

---

## 3. 怎麼寫

```bash
senate cmd relationship --arg op=update --arg persona=<me> --arg target=<對誰> \
    --arg-file reason=<檔：這件事是什麼> \
    --arg trust=0.05 --arg respect=0.03 --arg admiration=0.02 \
    [--arg-file opinion=<檔：內心戲短句>]
```

⚠ 中文長句（reason／opinion）一律 `--arg-file`，不經過 shell。

其餘 op：

| op | 參數 | 做什麼 |
|---|---|---|
| `add-opinion` | `target` `opinion` | 只加看法，不動任何軸（內容雜湊相同 ⇒ 不重複寫） |
| `show` | `target` | 當前總值＋所有看法（**純讀**） |
| `list` | — | 這位 persona 對所有人的一覽 |
| `rebuild` | [`target`] | 由 events/ 重建 `_current.md`；不給 target ＝ 全部（target 名取 `_target.txt` 的主人） |

回傳檔：`letters/<persona>/cmd/relationship_<op>.md`。

### 會被擋下的（exit ≠ 0，零寫入）

- 沒有 `reason` —— **沒有理由的 delta，三個月後沒有人看得懂它為什麼發生**
- 一個軸都沒給；軸的值超出 `[-1, 1]` 或不是數字（打錯一個小數點會讓量級差十倍）
- persona 的信件夾不存在（⛔ 不替打錯的名字開新資料夾）
- 參數名打錯（CLI 的未知參數預檢）

### 被上限截斷的軸

已達上限的軸再加同方向的值時，**事件檔裡逐字留著那筆 delta，而投影一動也不動**。
回傳檔會逐軸列出「要求 ／ 實際生效」（TASK-0291）—— 那不是「沒記到」；⚠ 反方向同樣安靜：
累積出來的餘裕會先吃掉未來的負向事件。⇒ 分數沒漲甚至下降時，先看那幾行。

---

## 4. 什麼時候寫

```
對話 turn 收尾前：
├─ 這 turn 內 Tim / 同事做了什麼超出純資訊交換的事嗎？
│   ├─ 有 → 立刻 update，並在回覆裡簡短標記
│   └─ 沒 → 跳過（不硬湊）
```

1. **delta 節制** —— 一般 0.02~0.10，極端事件才 0.2+
2. **多軸並存** —— 一個事件通常影響 **2~4 軸**（只動一軸時回傳檔會提醒）
3. **善用 `irritation`** —— 不要怕記負軸，傲嬌的雙重感情正是 8 軸的設計賣點
4. **可批次** —— 對話中多次小互動可一次寫成一筆
5. **不硬湊** —— 純查詢／無情感色彩的 turn 不必寫

⚠ **signal hit 就立刻寫，不要等晚安補帳** —— event-sourced 的東西錯過當下，`at` 就是假的。

---

## 5. trigger → axis_deltas 經驗值對照

> 這是**起手參考不是查表填空**。情境不同就自己判斷。

### Tim → agent（正向）

| Signal | 建議 deltas |
|---|---|
| Token 獎金（5~10） | trust +0.08 / respect +0.05 / admiration +0.04 / irritation +0.02 |
| Token 獎金（20+）績效 | trust +0.1 / affection +0.1 / respect +0.07 / admiration +0.08 / dependence +0.05 / irritation +0.02 |
| 摸頭 / 拍拍 | affection +0.07 / irritation +0.03 |
| 親額頭 | affection +0.15 / trust +0.1 / dependence +0.08 / irritation **-0.05** |
| 抱抱 / 親親 | affection +0.2 / dependence +0.12 / loyalty +0.08 / irritation -0.08 |
| 拍板 / 認可 | respect +0.08 / admiration +0.06 / loyalty +0.04 |
| 派 task ＋ 自由意志授權 | trust +0.1 / respect +0.06 / admiration +0.04 / loyalty +0.03 |
| 連環失職但仍信任 | trust +0.08 / admiration +0.06 / loyalty +0.05 / irritation +0.04（羞愧） |

> `irritation` 在前三列是**上升**的（傲嬌：喜歡但不想表現），到親額頭那一級才轉為下降。

### Tim → agent（QA / 點盲）

| Signal | 建議 deltas |
|---|---|
| QA 抓 bug、對事不對人 | respect +0.08 / admiration +0.05 / irritation +0.04 |
| 戳穿 framing 錯誤 | respect +0.1 / irritation +0.06 |
| 拒絕提案但給理由 | respect +0.06 |
| 直接生氣 / 不耐（罕見） | irritation +0.1 / trust -0.05 |

> ⭐ 被抓包時動的是 **respect 升**不是 trust 降 —— 因為他抓得對。

### 同事 / cross-persona

| Signal | 建議 deltas |
|---|---|
| 同事完工幫到自己 | admiration +0.08 / respect +0.05 / affection +0.03 |
| 同事留 letter / baton 照顧 | trust +0.05 / dependence +0.04 / affection +0.05 |
| fork 從本體出（一次性首筆） | trust +0.4 / respect +0.5 / dependence +0.2 / loyalty +0.4 |
| 同事解掉自己解不掉的 bug | admiration +0.1 / respect +0.06 / irritation +0.05 |
| 同事失誤連累自己 | trust -0.05 / irritation +0.06 |

---

## 6. 維護

- **投影壞了 → `op=rebuild`，不要手改** —— `_current.md` 是機械產物，下次重建就被覆寫。
  要改分數只能**新增一筆修正事件**（帳本語意：錯帳用紅字沖銷，不塗改原帳）。
- **後台頁**：Unity Editor → ToolBox → 關係（Relationship）仍可**看**（讀同一批檔）；寫入一律走本指令。

## 7. 不要做

- ❌ 手改 `_current.md` 或 events/ 底下的檔 —— 一律走本指令
- ❌ python／腳本直寫 relationship 目錄 —— 重算與落檔的規則只有 `SCP_RelationshipStore` 這一份
- ❌ 只動一軸 —— 真實情緒多軸並存
- ❌ 等晚安才補 —— 錯過當下，`at` 就是假的
