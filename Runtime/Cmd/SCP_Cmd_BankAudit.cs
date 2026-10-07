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
//   ② borrowed      只有別區宣告（跨區借用）  ⇒ 標示，**不是錯** —— 前提是借來的帳號**本區有開戶**
//   ②b borrowed_unopened 借別區、而借來的帳號本區帳本沒開戶 ⇒ 🔴 **計入問題**（TASK-0440）
//      🩸 新銀行「沒開戶的帳號不能收付」，而收付走的是**本區**的帳本 ⇒ 借來的那個帳號在這裡收不到錢。
//         erina 2026-10-06 在 Bar（BTC）第一次醒來：綁定借自 Florin＝`cc`，BTC 的 `Bank/accounts/` 沒有 cc；
//         早安回傳檔寫著「帳本裡查無此帳戶」，而本 Cmd 同一時間把她算成「不是錯」、不計入問題。
//   ③ unmaterialized 本區綁定指向**沒開戶**的帳號（只看帳戶檔）⇒ 🔴 **計入問題**（TASK-0441，Tim 2026-10-07）
//      新銀行沒開戶不能收付 ⇒ 下面 0173 那次降級的前提（「錢照樣入帳」）已不成立；判準也從「三處都沒有」收成「沒有帳戶檔」，
//      跟入帳的 `CheckUsable` 同一把尺。借別區的那一半在 ②b，而到新區的第一次早安會自己開戶補綁（`SCP_Morning.EnsureRegionBinding`）。
//
//   🩸 （歷史）③ 曾從「錯」降級成「狀態」（kaguya 2026-09-08，TASK-0173，同族第三次）：
//     合一模式（Tim 2026-08-20 拍板，開關已拔除）的定義就是 **agent id 即帳號 id**，
//     而權威是 `letters/<persona>/bank/<region>.md`。入帳那條路的 resolver 照這個定義做 ——
//     它把**每一個綁定值**都登記成正式帳號（`foreach s_PersonaToAgentLower → AddCanonical`）。
//     本 Cmd 的帳號宇宙卻停在「帳戶檔 ∪ system_accounts ∪ agent_banks」⇒
//     **同一個 `Luna`，入帳那條路判它合法、健檢這條路判它不存在。**
//     ⇒ 病不是「有人打錯 id」（綁定是查表來的，打不錯），是**同一個問題兩個實作、兩個相反答案**。
//     前兩次同族的血證就刻在 resolver 自己的註解裡：`FRS`「一個真的在用、裡面有 6253 token
//     的帳戶，被系統判定為不存在」／央行「**系統把自己的央行判定為不存在**，而錢照樣入帳」——
//     兩次都只在 resolver 那側補，**這側從來沒跟上**。同一前提兩入口各自守＝沒守。
//
//   ⚠ 而「把綁定值加進宇宙」之後，舊的 ③ 會**結構性地永遠是 0** ——
//     一個不可能變紅的守衛不該繼續佔著錯誤欄位假裝在守。所以它改報**實體化狀態**並退出問題數。
//   ⭐ 2026-09-22（TASK-0275 ⑥）：上面那個「只差大小寫」的缺口**在帳號宇宙這一側消失了** ——
//     帳戶來源換成新銀行的 `Bank/accounts/`（檔名是正規化過的小寫），而綁定檔存的是顯示寫法，
//     ⇒ 兩邊一律用 `OrdinalIgnoreCase` 比。⛔ 它**不是**在猜：新銀行的身分規則本來就是
//     `SCP_BankId.Normalize`（trim ＋ ToLowerInvariant），本 Cmd 只是照同一把尺讀。
//     🩸 而這一格是換來源的當下量到的：不改比較方式的話，**13 位**會一次被報成
//     「只靠合一成立、後台沒開過戶」—— 一句每一欄都合法的假話。
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
using SCP.Core.Bank;
using SCP.Core.Json;
using SCP.Core.Letters;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_BankAudit : SCP_Cmd
    {
        public override string Name => "bank-audit";
        public override string Category => SCP_CmdCategory.Bank;

        public override string Summary =>
            "金流綁定健檢：`bank/<region>.md`（唯一權威）逐位檢查帳戶存不存在／有沒有銷戶 —— **唯讀，不需要 Editor**";

        public override string Details =>
            "唯一權威＝`letters/<persona>/bank/<region>.md`（Tim 2026-09-07 拍板）。\n"
            + "⚠ **必須給 region** —— `bank/` 是 per-region 的，沒有區域定語算出來的覆蓋率是形狀正確的錯答案。\n"
            + "· 五格分開報：`no_binding` / `borrowed` / `unknown_acct` / `closed_acct` / `stale_reverse`。\n"
            + "· `borrowed`（只有別區宣告）**不算錯**，它只是要看得見 ——\n"
            + "  但借來的帳號**本區沒開戶** ⇒ `borrowed_unopened`，計入問題（沒開戶的帳號不能收付）。\n"
            + "· `unmaterialized`：本區自己的綁定指向沒開戶的帳號 ⇒ 計入問題（同一個理由）。\n"
            + "· 借別區的人到本區第一次早安時會自動補：本區有那個戶 ⇒ 直接綁；沒有 ⇒ 開戶（種子 1000）再綁。\n"
            + "· exit 0＝沒有任何一格是問題（`borrowed` 不計）；exit 5＝有。\n"
            + "⛔ 本 Cmd 不寫任何檔；綁定是錢的歸屬，改它要走有審計的寫入端。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("bank-audit"
                                   + " --arg region=Florin");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("region", "本區的區域（貨幣）ID。⛔ 必填，本 Cmd 不推導", iRequired: true),
            new SCP_CmdArgSpec("data_root", "資料根 —— 帳戶檔（`Bank/accounts/`）與 registry 都從這裡找", iRequired: true),
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
            // ⚠ 帳號宇宙這幾個集合用 **OrdinalIgnoreCase**：新銀行的帳戶檔名是**正規化過的小寫**
            //   （`SCP_BankId.Normalize`），而綁定檔與 registry 存的是顯示寫法（`Luna`／`Spectre`）。
            //   🩸 實測（TASK-0275 ⑥ 把帳戶來源從舊 `Treasury/accounts` 換成 `Bank/accounts` 的那一刻）：
            //     大小寫一比就把 **13 位**全報成「只靠合一成立、後台沒開過戶」——
            //     那是一句每一欄都合法的假話，而它的下一步會是有人去「補開戶」一批已經存在的帳戶。
            var aKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var aClosed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            SCP_JsonData? aMeta = null;
            // TASK-0265：registry 會被別的進程換檔 ⇒ 舊版 `File.Exists` 撞上那一瞬間 ⇒ 帳號宇宙與銷戶名單整段變空
            //   ⇒ 報一份每一欄都合法的假覆蓋率。健檢工具拿殘缺的輸入算出來的是「形狀正確的錯答案」⇒ 讀不了就停。
            if (!SCP.Core.Io.SCP_AtomicFileRead.TryReadAllText(aRegistry, out string aRegistryText, out var aRegistryState))
            {
                if (aRegistryState == SCP.Core.Io.SCP_FileReadState.Busy)
                    return SCP_CmdResult.Fail(1, "✗ registry 這一瞬間讀不了 ⇒ 本次不判（重跑即可）：" + SCP.Core.Io.SCP_AtomicFileRead.DescribeBusy(aRegistry));
            }
            else
            {
                try { aMeta = SCP_JsonParser.Parse(aRegistryText); }
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
            // 🩸 TASK-0275 ⑥：這一行原本寫死 `Treasury/accounts` —— **舊路徑**。
            //   新銀行 2026-09-18 起是唯一權威，而這支健檢還在拿一份**凍結的**帳戶清單當帳號宇宙。
            //   ⇒ 症狀不是報錯：舊清單裡有的（`Tim`／`tavern-keeper`）它照收，
            //     新開的（央行 `pacific-standard-…`）它看不到 ⇒ 「這個帳號不存在」與
            //     「我在看另一本帳」在輸出上**逐字同形**（TASK-0260 那一族）。
            //   ⛔ 而舊資料夾整包刪掉之後它會安靜地回「帳戶檔 0 份」——
            //     那跟一棵剛開的新樹長得一模一樣。
            string aAccDir = SCP_BankAccounts.AccountsDir(SCP_BankRegion.BankRootOfDataRoot(aDataRoot));
            var aAccountFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
            var aUnreadable = new List<string>();
            var aBorrowed = new List<string>();
            var aBorrowedSet = new HashSet<string>(StringComparer.Ordinal);
            var aBorrowedFrom = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string aName in aPool)
            {
                string aAcc = SCP_PersonaProfile.GetBankAccount(aLettersRoot, aName, aRegion,
                                                                out string aSrc, out string _);
                // TASK-0265：讀不了 ≠ 沒有綁定 —— 兩者的處置相反（前者重跑、後者去綁），⛔ 不可同格。
                if (aSrc == SCP_PersonaProfile.BankSourceUnreadable) { aUnreadable.Add(aName); continue; }
                if (aAcc.Length == 0) { aNoBinding.Add(aName); continue; }
                if (!string.Equals(aSrc, aRegion, StringComparison.Ordinal))
                { aBorrowed.Add(aName + "（宣告在 " + aSrc + "＝" + aAcc + "）"); aBorrowedSet.Add(aName); aBorrowedFrom[aName] = aSrc; }
                aBinding[aName] = aAcc;
            }

            // ── 合一那一跳：綁定值本身就是正式帳號（與入帳那條路同一個定義）──
            //   物理意義：合一模式下 agent id ＝ 帳號 id，而綁定檔是權威 ⇒ 綁定值天生是合法帳戶。
            //   ⚠ 這不是「多信任一張表」，是把**入帳那條路已經在用的定義**搬過來 ——
            //     兩邊用不同定義才是缺陷本身（TASK-0173）。
            //   ⛔ 銷戶仍然先判（見下方迴圈）：合一讓帳戶**存在**，不讓它**復活**。
            var aUnified = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, string> aKv in aBinding)
                if (!aKnown.Contains(aKv.Value)) aUnified.Add(aKv.Value);
            foreach (string aId in aUnified) aKnown.Add(aId);

            var aUnmaterialized = new List<string>();
            var aClosedHit = new List<string>();
            var aBorrowedUnopened = new List<string>();
            foreach (KeyValuePair<string, string> aKv in aBinding)
            {
                if (aClosed.ContainsKey(aKv.Value))
                { aClosedHit.Add(aKv.Key + " → `" + aKv.Value + "`（" + aClosed[aKv.Value] + "）"); continue; }
                // ⚠ 借用別區的人**不查帳戶存不存在** —— 他的帳戶本來就在別區的帳號宇宙裡，
                //   在這裡查一定查不到。第一版沒排除它，於是 kaguya 天天被報成
                //   「指向不存在的帳戶」。⇒ **一個天天紅的格子，等於沒有那個格子**
                //   （人會學會忽略它，然後真的那一天也一起忽略）。它已經由 ② 說明了。
                //   ⚠ 但「不查」只對**本區有開戶**的那一半成立（TASK-0440）：收付走本區帳本，
                //     借來的帳號在這裡沒開戶 ⇒ 錢收不進來。判準只看**帳戶檔**（開過戶才能收付），
                //     ⛔ 不看 registry 宣告或合一 —— 那兩者都不會替它開戶。
                if (aBorrowedSet.Contains(aKv.Key))
                {
                    if (!aAccountFiles.Contains(aKv.Value))
                        aBorrowedUnopened.Add(aKv.Key + " → `" + aKv.Value + "`（借自 " + aBorrowedFrom[aKv.Key]
                                              + "；本區 `Bank/accounts/` 沒有這個戶 ⇒ 收不到錢）"
                                              + "　⇒ 在本區綁一個有開戶的帳號：`senate cmd persona-profile --arg op=set_bank --arg persona="
                                              + aKv.Key + " --arg account=<帳號> --arg actor=<你> --arg reason=<理由>`");
                    continue;
                }
                // TASK-0441：判準跟入帳那條路對齊 —— **只看帳戶檔**（`SCP_BankAccounts.CheckUsable` 只認它）。
                // 🩸 舊判準是「帳戶檔／system_accounts／agent_banks 三處都沒有」：那是舊 Treasury（寫一筆就長出帳號）的定義，
                //   新銀行沒開戶就收不到錢 ⇒ 綁到「registry 有登記、但沒開戶」的帳號時，舊版這格不列、其他格也不列，
                //   **健檢全綠而錢進不去**。⇒ 這一格現在是缺陷，計入問題。
                // ⚠ 借別區的不在這裡判（上面 ②b 已判），到新區的第一次早安也會自己補（SCP_Morning.EnsureRegionBinding）；
                //   這一格剩下的是「**本區自己的綁定**指向沒開戶的帳號」—— 早安不會替它開，要人決定。
                if (!aAccountFiles.Contains(aKv.Value))
                    aUnmaterialized.Add(aKv.Key + " → `" + aKv.Value + "`（本區 `Bank/accounts/` 沒有這個戶 ⇒ 收不到錢"
                                        + (aUnified.Contains(aKv.Value) ? "；只靠合一成立" : "；registry 有登記但沒開過戶") + "）"
                                        + "　⇒ 開戶：`senate cmd bank --arg op=open --arg account=" + aKv.Value
                                        + " --arg caller=<你>`，或改綁一個有開戶的帳號（`persona-profile --arg op=set_bank`）");
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
            aR.Lines.Add("- 帳號宇宙：帳戶檔 " + aAccountFiles.Count + " 份 ∪ registry 宣告 ∪ 合一綁定值 "
                         + aUnified.Count + " 個 ⇒ 共 " + aKnown.Count + " 個");
            aR.Lines.Add("- pool **" + aPool.Count + "** 位：有綁定 **" + aBinding.Count
                         + "**（其中借用別區 " + aBorrowed.Count + "）／沒有綁定 " + aNoBinding.Count);
            aR.Lines.Add("");
            Section(aR, "⓪ unreadable　綁定檔這一瞬間讀不了（換檔中／被鎖）⇒ **重跑一次**，⛔ 不是沒有綁定", aUnreadable);
            Section(aR, "① no_binding　連別區都沒有宣告　⇒ 這個人的錢無處可去", aNoBinding);
            Section(aR, "② borrowed　只有別區宣告　⇒ **不是錯**，只是要看得見（前提：借來的帳號本區有開戶）", aBorrowed);
            Section(aR, "②b borrowed_unopened　借別區、但借來的帳號**本區沒開戶**　⇒ 🔴 收不到錢", aBorrowedUnopened);
            Section(aR, "③ unmaterialized　本區綁定指向**沒開戶**的帳號　⇒ 🔴 收不到錢（沒開戶不能收付）", aUnmaterialized);
            Section(aR, "④ closed_acct　綁定指向**已銷戶**帳戶　⇒ 🔴 最貴的一格", aClosedHit);
            Section(aR, "⑤ stale_reverse　`bank_personas` 殘留（已不參與解析）", aStale);

            foreach (string aW in aWarn) aR.Lines.Add("⚠ " + aW);

            aR.AddValue("pool_count", aPool.Count.ToString());
            aR.AddValue("bound", aBinding.Count.ToString());
            aR.AddValue("unreadable", aUnreadable.Count.ToString());
            aR.AddValue("no_binding", aNoBinding.Count.ToString());
            aR.AddValue("borrowed", aBorrowed.Count.ToString());
            aR.AddValue("borrowed_unopened", aBorrowedUnopened.Count.ToString());
            aR.AddValue("unmaterialized", aUnmaterialized.Count.ToString());
            aR.AddValue("closed_acct", aClosedHit.Count.ToString());
            aR.AddValue("stale_reverse", aStale.Count.ToString());

            // ⚠ `borrowed` **不計入**問題數 —— 它是狀態不是缺陷（前提：借來的帳號本區有開戶，沒開的在 ②b）。
            // ⚠ `unmaterialized` 計入（TASK-0441，推翻 TASK-0173 的降級，Tim 2026-10-07）：
            //   0173 降級的前提是舊 Treasury「錢照樣入帳」；新銀行沒開戶不能收付，那個前提不在了。
            //   ⇒ 它現在跟 `borrowed_unopened` 是同一種東西：錢收不進來。
            // ⚠ `unreadable` 計入：讀不了的那幾位**沒被檢查到**，這一次不能宣稱「沒有問題」。
            // ⚠ `borrowed_unopened` 計入（TASK-0440）：它不是狀態，是「錢收不進來」。
            int aBad = aUnreadable.Count + aNoBinding.Count + aBorrowedUnopened.Count + aUnmaterialized.Count
                       + aClosedHit.Count + aStale.Count;
            aR.Lines.Add("");
            if (aBad > 0)
            {
                aR.ExitCode = 5;
                aR.Lines.Add("⇒ 共 **" + aBad + "** 項要處理（exit 5；`borrowed` 不計，`borrowed_unopened`／`unmaterialized` 計）。⛔ 本 Cmd 只報不改。");
            }
            else aR.Lines.Add("✅ 計入問題的各格皆 0 —— 每位的綁定都指向本區一個有開戶、未銷戶的帳戶，反向表也清乾淨了。"
                              + "（`borrowed` 是狀態，數字在上面。）");
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
