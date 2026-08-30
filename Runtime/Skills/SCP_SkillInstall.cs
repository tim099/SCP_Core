// 區塊職責：把 `Skills~/<name>/` 鏡像到某個 target 的安裝目錄，並回報狀態與孤兒。
// 物理意義：安裝端是**純鏡像**（不是手改的副本）—— 所以同步就是「內容不同就覆蓋、源端沒有的就刪」。
//           這跟入口檔（SCP_EntryDoc）刻意不同：那邊是使用者的檔，這邊寫壞了重裝就好。
//           判準寫在這裡免得下一個人把兩套規矩搞混。
// 數值影響：一次同步 ＝ 逐檔比對 bytes（不算 hash）＋ 只寫不同的那些 ＋ 掃孤兒 ＋ 寫標記檔。
//
// 🩸 行尾**跟隨源檔**（不寫死）。UCL 那支 python 在這一族踩了三次，其中兩次是修法本身造成的：
//   ① 寫入端沒帶 newline ⇒ 行尾由執行環境決定
//   ② 修法寫死 `\n` ⇒ 製造了反方向的漂移（源檔是 CRLF 的那批，安裝端變 LF）
//   ③ up-to-date 比對用 ReadAllText（會把 CRLF 翻回 LF）⇒ **檢查看不見自己造成的差異**，
//      於是壞掉的那份永遠被跳過、永遠修不好
//   ⇒ 這裡一律走 bytes：比對與寫入是同一份位元組。
//
// ⚠ 兩套安裝器並存期（UCL python 的 `.ucl_source` 與本層的 `.scp_source`）：
//   孤兒判定要認得**兩種**標記，否則兩邊會互相把對方裝的東西當成「非受管」。
//   現在做只要一個常數，取代期才做要清現場。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SCP.Core.Json;

namespace SCP.Core.Skills
{
    /// <summary>一個 skill 在某個 target 上的狀態。⚠ 每一態都要有畫面上的字。</summary>
    public enum SCP_SkillState
    {
        /// <summary>安裝目錄沒有這個 skill。</summary>
        NotInstalled = 0,

        /// <summary>逐檔內容與源端相同。</summary>
        Synced = 1,

        /// <summary>有差（源端更新了，或安裝端被動過）—— 鏡像語意下兩者處置相同：覆蓋。</summary>
        Stale = 2,

        /// <summary>裝了但**源端已經沒有這個 skill** —— 殘留，可移除。</summary>
        Orphan = 3,

        /// <summary>安裝目錄裡有、源端沒有、**而且沒有任何安裝標記** ⇒ 使用者自己放的，不動它。</summary>
        Unmanaged = 4,

        /// <summary>
        /// **別套安裝器裝的**（帶 `.ucl_source`）—— 不是我的殘留，不給移除鈕。
        /// <para>🩸 2026-08-30 第一版把它併進 <see cref="Orphan"/>：頁面第一次跑就把 Bar 底下
        /// UCL 裝的每一個 skill 都標成「殘留．可移除」並附一顆刪除鈕。
        /// 那不是顯示錯誤，是**一顆會刪掉別套系統資產的按鈕**。
        /// ⇒ 標記的用途是「誰裝的」，不是「有沒有被裝過」。</para>
        /// </summary>
        Foreign = 5,
    }

    public sealed class SCP_SkillStatus
    {
        public string Name { get; internal set; } = "";
        public SCP_SkillState State { get; internal set; }

        /// <summary>與源端不同的檔數（Stale 時有意義）。</summary>
        public int DiffFiles { get; internal set; }

        /// <summary>人可讀的說明。</summary>
        public string Detail { get; internal set; } = "";
    }

    public sealed class SCP_SkillSyncResult
    {
        public bool Ok { get; internal set; }
        public int Copied { get; internal set; }
        public int RemovedOrphanFiles { get; internal set; }
        public string Message { get; internal set; } = "";
    }

    public static class SCP_SkillInstall
    {
        /// <summary>UCL python 安裝器的標記檔名 —— 孤兒判定要認得它，否則兩套互相當對方是野生的。</summary>
        public const string LegacyMarkerFileName = ".ucl_source";

        // ── 狀態 ──────────────────────────────────────────────────

        /// <summary>某個 target 的全部狀態（源端的 ∪ 已裝端的）。</summary>
        public static List<SCP_SkillStatus> Status(string iSkillsRoot, SCP_SkillTarget iTarget, string iProjectRoot)
        {
            var aOut = new List<SCP_SkillStatus>();
            List<string> aSource = SCP_SkillSource.Discover(iSkillsRoot);
            string aInstallRoot = iTarget.SkillsDir(iProjectRoot);

            foreach (string aName in aSource)
                aOut.Add(One(Path.Combine(iSkillsRoot, aName), Path.Combine(aInstallRoot, aName), aName));

            // 已裝端有、源端沒有 ⇒ Orphan / Unmanaged。
            // 📌 這一段是刻意的：以源端為基準逐一列出的清單，**結構上不可能**出現這種目錄，
            //    而 agent 載入 skill 時只掃安裝目錄、不看標記 ⇒ 它仍然會被吃進 context。
            //    **看不見 ＋ 仍生效 ＝ 靜默僵屍。**
            if (Directory.Exists(aInstallRoot))
            {
                var aKnown = new HashSet<string>(aSource, StringComparer.OrdinalIgnoreCase);
                string[] aDirs;
                try { aDirs = Directory.GetDirectories(aInstallRoot); } catch { aDirs = Array.Empty<string>(); }
                foreach (string aDir in aDirs)
                {
                    string aName = Path.GetFileName(aDir);
                    if (aKnown.Contains(aName)) continue;
                    // 三分：**我的**殘留／**別套**裝的／沒人認領。
                    // ⚠ 併成兩類的代價不是難看，是「移除殘留」那顆鈕會刪掉別套系統的資產。
                    bool aMine = File.Exists(Path.Combine(aDir, SCP_SkillSource.MarkerFileName));
                    bool aForeign = !aMine && File.Exists(Path.Combine(aDir, LegacyMarkerFileName));
                    aOut.Add(new SCP_SkillStatus
                    {
                        Name = aName,
                        State = aMine ? SCP_SkillState.Orphan
                              : aForeign ? SCP_SkillState.Foreign
                              : SCP_SkillState.Unmanaged,
                        Detail = aMine
                            ? "本工具裝的，但源端已經沒有它了 —— agent 仍然會載入它"
                            : aForeign
                            ? "帶 `.ucl_source` ⇒ **UCL 那套安裝器裝的**，不是本工具的殘留（本頁不動它）"
                            : "沒有任何安裝標記 ⇒ 視為使用者自己放的，自動流程不碰它（但**顯示**它：不顯示比不刪更糟）",
                    });
                }
            }

            aOut.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
            return aOut;
        }

        static SCP_SkillStatus One(string iSrcDir, string iDstDir, string iName)
        {
            var aOut = new SCP_SkillStatus { Name = iName };
            if (!Directory.Exists(iDstDir))
            {
                aOut.State = SCP_SkillState.NotInstalled;
                aOut.Detail = "還沒安裝";
                return aOut;
            }

            int aDiff = 0;
            foreach (string aSrc in EnumerateFiles(iSrcDir))
            {
                string aRel = Rel(iSrcDir, aSrc);
                string aDst = Path.Combine(iDstDir, aRel);
                if (!File.Exists(aDst) || !SameBytes(aSrc, aDst)) aDiff++;
            }
            // 已裝端多出來的檔也算差異（源端刪掉的殘檔）—— 標記檔本身不算
            foreach (string aDst in EnumerateFiles(iDstDir))
            {
                string aRel = Rel(iDstDir, aDst);
                if (aRel == SCP_SkillSource.MarkerFileName || aRel == LegacyMarkerFileName) continue;
                if (!File.Exists(Path.Combine(iSrcDir, aRel))) aDiff++;
            }

            aOut.DiffFiles = aDiff;
            aOut.State = aDiff == 0 ? SCP_SkillState.Synced : SCP_SkillState.Stale;
            aOut.Detail = aDiff == 0 ? "逐檔內容相同" : $"{aDiff} 個檔與源端不同（安裝端是純鏡像，同步會覆蓋）";
            return aOut;
        }

        // ── 同步 ──────────────────────────────────────────────────

        /// <summary>把一個 skill 鏡像過去（覆蓋不同的檔、刪掉源端沒有的檔、寫標記）。</summary>
        public static SCP_SkillSyncResult Sync(string iSkillsRoot, SCP_SkillTarget iTarget,
                                               string iProjectRoot, string iSkill)
        {
            var aRes = new SCP_SkillSyncResult();
            string aSrcDir = Path.Combine(iSkillsRoot, iSkill);
            if (!Directory.Exists(aSrcDir) || !File.Exists(Path.Combine(aSrcDir, SCP_SkillSource.SkillFileName)))
            {
                aRes.Message = $"源端沒有 `{iSkill}`（或缺 {SCP_SkillSource.SkillFileName}）—— 沒有動任何檔";
                return aRes;
            }
            string aDstDir = iTarget.SkillDir(iProjectRoot, iSkill);

            try
            {
                Directory.CreateDirectory(aDstDir);
                var aSrcRel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (string aSrc in EnumerateFiles(aSrcDir))
                {
                    string aRel = Rel(aSrcDir, aSrc);
                    aSrcRel.Add(aRel);
                    string aDst = Path.Combine(aDstDir, aRel);
                    if (File.Exists(aDst) && SameBytes(aSrc, aDst)) continue;   // 已經一樣就不寫

                    string? aDir = Path.GetDirectoryName(aDst);
                    if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir!);
                    // ⚠ 走 bytes：行尾跟隨源檔（見檔頭三筆血證）
                    File.WriteAllBytes(aDst, File.ReadAllBytes(aSrc));
                    aRes.Copied++;
                }

                // 孤兒檔（源端刪掉／改名後的殘留）—— 標記檔留著
                foreach (string aDst in EnumerateFiles(aDstDir))
                {
                    string aRel = Rel(aDstDir, aDst);
                    if (aRel == SCP_SkillSource.MarkerFileName || aRel == LegacyMarkerFileName) continue;
                    if (aSrcRel.Contains(aRel)) continue;
                    File.Delete(aDst);
                    aRes.RemovedOrphanFiles++;
                }

                WriteMarker(aDstDir, iSkill, iTarget);
            }
            catch (Exception e)
            {
                aRes.Message = $"同步 `{iSkill}` 失敗：{e.GetType().Name}: {e.Message}";
                return aRes;
            }

            aRes.Ok = true;
            aRes.Message = aRes.Copied == 0 && aRes.RemovedOrphanFiles == 0
                ? $"`{iSkill}` 本來就是最新的（沒有動檔案）"
                : $"✓ `{iSkill}`：寫入 {aRes.Copied} 檔／清掉殘留 {aRes.RemovedOrphanFiles} 檔";
            return aRes;
        }

        /// <summary>
        /// 移除一個已裝的 skill。
        /// <para>⚠ 沒有安裝標記的目錄**預設不刪** —— 那可能是使用者自己放的，而刪除不可逆。
        /// <paramref name="iAllowUnmanaged"/> 是呼叫端「我已經讓人看見並確認過這一筆」的顯式放行。</para>
        /// </summary>
        public static SCP_SkillSyncResult Remove(SCP_SkillTarget iTarget, string iProjectRoot,
                                                 string iSkill, bool iAllowUnmanaged = false)
        {
            var aRes = new SCP_SkillSyncResult();
            string aDir = iTarget.SkillDir(iProjectRoot, iSkill);
            if (!Directory.Exists(aDir)) { aRes.Ok = true; aRes.Message = $"`{iSkill}` 本來就不在（沒有動任何檔）"; return aRes; }

            bool aMarked = File.Exists(Path.Combine(aDir, SCP_SkillSource.MarkerFileName));
            if (!aMarked && File.Exists(Path.Combine(aDir, LegacyMarkerFileName)))
            {
                // ⛔ 別套安裝器的資產，本工具一律不刪（連顯式放行都不接受）——
                //    要移除請走那一套自己的入口，否則它那邊的狀態會跟磁碟脫鉤。
                aRes.Message = $"`{iSkill}` 是 UCL 那套安裝器裝的（有 .ucl_source）—— 本工具不動它。";
                return aRes;
            }
            if (!aMarked && !iAllowUnmanaged)
            {
                aRes.Message = $"`{iSkill}` 沒有安裝標記（可能是你自己放的）—— 沒有刪。"
                               + "確定要刪要顯式放行。";
                return aRes;
            }

            try { Directory.Delete(aDir, true); }
            catch (Exception e) { aRes.Message = $"刪不掉 `{iSkill}`：{e.GetType().Name}: {e.Message}"; return aRes; }

            aRes.Ok = true;
            aRes.Message = $"✓ 已移除 `{iSkill}`" + (aMarked ? "" : "（⚠ 無標記，是顯式放行才刪的）");
            return aRes;
        }

        // ── 小工具 ────────────────────────────────────────────────

        static void WriteMarker(string iDstDir, string iSkill, SCP_SkillTarget iTarget)
        {
            SCP_JsonData aData = SCP_JsonData.NewObject();
            aData.Set("source", "Skills~/" + iSkill);
            aData.Set("target", iTarget.Id);
            // ⚠ 刻意不記 commit／時間戳：那些會隨 churn 變，讓純鏡像每天長出假 diff。
            //   provenance 讓另一套安裝器認得「這是誰裝的」而不是把它當野生目錄。
            aData.Set("provenance", "scp_core");
            File.WriteAllText(Path.Combine(iDstDir, SCP_SkillSource.MarkerFileName),
                              aData.ToJson(iIndented: true) + "\n", new UTF8Encoding(false));
        }

        static IEnumerable<string> EnumerateFiles(string iDir)
        {
            if (!Directory.Exists(iDir)) yield break;
            string[] aAll;
            try { aAll = Directory.GetFiles(iDir, "*", SearchOption.AllDirectories); }
            catch { yield break; }
            foreach (string f in aAll) yield return f;
        }

        static string Rel(string iRoot, string iPath)
            => iPath.Substring(iRoot.Length).TrimStart('/', '\\').Replace('\\', '/');

        static bool SameBytes(string iA, string iB)
        {
            try
            {
                var a = new FileInfo(iA);
                var b = new FileInfo(iB);
                if (a.Length != b.Length) return false;
                byte[] ba = File.ReadAllBytes(iA);
                byte[] bb = File.ReadAllBytes(iB);
                for (int i = 0; i < ba.Length; i++) if (ba[i] != bb[i]) return false;
                return true;
            }
            catch { return false; }
        }
    }
}
