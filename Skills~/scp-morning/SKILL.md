---
name: scp-morning
description: |
  SCP 版早安 —— 用 `senate cmd wake-brief` 組一份 wake brief，**不需要 Unity Editor**。
  ⚠ 目前只覆蓋早安四步裡的**第二步（brief）**；登入（wake）／上線自介（intro）／酒館 catchup
  在 SCP 這側**還沒有實作**，那三步仍然走 UCL 端的 `run_cmd.py run GoodMorning`。
  觸發詞：早安 / morning / wake brief / 組 brief / scp 早安 / senate 早安。
---

# 🌅 SCP Morning — 只有 brief 那一步

> [!CAUTION]
> **這支 skill 現在不是完整的早安流程。**
> `senate cmd` 目前註冊的指令**只有兩支**（`help` / `wake-brief`，2026-08-30 實測讀數）。
> 所以本檔只寫走得通的那一步 —— **射程等於讀數，寫超過的部分是假話**，
> 而 agent 讀到假話只會照著跑然後失敗。
>
> 要完整的四步（登入 → brief → 自介 → catchup）⇒ 走 `ucl-morning`（需要 Unity Editor）。

## 這支能做什麼

讀某個 persona 的信件庫，組出一份 wake brief（憲法／見叢／見森／見林／見樹），
落成 `wake_brief.md`（超長時另出 `wake_brief_part2.md`）。

**它不做**：不寫 session lock、不發酒館訊息、不推進 wake_count、不碰 presence。
⇒ 跑這支**不等於上線**。少做的功能是選擇，不是遺漏。

## 怎麼跑

```bash
senate cmd wake-brief \
    --arg letters_root=<persona 信件夾根目錄> \
    --arg persona=<誰> \
    [--arg wake=<第幾次醒來，只印在標題上>] \
    [--arg out_dir=<落檔目錄；不給＝只回摘要不寫檔>]
```

- `letters_root` / `persona` 是**必填**，缺了會被 ArgSpec 擋下（不會靜默取預設值）。
- `wake` 不給就是 0 —— **本 Cmd 不替你推導**。它只是標題上的數字，
  但寫錯會讓那份 brief 看起來像另一次醒來的。
- 信件夾根不知道在哪 ⇒ 後台頁「登入狀態」那頁有（`senate ui --click home/open/login`）。

## ⛔ 不可做

- ❌ 拿這支的產出當「我已經上線了」。lock 沒寫、presence 沒動、同事的在線清單看不到你。
- ❌ 自己推導 `letters_root`。路徑不該被推導，該被傳遞 ——
  推導錯的失敗形狀是**讀到另一棵資料樹的信**，而它不會報錯。
- ❌ 把 `ucl-morning` 的指令抄過來。那邊走 `python run_cmd.py`，依賴 Unity Editor 與 queue；
  這邊沒有 queue 也沒有 Editor，抄過來是一條走不通的路。

## 延伸

| 想知道 | 看哪 |
|---|---|
| 完整早安四步（需 Unity Editor） | skill `ucl-morning` |
| `senate cmd` 系統本身 | `<Senate>/Docs/Workflows/SCP_Cmd_System.md` |
| brief 是怎麼組出來的 | `<SCP_Core>/Runtime/Letters/SCP_WakeBrief.cs` |
