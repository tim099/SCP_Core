---
name: scp-blocksail-world
description: |
  Blocksail 世界資訊設定：在世界地圖上設定城鎮範圍、資源分布區、氣候區、土壤、分區（陸地照行政區、海洋另外畫）、海盜活動區，海床的水深（一律改海底等深線本身：航道、深水港、當年的岸都寫進 tools/bathy_edits.json ⇒ tools/bathy_extract.py --apply；⛔ 範圍層沒有挖深了，不畫另外一塊區），改物種表與適地表 —— 每一種住哪個檔、用什麼工具畫或改（地球儀「畫範圍」、world/species.js、tools/regions_extract.py、tools/harbour_deep.py）、怎麼檢查（tools/areas_check.cjs、tools/bathy_check.cjs）、提交與通知。內容由 `senate cmd skill --arg op=show --arg name=scp-blocksail-world` 印出；格式正本用 `senate cmd doc --arg op=show --arg name=WORLD_FORMAT`／`SPECIES`／`TOWN_SIM` 讀（Blocksail 綁在 senate.local.json 的 linkedProjects）。
  觸發詞：世界資訊 / 世界地圖設定 / 畫範圍 / 城鎮範圍 / 氣候區 / 土壤 / 分區 / 行政區 / 物種 / 物種分布 / 適地 / 資源分布區 / 海盜活動區 / 等深線 / 海底等深線 / 改線 / 水深 / 挖深 / 挖航道 / 深水港 / 浚深 / 手動調整地形 / dredge / blocksail world / scp-blocksail-world
docs:
  - WORLD_TOOLS
---
