using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using UnityEngine;

namespace ChillAI.Plugin
{
    /// <summary>
    /// 自动写入 Codex 钩子配置，跨机器通用：
    /// 1. ~/.codex/hooks.json —— 钩子命令路径指向插件所在目录的 codex-hook.ps1（用 Newtonsoft 序列化，保证 JSON 语法正确）；
    /// 2. ~/.codex/config.toml —— 确保 [features] codex_hooks = true（CLI 通道需要；App 通道靠 App 内部的 Hooks 开关）。
    /// 若 ~/.codex/hooks.json 已存在且不是本插件的（不含 codex-hook.ps1），则不覆盖。
    /// </summary>
    public static class HooksInstaller
    {
        private static readonly string[] Events =
        {
            "SessionStart", "UserPromptSubmit", "PostToolUse",
            "PermissionRequest", "Stop", "SessionEnd"
        };

        public static void EnsureHooksJson()
        {
            var codexHome = CodexHome();
            var hooksPath = Path.Combine(codexHome, "hooks.json");

            var scriptPath = PluginScriptPath();
            if (scriptPath == null)
            {
                return;
            }

            if (File.Exists(hooksPath))
            {
                var existing = File.ReadAllText(hooksPath);
                if (!existing.Contains("codex-hook.ps1"))
                {
                    Log("检测到已有其他插件的 hooks.json，为避免冲突不覆盖。");
                    return;
                }
            }

            try
            {
                Directory.CreateDirectory(codexHome);
                File.WriteAllText(hooksPath, BuildJson(scriptPath));
                // 写入后自检：确保 JSON 语法有效（防止手拼转义类 bug 再次溜进来）
                try
                {
                    JsonConvert.DeserializeObject(File.ReadAllText(hooksPath));
                    Log("已更新 ~/.codex/hooks.json（钩子路径指向插件目录，JSON 校验通过）");
                }
                catch (Exception ex)
                {
                    Log("hooks.json 写入后校验失败: " + ex.Message);
                }
            }
            catch (Exception ex)
            {
                Log("写入 hooks.json 失败: " + ex.Message);
            }
        }

        /// <summary>确保 ~/.codex/config.toml 含 [features] codex_hooks = true（CLI 通道需要）。</summary>
        public static void EnsureConfigFeatures()
        {
            try
            {
                var codexHome = CodexHome();
                var configPath = Path.Combine(codexHome, "config.toml");
                Directory.CreateDirectory(codexHome);
                var content = File.Exists(configPath) ? File.ReadAllText(configPath) : "";

                if (Regex.IsMatch(content, @"codex_hooks\s*=\s*true"))
                {
                    return; // 已开启
                }

                const string key = "codex_hooks = true";
                if (Regex.IsMatch(content, @"codex_hooks\s*=\s*\w+"))
                {
                    // 存在 codex_hooks = false 之类的行 → 替换为 true
                    content = Regex.Replace(content, @"codex_hooks\s*=\s*\w+", key);
                }
                else
                {
                    var idx = content.IndexOf("[features]", StringComparison.Ordinal);
                    if (idx >= 0)
                    {
                        // 插入到 [features] 段内末尾（下一个 \n[ 段边界之前）
                        var after = idx + "[features]".Length;
                        var nextSeg = content.IndexOf("\n[", after, StringComparison.Ordinal);
                        var insertAt = nextSeg >= 0 ? nextSeg + 1 : content.Length;
                        content = content.Substring(0, insertAt) + key + "\n" + content.Substring(insertAt);
                    }
                    else
                    {
                        if (content.Length > 0 && !content.EndsWith("\n"))
                        {
                            content += "\n";
                        }
                        content += "\n[features]\n" + key + "\n";
                    }
                }

                File.WriteAllText(configPath, content);
                Log("已确保 config.toml 含 [features] codex_hooks = true（CLI 通道需要；App 用户请在 App 设置里开 Hooks 开关）");
            }
            catch (Exception ex)
            {
                Log("写入 config.toml 失败: " + ex.Message);
            }
        }

        private static string CodexHome() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        /// <summary>插件目录里的 codex-hook.ps1 路径；不存在则返回 null。</summary>
        private static string PluginScriptPath()
        {
            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(pluginDir))
            {
                return null;
            }
            var scriptPath = Path.Combine(pluginDir, "codex-hook.ps1");
            if (!File.Exists(scriptPath))
            {
                Log($"未找到 codex-hook.ps1（{scriptPath}），跳过 hooks.json 写入");
                return null;
            }
            return scriptPath;
        }

        /// <summary>用 Newtonsoft 序列化生成 hooks.json——引号/反斜杠自动转义，保证 JSON 语法正确。</summary>
        private static string BuildJson(string scriptPath)
        {
            var cmd = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -EventName ";
            var root = new Dictionary<string, object>
            {
                ["hooks"] = Events.ToDictionary(
                    evt => evt,
                    evt => (object)new object[]
                    {
                        new Dictionary<string, object>
                        {
                            ["hooks"] = new object[]
                            {
                                new Dictionary<string, object>
                                {
                                    ["type"] = "command",
                                    ["command"] = cmd + evt,
                                    ["timeout"] = 3
                                }
                            }
                        }
                    })
            };
            return JsonConvert.SerializeObject(root, Formatting.Indented);
        }

        private static void Log(string message)
        {
            Debug.Log("[ChillAI] " + message);
            Plugin.StaticLogger?.LogInfo("[ChillAI] " + message);
        }
    }
}
