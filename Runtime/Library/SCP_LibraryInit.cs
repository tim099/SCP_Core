// 區塊職責：閱讀庫的**建檔層** —— work.json（作品）／media.json（媒材）／reader.json（讀者）三層落檔。
// 物理意義：三層是有方向的：一部作品可以有多種媒材（漫畫／動畫／小說），一種媒材可以有多位讀者。
//           建檔一律**不覆寫既有檔** —— 重跑建檔不該蓋掉任何人已經累積的進度。
// 數值影響：只在缺檔時寫；已存在的檔**一個位元組都不動**（⛔ 不補欄、不升版）。
// 🩸 為什麼這一叢不能單獨搬（2026-09-16 逐支量呼叫端）：`EnsureReaderJson` 的最後一行呼叫
//   `SCP_LibraryBookshelf.SyncBookshelf` ⇒ 少了它，落地是「reader.json 建得出來、回 ✅，
//   **而閱讀卡停在上一次**」—— 每一層都綠的失效。⇒ 那一叢先搬（`57f3b91`），本檔才動得了。
// ⚠ 而它**到此為止**：本檔 ⛔ 不碰 `RenderRecall`（追回檔那條的入口是 `WriteRecallBrief`，
//   由 NoteChapter／AddCharacter／ReviseView／Bookmark 各自呼叫，與本檔平行、不串接）。
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Library
{
    public static class SCP_LibraryInit
    {
        // ===========================================================
        // 區塊職責：確保這部作品在新 store 有一份 work.json。
        // 物理意義：建新作品與「從舊 store 遷移」兩條路都會需要它，而它們建出來的 schema
        //          **必須逐欄相同** —— 各寫一份的話，兩種來源的 work.json 會慢慢長歪，
        //   🩸 而那個漂移**不會報錯**：讀取端 `GetString(key, "")` 對缺欄回空字串，
        //     於是「這本沒填」與「這條路徑沒寫這欄」同形。
        // 數值影響：檔案不存在 ⇒ 建一份；存在 ⇒ 一個位元組都不動（⛔ 不補欄、不升版）。
        // ===========================================================
        public static string EnsureWorkJson(string iDataRoot, string iWorkId, string iTitle,
                                            string? iTitleOriginal, string? iAuthor,
                                            IList<string>? iAliases, IList<string>? iGenreTags)
        {
            string aWorkPath = SCP_LibraryStore.WorkJsonPath(iDataRoot, iWorkId);
            if (File.Exists(aWorkPath)) return $"- work.json 已存在，不覆寫：`{iWorkId}`\n";

            SCP_JsonData aWork = BuildWorkJson(iWorkId, iTitle, iTitleOriginal, iAuthor, iAliases, iGenreTags);
            SCP_LibraryIO.SaveJson(aWorkPath, aWork);
            return $"- ✅ 建立 work.json：`{iWorkId}`《{iTitle}》（aliases {aWork[SCP_LibraryIO.Key_Aliases].Count} 筆）\n";
        }

        // ===========================================================
        // 區塊職責：work.json 的**內容**（純函式，零寫入）。
        // 🩸 為什麼要從 `EnsureWorkJson` 裡拆出來（TASK-0166）：那一支唯一的出口是寫檔 ⇒
        //   「它產出什麼形狀」只能靠先寫進真的資料樹再讀回來才驗得到，而那是**用寫入端驗寫入端**。
        //   拆出純函式之後，磁碟上既有的 work.json 才變成得了對照組。
        // ===========================================================
        public static SCP_JsonData BuildWorkJson(string iWorkId, string iTitle, string? iTitleOriginal,
                                                 string? iAuthor, IList<string>? iAliases,
                                                 IList<string>? iGenreTags)
        {
            SCP_JsonData aWork = SCP_JsonData.NewObject();
            aWork[SCP_LibraryIO.Key_WorkId] = iWorkId;
            aWork[SCP_LibraryIO.Key_Title] = iTitle;
            aWork[SCP_LibraryIO.Key_TitleOriginal] = iTitleOriginal ?? "";
            aWork[SCP_LibraryIO.Key_Author] = iAuthor ?? "";
            // 區塊職責：aliases 是**日後搜尋的唯一入口**（中／日／英 ＋ 常見異譯）。
            // 物理意義：搜尋比對打的是 title / title_original / aliases 三欄；
            //   🩸 漏建 alias 的後果不是「找不到」，是「找不到 → 有人再建一本」
            //     （arakawa 雙 entry 的成因，2026-08-05 實測 101 本裡有四組重複）。
            // 數值影響：純 metadata；不影響進度與章節。
            aWork[SCP_LibraryIO.Key_Aliases] = SCP_LibraryIO.ToStringArray(iAliases, iTitle, iTitleOriginal ?? "");
            aWork[SCP_LibraryIO.Key_GenreTags] = SCP_LibraryIO.ToStringArray(iGenreTags);
            aWork[SCP_LibraryIO.Key_SchemaVersion] = 1;
            return aWork;
        }

        /// <summary>media.json 的內容（純函式，零寫入）—— 拆出來的理由同 <see cref="BuildWorkJson"/>。</summary>
        public static SCP_JsonData BuildMediaJson(string iMediaId, string iWorkId, string iMediaKind)
        {
            SCP_JsonData aMedia = SCP_JsonData.NewObject();
            aMedia[SCP_LibraryIO.Key_MediaId] = iMediaId;
            aMedia[SCP_LibraryIO.Key_WorkId] = iWorkId;
            aMedia[SCP_LibraryIO.Key_MediaKind] = iMediaKind;
            aMedia[SCP_LibraryIO.Key_SchemaVersion] = 1;
            return aMedia;
        }

        /// <summary>
        /// reader.json 的**初值**（純函式，零寫入）—— 拆出來的理由同 <see cref="BuildWorkJson"/>。
        /// ⚠ 這是「剛建好」那一刻的形狀；讀過幾章之後磁碟上那份會長得不一樣，⛔ 別拿它去驗活的 reader。
        /// </summary>
        public static SCP_JsonData BuildReaderJson(string iMediaId, string iPersona, int iAnticipation,
                                                   string iToday)
        {
            SCP_JsonData aReader = SCP_JsonData.NewObject();
            aReader[SCP_LibraryIO.Key_SchemaVersion] = 2;
            aReader[SCP_LibraryIO.Key_ReaderPersona] = iPersona;
            aReader[SCP_LibraryIO.Key_MediaId] = iMediaId;
            aReader[SCP_LibraryIO.Key_Status] = "reading";
            aReader[SCP_LibraryIO.Key_Anticipation] = iAnticipation;
            aReader[SCP_LibraryIO.Key_ReadingStartedAt] = iToday;
            SCP_JsonData aProgress = SCP_JsonData.NewObject();
            aProgress[SCP_LibraryIO.Key_CurrentChapterId] = "";
            aProgress[SCP_LibraryIO.Key_LastRead] = iToday;
            aProgress[SCP_LibraryIO.Key_BookmarkNote] = "（尚未開始）";
            aReader[SCP_LibraryIO.Key_Progress] = aProgress;
            aReader[SCP_LibraryIO.Key_CurrentImpression] = "（尚未寫下第一筆心得）";
            aReader[SCP_LibraryIO.Key_UpdatedAt] = iToday;
            return aReader;
        }

        // ===========================================================
        // 建檔（op=media_init）—— work / media / reader 三層一次到位。
        // 數值影響：已存在的檔**不覆寫**；建檔重跑不該蓋掉既有進度。
        // ⚠ media.json 已存在而 work_id 不同 ⇒ **停下來報錯**，⛔ 不改寫也不「兩邊都記」：
        //   同一個 media id 指向兩個作品是**身分層**錯誤，猜錯的代價是把兩本書的進度混在一起。
        // ===========================================================
        public static string MediaInit(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                       string iWorkId, string iMediaId, string iMediaKind, string iPersona,
                                       string iTitle, string? iTitleOriginal, string? iAuthor,
                                       int iAnticipation, IList<string>? iAliases, IList<string>? iGenreTags,
                                       out string? oError)
        {
            oError = null;
            var aLog = new StringBuilder();

            aLog.Append(EnsureWorkJson(iDataRoot, iWorkId, iTitle, iTitleOriginal, iAuthor, iAliases, iGenreTags));

            string aMediaPath = SCP_LibraryStore.MediaJsonPath(iDataRoot, iMediaId);
            if (File.Exists(aMediaPath))
            {
                SCP_JsonData? aExisting = SCP_LibraryIO.LoadJson(aMediaPath, out string? aMediaErr);
                if (aExisting == null) { oError = aMediaErr; return aLog.ToString(); }
                string aExistingWork = aExisting.GetString(SCP_LibraryIO.Key_WorkId, "");
                if (aExistingWork != iWorkId)
                {
                    oError = $"media.json 已存在且 {SCP_LibraryIO.Key_WorkId}={aExistingWork}，與請求 {iWorkId} 不符 —— " +
                             "同一 media id 指向兩個作品是身分層錯誤，請改用不同 media_id 或先確認哪個才對";
                    return aLog.ToString();
                }
                aLog.AppendLine($"- media.json 已存在，不覆寫：`{iMediaId}`");
            }
            else
            {
                SCP_LibraryIO.SaveJson(aMediaPath, BuildMediaJson(iMediaId, iWorkId, iMediaKind));
                aLog.AppendLine($"- ✅ 建立 media.json：`{iMediaId}`（{iMediaKind}）");
            }

            aLog.Append(EnsureReaderJson(iLettersRoot, iDataRoot, iMediaId, iPersona, iAnticipation, out _));

            return aLog.ToString();
        }

        // ===========================================================
        // 區塊職責：**建 reader.json（不存在才建）** —— `MediaInit` 與 `RegisterReader` 共用這一份。
        // 物理意義：reader.json 是「這個人在看這部」這件事的落檔，schema 只准有一種形狀。
        // 數值影響：已存在 ⇒ **零寫入**（既有進度一個位元組都不動），`oCreated=false`。
        // 🩸 為什麼抽出來：TASK-0137 要在進場時也能登記，而「再寫一次同樣的初值」＝ 第二份 schema。
        //   兩份初值長得一樣時不會有人發現它們已經分岔（少一個欄位的 reader 讀回來也「正常」）。
        // ⚠ 建完**必定同步閱讀卡** —— 那不是順手加的，是本支的一部分（見檔頭血證）。
        // ===========================================================
        public static string EnsureReaderJson(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                              string iMediaId, string iPersona, int iAnticipation,
                                              out bool oCreated)
        {
            oCreated = false;
            string aReaderPath = SCP_LibraryStore.ReaderJsonPath(iDataRoot, iMediaId, iPersona);
            // ⚠ 這兩個 return 用 `Environment.NewLine`（CRLF），而同檔的 `EnsureWorkJson` 用 `\n` ——
            //   **原版自己就不一致**，而移植要保原樣，⛔ 不趁機統一：
            //   這些字串會併進 Cmd 的回傳檔，統一它等於讓回傳檔的版面在移植這一刀悄悄改變，
            //   而那個改變跟「移植寫錯了」在 diff 上同形。要正規化是另一個決定。
            if (File.Exists(aReaderPath))
                return $"- reader.json 已存在，不覆寫：`{iPersona}`（既有進度保留）" + System.Environment.NewLine;

            SCP_LibraryIO.SaveJson(aReaderPath,
                BuildReaderJson(iMediaId, iPersona, iAnticipation, SCP_LibraryIO.Today()));
            SCP_LibraryBookshelf.SyncBookshelf(iLettersRoot, iDataRoot, iMediaId, iPersona, out _, out _);
            oCreated = true;
            return $"- ✅ 建立 reader.json：`{iPersona}`（期待度 {iAnticipation}／5）" + System.Environment.NewLine;
        }

        // ===========================================================
        // 區塊職責：把 persona 登記成**既有 media** 的 reader（進場即註冊）——
        //          給觀影／閱讀流程在「我要開始看這部」的那一刻呼叫，⛔ 不是給建新作品用的。
        // 物理意義：reader 的語意是「這個人在看這部」，而那件事發生在**進場**，不是收工。
        //   🩸 TASK-0137（summit 2026-09-05）：第一次陪看某作品的人，收工回傳檔叫他寫接續點，
        //   而 `note_chapter`／`bookmark`／`recall` 三支的前置都是 reader.json ⇒ 三支全失敗。
        //   場次帳 exit 0、+10 token、公告照發，記憶帳是空的 —— **兩本帳分開結算，而空的那本不會叫**。
        // 數值影響：
        //   · media.json **不存在 ⇒ 什麼都不建**、回 false 並給 error。
        //     ⛔ 不從 media_id 反推 work/title 去補建 —— 那是替作品層捏身分，而它「看起來會很正常」。
        //   · reader.json 已存在 ⇒ 零寫入、`oCreated=false`、回 true（冪等，進場每次呼叫都安全）。
        //   · anticipation 預設 3（中性）—— 進場時本人還沒讀，工具**不替他表態**；他自己改。
        // ===========================================================
        public static bool RegisterReader(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                          string iMediaId, string iPersona,
                                          out bool oCreated, out string oLog, out string? oError)
        {
            oCreated = false; oLog = ""; oError = null;
            if (string.IsNullOrEmpty(iMediaId) || string.IsNullOrEmpty(iPersona))
            {
                oError = "RegisterReader：media_id 與 persona 都必填";
                return false;
            }
            string aMediaPath = SCP_LibraryStore.MediaJsonPath(iDataRoot, iMediaId);
            if (!File.Exists(aMediaPath))
            {
                oError = $"media.json 不存在：{aMediaPath} —— 這部作品還沒進閱讀庫，" +
                         "⇒ 先走 `Library op=media_init`（那一支才會建 work／media 層）";
                return false;
            }
            oLog = EnsureReaderJson(iLettersRoot, iDataRoot, iMediaId, iPersona, 3, out oCreated);
            return true;
        }

        /// <summary>章節關係 → 人讀標籤。⚠ 認不得的值回列舉名本身，⛔ 不回空字串（那會讓它從畫面上消失）。</summary>
        public static string RelationLabel(SCP_ChapterRelation iRelation)
        {
            switch (iRelation)
            {
                case SCP_ChapterRelation.FirstEver: return "首筆紀錄";
                case SCP_ChapterRelation.Reread: return "重讀同章 → 開新 round，舊 round 保留";
                case SCP_ChapterRelation.Next: return "續讀（+1）";
                case SCP_ChapterRelation.Gap: return "⚠ 跳章（已在 chapter.json 記 gap，未靜默）";
                case SCP_ChapterRelation.Prologue: return "序章（不參與連續性判定）";
            }
            return iRelation.ToString();
        }
    }
}
