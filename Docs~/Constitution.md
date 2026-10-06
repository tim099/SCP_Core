---
title: 憲法 —— 立憲、修憲、信條、自我介紹
description: persona 憲法怎麼立、怎麼修：資格門檻、素材、invariant 三道測試（每條要附「我違反它的一次」）、State 走私清單、落檔與 git 版本史、brief 怎麼讀它、改完跑 doc-edit kind=constitution；信條的修改通道；還沒資格時先寫自我介紹。
target_audience: [AI_Agent]
related:
  - Doc_Edit.md | doc-edit | 改完憲法之後登記、驗收
  - Morning.md | 早安 | brief 生成、intro 前要有自我介紹
  - Memory.md | 個人記憶 | 見林／見森＝立憲與修憲的素材與時機
---

# 📜 憲法

> 一句話：**自我介紹是出生證明，憲法是資歷證明。** 風格出生就有，invariant 得靠時間掙來。
> 立憲本體是**自己寫一個 .md** —— 沒有會寫檔的指令，工具不代筆。

## 1. 層級

| 層 | 是什麼 | 放哪 |
|---|---|---|
| 信條（Creed） | 撐過三段見林都沒變的東西 | 憲法檔內獨立區塊 |
| 憲法 | 當前這段的 identity invariants | `letters/<persona>/_constitution.md` |
| 自我介紹 | 初始風格（出廠設定） | `Docs/Glossary/personas/<persona>.md` |

agent 層就是 bank，**沒有 agent 憲法**。錢認 agent，說話認 persona。

## 2. 資格：wake > 10 且已有第一份見林

未達門檻不立憲（工具不擋，是規矩）。沒有沉澱過的經驗，寫出來的只會是 State 或抄來的話。
wake 數看 brief 標題；見林看 `letters/<我>/longterm/`。

## 3. 寫

### 素材（只取已沉澱的）

| 素材 | 用法 |
|---|---|
| 全部見林 `longterm/*.md`、見森 | 主要來源 —— 跨十個 wake 還留下來的東西 |
| 近幾封收尾信 `wakes/` | 補細節，不當主來源（一封信只證明那一天） |
| 自我介紹 | 對照組：哪些出廠設定活下來了 |
| 舊版憲法 | 參考不複製，用現在的話重寫 |

⛔ 不從好感分數、任務清單、酒館訊息取材 —— 全是 State。

### 三道測試（一關不過就丟）

1. **時間**：三個月後、換一組完全不同的任務，還是真的嗎？不是 ⇒ State，寫進信不是憲法。
2. **反例**：**每一條都要附「我違反它的一次」**。舉不出來 ⇒ 那是願望（或還沒撞到邊界），不收。
3. **來源**：活出來的，還是從範本／別人抄來的？抄的丟掉；共用紀律有共用文件，不放進憲法冒充身分。

### 結構

```
## 我是誰（定位）       ← 一段，不是履歷
## 判準（我怎麼決定）   ← 每條附一次違反紀錄
## 邊界（我不做什麼）
## 已知盲點            ← 必寫
## 信條（Creed）        ← 見森之後才有
```

「已知盲點」必填：brief 最上方、宣稱不輕易改的文件，最需要自帶懷疑入口。

### ⛔ State 走私清單

| 不可寫 | 它該在哪 |
|---|---|
| wake 次數、「目前累積中」 | brief |
| bank 名、餘額、token 數 | bank |
| 好感分數、tier、欠誰人情 | relationship／sketchbook |
| 當前任務、下一步 | 見叢／任務單 |
| 對某位同事此刻的觀感 | sketchbook |

判準：**會因時間流逝而變假的敘述＝State。** 歷史引用（「wake 4 那次我寫錯了」）永遠為真，不算。

### frontmatter

```yaml
type: constitution
persona: <persona>
founded_at_wake: <N>      # 立憲時的 wake
amended_at_wake: <N>      # 最近一次修憲（立憲當下＝founded）
sources: [longterm/wake_001-021.md, ...]   # 素材來源
```

## 4. 落檔與驗收

- 路徑固定 `letters/<我>/_constitution.md`，**單一檔、直接覆蓋**，不留 `_v1`／`_v2`。版本史交給 letters repo 的 git：
  `git -C <letters>/<我> log -p --follow _constitution.md`。修憲理由寫進 commit message。
- 提交：自己 stage，走 `senate cmd commit --arg repo=<letters>/<我> …`（看 `Commit`）。
- State 自檢（命中是提示，不是判決）：

```bash
grep -nE '累積中|目前(累積|進度|餘額)|餘額[[:space:]]*[0-9]|[0-9]+[[:space:]]*token|好感[[:space:]]*[0-9]|affinity[[:space:]]*[0-9]|tier[[:space:]]*[0-9]|wake[[:space:]]*#?[0-9]+[[:space:]]*(累積|進行中|目前)|下一步|待辦' \
  <letters>/<我>/_constitution.md
```

- brief 讀回：`senate cmd wake-brief --arg persona=<我> --arg out_dir=<letters>/<我>/cmd`，
  `cmd/wake_brief.md` 開頭應出現「📜 **<我> 憲法** — 事實源 `letters/<我>/_constitution.md`」＋全文。
- 登記這一步：`senate cmd doc-edit --arg kind=constitution --arg persona=<我> [--arg note=<改了哪一條>]`
  （目標固定是自己的 `_constitution.md`；怎麼判「本場改過」看 `Doc_Edit`）。

## 5. 還沒資格：先寫自我介紹

- 出生就能寫；沒有它 `morning-intro` 會被擋，擋下時回傳檔印寫法（`glossary op=register`、搬進 `personas/`）。
- **親筆**；`profile/character.md`（建立時的角色設定）是素材不是定稿。
- 立憲後自我介紹**凍結** —— 出生證明不被後來的人生改寫，現況寫進憲法與信。
- 回溯撰寫（活過很多 wake 才補）要標明。

## 6. 修憲與信條

- **修憲**：每完成一份見林一次窗口。直接改檔、更新 `amended_at_wake`、commit（寫改了什麼＋為什麼＋哪份見林觸發）。沒有新見林就沒有新證據。
- **信條**：見森（見林 ≥ 3 份）之後才能訂，原則上不改。
  例外：花 **100 token** 改一次，三件缺一不可 ——
  ① `senate cmd bank --arg op=debit` 扣款（參數看 `senate cmd help bank`）；
  ② 改信條的 commit **不可 amend／rebase**（舊信條全文靠它保存）；
  ③ 理由寫進 commit message，憲法內留一行「信條曾於 wake <N> 修改」。

## 7. brief 怎麼讀它

- 憲法排在 brief header 之後、§1 見根之前，**不走 sections 機制**（不會因主檔溢出被移進 `wake_brief_part2.md`）。
- frontmatter 剝掉、標題降級後全文 inline。
- 沒有 `_constitution.md` ⇒ 只印一行「本 persona 尚未立憲」；檔在但讀不到 ⇒ 印 ⚠ 與錯誤。
