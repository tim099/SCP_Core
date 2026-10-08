// 區塊職責：**記憶體影像**登記處 —— 頁面把算好的 RGBA 直接放進來，renderer 直接拿去變貼圖，不經過檔案。
// 物理意義：Image 節點的 <see cref="SCP_GuiNode.Value"/> 平常是圖檔路徑；以 <see cref="Prefix"/>（`mem:`）開頭時
//           ＝「去登記處拿這個 key 的那一張」。頁面（背景執行緒）<see cref="Put"/>、renderer（繪圖執行緒）<see cref="TryGet"/>，
//           每次 Put 版本 +1，renderer 用版本判斷貼圖要不要更新（取代檔案的修改時間）。
// 為什麼不寫檔：寫檔再讀回要編碼、解碼，而且檔案被別的程式鎖住（預覽軟體、同步工具、防毒）時
//           **寫不進去也讀不到，畫面就停在舊圖**，沒有任何一層會喊 —— 預覽這種每幀都在換的圖，不該依賴檔案系統。
// 數值影響：只存「最新一張」（同 key 覆蓋）；一張 720² RGBA ≈ 2 MB，4096² ≈ 64 MB（上限由呼叫端控制）。
//           Put 之後呼叫端不得再改那個陣列（renderer 會在別的執行緒讀它）—— 要換圖就 Put 新陣列。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Gui
{
    /// <summary>一張登記在記憶體裡的圖（不可變）。</summary>
    public sealed class SCP_GuiImageFrame
    {
        public SCP_GuiImageFrame(long iVersion, byte[] iRgba, int iWidth, int iHeight)
        {
            Version = iVersion;
            Rgba = iRgba;
            Width = iWidth;
            Height = iHeight;
        }

        /// <summary>這個 key 的第幾次 Put（從 1 起；全域遞增，所以兩個 key 的版本不會撞）。</summary>
        public long Version { get; }

        /// <summary>RGBA、由上到下、每像素 4 bytes；長度 ＝ Width × Height × 4。</summary>
        public byte[] Rgba { get; }

        public int Width { get; }
        public int Height { get; }
    }

    public static class SCP_GuiImageStore
    {
        /// <summary>Image 節點的 Value 以這個開頭 ＝ 記憶體影像（後面接 key）。</summary>
        public const string Prefix = "mem:";

        static readonly object s_Lock = new object();
        static readonly Dictionary<string, SCP_GuiImageFrame> s_Frames = new Dictionary<string, SCP_GuiImageFrame>(StringComparer.Ordinal);
        static long s_Counter;

        /// <summary>key → 放進 Image 節點 Value 的字串。</summary>
        public static string Ref(string iKey) => Prefix + iKey;

        public static bool IsMemory(string? iValue) => iValue != null && iValue.StartsWith(Prefix, StringComparison.Ordinal);

        /// <summary>Value → key（不是 `mem:` 開頭 ⇒ null）。</summary>
        public static string? KeyOf(string? iValue) => IsMemory(iValue) ? iValue!.Substring(Prefix.Length) : null;

        /// <summary>放一張新的（覆蓋同 key 的舊圖）；回傳新版本。尺寸與長度對不上 ⇒ 丟 ArgumentException（⛔ 不安靜吞掉）。</summary>
        public static long Put(string iKey, byte[] iRgba, int iWidth, int iHeight)
        {
            if (string.IsNullOrEmpty(iKey)) throw new ArgumentException("key 不能空", nameof(iKey));
            if (iWidth <= 0 || iHeight <= 0) throw new ArgumentException($"尺寸要大於 0：{iWidth}×{iHeight}");
            if (iRgba == null || iRgba.LongLength != (long)iWidth * iHeight * 4)
                throw new ArgumentException($"RGBA 長度要 ＝ 寬×高×4（{(long)iWidth * iHeight * 4}），實際 {(iRgba == null ? -1 : iRgba.LongLength)}");
            lock (s_Lock)
            {
                long aVersion = ++s_Counter;
                s_Frames[iKey] = new SCP_GuiImageFrame(aVersion, iRgba, iWidth, iHeight);
                return aVersion;
            }
        }

        public static bool TryGet(string iKey, out SCP_GuiImageFrame oFrame)
        {
            lock (s_Lock) return s_Frames.TryGetValue(iKey, out oFrame!);
        }

        public static bool Has(string iKey)
        {
            lock (s_Lock) return s_Frames.ContainsKey(iKey);
        }

        /// <summary>丟掉這個 key 的圖（頁面離開時釋放記憶體）。</summary>
        public static void Remove(string iKey)
        {
            lock (s_Lock) s_Frames.Remove(iKey);
        }
    }
}
