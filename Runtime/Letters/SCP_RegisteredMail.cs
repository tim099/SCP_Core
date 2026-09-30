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

        // ── 讀取端：收件匣／送達章／已讀（TASK-0333／0347，移植自 registered_mail.py）─────────────
        // 區塊職責：「投遞」＝早安 brief 列出到期未 ack 的信（0347：09-04 python brief 退場後這段沒有人做，90 封沒人看得到）。
        // 物理意義（規則逐條照 py 版，那是付過錢的東西的保證）：
        //   · 到期 ＝ 沒指定 wake，或指定的 wake ≤ 目前 wake。指定 #100 而現在 #105 ⇒ **仍算到期**（晚醒不該吃掉別人付過錢的信）。
        //   · 已 ack（frontmatter 有 read_at）才除名 —— **唯一**的除名條件。⛔ 不用「列過一次就消失」：
        //     只要有一次 render 沒被看到（溢出／那天沒讀 brief），信就永遠消失且無人知曉。
        //   · 送達章 first_seen_wake：第一次端上桌蓋一次、之後不覆寫；⚠ 蓋章≠除名。
        //   · 只改 frontmatter，⛔ 不動內文 —— 信的內容是寄件者寫的。

        /// <summary>收件匣裡的一封信（讀自 frontmatter）。</summary>
        public sealed class MailItem
        {
            public string Path = "";
            public string FileName = "";
            public string From = "";
            public string Subject = "";
            /// <summary>指定投遞的 wake；null ＝ 下次醒來（壞值也當 null —— 不吞信）。</summary>
            public int? DeliverAtWake;
            public string FirstSeenWake = "";
            public string ReadAt = "";
            public string Fee = "";
        }

        /// <summary>persona／收件者名稱合法（不含路徑字元）。⛔ 不合法就不碰檔案系統。</summary>
        public static bool IsValidPersonaName(string? iName)
            => !string.IsNullOrWhiteSpace(iName) && iName!.Trim().IndexOfAny(new[] { '/', '\\', ':' }) < 0
               && !iName.Contains("..");

        public static string MailboxDir(string iLettersRoot, string iPersona)
            => Path.Combine(iLettersRoot, iPersona, MailboxDirName).Replace('\\', '/');

        /// <summary>
        /// 列出 <paramref name="iPersona"/> 的未 ack 信件，分成「到期」與「未到期」。
        /// <paramref name="iWake"/> ＝ null ⇒ 全部算到期（py 版 `wake_count is None` 同語意：ack 全部用）。
        /// </summary>
        public static void ListUnread(string iLettersRoot, string iPersona, int? iWake,
                                      out System.Collections.Generic.List<MailItem> oDue,
                                      out System.Collections.Generic.List<MailItem> oLater)
        {
            oDue = new System.Collections.Generic.List<MailItem>();
            oLater = new System.Collections.Generic.List<MailItem>();
            if (!IsValidPersonaName(iPersona)) return;
            string aBox = MailboxDir(iLettersRoot, iPersona);
            if (!Directory.Exists(aBox)) return;
            string[] aFiles = Directory.GetFiles(aBox, "*.md");
            Array.Sort(aFiles, StringComparer.Ordinal);
            foreach (string f in aFiles)
            {
                MailItem m = ReadItem(f);
                if (m.ReadAt.Length > 0) continue;   // 已確認閱讀 ⇒ 除名
                if (m.DeliverAtWake == null || iWake == null || iWake.Value >= m.DeliverAtWake.Value) oDue.Add(m);
                else oLater.Add(m);
            }
        }

        public static MailItem ReadItem(string iPath)
        {
            var d = ReadFrontmatter(iPath);
            var m = new MailItem { Path = iPath.Replace('\\', '/'), FileName = Path.GetFileName(iPath) };
            d.TryGetValue("from", out m.From);
            d.TryGetValue("subject", out m.Subject);
            d.TryGetValue("first_seen_wake", out m.FirstSeenWake);
            d.TryGetValue("read_at", out m.ReadAt);
            d.TryGetValue("fee", out m.Fee);
            m.From ??= ""; m.Subject ??= ""; m.FirstSeenWake ??= ""; m.ReadAt ??= ""; m.Fee ??= "";
            if (d.TryGetValue("deliver_at_wake", out string? aRaw)
                && int.TryParse(aRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aW)) m.DeliverAtWake = aW;
            return m;
        }

        /// <summary>送達章：第一次端上桌時蓋 first_seen_wake（已蓋過不覆寫）。回是否有變動。</summary>
        public static bool StampDelivered(string iPath, int iWake)
        {
            if (ReadItem(iPath).FirstSeenWake.Length > 0) return false;
            return SetFrontmatterField(iPath, "first_seen_wake", iWake.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>ack 一封的結果（逐封回報，⛔ 不合成一個「成功 N 封」）。</summary>
        public sealed class AckResult
        {
            public string FileName = "";
            /// <summary>acked ／ already ／ missing ／ failed</summary>
            public string State = "";
            public bool MirrorUpdated;
            public string FirstSeenWake = "";
        }

        /// <summary>
        /// 確認閱讀：寫 read_at，並回寫寄件者 outbox 副本（同 ts、同對象；找不到不擋 ack）。
        /// <paramref name="iFileName"/> 空 ⇒ ack 全部未讀（不看 wake —— 同 py 版）。
        /// </summary>
        public static System.Collections.Generic.List<AckResult> Ack(string iLettersRoot, string iPersona, string? iFileName)
        {
            var aOut = new System.Collections.Generic.List<AckResult>();
            if (!IsValidPersonaName(iPersona)) return aOut;
            string aBox = MailboxDir(iLettersRoot, iPersona);
            var aTargets = new System.Collections.Generic.List<string>();
            if (!string.IsNullOrWhiteSpace(iFileName))
            {
                string aName = Path.GetFileName(iFileName!.Trim());   // ⛔ 只收檔名，不收路徑
                aTargets.Add(Path.Combine(aBox, aName));
            }
            else
            {
                ListUnread(iLettersRoot, iPersona, null, out var aDue, out _);
                foreach (MailItem m in aDue) aTargets.Add(m.Path);
            }
            string aNow = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "Z";
            foreach (string f in aTargets)
            {
                var r = new AckResult { FileName = Path.GetFileName(f) };
                aOut.Add(r);
                if (!File.Exists(f)) { r.State = "missing"; continue; }
                MailItem m = ReadItem(f);
                r.FirstSeenWake = m.FirstSeenWake;
                if (m.ReadAt.Length > 0) { r.State = "already"; continue; }
                if (!SetFrontmatterField(f, "read_at", aNow)) { r.State = "failed"; continue; }
                r.State = "acked";
                if (IsValidPersonaName(m.From))
                {
                    string aTs = r.FileName.Split(new[] { "__" }, 2, StringSplitOptions.None)[0];
                    string aMirror = Path.Combine(iLettersRoot, m.From.Trim(), OutboxDirName, $"{aTs}__to_{iPersona}.md");
                    if (File.Exists(aMirror)) r.MirrorUpdated = SetFrontmatterField(aMirror, "read_at", aNow);
                }
            }
            return aOut;
        }

        static System.Collections.Generic.Dictionary<string, string> ReadFrontmatter(string iPath)
        {
            var d = new System.Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal);
            try
            {
                string t = File.ReadAllText(iPath, Encoding.UTF8).TrimStart('﻿').TrimStart();
                if (!t.StartsWith("---", StringComparison.Ordinal)) return d;
                int aEnd = t.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (aEnd < 0) return d;
                foreach (string raw in t.Substring(3, aEnd - 3).Split('\n'))
                {
                    int c = raw.IndexOf(':');
                    if (c <= 0) continue;
                    string k = raw.Substring(0, c).Trim();
                    if (k.Length > 0 && !d.ContainsKey(k)) d[k] = raw.Substring(c + 1).Trim();
                }
            }
            catch (Exception) { }   // 讀不了 ⇒ 空 frontmatter（當成「下次醒來、未讀」—— 不吞信）
            return d;
        }

        /// <summary>在 frontmatter 就地寫入／更新一個欄位；已是該值不重寫（冪等）。⛔ 不動內文。</summary>
        static bool SetFrontmatterField(string iPath, string iField, string iValue)
        {
            try
            {
                string aText = File.ReadAllText(iPath, Encoding.UTF8).TrimStart('﻿');
                string t = aText.TrimStart();
                if (!t.StartsWith("---", StringComparison.Ordinal)) return false;
                int aEnd = t.IndexOf("\n---", 3, StringComparison.Ordinal);
                if (aEnd < 0) return false;
                string aRest = t.Substring(aEnd);
                var aLines = new System.Collections.Generic.List<string>();
                foreach (string raw in t.Substring(3, aEnd - 3).Split('\n'))
                    if (raw.Trim().Length > 0) aLines.Add(raw.TrimEnd('\r'));
                bool aHit = false;
                for (int i = 0; i < aLines.Count; i++)
                {
                    int c = aLines[i].IndexOf(':');
                    if (c <= 0 || aLines[i].Substring(0, c).Trim() != iField) continue;
                    if (aLines[i].Substring(c + 1).Trim() == iValue) return false;
                    aLines[i] = iField + ": " + iValue; aHit = true; break;
                }
                if (!aHit) aLines.Add(iField + ": " + iValue);
                WriteAtomic(iPath, "---\n" + string.Join("\n", aLines) + aRest);
                return true;
            }
            catch (Exception) { return false; }
        }
    }
}
