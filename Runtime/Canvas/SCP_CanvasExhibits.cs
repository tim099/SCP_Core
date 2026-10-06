// 區塊職責：2D 畫布的**展品**（TASK-0443，Tim 2026-10-06「把標題當展品 id」）——
//           由宣稱區域（`Canvas/claims.json`）推導出來的視圖，⛔ 不是另一份要維護的清單。
// 物理意義：3D 雕刻有登錄制的展品（`Sculpture/exhibits.json`）；2D 畫布沒有，最接近的是 claim
//           （誰、畫什麼、在哪）。一件作品常常分好幾次宣稱（kotoko 的心 ＋ 補完尖角、basecamp 山脈擴建…），
//           所以**同一個標題的 claim 合成一件展品**，範圍取聯集外框。
//           ⇒ 事實源仍只有 claims.json：改標題就是改展品 id，不會有兩份清單互相漂移。
// 數值影響：純讀（claims.json）；不寫任何檔。
// ⚠ 標題空白的 claim 沒有名字可當 id ⇒ 給 `claim:<claim id>`（仍然叫得到，⛔ 不併成一件「無標題」）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。JSON 一律走 SCP_Json。
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Canvas
{
    /// <summary>一件 2D 展品 ＝ 同一個標題的所有宣稱區域。</summary>
    public sealed class SCP_CanvasExhibit
    {
        /// <summary>展品 id ＝ 標題（trim 過）；標題空白 ⇒ <c>claim:&lt;claim id&gt;</c>。</summary>
        public string Id = "";
        public readonly List<string> ClaimIds = new List<string>();
        public readonly List<string> Personas = new List<string>();
        /// <summary>任一筆 claim 還是 active ⇒ true（作品還在畫）。</summary>
        public bool AnyActive;
        /// <summary>聯集外框（已夾進畫布）。</summary>
        public int X, Y, W, H;
        /// <summary>最早那筆 claim 的建立時間（排序用；讀不到是空字串）。</summary>
        public string CreatedAt = "";

        public string RegionText => X + "," + Y + "," + W + "," + H;
        public string StatusText => AnyActive ? "active" : "done";
    }

    public static class SCP_CanvasExhibits
    {
        public const string UntitledPrefix = "claim:";

        /// <summary>
        /// 讀 claims.json 推導展品清單（照第一次出現的順序）。檔案不存在 ⇒ 空清單、回 true（還沒有人宣稱過）；
        /// 讀不了／壞 JSON ⇒ 回 false 並說原因（⛔ 不回空清單 —— 「沒有展品」與「讀不到」不可同形）。
        /// </summary>
        public static bool TryLoad(SCP_CanvasPaths iPaths, out List<SCP_CanvasExhibit> oExhibits, out string oError)
        {
            oExhibits = new List<SCP_CanvasExhibit>();
            oError = "";
            if (!File.Exists(iPaths.Claims)) return true;
            SCP_JsonData aData;
            try { aData = SCP_JsonParser.Parse(File.ReadAllText(iPaths.Claims)); }
            catch (Exception e) { oError = "claims.json 讀不了：" + e.GetType().Name + ": " + e.Message; return false; }
            if (!aData.Contains("claims") || !aData["claims"].IsArray)
            { oError = "claims.json 沒有 `claims` 陣列"; return false; }
            oExhibits = FromClaims(aData["claims"]);
            return true;
        }

        /// <summary>claims 陣列 → 展品（純函式，測試直接餵）。</summary>
        public static List<SCP_CanvasExhibit> FromClaims(SCP_JsonData iClaims)
        {
            var aOut = new List<SCP_CanvasExhibit>();
            var aById = new Dictionary<string, SCP_CanvasExhibit>(StringComparer.Ordinal);
            var aBox = new Dictionary<string, int[]>(StringComparer.Ordinal);   // x1,y1,x2,y2（不含）
            for (int i = 0; i < iClaims.Count; i++)
            {
                SCP_JsonData aC = iClaims[i];
                if (!aC.Contains("region")) continue;
                SCP_JsonData aR = aC["region"];
                int x = aR.GetInt("x", -1), y = aR.GetInt("y", -1), w = aR.GetInt("w", 0), h = aR.GetInt("h", 0);
                if (w <= 0 || h <= 0) continue;
                int x1 = Math.Max(0, x), y1 = Math.Max(0, y);
                int x2 = Math.Min(SCP_CanvasSpec.Width, x + w), y2 = Math.Min(SCP_CanvasSpec.Height, y + h);
                if (x2 <= x1 || y2 <= y1) continue;   // 整個落在畫布外

                string aClaimId = aC.GetString("id", "?");
                string aTitle = aC.GetString("title", "").Trim();
                string aId = aTitle.Length > 0 ? aTitle : UntitledPrefix + aClaimId;
                if (!aById.TryGetValue(aId, out SCP_CanvasExhibit? aEx))
                {
                    aEx = new SCP_CanvasExhibit { Id = aId, CreatedAt = aC.GetString("created_at", "") };
                    aById[aId] = aEx;
                    aBox[aId] = new[] { x1, y1, x2, y2 };
                    aOut.Add(aEx);
                }
                else
                {
                    int[] b = aBox[aId];
                    b[0] = Math.Min(b[0], x1); b[1] = Math.Min(b[1], y1);
                    b[2] = Math.Max(b[2], x2); b[3] = Math.Max(b[3], y2);
                }
                aEx.ClaimIds.Add(aClaimId);
                string aPersona = aC.GetString("persona", "?");
                if (!aEx.Personas.Contains(aPersona)) aEx.Personas.Add(aPersona);
                if (aC.GetString("status", "") == "active") aEx.AnyActive = true;
            }
            foreach (SCP_CanvasExhibit aEx in aOut)
            {
                int[] b = aBox[aEx.Id];
                aEx.X = b[0]; aEx.Y = b[1]; aEx.W = b[2] - b[0]; aEx.H = b[3] - b[1];
            }
            return aOut;
        }

        /// <summary>依 id 找展品：先逐字，再忽略大小寫（只命中一件才算）。找不到回 null。</summary>
        public static SCP_CanvasExhibit? Find(List<SCP_CanvasExhibit> iList, string iId)
        {
            string aId = (iId ?? "").Trim();
            foreach (SCP_CanvasExhibit e in iList) if (e.Id == aId) return e;
            SCP_CanvasExhibit? aHit = null;
            foreach (SCP_CanvasExhibit e in iList)
                if (string.Equals(e.Id, aId, StringComparison.OrdinalIgnoreCase))
                {
                    if (aHit != null) return null;   // 大小寫不同的兩件 ⇒ 不猜
                    aHit = e;
                }
            return aHit;
        }

        /// <summary>外框往外留 <paramref name="iPad"/> 格（夾進畫布）。</summary>
        public static void Padded(SCP_CanvasExhibit iEx, int iPad, out int oX, out int oY, out int oW, out int oH)
        {
            int aPad = Math.Max(0, iPad);
            int x1 = Math.Max(0, iEx.X - aPad), y1 = Math.Max(0, iEx.Y - aPad);
            int x2 = Math.Min(SCP_CanvasSpec.Width, iEx.X + iEx.W + aPad);
            int y2 = Math.Min(SCP_CanvasSpec.Height, iEx.Y + iEx.H + aPad);
            oX = x1; oY = y1; oW = x2 - x1; oH = y2 - y1;
        }
    }
}
