// 區塊職責：舊頂層收尾信 → wakes/ 的顯式複製遷移；不登入、不改原信。
// 物理意義：只認第一層 frontmatter 的 trigger: cmd_goodnight；正文與 _latest.md 不算。
// 數值影響：confirm 才寫。缺 wakes 時先在 cmd/ 暫存整批，再一次搬入 wakes，避免半批成為醒次真相源。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using SCP.Core.Io;
using SCP.Core.Paths;

namespace SCP.Core.Letters
{
    public sealed class SCP_LettersMigrationResult
    {
        public bool Ok;
        public int Candidates;
        public int Copied;
        public int Existing;
        public readonly List<string> Lines = new List<string>();
    }

    public static class SCP_LettersMigration
    {
        /// <summary>讀第一層 frontmatter；不把正文內的 trigger 或 cmd_goodnight 的其他變體當成收尾信。</summary>
        public static bool IsGoodnightLetter(string iPath)
        {
            using var aReader = new StreamReader(iPath);
            if (aReader.ReadLine()?.Trim() != "---") return false;
            string? aTrigger = null;
            string? aLine;
            while ((aLine = aReader.ReadLine()) != null)
            {
                if (aLine.Trim() == "---")
                    return aTrigger == "cmd_goodnight"
                        && aReader.ReadToEnd().IndexOf("Manual logout via UCL_LoginStatusPage", StringComparison.Ordinal) < 0;
                if (aLine.StartsWith("trigger:", StringComparison.Ordinal))
                {
                    if (aTrigger != null) throw new InvalidDataException("重複 trigger 欄位：" + iPath);
                    aTrigger = aLine.Substring("trigger:".Length).Trim();
                    if (aTrigger.Length >= 2 && ((aTrigger[0] == '\'' && aTrigger[aTrigger.Length - 1] == '\'')
                        || (aTrigger[0] == '"' && aTrigger[aTrigger.Length - 1] == '"')))
                        aTrigger = aTrigger.Substring(1, aTrigger.Length - 2);
                }
            }
            return false;
        }

        public static SCP_LettersMigrationResult Run(SCP_LettersRoot iRoot, string iPersona, bool iConfirm)
        {
            var aResult = new SCP_LettersMigrationResult();
            try
            {
                // 先拒絕穿越與查無；不得藉由遷移幫拼錯的 persona 建一棵樹。
                if (string.IsNullOrWhiteSpace(iPersona) || iPersona == "." || iPersona == ".."
                    || iPersona.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                    throw new ArgumentException("persona 必須是單一目錄名稱");
                if (!SCP_PersonaProfile.Exists(iRoot.Value, iPersona))
                    throw new InvalidOperationException("persona 不存在：" + iPersona);
                if (!iConfirm) return Execute(iRoot, iPersona, false, aResult);

                // 手動遷移彼此互斥；取得鎖後重新掃描。鎖在 cmd/，不進信件歷史。
                using var aLock = SCP_FileLock.Acquire(SCP_LettersPaths.CmdPayload(iRoot, iPersona, "letters", "migration"));
                if (File.Exists(SCP_LettersPaths.SessionLockPath(iRoot, iPersona)))
                    throw new InvalidOperationException("persona 在線或 lock 未清除；先下線再遷移，避免更動本場醒次");
                return Execute(iRoot, iPersona, true, aResult);
            }
            catch (Exception e)
            {
                aResult.Lines.Add("✗ " + e.Message);
                if (aResult.Copied > 0) aResult.Lines.Add($"⚠ 本次已複製 {aResult.Copied} 封；原檔保留，修復後可重跑接續");
                return aResult;
            }
        }

        static SCP_LettersMigrationResult Execute(SCP_LettersRoot iRoot, string iPersona, bool iConfirm,
                                                  SCP_LettersMigrationResult ioResult)
        {
            string aTop = SCP_LettersPaths.PersonaDir(iRoot, iPersona);
            string aWakes = SCP_LettersPaths.WakesDir(iRoot, iPersona);
            if (File.Exists(aWakes)) throw new IOException("wakes 是檔案，不能建立目錄：" + aWakes);
            var aExisting = new Dictionary<string, string>(StringComparer.Ordinal);
            var aNumbers = new HashSet<int>();
            int aNext = 0;
            if (Directory.Exists(aWakes))
                foreach (string aPath in Directory.GetFiles(aWakes, "*.md"))
                {
                    string aName = Path.GetFileName(aPath);
                    if (aName.Length < 9 || aName[6] != '_' || !aName.Take(6).All(char.IsDigit)) continue;
                    int aNumber = int.Parse(aName.Substring(0, 6), CultureInfo.InvariantCulture);
                    string aOriginal = aName.Substring(7);
                    if (!aNumbers.Add(aNumber) || aExisting.ContainsKey(aOriginal))
                        throw new InvalidDataException("wakes 序號或來源檔名重複：" + aName);
                    aExisting.Add(aOriginal, aPath);
                    aNext = Math.Max(aNext, aNumber);
                }

            var aPlan = new List<(string Source, string Name, string Hash)>();
            // 舊檔名為 UTC 時間戳；ordinal 升冪讓編號穩定、不依賴檔案 mtime。
            foreach (string aPath in Directory.GetFiles(aTop, "*.md").OrderBy(Path.GetFileName, StringComparer.Ordinal))
            {
                string aName = Path.GetFileName(aPath);
                if (aName.StartsWith("_", StringComparison.Ordinal) || !IsGoodnightLetter(aPath)) continue;
                ioResult.Candidates++;
                string aHash = Hash(aPath);
                if (aExisting.TryGetValue(aName, out string? aCopy))
                {
                    if (Hash(aCopy) != aHash) throw new InvalidDataException("既有副本與原檔不同，未覆寫：" + aCopy);
                    ioResult.Existing++;
                    ioResult.Lines.Add("· 已存在且 SHA-256 一致：" + Path.GetFileName(aCopy));
                    continue;
                }
                if (++aNext > 999999) throw new InvalidDataException("收尾信序號超出六位數");
                string aTarget = aNext.ToString("D6", CultureInfo.InvariantCulture) + "_" + aName;
                if (File.Exists(Path.Combine(aWakes, aTarget)) || Directory.Exists(Path.Combine(aWakes, aTarget)))
                    throw new IOException("目標已存在，未覆寫：" + aTarget);
                aPlan.Add((aPath, aTarget, aHash));
                ioResult.Lines.Add((iConfirm ? "· 複製：" : "· 預計複製：") + aName + " → wakes/" + aTarget);
            }
            if (!iConfirm)
            {
                ioResult.Lines.Add("· dry-run：未建立 wakes、未寫入檔案；confirm=1 才執行");
                ioResult.Ok = true;
                return ioResult;
            }

            // 整批預檢成功才複製；暫存副本與原檔都驗 hash，原信逐位元組保留。
            string aStage = Path.Combine(SCP_LettersPaths.CmdDir(iRoot, iPersona), "letters-migration-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(aStage);
            try
            {
                foreach (var aItem in aPlan)
                {
                    string aCopy = Path.Combine(aStage, aItem.Name);
                    File.Copy(aItem.Source, aCopy, false);
                    if (Hash(aCopy) != aItem.Hash || Hash(aItem.Source) != aItem.Hash)
                        throw new IOException("複製驗證失敗或原檔在遷移期間變更：" + aItem.Source);
                }
                if (!Directory.Exists(aWakes))
                {
                    Directory.Move(aStage, aWakes);
                    ioResult.Copied = aPlan.Count;
                }
                else
                    foreach (var aItem in aPlan)
                    {
                        File.Move(Path.Combine(aStage, aItem.Name), Path.Combine(aWakes, aItem.Name));
                        ioResult.Copied++;
                    }
                ioResult.Lines.Add($"✓ 複製 {ioResult.Copied} 封／已有 {ioResult.Existing} 封；原檔未修改、未刪除");
                ioResult.Ok = true;
                return ioResult;
            }
            finally
            {
                if (Directory.Exists(aStage)) Directory.Delete(aStage, true);
            }
        }

        static string Hash(string iPath)
        {
            using var aSha = SHA256.Create();
            using var aStream = File.OpenRead(iPath);
            return BitConverter.ToString(aSha.ComputeHash(aStream));
        }
    }
}
