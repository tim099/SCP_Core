---
name: scp-blocksail-town
description: |
  Blocksail 城鎮建造：照城鎮那一筆範圍＋地形＋適地＋種子生成城鎮規劃（中心城區、城牆、公共建築、港口、道路、沿路建地；港口的棧橋伸到大船水深、橫頭與支棧橋、泊位與錨地、泊位要通得到外海；港口的水深一律改海底等深線本身：海的線沿水道彎進港，tools/harbour_deep.py ⇒ tools/bathy_edits.json ⇒ tools/bathy_extract.py --apply；⛔ 不挖深、不另外畫一塊區），用 town.html 與 tools/town_plan.cjs 換規模、風格、挑種子寫回那一筆，用 world/towns/<id>.js 拆掉或手擺建築、從空白平地復刻歷史城市，用 tools/town_check.cjs 走另一條路驗，改生成器 world/town.js 的紀律、提交與通知。內容由 `senate cmd skill --arg op=show --arg name=scp-blocksail-town` 印出；設計與格式正本用 `senate cmd doc --arg op=show --arg name=TOWN_BUILD` 讀（Blocksail 綁在 senate.local.json 的 linkedProjects）；範圍本身怎麼畫看 scp-blocksail-world。
  觸發詞：城鎮建造 / 建造城鎮 / 建城 / 城鎮規劃 / 城市建造 / 中心城區 / 城牆 / 城門 / 港口城鎮 / 港口 / 棧橋 / 支棧橋 / 泊位 / 錨地 / 停泊 / 航道 / 通外海 / 船被困住 / 水深 / 港口水深 / 等深線 / 海床平滑 / 沿路建地 / 手動配置 / 復刻歷史城市 / town.html / town_plan / blocksail town / scp-blocksail-town
docs:
  - TOWN_TOOLS
---
