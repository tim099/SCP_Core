# SCP_Core

**Unity 與 Senate 共用的核心碼。** 以 git submodule 掛在兩邊：

| 消費端 | 掛在哪 | 怎麼編 |
|---|---|---|
| Senate（.NET 10） | `<repo>/SCP_Core` | `SCP_Core.csproj`（netstandard2.1） |
| Unity 專案 | `Assets/Plugins/SCP_Core`（預定） | `Runtime/SCP_Core.asmdef` |

## 兩條規矩（都有護欄，不是靠記得）

### ① 方言：C# 9 / netstandard2.1 / 零第三方套件

共用碼必須是 **Unity 編得過的那個子集**，所以：

- `SCP_Core.csproj` 釘 `<LangVersion>9.0</LangVersion>` ⇒ 寫了檔案級 namespace、`record`、
  raw string literal，**在 Senate 這側就編不過**，不會等到搬進 Unity 才發現。
  🩸 實測（2026-08-22）：塞一個檔案級 namespace 進來 → `error CS8773: Feature 'file-scoped namespace'
  is not available in C# 9.0`。護欄有咬。
- `Runtime/SCP_Core.asmdef` 帶 `"noEngineReferences": true` ⇒ **Unity 那側會擋下任何 `UnityEngine` 引用**。
  兩邊各有一道，方向相反：一道擋「太新的語法」，一道擋「Unity 專屬 API」。
- **零 `PackageReference`**：Unity 不吃 NuGet。這條沒有自動護欄，加套件前先問一句
  「Unity 那邊哪來這個？」`System.Text.Json` 就是因此不能用 —— 也正是本 repo 自帶 JSON 層的理由。

### ② 邊界：只放「純函式 ＋ 零依賴」

| 可以進來 | 留在各自那邊 |
|---|---|
| 資料結構、解析／序列化、分類決策、路徑正規化 | 檔案 IO、跑 git、log、UI、設定檔載入 |

⇒ 判準：**它開始長出「服務」就是越界了。** 共用一個純函式的成本是零；共用一個會碰 IO 的東西，
成本是兩邊的生命週期、執行緒模型與錯誤處理全部綁在一起。

## 目前內容

### `Runtime/Json` — JSON 值樹與解析

概念沿用 UCL_Core 的 `JsonData`（一顆節點打通讀寫、下標取值、隱式轉換），但**重寫、零 Unity 耦合**。
三個刻意的設計：

1. **`Missing` 是一種型別，不是空值。** `data["不存在"]` 回 Missing 節點，**從它讀值會丟例外並附路徑**。
   要寬鬆就顯式寫出來：`GetString(key, fallback)` / `TryGetString`。
   🩸 理由：「查不到卻回 0／空字串」是不會叫的錯 —— 只會讓人拿假數字往下走。
2. **key 保留插入順序、非 ASCII 不轉義。** 兩者都是為了輸出能被 `git diff` 讀 ——
   順序漂掉或中文變成 `\uXXXX` 的檔案，人不會去看，而人不看的 diff 等於沒有 diff。
3. **數字保留原文**，讀取時才依 `InvariantCulture` 轉 —— 不讓 `long` 繞一趟 `double` 掉尾數。

驗收讀數（`senate selftest`，2026-08-22 實跑）：

```
Missing 語意  讀不存在的 key 會丟例外（訊息帶路徑）／fallback=True／Exists 判定=True          ✓
輸出穩定性    round-trip 逐字相同=True／插入順序保留=True／中文不轉義=True                    ✓
讀真檔（LY）  commands_schema.json：10632 字元／commands=48／寫回再讀等價=True                ✓
```

第三項是關鍵：那個檔是**Unity 端的 UCL JsonData 寫出來的**。共用層的第一個責任不是「能解析 JSON」，
是「讀得懂既有資料」—— 所以驗收方式是拿真檔案跑，不是自己造樣本。
