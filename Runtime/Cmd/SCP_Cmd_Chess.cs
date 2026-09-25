// 區塊職責：下棋活動的 **Senate／宿主中立入口** —— 把 `SCP.Core.Chess` 掛上 Cmd 系統。
// 物理意義：TASK-0268 —— `chess.py`（1178 行，10 個子命令，自寫規則引擎）整支移植進 SCP_Core 之後的唯一入口。
//           兩個宿主讀寫同一份實作：Senate CLI（`senate cmd chess`）與 Unity Editor
//           （自由時間 `cmd_steps` 路由，`Cmd_FreeTimeActivity.RunCmdStep` in-process 派遣）。
//           十個子命令是 `op` 的十個值（同 `library` 的形狀：一支 Cmd、op 分派）。
// 數值影響：寫的是 `<data_root>/Chess/games/<n>.json`；廣播與發券走 `SCP_ChessGatewayHost` 裝上的閘。
// ⚠ **本 Cmd 是 Native**：棋局本體在本地跑，Editor 沒開也下得了棋 ——
//   但**廣播與發券**需要宿主（Senate 那側會派給 Editor 的 `Cmd_Tavern`），宿主不在時那兩件事會出聲失敗、
//   ⛔ 不回滾棋步（同 python：best-effort）。
#nullable enable
using System.Collections.Generic;
using System.IO;
using SCP.Core.Chess;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Chess : SCP_Cmd
    {
        public override string Name => "chess";

        public override string Summary =>
            "下棋（西洋棋）：開局／配對／入座／釋座／走子／盤面／認輸／提和／對局清單 ＋ 回放對拍。"
            + "**棋局本地跑**，廣播與發券走宿主。";

        public override string Details =>
            "十個子命令是 `op` 的值：start｜join｜release｜lobby｜match｜move｜board｜resign｜draw｜list（＋ verify）。\n"
            + "⭐ **自律模式**：move 對非法步只警示、照樣套用（`🔢 legal = 0`）；硬擋的只有輸入有效性"
            + "（起點無子／起點是對方的子／UCI 形狀壞掉）。\n"
            + "⭐ 每一手都落盤，一局可以跨好幾場自由時間、跨好幾次醒來。\n"
            + "⚠ 廣播與發券是 best-effort：沒送出會印 `⚠` 並在 `🔢 broadcast` 標 `failed`／`no_gateway`，"
            + "⛔ 不回滾那一步棋。\n"
            + "🔬 `op=verify`（唯讀）：全部對局逐手重放、比對 FEN，並做反向對照（把一手換成非法著法，合法步判定必須拒絕）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("chess --arg op=match --arg persona=<你> --arg say=\"誰來陪我下一盤?\"")
            + "\n  " + SCP_CmdRegistry.Invoke("chess --arg op=move --arg idx=18 --arg uci=e2e4 --arg persona=<你>");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("data_root", "AgentCommands 資料根（絕對路徑）—— 對局在 `<data_root>/Chess/games/`", iRequired: true),
            new SCP_CmdArgSpec("op", "start｜join｜release｜lobby｜match｜move｜board｜resign｜draw｜list｜verify（預設 list＝純讀）"),
            new SCP_CmdArgSpec("persona", "你是誰（start／join／release／match／move／resign／draw 必填 —— ⛔ 不代取）"),
            new SCP_CmdArgSpec("idx", "對局編號（join／release／move／board／resign／draw 必填）"),
            new SCP_CmdArgSpec("uci", "op=move 用：UCI 著法（`e2e4`／升變 `e7e8q`／易位 `e1g1`）"),
            new SCP_CmdArgSpec("side", "start：white｜black｜both（預設 both＝solo 掌兩座）；join／release：white｜black（省略＝自動挑）"),
            new SCP_CmdArgSpec("vs_open", "op=start 用：=1 ⇒ 另一座留 OPEN 等人加入"),
            new SCP_CmdArgSpec("accept", "op=draw 用：=1 ⇒ 接受對方的提和（省略＝提和）"),
            new SCP_CmdArgSpec("say", "帶一句話（自言自語／跟對手聊天），會插在廣播的盤面前"),
            new SCP_CmdArgSpec("no_broadcast", "=1 ⇒ 不廣播酒館（本地測試用）"),
        };

        static readonly HashSet<string> k_NeedPersona = new HashSet<string>
            { "start", "join", "release", "match", "move", "resign", "draw" };
        static readonly HashSet<string> k_NeedIdx = new HashSet<string>
            { "join", "release", "move", "board", "resign", "draw" };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aDataRoot = iArgs.Get("data_root").Trim();
            if (!Directory.Exists(aDataRoot))
                return SCP_CmdResult.Fail(2, "✗ 資料根不存在：" + aDataRoot);

            // ⭐ 預設是**純讀**那一支 —— 打錯 op 的代價要是「什麼都沒發生」，不是「開了一局」。
            string aOp = iArgs.Get("op").Trim();
            if (aOp.Length == 0) aOp = "list";

            var q = new SCP_ChessRequest
            {
                DataRoot = aDataRoot,
                Persona = iArgs.Get("persona").Trim(),
                Uci = iArgs.Get("uci").Trim(),
                Side = iArgs.Get("side").Trim().ToLowerInvariant(),
                VsOpen = iArgs.Get("vs_open").Trim() == "1",
                Accept = iArgs.Get("accept").Trim() == "1",
                Say = iArgs.Get("say").Trim(),
                NoBroadcast = iArgs.Get("no_broadcast").Trim() == "1",
            };
            if (k_NeedPersona.Contains(aOp) && q.Persona.Length == 0)
                return SCP_CmdResult.Fail(2, $"✗ op=`{aOp}` 需要 `persona` —— ⛔ 不代取（身分猜錯是把一步棋記到別人頭上）");
            if (k_NeedIdx.Contains(aOp))
            {
                string aRaw = iArgs.Get("idx").Trim();
                if (!int.TryParse(aRaw, out q.Index) || q.Index < 0)
                    return SCP_CmdResult.Fail(2, $"✗ op=`{aOp}` 需要 `idx`（非負整數；收到 `{aRaw}`）");
            }
            if (aOp == "move" && q.Uci.Length == 0)
                return SCP_CmdResult.Fail(2, "✗ op=`move` 需要 `uci`（例 `e2e4`）");
            if (q.Side.Length > 0 && q.Side != "white" && q.Side != "black" && !(aOp == "start" && q.Side == "both"))
                return SCP_CmdResult.Fail(2, $"✗ side=`{q.Side}` 不合法（start 吃 white|black|both；join／release 吃 white|black）");

            SCP_IChessGateway? aGate = SCP_ChessGatewayHost.For(aDataRoot);
            SCP_CmdResult aResult = aOp switch
            {
                "start" => SCP_ChessPlay.Start(q, aGate),
                "join" => SCP_ChessPlay.Join(q, aGate),
                "release" => SCP_ChessPlay.Release(q, aGate),
                "lobby" => SCP_ChessPlay.Lobby(q),
                "match" => SCP_ChessPlay.Match(q, aGate),
                "move" => SCP_ChessPlay.Move(q, aGate),
                "board" => SCP_ChessPlay.Board(q),
                "resign" => SCP_ChessPlay.Resign(q, aGate),
                "draw" => SCP_ChessPlay.Draw(q, aGate),
                "list" => SCP_ChessPlay.List(q),
                "verify" => SCP_ChessPlay.Verify(q),
                _ => SCP_CmdResult.Fail(2,
                    $"✗ 不認得的 op：`{aOp}`（吃的是 start｜join｜release｜lobby｜match｜move｜board｜resign｜draw｜list｜verify）"),
            };
            // 定語：寫入類的 op 印出廣播／發券是誰在哪裡跑的（⛔ 不讓「宿主替我做完」與「我在本地做完」同形）
            if (aGate != null && (k_NeedPersona.Contains(aOp)) && !q.NoBroadcast)
                aResult.Lines.Insert(0, aGate.HostQualifier);
            return aResult;
        }
    }
}
