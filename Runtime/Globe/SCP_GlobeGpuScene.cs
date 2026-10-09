// 區塊職責：交給 GPU 宿主畫的一幀球面（TASK-0470）—— 狀態＋鏡頭＋疊圖開關，**不可變**（Put 之後不准再改）。
// 物理意義：SCP_Core 不碰 GL；頁面把這一份放進 <see cref="SCP.Core.Gui.SCP_GuiGpuViews"/>，視窗宿主（Senate.Desktop）
//          在自己的 GL context 裡把它畫進貼圖。格子資料由宿主快取在 GPU 上（只上傳畫過、而且內容變了的分塊），
//          所以同一個 State 物件連畫很多幀只付一次比對 —— <see cref="StateVersion"/> 變了宿主才重新同步。
// 數值影響：疊圖的語意與 CPU ortho 預覽相同（經緯線間隔、施工區框線顏色、面接縫顏色）；GPU 能塞的施工區有上限 <see cref="MaxZones"/>，
//          超過 ⇒ 宿主回錯誤、頁面退回 CPU（⛔ 不默默只畫前幾個）。
using System.Collections.Generic;

namespace SCP.Core.Globe
{
    public sealed class SCP_GlobeGpuScene
    {
        public const int MaxZones = 64;

        public SCP_GlobeGpuScene(SCP_GlobeState iState, int iStateVersion, SCP_GlobeCamera iCamera,
                                 double iGraticule, bool iSeams, IReadOnlyList<SCP_GlobeZone> iZones)
        {
            State = iState;
            StateVersion = iStateVersion;
            Camera = iCamera;
            Graticule = iGraticule;
            Seams = iSeams;
            Zones = iZones;
        }

        public SCP_GlobeState State { get; }
        /// <summary>頁面的狀態版本（寫入成功／重新讀取就 +1）；宿主用它判斷要不要重新同步格子。</summary>
        public int StateVersion { get; }
        public SCP_GlobeCamera Camera { get; }
        /// <summary>經緯線間隔（度）；0 ＝ 不畫。</summary>
        public double Graticule { get; }
        public bool Seams { get; }
        /// <summary>要疊框線的施工區（空 ＝ 不疊）。</summary>
        public IReadOnlyList<SCP_GlobeZone> Zones { get; }
    }
}
