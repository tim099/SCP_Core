// 區塊職責：銀行帳號 id 的**唯一正規化與驗證點**（TASK-0209 B1）。
// 物理意義：舊系統沒有這一層，代價是 2026-09-14 實測到的：
//           51 個有餘額的帳號裡有 **4 組只差大小寫**（`zeta`/`Zeta`、`zeta-da-xiaojie`/`Zeta-da-xiaojie`、
//           `gemini-da-xiaojie`/`Gemini-da-xiaojie`、`antigravity`/`Antigravity`），
//           而其中兩組**錢是分開放的**（`zeta` 3380 / `Zeta` 0；`Zeta-da-xiaojie` 1640 / `zeta-da-xiaojie` 0）。
//           🩸 更毒的是它在 Windows 上的形狀：檔案系統大小寫不敏感 ⇒ `Zeta.json` 與 `zeta.json`
//           **是同一個檔**，而帳本裡它們是兩個帳號 ⇒ 「餘額表有兩筆」與「檔案只有一份」同時成立，
//           而沒有任何一層會喊。（舊 `UCL_BankAccountProfile` 只在讀到撞名時印一行 Warning —— 那是提示不是閘。）
// 數值影響：純函式，零 IO。但它是**寫入端**的閘：所有 credit／debit／open 都要先過這裡。
//
// 🩸 三條判準，每一條都是「錯的時候長什麼樣」決定的：
//   ① **正規化只做大小寫，⛔ 不做任何字元替換。** 把不合法字元「修好」等於**靜默合併兩個不同的帳號**，
//      而那是錢。⇒ 不合法就**拒絕並說出哪個字元**，讓人去處理。
//   ② **正規化落在寫入端，不落在讀取端。** 讀取端比對（case-insensitive compare）的失效樣子是
//      「每個讀取端各自記得要不要忽略大小寫」，而漏掉的那個不會報錯 —— 它只是查不到，
//      跟「這個帳號不存在」同形。
//   ③ **id 與顯示名分開。** 顯示名要大寫要空白都行；id 是檔名與帳本的鍵，只有一種寫法。
// ⚠ 方言：C# 9 / netstandard2.1 / 零第三方（SCP_Core 的第一條規矩）——
//   這裡沒有 ImplicitUsings，using 要自己寫齊。
using System;
using System.Collections.Generic;

namespace SCP.Core.Bank
{
    /// <summary>正規化的結果 —— 三態，⛔ 不是「回 null 當失敗」（那會讓原因消失）。</summary>
    public readonly struct SCP_BankIdResult
    {
        /// <summary>正規化後的 id；<see cref="Ok"/> 為 false 時是空字串。</summary>
        public readonly string Id;

        public readonly bool Ok;

        /// <summary>不合法的原因（Ok 為 false 時一定有話說；空字串是 bug）。</summary>
        public readonly string Why;

        /// <summary>輸入跟正規化後**不一樣**（例如有大寫）—— 不是錯，但呼叫端多半要印出來讓人看見。</summary>
        public readonly bool Changed;

        SCP_BankIdResult(string iId, bool iOk, string iWhy, bool iChanged)
        { Id = iId; Ok = iOk; Why = iWhy; Changed = iChanged; }

        public static SCP_BankIdResult Good(string iId, bool iChanged) => new SCP_BankIdResult(iId, true, "", iChanged);
        public static SCP_BankIdResult Bad(string iWhy) => new SCP_BankIdResult("", false, iWhy, false);
    }

    public static class SCP_BankId
    {
        /// <summary>id 的長度上限 —— 它要當檔名，而路徑長度是有限的。</summary>
        public const int MaxLength = 64;

        /// <summary>
        /// 正規化並驗證一個帳號 id。
        /// <para>規則：**全小寫**；只收 <c>a-z 0-9 . _ -</c>；開頭必須是字母或數字；長度 1〜<see cref="MaxLength"/>。</para>
        /// <para>⛔ 不做字元替換 —— 不合法就拒絕（判準①）。</para>
        /// </summary>
        public static SCP_BankIdResult Normalize(string? iRaw)
        {
            if (string.IsNullOrWhiteSpace(iRaw)) return SCP_BankIdResult.Bad("帳號 id 是空的");

            string aTrim = iRaw.Trim();
            if (aTrim.Length > MaxLength)
                return SCP_BankIdResult.Bad($"帳號 id 太長（{aTrim.Length} > {MaxLength}）：'{aTrim}'");

            // ⚠ 只轉大小寫。ToLowerInvariant 而不是 ToLower：後者跟著文化走，
            //   而土耳其文的 'I' 會變成 'ı'（不是 'i'）⇒ 同一個 id 在不同機器上正規化成兩個。
            string aLower = aTrim.ToLowerInvariant();

            for (int i = 0; i < aLower.Length; i++)
            {
                char c = aLower[i];
                bool aAlnum = (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9');
                if (aAlnum) continue;
                if (i > 0 && (c == '.' || c == '_' || c == '-')) continue;

                if (i == 0)
                    return SCP_BankIdResult.Bad($"帳號 id 的第一個字元必須是字母或數字：'{aTrim}'（開頭是 '{c}'）");
                return SCP_BankIdResult.Bad(
                    $"帳號 id 含不合法字元 '{c}'（第 {i + 1} 個字）：'{aTrim}'"
                    + "　⇒ 只收 a-z 0-9 . _ -　⛔ 本層**不替你換掉它**：換字元等於靜默把兩個帳號併成一個，而那是錢");
            }

            return SCP_BankIdResult.Good(aLower, !string.Equals(aLower, aTrim, StringComparison.Ordinal));
        }

        /// <summary>
        /// 兩個 id 是不是**同一個帳號**（正規化後相等）。
        /// <para>⚠ 給的是「這兩個字串指同一個帳號嗎」的答案，⛔ 不是「可以拿來當比較器」——
        /// 讀取端不該自己比，它該拿正規化後的 id 去查（判準②）。</para>
        /// </summary>
        public static bool SameAccount(string? iA, string? iB)
        {
            SCP_BankIdResult a = Normalize(iA), b = Normalize(iB);
            return a.Ok && b.Ok && string.Equals(a.Id, b.Id, StringComparison.Ordinal);
        }

        /// <summary>
        /// 一批舊 id 依正規化後的 id 分組 —— **遷移時用來找「只差大小寫」的那幾組**（C4）。
        /// <para>回傳：canonical id → 該組的原始寫法（依原字串排序，去重）。只回**有一組以上原始寫法**的那些，
        /// 也就是真的需要人看一眼的那幾組。</para>
        /// </summary>
        public static Dictionary<string, List<string>> GroupCollisions(IEnumerable<string> iRawIds)
        {
            var aAll = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (string aRaw in iRawIds)
            {
                SCP_BankIdResult aR = Normalize(aRaw);
                if (!aR.Ok) continue;                 // 不合法的另外報，⛔ 不混進撞名表
                if (!aAll.TryGetValue(aR.Id, out List<string>? aList))
                { aList = new List<string>(); aAll[aR.Id] = aList; }
                // ⚠ 用 List.Contains（Ordinal 相等）而不是 LINQ 的多載：SCP_Core 不引 System.Linq，
                //   而 `string` 的預設相等本來就是 ordinal。
                if (!aList.Contains(aRaw)) aList.Add(aRaw);
            }

            var aOut = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (KeyValuePair<string, List<string>> kv in aAll)
            {
                if (kv.Value.Count < 2) continue;
                kv.Value.Sort(StringComparer.Ordinal);
                aOut[kv.Key] = kv.Value;
            }
            return aOut;
        }

        /// <summary>把一個 id 變成人看的字（給錯誤訊息用）—— 空字串要看得出來是空的。</summary>
        public static string Describe(string? iRaw)
            => string.IsNullOrEmpty(iRaw) ? "（空）" : "'" + iRaw + "'";
    }
}
