// 區塊職責：把已經落地、超過 SCP_SculptStore.MaxEventBytes 的舊 workedit 事件就地拆成頭檔＋續檔（TASK-0521）。
// 物理意義：新寫的刀由 CommitEdit 自己拆；這一支只處理拆檔之前就寫下的大檔（例：餘燼號 assemble 56.4 MB）。
//          頭檔**留原名** ⇒ undo 的 target_event、時間標記、草稿的底照舊指得到；續檔 X_p2.json… 排序緊跟在頭檔後面。
// 數值影響：每一格只落在一段裡 ⇒ 重播結果與拆之前逐格相同；不是 workedit 的大檔不拆（只回報）。
//          拆出來的段跟原檔同格式：縮排的照縮排（indent=2）、單行的寫成單行（TASK-0528 的緊湊格式）——
//          單行的舊檔照 indent=2 重寫會從 56 MB 膨脹成 150 MB。
// 失敗處置：預設只試算（一個位元組都不寫）；續檔名已存在、讀不出、欄位壞掉 ⇒ 那一檔不拆、照實回報。
#nullable enable
using System.Collections.Generic;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    /// <summary>一個超過上限的事件檔、以及拆它的結果。<see cref="Skip"/> 非空 ⇒ 沒拆，原因在裡面。</summary>
    public sealed class SCP_SculptSplitItem
    {
        public string Rel = "", Skip = "";
        public long Bytes;
        public int Parts;
        public List<long> PartBytes = new List<long>();
    }

    public sealed partial class SCP_SculptEngine
    {
        /// <summary>找出本作品 events/ 裡超過上限的事件；<paramref name="iApply"/> ＝ true 才真的改寫（呼叫端要握作品鎖）。</summary>
        public List<SCP_SculptSplitItem> SplitOversizeEvents(bool iApply)
        {
            var aOut = new List<SCP_SculptSplitItem>();
            var aAll = SCP_SculptStore.ListEvents(Paths);
            for (int i = 0; i < aAll.Count; i++)
            {
                long aBytes = new FileInfo(aAll[i].Full).Length;
                if (aBytes <= SCP_SculptStore.MaxEventBytes) continue;
                var aItem = new SCP_SculptSplitItem { Rel = aAll[i].Rel, Bytes = aBytes };
                aOut.Add(aItem);
                if (SCP_SculptStore.PartHead(aAll[i].Rel) != null) { aItem.Skip = "它本身是續檔（不該超過上限 —— 照實回報，不再拆）"; continue; }
                if (SCP_SculptStore.LastPartIndex(aAll, i) != i) { aItem.Skip = "後面已經有它的續檔 —— 不疊著拆"; continue; }
                SCP_JsonData? aEv = SCP_SculptStore.ReadEvent(aAll[i].Full);
                if (aEv == null) { aItem.Skip = "讀不出來"; continue; }
                string aOp = aEv.GetString("op", "");
                if (aOp != "workedit") { aItem.Skip = "op=" + aOp + " 不是 workedit（這一支只拆 workedit）"; continue; }
                SCP_SculptEdit aEdit;
                try { aEdit = SCP_SculptHistory.ReadEdit(aEv); }
                catch (SCP_SculptReplayException e) { aItem.Skip = e.Message; continue; }
                // 最上層其他欄位（source…）照原樣留在頭檔；edit 由拆檔重寫
                var aTop = new SCP_SculptPyObj();
                foreach (string k in aEv.Keys) if (k != "edit") aTop.Put(k, aEv[k]);
                var aFiles = EventFiles(aEdit, null, aTop, aAll[i].Rel, Indented(aAll[i].Full));
                if (aFiles.Count == 1) { aItem.Skip = "重寫之後一個檔就放得下 —— 不拆"; continue; }
                aItem.Parts = aFiles.Count;
                foreach (var f in aFiles) aItem.PartBytes.Add(SCP_SculptStore.DiskBytes(f.Text));
                bool aClash = false;
                for (int p = 1; p < aFiles.Count; p++)
                    if (File.Exists(Path.Combine(Paths.Events, aFiles[p].Rel))) { aItem.Skip = "續檔名 " + aFiles[p].Rel + " 已經存在 —— 不蓋"; aClash = true; break; }
                if (aClash || !iApply) continue;
                SCP_SculptStore.WriteEvents(Paths, aFiles);
            }
            return aOut;
        }

        /// <summary>原檔是縮排的嗎（開頭 <c>{</c> 之後緊接換行）—— 單行的（舊工具寫的）拆出來也寫單行。</summary>
        static bool Indented(string iFull)
        {
            using var aIn = File.OpenRead(iFull);
            int a = aIn.ReadByte(), b = aIn.ReadByte();
            return a == '{' && (b == 0x0A || b == 0x0D);
        }
    }
}
