// 區塊職責：雕刻各 op 的**輸入參數**（＝ python 時代引擎 argparse 的那幾組旗標，預設值照舊）。TASK-0377。
// 物理意義：python 用 argparse 把型別轉好（int／float），所以這裡**已經是 int 的就是 int**；
//          而 python 自己在 op 裡才解析的那幾格（at／facing／resize／region／axis／light_dir…）保留字串，
//          好讓錯誤訊息與 python 逐字相同（「--at 需為 'x,y,z': 1,2」那種）。
// 數值影響：預設值與 argparse 的 default 同（color 19、facing z+、thickness 1、alpha_threshold 128、exhibit_margin 2…）。
#nullable enable

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptBoxArgs
    {
        public int X1, X2, Y1, Y2, Z1, Z2;
        public int Color = 19;
        public string Persona = "";
    }

    public sealed class SCP_SculptCarveArgs
    {
        public int X1, X2, Y1, Y2, Z1, Z2;
        public string Persona = "";
    }

    /// <summary>stamp2d／stampimg 共用（python 的 parse_stamp_geometry ＋ _add_stamp_common_args）。</summary>
    public class SCP_SculptStampArgs
    {
        /// <summary>3D 錨點 "x,y,z"（python 自己解析 ⇒ 字串）。</summary>
        public string At = "";
        public string Facing = "z+";
        public int Thickness = 1;
        public bool Overwrite;
        public string Persona = "";
        /// <summary>預覽印出的非透明像素數；null ＝ 放棄這道閘門。</summary>
        public int? ExpectPixels;
        public int AlphaThreshold = 128;
        public bool AllowClip;
        public string? ExhibitId;
        public string? ExhibitTitle;
        public string? ExhibitDesc;
        public int? ExhibitMargin = 2;
    }

    public sealed class SCP_SculptStamp2dArgs : SCP_SculptStampArgs
    {
        public int SrcX1, SrcY1, SrcX2, SrcY2;
        /// <summary>傳給 canvas view 的 letters_root（空 ＝ 讓 canvas 用資料根的慣例位置）。</summary>
        public string LettersRoot = "";
    }

    public sealed class SCP_SculptStampImgArgs : SCP_SculptStampArgs
    {
        public string Png = "";
        /// <summary>"W,H"；空 ＝ 不縮放。</summary>
        public string Resize = "";
    }

    /// <summary>
    /// stampvox（TASK-0487）：3D 格子清單，每行 <c>x,y,z,color</c>（相對 <see cref="SCP_SculptStampArgs.At"/>）。
    /// 清單本身就是 3D ⇒ Facing／Thickness／AlphaThreshold 不吃；ExpectPixels ＝ 清單格數。
    /// </summary>
    public sealed class SCP_SculptStampVoxArgs : SCP_SculptStampArgs
    {
        public string Voxels = "";
    }

    /// <summary>
    /// carvevox（TASK-0492）：照清單刻 —— stampvox 的反向。清單每行 <c>x,y,z</c> 或 <c>x,y,z,color</c>（相對 <see cref="At"/>）；
    /// 帶了 color 的行是守門：那一格現在不是這個顏色 ⇒ 整刀拒絕（清單跟作品對不上，⛔ 不猜哪一邊對）。
    /// </summary>
    public sealed class SCP_SculptCarveVoxArgs
    {
        public string Voxels = "";
        public string At = "";
        public string Persona = "";
        /// <summary>清單格數；null ＝ 放棄這道閘門。</summary>
        public int? ExpectPixels;
        public bool AllowClip;
    }

    /// <summary>差異（TASK-0492）：兩個時間點之間改了哪幾格。時間點寫法見 <see cref="SCP_SculptMarks.ResolveIndex"/>。</summary>
    public sealed class SCP_SculptDiffArgs
    {
        public string From = "";
        /// <summary>空 ＝ 現在。</summary>
        public string To = "";
        /// <summary>只算這個框裡的格（x1..x2,y1..y2,z1..z2）；空 ＝ 整件。</summary>
        public string Region = "";
        /// <summary>差異清單輸出路徑（每行 x,y,z,before,after）；空 ＝ 不寫檔。</summary>
        public string Out = "";
    }

    /// <summary>stats（TASK-0492 起可選多報結構讀數）。什麼都沒給 ⇒ 與 python 時代的輸出逐字相同。</summary>
    public sealed class SCP_SculptStatsArgs
    {
        /// <summary>元件數（6 連通）＋封閉空腔。</summary>
        public bool Structure;
        /// <summary>鏡像差：<c>軸:中心</c>，例 <c>y:160</c>（以第 160 格的中心為鏡面）、<c>y:159.5</c>（159 與 160 兩格之間）。</summary>
        public string Mirror = "";
        /// <summary>結構讀數只算這個框（嚴格 x1..x2,y1..y2,z1..z2）；空 ＝ 整件。</summary>
        public string Region = "";
        /// <summary>結構讀數不算哪些顏色（c,c,…；嚴格解析）。</summary>
        public string ExcludeColor = "";
        public bool Scoped => Structure || Mirror.Length > 0 || Region.Length > 0 || ExcludeColor.Length > 0;
    }

    public sealed class SCP_SculptSliceArgs
    {
        public string Region = "";
        public string Axis = "z+";
        /// <summary>空 ＝ <c>Sculpture/_last_slice.png</c>。</summary>
        public string Out = "";
    }

    public sealed class SCP_SculptExportArgs
    {
        /// <summary>obj ／ vox。</summary>
        public string Format = "";
        public string Region = "";
        public string ExcludeColor = "";
        public string Out = "";
        public string OutDir = "";
        /// <summary>obj 的面合併：greedy（同色共面合成矩形＋頂點共用，預設）／none（逐 voxel 面，舊輸出）。vox 不吃。</summary>
        public string Merge = SCP_SculptEngine.MergeGreedy;
    }

    public sealed class SCP_SculptExhibitRegisterArgs
    {
        public string Id = "";
        public string Title = "";
        public string Author = "";
        public string Desc = "";
        public string Region = "";
        public string ExcludeColor = "";
        public string BgColor = "";
        public string Skybox = "";
        public string LightDir = "";
        public double Ambient = 0.4;
        public bool Smooth;
        public bool Shadow;
        public double? Zoom;
        /// <summary>鏡頭（選填；給了才寫進 preset —— 沒給時 preset 與 python 版逐字相同）。見 <see cref="SCP_SculptCamera"/>。</summary>
        public SCP_SculptCamera Camera = new SCP_SculptCamera();
    }

    /// <summary>
    /// 鏡頭參數（Tim 2026-10-02：可切透視）。全部是字串、空 ＝ 沒給 ⇒ 渲染器維持預設（正交、yaw 45、pitch 30、自動框住）。
    /// 展品 preset 可以帶同名 key（projection／yaw／pitch／roll／target／eye／distance／fov），**CLI 沒給的那幾格**才吃 preset。
    /// </summary>
    public sealed class SCP_SculptCamera
    {
        /// <summary>orthographic（ortho）／perspective（persp）。</summary>
        public string Projection = "";
        public string Yaw = "";
        public string Pitch = "";
        public string Roll = "";
        /// <summary>"x,y,z"。</summary>
        public string Target = "";
        /// <summary>"x,y,z"（三軸都要給，⛔ 不猜缺的那一軸）。</summary>
        public string Eye = "";
        public string Distance = "";
        public string Fov = "";

        public static readonly string[] Keys = { "projection", "yaw", "pitch", "roll", "target", "eye", "distance", "fov" };

        public string Get(string iKey)
        {
            switch (iKey)
            {
                case "projection": return Projection;
                case "yaw": return Yaw;
                case "pitch": return Pitch;
                case "roll": return Roll;
                case "target": return Target;
                case "eye": return Eye;
                case "distance": return Distance;
                case "fov": return Fov;
                default: return "";
            }
        }
    }

    /// <summary>view 的輸入（python <c>cmd_view</c> 的旗標；字串空 ＝ 沒給）。</summary>
    public sealed class SCP_SculptViewArgs
    {
        public string Region = "";
        public string ExcludeColor = "";
        public string LightDir = "";
        /// <summary>空 ＝ argparse 預設 0.4。</summary>
        public string Ambient = "";
        public bool Smooth;
        /// <summary>"1"/"true" ＝ 開；"0"/"false" ＝ 關；空 ＝ 沒給（python：沒給 ⇒ 看 preset，再沒有 ⇒ 關）。</summary>
        public string Shadow = "";
        /// <summary>空 ＝ 沒給（自動縮放）。</summary>
        public string Zoom = "";
        public string Exhibit = "";
        public SCP_SculptCamera Camera = new SCP_SculptCamera();
    }
}
