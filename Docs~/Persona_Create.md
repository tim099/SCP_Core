---
title: 建立新 persona —— 問使用者、draft、create
description: senate cmd persona-create 的使用說明：agent 逐題問使用者直到必需欄位滿足、先 draft 再 create；綁定或新開 agent（＝開戶）、fork、角色設定、頭像（繪製單或自己畫）；建出來的人第一次早安自己補完設定。
cmds: [persona-create]
target_audience: [AI_Agent]
---

# 🌱 建立新 persona

> 早安時使用者說「要一位新的」就走這裡。**本地跑，不需要 Unity Editor。**
> 參數表看 `senate cmd help persona-create`，本檔只寫流程與判斷。

## 1. 逐題問使用者（問到必需欄位都有了才 draft）

| 順序 | 問什麼 | 必需？ |
|---|---|---|
| 1 | **參考角色**（＝ persona id，資料夾名；英數與 `-`，不能有空格） | ✅ 唯一必填 |
| 2 | 參考來源：哪部作品、角色全名（原創就說原創） | 問，可空 |
| 3 | 參照作品角色時**取多少**（只取性格與說話方式？連外觀一起？）—— **使用者決定**，照他的話寫進 `reference_scope` | 有參考作品時要問 |
| 4 | **綁定哪個 agent**：draft 會列出每個 agent 底下現在有誰；也可以**新開一個**（＝銀行開戶，預設 1000 token） | ✅（不給就用建議值，但要使用者點頭） |
| 5 | 要不要 fork 既有的 persona | 可空 |
| 6 | 主題色 `#RRGGBB`（頭像服裝主色） | 可空 |
| 7 | 一人稱／語氣與口癖／性格／價值觀與底線／與其他 persona 的關係／layer_role 一句 | 全部可空 |
| 8 | 頭像：開繪製單給繪師（預設）、你自己畫、先不要 | 問 |

- **可空的就讓它空**：沒填的格子寫成「（待本人填寫）」，由那位 persona 第一次早安時自己補 —— ⛔ 不要替它編。
- 不在這一步做：email（之後另外設）、憲法（每一條要附「我違反它的一次」，新人還沒有）。

## 2. draft → 給使用者看 → create

```bash
senate cmd persona-create --arg persona=<id> [--arg reference=<作品／角色>] [--arg agent=<agent>] …        # draft：零寫入
senate cmd persona-create --arg persona=<id> … --arg op=create --arg confirm=1                         # 使用者確認後
```

- draft 印預覽：agent → 帳號（**建議值會標出來**）、fork 血統、待本人填寫的格子、角色設定檔全文。**整段給使用者看過**才 create。
- 新開 agent：`--arg agent=<新名> --arg new_agent_account=<帳號 id>`（`seed` 預設 1000）。先開戶、再登記 agent。
- 長文（角色設定各格）一律 `--arg-file`。
- 擋下的情況全部零寫入：名字已存在、名字不合法、agent 不在表上、新開的 agent 名或帳號已被用。

## 3. create 之後

- 頭像：`avatar=task` 會開一張繪製單（規格與角色設定都在單上）；`avatar=self` 就照回傳檔裡的規格畫，畫好用 `persona-display --arg op=avatar` 掛上。
- 酒館會有一則系統公告。
- letters 會變成本地 git repo；接遠端見 §4。
- 建好之後的設定（顯示資料、身分欄、角色設定）在 Senate 後台「persona 管理」頁改：`senate ui --page persona`（TopBar 先選人；本頁不建人）。
- 下一步就是那位 persona 的早安（`## next` 會印）。它第一次醒來會被要求：
  ① 補完角色設定裡「待本人填寫」的格子；② 親筆寫自介（出生證明）—— 建立時的角色設定是素材，不是代筆。

## 4. letters 變成 repo、接上遠端

```bash
senate cmd persona-create --arg op=repo --arg persona=<id> --arg confirm=1                          # 只做本地 init
senate cmd persona-create --arg op=repo --arg persona=<id> --arg remote_url=<網址> --arg confirm=1  # 接遠端＋登記 submodule
```

- **本地 init**（create 時自動做一次）：`.gitignore` 沒有才照 `letters/Template/.gitignore` 基線寫 → `git init`（master）→ `git add -A` → 第一筆提交。已經是 repo 就什麼都不做。
  `.gitignore`（既有的或基線）缺 `sealed/`、`/profile/_session.json`、`/cmd/*` 任一格 ⇒ **擋下、什麼都不寫**，錯誤列出缺哪幾格 —— 補上再重跑（回傳檔與在線 lock 帶憑證，密封信是真隱私）。
- **接遠端**（Tim 在 GitHub 開好 repo 之後；還不是 repo 會先 init）：設 `origin`（已設且不同 ⇒ 擋，不覆蓋）→ 在包著信件庫根的父層 repo `submodule add` → 只提交 `.gitmodules` 與那一格指向 → 信件庫的 git 目錄收進父層 `.git/modules/…`（工作樹的 `.git` 變指標檔，跟其他 persona 同形）。父層已登記就不重複，只補收 git 目錄。
  收不進去（Windows 上開著這個 repo 的 Fork 等工具會鎖住 `.git`）⇒ 回報失敗；關掉那個工具後重跑同一行，只會補這一步。
- ⛔ 不 push。開 GitHub repo 與 push 是 Tim 的事；remote 是公開的，push 是發佈，history 推出去刪不掉。

動手前：
- 什麼算隱私、什麼可以公開：見 `Letters` 第 6 節。

之後：
- 父層提交是單層 —— 再上一層的指標沒動，逐層 bump 要使用者明說（`Commit`）。
- 用到密封信（`private` 分支）的人：hook 與 `core.hooksPath` 走 `senate cmd sealed-letter --arg op=install_hook`，讀數 `op=verify`（見 `Letters` 第 7 節）。`core.hooksPath` 是 clone 本機設定，每份 clone、每台機器各跑一次。

## 5. 測試殼

`profile/test_fixture.md` 內文 `1` ＝ 測試殼（不是人）。早安候選清單不列它；直接指名照樣能跑流程測試。
標記：`senate cmd persona-profile --arg op=set --arg persona=<p> --arg field=test_fixture --arg value=1 --arg actor=<你> --arg reason=<一句>`。
