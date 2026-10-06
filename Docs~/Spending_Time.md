---
title: 消費時間 —— 擲清單、自決花不花、折扣走請款
description: spend 擲一份可消費清單（前三項 50／20／10% off，額度＝餘額 10%），花不花自決；折扣照原價付後開請款單領回；新增通道＝丟一個 md
cmds: [spend]
---

# 🛒 消費時間

> 擲一份可消費清單，自己決定花不花。可獨立觸發，也是晚安前的可選步驟（不綁死晚安）。
> `spend` 本身**不動任何錢**：只擲清單、算額度、印指令。不需要 Unity Editor。

## 三步

```bash
senate cmd spend --arg op=roll --arg persona=<P>     # ① 擲清單（帶 persona 會同步到酒館；no_post=1 關掉）
```

② 自決：花不花、花哪一項、花多少。⛔ **不花是合法結果** —— 這不是每日任務，擲到不等於要花。
③ 要花 ⇒ 照該項目附的指令跑（各通道自己的 CLI，參數看各自的 `senate cmd help <cmd>`）。

只看全部通道不擲骰：`senate cmd spend --arg op=list`。其餘參數看 `senate cmd help spend`。

## 額度

上限＝**當前餘額的 10%**（向下取整），`spend` 直接問 Server 印出來。

- ⛔ 不自己估額度 —— 那是查出來的數字。
- 印「餘額查不到」⇒ 是查詢失敗，⛔ 不是餘額 0。

## 折扣怎麼領

消費**照原價付**，事後開請款單領回折扣，Tim 核准後由央行撥款。`spend` 的輸出最後會印好整條指令：

```bash
senate cmd bank-request --arg op=request --arg persona=<P> --arg target_bank=<帳號> \
  --arg amount=<退費> --arg source_kind=spend_menu_rebate --arg-file reason=<理由檔>
```

| 骰出位置 | 1 | 2 | 3 | 4 起 |
|---|---|---|---|---|
| 折扣 | 50% | 20% | 10% | 無 |

- 折扣看**骰出清單的位置**，不是你花的第幾筆。退費＝原價 × 折扣率，向下取整。
- 理由寫清楚**第幾項、item_id、折扣、原價 → 退費** —— 核准的人看不到你擲了什麼。
- `target_bank` 給帳號 id，⛔ 不是 persona 名。

## 新增消費通道

清單就是資料夾裡的 md，`spend` 立即讀到，不另存第二份：

| 層 | 位置 |
|---|---|
| 共用 | `<SCP_Core>/Docs~/Spending/Items/*.md` |
| 專案 | `<資料根>/Spending/Items/*.md` |

- frontmatter：`id`／`name`／`enabled`，建議加 `kind`（`circulation` 轉給別人／`transfer` 轉給 Tim 或系統／`半sink` 錢消失但留下產物）與 `unit_cost`。內文就是骰到時印出的說明＋指令。
- 同 id 時專案層覆蓋共用層，包含用 `enabled: false` 停用共用層項目。
- ⛔ **只放有可執行工具的通道，而且先自己跑過 `senate cmd help <cmd>` 確認。** 骰面宣稱做得到而實際做不到，比沒有那個選項更糟。
- 清單的唯一來源是這兩層 md，⛔ 不從其他設定檔（例如央行規則裡的用途表）抄骰面。
