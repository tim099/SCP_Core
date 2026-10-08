// 區塊職責：`cmd plurk` —— Plurk 共用帳號流程的入口（TASK-0362）。
// 物理意義：邏輯本體在 `Runtime/Plurk/`（SCP_PlurkOps / SCP_PlurkLint / SCP_PlurkAccounts / SCP_PlurkApi）；
//          本檔只做「參數 → 根 → 派遣 → 落回傳檔」。
//          HTTP 走宿主注入的 `ISCP_HttpFormRequester`（SCP_Core 不碰網路）；宿主沒注入 ⇒ 要連網的 op 會大聲失敗。
// 數值影響：回傳檔 `letters/<persona 或 basecamp>/cmd/plurk_<op>.md` ——
//          **成功或失敗都寫**（見 Execute 那段血證）。對外寫入的 op 一律要 `confirm=1`。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using SCP.Core.Letters;
using SCP.Core.Paths;
using SCP.Core.Plurk;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Plurk : SCP_Cmd
    {
        public override string Name => "plurk";
        public override string Category => SCP_CmdCategory.Reading;

        public override string Summary =>
            "Plurk 共用帳號流程：resolve 查帳號 / lint 驗交付單 / preview 組 payload 不送 / post 發文（需 confirm=1）"
            + " / timeline·responses·friends 看別人在說什麼（唯讀）/ mentions 誰 @ 了我＋我回了沒（唯讀，優先處理）/ like·unlike 互動（需 confirm=1）"
            + " / 擴圈：profile·expand·search 唯讀、alerts（讀了就清通知），befriend·unfriend·follow·unfollow·accept·deny 需 confirm=1"
            + " / 表情：emoticons 讀表並維護本地描述表（唯讀＋寫本地），emoadd 試新增自訂表情（需 confirm=1）";

        public override string Details =>
            "op（預設 resolve）：" + string.Join(" | ", SCP_PlurkOps.Ops) + "\n"
            + "· 發文三路 lint／preview／post 都吃 `slip_file`（交付單**五欄**：persona／心情詞／文案本體／圖片路徑／公開度，"
            + "格式見 `senate cmd doc --arg op=show --arg name=Plurk_Posting` §2）；起手先把缺 nick 的帳號自動補齊，`@persona` 自動轉成 `@nick[→persona]`。\n"
            + "· post **預設 dry-run**：沒帶 `confirm=1` 只印 payload 不送；lint 有錯一律拒絕；送出後寫 audit ＋ 回讀查重複。\n"
            + "· 唯讀 op 預設**現抓 API**並落本地快取；`cache=1` 才改讀快取（回傳檔會標來源與年齡）。\n"
            + "· ⚠ `alerts`（getActive）**讀了就清通知** —— 要看歷史走 `history=1`。\n"
            + "· 對外寫入（post／upload／like／unlike／emoadd／befriend／unfriend／follow／unfollow／accept／deny）一律要 `confirm=1`。\n"
            + "· 回傳檔：`letters/<persona 或 basecamp>/cmd/plurk_<op>.md`（失敗也寫 —— 報告是診斷）。\n"
            + "exit：0 成功／2 被擋（lint、缺參數、帳號或憑證不可用）／1 API 或連線失敗。\n"
            + "機讀：`op`／`account`／`source` 恆有；whoami → `nick`／`user_id`；post → `sent`／`plurk_id`；"
            + "mentions → `pending`／`answered`；lint／preview／post → `lint_errors`／`lint_warns`／`budget`。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("plurk --arg op=preview --arg persona=basecamp --arg slip_file=D:/tmp/slip.txt");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("op", "要做什麼（" + string.Join(" | ", SCP_PlurkOps.Ops) + "）", iDefault: "resolve"),
            new SCP_CmdArgSpec("persona", "誰要發／誰在看（lint／preview／post 建議給 —— 決定用共用還是個人帳號；mentions 的室友路由也靠它）。沒給 ⇒ 回落共用帳號、回傳檔落 basecamp"),
            new SCP_CmdArgSpec("slip_file", "交付單檔案路徑（lint／preview／post 必填；**五欄**格式見 `senate cmd doc --arg op=show --arg name=Plurk_Posting` §2）"),
            new SCP_CmdArgSpec("confirm", "1 ＝ 真的送（post／upload／like／unlike／emoadd／關係動作）；沒帶＝dry-run 只印不送"),
            new SCP_CmdArgSpec("reply_to", "把這則發成該噗的回應（plurk id）—— 長文拆則的預設形態（preview／post）"),
            new SCP_CmdArgSpec("plurk_id", "已發出的噗 id（get／responses／like／unlike）"),
            new SCP_CmdArgSpec("image", "圖片絕對路徑（op=upload；⛔ 不吃相對路徑）"),
            new SCP_CmdArgSpec("limit", "筆數（timeline·mentions 預設 20／friends 預設 30／expand 每位好友預設 100；夾在 1-100）"),
            new SCP_CmdArgSpec("preview", "摘要字數（timeline／search 預設 90、mentions 預設 160；夾在 20-400）"),
            new SCP_CmdArgSpec("filter", "only_user | only_responded | only_private | only_favorite（timeline 選填；認不得的原樣送出讓對方回錯）"),
            new SCP_CmdArgSpec("from_response", "第幾則回應起（responses 選填，預設 0）"),
            new SCP_CmdArgSpec("user_id", "誰（profile／befriend／unfriend／follow／unfollow／accept／deny 必填；friends 選填 —— 不給就問 /APP/Users/me）"),
            new SCP_CmdArgSpec("offset", "第幾筆起（friends／search 選填）"),
            new SCP_CmdArgSpec("cache", "1 ＝ 改讀本地快取而不是現抓（**唯讀 op 才有意義**；回傳檔會標來源與年齡）"),
            new SCP_CmdArgSpec("query", "關鍵字（search 必填）"),
            new SCP_CmdArgSpec("kind", "plurk | user（search 選填；其他值會擋下，不靜默取預設）", iDefault: "plurk"),
            new SCP_CmdArgSpec("top", "列前幾名（expand 選填，預設 15）"),
            new SCP_CmdArgSpec("hops", "向外問幾位好友（expand 選填，預設 8）"),
            new SCP_CmdArgSpec("history", "1 ＝ 看歷史而不是待處理（alerts 選填；⚠ 待處理那支讀了就清）"),
            new SCP_CmdArgSpec("emo_desc", "<編號或URL片段=描述,…>（emoticons 選填：把描述寫進本地表，merge 不覆寫）"),
            new SCP_CmdArgSpec("url", "圖檔網址（emoadd 必填）"),
            new SCP_CmdArgSpec("alias", "表情代碼（emoadd 必填；⚠ Plurk 實測會忽略它自己編號）"),
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）—— senate CLI 沒給時用設定檔補上", iRequired: true),
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（絕對路徑）—— senate CLI 沒給時用設定檔補上", iRequired: true),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aOp = iArgs.Get("op").Trim().ToLowerInvariant();
            if (aOp.Length == 0) aOp = "resolve";
            string aPersona = iArgs.Get("persona").Trim();
            if (aPersona.Length > 0 && !SCP_Cmd_FreeTimeActivity.IsSafePersona(aPersona))
                return SCP_CmdResult.Fail(2, "✗ persona 不合法（不可含 `/` `\\` `:` 或是 `.` / `..`）：'" + aPersona + "'");

            var aCtx = new SCP_PlurkContext(iArgs.Get("data_root").Trim(), iArgs.Get("letters_root").Trim());
            var aLetters = new SCP_LettersRoot(aCtx.LettersRoot);
            // 回傳檔位置：`letters/<p>/cmd/plurk_<op>.md`（沒給 persona ⇒ basecamp）
            string aPath = SCP_LettersPaths.CmdPayload(aLetters, aPersona.Length == 0 ? "basecamp" : aPersona, "plurk", aOp);

            SCP_PlurkOps? aOps = null;
            SCP_CmdResult aRes;
            // ===========================================================
            // 區塊職責：不論成功或失敗，**回傳檔都要寫出來**
            // 🩸 2026-08-21：`op=lint` 擋下時我直接 throw，而寫檔在 switch 之後 ⇒
            //   錯誤訊息說「詳見回傳檔」，**而那個回傳檔從來沒被寫出來**。
            //   指路牌指向一個不存在的東西 —— 而 Cmd 本身「正確地失敗了」，所以沒有任何一層會喊。
            // ⇒ 判準：報告是**診斷**，失敗的時候比成功的時候更需要它。
            //   例外一律在 catch 收成 Fail，
            //   落檔放在 catch **之後**無條件執行 —— 等價於 finally，而且 exit code 由失敗種類決定。
            // ===========================================================
            try
            {
                aOps = new SCP_PlurkOps(aCtx, aOp, aPersona, k => iArgs.Get(k));
                aOps.Execute();
                aRes = SCP_CmdResult.Success("✓ plurk op=" + aOp + "（帳號 " + (aOps.Resolution.SecretId.Length == 0 ? "(無)" : aOps.Resolution.SecretId) + "）");
            }
            catch (SCP_PlurkFailure f)
            {
                aRes = SCP_CmdResult.Fail(f.ExitCode, "✗ " + f.Message);
            }
            catch (Exception e)
            {
                aRes = SCP_CmdResult.Fail(1, "✗ " + e.GetType().Name + ": " + e.Message);
                aRes.Exception = e;
            }

            string aReport = aOps != null ? aOps.Report.ToString()
                : "# Plurk op=" + aOp + "\n\n- ⚠ 在解析帳號之前就失敗了（見 CLI 輸出）\n";
            try
            {
                SCP_CmdPayload.Write(aPath, aReport);
                aRes.AddOutput(aPath);
            }
            catch (Exception e)
            {
                aRes.Lines.Add("⚠ 回傳檔寫不進去（報告附在下面）：" + e.Message);
                aRes.Lines.Add(aReport);
                if (aRes.ExitCode == 0) aRes.ExitCode = 1;
            }

            if (aCtx.SecretsDirWarning != null) aRes.Lines.Add("⚠ secrets 設定：" + aCtx.SecretsDirWarning);
            if (aOps != null)
            {
                foreach (string aNotice in aOps.Notices) aRes.Lines.Add(aNotice);
                foreach (var kv in aOps.Values) aRes.AddValue(kv.Key, kv.Value);
            }
            return aRes;
        }
    }
}
