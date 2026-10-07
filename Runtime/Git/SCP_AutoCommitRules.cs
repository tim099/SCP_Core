// 區塊職責：自動 commit 的**分群規則**（單一真相源）——「這個檔屬於哪一群、哪些檔永遠不收」。
// 物理意義：Senate 的自動 Commit 頁與 `senate cmd auto-commit` 共用這一份。
//          🩸 共用的理由不是「重複很醜」：這種規則的錯配等級是「檔進錯 commit」——
//            兩份規則漂掉的症狀是「同一個檔在頁面被分到 A 群、在 Cmd 被分到 B 群」，
//            而兩邊各自看起來都正常。
// 數值影響：純資料與純函式，不碰 IO ⇒ 可以被任何宿主、任何執行緒呼叫。
//          ⚠ 兩組寫死的規則（AgentCommands 本層 / persona 信件庫）是**專案慣例**，不由任何檔案覆寫；
//            其他 repo 走 `.ucl_autocommit.json` 自己宣告（見 SCP_AutoCommitConfig）。
//          ⚠ 地板由 **Classify 的判定順序**保證（subptr → ephemeral → 分群），
//            不是由「呼叫端記得檢查」保證 ⇒ 設定檔寫什麼前綴都碰不到 ephemeral 與特殊群。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
// @doc-sync: <SCP_Core>/Docs~/AutoCommit.md（兩組寫死分群的表）
#nullable enable
using System;

namespace SCP.Core.Git
{
    /// <summary>一群的規則。Match 吃「相對該 repo root 的正斜線路徑」。</summary>
    public sealed class SCP_AutoCommitGroupDef
    {
        public string Key = "";
        public string Label = "";
        /// <summary>命中判準。⚠ 刻意用前綴不用 regex —— 錯配是「檔進錯 commit」等級，規則要一眼能驗證。</summary>
        public Func<string, bool> Match = _ => false;
        /// <summary>commit 訊息主體（檔數統計由呼叫端補在後面）。</summary>
        public string Message = "";
        public bool DefaultOn;
    }

    /// <summary>自動 commit 的分群規則。頁面（人按）與 Cmd（agent 跑）共用。</summary>
    public static class SCP_AutoCommitRules
    {
        /// <summary>巢狀 submodule pointer —— **永不自動收**（bump 了別人會 pull 不到 hash）。</summary>
        public const string KeySubPtr = "__subptr";

        /// <summary>未分類（已追蹤）—— **永不自動收**（規則沒認出來 ≠ 機器生成）。</summary>
        public const string KeyOther = "__other";

        // 區塊職責：未分類**且從來沒進過版控**的檔 —— 從 KeyOther 拆出來（UCL 端 TASK-0129）。
        // 物理意義：「untracked」是一個**別人做過的決定** —— 那個檔沒有進版控，是有人選擇不放。
        //           替他翻案要顯式，而不是被一句 `groups=__other` 順手帶走。
        // 🩸 血證（@summit 2026-09-04）：`groups=__other` 一次收走 4 個機器檔
        //   ＋ **4 個 @calli／@kiara 的 untracked 交付單** ＋ 3 個有作者的 `.py`，
        //   而那筆的訊息寫著 `unclassified generated files` —— 三樣都不是機器生成的。
        public const string KeyOtherUntracked = "__other_untracked";

        /// <summary>三個特殊群 —— 永不自動收；只有顯式指定才收。</summary>
        public static readonly string[] NeverAutoKeys = { KeyOther, KeyOtherUntracked, KeySubPtr };

        /// <summary>這個 key 是不是保留給特殊群的（設定檔不准拿來當群名）。</summary>
        public static bool IsReservedKey(string iKey)
            => iKey == KeyOther || iKey == KeyOtherUntracked || iKey == KeySubPtr;

        // ── AgentCommands 本層 ──────────────────────────────────────────
        public static readonly SCP_AutoCommitGroupDef[] AgentGroupDefs =
        {
            new SCP_AutoCommitGroupDef
            {
                Key = "chat",
                Label = "酒館訊息（[chat] 獨立 commit — 硬規則）",
                // ⚠ `rooms_archive/`（TASK-0318 封存 ＝ 整個房間資料夾搬過去）要跟 `rooms/` 同一群：
                //   分兩群的話一次封存會拆成兩筆 commit（這邊刪、那邊加），git 就認不出那是一次搬家。
                Match = p => p.StartsWith("ChatTavern/rooms/", StringComparison.Ordinal)
                             || p.StartsWith("ChatTavern/rooms_archive/", StringComparison.Ordinal),
                Message = "[chat] sync tavern messages & inbox (auto)",
                DefaultOn = true,
            },
            new SCP_AutoCommitGroupDef
            {
                Key = "treasury",
                Label = "Treasury（帳本 / 帳戶）",
                Match = p => p.StartsWith("Treasury/", StringComparison.Ordinal),
                Message = "chore(treasury): sync ledger & account state (auto)",
                DefaultOn = true,
            },
            // ⚠ 新銀行（TASK-0216，2026-09-17 起住 `<資料根>/Bank`）**跟舊帳本分群**：
            //   同一筆錢在兩本帳上是兩個事件，合成一群的話 commit 訊息說不出動的是哪一本。
            // 🩸 遷移當天它不在任何一群裡 ⇒ 落進 `__other_untracked` ＝ **永遠不會被自動收**。
            new SCP_AutoCommitGroupDef
            {
                Key = "bank",
                Label = "Bank（新銀行：帳戶 / 分錄）",
                Match = p => p.StartsWith("Bank/", StringComparison.Ordinal),
                Message = "chore(bank): sync new-bank accounts & ledger (auto)",
                DefaultOn = true,
            },
            new SCP_AutoCommitGroupDef
            {
                Key = "runtime",
                Label = "Agent runtime state（cursor / bartender / persona / canvas…）",
                Match = p => p.StartsWith("ChatTavern/", StringComparison.Ordinal)
                             || p.StartsWith("AwakenInit/", StringComparison.Ordinal)
                             || p.StartsWith("Canvas/", StringComparison.Ordinal)
                             || p.StartsWith("Inbox/", StringComparison.Ordinal),
                Message = "chore(runtime): sync agent runtime state (auto)",
                DefaultOn = true,
            },
            new SCP_AutoCommitGroupDef
            {
                Key = "queue_state",
                Label = "PromptQueue 狀態（daemon 游標 —— 不含該目錄下的原始碼）",
                // ⚠ 判準刻意**不是目錄前綴**（`PromptQueue/`）——那底下住著一票 tracked 的 .py，
                //   前綴會把**有作者的產出**當成機器狀態自動收走，而那種錯不會當場叫。
                // ⇒ 只收頂層的 `_*_state.json`（daemon 自己寫的游標），子目錄一律不碰。
                Match = p => p.StartsWith("PromptQueue/", StringComparison.Ordinal)
                             && p.EndsWith("_state.json", StringComparison.Ordinal)
                             && p.IndexOf('/', "PromptQueue/".Length) < 0,
                Message = "chore(queue): sync prompt queue state (auto)",
                DefaultOn = true,
            },
            // `Lessons/` —— 跨 agent lesson 庫（TASK-0117）：`lessons.jsonl` 是 Cmd append 的機器檔，
            // 內容是誰的教訓寫在**欄位裡**（actor），跟酒館訊息同型（有 sender 欄的機器檔）。
            // 🩸 它原本落 `__other` ⇒ 每個人每天要繞一次的例外手勢，忘記的症狀是工作區靜默累積。
            new SCP_AutoCommitGroupDef
            {
                Key = "lessons",
                Label = "Lessons（跨 agent lesson 庫：jsonl ＋ 它的視圖）",
                Match = p => p.StartsWith("Lessons/", StringComparison.Ordinal),
                Message = "chore(lessons): sync lesson log (auto)",
                DefaultOn = true,
            },
            // `Plurk/post_audit.jsonl` —— 對外發文的 append-only 稽核帳（不存內文）⇒ 純機器帳。
            // ⚠ 判準刻意**不是** `Plurk/` 前綴：那底下住著同事親筆的交付單。
            new SCP_AutoCommitGroupDef
            {
                Key = "plurk_audit",
                Label = "Plurk 發文稽核帳（post_audit.jsonl —— ⛔ 不含同目錄的親筆交付單）",
                Match = p => p == "Plurk/post_audit.jsonl",
                Message = "chore(plurk): sync post audit ledger (auto)",
                DefaultOn = true,
            },
        };

        // ── persona 信件庫（letters/<persona>/，各自一個 repo）───────────
        // 區塊職責：persona 信件庫的分群規則
        // 物理意義：這裡的分界不是「檔案類型」，是**作者是誰** ——
        //          投遞件（別人寫的、系統寫的）與機械維護檔可以自動收；
        //          她自己寫的一律落到未分類（永不自動收），留給她自己的收尾 commit。
        // ⚠ TASK-0340（Tim 2026-09-30）拿掉了「在線 persona 預設不勾」那道守衛：
        //   本表收的群都是**機器獨佔**的檔（「自動 commit 管理的部分不應該手動 commit」），
        //   不存在「她正在寫、會被順手帶走」的情形。⇒ 那道守衛擋的從來是親筆檔，
        //   而親筆檔本來就不在本表上 —— 分界靠的是本表，不是在不在線。
        // ⚠ **新增一種 letters 產物就必須同時改這裡**，否則它落 `__other` 永遠不會進版控而且不會叫
        //   （`writing/` 2026-08-23、`vouchers/` 2026-09-22 兩次血證）。
        public static readonly SCP_AutoCommitGroupDef[] PersonaGroupDefs =
        {
            // `outbox/` 是掛號信的寄件存證，跟 `mailbox/` 同一個通道的兩端 —— 不是「她寫的信」。
            new SCP_AutoCommitGroupDef
            {
                Key = "mailbox",
                Label = "信件通道（mailbox/ 系統信與掛號信投遞、outbox/ 寄件存證）",
                Match = p => p.StartsWith("mailbox/", StringComparison.Ordinal)
                             || p.StartsWith("outbox/", StringComparison.Ordinal),
                Message = "[mailbox] 收信件通道檔（系統信／投遞／存證）(auto)",
                DefaultOn = true,
            },
            new SCP_AutoCommitGroupDef
            {
                Key = "portraits",
                Label = "他人投遞的畫像（portraits/ — 作者是別人，我只是收件人）",
                Match = p => p.StartsWith("portraits/", StringComparison.Ordinal),
                Message = "[portraits] 收他人投遞的畫像 (auto)",
                DefaultOn = true,
            },
            // `profile/` —— 身分欄（一欄一檔）。內容可能是人改的（PersonaProfile op=set），
            // 但那是**設定**不是**作品** ⇒ 沒有「替她簽名」的問題。沒進版控 ＝ 「這個人是誰」只存在這台機器上。
            new SCP_AutoCommitGroupDef
            {
                Key = "profile",
                Label = "身分欄 profile/（一欄一檔）",
                Match = p => p.StartsWith("profile/", StringComparison.Ordinal),
                Message = "[data] 收 profile/ 身分欄（Phase 1 lazy migration 產物）(auto)",
                DefaultOn = true,
            },
            // `bank/` —— 銀行綁定（一區一檔）。**錢的歸屬住在這裡**：缺綁定的處置是落央行
            // ⇒ 一次磁碟意外的症狀不是報錯，是薪水靜默轉向。
            // ⚠ 會出現別的專案的檔（letters 被多專案掛著）—— 照收，⛔ 絕不因為「不認識這個區域」而排除。
            new SCP_AutoCommitGroupDef
            {
                Key = "bank",
                Label = "銀行綁定 bank/（區域 → 帳號；一區一檔）",
                Match = p => p.StartsWith("bank/", StringComparison.Ordinal),
                Message = "[data] 收 bank/ 銀行綁定（區域 → 帳號）(auto)",
                DefaultOn = true,
            },
            // `vouchers/` —— 券簿（唯一寫入端 SCP_VoucherStore）。比 `bank/` 更急：**券刻意不記歷史**
            // （TASK-0243）⇒ 券簿丟了就是真的沒了，而畫面上跟「大家本來就沒有券」同形。
            // 🩸 2026-09-22 TASK-0270 第一次實發寫了 20 個 persona 的 `vouchers/BTC.json`，全部落 `__other_untracked`。
            new SCP_AutoCommitGroupDef
            {
                Key = "vouchers",
                Label = "券簿 vouchers/（一券一檔；唯一寫入端 SCP_VoucherStore —— ⚠ 券不記歷史，丟了補不回來）",
                Match = p => p.StartsWith("vouchers/", StringComparison.Ordinal),
                Message = "[data] 收 vouchers/ 券簿（餘額與零頭池；券不記歷史）(auto)",
                DefaultOn = true,
            },
            // `portfolio/` —— 投資組合帳（開帳快照＋交易事件；唯一寫入端 SCP_Portfolio）。券簿不記歷史，
            // 成本來源只住這裡 ⇒ 丟了報酬率就算不出來，而畫面上只會變成「與紀錄不符」。
            new SCP_AutoCommitGroupDef
            {
                Key = "portfolio",
                Label = "投資組合帳 portfolio/（開帳快照＋交易事件；唯一寫入端 SCP_Portfolio）",
                Match = p => p.StartsWith("portfolio/", StringComparison.Ordinal),
                Message = "[data] 收 portfolio/ 投資組合帳（開帳快照＋交易事件）(auto)",
                DefaultOn = true,
            },
            // `bookshelf/` —— 閱讀卡，由 `reader.json` 重新生成的機械投影（親筆住 Library 的原檔）。
            new SCP_AutoCommitGroupDef
            {
                Key = "bookshelf",
                Label = "閱讀卡 bookshelf/（由 reader.json 生成的機械投影）",
                Match = p => p.StartsWith("bookshelf/", StringComparison.Ordinal),
                Message = "[data] 收 bookshelf/ 閱讀卡（reader.json 的機械投影）(auto)",
                DefaultOn = true,
            },
            // `writing/` —— 書的續寫包（publish 時重生成的投影；作者親筆住 BookNotes）。
            new SCP_AutoCommitGroupDef
            {
                Key = "writing_dossier",
                Label = "書的續寫包 writing/（publish 自動投遞的機械投影）",
                Match = p => p.StartsWith("writing/", StringComparison.Ordinal),
                Message = "[data] 收 writing/ 續寫包（publish 投遞的機械投影）(auto)",
                DefaultOn = true,
            },
            // `sketchbook/<target>/raw/` —— 見人濃縮的**歸檔半**（只搬不刪，內容未變）。
            // ⚠ 濃縮檔本身（`<target>/*_vNNN.md`）刻意不在這裡：那是她親筆寫的判斷。
            //   ⇒ 判準要同時吃「在 sketchbook 底下」與「在某個 raw/ 子目錄裡」。
            new SCP_AutoCommitGroupDef
            {
                Key = "sketchbook_raw",
                Label = "見人濃縮的歸檔畫像 sketchbook/<target>/raw/（只搬不刪，內容未變）",
                Match = p => p.StartsWith("sketchbook/", StringComparison.Ordinal)
                             && p.IndexOf("/raw/", StringComparison.Ordinal) >= 0,
                Message = "[data] 收 sketchbook/<target>/raw/ 歸檔畫像（濃縮時搬入，內容未變）(auto)",
                DefaultOn = true,
            },
            // `relationship/<target>/` —— 好感度的**機器算出來那半**（events/ ＋ _current.md）。
            // ⛔ `opinions/` 刻意不在這一群：那是她親筆的看法 ⇒ 單看前綴會把兩種作者混成一筆。
            new SCP_AutoCommitGroupDef
            {
                Key = "relationship",
                Label = "好感度事件與當前值 relationship/（events/ ＋ _current.md；⛔ 不含親筆的 opinions/）",
                Match = p => p.StartsWith("relationship/", StringComparison.Ordinal)
                             && p.IndexOf("/opinions/", StringComparison.Ordinal) < 0,
                Message = "[data] 收 relationship/ 事件帳與重算值（⛔ 不含親筆 opinions）(auto)",
                DefaultOn = true,
            },
            // `_keys_open.md` —— 見叢。追加與勾銷**只有一個寫入端**（`senate cmd keys`），整份由 Cmd 重寫
            // ⇒ 分界是「這個檔誰在寫」，不是「這個內容誰想的」。
            new SCP_AutoCommitGroupDef
            {
                Key = "keys",
                Label = "見叢 _keys_open.md（追加／勾銷都走 senate cmd keys，整份由 Cmd 重寫）",
                Match = p => p == "_keys_open.md",
                Message = "[data] 收見叢 _keys_open.md（當期交棒清單）(auto)",
                DefaultOn = true,
            },
            new SCP_AutoCommitGroupDef
            {
                Key = "letters_mech",
                Label = "機械維護檔（_latest.md 指標 / cmd/.gitignore）",
                Match = p => p == "_latest.md" || p == "cmd/.gitignore",
                Message = "[data] 同步機械維護檔（指標／目錄 ignore）(auto)",
                DefaultOn = true,
            },
        };

        /// <summary>
        /// ephemeral —— 永遠不進候選（執行期瞬時檔：log / wait 旗標 / 臨時渲染 / DebugLogs / queue 瞬時檔）。
        /// </summary>
        public static bool IsEphemeral(string iPath)
        {
            string aName = iPath;
            int aSlash = iPath.LastIndexOf('/');
            if (aSlash >= 0) aName = iPath.Substring(aSlash + 1);
            if (aName.EndsWith(".log", StringComparison.Ordinal) || aName.EndsWith(".tmp", StringComparison.Ordinal)) return true;
            if (aName == "_last_op.md" || aName == "_last_view.md"
                || aName == "_active_waits.json" || aName == "pending.trigger") return true;
            if (aName.StartsWith("_wait_", StringComparison.Ordinal)) return true;
            if (iPath.StartsWith("DebugLogs/", StringComparison.Ordinal)
                || iPath.IndexOf("/DebugLogs/", StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        /// <summary>
        /// 一個路徑該進哪一群。回 null ＝ ephemeral（不進候選）。
        /// <para>⚠ 判定順序是**地板**：subptr → ephemeral → 分群 → 未分類。
        /// 設定檔宣告的前綴排在 ephemeral 之後，所以碰不到它。</para>
        /// </summary>
        public static string? Classify(string iPath, SCP_AutoCommitGroupDef[]? iDefs, bool iIsSubPointer)
        {
            if (iIsSubPointer) return KeySubPtr;
            if (IsEphemeral(iPath)) return null;
            if (iDefs != null)
            {
                foreach (var aDef in iDefs)
                    if (aDef.Match(iPath)) return aDef.Key;
            }
            return KeyOther;
        }

        /// <summary>
        /// 一群的 commit 訊息（尾巴補 `[N files]`）。
        /// <para>⚠ 未分類刻意**不寫 `generated`**：`__other` 的定義是「規則沒認出來」，不等於「機器生成的」。
        /// 一句宣稱它不知道的事的 commit 訊息，比沒有訊息貴 —— 它會讓讀 log 的人不再去看那些檔。</para>
        /// </summary>
        public static string MessageOf(string iKey, SCP_AutoCommitGroupDef[] iDefs, int iCount)
        {
            if (iKey == KeySubPtr)
                return $"chore(submodule): bump nested submodule pointers (auto) [{iCount} files]";
            if (iKey == KeyOther)
                return $"chore: sync unclassified files (auto) [{iCount} files]";
            if (iKey == KeyOtherUntracked)
                return $"chore: add unclassified untracked files (auto) [{iCount} files]";
            foreach (var aDef in iDefs)
                if (aDef.Key == iKey) return $"{aDef.Message} [{iCount} files]";
            return $"chore: sync {iKey} (auto) [{iCount} files]";
        }

        /// <summary>特殊群的顯示名（畫面用）。</summary>
        public static string LabelOfSpecial(string iKey)
        {
            if (iKey == KeySubPtr) return "巢狀 submodule pointer／未登記的巢狀 repo（⚠ 指向別人的 repo —— 確認對方已 push 再勾）";
            if (iKey == KeyOther) return "未分類（規則沒認出來的已追蹤檔 —— 可能是有作者的產出，逐一看過再勾）";
            if (iKey == KeyOtherUntracked) return "未分類且從未進版控（untracked —— 有人選擇不放進版控，替他翻案要顯式）";
            return iKey;
        }
    }
}
