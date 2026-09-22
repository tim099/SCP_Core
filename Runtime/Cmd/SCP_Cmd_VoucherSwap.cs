// 區塊職責：`cmd voucher-swap` —— 券互換交易（TASK-0272）。
// 物理意義：依據匯率快取（Market/rates_cache.json）進行 USD 中介兩段撮合與小數點無縫結算。
// 數值影響：未帶 `confirm=1` 時為純試算預覽（零寫入）；帶 `confirm=1` 時原子寫回來源券與目標券本。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Paths;
using SCP.Core.Voucher;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_VoucherSwap : SCP_Cmd
    {
        public override string Name => "voucher-swap";

        public override string Summary =>
            "券互換交易：依據匯率快取進行 USD 中介兩段撮合與小數點無縫結算 —— **預設只試算不扣款**";

        public override string Details =>
            "· 預設為純試算預覽（零寫入），驗證匯率、餘額充足性與折算目標量。\n"
            + "· 帶 `--arg confirm=1` 才會真的自來源券扣除並發放目標券。\n"
            + "· 目標券產出套用 TASK-0271 零頭小數點進位模型（滿 1e8 自動進位可用永久券）。\n"
            + "⚠ 無有效匯率快取報價之券種一律無法兌換（並非所有券都能互相兌換）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("voucher-swap --arg persona=gura --arg from=btc --arg to=gold --arg amount=1");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("persona", "執行兌換之 Persona（必填）", iRequired: true),
            new SCP_CmdArgSpec("from", "來源券種代號（如 btc, gold）", iRequired: true),
            new SCP_CmdArgSpec("to", "目標券種代號（如 btc, gold）", iRequired: true),
            new SCP_CmdArgSpec("amount", "欲兌換之整數張數（正整數）", iRequired: true),
            new SCP_CmdArgSpec("confirm", "=1 ⇒ 才會真的執行扣換與落盤；預設為純試算", iDefault: "0"),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（絕對路徑）。省略時自動嘗試推導", iDefault: ""),
            new SCP_CmdArgSpec("data_root", "資料根目錄（絕對路徑）。省略時自動嘗試推導", iDefault: ""),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aPersona = iArgs.Get("persona").Trim();
            if (string.IsNullOrEmpty(aPersona))
                return SCP_CmdResult.Fail(2, "✗ 缺 `persona`");

            string aFrom = iArgs.Get("from").Trim().ToLowerInvariant();
            string aTo = iArgs.Get("to").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(aFrom)) return SCP_CmdResult.Fail(2, "✗ 缺 `from`（來源券）");
            if (string.IsNullOrEmpty(aTo)) return SCP_CmdResult.Fail(2, "✗ 缺 `to`（目標券）");

            string aAmountStr = iArgs.Get("amount").Trim();
            if (!int.TryParse(aAmountStr, out int aAmount) || aAmount <= 0)
                return SCP_CmdResult.Fail(2, $"✗ amount 必須為大於 0 之整數（收到 '{aAmountStr}'）");

            string aLettersRaw = ResolveLettersRoot(iArgs.Get("letters_root"));
            if (!Directory.Exists(aLettersRaw))
                return SCP_CmdResult.Fail(1, $"✗ 信件夾根不存在：{aLettersRaw}");
            var aLetters = new SCP_LettersRoot(aLettersRaw);

            string aDataRoot = ResolveDataRoot(iArgs.Get("data_root"));
            if (!Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(1, $"✗ 資料根不存在：{aDataRoot}");

            bool aConfirm = iArgs.Get("confirm").Trim() == "1";
            DateTime aNow = DateTime.UtcNow;

            SCP_VoucherSwapResult aRes = aConfirm
                ? SCP_VoucherSwap.ExecuteSwap(aLetters, aDataRoot, aPersona, aFrom, aTo, aAmount, aNow)
                : SCP_VoucherSwap.PreviewSwap(aLetters, aDataRoot, aPersona, aFrom, aTo, aAmount, aNow);

            if (!aRes.Success)
            {
                return SCP_CmdResult.Fail(1, $"🔴 兌換失敗：{aRes.Error}");
            }

            var aR = SCP_CmdResult.Success(aConfirm
                ? $"# ✅ 券互換成功：`{aPersona}` 的 `{aFrom}` ➔ `{aTo}`"
                : $"# 🔍 券互換試算預覽（零寫入）：`{aPersona}` 的 `{aFrom}` ➔ `{aTo}`");

            aR.Lines.Add($"- 兌換折算率：1 `{aFrom}` ➔ **{aRes.EffectiveRate:0.########}** `{aTo}`");
            aR.Lines.Add($"- 來源券 `{aFrom}`：扣除 **{aRes.FromConsumed}** 張（扣除後永久券餘額：{aRes.FromRemainingPermanent}，可花餘額：{aRes.FromRemainingSpendable}）");
            aR.Lines.Add($"- 目標券 `{aTo}` 產出：**{(decimal)aRes.ToAddedUnitsE8 / SCP_VoucherBook.FractionScale:0.########}** 張（+{aRes.ToAddedUnitsE8} 聰級單位）");
            aR.Lines.Add($"  · 本次進位新增可用永久券：**+{aRes.ToPermanentAdded}** 張");
            aR.Lines.Add($"  · 最新永久券餘額：**{aRes.ToNewPermanent}** 張");
            aR.Lines.Add($"  · 最新零頭小數池：**{aRes.ToNewFractionalValue:0.########}** 張（`{aRes.ToNewFractionalE8}` / 100,000,000）");

            if (!aConfirm)
            {
                aR.Lines.Add("");
                aR.Lines.Add("💡 **這是純試算預覽，券本一個 byte 都未改動。** 欲正式執行扣換，請加上 `--arg confirm=1`。");
            }

            aR.AddValue("persona", aPersona);
            aR.AddValue("from", aFrom);
            aR.AddValue("to", aTo);
            aR.AddValue("consumed", aRes.FromConsumed.ToString());
            aR.AddValue("effective_rate", aRes.EffectiveRate.ToString());
            aR.AddValue("to_added_units_e8", aRes.ToAddedUnitsE8.ToString());
            aR.AddValue("to_permanent_added", aRes.ToPermanentAdded.ToString());
            aR.AddValue("to_new_permanent", aRes.ToNewPermanent.ToString());
            aR.AddValue("to_new_fractional_e8", aRes.ToNewFractionalE8.ToString());
            aR.AddValue("is_preview", aConfirm ? "0" : "1");
            return aR;
        }

        static string ResolveLettersRoot(string iGiven)
        {
            if (!string.IsNullOrWhiteSpace(iGiven)) return iGiven.Trim().Replace('\\', '/');
            string aCandidate = "D:/Unity/Bar/AgentCommands/ChatTavern/baton/letters";
            if (Directory.Exists(aCandidate)) return aCandidate;
            return Directory.GetCurrentDirectory().Replace('\\', '/');
        }

        static string ResolveDataRoot(string iGiven)
        {
            if (!string.IsNullOrWhiteSpace(iGiven)) return iGiven.Trim().Replace('\\', '/');
            string aCandidate = "D:/Unity/Bar/AgentCommands";
            if (Directory.Exists(aCandidate)) return aCandidate;
            return Directory.GetCurrentDirectory().Replace('\\', '/');
        }
    }
}
