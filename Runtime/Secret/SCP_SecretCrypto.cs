// 區塊職責：secret 對稱加解密（UCLS1 格式）—— **本專案唯一的加解密實作**（TASK-0300 由 UCL_Core 搬入）。
// 物理意義：純 .NET BCL（System.Security.Cryptography）：PBKDF2-SHA256 導 key ＋ AES-256-CBC ＋
//          HMAC-SHA256 Encrypt-then-MAC。Unity（netstandard2.1）與 senate.exe（net10）編同一份 ⇒
//          Unity 加的檔 Senate 解得開、反之亦然。UCL_Core 的 `UCL_SecretCrypto` 只剩轉呼叫本檔，⛔ 不留第二份。
//          格式與演算法**逐位元組搬自** `UCL_SecretCrypto`（2026-07-22 版，沿革見 UCL_Core git 歷史）。
// 設計取捨：
//   - Encrypt-then-MAC：先驗 HMAC 才解密 → 密碼錯／竄改在 AES 前就擋下（不洩 padding oracle）。
//   - metadata（hint/label/created）是明文行、不參與 KDF → 不需密碼就讀得到（失憶救援）。
//   - HMAC 涵蓋除 M 行外的整份序列化字串 → 竄改任一 metadata／iv／密文都驗不過。
// 數值影響：KDF 200k iter；salt／iv 各 16 byte 每次隨機；key 材料 64 byte 拆 enc(32)＋mac(32)。
//          密碼錯 → CryptographicException（不回傳任何明文）。
// UCLS1 格式（\n 分隔；base64 為標準非 urlsafe）：
//   UCLS1 / S:<salt> / N:<iter> / H:<hint> / C:<created> / L:<label> / V:<iv> / M:<mac> / <ciphertext>
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SCP.Core.Secret
{
    /// <summary>不需密碼就讀得回的 metadata。</summary>
    public sealed class SCP_SecretMeta
    {
        public string Hint = "";
        public string Label = "";
        public string CreatedAt = "";
        /// <summary>3 ＝ UCLS1；1/2 ＝ 舊 python TKN1/TKN2（本 lib 不解）。</summary>
        public int FormatVersion;
    }

    public static class SCP_SecretCrypto
    {
        public const string Magic = "UCLS1";
        public const int FormatVersion = 3;
        public const int KdfIterations = 200_000;
        const int SaltLen = 16;
        const int IvLen = 16;
        const int AesKeyLen = 32;
        const int MacKeyLen = 32;
        public const int HintMaxLen = 256;

        const string PrefixSalt = "S:";
        const string PrefixIter = "N:";
        const string PrefixHint = "H:";
        const string PrefixCreated = "C:";
        const string PrefixLabel = "L:";
        const string PrefixIv = "V:";
        const string PrefixMac = "M:";

        // 同 salt＋同密碼＋同輪數 → 同 64 byte；前 32 給 AES-256、後 32 給 HMAC。
        static (byte[] EncKey, byte[] MacKey) DeriveKeys(string iPassphrase, byte[] iSalt, int iIterations)
        {
            using (var aKdf = new Rfc2898DeriveBytes(Encoding.UTF8.GetBytes(iPassphrase ?? ""), iSalt, iIterations, HashAlgorithmName.SHA256))
            {
                byte[] aMaterial = aKdf.GetBytes(AesKeyLen + MacKeyLen);
                byte[] aEnc = new byte[AesKeyLen];
                byte[] aMac = new byte[MacKeyLen];
                Buffer.BlockCopy(aMaterial, 0, aEnc, 0, AesKeyLen);
                Buffer.BlockCopy(aMaterial, AesKeyLen, aMac, 0, MacKeyLen);
                return (aEnc, aMac);
            }
        }

        // 「除 M 行外」的整份序列化字串 —— HMAC 的輸入。解密時用解析出的欄位重組同一字串重算比對。
        static string BuildSignedBody(string iSaltB64, int iIter, string iHint, string iCreated, string iLabel, string iIvB64, string iCtB64)
        {
            var aSb = new StringBuilder();
            aSb.Append(Magic).Append('\n');
            aSb.Append(PrefixSalt).Append(iSaltB64).Append('\n');
            aSb.Append(PrefixIter).Append(iIter.ToString(CultureInfo.InvariantCulture)).Append('\n');
            aSb.Append(PrefixHint).Append(iHint).Append('\n');
            aSb.Append(PrefixCreated).Append(iCreated).Append('\n');
            aSb.Append(PrefixLabel).Append(iLabel).Append('\n');
            aSb.Append(PrefixIv).Append(iIvB64).Append('\n');
            aSb.Append(iCtB64);
            return aSb.ToString();
        }

        static void ValidateSingleLine(string iValue, string iField)
        {
            if (iValue.IndexOf('\n') >= 0 || iValue.IndexOf('\r') >= 0)
                throw new ArgumentException($"{iField} 不可含換行字元（會破壞 UCLS1 行格式）");
        }

        /// <summary>明文 → UCLS1 密文。每次加密 salt／iv 都重生 ⇒ 同明文同密碼兩次輸出不同。</summary>
        public static byte[] Encrypt(byte[] iPlaintext, string iPassphrase, string? iHint = "", string? iLabel = "", DateTime? iCreatedAt = null)
        {
            if (iPlaintext == null) throw new ArgumentNullException(nameof(iPlaintext));
            if (string.IsNullOrEmpty(iPassphrase)) throw new ArgumentException("passphrase 不可為空");
            string aHint = iHint ?? "";
            string aLabel = iLabel ?? "";
            ValidateSingleLine(aHint, "hint");
            ValidateSingleLine(aLabel, "label");
            if (aHint.Length > HintMaxLen)
                throw new ArgumentException($"hint 超過 {HintMaxLen} char 上限（目前 {aHint.Length}）");

            byte[] aSalt = new byte[SaltLen];
            byte[] aIv = new byte[IvLen];
            using (var aRng = RandomNumberGenerator.Create())
            {
                aRng.GetBytes(aSalt);
                aRng.GetBytes(aIv);
            }
            var (aEncKey, aMacKey) = DeriveKeys(iPassphrase, aSalt, KdfIterations);

            byte[] aCt;
            using (var aAes = Aes.Create())
            {
                aAes.KeySize = 256;
                aAes.Mode = CipherMode.CBC;
                aAes.Padding = PaddingMode.PKCS7;
                aAes.Key = aEncKey;
                aAes.IV = aIv;
                using (var aEnc = aAes.CreateEncryptor()) aCt = aEnc.TransformFinalBlock(iPlaintext, 0, iPlaintext.Length);
            }

            string aSaltB64 = Convert.ToBase64String(aSalt);
            string aIvB64 = Convert.ToBase64String(aIv);
            string aCtB64 = Convert.ToBase64String(aCt);
            string aCreated = (iCreatedAt ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

            string aSigned = BuildSignedBody(aSaltB64, KdfIterations, aHint, aCreated, aLabel, aIvB64, aCtB64);
            byte[] aMac;
            using (var aH = new HMACSHA256(aMacKey)) aMac = aH.ComputeHash(Encoding.UTF8.GetBytes(aSigned));

            // 最終檔 ＝ magic..V 行 ＋ M 行 ＋ 密文行（M 插在 V 與密文之間）
            var aSb = new StringBuilder();
            aSb.Append(Magic).Append('\n');
            aSb.Append(PrefixSalt).Append(aSaltB64).Append('\n');
            aSb.Append(PrefixIter).Append(KdfIterations.ToString(CultureInfo.InvariantCulture)).Append('\n');
            aSb.Append(PrefixHint).Append(aHint).Append('\n');
            aSb.Append(PrefixCreated).Append(aCreated).Append('\n');
            aSb.Append(PrefixLabel).Append(aLabel).Append('\n');
            aSb.Append(PrefixIv).Append(aIvB64).Append('\n');
            aSb.Append(PrefixMac).Append(Convert.ToBase64String(aMac)).Append('\n');
            aSb.Append(aCtB64);
            return Encoding.UTF8.GetBytes(aSb.ToString());
        }

        sealed class Parsed
        {
            public string SaltB64 = "", IterStr = "", Hint = "", Created = "", Label = "", IvB64 = "", MacB64 = "", CtB64 = "";
            public int Iter;
        }

        // 純結構解析（不需密碼）。非 UCLS1／缺行 → FormatException。
        static Parsed ParseFile(byte[] iCiphertext)
        {
            if (iCiphertext == null || iCiphertext.Length == 0) throw new FormatException("空密文");
            // 正規化 CRLF（git autocrlf）—— base64 不含換行、metadata 已驗無換行 ⇒ 不破壞內容
            string aText = Encoding.UTF8.GetString(iCiphertext).Replace("\r\n", "\n").TrimEnd('\n', '\r');
            string[] aLines = aText.Split('\n');
            if (aLines.Length < 9 || aLines[0] != Magic)
                throw new FormatException($"非 {Magic} 格式（可能是舊 python TKN1/TKN2，本 lib 不解；請用明文重加密）");
            var p = new Parsed
            {
                SaltB64 = StripPrefix(aLines[1], PrefixSalt, "S"),
                IterStr = StripPrefix(aLines[2], PrefixIter, "N"),
                Hint = StripPrefix(aLines[3], PrefixHint, "H"),
                Created = StripPrefix(aLines[4], PrefixCreated, "C"),
                Label = StripPrefix(aLines[5], PrefixLabel, "L"),
                IvB64 = StripPrefix(aLines[6], PrefixIv, "V"),
                MacB64 = StripPrefix(aLines[7], PrefixMac, "M"),
                CtB64 = aLines[8],
            };
            if (!int.TryParse(p.IterStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out p.Iter) || p.Iter <= 0)
                throw new FormatException("N: 輪數欄非正整數");
            return p;
        }

        static string StripPrefix(string iLine, string iPrefix, string iField)
        {
            if (iLine == null || !iLine.StartsWith(iPrefix, StringComparison.Ordinal))
                throw new FormatException($"{iField} 行格式異常（缺 {iPrefix} 前綴）");
            return iLine.Substring(iPrefix.Length);
        }

        /// <summary>不需密碼讀 metadata（不驗 HMAC、不解密）。非 UCLS1 → FormatException。</summary>
        public static SCP_SecretMeta ReadMetadata(byte[] iCiphertext)
        {
            var p = ParseFile(iCiphertext);
            return new SCP_SecretMeta { Hint = p.Hint, Label = p.Label, CreatedAt = p.Created, FormatVersion = FormatVersion };
        }

        /// <summary>開頭是不是 `UCLS1\n`（容 CRLF；不丟例外）。</summary>
        public static bool IsUclsFormat(byte[] iCiphertext)
        {
            if (iCiphertext == null || iCiphertext.Length < 6) return false;
            try
            {
                string aHead = Encoding.UTF8.GetString(iCiphertext, 0, Math.Min(8, iCiphertext.Length));
                return aHead.StartsWith(Magic + "\n", StringComparison.Ordinal) || aHead.StartsWith(Magic + "\r\n", StringComparison.Ordinal);
            }
            catch { return false; }
        }

        /// <summary>
        /// UCLS1 密文＋密碼 → 明文。先重算 HMAC 並常數時間比對，通過才 AES 解密。
        /// 密碼錯／竄改 ⇒ <see cref="CryptographicException"/>；格式不對 ⇒ <see cref="FormatException"/>。
        /// </summary>
        public static byte[] Decrypt(byte[] iCiphertext, string iPassphrase)
        {
            if (string.IsNullOrEmpty(iPassphrase)) throw new ArgumentException("passphrase 不可為空");
            var p = ParseFile(iCiphertext);
            byte[] aSalt = Convert.FromBase64String(p.SaltB64);
            byte[] aIv = Convert.FromBase64String(p.IvB64);
            byte[] aExpected = Convert.FromBase64String(p.MacB64);
            byte[] aCt = Convert.FromBase64String(p.CtB64);
            var (aEncKey, aMacKey) = DeriveKeys(iPassphrase, aSalt, p.Iter);

            string aSigned = BuildSignedBody(p.SaltB64, p.Iter, p.Hint, p.Created, p.Label, p.IvB64, p.CtB64);
            byte[] aActual;
            using (var aH = new HMACSHA256(aMacKey)) aActual = aH.ComputeHash(Encoding.UTF8.GetBytes(aSigned));
            if (!CryptographicOperations.FixedTimeEquals(aActual, aExpected))
                throw new CryptographicException("HMAC 驗證失敗 — passphrase 錯誤或密文已被竄改");

            using (var aAes = Aes.Create())
            {
                aAes.KeySize = 256;
                aAes.Mode = CipherMode.CBC;
                aAes.Padding = PaddingMode.PKCS7;
                aAes.Key = aEncKey;
                aAes.IV = aIv;
                using (var aDec = aAes.CreateDecryptor()) return aDec.TransformFinalBlock(aCt, 0, aCt.Length);
            }
        }

        /// <summary>
        /// 自測：四種 round-trip ＋ 錯密碼拒絕 ＋ 竄改偵測。全過回摘要；任一失敗 throw。
        /// （Editor 端 `UCL_SecretCrypto.SelfTest` 轉呼叫這支 —— 同一份實作。）
        /// </summary>
        public static string SelfTest()
        {
            const string aPass = "self-test-pass-2026";
            var aCases = new (byte[] Plain, string Hint, string Label)[]
            {
                (Encoding.UTF8.GetBytes(""), "", ""),
                (Encoding.UTF8.GetBytes("hello-secret"), "生日後三碼", "EOV Token"),
                (Encoding.UTF8.GetBytes("多位元組·祕密🔐"), new string('x', HintMaxLen), "Unicode Case"),
                (MakePattern(512), "", "Binary Case"),
            };
            int n = 0;
            foreach (var c in aCases)
            {
                byte[] aEnc = Encrypt(c.Plain, aPass, c.Hint, c.Label);
                var aMeta = ReadMetadata(aEnc);
                if (aMeta.Hint != c.Hint) throw new Exception($"case {n}: hint 不符 metadata");
                if (aMeta.Label != c.Label) throw new Exception($"case {n}: label 不符 metadata");
                if (!ByteEq(Decrypt(aEnc, aPass), c.Plain)) throw new Exception($"case {n}: round-trip 明文不符 (len={c.Plain.Length})");
                n++;
            }
            byte[] e2 = Encrypt(Encoding.UTF8.GetBytes("secret"), aPass, "h", "L");
            bool aRejected = false;
            try { Decrypt(e2, "wrong-passphrase"); } catch (CryptographicException) { aRejected = true; }
            if (!aRejected) throw new Exception("錯密碼未被拒絕（HMAC 驗證失效）");
            byte[] aTampered = (byte[])e2.Clone();
            aTampered[aTampered.Length - 1] ^= 0x01;
            bool aCaught = false;
            try { Decrypt(aTampered, aPass); } catch (Exception) { aCaught = true; }
            if (!aCaught) throw new Exception("密文竄改未被偵測");
            return $"OK: UCLS1 self-test passed ({aCases.Length} round-trip cases + wrong-pass rejected + tamper detected)";
        }

        static byte[] MakePattern(int iLen)
        {
            byte[] b = new byte[iLen];
            for (int i = 0; i < iLen; i++) b[i] = (byte)(i * 31 + 7);
            return b;
        }

        static bool ByteEq(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }
}
