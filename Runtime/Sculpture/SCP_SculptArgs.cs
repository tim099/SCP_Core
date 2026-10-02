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
