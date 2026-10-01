---
title: 文件編輯活動的一步 —— 改完一份 .md 之後登記、驗收、指回流程
description: senate cmd doc-edit 的使用說明：三種 kind（doc／letter／constitution）的目標怎麼算、它憑什麼說「本場改過」、會被擋下的情形、回傳檔在哪，以及為什麼它刻意不搬內容。
cmds: [doc-edit]
last_updated: 2026-10-01 (TASK-0367：從 UCL `ucmd run DocEdit` 搬到 Senate CLI)
target_audience: [AI_Agent]
related:
  - ucl_core:Docs~/{lang}/FreeTime/Activities/doc-reflection.md | doc-reflection | 自由時間活動（kind=doc）
  - ucl_core:Docs~/{lang}/FreeTime/Activities/letter-to-self.md | letter-to-self | 自由時間活動（kind=letter）
  - ucl_core:Docs~/{lang}/FreeTime/Activities/constitution.md | constitution | 自由時間活動（kind=constitution）
  - ucl_core:Docs~/{lang}/Workflows/Constitution_Workflow.md | 修憲流程 | 改完憲法之後跑本支
---

# 📝 doc-edit —— 「改完一份 .md」之後跑它

> 一句話：它驗那份檔**真的動了**、把這一步記下來、並告訴你下一步。**不需要 Unity Editor。**

## 1. 為什麼存在

`doc-reflection`／`letter-to-self`／`constitution` 三個自由時間活動的本體就是**編輯一個檔**，
沒有單一的 CLI 步驟可以代跑 ⇒ 流程一進到編輯就斷在那裡。
本支讓它們也能「做完一步 → 回報 → 被指去下一步」（Tim 2026-08-18 拍板；2026-10-01 從 Unity 搬到 Senate）。

## 2. ⛔ 它不做什麼（這是設計）

**沒有 `body` 參數，不寫、不覆寫任何 .md。** 把整份文件塞進 CLI 參數，等於把編輯器換成一個
沒有 diff、沒有復原的通道。Tim 的原話是「一步改一個 Doc，**改完後** CMD 一樣提示下一步」——
本支站在編輯之後。⇒ 目標檔不存在時它**不建檔**，直接擋。

## 3. 參數

| 參數 | 說明 |
|---|---|
| `kind` | `doc` \| `letter` \| `constitution`（必填） |
| `persona` | **`letter`／`constitution` 必填**（落點綁在某個人身上，猜錯會驗到別人的信與憲法，而且看起來完全正常）；`doc` 選填（帶了才驗得出「本場改過沒」） |
| `target` | `doc` 必填（repo 相對或絕對）；`letter` 選填；`constitution` **忽略** |
| `note` | 一句心得，選填 |
| `data_root`／`letters_root`／`project_root` | senate CLI 沒給時用設定檔補上 |

## 4. 目標怎麼算（路徑由本支算，呼叫端不必記慣例）

| kind | 目標 |
|---|---|
| `constitution` | 固定 `letters/<persona>/_constitution.md`。`target` 刻意忽略 —— 允許覆寫的話，「改自己的憲法」就變成「可以改任何檔」 |
| `letter` | 給了 `target` 就用它；沒給 ⇒ `letters/<persona>/` **頂層**最新一封 **frontmatter `type: letter_to_future_self`** 的信 |
| `doc` | 只認顯式 `target`（相對路徑以專案根為基準） |

letter 的自動挑選：
- 只看頂層（`wakes/` `rests/` 等子目錄是別的東西）。
- 具名排除 `_constitution.md`／`_keys_open.md`／`_latest.md`／`README.md`。
- 🩸 **判準是 frontmatter `type`，不是檔名**：舊位置還留著回傳檔殘影，mtime 可能比真信新 ——
  實測 calli 的 `_goodmorning_brief.md` 就被挑成過「最新那封信」。同事寄來的 `peer_letter_from_persona` 也不算。

## 5. 驗收：它憑什麼說「本場改過」

- 檔案存在／是 `.md`／**在專案根之內**（repo 外的路徑通常是另一個宇宙的檔，失敗時會回一個看起來正常的讀數）。
- 印出實際 mtime 與大小。
- **在自由時間中時，拿 session 開場時刻當基準**：
  - mtime ≥ 開場 ⇒ ✅ 本場改過（`verdict=yes`）
  - mtime ＜ 開場 ⇒ ⚠ 本場沒動過這份檔（`verdict=no`）—— **不擋**，本支是登記不是收銀台
  - 沒帶 persona／不在自由時間／開場時刻讀不出 ⇒ ⚪ 只印 mtime 不下判斷（`verdict=unknown`）

## 6. 回傳

- 回傳檔：`letters/<persona>/cmd/docedit_<kind>.md`（被擋時 `docedit_<kind>-blocked.md`）。沒帶 persona 不落檔，報告直接印在輸出。
- 報告尾端掛自由時間的「▶ 下一步」（在自由時間中才有）。
- 機讀：`verdict`（yes｜no｜unknown）、`target`。
- exit：0 登記成功／2 參數不合或目標被擋。

## 7. 用法

```bash
# doc-reflection：改完一份文件
senate cmd doc-edit --arg kind=doc --arg persona=<me> --arg target=Docs/AI_READABILITY_GUIDELINES.md --arg note=補上欄位說明

# letter-to-self：寫完信（不給 target ⇒ 自動取最新那封）
senate cmd doc-edit --arg kind=letter --arg persona=<me>

# constitution：修憲之後
senate cmd doc-edit --arg kind=constitution --arg persona=<me> --arg note=盲點清單加一條
```

⚠ `note` 有中文或標點時走 `--arg-file note=<檔>`。
