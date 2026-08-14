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
        /// 温和校正（每 ~4 秒由 StatusWorker 调用）：
        /// - 工具 working → 女主必须处于工作状态（WorkPC），防止被番茄钟/游戏内部漂移；
        /// - 工具非工作（waiting/idle/unknown/justdone/waitingreview）→ 女主若在"干活"（Work*）则拉回默认 None（停手、回到自然状态）。
        ///   不做"强制喝茶/强制动作"——女主不在工作状态时保持原样。
        /// </summary>
        public static void Reassert()
        {
            var heroine = GetHeroine();
            if (heroine == null)
            {
                return;
            }

            try
            {
                var current = heroine.GetCurrentState();
                if (StatusWorker.CurrentCodexState == "working")
                {
                    if (current != HeroineAI.ActionStateType.WorkPC)
                    {
                        heroine.DebugChangeState(HeroineAI.ActionStateType.WorkPC);
                        Debug.Log("[ChillAI] 校正女主角 -> 工作（工具 working）");
                    }
                    return;
                }

                // 工具不工作：女主若在干活 → 拉回默认（None），只"停手"，不指定动作
                if (current == HeroineAI.ActionStateType.WorkPC
                    || current == HeroineAI.ActionStateType.WorkBook
                    || current == HeroineAI.ActionStateType.WorkReport)
                {
                    heroine.DebugChangeState(HeroineAI.ActionStateType.None);
                    Debug.Log("[ChillAI] 校正女主角 -> 默认（工具空闲，停止干活）");
                }
            }
            catch (System.Exception ex)
            {
                Debug.Log("[ChillAI] 温和校正失败: " + ex.Message);
            }
        }

        private static HeroineAI.ActionStateType? Map(string codexState)
        {
            switch (codexState)
            {
                case "working": return HeroineAI.ActionStateType.WorkPC;          // 工具干活 → 她在电脑前工作
                case "waiting": return HeroineAI.ActionStateType.None;            // 休息 → 回默认自然状态（不再强制喝茶）
                case "justdone": return HeroineAI.ActionStateType.WildStretchFullBody; // 刚完成 → 伸展庆祝
                case "waitingreview": return HeroineAI.ActionStateType.WantTalk;  // 等你审批 → 想找你说话
                case "idle": return HeroineAI.ActionStateType.None;               // 空闲 → 回默认自然状态（不工作）
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
