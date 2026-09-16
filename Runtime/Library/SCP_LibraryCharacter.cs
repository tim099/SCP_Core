// 區塊職責：人物與書籤 —— facts（客觀，profile.json）與 view（主觀，vN_<date>.md）**分離**。
// 物理意義：**改觀就 fork 新版本，絕不覆寫舊版** —— 好書值得重讀正因看法會變，
//           v1→v2→v3 的演變本身就是閱讀體驗（同構於 relationship opinion history / persona fork）。
//           ⇒ 覆寫 v1 抹掉的不是一段文字，是「我當時還不知道」這件事，而它事後重建不回來。
// 數值影響：`AddCharacter` 只在人物不存在時建 v1；已存在一律要求走 `ReviseView`。
//           兩支寫完都重生成追回檔（stale 投影比沒有投影更糟）。
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Library
{
    public static class SCP_LibraryCharacter
    {
        static readonly Regex k_ViewFilePattern = new Regex(@"^v(\d+)_");

        // ===========================================================
        // 區塊職責：第一次記一個人物 —— profile.json（facts）＋ v1 看法檔。
        // ⚠ 人物已存在時**拒絕**，並指去 `revise_view`：
        //   用 add_character 覆寫既有 v1 會抹掉當時的「還不知道」，而那一格事後補不回來。
        // ===========================================================
        public static string? AddCharacter(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                           string iMediaId, string iPersona, string iCharacterId,
                                           string iName, string? iNameOriginal, string? iFacts, string iView,
                                           out string? oError)
        {
            if (SCP_LibraryIO.LoadReader(iDataRoot, iMediaId, iPersona, out oError) == null) return null;

            string aDir = SCP_LibraryStore.CharacterDir(iDataRoot, iMediaId, iPersona, iCharacterId);
            string aProfilePath = Path.Combine(aDir, SCP_LibraryStore.ProfileJsonName);
            if (File.Exists(aProfilePath))
            {
                oError = $"人物已存在：{iCharacterId} —— **看法有變請走 op=revise_view（fork 新版本）**，" +
                         "不要用 add_character 覆寫既有 v1（那會抹掉當時的「還不知道」）。" +
                         "只想補客觀 facts 也走 revise_view --facts。";
                return null;
            }

            SCP_JsonData aProfile = SCP_JsonData.NewObject();
            aProfile[SCP_LibraryIO.Key_CharacterId] = iCharacterId;
            aProfile[SCP_LibraryIO.Key_Name] = iName;
            aProfile[SCP_LibraryIO.Key_NameOriginal] = iNameOriginal ?? "";
            aProfile[SCP_LibraryIO.Key_Facts] = SCP_LibraryRecall.FactsToJson(iFacts);   // 一律陣列，寫端收斂
            aProfile[SCP_LibraryIO.Key_SchemaVersion] = 1;
            SCP_LibraryIO.SaveJson(aProfilePath, aProfile);

            string aFileName = $"v1_{SCP_LibraryIO.Today()}.md";
            SCP_LibraryIO.SaveText(Path.Combine(aDir, aFileName),
                RenderViewFile(iCharacterId, 1, iPersona, null, iView));

            SCP_LibraryRecall.WriteRecallBrief(iLettersRoot, iDataRoot, iMediaId, iPersona, true, out _);
            return $"- ✅ 新增人物 `{iCharacterId}`（{iName}）＋ 初版看法 `{aFileName}`";
        }

        /// <summary>
        /// 改觀 → fork 下一版 view（**永不覆寫**）。可同時補客觀 facts（那是可更新的已確認資料）。
        /// </summary>
        public static string? ReviseView(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                         string iMediaId, string iPersona, string iCharacterId,
                                         string iView, string? iChangeReason, string? iFacts,
                                         out string? oError)
        {
            if (SCP_LibraryIO.LoadReader(iDataRoot, iMediaId, iPersona, out oError) == null) return null;

            string aDir = SCP_LibraryStore.CharacterDir(iDataRoot, iMediaId, iPersona, iCharacterId);
            string aProfilePath = Path.Combine(aDir, SCP_LibraryStore.ProfileJsonName);
            SCP_JsonData? aProfile = SCP_LibraryIO.LoadJson(aProfilePath, out oError);
            if (aProfile == null)
            {
                oError = $"{oError}\n→ 人物不存在，第一次記請走 op=add_character。";
                return null;
            }

            // 版本號取既有**檔案**最大值 + 1（掃磁碟而非猜，缺號也不會覆蓋既有版本）
            int aMaxVersion = 0;
            foreach (string aExisting in Directory.GetFiles(aDir, "v*.md"))
            {
                Match m = k_ViewFilePattern.Match(Path.GetFileName(aExisting));
                if (m.Success && int.TryParse(m.Groups[1].Value, out int n) && n > aMaxVersion) aMaxVersion = n;
            }
            int aVersion = aMaxVersion + 1;

            string aFileName = $"v{aVersion}_{SCP_LibraryIO.Today()}.md";
            string aPath = Path.Combine(aDir, aFileName);
            if (File.Exists(aPath))
            {
                oError = $"同日已有 {aFileName} 但不在版本掃描結果內 —— 拒絕覆寫，請人先看一眼";
                return null;
            }
            SCP_LibraryIO.SaveText(aPath, RenderViewFile(iCharacterId, aVersion, iPersona, iChangeReason, iView));

            if (!string.IsNullOrEmpty(iFacts))
            {
                aProfile[SCP_LibraryIO.Key_Facts] = SCP_LibraryRecall.FactsToJson(iFacts);   // 一律陣列
                SCP_LibraryIO.SaveJson(aProfilePath, aProfile);
            }

            SCP_LibraryRecall.WriteRecallBrief(iLettersRoot, iDataRoot, iMediaId, iPersona, true, out _);
            return $"- ✅ `{iCharacterId}` 看法已 fork 為 **v{aVersion}**（`{aFileName}`）；" +
                   $"v1–v{aMaxVersion} 保留不動" + (string.IsNullOrEmpty(iFacts) ? "" : "；facts 同步更新");
        }

        /// <summary>view 檔內容 —— frontmatter 與既有樣本同構（character_id / version / date / reader_persona）。</summary>
        public static string RenderViewFile(string iCharacterId, int iVersion, string iPersona,
                                            string? iChangeReason, string iView)
        {
            var aSb = new StringBuilder();
            aSb.AppendLine("---");
            aSb.AppendLine($"{SCP_LibraryIO.Key_CharacterId}: {iCharacterId}");
            aSb.AppendLine($"version: {iVersion}");
            aSb.AppendLine($"date: {SCP_LibraryIO.Today()}");
            aSb.AppendLine($"{SCP_LibraryIO.Key_ReaderPersona}: {iPersona}");
            aSb.AppendLine("---");
            aSb.AppendLine();
            aSb.AppendLine($"## {iPersona} 的看法（v{iVersion}）");
            aSb.AppendLine();
            if (!string.IsNullOrEmpty(iChangeReason))
            {
                // 改觀理由單獨成段：**為什麼變**比**變成什麼**更難事後重建
                aSb.AppendLine($"> **改觀觸發**：{iChangeReason}");
                aSb.AppendLine();
            }
            aSb.AppendLine(iView.TrimEnd());
            return aSb.ToString();
        }

        /// <summary>只更新書籤與當前看法（op=bookmark）。</summary>
        public static string? Bookmark(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                       string iMediaId, string iPersona,
                                       string? iNote, string? iImpression, string? iStatus,
                                       out string? oError)
        {
            SCP_JsonData? aReader = SCP_LibraryIO.LoadReader(iDataRoot, iMediaId, iPersona, out oError);
            if (aReader == null) return null;

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
            if (!string.IsNullOrEmpty(iNote)) aProgress[SCP_LibraryIO.Key_BookmarkNote] = iNote!;
            aProgress[SCP_LibraryIO.Key_LastRead] = SCP_LibraryIO.Today();
            if (!string.IsNullOrEmpty(iImpression)) aReader[SCP_LibraryIO.Key_CurrentImpression] = iImpression!;
            if (!string.IsNullOrEmpty(iStatus)) aReader[SCP_LibraryIO.Key_Status] = iStatus!;
            aReader[SCP_LibraryIO.Key_UpdatedAt] = SCP_LibraryIO.Today();
            SCP_LibraryIO.SaveJson(SCP_LibraryStore.ReaderJsonPath(iDataRoot, iMediaId, iPersona), aReader);
            SCP_LibraryBookshelf.SyncBookshelf(iLettersRoot, iDataRoot, iMediaId, iPersona, out _, out _);
            // 同上：書籤變了追回檔就得重生成
            SCP_LibraryRecall.WriteRecallBrief(iLettersRoot, iDataRoot, iMediaId, iPersona, true, out _);
            return $"- 書籤已更新（`{iMediaId}` / `{iPersona}`）";
        }
    }
}
