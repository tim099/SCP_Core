// 區塊職責：酒館寫入端的**顯式開關** —— `tavern.writer = editor | server`（TASK-0106 / D10）。
// 物理意義：Tim 2026-09-20 拍板「丙」：切換是**人下的決定**，不是機器的 fallback。
//           ⇒ 沒切之前一切照舊（Editor 寫）；切了而 Server 沒跑 ⇒ **整筆失敗且大聲**，
//           ⛔ **不退回本地直寫**。「自動降級」那條路從頭到尾不存在 ⇒ 結構上不可能有第二個寫入端。
// 數值影響：落點是**資料根**底下的 `agent_settings.json`（Tim 2026-09-21 拍板）——
//           Editor 與 Server 兩個宿主看同一個檔，⛔ 不是 `senate.local.json`（Unity 那側讀不到）。
//
// ⚠ 四種讀法**不得壓成兩態**（這一層存在的全部理由）：
//   · 沒設定過      ⇒ `editor`，而 `IsExplicit=false` —— 呼叫端印得出「這是預設值不是有人選的」
//   · 設成 editor   ⇒ `editor`，`IsExplicit=true`
//   · 設成 server   ⇒ `server`
//   · 設成看不懂的值／檔壞了 ⇒ **`Error` 有話說，⛔ 不悄悄回 editor**
//     🩸 悄悄回預設值正是這個專案最貴的錯誤形狀：打錯開關名字的人會看到「一切正常」，
//        而他以為自己切過去了。
//
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using SCP.Core.Paths;
using SCP.Core.Prefs;

namespace SCP.Core.Tavern
{
    /// <summary>誰在寫酒館訊息。</summary>
    public enum SCP_TavernWriteHost
    {
        /// <summary>Unity Editor 直寫（**今天的行為**，預設）。</summary>
        Editor = 0,

        /// <summary>Senate 常駐 Server 單一寫入端。Server 沒跑 ⇒ 發文整筆失敗，⛔ 不降級。</summary>
        Server = 1,
    }

    /// <summary>一次開關讀取的結果。<see cref="Error"/> 非空時 <see cref="Host"/> **沒有意義**。</summary>
    public readonly struct SCP_TavernWriteModeRead
    {
        public SCP_TavernWriteModeRead(SCP_TavernWriteHost iHost, bool iIsExplicit, string iRaw, string? iError)
        {
            Host = iHost;
            IsExplicit = iIsExplicit;
            Raw = iRaw ?? "";
            Error = iError;
        }

        /// <summary>要走哪一邊。⛔ <see cref="Error"/> 非空時不要看這個值。</summary>
        public SCP_TavernWriteHost Host { get; }

        /// <summary>
        /// 這個值是**有人設的**嗎。<c>false</c> ＝ 設定檔裡沒有這一格 ⇒ 走預設。
        /// <para>⚠ 呼叫端要印得出兩者的差別：「有人選了 editor」與「沒有人選過」在行為上一樣、
        /// 在**排查時完全不一樣**。</para>
        /// </summary>
        public bool IsExplicit { get; }

        /// <summary>落盤的原始字面（給錯誤訊息用）。</summary>
        public string Raw { get; }

        /// <summary>讀不了／看不懂時的原因。<c>null</c> ＝ 這次讀取有效。</summary>
        public string? Error { get; }

        public bool Ok => Error == null;

        /// <summary>一行人讀的說明（直接貼進 Cmd 輸出）。</summary>
        public string Describe()
        {
            if (!Ok) return "⛔ 酒館寫入開關讀不了：" + Error;
            return "酒館寫入端 = " + (Host == SCP_TavernWriteHost.Server ? "server" : "editor")
                   + (IsExplicit ? "（設定檔裡有這一格）" : "（**沒有人設過** ⇒ 預設值）");
        }
    }

    /// <summary>
    /// 讀／寫 `tavern.writer`。⚠ 本型別**只回答「開關是什麼」**，
    /// ⛔ 不負責「Server 在不在」—— 那是另一個讀數，混在一起的話
    /// 「沒切過去」與「切了但 Server 掛了」會變成同一句話，而它們的處置相反。
    /// </summary>
    public static class SCP_TavernWriteMode
    {
        public const string SectionName = "tavern";
        public const string KeyName = "writer";

        public const string ValueEditor = "editor";
        public const string ValueServer = "server";

        /// <summary>
        /// ⚠ 預設值刻意**不寫進** <see cref="SCP_PrefKey"/> 的 default —— 那樣「沒設定」與「設成 editor」
        /// 會在讀取層被壓成同一態，而本檔存在的理由正是不讓它們同形。
        /// </summary>
        static readonly SCP_PrefKey<string> s_Key = SCP_PrefKey.String(SectionName, KeyName, "");

        /// <summary>設定檔路徑（`<資料根>/agent_settings.json`）。</summary>
        public static string SettingsPath(string iDataRoot)
            => SCP_DataPaths.Settings(new SCP_DataRoot(iDataRoot));

        /// <summary>讀開關。⛔ 看不懂的值一律回 <see cref="SCP_TavernWriteModeRead.Error"/>，不回預設。</summary>
        public static SCP_TavernWriteModeRead Read(string iDataRoot)
        {
            return Read(new SCP_JsonPrefs(SettingsPath(iDataRoot)));
        }

        /// <summary>讀開關（注入 prefs —— 測試與別的宿主用）。</summary>
        public static SCP_TavernWriteModeRead Read(ISCP_Prefs iPrefs)
        {
            SCP_PrefRead<string> aRead = iPrefs.Read(s_Key);
            switch (aRead.State)
            {
                case SCP_PrefState.Missing:
                    return new SCP_TavernWriteModeRead(SCP_TavernWriteHost.Editor, false, "", null);

                case SCP_PrefState.ReadError:
                    return new SCP_TavernWriteModeRead(SCP_TavernWriteHost.Editor, false, "",
                        "設定檔讀不了（" + SectionName + "." + KeyName + "）：" + (aRead.Error ?? "(沒說原因)"));

                default:
                    string aRaw = (aRead.Value ?? "").Trim();
                    // 空字串 ＝ 這一格存在但沒填。⚠ 那是**設定檔被動過**的訊號，不是「沒設定過」
                    //   ⇒ 不當 Missing 處理（兩者的排查方向不同：一個去看誰清空了它，一個去設它）。
                    if (aRaw.Length == 0)
                        return new SCP_TavernWriteModeRead(SCP_TavernWriteHost.Editor, false, "",
                            "設定檔裡有 " + SectionName + "." + KeyName + " 這一格但**值是空的** ——"
                            + " 要嘛填 `" + ValueEditor + "`／`" + ValueServer + "`，要嘛整格刪掉。");

                    if (aRaw == ValueEditor)
                        return new SCP_TavernWriteModeRead(SCP_TavernWriteHost.Editor, true, aRaw, null);
                    if (aRaw == ValueServer)
                        return new SCP_TavernWriteModeRead(SCP_TavernWriteHost.Server, true, aRaw, null);

                    return new SCP_TavernWriteModeRead(SCP_TavernWriteHost.Editor, false, aRaw,
                        "認不得的值 `" + aRaw + "`（" + SectionName + "." + KeyName + "）——"
                        + " 只吃 `" + ValueEditor + "` 或 `" + ValueServer + "`。"
                        + "⛔ 本層**不猜**：猜成 editor 的話，打錯字的人會看到一切正常而以為自己切過去了。");
            }
        }

        /// <summary>
        /// 寫開關。回 <c>(成功, 說明)</c>。⚠ 寫入走 <see cref="SCP_JsonPrefs"/>，
        /// 它保留未知 section 與未知欄位，並在寫完**回讀確認**。
        /// </summary>
        public static (bool Ok, string Message) Write(string iDataRoot, SCP_TavernWriteHost iHost)
        {
            var aPrefs = new SCP_JsonPrefs(SettingsPath(iDataRoot));
            string aValue = iHost == SCP_TavernWriteHost.Server ? ValueServer : ValueEditor;
            return aPrefs.Write(s_Key, aValue);
        }
    }
}
