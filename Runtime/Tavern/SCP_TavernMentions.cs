// 區塊職責：**@mention → 對方 inbox 通知** —— 寫入不變量的唯一實作（TASK-0299）。
// 物理意義：「任何進到房間的訊息都該觸發提及通知」跟來源無關（2026-07-29 就是因此從 Op_Post 下沉到 AppendMessage）。
//           但 AppendMessage 住在 Editor ⇒ 直打 `tavern-write` 的訊息沒人通知（TASK-0299 重現：seq 202 inbox 0→0）。
//           ⇒ 規則搬到這裡、掛在**寫入端**：server 模式由 Senate `Cmd_TavernWrite` 寫完呼叫，
//             editor 模式由 Editor 本地寫完呼叫 —— 兩邊同一支，⛔ 不留兩份。
// 數值影響：解析、白名單、條目標題與內文**逐字搬自 Editor 版 `UCL_ChatTavernIO.NotifyMentions`（2026-09-25）**：
//   · `@([a-zA-Z0-9_-]+)` 取名字；不通知自己（sender_id 與 sender_persona 都比）、`_` 開頭系統 id、白名單外的名字
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
    /// <summary>通知的輸入：一則**已經落檔、拿到 seq** 的訊息（兩個宿主各自從自己的訊息型別轉進來）。</summary>
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
        static readonly Regex s_RxMention = new Regex(@"@([a-zA-Z0-9_-]+)");
        /// <summary>已知中繼來源前綴（白名單而非黑名單 —— meta.source 是自由字串，後台來源也會寫它；沿革見 git 歷史 UCL_ChatTavernIO）。</summary>
        static readonly string[] s_RelayPrefixes = { "discord", "line", "telegram", "webhook" };

        /// <summary>訊息是否源自系統外部中繼（Discord 等）。語意逐條搬自 Editor 版 `IsExternalRelay`。</summary>
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
            MatchCollection aMatches = s_RxMention.Matches(iIn.Body);
            if (aMatches.Count == 0) return aRes;
            var aMentioned = new List<string>();
            foreach (Match m in aMatches) if (!aMentioned.Contains(m.Groups[1].Value)) aMentioned.Add(m.Groups[1].Value);

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
