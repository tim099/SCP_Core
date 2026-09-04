// 區塊職責：活動 session 的**唯一** IO 入口 —— 路徑 / 讀 / 寫 / 收工 / 現況查詢 / 全員掃描。
// 物理意義：這是 UCL 那側 `UCL_SessionService` 的搬家版本（TASK-0127）。搬過來的理由不是「新的比較好」——
//           是四張下游單裡有兩張半（0055／0056／0057）在舊宿主做完就要重做（0057 的執行點已經在新家）。
// 數值影響：一次讀 / 寫一個檔。檔案格式與位置**逐鍵、逐字相同**（`<DataRoot>/sessions/<persona>.json`）
//           ⇒ 既有檔不需遷移；同一份檔 Unity 與 Senate 兩邊讀到的是同一個東西。
//
// ⚠ **一人一檔位**：kind 是檔案裡的欄位，不是路徑段。⇒「同一個人同時兩種 session」在形狀層不可能，
//   而它的另一半是：**寫入端會覆蓋** —— 這一層因此提供 <see cref="TryStart"/>（先查再寫），
//   而不是讓每個呼叫端自己 Load 自己那個 kind（那正是 TASK-0056 那個洞：
//   `Load(FreeTime,…)` 在一場進行中的 StreamWatch 面前回 null ⇒ 守衛放行 ⇒ 覆蓋）。
//
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方；JSON 一律走 SCP_Json；路徑吃 typed root（傳錯根編不過）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Session
{
    public static class SCP_ActivitySessionStore
    {
        /// <summary>session 檔住的目錄名（跨端契約 —— UCL 那側同名，改了要對兩邊）。</summary>
        public const string SessionsDirName = "sessions";

        static readonly UTF8Encoding s_Utf8NoBom = new UTF8Encoding(false);

        // ── 路徑（唯一組法，零 IO）────────────────────────────────

        /// <summary>session 檔的資料夾：<c>&lt;DataRoot&gt;/sessions</c>。</summary>
        public static string Dir(SCP_DataRoot iRoot) => iRoot.Value + "/" + SessionsDirName;

        /// <summary>
        /// 某人的 session 檔：<c>&lt;DataRoot&gt;/sessions/&lt;persona&gt;.json</c>。
        /// <para>⚠ persona 常常直接來自 CLI 參數 ⇒ 內建穿越防護，擋下時回 null（不靜默改寫成別人的檔）。</para>
        /// </summary>
        public static string? PathOf(SCP_DataRoot iRoot, string? iPersona)
        {
            if (string.IsNullOrEmpty(iPersona)) return null;
            string aName = iPersona!.Trim();
            if (aName.Length == 0) return null;
            if (aName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0) return null;
            if (aName == "." || aName == "..") return null;
            return Dir(iRoot) + "/" + aName + ".json";
        }

        // ── 讀 ────────────────────────────────────────────────────

        /// <summary>
        /// 讀某人的 session。<paramref name="iKind"/> 給值就**過濾 kind**（不符回 null ＝「這個人不在這個 kind」）；
        /// 給 null 表示「先看它是哪一種」。
        /// </summary>
        /// <remarks>
        /// 🩸 這個 kind 過濾正是 TASK-0056 那個洞的來源：呼叫端拿它當「有沒有 session」的判斷，
        /// 而它回答的是「有沒有**我這種** session」。⇒ 要問「這個人現在忙不忙」一律用 <see cref="FindRunning"/>。
        /// </remarks>
        public static SCP_ActivitySession? Load(SCP_DataRoot iRoot, string? iPersona, string? iKind = null)
        {
            string? aPath = PathOf(iRoot, iPersona);
            if (aPath == null || !File.Exists(aPath)) return null;
            try
            {
                SCP_JsonData aData = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                var aSession = new SCP_ActivitySession();
                SCP_JsonMapper.Populate(aSession, aData);
                aSession.Raw = aData;   // 各 kind 的專屬欄位住在這 —— 寫回時以它為底（見 SCP_ActivitySession.Raw）
                if (!string.IsNullOrEmpty(iKind)
                    && !string.Equals(aSession.kind, iKind, StringComparison.Ordinal)) return null;
                return aSession;
            }
            catch (Exception)
            {
                // 壞檔當「沒有」—— 但**不刪不改**：壞檔是證據，靜默覆蓋它就沒有人查得出當時發生什麼事。
                return null;
            }
        }

        /// <summary>這個 persona 此刻在進行中的 session（0 或 1 筆 —— 一人一檔位）。</summary>
        /// <remarks>
        /// ⚠ 回傳空的語意是「**在已登記的種類裡**沒查到」，不是「他絕對沒在任何 session」。
        /// 回報時一併說掃了哪些 kind（<see cref="SCP_ActivitySessionKind.Kinds"/>）。
        /// </remarks>
        public static SCP_ActivitySession? FindRunning(SCP_DataRoot iRoot, string? iPersona, DateTime iNowLocal)
        {
            SCP_ActivitySession? aSession = Load(iRoot, iPersona);
            if (aSession == null) return null;
            if (!SCP_ActivitySessionKind.IsRegistered(aSession.kind)) return null;
            return aSession.IsRunningAt(iNowLocal, out _) ? aSession : null;
        }

        /// <summary>列出 sessions 目錄裡所有 persona 的 session（給管理頁用；讀不回來的檔不列，但會記進 <paramref name="oProblems"/>）。</summary>
        public static List<SCP_ActivitySession> LoadAll(SCP_DataRoot iRoot, List<string>? oProblems = null)
        {
            var aList = new List<SCP_ActivitySession>();
            string aDir = Dir(iRoot);
            if (!Directory.Exists(aDir)) return aList;
            string[] aFiles;
            try { aFiles = Directory.GetFiles(aDir, "*.json"); }
            catch (Exception e) { oProblems?.Add("讀取 sessions 目錄失敗：" + e.Message); return aList; }
            Array.Sort(aFiles, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < aFiles.Length; ++i)
            {
                string aPersona = Path.GetFileNameWithoutExtension(aFiles[i]);
                SCP_ActivitySession? aSession = Load(iRoot, aPersona);
                if (aSession == null) { oProblems?.Add("讀不回來（壞檔或穿越）：" + aFiles[i]); continue; }
                if (string.IsNullOrEmpty(aSession.persona)) aSession.persona = aPersona;
                aList.Add(aSession);
            }
            return aList;
        }

        // ── 寫 ────────────────────────────────────────────────────

        /// <summary>
        /// 寫一份 session（atomic —— 半寫的檔會讓下一次讀取判成「沒有 session」）。
        /// <para>⚠ <paramref name="iKind"/> 在這裡的作用是**落進 json 欄位**（扁平化後那是 kind 的唯一存放處）。</para>
        /// </summary>
        public static bool Save(SCP_DataRoot iRoot, string? iPersona, SCP_ActivitySession iSession, string? iKind = null)
        {
            if (iSession == null) return false;
            string? aPath = PathOf(iRoot, iPersona);
            if (aPath == null) return false;
            if (!string.IsNullOrEmpty(iKind)) iSession.kind = iKind!;
            if (string.IsNullOrEmpty(iSession.persona)) iSession.persona = iPersona ?? "";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aPath)!);
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(MergeOntoRaw(iSession), true), s_Utf8NoBom);
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
                return true;
            }
            catch (Exception) { return false; }
        }

        /// <summary>
        /// 開場的**唯一安全入口** —— 先查這個人在不在別的場，在就不寫。
        /// </summary>
        /// <remarks>
        /// 🩸 TASK-0056：舊路徑上每個 kind 各自 `Load(自己那個 kind)` 判「有沒有在跑」，
        /// 而那個判斷**看不見別的 kind** ⇒ 一場進行中的觀影在自由時間眼裡等於沒有 session
        /// ⇒ 守衛放行 ⇒ `Save` 覆蓋掉它。**擋在寫入端，不是擋在每個呼叫端的記性上。**
        /// </remarks>
        /// <param name="oBlockedBy">被擋下時，這裡是**擋住你的那一場**（給呼叫端組「原因＋處理方式」）。</param>
        public static bool TryStart(SCP_DataRoot iRoot, string? iPersona, SCP_ActivitySession iSession,
            string iKind, DateTime iNowLocal, out SCP_ActivitySession? oBlockedBy)
        {
            oBlockedBy = null;
            SCP_ActivitySession? aRunning = FindRunning(iRoot, iPersona, iNowLocal);
            if (aRunning != null)
            {
                // 同 kind 疊開仍由各 kind 自己的守衛管（本層不重造）—— 這裡只擋**跨 kind 覆蓋**。
                if (!string.Equals(aRunning.kind, iKind, StringComparison.Ordinal))
                {
                    oBlockedBy = aRunning;
                    return false;
                }
            }
            return Save(iRoot, iPersona, iSession, iKind);
        }

        /// <summary>
        /// 以載入時的原始 JSON 為底，覆寫共通欄位 —— **這一層不認識的鍵原樣留著**。
        /// </summary>
        /// <remarks>
        /// 🩸 沒有這一步的話，收工（讀→翻三欄→寫回）會把各 kind 的專屬欄位吃掉，
        /// 而症狀是「下一個讀那些欄位的人拿到預設值」——不會有任何一層報錯。
        /// </remarks>
        static SCP_JsonData MergeOntoRaw(SCP_ActivitySession iSession)
        {
            SCP_JsonData aCommon = SCP_JsonMapper.ToJson(iSession) ?? SCP_JsonData.NewObject();
            SCP_JsonData? aRaw = iSession.Raw;
            if (aRaw == null || aRaw.Type != SCP_JsonType.Object) return aCommon;
            IReadOnlyList<string> aKeys = aCommon.Keys;
            for (int i = 0; i < aKeys.Count; ++i) aRaw[aKeys[i]] = aCommon[aKeys[i]];
            return aRaw;
        }

        // ── 收工 ──────────────────────────────────────────────────

        /// <summary>
        /// 收工 —— 翻 active、記原因與時刻，**三個欄位一起**（散開來寫時漏掉 ended_at 不會有任何症狀）。
        /// <para>⚠ 這是 base close：**不跑結算**。有結算的 kind 走 <see cref="CloseWithSettlement"/>。</para>
        /// </summary>
        public static bool Close(SCP_DataRoot iRoot, string? iPersona, SCP_ActivitySession ioSession, string? iReason)
        {
            if (ioSession == null) return false;
            ioSession.active = false;
            ioSession.end_reason = iReason ?? "";
            ioSession.ended_at = SCP_ActivitySession.NowIso();
            return Save(iRoot, iPersona, ioSession, ioSession.kind);
        }

        /// <summary>
        /// **關場統一入口**（TASK-0055 拍板②）—— 所有關場路徑走這個門：
        /// 管理頁補收工 / 互斥出口指的收工 / 晚安自動關。
        /// </summary>
        /// <remarks>
        /// 次序是拍板過的，不讓各 kind 自選（TASK-0055 PM 增補）：
        /// **① 權威狀態先落地 → ② 金流結算 best-effort → ③ 廣播 best-effort**，每步結果分開回報。
        /// ⇒ 結算炸掉**不得冒充整場失敗**：session 仍然是關的，回傳檔分段列「已關閉／結算失敗（原因）」。
        /// 🩸 反過來寫（先結算再落狀態）的代價：結算成功而狀態沒寫 ⇒ 下一次會**再結算一次**。
        /// </remarks>
        public static SCP_ActivitySessionCloseResult CloseWithSettlement(
            SCP_DataRoot iRoot, string? iPersona, SCP_ActivitySession ioSession, string? iReason)
        {
            var aResult = new SCP_ActivitySessionCloseResult();
            if (ioSession == null) return aResult;

            // ── 有 gateway ⇒ **整步交給它**（它那一端連結算一起做，權威狀態也由它寫）──
            // ⚠ 這裡刻意**不先自己關場**：先關再委派的話，對面會判「已經收過工」而跳過結算
            //   ⇒ 結算永遠不發生，而兩邊都不報錯（2026-09-04 同日量到的形狀，見介面的 remarks）。
            //   ⇒ 而且不先寫檔還有第二個好處：**寫入端只有一個**（TASK-0100 的主題）。
            SCP_IActivitySessionCloseGateway? aGate = SCP_ActivitySessionGatewayHost.For(iRoot.Value, ioSession.kind);
            aResult.HasHandler = aGate != null;
            if (aGate != null)
            {
                try
                {
                    bool aOk = aGate.TryClose(ioSession, iReason ?? "", aResult.SettleLines, out string aErr);
                    aResult.ClosedByGateway = aOk;
                    aResult.Settled = aOk;
                    if (!aOk) aResult.SettleError = string.IsNullOrEmpty(aErr) ? "（gateway 沒說原因）" : aErr;
                }
                catch (Exception e)
                {
                    aResult.SettleError = e.GetType().Name + ": " + e.Message;
                }
                // ⚠ 回讀確認 —— gateway 說成功不算數，磁碟說了才算（它在另一個 process 裡）。
                SCP_ActivitySession? aBack = Load(iRoot, iPersona);
                aResult.Closed = aBack != null && !aBack.active;
                return aResult;
            }

            // ── 沒有 gateway ⇒ base close（明確降級，不靜默）──
            aResult.Closed = Close(iRoot, iPersona, ioSession, iReason);
            if (!aResult.Closed) aResult.SettleError = "session 檔寫不進去";
            return aResult;
        }
    }
}
