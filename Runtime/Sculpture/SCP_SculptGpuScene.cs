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

    /// <summary>
    /// 渲染器**實際用的**鏡頭（TASK-0501）—— 畫完由 <see cref="SCP.Core.Gui.SCP_GuiGpuStatus.Result"/> 交回頁面。
    /// 物理意義：自動框住（距離／置中）很貴（透視每幀把外露 voxel 掃十幾遍）⇒ 頁面只在第一幀讓它框，之後把這幾格**釘住**照原樣送回去：
    ///          透視 ＝ TargetXYZ＋Distance、正交 ＝ TargetXYZ＋Zoom —— 兩者都給了，渲染器就不再框，畫面跟框的那一刻相同。
    /// 數值影響：座標是作品的世界座標（x,y 地面、z 朝上，與 target= 同一套）；Distance／Zoom／WorldPerImage* 都是**檢視倍率 1** 的值（倍率另外乘）。
    /// </summary>
    public sealed class SCP_SculptResolvedCamera
    {
        public SCP_SculptProjection Projection;
        public double TargetX, TargetY, TargetZ;
        /// <summary>透視：鏡頭到注視點的距離（倍率 1）。正交：0。</summary>
        public double Distance;
        /// <summary>正交：縮放（倍率 1）。透視：0。</summary>
        public double Zoom;
        /// <summary>注視點那個深度上，整張圖寬／高對應的世界長度（倍率 1）—— 頁面把「拖了幾分之一張圖」換成世界距離用。</summary>
        public double WorldPerImageWidth, WorldPerImageHeight;
        /// <summary>畫面的右方、上方與視線方向（世界座標的單位向量）—— 平移要沿著它們投到地面（俯視 90° 時視線沒有水平分量，要靠「上方」）。</summary>
        public double RightX, RightY, RightZ, UpX, UpY, UpZ, ForwardX, ForwardY, ForwardZ;
    }
}
