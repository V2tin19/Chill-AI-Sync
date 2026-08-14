using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace ChillAI.Plugin
{
    /// <summary>
    /// 自动写入 AI 工具钩子配置，跨机器通用：
    /// 1. Codex（~/.codex/hooks.json）——Codex 官方 Hooks 格式，6 类事件，命令路径指向插件目录的 codex-hook.ps1；
    /// 2. ZCode（~/.zcode/cli/config.json）——Claude Code hook schema（events/matcher/type:"process"），
    ///    5 类事件复用同一转发脚本；合并保留用户已有 hooks，只覆盖我们的 5 个事件；
    /// 3. ~/.codex/config.toml —— 确保 [features] codex_hooks = true（Codex CLI 通道）。
    /// 若 Codex hooks.json 已存在且不是本插件的（不含 codex-hook.ps1），则不覆盖。
    /// ZCode 写入受 EnableZcode 开关控制。
    /// </summary>
    public static class HooksInstaller
    {
        private static readonly string[] CodexEvents =
        {
            "SessionStart", "UserPromptSubmit", "PostToolUse",
            "PermissionRequest", "Stop", "SessionEnd"
        };

        /// <summary>ZCode 支持的事件（无 SessionEnd；PostToolUseFailure 非必需）。</summary>
        private static readonly string[] ZcodeEvents =
        {
            "SessionStart", "UserPromptSubmit", "PostToolUse",
            "PermissionRequest", "Stop"
        };

        // ---------------- Codex ----------------

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
                    Log("检测到已有其他插件的 hooks.json，为避免冲突不覆盖: " + hooksPath);
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
                    Log("已更新 " + hooksPath + "（Codex 钩子路径指向插件目录，JSON 校验通过）");
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

        // ---------------- ZCode（Claude Code hook schema） ----------------

        /// <summary>
        /// 向 ~/.zcode/cli/config.json 写入 ZCode hooks（Claude Code schema，type:"process" 执行器）。
        /// 合并保留用户已有的 hooks 配置，只覆盖我们的 5 个事件。受 EnableZcode 开关控制。
        /// </summary>
        public static void EnsureZcodeHooks()
        {
            if (!(Plugin.EnableZcode?.Value ?? true))
            {
                return; // ZCode 联动关闭
            }

            var scriptPath = PluginScriptPath();
            if (scriptPath == null)
            {
                return;
            }

            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var configPath = Path.Combine(profile, ".zcode", "cli", "config.json");

            try
            {
                JObject root;
                if (File.Exists(configPath))
                {
                    try { root = JObject.Parse(File.ReadAllText(configPath)); }
                    catch { root = new JObject(); } // 解析失败：重建（不破坏原文件——先备份？见下）
                }
                else
                {
                    root = new JObject();
                }

                var hooks = (JObject)(root["hooks"] ?? (root["hooks"] = new JObject()));
                hooks["enabled"] = true;
                if (hooks["timeoutMs"] == null) hooks["timeoutMs"] = 60000;
                if (hooks["maxOutputBytes"] == null) hooks["maxOutputBytes"] = 32768;

                var events = (JObject)(hooks["events"] ?? (hooks["events"] = new JObject()));
                foreach (var evt in ZcodeEvents)
                {
                    events[evt] = BuildZcodeEventArray(scriptPath, evt);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                File.WriteAllText(configPath, root.ToString(Formatting.Indented));

                // 自检
                JObject.Parse(File.ReadAllText(configPath));
                Log("已更新 ~/.zcode/cli/config.json（ZCode hooks: " + ZcodeEvents.Length + " 事件，JSON 校验通过）");
            }
            catch (Exception ex)
            {
                Log("写入 ZCode hooks 配置失败: " + ex.Message);
            }
        }

        /// <summary>ZCode hooks 数组：单条 process 执行器（不经 shell，直接 argv 调用 powershell.exe 转发脚本）。</summary>
        private static JArray BuildZcodeEventArray(string scriptPath, string evt)
        {
            return new JArray(
                new JObject(
                    new JProperty("hooks", new JArray(
                        new JObject(
                            new JProperty("type", "process"),
                            new JProperty("command", "powershell.exe"),
                            new JProperty("args", new JArray("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-EventName", evt)),
                            new JProperty("timeoutMs", 3000)
                        )
                    ))
                )
            );
        }

        // ---------------- 通用 ----------------

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
                Log($"未找到 codex-hook.ps1（{scriptPath}），跳过 hooks 写入");
                return null;
            }
            return scriptPath;
        }

        /// <summary>用 Newtonsoft 序列化生成 Codex hooks.json——引号/反斜杠自动转义，保证 JSON 语法正确。</summary>
        private static string BuildJson(string scriptPath)
        {
            var cmd = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -EventName ";
            var root = new Dictionary<string, object>
            {
                ["hooks"] = CodexEvents.ToDictionary(
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
