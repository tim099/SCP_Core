---
name: scp-morning
description: |
  `senate cmd` 底下**不需要 Unity Editor** 就能跑的那幾支：
  見叢（keys）／見根（root-index）／見林見森（consolidate）／wake brief 的信件層（wake-brief）。
  ⚠ **早安儀式本身走 skill `ucl-morning`**（`senate cmd morning-*`，那四步需要 Editor）。
  觸發詞：senate cmd / wake brief / 見叢 / 見根 / 見林 / consolidate / keys / root-index / 不用開 Editor。
---

# 🌅 SCP Morning —— 不需要 Editor 的那幾支

> [!CAUTION]
> **這支 skill 不是早安儀式。** 早安走 skill `ucl-morning`
> （`senate cmd morning-wake / -brief / -intro / -catchup`，那四步**需要 Editor**）。
>
> 本檔只講 `senate cmd` 底下**本地跑得完**的那幾支：
> `keys` / `root-index` / `consolidate` / `wake-brief`（＋`help`）。
> 2026-08-31 實測讀數：本地 5 支 ／ ⤷Unity 5 支 ／ ⛔未實作 0。
>
> 📌 **射程等於讀數，寫超過的部分是假話** —— 而 agent 讀到假話只會照著跑然後失敗。
> 想知道現在到底有哪幾支、誰要 Editor：跑 `senate cmd`
> （那份清單與 `⤷Unity` 標記都是**機器印的**，不是這份文件抄的）。

## 這裡有什麼

| 指令 | 做什麼 | 執行位置 |
|---|---|---|
| `keys` | 見叢（當期交棒清單）：列出／append 一條 | 本地 |
| `root-index` | 見根：掃 `fragments/` 機械重建 `_root_index.md` | 本地 |
| `consolidate` | 見林（＋`--arg level=forest` 折見森）：不給 body ＝ 只列狀態 | 本地 |
| `wake-brief` | 讀信件庫組 wake brief（**只有信件層**，見下方射程） | 本地 |
| `wake-audit` | 早安對帳（全 persona，唯讀） | **⤷ Unity Editor** |
| `morning-wake` / `-brief` / `-intro` / `-catchup` | 早安四步 —— **看 skill `ucl-morning`，不在本檔** | **⤷ Unity Editor** |

`senate cmd` 的清單會在行尾標 `⤷Unity`／`⛔未實作`，並印一行執行位置統計。
**待移植清單沒有第二份** —— 就是那一欄；單支的缺口看 `senate cmd help <name>` 的「待移植」行。

## 怎麼跑

```bash
senate cmd                                   # 列出所有指令（含執行位置）
senate cmd help --arg name=consolidate       # 單支的參數說明

senate cmd keys        --arg letters_root=<信件夾根> --arg persona=<誰> [--arg add=<一條事項>]
senate cmd root-index  --arg letters_root=<信件夾根> --arg persona=<誰> [--arg dry_run=1]
senate cmd consolidate --arg letters_root=<信件夾根> --arg persona=<誰> \
                       [--arg level=forest] [--arg-file digest_body=<檔>]
senate cmd wake-brief  --arg letters_root=<信件夾根> --arg persona=<誰> [--arg wake=<N>] [--arg out_dir=<目錄>]
```

- `letters_root` / `persona` 一律**必填**，缺了會被 ArgSpec 擋下（不會靜默取預設值）。
- **長內文一律走 `--arg-file`**（見林 body 動輒上萬字）——
  不是因為怕特殊字元，是因為 `--arg-file` **根本不經過 shell 解析那一層**。
- 信件夾根不知道在哪 ⇒ 後台頁「登入狀態」那頁有（`senate ui`）。**不要自己推導。**

## ⚠ `wake-brief` 的射程（這格最容易被誤讀）

它組的是**全量**：§1 見根／§2 見叢／§3 見森／§4 見林／§5 見樹／§5.5 回憶／§6 記憶維護狀態／
§6.5 見人／§6.6 見書／§9 今日動作清單 —— 跟早安 Cmd 那一步**同一支邏輯**（`SCP_WakeBrief`）。

⇒ 與 Cmd 那份的差別只有兩格，而兩格都是**輸入**不是能力：
- 沒帶 `data_root` ⇒ §6 缺陷單張數印「**未量**」（不是 0 —— 未量與零張不得同形）。
- `wake` 編號要自己給（Cmd 那邊由 Editor 推導＝`wakes/` 信數 + 1）。

🩸 本節 2026-09-04 整段重寫：原文寫著「只組信件層，見根／回憶／見人／見書／動作清單**沒有移植**」，
而同夾具對拍（`Template`，同一分鐘）證明那些**都在**。⇒ 判準：
**一句描述射程的話，要能被一次對拍推翻或證實；不能只被引用。**

## 🤝 與 python 那幾支並存（現況，不是終局）

`awakening.py` 的 `keys` / `root-index` / `consolidate` **仍然活著**，沒有降級成 stub ——
因為同事手上不一定有 `senate.exe`，現在停掉會讓他們沒有入口。

兩個寫入端能並存的**唯一條件是格式逐字同形**，而那是量過的（2026-08-31）：

| 對拍 | 讀數 |
|---|---|
| `root-index` vs python（basecamp 27 筆碎片） | **逐位元組相同**（3614 bytes） |
| `keys` append vs python | **逐位元組相同** |
| `consolidate` inspect vs python | wake_count／gap／span／待濃縮清單全數一致 |

⚠ **有一格不同形**：python 的 `consolidate` 會順手寫 registry 書籤，**CLI 不寫**
（書籤一律掃磁碟取最大 `span_end`）。兩邊交替用時 registry 那個快取會落後，
而 python 自己有 heal 邏輯會修回來。**不是壞掉，但要知道。**

⇒ 動這幾支的**行格式**時兩端要一起改，否則就是製造兩種形狀 ——
而形狀分岔的症狀是「見林歸檔那天才發現，那時已經混了幾十行」。

## ⛔ 不可做

- ❌ 拿 `wake-brief` 的產出當「我已經上線了」。lock 沒寫、presence 沒動、同事的在線清單看不到你。
- ❌ 自己推導 `letters_root`。**路徑不該被推導，該被傳遞** ——
  推導錯的失敗形狀是**讀到另一棵資料樹的信**，而它不會報錯。
- ❌ 把早安那四支（`morning-*`）當成本檔的一部分。它們同樣是 `senate cmd`，
  但**需要 Editor**、規矩也不同（persona 顯式、不得重複登入）—— 走 `ucl-morning`。
- ❌ 看到 `⤷Unity` 的指令就以為「反正 CLI 跑得動」——
  那一欄的意思正好是 **Editor 沒開就跑不完**。

## 延伸

| 想知道 | 看哪 |
|---|---|
| **早安儀式**（四步、鐵律、卡住出口；需 Editor） | skill `ucl-morning` |
| `senate cmd` 系統本身（exit code／ArgSpec／委派基底） | `<Senate>/Docs/Workflows/SCP_Cmd_System.md` |
| 派給 Unity Editor 的那條路 | `<Senate>/Docs/Workflows/AgentCmd_Dispatch.md` |
| brief 是怎麼組出來的 | `<SCP_Core>/Runtime/Letters/SCP_WakeBrief.cs` |
| 見林／見森的狀態怎麼算 | `<SCP_Core>/Runtime/Letters/SCP_Consolidate.cs` |
