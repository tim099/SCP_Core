// 區塊職責：預覽 preset（TASK-0571，Tim 2026-10-14「需要可以做一些預覽preset(我用來觀測完工的部分 確認是否ok 因為很多部分在船殼內部無法直接看到)
//          可以用下拉選單切換preset(預設是all 就是正常顯示)」）—— 作品記幾組「這次只看／藏哪些」，觀測時用名字叫出來。
// 物理意義：一組 preset ＝ 幾個觀測參數的**原字串**（跟 CLI 同名：links、hide_links、models、hide_models、ropes、hide_ropes、rope_groups、
//          hide_rope_groups、sails、hide_sails、region、exclude_color）＋備註；存在作品資料夾的 view_presets.json（作者寫的、入 git），⛔ 不是 voxel、不在 events/。
//          展開只有 <see cref="Expand"/> 這一支：CLI 的 preset= 與觀測頁的下拉都叫它 ⇒ 兩邊送出去的參數同一份，驗證照各參數原本的規則（⛔ 這裡不另寫一份）。
//          all ＝ 正常顯示（保留字，不能當 preset 名）。
// 失敗處置：view_presets.json 讀不了 ⇒ 擋下（⛔ 不當成沒有 preset —— 寫回去會清光）；名字不存在 ⇒ ArgumentException（說有哪幾組）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Sculpture
{
    public sealed class SCP_SculptViewPreset
    {
        public string name = "", note = "";
        public string links = "", hide_links = "", models = "", hide_models = "", ropes = "", hide_ropes = "",
                      rope_groups = "", hide_rope_groups = "", sails = "", hide_sails = "", region = "", exclude_color = "";
        public string created_at = "", persona = "";
    }

    public sealed class SCP_SculptViewPresetFile
    {
        public int schema = 1;
        public List<SCP_SculptViewPreset> presets = new List<SCP_SculptViewPreset>();
    }

    public static class SCP_SculptViewPresets
    {
        /// <summary>正常顯示（不套任何 preset）；保留字。</summary>
        public const string All = "all";

        /// <summary>preset 收的參數（跟 CLI 同名）。前兩組（關聯、範圍、排除色）每個觀測 op 都吃；繩、帆、模型只有 view／export 畫。</summary>
        public static readonly string[] Keys =
            { "links", "hide_links", "region", "exclude_color", "models", "hide_models", "ropes", "hide_ropes", "rope_groups", "hide_rope_groups", "sails", "hide_sails" };
        static readonly string[] RigKeys = { "models", "hide_models", "ropes", "hide_ropes", "rope_groups", "hide_rope_groups", "sails", "hide_sails" };
        /// <summary>吃 preset 的觀測 op。</summary>
        public static readonly string[] Ops = { "view", "slice", "section", "stats", "export" };

        public static bool RigKey(string iKey) => Array.IndexOf(RigKeys, iKey) >= 0;
        /// <summary>這個 op 吃不吃這個鍵（繩、帆、模型只有 view／export 畫 —— slice／section／stats 只看格子）。</summary>
        public static bool AppliesTo(string iOp, string iKey) => !RigKey(iKey) || iOp == "view" || iOp == "export";

        public static string Get(SCP_SculptViewPreset p, string iKey) => iKey switch
        {
            "links" => p.links, "hide_links" => p.hide_links, "models" => p.models, "hide_models" => p.hide_models,
            "ropes" => p.ropes, "hide_ropes" => p.hide_ropes, "rope_groups" => p.rope_groups, "hide_rope_groups" => p.hide_rope_groups,
            "sails" => p.sails, "hide_sails" => p.hide_sails, "region" => p.region, "exclude_color" => p.exclude_color,
            _ => throw new ArgumentException("preset 不收參數 `" + iKey + "`"),
        };

        public static void Set(SCP_SculptViewPreset p, string iKey, string iValue)
        {
            string v = iValue.Trim();
            switch (iKey)
            {
                case "links": p.links = v; break;
                case "hide_links": p.hide_links = v; break;
                case "models": p.models = v; break;
                case "hide_models": p.hide_models = v; break;
                case "ropes": p.ropes = v; break;
                case "hide_ropes": p.hide_ropes = v; break;
                case "rope_groups": p.rope_groups = v; break;
                case "hide_rope_groups": p.hide_rope_groups = v; break;
                case "sails": p.sails = v; break;
                case "hide_sails": p.hide_sails = v; break;
                case "region": p.region = v; break;
                case "exclude_color": p.exclude_color = v; break;
                default: throw new ArgumentException("preset 不收參數 `" + iKey + "`");
            }
        }

        /// <summary>這組 preset 有寫的鍵（照 <see cref="Keys"/> 的順序）。</summary>
        public static List<KeyValuePair<string, string>> Pairs(SCP_SculptViewPreset p)
        {
            var aOut = new List<KeyValuePair<string, string>>();
            foreach (string k in Keys)
            {
                string v = Get(p, k);
                if (v.Length > 0) aOut.Add(new KeyValuePair<string, string>(k, v));
            }
            return aOut;
        }

        /// <summary>給人看的一行（`links=…｜hide_links=…`）。</summary>
        public static string Describe(SCP_SculptViewPreset p)
        {
            var aParts = new List<string>();
            foreach (var kv in Pairs(p)) aParts.Add(kv.Key + "=" + kv.Value);
            return aParts.Count == 0 ? "（沒有參數 ＝ 跟 all 一樣）" : string.Join("｜", aParts);
        }

        /// <summary>preset 名：非空、前後沒有空白、不含 , | : / \ 與換行、不是 all。null ＝ 合法。</summary>
        public static string? ValidName(string iName)
        {
            string a = iName.Trim();
            if (a.Length == 0) return "preset 要有名字（name=）";
            if (a != iName) return "preset 名字前後不可有空白";
            if (a.IndexOfAny(new[] { ',', '|', ':', '/', '\\', '\n', '\r' }) >= 0) return "preset 名字不可含 `,` `|` `:` `/` `\\` 或換行";
            if (string.Equals(a, All, StringComparison.OrdinalIgnoreCase)) return "`all` 是保留字（正常顯示），不能當 preset 名";
            return null;
        }

        public static SCP_SculptViewPresetFile Read(SCP_SculptPaths iPaths)
        {
            string aFile = iPaths.ViewPresetsFile;
            if (!File.Exists(aFile)) return new SCP_SculptViewPresetFile();
            SCP_SculptViewPresetFile aOut;
            try
            {
                aOut = new SCP_SculptViewPresetFile { schema = 0 };
                var aOpt = new SCP_JsonMapOptions();
                SCP_JsonMapper.Populate(aOut, SCP_JsonParser.Parse(SCP.Core.Io.SCP_SharedFile.ReadAllText(aFile), false), aOpt);
                if (aOpt.Diagnostics.Count > 0) throw new InvalidOperationException(string.Join("; ", aOpt.Diagnostics));
            }
            catch (Exception e) { throw new InvalidOperationException("view_presets.json 讀不了（" + aFile + "：" + e.Message + "）—— ⛔ 不當成沒有 preset，修好再用"); }
            if (aOut.schema != 1) throw new InvalidOperationException("view_presets.json 讀不了（" + aFile + "：schema=" + aOut.schema + "）");
            var aNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in aOut.presets)
            {
                string? aBad = ValidName(p.name) ?? (aNames.Add(p.name) ? null : "名字重複");
                if (aBad != null) throw new InvalidOperationException("view_presets.json 讀不了（" + aFile + "：preset `" + p.name + "` " + aBad + "）");
            }
            return aOut;
        }

        public static void Save(SCP_SculptPaths iPaths, SCP_SculptViewPresetFile iFile)
        {
            iFile.schema = 1;
            string aFile = iPaths.ViewPresetsFile;
            string aTmp = aFile + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";
            try
            {
                File.WriteAllText(aTmp, SCP_JsonWriter.Write(SCP_JsonMapper.ToJson(iFile), true) + "\n", new UTF8Encoding(false));
                SCP.Core.Io.SCP_SharedFile.Replace(aTmp, aFile);
            }
            finally { try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch (Exception) { } }
        }

        /// <summary>
        /// 展開一組 preset：回傳這個 op 要補上的參數（鍵 → 原字串）；all ⇒ 空。繩、帆、模型的鍵在 slice／section／stats 不回（那幾個 op 只看格子），
        /// 略掉的鍵放進 <paramref name="oSkipped"/>。名字不存在 ⇒ ArgumentException（列出有哪幾組）。
        /// </summary>
        public static Dictionary<string, string> Expand(SCP_SculptViewPresetFile iFile, string iName, string iOp, List<string> oSkipped)
        {
            var aOut = new Dictionary<string, string>(StringComparer.Ordinal);
            string aName = iName.Trim();
            if (aName.Length == 0 || string.Equals(aName, All, StringComparison.OrdinalIgnoreCase)) return aOut;
            SCP_SculptViewPreset? aHit = iFile.presets.Find(p => p.name == aName);
            if (aHit == null)
                throw new ArgumentException("沒有 preset `" + aName + "`（有：all" + (iFile.presets.Count > 0 ? "、" + string.Join("、", iFile.presets.ConvertAll(p => p.name)) : "") + "）");
            foreach (var kv in Pairs(aHit))
            {
                if (AppliesTo(iOp, kv.Key)) aOut[kv.Key] = kv.Value;
                else oSkipped.Add(kv.Key);
            }
            return aOut;
        }
    }
}
