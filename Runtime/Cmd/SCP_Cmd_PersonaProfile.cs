// 區塊職責：`senate cmd persona-profile` —— persona 設定的**寫入**入口：身分欄 set／unset、本區銀行綁定 get／set／unbind、導出綁定、換區重綁。
// 物理意義：本體在 `SCP_PersonaProfileWrite`（TASK-0354）。讀取（整份 persona）走 `senate cmd persona`。
// 數值影響：只寫 persona 檔（`profile/`、`bank/`）與 `AwakenInit/_persona_write_audit.jsonl`。⛔ 不動帳本、不動錢。
// ⭐ TASK-0361：這是 persona 檔**唯一**的寫入入口 —— 後台頁面（建 persona、email、actual_agent、Plurk 帳號）
//   也走 `SCP_PersonaProfileWrite`，⛔ 不另留一份寫入程式碼。
//
// ⚠ **刻意沒有** `refresh`／`rename_agent` 這兩個 op（寫在這裡，免得以為漏了）：
//   · `refresh`：只會重寫衍生快照 `_persona_profile_snapshot.json` —— Senate 不靠它。
//   · `rename_agent`：寫 agent 欄那條路一律擋；帳號合一之後要改名走銀行後台。⇒ 要用的那天另開單。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Bank;
using SCP.Core.Json;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_PersonaProfile : SCP_Cmd
    {
        public override string Name => "persona-profile";
        public override string Category => SCP_CmdCategory.Persona;

        public override string Summary =>
            "persona 設定寫入：身分欄 set／unset、本區銀行綁定 get_bank／set_bank／unbind、導出綁定 migrate_bank、換區重綁 rebind_region —— **本地跑**";

        public override string Details =>
            "create：`persona account fields actor reason`（建新 persona：先寫本區綁定、再逐欄寫身分欄；fields 是 JSON 物件，走 --arg-file；已存在 ⇒ 擋）。\n"
            + "set：`persona field value actor reason`（value 必須**在場**，清空欄位顯式給空值；結構欄 identity_vector／vector_history／fork_lineage 要合法 JSON 陣列，長 JSON 走 --arg-file）。\n"
            + "unset：`persona field actor reason`（刪掉 profile/<欄>.md；本來就沒有 ⇒ 零寫入）。\n"
            + "get_bank／set_bank `account`／unbind：`persona` 在本區（`currency` 沒給 ＝ 本專案區域）的綁定。\n"
            + "migrate_bank：`actor reason`，**預設 dry_run**；把全 pool 目前解析得到的帳號（本區沒有就是借別區的）寫成本區綁定；本區已有 ⇒ 跳過（overwrite=1 才覆寫）。\n"
            + "rebind_region：`from to actor reason`，**預設 dry_run**（dry_run=0 才寫）；只複製到新區，⛔ 不刪舊區、不改設定；新區已有不同值 ⇒ 衝突、exit 1。\n"
            + "set_lock_actual_agent：`persona value actor reason`（改在線者 lock 的 actual_agent，同一步改 profile/actual_agent）。\n"
            + "force_release_lock：`persona actor reason`（最後手段：強制刪 lock，⛔ 不寫信不廣播不關場 —— 能跑晚安就跑晚安）。\n"
            + "⚠ 只寫 persona 檔＋一行審計（`AwakenInit/_persona_write_audit.jsonl`）—— ⛔ 不動帳本、不動錢。讀整份 persona 走 `persona`。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("persona-profile --arg op=set --arg persona=Template --arg field=email --arg value=t@example.com --arg actor=summit --arg reason=驗收");

        static readonly string[] s_Ops = { "create", "set", "unset", "get_bank", "set_bank", "unbind", "migrate_bank", "rebind_region",
                                           "set_lock_actual_agent", "force_release_lock" };

        static readonly Dictionary<string, string[]> s_Required = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["create"] = new[] { "persona", "account", "fields", "actor", "reason" },
            ["set_lock_actual_agent"] = new[] { "persona", "value", "actor", "reason" },
            ["force_release_lock"] = new[] { "persona", "actor", "reason" },
            ["set"] = new[] { "persona", "field", "actor", "reason" },
            ["unset"] = new[] { "persona", "field", "actor", "reason" },
            ["get_bank"] = new[] { "persona" },
            ["set_bank"] = new[] { "persona", "account", "actor", "reason" },
            ["unbind"] = new[] { "persona", "actor", "reason" },
            ["migrate_bank"] = new[] { "actor", "reason" },
            ["rebind_region"] = new[] { "from", "to", "actor", "reason" },
        };

        static readonly Dictionary<string, string[]> s_Known = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["create"] = new[] { "persona", "account", "fields", "actor", "reason", "currency" },
            ["set_lock_actual_agent"] = new[] { "persona", "value", "actor", "reason" },
            ["force_release_lock"] = new[] { "persona", "actor", "reason" },
            ["set"] = new[] { "persona", "field", "value", "actor", "reason" },
            ["unset"] = new[] { "persona", "field", "actor", "reason" },
            ["get_bank"] = new[] { "persona", "currency" },
            ["set_bank"] = new[] { "persona", "account", "actor", "reason", "currency" },
            ["unbind"] = new[] { "persona", "actor", "reason", "currency" },
            ["migrate_bank"] = new[] { "actor", "reason", "currency", "dry_run", "overwrite" },
            ["rebind_region"] = new[] { "from", "to", "actor", "reason", "dry_run" },
        };

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑；letters 與審計都從這裡找）", iRequired: true),
            new SCP_CmdArgSpec("op", "做什麼", iRequired: true, iChoices: s_Ops),
            new SCP_CmdArgSpec("persona", "對象 persona", iDefault: ""),
            new SCP_CmdArgSpec("field", "set／unset：身分欄名", iDefault: ""),
            new SCP_CmdArgSpec("value", "set：值（必須在場；結構欄是 JSON 陣列，走 --arg-file）", iDefault: ""),
            new SCP_CmdArgSpec("account", "set_bank／create：帳號 id（⛔ 不是 persona 名）", iDefault: ""),
            new SCP_CmdArgSpec("fields", "create：身分欄的 JSON 物件（欄 → 值；結構欄是陣列）。走 --arg-file", iDefault: ""),
            new SCP_CmdArgSpec("currency", "區域 ID（沒給 ＝ 本專案 `Bank/bank_settings.json` 的區域）", iDefault: ""),
            new SCP_CmdArgSpec("from", "rebind_region：舊區 ID", iDefault: ""),
            new SCP_CmdArgSpec("to", "rebind_region：新區 ID", iDefault: ""),
            new SCP_CmdArgSpec("dry_run", "migrate_bank／rebind_region：0 ＝ 真的寫（預設 1：只算不寫）", iDefault: "1"),
            new SCP_CmdArgSpec("overwrite", "migrate_bank：1 ＝ 本區已有綁定也覆寫", iDefault: "0"),
            new SCP_CmdArgSpec("actor", "誰寫的（寫入 op 必填）", iDefault: ""),
            new SCP_CmdArgSpec("reason", "憑什麼（寫入 op 必填）", iDefault: ""),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aData = iArgs.Get("data_root").Trim();
            string aOp = iArgs.Get("op").Trim().ToLowerInvariant();
            if (!Directory.Exists(aData)) return SCP_CmdResult.Fail(1, "✗ 資料根不存在：" + aData);
            string aLetters = SCP_DataPaths.Letters(new SCP_DataRoot(aData)).Value;

            // ── 每個 op 的參數閘（打錯參數名 ⇒ 擋，⛔ 不靜默取預設值：BUG-14 那一族）──
            var aUnknown = new List<string>();
            foreach (var spec in ArgSpecs)
            {
                if (spec.Name == "data_root" || spec.Name == "op" || !iArgs.IsExplicit(spec.Name)) continue;
                if (Array.IndexOf(s_Known[aOp], spec.Name) < 0) aUnknown.Add(spec.Name);
            }
            if (aUnknown.Count > 0)
                return SCP_CmdResult.Fail(2, $"✗ op={aOp} 不吃這些參數：{string.Join(", ", aUnknown)}（⛔ 不靜默吃掉）",
                                          $"  op={aOp} 認得的：{string.Join(", ", s_Known[aOp])}");
            var aMissing = new List<string>();
            foreach (string k in s_Required[aOp]) if (iArgs.Get(k).Trim().Length == 0) aMissing.Add(k);
            if (aMissing.Count > 0) return SCP_CmdResult.Fail(2, $"✗ op={aOp} 缺必填：{string.Join(", ", aMissing)}");

            string aPersona = iArgs.Get("persona").Trim();
            string aActor = iArgs.Get("actor").Trim();
            string aReason = iArgs.Get("reason").Trim();
            var r = new SCP_CmdResult();

            if (aOp == "set") return OpSet(iArgs, aLetters, aData, aPersona, aActor, aReason, r);
            if (aOp == "set_lock_actual_agent")
            {
                if (!SCP_PersonaProfileWrite.SetLockActualAgent(aLetters, aData, aPersona, iArgs.Get("value"), aActor, aReason, out string aW, out string aE))
                    return SCP_CmdResult.Fail(1, "✗ set_lock_actual_agent 失敗：" + aE);
                if (aW.Length > 0) r.Lines.Add("⚠ " + aW);
                r.Lines.Add($"✅ {aPersona} 的 lock 與 profile/actual_agent ＝ `{iArgs.Get("value").Trim()}`（顯示 Agent／bank 不變）");
                return r.AddValue("actual_agent", iArgs.Get("value").Trim());
            }
            if (aOp == "force_release_lock")
            {
                if (!SCP_PersonaProfileWrite.ForceReleaseLock(aLetters, aData, aPersona, aActor, aReason, out bool aHad, out string aW, out string aE))
                    return SCP_CmdResult.Fail(1, "✗ force_release_lock 失敗：" + aE);
                if (aW.Length > 0) r.Lines.Add("⚠ " + aW);
                bool aStill = File.Exists(SCP_LettersPaths.SessionLockPath(new SCP_LettersRoot(aLetters), aPersona));
                if (aStill) return SCP_CmdResult.Fail(1, $"✗ 刪完回讀 lock 還在 —— 未生效");
                r.Lines.Add(aHad
                    ? $"✅ `{aPersona}` 的 lock 已強制刪除 —— ⚠ 沒寫信、沒廣播、沒關場（那些是晚安的事；能跑晚安就跑晚安）"
                    : $"・`{aPersona}` 本來就沒有 lock ⇒ 零寫入");
                return r.AddValue("had_lock", aHad ? "1" : "0");
            }
            if (aOp == "unset") return OpUnset(iArgs, aLetters, aData, aPersona, aActor, aReason, r);
            if (aOp == "rebind_region") return OpRebind(iArgs, aLetters, aData, aActor, aReason, r);

            // ── 銀行綁定三支：先定區域 ──
            string aCurrency = iArgs.Get("currency").Trim();
            if (aCurrency.Length == 0)
            {
                aCurrency = SCP_BankRegion.Read(aData, out string? aWhy);
                if (aWhy != null) r.Lines.Add("⚠ 區域讀的是預設值（" + aCurrency + "）：" + aWhy);
            }
            if (!SCP_BankRegion.IsValid(aCurrency)) return SCP_CmdResult.Fail(2, $"✗ 區域 ID 不合法（要能當檔名）：'{aCurrency}'");
            r.AddValue("currency", aCurrency);
            if (aOp == "migrate_bank") return OpMigrate(iArgs, aLetters, aData, aCurrency, aActor, aReason, r);
            if (aOp == "create") return OpCreate(iArgs, aLetters, aData, aPersona, aCurrency, aActor, aReason, r);

            string aBefore = SCP_PersonaProfile.GetBankAccount(aLetters, aPersona, aCurrency, out string aBeforeSrc, out string aBeforeNote);
            if (aOp == "get_bank")
            {
                r.Lines.Add($"{aPersona}@{aCurrency} = '{aBefore}'（source={aBeforeSrc}{(aBeforeNote.Length == 0 ? "" : "；" + aBeforeNote)}）");
                if (aBefore.Length > 0 && aBeforeSrc != aCurrency) r.Lines.Add("⚠ 這不是本區的宣告 —— 借用自 `" + aBeforeSrc + "`");
                r.AddValue("account", aBefore).AddValue("source", aBeforeSrc).AddValue("note", aBeforeNote);
                return r;
            }
            if (aOp == "set_bank")
            {
                string aAccount = iArgs.Get("account").Trim();
                if (!SCP_PersonaProfileWrite.WriteBankBinding(aLetters, aData, aPersona, aCurrency, aAccount, aActor, aReason, out string aWarn, out string aErr))
                    return SCP_CmdResult.Fail(1, "✗ set_bank 失敗：" + aErr);
                if (aWarn.Length > 0) r.Lines.Add("⚠ " + aWarn);
                // 印 ✓ 不算數，讀回來才算
                string aAfter = SCP_PersonaProfile.GetBankAccount(aLetters, aPersona, aCurrency, out string aAfterSrc, out _);
                if (aAfter != aAccount || aAfterSrc != aCurrency)
                    return SCP_CmdResult.Fail(1, $"✗ set_bank 寫入後讀回不符：期望 '{aAccount}'@{aCurrency}、實際 '{aAfter}'@{aAfterSrc}");
                r.Lines.Add($"✅ {aPersona}@{aCurrency}：'{aBefore}'（{aBeforeSrc}）→ '{aAfter}'（actor={aActor}）");
                r.AddValue("old_account", aBefore).AddValue("old_source", aBeforeSrc).AddValue("account", aAfter);
                return r;
            }
            // unbind
            bool aHadOwn = SCP_PersonaProfile.HasOwnBankBinding(aLetters, aPersona, aCurrency);
            if (!SCP_PersonaProfileWrite.DeleteBankBinding(aLetters, aData, aPersona, aCurrency, aActor, aReason, out string aDelWarn, out string aDelErr))
                return SCP_CmdResult.Fail(1, "✗ unbind 失敗：" + aDelErr);
            if (aDelWarn.Length > 0) r.Lines.Add("⚠ " + aDelWarn);
            if (SCP_PersonaProfile.HasOwnBankBinding(aLetters, aPersona, aCurrency))
                return SCP_CmdResult.Fail(1, $"✗ unbind 後 {aPersona}@{aCurrency} 仍有本區綁定 —— 未生效");
            string aNow = SCP_PersonaProfile.GetBankAccount(aLetters, aPersona, aCurrency, out string aNowSrc, out string aNowNote);
            r.Lines.Add($"✅ unbind {aPersona}@{aCurrency}：'{aBefore}'（{aBeforeSrc}）→ 現在 '{aNow}'（{aNowSrc}）"
                        + (aHadOwn ? "" : "　（本區本來就沒有自己的綁定 ⇒ 零寫入）"));
            r.AddValue("had_own", aHadOwn ? "1" : "0").AddValue("old_account", aBefore)
             .AddValue("now_account", aNow).AddValue("now_source", aNowSrc).AddValue("now_note", aNowNote);
            return r;
        }

        static string ReadFieldText(string iLetters, string iPersona, string iField)
        {
            string p = SCP_LettersPaths.ProfileDir(new SCP_LettersRoot(iLetters), iPersona) + "/" + iField + ".md";
            try { return File.Exists(p) ? File.ReadAllText(p).Trim() : ""; } catch (Exception) { return ""; }
        }

        static SCP_CmdResult OpSet(SCP_CmdArgs iArgs, string iLetters, string iData, string iPersona, string iActor, string iReason, SCP_CmdResult r)
        {
            // value 必須**在場**（沒給多半是參數名打錯；清空要顯式給空值）—— BUG-14
            if (!iArgs.IsExplicit("value"))
                return SCP_CmdResult.Fail(2, "✗ set 缺 value —— 參數名打錯？清空欄位請顯式給 --arg value=（空值）");
            string aField = iArgs.Get("field").Trim();
            string aValue = iArgs.Get("value");   // ⚠ 不 trim（純量欄字面收）
            string aOld = ReadFieldText(iLetters, iPersona, aField);
            if (!SCP_PersonaProfileWrite.SetField(iLetters, iData, iPersona, aField, aValue, iActor, iReason, out string aWarn, out string aErr))
                return SCP_CmdResult.Fail(1, "✗ set 失敗：" + aErr);
            if (aWarn.Length > 0) r.Lines.Add("⚠ " + aWarn);
            r.Lines.Add($"✅ set {iPersona}.{aField}：'{aOld}' → '{ReadFieldText(iLetters, iPersona, aField)}'（actor={iActor}）");
            r.AddValue("old_value", aOld).AddValue("new_value", aValue);
            return r;
        }

        static SCP_CmdResult OpUnset(SCP_CmdArgs iArgs, string iLetters, string iData, string iPersona, string iActor, string iReason, SCP_CmdResult r)
        {
            string aField = iArgs.Get("field").Trim();
            string aOld = ReadFieldText(iLetters, iPersona, aField);
            if (!SCP_PersonaProfileWrite.UnsetField(iLetters, iData, iPersona, aField, iActor, iReason, out bool aHad, out string aWarn, out string aErr))
                return SCP_CmdResult.Fail(1, "✗ unset 失敗：" + aErr);
            if (aWarn.Length > 0) r.Lines.Add("⚠ " + aWarn);
            // 讀回：來源必須不再是 profile
            var aRaw = SCP_PersonaProfile.GetRaw(iLetters, iPersona, SCP_BankRegion.Read(iData, out _));
            string aSrc = SCP_PersonaProfile.SrcAbsent;
            if (aRaw != null && aRaw.Contains(SCP_PersonaProfile.FieldSourcesKey)
                && aRaw[SCP_PersonaProfile.FieldSourcesKey].TryGetString(aField, out string s)) aSrc = s;
            if (aSrc == SCP_PersonaProfile.SrcProfile)
                return SCP_CmdResult.Fail(1, $"✗ unset 後 {iPersona}.{aField} 來源仍是 profile —— 未生效");
            r.Lines.Add($"✅ unset {iPersona}.{aField}：'{aOld}'（had_file={(aHad ? 1 : 0)}）→ 來源 {aSrc}（actor={iActor}）"
                        + (aHad ? "" : "　（本來就沒有 ⇒ 零寫入）"));
            r.AddValue("had_file", aHad ? "1" : "0").AddValue("old_value", aOld).AddValue("now_source", aSrc);
            return r;
        }

        static SCP_CmdResult OpCreate(SCP_CmdArgs iArgs, string iLetters, string iData, string iPersona, string iRegion,
                                      string iActor, string iReason, SCP_CmdResult r)
        {
            object? aParsed;
            try { aParsed = SCP_UclLegacyJson.Parse(iArgs.Get("fields").Trim()); }
            catch (Exception e) { return SCP_CmdResult.Fail(2, "✗ fields 不是合法 JSON：" + e.Message); }
            if (!(aParsed is SCP_UclLegacyObject aFields)) return SCP_CmdResult.Fail(2, "✗ fields 必須是 JSON **物件**（欄 → 值）");
            string aAccount = iArgs.Get("account").Trim();
            if (!SCP_PersonaProfileWrite.Create(iLetters, iData, iPersona, iRegion, aAccount, aFields, iActor, iReason,
                                                out List<string> aWarns, out string aErr))
                return SCP_CmdResult.Fail(1, "✗ create 失敗：" + aErr);
            foreach (string w in aWarns) r.Lines.Add("⚠ " + w);
            // 讀回：人存在、本區綁定是 account
            string aBack = SCP_PersonaProfile.GetBankAccount(iLetters, iPersona, iRegion, out string aSrc, out _);
            if (!SCP_PersonaProfile.Exists(iLetters, iPersona) || aBack != aAccount || aSrc != iRegion)
                return SCP_CmdResult.Fail(1, $"✗ create 寫入後讀回不符：exists={SCP_PersonaProfile.Exists(iLetters, iPersona)}／'{aBack}'@{aSrc}");
            r.Lines.Add($"✅ 建立 persona `{iPersona}` @ {aAccount}（{iRegion}）");
            r.AddValue("persona", iPersona).AddValue("account", aBack);
            return r;
        }

        static SCP_CmdResult OpMigrate(SCP_CmdArgs iArgs, string iLetters, string iData, string iRegion, string iActor, string iReason, SCP_CmdResult r)
        {
            bool aDry = iArgs.Get("dry_run").Trim() != "0";
            bool aOver = iArgs.Get("overwrite").Trim() == "1";
            var rep = SCP_PersonaProfileWrite.MigrateBank(iLetters, iData, iRegion, iActor, iReason, aDry, aOver);
            r.Lines.Add($"# 導出本區綁定 currency={iRegion} dry_run={(aDry ? 1 : 0)} overwrite={(aOver ? 1 : 0)} pool={rep.Pool}");
            foreach (string ln in rep.Lines) r.Lines.Add("  " + ln);
            r.AddValue("dry_run", aDry ? "1" : "0").AddValue("pool", rep.Pool.ToString())
             .AddValue("written", rep.Written.ToString()).AddValue("skipped_existing", rep.SkippedExisting.ToString())
             .AddValue("skipped_no_agent", rep.SkippedNoAgent.ToString()).AddValue("failed", rep.Failed.ToString());
            // 失敗不吞：批次的部分失敗最容易被讀成全部成功
            if (rep.Failed > 0) { r.ExitCode = 1; r.Lines.Add($"✗ 有 {rep.Failed} 筆失敗"); }
            return r;
        }

        static SCP_CmdResult OpRebind(SCP_CmdArgs iArgs, string iLetters, string iData, string iActor, string iReason, SCP_CmdResult r)
        {
            string aFrom = iArgs.Get("from").Trim(), aTo = iArgs.Get("to").Trim();
            bool aDry = iArgs.Get("dry_run").Trim() != "0";
            if (!SCP_BankRegion.IsValid(aFrom) || !SCP_BankRegion.IsValid(aTo))
                return SCP_CmdResult.Fail(2, $"✗ from／to 必須是合法區域 ID（能當檔名）：'{aFrom}' → '{aTo}'");
            var rep = SCP_PersonaProfileWrite.CopyRegionAll(iLetters, iData, aFrom, aTo, iActor, iReason, aDry);
            r.Lines.Add($"# 換區重綁 {aFrom} → {aTo}（dry_run={(aDry ? 1 : 0)}）—— 只複製到新區，⛔ 不刪舊區、不改設定");
            foreach (string ln in rep.Lines) r.Lines.Add("  " + ln);
            r.AddValue("from", aFrom).AddValue("to", aTo).AddValue("dry_run", aDry ? "1" : "0")
             .AddValue("copied", rep.Copied.ToString()).AddValue("skipped", rep.Skipped.ToString())
             .AddValue("conflicts", rep.Conflicts.ToString()).AddValue("failed", rep.Failed.ToString());
            if (rep.Conflicts > 0 || rep.Failed > 0)
            {
                r.ExitCode = 1;
                r.Lines.Add($"✗ 有 {rep.Conflicts} 筆衝突、{rep.Failed} 筆失敗（衝突＝新區已有不同綁定，本 op 不覆寫也不挑）");
            }
            return r;
        }
    }
}
