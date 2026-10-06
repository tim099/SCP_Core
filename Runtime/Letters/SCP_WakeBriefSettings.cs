// 區塊職責：早安 brief 與回傳檔大檔提示的**可調參數**（`<資料根>/ChatTavern/brief_settings.json`）的唯一讀寫層（TASK-0419）。
// 物理意義：brief 各層的顯示量（主檔上限、見樹合併、見林 overdue、見人、回憶）＋ CLI「📄 回傳檔」的大檔提示門檻。
//          讀取端：SCP_WakeBrief（每次 Build 讀一次）、SCP_ReadHint（每次印回傳檔讀一次）。
//          寫入端：Senate 後台「早安 brief」頁（SCP_GuiBriefSettingsPage）。
//          跟著資料根走（全專案共用），⛔ 不放進 senate.local.json（那是每台機器各自一份）。
// 數值影響：每一格有預設值與合法區間（<see cref="Items"/>）。
//   · 讀取寬容：檔不存在／讀不了 ⇒ 全部預設；**某一格**不合法 ⇒ 那一格照預設跑並記進 <see cref="Problems"/>（⛔ 不夾值 —— 夾出來的是沒人打過的數字）。
//   · 寫入嚴格：任何一格不合法就整份不寫；只寫跟預設不同、或檔裡本來就有的格；檔裡其他鍵原樣保留；寫完讀回比對。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Io;
using SCP.Core.Json;

namespace SCP.Core.Letters
{
    /// <summary>一格參數的定義（鍵、標題、說明、預設、合法區間）。頁面、驗證、讀寫都走這張表。</summary>
    public sealed class SCP_WakeBriefSettingItem
    {
        public SCP_WakeBriefSettingItem(string iKey, string iGroup, string iLabel, string iNote, int iDefault, int iMin, int iMax)
        {
            Key = iKey; Group = iGroup; Label = iLabel; Note = iNote; Default = iDefault; Min = iMin; Max = iMax;
        }
        public string Key { get; }
        public string Group { get; }
        public string Label { get; }
        public string Note { get; }
        public int Default { get; }
        public int Min { get; }
        public int Max { get; }
        public bool IsValid(int iValue) => iValue >= Min && iValue <= Max;
    }

    public sealed class SCP_WakeBriefSettings
    {
        public const string FileName = "brief_settings.json";

        public const string KeyMainLineCap = "main_line_cap";
        public const string KeyTreeMergeStopLines = "tree_merge_stop_lines";
        public const string KeyTreeMergeMaxExtra = "tree_merge_max_extra";
        public const string KeyDigestGapOverdue = "digest_gap_overdue";
        public const string KeyPeopleOpinionCount = "people_opinion_count";
        public const string KeyPeopleOfflineTop = "people_offline_top";
        public const string KeyPeoplePortraitCount = "people_portrait_count";
        public const string KeyRecallMinWake = "recall_min_wake";
        public const string KeyRecallMinAgeWakes = "recall_min_age_wakes";
        public const string KeyBigFileLines = "big_file_lines";
        public const string KeyBigFileKb = "big_file_kb";

        public const string GroupBrief = "早安 brief";
        public const string GroupReadHint = "回傳檔大檔提示";

        /// <summary>全部參數。順序＝頁面上的順序。</summary>
        public static readonly IReadOnlyList<SCP_WakeBriefSettingItem> Items = new[]
        {
            new SCP_WakeBriefSettingItem(KeyMainLineCap, GroupBrief, "主檔行數上限",
                "超過時，非必讀的區塊整段移進續讀檔 wake_brief_part2.md（必讀層不移）", SCP_WakeBrief.BriefLineCap, 100, 20000),
            new SCP_WakeBriefSettingItem(KeyTreeMergeStopLines, GroupBrief, "見樹：往前合併的停止行數",
                "已合併的收尾信內文累積超過這個行數就不再往前補；最新那封超過它就一封都不補", SCP_WakeBrief.MergeStopLines, 0, 5000),
            new SCP_WakeBriefSettingItem(KeyTreeMergeMaxExtra, GroupBrief, "見樹：最多往前補幾封",
                "不含最新那封", SCP_WakeBrief.MergeMaxExtra, 0, 50),
            new SCP_WakeBriefSettingItem(KeyDigestGapOverdue, GroupBrief, "見林 overdue 門檻（wake 數）",
                "距上次見林幾個 wake 起標 OVERDUE", SCP_WakeBrief.DigestGapOverdue, 1, 100),
            new SCP_WakeBriefSettingItem(KeyPeopleOpinionCount, GroupBrief, "見人：每人列幾條看法",
                "取最新的幾條", SCP_WakeBrief.PeopleOpinionCount, 0, 20),
            new SCP_WakeBriefSettingItem(KeyPeopleOfflineTop, GroupBrief, "見人：離線的列前幾名",
                "依好感排序", SCP_WakeBrief.PeopleOfflineTop, 0, 50),
            new SCP_WakeBriefSettingItem(KeyPeoplePortraitCount, GroupBrief, "見人：畫像列幾張",
                "最近印象最深的幾位", SCP_WakeBrief.PeoplePortraitCount, 0, 50),
            new SCP_WakeBriefSettingItem(KeyRecallMinWake, GroupBrief, "回憶：第幾個 wake 之後才抽",
                "wake 數不超過它就不抽遠方的信", SCP_WakeBrief.RecallMinWake, 1, 100000),
            new SCP_WakeBriefSettingItem(KeyRecallMinAgeWakes, GroupBrief, "回憶：至少隔幾個 wake",
                "抽的信距今至少這麼多個 wake", SCP_WakeBrief.RecallMinAgeWakes, 0, 100000),
            new SCP_WakeBriefSettingItem(KeyBigFileLines, GroupReadHint, "大檔門檻：行數",
                "回傳檔行數或大小任一超過門檻，就提示「用你的工具分段讀到第 N 行」", 500, 1, 10000000),
            new SCP_WakeBriefSettingItem(KeyBigFileKb, GroupReadHint, "大檔門檻：KB",
                "同上（KB ＝ 1024 bytes）。預設 20 KB ≈ 8.7k token（以 brief 實測 2.3 bytes/token 換算），壓在 Codex 預設輸出預算 10k 之下", 20, 1, 1048576),
        };

        public static SCP_WakeBriefSettingItem? Find(string iKey)
        {
            foreach (SCP_WakeBriefSettingItem it in Items) if (it.Key == iKey) return it;
            return null;
        }

        readonly Dictionary<string, int> m_Values = new Dictionary<string, int>(StringComparer.Ordinal);

        /// <summary>讀取時照預設跑的格與原因（空 ＝ 每一格都是檔裡的值或沒設過）。讀取端要把它說出來。</summary>
        public List<string> Problems { get; } = new List<string>();

        public SCP_WakeBriefSettings()
        {
            foreach (SCP_WakeBriefSettingItem it in Items) m_Values[it.Key] = it.Default;
        }

        public int Get(string iKey) => m_Values.TryGetValue(iKey, out int v) ? v : (Find(iKey)?.Default ?? 0);
        public void Set(string iKey, int iValue) { if (Find(iKey) != null) m_Values[iKey] = iValue; }

        public int MainLineCap => Get(KeyMainLineCap);
        public int TreeMergeStopLines => Get(KeyTreeMergeStopLines);
        public int TreeMergeMaxExtra => Get(KeyTreeMergeMaxExtra);
        public int DigestGapOverdue => Get(KeyDigestGapOverdue);
        public int PeopleOpinionCount => Get(KeyPeopleOpinionCount);
        public int PeopleOfflineTop => Get(KeyPeopleOfflineTop);
        public int PeoplePortraitCount => Get(KeyPeoplePortraitCount);
        public int RecallMinWake => Get(KeyRecallMinWake);
        public int RecallMinAgeWakes => Get(KeyRecallMinAgeWakes);
        public int BigFileLines => Get(KeyBigFileLines);
        public long BigFileBytes => (long)Get(KeyBigFileKb) * 1024;

        public SCP_WakeBriefSettings Clone()
        {
            var o = new SCP_WakeBriefSettings();
            foreach (var kv in m_Values) o.m_Values[kv.Key] = kv.Value;
            return o;
        }

        public bool SameAs(SCP_WakeBriefSettings o)
        {
            foreach (SCP_WakeBriefSettingItem it in Items) if (Get(it.Key) != o.Get(it.Key)) return false;
            return true;
        }

        public static string PathOf(string iDataRoot) => Path.Combine(iDataRoot, "ChatTavern", FileName);

        public static List<string> Validate(SCP_WakeBriefSettings iS)
        {
            var aBad = new List<string>();
            foreach (SCP_WakeBriefSettingItem it in Items)
            {
                int v = iS.Get(it.Key);
                if (!it.IsValid(v)) aBad.Add($"{it.Label} {v} 不合法（{it.Min}–{it.Max}）");
            }
            return aBad;
        }

        /// <summary>
        /// 讀設定。<paramref name="oExists"/>＝檔在不在；<paramref name="oError"/> 有值 ＝ 整份讀不了（此時全部預設）。
        /// 單格不合法不算 oError，記在回傳物件的 <see cref="Problems"/>。
        /// </summary>
        public static SCP_WakeBriefSettings Read(string iDataRoot, out bool oExists, out string? oError)
        {
            oExists = false; oError = null;
            var aOut = new SCP_WakeBriefSettings();
            if (string.IsNullOrEmpty(iDataRoot)) return aOut;
            string aPath = PathOf(iDataRoot);
            if (!SCP_AtomicFileRead.TryReadAllText(aPath, out string aText, out SCP_FileReadState aState))
            {
                if (aState == SCP_FileReadState.Busy)
                {
                    oExists = true;
                    oError = "這一瞬間讀不了（被鎖或換檔中）⇒ 全部用預設值：" + aPath;
                    aOut.Problems.Add(oError);
                }
                return aOut;
            }
            oExists = true;
            try
            {
                SCP_JsonData aJd = SCP_JsonData.Parse(aText);
                if (!aJd.IsObject)
                {
                    oError = "不是 JSON 物件 ⇒ 全部用預設值：" + aPath;
                    aOut.Problems.Add(oError);
                    return aOut;
                }
                foreach (SCP_WakeBriefSettingItem it in Items)
                {
                    if (!aJd.Contains(it.Key)) continue;
                    // 不是數字（字串／布林／物件…）⇒ 一律當不合法，⛔ 不讓 AsInt 丟例外被外層吞成「讀不了」—— 那會把原因講成別的。
                    SCP_JsonData aNode = aJd[it.Key];
                    bool aNumeric = !(aNode.IsString || aNode.IsArray || aNode.IsObject || aNode.IsNull);
                    int v = aNumeric ? aNode.AsInt() : int.MinValue;
                    if (aNumeric && it.IsValid(v)) aOut.m_Values[it.Key] = v;
                    else aOut.Problems.Add($"{it.Label}不合法（檔裡寫 {(aNumeric ? v.ToString() : aNode.ToJson())}；合法 {it.Min}–{it.Max}）⇒ 照預設 {it.Default} 跑");
                }
            }
            catch (Exception e)
            {
                oError = "讀不了（" + e.GetType().Name + ": " + e.Message + "）⇒ 全部用預設值：" + aPath;
                var aDefault = new SCP_WakeBriefSettings();
                aDefault.Problems.Add(oError);
                return aDefault;
            }
            return aOut;
        }

        /// <summary>只要數值（讀取端用）。問題仍在 <see cref="Problems"/> 裡，要說出來的呼叫端自己讀。</summary>
        public static SCP_WakeBriefSettings ReadOrDefault(string? iDataRoot) =>
            string.IsNullOrEmpty(iDataRoot) ? new SCP_WakeBriefSettings() : Read(iDataRoot!, out _, out _);

        /// <summary>
        /// 寫設定：驗 → 讀出現有檔（保留其他鍵）→ 暫存檔 → 換檔 → **讀回比對**。
        /// <para>⛔ 不拿「沒丟例外」當落盤的證據 —— 讀回來每一格都對得上才回 true。</para>
        /// </summary>
        public static bool Write(string iDataRoot, SCP_WakeBriefSettings iS, out string? oError)
        {
            oError = null;
            List<string> aBad = Validate(iS);
            if (aBad.Count > 0) { oError = "✗ 不寫：" + string.Join("；", aBad); return false; }
            string aPath = PathOf(iDataRoot);

            SCP_JsonData aJd = SCP_JsonData.NewObject();
            if (SCP_AtomicFileRead.TryReadAllText(aPath, out string aOld, out SCP_FileReadState aState))
            {
                try
                {
                    SCP_JsonData aParsed = SCP_JsonData.Parse(aOld);
                    if (aParsed.IsObject) aJd = aParsed;
                    else { oError = "✗ 不寫：現有檔不是 JSON 物件，覆寫會丟掉裡面的東西（先處理那份檔）：" + aPath; return false; }
                }
                catch (Exception e)
                {
                    oError = "✗ 不寫：現有檔壞了（" + e.Message + "），覆寫會丟掉裡面的東西（先處理那份檔）：" + aPath;
                    return false;
                }
            }
            else if (aState == SCP_FileReadState.Busy)
            {
                oError = "✗ 不寫：現有檔這一瞬間讀不了（被鎖或換檔中），稍後再存：" + aPath;
                return false;
            }
            foreach (SCP_WakeBriefSettingItem it in Items)
            {
                int v = iS.Get(it.Key);
                // 預設值而且檔裡本來沒有這格 ⇒ 不寫（讓「沒設過」維持沒設過；之後預設值變了才跟得上）
                if (v != it.Default || aJd.Contains(it.Key)) aJd.Set(it.Key, SCP_JsonData.NewNumber(v));
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aPath) ?? ".");
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, aJd.ToJson(), new UTF8Encoding(false));
                SCP_TextFile.ReplaceOrMove(aTmp, aPath);
            }
            catch (Exception e) { oError = "✗ 寫不進去：" + e.GetType().Name + ": " + e.Message + "（" + aPath + "）"; return false; }

            SCP_WakeBriefSettings aBack = Read(iDataRoot, out bool aExists, out string? aReadErr);
            if (!aExists) { oError = "✗ 寫完回讀：檔不見了（" + aPath + "）"; return false; }
            if (aReadErr != null || aBack.Problems.Count > 0)
            {
                oError = "✗ 寫完回讀失敗：" + (aReadErr ?? string.Join("；", aBack.Problems));
                return false;
            }
            if (!aBack.SameAs(iS)) { oError = "✗ 寫完回讀對不上（" + aPath + "）"; return false; }
            return true;
        }
    }
}
