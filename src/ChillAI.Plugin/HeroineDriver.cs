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

        /// <summary>Codex 状态变化时调用（由 StatusWorker 触发）。返回是否成功驱动或无需驱动。</summary>
        public static bool ApplyCodexState(string codexState)
        {
            var target = Map(codexState);
            if (target == null)
            {
                return true; // unknown 等不干预，保持游戏默认，视为已处理
            }

            var heroine = GetHeroine();
            if (heroine == null)
            {
                return false; // 女主角还没加载，等下次再试
            }

            try
            {
                if (heroine.GetCurrentState() == target.Value)
                {
                    return true; // 已经在目标状态，不重复调用
                }

                heroine.DebugChangeState(target.Value);
                Debug.Log($"[ChillAI] 女主角状态 -> {target.Value}");
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.Log("[ChillAI] 驱动女主角失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 温和校正（每 ~4 秒由 StatusWorker 调用）：
        /// - 工具 working → 女主必须处于工作状态（WorkPC），防止被番茄钟/游戏内部漂移；
        /// - 工具非工作（waiting/idle/justdone/waitingreview）→ 女主若在"干活"（Work*）则拉回默认 None（停手、回到自然状态）。
        ///   不做"强制喝茶/强制动作"——女主不在工作状态时保持原样。
        /// 仅在有真实工具数据时生效：Bridge 不可达或尚无任何事件（unknown）→ 不干预，交还游戏原生行为。
        /// </summary>
        public static void Reassert()
        {
            if (!StatusWorker.BridgeConnected || StatusWorker.CurrentCodexState == "unknown")
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

        /// <summary>
        /// 一次性释放工具驱动的状态（Bridge 断连、所有联动关闭等"工具状态失效"场景）：
        /// 女主若处于 Work*（工具驱动的工作）或 WantTalk（等待审批提醒），拉回默认 None。
        /// 不动 WildStretchFullBody（庆祝动画会自然结束）。
        /// </summary>
        public static void ReleaseToolDrivenState()
        {
            var heroine = GetHeroine();
            if (heroine == null)
            {
                return;
            }
            try
            {
                var current = heroine.GetCurrentState();
                if (current == HeroineAI.ActionStateType.WorkPC
                    || current == HeroineAI.ActionStateType.WorkBook
                    || current == HeroineAI.ActionStateType.WorkReport
                    || current == HeroineAI.ActionStateType.WantTalk)
                {
                    heroine.DebugChangeState(HeroineAI.ActionStateType.None);
                    Debug.Log("[ChillAI] 工具状态失效，女主角 -> 默认（释放工具驱动状态）");
                }
            }
            catch (System.Exception ex)
            {
                Debug.Log("[ChillAI] 释放工具驱动状态失败: " + ex.Message);
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
        /// 找到后每 120 帧（~2 秒）验证一次引用；未找到时至少间隔 60 帧（~1 秒）重试，避免逐帧全场景搜索造成卡顿。
        /// </summary>
        private static HeroineAI GetHeroine()
        {
            int currentFrame = Time.frameCount;
            if (_heroine != null && currentFrame - _lastFindFrame <= 120)
            {
                return _heroine;
            }
            if (_heroine == null && _lastFindFrame >= 0 && currentFrame - _lastFindFrame <= 60)
            {
                return null;
            }

            _lastFindFrame = currentFrame;
            _heroine = Object.FindFirstObjectByType<HeroineAI>();
            if (_heroine != null)
            {
                Debug.Log("[ChillAI] 已找到女主角 HeroineAI");
            }
            return _heroine;
        }
    }
}
