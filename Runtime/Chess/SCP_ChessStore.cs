// 區塊職責：棋局存檔（`<data_root>/Chess/games/<n>.json`）的**唯一讀寫層** ＋ 「哪些局在等人」的共用判準。
// 物理意義：TASK-0268 ⑤ —— 這份資料原本有三個解讀端（python chess.py、C# `UCL_FreeTimeGating` 兩支、
//           `Cmd_FreeTime.ChessNoteWith`），各自 `Path.Combine(DataRoot,"Chess","games")` 掃目錄、
//           各自決定「OPEN 座怎麼判」「persona 比對分不分大小寫」。
//           🩸 而它們**已經不一致**：python 比 persona 是區分大小寫，C# 三支都是 OrdinalIgnoreCase。
//           今天沒出事只因為 persona 全是小寫 ⇒ 那是運氣，不是設計。
//           ⇒ 收成這一支之後，判準只有一份：**Ordinal**（寫入端 python 一直是這個，既有資料是它寫的）。
// 數值影響：寫檔走 `SCP_TextFile.WriteCrLf`（UTF-8 無 BOM、CRLF、temp → 取代）；
//           版面＝兩格縮排＋冒號後空格＋空容器寫 `[]`／`{}` —— 與 python `json.dumps(indent=2, ensure_ascii=False)`
//           在 Windows 文字模式寫出的檔同形（TASK-0268 ④：既有 30 局不遷移、不轉檔）。
// ⚠ 本檔**不廣播、不發券** —— 那兩件是宿主能力，走 `SCP_IChessGateway`。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Io;
using SCP.Core.Json;

namespace SCP.Core.Chess
{
    /// <summary>「等人加入」的一局：它在等哪幾座、是不是 solo（可中途切入）。</summary>
    public readonly struct SCP_ChessWaiting
    {
        public readonly SCP_JsonData Game;
        /// <summary>空著的座位（white／black 的子集，順序固定先 white）。</summary>
        public readonly IReadOnlyList<string> OpenSides;
        /// <summary>兩座同一人 ⇒ 可以中途切入。</summary>
        public readonly bool Solo;

        public SCP_ChessWaiting(SCP_JsonData iGame, IReadOnlyList<string> iOpenSides, bool iSolo)
        { Game = iGame; OpenSides = iOpenSides; Solo = iSolo; }
    }

    public static class SCP_ChessStore
    {
        public const string DirName = "Chess";
        public const string GamesDirName = "games";

        static readonly SCP_JsonStyle k_Style = SCP_JsonStyle.Default.WithIndent("  ");

        public static string GamesDir(string iDataRoot)
            => Path.Combine(iDataRoot, DirName, GamesDirName).Replace('\\', '/');

        public static string GamePath(string iDataRoot, int iIndex)
            => GamesDir(iDataRoot) + "/" + iIndex.ToString(CultureInfo.InvariantCulture) + ".json";

        /// <summary>UTC ISO，毫秒三位＋Z（與 python `utcnow_iso` 同形）。</summary>
        public static string UtcNowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

        // ── 讀 ────────────────────────────────────────────────
        /// <summary>檔名是整數的那些局（index 升冪）。檔名不是整數的**略過**（同 python `next_index`）。</summary>
        static List<(int Index, string Path)> GameFiles(string iDataRoot)
        {
            var aOut = new List<(int, string)>();
            string aDir = GamesDir(iDataRoot);
            if (!Directory.Exists(aDir)) return aOut;
            foreach (string aFile in Directory.GetFiles(aDir, "*.json"))
                if (int.TryParse(Path.GetFileNameWithoutExtension(aFile), NumberStyles.Integer,
                                 CultureInfo.InvariantCulture, out int aIdx))
                    aOut.Add((aIdx, aFile.Replace('\\', '/')));
            aOut.Sort((a, b) => a.Item1.CompareTo(b.Item1));
            return aOut;
        }

        /// <summary>
        /// 讀全部對局（index 升冪）。
        /// <para>⚠ 壞檔**不靜默略過**：放進 <paramref name="oProblems"/> —— 呼叫端決定要不要出聲。
        /// 骰面那種「少一個推薦也不該炸」的讀取端可以只看 games；寫入端（match）要把 problems 印出來，
        /// 否則「有一局壞了所以沒配到」會長得跟「沒有局在等」一模一樣。</para>
        /// </summary>
        public static List<SCP_JsonData> LoadAll(string iDataRoot, out List<string> oProblems)
        {
            oProblems = new List<string>();
            var aOut = new List<SCP_JsonData>();
            foreach ((int aIdx, string aPath) in GameFiles(iDataRoot))
            {
                try { aOut.Add(SCP_JsonData.Parse(File.ReadAllText(aPath))); }
                catch (Exception e) { oProblems.Add($"#{aIdx}（{aPath}）讀不了：{e.GetType().Name}: {e.Message}"); }
            }
            return aOut;
        }

        public static SCP_JsonData? Load(string iDataRoot, int iIndex, out string oWhy)
        {
            oWhy = "";
            string aPath = GamePath(iDataRoot, iIndex);
            if (!File.Exists(aPath)) { oWhy = $"❌ 對局 #{iIndex} 不存在"; return null; }
            try { return SCP_JsonData.Parse(File.ReadAllText(aPath)); }
            catch (Exception e) { oWhy = $"❌ 對局 #{iIndex} 讀不了（{aPath}）：{e.GetType().Name}: {e.Message}"; return null; }
        }

        /// <summary>下一個 index ＝ 現有最大檔名 ＋ 1（中間有缺號照樣往後，⛔ 不回填）。</summary>
        public static int NextIndex(string iDataRoot)
        {
            List<(int Index, string Path)> aFiles = GameFiles(iDataRoot);
            return aFiles.Count == 0 ? 0 : aFiles[aFiles.Count - 1].Index + 1;
        }

        // ── 寫 ────────────────────────────────────────────────
        public static void Save(string iDataRoot, SCP_JsonData iGame)
        {
            int aIdx = iGame["index"].AsInt();
            // ⚠ python `json.dumps` 結尾**沒有換行** ⇒ 這裡也不加（多一個換行＝整批檔逐位元組翻紅）。
            SCP_TextFile.WriteCrLf(GamePath(iDataRoot, aIdx), SCP_JsonWriter.Write(iGame, k_Style));
        }

        /// <summary>新局的欄位與**順序**照 python `new_game`（順序是 ④「同 schema」的一部分）。</summary>
        public static SCP_JsonData NewGame(int iIndex, string? iWhite, string? iBlack, string iMode)
        {
            string aNow = UtcNowIso();
            SCP_JsonData aRep = SCP_JsonData.NewObject()
                .Set(SCP_ChessEngine.PositionKey(SCP_ChessEngine.ParseFen(SCP_ChessEngine.StartFen)), 1);
            return SCP_JsonData.NewObject()
                .Set("index", iIndex)
                .Set("ruleid", "chess")
                .Set("mode", iMode)                         // solo | open | versus
                .Set("seats", SCP_JsonData.NewObject()
                    .Set("white", Seat(iWhite))
                    .Set("black", Seat(iBlack)))
                .Set("fen", SCP_ChessEngine.StartFen)
                .Set("prior_fen", "")
                .Set("last_move", "")
                .Set("history", SCP_JsonData.NewArray())
                .Set("repetition", aRep)
                .Set("status", "in_progress")               // in_progress | checkmate | stalemate | draw | resigned
                .Set("result", "")                          // white | black | draw
                .Set("draw_offer", "")
                .Set("created", aNow)
                .Set("updated", aNow);
        }

        /// <summary>座位值：persona 或 JSON null（OPEN）。⛔ 空字串不是 OPEN 的寫法 —— python 寫的是 null。</summary>
        public static SCP_JsonData Seat(string? iPersona)
            => string.IsNullOrEmpty(iPersona) ? SCP_JsonData.NewNull() : SCP_JsonData.NewString(iPersona!);

        /// <summary>寫一筆 history（move／resign／draw／join／release 共用）。say 選填，空就不寫這個欄位。</summary>
        public static void AppendHistory(SCP_JsonData ioGame, string iUci, string iBy,
                                         string iPriorFen, string iResultFen, string iSay)
        {
            SCP_JsonData aHist = ioGame["history"];
            SCP_JsonData aEntry = SCP_JsonData.NewObject()
                .Set("n", aHist.Count + 1)
                .Set("uci", iUci)
                .Set("by", iBy)
                .Set("prior_fen", iPriorFen)
                .Set("result_fen", iResultFen)
                .Set("ts", UtcNowIso());
            if (!string.IsNullOrEmpty(iSay)) aEntry.Set("say", iSay);
            aHist.Add(aEntry);
        }

        // ── 共用判準（lobby／match／自由時間骰面／配對簡報 全部讀這幾支）──
        /// <summary>座位上是誰；OPEN（JSON null、缺欄、空字串）一律回空字串。</summary>
        /// <remarks>
        /// 🩸 2026-09-11（`UCL_FreeTimeGating` 那份舊讀取器的血證，搬過來跟著判準走）：
        ///   OPEN 座是**鍵在、而值是 JSON null**（`"white": null`）。漏判那一格的症狀是 NullRef 被 fail-soft 吞掉
        ///   ⇒ 任何一局有 OPEN 座時骰面的 Chess 優先層整條靜默失效。⚠ 也不能回 `"null"` 字串 ——
        ///   那會讓空座位變成一個叫 "null" 的人。
        /// </remarks>
        public static string SeatOf(SCP_JsonData iGame, string iSide)
        {
            SCP_JsonData aV = iGame["seats"][iSide];
            return aV.Exists && !aV.IsNull ? aV.AsString() : "";
        }

        public static bool IsInProgress(SCP_JsonData iGame) => iGame.GetString("status", "") == "in_progress";

        /// <summary>輪到白嗎（讀 FEN 第二段；讀不到回 null）。</summary>
        public static bool? WhiteToMove(SCP_JsonData iGame)
        {
            string[] aParts = iGame.GetString("fen", "").Split(' ');
            if (aParts.Length < 2) return null;
            return aParts[1] == "w";
        }

        /// <summary>真正的走子數 —— history 也記 join:/release: 這類事件，那些不是手數。</summary>
        public static int CountMoves(SCP_JsonData iGame)
        {
            int aN = 0;
            foreach (SCP_JsonData aH in iGame["history"])
            {
                string aUci = aH.GetString("uci", "");
                if (aUci.Length >= 4 && aUci.IndexOf(':') < 0) aN++;
            }
            return aN;
        }

        /// <summary>「等待加入」的局：in_progress 且（有 OPEN 座 或 solo）。</summary>
        public static List<SCP_ChessWaiting> WaitingGames(IEnumerable<SCP_JsonData> iGames)
        {
            var aOut = new List<SCP_ChessWaiting>();
            foreach (SCP_JsonData g in iGames)
            {
                if (!IsInProgress(g)) continue;
                string w = SeatOf(g, "white"), b = SeatOf(g, "black");
                var aOpen = new List<string>();
                if (w.Length == 0) aOpen.Add("white");
                if (b.Length == 0) aOpen.Add("black");
                bool aSolo = w.Length > 0 && w == b;
                if (aOpen.Count > 0 || aSolo) aOut.Add(new SCP_ChessWaiting(g, aOpen, aSolo));
            }
            return aOut;
        }

        /// <summary>
        /// 自動配對挑一局 —— **純函式**：回 (那一局 或 null, 候選數, 理由)。
        /// <para>排除我已經在座的局（含我自己的 solo）與 <paramref name="iSkip"/>（上一輪被搶掉的）。
        /// 排序：(已走手數, index) 升冪 —— 手數最少優先（接一局走了 50 手的殘局對接手的人不公平，
        /// Tim 2026-09-11 拍板）；同手數取小 index，⛔ 不用會隨時間改變的鍵當決勝（結果要可複驗）。</para>
        /// <para>⭐ 骰面的「有一局在等」（`UCL_FreeTimeGating.TryFindJoinableChess`）與 match 真的去配，
        /// **現在讀的是這同一支** —— 以前是跨語言兩份實作，漂掉的症狀是「骰面說有局在等，match 去了卻開新局」。</para>
        /// </summary>
        public static (SCP_JsonData? Game, int Candidates, string Reason) PickMatchCandidate(
            IEnumerable<SCP_JsonData> iGames, string iPersona, ICollection<int>? iSkip = null)
        {
            var aCands = new List<SCP_JsonData>();
            foreach (SCP_ChessWaiting aW in WaitingGames(iGames))
            {
                int aIdx = aW.Game["index"].AsInt();
                if (iSkip != null && iSkip.Contains(aIdx)) continue;
                if (SeatOf(aW.Game, "white") == iPersona || SeatOf(aW.Game, "black") == iPersona) continue;
                aCands.Add(aW.Game);
            }
            if (aCands.Count == 0) return (null, 0, "沒有可加入的局");
            aCands.Sort((a, b) =>
            {
                int c = CountMoves(a).CompareTo(CountMoves(b));
                return c != 0 ? c : a["index"].AsInt().CompareTo(b["index"].AsInt());
            });
            return (aCands[0], aCands.Count, "已走手數最少");
        }

        /// <summary>
        /// 我在座、而且對手是**另一個人**的進行中對局（OPEN 座與自己的 solo 不算「有對手」）。
        /// <para>骰面「對手在自由時間」與配對簡報「兩人之間有沒有未完的局」都從這裡開始篩。</para>
        /// </summary>
        public static List<(SCP_JsonData Game, string Opponent, bool IAmWhite)> MyVersusGames(
            IEnumerable<SCP_JsonData> iGames, string iPersona)
        {
            var aOut = new List<(SCP_JsonData, string, bool)>();
            if (string.IsNullOrEmpty(iPersona)) return aOut;
            foreach (SCP_JsonData g in iGames)
            {
                if (!IsInProgress(g)) continue;
                string w = SeatOf(g, "white"), b = SeatOf(g, "black");
                bool aW = w == iPersona, aB = b == iPersona;
                if (!aW && !aB) continue;
                string aOpp = aW ? b : w;
                if (aOpp.Length == 0 || aOpp == iPersona) continue;
                aOut.Add((g, aOpp, aW));
            }
            return aOut;
        }
    }
}
