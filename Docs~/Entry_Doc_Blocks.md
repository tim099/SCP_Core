---
title: Agent 入口檔的受管區塊
description: CLAUDE.md / AGENTS.md 這類 agent 入口檔的「附加式安裝」規格 —— marker 格式、七種狀態、遷移、以及為什麼不用整檔覆寫。
last_updated: 2026-08-30
target_audience: [AI_Agent, Tools_Maintainer, Backend_Programmer]
related:
  - Coding_Standards.md | SCP 專案撰寫規範 | 方言／JSON／prefs／路徑
---

# 📎 Agent 入口檔的受管區塊

> 一句話：**入口檔是使用者的檔，不是我們的鏡像。**
> skill 的安裝目錄寫壞了重裝就好；入口檔的使用者區**在源端沒有副本**。

拍板：Tim 2026-08-30。程式在 `Runtime/Entry/SCP_EntryDoc.cs`（純函式）
與 `SCP_EntryDocInstaller.cs`（落地）。

---

## 1. 形狀

```markdown
AAAA                                   ← 使用者的內容，一個字都不動

<!-- SCP_CORE:BEGIN v=1 target=claude src=ClaudeTemplate/CLAUDE.md sha=1a2b3c4d5e6f -->
<!-- 本區塊自動產生，手改會在下次同步被覆蓋。專案規則請寫在本區塊「之外」。 -->

BBB                                    ← 受管內容（同步時只有這一段會被換掉）

<!-- SCP_CORE:END -->

ZZZZ                                   ← ⭐ END 之後的內容也**原樣保留**
```

檔案不存在時：使用者區為空，**檔頭就是 BEGIN**。

## 2. 四個為什麼

| 決定 | 為什麼 |
|---|---|
| **成對 BEGIN/END**，不是單一分隔線 | 單一分隔線的語意只能是「這行以後都是我的」⇒ 更新 ＝ 砍到檔尾重寫 ⇒ **使用者在檔尾補的東西被無聲吃掉**，而人天生就是往檔尾補東西 |
| **HTML 註解**當機器邊界 | CommonMark 裡**整行只有 `-` 或只有 `=` 的一行，會把上一行變成標題**（setext heading）。一個「簡化成 `---`」的分隔線會把使用者最後一行悄悄變成 H2，而檔案看起來完全正常。HTML 註解沒有這個語法面，渲染時隱形，還能帶 key=value |
| 前綴 `SCP_CORE` **選定後不可改** | 改了等於全世界既有安裝一次孤兒化 —— 那些檔會被判成「沒安裝」然後再 append 一份 |
| marker 上**不放時間戳、不放 commit** | 入口檔是入版控的檔。放了會讓每次同步都產生 git diff，而人開 diff 想看的是規則有沒有變 |

`sha` ＝ 受管內容的 SHA-256 前 12 hex（行尾正規化後計算）。它只跟著內容變。

## 3. 七種狀態 —— **不得互相摺疊**

| 狀態 | 意思 | 安裝時 |
|---|---|---|
| `NotInstalled` | 沒有區塊（檔不存在／檔有內容但沒 marker） | append 在現有內容之後 |
| `Synced` | 區塊在、與來源一致 | 不動檔案 |
| `Stale` | 來源更新了 | 覆寫那一段（安全） |
| `LocalEdit` | 區塊被**手改過**（實際 hash ≠ marker 記的 sha） | ⛔ 停手，要 `force` |
| `MarkerBroken` | 只有 BEGIN 沒有 END／END 在前 | ⛔ 停手，**force 也不做** |
| `Duplicated` | 找到 ≥2 個區塊 | ⛔ 停手，**force 也不做** |
| `NeedsMigration` | 沒 marker，但整份就是舊版整檔安裝 | 原地包起來（**不是**再 append 一份） |

⚠ `LocalEdit` 與 `Stale` 分開是重點：Stale 覆寫是安全的，LocalEdit 覆寫會吃掉人寫的字。
⚠ `Duplicated` / `MarkerBroken` **連 force 都不放行** —— 那兩種是「我不知道該動哪裡」，
不是「我知道但怕你心疼」。硬做的結果是留下另一個還在生效的區塊，而畫面顯示成功。

## 4. 🩸 遷移：現有安裝整份都是 template

實測（2026-08-30，本 repo）：

```
sed 's|{{UCL_CORE_PATH}}|Assets/Plugins/UCL_Core|g' ClaudeTemplate/CLAUDE.md | diff - CLAUDE.md
→ IDENTICAL
```

⇒ 天真實作會判「整份都是使用者內容」，然後在下面再 append 一份 ——
**同一份規則出現兩次，而兩份都是真的、沒有一層會報錯。**

判準刻意**嚴格**：正規化後與來源逐字相同才算 `NeedsMigration`。
判錯的代價不對稱 —— 誤判成 migration 會吃掉使用者的字，誤判成 not-installed 只是多一段要人工收。

> 🩸 **這一格的第一版是錯的**（同日抓到）：`Apply` 呼叫 `Parse(existing, null)`，
> 而 migration 的判斷被 `iExpectedManaged != null` 的閘門擋掉 ⇒ 舊版整檔安裝被判成
> `NotInstalled` ⇒ 真的 append 出第二份。
> **抓到它的不是我又看了一遍，是 selftest 裡那一格「遷移後不重複」。**

## 5. 落地時多兩道護欄（skill 安裝沒有、這裡才有）

1. **第一次改動前落一份 `.scp_backup`** —— 消費端不一定有 git；而備份**不覆寫既有的那份**
   （那份是「我們第一次碰它之前」的樣子，最有價值）。備份失敗 ⇒ **不寫**。
2. **寫完回讀**，回讀不是 `Synced` 就算失敗。寫入端會替自己說謊。

原子替換走 `File.Replace`（netstandard2.1 沒有三參數 `File.Move`，見 `Coding_Standards.md` §1.1）。
⛔ 不可以退化成「先 Delete 再 Move」—— 中間那一格「檔案不存在」長得跟「還沒設定過」一模一樣。

## 6. 驗收讀數（`senate selftest`，2026-08-30 實跑）

```
入口檔區塊      新檔檔頭是 BEGIN=True／既有內容不動=True／判 Synced=True／END 後的字活著=True
                ／只有一個區塊=True／套兩次逐字相同=True／移除後前後都在=True
                ／CRLF 不造成幻影 Stale=True                                            ✓
入口檔異常形狀  兩個區塊=Duplicated／缺 END=MarkerBroken／手改=LocalEdit／來源更新=Stale
                ／舊版整檔=NeedsMigration（遷移後不重複=True）／一般檔=NotInstalled       ✓
入口檔落地      新建／再跑不動檔／前後使用者內容都活著／有備份
                ／手改時拒寫且檔案沒動／force 才動／移除後使用者的字全在                  ✓
```

## 7. ⚠ 還沒做的（不要當成已完成）

- **per-target 的受管內容來源**：`AgentTemplateManifest.json` 目前只有「整檔 template」，
  還沒有 fragment 型 template 與 `mode: full | append` 欄位。
  ⇒ 三個 target 一律 append 是錯的：`.agents/rules/UCL_Core_Entry.md` 是 UCL 專屬檔，
  該維持整檔覆寫。這格要 Tim 拍。
- **skill 管理頁**還沒接這一層（引擎在，UI 沒有）。
- **Unity 那側零讀數** —— 以上全部只在 .NET 這側跑過。
