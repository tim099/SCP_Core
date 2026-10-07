// 區塊職責：**@ 的別名表**（`<ChatTavern>/mention_aliases.json`：別名 → persona／identity id）—— TASK-0365。
// 物理意義：`@酒保` 與 `@tavern-keeper` 要是同一個人（Tim 2026-10-03），而別名要在後台設定、不寫死在程式碼裡。
//           讀的人只有一個：寫入端的 @ 判定（SCP_TavernMentions.Extract）；寫的人只有一個：TrySave（後台頁走它）。
// 數值影響：只影響「這則 @ 到誰」；本名（id）永遠有效、不用列進來。
// ⚠ 會讓 @ **安靜地送錯人或沒送到**的設定，存檔前一律擋（⛔ 不在讀取時默默略過）：
//   跟任何本名或別的別名撞名（大小寫不分）／空白／含 @、＠、空白字元／指向不存在的 id。
// ⚠ 讀不了（壞檔）與沒有檔不同形：沒有檔 ⇒ 空表、沒有錯；壞檔 ⇒ 空表＋錯誤訊息，⛔ TrySave 不覆蓋它（要人先看）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using SCP.Core.Io;
using SCP.Core.Json;

namespace SCP.Core.Tavern
{
    public static class SCP_TavernMentionAliases
    {
        public const string FileName = "mention_aliases.json";

        /// <summary>別名表路徑 —— 跟 identities.json 同一層（從同一個決定點推，⛔ 不自己再組一份）。</summary>
        public static string PathOf(string iDataRoot)
            => SCP.Core.Paths.SCP_DataPaths.ChatTavern(new SCP.Core.Paths.SCP_DataRoot(iDataRoot)).Replace('\\', '/') + "/" + FileName;

        /// <summary>讀別名表（別名 → id，別名大小寫不分）。沒有檔 ⇒ 空表、oError=null；讀不了 ⇒ 空表＋oError。</summary>
        public static Dictionary<string, string> Load(string iDataRoot, out string? oError)
        {
            oError = null;
            var aOut = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string aPath = PathOf(iDataRoot);
            if (!File.Exists(aPath)) return aOut;
            try
            {
                SCP_JsonData aJson = SCP_JsonParser.Parse(File.ReadAllText(aPath));
                if (!aJson.Contains("aliases") || !aJson["aliases"].IsObject) { oError = $"{aPath} 沒有 aliases 物件"; return aOut; }
                SCP_JsonData aMap = aJson["aliases"];
                foreach (string aKey in aMap.Keys)
                {
                    if (!aMap[aKey].IsString) { oError = $"{aPath} 的 aliases.{aKey} 不是字串"; return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase); }
                    aOut[aKey] = aMap[aKey].AsString();
                }
                return aOut;
            }
            catch (Exception e)
            {
                oError = $"{aPath} 讀不了：{e.GetType().Name}: {e.Message}";
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// 檢查別名表；回傳每一條問題（空 ＝ 可以存）。<paramref name="iKnownIds"/> ＝ 白名單（identities ∪ persona 池）。
        /// </summary>
        public static List<string> Validate(IEnumerable<KeyValuePair<string, string>> iAliases, ICollection<string> iKnownIds)
        {
            var aErrors = new List<string>();
            var aKnown = new HashSet<string>(iKnownIds, StringComparer.OrdinalIgnoreCase);
            var aSeen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> kv in iAliases)
            {
                string a = kv.Key ?? "", t = kv.Value ?? "";
                if (a.Trim().Length == 0) { aErrors.Add("有一條別名是空白"); continue; }
                if (a.IndexOf('@') >= 0 || a.IndexOf('＠') >= 0) { aErrors.Add($"別名「{a}」含 @（寫名字就好，@ 由比對端處理）"); continue; }
                if (a.Any(char.IsWhiteSpace)) { aErrors.Add($"別名「{a}」含空白（@ 後面遇到空白就斷了，永遠比對不到）"); continue; }
                if (aKnown.Contains(a)) { aErrors.Add($"別名「{a}」跟本名 {a} 撞名 ⇒ `@{a}` 會同時算成兩個人"); continue; }
                if (aSeen.TryGetValue(a, out string? aPrev)) { aErrors.Add($"別名「{a}」重複（{aPrev} 與 {t}）"); continue; }
                aSeen[a] = t;
                if (t.Trim().Length == 0) aErrors.Add($"別名「{a}」沒有指向任何人");
                else if (!aKnown.Contains(t)) aErrors.Add($"別名「{a}」指向的 {t} 不在白名單（identities／persona）裡");
            }
            return aErrors;
        }

        /// <summary>
        /// 檢查後寫入，再讀回比對。被擋 ⇒ 回 false、檔案不動。現有檔讀不了 ⇒ 也擋（⛔ 不安靜蓋掉一份壞檔）。
        /// </summary>
        public static bool TrySave(string iDataRoot, IReadOnlyDictionary<string, string> iAliases, ICollection<string> iKnownIds, out List<string> oErrors)
        {
            oErrors = Validate(iAliases, iKnownIds);
            if (oErrors.Count > 0) return false;
            Load(iDataRoot, out string? aLoadErr);
            if (aLoadErr != null) { oErrors.Add("現有的別名表讀不了，先修好或刪掉再存（⛔ 不覆蓋）：" + aLoadErr); return false; }
            var aMap = SCP_JsonData.NewObject();
            foreach (KeyValuePair<string, string> kv in iAliases.OrderBy(x => x.Key, StringComparer.Ordinal)) aMap.Set(kv.Key.Trim(), kv.Value.Trim());
            var aRoot = SCP_JsonData.NewObject();
            aRoot.Set("aliases", aMap);
            string aPath = PathOf(iDataRoot);
            try
            {
                string? aDir = Path.GetDirectoryName(aPath);
                if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir);
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(aRoot) + "\n", new UTF8Encoding(false));
                SCP_TextFile.ReplaceOrMove(aTmp, aPath);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                oErrors.Add("寫不進去：" + e.Message);
                return false;
            }
            Dictionary<string, string> aBack = Load(iDataRoot, out string? aBackErr);
            if (aBackErr != null || aBack.Count != iAliases.Count || iAliases.Any(kv => !aBack.TryGetValue(kv.Key.Trim(), out string? v) || v != kv.Value.Trim()))
            {
                oErrors.Add("寫完讀回來對不上：" + (aBackErr ?? $"讀回 {aBack.Count} 條、要寫 {iAliases.Count} 條"));
                return false;
            }
            return true;
        }
    }
}
