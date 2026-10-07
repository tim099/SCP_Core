---
title: 新增一種 activity session kind
description: 「一人一檔位」的 session 層要新增一種 kind 時，要動哪幾格、哪幾格會自動生效、以及三個不會報錯的漏做。
last_updated: 2026-10-07
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
related:
  - Coding_Standards.md | SCP 專案撰寫規範 | 方言／JSON／prefs／路徑單一落點
  - <Senate>/Docs/Architecture/Data_Layout.md | 資料版面 | `sessions/<persona>.json` 住哪
---

# 🎬 新增一種 activity session kind

> 一句話：**kind 的「名字」住共用層，kind 的「行為」住宿主 —— 而漏做的那幾格都不會報錯。**

適用於「活動 session」：自由時間、觀影、（TASK-0058 的）Coding…
資料形狀是**一人一檔位**：`<DataRoot>/sessions/<persona>.json`，`kind` 是**檔案裡的欄位不是路徑段**。

---

## 0. 先知道這個形狀，否則下面每一步都會被誤解

扁平化（TASK-0054 拍板⑤）之後：

- 「同一個人同時兩種 session」在**資料形狀層**就不可能 —— 因為只有一個檔位。
- ⚠ 而形狀層的「不可能」在**寫入端**長成了「**後來的覆蓋先來的**」。
  🩸 2026-09-05 的活體：一場進行中的觀影擺在檔位上，跑 `FreeTime step=start`
  ⇒ 那場觀影**不見了**，而 FreeTime 回 Success、還替它發了開場宣告。
  成因是各 kind 自己 `Load(自己那個 kind)` 判「有沒有在跑」—— 它 filter kind，看不見別人。

⇒ 所以下面第 2 步（開場走 `TryStart`）不是規矩，是**那個洞的補丁**。

---

## 1. 共用層：登記名字（`SCP_Core`，兩個宿主都認）

`Runtime/Session/SCP_ActivitySessionKind.cs`

```csharp
public const string Coding = "Coding";
public static readonly string[] Kinds = { FreeTime, StreamWatch, Coding };
```

- ⚠ **`Kinds` 沒加＝所有掃描都看不到它**，而畫面上長得像「沒有人在那種場」。
  回報「沒查到」時一律連 `Kinds` 一起印 —— 否則「沒登記」會被讀成「不存在」。
- kind 專屬欄位走**子類別**（`SCP_ActivitySession` 可被繼承）＋ `Load<T>`。
  📌 `Raw` 一定要留：**讀成子類別 ≠ 認識全部的鍵** —— 管理頁與關場路徑讀的是基底，
  此時 kind 專屬欄位一個都不認識，而它們必須原樣寫回去
  （🩸 2026-09-04 就是那條路吃掉了 `rounds` / `activity`）。
- ⛔ **不要為子類別寫 `SerializeToJson` 之類的 override** —— `SCP_JsonMapper` 寫原生 bool、
  用 `[SCP_Ignore]` 排除欄位。⚠ 別套別的框架的直覺：UCL 那套只看 `[UCL_HideInJson]`，
  **`[NonSerialized]` 它不看** —— 🩸 2026-09-04 實測真的把整包 `RawJson` 寫進了檔。

---

## 2. 開場：**一律走 `TryStart`**，不要自己 `Load` + `Save`

```csharp
if (!SCP_ActivitySessionStore.TryStart(aRoot, aPersona, aSession, Kind, DateTime.Now, out var aBlocker, aScope))
{
    // 寫 blocked 回傳檔（點名 aBlocker 那一場＋可直接複製的出口），非零退出
}
```

- `SCP_ActivitySessionStore.TryStart` ＝ 先查再寫，內部的 `FindRunning` **不 filter kind**。
- **被擋時一個位元組都不寫** ⇒ 守衛之後的發券／擲骰／公告一格都不會發生。
- ⚠ **「同 kind 疊開」不歸它管** —— 那是各 kind 自己的守衛。兩條是**正交的軸**，
  混在一起會讓其中一條的失效被另一條的通過掩蓋。
### 📐 全域互斥的 kind 要多傳一個「施工範圍」（TASK-0201，2026-09-11）

`TryStart` 的最後一個參數是**選填**的 `iScope`（絕對路徑）。它只被
`SCP_ActivitySessionKind.IsGlobalExclusive(kind)` 為真的那些 kind 讀。

判準從「這個 kind 有人在跑就擋」收窄成「有人在跑**而且範圍撞到我**才擋」——
重疊＝**路徑包含**（`…/Assets/Scripts` 與 `…/Assets/Scripts/Conditions` 重疊）。
實作在 `SCP_SessionScope`（正規化＋重疊）與 `SCP_ActivitySessionStore.FindConflictingGlobal`。

- ⛔ **兩側任一沒宣告範圍 ⇒ 退化成舊行為（全擋）。** 那是安全側：
  反過來（缺欄位就放行）會讓舊 session 檔在升級的那一刻**靜默失去保護**，
  而症狀是兩個人同時進場、各自以為自己是唯一。
- ⚠ **範圍解不開 ≠ 沒宣告** —— 呼叫端要當場擋下並說原因，⛔ 不可以靜默退化：
  靜默退化會讓打錯路徑的人拿到一個他沒要的全域鎖，而輸出跟「我刻意不宣告」一模一樣。
- ⚠ 判準是**純路徑**（Tim 2026-09-11 拍板）：同一個 repo 的兩份工作副本
  （`Senate/SCP_Core` 與 `LY/Assets/Plugins/SCP_Core`）**不算衝突**。
  代價已知且被選擇：兩人各改一份副本的同一支檔時這道閘不叫，要到 push 分叉才現形。
- ⚠ `FindRunningGlobal`（有沒有人在跑）與 `FindConflictingGlobal`（有沒有人擋到我）
  **不可以互相取代** —— show 那種問題問前者，進場守衛問後者。
  列全部在場的人走 `ListRunningGlobal`：只印第一場的話，
  「只有一個人在場上」與「有三個人但我只看得到一個」在輸出上同形。

- ⚠ 每一條**建立 session 的路徑**都要走它，不是只有那個叫 `start` 的。
  🩸 觀影有兩條：`step=start` 與 **`step=join`** —— 後者最容易漏，因為那支上面已經擋過
  「你自己那場觀影」，而那道守衛看不見別的 kind。
  （查法：`grep -rn 'SaveSession(' <你的 Cmd>` 找出所有**新建**那份 session 的地方。）

---

## 3. 關場不結算

**沒有任何 kind 在關場時結算。** 關場只做一件事：翻三欄（`active`／`end_reason`／`ended_at`）再回讀磁碟。
全部在 Senate 就地做，⛔ 不委派 Unity Editor（觀影在 Senate 重做中，TASK-0450）。

- 新 kind **不必**登記任何關場行為；`Kinds` 有它就關得到。
- ⛔ 不要再往 Unity 側的 `UCL_SessionKindHost` 登記新 kind —— 那張表只剩 Unity 舊指令在用，待 TASK-0454 退場。
- 你的 kind 要付錢，就在**自己的正常收工**（`step=end`）裡付，⛔ 不要塞進關場門。

---

## 4. 收工：兩條路

| 路 | 誰在走 | 做什麼 |
|---|---|---|
| **正常收工**（`step=end` 或到期） | 該 kind 自己 | 該 kind 自己的收尾（例：收工公告）→ `Store.Close`（翻三欄） |
| **補收工／殘留** | `senate cmd sessions --arg op=close`、後台活動 session 頁 | `SCP_ActivitySessionStore.CloseVerified`：翻三欄＋回讀；**不廣播** |

- 補收工只收**殘留**（`active` 且已過 `end_ts`）；進行中的場會被擋下，並印出該 kind 的正常收工指令。
- 缺 `confirm=1` ⇒ 擋下、零寫入；已收工 ⇒ 冪等 no-op。

---

## 5. 自動生效的（不必你做）

- `senate cmd sessions`（list / show / close）與 **Session 管理頁**：它們讀的是基底，
  只要 `Kinds` 有登記就看得到，**一行都不用改**（兩支都零 kind 硬編碼，2026-09-05 查證）。
- 跨 kind 互斥：只要開場走了 `TryStart`。
- 晚安自動關（TASK-0057）：走的是**同一個關場函式**（`CloseVerified`）——
  ⚠ **但判準不同**：補收工要「殘留」（`active` 且**已過 `end_ts`**），晚安只看 **`active`**。

---

## 5.5 ⚠ 如果你的 kind **沒有預定時長**，先讀這一段（2026-09-05，`Coding` 是第一個）

`SCP_ActivitySession.IsRunningAt` 在 `end_ts` 解析不出來時**回 `true`**（只信 `active`）——
那是刻意的：寧可誤判「還在」也不要把一場真的在跑的 session 當成不存在。

⇒ 三件事相乘：**沒有 `end_ts` ＋ `IsRunningAt` 恆真 ＋ 補收工的射程是「殘留」**
⇒ **這種場永遠是「進行中」，永遠不會落進補收工那條路。**

🩸 而最貴的後果不是驗收：**全域獨佔 ＋ 無時限**的 kind，持有者掉線之後
那場永遠 `active`、**永遠擋住所有人**，而唯一出口是持有者自己回來收工。
⚠ 更難看的一格（2026-09-05 QA 實測）：補收工被擋時印的出口寫著
「**或等它到期之後再跑本 Cmd**」—— 而它**永遠不會到期**。那是一條讀起來合理、
而且永遠不成立的指路。

⇒ 判準：**開場一律給 `end_ts`**（施工場有上限是合理的物理約束），
需要更久就在場中續期（`step=status` 順手推）。
⛔ 不要為了「無時限」去改共用層的三態判準 —— 那會讓每一個 kind 都多認識一個概念。

## 6. 交付前的四格讀數（照抄）

1. **擋得住**：別 kind 進行中 ⇒ 你的開場 blocked，回傳檔有**原因＋可直接複製的出口**。
2. **沒擋錯**：無任何進行中的場 ⇒ 放行。
   ⚠ 只驗第 1 格的話，**一個永遠擋的閘也會通過**。
3. **被保護的資料還在**：回讀 `sessions/<persona>.json`，被擋下之後**逐欄原封不動**。
   ⛔ 判準是那個檔，**不是你的 Cmd 回什麼**（它會說成功）。
4. **補收工認得你**：造一份你這個 kind 的殘留 ⇒ `senate cmd sessions --arg op=close --arg confirm=1`
   回 `closed=1`，回讀 `sessions/<persona>.json` 的 `active=false`。測試用 Template persona。

5. **selftest 要有反向對照**：不只驗「子類別寫得出、讀得回」，
   要驗「**讀成基底寫回去之後，kind 專屬欄位還在**」。
   ⚠ 只驗前者的話，**一個基底寫回就吃鍵的實作也會全綠**。

⚠ 第 4 格是唯一能證明第 3 步真的生效的讀數 —— 而它跟前三格**不同源**：
前三格量的是開場，第 4 格量的是關場。

---

## 7. 這份文件之前住在哪（給查歷史的人）

2026-09-05 之前，這套 SOP **只活在工作記憶** `session-architecture/pointer_port-0127-after-onecut`。
⇒ 而**記憶會歸檔**：主 Task 收尾那天它會被封存，於是「怎麼用」會跟著鷹架一起消失。
📌 判準：**記憶回答「為什麼／怎麼踩過」，文件回答「怎麼用」** —— 這份就是那次搬家的結果。
