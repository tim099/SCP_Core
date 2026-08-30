// 區塊職責：agent 入口檔（CLAUDE.md / AGENTS.md…）的**受管區塊**解析與合成 —— 全部是純函式。
// 物理意義：入口檔是**使用者的檔**，不是我們的鏡像。所以安裝不能整檔覆寫，
//           只能在檔尾維護一段成對 marker 夾住的區塊，其餘一個字都不動。
//           ⇒ 本檔不碰磁碟：字串進、字串出。IO 與「哪個檔」是呼叫端的事。
//
// 📌 設計拍板（Tim 2026-08-30）：
//   ① marker 前綴 `SCP_CORE`（**選定後不可改** —— 改了等於全世界既有安裝一次孤兒化）
//   ② **成對** BEGIN/END，不是單一分隔線。單一分隔線的語意只能是「這行以後都是我的」，
//     於是更新 ＝ 砍到檔尾重寫 ⇒ **使用者在檔尾補的東西會被無聲吃掉**，而人天生就是往檔尾補東西。
//   ③ `END` 之後的內容**原樣保留**
//   ④ 檔案不存在 ⇒ 新建，使用者區為空、檔頭就是 BEGIN
//
// ⚠ 為什麼用 HTML 註解當機器邊界，不用一行看得見的分隔線：
//   CommonMark 裡**整行只有 `-` 或只有 `=` 的一行會把上一行變成標題**（setext heading）。
//   一個「簡化成 `---`」的分隔線，會在使用者最後一行下面把那行悄悄變成 H2，而檔案看起來完全正常。
//   HTML 註解沒有這個語法面，而且渲染時隱形、能帶 key=value、agent 讀原文仍看得到。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SCP.Core.Entry
{
    /// <summary>入口檔目前的狀態。⚠ 每一態都要有畫面上的字 —— 不可以摺回「Stale」。</summary>
    public enum SCP_EntryState
    {
        /// <summary>檔不存在，或檔在但沒有任何 marker、也不像舊版整檔安裝。</summary>
        NotInstalled = 0,

        /// <summary>受管區塊在，內容與來源一致。</summary>
        Synced = 1,

        /// <summary>受管區塊在，內容與來源不同 ⇒ 來源更新了，可以直接覆寫那一段。</summary>
        Stale = 2,

        /// <summary>
        /// 受管區塊被**手動改過**（內容 hash 對不上 marker 上記的 sha）。
        /// <para>⚠ 跟 Stale 不同形：Stale 覆寫是安全的，這個覆寫會吃掉人寫的字 ⇒ 要 force。</para>
        /// </summary>
        LocalEdit = 3,

        /// <summary>只有 BEGIN 沒有 END、或 END 在 BEGIN 前面。**停手**，不要猜他想放哪。</summary>
        MarkerBroken = 4,

        /// <summary>找到 ≥2 個受管區塊。**停手並指名** —— 挑第一個修會留下另一個還在生效。</summary>
        Duplicated = 5,

        /// <summary>沒有 marker，但整份內容就是舊版的整檔安裝 ⇒ 可以原地包起來（不是重複 append）。</summary>
        NeedsMigration = 6,
    }

    /// <summary>一次解析的結果。</summary>
    public sealed class SCP_EntryParse
    {
        public SCP_EntryState State { get; internal set; }

        /// <summary>受管區塊的內文（不含 marker 行）。只有找到區塊時有意義。</summary>
        public string Managed { get; internal set; } = "";

        /// <summary>marker 上記的 sha（上次寫入時的內容 hash）。空 ＝ 舊格式或沒記。</summary>
        public string RecordedSha { get; internal set; } = "";

        /// <summary>受管區塊之前的使用者內容。</summary>
        public string Before { get; internal set; } = "";

        /// <summary>受管區塊之後的使用者內容（⚠ 一定要保留）。</summary>
        public string After { get; internal set; } = "";

        /// <summary>人可讀的說明（每一態都要有話說；Synced 也要）。</summary>
        public string Detail { get; internal set; } = "";
    }

    public static class SCP_EntryDoc
    {
        /// <summary>marker 前綴 —— **選定後不可改**（改了既有安裝全部變孤兒）。</summary>
        public const string Prefix = "SCP_CORE";

        public const string BeginToken = "<!-- " + Prefix + ":BEGIN";
        public const string EndToken = "<!-- " + Prefix + ":END -->";

        /// <summary>marker 的格式版本（將來要改區塊格式時，靠它分辨舊檔）。</summary>
        public const int FormatVersion = 1;

        const string kNotice =
            "<!-- 本區塊自動產生，手改會在下次同步被覆蓋。專案規則請寫在本區塊「之外」。 -->";

        // ── 合成 ──────────────────────────────────────────────────

        /// <summary>算內容 hash（marker 上記的 sha）。取 SHA-256 前 12 個 hex。</summary>
        public static string Sha(string iContent)
        {
            using (var aSha = SHA256.Create())
            {
                byte[] aBytes = aSha.ComputeHash(Encoding.UTF8.GetBytes(Normalize(iContent)));
                var aSb = new StringBuilder(12);
                for (int i = 0; i < 6; i++) aSb.Append(aBytes[i].ToString("x2", CultureInfo.InvariantCulture));
                return aSb.ToString();
            }
        }

        /// <summary>
        /// 把受管內容寫進（或更新）入口檔文字。
        /// <para>⚠ 這支是**純函式**：它不知道有沒有 LocalEdit、該不該 force。
        /// 那是呼叫端讀完 <see cref="Parse"/> 之後的決定。</para>
        /// </summary>
        public static string Apply(string? iExisting, string iManaged, string iTarget, string iSource)
        {
            // ⚠ 這裡**必須**把 iManaged 傳給 Parse。
            // 🩸 2026-08-30 第一版傳 null ⇒ `LooksLikeLegacyWholeFile` 的判斷被閘門擋掉
            //   ⇒ 舊版整檔安裝被判成 NotInstalled ⇒ 在自己下面又 append 一份，
            //   **同一份規則出現兩次，而兩份都是真的、沒有一層報錯**。
            //   （selftest「入口檔異常形狀」的「遷移後不重複」那格抓到的就是它。）
            SCP_EntryParse aParse = Parse(iExisting, iManaged);
            string aBlock = BuildBlock(iManaged, iTarget, iSource);

            // 有區塊 ⇒ 換掉那一段，前後原樣保留
            if (aParse.State == SCP_EntryState.Synced || aParse.State == SCP_EntryState.Stale
                || aParse.State == SCP_EntryState.LocalEdit)
                return Join(aParse.Before, aBlock, aParse.After);

            // 舊版整檔安裝 ⇒ 原地包起來（使用者區留空）—— **不是**在下面再 append 一份
            if (aParse.State == SCP_EntryState.NeedsMigration)
                return Join("", aBlock, "");

            // 沒有區塊 ⇒ 接在現有內容之後（檔不存在時 Before 為空 ⇒ 檔頭就是 BEGIN）
            return Join(Normalize(iExisting ?? ""), aBlock, "");
        }

        /// <summary>把受管區塊切掉，前後的使用者內容原樣保留。沒有區塊就原樣回傳。</summary>
        public static string Remove(string? iExisting)
        {
            SCP_EntryParse aParse = Parse(iExisting, null);
            if (aParse.State != SCP_EntryState.Synced && aParse.State != SCP_EntryState.Stale
                && aParse.State != SCP_EntryState.LocalEdit)
                return Normalize(iExisting ?? "");

            string aBefore = aParse.Before.TrimEnd('\n');
            string aAfter = aParse.After.TrimStart('\n');
            if (aBefore.Length == 0) return aAfter.Length == 0 ? "" : aAfter + "\n";
            if (aAfter.Length == 0) return aBefore + "\n";
            return aBefore + "\n\n" + aAfter + "\n";
        }

        static string BuildBlock(string iManaged, string iTarget, string iSource)
        {
            string aBody = Normalize(iManaged).Trim('\n');
            // ⚠ marker 上**不放時間戳、不放 commit** —— 放了每次同步都產生 git diff，
            //   而入口檔是入版控的檔。sha 只跟著內容變。
            string aHead = BeginToken
                           + " v=" + FormatVersion.ToString(CultureInfo.InvariantCulture)
                           + " target=" + iTarget
                           + " src=" + iSource
                           + " sha=" + Sha(aBody)
                           + " -->";
            return aHead + "\n" + kNotice + "\n\n" + aBody + "\n\n" + EndToken;
        }

        static string Join(string iBefore, string iBlock, string iAfter)
        {
            var aSb = new StringBuilder();
            string aB = iBefore.TrimEnd('\n');
            if (aB.Length > 0) aSb.Append(aB).Append("\n\n");
            aSb.Append(iBlock).Append('\n');
            string aA = iAfter.TrimStart('\n').TrimEnd('\n');
            if (aA.Length > 0) aSb.Append('\n').Append(aA).Append('\n');
            return aSb.ToString();
        }

        // ── 解析 ──────────────────────────────────────────────────

        /// <summary>
        /// 讀一份入口檔文字，判定它現在是什麼狀態。
        /// </summary>
        /// <param name="iExisting">現有檔案內容；null ＝ 檔不存在。</param>
        /// <param name="iExpectedManaged">
        /// 來源目前的受管內容。給了才分得出 Synced／Stale；不給（null）時兩者都回 Stale。
        /// </param>
        public static SCP_EntryParse Parse(string? iExisting, string? iExpectedManaged)
        {
            var aOut = new SCP_EntryParse();
            if (iExisting == null)
            {
                aOut.State = SCP_EntryState.NotInstalled;
                aOut.Detail = "檔案不存在 —— 安裝會新建一份（使用者區為空，檔頭就是 BEGIN）";
                return aOut;
            }

            string aText = Normalize(iExisting);

            int aBeginCount = CountOf(aText, BeginToken);
            int aEndCount = CountOf(aText, EndToken);

            if (aBeginCount > 1 || aEndCount > 1)
            {
                aOut.State = SCP_EntryState.Duplicated;
                aOut.Detail = $"找到 {aBeginCount} 個 BEGIN／{aEndCount} 個 END —— **停手**。"
                              + "挑一個修會留下另一個還在生效，而畫面會顯示綠燈。請人工併成一段。";
                return aOut;
            }

            if (aBeginCount == 0 && aEndCount == 0)
            {
                if (iExpectedManaged != null && LooksLikeLegacyWholeFile(aText, iExpectedManaged))
                {
                    aOut.State = SCP_EntryState.NeedsMigration;
                    aOut.Detail = "沒有 marker，但整份內容就是舊版的整檔安裝 ⇒ 可以原地包起來（不會多出第二份）";
                    return aOut;
                }
                aOut.State = SCP_EntryState.NotInstalled;
                aOut.Before = aText;
                aOut.Detail = aText.Trim().Length == 0
                    ? "檔案是空的 —— 安裝會寫進一段受管區塊"
                    : "檔案有內容但沒有受管區塊 —— 安裝會把區塊接在現有內容之後（現有內容一個字都不動）";
                return aOut;
            }

            if (aBeginCount != 1 || aEndCount != 1)
            {
                aOut.State = SCP_EntryState.MarkerBroken;
                aOut.Detail = $"marker 不成對（BEGIN {aBeginCount} 個／END {aEndCount} 個）—— "
                              + "**停手**，不要猜受管區塊到哪裡結束";
                return aOut;
            }

            int aBeginAt = aText.IndexOf(BeginToken, StringComparison.Ordinal);
            int aEndAt = aText.IndexOf(EndToken, StringComparison.Ordinal);
            if (aEndAt < aBeginAt)
            {
                aOut.State = SCP_EntryState.MarkerBroken;
                aOut.Detail = "END 出現在 BEGIN 之前 —— **停手**（這不是我們寫得出來的形狀）";
                return aOut;
            }

            int aHeadEnd = aText.IndexOf("-->", aBeginAt, StringComparison.Ordinal);
            if (aHeadEnd < 0 || aHeadEnd > aEndAt)
            {
                aOut.State = SCP_EntryState.MarkerBroken;
                aOut.Detail = "BEGIN 那一行沒有收尾的 `-->` —— **停手**";
                return aOut;
            }

            string aHead = aText.Substring(aBeginAt, aHeadEnd + 3 - aBeginAt);
            aOut.RecordedSha = AttrOf(aHead, "sha");
            aOut.Before = aText.Substring(0, aBeginAt);
            aOut.After = aText.Substring(aEndAt + EndToken.Length);

            string aInner = aText.Substring(aHeadEnd + 3, aEndAt - (aHeadEnd + 3));
            aOut.Managed = StripNotice(aInner).Trim('\n');

            string aActualSha = Sha(aOut.Managed);
            bool aTouched = aOut.RecordedSha.Length > 0 && aActualSha != aOut.RecordedSha;

            if (aTouched)
            {
                aOut.State = SCP_EntryState.LocalEdit;
                aOut.Detail = $"受管區塊被手改過（marker 記 sha={aOut.RecordedSha}，現在是 {aActualSha}）"
                              + " —— 覆寫會吃掉那些字，要 force 才動";
                return aOut;
            }

            if (iExpectedManaged == null)
            {
                aOut.State = SCP_EntryState.Stale;
                aOut.Detail = "找到受管區塊（沒有給來源內容，所以沒判斷是不是最新）";
                return aOut;
            }

            string aExpect = Normalize(iExpectedManaged).Trim('\n');
            if (aExpect == aOut.Managed)
            {
                aOut.State = SCP_EntryState.Synced;
                aOut.Detail = $"受管區塊與來源一致（sha={aActualSha}）"
                              + (aOut.After.Trim().Length > 0 ? "；END 之後另有使用者內容，會原樣保留" : "");
                return aOut;
            }

            aOut.State = SCP_EntryState.Stale;
            aOut.Detail = $"來源更新了（現在 sha={aActualSha}，來源 sha={Sha(aExpect)}）—— 覆寫只動這一段";
            return aOut;
        }

        // ── 小工具 ────────────────────────────────────────────────

        /// <summary>
        /// 行尾正規化成 `\n`。
        /// <para>⚠ 比對與寫入必須用同一份正規化過的字串 —— 🩸 install_skills.py 那族踩過三次，
        /// 其中最難查的一次是「比對時看不見自己造成的差異」⇒ 壞掉的那份永遠被跳過。</para>
        /// </summary>
        public static string Normalize(string? iText)
        {
            if (string.IsNullOrEmpty(iText)) return "";
            return iText!.Replace("\r\n", "\n").Replace("\r", "\n");
        }

        static int CountOf(string iText, string iToken)
        {
            int n = 0, i = 0;
            while ((i = iText.IndexOf(iToken, i, StringComparison.Ordinal)) >= 0) { n++; i += iToken.Length; }
            return n;
        }

        static string AttrOf(string iHead, string iName)
        {
            string aKey = " " + iName + "=";
            int i = iHead.IndexOf(aKey, StringComparison.Ordinal);
            if (i < 0) return "";
            i += aKey.Length;
            int j = i;
            while (j < iHead.Length && !char.IsWhiteSpace(iHead[j])) j++;
            return iHead.Substring(i, j - i);
        }

        static string StripNotice(string iInner)
        {
            int i = iInner.IndexOf(kNotice, StringComparison.Ordinal);
            return i < 0 ? iInner : iInner.Substring(i + kNotice.Length);
        }

        // 區塊職責：判斷「這份檔就是舊版整檔安裝」。
        // 物理意義: 🩸 這一格是遷移的關鍵：本 repo 的 CLAUDE.md 實測**整份就是 template**
        //          （2026-08-30 diff 逐字相同）。天真實作會判「整份都是使用者內容」然後在下面
        //          再 append 一份 ⇒ 同一份規則出現兩次，而兩份都是真的、沒有一層會報錯。
        // 數值影響: 判準刻意**嚴格**（正規化後逐字相同才算）—— 判錯的代價不對稱：
        //          誤判成 migration 會吃掉使用者的字，誤判成 not-installed 只是多一段要人工收。
        static bool LooksLikeLegacyWholeFile(string iText, string iExpectedManaged)
        {
            string a = iText.Trim('\n', ' ', '\t');
            string b = Normalize(iExpectedManaged).Trim('\n', ' ', '\t');
            return a.Length > 0 && a == b;
        }
    }
}
