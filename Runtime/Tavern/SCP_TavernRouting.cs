// 區塊職責：**酒館路由判準**的讀寫與解析 —— 哪個 category 走哪個 group、那個 group 計不計酬。
// 物理意義：TASK-0296（TASK-0295 ①的前置，Tim 2026-09-25 拍板「真相源搬到資料根」）。
//           此前判準住在 Unity 專案的 `UCL_TavernCategoryRoutingAsset`（`.BuiltinModules` 底下一 group 一檔），
//           而發薪判斷要搬進 Senate ⇒ Senate 若去讀那個路徑，只是把 Unity 依賴換個地方留著。
//           ⇒ 真相源改成 `<資料根>/ChatTavern/tavern_routing.json`，Senate 與 Editor 都讀這一份。
// 數值影響：
//   · group 放在**陣列**裡，陣列順序＝比對順序（第一個命中的 group 勝出；都沒命中 ⇒ 第一個 enabled 的 default）。
//     舊版的順序來自 asset ID 列舉，那是隱性的 —— 這裡把它寫成資料。
//   · ⛔ **不放 webhook URL**：只放路由語意與 env var／file 的**名字**。URL 是秘密，
//     仍留在 Unity asset 給 Discord 鏡像用（`import_unity` 會把它們剝掉並回報剝了幾條）。
// ⚠ 讀取是**嚴格**的：檔不存在／壞檔／布林欄位不是布林 ⇒ Error 有值，⛔ 不靜默回「沒有 group」。
//   理由：發薪判斷讀到空清單的樣子是「這則不計酬」，那是一個合理的答案 —— 而它會讓所有人安靜地領不到錢。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Tavern
{
    /// <summary>一個路由 group。欄位語意對齊 Unity 端 <c>UCL_TavernCategoryRoutingAsset</c>（⛔ 不含 webhook URL）。</summary>
    public sealed class SCP_TavernRouteGroup
    {
        public string Id = "";
        public List<string> Categories = new List<string>();
        public bool Enabled = true;
        /// <summary>沒命中任何 category 的訊息落到這裡（取第一個 enabled 的）。</summary>
        public bool IsDefault;
        /// <summary>落在這個 group 的訊息要不要付發文底薪。</summary>
        public bool IsPaidPost;
        /// <summary>廣播用：命中時只送這個 group（發薪判斷不讀它）。</summary>
        public bool Exclusive;
        /// <summary>webhook URL 的環境變數**名字**（不是值）。</summary>
        public string WebhookEnvVar = "";
        /// <summary>webhook URL 的檔名（不是內容）。</summary>
        public string WebhookFile = "";
        public string Description = "";

        public bool Matches(string iNormalizedCategory)
        {
            if (iNormalizedCategory.Length == 0) return false;
            foreach (string c in Categories)
                if (SCP_TavernRouting.Normalize(c) == iNormalizedCategory) return true;
            return false;
        }

        public SCP_TavernRouteGroup Clone()
        {
            var g = (SCP_TavernRouteGroup)MemberwiseClone();
            g.Categories = new List<string>(Categories);
            return g;
        }
    }

    /// <summary>一次讀取的結果 —— 讀不到的原因與「讀到了但設定可疑」分開放。</summary>
    public sealed class SCP_TavernRoutingRead
    {
        public string Path = "";
        public List<SCP_TavernRouteGroup> Groups = new List<SCP_TavernRouteGroup>();
        /// <summary>讀不了（檔不存在／壞檔／欄位型別不對）。有值時 <see cref="Groups"/> 一律是空的。</summary>
        public string? Error;
        /// <summary>檔案不存在（Error 也會有值）—— 跟「檔在但壞了」分開，處置不同（前者要匯入）。</summary>
        public bool Missing;
        /// <summary>讀得到、但設定可疑（沒有 default／多個 default…）。不擋讀取，顯示端要印出來。</summary>
        public List<string> Warnings = new List<string>();
        public bool Ok => Error == null;
    }

    public static class SCP_TavernRouting
    {
        public const int SchemaVersion = 1;

        public static string PathOf(string iDataRoot) => SCP_DataPaths.TavernRouting(new SCP_DataRoot(iDataRoot));

        public static string Normalize(string? iCategory) => (iCategory ?? "").Trim().ToLowerInvariant();

        // ── 讀 ──────────────────────────────────────────────────────────

        public static SCP_TavernRoutingRead Read(string iDataRoot)
        {
            var r = new SCP_TavernRoutingRead { Path = PathOf(iDataRoot) };
            if (!TryReadText(r.Path, out string aText, out bool aMissing, out string aReadErr))
            {
                r.Missing = aMissing;
                r.Error = aMissing
                    ? "路由判準檔不存在：" + r.Path + " —— ⛔ 這不是「沒有任何頻道計酬」，是**沒有判準**。"
                      + "第一次要從 Unity asset 匯入：`senate cmd tavern-routing --arg data_root=<資料根> --arg op=import_unity --arg unity_dir=<asset 目錄> --arg confirm=1`"
                    : "路由判準檔讀不了（重試用完）：" + aReadErr;
                return r;
            }
            try
            {
                SCP_JsonData aRoot = SCP_JsonData.Parse(aText);
                SCP_JsonData aGroups = aRoot["groups"];
                if (!aGroups.IsArray) throw new FormatException("缺 `groups` 陣列");
                int i = 0;
                foreach (SCP_JsonData g in aGroups)
                {
                    string aId = g.GetString("id", "").Trim();
                    if (aId.Length == 0) throw new FormatException($"第 {i} 個 group 沒有 id");
                    var aGroup = new SCP_TavernRouteGroup
                    {
                        Id = aId,
                        Enabled = g.GetBool("enabled", true),
                        IsDefault = g.GetBool("is_default", false),
                        IsPaidPost = g.GetBool("is_paid_post", false),
                        Exclusive = g.GetBool("exclusive", false),
                        WebhookEnvVar = g.GetString("webhook_env_var", ""),
                        WebhookFile = g.GetString("webhook_file", ""),
                        Description = g.GetString("description", ""),
                    };
                    foreach (SCP_JsonData c in g["categories"]) aGroup.Categories.Add(c.AsString());
                    r.Groups.Add(aGroup);
                    i++;
                }
                string? aInvalid = Validate(r.Groups);
                if (aInvalid != null) throw new FormatException(aInvalid);
            }
            catch (Exception e)
            {
                r.Groups.Clear();
                r.Error = "路由判準檔解析失敗（" + r.Path + "）：" + e.GetType().Name + ": " + e.Message;
                return r;
            }
            r.Warnings.AddRange(Diagnose(r.Groups));
            return r;
        }

        /// <summary>檢查會讓解析變成**另一個答案**的錯（空 id／重複 id）。null ＝ 沒問題。</summary>
        public static string? Validate(IReadOnlyList<SCP_TavernRouteGroup> iGroups)
        {
            var aSeen = new HashSet<string>(StringComparer.Ordinal);
            foreach (SCP_TavernRouteGroup g in iGroups)
            {
                if (string.IsNullOrWhiteSpace(g.Id)) return "有 group 沒有 id";
                if (!aSeen.Add(g.Id)) return "group id 重複：`" + g.Id + "`";
            }
            return null;
        }

        /// <summary>讀得到但可疑的設定 —— 不擋，但要讓看的人知道那個後果。</summary>
        public static List<string> Diagnose(IReadOnlyList<SCP_TavernRouteGroup> iGroups)
        {
            var aOut = new List<string>();
            int aDefaults = 0;
            foreach (SCP_TavernRouteGroup g in iGroups) if (g.Enabled && g.IsDefault) aDefaults++;
            if (aDefaults == 0)
                aOut.Add("沒有 enabled 的 default group ⇒ 沒帶 category（或 category 沒命中）的訊息**找不到 group、不計酬**");
            if (aDefaults > 1)
                aOut.Add($"有 {aDefaults} 個 enabled 的 default group ⇒ 只有陣列裡第一個生效");
            var aOwner = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (SCP_TavernRouteGroup g in iGroups)
            {
                if (!g.Enabled) continue;
                foreach (string c in g.Categories)
                {
                    string n = Normalize(c);
                    if (n.Length == 0) continue;
                    if (aOwner.TryGetValue(n, out string? aFirst))
                        aOut.Add($"category `{n}` 同時在 `{aFirst}` 與 `{g.Id}` ⇒ 發薪判斷取陣列裡前面那個（`{aFirst}`）");
                    else aOwner[n] = g.Id;
                }
            }
            return aOut;
        }

        // ── 解析 ────────────────────────────────────────────────────────

        /// <summary>
        /// 一則訊息落在哪個 group（發薪判斷用）：陣列裡第一個命中 category 的 enabled group；
        /// 都沒命中 ⇒ 第一個 enabled 的 default；再沒有 ⇒ null。語意逐條對齊 Unity 端 <c>ResolveTargetGroup</c>。
        /// </summary>
        public static SCP_TavernRouteGroup? ResolveTargetGroup(IReadOnlyList<SCP_TavernRouteGroup> iGroups, string? iCategory)
        {
            string n = Normalize(iCategory);
            SCP_TavernRouteGroup? aDefault = null;
            foreach (SCP_TavernRouteGroup g in iGroups)
            {
                if (!g.Enabled) continue;
                if (g.Matches(n)) return g;
                if (aDefault == null && g.IsDefault) aDefault = g;
            }
            return aDefault;
        }

        /// <summary>廣播用：所有命中的 enabled group；都沒命中 ⇒ [default]。語意對齊 Unity 端 <c>ResolveTargetGroups</c>。</summary>
        public static List<SCP_TavernRouteGroup> ResolveTargetGroups(IReadOnlyList<SCP_TavernRouteGroup> iGroups, string? iCategory)
        {
            string n = Normalize(iCategory);
            var aOut = new List<SCP_TavernRouteGroup>();
            SCP_TavernRouteGroup? aDefault = null;
            foreach (SCP_TavernRouteGroup g in iGroups)
            {
                if (!g.Enabled) continue;
                if (g.Matches(n)) aOut.Add(g);
                if (aDefault == null && g.IsDefault) aDefault = g;
            }
            if (aOut.Count == 0 && aDefault != null) aOut.Add(aDefault);
            return aOut;
        }

        // ── 寫 ──────────────────────────────────────────────────────────

        /// <summary>整份覆寫（暫存檔＋換檔），寫完**回讀**比對 id 序列。</summary>
        public static (bool Ok, string Message) Write(string iDataRoot, IReadOnlyList<SCP_TavernRouteGroup> iGroups)
        {
            string? aInvalid = Validate(iGroups);
            if (aInvalid != null) return (false, "✗ 不寫：" + aInvalid);
            string aPath = PathOf(iDataRoot);
            try
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(aPath) ?? ".");
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, ToJson(iGroups).ToJson(true), new UTF8Encoding(false));
                if (File.Exists(aPath)) File.Replace(aTmp, aPath, null);
                else File.Move(aTmp, aPath);
            }
            catch (Exception e) { return (false, "✗ 寫不進去：" + e.GetType().Name + ": " + e.Message + "（" + aPath + "）"); }

            // 回讀 —— ⛔ 不拿「沒丟例外」當落盤的證據
            SCP_TavernRoutingRead aBack = Read(iDataRoot);
            if (!aBack.Ok) return (false, "✗ 寫完回讀失敗：" + aBack.Error);
            if (aBack.Groups.Count != iGroups.Count)
                return (false, $"✗ 寫完回讀對不上：寫了 {iGroups.Count} 個 group，讀回 {aBack.Groups.Count} 個");
            for (int i = 0; i < iGroups.Count; i++)
                if (aBack.Groups[i].Id != iGroups[i].Id)
                    return (false, $"✗ 寫完回讀對不上：第 {i} 個寫 `{iGroups[i].Id}`、讀回 `{aBack.Groups[i].Id}`");
            return (true, $"✓ 已寫入 {iGroups.Count} 個 group（回讀一致）：{aPath}");
        }

        public static SCP_JsonData ToJson(IReadOnlyList<SCP_TavernRouteGroup> iGroups)
        {
            var aRoot = SCP_JsonData.NewObject();
            aRoot.Set("schema_version", SchemaVersion);
            aRoot.Set("note", "酒館路由判準（TASK-0296）。陣列順序＝比對順序。⛔ 不放 webhook URL。後台：senate ui --page tavern-routing");
            var aArr = SCP_JsonData.NewArray();
            foreach (SCP_TavernRouteGroup g in iGroups)
            {
                var o = SCP_JsonData.NewObject();
                o.Set("id", g.Id);
                var aCats = SCP_JsonData.NewArray();
                foreach (string c in g.Categories) aCats.Add(c);
                o.Set("categories", aCats);
                o.Set("enabled", g.Enabled);
                o.Set("is_default", g.IsDefault);
                o.Set("is_paid_post", g.IsPaidPost);
                o.Set("exclusive", g.Exclusive);
                o.Set("webhook_env_var", g.WebhookEnvVar);
                o.Set("webhook_file", g.WebhookFile);
                o.Set("description", g.Description);
                aArr.Add(o);
            }
            aRoot.Set("groups", aArr);
            return aRoot;
        }

        // ── 從 Unity asset 匯入（一次性搬家）─────────────────────────────

        /// <summary>
        /// 讀 Unity 端 <c>UCL_TavernCategoryRoutingAsset</c> 的 asset 目錄（一 group 一檔，檔名＝id）。
        /// <para>⚠ 那邊的布林存成**字串**（<c>"True"</c>／<c>"False"</c>），缺欄位走 Unity 類別的預設值
        /// （Enabled=true、其餘 false）。⛔ <c>WebhookUrls</c> 一律丟掉，只回報丟了幾條。</para>
        /// <para>⚠ 順序：檔名 ordinal 排序。Unity 端順序來自 asset ID 列舉 —— 兩者是否逐一相同**本函式不證明**，
        /// 由逐 category 對拍（Senate 與 Editor 解出同一個 group）驗。</para>
        /// </summary>
        public static (List<SCP_TavernRouteGroup> Groups, int StrippedUrls, List<string> Problems) ImportUnityAssetDir(string iDir)
        {
            var aGroups = new List<SCP_TavernRouteGroup>();
            var aProblems = new List<string>();
            int aStripped = 0;
            string[] aFiles;
            try { aFiles = Directory.GetFiles(iDir, "*.json"); }
            catch (Exception e) { aProblems.Add("列不出 asset 目錄：" + e.Message + "（" + iDir + "）"); return (aGroups, 0, aProblems); }
            Array.Sort(aFiles, (a, b) => string.CompareOrdinal(System.IO.Path.GetFileName(a), System.IO.Path.GetFileName(b)));
            foreach (string f in aFiles)
            {
                string aId = System.IO.Path.GetFileNameWithoutExtension(f);
                try
                {
                    SCP_JsonData d = SCP_JsonData.Parse(File.ReadAllText(f, Encoding.UTF8).TrimStart('﻿'));
                    var g = new SCP_TavernRouteGroup
                    {
                        Id = aId,
                        Enabled = LooseBool(d, "Enabled", true),
                        IsDefault = LooseBool(d, "IsDefault", false),
                        IsPaidPost = LooseBool(d, "IsPaidPost", false),
                        Exclusive = LooseBool(d, "Exclusive", false),
                        WebhookEnvVar = d.GetString("WebhookEnvVar", ""),
                        WebhookFile = d.GetString("WebhookFile", ""),
                        Description = d.GetString("Description", ""),
                    };
                    foreach (SCP_JsonData c in d["Categories"]) g.Categories.Add(c.AsString());
                    foreach (SCP_JsonData u in d["WebhookUrls"]) { if (u.Exists) aStripped++; }
                    aGroups.Add(g);
                }
                catch (Exception e) { aProblems.Add($"`{aId}` 讀不了：{e.GetType().Name}: {e.Message}"); }
            }
            return (aGroups, aStripped, aProblems);
        }

        /// <summary>Unity asset 的布林：真布林或字串 "True"/"False"；缺 ⇒ fallback；其他 ⇒ 丟例外（⛔ 不猜）。</summary>
        static bool LooseBool(SCP_JsonData iObj, string iKey, bool iFallback)
        {
            SCP_JsonData c = iObj[iKey];
            if (!c.Exists || c.IsNull) return iFallback;
            if (c.IsString)
            {
                string s = c.AsString().Trim();
                if (string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)) return true;
                if (string.Equals(s, "false", StringComparison.OrdinalIgnoreCase)) return false;
                throw new FormatException($"`{iKey}` 的值 `{s}` 不是布林");
            }
            return c.AsBool();
        }

        // ── 換檔期間安全讀檔（語意同 Editor 端 UCL_AtomicFileRead：重試跨過換檔窗口，Missing 與讀不了分開）──

        static bool TryReadText(string iPath, out string oText, out bool oMissing, out string oError)
        {
            oText = ""; oMissing = false; oError = "";
            Exception? aLast = null;
            for (int aAttempt = 1; aAttempt <= 5; ++aAttempt)
            {
                try { oText = File.ReadAllText(iPath, Encoding.UTF8); return true; }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    aLast = e;
                    if (aAttempt < 5) Thread.Sleep(2 * aAttempt);
                }
            }
            oMissing = aLast is FileNotFoundException || aLast is DirectoryNotFoundException;
            oError = aLast == null ? "" : aLast.GetType().Name + ": " + aLast.Message;
            return false;
        }
    }
}
