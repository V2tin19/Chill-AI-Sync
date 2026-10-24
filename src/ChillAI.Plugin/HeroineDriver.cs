using UnityEngine;

namespace ChillAI.Plugin
{
    /// <summary>
    /// 把工具状态翻译成女主角（HeroineAI）的原生行为。
    /// 找到女主角实例 → 状态变化时调用 DebugChangeState 驱动原生状态机
    /// （动画/语音由游戏原生状态类处理，我们只按按钮）。
    /// </summary>
    public static class HeroineDriver
    {
        private static HeroineAI _heroine;
        private static int _lastFindFrame = -1;

        /// <summary>
        /// 诊断日志。
        /// ★ 必须同时走 BepInEx 的 logger：这个工程里 UnityEngine.Debug.Log 的调用会被
        ///   编译器整个去掉（连字符串字面量一起），只写 Debug.Log 的话，驱动层在日志里
        ///   完全不可见——之前排查"女主不进想找你说话"时就是因为这个抓瞎。
        /// </summary>
        private static void Log(string message)
        {
            Debug.Log("[ChillAI] " + message);
            Plugin.StaticLogger?.LogInfo("[ChillAI] " + message);
        }

        /// <summary>Codex 状态变化时调用（由 StatusWorker 触发）。返回是否成功驱动或无需驱动。</summary>
        public static bool ApplyCodexState(string codexState)
        {
            var target = Map(codexState);
            if (target == null)
            {
                Log($"工具状态 {codexState} 不需要驱动女主（无对应映射）");
                return true; // unknown 等不干预，保持游戏默认，视为已处理
            }

            var heroine = GetHeroine();
            if (heroine == null)
            {
                return false; // 女主角还没加载，等下次再试（每帧都会调用，故不刷日志）
            }

            try
            {
                var current = heroine.GetCurrentState();
                if (current == target.Value)
                {
                    Log($"工具状态 {codexState} → 女主已是 {target.Value}，无需切换");
                    return true; // 已经在目标状态，不重复调用
                }

                heroine.DebugChangeState(target.Value);
                // 调用后立刻回读：如果游戏没接受（例如该状态需要前置条件），这里就能看出来
                var after = heroine.GetCurrentState();
                Log($"工具状态 {codexState} → 女主 {current} -> {target.Value}（调用后回读 {after}）");
                return true;
            }
            catch (System.Exception ex)
            {
                Log("驱动女主角失败: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 温和校正（每 ~4 秒由 StatusWorker 调用）：
        /// - 工具 working → 女主必须处于工作状态（WorkPC），防止被番茄钟/游戏内部漂移；
        /// - 工具 waitingreview（等你审批 / 智能体提问）→ 女主必须处于"想找你说话"（WantTalk）。
        ///   这一条是补上的：原先只维持 working，于是她在等你的期间会被游戏自身逻辑带回闲置，
        ///   从画面上完全看不出"它在等你"。
        /// - 工具非工作（waiting/idle/justdone）→ 女主若在"干活"（Work*）则拉回默认 None（停手、回到自然状态）。
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
                var state = StatusWorker.CurrentCodexState;
                var current = heroine.GetCurrentState();

                if (state == "working")
                {
                    if (current != HeroineAI.ActionStateType.WorkPC)
                    {
                        heroine.DebugChangeState(HeroineAI.ActionStateType.WorkPC);
                        Log($"校正女主 -> 工作（工具 working，原状态 {current}）");
                    }
                    return;
                }

                if (state == "waitingreview")
                {
                    // 只在"她不在想找你说话"时才纠正；已经在里面就不重复触发，避免动画被反复重启
                    if (current != HeroineAI.ActionStateType.WantTalk)
                    {
                        heroine.DebugChangeState(HeroineAI.ActionStateType.WantTalk);
                        Log($"校正女主 -> 想找你说话（工具在等你，原状态 {current}）");
                    }
                    return;
                }

                // 工具不工作：女主若在干活 → 拉回默认（None），只"停手"，不指定动作
                if (current == HeroineAI.ActionStateType.WorkPC
                    || current == HeroineAI.ActionStateType.WorkBook
                    || current == HeroineAI.ActionStateType.WorkReport)
                {
                    heroine.DebugChangeState(HeroineAI.ActionStateType.None);
                    Log($"校正女主 -> 默认（工具空闲，停止干活，原状态 {current}）");
                }
            }
            catch (System.Exception ex)
            {
                Log("温和校正失败: " + ex.Message);
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
                    Log($"工具状态失效，女主 {current} -> 默认（释放工具驱动状态）");
                }
            }
            catch (System.Exception ex)
            {
                Log("释放工具驱动状态失败: " + ex.Message);
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
                Log("已找到女主角 HeroineAI");
            }
            return _heroine;
        }
    }
}
