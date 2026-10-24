using System;
using System.Collections.Generic;

namespace ChillAI.Plugin
{
    /// <summary>
    /// DeepSeek Harness（dsh）profile 补丁（cordis.patch.yml）的纯文本合并/摘除逻辑。
    ///
    /// 该文件刻意不引用 Unity / BepInEx / Newtonsoft，因此可以被单独编译与测试
    /// （见 scripts/../ 说明：本插件通过文本区块标记来 reconcile，绝不解析或重写用户的其他 YAML 条目）。
    ///
    /// 设计要点：
    /// - 我们只负责自己那一段带标记的区块（BlockBegin … BlockEnd），其余字节原样保留；
    /// - profile 补丁是一个「顶层 YAML 数组」，所以我们的区块是一条 `- insert:` 列表项；
    /// - 文件当前可能只有注释和一行 `[]`（dsh 生成的默认空文档）——
    ///   `[]` 之后再写列表项不是合法 YAML，因此写入时必须先把 `[]` 去掉；
    /// - 摘除后若没有其他条目，要还原成合法的空数组文档（保留原注释）。
    /// </summary>
    public static class DshPatchFile
    {
        /// <summary>本插件写入的区块起始标记（同时用于识别「这段是我们的」）。</summary>
        public const string BlockBegin = "# >>> Chill AI DSH 联动（由游戏插件自动维护，请勿手改本区块）>>>";

        /// <summary>本插件写入的区块结束标记。</summary>
        public const string BlockEnd = "# <<< Chill AI DSH 联动 <<<";

        /// <summary>随插件分发的 dsh 侧桥接插件文件名（挂载时复制到 profile 目录，以相对路径引用）。</summary>
        public const string PluginFileName = "dsh-hook.mjs";

        /// <summary>该桥接插件在 dsh Loader 里的行 id。</summary>
        public const string PluginId = "chillai-sync";

        /// <summary>dsh 生成的 profile 补丁默认注释（还原空文档时使用，保持与原文件一致）。</summary>
        private static readonly string[] DefaultHeader =
        {
            "# Your patch layer for this dsh profile, applied after every bundle layer:",
            "# a top-level YAML array of loader patch entries (id-targeted config",
            "# overrides, disables, and insert lists; `!!js` expressions allowed).",
        };

        /// <summary>要写入 profile 补丁的区块文本（含首尾换行，便于直接拼接）。</summary>
        public static string Block()
        {
            return "\n" + BlockBegin + "\n"
                + "- insert:\n"
                + "    - id: " + PluginId + "\n"
                + "      name: './" + PluginFileName + "'\n"
                + BlockEnd + "\n";
        }

        /// <summary>文本里是否包含本插件的区块标记。</summary>
        public static bool ContainsBlock(string text)
        {
            return !string.IsNullOrEmpty(text)
                && text.IndexOf(BlockBegin, StringComparison.Ordinal) >= 0;
        }

        /// <summary>
        /// 区块标记是否残缺（只有起始或只有结束标记）。
        /// 典型来源是上一次写入被中断（File.WriteAllText 不是原子写）。
        /// 这种情况下一律原文返回、不做任何改动，避免把用户文件改得更坏。
        /// </summary>
        public static bool HasIncompleteBlock(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }
            var hasBegin = text.IndexOf(BlockBegin, StringComparison.Ordinal) >= 0;
            var hasEnd = text.IndexOf(BlockEnd, StringComparison.Ordinal) >= 0;
            return hasBegin != hasEnd;
        }

        /// <summary>
        /// 确保文本已挂载本插件区块（幂等）：先摘除旧区块，再按文档状态合并。
        /// </summary>
        public static string EnsureMounted(string text)
        {
            text = text ?? "";
            if (HasIncompleteBlock(text))
            {
                return text; // 标记残缺：保守起见不动
            }

            var stripped = StripBlock(text);
            if (HasDocument(stripped))
            {
                return stripped.TrimEnd() + "\n" + Block();
            }

            // 只有注释/空行，或仅有表示空数组的 `[]`：注释保留，去掉 `[]` 再写区块
            var head = RemoveEmptyArrayToken(stripped);
            return head + Block();
        }

        /// <summary>
        /// 确保文本已摘除本插件区块（幂等）：摘除后若没有其他条目，还原为合法空数组文档。
        /// </summary>
        public static string EnsureRemoved(string text)
        {
            text = text ?? "";
            if (HasIncompleteBlock(text))
            {
                return text; // 标记残缺：保守起见不动
            }
            if (!ContainsBlock(text))
            {
                return text; // 不含本插件区块：完全不动（保护用户配置）
            }

            var stripped = StripBlock(text);
            if (HasDocument(stripped))
            {
                return stripped.TrimEnd() + "\n";
            }

            var head = KeepCommentsOnly(stripped);
            return head + "[]\n";
        }

        /// <summary>摘除本插件区块（含区块前后的空行），其余内容原样返回。</summary>
        public static string StripBlock(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text;
            }

            var begin = text.IndexOf(BlockBegin, StringComparison.Ordinal);
            if (begin < 0)
            {
                return text;
            }

            var end = text.IndexOf(BlockEnd, begin, StringComparison.Ordinal);
            if (end < 0)
            {
                return text; // 标记不完整：保守起见不动，避免截断用户文件
            }

            // 连同区块前的空白一起吃掉，避免反复开关后堆积空行
            var cutFrom = begin;
            while (cutFrom > 0 && char.IsWhiteSpace(text[cutFrom - 1]))
            {
                cutFrom--;
            }

            var cutTo = end + BlockEnd.Length;
            while (cutTo < text.Length && (text[cutTo] == '\n' || text[cutTo] == '\r'))
            {
                cutTo++;
            }

            return text.Substring(0, cutFrom) + text.Substring(cutTo);
        }

        /// <summary>文本里是否已经有真正的 YAML 文档内容（非空行、非注释、非 `---`、非空数组 `[]`）。</summary>
        public static bool HasDocument(string text)
        {
            foreach (var raw in SplitLines(text))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal) || line == "---")
                {
                    continue;
                }
                if (line == "[]" || line == "[ ]")
                {
                    continue;
                }
                return true;
            }
            return false;
        }

        /// <summary>移除表示空数组的 `[]` 行（保留注释与空行），用于把空文档让位给我们的区块。</summary>
        public static string RemoveEmptyArrayToken(string text)
        {
            var kept = new List<string>();
            foreach (var raw in SplitLines(text))
            {
                var line = raw.Trim();
                if (line == "[]" || line == "[ ]")
                {
                    continue;
                }
                kept.Add(raw.TrimEnd());
            }
            var result = string.Join("\n", kept).TrimEnd();
            return result.Length == 0 ? "" : result + "\n\n";
        }

        /// <summary>只保留注释行（空行丢弃）；若一条都没有，则补上 dsh 的默认注释头。</summary>
        private static string KeepCommentsOnly(string text)
        {
            var kept = new List<string>();
            foreach (var raw in SplitLines(text))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line == "---" || line == "[]" || line == "[ ]")
                {
                    continue;
                }
                kept.Add(raw.TrimEnd());
            }

            if (kept.Count == 0)
            {
                kept.AddRange(DefaultHeader);
            }
            return string.Join("\n", kept) + "\n";
        }

        private static string[] SplitLines(string text)
        {
            return (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        }
    }
}
