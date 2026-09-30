// 區塊職責：**掛號信**的 CLI 入口（`senate cmd mail`）—— 寄件（扣郵資）／收件匣／確認閱讀／查郵資（TASK-0333）。
// 物理意義：移植自 `registered_mail.py`（Tim 2026-09-30：python 端入口完全廢除，遷到 Senate CLI）。
//          信件格式與讀取規則只有一份：`SCP_RegisteredMail`（Editor 那支寄系統信也呼叫它；早安 brief 的投遞也讀它，TASK-0347）。
//          跟既有三種東西的分界（別再開第四套）：給未來自己的信＝晚安儀式產物；酒館 @＝公開、即時、免費；
//          掛號信＝**指名、付費、可指定未來的 wake 投遞**。
// 數值影響：
//   · 郵資讀 `SCP_BankPolicy.MailFee`（後台可調，讀不到回預設 5 —— ⛔ 不回 0：設定檔壞掉不該靜默變成免費）。
//   · 郵資**蒸發，不進央行**（Tim 2026-08-01）⇒ 純 debit。扣款直接串 Server 的 `bank`（⛔ 不再繞 Editor 的 Treasury）。
//   · 先扣費再寫檔：錢是難以靜默還原的那一端。扣了但寫檔失敗 ⇒ 印出 ref 讓人開請款單退回，⛔ 不假裝沒發生。
//   · 扣款非零一律當「沒扣到」⇒ 不寄（不確定有沒有扣到就當沒扣 —— 當成扣到了就是白寄）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Bank;
using SCP.Core.Letters;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Mail : SCP_Cmd
    {
        public override string Name => "mail";

        public override string Summary => "掛號信：寄（扣郵資，可指定未來 wake 投遞）／收件匣／確認閱讀／查郵資 —— **不需要 Editor**（扣款直接串 Server）";

        public override string Details =>
            "· `op=send --arg from=<寄件 persona> --arg to=<收件 persona> --arg-file body=<檔> [--arg subject=…] [--arg deliver_at_wake=<N>]`\n"
            + "  郵資蒸發、不進央行；收件者 wake #N 之後（含）醒來才看得到，不帶＝下次醒來。晚醒不會吃掉信（指定 #100 而現在 #105 ⇒ 照樣出現）。\n"
            + "· `op=inbox --arg persona=<p> [--arg wake=<目前 wake>]`：到期／未到期兩串（不帶 wake ＝ 全部算到期）。\n"
            + "· `op=ack --arg persona=<p> [--arg file=<mailbox 內檔名>]`：確認閱讀 —— **除名的唯一方式**（信會每次醒來都出現，直到 ack）；同時回寫寄件者的寄件備份（已讀回執）。\n"
            + "· `op=fee`：目前郵資。\n"
            + "📌 早安 brief 的「📮 掛號信」一節會列出到期未確認的信，第一次列出時蓋送達章（TASK-0347）。";

        public override string Example => SCP_CmdRegistry.Invoke("mail --arg op=inbox --arg persona=Template");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（郵資設定、帳本）", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根", iRequired: true),
            new SCP_CmdArgSpec("op", "做什麼", iDefault: "inbox", iChoices: new[] { "send", "inbox", "ack", "fee" }),
            new SCP_CmdArgSpec("from", "op=send：寄件 persona（付郵資的人）", iDefault: ""),
            new SCP_CmdArgSpec("to", "op=send：收件 persona（可以是自己）", iDefault: ""),
            new SCP_CmdArgSpec("subject", "op=send：主旨（選填）", iDefault: ""),
            new SCP_CmdArgSpec("body", "op=send：內文 —— 長信一律 `--arg-file body=<檔>`", iDefault: ""),
            new SCP_CmdArgSpec("deliver_at_wake", "op=send：收件者第幾次醒來才投遞（空＝下次醒來）", iDefault: ""),
            new SCP_CmdArgSpec("persona", "op=inbox／ack：收件者（你自己）", iDefault: ""),
            new SCP_CmdArgSpec("wake", "op=inbox：目前 wake 編號（判斷哪些到期；空＝全部算到期）", iDefault: ""),
            new SCP_CmdArgSpec("file", "op=ack：mailbox 內的檔名（空＝ack 全部未讀）", iDefault: ""),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aData = iArgs.Get("data_root").Trim();
            string aLetters = iArgs.Get("letters_root").Trim();
            // 先全部讀掉（⛔ 別讓沒用到的參數被報「給了沒讀」）
            string aFrom = iArgs.Get("from").Trim(), aTo = iArgs.Get("to").Trim(), aSubject = iArgs.Get("subject").Trim();
            string aBody = iArgs.Get("body"), aDeliverRaw = iArgs.Get("deliver_at_wake").Trim();
            string aPersona = iArgs.Get("persona").Trim(), aWakeRaw = iArgs.Get("wake").Trim(), aFile = iArgs.Get("file").Trim();
            if (!Directory.Exists(aLetters)) return SCP_CmdResult.Fail(2, $"✗ 信件夾根不存在：{aLetters}");

            switch (iArgs.Get("op").Trim())
            {
                case "fee":
                {
                    SCP_BankPolicy.Reading p = SCP_BankPolicy.Read(aData, out string? aWhy);
                    var r = SCP_CmdResult.Success($"目前郵資：{p.MailFee} token / 封（蒸發，不進央行）" + (aWhy != null ? $"　⚠ {aWhy}" : ""));
                    r.AddValue("fee", p.MailFee.ToString(CultureInfo.InvariantCulture));
                    return r;
                }
                case "send": return Send(aData, aLetters, aFrom, aTo, aSubject, aBody, aDeliverRaw);
                case "inbox":
                {
                    if (!SCP_RegisteredMail.IsValidPersonaName(aPersona)) return SCP_CmdResult.Fail(2, "✗ 要 `--arg persona=<收件者>`");
                    int? aWake = null;
                    if (aWakeRaw.Length > 0)
                    {
                        if (!int.TryParse(aWakeRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int w))
                            return SCP_CmdResult.Fail(2, $"✗ wake 要是整數（收到 '{aWakeRaw}'）");
                        aWake = w;
                    }
                    SCP_RegisteredMail.ListUnread(aLetters, aPersona, aWake, out var aDue, out var aLater);
                    var r = SCP_CmdResult.Success($"# 📮 {aPersona} 的掛號信收件匣" + (aWake.HasValue ? $"（目前 wake #{aWake}）" : "（沒給 wake ⇒ 全部算到期）"));
                    if (aDue.Count == 0 && aLater.Count == 0) r.Lines.Add("(沒有未確認的掛號信)");
                    if (aDue.Count > 0)
                    {
                        r.Lines.Add($"## 可讀取（{aDue.Count} 封）");
                        foreach (var m in aDue)
                            r.Lines.Add($"- **@{m.From}** → {(m.Subject.Length > 0 ? m.Subject : "(無主旨)")}"
                                        + (m.DeliverAtWake.HasValue ? $"　[指定 wake #{m.DeliverAtWake}]" : "")
                                        + (m.FirstSeenWake.Length > 0 ? $"　（首次投遞 wake #{m.FirstSeenWake}）" : "")
                                        + $"\n  `{m.Path}`");
                    }
                    if (aLater.Count > 0)
                    {
                        r.Lines.Add($"## 未到投遞時點（{aLater.Count} 封，先不拆）");
                        foreach (var m in aLater) r.Lines.Add($"- @{m.From} → wake #{m.DeliverAtWake}　{(m.Subject.Length > 0 ? m.Subject : "(無主旨)")}");
                    }
                    r.AddValue("due", aDue.Count.ToString(CultureInfo.InvariantCulture));
                    r.AddValue("later", aLater.Count.ToString(CultureInfo.InvariantCulture));
                    return r;
                }
                case "ack":
                {
                    if (!SCP_RegisteredMail.IsValidPersonaName(aPersona)) return SCP_CmdResult.Fail(2, "✗ 要 `--arg persona=<收件者>`");
                    List<SCP_RegisteredMail.AckResult> aRes = SCP_RegisteredMail.Ack(aLetters, aPersona, aFile.Length > 0 ? aFile : null);
                    if (aRes.Count == 0) return SCP_CmdResult.Success("(沒有待確認的掛號信)");
                    int aAcked = 0, aBad = 0;
                    var r = SCP_CmdResult.Success($"# 📮 {aPersona} 確認閱讀");
                    foreach (var a in aRes)
                    {
                        switch (a.State)
                        {
                            case "acked":
                                aAcked++;
                                r.Lines.Add($"✅ {a.FileName}" + (a.FirstSeenWake.Length > 0 ? $"（首次投遞 wake #{a.FirstSeenWake}）" : "")
                                            + (a.MirrorUpdated ? "　寄件者已讀回執 ✓" : "　寄件者副本找不到（不擋 ack）"));
                                break;
                            case "already": r.Lines.Add($"· 已確認過，跳過：{a.FileName}"); break;
                            case "missing": aBad++; r.Lines.Add($"✗ 找不到：{a.FileName}"); break;
                            default: aBad++; r.Lines.Add($"✗ 寫不進 read_at：{a.FileName}"); break;
                        }
                    }
                    r.Lines.Add($"共 {aAcked} 封除名 —— 之後的 wake brief 不再列出。");
                    r.AddValue("acked", aAcked.ToString(CultureInfo.InvariantCulture));
                    if (aBad > 0) r.ExitCode = 1;
                    return r;
                }
                default: return SCP_CmdResult.Fail(2, "✗ 不認得的 op");
            }
        }

        static SCP_CmdResult Send(string iData, string iLetters, string iFrom, string iTo, string iSubject, string iBody, string iDeliverRaw)
        {
            if (!SCP_RegisteredMail.IsValidPersonaName(iFrom) || !SCP_RegisteredMail.IsValidPersonaName(iTo))
                return SCP_CmdResult.Fail(2, "✗ `from`／`to` 皆必填（persona 名稱，不含路徑字元）");
            if (string.IsNullOrWhiteSpace(iBody)) return SCP_CmdResult.Fail(2, "✗ 信件內容為空（`--arg-file body=<檔>`）");
            int? aDeliver = null;
            if (iDeliverRaw.Length > 0)
            {
                if (!int.TryParse(iDeliverRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int d) || d < 0)
                    return SCP_CmdResult.Fail(2, $"✗ deliver_at_wake 要是非負整數（收到 '{iDeliverRaw}'）");
                aDeliver = d;
            }
            if (!Directory.Exists(Path.Combine(iLetters, iTo)))
                return SCP_CmdResult.Fail(2, $"✗ 沒有這個收件 persona：`{iTo}`（{iLetters} 底下沒有這個信件夾）⇒ 未扣費、未寄出");

            SCP_BankPolicy.Reading aPolicy = SCP_BankPolicy.Read(iData, out string? aPolicyWhy);
            int aFee = aPolicy.MailFee;
            string aTs = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
            string aRef = $"mail-{aTs}-{iFrom}-to-{iTo}";
            string aAccount = "";

            if (aFee > 0)
            {
                string aRegion = SCP_BankRegion.Read(iData, out string? aRegionWhy);
                if (string.IsNullOrWhiteSpace(aRegion))
                    return SCP_CmdResult.Fail(1, "✗ 讀不到本區區域（" + (aRegionWhy ?? "?") + "）⇒ 無法解析帳號，未扣費、未寄出");
                aAccount = SCP_BankAccountResolver.ResolvePersonaAccount(iLetters, iData, aRegion, iFrom, out string aTrace);
                if (aAccount.Length == 0)
                    return SCP_CmdResult.Fail(1, $"✗ 查不到 `{iFrom}` 的帳號（{aTrace}）⇒ 未扣費、未寄出");
                var aDebit = new Dictionary<string, string>
                {
                    ["op"] = "debit",
                    ["bank_root"] = SCP_BankRegion.BankRootOfDataRoot(iData),
                    ["account"] = aAccount,
                    ["amount"] = aFee.ToString(CultureInfo.InvariantCulture),
                    ["kind"] = "registered_mail_fee",
                    ["ref"] = aRef,
                    ["description"] = $"掛號信郵資（寄給 @{iTo}）—— 本費用蒸發，不進央行",
                    ["caller"] = aAccount,
                };
                SCP_CmdResult aPaid = SCP_CmdRegistry.Dispatch("bank", aDebit);
                if (aPaid.ExitCode != 0)
                {
                    // ⚠ 印**全部**行：第一行常是「⤷ 由 senate server 執行」這種路由告示，真因在後面（實測踩過）。
                    var aFail = SCP_CmdResult.Fail(1, $"✗ 扣郵資沒有成功的收據（bank exit {aPaid.ExitCode}）⇒ 未寄出");
                    foreach (string l in aPaid.Lines) aFail.Lines.Add("  │ " + l);
                    return aFail;
                }
            }

            if (!SCP_RegisteredMail.Send(iLetters, iFrom, iTo, iSubject, iBody, aFee, aRef, aDeliver, out string aInbox, out string aErr))
                return SCP_CmdResult.Fail(1, $"✗ 已扣郵資 {aFee} token 但寫檔失敗：{aErr}",
                                          $"  請以此 ref 開請款單退回：{aRef}（`senate cmd bank-request`）");

            string aWhen = aDeliver.HasValue ? $"wake #{aDeliver}" : "下次醒來";
            var r = SCP_CmdResult.Success($"📮 掛號信已寄出：@{iFrom} → @{iTo}（投遞：{aWhen}）");
            r.Lines.Add($"   郵資 {aFee} token（蒸發，不進央行）" + (aAccount.Length > 0 ? $"／帳號 {aAccount}" : "（免費）")
                        + (aPolicyWhy != null ? $"　⚠ {aPolicyWhy}" : ""));
            r.Lines.Add($"   收件匣：{aInbox}");
            r.AddValue("fee", aFee.ToString(CultureInfo.InvariantCulture));
            r.AddValue("fee_ref", aRef);
            r.AddValue("mailbox_path", aInbox);
            return r;
        }
    }
}
