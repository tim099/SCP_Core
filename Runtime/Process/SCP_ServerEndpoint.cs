// 區塊職責：Server 在**它服務的那棵資料根**留下的名片 —— 「我是誰、我的 queue 在哪、我的心跳在哪」。
// 物理意義：TASK-0106（Tim 2026-09-21 拍板「②」）。委派需要一個可寫的 queue 目錄，而呼叫端不一定知道 Server 在哪。
//           ⇒ 由知道答案的那一方（Server 自己）把答案寫進大家都看得到的地方：資料根。
//
// ⭐ 順帶：「Server 在不在」有一個**讀得到**的讀數 —— 沒有答案時人會去猜。
//
// ⚠ 名片**只在啟動時寫一次、收工時刪掉**，⛔ 不跟著心跳每 500ms 覆寫：
//   資料根是一個 git repo，每半秒動一個檔會讓工作區永遠是髒的，
//   而「永遠髒」等於沒有人看得出這一次多了什麼。
//   ⇒ 活著與否**不看名片在不在**，看名片指向的那個心跳檔多久沒跳（心跳住 Senate 的 runtime 目錄，不進資料根）。
//   （例外：名片被刪掉或變成遺物、而握著資料根單例鎖的就是自己 ⇒ Server 補寫一次，見 ServerHost。）
//
// 🔴 TASK-0468：**一棵資料根上，同一個 serverId 只能有一顆**（Tim 2026-10-12 拍板：同根同 id 一律擋；
//   測試用 Server 換一個 id，或指向另一棵資料根）。三道：
//   ① 資料根層級的單例鎖 `_server_singleton.<id>.lock`（<see cref="SCP_ServerEndpoint.TryAcquireLock"/>）；
//   ② 名片**不蓋活的**、收工**只刪自己的**（防沒有 ① 的舊 exe）；
//   ③ 名片記 `repo_root` ⇒ 擋下時說得出握著的是哪一份 Senate，管理頁也列得出別的工作副本。
//
// 🩸 為什麼要三態（<see cref="SCP_ServerLiveness"/>）而不是一個 bool：
//   「沒有名片」＝ 這棵樹沒有 Server 服務過（或它正常收工了）⇒ 呼叫端該說「沒有 Server」；
//   「有名片但心跳過期」＝ 它**當掉了**（名片是遺物）⇒ 呼叫端該說「它死了，這是它的遺體」；
//   兩者的下一步不同（前者去啟動，後者去查它為什麼死、順手清遺物），
//   壓成 bool 的話兩邊都只會印「Server 沒跑」。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Proc
{
    /// <summary>Server 活著沒 —— **三態，不得壓成 bool**。</summary>
    public enum SCP_ServerLiveness
    {
        /// <summary>沒有名片 ⇒ 這棵樹上沒有 Server（沒起過，或正常收工了）。</summary>
        NoEndpoint = 0,

        /// <summary>名片在、心跳**過期** ⇒ 它當掉了，這張名片是遺物。⛔ 跟「沒有 Server」不同形。</summary>
        StaleHeartbeat = 1,

        /// <summary>心跳還在跳。</summary>
        Alive = 2,

        /// <summary>名片或心跳檔讀不了／解不開 ⇒ **不知道**。⛔ 不要當成沒有。</summary>
        Unreadable = 3,
    }

    /// <summary>一張名片。</summary>
    public sealed class SCP_ServerEndpointInfo
    {
        public string ServerId = "";
        public int Pid;
        public string BuildId = "";
        public string StartedAtUtc = "";

        /// <summary>Server 執行器的根：底下有 <c>queues/&lt;lane&gt;/</c> 與 <c>_cmd_results/</c>。</summary>
        public string ServerRoot = "";

        /// <summary>心跳檔的絕對路徑（⚠ 它**不在資料根裡**，在 Senate 自己的 runtime 目錄）。</summary>
        public string HeartbeatPath = "";

        /// <summary>心跳超過幾秒算過期 —— 由寫名片的那一方決定，⛔ 讀的人不要自己另訂一個。</summary>
        public double StaleSeconds = 4.0;

        /// <summary>
        /// 名片主人是哪一份 Senate（repo 根）—— 同一棵資料根可以被不同的工作副本服務，
        /// 擋下第二顆時要說得出「握著的是哪一份」（TASK-0468）。舊名片（schema 1）沒有這欄 ⇒ 讀的時候由 ServerRoot 推。
        /// </summary>
        public string RepoRoot = "";

        /// <summary>「是同一顆 process」：pid ＋ 啟動時刻都對上。⚠ 只比 pid 的話，pid 被 OS 回收再發時會認錯人。</summary>
        public bool IsSameServer(int iPid, string iStartedAtUtc)
            => Pid == iPid && string.Equals(StartedAtUtc, iStartedAtUtc ?? "", StringComparison.Ordinal);

        public SCP_JsonData ToJson()
        {
            SCP_JsonData aData = SCP_JsonData.NewObject();
            aData.Set("server_id", SCP_JsonData.NewString(ServerId));
            aData.Set("pid", SCP_JsonData.NewNumber(Pid));
            aData.Set("build_id", SCP_JsonData.NewString(BuildId));
            aData.Set("started_at_utc", SCP_JsonData.NewString(StartedAtUtc));
            aData.Set("server_root", SCP_JsonData.NewString(ServerRoot));
            aData.Set("heartbeat_path", SCP_JsonData.NewString(HeartbeatPath));
            aData.Set("stale_seconds", SCP_JsonData.NewNumber(StaleSeconds));
            aData.Set("repo_root", SCP_JsonData.NewString(RepoRoot));
            aData.Set("schema_version", SCP_JsonData.NewNumber(2));
            return aData;
        }

        public static SCP_ServerEndpointInfo FromJson(SCP_JsonData iData)
        {
            var aInfo = new SCP_ServerEndpointInfo
            {
                ServerId = iData.GetString("server_id", ""),
                Pid = iData.GetInt("pid", 0),
                BuildId = iData.GetString("build_id", ""),
                StartedAtUtc = iData.GetString("started_at_utc", ""),
                ServerRoot = iData.GetString("server_root", ""),
                HeartbeatPath = iData.GetString("heartbeat_path", ""),
                StaleSeconds = iData.GetInt("stale_seconds", 4),
                RepoRoot = iData.GetString("repo_root", ""),
            };
            if (aInfo.RepoRoot.Length == 0) aInfo.RepoRoot = RepoRootFromServerRoot(aInfo.ServerRoot);
            return aInfo;
        }

        /// <summary>舊名片沒有 repo_root ⇒ 由 `&lt;repo&gt;/SenateData/runtime/server…` 往回切；切不出來回空字串（⛔ 不猜）。</summary>
        static string RepoRootFromServerRoot(string iServerRoot)
        {
            string aNorm = (iServerRoot ?? "").Replace('\\', '/');
            int aAt = aNorm.LastIndexOf("/SenateData/", StringComparison.OrdinalIgnoreCase);
            return aAt > 0 ? aNorm.Substring(0, aAt) : "";
        }
    }

    /// <summary>寫名片的結果 —— **三態**：「沒寫成」與「刻意不寫（活著的另一顆是主人）」的下一步相反。</summary>
    public enum SCP_EndpointWrite
    {
        Written = 0,

        /// <summary>名片上是**另一顆活著的** Server ⇒ 不蓋（TASK-0468：蓋掉的話兩顆都以為自己是那一顆）。</summary>
        RefusedOtherAlive = 1,

        /// <summary>名片讀不開 ⇒ 不知道主人是不是活著 ⇒ 不蓋（⛔ 認不出來不等於沒有）。</summary>
        RefusedUnreadable = 2,

        /// <summary>IO 失敗。</summary>
        Failed = 3,
    }

    /// <summary>一次探測的結果。</summary>
    public readonly struct SCP_ServerProbe
    {
        public SCP_ServerProbe(SCP_ServerLiveness iState, SCP_ServerEndpointInfo? iInfo,
                               double? iBeatAgeSeconds, string iDetail)
        {
            State = iState;
            Info = iInfo;
            BeatAgeSeconds = iBeatAgeSeconds;
            Detail = iDetail ?? "";
        }

        public SCP_ServerLiveness State { get; }

        /// <summary>名片內容；<c>null</c> ＝ 沒有名片或讀不了。</summary>
        public SCP_ServerEndpointInfo? Info { get; }

        /// <summary>心跳距今幾秒；<c>null</c> ＝ 量不到（⛔ 不是 0 —— 0 是「剛跳過」）。</summary>
        public double? BeatAgeSeconds { get; }

        /// <summary>人讀的一行（直接貼進錯誤訊息）。</summary>
        public string Detail { get; }

        public bool Alive => State == SCP_ServerLiveness.Alive;
    }

    /// <summary>名片的讀、寫、刪。⛔ 本型別不啟動也不停止任何東西。</summary>
    public static class SCP_ServerEndpoint
    {
        /// <summary>`<資料根>/_server_endpoint.<serverId>.json`。底線開頭 ＝ 與資料根其餘 runtime 檔同形。</summary>
        public static string PathFor(string iDataRoot, string iServerId)
        {
            string aRoot = (iDataRoot ?? "").Replace('\\', '/').TrimEnd('/');
            return aRoot + "/_server_endpoint." + (iServerId ?? "") + ".json";
        }

        /// <summary>`<資料根>/_server_singleton.<serverId>.lock` —— 資料根層級的單例鎖（TASK-0468）。</summary>
        public static string LockPathFor(string iDataRoot, string iServerId)
        {
            string aRoot = (iDataRoot ?? "").Replace('\\', '/').TrimEnd('/');
            return aRoot + "/_server_singleton." + (iServerId ?? "") + ".lock";
        }

        /// <summary>
        /// 拿資料根層級的單例鎖：獨佔開檔（<see cref="FileShare.None"/>），**握著 handle ＝ 持有鎖**，process 死掉由 OS 釋放。
        /// <para>🩸 TASK-0468：原本的單例鎖住在**各自 repo 的 runtime**，看不到同一棵資料根上的另一份 Senate
        /// ⇒ 副本起了第二顆 tavern，兩顆都送 Discord、搶 Inbound 游標。⇒ 鎖要住在**它們共用的那棵樹上**。</para>
        /// <para>拿不到回 null，<paramref name="oWhy"/> 說出握著的是誰（讀名片：pid／repo 根／心跳）。</para>
        /// </summary>
        public static FileStream? TryAcquireLock(string iDataRoot, string iServerId, int iWaitMs, out string oWhy)
        {
            oWhy = "";
            string aPath = LockPathFor(iDataRoot, iServerId);
            var aSw = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    var aFs = new FileStream(aPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                    // 寫 pid 純粹給人看（握著的時候別人讀不到；process 死後留下來指認最後一任）—— ⛔ 互斥不靠它。
                    try
                    {
                        byte[] aBytes = Encoding.UTF8.GetBytes(
                            System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture) + "\n");
                        aFs.SetLength(0);
                        aFs.Write(aBytes, 0, aBytes.Length);
                        aFs.Flush();
                    }
                    catch (Exception) { /* 寫不進去不影響互斥 */ }
                    return aFs;
                }
                catch (IOException e)
                {
                    if (aSw.ElapsedMilliseconds >= iWaitMs)
                    {
                        SCP_ServerProbe aHolder = Probe(iDataRoot, iServerId);
                        oWhy = "這棵資料根上的 `" + iServerId + "` 已經有一顆握著單例鎖（" + aPath + "，"
                               + e.GetType().Name + "）⇒ 握著的那顆：" + Describe(aHolder);
                        return null;
                    }
                    System.Threading.Thread.Sleep(50);
                }
                catch (UnauthorizedAccessException e)
                {
                    oWhy = "沒有權限開 " + aPath + "：" + e.Message;
                    return null;
                }
            }
        }

        /// <summary>人讀的「名片上那顆是誰」：pid／repo 根／狀態。名片不在或讀不開就照實說。</summary>
        public static string Describe(SCP_ServerProbe iProbe)
        {
            if (iProbe.Info == null) return iProbe.Detail;
            return "pid=" + iProbe.Info.Pid
                   + "　repo 根=" + (iProbe.Info.RepoRoot.Length > 0 ? iProbe.Info.RepoRoot : "（名片上沒有、也推不出來）")
                   + "　build=" + iProbe.Info.BuildId
                   + "　狀態=" + iProbe.State;
        }

        /// <summary>
        /// 寫一張名片（tmp → 換名，讀的人不會讀到半張）。⛔ 失敗不丟例外：Server 不該因為名片寫不出去而不啟動。
        /// <para>🔴 **名片上是另一顆活著的 ⇒ 不蓋**（TASK-0468）：心跳新鮮、而且不是同一顆 process（pid＋啟動時刻）。
        /// 讀不開也不蓋 —— 不知道主人是不是活著。遺體（心跳過期／心跳檔不在）與沒有名片照常寫。</para>
        /// </summary>
        public static (SCP_EndpointWrite Result, string Message) Write(string iDataRoot, SCP_ServerEndpointInfo iInfo)
        {
            string aPath = PathFor(iDataRoot, iInfo.ServerId);
            SCP_ServerProbe aNow = Probe(iDataRoot, iInfo.ServerId);
            if (aNow.State == SCP_ServerLiveness.Alive && aNow.Info != null && !aNow.Info.IsSameServer(iInfo.Pid, iInfo.StartedAtUtc))
                return (SCP_EndpointWrite.RefusedOtherAlive,
                        "名片上是另一顆活著的 `" + iInfo.ServerId + "`（" + Describe(aNow) + "）⇒ **不蓋**（" + aPath + "）");
            if (aNow.State == SCP_ServerLiveness.Unreadable)
                return (SCP_EndpointWrite.RefusedUnreadable,
                        aNow.Detail + " ⇒ 不知道主人還在不在 ⇒ **不蓋**；人工看過再刪那張名片");
            string aTmp = aPath + ".tmp" + System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aPath) ?? ".");
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(iInfo.ToJson()) + "\n", new UTF8Encoding(false));
                if (File.Exists(aPath)) File.Replace(aTmp, aPath, null);
                else File.Move(aTmp, aPath);
                return (SCP_EndpointWrite.Written, aPath);
            }
            catch (Exception e)
            {
                try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch (Exception) { }
                return (SCP_EndpointWrite.Failed, "名片寫不出去（" + aPath + "）：" + e.Message
                               + " ⇒ ⚠ **讀名片的一方會判成『這棵樹沒有 Server』**");
            }
        }

        /// <summary>
        /// 收工時刪掉名片 —— **只刪自己的**（pid＋啟動時刻對上才刪；TASK-0468）。回 <c>(成功, 說明)</c>；檔本來就不在也算成功。
        /// <para>🩸 原本收工一律刪：同一個 id 有兩顆時，先收工的那顆會把**還在跑的那顆**的名片帶走。</para>
        /// </summary>
        public static (bool Ok, string Message) Delete(string iDataRoot, string iServerId, int iPid, string iStartedAtUtc)
        {
            string aPath = PathFor(iDataRoot, iServerId);
            try
            {
                if (!File.Exists(aPath)) return (true, aPath);
                SCP_ServerEndpointInfo aInfo;
                try { aInfo = SCP_ServerEndpointInfo.FromJson(SCP_JsonParser.Parse(File.ReadAllText(aPath))); }
                catch (Exception e) { return (false, "名片讀不開（" + aPath + "）：" + e.Message + " ⇒ 不知道是不是自己的 ⇒ **沒刪**"); }
                if (!aInfo.IsSameServer(iPid, iStartedAtUtc))
                    return (true, "名片上是別顆（pid=" + aInfo.Pid + "）⇒ 不是我的，**沒刪**（" + aPath + "）");
                File.Delete(aPath);
                return (true, aPath);
            }
            catch (Exception e)
            {
                return (false, "名片刪不掉（" + aPath + "）：" + e.Message
                               + " ⇒ ⚠ 它會變成遺物，而遺物讀起來像『有一顆 Server』"
                               + "（心跳過期那一態會擋住，但那要下一個人去讀心跳）");
            }
        }

        /// <summary>
        /// 這棵資料根上的**全部**名片（不分哪一份 Senate 寫的）—— 管理頁要看得到別的工作副本開的 Server（TASK-0468）。
        /// <para>回 (serverId, 探測)；列舉本身失敗丟例外（⛔ 不回空清單假裝沒有）。</para>
        /// </summary>
        public static System.Collections.Generic.List<(string ServerId, SCP_ServerProbe Probe)> ProbeAll(string iDataRoot)
        {
            var aOut = new System.Collections.Generic.List<(string, SCP_ServerProbe)>();
            const string aHead = "_server_endpoint.", aTail = ".json";
            foreach (string aFile in Directory.GetFiles(iDataRoot, aHead + "*" + aTail))
            {
                string aName = Path.GetFileName(aFile);
                string aId = aName.Substring(aHead.Length, aName.Length - aHead.Length - aTail.Length);
                aOut.Add((aId, Probe(iDataRoot, aId)));
            }
            aOut.Sort((a, b) => string.CompareOrdinal(a.Item1, b.Item1));
            return aOut;
        }

        /// <summary>兩個 repo 根是不是同一個（大小寫、斜線、結尾斜線不算差別）。</summary>
        public static bool SameRoot(string iA, string iB)
            => string.Equals((iA ?? "").Replace('\\', '/').TrimEnd('/'), (iB ?? "").Replace('\\', '/').TrimEnd('/'),
                             StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 探一下這棵資料根上那顆 Server。⛔ **不讀就不要說「沒有」** —— 四態各自說得出下一步。
        /// </summary>
        public static SCP_ServerProbe Probe(string iDataRoot, string iServerId)
        {
            string aPath = PathFor(iDataRoot, iServerId);
            if (!File.Exists(aPath))
                return new SCP_ServerProbe(SCP_ServerLiveness.NoEndpoint, null, null,
                    "這棵資料根上沒有 `" + iServerId + "` 的 Server 名片（" + aPath + "）"
                    + " ⇒ 它沒起過，或正常收工了。");

            SCP_ServerEndpointInfo aInfo;
            try { aInfo = SCP_ServerEndpointInfo.FromJson(SCP_JsonParser.Parse(File.ReadAllText(aPath))); }
            catch (Exception e)
            {
                return new SCP_ServerProbe(SCP_ServerLiveness.Unreadable, null, null,
                    "名片讀不開（" + aPath + "）：" + e.Message + " ⇒ **不知道**有沒有 Server，⛔ 不當成沒有。");
            }

            if (aInfo.HeartbeatPath.Length == 0 || !File.Exists(aInfo.HeartbeatPath))
                return new SCP_ServerProbe(SCP_ServerLiveness.StaleHeartbeat, aInfo, null,
                    "名片在（pid=" + aInfo.Pid + "）而**心跳檔不在**（" + aInfo.HeartbeatPath + "）"
                    + " ⇒ 那顆 Server 已經不在了，這張名片是遺物。");

            string aBeat;
            int aBeatPid;
            string aBeatStarted;
            try
            {
                SCP_JsonData aHb = SCP_JsonParser.Parse(File.ReadAllText(aInfo.HeartbeatPath));
                aBeat = aHb.GetString("beat_at_utc", "");
                aBeatPid = aHb.GetInt("pid", 0);
                aBeatStarted = aHb.GetString("started_at_utc", "");
            }
            catch (Exception e)
            {
                return new SCP_ServerProbe(SCP_ServerLiveness.Unreadable, aInfo, null,
                    "心跳檔讀不開（" + aInfo.HeartbeatPath + "）：" + e.Message + " ⇒ **不知道**。");
            }

            // 🩸 TASK-0527：心跳檔照 server id 取路徑 ⇒ 同一份 repo 的下一代寫的是**同一個檔**。
            //    只看新不新的話，下一代（連它自己寫名片前的第一拍）會替死掉的名片作證 ⇒ 遺物永遠 Alive、沒有人接手。
            //    ⇒ 心跳上寫的是另一顆 ⇒ 名片是遺物。心跳檔沒寫 pid（舊格式）⇒ 認不出是誰，照舊只看新不新。
            if (aBeatPid != 0
                && (aBeatPid != aInfo.Pid
                    || (aBeatStarted.Length > 0 && aInfo.StartedAtUtc.Length > 0
                        && !string.Equals(aBeatStarted, aInfo.StartedAtUtc, StringComparison.Ordinal))))
                return new SCP_ServerProbe(SCP_ServerLiveness.StaleHeartbeat, aInfo, null,
                    "名片在（pid=" + aInfo.Pid + "）而心跳檔已被另一顆接手（心跳 pid=" + aBeatPid
                    + (aBeatStarted.Length > 0 ? "，啟動 " + aBeatStarted : "") + "）"
                    + " ⇒ 名片上那顆已經不在了，這張名片是遺物。");

            if (!DateTime.TryParse(aBeat, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime aAt))
                return new SCP_ServerProbe(SCP_ServerLiveness.Unreadable, aInfo, null,
                    "心跳檔裡的 `beat_at_utc` 解不開（`" + aBeat + "`）⇒ **不知道**。");

            double aAge = (DateTime.UtcNow - aAt.ToUniversalTime()).TotalSeconds;
            if (aAge > aInfo.StaleSeconds)
                return new SCP_ServerProbe(SCP_ServerLiveness.StaleHeartbeat, aInfo, aAge,
                    "名片在（pid=" + aInfo.Pid + "）而心跳已經 "
                    + aAge.ToString("0.0", CultureInfo.InvariantCulture) + "s 沒跳（上限 "
                    + aInfo.StaleSeconds.ToString("0.0", CultureInfo.InvariantCulture) + "s）"
                    + " ⇒ 它當掉了，⛔ 這不是「沒有 Server」。");

            return new SCP_ServerProbe(SCP_ServerLiveness.Alive, aInfo, aAge,
                "Server `" + aInfo.ServerId + "` 活著　pid=" + aInfo.Pid + "　build=" + aInfo.BuildId
                + "　心跳 " + aAge.ToString("0.0", CultureInfo.InvariantCulture) + "s 前"
                + "　執行器根：" + aInfo.ServerRoot);
        }
    }
}
