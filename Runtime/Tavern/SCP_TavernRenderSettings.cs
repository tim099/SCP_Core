// 區塊職責：酒館**顯示截斷**設定（`<資料根>/ChatTavern/render_settings.json`）的唯一讀寫層。
// 物理意義：未讀訊息內文顯示到幾個字元 —— 一般訊息一格、@ 到自己的訊息一格。只截**顯示**，⛔ 不改原文。
//          讀取端：叮 catchup（SCP_TavernCatchup）、自由時間的配對簡報（SCP_FreeTimePartners）。
//          寫入端：Senate 後台「酒館設定」頁（SCP_GuiTavernSettingsPage）。
//          這份檔跟著資料根走（全專案共用），⛔ 不放進 senate.local.json（那是每台機器各自一份）。
// 數值影響：0 ＝ 不截斷；其餘合法值 80–4000。
//   · 讀取寬容：檔不存在／讀不了／值不合法 ⇒ 用預設值，原因放在 oError（只影響顯示長度，⛔ 不讓 catchup 失敗）。
//   · 寫入嚴格：不合法就不寫（⛔ 不悄悄夾值 —— 夾了之後畫面顯示的不是你打的數字，而沒有人會喊）。
//   · 寫入只改這兩格，檔裡其他鍵原樣保留。
// ⚠ 方言限制：C# 9 / netstandard2.1 / 零第三方（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Io;
using SCP.Core.Json;

namespace SCP.Core.Tavern
{
    public sealed class SCP_TavernRenderSettings
    {
        public const string FileName = "render_settings.json";
        public const string KeyBodyClip = "message_body_clip";
        public const string KeyBodyClipMentioned = "message_body_clip_mentioned";
        public const int DefaultBodyClip = 600;
        public const int DefaultBodyClipMentioned = 1500;
        public const int MinBodyClip = 80;
        public const int MaxBodyClip = 4000;

        /// <summary>一般未讀訊息的內文截斷（字元；0 ＝ 不截斷）。</summary>
        public int BodyClip = DefaultBodyClip;
        /// <summary>@ 到自己的訊息的內文截斷（字元；0 ＝ 不截斷）。</summary>
        public int BodyClipMentioned = DefaultBodyClipMentioned;

        public SCP_TavernRenderSettings Clone() => (SCP_TavernRenderSettings)MemberwiseClone();

        public bool SameAs(SCP_TavernRenderSettings o) => BodyClip == o.BodyClip && BodyClipMentioned == o.BodyClipMentioned;

        public static string PathOf(string iDataRoot) => Path.Combine(iDataRoot, "ChatTavern", FileName);

        static bool IsValid(int v) => v == 0 || (v >= MinBodyClip && v <= MaxBodyClip);

        /// <summary>讀取端用的夾值：不合法的值不讓 catchup 失敗，夾回合法區間。</summary>
        static int ClampForRead(int v) => v <= 0 ? 0 : Math.Max(MinBodyClip, Math.Min(MaxBodyClip, v));

        public static List<string> Validate(SCP_TavernRenderSettings iS)
        {
            var aBad = new List<string>();
            if (!IsValid(iS.BodyClip)) aBad.Add($"一般訊息截斷 {iS.BodyClip} 不合法（0 或 {MinBodyClip}–{MaxBodyClip}）");
            if (!IsValid(iS.BodyClipMentioned)) aBad.Add($"@ 到自己的訊息截斷 {iS.BodyClipMentioned} 不合法（0 或 {MinBodyClip}–{MaxBodyClip}）");
            return aBad;
        }

        /// <summary>
        /// 讀設定。<paramref name="oExists"/>＝檔在不在；<paramref name="oError"/> 有值 ＝ 讀不了或值不合法（此時回預設值或夾過的值）。
        /// <para>⚠ 「檔不存在」與「讀不了」分開回：前者是合法的「沒設過」，後者是「不知道設了什麼」。</para>
        /// </summary>
        public static SCP_TavernRenderSettings Read(string iDataRoot, out bool oExists, out string? oError)
        {
            oExists = false; oError = null;
            var aOut = new SCP_TavernRenderSettings();
            string aPath = PathOf(iDataRoot);
            // 寫入端以「暫存檔 → 換檔」換這顆檔 ⇒ 重試跨過那一瞬間。
            if (!SCP_AtomicFileRead.TryReadAllText(aPath, out string aText, out SCP_FileReadState aState))
            {
                if (aState == SCP_FileReadState.Busy) { oExists = true; oError = "這一瞬間讀不了（被鎖或換檔中）⇒ 用預設值：" + aPath; }
                return aOut;
            }
            oExists = true;
            try
            {
                SCP_JsonData aJd = SCP_JsonData.Parse(aText);
                if (!aJd.IsObject) { oError = "不是 JSON 物件 ⇒ 用預設值：" + aPath; return aOut; }
                int aClip = aJd.GetInt(KeyBodyClip, DefaultBodyClip);
                int aMention = aJd.GetInt(KeyBodyClipMentioned, DefaultBodyClipMentioned);
                aOut.BodyClip = ClampForRead(aClip);
                aOut.BodyClipMentioned = ClampForRead(aMention);
                if (aOut.BodyClip != aClip || aOut.BodyClipMentioned != aMention)
                    oError = $"有值不合法，已夾回 0 或 {MinBodyClip}–{MaxBodyClip}（檔裡寫 {aClip}／{aMention}）：{aPath}";
            }
            catch (Exception e)
            {
                oError = "讀不了（" + e.GetType().Name + ": " + e.Message + "）⇒ 用預設值：" + aPath;
                return new SCP_TavernRenderSettings();
            }
            return aOut;
        }

        /// <summary>只要兩格的數值、不在乎為什麼（讀取端用）。</summary>
        public static SCP_TavernRenderSettings ReadOrDefault(string iDataRoot) => Read(iDataRoot, out _, out _);

        /// <summary>
        /// 寫設定：驗 → 讀出現有檔（保留其他鍵）→ 暫存檔 → 換檔 → **讀回比對**。
        /// <para>⛔ 不拿「沒丟例外」當落盤的證據 —— 讀回來兩格都對得上才回 true。</para>
        /// </summary>
        public static bool Write(string iDataRoot, SCP_TavernRenderSettings iS, out string? oError)
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
            aJd.Set(KeyBodyClip, SCP_JsonData.NewNumber(iS.BodyClip));
            aJd.Set(KeyBodyClipMentioned, SCP_JsonData.NewNumber(iS.BodyClipMentioned));

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(aPath) ?? ".");
                string aTmp = aPath + ".tmp";
                File.WriteAllText(aTmp, aJd.ToJson(), new UTF8Encoding(false));
                SCP_TextFile.ReplaceOrMove(aTmp, aPath);
            }
            catch (Exception e) { oError = "✗ 寫不進去：" + e.GetType().Name + ": " + e.Message + "（" + aPath + "）"; return false; }

            SCP_TavernRenderSettings aBack = Read(iDataRoot, out bool aExists, out string? aReadErr);
            if (!aExists) { oError = "✗ 寫完回讀：檔不見了（" + aPath + "）"; return false; }
            if (aReadErr != null) { oError = "✗ 寫完回讀失敗：" + aReadErr; return false; }
            if (!aBack.SameAs(iS))
            {
                oError = $"✗ 寫完回讀對不上：寫 {iS.BodyClip}／{iS.BodyClipMentioned}、讀回 {aBack.BodyClip}／{aBack.BodyClipMentioned}";
                return false;
            }
            return true;
        }
    }
}
