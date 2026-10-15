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
    /// 自动写入/移除 AI 工具钩子配置，跨机器通用，行为与 F8 联动开关保持一致（reconcile 语义）：
    /// 1. Codex（~/.codex/hooks.json）——Codex 官方 Hooks 格式，6 类事件，命令路径指向插件目录的 codex-hook.ps1；
    ///    EnableCodex 开 → 写入/更新；关 → 摘除我们的钩子（文件不含我们的脚本则完全不动）。
    /// 2. ZCode（~/.zcode/cli/config.json）——Claude Code hook schema（events/matcher/type:"process"），
    ///    5 类事件复用同一转发脚本；合并保留用户已有 hooks（只替换/摘除引用 codex-hook.ps1 的条目）；
    ///    配置解析失败时先备份原文件再重建，不静默丢弃用户配置。
    /// 3. ~/.codex/config.toml —— 仅 EnableCodex 开时确保 [features] codex_hooks = true（Codex CLI 通道）。
    /// 所有文件写入仅在 AutoInstallHooks 开启时发生（F8 切换联动开关时同样按此同步）。
    /// </summary>
    public static class HooksInstaller
    {
        /// <summary>识别"钩子条目属于本插件"的标记（转发脚本文件名）。</summary>
        private const string ScriptMarker = "codex-hook.ps1";

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

        // 带词边界：避免误改 foo_codex_hooks = false 之类同后缀的键
        private static readonly Regex CodexHooksTruePattern = new Regex(@"(?<!\w)codex_hooks\s*=\s*true");
        private static readonly Regex CodexHooksAnyPattern = new Regex(@"(?<!\w)codex_hooks\s*=\s*\w+");

        // ---------------- Codex ----------------

        /// <summary>按 EnableCodex 写入或移除 ~/.codex/hooks.json（合并保留用户已有 hooks，只替换/追加本插件条目）。</summary>
        public static void EnsureHooksJson()
        {
            if (!(Plugin.EnableCodex?.Value ?? true))
            {
                RemoveCodexHooks();
                return;
            }

            var codexHome = CodexHome();
            var hooksPath = Path.Combine(codexHome, "hooks.json");

            var scriptPath = PluginScriptPath();
            if (scriptPath == null)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(codexHome);
                JObject root;
                if (File.Exists(hooksPath))
                {
                    var original = File.ReadAllText(hooksPath);
                    try
                    {
                        root = JObject.Parse(original);
                    }
                    catch
                    {
                        var backup = hooksPath + ".chillai-bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        try
                        {
                            File.Copy(hooksPath, backup, true);
                            Log("hooks.json 解析失败，原文件已备份到: " + backup);
                        }
                        catch (Exception bex)
                        {
                            Log("hooks.json 备份失败: " + bex.Message);
                        }
                        root = new JObject();
                    }
                }
                else
                {
                    root = new JObject();
                }

                var hooks = (JObject)(root["hooks"] ?? (root["hooks"] = new JObject()));
                foreach (var evt in CodexEvents)
                {
                    var group = BuildCodexEventGroup(scriptPath, evt);
                    if (hooks[evt] is JArray existingArr)
                    {
                        var kept = existingArr.Where(g => !RefsOurScript(g)).ToList();
                        kept.Add(group);
                        hooks[evt] = new JArray(kept);
                    }
                    else
                    {
                        hooks[evt] = new JArray(group);
                    }
                }

                File.WriteAllText(hooksPath, root.ToString(Formatting.Indented));
                // 写入后自检：确保 JSON 语法有效
                JObject.Parse(File.ReadAllText(hooksPath));
                Log("已更新 " + hooksPath + "（Codex 钩子已合并配置，JSON 校验通过）");
            }
            catch (Exception ex)
            {
                Log("写入 hooks.json 失败: " + ex.Message);
            }
        }

        /// <summary>从 ~/.codex/hooks.json 摘除本插件的钩子；文件不含我们的脚本（其他插件/用户配置）则不动。</summary>
        public static void RemoveCodexHooks()
        {
            var hooksPath = Path.Combine(CodexHome(), "hooks.json");
            try
            {
                if (!File.Exists(hooksPath))
                {
                    return;
                }
                var text = File.ReadAllText(hooksPath);
                if (!text.Contains(ScriptMarker))
                {
                    return;
                }

                JObject root = null;
                try { root = JObject.Parse(text); }
                catch { root = null; }

                if (root?["hooks"] is JObject hooks)
                {
                    var changed = false;
                    foreach (var prop in hooks.Properties().ToList())
                    {
                        if (!(prop.Value is JArray arr))
                        {
                            continue;
                        }
                        var kept = arr.Where(g => !RefsOurScript(g)).ToList();
                        if (kept.Count == arr.Count)
                        {
                            continue;
                        }
                        changed = true;
                        if (kept.Count == 0)
                        {
                            prop.Remove();
                        }
                        else
                        {
                            prop.Value = new JArray(kept);
                        }
                    }

                    if (!changed)
                    {
                        return;
                    }
                    if (hooks.Properties().Count() == 0)
                    {
                        File.Delete(hooksPath);
                        Log("已移除 " + hooksPath + "（Codex 联动已关闭，配置为空）");
                    }
                    else
                    {
                        File.WriteAllText(hooksPath, root.ToString(Formatting.Indented));
                        Log("已从 " + hooksPath + " 摘除 ChillAI 钩子（其余钩子保留）");
                    }
                }
                else
                {
                    // 含我们脚本但结构无法安全编辑：视为本插件的遗留文件，删除
                    File.Delete(hooksPath);
                    Log("已删除无法解析的 " + hooksPath + "（含 ChillAI 钩子的遗留文件）");
                }
            }
            catch (Exception ex)
            {
                Log("移除 Codex hooks.json 失败: " + ex.Message);
            }
        }

        /// <summary>确保 ~/.codex/config.toml 含 [features] codex_hooks = true（CLI 通道需要）。仅 EnableCodex 开时执行。</summary>
        public static void EnsureConfigFeatures()
        {
            try
            {
                if (!(Plugin.EnableCodex?.Value ?? true))
                {
                    return; // Codex 联动关闭：不代写 features
                }

                var codexHome = CodexHome();
                var configPath = Path.Combine(codexHome, "config.toml");
                Directory.CreateDirectory(codexHome);
                var content = File.Exists(configPath) ? File.ReadAllText(configPath) : "";

                if (CodexHooksTruePattern.IsMatch(content))
                {
                    return; // 已开启
                }

                const string key = "codex_hooks = true";
                if (CodexHooksAnyPattern.IsMatch(content))
                {
                    // 存在 codex_hooks = false 之类的行 → 替换为 true
                    content = CodexHooksAnyPattern.Replace(content, key);
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
        /// 按 EnableZcode 写入或移除 ~/.zcode/cli/config.json 的 ZCode hooks。
        /// 写入时合并保留用户已有 hooks，只替换我们此前写入的条目（防重复、防路径漂移）。
        /// </summary>
        public static void EnsureZcodeHooks()
        {
            if (!(Plugin.EnableZcode?.Value ?? true))
            {
                RemoveZcodeHooks();
                return;
            }

            var scriptPath = PluginScriptPath();
            if (scriptPath == null)
            {
                return;
            }

            var configPath = ZcodeConfigPath();

            try
            {
                JObject root;
                if (File.Exists(configPath))
                {
                    var original = File.ReadAllText(configPath);
                    try
                    {
                        root = JObject.Parse(original);
                    }
                    catch
                    {
                        // 解析失败：先备份原文件再重建，绝不静默丢弃用户配置
                        var backup = configPath + ".chillai-bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        try
                        {
                            File.Copy(configPath, backup, true);
                            Log("config.json 解析失败，原文件已备份到: " + backup);
                        }
                        catch (Exception bex)
                        {
                            Log("config.json 解析失败且备份失败（将直接重建）: " + bex.Message);
                        }
                        root = new JObject();
                    }
                }
                else
                {
                    root = new JObject();
                }

                var hooks = (JObject)(root["hooks"] ?? (root["hooks"] = new JObject()));
                if (hooks["enabled"] == null)
                {
                    hooks["enabled"] = true;
                }
                else if ((hooks["enabled"] as JValue)?.Value<bool>() == false)
                {
                    // 用户可能有意关掉 ZCode hooks 总开关：尊重设置，只提示不代改
                    Log("提示：ZCode hooks 总开关（hooks.enabled）为 false，ChillAI 钩子不会执行；已尊重该设置，未自动改回");
                }
                if (hooks["timeoutMs"] == null) hooks["timeoutMs"] = 60000;
                if (hooks["maxOutputBytes"] == null) hooks["maxOutputBytes"] = 32768;

                var events = (JObject)(hooks["events"] ?? (hooks["events"] = new JObject()));
                foreach (var evt in ZcodeEvents)
                {
                    if (events[evt] is JArray existing)
                    {
                        var kept = existing.Where(g => !RefsOurScript(g)).ToList();
                        kept.Add(BuildZcodeEventGroup(scriptPath, evt));
                        events[evt] = new JArray(kept);
                    }
                    else
                    {
                        events[evt] = new JArray(BuildZcodeEventGroup(scriptPath, evt));
                    }
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

        /// <summary>从 ~/.zcode/cli/config.json 摘除本插件的 hooks 条目，用户其余配置全部保留。</summary>
        public static void RemoveZcodeHooks()
        {
            var configPath = ZcodeConfigPath();
            try
            {
                if (!File.Exists(configPath))
                {
                    return;
                }
                JObject root;
                try
                {
                    root = JObject.Parse(File.ReadAllText(configPath));
                }
                catch
                {
                    return; // 解析不了就不动，避免进一步损坏用户配置
                }
                if (!(root["hooks"]?["events"] is JObject events))
                {
                    return;
                }

                var changed = false;
                foreach (var evt in ZcodeEvents)
                {
                    if (!(events[evt] is JArray arr))
                    {
                        continue;
                    }
                    var kept = arr.Where(g => !RefsOurScript(g)).ToList();
                    if (kept.Count == arr.Count)
                    {
                        continue;
                    }
                    changed = true;
                    if (kept.Count == 0)
                    {
                        events.Remove(evt);
                    }
                    else
                    {
                        events[evt] = new JArray(kept);
                    }
                }

                if (!changed)
                {
                    return;
                }
                File.WriteAllText(configPath, root.ToString(Formatting.Indented));
                Log("已从 ~/.zcode/cli/config.json 摘除 ChillAI 钩子（其余配置保留，ZCode 联动已关闭）");
            }
            catch (Exception ex)
            {
                Log("移除 ZCode 钩子配置失败: " + ex.Message);
            }
        }

        /// <summary>ZCode 单个事件的 matcher group：单条 process 执行器（不经 shell，直接 argv 调用 powershell.exe 转发脚本）。</summary>
        private static JObject BuildZcodeEventGroup(string scriptPath, string evt)
        {
            return new JObject(
                new JProperty("matcher", ""),
                new JProperty("hooks", new JArray(
                    new JObject(
                        new JProperty("type", "process"),
                        new JProperty("command", "powershell.exe"),
                        new JProperty("args", new JArray("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", scriptPath, "-EventName", evt)),
                        new JProperty("timeoutMs", 3000)
                    )
                ))
            );
        }

        /// <summary>Codex 单个事件的 matcher group：command 执行器，命令路径指向插件目录的 codex-hook.ps1。</summary>
        private static JObject BuildCodexEventGroup(string scriptPath, string evt)
        {
            var cmd = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -EventName {evt}";
            return new JObject(
                new JProperty("hooks", new JArray(
                    new JObject(
                        new JProperty("type", "command"),
                        new JProperty("command", cmd),
                        new JProperty("timeout", 3)
                    )
                ))
            );
        }

        // ---------------- Antigravity (Google / AGY) ----------------

        /// <summary>Antigravity 识别本插件的 hook 键名。</summary>
        private const string AntigravityHookKey = "chill-ai-sync";

        /// <summary>
        /// 按 EnableAntigravity 写入或移除 ~/.gemini/config/hooks.json。
        /// 遵循 Antigravity 官方 hooks.json schema，合并保留用户其余 named hooks，
        /// 仅覆盖或摘除 chill-ai-sync 键。
        /// </summary>
        public static void EnsureAntigravityHooks()
        {
            if (!(Plugin.EnableAntigravity?.Value ?? true))
            {
                RemoveAntigravityHooks();
                return;
            }

            var scriptPath = PluginScriptPath();
            if (scriptPath == null)
            {
                return;
            }

            var configPath = AntigravityConfigPath();

            try
            {
                JObject root;
                if (File.Exists(configPath))
                {
                    var original = File.ReadAllText(configPath);
                    try
                    {
                        root = JObject.Parse(original);
                    }
                    catch
                    {
                        var backup = configPath + ".chillai-bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                        try
                        {
                            File.Copy(configPath, backup, true);
                            Log("Antigravity hooks.json 解析失败，原文件已备份到: " + backup);
                        }
                        catch (Exception bex)
                        {
                            Log("Antigravity hooks.json 备份失败: " + bex.Message);
                        }
                        root = new JObject();
                    }
                }
                else
                {
                    root = new JObject();
                }

                root[AntigravityHookKey] = BuildAntigravityHookConfig(scriptPath);

                Directory.CreateDirectory(Path.GetDirectoryName(configPath));
                File.WriteAllText(configPath, root.ToString(Formatting.Indented));

                // 自检
                JObject.Parse(File.ReadAllText(configPath));
                Log("已更新 ~/.gemini/config/hooks.json（Antigravity 钩子已合并配置，JSON 校验通过）");
            }
            catch (Exception ex)
            {
                Log("写入 Antigravity hooks 配置失败: " + ex.Message);
            }
        }

        /// <summary>从 ~/.gemini/config/hooks.json 摘除 chill-ai-sync，用户其他 named hooks 全部保留。</summary>
        public static void RemoveAntigravityHooks()
        {
            var configPath = AntigravityConfigPath();
            try
            {
                if (!File.Exists(configPath))
                {
                    return;
                }
                JObject root;
                try
                {
                    root = JObject.Parse(File.ReadAllText(configPath));
                }
                catch
                {
                    return;
                }

                if (root[AntigravityHookKey] == null)
                {
                    return;
                }

                root.Remove(AntigravityHookKey);
                if (root.Properties().Count() == 0)
                {
                    File.Delete(configPath);
                    Log("已移除 ~/.gemini/config/hooks.json（Antigravity 联动已关闭，配置为空）");
                }
                else
                {
                    File.WriteAllText(configPath, root.ToString(Formatting.Indented));
                    Log("已从 ~/.gemini/config/hooks.json 摘除 ChillAI 钩子（其余 hooks 保留）");
                }
            }
            catch (Exception ex)
            {
                Log("移除 Antigravity 钩子配置失败: " + ex.Message);
            }
        }

        /// <summary>构建 Antigravity 官方 schema 格式的 named hook 配置。</summary>
        private static JObject BuildAntigravityHookConfig(string scriptPath)
        {
            var cmdTpl = $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" -EventName ";
            return new JObject(
                new JProperty("PreInvocation", new JArray(
                    new JObject(
                        new JProperty("type", "command"),
                        new JProperty("command", cmdTpl + "UserPromptSubmit"),
                        new JProperty("timeout", 3)
                    )
                )),
                new JProperty("PostToolUse", new JArray(
                    new JObject(
                        new JProperty("matcher", "*"),
                        new JProperty("hooks", new JArray(
                            new JObject(
                                new JProperty("type", "command"),
                                new JProperty("command", cmdTpl + "PostToolUse"),
                                new JProperty("timeout", 3)
                            )
                        ))
                    )
                )),
                new JProperty("Stop", new JArray(
                    new JObject(
                        new JProperty("type", "command"),
                        new JProperty("command", cmdTpl + "Stop"),
                        new JProperty("timeout", 3)
                    )
                ))
            );
        }

        // ---------------- 通用 ----------------

        private static string CodexHome() =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

        private static string ZcodeConfigPath()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, ".zcode", "cli", "config.json");
        }

        private static string AntigravityConfigPath()
        {
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(profile, ".gemini", "config", "hooks.json");
        }

        /// <summary>该钩子条目（及其子树）是否引用了本插件的转发脚本。</summary>
        private static bool RefsOurScript(JToken token)
        {
            return token.ToString(Formatting.None).IndexOf(ScriptMarker, StringComparison.OrdinalIgnoreCase) >= 0;
        }

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

        private static void Log(string message)
        {
            Debug.Log("[ChillAI] " + message);
            Plugin.StaticLogger?.LogInfo("[ChillAI] " + message);
        }
    }
}
