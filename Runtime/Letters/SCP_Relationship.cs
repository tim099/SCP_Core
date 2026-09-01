// 區塊職責：關係（好感度）的**讀取端** —— 讀 `letters/<persona>/relationship/<target>/`。
// 物理意義：關係是**事件帳本**不是一個數字：分數由事件重算，`_current.md` 是那本帳的當前投影。
//           ⇒ 本檔只讀那份投影與 `opinions/`，一個位元組都不寫；要改關係走寫入端（Cmd_Relationship）。
// 數值影響：純讀。分數解析失敗回 0，而 0 與「沒有這個人」**要分得開**（見 LoadError／找不到的差別）。
//
// ⚠ 「讀失敗」與「真的沒有紀錄」不可以合成一句：
//   前者是這一區沒生成出來，後者是這個人還沒跟誰互動過。
//   把前者印成後者，等於拿一個壞掉的區塊冒充一個空的區塊 —— python 端為此留了一個模組級旗標，
//   本檔改成回傳物件自己帶 <see cref="SCP_RelationshipSet.LoadError"/>（旗標會跨呼叫殘留，物件不會）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    /// <summary>對一個人的關係現況（`_current.md` 的投影 ＋ opinions 全文）。</summary>
    public sealed class SCP_RelationshipEntry
    {
        public string Target = "";

        /// <summary>表層分數。⚠ 解析不出來時是 0 —— 而 0 分與「沒解析到」在畫面上同形，所以另看 <see cref="ScoreParsed"/>。</summary>
        public int SurfaceScore;

        public bool ScoreParsed;

        public string Tier = "";

        /// <summary>opinions/ 底下每一筆的內文（舊 → 新，依檔名排序）。</summary>
        public List<string> Opinions = new List<string>();
    }

    /// <summary>一個 persona 的全部關係。<see cref="LoadError"/> 非 null ＝ **量不到**，不是「沒有」。</summary>
    public sealed class SCP_RelationshipSet
    {
        public List<SCP_RelationshipEntry> Entries = new List<SCP_RelationshipEntry>();
        public string? LoadError;

        public SCP_RelationshipEntry? Find(string iTarget)
        {
            foreach (SCP_RelationshipEntry aEntry in Entries)
                if (string.Equals(aEntry.Target, iTarget, StringComparison.OrdinalIgnoreCase)) return aEntry;
            return null;
        }
    }

    public static class SCP_Relationship
    {
        public const string RelationshipDirName = "relationship";
        public const string CurrentFileName = "_current.md";
        public const string OpinionsDirName = "opinions";

        public static string Dir(string iLettersRoot, string iPersona)
            => SCP_LettersPaths.PersonaDir(new SCP_LettersRoot(iLettersRoot), iPersona) + "/" + RelationshipDirName;

        public static SCP_RelationshipSet Load(string iLettersRoot, string iPersona)
        {
            var aSet = new SCP_RelationshipSet();
            string aDir = Dir(iLettersRoot, iPersona);
            if (!Directory.Exists(aDir)) return aSet;      // 真的沒有紀錄 —— 不是錯誤

            string[] aTargetDirs;
            try { aTargetDirs = Directory.GetDirectories(aDir); }
            catch (Exception e)
            {
                aSet.LoadError = e.GetType().Name + ": " + e.Message;
                return aSet;
            }
            Array.Sort(aTargetDirs, StringComparer.OrdinalIgnoreCase);

            foreach (string aTargetDir in aTargetDirs)
            {
                string aCurrent = Path.Combine(aTargetDir, CurrentFileName);
                if (!File.Exists(aCurrent)) continue;      // 有目錄沒投影 ⇒ 這一筆還沒結算過

                var aEntry = new SCP_RelationshipEntry
                {
                    Target = SCP_LetterText.ReadFrontmatterField(aCurrent, "target"),
                    Tier = SCP_LetterText.ReadFrontmatterField(aCurrent, "tier"),
                };
                if (aEntry.Target.Length == 0) aEntry.Target = Path.GetFileName(aTargetDir);

                string aScore = SCP_LetterText.ReadFrontmatterField(aCurrent, "surface_score");
                aEntry.ScoreParsed = int.TryParse(aScore, NumberStyles.AllowLeadingSign,
                                                  CultureInfo.InvariantCulture, out int aValue);
                aEntry.SurfaceScore = aEntry.ScoreParsed ? aValue : 0;

                string aOpinionsDir = Path.Combine(aTargetDir, OpinionsDirName);
                if (Directory.Exists(aOpinionsDir))
                {
                    string[] aFiles;
                    try { aFiles = Directory.GetFiles(aOpinionsDir, "*.md"); }
                    catch (Exception) { aFiles = Array.Empty<string>(); }
                    Array.Sort(aFiles, StringComparer.OrdinalIgnoreCase);   // 檔名是時戳 ⇒ 舊 → 新
                    foreach (string aFile in aFiles)
                    {
                        string aBody = ReadOpinionBody(aFile);
                        if (aBody.Length > 0) aEntry.Opinions.Add(aBody);
                    }
                }
                aSet.Entries.Add(aEntry);
            }
            return aSet;
        }

        static string ReadOpinionBody(string iPath)
        {
            try { return SCP_LetterText.StripFrontmatter(File.ReadAllText(iPath)).Trim(); }
            catch (Exception) { return ""; }
        }
    }
}
