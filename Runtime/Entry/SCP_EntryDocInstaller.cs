// 區塊職責：把受管區塊真的寫進磁碟上的入口檔（CLAUDE.md / AGENTS.md…）。
// 物理意義：解析與合成是純函式（SCP_EntryDoc）；本檔只做「讀檔 → 判定 → 決定要不要寫 → 原子寫入 → 回讀」。
//           ⚠ 這是本 repo 裡**唯一**會動使用者手寫檔案的地方。skill 的安裝目錄是純鏡像
//           （寫壞了重裝就好），入口檔不是：**使用者區在源端沒有副本**。
//           ⇒ 多兩道護欄，而且兩道都是這裡才需要的：
//             ① 第一次改動前落一份 `.scp_backup`（消費端不一定有 git）
//             ② 非 force 時，只有「安全的狀態」才寫（Stale / NotInstalled / NeedsMigration）；
//                LocalEdit / Duplicated / MarkerBroken **一律停手並說出來**
// 數值影響：一次安裝 ＝ 一次讀 ＋（最多）一次備份 ＋ 一次原子替換 ＋ 一次回讀。
// ⚠ 方言限制：C# 9 / netstandard2.1（`File.Move(src,dst,overwrite)` 在這裡不存在，見 §1.1）。
#nullable enable
using System;
using System.IO;
using System.Text;

namespace SCP.Core.Entry
{
    /// <summary>一次安裝的結果。⚠ 「沒寫」有兩種：不需要寫、與不敢寫 —— 兩者不得同形。</summary>
    public sealed class SCP_EntryInstallResult
    {
        public bool Ok { get; internal set; }

        /// <summary>真的動到磁碟了嗎。Ok 且 Changed=false ＝ 本來就是最新的。</summary>
        public bool Changed { get; internal set; }

        public SCP_EntryState StateBefore { get; internal set; }
        public string Message { get; internal set; } = "";

        /// <summary>有落備份時的路徑（null ＝ 沒有備份，通常是因為原本沒有檔）。</summary>
        public string? BackupPath { get; internal set; }
    }

    public static class SCP_EntryDocInstaller
    {
        /// <summary>備份檔的後綴 —— 固定一個，不加時間戳（時間戳會在專案裡長出一堆沒人敢刪的檔）。</summary>
        public const string BackupSuffix = ".scp_backup";

        /// <summary>看一眼現況（不寫）。<paramref name="iManaged"/> 是來源目前的受管內容。</summary>
        public static SCP_EntryParse Inspect(string iPath, string iManaged)
        {
            string? aExisting = null;
            if (File.Exists(iPath))
            {
                try { aExisting = File.ReadAllText(iPath, Encoding.UTF8); }
                catch (Exception e)
                {
                    var aBad = new SCP_EntryParse();
                    aBad.State = SCP_EntryState.MarkerBroken;   // 讀不了就不可以說「沒安裝」
                    aBad.Detail = $"讀不了 {Path.GetFileName(iPath)}：{e.GetType().Name}: {e.Message}";
                    return aBad;
                }
            }
            return SCP_EntryDoc.Parse(aExisting, iManaged);
        }

        /// <summary>
        /// 安裝／更新受管區塊。
        /// <para><paramref name="iForce"/> 只放行 <see cref="SCP_EntryState.LocalEdit"/> ——
        /// <c>Duplicated</c> / <c>MarkerBroken</c> **force 也不做**：那兩種是「我不知道該動哪裡」，
        /// 不是「我知道但怕你心疼」。硬做的結果是留下一個還在生效的區塊而畫面顯示成功。</para>
        /// </summary>
        public static SCP_EntryInstallResult Install(
            string iPath, string iManaged, string iTarget, string iSource, bool iForce = false)
        {
            var aRes = new SCP_EntryInstallResult();
            SCP_EntryParse aParse = Inspect(iPath, iManaged);
            aRes.StateBefore = aParse.State;

            switch (aParse.State)
            {
                case SCP_EntryState.Synced:
                    aRes.Ok = true; aRes.Changed = false;
                    aRes.Message = "已是最新，沒有動檔案。" + aParse.Detail;
                    return aRes;

                case SCP_EntryState.Duplicated:
                case SCP_EntryState.MarkerBroken:
                    aRes.Ok = false;
                    aRes.Message = "停手（force 也不做）：" + aParse.Detail;
                    return aRes;

                case SCP_EntryState.LocalEdit when !iForce:
                    aRes.Ok = false;
                    aRes.Message = "停手：" + aParse.Detail + "。確定要丟掉那些字才用強制覆寫。";
                    return aRes;
            }

            string? aExisting = File.Exists(iPath) ? SafeRead(iPath) : null;
            string aNext = SCP_EntryDoc.Apply(aExisting, iManaged, iTarget, iSource);

            if (aExisting != null && SCP_EntryDoc.Normalize(aExisting) == aNext)
            {
                aRes.Ok = true; aRes.Changed = false;
                aRes.Message = "內容逐字相同，沒有動檔案（避免製造假 diff）。";
                return aRes;
            }

            // ① 備份 —— 只在「本來就有檔」且還沒備份過時落一份。
            //    ⚠ 不覆寫既有備份：那份是「我們第一次碰它之前」的樣子，是最有價值的一份。
            if (aExisting != null)
            {
                string aBak = iPath + BackupSuffix;
                if (!File.Exists(aBak))
                {
                    try { File.WriteAllText(aBak, aExisting, new UTF8Encoding(false)); aRes.BackupPath = aBak; }
                    catch (Exception e)
                    {
                        // 備份失敗就不要寫 —— 這是使用者的檔，沒有退路的動作不做。
                        aRes.Ok = false;
                        aRes.Message = $"備份失敗，因此沒有寫入（檔案沒有被動過）：{e.GetType().Name}: {e.Message}";
                        return aRes;
                    }
                }
                else aRes.BackupPath = aBak;
            }

            // ② 原子寫入
            string aTmp = iPath + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                string? aDir = Path.GetDirectoryName(iPath);
                if (!string.IsNullOrEmpty(aDir)) Directory.CreateDirectory(aDir!);
                File.WriteAllText(aTmp, aNext, new UTF8Encoding(false));
                if (File.Exists(iPath)) File.Replace(aTmp, iPath, null);
                else File.Move(aTmp, iPath);
            }
            catch (Exception e)
            {
                try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch { /* 殘檔清不掉不蓋真錯 */ }
                aRes.Ok = false;
                aRes.Message = $"寫檔失敗：{e.GetType().Name}: {e.Message}"
                               + (aRes.BackupPath != null ? $"（備份在 {Path.GetFileName(aRes.BackupPath)}）" : "");
                return aRes;
            }

            // ③ 回讀驗證 —— 寫入端會替自己說謊。
            SCP_EntryParse aBack = Inspect(iPath, iManaged);
            if (aBack.State != SCP_EntryState.Synced)
            {
                aRes.Ok = false;
                aRes.Message = $"寫進去了但回讀不是 Synced（{aBack.State}）：{aBack.Detail}";
                return aRes;
            }

            aRes.Ok = true; aRes.Changed = true;
            aRes.Message = $"✓ 已更新 {Path.GetFileName(iPath)}（{aParse.State} → Synced，回讀確認）"
                           + (aRes.BackupPath != null ? $"；備份：{Path.GetFileName(aRes.BackupPath)}" : "");
            return aRes;
        }

        /// <summary>把受管區塊切掉（使用者的字全部留著）。找不到區塊 ＝ 不算失敗，但要說出來。</summary>
        public static SCP_EntryInstallResult Uninstall(string iPath, string iManaged)
        {
            var aRes = new SCP_EntryInstallResult();
            SCP_EntryParse aParse = Inspect(iPath, iManaged);
            aRes.StateBefore = aParse.State;

            if (aParse.State == SCP_EntryState.NotInstalled || aParse.State == SCP_EntryState.NeedsMigration)
            {
                aRes.Ok = true; aRes.Changed = false;
                aRes.Message = "沒有受管區塊可以移除（檔案沒有被動過）。" + aParse.Detail;
                return aRes;
            }
            if (aParse.State == SCP_EntryState.Duplicated || aParse.State == SCP_EntryState.MarkerBroken)
            {
                aRes.Ok = false;
                aRes.Message = "停手：" + aParse.Detail;
                return aRes;
            }

            string? aExisting = SafeRead(iPath);
            if (aExisting == null) { aRes.Ok = false; aRes.Message = "讀不了檔案，沒有動它。"; return aRes; }

            string aNext = SCP_EntryDoc.Remove(aExisting);
            string aTmp = iPath + ".tmp" + Guid.NewGuid().ToString("N").Substring(0, 8);
            try
            {
                File.WriteAllText(aTmp, aNext, new UTF8Encoding(false));
                File.Replace(aTmp, iPath, null);
            }
            catch (Exception e)
            {
                try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch { }
                aRes.Ok = false; aRes.Message = $"寫檔失敗：{e.GetType().Name}: {e.Message}";
                return aRes;
            }

            aRes.Ok = true; aRes.Changed = true;
            aRes.Message = $"✓ 已移除受管區塊（{Path.GetFileName(iPath)}）—— 你自己寫的內容都還在";
            return aRes;
        }

        static string? SafeRead(string iPath)
        {
            try { return File.ReadAllText(iPath, Encoding.UTF8); }
            catch { return null; }
        }
    }
}
