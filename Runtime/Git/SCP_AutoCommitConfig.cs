// 區塊職責：自動 commit 的**分群設定檔**（`.ucl_autocommit.json`，放各 repo 根）——
//          讓「這個 repo 的機器生成檔怎麼分群」由該 repo 自己宣告，而不是寫死在程式碼裡。
// 物理意義：檔名與欄位是讀者面介面，⛔ **不要隨手改**
//          （`Enabled` / `Name` / `Groups[].Key|Label|MatchPrefixes|Message|DefaultOn`）——
//          磁碟上已經有 Canvas／Chess／Tasks 三份，改名＝既有設定檔讀不回來。
//          ⭐ 「可宣告、但掀不動地板」（Tim 2026-08-21）：設定檔**入版控、由該 repo 擁有、改動在 diff 裡看得見**，
//            所以它不是當年被否決的那種「執行期參數」。地板由 SCP_AutoCommitRules.Classify 的判定順序保證。
// 數值影響：
//   · 讀檔**三態不同形**：沒有檔（Missing，合法）／讀到了（Ok）／壞掉（Error，要說出來）——
//     「設定寫錯」與「這個 repo 沒設定」必須是兩種可分辨的結果。
//   · 寫檔：先 Validate（不合法就不寫）→ temp → 原子取代 → 回讀比對。
//     **未知的頂層欄位原樣保留**（Coding_Standards §2.2 ③）——反序列化丟掉的東西，序列化就再也寫不回來。
//   · 只吃**前綴清單**，不吃 regex —— 設定檔要比 code **更受限**，不是更自由。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。JSON 一律走 SCP_Json（§2）。
// @doc-sync: <SCP_Core>/Docs~/AutoCommit.md（設定檔格式／地板表）
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Io;
using SCP.Core.Json;

namespace SCP.Core.Git
{
    /// <summary>設定檔裡的一群。對應 <see cref="SCP_AutoCommitGroupDef"/>，但 Match 只能是前綴清單。</summary>
    public sealed class SCP_AutoCommitGroupConfig
    {
        /// <summary>群 key（commit 分組用；不可與 `__other` / `__other_untracked` / `__subptr` 撞名）。</summary>
        public string Key { get; set; } = "";
        /// <summary>畫面上顯示的群名。作者自己寫。</summary>
        public string Label { get; set; } = "";
        /// <summary>相對 repo root 的**正斜線前綴**清單，任一命中即屬本群。空字串不合法（會吃掉整個 repo）。</summary>
        public List<string> MatchPrefixes { get; set; } = new List<string>();
        /// <summary>commit 訊息主體（檔數統計由呼叫端補在後面）。</summary>
        public string Message { get; set; } = "";
        /// <summary>沒指定群時是否納入。</summary>
        public bool DefaultOn { get; set; } = true;
    }

    /// <summary>讀設定檔的三態。</summary>
    public enum SCP_AutoCommitConfigState
    {
        /// <summary>沒有設定檔 —— 合法狀態（這個 repo 沒有加入自動 commit）。</summary>
        Missing = 0,
        /// <summary>讀到了、解析成功。</summary>
        Ok = 1,
        /// <summary>檔在但讀不了／解析失敗 —— ⛔ **不是**「沒有設定」。</summary>
        Error = 2,
    }

    /// <summary>一次讀檔的結果（值與「為什麼」一起回）。</summary>
    public sealed class SCP_AutoCommitConfigLoad
    {
        public SCP_AutoCommitConfigState State;
        public SCP_AutoCommitConfig? Config;
        public string Error = "";
        public string Path = "";
    }

    /// <summary>一個 repo 的自動提交設定。檔案位置：<c>&lt;repoRoot&gt;/.ucl_autocommit.json</c>。</summary>
    public sealed class SCP_AutoCommitConfig
    {
        /// <summary>設定檔檔名（放 repo 根）。⚠ 名字沿用 UCL 時代的 —— 那是磁碟上的契約，不是品牌。</summary>
        public const string FileName = ".ucl_autocommit.json";

        /// <summary>
        /// 這個 repo 的自動提交是否啟用。
        /// <para>⚠ **欄位缺席＝視為啟用**（`true`）—— 相容 2026-08-21 之前寫的設定檔，
        /// 那時的語意是「有設定檔就納管」。而**新建的檔一律顯式寫 `false`**（見 <see cref="CreateDefault"/>）：
        /// 「選了一個 submodule」不等於「同意開始自動 commit 它」。</para>
        /// </summary>
        public bool Enabled { get; set; } = true;

        /// <summary>顯示用的 repo 名稱；空的話呼叫端用目錄名。</summary>
        public string Name { get; set; } = "";

        /// <summary>這個 repo 的分群（順序即優先序，第一個命中的收走）。</summary>
        public List<SCP_AutoCommitGroupConfig> Groups { get; set; } = new List<SCP_AutoCommitGroupConfig>();

        /// <summary>
        /// 替還沒有設定檔的 repo 造一份**預設停用**的骨架（不寫檔，呼叫端決定何時 Save）。
        /// <para>預設停用是刻意的：自動創建若順便啟用，等於「選一下」就讓一個 repo 開始被自動 commit ——
        /// 而那種同意應該是顯式的，不是選取的副作用。</para>
        /// </summary>
        public static SCP_AutoCommitConfig CreateDefault(string iName)
            => new SCP_AutoCommitConfig { Name = iName ?? "", Enabled = false };

        public static string PathOf(string iRepoRoot)
            => System.IO.Path.Combine(iRepoRoot, FileName).Replace('\\', '/');

        public static bool Exists(string iRepoRoot)
            => !string.IsNullOrEmpty(iRepoRoot) && File.Exists(PathOf(iRepoRoot));

        /// <summary>讀設定檔（三態，見 <see cref="SCP_AutoCommitConfigState"/>）。⛔ 不丟例外。</summary>
        public static SCP_AutoCommitConfigLoad Load(string iRepoRoot)
        {
            var aOut = new SCP_AutoCommitConfigLoad { Path = PathOf(iRepoRoot) };
            if (!SCP_AtomicFileRead.TryReadAllText(aOut.Path, out string aText, out SCP_FileReadState aState))
            {
                if (aState == SCP_FileReadState.Missing) { aOut.State = SCP_AutoCommitConfigState.Missing; return aOut; }
                aOut.State = SCP_AutoCommitConfigState.Error;
                aOut.Error = SCP_AtomicFileRead.DescribeBusy(aOut.Path);
                return aOut;
            }
            try
            {
                SCP_JsonData aData = SCP_JsonParser.Parse(aText);
                if (!aData.IsObject) throw new FormatException("根值不是物件");
                var aConfig = new SCP_AutoCommitConfig();
                var aOpt = new SCP_JsonMapOptions();
                SCP_JsonMapper.Populate(aConfig, aData, aOpt);
                // ⚠ 型別不合的欄位 mapper 會**不寫並記一筆** —— 那等於那一格被靜默換成預設值，
                //   而那正是本類別最怕的形狀（`"Enabled": "false"` 讀成 true）。⇒ 有一筆就判壞檔。
                if (aOpt.Diagnostics.Count > 0)
                    throw new FormatException(string.Join("；", aOpt.Diagnostics));
                aOut.Config = aConfig;
                aOut.State = SCP_AutoCommitConfigState.Ok;
            }
            catch (Exception e)
            {
                aOut.State = SCP_AutoCommitConfigState.Error;
                aOut.Error = $"{e.GetType().Name}: {e.Message}";
            }
            return aOut;
        }

        /// <summary>
        /// 寫回設定檔。回傳（成功, 人可讀的說法）—— **失敗一定有話說，⛔ 不丟例外**。
        /// <para>順序：Validate → 讀舊檔（保留未知頂層欄位與行尾）→ temp → 原子取代 → 回讀比對。</para>
        /// </summary>
        public (bool ok, string message) Save(string iRepoRoot)
        {
            var aErrors = Validate();
            if (aErrors.Count > 0) return (false, "設定不合法，未寫入：" + string.Join("；", aErrors));

            string aPath = PathOf(iRepoRoot);
            try
            {
                SCP_JsonData aRoot = SCP_JsonData.NewObject();
                bool aCrLf = false;
                if (SCP_AtomicFileRead.TryReadAllText(aPath, out string aOld, out SCP_FileReadState aState))
                {
                    aCrLf = aOld.IndexOf("\r\n", StringComparison.Ordinal) >= 0;
                    // 舊檔壞掉就**不寫** —— 覆蓋掉的是別人寫的東西，而那筆改動沒有地方留得住。
                    SCP_JsonData aOldData;
                    try { aOldData = SCP_JsonParser.Parse(aOld); }
                    catch (Exception e) { return (false, $"舊的設定檔解析不了，⛔ 不覆蓋：{e.Message}"); }
                    if (!aOldData.IsObject) return (false, "舊的設定檔根值不是物件，⛔ 不覆蓋");
                    aRoot = aOldData;
                }
                else if (aState == SCP_FileReadState.Busy)
                {
                    return (false, SCP_AtomicFileRead.DescribeBusy(aPath) + " —— 未寫入");
                }

                // 已知欄位逐格蓋上（Set 保留既有 key 的位置），未知欄位原樣留著。
                SCP_JsonData aNew = SCP_JsonMapper.ToJson(this);
                foreach (string aKey in aNew.Keys) aRoot.Set(aKey, aNew[aKey]);

                string aText = SCP_JsonWriter.Write(aRoot, SCP_JsonStyle.Default.WithIndent("  ")) + "\n";
                if (aCrLf) aText = aText.Replace("\r\n", "\n").Replace("\n", "\r\n");
                string aTmp = aPath + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
                File.WriteAllText(aTmp, aText, new UTF8Encoding(false));
                SCP_TextFile.ReplaceOrMove(aTmp, aPath);

                // 回讀才算數：「我寫了」不是「它在裡面」。
                var aBack = Load(iRepoRoot);
                if (aBack.State != SCP_AutoCommitConfigState.Ok || aBack.Config == null)
                    return (false, "寫完回讀失敗：" + aBack.Error);
                if (SCP_JsonMapper.ToJson(aBack.Config).ToJson(false) != aNew.ToJson(false))
                    return (false, "寫完回讀的內容跟要寫的不一樣（可能有人同時在寫這個檔）");
                return (true, "✅ 已寫入 " + aPath);
            }
            catch (Exception e)
            {
                return (false, $"寫入失敗：{e.GetType().Name}: {e.Message}");
            }
        }

        /// <summary>合法性檢查。回傳空清單＝過。**寫入前必跑** —— 錯配等級是「檔進錯 commit」。</summary>
        public List<string> Validate()
        {
            var aErrors = new List<string>();
            var aSeen = new HashSet<string>();
            for (int i = 0; i < Groups.Count; ++i)
            {
                var aGroup = Groups[i];
                string aWhere = $"第 {i + 1} 群";
                if (aGroup == null) { aErrors.Add($"{aWhere}：空的"); continue; }
                if (string.IsNullOrWhiteSpace(aGroup.Key)) aErrors.Add($"{aWhere}：Key 不可空白");
                else
                {
                    if (SCP_AutoCommitRules.IsReservedKey(aGroup.Key))
                        aErrors.Add($"{aWhere}：Key '{aGroup.Key}' 是保留群，不可自訂");
                    if (!aSeen.Add(aGroup.Key)) aErrors.Add($"{aWhere}：Key '{aGroup.Key}' 重複");
                }
                if (string.IsNullOrWhiteSpace(aGroup.Message)) aErrors.Add($"{aWhere}：Message 不可空白（那是 commit 訊息）");
                int aValidPrefix = 0;
                foreach (string aPrefix in aGroup.MatchPrefixes)
                {
                    if (string.IsNullOrEmpty(aPrefix))
                    {
                        // 空前綴會 StartsWith 命中**每一個檔** ⇒ 一群吃掉整個 repo，而它不會報錯。
                        aErrors.Add($"{aWhere}：前綴不可為空字串（會吃掉整個 repo）");
                        continue;
                    }
                    if (aPrefix.IndexOf('\\') >= 0)
                        aErrors.Add($"{aWhere}：前綴 '{aPrefix}' 含反斜線 —— 比對用的是正斜線相對路徑");
                    ++aValidPrefix;
                }
                if (aValidPrefix == 0) aErrors.Add($"{aWhere}：至少要一個前綴");
            }
            if (Enabled && Groups.Count == 0)
                aErrors.Add("已啟用但沒有任何分群 —— 那會是「開著卻永遠收不到東西」，"
                            + "跟停用不可分辨。要嘛加一群，要嘛把 Enabled 關掉");
            return aErrors;
        }

        /// <summary>轉成分群規則。Match 一律是「正斜線相對路徑的前綴命中」。</summary>
        public SCP_AutoCommitGroupDef[] ToGroupDefs()
        {
            var aDefs = new List<SCP_AutoCommitGroupDef>();
            foreach (var aGroup in Groups)
            {
                if (aGroup == null || string.IsNullOrWhiteSpace(aGroup.Key)) continue;
                var aPrefixes = new List<string>();
                foreach (string aPrefix in aGroup.MatchPrefixes)
                    if (!string.IsNullOrEmpty(aPrefix)) aPrefixes.Add(aPrefix);
                aDefs.Add(new SCP_AutoCommitGroupDef
                {
                    Key = aGroup.Key,
                    Label = string.IsNullOrEmpty(aGroup.Label) ? aGroup.Key : aGroup.Label,
                    Match = p =>
                    {
                        foreach (string aPrefix in aPrefixes)
                            if (p.StartsWith(aPrefix, StringComparison.Ordinal)) return true;
                        return false;
                    },
                    Message = aGroup.Message,
                    DefaultOn = aGroup.DefaultOn,
                });
            }
            return aDefs.ToArray();
        }

        /// <summary>
        /// 列出 `&lt;repoRoot&gt;/.gitmodules` 宣告的**所有** submodule（絕對路徑，正斜線；不管有沒有設定檔）。
        /// <para>⚠ 目錄不存在的那幾顆**不列**（沒 init 的 submodule 沒有東西可收）。</para>
        /// </summary>
        public static List<string> ListSubmodulePaths(string iRepoRoot)
        {
            var aList = new List<string>();
            if (string.IsNullOrEmpty(iRepoRoot) || !Directory.Exists(iRepoRoot)) return aList;
            string aRoot = iRepoRoot.Replace('\\', '/').TrimEnd('/');
            string aGitModules = aRoot + "/.gitmodules";
            if (!File.Exists(aGitModules)) return aList;
            foreach (string aRawLine in File.ReadAllLines(aGitModules))
            {
                string aLine = aRawLine.Trim();
                if (!aLine.StartsWith("path", StringComparison.Ordinal)) continue;
                int aEq = aLine.IndexOf('=');
                if (aEq < 0) continue;
                // ⚠ `path` 之後到 `=` 之間只准有空白 —— 否則 `pathfoo = x` 也會被當成 path。
                if (aLine.Substring(4, aEq - 4).Trim().Length > 0) continue;
                string aRel = aLine.Substring(aEq + 1).Trim();
                if (aRel.Length == 0) continue;
                string aDir = (aRoot + "/" + aRel).Replace('\\', '/');
                if (!Directory.Exists(aDir)) continue;
                aList.Add(aDir);
            }
            aList.Sort(StringComparer.Ordinal);
            return aList;
        }
    }
}
