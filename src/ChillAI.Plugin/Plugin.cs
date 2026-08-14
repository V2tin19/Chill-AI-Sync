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
        public const string PluginVersion = "1.0.2";

        /// <summary>供 Worker 组件复用的 BepInEx 日志器。</summary>
        public static ManualLogSource StaticLogger;

        /// <summary>启用 Codex 联动（总开关）。</summary>
        public static ConfigEntry<bool> EnableCodex;

        /// <summary>以 Codex 为主（覆盖番茄钟自动动作）。</summary>
        public static ConfigEntry<bool> CodexPrimary;

        /// <summary>显示状态浮窗。</summary>
        public static ConfigEntry<bool> ShowOverlay;

        /// <summary>启用 ZCode 联动（写入 ~/.zcode/hooks.json，需 ZCode 支持 Codex 式 Hooks）。</summary>
        public static ConfigEntry<bool> EnableZcode;

        /// <summary>自动写入 ~/.codex/hooks.json（路径指向插件目录，跨机器通用）。</summary>
        public static ConfigEntry<bool> AutoInstallHooks;

        private void Awake()
        {
            StaticLogger = Logger;
            EnableCodex = Config.Bind("General", "EnableCodex", true, "启用 Codex 联动（总开关）");
            CodexPrimary = Config.Bind("General", "CodexPrimary", true, "以 Codex 状态为主：覆盖游戏番茄钟对女主角的自动动作");
            ShowOverlay = Config.Bind("General", "ShowOverlay", true, "显示 Codex 状态浮窗");
            EnableZcode = Config.Bind("General", "EnableZcode", true, "启用 ZCode 联动：向 ~/.zcode 写入 Codex 格式 hooks.json（需 ZCode 支持该规范）");
            AutoInstallHooks = Config.Bind("General", "AutoInstallHooks", true, "自动写入 ~/.codex/hooks.json（指向插件目录内的 codex-hook.ps1）");
            Logger.LogInfo($"{PluginName} {PluginVersion} loaded (EnableCodex={EnableCodex.Value}, CodexPrimary={CodexPrimary.Value}, EnableZcode={EnableZcode.Value})");

            if (AutoInstallHooks.Value)
            {
                HooksInstaller.EnsureHooksJson();
                HooksInstaller.EnsureConfigFeatures();
            }

            var harmony = new Harmony(PluginGuid);
            InstallTickPatch(harmony);
            harmony.PatchAll(); // 应用 PomodoroOverridePatches
            StaticLogger?.LogInfo($"[ChillAI] Harmony 补丁已应用，共挂钩 {harmony.GetPatchedMethods().Count()} 个方法");
        }

        /// <summary>
        /// 用 Harmony 挂钩游戏自己的每帧方法 RoomGameManager.Update。
        /// 本游戏对 BepInEx 插件/自建组件的 Unity 生命周期消息（Start/Update/OnGUI）
        /// 一律不驱动，但 Harmony 补丁（其他 mod 大量使用）验证过 100% 有效。
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
