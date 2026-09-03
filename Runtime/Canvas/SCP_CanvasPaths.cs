// 區塊職責：畫布的所有子路徑（events / 快取 / 預覽 / 快照 / notes / claims / meta）。
// 物理意義：畫布狀態是 **per-project** 的，住資料根底下的 `Canvas/`。
//           資料根由**宿主**給（Senate 是 senate.local.json 的專案設定、Unity 那側是既有解析器），
//           本層一律不推導、不 walk cwd。
// 🩸 為什麼這一段刻意沒有「找根」的邏輯（TASK-0112，2026-09-03，Tim 抓到的）：
//    python 那側三個儲存根原本是相對 cwd 的字串。shell 的 cwd 停在 Assets/Plugins/UCL_Core 時放點，
//    工具就在 UCL_Core 底下**長出一棵新的 AgentCommands 樹** —— 寫進去、回讀出來、四層全綠，
//    而真畫布 history 是 0、ledger 卻真的扣了 10 token。
//    ⇒ 「回讀」與「寫入」共用同一個錯的根時，綠不是證據，它只是同一個錯抄了兩遍。
//    ⇒ 所以這裡只接受呼叫端給的資料根；解析失敗是宿主的事，不在這裡猜一個看起來合理的。
using System.IO;
using SCP.Core.Paths;

namespace SCP.Core.Canvas
{
    public sealed class SCP_CanvasPaths
    {
        /// <summary>畫布根（<c>&lt;資料根&gt;/Canvas</c>）。</summary>
        public string Root { get; }

        public SCP_CanvasPaths(SCP_DataRoot iDataRoot)
        {
            Root = Path.Combine(iDataRoot.Value, DirName).Replace('\\', '/');
        }

        /// <summary>直接給畫布根（測試隔離用；正路走 <see cref="SCP_DataRoot"/> 那個建構子）。</summary>
        public SCP_CanvasPaths(string iCanvasRoot)
        {
            Root = iCanvasRoot.Replace('\\', '/').TrimEnd('/');
        }

        public const string DirName = "Canvas";

        string Sub(string iName) { return Root + "/" + iName; }

        public string Meta => Sub("_meta.json");
        public string Events => Sub("events");
        public string Vouchers => Sub("vouchers");
        public string FreeTime => Sub("freetime");
        public string Notes => Sub("notes");
        public string Claims => Sub("claims.json");
        public string Snapshots => Sub("snapshots");
        public string Previews => Sub("previews");
        public string Locks => Sub("_locks");

        public string LatestPng => Sub("canvas_latest.png");
        public string LatestTransparentPng => Sub("canvas_latest_t.png");
        public string LastViewPng => Sub("_last_view.png");
        public string LastViewTransparentPng => Sub("_last_view_t.png");

        /// <summary>增量快取（衍生物，可隨時丟棄；<c>.gitignore</c> 擋掉）。</summary>
        public string CacheMeta => Sub("_canvas_cache.json");
        public string CacheBin => Sub("_canvas_cache.bin");

        public string NoteFile(string iPersona) { return Notes + "/" + iPersona + ".json"; }
    }
}
