// 區塊職責：`cmd persona` —— persona 身分欄的**唯讀出口**。**原生**，不需要 Unity Editor。
//
// 物理意義：解析本體早就在共用層（`SCP_PersonaProfile`），少的只是一個「Editor 沒開也叫得到」的嘴。
//           在這支之前，python 端要拿**現場值**只有一條路：發 Cmd 給 Editor（`_lib/persona_profile`
//           的第一段）。⇒ Editor 沒開就退快照／本地解析，而快照**可能是舊的**。
//
//   🩸 TASK-0082 的活體（2026-09-07，把 `profile/email.md` 改成探針值後三段各量一次）：
//       live → 探針值／local-parse → 探針值／**snapshot → 舊值**，
//       而三段回的 `source` 是同一個字串 ⇒ 拿舊快照組出來的 commit trailer
//       與拿現場值組出來的**完全同形**，落點是改不掉的 git history。
//   ⇒ 那個洞的成因不是快照壞了，是**「正確」與「貴」被綁在一起**：
//     要現場值就得付一趟 Editor 往返（實測 2.30s／次）並且 Editor 得開著，
//     於是那兩段可能給舊值的備援才有存在的理由。
//   ⇒ 本 Cmd 讓現場值變成**最便宜的那條**（senate.exe 本地跑，實測 0.37s／次、不需要 Editor）。
//
// 數值影響：**純唯讀**（`SCP_PersonaProfile` 自己就不寫任何檔）。不動 lock、不動帳、不寫快照。
//           查無此人回 exit 1 並印出它找過的目錄 —— 「這個人不存在」與「信件夾根設錯」是兩件事。
//
// ⚠ 本 Cmd **不推導 region**（與 `SCP_Cmd_WakeBrief` 同一條慣例）：真相源是宿主的央行設定，
//   本層多長一張讀它的嘴 = 第二個真相源。不給就把 `agent` 欄留缺席並明說沒人給 ——
//   ⛔ 不填預設值：兩個沒設定過的專案會印出同一個區域，而那正是這個定語要防的事。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。JSON 一律走 SCP_Json。
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Json;
using SCP.Core.Letters;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Persona : SCP_Cmd
    {
        public override string Name => "persona";

        public override string Summary => "persona 身分欄唯讀查詢（agent／actual_agent／model／email／狀態）—— **本地跑，不需要 Editor**";

        public override string Details =>
            "解析本體是 `SCP_PersonaProfile`（Unity 與 senate.exe 同一份），本 Cmd 只是它的 CLI 出口。\n"
            + "· 單一 persona：`--arg persona=<p>`；整個 pool：`--arg all=1`（兩者擇一）\n"
            + "· `--arg field=<欄名>` 只印那一欄的值（給腳本取用；查無該欄＝exit 4，**不印空字串**）\n"
            + "· `--arg json=1` 印完整 JSON；配 `all=1` 時的形狀是 `{personas, pool, generated_at}`\n"
            + "  ⚠ `json=1` 與 `field` 的 stdout **只有那個值**（警語不印，但 `warning_count` 照落）——\n"
            + "    消費端是程式，多一行中文就得多一條切它的規則，而那條規則會安靜地過期。\n"
            + "  📌 python 接縫（`_lib/persona_profile.py` 第一段）吃的就是 `all=1 json=1` 的 stdout。\n"
            + "⚠ 本 Cmd **純唯讀**：不動 lock、不動帳、不寫快照。\n"
            + "⚠ `region` 不給時 `agent`（帳號 id）欄會缺席 —— 那是**沒人告訴我區域**，不是「這人沒帳號」。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("persona --arg letters_root=D:/Unity/LY/AgentCommands/ChatTavern/baton/letters"
                                   + " --arg persona=Template --arg field=email");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("persona", "要讀誰（與 all 擇一）"),
            new SCP_CmdArgSpec("all", "=1 讀整個 pool（與 persona 擇一）。⚠ 每位都要掃信數與 lock，比單筆貴"),
            new SCP_CmdArgSpec("region", "現地的區域（貨幣）ID —— 決定 `agent` 讀哪一份 bank 綁定。不給＝該欄缺席，本 Cmd 不推導"),
            new SCP_CmdArgSpec("field", "只印這一欄的值（email／model／actual_agent／agent／status…）。查無該欄＝exit 4"),
            new SCP_CmdArgSpec("json", "=1 印完整 JSON（給程式讀；配 all=1 時形狀為 {personas, pool, generated_at}）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aPersona = iArgs.Get("persona").Trim();
            string aRegion = iArgs.Get("region").Trim();
            string aField = iArgs.Get("field").Trim();
            bool aAll = IsOn(iArgs.Get("all"));
            bool aJson = IsOn(iArgs.Get("json"));

            // 兩個選擇器同時給／都不給 —— 兩種都是「我不知道你要什麼」，而**猜一個是最糟的處置**：
            // 猜單筆會讓想掃全部的人拿到一筆就以為只有一個人，那不會報錯。
            if (aAll && aPersona.Length > 0)
                return SCP_CmdResult.Fail(2, "✗ persona 與 all 同時給了 —— 兩者擇一，我不猜");
            if (!aAll && aPersona.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ 要給 --arg persona=<誰> 或 --arg all=1");

            if (!Directory.Exists(aLettersRoot))
                return SCP_CmdResult.Fail(1, "✗ 信件夾根不存在：" + aLettersRoot);

            var aWarnings = new List<string>();
            Action<string> aWarn = m => aWarnings.Add(m);

            var aResult = new SCP_CmdResult();
            // ── 警語要不要進 stdout，取決於呼叫端是誰 ─────────────────────────────
            // `field` 模式的消費端是**腳本**（`$(senate cmd persona … --arg field=email)`）⇒
            // 多一行字就是把警語吃進那個變數裡。⛔ 但也不能安靜地全部丟掉
            // （「沒有輸出不是沒有問題，它是沒有讀數」）。
            // ⇒ 判準：**只講跟被問的那一欄有關的事**。region 只影響 `agent`，
            //   所以問 email 的人不該收到 region 的警語；問 agent 的人一定要收到。
            //   兩種情況 `warning_count` 都照落，⇒ 「被抑制」仍然數得出來。
            // ⚠ `json=1` 與 `field` 同一類：消費端是程式，stdout 必須只有它要的東西。
            //   python 接縫（`_lib/persona_profile.py` 第一段）就是拿 `json=1` 的 stdout
            //   去 `json.loads` —— 多一行中文警語，那邊就得多一條「怎麼把它切掉」的規則，
            //   而那條規則會在下一次有人加一行輸出時安靜地失效。
            bool aFieldMode = aField.Length > 0;
            bool aFieldNeedsRegion = string.Equals(aField, "agent", StringComparison.Ordinal);
            bool aTellRegion = (!aFieldMode && !aJson) || aFieldNeedsRegion;
            if (aRegion.Length == 0 && aTellRegion)
                aResult.Lines.Add("⚠ 沒給 region ⇒ `agent`（帳號 id）欄會缺席。"
                                  + "那是**沒人告訴我區域**，不是這個人沒有帳號。");

            return aAll
                ? ExecuteAll(aLettersRoot, aRegion, aJson, aResult, aWarn, aWarnings)
                : ExecuteOne(aLettersRoot, aPersona, aRegion, aField, aJson, aResult, aWarn, aWarnings);
        }

        // ===========================================================
        // 區塊職責：單一 persona。
        // 數值影響：`field` 給了就**只印那一欄**（stdout 乾淨，給腳本 `$(…)` 取用）。
        //   ⚠ 查無該欄回 exit 4 而不是印空字串 —— 空字串在呼叫端長得像「這欄是空的」，
        //     而那與「我拼錯欄名」處置相反（一個去設定，一個去改指令）。
        // ===========================================================
        static SCP_CmdResult ExecuteOne(string iLettersRoot, string iPersona, string iRegion,
                                        string iField, bool iJson, SCP_CmdResult ioResult,
                                        Action<string> iWarn, List<string> iWarnings)
        {
            SCP_JsonData? aRaw = SCP_PersonaProfile.GetRaw(iLettersRoot, iPersona, iRegion, iWarn);
            if (aRaw == null)
                return SCP_CmdResult.Fail(1,
                    "✗ 查無此 persona：" + iPersona,
                    "  找過：" + Path.Combine(iLettersRoot, iPersona),
                    "  ⚠ 「這個人不存在」與「信件夾根設錯」是兩件事 —— 上面那行是我真的去看的路徑");

            if (iField.Length > 0)
            {
                if (!aRaw.Contains(iField))
                {
                    // 🩸 第三態：`agent` 缺席**而且沒人給 region** —— 那不是「這人沒有帳號」，
                    //   是我根本沒被告知要讀哪一區的綁定。這兩件事的處置相反
                    //   （去補參數 vs 去替他綁帳號），而在「沒有這一欄」這句話底下它們同形。
                    if (string.Equals(iField, "agent", StringComparison.Ordinal) && iRegion.Length == 0)
                        return SCP_CmdResult.Fail(4,
                            "✗ `agent` 缺席，而**你沒給 region** —— 這不是「" + iPersona + " 沒有帳號」",
                            "  `agent`（帳號 id）讀的是 bank/<region>.md，沒有 region 就沒有要讀的檔",
                            "  ⇒ 補 --arg region=<區域 ID>（真相源：宿主的 Treasury/bank_settings.json 的 currency_id）");
                    return SCP_CmdResult.Fail(4,
                        "✗ " + iPersona + " 沒有 `" + iField + "` 這一欄",
                        "  ⚠ 這是**查無該欄**，不是「那一欄是空的」—— 前者要改指令，後者要去設定",
                        "  有哪些欄：" + string.Join(" / ", aRaw.Keys));
                }
                ioResult.Lines.Add(Str(aRaw[iField]));
                ioResult.AddValue("field", iField);
                // ⚠ 只有問 `agent` 時才把警語印進 stdout —— 其餘欄位的呼叫端是腳本，
                //   多一行就是把警語吃進 `$(…)`。被抑制的仍然數得出來（`warning_count`）。
                AppendWarnings(ioResult, iWarnings,
                               iPrint: string.Equals(iField, "agent", StringComparison.Ordinal));
                return ioResult;
            }

            if (iJson) ioResult.Lines.Add(aRaw.ToJson(true));
            else foreach (string aLine in Human(iPersona, aRaw)) ioResult.Lines.Add(aLine);

            ioResult.AddValue("persona", iPersona);
            ioResult.AddValue("email", aRaw.GetString("email", ""));
            ioResult.AddValue("actual_agent", aRaw.GetString("actual_agent", ""));
            AppendWarnings(ioResult, iWarnings, iPrint: !iJson);
            return ioResult;
        }

        // ===========================================================
        // 區塊職責：整個 pool，形狀刻意對齊 python 接縫吃的那份快照（`{personas, pool, generated_at}`）。
        // 物理意義：那不是為了好看 —— **step 3 要把 python 的第一段從「發 Cmd 給 Editor」改成「叫這支」，
        //   而形狀一樣就不需要在中間再長一層轉譯**（多一層轉譯 = 多一個會漂的地方）。
        // 數值影響：每位 persona 都要數信、讀 lock、掃 longterm ⇒ 比單筆貴。**一次拿全部**仍然
        //   遠比「每位一次」便宜（那正是 BUG-17 那一族的形狀）。
        // ⚠ `generated_at` 照填，但語意與快照檔那個欄位**不同**：這裡是「這一趟讀的時刻」，
        //   而快照那個是「那份檔被生出來的時刻」。同名不同義的欄位要在文件裡講明，
        //   否則下游會拿它去判斷新舊 —— 而它永遠是「剛剛」。
        // ===========================================================
        static SCP_CmdResult ExecuteAll(string iLettersRoot, string iRegion, bool iJson,
                                        SCP_CmdResult ioResult, Action<string> iWarn,
                                        List<string> iWarnings)
        {
            List<string> aPool = SCP_PersonaProfile.PoolNames(iLettersRoot, iWarn);
            var aPersonas = SCP_JsonData.NewObject();
            int aMissed = 0;
            foreach (string aName in aPool)
            {
                SCP_JsonData? aRaw = SCP_PersonaProfile.GetRaw(iLettersRoot, aName, iRegion, iWarn);
                // 掃得到名字卻讀不出內容 ⇒ 計數並印出來。安靜跳過會讓 pool 與 personas 對不上，
                // 而那個差額**沒有任何一層在看**。
                if (aRaw == null) { aMissed++; iWarn("[persona] pool 有 " + aName + " 但讀不出內容"); continue; }
                aPersonas.Set(aName, aRaw);
            }

            var aOut = SCP_JsonData.NewObject();
            aOut.Set("personas", aPersonas);
            var aPoolArr = SCP_JsonData.NewArray();
            foreach (string aName in aPool) aPoolArr.Add(SCP_JsonData.NewString(aName));
            aOut.Set("pool", aPoolArr);
            aOut.Set("generated_at", SCP_JsonData.NewString(UtcNowIso()));

            if (iJson) ioResult.Lines.Add(aOut.ToJson(true));
            else
            {
                ioResult.Lines.Add("# persona pool（" + aPool.Count + " 位）");
                foreach (string aName in aPool)
                    ioResult.Lines.Add("  " + aName.PadRight(16)
                                       + (aPersonas.Contains(aName)
                                          ? aPersonas[aName].GetString("email", "(缺席)")
                                          : "(讀不出內容)"));
            }
            ioResult.AddValue("pool_count", aPool.Count.ToString());
            ioResult.AddValue("resolved_count", (aPool.Count - aMissed).ToString());
            // 0 也照印 —— 「沒有漏」與「我沒在數」在輸出上不可同形。
            ioResult.AddValue("missed_count", aMissed.ToString());
            AppendWarnings(ioResult, iWarnings, iPrint: !iJson);
            return ioResult;
        }

        static IEnumerable<string> Human(string iPersona, SCP_JsonData iRaw)
        {
            yield return "# persona " + iPersona;
            foreach (string aKey in new[] { "agent", "actual_agent", "model", "email",
                                            "plurk_account", "status", "wake_count", "layer_role" })
                // 缺席就寫「缺席」，⛔ 不印空值 —— 空白會被讀成「這欄是空的」，
                // 而「沒有這一欄」與「這一欄是空的」的處置不同（去設定 vs 去查為什麼沒被寫）。
                yield return "  " + aKey.PadRight(14)
                             + (iRaw.Contains(aKey) ? Str(iRaw[aKey]) : "(缺席)");
        }

        /// <summary>
        /// 任何型別安全轉字串 —— 字串取字面，其餘（數字／布林／陣列／物件）取 compact JSON。
        /// <para>⚠ 不能一律 <c>AsString()</c>：`wake_count` 是數字，那條路會丟
        /// <c>SCP_JsonTypeException</c>，而它炸的位置在**印給人看的那一行**
        /// —— 一個只在「剛好印到那一欄」時才炸的錯，是最難重現的那種。</para>
        /// </summary>
        static string Str(SCP_JsonData iVal)
            => iVal.Type == SCP_JsonType.String ? iVal.AsString() : iVal.ToString();

        /// <summary>
        /// 警語落點。<paramref name="iPrint"/>＝false 時**不進 stdout，但仍落 `warning_count`**
        /// —— 抑制的是版面，不是讀數。
        /// </summary>
        static void AppendWarnings(SCP_CmdResult ioResult, List<string> iWarnings, bool iPrint = true)
        {
            if (iPrint) foreach (string aW in iWarnings) ioResult.Lines.Add("⚠ " + aW);
            ioResult.AddValue("warning_count", iWarnings.Count.ToString());
            if (!iPrint && iWarnings.Count > 0)
                // 數字給機器，這一格給人：不然「有 3 筆警語但沒印」只有讀 values 的人看得到。
                ioResult.AddValue("warnings_suppressed", "field 模式只印被問的那一欄");
        }

        /// <summary>旗標判定：`1` / `true` / `yes` 都算開；⛔ 空字串不算（沒給 ≠ 給了 false）。</summary>
        static bool IsOn(string iValue)
        {
            string v = (iValue ?? "").Trim();
            return v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
        }

        static string UtcNowIso()
            => DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
                                        System.Globalization.CultureInfo.InvariantCulture);
    }
}
