// 區塊職責：**persona 顯示資料**（頭像／顏色）的唯一讀寫層（TASK-0317）。
// 物理意義：資料住在 persona 自己的信件夾 `<letters>/<persona>/profile/`，照 profile 既有的「一欄一檔」：
//             · `avatar.png` —— 頭像。⚠ **內容可以是 PNG 或 JPEG**（檔名固定，讀取端只找這一個名字）：
//               🩸 2026-09-28 實測：從舊 sprite 搬來的 22 張裡 16 張其實是 JPEG（原始素材副檔名就寫 .png）。
//             · `color.md`   —— 一行 `#RRGGBB`（＋LF，無 BOM，同 `actual_agent.md`）
//             · `avatar_url.md` —— 一行**公開**頭像網址（https；TASK-0320，給 Discord 用 —— Discord 只收公開網址，讀不到本機檔）。
//               沒填 ⇒ Discord 那側用預設範本（`SCP_DiscordAvatar`），⛔ 本檔不知道範本。
//           **顯示名一律是 persona id**（Tim 2026-09-28：不另存顯示名）。
//           Tim 2026-09-28：「不使用之前的 UCL_Asset」「之後不做 agent fallback」
//           ⇒ 本檔**只看 persona 自己的資料夾**：缺圖就是缺圖（顯示端畫預設圖），⛔ 不借 agent 或別人的。
//           系統身分也是 persona：酒保＝`tavern-keeper`、系統訊息＝`system`。
// 數值影響：讀取零寫入。寫入（`TrySetColor`／`TrySetAvatar`）走暫存檔再搬，失敗不留半個檔。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace SCP.Core.Letters
{
    /// <summary>一個 persona 的顯示資料。缺的欄位是空字串（⛔ 不是 null，也⛔ 不是借來的值）。</summary>
    public sealed class SCP_PersonaDisplayInfo
    {
        public string Persona = "";

        /// <summary>顯示名 ＝ persona id（Tim 2026-09-28）。</summary>
        public string DisplayName => Persona;

        /// <summary>頭像檔的絕對路徑（正斜線）；空 ＝ 這個 persona 沒有頭像 ⇒ 顯示端畫預設圖。</summary>
        public string AvatarPath = "";

        /// <summary>`#RRGGBB`；空 ＝ 沒設 ⇒ 顯示端用預設色。</summary>
        public string ColorHex = "";

        /// <summary>color.md 在但內容不是 `#RRGGBB` ⇒ 這裡放原文（顯示端要出聲，⛔ 不當成沒設）。</summary>
        public string ColorInvalidRaw = "";

        /// <summary>`avatar_url.md` 填的公開網址；空 ＝ 沒填（顯示端自己決定預設）。</summary>
        public string AvatarUrl = "";

        /// <summary>信件夾存在嗎（false ＝ 不是 pool 裡的 persona，只是一個寄件人 id）。</summary>
        public bool HasLettersDir;

        public bool HasAvatar => AvatarPath.Length > 0;
    }

    public static class SCP_PersonaDisplay
    {
        public const string AvatarFileName = "avatar.png";
        public const string ColorFileName = "color.md";
        public const string AvatarUrlFileName = "avatar_url.md";

        /// <summary>酒保（系統廣播：保管費／時間提醒…）。</summary>
        public const string TavernKeeperPersona = "tavern-keeper";

        /// <summary>系統訊息（kind=system 等沒有 persona 的那些）。</summary>
        public const string SystemPersona = "system";

        static readonly Regex s_Color = new Regex("^#[0-9A-Fa-f]{6}$", RegexOptions.CultureInvariant);

        public static bool IsValidColor(string iHex) => iHex != null && s_Color.IsMatch(iHex.Trim());

        public static string ProfileDir(string iLettersRoot, string iPersona)
            => Path.Combine(iLettersRoot, iPersona, "profile").Replace('\\', '/');

        /// <summary>讀一個 persona 的顯示資料。persona 名不合法（含路徑字元）⇒ 空資料，⛔ 不往外讀。</summary>
        public static SCP_PersonaDisplayInfo Get(string iLettersRoot, string iPersona)
        {
            var aInfo = new SCP_PersonaDisplayInfo { Persona = iPersona ?? "" };
            if (!IsSafeName(aInfo.Persona) || string.IsNullOrEmpty(iLettersRoot)) return aInfo;

            aInfo.HasLettersDir = Directory.Exists(Path.Combine(iLettersRoot, aInfo.Persona));
            string aDir = ProfileDir(iLettersRoot, aInfo.Persona);

            string aAvatar = aDir + "/" + AvatarFileName;
            if (File.Exists(aAvatar)) aInfo.AvatarPath = aAvatar;

            string aColorPath = aDir + "/" + ColorFileName;
            if (File.Exists(aColorPath))
            {
                string aRaw;
                try { aRaw = File.ReadAllText(aColorPath, Encoding.UTF8).Trim().TrimStart('﻿'); }
                catch (Exception e) { aRaw = "（讀不了：" + e.Message + "）"; }
                if (IsValidColor(aRaw)) aInfo.ColorHex = aRaw.ToUpperInvariant();
                else aInfo.ColorInvalidRaw = aRaw;
            }
            string aUrlPath = aDir + "/" + AvatarUrlFileName;
            if (File.Exists(aUrlPath))
            {
                try { aInfo.AvatarUrl = File.ReadAllText(aUrlPath, Encoding.UTF8).Trim().TrimStart('﻿'); }
                catch (Exception) { /* 讀不了 ⇒ 當作沒填 */ }
            }
            return aInfo;
        }

        /// <summary>設頭像公開網址。空字串 ⇒ 刪掉 avatar_url.md（回到預設）。只收 https ⇒ 其他一律拒絕、零寫入。</summary>
        public static bool TrySetAvatarUrl(string iLettersRoot, string iPersona, string iUrl, out string? oError)
        {
            oError = null;
            if (!IsSafeName(iPersona)) { oError = $"persona 名不合法：'{iPersona}'"; return false; }
            if (!Directory.Exists(Path.Combine(iLettersRoot, iPersona)))
            { oError = $"信件夾不存在：{Path.Combine(iLettersRoot, iPersona)}（⛔ 不代建 persona）"; return false; }
            string aDir = ProfileDir(iLettersRoot, iPersona);
            string aPath = aDir + "/" + AvatarUrlFileName;
            string aUrl = (iUrl ?? "").Trim();
            try
            {
                if (aUrl.Length == 0) { if (File.Exists(aPath)) File.Delete(aPath); return true; }
                if (!aUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || aUrl.Any(char.IsWhiteSpace))
                { oError = $"頭像網址要是 https 開頭、中間沒有空白（收到 '{aUrl}'）"; return false; }
                Directory.CreateDirectory(aDir);
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, aUrl + "\n", new UTF8Encoding(false));
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
                return true;
            }
            catch (Exception e) { oError = $"寫不進去（{aPath}）：{e.Message}"; return false; }
        }

        /// <summary>信件夾裡有 `profile/` 的那些名字（排序）。⚠ 包含 system／tavern-keeper 這類系統身分。</summary>
        public static List<string> ListPersonas(string iLettersRoot)
        {
            var aOut = new List<string>();
            if (string.IsNullOrEmpty(iLettersRoot) || !Directory.Exists(iLettersRoot)) return aOut;
            foreach (string aDir in Directory.GetDirectories(iLettersRoot))
            {
                string aName = Path.GetFileName(aDir);
                if (aName.StartsWith("_", StringComparison.Ordinal) || aName.StartsWith(".", StringComparison.Ordinal)) continue;
                if (Directory.Exists(Path.Combine(aDir, "profile"))) aOut.Add(aName);
            }
            aOut.Sort(StringComparer.OrdinalIgnoreCase);
            return aOut;
        }

        /// <summary>設顏色。空字串 ⇒ **刪掉** color.md（回到「沒設」）。不是 `#RRGGBB` ⇒ 拒絕、零寫入。</summary>
        public static bool TrySetColor(string iLettersRoot, string iPersona, string iHex, out string? oError)
        {
            oError = null;
            if (!IsSafeName(iPersona)) { oError = $"persona 名不合法：'{iPersona}'"; return false; }
            if (!Directory.Exists(Path.Combine(iLettersRoot, iPersona)))
            { oError = $"信件夾不存在：{Path.Combine(iLettersRoot, iPersona)}（⛔ 不代建 persona）"; return false; }

            string aDir = ProfileDir(iLettersRoot, iPersona);
            string aPath = aDir + "/" + ColorFileName;
            string aHex = (iHex ?? "").Trim();
            try
            {
                if (aHex.Length == 0)
                {
                    if (File.Exists(aPath)) File.Delete(aPath);
                    return true;
                }
                if (!IsValidColor(aHex)) { oError = $"顏色要 `#RRGGBB`（收到 '{aHex}'）"; return false; }
                Directory.CreateDirectory(aDir);
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, aHex.ToUpperInvariant() + "\n", new UTF8Encoding(false));
                if (File.Exists(aPath)) File.Delete(aPath);
                File.Move(aTmp, aPath);
                return true;
            }
            catch (Exception e) { oError = $"寫不進去（{aPath}）：{e.Message}"; return false; }
        }

        /// <summary>
        /// 換頭像：把 <paramref name="iSourcePng"/> 複製成 `profile/avatar.png`。
        /// ⚠ 只收 PNG／JPEG（看檔頭，⛔ 不信副檔名）；已有頭像時 <paramref name="iOverwrite"/> 要是 true 才會蓋 ——
        ///   蓋掉之後舊圖就回不來了（除非它在 git 裡）。
        /// </summary>
        public static bool TrySetAvatar(string iLettersRoot, string iPersona, string iSourcePng, bool iOverwrite, out string? oError)
        {
            oError = null;
            if (!IsSafeName(iPersona)) { oError = $"persona 名不合法：'{iPersona}'"; return false; }
            if (!Directory.Exists(Path.Combine(iLettersRoot, iPersona)))
            { oError = $"信件夾不存在：{Path.Combine(iLettersRoot, iPersona)}（⛔ 不代建 persona）"; return false; }
            string aSrc = (iSourcePng ?? "").Trim().Trim('"');
            if (!File.Exists(aSrc)) { oError = $"來源檔不存在：'{aSrc}'"; return false; }
            if (!LooksLikeImage(aSrc)) { oError = $"來源不是 PNG／JPEG（看的是檔頭，不是副檔名）：{aSrc}"; return false; }

            string aDir = ProfileDir(iLettersRoot, iPersona);
            string aDst = aDir + "/" + AvatarFileName;
            if (File.Exists(aDst) && !iOverwrite) { oError = "已經有頭像 ⇒ 要覆寫請確認（覆寫後舊圖回不來，除非在 git 裡）"; return false; }
            try
            {
                Directory.CreateDirectory(aDir);
                string aTmp = aDst + ".tmp";
                File.Copy(aSrc, aTmp, true);
                if (File.Exists(aDst)) File.Delete(aDst);
                File.Move(aTmp, aDst);
                return true;
            }
            catch (Exception e) { oError = $"複製失敗（{aDst}）：{e.Message}"; return false; }
        }

        static bool LooksLikeImage(string iPath)
        {
            try
            {
                using var aFs = File.OpenRead(iPath);
                var aHead = new byte[8];
                if (aFs.Read(aHead, 0, 8) != 8) return false;
                bool aPng = aHead[0] == 0x89 && aHead[1] == 0x50 && aHead[2] == 0x4E && aHead[3] == 0x47
                            && aHead[4] == 0x0D && aHead[5] == 0x0A && aHead[6] == 0x1A && aHead[7] == 0x0A;
                bool aJpeg = aHead[0] == 0xFF && aHead[1] == 0xD8 && aHead[2] == 0xFF;
                return aPng || aJpeg;
            }
            catch (Exception) { return false; }
        }

        /// <summary>persona 名只能當一層資料夾名 —— 含 `/`、`\`、`..` 的一律不收（⛔ 不讓寄件人 id 把讀取帶出信件夾）。</summary>
        static bool IsSafeName(string iName)
            => !string.IsNullOrWhiteSpace(iName)
               && iName.IndexOfAny(new[] { '/', '\\', ':' }) < 0
               && iName != "." && iName != ".."
               && iName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;
    }
}
