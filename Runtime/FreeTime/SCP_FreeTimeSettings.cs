// 區塊職責：自由時間的**可調數值**（`<data_root>/FreeTime/freetime_settings.json`）—— 取代 Unity 版散在三支檔裡的常數。
// 物理意義：TASK-0360 搬家時 Tim 拍板：每場幾張限時券、券的緩衝、囤券門檻、飢餓門檻／置頂上限、
//          配對簡報列幾筆 inbox —— 這六格原本是寫死在程式裡的 `const`，
//          改它要改 code、要編譯、要等人 commit。搬成一份資料之後，後台頁就能改。
// 數值影響：每支 Cmd 呼叫**讀一次**、整趟傳遞（⛔ 不在半路重讀 —— 同一趟裡兩處讀到不同值會讓骰面自相矛盾）。
//          檔不存在 ⇒ 預設值，而且**要說出來**（「使用預設值（設定檔不存在）」）；
//          檔在但讀不了／值不合法 ⇒ 預設值 ＋ oError，呼叫端必須把那句印進回傳檔。
//          ⛔ 兩種都不准安靜：「我用的是預設」與「我用的是你設的」在骰面上長得一模一樣。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Io;
using SCP.Core.Json;

namespace SCP.Core.FreeTime
{
    /// <summary>自由時間的可調數值。欄位名 ≠ JSON 鍵名（JSON 用 snake_case，見 <see cref="ToJson"/>）。</summary>
    public sealed class SCP_FreeTimeSettings
    {
        /// <summary>設定檔在資料根底下的相對位置。</summary>
        public const string RelPath = "FreeTime/freetime_settings.json";

        /// <summary>每場自由時間發幾張限時繪圖券（Tim 2026-08-13：10；per-session 清零不累積）。</summary>
        public int PixelsPerSession = 10;

        /// <summary>
        /// 限時券到期的緩衝（分鐘）—— **截止是軟的**：最後一件活動可能跨過 until 才收工，
        /// 而那一刻他手上的券不該已經失效（Tim 2026-08-18：1 分）。
        /// </summary>
        public int VoucherGraceMinutes = 1;

        /// <summary>永久繪圖券**超過**幾張算囤積（Tim 2026-08-18：100）。只影響排序與名字，不擋任何事。</summary>
        public int VoucherHoardThreshold = 100;

        /// <summary>幾場沒被選算「太久」（Tim 2026-08-24：5）。</summary>
        public int StarveThreshold = 5;

        /// <summary>
        /// 一次最多頂幾項飢餓活動（Tim 2026-08-24：2）。
        /// 🩸 為什麼一定要有上限：飢餓度天生會整批超標（新增一件活動時它立刻是最餓的），
        ///   **當多數項目同時符合條件，排序就失去解析度**（前 15 名全部同分 ⇒ 名次其實由 tie-break 決定）。
        /// </summary>
        public int StarveHoistMax = 2;

        /// <summary>配對簡報裡酒館 inbox 列最新幾筆（Unity 版寫死 10）。全部都在 inbox 檔裡，這裡只決定一眼看得完多少。</summary>
        public int InboxHeadsShown = 10;

        /// <summary>設定檔路徑：<c>&lt;data_root&gt;/FreeTime/freetime_settings.json</c>。</summary>
        public static string PathOf(string iDataRoot)
            => Path.Combine(iDataRoot, "FreeTime", "freetime_settings.json").Replace('\\', '/');

        public static SCP_FreeTimeSettings Defaults() => new SCP_FreeTimeSettings();

        public SCP_FreeTimeSettings Clone() => (SCP_FreeTimeSettings)MemberwiseClone();

        // ── 鍵表（唯一一份：讀、寫、比對都走它 —— 三處各寫一次就是三份會漂的 schema）──
        static readonly string[] s_Keys =
        {
            "pixels_per_session", "voucher_grace_minutes", "voucher_hoard_threshold",
            "starve_threshold", "starve_hoist_max", "inbox_heads_shown",
        };

        int Get(string iKey)
        {
            switch (iKey)
            {
                case "pixels_per_session": return PixelsPerSession;
                case "voucher_grace_minutes": return VoucherGraceMinutes;
                case "voucher_hoard_threshold": return VoucherHoardThreshold;
                case "starve_threshold": return StarveThreshold;
                case "starve_hoist_max": return StarveHoistMax;
                case "inbox_heads_shown": return InboxHeadsShown;
                default: throw new ArgumentException("不認得的設定鍵：" + iKey);
            }
        }

        void Set(string iKey, int iValue)
        {
            switch (iKey)
            {
                case "pixels_per_session": PixelsPerSession = iValue; break;
                case "voucher_grace_minutes": VoucherGraceMinutes = iValue; break;
                case "voucher_hoard_threshold": VoucherHoardThreshold = iValue; break;
                case "starve_threshold": StarveThreshold = iValue; break;
                case "starve_hoist_max": StarveHoistMax = iValue; break;
                case "inbox_heads_shown": InboxHeadsShown = iValue; break;
                default: throw new ArgumentException("不認得的設定鍵：" + iKey);
            }
        }

        /// <summary>序列化（鍵序固定 ＝ <see cref="s_Keys"/>）。</summary>
        public string ToJson()
        {
            var aJd = SCP_JsonData.NewObject();
            foreach (string aKey in s_Keys) aJd[aKey] = Get(aKey);
            return SCP_JsonWriter.Write(aJd, true);
        }

        /// <summary>
        /// 合法性。空清單 ＝ 合法。
        /// <para>每格 ≥ 0（含 <c>starve_hoist_max</c>，0 ＝ 關掉飢餓置頂）；
        /// <c>pixels_per_session</c> ≥ 1（發 0 張的自由時間等於「發券」這一步不存在，而公告照樣會說發了）；
        /// <c>inbox_heads_shown</c> ≥ 1（列 0 筆的 inbox 段跟「沒有 inbox」同形）。</para>
        /// </summary>
        public static List<string> Validate(SCP_FreeTimeSettings iS)
        {
            var aOut = new List<string>();
            if (iS == null) { aOut.Add("設定是 null"); return aOut; }
            foreach (string aKey in s_Keys)
                if (iS.Get(aKey) < 0) aOut.Add($"`{aKey}` 不可以是負數（收到 {iS.Get(aKey)}）");
            if (iS.PixelsPerSession < 1) aOut.Add($"`pixels_per_session` 至少 1（收到 {iS.PixelsPerSession}）");
            if (iS.InboxHeadsShown < 1) aOut.Add($"`inbox_heads_shown` 至少 1（收到 {iS.InboxHeadsShown}）");
            // `starve_hoist_max` 允許 0（＝關掉飢餓置頂）；它的 ≥0 已在上面那條迴圈裡擋過。
            return aOut;
        }

        /// <summary>
        /// 讀設定。
        /// <para>檔不存在 ⇒ 預設值、<paramref name="oFileExists"/>=false、<paramref name="oError"/>=null（呼叫端印「使用預設值（設定檔不存在）」）。</para>
        /// <para>檔在但讀不了／不是物件／某格不是整數／驗不過 ⇒ **整份**預設值 ＋ <paramref name="oError"/>。
        /// ⛔ 不做「壞的那格用預設、其餘照用」：半份設定會讓「我改了 A 格」與「A 格其實沒生效」同形。</para>
        /// <para>某個鍵**缺席** ⇒ 那一格用預設（向前相容：之後加新格不必回頭改舊檔）。未知鍵忽略。</para>
        /// </summary>
        public static SCP_FreeTimeSettings Read(string iDataRoot, out bool oFileExists, out string? oError)
        {
            oFileExists = false;
            oError = null;
            string aPath = PathOf(iDataRoot);
            // TASK-0265 那一族：後台頁以換檔寫入 ⇒ 重試跨過那一瞬間；只有**真的不存在**才走「設定檔不存在」。
            if (!SCP_AtomicFileRead.TryReadAllText(aPath, out string aText, out SCP_FileReadState aState))
            {
                if (aState == SCP_FileReadState.Missing) return Defaults();
                oFileExists = true;
                oError = "設定檔這一瞬間讀不了 ⇒ 用預設值（⚠ 這不是你設的值）：" + SCP_AtomicFileRead.DescribeBusy(aPath);
                return Defaults();
            }
            oFileExists = true;
            SCP_JsonData aJd;
            try { aJd = SCP_JsonData.Parse(aText); }
            catch (Exception e) { oError = $"設定檔解析失敗（{e.GetType().Name}: {e.Message}）⇒ 用預設值：{aPath}"; return Defaults(); }
            if (aJd == null || !aJd.IsObject) { oError = "設定檔不是 JSON 物件 ⇒ 用預設值：" + aPath; return Defaults(); }

            var aOut = Defaults();
            foreach (string aKey in s_Keys)
            {
                SCP_JsonData aV = aJd[aKey];
                if (!aV.Exists || aV.IsNull) continue;
                long aNum;
                try { aNum = aV.AsLong(); }
                catch (Exception) { oError = $"`{aKey}` 不是整數（`{aV}`）⇒ 整份用預設值：{aPath}"; return Defaults(); }
                if (aNum < int.MinValue || aNum > int.MaxValue)
                { oError = $"`{aKey}` 超出整數範圍（{aNum}）⇒ 整份用預設值：{aPath}"; return Defaults(); }
                aOut.Set(aKey, (int)aNum);
            }
            List<string> aBad = Validate(aOut);
            if (aBad.Count > 0)
            {
                oError = "設定值不合法 ⇒ 整份用預設值：" + string.Join("；", aBad) + "（" + aPath + "）";
                return Defaults();
            }
            return aOut;
        }

        /// <summary>
        /// 寫設定：驗 → 暫存檔 → 換檔 → **讀回比對**。
        /// <para>⛔ 不拿「沒丟例外」當落盤的證據 —— 讀回來每一格都對得上才回 true。</para>
        /// </summary>
        public static bool Write(string iDataRoot, SCP_FreeTimeSettings iS, out string? oError)
        {
            oError = null;
            List<string> aBad = Validate(iS);
            if (aBad.Count > 0) { oError = "✗ 不寫：" + string.Join("；", aBad); return false; }
            string aPath = PathOf(iDataRoot);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aPath) ?? ".");
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, iS.ToJson(), new UTF8Encoding(false));
                SCP_TextFile.ReplaceOrMove(aTmp, aPath);
            }
            catch (Exception e) { oError = "✗ 寫不進去：" + e.GetType().Name + ": " + e.Message + "（" + aPath + "）"; return false; }

            SCP_FreeTimeSettings aBack = Read(iDataRoot, out bool aExists, out string? aReadErr);
            if (!aExists) { oError = "✗ 寫完回讀：檔不見了（" + aPath + "）"; return false; }
            if (aReadErr != null) { oError = "✗ 寫完回讀失敗：" + aReadErr; return false; }
            foreach (string aKey in s_Keys)
                if (aBack.Get(aKey) != iS.Get(aKey))
                {
                    oError = $"✗ 寫完回讀對不上：`{aKey}` 寫 {iS.Get(aKey)}、讀回 {aBack.Get(aKey)}";
                    return false;
                }
            return true;
        }

        /// <summary>給回傳檔的一行：本趟用的是哪一份設定、值是多少。</summary>
        public string Describe(bool iFileExists, string? iError)
        {
            string aSrc = iError != null ? "⚠ **使用預設值（設定檔讀不了）**"
                        : !iFileExists ? "使用預設值（設定檔不存在）"
                        : "設定檔";
            return aSrc + "：每場 " + PixelsPerSession.ToString(CultureInfo.InvariantCulture) + " 張限時券"
                   + "／緩衝 " + VoucherGraceMinutes.ToString(CultureInfo.InvariantCulture) + " 分"
                   + "／囤券門檻 >" + VoucherHoardThreshold.ToString(CultureInfo.InvariantCulture)
                   + "／飢餓門檻 " + StarveThreshold.ToString(CultureInfo.InvariantCulture) + " 場"
                   + "（最多頂 " + StarveHoistMax.ToString(CultureInfo.InvariantCulture) + " 項）"
                   + "／inbox 列 " + InboxHeadsShown.ToString(CultureInfo.InvariantCulture) + " 筆";
        }
    }
}
