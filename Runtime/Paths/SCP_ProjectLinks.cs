// 區塊職責：綁定的外部專案 —— 專案名 ⇒ 專案根（絕對路徑）＋ 文件資料夾。
//           Tim 2026-10-19：「讓 CLI 支援綁定外部專案&文件」「把目前的Blocksail & Blockside綁定吧
//           (同時跟 /scp-task 系統整合 可以知道這個task對應專案路徑在哪裡」。
// 物理意義：名字跟任務單的 `project` 欄位是**同一把尺**（不分大小寫）：單子標 `Blocksail` ⇒ 查單時印出 Blocksail 的路徑；
//           有文件資料夾的專案，宿主同時把它接成文件根（`senate cmd doc` 查得到、skill 的 `docs:` 讀得到）。
//           本層**不知道設定住哪** —— 由宿主裝上（<see cref="SCP_ProjectLinks.Provider"/>，同 `SCP_DocStore.RootsProvider` 的形狀）。
// 數值影響：純讀。
//
// ⚠ 三個「不會叫」的失效，查詢時都要說出來（<see cref="SCP_ProjectLinks.Describe"/>）：
//   ① 單子標了專案、設定裡沒有這個名字 ⇒ 「沒有綁定」，⛔ 不印空白。
//   ② 綁了、路徑不存在（換機器、搬家）⇒ 「路徑不存在」。
//   ③ 宿主沒裝 ⇒ 「這個宿主沒有裝上專案綁定」—— 跟「沒有綁定」分開。
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;

namespace SCP.Core.Paths
{
    /// <summary>一個綁定的外部專案。<see cref="Docs"/> 空字串 ＝ 沒有文件資料夾。</summary>
    public sealed class SCP_ProjectLink
    {
        public readonly string Name;
        public readonly string Root;
        public readonly string Docs;
        public SCP_ProjectLink(string iName, string iRoot, string iDocs) { Name = iName; Root = iRoot; Docs = iDocs; }
    }

    public static class SCP_ProjectLinks
    {
        /// <summary>宿主裝上的專案綁定。⚠ null ＝ **這個宿主沒有裝**（跟「一個都沒綁」分開）。</summary>
        public static Func<IReadOnlyList<SCP_ProjectLink>>? Provider;

        /// <summary>全部綁定。宿主沒裝 ⇒ 空清單（要分辨就看 <see cref="Provider"/> 是不是 null）。</summary>
        public static IReadOnlyList<SCP_ProjectLink> All()
            => Provider?.Invoke() ?? (IReadOnlyList<SCP_ProjectLink>)Array.Empty<SCP_ProjectLink>();

        /// <summary>照名字找（不分大小寫）；沒有 ⇒ null。</summary>
        public static SCP_ProjectLink? Find(string iName)
        {
            if (string.IsNullOrWhiteSpace(iName)) return null;
            foreach (SCP_ProjectLink aLink in All())
                if (string.Equals(aLink.Name, iName.Trim(), StringComparison.OrdinalIgnoreCase)) return aLink;
            return null;
        }

        /// <summary>
        /// 給人看的一句：專案根與文件在哪；沒綁、路徑不存在、宿主沒裝都明說。
        /// <paramref name="oRoot"/>／<paramref name="oDocs"/>：綁了而且存在才有值（給 values）。
        /// </summary>
        public static string Describe(string iName, out string oRoot, out string oDocs)
        {
            oRoot = ""; oDocs = "";
            if (Provider == null) return "⚠ 這個宿主沒有裝上專案綁定（SCP_ProjectLinks.Provider）—— 查不到路徑不代表沒有綁";
            SCP_ProjectLink? aLink = Find(iName);
            if (aLink == null)
                return "⚠ 「" + iName + "」沒有綁定 —— senate.local.json 的 `linkedProjects` 沒有這個名字（現有："
                       + Names() + "）";
            if (!Directory.Exists(aLink.Root)) return "⚠ 綁到 " + aLink.Root + "，但這個路徑不存在（換機器或搬家了？改 senate.local.json 的 `linkedProjects`）";
            oRoot = aLink.Root;
            string aDocs = "（沒有設文件資料夾）";
            if (aLink.Docs.Length > 0)
            {
                if (Directory.Exists(aLink.Docs)) { oDocs = aLink.Docs; aDocs = "　文件：" + aLink.Docs + "（`senate cmd doc` 查得到）"; }
                else aDocs = "　⚠ 文件資料夾不存在：" + aLink.Docs;
            }
            return "路徑：" + aLink.Root + aDocs;
        }

        static string Names()
        {
            var aNames = new List<string>();
            foreach (SCP_ProjectLink aLink in All()) aNames.Add(aLink.Name);
            return aNames.Count == 0 ? "一個都沒有" : string.Join("、", aNames.ToArray());
        }
    }
}
