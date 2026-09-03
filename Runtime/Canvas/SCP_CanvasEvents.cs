// 區塊職責：事件日誌的讀取、排序與塗色 —— 畫布的**唯一事實源**就是 events/ 底下那些 json。
// 物理意義：events/<日期>/<時分秒>_<毫秒>_<uuid>.json，append-only；一筆事件含 pixels 陣列。
//           同座標多筆時「ts 最晚的那筆勝」（last-write-wins）。
// 數值影響：排序主 key ＝ 事件 JSON 裡的 ts（毫秒精度），次 key ＝ uuid（同毫秒的 deterministic tiebreak）。
//           ⛔ **不靠檔名排序** —— 檔名只有秒級精度，同秒兩筆的字典序 tiebreak 是隨機 uuid 序，
//           不是真實時間序。壞掉／缺少 ts 的事件退到最舊（DateTime.MinValue），
//           讓有效 ts 的事件永遠勝過它。
// 設計取捨：時間字串一律以 InvariantCulture 解析、當作 naive UTC —— 與 python parse_iso 同形
//           （容忍尾端 Z、容忍無毫秒）。⚠ 換成 DateTimeOffset 會讓「無時區的舊 ts」被當成本地時間，
//           而那種偏移在 +08 這台是 8 小時，剛好足以把兩筆事件的先後對調。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Canvas
{
    /// <summary>一個事件檔在清單裡的身分：相對路徑 ＋ 位元組大小。</summary>
    public readonly struct SCP_CanvasEventFile
    {
        public readonly string Rel;
        public readonly long Size;
        public SCP_CanvasEventFile(string iRel, long iSize) { Rel = iRel; Size = iSize; }
    }

    public static class SCP_CanvasEvents
    {
        /// <summary>ISO8601（容忍尾端 Z／無毫秒）→ naive UTC；失敗回 <see cref="DateTime.MinValue"/>。</summary>
        public static DateTime ParseIso(string? iTs)
        {
            string aS = (iTs ?? "").Trim().TrimEnd('Z');
            if (aS.Length == 0) return DateTime.MinValue;
            string[] aFormats = { "yyyy-MM-ddTHH:mm:ss.FFFFFF", "yyyy-MM-ddTHH:mm:ss" };
            if (DateTime.TryParseExact(aS, aFormats, CultureInfo.InvariantCulture,
                                       DateTimeStyles.None, out DateTime aDt))
                return aDt;
            return DateTime.MinValue;
        }

        /// <summary>naive UTC → ISO8601 毫秒 Z（與 python iso_ms 逐字同形）。</summary>
        public static string IsoMs(DateTime iUtc)
        {
            return iUtc.ToString("yyyy-MM-ddTHH:mm:ss.", CultureInfo.InvariantCulture)
                   + (iUtc.Millisecond).ToString("000", CultureInfo.InvariantCulture) + "Z";
        }

        /// <summary>
        /// 掃出所有事件檔的（相對路徑, 大小），依相對路徑排序。
        /// <para>只 stat 不解析 JSON —— 這是「要不要重建快取」的判斷成本下限。</para>
        /// </summary>
        public static List<SCP_CanvasEventFile> ScanManifest(SCP_CanvasPaths iPaths)
        {
            var aOut = new List<SCP_CanvasEventFile>();
            if (!Directory.Exists(iPaths.Events)) return aOut;
            var aDirs = new List<string>(Directory.GetDirectories(iPaths.Events));
            aDirs.Sort(StringComparer.Ordinal);
            foreach (string aDir in aDirs)
            {
                string aDirName = Path.GetFileName(aDir);
                var aFiles = new List<string>(Directory.GetFiles(aDir, "*.json"));
                aFiles.Sort(StringComparer.Ordinal);
                foreach (string aFile in aFiles)
                {
                    long aSize;
                    try { aSize = new FileInfo(aFile).Length; }
                    catch (IOException) { continue; }
                    catch (UnauthorizedAccessException) { continue; }
                    aOut.Add(new SCP_CanvasEventFile(aDirName + "/" + Path.GetFileName(aFile), aSize));
                }
            }
            aOut.Sort((a, b) => StringComparer.Ordinal.Compare(a.Rel, b.Rel));
            return aOut;
        }

        /// <summary>清單指紋（新增／刪除／改大小都會讓它變）。與 python _manifest_hash 同式。</summary>
        public static string ManifestHash(List<SCP_CanvasEventFile> iEntries)
        {
            using var aSha = SHA256.Create();
            var aSb = new StringBuilder();
            foreach (SCP_CanvasEventFile aE in iEntries)
                aSb.Append(aE.Rel).Append(':').Append(aE.Size.ToString(CultureInfo.InvariantCulture)).Append('\n');
            byte[] aHash = aSha.ComputeHash(Encoding.UTF8.GetBytes(aSb.ToString()));
            var aHex = new StringBuilder(aHash.Length * 2);
            foreach (byte b in aHash) aHex.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return aHex.ToString();
        }

        /// <summary>讀指定的事件檔（相對路徑）並依 ts 排序；讀不動／解不開的**跳過**（不讓一顆壞檔擋住整張畫布）。</summary>
        public static List<SCP_JsonData> ReadEvents(SCP_CanvasPaths iPaths, IEnumerable<string> iRels)
        {
            var aOut = new List<SCP_JsonData>();
            foreach (string aRel in iRels)
            {
                SCP_JsonData? aEv = TryReadEvent(iPaths.Events + "/" + aRel);
                if (aEv != null) aOut.Add(aEv);
            }
            Sort(aOut);
            return aOut;
        }

        /// <summary>讀全部事件（依 ts 排序）。</summary>
        public static List<SCP_JsonData> ReadAllEvents(SCP_CanvasPaths iPaths)
        {
            var aRels = new List<string>();
            foreach (SCP_CanvasEventFile aE in ScanManifest(iPaths)) aRels.Add(aE.Rel);
            return ReadEvents(iPaths, aRels);
        }

        static SCP_JsonData? TryReadEvent(string iPath)
        {
            try { return SCP_JsonParser.Parse(File.ReadAllText(iPath, Encoding.UTF8)); }
            catch (IOException) { return null; }
            catch (UnauthorizedAccessException) { return null; }
            catch (SCP_JsonParseException) { return null; }
        }

        /// <summary>依 (ts, uuid) 穩定排序 —— replay 順序就是這個順序。</summary>
        public static void Sort(List<SCP_JsonData> ioEvents)
        {
            ioEvents.Sort((a, b) =>
            {
                int aCmp = ParseIso(a.GetString("ts", "")).CompareTo(ParseIso(b.GetString("ts", "")));
                if (aCmp != 0) return aCmp;
                return StringComparer.Ordinal.Compare(a.GetString("uuid", ""), b.GetString("uuid", ""));
            });
        }

        /// <summary>事件集合裡最晚的 ts（回原字串；空集合回空字串）。</summary>
        public static string MaxTs(List<SCP_JsonData> iEvents)
        {
            DateTime aBest = DateTime.MinValue;
            string aBestS = "";
            foreach (SCP_JsonData aEv in iEvents)
            {
                string aTs = aEv.GetString("ts", "");
                DateTime aDt = ParseIso(aTs);
                // ⚠ 比大小走解析後的時間不走字串比對：只要有一筆缺毫秒或帶別種時區寫法，
                //   字串序就跟時間序不一致，而那種錯會安靜地把增量判定變成擲骰子。
                if (aDt != DateTime.MinValue && (aBestS.Length == 0 || aDt > aBest))
                {
                    aBest = aDt;
                    aBestS = aTs;
                }
            }
            return aBestS;
        }

        /// <summary>
        /// 把事件逐像素塗進 buffer/mask（全 replay 與增量共用這一份塗法，不寫第二套）。
        /// <para>越界座標與解不出來的顏色**跳過** —— 與 python 同形。</para>
        /// </summary>
        public static void Apply(byte[] ioBuffer, byte[] ioMask, List<SCP_JsonData> iEvents)
        {
            foreach (SCP_JsonData aEv in iEvents)
            {
                SCP_JsonData aPixels = aEv["pixels"];
                if (!aPixels.Exists) continue;
                for (int i = 0; i < aPixels.Count; i++)
                {
                    SCP_JsonData aPx = aPixels[i];
                    SCP_JsonData aXd = aPx["x"], aYd = aPx["y"];
                    if (!aXd.Exists || !aYd.Exists) continue;
                    int aX, aY;
                    try { aX = aXd.AsInt(); aY = aYd.AsInt(); }
                    catch (Exception) { continue; }
                    if (!SCP_CanvasSpec.InBounds(aX, aY)) continue;
                    if (!TryColor(aPx["color"], out int aIdx)) continue;
                    int aPos = aY * SCP_CanvasSpec.Width + aX;
                    ioBuffer[aPos] = (byte)aIdx;
                    ioMask[aPos] = 1;
                }
            }
        }

        /// <summary>事件 JSON 裡的 color 欄（數字或字串）→ palette index。</summary>
        public static bool TryColor(SCP_JsonData iColor, out int oIndex)
        {
            oIndex = 0;
            if (!iColor.Exists || iColor.IsNull) return false;
            string aText;
            try { aText = iColor.AsString(); }
            catch (Exception)
            {
                try { aText = iColor.AsLong().ToString(CultureInfo.InvariantCulture); }
                catch (Exception) { return false; }
            }
            return SCP_CanvasPalette.TryParse(aText, out oIndex, out _);
        }
    }
}
