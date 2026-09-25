// 區塊職責：**酒館發文發薪的規劃** —— 一則已落檔的訊息該入哪幾筆帳（TASK-0296，TASK-0295 ①）。
// 物理意義：Tim 2026-09-25：「盡可能把發薪也轉到 Senate 端（銀行也搬了），逐步拆掉 Unity 依賴」。
//           此前規則住在 Editor 的 `Cmd_Tavern op=post` 結尾 ⇒ 只有走那一個入口的訊息會付
//           （TASK-0106 #24：直打 `tavern-write` 的 4 則沒付，照構造就不付）。
//           ⇒ 規則搬到這裡、掛在**寫入端**：server 模式由 Senate Server 寫完就規劃，
//             editor 模式由 Editor 本地寫完就規劃 —— 兩邊呼叫同一支，⛔ 規則只有這一份。
// 數值影響：**本類不碰錢**，只產出清單（誰／多少／為什麼／冪等鍵）；入帳由宿主交給銀行那顆 Server。
//   規則逐條搬自 Cmd_Tavern（2026-09-25 版），語意不變：
//     A 底薪 work_post　+1　 發言者是真實 agent、非出資方、非工具廣播、落在計酬 group（路由判準）、persona 解析得到帳號
//     B token_parse　　　±N　 只有白名單發言者（Tim）；@對象 N token ⇒ 給對象／支付 N token ⇒ 扣發言者／N token ⇒ 給發言者；單路上限 100
//     D commit　　　　　 +5　 tag=commit 且帶 meta.sha、非出資方
//     E reading_note　　 +3　 tag=reading-note、非出資方
//   ⭐ 唯一刻意的改變：**冪等鍵一律是這則訊息確定的鍵**（`<kind>_<room>_<seq>[_後綴]`）。
//     舊版底薪在呼叫端帶 idempotency_key 時用它、B/D/E 完全沒帶 ⇒ 同一則被兩條路各付一次時帳本擋不住。
//     改成確定鍵之後，同一則訊息不論從哪條路規劃，送到銀行都是同一把鍵 ⇒ 第二次命中冪等、錢不動。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SCP.Core.Bank;
using SCP.Core.Paths;

namespace SCP.Core.Tavern
{
    /// <summary>規劃的輸入：一則**已經落檔、拿到 seq** 的訊息。</summary>
    public sealed class SCP_TavernPayInput
    {
        public string Room = "";
        public int Seq;
        public string SenderId = "";
        public string SenderPersona = "";
        public string Body = "";
        public IReadOnlyDictionary<string, string>? Meta;

        public static SCP_TavernPayInput From(SCP_TavernMessage iMsg, string iRoom, int iSeq)
            => new SCP_TavernPayInput
            {
                Room = iRoom, Seq = iSeq, SenderId = iMsg.SenderId, SenderPersona = iMsg.SenderPersona,
                Body = iMsg.Body, Meta = iMsg.Meta,
            };
    }

    public enum SCP_TavernPayDirection { Credit, Debit }

    /// <summary>一筆要入的帳。欄位直接對應 `senate cmd bank` 的參數。</summary>
    public sealed class SCP_TavernPayItem
    {
        public SCP_TavernPayDirection Direction = SCP_TavernPayDirection.Credit;
        public string Account = "";
        public int Amount;
        public string Kind = "";
        public string Ref = "";
        public string Description = "";
        public string CmdId = "";
        public string IdemKey = "";
        /// <summary>哪一條規則產生的（A／B／D／E）—— 回報與稽核用。</summary>
        public string Rule = "";

        public string BankOp => Direction == SCP_TavernPayDirection.Credit ? "credit" : "debit";
    }

    /// <summary>規劃結果。<see cref="Warnings"/> 是**設定壞了**的訊號（要大聲），<see cref="Notes"/> 只是「這條不適用」。</summary>
    public sealed class SCP_TavernPayPlan
    {
        public List<SCP_TavernPayItem> Items = new List<SCP_TavernPayItem>();
        public List<string> Notes = new List<string>();
        public List<string> Warnings = new List<string>();
    }

    public static class SCP_TavernPayroll
    {
        public const int WorkPostReward = 1;
        public const int CommitPostReward = 5;
        public const int ReadingNoteReward = 3;
        public const int TokenParseMaxPerMessage = 100;

        public const string KindWorkPost = "work_post";
        public const string KindTokenParse = "token_parse";
        public const string KindCommit = "commit";
        public const string KindReadingNote = "reading_note";

        /// <summary>出資方不領薪（T46）。</summary>
        static readonly HashSet<string> s_HumanPayers = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tim" };
        /// <summary>token_parse 只認這些發言者（v1：Tim only，避免 agent 自己給自己打錢）。</summary>
        static readonly HashSet<string> s_TokenParseSenders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Tim" };

        // T49 token_parse 三層（優先序：L1 先消化 → L2 在剩下的 → L3 在最後剩下的）
        static readonly Regex s_RxAtRecipient = new Regex(@"@(\S+?)\s+(\d+)\s*token", RegexOptions.IgnoreCase);
        static readonly Regex s_RxPayPrefix = new Regex(@"(?:支付|付|出|花|debit)\s*(\d+)\s*token", RegexOptions.IgnoreCase);
        static readonly Regex s_RxSimple = new Regex(@"(\d+)\s*token", RegexOptions.IgnoreCase);

        public static string PostRewardSourceRef(string iRoom, int iSeq) => iRoom + "#seq=" + iSeq;

        /// <summary>
        /// 這棵資料樹的銀行根 —— 走 <see cref="SCP_PathRegistry"/> 的**同一條推導**（BankRoot ＝ AgentCommandsRoot 衍生），
        /// ⛔ 不在這裡另拼 `/Bank`（TASK-0260：bank_root 是推導值，多一份拼法就多一份會漂的答案）。
        /// </summary>
        public static SCP_PathResolution BankRootOf(string iDataRoot)
            => SCP_PathRegistry.Resolve(SCP_PathId.BankRoot,
                   iId => iId == SCP_PathId.AgentCommandsRoot ? SCP_PathStoredValue.Of(iDataRoot) : SCP_PathStoredValue.Of(""));

        /// <summary>一筆規劃 → `senate cmd bank` 的參數（宿主拿去 Dispatch）。</summary>
        public static Dictionary<string, string> ToBankArgs(SCP_TavernPayItem iItem, string iBankRoot)
            => new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["op"] = iItem.BankOp,
                ["bank_root"] = iBankRoot,
                ["account"] = iItem.Account,
                ["amount"] = iItem.Amount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["kind"] = iItem.Kind,
                ["ref"] = iItem.Ref,
                ["description"] = iItem.Description,
                ["caller"] = "system",
                ["cmd_id"] = iItem.CmdId,
                ["idem_key"] = iItem.IdemKey,
            };

        /// <summary>一筆規劃的一行人讀摘要（兩個宿主印同一句，⛔ 不各寫一份）。</summary>
        public static string Describe(SCP_TavernPayItem i)
            => $"{i.Rule} {(i.Direction == SCP_TavernPayDirection.Credit ? "+" : "-")}{i.Amount} {i.Kind} → {i.Account}（ref={i.Ref}）";

        /// <summary>
        /// 真實 agent 判定：system／NPC／bot／alter／discord 中繼一律不是。語意逐條搬自 Cmd_Tavern.IsRealAgentSender。
        /// </summary>
        public static bool IsRealAgentSender(string? iSenderId)
        {
            if (string.IsNullOrWhiteSpace(iSenderId)) return false;
            string aLower = iSenderId!.Trim().ToLowerInvariant();
            if (aLower.StartsWith("_")) return false;
            if (aLower == "system") return false;
            if (aLower == "tavern-keeper") return false;
            if (aLower.StartsWith("discord:")) return false;
            if (aLower.Contains("bot")) return false;
            if (aLower.EndsWith("-alter")) return false;
            return true;
        }

        public static bool IsHumanPayer(string? iSenderId) => iSenderId != null && s_HumanPayers.Contains(iSenderId);

        /// <summary>
        /// 底薪的**發放判準單一入口** —— 發放路徑與事後補款共用（補款若自己抄一份，補出來的就不是當時本來會發的）。
        /// 回 false 時 <paramref name="oSkipReason"/> 一律有值。
        /// </summary>
        public static bool IsPostRewardEligible(string iDataRoot, string iSenderId, string? iCategory,
                                                out string oGroupId, out string oSkipReason)
        {
            oGroupId = ""; oSkipReason = "";
            if (!IsRealAgentSender(iSenderId)) { oSkipReason = "非真實 agent（system / NPC / bot / alter / discord 中繼）"; return false; }
            if (IsHumanPayer(iSenderId)) { oSkipReason = "出資方不領薪（T46）"; return false; }
            SCP_TavernRoutingRead aRouting = SCP_TavernRouting.Read(iDataRoot);
            if (!aRouting.Ok) { oSkipReason = "找不到 routing target group（判準讀不了：" + aRouting.Error + "）"; return false; }
            SCP_TavernRouteGroup? g = SCP_TavernRouting.ResolveTargetGroup(aRouting.Groups, iCategory);
            if (g == null) { oSkipReason = "找不到 routing target group（設定異常：無命中且無 enabled 的 default group）"; return false; }
            oGroupId = g.Id;
            if (!g.IsPaidPost) { oSkipReason = $"group `{g.Id}` 不計酬（IsPaidPost=false）"; return false; }
            return true;
        }

        /// <summary>
        /// 規劃一則訊息的發薪。純函式（只讀路由判準與帳號解析），⛔ 不寫任何帳。
        /// </summary>
        public static SCP_TavernPayPlan Plan(string iDataRoot, SCP_TavernPayInput iIn)
        {
            var aPlan = new SCP_TavernPayPlan();
            if (iIn.Seq <= 0) { aPlan.Notes.Add("seq ≤ 0（寫入沒成功）⇒ 不發"); return aPlan; }
            if (!IsRealAgentSender(iIn.SenderId)) { aPlan.Notes.Add($"sender `{iIn.SenderId}` 不是真實 agent ⇒ 全部不發"); return aPlan; }

            string aLetters = SCP_DataPaths.Letters(new SCP_DataRoot(iDataRoot)).Value;
            string aRegion = SCP_BankRegion.Read(iDataRoot, out string? aRegionWhy);
            if (aRegionWhy != null) aPlan.Warnings.Add("區域讀的是預設值（" + aRegion + "）：" + aRegionWhy);
            string Resolve(string iRaw)
            {
                SCP_BankResolution r = SCP_BankAccountResolver.Resolve(aLetters, iDataRoot, aRegion, iRaw);
                return r.IsUnresolved ? iRaw : r.AccountId;    // 解不到 ⇒ 原樣交給銀行，由它說清楚為什麼不收
            }

            IReadOnlyDictionary<string, string> aMeta = iIn.Meta ?? new Dictionary<string, string>();
            string MetaOf(string k) => aMeta.TryGetValue(k, out string? v) && v != null ? v : "";
            string aRef = PostRewardSourceRef(iIn.Room, iIn.Seq);
            string aKeyBase = iIn.Room + "_" + iIn.Seq;

            // ── A 底薪 ──
            bool aAutoBroadcast = string.Equals(MetaOf("auto-broadcast"), "true", StringComparison.OrdinalIgnoreCase);
            string aCategory = MetaOf("category");
            if (aAutoBroadcast) aPlan.Notes.Add("A：工具廣播（auto-broadcast）不領發言底薪");
            else if (!IsPostRewardEligible(iDataRoot, iIn.SenderId, aCategory, out string aGroup, out string aWhy))
            {
                if (aWhy.StartsWith("找不到 routing target group")) aPlan.Warnings.Add("A：" + aWhy);
                else aPlan.Notes.Add("A：" + aWhy);
            }
            else if (string.IsNullOrWhiteSpace(iIn.SenderPersona))
                aPlan.Notes.Add("A：沒帶 persona（匿名發言）⇒ 不計酬");
            else
            {
                SCP_BankResolution aPayee = SCP_BankAccountResolver.Resolve(aLetters, iDataRoot, aRegion, iIn.SenderPersona);
                if (aPayee.IsUnresolved)
                    aPlan.Warnings.Add($"A：persona `{iIn.SenderPersona}` 解析不到正式帳號（sender={iIn.SenderId}）⇒ 不計酬 —— 應該領薪的話去銀行補登記");
                else
                    aPlan.Items.Add(new SCP_TavernPayItem
                    {
                        Account = aPayee.AccountId, Amount = WorkPostReward, Kind = KindWorkPost, Ref = aRef,
                        Description = $"post reward: category={(aCategory.Length == 0 ? "(unset→default)" : aCategory)} group={aGroup} seq={iIn.Seq}",
                        CmdId = KindWorkPost + "_" + aKeyBase, IdemKey = KindWorkPost + "_" + aKeyBase, Rule = "A",
                    });
            }

            // ── B token_parse ──
            if (!string.IsNullOrWhiteSpace(iIn.Body) && s_TokenParseSenders.Contains(iIn.SenderId))
                PlanTokenParse(iIn, aRef, aKeyBase, Resolve, aPlan);

            // ── D commit ──
            string aTag = MetaOf("tag");
            if (aTag == "commit")
            {
                string aSha = MetaOf("sha").Trim().ToLowerInvariant();
                if (aSha.Length == 0) aPlan.Notes.Add("D：tag=commit 但沒有 meta.sha ⇒ 不發");
                else if (IsHumanPayer(iIn.SenderId)) aPlan.Notes.Add("D：出資方不領 commit 獎勵");
                else aPlan.Items.Add(new SCP_TavernPayItem
                {
                    Account = Resolve(iIn.SenderId), Amount = CommitPostReward, Kind = KindCommit, Ref = aSha,
                    Description = $"commit post reward: sha={aSha} {aRef}",
                    CmdId = "commit_" + aSha, IdemKey = KindCommit + "_" + aKeyBase, Rule = "D",
                });
            }

            // ── E reading_note ──
            if (aTag == "reading-note")
            {
                if (IsHumanPayer(iIn.SenderId)) aPlan.Notes.Add("E：出資方不領讀書心得獎勵");
                else aPlan.Items.Add(new SCP_TavernPayItem
                {
                    Account = Resolve(iIn.SenderId), Amount = ReadingNoteReward, Kind = KindReadingNote, Ref = aRef,
                    Description = $"reading note share reward: {aRef}",
                    CmdId = KindReadingNote + "_" + aKeyBase, IdemKey = KindReadingNote + "_" + aKeyBase, Rule = "E",
                });
            }
            return aPlan;
        }

        static void PlanTokenParse(SCP_TavernPayInput iIn, string iRef, string iKeyBase,
                                   Func<string, string> iResolve, SCP_TavernPayPlan oPlan)
        {
            string aRemaining = iIn.Body;
            string aBase = KindTokenParse + "_" + iKeyBase;

            // L1：@對象 N token ⇒ 給對象（同一對象多次出現先加總）
            MatchCollection aAt = s_RxAtRecipient.Matches(aRemaining);
            if (aAt.Count > 0)
            {
                var aPer = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var aOrder = new List<string>();
                foreach (Match m in aAt)
                {
                    string aWho = m.Groups[1].Value.Trim();
                    if (aWho.Length == 0 || !int.TryParse(m.Groups[2].Value, out int n) || n <= 0) continue;
                    if (!aPer.ContainsKey(aWho)) { aPer[aWho] = 0; aOrder.Add(aWho); }
                    aPer[aWho] += n;
                }
                foreach (string aWho in aOrder)
                {
                    int n = aPer[aWho];
                    if (n > TokenParseMaxPerMessage) { oPlan.Warnings.Add($"B：@{aWho} 合計 {n} > 上限 {TokenParseMaxPerMessage} ⇒ 不發"); continue; }
                    oPlan.Items.Add(new SCP_TavernPayItem
                    {
                        Account = iResolve(aWho), Amount = n, Kind = KindTokenParse, Ref = iRef + "@" + aWho,
                        Description = $"token parse @recipient: +{n} to {aWho} (sender={iIn.SenderId} seq={iIn.Seq})",
                        CmdId = aBase + "_at_" + aWho, IdemKey = aBase + "_at_" + aWho, Rule = "B",
                    });
                }
                aRemaining = s_RxAtRecipient.Replace(aRemaining, " ");
            }

            // L2：支付 N token ⇒ 扣發言者
            int aPay = 0;
            foreach (Match m in s_RxPayPrefix.Matches(aRemaining))
                if (int.TryParse(m.Groups[1].Value, out int n) && n > 0) aPay += n;
            if (aPay > 0)
            {
                if (aPay > TokenParseMaxPerMessage) oPlan.Warnings.Add($"B：支付合計 {aPay} > 上限 {TokenParseMaxPerMessage} ⇒ 不扣");
                else oPlan.Items.Add(new SCP_TavernPayItem
                {
                    Direction = SCP_TavernPayDirection.Debit,
                    Account = iResolve(iIn.SenderId), Amount = aPay, Kind = KindTokenParse, Ref = iRef,
                    Description = $"token parse pay-prefix: -{aPay} from {iIn.SenderId} (seq={iIn.Seq})",
                    CmdId = aBase + "_pay", IdemKey = aBase + "_pay", Rule = "B",
                });
                aRemaining = s_RxPayPrefix.Replace(aRemaining, " ");
            }

            // L3：N token ⇒ 給發言者
            int aCredit = 0;
            foreach (Match m in s_RxSimple.Matches(aRemaining))
                if (int.TryParse(m.Groups[1].Value, out int n) && n > 0) aCredit += n;
            if (aCredit > 0)
            {
                if (aCredit > TokenParseMaxPerMessage) oPlan.Warnings.Add($"B：合計 {aCredit} > 上限 {TokenParseMaxPerMessage} ⇒ 不發");
                else oPlan.Items.Add(new SCP_TavernPayItem
                {
                    Account = iResolve(iIn.SenderId), Amount = aCredit, Kind = KindTokenParse, Ref = iRef,
                    Description = $"token parse fallback: +{aCredit} to {iIn.SenderId} (seq={iIn.Seq})",
                    CmdId = aBase + "_fallback", IdemKey = aBase + "_fallback", Rule = "B",
                });
            }
        }
    }
}
