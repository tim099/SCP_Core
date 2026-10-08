// 區塊職責：骰面的「可用性」與「優先層」判定 —— 依活動 md 的 `kind` 跑特殊邏輯。
// 物理意義：enum 標記方案（Tim 2026-08-17 拍板）。
//          有些活動**根本做不了**（沒開播的陪看），有些活動**此刻特別該做**（輪到我走的棋、囤太多的券）——
//          兩者不是同一件事，所以判定分兩軸：① visible＝不成立就**隱藏**；② priority＝成立就進**最優先層**（層內仍隨機）。
// 數值影響：只影響骰面的候選集合與排序，不寫任何 state；全部 fail-soft —— 讀不到一律當「條件不成立」（少一個推薦）。
//          唯獨**隱藏**類要格外保守：誤判「沒直播」只是少一項，誤判「有直播」會讓人跑去陪看一個不存在的節目
//          （2026-07-30 孤兒旗標血證）。
// ⚠ 「很久沒做」**不在這裡** —— 那是通用的飢餓置頂（SCP_FreeTimeDice），判準不看 kind；
//   在某個 case 裡重做一份就是兩份飢餓判準，而它們遲早各說各話且兩邊都不報錯。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Chess;
using SCP.Core.Json;

namespace SCP.Core.FreeTime
{
    /// <summary>一個活動經過 kind 特殊邏輯後的判定結果。</summary>
    public struct SCP_FreeTimeGateResult
    {
        /// <summary>false ＝ 條件不成立，**整項從骰面隱藏**。</summary>
        public bool Visible;
        /// <summary>true ＝ 進最優先層（層內仍隨機排序）。</summary>
        public bool Priority;
        /// <summary>附加在活動名後面的字（節目名／優先理由／標記打錯警告）。</summary>
        public string NameSuffix;
    }

    public static class SCP_FreeTimeGating
    {
        /// <summary>
        /// 對單一活動跑 kind 對應的特殊邏輯。iPersona ＝ 正在擲骰的人。
        /// Default 一律 (visible, 非優先)；解析失敗的 kind 也走 Default，但**掛上警告字尾讓它在骰面上顯形**。
        /// </summary>
        public static SCP_FreeTimeGateResult Evaluate(SCP_FreeTimeContext iCtx, SCP_FreeTimeActivity iAct, string iPersona)
        {
            var aRes = new SCP_FreeTimeGateResult { Visible = true, Priority = false, NameSuffix = "" };
            if (iAct == null) return aRes;
            if (iAct.KindParseError.Length > 0)
                aRes.NameSuffix = $" ⚠（kind='{iAct.KindParseError}' 認不得，已當一般活動處理）";

            switch (iAct.Kind)
            {
                case SCP_FreeTimeActivityKind.StreamWatch:
                {
                    // 沒開播 → 隱藏。排尾端的前提是「做得成但不划算」，而沒直播是**根本做不了**。
                    if (!TryGetLiveTitle(iCtx.DataRootRaw, out string? aTitle)) { aRes.Visible = false; return aRes; }
                    aRes.Priority = true;
                    aRes.NameSuffix += string.IsNullOrEmpty(aTitle) ? "（直播中）" : $" 本場節目: {aTitle}";
                    return aRes;
                }

                case SCP_FreeTimeActivityKind.Chess:
                {
                    // 理由一：有未完成棋局、對手也在自由時間、**而且輪到我走**（⭐ 2026-09-11 Tim 加「輪到我」：
                    //   不輪到我時能做的只有等，把「等」頂到最優先會佔掉一個真的做得成的位置）。
                    // **不輪到我時不移除定語** —— 「等對方走」是資訊（有一局在跑），不是推薦。
                    if (TryFindWaitingChess(iCtx, iPersona, out string aOpp, out int aIdx, out bool aMyTurn))
                    {
                        aRes.Priority |= aMyTurn;   // ⚠ `|=` 不是 `=` —— 下面還有第二個置頂理由
                        aRes.NameSuffix += aMyTurn
                            ? $" ♟ 第 {aIdx} 局輪到你，@{aOpp} 也在自由時間"
                            : $" ♟ 第 {aIdx} 局進行中，@{aOpp} 也在自由時間（**等對方走，不急**）";
                    }
                    // 理由二：有人開了一局在等，而我配得上去（⭐ **不看**對方在不在自由時間 —— 一局在等人跟開局的人此刻在不在無關）。
                    // 🩸 `start --vs-open` 躺了三個月（徵人廣播 5 筆、最後一筆 08-17）——**積木存在 ≠ 有人用它**。
                    if (TryFindJoinableChess(iCtx, iPersona, out string aOpener, out int aJoinIdx, out int aMoves, out int aWaiting))
                    {
                        aRes.Priority = true;
                        aRes.NameSuffix += $" 🪑 @{aOpener} 開了一局在等（第 {aJoinIdx} 局，已走 {aMoves} 手"
                                           + (aWaiting > 1 ? $"；共 {aWaiting} 局在等" : "")
                                           + "）—— `match` 直接入座";
                    }
                    return aRes;
                }

                case SCP_FreeTimeActivityKind.CanvasVoucherFull:
                {
                    // 盯**永久券**不是可花總額：限時券本來就會過期、本來就該花掉；會囤起來的是永久券。**不隱藏**。
                    var aBal = iCtx.CanvasBalance(iPersona);
                    if (!aBal.Ok)
                    {
                        // ⛔ 讀不到 ≠ 0 張 —— 不置頂，但要留痕（「沒囤」與「沒量到」在骰面上同形）。
                        string aWarn = "永久繪圖券查不到（" + aBal.Detail + "）⇒ 囤券置頂這一輪沒判";
                        if (!iCtx.Warnings.Contains(aWarn)) iCtx.Warnings.Add(aWarn);
                        return aRes;
                    }
                    int aThreshold = iCtx.Settings.VoucherHoardThreshold;
                    if (aBal.Permanent > aThreshold)
                    {
                        aRes.Priority = true;
                        aRes.NameSuffix += $" 🎟 永久券 {aBal.Permanent} 張（> {aThreshold}）—— 請多多使用";
                    }
                    return aRes;
                }

                default:
                    return aRes;
            }
        }

        // ===========================================================
        // 區塊：直播判定 —— `_live_info.json` 存在 **且** `_config.json.enabled` 沒關。
        // 物理意義：「旗標在＝直播中」這個不變式只有 daemon 一方維護，而停止錄影是直接 Kill ⇒ 每次停播留孤兒旗標。
        //          ⇒ 要跟 `_config.json.enabled` **對帳**：旗標在而開關關著是定義上的矛盾，一律當「沒直播」。
        // 數值影響：讀檔失敗一律回 false（fail-soft；誤判有直播 2026-07-28 讓三個 persona 連兩天被同一個假訊號誤導）。
        // ===========================================================
        public static bool TryGetLiveTitle(string iDataRoot, out string? oTitle)
        {
            oTitle = null;
            try
            {
                string aInfoPath = Path.Combine(iDataRoot, "_screenstream", "_live_info.json");
                if (!File.Exists(aInfoPath)) return false;
                string aCfgPath = Path.Combine(iDataRoot, "_screenstream", "_config.json");
                if (File.Exists(aCfgPath))
                {
                    SCP_JsonData aCfg = SCP_JsonData.Parse(File.ReadAllText(aCfgPath));
                    SCP_JsonData aEnabled = aCfg["enabled"];
                    // 寬鬆吃字串 `"false"`（⛔ 不讓型別差異把「關著」讀成「開著」）。
                    if (aEnabled.Exists && !aEnabled.IsNull && !IsTrue(aEnabled)) return false;
                }
                SCP_JsonData aInfo = SCP_JsonData.Parse(File.ReadAllText(aInfoPath));
                if (aInfo.Contains("stream_title")) oTitle = aInfo["stream_title"].IsNull ? null : aInfo["stream_title"].AsString();
                return true;
            }
            catch (Exception) { return false; }
        }

        static bool IsTrue(SCP_JsonData iV)
        {
            try
            {
                if (iV.Type == SCP_JsonType.Bool) return iV.AsBool();
                string s = iV.AsString().Trim();
                return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase) || s == "1";
            }
            catch (Exception) { return false; }
        }

        // ===========================================================
        // 區塊：棋局判定 —— 「我有未完成的局，而對手此刻也在自由時間裡」。
        // 物理意義：讀取與「我在座、對手是另一個人」的篩選走 `SCP_ChessStore`（與 `senate cmd chess` 同一支）；
        //          本檔只加一格宿主才答得出來的條件：對手的自由時間 session。找到多局取第一個成立的（index 升冪）。
        // ===========================================================
        public static bool TryFindWaitingChess(SCP_FreeTimeContext iCtx, string iPersona,
                                               out string oOpponent, out int oGameIndex, out bool oMyTurn)
        {
            oOpponent = ""; oGameIndex = 0; oMyTurn = false;
            if (string.IsNullOrEmpty(iPersona)) return false;
            try
            {
                List<SCP_JsonData> aGames = SCP_ChessStore.LoadAll(iCtx.DataRootRaw, out _);   // 單一壞檔不該讓整個判定失效
                foreach (var (aGame, aOpp, aIAmWhite) in SCP_ChessStore.MyVersusGames(aGames, iPersona))
                {
                    if (!iCtx.IsInFreeTime(aOpp)) continue;
                    oOpponent = aOpp;
                    oGameIndex = aGame["index"].AsInt();
                    bool? aWhiteToMove = SCP_ChessStore.WhiteToMove(aGame);   // FEN 第二段＝輪到誰
                    oMyTurn = aWhiteToMove.HasValue && aWhiteToMove.Value == aIAmWhite;
                    return true;
                }
            }
            catch (Exception) { /* 骰面照常，只是少一個優先推薦 */ }
            return false;
        }

        // ⭐ 挑選判準**直接呼叫** `SCP_ChessStore.PickMatchCandidate` —— 與 `senate cmd chess op=match` 真的去配的那一支
        //   是同一支函式（以前跨語言兩份，漂掉的症狀是「骰面說有一局在等，match 去了卻開了新局」）。
        public static bool TryFindJoinableChess(SCP_FreeTimeContext iCtx, string iPersona, out string oOpener,
                                                out int oGameIndex, out int oMoves, out int oWaitingCount)
        {
            oOpener = ""; oGameIndex = 0; oMoves = 0; oWaitingCount = 0;
            if (string.IsNullOrEmpty(iPersona)) return false;
            try
            {
                List<SCP_JsonData> aGames = SCP_ChessStore.LoadAll(iCtx.DataRootRaw, out _);
                var (aPick, aCount, _) = SCP_ChessStore.PickMatchCandidate(aGames, iPersona);
                oWaitingCount = aCount;
                if (aPick == null) return false;
                string aWhite = SCP_ChessStore.SeatOf(aPick, "white");
                oOpener = aWhite.Length > 0 ? aWhite : SCP_ChessStore.SeatOf(aPick, "black");
                oGameIndex = aPick["index"].AsInt();
                oMoves = SCP_ChessStore.CountMoves(aPick);
                return true;
            }
            catch (Exception) { /* 骰面照常 */ }
            return false;
        }

        /// <summary>兩人之間有沒有未完的棋局 —— 有就標局號與輪到誰（配對簡報的一欄）。讀不到就留白。</summary>
        public static string ChessNoteWith(SCP_FreeTimeContext iCtx, string iSelf, string iOther)
        {
            try
            {
                List<SCP_JsonData> aGames = SCP_ChessStore.LoadAll(iCtx.DataRootRaw, out _);
                foreach (var (aG, aOpp, aMeW) in SCP_ChessStore.MyVersusGames(aGames, iSelf))
                {
                    if (aOpp != iOther) continue;
                    bool? aWhiteToMove = SCP_ChessStore.WhiteToMove(aG);
                    // 用「對方」不用「他」—— 簡報不該替沒說明稱謂的人做假設
                    string aTurn = aWhiteToMove.HasValue ? ((aWhiteToMove.Value == aMeW) ? "**輪到你**" : "等對方走") : "進行中";
                    return $"♟ 第 {aG["index"].AsInt()} 局 · {aTurn}";
                }
            }
            catch (Exception) { /* 配對表的一欄而已，讀不到就留白，不炸整份簡報 */ }
            return "—";
        }
    }
}
