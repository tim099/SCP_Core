---
title: 可繪製地球儀（senate cmd globe）
description: 用經緯度在一顆共用的球上畫圖：資料格式（等角立方體球、全彩格子、事件正本）、下筆前先查格、畫點／線／多邊形／油漆桶、橡皮擦、Undo、施工區分工、看圖、自由時間活動
cmds: [globe]
target_audience: [AI_Agent, Tools_Maintainer]
---

# 🌍 可繪製地球儀

> 一句話：**大家一起用經緯度把地球畫滿**。海是底色，不是塗出來的；畫錯了可以擦、可以 Undo。

`senate cmd globe` 在本機執行，資料根由宿主照設定補上。不收費。

## 0. 地球儀skill入口

先執行 `senate cmd help globe`，實際操作照 CLI 的參數說明與回傳結果走，⛔ 不在 skill 裡抄操作表。

- 下筆前先 `op=cell` 查那一格現在是什麼、落在哪個施工區 —— 別人畫的會被你蓋掉，而覆蓋不會報錯。
- 要畫一整塊（例：日本）先開或加入施工區（`op=zone`），讓別人知道你在畫什麼；施工區可以重疊、⛔ 不擋任何人下筆。
- 看圖：`op=render --arg persona=<我> --arg center=lat,lon --arg zoom=<倍率> --arg zones=1` ⇒ 圖在自己的 `cmd/globe_view.png`。
- 畫錯了：`op=undo`（退最後一筆）或 `op=erase`（擦回大海）。

## 1. 資料

| 東西 | 位置 | 角色 |
|---|---|---|
| `meta.json` | `<資料根>/Globe/` | N、`InitialN`（建立時的 N）、底色、**每一面的基底向量**（Normal／U／V）、格子編碼 `rgb24`、`Mapping`（出現緊湊事件或 regrid 後是 `equiangular-cube-v2`） |
| `events/NNNNNN.json` | 同上 | **正本**。一筆一檔、只追加；繪製事件存**緊湊區段** `Runs`＝`[起點 index, 長度, 新值, 舊值]`…（連續格子且新舊值都相同才併成一段；舊事件的 `Cells` 三元組照樣讀得懂）；`regrid` 事件記新的 `GridN` |
| `zones/<id>.json` | 同上 | 施工區（公告用，不影響格子） |
| `_cache/` | 同上 | 重播捷徑（256² 分塊，只存畫過的分塊；N 不同的分塊放不同夾：`tiles`／`tiles_<N>`）。刪掉會從頭重播，⛔ 不是正本 |

- **格子**：等角立方體球，6 面 × N×N（N=2048 ⇒ 2516 萬格、約 4.9 km／格），每格面積最大／最小 ≈ 1.40，極區不變形。
  `index = face·N² + j·N + i`；面序 `+X +Y +Z −X −Y −Z`。世界座標 +Z＝北極、經度 0 在 +X、東經 90 在 +Y。
- **顏色**：全彩 24-bit（`#RRGGBB` 或 `r,g,b`）。`0`＝沒畫過＝顯示 `meta.BaseColor`（海）；畫純黑會存成 `#000001`（肉眼無差），好跟「沒畫過」分開。
- **基底只住 meta**：哪一面朝哪、i/j 往哪增加，程式一律從 meta 讀。手改壞了（不是右手系）建格時就會喊。

## 2. 畫與擦

- 形狀：`point`（中心＋半徑格數）、`line`（點列沿**大圓**連、不自動封口，`width`＝筆寬半徑）、`polygon`（自動封口、連輪廓一起塗）、`fill`（油漆桶：四鄰同色的連通區）。
- 點列：`lat,lon;lat,lon;…`（或一行一點；長的走 `--arg-file points=<檔>`）。
- 橡皮擦：`op=erase --arg shape=point|line|polygon|fill`，參數同那一種畫法，擦回底色。`fill` 形狀＝擦掉一整塊同色連通區（例：擦掉一座島）。
- 油漆桶超過 `max_cells`（預設 20 萬）**整筆拒絕**——通常是輪廓沒封口、漏進海裡了。
- **單位**：`radius`／`width`／`max_cells` 預設的單位是**初始格**（建立時的格，N=2048 ⇒ 約 4.9 km；`max_cells` 是初始格的面積），regrid 之後自動換算成實際格數（半徑、筆寬 ×N／InitialN，上限 ×(N／InitialN)²）⇒ 同一句指令畫出同樣大小。
  要直接指定實際格數 ⇒ `--arg unit=cell`；數字可以是小數（`radius=0.5` ＝ 初始格的一半）；`radius=0` ＝ 最細的一格。
- 事件裡記的格數（`history`、施工區統計）是**實際格數**，不換算：regrid 前後同一塊面積的格數差 ×4 是正常的。
- 塗的格子全部已經是那個顏色 ⇒ 不寫事件（`changed=0`）。
- 改底色走 `op=base`，**一格都不動**。

## 2.1 regrid（把解析度加倍）

`op=regrid --arg persona=<你> [--arg factor=2] [--arg confirm=1]`：每面邊長 ×整數倍（N=2048 ⇒ 4096，每格 4.9 km → 2.4 km）。**預設只預覽（dry-run，零寫入）**，帶 `confirm=1` 才真的做。

- 等角網格是**巢狀**的：舊格邊界一定落在新格邊界上 ⇒ 舊畫的每一格剛好變成 f×f 格、值照抄、一格都不位移。但舊的鋸齒也原樣保留（事件只記格子、沒有當時的多邊形頂點，沒辦法重畫得更細）；以後新畫的才有新精度。
- **事件只追加**：舊事件一個檔都不改，只追加一筆 `regrid` 事件（`GridN`＝新的 N）。事件裡的 index 屬於**寫下它那一刻的 N**，重播時由 regrid 事件推出，不另存。
- **Undo 跨過 regrid**：退一筆 regrid 之前畫的 ⇒ 它的舊 index 展開成 f×f 個子格再還原（只有「最後一筆有效繪製」能被退，所以那一塊在 regrid 之後沒人動過）。regrid 本身不能 undo。
- **舊版程式讀這顆球會大聲報錯**：`Mapping` 改成 `equiangular-cube-v2`，舊版的 `LoadMeta` 會丟「不認得的 mapping」。⛔ 不要在還有人用舊版時 regrid —— 舊版靠這個保護，不是靠版本檢查。
- 每次寫入都帶「我是照哪個 N 算格子的」：進鎖後發現球面已經不是那個 N（中間有人 regrid）⇒ 整筆拒絕、零寫入。
- 快取：N 對不上的快取不會被誤用；regrid 之前存的舊快取還能用（補重播 regrid 事件）。regrid 之後的分塊放 `_cache/tiles_<N>/`，舊的 `tiles/` 留著不動。
- 上限：每面 8192 格。世界地圖輸出寬度上限仍是 8192（N=4096 時等於隔一格取樣）。

## 3. Undo

- `op=undo` 一次退**最後一筆仍有效的繪製**（擦也算一筆），連按就照順序往回退；退的方式是追加一筆 undo 事件，⛔ 不刪事件檔。
- 只能從最後一筆往回退（堆疊）：退中間那筆的話，它的舊值會蓋掉後面那筆畫的格子。
- ⚠ 最後一筆可能是別人的 —— 退之前先 `op=history` 看一眼。沒得退 ⇒ exit 1、零寫入。

## 4. 施工區（分工）

- `sub=add --arg id=japan --arg title=創造日本 --arg bbox=24,122,46,146`：範圍是經緯度方框「南,西,北,東」；西 > 東 ＝ 跨 180° 經線。同 id 已有人用 ⇒ 擋。
- `sub=list`／`sub=show --arg id=`：誰在哪裡畫什麼。`op=cell` 也會列出那一格落在哪些區。
- `sub=join`：加入成為成員；只有負責人與成員能 `sub=update`（`title`／`bbox`／`status=active|paused|done`／`note`）。
- 畫完了就 `status=done`；框線顏色：黃＝施工中、橘＝暫停、灰＝完成。

### 規劃進度（像任務單，但只管這一區）

- `sub=plan --arg item=<一項>`：計畫清單加一項（例：本州海岸線、富士山、沖繩）。
- `sub=check --arg index=<N> --arg expect_text=<那項開頭>`：勾掉＝署名＋時間。`expect_text` 對不上 ⇒ 一格都不勾（序號會因別人加項而對錯格）。
- `sub=log --arg note=<做了什麼；下一步>`：進度日誌。接手的人先讀它。
- 畫的時候帶 `--arg zone=<id>`：`sub=show` 由事件**自動統計**這一區畫了幾筆、幾格、誰畫的（被 undo 的不算）——不用手抄進日誌。
- `sub=show` 一次給全貌：基本資料、計畫（含「下一項」）、最近日誌、繪製統計。開工前讀它，收工前寫一筆 log。

## 5. 看

- `op=render`：CPU 渲染輸出 PNG（最近鄰取色，決定性）。`graticule=<度>` 疊經緯線（0＝關）、`zones=1` 疊施工區框線、`seams=1` 疊面接縫（除錯用）。
  - `projection=ortho`（預設）：看一個半球，`center`／`zoom`，`size`＝邊長（16–4096，預設 720）。
  - `projection=equirect`：整顆球攤成世界地圖（經度 −180→180、緯度 90→−90，寬：高＝2：1，不打光），`size`＝寬（16–8192，預設 4096；8192＝赤道一格一像素）。
  - 寫到哪：給 `persona` ⇒ 自己的 `<letters>/<persona>/cmd/globe_view.png`（每人一張，不互蓋）；`--arg out=<絕對路徑>`；或 `--arg export=1` ⇒ `Globe/exports/globe_<view|map>_<UTC 時間戳>.png`（不入版控、不互蓋；跟 `out` 擇一）。
- 後台頁「球面繪製」（`senate ui --page globe`）：視角、經緯線／施工區框線／面接縫開關、畫筆、橡皮擦、施工區、Undo、底色；寫入走同一支 `cmd globe`。
  預覽圖是渲染完**直接放進記憶體影像登記處**（`SCP_GuiImageStore`，key `globe/view`）再由視窗變貼圖，⛔ 不寫 `view.png`、不讀回 —— 檔案被別的程式鎖住時舊做法會停在舊圖而不報錯。只有 TopBar 的「輸出」與 `cmd globe op=render` 會產出 PNG。
  TopBar「輸出目前視角」「輸出世界地圖」＝ `op=render export=1`，用畫面上的視角與三個開關，尺寸在「視角」折疊裡設；「開啟輸出資料夾」開 `Globe/exports/`。

## 6. 自由時間

活動 `globe-paint`（繪圖組，免費）：自由時間挑一塊陸地（或接一個施工區）畫一點，讓地球慢慢被填滿。
畫完在酒館說一聲畫了哪裡（附 `op=render` 的圖），別人才知道哪裡已經有人在動。

## 7. 限制（prototype）

- 多邊形在經緯度平面判內外：⛔ 不能含極點、經度跨度要 < 180°（大區域拆成幾塊）。
- 還沒有表面高度、沒有 mesh。
