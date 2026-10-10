// 區塊職責：寫一幅見人畫像 —— 事實源進自己的 sketchbook，公開層投遞到對方的 portraits。
// 物理意義：TASK-0305。檔名、frontmatter、CRLF 行尾逐位元組對齊 python `portraits.py write_portrait`，
//          既有讀者（SCP_PortraitView／portrait-next／portraits.py mine）不必改。
// ⚠ 不覆寫任何既有檔 —— 檔名帶 UTC 時間戳，同一天寫兩幅就是兩幅（改觀的形狀是多一個版本）。
// ⚠ private_body **只寫進 sketchbook**，投遞件裡不留任何「另有私層」的痕跡（Tim 2026-08-04 拍板）。
// ⚠ body 夾帶私層時由本層分層，不信呼叫端：有私層標記 ⇒ 標記以下移進私層；
//   沒標記卻有「🔒 …」行或「只給我自己看」字樣 ⇒ 擋下、兩個檔都不寫（Tim 2026-10-10：私層曾整段寄出）。
// 與 python 版刻意的差異：`about` 必須是現有 persona —— python 版照單全收，打錯字會在 letters/ 底下
//   長出一個不存在的人的 portraits/ 目錄，而沒有任何一層會喊。
// 數值影響：寫兩個新檔（sketch＋投遞件），不動任何既有檔。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.IO;
using System.Text;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    public sealed class SCP_PortraitWriteResult
    {
        public string? Error;
        public string SketchPath = "";
        public string DeliveredPath = "";
        /// <summary>與 python 版 cmd_write 同形的輸出行（回傳檔原樣附上）。</summary>
        public string Report = "";
    }

    public static class SCP_PortraitWriter
    {
        public const string PortraitsDirName = "portraits";

        public static SCP_PortraitWriteResult Write(string iLettersRoot, string iBy, string iAbout, string iBody,
                                                    string iHeadline, string iAffinity, string iPrivateBody)
        {
            var aOut = new SCP_PortraitWriteResult();
            string aBy = (iBy ?? "").Trim(), aAbout = (iAbout ?? "").Trim();
            string aPublic = (iBody ?? "").Trim();
            string aPrivate = (iPrivateBody ?? "").Trim();
            string aHeadline = (iHeadline ?? "").Trim(), aAffinity = (iAffinity ?? "").Trim();
            // body 是要寄出去的那一層 —— 呼叫端把私層也塞進來時，寫入端分層，不信呼叫端已經分好
            int aMoved = SplitPrivateFromBody(ref aPublic, ref aPrivate);
            string? aLeak = FindPrivateLookingLine(aPublic);
            if (aLeak != null)
            {
                aOut.Error = $"body 裡有像私層的一行（`{aLeak}`）但沒有私層標記 —— 照寫會整段寄給對方。"
                           + "私層請走 private_body，或在它上方單獨放一行 " + SCP_PortraitView.PrivateMarker;
                return aOut;
            }
            if (aPublic.Length == 0) { aOut.Error = "內容為空（body 必填）"; return aOut; }
            if (!SCP_PersonaProfile.Exists(iLettersRoot, aAbout))
            { aOut.Error = $"about='{aAbout}' 不是現有 persona —— 打錯字會替一個不存在的人開 portraits/ 目錄，本步不代建"; return aOut; }

            DateTime aNow = DateTime.UtcNow;
            string aTs = aNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            // python `isoformat()`：微秒 6 位（C# ticks 7 位 ⇒ 截掉一位）
            string aAt = aNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
            var aRoot = new SCP_LettersRoot(iLettersRoot);

            string Fm(string iKind, params string[] iExtra)
            {
                var sb = new StringBuilder();
                sb.Append("---\n").Append($"type: {iKind}\n").Append($"by: {aBy}\n").Append($"about: {aAbout}\n").Append($"at: {aAt}\n");
                if (aHeadline.Length > 0) sb.Append($"headline: {aHeadline}\n");
                if (aAffinity.Length > 0) sb.Append($"affinity_snapshot: {aAffinity}\n");
                foreach (string x in iExtra) sb.Append(x).Append('\n');
                sb.Append("---\n");
                return sb.ToString();
            }
            string aHead = $"# 🖼 {aAbout} — by {aBy}\n\n" + (aHeadline.Length > 0 ? $"**{aHeadline}**\n\n" : "");

            // ① 事實源：自己的 sketchbook（公開層 + 私層）
            string aSkDir = SCP_LettersPaths.SketchbookDir(aRoot, aBy);
            Directory.CreateDirectory(aSkDir);
            string aSkName = $"{aTs}__about_{aAbout}.md";
            string aSkPath = aSkDir + "/" + aSkName;
            string aSkBody = aPublic + (aPrivate.Length > 0
                ? $"\n\n{SCP_PortraitView.PrivateMarker}\n\n## 🔒 只給我自己看\n\n{aPrivate}" : "");
            // ② 投遞件：對方的 portraits（只有公開層）
            string aDDir = SCP_LettersPaths.PersonaDir(aRoot, aAbout) + "/" + PortraitsDirName;
            Directory.CreateDirectory(aDDir);
            string aDPath = aDDir + $"/{aTs}__by_{aBy}.md";
            if (File.Exists(aSkPath) || File.Exists(aDPath))
            { aOut.Error = $"同一秒已經有一幅（{aSkName}）—— 不覆寫，隔一秒再畫"; return aOut; }

            // python write_text 在 Windows 寫 CRLF —— 對齊它，否則同一個 sketchbook 裡行尾混兩種
            File.WriteAllText(aSkPath, Crlf(Fm("sketch", $"has_private: {(aPrivate.Length > 0 ? "true" : "false")}") + aHead + aSkBody + "\n"),
                              new UTF8Encoding(false));
            File.WriteAllText(aDPath, Crlf(Fm("portrait", $"delivered_at: {aAt}", $"derived_from: {aBy}/{SCP_LettersPaths.SketchbookDirName}/{aSkName}")
                                           + aHead + aPublic + "\n"), new UTF8Encoding(false));
            aOut.SketchPath = aSkPath;
            aOut.DeliveredPath = aDPath;

            // 與 python cmd_write 同形的輸出（第 N 幅／對某人第 K 幅：數 sketchbook 根層）
            int aMine = 0, aToHim = 0;
            foreach (string f in Directory.GetFiles(aSkDir, "*.md"))
            {
                aMine++;
                if (SCP_LetterText.ReadFrontmatterField(f, "about") == aAbout) aToHim++;
            }
            var r = new StringBuilder();
            r.AppendLine($"🖼 畫像已寫入：{aBy} → {aAbout}");
            r.AppendLine($"   事實源（含私層）: {aSkPath}");
            r.AppendLine($"   投遞件（公開層）: {aDPath}");
            if (aMoved > 0) r.AppendLine($"   ⚠ body 裡有私層標記 ⇒ 標記以下 {aMoved} 行已移進私層，沒有寄出（私層請直接走 private_body）");
            if (aPrivate.Length > 0) r.AppendLine("   🔒 私層只在 sketchbook —— 投遞件不留任何痕跡");
            r.AppendLine($"   （這是你畫過的第 {aMine} 幅；對 {aAbout} 的第 {aToHim} 幅）");
            aOut.Report = r.ToString();
            return aOut;
        }

        // body 裡有私層標記 ⇒ 標記以下併進私層（排在 private_body 前面），標記與其下第一個「只給我自己看」標題不留。
        // 回傳移走的非空行數；0 ＝ 沒有標記。
        static int SplitPrivateFromBody(ref string ioPublic, ref string ioPrivate)
        {
            string[] aLines = ioPublic.Replace("\r\n", "\n").Split('\n');
            int aAt = Array.FindIndex(aLines, l => l.Trim() == SCP_PortraitView.PrivateMarker);
            if (aAt < 0) return 0;
            var aBelow = new List<string>(aLines.Skip(aAt + 1));
            int aHead = aBelow.FindIndex(l => l.Trim().Length > 0);
            if (aHead >= 0 && aBelow[aHead].TrimStart().StartsWith("#") && aBelow[aHead].Contains("只給我自己看"))
                aBelow.RemoveAt(aHead);
            string aMovedText = string.Join("\n", aBelow).Trim();
            ioPublic = string.Join("\n", aLines.Take(aAt)).Trim();
            if (aMovedText.Length > 0)
                ioPrivate = ioPrivate.Length > 0 ? aMovedText + "\n\n" + ioPrivate : aMovedText;
            return aBelow.Count(l => l.Trim().Length > 0);
        }

        // 沒有標記的舊寫法：「🔒 …」開頭的行（可在引用／標題／粗體裡）或「只給我自己看」字樣 ⇒ 回傳那一行，否則 null。
        static string? FindPrivateLookingLine(string iPublic)
        {
            foreach (string l in iPublic.Replace("\r\n", "\n").Split('\n'))
            {
                string aBare = l.TrimStart(' ', '\t', '>', '#', '*', '-');
                if (aBare.StartsWith("🔒") || l.Contains("只給我自己看")) return l.Trim();
            }
            return null;
        }

        static string Crlf(string s) => s.Replace("\r\n", "\n").Replace("\n", "\r\n");
    }
}
