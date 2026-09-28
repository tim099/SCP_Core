// 區塊職責：`cmd persona-display` —— persona 顯示資料（頭像／顏色）的 CLI 出口（TASK-0317）。
// 物理意義：讀寫全走 `SCP_PersonaDisplay`（與 Senate 後台「persona 顯示資料」頁同一份）；本 Cmd 只是薄殼。
//           顯示名一律是 persona id（Tim 2026-09-28），所以這裡沒有改名。
// 數值影響：`op=list`／`op=show` 零寫入；`op=color` 寫／刪 `profile/color.md`；
//           `op=avatar` 複製 PNG 成 `profile/avatar.png` —— **已有頭像時要 `confirm=1`** 才覆寫。
#nullable enable
using System.Collections.Generic;
using System.IO;
using SCP.Core.Letters;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_PersonaDisplay : SCP_Cmd
    {
        public override string Name => "persona-display";

        public override string Summary => "persona 顯示資料（頭像 avatar.png／顏色 color.md）：列出、查看、改顏色、換頭像 —— **本地跑，不需要 Editor**";

        public override string Details =>
            "資料住 `<letters>/<persona>/profile/`。顯示名一律是 persona id；沒有頭像／顏色 ⇒ 顯示端畫預設（⛔ 不借 agent 或別人的）。\n"
            + "· `op=list`（預設）：全部 persona 的頭像有無與顏色。\n"
            + "· `op=show --arg persona=<p>`：單一 persona。\n"
            + "· `op=color --arg persona=<p> --arg color=#RRGGBB`：設顏色；`color` 留空 ＝ **刪掉**（回到沒設）。\n"
            + "· `op=avatar --arg persona=<p> --arg src=<PNG 路徑>`：換頭像。已有頭像時要 `--arg confirm=1`（蓋掉之後舊圖回不來，除非在 git 裡）。\n"
            + "· `op=avatar-url --arg persona=<p> --arg url=<https>`：Discord 用的公開頭像網址（寫 `profile/avatar_url.md`）；`url` 留空 ＝ 刪掉、回到範本。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("persona-display --arg op=show --arg persona=gura");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("op", "做什麼", iDefault: "list", iChoices: new[] { "list", "show", "color", "avatar", "avatar-url" }),
            new SCP_CmdArgSpec("persona", "誰（show／color／avatar 必填）", iDefault: ""),
            new SCP_CmdArgSpec("color", "op=color：`#RRGGBB`；留空＝刪掉", iDefault: ""),
            new SCP_CmdArgSpec("src", "op=avatar：來源圖檔（PNG／JPEG，看檔頭）的完整路徑", iDefault: ""),
            new SCP_CmdArgSpec("confirm", "op=avatar：1＝允許覆寫既有頭像", iDefault: "0"),
            new SCP_CmdArgSpec("url", "op=avatar-url：公開頭像網址（https；給 Discord 用）；留空＝刪掉、回到預設範本", iDefault: ""),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aRoot = iArgs.Get("letters_root").Trim();
            if (!Directory.Exists(aRoot)) return SCP_CmdResult.Fail(1, $"✗ 信件夾根不存在：{aRoot}");
            string aOp = iArgs.Get("op").Trim();
            string aPersona = iArgs.Get("persona").Trim();

            if (aOp == "list")
            {
                var aR = SCP_CmdResult.Success("# persona 顯示資料（顯示名＝persona id）");
                aR.Lines.Add("| persona | 頭像 | 顏色 |");
                aR.Lines.Add("|---|---|---|");
                int aNoAvatar = 0, aNoColor = 0;
                foreach (string p in SCP_PersonaDisplay.ListPersonas(aRoot))
                {
                    SCP_PersonaDisplayInfo i = SCP_PersonaDisplay.Get(aRoot, p);
                    if (!i.HasAvatar) aNoAvatar++;
                    if (i.ColorHex.Length == 0) aNoColor++;
                    aR.Lines.Add($"| `{p}` | {(i.HasAvatar ? "有" : "**沒有**（畫預設圖）")} | {ColorText(i)} |");
                }
                aR.AddValue("no_avatar", aNoAvatar.ToString());
                aR.AddValue("no_color", aNoColor.ToString());
                return aR;
            }

            if (aPersona.Length == 0) return SCP_CmdResult.Fail(2, $"✗ op={aOp} 要 `--arg persona=<p>`");

            if (aOp == "show")
            {
                SCP_PersonaDisplayInfo i = SCP_PersonaDisplay.Get(aRoot, aPersona);
                if (!i.HasLettersDir) return SCP_CmdResult.Fail(1, $"✗ 沒有這個 persona 的信件夾：{Path.Combine(aRoot, aPersona)}");
                var aR = SCP_CmdResult.Success($"# `{aPersona}` 的顯示資料");
                aR.Lines.Add($"- 顯示名：`{i.DisplayName}`（＝persona id）");
                aR.Lines.Add($"- 頭像：{(i.HasAvatar ? "`" + i.AvatarPath + "`" : "**沒有**（顯示端畫預設圖）")}");
                aR.Lines.Add($"- 顏色：{ColorText(i)}");
                aR.AddValue("avatar_path", i.AvatarPath);
                aR.AddValue("color", i.ColorHex);
                return aR;
            }

            if (aOp == "color")
            {
                string aHex = iArgs.Get("color").Trim();
                if (!SCP_PersonaDisplay.TrySetColor(aRoot, aPersona, aHex, out string? aErr))
                    return SCP_CmdResult.Fail(aErr != null && aErr.StartsWith("顏色要") ? 2 : 1, "✗ 沒有寫入：" + aErr);
                SCP_PersonaDisplayInfo i = SCP_PersonaDisplay.Get(aRoot, aPersona);   // 回讀，⛔ 不報寫入端以為自己寫了什麼
                var aR = SCP_CmdResult.Success(aHex.Length == 0
                    ? $"✅ `{aPersona}` 的顏色已刪掉（回到沒設）"
                    : $"✅ `{aPersona}` 的顏色 ＝ **{i.ColorHex}**（回讀）");
                aR.AddValue("color", i.ColorHex);
                return aR;
            }

            if (aOp == "avatar-url")
            {
                string aUrl = iArgs.Get("url").Trim();
                if (!SCP_PersonaDisplay.TrySetAvatarUrl(aRoot, aPersona, aUrl, out string? aUErr))
                    return SCP_CmdResult.Fail(2, "✗ 沒有寫入：" + aUErr);
                string aBack = SCP_PersonaDisplay.Get(aRoot, aPersona).AvatarUrl;   // 回讀
                return SCP_CmdResult.Success(aBack.Length == 0 ? $"✅ `{aPersona}` 的頭像網址已刪掉（回到預設範本）" : $"✅ `{aPersona}` 的頭像網址 ＝ {aBack}（回讀）")
                    .AddValue("avatar_url", aBack);
            }

            // op=avatar
            string aSrc = iArgs.Get("src").Trim();
            bool aConfirm = iArgs.Get("confirm").Trim() == "1";
            if (!SCP_PersonaDisplay.TrySetAvatar(aRoot, aPersona, aSrc, aConfirm, out string? aAvErr))
                return SCP_CmdResult.Fail(1, "✗ 沒有寫入：" + aAvErr);
            SCP_PersonaDisplayInfo aAfter = SCP_PersonaDisplay.Get(aRoot, aPersona);
            var aOk = SCP_CmdResult.Success($"✅ `{aPersona}` 的頭像已換成 `{aSrc}`");
            aOk.AddValue("avatar_path", aAfter.AvatarPath);
            return aOk;
        }

        static string ColorText(SCP_PersonaDisplayInfo i)
            => i.ColorInvalidRaw.Length > 0 ? $"**壞掉**（color.md 內容 `{i.ColorInvalidRaw}` 不是 #RRGGBB）"
             : i.ColorHex.Length > 0 ? $"`{i.ColorHex}`" : "沒設（預設色）";
    }
}
