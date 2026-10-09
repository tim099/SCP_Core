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
            for (int i = 0; i < files.Count; i++)
            {
                var file = files[i]; var entry = entries[i];
                if (entry.kind == SCP_SculptHistoryEntry.KindUnreadable) throw new SCP_SculptReplayException("事件讀取失敗：" + file.Rel);
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
        public SCP_SculptEdit Assemble(SCP_SculptSpace iSource, int iWidth, int iHeight, int[] iAt, int iTurn, bool iOverwrite)
        {
            if (iTurn != 0 && iTurn != 90 && iTurn != 180 && iTurn != 270) throw new ArgumentException("turn需為0/90/180/270，繞Z軸旋轉");
            var current = LoadSpace(); var desired = CopySpace(current);
            foreach (var v in iSource.Voxels.Entries())
            {
                int x = v.X, y = v.Y;
                if (iTurn == 90) { x = iHeight - 1 - v.Y; y = v.X; }
                if (iTurn == 180) { x = iWidth - 1 - v.X; y = iHeight - 1 - v.Y; }
                if (iTurn == 270) { x = v.Y; y = iWidth - 1 - v.X; }
                long tx = (long)x + iAt[0], ty = (long)y + iAt[1], tz = (long)v.Z + iAt[2];
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
            var desired = iRedo ? CopySpace(current) : new SCP_SculptSpace();
            if (iRedo) SCP_SculptStore.ApplyEvent(desired, SCP_SculptStore.ReadEvent(target.Full)!, target.Rel);
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
                if (!Inside(x, y, z)) throw new ArgumentException("還原內容超出目前作品尺寸；請先擴大作品再Undo/Redo");
                if (edit.after.Count >= MaxVolume) throw new ArgumentException("單次變動超過1,000,000格；請縮小選區或拆分零件");
                edit.before.Add(new SCP_SculptChange(x, y, z, before)); edit.after.Add(new SCP_SculptChange(x, y, z, after));
            }
            foreach (var v in iCurrent.Voxels.Entries()) Visit(v.X, v.Y, v.Z);
            foreach (var v in iDesired.Voxels.Entries()) Visit(v.X, v.Y, v.Z);
            return edit;
        }
        public string CommitEdit(SCP_SculptEdit iEdit, string iPersona)
        {
            if (iEdit.after.Count == 0 && iEdit.action != "undo" && iEdit.action != "redo") throw new ArgumentException("沒有voxel變動；未新增事件");
            iEdit.persona = iPersona;
            var space = LoadSpace();
            foreach (var v in iEdit.before)
                if (space.Voxels.Get(v[0], v[1], v[2]) != v[3]) throw new InvalidOperationException("編輯前的內容已改變；未提交");
            var options = new SCP_JsonMapOptions();
            var data = SCP_JsonMapper.ToJson(iEdit, options);
            if (options.Diagnostics.Count > 0) throw new InvalidOperationException("編輯事件無法完整寫入：" + string.Join("; ", options.Diagnostics));
            var ev = new SCP_SculptPyObj().Put("op", "workedit").Put("edit", data);
            string file = SCP_SculptStore.RecordEvent(Paths, ev, Clock(), out string rel);
            foreach (var v in iEdit.after) space.Voxels.Set(v[0], v[1], v[2], v[3]);
            space.LastEventFile = rel; SCP_SculptStore.SaveCache(Paths, space);
            return file;
        }
    }
}
