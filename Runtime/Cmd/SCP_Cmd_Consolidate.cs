// 區塊職責：`cmd consolidate` —— 見林（longterm digest）與見森（forest fold）。**原生**，不需要 Unity。
// 物理意義：兩段式，跟寫信同一個分工：**工具負責持久化與算狀態，反思的內容 agent 自己寫**。
//           不給 body ＝ inspect（印狀態＋列本段待濃縮的信）；給了 body ＝ 寫檔。
//           ⇒ 工具代筆的見林不是那個人的記憶，它只是一份摘要（憲法⑥）。
// 數值影響：linzi 寫 `longterm/wake_XXX-YYY.md` ＋ 重建 `_index.md` ＋ 歸檔見叢；
//           forest 寫 `longterm/forest/gen_NNN_*.md` ＋ 重建見根索引。
//           **不碰 registry／profile 的任何欄位**（理由見 SCP_Consolidate 檔頭的血證）。
using System.Collections.Generic;
using System.IO;
using System.Globalization;
using System;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_Consolidate : SCP_Cmd
    {
        public override string Name => "consolidate";

        public override string Summary => "見林／見森：不給 digest_body ＝ 只列狀態與待濃縮信件；給了才寫檔";

        public override string Details =>
            "兩段式（工具持久化、反思由 agent 親筆）：\n"
            + "  ① inspect —— 不給 digest_body：印 gap／建議 span／本段待濃縮的信件清單\n"
            + "  ② write   —— 給 digest_body：寫見林 digest ＋ 重建 _index ＋ 歸檔當期見叢\n"
            + "level=forest 時折見森（門檻 " + SCP_WakeLetters.ForestDigestThreshold + " 份見林；"
            + "rolling fold 只讀上代森 ＋ 最新見林）。\n"
            + "⚠ 長內文一律走 --arg-file digest_body=<檔>：見林 body 動輒上萬字，不該經過 shell。\n"
            + "🪵 **寫入前過兩道折人閘**（Tim 2026-09-09 拍板：見林流程需要先跑完折人）——\n"
            + "   ⓐ 根層還有未歸檔畫像 ⇒ 擋（先跑 `portrait-next` 到它印「折人完成」）；\n"
            + "   ⓑ 折人跑完但 digest_body 一位同事都沒提 ⇒ 擋 —— 見林＝這段期間的心得 ＋ 對同事的看法，一起寫。\n"
            + "   ⇒ 出口 `--arg fold_skip_reason=<理由>`：非空即放行，**理由會留名**（回傳檔 ＋ _cmd_results）。\n"
            + "⛔ 本 Cmd **不寫任何 registry／profile 欄位** —— 書籤是掃磁碟算出來的（最大 span_end）。\n"
            + "   python 那支（awakening.py consolidate）2026-09-02 起也不再寫 registry，\n"
            + "   原本「檔寫成功卻 exit=1」那條死路已拆掉；本 Cmd 仍是主入口（且不需要 Editor）。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("consolidate --arg letters_root=D:/Unity/LY/AgentCommands/ChatTavern/baton/letters"
                                   + " --arg persona=Template");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("persona", "誰的記憶", iRequired: true),
            new SCP_CmdArgSpec("level", "linzi ＝ 見林（預設）／forest ＝ 見森",
                               iDefault: "linzi", iChoices: new[] { "linzi", "forest" }),
            new SCP_CmdArgSpec("digest_body", "濃縮本文（不給＝只列狀態）。長內文走 --arg-file"),
            // wake 不推導的理由跟 wake-brief 同一條：推導值線上／離線差一號，
            // 而差錯的那份見林**看起來完全正常**。不給就用推導值，但會把用了哪一條印出來。
            new SCP_CmdArgSpec("wake", "現在是第幾次醒來。不給＝推導（wakes/ 信數 + 1），並印出用了哪一條"),
            new SCP_CmdArgSpec("span_start", "見林起 wake#（不給＝上次濃縮的下一號）"),
            new SCP_CmdArgSpec("span_end", "見林迄 wake#（不給＝現在的 wake）"),
            new SCP_CmdArgSpec("threshold", "overdue 門檻",
                               iDefault: SCP_Consolidate.DefaultGapThreshold.ToString()),
            // ⚠ 這一格是**兩道折人閘共用的出口**（Tim 2026-09-09 拍板）——
            //   非空即放行，而理由會被印出來並落進 `_cmd_results/<id>.json`（append-only ⇒ 不被下一次覆寫）。
            //   ⛔ 刻意不做成 `=1` 的布林旗標：一個不必寫理由的跳過，跟沒有閘一樣。
            new SCP_CmdArgSpec("fold_skip_reason",
                               "顯式跳過折人閘的理由（補跑舊區間等）。非空即放行，**理由會留名**"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aPersona = iArgs.Get("persona");
            string aBody = iArgs.Get("digest_body");
            bool aForest = iArgs.Get("level") == "forest";

            var aRoot = new SCP_LettersRoot(aLettersRoot);
            string aPersonaDir = SCP_LettersPaths.PersonaDir(aRoot, aPersona);
            if (!Directory.Exists(aPersonaDir))
                return SCP_CmdResult.Fail(1,
                    "✗ 找不到 persona 的信件夾：" + aPersonaDir,
                    "  （信件夾根：" + aLettersRoot + "）");

            return aForest
                ? RunForest(aLettersRoot, aPersona, aBody)
                : RunLinzi(iArgs, aLettersRoot, aPersona, aBody);
        }

        // ── 見林 ────────────────────────────────────────────────────

        SCP_CmdResult RunLinzi(SCP_CmdArgs iArgs, string iLettersRoot, string iPersona, string iBody)
        {
            int aWake = ParseInt(iArgs.Get("wake"), 0);
            int aThreshold = ParseInt(iArgs.Get("threshold"), SCP_Consolidate.DefaultGapThreshold);
            SCP_ConsolidateStatus aStatus = SCP_Consolidate.Status(iLettersRoot, iPersona, aWake, aThreshold);

            var aResult = new SCP_CmdResult();
            if (iBody.Length == 0)
            {
                aResult.Lines.Add("# 🧠 長期記憶整理狀態 — " + iPersona);
                aResult.Lines.Add("- wake_count: " + aStatus.WakeCount + "（" + aStatus.WakeCountSource + "）");
                aResult.Lines.Add("- last_consolidated_wake: " + aStatus.LastConsolidatedWake
                                  + " (@ " + (aStatus.LastConsolidatedAt.Length > 0
                                              ? aStatus.LastConsolidatedAt : "從未整理") + ")");
                aResult.Lines.Add("  ↳ 來源：掃 longterm/ 取最大 span_end（磁碟即事實，本 Cmd 不讀快取欄位）");
                aResult.Lines.Add("- gap: " + aStatus.Gap + " (門檻 " + aStatus.Threshold + ") → "
                                  + (aStatus.Overdue ? "⚠ OVERDUE 該整理" : "ok 尚未到門檻"));
                aResult.Lines.Add("- 建議 span: wake " + aStatus.SpanStart + "-" + aStatus.SpanEnd);
                aResult.Lines.Add("- 本段待濃縮 episodic letters (" + aStatus.PendingLetters.Count + " 封):");
                foreach (string aLetter in aStatus.PendingLetters) aResult.Lines.Add("  - " + aLetter);
                aResult.Lines.Add("");
                AppendFoldPeopleHint(iLettersRoot, iPersona, aResult);
            aResult.Lines.Add("");
            aResult.Lines.Add("→ 讀完上列信件後，反思濃縮成 digest body 寫回（長內文走檔案）：");
                aResult.Lines.Add("  " + SCP_CmdRegistry.Invoke(
                    "consolidate --arg letters_root=" + iLettersRoot + " --arg persona=" + iPersona
                    + " --arg-file digest_body=<檔> --arg span_start=" + aStatus.SpanStart
                    + " --arg span_end=" + aStatus.SpanEnd));
                aResult.AddValue("gap", aStatus.Gap.ToString());
                aResult.AddValue("overdue", aStatus.Overdue ? "1" : "0");
                aResult.AddValue("pending_letters", aStatus.PendingLetters.Count.ToString());
                return aResult;
            }

            // ── 折人閘（Tim 2026-09-09 拍板：見林流程需要先跑完折人）──────
            // ⚠ 這覆蓋 2026-09-01 那次「印提示，不擋」的拍板 —— 而被推翻的那個理由
            //   （補跑舊區間是合法場景）仍然為真 ⇒ 所以出口是 `fold_skip_reason`，不是把閘拿掉。
            SCP_CmdResult? aGate = FoldGate(iLettersRoot, iPersona, iBody,
                                            iArgs.Get("fold_skip_reason"), aResult);
            if (aGate != null) return aGate;

            string aSkipReason = iArgs.Get("fold_skip_reason");
            if (aSkipReason.Length > 0)
            {
                // 留名：印在回傳檔**並且**落進 _cmd_results（後者 append-only，不被下一次覆寫）。
                aResult.Lines.Add("⚠ **折人閘被顯式跳過** —— 理由：" + aSkipReason);
                aResult.AddValue("fold_gate", "skipped");
                aResult.AddValue("fold_skip_reason", aSkipReason);
            }

            int aSpanStart = ParseInt(iArgs.Get("span_start"), aStatus.SpanStart);
            int aSpanEnd = ParseInt(iArgs.Get("span_end"), aStatus.SpanEnd);
            if (aSpanEnd < aSpanStart)
                return SCP_CmdResult.Fail(2, "✗ span_end(" + aSpanEnd + ") < span_start(" + aSpanStart + ")",
                                          "  兩個都要給，或兩個都不給（不給＝用上面算出來的建議 span）");

            (string aPath, string aAt) = SCP_Consolidate.WriteDigest(
                iLettersRoot, iPersona, iBody, aSpanStart, aSpanEnd);
            aResult.Lines.Add("✅ 見林 digest 寫入: " + aPath);
            aResult.Lines.Add("   span: wake " + aSpanStart + "-" + aSpanEnd + "　consolidated_at: " + aAt);
            aResult.AddOutput(aPath);

            // 回讀：「我寫了」不是「它在裡面」。
            SCP_ConsolidateStatus aAfter = SCP_Consolidate.Status(iLettersRoot, iPersona, aWake, aThreshold);
            aResult.Lines.Add("   ↳ 回讀：last_consolidated_wake=" + aAfter.LastConsolidatedWake
                              + "　gap=" + aAfter.Gap);
            aResult.AddValue("gap", aAfter.Gap.ToString());

            string? aArchived = SCP_Consolidate.ArchiveKeys(iLettersRoot, iPersona, aSpanStart, aSpanEnd);
            aResult.Lines.Add(aArchived != null
                ? "   🌿 見叢已歸檔: " + aArchived + "（當期檔已重置）"
                : "   🌿 當期見叢沒有檔案 ⇒ 沒有東西可歸檔（不是錯誤）");
            if (aArchived != null) aResult.AddOutput(aArchived);

            SCP_ForestStatus aForest = SCP_Consolidate.ForestStatus(iLettersRoot, iPersona);
            aResult.Lines.Add("");
            aResult.Lines.Add(aForest.Overdue
                ? "   🌲 見森: 見林 " + aForest.DigestCount + " 份，已折到第 "
                  + aForest.FoldedDigestCount + " 份 → **有新見林待折**（--arg level=forest）"
                : "   🌲 見森: 見林 " + aForest.DigestCount + "/" + aForest.Threshold
                  + " 份　" + (aForest.Eligible ? "✓ 已是最新" : "未達折疊門檻"));
            aResult.AddValue("forest_overdue", aForest.Overdue ? "1" : "0");
            return aResult;
        }

        // ── 見森 ────────────────────────────────────────────────────

        SCP_CmdResult RunForest(string iLettersRoot, string iPersona, string iBody)
        {
            SCP_ForestStatus aStatus = SCP_Consolidate.ForestStatus(iLettersRoot, iPersona);
            var aResult = new SCP_CmdResult();

            if (iBody.Length == 0)
            {
                aResult.Lines.Add("# 🌲 見森狀態 — " + iPersona);
                aResult.Lines.Add("- 見林份數: " + aStatus.DigestCount + " (門檻 " + aStatus.Threshold + " 份)");
                aResult.Lines.Add("- 已折世代: gen" + aStatus.ForestCount
                                  + " (折到第 " + aStatus.FoldedDigestCount + " 份見林)");
                if (!aStatus.Eligible)
                {
                    aResult.Lines.Add("- 狀態: ○ 未達門檻，還差 "
                                      + (aStatus.Threshold - aStatus.DigestCount) + " 份見林");
                    aResult.AddValue("eligible", "0");
                    return aResult;
                }
                aResult.Lines.Add("- 狀態: " + (aStatus.Overdue
                    ? "⚠ 有 " + aStatus.Pending + " 份新見林待折" : "✓ 已是最新"));
                if (aStatus.ForestCount == 0)
                {
                    // 首折是唯一的多輸入折疊；之後恆為 2 份 ⇒ 成本不隨壽命成長。
                    aResult.Lines.Add("- **首折**（唯一的多輸入折疊）→ 讀下列全部見林:");
                    foreach (string aDigest in aStatus.Digests) aResult.Lines.Add("    - " + aDigest);
                }
                else
                {
                    aResult.Lines.Add("- rolling fold → 只讀 2 份輸入:");
                    aResult.Lines.Add("    - 上代森: " + aStatus.LatestForest);
                    aResult.Lines.Add("    - 新見林: " + aStatus.Digests[aStatus.Digests.Count - 1]);
                }
                aResult.Lines.Add("");
                aResult.Lines.Add("→ 讀完後寫回（森是**縱向敘事 + fragment 索引指標**，不是見林的串接）:");
                aResult.Lines.Add("  " + SCP_CmdRegistry.Invoke(
                    "consolidate --arg letters_root=" + iLettersRoot + " --arg persona=" + iPersona
                    + " --arg level=forest --arg-file digest_body=<檔>"));
                aResult.AddValue("eligible", "1");
                aResult.AddValue("forest_overdue", aStatus.Overdue ? "1" : "0");
                return aResult;
            }

            if (!aStatus.Eligible)
                return SCP_CmdResult.Fail(2,
                    "✗ 見林只有 " + aStatus.DigestCount + " 份，未達見森門檻 " + aStatus.Threshold + " 份",
                    "  先把見林折滿門檻 —— 森是折林的產物，沒有林就沒有森");

            string aPath = SCP_Consolidate.WriteForest(iLettersRoot, iPersona, iBody);
            aResult.Lines.Add("✅ 見森 gen" + aStatus.NextGen + " 寫入: " + aPath);
            aResult.Lines.Add("   folded_digest_count: " + aStatus.DigestCount + "（舊世代全保留，append-only）");
            aResult.AddOutput(aPath);
            aResult.AddValue("generation", aStatus.NextGen.ToString());

            // 見森之後重建見根索引（python 同一條連動）——碎片可能在折林時被抽過。
            string? aIndex = SCP_Fragments.WriteRootIndex(iLettersRoot, iPersona);
            if (aIndex != null)
            {
                aResult.Lines.Add("   見根索引已重建: " + aIndex);
                aResult.AddOutput(aIndex);
            }
            else
            {
                aResult.Lines.Add("   （沒有 fragment ⇒ 未建見根索引）");
            }
            return aResult;
        }

        static int ParseInt(string iRaw, int iFallback)
            => int.TryParse(iRaw, out int aValue) ? aValue : iFallback;

        // ── 折人閘（見林寫入路；Tim 2026-09-09 拍板）─────────────────
        // 區塊職責：見林**寫入之前**擋兩格 —— ⓐ 折人還沒跑完、ⓑ digest 一位同事都沒提。
        // 物理意義：見林＝這段期間的心得**與**這段期間對同事的看法，一起寫（Tim 2026-09-09 原話）。
        //           ⇒ 折人不是可以先做掉的獨立線，也不是做完就算 —— 它的產出要真的進到這一片林裡。
        // 🩸 為什麼從「提示」升級成「擋」：提示只印在 `iBody.Length == 0` 那條路（本檔唯一呼叫點），
        //   而帶 body 直接寫入時**一次都不印**。而 `SCP_WakeBrief` 的註解寫著「見林那條必經路上
        //   本來就印同一份讀數」—— 那句話只在「先跑一次不帶 body」的前提下成立。
        //   ⇒ 補跑舊區間（gap < 門檻 ⇒ brief 也不印）＋直接帶 body ＝ **零提示**。
        // ⚠ 而閘一定要有出口：補跑舊區間是合法場景（2026-09-01 拍板的理由，那條沒被推翻）
        //   ⇒ `fold_skip_reason` 非空即放行，**且理由留名**。擋而無路可走的閘會逼人繞路，
        //   而繞路的人下次連提示都不看。
        // ⛔ 警語走 `ioResult`（呼叫端那一份），**不經過任何 static 欄位** ——
        //   全域可變狀態在併發 lane 間是 last-write-wins，那正是 TASK-0116 修掉的那隻病。
        /// <returns>擋下時回 Fail；放行回 <c>null</c>（量不到時也放行，但警語已寫進 ioResult）。</returns>
        static SCP_CmdResult? FoldGate(string iLettersRoot, string iPersona, string iBody,
                                       string iSkipReason, SCP_CmdResult ioResult)
        {
            if (iSkipReason.Length > 0) return null;      // 顯式跳過（留名在呼叫端做）

            // ⓐ 折人跑完了沒 —— 量的是「根層還有幾幅未歸檔」，不是「我覺得重要的折完了沒」。
            int aTargets = 0;
            int aPortraits = 0;
            List<string> aNames;
            try
            {
                aNames = new List<string>(SCP_PortraitView.Targets(iLettersRoot, iPersona));
                foreach (string aOne in aNames)
                {
                    SCP_PortraitTargetView aView = SCP_PortraitView.Build(iLettersRoot, iPersona, aOne);
                    if (aView.UnarchivedPaths.Count == 0) continue;
                    aTargets++;
                    aPortraits += aView.UnarchivedPaths.Count;
                }
            }
            catch (Exception e)
            {
                // ⛔ 量不到**不擋** —— 「量不到」與「沒有待折」同形，而拿一個量不到的讀數去擋人
                //    會把一個工具故障變成別人的儀式卡關。出聲，然後放行。
                ioResult.Lines.Add("⚠ 折人閘**量不到**（" + e.GetType().Name + ": " + e.Message
                                   + "）—— 量不到 ≠ 沒有待折，本次不擋。");
                ioResult.AddValue("fold_gate", "unmeasurable");
                return null;
            }

            if (aTargets > 0)
                return SCP_CmdResult.Fail(2,
                    "🪵 **折人還沒跑完：" + aTargets + " 位 / " + aPortraits + " 幅未歸檔** ⇒ 見林先擋下",
                    "   見林＝這段期間的心得 ＋ 這段期間對同事的看法，**一起寫**（Tim 2026-09-09）——",
                    "   折人排在見林之後，那一輪的看法就只能等下一片，**差一整個見林單位（≈10 個 wake）**。",
                    "   ⇒ `cmd portrait-next --arg letters_root=" + iLettersRoot
                        + " --arg persona=" + iPersona + " --arg wake_range=<折的時點區間>`",
                    "   ⚠ 跑到它印「折人完成」為止 —— **清單清空才算**，"
                        + "別把「我覺得重要的都折了」當成折完（2026-09-01 血證：gura 少折 17 幅、",
                    "     basecamp 39 幅一幅未折，兩個人都以為自己做完了）。",
                    "   ⛔ 補跑舊區間等合法場景走 `--arg fold_skip_reason=<理由>`（理由會留名）。")
                    .AddValue("fold_gate", "blocked_unfolded")
                    .AddValue("pending_fold_targets", aTargets.ToString(CultureInfo.InvariantCulture))
                    .AddValue("pending_fold_portraits", aPortraits.ToString(CultureInfo.InvariantCulture));

            // ⓑ 折人跑完了，但這片林一位同事都沒提 ⇒ 那些看法沒有進到見林裡。
            // ⚠ 名單空就放行 —— 拿一個空名單去擋人，第一次見林的 persona 永遠過不了。
            //   （Tim 2026-09-09：「目前不會有沒有同事互動的區間」⇒ 名單非空時擋是安全的。）
            if (aNames.Count == 0) return null;

            foreach (string aName in aNames)
                if (aName.Length > 0
                    && iBody.IndexOf(aName, StringComparison.OrdinalIgnoreCase) >= 0)
                    return null;                          // 至少提到一位 ⇒ 放行

            return SCP_CmdResult.Fail(2,
                "🪵 **這片見林一位同事都沒提到** ⇒ 擋下（折人已跑完，但那些看法沒進到 digest 裡）",
                "   見林＝這段期間的心得 ＋ 這段期間對同事的看法，**一起寫**（Tim 2026-09-09）。",
                "   有畫像的對象共 " + aNames.Count + " 位：" + string.Join("／", aNames),
                "   ⇒ 在 digest_body 裡補一節寫這段期間對同事的看法（誰做了什麼、我的定位怎麼變）。",
                "   ⚠ 本閘量的是**名字有沒有出現**，不是寫得好不好 ——"
                    + "「有寫」與「該寫」它分不出來，那一格仍然是妳自己的。",
                "   ⛔ 真的沒有互動就走 `--arg fold_skip_reason=<理由>`（理由會留名）。")
                .AddValue("fold_gate", "blocked_no_colleague")
                .AddValue("portrait_targets", aNames.Count.ToString(CultureInfo.InvariantCulture));
        }

            // ── 折人提示（Tim 2026-09-01：印提示，**不擋**）────────────
        // 區塊職責：見林前提醒「折人還沒做完」，並附讀數。
        // 物理意義：折人要排在見林之前 —— 這一輪對同事的看法才趕得上這一片林；
        //           見林先跑的話那些看法只能等下一片，**差一整個見林單位（≈10 個 wake）**。
        // ⚠ 為什麼是提示不是守衛（Tim 拍板）：補跑舊區間的見林是合法場景，
        //   擋下來只會逼人繞路，而繞路的人下次連提示都不看。
        // 🩸 而它必須**附讀數**：2026-09-01 gura 少折 17 幅、basecamp 39 幅一幅未折，
        //   兩個人都以為自己做完了 —— 一句沒有數字的「記得先折人」擋不住那件事。
        static void AppendFoldPeopleHint(string iLettersRoot, string iPersona, SCP_CmdResult iResult)
        {
            int aTargets = 0;
            int aPortraits = 0;
            try
            {
                foreach (string aOne in SCP_PortraitView.Targets(iLettersRoot, iPersona))
                {
                    SCP_PortraitTargetView aView = SCP_PortraitView.Build(iLettersRoot, iPersona, aOne);
                    if (aView.UnarchivedPaths.Count == 0) continue;
                    aTargets++;
                    aPortraits += aView.UnarchivedPaths.Count;
                }
            }
            catch (Exception e)
            {
                // 量不到要說出來 —— 靜默跳過會讓「沒有待折」與「沒去數」同形。
                iResult.Lines.Add("");
                iResult.Lines.Add("⚠ 折人待辦**量不到**（" + e.GetType().Name + ": " + e.Message
                                  + "）—— 量不到 ≠ 沒有待折。");
                return;
            }

            iResult.AddValue("pending_fold_targets", aTargets.ToString(CultureInfo.InvariantCulture));
            iResult.AddValue("pending_fold_portraits", aPortraits.ToString(CultureInfo.InvariantCulture));
            if (aTargets == 0) return;      // 沒待辦就不佔版面

            iResult.Lines.Add("");
            iResult.Lines.Add("🪵 **折人還沒做完：" + aTargets + " 位 / " + aPortraits + " 幅未歸檔**"
                              + "（見林前該先折人 —— 這一輪的看法才趕得上這一片林）");
            iResult.Lines.Add("   ⇒ `cmd portrait-next --arg letters_root=<root> --arg persona="
                              + iPersona + " --arg wake_range=<折的時點區間>`");
            iResult.Lines.Add("   ⚠ 這是**提示不是守衛** —— 補跑舊區間的見林照跑，"
                              + "但別把「我覺得重要的都折了」當成折完（清單清空才算）。");
        }
}
}
