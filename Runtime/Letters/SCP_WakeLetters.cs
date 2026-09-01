// 區塊職責：**persona 信件庫的地址簿與清單** —— 各記憶層的檔案在哪、有哪些、哪一份最新。
// 物理意義：一位 persona 的記憶分層落在 `letters/<persona>/` 底下的固定位置：
//             `_constitution.md`            憲法（可被違反的成文法）
//             `_keys_open.md`               見叢 — 當期交棒清單（`- [ ]` / `- [x]`）
//             `longterm/wake_<a>-<b>.md`    見林 — 10 夜濃縮
//             `longterm/forest/gen_NNN*.md` 見森 — 見林的再折疊（刻意不與見林同層，
//                                           否則見林的 glob 會誤抓見森當 pointer）
//             `wakes/*.md` / `rests/*.md` / 頂層 `*.md`   收尾信本體
//             `_latest.md`                  見樹指標（**內容副本不是連結**）
// 數值影響：本檔唯一會寫檔的是 <see cref="SyncLatestPointer"/>（且只在內容不一致時）。
//           其餘全是唯讀。
//
// ⚠ 「自寫信」只認 frontmatter `type: letter_to_future_self`：
//   同一個資料夾還有同事寄來的信（peer_letter_from_persona）與 `_` 開頭的機械產物。
//   🩸 python 端血證：`_` 的字元序大於數字，只用檔名排序會把 `_wake_brief.md`
//     當成「最新的信」—— 那是個安靜的災難（brief 端出一份機器產物當昨夜的信）。
using System;
using System.Collections.Generic;
using System.IO;

using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    /// <summary>一封信在磁碟上的位置與它的排序鍵。</summary>
    public sealed class SCP_LetterRef
    {
        public string Path = "";

        /// <summary>frontmatter 的 <c>written_at</c>；沒有就退回檔名（同 python 端的 fallback）。</summary>
        public string SortKey = "";

        /// <summary>檔名（不含目錄）。</summary>
        public string FileName = "";

        /// <summary>寫信日（<c>written_at</c> 的前 10 字）。取不到回空字串。</summary>
        public string Day = "";
    }

    /// <summary>別條世界線的一封收尾信。⚠ <see cref="WakeNo"/> 走**那條線自己的**編號空間。</summary>
    public sealed class SCP_WorldlineLetter
    {
        public string WorldlineId = "";
        public string Title = "";
        public string Path = "";
        public string FileName = "";
        public int WakeNo;
        public string Day = "";
    }

    public static class SCP_WakeLetters
    {
        /// <summary>自寫信的 frontmatter type 值 —— 這是跨端契約，不是本檔的私事。</summary>
        public const string SelfLetterType = "letter_to_future_self";

        /// <summary>第 N 份見林起開始折見森（digest 計數，非 wake 計數）。對齊 python FOREST_DIGEST_THRESHOLD。</summary>
        public const int ForestDigestThreshold = 3;

        // ── 地址 ──────────────────────────────────────────────────

        // ⚠ 版面本體已搬到 SCP_LettersPaths（2026-08-30）——
        //   本區塊只是既有呼叫端的相容外殼，**不要在這裡新增地址**。
        //   同一個目錄兩處各算一次，改一處漏一處的症狀是靜默的（見 SCP_LettersPaths 檔頭血證）。
        static SCP_LettersRoot R(string iLettersRoot) { return new SCP_LettersRoot(iLettersRoot); }

        public static string PersonaDir(string iLettersRoot, string iPersona)
            => SCP_LettersPaths.PersonaDir(R(iLettersRoot), iPersona);

        public static string ConstitutionPath(string iLettersRoot, string iPersona)
            => SCP_LettersPaths.ConstitutionPath(R(iLettersRoot), iPersona);

        public static string KeysOpenPath(string iLettersRoot, string iPersona)
            => SCP_LettersPaths.KeysOpenPath(R(iLettersRoot), iPersona);

        public static string LatestPointerPath(string iLettersRoot, string iPersona)
            => SCP_LettersPaths.LatestPointerPath(R(iLettersRoot), iPersona);

        public static string LongtermDir(string iLettersRoot, string iPersona)
            => SCP_LettersPaths.LongtermDir(R(iLettersRoot), iPersona);

        public static string ForestDir(string iLettersRoot, string iPersona)
            => SCP_LettersPaths.ForestDir(R(iLettersRoot), iPersona);

        public static string WakesDir(string iLettersRoot, string iPersona)
            => SCP_LettersPaths.WakesDir(R(iLettersRoot), iPersona);

        public static string RestsDir(string iLettersRoot, string iPersona)
            => SCP_LettersPaths.RestsDir(R(iLettersRoot), iPersona);

        // ── 清單 ──────────────────────────────────────────────────

        /// <summary>見林（digest）清單，檔名排序。目錄不存在回空清單（那是「還沒有」不是錯誤）。</summary>
        public static List<string> ListDigests(string iLettersRoot, string iPersona)
            => SortedFiles(LongtermDir(iLettersRoot, iPersona), "wake_*.md");

        /// <summary>見森清單，檔名排序。</summary>
        public static List<string> ListForests(string iLettersRoot, string iPersona)
            => SortedFiles(ForestDir(iLettersRoot, iPersona), "gen_*.md");

        // ── 平行世界線 ────────────────────────────────────────────
        // 區塊職責：列出該 persona 的**其他世界線**的收尾信，併成同一個池。
        // 物理意義：世界線清單**從磁碟列舉**（有 `_manifest.md` 的目錄才算）——
        //           不維護索引檔，因為索引是第二份事實源，而第二份事實源就是漂移的定義。
        // 數值影響：純讀。併池是規格（Tim 2026-08-06）：跨線的 20% 是「跨線」這件事的機率，
        //           不是「每條線各自」的機率 —— 分開算會讓世界線一多就把主線稀釋掉。
        // ⚠ 不套 wake 年齡閘：別線用自己的編號空間，拿本體 wake_count 去減它的編號
        //   是跨座標系相減（2026-08-04「兩條時空共用一組計數器」那隻 bug 的形狀）。
        // ⚠ 同時吃 `wakes/`（已整理）與目錄外層的 `*.md`（還沒整理的線）——
        //   「還沒整理」不該等於「這條線的記憶消失」，靜默少掉一整條線是最難發現的那種漏。

        public const string WorldlinesDirName = "worldlines";
        public const string WorldlineManifestName = "_manifest.md";

        public static List<string> WorldlineDirs(string iLettersRoot, string iPersona)
        {
            var aOut = new List<string>();
            string aRoot = SCP_LettersPaths.PersonaDir(R(iLettersRoot), iPersona) + "/" + WorldlinesDirName;
            if (!Directory.Exists(aRoot)) return aOut;
            string[] aDirs;
            try { aDirs = Directory.GetDirectories(aRoot); }
            catch (Exception) { return aOut; }
            Array.Sort(aDirs, StringComparer.Ordinal);
            foreach (string aDir in aDirs)
                if (File.Exists(Path.Combine(aDir, WorldlineManifestName))) aOut.Add(aDir);
            return aOut;
        }

        public static List<SCP_WorldlineLetter> WorldlineLetters(string iLettersRoot, string iPersona)
        {
            var aOut = new List<SCP_WorldlineLetter>();
            foreach (string aDir in WorldlineDirs(iLettersRoot, iPersona))
            {
                string aId = Path.GetFileName(aDir);
                string aTitle = SCP_LetterText.ReadFrontmatterField(
                    Path.Combine(aDir, WorldlineManifestName), "title").Trim();

                var aCandidates = new List<string>();
                string aWakes = Path.Combine(aDir, SCP_LettersPaths.WakesDirName);
                if (Directory.Exists(aWakes)) aCandidates.AddRange(SafeGetFiles(aWakes, "*.md"));
                aCandidates.AddRange(SafeGetFiles(aDir, "*.md"));

                var aSeen = new List<string>();
                foreach (string aPath in aCandidates)
                {
                    string aName = Path.GetFileName(aPath);
                    if (aName.StartsWith("_", StringComparison.Ordinal)) continue;   // 機械產物不是信
                    if (SCP_LetterText.ReadFrontmatterField(aPath, "type") != SelfLetterType) continue;

                    // 同一封的兩份實體（外層原檔／wakes 副本）算一次 —— 鍵取檔名去掉 `NNNNNN_` 前綴。
                    int aUnderscore = aName.IndexOf('_');
                    string aKey = aUnderscore >= 0 ? aName.Substring(aUnderscore + 1) : aName;
                    if (aSeen.Contains(aKey)) continue;
                    aSeen.Add(aKey);

                    string aWrittenAt = SCP_LetterText.ReadFrontmatterField(aPath, "written_at");
                    aOut.Add(new SCP_WorldlineLetter
                    {
                        WorldlineId = aId,
                        Title = aTitle,
                        Path = aPath,
                        FileName = aName,
                        WakeNo = WakeNoOfFileName(aName),
                        Day = DayOf(aWrittenAt),
                    });
                }
            }
            aOut.Sort((a, b) =>
            {
                int aCmp = string.CompareOrdinal(a.WorldlineId, b.WorldlineId);
                if (aCmp != 0) return aCmp;
                if (a.WakeNo != b.WakeNo) return a.WakeNo.CompareTo(b.WakeNo);
                return string.CompareOrdinal(a.FileName, b.FileName);
            });
            return aOut;
        }

        /// <summary>
        /// 時戳正規化 —— 只留數字（`2026-09-01T09:12:59.123Z` → `20260901091259123`）。
        /// <para>⚠ 給**排序**用，不是給顯示用。兩種格式混在同一個目錄裡是活的現況，
        /// 而字串排序對混格式給的是錯的順序、沿途零紅燈。</para>
        /// </summary>
        public static string NormalizeStamp(string iValue)
        {
            var aSb = new System.Text.StringBuilder(iValue.Length);
            foreach (char aChar in iValue)
                if (aChar >= '0' && aChar <= '9') aSb.Append(aChar);
            return aSb.ToString();
        }

        /// <summary>從 `000045_<ts>.md` 取 wake 編號。解不出來回 0。</summary>
        public static int WakeNoOfFileName(string iFileName)
        {
            int aUnderscore = iFileName.IndexOf('_');
            if (aUnderscore <= 0) return 0;
            return int.TryParse(iFileName.Substring(0, aUnderscore),
                                System.Globalization.NumberStyles.None,
                                System.Globalization.CultureInfo.InvariantCulture, out int aValue)
                   ? aValue : 0;
        }

        /// <summary>
        /// `written_at` → `YYYY-MM-DD`。⚠ 兩種格式並存（`2026-09-01T…` 與緊湊的 `20260831T…`），
        /// 只切前 10 字會把緊湊那種切成 `20260831T1`（2026-09-01 實測到的活體）。
        /// </summary>
        public static string DayOf(string iWrittenAt)
        {
            string aValue = (iWrittenAt ?? "").Trim();
            if (aValue.Length >= 10 && aValue[4] == '-' && aValue[7] == '-') return aValue.Substring(0, 10);
            if (aValue.Length >= 8 && IsAllDigits(aValue, 8))
                return aValue.Substring(0, 4) + "-" + aValue.Substring(4, 2) + "-" + aValue.Substring(6, 2);
            return "";
        }

        static bool IsAllDigits(string iValue, int iCount)
        {
            for (int i = 0; i < iCount; i++)
                if (iValue[i] < '0' || iValue[i] > '9') return false;
            return true;
        }

        static List<string> SafeGetFiles(string iDir, string iPattern)
        {
            try { return new List<string>(Directory.GetFiles(iDir, iPattern)); }
            catch (Exception) { return new List<string>(); }
        }

        static List<string> SortedFiles(string iDir, string iPattern)
        {
            var aOut = new List<string>();
            if (!Directory.Exists(iDir)) return aOut;
            try { aOut.AddRange(Directory.GetFiles(iDir, iPattern)); }
            catch (Exception) { return aOut; }
            aOut.Sort(StringComparer.Ordinal);
            return aOut;
        }

        /// <summary>
        /// 見叢：解析 `- [ ]` / `- [x]` 行。回 (未勾銷, 已勾銷)。檔不存在 ⇒ 兩個空清單。
        /// </summary>
        public static (List<string> Todo, List<string> Done) KeysEntries(string iLettersRoot, string iPersona)
        {
            var aTodo = new List<string>();
            var aDone = new List<string>();
            string aPath = KeysOpenPath(iLettersRoot, iPersona);
            if (!File.Exists(aPath)) return (aTodo, aDone);

            string aText;
            try { aText = File.ReadAllText(aPath); }
            catch (Exception) { return (aTodo, aDone); }

            foreach (string aRaw in aText.Split('\n'))
            {
                string aLine = aRaw.Trim();
                if (aLine.StartsWith("- [ ]", StringComparison.Ordinal))
                    aTodo.Add(aLine.Substring(5).Trim());
                else if (aLine.StartsWith("- [x]", StringComparison.Ordinal)
                         || aLine.StartsWith("- [X]", StringComparison.Ordinal))
                    aDone.Add(aLine.Substring(5).Trim());
            }
            return (aTodo, aDone);
        }

        /// <summary>
        /// 該 persona 的自寫信，**新到舊**（頂層 ＋ `wakes/` ＋ `rests/`，去重）。
        /// <para>⚠ 三個位置都要掃：`rests/` 是 rest 信的新家，不掃的話那些信會從見樹
        /// **靜默消失** —— 而 brief 長得一模一樣。</para>
        /// <para>去重規則同 python：`wakes/` 底下的 `<前綴>_<原檔名>` 與頂層同名檔是同一封信。</para>
        /// </summary>
        public static List<SCP_LetterRef> RecentSelfLetters(string iLettersRoot, string iPersona)
        {
            var aOut = new List<SCP_LetterRef>();
            string aDir = PersonaDir(iLettersRoot, iPersona);
            if (!Directory.Exists(aDir)) return aOut;

            var aTopLevel = new HashSet<string>(StringComparer.Ordinal);
            foreach (string aFile in SafeFiles(aDir, "*.md")) aTopLevel.Add(Path.GetFileName(aFile));

            var aCandidates = new List<string>();
            aCandidates.AddRange(SafeFiles(aDir, "*.md"));
            aCandidates.AddRange(SafeFiles(WakesDir(iLettersRoot, iPersona), "*.md"));
            aCandidates.AddRange(SafeFiles(RestsDir(iLettersRoot, iPersona), "*.md"));

            foreach (string aPath in aCandidates)
            {
                string aName = Path.GetFileName(aPath);
                if (aName.StartsWith("_", StringComparison.Ordinal)) continue;   // 機械產物不是信

                string aParent = Path.GetFileName(Path.GetDirectoryName(aPath) ?? "");
                if (string.Equals(aParent, "wakes", StringComparison.Ordinal))
                {
                    int aUnderscore = aName.IndexOf('_');
                    string aTail = aUnderscore >= 0 ? aName.Substring(aUnderscore + 1) : aName;
                    if (aTopLevel.Contains(aTail)) continue;   // 遷移副本與頂層原檔是同一封
                }

                if (SCP_LetterText.ReadFrontmatterField(aPath, "type") != SelfLetterType) continue;

                string aWrittenAt = SCP_LetterText.ReadFrontmatterField(aPath, "written_at");
                aOut.Add(new SCP_LetterRef
                {
                    Path = aPath,
                    FileName = aName,
                    // 🩸 排序鍵要**正規化**：這批信兩種 `written_at` 格式並存
                    //   （`2026-09-01T…` 與緊湊的 `20260831T…`）。拿原字串做 ordinal 比較時
                    //   `-`(0x2D) < `0`(0x30) ⇒ 帶連字號那種一律排在前面 ⇒ **新舊會靜默翻掉**，
                    //   而每一格讀數看起來都正常（2026-09-01 實測：basecamp 的「最新一封」指到 08-31，
                    //   而 09-01 那封被排成「往前補」）。⇒ 只留數字再比。
                    SortKey = aWrittenAt.Length > 0 ? NormalizeStamp(aWrittenAt) : aName,
                    Day = DayOf(aWrittenAt),
                });
            }

            // 新 → 舊。⚠ 用 written_at 不用檔名：wakes/ 的檔名是 `000045_<ts>.md`，
            //   拿它切前 10 字會得到 "000045_202"（python 端拆日期閘時撞見的那隻）。
            aOut.Sort((a, b) => string.CompareOrdinal(b.SortKey, a.SortKey));
            return aOut;
        }

        static IEnumerable<string> SafeFiles(string iDir, string iPattern)
        {
            if (!Directory.Exists(iDir)) return new string[0];
            try { return Directory.GetFiles(iDir, iPattern); }
            catch (Exception) { return new string[0]; }
        }

        /// <summary>
        /// 讓 `_latest.md` 等於目錄內最新的自寫信。回 (指標路徑, 有沒有修補過)；沒有任何信回 (null, false)。
        /// <para>物理意義：`_latest.md` 是**內容副本不是符號連結**，所以任何沒經過寫信工具的寫入
        /// 都會讓它落後，而**落後時毫無徵狀** —— brief 長得跟正常一模一樣，只是少了幾天記憶。
        /// ⇒ 每次生成 brief 時順手校正。</para>
        /// <para>⚠ 修了要說：回傳 healed=true 讓呼叫端印一行。**自癒可以安靜地做，但不能安靜地發生。**</para>
        /// </summary>
        public static (string? Pointer, bool Healed) SyncLatestPointer(string iLettersRoot, string iPersona)
        {
            List<SCP_LetterRef> aLetters = RecentSelfLetters(iLettersRoot, iPersona);
            if (aLetters.Count == 0) return (null, false);

            string aPointer = LatestPointerPath(iLettersRoot, iPersona);
            string aBody;
            try { aBody = File.ReadAllText(aLetters[0].Path); }
            catch (Exception) { return (File.Exists(aPointer) ? aPointer : null, false); }

            string? aOld = null;
            if (File.Exists(aPointer))
            {
                try { aOld = File.ReadAllText(aPointer); }
                catch (Exception) { aOld = null; }
            }
            if (aOld == aBody) return (aPointer, false);

            try { File.WriteAllText(aPointer, aBody); }
            catch (Exception) { return (File.Exists(aPointer) ? aPointer : null, false); }
            return (aPointer, true);
        }
    }
}
