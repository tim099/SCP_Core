// 區塊職責：**Server 沒在跑就把它拉起來**的那條決策迴圈（等待／四態判定／該說什麼話）。
// 物理意義：TASK-0209 A4／A5 的本體，TASK-0267 搬進共用層（Tim 2026-09-22 拍板）。
//           D20 ⑦ 原本是「手動啟動，沒跑就 exit 3 印指令，不做降級路」。
//           銀行 ledger 搬進 Server 之後，那條規矩的代價會落在每一次扣款上
//           （早安自介、commit 領薪、發文計酬、跨日保管費全部要人先開一個終端機）
//           ⇒ Tim 翻掉 ⑦ 的「手動」那半。**「不降級」那半沒有翻** —— 本檔只負責把 Server 拉起來，
//           拉不起來仍然讓呼叫端失敗，⛔ 絕不改成本地跑（本地跑＝第二個寫入者）。
// 數值影響：本層自己**不碰任何檔案、不生任何行程** —— spawn 與路徑由呼叫端注入。
//           它只做三件事：問一次「在跑嗎」、（沒跑就）叫一次 spawn、然後輪詢等到上線或逾時。
//
// 🔴 為什麼「怎麼 spawn」不在這裡（TASK-0267 切線的判準）：
//   spawn 那半的每一格都是**平台／宿主**的事 —— `Environment.ProcessPath`（.NET 6+）、
//   `OperatingSystem.IsWindows()`（.NET 5+）、WMI 脫樹、`dotnet <dll>` 那條回退路。
//   ⛔ netstandard2.1 沒有前兩個 API，而 Unity 那側的 `ProcessPath` 是 `Unity.exe`
//   ⇒ 那段邏輯在另一個宿主底下**不是比較難，是沒有受詞**。
//   ⇒ 所以進共用層的是「**判斷與等待**」，留在宿主的是「**怎麼生出那顆行程**」。
//   📌 判準一般化：一個概念要進共用層，條件是**它在兩個宿主底下是同一件事**，
//     ⛔ 不是「有人需要它」。（同理 `Probe`／`BuildId` 那幾格沒有搬 —— 見驗收 ②。）
//
// 🩸 設計判準（每一條都是「錯的時候長什麼樣」決定的）：
//   ① **「確保有一顆」不是「起我的那顆」**：N 顆呼叫端同時發現沒在跑 ⇒ N 顆一起 spawn，
//      而單例鎖（A2）只讓一顆活。其餘那幾顆**不該報錯** —— 它們要等的是「有沒有一顆好了」，
//      不是「我 spawn 的那顆好了沒」。⇒ 等待條件一律走注入的 `iIsRunning`，**不看 pid**。
//      ⛔ 沒有 A2 的話這裡就是**自動製造**多個寫入者（2026-09-14 實測：拿掉鎖，4 顆全登記成功）。
//   ② **「正在啟動」與「啟動失敗」必須不同形**（A4 條文）：兩者都是「現在還沒有 Server」，
//      而處置相反（再等 ／ 去看 log）。⇒ 四態 enum ＋ <see cref="SCP_ServerAutoStart.Explain"/> 兩段不同的話。
//   ③ **啟動期的 log 一定要落檔**（A5）：detached 子行程沒有 stdout 可以給人看，
//      而「它為什麼沒起來」只活在那幾行裡。⇒ 本層要求呼叫端給得出 `iLogPath`。
//   ④ **不重試、不退避**：拉一次拉不起來就交給人。自動重試會把「環境壞了」變成一個
//      每次都慢 N 秒、而且永遠不報錯的黑洞。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

namespace SCP.Core.Proc
{
    /// <summary>拉起 Server 的結果 —— 四態，⛔ 不是 bool（「還沒好」與「起不來」處置相反）。</summary>
    public enum SCP_ServerAutoStartOutcome
    {
        /// <summary>本來就在跑（沒做任何事）。</summary>
        AlreadyRunning,

        /// <summary>拉起來了，而且已經可以收工作。</summary>
        Started,

        /// <summary>spawn 出去了，但等到逾時仍然沒有 Server 上線 —— **不知道它是慢還是死了**。</summary>
        TimedOut,

        /// <summary>連 spawn 都失敗（找不到執行檔、權限…）——**確定沒起來**。</summary>
        SpawnFailed,
    }

    public sealed class SCP_ServerAutoStartReport
    {
        public SCP_ServerAutoStartOutcome Outcome;

        /// <summary>人讀的原因（TimedOut／SpawnFailed 時一定有話說；空字串是 bug）。</summary>
        public string Detail = "";

        /// <summary>啟動 log 的路徑（spawn 成功就一定有）—— 失敗時呼叫端要指得出這個檔。</summary>
        public string? LogPath;

        public bool Ok => Outcome == SCP_ServerAutoStartOutcome.AlreadyRunning
                          || Outcome == SCP_ServerAutoStartOutcome.Started;
    }

    public static class SCP_ServerAutoStart
    {
        /// <summary>
        /// 生出那顆 Server —— **由宿主實作**（見檔頭「為什麼不在這裡」）。
        /// <para>回 <c>true</c> 代表子行程生出來了（⛔ 不代表它已經能收工作 —— 那由 <c>iIsRunning</c> 回答）。</para>
        /// </summary>
        public delegate bool SpawnFunc(out int oChildPid, out string oErr);

        /// <summary>
        /// 等 Server 上線的上限（預設）。啟動要載 assembly ＋ 探測 ⇒ 給寬一點，但不能無限（無限＝掛住）。
        /// <para>🩸 TASK-0267：它原本是寫死的 <c>const</c>，而那讓 <see cref="SCP_ServerAutoStartOutcome.TimedOut"/>
        /// **結構上測不到** —— 要驗它得真的等 20 秒，於是那一態永遠只有註解沒有讀數。
        /// ⇒ <see cref="Ensure"/> 收一個可選的覆寫值（預設仍是這個常數），
        /// 對拍就能把窗口撐到 600ms。⭐ 判準不是「加一個參數比較彈性」，是
        /// **一個量不到的態跟一個壞掉的態在計數上同形**。</para>
        /// </summary>
        public const int ReadyTimeoutMs = 20000;

        const int PollMs = 200;

        /// <summary>
        /// 確保有一顆 Server 在跑；沒有就拉一顆起來，等到它能收工作為止。
        /// <para>⚠ 等的是「**有沒有一顆**」不是「我 spawn 的那顆」—— 見檔頭判準①。</para>
        /// </summary>
        /// <param name="iIsRunning">問一次「現在有沒有一顆在跑」。⚠ 由宿主決定判準（registry／心跳），本層不自己判。</param>
        /// <param name="iSpawn">生出一顆（宿主實作）。</param>
        /// <param name="iLogPath">給 child 的 pid，回那一顆的啟動 log 路徑（判準③）。</param>
        /// <param name="iLog">過程要說的話；<c>null</c> ＝ 不說。</param>
        public static SCP_ServerAutoStartReport Ensure(Func<bool> iIsRunning, SpawnFunc iSpawn,
                                                       Func<int, string> iLogPath, Action<string>? iLog = null,
                                                       int iReadyTimeoutMs = ReadyTimeoutMs)
        {
            if (iIsRunning == null) throw new ArgumentNullException(nameof(iIsRunning));
            if (iSpawn == null) throw new ArgumentNullException(nameof(iSpawn));
            if (iLogPath == null) throw new ArgumentNullException(nameof(iLogPath));

            var aReport = new SCP_ServerAutoStartReport();
            if (iIsRunning())
            {
                aReport.Outcome = SCP_ServerAutoStartOutcome.AlreadyRunning;
                return aReport;
            }

            // 🩸 兩格實測（2026-09-14）決定了 log 的形狀，兩格都是「最需要它的時候它不在」：
            //   ① 共用一份 `_server_start.log` ⇒ 4 顆同時 spawn 時 `WriteAllText` 把前三份**截斷覆蓋**。
            //   ② 改成一顆一份之後仍然空的：輸出原本是**接到 parent 的管線**，
            //      而 parent 跑完就退 ⇒ **非同步讀取器跟著死**，
            //      那三顆「拿不到單例鎖」的訊息一個字都沒留下。
            //   ⇒ 結論：**log 由 child 自己寫**，parent 不接管線。
            //     parent 只負責記下 child 的 pid，好指得出**那一份**。
            if (!iSpawn(out int aChildPid, out string aSpawnErr))
            {
                aReport.Outcome = SCP_ServerAutoStartOutcome.SpawnFailed;
                aReport.Detail = aSpawnErr ?? "";
                return aReport;
            }
            aReport.LogPath = iLogPath(aChildPid);
            iLog?.Invoke($"⤷ Server 沒在跑 ⇒ 已拉起一顆，等它上線（最多 {iReadyTimeoutMs / 1000.0:0.#}s）…");

            var aSw = Stopwatch.StartNew();
            while (aSw.ElapsedMilliseconds < iReadyTimeoutMs)
            {
                Thread.Sleep(PollMs);
                // ⚠ 條件是宿主的 IsRunning，不是「我那顆 pid 活著」：
                //   輸掉單例鎖的那幾顆會自己退出，而真正上線的是別人 spawn 的那顆 —— 那也算數。
                if (iIsRunning())
                {
                    aReport.Outcome = SCP_ServerAutoStartOutcome.Started;
                    aReport.Detail = $"{aSw.ElapsedMilliseconds} ms";
                    return aReport;
                }
            }

            aReport.Outcome = SCP_ServerAutoStartOutcome.TimedOut;
            aReport.Detail = $"等了 {iReadyTimeoutMs / 1000.0:0.#}s 仍然沒有 Server 上線";
            return aReport;
        }

        /// <summary>把四態翻成呼叫端要說的話 —— **「還沒好」與「起不來」的出口不同**（檔頭判準②）。</summary>
        public static void Explain(SCP_ServerAutoStartReport iReport, List<string> iLines)
        {
            if (iReport == null || iLines == null) return;
            switch (iReport.Outcome)
            {
                case SCP_ServerAutoStartOutcome.TimedOut:
                    iLines.Add($"✗ 自動啟動：{iReport.Detail} —— 這一筆**沒有送出**。");
                    iLines.Add("  ⚠ 這是「**不知道**」不是「起不來」：它可能還在載入，也可能已經死了。");
                    iLines.Add($"  下一步：先看啟動 log → {iReport.LogPath}");
                    iLines.Add("        再看現況 → `senate server status`（它上線了就直接重跑這一行）");
                    break;
                case SCP_ServerAutoStartOutcome.SpawnFailed:
                    iLines.Add($"✗ 自動啟動**連子行程都沒起來**：{iReport.Detail} —— 這一筆**沒有送出**。");
                    iLines.Add("  ⚠ 這是「**確定沒起來**」：不必等，環境有問題。");
                    iLines.Add("  下一步：手動跑一次 `senate server start`，看它印什麼。");
                    break;
            }
        }
    }
}
