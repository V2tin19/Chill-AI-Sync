using UnityEngine;

namespace ChillAI.Plugin
{
    /// <summary>
    /// 阶段 1：把 Codex 状态翻译成女主角（HeroineAI）的原生行为。
    /// 找到女主角实例 → 状态变化时调用 DebugChangeState 驱动原生状态机
    /// （动画/语音由游戏原生状态类处理，我们只按按钮）。
    /// </summary>
    public static class HeroineDriver
    {
        private static HeroineAI _heroine;
        private static int _lastFindFrame = -1;

        /// <summary>Codex 状态变化时调用（由 StatusWorker 触发）。</summary>
        public static void ApplyCodexState(string codexState)
        {
            var target = Map(codexState);
            if (target == null)
            {
                return; // unknown 等不干预，保持游戏默认
            }

            var heroine = GetHeroine();
            if (heroine == null)
            {
                return; // 女主角还没加载，等下次再试
            }

            try
            {
                if (heroine.GetCurrentState() == target.Value)
                {
                    return; // 已经在目标状态，不重复调用
                }

                heroine.DebugChangeState(target.Value);
                Debug.Log($"[ChillAI] 女主角状态 -> {target.Value}");
            }
            catch (System.Exception ex)
            {
                Debug.Log("[ChillAI] 驱动女主角失败: " + ex.Message);
            }
        }

        /// <summary>
        /// 强制校正：不管游戏内部（番茄钟等）把她改成了什么状态，
        /// 只要和 Codex 期望状态不一致就拉回来。每 ~2 秒由 StatusWorker 调用一次。
        /// </summary>
        public static void Reassert()
        {
            var target = Map(StatusWorker.CurrentCodexState);
            if (target == null)
            {
                return;
            }

            var heroine = GetHeroine();
            if (heroine == null)
            {
                return;
            }

            try
            {
                if (heroine.GetCurrentState() == target.Value)
                {
                    return;
                }

                heroine.DebugChangeState(target.Value);
                Debug.Log($"[ChillAI] 强制校正女主角状态 -> {target.Value}（覆盖游戏内部动作）");
            }
            catch (System.Exception ex)
            {
                Debug.Log("[ChillAI] 强制校正失败: " + ex.Message);
            }
        }

        private static HeroineAI.ActionStateType? Map(string codexState)
        {
            switch (codexState)
            {
                case "working": return HeroineAI.ActionStateType.WorkPC;          // Codex 干活 → 她在电脑前工作
                case "waiting": return HeroineAI.ActionStateType.BreakTeaTime;    // 休息 → 喝茶
                case "justdone": return HeroineAI.ActionStateType.WildStretchFullBody; // 刚完成 → 伸展庆祝
                case "waitingreview": return HeroineAI.ActionStateType.WantTalk;  // 等你审批 → 想找你说话
                case "idle": return HeroineAI.ActionStateType.WorkPC;             // 空闲 → 默认陪伴工作
                default: return null;
            }
        }

        /// <summary>
        /// 全场景按脚本类型找女主角（不需要知道对象名）。
        /// 每 2 秒刷新一次引用，防止切场景后失效。
        /// </summary>
        private static HeroineAI GetHeroine()
        {
            if (_heroine == null || Time.frameCount - _lastFindFrame > 120)
            {
                _heroine = Object.FindFirstObjectByType<HeroineAI>();
                _lastFindFrame = Time.frameCount;
                if (_heroine != null)
                {
                    Debug.Log("[ChillAI] 已找到女主角 HeroineAI");
                }
            }
            return _heroine;
        }
    }
}
