// 區塊職責：西洋棋規則核心 —— FEN 解析／序列化、攻擊判定、合法步、套用一手、終局判定、字母版盤面。
// 物理意義：TASK-0268 從 `chess.py` 逐函式移植（parse_fen / serialize_fen / position_key /
//           is_square_attacked / _pseudo_moves / apply_move / legal_moves / insufficient_material /
//           result_status / render_board）。盤面 = 64 格，index = rank*8 + file（a1=0 … h8=63），
//           白大寫、黑小寫、'.' 空格；白往 rank 增加方向走。
// 數值影響：純函式，零 IO。
//
// 🔴 **「逐字相同」是本檔唯一的正確性標準，不是「更正確」**（TASK-0268 ①②）。
//   既有 30 局是 python 下出來的，而那些 FEN 是它們唯一的真相 ⇒ 新引擎與舊引擎在同一個輸入上
//   只要差一個字元，那一局就從那一手起分岔，而兩邊都會印出一份合法的盤面。
//   ⇒ 所以下面幾格**刻意照抄 python 的行為，即使它們看起來可以更嚴**：
//   · `ApplyMove` **不做合法性檢查**（自律模式：非法步只警示、照樣 relocate）
//   · 車離開角格就拿掉那一側的易位權 —— **不看那顆車是誰的**（python 的判法）
//   · 過路兵格對輪到方的兵一律開放（`t == ep` 就算可吃）
//   · 子力不足只判常見子集（見 `InsufficientMaterial`）
//   ⛔ 想修其中任何一格，先開單、先量既有棋局會不會因此改判 —— 別在移植裡順手修。
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace SCP.Core.Chess
{
    /// <summary>一個盤面狀態（FEN 的六個欄位）。</summary>
    public sealed class SCP_ChessState
    {
        public char[] Board = new char[64];
        /// <summary>'w' 或 'b'。</summary>
        public char Turn = 'w';
        /// <summary>易位權字串（"KQkq" 的子序列，沒有就是 "-"）。</summary>
        public string Castling = "-";
        /// <summary>過路兵目標格 index；-1 ＝ 沒有。</summary>
        public int Ep = -1;
        public int Half;
        public int Full = 1;

        public bool WhiteToMove => Turn == 'w';

        public SCP_ChessState Clone()
        {
            var aCopy = (SCP_ChessState)MemberwiseClone();
            aCopy.Board = (char[])Board.Clone();
            return aCopy;
        }
    }

    /// <summary>終局判定的結果（對應 python `result_status` 回的 tuple）。</summary>
    public readonly struct SCP_ChessOutcome
    {
        /// <summary>in_progress｜checkmate｜stalemate｜draw。</summary>
        public readonly string Status;
        /// <summary>checkmate ⇒ 贏家 white/black；draw ⇒ fifty_move／insufficient_material／threefold_repetition；
        /// in_progress ⇒ "check" 或空字串。</summary>
        public readonly string Detail;

        public SCP_ChessOutcome(string iStatus, string iDetail) { Status = iStatus; Detail = iDetail; }
    }

    public static class SCP_ChessEngine
    {
        public const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        /// <summary>字母版盤面的圖例（廣播與 board 都附這一行）。</summary>
        public const string GlyphLegend = "K/k=王 Q/q=后 R/r=車 B/b=象 N/n=馬 P/p=兵 (大寫=白 小寫=黑) .=空格";

        static readonly int[,] k_KnightOffs = { { 1, 2 }, { 2, 1 }, { 2, -1 }, { 1, -2 }, { -1, -2 }, { -2, -1 }, { -2, 1 }, { -1, 2 } };
        static readonly int[,] k_KingOffs = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 }, { 1, 1 }, { 1, -1 }, { -1, 1 }, { -1, -1 } };
        static readonly int[,] k_BishopDirs = { { 1, 1 }, { 1, -1 }, { -1, 1 }, { -1, -1 } };
        static readonly int[,] k_RookDirs = { { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } };
        static readonly int[,] k_QueenDirs = { { 1, 1 }, { 1, -1 }, { -1, 1 }, { -1, -1 }, { 1, 0 }, { -1, 0 }, { 0, 1 }, { 0, -1 } };
        const string k_Files = "abcdefgh";

        // ── 座標 ──────────────────────────────────────────────
        public static int Sq(int iFile, int iRank) => iRank * 8 + iFile;
        public static int FileOf(int iIdx) => iIdx % 8;
        public static int RankOf(int iIdx) => iIdx / 8;
        public static string Alg(int iIdx) => k_Files[FileOf(iIdx)].ToString() + (RankOf(iIdx) + 1).ToString();
        public static int AlgToIdx(string iAlg) => Sq(k_Files.IndexOf(iAlg[0]), iAlg[1] - '1');

        static bool OnBoard(int iFile, int iRank) => iFile >= 0 && iFile < 8 && iRank >= 0 && iRank < 8;
        static bool IsWhite(char p) => p != '.' && char.IsUpper(p);
        static bool IsBlack(char p) => p != '.' && char.IsLower(p);

        /// <summary>
        /// UCI 字串的**形狀**對不對（`e2e4` / `e7e8q`）。
        /// <para>⚠ 這一格是輸入有效性，**不是**棋規：python 那側遇到壞形狀是直接炸 traceback，
        /// 這裡改成先擋、給一句話 —— 行為差異只在「原本就會當掉」的輸入上。</para>
        /// </summary>
        public static bool IsWellFormedUci(string iUci)
        {
            if (iUci == null || (iUci.Length != 4 && iUci.Length != 5)) return false;
            if (k_Files.IndexOf(iUci[0]) < 0 || iUci[1] < '1' || iUci[1] > '8') return false;
            if (k_Files.IndexOf(iUci[2]) < 0 || iUci[3] < '1' || iUci[3] > '8') return false;
            return iUci.Length == 4 || "qrbn".IndexOf(iUci[4]) >= 0;
        }

        // ── FEN ───────────────────────────────────────────────
        public static SCP_ChessState ParseFen(string iFen)
        {
            string[] aParts = iFen.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var aSt = new SCP_ChessState();
            for (int i = 0; i < 64; i++) aSt.Board[i] = '.';
            string[] aRows = aParts[0].Split('/');
            for (int ri = 0; ri < aRows.Length; ri++)           // rows[0] = rank 8
            {
                int r = 7 - ri, f = 0;
                foreach (char ch in aRows[ri])
                {
                    if (char.IsDigit(ch)) f += ch - '0';
                    else { aSt.Board[Sq(f, r)] = ch; f++; }
                }
            }
            aSt.Turn = aParts.Length > 1 ? aParts[1][0] : 'w';
            aSt.Castling = aParts.Length > 2 ? aParts[2] : "-";
            aSt.Ep = aParts.Length > 3 && aParts[3] != "-" ? AlgToIdx(aParts[3]) : -1;
            aSt.Half = aParts.Length > 4 ? int.Parse(aParts[4]) : 0;
            aSt.Full = aParts.Length > 5 ? int.Parse(aParts[5]) : 1;
            return aSt;
        }

        public static string SerializeFen(SCP_ChessState iSt)
        {
            var aSb = new StringBuilder();
            for (int r = 7; r >= 0; r--)
            {
                int aEmpty = 0;
                for (int f = 0; f < 8; f++)
                {
                    char p = iSt.Board[Sq(f, r)];
                    if (p == '.') { aEmpty++; continue; }
                    if (aEmpty > 0) { aSb.Append(aEmpty); aEmpty = 0; }
                    aSb.Append(p);
                }
                if (aEmpty > 0) aSb.Append(aEmpty);
                if (r > 0) aSb.Append('/');
            }
            string aEp = iSt.Ep >= 0 ? Alg(iSt.Ep) : "-";
            return $"{aSb} {iSt.Turn} {iSt.Castling} {aEp} {iSt.Half} {iSt.Full}";
        }

        /// <summary>重複判定用 key：子力擺放 ＋ 輪誰 ＋ 易位權 ＋ 過路兵（不含 half/full）。</summary>
        public static string PositionKey(SCP_ChessState iSt)
        {
            string[] aParts = SerializeFen(iSt).Split(' ');
            return string.Join(" ", aParts, 0, 4);
        }

        // ── 攻擊與將軍 ────────────────────────────────────────
        /// <summary>target 格是否被 byWhite 那一方的任一子攻擊。</summary>
        public static bool IsSquareAttacked(char[] iBoard, int iTarget, bool iByWhite)
        {
            int tf = FileOf(iTarget), tr = RankOf(iTarget);
            // 兵：攻擊者的兵相對防守格在哪個 rank 偏移
            int pr = iByWhite ? -1 : 1;
            for (int df = -1; df <= 1; df += 2)
            {
                int f = tf + df, r = tr + pr;
                if (OnBoard(f, r) && iBoard[Sq(f, r)] == (iByWhite ? 'P' : 'p')) return true;
            }
            for (int k = 0; k < 8; k++)
            {
                int f = tf + k_KnightOffs[k, 0], r = tr + k_KnightOffs[k, 1];
                if (OnBoard(f, r) && iBoard[Sq(f, r)] == (iByWhite ? 'N' : 'n')) return true;
            }
            for (int k = 0; k < 8; k++)
            {
                int f = tf + k_KingOffs[k, 0], r = tr + k_KingOffs[k, 1];
                if (OnBoard(f, r) && iBoard[Sq(f, r)] == (iByWhite ? 'K' : 'k')) return true;
            }
            if (SlideHits(iBoard, tf, tr, k_BishopDirs, iByWhite ? "BQ" : "bq")) return true;
            if (SlideHits(iBoard, tf, tr, k_RookDirs, iByWhite ? "RQ" : "rq")) return true;
            return false;
        }

        static bool SlideHits(char[] iBoard, int tf, int tr, int[,] iDirs, string iWant)
        {
            for (int d = 0; d < iDirs.GetLength(0); d++)
            {
                int df = iDirs[d, 0], dr = iDirs[d, 1];
                int f = tf + df, r = tr + dr;
                while (OnBoard(f, r))
                {
                    char p = iBoard[Sq(f, r)];
                    if (p != '.')
                    {
                        if (iWant.IndexOf(p) >= 0) return true;
                        break;
                    }
                    f += df; r += dr;
                }
            }
            return false;
        }

        public static int KingIdx(char[] iBoard, bool iWhite)
        {
            char k = iWhite ? 'K' : 'k';
            for (int i = 0; i < 64; i++) if (iBoard[i] == k) return i;
            return -1;
        }

        public static bool InCheck(SCP_ChessState iSt, bool iWhite)
        {
            int ki = KingIdx(iSt.Board, iWhite);
            return ki >= 0 && IsSquareAttacked(iSt.Board, ki, !iWhite);
        }

        // ── 產生步 ────────────────────────────────────────────
        /// <summary>輪到方的偽合法步（尚未過濾自將），UCI 字串，**順序與 python 相同**。</summary>
        static List<string> PseudoMoves(SCP_ChessState iSt)
        {
            char[] b = iSt.Board;
            bool aWhite = iSt.WhiteToMove;
            Func<char, bool> own = aWhite ? (Func<char, bool>)IsWhite : IsBlack;
            Func<char, bool> enemy = aWhite ? (Func<char, bool>)IsBlack : IsWhite;
            var aMoves = new List<string>();
            for (int i = 0; i < 64; i++)
            {
                char p = b[i];
                if (p == '.' || !own(p)) continue;
                int f = FileOf(i), r = RankOf(i);
                char u = char.ToUpperInvariant(p);
                if (u == 'P')
                {
                    int fwd = aWhite ? 1 : -1;
                    int startRank = aWhite ? 1 : 6;
                    int lastRank = aWhite ? 7 : 0;
                    int r1 = r + fwd;
                    if (r1 >= 0 && r1 < 8 && b[Sq(f, r1)] == '.')
                    {
                        AddPawn(aMoves, i, Sq(f, r1), r1 == lastRank);
                        if (r == startRank && b[Sq(f, r + 2 * fwd)] == '.')
                            aMoves.Add(Alg(i) + Alg(Sq(f, r + 2 * fwd)));
                    }
                    for (int df = -1; df <= 1; df += 2)
                    {
                        int f2 = f + df;
                        if (f2 >= 0 && f2 < 8 && r1 >= 0 && r1 < 8)
                        {
                            int t = Sq(f2, r1);
                            if (enemy(b[t]) || t == iSt.Ep) AddPawn(aMoves, i, t, r1 == lastRank);
                        }
                    }
                }
                else if (u == 'N' || u == 'K')
                {
                    int[,] aOffs = u == 'N' ? k_KnightOffs : k_KingOffs;
                    for (int k = 0; k < 8; k++)
                    {
                        int f2 = f + aOffs[k, 0], r2 = r + aOffs[k, 1];
                        if (OnBoard(f2, r2) && !own(b[Sq(f2, r2)])) aMoves.Add(Alg(i) + Alg(Sq(f2, r2)));
                    }
                    if (u == 'K') CastleMoves(iSt, aMoves, aWhite);
                }
                else
                {
                    int[,] aDirs = u == 'B' ? k_BishopDirs : u == 'R' ? k_RookDirs : k_QueenDirs;
                    for (int d = 0; d < aDirs.GetLength(0); d++)
                    {
                        int df = aDirs[d, 0], dr = aDirs[d, 1];
                        int f2 = f + df, r2 = r + dr;
                        while (OnBoard(f2, r2))
                        {
                            int t = Sq(f2, r2);
                            if (b[t] == '.') aMoves.Add(Alg(i) + Alg(t));
                            else
                            {
                                if (enemy(b[t])) aMoves.Add(Alg(i) + Alg(t));
                                break;
                            }
                            f2 += df; r2 += dr;
                        }
                    }
                }
            }
            return aMoves;
        }

        static void AddPawn(List<string> ioMoves, int iFrom, int iTo, bool iPromo)
        {
            if (iPromo) foreach (char pc in "qrbn") ioMoves.Add(Alg(iFrom) + Alg(iTo) + pc);
            else ioMoves.Add(Alg(iFrom) + Alg(iTo));
        }

        static void CastleMoves(SCP_ChessState iSt, List<string> ioMoves, bool iWhite)
        {
            char[] b = iSt.Board;
            string aRights = iSt.Castling;
            int rank = iWhite ? 0 : 7;
            int ke = Sq(4, rank);
            if (b[ke] != (iWhite ? 'K' : 'k')) return;
            if (InCheck(iSt, iWhite)) return;
            char kside = iWhite ? 'K' : 'k', qside = iWhite ? 'Q' : 'q';
            char aRook = iWhite ? 'R' : 'r';
            // 王翼：f,g 空 ＋ f,g 不被攻擊 ＋ h 角有車
            if (aRights.IndexOf(kside) >= 0 && b[Sq(5, rank)] == '.' && b[Sq(6, rank)] == '.' && b[Sq(7, rank)] == aRook)
                if (!IsSquareAttacked(b, Sq(5, rank), !iWhite) && !IsSquareAttacked(b, Sq(6, rank), !iWhite))
                    ioMoves.Add(Alg(ke) + Alg(Sq(6, rank)));
            // 后翼：b,c,d 空 ＋ d,c 不被攻擊 ＋ a 角有車
            if (aRights.IndexOf(qside) >= 0 && b[Sq(1, rank)] == '.' && b[Sq(2, rank)] == '.' && b[Sq(3, rank)] == '.'
                && b[Sq(0, rank)] == aRook)
                if (!IsSquareAttacked(b, Sq(3, rank), !iWhite) && !IsSquareAttacked(b, Sq(2, rank), !iWhite))
                    ioMoves.Add(Alg(ke) + Alg(Sq(2, rank)));
        }

        /// <summary>
        /// 套用一手（pattern-based，信任出手）⇒ 回**新**的 state。
        /// <para>⚠ **不做合法性 reject** —— 易位／過路兵／升變靠形狀偵測；非法步也照樣 relocate（自律模式）。
        /// 呼叫端要先過 <see cref="IsWellFormedUci"/>。</para>
        /// </summary>
        public static SCP_ChessState ApplyMove(SCP_ChessState iSt, string iUci)
        {
            SCP_ChessState ns = iSt.Clone();
            char[] b = ns.Board;
            int frm = AlgToIdx(iUci.Substring(0, 2));
            int to = AlgToIdx(iUci.Substring(2, 2));
            char promo = iUci.Length >= 5 ? char.ToLowerInvariant(iUci[4]) : '\0';
            bool white = iSt.WhiteToMove;
            char p = b[frm];
            char u = p != '.' ? char.ToUpperInvariant(p) : '\0';
            int ff = FileOf(frm), fr = RankOf(frm);
            int tf = FileOf(to), tr = RankOf(to);
            bool captured = b[to] != '.';

            bool isCastle = u == 'K' && Math.Abs(tf - ff) == 2;
            bool isEp = u == 'P' && tf != ff && b[to] == '.' && to == iSt.Ep;
            int lastRank = white ? 7 : 0;
            bool isPromo = u == 'P' && tr == lastRank;

            // 主移動
            b[frm] = '.';
            if (isPromo)
            {
                char pc = promo != '\0' ? promo : 'q';
                b[to] = white ? char.ToUpperInvariant(pc) : char.ToLowerInvariant(pc);
            }
            else b[to] = p;
            // 過路兵：移除被吃的兵（與 to 同 file、與 from 同 rank）
            if (isEp) { b[Sq(tf, fr)] = '.'; captured = true; }
            // 易位：同步移車
            if (isCastle)
            {
                int rank = white ? 0 : 7;
                if (tf == 6) { b[Sq(5, rank)] = b[Sq(7, rank)]; b[Sq(7, rank)] = '.'; }
                else if (tf == 2) { b[Sq(3, rank)] = b[Sq(0, rank)]; b[Sq(0, rank)] = '.'; }
            }

            // 易位權（⚠ 車離開角格不看是誰的車 —— 照 python，見檔頭）
            var aRights = new HashSet<char>(ns.Castling);
            aRights.Remove('-');
            if (u == 'K')
            {
                if (white) { aRights.Remove('K'); aRights.Remove('Q'); }
                else { aRights.Remove('k'); aRights.Remove('q'); }
            }
            if (u == 'R')
            {
                if (frm == Sq(0, 0)) aRights.Remove('Q');
                else if (frm == Sq(7, 0)) aRights.Remove('K');
                else if (frm == Sq(0, 7)) aRights.Remove('q');
                else if (frm == Sq(7, 7)) aRights.Remove('k');
            }
            // 角上的車被吃 ⇒ 該側失去易位權
            if (to == Sq(0, 0)) aRights.Remove('Q');
            if (to == Sq(7, 0)) aRights.Remove('K');
            if (to == Sq(0, 7)) aRights.Remove('q');
            if (to == Sq(7, 7)) aRights.Remove('k');
            var aCastle = new StringBuilder();
            foreach (char c in "KQkq") if (aRights.Contains(c)) aCastle.Append(c);
            ns.Castling = aCastle.Length > 0 ? aCastle.ToString() : "-";

            // 過路兵目標
            ns.Ep = u == 'P' && Math.Abs(tr - fr) == 2 ? Sq(ff, (fr + tr) / 2) : -1;
            // 半回合（50 步規則）：兵動或吃子歸零，否則 +1
            ns.Half = u == 'P' || captured ? 0 : iSt.Half + 1;
            if (iSt.Turn == 'b') ns.Full = iSt.Full + 1;
            ns.Turn = white ? 'b' : 'w';
            return ns;
        }

        /// <summary>合法步：過濾掉走完之後己方王被將的偽合法步。</summary>
        public static List<string> LegalMoves(SCP_ChessState iSt)
        {
            bool aWhite = iSt.WhiteToMove;
            var aOut = new List<string>();
            foreach (string m in PseudoMoves(iSt))
                if (!InCheck(ApplyMove(iSt, m), aWhite)) aOut.Add(m);
            return aOut;
        }

        /// <summary>子力不足判和（常見子集）：K vs K／K+單一輕子 vs K／盤上恰兩象且同色格。</summary>
        public static bool InsufficientMaterial(char[] iBoard)
        {
            var aNonKing = new List<char>();
            foreach (char p in iBoard)
                if (p != '.' && char.ToUpperInvariant(p) != 'K') aNonKing.Add(char.ToUpperInvariant(p));
            if (aNonKing.Count == 0) return true;
            if (aNonKing.Count == 1 && (aNonKing[0] == 'B' || aNonKing[0] == 'N')) return true;
            if (aNonKing.Count <= 2 && aNonKing.TrueForAll(c => c == 'B'))
            {
                var aBsq = new List<int>();
                for (int i = 0; i < 64; i++) if (iBoard[i] == 'B' || iBoard[i] == 'b') aBsq.Add(i);
                if (aBsq.Count == 2)
                {
                    int c0 = (FileOf(aBsq[0]) + RankOf(aBsq[0])) % 2;
                    int c1 = (FileOf(aBsq[1]) + RankOf(aBsq[1])) % 2;
                    return c0 == c1;
                }
            }
            return false;
        }

        public static SCP_ChessOutcome ResultStatus(SCP_ChessState iSt, int iRepCount)
        {
            List<string> aLegal = LegalMoves(iSt);
            bool aWhite = iSt.WhiteToMove;
            bool aChecked = InCheck(iSt, aWhite);
            if (aLegal.Count == 0)
                return aChecked
                    ? new SCP_ChessOutcome("checkmate", aWhite ? "black" : "white")   // 輪到方被將死 ⇒ 對手贏
                    : new SCP_ChessOutcome("stalemate", "");
            if (iSt.Half >= 100) return new SCP_ChessOutcome("draw", "fifty_move");
            if (InsufficientMaterial(iSt.Board)) return new SCP_ChessOutcome("draw", "insufficient_material");
            if (iRepCount >= 3) return new SCP_ChessOutcome("draw", "threefold_repetition");
            return new SCP_ChessOutcome("in_progress", aChecked ? "check" : "");
        }

        // ── 渲染 ──────────────────────────────────────────────
        /// <summary>字母版盤面（ASCII 恆等寬，跨字型不歪）。包 code block 由呼叫端處理。</summary>
        public static string RenderBoard(SCP_ChessState iSt, string iLastMove)
        {
            var aSb = new StringBuilder("  a b c d e f g h");
            for (int r = 7; r >= 0; r--)
            {
                aSb.Append('\n').Append(r + 1);
                for (int f = 0; f < 8; f++) aSb.Append(' ').Append(iSt.Board[Sq(f, r)]);
            }
            if (!string.IsNullOrEmpty(iLastMove)) aSb.Append("\nlast: ").Append(iLastMove);
            return aSb.ToString();
        }
    }
}
