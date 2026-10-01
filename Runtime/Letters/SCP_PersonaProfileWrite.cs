// 區塊職責：persona 設定的**寫入**本體 —— 身分欄 set／unset、本區銀行綁定 set／unbind、換區重綁（複製）、寫入審計。
// 物理意義：TASK-0354。移植自 UCL `UCL_PersonaProfile` 的寫入那半（2026-10-01），讓 `senate cmd persona-profile`
//           不需要 Unity Editor。讀取那半早就在 `SCP_PersonaProfile`（本檔只寫，讀回驗證走它）。
//           ⭐ 寫出來的檔與 Editor 版**逐位元組同形**：純量欄 `值\n`、結構欄 UCL beautify（`\r\n`＋`\uXXXX`）、
//           綁定檔 `帳號\n`、審計一行 UCL 緊湊 JSON（`\uXXXX`）。
// ⭐ TASK-0361（Tim 2026-10-01「寫入端整合到 Senate，Unity 端不留」）：**persona 檔的唯一寫入端**。
//   CLI（`persona-profile`）、Senate 銀行後台換綁、早安寫 model／actual_agent、Unity 頁面（經 CLI）全部走這裡；
//   稽核只有 `_persona_write_audit.jsonl` 一份（原本銀行後台另寫 `bank/_audit.log`，已收掉）。
// 數值影響：只寫 `letters/<p>/profile/<field>.md`、`letters/<p>/bank/<region>.md` 與 `AwakenInit/_persona_write_audit.jsonl`。
//           ⛔ **不碰帳本、不動任何一分錢、不改央行設定**（綁定只決定「之後的收付進哪一戶」，既有分錄不追溯）。
//
// ⚠ 與 Editor 版刻意不同的格（都寫在這裡，免得以為是漏移植）：
//   ① 不刷新 `_persona_profile_snapshot.json`：衍生快照，SCP_Morning 寫 profile 時已經做過同一個判斷（它的檔頭 ⑤）。
//   ② 綁定寫入要求 persona 存在（Editor 版不查 ⇒ 打錯名字會長出 `letters/<typo>/bank/`）。
//   ③ 寫檔走 tmp＋Replace（Editor 版是 Delete＋Move，中間有一格「檔案不存在」）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    public static class SCP_PersonaProfileWrite
    {
        public static string AuditPath(string iDataRoot) => Path.Combine(iDataRoot, "AwakenInit", "_persona_write_audit.jsonl");

        static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        static string ProfileFieldPath(string iLettersRoot, string iPersona, string iField)
            => SCP_LettersPaths.ProfileDir(new SCP_LettersRoot(iLettersRoot), iPersona) + "/" + iField + ".md";

        static string BankFieldPath(string iLettersRoot, string iPersona, string iRegion)
            => SCP_LettersPaths.PersonaDir(new SCP_LettersRoot(iLettersRoot), iPersona) + "/bank/" + iRegion + ".md";

        static bool IsStructured(string iField) => Array.IndexOf(SCP_PersonaProfile.StructuredFieldsOrder, iField) >= 0;

        static bool NeedActorReason(string iActor, string iReason, out string oError)
        {
            oError = "";
            if (!string.IsNullOrWhiteSpace(iActor) && !string.IsNullOrWhiteSpace(iReason)) return true;
            oError = "actor 與 reason 必填（§8.6）—— 寫入要能回答「是誰、憑什麼」；匿名寫入不收";
            return false;
        }

        /// <summary>原子寫（tmp＋Replace，UTF-8 無 BOM）。</summary>
        static void WriteAtomic(string iPath, string iText)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iPath)!);
            string aTmp = iPath + ".tmp";
            File.WriteAllText(aTmp, iText, new UTF8Encoding(false));
            SCP.Core.Io.SCP_TextFile.ReplaceOrMove(aTmp, iPath);
        }

        // ── 審計 ─────────────────────────────────────────────────
        /// <summary>
        /// append 一行審計（UCL 緊湊 JSON，key 順序 ts, persona, fields, actor, reason）。
        /// 回 "" ＝ 成功；否則是錯誤訊息 —— ⚠ 審計失敗**不擋主寫入**（資料已落地），但呼叫端要印出來。
        /// </summary>
        public static string AppendAudit(string iDataRoot, string iPersona, string iFields, string iActor, string iReason)
        {
            try
            {
                var o = new SCP_UclLegacyObject();
                o.Set("ts", NowIso());
                o.Set("persona", iPersona);
                o.Set("fields", iFields);
                o.Set("actor", iActor);
                o.Set("reason", iReason);
                string aPath = AuditPath(iDataRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
                File.AppendAllText(aPath, SCP_UclLegacyJson.ToJson(o) + "\n", new UTF8Encoding(false));
                return "";
            }
            catch (Exception e) { return "審計 append 失敗（主寫入不受影響）：" + e.Message; }
        }

        // ── 身分欄 ───────────────────────────────────────────────
        /// <summary>
        /// 結構欄的值 parse ＋ 形狀檢查（陣列；identity_vector＝數字、vector_history＝物件、fork_lineage＝字串）。
        /// ⛔ parse 失敗**不退存字串**（那會變成一個長得像陣列的字串，讀回來才炸）。
        /// </summary>
        public static bool ParseStructured(string iField, string? iValue, out List<object?>? oList, out string oError)
        {
            oList = null; oError = "";
            string aText = (iValue ?? "").Trim();
            if (aText.Length == 0) { oError = iField + " 是結構欄，值不能是空的 —— 空陣列請顯式給 `[]`（空字串與空陣列是兩件事，不猜）"; return false; }
            object? v;
            try { v = SCP_UclLegacyJson.Parse(aText); }
            catch (Exception e) { oError = iField + " 是結構欄，值必須是合法 JSON —— parse 失敗：" + e.Message + "（不會退存成字串）"; return false; }
            if (!(v is List<object?> aList)) { oError = iField + " 是結構欄，值必須是 JSON **陣列**（收到的不是陣列）"; return false; }
            for (int i = 0; i < aList.Count; i++)
            {
                object? e = aList[i];
                bool ok; string want;
                switch (iField)
                {
                    case "vector_history": ok = e is SCP_UclLegacyObject; want = "物件"; break;
                    case "fork_lineage": ok = e is string; want = "字串"; break;
                    default: ok = e is int || e is long || e is double; want = "數字"; break;
                }
                if (!ok) { oError = $"{iField} 的第 {i} 個元素不是{want}（本欄要求：陣列的每個元素都是{want}）"; return false; }
            }
            oList = aList;
            return true;
        }

        /// <summary>
        /// 寫一個身分欄（`profile/&lt;field&gt;.md`）＋ 審計一行。
        /// 純量欄一律字面收（長得像 JSON 也不猜）；結構欄必須是形狀相符的 JSON 陣列。
        /// <para>⛔ `agent` 不由這裡寫（走 set_bank）；其餘非身分欄是推導欄，一律擋。</para>
        /// </summary>
        public static bool SetField(string iLettersRoot, string iDataRoot, string iPersona, string iField, string iValue,
                                    string iActor, string iReason, out string oAuditWarn, out string oError)
        {
            oAuditWarn = ""; oError = "";
            if (string.IsNullOrWhiteSpace(iField)) { oError = "field 必填"; return false; }
            if (!SCP_PersonaProfile.Exists(iLettersRoot, iPersona))
            { oError = $"查無此 persona（`letters/{iPersona}/profile/` 不存在）：{iPersona}"; return false; }
            if (!SCP_PersonaProfile.IsIdentityField(iField))
            {
                oError = iField == "agent"
                    ? "`agent`（＝帳號 id）不由本入口寫 —— 走 `op=set_bank`（一區一檔的綁定，有自己的審計與跨區借用判準）。"
                    : $"`{iField}` 是推導欄（真相源在 wakes/ 信件數、lock、longterm/ 檔名），不接受寫入 —— 要改就去改那個既成事實。";
                return false;
            }
            if (!NeedActorReason(iActor, iReason, out oError)) return false;

            string aBody;
            if (IsStructured(iField))
            {
                if (!ParseStructured(iField, iValue, out List<object?>? aList, out oError)) return false;
                aBody = SCP_UclLegacyJson.ToJsonBeautify(aList);
            }
            // 純量欄字面收，⚠ 但去掉**結尾的**換行：`--arg-file` 讀進來的值帶著檔尾 `\n`，而一檔一值的欄位
            //   不可能想要它（🩸 2026-10-01 實測：照字面收會寫成 `email\n\n`）。中間與前導的字元一律不動。
            else aBody = (iValue ?? "").TrimEnd('\r', '\n');

            try { WriteAtomic(ProfileFieldPath(iLettersRoot, iPersona, iField), aBody + "\n"); }
            catch (Exception e) { oError = e.Message; return false; }
            oAuditWarn = AppendAudit(iDataRoot, iPersona, "profile/" + iField, iActor, iReason);
            return true;
        }

        /// <summary>
        /// **建一個新 persona**：建 `profile/`、寫本區綁定、逐欄寫身分欄（每欄一行審計）、最後一行總結審計。
        /// <para>移植自 Editor 版身分後台的「建 persona」（`WriteBankAccount` ＋ `WriteRaw`，TASK-0361）。
        /// 總結那一行的形狀照 `WriteRaw`：`profile:[a,b] skipped(推導欄):[..] refused(走 set_bank):[agent]`。</para>
        /// <para>⚠ 順序刻意先綁定再寫身分欄：先有帳號歸屬，錢才不會在半成品狀態落央行。</para>
        /// </summary>
        /// <param name="iFields">JSON 物件：身分欄 → 值（結構欄是陣列；`forked_from`／`forked_at` 可為 null）。</param>
        public static bool Create(string iLettersRoot, string iDataRoot, string iPersona, string iRegion, string iAccount,
                                  SCP_UclLegacyObject iFields, string iActor, string iReason,
                                  out List<string> oWarnings, out string oError)
        {
            oWarnings = new List<string>(); oError = "";
            string p = (iPersona ?? "").Trim();
            if (p.Length == 0 || p.StartsWith("_", StringComparison.Ordinal) || p.StartsWith(".", StringComparison.Ordinal)
                || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || p.Contains(".."))
            { oError = $"persona 名不合法（不能空白、不能 `_`／`.` 開頭、要能當資料夾名）：'{iPersona}'"; return false; }
            if (SCP_PersonaProfile.Exists(iLettersRoot, p)) { oError = $"`{p}` 已經存在 —— 建人不覆寫既有的人（改欄位走 op=set）"; return false; }
            if (!NeedActorReason(iActor, iReason, out oError)) return false;
            if (string.IsNullOrWhiteSpace(iAccount)) { oError = "account 必填 —— 建人要先有本區帳號歸屬"; return false; }

            // 先驗完整份 payload（⛔ 寫到一半才發現第三欄壞掉＝留下半個人）
            var aBodies = new List<KeyValuePair<string, string>>();
            var aSkipped = new List<string>();
            var aRefused = new List<string>();
            foreach (string f in SCP_PersonaProfile.IdentityFields)
            {
                int i = iFields.FindIndex(kv => kv.Key == f);
                if (i < 0) continue;
                object? v = iFields[i].Value;
                string aBody;
                if (IsStructured(f))
                {
                    if (v == null) aBody = "null";
                    else
                    {
                        if (!ParseStructured(f, SCP_UclLegacyJson.ToJson(v), out List<object?>? aList, out oError)) return false;
                        aBody = SCP_UclLegacyJson.ToJsonBeautify(aList);
                    }
                }
                else aBody = v == null ? "" : (v is string s ? s.TrimEnd('\r', '\n') : SCP_UclLegacyJson.ToJson(v));
                aBodies.Add(new KeyValuePair<string, string>(f, aBody));
            }
            foreach (var kv in iFields)
            {
                if (SCP_PersonaProfile.IsIdentityField(kv.Key) || kv.Key == SCP_PersonaProfile.FieldSourcesKey) continue;
                if (kv.Key == "agent") aRefused.Add(kv.Key); else aSkipped.Add(kv.Key);
            }

            try { Directory.CreateDirectory(SCP_LettersPaths.ProfileDir(new SCP_LettersRoot(iLettersRoot), p)); }
            catch (Exception e) { oError = "建不出 profile/：" + e.Message; return false; }
            if (!WriteBankBinding(iLettersRoot, iDataRoot, p, iRegion, iAccount, iActor, iReason, out string aWarn, out oError))
            { oError = "本區綁定寫入失敗（" + iRegion + "）：" + oError; return false; }
            if (aWarn.Length > 0) oWarnings.Add(aWarn);

            var aWritten = new List<string>();
            foreach (var kv in aBodies)
            {
                try { WriteAtomic(ProfileFieldPath(iLettersRoot, p, kv.Key), kv.Value + "\n"); }
                catch (Exception e) { oError = $"profile/{kv.Key} 寫入失敗：{e.Message}"; return false; }
                string w = AppendAudit(iDataRoot, p, "profile/" + kv.Key, iActor, iReason);
                if (w.Length > 0) oWarnings.Add(w);
                aWritten.Add(kv.Key);
            }
            string aSum = AppendAudit(iDataRoot, p, "profile:[" + string.Join(",", aWritten) + "]"
                + (aSkipped.Count > 0 ? " skipped(推導欄):[" + string.Join(",", aSkipped) + "]" : "")
                + (aRefused.Count > 0 ? " refused(走 set_bank):[" + string.Join(",", aRefused) + "]" : ""), iActor, iReason);
            if (aSum.Length > 0) oWarnings.Add(aSum);
            if (aRefused.Count > 0) oWarnings.Add("以下欄位**未寫入**（帳號欄走 account／set_bank）：" + string.Join(",", aRefused));
            if (aSkipped.Count > 0) oWarnings.Add("推導欄不儲存（真相源在 wakes/、lock、longterm/）：" + string.Join(",", aSkipped));
            return true;
        }

        /// <summary>
        /// 刪掉一個 `profile/&lt;field&gt;.md`（set 的逆操作）。檔本來就不存在 ⇒ 回 true、<paramref name="oHadFile"/>=false、**零寫入**
        /// （不審計 —— 沒有發生的事不留一筆看起來發生過的帳）。
        /// </summary>
        public static bool UnsetField(string iLettersRoot, string iDataRoot, string iPersona, string iField,
                                      string iActor, string iReason, out bool oHadFile, out string oAuditWarn, out string oError)
        {
            oHadFile = false; oAuditWarn = ""; oError = "";
            if (string.IsNullOrWhiteSpace(iPersona)) { oError = "persona 必填"; return false; }
            if (string.IsNullOrWhiteSpace(iField)) { oError = "field 必填"; return false; }
            if (!SCP_PersonaProfile.IsIdentityField(iField)) { oError = iField + " 不是 identity 欄 —— profile/ 只收身分欄（§8.3）"; return false; }
            if (!NeedActorReason(iActor, iReason, out oError)) return false;
            try
            {
                string aPath = ProfileFieldPath(iLettersRoot, iPersona, iField);
                oHadFile = File.Exists(aPath);
                if (!oHadFile) return true;
                File.Delete(aPath);
            }
            catch (Exception e) { oError = e.Message; return false; }
            oAuditWarn = AppendAudit(iDataRoot, iPersona, "profile/" + iField + " (unset)", iActor, iReason);
            return true;
        }

        // ── 銀行綁定（一區一檔）─────────────────────────────────────
        /// <summary>
        /// 寫 persona 在 <paramref name="iRegion"/> 的綁定（`帳號\n`）＋ 審計 `bank/&lt;region&gt;`。
        /// 同值照寫照審計（與 Editor 版同）。
        /// </summary>
        public static bool WriteBankBinding(string iLettersRoot, string iDataRoot, string iPersona, string iRegion, string iAccount,
                                            string iActor, string iReason, out string oAuditWarn, out string oError)
        {
            oAuditWarn = ""; oError = "";
            if (string.IsNullOrWhiteSpace(iPersona)) { oError = "persona 必填"; return false; }
            if (!SCP_PersonaProfile.Exists(iLettersRoot, iPersona))
            { oError = $"查無此 persona（`letters/{iPersona}/profile/` 不存在）：{iPersona} —— ⛔ 不替打錯的名字開新資料夾"; return false; }
            if (string.IsNullOrWhiteSpace(iRegion)) { oError = "region 必填"; return false; }
            if (string.IsNullOrWhiteSpace(iAccount)) { oError = "account 必填 —— 要清空綁定請走 op=unbind（⛔ 不是寫空字串）"; return false; }
            if (!NeedActorReason(iActor, iReason, out oError)) return false;
            string aAccount = iAccount.Trim();
            if (aAccount.IndexOf('\n') >= 0 || aAccount.IndexOf('\r') >= 0) { oError = "account 不可含換行 —— 一檔一值"; return false; }
            try { WriteAtomic(BankFieldPath(iLettersRoot, iPersona, iRegion), aAccount + "\n"); }
            catch (Exception e) { oError = e.Message; return false; }
            oAuditWarn = AppendAudit(iDataRoot, iPersona, "bank/" + iRegion, iActor, iReason);
            return true;
        }

        /// <summary>刪掉 persona 在 <paramref name="iRegion"/> 的綁定檔。檔不存在 ⇒ 回 true、零寫入、不審計。</summary>
        public static bool DeleteBankBinding(string iLettersRoot, string iDataRoot, string iPersona, string iRegion,
                                             string iActor, string iReason, out string oAuditWarn, out string oError)
        {
            oAuditWarn = ""; oError = "";
            if (string.IsNullOrWhiteSpace(iPersona)) { oError = "persona 必填"; return false; }
            if (string.IsNullOrWhiteSpace(iRegion)) { oError = "region 必填"; return false; }
            if (!NeedActorReason(iActor, iReason, out oError)) return false;
            try
            {
                string aPath = BankFieldPath(iLettersRoot, iPersona, iRegion);
                if (!File.Exists(aPath)) return true;
                File.Delete(aPath);
            }
            catch (Exception e) { oError = e.Message; return false; }
            oAuditWarn = AppendAudit(iDataRoot, iPersona, "bank/" + iRegion + " (deleted)", iActor, iReason);
            return true;
        }

        // ── session lock（`profile/_session.json`）——後台操作的兩支（TASK-0361：Unity 登入狀態頁原本直寫）──
        // ⚠ lock 的**建立**只在早安（`SCP_Morning`）、**正常刪除**只在晚安（`SCP_Goodnight.SleepApply`）；
        //   這裡只收後台的兩個例外動作，每一筆都留審計（lock 本身不入版控，事後沒有別的地方查得到是誰動的）。

        /// <summary>
        /// 改在線者 lock 裡的 `actual_agent`，同一步把 `profile/actual_agent.md` 也改掉（兩邊不准只改一邊）。
        /// lock 讀進來只換那一格、用早安寫 lock 的同一個格式寫回（key 順序與其他欄位不動）。
        /// </summary>
        public static bool SetLockActualAgent(string iLettersRoot, string iDataRoot, string iPersona, string iValue,
                                              string iActor, string iReason, out string oAuditWarn, out string oError)
        {
            oAuditWarn = ""; oError = "";
            string aValue = (iValue ?? "").Trim();
            if (aValue.Length == 0) { oError = "actual_agent 不能是空的"; return false; }
            if (!SCP_PersonaProfile.Exists(iLettersRoot, iPersona)) { oError = $"查無此 persona：{iPersona}"; return false; }
            if (!NeedActorReason(iActor, iReason, out oError)) return false;
            string aLock = SCP_LettersPaths.SessionLockPath(new SCP_LettersRoot(iLettersRoot), iPersona);
            if (!SCP.Core.Io.SCP_AtomicFileRead.TryReadAllText(aLock, out string aText, out var aState))
            { oError = aState == SCP.Core.Io.SCP_FileReadState.Busy ? "lock 這一瞬間讀不了（換檔中）—— 重跑一次" : $"`{iPersona}` 沒有 lock（不在線）"; return false; }
            SCP_JsonData aJson;
            try { aJson = SCP_JsonData.Parse(aText); }
            catch (Exception e) { oError = "lock 解析不了（⛔ 不覆寫一顆壞 lock）：" + e.Message; return false; }
            aJson["actual_agent"] = aValue;
            try { WriteAtomic(aLock, SCP_JsonWriter.Write(aJson, SCP_JsonStyle.UclLegacy)); }
            catch (Exception e) { oError = "lock 寫不進去：" + e.Message; return false; }
            oAuditWarn = AppendAudit(iDataRoot, iPersona, "lock/actual_agent", iActor, iReason);
            if (!SetField(iLettersRoot, iDataRoot, iPersona, "actual_agent", aValue, iActor, iReason, out string aW2, out oError))
            { oError = "lock 已更新，而 profile/actual_agent 寫入失敗：" + oError; return false; }
            if (aW2.Length > 0) oAuditWarn = (oAuditWarn + " " + aW2).Trim();
            return true;
        }

        /// <summary>
        /// **強制刪 lock**（最後手段：晚安跑不通、lock 卡死）。⛔ 不寫信、不廣播、不關場 —— 那些是晚安的事。
        /// lock 本來就不在 ⇒ 回 true、零寫入、不審計。
        /// </summary>
        public static bool ForceReleaseLock(string iLettersRoot, string iDataRoot, string iPersona,
                                            string iActor, string iReason, out bool oHadLock, out string oAuditWarn, out string oError)
        {
            oHadLock = false; oAuditWarn = ""; oError = "";
            if (string.IsNullOrWhiteSpace(iPersona)) { oError = "persona 必填"; return false; }
            if (!NeedActorReason(iActor, iReason, out oError)) return false;
            string aLock = SCP_LettersPaths.SessionLockPath(new SCP_LettersRoot(iLettersRoot), iPersona);
            try
            {
                oHadLock = File.Exists(aLock);
                if (!oHadLock) return true;
                File.Delete(aLock);
            }
            catch (Exception e) { oError = e.Message; return false; }
            oAuditWarn = AppendAudit(iDataRoot, iPersona, "lock (force-released)", iActor, iReason);
            return true;
        }

        /// <summary>導出綁定的計數。</summary>
        public sealed class MigrateReport
        {
            public int Pool, Written, SkippedExisting, SkippedNoAgent, Failed;
            public List<string> Lines = new List<string>();
        }

        /// <summary>
        /// 新專案第一次用區域綁定：把全 pool 目前解析得到的帳號（本區沒有就是借別區的那一個）**寫成本區自己的綁定**。
        /// 本區已有 ⇒ 跳過（<paramref name="iOverwrite"/> 才覆寫）；解析不到 ⇒ 跳過；本區這一瞬間讀不了 ⇒ 失敗（⛔ 不覆寫）。
        /// <para>⚠ Editor 版讀的是 `persona.agent`，而它本來就是從綁定檔推導的（本區 → 借別區）⇒ 兩者同值。</para>
        /// </summary>
        public static MigrateReport MigrateBank(string iLettersRoot, string iDataRoot, string iRegion,
                                                string iActor, string iReason, bool iDryRun, bool iOverwrite)
        {
            var r = new MigrateReport();
            var aPool = SCP_PersonaProfile.PoolNames(iLettersRoot);
            r.Pool = aPool.Count;
            foreach (string p in aPool)
            {
                string aCur = SCP_PersonaProfile.GetBankAccount(iLettersRoot, p, iRegion, out string aSrc, out _);
                if (aSrc == SCP_PersonaProfile.BankSourceUnreadable)
                { r.Failed++; r.Lines.Add($"✗ {p}：本區綁定這一瞬間讀不了 —— ⛔ 不覆寫；重跑即可"); continue; }
                if (aCur.Length == 0) { r.SkippedNoAgent++; r.Lines.Add($"⛔ {p}：解析不到帳號（source={aSrc}）—— 跳過（沒有可導出的來源）"); continue; }
                bool aHasOwn = aSrc == iRegion;
                if (aHasOwn && !iOverwrite) { r.SkippedExisting++; r.Lines.Add($"○ {p}：本區已有綁定 '{aCur}' —— 跳過（overwrite=1 才覆寫）"); continue; }
                if (iDryRun) { r.Lines.Add($"→ {p}：會寫入 '{aCur}'（目前 source={aSrc}）"); continue; }
                if (!WriteBankBinding(iLettersRoot, iDataRoot, p, iRegion, aCur, iActor, iReason, out string aWarn, out string aErr))
                { r.Failed++; r.Lines.Add($"✗ {p}：寫入失敗 —— {aErr}"); continue; }
                if (aWarn.Length > 0) r.Lines.Add("⚠ " + p + "：" + aWarn);
                string aBack = SCP_PersonaProfile.GetBankAccount(iLettersRoot, p, iRegion, out string aBackSrc, out _);
                if (aBack != aCur || aBackSrc != iRegion)
                { r.Failed++; r.Lines.Add($"✗ {p}：寫入後讀回不符（期望 '{aCur}'@{iRegion}、實際 '{aBack}'@{aBackSrc}）"); continue; }
                r.Written++;
                r.Lines.Add($"✓ {p}：'{aBack}'");
            }
            r.Lines.Add($"⇒ 寫入 {r.Written}／既有跳過 {r.SkippedExisting}／解析不到跳過 {r.SkippedNoAgent}／失敗 {r.Failed}");
            return r;
        }

        /// <summary>換區重綁的計數。</summary>
        public sealed class RebindReport
        {
            public int Copied, Skipped, Conflicts, Failed;
            public List<string> Lines = new List<string>();
        }

        /// <summary>
        /// 把全 pool 的綁定從 <paramref name="iFrom"/> 區**複製**到 <paramref name="iTo"/> 區（⛔ 不刪舊區、不改設定）。
        /// 新區已有**不同值** ⇒ 衝突（不覆寫、不挑一個）；任一區這一瞬間讀不了 ⇒ 失敗（⛔ 不當成「沒有綁定」）。
        /// </summary>
        public static RebindReport CopyRegionAll(string iLettersRoot, string iDataRoot, string iFrom, string iTo,
                                                 string iActor, string iReason, bool iDryRun)
        {
            var r = new RebindReport();
            if (string.IsNullOrWhiteSpace(iFrom) || string.IsNullOrWhiteSpace(iTo) || iFrom == iTo)
            { r.Failed = 1; r.Lines.Add("⛔ from／to 必填且不可相同 —— 未動任何檔"); return r; }
            foreach (string p in SCP_PersonaProfile.PoolNames(iLettersRoot))
            {
                string aOld = SCP_PersonaProfile.ReadOwnBankBinding(iLettersRoot, p, iFrom, out bool aOldBusy);
                string aNew = SCP_PersonaProfile.ReadOwnBankBinding(iLettersRoot, p, iTo, out bool aNewBusy);
                if (aOldBusy || aNewBusy)
                { r.Failed++; r.Lines.Add($"✗ {p}：綁定檔這一瞬間讀不了（{(aOldBusy ? iFrom : iTo)}；換檔中或被鎖）—— 不動，重跑一次即可"); continue; }
                if (aOld.Length == 0)
                { r.Skipped++; r.Lines.Add($"・{p}：舊區（{iFrom}）無綁定 —— 跳過" + (aNew.Length == 0 ? "" : $"（新區已有 '{aNew}'，不動）")); continue; }
                if (aNew.Length > 0)
                {
                    if (aNew == aOld) { r.Skipped++; r.Lines.Add($"○ {p}：新區已是同值 '{aNew}' —— 視為已完成"); continue; }
                    r.Conflicts++;
                    r.Lines.Add($"⛔ {p}：**衝突** —— 舊區（{iFrom}）='{aOld}'、新區（{iTo}）已有不同值 '{aNew}'。不覆寫、不挑一個。");
                    continue;
                }
                if (iDryRun) { r.Copied++; r.Lines.Add($"→ {p}：會寫入 '{aOld}'"); continue; }
                if (!WriteBankBinding(iLettersRoot, iDataRoot, p, iTo, aOld, iActor, iReason, out string aWarn, out string aErr))
                { r.Failed++; r.Lines.Add($"✗ {p}：寫入失敗 —— {aErr}"); continue; }
                if (aWarn.Length > 0) r.Lines.Add("⚠ " + p + "：" + aWarn);
                string aBack = SCP_PersonaProfile.ReadOwnBankBinding(iLettersRoot, p, iTo, out _);
                if (aBack != aOld) { r.Failed++; r.Lines.Add($"✗ {p}：寫入後讀回不符（期望 '{aOld}'、實際 '{aBack}'）"); continue; }
                r.Copied++;
                r.Lines.Add($"✓ {p}：'{aBack}'");
            }
            r.Lines.Add($"⇒ 複製 {r.Copied}／跳過 {r.Skipped}／衝突 {r.Conflicts}／失敗 {r.Failed}");
            return r;
        }
    }
}
