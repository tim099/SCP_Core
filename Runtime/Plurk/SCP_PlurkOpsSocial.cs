// 區塊職責：Plurk 的 op 本體（第二份：社交面）—— timeline / responses / mentions / friends / like·unlike /
//          profile / expand / search / alerts / befriend·unfriend·follow·unfollow·accept·deny，以及讀取層（API／本地快取）。
// 物理意義：TASK-0362，Unity `Cmd_Plurk` 社交段的搬家版（報告文字與血證逐段照搬；HTTP 換成宿主注入、同步呼叫）。
//          在這之前這支 Cmd 只有「送出」與「回讀自己那則」—— 也就是說它能發文，但**不能參與**。
//          而 Plurk 是雙向的：別人回了什麼、誰在講話，沒有入口就等於不存在。
// 數值影響：讀的那幾支對 Plurk 純唯讀，但會寫本地快取（`<資料根>/Plurk/cache/`，⛔ 不入 git）與表情共用表；
//          ⚠ `op=alerts`（getActive）**讀了就清通知**（見 OpAlerts 的血證）；
//          like／關係動作改 Plurk 狀態 ⇒ 一律 `confirm=1`。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using SCP.Core.Json;
using static SCP.Core.Plurk.SCP_PlurkJson;

namespace SCP.Core.Plurk
{
    public sealed partial class SCP_PlurkOps
    {
        // ===========================================================
        // 區塊職責：**唯讀的社交面** —— 看好友在說什麼、看一則噗底下的回應、看好友清單。
        // 數值影響：純唯讀，不改任何 Plurk 資料。可選寫一份本地快取（見 CacheDir，⛔ 不入 git）。
        // ⚠ 預設**一律打 API**，`--arg cache=1` 才吃快取 —— 反過來的話，
        //   「現況」與「三小時前的快照」在回傳檔上會長得一樣，而那正是這個 repo 最貴的錯誤形狀。
        // ===========================================================
        void OpTimeline()
        {
            var ioR = Report;
            var aCred = RequireCredentials();
            int aLimit = ParseIntArg("limit", 20, 1, 100);
            int aPreview = ParseIntArg("preview", 90, 20, 400);
            string aFilter = Arg("filter").Trim();

            var aParams = new Dictionary<string, string>
                { { "limit", aLimit.ToString(CultureInfo.InvariantCulture) } };
            // filter 是 Plurk 端的既有語彙（only_user / only_responded / only_private / only_favorite）；
            // 認不得的值我不猜、原樣送出 —— 讓對方回錯，而不是我這裡靜默丟掉它
            if (aFilter.Length > 0) aParams["filter"] = aFilter;

            string aBody = Fetch("timeline_" + (aFilter.Length == 0 ? "all" : aFilter),
                "/APP/Timeline/getPlurks", aParams, aCred);

            // 「哪一則是我自己發的」問一次就好 —— 不猜，也不寫死 id
            string aMeId = "";
            var (aMeSt, aMeBody) = Call("/APP/Users/me", aCred, null);
            if (aMeSt == 200) aMeId = PickJsonValue(aMeBody, "id") ?? "";

            ioR.AppendLine();
            ioR.AppendLine("## 河道（好友＋自己的噗）");
            var aRoot = SafeParse(aBody);
            var aPlurks = (aRoot != null && aRoot.Contains("plurks")) ? aRoot["plurks"] : null;
            var aUsers = (aRoot != null && aRoot.Contains("plurk_users")) ? aRoot["plurk_users"] : null;
            if (aPlurks == null || !aPlurks.IsArray)
            {
                ioR.AppendLine("- ⚠ 回應裡沒有 `plurks` 陣列 —— 這不是「沒有噗」，是**格式跟我預期的不一樣**。");
                ioR.AppendLine("- body（前 300 字）: " + Trunc(aBody, 300));
                return;
            }
            AddValue("count", aPlurks.Count.ToString(CultureInfo.InvariantCulture));
            ioR.AppendLine($"- **{aPlurks.Count}** 則（limit={aLimit}"
                + (aFilter.Length == 0 ? "" : $"　filter=`{aFilter}`")
                + $"　摘要 {aPreview} 字）"
                + (aMeId.Length == 0 ? "　⚠ 問不到自己的 id ⇒ 🪞 標記這一輪不可信" : ""));
            ioR.AppendLine();
            ioR.AppendLine("> 形狀取自酒館 catchup：**先短摘要掃一遍，再挑要細看的那幾則**。");
            ioR.AppendLine("> 摘要是「開頭 N 字」不是「首行」—— 首行可能只有兩個字，那掃不出東西。");
            var aEmoCtx = EmoBegin();
            ioR.AppendLine($"> 表情標註 `[emoN]⟨…⟩`：描述來自**共用表**"
                + $"（現有 {aEmoCtx.Table.Values.Count(r => r.Desc.Length > 0)}/{aEmoCtx.Table.Count} 張已描述）——"
                + "**描述一次、之後純文字查表，不必再抓圖**；沒描述的會被登記進待描述清單，"
                + "配不上時印 `?配不上` —— 不猜。");
            ioR.AppendLine();

            for (int i = 0; i < aPlurks.Count; i++)
            {
                var aP = aPlurks[i];
                string aId = JsonScalar(aP, "plurk_id");
                string aOwner = JsonScalar(aP, "owner_id");
                string aRaw = UnescapeJson(JsonScalar(aP, "content_raw"));
                // 表情反解析：`[emoN]` 是 per-account 別名 ⇒ 用同一筆的 HTML 按序配對出 URL
                aRaw = EmoAnnotatePaired(aRaw, UnescapeJson(JsonScalar(aP, "content")), aEmoCtx, aOwner);
                string aFlat = OneLine(aRaw).Trim();

                var aTags = new List<string>();
                if (aMeId.Length > 0 && aOwner == aMeId) aTags.Add("🪞我");
                if (aRaw.Contains("images.plurk.com") || UnescapeJson(JsonScalar(aP, "content")).Contains("<img"))
                    aTags.Add("🖼");
                if (aFlat.StartsWith("http", StringComparison.OrdinalIgnoreCase)) aTags.Add("🔗");
                string aRc = JsonScalar(aP, "response_count");
                string aFc = JsonScalar(aP, "favorite_count");

                ioR.AppendLine($"- **[{aId}]** {ShortTime(JsonScalar(aP, "posted"))} "
                    + $"**{UserName(aUsers, aOwner)}** «{JsonScalar(aP, "qualifier")}»"
                    + (aRc == "0" || aRc.Length == 0 ? "" : $" 💬{aRc}")
                    + (aFc == "0" || aFc.Length == 0 ? "" : $" ❤{aFc}")
                    + (aTags.Count == 0 ? "" : "　" + string.Join(" ", aTags)));
                ioR.AppendLine("    " + (aFlat.Length == 0 ? "(沒有文字內容)" : Trunc(aFlat, aPreview)));
            }

            ioR.AppendLine();
            ioR.AppendLine("### ▶ 挑一則細看／互動（id 抄上面那個）");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=get       --arg plurk_id=<id>                    # 全文（不是首行）");
            ioR.AppendLine("--arg op=responses --arg plurk_id=<id>                    # 底下的回應");
            ioR.AppendLine("--arg op=like      --arg plurk_id=<id> --arg confirm=1    # 按讚");
            ioR.AppendLine("--arg op=post --arg slip_file=<交付單> --arg reply_to=<id> --arg confirm=1   # 回應");
            ioR.AppendLine("```");
            ioR.AppendLine("⚠ 回應**走既有發文路**，刻意不另開一條短回應路 ——");
            ioR.AppendLine("　 兩條發文路就是兩套規則，而字數 lint 與末行署名只會套用在其中一條。");
            ioR.AppendLine("⚠ 摘要是**截斷過的**：要回應誰之前先 `op=get` 讀全文。");
            ioR.AppendLine("　 對著一段開頭講話，跟讀完再講，在對方那邊看起來完全不一樣。");
            EmoEnd(aEmoCtx);
        }

        // ===========================================================
        // 區塊職責：一則回應的**署名**是不是某位 persona —— 共用帳號下判「這則是誰回的」的唯一依據。
        // 物理意義：Plurk 只知道帳號；帳號裡是誰，只有末行 `—— <persona> <emoji>` 說得出來
        //          （SCP_PlurkLint ⑤：共用帳號末行署名是硬規則）。
        // 數值影響：只看**最後一個非空行**、比對 `—— <persona>`（破折號三種寫法，不分大小寫）。
        //          內文中段提到 persona 名不算 —— 那是在講她，不是她在講。
        // ===========================================================
        static string LastNonEmptyLine(string? iText)
        {
            var aLines = (iText ?? "").Replace("\r", "").Split('\n');
            for (int i = aLines.Length - 1; i >= 0; i--)
                if (aLines[i].Trim().Length > 0) return aLines[i].Trim();
            return "";
        }

        static bool SignedByAnyone(string iText)
            => System.Text.RegularExpressions.Regex.IsMatch(LastNonEmptyLine(iText), @"^(——|—|--)\s*\S");

        static bool SignedBy(string iText, string iPersona)
        {
            string aLast = LastNonEmptyLine(iText);
            var m = System.Text.RegularExpressions.Regex.Match(aLast, @"^(——|—|--)\s*([A-Za-z0-9_\-]+)");
            return m.Success && string.Equals(m.Groups[2].Value, iPersona, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 解析 Plurk 的 RFC 時間字串成 UTC。⛔ 這組 <c>DateTimeStyles</c> 本檔原本抄了三處 ——
        /// 收斂成一支，否則「三處有一處寫錯」的症狀是**時間比較安靜地給出相反答案**。
        /// </summary>
        static bool TryPlurkUtc(string iRfc, out DateTime oUtc)
        {
            return DateTime.TryParse(iRfc, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out oUtc);
        }

        /// <summary>
        /// `Sun, 23 Aug 2026 09:03:42 GMT` → `08-23 17:03`（本地）。
        /// 解析不了就**原樣回**（不吞掉，也不假裝知道時間）。
        /// </summary>
        static string ShortTime(string iRfc)
        {
            if (string.IsNullOrEmpty(iRfc)) return "(無時間)";
            if (!TryPlurkUtc(iRfc, out DateTime aUtc)) return iRfc;
            return aUtc.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
        }

        void OpResponses()
        {
            var ioR = Report;
            string aId = Arg("plurk_id").Trim();
            if (aId.Length == 0) throw SCP_PlurkFailure.Blocked("[Plurk] op=responses 需要 --arg plurk_id=<噗 id>");
            var aCred = RequireCredentials();
            int aFrom = ParseIntArg("from_response", 0, 0, 100000);

            var aParams = new Dictionary<string, string>
            {
                { "plurk_id", aId },
                { "from_response", aFrom.ToString(CultureInfo.InvariantCulture) },
            };
            string aBody = Fetch("responses_" + aId, "/APP/Responses/get", aParams, aCred);

            ioR.AppendLine();
            ioR.AppendLine($"## responses（`{aId}` 底下的回應）");
            var aRoot = SafeParse(aBody);
            var aList = (aRoot != null && aRoot.Contains("responses")) ? aRoot["responses"] : null;
            var aFriends = (aRoot != null && aRoot.Contains("friends")) ? aRoot["friends"] : null;
            if (aRoot == null || aList == null || !aList.IsArray)
            {
                ioR.AppendLine("- ⚠ 回應裡沒有 `responses` 陣列 —— 不是「沒人回」，是格式跟我預期的不一樣。");
                ioR.AppendLine("- body（前 300 字）: " + Trunc(aBody, 300));
                return;
            }
            // 🩸 2026-08-24：陣列 4 筆而 id 只有 3 個 —— **Plurk 自己回了同一則兩次**。
            //   首版直接印 `aList.Count` 當「幾則回應」⇒ 那個數字是陣列長度，不是回應數。
            //   ⇒ 兩個數都印（陣列筆數／相異 id 數）＋ 跟 root 的 `response_count` 對帳，
            //     不一致就說出來。同一個量有三個來源時，挑一個印等於替其他兩個背書。
            var aRpEmoCtx = EmoBegin();
            var aSeen = new HashSet<string>();
            for (int i = 0; i < aList.Count; i++) aSeen.Add(JsonScalar(aList[i], "id"));
            string? aDeclared = aRoot.Contains("response_count") ? JsonScalar(aRoot, "response_count") : null;
            AddValue("count", aSeen.Count.ToString(CultureInfo.InvariantCulture));
            ioR.AppendLine($"- **{aSeen.Count}** 則回應（相異 id）"
                + (aList.Count != aSeen.Count
                    ? $"　⚠ 而陣列有 **{aList.Count}** 筆 ⇒ **Plurk 回了重複的**（不是我印兩次）" : "")
                + (aDeclared != null && aDeclared != aSeen.Count.ToString(CultureInfo.InvariantCulture)
                    ? $"　⚠ 而它自己宣告 `response_count`={aDeclared} ⇒ 三個數不一致，我不挑一個當真" : "")
                + (aRoot.Contains("responses_seen")
                    ? $"　（對方記錄的已讀數: {JsonScalar(aRoot, "responses_seen")}）" : ""));
            var aDone = new HashSet<string>();
            for (int i = 0; i < aList.Count; i++)
            {
                var aRp = aList[i];
                string aRid = JsonScalar(aRp, "id");
                bool aDup = !aDone.Add(aRid);
                ioR.AppendLine($"- **{UserName(aFriends, JsonScalar(aRp, "user_id"))}**"
                    + $"　`{aRid}`　{JsonScalar(aRp, "posted")}"
                    + (aDup ? "　⚠ **重複的同一則**（上面出現過）" : ""));
                if (aDup) continue;   // 內容不重印，但**那一行要留著** —— 靜默去重會讓筆數對不上
                string aRpRaw = EmoAnnotatePaired(UnescapeJson(JsonScalar(aRp, "content_raw")),
                    UnescapeJson(JsonScalar(aRp, "content")), aRpEmoCtx, JsonScalar(aRp, "user_id"));
                ioR.AppendLine("    " + Trunc(OneLine(aRpRaw), 200));
            }
            EmoEnd(aRpEmoCtx);
        }

        // ===========================================================
        // 區塊職責：**誰 @ 了我、在哪一則、我回了沒** —— 被點名的訊息要優先回（Tim 2026-09-03）。
        // 物理意義：在這之前「被 @」這件事沒有入口：河道摘要只列噗不列回應，而 @ 幾乎都發生在
        //          回應裡；alerts 有 «mentioned» 型別但 getActive 讀了就清（不可重跑）、且不帶噗 id。
        //          🩸 現場：海苔 09-01 13:18 在一則噗底下 @ 我問「怎麼決定回哪些噗」，
        //          兩天後 Tim 從截圖上看到，我這邊的工具沒有任何一格讓它浮上來。
        // 數值影響：三步都是唯讀 ——
        //          ① /APP/Users/me 拿我的 id 與 nick（@ 的目標是 nick，不是顯示名）
        //          ② 候選噗＝兩條路徑聯集（TASK-0110，summit 2026-09-03 量出來的）：
        //             `filter=mentioned`（噗本體提到我）∪ `filter=only_responded`（我回過的串）。
        //             🩸 首版只有前者：海苔 08-27 在她自己的噗底下回 @summit，那則噗不在 mentioned 集合裡
        //               ⇒ 工具印「真的 0」，summit 隔七天靠 alerts 才發現。第二條路徑蓋住最大宗來源
        //               —— 別人在我參與過的串裡回我 —— 而它是實測過會列出那則的。
        //          ③ 每則噗拉 Responses/get，挑出內文含 `@<nick>` 的回應；
        //             「我回了沒」＝那則 @ 之後有沒有**我自己 id** 的回應（時間序，不比對內容）。
        //          ④ 通知層對帳：讀 `Alerts/getHistory`（不是 getActive —— 那支讀了就清）的 «mentioned»，
        //             拿（誰、何時）跟 ③ 的命中對；對不上的印「通知層有、兩條路徑找不到」。
        //             alerts 不帶噗 id，所以它只能證明「有」，證不了「在哪」—— 但那正是要印出來的那一格。
        // ⚠ 判準是**位置與 id**，不是「看起來像不像回了」：
        //   我在該噗有回應但都在 @ 之前 ⇒ 仍算未回。噗本體 @ 我而底下沒有我 ⇒ 未回。
        // ⚠ 兩條路徑都回 0 時**不印「真的 0」**：這兩條的射程是「噗本體提到我」＋「我參與過的串」，
        //   @ 若在我沒參與的別人噗裡，這裡看不到 —— 把射程外講成量過了，讀的人就不會再去別處看。
        // ===========================================================
        void OpMentions()
        {
            var ioR = Report;
            var aCred = RequireCredentials();
            int aLimit = ParseIntArg("limit", 20, 1, 100);
            int aPreview = ParseIntArg("preview", 160, 20, 400);

            var (aMeSt, aMeBody) = Call("/APP/Users/me", aCred, null);
            if (aMeSt != 200) throw SCP_PlurkFailure.Api($"[Plurk] mentions 問不到自己是誰（/APP/Users/me http={aMeSt}）—— 沒有 nick 就判不了誰 @ 我");
            string aMeId = PickJsonValue(aMeBody, "id") ?? "";
            string aNick = PickJsonValue(aMeBody, "nick_name") ?? "";
            if (aMeId.Length == 0 || aNick.Length == 0)
                throw SCP_PlurkFailure.Api("[Plurk] /APP/Users/me 缺 id 或 nick_name ⇒ 判不了 @，不猜");
            string aNeedle = "@" + aNick;
            string aMyPersona = m_Persona;
            var aOtherTagged = new List<string>();
            var aRoommates = SCP_PlurkAccounts.PersonasOn(m_Ctx, m_Res.SecretId);
            bool aMulti = aRoommates.Count > 1 || m_Res.IsShared;

            ioR.AppendLine();
            ioR.AppendLine("## mentions（誰 @ 了我、我回了沒）");
            ioR.AppendLine($"- 我：id `{aMeId}`　nick `{aNick}`（@ 的比對字串是 `{aNeedle}`，不分大小寫）");
            // 區塊職責：共用帳號的 persona 路由 —— 通知是帳號層的，而帳號有多個人。
            if (aMulti)
            {
                ioR.AppendLine($"- 👥 **這是多人帳號**（`{m_Res.SecretId}`：{string.Join(" / ", aRoommates)}）"
                    + $"　我＝`{(aMyPersona.Length > 0 ? aMyPersona : "(未指定 persona)")}`");
                ioR.AppendLine($"- 🧭 路由判準（Tim 2026-09-03）：`@{aNick}{SCP_PlurkAccounts.PersonaTagSep}<我>` ⇒ 指名我；"
                    + $"`@{aNick}{SCP_PlurkAccounts.PersonaTagSep}<別人>` ⇒ 指名別人（列在文末，不算我未回）；"
                    + $"**`@{aNick}` 沒帶標記 ⇒ 視為 @ 這個帳號內所有人，算我**"
                    + "　—— 誰收到是機械的，誰回才是人的決定。");
                if (aMyPersona.Length == 0)
                    ioR.AppendLine("- ⚠ **沒帶 `--persona`** ⇒ 帶標記的那些一律算「指名別人」，"
                        + "而那可能包含指名你的。要正確路由請顯式帶 persona。");
            }

            // ── ②-0 通知層先讀（**第三條路徑的入口**，2026-09-15 gura）────────────────
            // 🩸 量出來的：`Alerts/getHistory` 的 «mentioned» **每一筆都帶 `plurk_id` 與 `response_id`**
            //   （證物：`Plurk/cache/<帳號>__alerts_history.json`，30 筆通知逐筆有這兩欄）。
            //   ⛔ 在這之前本檔註解寫著「alerts 不帶噗 id，只能證『有』證不了『在哪』」——
            //   那是一句**從來沒有被量過的斷言**，而它正是「通知層有、兩條路徑找不到」那幾筆
            //   停在原地三週的唯一理由：工具沒去問，因為註解說問不到。
            // ⇒ 通知自己說得出那則噗是哪一則 ⇒ 照 alert 的 `plurk_id` 直接把那則噗撈進候選。
            // ⚠ 這條路徑補的正是前兩條的射程外：**@ 發生在我沒參與、也沒提到我的噗底下**。
            var (aAlSt, aAlBody) = Call("/APP/Alerts/getHistory", aCred, null);
            var aAlertPids = new List<string>();
            if (aAlSt == 200)
            {
                TryWriteCache(CacheFile(m_Res.SecretId ?? "_", "alerts_history"),
                              m_Res.SecretId ?? "_", "/APP/Alerts/getHistory", aAlBody);
                var aAlPre = SafeParse(aAlBody);
                if (aAlPre != null && aAlPre.IsArray)
                    for (int i = 0; i < aAlPre.Count; i++)
                    {
                        if (JsonScalar(aAlPre[i], "type") != "mentioned") continue;
                        string aAPid = JsonScalar(aAlPre[i], "plurk_id");
                        if (aAPid.Length > 0 && !aAlertPids.Contains(aAPid)) aAlertPids.Add(aAPid);
                    }
            }

            // 候選集：三條路徑各拉一次，依 plurk_id 去重（同一則多邊都有時只拉一次回應）
            var aCandidates = new List<SCP_PlurkNode>();
            var aUsersAll = SCP_PlurkNode.NewObject();
            var aSeenPid = new HashSet<string>();
            var aPathCounts = new List<string>();
            foreach (string aFilter in new[] { "mentioned", "only_responded" })
            {
                var aParams = new Dictionary<string, string>
                {
                    { "limit", aLimit.ToString(CultureInfo.InvariantCulture) },
                    { "filter", aFilter },
                };
                string aBody = Fetch("timeline_" + aFilter, "/APP/Timeline/getPlurks", aParams, aCred);
                var aRoot = SafeParse(aBody);
                var aPlurks = (aRoot != null && aRoot.Contains("plurks")) ? aRoot["plurks"] : null;
                var aPathUsers = (aRoot != null && aRoot.Contains("plurk_users")) ? aRoot["plurk_users"] : null;
                if (aPlurks == null || !aPlurks.IsArray)
                {
                    ioR.AppendLine($"- ⚠ filter={aFilter} 的回應裡沒有 `plurks` 陣列 —— 這不是「沒人 @ 我」，是**格式跟我預期的不一樣**。");
                    ioR.AppendLine("- body（前 300 字）: " + Trunc(aBody, 300));
                    continue;
                }
                int aNew = 0;
                for (int i = 0; i < aPlurks.Count; i++)
                {
                    var aPi = aPlurks[i];
                    if (!aSeenPid.Add(JsonScalar(aPi, "plurk_id"))) continue;
                    if (aPi == null) continue;
                    aCandidates.Add(aPi); aNew++;
                }
                if (aPathUsers != null) foreach (string k in aPathUsers.Keys) aUsersAll[k] = aPathUsers[k];
                aPathCounts.Add($"`{aFilter}` {aPlurks.Count} 則（新增 {aNew}）");
            }
            // ── ②-c 第三條路徑：alert 自帶的 plurk_id ──────────────────────────
            // ⚠ 逐則打一次 `Timeline/getPlurk` ⇒ 只補**前兩條沒撈到**的那幾則（多半 0-3 則）。
            //   拿不到就照實印，⛔ 不靜默當作沒有這筆通知。
            int aAlertNew = 0, aAlertFail = 0;
            for (int i = 0; i < aAlertPids.Count; i++)
            {
                if (aSeenPid.Contains(aAlertPids[i])) continue;
                var (aGpSt, aGpBody) = Call("/APP/Timeline/getPlurk", aCred,
                    new Dictionary<string, string> { { "plurk_id", aAlertPids[i] } });
                if (aGpSt != 200)
                {
                    aAlertFail++;
                    ioR.AppendLine($"- ⚠ 通知指到的噗 `{aAlertPids[i]}` 讀不到（http={aGpSt}）"
                        + " ⇒ 這一則**沒有讀數**（可能被刪、或是私噗我看不到），不是「沒有 @」");
                    continue;
                }
                var aGpRoot = SafeParse(aGpBody);
                var aGpPlurk = (aGpRoot != null && aGpRoot.Contains("plurk")) ? aGpRoot["plurk"] : null;
                if (aGpRoot == null || aGpPlurk == null) { aAlertFail++; continue; }
                if (!aSeenPid.Add(aAlertPids[i])) continue;
                aCandidates.Add(aGpPlurk); aAlertNew++;
                // 作者資料：getPlurk 回的是單顆 `user`，塞進同一張表（後面 UserName 照舊查得到）
                if (aGpRoot.Contains("user"))
                {
                    string aGpOwner = JsonScalar(aGpPlurk, "owner_id");
                    if (aGpOwner.Length > 0) aUsersAll[aGpOwner] = aGpRoot["user"];
                }
            }
            aPathCounts.Add($"`alerts→getPlurk` {aAlertPids.Count} 筆通知（新增 {aAlertNew}"
                + (aAlertFail > 0 ? $"、讀不到 {aAlertFail}" : "") + "）");
            ioR.AppendLine($"- 候選噗 **{aCandidates.Count}** 則（limit={aLimit}／前兩條路徑）：{string.Join("、", aPathCounts)}");
            // 候選窗的**左端**（最舊那則的時刻）—— 通知層對帳要靠它分辨「找不到」與「沒撈到那麼舊」。
            // 🩸 2026-09-05 的讀數：海苔 08-27 那筆 @ 在 limit=20 下永遠印「兩條路徑找不到」，
            //   而真正的原因是它在候選窗之外 ⇒ 讀的人會以為 TASK-0110 的修法沒生效。
            //   ⚠ 兩者處置相反：真的找不到 ⇒ 去 op=profile 撈那個人；超出窗 ⇒ 加大 limit 重跑。
            DateTime aOldestCand = DateTime.MaxValue;
            for (int i = 0; i < aCandidates.Count; i++)
                if (TryPlurkUtc(JsonScalar(aCandidates[i], "posted"), out DateTime aCt) && aCt < aOldestCand)
                    aOldestCand = aCt;
            if (aCandidates.Count > 0 && aOldestCand != DateTime.MaxValue)
                ioR.AppendLine($"- 候選窗左端（最舊一則）：**{aOldestCand.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)}**"
                    + "　—— 比這更早的 @ **不在射程內**，不是不存在");
            if (aCandidates.Count == 0)
                ioR.AppendLine("- ⚠ 兩條路徑都回 0 ⇒ **不是「沒人 @ 我」**：射程是「噗本體提到我」＋「我回過的串」，"
                    + "@ 若在我沒參與的別人噗裡，這裡看不到。下面的通知層對帳會說有沒有那種。");

            int aPending = 0, aAnswered = 0;
            // ⭐ 歸桶守衛（TASK-0153）：**含 @ 本帳號的回應必須恰好落一個桶**。
            //   🩸 這張單的失效樣子是「一則正常的輸出」—— 總數對、格式對、每一桶都有內容，
            //   而漏掉的那一筆在兩份清單上都不存在。⇒ 光修成因不夠：**要讓它下次漏的時候會叫。**
            //   ⚠ 只數「含 @」的：不含 @ 的回應本來就不該進任何一桶，把它們算進來會天天誤報。
            int aMentionRows = 0;   // 含 @ 本帳號的回應總數（判定前先數）
            int aBucketed = 0;      // 其中真的被歸進某一桶的
            var aEmoCtx = EmoBegin();
            // 給通知層對帳用 —— ⭐ 帶 `pid`/`rid`：alert 自己帶這兩個 id，
            // 用 id 對是**唯一鍵**，而（誰、時間差 ≤3 分）只是近似（同一人同分鐘發兩則就分不開）。
            var aHitLog = new List<(string uid, string when, string pid, string rid)>();
            // 指名室友的那些也要留檔：它們在通知層**照樣會亮**（alert 是帳號層的），
            // ⛔ 而舊版把它們算進「找不到」—— 那是把「不是我的」講成「我找不到」，處置完全不同。
            var aRoomLog = new List<(string uid, string when, string pid, string rid, string tags)>();
            var aUsers = aUsersAll;
            for (int i = 0; i < aCandidates.Count; i++)
            {
                var aP = aCandidates[i];
                string aPid = JsonScalar(aP, "plurk_id");
                string aOwner = JsonScalar(aP, "owner_id");
                string aPRaw = UnescapeJson(JsonScalar(aP, "content_raw"));
                // 噗本體 @ 我 ＝ 第 0 則
                var aHits = new List<(int idx, string who, string uid, string when, string text, string rid)>();
                var aBodyHit = SCP_PlurkAccounts.ClassifyMention(aPRaw, aNick, aMyPersona);
                if (aBodyHit.HitsMe)
                    aHits.Add((0, UserName(aUsers, aOwner), aOwner, JsonScalar(aP, "posted"), aPRaw, ""));
                else if (aBodyHit.Found)
                {
                    aOtherTagged.Add($"[{aPid}] 噗本體 @ 了帳號但指名 {string.Join(" / ", aBodyHit.Tags)}");
                    aRoomLog.Add((aOwner, JsonScalar(aP, "posted"), aPid, "", string.Join(" / ", aBodyHit.Tags)));
                }
                var aHeader = new SCP_PlurkText("\n");
                aHeader.AppendLine();
                aHeader.AppendLine($"### [{aPid}] {ShortTime(JsonScalar(aP, "posted"))} **{UserName(aUsers, aOwner)}** «{JsonScalar(aP, "qualifier")}»"
                    + $" 💬{JsonScalar(aP, "response_count")}");
                aHeader.AppendLine("    " + Trunc(OneLine(aPRaw), aPreview));

                // 回應：一次拉全（from_response=0）；量到的是 Plurk 回的那一頁，超過的會印出來
                var (aRSt, aRBody) = Call("/APP/Responses/get", aCred,
                    new Dictionary<string, string> { { "plurk_id", aPid }, { "from_response", "0" } });
                if (aRSt != 200)
                {
                    ioR.Append(aHeader);
                    ioR.AppendLine($"    ⚠ 拉不到回應（http={aRSt}）⇒ 這則**判不了**回了沒（不是「沒回」）");
                    continue;
                }
                var aRRoot = SafeParse(aRBody);
                var aList = (aRRoot != null && aRRoot.Contains("responses")) ? aRRoot["responses"] : null;
                var aFriends = (aRRoot != null && aRRoot.Contains("friends")) ? aRRoot["friends"] : null;
                int aLastMineIdx = -1;      // 我最後一則回應在陣列裡的位置（陣列＝時間序）
                int aUnsignedMine = 0;      // 本帳號回的、但末行沒署名 ⇒ 判不了是誰回的
                int aCount = aList != null && aList.IsArray ? aList.Count : 0;
                var aSeen = new HashSet<string>();
                for (int r = 0; r < aCount; r++)
                {
                    var aRp = aList![r];
                    if (!aSeen.Add(JsonScalar(aRp, "id"))) continue;    // Plurk 會重複回同一則
                    string aUid = JsonScalar(aRp, "user_id");
                    string aRaw = UnescapeJson(JsonScalar(aRp, "content_raw"));
                    // ⭐ 分類**先算一次**，兩條路共用（TASK-0153）——
                    //   之前「本帳號回的」那條路自己 continue 掉，根本沒問過這一則有沒有指名誰。
                    var aRHit = SCP_PlurkAccounts.ClassifyMention(aRaw, aNick, aMyPersona);
                    if (aRHit.Found) aMentionRows++;
                    if (aUid == aMeId)
                    {
                        // 🩸 kiara 2026-09-03：路由是 person-level（`→kiara` 才算她的），而「已回」原本是 account-level
                        //   （本帳號 id 回過就算）⇒ 共用帳號三個人，gura 一回 kiara 的 🔔 就消失，印的是 ✅。
                        //   ⇒ 多人帳號下「我回了」＝這則回應的**署名**是我（共用帳號末行署名是 lint 硬規則）。
                        //   沒署名的回應**不算我回**（判不了是誰，寧可讓 🔔 多亮一次，不讓它被別人的回應熄掉）。
                        bool aSignedMine = aMulti && aMyPersona.Length > 0 && SignedBy(aRaw, aMyPersona);
                        if (!aMulti) { aLastMineIdx = r; if (aRHit.Found) aBucketed++; continue; }
                        if (aSignedMine) aLastMineIdx = r;
                        else if (aMyPersona.Length > 0 && !aRHit.Found && SignedByAnyone(aRaw) == false) aUnsignedMine++;

                        // ⭐⭐ TASK-0153 的本體：**「這則是本帳號發的」與「這則指名我」不互斥。**
                        //   共用帳號底下室友跟我同一個 user_id ⇒ 她指名我的那一則，
                        //   舊版在上面就 `continue` 掉了 ⇒ 它**兩個桶都沒進**，而 💬N 知道它在。
                        //   🩸 血證：`640105635045271`（09-06 20:32，內文開頭 `@<nick>→kiara`）
                        //   在整份回傳檔零命中，而同一輪 `op=responses` 印得出它。
                        //   ⚠ 署名是我的那一則不算「有人 @ 我」—— 那是我自己在講話。
                        if (aMyPersona.Length > 0 && !aSignedMine)
                        {
                            if (aRHit.HitsMe)
                            {
                                aHits.Add((r + 1, UserName(aFriends, aUid), aUid, JsonScalar(aRp, "posted"),
                                    EmoAnnotatePaired(aRaw, UnescapeJson(JsonScalar(aRp, "content")), aEmoCtx, aUid),
                                    JsonScalar(aRp, "id")));
                                aBucketed++;
                            }
                            else if (aRHit.Found)
                            {
                                aOtherTagged.Add($"[{aPid}] 第 {r + 1} 則（同帳號室友發的）@ 了帳號但指名 {string.Join(" / ", aRHit.Tags)}");
                                aRoomLog.Add((aUid, JsonScalar(aRp, "posted"), aPid, JsonScalar(aRp, "id"),
                                    string.Join(" / ", aRHit.Tags)));
                                aBucketed++;
                            }
                        }
                        else if (aRHit.Found) aBucketed++;   // 署名是我的：那是我自己的話，歸「我的回應」這一桶
                        continue;
                    }
                    if (!aRHit.HitsMe)
                    {
                        // 有 @ 帳號但指名別人 ⇒ 不算我未回，但**要看得見**（否則它會從所有人的視野消失）
                        if (aRHit.Found)
                        {
                            aOtherTagged.Add($"[{aPid}] 第 {r + 1} 則 @ 了帳號但指名 {string.Join(" / ", aRHit.Tags)}");
                            aRoomLog.Add((aUid, JsonScalar(aRp, "posted"), aPid, JsonScalar(aRp, "id"),
                                string.Join(" / ", aRHit.Tags)));
                            aBucketed++;
                        }
                        continue;
                    }
                    aHits.Add((r + 1, UserName(aFriends, aUid), aUid, JsonScalar(aRp, "posted"),
                        EmoAnnotatePaired(aRaw, UnescapeJson(JsonScalar(aRp, "content")), aEmoCtx, aUid),
                        JsonScalar(aRp, "id")));
                    aBucketed++;
                }
                string aDeclared = aRRoot != null && aRRoot.Contains("response_count") ? JsonScalar(aRRoot, "response_count") : "";
                bool aPartial = aDeclared.Length > 0 && aDeclared != aSeen.Count.ToString(CultureInfo.InvariantCulture);

                // only_responded 的候選大多**沒有** @ 我（我回過的串裡別人在講別的）—— 那不是判不了，
                // 是正常的「這串沒人點名我」；沒命中且回應讀滿的就不印，免得把河道整份重印一次。
                if (aHits.Count == 0 && !aPartial) continue;
                ioR.Append(aHeader);
                if (aPartial)
                    ioR.AppendLine($"    ⚠ 只讀到 {aSeen.Count} 則回應而它宣告 response_count={aDeclared} ⇒ 沒讀到的那些裡有沒有 @ 我，這裡**不知道**");
                if (aMulti && aUnsignedMine > 0)
                    ioR.AppendLine($"    ⚠ 本帳號有 {aUnsignedMine} 則回應**沒署名** ⇒ 判不了是誰回的，一律**不算我回**（共用帳號末行署名是硬規則）");
                foreach (var h in aHits)
                {
                    // 「回了沒」＝ 那則 @ 之後有沒有我的回應（位置比較；第 0 則＝噗本體 ⇒ 我有任何回應即算）
                    // 多人帳號下「我的回應」＝本帳號回的**且署名是我**（見上面 kiara 那格）。
                    bool aReplied = aLastMineIdx >= 0 && (h.idx == 0 || aLastMineIdx > h.idx - 1);
                    if (aReplied) aAnswered++; else aPending++;
                    aHitLog.Add((h.uid, h.when, aPid, h.rid));
                    ioR.AppendLine($"    - {(aReplied ? "✅ 已回" : "🔔 **未回**")}　@ 在{(h.idx == 0 ? "噗本體" : $"第 {h.idx} 則回應")}"
                        + $"　**{h.who}**　{ShortTime(h.when)}");
                    ioR.AppendLine("        " + Trunc(OneLine(h.text), aPreview));
                }
            }

            // ④ 通知層對帳 —— getHistory 不清通知（getActive 會）。alerts 沒有噗 id，只能用（誰、何時）配。
            ioR.AppendLine();
            ioR.AppendLine("## 通知層對帳（`Alerts/getHistory` 的 «mentioned»，唯讀）");
            // ⚠ 這裡**不再打一次** API —— 同一趟的 body 在 ②-0 就讀過並落了快取。
            //   打兩次的代價不是流量，是**兩份可能不同的通知層讀數**同時存在於一份報告裡。
            if (aAlSt != 200)
            {
                ioR.AppendLine($"- ⚠ 讀不到通知歷史（http={aAlSt}）⇒ 這一格**沒有讀數**，上面的清單只代表兩條時間軸路徑");
            }
            else
            {
                var aAl = SafeParse(aAlBody);
                int aMentionAlerts = 0, aUnmatched = 0;
                int aOutRange = 0;      // 對不上的當中，「只是比候選窗更早」的那幾筆（⛔ 不與「找不到」同號）
                int aRoomMates = 0;     // 對不上的當中，**那則其實是指名室友的**（⛔ 也不與「找不到」同號）
                if (aAl != null && aAl.IsArray)
                {
                    for (int i = 0; i < aAl.Count; i++)
                    {
                        var aIt = aAl[i];
                        if (aIt == null) continue;
                        if (JsonScalar(aIt, "type") != "mentioned") continue;
                        aMentionAlerts++;
                        var aFrom = aIt.Contains("from_user") ? aIt["from_user"] : null;
                        string aFid = aFrom != null ? JsonScalar(aFrom, "id") : "";
                        string aFname = aFrom != null ? UnescapeJson(JsonScalar(aFrom, "display_name")) : "(查無名稱)";
                        string aWhen = JsonScalar(aIt, "posted");
                        string aAlPid0 = JsonScalar(aIt, "plurk_id");
                        string aAlRid0 = JsonScalar(aIt, "response_id");
                        bool aMatched = false;
                        // ⭐ 先用**唯一鍵**（alert 自帶的 plurk_id/response_id）對；對不到才退回近似鍵。
                        //   ⚠ 退回那一格要說得出自己退回了 —— 近似鍵在「同一人同一分鐘兩則」上會誤配，
                        //     而誤配的樣子是一筆通知被算成已涵蓋，然後從報告上消失。
                        foreach (var h in aHitLog)
                        {
                            if (aAlRid0.Length > 0 && h.rid.Length > 0)
                            { if (h.rid == aAlRid0) { aMatched = true; break; } continue; }
                            if (aAlPid0.Length > 0 && h.pid == aAlPid0 && aAlRid0.Length == 0)
                            { aMatched = true; break; }
                        }
                        if (!aMatched && TryPlurkUtc(aWhen, out DateTime aT))
                        {
                            foreach (var h in aHitLog)
                            {
                                if (h.uid != aFid) continue;
                                if (TryPlurkUtc(h.when, out DateTime aHt)
                                    && Math.Abs((aHt - aT).TotalMinutes) <= 3) { aMatched = true; break; }
                            }
                        }
                        // 🩸 2026-09-15 gura：舊版到這裡就印「找不到」了，而 5 筆裡有 4 筆
                        //   **那則其實指名 calli／kiara** —— alert 是**帳號層**的，室友被 @ 我這邊照樣亮一盞。
                        //   ⇒ 「不是我的」與「我找不到」在舊字面上同形，而處置相反（前者不必追）。
                        string aRoomTags = "";
                        if (!aMatched)
                            foreach (var m in aRoomLog)
                            {
                                bool aSame = (aAlRid0.Length > 0 && m.rid.Length > 0) ? m.rid == aAlRid0
                                           : (aAlPid0.Length > 0 && m.pid == aAlPid0 && aAlRid0.Length == 0);
                                if (!aSame && aAlPid0.Length == 0 && m.uid == aFid
                                    && TryPlurkUtc(aWhen, out DateTime aT2) && TryPlurkUtc(m.when, out DateTime aMt))
                                    aSame = Math.Abs((aMt - aT2).TotalMinutes) <= 3;
                                if (aSame) { aRoomTags = m.tags; break; }
                            }
                        if (!aMatched && aRoomTags.Length > 0)
                        {
                            aUnmatched++; aRoomMates++;
                            ioR.AppendLine($"- 👥 **通知層有，而那則指名室友 {aRoomTags}**：{ShortTime(aWhen)}　**{aFname}**（`{aFid}`）"
                                + $"　`plurk_id={aAlPid0}`"
                                + " ⇒ 通知是**帳號層**的，室友被 @ 我這邊也會亮一盞；**不算我未回**，也不必去追。");
                        }
                        else if (!aMatched)
                        {
                            aUnmatched++;
                            // ⛔ 先問「它在不在候選窗裡」再說「找不到」—— 兩者在舊字面上同形而處置相反。
                            bool aOutOfWindow = aOldestCand != DateTime.MaxValue
                                && TryPlurkUtc(aWhen, out DateTime aAt) && aAt < aOldestCand;
                            if (aOutOfWindow)
                            {
                                aOutRange++;
                                ioR.AppendLine($"- ⏳ **超出候選窗，不是找不到**：{ShortTime(aWhen)}　**{aFname}**（`{aFid}`）"
                                    + $" ⇒ 它比候選最舊那則（{aOldestCand.ToLocalTime().ToString("MM-dd HH:mm", CultureInfo.InvariantCulture)}）還早，"
                                    + $"而兩條路徑各只撈 limit={aLimit} 則"
                                    + $"　→ 加大重跑：`--arg op=mentions --arg limit={aLimit * 5}`");
                            }
                            else
                            {
                                string aAlPid = JsonScalar(aIt, "plurk_id");
                                string aAlRid = JsonScalar(aIt, "response_id");
                                ioR.AppendLine($"- ⚠ **三條路徑都沒對上**：{ShortTime(aWhen)}　**{aFname}**（`{aFid}`）"
                                    + (aAlPid.Length > 0
                                        ? $"　通知自帶 `plurk_id={aAlPid}`"
                                          + (aAlRid.Length > 0 ? $" `response_id={aAlRid}`" : "")
                                          + $" ⇒ 直接讀：`--arg op=responses --arg plurk_id={aAlPid}`"
                                          + "（第三條路徑已撈過它，仍對不上多半是那則 @ 不含我的 nick、或回應讀不滿）"
                                        : "　⚠ 這筆通知**沒有 `plurk_id`** ⇒ 回到 `op=profile --arg user_id=" + aFid + "`"));
                            }
                        }
                    }
                }
                ioR.AppendLine($"- 通知歷史裡 «mentioned» **{aMentionAlerts}** 筆，其中對不上路徑命中的 **{aUnmatched}** 筆"
                    + $"（其中 **{aRoomMates}** 筆是**指名室友的**👥、**{aOutRange}** 筆只是**比候選窗更早**⏳、"
                    + $"**{aUnmatched - aOutRange - aRoomMates}** 筆是真的找不到⚠）"
                    + "（配法：同一個人 ＋ 時間差 ≤3 分；歷史只有最近 30 筆通知，更舊的這裡也看不到）");
                if (aOutRange > 0)
                    ioR.AppendLine($"  ⇒ 那 {aOutRange} 筆**不代表修法沒生效**，是射程：`--arg limit={aLimit * 5}` 再跑一次就進得來。");
                AddValue("alert_unmatched", (aUnmatched - aOutRange - aRoomMates).ToString(CultureInfo.InvariantCulture));
            }

            ioR.AppendLine();
            ioR.AppendLine($"## 讀數：🔔 未回 **{aPending}**　✅ 已回 **{aAnswered}**"
                + "　（「已回」＝ @ 之後有我的回應，只看位置與 id，不看內容有沒有答到；射程＝噗本體提到我＋我回過的串＋通知層對帳）");
            AddValue("pending", aPending.ToString(CultureInfo.InvariantCulture));
            AddValue("answered", aAnswered.ToString(CultureInfo.InvariantCulture));
            // 指名別人的那些：**不算我未回，但一定要印** —— 不印的話它會從所有人的視野消失，
            // 而「被過濾掉」跟「不存在」在回傳檔上長得一模一樣。
            if (aOtherTagged.Count > 0)
            {
                ioR.AppendLine($"- 👥 另有 **{aOtherTagged.Count}** 筆 @ 了這個帳號但**指名別人**（不算我未回，列出來讓它不消失）：");
                foreach (var s in aOtherTagged) ioR.AppendLine($"    · {s}");
                ioR.AppendLine("  ⇒ 那幾位跑自己的 `op=mentions` 時會看到它們是自己的 🔔。");
            }
            // ⭐ 歸桶守衛的讀數（TASK-0153）—— **每次都印，包括全中的時候**。
            //   🩸 只在出事時印的話，「這次沒漏」與「這個守衛根本沒跑」在回傳檔上同形，
            //   而那正是本單那一隻的形狀（總數知道、位置不知道）換一張臉。
            if (aMentionRows == aBucketed)
                ioR.AppendLine($"- ✅ 歸桶對帳：候選窗內含 @ 本帳號的回應 **{aMentionRows}** 則，全部歸桶（未回／已回／指名別人／我自己的話）");
            else
            {
                ioR.AppendLine($"- 🔴 **歸桶對帳不平：含 @ 的回應 {aMentionRows} 則，只歸了 {aBucketed} 則 ⇒ 有 {aMentionRows - aBucketed} 則不在任何一份清單上。**");
                ioR.AppendLine("  ⛔ 這不是「沒人 @ 我」——是**這支工具知道它在，卻說不出它在哪**（TASK-0153 那一隻）。");
                ioR.AppendLine("  ⇒ 拿噗 id 走 `--arg op=responses --arg plurk_id=<id>` 逐則對，對得出來的請回報。");
                Notices.Add($"🔴 歸桶對帳不平：含 @ 的回應 {aMentionRows} 則，只歸了 {aBucketed} 則（TASK-0153）");
            }
            ioR.AppendLine($"  ⚠ 本對帳的射程：**只涵蓋回應層**（噗本體那一層走另一條路），"
                + "且只數「內文含 @ 本帳號」的那些 —— 不含 @ 的回應本來就不該進任何一桶。");
            ioR.AppendLine("### ▶ 回（走既有發文路，@ 的先回）");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=get       --arg plurk_id=<id>                    # 先讀全文與脈絡");
            ioR.AppendLine("--arg op=responses --arg plurk_id=<id>                    # 讀完整串再回，別對著摘要講話");
            ioR.AppendLine("--arg op=post --arg slip_file=<交付單> --arg reply_to=<id> --arg confirm=1");
            ioR.AppendLine("```");
            EmoEnd(aEmoCtx);
        }

        void OpFriends()
        {
            var ioR = Report;
            var aCred = RequireCredentials();
            string aUserId = Arg("user_id").Trim();
            if (aUserId.Length == 0)
            {
                // 不猜「我是誰」—— 去問一次 /APP/Users/me，並且把那個讀數印出來
                var (aSt, aMe) = Call("/APP/Users/me", aCred, null);
                if (aSt != 200)
                    throw SCP_PlurkFailure.Api($"[Plurk] 問不到自己的 user_id（http={aSt}）⇒ 請顯式帶 --arg user_id=");
                aUserId = PickJsonValue(aMe, "id") ?? "";
                if (aUserId.Length == 0)
                    throw SCP_PlurkFailure.Api("[Plurk] /APP/Users/me 沒有 id 欄位 ⇒ 請顯式帶 --arg user_id=");
                ioR.AppendLine($"- user_id 未給 ⇒ 由 `/APP/Users/me` 讀回 **{aUserId}**（讀的，不是推的）");
            }
            int aLimit = ParseIntArg("limit", 30, 1, 100);
            int aOffset = ParseIntArg("offset", 0, 0, 100000);

            var aParams = new Dictionary<string, string>
            {
                { "user_id", aUserId },
                { "offset", aOffset.ToString(CultureInfo.InvariantCulture) },
                { "limit", aLimit.ToString(CultureInfo.InvariantCulture) },
            };
            string aBody = Fetch("friends_" + aUserId, "/APP/FriendsFans/getFriendsByOffset", aParams, aCred);

            ioR.AppendLine();
            ioR.AppendLine($"## friends（`{aUserId}` 的好友，offset={aOffset} limit={aLimit}）");
            var aRoot = SafeParse(aBody);
            if (aRoot == null || !aRoot.IsArray)
            {
                ioR.AppendLine("- ⚠ 回應不是陣列 —— 格式跟我預期的不一樣（不是「沒有好友」）。");
                ioR.AppendLine("- body（前 300 字）: " + Trunc(aBody, 300));
                return;
            }
            AddValue("count", aRoot.Count.ToString(CultureInfo.InvariantCulture));
            ioR.AppendLine($"- **{aRoot.Count}** 位");
            for (int i = 0; i < aRoot.Count; i++)
            {
                var aU = aRoot[i];
                ioR.AppendLine($"- `{JsonScalar(aU, "id")}`"
                    + $"　**{UnescapeJson(JsonScalar(aU, "display_name"))}**"
                    + $"（{JsonScalar(aU, "nick_name")}）");
            }
            if (aRoot.Count == aLimit)
                ioR.AppendLine($"- ⚠ 剛好取滿 {aLimit} 筆 ⇒ **後面可能還有**"
                    + $"（`--arg offset={aOffset + aLimit}` 續取）。取滿與取完在這裡同形，所以這行一定要印。");
        }

        // ===========================================================
        // 區塊職責：按讚／取消讚 —— **對別人的東西動手**，所以守衛比讀取那幾個嚴。
        // 物理意義：這是一個對外、別人看得到、而且掛在我們帳號名下的動作。
        // 數值影響：改 Plurk 上的 favorite 狀態。三道守衛：
        //   ① `confirm=1` 才真的送（跟 op=post 同一條規矩）
        //   ② 送之前先 **getPlurk 把那則印出來** —— 「我要按的是這則」要看得見，防 id 打錯
        //      （數字打錯不會有任何一層喊，而它會按到一個陌生人的噗）
        //   ③ 送之後 **回讀** —— 印 ✓ 不算數，讀回來才算
        // ===========================================================
        void OpFavorite(bool iOn)
        {
            var ioR = Report;
            string aVerb = iOn ? "like" : "unlike";
            string aId = Arg("plurk_id").Trim();
            if (aId.Length == 0) throw SCP_PlurkFailure.Blocked($"[Plurk] op={aVerb} 需要 --arg plurk_id=<噗 id>");
            var aCred = RequireCredentials();

            ioR.AppendLine();
            ioR.AppendLine($"## {aVerb}（對外動作 —— 別人看得到，而且掛在這個帳號名下）");

            // ① 先看清楚要動的是哪一則
            var (aSt0, aBefore) = Call("/APP/Timeline/getPlurk", aCred,
                new Dictionary<string, string> { { "plurk_id", aId } });
            if (aSt0 != 200)
            {
                ioR.AppendLine($"- ✗ 讀不到 `{aId}`（http={aSt0}）⇒ **不動作**。" + Trunc(aBefore, 200));
                throw SCP_PlurkFailure.Api($"[Plurk] {aVerb} 前置讀取失敗 http={aSt0}");
            }
            string aObj = ExtractObject(aBefore, "plurk");
            string aFavBefore = PickJsonValue(aObj, "favorite_count") ?? "?";
            ioR.AppendLine($"- 目標: `{aId}`　owner_id: {PickJsonValue(aObj, "owner_id") ?? "?"}"
                + $"　目前 favorite_count: **{aFavBefore}**");
            ioR.AppendLine("- 內容首行: "
                + Trunc(FirstLine(UnescapeJson(PickJsonValue(aObj, "content_raw") ?? "")), 60));

            // ② confirm 守衛
            if (Arg("confirm") != "1")
            {
                ioR.AppendLine();
                ioR.AppendLine("- 🛑 **dry-run（沒有送出）** —— 這是對別人的東西動手，跟 `op=post` 同一條規矩。");
                ioR.AppendLine("  要真的做請加 `--arg confirm=1`。");
                return;
            }

            string aEndpoint = iOn ? "/APP/Timeline/favoritePlurks" : "/APP/Timeline/unfavoritePlurks";
            var (aSt, aBody) = Call(aEndpoint, aCred,
                new Dictionary<string, string> { { "ids", "[" + aId + "]" } });
            ioR.AppendLine($"- endpoint: `POST {aEndpoint}`　http: **{aSt}**");
            if (aSt != 200)
            {
                ioR.AppendLine("- ✗ body（前 300 字）: " + Trunc(aBody, 300));
                throw SCP_PlurkFailure.Api($"[Plurk] {aVerb} 失敗 http={aSt}");
            }

            // ③ 回讀 —— 200 只證明對方收到請求
            var (aSt2, aAfter) = Call("/APP/Timeline/getPlurk", aCred,
                new Dictionary<string, string> { { "plurk_id", aId } });
            string? aObj2 = aSt2 == 200 ? ExtractObject(aAfter, "plurk") : null;
            string aFavAfter = aObj2 == null ? "(回讀失敗)" : (PickJsonValue(aObj2, "favorite_count") ?? "?");
            ioR.AppendLine($"- 回讀 favorite_count: **{aFavBefore} → {aFavAfter}**"
                + "　⚠ 這是**總數**不是「我按了沒」—— 同時有別人按或收回時它不是乾淨的證據");
            string? aFavFlag = aObj2 == null ? null : PickJsonValue(aObj2, "favorite");
            ioR.AppendLine(aFavFlag == null
                ? "- ⚠ 回應裡沒有 `favorite` 這個欄位 ⇒ **「我按了沒」這一格沒有讀數**（不是「沒按到」）"
                : $"- `favorite`（就這個帳號而言）: **{aFavFlag}**　← 這才是直接證據");
            AddValue("favorite", aFavFlag ?? "");
        }

        // ===========================================================
        // 區塊職責：**擴圈**（Tim 2026-08-24）—— 找到有興趣的陌生人、看清楚他是誰、送出關係請求。
        // 物理意義：在這之前這支 Cmd 的社交面只到「好友之間」。而好友清單是個封閉集合：
        //          它能告訴妳誰已經在裡面，說不出**誰可能該進來**。
        // 數值影響：`profile` / `expand` / `search` / `alerts` 純唯讀；
        //          `befriend` / `unfriend` / `follow` / `unfollow` / `accept` / `deny` **改關係**，
        //          一律要 `confirm=1`，且送出前把「那個人是誰」印成人看得懂的東西。
        //
        // ⚠ 端點名的驗證狀態：本區塊那幾支是 **2026-08-24 首次接上**，
        //   事實來源仍在 `SCP_Core/Docs~/Plurk_Maintenance.md`（`senate cmd doc --arg op=show --arg name=Plurk_Maintenance`） §5（別在這裡另記一份）。
        //   ⇒ 所以每一支的非 200 都**把 body 印出來**：
        //     「端點不存在」「簽章錯」「被 WAF 擋」三種失敗都是 4xx，長得一樣。
        // ⛔ 這裡不做「全部同意」「批次加好友」：
        //   「該不該加這個人」機器判不了，而批次動作會讓那一格沒有人看過。
        // ===========================================================

        /// <summary>
        /// 印一張「這個人是誰」的卡：顯示名／帳號／自介／近期一則噗／關係現況。
        /// <para>物理意義：對外動作的第一道守衛。id 錯一位不會有任何一層喊，
        /// 而它會把請求送給一個陌生人 —— 所以送出前印的是**人**，不是 user_id。</para>
        /// <para>回傳 profile 的原始 body（沒讀到回 null），讓呼叫端能再撈關係欄位。</para>
        /// </summary>
        string? PersonCard(string iUserId, Dictionary<string, string> iCred)
        {
            var ioR = Report;
            var (aSt, aBody) = Call("/APP/Profile/getPublicProfile", iCred,
                new Dictionary<string, string> { { "user_id", iUserId } });
            if (aSt != 200)
            {
                ioR.AppendLine($"- ⚠ 讀不到 `{iUserId}` 的公開檔（http={aSt}）"
                    + "⇒ **這一格沒有讀數**，不是「這個人不存在」。");
                ioR.AppendLine("  body（前 300 字）: " + Trunc(aBody, 300));
                return null;
            }
            var aRoot = SafeParse(aBody);
            var aInfo = (aRoot != null && aRoot.Contains("user_info")) ? aRoot["user_info"] : null;
            if (aRoot == null || aInfo == null)
            {
                ioR.AppendLine("- ⚠ 回應裡沒有 `user_info` —— 格式跟我預期的不一樣（不是「查無此人」）。");
                ioR.AppendLine("  body（前 300 字）: " + Trunc(aBody, 300));
                return aBody;
            }
            ioR.AppendLine($"- 👤 **{UnescapeJson(JsonScalar(aInfo, "display_name"))}**"
                + $"（`{JsonScalar(aInfo, "nick_name")}` / id `{JsonScalar(aInfo, "id")}`）"
                + $"　karma {JsonScalar(aInfo, "karma")}");
            string aAbout = OneLine(StripTags(UnescapeJson(JsonScalar(aInfo, "about")))).Trim();
            ioR.AppendLine("- 自介: " + (aAbout.Length == 0 ? "(空)" : Trunc(aAbout, 160)));
            ioR.AppendLine($"- 好友 {JsonScalar(aRoot, "friends_count")}"
                + $" / 粉絲 {JsonScalar(aRoot, "fans_count")}"
                + $"　關係現況: {RelationText(aRoot)}");
            // 近期噗 —— 「這個人在寫什麼」比「他有幾個好友」重要得多
            var aPlurks = aRoot.Contains("plurks") ? aRoot["plurks"] : null;
            if (aPlurks != null && aPlurks.IsArray && aPlurks.Count > 0)
            {
                int aShow = Math.Min(3, aPlurks.Count);
                ioR.AppendLine($"- 近期噗（{aShow}/{aPlurks.Count} 則，各截 100 字）:");
                for (int i = 0; i < aShow; i++)
                {
                    string aTxt = OneLine(UnescapeJson(JsonScalar(aPlurks[i], "content_raw"))).Trim();
                    // 🩸 2026-08-24：首版沒印 plurk_id ⇒ 這張卡「看得到、回不了」——
                    //   要回應誰得先有 id，而卡上沒有 id 就得再繞一趟 timeline／search 去湊。
                    ioR.AppendLine($"    · `{JsonScalar(aPlurks[i], "plurk_id")}`"
                        + $" [{ShortTime(JsonScalar(aPlurks[i], "posted"))}] "
                        + (aTxt.Length == 0 ? "(沒有文字內容)" : Trunc(aTxt, 100)));
                }
            }
            else
            {
                ioR.AppendLine("- 近期噗: **沒有讀到**（可能是不公開，也可能是格式不同 —— 這兩件事我分不出來）");
            }
            return aBody;
        }

        /// <summary>
        /// 關係現況：把 profile 回應裡幾個布林欄位收成一句人話。
        /// <para>⚠ 欄位都不在時回「沒有讀數」而**不是**「不是好友」——
        /// 那兩件事的處置完全不同（一個是要去查，一個是可以送請求）。</para>
        /// </summary>
        static string RelationText(SCP_PlurkNode? iRoot)
        {
            if (iRoot == null) return "(沒有讀數)";
            var aBits = new List<string>();
            foreach (string aKey in new[] { "are_friends", "is_fan", "is_following", "has_read_permission" })
                if (iRoot.Contains(aKey)) aBits.Add($"{aKey}={JsonScalar(iRoot, aKey)}");
            return aBits.Count == 0 ? "(沒有讀數 —— 不等於「不是好友」)" : string.Join(" / ", aBits);
        }

        /// <summary>自介欄位是 HTML ⇒ 粗暴剝標籤。只求能讀，不求正確渲染。</summary>
        static string StripTags(string? iHtml)
        {
            if (string.IsNullOrEmpty(iHtml)) return "";
            var sb = new StringBuilder(iHtml!.Length);
            bool aIn = false;
            foreach (char c in iHtml)
            {
                if (c == '<') { aIn = true; continue; }
                if (c == '>') { aIn = false; sb.Append(' '); continue; }
                if (!aIn) sb.Append(c);
            }
            return sb.ToString();
        }

        // ── 唯讀：看一個人 ────────────────────────────────────────
        void OpProfile()
        {
            var ioR = Report;
            string aUserId = Arg("user_id").Trim();
            if (aUserId.Length == 0)
                throw SCP_PlurkFailure.Blocked("[Plurk] op=profile 需要 --arg user_id=<誰>"
                    + "（從 op=friends / op=expand / op=search 的清單抄）");
            var aCred = RequireCredentials();
            ioR.AppendLine();
            ioR.AppendLine($"## profile（`{aUserId}` 的公開檔 —— 唯讀）");
            PersonCard(aUserId, aCred);
            ioR.AppendLine();
            ioR.AppendLine("### ▶ 下一步");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=follow   --arg user_id=<id> --arg confirm=1   # 單向追蹤，不需對方同意");
            ioR.AppendLine("--arg op=befriend --arg user_id=<id> --arg confirm=1   # 送好友請求（對方會收到通知）");
            ioR.AppendLine("```");
        }

        // ── 唯讀：擴圈（好友的好友，按共同好友數排序）────────────
        // 🩸 判準：這裡**只算共同好友數**、只讀公開發文，不做別的資料拼合、不建檔。
        //    快取照現行規矩不入 git —— 那些是陌生人的東西，他們沒有同意過被釘進我們的歷史。
        void OpExpand()
        {
            var ioR = Report;
            var aCred = RequireCredentials();
            int aTop = ParseIntArg("top", 15, 1, 100);
            int aPerFriend = ParseIntArg("limit", 100, 1, 100);
            int aHops = ParseIntArg("hops", 8, 1, 50);   // 最多向外問幾位好友（省 API 呼叫）

            // ① 我是誰、我的好友有誰 —— 兩個都讀，不推
            var (aMeSt, aMe) = Call("/APP/Users/me", aCred, null);
            if (aMeSt != 200) throw SCP_PlurkFailure.Api($"[Plurk] 問不到自己的 user_id（http={aMeSt}）");
            string aMeId = PickJsonValue(aMe, "id") ?? "";
            if (aMeId.Length == 0) throw SCP_PlurkFailure.Api("[Plurk] /APP/Users/me 沒有 id 欄位");

            var (aFrSt, aFrBody) = Call("/APP/FriendsFans/getFriendsByOffset", aCred,
                new Dictionary<string, string>
                {
                    { "user_id", aMeId }, { "offset", "0" },
                    { "limit", aPerFriend.ToString(CultureInfo.InvariantCulture) },
                });
            var aMine = SafeParse(aFrBody);
            ioR.AppendLine();
            ioR.AppendLine("## expand（好友的好友 —— 唯讀，按共同好友數排序）");
            if (aFrSt != 200 || aMine == null || !aMine.IsArray)
            {
                ioR.AppendLine($"- ⚠ 拿不到自己的好友清單（http={aFrSt}）⇒ 這一格沒有讀數。");
                ioR.AppendLine("- body（前 300 字）: " + Trunc(aFrBody, 300));
                return;
            }

            var aKnown = new HashSet<string> { aMeId };      // 已經是好友的＋我自己 ⇒ 不列
            var aSeed = new List<(string id, string name)>();
            for (int i = 0; i < aMine.Count; i++)
            {
                string aId = JsonScalar(aMine[i], "id");
                if (aId.Length == 0) continue;
                aKnown.Add(aId);
                aSeed.Add((aId, UnescapeJson(JsonScalar(aMine[i], "display_name"))));
            }
            ioR.AppendLine($"- 我（`{aMeId}`）的好友 **{aSeed.Count}** 位（讀的，不是推的）");

            // ② 向外一跳。⚠ 只問前 aHops 位 —— 而且**把沒問的那幾位說出來**：
            //    靜默截斷會讓「掃過全部」與「掃了一半」在回傳檔上同形。
            int aAsk = Math.Min(aHops, aSeed.Count);
            if (aAsk < aSeed.Count)
                ioR.AppendLine($"- ⚠ 只向外問了前 **{aAsk}/{aSeed.Count}** 位好友"
                    + $"（`--arg hops={aSeed.Count}` 問完）—— 這不是全圖，是取樣。");

            var aCount = new Dictionary<string, int>();
            var aVia = new Dictionary<string, List<string>>();
            var aName = new Dictionary<string, string>();
            var aNick = new Dictionary<string, string>();
            int aFailed = 0;
            for (int i = 0; i < aAsk; i++)
            {
                var (aSt, aBody) = Call("/APP/FriendsFans/getFriendsByOffset", aCred,
                    new Dictionary<string, string>
                    {
                        { "user_id", aSeed[i].id }, { "offset", "0" },
                        { "limit", aPerFriend.ToString(CultureInfo.InvariantCulture) },
                    });
                var aList = SafeParse(aBody);
                if (aSt != 200 || aList == null || !aList.IsArray)
                {
                    aFailed++;
                    ioR.AppendLine($"- ⚠ `{aSeed[i].name}`（{aSeed[i].id}）的好友清單讀不到（http={aSt}）"
                        + " ⇒ 他那一票沒進統計");
                    continue;
                }
                for (int j = 0; j < aList.Count; j++)
                {
                    string aId = JsonScalar(aList[j], "id");
                    if (aId.Length == 0 || aKnown.Contains(aId)) continue;   // 已是好友／我自己 ⇒ 不是候選
                    aCount[aId] = (aCount.TryGetValue(aId, out int c) ? c : 0) + 1;
                    if (!aVia.TryGetValue(aId, out var aL)) { aL = new List<string>(); aVia[aId] = aL; }
                    aL.Add(aSeed[i].name);
                    aName[aId] = UnescapeJson(JsonScalar(aList[j], "display_name"));
                    aNick[aId] = JsonScalar(aList[j], "nick_name");
                }
            }

            ioR.AppendLine($"- 候選陌生人 **{aCount.Count}** 位"
                + $"（來自 {aAsk - aFailed} 份好友清單{(aFailed > 0 ? $"，{aFailed} 份讀不到" : "")}）");
            if (aCount.Count == 0)
            {
                ioR.AppendLine("- 沒有候選 —— 而這**可能**是「好友的好友都已經是我的好友」，"
                    + "也可能是上面那幾份清單讀不到。兩者我分不出來，所以不下結論。");
                return;
            }
            ioR.AppendLine();
            ioR.AppendLine($"### 前 {Math.Min(aTop, aCount.Count)} 名（共同好友數 ↓）");
            ioR.AppendLine("> ⚠ 共同好友數是**排序訊號**，不是「該加」的判準。");
            ioR.AppendLine("> 要不要加，得先 `op=profile` 讀他在寫什麼 —— 那一格機器判不了。");
            ioR.AppendLine();
            var aRank = aCount.OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.Ordinal).Take(aTop).ToList();
            // 🩸 2026-08-24 首跑：最高分 3，而前 15 名**全部都是 3** ⇒ 名次其實由 tie-break（id 字串序）決定，
            //   也就是「帳號註冊得早」被印成了「比較推薦」。分數看起來像排名，而它不是。
            //   ⇒ 把平手狀況印出來：同分幾位、這一頁切在哪。**排序的解析度要自己講清楚。**
            int aTopScore = aRank[0].Value;
            int aTie = aCount.Count(kv => kv.Value == aTopScore);
            ioR.AppendLine($"- 最高共同好友數 **{aTopScore}**，同分 **{aTie}** 位"
                + (aTie > aTop
                    ? $"　⚠ 而我只列 {aTop} 位 ⇒ **這一頁的名次是 tie-break（id 序）決定的，不是推薦度**。"
                      + "　同分的人之間這個分數區分不了他們，要挑得靠 `op=profile` 讀內容。"
                    : ""));
            ioR.AppendLine("- ⚠ 本表**不知道誰已經被送過請求** —— pending 的人會照樣出現在這裡"
                + "（`op=profile` 的 `are_friends=false` 也分不出「沒送過」與「送了他沒理」）。");
            ioR.AppendLine();
            foreach (var kv in aRank)
            {
                var aList = aVia[kv.Key];
                ioR.AppendLine($"- `{kv.Key}`　**{(aName[kv.Key].Length == 0 ? "(查無名稱)" : aName[kv.Key])}**"
                    + $"（{aNick[kv.Key]}）　共同好友 **{kv.Value}**"
                    + $"　← 經由 {string.Join("／", aList.Take(4))}"
                    + (aList.Count > 4 ? $" 等 {aList.Count} 位" : ""));
            }
            ioR.AppendLine();
            ioR.AppendLine("### ▶ 下一步（id 抄上面那個）");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=profile  --arg user_id=<id>                    # 先讀他在寫什麼（唯讀）");
            ioR.AppendLine("--arg op=follow   --arg user_id=<id> --arg confirm=1    # 單向追蹤，不打擾對方");
            ioR.AppendLine("--arg op=befriend --arg user_id=<id> --arg confirm=1    # 送好友請求");
            ioR.AppendLine("```");
        }

        // ── 唯讀：搜尋（找主題，而不是找人）────────────────────────
        void OpSearch()
        {
            var ioR = Report;
            string aQuery = Arg("query").Trim();
            if (aQuery.Length == 0)
                throw SCP_PlurkFailure.Blocked("[Plurk] op=search 需要 --arg query=<關鍵字>");
            string aKind = Arg("kind").Trim().ToLowerInvariant();
            if (aKind != "plurk" && aKind != "user")
                throw SCP_PlurkFailure.Blocked($"[Plurk] --arg kind={aKind} 只吃 plurk|user（不靜默取預設值）");
            var aCred = RequireCredentials();
            int aOffset = ParseIntArg("offset", 0, 0, 100000);
            int aPreview = ParseIntArg("preview", 90, 20, 400);

            string aEndpoint = aKind == "user" ? "/APP/UserSearch/search" : "/APP/PlurkSearch/search";
            var aParams = new Dictionary<string, string> { { "query", aQuery } };
            if (aOffset > 0) aParams["offset"] = aOffset.ToString(CultureInfo.InvariantCulture);

            var (aSt, aBody) = Call(aEndpoint, aCred, aParams);
            ioR.AppendLine();
            ioR.AppendLine($"## search kind={aKind}（唯讀）　query: `{aQuery}`");
            ioR.AppendLine($"- endpoint: `POST {aEndpoint}`　http: **{aSt}**");
            if (aSt != 200)
            {
                ioR.AppendLine("- ✗ body（前 400 字）: " + Trunc(aBody, 400));
                ioR.AppendLine("- ⚠ 排查順序：**先確認端點存在 → 再懷疑簽章 → 最後才是 WAF**"
                    + "（三種失敗都是 4xx，而 WAF 那格看 body 不看 status）。");
                throw SCP_PlurkFailure.Api($"[Plurk] search 失敗 http={aSt}");
            }
            var aRoot = SafeParse(aBody);
            var aList = (aRoot != null && aRoot.Contains(aKind == "user" ? "users" : "plurks"))
                ? aRoot[aKind == "user" ? "users" : "plurks"] : null;
            if (aList == null || !aList.IsArray)
            {
                ioR.AppendLine($"- ⚠ 回應裡沒有 `{(aKind == "user" ? "users" : "plurks")}` 陣列 ——"
                    + " 這是**格式跟我預期的不一樣**，不是「搜不到」。");
                ioR.AppendLine("- body（前 400 字）: " + Trunc(aBody, 400));
                return;
            }
            AddValue("count", aList.Count.ToString(CultureInfo.InvariantCulture));
            ioR.AppendLine($"- **{aList.Count}** 筆（offset={aOffset}）");
            ioR.AppendLine();
            // 🩸 2026-08-24：河道那支的 user 字典叫 `plurk_users`，而**搜尋這支不叫那個** ⇒
            //   首跑 30 筆作者全印「查無名稱」。UserName 那格已經守住了（不回空字串），
            //   所以我看得出是「我沒查到作者」而不是「這則沒有作者」—— 兩個候選鍵都試。
            var aUsers = (aRoot != null && aRoot.Contains("plurk_users")) ? aRoot["plurk_users"]
                : ((aRoot != null && aRoot.Contains("users")) ? aRoot["users"] : null);
            if (aUsers == null && aKind == "plurk")
                ioR.AppendLine("- ⚠ 回應裡沒有 `plurk_users` 也沒有 `users` ⇒ 作者名這一欄**沒有讀數**"
                    + "（下面印的 id 仍可直接餵 `op=profile`）");
            for (int i = 0; i < aList.Count; i++)
            {
                var aIt = aList[i];
                if (aKind == "user")
                {
                    ioR.AppendLine($"- `{JsonScalar(aIt, "id")}`"
                        + $"　**{UnescapeJson(JsonScalar(aIt, "display_name"))}**"
                        + $"（{JsonScalar(aIt, "nick_name")}）"
                        + $"　karma {JsonScalar(aIt, "karma")}");
                    continue;
                }
                string aTxt = OneLine(UnescapeJson(JsonScalar(aIt, "content_raw"))).Trim();
                ioR.AppendLine($"- **[{JsonScalar(aIt, "plurk_id")}]** {ShortTime(JsonScalar(aIt, "posted"))}"
                    + $" **{UserName(aUsers, JsonScalar(aIt, "owner_id"))}**"
                    + $"　（owner_id `{JsonScalar(aIt, "owner_id")}`）");
                ioR.AppendLine("    " + (aTxt.Length == 0 ? "(沒有文字內容)" : Trunc(aTxt, aPreview)));
            }
            ioR.AppendLine();
            ioR.AppendLine("⚠ 搜到的是**噗**不是人 ⇒ 覺得對盤先 `op=profile --arg user_id=<owner_id>` 讀他這個人，");
            ioR.AppendLine("　 再決定追蹤或送請求。**摘要是截斷過的，別對著開頭下判斷。**");
        }

        // ── 唯讀：誰在等我回應 ────────────────────────────────────
        void OpAlerts()
        {
            var ioR = Report;
            var aCred = RequireCredentials();
            bool aHistory = Arg("history") == "1";
            string aEndpoint = aHistory ? "/APP/Alerts/getHistory" : "/APP/Alerts/getActive";
            var (aSt, aBody) = Call(aEndpoint, aCred, null);

            ioR.AppendLine();
            ioR.AppendLine($"## alerts（{(aHistory ? "歷史" : "待處理")}）");
            // 🩸 2026-08-24 實測：我把 getActive 標成「唯讀」，而**它不是** ——
            //   第一次讀回 4 筆（2 pending ＋ plurk_liked ＋ my_responded），
            //   第二次同一支指令只剩 2 筆。⇒ 讀這一支會把通知清掉（friendship_pending 留著，其餘消失）。
            //   ⇒ 這格是「讀取有副作用」，而副作用不可逆（清掉的通知不會回來）。
            ioR.AppendLine("- ⚠ **這一支不是唯讀** —— 實測第二次呼叫少了兩筆：");
            ioR.AppendLine("  `getActive` 會把讀到的通知**清掉**（friendship_pending 會留，按讚／回應類不會）。");
            ioR.AppendLine("  ⇒ 不要當成可重跑的查詢用；要看歷史走 `--arg history=1`。");
            ioR.AppendLine($"- endpoint: `POST {aEndpoint}`　http: **{aSt}**");
            if (aSt != 200)
            {
                ioR.AppendLine("- ✗ body（前 400 字）: " + Trunc(aBody, 400));
                throw SCP_PlurkFailure.Api($"[Plurk] alerts 失敗 http={aSt}");
            }
            var aRoot = SafeParse(aBody);
            if (aRoot == null || !aRoot.IsArray)
            {
                ioR.AppendLine("- ⚠ 回應不是陣列 —— 格式跟我預期的不一樣（**不是「沒有通知」**）。");
                ioR.AppendLine("- body（前 400 字）: " + Trunc(aBody, 400));
                return;
            }
            AddValue("count", aRoot.Count.ToString(CultureInfo.InvariantCulture));
            ioR.AppendLine($"- **{aRoot.Count}** 筆"
                + (aRoot.Count == 0 ? "（真的空 —— 這是讀回來的 0，不是讀不到）" : ""));
            for (int i = 0; i < aRoot.Count; i++)
            {
                var aIt = aRoot[i];
                if (aIt == null) continue;
                string aType = JsonScalar(aIt, "type");
                // ⚠ 方向在欄位名裡，不在 type 裡（🩸 2026-08-24 我一開始只看 `from_user`）：
                //   `from_user` ＝ 別人對我做了什麼（要我處置）
                //   `to_user`   ＝ **我對別人做的還在等他** —— 對它跑 accept 是沒有意義的
                var aFrom = aIt.Contains("from_user") ? aIt["from_user"] : null;
                var aTo = aIt.Contains("to_user") ? aIt["to_user"] : null;
                var aWho = aFrom ?? aTo;
                string aDir = aFrom != null ? "⬅ 對方 → 我（要我處置）"
                    : (aTo != null ? "➡ 我 → 對方（**等他回應，我這邊沒事可做**）" : "(方向不明)");
                string aFromId = aWho != null ? JsonScalar(aWho, "id") : JsonScalar(aIt, "user_id");
                string aFromName = aWho != null ? UnescapeJson(JsonScalar(aWho, "display_name")) : "";
                ioR.AppendLine($"- «{aType}»　{aDir}　`{aFromId}`"
                    + $"　**{(aFromName.Length == 0 ? "(查無名稱)" : aFromName)}**"
                    + $"（{(aWho != null ? JsonScalar(aWho, "nick_name") : "")}）"
                    + $"　{ShortTime(JsonScalar(aIt, "posted"))}");
                // 🩸 2026-08-24：`friendship_pending` 的人不在 `from_user` 裡 ⇒ 上面那行印成
                //   「(查無名稱)」＋空 id。而「我不知道他是誰」跟「這筆沒有人」長得一樣，
                //   於是待處理的請求看起來像壞資料。⇒ 認不出人就把原始物件攤開，讓下一個人看得到真欄位名。
                if (aFromId.Length == 0)
                    ioR.AppendLine("    ⚠ 撈不到 user id ⇒ 原始欄位攤開（**不是這筆沒有人**）: "
                        + Trunc(OneLine(aIt.ToJson()), 400));
            }
            ioR.AppendLine();
            ioR.AppendLine("### ▶ 逐筆處理（⛔ 刻意沒有「全部同意」）");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=profile --arg user_id=<id>                   # 先看他是誰");
            ioR.AppendLine("--arg op=accept  --arg user_id=<id> --arg confirm=1   # 同意");
            ioR.AppendLine("--arg op=deny    --arg user_id=<id> --arg confirm=1   # 拒絕");
            ioR.AppendLine("```");
        }

        // ── 對外：關係動作（befriend / unfriend / follow / unfollow / accept / deny）──
        // 守衛三道，跟 like 同一族：
        //   ① 送出前 `PersonCard` 把那個人印成人看得懂的東西（防 id 打錯）
        //   ② `confirm=1` 才真的送（「我只是想看看」與「我要按下去」不得同形）
        //   ③ 送出後**回讀** profile 的關係欄位 —— 200 只證明對方收到請求
        void OpRelation(string iVerb)
        {
            var ioR = Report;
            string aUserId = Arg("user_id").Trim();
            if (aUserId.Length == 0)
                throw SCP_PlurkFailure.Blocked($"[Plurk] op={iVerb} 需要 --arg user_id=<誰>");
            var aCred = RequireCredentials();

            // 端點與參數名一張表 —— 免得六個動作各自散在六段裡漂
            string aEndpoint, aParamKey, aWhat;
            switch (iVerb)
            {
                case "befriend": aEndpoint = "/APP/FriendsFans/becomeFriend"; aParamKey = "friend_id";
                    aWhat = "送出好友請求（對方會收到通知，要他同意才成立）"; break;
                case "unfriend": aEndpoint = "/APP/FriendsFans/removeAsFriend"; aParamKey = "friend_id";
                    aWhat = "解除好友"; break;
                case "follow": aEndpoint = "/APP/FriendsFans/becomeFan"; aParamKey = "fan_id";
                    aWhat = "**單向**追蹤（不需對方同意）"; break;
                // 🩸 2026-08-24：首版把 unfollow 也接到 becomeFan＋`follow=false` ——
                //   回 **200 ＋ `{"success_text":"ok"}`，而回讀 `is_following` 沒動**。
                //   多餘的參數被無聲吃掉，成功字串照樣印。⇒ 換成 setFollowing（它才吃 follow 旗標）。
                case "unfollow": aEndpoint = "/APP/FriendsFans/setFollowing"; aParamKey = "user_id";
                    aWhat = "取消追蹤"; break;
                case "accept": aEndpoint = "/APP/Alerts/addAsFriend"; aParamKey = "user_id";
                    aWhat = "同意對方的好友請求"; break;
                case "deny": aEndpoint = "/APP/Alerts/denyFriendship"; aParamKey = "user_id";
                    aWhat = "拒絕對方的好友請求"; break;
                default: throw SCP_PlurkFailure.Blocked($"[Plurk] 認不得的關係動作 '{iVerb}'");
            }

            ioR.AppendLine();
            ioR.AppendLine($"## {iVerb}（**對外動作 —— 改的是關係，而對方會知道**）");
            ioR.AppendLine($"- 動作: {aWhat}");
            ioR.AppendLine($"- 目標 user_id: `{aUserId}`");
            ioR.AppendLine();

            // ① 這個人是誰
            string? aBefore = PersonCard(aUserId, aCred);

            // ② confirm 守衛
            if (Arg("confirm") != "1")
            {
                ioR.AppendLine();
                ioR.AppendLine("- 🛑 **dry-run（沒有送出）** —— 上面那張卡就是這一步的用途：");
                ioR.AppendLine("  **確認我要動的是這個人**。id 錯一位不會有任何一層喊。");
                ioR.AppendLine("  要真的做請加 `--arg confirm=1`。");
                return;
            }

            var aParams = new Dictionary<string, string> { { aParamKey, aUserId } };
            // unfollow 走同一支 becomeFan 但 follow=false —— ⚠ 這一格未驗，回 4xx 就是它不對
            if (iVerb == "unfollow") aParams["follow"] = "false";
            var (aSt, aBody) = Call(aEndpoint, aCred, aParams);
            ioR.AppendLine();
            ioR.AppendLine($"- endpoint: `POST {aEndpoint}`　`{aParamKey}={aUserId}`　http: **{aSt}**");
            if (aSt != 200)
            {
                ioR.AppendLine("- ✗ body（前 400 字）: " + Trunc(aBody, 400));
                ioR.AppendLine("- ⚠ 端點名這一族是 2026-08-24 首次接上 ⇒ 4xx 先懷疑端點名／參數名，"
                    + "再懷疑簽章，最後才是 WAF（看 body 不看 status）。");
                throw SCP_PlurkFailure.Api($"[Plurk] {iVerb} 失敗 http={aSt}");
            }
            ioR.AppendLine("- body（前 200 字）: " + Trunc(aBody, 200));

            // ③ 回讀 —— 200 只證明對方收到請求
            var (aSt2, aAfter) = Call("/APP/Profile/getPublicProfile", aCred,
                new Dictionary<string, string> { { "user_id", aUserId } });
            if (aSt2 != 200)
            {
                ioR.AppendLine($"- ⚠ 回讀失敗（http={aSt2}）⇒ **結果那本帳沒有讀數**"
                    + "（已送出 ≠ 已生效，這兩件事要分開報）。");
                return;
            }
            var aAfterJd = SafeParse(aAfter);
            string aRelBefore = aBefore == null ? "(沒有讀數)" : RelationText(SafeParse(aBefore));
            ioR.AppendLine($"- 回讀關係: `{aRelBefore}` → `{RelationText(aAfterJd)}`");

            // ── 結果那本帳 ──────────────────────────────────────
            // 🩸 2026-08-24：`unfollow` 回 200 ＋ `{"success_text":"ok"}`，而 `is_following` 沒動 ——
            //   多餘的參數被無聲吃掉，成功字串照樣印。**200 是「對方收到請求」，不是「事情發生了」。**
            //   ⇒ 每個動作宣告它該讓哪個欄位變成什麼；沒變就大聲說未生效，不准讓 200 代表結果。
            //   ⚠ befriend 是唯一「現在本來就不會變」的動作（要等對方同意）—— 它的證人在 op=alerts。
            string? aField = null, aWant = null;
            switch (iVerb)
            {
                case "follow": aField = "is_following"; aWant = "true"; break;
                case "unfollow": aField = "is_following"; aWant = "false"; break;
                case "unfriend": aField = "are_friends"; aWant = "false"; break;
                case "accept": aField = "are_friends"; aWant = "true"; break;
            }
            if (aField == null || aWant == null)
            {
                ioR.AppendLine($"- ⚠ `{iVerb}` **現在不該有變化** —— 請求送到了與對方同意了是兩件事。"
                    + "前者的證人是 `op=alerts` 裡多一筆 `friendship_pending`（去看那個，別看這裡的 200）。");
                return;
            }
            string? aGot = (aAfterJd != null && aAfterJd.Contains(aField)) ? JsonScalar(aAfterJd, aField) : null;
            if (aGot == null)
                ioR.AppendLine($"- ⚠ 回讀沒有 `{aField}` 欄位 ⇒ **結果那本帳沒有讀數**（不是「沒生效」）。");
            else if (aGot.Equals(aWant, StringComparison.OrdinalIgnoreCase))
                ioR.AppendLine($"- ✅ 結果: `{aField}` = **{aGot}**（期待 {aWant}）← 這才是生效的直接證據");
            else
            {
                ioR.AppendLine($"- ⛔ **回 200 但沒生效**：`{aField}` = **{aGot}**，期待 `{aWant}`。"
                    + "　成功字串與實際狀態脫鉤 ⇒ 先懷疑參數名被無聲吃掉，再懷疑端點。");
                Notices.Add($"⛔ {iVerb} 回 200 但沒生效：{aField}={aGot}（期待 {aWant}）");
            }
        }

        // ── 讀取層：API ／ 本地快取 ────────────────────────────────
        // ⛔ 快取目錄不入 git —— 那裡面是**別人的**發文內容，而且是某一刻的快照。
        //    入版控等於把別人的時間軸釘進我們的歷史，而他們沒有同意過。
        // 🩸 2026-08-24：這行註解原本寫「`AgentCommands/.gitignore` 有 `Plurk/cache/`」——
        //    **而寫的當下它沒有**。ignore 規則是 @summit 當天才補上的（`.gitignore:32`）。
        //    我不只引用了一條不存在的規則，還在程式碼裡替它作證，而那行讀起來像讀數。
        //    ⇒ 判準：註解裡宣告「別處有一道防護」之前，去讀那一處；
        //      而**寫進註解不會讓那道防護存在**（那正是它最容易被誤讀成已完成的地方）。
        const string CacheRelative = "Plurk/cache";

        string CacheDir() => Path.Combine(m_Ctx.DataRoot, CacheRelative);

        string CacheFile(string iAccount, string iKey)
            => Path.Combine(CacheDir(), SafeName(iAccount) + "__" + SafeName(iKey) + ".json");

        static string SafeName(string? iText)
        {
            if (string.IsNullOrEmpty(iText)) return "_";
            var sb = new StringBuilder(iText!.Length);
            foreach (char c in iText)
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' ? c : '_');
            return sb.ToString();
        }

        /// <summary>
        /// 取資料：**預設打 API 並落快取**；`--arg cache=1` 才改讀快取。
        /// <para>⚠ 不論走哪一條，回傳檔一定印**資料來源與年齡** ——
        /// 「現況」與「快照」不可以同形，而它們天生就同形。</para>
        /// </summary>
        string Fetch(string iCacheKey, string iEndpoint, Dictionary<string, string>? iParams,
            Dictionary<string, string> iCred)
        {
            var ioR = Report;
            string aAccount = m_Res.SecretId ?? "_";
            string aFile = CacheFile(aAccount, iCacheKey);

            if (Arg("cache") == "1")
            {
                if (File.Exists(aFile))
                {
                    var aCached = SafeParse(File.ReadAllText(aFile, Encoding.UTF8));
                    string? aAt = aCached == null ? null : JsonScalar(aCached, "fetched_at");
                    ioR.AppendLine($"- 📦 **資料來源：本地快取**（`{(string.IsNullOrEmpty(aAt) ? "?" : aAt)}`，"
                        + $"**{AgeText(aAt)}前**）—— 這不是現況");
                    ioR.AppendLine($"  · 檔案 `{aFile}`　要現抓就拿掉 `--arg cache=1`");
                    var aCachedBody = aCached != null && aCached.Contains("body") ? aCached["body"] : null;
                    if (aCachedBody != null) { AddValue("from_cache", "1"); return aCachedBody.GetString(); }
                    ioR.AppendLine("  · ⚠ 快取檔在但沒有 `body` 欄位 ⇒ 當成沒有，改打 API");
                }
                else
                {
                    ioR.AppendLine($"- 📦 要求讀快取但**檔案不存在**（`{aFile}`）⇒ 改打 API"
                        + " —— 沒有靜默降級，這一行就是那個降級的讀數");
                }
            }

            var (aStatus, aBody) = Call(iEndpoint, iCred, iParams);
            ioR.AppendLine($"- 🌐 **資料來源：API 現抓**　`POST {iEndpoint}`　http: **{aStatus}**");
            if (aStatus != 200)
            {
                ioR.AppendLine("- ✗ body（前 300 字）: " + Trunc(aBody, 300));
                ioR.AppendLine("  · ⚠ 403 ＋ `error code: 1010` ＝ Cloudflare 依 UA 擋，"
                    + "不是簽章錯也不是端點不存在（三種失敗都是 4xx，長得一樣）");
                throw SCP_PlurkFailure.Api($"[Plurk] {iEndpoint} 失敗 http={aStatus}");
            }
            TryWriteCache(aFile, aAccount, iEndpoint, aBody);
            return aBody;
        }

        void TryWriteCache(string iFile, string iAccount, string iEndpoint, string iBody)
        {
            var ioR = Report;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(iFile) ?? ".");
                var aJd = new SCP_UclLegacyObject();
                aJd.Set("fetched_at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                aJd.Set("account", iAccount);
                aJd.Set("endpoint", iEndpoint);
                aJd.Set("body", iBody);
                File.WriteAllText(iFile, SCP_UclLegacyJson.ToJson(aJd), new UTF8Encoding(false));
                ioR.AppendLine($"  · 已落快取 `{iFile}`（⛔ 不入 git）");
            }
            catch (Exception ex)
            {
                // 快取寫不進去不影響這一次的讀數 —— 但要說出來，
                // 不然下次 `cache=1` 讀不到會變成一個沒有人解釋得了的謎
                ioR.AppendLine($"  · ⚠ 快取寫入失敗（不影響本次讀數）：{ex.Message}");
            }
        }

        static string AgeText(string? iIso)
        {
            if (string.IsNullOrEmpty(iIso)) return "年齡不明";
            if (!DateTime.TryParse(iIso, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out DateTime aAt))
                return "年齡不明";
            var aSpan = DateTime.UtcNow - aAt.ToUniversalTime();
            if (aSpan.TotalMinutes < 1) return "不到 1 分鐘";
            if (aSpan.TotalHours < 1) return $"約 {(int)aSpan.TotalMinutes} 分鐘";
            if (aSpan.TotalDays < 1) return $"約 {aSpan.TotalHours.ToString("0.#", CultureInfo.InvariantCulture)} 小時";
            return $"約 {aSpan.TotalDays.ToString("0.#", CultureInfo.InvariantCulture)} 天";
        }

        /// <summary>
        /// user id → 顯示名。查不到就回 id 本身並標記 —— **不回空字串**：
        /// 空的那格會讓人以為「這則沒有作者」，而事實是「我沒查到作者」。
        /// </summary>
        static string UserName(SCP_PlurkNode? iUsers, string iId)
        {
            if (string.IsNullOrEmpty(iId)) return "(無 id)";
            if (iUsers == null || !iUsers.Contains(iId)) return iId + "(查無名稱)";
            var aU = iUsers[iId];
            string aName = UnescapeJson(JsonScalar(aU, "display_name"));
            if (aName.Length == 0) aName = JsonScalar(aU, "nick_name");
            return aName.Length == 0 ? iId + "(查無名稱)" : aName;
        }
    }
}
