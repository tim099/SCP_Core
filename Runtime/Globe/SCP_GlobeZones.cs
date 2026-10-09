// 區塊職責：球面的**施工區** —— 「某人正在這塊範圍畫什麼」的公告板（例：「創造日本中」），讓大家分工。
// 物理意義：施工區只是標記，⛔ 不擋任何人下筆、可以互相重疊；畫的仍然走格子事件。
//          範圍是經緯度方框（南,西,北,東）；西 > 東 ＝ 跨 180° 經線。
// 數值影響：一區一檔 `zones/<id>.json`；改的人只能是負責人或成員。
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Json;

namespace SCP.Core.Globe
{
    /// <summary>施工區計畫清單的一項（像任務單的驗收格：勾＝署名）。</summary>
    public sealed class SCP_GlobeZoneItem
    {
        public string Text { get; set; } = "";
        public string AddedBy { get; set; } = "";
        public bool Done { get; set; }
        public string DoneBy { get; set; } = "";
        public string DoneAt { get; set; } = "";
    }

    /// <summary>施工區進度日誌一筆：做了什麼、下一步。</summary>
    public sealed class SCP_GlobeZoneLog
    {
        public string At { get; set; } = "";
        public string Persona { get; set; } = "";
        public string Text { get; set; } = "";
    }

    public sealed class SCP_GlobeZone
    {
        public List<SCP_GlobeZoneItem> Items { get; set; } = new List<SCP_GlobeZoneItem>();
        public List<SCP_GlobeZoneLog> Logs { get; set; } = new List<SCP_GlobeZoneLog>();
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Owner { get; set; } = "";
        public List<string> Members { get; set; } = new List<string>();
        public double South { get; set; }
        public double West { get; set; }
        public double North { get; set; }
        public double East { get; set; }
        /// <summary>active（施工中）｜paused（暫停）｜done（完成）。</summary>
        public string Status { get; set; } = "active";
        public string Note { get; set; } = "";
        public string CreatedAt { get; set; } = "";
        public string UpdatedAt { get; set; } = "";

        public bool Contains(double iLat, double iLon)
        {
            if (iLat < South || iLat > North) return false;
            double lo = Norm(iLon), w = Norm(West), e = Norm(East);
            return w <= e ? lo >= w && lo <= e : lo >= w || lo <= e;   // 西 > 東 ⇒ 跨 180°
        }

        public bool CanEdit(string iPersona) =>
            string.Equals(iPersona, Owner, StringComparison.OrdinalIgnoreCase)
            || Members.Exists(m => string.Equals(m, iPersona, StringComparison.OrdinalIgnoreCase));

        public string BboxText => string.Format(CultureInfo.InvariantCulture, "{0:0.###},{1:0.###},{2:0.###},{3:0.###}", South, West, North, East);

        static double Norm(double iLon)
        {
            double v = iLon % 360;
            if (v > 180) v -= 360;
            if (v < -180) v += 360;
            return v;
        }
    }

    public sealed class SCP_GlobeZones
    {
        public static readonly string[] Statuses = { "active", "paused", "done" };
        readonly SCP_GlobePaths m_Paths;
        public SCP_GlobeZones(SCP_GlobePaths iPaths) { m_Paths = iPaths; }

        public string Dir => Path.Combine(m_Paths.Root, "zones");
        public string FileOf(string iId) => Path.Combine(Dir, iId + ".json");

        public static bool ValidId(string iId)
        {
            if (iId.Length == 0 || iId.Length > 64) return false;
            foreach (char c in iId)
                if (!(c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '-' || c == '_')) return false;
            return true;
        }

        /// <summary>「南,西,北,東」（度）。南 ≤ 北；西 > 東 代表跨 180°。</summary>
        public static bool TryParseBbox(string iText, out double s, out double w, out double n, out double e, out string oWhy)
        {
            s = w = n = e = 0; oWhy = "";
            string[] p = (iText ?? "").Split(',');
            if (p.Length != 4) { oWhy = "bbox 要寫成 南,西,北,東：「" + iText + "」"; return false; }
            var v = new double[4];
            for (int k = 0; k < 4; k++)
                if (!double.TryParse(p[k].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v[k]) || double.IsNaN(v[k]) || double.IsInfinity(v[k]))
                { oWhy = "bbox 不是數字：「" + iText + "」"; return false; }
            s = v[0]; w = v[1]; n = v[2]; e = v[3];
            if (s < -90 || n > 90 || s > n) { oWhy = "bbox 的緯度要 -90 ≤ 南 ≤ 北 ≤ 90：「" + iText + "」"; return false; }
            if (w < -180 || w > 360 || e < -180 || e > 360) { oWhy = "bbox 的經度要在 -180..360：「" + iText + "」"; return false; }
            return true;
        }

        public List<SCP_GlobeZone> List()
        {
            var a = new List<SCP_GlobeZone>();
            if (!Directory.Exists(Dir)) return a;
            var aFiles = new List<string>(Directory.GetFiles(Dir, "*.json"));
            aFiles.Sort(StringComparer.Ordinal);
            foreach (string f in aFiles) a.Add(Read(f));
            return a;
        }

        public SCP_GlobeZone? Find(string iId) => File.Exists(FileOf(iId)) ? Read(FileOf(iId)) : null;

        static SCP_GlobeZone Read(string iFile)
        {
            var z = new SCP_GlobeZone();
            SCP_JsonMapper.Populate(z, SCP_JsonData.Parse(File.ReadAllText(iFile)));
            return z;
        }

        /// <summary>新建；同 id 已存在 ⇒ 丟例外（⛔ 不覆寫別人的區）。</summary>
        public void Create(SCP_GlobeZone iZone)
        {
            if (!ValidId(iZone.Id)) throw new SCP_GlobeException("施工區 id 只能用小寫英數、- 與 _（≤64 字）：" + iZone.Id);
            Directory.CreateDirectory(Dir);
            string aFile = FileOf(iZone.Id);
            if (File.Exists(aFile)) throw new SCP_GlobeException("施工區 id 已經有人用了：" + iZone.Id);
            Write(aFile, iZone);
        }

        public void Save(SCP_GlobeZone iZone) => Write(FileOf(iZone.Id), iZone);

        static void Write(string iFile, SCP_GlobeZone iZone)
        {
            string aTmp = iFile + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            File.WriteAllText(aTmp, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(iZone), true) + "\n");
            SCP.Core.Io.SCP_TextFile.ReplaceOrMove(aTmp, iFile);
        }
    }
}
