// 區塊職責：**@mention → 對方 inbox 通知** —— 寫入不變量的唯一實作（TASK-0299）。
// 物理意義：「任何進到房間的訊息都該觸發提及通知」跟來源無關 ⇒ 規則掛在**寫入端**：
//           Senate `Cmd_TavernWrite` 寫完呼叫（TASK-0299）—— ⛔ 不在發文入口另寫一份。
// 數值影響：解析、白名單、條目標題與內文（2026-09-25 起）：
//   · `@([a-zA-Z0-9_-]+)` 取名字；不通知自己（sender_id 與 sender_persona 都比）、`_` 開頭系統 id、白名單外的名字
//   · TASK-0365（2026-10-05）加三格：① **程式碼區段裡的 @ 不算點名**（``` 區塊與 `行內`）——
//     09-29 有人在留言裡**引用** `@酒保` 被當成點名；② 全形 `＠` 與半形 `@` 同義；
//     ③ **別名表**（`mention_aliases.json`，例 `酒保 → tavern-keeper`）：別名可以是中文，所以不能只靠 ASCII 正則 ——
//     `@` 後面先比別名（長的先比）；ASCII 別名後面不能緊接 ASCII 名字字元，中文別名不看下一個字（`@酒保幫我調一杯` 算）。
//   · 白名單 ＝ identities.json 的 id ∪ persona 池（letters 底下有 profile/ 的目錄）
//   · 標題「💬 <persona 或顯示名> @妳 <📱/[tag]/↩seq/📎N>」；內文引用前 200 字、截斷時附原訊息檔路徑（只印寫入端交來的真路徑）
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Tavern
{
    /// <summary>通知的輸入：一則**已經落檔、拿到 seq** 的訊息（呼叫端從自己的訊息型別轉進來）。</summary>
    public sealed class SCP_MentionInput
    {
        public string Room = "";
        public int Seq;
        public string SenderId = "";
        public string SenderName = "";
        public string SenderPersona = "";
        public string Body = "";
        public IReadOnlyDictionary<string, string>? Meta;
        public int? ReplyTo;
        public int RefsCount;
        /// <summary>這則實際寫出的訊息檔（絕對路徑）；空 ＝ 呼叫端沒給 ⇒ 只印 seq，⛔ 不由 seq 反推編造。</summary>
        public string MsgFilePath = "";

        public static SCP_MentionInput From(SCP_TavernMessage iMsg, string iRoom, int iSeq, string iMsgFilePath)
            => new SCP_MentionInput
            {
                Room = iRoom, Seq = iSeq, SenderId = iMsg.SenderId, SenderName = iMsg.SenderName,
                SenderPersona = iMsg.SenderPersona, Body = iMsg.Body, Meta = iMsg.Meta, ReplyTo = iMsg.ReplyTo,
                RefsCount = iMsg.Refs?.Count ?? 0, MsgFilePath = iMsgFilePath,
            };
    }

    public sealed class SCP_MentionResult
    {
        public List<string> Notified = new List<string>();
        public List<string> Duplicates = new List<string>();
        public List<string> Skipped = new List<string>();
        public List<string> Failures = new List<string>();
    }

    public static class SCP_TavernMentions
    {
        static readonly Regex s_RxName = new Regex(@"\G[a-zA-Z0-9_-]+");
        static readonly Regex s_RxFence = new Regex(@"```[\s\S]*?```");
        static readonly Regex s_RxInlineCode = new Regex(@"`[^`\r\n]*`");

        /// <summary>把程式碼區段（``` 區塊、`行內`）換成空白 —— 裡面的 @ 是引用，不是點名。</summary>
        public static string StripCode(string iBody)
            => s_RxInlineCode.Replace(s_RxFence.Replace(iBody ?? "", " "), " ");

        static bool IsAsciiName(char c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '_' || c == '-';

        /// <summary>
        /// 這段內文 @ 到哪些 id（去重、照出現順序）。別名先換成本名；⛔ 不在這裡判白名單（那是 Notify 的事）。
        /// <paramref name="iAliases"/> 為 null ＝ 不用別名。
        /// </summary>
        public static List<string> Extract(string iBody, IReadOnlyDictionary<string, string>? iAliases)
        {
            var aOut = new List<string>();
            string s = StripCode(iBody);
            List<KeyValuePair<string, string>> aByLength = iAliases == null
                ? new List<KeyValuePair<string, string>>()
                : new List<KeyValuePair<string, string>>(iAliases);
            aByLength.Sort((x, y) => y.Key.Length.CompareTo(x.Key.Length));
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '@' && s[i] != '＠') continue;
                int aStart = i + 1;
                string? aId = null;
                foreach (KeyValuePair<string, string> kv in aByLength)
                {
                    int aEnd = aStart + kv.Key.Length;
                    if (kv.Key.Length == 0 || aEnd > s.Length) continue;
                    if (string.Compare(s, aStart, kv.Key, 0, kv.Key.Length, StringComparison.OrdinalIgnoreCase) != 0) continue;
                    // ASCII 別名只是更長的名字的開頭（@barte → @bartender2）⇒ 不算；
                    // 中文別名不看下一個字 —— 中文不加空格，`@酒保幫我調一杯` 要算（代價：`@酒保們` 也算）
                    if (IsAsciiName(kv.Key[kv.Key.Length - 1]) && aEnd < s.Length && IsAsciiName(s[aEnd])) continue;
                    aId = kv.Value;
                    i = aEnd - 1;
                    break;
                }
                if (aId == null)
                {
                    Match m = s_RxName.Match(s, aStart);
                    if (!m.Success) continue;
                    aId = m.Value;
                    i = aStart + m.Length - 1;
                    if (iAliases != null && iAliases.TryGetValue(aId, out string? aMapped)) aId = aMapped;
                }
                if (!aOut.Contains(aId)) aOut.Add(aId);
            }
            return aOut;
        }
        /// <summary>已知中繼來源前綴（白名單而非黑名單 —— meta.source 是自由字串，後台來源也會寫它）。</summary>
        static readonly string[] s_RelayPrefixes = { "discord", "line", "telegram", "webhook" };

        /// <summary>訊息是否源自系統外部中繼（Discord 等）。</summary>
        public static bool IsExternalRelay(IReadOnlyDictionary<string, string>? iMeta, string? iSenderId)
        {
            if (iMeta != null && iMeta.TryGetValue("source", out string? aSrc) && !string.IsNullOrEmpty(aSrc))
            {
                string aLower = aSrc!.ToLowerInvariant();
                foreach (string p in s_RelayPrefixes) if (aLower.StartsWith(p)) return true;
            }
            return !string.IsNullOrEmpty(iSenderId) && iSenderId!.StartsWith("discord:");
        }

        /// <summary>白名單：identities.json 的 id ∪ persona 池。每次呼叫重讀（檔小；⛔ 不做跨呼叫快取 —— 快取過期時的症狀是「新同事 @ 不到」且不叫）。</summary>
        public static HashSet<string> Whitelist(string iDataRoot)
        {
            var aSet = new HashSet<string>(StringComparer.Ordinal);
            foreach (string aId in SCP_TavernRooms.LoadIdentities(iDataRoot).Keys) aSet.Add(aId);
            foreach (string aP in SCP_PersonaProfile.PoolNames(SCP_DataPaths.Letters(new SCP_DataRoot(iDataRoot)).Value)) aSet.Add(aP);
            return aSet;
        }

        /// <summary>
        /// 通知這則訊息 @ 到的人。<paramref name="iRepoRoot"/> 用來把訊息檔路徑印成 repo 相對（跨機器可讀）；
        /// 拿不到或不在它底下 ⇒ 原樣印。失敗逐人記在結果裡，⛔ 不丟例外（訊息已經寫進去了）。
        /// </summary>
        public static SCP_MentionResult Notify(string iDataRoot, SCP_MentionInput iIn, string iRepoRoot, Action<string>? iLog = null)
        {
            var aRes = new SCP_MentionResult();
            if (string.IsNullOrEmpty(iIn.Body) || iIn.Seq <= 0) return aRes;
            if (iIn.Body.IndexOf('@') < 0 && iIn.Body.IndexOf('＠') < 0) return aRes;
            Dictionary<string, string> aAliases = SCP_TavernMentionAliases.Load(iDataRoot, out string? aAliasErr);
            // 別名表讀不了 ⇒ 本名照常通知，但要出聲（⛔ 不讓「@酒保 沒送到」安靜發生）
            if (aAliasErr != null) aRes.Failures.Add("別名表讀不了，這一則只認本名：" + aAliasErr);
            List<string> aMentioned = Extract(iIn.Body, aAliases);
            if (aMentioned.Count == 0) return aRes;

            HashSet<string> aValid;
            try { aValid = Whitelist(iDataRoot); }
            catch (Exception e) { aRes.Failures.Add("白名單讀不了：" + e.Message + " ⇒ 這一則**沒有通知任何人**"); return aRes; }

            string aSenderId = iIn.SenderId ?? "";
            string aSenderName = !string.IsNullOrEmpty(iIn.SenderName) ? iIn.SenderName : aSenderId;
            string aSenderLabel = !string.IsNullOrEmpty(iIn.SenderPersona) ? iIn.SenderPersona : aSenderName;
            var aMarks = new List<string>();
            if (IsExternalRelay(iIn.Meta, aSenderId)) aMarks.Add("📱");
            if (iIn.Meta != null && iIn.Meta.TryGetValue("tag", out string? aTag) && !string.IsNullOrEmpty(aTag)) aMarks.Add($"[{aTag}]");
            if (iIn.ReplyTo.HasValue && iIn.ReplyTo.Value > 0) aMarks.Add($"↩seq={iIn.ReplyTo.Value}");
            if (iIn.RefsCount > 0) aMarks.Add($"📎{iIn.RefsCount}");
            string aMarkStr = aMarks.Count > 0 ? " " + string.Join(" ", aMarks) : "";
            bool aTruncated = iIn.Body.Length > 200;
            string aQuoted = aTruncated ? iIn.Body.Substring(0, 200) + "…" : iIn.Body;
            string aTail = "";
            if (aTruncated)
            {
                string aRel = ToRepoRelative(iIn.MsgFilePath, iRepoRoot);
                aTail = string.IsNullOrEmpty(aRel) ? $"（全文 seq={iIn.Seq}）" : $"（全文 seq={iIn.Seq} — 完整原文請讀 `{aRel}`）";
            }
            string aTitle = $"💬 {aSenderLabel} @妳{aMarkStr}";
            string aBody = $"> {aQuoted}\n\n建議前往 `{iIn.Room}` 房回覆{aTail}";

            foreach (string aTarget in aMentioned)
            {
                if (aTarget == aSenderId) { aRes.Skipped.Add(aTarget + "（自己）"); continue; }
                if (!string.IsNullOrEmpty(iIn.SenderPersona) && aTarget == iIn.SenderPersona) { aRes.Skipped.Add(aTarget + "（自己）"); continue; }
                if (aTarget.StartsWith("_")) { aRes.Skipped.Add(aTarget + "（系統 id）"); continue; }
                if (!aValid.Contains(aTarget)) { aRes.Skipped.Add(aTarget + "（白名單外）"); continue; }
                try
                {
                    SCP_InboxAppendOutcome o = SCP_TavernInbox.Append(iDataRoot, iIn.Room, aTarget, iIn.Seq, aTitle, aBody, iLog);
                    if (o == SCP_InboxAppendOutcome.Duplicate) aRes.Duplicates.Add(aTarget); else aRes.Notified.Add(aTarget);
                }
                catch (Exception e) { aRes.Failures.Add($"{aTarget}：{e.GetType().Name}: {e.Message}"); }
            }
            return aRes;
        }

        /// <summary>絕對路徑 → repo 相對（跨機器可讀）。不在 repo 底下 ⇒ 原樣（正斜線）；空 ⇒ 空。</summary>
        public static string ToRepoRelative(string? iAbsPath, string? iRepoRoot)
        {
            if (string.IsNullOrEmpty(iAbsPath)) return "";
            try
            {
                string aFull = Path.GetFullPath(iAbsPath).Replace('\\', '/');
                if (string.IsNullOrEmpty(iRepoRoot)) return aFull;
                string aRoot = Path.GetFullPath(iRepoRoot).Replace('\\', '/').TrimEnd('/') + "/";
                return aFull.StartsWith(aRoot, StringComparison.OrdinalIgnoreCase) ? aFull.Substring(aRoot.Length) : aFull;
            }
            catch { return iAbsPath!.Replace('\\', '/'); }
        }
    }
}
