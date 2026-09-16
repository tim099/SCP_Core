// 區塊職責：閱讀追回檔的**渲染層** —— reader/media/work/chapter/character 五種落檔 → 一份 md 視圖。
// 物理意義：這一叢是整個寫入端的**葉**：`SyncBookshelf` 呼叫它、而 `EnsureReaderJson` 呼叫 `SyncBookshelf`、
//           `NoteChapter` 每次寫入也呼叫它（「stale 投影比沒有投影更糟」）。
//           ⇒ 上面每一支寫入 op 都經由本檔收斂，所以它是 TASK-0166 ① 的第一刀。
//   🩸 為什麼不先搬「最小的那支 op」（2026-09-16 現撈的相依鏈）：
//     `MediaInit → EnsureReaderJson → SyncBookshelf → RenderRecall → AppendCharacters → ReadFactsList`
//     —— 從上面切會一路拖到這裡，而中途停手的落地樣子是「reader.json 建得出來、回 ✅，
//     而書架與追回檔停在上一次」：**每一層都綠的失效**。⇒ 由葉往根切。
// 數值影響：**純讀＋純渲染**，本檔一個位元組都不寫（唯一的寫入口是 `WriteRecallBrief`，
//           而它只是把渲染結果交給 `SCP_LibraryIO.SaveText`）。
// ⚠ 本檔的驗收判準是**逐位元組等於 Editor 端 `UCL_ReadingLibraryIO.RenderRecall` 的輸出**，
//   ⛔ 不是「看起來一樣」也不是「兩邊都跑得動」。少一節、多一個空行、順序換一下都算不同 ——
//   🩸 前例：reading-recall 兩版（python 6308 bytes ／ C# 4210 ／ 1973）**互有對方沒有的節**，
//   而每一版單獨讀起來都完整。⇒ 收斂只能逐節點名，不能整段照抄任一邊。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Library
{
    public static class SCP_LibraryRecall
    {
        // ===========================================================
        // 區塊職責：把一位讀者在一部作品上的全部落檔渲染成追回檔。
        // 物理意義：frontmatter 明寫「機械產物、手改會被覆寫」—— 這份是**視圖不是筆記**，
        //          事實源永遠是 reader.json / chapter round / character view。
        // 數值影響：讀不到 reader.json ⇒ 回 null ＋ oError，⛔ 不回一份「看起來正常的空白追回檔」。
        // ===========================================================
        public static string? RenderRecall(string iDataRoot, string iMediaId, string iPersona,
                                           bool iFullRounds, out string? oError)
        {
            SCP_JsonData? aReader = SCP_LibraryIO.LoadReader(iDataRoot, iMediaId, iPersona, out oError);
            if (aReader == null) return null;

            SCP_JsonData? aMedia = SCP_LibraryIO.LoadJson(
                SCP_LibraryStore.MediaJsonPath(iDataRoot, iMediaId), out _);
            string aWorkId = aMedia != null ? aMedia.GetString(SCP_LibraryIO.Key_WorkId, "") : "";
            SCP_JsonData? aWork = string.IsNullOrEmpty(aWorkId)
                ? null
                : SCP_LibraryIO.LoadJson(SCP_LibraryStore.WorkJsonPath(iDataRoot, aWorkId), out _);
            SCP_JsonData? aProgress = aReader.Contains(SCP_LibraryIO.Key_Progress)
                ? aReader[SCP_LibraryIO.Key_Progress] : null;

            var aSb = new StringBuilder();
            // frontmatter —— 與 cmd/wake_brief.md 同慣例。
            // ⚠ generated_at 用**本機時間**：跨機比對時以檔內 media/persona 為準，不是這一欄。
            aSb.AppendLine("---");
            aSb.AppendLine("type: reading_recall");
            aSb.AppendLine($"persona: {iPersona}");
            aSb.AppendLine($"media_id: {iMediaId}");
            aSb.AppendLine($"work_id: {(string.IsNullOrEmpty(aWorkId) ? "unknown" : aWorkId)}");
            aSb.AppendLine($"generated_at: {DateTime.Now:yyyy-MM-ddTHH:mm:sszzz}");
            aSb.AppendLine("generated: mechanical   # 每次 recall / 寫入後重新生成 —— 手改會被覆寫");
            aSb.AppendLine("source_of_truth: AgentCommands/BookNotes/Library");
            aSb.AppendLine("---");
            aSb.AppendLine();
            aSb.AppendLine($"# 📖 閱讀追回｜{(aWork != null ? aWork.GetString(SCP_LibraryIO.Key_Title, iMediaId) : iMediaId)}");
            aSb.AppendLine();
            aSb.AppendLine($"- reader：`{iPersona}`　media：`{iMediaId}`" +
                           $"（{(aMedia != null ? aMedia.GetString(SCP_LibraryIO.Key_MediaKind, "unknown") : "unknown")}）");
            if (aWork != null)
            {
                aSb.AppendLine($"- 原文名：{aWork.GetString(SCP_LibraryIO.Key_TitleOriginal, "（未登錄）")}　" +
                               $"作者／監督：{aWork.GetString(SCP_LibraryIO.Key_Author, "（未登錄）")}");
                SCP_JsonData? aAliases = aWork.Contains(SCP_LibraryIO.Key_Aliases)
                    ? aWork[SCP_LibraryIO.Key_Aliases] : null;
                if (aAliases != null && aAliases.IsArray && aAliases.Count > 0)
                {
                    var aNames = new List<string>();
                    for (int i = 0; i < aAliases.Count; i++)
                    {
                        string a = AliasToString(aAliases[i]);   // 物件形狀 alias 也要印，別靜默跳過
                        if (!string.IsNullOrEmpty(a)) aNames.Add(a);
                    }
                    if (aNames.Count > 0) aSb.AppendLine($"- 別名（搜尋用）：{string.Join(" / ", aNames)}");
                }
            }
            aSb.AppendLine($"- status：`{aReader.GetString(SCP_LibraryIO.Key_Status, "unknown")}`　" +
                           $"期待度 {aReader.GetInt(SCP_LibraryIO.Key_Anticipation, 0)}／5");
            aSb.AppendLine($"- 讀到：`{(aProgress != null ? aProgress.GetString(SCP_LibraryIO.Key_CurrentChapterId, "未設定") : "未設定")}`　" +
                           $"最後閱讀：{(aProgress != null ? aProgress.GetString(SCP_LibraryIO.Key_LastRead, "未設定") : "未設定")}");
            aSb.AppendLine();
            aSb.AppendLine("## 🔖 書籤（上次寫到哪）");
            aSb.AppendLine();
            aSb.AppendLine(aProgress != null ? aProgress.GetString(SCP_LibraryIO.Key_BookmarkNote, "（無）") : "（無）");
            aSb.AppendLine();
            aSb.AppendLine("## 💭 目前看法");
            aSb.AppendLine();
            aSb.AppendLine(aReader.GetString(SCP_LibraryIO.Key_CurrentImpression, "（尚無）"));
            aSb.AppendLine();
            // 「作品與媒材」「書架投影」兩節 —— Python 版有、C# 初版漏（Sirius diff 抓到）。
            // 收斂規則是逐節點名補齊，不是整段照抄任一邊（兩版互有對方沒有的節）。
            aSb.AppendLine("## 🗂 作品與媒材");
            aSb.AppendLine();
            aSb.AppendLine($"- work_id: `{(string.IsNullOrEmpty(aWorkId) ? "unknown" : aWorkId)}`");
            if (aWork != null)
            {
                aSb.AppendLine($"- title: {aWork.GetString(SCP_LibraryIO.Key_Title, "（未登錄）")}");
                aSb.AppendLine($"- title_original: {aWork.GetString(SCP_LibraryIO.Key_TitleOriginal, "（未登錄）")}");
                aSb.AppendLine($"- author: {aWork.GetString(SCP_LibraryIO.Key_Author, "（未登錄）")}");
                SCP_JsonData? aTags = aWork.Contains(SCP_LibraryIO.Key_GenreTags)
                    ? aWork[SCP_LibraryIO.Key_GenreTags] : null;
                if (aTags != null && aTags.IsArray && aTags.Count > 0)
                {
                    var aTagList = new List<string>();
                    for (int i = 0; i < aTags.Count; i++) aTagList.Add(NodeToString(aTags[i]));
                    aSb.AppendLine($"- genre_tags: {string.Join(", ", aTagList)}");
                }
                else
                {
                    aSb.AppendLine("- genre_tags: （未登錄）");
                }
            }
            else
            {
                aSb.AppendLine("- （work.json 未登錄或讀取失敗 —— 只列 media 層資訊）");
            }
            aSb.AppendLine();
            aSb.AppendLine("## 🗄 書架投影");
            aSb.AppendLine();
            string aShelfPath = Path.Combine(SCP_LibraryStore.ReaderRoot(iDataRoot, iMediaId, iPersona),
                                             SCP_LibraryStore.BookshelfName);
            aSb.AppendLine(File.Exists(aShelfPath)
                ? File.ReadAllText(aShelfPath, Encoding.UTF8).TrimEnd()
                : "（無 bookshelf 投影）");
            aSb.AppendLine();
            aSb.AppendLine("## 📚 章節與 round");
            aSb.AppendLine();

            string aChaptersRoot = Path.Combine(SCP_LibraryStore.ReaderRoot(iDataRoot, iMediaId, iPersona),
                                                SCP_LibraryStore.ChaptersDirName);
            // ⛔ 沒有章節**不能提早 return** —— 人物觀點也要出現在追回檔裡
            //   （2026-08-06 Tim QA 指出的缺口：那時「沒讀過任何一章」會讓整個人物段消失）。
            if (!Directory.Exists(aChaptersRoot))
            {
                aSb.AppendLine("（尚無章節紀錄）");
                aSb.AppendLine();
                AppendCharacters(aSb, iDataRoot, iMediaId, iPersona);
                return aSb.ToString();
            }

            var aChapterDirs = new List<string>(Directory.GetDirectories(aChaptersRoot));
            aChapterDirs.Sort(StringComparer.Ordinal);
            foreach (string aDir in aChapterDirs)
            {
                string aId = Path.GetFileName(aDir);
                SCP_JsonData? aChapter = SCP_LibraryIO.LoadJson(
                    Path.Combine(aDir, SCP_LibraryStore.ChapterJsonName), out string? aChapterErr);
                if (aChapter == null)
                {
                    aSb.AppendLine($"### `{aId}`");
                    aSb.AppendLine($"> [!WARNING]");
                    aSb.AppendLine($"> {aChapterErr}");
                    aSb.AppendLine();
                    continue;
                }
                string aDisplay = aChapter.GetString(SCP_LibraryIO.Key_DisplayNumber, "");
                if (string.IsNullOrEmpty(aDisplay)) aDisplay = aId;   // display_number 缺 → 由 id 派生
                string aTimeRange = aChapter.GetString(SCP_LibraryIO.Key_TimeRange, "");
                aSb.AppendLine($"### {aDisplay}｜{aChapter.GetString(SCP_LibraryIO.Key_Title, "（未命名）")}" +
                               (string.IsNullOrEmpty(aTimeRange) ? "" : $"　`{aTimeRange}`"));
                SCP_JsonData? aRounds = aChapter.Contains(SCP_LibraryIO.Key_Rounds)
                    ? aChapter[SCP_LibraryIO.Key_Rounds] : null;
                if (aRounds == null || !aRounds.IsArray || aRounds.Count == 0)
                {
                    aSb.AppendLine("（尚無 round）");
                    aSb.AppendLine();
                    continue;
                }
                for (int i = 0; i < aRounds.Count; i++)
                {
                    SCP_JsonData aEntry = aRounds[i];
                    // legacy round 條目可能是**純字串檔名**（Python 舊格式；library.py 端也容忍）——
                    // 用物件 API 讀字串節點會拿到預設值，round 心得就靜默消失。
                    if (aEntry.IsString)
                    {
                        string aLegacyFile = aEntry.AsString();
                        aSb.AppendLine($"- **r?**（—）`{aLegacyFile}`　⚠ legacy 字串條目（無 round/日期欄）");
                        if (iFullRounds)
                        {
                            string aLegacyPath = Path.Combine(aDir, aLegacyFile);
                            aSb.AppendLine();
                            aSb.AppendLine(File.Exists(aLegacyPath)
                                ? File.ReadAllText(aLegacyPath, Encoding.UTF8).TrimEnd()
                                : $"> [!WARNING]\n> 索引指向的 round 檔不存在：`{aLegacyFile}`");
                            aSb.AppendLine();
                        }
                        continue;
                    }
                    string aFile = aEntry.GetString(SCP_LibraryIO.Key_File, "");
                    // ⚠ 場數一定要露出來（TASK-0121 ③）：讀的人要分得出「一話兩場」與「看了兩遍」——
                    //   不印的話，這兩件事在讀回視圖上長得一模一樣，而誤讀不會有任何一層報錯。
                    int aSegs = aEntry.GetInt(SCP_LibraryIO.Key_Segments, 1);
                    aSb.AppendLine($"- **r{aEntry.GetInt(SCP_LibraryIO.Key_Round, 0)}**（{aEntry.GetString(SCP_LibraryIO.Key_ReadingDate, "")}）" +
                                   $"`{aFile}`" +
                                   (aSegs > 1 ? $"　▸ 這一輪分 **{aSegs} 場**寫完（續寫，不是重看）" : "") +
                                   (aEntry.GetBool(SCP_LibraryIO.Key_Gap, false) ? "　⚠ gap" : "") +
                                   (aEntry.Contains(SCP_LibraryIO.Key_SharedSeq)
                                       ? $"　酒館 seq={aEntry.GetInt(SCP_LibraryIO.Key_SharedSeq, 0)}" : ""));
                    if (!iFullRounds) continue;
                    string aRoundPath = Path.Combine(aDir, aFile);
                    aSb.AppendLine();
                    aSb.AppendLine(File.Exists(aRoundPath)
                        ? File.ReadAllText(aRoundPath, Encoding.UTF8).TrimEnd()
                        : $"> [!WARNING]\n> 索引指向的 round 檔不存在：`{aFile}`");
                    aSb.AppendLine();
                }
                aSb.AppendLine();
            }

            AppendCharacters(aSb, iDataRoot, iMediaId, iPersona);
            return aSb.ToString();
        }

        // ===========================================================
        // 區塊職責：人物段 —— 已確認 facts（profile.json）與主觀 view 的版本史（vN_<date>.md）分開列。
        // 物理意義：續讀時最需要的兩件事是「這人是誰」與「我上次怎麼看他」；**看法要按版本並列**，
        //          因為改觀的演變本身就是閱讀體驗（不覆寫是本 schema 的核心不變量）。
        // 數值影響：純讀；缺 profile／版本檔一律留 WARNING，⛔ 不靜默略過。
        // ===========================================================
        static void AppendCharacters(StringBuilder ioSb, string iDataRoot, string iMediaId, string iPersona)
        {
            ioSb.AppendLine("## 🧑 人物（facts ＋ 我的看法版本史）");
            ioSb.AppendLine();

            string aCharactersRoot = Path.Combine(SCP_LibraryStore.ReaderRoot(iDataRoot, iMediaId, iPersona),
                                                  SCP_LibraryStore.CharactersDirName);
            if (!Directory.Exists(aCharactersRoot))
            {
                ioSb.AppendLine("（尚無人物紀錄）");
                ioSb.AppendLine();
                return;
            }

            var aCharacterDirs = new List<string>(Directory.GetDirectories(aCharactersRoot));
            aCharacterDirs.Sort(StringComparer.Ordinal);
            if (aCharacterDirs.Count == 0)
            {
                ioSb.AppendLine("（尚無人物紀錄）");
                ioSb.AppendLine();
                return;
            }

            foreach (string aDir in aCharacterDirs)
            {
                string aId = Path.GetFileName(aDir);
                SCP_JsonData? aProfile = SCP_LibraryIO.LoadJson(
                    Path.Combine(aDir, SCP_LibraryStore.ProfileJsonName), out string? aProfileErr);
                string aName = aProfile != null ? aProfile.GetString(SCP_LibraryIO.Key_Name, aId) : aId;
                ioSb.AppendLine($"### {aName}　`{aId}`");

                if (aProfile == null)
                {
                    ioSb.AppendLine("> [!WARNING]");
                    ioSb.AppendLine($"> {aProfileErr}");
                }
                else
                {
                    string aNameOriginal = aProfile.GetString(SCP_LibraryIO.Key_NameOriginal, "");
                    if (!string.IsNullOrEmpty(aNameOriginal)) ioSb.AppendLine($"- 原文讀音：{aNameOriginal}");
                    // facts 有兩種形狀：陣列（Python 時代寫的 legacy corpus）與字串（C# 初版寫的）。
                    // 🩸 舊碼用 GetString 讀 —— 對陣列節點回傳預設值 "" → 印「（未登錄）」**且無 warning**。
                    //   那是一個滿的、寫得很篤定的錯值：讀的人會以為自己真的沒登錄過
                    //   （Sirius 2026-08-07 用 dungeon 測資抓到，三個角色全中）。
                    var aFacts = ReadFactsList(aProfile);
                    if (aFacts.Count == 0)
                    {
                        ioSb.AppendLine("- **已確認 facts**：（未登錄）");
                    }
                    else
                    {
                        ioSb.AppendLine("- **已確認 facts**：");
                        foreach (string f in aFacts) ioSb.AppendLine($"  - {f}");
                    }
                }

                // view 版本史：v1 → vN 依檔名排序並列，**不只印最新版**
                var aViews = new List<string>(Directory.GetFiles(aDir, "v*.md"));
                aViews.Sort(StringComparer.Ordinal);
                if (aViews.Count == 0)
                {
                    ioSb.AppendLine("- （尚無主觀 view 版本）");
                    ioSb.AppendLine();
                    continue;
                }
                ioSb.AppendLine();
                foreach (string aViewPath in aViews)
                {
                    ioSb.AppendLine($"#### {Path.GetFileName(aViewPath)}");
                    ioSb.AppendLine(File.ReadAllText(aViewPath, Encoding.UTF8).TrimEnd());
                    ioSb.AppendLine();
                }
            }
        }

        // 區塊職責：讀 profile.json 的 facts —— 同時吃陣列與字串兩種形狀。
        // 物理意義：legacy corpus（Python 寫的）是 JSON 陣列；C# 初版寫成單一字串。
        //          schema 收斂方向是**陣列**（沿 corpus 多數），字串形狀讀入時按行拆開，
        //          兩種來源在視圖層長一樣 —— 讀端相容、寫端從此只寫陣列（見 FactsToJson）。
        public static List<string> ReadFactsList(SCP_JsonData? iProfile)
        {
            var aOut = new List<string>();
            if (iProfile == null || !iProfile.Contains(SCP_LibraryIO.Key_Facts)) return aOut;
            SCP_JsonData aFacts = iProfile[SCP_LibraryIO.Key_Facts];
            if (aFacts.IsArray)
            {
                for (int i = 0; i < aFacts.Count; i++)
                {
                    string s = NodeToString(aFacts[i]);
                    if (!string.IsNullOrEmpty(s)) aOut.Add(s);
                }
                return aOut;
            }
            string aRaw = NodeToString(aFacts);
            if (string.IsNullOrEmpty(aRaw)) return aOut;
            foreach (string aLine in aRaw.Split('\n'))
            {
                string t = aLine.Trim();
                if (t.Length > 0) aOut.Add(t);
            }
            return aOut;
        }

        // facts 寫入端的唯一出口：一律寫**陣列**（多行輸入按行拆）。
        // 🩸 字串與陣列兩種寫法並存就是那次「假滿值」的土壤 —— 寫端收斂成一種。
        public static SCP_JsonData FactsToJson(string? iFacts)
        {
            SCP_JsonData aArray = SCP_JsonData.NewArray();
            if (string.IsNullOrEmpty(iFacts)) return aArray;
            foreach (string aLine in iFacts!.Split('\n'))
            {
                string t = aLine.Trim();
                if (t.Length > 0) aArray.Add(t);
            }
            return aArray;
        }

        // 區塊職責：alias 條目轉字串 —— aliases 也有兩形狀（facts 同族病，2026-08-07 scan 實測抓到）：
        // mononoke 是字串陣列、arakawa 是物件陣列（{slug,source,note} / {title,note}）。
        // 🩸 GetString 對物件回空字串 → 物件形狀的 alias 被**靜默跳過**。
        public static string AliasToString(SCP_JsonData? iAlias)
        {
            if (iAlias == null) return "";
            if (iAlias.IsObject)
            {
                string t = iAlias.GetString(SCP_LibraryIO.Key_Title, "");
                if (string.IsNullOrEmpty(t)) t = iAlias.GetString("slug", "");
                return t;
            }
            return NodeToString(iAlias);
        }

        /// <summary>
        /// 純量節點 → 字串。⚠ 這是 UCL 端 `JsonData.GetString()`（無參版）的對應物，而兩邊語意不同：
        /// UCL 那版對任何節點都回一個字串，本層的 <c>AsString()</c> 對非字串節點**會丟例外**。
        /// ⛔ 所以不可以直接呼叫 —— 非字串一律回空字串，與 UCL 端的行為對齊。
        /// </summary>
        static string NodeToString(SCP_JsonData? iNode)
        {
            if (iNode == null || !iNode.Exists || iNode.IsNull) return "";
            return iNode.IsString ? iNode.AsString() : "";
        }

        // ===========================================================
        // 區塊職責：把追回檔寫進該 persona 自己的 letters/cmd/ —— 與其他 Cmd 回傳檔同一個家。
        // 物理意義：落點 `cmd/reading_recall_<media-id>.md`（`SCP_LettersPaths.CmdPayload` 是版面唯一實作）。
        //          🩸 原本平鋪在 letters 頂層、與人寫的信混住 —— 那正是 Cmd_DocEdit
        //          「找最新那封信」抓到機器產物的病灶。
        // 數值影響：每次**完整覆寫**；原始章節與人物歷史不受影響。回傳寫出的絕對路徑。
        // ⚠ 渲染失敗（讀不到 reader.json）⇒ 回 null 且**不寫檔** —— 不留一份空的追回檔在那裡，
        //   那會讓「沒讀過」與「渲染壞了」同形。
        // ===========================================================
        public static string? WriteRecallBrief(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                               string iMediaId, string iPersona, bool iFullRounds,
                                               out string? oError)
        {
            string? aText = RenderRecall(iDataRoot, iMediaId, iPersona, iFullRounds, out oError);
            if (aText == null) return null;
            string aPath = SCP_LettersPaths.CmdPayload(iLettersRoot, iPersona, "reading_recall", iMediaId);
            SCP_LibraryIO.SaveText(aPath, aText);   // 父目錄自動建立
            return aPath;
        }
    }
}
