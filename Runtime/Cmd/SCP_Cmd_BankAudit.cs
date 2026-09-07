// 區塊職責：`cmd bank-audit` —— 「錢屬於誰」那張反向表（`bank_personas`）的**唯讀對帳**。
//           **原生，不需要 Unity Editor。**
//
// 物理意義：§8.1（Tim 2026-08-19 拍板）反向表 `bank_personas[<bank>] = [personas…]` 是權威表，
//           而它**全樹只有讀取端、零寫入端** —— 現值是 2026-08-19 人工導出手寫的（TASK-0083）。
//   ⇒ 一張沒有寫入端的權威表，它的失效方式不是「壞掉」，是**安靜地跟現實愈差愈遠**：
//     每建一個 persona／改一次綁定，覆蓋率就掉一格，而沒有任何一層會出聲。
//
//   本 Cmd **不補寫入端**（那是政策題：Tim 2026-08-31 拍板「讀綁定不是動錢，**寫綁定是**」
//   ⇒ 共用層不碰綁定寫入）。它做的是另一件事：**把「有沒有漂」變成一個誰都能在任何一天取得的讀數**。
//   🩸 判準來自 TASK-0083 現場：這張表**推導得出來**（正向 `bank/<region>.md` 反轉即得）——
//     而一個推導得出來的索引，需要的是**產生器與對帳**，不是「建人時記得也寫一筆」。
//     「記得」正是系統不能依賴的東西。
//
// 數值影響：**純唯讀**。四種不一致各自分開報（它們的處置不同，混成一個數字等於沒報）：
//   ① hole    本區有綁定、反向表沒登記   ⇒ 補登記
//   ② ghost   反向表有、本區沒有綁定     ⇒ 可能是別區的人，也可能是舊資料
//   ③ dup     同一人被登記到多家 bank    ⇒ 解析會**拒絕**（錢進錯帳戶是最貴的靜默錯）
//   ④ mismatch 正反都有，但**指到不同的 bank** ⇒ ⛔ 這一格不准自動修，它是錢的歸屬
//
// ⚠ **區域定語不可省**：`bank/` 是 per-region 的，而反向表在**本專案的** `_registry_meta.json`。
//   🩸 2026-09-07 我第一版沒帶定語就得出「覆蓋率 95%、破洞是 kaguya」——
//   而 kaguya 只有別區（BTC）綁定，她不在本區表裡是**對的**。
//   那是一個**形狀正確的錯答案**，而它會讓人去「修」一個沒有壞的東西。
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
            "`bank_personas` 反向表對帳：正向綁定 ✕ 反向登記，四種不一致分開報 —— **唯讀，不需要 Editor**";

        public override string Details =>
            "正向真相源＝`letters/<persona>/bank/<region>.md`；反向表＝`_registry_meta.json` 的 `bank_personas`。\n"
            + "⚠ **必須給 region** —— `bank/` 是 per-region 的，沒有區域定語算出來的覆蓋率是形狀正確的錯答案。\n"
            + "· `--arg emit=1` 額外印出「照正向推導出來的那張表」（JSON），⛔ **本 Cmd 不寫任何檔**。\n"
            + "· exit 0＝四種不一致都是 0；exit 5＝有不一致（數字在 values 裡，逐項在輸出裡）。\n"
            + "⛔ `mismatch`（正反指到不同 bank）**不要自動修** —— 那是錢的歸屬，要人拍板。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("bank-audit --arg letters_root=<letters> --arg data_root=<AgentCommands>"
                                   + " --arg region=Florin");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("region", "本區的區域（貨幣）ID —— 決定讀哪一份 bank 綁定。⛔ 必填，本 Cmd 不推導", iRequired: true),
            new SCP_CmdArgSpec("data_root", "資料根；`<data_root>/AwakenInit/_registry_meta.json` ＝ 反向表所在（與 registry 二擇一）"),
            new SCP_CmdArgSpec("registry", "反向表檔案路徑（直接指定；與 data_root 二擇一，兩個都給以本欄為準）"),
            new SCP_CmdArgSpec("emit", "=1 額外印出照正向推導出來的 `bank_personas`（JSON）。⛔ 只印不寫"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aRegion = iArgs.Get("region").Trim();
            string aDataRoot = iArgs.Get("data_root").Trim();
            string aRegistry = iArgs.Get("registry").Trim();
            bool aEmit = IsOn(iArgs.Get("emit"));

            if (aRegion.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ region 是必填 —— 沒有區域定語的覆蓋率是**形狀正確的錯答案**");
            if (!Directory.Exists(aLettersRoot))
                return SCP_CmdResult.Fail(1, "✗ 信件夾根不存在：" + aLettersRoot);

            if (aRegistry.Length == 0)
            {
                if (aDataRoot.Length == 0)
                    return SCP_CmdResult.Fail(2, "✗ 要給 --arg registry=<檔> 或 --arg data_root=<AgentCommands>");
                aRegistry = Path.Combine(aDataRoot, "AwakenInit", "_registry_meta.json");
            }
            if (!File.Exists(aRegistry))
                return SCP_CmdResult.Fail(1, "✗ 找不到反向表：" + aRegistry);

            SCP_JsonData aMeta;
            try { aMeta = SCP_JsonParser.Parse(File.ReadAllText(aRegistry)); }
            catch (Exception e)
            { return SCP_CmdResult.Fail(1, "✗ 反向表不是合法 JSON：" + e.Message, "  " + aRegistry); }

            // ── 反向：bank → personas ─────────────────────────────────────────
            var aStored = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var aBankOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);   // persona → banks
            if (aMeta.Contains("bank_personas"))
            {
                SCP_JsonData aBp = aMeta["bank_personas"];
                foreach (string aBank in aBp.Keys)
                {
                    var aNames = new List<string>();
                    foreach (SCP_JsonData aN in aBp[aBank]) aNames.Add(aN.AsString());
                    aStored[aBank] = aNames;
                    foreach (string aN in aNames)
                    {
                        if (!aBankOf.TryGetValue(aN, out List<string>? aList))
                        { aList = new List<string>(); aBankOf[aN] = aList; }
                        aList.Add(aBank);
                    }
                }
            }

            // ── 正向：persona → 本區綁定（含「借用別區」三態）─────────────────
            var aWarn = new List<string>();
            List<string> aPool = SCP_PersonaProfile.PoolNames(aLettersRoot, m => aWarn.Add(m));
            var aForward = new Dictionary<string, string>(StringComparer.Ordinal);        // persona → bank（本區宣告）
            var aBorrowed = new List<string>();                                           // 只有別區綁定
            var aNoBinding = new List<string>();                                          // 一區都沒有
            foreach (string aName in aPool)
            {
                string aAcc = SCP_PersonaProfile.GetBankAccount(aLettersRoot, aName, aRegion,
                                                                out string aSrc, out string _);
                if (aAcc.Length == 0) { aNoBinding.Add(aName); continue; }
                // ⚠ 借用別區 ≠ 本區宣告：把它算進本區覆蓋率，就是把「他是別區的人」讀成「他漏登記」。
                if (!string.Equals(aSrc, aRegion, StringComparison.Ordinal))
                { aBorrowed.Add(aName + "（來源 " + aSrc + "＝" + aAcc + "）"); continue; }
                aForward[aName] = aAcc;
            }

            // ── 四種不一致 ───────────────────────────────────────────────────
            var aHole = new List<string>();
            var aMismatch = new List<string>();
            foreach (KeyValuePair<string, string> aKv in aForward)
            {
                if (!aBankOf.TryGetValue(aKv.Key, out List<string>? aBanks))
                { aHole.Add(aKv.Key + "（本區綁 " + aKv.Value + "）"); continue; }
                if (aBanks.Count == 1 && !string.Equals(aBanks[0], aKv.Value, StringComparison.Ordinal))
                    aMismatch.Add(aKv.Key + "：正向 `" + aKv.Value + "` ／ 反向表登記在 `" + aBanks[0] + "`");
            }
            var aGhost = new List<string>();
            foreach (KeyValuePair<string, List<string>> aKv in aBankOf)
                if (!aForward.ContainsKey(aKv.Key))
                    aGhost.Add(aKv.Key + "（反向表放在 " + string.Join("/", aKv.Value) + "）");
            var aDup = new List<string>();
            foreach (KeyValuePair<string, List<string>> aKv in aBankOf)
                if (aKv.Value.Count > 1) aDup.Add(aKv.Key + " → " + string.Join(" / ", aKv.Value));

            aHole.Sort(); aGhost.Sort(); aDup.Sort(); aMismatch.Sort();

            var aR = new SCP_CmdResult();
            aR.Lines.Add("# bank_personas 對帳　region=`" + aRegion + "`");
            aR.Lines.Add("- 反向表：`" + aRegistry + "`（" + aStored.Count + " 家 bank）");
            aR.Lines.Add("- 正向真相源：`letters/<persona>/bank/" + aRegion + ".md`");
            aR.Lines.Add("- pool **" + aPool.Count + "** 位：本區宣告 **" + aForward.Count
                         + "**／借用別區 " + aBorrowed.Count + "／完全沒綁定 " + aNoBinding.Count);
            if (aBorrowed.Count > 0)
                aR.Lines.Add("  · 借用別區（**不算本區破洞**）：" + string.Join("、", aBorrowed));
            if (aNoBinding.Count > 0)
                aR.Lines.Add("  · 完全沒有 bank 綁定：" + string.Join("、", aNoBinding));
            aR.Lines.Add("");
            Section(aR, "① hole　本區有綁定、反向表沒登記　⇒ 補登記", aHole);
            Section(aR, "② ghost　反向表有、本區沒有綁定　⇒ 可能是別區的人或舊資料", aGhost);
            Section(aR, "③ dup　同一人被登記到多家 bank　⇒ **解析會拒絕**", aDup);
            Section(aR, "④ mismatch　正反都有但指到不同 bank　⇒ ⛔ **錢的歸屬，不准自動修**", aMismatch);

            if (aEmit)
            {
                // 照正向推導出來的那張表 —— 給人拿去對照／套用。⛔ 本 Cmd 不寫檔。
                var aDerived = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach (KeyValuePair<string, string> aKv in aForward)
                {
                    if (!aDerived.TryGetValue(aKv.Value, out List<string>? aL))
                    { aL = new List<string>(); aDerived[aKv.Value] = aL; }
                    aL.Add(aKv.Key);
                }
                var aOut = SCP_JsonData.NewObject();
                var aKeys = new List<string>(aDerived.Keys); aKeys.Sort(StringComparer.Ordinal);
                foreach (string aBank in aKeys)
                {
                    aDerived[aBank].Sort(StringComparer.Ordinal);
                    var aArr = SCP_JsonData.NewArray();
                    foreach (string aN in aDerived[aBank]) aArr.Add(SCP_JsonData.NewString(aN));
                    aOut.Set(aBank, aArr);
                }
                aR.Lines.Add("");
                aR.Lines.Add("## 照正向推導出來的 bank_personas（本區；⛔ 只印不寫）");
                aR.Lines.Add(aOut.ToJson(true));
            }

            foreach (string aW in aWarn) aR.Lines.Add("⚠ " + aW);

            aR.AddValue("pool_count", aPool.Count.ToString());
            aR.AddValue("declared_here", aForward.Count.ToString());
            aR.AddValue("borrowed", aBorrowed.Count.ToString());
            aR.AddValue("no_binding", aNoBinding.Count.ToString());
            // 四個數字分開落 —— 加總成一個「不一致數」會讓處置相反的東西同形。
            aR.AddValue("hole", aHole.Count.ToString());
            aR.AddValue("ghost", aGhost.Count.ToString());
            aR.AddValue("dup", aDup.Count.ToString());
            aR.AddValue("mismatch", aMismatch.Count.ToString());

            int aBad = aHole.Count + aGhost.Count + aDup.Count + aMismatch.Count;
            if (aBad > 0)
            {
                aR.ExitCode = 5;
                aR.Lines.Add("");
                aR.Lines.Add("⇒ 共 **" + aBad + "** 項不一致（exit 5）。⛔ 本 Cmd 只報不改。");
            }
            else
            {
                aR.Lines.Add("");
                aR.Lines.Add("✅ 四項都是 0 —— 本區的反向表與正向綁定一致。");
            }
            return aR;
        }

        /// <summary>一段一節；**空的時候也印節標題**，因為「這一格是 0」與「我沒有量這一格」不可同形。</summary>
        static void Section(SCP_CmdResult ioR, string iTitle, List<string> iItems)
        {
            ioR.Lines.Add("## " + iTitle + "：**" + iItems.Count + "**");
            foreach (string aItem in iItems) ioR.Lines.Add("  - " + aItem);
        }

        static bool IsOn(string iValue)
        {
            string v = (iValue ?? "").Trim();
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
        }
    }
}
