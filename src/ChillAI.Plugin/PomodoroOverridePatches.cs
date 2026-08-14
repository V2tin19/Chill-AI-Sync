using HarmonyLib;
using UnityEngine;

namespace ChillAI.Plugin
{
    /// <summary>
    /// 以 Codex 为主模式：拦截游戏番茄钟对女主角的自动动作驱动。
    /// ★ 真实链路（IL 反编译确认）：PomodoroService.OnTimerEnd/StartPomodoro
    ///   → HeroineService.StartPomodoroTimer/OnPomodoroWorkEnd/OnPomodoroBreakTimeEnd/OnPomodoroComplete
    ///   → HeroineAI.ChangePomodoroActionAsync() ← 番茄钟动作的唯一汇聚入口
    /// 旧的 ReadyChangePomodoroState/StartWork/StartBreak 在程序集内无调用点，保留作兜底。
    /// 所有拦截受 EnableCodex 与 CodexPrimary 双重控制。
    /// </summary>
    public static class PomodoroOverridePatches
    {
        private static float _lastLogTime = -100f;

        private static bool Enabled()
        {
            return (Plugin.EnableCodex?.Value ?? true) && (Plugin.CodexPrimary?.Value ?? true);
        }

        private static void ThrottledLog(string message)
        {
            if (Time.unscaledTime - _lastLogTime < 5f)
            {
                return;
            }
            _lastLogTime = Time.unscaledTime;
            Plugin.StaticLogger?.LogInfo("[ChillAI] " + message);
        }

        /// <summary>番茄钟动作的真正入口：开始/切换工作/休息/完成全部汇聚于此。以 Codex 为主时一律拦截，女主状态改由 Codex 驱动（Reassert 每 2s 校正）。</summary>
        [HarmonyPatch(typeof(HeroineAI), nameof(HeroineAI.ChangePomodoroActionAsync))]
        public static class ChangePomodoroActionPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                if (!Enabled())
                {
                    return true;
                }
                ThrottledLog("拦截番茄钟自动动作（以 Codex 为主，女主状态由 Codex 驱动）");
                return false;
            }
        }

        /// <summary>番茄钟状态切换的真正入口（私有方法，IL 确认无调用点，保留兜底）。</summary>
        [HarmonyPatch(typeof(HeroineAI), "ReadyChangePomodoroState")]
        public static class ReadyChangePomodoroStatePatch
        {
            [HarmonyPrefix]
            private static bool Prefix(Bulbul.PomodoroService.PomodoroType nextPomodoroType)
            {
                if (!Enabled())
                {
                    return true;
                }
                var codex = StatusWorker.CurrentCodexState;
                var typeName = nextPomodoroType.ToString();
                if (typeName == "Work" && codex != "working")
                {
                    ThrottledLog($"拦截番茄钟→工作（Codex={codex}，番茄钟动作被抑制）");
                    return false;
                }
                if (typeName == "Break" && codex == "working")
                {
                    ThrottledLog("拦截番茄钟→休息（Codex 正在工作）");
                    return false;
                }
                return true;
            }
        }

        [HarmonyPatch(typeof(HeroineAI), nameof(HeroineAI.StartWork))]
        public static class StartWorkPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                if (!Enabled())
                {
                    return true;
                }
                if (StatusWorker.CurrentCodexState == "working")
                {
                    return true;
                }
                ThrottledLog("拦截 StartWork（Codex 未在工作）");
                return false;
            }
        }

        [HarmonyPatch(typeof(HeroineAI), nameof(HeroineAI.StartBreak))]
        public static class StartBreakPatch
        {
            [HarmonyPrefix]
            private static bool Prefix()
            {
                if (!Enabled())
                {
                    return true;
                }
                if (StatusWorker.CurrentCodexState != "working")
                {
                    return true;
                }
                ThrottledLog("拦截 StartBreak（Codex 正在工作）");
                return false;
            }
        }
    }
}
