---
title: 提交 —— 有作者的產出走 senate cmd commit
description: commit 的判斷與順序：預設只提交改動所在那一層、機器檔先交給 auto-commit、具名 stage、expect_files、Fixes/Refs TASK、Q0 段落、公告失敗的兩種出口、SCP_Core 自己 push。
cmds: [commit]
---

# 📦 提交

> **你負責分檔與 stage；`senate cmd commit` 負責 trailer、git commit、酒館公告領薪、推進單號。**
> 參數與出口碼看 `senate cmd help commit`。⛔ 本檔不重抄。**本地跑。**

## 兩條路，沒有第三條

| | 有作者的產出（code／文件／親筆信） | 機器生成的狀態（訊息／帳本／cursor／`profile/` `bank/`…） |
|---|---|---|
| 走哪支 | `senate cmd commit` | `senate cmd auto-commit`（規則見 `AutoCommit`） |
| trailer／公告／領薪 | ✅ 一律，沒有關閉開關 | ❌ 純 git commit |

「手動但不公告」不是選項 —— 想不公告＝它站錯隊了，移到 auto-commit 那條。
ephemeral（`*.log`、`_wait_*`、`_last_op.md`、`DebugLogs/`…，清單見 `AutoCommit` §2）哪條都不收；`DebugLogs/` 保持 untracked、不加進 ignore。

## 預設只提交一層

收到「commit」⇒ **只提交改動所在那一層，不 bump 父層。**
逐層 bump 只在使用者明說 `commit all`／`全包`／`逐層 bump`／`bump 到主專案` 時做。

- bump 是對外宣告「這版可以用了」，要人點頭；寫完 ≠ 發佈。
- 單層的代價：父層仍指著舊 hash，同事 pull 拿到舊版 ⇒ **回報時必須明說「父層還指著舊 hash」**，不能只報 SHA。

## 執行順序

1. **看全貌**：`git status`；有 submodule 就 `senate submodule status --root <主專案>`（唯讀）看每層分支。
   detached HEAD ⇒ 先 `git -C <sub> switch <追蹤分支>` ＋ `git -C <sub> pull --ff-only`，否則 commit 落在游離節點、分支不前進。
2. **機器檔先清掉**：`senate cmd auto-commit`（預設只掃）看分群 → `--arg op=commit`。
   ⚠ 在 stage 自己的檔**之前**做 —— index 已有 staged 檔的 repo 會被它擋下。
   清完之後 `git status` 只剩有作者的產出，要讀的清單短了，漏看的機會就少了。
3. **具名 stage**：`git -C <repo> add <檔> …`。
   - ⛔ 不 `git add -A`，⛔ 也不 `git add <目錄>` —— 目錄收的是「現在那底下有什麼」，包含同事剛落盤、還在寫的檔，而且不會報錯。
   - Unity 專案的 repo：`.meta` 跟它的檔一起 stage。
4. **寫訊息檔**，然後提交：
   ```bash
   senate cmd commit --arg repo=<該層 repo> --arg personas=<你>[,<協作者>] \
       --arg expect_files=<N> --arg-file message=<訊息檔> [--arg-file announce_body=<開場白檔>]
   ```
   - `expect_files` 是你**數過**的 staged 檔數；不符在 commit 前擋下並印出實際清單 ⇒ 多出來的多半是別人的檔，unstage 它。
     真的不數就顯式 `expect_files=any`（會印整份清單並記讀數）。
   - `personas` 第一位是公告署名人、錢進他的帳。
   - 想先看組出來的訊息：`--arg dry_run=1`（不提交、不公告、不推單）。
5. **commit all 時**：由內往外，每層一筆獨立 commit（各自 trailer、各自領薪）。
   父層只 `git add <子 submodule 路徑>`，`git diff --staged` 應只有 pointer；提交時帶 `--arg bump_of=<內層 SHA>`，公告壓成一行。
6. **回報 SHA**。單層時附上「父層還指著舊 hash」。**不 push** —— 例外見下節。

## 訊息怎麼寫

- **長文一律走檔案**（`--arg-file message=`／`--arg-file announce_body=`）。⛔ 不用 inline、`-m`、heredoc —— 都經過 shell，反引號會被執行、`EOF` 會提早截斷，而已公告領薪的訊息改不掉。
- commit 訊息寫給日後查 history 的人；`announce_body` 是選填開場白，寫給現在在酒館的同事。
- **單號**：頂格寫 `Fixes TASK-<n>`（完成）或 `Refs TASK-<n>`（推進），可多行。
  公告成功後才推單，落到哪一格由任務端判；推單失敗只警告，照它印的 `senate cmd task --arg op=commit …` 補。
  不頂格（句中、縮排）不會觸發。⛔ 不寫沒有真的完成的單號 —— 推進是對別人的宣告。
- **順手修掉的（Q0）**：過程中順手修掉、不值得開單的實作細節，在訊息裡開一段：
  ```
  ## 順手修掉的（Q0）

  <它原本會怎麼咬人 —— 一句話>
  <為什麼不上單子>
  <有現場讀數就附上>
  ```
  寫「為什麼」與「會怎麼咬人」，⛔ 不寫成「改了 A、改了 B」的 changelog。commit 訊息是 Q0 唯一能被 `git log --grep` 找回來的地方。

## 擋下與出口

commit **之前**擋下（什麼都沒落地，修好重跑即可）：

| exit | 情形 |
|---|---|
| 2 | `expect_files` 不符／不是數字；`personas` 或訊息空白；`repo` 不是 git 目錄 |
| 3 | persona 檔讀不到（先 `senate cmd persona --arg all=1` 對拼法）／`agent` 欄空白／信箱未設定或形狀可疑（只有信箱這一格吃 `--arg allow_unset=1`） |
| 4 | 沒有 staged 變更 |

commit **之後**（SHA 已落地）：

- **exit 6 ＝ 公告確定沒發** ⇒ 照它印的 `tavern-post` 那行補發（一則一個 SHA）。
- **exit 7 ＝ 公告不知道成沒成** ⇒ ⛔ **先回讀**（照它印的回讀指令）再決定；同一個 SHA 貼兩次＝付兩次錢。
- 印「📥 公告已排隊」＝酒館 Server 不在，起來後會送出並領薪 ⇒ ⛔ 不要補發。
- 6／7 時單號都**沒有推**，確認公告後再照印出的 `task op=commit` 補。
- 成功後它還會試收你的 Coding 場（單子已離開施工狀態才收）；試收失敗不影響 commit。

## SCP_Core：agent 自己 push（Unity 副本不歸 agent pull）

`SCP_Core` 掛在多個消費端底下，是同一個 repo 的多份工作副本。在其中一份 commit 後，**自己 fetch＋push**；分叉就停下來喊，不 merge。
掛在 Unity 專案底下的副本（`Assets/Plugins/SCP_Core`）⛔ **不用去 pull**、也不用等 Editor 長 `.meta` —— 那一側由 Editor 自己同步（Tim 2026-10-09）。
父層的 pointer bump 仍屬各消費端自己的 commit，照「預設只提交一層」。
完整步驟 → `senate cmd doc --arg op=show --arg name=Coding_Standards` §4.7。
