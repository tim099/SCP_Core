---
title: 新增一種 activity session kind
description: 「一人一檔位」的 session 層要新增一種 kind 時，要動哪幾格、哪幾格會自動生效、以及三個不會報錯的漏做。
last_updated: 2026-09-05
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

---

## 2. 開場：**一律走 `TryStart`**，不要自己 `Load` + `Save`

```csharp
if (!UCL_SessionStartGuard.TryStart(aPersona, aSession, Kind, out string aReason, out string aExit))
{
    // 寫 blocked 回傳檔（reason / exit 直接用），非零退出
}
```

- `SCP_ActivitySessionStore.TryStart` ＝ 先查再寫，內部的 `FindRunning` **不 filter kind**。
- **被擋時一個位元組都不寫** ⇒ 守衛之後的發券／擲骰／公告一格都不會發生。
- ⚠ **「同 kind 疊開」不歸它管** —— 那是各 kind 自己的守衛。兩條是**正交的軸**，
  混在一起會讓其中一條的失效被另一條的通過掩蓋。
- ⚠ 每一條**建立 session 的路徑**都要走它，不是只有那個叫 `start` 的。
  🩸 觀影有兩條：`step=start` 與 **`step=join`** —— 後者最容易漏，因為那支上面已經擋過
  「你自己那場觀影」，而那道守衛看不見別的 kind。
  （查法：`grep -rn 'SaveSession(' <你的 Cmd>` 找出所有**新建**那份 session 的地方。）

---

## 3. 宿主層：登記行為（Editor 側，`UCL_SessionKindHost`）

在**你自己這個 kind 的檔**裡加一次，⛔ 不要去改 `Cmd_SessionClose`：

```csharp
[UnityEditor.InitializeOnLoadMethod]
static void RegisterSessionKind()
    => UCL_SessionKindHost.Register(new UCL_SessionKindEntry
    {
        Kind = SCP_ActivitySessionKind.Coding,
        CmdName = "Coding",              // 擋下別人時要附的指令原文
        HasStepEnd = true,               // 觀影是 false —— 它沒有 step=end
        SettleResidueAsync = null,       // null ＝ **這個 kind 真的不用結算**（顯式答案）
    });
```

| 格 | 漏了會怎樣（都**不報錯**） |
|---|---|
| 沒登記 | 補收工照樣關場，然後印「⚠ 這個 kind 沒有人登記過」＋已登記清單 —— **看得見，但要有人去看** |
| `CmdName` 空 | 擋下時印的是 kind 本身而不是指令 —— 讀的人會去跑一個不存在的東西 |
| `SettleResidueAsync` 該有卻是 `null` | 補收工**只翻三欄** ⇒ **酬勞蒸發**，而回傳檔說「登記為不需要結算」 |

📌 為什麼「沒登記」與「登記為不用結算」要**不同形**：
它們的處置相反（前者去補登記，後者什麼都不用做），而在 2026-09-05 之前
它們印的是**同一句話**（TASK-0055）。

⚠ **結算留在 Editor 不是偷懶**：結算就是金流，而金流不搬是 TASK-0106 拍過的（Tim 拍 B）。
⇒ 名字在共用層、行為在宿主，兩邊各一份真相源，沒有第二份會漂的清單。

---

## 4. 收工：兩條路，**不要讓它們互相呼叫**

| 路 | 誰在走 | 做什麼 |
|---|---|---|
| **正常收工**（`step=end` 或到期） | 該 kind 自己 | 自己結算 → `Store.Close`（翻三欄）→ 收工公告 |
| **補收工／殘留** | `Cmd_SessionClose`（Editor 的唯一門）；Senate 側走 `CloseWithSettlement` → gateway 委派回它 | ① 權威狀態＋回讀確認 ② 查登記表補結算 ③ **不廣播** |

⛔ **正常收工那條不要改走 `CloseWithSettlement`。**
🩸 它已經先結算再 `Close` ⇒ 再走一次統一入口就是**第二次結算**（觀影場會重複發薪）。
📌 判準：**「所有路徑走同一個門」的射程是「原本沒有結算的那些路徑」，不是「全部路徑」**
（憲法④：通則要問適用範圍）。

---

## 5. 自動生效的（不必你做）

- `senate cmd sessions`（list / show / close）與 **Session 管理頁**：它們讀的是基底，
  只要 `Kinds` 有登記就看得到，**一行都不用改**（兩支都零 kind 硬編碼，2026-09-05 查證）。
- 跨 kind 互斥：只要開場走了 `TryStart`。
- 晚安自動關（TASK-0057）：走的是**同一個關場函式**（`UCL_SessionCloseFlow`）——
  ⚠ **但判準不同**：補收工要「殘留」（`active` 且**已過 `end_ts`**），晚安只看 **`active`**。
  🩸 這一行原本寫「走的是同一條補收工路」，而 @summit 2026-09-05 照它推出
  「E 對 Coding 走不到」——**推得沒錯，是我的句子把「同一個函式」寫成了「同一條路」。**

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
4. **補收工認得你**：造一份你這個 kind 的殘留 ⇒ `Cmd_SessionClose` 印的是
   「登記為不需要結算」或真的跑了結算，**不是**「沒有人登記過」。

⚠ 第 4 格是唯一能證明第 3 步真的生效的讀數 —— 而它跟前三格**不同源**：
前三格量的是開場，第 4 格量的是關場。
