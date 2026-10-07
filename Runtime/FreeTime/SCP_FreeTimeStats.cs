// 區塊職責：自由時間「每個活動有多少場沒被選過」的統計（飢餓度）與置頂判定。
// 物理意義：骰面每場重新洗牌 ⇒ **冷門活動的冷門本來是不可觀測的**：它每場都在清單裡，看起來一切正常，
//          而沒有任何一層會說「這件事你 12 場沒碰過」。⇒ 飢餓度 ＝ 本 persona 已跑過的場次 − 該活動最後被選中的場次。
//          by persona：每個人做的事不一樣，全域統計會讓多數人的偏好把少數人的空白抹平。
// 數值影響：存 `letters/<persona>/profile/freetime_activity_stats.md`（profile 欄慣例：檔名＝欄位、內文＝值；
//          本欄的值是**壓縮 JSON**，與 Unity 版逐字同格式）。只影響骰面排序與名字後綴，**不擋任何事**。
//          讀不到／解析失敗一律當「沒有統計」（不置頂），但 `Loaded=false` 讓呼叫端印「沒有讀數」而不是「0 場」——
//          把讀取失敗印成 0 場飢餓會讓一個真的空白被隱藏。
// ⚠ 寫入端只有兩處：step=start 推場次（BumpSession）、op=pick 記選中（RecordPick）。
//   **骰面出現不算被選** —— 出現而沒人做，正是「飢餓」這個詞要描述的狀態。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Io;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.FreeTime
{
    /// <summary>一個活動在某 persona 身上的觸發紀錄。</summary>
    public sealed class SCP_FreeTimeActivityStat
    {
        /// <summary>被選中過幾次（累計，不隨場次重置）。</summary>
        public int Picks;
        /// <summary>最後一次被選中發生在第幾場（`sessions_total` 的當時值）。0＝從未被選過。</summary>
        public int LastSession;
        /// <summary>最後一次被選中的 ISO 時刻；空＝從未。</summary>
        public string LastAt = "";
    }

    /// <summary>某 persona 的自由時間活動統計（一個 profile 欄 ＝ 一份本型別）。</summary>
    public sealed class SCP_FreeTimeStats
    {
        /// <summary>這個 persona 一共開過幾場自由時間（飢餓度的分母基準）。</summary>
        public int SessionsTotal;
        /// <summary>最後寫入時刻（ISO）。</summary>
        public string UpdatedAt = "";
        /// <summary>活動 id → 紀錄（id 比對不分大小寫，同 Unity 版）。</summary>
        public Dictionary<string, SCP_FreeTimeActivityStat> Activities
            = new Dictionary<string, SCP_FreeTimeActivityStat>(StringComparer.OrdinalIgnoreCase);
        /// <summary>
        /// false ＝ **檔案讀不到或解析失敗**（不是「這個人還沒跑過自由時間」）。兩者的數字一模一樣（全 0），
        /// 所以差別只能靠這個欄位講。
        /// </summary>
        public bool Loaded;
        /// <summary>讀取失敗的原因（檔不存在時為空 —— 那是「還沒有」，不是錯誤）。</summary>
        public string LoadError = "";

        /// <summary>飢餓度 ＝ 已跑場次 − 最後被選中的場次。從未被選過 ⇒ 等於 SessionsTotal；沒有統計 ⇒ 0（不置頂）。</summary>
        public int Starvation(string iActivityId)
        {
            if (!Loaded || string.IsNullOrEmpty(iActivityId)) return 0;
            int aLast = Activities.TryGetValue(iActivityId, out var aStat) ? aStat.LastSession : 0;
            int aGap = SessionsTotal - aLast;
            return aGap < 0 ? 0 : aGap;   // 負值只可能來自資料被手改，當 0 不當異常
        }

        /// <summary>被選中過幾次（沒有紀錄＝0）。</summary>
        public int Picks(string iActivityId)
            => (iActivityId != null && Activities.TryGetValue(iActivityId, out var aStat)) ? aStat.Picks : 0;
    }

    /// <summary>統計欄的讀寫與置頂判定 —— **本欄唯一的實作**（Cmd 擲骰、活動層記錄、後台頁顯示都走這裡）。</summary>
    public static class SCP_FreeTimeStatsIO
    {
        /// <summary>profile 欄名（＝檔名，`letters/&lt;persona&gt;/profile/&lt;此值&gt;.md`）。跨端契約，⛔ 別改。</summary>
        public const string FieldName = "freetime_activity_stats";

        public static string FieldPath(SCP_LettersRoot iLetters, string iPersona)
            => SCP_LettersPaths.ProfileDir(iLetters, iPersona) + "/" + FieldName + ".md";

        /// <summary>
        /// 讀某 persona 的統計。檔不存在 ⇒ `Loaded=false` 的空統計（**不建檔** —— 讀取不該有副作用，
        /// 而憑空長出來的空檔會讓「沒跑過」與「跑過但沒紀錄」同形）。
        /// </summary>
        public static SCP_FreeTimeStats Load(SCP_LettersRoot iLetters, string iPersona)
        {
            var aRes = new SCP_FreeTimeStats();
            if (string.IsNullOrEmpty(iPersona)) return aRes;
            string aPath = FieldPath(iLetters, iPersona);
            if (!SCP_AtomicFileRead.TryReadAllText(aPath, out string aText, out SCP_FileReadState aState))
            {
                if (aState != SCP_FileReadState.Missing) aRes.LoadError = SCP_AtomicFileRead.DescribeBusy(aPath);
                return aRes;
            }
            try
            {
                SCP_JsonData aJd = SCP_JsonData.Parse(aText);
                if (!aJd.IsObject) { aRes.LoadError = "統計檔不是 JSON 物件：" + aPath; return aRes; }
                aRes.SessionsTotal = aJd.GetInt("sessions_total", 0);
                aRes.UpdatedAt = aJd.GetString("updated_at", "");
                // 🩸 2026-08-24 首次實跑（Unity 版）：空字典被寫成 `"activities":null`，而「鍵存在」不等於「有值」
                //   ⇒ 判定要看**值本身**是不是物件，不是看鍵在不在。
                SCP_JsonData aActs = aJd["activities"];
                if (aActs.IsObject)
                {
                    foreach (string aKey in aActs.Keys)
                    {
                        SCP_JsonData aIt = aActs[aKey];
                        if (!aIt.IsObject) continue;
                        aRes.Activities[aKey] = new SCP_FreeTimeActivityStat
                        {
                            Picks = aIt.GetInt("picks", 0),
                            LastSession = aIt.GetInt("last_session", 0),
                            LastAt = aIt.GetString("last_at", ""),
                        };
                    }
                }
                aRes.Loaded = true;
                return aRes;
            }
            catch (Exception e)
            {
                // fail-soft，但**要留痕** —— 靜默失敗的症狀是「置頂規則好像沒在動」，而那跟「大家都不餓」長得一樣。
                aRes = new SCP_FreeTimeStats { LoadError = $"統計檔解析失敗（{e.GetType().Name}: {e.Message}）：{aPath}" };
                return aRes;
            }
        }

        static void Save(SCP_LettersRoot iLetters, string iPersona, SCP_FreeTimeStats iStats)
        {
            string aPath = FieldPath(iLetters, iPersona);
            var aJd = SCP_JsonData.NewObject();
            aJd["sessions_total"] = iStats.SessionsTotal;
            aJd["updated_at"] = DateTime.UtcNow.ToString("o");
            // ⚠ 沒有內容就**不要寫那個鍵**（Unity 版的空字典會寫成 null，而 null 是讀取端處理不了的第三種狀態）。
            if (iStats.Activities.Count > 0)
            {
                var aActs = SCP_JsonData.NewObject();
                foreach (var aKv in iStats.Activities)
                {
                    var aIt = SCP_JsonData.NewObject();
                    aIt["picks"] = aKv.Value.Picks;
                    aIt["last_session"] = aKv.Value.LastSession;
                    aIt["last_at"] = aKv.Value.LastAt ?? "";
                    aActs[aKv.Key] = aIt;
                }
                aJd["activities"] = aActs;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
            string aTmp = aPath + ".tmp";
            // 壓縮 JSON —— 與 Unity `JsonData.ToJson()` 同形（既有檔就是這個樣子，換格式會讓整批檔逐位元組翻紅）。
            File.WriteAllText(aTmp, SCP_JsonWriter.Write(aJd, false), new UTF8Encoding(false));
            SCP_TextFile.ReplaceOrMove(aTmp, aPath);
        }

        /// <summary>
        /// 開一場自由時間 ⇒ 場次 +1。**這是飢餓度的時鐘** —— 不推它的話「幾場沒被選」永遠是 0，置頂規則會安靜地永不觸發。
        /// </summary>
        /// <returns>推進後的場次總數；寫入失敗回 -1（呼叫端據此印「沒有讀數」），原因在 <paramref name="oError"/>。</returns>
        public static int BumpSession(SCP_LettersRoot iLetters, string iPersona, out string oError)
        {
            oError = "";
            try
            {
                var aStats = Load(iLetters, iPersona);
                // ⚠ 讀不了（不是不存在）就**不寫** —— 拿一份空統計覆寫一份讀不了的檔，等於把那個人的歷史清零。
                if (!aStats.Loaded && aStats.LoadError.Length > 0) { oError = aStats.LoadError; return -1; }
                aStats.SessionsTotal += 1;
                Save(iLetters, iPersona, aStats);
                return aStats.SessionsTotal;
            }
            catch (Exception e) { oError = e.GetType().Name + ": " + e.Message; return -1; }
        }

        /// <summary>記錄一次「選中」（唯一寫入端：活動層 op=pick）。回該活動累計被選次數；失敗回 -1。</summary>
        public static int RecordPick(SCP_LettersRoot iLetters, string iPersona, string iActivityId, out string oError)
        {
            oError = "";
            if (string.IsNullOrEmpty(iPersona) || string.IsNullOrEmpty(iActivityId)) { oError = "persona／activity 是空的"; return -1; }
            try
            {
                var aStats = Load(iLetters, iPersona);
                if (!aStats.Loaded && aStats.LoadError.Length > 0) { oError = aStats.LoadError; return -1; }
                if (!aStats.Activities.TryGetValue(iActivityId, out var aStat))
                {
                    aStat = new SCP_FreeTimeActivityStat();
                    aStats.Activities[iActivityId] = aStat;
                }
                aStat.Picks += 1;
                aStat.LastSession = aStats.SessionsTotal;
                aStat.LastAt = DateTime.UtcNow.ToString("o");
                Save(iLetters, iPersona, aStats);
                return aStat.Picks;
            }
            catch (Exception e) { oError = e.GetType().Name + ": " + e.Message; return -1; }
        }

        /// <summary>
        /// 從一批候選活動 id 裡挑出「該被置頂」的那幾個：飢餓度 ≥ 門檻者取最餓的前 <c>StarveHoistMax</c> 個
        /// （同分**穩定挑 id 序** —— 骰面本來就會再洗牌，不需要第二個隨機源）。
        /// <para>⚠ <paramref name="oOverflow"/> ＝ 符合門檻但沒被頂上來的數量 —— **這格一定要往外傳**：
        /// 「只有 2 項餓」與「有 9 項餓而我只頂 2 項」在骰面上長得一模一樣。</para>
        /// </summary>
        public static Dictionary<string, int> PickStarved(SCP_FreeTimeStats iStats, IEnumerable<string> iCandidateIds,
            SCP_FreeTimeSettings iSettings, out int oOverflow)
        {
            oOverflow = 0;
            var aRes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (iStats == null || !iStats.Loaded || iCandidateIds == null || iSettings == null) return aRes;

            var aQualified = new List<KeyValuePair<string, int>>();
            foreach (string aId in iCandidateIds)
            {
                int aGap = iStats.Starvation(aId);
                if (aGap >= iSettings.StarveThreshold) aQualified.Add(new KeyValuePair<string, int>(aId, aGap));
            }
            aQualified.Sort((a, b) =>
            {
                int aCmp = b.Value.CompareTo(a.Value);          // 餓的在前
                return aCmp != 0 ? aCmp : string.CompareOrdinal(a.Key, b.Key);
            });
            for (int i = 0; i < aQualified.Count; i++)
            {
                if (i < iSettings.StarveHoistMax) aRes[aQualified[i].Key] = aQualified[i].Value;
                else oOverflow++;
            }
            return aRes;
        }

        /// <summary>掃出所有有統計欄的 persona（後台頁用）。判準是**檔案存在**，不是任何名單 —— 名單會跟磁碟漂移。</summary>
        public static List<string> ListPersonasWithStats(SCP_LettersRoot iLetters, List<string>? oWarnings = null)
        {
            var aRes = new List<string>();
            try
            {
                if (!Directory.Exists(iLetters.Value)) { oWarnings?.Add("信件夾根不存在：" + iLetters.Value); return aRes; }
                foreach (string aDir in Directory.GetDirectories(iLetters.Value))
                {
                    string aName = Path.GetFileName(aDir);
                    if (File.Exists(FieldPath(iLetters, aName))) aRes.Add(aName);
                }
                aRes.Sort(StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception e) { oWarnings?.Add("掃 persona 統計失敗：" + e.Message); }
            return aRes;
        }

        /// <summary>把飢餓度講成骰面上的一句話（名字後綴）。</summary>
        public static string StarveSuffix(int iGap, int iPicks)
            => iPicks <= 0
                ? $" 💤 **從未做過**（已 {iGap} 場）—— 要不要試一次？"
                : $" 💤 已 **{iGap} 場**沒選它（累計做過 {iPicks} 次）";
    }
}
