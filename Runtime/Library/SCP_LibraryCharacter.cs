// 區塊職責：人物與書籤 —— facts（客觀，profile.json）與 view（主觀，vN_<date>.md）**分離**。
// 物理意義：**改觀就 fork 新版本，絕不覆寫舊版** —— 好書值得重讀正因看法會變，
//           v1→v2→v3 的演變本身就是閱讀體驗（同構於 relationship opinion history / persona fork）。
//           ⇒ 覆寫 v1 抹掉的不是一段文字，是「我當時還不知道」這件事，而它事後重建不回來。
// 數值影響：`AddCharacter` 只在人物不存在時建 v1；已存在一律要求走 `ReviseView`。
//           兩支寫完都重生成追回檔（stale 投影比沒有投影更糟）。
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
        /// facts 預設**接在既有後面**（完全相同的不重收）；<paramref name="iReplaceFacts"/> ＝ true 才整份換掉，回傳逐條列出被拿掉的。
        /// 🩸 TASK-0634：原本一律整份覆寫 ⇒ 讀者只給本章新查到的 facts，前面幾章的就安靜消失
        ///   （BookNotes 2e2a39d 少 8 條、a901612 少 5 條、kotoko 的拉塞爾斯少 6 條），回傳只寫「facts 同步更新」。
        /// </summary>
        public static string? ReviseView(SCP_LettersRoot iLettersRoot, string iDataRoot,
                                         string iMediaId, string iPersona, string iCharacterId,
                                         string iView, string? iChangeReason, string? iFacts, bool iReplaceFacts,
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

            // facts 先算好（寫 view 之前）：算不出來就整筆拒絕，不留「view 寫了、facts 沒動」的半套
            string aFactsLog = "";
            SCP_JsonData? aNewFacts = null;
            if (!string.IsNullOrEmpty(iFacts))
            {
                aNewFacts = MergeFacts(aProfile, iFacts!, iReplaceFacts, out aFactsLog, out oError);
                if (aNewFacts == null) return null;
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

            if (aNewFacts != null)
            {
                aProfile[SCP_LibraryIO.Key_Facts] = aNewFacts;   // 一律陣列
                SCP_LibraryIO.SaveJson(aProfilePath, aProfile);
            }

            SCP_LibraryRecall.WriteRecallBrief(iLettersRoot, iDataRoot, iMediaId, iPersona, true, out _);
            return $"- ✅ `{iCharacterId}` 看法已 fork 為 **v{aVersion}**（`{aFileName}`）；" +
                   $"v1–v{aMaxVersion} 保留不動" + aFactsLog;
        }

        // ===========================================================
        // 區塊職責：revise_view 的 facts —— 既有 ＋ 這次給的 ⇒ 要寫回的陣列，以及一行讀數。
        // 物理意義：合併（預設）＝ 既有照原順序、新的接在後面、跟既有或彼此完全相同（trim 後）的不重收。
        //          取代（iReplace）＝ 整份換成這次給的；**被拿掉的逐條寫進回傳**（安靜消失正是 TASK-0634 的病）。
        // ⚠ 既有 facts 是陣列卻有非字串元素 ⇒ 拒絕：ReadFactsList 讀不到它們，照讀出來的寫回去會把它們刪掉。
        //   （2026-10-16 掃 BookNotes 121 份 profile：0 份有，這一格是守門不是修資料。）
        // ===========================================================
        static SCP_JsonData? MergeFacts(SCP_JsonData iProfile, string iFacts, bool iReplace,
                                        out string oLog, out string? oError)
        {
            oLog = ""; oError = null;
            if (iProfile.Contains(SCP_LibraryIO.Key_Facts) && iProfile[SCP_LibraryIO.Key_Facts].IsArray)
            {
                SCP_JsonData aRaw = iProfile[SCP_LibraryIO.Key_Facts];
                for (int i = 0; i < aRaw.Count; i++)
                    if (!aRaw[i].IsString)
                    {
                        oError = $"profile.json 的 facts 第 {i + 1} 條不是字串 —— 合併會把它刪掉，拒絕；先手動看一眼那份檔（view 也沒有 fork）";
                        return null;
                    }
            }
            List<string> aOld = SCP_LibraryRecall.ReadFactsList(iProfile);
            var aGiven = new List<string>();
            foreach (string aLine in iFacts.Split('\n'))
            {
                string t = aLine.Trim();
                if (t.Length > 0 && !aGiven.Contains(t)) aGiven.Add(t);
            }

            var aOut = SCP_JsonData.NewArray();
            if (iReplace)
            {
                foreach (string s in aGiven) aOut.Add(s);
                var aDropped = aOld.Where(s => !aGiven.Contains(s)).ToList();
                oLog = $"；facts **整份換成** {aGiven.Count} 條（facts_mode=replace）"
                     + (aDropped.Count == 0 ? "，沒有拿掉任何一條"
                        : $"，拿掉 {aDropped.Count} 條：\n" + string.Join("\n", aDropped.Select(s => "  - ⚠ " + s)));
                return aOut;
            }
            foreach (string s in aOld) aOut.Add(s);
            int aAdded = 0;
            foreach (string s in aGiven)
                if (!aOld.Contains(s)) { aOut.Add(s); aAdded++; }
            oLog = $"；facts 接上 {aAdded} 條（既有 {aOld.Count} 條保留"
                 + (aGiven.Count - aAdded > 0 ? $"、{aGiven.Count - aAdded} 條跟既有相同沒重收" : "")
                 + $" ⇒ 共 {aOld.Count + aAdded} 條）";
            return aOut;
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
