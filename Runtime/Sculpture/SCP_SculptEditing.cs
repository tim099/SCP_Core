// 區塊職責：作品零件組裝、區域移動、Undo/Redo與來源Credit；宿主握鎖後呼叫。
// 物理意義：每刀保存完整前後差異與來源快照，重播不依賴零件當下狀態。
// 數值影響：每刀最多1,000,000個變動格；碰撞預設拒絕，覆蓋需顯式指定。
#nullable enable
using System;
using System.Collections.Generic;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptCredit
    {
        public string work = "", author = "", title = "", revision = "";
    }
    public sealed class SCP_SculptChange
    {
        public int x, y, z, color;
        public SCP_SculptChange() { }
        public SCP_SculptChange(int iX, int iY, int iZ, int iColor) { x = iX; y = iY; z = iZ; color = iColor; }
        public int this[int index] => index == 0 ? x : index == 1 ? y : index == 2 ? z : index == 3 ? color : throw new IndexOutOfRangeException();
    }
    public sealed class SCP_SculptEdit
    {
        public string action = "", target_event = "", persona = "";
        public List<SCP_SculptChange> before = new List<SCP_SculptChange>(), after = new List<SCP_SculptChange>();
        public List<SCP_SculptCredit> credits = new List<SCP_SculptCredit>();
    }
    public sealed class SCP_SculptHistory
    {
        public List<SCP_SculptStore.EventFile> Active = new List<SCP_SculptStore.EventFile>();
        public List<SCP_SculptStore.EventFile> Redo = new List<SCP_SculptStore.EventFile>();
        public static SCP_SculptEdit ReadEdit(SCP_JsonData iEvent)
        {
            var result = new SCP_SculptEdit();
            var options = new SCP_JsonMapOptions();
            SCP_JsonMapper.Populate(result, iEvent["edit"], options);
            if (options.Diagnostics.Count > 0) throw new SCP_SculptReplayException("編輯事件不合法：" + string.Join("; ", options.Diagnostics));
            return result;
        }
        /// <summary>這一趟實際解析了幾個事件檔（其餘取自歷史索引）。</summary>
        public int Parsed;
        readonly Dictionary<string, List<SCP_SculptCredit>> m_Credits = new Dictionary<string, List<SCP_SculptCredit>>(StringComparer.Ordinal);
        /// <summary>Undo/Redo 堆疊與每刀 Credit 取自歷史索引（<see cref="SCP_SculptHistoryIndex"/>）：只解析索引之後新增或改過的事件。</summary>
        public static SCP_SculptHistory Read(SCP_SculptPaths iPaths)
        {
            var result = new SCP_SculptHistory();
            var files = SCP_SculptStore.ListEvents(iPaths);
            var entries = SCP_SculptHistoryIndex.Refresh(iPaths, files, out result.Parsed);
            string head = "";
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i]; var entry = entries[i];
                if (entry.kind == SCP_SculptHistoryEntry.KindUnreadable) throw new SCP_SculptReplayException("事件讀取失敗：" + file.Rel);
                if (head.Length > 0 && SCP_SculptStore.IsPartOf(file.Rel, head)) continue;   // 續檔：同一刀的後半（TASK-0521），不另算一筆
                head = file.Rel;
                if (entry.kind == SCP_SculptHistoryEntry.KindUndo || entry.kind == SCP_SculptHistoryEntry.KindRedo)
                {
                    bool undo = entry.kind == SCP_SculptHistoryEntry.KindUndo;
                    var from = undo ? result.Active : result.Redo;
                    var to = undo ? result.Redo : result.Active;
                    if (from.Count == 0 || from[from.Count - 1].Rel.Replace('\\', '/') != entry.target.Replace('\\', '/'))
                        throw new SCP_SculptReplayException("Undo/Redo事件順序不合法：" + file.Rel);
                    to.Add(from[from.Count - 1]); from.RemoveAt(from.Count - 1);
                }
                else { result.Active.Add(file); result.Redo.Clear(); }
                result.m_Credits[file.Rel] = entry.credits;
            }
            return result;
        }
        public List<SCP_SculptCredit> Credits()
        {
            var result = new List<SCP_SculptCredit>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in Active)
                if (m_Credits.TryGetValue(file.Rel, out var credits))
                    foreach (var credit in credits)
                        if (seen.Add(credit.work + "|" + credit.author + "|" + credit.revision)) result.Add(credit);
            return result;
        }
    }
    public sealed partial class SCP_SculptEngine
    {
        bool Inside(int x, int y, int z) => x >= 0 && x < SizeX && y >= 0 && y < SizeY && z >= 0 && z < SizeZ;
        static SCP_SculptSpace CopySpace(SCP_SculptSpace iSpace)
        {
            var copy = new SCP_SculptSpace();
            foreach (var v in iSpace.Voxels.Entries()) copy.Voxels.Set(v.X, v.Y, v.Z, v.Color);
            return copy;
        }
        /// <summary>
        /// 放入零件副本。<paramref name="iMirror"/>（TASK-0497）：正規化過的鏡像軸（""／x／y／z 組合），在零件自己的框（W×H×D）裡先翻再轉 ——
        /// 公式與關聯共用 <see cref="SCP_SculptLinks.Transform"/>。
        /// ⚠ 事件記的是完整前後差異，重播不重算這裡 ⇒ 改公式不會動到舊事件的重播結果。
        /// </summary>
        public SCP_SculptEdit Assemble(SCP_SculptSpace iSource, int iWidth, int iHeight, int iDepth, int[] iAt, int iTurn, string iMirror, bool iOverwrite)
        {
            if (iTurn != 0 && iTurn != 90 && iTurn != 180 && iTurn != 270) throw new ArgumentException("turn需為0/90/180/270，繞Z軸旋轉");
            if (SCP_SculptLinks.NormalizeMirror(iMirror, out string mirror) != null || mirror != iMirror) throw new ArgumentException("mirror需為x/y/z或組合（已正規化）：" + iMirror);
            var current = LoadSpace(); var desired = CopySpace(current);
            foreach (var v in iSource.Voxels.Entries())
            {
                SCP_SculptLinks.Transform(mirror, iTurn, iWidth, iHeight, iDepth, v.X, v.Y, v.Z, out int x, out int y, out int z0);
                long tx = (long)x + iAt[0], ty = (long)y + iAt[1], tz = (long)z0 + iAt[2];
                if (tx < 0 || tx >= SizeX || ty < 0 || ty >= SizeY || tz < 0 || tz >= SizeZ) throw new ArgumentException("零件越界；未放入任何voxel");
                x = (int)tx; y = (int)ty; int z = (int)tz;
                if (!iOverwrite && current.Voxels.Get(x, y, z) != 0) throw new ArgumentException("零件碰到既有voxel；未修改。需要覆蓋時顯式overwrite=1");
                desired.Voxels.Set(x, y, z, v.Color);
            }
            return Difference(current, desired, "assemble");
        }
        public SCP_SculptEdit Move(int[] iRegion, int[] iDelta, bool iOverwrite)
        {
            if (!Inside(iRegion[0], iRegion[2], iRegion[4]) || !Inside(iRegion[1], iRegion[3], iRegion[5])) throw new ArgumentException("region超出作品尺寸");
            var current = LoadSpace(); var desired = CopySpace(current);
            var selected = new List<int[]>();
            foreach (var v in current.Voxels.Entries())
                if (v.X >= iRegion[0] && v.X <= iRegion[1] && v.Y >= iRegion[2] && v.Y <= iRegion[3] && v.Z >= iRegion[4] && v.Z <= iRegion[5])
                    selected.Add(new[] { v.X, v.Y, v.Z, v.Color });
            foreach (var v in selected) desired.Voxels.Set(v[0], v[1], v[2], 0);
            // 所有來源先移開，因此選區與目的地重疊時仍是整體平移，不會邊搬邊擦。
            foreach (var v in selected)
            {
                long x = (long)v[0] + iDelta[0], y = (long)v[1] + iDelta[1], z = (long)v[2] + iDelta[2];
                if (x < 0 || x >= SizeX || y < 0 || y >= SizeY || z < 0 || z >= SizeZ) throw new ArgumentException("移動後越界；未修改");
                if (!iOverwrite && desired.Voxels.Get((int)x, (int)y, (int)z) != 0) throw new ArgumentException("移動碰到選區外的voxel；未修改。需要覆蓋時顯式overwrite=1");
                desired.Voxels.Set((int)x, (int)y, (int)z, v[3]);
            }
            return Difference(current, desired, "move");
        }
        public SCP_SculptEdit UndoRedo(bool iRedo)
        {
            var history = SCP_SculptHistory.Read(Paths);
            var candidates = iRedo ? history.Redo : history.Active;
            if (candidates.Count == 0) throw new ArgumentException(iRedo ? "沒有可Redo的編輯" : "沒有可Undo的編輯");
            var target = candidates[candidates.Count - 1];
            var current = LoadSpace();
            var desired = iRedo ? CopySpace(current) : Paths.NewStart();   // 草稿 ⇒ 從底起重播（TASK-0493）
            if (iRedo)
            {
                // 頭檔連同續檔一起套（TASK-0521）；Undo 那一支在頭檔就停 ⇒ 續檔天生不在裡面
                var all = SCP_SculptStore.ListEvents(Paths);
                int at = all.FindIndex(f => f.Rel == target.Rel);
                if (at < 0) throw new SCP_SculptReplayException("Redo 的目標不在 events/：" + target.Rel);
                for (int i = at; i <= SCP_SculptStore.LastPartIndex(all, at); i++)
                {
                    var ev = SCP_SculptStore.ReadEvent(all[i].Full);
                    if (ev == null) throw new SCP_SculptReplayException("事件讀取失敗：" + all[i].Rel);
                    SCP_SculptStore.ApplyEvent(desired, ev, all[i].Rel);
                }
            }
            else foreach (var file in SCP_SculptStore.ListEvents(Paths))
            {
                if (file.Rel == target.Rel) break;
                var ev = SCP_SculptStore.ReadEvent(file.Full);
                if (ev == null) throw new SCP_SculptReplayException("事件讀取失敗：" + file.Rel);
                SCP_SculptStore.ApplyEvent(desired, ev, file.Rel);
            }
            var edit = Difference(current, desired, iRedo ? "redo" : "undo");
            edit.target_event = target.Rel;
            return edit;
        }
        SCP_SculptEdit Difference(SCP_SculptSpace iCurrent, SCP_SculptSpace iDesired, string iAction)
        {
            var edit = new SCP_SculptEdit { action = iAction };
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Visit(int x, int y, int z)
            {
                if (!seen.Add(SCP_SculptVoxelMap.KeyText(x, y, z))) return;
                int before = iCurrent.Voxels.Get(x, y, z), after = iDesired.Voxels.Get(x, y, z);
                if (before == after) return;
                if (!Inside(x, y, z))
                    throw new ArgumentException(iAction == SCP_SculptDraft.ApplyAction ? "草稿有格子在作品現在的尺寸之外（作品被縮小過？）；未套用"
                                                                                       : "還原內容超出目前作品尺寸；請先擴大作品再Undo/Redo");
                // 草稿 apply 不設上限（TASK-0493）：草稿是很多刀累積的，拆不開；重播 workedit 本來就沒有上限
                if (edit.after.Count >= MaxVolume && iAction != SCP_SculptDraft.ApplyAction) throw new ArgumentException("單次變動超過1,000,000格；請縮小選區或拆分零件");
                edit.before.Add(new SCP_SculptChange(x, y, z, before)); edit.after.Add(new SCP_SculptChange(x, y, z, after));
            }
            foreach (var v in iCurrent.Voxels.Entries()) Visit(v.X, v.Y, v.Z);
            foreach (var v in iDesired.Voxels.Entries()) Visit(v.X, v.Y, v.Z);
            return edit;
        }
        /// <summary>
        /// 落一筆 workedit。<paramref name="iSource"/>（選填）寫在事件**最上層**、不進 edit ——
        /// 重播與歷史索引只讀 edit（舊版對 edit 裡不認得的鍵會報錯），所以多帶的說明不會讓沒更新的 senate.exe 重播失敗（TASK-0495 噴漆）。
        /// </summary>
        public string CommitEdit(SCP_SculptEdit iEdit, string iPersona, SCP_SculptPyObj? iSource = null)
        {
            if (iEdit.after.Count == 0 && iEdit.action != "undo" && iEdit.action != "redo") throw new ArgumentException("沒有voxel變動；未新增事件");
            iEdit.persona = iPersona;
            var space = LoadSpace();
            foreach (var v in iEdit.before)
                if (space.Voxels.Get(v[0], v[1], v[2]) != v[3]) throw new InvalidOperationException("編輯前的內容已改變；未提交");
            string headRel = SCP_SculptStore.NewEventRel(Paths, Clock());
            var files = EventFiles(iEdit, iSource, null, headRel);
            string file = SCP_SculptStore.WriteEvents(Paths, files);
            LastParts = files.Count;
            foreach (var v in iEdit.after) space.Voxels.Set(v[0], v[1], v[2], v[3]);
            space.LastEventFile = files[files.Count - 1].Rel; SCP_SculptStore.SaveCache(Paths, space);
            return file;
        }

        /// <summary>上一次 <see cref="CommitEdit"/> 寫了幾個檔（1 ＝ 沒拆；TASK-0521）。</summary>
        public int LastParts { get; private set; }

        /// <summary>
        /// 一刀 ⇒ 要落的檔（rel, 全文）（TASK-0521）：整份寫得下就一個檔；超過 <see cref="SCP_SculptStore.MaxEventBytes"/>
        /// 就把 before／after 照同一個順序切段 —— 頭檔留這一刀的 action／target_event／credits／source，最上層多一個 <c>parts</c>；
        /// 續檔的 action 是 <c>part</c>、最上層帶 <c>part_of</c>（頭檔）與 <c>part</c>（第幾段）。
        /// <para>⭐ 每一格只落在一段裡 ⇒ 依序重播各段 ＝ 重播整刀；沒更新的 senate.exe 也照樣重播得出來（只讀 edit.after）。</para>
        /// <para><paramref name="iTop"/>：拆舊檔時原事件最上層的其他欄位（照原樣留在頭檔）；新寫的刀給 null。</para>
        /// <para><paramref name="iIndent"/>：預設 false ＝ 單行（新寫的刀一律單行，TASK-0528）；true ＝ indent=2 —— 只給拆舊檔用：跟原檔同格式。</para>
        /// </summary>
        public static List<(string Rel, string Text)> EventFiles(SCP_SculptEdit iEdit, SCP_SculptPyObj? iSource, SCP_SculptPyObj? iTop, string iHeadRel, bool iIndent = false)
        {
            int total = iEdit.after.Count, per = Math.Max(1, total);
            while (true)
            {
                int parts = total == 0 ? 1 : (total + per - 1) / per;
                var outFiles = new List<(string Rel, string Text)>(parts);
                bool fits = true;
                for (int p = 0; p < parts && fits; p++)
                {
                    var piece = new SCP_SculptEdit
                    {
                        action = p == 0 ? iEdit.action : PartAction, target_event = p == 0 ? iEdit.target_event : "", persona = iEdit.persona,
                        before = iEdit.before.GetRange(p * per, Math.Min(per, total - p * per)),
                        after = iEdit.after.GetRange(p * per, Math.Min(per, total - p * per)),
                        credits = p == 0 ? iEdit.credits : new List<SCP_SculptCredit>(),
                    };
                    var options = new SCP_JsonMapOptions();
                    var data = SCP_JsonMapper.ToJson(piece, options);
                    if (options.Diagnostics.Count > 0) throw new InvalidOperationException("編輯事件無法完整寫入：" + string.Join("; ", options.Diagnostics));
                    var ev = new SCP_SculptPyObj();
                    if (p == 0 && iTop != null) foreach (var kv in iTop) ev.Put(kv.Key, kv.Value);
                    ev.Put("op", "workedit");
                    if (p == 0 && iSource != null) ev.Put("source", iSource);
                    ev.Put("edit", data);
                    string rel = p == 0 ? iHeadRel : SCP_SculptStore.PartRel(iHeadRel, p + 1);
                    if (p == 0) { if (parts > 1) ev.Put("parts", parts); else ev.RemoveAll(kv => kv.Key == "parts"); }
                    else ev.Put("part_of", iHeadRel.Replace('\\', '/')).Put("part", p + 1);
                    string text = iIndent ? SCP_SculptPy.Dumps(ev, true) : SCP_SculptPy.DumpsCompact(ev);
                    if (SCP_SculptStore.DiskBytes(text) > SCP_SculptStore.MaxEventBytes && per > 1) fits = false;
                    else outFiles.Add((rel, text));
                }
                if (fits) return outFiles;
                per = (per + 1) / 2;
            }
        }

        /// <summary>續檔的 edit.action（TASK-0521）。⛔ 不沿用頭檔的 action：沒更新的 senate.exe 會把 undo 的續檔當成第二次 undo 而整份歷史讀不了。</summary>
        public const string PartAction = "part";
    }
}
