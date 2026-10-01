// 區塊職責：一次 free-time／free-time-activity 呼叫的**共用現場** —— 三個根、本趟設定、活動清單快取，
//          以及三條**跨 Cmd 的出口**（session 檔、券、酒館發文）。
// 物理意義：Unity 版這些東西散在 `UCL_AgentCommandsPath.DataRoot`（全域靜態）、`Cmd_FreeTime` 的 internal static、
//          `UCL_CanvasVoucherLedger` 的直讀。搬到 SCP_Core 之後根要由宿主傳進來（本層不推導），
//          而設定要「每趟讀一次、整趟傳遞」—— 兩件事都需要一個物件裝著。
// 數值影響：
//   · 券：**只走 `SCP_CmdRegistry.Dispatch("voucher", …)`**（Server 單一寫入端；券不記歷史，第二個寫入端蓋掉的東西回推不出來）。
//   · 發文：**只走 `Dispatch("tavern-post", …)`**（同一份 compose ＋ alter 配對延遲規則；⛔ 不自己組訊息）。
//   · 兩者都是 best-effort 的那一側由呼叫端決定（發文失敗不擋步驟；發券失敗擋 start 並回滾）。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Cmd;
using SCP.Core.Paths;
using SCP.Core.Session;

namespace SCP.Core.FreeTime
{
    /// <summary>一次酒館發文的結果 —— 四態，⛔ 不壓成「有沒有 seq」。</summary>
    public sealed class SCP_FreeTimePostResult
    {
        /// <summary>配到的 seq；0 ＝ 沒有（沒發／排程中／不知道）。</summary>
        public int Seq;
        /// <summary>exit 0 但 `scheduled=1`：alter 配對延遲，排進延後發文匣，**還沒配號**。</summary>
        public bool Scheduled;
        public string DeferredUntil = "";
        public int ExitCode;
        public string Reason = "";

        /// <summary>回傳檔那一行要說的話（四態各自一句）。</summary>
        public string Describe(string iSkipped = "未發（best-effort，不影響 session）")
        {
            if (Seq > 0) return $"seq **{Seq}**";
            if (Scheduled) return $"**已排程**（alter 配對延遲，到 `{DeferredUntil}` 才由 Server 發出 —— 還沒配號，⛔ 別補發）";
            if (ExitCode == 0) return "已送出但回傳沒有 `post_seq`（⚠ 不知道 seq —— 先 `tavern-query` 回讀再說）";
            if (ExitCode == 6) return $"{iSkipped}：exit 6 **確定沒發**（補發是安全的）—— {Reason}";
            if (ExitCode == 7) return $"⚠ exit 7 **不知道有沒有發** —— 先 `tavern-query` 回讀再補發（⛔ 直接補發可能重複）：{Reason}";
            return $"{iSkipped}：exit {ExitCode} —— {Reason}";
        }
    }

    /// <summary>某一批限時券的用量（`voucher op=usage`）。⚠ `Found=false` ＝「我不知道」，⛔ 不是「一張都沒用」。</summary>
    public sealed class SCP_FreeTimeVoucherUsage
    {
        /// <summary>那支 Cmd 本身跑成了沒（false ＝ 連問都沒問到）。</summary>
        public bool Queried;
        public bool Found;
        public int Granted, Remain, Alive, Used;
        public string Error = "";
    }

    public sealed class SCP_FreeTimeContext
    {
        public string DataRootRaw = "";
        public string LettersRootRaw = "";
        public string ProjectRoot = "";
        public SCP_DataRoot Data;
        public SCP_LettersRoot Letters;

        public SCP_FreeTimeSettings Settings = SCP_FreeTimeSettings.Defaults();
        public bool SettingsFileExists;
        public string? SettingsError;

        /// <summary>本趟累積的警告（活動掃描、券查詢失敗…）—— 呼叫端在回傳檔印出來，⛔ 不吞。</summary>
        public readonly List<string> Warnings = new List<string>();

        public SCP_FreeTimeContext(string iDataRoot, string iLettersRoot, string iProjectRoot)
        {
            DataRootRaw = iDataRoot.Replace('\\', '/').TrimEnd('/');
            LettersRootRaw = iLettersRoot.Replace('\\', '/').TrimEnd('/');
            ProjectRoot = iProjectRoot.Replace('\\', '/').TrimEnd('/');
            Data = new SCP_DataRoot(DataRootRaw);
            Letters = new SCP_LettersRoot(LettersRootRaw);
            // 設定讀**一次**、整趟傳遞（⛔ 不在半路重讀）。
            Settings = SCP_FreeTimeSettings.Read(DataRootRaw, out SettingsFileExists, out SettingsError);
        }

        public string SettingsLine() => Settings.Describe(SettingsFileExists, SettingsError);

        // ── 活動清單（一趟掃一次）─────────────────────────────────

        List<SCP_FreeTimeActivity>? m_Activities;
        public readonly List<string> ScanWarnings = new List<string>();

        /// <summary>兩層活動 md 的合併清單（含停用項）。一趟只掃一次 —— 同一趟裡兩次掃描之間有人改 md，骰面會自相矛盾。</summary>
        public List<SCP_FreeTimeActivity> Activities
            => m_Activities ??= SCP_FreeTimeCatalog.Scan(ProjectRoot, ScanWarnings);

        public SCP_FreeTimeActivity? FindActivity(string iId, bool iEnabledOnly)
        {
            foreach (var a in Activities)
            {
                if (iEnabledOnly && !a.Enabled) continue;
                if (string.Equals(a.Id, iId, StringComparison.OrdinalIgnoreCase)) return a;
            }
            return null;
        }

        // ── 回傳檔 ────────────────────────────────────────────────

        /// <summary>`letters/&lt;P&gt;/cmd/freetime_&lt;step&gt;.md`（版面只有一份實作：SCP_LettersPaths.CmdPayload）。</summary>
        public string PayloadPath(string iPersona, string iStep)
            => SCP_LettersPaths.CmdPayload(Letters, iPersona, "freetime", iStep);

        // ── session ───────────────────────────────────────────────

        public string SessionPath(string iPersona) => SCP_ActivitySessionStore.PathOf(Data, iPersona) ?? "";

        public SCP_FreeTimeSession? LoadSession(string iPersona)
            => SCP_ActivitySessionStore.Load<SCP_FreeTimeSession>(Data, iPersona, SCP_ActivitySessionKind.FreeTime);

        public bool SaveSession(string iPersona, SCP_FreeTimeSession iSession)
            => SCP_ActivitySessionStore.Save(Data, iPersona, iSession, SCP_ActivitySessionKind.FreeTime);

        /// <summary>收工（翻三欄走 store）。rounds 是自由時間專屬的，由這裡取出回報。⚠ 自由時間沒有金流結算 ⇒ base close。</summary>
        public bool CloseSession(string iPersona, SCP_FreeTimeSession ioSession, string iReason, out int oRounds)
        {
            oRounds = ioSession.rounds;
            return SCP_ActivitySessionStore.Close(Data, iPersona, ioSession, iReason);
        }

        /// <summary>
        /// 某 persona 此刻是否在自由時間中 —— active **且未過 end_ts**（判準委派 `IsRunningAt`，唯一一份）。
        /// ⚠ 只看 active 不夠：超時沒回來跑 next 的人會一直停在 active=true，把他讀成「在」等於叫人去 @ 一個早就下線的對手。
        /// </summary>
        public bool IsInFreeTime(string iPersona)
        {
            try
            {
                var aS = SCP_ActivitySessionStore.Load(Data, iPersona, SCP_ActivitySessionKind.FreeTime);
                return aS != null && aS.IsRunningAt(DateTime.Now, out _);
            }
            catch (Exception) { return false; }
        }

        // ── 券（只走 voucher Cmd）────────────────────────────────

        readonly Dictionary<string, (bool Ok, int Permanent, int Expiring, string Detail)> m_Balance
            = new Dictionary<string, (bool, int, int, string)>(StringComparer.Ordinal);

        /// <summary>
        /// 繪圖券餘額（`voucher op=balance`）。一趟同一個人只問一次（骰面上兩件繪圖活動不該打兩次 Server）。
        /// <para>⚠ Ok=false ＝「不知道」，⛔ 不是 0 張。</para>
        /// </summary>
        public (bool Ok, int Permanent, int Expiring, string Detail) CanvasBalance(string iPersona)
        {
            if (m_Balance.TryGetValue(iPersona, out var aHit)) return aHit;
            var aArgs = new Dictionary<string, string>
            {
                ["op"] = "balance",
                ["letters_root"] = LettersRootRaw,
                ["persona"] = iPersona,
                ["voucher"] = "canvas",
            };
            (bool, int, int, string) aOut;
            try
            {
                SCP_CmdResult aRes = SCP_CmdRegistry.Dispatch("voucher", aArgs);
                if (aRes.ExitCode != 0) aOut = (false, 0, 0, $"voucher exit {aRes.ExitCode}：{Reason(aRes)}");
                else if (!int.TryParse(Value(aRes, "permanent"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aPerm)
                         || !int.TryParse(Value(aRes, "expiring"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aExp))
                    aOut = (false, 0, 0, "voucher 成功但回傳沒有 permanent／expiring 那兩欄");
                else aOut = (true, aPerm, aExp, "");
            }
            catch (Exception e) { aOut = (false, 0, 0, "voucher 派遣例外：" + e.GetType().Name + ": " + e.Message); }
            m_Balance[iPersona] = aOut;
            return aOut;
        }

        /// <summary>
        /// 發一批綁本場的限時繪圖券（模型：`SenateChessGateway.GrantCanvasVoucher`）。
        /// <para>回 null ＝ 成功；非 null ＝ **券帳那一層的原文**（⛔ 不代它下結論 —— Server 有可能已發完才斷線）。</para>
        /// </summary>
        public string? GrantFreeTimeVouchers(string iPersona, string iSessionId, DateTime iUntilLocal, out string oExpiresIso)
        {
            oExpiresIso = iUntilLocal.AddMinutes(Settings.VoucherGraceMinutes).ToUniversalTime()
                              .ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";
            var aArgs = new Dictionary<string, string>
            {
                ["op"] = "grant",
                ["letters_root"] = LettersRootRaw,
                ["persona"] = iPersona,
                ["voucher"] = "canvas",
                ["amount"] = Settings.PixelsPerSession.ToString(CultureInfo.InvariantCulture),
                // ⚠ 券沒有歷史 ⇒ `region` 是唯一的「誰動過它」線索，寫入時必填。
                ["region"] = SCP.Core.Bank.SCP_BankRegion.Read(DataRootRaw, out string? _),
                ["source"] = "freetime",
                ["ref"] = iSessionId,
                ["expires_at"] = oExpiresIso,
            };
            try
            {
                SCP_CmdResult aRes = SCP_CmdRegistry.Dispatch("voucher", aArgs);
                if (aRes.ExitCode == 0) return null;
                string aFail = Value(aRes, "delegate_failure");
                return $"voucher exit {aRes.ExitCode}" + (aFail.Length > 0 ? $"（{aFail}）" : "") + "：" + string.Join(" ／ ", aRes.Lines);
            }
            catch (Exception e) { return "voucher 派遣例外：" + e.GetType().Name + ": " + e.Message; }
        }

        /// <summary>
        /// 本場那一批的用量（`voucher op=usage ref=<session_id>`）。
        /// <para>🩸 TASK-0195／0198：**查無 ≠ 用完**。`Found=false` 時三個數字一律 0，⛔ 呼叫端不准拿發放量去補一個數字。</para>
        /// </summary>
        public SCP_FreeTimeVoucherUsage CanvasUsage(string iPersona, string iRef)
        {
            var aOut = new SCP_FreeTimeVoucherUsage();
            var aArgs = new Dictionary<string, string>
            {
                ["op"] = "usage",
                ["letters_root"] = LettersRootRaw,
                ["persona"] = iPersona,
                ["voucher"] = "canvas",
                ["ref"] = iRef,
            };
            try
            {
                SCP_CmdResult aRes = SCP_CmdRegistry.Dispatch("voucher", aArgs);
                if (aRes.ExitCode != 0) { aOut.Error = $"voucher exit {aRes.ExitCode}：{Reason(aRes)}"; return aOut; }
                aOut.Queried = true;
                aOut.Found = Value(aRes, "found") == "1";
                if (aOut.Found)
                {
                    int.TryParse(Value(aRes, "granted"), NumberStyles.Integer, CultureInfo.InvariantCulture, out aOut.Granted);
                    int.TryParse(Value(aRes, "remain"), NumberStyles.Integer, CultureInfo.InvariantCulture, out aOut.Remain);
                    int.TryParse(Value(aRes, "alive"), NumberStyles.Integer, CultureInfo.InvariantCulture, out aOut.Alive);
                    int.TryParse(Value(aRes, "used"), NumberStyles.Integer, CultureInfo.InvariantCulture, out aOut.Used);
                }
            }
            catch (Exception e) { aOut.Error = "voucher 派遣例外：" + e.GetType().Name + ": " + e.Message; }
            return aOut;
        }

        // ── 酒館發文（只走 tavern-post Cmd）──────────────────────

        /// <summary>
        /// 自由時間的開場／換骰／收工／活動宣告。**best-effort**：失敗只回報，⛔ 不擋步驟、不丟例外。
        /// <para>🩸 2026-08-14（Unity 版，apex-one 讀 code 抓到）：這裡曾經是「讀不到 bank → return 0」，也就是**沒錢就沒聲音**
        /// —— 宣告安靜地不出現，同事只會以為「她這場沒發」。發言權與收款權是兩回事；本入口不碰 bank。</para>
        /// </summary>
        public SCP_FreeTimePostResult Post(string iPersona, string iBody, string iSubtag)
        {
            var aOut = new SCP_FreeTimePostResult();
            var aArgs = new Dictionary<string, string>
            {
                ["persona"] = iPersona,
                ["room"] = "tavern",
                ["body"] = iBody,
                ["meta"] = "{\"tag\":\"free-time\",\"subtag\":\"" + iSubtag + "\",\"category\":\"chat\"}",
            };
            try
            {
                SCP_CmdResult aRes = SCP_CmdRegistry.Dispatch("tavern-post", aArgs);
                aOut.ExitCode = aRes.ExitCode;
                if (aRes.ExitCode == 0)
                {
                    int.TryParse(Value(aRes, "post_seq"), NumberStyles.Integer, CultureInfo.InvariantCulture, out aOut.Seq);
                    aOut.Scheduled = Value(aRes, "scheduled") == "1";
                    aOut.DeferredUntil = Value(aRes, "deferred_until");
                }
                else aOut.Reason = Reason(aRes);
            }
            catch (Exception e) { aOut.ExitCode = 70; aOut.Reason = "tavern-post 派遣例外：" + e.GetType().Name + ": " + e.Message; }
            return aOut;
        }

        // ── 小工具 ────────────────────────────────────────────────

        public static string Value(SCP_CmdResult iR, string iKey)
        {
            foreach (var kv in iR.Values) if (kv.Key == iKey) return kv.Value;
            return "";
        }

        /// <summary>取失敗結果裡「哪一格不成立」那一行（優先 ✗ 開頭的行）。</summary>
        public static string Reason(SCP_CmdResult iR)
        {
            foreach (string aLine in iR.Lines) if (aLine.IndexOf('✗') >= 0) return aLine.Trim();
            foreach (string aLine in iR.Lines)
                if (!string.IsNullOrWhiteSpace(aLine) && !aLine.StartsWith("⤷", StringComparison.Ordinal)) return aLine.Trim();
            return "(沒有理由那一行)";
        }
    }
}
