// 區塊職責：**把一筆 Cmd 送給 Senate Server 並等它的判定** —— 檔案協議的 client 那一半。
// 物理意義：TASK-0106 第 5 步。Unity Editor 需要這條路（委派酒館寫入），而 Senate 那側的
//           `AgentCmdClient` 住在 `Senate.Core`（吃 `System.Text.Json.Nodes`）—— Unity 編不到。
//           ⇒ 本檔是同一個協議的 netstandard2.1 版，兩個宿主都編得過。
//
// 🔴 **兩份實作 ＝ 會分岔**，而分岔的失效樣子是「送出去了，對面永遠看不到」——
//   queue 落在對的路徑、JSON 合法、trigger 也寫了，而 Watcher 不認得那個形狀，於是**沒有人報錯**。
//   ⇒ 所以這一份的形狀由一格對拍釘住：`ServerCmdClientMatchesAgentCmdClient`
//     （兩邊各送一筆進暫存樹，逐欄比 queue.json）。⛔ 改本檔的欄位就會當場紅燈。
//
// ⚠ 本檔**不決定要不要送**（那是開關的事）、**不決定 Server 在不在**（那是 `SCP_ServerEndpoint` 的事）。
//   三件事分開，因為它們失敗時的下一步不同。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Proc
{
    /// <summary>等一筆 Cmd 的結局。⛔ 三態，**不得壓成 bool**。</summary>
    public enum SCP_ServerCmdOutcome
    {
        /// <summary>判定檔說成功。</summary>
        Success = 0,

        /// <summary>判定檔說失敗（<see cref="SCP_ServerCmdWait.Detail"/> 是它的第一行）。</summary>
        Failed = 1,

        /// <summary>
        /// **等不到判定檔**。⛔ 這不是「失敗」—— 那一筆可能已經做完了。
        /// <para>🩸 補送是危險的：酒館 seq 全域遞增，重送 ＝ 多一則。⇒ 呼叫端要先回讀。</para>
        /// </summary>
        Timeout = 2,
    }

    /// <summary>一次等待的結果。</summary>
    public readonly struct SCP_ServerCmdWait
    {
        public SCP_ServerCmdWait(SCP_ServerCmdOutcome iOutcome, string iCmdId, string iDetail,
                                 IReadOnlyList<KeyValuePair<string, string>> iValues,
                                 IReadOnlyList<string> iLines)
        {
            Outcome = iOutcome;
            CmdId = iCmdId ?? "";
            Detail = iDetail ?? "";
            Values = iValues ?? new List<KeyValuePair<string, string>>();
            Lines = iLines ?? new List<string>();
        }

        public SCP_ServerCmdOutcome Outcome { get; }
        public string CmdId { get; }
        public string Detail { get; }

        /// <summary>執行端回報的純量（`values`）。</summary>
        public IReadOnlyList<KeyValuePair<string, string>> Values { get; }

        /// <summary>執行端的人可讀行（`lines`）。</summary>
        public IReadOnlyList<string> Lines { get; }

        public bool Ok => Outcome == SCP_ServerCmdOutcome.Success;

        /// <summary>取一個 value；沒有就回空字串。</summary>
        public string Value(string iKey)
        {
            foreach (KeyValuePair<string, string> kv in Values)
                if (string.Equals(kv.Key, iKey, StringComparison.Ordinal)) return kv.Value;
            return "";
        }
    }

    /// <summary>檔案協議的 client：寫 queue ＋ trigger，等 `_cmd_results/&lt;id&gt;.json`。</summary>
    public static class SCP_ServerCmdClient
    {
        /// <summary>這個 client 的名字 —— 會落進判定檔的 `client` 欄（「哪個入口送的」）。</summary>
        public const string ClientId = "unity-editor";

        /// <summary>
        /// queue 檔的路徑。⛔ **不自己拼** —— 走 <see cref="SCP_DataPaths"/> 那一份。
        /// <para>🩸 我第一版自己拼了一套（把整個 lane 當資料夾名、冒號換成 `-`），
        /// 而協議其實是**資料夾＋子分道**：`queues/&lt;folder&gt;/queue-&lt;lane&gt;.json`，子分道住在**檔名**裡。
        /// ⇒ 那一版會把同一條 lane 寫到 Senate 那側**不會去看**的地方，而兩邊都不會報錯。
        /// 📌 教訓：協議已經有一份實作在 SCP_Core 裡（兩個宿主都編得到），我卻先造了第二份。</para>
        /// </summary>
        public static string QueuePath(string iServerRoot, string iLane)
            => SCP_DataPaths.QueueFile(new SCP_DataRoot(iServerRoot), iLane);

        public static string TriggerPath(string iServerRoot, string iLane)
            => SCP_DataPaths.TriggerFile(new SCP_DataRoot(iServerRoot), iLane);

        public static string QueueFolder(string iServerRoot, string iLane)
            => SCP_DataPaths.QueueFolder(new SCP_DataRoot(iServerRoot), iLane);

        public static string ResultPath(string iServerRoot, string iCmdId)
            => Clean(iServerRoot) + "/" + SCP_DataPaths.CmdResultsDirName + "/" + iCmdId + ".json";

        static string Clean(string iPath) => (iPath ?? "").Replace("\\", "/").TrimEnd('/');

        /// <summary>產一個 cmd id（形狀與 `AgentCmdClient.MakeId` 相同：本地時間＋6 碼亂數＋型別小寫）。</summary>
        public static string MakeId(string iCmdType)
            => DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)
               + "-" + Guid.NewGuid().ToString("N").Substring(0, 6)
               + "-" + (iCmdType ?? "").ToLowerInvariant();

        /// <summary>
        /// append 一筆 OneShot 指令到 `queue.json` 並寫 `pending.trigger`，回傳 cmd_id。
        /// <para>⚠ 既有指令（含本版不認得的欄位）**原樣保留** —— 讀改寫，⛔ 不整檔重生。</para>
        /// <para>🔴 **讀改寫整段在 <see cref="SCP_FileLock"/> 裡**（TASK-0263）：這顆 queue 有多個
        /// 寫入端（每顆 CLI／Unity Editor／Server 執行器）。沒有互斥時兩邊各自讀到同一份舊內容、
        /// 各自寫回，**後寫的把先寫的那一筆整個吃掉** —— 而每一層都回報成功。
        /// 實測（2026-09-21，60 筆併發委派）：落盤 55，client **全部 exit 0**。</para>
        /// <para>⛔ 別把 `WriteAtomic` 讀成已經有互斥了：它保護的是「寫到一半的檔」，
        /// 不是「讀到一半的世界」——兩者中間隔著一整個決策。</para>
        /// </summary>
        public static string Submit(string iServerRoot, string iLane, string iCmdType,
                                    IReadOnlyDictionary<string, string> iArgs)
        {
            string aCmdId = MakeId(iCmdType);
            string aFolder = QueueFolder(iServerRoot, iLane);
            Directory.CreateDirectory(aFolder);
            string aQueuePath = QueuePath(iServerRoot, iLane);

            using (SCP.Core.Io.SCP_FileLock.Acquire(aQueuePath))
            {
                // ⚠ 載入**在鎖裡面**：拿著鎖去寫一份拿鎖之前讀的副本，鎖一點忙都幫不上。
                SCP_JsonData aRoot = LoadQueue(aQueuePath);
                SCP_JsonData aCommands = aRoot["Commands"];

                SCP_JsonData aArgs = SCP_JsonData.NewObject();
                if (!iArgs.ContainsKey("_caller_client")) aArgs.Set("_caller_client", SCP_JsonData.NewString(ClientId));
                foreach (KeyValuePair<string, string> kv in iArgs) aArgs.Set(kv.Key, SCP_JsonData.NewString(kv.Value));

                SCP_JsonData aCmd = SCP_JsonData.NewObject();
                aCmd.Set("Id", SCP_JsonData.NewString(aCmdId));
                aCmd.Set("Type", SCP_JsonData.NewString(iCmdType));
                aCmd.Set("Mode", SCP_JsonData.NewString("OneShot"));
                aCmd.Set("RunCount", SCP_JsonData.NewNumber(0));
                aCmd.Set("Args", aArgs);
                aCmd.Set("CreatedAt", SCP_JsonData.NewString(UtcStamp()));
                aCmd.Set("LastRunAt", SCP_JsonData.NewNull());
                aCmd.Set("LastRunResult", SCP_JsonData.NewNull());
                aCmd.Set("LastRunError", SCP_JsonData.NewNull());
                aCmd.Set("Description", SCP_JsonData.NewNull());
                aCommands.Add(aCmd);

                WriteAtomic(aQueuePath, SCP_JsonWriter.Write(aRoot) + "\n");
            }

            // ⚠ trigger 在鎖**外面**：它不是讀改寫（整檔覆寫，內容只是「有人送東西了」），
            //   而且必須在 queue 落盤之後才寫 —— 反過來的話執行器可能在看到 trigger 時讀到舊 queue。
            SCP_JsonData aTrigger = SCP_JsonData.NewObject();
            aTrigger.Set("createdAt", SCP_JsonData.NewString(UtcStamp()));
            aTrigger.Set("submittedBy", SCP_JsonData.NewString(ClientId + " → " + iCmdType));
            File.WriteAllText(TriggerPath(iServerRoot, iLane),
                              SCP_JsonWriter.Write(aTrigger) + "\n", new UTF8Encoding(false));
            return aCmdId;
        }

        /// <summary>
        /// 等一筆 cmd 的判定檔。⛔ 判定的**唯一**來源是 `_cmd_results/&lt;id&gt;.json` ——
        /// 「從 queue 消失」只代表結束，不代表成功。
        /// </summary>
        public static SCP_ServerCmdWait Wait(string iServerRoot, string iCmdId,
                                             double iTimeoutSec, double iPollSec = 0.1)
        {
            string aPath = ResultPath(iServerRoot, iCmdId);
            var aSw = System.Diagnostics.Stopwatch.StartNew();
            int aSleepMs = (int)Math.Max(10, iPollSec * 1000);
            while (aSw.Elapsed.TotalSeconds < iTimeoutSec)
            {
                if (File.Exists(aPath))
                {
                    SCP_ServerCmdWait? aRead = TryReadResult(aPath, iCmdId);
                    // 檔剛出現時可能只寫了一半 —— 讀不開就再等一圈，⛔ 不當成失敗。
                    if (aRead.HasValue) return aRead.Value;
                }
                System.Threading.Thread.Sleep(aSleepMs);
            }
            return new SCP_ServerCmdWait(SCP_ServerCmdOutcome.Timeout, iCmdId,
                "等不到判定檔（" + iTimeoutSec.ToString("0.#", CultureInfo.InvariantCulture) + "s）："
                + aPath + " ⇒ ⛔ **這不是「失敗」** —— 那一筆可能已經做完了。"
                + "補送之前先回讀（酒館 seq 全域遞增，重送 ＝ 多一則）。",
                new List<KeyValuePair<string, string>>(), new List<string>());
        }

        static SCP_ServerCmdWait? TryReadResult(string iPath, string iCmdId)
        {
            try
            {
                SCP_JsonData aJson = SCP_JsonParser.Parse(File.ReadAllText(iPath));
                string aVerdict = aJson.GetString("result", "");
                if (aVerdict.Length == 0) return null;      // 半寫檔 ⇒ 再等一圈

                var aValues = new List<KeyValuePair<string, string>>();
                if (aJson.Contains("values"))
                {
                    SCP_JsonData aArr = aJson["values"];
                    for (int i = 0; i < aArr.Count; ++i)
                    {
                        SCP_JsonData aKv = aArr[i];
                        string aKey = aKv.GetString("key", "");
                        if (aKey.Length > 0) aValues.Add(new KeyValuePair<string, string>(aKey, aKv.GetString("value", "")));
                    }
                }
                var aLines = new List<string>();
                if (aJson.Contains("lines"))
                {
                    SCP_JsonData aArr = aJson["lines"];
                    for (int i = 0; i < aArr.Count; ++i) aLines.Add(aArr[i].AsString());
                }

                bool aOk = string.Equals(aVerdict, "Success", StringComparison.Ordinal);
                string aDetail = aOk
                    ? "Server 回報成功（exit " + aJson.GetInt("exit_code", 0) + "）"
                    : "Server 回報失敗（exit " + aJson.GetInt("exit_code", 1) + "）："
                      + aJson.GetString("error", aLines.Count > 0 ? aLines[0] : "(沒說原因)");
                return new SCP_ServerCmdWait(aOk ? SCP_ServerCmdOutcome.Success : SCP_ServerCmdOutcome.Failed,
                                             iCmdId, aDetail, aValues, aLines);
            }
            catch (Exception) { return null; }   // 半寫檔／壞檔 ⇒ 再等一圈（逾時那條會兜住）
        }

        static string UtcStamp()
            => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

        static SCP_JsonData LoadQueue(string iPath)
        {
            if (File.Exists(iPath))
            {
                try
                {
                    SCP_JsonData aNode = SCP_JsonParser.Parse(File.ReadAllText(iPath));
                    if (aNode.Contains("Commands")) return aNode;
                }
                catch (Exception) { /* 壞檔 ⇒ 用空骨架續行（舊內容由 atomic replace 覆蓋） */ }
            }
            SCP_JsonData aRoot = SCP_JsonData.NewObject();
            aRoot.Set("Commands", SCP_JsonData.NewArray());
            return aRoot;
        }

        /// <summary>temp → move overwrite；撞檔鎖 backoff 重試（Editor 與 Server 可能同時碰同一個 queue）。</summary>
        static void WriteAtomic(string iPath, string iPayload)
        {
            string aTmp = iPath + ".tmp." + Guid.NewGuid().ToString("N").Substring(0, 6);
            Exception? aLast = null;
            for (int i = 0; i < 5; ++i)
            {
                try
                {
                    File.WriteAllText(aTmp, iPayload, new UTF8Encoding(false));
                    if (File.Exists(iPath)) File.Delete(iPath);
                    File.Move(aTmp, iPath);
                    return;
                }
                catch (IOException e)
                {
                    aLast = e;
                    System.Threading.Thread.Sleep(20 * (i + 1));
                }
            }
            try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch { }
            throw new IOException("queue.json 寫不進去（重試 5 次）：" + iPath, aLast);
        }
    }
}
