// 區塊職責：**跨進程換檔期間的讀取端護欄** —— 重試讀檔 ＋ 把失敗分成「不存在」與「這一瞬間讀不了」。
// 物理意義：全樹的原子寫都是 `tmp → 換檔`（Delete→Move 或 File.Replace），而換檔那一瞬間目標檔
//           **短暫不存在或開不了**。不拿鎖的讀取端寫成 `if (!File.Exists(p)) return <空>` 的話，
//           就會把那一瞬間讀成一個合法的空值，而沒有任何一層會叫。
//
// 🩸 為什麼要有這一層（TASK-0265，2026-09-23 實測；3000 次寫入 × 一個不拿鎖的讀取端狂 stat）：
//   | 換檔寫法              | 讀取端讀到「檔案不存在」 |
//   |----------------------|------------------------:|
//   | `Delete` 然後 `Move` |              **40.9%** |
//   | `File.Replace`       |               **6.4%** |
//   ⇒ **換寫入端只能把窗口縮小，⛔ 關不掉它**（`File.Move(…, overwrite)` 在 netstandard2.1 上不存在）。
//   ⇒ 護欄只能長在**讀取端**：重試跨過那個窗口。2+4+6+8 ＝ 20ms 跨得過去，
//     而一個真的不存在的檔，重試 5 次仍然不存在。
//
// ⭐ 本檔是**唯一實作**：UCL_Core 的 `UCL_AtomicFileRead` 轉呼叫這裡（2026-09-27 起）。
//   ⛔ 別在任何一側再抄一份 —— 寫入端的 AtomicWrite 全樹被抄了 12 份而且已經在漂移。
//
// ⚠ 判準（這一段是本檔的本體，⛔ 別照名字用）：
//   ① `FileNotFoundException` **也要重試** —— 爭用在這一層的長相就是它。
//   ② 分類**留到重試用完之後**，看**最後那個例外**的型別 —— ⛔ 不是看第一個。
//   ③ `Missing` 與 `Busy` 必須是兩個出口：前者是合法狀態（還沒有人寫過），
//      後者是「我這次沒讀到，⛔ 不代表那裡沒東西」。呼叫端把兩者壓成同一句話，就退回本單要治的病。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.IO;
using System.Text;

namespace SCP.Core.Io
{
    /// <summary>讀一個可能正在被換檔的檔案，結果分三態。⛔ <see cref="Missing"/> 與 <see cref="Busy"/> 不可合併。</summary>
    public enum SCP_FileReadState
    {
        /// <summary>讀到了。</summary>
        Ok = 0,
        /// <summary>檔案**真的**不存在（重試用完仍然不存在）—— 合法狀態。</summary>
        Missing = 1,
        /// <summary>這一瞬間讀不了（被鎖／換檔中／權限）—— ⛔ **不是**「那裡沒東西」。</summary>
        Busy = 2,
    }

    /// <summary>換檔期間安全讀檔 —— 重試 ＋ 三態分類。用它取代 `if (!File.Exists(p)) return 空;`。</summary>
    public static class SCP_AtomicFileRead
    {
        /// <summary>重試次數。2+4+6+8 ＝ 20ms —— 足以跨過一次換檔，而不足以讓一個不存在的檔長出來。</summary>
        public const int DefaultMaxAttempt = 5;

        /// <summary>
        /// 讀整個檔（UTF-8，BOM 會被去掉）。回 true 代表 <paramref name="oText"/> 有效。
        /// </summary>
        /// <remarks>
        /// ⛔ 呼叫端**不要**把 false 一律當成「沒有東西」：
        /// <see cref="SCP_FileReadState.Missing"/> 才是那個意思，<see cref="SCP_FileReadState.Busy"/> 是「不知道」。
        /// </remarks>
        public static bool TryReadAllText(string? iPath, out string oText, out SCP_FileReadState oState,
                                          int iMaxAttempt = DefaultMaxAttempt)
        {
            oText = "";
            if (string.IsNullOrEmpty(iPath)) { oState = SCP_FileReadState.Missing; return false; }

            Exception? aLast = null;
            for (int aAttempt = 1; aAttempt <= iMaxAttempt; ++aAttempt)
            {
                try
                {
                    oText = File.ReadAllText(iPath, Encoding.UTF8);
                    oState = SCP_FileReadState.Ok;
                    return true;
                }
                // ⚠ `FileNotFoundException` / `DirectoryNotFoundException` 都是 `IOException` 的子類
                //   ⇒ 它們**一起**落進重試（判準①）。分類等迴圈跑完再做。
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    aLast = e;
                    if (aAttempt < iMaxAttempt) System.Threading.Thread.Sleep(2 * aAttempt);
                }
            }

            oState = (aLast is FileNotFoundException || aLast is DirectoryNotFoundException)
                   ? SCP_FileReadState.Missing
                   : SCP_FileReadState.Busy;
            return false;
        }

        /// <summary>
        /// 人讀的一句話 —— 給呼叫端在 <see cref="SCP_FileReadState.Busy"/> 時出聲用。
        /// ⛔ 別自己另寫一句：那會讓同一個狀態在不同地方長不一樣。
        /// </summary>
        public static string DescribeBusy(string? iPath)
            => $"[AtomicFileRead] 重試 {DefaultMaxAttempt} 次仍讀不到 `{iPath}` ——"
             + " ⚠ 這是「這一瞬間讀不了」，⛔ **不是**「那裡沒有東西」（多半是換檔或被鎖）。";
    }
}
