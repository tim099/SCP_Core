// 區塊職責：T26 Solo Alter 配對發言間隔 —— 「這一則要不要延後、延多久」的判準（只判，不等）。
// 物理意義：TASK-0312。Alter 機制觸發後 agent 容易 self↔alter ping-pong
//          秒回、失去慢速意義；純 SKILL.md 自律守不住 ⇒ 寫入端自動延遲（不擋訊息）。
//          Editor 版在 handler 裡 `await` 剩餘秒數；Senate 版把訊息放進酒館 Server 的延後發文匣（`SenateTavernDeferred`），
//          到點由 Server 投回 tavern lane —— **兩邊用同一支判準**，⛔ 不各算一份。
// 延遲秒數（hierarchy 由高到低，逐字照 Editor）：
//   1. meta alter-pacing-bypass=true → 不延遲
//   2. meta alter-delay-sec=N（N ≥ 0）→ min(N, 900)
//   3. meta tag 含 idle-self-talk／idle-standby／standby → 720s
//   4. meta tag 含 brainstorm／self-talk → 30s
//   5. meta tag 含 slow → 300s；其他 → 預設 300s（fail-safe 走慢速）
// 配對條件：同房**最後一則**的 sender 是本 sender 的 alter 搭檔（`x` ↔ `x-alter`），且它的 ts 解析得出來。
//          中間有第三方 ⇒ 不算 ping-pong；第一筆無前筆 ⇒ 不延遲。剩餘 ＝ min(有效秒數 − 已過秒數, 900)。
// 數值影響：純函式，零 IO（最後一則由呼叫端讀好傳進來）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;

namespace SCP.Core.Tavern
{
    public static class SCP_TavernAlterPacing
    {
        public const double DefaultSec = 300.0;
        public const double BrainstormSec = 30.0;
        public const double IdleSec = 720.0;
        /// <summary>安全上限（T26.1：從 600s 上修至 900s 配合 idle 拉長至 720s）。</summary>
        public const double MaxSec = 900.0;

        const string AlterSuffix = "-alter";

        /// <summary>搭檔 id：<c>x</c> ↔ <c>x-alter</c>。</summary>
        public static string ExpectedPartner(string iSenderId)
            => iSenderId.EndsWith(AlterSuffix, StringComparison.Ordinal)
                ? iSenderId.Substring(0, iSenderId.Length - AlterSuffix.Length)
                : iSenderId + AlterSuffix;

        /// <summary>依 meta 決定有效間隔。<paramref name="oBypass"/>＝true 時不延遲（回傳值無意義）。</summary>
        public static double EffectiveSec(IReadOnlyDictionary<string, string>? iMeta, out bool oBypass)
        {
            oBypass = false;
            if (iMeta == null) return DefaultSec;
            if (iMeta.TryGetValue("alter-pacing-bypass", out string? aBypass)
                && aBypass != null && aBypass.ToLowerInvariant() == "true")
            {
                oBypass = true;
                return 0;
            }
            if (iMeta.TryGetValue("alter-delay-sec", out string? aRaw)
                && double.TryParse(aRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out double aExplicit)
                && aExplicit >= 0)
                return Math.Min(aExplicit, MaxSec);
            if (iMeta.TryGetValue("tag", out string? aTag) && !string.IsNullOrEmpty(aTag))
            {
                string aLow = aTag.ToLowerInvariant();
                if (aLow.Contains("idle-self-talk") || aLow.Contains("idle-standby") || aLow.Contains("standby")) return IdleSec;
                if (aLow.Contains("brainstorm") || aLow.Contains("self-talk")) return BrainstormSec;
                if (aLow.Contains("slow")) return DefaultSec;
            }
            return DefaultSec;
        }

        /// <summary>
        /// 這一則還要等多久。回 <c>null</c> ＝ 不延遲（bypass／有效秒數 0／上一則不是搭檔／沒有上一則／ts 解析不出）。
        /// </summary>
        /// <param name="iLastSenderId">同房最後一則的 sender_id（沒有上一則就給 null）。</param>
        /// <param name="iLastTs">那一則的 ts（ISO 8601；解析不出 ⇒ 不延遲，照 Editor）。</param>
        public static TimeSpan? Remaining(IReadOnlyDictionary<string, string>? iMeta, string iSenderId,
                                          string? iLastSenderId, string? iLastTs, DateTime iNowUtc)
        {
            double aEff = EffectiveSec(iMeta, out bool aBypass);
            if (aBypass || aEff <= 0) return null;
            if (string.IsNullOrEmpty(iLastSenderId) || string.IsNullOrEmpty(iLastTs)) return null;
            if (!string.Equals(iLastSenderId, ExpectedPartner(iSenderId), StringComparison.Ordinal)) return null;
            if (!DateTime.TryParse(iLastTs, CultureInfo.InvariantCulture,
                                   DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime aLast))
                return null;
            double aElapsed = (iNowUtc - aLast).TotalSeconds;
            if (aElapsed >= aEff) return null;
            return TimeSpan.FromSeconds(Math.Min(aEff - aElapsed, MaxSec));
        }
    }
}
