// 區塊職責：quest 事件的**讀取層**（Senate 側）—— `events_since` 要的那一份。
// 物理意義：TASK-0247。事件落在 `rooms/<room>/events/<yyyy-MM-dd>/<HHmmss_fff_uuid__type>.json`，
//           一事件一檔（T38 之後不再是 events.jsonl）。
// 數值影響：**純讀**。⛔ 不寫、不建目錄。
//
// 🔴 `seq` **不在檔案裡** —— 它是讀的時候**依相對路徑字典序**現算出來的（1-based）。
//   ⇒ 這件事有兩個後果，兩個都要記：
//   ① 本層必須**逐字沿用** Editor 側的排序規則（`string.CompareOrdinal` 比相對路徑），
//      換一種排序 ⇒ 同一筆事件在兩端拿到**不同的 seq**，而 `events_since` 的輸出仍然完全合理。
//   ② 中間插進一個更早的檔（例如補寫歷史）會讓**它之後所有事件的 seq 位移一格** ——
//      那不是本層造成的，但 `since_seq` 的使用者會踩到。⛔ 本層不「修正」它：
//      修正的話兩端就不一致了，而不一致的那一端沒有人會發現。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Tavern
{
    /// <summary>一筆 quest 事件。欄位名逐字對齊落盤。</summary>
    public sealed class SCP_TavernQuestEvent
    {
        /// <summary>⚠ 現算的（1-based，依相對路徑字典序），**不是檔案裡的欄位**。見檔頭。</summary>
        public int Seq;

        public string Ts = "";
        public string Actor = "";
        public string Type = "";
        public string TaskId = "";
        public Dictionary<string, string> Data = new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public static class SCP_TavernQuestRead
    {
        public static string EventsRoot(string iDataRoot, string iRoom)
            => Path.Combine(SCP_TavernRooms.RoomDir(iDataRoot, iRoom), "events").Replace('\\', '/');

        /// <summary>
        /// 讀一房全部 quest 事件（排序與 seq 計算逐字照 Editor 側 `PerMsgFile.LoadAllEvents`）。
        /// <para>⚠ 讀不動的檔**計入 <paramref name="oWarn"/>**，⛔ 不靜默跳過 ——
        /// 靜默跳過會讓之後每一筆的 seq 都少一格，而輸出看起來完全正常。</para>
        /// </summary>
        public static List<SCP_TavernQuestEvent> LoadAll(string iDataRoot, string iRoom, List<string>? oWarn)
        {
            var aOut = new List<SCP_TavernQuestEvent>();
            string aRoot = EventsRoot(iDataRoot, iRoom);
            if (!Directory.Exists(aRoot)) return aOut;

            string[] aFiles = Directory.GetFiles(aRoot, "*.json", SearchOption.AllDirectories);
            // ⚠ 比的是**相對路徑**（含日期目錄）而不是檔名：跨日的先後靠目錄名決定。
            //   ⛔ 別改成比檔名 —— 那會讓不同日期的同一時刻互相穿插，而 seq 照樣算得出來。
            Array.Sort(aFiles, (a, b) =>
            {
                string ra = a.Substring(aRoot.Length).Replace('\\', '/');
                string rb = b.Substring(aRoot.Length).Replace('\\', '/');
                return string.CompareOrdinal(ra, rb);
            });

            int aSeq = 0;
            foreach (string aFile in aFiles)
            {
                SCP_TavernQuestEvent? aEvent = null;
                try
                {
                    SCP_JsonData aJson = SCP_JsonParser.Parse(File.ReadAllText(aFile));
                    aEvent = new SCP_TavernQuestEvent
                    {
                        Ts = aJson.GetString("ts", ""),
                        Actor = aJson.GetString("actor", ""),
                        Type = aJson.GetString("type", ""),
                        TaskId = aJson.GetString("task_id", ""),
                    };
                    if (aJson.Contains("data"))
                    {
                        SCP_JsonData aData = aJson["data"];
                        foreach (string aK in aData.Keys) aEvent.Data[aK] = aData.GetString(aK, "");
                    }
                }
                catch (Exception e)
                {
                    if (oWarn != null)
                        oWarn.Add("⚠ 讀不動事件檔 " + Path.GetFileName(aFile) + "：" + e.Message
                                  + "（本筆不佔 seq —— 與 Editor 側同語意）");
                }
                // ⚠ **號在這裡才發**：Editor 側是 `if (e != null) e.seq = ++seq`
                //   ⇒ 解析失敗那筆**不佔號**。號在 try 裡面發的話，一個壞檔會讓
                //   它之後每一筆的 seq 都比 Editor 側多一 —— 而兩邊的輸出都合理。
                if (aEvent == null) continue;
                aEvent.Seq = ++aSeq;
                aOut.Add(aEvent);
            }
            return aOut;
        }
    }
}
