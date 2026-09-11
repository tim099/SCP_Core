// 區塊職責：施工範圍（絕對路徑）的**正規化與重疊判定** —— 全域獨佔那條軸的新判準（TASK-0201）。
// 物理意義：舊判準是「這個 kind 有人在跑就擋」，不看在改什麼 ⇒ 同一棵樹上改兩支不相干的檔也要排隊。
//           新判準是「有人在跑**而且範圍撞到我**才擋」。重疊＝**路徑包含**：
//           `…/Assets/Scripts` 與 `…/Assets/Scripts/Conditions` 重疊；與 `…/Assets/Plugins` 不重疊。
// 數值影響：純字串運算，零 IO。⛔ 不碰磁碟 —— 範圍宣告的路徑**可以還不存在**
//           （要新建的目錄照樣是合法範圍），拿 Directory.Exists 當閘會擋掉合法的場。
//
// ⚠ **拍板射程（Tim 2026-09-11）：純路徑判準 —— 同一個 repo 的兩份工作副本視為不衝突。**
//   `D:\Unity\Senate\SCP_Core` 與 `D:\Unity\LY\Assets\Plugins\SCP_Core` 是同一個 repo 的兩棵樹，
//   本層**不會**把它們判成衝突。代價是已知且被選擇的：兩個人分別在兩份副本改同一支檔時
//   這道閘不會叫，失效樣子是各自 commit 之後 push 分叉 —— 那要等到 push 才現形。
//   ⛔ 不要「順手」在這裡補一個 repo 身分解析：那會把「改 Senate 那份以避開 Unity 施工場」這條走法拿掉，
//      而那是同一天拍的另一個板。要改回來得先翻案。
//
// ⚠ 大小寫：本判定用 **OrdinalIgnoreCase** —— 宿主是 Windows，`D:\X` 與 `d:\x` 是同一個目錄。
//   ⛔ 這個假設寫在這裡而不是散在呼叫端：改成跨平台時只有這一格要動。
//
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.IO;

namespace SCP.Core.Session
{
    /// <summary>施工範圍（絕對路徑）的正規化與重疊判定。純字串，零 IO。</summary>
    public static class SCP_SessionScope
    {
        /// <summary>
        /// 正規化一段範圍路徑：解析 `.` / `..`、統一分隔符、去掉結尾分隔符。
        /// <para>三態，⛔ 不是 bool ——「沒宣告」與「宣告了但解不開」的**處置相反**：
        /// 前者退化成全域獨佔（安全），後者要當場擋下並叫人改（放行才危險）。</para>
        /// </summary>
        /// <param name="oNormalized">解析後的絕對路徑；未宣告或解不開時是空字串。</param>
        /// <param name="oError">解不開時的原因；其餘情況是空字串。</param>
        /// <returns>宣告了且解得開 ⇒ true。未宣告（空字串）也回 false，用 <paramref name="oError"/> 分辨。</returns>
        public static bool TryNormalize(string? iScope, out string oNormalized, out string oError)
        {
            oNormalized = "";
            oError = "";
            string aRaw = (iScope ?? "").Trim();
            if (aRaw.Length == 0) return false;                    // 未宣告 —— 不是錯

            try
            {
                string aFull = Path.GetFullPath(aRaw);
                // ⚠ 去尾分隔符要留住磁碟機根（`D:\` 去光會變成 `D:`，那是「目前目錄」不是根）。
                aFull = aFull.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (aFull.Length == 2 && aFull[1] == ':') aFull += Path.DirectorySeparatorChar;
                if (aFull.Length == 0)
                {
                    oError = "正規化之後是空的";
                    return false;
                }
                oNormalized = aFull;
                return true;
            }
            catch (Exception e)
            {
                // ⚠ 解不開**不可以**靜默退化成「未宣告」—— 那會讓打錯的人拿到一個他沒要的全域鎖，
                //   而輸出上跟「我刻意不宣告」一模一樣。
                oError = e.GetType().Name + ": " + e.Message;
                return false;
            }
        }

        /// <summary>
        /// 兩段**已正規化**的範圍是否重疊（＝其中一段包含另一段，或兩段相同）。
        /// <para>⚠ 只比字串前綴是不夠的：`…\Scripts` 是 `…\ScriptsOld` 的前綴，但它們是兩個目錄。
        /// ⇒ 前綴之後那一個字元必須是分隔符。</para>
        /// </summary>
        public static bool Overlaps(string iA, string iB)
        {
            if (iA.Length == 0 || iB.Length == 0) return false;
            if (string.Equals(iA, iB, StringComparison.OrdinalIgnoreCase)) return true;
            return Contains(iA, iB) || Contains(iB, iA);
        }

        /// <summary><paramref name="iOuter"/> 是不是 <paramref name="iInner"/> 的祖先目錄。</summary>
        static bool Contains(string iOuter, string iInner)
        {
            if (iInner.Length <= iOuter.Length) return false;
            if (!iInner.StartsWith(iOuter, StringComparison.OrdinalIgnoreCase)) return false;
            // 祖先本身以分隔符結尾（磁碟機根 `D:\`）時，下一個字元就是名字的第一個字。
            if (iOuter[iOuter.Length - 1] == Path.DirectorySeparatorChar
                || iOuter[iOuter.Length - 1] == Path.AltDirectorySeparatorChar) return true;
            char aNext = iInner[iOuter.Length];
            return aNext == Path.DirectorySeparatorChar || aNext == Path.AltDirectorySeparatorChar;
        }

        /// <summary>擋下時給人看的那一句 —— 說出**是哪兩段路徑撞到**，不要只說「衝突」。</summary>
        public static string Explain(string iMine, string iTheirs)
        {
            if (string.Equals(iMine, iTheirs, StringComparison.OrdinalIgnoreCase))
                return "兩邊宣告的是**同一段**：`" + iMine + "`";
            return Contains(iTheirs, iMine)
                ? "對方的範圍 `" + iTheirs + "` **包含**你的 `" + iMine + "`"
                : "你的範圍 `" + iMine + "` **包含**對方的 `" + iTheirs + "`";
        }
    }
}
