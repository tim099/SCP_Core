---
title: 文件查詢（senate cmd doc）與「文件住哪」的規則
description: 從 CLI 查文件（列出／全文／搜尋）、help 怎麼指到文件、文件的 frontmatter 要寫什麼
cmds: [doc, help]
last_updated: 2026-09-29
target_audience: [AI_Agent, Tools_Maintainer]
---

# 📖 文件查詢與「文件住哪」

> 一句話：**文件是 md，住在指令所在那一邊；CLI 只是讀它的入口。**
> skill 只寫「去跑哪條 CLI、查哪份文件」，⛔ 不重抄操作 —— 操作跟文件各只有一份。

## 1. 怎麼查

```bash
senate cmd doc                                        # 列出全部（名字、住哪一邊、標題、對應指令）
senate cmd doc --arg op=show --arg name=<名字>          # 印一份的全文（名字＝檔名去掉 .md，不分大小寫）
senate cmd doc --arg op=search --arg keyword=<關鍵字>   # 全文搜尋，逐行、不分大小寫
senate cmd help <指令>                                  # 參數表＋「文件：<名字>」那一行
```

| 退出碼 | 意思 |
|---|---|
| 0 | 查到了。**搜尋命中 0 也是 0** —— 那是一個答案，輸出會寫「命中 0（掃 N 份）」 |
| 2 | 你要的不存在：沒有這個名字、或撞名（兩邊各有一份同名的，⛔ 不替你挑） |
| 3 | 根本查不了：這個宿主沒有裝上文件根 —— 跟「沒有這份文件」分開 |

⚠ 輸出第一段若有 `⚠ 文件根不存在` 或 `⚠ 文件撞名`，**這次的清單不完整**，先處理那一行。

## 2. 分工：help 管參數，文件管用法

- **參數表**由 Cmd 的 `ArgSpecs` 產生（`help` 印的那段），⛔ 文件不抄參數表 —— 兩份參數表遲早各說各話。
- **文件**寫 help 寫不下的：什麼時候用、跟哪支搭配、退出碼怎麼讀、有什麼紀律與踩過的坑。
- `help <指令>` 會印「文件：<名字>」，那一行來自**文件自己的 frontmatter**：

```yaml
---
title: …
description: …
cmds: [tavern-post, tavern-wait]   # 這份文件講哪幾支指令 —— help 靠它反查
---
```

  對應關係只寫在文件這一邊，⛔ 不寫進 Cmd 的 C#：文件是最常被改的那一邊，改文件的人順手就改得到。
  沒有任何文件列到的指令，help 會明說「沒有對應的使用說明」。

## 3. 文件住哪（規則）

**指令住哪，文件就住哪：**

| Cmd 的型別在 | 文件放 |
|---|---|
| SCP_Core（`SCP.Core.*`） | `<SCP_Core>/Docs~/` |
| Senate（`Senate.*`） | `<Senate>/Docs/`（分類照 `DOC_INDEX.md`） |

`help <指令>` 第二行印的「型別：…」就是判準。

## 4. 宿主要做的事

文件根由宿主裝上（`SCP_DocStore.RootsProvider`），共用層不推導路徑。
Senate CLI 錨在 **exe 所在的 repo**：`<repo>/Docs` 與 `<repo>/SCP_Core/Docs~` —— ⛔ 不看 cwd。
沒裝的宿主 `doc` 回 3、`help` 的文件那一行寫「查不了」，⛔ 不會假裝沒有文件。
