// 區塊職責：Plurk 的 op 本體（第一份：派遣、帳號、發文路徑）—— resolve / whoami / lint / preview / upload / post / get。
// 物理意義：**發文的唯一寫入端**（Tim 2026-08-21：「這部分可以走 c# CMD」）。
//          TASK-0362：Unity `Cmd_Plurk`（UCL_Core/Editor/Plurk）的搬家版 —— **忠實移植，不是重設計**：
//          報告文字、守衛、血證註解逐段照搬；只換三件事：
//            ① 路徑：UCL 全域 → 宿主傳進來的根（<see cref="SCP_PlurkContext"/>）
//            ② HTTP：HttpClient → 宿主注入的 `ISCP_HttpFormRequester`（SCP_Core 不碰網路）
//            ③ 同步：UniTask → 直接呼叫（SCP_Core 不碰 async）
//          為什麼是 C# 而不是 python：
//            ① 規則要長在必經路上 —— lint 若住在另一個語言的另一支工具裡，發文那條路繞得過它，
//               而繞過去不會報錯。這裡 `post` **強制先跑 lint**，errors 非空就不送。
//            ② 帳號解析同源 —— 直接呼叫 `SCP_PlurkAccounts.Resolve`，
//               不必再維護一份 python 鏡像（兩份遲早各說各話，且兩邊都不報錯）。
// 數值影響：`resolve` / `lint` / `preview` 零副作用、不連網（lint／preview／post 起手的 nick 補齊例外，見 EnsureNicks）。
//          `post` **預設 dry-run**：沒有 `confirm=1` 一律只印 payload 不送。
//          真送時寫一筆 audit jsonl（時間／persona／帳號／source／內容雜湊／回傳 plurk id）。
//          報告換行一律 `\n`（SCP 慣例；Unity 版是 `AppendLine` 的 CRLF —— 內容同、換行不同）。
//
// ⛔ 發布不可回復，而 Plurk 沒有 history ⇒ 這支永遠不自動發：`confirm=1` 是人打的。
// ⚠ 端點與參數的**驗證狀態**：事實來源在 `SCP_Core/Docs~/Plurk_Maintenance.md`（`senate cmd doc --arg op=show --arg name=Plurk_Maintenance`） §5
//   （別在這裡另記一份 —— 兩份清單必漂，而漂掉的那份看起來一樣可信）。
//   ⇒ `preview` 印出**完整將送內容**，讓人在送之前用眼睛驗一次。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using SCP.Core.Json;
using static SCP.Core.Plurk.SCP_PlurkJson;

namespace SCP.Core.Plurk
{
    public sealed partial class SCP_PlurkOps
    {
        public const string AuditRelative = "Plurk/post_audit.jsonl";

        /// <summary>全部 op（help 與 did-you-mean 用；派遣表在 <see cref="Execute"/>）。</summary>
        public static readonly string[] Ops =
        {
            "resolve", "whoami", "lint", "preview", "upload", "post", "get",
            "timeline", "responses", "mentions", "friends", "like", "unlike",
            "emoticons", "emoadd",
            "profile", "expand", "search", "alerts",
            "befriend", "unfriend", "follow", "unfollow", "accept", "deny",
        };

        readonly SCP_PlurkContext m_Ctx;
        readonly Func<string, string> m_Arg;
        readonly string m_Op;
        readonly string m_Persona;
        readonly SCP_PlurkAccountResolution m_Res;

        /// <summary>回傳檔本文（不論成功或失敗都要落檔 —— 見 Cmd 那一層的血證）。</summary>
        public readonly SCP_PlurkText Report = new SCP_PlurkText("\n");

        /// <summary>機讀純量（`🔢 key = value`）。</summary>
        public readonly List<KeyValuePair<string, string>> Values = new List<KeyValuePair<string, string>>();

        /// <summary>要印在 CLI 輸出（不只回傳檔）的提醒 —— 例如「噗發出去了但 audit 沒寫進去」。</summary>
        public readonly List<string> Notices = new List<string>();

        public SCP_PlurkAccountResolution Resolution => m_Res;

        /// <param name="iArg">取參數（未給 ⇒ 規格預設值）。⚠ **依 op 讀** —— 讀了不屬於這個 op 的參數，
        /// 「給了而從來沒被讀」那盞燈（TASK-0289）就永遠不會亮。</param>
        public SCP_PlurkOps(SCP_PlurkContext iCtx, string iOp, string iPersona, Func<string, string> iArg)
        {
            m_Ctx = iCtx;
            m_Arg = iArg;
            m_Op = (iOp ?? "").Trim().ToLowerInvariant();
            if (m_Op.Length == 0) m_Op = "resolve";
            m_Persona = (iPersona ?? "").Trim();

            Report.AppendLine($"# Plurk op={m_Op} persona={(m_Persona.Length == 0 ? "(未給)" : m_Persona)}"
                + $"  ts=`{DateTime.Now.ToString("yyyy-MM-dd HH:mm:sszzz", CultureInfo.InvariantCulture)}`（本地時間）");
            Report.AppendLine();

            m_Res = SCP_PlurkAccounts.Resolve(m_Ctx, m_Persona);
            Report.AppendLine($"- 帳號: **{(string.IsNullOrEmpty(m_Res.SecretId) ? "(無)" : m_Res.SecretId)}**"
                + $" / source: `{m_Res.Source}` / 署名必填: {(m_Res.RequiresSignature ? "是" : "否")}");
            Report.AppendLine($"- 說明: {m_Res.Describe()}");

            AddValue("op", m_Op);
            AddValue("account", m_Res.SecretId);
            AddValue("source", m_Res.Source);
        }

        public string Op => m_Op;

        void AddValue(string iKey, string iValue) => Values.Add(new KeyValuePair<string, string>(iKey, iValue ?? ""));

        string Arg(string iKey) => m_Arg(iKey) ?? "";

        /// <summary>
        /// 跑這一趟的 op。失敗一律丟例外（<see cref="SCP_PlurkFailure"/> 帶 exit code；其他例外＝程式或環境問題）。
        /// <para>⚠ **報告不在這裡落檔** —— 呼叫端（Cmd）在 catch 之後無條件寫 <see cref="Report"/>：
        /// 報告是**診斷**，失敗的時候比成功的時候更需要它。</para>
        /// </summary>
        public void Execute()
        {
            // ===========================================================
            // 區塊職責：發文三路（lint／preview／post）走 `@persona` 轉換**之前**，
            //          先把缺的 nick 補齊 —— 「還沒查過」不該長成「擋下來要人跑指令」。
            // 物理意義：nick 是**帳號**的屬性，而問它要的是**那份憑證**（`/APP/Users/me`）——
            //          而憑證是檔案，就在 secrets 資料夾底下。⇒ 這件事**不需要那個人在場**。
            //          🩸 血證（summit 2026-09-03 / calli 2026-09-04 復現）：舊實作把
            //          「不能猜」實作成了「必須人工」，於是一個系統缺口被轉成三位同事的待辦；
            //          而真正的成因是**登記表每棵樹一份**（Bar 樹那份連 `Nicks` 欄位都沒有），
            //          在那棵樹上跑 whoami 也只補那一棵。
            // 數值影響：全滿時**零往返**（只讀 registry 比對）；有缺才打 API，而且一次補齊全部
            //          （既然要開一次往返，就不要留下一格明天再開一次）。
            //          ⛔ 補不到仍然擋 —— 放行的唯一方式是猜一個 nick，而猜錯＝公開標注陌生人
            //          （TASK-0111 血證：`Calli` 是 karma 94.97 的活人）。
            // ⚠ 掛在 switch 之前而不是塞進 `ResolveMention`：後者是純判定函式，
            //   讓它去打 API 會把「解析」與「取得」混成一件事；而三條路共用一個補齊點，
            //   分三處寫就會漂，漂掉的那一處剛好是真的送出去的那一條。
            // ===========================================================
            if (m_Op == "lint" || m_Op == "preview" || m_Op == "post")
                EnsureNicks();

            switch (m_Op)
            {
                case "resolve": OpResolve(); break;
                case "whoami": OpWhoAmI(); break;
                case "lint": OpLint(); break;
                case "preview": OpPreview(out _); break;
                case "upload": OpUpload(); break;
                case "get": OpGet(); break;
                case "post": OpPost(); break;
                // ── 社交面（讀）──
                case "timeline": OpTimeline(); break;
                case "responses": OpResponses(); break;
                case "mentions": OpMentions(); break;
                case "friends": OpFriends(); break;
                // ── 社交面（寫，對別人動手 ⇒ 要 confirm=1）──
                case "like": OpFavorite(true); break;
                case "unlike": OpFavorite(false); break;
                // ── 擴圈（讀）──
                case "emoticons": OpEmoticons(); break;
                case "emoadd": OpEmoAdd(); break;
                case "profile": OpProfile(); break;
                case "expand": OpExpand(); break;
                case "search": OpSearch(); break;
                case "alerts": OpAlerts(); break;
                // ── 擴圈（寫，改的是關係 ⇒ 要 confirm=1）──
                case "befriend": OpRelation("befriend"); break;
                case "unfriend": OpRelation("unfriend"); break;
                case "follow": OpRelation("follow"); break;
                case "unfollow": OpRelation("unfollow"); break;
                case "accept": OpRelation("accept"); break;
                case "deny": OpRelation("deny"); break;
                default:
                    throw SCP_PlurkFailure.Blocked($"[Plurk] 認不得的 op='{m_Op}'"
                        + "（" + string.Join("|", Ops) + "）");
            }
        }

        // ── 小工具（依 op 讀參數）────────────────────────────────
        int ParseIntArg(string iKey, int iDefault, int iMin, int iMax)
        {
            string aRaw = Arg(iKey).Trim();
            if (aRaw.Length == 0) return iDefault;
            if (!int.TryParse(aRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aVal))
                throw SCP_PlurkFailure.Blocked($"[Plurk] --arg {iKey}={aRaw} 不是整數"
                    + "（不靜默取預設值 —— 打錯字要當場知道）");
            return aVal < iMin ? iMin : (aVal > iMax ? iMax : aVal);
        }

        bool Confirmed() => Arg("confirm").Trim() == "1";

        Dictionary<string, string> RequireCredentials() => SCP_PlurkApi.RequireCredentials(m_Ctx, m_Res);

        static (int status, string body) Call(string iPath, Dictionary<string, string> iCred,
            Dictionary<string, string>? iParams) => SCP_PlurkApi.Call(iPath, iCred, iParams);

        // ===========================================================
        // 區塊職責：帳號與憑證狀態
        // 數值影響：只讀存在性與欄位到齊；⛔ 憑證值一律不印（外洩沒有錯誤訊息）。
        // ===========================================================
        void OpResolve()
        {
            var ioR = Report;
            if (string.IsNullOrEmpty(m_Res.SecretId))
            {
                ioR.AppendLine();
                ioR.AppendLine("⛔ 帳號未設定 ⇒ 不能發文。先在 Senate 後台 `senate ui --page plurk`設共用帳號或個人 override。");
                return;
            }
            string aEnc = SCP_PlurkApi.SecretPath(m_Ctx, m_Res.SecretId, ".enc");
            string aTxt = SCP_PlurkApi.SecretPath(m_Ctx, m_Res.SecretId, ".txt");
            ioR.AppendLine();
            // `.enc 有` 與 `明文已安裝` 分開報 —— 合成一個綠燈的話，只有密文的機器看起來也像好了
            ioR.AppendLine($"- `.enc`: {(File.Exists(aEnc) ? "有" : "無")}　`{aEnc}`");
            ioR.AppendLine($"- 明文已安裝: {(File.Exists(aTxt) ? "**有**" : "無")}　`{aTxt}`");
            AddValue("plain_installed", File.Exists(aTxt) ? "1" : "0");
            if (!File.Exists(aTxt)) return;
            var aCred = SCP_PlurkApi.LoadCredentials(m_Ctx, m_Res.SecretId, out string aWhy);
            ioR.AppendLine(aCred == null
                ? $"- ⚠ 憑證不完整：{aWhy}"
                : "- 憑證: 四欄到齊（長度 "
                  + string.Join(" / ", SCP_PlurkApi.CredFields.Select(k => $"{k}={aCred[k].Length}"))
                  + "）　⛔ 值不印");
        }

        void OpWhoAmI()
        {
            var ioR = Report;
            var aCred = RequireCredentials();
            var (aStatus, aBody) = Call("/APP/Users/me", aCred, null);
            ioR.AppendLine();
            ioR.AppendLine($"## whoami（唯讀，不寫入任何 Plurk 資料）");
            ioR.AppendLine($"- http: **{aStatus}**");
            // 只挑身分欄位；整包回應含 email／生日等個資，不無條件倒進回傳檔
            foreach (var aKey in new[] { "id", "nick_name", "display_name", "full_name", "karma" })
            {
                string? v = PickJsonValue(aBody, aKey);
                if (v != null) ioR.AppendLine($"- {aKey}: {v}");
            }

            // 區塊職責：把這個帳號的 nick 寫回 registry —— 發文端 `@persona` 轉換的唯一對照來源。
            // 物理意義：**nick 只有帳號自己問得到**（`/APP/Users/me` 走的是這份憑證）。
            //          ⇒ 每個帳號的持有者跑一次 whoami，表就自己長出來；沒人手打，就不會漂。
            // 數值影響：只在 http 200 且真的讀到 nick 時寫。⚠ 寫入要出聲 ——
            //          「唯讀 op 偷偷寫了本地檔」是我們自己抓過的那一族。
            if (aStatus == 200)
            {
                string aNick = (PickJsonValue(aBody, "nick_name") ?? "").Trim().Trim('"');
                string aUid = (PickJsonValue(aBody, "id") ?? "").Trim().Trim('"');
                AddValue("nick", aNick);
                AddValue("user_id", aUid);
                if (!string.IsNullOrEmpty(aNick) && !string.IsNullOrEmpty(m_Res.SecretId))
                {
                    string aOld = SCP_PlurkAccounts.NickOf(m_Ctx, m_Res.SecretId);
                    string aOldUid = SCP_PlurkAccounts.UserIdOf(m_Ctx, m_Res.SecretId);
                    // 換綁與改名要分開講 —— user id 是穩定鍵，nick 不是。
                    if (aOldUid.Length > 0 && aUid.Length > 0 && aOldUid != aUid)
                        ioR.AppendLine($"- 🩸 **換綁**：user_id `{aOldUid}` → `{aUid}`"
                            + " —— 這份憑證現在指向另一個 Plurk 帳號，不是同一個人改了名字");
                    SCP_PlurkAccounts.SetNick(m_Ctx, m_Res.SecretId, aNick, aUid, "whoami");
                    ioR.AppendLine(aOld == aNick
                        ? $"- 📝 nick 登記表：`{m_Res.SecretId}` = `{aNick}`（已是這個值，重新蓋時間戳）"
                        : $"- 📝 **已寫入 nick 登記表**：`{m_Res.SecretId}` = `{aNick}`"
                          + (string.IsNullOrEmpty(aOld) ? "（原本沒登記）" : $"（原本是 `{aOld}`）")
                          + " —— 發文端 `@persona` 轉換讀的就是這張表");
                    ioR.AppendLine($"- 　user_id: `{(aUid.Length > 0 ? aUid : "(這次沒讀到)")}`　source: `whoami`");
                }
            }
            if (aStatus != 200)
            {
                ioR.AppendLine($"- ✗ body（前 300 字）: {Trunc(aBody, 300)}");
                throw SCP_PlurkFailure.Api($"[Plurk] whoami 失敗 http={aStatus} —— "
                    + "判準：先確認端點存在，再懷疑簽章，最後才是 WAF。三者的失敗都是 4xx。");
            }
        }

        // ===========================================================
        // 區塊職責：把「這台機器上有憑證、但登記表沒 nick」的帳號一次補齊。
        // 物理意義：查的單位是**帳號**不是 persona（21 位 persona 只落在 4 個帳號上）——
        //          所以枚舉的是 `ListSecretIds()`，不是 persona pool。
        //          每份憑證問的是它自己的 `/APP/Users/me`，⇒ 誰都不必上線。
        // 數值影響：全滿 ⇒ 一次 HTTP 都不發（迴圈只讀 registry）。有缺 ⇒ 缺幾個打幾次，
        //          **不是只補撞到的那一個** —— 既然要開往返，就不要留下一格明天再開一次。
        // ⛔ 只准打 `/APP/Users/me` 這一個唯讀端點。這條路用的是**別人的憑證**，
        //   它跟「代跑 `op=whoami --persona <他>`」的差別是：後者以那個 persona 的身分執行
        //   （進他的 lane、算他的帳），本函式不掛任何 persona 的帳、也不改任何 Plurk 狀態。
        //   ⚠ 這道白名單一旦鬆掉，它就從「解析 nick」長成「工具可以拿任何人的憑證做任何事」，
        //   而那一天不會有任何一層喊。
        // ⚠ 補不到的**不在這裡擋** —— 擋的判定留在 `ResolveMention`（它才知道文案 @ 了誰）。
        //   這裡只負責「能補的都補了」，補不到就照實印，讓後面那道守衛拿著真的理由去擋。
        // ===========================================================
        void EnsureNicks()
        {
            var ioR = Report;
            // ⚠ 條件是「nick 缺 **或** user_id 缺」不只是 nick：
            //   `PlurkUserId` 是後加的欄，既有各筆讀回來是空字串 ⇒ 只看 nick 的話那些筆**永遠補不上 id**，
            //   而空的 id 會讓「這是同一個帳號嗎」永遠答不出來。⇒ 一次性遷移，補完之後照樣零往返。
            var aMissing = new List<string>();
            foreach (string aId in SCP_PlurkAccounts.ListSecretIds(m_Ctx))
                if (string.IsNullOrEmpty(SCP_PlurkAccounts.NickOf(m_Ctx, aId))
                    || string.IsNullOrEmpty(SCP_PlurkAccounts.UserIdOf(m_Ctx, aId))) aMissing.Add(aId);
            if (aMissing.Count == 0) return;

            ioR.AppendLine();
            ioR.AppendLine($"## nick 自動補齊（{aMissing.Count} 個帳號缺 nick 或 user_id ⇒ 現在查）");
            ioR.AppendLine("- 判準：nick 是帳號的屬性，問它要的是那份憑證而不是那個人 ——"
                + " 憑證在 `Secret/` 底下，所以不需要誰上線跑指令。");
            foreach (string aId in aMissing)
            {
                var aCred = SCP_PlurkApi.LoadCredentials(m_Ctx, aId, out string aWhy);
                if (aCred == null)
                {
                    // 「這台機器上沒有可用憑證」是**當下為真**的那句話 —— 不要退回去講「請那個人跑 whoami」，
                    // 因為他跑了也補不進這棵樹（那正是 2026-09-03 那三則公開回應的成因）。
                    ioR.AppendLine($"- ⚠ `{aId}`：憑證不可用（{aWhy}）⇒ 這一筆補不了");
                    continue;
                }
                var (aSt, aBody) = Call("/APP/Users/me", aCred, null);
                if (aSt != 200)
                {
                    ioR.AppendLine($"- ⚠ `{aId}`：`/APP/Users/me` http={aSt} ⇒ 這一筆補不了"
                        + "（憑證可能已失效或被撤銷）");
                    continue;
                }
                string aNick = (PickJsonValue(aBody, "nick_name") ?? "").Trim().Trim('"');
                string aUid = PickJsonValue(aBody, "id") ?? "";
                if (string.IsNullOrEmpty(aNick))
                {
                    // 空 nick 不寫入 —— 「還沒讀過」跟「讀到空的」不得同形（SetNick 也擋，這裡先出聲）。
                    ioR.AppendLine($"- ⚠ `{aId}`：http 200 但沒讀到 `nick_name` ⇒ 不寫入（不拿空值蓋掉未知）");
                    continue;
                }
                // ⛔ 換綁偵測：**user id 才是「這是同一個帳號」的穩定鍵**（nick 會被改名）。
                //   舊值非空且對不上 ⇒ 這份憑證現在指向**別的 Plurk 帳號**，而那不是改名。
                //   ⚠ 這裡刻意只出聲不擋 —— 換綁本身是合法操作（換 token 就是換綁），
                //   但它必須被看見：舊 nick 留在表上不會有任何一層喊，而它會 @ 到前一個帳號。
                string aPrevUid = SCP_PlurkAccounts.UserIdOf(m_Ctx, aId);
                string aPrevNick = SCP_PlurkAccounts.NickOf(m_Ctx, aId);
                if (aPrevUid.Length > 0 && aUid.Length > 0 && aPrevUid != aUid)
                    ioR.AppendLine($"- 🩸 `{aId}` **換綁了**：user_id `{aPrevUid}` → `{aUid}`"
                        + $"（nick `{aPrevNick}` → `{aNick}`）—— 這不是改名，是這份憑證換到別的帳號");
                else if (aPrevUid.Length > 0 && aPrevNick.Length > 0 && aPrevNick != aNick)
                    ioR.AppendLine($"- ⚠ `{aId}` **改名了**：`{aPrevNick}` → `{aNick}`"
                        + $"（user_id `{aUid}` 沒變 ⇒ 同一個帳號）");

                SCP_PlurkAccounts.SetNick(m_Ctx, aId, aNick, aUid, "secret-scan");
                ioR.AppendLine($"- ✅ `{aId}` = `{aNick}`"
                    + (string.IsNullOrEmpty(aUid) ? "（⚠ 這次沒讀到 user_id）" : $"（user_id `{aUid}`）")
                    + "　source: `secret-scan`");
            }
        }

        // ===========================================================
        // 區塊職責：lint（形式檢查）—— 規則本體在 SCP_PlurkLint
        // 數值影響：errors 非空 ⇒ **失敗**（exit 2），因為 lint 的存在意義就是擋下。
        // ===========================================================
        void OpLint()
        {
            var aSlip = LoadSlip();
            WriteLintSection(aSlip, out var aErrors);
            if (aErrors.Count > 0)
                throw SCP_PlurkFailure.Blocked($"[Plurk] lint 擋下 {aErrors.Count} 個錯誤（詳見回傳檔）");
        }

        void WriteLintSection(SCP_PlurkSlip iSlip, out List<string> oErrors)
        {
            var ioR = Report;
            var (aTotal, aEmoLen, aEmoCount) = SCP_PlurkLint.Budget(iSlip.Body);
            var (aErr, aWarn) = SCP_PlurkLint.Check(m_Ctx, iSlip, m_Res.RequiresSignature);
            // 🩸 轉換不掉的**不在這裡補** —— `Check` 的 ⑦ 已經對同一段文案問過一次
            //   （轉換不掉的 `@gura` 還留在 body 裡，所以它一定會被 ⑦ 命中）。
            //   首版兩邊都加 ⇒ 同一條錯誤印兩次。實跑抓到的，不是讀 code 想到的。
            oErrors = aErr;
            AddValue("lint_errors", aErr.Count.ToString(CultureInfo.InvariantCulture));
            AddValue("lint_warns", aWarn.Count.ToString(CultureInfo.InvariantCulture));
            AddValue("budget", aTotal.ToString(CultureInfo.InvariantCulture));
            ioR.AppendLine();
            ioR.AppendLine("## lint（形式檢查）");
            if (iSlip.MentionNotes != null && iSlip.MentionNotes.Count > 0)
            {
                ioR.AppendLine($"- ✍ **`@persona` 已自動轉換 {iSlip.MentionNotes.Count} 處**"
                    + "（Plurk 的 @ 只認 nick；persona 名會連到同名的第三方帳號）：");
                foreach (var n in iSlip.MentionNotes) ioR.AppendLine($"    · {n}");
                ioR.AppendLine("  ⚠ 下面的字元預算算的是**轉換後**的文案 —— 轉換會變長。");
            }
            ioR.AppendLine($"- 預算: **{aTotal}** 字元　上限 {SCP_PlurkLint.Allowed(iSlip)}"
                + $"（{SCP_PlurkLint.Limit}{(iSlip.HasImage ? " − 附圖保留 " + SCP_PlurkLint.ImageReserve : "")}）"
                + $"；[emoN] {aEmoLen} 字元 × {aEmoCount} 個");
            ioR.AppendLine($"- 公開度: {(string.IsNullOrWhiteSpace(iSlip.Privacy) ? "**(未指定)**" : iSlip.Privacy)}"
                + $"　心情詞: {(string.IsNullOrWhiteSpace(iSlip.Qualifier) ? "(未指定→ says)" : iSlip.Qualifier)}");
            foreach (var w in aWarn) ioR.AppendLine($"- ⚠ {w}");
            foreach (var e in aErr) ioR.AppendLine($"- ✗ {e}");
            ioR.AppendLine();
            ioR.AppendLine(SCP_PlurkLint.Disclaimer);
        }

        // ===========================================================
        // 區塊職責：preview —— 組出**完整將送內容**但不送
        // 物理意義：端點參數名未對照官方文件 ⇒ 讓人在送之前用眼睛驗一次它到底要送什麼。
        // 數值影響：零副作用。回傳 payload 供 post 重用（同一份，不重組 —— 重組就會漂）。
        // ===========================================================
        /// <param name="iWillSend">
        /// 這一趟**接下來會不會真的送**（`op=post` ＋ `confirm=1`）。
        /// 🩸 BUG-28：本段被 `post` 重用，而標題硬寫「本 op 不送」⇒
        /// 真發出去的那一份回傳檔，開頭寫「不送」、下面寫「已送出」。
        /// 兩句都在同一個檔裡自相矛盾，而**先讀到的是錯的那句**。
        /// ⇒ 共用渲染段不可以宣告呼叫端的行為，那件事只有呼叫端知道。
        /// </param>
        void OpPreview(out Dictionary<string, string> oPayload, bool iWillSend = false)
        {
            var ioR = Report;
            var aSlip = LoadSlip();
            WriteLintSection(aSlip, out var aErrors);
            oPayload = BuildPayload(aSlip);
            ioR.AppendLine();
            ioR.AppendLine(iWillSend
                ? "## 將送的 payload（**帶了 `confirm=1` ⇒ lint 過就會送出**）"
                : "## 將送的 payload（**本 op 不送**）");
            ioR.AppendLine($"- endpoint: `POST {(string.IsNullOrEmpty(Arg("reply_to").Trim()) ? "/APP/Timeline/plurkAdd" : "/APP/Responses/responseAdd")}`");
            foreach (var kv in oPayload.OrderBy(k => k.Key, SCP_PlurkCultureLikeComparer.Instance))
            {
                ioR.AppendLine(kv.Key == "content"
                    ? $"- `content`（{kv.Value.Length} 字元）:\n\n```\n{kv.Value}\n```"
                    : $"- `{kv.Key}`: `{kv.Value}`");
            }
            if (aSlip.HasImage)
            {
                ioR.AppendLine();
                ioR.AppendLine($"⚠ **本則有附圖**：`{aSlip.Image}`");
                ioR.AppendLine($"　post 會**先上傳**（`{SCP_PlurkApi.UploadEndpoint}`）再把回傳的 URL 併進 content 末行"
                    + $" —— 實測 URL 50 字元，lint 已為此保留 {SCP_PlurkLint.ImageReserve} 字元。"
                    + "　⛔ 本 op **不上傳**。");
            }
            if (aErrors.Count > 0)
                ioR.AppendLine($"\n⛔ lint 有 {aErrors.Count} 個錯誤 ⇒ **post 會拒絕**（先修那些）。");
        }

        // ===========================================================
        // 區塊職責：post —— 真的送出（預設 dry-run）
        // 物理意義：發布不可回復。所以三道閘：① lint errors 非空一律拒絕；
        //          ② 沒有 `confirm=1` 只印不送；③ 送成功後寫 audit jsonl。
        // 數值影響：真送時對外新增一則噗（或一則回應）。audit 落 `<data_root>/Plurk/post_audit.jsonl`。
        // ===========================================================
        void OpPost()
        {
            var ioR = Report;
            bool aConfirm = Confirmed();
            OpPreview(out var aPayload, aConfirm);   // Fixes BUG-28
            var aSlip = LoadSlip();
            var (aErr, _) = SCP_PlurkLint.Check(m_Ctx, aSlip, m_Res.RequiresSignature);
            if (aErr.Count > 0)
                throw SCP_PlurkFailure.Blocked($"[Plurk] post 拒絕：lint 有 {aErr.Count} 個錯誤 —— 規則長在這條路上，繞不過");

            ioR.AppendLine();
            if (!aConfirm)
            {
                AddValue("sent", "0");
                ioR.AppendLine("## dry-run（沒帶 `confirm=1` ⇒ **什麼都沒送出**）");
                ioR.AppendLine("要真的發，重跑同一道指令並加 `--arg confirm=1`。"
                    + "⚠ 發布不可回復，Plurk 沒有 history —— 這一步刻意要人打一個字。");
                return;
            }

            var aCred = RequireCredentials();

            // ===========================================================
            // 區塊職責：附圖 —— 兩段式的接合處
            // 物理意義：先上傳拿到圖片 URL，再把那個 URL 併進 content（Plurk 自己渲染成圖）。
            //          ⇒ 順序不能顛倒：URL 是上傳的**回傳值**，不是可以先算出來的東西。
            // 數值影響：content 變長（實測 URL 50 字元）⇒ 送出前用**最終長度**再驗一次預算。
            // ⚠ 上傳成功之後才發現超長 ⇒ 圖片已經在 CDN 上（無主圖片，無害但清不掉），
            //   所以 lint 的附圖保留額度要夠（見 SCP_PlurkLint.ImageReserve 的實測值）。
            // ===========================================================
            if (aSlip.HasImage)
            {
                RequireAbsoluteExistingImage(aSlip.Image);
                var (aUpStatus, aUpBody) = SCP_PlurkApi.UploadImage(aSlip.Image, aCred);
                if (aUpStatus != 200)
                {
                    ioR.AppendLine($"- ✗ 圖片上傳失敗 http={aUpStatus}：{Trunc(aUpBody, 300)}");
                    throw SCP_PlurkFailure.Api($"[Plurk] 圖片上傳失敗 http={aUpStatus} —— **噗沒有發出去**");
                }
                string? aImgUrl = PickJsonValue(aUpBody, "full");
                if (string.IsNullOrEmpty(aImgUrl))
                    throw SCP_PlurkFailure.Api("[Plurk] 圖片上傳回 200 但拿不到 `full` URL —— 噗沒有發出去");
                ioR.AppendLine($"- 圖片已上傳: `{aImgUrl}`（{aImgUrl!.Length} 字元）");
                aPayload["content"] = aPayload["content"] + "\n" + aImgUrl;
                int aFinal = aPayload["content"].Length;
                ioR.AppendLine($"- content 併入圖片後: **{aFinal}** 字元（上限 {SCP_PlurkLint.Limit}）");
                if (aFinal > SCP_PlurkLint.Limit)
                    throw SCP_PlurkFailure.Blocked($"[Plurk] 併入圖片 URL 後超出上限（{aFinal} > {SCP_PlurkLint.Limit}）"
                        + " —— 噗沒有發出去；⚠ 圖片已上傳到 CDN（無主圖片）。請縮短文案再跑一次");
            }

            string aReplyTo = Arg("reply_to").Trim();
            string aEndpoint = aReplyTo.Length > 0 ? "/APP/Responses/responseAdd" : "/APP/Timeline/plurkAdd";
            var (aStatus, aBody) = Call(aEndpoint, aCred, aPayload);
            ioR.AppendLine("## post（已送出）");
            ioR.AppendLine($"- http: **{aStatus}**　endpoint: `{aEndpoint}`");
            if (aStatus != 200)
            {
                ioR.AppendLine($"- ✗ body（前 400 字）: {Trunc(aBody, 400)}");
                throw SCP_PlurkFailure.Api($"[Plurk] post 失敗 http={aStatus}（內容未發出；詳見回傳檔）");
            }
            string aPlurkId = PickJsonValue(aBody, "plurk_id") ?? PickJsonValue(aBody, "id") ?? "?";
            ioR.AppendLine($"- plurk_id: **{aPlurkId}**");
            AddValue("sent", "1");
            AddValue("plurk_id", aPlurkId);
            if (aReplyTo.Length > 0) AddValue("reply_to", aReplyTo);
            WriteAudit(aSlip, aPayload, aPlurkId, aReplyTo);
            // ⚠ 這一行印的是**台帳在哪**，不是「寫成功了」—— 失敗的話上一句已經在 ioR 裡喊過。
            //   台帳按 data_root 分裂（TASK-0184）⇒ 路徑本身就是那筆帳的定語，要印全的。
            ioR.AppendLine($"- audit: `{AuditPath()}`（append-only）");
            VerifyNotDuplicated(aPayload, aReplyTo, aCred);
        }

        // ===========================================================
        // 區塊職責：發文之後**回讀對面** —— 問「我這一則在那邊出現了幾次」。
        // 物理意義：TASK-0259。2026-09-21 量到的形狀是 **一次 OpPost、一次 WriteAudit、兩則回應**
        //          （噗 `358787423748926`：同一份內容 sha16 `4dc35118208721de` 在 Plurk 上有兩個
        //            response id、相隔 12 秒，而 `post_audit.jsonl` 同一個雜湊只有一行）
        //          ⇒ 重送發生在 `OpPost` **以下**（連線重用時的透明重試／對方基礎設施），
        //            ⛔ 不在這支控制得到的那一層 —— 所以這裡不做「不再送第二次」，做「送完去問」。
        // 📐 修法等級（「讓失敗不可能」＞「當場喊」＞「記得注意」）：
        //          Plurk 的 API **沒有冪等鍵** ⇒ 做不到第一級；取第二級。
        // ⚠ 去重一律**先用 id**：Plurk 自己會在同一個陣列裡回同一則兩次（2026-08-24 實測，
        //   見 `OpResponses` 那一段血證）⇒ 不先去重的話，每一則正常發文都會被誤報成重複。
        // ⚠ 分母也要印（我的則數／陣列筆數／相異 id 數）—— 只印一個「重複 0 則」的話，
        //   「真的沒重複」與「我根本沒數到自己那一則」同形。
        // ⛔ 而回讀失敗一律說成**判不了**，不說「沒有重複」—— 兩者的處置相反。
        // ===========================================================
        void VerifyNotDuplicated(Dictionary<string, string> iPayload, string iReplyTo, Dictionary<string, string> iCred)
        {
            var ioR = Report;
            ioR.AppendLine();
            ioR.AppendLine("## 回讀：這一則在對面出現了幾次（TASK-0259）");
            string aContent = (iPayload != null && iPayload.TryGetValue("content", out string? aC) ? aC : "").Trim();
            if (aContent.Length == 0)
            {
                ioR.AppendLine("- ⚠ 送出的 `content` 是空的 ⇒ **這一格判不了**（⛔ 不是「沒有重複」）");
                return;
            }

            // 「哪一則是我發的」問一次就好 —— 不猜、不寫死 id（同 OpTimeline 那一格）
            var (aMeSt, aMeBody) = Call("/APP/Users/me", iCred, null);
            string aMeId = aMeSt == 200 ? (PickJsonValue(aMeBody, "id") ?? "") : "";
            if (aMeId.Length == 0)
            {
                ioR.AppendLine($"- ⚠ 問不到自己的 user id（http={aMeSt}）⇒ **這一格判不了**（⛔ 不是「沒有重複」）");
                return;
            }

            bool aIsResponse = iReplyTo.Length > 0;
            string aEndpoint = aIsResponse ? "/APP/Responses/get" : "/APP/Timeline/getPlurks";
            var aParams = aIsResponse
                ? new Dictionary<string, string> { { "plurk_id", iReplyTo }, { "from_response", "0" } }
                : new Dictionary<string, string> { { "limit", "20" }, { "filter", "only_user" } };
            var (aSt, aBody) = Call(aEndpoint, iCred, aParams);
            if (aSt != 200)
            {
                ioR.AppendLine($"- ⚠ 回讀失敗（http={aSt}　`{aEndpoint}`）⇒ **這一格判不了**（⛔ 不是「沒有重複」）");
                return;
            }
            var aRoot = SafeParse(aBody);
            string aKey = aIsResponse ? "responses" : "plurks";
            // 🩸 兩個端點的**唯一鍵欄位名不同**（2026-09-21 活體量到）：
            //   `/APP/Responses/get` 的每一筆是 `id`；`/APP/Timeline/getPlurks` 是 `plurk_id`，**沒有 `id`**。
            //   ⛔ 兩條路共用 `"id"` 的後果不是報錯，是**去重 key 全成空字串** ⇒ 只留得下第一筆
            //   ⇒ 時間軸那條**結構上永遠看不到第二筆**，於是它永遠偵測不到重複，而畫面上印的是 ✅。
            //   📌 抓到它的是本段刻意印出來的分母（陣列筆數 vs 相異 id 數），不是任何一層警告。
            string aIdKey = aIsResponse ? "id" : "plurk_id";
            var aList = (aRoot != null && aRoot.Contains(aKey)) ? aRoot[aKey] : null;
            if (aList == null || !aList.IsArray)
            {
                ioR.AppendLine($"- ⚠ 回讀的 body 裡沒有 `{aKey}` 陣列 ⇒ **這一格判不了**（格式跟我預期的不一樣）");
                return;
            }

            var aSeenId = new HashSet<string>();
            var aMine = new List<string>();
            var aSame = new List<string>();
            var aByContent = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            for (int i = 0; i < aList.Count; i++)
            {
                var aIt = aList[i];
                string aId = JsonScalar(aIt, aIdKey);
                if (aId.Length == 0) continue;                       // ⛔ 沒有唯一鍵就不數，不拿空字串當一筆
                if (!aSeenId.Add(aId)) continue;                     // ⚠ Plurk 會回重複的同一則
                if (JsonScalar(aIt, "user_id") != aMeId) continue;
                aMine.Add(aId);
                string aRaw = UnescapeJson(JsonScalar(aIt, "content_raw")).Trim();
                if (aRaw == aContent) aSame.Add(aId);
                // 同一份內容底下掛著哪幾個 id —— 之後用來數「我先前留下的重複組」
                if (!aByContent.TryGetValue(aRaw, out var aIds)) aByContent[aRaw] = aIds = new List<string>();
                aIds.Add(aId);
            }

            string aWhere = aIsResponse ? $"噗 `{iReplyTo}` 這一串" : "自己的時間軸前 20 則";
            ioR.AppendLine($"- 範圍: {aWhere}　（陣列 **{aList.Count}** 筆／相異 id **{aSeenId.Count}** 個）");
            ioR.AppendLine($"- 其中**我發的**: **{aMine.Count}** 則");
            ioR.AppendLine($"- 其中與這次內容**逐字相同**的: **{aSame.Count}** 則");
            AddValue("same_content_count", aSame.Count.ToString(CultureInfo.InvariantCulture));

            // ⚠ 先前留下的重複（⛔ 不是這一次造成的）—— 同一份回讀就數得出來，
            //   而它回答的是「還有哪幾則躺在那裡要手動刪」。0 組時整段不印，不佔版面。
            var aOldDup = new List<string>();
            foreach (var aKv in aByContent)
            {
                if (aKv.Value.Count < 2) continue;
                if (aKv.Key == aContent) continue;          // 這一次那一組在上面已經講過了
                aOldDup.Add($"{aKv.Value.Count} 則 `" + string.Join("` / `", aKv.Value) + "`");
            }
            if (aOldDup.Count > 0)
            {
                ioR.AppendLine($"- ⚠ 而這個範圍內另有 **{aOldDup.Count}** 組**先前留下的**重複"
                    + "（⛔ 不是這一次造成的，但它們還在對外顯示）:");
                foreach (string aLine in aOldDup) ioR.AppendLine("　　· " + aLine);
            }

            if (aSame.Count == 1)
            {
                ioR.AppendLine("- ✅ 沒有重複（1 ＝ 只有剛剛那一則）");
                return;
            }
            if (aSame.Count == 0)
            {
                ioR.AppendLine("- ⚠ **一則都沒對上** ⇒ **這一格判不了**（⛔ 不是「沒有重複」）——"
                    + "剛發出去的那一則本來就該在裡面。可能是對面還沒讀到、或送出的 content 與回讀的"
                    + " `content_raw` 不逐字相同（例如被對方改寫）。");
                return;
            }
            ioR.AppendLine($"- 🔴 **重複了：同一份內容有 {aSame.Count} 則** —— id: `"
                + string.Join("` / `", aSame) + "`");
            ioR.AppendLine("　　⚠ ⛔ 這**不是**你多按了一次：TASK-0259 量到重送發生在 `OpPost` **以下**"
                + "（一次送出、台帳只有一行、對面兩則）。");
            ioR.AppendLine("　　⛔ 本 build 的 `plurk` Cmd **沒有刪除 op** ⇒ 只能上 Plurk 網頁 UI 手動刪掉多的那幾則。");
            Notices.Add($"🔴 重複了：同一份內容在對面有 {aSame.Count} 則（{string.Join(" / ", aSame)}）—— 上 Plurk 網頁 UI 手動刪多的");
        }

        // ===========================================================
        // 區塊職責：op=upload —— 單獨驗上傳端點（不建立噗）
        // 物理意義：先驗端點再接流程。這一步拿到的是**真實 URL 長度**，
        //          而那個長度決定 lint 該替附圖保留多少字元預算（不是憑估）。
        // 數值影響：對外寫入（CDN 上多一張無主圖片）⇒ 要 `confirm=1`。
        // ===========================================================
        void OpUpload()
        {
            var ioR = Report;
            string aPath = Arg("image").Trim();
            if (aPath.Length == 0)
                throw SCP_PlurkFailure.Blocked("[Plurk] op=upload 需要 --arg image=<圖片的絕對路徑>");
            RequireAbsoluteExistingImage(aPath);
            var aCred = RequireCredentials();

            ioR.AppendLine();
            ioR.AppendLine("## upload（**對外寫入** —— CDN 上會留下一張圖）");
            ioR.AppendLine($"- 檔案: `{aPath}`（{SCP_PlurkApi.FormatSize(new FileInfo(aPath).Length)}, {SCP_PlurkApi.GuessMime(aPath)}）");
            if (!Confirmed())
            {
                ioR.AppendLine("- **dry-run**：沒帶 `confirm=1` ⇒ 什麼都沒上傳。");
                return;
            }
            var (aStatus, aBody) = SCP_PlurkApi.UploadImage(aPath, aCred);
            ioR.AppendLine($"- http: **{aStatus}**　endpoint: `{SCP_PlurkApi.UploadEndpoint}`");
            if (aStatus != 200)
            {
                ioR.AppendLine($"- ✗ body（前 400 字）: {Trunc(aBody, 400)}");
                throw SCP_PlurkFailure.Api($"[Plurk] 上傳失敗 http={aStatus}"
                    + "（判準：先確認端點與欄位名，再懷疑 multipart 的簽章 —— 兩者都是 4xx）");
            }
            string? aFull = PickJsonValue(aBody, "full");
            string? aThumb = PickJsonValue(aBody, "thumbnail");
            ioR.AppendLine($"- full: `{aFull}`（**{(aFull ?? "").Length} 字元** —— 這個長度會吃掉 content 預算）");
            if (aThumb != null) ioR.AppendLine($"- thumbnail: `{aThumb}`");
            if (string.IsNullOrEmpty(aFull))
            {
                ioR.AppendLine($"- ⚠ 回應裡沒有 `full` 欄位。body（前 300 字）: {Trunc(aBody, 300)}");
                throw SCP_PlurkFailure.Api("[Plurk] 上傳回 200 但拿不到圖片 URL —— 欄位名可能不是 `full`");
            }
            AddValue("full", aFull!);
        }

        // 區塊職責：圖片路徑的硬性檢查（Tim 2026-08-21：**要完整路徑，不吃相對路徑**）
        // 物理意義：相對路徑會相對於宿主的工作目錄（那是啟動處，而不是交付單所在的位置）
        //          ⇒ 同一份交付單在不同地方跑會指到不同檔，而**檔案不存在的失敗發生在上傳那一刻**，
        //          不是在 lint。所以這條擋在前面。
        static void RequireAbsoluteExistingImage(string iPath)
        {
            if (!Path.IsPathRooted(iPath))
                throw SCP_PlurkFailure.Blocked($"[Plurk] 圖片要**絕對路徑**：'{iPath}' 是相對路徑"
                    + "（相對路徑會相對於 Editor 的工作目錄，同一份交付單換個地方跑就指到別的檔）");
            if (!File.Exists(iPath))
                throw SCP_PlurkFailure.Blocked($"[Plurk] 圖片不存在：{iPath}");
        }

        // ===========================================================
        // 區塊職責：op=get —— **唯讀回讀**一則已發出的噗（驗「它真的在那裡」）
        // 物理意義：`post` 回的 200 ＋ `plurk_id` 只證明**送出被接受**。
        //          「公開度真的生效了嗎」「用哪個帳號發的」「附圖真的被渲染成 `<img>` 嗎」
        //          —— 這三件都**不是從 plurk_id 推得出來的**，要去問對方。
        // 🩸 2026-08-21：唯讀診斷 `plurk.py` 移除之後，這條線只剩「送出」那一半有工具，
        //          當天最後一則（第一次走「所有人」）因此只驗到 200，沒驗到它真的公開。
        //          ⇒ 把歸路搬進 Cmd（Tim 2026-08-21：「CMD 流程應該可以跑驗證」）。
        // 數值影響：純唯讀（`/APP/Timeline/getPlurk`），不改任何 Plurk 資料。
        // ===========================================================
        void OpGet()
        {
            var ioR = Report;
            string aId = Arg("plurk_id").Trim();
            if (aId.Length == 0)
                throw SCP_PlurkFailure.Blocked("[Plurk] op=get 需要 --arg plurk_id=<發文回傳的 id>");
            var aCred = RequireCredentials();
            var (aStatus, aBody) = Call("/APP/Timeline/getPlurk", aCred,
                new Dictionary<string, string> { { "plurk_id", aId } });

            ioR.AppendLine();
            ioR.AppendLine($"## get（唯讀回讀 —— 驗「它真的在那裡」）");
            ioR.AppendLine($"- http: **{aStatus}**　plurk_id: `{aId}`");
            if (aStatus != 200)
            {
                ioR.AppendLine($"- ✗ body（前 300 字）: {Trunc(aBody, 300)}");
                throw SCP_PlurkFailure.Api($"[Plurk] getPlurk 失敗 http={aStatus}");
            }
            // 回應是 { "plurk": {...}, "user": {...} } —— 取內層那顆
            string aPlurk = ExtractObject(aBody, "plurk");
            string? aOwner = PickJsonValue(aPlurk, "owner_id");
            string? aLimited = PickJsonValue(aPlurk, "limited_to");
            string aRaw = PickJsonValue(aPlurk, "content_raw") ?? "";
            string aHtml = PickJsonValue(aPlurk, "content") ?? "";
            ioR.AppendLine($"- owner_id: **{aOwner ?? "?"}**"
                + "（比對 `op=whoami` 的 `id` ⇒ 這則到底是哪個帳號發的）");
            ioR.AppendLine($"- limited_to: **{(string.IsNullOrEmpty(aLimited) ? "(無 ⇒ 公開)" : aLimited)}**"
                + "　⚠ 存回來的格式與送出的**不同形**（送 `[0]`、存 `|0|`）⇒ 別拿送出的值比對");
            ioR.AppendLine($"- qualifier: {PickJsonValue(aPlurk, "qualifier") ?? "?"}"
                + $"　posted: {PickJsonValue(aPlurk, "posted") ?? "?"}");
            AddValue("owner_id", aOwner ?? "");
            // 🩸 2026-08-23：本 op 原本只印首行 —— 那對「驗它在不在」夠用，
            //   但要**回應別人**時，只讀首行等於對著一句話的開頭講話。
            //   ⇒ 全文印出來（截 800 字，而且截了會說）。
            string aFull = UnescapeJson(aRaw);
            // 表情反解析標在全文上；⚠ 標註會**加長字串**，所以字元數用標註前的算
            int aFullLen = aFull.Length;
            var aEmoCtx = EmoBegin();
            aFull = EmoAnnotatePaired(aFull, UnescapeJson(aHtml), aEmoCtx, aOwner);
            ioR.AppendLine($"- content_raw（{aFullLen} 字元"
                + (aFull.Length == aFullLen ? "" : "；下面的 `⟨…⟩` 是表情標註，不在原文裡") + "）:");
            ioR.AppendLine();
            ioR.AppendLine("```");
            ioR.AppendLine(aFull.Length <= 800 ? aFull
                : aFull.Substring(0, 800) + "\n…（截斷 —— 全文比這長，別拿這段當全部）");
            ioR.AppendLine("```");
            // 附圖驗的是**渲染**不是字串：content_raw 有 URL 只證明我送進去了
            bool aHasImg = aHtml.Contains("<img");
            ioR.AppendLine($"- 渲染成 `<img>`: **{(aHasImg ? "是" : "否")}**"
                + "（附圖那則要看這格 —— `content_raw` 裡有 URL 只證明我送進去了，Plurk 認不認是另一回事）");
            EmoEnd(aEmoCtx);
        }

        // ===========================================================
        // 區塊職責：payload 組裝
        // 物理意義：`qualifier` 是心情詞（Plurk 的固定詞彙表，非自由字串 ⇒ 認不得就退 `says`）；
        //          公開度靠 `limited_to`：**沒有這個參數就是公開**（所以「沒指定」必須在 lint 就擋下，
        //          不能讓它一路走到這裡變成預設公開 —— summit 2026-08-21 指出的漏洞）。
        // ⚠ 未對照官方文件：`limited_to` 的值格式（`[0]`＝僅朋友）取自社群慣例，preview 會印出來讓人看。
        // ===========================================================
        static readonly Dictionary<string, string> QualifierMap = new Dictionary<string, string>
        {
            { "覺得", "feels" }, { "說", "says" }, { "想", "thinks" }, { "哭", "cries" },
            { "正在", "is" }, { "分享", "shares" }, { "問", "asks" }, { "希望", "hopes" },
            { "愛", "loves" }, { "討厭", "hates" }, { "需要", "needs" }, { "有", "has" },
        };

        Dictionary<string, string> BuildPayload(SCP_PlurkSlip iSlip)
        {
            var aOut = new Dictionary<string, string>();
            string aReplyTo = Arg("reply_to").Trim();
            if (aReplyTo.Length > 0)
            {
                aOut["plurk_id"] = aReplyTo;
                aOut["content"] = iSlip.Body;
                aOut["qualifier"] = MapQualifier(iSlip.Qualifier);
                return aOut;                        // 回應沒有公開度參數（跟著母噗）
            }
            aOut["content"] = iSlip.Body;
            aOut["qualifier"] = MapQualifier(iSlip.Qualifier);
            string aPrivacy = (iSlip.Privacy ?? "").Trim();
            if (aPrivacy == "只限朋友" || aPrivacy.Equals("friends", StringComparison.OrdinalIgnoreCase))
                aOut["limited_to"] = "[0]";         // 社群慣例：[0] = 僅好友可見
            else if (aPrivacy == "本人" || aPrivacy.Equals("self", StringComparison.OrdinalIgnoreCase))
                aOut["limited_to"] = "[]";          // ⚠ 未驗證：空清單是否等於只有自己
            // 「所有人」⇒ 不帶 limited_to（公開）
            return aOut;
        }

        static string MapQualifier(string? iWord)
        {
            string w = (iWord ?? "").Trim();
            if (w.Length == 0) return "says";
            return QualifierMap.TryGetValue(w, out string? v) ? v : "says";
        }

        SCP_PlurkSlip LoadSlip()
        {
            string aFile = Arg("slip_file").Trim();
            if (aFile.Length == 0)
                throw SCP_PlurkFailure.Blocked("[Plurk] 需要 --arg slip_file=<交付單檔案>"
                    + "（四欄格式見 `senate cmd doc --arg op=show --arg name=Plurk_Posting` §2；長文一律走檔案不走參數）");
            if (!File.Exists(aFile)) throw SCP_PlurkFailure.Blocked($"[Plurk] 找不到交付單：{aFile}");
            var aSlip = SCP_PlurkLint.Parse(File.ReadAllText(aFile, Encoding.UTF8));

            // 區塊職責：`@persona` → `@nick[→persona]` 自動轉換（Tim 2026-09-03 拍板）。
            // 物理意義：**放這裡而不是放各 op** —— lint／preview／post 走同一個 LoadSlip，
            //          分三處寫就會漂，而漂掉的那一處剛好是真的送出去的那一條路。
            // 數值影響：改寫**在 Budget/Check 之前** —— 它會改變長度
            //          （`@gura` 5 字 → `@hololive_myth→gura` 20 字），
            //          先算預算再改寫的話那個數字是假的。
            aSlip.Body = SCP_PlurkLint.RewriteMentions(m_Ctx, aSlip.Body,
                out var aNotes, out var aProblems);
            aSlip.MentionNotes = aNotes;
            aSlip.MentionProblems = aProblems;
            return aSlip;
        }

        // ===========================================================
        // 區塊職責：audit —— 發出去的東西留一筆不可回復動作的帳
        // 物理意義：Plurk 沒有 history ⇒ 對帳只能靠自己留。內容存 **SHA256 前 16 位**不存全文
        //          （全文在 Plurk 上；這裡要的是「這則是不是我發的」而不是再存一份）。
        // 數值影響：append 一行 jsonl（UCL `ToJson()` 的位元組形狀 —— `\u` 轉義、LF 結尾，與既有台帳同形）；
        //          寫失敗**同時**進回傳檔與 CLI 輸出（見下）。
        //
        // 🩸 2026-09-10（TASK-0184）這一區塊被兩件事同時咬過，而兩隻都是**靜默**的：
        //   ① 台帳路徑長在資料根底下，而 `data_root` 是可 override 的
        //      ⇒ **每棵資料樹一份帳**。而一行帳不說自己是哪棵樹寫的，於是
        //      「這棵樹沒記到」與「根本沒記到」**逐位元組同形**。
        //      現場：我在 `D:/Unity/Bar/...` 那棵樹上找 09-09 17:1x 那 4 則，報「漏記」並開了本單；
        //      隔天站在 `D:/Unity/LY/...` 一撈就在（`2026-09-09T09:12:23Z`）。一小時 git 考古全白費。
        //      ⇒ 修法＝每行自帶定語（`host` / `data_root` / `git_ref`），**不是**把帳搬去別的地方
        //        （搬儲存位置是政策不是 bug 修法，而在成因未收斂時動它是拿穩定性換乾淨）。
        //   ② 寫失敗只 `LogError` ⇒ **agent 讀不到**（回傳檔是 agent 唯一會讀的那格）。
        //      於是「沒寫」與「寫失敗」也同形，而兩者的處置相反（一個補發、一個修寫入端）。
        //      ⇒ 失敗一律進回傳檔。⛔ 但仍然不 throw：噗已經發出去了，這裡拋只會把
        //        「對外動作成功」報成整支失敗 —— 那是把一個更貴的假象換進來。
        // ===========================================================
        string AuditPath()
            => Path.Combine(m_Ctx.DataRoot, AuditRelative).Replace('\\', '/');

        // 區塊職責：讀出「這一行是哪條 git ref 寫的」
        // 物理意義：`AgentCommands` 在各專案是 **submodule** ⇒ 它的 `.git` 是**檔案**不是目錄
        //          （內容 `gitdir: ../.git/modules/...`）⇒ 直接接 `.git/HEAD` 會撈不到。
        // ⚠ 失敗一律回一個**看得出是失敗的字串**，⛔ 不回空字串 ——
        //   空字串與「這個欄位沒被寫」同形，而人往空格裡填的一定是成功。
        // 🩸 而首版就在同一句話上失手（2026-09-10，反射實跑當場抓到）：
        //   「目錄不存在」與「目錄在但不是 git 樹」都回 `(no-git)` ⇒ **兩個處置相反的情況同形**
        //   （前者是 `data_root` 設錯 —— 那筆帳可能根本沒落地；後者是資料根不在版控下 —— 政策問題）。
        //   ⇒ 拆成 `(no-dir)` / `(not-in-git)`。⛔ 不是把註解寫得更好，是把那個共用的值拿掉。
        // ⚠ 往上走找 `.git`（git 自己就是這樣找的）：`data_root` 若被指到 repo 的**子目錄**，
        //   不往上走就會回「不在版控下」——而那棵樹其實在版控下。**有出處的假定語比未知更毒。**
        public static string ResolveGitRef(string? iDataRoot)
        {
            try
            {
                if (string.IsNullOrEmpty(iDataRoot)) return "(no-data-root)";
                if (!Directory.Exists(iDataRoot)) return "(no-dir)";

                // 從 iDataRoot 往上找第一個帶 `.git` 的祖先（含自己）
                string? aDotGit = null;
                for (DirectoryInfo? aDir = new DirectoryInfo(Path.GetFullPath(iDataRoot)); aDir != null; aDir = aDir.Parent)
                {
                    string aCandidate = Path.Combine(aDir.FullName, ".git");
                    if (Directory.Exists(aCandidate) || File.Exists(aCandidate)) { aDotGit = aCandidate; break; }
                }
                if (aDotGit == null) return "(not-in-git)";
                string aOwnerDir = Path.GetDirectoryName(aDotGit) ?? "";

                string aGitDir;
                if (Directory.Exists(aDotGit)) aGitDir = aDotGit;
                else
                {
                    // submodule / worktree：`gitdir: <path>`（相對路徑相對於 `.git` 所在目錄）
                    string aLine = File.ReadAllText(aDotGit).Trim();
                    const string aPrefix = "gitdir:";
                    if (!aLine.StartsWith(aPrefix, StringComparison.Ordinal)) return "(gitdir-unparsed)";
                    string aRel = aLine.Substring(aPrefix.Length).Trim();
                    aGitDir = Path.IsPathRooted(aRel) ? aRel : Path.GetFullPath(Path.Combine(aOwnerDir, aRel));
                }

                string aHeadPath = Path.Combine(aGitDir, "HEAD");
                if (!File.Exists(aHeadPath)) return "(no-head)";
                string aHead = File.ReadAllText(aHeadPath).Trim();

                // detached：HEAD 直接是 sha
                if (!aHead.StartsWith("ref:", StringComparison.Ordinal))
                    return "(detached)@" + Short(aHead);

                string aRefName = aHead.Substring(4).Trim();              // refs/heads/<branch>
                string aShortName = aRefName.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? aRefName.Substring("refs/heads/".Length) : aRefName;
                string aRefFile = Path.Combine(aGitDir, aRefName.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(aRefFile)) return aShortName + "@" + Short(File.ReadAllText(aRefFile).Trim());

                // ref 檔不在 ⇒ 走 packed-refs（剛 clone / gc 過的 repo 是這個形狀）
                string aPacked = Path.Combine(aGitDir, "packed-refs");
                if (File.Exists(aPacked))
                {
                    foreach (string aRow in File.ReadAllLines(aPacked))
                    {
                        if (aRow.Length == 0 || aRow[0] == '#' || aRow[0] == '^') continue;
                        int aSp = aRow.IndexOf(' ');
                        if (aSp > 0 && aRow.Substring(aSp + 1).Trim() == aRefName)
                            return aShortName + "@" + Short(aRow.Substring(0, aSp));
                    }
                }
                return aShortName + "@(unresolved)";
            }
            catch (Exception ex) { return "(git-read-failed: " + ex.GetType().Name + ")"; }

            static string Short(string iSha) => iSha.Length >= 7 ? iSha.Substring(0, 7) : iSha;
        }

        void WriteAudit(SCP_PlurkSlip iSlip, Dictionary<string, string> iPayload, string iPlurkId, string iReplyTo)
        {
            var ioR = Report;
            try
            {
                string aPath = AuditPath();
                Directory.CreateDirectory(Path.GetDirectoryName(aPath) ?? ".");
                string aHash;
                using (var aSha = SHA256.Create())
                {
                    aHash = BitConverter.ToString(aSha.ComputeHash(Encoding.UTF8.GetBytes(iSlip.Body)))
                        .Replace("-", "").ToLowerInvariant().Substring(0, 16);
                }
                var aJd = new SCP_UclLegacyObject();
                aJd.Set("at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                aJd.Set("persona", iSlip.Persona ?? "");
                aJd.Set("account", m_Res.SecretId ?? "");
                aJd.Set("source", m_Res.Source ?? "");
                aJd.Set("privacy", iSlip.Privacy ?? "");
                aJd.Set("limited_to", iPayload.TryGetValue("limited_to", out string? lt) ? lt : "(public)");
                aJd.Set("reply_to", iReplyTo ?? "");
                aJd.Set("body_sha256_16", aHash);
                aJd.Set("body_len", iSlip.Body.Length);
                aJd.Set("plurk_id", iPlurkId ?? "");
                // ⭐ 定語三欄（TASK-0184，**純新增** —— 既有欄位與讀取端一個字不動）：
                //    讓一行帳說得出自己是「哪台機器、哪棵資料樹、哪條 git ref」寫的。
                string aDataRoot = m_Ctx.DataRoot;
                aJd.Set("host", SafeHost());
                aJd.Set("data_root", (aDataRoot ?? "").Replace('\\', '/'));
                aJd.Set("git_ref", ResolveGitRef(aDataRoot));
                File.AppendAllText(aPath, SCP_UclLegacyJson.ToJson(aJd) + "\n", new UTF8Encoding(false));
            }
            catch (Exception ex)
            {
                // ⚠ 兩個出口都要走：CLI 輸出給人看，回傳檔給 agent 看。
                //   只寫其中一邊的話，另一邊「沒寫」與「寫失敗」同形（而處置相反）。
                Notices.Add($"⚠ audit 寫入失敗（噗已經發出去了，plurk_id {iPlurkId}，這筆帳要手動補）：{ex.Message}");
                ioR.AppendLine($"- ⚠ **audit 寫入失敗**（{ex.GetType().Name}）：{ex.Message}");
                ioR.AppendLine($"　　路徑: `{AuditPath()}`");
                ioR.AppendLine($"　　⛔ **噗已經發出去了**（plurk_id `{iPlurkId}`）—— 失敗的是**記帳**不是發文，"
                    + "別重發；這筆帳要手動補。");
            }
        }

        // 主機名 —— 取不到也要回一個看得出是「取不到」的值（⛔ 不回空字串）
        static string SafeHost()
        {
            try
            {
                string aName = Environment.MachineName;
                return string.IsNullOrEmpty(aName) ? "(host-empty)" : aName;
            }
            catch { return "(host-unavailable)"; }
        }
    }
}
