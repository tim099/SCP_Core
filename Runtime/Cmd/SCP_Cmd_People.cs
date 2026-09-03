// 區塊職責：`cmd people` —— 見人查詢的 Cmd 介面（**原生**，不需要 Unity）。
// 物理意義：組裝邏輯在 SCP_PortraitView，本檔只做「參數 → 呼叫 → 回報」。
//           ⇒ brief §6.5 之後接的是同一支邏輯，**不是第二份組法**。
//           🩸 為什麼堅持同源：兩處各組一次的症狀不是報錯，是「CLI 說信任、brief 說 65」
//             兩邊都不紅 —— 同族活體見 UCL 那側的 commands_schema（宣告 30 op／實作 39 分支）。
// 數值影響：**純讀**，一個位元組都不寫。
//
// ⚠ `online=1` 的空清單有兩種意思，本 Cmd **不把它們合成一句**：
//   「真的沒人在線」與「lock 讀不到（狀態未知）」是不同的答案，
//   而後者被印成前者的話，讀的人會拿它當「今天沒人陪我」的證據。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using SCP.Core.Letters;
using SCP.Core.Paths;

namespace SCP.Core.Cmd
{
    public sealed class SCP_Cmd_People : SCP_Cmd
    {
        public override string Name => "people";

        public override string Summary => "見人：對某位同事的看法（最新一版濃縮 ＋ 本期未歸檔畫像）";

        public override string Details =>
            "資料源是**自己的** sketchbook：`<target>/<target>_vNNN.md`（濃縮，取最大版號）\n"
            + "⭐ `pending=1` ＝ 見林折人前的待折清單（把「折完了嗎」從記得變成量得到）。\n"
            + "＋ 根層 `<ts>__about_<target>.md`（本期未歸檔）。\n"
            + "⚠ 版號一律**解析整數**取最大 —— 字串排序在第 10 版之後會安靜讀成第 9 版。\n"
            + "⚠ 本 Cmd 不回頭撈已歸檔的逐幅畫像（`<target>/raw/`）：看法本來就隨時間衰減，\n"
            + "   raw 存在的理由是「哪天覺得怪，回頭分得出是變化還是失真」，不是每次都讀。\n"
            + "⚠ 分數不在這裡 —— relationship 是事件帳本、分數由事件重算，這支只給質性看法。";

        public override string Example =>
            SCP_CmdRegistry.Invoke("people --arg letters_root=D:/Unity/Bar/AgentCommands/ChatTavern/baton/letters"
                                   + " --arg persona=Template --arg target=summit");

        public override IReadOnlyList<SCP_CmdArgSpec> ArgSpecs => new[]
        {
            new SCP_CmdArgSpec("letters_root", "persona 信件夾根目錄（絕對路徑）", iRequired: true),
            new SCP_CmdArgSpec("persona", "誰的看法（讀誰的 sketchbook）", iRequired: true),
            new SCP_CmdArgSpec("target", "看對誰的看法。不給＝依 online / all 決定要列誰"),
            new SCP_CmdArgSpec("online", "1 ＝ 依次列出所有**在線**同事（需要 _session 讀得到）"),
            new SCP_CmdArgSpec("all", "1 ＝ 列出所有畫過的對象（不管在不在線）"),
            new SCP_CmdArgSpec("pending", "1 ＝ 只列**還有未歸檔畫像**的對象（見林折人前的清單）"),
            new SCP_CmdArgSpec("bodies", "1 ＝ 連內文一起印（預設只印指標與讀數）"),
        };

        public override SCP_CmdResult Execute(SCP_CmdArgs iArgs)
        {
            string aLettersRoot = iArgs.Get("letters_root");
            string aPersona = iArgs.Get("persona");
            string aTarget = iArgs.Get("target");
            bool aOnline = iArgs.Get("online") == "1";
            bool aAll = iArgs.Get("all") == "1";
            bool aPending = iArgs.Get("pending") == "1";
            bool aBodies = iArgs.Get("bodies") == "1";

            var aRoot = new SCP_LettersRoot(aLettersRoot);
            string aPersonaDir = SCP_LettersPaths.PersonaDir(aRoot, aPersona);
            if (!Directory.Exists(aPersonaDir))
                // 「這個人不存在」與「信件夾根設錯」是兩件事 —— 兩條路徑都印出來讓人自己分辨。
                return SCP_CmdResult.Fail(1,
                    "✗ 找不到 persona 的信件夾：" + aPersonaDir,
                    "  （信件夾根：" + aLettersRoot + "）");

            var aResult = new SCP_CmdResult();
            List<string> aTargets;

            // ── pending：見林折人前的「還沒折完」清單 ──────────────
            // 區塊職責：把「折完了嗎」從**記得**變成**量得到**。
            // 🩸 為什麼要有它（2026-09-01 的實害）：Tim 的規則是見林時把根層未歸檔的全折完，
            //   而那天 gura 漏了 17 幅（summit 10／Sirius 6／apex-one 1）、basecamp 漏了 39 幅，
            //   **兩個人都以為自己做完了**。漏掉的畫像既不會被任何一版吃進去、
            //   又因為見人只看近 14 天而看不見 ⇒ 靜默遺棄，不會有任何一格報錯。
            //   ⇒ 修法不是「下次記得」，是給見林那步一個可以印出來的讀數。
            if (aPending)
            {
                var aRows = new List<string>();
                int aTotalPortraits = 0;
                foreach (string aOne in SCP_PortraitView.Targets(aLettersRoot, aPersona))
                {
                    SCP_PortraitTargetView aView = SCP_PortraitView.Build(aLettersRoot, aPersona, aOne);
                    if (aView.UnarchivedPaths.Count == 0) continue;
                    aTotalPortraits += aView.UnarchivedPaths.Count;
                    string aHas = aView.Latest != null
                        ? "已有 v" + aView.Latest.Version + "（" + (aView.Latest.WakeRange.Length > 0
                            ? aView.Latest.WakeRange : "區間不明") + "）⇒ 這批會折成 v"
                            + (aView.Latest.Version + 1)
                        : "尚無濃縮 ⇒ 這批會折成 v1";
                    aRows.Add("- **" + aOne + "**　未歸檔 " + aView.UnarchivedPaths.Count + " 幅　"
                              + aHas);
                }

                if (aRows.Count == 0)
                {
                    aResult.Lines.Add("✅ 沒有待折的對象 —— " + aPersona
                                      + " 的 sketchbook 根層一幅未歸檔畫像都沒有。");
                }
                else
                {
                    aResult.Lines.Add("# 🪵 待折清單 — " + aPersona + "（" + aRows.Count + " 位／共 "
                                      + aTotalPortraits + " 幅未歸檔）");
                    aResult.Lines.Add("");
                    aResult.Lines.AddRange(aRows);
                    aResult.Lines.Add("");
                    aResult.Lines.Add("⇒ 逐位跑：`cmd portrait-fold --arg letters_root=<root> --arg persona="
                                      + aPersona + " --arg target=<上面那位> --arg wake_range=<折的時點區間>"
                                      + " --arg-file body=<親筆>`");
                    aResult.Lines.Add("⚠ **一幅也折**（Tim 2026-09-01 拍板）—— 這份清單清空才算折完，"
                                      + "不是「我覺得重要的都折了」。");
                }
                aResult.AddValue("pending_targets", aRows.Count.ToString());
                aResult.AddValue("pending_portraits", aTotalPortraits.ToString());
                return aResult;
            }


            if (aTarget.Length > 0)
            {
                aTargets = new List<string> { aTarget };
            }
            else if (aOnline)
            {
                SCP_PersonaScan aScan = SCP_PersonaLetters.Scan(aLettersRoot);
                aTargets = new List<string>();
                foreach (SCP_PersonaStatus aStatus in aScan.Personas)
                {
                    if (aStatus.Online != SCP_PersonaOnline.Online) continue;
                    if (string.Equals(aStatus.Name, aPersona, StringComparison.OrdinalIgnoreCase)) continue;
                    aTargets.Add(aStatus.Name);
                }

                // ⚠ 這裡是本 Cmd 最容易說謊的一格：空清單有兩種來源，分開講。
                if (aTargets.Count == 0)
                {
                    int aUnknown = aScan.UnknownCount;
                    if (aScan.Problems.Count > 0 || aUnknown > 0)
                    {
                        aResult.Lines.Add("？ **在線清單量不到**（不是「沒人在線」）：");
                        foreach (string aProblem in aScan.Problems) aResult.Lines.Add("  · " + aProblem);
                        // ⚠ 只說「未知」不替它填原因：`_session` 整個不見與「lock 檔在但讀不了」
                        //   都會落到 Unknown，而本 Cmd 分不出是哪一種（分得出的是 Scan 的 Problems）。
                        //   替它猜一個原因就是我自己在製造一句沒有讀數的話。
                        if (aUnknown > 0)
                            aResult.Lines.Add("  · 狀態未知：" + aUnknown + " 人（未知 ≠ 離線）");
                        aResult.AddValue("online_targets", "unknown");
                        return aResult;
                    }
                    aResult.Lines.Add("・目前沒有其他人在線（`_session` 讀得到，掃到 "
                                      + aScan.Personas.Count + " 人、在線 " + aScan.OnlineCount + " 人）");
                    aResult.AddValue("online_targets", "0");
                    return aResult;
                }
            }
            else if (aAll)
            {
                aTargets = SCP_PortraitView.Targets(aLettersRoot, aPersona);
                if (aTargets.Count == 0)
                {
                    aResult.Lines.Add("・" + aPersona + " 還沒畫過任何人（sketchbook 根層與濃縮目錄都是空的）");
                    aResult.AddValue("target_count", "0");
                    return aResult;
                }
            }
            else
            {
                return SCP_CmdResult.Fail(2,
                    "✗ 要看誰？給 `target=<名字>`，或 `online=1`（在線同事）／`all=1`（全部畫過的人）／`pending=1`（還沒折完的清單）",
                    "  ⚠ 刻意不給預設值：預設列全部在人多的時候是一份沒人讀的長清單，"
                    + "而預設列在線在沒人在線時看起來像「查不到資料」。");
            }

            aResult.Lines.Add("# 🧑 見人 — " + aPersona + " 眼中的 " + (aTargets.Count > 1
                              ? "這 " + aTargets.Count + " 位" : aTargets[0]));
            aResult.Lines.Add("");

            int aWithConsolidated = 0;
            foreach (string aOne in aTargets)
            {
                SCP_PortraitTargetView aView = SCP_PortraitView.Build(aLettersRoot, aPersona, aOne);
                if (aView.Latest != null) aWithConsolidated++;
                aResult.Lines.AddRange(SCP_PortraitView.ViewLines(aView, aBodies));
                aResult.Lines.Add("");
            }

            aResult.AddValue("target_count", aTargets.Count.ToString());
            aResult.AddValue("with_consolidated", aWithConsolidated.ToString());
            return aResult;
        }
    }
}
