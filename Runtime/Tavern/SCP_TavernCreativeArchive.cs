// 區塊職責：創作型發言（`tag=creative`）的**留念掛號信** —— 發文成功之後寄一封給作者，原文存一份。
// 物理意義：TASK-0312。Senate 的發文路
//          （`senate cmd tavern-post`）拿到 seq 之後呼叫這裡 ⇒ 判準與信文只有一份。
//          「訊息是流，會被推走、被讀掉、被壓縮；這封是存檔，跟著你走。」—— 免費系統掛號信（fee 0，不碰帳）。
// 邊界（三個都刻意，照 Editor 版）：
//   - **不擋發文主流程**：寄信失敗只回原因，⛔ 不讓已經貼出去的創作看起來像失敗。
//   - **匿名發文不寄**：sender_persona 空＝沒有可投遞的收件人。那不是錯誤，是沒有收件人。
//   - **不防重**：同一段創作重貼兩次就會收到兩封（不寄錢，重複的代價只是多一封信）。
// 數值影響：寫兩份信件檔（收件匣＋寄件備份）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Text;
using SCP.Core.Letters;

namespace SCP.Core.Tavern
{
    public static class SCP_TavernCreativeArchive
    {
        /// <summary>創作型發言的 tag —— 蓋這個 tag 的貼文會收到一封留念掛號信。</summary>
        public const string CreativeTag = "creative";

        /// <summary>這一則該不該寄（tag=creative、有作者、內文非空）。</summary>
        public static bool Applies(string? iSenderPersona, string? iBody, IReadOnlyDictionary<string, string>? iMeta)
            => iMeta != null && iMeta.TryGetValue("tag", out string? aTag) && aTag == CreativeTag
               && !string.IsNullOrEmpty(iSenderPersona) && !string.IsNullOrWhiteSpace(iBody);

        public static string Subject(string iRoom, int iSeq) => $"📜 創作留念 — {iRoom} seq {iSeq}";

        public static string RefId(string iRoom, int iSeq) => $"creative-{iRoom}-{iSeq}";

        /// <summary>信文本體（逐字沿用 Editor 版）。</summary>
        public static string BuildBody(string iRoom, int iSeq, string iBody)
        {
            var aSb = new StringBuilder();
            aSb.AppendLine($"你在 `{iRoom}` 發表的創作（seq {iSeq}），原文留一份在這裡。");
            aSb.AppendLine();
            aSb.AppendLine("---");
            aSb.AppendLine();
            aSb.AppendLine(iBody.TrimEnd());
            aSb.AppendLine();
            aSb.AppendLine("---");
            // 出處寫清楚：日後想回頭對照酒館原串時，seq 是唯一能定位的東西。
            aSb.AppendLine($"（出處：{iRoom} seq {iSeq}　tag=`{CreativeTag}`　"
                           + $"寄出於 {DateTime.Now:yyyy-MM-dd HH:mm}）");
            aSb.AppendLine("訊息是流，會被推走、被讀掉、被壓縮；這封是存檔，跟著你走。");
            return aSb.ToString();
        }

        /// <summary>
        /// 寄留念信。回 null ＝ 不適用（不是 creative／匿名／空內文）；回 true／false ＝ 寄了沒（失敗原因在 oError）。
        /// <para>⚠ 三態故意分開：「不需要寄」與「寄失敗」印同一句話，就是這個專案反覆咬人的那個形狀。</para>
        /// </summary>
        public static bool? TrySend(string iLettersRoot, string? iSenderPersona, string iRoom, int iSeq, string? iBody,
                                    IReadOnlyDictionary<string, string>? iMeta, out string oInboxPath, out string oError)
        {
            oInboxPath = ""; oError = "";
            if (!Applies(iSenderPersona, iBody, iMeta)) return null;
            return SCP_RegisteredMail.SendSystemMail(iLettersRoot, iSenderPersona!, Subject(iRoom, iSeq),
                BuildBody(iRoom, iSeq, iBody!), out oInboxPath, out oError, iRefId: RefId(iRoom, iSeq));
        }
    }
}
