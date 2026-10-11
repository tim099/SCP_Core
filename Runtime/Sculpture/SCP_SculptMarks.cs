// 區塊職責：作品的時間標記（TASK-0491）—— 「某一刻」＝ 重播到某個事件（含）為止的樣子；區間 ＝ 同名的開始＋結束兩個標記。
// 物理意義：標記存在空間根的 marks.json，⛔ 不寫進 events/ —— 舊版重播器看不到它也照常重播
//          （stampvox 那次的教訓：新 op 進了 events，沒更新的 senate.exe 就讀不懂整件作品）。
//          一個標記只記「哪一個事件檔」；事件是 append-only，所以那一刻永遠重播得出來（undo／redo 也是帶前後內容的事件，照順序套用就對）。
// 數值影響：觀測時從頭重播到那個事件 —— 不讀、不寫主快取（快取是「現在」，拿它當起點會把未來帶進過去）。
// 失敗處置：名字不存在、事件不在、同名有開始與結束卻沒說哪一個、marks.json 讀不了 ⇒ ArgumentException／InvalidOperationException，
//          呼叫端轉 exit 2、零寫入。⛔ marks.json 讀不了時不當成空的 —— 寫回去就把別人的標記全清掉了。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptMark
    {
        public string name = "", phase = "";
        /// <summary>相對 events/ 的路徑（一律正斜線）；空字串 ＝ 第一個事件之前（空的作品）。</summary>
        public string @event = "";
        /// <summary>標的當下這個事件是第幾個（1 起算；0 ＝ 第一個事件之前）—— 只是給人看的，解析一律照 <see cref="@event"/>。</summary>
        public int event_index;
        public string created_at = "", persona = "", note = "";
    }

    public sealed class SCP_SculptMarkFile
    {
        public int schema = 1;
        public List<SCP_SculptMark> marks = new List<SCP_SculptMark>();
    }

    /// <summary>觀測的時間條件：重播到哪、哪幾段撤掉、只看哪一段。事件一律以 events 清單裡的序號表示（−1 ＝ 第一個事件之前）。</summary>
    public sealed class SCP_SculptViewFilter
    {
        public string UptoEvent = "";
        public bool UptoStart;
        public readonly List<(string Begin, string End, string Name)> Hide = new List<(string, string, string)>();
        public (string Begin, string End, string Name)? Only;
        public bool Active => UptoEvent.Length > 0 || UptoStart || Hide.Count > 0 || Only != null;
    }

    public static class SCP_SculptMarks
    {
        public const string PhasePoint = "point", PhaseBegin = "begin", PhaseEnd = "end";
        public const string Start = "start", Now = "now";

        static string Norm(string iRel) => iRel.Replace('\\', '/');

        public static SCP_SculptMarkFile Read(SCP_SculptPaths iPaths)
        {
            string aFile = iPaths.MarksFile;
            if (!File.Exists(aFile)) return new SCP_SculptMarkFile();
            try
            {
                var aOut = new SCP_SculptMarkFile { schema = 0 };
                var aOpt = new SCP_JsonMapOptions();
                SCP_JsonMapper.Populate(aOut, SCP_JsonParser.Parse(File.ReadAllText(aFile, new UTF8Encoding(false)), false), aOpt);
                if (aOpt.Diagnostics.Count > 0) throw new InvalidOperationException(string.Join("; ", aOpt.Diagnostics));
                if (aOut.schema != 1) throw new InvalidOperationException("schema=" + aOut.schema);
                return aOut;
            }
            catch (Exception e) when (e is not InvalidOperationException)
            {
                throw new InvalidOperationException("marks.json 讀不了（" + e.Message + "）—— ⛔ 不當成空的，修好再標");
            }
        }

        static void Save(SCP_SculptPaths iPaths, SCP_SculptMarkFile iFile)
        {
            string aFile = iPaths.MarksFile;
            string aTmp = aFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(iFile), true) + "\n", new UTF8Encoding(false));
                if (File.Exists(aFile)) File.Replace(aTmp, aFile, null);
                else File.Move(aTmp, aFile);
            }
            finally { try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch (Exception) { } }
        }

        /// <summary>事件在清單裡的序號；<see cref="Start"/>／空字串 ⇒ −1；找不到 ⇒ ArgumentException。</summary>
        public static int IndexOf(List<SCP_SculptStore.EventFile> iEvents, string iRel, string iWho)
        {
            if (iRel.Length == 0 || iRel == Start) return -1;
            string aWant = Norm(iRel);
            for (int i = 0; i < iEvents.Count; i++)
                if (Norm(iEvents[i].Rel) == aWant) return i;
            throw new ArgumentException(iWho + " 指的事件 `" + aWant + "` 不在 events/ 裡（被刪、改名，或打錯了？）");
        }

        /// <summary>事件：相對 events/ 的路徑，或唯一的檔名（不含資料夾）⇒ 序號；對不到或檔名不唯一 ⇒ ArgumentException。</summary>
        public static int FindEvent(List<SCP_SculptStore.EventFile> iEvents, string iSpec, string iWho)
        {
            if (iSpec.IndexOf('/') >= 0 || iSpec.IndexOf('\\') >= 0) return IndexOf(iEvents, iSpec, iWho);
            int aIdx = -1;
            for (int i = 0; i < iEvents.Count; i++)
                if (Path.GetFileName(iEvents[i].Rel) == iSpec)
                {
                    if (aIdx >= 0) throw new ArgumentException("檔名 `" + iSpec + "` 對到不只一個事件 —— 請給相對 events/ 的完整路徑");
                    aIdx = i;
                }
            if (aIdx < 0) throw new ArgumentException(iWho + " 指的事件 `" + iSpec + "` 不在 events/ 裡");
            return aIdx;
        }

        /// <summary>
        /// 一個「時間點」的寫法 ⇒ events 清單裡的序號（−1 ＝ 第一個事件之前）（TASK-0492，差異的兩端用）：
        /// 空字串／<c>now</c> ＝ 最新；<c>start</c> ＝ 開始之前；以 <c>.json</c> 結尾 ＝ 事件（相對 events/ 的路徑或唯一檔名）；
        /// 其餘 ＝ 標記（<c>name</c>、<c>name:begin</c>、<c>name:end</c>）。解不出來 ⇒ ArgumentException（marks.json 壞了 ⇒ InvalidOperationException）。
        /// </summary>
        public static int ResolveIndex(SCP_SculptPaths iPaths, List<SCP_SculptStore.EventFile> iEvents, string iSpec, string iWho)
        {
            string aSpec = iSpec.Trim();
            if (aSpec.Length == 0 || aSpec == Now) return iEvents.Count - 1;
            if (aSpec == Start) return -1;
            if (aSpec.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) return FindEvent(iEvents, aSpec, iWho);
            return IndexOf(iEvents, ResolvePoint(Read(iPaths), aSpec), iWho + " `" + aSpec + "`");
        }

        /// <summary>
        /// 加一個標記。<paramref name="iUpto"/> 空 ⇒ 標在現在（最後一個事件）；<c>start</c> ⇒ 第一個事件之前；
        /// 其餘 ⇒ 那個事件（相對 events/ 的路徑，或唯一的檔名）。同名同類已存在 ⇒ 擋下，除非 <paramref name="iOverwrite"/>。
        /// </summary>
        public static SCP_SculptMark Add(SCP_SculptPaths iPaths, string iName, string iPhase, string iUpto, string iPersona, string iNote,
                                         bool iOverwrite, DateTime iNow)
        {
            string aName = iName.Trim();
            if (aName.Length == 0) throw new ArgumentException("標記要有名字（name=）");
            if (aName.IndexOfAny(new[] { ':', ',', '|', '\n', '\r' }) >= 0) throw new ArgumentException("標記名字不可含 `:` `,` `|` 或換行（`:` 留給 name:begin 這種寫法）");
            string aPhase = iPhase.Trim().Length == 0 ? PhasePoint : iPhase.Trim();
            if (aPhase != PhasePoint && aPhase != PhaseBegin && aPhase != PhaseEnd) throw new ArgumentException("phase 要是 point|begin|end（got '" + aPhase + "'）");

            var aEvents = SCP_SculptStore.ListEvents(iPaths);
            string aRel;
            int aIdx;
            string aUpto = iUpto.Trim();
            if (aUpto.Length == 0)
            {
                if (aEvents.Count == 0) throw new ArgumentException("作品還沒有任何事件 —— 要標「開始之前」請給 upto=start");
                aIdx = aEvents.Count - 1;
                aRel = Norm(aEvents[aIdx].Rel);
            }
            else if (aUpto == Start) { aIdx = -1; aRel = ""; }
            else
            {
                aIdx = FindEvent(aEvents, aUpto, "upto");
                aRel = Norm(aEvents[aIdx].Rel);
            }

            var aFile = Read(iPaths);
            int aOld = aFile.marks.FindIndex(m => m.name == aName && m.phase == aPhase);
            if (aOld >= 0 && !iOverwrite)
                throw new ArgumentException("已經有標記 `" + aName + "`（" + aPhase + "，事件 " + (aFile.marks[aOld].@event.Length == 0 ? "start" : aFile.marks[aOld].@event)
                                            + "）—— ⛔ 不默默蓋掉；要改請加 overwrite=1");
            var aMark = new SCP_SculptMark
            {
                name = aName, phase = aPhase, @event = aRel, event_index = aIdx + 1,
                created_at = iNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture), persona = iPersona, note = iNote.Trim(),
            };
            // 區間的開始不能晚於結束（同名另一半已經在時才比得到）
            string aOther = aPhase == PhaseBegin ? PhaseEnd : aPhase == PhaseEnd ? PhaseBegin : "";
            var aPair = aOther.Length == 0 ? null : aFile.marks.Find(m => m.name == aName && m.phase == aOther);
            if (aPair != null)
            {
                int aPairIdx = IndexOf(aEvents, aPair.@event, "標記 `" + aName + "`（" + aOther + "）");
                int b = aPhase == PhaseBegin ? aIdx : aPairIdx, e = aPhase == PhaseBegin ? aPairIdx : aIdx;
                if (b > e) throw new ArgumentException("區間 `" + aName + "` 的開始晚於結束（開始第 " + (b + 1) + " 個事件、結束第 " + (e + 1) + " 個）");
            }
            if (aOld >= 0) aFile.marks[aOld] = aMark;
            else aFile.marks.Add(aMark);
            Save(iPaths, aFile);
            return aMark;
        }

        /// <summary><c>name</c>／<c>name:begin</c>／<c>name:end</c>／<c>name:point</c>／<c>start</c> ⇒ 那一刻的事件（空字串 ＝ 第一個事件之前）。</summary>
        public static string ResolvePoint(SCP_SculptMarkFile iFile, string iSpec)
        {
            string aSpec = iSpec.Trim();
            if (aSpec == Start) return "";
            string aName = aSpec, aPhase = "";
            int aColon = aSpec.LastIndexOf(':');
            if (aColon > 0) { aName = aSpec.Substring(0, aColon); aPhase = aSpec.Substring(aColon + 1); }
            var aHits = iFile.marks.FindAll(m => m.name == aName && (aPhase.Length == 0 || m.phase == aPhase));
            if (aHits.Count == 0) throw new ArgumentException("沒有標記 `" + aSpec + "`（現有：" + Names(iFile) + "）");
            if (aHits.Count > 1) throw new ArgumentException("`" + aName + "` 有開始也有結束 —— 請寫 `" + aName + ":begin` 或 `" + aName + ":end`");
            return aHits[0].@event;
        }

        /// <summary>區間 ＝ 同名的 begin 與 end；沒有 end ⇒ 到現在（還沒收尾的區間）。回傳 (開始那一刻, 結束那一刻)，開始那一刻本身不算在區間裡。</summary>
        public static (string Begin, string End) ResolveInterval(SCP_SculptMarkFile iFile, string iName)
        {
            string aName = iName.Trim();
            var aBegin = iFile.marks.Find(m => m.name == aName && m.phase == PhaseBegin);
            var aEnd = iFile.marks.Find(m => m.name == aName && m.phase == PhaseEnd);
            if (aBegin == null)
                throw new ArgumentException("`" + aName + "` 不是區間（要有 phase=begin 的標記；現有：" + Names(iFile) + "）");
            return (aBegin.@event.Length == 0 ? Start : aBegin.@event, aEnd == null ? "" : aEnd.@event);
        }

        static string Names(SCP_SculptMarkFile iFile)
        {
            if (iFile.marks.Count == 0) return "（沒有）";
            var aSb = new StringBuilder();
            foreach (var m in iFile.marks) aSb.Append(aSb.Length > 0 ? "、" : "").Append(m.name).Append(m.phase == PhasePoint ? "" : ":" + m.phase);
            return aSb.ToString();
        }
    }
}
