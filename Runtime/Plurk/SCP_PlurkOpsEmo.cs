// 區塊職責：Plurk 的 op 本體（第三份：表情）—— emoticons / emoadd，以及讀取端的 `[emoN]` 反解析（EmoCtx）。
// 物理意義：`[emoN]` 在文案裡是**不透明的**。lint 只數得出「有幾個」，數不出「那是什麼」，
//          所以既有規則只能是一句「請對照面板逐一確認」—— 把那一格整個丟給人，
//          而 agent 讀別人的噗時看到 `[emo17399]` 也只能當它是一段亂碼。
//          ⇒ 這支把面板搬進 repo：代碼／圖檔 URL **從 API 讀**，描述**由人或 agent 寫**。
// 數值影響：`emoticons` 對 Plurk 純唯讀（`/APP/Emoticons/get`），但**會寫本地表**：
//          `Plurk/emoticons/shared.json` ＋ 人可讀投影 `shared.md`。
//          ⚠ 兩份都是既有、git 追蹤中的檔 ⇒ 位元組相容：json 走 UCL `ToJson()` 形狀
//            （compact、`\u` 轉義），md 走 `\r\n`；列序見 <see cref="SCP_PlurkCultureLikeComparer"/>。
//          刷新是 **merge 不是覆寫** —— API 那邊沒有「描述」這個欄位，
//          覆寫等於每次刷新都把人寫的擦掉，而擦掉之後跟「還沒寫」長得一模一樣。
//          消失的條目**不刪**，標 `missing` 留著 —— 「被下架」與「我沒讀到」不可以同形。
// ⚠ 官方 API 頁（2026-08-24 以顯式 UA 讀回 200）**只有** `/APP/Emoticons/get`：
//   新增自訂表情沒有任何文件化端點。emoadd 是**未驗證的嘗試**，讀數見它自己的回傳檔。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Json;
using static SCP.Core.Plurk.SCP_PlurkJson;

namespace SCP.Core.Plurk
{
    public sealed partial class SCP_PlurkOps
    {
        const string EmoTableRelative = "Plurk/emoticons";

        // ⚠ **一份共用表，不是 per-account 表**（Tim 2026-08-24 拍板）：
        //   表要存的是「這張圖是什麼」——那個事實跟哪個帳號在看它無關。
        //   per-account 分檔會讓同一張圖被每個帳號各自看圖描述一次（那是最貴的一步），
        //   而共用表只要**任何人描述過一次**，之後所有帳號都是純文字查表、不必再抓圖。
        //   ⇒ 鍵用圖檔 URL（跨帳號穩定），別名（`emoN`）只是某帳號怎麼叫它，存在同一列裡。
        string EmoTableJson()
            => Path.Combine(m_Ctx.DataRoot, EmoTableRelative, "shared.json");

        string EmoTableMd()
            => Path.Combine(m_Ctx.DataRoot, EmoTableRelative, "shared.md");

        /// <summary>舊的 per-account 檔（2026-08-24 首版）—— 只讀來搬一次，不再寫。</summary>
        string EmoTableLegacyJson(string iAccount)
            => Path.Combine(m_Ctx.DataRoot, EmoTableRelative, SafeName(iAccount) + ".json");

        /// <summary>
        /// 表情表的一列。
        /// <para>⚠ `Code` 是 **per-account 的別名**（`(bigeyes)` 是全站碼，但自訂表情是 `emo1`/`emo4`
        /// 這種帳號內編號）—— 🩸 2026-08-24 讀 `/APP/Emoticons/get` 才知道：
        /// 別人噗裡的 `[emo17399]` 跟我的 `[emo4]` **不在同一個命名空間**，
        /// 拿我的表去查他的編號會查到一個長得很像答案的錯答案。</para>
        /// <para>⇒ 所以 `Key` 用 **圖檔 URL**（跨帳號唯一穩定），別名只是這個帳號怎麼叫它。</para>
        /// </summary>
        sealed class EmoRow
        {
            public string Code = "";
            public string Id = "";
            public string Url = "";
            public string Tier = "";
            public string Desc = "";
            public string State = "present";
            /// <summary>誰怎麼叫它：`plurk_summit:emo4` / `18166697:emo17399`（讀別人的噗時登記的）。</summary>
            public List<string> Aliases = new List<string>();
            public string FirstSeen = "";
            public string Key => Url.Length > 0 ? "url:" + Url : "code:" + Code;

            public void AddAlias(string iAlias)
            {
                if (iAlias.Length > 0 && !Aliases.Contains(iAlias)) Aliases.Add(iAlias);
            }
        }

        /// <summary>
        /// 讀取端的表情上下文：**一次載入、整趟共用**，並把「這趟新看到的圖」登記起來。
        /// <para>物理意義：描述一張圖要**看圖**（那一步貴且要人／要視覺模型）；
        /// 查一段文字不用。所以流程是「看一次 → 寫進共用表 → 之後永遠查表」。
        /// 而要知道「還有哪些沒看過」，讀取端就得**把沒見過的登記下來** ——
        /// 否則待描述清單只存在於某一次的畫面上，關掉就沒了。</para>
        /// </summary>
        sealed class EmoCtx
        {
            public Dictionary<string, EmoRow> Table = new Dictionary<string, EmoRow>();
            public int NewSeen;
            public int Hit;
            public int Miss;
            public bool Dirty;
        }

        static IComparer<string> EmoOrder => SCP_PlurkCultureLikeComparer.Instance;

        void OpEmoticons()
        {
            var ioR = Report;
            var aCred = RequireCredentials();
            string aBody = Fetch("emoticons", "/APP/Emoticons/get", null, aCred);

            var aRoot = SafeParse(aBody);
            ioR.AppendLine();
            ioR.AppendLine("## emoticons（表情表）");
            if (aRoot == null || !aRoot.IsObject)
            {
                ioR.AppendLine("- ⚠ 回應不是物件 —— **格式跟我預期的不一樣**（不是「沒有表情」）。");
                ioR.AppendLine("- body（前 300 字）: " + Trunc(aBody, 300));
                return;
            }

            // ① 結構讀數：先把「這份回應長什麼樣」印出來，再談內容。
            //    🩸 判準取自這支既有的血證：格式不同與沒有資料必須分得開。
            var aGroups = aRoot.Keys.ToList();
            ioR.AppendLine($"- 頂層分組 **{aGroups.Count}** 組: "
                + string.Join(" / ", aGroups.Select(k =>
                {
                    var aG = aRoot[k];
                    return $"`{k}`"
                        + (aG != null && aG.IsArray ? $"[{aG.Count}]"
                            : aG != null && aG.IsObject ? $"{{{aG.Count}}}" : "(純量)");
                })));

            var aRows = new List<EmoRow>();
            foreach (string aGroup in aGroups)
            {
                var aNode = aRoot[aGroup];
                if (aNode == null) continue;
                if (aNode.IsObject)
                {
                    // karma / recruited：{ "0": [[code,url],...], "25": [...] }
                    foreach (string aTier in aNode.Keys.ToList())
                        CollectEmoList(aNode[aTier], aGroup + "/" + aTier, aRows);
                }
                else if (aNode.IsArray)
                {
                    CollectEmoList(aNode, aGroup, aRows);   // custom 走這條
                }
            }

            ioR.AppendLine($"- 讀到 **{aRows.Count}** 個表情"
                + $"（其中有數字編號的 **{aRows.Count(r => r.Id.Length > 0)}** 個）");
            foreach (var aTierGroup in aRows.GroupBy(r => r.Tier).OrderBy(g => g.Key, EmoOrder))
                ioR.AppendLine($"    · `{aTierGroup.Key}`　{aTierGroup.Count()} 個"
                    + $"　例: {string.Join(" ", aTierGroup.Take(4).Select(r => r.Code))}");

            // ② 反解析能不能做，用**讀數**回答，不用推論
            var aOwn = aRows.Where(r => EmoAliasRe.IsMatch(r.Code)).ToList();
            ioR.AppendLine();
            ioR.AppendLine("### ▶ `[emoN]` 反解析可行性（用讀數回答，不是用推論）");
            ioR.AppendLine($"- 本帳號自己的 `[emoN]` 別名: **{aOwn.Count}** 個"
                + (aOwn.Count == 0 ? "" : "（" + string.Join(" ", aOwn.Select(r => "[" + r.Code + "]")) + "）")
                + " ⇒ **我自己文案裡的 `[emoN]` 這張表查得到**。");
            ioR.AppendLine("- ⛔ 而**別人噗裡的 `[emoN]` 這張表查不到**："
                + "`emoN` 是 per-account 別名，他的 `[emo17399]` 與我的 `[emo4]` 不同命名空間。"
                + "拿我的表去查他的編號，會查到一個長得很像答案的**錯**答案。");
            ioR.AppendLine("- ✅ 跨帳號真正對得上的鍵是**圖檔 URL**："
                + "`getPlurks` 同一筆裡的 `content`（HTML）帶著每個表情的 `<img src>`，"
                + "跟 `content_raw` 的 `[emoN]` **同序**⇒ 讀取端按序配對就拿得到 URL，"
                + "再用 URL 查描述。timeline／responses／get 已接上這條（配不上時標 `⟨?配不上⟩`，不猜）。");

            // ③ merge 進共用表（描述**只增不減**）
            var aOld = LoadEmoTable(m_Res.SecretId ?? "_");
            string aAccountTag = m_Res.SecretId ?? "_";
            foreach (var aRow in aRows) aRow.AddAlias(aAccountTag + ":" + aRow.Code);
            int aKept = 0, aNew = 0;
            // ⚠ `ToDictionary`（同 key 兩列會丟例外）是刻意的：同 URL 兩列 ⇒ 失敗，⛔ 不靜默留一列
            var aByKey = aRows.ToDictionary(r => r.Key, r => r);
            foreach (var aRow in aRows)
            {
                if (aOld.TryGetValue(aRow.Key, out EmoRow? aPrev))
                {
                    if (aPrev.Desc.Length > 0)
                    {
                        aRow.Desc = aPrev.Desc;   // 人寫的描述活下來
                        aKept++;
                    }
                    foreach (string aAlias in aPrev.Aliases) aRow.AddAlias(aAlias);
                    if (aPrev.FirstSeen.Length > 0) aRow.FirstSeen = aPrev.FirstSeen;
                }
                else aNew++;
            }
            // 舊表裡這次沒讀到的：**不刪**，留著。
            // ⚠ 但只有「上次是 API 給的」那些才標 missing ——
            //   `state=seen` 是讀別人的噗登記進來的圖，它**本來就不會**出現在我這個帳號的 API 表裡。
            //   把它標成 missing 等於說「它下架了」，而那是假的。
            int aMissing = 0;
            foreach (var aPrev in aOld.Values)
            {
                if (aByKey.ContainsKey(aPrev.Key)) continue;
                if (aPrev.State != "seen") { aPrev.State = "missing"; aMissing++; }
                aRows.Add(aPrev);
            }

            // ④ 手動描述（`--arg emo_desc=17399=紅心眼,590=攤手`）
            string aDescArg = Arg("emo_desc").Trim();
            int aWrote = 0;
            var aUnmatched = new List<string>();
            if (aDescArg.Length > 0)
            {
                foreach (string aPair in aDescArg.Split(','))
                {
                    int aEq = aPair.IndexOf('=');
                    if (aEq <= 0) continue;
                    string aKey = aPair.Substring(0, aEq).Trim();
                    string aDesc = aPair.Substring(aEq + 1).Trim();
                    if (aKey.Length == 0 || aDesc.Length == 0) continue;
                    // 三種鍵都收：別名（emo4）／全站碼（(bigeyes)）／圖檔 URL 片段
                    // —— 因為跨帳號唯一穩定的是 URL，而人手上最常有的是別名
                    var aHit = aRows.FirstOrDefault(r => r.Id == aKey || r.Code == aKey
                        || (aKey.Length >= 6 && r.Url.Contains(aKey)));
                    if (aHit == null)
                    {
                        // 表裡沒有這個編號 ⇒ **新增一列**（那正是好友噗裡撈到的編號的家），
                        // 並且標明它不是 API 給的
                        aHit = new EmoRow { Id = aKey, Code = "[emo" + aKey + "]", Tier = "manual", State = "manual" };
                        aRows.Add(aHit);
                        aUnmatched.Add(aKey);
                    }
                    aHit.Desc = aDesc;
                    aWrote++;
                }
            }

            SaveEmoTable(aRows);
            ioR.AppendLine($"- merge: 新增 **{aNew}**／保留既有描述 **{aKept}**"
                + $"／這次沒讀到但留著 **{aMissing}**（標 `missing`，不刪）"
                + (aWrote > 0 ? $"／本次寫入描述 **{aWrote}**" : ""));
            if (aUnmatched.Count > 0)
                ioR.AppendLine($"    · ⚠ 其中 {aUnmatched.Count} 筆編號**不在 API 表裡**（標 `manual`）: "
                    + string.Join(" ", aUnmatched.Select(s => "[emo" + s + "]")));

            int aDescribed = aRows.Count(r => r.Desc.Length > 0);
            ioR.AppendLine($"- 描述覆蓋率: **{aDescribed}/{aRows.Count}**"
                + "　（描述是人寫的，API 沒有這個欄位 ⇒ 覆蓋率只會靠人推進）");
            AddValue("emo_total", aRows.Count.ToString(CultureInfo.InvariantCulture));
            AddValue("emo_described", aDescribed.ToString(CultureInfo.InvariantCulture));

            ioR.AppendLine();
            ioR.AppendLine("### ▶ 下一步");
            ioR.AppendLine("```bash");
            ioR.AppendLine("--arg op=emoticons --arg emo_desc=17399=紅心眼,590=攤手   # 補描述（merge，不覆寫）");
            ioR.AppendLine("--arg op=emoadd --arg url=<圖檔網址> --arg alias=<代碼> --arg confirm=1  # 試新增（未驗證）");
            ioR.AppendLine("```");
        }

        /// <summary>把 `[[code,url],...]` 或 `[{...},...]` 收成列。認不得的元素**跳過但不假裝沒有**。</summary>
        static void CollectEmoList(SCP_PlurkNode? iNode, string iTier, List<EmoRow> ioRows)
        {
            if (iNode == null || !iNode.IsArray) return;
            for (int i = 0; i < iNode.Count; i++)
            {
                var aItem = iNode[i];
                if (aItem == null) continue;
                var aRow = new EmoRow { Tier = iTier };
                if (aItem.IsArray && aItem.Count >= 2)
                {
                    aRow.Code = UnescapeJson(StripQuote(aItem[0]?.ToJson()));
                    aRow.Url = UnescapeJson(StripQuote(aItem[1]?.ToJson()));
                }
                else if (aItem.IsObject)
                {
                    aRow.Code = UnescapeJson(JsonScalar(aItem, "alias"));
                    if (aRow.Code.Length == 0) aRow.Code = UnescapeJson(JsonScalar(aItem, "name"));
                    aRow.Url = UnescapeJson(JsonScalar(aItem, "url"));
                    aRow.Id = JsonScalar(aItem, "id");
                }
                else continue;
                // 自訂表情的別名本身就是 `emoN` ⇒ 那個 N 就是文案裡 `[emoN]` 的編號。
                // 🩸 首版把 Id 留空，於是表格的「編號」欄印 `—`，
                //    看起來像「這個表情沒有編號可用」—— 而它其實是**唯一**能打進文案的那格。
                if (aRow.Id.Length == 0 && EmoAliasRe.IsMatch(aRow.Code))
                    aRow.Id = aRow.Code.Substring(3);
                if (aRow.Id.Length == 0) aRow.Id = IdFromUrl(aRow.Url);
                if (aRow.Code.Length == 0 && aRow.Url.Length == 0) continue;
                ioRows.Add(aRow);
            }
        }

        /// <summary>URL 檔名**純數字**時當它是編號；其餘回空 —— 不從雜湊檔名硬擠一個編號出來。</summary>
        static string IdFromUrl(string? iUrl)
        {
            if (string.IsNullOrEmpty(iUrl)) return "";
            int aSlash = iUrl!.LastIndexOf('/');
            string aName = aSlash >= 0 ? iUrl.Substring(aSlash + 1) : iUrl;
            int aDot = aName.IndexOf('.');
            if (aDot > 0) aName = aName.Substring(0, aDot);
            return aName.Length > 0 && aName.All(char.IsDigit) ? aName : "";
        }

        static string StripQuote(string? iRaw)
        {
            string aText = (iRaw ?? "").Trim();
            if (aText.Length >= 2 && aText[0] == '"' && aText[aText.Length - 1] == '"')
                aText = aText.Substring(1, aText.Length - 2);
            return aText == "null" ? "" : aText;
        }

        Dictionary<string, EmoRow> LoadEmoTable(string? iLegacyAccount = null)
        {
            var aMap = new Dictionary<string, EmoRow>();
            string aFile = EmoTableJson();
            // 共用表還不存在時，把舊的 per-account 檔搬進來一次（含它累積的描述）
            if (!File.Exists(aFile) && !string.IsNullOrEmpty(iLegacyAccount)
                && File.Exists(EmoTableLegacyJson(iLegacyAccount!)))
                aFile = EmoTableLegacyJson(iLegacyAccount!);
            if (!File.Exists(aFile)) return aMap;
            var aRoot = SafeParse(File.ReadAllText(aFile, Encoding.UTF8));
            var aArr = aRoot != null && aRoot.Contains("entries") ? aRoot["entries"] : null;
            if (aArr == null || !aArr.IsArray) return aMap;
            for (int i = 0; i < aArr.Count; i++)
            {
                var aIt = aArr[i];
                var aRow = new EmoRow
                {
                    Code = UnescapeJson(JsonScalar(aIt, "code")),
                    Id = JsonScalar(aIt, "id"),
                    Url = UnescapeJson(JsonScalar(aIt, "url")),
                    Tier = JsonScalar(aIt, "tier"),
                    Desc = UnescapeJson(JsonScalar(aIt, "desc")),
                    State = JsonScalar(aIt, "state"),
                    FirstSeen = JsonScalar(aIt, "first_seen"),
                };
                var aAl = aIt != null && aIt.Contains("aliases") ? aIt["aliases"] : null;
                if (aAl != null && aAl.IsArray)
                {
                    for (int j = 0; j < aAl.Count; j++)
                        aRow.AddAlias(UnescapeJson(StripQuote(aAl[j]?.ToJson())));
                }
                if (aRow.Code.Length == 0 && aRow.Id.Length == 0 && aRow.Url.Length == 0) continue;
                aMap[aRow.Key] = aRow;
            }
            return aMap;
        }

        void SaveEmoTable(List<EmoRow> iRows)
        {
            var ioR = Report;
            string aFile = EmoTableJson();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aFile) ?? ".");
                var aRoot = new SCP_UclLegacyObject();
                aRoot.Set("refreshed_at", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
                var aArr = new List<object?>();
                foreach (var aRow in iRows.OrderBy(r => r.Tier, EmoOrder).ThenBy(r => r.Code, EmoOrder))
                {
                    var aItem = new SCP_UclLegacyObject();
                    aItem.Set("code", aRow.Code);
                    aItem.Set("id", aRow.Id);
                    aItem.Set("url", aRow.Url);
                    aItem.Set("tier", aRow.Tier);
                    aItem.Set("desc", aRow.Desc);
                    aItem.Set("state", aRow.State);
                    aItem.Set("first_seen", aRow.FirstSeen);
                    var aAl = new List<object?>();
                    foreach (string aAlias in aRow.Aliases) aAl.Add(aAlias);
                    aItem.Set("aliases", aAl);
                    aArr.Add(aItem);
                }
                aRoot.Set("entries", aArr);
                File.WriteAllText(aFile, SCP_UclLegacyJson.ToJson(aRoot), new UTF8Encoding(false));
                ioR.AppendLine($"- 📋 共用表: `{aFile}`（**merge 寫入**，描述不被刷新擦掉）");

                // ⚠ `\r\n`：這份是既有 CRLF、git 追蹤中的檔 —— 換行換掉整份 diff 就翻過來
                var aMd = new SCP_PlurkText("\r\n");
                aMd.AppendLine("# Plurk 表情共用表（描述一次，之後純文字查表）");
                aMd.AppendLine();
                aMd.AppendLine("> 機械投影：`" + Path.GetFileName(aFile) + "` 是真相源"
                    + "（改描述走 `--arg op=emoticons --arg emo_desc=<別名或URL片段>=<描述>`；本檔每次寫入重生成）。");
                aMd.AppendLine("> **鍵是圖檔 URL 不是編號**：`[emoN]` 是 per-account 別名，"
                    + "同一個編號在不同帳號是不同張圖 ⇒ 別名記在 `aliases` 欄，查表查 URL。");
                aMd.AppendLine("> `state=seen` ＝ 讀別人的噗時撞見的圖，**還沒有人看過它** ⇒ 那就是待描述清單。");
                aMd.AppendLine();
                int aDesc = iRows.Count(r => r.Desc.Length > 0);
                aMd.AppendLine($"- 共 **{iRows.Count}** 張／已描述 **{aDesc}**"
                    + $"／待描述 **{iRows.Count - aDesc}**");
                aMd.AppendLine();
                aMd.AppendLine("| 別名 | 全站碼 | 分層 | 描述 | 狀態 | 圖檔 |");
                aMd.AppendLine("|---|---|---|---|---|---|");
                foreach (var aRow in iRows.OrderBy(r => r.Tier, EmoOrder).ThenBy(r => r.Code, EmoOrder))
                    aMd.AppendLine($"| {(aRow.Aliases.Count == 0 ? "—" : "`" + string.Join("` `", aRow.Aliases) + "`")}"
                        + $" | `{(aRow.Code.Length == 0 ? "—" : aRow.Code)}` | {aRow.Tier}"
                        + $" | {(aRow.Desc.Length == 0 ? "*(未描述)*" : aRow.Desc)} | {aRow.State}"
                        + $" | [{EmoShort(aRow.Url)}]({aRow.Url}) |");
                File.WriteAllText(EmoTableMd(), aMd.ToString(), new UTF8Encoding(false));
                ioR.AppendLine($"- 📋 人可讀投影: `{EmoTableMd()}`");
            }
            catch (Exception ex)
            {
                // 寫不進去不影響這次的讀數，但要說 —— 不然下次讀不到會變成沒人解釋得了的謎
                ioR.AppendLine($"- ⚠ 本地表寫入失敗（不影響本次讀數）：{ex.Message}");
            }
        }

        static readonly Regex EmoAliasRe = new Regex(@"^emo\d+$", RegexOptions.Compiled);

        static readonly Regex EmoTokenRe = new Regex(@"\[emo\d+\]", RegexOptions.Compiled);

        // 表情圖只從這兩個 host 來（`s.plurk.com/emoticons/...` 是全站表情，
        // `emos.plurk.com/...` 是自訂表情）。⚠ 一定要濾 host：
        // 同一段 HTML 裡還有**使用者上傳的圖片**（images.plurk.com），
        // 把它們算進來會讓配對整排錯開一格 —— 而錯開一格的結果每一個都看起來像答案。
        static readonly Regex EmoImgRe = new Regex(
            @"<img[^>]+src=[""'](?<u>https?://(?:emos\.plurk\.com|s\.plurk\.com/emoticons)/[^""']+)[""']",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 讀取端的反解析：拿同一筆噗的 `content`（HTML）與 `content_raw` **按序配對**，
        /// 把 `[emoN]` 標成 `[emoN]⟨描述⟩`（或 `⟨🖼 短碼⟩`／`⟨?配不上⟩`）。
        /// <para>為什麼不查本地表的別名：`emoN` 是 per-account 的，
        /// 別人的 `[emo17399]` 用我的表查會查到**錯**的那一個 —— 而錯的那個看起來一樣像答案。
        /// URL 才是跨帳號穩定的鍵。</para>
        /// <para>數量對不上時**每一個都標 `⟨?配不上⟩`**，不做「前 N 個先配」：
        /// 錯開一格的結果比沒有結果更貴。</para>
        /// </summary>
        static string EmoAnnotatePaired(string iRaw, string? iHtml, EmoCtx iCtx, string? iOwnerId)
        {
            if (string.IsNullOrEmpty(iRaw) || !EmoTokenRe.IsMatch(iRaw)) return iRaw;
            var aTokens = EmoTokenRe.Matches(iRaw);
            var aUrls = EmoImgRe.Matches(iHtml ?? "").Cast<Match>()
                .Select(m => m.Groups["u"].Value).ToList();
            bool aAligned = aUrls.Count == aTokens.Count;
            int aIdx = 0;
            return EmoTokenRe.Replace(iRaw, m =>
            {
                string aNote;
                if (!aAligned) aNote = "?配不上";
                else
                {
                    string aUrl = aUrls[aIdx];
                    string aKey = "url:" + aUrl;
                    if (!iCtx.Table.TryGetValue(aKey, out EmoRow? aRow))
                    {
                        // 沒見過這張圖 ⇒ **登記**（描述留空）。
                        // 不登記的話「還有哪些沒看過」只存在於這一次的畫面上，關掉就沒了。
                        aRow = new EmoRow
                        {
                            Url = aUrl,
                            Tier = "seen",
                            State = "seen",
                            FirstSeen = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                        };
                        iCtx.Table[aKey] = aRow;
                        iCtx.NewSeen++;
                        iCtx.Dirty = true;
                    }
                    string aAlias = (string.IsNullOrEmpty(iOwnerId) ? "?" : iOwnerId)
                        + ":" + m.Value.Trim('[', ']');
                    if (!aRow.Aliases.Contains(aAlias)) { aRow.AddAlias(aAlias); iCtx.Dirty = true; }

                    if (aRow.Desc.Length > 0) { aNote = aRow.Desc; iCtx.Hit++; }
                    else { aNote = "未描述:" + EmoShort(aUrl); iCtx.Miss++; }
                }
                aIdx++;
                return m.Value + "⟨" + aNote + "⟩";
            });
        }

        /// <summary>目前 `custom` 有幾個。讀不到回 **-1**（不回 0 —— 「讀不到」與「沒有」不可以同形）。</summary>
        static int EmoCustomCount(Dictionary<string, string> iCred)
        {
            var (aSt, aBody) = Call("/APP/Emoticons/get", iCred, null);
            if (aSt != 200) return -1;
            var aRoot = SafeParse(aBody);
            if (aRoot == null || !aRoot.IsObject || !aRoot.Contains("custom")) return -1;
            var aCustom = aRoot["custom"];
            return aCustom != null && aCustom.IsArray ? aCustom.Count : -1;
        }

        /// <summary>讀取端起手：載共用表（必要時從舊 per-account 檔搬一次）。</summary>
        EmoCtx EmoBegin() => new EmoCtx { Table = LoadEmoTable(m_Res.SecretId ?? "_") };

        /// <summary>
        /// 讀取端收尾：**有新登記就落盤**，並把三個數印出來（命中／待描述／新登記）。
        /// <para>⚠ 這讓唯讀 op 產生一個本地寫入 —— 所以它一定要印出來。
        /// 靜默寫檔會讓「唯讀」這個標籤比事實大。</para>
        /// </summary>
        void EmoEnd(EmoCtx? iCtx)
        {
            var ioR = Report;
            if (iCtx == null) return;
            if (iCtx.Hit + iCtx.Miss + iCtx.NewSeen == 0) return;
            ioR.AppendLine();
            // ⚠ 每個數字都要自己說出**它數的是哪一群、單位是什麼** —— 見本函式尾端那段血證。
            //   Hit / Miss 數的是「這一趟訊息裡 `[emoN]` 的**出現次數**」（同一張圖出現兩次算兩次），
            //   ⛔ 不是「表上有幾列」；NewSeen 才是張數。
            ioR.AppendLine($"### 🙂 表情查表（**本趟訊息內**，單位＝出現次數）："
                + $"命中描述 **{iCtx.Hit}** 次／查表無描述 **{iCtx.Miss}** 次"
                + $"／本趟新登記 **{iCtx.NewSeen}** 張圖");
            if (iCtx.Dirty)
            {
                SaveEmoTable(iCtx.Table.Values.ToList());
                ioR.AppendLine("- ⚠ 本 op 對 Plurk 是唯讀，但**寫了本地共用表**（新圖登記／別名補齊）——"
                    + "這一行就是那個寫入的讀數。");
            }
            // ⭐ 這一段數的是**整張共用表**，不是本趟訊息 —— 兩個不同的族群，所以標題與這裡各自帶定語。
            // 🩸 2026-09-10：改之前兩邊都只寫「待描述」⇒ 同一份輸出印「待描述 **0**」而底下列了 8 張。
            //   實測真值：`state=seen` 且無描述 **74 列**（全表 253 列）⇒ 標題少報 74、清單少報 66，
            //   而清單**靜默截在 8** 連一句「還有多少」都沒有。
            //   ⇒ 「這一趟沒有待描述」與「表上沒有待描述」在那份輸出上同形，而處置完全不同
            //   （前者＝繼續做事，後者＝去看圖描述）。
            //   📌 對應 Glossary《作用域錯位》Review #0（@summit 2026-09-10）：
            //      **「這個東西的名字，宣告了它的範圍嗎？」** —— 「待描述」沒有宣告它數的是哪一群。
            const int k_TodoShown = 8;
            var aTodoAll = iCtx.Table.Values.Where(r => r.Desc.Length == 0 && r.State == "seen")
                .OrderBy(r => r.FirstSeen, EmoOrder).ToList();
            int aNoDescAnyState = iCtx.Table.Values.Count(r => r.Desc.Length == 0);
            if (aTodoAll.Count > 0)
            {
                ioR.AppendLine($"- **整張共用表**待描述：**{aTodoAll.Count}** 列"
                    + $"（`state=seen` 且無描述；全表 {iCtx.Table.Count} 列、無描述的共 {aNoDescAnyState} 列）"
                    + $"　⚠ 以下只列最舊 {Math.Min(k_TodoShown, aTodoAll.Count)} 列"
                    + (aTodoAll.Count > k_TodoShown ? $"，**其餘 {aTodoAll.Count - k_TodoShown} 列沒有印出來**" : "")
                    + "：");
                foreach (var aRow in aTodoAll.Take(k_TodoShown))
                    ioR.AppendLine($"    · {aRow.Url}"
                        + (aRow.Aliases.Count == 0 ? "" : "　（" + string.Join(" ", aRow.Aliases) + "）"));
                ioR.AppendLine("    ⇒ 描述寫回: `--arg op=emoticons --arg emo_desc=<URL片段>=<描述>`");
            }
        }

        /// <summary>圖檔 URL 的短碼（檔名前 8 碼）—— 給人眼比對「這兩個 `[emoN]` 是不是同一張圖」。</summary>
        static string EmoShort(string? iUrl)
        {
            if (string.IsNullOrEmpty(iUrl)) return "?";
            int aSlash = iUrl!.LastIndexOf('/');
            string aName = aSlash >= 0 ? iUrl.Substring(aSlash + 1) : iUrl;
            return aName.Length <= 8 ? aName : aName.Substring(0, 8);
        }

        // ===========================================================
        // 區塊職責：試著新增一個自訂表情。
        // ⚠ 這支是**未驗證的嘗試**，不是已知可用的功能：
        //   官方 API 頁（2026-08-24，200）的 Emoticons 章節**只有** `get`，
        //   一個新增用的端點都沒有。所以這裡送的路徑取自社群慣例，
        //   而它的三種失敗（端點不存在／簽章錯／WAF 擋）**全都是 4xx，長得一樣**。
        // ⇒ 判準：不論成功或失敗，把 http 碼與 body 原樣印出來，**不翻譯成「成功／不支援」**。
        // 數值影響：若真的成立，這會在帳號上新增一個自訂表情（對外、可見）⇒ 要 `confirm=1`。
        // ===========================================================
        void OpEmoAdd()
        {
            var ioR = Report;
            string aUrl = Arg("url").Trim();
            string aAlias = Arg("alias").Trim();
            if (aUrl.Length == 0 || aAlias.Length == 0)
                throw SCP_PlurkFailure.Blocked("[Plurk] op=emoadd 需要 --arg url=<圖檔網址> 與 --arg alias=<表情代碼>");
            var aCred = RequireCredentials();

            ioR.AppendLine();
            ioR.AppendLine("## emoadd（**未驗證的嘗試** —— 官方 API 頁沒有這個端點）");
            ioR.AppendLine($"- alias: `{aAlias}`　url: `{aUrl}`");
            ioR.AppendLine("- ⚠ 官方 `/APP/API` 的 Emoticons 章節只有 `get`（2026-08-24 顯式 UA 讀回 200 確認）。");
            ioR.AppendLine("  ⇒ 下面每一個候選端點的 4xx **不能**當成「Plurk 不支援」——"
                + "端點不存在／簽章錯／WAF 擋在這裡長得一樣。");

            if (Arg("confirm") != "1")
            {
                ioR.AppendLine("- 🛑 dry-run（沒帶 `confirm=1`）⇒ 一個請求都沒送。");
                return;
            }

            // ① 動手**之前**先數一次 —— before/after 才是「這一次加成功了」的證據。
            //    🩸 2026-08-24 首版只驗「送出的 alias 有沒有出現在回讀裡」，而 Plurk **不吃我給的
            //    alias**（它自己回 `{"success_text":"ok","keyword":"emo7"}` 自動編號）⇒
            //    那一行印「否 ← 沒生效」，而事實是 custom 從 6 變成 7，**加成功了**。
            //    ⇒ 判準：驗收要問「這個動作有沒有發生」，不是「我猜的那個副作用有沒有出現」。
            int aBefore = EmoCustomCount(aCred);
            ioR.AppendLine($"- 動手前 `custom` 數量: **{(aBefore < 0 ? "讀不到" : aBefore.ToString(CultureInfo.InvariantCulture))}**");

            // 兩個候選端點都試：先 addFromURL（社群慣例），再 add。
            // 兩個都印讀數 —— 只試一個然後說「不支援」，那是拿一條路徑的結果替整個世界作答。
            string aKeyword = "";
            string[] aCandidates = { "/APP/Emoticons/addFromURL", "/APP/Emoticons/add" };
            foreach (string aEndpoint in aCandidates)
            {
                var aParams = new Dictionary<string, string> { { "url", aUrl }, { "alias", aAlias } };
                var (aSt, aBody) = Call(aEndpoint, aCred, aParams);
                ioR.AppendLine($"- `POST {aEndpoint}`　http: **{aSt}**　body（前 200 字）: {Trunc(aBody, 200)}");
                if (aSt == 200 && aKeyword.Length == 0)
                    aKeyword = PickJsonValue(aBody, "keyword") ?? "";
            }
            if (aKeyword.Length > 0)
                ioR.AppendLine($"- ⚠ Plurk **自己命名**成 `{aKeyword}`（我送的 alias `{aAlias}` 被忽略）"
                    + " ⇒ 文案裡要打的是 `[" + aKeyword + "]`，不是我取的那個名字。");

            ioR.AppendLine("- ▶ 回讀（`/APP/Emoticons/get` 的 `custom` 分組才是憑據，200 不是）:");
            var (aGetSt, aGetBody) = Call("/APP/Emoticons/get", aCred, null);
            var aRoot = SafeParse(aGetBody);
            bool aHasCustom = aRoot != null && aRoot.IsObject && aRoot.Contains("custom");
            var aCustom = aHasCustom ? aRoot!["custom"] : null;
            int aAfter = aCustom != null && aCustom.IsArray ? aCustom.Count : -1;
            ioR.AppendLine($"    · http {aGetSt}　`custom` 分組: "
                + (aHasCustom ? $"有，{(aAfter < 0 ? "不是陣列（格式與預期不同）" : aAfter + " 個")}"
                    : "**沒有這個分組**"));
            ioR.AppendLine($"    · 數量 **{(aBefore < 0 ? "?" : aBefore.ToString(CultureInfo.InvariantCulture))} → "
                + $"{(aAfter < 0 ? "?" : aAfter.ToString(CultureInfo.InvariantCulture))}**　"
                + (aBefore >= 0 && aAfter == aBefore + 1 ? "✅ 加了一個（這是直接證據）"
                    : aBefore >= 0 && aAfter == aBefore ? "⛔ **沒變 ⇒ 沒生效**"
                    : "⚠ 兩個讀數之一沒拿到 ⇒ 這一格沒有證據，不當成成功"));
            if (aKeyword.Length > 0)
                ioR.AppendLine($"    · 回傳的 `{aKeyword}` 出現在 custom 清單裡: "
                    + (aHasCustom && (aGetBody ?? "").Contains("\"" + aKeyword + "\"") ? "**是**" : "**否**"));
            ioR.AppendLine("- ⚠ API **沒有刪除端點** ⇒ 加錯了只能上網頁 UI 收拾。");
            ioR.AppendLine("- ▶ 下一步：跑 `--arg op=emoticons` 把它併進共用表（順手補描述）。");
            if (aKeyword.Length > 0) AddValue("keyword", aKeyword);
        }
    }
}
