// 區塊職責：「別的行程隨時會來讀」的小檔（雕刻作品的 links／models／ropes／anchors／sails／marks.json…）的讀與換檔（TASK-0534）。
// 物理意義：寫入端是 tmp → File.Replace；Windows 上目標檔這一瞬間被別的行程開著、而那個 handle 沒給 FILE_SHARE_DELETE，
//           Replace 就丟 IOException（共享衝突 0x20，或先後順序換一下就是「無法移除要被取代的檔案」1175）。
//   🩸 2026-10-13 量（scratchpad 探針＋真的 `senate cmd sculpture`）：
//     | 讀取端開檔方式（狂讀同一個檔）          | 寫入端 Replace 撞到 |
//     |----------------------------------------|--------------------:|
//     | File.ReadAllText（FileShare.Read）       |  79／1000（探針）、27／100（CLI 寫固定點） |
//     | FileShare.ReadWrite（沒有 Delete）       |  62／800 |
//     | FileShare.ReadWrite｜Delete               |   0／800 |
//     | 沒有讀取端                               |   0／2400（同一行程）、0／300（CLI） |
//   ⇒ 兩邊各做一件：讀的人給 Delete 共享（⛔ 不擋別人換檔）；寫的人撞到共享衝突就**有上限地**重試
//     （擋路的也可能是防毒、git、別的工具 —— 那些讀法我們改不了）。
//   常駐視窗的雕刻頁就是一個這樣的讀取端：繩／帆／模型開關清單每秒把選中作品整棵關聯樹重讀一次（RigRows）。
//   被擋的當下問 Restart Manager 誰開著（SCP_FileHolders）⇒ 讀數與失敗訊息都指名。
// 數值影響：讀到的位元組與 File.ReadAllText 相同（UTF-8、BOM 去掉）；換檔成功時落盤的就是那一份 tmp。
//           ⛔ 只重試「一瞬間被開著」那幾種錯（32／33／1175／1176／1177）—— 唯讀、權限、路徑不存在一次就失敗，錯誤帶目標路徑。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.IO;
using System.Text;
using System.Threading;

namespace SCP.Core.Io
{
    public static class SCP_SharedFile
    {
        /// <summary>換檔最多試幾次。間隔 10、20、40…640 ms ⇒ 八次合計約 1.3 秒才放棄。</summary>
        public const int DefaultReplaceAttempts = 8;
        /// <summary>讀檔最多試幾次（被換檔那一瞬間擋到）：間隔 5、10、15… ms。</summary>
        public const int DefaultReadAttempts = 6;

        /// <summary>
        /// 換檔撞到一次共享衝突、準備重試時叫（目標路徑、第幾次、HResult、當下誰開著）—— 給呼叫端印讀數用；⛔ 不影響重試本身。
        /// <para>「誰開著」只在每次換檔的**第一次**被擋時問（<see cref="SCP_FileHolders"/>；之後幾次給空字串 —— 一次問一下就夠指名，⛔ 不每次都多花那一趟）。</para>
        /// </summary>
        public static event Action<string, int, int, string>? Contention;

        /// <summary>這個例外是不是「這一瞬間被別人開著」（重試可能會過）。⛔ 不存在／唯讀／權限都不是。</summary>
        public static bool IsTransient(Exception iError)
        {
            if (!(iError is IOException) || iError is FileNotFoundException || iError is DirectoryNotFoundException) return false;
            int aCode = iError.HResult & 0xFFFF;
            return aCode == 32 || aCode == 33 || aCode == 1175 || aCode == 1176 || aCode == 1177;
        }

        /// <summary>
        /// 讀整份文字（UTF-8，BOM 去掉，同 <c>File.ReadAllText(p, new UTF8Encoding(false))</c>）。
        /// 開檔給 ReadWrite｜Delete 共享 ⇒ 讀的時候別人照樣換得了檔；被換檔那一瞬間擋到 ⇒ 短重試。
        /// 不存在、權限等照 <see cref="File.ReadAllText(string)"/> 的例外丟（⛔ 不吞成空字串）。
        /// </summary>
        public static string ReadAllText(string iPath, int iMaxAttempt = DefaultReadAttempts)
        {
            for (int aAttempt = 1; ; aAttempt++)
            {
                try
                {
                    using (var aFs = new FileStream(iPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                    using (var aReader = new StreamReader(aFs, new UTF8Encoding(false), true))
                        return aReader.ReadToEnd();
                }
                catch (IOException e) when (IsTransient(e) && aAttempt < iMaxAttempt)
                {
                    Thread.Sleep(5 * aAttempt);
                }
            }
        }

        /// <summary>
        /// 把寫好的 <paramref name="iTmp"/> 換成 <paramref name="iPath"/>（目標存在走 Replace，不存在走 Move）。回傳試了幾次（1 ＝ 一次就過）。
        /// <para>共享衝突 ⇒ 有上限地重試（<paramref name="iMaxAttempt"/>）；試完還是被擋、或是別種錯（唯讀、權限…）⇒ 丟 IOException，
        /// 訊息寫目標路徑、試了幾次、最後那個例外（inner）—— 呼叫端的錯誤報告才看得出是哪個檔。⛔ tmp 由呼叫端清。</para>
        /// </summary>
        public static int Replace(string iTmp, string iPath, int iMaxAttempt = DefaultReplaceAttempts)
        {
            for (int aAttempt = 1; ; aAttempt++)
            {
                try
                {
                    if (File.Exists(iPath)) File.Replace(iTmp, iPath, null);
                    else File.Move(iTmp, iPath);
                    return aAttempt;
                }
                catch (IOException e) when (IsTransient(e))
                {
                    if (aAttempt >= iMaxAttempt)
                        throw new IOException("換檔失敗：`" + iPath + "` —— 試了 " + aAttempt + " 次都被別的行程開著擋住（最後一次 0x"
                                              + e.HResult.ToString("X8") + "：" + e.Message + "；當下開著的：" + SCP_FileHolders.Describe(iPath) + "）", e);
                    var aOnContention = Contention;   // 沒人聽就不去問 Restart Manager
                    if (aOnContention != null) aOnContention(iPath, aAttempt, e.HResult, aAttempt == 1 ? SCP_FileHolders.Describe(iPath) : "");
                    Thread.Sleep(10 << (aAttempt - 1 < 6 ? aAttempt - 1 : 6));
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    throw new IOException("換檔失敗：`" + iPath + "` —— " + e.GetType().Name + "：" + e.Message + "（不是一瞬間被擋，⛔ 不重試）", e);
                }
            }
        }
    }
}
