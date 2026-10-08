// 區塊職責：**Discord 圖片／附件的兩個方向**（TASK-0323）—— Inbound 附件下載落地、Outbound 挑出要上傳的圖。
// 物理意義：
//   · Inbound：Discord 附件 URL 帶簽章、會過期 ⇒ 必須**落地保存**，否則事後讀訊息的 agent 開不了圖。
//     落點（新舊資料混放不衝突）：`<DataRoot>/ChatTavern/media/discord/<yyyy-MM-dd>/<msgId>__<attId>__<檔名>`。
//     ⭐ 日期取自**訊息 id（snowflake）裡的時間**，不取「現在」⇒ 寫入失敗下一輪重抓時落在同一格、已存在就不重抓。
//     refs[].path 記 **repo 相對路徑＋斜線**（＝酒館 refs 慣例，跟畫布分享圖同形）。
//   · Outbound：從訊息 refs 挑出本地圖檔（副檔名 png/jpg/jpeg/gif/webp、檔案實存），轉成 multipart 的檔案段。
// 數值影響（上限）：
//   · 下載：單檔 ≤ <see cref="MaxDownloadBytes"/>（24MB；Discord 附件免費上限 25MB）。過大 ⇒ 跳過並在本文標明。
//   · 上傳：每則最多 <see cref="MaxUploadFiles"/> 張、單檔 ≤ <see cref="MaxUploadFileBytes"/>、合計 ≤ <see cref="MaxUploadTotalBytes"/>
//     （Discord 沒加成的伺服器每個請求約 10MB）。超過的 ⇒ 不上傳、在本文末尾列「未上傳：檔名（原因）」—— ⛔ 不靜默少一張。
//   · 失敗一律 fail-soft：附件掛掉不能讓文字消失。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。會連網的只有 Download ⇒ 只給 Senate 宿主呼叫。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Market;
using SCP.Core.Tavern;

namespace SCP.Core.Discord
{
    public static class SCP_DiscordMedia
    {
        public const long MaxDownloadBytes = 24L * 1024 * 1024;
        public const int DownloadTimeoutSec = 30;
        public const int MaxUploadFiles = 4;
        public const long MaxUploadFileBytes = 8_000_000;
        public const long MaxUploadTotalBytes = 9_500_000;
        static readonly string[] s_ImageExts = { ".png", ".jpg", ".jpeg", ".gif", ".webp" };
        static readonly char[] s_UnsafeFileChars = { '<', '>', ':', '"', '/', '\\', '|', '?', '*', '=', ' ' };

        // ⛔ 2026-10-07（TASK-0390）刪掉 `RepoRootOf`（資料根上一層＝repo 根）與 `MakeRepoRelative`：
        //   refs 改存資料根相對，存法與解法都在 SCP_TavernRefPath。

        public static string MediaDir(string iDataRoot) => Path.Combine(SCP.Core.Paths.SCP_DataPaths.ChatTavern(new SCP.Core.Paths.SCP_DataRoot(iDataRoot)), "media", "discord");

        /// <summary>Discord 檔名 ⇒ 本地安全檔名。</summary>
        public static string SanitizeFileName(string iName)
        {
            if (string.IsNullOrEmpty(iName)) return "unnamed";
            var sb = new StringBuilder(iName.Length);
            foreach (char c in iName) sb.Append(c < 0x20 || Array.IndexOf(s_UnsafeFileChars, c) >= 0 ? '_' : c);
            string t = sb.ToString().TrimStart('-', '.', '_');
            return t.Length == 0 ? "unnamed" : t;
        }

        /// <summary>snowflake ⇒ UTC 日期（`yyyy-MM-dd`）；解析不了 ⇒ 今天。</summary>
        public static string DateOfSnowflake(string iId)
        {
            if (ulong.TryParse(iId, NumberStyles.None, CultureInfo.InvariantCulture, out ulong v))
            {
                long aMs = (long)(v >> 22) + 1420070400000L;
                return DateTimeOffset.FromUnixTimeMilliseconds(aMs).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
            return DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        // ── Inbound ──────────────────────────────────────────────────

        /// <summary>一個附件的處理結果。<see cref="Ref"/>＝null ⇒ 沒落地（<see cref="Note"/> 說為什麼）。</summary>
        public sealed class InboundAttachment
        {
            public string FileName = "";
            public long Size;
            public SCP_TavernRef? Ref;
            public string Note = "";
        }

        /// <summary>
        /// 下載一則訊息的全部附件。<paramref name="iDownload"/>=false（偷看模式）⇒ 只列、⛔ 不連網不落檔。
        /// ⛔ 不丟例外；每個附件各自成敗。
        /// </summary>
        public static List<InboundAttachment> DownloadAttachments(string iDataRoot, string iRepoRoot, string iMsgId,
                                                                  SCP_JsonData iAttachments, bool iDownload)
        {
            var aOut = new List<InboundAttachment>();
            if (!iAttachments.IsArray) return aOut;
            // refs 一律存「資料根相對」（TASK-0390）—— 存法在 SCP_TavernRefPath；⛔ 不用 repo 根（呼叫端給的那個是 Senate 自己的 repo，
            //   2026-09-28 seq 22520）。iRepoRoot 保留只為了不改簽名。
            string aDir = Path.Combine(MediaDir(iDataRoot), DateOfSnowflake(iMsgId));
            for (int i = 0; i < iAttachments.Count; i++)
            {
                SCP_JsonData a = iAttachments[i];
                var r = new InboundAttachment
                {
                    FileName = a.GetString("filename", ""),
                    Size = a.GetLong("size", 0),
                };
                if (r.FileName.Length == 0) r.FileName = "（無檔名）";
                aOut.Add(r);
                string aUrl = a.GetString("url", "");
                string aAttId = a.GetString("id", i.ToString(CultureInfo.InvariantCulture));
                if (aUrl.Length == 0) { r.Note = "沒有下載網址"; continue; }
                if (r.Size > MaxDownloadBytes) { r.Note = $"過大未下載（{FormatSize(r.Size)}，上限 {FormatSize(MaxDownloadBytes)}）"; continue; }
                if (!iDownload) { r.Note = "偷看模式不下載"; continue; }
                string aLocal = Path.Combine(aDir, $"{iMsgId}__{aAttId}__{SanitizeFileName(r.FileName)}");
                try
                {
                    if (!File.Exists(aLocal))   // 同一則重抓（上一輪寫入失敗）⇒ 不重下載
                    {
                        if (!(SCP_HttpFetch.Current is ISCP_HttpBytesFetcher f)) { r.Note = "本宿主不能下載（要在 Senate 跑）"; continue; }
                        if (!f.TryGetBytes(aUrl, MaxDownloadBytes, DownloadTimeoutSec, out byte[] aData, out _, out string? aErr))
                        { r.Note = "下載失敗：" + (aErr ?? "?"); continue; }
                        if (aData.Length == 0) { r.Note = "下載到 0 bytes"; continue; }
                        Directory.CreateDirectory(aDir);
                        File.WriteAllBytes(aLocal + ".tmp", aData);
                        SCP.Core.Io.SCP_TextFile.ReplaceOrMove(aLocal + ".tmp", aLocal);
                    }
                    // 標籤只放檔名：Discord 回報的 content_type 不可信（實測 22520：標 image/webp、位元組是 PNG）
                    r.Ref = new SCP_TavernRef { Path = SCP_TavernRefPath.ToStored(iDataRoot, aLocal, out _), Label = r.FileName };
                }
                catch (Exception e) { r.Note = "落地失敗：" + e.GetType().Name + "：" + e.Message; }
            }
            return aOut;
        }

        /// <summary>本文用的一行：`[Discord 附件 N 個] a.png, b.zip（過大未下載 30MB）`。</summary>
        public static string DescribeLine(List<InboundAttachment> iAtts)
        {
            var aParts = new List<string>();
            foreach (InboundAttachment a in iAtts)
                aParts.Add(a.Ref != null || a.Note.Length == 0 ? a.FileName : $"{a.FileName}（{a.Note}）");
            return $"[Discord 附件 {iAtts.Count} 個] " + string.Join(", ", aParts);
        }

        // ── Outbound ─────────────────────────────────────────────────

        /// <summary>
        /// 從訊息 refs 挑出要上傳的圖。回檔案段；<paramref name="oSkipped"/>＝「檔名（原因）」—— 呼叫端要把它寫進本文。
        /// 非圖片的 ref 不算跳過（它本來就不是要上傳的東西）。
        /// </summary>
        public static List<SCP_HttpFilePart> CollectUploads(string iDataRoot, string iRepoRoot, SCP_TavernMessage iMsg, out List<string> oSkipped)
        {
            var aOut = new List<SCP_HttpFilePart>();
            oSkipped = new List<string>();
            if (iMsg.Refs == null || iMsg.Refs.Count == 0) return aOut;
            // 解法唯一一處：SCP_TavernRefPath（資料根相對；舊的 `AgentCommands/…` 去前綴）—— iRepoRoot 保留只為了不改簽名。
            long aTotal = 0;
            foreach (SCP_TavernRef aRef in iMsg.Refs)
            {
                string aRel = aRef.Path ?? "";
                if (aRel.Trim().Length == 0) continue;
                string aExt = Path.GetExtension(aRel).ToLowerInvariant();
                if (Array.IndexOf(s_ImageExts, aExt) < 0) continue;
                string aName = Path.GetFileName(aRel);
                string aAbs;
                try { aAbs = Path.GetFullPath(SCP_TavernRefPath.Resolve(iDataRoot, aRel)); }
                catch (Exception) { oSkipped.Add($"{aName}（路徑解析不了）"); continue; }
                if (!File.Exists(aAbs)) { oSkipped.Add($"{aName}（找不到檔案）"); continue; }
                long aLen;
                try { aLen = new FileInfo(aAbs).Length; } catch (Exception) { oSkipped.Add($"{aName}（讀不到大小）"); continue; }
                if (aOut.Count >= MaxUploadFiles) { oSkipped.Add($"{aName}（超過每則 {MaxUploadFiles} 張）"); continue; }
                if (aLen > MaxUploadFileBytes) { oSkipped.Add($"{aName}（{FormatSize(aLen)} 超過單檔 {FormatSize(MaxUploadFileBytes)}）"); continue; }
                if (aTotal + aLen > MaxUploadTotalBytes) { oSkipped.Add($"{aName}（合計超過 {FormatSize(MaxUploadTotalBytes)}）"); continue; }
                byte[] aData;
                try { aData = File.ReadAllBytes(aAbs); } catch (Exception e) { oSkipped.Add($"{aName}（讀檔失敗：{e.GetType().Name}）"); continue; }
                aTotal += aLen;
                aOut.Add(new SCP_HttpFilePart
                {
                    FieldName = $"files[{aOut.Count}]",
                    FileName = aName,
                    ContentType = ContentTypeOf(aExt),
                    Data = aData,
                });
            }
            return aOut;
        }

        static string ContentTypeOf(string iExt)
        {
            switch (iExt)
            {
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".webp": return "image/webp";
                default: return "application/octet-stream";
            }
        }

        public static string FormatSize(long iBytes)
        {
            if (iBytes >= 1024 * 1024) return (iBytes / 1024.0 / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + "MB";
            if (iBytes >= 1024) return (iBytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + "KB";
            return iBytes.ToString(CultureInfo.InvariantCulture) + "B";
        }
    }
}
