using System;
using System.Linq;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace ChillAI.Plugin
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInProcess("Chill With You.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.haikisha.chillai";
        public const string PluginName = "Chill AI";
        public const string PluginVersion = "1.0.8";

        /// <summary>供 Worker 组件复用的 BepInEx 日志器。</summary>
        public static ManualLogSource StaticLogger;

        /// <summary>启用 Codex 联动（总开关）。</summary>
        public static ConfigEntry<bool> EnableCodex;

        /// <summary>工具优先：任一已启用工具（Codex/ZCode）的状态优先，覆盖番茄钟自动动作。</summary>
        public static ConfigEntry<bool> ToolPrimary;

        /// <summary>显示状态浮窗。</summary>
        public static ConfigEntry<bool> ShowOverlay;

        /// <summary>启用 ZCode 联动（写入 ~/.zcode/cli/config.json，Claude Code hook schema）。</summary>
        public static ConfigEntry<bool> EnableZcode;

        /// <summary>启用 Antigravity 联动（写入 ~/.gemini/config/hooks.json）。</summary>
        public static ConfigEntry<bool> EnableAntigravity;

        /// <summary>自动写入 AI 工具钩子配置（~/.codex/hooks.json + ~/.zcode/cli/config.json + ~/.gemini/config/hooks.json，路径指向插件目录，跨机器通用）。</summary>
        public static ConfigEntry<bool> AutoInstallHooks;

        private void Awake()
        {
            StaticLogger = Logger;
            EnableCodex = Config.Bind("General", "EnableCodex", true, "启用 Codex 联动（总开关）");
            EnableZcode = Config.Bind("General", "EnableZcode", true, "启用 ZCode 联动：向 ~/.zcode/cli/config.json 写入 Claude Code 格式 hooks");
            EnableAntigravity = Config.Bind("General", "EnableAntigravity", true, "启用 Antigravity 联动：向 ~/.gemini/config/hooks.json 写入生命周期 hooks");
            ToolPrimary = Config.Bind("General", "ToolPrimary", true, "工具优先：已启用工具（Codex/ZCode/Antigravity）的状态优先，覆盖游戏番茄钟对女主角的自动动作");
            ShowOverlay = Config.Bind("General", "ShowOverlay", true, "显示状态浮窗");
            AutoInstallHooks = Config.Bind("General", "AutoInstallHooks", true, "自动写入 AI 工具钩子配置（指向插件目录内的 codex-hook.ps1）");
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded (EnableCodex={EnableCodex.Value}, EnableZcode={EnableZcode.Value}, EnableAntigravity={EnableAntigravity.Value}, ToolPrimary={ToolPrimary.Value})");

            if (AutoInstallHooks.Value)
            {
                // 各函数按各自开关 reconcile：开 → 写入/更新，关 → 摘除我们的钩子（保留用户自己的）
                HooksInstaller.EnsureHooksJson();          // Codex: ~/.codex/hooks.json（受 EnableCodex 控制）
                HooksInstaller.EnsureZcodeHooks();         // ZCode: ~/.zcode/cli/config.json（受 EnableZcode 控制）
                HooksInstaller.EnsureAntigravityHooks();    // Antigravity: ~/.gemini/config/hooks.json（受 EnableAntigravity 控制）
                HooksInstaller.EnsureConfigFeatures();     // Codex: ~/.codex/config.toml（仅 EnableCodex 开时）
            }

            var harmony = new Harmony(PluginGuid);
            InstallTickPatch(harmony);
            InstallPomodoroPatchesSafely(harmony); // 逐补丁容错，替代一次性 PatchAll
            StaticLogger?.LogInfo($"[ChillAI] Harmony 补丁已应用，共挂钩 {harmony.GetPatchedMethods().Count()} 个方法");
        }

        /// <summary>
        /// 逐个应用番茄钟/自言自语补丁（PomodoroOverridePatches 的嵌套补丁类）：
        /// 游戏更新改了类型/签名时，只损失单个补丁并记录警告，不让整个插件半初始化。
        /// </summary>
        private static void InstallPomodoroPatchesSafely(Harmony harmony)
        {
            var patchTypes = typeof(PomodoroOverridePatches).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
            int ok = 0, fail = 0;
            foreach (var type in patchTypes)
            {
                bool isPatchType;
                try
                {
                    isPatchType = type.IsClass && type.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0;
                }
                catch (Exception ex)
                {
                    fail++;
                    StaticLogger?.LogWarning($"[ChillAI] 补丁类型 {type.Name} 读取失败（游戏版本可能已更新）: {ex.Message}");
                    continue;
                }
                if (!isPatchType)
                {
                    continue;
                }
                try
                {
                    harmony.CreateClassProcessor(type).Patch();
                    ok++;
                }
                catch (Exception ex)
                {
                    fail++;
                    StaticLogger?.LogWarning($"[ChillAI] 补丁 {type.Name} 应用失败（游戏版本可能已更新）: {ex.Message}");
                }
            }
            StaticLogger?.LogInfo($"[ChillAI] 番茄钟/自言自语补丁应用完成：成功 {ok}，失败 {fail}");
        }

        /// <summary>
        /// 用 Harmony 挂钩游戏自己的每帧方法 RoomGameManager.Update。
        /// 本游戏对 BepInEx 插件/自建组件的 Unity 生命周期消息驱动不可靠（不同版本行为不一），
        /// 因此每帧驱动统一走 Harmony 补丁；其余初始化（客户端、Bridge 拉起）全部在 Tick 内惰性完成，
        /// 不依赖 OnEnable/Start 一定会被调用。
        /// </summary>
        private static void InstallTickPatch(Harmony harmony)
        {
            try
            {
                var gameAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "Assembly-CSharp");
                if (gameAsm == null)
                {
                    StaticLogger?.LogWarning("[ChillAI] 未找到 Assembly-CSharp");
                    return;
                }

                var roomType = gameAsm.GetType("Bulbul.RoomGameManager");
                var updateMethod = roomType?.GetMethod("Update",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (updateMethod == null)
                {
                    StaticLogger?.LogWarning("[ChillAI] 未找到 RoomGameManager.Update");
                    return;
                }

                harmony.Patch(updateMethod, postfix: new HarmonyMethod(
                    typeof(Plugin).GetMethod(nameof(TickPostfix), BindingFlags.Static | BindingFlags.NonPublic)));
                StaticLogger?.LogInfo("[ChillAI] 已挂钩 RoomGameManager.Update（每帧驱动）");
            }
            catch (Exception ex)
            {
                StaticLogger?.LogError("[ChillAI] 挂钩失败: " + ex);
            }
        }

        private static void TickPostfix()
        {
            StatusWorker.EnsureInstance();
            StatusWorker.Instance?.Tick();
        }
    }
}
