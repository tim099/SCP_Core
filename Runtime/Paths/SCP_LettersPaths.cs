// 區塊職責：persona **信件夾根**底下的版面 —— 一個人的信、憲法、見叢、見林、見森、Cmd 回傳檔。
// 物理意義：這批版面原本散在 `SCP_WakeLetters`（8 支）與 Senate 的 `PersonaLetters`
//           （`profile` 判準、`_persona_` lock 前綴）兩處。收攏成一份的理由跟 UCL 那側
//           `UCL_LettersPath` 一樣，而那條規則是踩出來的：
//           🩸 2026-08-18 之前 `Cmd_FreeTime` / `Cmd_Sculpture` / `Cmd_StreamWatch` 各自組回傳檔路徑，
//             其中一支連 letters 根都自己推 —— **同一個目錄的第四種算法**。
//             於是「回傳檔搬進 cmd/ 子目錄」從改一行變成 12 處各改一次，
//             而**漏掉一處不會報錯**（寫檔會自動建目錄 ⇒ 那支的回傳檔靜靜留在舊位置）。
// 數值影響：純字串組裝，零 IO。根的型別是 SCP_LettersRoot ⇒ 拿資料根來呼叫會編譯錯。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
namespace SCP.Core.Paths
{
    public static class SCP_LettersPaths
    {
        // ── 目錄／檔名常數（跨端契約：python awakening.py 那側同名）──────

        /// <summary>persona 的**判準**：信件夾底下有這個子目錄的才算一個人。</summary>
        public const string ProfileDirName = "profile";

        /// <summary>lock 檔名前綴（awakening 端 <c>write_lock()</c> 的格式，跨端契約）。</summary>
        public const string LockPrefix = "_persona_";

        public const string ConstitutionFileName = "_constitution.md";
        public const string KeysOpenFileName = "_keys_open.md";
        public const string LatestPointerFileName = "_latest.md";
        public const string LongtermDirName = "longterm";
        public const string ForestDirName = "forest";
        public const string WakesDirName = "wakes";
        public const string RestsDirName = "rests";
        public const string CmdDirName = "cmd";

        /// <summary>見根：關鍵記憶碎片目錄（python memory.fragments_dir 同名）。</summary>
        public const string FragmentsDirName = "fragments";

        /// <summary>
        /// 見根索引檔名。⚠ 底線開頭是**產物檔的標記** —— 掃碎片時要跳過底線開頭的檔，
        /// 否則索引會把自己也算成一筆碎片（而且每重建一次都合理地多一筆）。
        /// </summary>
        public const string RootIndexFileName = "_root_index.md";

        // ── 版面 ──────────────────────────────────────────────────

        public static string PersonaDir(SCP_LettersRoot iRoot, string iPersona)
            => iRoot.Value + "/" + iPersona;

        public static string ProfileDir(SCP_LettersRoot iRoot, string iPersona)
            => PersonaDir(iRoot, iPersona) + "/" + ProfileDirName;

        public static string ConstitutionPath(SCP_LettersRoot iRoot, string iPersona)
            => PersonaDir(iRoot, iPersona) + "/" + ConstitutionFileName;

        public static string KeysOpenPath(SCP_LettersRoot iRoot, string iPersona)
            => PersonaDir(iRoot, iPersona) + "/" + KeysOpenFileName;

        public static string LatestPointerPath(SCP_LettersRoot iRoot, string iPersona)
            => PersonaDir(iRoot, iPersona) + "/" + LatestPointerFileName;

        public static string FragmentsDir(SCP_LettersRoot iRoot, string iPersona)
            => PersonaDir(iRoot, iPersona) + "/" + FragmentsDirName;

        public static string RootIndexPath(SCP_LettersRoot iRoot, string iPersona)
            => FragmentsDir(iRoot, iPersona) + "/" + RootIndexFileName;

        public static string LongtermDir(SCP_LettersRoot iRoot, string iPersona)
            => PersonaDir(iRoot, iPersona) + "/" + LongtermDirName;

        public static string ForestDir(SCP_LettersRoot iRoot, string iPersona)
            => LongtermDir(iRoot, iPersona) + "/" + ForestDirName;

        public static string WakesDir(SCP_LettersRoot iRoot, string iPersona)
            => PersonaDir(iRoot, iPersona) + "/" + WakesDirName;

        public static string RestsDir(SCP_LettersRoot iRoot, string iPersona)
            => PersonaDir(iRoot, iPersona) + "/" + RestsDirName;

        /// <summary>Cmd 回傳檔的目錄（<c>&lt;persona&gt;/cmd/</c>）。</summary>
        public static string CmdDir(SCP_LettersRoot iRoot, string iPersona)
            => PersonaDir(iRoot, iPersona) + "/" + CmdDirName;

        /// <summary>
        /// 一份 Cmd 回傳檔（<c>&lt;persona&gt;/cmd/&lt;cmd&gt;_&lt;step&gt;.md</c>）。
        /// <para>⚠ 沒有 step 時就是 <c>&lt;cmd&gt;.md</c> —— 不要補一個空的底線
        /// （<c>goodmorning_.md</c> 這種檔名會讓 glob 對不上）。</para>
        /// </summary>
        public static string CmdPayload(SCP_LettersRoot iRoot, string iPersona, string iCmd, string? iStep = null)
        {
            string aName = string.IsNullOrEmpty(iStep) ? iCmd : iCmd + "_" + iStep;
            return CmdDir(iRoot, iPersona) + "/" + aName + ".md";
        }

        /// <summary>某個 persona 的 lock 檔（在 <c>_session</c> 目錄底下，**不在信件夾裡**）。</summary>
        public static string LockFileName(string iPersona)
            => LockPrefix + iPersona + ".json";
    }
}
