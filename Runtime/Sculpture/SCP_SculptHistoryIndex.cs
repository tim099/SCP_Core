// 區塊職責：雕刻空間的**歷史索引**（本機快取）—— 每個事件檔一筆：種類（一般／undo／redo／讀不出）、Undo/Redo 目標、這一刀帶的 Credit。
// 物理意義：事實源仍是 events/（append-only）；索引只是衍生物，落在空間根的 sculpt_history_cache.json（不入 git、可隨時刪）。
//          索引必須是事件清單的**前綴**：逐筆比 rel＋大小＋修改時間，第一筆對不上起全部重解析
//          （TASK-0473，Tim 2026-10-09：前 30 筆算過就只跑 30～N）。
// 數值影響：命中時零解析、只 stat —— 小木屋 87 MB／48 檔的 Credit 不再每次進頁整份解析。
// 失敗處置：索引讀不了／版本不對 ⇒ 當成空的全重算；寫不了 ⇒ 靜默（下次再算）。
//          事件讀不出照記成 unreadable（由 SCP_SculptHistory 照舊丟例外）；編輯事件欄位壞掉 ⇒ 丟例外、不存索引。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptHistoryEntry
    {
        public const string KindEvent = "event", KindUndo = "undo", KindRedo = "redo", KindUnreadable = "unreadable";
        public string rel = "", kind = "", target = "";
        public long size;
        /// <summary>修改時間（UTC ticks）存**字串**：ticks 約 6.4e17 &gt; 2^53，走數字型別讀回會經過 double 被磨掉尾數（實測 …795485 → …795520），每一筆都對不上而全重算。</summary>
        public string mtime = "";
        public List<SCP_SculptCredit> credits = new List<SCP_SculptCredit>();
    }

    public sealed class SCP_SculptHistoryIndex
    {
        public const int CurrentVersion = 2;   // 2：mtime 改存字串
        public int version = CurrentVersion;
        public List<SCP_SculptHistoryEntry> events = new List<SCP_SculptHistoryEntry>();

        /// <summary>讀索引 → 對上的前綴照用、之後的事件逐檔解析 → 有變就存。回傳與 <paramref name="iFiles"/> 一一對應。</summary>
        public static List<SCP_SculptHistoryEntry> Refresh(SCP_SculptPaths iPaths, List<SCP_SculptStore.EventFile> iFiles, out int oParsed)
        {
            SCP_SculptHistoryIndex? aOld = TryRead(iPaths.HistoryCacheFile);
            var aOut = new List<SCP_SculptHistoryEntry>(iFiles.Count);
            int k = 0;
            if (aOld != null)
                for (; k < iFiles.Count && k < aOld.events.Count; k++)
                {
                    if (!Same(aOld.events[k], iFiles[k])) break;
                    aOut.Add(aOld.events[k]);
                }
            oParsed = iFiles.Count - k;
            for (int i = k; i < iFiles.Count; i++) aOut.Add(Build(iFiles[i]));
            if (oParsed > 0 || aOld == null || aOld.events.Count != iFiles.Count)
                Save(iPaths.HistoryCacheFile, new SCP_SculptHistoryIndex { events = aOut });
            return aOut;
        }

        /// <summary>
        /// 不解析事件、只查索引：這一檔「讀得出來」嗎？索引沒有它或它已經改過 ⇒ null（呼叫端自己解析）。
        /// 給 voxel 快取比水位用 —— 水位那一檔可能是 35 MB 的大檔，只為了確認它讀得出來而整份解析不值得。
        /// </summary>
        public static bool? KnownReadable(SCP_SculptPaths iPaths, SCP_SculptStore.EventFile iFile)
        {
            SCP_SculptHistoryIndex? aIndex = TryRead(iPaths.HistoryCacheFile);
            if (aIndex == null) return null;
            foreach (var e in aIndex.events)
                if (Same(e, iFile)) return e.kind != SCP_SculptHistoryEntry.KindUnreadable;
            return null;
        }

        static bool Same(SCP_SculptHistoryEntry iEntry, SCP_SculptStore.EventFile iFile)
        {
            if (Norm(iEntry.rel) != Norm(iFile.Rel)) return false;
            Stat(iFile.Full, out long aSize, out string aTime);
            return aSize >= 0 && iEntry.size == aSize && iEntry.mtime == aTime;
        }

        static string Norm(string iRel) => iRel.Replace('\\', '/');

        static void Stat(string iFull, out long oSize, out string oTime)
        {
            var aInfo = new FileInfo(iFull);
            if (!aInfo.Exists) { oSize = -1; oTime = ""; return; }
            oSize = aInfo.Length; oTime = aInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
        }

        static SCP_SculptHistoryEntry Build(SCP_SculptStore.EventFile iFile)
        {
            // 先 stat 再讀：讀的途中檔案被改 ⇒ 記下的是舊的大小／時間，下次自然對不上而重算（不會把新內容配舊戳記）。
            Stat(iFile.Full, out long aSize, out string aTime);
            var aEntry = new SCP_SculptHistoryEntry { rel = iFile.Rel, size = aSize, mtime = aTime, kind = SCP_SculptHistoryEntry.KindEvent };
            SCP_JsonData? aEv = SCP_SculptStore.ReadEvent(iFile.Full);
            if (aEv == null) { aEntry.kind = SCP_SculptHistoryEntry.KindUnreadable; return aEntry; }
            if (aEv.GetString("op", "") != "workedit") return aEntry;
            SCP_SculptEdit aEdit = SCP_SculptHistory.ReadEdit(aEv);
            if (aEdit.action == "undo" || aEdit.action == "redo")
            {
                aEntry.kind = aEdit.action == "undo" ? SCP_SculptHistoryEntry.KindUndo : SCP_SculptHistoryEntry.KindRedo;
                aEntry.target = aEdit.target_event;
            }
            aEntry.credits = aEdit.credits;
            return aEntry;
        }

        static SCP_SculptHistoryIndex? TryRead(string iFile)
        {
            try
            {
                if (!File.Exists(iFile)) return null;
                var aIndex = new SCP_SculptHistoryIndex { version = 0 };
                var aOptions = new SCP_JsonMapOptions();
                SCP_JsonMapper.Populate(aIndex, SCP_JsonParser.Parse(File.ReadAllText(iFile, new UTF8Encoding(false)), false), aOptions);
                return aIndex.version == CurrentVersion && aOptions.Diagnostics.Count == 0 ? aIndex : null;
            }
            catch (Exception) { return null; }
        }

        static void Save(string iFile, SCP_SculptHistoryIndex iIndex)
        {
            // 觀測頁與 CLI 可能同時寫：各自用獨占的暫存檔，換檔失敗就放棄（索引是衍生物，下次再算）。
            string aTmp = iFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(iIndex), false) + "\n", new UTF8Encoding(false));
                if (File.Exists(iFile)) File.Replace(aTmp, iFile, null);
                else File.Move(aTmp, iFile);
            }
            catch (Exception) { }
            finally { try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch (Exception) { } }
        }
    }
}
