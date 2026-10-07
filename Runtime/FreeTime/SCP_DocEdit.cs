// 區塊職責：文件編輯類活動的「改完一份 .md 之後」—— 解析目標、驗收它真的動了、指回自由時間流程。
//          三個自由時間活動共用：`doc-reflection`（kind=doc）／`letter-to-self`（kind=letter）／`constitution`。
// 物理意義：TASK-0367 —— 入口是 `senate cmd doc-edit`。
//          ⛔ **刻意不搬檔案內容**（沒有 body 參數、不寫任何 .md）：把整份文件塞進 CLI 參數，
//          等於把編輯器換成一個沒有 diff、沒有復原的通道 —— Tim 的原話是「改完後 CMD 一樣提示下一步」，
//          「改完後」意味著編輯已經發生，本支站在那之後。
// 數值影響：**唯讀**（只 stat 檔案、讀 session 檔與信的 frontmatter）。報告由呼叫端落檔。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.IO;
using System.Text;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Session;

namespace SCP.Core.FreeTime
{
    /// <summary>一次 doc-edit 的結果。<see cref="Blocked"/> 非空 ＝ 被擋（報告照樣完整）。</summary>
    public sealed class SCP_DocEditResult
    {
        public string Report = "";
        public string Blocked = "";
        public string TargetFull = "";
        /// <summary>本場改過了嗎：<c>yes</c>／<c>no</c>／<c>unknown</c>（沒帶 persona、不在自由時間、開場時刻讀不出）。</summary>
        public string Verdict = "unknown";
    }

    public static class SCP_DocEdit
    {
        public static readonly string[] Kinds = { "doc", "letter", "constitution" };

        /// <summary>「自己寫給自己的信」的 frontmatter type —— 與 wake brief 挑最新信同一個值（改了任一邊，另一邊會安靜地挑錯檔）。</summary>
        public const string LetterType = "letter_to_future_self";

        /// <summary>
        /// letters **頂層**不是信的那幾個檔 —— 具名清單，不是前綴規則。
        /// 回傳檔都住 `cmd/` 子目錄；頂層剩下的機器產物是三個**耐久**檔（憲法／見叢／最新信指針）＋README。
        /// </summary>
        static readonly string[] TopLevelNonLetters = { "_constitution.md", "_keys_open.md", "_latest.md", "README.md" };

        /// <param name="iNow">現在（本地時間；selftest 用）。</param>
        public static SCP_DocEditResult Run(SCP_DataRoot iData, SCP_LettersRoot iLetters, string iProjectRoot,
                                            string iKind, string iPersona, string iTargetArg, string iNote, DateTime iNow)
        {
            var r = new SCP_DocEditResult();
            var sb = new StringBuilder();
            sb.Append("# DocEdit kind=").Append(iKind)
              .Append(iPersona.Length == 0 ? "" : " persona=" + iPersona)
              .Append("  ts=`").Append(iNow.ToString("yyyy-MM-dd HH:mm:sszzz")).Append("`（本地時間）\n\n");

            string? aTarget = ResolveTarget(iLetters, iProjectRoot, iKind, iPersona, iTargetArg, out string aHow);

            // ── 驗收：存在／副檔名／在 repo 內 ──
            if (string.IsNullOrEmpty(aTarget))
                return Block(r, sb, iData, iPersona, "找不到目標檔", aHow + "　⇒ 用 --arg target=<.md 路徑> 顯式指定");
            if (!aTarget!.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                return Block(r, sb, iData, iPersona, "目標不是 .md", "target: `" + aTarget + "`");
            // 只認允許範圍內的檔：宿主 repo（文件）＋ 信件根（信、憲法）。範圍外的路徑通常是「另一個宇宙的檔」，那種失敗會回一個看起來正常的讀數。
            // 🩸 TASK-0390：原本只認「專案根」—— 信件根搬到 Valhalla 之後，kind=letter／constitution 一律被擋成「在 repo 之外」。
            string aRepo = Path.GetFullPath(iProjectRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string aLettersDir = Path.GetFullPath(iLetters.Value).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            string aFull = Path.GetFullPath(aTarget);
            if (!aFull.StartsWith(aRepo, StringComparison.OrdinalIgnoreCase) && !aFull.StartsWith(aLettersDir, StringComparison.OrdinalIgnoreCase))
                return Block(r, sb, iData, iPersona, "目標在允許範圍之外", "target: `" + aFull + "`　允許：`" + aRepo + "`、`" + aLettersDir + "`");
            if (!File.Exists(aFull))
                return Block(r, sb, iData, iPersona, "目標檔不存在",
                             "target: `" + aFull + "`　⇒ **本 Cmd 不建檔**（它站在「改完之後」，不負責產生內容）");

            r.TargetFull = aFull;
            DateTime aMtime = File.GetLastWriteTime(aFull);
            long aBytes = new FileInfo(aFull).Length;
            sb.Append("## 目標（讀回的事實）\n");
            sb.Append("- target: `").Append(aFull).Append("`\n");
            sb.Append("- 解析方式: ").Append(aHow).Append('\n');
            sb.Append("- 最後修改: **").Append(aMtime.ToString("yyyy-MM-dd HH:mm:ss")).Append("**　大小: ")
              .Append(aBytes.ToString("N0")).Append(" bytes\n");
            if (iNote.Length > 0) sb.Append("- note: ").Append(iNote).Append('\n');
            sb.Append('\n');

            r.Verdict = AppendSessionVerdict(sb, iData, iPersona, aMtime, iNow);
            // 順序刻意：hint 在最後，讀的人往下讀就看到下一步。
            SCP_FreeTimeHint.Append(sb, iData, iPersona, out _);
            r.Report = sb.ToString();
            return r;
        }

        /// <summary>
        /// 三種 kind 各自的目標解析（**路徑由本層算**），並把「怎麼算出來的」放進 <paramref name="oHow"/>。回 null ＝ 解析不出來。
        /// </summary>
        public static string? ResolveTarget(SCP_LettersRoot iLetters, string iProjectRoot, string iKind, string iPersona,
                                            string iTargetArg, out string oHow)
        {
            if (iKind == "constitution")
            {
                // 憲法是單一檔，target 刻意忽略：允許覆寫的話，「改自己的憲法」就變成「可以改任何檔」。
                oHow = "constitution 固定指向該 persona 自己的 `_constitution.md`（忽略 target 參數）";
                return SCP_LettersPaths.ConstitutionPath(iLetters, iPersona);
            }
            if (iTargetArg.Length > 0)
            {
                oHow = "由 --arg target 顯式指定";
                return Path.IsPathRooted(iTargetArg) ? iTargetArg : Path.Combine(iProjectRoot, iTargetArg);
            }
            if (iKind == "letter")
            {
                // 沒給 target ⇒ 取 letters 頂層**最新一封自己寫給自己的信**。⚠ 只看頂層：wakes/ rests/ 等子目錄是別的東西。
                // 🩸 只靠檔名排除不夠：舊位置還留著回傳檔殘影，mtime 可能比真信新 ⇒ 判準是 frontmatter `type`。
                string aDir = SCP_LettersPaths.PersonaDir(iLetters, iPersona);
                if (!Directory.Exists(aDir)) { oHow = "letters 目錄不存在：`" + aDir + "`"; return null; }
                string? aNewest = null;
                DateTime aBest = DateTime.MinValue;
                int aSkippedNamed = 0, aSkippedNotLetter = 0;
                foreach (string f in Directory.GetFiles(aDir, "*.md"))
                {
                    if (IsNonLetterTopLevel(Path.GetFileName(f))) { aSkippedNamed++; continue; }
                    if (SCP_LetterText.ReadFrontmatterField(f, "type") != LetterType) { aSkippedNotLetter++; continue; }
                    DateTime aT = File.GetLastWriteTime(f);
                    if (aT > aBest) { aBest = aT; aNewest = f; }
                }
                string aSkip = "排除 " + aSkippedNamed + " 個具名耐久檔／README、"
                               + aSkippedNotLetter + " 個非 `" + LetterType + "`（舊位置回傳檔殘影／同事來信）";
                oHow = aNewest == null
                    ? "letters 頂層沒有任何「自己寫給自己的信」（" + aSkip + "）：`" + aDir + "`"
                    : "letter 未給 target ⇒ 取 letters 頂層最新的信（" + aSkip + "、不遞迴子目錄）";
                return aNewest;
            }
            oHow = "kind=doc 需要顯式 target";
            return null;
        }

        static bool IsNonLetterTopLevel(string iFileName)
        {
            foreach (string n in TopLevelNonLetters)
                if (iFileName.Equals(n, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>
        /// 拿自由時間 session 的開場時刻當基準，回答「這份檔在本場真的被改過嗎」。
        /// 沒有基準（沒帶 persona／不在自由時間）⇒ **只印 mtime 不下判斷**；判「沒動過」時**不擋**（本支是登記不是收銀台）。
        /// </summary>
        static string AppendSessionVerdict(StringBuilder ioR, SCP_DataRoot iData, string iPersona, DateTime iMtime, DateTime iNow)
        {
            ioR.Append("## 本場改過了嗎\n");
            if (iPersona.Length == 0)
            {
                ioR.Append("- ⚪ **沒帶 persona，不下判斷** —— 只有上面那個 mtime 是事實。要驗「本場改過沒」請帶 `--arg persona=<名字>`。\n");
                return "unknown";
            }
            SCP_ActivitySession? aRunning = SCP_ActivitySessionStore.FindRunning(iData, iPersona, iNow);
            SCP_ActivitySession? aFt = aRunning != null && aRunning.kind == SCP_ActivitySessionKind.FreeTime ? aRunning : null;
            if (aFt == null)
            {
                ioR.Append("- ⚪ **").Append(iPersona).Append(" 不在自由時間中，沒有基準可比** —— 只有 mtime 是事實。")
                   .Append("（掃描範圍：").Append(string.Join(" / ", SCP_ActivitySessionKind.Kinds)).Append("）\n");
                return "unknown";
            }
            DateTime? aStart = SCP_ActivitySession.ParseIsoToLocal(aFt.start_ts);
            if (!aStart.HasValue)
            {
                ioR.Append("- ⚪ session 的 start_ts 解析不出來（`").Append(aFt.start_ts).Append("`）—— 不下判斷。\n");
                return "unknown";
            }
            if (iMtime >= aStart.Value)
            {
                ioR.Append("- ✅ **本場改過** —— mtime ").Append(iMtime.ToString("HH:mm:ss")).Append(" 晚於本場開場 ")
                   .Append(aStart.Value.ToString("HH:mm:ss")).Append("（session `").Append(aFt.session_id).Append("`）\n");
                return "yes";
            }
            ioR.Append("- ⚠ **本場沒動過這份檔** —— mtime ").Append(iMtime.ToString("HH:mm:ss")).Append(" 早於本場開場 ")
               .Append(aStart.Value.ToString("HH:mm:ss"))
               .Append("。**沒擋你**（本 Cmd 是登記不是收銀台），但這一步在帳上就是「登記了一份沒被改的檔」。\n");
            return "no";
        }

        // 被擋也要有完整報告 —— 只回一行錯誤的話，「哪個路徑、怎麼算出來的」全都不見了（那正是最需要看的）。
        static SCP_DocEditResult Block(SCP_DocEditResult r, StringBuilder sb, SCP_DataRoot iData, string iPersona,
                                       string iReason, string iDetail)
        {
            sb.Append("## blocked\n- reason: ").Append(iReason).Append("\n- ").Append(iDetail).Append('\n');
            SCP_FreeTimeHint.Append(sb, iData, iPersona, out _);
            r.Blocked = iReason;
            r.Report = sb.ToString();
            return r;
        }
    }
}
