// 區塊職責：**GPU 即時畫面**登記處（TASK-0470）—— 頁面放「要畫什麼」（場景物件），視窗宿主在自己的 GL context 裡畫進貼圖直接顯示。
// 物理意義：Image 節點的 Value 以 <see cref="Prefix"/>（`gpu:`）開頭 ＝「去登記處拿這個 key 的場景，交給宿主畫」。
//          跟 `mem:`（<see cref="SCP_GuiImageStore"/>）的差別：mem 是頁面算好的 RGBA；gpu 是**場景描述**，像素由宿主的 GPU 算，
//          ⛔ 不經過 CPU 讀回 ⇒ 拖曳、縮放每幀重畫只花 GPU 的時間。
//          宿主能畫哪幾種場景由它自己登記（<see cref="RegisterPainter"/>）；文字模式／沒有 GL 的宿主不登記 ⇒ 頁面看 <see cref="CanPaint"/> 退回 CPU。
//          宿主畫完（或失敗）回報 <see cref="Report"/>：失敗的原因要讓頁面讀得到、印得出來 —— 「GPU 壞了」不能長得跟「還沒畫」一樣。
// 數值影響：只存最新一幀（同 key 覆蓋）；場景物件 Put 之後呼叫端不得再改（宿主在繪圖執行緒讀它）。
// ⚠ 方言限制：C# 9 / netstandard2.1。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Gui
{
    /// <summary>登記的一幀（不可變）。</summary>
    public sealed class SCP_GuiGpuFrame
    {
        public SCP_GuiGpuFrame(long iVersion, object iScene, int iWidth, int iHeight)
        {
            Version = iVersion; Scene = iScene; Width = iWidth; Height = iHeight;
        }
        public long Version { get; }
        public object Scene { get; }
        public int Width { get; }
        public int Height { get; }
    }

    /// <summary>宿主對某個 key 最近一次的回報。</summary>
    public sealed class SCP_GuiGpuStatus
    {
        public SCP_GuiGpuStatus(long iVersion, string? iError, string iInfo)
        {
            Version = iVersion; Error = iError; Info = iInfo;
        }
        /// <summary>畫的是哪一版（<see cref="SCP_GuiGpuFrame.Version"/>）。</summary>
        public long Version { get; }
        /// <summary>null ＝ 畫成功；否則是失敗原因。</summary>
        public string? Error { get; }
        /// <summary>讀數（GL 型號、同步了幾個分塊…）—— 頁面原樣印出。</summary>
        public string Info { get; }
    }

    public static class SCP_GuiGpuViews
    {
        public const string Prefix = "gpu:";

        static readonly object s_Lock = new object();
        static readonly Dictionary<string, SCP_GuiGpuFrame> s_Frames = new Dictionary<string, SCP_GuiGpuFrame>(StringComparer.Ordinal);
        static readonly Dictionary<string, SCP_GuiGpuStatus> s_Status = new Dictionary<string, SCP_GuiGpuStatus>(StringComparer.Ordinal);
        static readonly HashSet<Type> s_Painters = new HashSet<Type>();
        static long s_Counter;

        public static string Ref(string iKey) => Prefix + iKey;
        public static bool IsGpu(string? iValue) => iValue != null && iValue.StartsWith(Prefix, StringComparison.Ordinal);
        public static string? KeyOf(string? iValue) => IsGpu(iValue) ? iValue!.Substring(Prefix.Length) : null;

        // ── 宿主那一側 ─────────────────────────────────────
        /// <summary>宿主宣告「這種場景我畫得出來」（有 GL context 之後才登記）。</summary>
        public static void RegisterPainter(Type iSceneType) { lock (s_Lock) s_Painters.Add(iSceneType); }
        public static void UnregisterPainter(Type iSceneType) { lock (s_Lock) s_Painters.Remove(iSceneType); }
        public static bool CanPaint(Type iSceneType) { lock (s_Lock) return s_Painters.Contains(iSceneType); }

        /// <summary>宿主畫完（或失敗）之後回報。</summary>
        public static void Report(string iKey, long iVersion, string? iError, string iInfo)
        {
            lock (s_Lock) s_Status[iKey] = new SCP_GuiGpuStatus(iVersion, iError, iInfo ?? "");
        }

        public static bool TryGetStatus(string iKey, out SCP_GuiGpuStatus oStatus)
        {
            lock (s_Lock) return s_Status.TryGetValue(iKey, out oStatus!);
        }

        // ── 頁面那一側 ─────────────────────────────────────
        /// <summary>放一幀新的場景（覆蓋同 key）；回傳版本。</summary>
        public static long Put(string iKey, object iScene, int iWidth, int iHeight)
        {
            if (string.IsNullOrEmpty(iKey)) throw new ArgumentException("key 不能空", nameof(iKey));
            if (iScene == null) throw new ArgumentNullException(nameof(iScene));
            if (iWidth <= 0 || iHeight <= 0) throw new ArgumentException($"尺寸要大於 0：{iWidth}×{iHeight}");
            lock (s_Lock)
            {
                long v = ++s_Counter;
                s_Frames[iKey] = new SCP_GuiGpuFrame(v, iScene, iWidth, iHeight);
                return v;
            }
        }

        public static bool TryGet(string iKey, out SCP_GuiGpuFrame oFrame)
        {
            lock (s_Lock) return s_Frames.TryGetValue(iKey, out oFrame!);
        }

        public static bool Has(string iKey) { lock (s_Lock) return s_Frames.ContainsKey(iKey); }

        public static void Remove(string iKey)
        {
            lock (s_Lock) { s_Frames.Remove(iKey); s_Status.Remove(iKey); }
        }
    }
}
