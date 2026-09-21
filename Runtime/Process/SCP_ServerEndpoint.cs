// 區塊職責：Server 在**它服務的那棵資料根**留下的名片 —— 「我是誰、我的 queue 在哪、我的心跳在哪」。
// 物理意義：TASK-0106 第 4 步（Tim 2026-09-21 拍板「②」）。Unity Editor 那側**不知道 Senate 在哪**
//           （UCL_Core 全樹零個 Server 根參照），而委派需要一個可寫的 queue 目錄。
//           ⇒ 由知道答案的那一方（Server 自己）把答案寫進兩邊都看得到的地方：資料根。
//
// ⭐ 順帶解掉第二件事：「Server 在不在」現在有一個 **Editor 讀得到**的讀數 ——
//    在這之前那個問題在 Unity 那側**沒有任何答案**，而沒有答案時人會去猜。
//
// ⚠ 名片**只在啟動時寫一次、收工時刪掉**，⛔ 不跟著心跳每 500ms 覆寫：
//   資料根是一個 git repo，每半秒動一個檔會讓工作區永遠是髒的，
//   而「永遠髒」等於沒有人看得出這一次多了什麼。
//   ⇒ 活著與否**不看名片在不在**，看名片指向的那個心跳檔多久沒跳（心跳住 Senate 的 runtime 目錄，不進資料根）。
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
            aData.Set("schema_version", SCP_JsonData.NewNumber(1));
            return aData;
        }

        public static SCP_ServerEndpointInfo FromJson(SCP_JsonData iData)
        {
            return new SCP_ServerEndpointInfo
            {
                ServerId = iData.GetString("server_id", ""),
                Pid = iData.GetInt("pid", 0),
                BuildId = iData.GetString("build_id", ""),
                StartedAtUtc = iData.GetString("started_at_utc", ""),
                ServerRoot = iData.GetString("server_root", ""),
                HeartbeatPath = iData.GetString("heartbeat_path", ""),
                StaleSeconds = iData.GetInt("stale_seconds", 4),
            };
        }
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

        /// <summary>寫一張名片（整檔覆寫）。回 <c>(成功, 說明)</c> —— ⛔ 失敗不丟例外：Server 不該因為名片寫不出去而不啟動。</summary>
        public static (bool Ok, string Message) Write(string iDataRoot, SCP_ServerEndpointInfo iInfo)
        {
            string aPath = PathFor(iDataRoot, iInfo.ServerId);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aPath) ?? ".");
                File.WriteAllText(aPath, SCP_JsonWriter.Write(iInfo.ToJson()) + "\n", new UTF8Encoding(false));
                return (true, aPath);
            }
            catch (Exception e)
            {
                return (false, "名片寫不出去（" + aPath + "）：" + e.Message
                               + " ⇒ ⚠ **Editor 那側會判成『這棵樹沒有 Server』**");
            }
        }

        /// <summary>收工時刪掉名片。回 <c>(成功, 說明)</c>；檔本來就不在也算成功。</summary>
        public static (bool Ok, string Message) Delete(string iDataRoot, string iServerId)
        {
            string aPath = PathFor(iDataRoot, iServerId);
            try
            {
                if (File.Exists(aPath)) File.Delete(aPath);
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
            try { aBeat = SCP_JsonParser.Parse(File.ReadAllText(aInfo.HeartbeatPath)).GetString("beat_at_utc", ""); }
            catch (Exception e)
            {
                return new SCP_ServerProbe(SCP_ServerLiveness.Unreadable, aInfo, null,
                    "心跳檔讀不開（" + aInfo.HeartbeatPath + "）：" + e.Message + " ⇒ **不知道**。");
            }

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
