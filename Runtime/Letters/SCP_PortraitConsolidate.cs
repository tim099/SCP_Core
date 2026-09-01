// 區塊職責：見人濃縮的**寫入端** —— 產生 `sketchbook/<target>/<target>_vNNN.md` 並把逐幅畫像搬進 `raw/`。
// 物理意義：新的一版 ＝ 前一版 ＋ 這段期間的新畫像（rolling fold，同見森折見林的形狀）。
//           設計意圖（Tim 2026-09-01 拍板）：**對一個人的看法本來就隨時間改變，所以不追求精確** ——
//           對方也在變，舊看法的權重本來就該衰減。⇒ 遺忘在這套機制裡是設計，不是缺陷。
//           而 raw 只搬不刪、vN 記 inputs，買的**不是精確度**，是「我的看法真的變了」與
//           「上一版寫歪了、這一版照抄」分得開 —— 那兩者在檔案上長得一模一樣。
// 數值影響：寫一個新檔 ＋ 搬 N 個檔（不刪任何東西）。版號 = 掃目錄取 max + 1。
//
// ⚠ 三道守衛，全部是「擋下來」不是「幫你修」：
//   ① 大小寫變體（`Sirius/` vs `sirius/`）—— NTFS 看起來同一個目錄，git 記成兩筆
//   ② 同一個 `wake_range` 想再寫一版 —— 重跑見林不該多長一版
//   ③ 沒有新素材 —— 一版沒有輸入的濃縮是一句沒有讀數的話
// ⚠ 順序不可換：**先寫成功、才搬檔**。反過來的話寫入失敗時畫像已經離開根層，
//   而 §6.5 讀根層 ⇒ 那個人會從「我認識誰」裡消失，且沒有任何一格會紅。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    /// <summary>濃縮的結果。<see cref="Blocked"/> 非 null ＝ 一個字都沒寫。</summary>
    public sealed class SCP_ConsolidateResult
    {
        /// <summary>被擋下來的原因（含怎麼解）。null ＝ 沒被擋。</summary>
        public string? Blocked;

        public string Path = "";
        public int Version;

        /// <summary>這一版讀了哪幾幅（檔名，寫進檔頭 inputs）。</summary>
        public List<string> Inputs = new List<string>();

        /// <summary>實際搬進 `raw/` 的檔名。⚠ 與 <see cref="Inputs"/> 可能不同：搬檔失敗的留在原地。</summary>
        public List<string> Archived = new List<string>();

        /// <summary>搬檔失敗的（檔名 → 原因）。空 ＝ 全部搬成功。</summary>
        public List<string> ArchiveFailures = new List<string>();

        /// <summary>回讀確認：寫出去的檔在磁碟上有幾行。0 ＝ 回讀不到（要當失敗看）。</summary>
        public int WrittenLines;
    }

    public static class SCP_PortraitConsolidate
    {
        public const string ConsolidatedType = "consolidated_portrait";

        /// <summary>
        /// 折一版。<paramref name="iBody"/> 必須是**親筆**內文（工具不代筆 —— 見人是判斷不是統計）。
        /// </summary>
        /// <param name="iWakeRange">
        /// 本版是在**哪個 wake 區間折的**（例如 `33-49`），Tim 2026-09-01 拍板 ——
        /// **不是素材的產出區間**。素材的真實日期在 `inputs.raw_portraits` 的檔名裡看得到，
        /// 不必在這一格再編一次（一個欄位兩種語意，讀的人分不出手上那個是哪一種）。
        /// </param>
        public static SCP_ConsolidateResult Run(string iLettersRoot, string iPersona, string iTarget,
                                                string iWakeRange, string iBody, string iBy,
                                                bool iArchive = true)
        {
            var aResult = new SCP_ConsolidateResult();

            string aBody = (iBody ?? "").Trim();
            if (aBody.Length == 0)
            {
                aResult.Blocked = "✗ 濃縮內文是空的 —— 這一版必須是妳親筆寫的（工具代筆的看法不是妳的）。";
                return aResult;
            }
            string aRange = (iWakeRange ?? "").Trim();
            if (aRange.Length == 0)
            {
                aResult.Blocked = "✗ 缺 wake_range —— 沒有區間的版本無法判斷「同區間重寫」，"
                                  + "而那道守衛正是為了擋重跑見林多長一版。";
                return aResult;
            }

            // ── 守衛① 大小寫變體 ────────────────────────────────────
            (string aDir, string? aActual) = SCP_PortraitView.ResolveTargetDir(iLettersRoot, iPersona, iTarget);
            if (aActual != null && !string.Equals(aActual, iTarget, StringComparison.Ordinal))
            {
                aResult.Blocked = "✗ 濃縮目錄已經存在，但叫 `" + aActual + "`（妳給的是 `" + iTarget + "`）。\n"
                                  + "  這台的檔案系統大小寫不敏感 ⇒ 兩個名字指到同一個目錄，"
                                  + "而 git 會把它們記成兩筆 ⇒ 別人 clone 之後才會炸。\n"
                                  + "  ⇒ 用既有的那個名字（`" + aActual + "`）重跑；真要改名走 git mv，不要靠寫入端猜。";
                return aResult;
            }

            SCP_PortraitTargetView aView = SCP_PortraitView.Build(iLettersRoot, iPersona, iTarget);

            // ── 守衛② 同一個 wake_range ─────────────────────────────
            if (Directory.Exists(aDir))
            {
                foreach (string aFile in SafeFiles(aDir, "*.md"))
                {
                    if (SCP_PortraitView.VersionOf(Path.GetFileName(aFile)) <= 0) continue;
                    string aExisting = SCP_LetterText.ReadFrontmatterField(aFile, "wake_range").Trim();
                    if (!string.Equals(aExisting, aRange, StringComparison.Ordinal)) continue;
                    aResult.Blocked = "✗ 已經有一版涵蓋 `" + aRange + "`：`" + Path.GetFileName(aFile) + "`。\n"
                                      + "  同區間不覆寫、不再長一版（Tim 2026-09-01 拍板）——"
                                      + "重跑見林時多一版會讓 rolling fold 多轉述一次同一段話。\n"
                                      + "  ⇒ 真要改那一版的內容就直接編輯它；要往前推就給新的區間。";
                    return aResult;
                }
            }

            // ── 守衛③ 沒有新素材 ───────────────────────────────────
            List<string> aRaw = aView.UnarchivedPaths;      // 新 → 舊
            if (aRaw.Count == 0)
            {
                aResult.Blocked = "✗ 根層沒有未歸檔的 `" + iTarget + "` 畫像 ⇒ 這一版沒有輸入。\n"
                                  + "  一版沒有素材的濃縮，跟一版憑印象寫的濃縮，從外面看一模一樣。";
                return aResult;
            }
            // ⛔ 這裡曾經有一道「只有 1 幅就擋」的閘（basecamp 的建議），**已於 2026-09-01 移除**。
            //   🩸 Tim 的拍板：**見林時把根層未歸檔的全折完，一幅也折，複製沒錯。**
            //   而我那道閘造成的後果是實害不是理論：gura 照我的建議少折 17 幅（summit 10／Sirius 6／
            //   apex-one 1），而那 17 幅既不會被任何一版吃進去、又因為見人只看近 14 天而看不見
            //   ⇒ 那不是「自然衰減」，是**靜默遺棄**。
            //   📌 一般形：**「衰減」講的是新版取代舊版的內容，不是「不折」。**
            //     把顯示規則（只讀 max(v)＋未歸檔）推導成寫入規則（舊的不必折）＝跨層推論。

            int aVersion = (aView.Latest?.Version ?? 0) + 1;   // 掃目錄取 max + 1，不用外部計數器
            string aPath = SCP_LettersPaths.ConsolidatedPortraitPath(
                new SCP_LettersRoot(iLettersRoot), iPersona, iTarget, aVersion);
            if (File.Exists(aPath))
            {
                // 版號算出來卻已經有檔 ⇒ 我的 max 判定與磁碟不一致，停手比猜安全。
                aResult.Blocked = "✗ 算出來的版號檔已經存在：`" + Path.GetFileName(aPath) + "`"
                                  + "（max 判定與磁碟不一致 —— 先去看那個目錄裡有什麼）。";
                return aResult;
            }

            foreach (string aFile in aRaw) aResult.Inputs.Add(Path.GetFileName(aFile));

            var aText = new StringBuilder();
            aText.Append("---\n");
            aText.Append("type: ").Append(ConsolidatedType).Append('\n');
            aText.Append("about: ").Append(iTarget).Append('\n');
            aText.Append("by: ").Append(iBy.Length > 0 ? iBy : iPersona).Append('\n');
            // ⚠ `version` 是**派生值**：權威是檔名。兩邊不一致時讀取端會出聲並以檔名為準。
            aText.Append("version: ").Append(aVersion.ToString(CultureInfo.InvariantCulture))
                 .Append("   # 派生值，權威是檔名\n");
            aText.Append("wake_range: ").Append(aRange).Append('\n');
            aText.Append("consolidated_at: ").Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ",
                                                                             CultureInfo.InvariantCulture)).Append('\n');
            aText.Append("inputs:\n");
            aText.Append("  previous_version: ")
                 .Append(aView.Latest != null ? Path.GetFileName(aView.Latest.Path) : "null").Append('\n');
            aText.Append("  raw_portraits:\n");
            foreach (string aName in aResult.Inputs) aText.Append("    - ").Append(aName).Append('\n');
            aText.Append("---\n\n");
            aText.Append(aBody.Replace("\r\n", "\n"));
            if (!aBody.EndsWith("\n", StringComparison.Ordinal)) aText.Append('\n');

            try
            {
                Directory.CreateDirectory(aDir);
                // tmp + replace：寫一半的濃縮檔比沒有濃縮檔糟（讀取端會把它當一版）
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, aText.ToString(), new UTF8Encoding(false));
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
            }
            catch (Exception e)
            {
                aResult.Blocked = "✗ 寫濃縮檔失敗（**沒有搬任何畫像**）：" + e.GetType().Name + ": " + e.Message;
                return aResult;
            }

            aResult.Path = aPath;
            aResult.Version = aVersion;

            // 回讀 —— 「我寫了」不是「它在裡面」。
            try { aResult.WrittenLines = File.ReadAllLines(aPath).Length; }
            catch (Exception) { aResult.WrittenLines = 0; }

            // ── 搬檔（只搬不刪）—— 一定在寫入成功之後 ──────────────
            if (!iArchive) return aResult;
            string aRawDir = SCP_LettersPaths.SketchbookRawDir(
                new SCP_LettersRoot(iLettersRoot), iPersona, iTarget);
            foreach (string aFile in aRaw)
            {
                string aName = Path.GetFileName(aFile);
                try
                {
                    Directory.CreateDirectory(aRawDir);
                    string aTo = Path.Combine(aRawDir, aName);
                    if (File.Exists(aTo))
                    {
                        // 同名已在 raw/ ⇒ 不覆寫、不刪來源。兩份同名畫像要人來看，不該由工具選一份。
                        aResult.ArchiveFailures.Add(aName + "（raw/ 已有同名檔，未搬、未刪）");
                        continue;
                    }
                    File.Move(aFile, aTo);
                    aResult.Archived.Add(aName);
                }
                catch (Exception e)
                {
                    aResult.ArchiveFailures.Add(aName + "（" + e.GetType().Name + ": " + e.Message + "）");
                }
            }
            return aResult;
        }

        static List<string> SafeFiles(string iDir, string iPattern)
        {
            try { return new List<string>(Directory.GetFiles(iDir, iPattern)); }
            catch (Exception) { return new List<string>(); }
        }
    }
}
