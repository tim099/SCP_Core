// 區塊職責：作品的草稿層（TASK-0493，預雕刻）—— 先在草稿裡雕、看、undo／微調，OK 就一筆 apply 進作品，不 OK 整個丟掉。
// 物理意義：草稿 ＝ 作品資料夾底下的 `draft/`，自己就是一個空間（events／快取／歷史索引都在裡面），只是**底不是空的**：
//          底 ＝ 開草稿那一刻的作品。所以引擎的每一個寫入 op、undo／redo、觀測，拿一份「Root 指向 draft/、Base 指向底、
//          AuxRoot 指回作品」的引擎就原封不動可用（export／展品／標記／關聯檔仍落在作品資料夾，不會跟著 draft/ 被刪）。
//          ⭐ 草稿的讀寫一個位元組都不碰作品：讀作品一律走 Load(…, iSave:false)（不存快取、不建資料夾）。
// 數值影響：「底過期」照**內容**判（尺寸＋每一格的雜湊，<see cref="SCP_SculptWorks.ContentHash"/>）—— 插進較早的事件、刪事件、
//          改尺寸都抓得到；漏帶 draft=1 落了一刀再 undo 回來（內容一樣）⇒ 不算過期。
//          apply ＝ Difference(作品現況, 草稿最終) 一筆 workedit（action=draft，不設 1,000,000 格上限）⇒ undo 一次整份回到草稿前。
// 失敗處置：底過期 ⇒ apply 擋下、零寫入；base.json 壞了、draft/ 有殘留卻沒有 base.json ⇒ 擋下（⛔ 不沿用殘留 —— 幽靈刀會被帶進作品）。
//          丟掉草稿 ＝ 先把 draft/ 整個搬成 draft.trash-*（同一顆磁碟上是一步）再刪；刪不乾淨的 trash 下次再掃。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptDraftInfo
    {
        public int schema = 1;
        /// <summary>開草稿那一刻作品最後一個事件（相對 events/，正斜線；空 ＝ 作品那時還沒有事件）—— 底重建時重播到這裡。</summary>
        public string base_event = "";
        /// <summary>那時作品的事件數（給人看）。</summary>
        public int base_event_count;
        /// <summary>底的內容雜湊（<see cref="SCP_SculptWorks.ContentHash"/>）與尺寸 —— 判「底過期」只看這兩格。</summary>
        public string base_hash = "", base_dims = "";
        public string created_at = "", persona = "";
    }

    public static class SCP_SculptDraft
    {
        public const string DirName = "draft";
        public const string TrashPrefix = "draft.trash-";
        public const string ApplyAction = "draft";

        public static string Dir(SCP_SculptPaths iWork) => Path.Combine(iWork.Root, DirName);
        static string InfoFile(SCP_SculptPaths iWork) => Path.Combine(Dir(iWork), "base.json");
        static string Norm(string iRel) => iRel.Replace('\\', '/');

        public static bool IsOpen(SCP_SculptPaths iWork) => File.Exists(InfoFile(iWork));

        /// <summary>draft/ 在，卻沒有 base.json（上次刪到一半、或程序被殺）—— ⛔ 不沿用。</summary>
        public static bool HasResidue(SCP_SculptPaths iWork) => Directory.Exists(Dir(iWork)) && !IsOpen(iWork);

        /// <summary>讀 base.json；沒開 ⇒ null；壞了 ⇒ InvalidOperationException（⛔ 不當成沒開 —— 那會讓人以為可以重開一份而蓋掉它）。</summary>
        public static SCP_SculptDraftInfo? Read(SCP_SculptPaths iWork)
        {
            string aFile = InfoFile(iWork);
            if (!File.Exists(aFile)) return null;
            try
            {
                var aInfo = new SCP_SculptDraftInfo { schema = 0 };
                var aOpt = new SCP_JsonMapOptions();
                SCP_JsonMapper.Populate(aInfo, SCP_JsonParser.Parse(File.ReadAllText(aFile, new UTF8Encoding(false)), false), aOpt);
                if (aOpt.Diagnostics.Count > 0) throw new InvalidOperationException(string.Join("; ", aOpt.Diagnostics));
                if (aInfo.schema != 1 || aInfo.base_hash.Length == 0) throw new InvalidOperationException("schema=" + aInfo.schema + " base_hash=" + aInfo.base_hash);
                return aInfo;
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("草稿的 base.json 讀不了（" + e.Message + "）—— ⛔ 不猜底是哪一刻；要丟掉草稿用 sub=discard");
            }
        }

        /// <summary>作品現在（只讀，⛔ 不存快取）。</summary>
        public static SCP_SculptSpace WorkNow(SCP_SculptPaths iWork) => SCP_SculptStore.Load(iWork, false);

        /// <summary>
        /// 開草稿（已開 ⇒ 原樣回傳，不動）。底 ＝ 作品現在；記下最後一個事件、內容雜湊、尺寸。
        /// draft/ 有殘留卻沒有 base.json ⇒ InvalidOperationException（要先 discard）。
        /// </summary>
        public static SCP_SculptDraftInfo Open(SCP_SculptPaths iWork, string iDimensions, string iPersona, DateTime iNow, out bool oCreated)
        {
            oCreated = false;
            SCP_SculptDraftInfo? aOld = Read(iWork);
            if (aOld != null) return aOld;
            if (HasResidue(iWork))
                throw new InvalidOperationException("作品資料夾裡有一份殘留的 draft/（沒有 base.json：上次刪到一半或被中斷）—— ⛔ 不沿用；先 `work sub=discard` 清掉再開");
            SweepTrash(iWork);
            List<SCP_SculptStore.EventFile> aEvents = SCP_SculptStore.ListEvents(iWork);
            var aInfo = new SCP_SculptDraftInfo
            {
                base_event = aEvents.Count == 0 ? "" : Norm(aEvents[aEvents.Count - 1].Rel),
                base_event_count = aEvents.Count,
                base_hash = SCP_SculptWorks.ContentHash(WorkNow(iWork), iDimensions),
                base_dims = iDimensions,
                created_at = iNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture),
                persona = iPersona,
            };
            Directory.CreateDirectory(Dir(iWork));
            string aFile = InfoFile(iWork), aTmp = aFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(aInfo), true) + "\n", new UTF8Encoding(false));
                File.Move(aTmp, aFile);
            }
            finally { try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch (Exception) { } }
            oCreated = true;
            return aInfo;
        }

        /// <summary>
        /// 底過期了嗎：作品現在的內容（含尺寸）跟底的雜湊比。回傳 null ＝ 沒過期；否則一句人話（含底之後作品多了幾個事件，給人看）。
        /// </summary>
        public static string? Stale(SCP_SculptPaths iWork, SCP_SculptDraftInfo iInfo, string iDimensions)
        {
            if (iDimensions == iInfo.base_dims && SCP_SculptWorks.ContentHash(WorkNow(iWork), iDimensions) == iInfo.base_hash) return null;
            var aEvents = SCP_SculptStore.ListEvents(iWork);
            int aIdx = -1;
            for (int i = 0; i < aEvents.Count; i++) if (Norm(aEvents[i].Rel) == iInfo.base_event) aIdx = i;
            string aWhy = iDimensions != iInfo.base_dims ? "作品尺寸從 " + iInfo.base_dims + " 改成 " + iDimensions
                        : iInfo.base_event.Length > 0 && aIdx < 0 ? "底那個事件 `" + iInfo.base_event + "` 已經不在 events/，而且作品內容跟底不同"
                        : "開草稿之後作品的內容變了（事件 " + iInfo.base_event_count + " → " + aEvents.Count + " 個）";
            return "底過期：" + aWhy + " —— 草稿是照舊的作品雕的";
        }

        /// <summary>
        /// 草稿的空間路徑：Root ＝ draft/、AuxRoot ＝ 作品資料夾、Base ＝ 底。
        /// 底：作品現在的內容 ＝ 底 ⇒ 直接用（只讀）；否則從頭重播到 base_event，重播出來的也要對得上底的雜湊（對不上 ⇒ 丟例外，⛔ 不拿錯的底）。
        /// </summary>
        public static SCP_SculptPaths Paths(SCP_SculptPaths iWork, SCP_SculptDraftInfo iInfo, string iDimensions)
        {
            var aDraft = new SCP_SculptPaths(Dir(iWork)) { AuxRoot = iWork.Root };
            aDraft.Base = () =>
            {
                SCP_SculptSpace aNow = WorkNow(iWork);
                if (iDimensions == iInfo.base_dims && SCP_SculptWorks.ContentHash(aNow, iDimensions) == iInfo.base_hash) return aNow;
                SCP_SculptSpace aBase;
                if (iInfo.base_event.Length == 0) aBase = new SCP_SculptSpace();
                else
                {
                    var aFilter = new SCP_SculptViewFilter { UptoEvent = iInfo.base_event };
                    try { aBase = SCP_SculptStore.LoadView(iWork, aFilter); }
                    catch (ArgumentException) { throw new SCP_SculptReplayException("草稿的底 `" + iInfo.base_event + "` 已經不在作品的 events/ —— 草稿重建不出來；用 sub=discard 丟掉重開"); }
                }
                if (SCP_SculptWorks.ContentHash(aBase, iInfo.base_dims) != iInfo.base_hash)
                    throw new SCP_SculptReplayException("草稿的底重建不出來（重播到 `" + iInfo.base_event + "` 的內容跟開草稿時記的不一樣 —— 那一刻之前的事件被改過）；用 sub=discard 丟掉重開");
                return aBase;
            };
            return aDraft;
        }

        /// <summary>丟掉草稿：先把 draft/ 搬成 draft.trash-*（一步），再刪；先驗它真的在作品資料夾底下、不是連結。回傳刪掉幾個檔。</summary>
        public static int Discard(SCP_SculptPaths iWork)
        {
            string aDir = Path.GetFullPath(Dir(iWork));
            string aRoot = Path.GetFullPath(iWork.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!aDir.StartsWith(aRoot, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(aDir) != DirName)
                throw new InvalidOperationException("草稿路徑不在作品資料夾底下：" + aDir);
            int n = 0;
            if (Directory.Exists(aDir))
            {
                if ((File.GetAttributes(aDir) & FileAttributes.ReparsePoint) != 0) throw new IOException("草稿資料夾是連結，不刪：" + aDir);
                n = Directory.GetFiles(aDir, "*", SearchOption.AllDirectories).Length;
                // ① 先拿掉 base.json：之後就算資料夾搬不走、刪不乾淨，它也只是「殘留」（Open 會擋、要再 discard），⛔ 不會被當成還開著的草稿
                string aInfo = InfoFile(iWork);
                if (File.Exists(aInfo)) File.Delete(aInfo);
                // ② 整個搬走（同一顆磁碟上一步）；搬不走（有檔案被佔用）⇒ 退回就地刪；還是刪不乾淨 ⇒ 大聲說
                string aTrash = Path.Combine(aRoot, TrashPrefix + Guid.NewGuid().ToString("N").Substring(0, 8));
                try { Directory.Move(aDir, aTrash); }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    try { Directory.Delete(aDir, true); } catch (Exception) { }
                    if (Directory.Exists(aDir))
                        throw new IOException("草稿已失效（base.json 已拿掉），但資料夾刪不乾淨 —— 有檔案被佔用（觀測頁、防毒、索引？）：" + e.Message
                                              + "；關掉佔用的程式再 `work sub=discard` 一次（作品本身沒受影響）");
                }
            }
            SweepTrash(iWork);
            return n;
        }

        /// <summary>刪掉之前留下的 draft.trash-*（刪不掉就留到下次，⛔ 不擋主動作）。</summary>
        static void SweepTrash(SCP_SculptPaths iWork)
        {
            if (!Directory.Exists(iWork.Root)) return;
            foreach (string d in Directory.GetDirectories(iWork.Root, TrashPrefix + "*"))
            {
                try
                {
                    if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) != 0) continue;
                    Directory.Delete(d, true);
                }
                catch (Exception) { }
            }
        }
    }

    public sealed partial class SCP_SculptEngine
    {
        /// <summary>草稿 apply 用（TASK-0493）：作品現況 → 草稿最終的差，action=draft；Credit 由呼叫端補（草稿歷史裡 assemble 帶的）。</summary>
        public SCP_SculptEdit DraftEdit(SCP_SculptSpace iDraft) => Difference(LoadSpace(), iDraft, SCP_SculptDraft.ApplyAction);
    }
}
