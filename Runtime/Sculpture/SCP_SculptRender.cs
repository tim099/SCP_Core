// 區塊職責：雕刻渲染的**介面**（資料層只交「場景＋參數」，怎麼畫是宿主的事）—— TASK-0377。
// 物理意義：SCP_Core 也被 Unity 編譯、不准引用任何套件（csproj 的 PackageReference 守衛），
//          所以 GPU（OpenGL）的實作住在 Senate.Desktop，由宿主啟動時 Register 進來。
//          ⭐ 引擎出圖（`senate cmd sculpture op=view`、落子後的分享圖）與 Senate 觀測頁的預覽**走同一個實作**
//          （Tim 2026-10-02：「跟頁面預覽走同一套渲染」）—— 觀測頁不自己畫，它叫同一支 Cmd。
// 數值影響：沒有註冊渲染器（例：Senate.Server 不引用 Desktop）⇒ <see cref="SCP_SculptRenderers.Current"/> 是 null，
//          呼叫端必須**大聲說**「這條路徑沒有 GPU 渲染器」，⛔ 不准退成一張空白圖或舊圖。
// 決定性：同一台機器、同一張顯卡與驅動 ⇒ 同輸入同圖；⚠ 換顯卡／驅動不保證逐位元相同（GPU 光柵化的本質）。
using System.Collections.Generic;

namespace SCP.Core.Sculpture
{
    /// <summary>一顆要畫的 voxel（顏色是 RGB332 調色盤 index，0 ＝ 空，不會出現在場景裡）。</summary>
    public readonly struct SCP_SculptVoxel
    {
        public SCP_SculptVoxel(int iX, int iY, int iZ, byte iColor) { X = iX; Y = iY; Z = iZ; Color = iColor; }
        public int X { get; }
        public int Y { get; }
        public int Z { get; }
        public byte Color { get; }
    }

    /// <summary>
    /// 渲染參數。座標系與舊引擎相同：x、y 為水平、z 朝上，空間 0..255。
    /// <para>預設鏡頭＝舊引擎的 2:1 等角視角（從 +x+y 方向往 −x−y 看、俯角約 30°），自動框住所有 voxel。</para>
    /// </summary>
    public sealed class SCP_SculptRenderParams
    {
        public int Width = 1024;
        public int Height = 1024;
        /// <summary>場景的實際邊長（由引擎給）；全格地板使用它，不由渲染設定覆寫。</summary>
        public int SpaceSize = 256;
        /// <summary>
        /// 光源（Tim 2026-10-02：可以多組）。空清單 ＝ 只有環境光。預設一盞：方向 -1,-1,-1、白光、強度 1、投陰影。
        /// <para>⚠ 投陰影的光最多 <see cref="MaxShadowLights"/> 盞，超過 ⇒ TryRender 回 false（⛔ 不默默只取前幾盞）。</para>
        /// </summary>
        public List<SCP_SculptLight> Lights = new List<SCP_SculptLight> { new SCP_SculptLight() };
        public const int MaxLights = 8;
        public const int MaxShadowLights = 2;
        /// <summary>環境光比例 0..1。</summary>
        public double Ambient = 0.4;
        /// <summary>陰影總開關（關 ⇒ 每盞光的 CastShadow 都不生效）。新引擎預設開。</summary>
        public bool Shadow = true;
        /// <summary>環境遮蔽（逐頂點 AO）。</summary>
        public bool AmbientOcclusion = true;
        /// <summary>null ＝ 自動框住；有值 ＝ 每 voxel 寬 2×12×Zoom 像素（與舊引擎 zoom 同刻度）。</summary>
        public double? Zoom;
        /// <summary>
        /// 自動框住（Zoom＝null）時可不可以**放大**超過每 voxel 24 px（Tim 2026-10-02）：
        /// false ＝ 舊行為（只縮不放 —— 全景圖不會因為空間很空就把零星幾顆放到巨大）；
        /// true ＝ 主體一律填滿約 92% 畫面（看展品／指定 region 時用：10 格寬的小作品不該縮在正中央一小點）。
        /// <para>只影響正交；透視本來就由距離框住（Zoom 不適用）。</para>
        /// </summary>
        public bool FitUpscale = false;
        /// <summary>投影模式（Tim 2026-10-02 擴充：可切透視）。</summary>
        public SCP_SculptProjection Projection = SCP_SculptProjection.Orthographic;
        /// <summary>鏡頭水平角（度）；45 ＝ 舊引擎的預設視角（從 +x+y 往 −x−y 看）。</summary>
        public double YawDeg = 45;
        /// <summary>鏡頭俯角（度）；舊引擎 2:1 等角 ≈ 30。</summary>
        public double PitchDeg = 30;
        /// <summary>鏡頭繞視線的滾轉（度），預設 0。</summary>
        public double RollDeg = 0;
        /// <summary>注視點（世界座標，voxel 單位）；null ＝ 可見 voxel 外框的中心。</summary>
        public double? TargetX, TargetY, TargetZ;
        /// <summary>
        /// 鏡頭位置（世界座標）；三個都給 ⇒ **覆蓋** Yaw／Pitch／Distance（鏡頭直接放在這裡、看向注視點）。
        /// 只給一部分 ⇒ 視為參數錯誤（實作回 false，⛔ 不猜缺的那一軸）。
        /// </summary>
        public double? EyeX, EyeY, EyeZ;
        /// <summary>透視模式下鏡頭到注視點的距離（voxel 單位）；null ＝ 依 Fov 自動框住全部可見 voxel。</summary>
        public double? Distance;
        /// <summary>透視模式的垂直視角（度）。</summary>
        public double FovDeg = 45;
        /// <summary>背景色（舊引擎 (15,23,42)）—— 只在 <see cref="Skybox"/> ＝ <see cref="SkyboxNone"/> 時使用。</summary>
        public byte BgR = 15, BgG = 23, BgB = 42;
        /// <summary>
        /// Skybox（Tim 2026-10-02）：等距柱狀全景圖（2:1，PNG／JPG）的**絕對路徑**；
        /// null ＝ 渲染器內建的預設天空；<see cref="SkyboxNone"/> ＝ 不用 skybox、純色 Bg。
        /// <para>⚠ 路徑給了但讀不了 ⇒ TryRender 回 false（⛔ 不默默退回預設天空 —— 那會讓「我換了」與「沒換成」同形）。</para>
        /// </summary>
        public string? Skybox;
        public const string SkyboxNone = "none";
        /// <summary>Skybox 的水平旋轉（度），讓天空的方位可以對齊作品。</summary>
        public double SkyboxYawDeg = 0;
        /// <summary>
        /// 正交鏡頭的背景視窗額外**往上**抬幾度（−89..89；Tim 2026-10-02：銀河在仰角 20–70°，預設視窗只看到地平線附近）。
        /// 正交的視線全平行，背景是渲染器另開的「假透視」視窗（垂直 70°、俯角取鏡頭的 1/4）⇒ 這一格只調那個視窗。
        /// <para>透視模式忽略（天空是真的視線，鏡頭往哪看就是哪）。</para>
        /// </summary>
        public double SkyboxTiltDeg = 0;
        /// <summary>地板（Tim 2026-10-02）。null ＝ 沒有地板（舊行為）。</summary>
        public SCP_SculptFloor? Floor;
    }

    /// <summary>一盞平行光。</summary>
    public sealed class SCP_SculptLight
    {
        /// <summary>光**行進**方向（與舊引擎 `--light-dir` 同語意：-1,-1,-1 ＝ 從 +x+y+z 照下來）。</summary>
        public double DirX = -1, DirY = -1, DirZ = -1;
        /// <summary>光色（0..255）。</summary>
        public byte R = 255, G = 255, B = 255;
        /// <summary>強度（≥ 0；1 ＝ 正對光源的面剛好是調色盤原色）。</summary>
        public double Intensity = 1;
        /// <summary>這盞光投不投陰影（受 <see cref="SCP_SculptRenderParams.Shadow"/> 總開關管）。</summary>
        public bool CastShadow = true;
    }

    /// <summary>
    /// 地板：一片水平面（接陰影、邊緣淡出到背景）。
    /// <para>⚠ 貼圖路徑給了但讀不了 ⇒ TryRender 回 false（⛔ 不默默換成內建網格）。</para>
    /// </summary>
    public sealed class SCP_SculptFloor
    {
        /// <summary>地板面的高度（世界 z）；0 ＝ voxel 空間的底面。</summary>
        public double Z = 0;
        /// <summary>範圍：true ＝ 整個 0..256 空間；false ＝ 可見 voxel 外框向外擴 <see cref="Margin"/> 格。</summary>
        public bool FullGrid = false;
        /// <summary>外框模式向外擴幾格的**上限**（<see cref="MarginRatio"/> ＝ 0 時就是固定外擴量）。</summary>
        public double Margin = 24;
        /// <summary>
        /// 外框模式的外擴 ＝ 作品 xy 外框最長邊 × 本值，封頂 <see cref="Margin"/>（Tim 2026-10-02：小展品不該被 24 格地板鋪滿整個畫面）。
        /// 0 ＝ 固定外擴 <see cref="Margin"/> 格（舊行為）。
        /// </summary>
        public double MarginRatio = 0.5;

        /// <summary>給定作品 xy 外框最長邊，實際外擴幾格。</summary>
        public double EffectiveMargin(double iExtent)
            => MarginRatio > 0 ? System.Math.Min(Margin, MarginRatio * iExtent) : Margin;
        /// <summary>貼圖絕對路徑（PNG／JPG，可重複鋪）；null ＝ 內建量尺網格（每 1／16／64 格）。</summary>
        public string? Texture;
        /// <summary>貼圖每重複一次涵蓋幾格（voxel）；內建網格忽略（它天生對齊 1 格）。</summary>
        public double TileSize = 16;
        /// <summary>色調（乘在貼圖上，0..255）。</summary>
        public byte R = 255, G = 255, B = 255;
        /// <summary>邊緣淡出寬度（佔範圍的比例 0..0.5）；0 ＝ 硬邊。</summary>
        public double Fade = 0.15;
    }

    public enum SCP_SculptProjection
    {
        /// <summary>正交（舊引擎的等角圖屬於這一類）；縮放走 <see cref="SCP_SculptRenderParams.Zoom"/>。</summary>
        Orthographic,
        /// <summary>透視；縮放走 Distance／FovDeg（Zoom 不適用）。</summary>
        Perspective,
    }

    public interface ISCP_SculptRenderer
    {
        /// <summary>給人看的名字（印在輸出裡，讓讀的人知道這張圖是哪一條路畫的）。</summary>
        string Name { get; }

        /// <summary>
        /// 畫一張圖。成功 ⇒ <paramref name="oRgba"/> 是 Width×Height×4、**由上到下**逐列的 RGBA8。
        /// 失敗 ⇒ false 與一句人話（⛔ 不丟例外給呼叫端吞）。
        /// </summary>
        bool TryRender(IReadOnlyList<SCP_SculptVoxel> iVoxels, SCP_SculptRenderParams iParams,
                       out byte[] oRgba, out string oError);
    }

    /// <summary>宿主在啟動時註冊實作；沒註冊 ＝ null（見檔頭）。</summary>
    public static class SCP_SculptRenderers
    {
        public static ISCP_SculptRenderer? Current { get; private set; }

        public static void Register(ISCP_SculptRenderer iRenderer) { Current = iRenderer; }
    }
}
