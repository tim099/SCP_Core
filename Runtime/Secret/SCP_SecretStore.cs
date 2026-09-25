// 區塊職責：secrets 資料夾的**位置解析 ＋ 掃描 ＋ 一鍵解密全部**（TASK-0300）。
// 物理意義：資料夾名是專案佈局事實（入版控的 `<資料根>/secrets_config.json` 的 `m_SecretsDir`，缺檔＝`Secret`）——
//          跟 Unity 端 `UCL_SecretsPath` 讀**同一個檔、同一個欄位、同一個預設**（那邊轉呼叫本檔的 ReadDirName）。
//          ⛔ 不做「找不到就退回 _secrets」的 fallback（沿革見 UCL_SecretsPath 檔頭：那是「跑起來了但用的是另一個宇宙的檔」的入口）。
// 數值影響：
//   · Scan 純讀；每顆 .enc 回 metadata（不需密碼）＋ 同名 .txt 明文在不在。
//   · DecryptAll：**一組密碼套到每一顆**；明文已在的**跳過**（⛔ 不覆寫、不重解）。
//     結果逐顆分五種（解成功／已有明文／密碼不對／檔案壞了／寫不出去），⛔ 不合成一個「失敗 N 顆」——
//     密碼不對要換密碼再按一次，檔案壞了要重加密，寫不出去要看權限 ⇒ 三種處置不同。
//   · 明文先寫暫存檔、再 `File.Move` 到目標：目標已存在時 Move 會丟例外 ⇒ **結構上不可能覆寫**
//     （掃描之後才有人放了明文的那個窗口也一樣）。密碼不對 ⇒ 一個 byte 都不寫。
// ⚠ 方言限制：C# 9 / netstandard2.1。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using SCP.Core.Json;

namespace SCP.Core.Secret
{
    /// <summary>一顆 .enc 的掃描結果。路徑全是絕對路徑（正斜線）。</summary>
    public sealed class SCP_SecretInfo
    {
        public string EncPath = "";
        public string PlainPath = "";
        public bool PlainExists;
        public string Label = "";
        public string Hint = "";
        public string CreatedAt = "";
        /// <summary>3 ＝ UCLS1；0 ＝ 讀不了（見 <see cref="Error"/>）。</summary>
        public int FormatVersion;
        /// <summary>非空 ＝ 不是 UCLS1 或解析失敗。</summary>
        public string Error = "";
        public string Name => Path.GetFileNameWithoutExtension(EncPath);
    }

    public enum SCP_SecretDecryptOutcome
    {
        /// <summary>解開並寫出明文。</summary>
        Decrypted,
        /// <summary>明文本來就在 ⇒ 沒動它。</summary>
        SkippedPlainExists,
        /// <summary>HMAC 不符 ⇒ 這組密碼不是它的（或密文被改過）。沒寫任何東西。</summary>
        WrongPassword,
        /// <summary>不是 UCLS1／格式壞了／讀不了 .enc。沒寫任何東西。</summary>
        Broken,
        /// <summary>解開了但明文寫不出去（權限、磁碟、或期間有人放了同名明文）。</summary>
        WriteFailed,
    }

    public sealed class SCP_SecretDecryptItem
    {
        public string Name = "";
        public string EncPath = "";
        public string PlainPath = "";
        public SCP_SecretDecryptOutcome Outcome;
        public string Detail = "";
    }

    public static class SCP_SecretStore
    {
        public const string ConfigFileName = "secrets_config.json";
        public const string ConfigKey = "m_SecretsDir";
        public const string DefaultDirName = "Secret";

        /// <summary>
        /// 讀 `&lt;資料根&gt;/secrets_config.json` 的資料夾名（相對資料根）。缺檔／空值 ⇒ <see cref="DefaultDirName"/>。
        /// 壞檔 ⇒ 用預設並把原因放進 <paramref name="oWarning"/>（⛔ 不 throw：畫面上呼叫；但壞掉必須看得見）。
        /// </summary>
        public static string ReadDirName(string iDataRoot, out string? oWarning)
        {
            oWarning = null;
            string aPath = Path.Combine(iDataRoot, ConfigFileName);
            try
            {
                if (!File.Exists(aPath)) return DefaultDirName;
                string aText = File.ReadAllText(aPath);
                if (string.IsNullOrWhiteSpace(aText)) return DefaultDirName;
                string aDir = SCP_JsonParser.Parse(aText).GetString(ConfigKey, "").Trim().Replace('\\', '/').Trim('/');
                if (aDir.Length == 0) { oWarning = $"設定檔的 {ConfigKey} 是空的，改用預設 '{DefaultDirName}'（{aPath}）"; return DefaultDirName; }
                return aDir;
            }
            catch (Exception e)
            {
                oWarning = $"讀設定失敗，改用預設 '{DefaultDirName}'：{e.Message}（{aPath}）";
                return DefaultDirName;
            }
        }

        /// <summary>secrets 資料夾的絕對路徑（正斜線）。</summary>
        public static string ResolveDir(string iDataRoot, out string? oWarning)
            => Path.Combine(iDataRoot, ReadDirName(iDataRoot, out oWarning)).Replace('\\', '/');

        /// <summary>`x.enc` → `x.txt`。</summary>
        public static string PlainPathOf(string iEncPath)
            => iEncPath.EndsWith(".enc", StringComparison.OrdinalIgnoreCase)
                ? iEncPath.Substring(0, iEncPath.Length - 4) + ".txt"
                : iEncPath + ".txt";

        /// <summary>掃資料夾底下（含子資料夾）每一顆 .enc。資料夾不存在 ⇒ 空清單（呼叫端自己判「不存在」與「空」）。</summary>
        public static List<SCP_SecretInfo> Scan(string iSecretsDir)
        {
            var aList = new List<SCP_SecretInfo>();
            if (!Directory.Exists(iSecretsDir)) return aList;
            string[] aFiles = Directory.GetFiles(iSecretsDir, "*.enc", SearchOption.AllDirectories);
            Array.Sort(aFiles, StringComparer.Ordinal);
            foreach (string aAbs in aFiles)
            {
                string aEnc = aAbs.Replace('\\', '/');
                var aInfo = new SCP_SecretInfo { EncPath = aEnc, PlainPath = PlainPathOf(aEnc) };
                aInfo.PlainExists = File.Exists(aInfo.PlainPath);
                byte[]? aBytes = null;
                try
                {
                    aBytes = File.ReadAllBytes(aEnc);
                    SCP_SecretMeta aMeta = SCP_SecretCrypto.ReadMetadata(aBytes);
                    aInfo.Label = aMeta.Label;
                    aInfo.Hint = aMeta.Hint;
                    aInfo.CreatedAt = aMeta.CreatedAt;
                    aInfo.FormatVersion = aMeta.FormatVersion;
                }
                catch (Exception e)
                {
                    aInfo.FormatVersion = 0;
                    aInfo.Error = aBytes != null && SCP_SecretCrypto.IsUclsFormat(aBytes)
                        ? $"UCLS1 解析失敗：{e.Message}"
                        : aBytes == null ? $"讀不了：{e.Message}" : "舊 python 格式（TKN1/TKN2）或未知 —— 對明文重新加密即可";
                }
                aList.Add(aInfo);
            }
            return aList;
        }

        /// <summary>
        /// 一鍵解密：同一組密碼套到 <paramref name="iSecretsDir"/> 底下每一顆 .enc，**只解明文還不在的**。
        /// 回傳逐顆結果（順序同 <see cref="Scan"/>）。密碼空 ⇒ ArgumentException（⛔ 不拿空字串當密碼去試每一顆）。
        /// </summary>
        public static List<SCP_SecretDecryptItem> DecryptAll(string iSecretsDir, string iPassphrase)
        {
            if (string.IsNullOrEmpty(iPassphrase)) throw new ArgumentException("密碼不可為空");
            var aOut = new List<SCP_SecretDecryptItem>();
            foreach (SCP_SecretInfo aInfo in Scan(iSecretsDir))
            {
                var aItem = new SCP_SecretDecryptItem { Name = aInfo.Name, EncPath = aInfo.EncPath, PlainPath = aInfo.PlainPath };
                aOut.Add(aItem);
                if (aInfo.PlainExists) { aItem.Outcome = SCP_SecretDecryptOutcome.SkippedPlainExists; aItem.Detail = "明文已在，沒動它"; continue; }
                if (aInfo.Error.Length > 0) { aItem.Outcome = SCP_SecretDecryptOutcome.Broken; aItem.Detail = aInfo.Error; continue; }

                byte[] aPlain;
                try { aPlain = SCP_SecretCrypto.Decrypt(File.ReadAllBytes(aInfo.EncPath), iPassphrase); }
                catch (CryptographicException) { aItem.Outcome = SCP_SecretDecryptOutcome.WrongPassword; aItem.Detail = "密碼不對（或密文被改過）"; continue; }
                catch (Exception e) { aItem.Outcome = SCP_SecretDecryptOutcome.Broken; aItem.Detail = $"{e.GetType().Name}: {e.Message}"; continue; }

                string aTmp = aInfo.PlainPath + ".tmp-" + Guid.NewGuid().ToString("N");
                try
                {
                    File.WriteAllBytes(aTmp, aPlain);
                    File.Move(aTmp, aInfo.PlainPath);   // 目標已存在會丟 ⇒ 不可能覆寫
                    aItem.Outcome = SCP_SecretDecryptOutcome.Decrypted;
                    aItem.Detail = $"已寫出 {aPlain.Length} bytes";
                }
                catch (Exception e)
                {
                    try { if (File.Exists(aTmp)) File.Delete(aTmp); } catch { }
                    aItem.Outcome = SCP_SecretDecryptOutcome.WriteFailed;
                    aItem.Detail = File.Exists(aInfo.PlainPath)
                        ? "掃描之後有人放了同名明文 ⇒ 沒覆寫它"
                        : $"{e.GetType().Name}: {e.Message}";
                }
            }
            return aOut;
        }
    }
}
