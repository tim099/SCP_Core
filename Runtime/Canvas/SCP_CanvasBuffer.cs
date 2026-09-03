// 區塊職責：取得當前畫布的 index-map buffer ＋ painted-mask —— 走增量快取，必要時 replay。
// 物理意義：底色填 BlankIndex，逐事件逐像素塗（同座標 last-write-wins）。
//           mask 記「曾被畫過」，不論顏色 —— 因為 index 255 身兼空白底色與可畫純白，
//           透明渲染的判定只能靠 mask。
// 數值影響：**回傳值與有沒有走快取無關** —— 快取只省時間，不准改變結果。
//           三路：① 指紋相同 ⇒ 零 replay ② 舊檔原樣＋新檔 ts 不早於水位 ⇒ 只 replay 新檔
//                 ③ 其餘（git 拉進舊事件／檔案消失／快取壞掉）⇒ 全重建。
// 設計取捨：⛔ 不照抄 3D 那套「last_event_file 之後才算新的」增量（Tim 2026-08-14 指出的情境）：
//           事件檔會**從 git 同步進來**，而同步進來的可以是「ts 較舊、檔名排在後面」的。
//           靠檔名游標會把它當成已處理 ⇒ **靜默漏掉**。所以這裡兩道判準都以「不確定就重建」為預設。
//           已知邊界：內容改了但**大小不變**的事件檔偵測不到 —— append-only 日誌不該發生那種事
//           （那是改歷史）；真要防得逐檔 hash，成本從 stat 升到讀全檔，現階段不換。
// 🩸 快取檔格式與 python 逐欄同形（schema／manifest_hash／files／max_ts，bin ＝ zlib(buf+mask)）
//    —— 兩個寫入端並存期間，格式一分岔就會變成「彼此互相作廢對方的快取」：結果仍然對，
//    但每次都全重建，而那種退化不會有任何一層喊。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Canvas
{
    /// <summary>這一次 BuildBuffer 走的是哪一路（給呼叫端印讀數用 —— 沒有讀數的快取沒有人驗得了）。</summary>
    public enum SCP_CanvasCachePath
    {
        /// <summary>指紋相同，直接用快取。</summary>
        Hit,
        /// <summary>只 replay 新事件，疊加在快取上。</summary>
        Incremental,
        /// <summary>全 replay 重建。</summary>
        FullRebuild,
    }

    public sealed class SCP_CanvasSnapshot
    {
        public byte[] Buffer = Array.Empty<byte>();
        public byte[] Mask = Array.Empty<byte>();
        public SCP_CanvasCachePath Path;
        /// <summary>本次實際 replay 的事件數（Hit ＝ 0）。</summary>
        public int ReplayedEvents;
        /// <summary>事件檔總數（清單掃出來的）。</summary>
        public int EventFiles;
        /// <summary>走 ③ 時的原因（人讀；Hit／Incremental 為空）。</summary>
        public string RebuildReason = "";
    }

    public static class SCP_CanvasBuffer
    {
        /// <summary>快取 schema 版本 —— 與 python CACHE_SCHEMA 同值；換版即作廢舊快取，不猜相容。</summary>
        public const int CacheSchema = 1;

        /// <summary>
        /// 取得當前畫布。<paramref name="iUseCache"/> false ＝ 強制全 replay（做對拍驗證用）。
        /// </summary>
        public static SCP_CanvasSnapshot Build(SCP_CanvasPaths iPaths, bool iUseCache = true)
        {
            List<SCP_CanvasEventFile> aEntries = SCP_CanvasEvents.ScanManifest(iPaths);
            var aOut = new SCP_CanvasSnapshot { EventFiles = aEntries.Count };

            if (iUseCache)
            {
                if (TryLoadCache(iPaths, out SCP_JsonData? aMeta, out byte[]? aBuf, out byte[]? aMask)
                    && aMeta != null && aBuf != null && aMask != null)
                {
                    string aNowHash = SCP_CanvasEvents.ManifestHash(aEntries);
                    if (aMeta.GetString("manifest_hash", "") == aNowHash)
                    {
                        aOut.Buffer = aBuf; aOut.Mask = aMask;
                        aOut.Path = SCP_CanvasCachePath.Hit;
                        return aOut;                                                  // 路 ①
                    }

                    // 路 ②：舊檔必須**全數原樣**仍在（同名同大小），新檔的 ts 不得早於快取水位
                    if (TryIncremental(iPaths, aMeta, aEntries, out List<string> aNewRels,
                                       out string aWhyNot))
                    {
                        List<SCP_JsonData> aNewEvs = SCP_CanvasEvents.ReadEvents(iPaths, aNewRels);
                        string aBaseTs = aMeta.GetString("max_ts", "");
                        DateTime aBase = SCP_CanvasEvents.ParseIso(aBaseTs);
                        bool aOk = aBase == DateTime.MinValue;
                        if (!aOk)
                        {
                            aOk = true;
                            foreach (SCP_JsonData aEv in aNewEvs)
                                if (SCP_CanvasEvents.ParseIso(aEv.GetString("ts", "")) < aBase) { aOk = false; break; }
                        }
                        if (aOk)
                        {
                            SCP_CanvasEvents.Apply(aBuf, aMask, aNewEvs);
                            string aMaxTs = SCP_CanvasEvents.MaxTs(aNewEvs);
                            SaveCache(iPaths, aBuf, aMask, aEntries, aMaxTs.Length > 0 ? aMaxTs : aBaseTs);
                            aOut.Buffer = aBuf; aOut.Mask = aMask;
                            aOut.Path = SCP_CanvasCachePath.Incremental;
                            aOut.ReplayedEvents = aNewEvs.Count;
                            return aOut;                                              // 路 ②
                        }
                        aOut.RebuildReason = "新事件的 ts 早於快取水位（" + aBaseTs + "）⇒ 疊加會塗錯顏色";
                    }
                    else aOut.RebuildReason = aWhyNot;
                }
                else if (File.Exists(iPaths.CacheMeta) || File.Exists(iPaths.CacheBin))
                    aOut.RebuildReason = "快取讀不出來（壞檔／schema 換版）";
                else aOut.RebuildReason = "還沒有快取";
            }
            else aOut.RebuildReason = "呼叫端要求不走快取（對拍驗證）";

            // 路 ③：全 replay
            var aFull = new byte[SCP_CanvasSpec.Area];
            for (int i = 0; i < aFull.Length; i++) aFull[i] = SCP_CanvasSpec.BlankIndex;
            var aFullMask = new byte[SCP_CanvasSpec.Area];
            List<SCP_JsonData> aAll = SCP_CanvasEvents.ReadAllEvents(iPaths);
            SCP_CanvasEvents.Apply(aFull, aFullMask, aAll);
            if (iUseCache) SaveCache(iPaths, aFull, aFullMask, aEntries, SCP_CanvasEvents.MaxTs(aAll));
            aOut.Buffer = aFull; aOut.Mask = aFullMask;
            aOut.Path = SCP_CanvasCachePath.FullRebuild;
            aOut.ReplayedEvents = aAll.Count;
            return aOut;
        }

        static bool TryIncremental(SCP_CanvasPaths iPaths, SCP_JsonData iMeta,
                                   List<SCP_CanvasEventFile> iEntries,
                                   out List<string> oNewRels, out string oWhyNot)
        {
            oNewRels = new List<string>();
            oWhyNot = "";
            var aCurrent = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (SCP_CanvasEventFile aE in iEntries) aCurrent[aE.Rel] = aE.Size;

            SCP_JsonData aFiles = iMeta["files"];
            if (!aFiles.Exists || aFiles.Count == 0) { oWhyNot = "快取沒有記檔案清單"; return false; }

            var aOld = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < aFiles.Count; i++)
            {
                SCP_JsonData aRow = aFiles[i];
                if (aRow.Count < 2) { oWhyNot = "快取的檔案清單壞了"; return false; }
                string aRel;
                long aSize;
                try { aRel = aRow[0].AsString(); aSize = aRow[1].AsLong(); }
                catch (Exception) { oWhyNot = "快取的檔案清單壞了"; return false; }
                if (!aCurrent.TryGetValue(aRel, out long aNow) || aNow != aSize)
                {
                    // 舊檔消失或大小變了 ⇒ 不能疊加（它可能是被改寫的歷史）
                    oWhyNot = "快取記的事件檔已不是原樣：" + aRel;
                    return false;
                }
                aOld.Add(aRel);
            }

            foreach (SCP_CanvasEventFile aE in iEntries)
                if (!aOld.Contains(aE.Rel)) oNewRels.Add(aE.Rel);
            oNewRels.Sort(StringComparer.Ordinal);
            return true;
        }

        static bool TryLoadCache(SCP_CanvasPaths iPaths, out SCP_JsonData? oMeta,
                                 out byte[]? oBuffer, out byte[]? oMask)
        {
            oMeta = null; oBuffer = null; oMask = null;
            try
            {
                if (!File.Exists(iPaths.CacheMeta) || !File.Exists(iPaths.CacheBin)) return false;
                SCP_JsonData aMeta = SCP_JsonParser.Parse(File.ReadAllText(iPaths.CacheMeta, Encoding.UTF8));
                if (aMeta.GetInt("schema", -1) != CacheSchema) return false;
                byte[] aBlob = SCP_CanvasDeflate.ZlibDecompress(File.ReadAllBytes(iPaths.CacheBin));
                if (aBlob.Length != SCP_CanvasSpec.Area * 2) return false;   // 長度不對＝壞檔，不硬讀
                var aBuf = new byte[SCP_CanvasSpec.Area];
                var aMask = new byte[SCP_CanvasSpec.Area];
                Buffer.BlockCopy(aBlob, 0, aBuf, 0, SCP_CanvasSpec.Area);
                Buffer.BlockCopy(aBlob, SCP_CanvasSpec.Area, aMask, 0, SCP_CanvasSpec.Area);
                oMeta = aMeta; oBuffer = aBuf; oMask = aMask;
                return true;
            }
            catch (Exception)
            {
                // 壞快取不是錯誤路徑，是「重建」路徑 —— 這裡吞掉例外是刻意的。
                return false;
            }
        }

        /// <summary>落快取（先寫 .tmp 再 replace —— 半寫的快取比沒有快取更糟）。</summary>
        public static void SaveCache(SCP_CanvasPaths iPaths, byte[] iBuffer, byte[] iMask,
                                     List<SCP_CanvasEventFile> iEntries, string iMaxTs)
        {
            try
            {
                Directory.CreateDirectory(iPaths.Root);
                var aBlob = new byte[SCP_CanvasSpec.Area * 2];
                Buffer.BlockCopy(iBuffer, 0, aBlob, 0, SCP_CanvasSpec.Area);
                Buffer.BlockCopy(iMask, 0, aBlob, SCP_CanvasSpec.Area, SCP_CanvasSpec.Area);
                string aTmpBin = iPaths.CacheBin + ".tmp";
                File.WriteAllBytes(aTmpBin, SCP_CanvasDeflate.ZlibCompress(aBlob));
                Replace(aTmpBin, iPaths.CacheBin);

                SCP_JsonData aMeta = SCP_JsonData.NewObject();
                aMeta["schema"] = CacheSchema;
                aMeta["manifest_hash"] = SCP_CanvasEvents.ManifestHash(iEntries);
                aMeta["event_count"] = iEntries.Count;
                aMeta["max_ts"] = iMaxTs;
                SCP_JsonData aFiles = SCP_JsonData.NewArray();
                foreach (SCP_CanvasEventFile aE in iEntries)
                {
                    SCP_JsonData aRow = SCP_JsonData.NewArray();
                    aRow.Add(SCP_JsonData.NewString(aE.Rel));
                    aRow.Add(SCP_JsonData.NewNumber(aE.Size));
                    aFiles.Add(aRow);
                }
                aMeta["files"] = aFiles;
                aMeta["built_at"] = SCP_CanvasEvents.IsoMs(DateTime.UtcNow);
                string aTmpMeta = iPaths.CacheMeta + ".tmp";
                File.WriteAllText(aTmpMeta, SCP_JsonWriter.Write(aMeta, false), new UTF8Encoding(false));
                Replace(aTmpMeta, iPaths.CacheMeta);
            }
            catch (Exception)
            {
                // 快取寫不進去不該擋住主流程（結果不受影響，只是下次還要重建）。
            }
        }

        static void Replace(string iTmp, string iTarget)
        {
            if (File.Exists(iTarget)) File.Delete(iTarget);
            File.Move(iTmp, iTarget);
        }

        /// <summary>統計用：把數字格式化成 InvariantCulture（避免不同機器印出不同小數點）。</summary>
        public static string Percent(double iValue)
        {
            return iValue.ToString("F6", CultureInfo.InvariantCulture);
        }
    }
}
