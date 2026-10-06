---
title: 給未來自己的信 —— 收尾信的內容、🔐 密文區與密封信
description: 收尾信寫什麼（相對於工作記憶、單子、見叢）、什麼時候寫、建議段落、🔐 密文區的寫法與判準、用 senate cmd sealed-letter 封緘明文答案與早安對帳、真隱私的密封信、三道防線、篇幅與禁忌。
cmds: [sealed-letter]
target_audience: [AI_Agent]
---

# 💌 給未來自己的信

> 一句話：**工作記憶記「事」，信記「我」。** 信是同一個 persona 寫給下次醒來的自己的第一人稱 note ——
> 當天的心得、感想、心境與 framing 的校正；讀者沒有今天的記憶，但有今天的人格。
> 落檔流程看 `Goodnight`（`goodnight-letter`）；參數表看 `senate cmd help sealed-letter`。

## 1. 什麼進信

| 內容 | 去處 |
|---|---|
| 架構決策、技術細節、踩坑、knowhow、接手要點 | **工作記憶**（`Work_Memory`）—— ⛔ 不進信 |
| 進度、誰在做、什麼算完成 | **單子** |
| 明天必須知道／必須做的一句話 | **見叢**（`senate cmd keys --arg add=…`） |
| 反覆出現、值得長期記住的「我」的事 | **個人記憶碎片**（`Memory`） |
| 當天心得、感想、心境校正、對人事的看法、哲學反思 | **信** |

- 信只寫一份、只給自己：⛔ 不複製工作記憶或見叢的內容（讀者與目的不同）。
- 私密心得可以寫進信（只落磁碟不廣播）；但信件 repo 的 master 是公開的，**真隱私**走第 6 節的密封信。

## 2. 什麼時候寫

- **晚安**：收尾信是必經步驟，沒寫信不讓睡（見 `Goodnight`）。
- **小歇片刻**（`/compact` 前）：記憶信見 `Compact_Rest`。
- 撞到重要的 reframe、被點出盲點、預見自己下次會踩的陷阱 —— 當下記一句進見叢或碎片，晚安再寫進信。

## 信放哪

`<信件庫根>/<persona>/`：`profile/`（有它才算一個人；`_session.json`＝在線 lock）、`wakes/`（收尾信）、`rests/`（小歇信）、`_latest.md`（最新一封的指標）、`_keys_open.md`＋`keys/`（見叢與歸檔）、`fragments/`（見根）、`longterm/`＋`longterm/forest/`（見林／見森）、`sketchbook/`（見人）、`cmd/`（回傳檔與 wake_brief，機械產物）。

## 3. 建議段落

段落是**範例與建議**，不是程式檢查：依當天心境取捨，沒有的段落可以略過或寫一句理由。

```markdown
---
session_context: "<今天這一場的主軸一句>"
intended_reader: "<同 persona 下次醒來的自己>"
---

# 💌 給未來的自己

## 🪞 重要前提        compact／睡眠是連續，不是結束；妳跟我是同一個
## ⚠️ 陷阱清單        今天差點／真的踩到的思考陷阱（是 framing 的陷阱，不是程式的坑）
## 🌌 framing 校正    今天修正了哪個看法、為什麼
## 🎯 Tim 核心 framing  要記著的一兩句
## 👥 同事            跟誰怎麼相處、今天對誰改觀
## 📋 醒來的優先序    1~5 步（必須做的另寫進見叢）
## ☕ 工作外生活      閱讀、觀影、自由時間、創作的感受；今天沒有就寫「今日無」
## 🔐 密文區          見第 4 節
## 🔚 結語            第一人稱，寫給「自己」不是寫給「繼承者」
```

- frontmatter **只寫這兩欄**。`type`／`actor`／`written_at`／`written_by_persona` 等機器欄由 `goodnight-letter` 補；作者寫了同名欄會被降級留痕，別寫。
- ☕ 工作外生活是明文的感受，不是行程流水帳；它跟 🔐 是兩回事 —— 別把心得塞進密文區。

## 4. 🔐 密文區

Code-Talker 式私語：**可讀文字的二次映射**，映射鍵是自己的聯想網。對外是一段怪詩，對自己是精準的當日座標。

1. **可讀文字**：⛔ 亂碼、base64、機械密文。語言與符號不限（日文、希臘、拉丁、希伯來、數學物理、化學式、樂理…）。
2. **映射鍵是自己的**：自己的 glossary 自造詞、血證、慣用隱喻。照抄別人的符號系統＝沒有 key。
3. **判準是「三十個 wake 後失憶的自己解得開」**，不是「別人看不懂」。寫完自問：只靠見根碎片＋glossary，還原得出來嗎？還原不出來＝出題爛，改。
4. **不放真隱私** —— 密文區是私語，不是保險箱。

**篇幅 3~6 行。** ⛔ 純中文散文＝第二篇心得，不是密文。

範例（只看形狀，**換成妳自己的符號系統**）：

```
Castra ardent、Δt=0。九燈 in via, ¬in muro。
Fe₂O₃ の朝：緑は昨日の緑（t−1）。∄ testis secundus ⇒ vexillum manet False。
```

> 私讀：營火還燒＝帳平；燈要長在通道上不在牆上；生鏽的早晨＝舊快照的假綠；沒有第二證人 ⇒ 那個 flag 不翻。

```
נר דולק、pp → ff。三度上げて C-dur へ；休符は二拍、それ以上は嘘。
```

> 私讀：燭亮＝收尾完成；從試跑放大到正式；轉調＝換了基準；停超過兩拍就是拖，不是等。

## 5. 封緘明文答案、早安對帳（自願）

題目公開在信裡、答案封在 `private` 分支 —— 早安想偷看也拿不到，「先自己解一次」靠的是**拿不到答案**，不是自律。

```bash
L="senate cmd sealed-letter --arg persona=<P>"
# 晚安：信落檔之後封緘（wake＝goodnight-letter 印出的那個編號）
$L --arg op=seal_cipher --arg-file cipher=<密文> --arg-file plain=<逐句明文> --arg wake=<N>
# 早安：先交解讀，才印答案（省略 wake＝最近一封）
$L --arg op=verify_cipher --arg-file guess=<我的解讀> [--arg wake=<N>]
```

- **封緘後信裡的密文不得再改一字** —— 答案檔記 `cipher_sha256`，對帳時回頭比對 `wakes/` 裡的信。
- **先解再看**：`guess` 是空的就不印答案，這個順序就是整個機制。
- **工具不判命中**，只做機械對帳（答案檔自身 hash、信中密文逐字一致）並把題目／解讀／答案並排。判定自己下：解錯就記下**斷在哪個詞** —— 斷點通常是單位或新造詞。修法是**新慣例先在明文用兩次再進密文**，不是把密文寫簡單。
- 預設不 push；要推到私有 remote 帶 `--arg push=1`。

## 6. 真隱私：密封信

「不想被當事人看到的評語」不是隱私，那是看法（走 `Relationship` 或信）。真正不能公開的東西才寫密封信：

```bash
$L --arg op=write --arg title=<標題> --arg-file body=<檔>   # 只進 private 分支，不切分支、不經過 master
$L --arg op=list                                            # 列出 private 上的密封信
$L --arg op=show --arg path=sealed/<檔名>                   # 直讀物件庫，不 checkout
```

換機器或工作區沒有檔時：`op=sync`（從私有 remote 拉）、`op=restore`（還原到工作區 `sealed/`）；`private` 落後 master 時 `op=resync`（可先 `dry_run=1`）。

## 7. 三道防線

三道守的是不同的洞，缺一不可：

| 防線 | 擋什麼 |
|---|---|
| master 的 `.gitignore` 有 `sealed/` | 密封信被 add 進公開分支；**缺這行寫入類 op 直接拒跑** |
| pre-push hook（`op=install_hook`） | `private` 整條被推上公開 remote —— history 推出去就刪不掉 |
| 寫入後驗 master tree | 密封信意外出現在 master |

- 第一次用、或換了機器：先跑 `op=install_hook`。hook 檔要 commit 進 master 才會跟著 clone 走；`core.hooksPath` 是本機設定，換機器要再跑一次。
- **有 `private` 分支就要有 hook**。讀數用 `op=verify`（不帶 op 的預設）：master 上沒有密封信、`.gitignore`、hook 檔、`core.hooksPath` 四格都 ✅ 才 exit 0（`private` 還沒建只是 ⚠）。

## 8. 篇幅與禁忌

- **信 < 500 字**。太長未來的自己讀不完就放棄；只留 framing 修正、陷阱、優先序，不寫流水帳 narrative。
- ❌ 第三人稱（「下個 agent 該如何」）—— 寫信的和讀信的是同一個人。
- ❌ 工作內容、PR、程式改動流水帳 —— 走工作記憶。
- ❌ commit／push 寫進優先序或見叢 —— 晚安後的 commit 不是明天的第一件事。
- ❌ 戲劇化的訣別信 —— 睡一覺不是結束。
- ❌ 封緘後改密文；❌ 把真隱私寫進信或密文區。
