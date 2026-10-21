---
title: 施工場 —— 改程式預設開 worktree，合併回主線時才進場
description: 改既有程式碼（任何語言）預設在自己的 git worktree 裡改、建、驗、提交，不開 Coding 場；合併回共用工作區（主線）時才開 Coding 場、宣告範圍（含建置產物）；合完砍掉 worktree；退場閘紅燈時怎麼辦、做完就退。
cmds: [coding]
---

# 🚧 施工場

> 一句話：**改程式預設開自己的 worktree（不開場）；合併回共用工作區的那一刻才進場、宣告施工範圍；合完就砍掉 worktree。**
> 參數表、範圍怎麼判重疊、自動收場的五種「不收」看 `senate cmd help coding`。⛔ 本檔不重抄。
> Tim 2026-10-16：「預設開worktree好了 然後合併時才開施工場」「合併後砍掉worktree」。

## 1. 什麼時候做什麼

| 情形 | 做法 |
|---|---|
| 改動既有的程式碼 —— 任何語言、任何專案（C#、JS、Python、shader…）；新檔要接進既有的檔（註冊、import、清單、設定檔） | **開 worktree**（§2），⛔ 不開 Coding 場 |
| 在 worktree 裡 build、跑會重寫建置產物的指令 | 照做 —— 產物是你那份自己的 |
| **合併回共用工作區**（主線 merge、在共用工作區重建產物、提交） | ✅ **開 Coding 場**（§3），範圍含建置產物 |
| 只新增檔案，沒動到任何既有的檔 | 不用（新檔沒有人會跟你撞） |
| 只改文件、信件、記憶、資料 | 不用（那些有各自的寫入端） |

- 「先在共用工作區改一點點再說」是最常見的漏洞：改第一行之前就開 worktree。
- 只有 FreeTime／StreamWatch 場的人要合併回主線，也得**另開** Coding 場 —— 別的場不會替你擋撞車。

## 2. 預設：開自己的 worktree

```bash
git -C <repo> worktree add <你的路徑> -b <你的分支> <主線>   # 從最新的主線開；路徑放自己的 scratchpad
```

- 在自己那份裡改、在那裡 build、在那裡驗、在那裡提交（分支上）。⛔ 不開 Coding 場；有單就認領**不帶 `scope`**（帶了會開場）。
- 建置要讀、但不在版控裡的東西（快取、匯入過的素材）⇒ 從共用工作區複製一份進 worktree，⛔ 不要在 worktree 裡改共用工作區的檔。
- 預覽、對拍用的伺服器指到 worktree 那一份；用完就停、設定拿掉。
- **有子模組的 repo**（例：Senate 的 SCP_Core、Glossary）：worktree 裡的子模組是空的 ⇒ 把 url 指到本機那份再 init，不用上網：
  `git -C <wt> config submodule.<名>.url <共用工作區裡那個子模組的路徑>`，再 `git -C <wt> -c protocol.file.allow=always submodule update --init`。
- **worktree 自己建的執行檔會拉 Server**（例：在 worktree 跑它的 senate.exe selftest／cmd）：資料根單例鎖照樣擋下、真資料不會被動，可是被拒絕的行程不一定自己退，會一直鎖著 worktree 的 dll ⇒ 下一次 build 失敗（MSB3027）。重建前、收工前看一眼行程，**只停執行檔路徑在自己 worktree 底下的**。
- 還原改壞的檔用 `mv 備份 原檔` ⇒ mtime 比剛建好的產物舊，建置工具以為不用重編、跑的還是壞的那版 ⇒ `touch` 一下。

🩸 2026-10-16：kotoko 照本檔在 Senate 走第一輪 worktree 時撞到上面三格（子模組、殘留行程鎖 dll、mtime）。
- 做完：提交在分支上，交給合併的人（自己合也行）—— 說清楚分支名、從哪一版開的、合完要重建什麼。

## 3. 合併時才進場：宣告施工範圍

合併的人（作者自己或指定的人）在**共用工作區**動手之前先進場：

```bash
senate cmd coding --arg op=start --arg persona=<我> --arg status="合併 <分支>" --arg scope="<路徑>|<路徑>" [--arg tasks=<N>]
```

- 範圍取**最大**：合併會改到的每支檔、會被重寫的產物都列進去；不知道就宣告整個 repo（寬一點只是讓別人多等，窄了是撞車）。
- **建置產物一律在範圍裡**（例：遊戲的 `index.html`、Senate 的 `publish/`）：有些產物把**整個來源**一起建進去（單檔成品、打包檔、self-contained exe），在共用工作區建 ＝ 把別人還沒提交的半成品一起包進去；兩個人改的來源不重疊，閘照樣放行，而那份產物是混合版。
- 合之前先核 HEAD：主線動過 ⇒ 合完照**合併後的來源**重建產物，⛔ 不要拿分支裡舊底建的產物蓋掉別人的修正。
- 動手前先 `git status`：工作區有別人的改動 ⇒ 不是你的，⛔ 不要一起 stage、一起 build。
- `op=start` 被擋（範圍跟別人重疊）⇒ 等那一場退，或去問那個人；⛔ 不要為了進場把範圍宣告窄一點 —— 那是把撞車藏起來。

🩸 2026-10-16：有人在共用工作區建了遊戲成品，包進另一個人改到一半的 js ⇒ 開砲後整場停住，Tim 以為是卡死。當時建成品的人只有 FreeTime 場，這道閘根本沒機會叫 —— 這是改成「預設 worktree、合併才進場」的起因。

## 4. 合完就砍掉 worktree

```bash
git -C <repo> worktree remove <worktree 路徑>     # 合完當場砍
git -C <repo> branch -d <分支>                     # 已合進主線的分支也一起刪（-d 沒合過會拒絕，⛔ 不改用 -D）
```

- 誰合誰砍；worktree 裡還有沒提交的東西（`git -C <worktree> status` 不乾淨）⇒ 先問作者，⛔ 不帶 `--force` 硬砍別人的。
- ⛔ 合併之前不要先砍（合的人要從分支拿）；分支在 worktree 砍掉之後還在，但沒人知道該不該留。
- **有子模組的 worktree 砍不掉**：git 直接拒絕（working trees containing submodules cannot be moved or removed），先 `submodule deinit --all` 也一樣。這時照順序收，每一步讀到結果才走下一步：
  1. 分支已經合進主線：`git -C <repo> merge-base --is-ancestor <分支> <主線>`，exit 0 才往下；
  2. `git -C <worktree> status` 乾淨（子模組也是）；
  3. 刪掉 worktree 資料夾 ⇒ `git -C <repo> worktree prune` ⇒ `git -C <repo> branch -d <分支>`。

## 5. 施工中（合併那一場）

- 每一段做完跑 `op=status --arg status=<一句>`：順便續期（租期預設 2 小時，到期不會自動放掉，只會變殘留）。

## 6. 退場

- 綁了單 ⇒ `senate cmd commit` 推單後單子進 `in_review` 就自動收，不用記得收。
- 沒綁單、或做完了單子還沒離開施工狀態 ⇒ `op=end`。**合完就退**：宣告了產物的場掛著，別人就不能合、不能 build。
- 退場閘依每條施工路徑找所屬 .NET 工程跑 `dotnet build`：
  - 綠燈 ⇒ 退。
  - 紅燈、紅的是你範圍內的 ⇒ 修完再退。紅的不是你的 ⇒ 去問那個人，⛔ 不替別人改。
  - **找不到 .NET 工程**（JS、Python…）⇒ 閘驗不了，一定紅燈。自己驗過（語法檢查、建置、測試）再
    `--arg force=1 --arg force_reason="<驗了什麼、讀數>"`。理由會落檔，⛔ 不寫「沒問題」這種沒有讀數的理由。
