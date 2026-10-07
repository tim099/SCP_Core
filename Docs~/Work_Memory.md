---
title: 工作記憶 —— 以工作主題為單位的 knowhow 庫
description: senate cmd work-memory 的使用說明：什麼東西進工作記憶（相對於單子、文件、收尾信）、主題與 fragment 的形狀、十個 op 的用法、讀取的共讀 briefing、Task 雙向錨點、歸檔與刪除的 git 守衛。
cmds: [work-memory]
target_audience: [AI_Agent]
---

# 🧠 工作記憶

> 一句話：**記憶回答「為什麼」與「怎麼踩過」，單子回答「到哪了」，文件回答「怎麼用」。**
> 記憶是工作期間的鷹架，不是永久資產 —— 相關單全關之後歸檔或刪除，內容留在 git。
> 參數表看 `senate cmd help work-memory`。

## 1. 什麼進工作記憶

照順序問：

1. 是「到哪了」（進度、順序、誰在做、什麼算完成）？⇒ **單子**。
2. 是「怎麼用」（規格、欄位、步驟）？⇒ **文件**。
3. 是「要小心什麼／為什麼是這樣／踩過什麼坑」，而且**換人接手需要知道**？⇒ **工作記憶**。
4. 是當天的心得、感受？⇒ **收尾信**。只有自己要記的提醒 ⇒ 見叢。

- ⛔ 不記進度與看板快照（進度住單子的時間線）；綁了單的主題不寫 `state`。
- ⛔ 不把文件整段貼進來 —— 寫 `docs` 指向權威文件。
- 個人身分、關係、對所有人都成立的通用經驗不放這裡（那是個人記憶與 Alaya，見 `Memory`）。
- 不是每張單都要有主題：通常只有跨日的單需要。

## 2. 形狀

`<資料根>/WorkMemory/<topic>/`：

| 檔 | 是什麼 |
|---|---|
| `_topic.md` | 主題卡：`id`／`title`／`status`（active／archived）／`key_docs`／`related_topics`／`task_indices` |
| `<type>_<slug>.md` | fragment：`decision`／`knowhow`／`pitfall`／`state`／`pointer`。**正文寫一次不改寫** |
| `_index.md` | 機械生成的索引，手改會被覆寫 |

fragment 的價值在它的 ref：`docs`（沒前綴＝相對資料根；`senate:`／`scp_core:` 前綴＝相對該 repo；`ucl_core:` 是 Unity 專案的檔、Senate 不讀 —— TASK-0390）指向知識點，`links`（`<topic>/<id>`）指向別的 fragment（可跨主題）。

## 3. 常用

```bash
W="senate cmd work-memory"
$W --arg op=topics                                                     # 所有主題（active／archived 分組）
$W --arg op=read --arg topic=<t> --arg with_links=1                    # 開工前讀；📄 回傳檔是共讀 briefing
$W --arg op=read --arg topic=<t> --arg types=decision,pitfall,pointer  # 最快的接手讀法
$W --arg op=init --arg topic=<t> --arg title=<標題> --arg-file desc=<檔>
$W --arg op=add --arg topic=<t> --arg type=pitfall --arg id=<slug> --arg title=<標題> \
   --arg-file body=<檔> [--arg docs=<路徑,…>] [--arg links=<topic/id,…>] --arg by=<你>
$W --arg op=supersede --arg topic=<t> --arg id=<舊 id> \
   --arg new_id=<slug> --arg new_title=<標題> --arg-file new_body=<檔>  # 一步式取代：新的接舊的、舊的標 superseded
$W --arg op=link --arg from=<topic/id> --arg to=<topic/id>             # 雙向
```

- **先讀再寫**：`add` 會掃全部主題的標題，近似的會警示（不擋）；語意級的查重用 `senate cmd kb --arg target=work_memory`。
- 長文一律 `--arg-file`。
- `read` 會把記憶摘要與 `docs` 指到的本地檔（各前 100 行）寫成一份 briefing —— **讀那份**，不讀終端截斷的內容。
- 收工時順手留「為什麼卡住」：`senate cmd task --arg op=wrapup … --arg-file why=<檔>` 會寫進單子 `memory_topic` 那個主題（單子要先有 `memory_topic`）。

## 4. 跟單子的雙向錨點

- 單子側：`memory_topic`、`memory_archived_commit` —— 由 `senate cmd task --arg op=update` 寫。
- 記憶側：主題卡的 `task_indices` —— 由本指令寫：`--arg op=tasks --arg topic=<t>`（不帶動作＝印現況）、`--arg add=17 --arg remove=3`、`--arg set=5,8`（顯式給空值＝清空）。
- 關聯的真相源是**單子的 `memory_topic`**：`read`／`tasks` 都會掃全部單檔；只在記憶側宣告、單子沒指回來的會標「單向」。

## 5. 歸檔與刪除

```bash
$W --arg op=archive --arg topic=<t> [--arg commit=<sha>]     # status → archived；--arg undo=1 改回
$W --arg op=delete --arg topic=<t> --arg by=<你>             # dry-run；加 --arg confirm=1 才刪，並留墓碑
```

- **git 守衛**：主題目錄的每個檔都要被追蹤、而且沒有待處理的變更，否則 exit 3 並印出實際讀數（被 ignore 與 untracked 分開標）。git 問不出來也算不乾淨。
- `archived_commit` 取**擁有這份內容的那個工作區**的 HEAD（WorkMemory 可能是巢狀 submodule，不是父 repo 的）。
- 有未關的關聯單只警示不擋；歸檔後 `read` 照樣印全文，頭上多一條「已歸檔」。
- 做完記得對每張關聯單補 `memory_archived_commit`（指令會印出那一行）。
