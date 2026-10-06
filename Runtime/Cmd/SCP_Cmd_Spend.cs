// 區塊職責：**消費時間**的 CLI 入口（`senate cmd spend`）—— 擲一份可消費清單（前三項遞減折扣）／列出全部通道（TASK-0333）。
// 物理意義：移植自 `spend_menu.py`（Tim 2026-09-30：python 端入口完全廢除，遷到 Senate CLI）。清單與擲骰規則在 `SCP_SpendMenu`。
//          · 餘額直接問 Server 的 `bank`（py 版繞 Editor 的 Treasury）；帳號沒給就用 persona 解析（`SCP_BankAccountResolver`）。
//          · 擲骰結果同步進酒館（帶 persona 才發；`no_post=1` 可關）—— 消費從一個人的動作變成看得見的事件
//            （Tim 2026-08-01：沒有人看得到的行為，不會因為多一個工具就開始發生）。發文失敗只警告，不影響擲骰本體。
// 數值影響：**不動任何錢**。餘額問不到 ⇒ 額度印「不知道」，⛔ 不印 0（0 是「查到了沒錢」，看起來會像破產）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using SCP.Core.Bank;
using SCP.Core.Docs;
using SCP.Core.Letters;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Spend : SCP_Cmd
    {
        public override string Name => "spend";
        public override string Category => SCP_CmdCategory.Routine;

        public override string Summary => "消費時間：擲一份可消費清單（前三項 50／20／10% 折扣、額度＝餘額 10%）／列出全部通道 —— **不需要 Editor、不動任何錢**";

        public override string Details =>
            "· `op=roll --arg persona=<me> [--arg account=<帳號>] [--arg count=3] [--arg no_post=1]`：擲清單、算額度，帶 persona 會同步到酒館。\n"
            + "· `op=list`：列出全部可用通道（不擲骰）。\n"
            + "清單：共用層 `SCP_Core/Docs~/" + SCP_SpendMenu.SharedSubdir + "` ＋ 專案層 `<資料根>/Spending/Items`（同 id 專案層覆蓋，`enabled: false` 可跨層停用）。\n"
            + "⚠ 只放**有可執行工具**的通道 —— 骰面宣稱做得到而實際做不到，比沒有那個選項更糟。\n"
            + "💸 折扣**不自動退**：照原價付，之後開請款單領回（`bank-request op=request --arg source_kind=spend_menu_rebate`），Tim 核准後撥款。";

        public override string Example => SCP_CmdRegistry.Invoke("spend --arg op=roll --arg persona=Template --arg no_post=1");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（專案層清單、帳本）", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（persona → 帳號解析）", iRequired: true),
            new SCP_CmdArgSpec("op", "做什麼", iDefault: "roll", iChoices: new[] { "roll", "list" }),
            new SCP_CmdArgSpec("persona", "op=roll：誰在消費（顯示＋酒館同步；沒給 account 時拿來解析帳號）", iDefault: ""),
            new SCP_CmdArgSpec("account", "op=roll：查餘額用的帳號（空＝由 persona 解析）", iDefault: ""),
            new SCP_CmdArgSpec("count", "op=roll：擲幾項", iDefault: SCP_SpendMenu.DefaultRollCount.ToString(CultureInfo.InvariantCulture)),
            new SCP_CmdArgSpec("no_post", "op=roll：1＝不同步到酒館", iDefault: "0", iChoices: new[] { "0", "1" }),
            new SCP_CmdArgSpec("seed", "op=roll：亂數種子（測試／重現用；空＝每次不同）", iDefault: ""),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aData = iArgs.Get("data_root").Trim();
            string aLetters = iArgs.Get("letters_root").Trim();
            string aPersona = iArgs.Get("persona").Trim(), aAccount = iArgs.Get("account").Trim();
            string aCountRaw = iArgs.Get("count").Trim(), aSeedRaw = iArgs.Get("seed").Trim();
            bool aNoPost = iArgs.Get("no_post").Trim() == "1";

            var aWarn = new List<string>();
            string? aShared = SharedDir(aWarn);
            string aProjectDir = SCP_SpendMenu.ProjectItemsDir(aData);
            List<SCP_SpendItem> aItems = SCP_SpendMenu.Load(aShared, aProjectDir, out int aNProj, aWarn);
            string aSource = $"共用 {aItems.Count - aNProj} + 專案 {aNProj}";

            if (iArgs.Get("op").Trim() == "list")
            {
                var r = SCP_CmdResult.Success($"# 🛒 可消費通道（{aSource}，共 {aItems.Count} 項）");
                foreach (var it in aItems)
                    r.Lines.Add($"- `{it.Id}`　{it.Name}" + (it.Kind.Length > 0 ? $"　[{it.Kind}]" : "")
                                + (it.UnitCost.Length > 0 ? $"　{it.UnitCost} token/單位" : "") + $"　（{it.Layer}）");
                r.Lines.Add($"共用層：{aShared ?? "（宿主沒裝 scp_core 文件根）"}");
                r.Lines.Add($"專案層：{aProjectDir}");
                foreach (string w in aWarn) r.Lines.Add("⚠ " + w);
                r.AddValue("items", aItems.Count.ToString(CultureInfo.InvariantCulture));
                return r;
            }

            if (!int.TryParse(aCountRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aCount) || aCount <= 0)
                return SCP_CmdResult.Fail(2, $"✗ count 要是正整數（收到 '{aCountRaw}'）");
            if (aItems.Count == 0)
                return SCP_CmdResult.Fail(1, "⚠ 沒有任何可消費項目。", $"   共用層：{aShared ?? "（沒裝）"}", $"   專案層：{aProjectDir}",
                                          "   新增項目＝丟一個帶 frontmatter（id／name／enabled）的 md 進去。");
            Random aRng;
            if (aSeedRaw.Length > 0)
            {
                if (!int.TryParse(aSeedRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aSeed))
                    return SCP_CmdResult.Fail(2, $"✗ seed 要是整數（收到 '{aSeedRaw}'）");
                aRng = new Random(aSeed);
            }
            else aRng = new Random();
            List<SCP_SpendItem> aRolled = SCP_SpendMenu.Roll(aItems, aCount, aRng);

            // 餘額 ⇒ 額度（問不到 ⇒ -1，⛔ 不當 0）
            string aBalanceNote;
            long aBal = QueryBalance(aData, aLetters, aPersona, ref aAccount, out aBalanceNote);
            long aCap = SCP_SpendMenu.CapOf(aBal);

            var res = SCP_CmdResult.Success($"# 🛒 消費時間 — {(aPersona.Length > 0 ? aPersona : "(未指定 persona)")}");
            res.Lines.Add($"清單來源：{aSource}（共 {aItems.Count} 項可用，本次擲出 {aRolled.Count} 項）");
            res.Lines.Add(aBal >= 0
                ? $"帳戶 `{aAccount}` 餘額 **{aBal}** → 本次額度上限 **{aCap}**（當前餘額 10%，向下取整）"
                : $"⚠ 餘額查不到（{aBalanceNote}）—— **這不代表你沒錢**，是查詢失敗。額度請自行確認。");
            res.Lines.Add("");
            for (int i = 0; i < aRolled.Count; i++)
            {
                var it = aRolled[i];
                double off = SCP_SpendMenu.DiscountAt(i);
                res.Lines.Add($"## {i + 1}. {it.Name}　`{it.Id}`　{(off > 0 ? $"**{(int)(off * 100)}% off**" : "原價")}");
                if (it.Kind.Length > 0) res.Lines.Add($"性質：{it.Kind}");
                res.Lines.Add("");
                res.Lines.Add(it.Body);
                res.Lines.Add("");
            }
            res.Lines.Add("---");
            res.Lines.Add("## 💸 折扣怎麼拿（走請款，不自動退）");
            res.Lines.Add("消費**照原價付**，之後開一張請款單把折扣領回來 —— Tim 核准後由央行撥款：");
            res.Lines.Add("```bash");
            res.Lines.Add($"senate cmd bank-request --arg op=request --arg persona={(aPersona.Length > 0 ? aPersona : "<me>")} --arg target_bank={(aAccount.Length > 0 ? aAccount : "<你的帳號>")} \\");
            res.Lines.Add("  --arg amount=<折扣金額> --arg source_kind=spend_menu_rebate --arg-file reason=<檔：消費時間 第N項 <item_id> 折扣 X%：原價 A → 退 B>");
            res.Lines.Add("```");
            res.Lines.Add("- 折扣按**骰出清單的位置**算：第 1 項 50% / 第 2 項 20% / 第 3 項 10%，第 4 項起無折扣；退費＝原價 × 折扣率，**向下取整**。");
            res.Lines.Add("- ⚠ 請款單要寫清楚是哪一項、原價多少 —— 核准的人看不到你這次擲了什麼。");
            res.Lines.Add("_本指令不動任何錢：只擲清單、算額度、印指令。_");
            foreach (string w in aWarn) res.Lines.Add("⚠ " + w);

            if (aPersona.Length > 0 && !aNoPost)
            {
                var aPost = new List<string> { $"🛒 **消費時間** — {aPersona} 擲出 {aRolled.Count} 項" };
                if (aBal >= 0) aPost.Add($"餘額 {aBal} → 本次額度上限 **{aCap}**（當前餘額 10%）");
                aPost.Add("");
                for (int i = 0; i < aRolled.Count; i++)
                {
                    double off = SCP_SpendMenu.DiscountAt(i);
                    aPost.Add($"{i + 1}. **{aRolled[i].Name}**　`{aRolled[i].Id}`" + (off > 0 ? $"　← **{(int)(off * 100)}% off**" : "")
                              + (aRolled[i].Kind.Length > 0 ? $"　[{aRolled[i].Kind}]" : ""));
                }
                aPost.Add("");
                aPost.Add("折扣按骰出位置遞減（50 / 20 / 10%），照原價付、事後開請款單領回（央行撥款）。");
                aPost.Add("_擲到不等於要花 —— 自決不花是合法結果。_");
                var aPostArgs = new Dictionary<string, string>
                {
                    ["persona"] = aPersona,
                    ["body"] = string.Join("\n", aPost),
                    ["tag"] = "spend-menu",
                    ["meta"] = "{\"category\":\"chat\"}",
                };
                SCP_CmdResult aP = SCP_CmdRegistry.Dispatch("tavern-post", aPostArgs);
                res.Lines.Add(aP.ExitCode == 0
                    ? "📣 已同步到酒館" + (Value(aP, "post_seq") is { Length: > 0 } s ? $"（seq {s}）" : "")
                    : $"⚠ 酒館同步失敗（exit {aP.ExitCode}；不影響上面的結果）" + (aP.ExitCode == 7 ? " ⛔ exit 7＝不知道有沒有發 —— 先回讀再補" : ""));
            }
            res.AddValue("rolled", string.Join(",", aRolled.Select(i => i.Id)));
            res.AddValue("balance", aBal.ToString(CultureInfo.InvariantCulture));
            res.AddValue("cap", aCap.ToString(CultureInfo.InvariantCulture));
            return res;
        }

        /// <summary>共用層＝宿主裝上的 `scp_core` 文件根底下的 Spending/Items（TASK-0337 的文件根）。沒裝 ⇒ null ＋ warning（⛔ 不猜路徑）。</summary>
        static string? SharedDir(List<string> ioWarn)
        {
            IReadOnlyList<SCP_DocRoot>? aRoots = SCP_DocStore.RootsProvider?.Invoke();
            SCP_DocRoot? aScp = aRoots?.FirstOrDefault(r => r.Label == "scp_core");
            if (aScp == null) { ioWarn.Add("宿主沒有裝 `scp_core` 文件根 ⇒ 共用層清單讀不到（只剩專案層）"); return null; }
            return Path.Combine(aScp.Path, SCP_SpendMenu.SharedSubdir).Replace('\\', '/');
        }

        static long QueryBalance(string iData, string iLetters, string iPersona, ref string ioAccount, out string oNote)
        {
            if (ioAccount.Length == 0)
            {
                if (iPersona.Length == 0) { oNote = "沒給 persona 也沒給 account"; return -1; }
                string aRegion = SCP_BankRegion.Read(iData, out string? aWhy);
                if (string.IsNullOrWhiteSpace(aRegion)) { oNote = "讀不到本區區域（" + (aWhy ?? "?") + "）"; return -1; }
                ioAccount = SCP_BankAccountResolver.ResolvePersonaAccount(iLetters, iData, aRegion, iPersona, out string aTrace);
                if (ioAccount.Length == 0) { oNote = $"`{iPersona}` 解析不到帳號（{aTrace}）"; return -1; }
            }
            SCP_CmdResult aR = SCP_CmdRegistry.Dispatch("bank", new Dictionary<string, string>
            {
                ["op"] = "balance",
                ["bank_root"] = SCP_BankRegion.BankRootOfDataRoot(iData),
                ["account"] = ioAccount,
            });
            if (aR.ExitCode != 0) { oNote = $"bank exit {aR.ExitCode}：{string.Join(" ／ ", aR.Lines)}"; return -1; }   // 全部行：第一行常只是路由告示
            if (!long.TryParse(Value(aR, "balance"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long b))
            { oNote = "bank 成功但讀不到 balance 欄"; return -1; }
            oNote = "";
            return b;
        }

        static string Value(SCP_CmdResult iR, string iKey)
        {
            foreach (var kv in iR.Values) if (kv.Key == iKey) return kv.Value;
            return "";
        }
    }
}
