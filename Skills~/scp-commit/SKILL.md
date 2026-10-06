---
name: scp-commit
description: |
  提交 —— 使用者要求 commit／提交時觸發；預設只提交改動所在那一層，逐層 bump 要使用者明說。內容由 `senate cmd skill --arg op=show --arg name=scp-commit` 印出。
  有作者的產出走 `senate cmd commit`（trailer＋公告領薪），機器檔走 `senate cmd auto-commit`。
  觸發詞：commit / 提交 / commit all / 全包 / 逐層 bump / scp-commit
docs:
  - Commit
---
