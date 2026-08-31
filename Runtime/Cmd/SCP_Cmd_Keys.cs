// 區塊職責：`cmd keys` —— 見叢（當期交棒清單）的 list / append。**原生**，不需要 Unity。
// 物理意義：見叢是「撞到未解線就當場丟進來」的東西（summit 2026-07-27 拍板：斷線風險最高的
//           正是沒走到任何儀式就掛掉的場景）⇒ 入口越短越好，不該綁在 Editor 開著這個前提上。
//           資料就是 `letters/<persona>/_keys_open.md`，append-only 純文字，沒有第二個真相源。
// 數值影響：只 append 一行到那個檔（檔不存在時先寫 frontmatter 骨架）；不刪不改既有行。
//
// ⚠ **與 python `awakening.py keys --add` 逐字同形**（awakening.py → memory.keys_append）：
//   行格式 `- [ ] <內容>  <!-- <UTC ISO> -->`，兩個空格、註解裡是時間戳。
//   兩個寫入端要並存一段時間（同事手上不一定有 senate.exe），而 append-only 純文字的並存
//   **只在格式同形時才安全** —— 形狀一旦分岔，見林歸檔那天才會發現，那時已經混了好幾十行。
//   ⇒ 改這裡的格式＝同時要改 python 那支，否則就是製造兩種形狀。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Keys : SCP_Cmd
    {
        public override string Name => "keys";

        public override string Summary => "見叢（當期交棒清單）：列出未完／已完，或 append 一條";

        public override string Details =>
            "見叢是給明天的自己**執行**用的清單（可勾銷）；抒發與敘事寫進 letter，不寫這裡。\n"
            + "⚠ 本 Cmd 只 append，**不刪不改既有行** —— 勾銷請直接編輯那個 md 檔。\n"
            + "⚠ 與 python `awakening.py keys` 寫出的行**逐字同形**；兩支目前並存。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("keys --arg letters_root=D:/Unity/LY/AgentCommands/ChatTavern/baton/letters"
                                   + " --arg persona=Template --arg add=\"明天先驗 X 那一格\"");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("persona", "誰的見叢", iRequired: true),
            // 一次一條：多條走多次呼叫。合併成一個參數要挑分隔字元，而**交棒事項本身很常含標點**，
            // 挑到的那個字元遲早會出現在內容裡，然後把一條切成兩條而且不報錯。
            new SCP_CmdArgSpec("add", "要 append 的一條事項（不給＝只列出）。長內文走 --arg-file"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aPersona = iArgs.Get("persona");
            string aAdd = iArgs.Get("add");

            var aRoot = new SCP_LettersRoot(aLettersRoot);
            string aPersonaDir = SCP_LettersPaths.PersonaDir(aRoot, aPersona);
            if (!Directory.Exists(aPersonaDir))
                // 「這個人不存在」與「信件夾根設錯」是兩件事 —— 兩條路徑都印出來讓人自己分辨。
                return SCP_CmdResult.Fail(1,
                    "✗ 找不到 persona 的信件夾：" + aPersonaDir,
                    "  （信件夾根：" + aLettersRoot + "）");

            var aResult = new SCP_CmdResult();
            string aPath = SCP_LettersPaths.KeysOpenPath(aRoot, aPersona);

            if (aAdd.Length > 0)
            {
                string aTrimmed = aAdd.Trim();
                if (aTrimmed.Length == 0)
                    return SCP_CmdResult.Fail(2, "✗ add 只有空白 —— 空的交棒事項比沒有更糟（它會佔一行卻不說話）");
                try { Append(aPath, aPersona, aTrimmed); }
                catch (Exception e)
                {
                    return SCP_CmdResult.Fail(1, "✗ 寫不進見叢：" + e.GetType().Name + ": " + e.Message,
                                              "  " + aPath);
                }
                aResult.Lines.Add("✅ 見叢 append 1 條 → " + aPath);
                aResult.AddOutput(aPath);
            }

            // append 完**回讀**再列 —— 「我寫了」不是「它在裡面」。
            (List<string> aTodo, List<string> aDone) = SCP_WakeLetters.KeysEntries(aLettersRoot, aPersona);
            aResult.Lines.Add("");
            aResult.Lines.Add($"# 🌿 見叢 — {aPersona}（{aTodo.Count} 未完 / {aDone.Count} 已完）");
            foreach (string aItem in aTodo) aResult.Lines.Add("- [ ] " + aItem);
            // 已完只印最後 3 條（跟 python 同）—— 已完的價值在「最近勾掉什麼」，不在全部歷史。
            for (int i = Math.Max(0, aDone.Count - 3); i < aDone.Count; i++)
                aResult.Lines.Add("- [x] " + aDone[i]);
            if (aTodo.Count == 0 && aDone.Count == 0) aResult.Lines.Add("(當期無事項)");

            aResult.AddValue("todo_count", aTodo.Count.ToString());
            aResult.AddValue("done_count", aDone.Count.ToString());
            return aResult;
        }

        /// <summary>append 一條（檔不存在時先寫骨架）。⚠ 骨架與行格式都與 python memory.keys_append 同形。</summary>
        static void Append(string iPath, string iPersona, string iItem)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iPath)!);
            string aNewLine = DetectNewLine(iPath);
            if (!File.Exists(iPath))
            {
                var aHeader = new StringBuilder();
                aHeader.Append("---\ntype: keys_open\npersona: ").Append(iPersona)
                       .Append("\nopened_at: ").Append(UtcNowIso()).Append("\n---\n\n")
                       .Append("# 🌿 見叢 — 當期交棒清單（跨夜 append-only，見林時歸檔）\n\n")
                       .Append("> 給明天的自己**執行**用（可勾銷）；抒發與敘事寫進 letter，不寫這裡。\n\n");
                File.WriteAllText(iPath, aHeader.Replace("\n", aNewLine).ToString(), s_Utf8NoBom);
            }
            // append 而不是讀改寫：讀改寫會在「同時有人寫」時把對方那行吃掉，
            // 而見叢的使用情境正是「隨時、可能同時」。
            File.AppendAllText(iPath, "- [ ] " + iItem + "  <!-- " + UtcNowIso() + " -->" + aNewLine,
                               s_Utf8NoBom);
        }

        /// <summary>
        /// 這個檔用什麼行尾 —— **沿用既有的**，新檔才用平台預設。
        /// <para>🩸 2026-08-31 對拍實測：python 的 <c>open(p,"a")</c> 在 Windows 是文字模式，
        /// 會把 <c>\n</c> 轉成 <c>\r\n</c>；而我第一版寫死 <c>"\n"</c> ⇒ **同一個檔裡兩種行尾**。
        /// 兩邊的 parser 都會 trim，所以功能正常、git diff 卻會整段翻動，
        /// 而「兩個工具生出看起來都正常的兩份」正是最難追的那一族（wake 79 血證）。</para>
        /// <para>⚠ 判準是**檔案現在長什麼樣**，不是「我覺得應該用哪種」。</para>
        /// </summary>
        static string DetectNewLine(string iPath)
        {
            try
            {
                if (!File.Exists(iPath)) return Environment.NewLine;
                string aText = File.ReadAllText(iPath);
                int aLf = 0, aCrLf = 0;
                for (int i = 0; i < aText.Length; i++)
                {
                    if (aText[i] != '\n') continue;
                    if (i > 0 && aText[i - 1] == '\r') aCrLf++; else aLf++;
                }
                if (aCrLf == 0 && aLf == 0) return Environment.NewLine;   // 空檔／單行無換行
                return aCrLf >= aLf ? "\r\n" : "\n";                     // 混用時跟多數走
            }
            catch { return Environment.NewLine; }
        }

        /// <summary>與 python `utcnow_iso()` 同形：微秒 ＋ 尾綴 Z。</summary>
        static string UtcNowIso()
            => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                                        System.Globalization.CultureInfo.InvariantCulture);

        // ⚠ BOM 會讓 python 那端讀到的第一行變成 "\ufeff---"，而 frontmatter 判定就此失效。
        //   兩端共寫同一個檔時，編碼不是細節。
        static readonly UTF8Encoding s_Utf8NoBom = new UTF8Encoding(false);
    }
}
