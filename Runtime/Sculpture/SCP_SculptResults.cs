// 區塊職責：雕刻各 op 的**回傳物件** —— 欄位名對著 python 時代引擎印的 JSON key，外加一支 <see cref="SCP_SculptResult.Render"/>
//          把它還原成那段文字（Cmd_Sculpture 的回傳檔附它，人讀的形狀與以前相同）。TASK-0377。
// 物理意義：python 的輸出有兩種形狀：JSON（box/carve/stamp*/slice）與人讀的文字行（stats/export/exhibit/錯誤）。
//          兩種都收進同一個基底：<see cref="SCP_SculptResult.ExitCode"/> ＝ python 的 process exit code。
// 數值影響：Render() 的換行是 <c>\n</c>、**不含**最後那個換行（python print 會多一個）。
#nullable enable
using System.Collections.Generic;

namespace SCP.Core.Sculpture
{
    /// <summary>所有 op 的回傳基底。</summary>
    public class SCP_SculptResult
    {
        /// <summary>＝ python 的 exit code（0 成功；2 參數；3 empty；4 mismatch；5 out_of_bounds；1 其他失敗）。</summary>
        public int ExitCode;

        /// <summary>python 印的 JSON 物件（有序）；文字輸出的 op 為 null。</summary>
        public SCP_SculptPyObj? Json;

        /// <summary>python 印的文字行（JSON 輸出的 op 若在 JSON 前另印了人話也放這裡）。</summary>
        public List<string> Lines = new List<string>();

        /// <summary>JSON 的 status 欄（沒有 JSON ＝ ""）。</summary>
        public string Status => Json != null && Json.TryGet("status", out object? s) && s is string a ? a : "";

        public bool Ok => ExitCode == 0;

        /// <summary>還原 python 的 stdout（文字行在前、JSON 在後；JSON 為 indent=2）。</summary>
        public virtual string Render()
        {
            var aParts = new List<string>(Lines);
            if (Json != null) aParts.Add(SCP_SculptPy.Dumps(Json, true));
            return string.Join("\n", aParts);
        }

        public static SCP_SculptResult Text(int iExit, params string[] iLines)
        {
            var a = new SCP_SculptResult { ExitCode = iExit };
            a.Lines.AddRange(iLines);
            return a;
        }

        public static SCP_SculptResult Error(int iExit, string iMessage) => Text(iExit, "❌ " + iMessage);
    }

    /// <summary>stamp 的軸映射（＝ python 的 axis_map；同時印進事件與回傳，可事後對帳）。</summary>
    public sealed class SCP_SculptAxisMap
    {
        public string U = "", V = "", Normal = "";
        public bool VFlipped;

        public SCP_SculptPyObj ToPy()
            => new SCP_SculptPyObj().Put("u", U).Put("v", V).Put("normal", Normal).Put("v_flipped", VFlipped);
    }

    public sealed class SCP_SculptBoxResult : SCP_SculptResult
    {
        public string Persona = "";
        public long TotalVolume;
        public int PlacedCount;
        public int SkippedCount;
        public string EventFile = "";
    }

    public sealed class SCP_SculptCarveResult : SCP_SculptResult
    {
        public string Persona = "";
        public int CarvedCount;
        public string EventFile = "";
    }

    /// <summary>stamp2d／stampimg（success／mismatch／empty／out_of_bounds 四種 status 共用；沒用到的欄位留預設）。</summary>
    public sealed class SCP_SculptStampResult : SCP_SculptResult
    {
        public string Op = "";
        public string Persona = "";
        public string Reason = "";
        public int? ExpectPixels;
        public int PaintedSourcePixels;
        public int RegionW, RegionH;
        public int PlacedCount;
        public int SkippedOccupied;
        public int OutOfBounds;
        public int WouldPlace;
        public int RemappedBlack;
        public int[] At = new int[3];
        public string Facing = "";
        public int Thickness;
        public SCP_SculptAxisMap? AxisMap;
        public SCP_SculptPyObj? Source;
        /// <summary>auto-exhibit 的回報（id／region／mode／title／author／file／photo／warning）；沒帶 exhibit_id ＝ null。</summary>
        public SCP_SculptPyObj? Exhibit;
        public string EventFile = "";
    }

    public sealed class SCP_SculptSliceResult : SCP_SculptResult
    {
        public string Region = "";
        public string Axis = "";
        public string Size = "";
        public int Thickness;
        public SCP_SculptAxisMap? AxisMap;
        public int NonTransparentPixels;
        public string Sha256 = "";
        public string OutputPath = "";
    }

    public sealed class SCP_SculptStatsResult : SCP_SculptResult
    {
        public int TotalVoxels;
    }

    public sealed class SCP_SculptExportResult : SCP_SculptResult
    {
        public string Format = "";
        public int VoxelCount;
        public int FaceCount;
        /// <summary>obj ⇒ .obj 路徑；vox ⇒ .vox 路徑。</summary>
        public string OutputPath = "";
        /// <summary>obj 才有。</summary>
        public string MtlPath = "";
        public int SizeX, SizeY, SizeZ;
    }

    public sealed class SCP_SculptExhibitResult : SCP_SculptResult
    {
        public string Id = "";
        public string ExhibitFile = "";
        /// <summary>出圖成功才有；沒有渲染器／渲染失敗 ⇒ 空字串，原因在 <see cref="Warning"/>。</summary>
        public string PhotoPath = "";
        public string Warning = "";
        /// <summary>list 用：依目錄列舉序的展品（id → preset）。</summary>
        public List<KeyValuePair<string, SCP.Core.Json.SCP_JsonData>> Exhibits =
            new List<KeyValuePair<string, SCP.Core.Json.SCP_JsonData>>();
    }
}
