// 區塊職責：一個 agent target 的安裝位置 —— skill 裝哪、入口檔放哪。
// 物理意義：每家 agent 讀自己的目錄（Claude `.claude/skills/`、Codex `.codex/skills/`、
//           Antigravity `.agents/skills/`）。⚠ 三者**必須各自獨立**：
//           🩸 UCL 那邊踩過 —— Codex 一度共用 Antigravity 的 `.agents/skills/.ucl_installed`，
//           於是 Codex 頁面讀到別人的標記後**誤判為已安裝**。
// 數值影響：純字串（路徑組裝走 SCP_Paths 的同一套規矩：根傳進來、不推導）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;

namespace SCP.Core.Skills
{
    /// <summary>一家 agent 的安裝位置。</summary>
    public sealed class SCP_SkillTarget
    {
        SCP_SkillTarget(string iId, string iDisplay, string iSkillsRel)
        {
            Id = iId;
            Display = iDisplay;
            SkillsRelative = iSkillsRel;
        }

        /// <summary>CLI 用的 id（`--target` 的值）。</summary>
        public string Id { get; }

        /// <summary>畫面上的名字。</summary>
        public string Display { get; }

        /// <summary>skill 安裝目錄（相對專案根）。</summary>
        public string SkillsRelative { get; }

        /// <summary>某個專案底下這個 target 的 skill 目錄。</summary>
        public string SkillsDir(string iProjectRoot)
            => iProjectRoot.Replace('\\', '/').TrimEnd('/') + "/" + SkillsRelative;

        public string SkillDir(string iProjectRoot, string iSkill)
            => SkillsDir(iProjectRoot) + "/" + iSkill;

        public override string ToString() { return Id; }

        // ── 內建清單 ──────────────────────────────────────────────

        public static readonly SCP_SkillTarget Claude =
            new SCP_SkillTarget("claude", "Claude Code", ".claude/skills");

        public static readonly SCP_SkillTarget Codex =
            new SCP_SkillTarget("codex", "Codex", ".codex/skills");

        public static readonly SCP_SkillTarget Antigravity =
            new SCP_SkillTarget("antigravity", "Antigravity", ".agents/skills");

        /// <summary>全部 target（畫面逐列、一鍵全裝都走這個順序）。</summary>
        public static readonly IReadOnlyList<SCP_SkillTarget> All =
            new List<SCP_SkillTarget> { Claude, Codex, Antigravity };

        /// <summary>依 id 找。認不得回 null —— **不要退回第一個**（那會裝到別人的目錄）。</summary>
        public static SCP_SkillTarget? ById(string? iId)
        {
            if (string.IsNullOrEmpty(iId)) return null;
            foreach (SCP_SkillTarget t in All)
                if (string.Equals(t.Id, iId, StringComparison.OrdinalIgnoreCase)) return t;
            return null;
        }
    }
}
