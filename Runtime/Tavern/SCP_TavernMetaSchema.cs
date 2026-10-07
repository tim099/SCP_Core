// 區塊職責：酒館訊息 meta 的 **T06.3 schema 驗證** —— tag=commit／task-assign／task-ack 的必填欄位與格式。
// 物理意義：TASK-0311，epic 0295 ③ 第二刀。
//          之前只有 Editor 那一份 ⇒ Senate 的發文路（`senate cmd tavern-post`／`senate cmd commit` 的公告）
//          遇到這三個 tag 只能擋下交回 Editor。⇒ 抽到這裡後 **Editor 與 Senate 呼叫同一支**，⛔ 不各寫一份：
//          兩份驗證遲早分岔，而分岔的失效樣子是「同一則公告，走這條路被擋、走那條路被收」。
//   · commit     ：必帶 `sha`，只能一個，7～40 位十六進位（commit 公告同時是 +5 的請款憑證 —— Tim 2026-07-30）
//   · task-assign：必帶 task_id／task_body／assigned_by／requires_ack
//   · task-ack   ：必帶 task_id，action ∈ accept|decline|defer
// 數值影響：純函式，零 IO。回 null ＝ 通過（或不是這三個 tag）；回字串 ＝ 拒絕理由（訊息逐字沿用 Editor 版）。
// ⚠ 不驗「那個 SHA 真的存在」—— 那要枚舉 submodule 路徑，正是 install-path 陷阱（Editor 版原註解的判斷，照留）。
#nullable enable
using System.Collections.Generic;

namespace SCP.Core.Tavern
{
    public static class SCP_TavernMetaSchema
    {
        /// <summary>本支會檢查的 tag。其他 tag 一律放行（回 null）。</summary>
        public static readonly string[] CheckedTags = { "commit", "task-assign", "task-ack" };

        /// <summary>
        /// 驗 meta。回 <c>null</c> ＝ 通過；回字串 ＝ 拒絕理由（呼叫端要**確定沒發**，⛔ 不可以照發）。
        /// <para>⚠ 以 <c>meta["tag"]</c> 判斷；沒有 tag 或 tag 不在 <see cref="CheckedTags"/> 裡 ⇒ 放行。</para>
        /// </summary>
        public static string? Validate(IReadOnlyDictionary<string, string>? iMeta)
        {
            if (iMeta == null) return null;
            if (!iMeta.TryGetValue("tag", out string? aTag) || string.IsNullOrEmpty(aTag)) return null;

            if (aTag == "task-assign")
            {
                foreach (string aReq in new[] { "task_id", "task_body", "assigned_by", "requires_ack" })
                    if (!Has(iMeta, aReq))
                        return $"tag=task-assign 缺 meta.{aReq} (T06.3 schema). Required: task_id / task_body / assigned_by / requires_ack";
                return null;
            }

            if (aTag == "commit")
            {
                if (!Has(iMeta, "sha"))
                    return "tag=commit 缺 meta.sha (T06.3 schema)。commit 公告必須帶 SHA 當計酬憑證。"
                           + "一則訊息對一個 SHA；三層 bump 請分三則各自公告（Tim 2026-07-30 拍板）。";
                string aSha = iMeta["sha"].Trim();
                if (aSha.Contains(","))
                    return "tag=commit 的 meta.sha 只能帶一個 SHA（收到逗號分隔的多個）。"
                           + "三層 bump 請分三則訊息各自公告，每則帶自己那層的 SHA。";
                if (!LooksLikeSha(aSha))
                    return $"tag=commit 的 meta.sha 格式不像 git SHA（收到 '{aSha}'）。"
                           + "需為 7~40 位十六進位字元，例如 sha:910a2493。";
                return null;
            }

            if (aTag == "task-ack")
            {
                if (!Has(iMeta, "task_id")) return "tag=task-ack 缺 meta.task_id (T06.3 schema)";
                if (!iMeta.TryGetValue("action", out string? aAction)
                    || (aAction != "accept" && aAction != "decline" && aAction != "defer"))
                    return "tag=task-ack 缺 meta.action 或 action 非 accept|decline|defer (T06.3 schema)";
                return null;
            }
            return null;
        }

        static bool Has(IReadOnlyDictionary<string, string> iMeta, string iKey)
            => iMeta.TryGetValue(iKey, out string? aVal) && !string.IsNullOrEmpty(aVal);

        /// <summary>輕量格式檢查：7～40 位十六進位（git short sha 最短 7、full sha 40）。</summary>
        static bool LooksLikeSha(string iSha)
        {
            if (iSha.Length < 7 || iSha.Length > 40) return false;
            foreach (char c in iSha)
            {
                bool aHex = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
                if (!aHex) return false;
            }
            return true;
        }
    }
}
