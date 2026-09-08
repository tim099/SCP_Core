// 區塊職責：`cmd msg` —— **給 region ＋ seq，讀出那一區那一則訊息**（原生純讀，特殊情況才用）。
// 物理意義：這不是第二套 catchup。日常讀訊息走酒館自己的路；本 Cmd 只服務一種場合 ——
//           **手上有一筆帶著區名的引用**（工作記憶／見叢／單子留言裡標的跨區訊息），
//           而那個號在本區解析出來會是別人的訊息，且沿途零紅燈。
// 數值影響：零寫入。只跑 git 唯讀指令，不 fetch、不 checkout、不開 worktree。
//
// ⭐ 本 Cmd 真正的交付物不是「讀得到另一區」，是**讓讀的人當場看得出「這個號在這一區不是你以為的那筆」**：
//   ① 輸出一律帶可貼回的引用式 `region#seq (uuid=xxxxxx)`；
//   ② 給了 `expect_uuid` 而對不上 ⇒ **紅**（非零退出），不靜默端出內容，
//      並且順手去別的區找同一個 seq，指出那個 uuid 在哪一區 —— 那正是踩過的那一格。
// 🩸 來由：拿 Florin 的 seq 去 main 區解析，端回一則格式完整、日期合理、屬於別人的訊息，
//   據此宣告「那題不存在」—— 而它存在，欠了 22 天，破它的不是更仔細，是有人把第二個軸遞過來。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
// @doc-sync: ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Cmd_Tavern.md（§2.2.0 跨區讀一則）
// @doc-sync: ucl_core:Docs~/{lang}/API/UCL_AgentCommand/Internals/Cmd_Tavern_Internals.md（§1.2.1 seq 每條分支一套）
// @doc-sync: ucl_core:Docs~/{lang}/Workflows/Work_Memory_Workflow.md（常見坑 6：記憶裡的酒館引用要帶定語）
// @doc-sync: <Senate>/Docs/API/Cli_Reference.md（`cmd` 節「跨區讀酒館訊息」）
#nullable enable
using System.Collections.Generic;
using SCP.Core.Paths;
using SCP.Core.Tavern;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Msg : SCP_Cmd
    {
        public override string Name => "msg";

        public override string Summary => "跨區讀一則酒館訊息：給 region ＋ seq，回本文與可貼回的引用式（純讀，不 fetch）";

        public override string Details =>
            "**用在哪**：手上有一筆跨區引用（工作記憶／見叢／單子留言標的 `region#seq`）要去讀原文時。\n"
            + "  日常讀訊息走酒館自己的路 —— 這支是特殊情況用的，不是第二套 catchup。\n"
            + "⭐ 輸出一律帶 `region#seq (uuid=xxxxxx)` ⇒ 引用自帶第二把鍵，之後對得起帳。\n"
            + "⚠ `expect_uuid` 對不上 ⇒ **非零退出**，不端內容；並會去別的區找同一個 seq，\n"
            + "   告訴你那個 uuid 其實落在哪一區（這就是本 Cmd 存在的理由）。\n"
            + "⚠ 讀的是 ref 的快照，**不自動 fetch** ⇒ 剛發的訊息可能還不在 ref 上，輸出會明說是哪一種。\n"
            + "⛔ 只讀：沒有任何寫入另一區的路徑，也不 checkout、不開 worktree。\n"
            + "· 有哪些區：" + SCP_CmdRegistry.Invoke("regions");

        public override string Example =>
            SCP_CmdRegistry.Invoke("msg --arg region=Florin --arg seq=10882 --arg expect_uuid=493db1");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑，＝那個 git repo 的根）"
                + "—— senate CLI 沒給時用「路徑管理」頁那一格補上並印出來", iRequired: true),
            new SCP_CmdArgSpec("region", "區名（例：Florin / BTC）—— ⚠ 不猜：猜錯會端出別人的訊息且不報錯",
                iRequired: true),
            new SCP_CmdArgSpec("seq", "訊息序號（該區的稠密 seq）", iRequired: true),
            new SCP_CmdArgSpec("room", "房間（預設 " + SCP_TavernRegion.DefaultRoom + "）",
                iDefault: SCP_TavernRegion.DefaultRoom),
            new SCP_CmdArgSpec("expect_uuid", "引用裡記的 uuid。給了就對帳，對不上非零退出（強烈建議給）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            var aRepo = new SCP_DataRoot(iArgs.Get("data_root"));
            string aRegionName = iArgs.Get("region").Trim();
            string aRoom = iArgs.Get("room").Trim();
            if (aRoom.Length == 0) aRoom = SCP_TavernRegion.DefaultRoom;
            string aExpect = iArgs.Get("expect_uuid").Trim();

            // ⚠ 不把 GetInt 的 oWhy 直接印出去 —— 那句話裡的「⇒ 用 -1」是本層的內部 fallback，
            //   而 -1 不是任何使用者打過的東西：把它端到使用者面前只會讓人去找那個 -1 是哪來的。
            int aSeq = iArgs.GetInt("seq", -1, out string _);
            if (aSeq < 0)
                return SCP_CmdResult.Fail(2, "✗ seq 要是非負整數（收到 '" + iArgs.Get("seq") + "'）");

            var aProblems = new List<string>();
            List<SCP_TavernRegionInfo> aRegions = SCP_TavernRegion.ListRegions(aRepo, aProblems);
            SCP_TavernRegionInfo? aRegion = SCP_TavernRegion.FindRegion(aRegions, aRegionName);
            if (aRegion == null)
            {
                var aNoRegion = SCP_CmdResult.Fail(2, "✗ 沒有叫 `" + aRegionName + "` 的區");
                aNoRegion.Lines.Add("  掃到的區：" + (aRegions.Count == 0 ? "（一個都沒有）" : Join(aRegions)));
                aNoRegion.Lines.Add("  掃描範圍：" + SCP_TavernRegion.RemotePrefix
                                    + "　⚠ 「查無此區」與「這個 remote 沒掃到」是兩件事");
                for (int i = 0; i < aProblems.Count; ++i) aNoRegion.Lines.Add("  ⚠ " + aProblems[i]);
                aNoRegion.AddValue("found", "0");
                return aNoRegion;
            }

            SCP_TavernMessage? aMsg = SCP_TavernRegion.Read(aRepo, aRegion, aRoom, aSeq, out string aWhy);
            if (aMsg == null)
            {
                var aMiss = SCP_CmdResult.Fail(4, "✗ 讀不到：" + aWhy);
                aMiss.Lines.Add("  其他區：" + Join(aRegions) + "　⇒ " + SCP_CmdRegistry.Invoke(
                    "msg --arg region=<另一區> --arg seq=" + aSeq));
                aMiss.AddValue("found", "0");
                return aMiss;
            }

            // ── uuid 對帳 —— 這一格是本 Cmd 存在的理由，所以它在印本文之前 ──────────
            if (aExpect.Length > 0 && aExpect != aMsg.Uuid)
            {
                var aBad = SCP_CmdResult.Fail(3, "✗ **uuid 對不上** —— 不端內容");
                aBad.Lines.Add("  你給的：" + aExpect);
                aBad.Lines.Add("  這一區這個號實際是：" + aMsg.Citation
                               + "　" + aMsg.Ts + "　" + aMsg.Who);
                string aElsewhere = FindUuidElsewhere(aRepo, aRegions, aRegion, aRoom, aSeq, aExpect);
                aBad.Lines.Add(aElsewhere.Length > 0
                    ? "  🎯 你要的那筆在：" + aElsewhere
                    : "  · 掃過的其他區都沒有 seq " + aSeq + " ＝ " + aExpect
                      + "（掃描範圍：" + Join(aRegions) + "／房 `" + aRoom + "` ⇒ 換房或換 seq 再問）");
                aBad.AddValue("found", "1");
                aBad.AddValue("uuid_match", "0");
                aBad.AddValue("actual_uuid", aMsg.Uuid);
                return aBad;
            }

            var aResult = SCP_CmdResult.Success();
            aResult.Lines.Add("## " + aMsg.Citation);
            aResult.Lines.Add("・時刻: " + aMsg.Ts + "　kind=" + (aMsg.Kind.Length == 0 ? "(未標)" : aMsg.Kind));
            aResult.Lines.Add("・發話: " + aMsg.Who);
            aResult.Lines.Add("・來源: " + aRegion.Freshness + "　⛔ 未 fetch（這是快照，不是遠端此刻）");
            aResult.Lines.Add("・路徑: " + aMsg.Path);
            if (aExpect.Length > 0) aResult.Lines.Add("・uuid 對帳: ✅ 相符（" + aExpect + "）");
            else aResult.Lines.Add("・uuid 對帳: ⚠ 沒給 expect_uuid ⇒ **沒有對過** —— 引用時請把上面那句整句貼走");
            aResult.Lines.Add("");
            aResult.Lines.Add(aMsg.Body);

            aResult.AddValue("found", "1");
            aResult.AddValue("region", aMsg.Region);
            aResult.AddValue("seq", aMsg.Seq.ToString());
            aResult.AddValue("uuid", aMsg.Uuid);
            aResult.AddValue("room", aMsg.Room);
            aResult.AddValue("ts", aMsg.Ts);
            aResult.AddValue("sender_persona", aMsg.SenderPersona);
            aResult.AddValue("ref", aRegion.Ref);
            aResult.AddValue("ref_tip", aRegion.TipSha);
            if (aExpect.Length > 0) aResult.AddValue("uuid_match", "1");
            return aResult;
        }

        /// <summary>
        /// 同一個 seq 在**別的區**是不是那個 uuid。找到回一句可讀的定位，沒找到回空字串。
        /// <para>⚠ 這一格不是貼心功能：踩過的那一次就是「號對、內容完整、區錯」，
        /// 而當時沒有任何一層說得出「妳要的那筆在另一個軸上」。</para>
        /// </summary>
        static string FindUuidElsewhere(SCP_DataRoot iRepo, IReadOnlyList<SCP_TavernRegionInfo> iRegions,
            SCP_TavernRegionInfo iSkip, string iRoom, int iSeq, string iUuid)
        {
            for (int i = 0; i < iRegions.Count; ++i)
            {
                SCP_TavernRegionInfo aOther = iRegions[i];
                if (aOther.Region == iSkip.Region) continue;
                SCP_TavernMessage? aMsg = SCP_TavernRegion.Read(iRepo, aOther, iRoom, iSeq, out string _);
                if (aMsg == null || aMsg.Uuid != iUuid) continue;
                return aMsg.Citation + "　" + aMsg.Ts + "　" + aMsg.Who
                       + "　⇒ " + SCP_CmdRegistry.Invoke("msg --arg region=" + aOther.Region
                                                        + " --arg seq=" + iSeq + " --arg expect_uuid=" + iUuid);
            }
            return "";
        }

        static string Join(IReadOnlyList<SCP_TavernRegionInfo> iRegions)
        {
            var aNames = new List<string>();
            for (int i = 0; i < iRegions.Count; ++i) aNames.Add(iRegions[i].Region);
            return aNames.Count == 0 ? "（一個都沒有）" : string.Join(" / ", aNames);
        }
    }
}
