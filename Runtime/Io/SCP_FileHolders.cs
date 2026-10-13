// 區塊職責：問 Windows「現在是誰開著這個檔」（Restart Manager：RmStartSession → RmRegisterResources → RmGetList）（TASK-0534）。
// 物理意義：換檔被別的行程擋住那一刻問一次，把答案寫進讀數／錯誤訊息 —— 事後才問就太晚了（讀取端開檔通常只有幾毫秒）。
//   🩸 為什麼要它：TASK-0534 量得出「哪一類讀取端會撞」，卻抓不到 10-13 那幾次的現行犯（事後盯檔 185 次輪詢一個都沒看到）。
// 數值影響：只查詢、不開檔、不改任何東西；非 Windows、API 失敗 ⇒ 回一句說明（⛔ 不丟例外：它只是讀數，不能讓寫入因為它失敗）。
//   ⚠ 問的時候對方可能已經關掉了 ⇒ 「沒有人」不代表剛剛沒人；系統行程可能只回 pid 不回名字（權限）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SCP.Core.Io
{
    public static class SCP_FileHolders
    {
        /// <summary>「問的時候已經沒有人開著」（讀數用字面，⛔ 別讀成「剛剛也沒人」）。</summary>
        public const string NobodyNow = "（問的時候已經沒人開著）";

        /// <summary>
        /// 一句話：開著 <paramref name="iPath"/> 的行程（<c>名字(pid N)</c>，多個用「、」）；沒人 ⇒ <see cref="NobodyNow"/>；問不了 ⇒ 「（查不了：原因）」。
        /// </summary>
        public static string Describe(string iPath)
        {
            try
            {
                if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "（查不了：不是 Windows）";
                var aList = Query(iPath, out string? aWhy);
                if (aWhy != null) return "（查不了：" + aWhy + "）";
                return aList.Count == 0 ? NobodyNow : string.Join("、", aList);
            }
            catch (Exception e) { return "（查不了：" + e.GetType().Name + "：" + e.Message + "）"; }
        }

        static List<string> Query(string iPath, out string? oWhy)
        {
            var aOut = new List<string>();
            oWhy = null;
            int r = RmStartSession(out uint aSession, 0, Guid.NewGuid().ToString("N"));
            if (r != 0) { oWhy = "RmStartSession " + r; return aOut; }
            try
            {
                r = RmRegisterResources(aSession, 1, new[] { iPath }, 0, null, 0, null);
                if (r != 0) { oWhy = "RmRegisterResources " + r; return aOut; }
                uint aNeeded = 0, aCount = 0, aReasons = 0;
                r = RmGetList(aSession, out aNeeded, ref aCount, null, ref aReasons);
                if (r == 0) return aOut;                       // 沒有人
                if (r != ErrorMoreData) { oWhy = "RmGetList " + r; return aOut; }
                var aInfo = new RM_PROCESS_INFO[aNeeded];
                aCount = aNeeded;
                r = RmGetList(aSession, out aNeeded, ref aCount, aInfo, ref aReasons);
                if (r != 0) { oWhy = "RmGetList " + r; return aOut; }
                for (int i = 0; i < aCount; i++)
                {
                    int aPid = aInfo[i].Process.dwProcessId;
                    string aName = aInfo[i].strAppName ?? "";
                    try { aName = System.Diagnostics.Process.GetProcessById(aPid).ProcessName; } catch (Exception) { }   // 系統行程可能拿不到名字
                    string aSvc = aInfo[i].strServiceShortName ?? "";
                    aOut.Add((aName.Length > 0 ? aName : "?") + "(pid " + aPid + (aSvc.Length > 0 ? "，服務 " + aSvc : "") + ")");
                }
                return aOut;
            }
            finally { RmEndSession(aSession); }
        }

        const int ErrorMoreData = 234;

        [StructLayout(LayoutKind.Sequential)]
        struct RM_UNIQUE_PROCESS
        {
            public int dwProcessId;
            public System.Runtime.InteropServices.ComTypes.FILETIME ProcessStartTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct RM_PROCESS_INFO
        {
            public RM_UNIQUE_PROCESS Process;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string strAppName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string strServiceShortName;
            public int ApplicationType;
            public uint AppStatus;
            public uint TSSessionId;
            [MarshalAs(UnmanagedType.Bool)] public bool bRestartable;
        }

        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmStartSession(out uint pSessionHandle, int dwSessionFlags, string strSessionKey);
        [DllImport("rstrtmgr.dll")]
        static extern int RmEndSession(uint pSessionHandle);
        [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
        static extern int RmRegisterResources(uint pSessionHandle, uint nFiles, string[] rgsFilenames, uint nApplications,
                                              RM_UNIQUE_PROCESS[]? rgApplications, uint nServices, string[]? rgsServiceNames);
        [DllImport("rstrtmgr.dll")]
        static extern int RmGetList(uint dwSessionHandle, out uint pnProcInfoNeeded, ref uint pnProcInfo,
                                    [In, Out] RM_PROCESS_INFO[]? rgAffectedApps, ref uint lpdwRebootReasons);
    }
}
