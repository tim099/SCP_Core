// 區塊職責：`cmd bank-audit` —— 金流綁定的**唯讀健檢**。**原生，不需要 Unity Editor。**
//
// 物理意義：Tim 2026-09-07 拍板：**`letters/<persona>/bank/<region>.md` 才是權威版本**（用哪個帳戶）。
//           ⇒ 本 Cmd 從「對兩張表」改成「**一張表的健檢**」——
//             因為第二張表（`_registry_meta.json` 的 `bank_personas`）已經退出解析。
//
//   🩸 為什麼反向表退場（讀數，不是偏好）：
//     · 它**沒有寫入端**，現值是 2026-08-19 人工導出手寫的。
//     · 2026-08-20 `Sirius` 的帳戶改名 `Federal Reserve System` → `FRS`：綁定檔跟著改了、
//       反向表沒有 ⇒ **它錯了 18 天而沒有任何一層喊**（ledger：08-20 之後 193 筆全進 `FRS`、
//       舊帳號零筆）。沒釀成事故只是因為解析在更前面就命中了正向綁定 ——
//       那是**沒有人踩到**，不是那條路是對的。
//     · 它也不帶任何獨有資訊：14 家 bank 的鍵**全部**涵蓋在 `system_accounts` 或 `agent_banks` 值裡，
//       而帳號宇宙（`all_account_ids`）本來就不讀它。
//
// 數值影響：**純唯讀**。五種狀況分開報（處置不同，合成一個數字等於沒報）：
//   ① no_binding    本區與別區都沒有綁定      ⇒ 這個人的錢無處可去
//   ② borrowed      只有別區宣告（跨區借用）  ⇒ 標示，**不是錯**
//   ③ unknown_acct  綁定指向不存在的帳戶      ⇒ 錢會進一個沒有登記的地方
//   ④ closed_acct   綁定指向已銷戶帳戶        ⇒ 🔴 最貴的一格
//   ⑤ stale_reverse `bank_personas` 欄位還在  ⇒ 待清理（附它與正向差在哪，好判斷刪了會不會丟資訊）
//
// ⚠ **區域定語不可省**：`bank/` 是 per-region 的。
//   🩸 2026-09-07 第一版沒帶定語就算出「覆蓋率 95%、破洞是 kaguya」——
//   而 kaguya 只有別區（BTC）宣告。那是**形狀正確的錯答案**，會讓人去修一個沒壞的東西。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。JSON 一律走 SCP_Json。
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Json;
using SCP.Core.Letters;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_BankAudit : SCP_Cmd
    {
        public override string Name => "bank-audit";

        public override string Summary =>
            "金流綁定健檢：`bank/<region>.md`（唯一權威）逐位檢查帳戶存不存在／有沒有銷戶 —— **唯讀，不需要 Editor**";

        public override string Details =>
            "唯一權威＝`letters/<persona>/bank/<region>.md`（Tim 2026-09-07 拍板）。\n"
            + "⚠ **必須給 region** —— `bank/` 是 per-region 的，沒有區域定語算出來的覆蓋率是形狀正確的錯答案。\n"
            + "· 五格分開報：`no_binding` / `borrowed` / `unknown_acct` / `closed_acct` / `stale_reverse`。\n"
            + "· `borrowed`（只有別區宣告）**不算錯**，它只是要看得見。\n"
            + "· exit 0＝沒有任何一格是問題（`borrowed` 不計）；exit 5＝有。\n"
            + "⛔ 本 Cmd 不寫任何檔；綁定是錢的歸屬，改它要走有審計的寫入端。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("bank-audit --arg letters_root=<letters> --arg data_root=<AgentCommands>"
                                   + " --arg region=Florin");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("region", "本區的區域（貨幣）ID。⛔ 必填，本 Cmd 不推導", iRequired: true),
            new SCP_CmdArgSpec("data_root", "資料根 —— 帳戶檔（`Treasury/accounts/`）與 registry 都從這裡找", iRequired: true),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aRegion = iArgs.Get("region").Trim();
            string aDataRoot = iArgs.Get("data_root").Trim();

            if (aRegion.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ region 是必填 —— 沒有區域定語的覆蓋率是**形狀正確的錯答案**");
            if (!Directory.Exists(aLettersRoot))
                return SCP_CmdResult.Fail(1, "✗ 信件夾根不存在：" + aLettersRoot);
            if (!Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aDataRoot);

            // ── 帳號宇宙：帳戶檔 ∪ system_accounts ∪ agent_banks 值 ──────────────
            //    ⚠ 三者都要 —— 只看帳戶檔的話，還沒被寫過任何一筆的新帳戶會被判成不存在。
            string aRegistry = Path.Combine(aDataRoot, "AwakenInit", "_registry_meta.json");
            var aKnown = new HashSet<string>(StringComparer.Ordinal);
            var aClosed = new Dictionary<string, string>(StringComparer.Ordinal);
            SCP_JsonData? aMeta = null;
            if (File.Exists(aRegistry))
            {
                try { aMeta = SCP_JsonParser.Parse(File.ReadAllText(aRegistry)); }
                catch (Exception e)
                { return SCP_CmdResult.Fail(1, "✗ registry 不是合法 JSON：" + e.Message, "  " + aRegistry); }
                if (aMeta.Contains("system_accounts"))
                    foreach (string aK in aMeta["system_accounts"].Keys) aKnown.Add(aK);
                if (aMeta.Contains("agent_banks"))
                    foreach (string aK in aMeta["agent_banks"].Keys)
                        aKnown.Add(aMeta["agent_banks"].GetString(aK, ""));
                if (aMeta.Contains("closed_accounts"))
                    foreach (string aK in aMeta["closed_accounts"].Keys)
                        aClosed[aK] = aMeta["closed_accounts"].GetString(aK, "");
            }
            string aAccDir = Path.Combine(aDataRoot, "Treasury", "accounts");
            var aAccountFiles = new HashSet<string>(StringComparer.Ordinal);
            if (Directory.Exists(aAccDir))
                foreach (string f in Directory.GetFiles(aAccDir, "*.json"))
                {
                    string aId = Path.GetFileNameWithoutExtension(f);
                    if (aId.StartsWith("_")) continue;      // `_balances.snapshot` 那類不是帳戶
                    aAccountFiles.Add(aId); aKnown.Add(aId);
                }

            // ── 逐位 persona 讀權威綁定 ────────────────────────────────────────
            var aWarn = new List<string>();
            List<string> aPool = SCP_PersonaProfile.PoolNames(aLettersRoot, m => aWarn.Add(m));
            var aBinding = new Dictionary<string, string>(StringComparer.Ordinal);
            var aNoBinding = new List<string>();
            var aBorrowed = new List<string>();
            var aBorrowedSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (string aName in aPool)
            {
                string aAcc = SCP_PersonaProfile.GetBankAccount(aLettersRoot, aName, aRegion,
                                                                out string aSrc, out string _);
                if (aAcc.Length == 0) { aNoBinding.Add(aName); continue; }
                if (!string.Equals(aSrc, aRegion, StringComparison.Ordinal))
                { aBorrowed.Add(aName + "（宣告在 " + aSrc + "＝" + aAcc + "）"); aBorrowedSet.Add(aName); }
                aBinding[aName] = aAcc;
            }

            var aUnknown = new List<string>();
            var aClosedHit = new List<string>();
            foreach (KeyValuePair<string, string> aKv in aBinding)
            {
                if (aClosed.ContainsKey(aKv.Value))
                { aClosedHit.Add(aKv.Key + " → `" + aKv.Value + "`（" + aClosed[aKv.Value] + "）"); continue; }
                // ⚠ 借用別區的人**不查帳戶存不存在** —— 他的帳戶本來就在別區的帳號宇宙裡，
                //   在這裡查一定查不到。第一版沒排除它，於是 kaguya 天天被報成
                //   「指向不存在的帳戶」。⇒ **一個天天紅的格子，等於沒有那個格子**
                //   （人會學會忽略它，然後真的那一天也一起忽略）。它已經由 ② 說明了。
                if (aBorrowedSet.Contains(aKv.Key)) continue;
                if (!aKnown.Contains(aKv.Value))
                    aUnknown.Add(aKv.Key + " → `" + aKv.Value + "`（帳戶檔、system_accounts、agent_banks 值都沒有）");
            }

            // ── ⑤ 反向表還在不在（它已退出解析，留著只是待清理）────────────────
            var aStale = new List<string>();
            if (aMeta != null && aMeta.Contains("bank_personas"))
            {
                SCP_JsonData aBp = aMeta["bank_personas"];
                int aEntries = 0;
                foreach (string aBank in aBp.Keys)
                {
                    foreach (SCP_JsonData aN in aBp[aBank])
                    {
                        aEntries++;
                        string aP = aN.AsString();
                        // 只報**與權威不同**的那幾格 —— 一致的那些刪掉不會丟任何資訊。
                        if (aBinding.TryGetValue(aP, out string? aAuth)
                            && !string.Equals(aAuth, aBank, StringComparison.Ordinal))
                            aStale.Add(aP + "：權威 `" + aAuth + "` ／ 反向表 `" + aBank + "`");
                    }
                }
                aStale.Insert(0, "`bank_personas` 仍在 registry（" + aBp.Keys.Count + " 家／" + aEntries
                              + " 筆登記）—— **已退出解析**，留著只是待清理");
            }

            var aR = new SCP_CmdResult();
            aR.Lines.Add("# 金流綁定健檢　region=`" + aRegion + "`");
            aR.Lines.Add("- 唯一權威：`letters/<persona>/bank/" + aRegion + ".md`（Tim 2026-09-07 拍板）");
            aR.Lines.Add("- 帳號宇宙：帳戶檔 " + aAccountFiles.Count + " 份 ∪ registry 宣告 ⇒ 共 " + aKnown.Count + " 個");
            aR.Lines.Add("- pool **" + aPool.Count + "** 位：有綁定 **" + aBinding.Count
                         + "**（其中借用別區 " + aBorrowed.Count + "）／沒有綁定 " + aNoBinding.Count);
            aR.Lines.Add("");
            Section(aR, "① no_binding　連別區都沒有宣告　⇒ 這個人的錢無處可去", aNoBinding);
            Section(aR, "② borrowed　只有別區宣告　⇒ **不是錯**，只是要看得見", aBorrowed);
            Section(aR, "③ unknown_acct　綁定指向不存在的帳戶", aUnknown);
            Section(aR, "④ closed_acct　綁定指向**已銷戶**帳戶　⇒ 🔴 最貴的一格", aClosedHit);
            Section(aR, "⑤ stale_reverse　`bank_personas` 殘留（已不參與解析）", aStale);

            foreach (string aW in aWarn) aR.Lines.Add("⚠ " + aW);

            aR.AddValue("pool_count", aPool.Count.ToString());
            aR.AddValue("bound", aBinding.Count.ToString());
            aR.AddValue("no_binding", aNoBinding.Count.ToString());
            aR.AddValue("borrowed", aBorrowed.Count.ToString());
            aR.AddValue("unknown_acct", aUnknown.Count.ToString());
            aR.AddValue("closed_acct", aClosedHit.Count.ToString());
            aR.AddValue("stale_reverse", aStale.Count.ToString());

            // ⚠ `borrowed` **不計入**問題數 —— 它是狀態不是缺陷。
            //   把它算進去的話，每天都會紅一格，而天天紅的東西沒有人會再看。
            int aBad = aNoBinding.Count + aUnknown.Count + aClosedHit.Count + aStale.Count;
            aR.Lines.Add("");
            if (aBad > 0)
            {
                aR.ExitCode = 5;
                aR.Lines.Add("⇒ 共 **" + aBad + "** 項要處理（exit 5；`borrowed` 不計）。⛔ 本 Cmd 只報不改。");
            }
            else aR.Lines.Add("✅ 四格皆 0 —— 每位的綁定都指向一個存在且未銷戶的帳戶，反向表也清乾淨了。");
            return aR;
        }

        /// <summary>一段一節；**空的時候也印節標題** —— 「這一格是 0」與「我沒有量這一格」不可同形。</summary>
        static void Section(SCP_CmdResult ioR, string iTitle, List<string> iItems)
        {
            ioR.Lines.Add("## " + iTitle + "：**" + iItems.Count + "**");
            foreach (string aItem in iItems) ioR.Lines.Add("  - " + aItem);
        }
    }
}
