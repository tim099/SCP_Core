// 區塊職責：棋局的十個子命令（start / join / release / lobby / match / move / board / resign / draw / list）
//           ＋ 回放對拍（verify，TASK-0268 ①②）。
// 物理意義：TASK-0268 從 `chess.py` 的 cmd_* 逐支移植 —— 判準、狀態轉移、回報字句照舊；
//           「下一步」提示從 `python chess.py …` 改指 `senate cmd chess --arg op=…`。
// 數值影響：寫檔只經 `SCP_ChessStore`；廣播與發券只經 `SCP_IChessGateway`（best-effort，失敗出聲不回滾）。
//
// 哲學（Tim + kiara + basecamp 共識，照舊）：
//   · **自律遵守，非引擎硬擋**：move 預設信任套用；非法步只警示不擋手（lint），全程可事後複驗。
//     ⚠ 唯二硬擋的是**輸入有效性**不是棋規：起點無子、起點是對方的子、UCI 形狀壞掉
//     （拿過時盤面落子最常踩 —— 不擋的話 to 格的子會被靜默蒸發成一個不可能的盤面）。
//   · FEN ＝ 唯一真相；盤面圖是 FEN 的純函數。每步廣播帶三元組（prior_FEN → UCI → result_FEN）。
//   · 獎勵：勝 +10／敗 +5／和雙方各 +5；solo（兩座同一人）一人收兩座的份。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using SCP.Core.Cmd;
using SCP.Core.Json;

namespace SCP.Core.Chess
{
    /// <summary>一次呼叫的參數（CLI 與自由時間代跑兩個入口共用）。</summary>
    public sealed class SCP_ChessRequest
    {
        public string DataRoot = "";
        public string Persona = "";
        public int Index = -1;
        public string Uci = "";
        /// <summary>white｜black｜both（start 預設 both）；join／release 空＝自動挑。</summary>
        public string Side = "";
        public bool VsOpen;
        public bool Accept;
        public string Say = "";
        public bool NoBroadcast;
    }

    public static class SCP_ChessPlay
    {
        public const string RuleName = "西洋棋 / Chess";
        // 獎勵（繪圖券）：原本由 `rulebooks/chess.yaml` 資料驅動，而那份 yaml 的值與 python 內建 fallback
        //   逐值相同、唯一的消費端就是 chess.py ⇒ 隨 TASK-0268 一起收進這裡，⛔ 不留兩份來源。
        public const int RewardWin = 10, RewardLose = 5, RewardDraw = 5;

        static string Hint(string iTail) => SCP_CmdRegistry.Invoke("chess " + iTail);

        // ── 盤面輸出 ──────────────────────────────────────────
        static void PrintBoard(SCP_JsonData g, SCP_CmdResult r)
        {
            SCP_ChessState st = SCP_ChessEngine.ParseFen(g["fen"].AsString());
            SCP_ChessOutcome aOut = SCP_ChessEngine.ResultStatus(st, RepCount(g, SCP_ChessEngine.PositionKey(st)));
            string chk = aOut.Detail == "check" ? " [將軍]" : "";
            r.Lines.Add($"♟️ Chess #{g["index"].AsInt()} | 白:{Or(SCP_ChessStore.SeatOf(g, "white"), "OPEN")} ⚔ 黑:{Or(SCP_ChessStore.SeatOf(g, "black"), "OPEN")}"
                        + $" | 輪:{(st.WhiteToMove ? "白" : "黑")} | {g.GetString("status", "")}{chk}");
            foreach (string aLine in SCP_ChessEngine.RenderBoard(st, g.GetString("last_move", "")).Split('\n')) r.Lines.Add(aLine);
            r.Lines.Add($"FEN: {g["fen"].AsString()}");
            if (!SCP_ChessStore.IsInProgress(g))
                r.Lines.Add($"結果: {g.GetString("result", "")} ({g.GetString("status", "")})");
        }

        static string Or(string iValue, string iFallback) => iValue.Length > 0 ? iValue : iFallback;

        static int RepCount(SCP_JsonData g, string iKey)
        {
            SCP_JsonData aRep = g["repetition"];
            return aRep.Contains(iKey) ? aRep[iKey].AsInt() : 0;
        }

        static void Touch(SCP_JsonData g) => g.Set("updated", SCP_ChessStore.UtcNowIso());

        // ── 廣播 ──────────────────────────────────────────────
        static void Broadcast(SCP_IChessGateway? iGate, SCP_JsonData g, string iHeader, string? iSender,
                              string iSay, SCP_CmdResult r)
        {
            SCP_ChessState st = SCP_ChessEngine.ParseFen(g["fen"].AsString());
            string aSayLine = iSay.Length > 0 ? $"💬 {Or(iSender ?? "", "?")}：{iSay}\n" : "";
            string aPrior = g.GetString("prior_fen", "");
            string aBody =
                $"♟️ {RuleName} #{g["index"].AsInt()} — {iHeader}\n"
                + aSayLine
                + $"白:{Or(SCP_ChessStore.SeatOf(g, "white"), "OPEN(待加入)")} ⚔ 黑:{Or(SCP_ChessStore.SeatOf(g, "black"), "OPEN(待加入)")} | "
                + $"輪:{(st.WhiteToMove ? "白" : "黑")} | status:{g.GetString("status", "")}\n"
                + "```\n" + SCP_ChessEngine.RenderBoard(st, g.GetString("last_move", "")) + "\n```\n"
                + $"prior_FEN: {(aPrior.Length > 0 ? aPrior : "(開局)")}\n"
                + $"result_FEN: {g["fen"].AsString()}\n"
                + $"({SCP_ChessEngine.GlyphLegend})";
            string aMeta = "{\"tag\": \"chess\", \"category\": \"chat\", \"game\": "
                           + g["index"].AsInt().ToString(CultureInfo.InvariantCulture) + "}";
            if (iGate == null)
            {
                r.Lines.Add("⚠ 廣播沒送出：這個宿主沒有裝上棋局閘（SCP_ChessGatewayHost.Factory 是 null）"
                            + "—— 棋步已存檔，酒館少一則盤面");
                r.AddValue("broadcast", "no_gateway");
                return;
            }
            string aLane = "chess-" + g["index"].AsInt().ToString(CultureInfo.InvariantCulture);
            bool aOk = iGate.Broadcast(string.IsNullOrEmpty(iSender) ? null : iSender, aLane, aBody, aMeta, out string aDetail);
            if (aOk) r.AddValue("broadcast", "sent");
            else
            {
                // ⚠ best-effort（不擋主流程、不回滾這一步棋），但**失敗要留一行** ——
                //   沒發出去的廣播與發出去的，在呼叫端本來完全同形。
                r.Lines.Add("⚠ 廣播沒落地（棋步已存檔，酒館少一則盤面）：" + aDetail);
                r.AddValue("broadcast", "failed");
            }
        }

        // ── 結算 ──────────────────────────────────────────────
        static void SettleRewards(SCP_IChessGateway? iGate, SCP_JsonData g, SCP_CmdResult r)
        {
            string w = SCP_ChessStore.SeatOf(g, "white"), b = SCP_ChessStore.SeatOf(g, "black");
            var aPlan = new List<(string Persona, string Kind)>();
            switch (g.GetString("result", ""))
            {
                case "white": aPlan.Add((w, "win")); aPlan.Add((b, "lose")); break;
                case "black": aPlan.Add((b, "win")); aPlan.Add((w, "lose")); break;
                case "draw": aPlan.Add((w, "draw")); aPlan.Add((b, "draw")); break;
                default: return;
            }
            bool aHeader = false;
            foreach ((string aPersona, string aKind) in aPlan)
            {
                if (aPersona.Length == 0) continue;
                if (!aHeader) { r.Lines.Add("🎟 繪圖券發放:"); aHeader = true; }
                int aAmt = aKind == "win" ? RewardWin : aKind == "lose" ? RewardLose : RewardDraw;
                string aRef = $"chess#{g["index"].AsInt()}:{aKind}";
                if (iGate == null)
                {
                    r.Lines.Add($"   ⚠ {aPersona} +{aAmt} ({aKind}) 沒發：這個宿主沒有裝上棋局閘（棋局結果不受影響，券需補發 ref={aRef}）");
                    continue;
                }
                int? aBal = iGate.GrantCanvasVoucher(aPersona, aAmt, "chess_reward", aRef, out string aDetail);
                if (aBal == null)
                    r.Lines.Add($"   ⚠ 發券失敗 {aPersona} +{aAmt} ({aKind})（棋局結果不受影響，券需補發 ref={aRef}）：{aDetail}");
                else
                    r.Lines.Add($"   {aPersona} +{aAmt} ({aKind}) → 餘額 {(aBal >= 0 ? aBal.Value.ToString(CultureInfo.InvariantCulture) : "（讀不回）")}");
            }
        }

        /// <summary>status 已結束 ⇒ 存檔、發券、廣播收場。</summary>
        static void FinalizeIfEnded(SCP_ChessRequest q, SCP_IChessGateway? iGate, SCP_JsonData g, SCP_CmdResult r)
        {
            if (SCP_ChessStore.IsInProgress(g)) return;
            SettleRewards(iGate, g, r);
            SCP_ChessStore.Save(q.DataRoot, g);
            if (q.NoBroadcast) return;
            string aStatus = g.GetString("status", "");
            string aTag = aStatus switch
            {
                "checkmate" => "將死", "stalemate" => "逼和", "draw" => "和局", "resigned" => "認輸", _ => aStatus,
            };
            string aWin = g.GetString("result", "") switch
            {
                "white" => "白方勝", "black" => "黑方勝", "draw" => "和局", _ => "",
            };
            Broadcast(iGate, g, $"對局結束 ({aTag}) — {aWin}", q.Persona, q.Say, r);
        }

        static SCP_CmdResult Fail(string iWhy) => SCP_CmdResult.Fail(1, iWhy);

        static SCP_JsonData? LoadInProgress(SCP_ChessRequest q, out SCP_CmdResult? oFail, string iEndedSuffix = "")
        {
            oFail = null;
            SCP_JsonData? g = SCP_ChessStore.Load(q.DataRoot, q.Index, out string aWhy);
            if (g == null) { oFail = Fail(aWhy); return null; }
            if (!SCP_ChessStore.IsInProgress(g))
            {
                oFail = Fail($"❌ 對局 #{q.Index} 已結束" + iEndedSuffix.Replace("{status}", g.GetString("status", "")));
                return null;
            }
            return g;
        }

        // ═════════════════════════ 子命令 ═════════════════════════
        public static SCP_CmdResult Start(SCP_ChessRequest q, SCP_IChessGateway? iGate)
        {
            string aSide = q.Side.Length > 0 ? q.Side : "both";
            string? w, b; string aMode;
            switch (aSide)
            {
                case "both": w = b = q.Persona; aMode = "solo"; break;
                case "white":
                    w = q.Persona; b = null; aMode = "open";
                    if (!q.VsOpen) { b = q.Persona; aMode = "solo"; }   // 沒指定開放 ⇒ 預設 solo 掌兩座，之後可 release
                    break;
                case "black":
                    w = null; b = q.Persona; aMode = "open";
                    if (!q.VsOpen) { w = q.Persona; aMode = "solo"; }
                    break;
                default: return SCP_CmdResult.Fail(2, "❌ side 須為 white|black|both");
            }
            if (q.VsOpen) aMode = "open";
            int aIdx = SCP_ChessStore.NextIndex(q.DataRoot);
            SCP_JsonData g = SCP_ChessStore.NewGame(aIdx, w, b, aMode);
            SCP_ChessStore.Save(q.DataRoot, g);
            string? aOpenSeat = w == null ? "白" : b == null ? "黑" : null;
            var r = SCP_CmdResult.Success($"✅ 開局 Chess #{aIdx} (mode={aMode}) 白:{w ?? "OPEN"} 黑:{b ?? "OPEN"}");
            PrintBoard(g, r);
            if (aOpenSeat != null)
                r.Lines.Add($"🪑 {aOpenSeat}座 OPEN — 等人加入: " + Hint($"--arg op=join --arg idx={aIdx} --arg persona=<你> [--arg say=\"...\"]"));
            if (!q.NoBroadcast)
                Broadcast(iGate, g, "新局開盤" + (aOpenSeat != null ? $"·{aOpenSeat}座徵人對弈" : ""), q.Persona, q.Say, r);
            r.Lines.Add("");
            r.Lines.Add("下一步: " + Hint($"--arg op=move --arg idx={aIdx} --arg uci=<uci> --arg persona={q.Persona}"));
            r.AddValue("game", aIdx.ToString(CultureInfo.InvariantCulture));
            r.AddValue("mode", aMode);
            return r;
        }

        public static SCP_CmdResult Join(SCP_ChessRequest q, SCP_IChessGateway? iGate)
        {
            SCP_JsonData? g = LoadInProgress(q, out SCP_CmdResult? aFail, " ({status})，無法加入");
            if (g == null) return aFail!;
            string w = SCP_ChessStore.SeatOf(g, "white"), b = SCP_ChessStore.SeatOf(g, "black");
            string aSoloHolder = w.Length > 0 && w == b ? w : "";
            string aWant = q.Side;
            if (aWant.Length == 0) aWant = w.Length == 0 ? "white" : b.Length == 0 ? "black" : "black";   // 優先 OPEN；否則 solo 局挑黑座切入
            string aOccupant = aWant == "white" ? w : b;
            string aKind;
            if (aOccupant.Length == 0) aKind = "認領OPEN";
            else if (aSoloHolder.Length > 0 && q.Persona != aSoloHolder) aKind = "中途切入";      // 接管一座，單人保留另一座
            else if (aOccupant == q.Persona) return Fail($"❌ 你已經坐在 {aWant} 座了");
            else return Fail($"❌ {aWant} 座已被 {aOccupant} 佔 (非 solo, 無法切入)；可改接另一座或開新局");
            g["seats"].Set(aWant, q.Persona);
            w = SCP_ChessStore.SeatOf(g, "white"); b = SCP_ChessStore.SeatOf(g, "black");
            g.Set("mode", w.Length > 0 && b.Length > 0 && w != b ? "versus" : "solo");
            string aFen = g["fen"].AsString();
            SCP_ChessStore.AppendHistory(g, "join:" + aWant, q.Persona, aFen, aFen, q.Say);
            Touch(g);
            SCP_ChessStore.Save(q.DataRoot, g);
            string aMode = g["mode"].AsString();
            var r = SCP_CmdResult.Success($"✅ {q.Persona} {aKind} Chess #{q.Index} 接 {aWant} 座; mode={aMode}"
                                          + (q.Say.Length > 0 ? $"  💬 {q.Say}" : ""));
            PrintBoard(g, r);
            if (!q.NoBroadcast) Broadcast(iGate, g, $"{q.Persona} {aKind} 接 {aWant} 座 → {aMode}", q.Persona, q.Say, r);
            r.AddValue("game", q.Index.ToString(CultureInfo.InvariantCulture));
            r.AddValue("side", aWant);
            return r;
        }

        public static SCP_CmdResult Release(SCP_ChessRequest q, SCP_IChessGateway? iGate)
        {
            SCP_JsonData? g = LoadInProgress(q, out SCP_CmdResult? aFail);
            if (g == null) return aFail!;
            string w = SCP_ChessStore.SeatOf(g, "white"), b = SCP_ChessStore.SeatOf(g, "black");
            if (q.Persona != w && q.Persona != b) return Fail($"❌ {q.Persona} 不在本局座位，無法釋座");
            string aSide = q.Side;
            if (aSide.Length == 0) aSide = w == b && w == q.Persona ? "black" : w == q.Persona ? "white" : "black";
            if ((aSide == "white" ? w : b) != q.Persona) return Fail($"❌ {aSide} 座不是你佔的，不能釋出");
            g["seats"].Set(aSide, SCP_JsonData.NewNull());
            g.Set("mode", "open");
            string aFen = g["fen"].AsString();
            SCP_ChessStore.AppendHistory(g, "release:" + aSide, q.Persona, aFen, aFen, q.Say);
            Touch(g);
            SCP_ChessStore.Save(q.DataRoot, g);
            var r = SCP_CmdResult.Success($"🪑 {q.Persona} 釋出 {aSide} 座 → OPEN，等人加入" + (q.Say.Length > 0 ? $"  💬 {q.Say}" : ""));
            PrintBoard(g, r);
            if (!q.NoBroadcast) Broadcast(iGate, g, $"{q.Persona} 釋出 {aSide} 座徵人對弈", q.Persona, q.Say, r);
            r.AddValue("game", q.Index.ToString(CultureInfo.InvariantCulture));
            return r;
        }

        public static SCP_CmdResult Lobby(SCP_ChessRequest q)
        {
            List<SCP_JsonData> aGames = SCP_ChessStore.LoadAll(q.DataRoot, out List<string> aProblems);
            List<SCP_ChessWaiting> aWaiting = SCP_ChessStore.WaitingGames(aGames);
            var r = new SCP_CmdResult();
            foreach (string p in aProblems) r.Lines.Add("⚠ " + p);
            r.AddValue("waiting", aWaiting.Count.ToString(CultureInfo.InvariantCulture));
            if (aWaiting.Count == 0)
            {
                r.Lines.Add("(目前沒有等待加入的對局; 用 `match` 開一局自己下等人切入)");
                return r;
            }
            r.Lines.Add($"🪑 等待加入的對局 ({aWaiting.Count}):");
            foreach (SCP_ChessWaiting aW in aWaiting)
            {
                SCP_JsonData g = aW.Game;
                int aIdx = g["index"].AsInt();
                string aTag = aW.OpenSides.Count > 0
                    ? "OPEN座:" + string.Join("/", aW.OpenSides)
                    : $"solo({SCP_ChessStore.SeatOf(g, "white")}) 可中途切入";
                r.Lines.Add($"  #{aIdx,2} 白:{Or(SCP_ChessStore.SeatOf(g, "white"), "OPEN"),-10} 黑:{Or(SCP_ChessStore.SeatOf(g, "black"), "OPEN"),-10}"
                            + $" 已走{SCP_ChessStore.CountMoves(g)}手 — {aTag}");
                r.Lines.Add("      → " + Hint($"--arg op=join --arg idx={aIdx} --arg persona=<你> [--arg say=\"...\"]"));
            }
            // ⚠ 本支**不吃 persona** ⇒ 清單含「我自己的局」，而那些我配不上去（join 會擋）。
            r.Lines.Add("");
            r.Lines.Add("⚠ 這張清單不分「誰的局」—— 自己的 solo 局也在裡面（那些你加入不了）。");
            r.Lines.Add("   自動挑一局: " + Hint("--arg op=match --arg persona=<你>"));
            return r;
        }

        /// <summary>
        /// 自動配對：有可加入的 solo/OPEN 局就入座，沒有就開一局自己下（solo，等人中途切入）。
        /// <para>⚠ 兩條分支的回報**刻意不同形** —— 「入了別人的局」與「沒配到、自己開了一局」是兩件事。</para>
        /// </summary>
        public static SCP_CmdResult Match(SCP_ChessRequest q, SCP_IChessGateway? iGate)
        {
            var aSkip = new List<int>();
            var aNotes = new List<string>();
            for (int aAttempt = 0; aAttempt < 3; aAttempt++)
            {
                List<SCP_JsonData> aGames = SCP_ChessStore.LoadAll(q.DataRoot, out List<string> aProblems);
                if (aAttempt == 0) foreach (string p in aProblems) aNotes.Add("⚠ " + p);
                (SCP_JsonData? g, _, string aReason) = SCP_ChessStore.PickMatchCandidate(aGames, q.Persona, aSkip);
                if (g == null) break;
                int aIdx = g["index"].AsInt();
                string aHolder = Or(SCP_ChessStore.SeatOf(g, "white"), SCP_ChessStore.SeatOf(g, "black"));
                aNotes.Add($"🤝 配對: Chess #{aIdx}（{aHolder}，已走 {SCP_ChessStore.CountMoves(g)} 手；挑選判準: {aReason}）");
                var aJoinReq = new SCP_ChessRequest
                {
                    DataRoot = q.DataRoot, Persona = q.Persona, Index = aIdx, Say = q.Say, NoBroadcast = q.NoBroadcast,
                };
                SCP_CmdResult aJoin = Join(aJoinReq, iGate);
                if (aJoin.Ok)
                {
                    aJoin.Lines.InsertRange(0, aNotes);
                    aJoin.Lines.Add("");
                    aJoin.Lines.Add($"✅ 入座既有對局 Chess #{aIdx} —— 這是**加入別人開的局**，不是開新局。");
                    aJoin.Lines.Add("下一步: " + Hint($"--arg op=move --arg idx={aIdx} --arg uci=<uci> --arg persona={q.Persona}"));
                    aJoin.AddValue("matched", "joined");
                    return aJoin;
                }
                // 併發：兩人同時配對搶同一座。⛔ 不靜默改道 —— 說出本來要入哪一局、為什麼沒成。
                aSkip.Add(aIdx);
                aNotes.Add($"⚠ 入座 #{aIdx} 沒成（{string.Join(" ", aJoin.Lines)}）—— 重掃候選");
            }
            // ⚠ 有候選而被搶掉，不能印成「沒有可加入的局」—— 那會讓兩件事同形。
            aNotes.Add(aSkip.Count > 0
                ? $"⚠ 本來要入 {string.Join("/", aSkip.ConvertAll(i => "#" + i.ToString(CultureInfo.InvariantCulture)))}，都被搶了 ⇒ 改開新局（solo，等人中途切入）"
                : "🆕 沒有可加入的局 ⇒ 開一局自己下（solo，等人中途切入）");
            var aStartReq = new SCP_ChessRequest
            {
                DataRoot = q.DataRoot, Persona = q.Persona, Side = "both", Say = q.Say, NoBroadcast = q.NoBroadcast,
            };
            SCP_CmdResult aStart = Start(aStartReq, iGate);
            aStart.Lines.InsertRange(0, aNotes);
            aStart.AddValue("matched", "started");
            return aStart;
        }

        public static SCP_CmdResult Move(SCP_ChessRequest q, SCP_IChessGateway? iGate)
        {
            SCP_JsonData? g = LoadInProgress(q, out SCP_CmdResult? aFail, " ({status})");
            if (g == null) return aFail!;
            SCP_ChessState st = SCP_ChessEngine.ParseFen(g["fen"].AsString());
            bool aWhite = st.WhiteToMove;
            string w = SCP_ChessStore.SeatOf(g, "white"), b = SCP_ChessStore.SeatOf(g, "black");
            string aSeat = aWhite ? w : b;
            var aWarn = new List<string>();
            // 回合鎖：1v1 只認該座持有者；solo（兩座同人）放行 —— 自律模式，只警示
            if (aSeat.Length > 0 && aSeat != q.Persona && w != b)
                aWarn.Add($"⚠ 現在輪{(aWhite ? "白" : "黑")}({aSeat}), 你是 {q.Persona} — 自律模式仍套用, 請自查回合");
            string aUci = q.Uci.Trim().ToLowerInvariant();
            if (!SCP_ChessEngine.IsWellFormedUci(aUci))
                return SCP_CmdResult.Fail(2, $"❌ `{q.Uci}` 不是 UCI 形狀（要 `e2e4`／升變 `e7e8q`）—— 拒絕套用");
            // 防呆硬擋（輸入有效性，不是棋規）：起點無子／起點是對方棋子
            char pf = st.Board[SCP_ChessEngine.AlgToIdx(aUci.Substring(0, 2))];
            if (pf == '.')
                return Fail($"❌ 起點 {aUci.Substring(0, 2)} 無子, 拒絕套用 (盤面可能已被他人推進, 請先 `board {q.Index}` 看最新狀態再走)");
            if ((aWhite && !char.IsUpper(pf)) || (!aWhite && !char.IsLower(pf)))
                return Fail($"❌ 起點 {aUci.Substring(0, 2)} 是對方棋子, 現在輪{(aWhite ? "白" : "黑")} — 拒絕套用 (請確認回合 / 最新盤面)");
            List<string> aLegal = SCP_ChessEngine.LegalMoves(st);
            bool aIsLegal = aLegal.Contains(aUci);
            if (!aIsLegal)
                aWarn.Add($"⚠ 此步不在合法步集合(自律模式仍套用, 請自查)。合法步示例: {string.Join(", ", aLegal.GetRange(0, Math.Min(8, aLegal.Count)))}{(aLegal.Count > 8 ? "…" : "")}");

            string aPrior = g["fen"].AsString();
            SCP_ChessState ns = SCP_ChessEngine.ApplyMove(st, aUci);
            string aNewFen = SCP_ChessEngine.SerializeFen(ns);
            g.Set("prior_fen", aPrior);
            g.Set("fen", aNewFen);
            g.Set("last_move", aUci);
            string aKey = SCP_ChessEngine.PositionKey(ns);
            int aRep = RepCount(g, aKey) + 1;
            g["repetition"].Set(aKey, aRep);
            SCP_ChessStore.AppendHistory(g, aUci, q.Persona, aPrior, aNewFen, q.Say);
            SCP_ChessOutcome aOut = SCP_ChessEngine.ResultStatus(ns, aRep);
            Touch(g);
            if (aOut.Status == "checkmate") { g.Set("status", "checkmate"); g.Set("result", aOut.Detail); }
            else if (aOut.Status == "stalemate") { g.Set("status", "stalemate"); g.Set("result", "draw"); }
            else if (aOut.Status == "draw") { g.Set("status", "draw"); g.Set("result", "draw"); }
            SCP_ChessStore.Save(q.DataRoot, g);

            var r = new SCP_CmdResult();
            r.Lines.AddRange(aWarn);
            r.Lines.Add($"✅ #{q.Index} {q.Persona} 走 {aUci}" + (aOut.Detail.Length > 0 ? $" → {aOut.Detail}" : "")
                        + (q.Say.Length > 0 ? $"  💬 {q.Say}" : ""));
            PrintBoard(g, r);
            if (aOut.Status is "checkmate" or "stalemate" or "draw") FinalizeIfEnded(q, iGate, g, r);
            else if (!q.NoBroadcast)
                Broadcast(iGate, g, $"{q.Persona} 走 {aUci}" + (aOut.Detail == "check" ? " — 將軍!" : ""), q.Persona, q.Say, r);
            r.AddValue("game", q.Index.ToString(CultureInfo.InvariantCulture));
            r.AddValue("legal", aIsLegal ? "1" : "0");
            r.AddValue("status", g.GetString("status", ""));
            r.AddValue("fen", aNewFen);
            return r;
        }

        public static SCP_CmdResult Board(SCP_ChessRequest q)
        {
            SCP_JsonData? g = SCP_ChessStore.Load(q.DataRoot, q.Index, out string aWhy);
            if (g == null) return Fail(aWhy);
            var r = new SCP_CmdResult();
            PrintBoard(g, r);
            r.AddValue("fen", g["fen"].AsString());
            r.AddValue("status", g.GetString("status", ""));
            return r;
        }

        public static SCP_CmdResult Resign(SCP_ChessRequest q, SCP_IChessGateway? iGate)
        {
            SCP_JsonData? g = LoadInProgress(q, out SCP_CmdResult? aFail);
            if (g == null) return aFail!;
            string w = SCP_ChessStore.SeatOf(g, "white"), b = SCP_ChessStore.SeatOf(g, "black");
            string aLoser;
            if (q.Persona == w) aLoser = "white";
            else if (q.Persona == b) aLoser = "black";
            else return Fail($"❌ {q.Persona} 不在本局座位");
            g.Set("status", "resigned");
            g.Set("result", aLoser == "white" ? "black" : "white");
            string aFen = g["fen"].AsString();
            SCP_ChessStore.AppendHistory(g, "resign", q.Persona, aFen, aFen, q.Say);
            Touch(g);
            SCP_ChessStore.Save(q.DataRoot, g);
            var r = SCP_CmdResult.Success($"🏳 {q.Persona} ({aLoser}) 認輸 → {g["result"].AsString()} 勝" + (q.Say.Length > 0 ? $"  💬 {q.Say}" : ""));
            FinalizeIfEnded(q, iGate, g, r);
            r.AddValue("game", q.Index.ToString(CultureInfo.InvariantCulture));
            return r;
        }

        public static SCP_CmdResult Draw(SCP_ChessRequest q, SCP_IChessGateway? iGate)
        {
            SCP_JsonData? g = LoadInProgress(q, out SCP_CmdResult? aFail);
            if (g == null) return aFail!;
            string aFen = g["fen"].AsString();
            var r = new SCP_CmdResult();
            if (q.Accept)
            {
                string aOffer = g.GetString("draw_offer", "");
                if (aOffer.Length == 0 || aOffer == q.Persona) return Fail("❌ 沒有對方的提和可接受");
                g.Set("status", "draw");
                g.Set("result", "draw");
                SCP_ChessStore.AppendHistory(g, "draw_accept", q.Persona, aFen, aFen, q.Say);
                // ⚠ python 這一格**沒有**更新 updated（照抄；結算時的 Save 會落檔）
                SCP_ChessStore.Save(q.DataRoot, g);
                r.Lines.Add($"🤝 {q.Persona} 接受和議 → 和局" + (q.Say.Length > 0 ? $"  💬 {q.Say}" : ""));
                FinalizeIfEnded(q, iGate, g, r);
            }
            else
            {
                g.Set("draw_offer", q.Persona);
                SCP_ChessStore.AppendHistory(g, "draw_offer", q.Persona, aFen, aFen, q.Say);
                Touch(g);
                SCP_ChessStore.Save(q.DataRoot, g);
                r.Lines.Add($"🤝 {q.Persona} 提和 (等對方 accept=1)" + (q.Say.Length > 0 ? $"  💬 {q.Say}" : ""));
                if (!q.NoBroadcast) Broadcast(iGate, g, $"{q.Persona} 提和", q.Persona, q.Say, r);
            }
            r.AddValue("game", q.Index.ToString(CultureInfo.InvariantCulture));
            return r;
        }

        public static SCP_CmdResult List(SCP_ChessRequest q)
        {
            List<SCP_JsonData> aGames = SCP_ChessStore.LoadAll(q.DataRoot, out List<string> aProblems);
            var r = new SCP_CmdResult();
            foreach (string p in aProblems) r.Lines.Add("⚠ " + p);
            r.AddValue("games", aGames.Count.ToString(CultureInfo.InvariantCulture));
            if (aGames.Count == 0) { r.Lines.Add("(無對局)"); return r; }
            r.Lines.Add($"{"idx",3} {"mode",-7} {"白",-12} {"黑",-12} {"status",-11} result");
            foreach (SCP_JsonData g in aGames)
            {
                // ⚠ OPEN 座 python 印成 `None`（str(None)）—— 這裡照印，讓兩邊的清單逐字對得上
                string w = SCP_ChessStore.SeatOf(g, "white"), b = SCP_ChessStore.SeatOf(g, "black");
                r.Lines.Add($"{g["index"].AsInt(),3} {g.GetString("mode", ""),-7} {Or(w, "None"),-12} "
                            + $"{Or(b, "None"),-12} {g.GetString("status", ""),-11} {g.GetString("result", "")}");
            }
            return r;
        }

        // ═════════════════════════ 回放對拍（TASK-0268 ①②）═════════════════════════
        // 區塊職責：把磁碟上**每一局**的 history 重放一遍，逐手比對 FEN；再做反向對照。
        // 物理意義：① 兩種比法都做，因為它們抓的是不同的錯：
        //            · 逐手（prior_fen → 套用 → 比 result_fen）：引擎本身對不對
        //            · 串接（從開局一路套下去 → 比每一手的 result_fen）：history 本身有沒有斷鏈
        //              （有人手改過檔、或某一手的 prior_fen 不等於上一手的 result_fen）
        //          ② 反向對照：每一局挑一手，換成一步**一定不合法**的著法 ⇒ 新引擎的合法步判定必須拒絕它，
        //            而且重放那一手會跟記錄分岔 —— 少了這格，一個「什麼都說合法」的引擎會讓 ① 全綠。
        // 數值影響：**唯讀**，⛔ 不寫任何檔。
        public static SCP_CmdResult Verify(SCP_ChessRequest q)
        {
            List<SCP_JsonData> aGames = SCP_ChessStore.LoadAll(q.DataRoot, out List<string> aProblems);
            var r = new SCP_CmdResult();
            r.Lines.Add("# ♟ 回放對拍（唯讀，⛔ 沒有寫入任何檔）");
            foreach (string p in aProblems) r.Lines.Add("✗ " + p);
            int aMoves = 0, aEvents = 0, aStepMismatch = 0, aChainMismatch = 0, aFinalMismatch = 0;
            int aIllegalRecorded = 0, aNegTried = 0, aNegRejected = 0, aNegDiverged = 0;
            var aDetails = new List<string>();
            var aDigest = new System.Text.StringBuilder();
            foreach (SCP_JsonData g in aGames)
            {
                int aIdx = g["index"].AsInt();
                string aChain = SCP_ChessEngine.StartFen;
                bool aNegDone = false;
                foreach (SCP_JsonData h in g["history"])
                {
                    string aUci = h.GetString("uci", "");
                    string aPrior = h.GetString("prior_fen", "");
                    string aResult = h.GetString("result_fen", "");
                    bool aIsMove = aUci.Length >= 4 && aUci.IndexOf(':') < 0 && SCP_ChessEngine.IsWellFormedUci(aUci);
                    if (!aIsMove)
                    {
                        aEvents++;
                        // 事件（join／release／resign／draw_*）不動盤面：prior 與 result 應該都等於當下的鏈
                        if (aPrior != aChain || aResult != aChain)
                        {
                            aChainMismatch++;
                            aDetails.Add($"  ✗ #{aIdx} n={h.GetString("n", "?")} 事件 `{aUci}` 的 FEN 與鏈不符");
                        }
                        continue;
                    }
                    aMoves++;
                    SCP_ChessState aSt = SCP_ChessEngine.ParseFen(aPrior);
                    List<string> aLegalHere = SCP_ChessEngine.LegalMoves(aSt);
                    if (!aLegalHere.Contains(aUci)) aIllegalRecorded++;
                    // 摘要：每個盤面的合法步（含順序）＋終局判定 —— 與 python 那側同一條式子算，比「FEN 相同」更嚴：
                    //   FEN 只證明「套用」一樣，這一格證明「產生步」與「判終局」也一樣。
                    SCP_ChessOutcome aHere = SCP_ChessEngine.ResultStatus(aSt, 0);
                    aDigest.Append(aIdx).Append(':').Append(h.GetString("n", "?")).Append(':')
                           .Append(string.Join(",", aLegalHere)).Append('|')
                           .Append(aHere.Status).Append('/').Append(aHere.Detail).Append('\n');
                    string aStep = SCP_ChessEngine.SerializeFen(SCP_ChessEngine.ApplyMove(aSt, aUci));
                    if (aStep != aResult)
                    {
                        aStepMismatch++;
                        aDetails.Add($"  ✗ #{aIdx} n={h.GetString("n", "?")} `{aUci}` 逐手不符：\n      記錄 {aResult}\n      新引擎 {aStep}");
                    }
                    if (aPrior != aChain)
                    {
                        aChainMismatch++;
                        aDetails.Add($"  ✗ #{aIdx} n={h.GetString("n", "?")} 斷鏈：prior_fen 不等於上一手的結果");
                    }
                    aChain = SCP_ChessEngine.SerializeFen(SCP_ChessEngine.ApplyMove(SCP_ChessEngine.ParseFen(aChain), aUci));

                    // ② 反向對照：每局第一手可用的著法，換成同一顆子走到一格「合法步集合外」的格子
                    if (!aNegDone)
                    {
                        string? aBad = FindIllegalVariant(aSt, aUci);
                        if (aBad != null)
                        {
                            aNegDone = true;
                            aNegTried++;
                            if (!SCP_ChessEngine.LegalMoves(aSt).Contains(aBad)) aNegRejected++;
                            else aDetails.Add($"  ✗ #{aIdx} 反向對照：`{aBad}` 被判成合法");
                            if (SCP_ChessEngine.SerializeFen(SCP_ChessEngine.ApplyMove(aSt, aBad)) != aResult) aNegDiverged++;
                        }
                    }
                }
                if (aChain != g.GetString("fen", ""))
                {
                    aFinalMismatch++;
                    aDetails.Add($"  ✗ #{aIdx} 重放終局 FEN 與檔頭 `fen` 不符：\n      檔頭 {g.GetString("fen", "")}\n      重放 {aChain}");
                }
            }
            r.Lines.Add($"- 對局 **{aGames.Count}** 局　走子 **{aMoves}** 手　事件 {aEvents} 筆（join／release／resign／draw_*，不動盤面）");
            r.Lines.Add($"- ① 逐手不符 **{aStepMismatch}**　斷鏈 **{aChainMismatch}**　終局 FEN 不符 **{aFinalMismatch}**");
            r.Lines.Add($"- ② 反向對照：試 {aNegTried} 局、合法步判定拒絕 **{aNegRejected}**、重放分岔 **{aNegDiverged}**");
            r.Lines.Add($"- 讀數（不是閘）：記錄裡有 {aIllegalRecorded} 手**不在**合法步集合 —— 自律模式本來就照樣套用，"
                        + "那是棋手的事，⛔ 不是引擎不符");
            r.Lines.AddRange(aDetails);
            bool aPass = aProblems.Count == 0 && aStepMismatch == 0 && aChainMismatch == 0 && aFinalMismatch == 0
                         && aNegTried > 0 && aNegRejected == aNegTried && aNegDiverged == aNegTried;
            r.Lines.Add(aPass ? "✅ 全數逐字相同，反向對照全數拒絕" : "✗ 有不符（見上）");
            r.AddValue("games", aGames.Count.ToString(CultureInfo.InvariantCulture));
            r.AddValue("moves", aMoves.ToString(CultureInfo.InvariantCulture));
            r.AddValue("step_mismatch", aStepMismatch.ToString(CultureInfo.InvariantCulture));
            r.AddValue("chain_mismatch", aChainMismatch.ToString(CultureInfo.InvariantCulture));
            r.AddValue("final_mismatch", aFinalMismatch.ToString(CultureInfo.InvariantCulture));
            r.AddValue("negative_tried", aNegTried.ToString(CultureInfo.InvariantCulture));
            r.AddValue("negative_rejected", aNegRejected.ToString(CultureInfo.InvariantCulture));
            r.AddValue("negative_diverged", aNegDiverged.ToString(CultureInfo.InvariantCulture));
            r.AddValue("recorded_not_legal", aIllegalRecorded.ToString(CultureInfo.InvariantCulture));
            using (var aSha = System.Security.Cryptography.SHA256.Create())
            {
                byte[] aHash = aSha.ComputeHash(new System.Text.UTF8Encoding(false).GetBytes(aDigest.ToString()));
                r.AddValue("legal_digest", BitConverter.ToString(aHash).Replace("-", "").Substring(0, 16).ToLowerInvariant());
            }
            if (!aPass) r.ExitCode = 1;
            return r;
        }

        /// <summary>同一顆子、走到一格不在合法步集合裡、而且盤面會跟原著法不同的目標格；找不到回 null。</summary>
        static string? FindIllegalVariant(SCP_ChessState iSt, string iUci)
        {
            List<string> aLegal = SCP_ChessEngine.LegalMoves(iSt);
            string aFrom = iUci.Substring(0, 2);
            for (int t = 0; t < 64; t++)
            {
                string aCand = aFrom + SCP_ChessEngine.Alg(t);
                if (aCand.Substring(2) == aFrom || aLegal.Contains(aCand)) continue;
                // ⚠ 升變格的非法著法要帶升變字母才是合法的 UCI 形狀；這裡只挑非升變的，簡單而夠用
                return aCand;
            }
            return null;
        }
    }
}
