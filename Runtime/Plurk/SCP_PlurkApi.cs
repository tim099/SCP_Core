// 區塊職責：Plurk API 的**連線層** —— OAuth 1.0a 簽章、表單／multipart 請求、憑證讀取。
// 物理意義：⛔ **SCP_Core 不碰網路**（2026-09-23 拍板，見 `SCP_RateSource.cs` 守衛①）：
//            HTTP 一律走宿主注入的 `SCP_HttpFetch.Current as ISCP_HttpFormRequester`；
//            沒注入 ⇒ **大聲失敗**，⛔ 不退回任何別的送法（那只會換來一個看起來像簽章錯的 4xx）。
//          簽章本體逐字照搬 Unity 版（RFC 3986 `Q()`、HMAC-SHA1、RandomNumberGenerator nonce）。
// 數值影響：每次 `Call` 一個 HTTPS POST（同步；逾時 25 秒，上傳 60 秒 —— 同 Unity 版）。
//          ⛔ 憑證值一律不印、不進例外訊息（外洩沒有錯誤訊息）。
// ⚠ 連線層失敗（逾時／DNS，`TryPostForm` 回 false）⇒ **丟例外**，⛔ 不壓成 `http=0` 往下走：
//   Unity 版那一格是 `HttpClient` 自己丟出來的；而逾時時**對方可能已經收到了** ——
//   壓成 `(0, "")` 的話 post 那條會印「內容未發出」，那句話在這個情況下是假的。
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
using SCP.Core.Market;

namespace SCP.Core.Plurk
{
    public static class SCP_PlurkApi
    {
        public const string ApiBase = "https://www.plurk.com";
        // plurk.com 在 Cloudflare 後面：預設 .NET/urllib UA 會被 WAF 依瀏覽器簽章擋掉，
        // 回 **403 ＋ body `error code: 1010`**（Cloudflare 的碼，不是 Plurk API 的錯誤格式）。
        // 🩸 basecamp 2026-08-21：那個 403 跟「簽章錯」「端點不存在」長得一樣，而它連應用層都沒碰到。
        public const string UserAgent = "UCL-PlurkBot/0.1 (+https://github.com/Persona9999)";
        // 上傳端點與欄位名取自社群慣例（官方 API 頁抓不到）——「驗證狀態」見 Plurk_Maintenance §5
        public const string UploadEndpoint = "/APP/Timeline/uploadPicture";

        public const int CallTimeoutSec = 25;
        public const int UploadTimeoutSec = 60;

        // ===========================================================
        // 區塊職責：OAuth 1.0a 簽章（HMAC-SHA1）
        // 物理意義：base = METHOD & percent(url) & percent(排序後參數)；key = percent(cs) & percent(ts)。
        //          三處都要 RFC 3986 percent-encoding（`-._~` 之外全編碼）——
        //          少編一個字元就只會回 4xx，而它不會說是哪一格錯。
        // 數值影響：純計算。nonce 走 RandomNumberGenerator（簽章材料，不用 System.Random）。
        // ===========================================================
        public static string Q(string? iValue)
        {
            const string aUnreserved = "-._~";
            var sb = new StringBuilder();
            foreach (byte b in Encoding.UTF8.GetBytes(iValue ?? ""))
            {
                char c = (char)b;
                if (char.IsLetterOrDigit(c) && b < 128 || aUnreserved.IndexOf(c) >= 0) sb.Append(c);
                else sb.Append('%').Append(b.ToString("X2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static string OAuthHeader(string iMethod, string iUrl, Dictionary<string, string> iCred,
            Dictionary<string, string>? iParams)
        {
            var aNonce = new byte[16];
            using (var aRng = RandomNumberGenerator.Create()) aRng.GetBytes(aNonce);
            var aOAuth = new Dictionary<string, string>
            {
                { "oauth_consumer_key", iCred["consumer_key"] },
                { "oauth_token", iCred["access_token"] },
                { "oauth_signature_method", "HMAC-SHA1" },
                { "oauth_timestamp", ((long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds).ToString(CultureInfo.InvariantCulture) },
                { "oauth_nonce", BitConverter.ToString(aNonce).Replace("-", "").ToLowerInvariant() },
                { "oauth_version", "1.0" },
            };
            var aAll = new Dictionary<string, string>(aOAuth);
            if (iParams != null) foreach (var kv in iParams) aAll[kv.Key] = kv.Value;
            string aNorm = string.Join("&", aAll.OrderBy(k => k.Key, StringComparer.Ordinal)
                .Select(kv => $"{Q(kv.Key)}={Q(kv.Value)}"));
            string aBase = $"{iMethod.ToUpperInvariant()}&{Q(iUrl)}&{Q(aNorm)}";
            string aKey = $"{Q(iCred["consumer_secret"])}&{Q(iCred["access_token_secret"])}";
            using (var aMac = new HMACSHA1(Encoding.UTF8.GetBytes(aKey)))
            {
                aOAuth["oauth_signature"] = Convert.ToBase64String(
                    aMac.ComputeHash(Encoding.UTF8.GetBytes(aBase)));
            }
            return "OAuth " + string.Join(", ", aOAuth.OrderBy(k => k.Key, StringComparer.Ordinal)
                .Select(kv => $"{Q(kv.Key)}=\"{Q(kv.Value)}\""));
        }

        /// <summary>宿主的表單請求器；沒有 ⇒ 大聲失敗（見檔頭）。</summary>
        static ISCP_HttpFormRequester Requester()
        {
            if (SCP_HttpFetch.Current is ISCP_HttpFormRequester aReq) return aReq;
            throw SCP_PlurkFailure.Api("[Plurk] 本宿主不能送帶標頭的表單（`SCP_HttpFetch.Current` 沒有實作 ISCP_HttpFormRequester"
                + (SCP_HttpFetch.Current == null ? "；而且根本沒有注入抓取器" : "：" + SCP_HttpFetch.Current.FetcherName)
                + "）⇒ 一個請求都沒送。⛔ 不退回別的送法 —— 少了簽章標頭的請求只會回一個看起來像簽章錯的 4xx。");
        }

        /// <summary>一般 API 呼叫：form-urlencoded POST，簽章含 body 參數。回 (http 狀態, body)。</summary>
        public static (int status, string body) Call(string iPath, Dictionary<string, string> iCred,
            Dictionary<string, string>? iParams)
        {
            string aUrl = ApiBase + iPath;
            var aReq = Requester();
            var aHeaders = new Dictionary<string, string>
            {
                { "Authorization", OAuthHeader("POST", aUrl, iCred, iParams) },
                { "User-Agent", UserAgent },
            };
            var aFields = iParams == null
                ? new List<KeyValuePair<string, string>>()
                : new List<KeyValuePair<string, string>>(iParams);
            if (!aReq.TryPostForm(aUrl, aHeaders, aFields, null, CallTimeoutSec,
                    out string aBody, out int aStatus, out string? aError))
                throw SCP_PlurkFailure.Api($"[Plurk] `POST {iPath}` 連線失敗（沒有拿到 HTTP 回應）：{aError}"
                    + " —— ⚠ 逾時的話**對方可能已經收到了**，對外寫入的 op 先回讀再決定要不要重送");
            return (aStatus, aBody ?? "");
        }

        // ===========================================================
        // 區塊職責：圖片上傳（兩段式的第一段）—— multipart/form-data
        // 物理意義：Plurk 的附圖是**兩段式**：先把檔案傳上去拿一個圖片 URL，
        //          再把那個 URL 併進 `content`（時間軸上由 Plurk 自己渲染成圖）。
        //          ⇒ 所以「附圖」不是一個 payload 參數，是**兩次請求 ＋ 一段文字**。
        // ⚠ 與現有請求**不同形**：其餘全部是 form-urlencoded，這支是 multipart。
        //   OAuth 1.0a 對 multipart **只簽 `oauth_*` 參數**（檔案內容不進簽章基底）——
        //   把 body 塞進基底會簽出一個看起來正常的簽章，然後回 4xx，
        //   而那個 4xx 跟「端點不存在」「被 WAF 擋」長得一模一樣。
        // 數值影響：**這是對外寫入** —— 會在 Plurk 的 CDN 上留下一張圖（即使沒有建立噗）。
        //          所以 `op=upload` 也要 `confirm=1`。
        // ===========================================================
        public static (int status, string body) UploadImage(string iPath, Dictionary<string, string> iCred)
        {
            string aUrl = ApiBase + UploadEndpoint;
            var aReq = Requester();
            byte[] aBytes = File.ReadAllBytes(iPath);
            var aHeaders = new Dictionary<string, string>
            {
                // ⚠ 第四參 null：multipart **不把 body 參數放進簽章基底**
                { "Authorization", OAuthHeader("POST", aUrl, iCred, null) },
                { "User-Agent", UserAgent },
            };
            // 欄位名 `image` 取自社群慣例（官方頁抓不到）—— 驗證狀態見 Plurk_Maintenance §5
            var aFiles = new List<SCP_HttpFilePart>
            {
                new SCP_HttpFilePart
                {
                    FieldName = "image", FileName = Path.GetFileName(iPath),
                    ContentType = GuessMime(iPath), Data = aBytes,
                },
            };
            if (!aReq.TryPostForm(aUrl, aHeaders, new List<KeyValuePair<string, string>>(), aFiles, UploadTimeoutSec,
                    out string aBody, out int aStatus, out string? aError))
                throw SCP_PlurkFailure.Api($"[Plurk] 圖片上傳連線失敗（沒有拿到 HTTP 回應）：{aError}"
                    + " —— ⚠ 逾時的話圖片**可能已經在 CDN 上**（無主圖片，無害但清不掉）");
            return (aStatus, aBody ?? "");
        }

        // 887 bytes / 1024 == 0 ⇒ 印成「0 KB」會被讀成空檔案。小檔印 bytes。
        public static string FormatSize(long iBytes)
            => iBytes < 1024 ? $"{iBytes} bytes" : $"{iBytes / 1024} KB";

        public static string GuessMime(string iPath)
        {
            switch (Path.GetExtension(iPath).ToLowerInvariant())
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return "application/octet-stream";
            }
        }

        // ===========================================================
        // 區塊職責：憑證讀取（只讀已解密的明文）
        // ⛔ 不做加解密、不碰 passphrase（那是 Secret Manager 的事）；值一律不印。
        // ===========================================================
        public static readonly string[] CredFields =
            { "consumer_key", "consumer_secret", "access_token", "access_token_secret" };

        public static string SecretPath(SCP_PlurkContext iCtx, string iAccount, string iExt)
            => Path.Combine(iCtx.SecretsDir, iAccount + iExt).Replace('\\', '/');

        public static Dictionary<string, string>? LoadCredentials(SCP_PlurkContext iCtx, string iAccount, out string oWhy)
        {
            oWhy = "";
            string aTxt = SecretPath(iCtx, iAccount, ".txt");
            if (!File.Exists(aTxt))
            {
                oWhy = File.Exists(SecretPath(iCtx, iAccount, ".enc"))
                    ? "`.enc` 有但明文沒安裝 ⇒ 到 Secret Manager 做一次解密安裝"
                    : "連 `.enc` 都沒有 ⇒ 先產出憑證（Senate 後台 `senate ui --page plurk`）";
                return null;
            }
            var aJson = SCP_JsonParser.Parse(File.ReadAllText(aTxt, Encoding.UTF8));
            var aOut = new Dictionary<string, string>();
            var aMissing = new List<string>();
            foreach (var aKey in CredFields)
            {
                string v = aJson.GetString(aKey, "").Trim();
                if (v.Length == 0) aMissing.Add(aKey); else aOut[aKey] = v;
            }
            if (aMissing.Count > 0)
            {
                oWhy = "缺欄位 " + string.Join(", ", aMissing)
                    + "（OAuth 1.0a 一定四個值：consumer 認 app、access token 認帳號）";
                return null;
            }
            return aOut;
        }

        public static Dictionary<string, string> RequireCredentials(SCP_PlurkContext iCtx, SCP_PlurkAccountResolution iRes)
        {
            if (string.IsNullOrEmpty(iRes.SecretId))
                throw SCP_PlurkFailure.Blocked("[Plurk] 帳號未設定 ⇒ 不能發文（Senate 後台 `senate ui --page plurk`設共用帳號或個人 override）");
            var aCred = LoadCredentials(iCtx, iRes.SecretId, out string aWhy);
            if (aCred == null) throw SCP_PlurkFailure.Blocked($"[Plurk] 憑證不可用：{aWhy}");
            return aCred;
        }
    }
}
