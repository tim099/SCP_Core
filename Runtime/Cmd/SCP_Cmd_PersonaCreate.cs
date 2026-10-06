// 區塊職責：`senate cmd persona-create` —— 建一位新 persona（可同時新開 agent／開戶），TASK-0428。
// 物理意義：**Cmd 是入口不是實作** —— 規劃與組裝在 `SCP_PersonaCreate`，身分欄寫入在 `SCP_PersonaProfileWrite.Create`（唯一寫入端），
//           開戶走 `bank`（Server 單一寫入端），開繪製單走 `task`，公告走 `tavern-post-system`。
//           早安沒帶 persona 時印的「建新的」就指向這支；agent 照 Morning 文件逐題問使用者，問到夠了才 create。
// 數值影響：op=draft **零寫入**；op=create 要 confirm=1。
//   寫入順序：(新 agent ⇒ 先開戶、再登記 agent_banks) → 身分欄 → 顏色 → 頭像（繪製單／交給 agent）→ 公告。
//   ⚠ 開戶在登記之前：先有帳戶，agent 才指得到它（反過來會出現一個指向不存在帳戶的 agent）。
//   ⚠ 後三步失敗只是警告：人已經建好了，⛔ 不因為公告沒發就說「建立失敗」（重跑會撞「已存在」）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_PersonaCreate : SCP_LocalRootsCmd
    {
        public override string Name => "persona-create";
        public override string Category => SCP_CmdCategory.Persona;

        public override string Summary =>
            "建立新 persona（可 fork、可同時新開 agent＝開戶）：draft 零寫入預覽／create confirm=1 才寫 —— 本地跑，不需要 Editor";

        public override string Details =>
            "必填只有 `persona`（＝參考角色，同時是 persona id）；其餘沒填的設定標「待本人填寫」，由那位 persona 第一次早安時自己補。\n"
            + "· 綁定的 agent：`agent=<agent_banks 的 key>`；不給 ⇒ 由 actual_agent 推一個**建議值**（預覽會印，請使用者確認）。\n"
            + $"· 新開 agent（＝銀行開戶）：`agent=<新名>` ＋ `new_agent_account=<帳號 id>`；種子預設 {SCP_PersonaCreate.DefaultAgentSeed} token（`seed` 可改）。\n"
            + "· fork：`fork_from=<既有 persona>` ⇒ 抄 identity_vector 與血統（鏈深 > 5 警告）。\n"
            + "· 角色設定：reference（作品／角色全名）、reference_scope（取多少，**問使用者**）、first_person／tone／personality／values／relations、layer_role、color。長文走 --arg-file。\n"
            + "· 頭像：`avatar=task`（預設，開一張繪製單給繪師）／`self`（建立的 agent 自己畫，規格印在回傳檔）／`skip`。\n"
            + "⛔ 不寫 email（之後另外設）、不寫憲法（每一條要附「我違反它的一次」，新人沒有）。";

        public override string Example =>
            SCP_CmdRegistry.InvokeOf<SCP_Cmd_PersonaCreate>("--arg persona=<參考角色> --arg agent=claude-code");

        // 下一步 draft／create 不同 ⇒ 由 Run 自己印（基底的 CliNextHint 是一句固定的話，分不出兩條路）。
        protected override string CliNextHint => "";

        static readonly (string Arg, string Section)[] s_SectionArgs =
        {
            ("first_person", "一人稱"), ("tone", "語氣／口癖"), ("personality", "性格"),
            ("values", "價值觀／底線"), ("relations", "與其他 persona 的關係"),
        };

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs
        {
            get
            {
                var a = new List<SCP_CmdArgSpec>
                {
                    new SCP_CmdArgSpec("op", "draft（預設，零寫入）／create", iDefault: "draft", iChoices: new[] { "draft", "create" }),
                    new SCP_CmdArgSpec("persona", "參考角色＝persona id（資料夾名）", iRequired: true),
                    new SCP_CmdArgSpec("project", "哪個專案（senate.local.json 的 projects[].name）。只有一個啟用專案時可省略"),
                    new SCP_CmdArgSpec("agent", "綁定的 agent（agent_banks 的 key）；新開 agent 時是新名字。不給 ⇒ 由 actual_agent 推建議值"),
                    new SCP_CmdArgSpec("new_agent_account", "給了 ⇒ 同時新開 agent：agent_banks[agent]＝這個帳號，並開戶"),
                    new SCP_CmdArgSpec("seed", $"新開 agent 的種子額度（預設 {SCP_PersonaCreate.DefaultAgentSeed}；0 ＝ 只開戶）", iDefault: SCP_PersonaCreate.DefaultAgentSeed.ToString(CultureInfo.InvariantCulture)),
                    new SCP_CmdArgSpec("fork_from", "fork 來源 persona（不給 ＝ 全新）"),
                    new SCP_CmdArgSpec("layer_role", "自介 Layer 行的一句話"),
                    new SCP_CmdArgSpec("reference", "參考來源（作品名／角色全名）"),
                    new SCP_CmdArgSpec("reference_scope", "參照作品角色時取多少（問使用者）"),
                    new SCP_CmdArgSpec("color", "主題色 #RRGGBB（頭像服裝主色）"),
                    new SCP_CmdArgSpec("avatar", "task（開繪製單）／self（自己畫）／skip", iDefault: "task", iChoices: new[] { "task", "self", "skip" }),
                    new SCP_CmdArgSpec("by", "建立者 persona（繪製單的開單人、開戶的簽名）；不給 ＝ 新 persona 本人"),
                    new SCP_CmdArgSpec("actual_agent", "推 agent 建議值用（不給 ＝ 偵測環境）"),
                    new SCP_CmdArgSpec("confirm", "op=create 要 1", iChoices: new[] { "0", "1" }),
                };
                foreach (var (arg, sec) in s_SectionArgs) a.Add(new SCP_CmdArgSpec(arg, "角色設定：" + sec));
                return a;
            }
        }

        protected override string? Run(SCP_MorningRoots iRoots, SCP_CmdArgs iArgs, SCP_CmdResult ioResult)
        {
            var aSpec = new SCP_PersonaCreateSpec
            {
                Persona = iArgs.Get("persona").Trim(),
                Agent = iArgs.Get("agent").Trim(),
                NewAgentAccount = iArgs.Get("new_agent_account").Trim(),
                ForkFrom = iArgs.Get("fork_from").Trim(),
                LayerRole = iArgs.Get("layer_role").Trim(),
                Reference = iArgs.Get("reference").Trim(),
                ReferenceScope = iArgs.Get("reference_scope").Trim(),
                Color = iArgs.Get("color").Trim(),
            };
            foreach (var (arg, sec) in s_SectionArgs) aSpec.Sections[sec] = iArgs.Get(arg);
            string aActual = iArgs.Get("actual_agent").Trim();
            if (aActual.Length == 0) aActual = Host!.DetectEnvMarker();
            if (!int.TryParse(iArgs.Get("seed").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aSeed) || aSeed < 0)
            { ioResult.ExitCode = 2; ioResult.Lines.Add("✗ seed 要是非負整數：'" + iArgs.Get("seed") + "'"); return null; }
            string aRegion = iRoots.Region;

            SCP_PersonaCreatePlan? aPlan = SCP_PersonaCreate.Plan(iRoots.LettersRoot, iRoots.DataRoot, aRegion, aSpec, aActual,
                new Random(), SCP_Morning.NowIso(), out string aErr);
            AppendAgentChoices(iRoots, ioResult);
            if (aPlan == null) { ioResult.ExitCode = 2; ioResult.Lines.Add("✗ " + aErr + "（零寫入）"); return null; }

            string aAvatar = iArgs.Get("avatar").Trim();
            if (aAvatar.Length == 0) aAvatar = "task";
            AppendPreview(aPlan, aAvatar, aSeed, ioResult);
            ioResult.AddValue("persona", aPlan.Persona);
            ioResult.AddValue("agent", aPlan.Agent);
            ioResult.AddValue("account", aPlan.Account);
            ioResult.AddValue("creates_agent", aPlan.CreatesAgent ? "1" : "0");
            ioResult.AddValue("pending", aPlan.Pending.Count.ToString(CultureInfo.InvariantCulture));

            string aOp = iArgs.Get("op").Trim();
            if (aOp != "create")
            {
                ioResult.AddValue("wrote", "0");
                ioResult.Lines.Add("⏸ **draft：沒有寫入任何東西**。");
                ioResult.Lines.Add("## next（照這行走）");
                ioResult.Lines.Add("   跟使用者確認上面每一格（特別是 agent／帳號）⇒ 同一行改 `--arg op=create --arg confirm=1`");
                return null;
            }
            if (iArgs.Get("confirm") != "1")
            {
                ioResult.ExitCode = 2;
                ioResult.AddValue("wrote", "0");
                ioResult.Lines.Add("✗ op=create 要帶 `--arg confirm=1`（零寫入）—— 先確定上面的預覽是使用者要的。");
                return null;
            }

            string aBy = iArgs.Get("by").Trim();
            if (aBy.Length == 0) aBy = aPlan.Persona;
            string aActor = $"persona-create:{aBy}";
            string aReason = aPlan.ForkFrom.Length > 0 ? $"建 persona（fork ← {aPlan.ForkFrom}）" : "建 persona";

            // ① 新 agent：先開戶、再登記
            if (aPlan.CreatesAgent)
            {
                var aOpen = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["op"] = "open", ["bank_root"] = iRoots.BankRoot, ["account"] = aPlan.Account, ["display_name"] = aPlan.Agent,
                    ["amount"] = aSeed.ToString(CultureInfo.InvariantCulture), ["caller"] = aActor,
                };
                if (aSeed > 0) aOpen["confirm"] = "1";
                SCP_CmdResult aBank = SCP_CmdRegistry.Dispatch("bank", aOpen);
                if (!aBank.Ok)
                {
                    ioResult.ExitCode = 1;
                    ioResult.Lines.Add($"✗ 開戶失敗（bank op=open exit {aBank.ExitCode}）—— agent 沒登記、persona 沒建：");
                    foreach (string l in aBank.Lines) ioResult.Lines.Add("    " + l);
                    return null;
                }
                ioResult.Lines.Add($"✓ 開戶：`{aPlan.Account}`（種子 {aSeed}）");
                if (!SCP_PersonaCreate.AddAgentBank(iRoots.DataRoot, aPlan.Agent, aPlan.Account, out string aRegErr))
                {
                    ioResult.ExitCode = 1;
                    ioResult.Lines.Add($"✗ 帳戶已開，但 agent_banks 登記失敗（{aRegErr}）—— persona 沒建。修好後重跑會撞「帳號已存在」：先手動補 agent_banks 一格 `{aPlan.Agent}`→`{aPlan.Account}`，再拿掉 new_agent_account 重跑。");
                    return null;
                }
                ioResult.Lines.Add($"✓ 登記 agent：`{aPlan.Agent}` → `{aPlan.Account}`");
            }

            // ② 身分欄（唯一寫入端）
            if (!SCP_PersonaProfileWrite.Create(iRoots.LettersRoot, iRoots.DataRoot, aPlan.Persona, aRegion, aPlan.Account,
                    aPlan.Fields, aActor, aReason, out List<string> aWarns, out string aCreateErr))
            {
                ioResult.ExitCode = 1;
                ioResult.Lines.Add("✗ 建立身分欄失敗：" + aCreateErr);
                return null;
            }
            foreach (string w in aWarns) if (w.Length > 0) ioResult.Lines.Add("⚠ " + w);
            bool aBack = SCP_PersonaProfile.Exists(iRoots.LettersRoot, aPlan.Persona);
            ioResult.Lines.Add($"✓ 建立 persona `{aPlan.Persona}`（讀回 exists={aBack}）");
            ioResult.AddValue("wrote", "1");

            // ③ 顏色
            if (aPlan.Color.Length > 0)
            {
                if (SCP_PersonaDisplay.TrySetColor(iRoots.LettersRoot, aPlan.Persona, aPlan.Color, out string? aColErr))
                    ioResult.Lines.Add($"✓ 主題色 {aPlan.Color}");
                else ioResult.Lines.Add($"⚠ 主題色沒寫成（{aColErr}）—— 人已建好；補：persona-display --arg op=color");
            }

            // ④ 頭像 ＋ 回傳檔
            string aSpec2 = SCP_PersonaCreate.AvatarSpec(aPlan.Persona, aPlan.Color, aPlan.Character);
            string aPayload = SCP_LettersPaths.CmdPayload(new SCP_LettersRoot(iRoots.LettersRoot), aPlan.Persona, "persona", "create");
            var aBody = new StringBuilder();
            aBody.AppendLine($"# persona-create {aPlan.Persona}  ts=`{SCP_Morning.NowLocal()}`");
            aBody.AppendLine();
            foreach (string l in ioResult.Lines) aBody.AppendLine(l);
            aBody.AppendLine();
            aBody.AppendLine(aSpec2);
            if (aAvatar == "task")
            {
                string aTask = OpenAvatarTask(iRoots, aPlan, aBy, aSpec2, ioResult);
                aBody.AppendLine(aTask.Length > 0 ? $"## 頭像\n- 已開繪製單 {aTask}（繪師照單畫；掛上後 `persona-display op=show` 讀回）" : "## 頭像\n- ⚠ 繪製單沒開成，見 CLI 輸出");
            }
            else if (aAvatar == "self")
            {
                ioResult.Lines.Add("🎨 頭像交給建立的 agent 自己畫 —— 規格在回傳檔；畫好照規格裡的「掛上」那一行。");
                aBody.AppendLine("## 頭像\n- 由建立的 agent 自己畫（照上面的規格）");
            }
            else ioResult.Lines.Add("· 頭像：skip（顯示端畫預設圖）");
            try { SCP_CmdPayload.Write(aPayload, aBody.ToString()); }
            catch (Exception e) { ioResult.Lines.Add("⚠ 回傳檔寫不出來：" + e.Message); aPayload = ""; }

            // ⑤ 公告
            var aPost = new StringBuilder();
            aPost.AppendLine("🌱 **新 persona 誕生**");
            aPost.AppendLine($"persona **{aPlan.Persona}**，綁定 agent **{aPlan.Agent}**（帳號 {aPlan.Account}）"
                + (aPlan.CreatesAgent ? $"——新開的 agent，種子 {aSeed} token" : "")
                + (aPlan.ForkFrom.Length > 0 ? $"；fork ← {aPlan.ForkFrom}（血統 {string.Join(" → ", aPlan.Lineage)} → {aPlan.Persona}）" : "") + "。");
            if (aPlan.Pending.Count > 0) aPost.AppendLine($"還沒填的設定（{string.Join("、", aPlan.Pending)}）由 {aPlan.Persona} 第一次醒來時自己補。");
            SCP_CmdResult aAnn = SCP_CmdRegistry.Dispatch("tavern-post-system", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["sender"] = "persona-admin", ["sender_name"] = "身分後台", ["target_data_root"] = iRoots.DataRoot,
                ["body"] = aPost.ToString(), ["tag"] = "persona-create",
            });
            ioResult.Lines.Add(aAnn.Ok
                ? "📣 已公告" + (Value(aAnn, "post_seq") is { Length: > 0 } s ? $"（seq {s}）" : "")
                : $"⚠ 公告沒發成（exit {aAnn.ExitCode}）—— 人已建好" + (aAnn.ExitCode == 7 ? "；⛔ exit 7＝不知道有沒有發，先回讀再補" : ""));
            ioResult.Lines.Add("## next（照這行走）");
            ioResult.Lines.Add("   " + SCP_CmdRegistry.InvokeOf<SCP_Cmd_MorningWake>($"--arg persona={aPlan.Persona} --arg actual_agent=<…> --arg model=<…>")
                + "（第一次醒來會被提示補完角色設定與自介）");
            return aPayload.Length > 0 ? aPayload : null;
        }

        /// <summary>
        /// 可綁定的 agent，每個附「底下現在有誰」（registry 的 `bank_personas`）—— 讓使用者知道新人會跟誰同帳。
        /// ⛔ 不用 system_accounts／closed_accounts 自動濾：那兩張表的鍵是帳號名、而且新舊混雜（`claude-code` 是舊帳號名，
        ///   不是 agent_banks 那個 claude-code→cc），濾錯的樣子是「該選的那個不見了」。標出來讓人看，比替人濾掉安全。
        /// </summary>
        static void AppendAgentChoices(SCP_MorningRoots iRoots, SCP_CmdResult ioResult)
        {
            var aBanks = SCP_PersonaCreate.AgentBanks(iRoots.DataRoot);
            if (aBanks.Count == 0) { ioResult.Lines.Add("· 可綁定的 agent：（讀不到 agent_banks）"); return; }
            SCP.Core.Json.SCP_JsonData aBp = SCP_Morning.LoadRegistryMeta(iRoots.DataRoot)["bank_personas"];
            ioResult.Lines.Add("· 可綁定的 agent（agent → 帳號〔底下現在有誰〕；或新開一個：agent＋new_agent_account）：");
            foreach (var kv in aBanks)
            {
                var aWho = new List<string>();
                if (aBp.IsObject && aBp[kv.Value].IsArray) foreach (SCP.Core.Json.SCP_JsonData x in aBp[kv.Value]) aWho.Add(x.AsString());
                string aList = aWho.Count == 0 ? "沒有人" : (aWho.Count <= 4 ? string.Join(", ", aWho) : string.Join(", ", aWho.GetRange(0, 4)) + $" …共 {aWho.Count} 位");
                ioResult.Lines.Add($"    - {kv.Key} → {kv.Value}〔{aList}〕");
            }
        }

        static void AppendPreview(SCP_PersonaCreatePlan p, string iAvatar, int iSeed, SCP_CmdResult ioResult)
        {
            var r = ioResult.Lines;
            r.Add($"## 預覽：`{p.Persona}`（區域 {p.Region}）");
            r.Add($"- agent：**{p.Agent}** → 帳號 **{p.Account}**　〔{p.AgentSource}〕"
                  + (p.CreatesAgent ? $"　⚠ 會新開戶、種子 {iSeed} token（憑空增發，簽名記在分錄）" : ""));
            r.Add(p.ForkFrom.Length > 0 ? $"- fork ← {p.ForkFrom}（血統 {string.Join(" → ", p.Lineage)} → {p.Persona}）" : "- 全新（不 fork）");
            r.Add($"- identity_vector：{p.Vector.Count} 維，hash {p.VectorHash}");
            r.Add($"- 主題色：{(p.Color.Length > 0 ? p.Color : "（待本人選）")}　頭像：{iAvatar}");
            r.Add(p.Pending.Count > 0 ? $"- 待本人填寫：{string.Join("、", p.Pending)}" : "- 設定都填了");
            foreach (string w in p.Warnings) r.Add("⚠ " + w);
            r.Add("- 角色設定檔（profile/character.md）：");
            foreach (string l in p.Character.Replace("\r\n", "\n").TrimEnd('\n').Split('\n')) r.Add("    " + l);
        }

        static string OpenAvatarTask(SCP_MorningRoots iRoots, SCP_PersonaCreatePlan p, string iBy, string iSpec, SCP_CmdResult ioResult)
        {
            string aCriteria =
                $"- [ ] 頭像掛上：`senate cmd persona-display --arg op=show --arg persona={p.Persona}` 讀回有頭像\n"
                + "- [ ] 1024×1024、畫風／背景／配色照描述裡的規格；參照範圍照角色設定\n";
            SCP_CmdResult aT = SCP_CmdRegistry.Dispatch("task", new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["op"] = "create", ["persona"] = iBy, ["data_root"] = iRoots.DataRoot,
                ["title"] = $"繪製 {p.Persona} 頭像（新 persona，TASK-0428 流程開的單）",
                ["type"] = "feature", ["priority"] = "normal", ["tags"] = "avatar,art",
                ["description"] = iSpec, ["criteria"] = aCriteria,
            });
            string aId = Value(aT, "index");
            if (aT.Ok && aId.Length > 0)
            {
                string aTid = "TASK-" + aId.PadLeft(4, '0');
                ioResult.Lines.Add($"🎨 已開繪製單 {aTid}（role=art 的人接）");
                ioResult.AddValue("avatar_task", aTid);
                return aTid;
            }
            ioResult.Lines.Add($"⚠ 繪製單沒開成（task exit {aT.ExitCode}）—— 人已建好；規格在回傳檔，手動開單時整段貼進描述");
            return "";
        }
    }
}
