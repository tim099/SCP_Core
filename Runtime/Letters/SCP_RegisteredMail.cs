// 區塊職責：寫一封**掛號信**（收件匣＋寄件備份兩份）—— 兩個宿主共用的那一份。
// 物理意義：移植自 UCL_Core `UCL_RegisteredMailIO.Send`（TASK-0312，epic 0295 ③ 第三刀：creative 留念信要在 Senate 寄得出去）。
//          Editor 那支改成呼叫這裡 ⇒ 信件格式只剩一份（py 端 `mailbox` 掃描與 ack 回執都靠這個格式，分岔不會報錯，
//          只會「信照讀、寄件者永遠等不到回執」）。
// 數值影響：寫 `letters/<to>/mailbox/<ts>__from_<from>.md` 與 `letters/<from>/outbox/<ts>__to_<to>.md`，不動帳。
//          ⛔ 寫檔失敗回 false，**絕不拋例外** —— 呼叫端多半是「主操作已經完成」的路徑（錢已入帳／訊息已發出），
//          一封通知信寫失敗不該讓它看起來像失敗。原因放在 oError 給呼叫端說出來（不靜默）。
// ⚠ 落檔改走 `SCP_TextFile.ReplaceOrMove`：Editor 舊版是「先 Delete 再 Move」（中間一格檔案不存在，Coding_Standards §1.1 點名的錯法）。
//   產物位元組不變（同一份 UTF-8 無 BOM 內容，換行由 AppendLine ＝ 本機 NewLine，兩個宿主都在 Windows 上跑）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Io;

namespace SCP.Core.Letters
{
    public static class SCP_RegisteredMail
    {
        /// <summary>系統信的寄件者（免費、不碰帳）。</summary>
        public const string SystemSender = "tavern-keeper";

        const string MailboxDirName = "mailbox";   // 收件者端（投遞用）
        const string OutboxDirName = "outbox";     // 寄件者端（存證用）

        /// <summary>寄一封**系統**掛號信（fee 固定 0 —— 系統信不收費是規則不是預設值，⛔ 不開放覆寫）。</summary>
        public static bool SendSystemMail(string iLettersRoot, string iTo, string iSubject, string iBody,
                                          out string oPathInbox, out string oError,
                                          int? iDeliverAtWake = null, string? iRefId = null)
            => Send(iLettersRoot, SystemSender, iTo, iSubject, iBody, 0,
                    string.IsNullOrEmpty(iRefId) ? "system-mail" : iRefId!, iDeliverAtWake, out oPathInbox, out oError);

        /// <summary>
        /// 寫兩份信件檔。收件者或寄件者空白、內文空白 ⇒ 回 false 且 <paramref name="oError"/> 說明（那不是錯誤，是沒有可投遞的東西）。
        /// </summary>
        public static bool Send(string iLettersRoot, string iFrom, string iTo, string iSubject, string iBody,
                                int iFee, string iFeeRef, int? iDeliverAtWake, out string oPathInbox, out string oError)
        {
            oPathInbox = ""; oError = "";
            if (string.IsNullOrWhiteSpace(iTo) || string.IsNullOrWhiteSpace(iFrom)) { oError = "沒有收件者或寄件者"; return false; }
            if (string.IsNullOrWhiteSpace(iBody)) { oError = "內文是空的"; return false; }
            if (string.IsNullOrWhiteSpace(iLettersRoot)) { oError = "沒有 letters 根"; return false; }

            string aFrom = iFrom.Trim();
            string aTo = iTo.Trim();
            // ⚠ ts 格式必須是 py 的 "%Y%m%dT%H%M%SZ" —— ack 靠 `檔名.split("__")[0]` 反查 outbox 副本，
            //   格式一變，已讀回執就悄悄對不上（信照讀，寄件者永遠等不到回執）。
            string aTs = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string aContent = BuildContent(aFrom, aTo, iSubject, iBody, iFee, iFeeRef, iDeliverAtWake);
            try
            {
                string aMailbox = Path.Combine(iLettersRoot, aTo, MailboxDirName);
                string aOutbox = Path.Combine(iLettersRoot, aFrom, OutboxDirName);
                Directory.CreateDirectory(aMailbox);
                Directory.CreateDirectory(aOutbox);
                string aIn = Path.Combine(aMailbox, $"{aTs}__from_{aFrom}.md");
                WriteAtomic(aIn, aContent);
                WriteAtomic(Path.Combine(aOutbox, $"{aTs}__to_{aTo}.md"), aContent);
                oPathInbox = aIn.Replace('\\', '/');
                return true;
            }
            catch (Exception e)
            {
                oError = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>信件內文（frontmatter ＋ 標題 ＋ 本文）—— 逐字沿用 Editor 版，py 端靠 frontmatter 讀。</summary>
        public static string BuildContent(string iFrom, string iTo, string iSubject, string iBody,
                                          int iFee, string iFeeRef, int? iDeliverAtWake)
        {
            string aSubj = Flatten(iSubject);
            var aSb = new StringBuilder();
            aSb.AppendLine("---");
            aSb.AppendLine("type: registered_mail");
            aSb.AppendLine($"from: {iFrom}");
            aSb.AppendLine($"to: {iTo}");
            aSb.AppendLine($"sent_at: {DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture)}Z");
            aSb.AppendLine($"fee: {iFee}");
            aSb.AppendLine($"fee_ref: {Flatten(iFeeRef)}");
            if (!string.IsNullOrEmpty(aSubj)) aSb.AppendLine($"subject: {aSubj}");
            if (iDeliverAtWake.HasValue) aSb.AppendLine($"deliver_at_wake: {iDeliverAtWake.Value}");
            aSb.AppendLine("---");
            aSb.AppendLine();
            aSb.AppendLine($"# 📮 掛號信 — 寄件者 @{iFrom} → 收件者 @{iTo}");
            aSb.AppendLine();
            if (!string.IsNullOrEmpty(aSubj)) { aSb.AppendLine($"**主旨**：{aSubj}"); aSb.AppendLine(); }
            aSb.AppendLine(iDeliverAtWake.HasValue ? $"**投遞時點**：wake #{iDeliverAtWake.Value}" : "**投遞時點**：下次醒來");
            aSb.AppendLine();
            aSb.AppendLine("---");
            aSb.AppendLine();
            aSb.AppendLine(iBody.Trim());
            return aSb.ToString();
        }

        /// <summary>壓成單行 —— frontmatter 的值含換行會把後面的欄位全部吃掉。</summary>
        static string Flatten(string? iS) =>
            string.IsNullOrEmpty(iS) ? "" : iS!.Replace("\r", " ").Replace("\n", " ").Trim();

        static void WriteAtomic(string iPath, string iContent)
        {
            // UTF8 無 BOM —— py 端以 encoding="utf-8" 讀，BOM 會混進 frontmatter 的第一個 "---"
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iContent, new UTF8Encoding(false));
            SCP_TextFile.ReplaceOrMove(aTmp, iPath);
        }
    }
}
