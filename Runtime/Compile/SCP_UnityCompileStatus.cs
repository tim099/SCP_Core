// 區塊職責：讀 Unity 端 `UCL_CompileErrorTracker` 寫的 `.compile_status.json`，
//           並跟**第二來源**（`Assets/DebugLogs~/Errors_latest.log`）交叉對帳。**純讀，零 Unity 依賴。**
// 物理意義：這一層回答的是「**磁碟上那份編譯狀態說什麼**」，不是「我的改動編譯過了嗎」——
//           後者要再加一個基準（見 <see cref="SCP_UnityCompileStatus.IsFresherThan"/>），
//           而把兩者當成同一件事正是 python `check_compile.py --watch` 那隻 bug 的內容
//           （TASK-0154：觸發還沒開始時 `in_progress` 已經是 false ⇒ 回上一次的快照）。
// 數值影響：零寫入。
//
// ⛔ **本層量的是 Unity assemblies，不涵蓋 `senate.exe`。**
//   兩個宿主的尺不同形，而且不可以合成一把（`SCP_CodingExitGateHost` 檔頭已拍板）。
//   🩸 2026-09-07 血證：同一份 `SCP_Cmd_Keys.cs`，Unity 印 **0 errors**（LangVersion 9、nullable 沒開，
//   `?` 只是 CS8632 警告），而 `dotnet build` **CS8603 紅燈**（nullable 開著且警告當錯誤）。
//   ⇒ 呼叫端印結論時**必須同時印射程**，否則綠燈會被讀成「兩邊都過了」。
//
// ⚠ 時間戳的精度是**秒**（tracker 寫 `DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss")`）⇒
//   「這份是不是我這一趟的」不可以只比那個字串：同一秒內觸發時它會等於基準而不是大於。
//   ⇒ 新鮮度用**檔案 mtime**（次秒精度、而且是另一條路徑）判，時間戳只拿來印給人看。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Compile
{
    /// <summary>一則編譯訊息（tracker 的 `messages[]` 一筆）。</summary>
    public sealed class SCP_UnityCompileMessage
    {
        public string assembly = "";
        public string file = "";
        public int line;
        public int column;

        /// <summary>`Error` / `Warning`（tracker 的字面）。</summary>
        public string type = "";

        public string message = "";

        public bool IsError() => string.Equals(type, "Error", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>`.compile_status.json` 的 typed model。⚠ 鍵名打錯只會讀回預設值，而那長得跟「沒有錯誤」一樣。</summary>
    public sealed class SCP_UnityCompileStatus
    {
        public string tracker = "";
        public string timestamp = "";
        public double duration_seconds;
        public bool in_progress;
        public int total_errors;
        public int total_warnings;
        public int total_messages;
        public List<SCP_UnityCompileMessage> messages = new List<SCP_UnityCompileMessage>();
    }

    /// <summary>
    /// 讀一次的結果。<see cref="Found"/> ＝ 檔在不在 —— **「檔不在」與「0 errors」必須不同形**，
    /// 那是這個專案反覆咬人的那一族。
    /// </summary>
    public sealed class SCP_UnityCompileRead
    {
        public bool Found;

        /// <summary>讀的是哪個檔（成功失敗都要有值 —— 失敗訊息不帶路徑，人就得自己猜是哪棵樹）。</summary>
        public string Path = "";

        public SCP_UnityCompileStatus? Status;

        /// <summary>讀失敗的原因（`Found=false` 或解析炸掉）。空 ＝ 沒問題。</summary>
        public string Error = "";

        /// <summary>檔案最後寫入時間（UTC）。⚠ 這是**跟內嵌時間戳不同源**的第二個讀數。</summary>
        public DateTime WriteTimeUtc;
    }

    /// <summary>讀 `.compile_status.json` 與 ErrorLog 交叉對帳。純函式層，呼叫端負責印。</summary>
    public static class SCP_UnityCompile
    {
        /// <summary>狀態檔在資料根底下的檔名（Unity 端 `UCL_CompileErrorTracker` 寫的）。</summary>
        public const string StatusFileName = ".compile_status.json";

        /// <summary>第二來源：Editor 內 ErrorLog 的落檔（相對 Unity 專案根）。</summary>
        public const string ErrorLogRelPath = "Assets/DebugLogs~/Errors_latest.log";

        // ── 讀狀態 ────────────────────────────────────────────────

        /// <summary>讀一次狀態檔。**任何失敗都帶原因回來**，⛔ 不回 null 讓呼叫端自己編故事。</summary>
        public static SCP_UnityCompileRead Read(string iDataRoot)
        {
            string aPath = System.IO.Path.Combine(iDataRoot ?? "", StatusFileName);
            var aOut = new SCP_UnityCompileRead { Path = aPath };
            if (!File.Exists(aPath))
            {
                // 這一句刻意不說「沒有錯誤」：檔不在的成因是「Editor 從沒編譯過／資料根給錯」，
                // 而那兩件事跟「編譯乾淨」的處置完全相反。
                aOut.Error = "找不到編譯狀態檔 —— 這不是「沒有錯誤」，是**沒有讀數**";
                return aOut;
            }
            try
            {
                aOut.WriteTimeUtc = File.GetLastWriteTimeUtc(aPath);
                SCP_JsonData aRoot = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                aOut.Status = SCP_JsonMapper.Create(typeof(SCP_UnityCompileStatus), aRoot) as SCP_UnityCompileStatus;
                if (aOut.Status == null)
                {
                    aOut.Error = "狀態檔解析回 null（內容不是預期的物件）";
                    return aOut;
                }
                aOut.Found = true;
                return aOut;
            }
            catch (Exception e)
            {
                aOut.Error = "讀不動狀態檔：" + e.GetType().Name + ": " + e.Message;
                return aOut;
            }
        }

        /// <summary>
        /// 這份讀數是不是**晚於基準**（＝屬於基準之後那一趟）。
        /// <para>⚠ 用檔案 mtime 不用內嵌時間戳：後者只有秒精度，同一秒內觸發時會等於基準而不是大於，
        /// 而「等於」在那個判準下會被讀成「還沒跑」⇒ 永遠等下去。</para>
        /// </summary>
        public static bool IsFresherThan(SCP_UnityCompileRead iRead, DateTime iBaselineUtc)
            => iRead.Found && iRead.WriteTimeUtc >= iBaselineUtc;

        /// <summary>只留錯誤，並以 (type, file, line, message) 去重（同一顆錯會被重試累積多次）。</summary>
        public static List<SCP_UnityCompileMessage> ErrorsOf(SCP_UnityCompileStatus iStatus)
        {
            var aOut = new List<SCP_UnityCompileMessage>();
            var aSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (SCP_UnityCompileMessage aMsg in iStatus.messages)
            {
                if (!aMsg.IsError()) continue;
                string aKey = aMsg.type + "|" + aMsg.file + "|" + aMsg.line + "|" + aMsg.message;
                if (aSeen.Add(aKey)) aOut.Add(aMsg);
            }
            return aOut;
        }

        // ── 第二來源：ErrorLog 交叉對帳 ────────────────────────────

        /// <summary>對帳結論。⚠ 四種，**兩種「一致」的意思相反**（都沒錯／都有錯）。</summary>
        public enum SCP_CrosscheckVerdict
        {
            /// <summary>找不到第二來源 —— ⛔ 這**不是**一致。</summary>
            NoSecondSource,

            /// <summary>兩邊都沒錯。</summary>
            AgreeClean,

            /// <summary>兩邊都有錯（一致，但不是好消息）。</summary>
            AgreeDirty,

            /// <summary>tracker 說 0 而 ErrorLog 有 —— **以 ErrorLog 為準，這不是 clean compile**。</summary>
            TrackerMissedErrors,

            /// <summary>tracker 有錯而 ErrorLog 沒看到 —— 兩邊射程不同（tracker 看回調、ErrorLog 看落檔）。</summary>
            LogMissedErrors,
        }

        /// <summary>對帳結果：結論 ＋ 第二來源看到幾筆 ＋ 它是哪個檔。</summary>
        public readonly struct SCP_CrosscheckResult
        {
            public SCP_CrosscheckResult(SCP_CrosscheckVerdict iVerdict, int iLogCount, string iLogPath)
            {
                Verdict = iVerdict;
                LogCount = iLogCount;
                LogPath = iLogPath ?? "";
            }

            public SCP_CrosscheckVerdict Verdict { get; }
            public int LogCount { get; }
            public string LogPath { get; }
        }

        /// <summary>
        /// 拿 ErrorLog 當第二來源對一次帳。
        /// <para>物理意義：tracker 走的是編譯回調，而**有些錯只會落到 Editor 內的 ErrorLog**
        /// （tracker 與出錯的檔同屬一個 assembly 時，它自己也編不出來 ⇒ 回調不會來）。
        /// ⇒ 這條是**走不同路徑的證言**，不是同一把尺量兩次。</para>
        /// <para>⚠ ErrorLog 沒有日期只有時分秒 ⇒ 只取「時:分:秒 &gt;= 狀態檔那一刻」的行，
        /// 那是刻意寬鬆的：寧可多算幾筆（會現形），不要漏掉（會變成假綠燈）。</para>
        /// </summary>
        public static SCP_CrosscheckResult Crosscheck(string iProjectRoot, SCP_UnityCompileStatus iStatus)
        {
            string aLogPath = System.IO.Path.Combine(iProjectRoot ?? "", ErrorLogRelPath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!File.Exists(aLogPath))
                return new SCP_CrosscheckResult(SCP_CrosscheckVerdict.NoSecondSource, 0, aLogPath);

            int aCount = CountCompileErrorsSince(aLogPath, TimeOfDayOf(iStatus.timestamp));
            int aTracker = iStatus.total_errors;
            if (aTracker == 0 && aCount > 0) return new SCP_CrosscheckResult(SCP_CrosscheckVerdict.TrackerMissedErrors, aCount, aLogPath);
            if (aTracker == 0 && aCount == 0) return new SCP_CrosscheckResult(SCP_CrosscheckVerdict.AgreeClean, 0, aLogPath);
            if (aCount > 0) return new SCP_CrosscheckResult(SCP_CrosscheckVerdict.AgreeDirty, aCount, aLogPath);
            return new SCP_CrosscheckResult(SCP_CrosscheckVerdict.LogMissedErrors, 0, aLogPath);
        }

        /// <summary>從 `2026-09-07T09:01:53` 取出 `09:01:53`。解不出來回空（⇒ 不做時間過濾，全算）。</summary>
        static string TimeOfDayOf(string iTimestamp)
        {
            if (string.IsNullOrEmpty(iTimestamp)) return "";
            int aT = iTimestamp.IndexOf('T');
            if (aT < 0 || aT + 9 > iTimestamp.Length) return "";
            return iTimestamp.Substring(aT + 1, 8);
        }

        /// <summary>數 ErrorLog 裡「時分秒 &gt;= iSince」且看起來是編譯錯的行。iSince 為空 ⇒ 不過濾。</summary>
        static int CountCompileErrorsSince(string iLogPath, string iSince)
        {
            int aCount = 0;
            try
            {
                foreach (string aLine in File.ReadLines(iLogPath))
                {
                    // 編譯錯的字面是 `error CS<n>:` —— 只認這個，不把 runtime 例外算進來
                    // （那是另一種東西，混進來會讓對帳每天都亮紅燈，然後沒有人再看它）。
                    if (aLine.IndexOf("error CS", StringComparison.Ordinal) < 0) continue;
                    if (iSince.Length > 0)
                    {
                        string aStamp = ExtractTimeOfDay(aLine);
                        // 取不到時間就算進來：漏算會變成假綠燈，多算會被人看到。
                        if (aStamp.Length == 8 && string.CompareOrdinal(aStamp, iSince) < 0) continue;
                    }
                    aCount++;
                }
            }
            catch
            {
                // 讀不動第二來源 ⇒ 當作沒有第二來源（回 0），呼叫端會看到 tracker 那半照印。
                return 0;
            }
            return aCount;
        }

        /// <summary>抓一行裡第一個 `HH:mm:ss`。找不到回空。</summary>
        static string ExtractTimeOfDay(string iLine)
        {
            for (int i = 0; i + 8 <= iLine.Length; i++)
            {
                if (iLine[i + 2] != ':' || iLine[i + 5] != ':') continue;
                if (!IsDigit(iLine[i]) || !IsDigit(iLine[i + 1]) || !IsDigit(iLine[i + 3])
                    || !IsDigit(iLine[i + 4]) || !IsDigit(iLine[i + 6]) || !IsDigit(iLine[i + 7])) continue;
                return iLine.Substring(i, 8);
            }
            return "";
        }

        static bool IsDigit(char iChar) => iChar >= '0' && iChar <= '9';

        // ── 組件新鮮度（TASK-0159）────────────────────────────────

        // ===========================================================
        // 區塊職責：回答「**這一趟編到我改的檔了嗎**」—— 而那跟「編譯有沒有錯」是兩題。
        // 物理意義：`.compile_status.json` 只證明「tracker 又寫了一份」。而 Unity 在
        //   **沒有東西要編**的時候（視窗失焦沒 refresh／剛剛已經編過）照樣會寫一份新的
        //   ⇒ 時間戳晚於基準、`in_progress=false`、`errors=0` —— 那份讀數與「真的編過我的改動」
        //   **在回傳值上完全同形**。
        //   🩸 2026-09-10 實測：改完 5 個 .cs 送 recompile，收到 `clean / 0.25s / warnings 0`
        //     （前一趟同樣的改動是 5.94s / 21 warnings）。分辨它們的不是那份 JSON，
        //     是 `.cs` 與 `.dll` 的 mtime 先後 —— 而那個比對當時是**人手動做的**。
        // ⇒ 本函式把那一步變成讀數：**比最新組件還新的 .cs**，一個都不該有。
        // 數值影響：純檔案系統 stat，不解析內容、不碰 Unity API。
        // 邊界（⚠ 全部寫出來，因為它們的失效樣子都是安靜的）：
        //   · **全域近似**：比的是「所有 .cs 的 mtime」對「**最新那顆** dll 的 mtime」，
        //     ⛔ 不做 per-asmdef 對照（那要解析每個 asmdef 的射程，成本遠大於本症狀）。
        //     ⇒ 已知假陰性：Unity 只重編了 B assembly，而我改的檔在**沒被重編的** A ——
        //     最新 dll 很新，於是 A 那個檔被判成 fresh。⛔ 這一格本版**量不到**。
        //   · 跳過路徑中含 `~` 的目錄（Unity 慣例：那種資料夾不進編譯），否則 `Tools~` 底下
        //     的東西會製造永遠不會消失的假陽性。
        //   · 找不到 `Library/ScriptAssemblies` ⇒ `Measured=false`，**⛔ 不是 0**。
        //     那兩件事的處置相反（一個是「還沒編過」，一個是「已經同步」）。
        // ===========================================================
        public sealed class SCP_UnityStaleResult
        {
            /// <summary>有沒有量到。⛔ `false` 不等於 `StaleCount==0`。</summary>
            public bool Measured;

            /// <summary>沒量到的原因（`Measured=false` 時必有值）。</summary>
            public string Error = "";

            /// <summary>比最新組件還新的 .cs 數。</summary>
            public int StaleCount;

            /// <summary>其中幾個的相對路徑（有上限 —— 全列會把結論淹掉）。</summary>
            public List<string> StaleFiles = new List<string>();

            public DateTime NewestSourceUtc;
            public DateTime NewestAssemblyUtc;
            public string NewestSourcePath = "";
        }

        /// <summary>組件目錄（相對 Unity 專案根）。</summary>
        public const string ScriptAssembliesRelPath = "Library/ScriptAssemblies";

        /// <summary>比對 `Assets/` 下的 .cs 與 `Library/ScriptAssemblies/*.dll` 的 mtime。</summary>
        public static SCP_UnityStaleResult StaleSources(string iProjectRoot, int iMaxList = 5)
        {
            var aOut = new SCP_UnityStaleResult();
            string aRoot = iProjectRoot ?? "";
            string aAsmDir = System.IO.Path.Combine(aRoot, "Library", "ScriptAssemblies");
            if (!Directory.Exists(aAsmDir))
            {
                aOut.Error = "找不到 " + ScriptAssembliesRelPath + " —— **沒有量到**（不是「已經同步」）";
                return aOut;
            }

            DateTime aNewestAsm = DateTime.MinValue;
            foreach (string aDll in Directory.EnumerateFiles(aAsmDir, "*.dll", SearchOption.TopDirectoryOnly))
            {
                DateTime t = File.GetLastWriteTimeUtc(aDll);
                if (t > aNewestAsm) aNewestAsm = t;
            }
            if (aNewestAsm == DateTime.MinValue)
            {
                aOut.Error = ScriptAssembliesRelPath + " 底下一個 .dll 都沒有 —— **沒有量到**";
                return aOut;
            }

            string aAssets = System.IO.Path.Combine(aRoot, "Assets");
            if (!Directory.Exists(aAssets))
            {
                aOut.Error = "找不到 " + aAssets + " —— **沒有量到**";
                return aOut;
            }

            aOut.Measured = true;
            aOut.NewestAssemblyUtc = aNewestAsm;
            foreach (string aCs in Directory.EnumerateFiles(aAssets, "*.cs", SearchOption.AllDirectories))
            {
                if (IsInsideTildeFolder(aCs, aAssets)) continue;
                DateTime t = File.GetLastWriteTimeUtc(aCs);
                if (t > aOut.NewestSourceUtc)
                {
                    aOut.NewestSourceUtc = t;
                    aOut.NewestSourcePath = Relative(aCs, aRoot);
                }
                if (t <= aNewestAsm) continue;
                aOut.StaleCount++;
                if (aOut.StaleFiles.Count < iMaxList) aOut.StaleFiles.Add(Relative(aCs, aRoot));
            }
            return aOut;
        }

        /// <summary>路徑中有沒有以 `~` 結尾的資料夾（Unity 不收那種目錄底下的腳本）。</summary>
        static bool IsInsideTildeFolder(string iFullPath, string iStopAt)
        {
            string? aDir = System.IO.Path.GetDirectoryName(iFullPath);
            while (!string.IsNullOrEmpty(aDir) && aDir.Length >= iStopAt.Length)
            {
                string aName = System.IO.Path.GetFileName(aDir);
                if (aName.EndsWith("~", StringComparison.Ordinal)) return true;
                aDir = System.IO.Path.GetDirectoryName(aDir);
            }
            return false;
        }

        static string Relative(string iFullPath, string iRoot)
        {
            string aFull = iFullPath.Replace(System.IO.Path.DirectorySeparatorChar, '/');
            string aBase = (iRoot ?? "").Replace(System.IO.Path.DirectorySeparatorChar, '/').TrimEnd('/') + "/";
            return aFull.StartsWith(aBase, StringComparison.OrdinalIgnoreCase) ? aFull.Substring(aBase.Length) : aFull;
        }

        /// <summary>把新鮮度結果組成可印的行（⚠ 沒量到、0、&gt;0 三種說法必須不同形）。</summary>
        public static List<string> RenderStale(SCP_UnityStaleResult iStale)
        {
            var aLines = new List<string>();
            if (!iStale.Measured)
            {
                aLines.Add("- 🧭 組件新鮮度：⚪ **沒有量到** —— " + iStale.Error);
                return aLines;
            }
            string aAsm = iStale.NewestAssemblyUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            if (iStale.StaleCount == 0)
            {
                aLines.Add("- 🧭 組件新鮮度：✅ **0 個 .cs 比組件新**（最新組件 " + aAsm
                           + "；最新原始碼 " + iStale.NewestSourceUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                           + "　`" + iStale.NewestSourcePath + "`）");
                return aLines;
            }
            aLines.Add("- 🧭 組件新鮮度：🚨 **" + iStale.StaleCount + " 個 .cs 比最新組件（" + aAsm
                       + "）還新 ⇒ 本讀數不涵蓋它們**");
            foreach (string f in iStale.StaleFiles)
                aLines.Add("    · " + f);
            if (iStale.StaleCount > iStale.StaleFiles.Count)
                aLines.Add("    · …另有 " + (iStale.StaleCount - iStale.StaleFiles.Count) + " 個未列");
            aLines.Add("  ⇒ Unity 可能還沒 import 那些改動（視窗失焦時不會自動重編）。"
                       + "⛔ 這一格為真時，上面的 errors 數字**不是**針對你這次的改動。");
            return aLines;
        }

        // ── 組輸出 ────────────────────────────────────────────────

        /// <summary>本層讀數的射程 —— **每一次印結論都要帶著它**。</summary>
        public const string ScopeLine =
            "⚠ 射程：本讀數涵蓋 **Unity assemblies**；⛔ **不涵蓋 `senate.exe`**"
            + "（那條走 `dotnet build` ／ `build.sh` 出廠驗收）。";

        /// <summary>把一次讀取組成可印的行。<paramref name="iErrorsOnly"/> ＝ 只列 Error。</summary>
        public static List<string> Render(SCP_UnityCompileRead iRead, string iProjectRoot,
                                          bool iErrorsOnly, int iMaxMessages)
        {
            var aLines = new List<string>();
            if (!iRead.Found || iRead.Status == null)
            {
                aLines.Add("✗ " + iRead.Error);
                aLines.Add("  " + iRead.Path);
                return aLines;
            }

            SCP_UnityCompileStatus aStatus = iRead.Status;
            aLines.Add("- 狀態檔時間戳：`" + aStatus.timestamp + "`（秒精度）／檔案 mtime："
                       + iRead.WriteTimeUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
            aLines.Add("- 耗時 " + aStatus.duration_seconds.ToString("0.00", CultureInfo.InvariantCulture)
                       + "s ／ **Errors: " + aStatus.total_errors + "** ／ Warnings: " + aStatus.total_warnings);
            if (aStatus.in_progress)
                aLines.Add("- ⏳ **`in_progress=true`** —— 這份是**編譯中**的快照，數字還會變（不是結論）");

            SCP_CrosscheckResult aCross = Crosscheck(iProjectRoot, aStatus);
            aLines.Add("- 🔍 ErrorLog 對帳：" + DescribeCrosscheck(aCross));

            List<SCP_UnityCompileMessage> aErrors = ErrorsOf(aStatus);
            if (aErrors.Count > 0)
            {
                aLines.Add("");
                int aShown = Math.Min(aErrors.Count, iMaxMessages);
                for (int i = 0; i < aShown; i++)
                {
                    SCP_UnityCompileMessage aMsg = aErrors[i];
                    aLines.Add("  ❌ `" + aMsg.assembly + "` " + aMsg.file + ":" + aMsg.line);
                    aLines.Add("     " + aMsg.message);
                }
                if (aErrors.Count > aShown)
                    aLines.Add("  …另有 " + (aErrors.Count - aShown) + " 個錯未列（顯示上限 " + iMaxMessages + "）");
            }
            else if (!iErrorsOnly && aStatus.total_warnings > 0)
            {
                aLines.Add("  （只列 Error；本次 " + aStatus.total_warnings + " 個 warning 未列）");
            }

            aLines.Add(ScopeLine);
            return aLines;
        }

        static string DescribeCrosscheck(SCP_CrosscheckResult iResult)
        {
            switch (iResult.Verdict)
            {
                case SCP_CrosscheckVerdict.NoSecondSource:
                    return "**無第二來源**（找不到 `" + ErrorLogRelPath + "`）—— ⛔ 這不是「一致」，是只有一個讀數";
                case SCP_CrosscheckVerdict.AgreeClean:
                    return "✅ 一致：**兩邊都沒有錯**";
                case SCP_CrosscheckVerdict.AgreeDirty:
                    return "⚠ 一致：**兩邊都有錯**（ErrorLog " + iResult.LogCount
                           + " 筆）—— 「一致」在這裡不是好消息";
                case SCP_CrosscheckVerdict.TrackerMissedErrors:
                    return "🚨 **不一致（ErrorLog " + iResult.LogCount
                           + " 筆，而 tracker 說 0）** ⇒ 以 ErrorLog 為準：這不是 clean compile";
                default:
                    return "⚠ **反向不一致**：tracker 有錯而 ErrorLog 沒看到 —— 兩邊射程不同"
                           + "（tracker 看這一趟回調，ErrorLog 看落檔），以 tracker 為準";
            }
        }
    }
}
