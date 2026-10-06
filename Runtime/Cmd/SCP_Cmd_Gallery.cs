// 區塊職責：`cmd gallery` —— 畫展隨機挑展品（自由時間「逛畫展」活動用）。
// 物理意義：畫展根目錄＝`<資料根>/ArtGallery`；走訪全部檔案，略過程式與設定檔、根目錄 README、
//           `Persona_*`／`.original` 備份／`Zeta.md`（非公開展出）、點開頭的路徑，無重複抽 N 件，印相對路徑。
// 數值影響：**純讀**。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Gallery : SCP_Cmd
    {
        public override string Name => "gallery";

        public override string Summary => "畫展：從 ArtGallery 隨機挑展品（可限定主題資料夾）—— 純讀，不需要 Editor";

        public override string Details =>
            "畫展根目錄＝`<資料根>/ArtGallery`。印的是相對路徑 —— 開那個檔去看。\n"
            + "略過：程式與設定檔（.py／.json／.pyc）、根目錄 README.md、`Persona_*`、含 `.original` 的備份、`Zeta.md`、"
            + "以點開頭的檔與資料夾（`.git`／`.github`／`.gitignore`）。";

        public override string Example => SCP_CmdRegistry.Invoke("gallery --arg n=5");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("n", "挑幾件", iDefault: "5"),
            new SCP_CmdArgSpec("theme", "只看這個主題資料夾（ArtGallery 底下的子資料夾名）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aRoot = Path.Combine(iArgs.Get("data_root"), "ArtGallery");
            if (!Directory.Exists(aRoot))
                return SCP_CmdResult.Fail(1, "✗ 找不到畫展：" + aRoot);
            if (!int.TryParse(iArgs.Get("n"), out int aCount) || aCount < 0)
                return SCP_CmdResult.Fail(2, "✗ n 要是 0 以上的整數（got '" + iArgs.Get("n") + "'）");

            string aTheme = iArgs.Get("theme");
            string aScope = aRoot;
            if (aTheme.Length > 0)
            {
                aScope = Path.Combine(aRoot, aTheme);
                if (!Directory.Exists(aScope))
                    return SCP_CmdResult.Fail(1, "✗ 找不到主題資料夾：" + aTheme + "（" + aScope + "）");
            }

            List<string> aWorks = Collect(aRoot, aScope);
            var aResult = new SCP_CmdResult();
            aResult.AddValue("total", aWorks.Count.ToString());
            if (aWorks.Count == 0)
            {
                aResult.Lines.Add("（畫展裡目前什麼都沒有）");
                return aResult;
            }

            int k = Math.Min(aCount, aWorks.Count);
            var aRng = new Random();
            for (int i = 0; i < k; ++i)          // 部分 Fisher–Yates：前 k 格就是無重複的抽樣
            {
                int j = aRng.Next(i, aWorks.Count);
                (aWorks[i], aWorks[j]) = (aWorks[j], aWorks[i]);
            }

            aResult.Lines.Add("✨ 隨機挑選的 " + k + " 件展品" + (aTheme.Length > 0 ? "（" + aTheme + "）" : "")
                              + "　／共 " + aWorks.Count + " 件");
            for (int i = 0; i < k; ++i) aResult.Lines.Add("[" + (i + 1) + "] " + aWorks[i]);
            aResult.Lines.Add("⇒ 開對應的檔去看（路徑相對於 " + aRoot.Replace('\\', '/') + "）");
            aResult.AddValue("picked", k.ToString());
            return aResult;
        }

        static List<string> Collect(string iRoot, string iScope)
        {
            var aList = new List<string>();
            foreach (string f in Directory.EnumerateFiles(iScope, "*", SearchOption.AllDirectories))
            {
                string aName = Path.GetFileName(f);
                string aExt = Path.GetExtension(aName).ToLowerInvariant();
                if (aExt == ".py" || aExt == ".json" || aExt == ".pyc") continue;
                if (aName.StartsWith("Persona_", StringComparison.Ordinal) || aName.Contains(".original") || aName == "Zeta.md") continue;
                // ⚠ 比相對路徑，不比目錄字串：資料根帶正斜線時，Combine 出來的根與 GetDirectoryName 的分隔符不同、永遠不相等。
                string aRel = f.Substring(iRoot.Length).TrimStart('\\', '/').Replace('\\', '/');
                if (aRel == "README.md") continue;
                if (aRel.StartsWith(".", StringComparison.Ordinal) || aRel.Contains("/.")) continue;   // .git／.github／.gitignore 不是展品（畫展本身是 git repo）
                aList.Add(aRel);
            }
            return aList;
        }
    }
}
