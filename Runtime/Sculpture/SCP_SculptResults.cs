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
        /// <summary>carvevox 才有（TASK-0492）：清單格數、清單裡現在就是空的、越界、顏色對不上、拒絕的原因、清單來源。</summary>
        public int ListCount, AlreadyEmpty, OutOfBounds, ColorMismatch;
        public string Reason = "";
        public SCP_SculptPyObj? Source;
    }

    /// <summary>兩個時間點之間的差異（TASK-0492）。</summary>
    public sealed class SCP_SculptDiffResult : SCP_SculptResult
    {
        /// <summary>events 清單裡的序號（−1 ＝ 第一個事件之前）。</summary>
        public int FromIndex, ToIndex;
        public string FromEvent = "", ToEvent = "";
        public int Placed, Carved, Recolored;
        public SortedDictionary<int, int> PlacedByColor = new SortedDictionary<int, int>();
        public SortedDictionary<int, int> CarvedByColor = new SortedDictionary<int, int>();
        public SortedDictionary<(int From, int To), int> RecoloredByPair = new SortedDictionary<(int, int), int>();
        /// <summary>區間 (from, to] 裡的事件：各 op 幾個（讀不出來的記成 <c>(讀不出來)</c>）。</summary>
        public SortedDictionary<string, int> EventOps = new SortedDictionary<string, int>(System.StringComparer.Ordinal);
        public int EventCount;
        public string OutputPath = "", Sha256 = "";
    }

    /// <summary>結構讀數的一塊（元件或空腔）：格數、外框、其中一格（字典序最小）。</summary>
    public sealed class SCP_SculptBlob
    {
        public int Size;
        public int[] Min = new int[3], Max = new int[3], Cell = new int[3];
        public override string ToString()
            => Size + " 格 @(" + Cell[0] + "," + Cell[1] + "," + Cell[2] + ") 框 " + Min[0] + ".." + Max[0] + "," + Min[1] + ".." + Max[1] + "," + Min[2] + ".." + Max[2];
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
        /// <summary>以下 TASK-0492（<see cref="SCP_SculptStatsArgs.Scoped"/> 才算）：範圍內（region／exclude_color 之後）的格數。</summary>
        public int ScopedVoxels = -1;
        /// <summary>元件（6 連通）：塊數、最大一塊、最小幾塊（由小到大）。-1 ＝ 沒算。</summary>
        public int Components = -1, LargestComponent;
        public List<SCP_SculptBlob> SmallestComponents = new List<SCP_SculptBlob>();
        /// <summary>封閉空腔（6 連通、跟外框外面不通的空格）：塊數、總格數、最大幾塊。-1 ＝ 沒算（原因在 <see cref="CavitySkipped"/>）。</summary>
        public int Cavities = -1, CavityCells;
        public List<SCP_SculptBlob> LargestCavities = new List<SCP_SculptBlob>();
        public string CavitySkipped = "";
        /// <summary>鏡像差：有格而鏡像那格空（兩側分開數）、兩邊都有但顏色不同（每對算一次）、鏡像落在 region 外而沒比的。</summary>
        public string MirrorAxis = "";
        public double MirrorPivot;
        public int MirrorMissingLow = -1, MirrorMissingHigh, MirrorColorDiff, MirrorSkipped;
        public List<string> MirrorSamples = new List<string>();
    }

    public sealed class SCP_SculptExportResult : SCP_SculptResult
    {
        public string Format = "";
        public int VoxelCount;
        public int FaceCount;
        /// <summary>obj 寫出的 `v` 行數（merge=none 不共用頂點 ⇒ 面數×4）。</summary>
        public int VertexCount;
        /// <summary>obj 用的合併方式（greedy／none）；vox 為空。</summary>
        public string Merge = "";
        /// <summary>obj ⇒ .obj 路徑；vox ⇒ .vox 路徑。</summary>
        public string OutputPath = "";
        /// <summary>obj 才有。</summary>
        public string MtlPath = "";
        /// <summary>list 才有（寫出的位元組的 sha256）。</summary>
        public string Sha256 = "";
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
