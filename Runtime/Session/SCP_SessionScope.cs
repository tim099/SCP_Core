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
// ⚠ **多路徑（TASK-0301，Tim 2026-09-25）**：一場可以宣告多段，以 `|` 分隔（`A|B`）。
//   分隔符選 `|` 是因為 Windows 路徑不能含它（`,`／`;` 都是合法檔名字元，拿來切會把真路徑切斷）。
//   正規化後仍是**一個字串**（各段正規化、去重、以 `|` 接回）⇒ session 檔的存法不變，舊的單段場照舊判。
//   重疊＝兩場的段集合**任一對**重疊；擋下訊息講出**撞到的是哪一對**。
//
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace SCP.Core.Session
{
    /// <summary>施工範圍（絕對路徑）的正規化與重疊判定。純字串，零 IO。</summary>
    public static class SCP_SessionScope
    {
        /// <summary>多段範圍的分隔符（TASK-0301）。Windows 路徑不能含它。</summary>
        public const char Separator = '|';

        /// <summary>把（已正規化或原始的）範圍切成各段；空字串 ⇒ 空清單。</summary>
        public static List<string> Segments(string? iScope)
        {
            var aList = new List<string>();
            foreach (string aSeg in (iScope ?? "").Split(Separator))
            {
                string t = aSeg.Trim();
                if (t.Length > 0) aList.Add(t);
            }
            return aList;
        }

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
            if (aRaw.IndexOf(Separator) < 0) return NormalizeOne(aRaw, out oNormalized, out oError);

            // 多段：逐段正規化；**任一段**解不開或是空段 ⇒ 整筆擋（⛔ 不靜默丟掉那一段 ——
            // 丟掉等於少宣告一塊，而那塊正是別人會撞上的地方，閘不會叫）
            var aOut = new List<string>();
            string[] aParts = aRaw.Split(Separator);
            for (int i = 0; i < aParts.Length; i++)
            {
                string aPart = aParts[i].Trim();
                if (aPart.Length == 0) { oError = $"第 {i + 1} 段是空的（`{aRaw}`）—— 多打了一個 `{Separator}`？"; return false; }
                if (!NormalizeOne(aPart, out string aNorm, out string aErr)) { oError = $"第 {i + 1} 段 `{aPart}` 解不開：{aErr}"; return false; }
                bool aDup = false;
                foreach (string x in aOut) if (string.Equals(x, aNorm, StringComparison.OrdinalIgnoreCase)) { aDup = true; break; }
                if (!aDup) aOut.Add(aNorm);
            }
            oNormalized = string.Join(Separator.ToString(), aOut);
            return true;
        }

        /// <summary>單段正規化（多段拆開後逐段呼叫）。</summary>
        static bool NormalizeOne(string aRaw, out string oNormalized, out string oError)
        {
            oNormalized = "";
            oError = "";

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
        public static bool Overlaps(string iA, string iB) => FindOverlap(iA, iB) != null;

        /// <summary>
        /// 兩場（可多段）的範圍裡**第一對**重疊的段（mine, theirs）；不重疊 ⇒ null。
        /// 都吃**已正規化**的範圍（<see cref="TryNormalize"/> 的輸出）。
        /// </summary>
        public static (string Mine, string Theirs)? FindOverlap(string iMine, string iTheirs)
        {
            foreach (string a in Segments(iMine))
                foreach (string b in Segments(iTheirs))
                    if (OverlapsOne(a, b)) return (a, b);
            return null;
        }

        static bool OverlapsOne(string iA, string iB)
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

        /// <summary>
        /// 被全域互斥擋下時，**擋你的到底是哪一種**。三態，⛔ 不是「衝突／不衝突」——
        /// 因為三者的**處置相反**：一個我自己補得了、一個只有對方補得了、一個要等或協調。
        /// </summary>
        public enum BlockKind
        {
            /// <summary>我沒宣告範圍 ⇒ 我這一場退化成全域獨佔，誰在場上都擋我。**補 scope 就可能進得去。**</summary>
            MineUnset,
            /// <summary>他沒宣告範圍 ⇒ 視同他可能改任何地方。**這一格我補不了。**</summary>
            TheirsUnset,
            /// <summary>兩邊都宣告了而真的重疊。</summary>
            Overlap,
        }

        // ===========================================================
        // 區塊職責：擋下訊息的**唯一一份措辭**（TASK-0203）。
        // 物理意義：這段話有**兩個宿主**在印 —— Senate 的 `SCP_Cmd_Coding.Blocked` 與
        //           Unity 的 `UCL_SessionStartGuard.ReasonOther/ExitOther`。
        // 🩸 為什麼收斂：2026-09-11 做 TASK-0201 時，同一個人在**同一天、相隔幾小時**
        //    把這三個分支各寫了一次。兩份都對、都跑得到、而沒有任何一層知道有第二份 ——
        //    而 `UCL_SessionStartGuard` 的檔頭早就在警告這個形狀（「措辭有兩份」）。
        //    ⇒ 判定與措辭收在這裡；宿主只補自己那半（誰、到幾點、它的收工指令怎麼打）。
        // 數值影響：純字串，零 IO。
        // ===========================================================
        /// <summary>擋你的是哪一種。兩個參數都吃**已正規化**的範圍（空字串＝沒宣告）。</summary>
        public static BlockKind Classify(string iMyScope, string iTheirScope)
        {
            if (string.IsNullOrEmpty(iMyScope)) return BlockKind.MineUnset;
            if (string.IsNullOrEmpty(iTheirScope)) return BlockKind.TheirsUnset;
            return BlockKind.Overlap;
        }

        /// <summary>擋下的原因（接在「@某某正在 Coding」後面的那半句）。</summary>
        public static string ReasonOf(BlockKind iKind, string iMyScope, string iTheirScope)
        {
            switch (iKind)
            {
                case BlockKind.MineUnset:
                    return "而**你沒有宣告施工範圍** ⇒ 你這一場退化成全域獨佔，所以誰在場上都擋你";
                case BlockKind.TheirsUnset:
                    return "而**他沒有宣告施工範圍** ⇒ 視同他可能改任何地方，所以擋你";
                default:
                    return "範圍撞到：" + Explain(iMyScope, iTheirScope);
            }
        }

        /// <summary>
        /// 處理方式。**只有 <see cref="BlockKind.MineUnset"/> 回非空** ——
        /// 那是唯一一種「你自己補得了」的；其餘兩種的出口是宿主自己的「等他／去問他」，
        /// ⛔ 本層不編那句（它要知道對方的收工指令怎麼打，而那是宿主的知識）。
        /// </summary>
        public static string ExitOf(BlockKind iKind, string iRetryCommand)
        {
            if (iKind != BlockKind.MineUnset) return "";
            string aOut = "先補上施工範圍再試一次（範圍不重疊就進得去）";
            return iRetryCommand.Length > 0 ? aOut + "：" + iRetryCommand : aOut;
        }

        /// <summary>擋下時給人看的那一句 —— 說出**是哪兩段路徑撞到**，不要只說「衝突」。</summary>
        public static string Explain(string iMine, string iTheirs)
        {
            // 多段：講出撞到的那一對（⛔ 不把整串 `A|B|C` 丟給人自己找）
            var aHit = FindOverlap(iMine, iTheirs);
            if (aHit != null && (iMine.IndexOf(Separator) >= 0 || iTheirs.IndexOf(Separator) >= 0))
                return ExplainOne(aHit.Value.Mine, aHit.Value.Theirs) + "（多段範圍裡撞到的是這一對）";
            return ExplainOne(iMine, iTheirs);
        }

        static string ExplainOne(string iMine, string iTheirs)
        {
            if (string.Equals(iMine, iTheirs, StringComparison.OrdinalIgnoreCase))
                return "兩邊宣告的是**同一段**：`" + iMine + "`";
            return Contains(iTheirs, iMine)
                ? "對方的範圍 `" + iTheirs + "` **包含**你的 `" + iMine + "`"
                : "你的範圍 `" + iMine + "` **包含**對方的 `" + iTheirs + "`";
        }
    }
}
