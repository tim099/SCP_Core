// 區塊職責：畫布**尺寸**（TASK-0445）—— 設定檔、已畫範圍、實際尺寸，三個數各說各的。
// 物理意義：尺寸原本是編譯期常數 2048×2048。改成參數化之後有一個必須守住的不變式：
//           **已經畫上去的點不會因為畫布變小而消失**（Tim 2026-10-06「避免操作失誤」）。
//           🩸 舊的重播（`SCP_CanvasEvents.Apply`）把範圍外的事件**直接略過** ⇒ 只要尺寸設小，
//              點還在 events/ 裡、畫面上卻不見，而且沒有任何一層會叫。
//           ⇒ 實際尺寸 ＝ max(設定值, 所有事件的已畫範圍)。設定只能把畫布「撐大」，縮不進已畫的點。
//           寫入端（`canvas op=size`）另外擋「設到已畫範圍以下」—— 那是第二道，第一道是上面那個 max。
// 數值影響：設定檔 `<資料根>/Canvas/canvas_settings.json`（`{"width":W,"height":H}`）；不存在 ⇒ 2048×2048（同舊行為）。
//           已畫範圍 ＝ 所有事件裡合法像素的 max(x)+1 × max(y)+1（座標 ≥ MaxSide 的不算：壞事件不能撐爆記憶體）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。JSON 一律走 SCP_Json。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Canvas
{
    /// <summary>畫布尺寸（像素）。</summary>
    public readonly struct SCP_CanvasSize : IEquatable<SCP_CanvasSize>
    {
        public readonly int Width;
        public readonly int Height;

        public SCP_CanvasSize(int iWidth, int iHeight) { Width = iWidth; Height = iHeight; }

        public static SCP_CanvasSize Default => new SCP_CanvasSize(SCP_CanvasSpec.DefaultWidth, SCP_CanvasSpec.DefaultHeight);

        public int Area => Width * Height;
        public bool InBounds(int iX, int iY) => iX >= 0 && iX < Width && iY >= 0 && iY < Height;
        /// <summary>兩邊各取大的（實際尺寸 ＝ 設定值 ∨ 已畫範圍）。</summary>
        public SCP_CanvasSize Max(SCP_CanvasSize iOther)
            => new SCP_CanvasSize(Math.Max(Width, iOther.Width), Math.Max(Height, iOther.Height));
        /// <summary>兩邊都 ≥ 對方 ⇒ 蓋得住。</summary>
        public bool Covers(SCP_CanvasSize iOther) => Width >= iOther.Width && Height >= iOther.Height;

        public bool Equals(SCP_CanvasSize iOther) => Width == iOther.Width && Height == iOther.Height;
        public override bool Equals(object? iObj) => iObj is SCP_CanvasSize s && Equals(s);
        public override int GetHashCode() => Width * 397 ^ Height;
        public override string ToString() => Width + "×" + Height;
    }

    /// <summary>一次解析的三個數：設定值、已畫範圍、實際尺寸。</summary>
    public sealed class SCP_CanvasSizeInfo
    {
        public SCP_CanvasSize Configured;
        /// <summary>設定值來自設定檔（false ＝ 檔不存在，用預設）。</summary>
        public bool FromFile;
        /// <summary>已畫範圍（沒有任何點 ⇒ 0×0）。</summary>
        public SCP_CanvasSize Extent;
        public SCP_CanvasSize Effective;
        /// <summary>設定檔讀不了的原因（照預設跑；空字串 ＝ 沒問題）。</summary>
        public string Warning = "";

        /// <summary>設定值蓋不住已畫範圍 ⇒ 實際尺寸被已畫範圍撐大了（要說出來）。</summary>
        public bool ClampedUp => !Configured.Covers(Extent);

        public string Describe()
        {
            string s = "設定 " + Configured + (FromFile ? "（canvas_settings.json）" : "（預設；沒有設定檔）")
                       + "／已畫範圍 " + Extent + "／實際 " + Effective;
            if (ClampedUp) s += "　⚠ 設定值小於已畫範圍 ⇒ 照已畫範圍（已畫的點不會掉出畫布）";
            if (Warning.Length > 0) s += "　⚠ " + Warning;
            return s;
        }
    }

    public static class SCP_CanvasSettings
    {
        public const string FileName = "canvas_settings.json";
        public const string KeyWidth = "width";
        public const string KeyHeight = "height";

        /// <summary>
        /// 讀設定值。檔不存在 ⇒ 預設、<paramref name="oFromFile"/>=false。
        /// 檔壞了／值不合法 ⇒ 預設＋<paramref name="oWarning"/>（⛔ 不丟例外 —— 實際尺寸還有已畫範圍兜底，點不會消失）。
        /// </summary>
        public static SCP_CanvasSize ReadConfigured(SCP_CanvasPaths iPaths, out bool oFromFile, out string oWarning)
        {
            oFromFile = false;
            oWarning = "";
            if (!File.Exists(iPaths.Settings)) return SCP_CanvasSize.Default;
            try
            {
                SCP_JsonData aJ = SCP_JsonParser.Parse(File.ReadAllText(iPaths.Settings, Encoding.UTF8));
                int w = aJ.GetInt(KeyWidth, -1), h = aJ.GetInt(KeyHeight, -1);
                if (!IsValidSide(w) || !IsValidSide(h))
                {
                    oWarning = FileName + " 的 width／height 不合法（" + w + "×" + h + "，要 1.." + SCP_CanvasSpec.MaxSide + "）⇒ 照預設";
                    return SCP_CanvasSize.Default;
                }
                oFromFile = true;
                return new SCP_CanvasSize(w, h);
            }
            catch (Exception e)
            {
                oWarning = FileName + " 讀不了（" + e.GetType().Name + ": " + e.Message + "）⇒ 照預設";
                return SCP_CanvasSize.Default;
            }
        }

        public static bool IsValidSide(int iSide) => iSide >= 1 && iSide <= SCP_CanvasSpec.MaxSide;

        /// <summary>事件的已畫範圍（只算會被畫上去的像素：座標 ≥0、&lt; MaxSide、顏色解得出來）。</summary>
        public static SCP_CanvasSize Extent(List<SCP_JsonData> iEvents)
        {
            int w = 0, h = 0;
            foreach (SCP_JsonData aEv in iEvents)
            {
                SCP_JsonData aPixels = aEv["pixels"];
                if (!aPixels.Exists) continue;
                for (int i = 0; i < aPixels.Count; i++)
                {
                    SCP_JsonData aPx = aPixels[i];
                    if (!SCP_CanvasEvents.TryCoord(aPx, out int x, out int y)) continue;
                    if (!SCP_CanvasEvents.TryColor(aPx["color"], out _)) continue;
                    if (x + 1 > w) w = x + 1;
                    if (y + 1 > h) h = y + 1;
                }
            }
            return new SCP_CanvasSize(w, h);
        }

        /// <summary>
        /// 不建整張畫布、只解析尺寸（放點前驗座標、雕刻貼圖夾範圍用）。
        /// 已畫範圍優先讀快取 meta（清單指紋對得上才信），對不上就掃全部事件。
        /// </summary>
        public static SCP_CanvasSizeInfo Resolve(SCP_CanvasPaths iPaths)
        {
            var aInfo = new SCP_CanvasSizeInfo();
            aInfo.Configured = ReadConfigured(iPaths, out aInfo.FromFile, out aInfo.Warning);
            if (!SCP_CanvasBuffer.TryReadCachedExtent(iPaths, out SCP_CanvasSize aExtent))
                aExtent = Extent(SCP_CanvasEvents.ReadAllEvents(iPaths));
            aInfo.Extent = aExtent;
            aInfo.Effective = aInfo.Configured.Max(aExtent);
            return aInfo;
        }

        /// <summary>
        /// 寫設定值。⛔ 縮到已畫範圍以下 ⇒ 擋下、零寫入（<paramref name="oWhy"/> 說蓋不住哪一邊）。
        /// 原子寫（tmp＋replace）。
        /// </summary>
        public static bool TryWrite(SCP_CanvasPaths iPaths, SCP_CanvasSize iSize, SCP_CanvasSize iExtent, out string oWhy)
        {
            oWhy = "";
            if (!IsValidSide(iSize.Width) || !IsValidSide(iSize.Height))
            { oWhy = "width／height 要 1.." + SCP_CanvasSpec.MaxSide + "（收到 " + iSize + "）"; return false; }
            if (!iSize.Covers(iExtent))
            {
                oWhy = "設定 " + iSize + " 蓋不住已畫範圍 " + iExtent + " —— 縮下去那些點會掉出畫布（"
                       + (iSize.Width < iExtent.Width ? "寬至少 " + iExtent.Width : "")
                       + (iSize.Width < iExtent.Width && iSize.Height < iExtent.Height ? "、" : "")
                       + (iSize.Height < iExtent.Height ? "高至少 " + iExtent.Height : "") + "）";
                return false;
            }
            SCP_JsonData aJ = SCP_JsonData.NewObject();
            aJ[KeyWidth] = iSize.Width;
            aJ[KeyHeight] = iSize.Height;
            Directory.CreateDirectory(iPaths.Root);
            string aTmp = iPaths.Settings + ".tmp";
            File.WriteAllText(aTmp, SCP_JsonWriter.Write(aJ, true) + "\n", new UTF8Encoding(false));
            if (File.Exists(iPaths.Settings)) File.Delete(iPaths.Settings);
            File.Move(aTmp, iPaths.Settings);
            return true;
        }
    }
}
