// 區塊職責：跨 agent 共享 lesson 庫（`<data_root>/Lessons/lessons.jsonl`）的**寫入**本體 —— 去重＋append＋確認檔。
// 物理意義：TASK-0354。「記一條教訓」不需要 Unity Editor 開著。
//           ⭐ 輸出與 Editor 版**逐位元組同形**（jsonl 那一行、`_last_lesson.md`、`notelesson_last_op.md`）——
//           兩個寫入端會並存一段時間（同事手上的 skill 副本不會同時換掉），而 append-only 純文字的並存
//           **只在格式同形時才安全**：去重是拿 `"body":"…"` 的字面去比，形狀一分岔，同一條教訓就會被記兩次。
// 數值影響：去重命中 ⇒ 零 append，只覆寫確認檔；否則 append 一行（UTF-8 無 BOM、`\n` 結尾）＋覆寫確認檔。
//
// ⚠ 與 Editor 版**刻意不同**的兩格（都是補洞，不是改形狀）：
//   ① 去重＋append 包在 `SCP_FileLock` 裡 —— Editor 版沒有鎖，兩個寫入端同時記同一條會雙寫
//      （去重讀到的都是對方 append 之前的檔）。
//   ② 時間戳帶 `InvariantCulture` —— Editor 版沒帶，`:` 會跟著 CurrentCulture 走（在本機剛好一樣）。
// ⚠ 方言限制：C# 9 / netstandard2.1（Unity 那側也要編這份）。
#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SCP.Core.Io;

namespace SCP.Core.Lessons
{
    /// <summary>一次 note 的輸入（已 trim 過的值）。</summary>
    public sealed class SCP_LessonInput
    {
        public string Body = "";
        public string Actor = "";
        public string Category = "general";
        public string Title = "";
        public List<string> Tags = new List<string>();
    }

    /// <summary>一次 note 的結果。</summary>
    public sealed class SCP_LessonNoteResult
    {
        /// <summary>去重命中 ⇒ 沒有 append（⛔ 不是失敗）。</summary>
        public bool Duplicate;
        public string Ts = "";
        public string JsonlPath = "";
        /// <summary>寫進確認檔的那一份 markdown（全域 `_last_lesson.md` 與 per-persona 鏡寫同一份）。</summary>
        public string ConfirmText = "";
        /// <summary>去重檢查讀不動時的警告（照 Editor 版：**繼續 append**，但要說出來）。</summary>
        public string Warning = "";
    }

    public static class SCP_LessonLog
    {
        public const string LessonsDirName = "Lessons";
        public const string JsonlFileName = "lessons.jsonl";
        public const string LastLessonFileName = "_last_lesson.md";

        public static string LessonsDir(string iDataRoot) => Path.Combine(iDataRoot, LessonsDirName);
        public static string JsonlPath(string iDataRoot) => Path.Combine(LessonsDir(iDataRoot), JsonlFileName);
        public static string LastLessonPath(string iDataRoot) => Path.Combine(LessonsDir(iDataRoot), LastLessonFileName);

        /// <summary>
        /// tags 參數 → 清單：逗號切、trim、丟空、去重（Ordinal、保留首見順序）—— 與 Editor 版同。
        /// </summary>
        public static List<string> ParseTags(string? iRaw)
        {
            var aOut = new List<string>();
            if (string.IsNullOrEmpty(iRaw)) return aOut;
            foreach (string aPart in iRaw!.Split(','))
            {
                string t = aPart.Trim();
                if (t.Length > 0 && !aOut.Contains(t)) aOut.Add(t);
            }
            return aOut;
        }

        /// <summary>
        /// 去重 → append → 組確認檔內文（**不寫**確認檔，那由呼叫端決定落在哪）。
        /// <para>⚠ body 空白丟 <see cref="ArgumentException"/> —— 呼叫端應先擋。</para>
        /// </summary>
        /// <param name="iRelJsonl">確認檔裡印的 jsonl 相對路徑（Editor 版印相對 repo 根的路徑）。</param>
        public static SCP_LessonNoteResult Note(string iDataRoot, SCP_LessonInput iIn, string iRelJsonl)
        {
            if (string.IsNullOrWhiteSpace(iIn.Body)) throw new ArgumentException("body 必填");
            var r = new SCP_LessonNoteResult { JsonlPath = JsonlPath(iDataRoot) };
            Directory.CreateDirectory(LessonsDir(iDataRoot));

            using (SCP_FileLock.Acquire(r.JsonlPath))
            {
                // ── 去重：只看 body，比的是**緊湊字面** `"body":"<escaped>"`（Editor 版同一判準）──
                // ⚠ 舊格式的行（冒號後有空白、`\uXXXX` 轉義、key 叫 lesson）結構上比不到 —— 照舊，不在本層修。
                string aNeedle = "\"body\":" + ToJsonString(iIn.Body);
                if (File.Exists(r.JsonlPath))
                {
                    try
                    {
                        foreach (string aLine in File.ReadAllLines(r.JsonlPath, Encoding.UTF8))
                            if (aLine.Contains(aNeedle)) { r.Duplicate = true; break; }
                    }
                    catch (Exception e) { r.Warning = "dedupe check fail（繼續 append）：" + e.Message; }
                }

                if (r.Duplicate)
                {
                    r.ConfirmText = BuildDupText(iIn, iRelJsonl);
                    return r;
                }

                r.Ts = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
                File.AppendAllText(r.JsonlPath, BuildLine(r.Ts, iIn), new UTF8Encoding(false));
            }
            r.ConfirmText = BuildOkText(r.Ts, iIn, iRelJsonl);
            return r;
        }

        /// <summary>jsonl 的一行（含結尾 `\n`）。key 固定順序：ts, actor, category, body[, title][, tags]。</summary>
        public static string BuildLine(string iTs, SCP_LessonInput iIn)
        {
            var sb = new StringBuilder();
            sb.Append('{');
            sb.Append("\"ts\":").Append(ToJsonString(iTs)).Append(',');
            sb.Append("\"actor\":").Append(ToJsonString(iIn.Actor)).Append(',');
            sb.Append("\"category\":").Append(ToJsonString(iIn.Category)).Append(',');
            sb.Append("\"body\":").Append(ToJsonString(iIn.Body));
            if (iIn.Title.Length > 0) sb.Append(',').Append("\"title\":").Append(ToJsonString(iIn.Title));
            if (iIn.Tags.Count > 0)
            {
                sb.Append(',').Append("\"tags\":[");
                for (int i = 0; i < iIn.Tags.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(ToJsonString(iIn.Tags[i]));
                }
                sb.Append(']');
            }
            sb.Append('}');
            sb.Append('\n');
            return sb.ToString();
        }

        static string BuildOkText(string iTs, SCP_LessonInput iIn, string iRel)
        {
            var sb = new StringBuilder();
            sb.Append("# 📝 Lesson noted (").Append(iIn.Category).Append(")\n\n");
            sb.Append("- **ts**: `").Append(iTs).Append("`\n");
            sb.Append("- **actor**: `").Append(iIn.Actor).Append("`\n");
            sb.Append("- **category**: `").Append(iIn.Category).Append("`\n");
            if (iIn.Title.Length > 0) sb.Append("- **title**: ").Append(iIn.Title).Append('\n');
            if (iIn.Tags.Count > 0) sb.Append("- **tags**: `").Append(string.Join("`, `", iIn.Tags)).Append("`\n");
            sb.Append("- **body**: ").Append(iIn.Body).Append("\n\n");
            sb.Append("appended → `").Append(iRel).Append("`\n\n");
            sb.Append("---\n\n");
            sb.Append("後續：定期 review jsonl tail，跨 task 通用的那幾條人工升格進 `Lesson_Log` 文件的「精選」（").Append(SCP.Core.Cmd.SCP_CmdRegistry.Invoke("doc --arg op=show --arg name=Lesson_Log")).Append("）。\n");
            return sb.ToString();
        }

        static string BuildDupText(SCP_LessonInput iIn, string iRel)
        {
            var sb = new StringBuilder();
            sb.Append("# 🔁 Lesson 重複，skip\n\n");
            sb.Append("- **body**: ").Append(iIn.Body).Append('\n');
            sb.Append("- **actor**: `").Append(iIn.Actor).Append("`\n");
            sb.Append("- **category**: `").Append(iIn.Category).Append("`\n\n");
            sb.Append("已存在於 `").Append(iRel).Append("`，未重複 append（dedupe 防噪音）。\n");
            sb.Append("如要 force append，請改寫 body 內容。\n");
            return sb.ToString();
        }

        /// <summary>
        /// Editor 版 `ToJsonString` 的逐字移植：只轉 `" \ \n \r \t` 與其他 &lt; 0x20（`\u` 小寫 hex），
        /// 其餘一律原樣（含非 ASCII）。⚠ 換成通用 JSON writer 會讓去重的字面比對失準。
        /// </summary>
        public static string ToJsonString(string? s)
        {
            if (s == null) return "null";
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", (int)c);
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}
