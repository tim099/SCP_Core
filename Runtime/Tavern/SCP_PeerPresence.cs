// 區塊職責：**另一台主機上在線的人**（跨主機連線）—— 讀法只有這一份；酒館追讀的「在線」段落用它。
// 物理意義：
//   · 寫入端是 Senate 酒館 Server 裡的跨主機連線工作，檔案是 `<機密資料夾>/peer/peer_presence.json`
//     （那個資料夾 git 一律忽略：這份檔每台都不一樣，⛔ 不進版控）。
//   · ⛔ 不寫進本機的 persona lock：lock 是「同一個 persona 不能同時登入兩次」的守衛，對面的人不該擋到這一台。
//   · 三種狀態必須長得不一樣：沒開過（檔不在）／連著（`link=up` 而且夠新）／**不知道**（斷線或檔太舊）。
//     ⚠ 「不知道」不是「對面沒人」。
// 數值影響：純讀。壞檔 ⇒ `Error` 有值、清單空。
// ⚠ 方言限制：C# 9 / netstandard2.1。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Secret;

namespace SCP.Core.Tavern
{
    public sealed class SCP_PeerPresencePerson
    {
        public string Name = "", Agent = "", Status = "";
    }

    public sealed class SCP_PeerPresenceNode
    {
        public string Id = "", Name = "";
        public List<SCP_PeerPresencePerson> Online = new List<SCP_PeerPresencePerson>();
    }

    public sealed class SCP_PeerPresenceView
    {
        /// <summary>檔在不在。不在 ＝ 這台沒開過跨主機連線。</summary>
        public bool Exists;
        public string Role = "", Link = "", Via = "", UpdatedAt = "", Error = "";
        /// <summary>連著而且夠新 ⇒ 名單可信。否則名單是「最後一次知道的」。</summary>
        public bool Live;
        public double AgeSeconds;
        public List<SCP_PeerPresenceNode> Nodes = new List<SCP_PeerPresenceNode>();
    }

    public static class SCP_PeerPresence
    {
        public const string FileName = "peer_presence.json";
        /// <summary>超過這個秒數沒更新 ⇒ 當成不知道（寫入端連著的時候至少每 10 秒寫一次）。</summary>
        public const int StaleSeconds = 90;

        public static string PathOf(string iDataRoot)
            => SCP_SecretStore.ResolveDir(iDataRoot, out _) + "/peer/" + FileName;

        public static SCP_PeerPresenceView Read(string iDataRoot)
        {
            var v = new SCP_PeerPresenceView();
            string aPath = PathOf(iDataRoot);
            if (!File.Exists(aPath)) return v;
            v.Exists = true;
            try
            {
                SCP_JsonData j = SCP_JsonParser.Parse(File.ReadAllText(aPath, Encoding.UTF8));
                v.Role = j.GetString("role", "");
                v.Link = j.GetString("link", "");
                v.Via = j.GetString("via", "");
                v.UpdatedAt = j.GetString("updated_at", "");
                v.AgeSeconds = double.MaxValue;
                if (DateTime.TryParse(v.UpdatedAt, CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime t))
                    v.AgeSeconds = (DateTime.UtcNow - t).TotalSeconds;
                v.Live = v.Link == "up" && v.AgeSeconds <= StaleSeconds;
                if (j["nodes"].IsArray)
                    foreach (SCP_JsonData n in j["nodes"])
                    {
                        var aNode = new SCP_PeerPresenceNode { Id = n.GetString("id", ""), Name = n.GetString("name", "") };
                        if (n["online"].IsArray)
                            foreach (SCP_JsonData p in n["online"])
                                aNode.Online.Add(new SCP_PeerPresencePerson
                                {
                                    Name = p.GetString("name", ""), Agent = p.GetString("agent", ""), Status = p.GetString("status", ""),
                                });
                        v.Nodes.Add(aNode);
                    }
            }
            catch (Exception e) { v.Error = e.GetType().Name + "：" + e.Message; }
            return v;
        }

        /// <summary>
        /// 酒館追讀「在線」段落後面接的那幾行。這台沒開（檔不在、或模式是關的）⇒ 一行都不加。
        /// </summary>
        public static void AppendTo(StringBuilder sb, string iDataRoot)
        {
            SCP_PeerPresenceView v;
            try { v = Read(iDataRoot); } catch (Exception) { return; }
            if (!v.Exists || v.Link == "off") return;
            if (v.Error.Length > 0)
            {
                sb.AppendLine($"## 🌐 另一台主機：**讀取失敗** —— {v.Error}（空 ≠ 沒人）");
                sb.AppendLine();
                return;
            }
            if (!v.Live)
            {
                string aWhy = v.Link == "up" ? $"名單已經 {v.AgeSeconds:0} 秒沒更新" : "連線沒有接上";
                sb.AppendLine($"## 🌐 另一台主機：**不知道**（{aWhy}）—— 不代表對面沒人");
                sb.AppendLine();
                return;
            }
            int aTotal = 0;
            foreach (SCP_PeerPresenceNode n in v.Nodes) aTotal += n.Online.Count;
            sb.AppendLine($"## 🌐 另一台主機在線（{aTotal}）");
            if (v.Nodes.Count == 0) sb.AppendLine("- （連著，但還沒收到對面的名單）");
            foreach (SCP_PeerPresenceNode n in v.Nodes)
            {
                if (n.Online.Count == 0) { sb.AppendLine($"- 〔{n.Name}〕（沒有人在線）"); continue; }
                foreach (SCP_PeerPresencePerson p in n.Online)
                    sb.AppendLine($"- **{p.Name}**（{p.Agent}）〔{n.Name}〕{(p.Status.Length == 0 ? "" : "　💬 " + p.Status)}");
            }
            sb.AppendLine();
        }
    }
}
