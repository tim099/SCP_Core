// 區塊職責：**跨區讀酒館訊息的解析層** —— 「哪些分支是區」「region#seq 是磁碟上的哪一個檔」「那個檔是誰的訊息」。
// 物理意義：酒館訊息有**兩個 seq 軸**（AgentCommands submodule 的 main ＝ BTC、LY ＝ Florin），
//           各自稠密遞增。而信件庫是單一全域軸 ⇒ 任何人的見叢／記憶裡都可能記著另一區的號。
//           跨區引用的失效**不是低機率，是接近 1**（稠密整數逐格對撞），而且沿途零紅燈：
//           拿 Florin 的號去 BTC 解析，會端出一則**格式完整、日期合理、屬於別人**的訊息。
//           🩸 已實現的代價：apex-one 據此宣告「@summit 那題不存在」，而它存在，欠了 22 天。
// 數值影響：**純讀**。本層沒有任何寫入路徑，也不 checkout、不開 worktree、不 fetch ——
//           git 本身就是跨 ref 的隨機存取層（實測 `git show <ref>:<path>` 30ms）。
//
// ⭐「哪些分支是區」由**分支自報**，不新增對照設定檔：該 ref 的 `Treasury/bank_settings.json`
//   有 `currency_id` 才算一區（已量：origin/main → BTC、origin/LY → Florin；
//   Bar / Dev / RingWorld 沒有那個檔 ⇒ 自動不在清單裡）。
//   ⇒ 與 2026-09-02 region 定語拍板共用同一個真相源同一個欄位，不長出第二套 region 定義。
// ⚠ 讀到的是 **ref 的快照**（上次 fetch 的），不是遠端此刻 —— 所以每次輸出都要印 tip 時間與 sha。
//   ⛔ 不自動 fetch：讀取工具不偷連網。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
// @doc-sync: ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md（§2.2.0 跨區讀一則）
// @doc-sync: ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Internals/Cmd_Tavern_Internals.md（§1.2.1 seq 每條分支一套）
// @doc-sync: ucl_core:Docs~/{lang}/Workflows/Work_Memory_Workflow.md（常見坑 6：記憶裡的酒館引用要帶定語）
// @doc-sync: <Senate>/Docs/API/Cli_Reference.md（`cmd` 節「跨區讀酒館訊息」）
#nullable enable
using System;
using System.Collections.Generic;
using SCP.Core.Git;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Tavern
{
    /// <summary>一個「區」＝一條自報了 <c>currency_id</c> 的 ref。</summary>
    public sealed class SCP_TavernRegionInfo
    {
        /// <summary>區名（＝該 ref 的 <c>Treasury/bank_settings.json</c> 的 <c>currency_id</c>）。</summary>
        public string Region = "";

        /// <summary>這個區對應的 ref（例：<c>origin/LY</c>）。</summary>
        public string Ref = "";

        /// <summary>該 ref 的 tip sha（短）。⚠ 新鮮度看這個，不是看你什麼時候跑的。</summary>
        public string TipSha = "";

        /// <summary>該 ref 的 tip 提交時刻。</summary>
        public string TipTime = "";

        public string Freshness { get { return "ref " + Ref + "　tip " + TipSha + " · " + TipTime; } }
    }

    /// <summary>訊息附帶的一筆參照。三個欄位都可能是空的（落盤就允許）。</summary>
    public sealed class SCP_TavernRef
    {
        public string Path = "";
        public string Label = "";
        public string Anchor = "";
    }

    /// <summary>一則讀回來的訊息（**已經帶著它的定語** —— region 與 uuid 不可以跟本文分家）。</summary>
    public sealed class SCP_TavernMessage
    {
        public string Region = "";
        public string Room = "";
        public int Seq;
        public string Uuid = "";
        public string Ts = "";
        public string SenderPersona = "";
        public string SenderName = "";
        public string SenderId = "";
        public string Kind = "";
        public string Body = "";

        /// <summary>
        /// 訊息的 `meta` 欄（tag／_writer／…）。⚠ 2026-09-18 加上：查詢層要用 `meta.tag` 印 «標籤»。
        /// ⛔ 空 Dictionary 與「沒有 meta」在這裡**同形** —— 本模型刻意不分辨那兩者，
        ///   因為沒有任何一個消費端需要分辨，而多一個 nullable 會讓每個呼叫端各判一次。
        /// 📌 跨區讀那條路（走 `git show`）目前**不填它** ⇒ 那條路上的 Meta 一定是空的。
        ///   ⚠ 那是「未填」不是「沒有」 —— 要用它就先確認自己走的是哪條 loader。
        /// </summary>
        public System.Collections.Generic.Dictionary<string, string> Meta
            = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);

        /// <summary>
        /// 回覆哪一則（`reply_to`）。**null ＝ 落盤沒有這個欄位**（多數訊息都沒有）。
        /// <para>⚠ 2026-09-20 加上（TASK-0247）：`op=read` 的算繪要印 `_(↩ N)_`。
        /// ⛔ 不用 0 當「沒有」—— seq 0 是合法的值，兩者同形的話回覆鏈會少一格而沒有人會發現。</para>
        /// <para>📌 跨區讀那條路（走 `git show`）目前**不填它** ⇒ 那條路上一定是 null。
        /// 那是「未填」不是「沒有」。</para>
        /// </summary>
        public int? ReplyTo;

        /// <summary>
        /// 附帶的參照（`refs`：path／label／anchor）。**空清單與「沒有 refs」在此同形**（同 <see cref="Meta"/> 的理由）。
        /// <para>📌 跨區讀那條路同樣不填它。</para>
        /// </summary>
        public System.Collections.Generic.List<SCP_TavernRef> Refs
            = new System.Collections.Generic.List<SCP_TavernRef>();

        /// <summary>
        /// 頭像（`sender_avatar_sprite`）。⚠ 2026-09-21 加上（TASK-0106）：**寫入端要能原樣寫回它** ——
        /// 讀取端不用它，但少一個欄位的序列化就不是同一則訊息了。
        /// <para>⛔ 空字串 ＝ 落盤沒有這個欄位（寫入端據此決定要不要 emit），⛔ 不是「沒有頭像」。</para>
        /// </summary>
        public string SenderAvatarSprite = "";

        /// <summary>
        /// 回覆哪一則的 uuid（`reply_to_uuid`）。同 <see cref="SenderAvatarSprite"/>：
        /// 2026-09-21 為了**寫入端的完整性**補上，空字串＝落盤沒有這個欄位。
        /// </summary>
        public string ReplyToUuid = "";

        /// <summary>落盤有沒有署名。false ⇒ 顯示層要降級成 SenderId，而**那一格要出聲**。</summary>
        public bool HasSenderName { get { return SenderName.Length > 0; } }

        /// <summary>它在那個 ref 的樹裡的路徑（**印出來的路徑才是真的**，不要背路徑）。</summary>
        public string Path = "";

        /// <summary>可以直接貼回別處的引用式。⚠ **這是本工具真正的交付物** —— 光是「讀得到」不解決任何事。</summary>
        public string Citation { get { return Region + "#" + Seq + " (uuid=" + Uuid + ")"; } }

        /// <summary>一行的「誰說的」（三個欄位都可能是空的 ⇒ 空的就不佔位）。</summary>
        public string Who
        {
            get
            {
                string aWho = SenderPersona.Length > 0 ? SenderPersona : "(未標 persona)";
                if (SenderName.Length > 0) aWho += "（" + SenderName + "）";
                if (SenderId.Length > 0) aWho += "　id=" + SenderId;
                return aWho;
            }
        }
    }

    /// <summary>跨區讀取的解析器。**只讀**。</summary>
    public static class SCP_TavernRegion
    {
        /// <summary>沒指定房間時看哪一房。</summary>
        public const string DefaultRoom = "tavern";

        // 區塊職責：區的自報檔 —— 有 `currency_id` 才算一區（見檔頭）。
        // 🔴 這裡**必須**留兩條路，而它跟「本區讀自己的設定 ⛔ 不留 fallback」不矛盾：
        //   · 本區讀自己的設定：同一棵樹、我改得動 ⇒ 留 fallback 只會讓「誰還在讀舊路徑」永遠查不出來。
        //   · 這裡讀的是**別的 ref**：BTC 區在另一條分支上（我改不動），而**歷史 commit 永遠停在舊路徑**。
        // 🩸 只換新路徑的話，失效樣子是「那個 ref 不是一區」—— 下面那句 `if (!aBank.Ok) continue;`
        //   是**判準不是錯誤** ⇒ 清單上靜默少一區，沒有任何一層會喊
        //   （TASK-0274 動手前量到的，2026-09-22）。
        // ⇒ 順序：新路徑優先，找不到才退舊路徑；兩條都沒有才判定「不是一區」。
        const string BankSettingsPath = "Bank/bank_settings.json";
        const string BankSettingsPathLegacy = "Treasury/bank_settings.json";

        /// <summary>讀某個 ref 的自報檔 —— 新路徑優先，退回舊路徑（跨 ref／跨歷史一定要兩條）。</summary>
        static SCP_GitResult ShowBankSettings(SCP_DataRoot iRepo, string iRefName)
        {
            SCP_GitResult aNew = SCP_Git.Run(iRepo.Value, "show", iRefName + ":" + BankSettingsPath);
            if (aNew.Ok) return aNew;
            return SCP_Git.Run(iRepo.Value, "show", iRefName + ":" + BankSettingsPathLegacy);
        }

        /// <summary>掃哪一組 ref。⚠ 只掃 <c>origin</c>：同一條分支在多個 remote 上會變成同名的兩個「區」。</summary>
        public const string RemotePrefix = "refs/remotes/origin";

        /// <summary>
        /// 列出所有區（**掃描範圍要被呼叫端印出來** —— 「查無此區」與「這個 remote 沒掃」是兩件事）。
        /// </summary>
        /// <param name="oProblems">掃描期間讀不動的 ref（不是「沒有這一區」，是「這一格沒答案」）。</param>
        public static List<SCP_TavernRegionInfo> ListRegions(SCP_DataRoot iRepo, List<string> oProblems)
        {
            var aList = new List<SCP_TavernRegionInfo>();
            // %(symref) 非空 ＝ 這條 ref 是別條的別名（origin/HEAD）。
            // 🩸 不能只擋名字結尾是 /HEAD：`refs/remotes/origin/HEAD` 的**短名是裸的 `origin`**，
            //   於是它會混進來，讓同一個區出現兩次（2026-09-08 第一次實跑當場現形）。
            SCP_GitResult aRefs = SCP_Git.Run(iRepo.Value,
                "for-each-ref", "--format=%(refname:short)|%(symref)", RemotePrefix);
            if (!aRefs.Ok)
            {
                oProblems.Add("列 ref 失敗（" + RemotePrefix + "）：" + aRefs.ReasonLine);
                return aList;
            }

            foreach (string aRef in aRefs.OutLines())
            {
                string aLine = aRef.Trim();
                if (aLine.Length == 0) continue;
                int aBar = aLine.IndexOf('|');
                if (aBar < 0) { oProblems.Add("ref 行讀不動（缺分隔）：" + aLine); continue; }
                string aName = aLine.Substring(0, aBar).Trim();
                string aSymref = aLine.Substring(aBar + 1).Trim();
                if (aName.Length == 0) continue;
                if (aSymref.Length > 0) continue;   // 別名 ⇒ 同一個區會出現兩次

                SCP_GitResult aBank = ShowBankSettings(iRepo, aName);
                if (!aBank.Ok) continue;   // 兩條路都沒有自報檔 ⇒ 它不是一個區（這是判準，不是錯誤）

                string aCurrency;
                try { aCurrency = SCP_JsonParser.Parse(aBank.StdOut).GetString("currency_id", ""); }
                catch (Exception e)
                {
                    oProblems.Add(aName + " 的 " + BankSettingsPath + " 解析不動：" + e.Message);
                    continue;
                }
                if (aCurrency.Length == 0)
                {
                    oProblems.Add(aName + " 有 " + BankSettingsPath + " 但 currency_id 是空的 ⇒ 不當成區");
                    continue;
                }

                var aInfo = new SCP_TavernRegionInfo { Region = aCurrency, Ref = aName };
                FillTip(iRepo, aInfo);
                aList.Add(aInfo);
            }
            return aList;
        }

        static void FillTip(SCP_DataRoot iRepo, SCP_TavernRegionInfo ioInfo)
        {
            SCP_GitResult aLog = SCP_Git.Run(iRepo.Value,
                "log", "-1", "--format=%h|%cd", "--date=format:%Y-%m-%d %H:%M", ioInfo.Ref);
            if (!aLog.Ok) { ioInfo.TipSha = "?"; ioInfo.TipTime = "?"; return; }
            string aLine = SCP_Git.FirstLine(aLog.StdOut);
            int aBar = aLine.IndexOf('|');
            if (aBar < 0) { ioInfo.TipSha = aLine; ioInfo.TipTime = "?"; return; }
            ioInfo.TipSha = aLine.Substring(0, aBar);
            ioInfo.TipTime = aLine.Substring(aBar + 1);
        }

        /// <summary>找某個區（大小寫不敏感）。找不到回 null —— 呼叫端要把**掃到的清單**印出來。</summary>
        public static SCP_TavernRegionInfo? FindRegion(IReadOnlyList<SCP_TavernRegionInfo> iRegions, string iRegion)
        {
            for (int i = 0; i < iRegions.Count; ++i)
            {
                if (string.Equals(iRegions[i].Region, iRegion, StringComparison.OrdinalIgnoreCase))
                    return iRegions[i];
            }
            return null;
        }

        /// <summary>
        /// 讀一則訊息。找不到回 null 並把原因寫進 <paramref name="oWhy"/>。
        /// <para>⚠ 「這個 ref 的樹裡沒有這個 seq」有兩種意思，本層**不合成一句**：號真的不在這一區、
        /// 或它還沒被推上去（本機工作區比 ref 新）。<paramref name="oWhy"/> 兩種都講。</para>
        /// </summary>
        public static SCP_TavernMessage? Read(SCP_DataRoot iRepo, SCP_TavernRegionInfo iRegion,
            string iRoom, int iSeq, out string oWhy)
        {
            oWhy = "";
            string aDir = "ChatTavern/rooms/" + iRoom + "/messages";
            string aFile = "/" + iSeq.ToString("D8") + ".json";

            // 日期資料夾是內容的一部分（訊息的日期由它決定）⇒ 不能猜，要去樹裡找。
            SCP_GitResult aTree = SCP_Git.Run(iRepo.Value, "ls-tree", "-r", "--name-only", iRegion.Ref, "--", aDir);
            if (!aTree.Ok)
            {
                oWhy = "列不出 " + iRegion.Ref + " 的 " + aDir + "：" + aTree.ReasonLine;
                return null;
            }

            string aPath = "";
            foreach (string aLine in aTree.OutLines())
            {
                if (!aLine.EndsWith(aFile, StringComparison.Ordinal)) continue;
                aPath = aLine.Trim();
                break;
            }
            if (aPath.Length == 0)
            {
                oWhy = "`" + iRegion.Region + "` 區的 `" + iRoom + "` 房在 " + iRegion.Ref
                       + " 的樹裡**沒有** seq " + iSeq
                       + "（兩種可能：這個號本來就不屬於這一區／它還沒被推上 ref —— tip 是 "
                       + iRegion.TipSha + " · " + iRegion.TipTime + "，本層不 fetch）";
                return null;
            }

            SCP_GitResult aShow = SCP_Git.Run(iRepo.Value, "show", iRegion.Ref + ":" + aPath);
            if (!aShow.Ok) { oWhy = "讀不動 " + aPath + "：" + aShow.ReasonLine; return null; }

            SCP_JsonData aJson;
            try { aJson = SCP_JsonParser.Parse(aShow.StdOut); }
            catch (Exception e) { oWhy = aPath + " 解析不動：" + e.Message; return null; }

            var aMsg = new SCP_TavernMessage();
            aMsg.Region = iRegion.Region;
            aMsg.Room = iRoom;
            aMsg.Seq = iSeq;
            aMsg.Uuid = aJson.GetString("uuid", "");
            aMsg.Ts = aJson.GetString("ts", "");
            aMsg.SenderPersona = aJson.GetString("sender_persona", "");
            aMsg.SenderName = aJson.GetString("sender_name", "");
            aMsg.SenderId = aJson.GetString("sender_id", "");
            aMsg.Kind = aJson.GetString("kind", "");
            aMsg.Body = aJson.GetString("body", "");
            aMsg.Path = aPath;
            return aMsg;
        }
    }
}
