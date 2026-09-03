// 區塊職責：放點的**寫入端** —— 像素驗證、付款計畫、跨 process 付款鎖、事件檔落盤。
// 物理意義：一次 place ＝ 「收錢 → 寫事件 → 重渲」三步，而順序不可換：
//           **先收錢再畫**。畫了卻沒扣到錢等於免費像素，那比拒絕嚴重得多。
// 數值影響：付款優先序（pay=auto）＝ 限時券 → 永久券 → token（限時的會過期所以先花）；
//           三者合計不足 ⇒ **整批拒絕**，不部分扣、不部分畫。
// 設計取捨：付款鎖與 python 的 `payment_lock` **同檔名同語意**
//           （`Canvas/_locks/place_<bank>__<persona>.lock`，O_EXCL 建檔、寫 pid、finally 刪）——
//           🩸 兩個寫入端並存期間不共用同一把鎖，就是 TOCTOU double-spend：
//           兩個 place 各自讀到同一個餘額、各自扣一次，而兩邊的帳看起來都對。
//           ⇒ 鎖檔的名字是**協議的一部分**，不是實作細節，改名等於解除互斥。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Canvas
{
    /// <summary>一顆要放的像素（座標已驗證在界內、顏色已解析成 palette index）。</summary>
    public readonly struct SCP_CanvasPixel
    {
        public readonly int X;
        public readonly int Y;
        public readonly int ColorIndex;

        public SCP_CanvasPixel(int iX, int iY, int iColorIndex) { X = iX; Y = iY; ColorIndex = iColorIndex; }
    }

    /// <summary>付款分配：三者合計 ＝ 像素數。</summary>
    public readonly struct SCP_CanvasPayPlan
    {
        /// <summary>限時券（事件檔裡的 key 是 <c>freetime</c>，沿用不改）。</summary>
        public readonly int Expiring;
        /// <summary>永久券。</summary>
        public readonly int Permanent;
        public readonly int Token;

        public SCP_CanvasPayPlan(int iExpiring, int iPermanent, int iToken)
        {
            Expiring = iExpiring; Permanent = iPermanent; Token = iToken;
        }

        public int Total => Expiring + Permanent + Token;
    }

    public static class SCP_CanvasPlace
    {
        public const double LockTimeoutSec = 10.0;
        public const int LockPollMs = 20;

        // ───────────────────────────── 像素解析與驗證 ─────────────────────────────

        /// <summary>
        /// 解析 <c>--arg pixels=[{x,y,color},…]</c> 或單點 x/y/color。
        /// <para>⚠ 任何一顆不合法 ⇒ **整批拒絕**（不挑掉壞的那顆繼續畫：
        /// 那會讓「我放了 10 顆」與「畫上去 9 顆」同時是真的）。</para>
        /// </summary>
        public static bool TryParsePixels(string iPixelsJson, string iX, string iY, string iColor,
                                          out List<SCP_CanvasPixel> oPixels, out string oWhy)
        {
            oPixels = new List<SCP_CanvasPixel>();
            oWhy = "";
            if (iPixelsJson.Trim().Length > 0)
            {
                SCP_JsonData aArr;
                try { aArr = SCP_JsonParser.Parse(iPixelsJson); }
                catch (SCP_JsonParseException e) { oWhy = "pixels 不是合法 JSON：" + e.Message; return false; }
                if (aArr.Count == 0) { oWhy = "pixels 是空陣列 —— 放 0 顆不是成功，是沒說要放什麼"; return false; }
                for (int i = 0; i < aArr.Count; i++)
                {
                    SCP_JsonData aP = aArr[i];
                    int aPx = aP.GetInt("x", int.MinValue);
                    int aPy = aP.GetInt("y", int.MinValue);
                    if (!SCP_CanvasSpec.InBounds(aPx, aPy))
                    { oWhy = "第 " + (i + 1) + " 顆座標越界：(" + aPx + "," + aPy + ")"; return false; }
                    if (!SCP_CanvasEvents.TryColor(aP["color"], out int aIdx))
                    { oWhy = "第 " + (i + 1) + " 顆的顏色解不出來"; return false; }
                    oPixels.Add(new SCP_CanvasPixel(aPx, aPy, aIdx));
                }
                return true;
            }

            if (!int.TryParse(iX.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aX)
                || !int.TryParse(iY.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int aY))
            { oWhy = "沒給 pixels 就要給 x 與 y（整數）"; return false; }
            if (!SCP_CanvasSpec.InBounds(aX, aY))
            { oWhy = "座標越界：(" + aX + "," + aY + ")"; return false; }
            if (!SCP_CanvasPalette.TryParse(iColor, out int aColor, out string aColorWhy))
            { oWhy = aColorWhy; return false; }
            oPixels.Add(new SCP_CanvasPixel(aX, aY, aColor));
            return true;
        }

        /// <summary>
        /// 白色陷阱守衛（python 那側**沒有**這道，本階段新增）。
        /// <para>🩸 index 255 同時是「純白」與「沒有人畫過」⇒ 畫上去之後：扣了款、事件落盤、
        /// 回讀是「空白」，三邊都不出錯誤訊息（basecamp 2026-08-19 實測；畫布上現有 66 格是這樣來的）。</para>
        /// <para>⚠ 但「覆蓋成白」是**合法語彙**（可覆蓋的共用畫布需要「擦掉」）⇒ 不能硬擋死：
        /// 預設拒絕並說清楚，顯式帶 <c>allow_white=1</c> 才放行。
        /// 這是「讓失敗不可能 ＞ 當場喊 ＞ 記得注意」裡的第二階 —— 第一階會拿掉語彙。</para>
        /// </summary>
        public static bool HasBlankWhite(List<SCP_CanvasPixel> iPixels, out int oCount)
        {
            oCount = 0;
            foreach (SCP_CanvasPixel aP in iPixels)
                if (aP.ColorIndex == SCP_CanvasSpec.BlankIndex) oCount++;
            return oCount > 0;
        }

        // ───────────────────────────── 付款計畫 ─────────────────────────────

        /// <summary>
        /// 規劃 N 顆的付款（atomic 預驗：不足整批拒絕）。**必須在鎖內呼叫** ——
        /// 讀餘額與寫扣款之間若能被別的 process 插進來，兩邊會各自扣一次。
        /// <para>⚠ 任何一種資源「查不到」時**不准當 0**：那是拿「不知道」冒充「沒錢」，
        /// 而使用者會照著那句去加值（python 那側同語意，2026-08 起）。</para>
        /// </summary>
        public static bool TryPlan(SCP_ICanvasGateway iGate, string iPersona, string iAccount,
                                   int iCount, string iPay, out SCP_CanvasPayPlan oPlan, out string oWhy)
        {
            oPlan = default;
            oWhy = "";

            int aExpiring = iGate.QueryExpiringVouchers(iPersona, out string aExpWhy);
            int aPermanent = iGate.QueryPermanentVouchers(iPersona, out string aPermWhy);
            if (aExpiring < 0 || aPermanent < 0)
            {
                oWhy = "查不到券數（" + (aExpiring < 0 ? aExpWhy : aPermWhy) + "）"
                       + " —— 這是「不知道」不是「沒有券」，本次不扣款、不放點";
                return false;
            }

            long aToken = -1;
            if (iAccount.Length > 0)
            {
                aToken = iGate.QueryTokenBalance(iAccount, out string aTokenWhy);
                if (aToken < 0)
                {
                    oWhy = "查不到 " + iAccount + " 的餘額（" + aTokenWhy + "）"
                           + " —— 這是「不知道」不是「沒錢」，別照這句去加值";
                    return false;
                }
            }

            switch (iPay)
            {
                case "freetime":
                case "expiring":
                    if (iCount > aExpiring)
                    { oWhy = "限時券不足：需 " + iCount + "，未過期限時券 " + aExpiring + "（不在自由時間時這個數字是 0）"; return false; }
                    oPlan = new SCP_CanvasPayPlan(iCount, 0, 0);
                    return true;

                case "voucher":
                case "permanent":
                    if (iCount > aPermanent)
                    { oWhy = "永久券不足：需 " + iCount + "，永久券 " + aPermanent + "（限時券另有 " + aExpiring + " 張，pay=auto 會先花它們）"; return false; }
                    oPlan = new SCP_CanvasPayPlan(0, iCount, 0);
                    return true;

                case "token":
                    if (iAccount.Length == 0)
                    { oWhy = "pay=token 必須顯式帶 account —— ⛔ 不從 persona 猜一個帳戶（猜錯是扣別人的錢）"; return false; }
                    if (iCount > aToken)
                    { oWhy = "token 不足：需 " + iCount + "，" + iAccount + " 餘額 " + aToken; return false; }
                    oPlan = new SCP_CanvasPayPlan(0, 0, iCount);
                    return true;

                default:
                    // auto：限時券 → 永久券 → token
                    int aRemaining = iCount;
                    int aUseExp = Math.Min(aExpiring, aRemaining); aRemaining -= aUseExp;
                    int aUsePerm = Math.Min(aPermanent, aRemaining); aRemaining -= aUsePerm;
                    int aUseToken = 0;
                    if (aRemaining > 0)
                    {
                        if (iAccount.Length == 0)
                        {
                            oWhy = "券不夠（限時 " + aExpiring + " ＋ 永久 " + aPermanent + " ＝ "
                                   + (aExpiring + aPermanent) + "，需 " + iCount + "），而沒給 account"
                                   + " ⇒ 不足的 " + aRemaining + " 顆要用 token，但**我不猜帳戶**（猜錯是扣別人的錢）";
                            return false;
                        }
                        aUseToken = (int)Math.Min(aToken, aRemaining);
                        aRemaining -= aUseToken;
                    }
                    if (aRemaining > 0)
                    {
                        oWhy = "資源合計不足：需 " + iCount + "，限時券 " + aExpiring + " ＋ 永久券 "
                               + aPermanent + " ＋ token " + aToken + " ＝ " + (aExpiring + aPermanent + aToken);
                        return false;
                    }
                    oPlan = new SCP_CanvasPayPlan(aUseExp, aUsePerm, aUseToken);
                    return true;
            }
        }

        // ───────────────────────────── 付款鎖 ─────────────────────────────

        /// <summary>
        /// 跨 process 付款鎖。鎖檔名與 python 逐字同形 —— 它是協議不是實作細節。
        /// <para>逾時**不強奪**（對方可能是 crash 留下的 stale lock，強奪會再造一次 double-spend）；
        /// 保守失敗，讓人自己看那個檔案裡的 pid。</para>
        /// </summary>
        public static IDisposable? TryAcquireLock(SCP_CanvasPaths iPaths, string iBank, string iPersona,
                                                  out string oWhy)
        {
            oWhy = "";
            string aSafe = (iBank + "__" + iPersona).Replace('/', '_').Replace('\\', '_');
            string aPath = iPaths.Locks + "/place_" + aSafe + ".lock";
            try { Directory.CreateDirectory(iPaths.Locks); }
            catch (Exception e) { oWhy = "建不了鎖目錄：" + e.Message; return null; }

            DateTime aDeadline = DateTime.UtcNow.AddSeconds(LockTimeoutSec);
            while (true)
            {
                try
                {
                    // FileMode.CreateNew ＝ O_CREAT|O_EXCL：檔在就丟例外，不覆蓋
                    var aFs = new FileStream(aPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    byte[] aPid = Encoding.UTF8.GetBytes(
                        System.Diagnostics.Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture));
                    aFs.Write(aPid, 0, aPid.Length);
                    aFs.Flush();
                    return new LockHandle(aFs, aPath);
                }
                catch (IOException)
                {
                    if (DateTime.UtcNow >= aDeadline)
                    {
                        oWhy = "payment_lock 逾時（" + LockTimeoutSec.ToString("0", CultureInfo.InvariantCulture)
                               + "s）：" + aPath + " 被占用（檔案內容是持有者的 pid）";
                        return null;
                    }
                    System.Threading.Thread.Sleep(LockPollMs);
                }
                catch (Exception e)
                {
                    oWhy = e.GetType().Name + ": " + e.Message;
                    return null;
                }
            }
        }

        sealed class LockHandle : IDisposable
        {
            readonly FileStream m_Stream;
            readonly string m_Path;
            public LockHandle(FileStream iStream, string iPath) { m_Stream = iStream; m_Path = iPath; }

            public void Dispose()
            {
                try { m_Stream.Dispose(); } catch (Exception) { }
                // 正常／例外路徑都要刪 —— 留著就是下一個人的 stale lock（而他只會看到「被占用」）
                try { if (File.Exists(m_Path)) File.Delete(m_Path); } catch (Exception) { }
            }
        }

        // ───────────────────────────── 事件檔 ─────────────────────────────

        /// <summary>
        /// 寫事件檔（append-only，schema 與檔名與 python 同形）。
        /// <para>檔名 <c>&lt;HHmmss&gt;_&lt;ms&gt;_&lt;uuid&gt;.json</c> —— 帶毫秒是為了把同秒碰撞
        /// 壓到「同毫秒 ＋ 同 6 hex」才會撞，否則就是 silent overwrite 丟事件。</para>
        /// </summary>
        public static string WriteEvent(SCP_CanvasPaths iPaths, DateTime iNowUtc, string iUuid,
                                        string iPersona, string iAgent, string iAccount,
                                        List<SCP_CanvasPixel> iPixels, SCP_CanvasPayPlan iPlan,
                                        List<string> iLedgerRefs)
        {
            SCP_JsonData aEvent = SCP_JsonData.NewObject();
            aEvent["ts"] = SCP_CanvasEvents.IsoMs(iNowUtc);
            aEvent["uuid"] = iUuid;
            aEvent["persona"] = iPersona;
            aEvent["agent"] = iAgent;
            aEvent["account_id"] = iAccount;
            SCP_JsonData aPixels = SCP_JsonData.NewArray();
            foreach (SCP_CanvasPixel aP in iPixels)
            {
                SCP_JsonData aPx = SCP_JsonData.NewObject();
                aPx["x"] = aP.X; aPx["y"] = aP.Y; aPx["color"] = aP.ColorIndex;
                aPixels.Add(aPx);
            }
            aEvent["pixels"] = aPixels;
            aEvent["cost"] = iPixels.Count;
            SCP_JsonData aBreakdown = SCP_JsonData.NewObject();
            // ⚠ key 名 freetime / voucher 是**事件檔的既有 schema**（改了弄壞既有事件與 replay）——
            //   顯示名（限時券／永久券）在人讀那一行對映，不動 key。
            aBreakdown["freetime"] = iPlan.Expiring;
            aBreakdown["voucher"] = iPlan.Permanent;
            aBreakdown["token"] = iPlan.Token;
            aEvent["pay_breakdown"] = aBreakdown;
            SCP_JsonData aRefs = SCP_JsonData.NewArray();
            foreach (string aRef in iLedgerRefs) aRefs.Add(SCP_JsonData.NewString(aRef));
            aEvent["ledger_refs"] = aRefs;

            string aDateDir = iNowUtc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            string aName = iNowUtc.ToString("HHmmss", CultureInfo.InvariantCulture) + "_"
                           + iNowUtc.Millisecond.ToString("000", CultureInfo.InvariantCulture) + "_"
                           + iUuid + ".json";
            string aDir = iPaths.Events + "/" + aDateDir;
            Directory.CreateDirectory(aDir);
            string aPath = aDir + "/" + aName;
            File.WriteAllText(aPath, SCP_JsonWriter.Write(aEvent, true), new UTF8Encoding(false));
            return aPath;
        }

        /// <summary>6 hex 的事件 uuid（與 python <c>secrets.token_hex(3)</c> 同形）。</summary>
        public static string NewUuid()
        {
            var aBytes = new byte[3];
            using (var aRng = System.Security.Cryptography.RandomNumberGenerator.Create()) aRng.GetBytes(aBytes);
            var aSb = new StringBuilder(6);
            foreach (byte b in aBytes) aSb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return aSb.ToString();
        }
    }
}
