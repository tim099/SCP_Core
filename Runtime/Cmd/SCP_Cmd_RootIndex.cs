// 區塊職責：`cmd root-index` —— 重建見根索引（掃 fragments/ frontmatter）。**原生**，不需要 Unity。
// 物理意義：索引是視圖不是真相源 ⇒ 這支永遠可以重跑，重跑的結果只取決於碎片檔本身。
//           組裝邏輯在 SCP_Fragments（Runtime/Letters），本檔只做「參數 → 呼叫 → 回報」。
// 數值影響：整份覆寫 `fragments/_root_index.md`；無碎片時**不建檔**（回報「沒有碎片」而不是生一個空索引）。
using System.Collections.Generic;
using System.IO;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_RootIndex : SCP_Cmd
    {
        public override string Name => "root-index";

        public override string Summary => "見根：掃 fragments/ 機械重建 _root_index.md";

        public override string Details =>
            "索引是**視圖**，事實來源是每個 fragment 檔自己的 frontmatter ⇒ 隨時可重建、可 diff 驗證。\n"
            + "排序＝踩過次數降冪 → 型別群組 → id。status=closed 不列但不刪檔。\n"
            + "⚠ 顯示上限 " + SCP_Fragments.RootIndexShowLimit + " 筆，其餘**明說隱藏筆數**（不靜默截斷）。\n"
            + "⚠ 與 python `awakening.py root-index` 逐字同形；兩支目前並存。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("root-index --arg letters_root=D:/Unity/LY/AgentCommands/ChatTavern/baton/letters"
                                   + " --arg persona=Template");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("persona", "誰的見根", iRequired: true),
            // dry_run 存在的理由：這支是**整份覆寫**。想先看看會寫成什麼樣，不該付出「先寫壞再回滾」的代價。
            new SCP_CmdArgSpec("dry_run", "1 ＝ 只算不寫（回報會列出筆數與各段落統計）",
                               iDefault: "0", iChoices: new[] { "0", "1" }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aPersona = iArgs.Get("persona");
            bool aDryRun = iArgs.Get("dry_run") == "1";

            var aRoot = new SCP_LettersRoot(aLettersRoot);
            string aPersonaDir = SCP_LettersPaths.PersonaDir(aRoot, aPersona);
            if (!Directory.Exists(aPersonaDir))
                return SCP_CmdResult.Fail(1,
                    "✗ 找不到 persona 的信件夾：" + aPersonaDir,
                    "  （信件夾根：" + aLettersRoot + "）");

            List<SCP_Fragment> aFrags = SCP_Fragments.Load(aLettersRoot, aPersona);
            var aResult = new SCP_CmdResult();
            string aFragDir = SCP_LettersPaths.FragmentsDir(aRoot, aPersona);

            if (aFrags.Count == 0)
            {
                // 「沒有碎片」與「索引寫失敗」是兩件事 —— 前者不是錯誤，但也不該生一個空索引。
                aResult.Lines.Add("· 沒有任何 fragment（掃描：" + aFragDir + "）");
                aResult.Lines.Add("  ⇒ 不建索引：一份「0 筆」的索引跟「還沒開始留碎片」長得一樣。");
                aResult.AddValue("fragment_total", "0");
                aResult.AddValue("written", "0");
                return aResult;
            }

            int aOpen = 0, aInternalized = 0, aClosed = 0, aShared = 0;
            foreach (SCP_Fragment aFrag in aFrags)
            {
                string aStatus = aFrag.Get("status");
                if (aStatus == "open") aOpen++;
                else if (aStatus == "internalized") aInternalized++;
                else if (aStatus == "closed") aClosed++;
                if (aFrag.Get("visibility") == "shared") aShared++;
            }

            aResult.Lines.Add("· 碎片目錄：" + aFragDir);
            aResult.Lines.Add("· 掃到 " + aFrags.Count + " 筆　（open " + aOpen
                              + " ／ internalized " + aInternalized + " ／ closed " + aClosed
                              + " ／ 其他 " + (aFrags.Count - aOpen - aInternalized - aClosed) + "）");
            aResult.Lines.Add("· shared " + aShared + " ／ private " + (aFrags.Count - aShared));
            int aHidden = aOpen - SCP_Fragments.RootIndexShowLimit;
            if (aHidden > 0)
                aResult.Lines.Add("· 必讀表只列前 " + SCP_Fragments.RootIndexShowLimit
                                  + " 筆，**另有 " + aHidden + " 筆 open 不在表上**（索引裡會寫明）");

            aResult.AddValue("fragment_total", aFrags.Count.ToString());
            aResult.AddValue("open_count", aOpen.ToString());
            aResult.AddValue("internalized_count", aInternalized.ToString());

            if (aDryRun)
            {
                aResult.Lines.Add("（dry_run=1 ⇒ 沒有寫檔）");
                aResult.AddValue("written", "0");
                return aResult;
            }

            string? aPath = SCP_Fragments.WriteRootIndex(aLettersRoot, aPersona);
            if (aPath == null)
                return SCP_CmdResult.Fail(1, "✗ 掃到碎片卻沒寫成索引 —— 這是程式錯誤，不是資料問題");

            // 回讀：「我寫了」不是「它在裡面」。
            int aLines = 0;
            try { aLines = File.ReadAllLines(aPath).Length; } catch { /* 回讀失敗下面會顯示 0 */ }
            aResult.Lines.Add("✅ 見根索引重建 → " + aPath + "（回讀 " + aLines + " 行）");
            aResult.AddOutput(aPath);
            aResult.AddValue("written", "1");
            aResult.AddValue("index_lines", aLines.ToString());
            return aResult;
        }
    }
}
