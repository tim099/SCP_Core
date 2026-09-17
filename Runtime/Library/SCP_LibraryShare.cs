using System;
using System.IO;
using System.Text;
using SCP.Core.Json;

// 區塊職責：把一筆章節心得組成「可以貼進酒館」的內文（`op=share`），以及把發文回來的 seq 落回索引。
// 物理意義：`UCL_ReadingLibraryIO` 的 share 那一叢移進 SCP_Core（TASK-0166 ①，Tim 2026-09-17 拍板全搬）。
//          **round 檔是事實源，酒館貼文是投影** —— 本層只讀不寫（`RecordSharedSeq` 除外，它落 receipt）；
//          發文成敗都不回滾心得檔（檔優先於投影）。
// 數值影響：`iRoundNumber<=0` ⇒ 取該章最大 round 並回填；**已有 `shared_seq` 的 round 直接拒絕**
//          （同一則心得重發會重複計酬，與「同一個 SHA 重貼一次」同型）。
// ⛔ 本層不發文：發文是宿主那一側的事（Cmd 走 Tavern pipeline、CLI 走它自己的路）。
//    這裡只回「要貼什麼」與「貼完之後把 seq 記回哪」——⇒ 兩個入口共用同一份內文組法。
namespace SCP.Core.Library
{
    public static class SCP_LibraryShare
    {
        /// <summary>組一則「章節心得 → 酒館」的發文內文。讀不動／已發過 ⇒ 回 null ＋ oError。</summary>
        public static string? BuildShareBody(string iDataRoot, string iMediaId, string iPersona, string iChapterId,
                                             ref int ioRoundNumber, out string? oError)
        {
            oError = null;
            if (SCP_LibraryIO.LoadReader(iDataRoot, iMediaId, iPersona, out oError) == null) return null;

            string aChapterDir = SCP_LibraryStore.ChapterDir(iDataRoot, iMediaId, iPersona, iChapterId);
            SCP_JsonData? aChapter = SCP_LibraryIO.LoadJson(
                Path.Combine(aChapterDir, SCP_LibraryStore.ChapterJsonName), out oError);
            if (aChapter == null) return null;

            SCP_JsonData? aRounds = aChapter.Contains(SCP_LibraryIO.Key_Rounds)
                ? aChapter[SCP_LibraryIO.Key_Rounds] : null;
            if (aRounds == null || !aRounds.IsArray || aRounds.Count == 0)
            {
                oError = "chapter.json 缺 rounds —— 先 note_chapter 再 share";
                return null;
            }

            SCP_JsonData? aHit = null, aMaxEntry = null;
            int aMaxRound = 0;
            for (int i = 0; i < aRounds.Count; i++)
            {
                SCP_JsonData aEntry = aRounds[i];
                if (aEntry == null || aEntry.IsString) continue;   // legacy 字串條目沒有 round 號可對
                int aRn = aEntry.GetInt(SCP_LibraryIO.Key_Round, 0);
                if (aRn > aMaxRound) { aMaxRound = aRn; aMaxEntry = aEntry; }
                if (ioRoundNumber > 0 && aRn == ioRoundNumber) aHit = aEntry;
            }
            if (ioRoundNumber <= 0) { aHit = aMaxEntry; ioRoundNumber = aMaxRound; }
            if (aHit == null)
            {
                oError = $"找不到 round {ioRoundNumber}（該章最大 round = {aMaxRound}）";
                return null;
            }
            if (aHit.Contains(SCP_LibraryIO.Key_SharedSeq))
            {
                oError = $"round {ioRoundNumber} 已發過（seq={aHit.GetInt(SCP_LibraryIO.Key_SharedSeq, 0)}）—— " +
                         "重發會重複領發文計酬；真要重發請先人工清掉該 round 的 shared_seq";
                return null;
            }

            string aFile = aHit.GetString(SCP_LibraryIO.Key_File, "");
            string aRoundPath = Path.Combine(aChapterDir, aFile);
            if (!File.Exists(aRoundPath))
            {
                oError = $"索引指向的 round 檔不存在：{aFile}";
                return null;
            }
            string aContent = StripFrontmatter(File.ReadAllText(aRoundPath, Encoding.UTF8)).Trim();

            // 標頭：作品名（media → work 兩跳，缺檔就退回 mediaId —— ⛔ 不因標頭缺料擋分享）
            string aWorkTitle = iMediaId;
            SCP_JsonData? aMedia = SCP_LibraryIO.LoadJson(SCP_LibraryStore.MediaJsonPath(iDataRoot, iMediaId), out _);
            if (aMedia != null)
            {
                string aWorkId = aMedia.GetString(SCP_LibraryIO.Key_WorkId, "");
                SCP_JsonData? aWork = string.IsNullOrEmpty(aWorkId) ? null
                    : SCP_LibraryIO.LoadJson(SCP_LibraryStore.WorkJsonPath(iDataRoot, aWorkId), out _);
                if (aWork != null) aWorkTitle = aWork.GetString(SCP_LibraryIO.Key_Title, iMediaId);
            }

            string aDisplay = aChapter.GetString(SCP_LibraryIO.Key_DisplayNumber, "");
            if (string.IsNullOrEmpty(aDisplay)) aDisplay = iChapterId;
            string aChapterTitle = aChapter.GetString(SCP_LibraryIO.Key_Title, "");

            return $"📖 **閱讀心得｜{aWorkTitle}** {aDisplay}" +
                   (string.IsNullOrEmpty(aChapterTitle) ? "" : $"｜{aChapterTitle}") +
                   $"　(r{ioRoundNumber} by {iPersona})\n\n{aContent}";
        }

        /// <summary>frontmatter 只認**檔案開頭**的 `---` 區塊 —— 內文中的 hr 不受影響。</summary>
        public static string StripFrontmatter(string? iText)
        {
            if (string.IsNullOrEmpty(iText) || !iText!.StartsWith("---", StringComparison.Ordinal)) return iText ?? "";
            int aEnd = iText.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (aEnd < 0) return iText;
            int aLineEnd = iText.IndexOf('\n', aEnd + 1);
            return aLineEnd < 0 ? "" : iText.Substring(aLineEnd + 1);
        }

        /// <summary>把發文回來的 seq 寫回該 round —— 「已發文」的可驗證 receipt。</summary>
        /// <remarks>⚠ 找不到那個 round ⇒ **出聲**（oError），⛔ 不靜默 return：
        /// 沒落 receipt 的那一則下次會被當成「還沒發過」而重發一次，而重發＝重複計酬。</remarks>
        public static void RecordSharedSeq(string iDataRoot, string iMediaId, string iPersona, string iChapterId,
                                           int iRoundNumber, int iSeq, out string? oError)
        {
            string aChapterJsonPath = Path.Combine(
                SCP_LibraryStore.ChapterDir(iDataRoot, iMediaId, iPersona, iChapterId),
                SCP_LibraryStore.ChapterJsonName);
            SCP_JsonData? aChapter = SCP_LibraryIO.LoadJson(aChapterJsonPath, out oError);
            if (aChapter == null) return;

            SCP_JsonData? aRounds = aChapter.Contains(SCP_LibraryIO.Key_Rounds)
                ? aChapter[SCP_LibraryIO.Key_Rounds] : null;
            if (aRounds == null || !aRounds.IsArray) { oError = "chapter.json 缺 rounds"; return; }

            for (int i = 0; i < aRounds.Count; i++)
            {
                if (aRounds[i].GetInt(SCP_LibraryIO.Key_Round, 0) != iRoundNumber) continue;
                aRounds[i][SCP_LibraryIO.Key_SharedSeq] = iSeq;
                SCP_LibraryIO.SaveJson(aChapterJsonPath, aChapter);
                return;
            }
            oError = $"chapter.json 找不到 round {iRoundNumber}，seq={iSeq} 未落 receipt";
        }
    }
}
