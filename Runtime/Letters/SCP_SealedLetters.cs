// 區塊職責：密封信 —— 寫進 persona 信件 repo 的 `private` 分支，不切分支、不經過公開的 master。
// 物理意義：信件 repo 的 master 追公開 remote（origin），`private` 只推私有 remote（gitlab.private）。
//           工作區只有一份、checkout 的是 master ⇒ 要把檔案送進 `private` 又不換掉工作區，
//           只能繞過 index 與工作區、用 plumbing 造 commit：
//             暫存 index ← master 的 tree → 帶回既有 sealed/ → 塞新 blob → write-tree
//             → commit-tree（父 = private ＋ master）→ update-ref（帶舊值防併發覆寫）。
//           ⇒ `private` 永遠是「當前 master ＋ sealed/」，`git diff master private` 只剩 sealed/。
// 數值影響：只寫物件庫＋移動 `refs/heads/private`（＋工作區 sealed/ 下的檔，被 master 的 .gitignore 擋著）。
//           不動 HEAD、不動 master；預設不 push。不跑 hooks、不公告領薪 —— 密封信是私事，不是工作 commit。
// 防線：① master 的 .gitignore 要有 `sealed/`（寫入類 op 開頭驗，沒有就拒跑）
//       ② 寫完驗 master 的 tree 裡沒有這些路徑
//       ③ pre-push hook（版控內 `tools/githooks/pre-push` ＋ `core.hooksPath`）擋 private 被推上非私有 host
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SCP.Core.Git;

namespace SCP.Core.Letters
{
    public sealed class SCP_SealedResult
    {
        public int Exit;
        public readonly List<string> Lines = new List<string>();
        public SCP_SealedResult Say(string s) { Lines.Add(s); return this; }
        public SCP_SealedResult Fail(int e, string s) { Exit = e; Lines.Add(s); return this; }
    }

    sealed class SCP_SealedGitError : Exception { public SCP_SealedGitError(string m) : base(m) { } }

    public sealed class SCP_SealedLetters
    {
        public const string SealedDir = "sealed";
        public const string PrivateBranch = "private";
        public const string PublicBranch = "master";
        public const string PrivateRemote = "gitlab.private";
        public const string CipherAnswerSuffix = "cipher-answer";
        public const string HooksPath = "tools/githooks";
        public const string AllowedHost = "gitlab.com";

        public readonly string Repo;
        public readonly string Persona;

        SCP_SealedLetters(string iRepo, string iPersona) { Repo = iRepo; Persona = iPersona; }

        /// <summary>`<信件根>/<persona>`，必須是 git repo。</summary>
        public static SCP_SealedLetters? Open(string iLettersRoot, string iPersona, out string oError)
        {
            oError = "";
            string repo = Path.Combine(iLettersRoot, iPersona).Replace('\\', '/');
            if (!Directory.Exists(repo)) { oError = $"✗ 找不到 persona '{iPersona}' 的信件 repo：{repo}（信件根 = {iLettersRoot}）"; return null; }
            if (!Directory.Exists(Path.Combine(repo, ".git")) && !File.Exists(Path.Combine(repo, ".git")))
            { oError = $"✗ {repo} 不是 git repo —— 密封信需要 private 分支"; return null; }
            return new SCP_SealedLetters(repo, iPersona);
        }

        // ── git ───────────────────────────────────────────────────

        string Git(params string[] iArgs) => GitEnv(null, iArgs);

        string GitEnv(IReadOnlyDictionary<string, string>? iEnv, params string[] iArgs)
        {
            SCP_GitResult r = iEnv == null ? SCP_Git.RunTimeout(Repo, SCP_Git.DefaultTimeoutMs, iArgs)
                                           : SCP_Git.RunWithEnv(Repo, SCP_Git.DefaultTimeoutMs, iEnv, iArgs);
            if (r.Exit != 0)
                throw new SCP_SealedGitError($"git {string.Join(" ", iArgs)} 失敗：{((r.StdErr ?? "").Trim().Length > 0 ? r.StdErr!.Trim() : (r.StdOut ?? "").Trim())}");
            return Norm(r.StdOut ?? "").Trim();
        }

        string GitSoft(params string[] iArgs)
        {
            SCP_GitResult r = SCP_Git.RunTimeout(Repo, SCP_Git.DefaultTimeoutMs, iArgs);
            return Norm(r.StdOut ?? "").Trim();
        }

        static string Norm(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n");
        static List<string> Lines(string s) => s.Split('\n').Where(x => x.Length > 0).ToList();

        static void WriteCrlf(string iPath, string iText)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(iPath)!);
            File.WriteAllText(iPath, Norm(iText).Replace("\n", "\r\n"), new UTF8Encoding(false));
        }

        static string NowIso() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.ffffff", CultureInfo.InvariantCulture) + "Z";
        static string NowStamp() => DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);

        public static string Slug(string s)
        {
            string t = Regex.Replace((s ?? "").Trim(), @"[^\w一-鿿-]+", "-");
            t = Regex.Replace(t, "-+", "-").Trim('-');
            if (t.Length > 60) t = t.Substring(0, 60);
            return t.Length > 0 ? t : "untitled";
        }

        // ── 防線 ──────────────────────────────────────────────────

        public bool MasterIgnoresSealed(out string oWhy)
        {
            string gi = Path.Combine(Repo, ".gitignore");
            var lines = File.Exists(gi) ? Norm(File.ReadAllText(gi, Encoding.UTF8)).Split('\n').Select(x => x.Trim()).ToList() : new List<string>();
            if (lines.Contains(SealedDir + "/") || lines.Contains(SealedDir)) { oWhy = ""; return true; }
            oWhy = $"✗ 目前 checkout 的分支 .gitignore 沒有 `{SealedDir}/` —— 拒絕繼續。\n"
                 + $"  沒有那行的話，密封信會以 untracked 出現在 {PublicBranch} 的 git status，下一個 `git add -A` 就會把它推上公開 remote（history 刪不掉）。\n"
                 + $"  修法：在 {gi.Replace('\\', '/')} 加一行 `{SealedDir}/`";
            return false;
        }

        void AssertNotOnPublic(IEnumerable<string> iPaths)
        {
            var tracked = new HashSet<string>(Lines(Git("ls-tree", "-r", "--name-only", PublicBranch)));
            var leaked = iPaths.Where(tracked.Contains).ToList();
            if (leaked.Count > 0) throw new SCP_SealedGitError($"✗ 這些路徑竟然在 {PublicBranch} 上（公開）：{string.Join(", ", leaked)}");
        }

        bool BranchExists() => SCP_Git.RunTimeout(Repo, SCP_Git.DefaultTimeoutMs, "rev-parse", "--verify", "refs/heads/" + PrivateBranch).Exit == 0;

        /// <summary>`private` 不存在就從當前 master 建；回 true ＝ 這次新建。</summary>
        bool EnsurePrivateBranch()
        {
            if (BranchExists()) return false;
            Git("update-ref", "refs/heads/" + PrivateBranch, Git("rev-parse", PublicBranch));
            return true;
        }

        List<(string Mode, string Sha, string Path)> ExistingSealed()
        {
            var aOut = new List<(string, string, string)>();
            SCP_GitResult r = SCP_Git.RunTimeout(Repo, SCP_Git.DefaultTimeoutMs, "ls-tree", "-r", PrivateBranch, "--", SealedDir + "/");
            if (r.Exit != 0) return aOut;
            foreach (string line in Lines(Norm(r.StdOut ?? "")))
            {
                int tab = line.IndexOf('\t');
                if (tab < 0) continue;
                string[] parts = line.Substring(0, tab).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 3 && parts[1] == "blob") aOut.Add((parts[0], parts[2], line.Substring(tab + 1)));
            }
            return aOut;
        }

        /// <summary>把工作區的檔 commit 進 `private`（基底＝當前 master ＋既有 sealed/），不切分支。回新 commit sha。</summary>
        string CommitToPrivate(IReadOnlyList<string> iRelPaths, string iMessage)
        {
            string parent = Git("rev-parse", PrivateBranch);
            string baseSha = Git("rev-parse", PublicBranch);
            string idx = Path.Combine(Path.GetTempPath(), "sealed_idx_" + Guid.NewGuid().ToString("N"));
            var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = idx };
            string tree;
            try
            {
                GitEnv(env, "read-tree", PublicBranch);
                foreach (var e in ExistingSealed())
                    GitEnv(env, "update-index", "--add", "--cacheinfo", $"{e.Mode},{e.Sha},{e.Path}");
                foreach (string rel in iRelPaths)
                {
                    string src = Path.Combine(Repo, rel);
                    if (!File.Exists(src)) throw new SCP_SealedGitError("✗ 檔案不存在：" + src);
                    // --path 讓 .gitattributes 的換行／filter 規則生效（不帶的話 blob 與 checkout 出來的不一致）
                    string sha = GitEnv(env, "hash-object", "-w", "--path=" + rel, src);
                    GitEnv(env, "update-index", "--add", "--cacheinfo", $"100644,{sha},{rel}");
                }
                tree = GitEnv(env, "write-tree");
            }
            finally { try { if (File.Exists(idx)) File.Delete(idx); } catch (IOException) { } }

            string msg = Path.Combine(Path.GetTempPath(), "sealed_msg_" + Guid.NewGuid().ToString("N") + ".txt");
            string created;
            try
            {
                File.WriteAllText(msg, iMessage, new UTF8Encoding(false));
                var args = new List<string> { "commit-tree", tree, "-p", parent };
                // 第二父＝master：宣告「這份包含了到此為止的公開內容」，`git log private..master` 才對得了帳
                if (baseSha != parent && !Lines(Git("rev-list", parent)).Contains(baseSha)) { args.Add("-p"); args.Add(baseSha); }
                args.Add("-F"); args.Add(msg);
                created = Git(args.ToArray());
            }
            finally { try { File.Delete(msg); } catch (IOException) { } }
            Git("update-ref", "refs/heads/" + PrivateBranch, created, parent);
            return created;
        }

        string WriteSealed(string iRel, string iTitle, string iBody, IEnumerable<(string, string)>? iExtra = null)
        {
            string dst = Path.Combine(Repo, iRel).Replace('\\', '/');
            var fm = new List<string> { "---", "type: sealed_letter", "title: " + iTitle, "at: " + NowIso(), "visibility: private-branch-only" };
            if (iExtra != null) foreach (var (k, v) in iExtra) fm.Add($"{k}: {v}");
            fm.Add("---"); fm.Add("");
            WriteCrlf(dst, string.Join("\n", fm) + $"# 🔐 {iTitle}\n\n" + Norm(iBody).Trim() + "\n");
            return dst;
        }

        void PushNote(SCP_SealedResult r, bool iPush)
        {
            if (iPush) { Git("push", PrivateRemote, $"{PrivateBranch}:{PrivateBranch}"); r.Say($"   ⬆ 已推到 {PrivateRemote}/{PrivateBranch}"); }
            else r.Say("   ⚠ **未 push**（推送是對外動作，要顯式 push=1）");
        }

        // ── 密文 ──────────────────────────────────────────────────

        public static string NormCipher(string s)
            => string.Join("\n", Norm(s ?? "").Split('\n').Select(l => l.TrimEnd())).Trim();

        public static string CipherSha(string s)
        {
            using (var sha = SHA256.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(NormCipher(s))).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        List<string> SealedAnswers()
            => Lines(Git("ls-tree", "-r", "--name-only", PrivateBranch))
               .Where(n => n.StartsWith(SealedDir + "/", StringComparison.Ordinal) && n.Contains(CipherAnswerSuffix))
               .OrderByDescending(x => x, StringComparer.Ordinal).ToList();

        static (Dictionary<string, string> Meta, string Body) ReadFm(string text)
        {
            var meta = new Dictionary<string, string>();
            if (!text.StartsWith("---", StringComparison.Ordinal)) return (meta, text);
            int b = text.IndexOf("---", 3, StringComparison.Ordinal);
            if (b < 0) return (meta, text);
            foreach (string line in text.Substring(3, b - 3).Split('\n'))
            {
                int c = line.IndexOf(':');
                string k = (c < 0 ? line : line.Substring(0, c)).Trim();
                if (k.Length > 0) meta[k] = c < 0 ? "" : line.Substring(c + 1).Trim();
            }
            return (meta, text.Substring(b + 3).TrimStart('\n'));
        }

        static string ExtractFencedCipher(string body)
        {
            Match m = Regex.Match(body, "```cipher\n(.*?)```", RegexOptions.Singleline);
            return m.Success ? m.Groups[1].Value : "";
        }

        List<(string Name, string Text)> WakeLetters()
        {
            string d = Path.Combine(Repo, "wakes");
            var aOut = new List<(string, string)>();
            if (!Directory.Exists(d)) return aOut;
            foreach (string p in Directory.GetFiles(d, "*.md").OrderByDescending(x => x.ToLowerInvariant(), StringComparer.Ordinal))
            {
                try { aOut.Add((Path.GetFileName(p), Norm(File.ReadAllText(p, Encoding.UTF8)))); }
                catch (Exception) { }
            }
            return aOut;
        }

        // ── op ────────────────────────────────────────────────────

        SCP_SealedResult Guard(Func<SCP_SealedResult> iBody)
        {
            try { return iBody(); }
            catch (SCP_SealedGitError e) { return new SCP_SealedResult().Fail(1, e.Message); }
        }

        public SCP_SealedResult Write(string iTitle, string iBody, string iMessage, bool iPush) => Guard(() =>
        {
            var r = new SCP_SealedResult();
            if (!MasterIgnoresSealed(out string why)) return r.Fail(1, why);
            if (iTitle.Trim().Length == 0) return r.Fail(2, "✗ 要 title");
            if (Norm(iBody).Trim().Length == 0) return r.Fail(2, "✗ 內容為空（body）");
            bool created = EnsurePrivateBranch();
            string rel = $"{SealedDir}/{NowStamp()}__{Slug(iTitle)}.md";
            WriteSealed(rel, iTitle, iBody);
            string sha = CommitToPrivate(new[] { rel }, iMessage.Length > 0 ? iMessage : "密封信：" + iTitle);
            AssertNotOnPublic(new[] { rel });
            if (created) r.Say($"🌱 `{PrivateBranch}` 分支首次建立（基底＝當前 {PublicBranch}）");
            r.Say($"🔐 密封信已寫入 `{PrivateBranch}`：{sha.Substring(0, 8)}");
            r.Say("   " + rel);
            r.Say($"   工作區檔案存在但被 .gitignore 擋住 —— {PublicBranch} 看不到、不會被 add 走");
            r.Say($"   HEAD 仍在：{Git("rev-parse", "--abbrev-ref", "HEAD")}（沒有切分支）");
            PushNote(r, iPush);
            return r;
        });

        public SCP_SealedResult SealCipher(string iCipher, string iPlain, string iWake, string iMessage, bool iPush, string iVerifyHint) => Guard(() =>
        {
            var r = new SCP_SealedResult();
            if (!MasterIgnoresSealed(out string why)) return r.Fail(1, why);
            if (NormCipher(iCipher).Length == 0) return r.Fail(2, "✗ 密文為空 —— 沒有題目就不必封答案");
            if (iPlain.Trim().Length == 0) return r.Fail(2, "✗ 明文答案為空（工具不代筆；答案只有妳寫得出來）");
            bool created = EnsurePrivateBranch();
            string digest = CipherSha(iCipher);
            string wake = iWake.Length > 0 ? iWake : "?";
            string title = $"wake{wake}-{CipherAnswerSuffix}";
            string rel = $"{SealedDir}/{NowStamp()}__{Slug(title)}.md";
            string body = "## 協議\n"
                + "明早讀收尾信 🔐 密文區 → **先憑記憶網解密、寫下解讀** → 再開本檔比對：\n"
                + $"`{iVerifyHint}`\n"
                + "（工具只做機械對帳與並排，命中與否由妳判 —— 解不開＝出題爛，不是記性爛。）\n\n"
                + $"## 密文（原文，與信中逐字一致；sha256={digest.Substring(0, 16)}…）\n"
                + $"```cipher\n{NormCipher(iCipher)}\n```\n\n"
                + "## 逐句明文\n"
                + Norm(iPlain).Trim() + "\n";
            WriteSealed(rel, title, body, new[] { ("kind", "cipher_answer"), ("wake", wake), ("cipher_sha256", digest) });
            string sha = CommitToPrivate(new[] { rel }, iMessage.Length > 0 ? iMessage : "密封：" + title);
            AssertNotOnPublic(new[] { rel });
            if (created) r.Say($"🌱 `{PrivateBranch}` 分支首次建立（基底＝當前 {PublicBranch}）");
            r.Say($"🔐 密文答案已封緘 → `{PrivateBranch}`：{sha.Substring(0, 8)}");
            r.Say("   " + rel);
            r.Say("   cipher_sha256 = " + digest);
            r.Say("   ⚠ 封緘完成 —— **信裡的密文從現在起不准再改一字**（改了明早對帳會紅）");
            PushNote(r, iPush);
            return r;
        });

        public SCP_SealedResult VerifyCipher(string iGuess, string iWake) => Guard(() =>
        {
            var r = new SCP_SealedResult();
            if (iGuess.Trim().Length == 0) return r.Fail(2, "✗ 解讀為空 —— 先寫再開答案，這個順序就是整個機制");
            if (!BranchExists()) return r.Fail(1, $"(`{PrivateBranch}` 分支還不存在 —— 晚安時封緘過才會有)");
            List<string> answers = SealedAnswers();
            if (answers.Count == 0) return r.Fail(1, $"(`{PrivateBranch}` 上還沒有密文答案 —— 晚安時封緘過才會有)");
            string? path;
            if (iWake.Length > 0)
            {
                path = answers.FirstOrDefault(n => n.Contains($"wake{iWake}-{CipherAnswerSuffix}"));
                if (path == null) return r.Fail(1, $"✗ 找不到 wake{iWake} 的封緘答案；現有：\n  " + string.Join("\n  ", answers));
            }
            else path = answers[0];
            var (meta, body) = ReadFm(Git("show", $"{PrivateBranch}:{path}"));
            string sealedCipher = ExtractFencedCipher(body);
            string sealedSha = meta.TryGetValue("cipher_sha256", out string? s) ? s : "";
            r.Say($"# 🔐 密文對帳 — {path}");
            r.Say($"- 封緘於：{(meta.TryGetValue("at", out string? at) ? at : "?")}　wake={(meta.TryGetValue("wake", out string? wk) ? wk : "?")}");
            bool selfOk = sealedCipher.Length > 0 && CipherSha(sealedCipher) == sealedSha;
            r.Say("- 答案檔自身一致（區塊 vs frontmatter hash）：" + (selfOk ? "✅" : "❌"));
            string nc = NormCipher(sealedCipher);
            var hit = nc.Length > 0 ? WakeLetters().FirstOrDefault(w => NormCipher(w.Text).Contains(nc)) : default;
            if (hit.Name != null) r.Say($"- 信中密文逐字一致：✅（{hit.Name}）");
            else
            {
                r.Say("- 信中密文逐字一致：⚠ 沒在 wakes/ 找到逐字相同的密文");
                r.Say("    可能一：封緘後改了信（那這次對帳是在對一份被改過的題目）");
                r.Say("    可能二：密文在信裡被排版拆行 —— 讀一眼再判，別直接當成竄改");
            }
            var a = new HashSet<char>(NormCipher(iGuess));
            var bset = new HashSet<char>(Regex.Replace(body, @"\s+", ""));
            double overlap = a.Count > 0 ? (double)a.Count(bset.Contains) / a.Count : 0;
            r.Say($"- 字元重疊（粗糙讀數，**不是命中率**）：{overlap:P0}");
            r.Say("\n## 密文（題目）\n");
            r.Say(sealedCipher.Length > 0 ? sealedCipher : "(答案檔裡沒有 ```cipher 區塊)");
            r.Say("\n## 我的解讀（解封前寫的）\n");
            r.Say(iGuess.Trim());
            r.Say("\n## 封緘答案\n");
            r.Say(body.Trim());
            r.Say("\n---\n判定由妳自己下：逐句對，錯的那句記下**斷在哪個詞**。斷點通常是單位或新造詞 —— 修法是新慣例先在明文用兩次再進密文，不是把密文寫簡單。");
            return r;
        });

        public SCP_SealedResult List() => Guard(() =>
        {
            var r = new SCP_SealedResult();
            List<string> names = BranchExists()
                ? Lines(Git("ls-tree", "-r", "--name-only", PrivateBranch)).Where(n => n.StartsWith(SealedDir + "/", StringComparison.Ordinal)).ToList()
                : new List<string>();
            if (names.Count == 0) return r.Say($"(`{PrivateBranch}` 上還沒有密封信)");
            r.Say($"# 🔐 密封信（{names.Count} 封，在 `{PrivateBranch}` 分支）\n");
            foreach (string n in names.OrderByDescending(x => x, StringComparer.Ordinal)) r.Say("- " + n);
            return r;
        });

        /// <summary>直讀物件庫（⛔ 不用 checkout —— 那會把檔塞進 master 的 index）。</summary>
        public SCP_SealedResult Show(string iPath) => Guard(() => new SCP_SealedResult().Say(Git("show", $"{PrivateBranch}:{iPath}")));

        public SCP_SealedResult Restore(bool iOverwrite) => Guard(() =>
        {
            var r = new SCP_SealedResult();
            if (!MasterIgnoresSealed(out string why)) return r.Fail(1, why);
            if (!BranchExists()) return r.Say($"(本地還沒有 `{PrivateBranch}` 分支 —— 先 op=sync 或寫第一封)");
            List<string> names = Lines(Git("ls-tree", "-r", "--name-only", PrivateBranch)).Where(n => n.StartsWith(SealedDir + "/", StringComparison.Ordinal)).ToList();
            int n0 = 0;
            foreach (string rel in names)
            {
                string dst = Path.Combine(Repo, rel);
                if (File.Exists(dst) && !iOverwrite) continue;
                WriteCrlf(dst, Git("show", $"{PrivateBranch}:{rel}") + "\n");
                n0++;
            }
            return r.Say($"🔐 還原 {n0} 封（共 {names.Count} 封在分支上）" + (iOverwrite ? "" : "；已存在的跳過，要蓋過去用 overwrite=1"));
        });

        public SCP_SealedResult Resync(bool iDryRun) => Guard(() =>
        {
            var r = new SCP_SealedResult();
            EnsurePrivateBranch();
            List<string> behind = Lines(Git("log", "--oneline", $"{PrivateBranch}..{PublicBranch}"));
            if (behind.Count == 0) return r.Say($"✓ `{PrivateBranch}` 已涵蓋 {PublicBranch}，不需 resync");
            r.Say($"`{PrivateBranch}` 落後 {PublicBranch} {behind.Count} 筆：");
            foreach (string l in behind) r.Say("  - " + l);
            if (iDryRun) return r.Say("（dry_run=1，沒有真的動 ref）");
            string sha = CommitToPrivate(Array.Empty<string>(), $"resync: private 基底追上 {PublicBranch}（追 {behind.Count} 筆）");
            return r.Say($"✓ {sha.Substring(0, 8)} —— private 現在 = {PublicBranch} + {SealedDir}/");
        });

        public SCP_SealedResult Sync(bool iOverwrite) => Guard(() =>
        {
            var r = new SCP_SealedResult();
            if (!MasterIgnoresSealed(out string why)) return r.Fail(1, why);
            r.Say($"⬇ fetch {PrivateRemote} …");
            SCP_Git.RunTimeout(Repo, SCP_Git.DefaultTimeoutMs, "fetch", PrivateRemote, PrivateBranch);
            SCP_GitResult rr = SCP_Git.RunTimeout(Repo, SCP_Git.DefaultTimeoutMs, "rev-parse", $"refs/remotes/{PrivateRemote}/{PrivateBranch}");
            string remote = rr.Exit == 0 ? Norm(rr.StdOut ?? "").Trim() : "";
            if (remote.Length == 0) r.Say($"  （遠端還沒有 {PrivateBranch} 分支 —— 第一次要先 `git push -u {PrivateRemote} {PrivateBranch}`）");
            else if (!BranchExists())
            {
                Git("update-ref", "refs/heads/" + PrivateBranch, remote);
                r.Say($"  🌱 本地 {PrivateBranch} 由遠端建立 → {remote.Substring(0, 8)}");
            }
            else
            {
                string local = Git("rev-parse", PrivateBranch);
                if (remote == local) r.Say("  本地與遠端同一個 commit，無需更新");
                else if (Lines(Git("rev-list", remote)).Contains(local))
                {
                    Git("update-ref", "refs/heads/" + PrivateBranch, remote, local);
                    r.Say($"  ⏩ 本地 {PrivateBranch} 快進到 {remote.Substring(0, 8)}");
                }
                else return r.Fail(1, $"  ⚠ 本地與遠端**分岔**（local={local.Substring(0, 8)} remote={remote.Substring(0, 8)}）—— 不自動合併，請人工判斷。");
            }
            SCP_SealedResult rs = Restore(iOverwrite);
            r.Lines.AddRange(rs.Lines); r.Exit = rs.Exit;
            return r;
        });

        public const string PrePushHook = "#!/bin/sh\n"
            + "# 擋下「private 分支被推到非私有 remote」—— 判斷的是目標 remote 的 URL（名字可以改，URL 才是事實）。\n"
            + "# 安裝：senate cmd sealed-letter --arg op=install_hook（寫入本檔並設 core.hooksPath）。\n"
            + "ALLOWED_HOST='" + AllowedHost + "'\n"
            + "remote_name=\"$1\"\n"
            + "remote_url=\"$2\"\n\n"
            + "while read -r local_ref local_sha remote_ref remote_sha; do\n"
            + "    case \"$local_ref$remote_ref\" in\n"
            + "        *refs/heads/private*)\n"
            + "            case \"$remote_url\" in\n"
            + "                *\"$ALLOWED_HOST\"*) ;;\n"
            + "                *)\n"
            + "                    echo \"✗ 拒絕推送：private 分支只能推到 $ALLOWED_HOST（私有）。\" >&2\n"
            + "                    echo \"  目標 remote: $remote_name  → $remote_url\" >&2\n"
            + "                    echo \"  private 含密封信；推上公開 remote 後 history 刪不掉。\" >&2\n"
            + "                    exit 1\n"
            + "                    ;;\n"
            + "            esac\n"
            + "            ;;\n"
            + "    esac\n"
            + "done\n"
            + "exit 0\n";

        string HookFile => Path.Combine(Repo, "tools", "githooks", "pre-push").Replace('\\', '/');

        public SCP_SealedResult InstallHook(bool iForce) => Guard(() =>
        {
            var r = new SCP_SealedResult();
            bool existed = File.Exists(HookFile);
            if (existed && !iForce) r.Say($"✓ hook 已存在：{HookFile}（要覆寫用 force=1）");
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HookFile)!);
                File.WriteAllText(HookFile, PrePushHook, new UTF8Encoding(false));   // LF：sh 腳本
                r.Say((existed ? "♻ 覆寫" : "🛡 寫入") + " hook：" + HookFile);
            }
            string cur = GitSoft("config", "core.hooksPath");
            if (cur != HooksPath)
            {
                Git("config", "core.hooksPath", HooksPath);
                r.Say($"🛡 core.hooksPath：{(cur.Length > 0 ? cur : "(未設)")} → {HooksPath}");
            }
            else r.Say($"✓ core.hooksPath 已是 {HooksPath}");
            return r.Say("   ⚠ hook 檔本身要 commit 進 master 才會跟著 clone 走；core.hooksPath 是本機設定，換機器要再跑一次");
        });

        public SCP_SealedResult Verify(string iInstallHint) => Guard(() =>
        {
            var r = new SCP_SealedResult();
            List<string> tracked = Lines(Git("ls-tree", "-r", "--name-only", PublicBranch)).Where(n => n.StartsWith(SealedDir + "/", StringComparison.Ordinal)).ToList();
            bool giOk = MasterIgnoresSealed(out string why);
            if (!giOk) r.Say(why);
            string cfg = GitSoft("config", "core.hooksPath");
            bool hookOk = File.Exists(HookFile), pathOk = cfg == HooksPath;
            r.Say($"- {PublicBranch} 上的密封信：{tracked.Count} 個 " + (tracked.Count == 0 ? "✅" : "❌ " + string.Join(", ", tracked)));
            r.Say("- .gitignore 防線：" + (giOk ? "✅" : "❌"));
            r.Say("- pre-push hook 檔：" + (hookOk ? "✅" : "❌ 缺（private 可被推上公開 remote）"));
            r.Say("- core.hooksPath：" + (pathOk ? "✅ " + cfg : $"❌ {(cfg.Length > 0 ? cfg : "未設")} —— 上面那個檔不會被執行"));
            r.Say("- private 分支：" + (BranchExists() ? "✅" : "⚠ 尚未建立（第一次寫入時自動建）"));
            bool ok = tracked.Count == 0 && giOk && hookOk && pathOk;
            if (!ok) { r.Say("\n修法：" + iInstallHint); r.Exit = 1; }
            return r;
        });
    }
}
