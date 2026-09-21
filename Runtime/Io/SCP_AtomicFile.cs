// 區塊職責：原子建檔，以及**「這個 IOException 是不是『檔名已經有人了』」**這個判斷。
// 物理意義：TASK-0256。撞檔要回報給呼叫端去重算號碼，而**其他 IO 失敗必須原樣往上炸** ——
//           兩者的處置相反，混起來的代價是：真正的成因被換成一個假的。
//
// 🩸 為什麼這個判斷要獨立成一支（@kiara 2026-09-21 QA 擋下來的那一格）：
//   第一版寫的是 `catch (IOException) when (File.Exists(path))`，而她量到
//   **`FileStream(CreateNew)` 一建構，那顆檔就已經存在了（0 bytes，內容還沒寫）**
//   ⇒ 那個條件在**任何**建構之後才發生的 IOException 上都是 true。
//   實測：把「磁碟空間不足」丟進去 ⇒ 被吃掉、降級成撞檔、留下 0-byte 孤兒檔，
//   而孤兒檔會**永久佔住一個 seq**，最後報出來的成因是「資料層可能損壞，去檢查 messages/」——
//   **那句話會把人送去翻一個沒有壞的目錄。**
//   ⇒ 判準換成 win32 error code（她量的：撞檔 80 `ERROR_FILE_EXISTS`／磁碟滿 112 `ERROR_DISK_FULL`）。
//
// ⛔ **不要回頭問 `File.Exists`** —— 那一問等於用「現在磁碟上有沒有檔」去回答
//   「剛剛是誰先建的」，而它們在建構之後永遠同形。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.IO;
using System.Text;

namespace SCP.Core.Io
{
    public static class SCP_AtomicFile
    {
        /// <summary>`ERROR_FILE_EXISTS`（win32 80）—— 目標檔名已經有人了。</summary>
        public const int ErrorFileExists = 80;

        /// <summary>`ERROR_ALREADY_EXISTS`（win32 183）—— 同一件事的另一個回報碼。</summary>
        public const int ErrorAlreadyExists = 183;

        /// <summary>
        /// 這個 <see cref="IOException"/> 是不是「**檔名已經有人了**」。
        /// <para>⚠ 只認 win32 80／183。⛔ 其餘一律回 <c>false</c> ——
        /// 磁碟滿（112）、路徑失效、共用衝突都**不是**撞檔，它們要原樣往上炸。</para>
        /// <para>⚠ 非 Windows 平台上 <c>HResult</c> 帶的是 errno 對映，本判準**只在 Windows 上量過**
        /// （2026-09-21）。⇒ 那不是「跨平台成立」，是「還沒量」。</para>
        /// </summary>
        public static bool IsAlreadyExists(IOException iEx)
        {
            if (iEx == null) return false;
            int aCode = iEx.HResult & 0xFFFF;
            return aCode == ErrorFileExists || aCode == ErrorAlreadyExists;
        }

        /// <summary>
        /// 原子建檔：目標**已存在就回 <c>false</c>**，⛔ 不覆寫。
        /// <para>其餘 IO 失敗（磁碟滿／路徑失效／共用衝突）**原樣往上炸** —— 呼叫端不該把它們當撞檔重試。</para>
        /// </summary>
        /// <returns><c>true</c> ＝ 這一次真的建出來並寫完了。</returns>
        public static bool TryCreateNew(string iPath, string iText)
        {
            try
            {
                using (var aStream = new FileStream(iPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                using (var aWriter = new StreamWriter(aStream, new UTF8Encoding(false)))
                {
                    aWriter.Write(iText);
                }
                return true;
            }
            catch (IOException e) when (IsAlreadyExists(e))
            {
                return false;
            }
        }
    }
}
