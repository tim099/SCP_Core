// 區塊職責：`cmd bank-resolve` —— 帳號解析的**唯讀查詢入口**。**原生，不需要 Editor／Server。**
// 物理意義：規則本體在 `SCP_BankAccountResolver`；本檔只是把它開給 CLI 與 python 用。
// 數值影響：**零寫入。** 不動 registry、不動 ledger、不動綁定檔。
//
// ⛔ 為什麼要有這一支（TASK-0269）：python 那側原本自己維護一份 292 行的解析
//   （`_lib/bank_resolver.py`）—— 那是**第三份實作**，而三份讀同一個權威、
//   差異不會在當下報錯，只在其中一份先過期的那天現形。
//   🩸 已經現形過：2026-08-20 `Sirius` 改名，其中一份沒跟上 ⇒ **錯了 18 天沒有人喊**。
//   ⇒ 給 python 一個叫得到的入口，它那份才刪得掉。
#nullable enable
using System.Collections.Generic;
using System.IO;
using SCP.Core.Bank;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_BankResolve : SCP_Cmd
    {
        public override string Name => "bank-resolve";

        public override string Summary =>
            "帳號解析：任何字串（persona／agent／別名／帳號）→ 正式帳號 —— **唯讀，不需要 Editor**";

        public override string Details =>
            "唯一權威＝`letters/<persona>/bank/<region>.md`（Tim 2026-09-07 拍板）。\n"
            + "· `input` 解析一個字串；`bound_of` 改問「哪些 persona 綁在這個帳號底下」。\n"
            + "· 回傳除了帳號，還印 **`kind`（走了哪一段）** 與 `trace` ——\n"
            + "  ⚠ 「權威綁定命中」與「legacy `agent_banks` 猜出來的」在帳號字串上**同形**，\n"
            + "  而它們的可信度差很多；只讀帳號的呼叫端分不出來。\n"
            + "· `exit 4` ＝ 查無對應（⛔ 它與「解析成功」**不同出口** —— 查無不可以當成一個帳號拿去記帳）。\n"
            + "⛔ 本 Cmd 不寫任何檔。銷戶（`closed_accounts`）走有審計的寫入端，不在這裡。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("bank-resolve --arg letters_root=<letters> --arg data_root=<AgentCommands>"
                                   + " --arg region=Florin --arg input=kaguya");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("data_root", "資料根 —— registry 從這裡找", iRequired: true),
            new SCP_CmdArgSpec("region", "本區的區域（貨幣）ID。⛔ 必填，本 Cmd 不推導 —— "
                                       + "`bank/` 是 per-region 的，少了它算出來的是形狀正確的錯答案", iRequired: true),
            new SCP_CmdArgSpec("input", "要解析的字串（persona／agent／別名／帳號）"),
            new SCP_CmdArgSpec("bound_of", "改問：哪些 persona 綁在這個帳號底下（由正向綁定檔導出）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLetters = iArgs.Get("letters_root").Trim();
            string aData = iArgs.Get("data_root").Trim();
            string aRegion = iArgs.Get("region").Trim();
            string aInput = iArgs.Get("input").Trim();
            string aBoundOf = iArgs.Get("bound_of").Trim();

            if (aRegion.Length == 0) return SCP_CmdResult.Fail(2, "✗ region 是必填");
            if (!Directory.Exists(aLetters)) return SCP_CmdResult.Fail(1, "✗ 信件夾根不存在：" + aLetters);
            if (!Directory.Exists(aData)) return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aData);
            if (aInput.Length == 0 && aBoundOf.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ 要給 `input` 或 `bound_of` 其中一個");

            var aR = new SCP_CmdResult();

            if (aBoundOf.Length > 0)
            {
                List<string> aPersonas = SCP_BankAccountResolver.GetBoundPersonas(aLetters, aData, aRegion, aBoundOf);
                aR.Lines.Add("# 綁在 `" + aBoundOf + "` 底下的 persona（region=" + aRegion + "）");
                aR.Lines.Add("· 由**正向綁定檔導出** —— ⛔ 不讀 registry 的 `bank_personas`（那張表已退出解析）");
                if (aPersonas.Count == 0) aR.Lines.Add("  （空）");
                foreach (string aP in aPersonas) aR.Lines.Add("  - " + aP);
                aR.AddValue("bound_count", aPersonas.Count.ToString());
                aR.AddValue("bound_personas", string.Join(",", aPersonas));
                return aR;
            }

            SCP_BankResolution aRes = SCP_BankAccountResolver.Resolve(aLetters, aData, aRegion, aInput);
            aR.Lines.Add("# 解析 `" + aInput + "`（region=" + aRegion + "）");
            aR.Lines.Add("- 帳號：**" + (aRes.IsUnresolved ? "（查無）" : aRes.AccountId) + "**");
            aR.Lines.Add("- 走哪一段：`" + aRes.Kind + "`");
            aR.Lines.Add("- trace：" + aRes.Trace);
            aR.AddValue("input", aRes.Input);
            aR.AddValue("account", aRes.IsUnresolved ? "" : aRes.AccountId);
            aR.AddValue("kind", aRes.Kind.ToString());
            aR.AddValue("resolved", aRes.IsUnresolved ? "0" : "1");
            // ⛔ 查無給**自己的出口** —— 與成功同一個 exit code 的話，
            //   呼叫端拿空字串去記帳的那一天不會有任何一層叫。
            if (aRes.IsUnresolved) aR.ExitCode = 4;
            return aR;
        }
    }
}
