// 區塊職責：交給視窗 GPU 即時畫的一幀雕刻場景（TASK-0472）—— voxel＋渲染參數，**不可變**（Put 之後不准再改）。
// 物理意義：跟 SCP_GlobeGpuScene 同一條路（SCP_GuiGpuViews 的 `gpu:`）：頁面只放場景，像素由視窗的 GL 畫。
//          網格快取以 <see cref="Voxels"/> 這個**清單物件**為鍵（＋AO／合併旗標）：頁面在作品內容沒變時沿用同一個清單物件，
//          宿主就不重建網格 —— 換視角／換光只改 uniform。
// 數值影響：Params 由頁面複製一份再改相機欄位（<see cref="SCP_SculptRenderParams.Clone"/>），⛔ 不改 plan 手上那一份。
using System.Collections.Generic;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptGpuScene
    {
        public SCP_SculptGpuScene(IReadOnlyList<SCP_SculptVoxel> iVoxels, SCP_SculptRenderParams iParams)
        {
            Voxels = iVoxels;
            Params = iParams;
        }

        public IReadOnlyList<SCP_SculptVoxel> Voxels { get; }
        public SCP_SculptRenderParams Params { get; }
    }
}
