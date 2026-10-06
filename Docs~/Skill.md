---
title: 新增一個 skill —— 源檔、內容宣告、安裝
description: senate cmd skill 的使用說明：skill 源檔住哪、SKILL.md 只放入口（name／description／觸發詞／docs）、內容由文件的章節在查詢時現組、缺一塊整份失敗；op=show 先驗內容，op=sync 裝到三家 agent，op=status／remove 看狀態與清殘留。
cmds: [skill]
target_audience: [AI_Agent]
---

# 🧭 新增一個 skill

> skill 本身**只有入口**：裝出去的 SKILL.md 只有一行 `senate cmd skill --arg op=show --arg name=<名>`，
> 內容在查詢當下才從文件組出來 ⇒ 改文件不必重裝 skill、不必重 build。參數表看 `senate cmd help skill`。

## 1. 先寫文件，再寫 skill

內容住在文件裡（`SCP_Core/Docs~/` 或 `Senate/Docs/`，規則見 `Doc_Query`「文件住哪」）。skill 只**挑章節**：

- 一個 skill 要講的事，先確認文件裡有對應的章節；沒有就先補文件。⛔ 不在 SKILL.md 裡寫操作說明 —— 源檔 frontmatter 之後的內文安裝時一律丟掉。
- 章節標題要穩定：skill 用「標題開頭」對章節，改標題＝skill 斷掉（見第 4 節）。

## 2. 源檔：`SCP_Core/Skills~/<名>/SKILL.md`

```yaml
---
name: scp-<名>
description: |
  一句話說它做什麼（含主要指令）。內容由 `senate cmd skill --arg op=show --arg name=scp-<名>` 印出。
  觸發詞：詞一 / 詞二 / … / scp-<名>
docs:
  - 文件名#章節標題開頭
  - 文件名            # 不帶 # ＝ 整份
---
```

- `name` 與資料夾同名，前綴 `scp-`。
- `description` 是 agent 決定要不要載這個 skill 的唯一依據：寫清楚**什麼時候用**。
- **「觸發詞：」那一行**：Antigravity 的 `trigger:` 由它推出來（`/` 分隔）；沒有它就退成 `always_on`（每次都載）。
- `docs:` 依序合併；`文件名#前綴` 取標題文字以前綴開頭的那一節（到下一個同級或更高級標題為止，程式碼區塊裡的 `#` 不算）。
- 沒有 `docs:` 的 skill 走舊的鏡像模式（整個資料夾照抄）—— 新 skill 一律用 `docs:`。

## 3. 驗內容，再安裝

```bash
senate cmd skill --arg op=show --arg name=scp-<名>                  # ① 組出來的完整內容（缺一塊 ⇒ 整份失敗、列出缺哪裡）
senate cmd skill --arg op=sync --arg name=scp-<名>                  # ② 只印計畫
senate cmd skill --arg op=sync --arg name=scp-<名> --arg confirm=1  # ③ 真的裝（三家：.claude／.codex／.agents）
senate cmd skill --arg op=status                                    # ④ 讀回：每家都是 Synced
```

- **缺一塊就整份失敗**：宣告的文件不存在、撞名、章節找不到 ⇒ `op=show` 一個字的內容都不給、列出錯誤。⛔ 不要為了讓它過而拿掉那一行 —— 補文件或改宣告。
- 裝到的是**執行指令的那個 repo**（Senate 根）；安裝目錄是純鏡像（帶 `.scp_source` 標記），手改會在下次 sync 被覆蓋。
- `agent=claude|codex|antigravity` 只裝一家；省略＝三家。

## 4. 改名、刪除、殘留

- 改文件章節標題 ⇒ 先 `grep` 哪些 skill 的 `docs:` 用到那個前綴，同一筆一起改；改完每支跑一次 `op=show`。
- 刪 skill：刪源檔資料夾 → `op=status` 會列成「殘留」→ `op=remove --arg name=<名> --arg confirm=1`。
- `op=status` 的三種「源端沒有」分開處置：**殘留**（本工具裝的，可移除）、**別套裝的**（帶 `.ucl_source`，⛔ 不動）、**沒人認領**（沒有標記＝使用者自己放的，預設不刪）。

## 5. 收尾

- 源檔與文件是有作者的產出 ⇒ 走 `senate cmd commit`（SCP_Core 那一層）；安裝目錄的變動在 Senate 那一層。
- 會被其他 skill 或文件提到的話（例：別的 skill 說「寫書看 scp-book-writing」），同一筆補上指路。
