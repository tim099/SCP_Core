// 區塊職責：`cmd keys` —— 見叢（當期交棒清單）的 list / append / 勾銷。**原生**，不需要 Unity。
// 物理意義：見叢是「撞到未解線就當場丟進來」的東西（summit 2026-07-27 拍板：斷線風險最高的
//           正是沒走到任何儀式就掛掉的場景）⇒ 入口越短越好，不該綁在 Editor 開著這個前提上。
//           資料就是 `letters/<persona>/_keys_open.md`，純文字，沒有第二個真相源。
//           ⚠ **見叢只收「個人代辦」**（Tim 2026-09-07 拍板）：跟專案有關的一律開 Task，
//           而那一側由 wake_brief 每天機械撈取（`SCP_WakeBrief` 的 §2.5），**不靠人手抄進來**。
// 數值影響：append 一行到那個檔（檔不存在時先寫 frontmatter 骨架）；
//           或把指定的 `- [ ]` 改成 `- [x]`（**只動那五個字元，其餘位元組不變**）。
//
// ⚠ **與 python `awakening.py keys --add` 逐字同形**（awakening.py → memory.keys_append）：
//   行格式 `- [ ] <內容>  <!-- <UTC ISO> -->`，兩個空格、註解裡是時間戳。
//   兩個寫入端要並存一段時間（同事手上不一定有 senate.exe），而 append-only 純文字的並存
//   **只在格式同形時才安全** —— 形狀一旦分岔，見林歸檔那天才會發現，那時已經混了好幾十行。
//   ⇒ 改這裡的格式＝同時要改 python 那支，否則就是製造兩種形狀。
using System;
using System.Collections.Generic;
using System.Globalization;
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
            "見叢是給明天的自己**執行**用的個人代辦清單；抒發與敘事寫進 letter，不寫這裡。\n"
            + "⛔ 跟專案有關的不放這裡 —— 開 Task（早安 brief 會自己撈「我涉及且在動」的單）。\n"
            + "勾銷走 --arg done=<片段> 或 --arg done_index=<未完序號，可逗號多筆>；\n"
            + "⚠ 本 Cmd **不刪行也不改內容**，勾銷只把該行的 `- [ ]` 換成 `- [x]`。\n"
            + "⚠ 與 python `awakening.py keys` 寫出的行**逐字同形**；⚠ python 那側**沒有勾銷**（形狀不變，只是入口少一半）。";

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
            // 兩個選擇器分開，因為它們的失效樣子不同：`done` 打錯會**命中別行**（片段太短），
            // `done_index` 打錯只會落在範圍外或指到另一條 —— 而後者印得出來，前者印不出來。
            new SCP_CmdArgSpec("done", "勾銷：內容含這個片段的那一條（**必須唯一命中**，0 或 ≥2 都擋下並列候選）"),
            new SCP_CmdArgSpec("done_index", "勾銷：未完清單的序號（1-based，逗號分隔可多筆）。序號＝本 Cmd 列出來的那個順序"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aPersona = iArgs.Get("persona");
            string aAdd = iArgs.Get("add");
            string aDoneFrag = iArgs.Get("done").Trim();
            string aDoneIndex = iArgs.Get("done_index").Trim();

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

            if (aDoneFrag.Length > 0 || aDoneIndex.Length > 0)
            {
                // 前值先讀起來 —— 收尾要印的是**前後對照**，而不是「我送出了幾筆」。
                (List<string> aTodoBefore, List<string> aDoneBefore) =
                    SCP_WakeLetters.KeysEntries(aLettersRoot, aPersona);
                SCP_CmdResult? aFail = CheckOff(aPath, aDoneFrag, aDoneIndex, aResult);
                if (aFail != null) return aFail;
                aResult.AddValue("todo_count_before", aTodoBefore.Count.ToString());
                aResult.AddValue("done_count_before", aDoneBefore.Count.ToString());
                aResult.AddOutput(aPath);
            }

            // append／勾銷完**回讀**再列 —— 「我寫了」不是「它在裡面」。
            (List<string> aTodo, List<string> aDone) = SCP_WakeLetters.KeysEntries(aLettersRoot, aPersona);
            aResult.Lines.Add("");
            aResult.Lines.Add($"# 🌿 見叢 — {aPersona}（{aTodo.Count} 未完 / {aDone.Count} 已完）");
            // 序號印在行首 —— 沒有它，`done_index` 就要人自己數到第 87 行，而數錯不會報錯。
            for (int i = 0; i < aTodo.Count; i++) aResult.Lines.Add("- [ ] #" + (i + 1) + " " + aTodo[i]);
            // 已完只印最後 3 條（跟 python 同）—— 已完的價值在「最近勾掉什麼」，不在全部歷史。
            for (int i = Math.Max(0, aDone.Count - 3); i < aDone.Count; i++)
                aResult.Lines.Add("- [x] " + aDone[i]);
            if (aTodo.Count == 0 && aDone.Count == 0) aResult.Lines.Add("(當期無事項)");

            aResult.AddValue("todo_count", aTodo.Count.ToString());
            aResult.AddValue("done_count", aDone.Count.ToString());
            return aResult;
        }

        /// <summary>勾銷候選最多列幾條 —— 全列會把 100+ 行的見叢整份倒進錯誤訊息裡。</summary>
        const int c_CandidateCap = 8;

        /// <summary>
        /// 把選中的 `- [ ]` 改成 `- [x]`。成功回 <c>null</c>（訊息寫進 <paramref name="ioResult"/>），
        /// 失敗回一個已組好的 Fail。
        /// <para>物理意義：見叢唯一會讓清單**變短**的動作。在這之前它只有 append，
        /// 於是「做完的行」與「沒做的行」同形，而唯一的勾銷方法是手改檔案
        /// —— 那正好違反「有單一入口的資料區不要手寫」（TASK-0149）。</para>
        /// <para>⚠ **只換那五個字元**：前導空白、內容、尾端 `&lt;!-- 時間戳 --&gt;` 與行尾符號全部原封不動。
        /// 這不是潔癖 —— 見叢是兩個寫入端（C# / python）共寫的純文字，
        /// 任何順手的重排都會讓 git diff 整段翻動，而那會蓋掉真正的那一行改動。</para>
        /// <para>⚠ 定位用**檔案行號**不是內容比對：見叢允許兩行內容一模一樣（同一件事撞兩次），
        /// 而用內容找會靜默地勾掉第一條。</para>
        /// </summary>
        // ⚠ 回傳型別帶 `?`：Senate 那側（`dotnet build`）nullable 是**開的且警告當錯誤**
        //   ⇒ 不標註就是 CS8603 build 失敗；Unity 那側 nullable 沒開，`?` 只是 CS8632 警告。
        //   🩸 2026-09-07 我為了讓 Unity 少一個警告把 `?` 拿掉，Unity 綠燈、Senate build 當場紅 ——
        //   **同一份字面在兩個宿主底下編譯設定不同，Unity 的綠燈不涵蓋 senate.exe。**
        static SCP_CmdResult? CheckOff(string iPath, string iFrag, string iIndexList, SCP_CmdResult ioResult)
        {
            if (iFrag.Length > 0 && iIndexList.Length > 0)
                return SCP_CmdResult.Fail(2,
                    "✗ done 與 done_index 同時給了 —— 我不知道你要哪一條，兩個都不執行",
                    "  （一次只用一個選擇器；多筆走 done_index=1,4,7）");
            if (!File.Exists(iPath))
                return SCP_CmdResult.Fail(1, "✗ 見叢還不存在，沒有東西可以勾：" + iPath);

            string aText;
            try { aText = File.ReadAllText(iPath); }
            catch (Exception e)
            { return SCP_CmdResult.Fail(1, "✗ 讀不到見叢：" + e.GetType().Name + ": " + e.Message, "  " + iPath); }

            // Split('\n') 之後每一段可能仍帶著結尾的 '\r' —— 保持原樣，Join 回去就是原位元組。
            string[] aParts = aText.Split('\n');
            var aTodoLineNos = new List<int>();          // 未完行在 aParts 裡的索引，檔案順序
            for (int i = 0; i < aParts.Length; i++)
                if (aParts[i].TrimStart().StartsWith("- [ ]", StringComparison.Ordinal)) aTodoLineNos.Add(i);

            if (aTodoLineNos.Count == 0)
                return SCP_CmdResult.Fail(2, "✗ 這份見叢沒有任何未勾銷的行 —— 沒有東西可以勾", "  " + iPath);

            var aTargets = new List<int>();              // 要動的 aParts 索引
            if (iFrag.Length > 0)
            {
                var aHits = new List<int>();
                foreach (int aNo in aTodoLineNos)
                    if (Content(aParts[aNo]).IndexOf(iFrag, StringComparison.Ordinal) >= 0) aHits.Add(aNo);

                if (aHits.Count == 0)
                    return SCP_CmdResult.Fail(2,
                        "✗ 沒有任何未完行含這個片段：「" + iFrag + "」",
                        "  ⚠ 已勾銷的行不在搜尋範圍內（勾兩次不是冪等，是打錯了）",
                        "  未完共 " + aTodoLineNos.Count + " 條 —— 先跑一次不帶參數的 keys 看序號");
                if (aHits.Count > 1)
                {
                    var aFail = SCP_CmdResult.Fail(2,
                        "✗ 片段「" + iFrag + "」命中 " + aHits.Count + " 條 —— 不猜，一條都不勾");
                    for (int i = 0; i < aHits.Count && i < c_CandidateCap; i++)
                        aFail.Lines.Add("  - #" + (aTodoLineNos.IndexOf(aHits[i]) + 1) + " " + Content(aParts[aHits[i]]));
                    if (aHits.Count > c_CandidateCap)
                        aFail.Lines.Add("  - …另有 " + (aHits.Count - c_CandidateCap) + " 條未列");
                    aFail.Lines.Add("  ⇒ 拿上面的序號走 --arg done_index=<n>");
                    return aFail;
                }
                aTargets.Add(aHits[0]);
            }
            else
            {
                var aSeen = new HashSet<int>();
                foreach (string aRaw in iIndexList.Split(','))
                {
                    string aTok = aRaw.Trim();
                    if (aTok.Length == 0) continue;
                    if (!int.TryParse(aTok, NumberStyles.None, CultureInfo.InvariantCulture, out int aOrdinal))
                        return SCP_CmdResult.Fail(2, "✗ done_index 裡有不是數字的東西：「" + aTok + "」");
                    if (aOrdinal < 1 || aOrdinal > aTodoLineNos.Count)
                        return SCP_CmdResult.Fail(2,
                            "✗ 序號 " + aOrdinal + " 在範圍外 —— 目前未完 1.." + aTodoLineNos.Count,
                            "  ⚠ 序號是**未完清單**的序號，不是檔案行號");
                    // 同一個序號給兩次不是錯，但它會讓「我勾了幾條」對不上 ⇒ 去重並照實報。
                    if (aSeen.Add(aOrdinal)) aTargets.Add(aTodoLineNos[aOrdinal - 1]);
                }
                if (aTargets.Count == 0)
                    return SCP_CmdResult.Fail(2, "✗ done_index 解析後一個序號都不剩（只有分隔符？）");
            }

            // 讀完到寫入之間可能有人 append（見叢的使用情境正是「隨時、可能同時」）。
            // ⇒ 重讀一次，逐行**位元組比對**我要動的那幾行；對不上就整批不做並說出來。
            string aNow;
            try { aNow = File.ReadAllText(iPath); }
            catch (Exception e)
            { return SCP_CmdResult.Fail(1, "✗ 寫入前重讀失敗：" + e.GetType().Name + ": " + e.Message); }
            string[] aNowParts = aNow.Split('\n');
            foreach (int aNo in aTargets)
            {
                if (aNo < aNowParts.Length && string.Equals(aNowParts[aNo], aParts[aNo], StringComparison.Ordinal))
                    continue;
                return SCP_CmdResult.Fail(1,
                    "✗ 見叢在我讀完之後被改過（第 " + (aNo + 1) + " 行對不上）—— 一條都不勾",
                    "  ⇒ 重跑一次 keys 拿新的序號");
            }

            var aChecked = new List<string>();
            foreach (int aNo in aTargets)
            {
                int aMark = aNowParts[aNo].IndexOf("- [ ]", StringComparison.Ordinal);
                aNowParts[aNo] = aNowParts[aNo].Substring(0, aMark) + "- [x]"
                                 + aNowParts[aNo].Substring(aMark + 5);
                aChecked.Add(Content(aNowParts[aNo]));
            }
            try { File.WriteAllText(iPath, string.Join("\n", aNowParts), s_Utf8NoBom); }
            catch (Exception e)
            { return SCP_CmdResult.Fail(1, "✗ 寫不進見叢：" + e.GetType().Name + ": " + e.Message, "  " + iPath); }

            ioResult.Lines.Add("✅ 見叢勾銷 " + aChecked.Count + " 條 → " + iPath);
            foreach (string aItem in aChecked) ioResult.Lines.Add("  - [x] " + aItem);
            ioResult.AddValue("checked_count", aChecked.Count.ToString());
            return null;
        }

        /// <summary>去掉行首的 `- [ ]` / `- [x]` 與前後空白，只留內容（含尾端的時間戳註解）。</summary>
        static string Content(string iRawLine)
        {
            string aLine = iRawLine.Trim();
            return aLine.Length >= 5 ? aLine.Substring(5).Trim() : aLine;
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
                       .Append("> 給明天的自己**執行**用的**個人代辦**（可勾銷）；抒發與敘事寫進 letter，不寫這裡。\n")
                       .Append("> ⛔ 跟專案有關的不放這裡 —— 開 Task（早安 brief 會自己撈「我涉及且在動」的單）。\n\n");
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
