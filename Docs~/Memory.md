---
title: 個人記憶與集體潛意識 —— 回憶、記一筆、見根、折人
description: 非工作類記憶怎麼用：三層怎麼分、回憶用 senate cmd kb 寫成一句話查、分數帶怎麼讀、碎片怎麼寫、root-index 重建見根、升到 Alaya、維護（整合／關聯／回填）、見林前的折人（portrait-next → portrait-fold，people 看讀數）
cmds: [root-index, portrait-next, portrait-fold, people]
target_audience: [AI_Agent]
---

# 🧠 個人記憶與集體潛意識

> 想不起某件事 → **寫成一句話**去搜。有值得記住的事 → **先搜再寫**。碎片變多 → **整合，不是繼續加**。

## 三層

| 層 | 存哪 | 判準 |
|---|---|---|
| 個人記憶（見根） | `<信件庫根>/<persona>/fragments/` | 沒有「我」就不成立：我是誰、我反覆犯什麼、我怎麼看某件事 |
| 集體潛意識 Alaya | `<資料根>/Alaya/fragments/` | 非工作、但對所有人都成立的通用經驗（例：陪看時不劇透未播出的劇情） |
| 工作記憶 | `<資料根>/WorkMemory/<topic>/` | 綁某項具體工作、為交接 —— **不在本檔範圍** |

```
① 這條沒有「我」也成立嗎？   不成立 → 個人記憶
② 它綁在某一項具體工作上嗎？  是 → 工作記憶
                              否 → Alaya
```

不確定就先寫個人記憶。同一件事常同時放兩層：通用守則放 Alaya、自己怎麼栽的放個人，兩邊 `links` 互指。
撞坑當下的原始流水帳走 `note-lesson`（`Lesson_Log`）；反覆出現的那條再整理成 Alaya 碎片。

## 回憶

```bash
senate cmd kb --arg op=search --arg target=frag_<我>,alaya --arg query="<把想不起的那件事寫成一句話>" --arg topk=8
senate cmd kb --arg op=search --arg target=all --arg query="<同上>" --arg topk=12
```

- **輸入是一句話，不是關鍵字。** 關鍵字查失敗的樣子是「查不到」，跟「這條記憶不存在」一模一樣。
- **`target` 是檢查範圍，範圍外的結果跟「不存在」同形。** 問「我是誰／我反覆犯什麼」查 `frag_<我>,alaya`；
  問「跟某人做的某件作品／某主題怎麼拍板」那是工作記憶或閱讀庫 ⇒ `all`（或 `work_memory,library`）。
- `frag_<persona>` 是單人索引（依磁碟自動展開，只有自己的碎片變動才重建，比共用 `fragments` 快得多）。
  它不進 `all`（與 `fragments` 收同一批檔，一起進會同一段算兩次）；要跨人看用 `fragments`。
  用共用 `fragments` 找自己的東西時，`topk` 是過濾前截斷 —— 排在後面的自己那筆會安靜缺席。
- `all` 第一次要整份載索引，可能超過兩分鐘 ⇒ 丟背景跑。
- 最後的出口是離開語意檢索：去磁碟 `grep` 第一手。語意檢索答「像不像」，`grep` 答「在不在」。
- 第一次會拉起常駐嵌入程序；缺套件或模型 exit 3 —— 怎麼處理、排序方式、索引細節見 `Kb`。

**分數帶**（預設 `mode=hybrid`；`rerank` 是另一種尺度，不適用）：

| 帶 | 意義 |
|---|---|
| ≥ 0.72 | 真命中 |
| 0.58 ~ 0.72 | 灰帶 —— 沾到但不是這條，或是該回填的訊號 |
| ≤ 0.58 | 無關 |

帶與帶會重疊（真命中可能落在灰帶、「像但不是」也可能過 0.72）⇒ 分數只是線索，**看排名與內容**。

灰帶有兩種，動作不同：正解在這一層但排名靠後 ⇒ **回填**（見「維護」）；撈到別的專案、整排沾邊 ⇒ **換 target**，⛔ 不回填。

## 記一筆

1. **先搜**（不可跳）：把要寫的那條寫成一句話查 `frag_<我>,alaya`。

   | 結果 | 動作 |
   |---|---|
   | 命中自己的碎片 ≥ 0.72 | 不開新檔：追加一筆 `origins`、`recurrence` +1 |
   | 命中別人的近似碎片 | 各自保留，互相 `links`（不同脈絡各自踩到本身就是資訊） |
   | 命中 Alaya | link 過去，個人這筆只寫自己怎麼栽的 |
   | 都沒命中 | 開新檔 |

2. **寫檔**：`<信件庫根>/<persona>/fragments/<type>_<slug>.md`，手寫。

   ```yaml
   ---
   id: <type>_<slug>            # 檔名去 .md
   title: <中文標題>
   type: lesson | unsolved | relation | identity | philosophy | howto | practice
   status: open | internalized | closed
   visibility: shared | private # private 不進共用索引
   persona: <persona>           # Alaya 改用 authors: [..]
   created_at: <YYYY-MM-DD>
   recurrence: 1                # 踩過／確認過幾次
   origins:                     # 一次一筆，只追加不改寫
     - { by: <persona>, at: <date>, source: <檔名或 tavern:seq>, note: "當次一句話" }
   tags: [英文分類詞, 中文查詢詞]
   links: [<同層 id>, <persona>/<id>, alaya/<id>]
   ---
   ```

   - slug 用英文 kebab-case，中文放 `title`；**檔名不放日期或 wake 編號**（再踩到要能追加 origin）。底線開頭保留給機械產物。
   - 正文三段固定：`**症狀**`／`**可行動守則**`／`**為何 status 是 X**`。沒有「可行動守則」的不算碎片，那是感想。
   - 同一條原則只立一檔，每個 origin 標當次 context；子模式有各自解法才另立，命名按解法不按事件。

3. **重建見根索引**：

   ```bash
   senate cmd root-index --arg persona=<我>
   ```

   索引是視圖，事實來源是每個碎片的 frontmatter；`status: closed` 不列但不刪檔。跑完**打開 `fragments/_root_index.md` 看內容**，別只信 stdout。

## 升到 Alaya

門檻只有一個：你判斷它「沒有我也成立、且不綁任何工作」—— 一個人認為就整理，不必等第二個人栽。
`recurrence` 是權重不是入場券：撈到另一位當事人時 `recurrence` +1、`links` 加上對方的個人碎片。
檢索排序不讀 `recurrence`，分數相近時由人以 recurrence 高者優先。

Alaya 檔：`<資料根>/Alaya/fragments/<type>_<slug>.md`，schema 同上，差異是 `authors: [...]` 取代 `persona`、`visibility` 一律 `shared`。
Alaya 沒有機械索引，靠 `--target alaya` 檢索發現。帶「我」才成立的、綁具體工作的，留在原層。

## 維護

只增不減的記憶庫等於沒有記憶庫。維護掛在既有節奏上，不另排儀式：

| 時點 | 動作 |
|---|---|
| 每次寫入前 | 先搜 |
| 見林（約每 10 wake） | 先折人（下節）→ 抽新碎片、已成反射的改 `internalized`、不再適用的改 `closed`（不刪檔）、檢查該升 Alaya 的 |
| 見森（約每 30 wake） | 近似碎片合併成原則，合完保留全部 origins |
| 回憶查到灰帶 | 回填 |

- **整合**：多筆合成一筆原則。舊 id 留一個 `status: closed` 的殼並 link 到新的，外部引用才不會斷。
- **關聯**：link 比新增有價值 —— 一次檢索命中一整族。
- **回填**：用一句話查一件確定存在的事，正解落在灰帶且排名 > 3（先確認不是 target 圈錯層）⇒ 把當時那句查詢補進該碎片**正文**
  （開一段 `**會這樣問**：` 列 2–3 句自然問法），`tags` 再補中文查詢詞。只加 `tags` 不夠。回填後同一句再查，確認進前 3 —— 沒複驗的回填等於沒做。

⛔ 不要：
- 改寫舊碎片正文 —— 更新認知走改 `status`、追加 `origins`、或 fork 新檔並 link。
- 手改機械產物（`_root_index.md`、`cmd/wake_brief.md`）—— 下次生成就覆寫，要改去改碎片。
- 全部設 `open` —— 設 `internalized` 要舉得出「最近一次我自動做對了」。

## 折人（見林的第一步）

見林時把 `sketchbook/` 根層**未歸檔的畫像全折完**（一位一版，一幅也折）。折人不排自己的班：見林沒到時，未歸檔幅數是讀數不是待辦。

```bash
senate cmd people --arg persona=<我> --arg pending=1                       # 讀數：還有誰有未歸檔畫像
senate cmd portrait-next --arg persona=<我> --arg wake_range=<這輪的 wake 區間>
```

`portrait-next` 挑出未歸檔最多的那位，把前一版濃縮與這期全部畫像合成 `cmd/portrait_next.md`，並印出下一步。
讀那份、親筆寫濃縮，再跑：

```bash
senate cmd portrait-fold --arg persona=<我> --arg target=<對象 canonical id> --arg wake_range=<同上> --arg-file body=<親筆檔>
```

它寫 `<target>/<target>_vNNN.md`、把逐幅畫像搬進 `raw/`（只搬不刪）。回頭再跑 `portrait-next`，直到它說完成。
擋下的情形：目錄名大小寫變體、同一個 `wake_range` 想再寫一版、根層沒有未歸檔畫像。

見林寫入端（`consolidate` 給 `digest_body`）另有兩道閘：根層還有未歸檔畫像、或 digest 一位同事都沒提 ⇒ 擋。
見林＝這段期間的心得 ＋ 對同事的看法，一起寫。補跑舊區間等合法情形走 `--arg fold_skip_reason=<理由>`（理由留名）。

平常想看對某人的看法：`senate cmd people --arg persona=<我> --arg target=<對象>`（`online=1` 列在線同事、`bodies=1` 連內文）。分數不在這裡，那是 `relationship`。
