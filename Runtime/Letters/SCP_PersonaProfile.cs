// 區塊職責：persona 身分欄的**讀取本體**（共用層）—— Unity 與 senate.exe 走同一份。
// 物理意義：這一份是從 UCL_Core 的 `UCL_PersonaProfile` 收斂過來的，不是複製。
//           收斂的理由：LY 已掛 SCP_Core 且 Unity 真的編它 ⇒ **兩個宿主可以共用一份實作**，
//           而「一份實作」正是這次移植唯一要換到的東西（複製一份到 CLI 反而更糟）。
// 數值影響：純唯讀。`profile/` 不存在 ⇒ 回 null（＝查無此人，與 Exists 同一套判準）。
//
// ⚠ **不做的三件事**（少做是選擇，不是遺漏）：
//   ① 不寫任何檔（owned 欄寫入、審計、快照仍在 UCL 端 —— 讀寫分工，不是兩份實作）
//   ② 不做 lazy migration（實測 2026-08-31 本 repo 全庫 `_field_sources` ＝
//      profile 178／absent 53／**legacy 0** ⇒ 那條路是死碼。⚠ 別台的 clone 若還沒遷完會不一樣，
//      所以 `SrcLegacy` 常數與那條分支的**位置**保留，只是不搬「自動遷移的寫入」）
//   ③ 不碰帳本／餘額／綁定寫入 —— 只讀 `bank/<region>.md`（Tim 2026-08-31 拍板：
//      **讀綁定不是動錢，寫綁定是**。08-17 那筆 453 vs 1330 是餘額查到另一棵資料樹，不是讀綁定）
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。JSON 一律走 SCP_Json。
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    public static class SCP_PersonaProfile
    {
        // ── 欄位分類（與 python `_lib/persona_profile.py`／UCL 端同名常數是**兩端同步義務**）──

        public static readonly string[] RoutingFields = { "agent", "model", "actual_agent" };

        public static readonly string[] IdentityFields =
        {
            "layer_role", "forked_from", "fork_lineage", "forked_at", "created_at",
            "identity_vector", "vector_history", "email", "plurk_account", "model", "actual_agent",
        };

        /// <summary>結構值欄（檔案內文＝JSON）。**本表是型別判準的唯一真相源**，不准在對側另立一張。</summary>
        public static readonly string[] StructuredFieldsOrder =
            { "identity_vector", "vector_history", "fork_lineage" };

        static readonly HashSet<string> s_Structured = new HashSet<string>(StructuredFieldsOrder);

        /// <summary>可為 null 的純量欄：**空檔＝null**（不是空字串）。</summary>
        static readonly HashSet<string> s_NullableScalar =
            new HashSet<string> { "forked_from", "forked_at" };

        public const string FieldSourcesKey = "_field_sources";
        public const string SrcProfile = "profile";
        /// <summary>⚠ 本 repo 已無此來源（實測 0 筆），保留是給還沒遷完的別台 clone。</summary>
        public const string SrcLegacy = "legacy";
        public const string SrcAbsent = "absent";

        public const string BankSourceAbsent = "absent";
        /// <summary>多個其他區域都有值 —— **不挑一個**，回空並由呼叫端處置。</summary>
        public const string BankSourceAmbiguous = "ambiguous";

        /// <summary>`_` / `.` 前綴的目錄名不是人（機械產物／隱藏目錄）。Exists 與 PoolNames 共用。</summary>
        static bool IsReservedName(string iName)
            => iName.StartsWith("_", StringComparison.Ordinal)
               || iName.StartsWith(".", StringComparison.Ordinal);

        public static bool IsIdentityField(string iField)
        {
            if (string.IsNullOrEmpty(iField)) return false;
            foreach (string f in IdentityFields) if (f == iField) return true;
            return false;
        }

        // ── persona 名單與判準 ──────────────────────────────────────

        /// <summary>persona 的判準：<c>letters/&lt;p&gt;/profile/</c> 目錄存在。名字不是判準，資料才是。</summary>
        public static bool Exists(string iLettersRoot, string iPersona)
        {
            if (string.IsNullOrWhiteSpace(iPersona)) return false;
            // ⚠ `_` / `.` 前綴一律不是人 —— 與 PoolNames 同一套判準。
            //   兩個判準給不同答案是「同一個問題兩個真相源」的病理型（紅隊 seq 12274 洞②）。
            if (IsReservedName(iPersona)) return false;
            return Directory.Exists(
                SCP_LettersPaths.ProfileDir(new SCP_LettersRoot(iLettersRoot), iPersona));
        }

        /// <summary>
        /// pool 名單（有 <c>profile/</c> 的目錄）。
        /// <para>⚠ **空名單一定要讓呼叫端知道**：letters submodule 沒 init 時整個目錄是空的，
        /// 那時每個人都會安靜地從名單上消失（錢與登入都查不到他，而沒有一格會報錯）。
        /// 一個都掃不到幾乎不可能是真的。</para>
        /// </summary>
        public static List<string> PoolNames(string iLettersRoot, Action<string>? iWarn = null)
        {
            var aOut = new List<string>();
            if (!Directory.Exists(iLettersRoot))
            {
                iWarn?.Invoke("[PersonaProfile] 信件夾根不存在：" + iLettersRoot);
                return aOut;
            }
            string[] aDirs;
            try { aDirs = Directory.GetDirectories(iLettersRoot); }
            catch (Exception e)
            {
                iWarn?.Invoke("[PersonaProfile] 列不出信件夾底下的目錄：" + e.Message);
                return aOut;
            }
            foreach (string aDir in aDirs)
            {
                string aName = Path.GetFileName(aDir);
                if (IsReservedName(aName)) continue;
                if (Directory.Exists(Path.Combine(aDir, SCP_LettersPaths.ProfileDirName))) aOut.Add(aName);
            }
            aOut.Sort(StringComparer.Ordinal);
            if (aOut.Count == 0)
                iWarn?.Invoke("[PersonaProfile] pool 掃到 0 個 persona（" + iLettersRoot
                              + "）—— 幾乎不可能是真的，先確認 letters submodule 有沒有 init。");
            return aOut;
        }

        // ── 帳號綁定（**唯讀**）────────────────────────────────────

        /// <summary>
        /// 讀 persona 在指定區域使用的帳號（＝agent id）。找不到回空字串。
        /// <para>資料是 <c>letters/&lt;p&gt;/bank/&lt;region&gt;.md</c> 的內文 —— 一個純文字檔，
        /// **不是帳本**。</para>
        /// </summary>
        /// <param name="oSource">命中的區域 ID（本區或借用來源）／absent／ambiguous。
        /// ⚠ <c>oSource != iCurrencyId</c> 就代表這不是本區的宣告 —— 呼叫端必須讓它可見，
        /// 否則「本區真的綁了」與「借用別區的」在輸出上同形。</param>
        public static string GetBankAccount(string iLettersRoot, string iPersona, string iCurrencyId,
                                            out string oSource, out string oNote,
                                            Action<string>? iWarn = null)
        {
            oSource = BankSourceAbsent;
            oNote = "";
            if (string.IsNullOrWhiteSpace(iPersona) || string.IsNullOrWhiteSpace(iCurrencyId)) return "";

            var aRoot = new SCP_LettersRoot(iLettersRoot);
            string aBankDir = SCP_LettersPaths.PersonaDir(aRoot, iPersona) + "/bank";

            // ① 本區
            string aOwn = ReadBankFile(aBankDir + "/" + iCurrencyId + ".md");
            if (aOwn.Length > 0) { oSource = iCurrencyId; return aOwn; }

            // ② 其他區域（跨區借用）
            if (!Directory.Exists(aBankDir)) return "";
            string[] aFiles;
            try { aFiles = Directory.GetFiles(aBankDir, "*.md"); }
            catch (Exception e)
            {
                // 讀不到要出聲：靜默回空會把「讀取失敗」講成「沒有綁定」，
                // 而後者的處置是落央行 —— 一個看起來合理的處置掛在錯誤的原因上。
                iWarn?.Invoke("[PersonaProfile] 掃 bank/ 失敗（" + iPersona + "）：" + e.Message);
                return "";
            }
            var aHits = new List<KeyValuePair<string, string>>();
            foreach (string aFile in aFiles)
            {
                string aRegion = Path.GetFileNameWithoutExtension(aFile);
                if (string.Equals(aRegion, iCurrencyId, StringComparison.Ordinal)) continue;
                string v = ReadBankFile(aFile);
                if (v.Length > 0) aHits.Add(new KeyValuePair<string, string>(aRegion, v));
            }
            if (aHits.Count == 0) return "";
            if (aHits.Count > 1)
            {
                // 多個候選**不挑一個** —— 挑錯的那次，錢會進別人的帳戶而且沒有任何一層會喊。
                var aNames = new List<string>();
                foreach (var h in aHits) aNames.Add(h.Key + "=" + h.Value);
                oSource = BankSourceAmbiguous;
                oNote = "多區都有綁定，不挑：" + string.Join(" / ", aNames);
                return "";
            }
            oSource = aHits[0].Key;
            oNote = "本區（" + iCurrencyId + "）沒有宣告，借用 " + aHits[0].Key + " 的綁定";
            return aHits[0].Value;
        }

        public static bool HasOwnBankBinding(string iLettersRoot, string iPersona, string iCurrencyId)
        {
            var aRoot = new SCP_LettersRoot(iLettersRoot);
            return ReadBankFile(SCP_LettersPaths.PersonaDir(aRoot, iPersona)
                                + "/bank/" + iCurrencyId + ".md").Length > 0;
        }

        /// <summary>bank 檔的內文（去掉尾端換行）。讀不到 ⇒ 空字串。</summary>
        static string ReadBankFile(string iPath)
        {
            if (!File.Exists(iPath)) return "";
            try { return File.ReadAllText(iPath).Trim(); }
            catch (Exception) { return ""; }
        }

        // ── 合併讀取 ────────────────────────────────────────────────

        /// <summary>
        /// 整份 persona 資料 —— 推導欄 ＋ <c>profile/</c> 疊上去 ＋ <c>_field_sources</c>。
        /// 不存在回 null。
        /// </summary>
        /// <param name="iCurrencyId">本專案的央行區域 ID。**由宿主傳進來，本層不推導。**</param>
        public static SCP_JsonData? GetRaw(string iLettersRoot, string iPersona, string iCurrencyId,
                                           Action<string>? iWarn = null)
        {
            SCP_JsonData? aRaw = BuildRaw(iLettersRoot, iPersona, iCurrencyId, iWarn);
            if (aRaw == null) return null;
            return MergeProfile(iLettersRoot, iPersona, aRaw, iWarn);
        }

        /// <summary>
        /// 非 profile 欄的組裝 —— 真相源全部在 <c>letters/&lt;persona&gt;/</c>。
        /// <para>· <c>agent</c>（＝帳號 id）← <c>bank/&lt;region&gt;.md</c>
        /// · <c>status</c> / <c>last_active</c> ← lock
        /// · <c>wake_count</c> ← <c>wakes/</c> 信數 ∪ lock 的 <c>wake_expected</c>
        /// · <c>last_consolidated_*</c> ← <c>longterm/</c> 的最大 span_end</para>
        /// </summary>
        public static SCP_JsonData? BuildRaw(string iLettersRoot, string iPersona, string iCurrencyId,
                                             Action<string>? iWarn = null)
        {
            if (!Exists(iLettersRoot, iPersona)) return null;
            var aJd = SCP_JsonData.NewObject();

            string aAgent = GetBankAccount(iLettersRoot, iPersona, iCurrencyId,
                                           out string aBankSrc, out string aBankNote, iWarn);
            if (aAgent.Length > 0)
            {
                aJd.Set("agent", SCP_JsonData.NewString(aAgent));
                if (!string.Equals(aBankSrc, iCurrencyId, StringComparison.Ordinal))
                    iWarn?.Invoke("[PersonaProfile] " + iPersona + " 的 agent 借用了別區的綁定"
                                  + "（本區 " + iCurrencyId + " 沒有宣告，來源 " + aBankSrc + "）：" + aBankNote);
            }
            else
            {
                // 不填空字串頂替：下游拿到空 agent 會落央行，而那是一個看起來合理的處置
                // 掛在錯誤的原因上（真正的原因是「這個人沒有本區綁定」）。
                iWarn?.Invoke("[PersonaProfile] " + iPersona + " 在區域 " + iCurrencyId
                              + " 查無帳號綁定（bank/" + iCurrencyId + ".md 不存在）—— agent 欄留缺席。");
            }

            (bool aOnline, string aLockedAt, int aWakeExpected) = ReadLockFields(iLettersRoot, iPersona);
            aJd.Set("status", SCP_JsonData.NewString(aOnline ? "online" : "offline"));
            if (aOnline && aLockedAt.Length > 0)
                aJd.Set("last_active", SCP_JsonData.NewString(aLockedAt));

            // ⚠ 不可寫成「在線就 +1」：收尾信寫完之後信數已經追上期望，硬加 1 會讓顯示值多一歲，
            //   而 sleep 端的 letter 閘門正是拿這兩個數在對帳。
            int aLetters = SCP_Consolidate.WakeLetterCount(iLettersRoot, iPersona);
            aJd.Set("wake_count", SCP_JsonData.NewNumber(aWakeExpected > aLetters ? aWakeExpected : aLetters));

            (int aSpanEnd, string aAt) = SCP_Consolidate.LatestDigestSpan(iLettersRoot, iPersona);
            if (aSpanEnd > 0)
            {
                aJd.Set("last_consolidated_wake", SCP_JsonData.NewNumber(aSpanEnd));
                if (aAt.Length > 0) aJd.Set("last_consolidated_at", SCP_JsonData.NewString(aAt));
            }
            return aJd;
        }

        /// <summary>
        /// 把 <c>profile/</c> 疊到推導欄之上，補 <c>_field_sources</c>。**只讀不寫。**
        /// </summary>
        static SCP_JsonData MergeProfile(string iLettersRoot, string iPersona, SCP_JsonData ioRaw,
                                         Action<string>? iWarn)
        {
            var aSources = SCP_JsonData.NewObject();
            foreach (string f in IdentityFields)
            {
                if (TryReadProfileField(iLettersRoot, iPersona, f, out SCP_JsonData? aVal, iWarn))
                {
                    ioRaw.Set(f, aVal!);                        // profile/ 為準
                    aSources.Set(f, SCP_JsonData.NewString(SrcProfile));
                    continue;
                }
                // ⚠ 推導欄也沒有這個 key ⇒ **缺席**，不是「空值」。
                //   讓「沒有」自己有名字，不靠檔案不存在來暗示。
                //   （legacy 那條分支的位置就在這裡 —— 本 repo 已無此來源，見檔頭 ②。）
                aSources.Set(f, SCP_JsonData.NewString(
                    ioRaw.Contains(f) ? SrcLegacy : SrcAbsent));
            }
            ioRaw.Set(FieldSourcesKey, aSources);
            return ioRaw;
        }

        /// <summary>
        /// 讀一個 <c>profile/&lt;field&gt;.md</c>。檔不存在或壞掉回 false。
        /// <para>⚠ 壞掉**會警告** —— 靜默退回會讓「壞檔」跟「還沒有這一欄」同形。</para>
        /// <para>⚠ 型別由**欄名**決定，不由值決定：看值猜型別在讀回時分不出字串 "null" 與真的 null。</para>
        /// </summary>
        static bool TryReadProfileField(string iLettersRoot, string iPersona, string iField,
                                        out SCP_JsonData? oValue, Action<string>? iWarn)
        {
            oValue = null;
            string aPath = SCP_LettersPaths.ProfileDir(new SCP_LettersRoot(iLettersRoot), iPersona)
                           + "/" + iField + ".md";
            if (!File.Exists(aPath)) return false;

            string aText;
            try { aText = File.ReadAllText(aPath, Encoding.UTF8); }
            catch (Exception e)
            {
                iWarn?.Invoke("[PersonaProfile] profile/" + iField + ".md 讀取失敗（"
                              + iPersona + "）：" + e.Message);
                return false;
            }
            // ⚠ 寫檔一律補一個換行，讀回時 TrimEnd 掉 ⇒ **純量值尾端的換行不保留**。
            aText = aText.TrimEnd('\r', '\n');

            if (s_Structured.Contains(iField))
            {
                try
                {
                    SCP_JsonData aJd = SCP_JsonParser.Parse(aText);
                    oValue = aJd;
                    return true;
                }
                catch (Exception e)
                {
                    iWarn?.Invoke("[PersonaProfile] profile/" + iField + ".md（" + iPersona
                                  + "）JSON 解析失敗：" + e.Message + " —— 退回未覆蓋；請修那個檔");
                    return false;
                }
            }

            if (s_NullableScalar.Contains(iField) && aText.Length == 0)
            {
                oValue = SCP_JsonData.NewNull();                // 空檔 ＝ null（不是空字串）
                return true;
            }
            oValue = SCP_JsonData.NewString(aText);
            return true;
        }

        /// <summary>
        /// 讀 lock 的三個欄（在線／locked_at／wake_expected）。
        /// <para>⚠ 走 <see cref="SCP_PersonaLetters"/> 既有的 lock 解析 —— **不在這裡重造第二支**。
        /// 兩支解析器對同一顆 lock 給出不同答案時，不會有任何一層報錯。</para>
        /// </summary>
        static (bool Online, string LockedAt, int WakeExpected) ReadLockFields(
            string iLettersRoot, string iPersona)
        {
            SCP_PersonaStatus? aStatus = SCP_PersonaLetters.ReadPersonaLock(iLettersRoot, iPersona);
            if (aStatus == null || aStatus.Online != SCP_PersonaOnline.Online) return (false, "", 0);
            return (true, aStatus.LockedAt, aStatus.WakeExpected);
        }
    }
}
