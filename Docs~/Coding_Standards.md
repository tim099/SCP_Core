---
title: SCP 專案撰寫規範
description: SCP_Core 與其消費端（Senate / Unity）共用的 C# 撰寫規則 —— 方言限制、JSON 一律走 SCP_Json、設定一律走專案層 prefs、純函式邊界、路徑單一落點。
last_updated: 2026-09-01
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
related:
  - ../README.md | SCP_Core README | 兩條規矩的來源（方言 / 邊界）
  - <Senate>/Docs/Architecture/Overview.md | Senate 架構總覽 | 四層分工與共用碼邊界
  - ucl_core:Docs~/zh-Hant/Agent/Code_Comment_Standards.md | 註解規範 | 區塊職責／物理意義／數值影響（本專案沿用）
---

<!-- ⚠ 路徑寫法：本 repo 的檔案用相對路徑；跨 repo 的用 <Senate> / <UCL_Core> 角括號佔位。
     **目前沒有 `scp_core:` prefix resolver**（只有 `ucl_core:` / `repo:` 兩個，
     註冊在 UCL_CoreDocsBootstrap.cs）—— 需要 URL token 形式時要先補 resolver，不要自己造第三種寫法。 -->

# 🧱 SCP 專案撰寫規範

> 一句話：**這裡的每一條規則，都是「在 Unity 那側也要成立」的後果。**
> SCP_Core 同時掛在 .NET 10（Senate）與 Unity 專案底下，所以任何「只有一邊成立」的寫法，
> 症狀都不是編譯錯誤，是**另一邊安靜地少一塊**。

適用範圍：`SCP_Core/**`，以及**將來會搬進 SCP_Core 的**消費端程式碼（判準見 §2.1）。

> [!NOTE]
> UCL_Core 的 `Coding_Standards.md` / `Json_Coding_Standards.md` **不完全適用於本專案** ——
> 那兩份假設有 Unity（`UCL_Asset<T>`、`JsonData`、`EditorPrefs`、`Debug.Log`），
> 而本專案的第一條規矩就是不能假設有 Unity。跨語言的通則（路徑不該被推導、錢走 Cmd、
> `--persona` 顯式）仍然成立，那些住 skill `ucl-coding`。

---

## §1 方言：C# 9 / netstandard2.1 / 零第三方套件

| 規則 | 護欄 | 護欄在哪 |
|---|---|---|
| 語法只能用 C# 9 | `<LangVersion>9.0</LangVersion>` | `SCP_Core.csproj` |
| 不得引用 `UnityEngine` / `UnityEditor` | `"noEngineReferences": true` | `Runtime/SCP_Core.asmdef` |
| 零 `PackageReference` | ❌ **沒有自動護欄** | 只能靠這條規則 |

兩道自動護欄方向相反：一道擋「太新的語法」（在 Senate 這側就編不過），一道擋「Unity 專屬 API」
（在 Unity 那側才擋）。⇒ **加套件前先問一句「Unity 那邊哪來這個？」** 這格沒有護欄會咬人。

> 🩸 **實測（2026-08-22）**：塞一個檔案級 namespace 進來 →
> `error CS8773: Feature 'file-scoped namespace' is not available in C# 9.0`。護欄有咬。

⛔ 因此不可用：檔案級 namespace、`record`、raw string literal、`init`-only 以外的 C# 10+ 語法、
`System.Text.Json`、任何 NuGet 套件。

### 1.1 ⚠ 語法只是一半 —— **BCL 也有射程**

`LangVersion` 只擋語法。**netstandard2.1 缺的 API 它一個都擋不住**，
而消費端（Senate）是 net10.0 ⇒ 從那邊「搬一段能跑的程式碼進來」時，最常撞的就是這一格。

| 想用 | 從哪版才有 | netstandard2.1 的寫法 |
|---|---|---|
| `Environment.ProcessId` | .NET 5 | `Guid`／`Process.GetCurrentProcess().Id` |
| `File.Move(src, dst, overwrite)` | .NET Core 3.0 | 目標存在走 `File.Replace(tmp, dst, null)`；不存在才 `File.Move(tmp, dst)` |

> 🩸 **實測（2026-08-30，把 `SenatePageStore` 的讀寫本體搬進 SCP_Core 時）**：
> 兩個都中。`File.Move` 那格是 `error CS1501: No overload for method 'Move' takes 3 arguments`。
> ⇒ **搬家不是複製貼上**：同一段程式碼在 net10.0 編得過，不代表在共用碼裡編得過。
>
> ⛔ 而修法本身有一個更貴的錯誤選項：把 atomic replace 退化成「先 `Delete` 再 `Move`」。
> 那中間有一格「檔案不存在」，而那一格**長得跟「這個設定還沒存過」一模一樣** ——
> 崩在那一瞬間的話，使用者的設定不是壞掉，是**看起來從來沒設定過**。
>
> 📌 一般形：**方言限制的護欄只咬得到語法；API 的射程要自己去撞，而撞的時機是「搬家那一刻」。**
> ⇒ 從消費端搬碼進來時，`dotnet build` 跑的是 **SCP_Core.csproj**（netstandard2.1）那一份才算數。

---

## §2 ⛔ JSON 一律走 `SCP_Json`（Tim 2026-08-30 拍板）

**本專案所有 JSON 讀寫走 `SCP_Core/Runtime/Json`，不得用 `System.Text.Json`。**

理由不是偏好，是**它在 Unity 那側不存在** —— Unity 不吃 NuGet，而 netstandard2.1 profile
沒有內建 `System.Text.Json`。這正是本 repo 自帶 JSON 層的理由（見 README）。

### 2.1 判準：這段碼將來會不會進 SCP_Core？

| 情況 | 用什麼 |
|---|---|
| 已在 `SCP_Core/**` | `SCP_Json`（沒得選，編不過） |
| 在消費端，但**已規劃搬進** SCP_Core | **現在就用 `SCP_Json`** —— 搬家時才換等於把移植成本延後並放大 |
| 純宿主專屬、確定不會搬（例：Senate 的 CLI 參數解析） | 可用宿主自己的 |

⚠ 判準是**規劃**不是現況。「先用 `System.Text.Json`，搬的時候再改」的實際結果是：
搬家那天要同時處理「換 JSON 層」與「拆宿主依賴」兩件事，而它們的失敗互相遮蔽。

### 2.2 三條寫法規則

**① 已知 schema 一律 typed model**，走 `SCP_JsonMapper.ToJson` / `Populate` / `Create`。
手打字串 key 讀值的代價是：**鍵名打錯只會讀回預設值，而那長得跟「這件事沒發生」一模一樣。**

**② 讀值要顯式分「必須有」與「可以沒有」。**

```csharp
// 必須有 —— 讀不到就丟例外（訊息帶路徑）。這是預設。
string aName = aData["persona"].AsString();

// 可以沒有 —— 顯式寫出 fallback，或走 TryGet 拿到三態
string aNote = aData.GetString("note", "");
if (!aData.TryGetString("session_key", out string aKey)) { /* 沒設定，不是空字串 */ }
```

`SCP_JsonData` 的 `Missing` **是一種型別，不是空值** —— 從它讀值會丟例外並附路徑。
這是刻意的：「查不到卻回 0／空字串」是不會叫的錯，只會讓人拿假數字往下走。
⇒ **不要為了「安全」把每一處都改成帶 fallback 的版本**，那等於把護欄拆掉。

**③ 未知欄位要接住、原樣寫回。**

> 🩸 **血證（2026-08-23，`SenateConfig`）**：介面尺寸寫回設定檔的第一版，把使用者手寫的
> `"//"` 註解整行吃掉了。那不是格式化差異，是**寫入端省略不可逆** —— projects 還在，
> 所以看起來一切正常。**反序列化丟掉的東西，序列化就再也寫不回來**，除非顯式接住。

輸出端還有兩條既有設計要守（不要在新程式碼裡繞過去）：**key 保留插入順序**、
**非 ASCII 不轉義** —— 兩者都是為了輸出能被 `git diff` 讀。順序漂掉或中文變成 `\uXXXX`
的檔案人不會去看，而**人不看的 diff 等於沒有 diff**。

### 2.3 待移植清單

⭐ 已有正面樣本：`Senate.Core/SenatePageStore.cs` 的檔頭寫著
「**不用 `System.Text.Json` 是刻意的：頁面設定型別已經在 `SCP_TypeSchema` 的方言內**」——
本節不是新規矩，是把那個已經做對一次的判斷寫成規則。

**讀數（`grep -rn "System.Text.Json" src --include=*.cs`，2026-08-30，Senate repo，4 個檔）：**

| 檔案 | 判定 | 備註 |
|---|---|---|
| `Senate.Core/PersonaLetters.cs`（324 行） | **要移植** | 規劃搬進 SCP_Core（`LoginStatusPage` 遷移的前置） |
| `Senate.Core/AgentCmdClient.cs` | **待判** | 酒館遷移到 Senate 之後這支的歸屬會變，先不動 |
| `Senate.Core/SenateConfig.cs` | 暫不搬 | 宿主專屬設定。但它的「未知欄位原樣寫回」語意要在 prefs 層重現（§3.5） |
| `Senate.Cli/Program.cs:641-643` | 暫不搬 | CLI 內聯讀 queue 檔，宿主專屬 |

⚠ 這張表是**當時的讀數不是承諾**。動工前重跑一次那行 `grep`，不要拿這張表當現況。

---

## §3 ⛔ 設定一律走「專案層設定」，不得直接讀宿主的設定檔（Tim 2026-08-30 拍板）

### 3.1 現況病灶

`LoginStatusPage` 直接呼叫 `PersonaLetters.LoadSettings(repoRoot)` → 讀 `senate.local.json`
的 `awakening.lettersRoot`。⇒ **一個功能頁綁死了某一個宿主的設定檔名與形狀**，
於是它搬不進 SCP_Core（Unity 那側沒有 `senate.local.json`）。

### 3.2 規則

**功能程式碼只能透過 prefs 介面拿設定值，不得知道設定檔的檔名、路徑與結構。**
「這個值存在哪個檔、哪一層 key」是宿主的事，不是功能的事。

### 3.3 「參考 PlayerPrefs」指的是 scope，不是 API 語意

借的是 **per-project scope 的 key-value 存放**這個概念。
⛔ **不借它的三個病** —— 而這三個正好是本專案最在意的形狀：

| PlayerPrefs 的病 | 症狀 | 本專案要求 |
|---|---|---|
| key 打錯回預設值 | 「打錯 key」與「這個設定沒設過」**同形**，兩者都不報錯 | 必須有 `TryGet` 三態；**已知 key 應該是宣告出來的常數／typed key，不是散在各處的字串** |
| 型別靠呼叫端記得 | 同一個 key 這裡讀 string、那裡讀 int，兩邊都不報錯 | key 帶型別；型別不符要丟例外並附 key 名 |
| 沒有「未設定」態 | 「沒設定」與「設定成跟預設值一樣」壓成同一態 | **三態不得同形**（未設定／設定了／讀取失敗） |

> 🩸 三態同形是這套系統最貴的錯誤形狀，已有兩筆血證：
> LY 2026-08-21 查無帳戶被 `GetBalance` 回成 `0`（「不存在」長得跟「餘額零」一樣，每天印一次）；
> `LoginStatusPage` 自己的註解也寫著同一條 —— `_session` 找不到時把所有人畫成離線，
> 跟「真的全體離線」一模一樣。⇒ **未知就印未知，並把原因印在旁邊。**

### 3.4 機器路徑不入版控

`lettersRoot` 這類值是**機器絕對路徑**。存放要沿用既有的兩份形狀
（`SenateData/config/senate.local.example.json` 入版控 ／ `SenateData/config/senate.local.json` 不入版控）：

- 入版控的那份：跨機器成立的預設值，**不得含絕對路徑**
- 不入版控的那份：本機實際值

> 🩸 為什麼一定要分開（`SenateConfig` 檔頭原文）：機器路徑一旦進了版控，下一台機器 clone
> 下來會拿到「**看起來設定好了、但指向不存在的磁碟**」的狀態 —— 那跟「還沒設定」不同形，
> 卻同樣安靜。

### 3.5 兩個宿主可能同時寫

SCP_Core 同時活在 Unity Editor 與 Senate 裡，同一個專案的 prefs **可能被兩邊同時寫**。
⇒ 寫入必須：**temp file + atomic replace**、**未知鍵原樣保留**（§2.2 ③）、
**寫完回讀驗證**。單純 `WriteAllText` 覆蓋會讓後寫的那邊靜默吃掉先寫的那邊。

### 3.6 這一層在哪（2026-08-30 落地）

`Runtime/Prefs/SCP_Prefs.cs`。消費端只認 `ISCP_Prefs`；檔案背書的實作是 `SCP_JsonPrefs`，
**路徑由宿主傳進來**（Senate 那側唯一的決定點是 `SenatePageStore.DefaultPath`）。

```csharp
// key 宣告成常數並共用 —— ⛔ 不要在呼叫點打字串
static readonly SCP_PrefKey<string> LettersRoot = SCP_PrefKey.String("awakening", "lettersRoot", "");

var aRead = iPrefs.Read(LettersRoot);              // 三態：Missing / Present / ReadError
if (aRead.State == SCP_PrefState.ReadError) ShowError(aRead.Error!);
else if (!aRead.IsPresent) ShowNotConfigured();    // ⚠「沒設定」不是空字串
else Use(aRead.Value);

string aOrDefault = iPrefs.Get(LettersRoot);       // 顯式接受預設值時才用這支
var (aOk, aMsg) = iPrefs.Write(LettersRoot, aNewPath);   // 失敗一定有話說
```

驗收讀數（`senate selftest`，2026-08-30 實跑）：

```
prefs 三態          未設定=Missing:True／顯式 Get 用預設:True／寫後=Present:True
                    ／型別不符=ReadError 且訊息帶 key:True                              ✓
prefs 只動自己那格  根層註解保留=True／別的 section 保留=True／未知欄位保留=True
                    ／新值回讀=True                                                     ✓
```

⚠ 已落地的**只有機制**。§3.4（機器路徑分兩份存放）目前還沒有消費端在用它 ——
`LoginStatusPage` 仍直接讀 `senate.local.json`，那格要等頁面搬家（六步的第 4 步）才收。
⇒ 新程式碼**不要**再多接一條「直接讀宿主設定檔」的路徑。

---

## §4 ⛔ 路徑：**同一個路徑不准在兩個地方各解析一次**（Tim 2026-08-30 拍板）

規則的重點不是「路徑一定要在 Core 算」，是**一個路徑只能有一個決定點**。
宿主專屬的檔（Senate 的 `SenateData/prefs/senate.pages.local.json`、`SenateData/runtime/ui_session.json`）留在宿主沒問題 ——
只要它在那邊也只有一個決定點。**跨端契約的版面才必須進 SCP_Core**（兩邊都要走的東西，
各拼一次就是兩把尺）。

### 三個解析器，各吃各的根

| class | 根 | 管什麼 |
|---|---|---|
| `SCP_ProjectPaths` | `SCP_ProjectRoot` | `.agentcommands_root.local`（**跨語言契約**）、資料根解析 |
| `SCP_DataPaths` | `SCP_DataRoot` | `queues/<persona>/` `queue.json` `pending.trigger` `_session/` `ChatTavern/` |
| `SCP_LettersPaths` | `SCP_LettersRoot` | persona 版面（`profile/` `wakes/` `cmd/` `_constitution.md` …）、`_persona_` lock 前綴 |

### ⭐ 根是 **typed struct**，不是裸 string

```csharp
// ✅ 傳錯根 ⇒ 編譯錯
SCP_DataPaths.QueueFolder(new SCP_DataRoot(aDataRoot), aPersona);

// ❌ 三種根都是 string 的話，這一行編得過，而 <lettersRoot>/queues/basecamp
//    不會有任何一層喊 —— 它只是指到一個不存在（或更糟：存在但是別人的）的地方
```

判準是修法優先序：讓「傳錯根」**不可能發生**（第一階）＞ 讓它當場喊（第二階）＞ 記得傳對（第三階）。

### ⛔ 解析器不准自己「找根」

根一律由**宿主**傳進來。SCP_Core 不 walk `.git`、不讀 cwd、不從 assembly location 反推、
不自排 fallback。理由是 SCP_Core 掛在未知位置，而 Senate 同時管**一批**專案 ——
「the 專案根」這個東西在這裡根本不存在。

> 🩸 **最壞的失敗不是找不到檔，是找到了另一個宇宙的檔** ——
> 前者會喊，後者回一個看起來正常的數字。
> UCL 那側 2026-08-17 一天三撞，最貴的一筆是 `dataPath/../..` 跳出去剛好命中一棵舊資料樹
> ⇒ 餘額回報 453、真實帳本 1330，**差 877 而連錯誤訊息都沒有**。
> 而 2026-08-30 我自己也撞了同族的一次：SCP_Core 有**兩個工作樹**（Bar／Senate 各一份），
> 我把新檔寫進其中一個、`dotnet build` 讀另一個。

### 例外要有 Origin，不要只回一個字串

`SCP_ProjectPaths.ResolveDataRoot` 回 `(Root, Origin)`，Origin 是
`Configured` / `Pointer` / `Convention` 三態。**三種來源不得同形** ——
它們錯起來的修法完全不同，而只回字串的話，後台頁沒辦法回答「我為什麼看這裡」。

### 落地讀數（`senate selftest`，2026-08-30）

```
路徑單一落點  queue 舊新一致=True／status 掃的是父層=True／穿越擋回 anonymous=True
              ／根正規化=True／letters 舊新一致=True／資料根三來源可分=True        ✓
```

真檔驗證：`senate ucmd status --project Bar` 列出 10 個真實 queue 分道（資料根
`D:/Unity/Bar/AgentCommands`）—— 不是只跑合成字串。

---

## §4.5 ⛔ 驗收：改完 code **先 build，再對 exe 實跑**（Tim 2026-08-30 拍板）

這是 UCL 那側「改完 `.cs` 一律送 `Cmd_Recompile`」的對應條款 —— 同一個病，不同的宿主。

| 你跑的 | 實際是什麼 |
|---|---|
| `dotnet run --project src/Senate.Cli` | **Debug**、framework-dependent 的 DLL |
| 根層 `senate.exe`（PATH 上的 `senate` 也是它） | **Release**、self-contained、single-file |

**兩個不同的二進位檔。** ⇒ 「Debug 全綠」與「你交付的那顆 exe 全綠」是**兩本帳**，
而它們在畫面上長得一模一樣（憲法⑥的又一個實例：處置成功 ⊭ 結果安全）。

```bash
./build.sh                 # publish → 出廠驗收（doctor + selftest + 開窗）
./senate.exe <要驗的事>     # 收工前的那一次，必須跑在這上面
```

`dotnet run` 是**迭代**用的（秒級），不是驗收用的。
⚠ 只驗過 Debug 是**完全合法的交付狀態** —— 把它講成「驗過了」才不是。

> 🩸 2026-08-30：agent 整個下午的驗證迴圈都是 `dotnet run`，而人每次要測都得自己
> 先跑一次 `build.sh`。兩條路從來沒接起來，是他問「目前是如何驗證的」才現形。
> 同日順手量到：published single-file 底下反射照常運作（`頁面發現` 在 exe 上也是 ✓）——
> **那是讀數不是保證**，所以更要每次都跑。

📌 修法不是只寫這條規則（第三階）：同日把 `selftest` 綁進 `build.sh` / `build.ps1`
的出廠驗收（第二階，長在必經路上）。**規則只負責解釋為什麼，不負責被記得。**

細節與三格驗收 → `<Senate>/Docs/Workflows/Setup_And_Build.md`。

---

## §5 邊界：純函式優先，服務要有理由

README 原本的判準是「只放純函式 ＋ 零依賴；檔案 IO、跑 git、log、UI 留在各自那邊」，
而**現況已經越過那條線**（`SCP_Git` 跑 git、`SCP_ProcessRegistry` 管 process、`SCP_Gui` 是 UI 中間層）。

⇒ 現行判準改寫成兩句：

1. **能寫成純函式的就寫成純函式** —— 解析、正規化、分類決策、狀態機、hash、diff 判定。
   這類東西共用的成本是零，而且測得起來。
2. **會碰 IO／外部程序的東西進來，必須是「只能有一個落點」的那種** ——
   例：git 的護欄（`core.quotepath` / `GIT_TERMINAL_PROMPT` / 逾時 kill / process 登記）
   如果兩邊各寫一份，**漏掉其中一格的症狀全是靜默的**。這種才值得付「兩邊生命週期綁在一起」的成本。

⛔ 反例：只有一個消費端在用、又碰 IO 的東西 —— 那是宿主的東西，放進來只是把它變成兩邊的負債。

---

## §6 註解規範

沿用 UCL 的三段式（**區塊職責 / 物理意義 / 數值影響**），細節見
`ucl_core:Docs~/zh-Hant/Agent/Code_Comment_Standards.md`。

本專案額外一條，現有檔案已在做，寫下來免得漂掉：
**⚠ 方言限制與血證要寫在檔頭。** 因為「為什麼不用那個更好寫的語法」這件事，
下一個人只會在檔案裡找答案，不會先去讀 README。

---

## §7 血證登記處

撞到坑之後寫回這裡的判準（三選一即可）：**會再犯 ／ 失敗是靜默的 ／ 修法本身會長出同族的下一隻**。
一次性手誤不用寫。

寫法三條：**寫判準不要寫願望**（要寫成「符合什麼形狀就停下來」）、**附血證**（日期 ＋ 當時的讀數）、
**修法優先序**：讓那格失敗不可能發生 ＞ 讓它當場喊 ＞ 才輪到「記得注意」。
