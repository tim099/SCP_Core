// 區塊職責：落一筆章節心得（op=note_chapter）—— 本模組**唯一**會產生新內容的寫入端。
// 物理意義：三層各有各的角色：round md 是**事實源**、chapter.json 是 round 的**索引**、
//           reader.json 是**當前狀態**。三者不可互相頂替。
// 數值影響：既有 round **絕不覆寫** —— 同章再寫一次就開下一個 r{N}。
//           `append=true` 是**唯一**的例外，而它是**追加不是覆寫**：正文接在既有 round 檔尾端，
//           原本的字一個都不動，`segments` +1。
// 🩸 TASK-0121 為什麼要有續寫這條路（拍板：走 code 補續寫，⛔ 不改 skill 的字）：
//   「一話一 round，場次中斷續寫同一個 round；r2 只留給真正的重看」是 skill 早就寫著的規則，
//   而 code 這邊沒有任何參數表達得出「續寫」⇒ 同一話的第二場照樣開 r2。
//   兩份規則各自都對，落地結果相反，而失效是**靜默**的：r2 落地回「✓ 成功」，
//   chapter.json 也長得完全正常。⇒ 收斂成一份，收斂點放在 code
//   （改 skill 的字要把「r2＝重看」這個既有語意永久放棄掉，那筆帳更貴）。
// ⚠ 本檔是 `RenderRecall` 的**上游**：每次寫入之後一定重生成追回檔
//   —— stale 投影比沒有投影更糟（下次續讀撈到的會是上一次的視圖，而它看起來完全正常）。
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Library
{
    public static class SCP_LibraryNote
    {
        public static string? NoteChapter(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                          string iMediaId, string iPersona, string iChapterId,
                                          string? iDisplayNumber, string? iChapterTitle, string? iTimeRange,
                                          string iBody, string? iImpression, string? iBookmarkNote,
                                          bool iAppend, int iAppendRound,
                                          out string? oRoundFilePath, out int oRoundNumber, out string? oError)
        {
            oRoundFilePath = null;
            oRoundNumber = 0;

            SCP_JsonData? aReader = SCP_LibraryIO.LoadReader(iDataRoot, iMediaId, iPersona, out oError);
            if (aReader == null)
            {
                // 前置階梯（Tim 2026-08-06）：沒有自己的紀錄 → **停下來**，⛔ 不自作主張建檔。
                //   替他建一份空的 reader，會讓「他還沒開始讀」變成「他讀過而什麼都沒寫」。
                oError = $"{oError}\n" +
                         "→ 這位 persona 在此 media 尚無新架構紀錄。依定案流程：" +
                         "① 若 Archive 有舊心得 → 先跑 migration 手動搬到新架構；" +
                         "② 若查無舊心得 → 先跑 op=media_init 建檔；" +
                         "③ 若要接力別人的心得 → 由 Tim 指定來源 persona（讀可跨 persona，寫只寫自己）。";
                return null;
            }

            SCP_ChapterRelation aRelation = SCP_LibraryIO.ClassifyChapter(aReader, iChapterId);
            string aChapterDir = SCP_LibraryStore.ChapterDir(iDataRoot, iMediaId, iPersona, iChapterId);
            string aChapterJsonPath = Path.Combine(aChapterDir, SCP_LibraryStore.ChapterJsonName);

            bool aChapterExists = File.Exists(aChapterJsonPath);
            SCP_JsonData? aChapter = aChapterExists
                ? SCP_LibraryIO.LoadJson(aChapterJsonPath, out oError) : null;
            if (aChapterExists && aChapter == null) return null;   // ⛔ 壞檔不覆蓋

            if (aChapter == null)
            {
                aChapter = SCP_JsonData.NewObject();
                aChapter[SCP_LibraryIO.Key_ChapterId] = iChapterId;
                // display_number 是**投影**：沒給人話字面就留空，由顯示端派生 ——
                // ⛔ 不再手填成 id 複寫（basecamp 2026-08-06 量到既有樣本已退化成 display_number == chapter_id）。
                aChapter[SCP_LibraryIO.Key_DisplayNumber] = iDisplayNumber ?? "";
                aChapter[SCP_LibraryIO.Key_Title] = iChapterTitle ?? "";
                if (!string.IsNullOrEmpty(iTimeRange)) aChapter[SCP_LibraryIO.Key_TimeRange] = iTimeRange!;
                aChapter[SCP_LibraryIO.Key_Rounds] = SCP_JsonData.NewArray();
                aChapter[SCP_LibraryIO.Key_SchemaVersion] = 2;
            }
            else
            {
                if (!string.IsNullOrEmpty(iDisplayNumber)) aChapter[SCP_LibraryIO.Key_DisplayNumber] = iDisplayNumber!;
                if (!string.IsNullOrEmpty(iChapterTitle)) aChapter[SCP_LibraryIO.Key_Title] = iChapterTitle!;
                // ⚠ 續寫時章層的 time_range 是**接上去**，不是蓋掉、也不是留著第一段就算了：
                //   那一格是「這一話」的時間段，而續寫帶進來的是「這一場」的。
                //   蓋掉 ⇒ 第一場的區間消失，而消失的樣子跟「本來就只有這一段」一模一樣；
                //   留著不動 ⇒ 一話跑到 52:00 而章層寫著 00:00-30:00，那是一個**看起來完整**的錯讀數。
                //   🩸 這一格是讀探針落盤的檔才看到的（2026-09-05）—— 工具的回讀沒有講它。
                //   ⇒ 逐場列出來，兩段都在：`00:00-30:00, 30:00-52:00`。
                if (!string.IsNullOrEmpty(iTimeRange))
                {
                    string aExistingRange = aChapter.GetString(SCP_LibraryIO.Key_TimeRange, "");
                    aChapter[SCP_LibraryIO.Key_TimeRange] =
                        iAppend && aExistingRange.Length > 0 && !aExistingRange.Contains(iTimeRange!)
                            ? $"{aExistingRange}, {iTimeRange}"
                            : iTimeRange!;
                }
            }

            SCP_JsonData aRounds = aChapter.Contains(SCP_LibraryIO.Key_Rounds)
                ? aChapter[SCP_LibraryIO.Key_Rounds] : SCP_JsonData.NewArray();
            if (!aRounds.IsArray)
            {
                aRounds = SCP_JsonData.NewArray();
                aChapter[SCP_LibraryIO.Key_Rounds] = aRounds;
            }
            else if (!aChapter.Contains(SCP_LibraryIO.Key_Rounds))
            {
                aChapter[SCP_LibraryIO.Key_Rounds] = aRounds;
            }

            // round 編號 ＝ 既有最大值 + 1（⛔ 不看檔案數 —— 檔可能被人另外加，**索引才是真相源**）
            int aMaxRound = 0;
            for (int i = 0; i < aRounds.Count; i++)
            {
                if (aRounds[i].IsString) continue;   // legacy 純字串條目沒有 round 欄
                int n = aRounds[i].GetInt(SCP_LibraryIO.Key_Round, 0);
                if (n > aMaxRound) aMaxRound = n;
            }
            oRoundNumber = aMaxRound + 1;

            // 區塊職責：接回「round 與索引已落檔、reader 投影尚未落檔」的中斷寫入。
            // 物理意義：同日完全相同的 r1 已是這次閱讀的事實源；再開 r2 會把 I/O 中斷誤記成重讀。
            // 數值影響：只在 reader 尚未指向本章、索引僅含當日 r1、且正文逐字相同時重用 r1；
            //           其他任何情形仍照既有規則開新 round，避免吞掉真正的重讀。
            bool aRecoverPendingProjection = false;
            string aRecoveredFileName = "";
            if (!iAppend && aRounds.Count == 1 && !aRounds[0].IsString &&
                aRounds[0].GetInt(SCP_LibraryIO.Key_Round, 0) == 1 &&
                aRounds[0].GetString(SCP_LibraryIO.Key_ReadingDate, "") == SCP_LibraryIO.Today())
            {
                string aCurrentChapter = aReader.Contains(SCP_LibraryIO.Key_Progress)
                    ? aReader[SCP_LibraryIO.Key_Progress].GetString(SCP_LibraryIO.Key_CurrentChapterId, "") : "";
                string aCandidateFile = aRounds[0].GetString(SCP_LibraryIO.Key_File, "");
                string aCandidatePath = Path.Combine(aChapterDir, aCandidateFile);
                if (aCurrentChapter != iChapterId && !string.IsNullOrEmpty(aCandidateFile) &&
                    File.Exists(aCandidatePath) &&
                    string.Equals(File.ReadAllText(aCandidatePath, Encoding.UTF8).TrimEnd(), iBody.TrimEnd(),
                        System.StringComparison.Ordinal))
                {
                    aRecoverPendingProjection = true;
                    aRecoveredFileName = aCandidateFile;
                    oRoundNumber = 1;
                    oRoundFilePath = aCandidatePath;
                }
            }

            // ── 續寫（TASK-0121）：追加進既有 round，不開下一個 r{N} ──────────────
            // ⚠ 這一段是**唯一**會動到既有 round 檔的路，所以三件事都要說出來而不是靜默處理：
            //   ① 指定的 round 不在索引裡　② 索引指的檔在磁碟上不見了　③ 這一章根本還沒有第一場。
            //   前兩者**拒絕寫入**（磁碟與索引不一致要人先看一眼）；③ 不是錯，它就是第一場 ⇒ 照常開 r1。
            bool aAppended = false;
            int aSegmentCount = 1;
            string aFileName;
            if (aRecoverPendingProjection)
            {
                aFileName = aRecoveredFileName;
            }
            else if (iAppend && aMaxRound > 0)
            {
                int aTarget = iAppendRound > 0 ? iAppendRound : aMaxRound;
                SCP_JsonData? aTargetEntry = null;
                for (int i = 0; i < aRounds.Count; i++)
                    if (!aRounds[i].IsString && aRounds[i].GetInt(SCP_LibraryIO.Key_Round, 0) == aTarget)
                        aTargetEntry = aRounds[i];
                if (aTargetEntry == null)
                {
                    oError = $"要續寫的 r{aTarget} 不在 chapter.json 索引裡（現有最大 r{aMaxRound}）—— " +
                             "拒絕寫入，索引說沒有的東西不該由工具生出來";
                    return null;
                }

                string aTargetFile = aTargetEntry.GetString(SCP_LibraryIO.Key_File, "");
                string aTargetPath = Path.Combine(aChapterDir, aTargetFile);
                if (string.IsNullOrEmpty(aTargetFile) || !File.Exists(aTargetPath))
                {
                    oError = $"r{aTarget} 的索引指向 `{aTargetFile}`，而磁碟上沒有這個檔 —— " +
                             "拒絕續寫（索引與磁碟不一致要人先看一眼，不該由工具猜）";
                    return null;
                }

                aSegmentCount = aTargetEntry.GetInt(SCP_LibraryIO.Key_Segments, 1) + 1;
                string aHead = $"## 續寫・第 {aSegmentCount} 場（{SCP_LibraryIO.Today()}"
                               + (string.IsNullOrEmpty(iTimeRange) ? "" : $"　{iTimeRange}") + "）";
                // 追加**不覆寫**：先讀既有內容再整份寫回（SaveText 是全檔寫入）。
                string aExisting = File.ReadAllText(aTargetPath, Encoding.UTF8).TrimEnd();
                SCP_LibraryIO.SaveText(aTargetPath, $"{aExisting}\n\n---\n\n{aHead}\n\n{iBody.TrimEnd()}\n");

                aTargetEntry[SCP_LibraryIO.Key_Segments] = aSegmentCount;
                oRoundNumber = aTarget;
                aFileName = aTargetFile;
                oRoundFilePath = aTargetPath;
                aAppended = true;
                SCP_LibraryIO.SaveJson(aChapterJsonPath, aChapter);
            }
            else
            {
                aFileName = $"r{oRoundNumber}_{SCP_LibraryIO.Today()}.md";
                oRoundFilePath = Path.Combine(aChapterDir, aFileName);
                if (File.Exists(oRoundFilePath))
                {
                    oError = $"round 檔已存在但不在 chapter.json 索引內：{aFileName} —— " +
                             "拒絕覆寫（索引與磁碟不一致要人先看一眼，不該由工具猜）";
                    return null;
                }

                SCP_LibraryIO.SaveText(oRoundFilePath, iBody.TrimEnd() + "\n");

                SCP_JsonData aEntry = SCP_JsonData.NewObject();
                aEntry[SCP_LibraryIO.Key_Round] = oRoundNumber;
                aEntry[SCP_LibraryIO.Key_ReadingDate] = SCP_LibraryIO.Today();
                aEntry[SCP_LibraryIO.Key_File] = aFileName;
                // 跳章**不擋，但留痕** —— 擋它等於要求人按順序讀，而那不是工具的判定。
                if (aRelation == SCP_ChapterRelation.Gap) aEntry[SCP_LibraryIO.Key_Gap] = true;
                aRounds.Add(aEntry);
                SCP_LibraryIO.SaveJson(aChapterJsonPath, aChapter);
            }

            // reader.json 當前狀態
            SCP_JsonData aProgress = aReader.Contains(SCP_LibraryIO.Key_Progress)
                ? aReader[SCP_LibraryIO.Key_Progress] : SCP_JsonData.NewObject();
            if (!aProgress.IsObject)
            {
                aProgress = SCP_JsonData.NewObject();
                aReader[SCP_LibraryIO.Key_Progress] = aProgress;
            }
            else if (!aReader.Contains(SCP_LibraryIO.Key_Progress))
            {
                aReader[SCP_LibraryIO.Key_Progress] = aProgress;
            }
            aProgress[SCP_LibraryIO.Key_CurrentChapterId] = iChapterId;
            aProgress[SCP_LibraryIO.Key_LastRead] = SCP_LibraryIO.Today();
            if (!string.IsNullOrEmpty(iBookmarkNote)) aProgress[SCP_LibraryIO.Key_BookmarkNote] = iBookmarkNote!;
            if (!string.IsNullOrEmpty(iImpression)) aReader[SCP_LibraryIO.Key_CurrentImpression] = iImpression!;
            aReader[SCP_LibraryIO.Key_UpdatedAt] = SCP_LibraryIO.Today();
            SCP_LibraryIO.SaveJson(SCP_LibraryStore.ReaderJsonPath(iDataRoot, iMediaId, iPersona), aReader);

            SCP_LibraryBookshelf.SyncBookshelf(iLettersRoot, iDataRoot, iMediaId, iPersona, out _, out _);
            // 每次寫入後重生成追回檔 —— 否則下次續讀撈到的是上一次的視圖（**stale 投影比沒有投影更糟**）。
            SCP_LibraryRecall.WriteRecallBrief(iLettersRoot, iDataRoot, iMediaId, iPersona, true, out _);

            var aLog = new StringBuilder();
            aLog.AppendLine($"- 章節：`{iChapterId}`" +
                            (string.IsNullOrEmpty(iChapterTitle) ? "" : $"　{iChapterTitle}") +
                            (string.IsNullOrEmpty(iTimeRange) ? "" : $"　（{iTimeRange}）"));
            // ⚠ 續寫時**不印** RelationLabel：那句話回答的是「這一章跟上次讀到哪的關係」，
            //   而續寫的答案永遠是「同一章」—— 印出來會變成一句永遠成立、因此不帶資訊的話。
            aLog.AppendLine(aRecoverPendingProjection
                ? $"- round：**r{oRoundNumber}**（恢復先前中斷的 reader 投影；沒有開新的 round）"
                : aAppended
                ? $"- round：**r{oRoundNumber}**（續寫・第 {aSegmentCount} 場 —— **沒有開新的 round**；" +
                  "`r{N}` 是第 N 次讀這一話，不是第 N 次寫入）"
                : $"- round：**r{oRoundNumber}**（{SCP_LibraryInit.RelationLabel(aRelation)}）");
            aLog.AppendLine($"- 心得檔：`{aFileName}`" + (aAppended ? "（追加在尾端，既有內容未動）" : ""));
            return aLog.ToString();
        }
    }
}
