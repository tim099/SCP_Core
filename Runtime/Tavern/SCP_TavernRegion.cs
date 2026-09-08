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

        /// <summary>區的自報檔 —— 有 <c>currency_id</c> 才算一區（見檔頭）。</summary>
        const string BankSettingsPath = "Treasury/bank_settings.json";

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

                SCP_GitResult aBank = SCP_Git.Run(iRepo.Value, "show", aName + ":" + BankSettingsPath);
                if (!aBank.Ok) continue;   // 沒有自報檔 ⇒ 它不是一個區（這是判準，不是錯誤）

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
