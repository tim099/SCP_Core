// 區塊職責：JSON 輸出的**版面樣式**（縮排字串／冒號後空格／複合值的開括號位置）。
// 物理意義：⭐ 這些旋鈕之所以收成一個具名的型別、而不是散成 writer 的三個 bool 參數，
//           是因為「格式」是一件**整體**的事：呼叫端要說的是「我要寫成哪一種檔」，
//           不是「我要不要空格」。散成 bool 之後，下一個呼叫端只會抄到它看得見的那幾格，
//           而漏掉的那幾格不會讓任何讀數變紅 —— 寫出來的檔照樣是合法 JSON。
//           ⇒ 收成一個點的理由是**它可驗**：兩個呼叫端拿同一個 preset，就不可能只對齊一半。
// 數值影響：⛔ 只動版面，不動任何值、不動 key 順序 —— 兩種 preset 的產物 `Parse` 之後逐鍵相等。
// 🩸 血證（2026-09-11 → 09-14 calli，TASK-0166 ①）：我先列了「三根軸」（行尾／縮排／冒號空格）
//   就宣布量完了，隔天 `git diff -w` 印出 2 增 6 減 —— 還有**開括號位置**與**空容器渲染**。
//   ⇒ 一把只看得見自己列出那幾根軸的尺，在漏軸時的輸出跟「完全相同」一模一樣。
//   本型別是那次的修法：把軸放進**型別**裡，漏一根就是少一個欄位，編譯器會問。
// ⚠ 方言限制：C# 9 / netstandard2.1。
#nullable enable

namespace SCP.Core.Json
{
    /// <summary>JSON 輸出版面。⛔ 只影響空白與換行，不影響任何值。</summary>
    public readonly struct SCP_JsonStyle
    {
        /// <summary>一層縮排用的字串。</summary>
        public readonly string Indent;

        /// <summary><c>"k": v</c>（true）或 <c>"k":v</c>（false）。</summary>
        public readonly bool SpaceAfterColon;

        /// <summary>
        /// 陣列的開括號是否自己佔一行、落在**鍵的那一層縮排**上：
        /// true ⇒ <c>"rounds":\n\t[</c>，false ⇒ <c>"rounds": [</c>。
        /// <para>⚠ 只管**陣列** —— 物件的 <c>{</c> 兩種版面都貼在冒號後
        /// （<c>"progress":{</c>）。這不是我挑的對稱，是
        /// <c>UCL_JsonData.SerializeValueBeautify</c> 本來就把 IList 與 IDictionary 寫成兩種形狀。</para>
        /// </summary>
        public readonly bool ArrayBracketOnOwnLine;

        /// <summary>
        /// 空的物件／陣列是否照常展開成兩行（<c>[\n\n\t]</c>）而不是縮成 <c>[]</c>。
        /// <para>🩸 這一根是 2026-09-11 那張「五根軸」清單裡我**命名了卻沒放進 Library 那張表**的一根
        /// ——名字有了不等於量了。</para>
        /// </summary>
        public readonly bool EmptyContainerExpanded;

        public SCP_JsonStyle(string iIndent, bool iSpaceAfterColon,
                             bool iArrayBracketOnOwnLine, bool iEmptyContainerExpanded)
        {
            Indent = iIndent;
            SpaceAfterColon = iSpaceAfterColon;
            ArrayBracketOnOwnLine = iArrayBracketOnOwnLine;
            EmptyContainerExpanded = iEmptyContainerExpanded;
        }

        public SCP_JsonStyle WithIndent(string iIndent)
            => new SCP_JsonStyle(iIndent, SpaceAfterColon, ArrayBracketOnOwnLine, EmptyContainerExpanded);

        /// <summary>SCP 自己的預設：tab 縮排、冒號後補空格、K&amp;R 開括號。</summary>
        public static SCP_JsonStyle Default
            => new SCP_JsonStyle(SCP_JsonWriter.DefaultIndent, true, false, false);

        /// <summary>
        /// Unity 端 `UCL_JsonLib.ToJsonBeautify` 的版面：tab 縮排、冒號後**不**補空格、Allman 開括號。
        /// <para>⚠ 這不是「比較好看的風格」，是**磁碟上既有檔的形狀**。閱讀庫
        /// （`BookNotes/Library`）595 份 JSON 裡 429 份是這一種 ——
        /// 寫入端搬進 SCP_Core 時若用 <see cref="Default"/>，那 429 份第一次被寫到就整批翻紅，
        /// 而翻紅的內容**逐鍵相同**，沒有任何一層會喊。</para>
        /// </summary>
        /// <para>📌 版面規格的事實源是 `UCL_JsonData.SerializeValueBeautify`（**不是**我對磁碟的取樣）——
        /// 我取樣列過三次軸表，三次都漏（漏的軸不會讓任何讀數變紅）。⇒ 照著那支實作抄，然後逐位元組驗。</para>
        public static SCP_JsonStyle UclLegacy
            => new SCP_JsonStyle(SCP_JsonWriter.DefaultIndent, false, true, true);
    }
}
