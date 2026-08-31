// 區塊職責：把路徑描述**黏在 enum 成員上**（Tim 2026-08-31）。
// 物理意義：宣告與描述同一個位置 ⇒ **加了成員就一定看得到描述的空位**。
//           上一版把描述放在另一支 `SCP_PathDescriptor[]` 陣列裡，那是「兩個要同步的清單」——
//           而漏填的症狀是**執行到那一格才丟例外**（頁面打不開／CLI 少一列），不是寫的時候就看見。
//           ⇒ 這一版沒有第二份清單可以漏。
//
// ⚠ **為什麼是三個 attribute 而不是一個帶 nullable 欄位的**：
//   attribute 參數不能是 `SCP_PathId?`（C# 不允許 nullable enum 當 attribute 參數型別），
//   而用「成員 0 當沒設定」＝拿 `ProjectRoot` 當哨兵 ⇒ **「沒設上游」與「上游是 ProjectRoot」同形**。
//   那正是這一整條線在修的病。
//   ⇒ 改成**用「有沒有掛」表達有沒有**：
//     · `[SCP_PathStored]` ＝ 值存在設定檔（人填 / auto）
//     · `[SCP_PathDerived]` ＝ 永遠算出來、不存（上游是**建構子必填參數**，不可能忘）
//     · `[SCP_PathAuto]`   ＝ 這格的 Stored 值支援 `auto`（掛了才支援）
//   每一種狀態都表示得出來，而且沒有哨兵值。
//
// ⚠ 合法性由 `SCP_PathRegistry.Validate()` 檢查，並掛在 `senate selftest` 上 ——
//   「漏掛 attribute」必須是**出廠驗收擋下**的事，不是執行到那一格才炸。
#nullable enable
using System;

namespace SCP.Core.Paths
{
    /// <summary>這條路徑的值**存在設定檔裡**（人填，或掛 <see cref="SCP_PathAutoAttribute"/> 後填 auto）。</summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class SCP_PathStoredAttribute : Attribute
    {
        /// <param name="iJsonKey">
        /// 儲存鍵。⚠ **這是 wire name** —— 改它＝改設定檔格式；
        /// enum 成員名改了不影響這裡（Task 那組 enum 的成員名就是磁碟格式，同一個坑不再挖一次）。
        /// </param>
        public SCP_PathStoredAttribute(string iJsonKey, SCP_PathScope iScope)
        {
            JsonKey = iJsonKey;
            Scope = iScope;
        }

        public string JsonKey { get; }
        public SCP_PathScope Scope { get; }
    }

    /// <summary>
    /// 這條路徑**永遠由上游算出來、不儲存**。
    /// <para>⚠ 上游是建構子必填 —— Derived 而沒有上游這個狀態**表示不出來**。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class SCP_PathDerivedAttribute : Attribute
    {
        public SCP_PathDerivedAttribute(SCP_PathId iFrom, string iSuffix, SCP_PathScope iScope)
        {
            From = iFrom;
            Suffix = iSuffix;
            Scope = iScope;
        }

        public SCP_PathId From { get; }
        public string Suffix { get; }
        public SCP_PathScope Scope { get; }
    }

    /// <summary>
    /// 這格的 Stored 值支援 <c>auto</c>（＝交給上游推導）。**掛了才支援。**
    /// <para>只能掛在有 <see cref="SCP_PathStoredAttribute"/> 的成員上（Validate 會擋）。</para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class SCP_PathAutoAttribute : Attribute
    {
        public SCP_PathAutoAttribute(SCP_PathId iFrom, string iSuffix)
        {
            From = iFrom;
            Suffix = iSuffix;
        }

        public SCP_PathId From { get; }
        public string Suffix { get; }
    }

    /// <summary>顯示名 ＋ 說明。<para>⚠ `Note` 要留在 attribute 而不是 XML doc：
    /// 頁面與 CLI **會把它印出來**，而 XML doc 在執行期拿不到（除非跟著 ship .xml）。</para></summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false)]
    public sealed class SCP_PathInfoAttribute : Attribute
    {
        public SCP_PathInfoAttribute(string iLabel, string iNote)
        {
            Label = iLabel;
            Note = iNote;
        }

        public string Label { get; }

        /// <summary>這格是幹什麼的、以及**它為什麼是 Stored 或 Derived** ——
        /// 「刻意如此」與「還沒做」在程式碼裡長得一模一樣，所以要寫下來。</summary>
        public string Note { get; }
    }
}
