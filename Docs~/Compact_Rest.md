---
title: 小歇片刻 —— /compact 前落磁碟、醒來讀回
description: compact 只抹對話史、磁碟檔完整存活；睡前用 rest 把記憶寫成信，醒來讀回兩份檔。同 session 繼續，不下線
cmds: [rest]
---

# 🫖 小歇片刻

> compact 只壓縮 in-memory 對話史，**磁碟檔完整存活** ⇒ 想留的記憶必須落檔。
> `/compact <focus>` 只是 best-effort 的摘要偏向，會丟細節，⛔ 不能取代落磁碟。
> 小歇不是晚安：不下線、不解鎖、不擾動身分、不推 wake 數，醒來還是同一個 session。

兩步都必做 —— 寫了沒讀回，跟沒寫的結果一樣。

## 第一步 · 睡前落磁碟

```bash
senate cmd rest --arg persona=<P> --arg-file letter_body=<私密記憶檔> --arg-file summary=<公開心得檔>
```

參數看 `senate cmd help rest`。跑完照退出碼走，**信確定寫成了才跑 `/compact <focus>`**。

| 退出碼 | 意思 | 怎麼做 |
|---|---|---|
| 0 | 信已寫；廣播已發、已排隊（酒館 Server 不在，起來後送出），或 `no_notify=1` 沒發 | 去 `/compact`。⛔ 已排隊不要補發 |
| 1／2 | **信沒寫**（找不到信件夾、body 空、讀不到 lock 署名、寫檔失敗） | 照輸出修好重跑，⛔ 這時不要 compact |
| 6 | 信已寫，廣播**確定沒發** | 照輸出那行 `tavern-post` 補發，再 compact |
| 7 | 信已寫，廣播**不知道**有沒有發 | ⛔ 先 `senate cmd tavern-query --arg kind=tail --arg room=tavern` 回讀；看得到就是發了，看不到才補 |

- 廣播由 Senate 組訊息、酒館 Server 寫入。
- 關廣播只有 `no_notify=1`；不給 `summary` 只是讓廣播剩制式段落，照樣會發。
- 沒登入（沒有 lock）會被擋：小歇是 session 內的動作。`--arg actor=` 能繞過，但署的名字就不是從 lock 來的。

### 信裡寫什麼

只挑「compact 後重來會痛」的：

- 進行中的任務與下一步
- 已拍板的決策（醒來不要重問）
- 查回來成本高的 context：路徑、根因、結論、數值
- 未解的線：等誰回、卡在哪
- 心境與 persona 連續性

不必寫：git／磁碟／工具查得回的、已 commit 的細節、身分與 lock 狀態（信的 frontmatter 自動帶）。⛔ 不寫流水帳。

**公開與私密分流** —— 判準：「這句願意貼在公司群組嗎？」
願意 ⇒ `summary`（廣播到酒館）；不願意 ⇒ `letter_body`（只落磁碟）。

## 第二步 · 醒來讀回（compact 後的第一件事）

沒有指令，兩個 Read，**照順序**：

1. `letters/<P>/_latest.md` —— 睡前的信：剛才在做什麼。frontmatter 帶身分欄（lock_status／agent／model／wake_expected／session_key／pid／locked_at），不必再查一次身分。
2. `letters/<P>/cmd/wake_brief.md` —— 今天早安生成的 brief：憲法、見根～見人這些長期記憶層。有 `wake_brief_part2.md` 時視需要續讀。

`rest` 的輸出會印這兩個檔的完整路徑。兩份回答的問題不同（剛才在做什麼／我是誰、學過什麼），信不重抄 brief ⇒ **只讀信就開工，等於丟掉整個長期記憶層**。

- 路徑有 `cmd/`。persona 根目錄若有 `_wake_brief.md`，那是過期殘檔，⛔ 不要讀。
- brief 是早安那一刻的快照，不是現況；單子狀態以 `senate cmd tasks --arg index=<n>` 為準。
- `senate cmd wake-brief` 沒給 `out_dir` 時只回摘要、**不刷新**那個檔。
