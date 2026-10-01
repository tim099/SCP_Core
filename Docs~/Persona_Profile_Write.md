---
title: persona 設定寫入 —— 身分欄與區域銀行綁定
description: senate cmd persona-profile 的使用說明：身分欄 set／unset（純量欄與結構欄的差別）、區域銀行綁定 get_bank／set_bank／unbind、全 pool 的 migrate_bank 與 rebind_region、寫入審計，以及「綁定不是動錢」的邊界。讀整份 persona 走 senate cmd persona。
cmds: [persona-profile]
last_updated: 2026-10-01 (TASK-0354：從 UCL `ucmd run PersonaProfile` 搬到 Senate CLI；TASK-0361：唯一寫入端＋op=create)
target_audience: [AI_Agent, Tools_Maintainer]
related:
  - ucl_core:Docs~/{lang}/Workflows/Bank_Region_Binding_Migration_Workflow.md | 區域綁定遷移 | 新專案第一次設區域時的半自動流程（migrate_bank 的用法在那裡）
  - ucl_core:Docs~/{lang}/Plan/Plan_Persona_Registry_Retirement.md | persona registry 退場 | §8.2 一欄一檔／§8.3 欄位分家／§8.6 寫入接縫
---

# 🪪 persona 設定寫入

**本地跑，不需要 Unity Editor。** 讀取（整份 persona、某一欄、帳號）走 `senate cmd persona`；本指令只管寫。

> ⛔ **只寫 persona 檔＋一行審計** —— 不碰帳本、不動任何一分錢、不改央行設定。
> 綁定決定的是「**之後**的收付進哪一戶」；既有分錄 append-only，不追溯。

> ⭐ **這是 persona 檔唯一的寫入端**（TASK-0361，Tim 2026-10-01「寫入端整合到 Senate，Unity 端不留」）：
> Senate 銀行後台換綁、早安寫 model／actual_agent、Unity Editor 的頁面（建 persona、email、actual_agent、Plurk 帳號；
> 經 `UCL_PersonaProfileSenateBridge` spawn 本指令）全部走同一份 `SCP_PersonaProfileWrite`，稽核只有一份。

## 建 persona

```bash
senate cmd persona-profile --arg op=create --arg persona=<新名字> --arg account=<帳號 id> \
    --arg-file fields=<檔：JSON 物件，身分欄 → 值> --arg actor=<誰> --arg reason=<憑什麼>
```

- 先寫本區綁定、再逐欄寫身分欄（先有帳號歸屬，錢才不會在半成品狀態落央行）；每欄一行稽核，最後一行總結
  `profile:[..] skipped(推導欄):[..] refused(走 set_bank):[agent]`。
- 整份 `fields` 先驗完才寫（結構欄形狀不符 ⇒ 整筆擋下、零寫入）；`agent` 與推導欄不寫並明講。
- 名字已存在、空白、`_`／`.` 開頭、不能當資料夾名 ⇒ 擋。

## 身分欄

```bash
senate cmd persona-profile --arg op=set   --arg persona=<p> --arg field=<欄> --arg-file value=<檔> --arg actor=<誰> --arg reason=<憑什麼>
senate cmd persona-profile --arg op=unset --arg persona=<p> --arg field=<欄> --arg actor=<誰> --arg reason=<憑什麼>
```

| 欄 | 型別 | 檔 |
|---|---|---|
| `identity_vector`（數字）、`vector_history`（物件）、`fork_lineage`（字串） | **結構欄**：值必須是合法 JSON 陣列、每個元素型別相符 | `profile/<欄>.md` |
| `layer_role` `forked_from` `forked_at` `created_at` `email` `plurk_account` `model` `actual_agent` | **純量欄**：一律字面收（長得像 JSON 也不猜），只去掉結尾換行 | `profile/<欄>.md` |

- `value` 必須**在場**：沒給多半是參數名打錯（BUG-14）；要清空欄位就顯式給空值。
- 結構欄 parse 失敗／形狀不符 ⇒ 擋下，⛔ **不退存成字串**（那會變成一個長得像陣列的字串，讀回來才炸）。空陣列給 `[]`。
- `agent` 不由這裡寫（＝帳號 id，走 `set_bank`）；`wake_count`／`status` 等推導欄一律擋（去改那個既成事實）。
- `unset` 刪掉 `profile/<欄>.md`；本來就沒有 ⇒ 零寫入、不審計。

## 區域銀行綁定（`letters/<p>/bank/<區域ID>.md`，一區一檔）

```bash
senate cmd persona-profile --arg op=get_bank --arg persona=<p>                     # 純讀
senate cmd persona-profile --arg op=set_bank --arg persona=<p> --arg account=<帳號 id> --arg actor= --arg reason=
senate cmd persona-profile --arg op=unbind   --arg persona=<p> --arg actor= --arg reason=
```

- `currency` 沒給 ＝ 本專案 `Bank/bank_settings.json` 的區域。
- `get_bank` 的 `source ≠ currency` ＝ **借用別區**的綁定，不是本區宣告（回傳會明講）。
- `set_bank` 寫完讀回，帳號與來源都對才算成功；persona 不存在 ⇒ 擋（⛔ 不替打錯的名字開資料夾）。
- `unbind` 之後可能改成借用別區 —— 那是對的，回傳會印現在解析到哪。

### 全 pool（**預設 dry_run**，`dry_run=0` 才寫）

```bash
senate cmd persona-profile --arg op=migrate_bank  --arg actor= --arg reason= [--arg currency=<區>] [--arg overwrite=1]
senate cmd persona-profile --arg op=rebind_region --arg from=<舊區> --arg to=<新區> --arg actor= --arg reason=
```

- `migrate_bank`：把每個人目前解析得到的帳號（本區沒有就是借別區的那一個）寫成本區自己的綁定；本區已有 ⇒ 跳過。
- `rebind_region`：把全 pool 從舊區**複製**到新區 —— ⛔ 不刪舊區、不改設定；新區已有**不同值** ⇒ 衝突、exit 1，不覆寫也不挑。
- 兩支都是：任一區這一瞬間讀不了 ⇒ 計失敗、不寫（⛔ 不當成「沒有綁定」—— TASK-0265）。

## 審計

每一筆寫入 append 一行到 `AgentCommands/AwakenInit/_persona_write_audit.jsonl`：
`{"ts","persona","fields","actor","reason"}`，`fields` 是 `profile/<欄>`、`profile/<欄> (unset)`、`bank/<區>`、`bank/<區> (deleted)`。
`actor`／`reason` 必填 —— 寫入要能回答「是誰、憑什麼」。審計寫不進去不擋主寫入（資料已落地），但回傳會印 ⚠。

## 沒有移植的 op

- `refresh`（重寫衍生快照 `_persona_profile_snapshot.json`）：Senate 不靠它；Editor 在 domain reload 時自己重寫。
- `rename_agent`：Editor 版寫入時必定失敗（最後一步寫 `agent` 欄而那條路一律擋）；帳號合一之後改名走銀行後台。要用的那天另開單。
