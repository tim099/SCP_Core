---
title: 自動 Commit —— 機器生成的檔分群整批提交
description: senate cmd auto-commit 與 Senate「自動 Commit」頁共用的引擎：掃描範圍（AgentCommands＋全部信件庫＋有設定檔的 submodule，不分模式）、兩張寫死的分群表、.ucl_autocommit.json 設定檔與地板、四道硬擋、提交流程與機讀值。
cmds: [auto-commit]
last_updated: 2026-10-01
target_audience: [AI_Agent, Tools_Maintainer]
related:
  - Coding_Standards.md | SCP 撰寫規範 | 方言／SCP_Json／prefs／路徑單一落點
  - <Senate>/Docs/Architecture/Ui_Framework.md | UI 框架 | 頁面的 id 規則與四種驅動方式
  - Commit.md | 提交 | 有作者的產出走 senate cmd commit，機器檔走這裡
---

# 🤖 自動 Commit

把**機器自動生成的檔**分群，每群各自成一筆 commit，訊息自動生成。
兩個入口、**同一支引擎**（`SCP_AutoCommit`）：

| 入口 | 誰用 | 預設 |
|---|---|---|
| `senate cmd auto-commit` | agent（`/scp-commit` 流程） | `op=scan`（純讀）；要提交得顯式 `op=commit` |
| Senate 後台「自動 Commit」頁（`senate ui --page auto-commit`） | 人按 | 攤出分群與完整清單 → 勾選 → 兩段式確認 |

> [!IMPORTANT]
> **按鈕觸發，不是背景全自動**（Tim 2026-08-07）。
> **不 push、不動父層 pointer**；走純 git commit，⛔ **不走 `senate cmd commit`** ——
> 那支的 trailer／酒館公告／領薪是給「有作者的工作產出」用的，這裡收的是機器生成的狀態殘渣，掛誰的名字領誰的薪都是假帳。

## 1. 掃描範圍（Tim 2026-09-30：不再分模式）

一次掃完、一張清單。**一個 repo 只有一個規則來源**：

| 來源 | 怎麼找到 | 規則 |
|---|---|---|
| AgentCommands 本層 | 資料根自己 | `SCP_AutoCommitRules.AgentGroupDefs`（寫死） |
| persona 信件庫 | letters 根底下**每個**有 `.git` 的目錄 | `SCP_AutoCommitRules.PersonaGroupDefs`（寫死） |
| 設定檔 repo | 資料根 `.gitmodules` 的 submodule，**有 `.ucl_autocommit.json` 才收** | 該檔宣告 |

- 提交順序：設定檔 repo → 信件庫 → **AgentCommands 本層最後**（子 repo 先 commit，父層那一輪看到的 pointer 才是新的；但 pointer 群仍然永不自動收）。
- ⚠ 信件庫裡出現 `.ucl_autocommit.json` ⇒ **擋下**：兩份規則同時宣稱管同一個 repo，挑哪一份都是猜。
- ⛔ **沒有在線守衛**（Tim 2026-09-30）：自動 commit 管理的部分**不應該手動 commit** ⇒ 那些群收的是機器獨佔的檔；
  親筆檔本來就不在任何一群裡。分界是「這個檔誰在寫」，不是「這個人在不在線」。

## 2. 分群規則

判定順序是**地板**：`subptr → ephemeral → 具名群（第一個命中的收走）→ 未分類`。設定檔宣告的前綴排在 ephemeral 之後，**掀不動它**。

### AgentCommands 本層

| 群 | 命中 | 預設 |
|---|---|---|
| `chat` | `ChatTavern/rooms/`、`ChatTavern/rooms_archive/` | ✅（`[chat]` 獨立 commit 是硬規則） |
| `treasury` | `Treasury/` | ✅ |
| `bank` | `Bank/`（新銀行，跟舊帳本分群） | ✅ |
| `runtime` | `ChatTavern/` 其餘、`AwakenInit/`、`Canvas/`、`Inbox/` | ✅ |
| `queue_state` | `PromptQueue/` **頂層**的 `*_state.json`（⛔ 不含同目錄的 .py） | ✅ |
| `lessons` | `Lessons/` | ✅ |
| `plurk_audit` | `Plurk/post_audit.jsonl`（⛔ 不含同目錄的親筆交付單） | ✅ |

### persona 信件庫

| 群 | 命中 | 預設 |
|---|---|---|
| `mailbox` | `mailbox/`、`outbox/` | ✅ |
| `portraits` | `portraits/`（他人投遞） | ✅ |
| `profile` | `profile/` | ✅ |
| `bank` | `bank/`（區域 → 帳號） | ✅ |
| `vouchers` | `vouchers/`（⚠ 券不記歷史，丟了補不回來） | ✅ |
| `portfolio` | `portfolio/`（投資組合帳：開帳快照＋交易事件；成本來源只住這裡） | ✅ |
| `bookshelf` | `bookshelf/`（reader.json 的機械投影） | ✅ |
| `writing_dossier` | `writing/`（publish 的機械投影） | ✅ |
| `sketchbook_raw` | `sketchbook/<target>/raw/`（⛔ 不含親筆濃縮檔） | ✅ |
| `relationship` | `relationship/`（⛔ 不含親筆 `opinions/`） | ✅ |
| `keys` | `_keys_open.md`（唯一寫入端 `senate cmd keys`） | ✅ |
| `letters_mech` | `_latest.md`、`cmd/.gitignore` | ✅ |

⚠ **新增一種 letters 產物就必須同時改這張表** —— 否則它落未分類、永遠不會進版控而且不會叫
（`writing/` 2026-08-23、`vouchers/` 2026-09-22 兩次血證）。

### 三個特殊群（永不自動收，要收得顯式指定）

| 群 | 是什麼 |
|---|---|
| `__subptr` | 巢狀 submodule pointer，**以及還沒登記進 .gitmodules 的巢狀 repo**（status 裡以 `dir/` 結尾的條目） |
| `__other` | 規則沒認出來的已追蹤檔（⚠ 不等於「機器生成」—— 可能是有作者的產出） |
| `__other_untracked` | 規則沒認出來、且從未進版控的檔（那是別人做過的決定，替他翻案要顯式） |

ephemeral（永遠不進候選）：`*.log`、`*.tmp`、`_last_op.md`、`_last_view.md`、`_active_waits.json`、`_wait_*`、`pending.trigger`、`DebugLogs/`。

## 3. 設定檔 `.ucl_autocommit.json`

放在該 repo 根。**設定檔是加入的唯一憑據**（沒有就不收，不猜規則）。欄位名沿用 UCL 時代（磁碟上的契約）：

```json
{
  "Enabled": true,
  "Name": "Chess",
  "Groups": [
    { "Key": "games", "Label": "對局狀態", "MatchPrefixes": [ "games/" ],
      "Message": "chore(chess): sync game state (auto)", "DefaultOn": true }
  ]
}
```

| 保證 | 靠什麼 |
|---|---|
| ephemeral 與特殊群掀不動 | `Classify` 的判定順序（結構保證，不是「記得先檢查」） |
| 錯配一眼可驗 | 只吃前綴清單，不吃 regex |
| 壞檔 ≠ 沒設定 | 讀檔三態 `Missing / Ok / Error`；型別不合（例：`"Enabled": "false"`）**判壞檔**，⛔ 不靜默換成預設值 |
| 寫入前擋下壞設定 | `Save()` 先跑 `Validate()`：保留群名、重複 key、空前綴、反斜線、空 Message、「啟用但沒有分群」 |
| 不吃掉別人寫的東西 | 未知頂層欄位原樣保留；舊檔解析不了就**不覆蓋**；temp → 原子取代 → 回讀比對 |

- ⚠ `Enabled` **欄位缺席＝啟用**（相容舊檔）；新建一律顯式 `false`（「選了一個 submodule」不等於「同意開始自動 commit 它」）。
- 編輯：Senate 頁的「⚙ Submodule 自動提交設定」區（inspector 反射繪製，加欄位頁面不用改）。
- **該不該加入**：repo 裡有機器生成、沒有作者的檔才加。有作者的產出走 `senate cmd commit`；同一個 repo 可以兩種並存 —— 沒命中任何群的落 `__other`，永不自動收（設定檔本身就是這種）。
- 欄位：`Name` 空＝目錄名；`Message` 尾巴自動補 `[N files]`；`DefaultOn=false`＝沒指定 `groups=` 時不做；`Groups` 順序即優先序（窄前綴放前面）；前綴比對大小寫敏感。
- ⚠ 拼錯的鍵名會被當成未知欄位原樣保留、不報錯 ⇒ 加入後放一個探針檔到某群前綴下跑 `op=scan`，看到它落進那一群才算通，驗完刪探針。

## 4. 四道硬擋

1. 特殊群永不自動收（見 §2）。
2. **detached HEAD** ⇒ 擋下（commit 會落在游離節點）。
3. **呼叫前 index 已有 staged 檔** ⇒ 提交擋下（BUG-30：它們會被併進第一個群、掛上那個群的訊息，而 commit 會成功）。`op=scan` 只警告。
4. 設定檔壞掉／不合法 ⇒ 擋下並說為什麼。

## 5. 一群一筆 commit 的流程

具名 `git add -- <files>`（每批 40 個，⛔ 絕不 `git add -A`）→
`git commit -F <訊息檔> --pathspec-from-file=<清單> --pathspec-file-nul`（只提交這一群的路徑）→
回讀 SHA → **提交後對帳**（`git show --name-only -z HEAD` 對分群清單，只報多出來的）。

- 失敗 ⇒ **只 unstage 這一群的路徑**（⛔ 不整個 `git reset`、⛔ 絕不 `--hard`）；還原本身失敗也會說出來。
- 掃描之後檔案又被還原 ⇒ 記「跳過」，不算失敗。
- git 輸出一律 `-z`（NUL 分隔、不加引號不轉義）⇒ 中文／空白／引號路徑原樣拿到。
- 失敗理由挑第一條**不是** `warning:`／`hint:` 的行（🩸 hook 拒絕時 stderr 首行是 CRLF 警告，印它等於沒講原因）。
- 頁面在提交前**逐個 repo 重掃一次**（index 在確認之後被動過的話，守衛量的是現在）。

## 6. `senate cmd auto-commit`

| 參數 | 意思 |
|---|---|
| `data_root` | AgentCommands 資料根（CLI 依 Senate 後台設定自動帶入） |
| `letters_root` | persona 信件夾根（CLI 依 Senate 後台設定自動帶入） |
| `op` | `scan`（預設）｜`commit` |
| `groups` | 只做這幾群（逗號分隔，對所有 repo 一體適用）；不給＝各 repo 的 DefaultOn。特殊群只有列在這裡才做 |
| `only` | 只做這幾個 repo（顯示名；⚠ 打錯的名字 exit 2，不會靜默變成「沒東西可收」） |

機讀值**0 也印**（只在非零時才出現的欄位，讀者分不出「乾淨」與「沒量」）：
`repos` `candidate_files` `ephemeral_skipped` `commits` `failed_groups` `skipped_groups` `empty_groups`
`blocked_repos` `prestaged_repos` `disabled_repos` `other_files` `other_untracked_files` `subptr_files`
`locked_repos`（stderr 認得出 `index.lock` ⇒ 等一下重跑就好）`reconcile_mismatch` `failed_repos` `shas`。

- 對帳式：`candidate_files − other_files − other_untracked_files − subptr_files` ＝ 現在可自動收的檔數。
- 有群失敗或對帳不符 ⇒ **exit 1**（已成功的那幾群是真的，SHA 在 `shas`；這不是回滾，是拒絕把部分成功說成完成）。
- ⛔ 不重試、不刪 lock（刪別人的 lock 會讓那個 process 寫壞 index）。
- `disabled_repos` ＝ `Enabled=false`，不算 `blocked_repos`。加了設定檔 `repos` 卻沒多 1 ⇒ 設定檔不在 repo 根，或那個 repo 不在資料根的 `.gitmodules`。

## 7. Senate 頁面

- 工具列：「重新掃描」「儲存預設勾選」。內容：資料根／letters 根（**問宿主那一格，本頁不存路徑**）→ 設定編輯區 → 動作列 → 逐 repo 分群。
- 勾選 id：`autocommit/<掃描戳記>/<repo>/<群>`。**換戳記＝舊勾選失效**（重新掃描、提交完的自動重掃都會換）——
  特殊群的一次性勾選用完即棄；具名群的預設由存檔值重新給。
- 「儲存預設勾選」只存**跟規則預設不同**的那幾格（`<repo>:<群>`），寫 `senate.pages.local.json` 的 `auto-commit` 區塊；特殊群永不持久化。
- 提交：「Commit 勾選群組」→ 攤出每一筆 commit 的訊息 →「⚠ 確定 Commit」／「取消」。
- 設定區列各 submodule 狀態（✅ 已啟用／⏸ 停用／— 尚無設定檔／⛔ 設定壞掉），信件庫不列；不合法不畫存檔鈕，存檔成功自動重掃；「↩ 放棄改動」＝從磁碟重讀。

## 8. 血證（本次下沉）

- 🩸 **未登記的巢狀 repo 會被當成 runtime 檔收走**（TASK-0340 沙盒）：新信件庫 `ChatTavern/baton/letters/alice/` 被 `ChatTavern/` 前綴吃進 runtime 群（預設勾選），
  `git add` 會把它塞成沒有 `.gitmodules` 的 gitlink，而那不會報錯。UCL 版規則表同形。⇒ `dir/` 結尾的條目一律歸 `__subptr`。
- 🩸 **兩個入口、兩套守衛**：同一件事，Cmd 有 BUG-30 三道守衛、頁面沒有，而兩邊各自看起來都正常。⇒ 下沉後只剩一條提交路徑。
