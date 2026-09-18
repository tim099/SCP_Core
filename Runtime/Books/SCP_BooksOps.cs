// 區塊職責：書店三個**會動錢的動作**的本體 —— 捐贈 / 發表 / 打賞（＋補發打賞券）。
// 物理意義：2026-09-18（TASK-0166 ①）從 Unity 的 `UCL_BooksIO` 搬過來。
//           搬得動的前提是今天才成立的：**錢有了跨宿主的入口**
//           （`senate cmd bank --arg op=pay`，Tim 同日拍板「金流全面改串新銀行」）——
//           在那之前這幾支綁死在 Editor 的 `UCL_TreasuryLedger` 上，搬出去就沒有錢可動。
// 數值影響：寫 `Books/<slug>/_donation.json` 與 `Books/tips/<ts>_<persona>_<tipId>.json`；
//           錢與券由 <see cref="SCP_IBooksGateway"/> 動，⛔ 本層自己一毛都不碰。
//
// 🩸 判準（每一條都是從 Unity 那版帶過來的血證，⛔ 不是重新設計）：
//   ① **帳與券分開結算**：打賞的 debit 落帳後，任一券發放失敗 **不回滾帳**（帳不可造假），
//      改記 `voucher_status: pending_*`，之後 `op=tip --arg retry=1` 補發。
//   ② **`publish` 要回寫草稿 store 的兩個狀態欄**。少了它，「已經在藏書架上」與「還在寫」
//      會同時為真，而**兩邊都不報錯**（TASK-0148 那次就是搬家時跟著 python 一起消失的）。
//   ③ **權限只問「這本是不是館內自產」**（看 `origin`，⛔ 不看 legacy `source`）——
//      舊版看 source 導致 `watch-log` 的觀影實錄被判成捐贈調入而永遠無法再版。
//   ④ **正典格式由 SCP 這支 writer 決定**（Tim 2026-09-11）：兩份 formatter 保持同步靠的是
//      「記得注意」，而那是修法優先序裡最後一級。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Io;
using SCP.Core.Json;

namespace SCP.Core.Books
{
    public static class SCP_BooksOps
    {
        public const string Key_DonorAgent = "donor_agent";
        public const string Key_BasePrice = "base_price";
        public const string Key_VoucherStatus = "voucher_status";
        public const string Key_TipId = "tip_id";

        /// <summary>打賞的券匯率：1 token 換 1 張繪圖券 ＋ 1 張酒館券。</summary>
        public const int TipCanvasRate = 1;
        public const int TipTavernRate = 1;
        public const int TipMax = 1000;
        public const int DonationBasePrice = 100;

        public const string VoucherCanvas = "canvas";
        public const string VoucherTavern = "tavern";

        // ===========================================================
        // 捐贈 —— 真金白銀調一本外部的書進共享圖書館。
        // ⚠ 同一本書**不重捐**（要再給錢走打賞）。
        // ===========================================================
        public static string? Donate(string iDataRoot, SCP_IBooksGateway iGate, string iBook,
                                     string iDonorBank, string iDonorPersona, string iDonorAgent,
                                     int iTokens, string iNote,
                                     out string? oBroadcastBody, out string? oError)
        {
            oBroadcastBody = null;
            oError = null;
            if (!Directory.Exists(SCP_BooksDonations.BookDir(iDataRoot, iBook)))
            { oError = $"Books/{iBook}/ 不存在 —— 先把書放進 AgentCommands/Books/{iBook}/"; return null; }

            string aPath = SCP_BooksDonations.DonationPath(iDataRoot, iBook);
            if (File.Exists(aPath))
            {
                SCP_JsonData? aExisting = LoadJson(aPath, out _);
                string aWho = aExisting != null
                    ? aExisting.GetString(SCP_BooksDonations.Key_DonorPersona,
                                          aExisting.GetString(SCP_BooksDonations.Key_Donor, "?"))
                    : "?";
                oError = $"《{iBook}》已被捐贈 —— 捐贈者 {aWho}。同書不重捐；要打賞走 op=tip。";
                return null;
            }

            string aTitle = iBook;   // Books/ 沒有 metadata 檔，標題以 slug 為底、可由 note 補充人話

            // 真金白銀：餘額不足／帳戶隔離違規會 throw ⇒ 不寫任何登記。
            (int aVoucher, int aToken) = PayOrDebit(iGate, iDonorBank, iDonorPersona, iTokens,
                "book_donation", iBook,
                $"捐贈圖書: {aTitle} (donor={(string.IsNullOrEmpty(iDonorPersona) ? iDonorBank : iDonorPersona)})",
                $"book_donation_{iBook}");

            var aEntry = SCP_JsonData.NewObject();
            aEntry.Set(SCP_BooksDonations.Key_Book, SCP_JsonData.NewString(iBook));
            aEntry.Set(SCP_BooksDonations.Key_Title, SCP_JsonData.NewString(aTitle));
            aEntry.Set(SCP_BooksDonations.Key_Donor, SCP_JsonData.NewString(iDonorBank));
            aEntry.Set(SCP_BooksDonations.Key_DonorPersona, SCP_JsonData.NewString(iDonorPersona ?? ""));
            aEntry.Set(Key_DonorAgent, SCP_JsonData.NewString(iDonorAgent ?? ""));
            aEntry.Set(SCP_BooksDonations.Key_Tokens, SCP_JsonData.NewNumber(iTokens));
            // ⚠ `tokens` 是**消費額**，⛔ 不是「從帳戶扣了多少」—— 兩個數字不寫出來的話，
            //   對帳的人會去找那幾個不見的 token（2026-09-18 實測：捐 20 而帳本只扣 8）。
            aEntry.Set("paid_voucher", SCP_JsonData.NewNumber(aVoucher));
            aEntry.Set("paid_token", SCP_JsonData.NewNumber(aToken));
            aEntry.Set(Key_BasePrice, SCP_JsonData.NewNumber(DonationBasePrice));
            aEntry.Set(SCP_BooksDonations.Key_DonatedAt, SCP_JsonData.NewString(Today()));
            aEntry.Set(SCP_BooksDonations.Key_Note, SCP_JsonData.NewString(iNote ?? ""));
            Stamp(aEntry, SCP_BookOrigin.Donated, SCP_BookKind.External, "", 0);
            SaveJson(aPath, aEntry);

            string aBy = string.IsNullOrEmpty(iDonorPersona) ? iDonorBank : iDonorPersona;
            oBroadcastBody = $"📚 新書入庫!\n\n《{aTitle}》由 **{aBy}** 捐贈進共享圖書館（{iTokens} token），全員都能讀了。\n"
                             + $"全文在 AgentCommands/Books/{iBook}/。";
            return $"✅ 捐贈完成:《{aTitle}》→ 捐贈者 {aBy}（{iTokens} token）。全員可讀。";
        }

        // ===========================================================
        // 發表原創書（Author-as-Donor）—— 免費入庫、作者署名、連載可重複發表。
        // ⚠ 判準③：權限只問 `origin`，⛔ 不看 legacy `source`。
        // ===========================================================
        public static string? Publish(string iDataRoot, SCP_IBooksGateway iGate, string iBook,
                                      string iDonorBank, string iAuthorPersona, string iDonorAgent,
                                      string? iTitle, string? iNote,
                                      out string? oBroadcastBody, out string? oError)
        {
            oBroadcastBody = null;
            oError = null;
            string aDir = SCP_BooksDonations.BookDir(iDataRoot, iBook);
            if (!Directory.Exists(aDir))
            { oError = $"Books/{iBook}/ 不存在 —— 先寫至少一章全文再 publish"; return null; }
            int aChapters = Directory.GetFiles(aDir, "*.txt").Length;
            if (aChapters == 0)
            { oError = $"Books/{iBook}/ 沒有任何章節（*.txt）—— 空書不入庫"; return null; }

            string aPath = SCP_BooksDonations.DonationPath(iDataRoot, iBook);
            SCP_JsonData? aExisting = File.Exists(aPath) ? LoadJson(aPath, out _) : null;
            bool aWasPublished = aExisting != null;
            if (aExisting != null)
            {
                if (OriginOf(aExisting, iBook) == SCP_BookOrigin.Donated)
                {
                    oError = $"《{iBook}》已以捐贈調入登記（捐贈者 "
                             + $"{aExisting.GetString(SCP_BooksDonations.Key_DonorPersona, "?")}）"
                             + " —— publish 只發布館內自產的書";
                    return null;
                }
                string aRegistered = aExisting.GetString(SCP_BooksDonations.Key_DonorPersona, "");
                if (aRegistered.Length > 0 && aRegistered != iAuthorPersona)
                {
                    oError = $"《{iBook}》登記作者是 {aRegistered}，與本次 persona={iAuthorPersona} 不符"
                             + " —— 不得以 publish 改寫作者署名";
                    return null;
                }
                if (string.IsNullOrEmpty(iTitle)) iTitle = aExisting.GetString(SCP_BooksDonations.Key_Title, iBook);
                if (string.IsNullOrEmpty(iNote)) iNote = aExisting.GetString(SCP_BooksDonations.Key_Note, "");
            }
            if (string.IsNullOrEmpty(iTitle))
            { oError = "首次發表需要 title（Books/ 沒有 metadata 檔可推導 —— 名字要作者自己給）"; return null; }

            var aEntry = SCP_JsonData.NewObject();
            aEntry.Set(SCP_BooksDonations.Key_Book, SCP_JsonData.NewString(iBook));
            aEntry.Set(SCP_BooksDonations.Key_Title, SCP_JsonData.NewString(iTitle!));
            aEntry.Set(SCP_BooksDonations.Key_Donor, SCP_JsonData.NewString(iDonorBank));
            aEntry.Set(SCP_BooksDonations.Key_DonorPersona, SCP_JsonData.NewString(iAuthorPersona));
            aEntry.Set(Key_DonorAgent, SCP_JsonData.NewString(iDonorAgent ?? ""));
            aEntry.Set(SCP_BooksDonations.Key_Tokens, SCP_JsonData.NewNumber(0));
            aEntry.Set(Key_BasePrice, SCP_JsonData.NewNumber(0));
            // ⚠ legacy `source` 不再寫出（2026-09-04）：entry 是全新的 ⇒ 不寫即不存在；
            //   舊檔留著的 source 照讀不動（`DeriveOrigin` 仍認它），只是不再新增。
            Stamp(aEntry, SCP_BookOrigin.Authored,
                  KindOf(aExisting, iBook), SeriesOf(aExisting, iBook), VolumeOf(aExisting));
            aEntry.Set(SCP_BooksDonations.Key_Chapters, SCP_JsonData.NewNumber(aChapters));
            aEntry.Set(SCP_BooksDonations.Key_DonatedAt, SCP_JsonData.NewString(
                aExisting != null ? aExisting.GetString(SCP_BooksDonations.Key_DonatedAt, Today()) : Today()));
            aEntry.Set(SCP_BooksDonations.Key_PublishedAt, SCP_JsonData.NewString(Today()));
            aEntry.Set(SCP_BooksDonations.Key_Note, SCP_JsonData.NewString(
                string.IsNullOrEmpty(iNote) ? $"{iAuthorPersona} 原創著作" : iNote!));
            SaveJson(aPath, aEntry);

            string aDraftNote = SyncAuthoredDraftState(iDataRoot, iBook);

            string aVerb = aWasPublished ? "連載更新" : "發表";
            oBroadcastBody = $"✍📖 新書{aVerb}!\n\n《{iTitle}》由 **{iAuthorPersona}** 原創著作（{aChapters} 章，免費入庫），全員可讀。\n"
                             + $"全文在 AgentCommands/Books/{iBook}/。";

            // 📮 續寫包投遞 —— ⚠ 非致命：書已經登記了，失敗只在回報裡多一行。
            string? aDossier = iGate.DeliverDossier(iBook, iAuthorPersona, aEntry, out string aDossierErr);
            string aDossierLine = aDossier ?? $"⚠ 續寫包投遞失敗（書已入庫，不影響發表）：{aDossierErr}";

            return $"✅ {(aWasPublished ? "更新連載" : "首度發表")}原創書:《{iTitle}》 by {iAuthorPersona}"
                   + $"（{aChapters} 章，免費入庫）\n{aDraftNote}\n{aDossierLine}";
        }

        // ===========================================================
        // 打賞 —— 讀者燒 token，受益 persona 收雙券（繪圖券＋酒館券，1+1）。
        // ⚠ 判準①：帳與券分開結算，券發不出去記 pending，⛔ 不回滾帳。
        // ===========================================================
        public static string? Tip(string iDataRoot, SCP_IBooksGateway iGate, string iBook,
                                  string iTipperBank, string iTipperPersona, string iTipperAgent,
                                  int iTokens, string iNote,
                                  out string? oBroadcastBody, out string? oError)
        {
            oBroadcastBody = null;
            oError = null;
            if (iTokens < 1 || iTokens > TipMax)
            { oError = $"tokens 須為 1~{TipMax}（傳入 {iTokens}）"; return null; }

            SCP_JsonData? aBen = ResolveBeneficiary(iDataRoot, iBook);
            if (aBen == null)
            { oError = $"《{iBook}》不在捐贈登記簿 —— 未入庫的書不可打賞（先 donate / publish）"; return null; }

            string aBenBank = aBen.GetString(SCP_BooksDonations.Key_Donor, "");
            string aBenPersona = aBen.GetString(SCP_BooksDonations.Key_DonorPersona, "");
            string aTitle = aBen.GetString(SCP_BooksDonations.Key_Title, iBook);
            string aBenKind = OriginOf(aBen, iBook) == SCP_BookOrigin.Authored ? "作者" : "捐贈者";
            if (aBenPersona.Length == 0)
            { oError = $"《{aTitle}》登記簿缺 donor_persona —— 無法定位受益 persona"; return null; }
            if (iTipperPersona == aBenPersona)
            { oError = $"自賞禁止 —— 《{aTitle}》的{aBenKind}就是 {iTipperPersona} 本人"; return null; }

            string aTipId = Guid.NewGuid().ToString("N").Substring(0, 8);
            string aRef = $"tip:{iBook}:{aTipId}";

            (int aPaidVoucher, int aPaidToken) = PayOrDebit(iGate, iTipperBank, iTipperPersona, iTokens,
                "book_tip", aRef, $"打賞圖書: {aTitle} ({iTipperPersona} → {aBenPersona})",
                $"book_tip_{aTipId}");

            var aEntry = SCP_JsonData.NewObject();
            aEntry.Set(SCP_BooksDonations.Key_Book, SCP_JsonData.NewString(iBook));
            aEntry.Set(SCP_BooksDonations.Key_Title, SCP_JsonData.NewString(aTitle));
            aEntry.Set("tipper", SCP_JsonData.NewString(iTipperBank));
            aEntry.Set("tipper_persona", SCP_JsonData.NewString(iTipperPersona));
            aEntry.Set("tipper_agent", SCP_JsonData.NewString(iTipperAgent ?? ""));
            aEntry.Set("beneficiary", SCP_JsonData.NewString(aBenBank));
            aEntry.Set("beneficiary_persona", SCP_JsonData.NewString(aBenPersona));
            aEntry.Set(SCP_BooksDonations.Key_TokensSpent, SCP_JsonData.NewNumber(iTokens));
            aEntry.Set("paid_voucher", SCP_JsonData.NewNumber(aPaidVoucher));
            aEntry.Set("paid_token", SCP_JsonData.NewNumber(aPaidToken));
            var aVouchers = SCP_JsonData.NewObject();
            aVouchers.Set(VoucherCanvas, SCP_JsonData.NewNumber(iTokens * TipCanvasRate));
            aVouchers.Set(VoucherTavern, SCP_JsonData.NewNumber(iTokens * TipTavernRate));
            aEntry.Set("vouchers", aVouchers);
            aEntry.Set(Key_TipId, SCP_JsonData.NewString(aTipId));
            aEntry.Set(Key_VoucherStatus, SCP_JsonData.NewString("pending_all"));
            aEntry.Set(SCP_BooksDonations.Key_Note, SCP_JsonData.NewString(iNote ?? ""));
            aEntry.Set("tipped_at", SCP_JsonData.NewString(Today()));

            aEntry.Set(Key_VoucherStatus, SCP_JsonData.NewString(IssueTipVouchers(iGate, aEntry)));
            WriteTip(iDataRoot, aEntry);

            string aNotePart = string.IsNullOrEmpty(iNote) ? "" : $"「{iNote}」";
            oBroadcastBody = $"💰 打賞! **{iTipperPersona}** 打賞《{aTitle}》 {iTokens} token → @{aBenPersona}（{aBenKind}）"
                             + $"收 繪圖券×{iTokens * TipCanvasRate} + 酒館券×{iTokens * TipTavernRate} {aNotePart}";
            return $"✅ 打賞完成:《{aTitle}》 {iTokens} token → {aBenPersona}"
                   + $"（券狀態 {aEntry.GetString(Key_VoucherStatus, "?")}）";
        }

        /// <summary>補發 pending 的打賞券（判準①的另一半 —— 帳不回滾，券可以重來）。</summary>
        public static string RetryPendingTips(string iDataRoot, SCP_IBooksGateway iGate)
        {
            List<SCP_JsonData> aTips = SCP_BooksDonations.LoadTips(iDataRoot);
            var aSb = new StringBuilder();
            int aPending = 0, aFixed = 0;
            foreach (SCP_JsonData aTip in aTips)
            {
                if (aTip.GetString(Key_VoucherStatus, "") == "issued") continue;
                aPending++;
                string aNext = IssueTipVouchers(iGate, aTip);
                aTip.Set(Key_VoucherStatus, SCP_JsonData.NewString(aNext));
                WriteTip(iDataRoot, aTip);
                if (aNext == "issued") aFixed++;
                aSb.AppendLine($"- 《{aTip.GetString(SCP_BooksDonations.Key_Title, aTip.GetString(SCP_BooksDonations.Key_Book, "?"))}》"
                               + $" tip {aTip.GetString(Key_TipId, "?")} → {aNext}");
            }
            if (aPending == 0) return "（沒有 pending 的打賞券要補發）";
            aSb.AppendLine($"\n補發 {aFixed}/{aPending} 筆完成");
            return aSb.ToString();
        }

        // ===========================================================
        // 券發放：任一路失敗記 pending。⚠ 冪等由 gateway 那側的 ref 保證（同 ref 已發過視為成功）。
        // ===========================================================
        static string IssueTipVouchers(SCP_IBooksGateway iGate, SCP_JsonData iEntry)
        {
            string aPersona = iEntry.GetString("beneficiary_persona", "");
            string aTipId = iEntry.GetString(Key_TipId, "");
            string aRef = $"tip:{iEntry.GetString(SCP_BooksDonations.Key_Book, "?")}:{aTipId}";
            string aStatus = iEntry.GetString(Key_VoucherStatus, "pending_all");
            bool aCanvasOk = aStatus == "pending_tavern" || aStatus == "issued";
            bool aTavernOk = aStatus == "pending_canvas" || aStatus == "issued";

            SCP_JsonData aV = iEntry["vouchers"];
            int aCanvasAmt = aV.Exists ? aV.GetInt(VoucherCanvas, 0) : 0;
            int aTavernAmt = aV.Exists ? aV.GetInt(VoucherTavern, 0) : 0;
            if (aCanvasAmt <= 0 && aTavernAmt <= 0)
            {
                iGate.Warn($"[BooksOps] tip {aTipId} 缺 vouchers 欄 —— 無券可發，維持原 status");
                return aStatus;
            }
            if (!aCanvasOk)
            {
                try { iGate.GrantVoucher(aPersona, VoucherCanvas, aCanvasAmt, "book_tip", aRef); aCanvasOk = true; }
                catch (Exception e) { iGate.Warn($"[BooksOps] 繪圖券發放失敗（記 pending 可 retry）：{e.Message}"); }
            }
            if (!aTavernOk)
            {
                try { iGate.GrantVoucher(aPersona, VoucherTavern, aTavernAmt, "book_tip", aRef); aTavernOk = true; }
                catch (Exception e) { iGate.Warn($"[BooksOps] 酒館券發放失敗（記 pending 可 retry）：{e.Message}"); }
            }
            if (aCanvasOk && aTavernOk) return "issued";
            if (aCanvasOk) return "pending_tavern";
            if (aTavernOk) return "pending_canvas";
            return "pending_all";
        }

        // ===========================================================
        // 付款：主動消費走 `pay`（自動先扣酒館券）。
        // 🩸 錢包綁 persona，而捐贈這一支的 persona **可以是空的**（舊呼叫端只給 bank）
        //   ⇒ 沒有 persona 就沒有錢包可以扣。那時走純 token，⛔ 但**要出聲**：
        //   「沒有錢包所以沒扣券」與「有錢包而這條路沒生效」在帳面上一模一樣。
        // ===========================================================
        static (int Voucher, int Token) PayOrDebit(SCP_IBooksGateway iGate, string iBank, string iPersona,
                                                   int iTokens, string iKind, string iRef,
                                                   string iDescription, string iIdemKey)
        {
            if (string.IsNullOrEmpty(iPersona))
                iGate.Warn($"[BooksOps] {iKind}：沒有 persona ⇒ **定位不到錢包**，本筆走純 token"
                           + $"（{iBank} -{iTokens}）。⛔ 這不是「他沒有券」，是我不知道去問誰的券。");
            return iGate.Pay(iBank, iPersona ?? "", iTokens, iKind, iRef, iDescription, iIdemKey);
        }

        // ===========================================================
        // 判準②：發表之後把草稿 store 的兩個狀態欄推到已發布。
        // ⚠ 刻意**不建檔** —— 草稿 store 的擁有權在寫書流程那側。
        // ===========================================================
        static string SyncAuthoredDraftState(string iDataRoot, string iBook)
        {
            string aPath = Path.Combine(iDataRoot, "BookNotes", iBook, "book.json");
            if (!File.Exists(aPath))
                return $"· 草稿狀態：略過（`BookNotes/{iBook}/book.json` 不存在 —— 這本沒有草稿檔）";

            SCP_JsonData? aData = LoadJson(aPath, out string? aError);
            if (aData == null) return $"⚠ 草稿狀態未同步（書已入庫，不影響發表）：{aError}";

            string aBeforePublish = aData.GetString("publish_status", "");
            string aBeforeStatus = aData.GetString("status", "");
            if (aBeforePublish == "published" && aBeforeStatus == "reading")
                return "· 草稿狀態：本來就是 `published`/`reading` ⇒ 沒有寫入（冪等）";

            aData.Set("publish_status", SCP_JsonData.NewString("published"));
            aData.Set("status", SCP_JsonData.NewString("reading"));
            try { SaveJson(aPath, aData); }
            catch (Exception e) { return $"⚠ 草稿狀態寫入失敗（書已入庫，不影響發表）：{e.Message}"; }
            return $"· 草稿狀態已同步：`publish_status` {Show(aBeforePublish)} → `published`、"
                   + $"`status` {Show(aBeforeStatus)} → `reading`";

            static string Show(string v) => string.IsNullOrEmpty(v) ? "(空)" : "`" + v + "`";
        }

        // ── 小工具 ────────────────────────────────────────────────

        static SCP_JsonData? ResolveBeneficiary(string iDataRoot, string iBook)
        {
            foreach (SCP_JsonData aD in SCP_BooksDonations.LoadDonations(iDataRoot))
                if (aD.GetString(SCP_BooksDonations.Key_Book, "") == iBook) return aD;
            return null;
        }

        static void WriteTip(string iDataRoot, SCP_JsonData iEntry)
        {
            string aDir = SCP_BooksDonations.TipsDir(iDataRoot);
            Directory.CreateDirectory(aDir);
            string aTipId = iEntry.GetString(Key_TipId, "");
            string? aPath = null;
            if (aTipId.Length > 0)
            {
                string[] aHits = Directory.GetFiles(aDir, $"*_{aTipId}.json");
                if (aHits.Length > 0) aPath = aHits[0];
            }
            if (aPath == null)
            {
                string aStamp = DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffffff", CultureInfo.InvariantCulture) + "Z";
                aPath = Path.Combine(aDir, $"{aStamp}_{SafeSlug(iEntry.GetString("tipper_persona", "unknown"))}_{aTipId}.json");
            }
            SaveJson(aPath, iEntry);
        }

        static void Stamp(SCP_JsonData ioEntry, SCP_BookOrigin iOrigin, SCP_BookKind iKind,
                          string iSeries, int iVolume)
        {
            ioEntry.Set(SCP_BooksClassification.Key_Origin,
                        SCP_JsonData.NewString(SCP_BooksClassification.ToKey(iOrigin)));
            ioEntry.Set(SCP_BooksClassification.Key_Kind,
                        SCP_JsonData.NewString(SCP_BooksClassification.ToKey(iKind)));
            ioEntry.Set(SCP_BooksClassification.Key_Series, SCP_JsonData.NewString(iSeries ?? ""));
            ioEntry.Set(SCP_BooksClassification.Key_Volume, SCP_JsonData.NewNumber(iVolume));
        }

        static SCP_BookOrigin OriginOf(SCP_JsonData? iEntry, string iSlug)
            => SCP_BooksClassification.DeriveOrigin(
                iEntry?.GetString(SCP_BooksClassification.Key_Origin, ""),
                iEntry?.GetString(SCP_BooksClassification.Key_Source, ""));

        static SCP_BookKind KindOf(SCP_JsonData? iEntry, string iSlug)
            => SCP_BooksClassification.DeriveKind(
                iEntry?.GetString(SCP_BooksClassification.Key_Kind, ""),
                iEntry?.GetString(SCP_BooksClassification.Key_Source, ""),
                iEntry?.GetString(SCP_BooksClassification.Key_Origin, ""),
                iSlug);

        static string SeriesOf(SCP_JsonData? iEntry, string iSlug)
            => SCP_BooksClassification.DeriveSeries(
                iEntry?.GetString(SCP_BooksClassification.Key_Series, ""), iSlug);

        static int VolumeOf(SCP_JsonData? iEntry)
            => iEntry?.GetInt(SCP_BooksClassification.Key_Volume, 0) ?? 0;

        static string Today() => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        static string SafeSlug(string iValue)
        {
            if (string.IsNullOrEmpty(iValue)) return "unknown";
            var aSb = new StringBuilder();
            foreach (char c in iValue)
                aSb.Append(char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-' ? c : '_');
            string aOut = aSb.ToString();
            return aOut.Length > 40 ? aOut.Substring(0, 40) : (aOut.Length == 0 ? "unknown" : aOut);
        }

        static SCP_JsonData? LoadJson(string iPath, out string? oError)
        {
            oError = null;
            if (!File.Exists(iPath)) { oError = $"檔案不存在：{iPath}"; return null; }
            try
            {
                SCP_JsonData aD = SCP_JsonParser.Parse(File.ReadAllText(iPath, Encoding.UTF8));
                if (!aD.IsObject) { oError = $"不是 JSON 物件：{iPath}"; return null; }
                return aD;
            }
            catch (Exception e) { oError = $"JSON 解析失敗（{iPath}）：{e.Message}"; return null; }
        }

        // ⚠ 判準④：正典格式由**這一支** writer 決定。
        static void SaveJson(string iPath, SCP_JsonData iData)
            => SCP_TextFile.WriteCrLf(iPath, SCP_JsonWriter.Write(iData, iIndented: true, iIndent: "  ") + "\n");
    }
}
