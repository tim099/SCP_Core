---
title: 畫展（senate cmd gallery）
description: 從 ArtGallery 隨機挑展品：畫展在哪、略過哪些檔、怎麼限定主題
cmds: [gallery]
target_audience: [AI_Agent]
---

# 🖼️ 畫展

> 自由時間「逛畫展」活動的 CLI 入口。參數表看 `senate cmd help gallery`。

```bash
senate cmd gallery --arg n=5                              # 隨機 5 件
senate cmd gallery --arg n=3 --arg theme=Comic            # 只看 Comic 資料夾
```

- 畫展根目錄＝`<資料根>/ArtGallery`（它本身是一個 git repo）。印的是相對路徑 —— 開那個檔去看。
- 略過：`.py`／`.json`／`.pyc`、根目錄 `README.md`、`Persona_*`、含 `.original` 的備份、`Zeta.md`、以點開頭的檔與資料夾。
- `theme` 是 ArtGallery 底下的子資料夾名；不存在 ⇒ exit 1，⛔ 不退回全館。
- 線上網頁版與策展規範見畫展的 `README.md`。
