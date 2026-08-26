// 區塊職責：child process 註冊中心 —— 本程式開的每一顆外部 process 都在此登記，
//           以「每 process 一個 json 檔」持久化，**跨 process 生命週期**仍能接管既有 process。
// 物理意義：解三族問題（概念取自 Unity 端的 UCL_ProcessRegistryService，Tim 2026-07-27 拍板）：
//           ① 多顆同功能 daemon 併跑互踩
//           ② 宿主重啟／domain reload 後失去 Process 物件 ⇒ 那顆變成沒人管得到的孤兒
//           ③ 光憑 PID 誤殺別人 —— **PID 會被 OS 回收再發**
//           ⇒ 身分＝PID ＋ process name ＋ start time (UTC) **三重比對**。
//             start time 是 kernel 記的，同一個 PID 的不同世代必不同 ⇒ 那是唯一可靠的世代標記。
//           ⚠ CLI 型宿主（一次呼叫一個 process）比 Editor 更需要這個：沒有常駐記憶體，
//             「上一次跑到哪」只能存在檔案裡。
// 數值影響：寫 <see cref="RegistryDir"/> 底下的記錄檔（runtime 狀態，**不該入版控**）。
//           kill 只對身分驗證 Alive 的下手；PidReused / Unknown 一律拒絕動手（那才是本檔存在的理由）。
//           每 process 單檔（`<tag>_<pid>.json`）而不是集中一檔 —— 併發寫不互蓋，單檔壞不連坐。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）—— 不用 record、不用檔案級 namespace。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Proc
{
    /// <summary>
    /// 已註冊 process 的身分驗證結果。
    /// <para>四態刻意分開 —— 把 <see cref="PidReused"/> / <see cref="Unknown"/> 併進
    /// <see cref="Dead"/> 就等於允許誤殺別人的 process，而那個錯誤**完全靜默**。</para>
    /// </summary>
    public enum SCP_ProcessStatus
    {
        /// <summary>拿不到對方資訊（權限不足／剛退出的 race）—— 保守，**不可 kill**。</summary>
        Unknown = 0,

        /// <summary>PID 活著且 name / start time 都吻合 ⇒ 確定是本尊。</summary>
        Alive,

        /// <summary>PID 不存在或已退出。</summary>
        Dead,

        /// <summary>PID 活著但 name 或 start time 不吻合 ⇒ 已被 OS 回收再發給別人，**絕不可 kill**。</summary>
        PidReused,
    }

    /// <summary>單顆已註冊 process 的持久化記錄（對應 <c>&lt;tag&gt;_&lt;pid&gt;.json</c>）。</summary>
    public sealed class SCP_ProcessRecord
    {
        public int Pid;
        public string ProcessName = "";

        /// <summary>啟動時間（UTC，ISO-8601 round-trip）—— PID 再利用判定的關鍵身分欄。</summary>
        public string StartTimeUtcText = "";

        /// <summary>這顆 process 在做什麼（穩定識別字，例 <c>"git_submodule_sync"</c>）—— 也是檔名前綴。</summary>
        public string Tag = "";

        /// <summary>給人看的一句（哪一頁／哪個動作開的）。</summary>
        public string Description = "";

        public string CommandLine = "";
        public string RegisteredBy = "";
        public string RegisteredAtUtcText = "";

        /// <summary>來源記錄檔的絕對路徑（載入時回填，**不序列化**）。</summary>
        public string SourceFile = "";

        /// <summary>解析過的啟動時間；解析不了回 null（⚠ 不要回 default(DateTime)，那會被當成 1 年）。</summary>
        public DateTime? StartTimeUtc
        {
            get
            {
                DateTime aTime;
                if (DateTime.TryParse(StartTimeUtcText, CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind, out aTime))
                    return aTime.ToUniversalTime();
                return null;
            }
        }

        public SCP_JsonData ToJson()
        {
            var aData = SCP_JsonData.NewObject();
            aData.Set("pid", SCP_JsonData.NewNumber(Pid));
            aData.Set("process_name", SCP_JsonData.NewString(ProcessName));
            aData.Set("start_time_utc", SCP_JsonData.NewString(StartTimeUtcText));
            aData.Set("tag", SCP_JsonData.NewString(Tag));
            aData.Set("description", SCP_JsonData.NewString(Description));
            aData.Set("command_line", SCP_JsonData.NewString(CommandLine));
            aData.Set("registered_by", SCP_JsonData.NewString(RegisteredBy));
            aData.Set("registered_at_utc", SCP_JsonData.NewString(RegisteredAtUtcText));
            aData.Set("schema_version", SCP_JsonData.NewNumber(1));
            return aData;
        }

        public static SCP_ProcessRecord? FromJson(SCP_JsonData? iData)
        {
            if (iData == null || !iData.Exists) return null;
            var aRec = new SCP_ProcessRecord();
            aRec.Pid = iData.GetInt("pid", 0);
            aRec.ProcessName = iData.GetString("process_name", "");
            aRec.StartTimeUtcText = iData.GetString("start_time_utc", "");
            aRec.Tag = iData.GetString("tag", "");
            aRec.Description = iData.GetString("description", "");
            aRec.CommandLine = iData.GetString("command_line", "");
            aRec.RegisteredBy = iData.GetString("registered_by", "");
            aRec.RegisteredAtUtcText = iData.GetString("registered_at_utc", "");
            return aRec;
        }
    }

    /// <summary>
    /// child process 註冊中心。開外部 process 之後**必須經此登記**，
    /// 以檔案持久化撐過宿主重啟；kill 前做 PID ＋ name ＋ start time 三重驗證防誤殺。
    /// </summary>
    public static class SCP_ProcessRegistry
    {
        /// <summary>
        /// start time 比對容差（秒）—— <c>Process.StartTime</c> 與記錄值之間的時鐘／精度緩衝。
        /// <para>⚠ 不能設 0：同一顆 process 兩次讀到的值可能差幾十毫秒，設 0 會把本尊判成 PidReused
        /// （於是 singleton guard 永遠殺不掉舊的那顆，而它看起來像「沒有舊的」）。</para>
        /// </summary>
        public const double StartTimeToleranceSeconds = 2.0;

        /// <summary>
        /// 記錄檔放哪。**宿主要在啟動時 <see cref="Configure"/> 一次**。
        /// <para>⚠ 沒設定時本服務**整體停用**（登記回 null 並喊一聲），刻意**不**退到暫存目錄：
        /// 退到暫存等於「登記在沒人會去看的地方」，而那跟沒登記的差別只有一個假的安全感。</para>
        /// </summary>
        public static string? RegistryDir { get; private set; }

        /// <summary>一般訊息（誰被收掉了）。null ＝ 不印。</summary>
        public static Action<string>? Log;

        /// <summary>
        /// 出問題但不致命的訊息。null ＝ 不印。
        /// <para>⚠ 這一條**強烈建議接上** —— 登記失敗而沒人知道，那顆就是孤兒。</para>
        /// </summary>
        public static Action<string>? Warn;

        /// <summary>設定記錄檔目錄（宿主啟動時呼叫一次）。傳空字串／null ＝ 停用本服務。</summary>
        public static void Configure(string? iRegistryDir)
        {
            RegistryDir = string.IsNullOrWhiteSpace(iRegistryDir) ? null : iRegistryDir;
        }

        /// <summary>本服務可用嗎（＝宿主設定過目錄了嗎）。</summary>
        public static bool Enabled { get { return RegistryDir != null; } }

        // ===========================================================
        // 登記 / 反登記
        // ===========================================================

        /// <summary>
        /// 等待型 spawn 的登記 ＋ 自動反登記（<c>using</c> scope）。
        /// <para>物理意義：Register / Unregister 必須成對，而**成對最常見的破法是例外路徑** ——
        /// 手寫 try/finally 很容易只寫在正常路徑上，留下一筆已死的 PID 記錄。
        /// 包成 IDisposable 之後，正常結束與丟例外都會反登記 —— 成對性由語言保證，不靠人記得。</para>
        /// <code>
        /// aProc.Start();
        /// using (SCP_ProcessRegistry.RegisterScope(aProc, Tag, "在做什麼", nameof(MyPage)))
        /// {
        ///     // …WaitForExit(timeout) 照舊…
        /// }
        /// </code>
        /// </summary>
        public static IDisposable RegisterScope(System.Diagnostics.Process iProc, string iTag,
            string iDescription = "", string iRegisteredBy = "", bool iAllowMultiple = true)
        {
            return new RegScope(iProc, iTag, iDescription, iRegisteredBy, iAllowMultiple);
        }

        sealed class RegScope : IDisposable
        {
            readonly int m_Pid;
            readonly string m_Tag;

            public RegScope(System.Diagnostics.Process iProc, string iTag, string iDesc,
                string iBy, bool iAllowMultiple)
            {
                m_Tag = iTag;
                m_Pid = -1;
                try
                {
                    if (Register(iProc, iTag, iDesc, iBy, iAllowMultiple) != null) m_Pid = iProc.Id;
                }
                catch (Exception e)
                {
                    // 登記失敗不擋工作本身（process 已經在跑了），但**不可靜默**：
                    // 沒登記成功卻沒人知道，那顆就是沒人管得到的孤兒。
                    Emit(Warn, "登記失敗（tag=" + iTag + "，該顆將無法被接管）：" + e.Message);
                }
            }

            public void Dispose()
            {
                if (m_Pid > 0) Unregister(m_Pid, m_Tag);
            }
        }

        /// <summary>
        /// 登記一顆剛啟動的 process。
        /// <para><paramref name="iAllowMultiple"/>=false ＝ singleton 語意：登記前先收掉所有既存同 tag
        /// （身分驗證通過的才動手）。⚠ 預設是 **true**：多顆併存在很多場景是正常的
        /// （使用者可以同時開兩個檔案總管），singleton 要**顯式**要求。</para>
        /// <para>回 null ＝ 沒登記成功（服務停用／process 已退出／拿不到資訊）。
        /// fail-soft：不炸掉呼叫端的 spawn 流程。</para>
        /// </summary>
        public static SCP_ProcessRecord? Register(System.Diagnostics.Process iProc, string iTag,
            string iDescription = "", string iRegisteredBy = "", bool iAllowMultiple = true)
        {
            if (iProc == null || string.IsNullOrEmpty(iTag)) return null;
            string? aDir = RegistryDir;
            if (aDir == null)
            {
                // 只在真的有人要登記時才喊 —— 沒用到這個服務的宿主不該被吵。
                Emit(Warn, "尚未 Configure(registryDir) ⇒ process 不會被登記（tag=" + iTag + "）");
                return null;
            }
            try
            {
                if (!iAllowMultiple)
                {
                    int aKilled = KillAllByTag(iTag);
                    if (aKilled > 0)
                        Emit(Warn, "Register(" + iTag + ") singleton：收掉 " + aKilled + " 顆既存同 tag process");
                }
                var aRec = new SCP_ProcessRecord();
                aRec.Pid = iProc.Id;
                aRec.ProcessName = SafeProcessName(iProc);
                aRec.StartTimeUtcText = SafeStartTimeUtcText(iProc);
                aRec.Tag = SanitizeTag(iTag);
                aRec.Description = iDescription ?? "";
                aRec.CommandLine = BuildCommandLine(iProc);
                aRec.RegisteredBy = iRegisteredBy ?? "";
                aRec.RegisteredAtUtcText = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);

                Directory.CreateDirectory(aDir);
                string aPath = RecordPath(aRec.Tag, aRec.Pid);
                // 先寫暫存再換檔 —— 防半寫檔被另一個 process 讀到（讀到半個 json 會被當成壞檔清掉）
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(aRec.ToJson()) + "\n");
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
                aRec.SourceFile = aPath;
                return aRec;
            }
            catch (Exception e)
            {
                Emit(Warn, "登記失敗（tag=" + iTag + "）：" + e.Message);
                return null;
            }
        }

        /// <summary>移除記錄檔（process 已由呼叫端正常收掉時用）。iTag 為 null 時只按 pid 找。</summary>
        public static void Unregister(int iPid, string? iTag = null)
        {
            string? aDir = RegistryDir;
            if (aDir == null) return;
            try
            {
                if (!Directory.Exists(aDir)) return;
                if (!string.IsNullOrEmpty(iTag))
                {
                    string aPath = RecordPath(SanitizeTag(iTag!), iPid);
                    if (File.Exists(aPath)) File.Delete(aPath);
                    return;
                }
                string[] aFiles = Directory.GetFiles(aDir, "*_" + iPid + ".json");
                for (int i = 0; i < aFiles.Length; ++i)
                {
                    SCP_ProcessRecord? aRec = LoadRecord(aFiles[i]);
                    if (aRec != null && aRec.Pid == iPid) File.Delete(aFiles[i]);
                }
            }
            catch (Exception e)
            {
                Emit(Warn, "反登記失敗（pid=" + iPid + "）：" + e.Message);
            }
        }

        // ===========================================================
        // 讀取 / 驗證
        // ===========================================================

        /// <summary>載入全部記錄（壞檔跳過並喊一聲 —— 靜默跳過會讓「表上少一顆」看起來很正常）。</summary>
        public static List<SCP_ProcessRecord> LoadAll()
        {
            var aList = new List<SCP_ProcessRecord>();
            string? aDir = RegistryDir;
            if (aDir == null || !Directory.Exists(aDir)) return aList;
            string[] aFiles;
            try { aFiles = Directory.GetFiles(aDir, "*.json"); }
            catch (Exception e) { Emit(Warn, "讀取登記目錄失敗：" + e.Message); return aList; }

            for (int i = 0; i < aFiles.Length; ++i)
            {
                SCP_ProcessRecord? aRec = LoadRecord(aFiles[i]);
                if (aRec != null) aList.Add(aRec);
            }
            return aList;
        }

        /// <summary>載入全部記錄 ＋ 各自的身分驗證結果（管理頁用）。</summary>
        public static List<KeyValuePair<SCP_ProcessRecord, SCP_ProcessStatus>> LoadAllWithStatus()
        {
            var aList = new List<KeyValuePair<SCP_ProcessRecord, SCP_ProcessStatus>>();
            List<SCP_ProcessRecord> aRecords = LoadAll();
            for (int i = 0; i < aRecords.Count; ++i)
                aList.Add(new KeyValuePair<SCP_ProcessRecord, SCP_ProcessStatus>(aRecords[i], Validate(aRecords[i])));
            return aList;
        }

        /// <summary>
        /// 身分驗證 —— 記錄 vs 當前 OS 狀態。
        /// <para>物理意義：PID 會被回收再發，所以只有 name ＋ start time 都吻合才能認定是
        /// 「當初登記的那顆」。拿不到對方資訊時回 <see cref="SCP_ProcessStatus.Unknown"/>
        /// 而不是 Dead —— kill 端會因此拒絕動手，那正是要的行為。</para>
        /// </summary>
        public static SCP_ProcessStatus Validate(SCP_ProcessRecord? iRec)
        {
            if (iRec == null || iRec.Pid <= 0) return SCP_ProcessStatus.Unknown;
            System.Diagnostics.Process aProc;
            try { aProc = System.Diagnostics.Process.GetProcessById(iRec.Pid); }
            catch (ArgumentException) { return SCP_ProcessStatus.Dead; }   // 無此 PID
            catch (Exception) { return SCP_ProcessStatus.Unknown; }
            try
            {
                using (aProc)
                {
                    if (aProc.HasExited) return SCP_ProcessStatus.Dead;
                    if (!string.IsNullOrEmpty(iRec.ProcessName)
                        && !string.Equals(aProc.ProcessName, iRec.ProcessName, StringComparison.OrdinalIgnoreCase))
                        return SCP_ProcessStatus.PidReused;
                    DateTime? aRecorded = iRec.StartTimeUtc;
                    if (aRecorded.HasValue)
                    {
                        double aDiff = Math.Abs((aProc.StartTime.ToUniversalTime() - aRecorded.Value).TotalSeconds);
                        if (aDiff > StartTimeToleranceSeconds) return SCP_ProcessStatus.PidReused;
                    }
                    return SCP_ProcessStatus.Alive;
                }
            }
            catch (Exception)
            {
                // 權限不足／剛退出的 race —— 保守回 Unknown
                return SCP_ProcessStatus.Unknown;
            }
        }

        /// <summary>把狀態翻成一句人話（表格與錯誤訊息共用同一份字，避免兩處說法不同）。</summary>
        public static string StatusText(SCP_ProcessStatus iStatus)
        {
            switch (iStatus)
            {
                case SCP_ProcessStatus.Alive: return "活著";
                case SCP_ProcessStatus.Dead: return "已結束（記錄可清）";
                case SCP_ProcessStatus.PidReused: return "PID 已易主 —— 拒絕 kill（防誤殺）";
                default: return "身分無法驗證 —— 拒絕 kill（保守）";
            }
        }

        // ===========================================================
        // Kill / 清理
        // ===========================================================

        /// <summary>
        /// 身分驗證通過才 kill（Alive 以外一律拒絕 —— 防誤殺是本服務存在的理由）。成功後順手移除記錄檔。
        /// </summary>
        public static bool KillRegistered(SCP_ProcessRecord iRec, out string oError)
        {
            oError = "";
            SCP_ProcessStatus aStatus = Validate(iRec);
            if (aStatus != SCP_ProcessStatus.Alive)
            {
                oError = StatusText(aStatus);
                return false;
            }
            try
            {
                using (var aProc = System.Diagnostics.Process.GetProcessById(iRec.Pid))
                {
                    // kill 前**最後一次**複驗 —— Validate 到 Kill 之間仍有極小的 race 窗，
                    // 而那個窗裡發生的事情正好就是「PID 易主」。
                    DateTime? aRecorded = iRec.StartTimeUtc;
                    if (aRecorded.HasValue)
                    {
                        double aDiff = Math.Abs((aProc.StartTime.ToUniversalTime() - aRecorded.Value).TotalSeconds);
                        if (aDiff > StartTimeToleranceSeconds)
                        {
                            oError = "kill 前複驗失敗：start time 不吻合（PID 已易主）";
                            return false;
                        }
                    }
                    aProc.Kill();
                    aProc.WaitForExit(3000);
                }
            }
            catch (ArgumentException)
            {
                // 自己先退出了 —— 視為成功收掉（目的達成，不是失敗）
            }
            catch (Exception e)
            {
                oError = "kill 失敗：" + e.Message;
                return false;
            }
            if (!string.IsNullOrEmpty(iRec.SourceFile))
            {
                try { File.Delete(iRec.SourceFile); } catch { /* 殘檔交給 CleanupStale */ }
            }
            return true;
        }

        /// <summary>
        /// 收掉所有同 tag 的已登記 process —— singleton guard（spawn 前呼叫）。
        /// <para>逐筆走 <see cref="Validate"/>：Alive 才 kill；Dead / PidReused **只清記錄檔**
        /// （PID 已易主的那顆是別人的，絕不碰）；Unknown 不 kill 也不清，
        /// 進 <paramref name="oSkipped"/> 讓呼叫端決定要不要人工處理。</para>
        /// <para>回傳實際 kill 掉的數量。</para>
        /// </summary>
        public static int KillAllByTag(string iTag, List<string>? oSkipped = null)
        {
            if (string.IsNullOrEmpty(iTag)) return 0;
            string aWant = SanitizeTag(iTag);
            int aKilled = 0;
            List<SCP_ProcessRecord> aRecords = LoadAll();
            for (int i = 0; i < aRecords.Count; ++i)
            {
                SCP_ProcessRecord aRec = aRecords[i];
                if (!string.Equals(aRec.Tag, aWant, StringComparison.OrdinalIgnoreCase)) continue;
                SCP_ProcessStatus aStatus = Validate(aRec);
                if (aStatus == SCP_ProcessStatus.Alive)
                {
                    string aErr;
                    if (KillRegistered(aRec, out aErr))
                    {
                        ++aKilled;
                        Emit(Log, "KillAllByTag(" + aWant + ")：收掉 PID " + aRec.Pid);
                    }
                    else if (oSkipped != null)
                    {
                        oSkipped.Add("PID " + aRec.Pid + "：" + aErr);
                    }
                }
                else if (aStatus == SCP_ProcessStatus.Dead || aStatus == SCP_ProcessStatus.PidReused)
                {
                    DeleteRecordFile(aRec);
                }
                else if (oSkipped != null)
                {
                    oSkipped.Add("PID " + aRec.Pid + "：" + StatusText(aStatus));
                }
            }
            return aKilled;
        }

        /// <summary>
        /// 清掉 Dead / PidReused 的**記錄檔**（絕不碰任何活著的 process）。回傳清掉幾筆。
        /// <para>物理意義：fire-and-forget 型的 spawn **沒有 <c>finally</c> 可以放 Unregister</c>**
        /// （<c>Process.Start</c> 完就 return，沒人等它），所以它們的記錄只能靠事後清。
        /// ⚠ 宿主要找一個**一定會經過**的時機呼叫它（CLI：每次啟動；視窗：開場一次），
        /// 否則「不會有屍潮」會被換成「殘檔堆積」，而堆積出來的畫面跟屍潮長得一樣 ——
        /// 一樣會訓練人忽略那張表。</para>
        /// </summary>
        public static int CleanupStale()
        {
            int aRemoved = 0;
            List<SCP_ProcessRecord> aRecords = LoadAll();
            for (int i = 0; i < aRecords.Count; ++i)
            {
                SCP_ProcessStatus aStatus = Validate(aRecords[i]);
                if (aStatus == SCP_ProcessStatus.Dead || aStatus == SCP_ProcessStatus.PidReused)
                {
                    if (DeleteRecordFile(aRecords[i])) ++aRemoved;
                }
            }
            return aRemoved;
        }

        // ===========================================================
        // 內部
        // ===========================================================

        static bool DeleteRecordFile(SCP_ProcessRecord iRec)
        {
            if (string.IsNullOrEmpty(iRec.SourceFile)) return false;
            try { File.Delete(iRec.SourceFile); return true; }
            catch (Exception e) { Emit(Warn, "清除記錄檔失敗（" + iRec.SourceFile + "）：" + e.Message); return false; }
        }

        static SCP_ProcessRecord? LoadRecord(string iPath)
        {
            try
            {
                SCP_JsonData aData = SCP_JsonParser.Parse(File.ReadAllText(iPath));
                SCP_ProcessRecord? aRec = SCP_ProcessRecord.FromJson(aData);
                if (aRec == null) return null;
                aRec.SourceFile = iPath;
                return aRec;
            }
            catch (Exception e)
            {
                // 壞檔不靜默跳過 —— 「表上少一顆」跟「本來就沒有那顆」在畫面上長得一樣
                Emit(Warn, "登記檔讀取失敗（" + iPath + "）：" + e.Message);
                return null;
            }
        }

        static string RecordPath(string iTag, int iPid)
        {
            return Path.Combine(RegistryDir ?? Path.GetTempPath(), iTag + "_" + iPid + ".json");
        }

        /// <summary>tag 會變成檔名的一部分 ⇒ 路徑字元一律換掉（不然登記會在一個猜不到的地方失敗）。</summary>
        static string SanitizeTag(string iTag)
        {
            char[] aChars = iTag.ToCharArray();
            for (int i = 0; i < aChars.Length; ++i)
            {
                char c = aChars[i];
                bool aSafe = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                             || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!aSafe) aChars[i] = '_';
            }
            return new string(aChars);
        }

        static string SafeProcessName(System.Diagnostics.Process iProc)
        {
            try { return iProc.ProcessName; } catch { return ""; }
        }

        /// <summary>
        /// 啟動時間。拿不到（權限不足）就回空字串 ——
        /// ⚠ 空字串的語意是「這一格沒有身分證據」，<see cref="Validate"/> 會因此**跳過** start time 比對，
        /// 於是那筆記錄只靠 PID＋name 認人。這是刻意的降級，不是漏寫。
        /// </summary>
        static string SafeStartTimeUtcText(System.Diagnostics.Process iProc)
        {
            try { return iProc.StartTime.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
            catch { return ""; }
        }

        static string BuildCommandLine(System.Diagnostics.Process iProc)
        {
            try
            {
                System.Diagnostics.ProcessStartInfo aInfo = iProc.StartInfo;
                string aArgs = aInfo.Arguments;
                if (string.IsNullOrEmpty(aArgs) && aInfo.ArgumentList.Count > 0)
                    aArgs = string.Join(" ", aInfo.ArgumentList);
                return (aInfo.FileName + " " + aArgs).Trim();
            }
            catch { return ""; }
        }

        static void Emit(Action<string>? iSink, string iMessage)
        {
            Action<string>? aSink = iSink;
            if (aSink != null) aSink("[SCP_ProcessRegistry] " + iMessage);
        }
    }
}
