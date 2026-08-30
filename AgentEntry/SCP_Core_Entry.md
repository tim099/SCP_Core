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
| 指令系統（`senate cmd`，不需要 Unity） | `<Senate>/Docs/Workflows/SCP_Cmd_System.md` |

## 常用入口指令

```bash
senate cmd                      # 列出所有可用指令
senate cmd help <name>          # 單支的參數說明
senate ui                       # 後台頁（純文字）
senate ui --window              # 後台頁（原生視窗）
senate selftest                 # 自我對拍：印出讀數，不是印 ✓
```

## 消費端 repo 的規則放哪

SCP_Core 只管**跨專案共用的 agent 機制**。專案自己的規則放該 repo 的共用文件，
**不要往 SCP_Core 塞專案限定內容**。
