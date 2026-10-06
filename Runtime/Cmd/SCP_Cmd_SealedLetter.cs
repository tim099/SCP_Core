// 區塊職責：`cmd sealed-letter` —— 密封信的入口（write／seal_cipher／verify_cipher／list／show／restore／resync／sync／verify／install_hook）。
// 物理意義：邏輯在 `SCP_SealedLetters`；本檔只做「參數 → 呼叫 → 印結果」。信件根由宿主給，persona 一律顯式。
// 數值影響：寫入類 op 只動該 persona 信件 repo 的 `private` 分支與工作區 `sealed/`；push 要顯式 push=1。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System.Collections.Generic;
using SCP.Core.Letters;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_SealedLetter : SCP_Cmd
    {
        public override string Name => "sealed-letter";

        public override string Summary => "密封信：寫進信件 repo 的 private 分支（不切分支、不經過公開的 master）＋晚安密文答案的封緘與早安對帳 —— 不需要 Editor";

        public override string Details =>
            "信件 repo 的 master 是公開的；密封信只進 `private` 分支（只推私有 remote）。工作區的 `sealed/` 靠 master 的 .gitignore 擋住 ——\n"
            + "沒有那一行，寫入類 op 直接拒跑。寫完會驗 master 的 tree 裡沒有這封信。\n"
            + "⚠ 預設不 push（對外動作要顯式 push=1）；不跑 hooks、不公告領薪。\n"
            + "⚠ seal_cipher 封緘後，信裡的密文不准再改一字（verify_cipher 會拿 sha256 對帳）。\n"
            + "⚠ verify_cipher 要先交解讀（guess）才印答案 —— 這個順序就是整個機制。工具不判命中。";

        public override string Example =>
            SCP_CmdRegistry.InvokeOf<SCP_Cmd_SealedLetter>("--arg op=verify --arg persona=<你>");

        static readonly string[] s_Ops = { "write", "seal_cipher", "verify_cipher", "list", "show", "restore", "resync", "sync", "verify", "install_hook" };

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("persona", "要操作誰的信件 repo（⚠ 一律顯式：猜錯會寫到別人的 repo）", iRequired: true),
            new SCP_CmdArgSpec("op", "做什麼", iDefault: "verify", iChoices: s_Ops),
            new SCP_CmdArgSpec("title", "write：標題"),
            new SCP_CmdArgSpec("body", "write：內容（長文走 --arg-file）"),
            new SCP_CmdArgSpec("message", "write／seal_cipher：commit 訊息（省略＝用標題）"),
            new SCP_CmdArgSpec("push", "write／seal_cipher：1＝順便推到私有 remote", iDefault: "0", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("cipher", "seal_cipher：密文原文（與信裡逐字一致；走 --arg-file）"),
            new SCP_CmdArgSpec("plain", "seal_cipher：逐句明文答案（親筆；走 --arg-file）"),
            new SCP_CmdArgSpec("wake", "seal_cipher：第幾次 wake／verify_cipher：對哪一封（省略＝最近一封）"),
            new SCP_CmdArgSpec("guess", "verify_cipher：解封前寫下的解讀（走 --arg-file）"),
            new SCP_CmdArgSpec("path", "show：`sealed/<檔名>`"),
            new SCP_CmdArgSpec("overwrite", "restore／sync：1＝工作區已存在也蓋過去", iDefault: "0", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("dry_run", "resync：1＝只印落後幾筆，不動 ref", iDefault: "0", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("force", "install_hook：1＝hook 已存在也覆寫", iDefault: "0", iChoices: new[] { "0", "1" }),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string persona = iArgs.Get("persona").Trim();
            SCP_SealedLetters? s = SCP_SealedLetters.Open(iArgs.Get("letters_root"), persona, out string err);
            if (s == null) return SCP_CmdResult.Fail(1, err);
            string op = iArgs.Get("op").Trim();
            string Self(string a) => SCP_CmdRegistry.InvokeOf<SCP_Cmd_SealedLetter>($"--arg persona={persona} " + a);
            SCP_SealedResult r;
            switch (op)
            {
                case "write": r = s.Write(iArgs.Get("title").Trim(), iArgs.Get("body"), iArgs.Get("message").Trim(), iArgs.Get("push") == "1"); break;
                case "seal_cipher":
                    r = s.SealCipher(iArgs.Get("cipher"), iArgs.Get("plain"), iArgs.Get("wake").Trim(), iArgs.Get("message").Trim(),
                                     iArgs.Get("push") == "1", Self("--arg op=verify_cipher --arg-file guess=<我的解讀>")); break;
                case "verify_cipher": r = s.VerifyCipher(iArgs.Get("guess"), iArgs.Get("wake").Trim()); break;
                case "list": r = s.List(); break;
                case "show":
                    if (iArgs.Get("path").Trim().Length == 0) return SCP_CmdResult.Fail(2, "✗ op=show 要 `--arg path=sealed/<檔名>`（清單：" + Self("--arg op=list") + "）");
                    r = s.Show(iArgs.Get("path").Trim()); break;
                case "restore": r = s.Restore(iArgs.Get("overwrite") == "1"); break;
                case "resync": r = s.Resync(iArgs.Get("dry_run") == "1"); break;
                case "sync": r = s.Sync(iArgs.Get("overwrite") == "1"); break;
                case "verify": r = s.Verify(Self("--arg op=install_hook")); break;
                case "install_hook": r = s.InstallHook(iArgs.Get("force") == "1"); break;
                default: return SCP_CmdResult.Fail(2, "✗ 不認得的 op：" + op);
            }
            var aResult = new SCP_CmdResult { ExitCode = r.Exit };
            aResult.Lines.AddRange(r.Lines);
            return aResult;
        }
    }
}
