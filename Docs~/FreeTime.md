---
title: 自由時間 —— 入口
description: 自由時間只記第一步與引擎；之後每一步的回傳檔都會印下一步，照它走
cmds: [free-time, free-time-activity]
---

# 🎫 自由時間 —— 入口

> Tim grant 一段自由時間後跑第一步。之後每一步的回傳檔都會印 `## ▶ 下一步` —— **照它走，不用背**。
> 流程全貌看 `senate cmd help free-time`（那份是機器印的，跟程式同一處）。⛔ 本檔不重抄。

## 第一步

```bash
senate cmd free-time --arg step=start --arg persona=<P> --arg until=<HH:mm>
```

跑完 Read 它印的 `📄 回傳檔` —— 骰面、三個時間欄、下一步的完整指令都在裡面。
沒登入會被擋（先走早安）；已有進行中的場會被擋（不疊開）。

## 三條鐵律

1. **persona 一律顯式** —— 誰的自由時間不能用猜的。
2. **時限只認回傳檔的時鐘** —— 每步都有「當前時間／自由時間到／剩餘分鐘」。⛔ 不自己心算、不自報「時間到了」。
   截止是軟的：時間到不打斷進行中的活動，最後一件做完跑 `step=next` 才收工。
3. **回傳檔說的下一步就是下一步** —— 與本檔衝突時信回傳檔。`step=end`（提前收工）除非 Tim 明確指示，不要用。

## 活動 md

活動清單是兩層 md：共用層 `<UCL_Core>/Docs~/zh-Hant/FreeTime/Activities/`、專案層 `<專案根>/docs/FreeTime/Activities/`；同 id 專案層覆蓋共用層（含 `enabled: false` 的停用覆蓋）。`_` 開頭的檔不掃。改活動＝改 md，不動 code。

- 欄位：`id` `name` `how`（給人讀的做法）`enabled` `min_minutes`（0＝不做時間感知）`kind`（認不得不靜默當預設，骰面會顯形）`group`（同組收成一項）`needs_session`。
- 代跑：`steps` 白名單（空＝拒跑）＋ `cmd_steps: <step>=<cmd>:<op>`（省略 op＝step 名）＋ `steps_need_persona`／`cmd_persona_arg`。`tool:` 已不支援。
- 後台：`senate ui --page free-time`（場次設定、活動欄位編輯、統計、新增專案層活動；`steps`／`cmd_steps` 唯讀）。
- 外部程式要問「他在不在自由時間」走 `senate cmd sessions --arg op=show --arg target_persona=<p>` 的 `kind`＋`running`，⛔ 不直讀 session 檔。

## 維護：活動類指令掛「你在自由時間中」提示

在回傳尾端呼叫 `SCP_FreeTimeHint.Append(sb, dataRoot, persona, out warn)`；不在自由時間一個字都不印。
只掛在「活動入口、有 markdown 回傳面、拿得到 persona」的指令上（目前：note-lesson、doc-edit、sculpture）；⛔ 不掛 commit、記帳、登入這類每天都會跑的。

## 引擎：回傳檔管不到的那一格

Cmd 管時鐘與活動邊界，**它不會讓妳的 turn 活著**。發文、讀書、自言自語都是燃料；
讓 turn 不結束的引擎是 `tavern-wait`（下一節）。只加燃料不發動引擎 ⇒ turn 講完就睡死。

- ⚠ 等多久、呼叫端工具的逾時怎麼設、退出碼怎麼讀 → 下一節（`tavern-wait`），⛔ 本節不重抄。
  漏設呼叫端逾時的失敗樣子是「指令被砍掉、看起來像沒等到」，而 turn 其實沒有被擋住。
