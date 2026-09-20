// 區塊職責：酒館的**非訊息**讀取層（Senate 側）—— 房間 meta／成員／身分／note／quest 事件。
// 物理意義：TASK-0247。訊息那半在 `SCP_TavernRead`（TASK-0240 交付），本檔補的是
//           `op=read`／`members`／`listrooms`／`note_read`／`note_list`／`events_since` 用得到的其餘資料。
// 數值影響：**純讀**。⛔ 一個位元組都不寫進 `ChatTavern/`（TASK-0247 驗收⑤）——
//           包括「目錄不在就建一個」那種順手：建目錄也是寫。
//
// ⚠ 搬過來的是**讀取本身**，⛔ 不是 `UCL_ChatTavernIO`（1,587 行）。
//   逐支量過 Editor 側那 6 個 op 真正呼叫的方法（大括號配對切範圍，⛔ 不是固定行數視窗）：
//   `GetRoom`／`LoadRooms`／`ReadCurrentSeq`／`LoadMembers`／`LoadIdentities`／
//   `ReadNote`／`ListNoteKeys`／`GetNotePath`／`QuestIO.LoadAllEvents` —— 就這 9 支，全部純讀。
//
// 🔴 `task_list`／`task_next`／`task_state` **不在本檔射程**：它們呼叫 `AutoRecoverStaleLeases`，
//   而那支會 `AppendEvent` ⇒ 它們是「讀為主、寫一格」，與 `catchup`／`inbox_read` 同一類，
//   歸 TASK-0106 那一側。⛔ 別因為名字裡有 list 就把它們當純讀搬進來。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Tavern
{
    /// <summary>房間的 `meta.json`。⚠ 欄位名逐字對齊落盤，⛔ 不改名（改名要動既有 20,000 筆資料的讀者）。</summary>
    public sealed class SCP_TavernRoomMeta
    {
        public string Id = "";
        public string Name = "";
        public string Description = "";
        public string CreatedAt = "";
    }

    /// <summary>`identities.json` 的一筆。</summary>
    public sealed class SCP_TavernIdentity
    {
        public string Id = "";
        public string DisplayName = "";
        public string Kind = "";
    }

    public static class SCP_TavernRooms
    {
        /// <summary>`<資料根>/ChatTavern/rooms/<room>/`。</summary>
        public static string RoomDir(string iDataRoot, string iRoom)
            => Path.Combine(SCP_TavernMsgIndex.RoomsRoot(iDataRoot), iRoom).Replace('\\', '/');

        /// <summary>
        /// `&lt;資料根&gt;/ChatTavern/identities.json`（**全域一份**，不分房）。
        /// <para>⚠ 從 <see cref="SCP_TavernMsgIndex.RoomsRoot"/> 往上一層推 ——
        /// ⛔ 不自己 `Combine(iDataRoot, "ChatTavern")` 再組一份：那是第二個決定點。</para>
        /// </summary>
        public static string IdentitiesPath(string iDataRoot)
            => (Path.GetDirectoryName(SCP_TavernMsgIndex.RoomsRoot(iDataRoot)) ?? iDataRoot)
               .Replace('\\', '/') + "/identities.json";

        public static string MetaPath(string iDataRoot, string iRoom)
            => Path.Combine(RoomDir(iDataRoot, iRoom), "meta.json").Replace('\\', '/');

        public static string MembersPath(string iDataRoot, string iRoom)
            => Path.Combine(RoomDir(iDataRoot, iRoom), "members.json").Replace('\\', '/');

        public static string SeqPath(string iDataRoot, string iRoom)
            => Path.Combine(RoomDir(iDataRoot, iRoom), "_seq.txt").Replace('\\', '/');

        public static string NotesDir(string iDataRoot, string iRoom)
            => Path.Combine(RoomDir(iDataRoot, iRoom), "notes").Replace('\\', '/');

        public static string NotePath(string iDataRoot, string iRoom, string iKey)
            => Path.Combine(NotesDir(iDataRoot, iRoom), iKey + ".md").Replace('\\', '/');

        /// <summary>
        /// 一房的 meta；**房間不存在回 null**。
        /// <para>⚠ 判準是 `meta.json` 在不在，⛔ 不是目錄在不在 —— 目錄可能只是別人剛建了 `messages/`。</para>
        /// </summary>
        public static SCP_TavernRoomMeta? LoadRoomMeta(string iDataRoot, string iRoom)
        {
            string aPath = MetaPath(iDataRoot, iRoom);
            if (!File.Exists(aPath)) return null;
            try
            {
                SCP_JsonData aJson = SCP_JsonParser.Parse(File.ReadAllText(aPath));
                return new SCP_TavernRoomMeta
                {
                    Id = aJson.GetString("id", iRoom),
                    Name = aJson.GetString("name", ""),
                    Description = aJson.GetString("description", ""),
                    CreatedAt = aJson.GetString("created_at", ""),
                };
            }
            catch (Exception)
            {
                // ⛔ 讀不動不等於不存在 —— 但本層沒有出口可以說這件事，
                //   所以回一個**帶 id、其餘空**的 meta：呼叫端仍看得到這一房存在。
                //   （名字空著會在輸出上看得出來，而回 null 會讓它整個消失。）
                return new SCP_TavernRoomMeta { Id = iRoom };
            }
        }

        /// <summary>
        /// 列出所有房間的 meta（依 id 排序）。
        /// <para>⚠ 判準是**有 `meta.json`**，而 <see cref="SCP_TavernRead.EnumerateRoomIds"/> 的判準是
        /// **有 `messages/`** —— 兩者不是同一個集合，⛔ 別互相代用。
        /// Editor 側 `listrooms` 走的是前者（`LoadRooms` → `EnumerateRoomIds` → `LoadRoomMeta`）。</para>
        /// </summary>
        public static List<SCP_TavernRoomMeta> LoadRooms(string iDataRoot)
        {
            var aOut = new List<SCP_TavernRoomMeta>();
            string aRoot = SCP_TavernMsgIndex.RoomsRoot(iDataRoot);
            if (!Directory.Exists(aRoot)) return aOut;
            var aIds = new List<string>();
            foreach (string aDir in Directory.GetDirectories(aRoot))
            {
                string aId = Path.GetFileName(aDir);
                if (File.Exists(Path.Combine(aDir, "meta.json"))) aIds.Add(aId);
            }
            aIds.Sort(StringComparer.Ordinal);
            foreach (string aId in aIds)
            {
                SCP_TavernRoomMeta? aMeta = LoadRoomMeta(iDataRoot, aId);
                if (aMeta != null) aOut.Add(aMeta);
            }
            return aOut;
        }

        /// <summary>目前的 seq（`_seq.txt`）。檔不在或解不出回 0 —— 與 Editor 側 `ReadCurrentSeq` 同語意。</summary>
        public static int ReadCurrentSeq(string iDataRoot, string iRoom)
        {
            string aPath = SeqPath(iDataRoot, iRoom);
            if (!File.Exists(aPath)) return 0;
            try { return int.TryParse(File.ReadAllText(aPath).Trim(), out int aVal) ? aVal : 0; }
            catch (Exception) { return 0; }
        }

        /// <summary>一房的成員 id（落盤順序，⛔ 不排序 —— Editor 側就是照落盤順序印的）。</summary>
        public static List<string> LoadMemberIds(string iDataRoot, string iRoom)
        {
            var aOut = new List<string>();
            string aPath = MembersPath(iDataRoot, iRoom);
            if (!File.Exists(aPath)) return aOut;
            try
            {
                SCP_JsonData aJson = SCP_JsonParser.Parse(File.ReadAllText(aPath));
                if (!aJson.Contains("member_ids")) return aOut;
                SCP_JsonData aArr = aJson["member_ids"];
                for (int i = 0; i < aArr.Count; ++i) aOut.Add(aArr[i].AsString());
            }
            catch (Exception) { /* 壞檔 ⇒ 回空；⚠ 呼叫端印出來的「0 人」因此可能是「讀不動」*/ }
            return aOut;
        }

        /// <summary>全域身分表。key ＝ id。</summary>
        public static Dictionary<string, SCP_TavernIdentity> LoadIdentities(string iDataRoot)
        {
            var aOut = new Dictionary<string, SCP_TavernIdentity>(StringComparer.Ordinal);
            string aPath = IdentitiesPath(iDataRoot);
            if (!File.Exists(aPath)) return aOut;
            try
            {
                SCP_JsonData aJson = SCP_JsonParser.Parse(File.ReadAllText(aPath));
                if (!aJson.Contains("identities")) return aOut;
                SCP_JsonData aArr = aJson["identities"];
                for (int i = 0; i < aArr.Count; ++i)
                {
                    SCP_JsonData aIt = aArr[i];
                    string aId = aIt.GetString("id", "");
                    if (aId.Length == 0) continue;
                    aOut[aId] = new SCP_TavernIdentity
                    {
                        Id = aId,
                        DisplayName = aIt.GetString("display_name", ""),
                        Kind = aIt.GetString("kind", ""),
                    };
                }
            }
            catch (Exception) { /* 同上 */ }
            return aOut;
        }

        /// <summary>note 的 key 清單（`*.md` 的檔名，不分大小寫排序 —— 與 Editor 側同一個比較器）。</summary>
        public static List<string> ListNoteKeys(string iDataRoot, string iRoom)
        {
            var aOut = new List<string>();
            string aDir = NotesDir(iDataRoot, iRoom);
            if (!Directory.Exists(aDir)) return aOut;
            foreach (string aFile in Directory.GetFiles(aDir, "*.md"))
                aOut.Add(Path.GetFileNameWithoutExtension(aFile));
            aOut.Sort(StringComparer.OrdinalIgnoreCase);
            return aOut;
        }

        /// <summary>note 內容；**不存在回 null**（⛔ 不回空字串 —— 空的 note 是合法的）。</summary>
        public static string? ReadNote(string iDataRoot, string iRoom, string iKey)
        {
            string aPath = NotePath(iDataRoot, iRoom, iKey);
            if (!File.Exists(aPath)) return null;
            return File.ReadAllText(aPath);
        }
    }
}
