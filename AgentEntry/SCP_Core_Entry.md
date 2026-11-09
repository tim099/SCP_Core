# SCP_Core — Agent 入口（agent-neutral）

> [!IMPORTANT]
> 本檔是 **SCP_Core submodule 的 agent 入口薄索引**，內容 agent-neutral
> （Claude Code / Codex / Antigravity / Gemini 皆適用）。
>
> ⚠ `<SCP_Core>` 是佔位符：各專案掛載位置不同
> （`Assets/Plugins/SCP_Core` / `<repo>/SCP_Core`…）。**不要寫死安裝路徑** ——
> 寫死的失敗形狀是靜默（`File.Exists` 失敗後 fail-soft return，連 warning 都沒有）。

## 這裡有什麼

| 主題 | 位置 |
|---|---|
| **撰寫規範**（方言 / JSON 走 SCP_Json / 設定走 prefs / 路徑單一落點） | `<SCP_Core>/Docs~/Coding_Standards.md` |
| **入口檔受管區塊**（本檔就是被那個機制裝進來的） | `<SCP_Core>/Docs~/Entry_Doc_Blocks.md` |
| **Agent skills** | `<SCP_Core>/Skills~/<name>/SKILL.md` |
| 指令系統（`senate cmd`） | `<Senate>/Docs/Workflows/SCP_Cmd_System.md` |
| **查文件**（`senate cmd doc`）＋**文件住哪**：指令住哪、文件就住哪 | `senate cmd doc --arg op=show --arg name=Doc_Query` |

## 先配置環境：`senate` 要找得到

下面每一支都是 `senate …`，它找不到，skill 和指令系統就全部用不了。
**第一次用、或撞到 `senate: command not found`，先在 Git Bash 跑 Senate repo 根層的安裝腳本：**

```bash
<Senate>/install.sh             # 前置檢查（.NET 10 SDK、git）→ build → senate init → 掛使用者 PATH → 驗收
```

- `<Senate>` 是 Senate repo 的位置（佔位符，同上：**不要寫死**）。只支援 Windows（Git Bash）；
  Linux／macOS 的做法腳本會印出來。反向操作是 `<Senate>/uninstall.sh`。
- PATH 掛的是 `<Senate>/publish/`（`senate.exe` 住那裡，是 build 產物）。
  ⚠ **PATH 寫進去了不代表找得到**：`publish/` 被清掉（重新 clone、清產物）時，
  PATH 照樣指著它，`senate` 照樣找不到 —— 先看 `<Senate>/publish/senate.exe` 在不在，不在就重跑安裝。
- ⚠ **已經開著的終端機／agent session 不會讀到新 PATH**（PATH 是 process 啟動時複製的）。
  裝完要開新視窗；session 內等不及就直接叫 `<Senate>/publish/senate.exe …`。
- 安裝失敗看腳本印的那一行 ✗，**不要憑印象或舊記憶繞過去照做**；環境判定在 `senate doctor`。

## 常用入口指令

```bash
senate cmd                      # 依分類列出外層指令（`help <分類>` 只列一類；內層由入口的回傳指路）
senate cmd help <name>          # 單支的參數說明（＋它的使用說明在哪份文件）
senate cmd doc                  # 列出文件；--arg op=show --arg name=<名字> 看全文
senate ui                       # 後台頁（純文字）
senate ui --window              # 後台頁（原生視窗）
senate selftest                 # 自我對拍：印出讀數，不是印 ✓
```

## 消費端 repo 的規則放哪

SCP_Core 只管**跨專案共用的 agent 機制**。專案自己的規則放該 repo 的共用文件，
**不要往 SCP_Core 塞專案限定內容**。
