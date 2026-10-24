using System;
using System.Collections.Generic;

namespace ChillAI.Plugin
{
    /// <summary>
    /// 状态浮窗与设置窗口的**文案**（纯函数，不依赖 Unity/BepInEx，可单独编译测试）。
    ///
    /// 设计原则（面向玩家，不是面向开发者）：
    /// - 只说中文，不暴露 PostToolUse / agent/pre-step 这类内部事件名；
    /// - 先给结论（状态），再给依据（谁在驱动、最近发生了什么），最后给操作提示；
    /// - 时间说人话（"刚刚 / 3 秒前 / 2 分钟前"），不写 "距今 3s"；
    /// - 只在真的可能发生时提示（例如"动作抽风属正常"只在工具优先开启时出现）；
    /// - 缺信息就不占行（没声明来源的工具就不显示"驱动中"那一行）。
    /// </summary>
    public static class OverlayText
    {
        // 浮窗布局常量（与 StatusWorker 的绘制保持一致，放在这里便于测试"行数→高度"）
        public const float OverlayWidth = 380f;
        public const float PadX = 12f;
        public const float PadTop = 8f;
        public const float TitleHeight = 22f;
        public const float InfoHeight = 17f;
        public const float HintHeight = 16f;
        public const float Gap = 4f;

        /// <summary>浮窗要显示的内容：一行标题 + 若干说明行 + 一行操作提示。</summary>
        public sealed class Content
        {
            public string Title = "";
            public readonly List<string> Infos = new List<string>();
            public string Hint = "";
        }

        /// <summary>
        /// 组装浮窗文案。
        /// </summary>
        /// <param name="anyToolOn">是否有任一工具联动开启。</param>
        /// <param name="connected">Bridge 是否可达。</param>
        /// <param name="state">Bridge 状态（working/waiting/...）。</param>
        /// <param name="source">当前驱动来源（codex/zcode/antigravity/dsh，可为空）。</param>
        /// <param name="lastEvent">最近事件名（可为空）。</param>
        /// <param name="detail">最近事件细节（通常是被调用的工具名，可为空）。</param>
        /// <param name="error">连接错误信息（可为空）。</param>
        /// <param name="secondsSinceLastEvent">距最近事件的秒数。</param>
        /// <param name="toolPrimary">「工具优先」是否开启。</param>
        public static Content Build(
            bool anyToolOn,
            bool connected,
            string state,
            string source,
            string lastEvent,
            string detail,
            string error,
            long secondsSinceLastEvent,
            bool toolPrimary)
        {
            var content = new Content();

            if (!anyToolOn)
            {
                content.Title = "○ 未启用联动";
                content.Infos.Add("所有 AI 工具联动都关着");
                content.Hint = "按 F8 打开设置";
                return content;
            }

            if (!connected)
            {
                content.Title = "● 未连接本地服务";
                content.Infos.Add("Bridge 没在运行（游戏启动时会自动拉起）");
                if (!string.IsNullOrEmpty(error))
                {
                    content.Infos.Add("错误：" + Truncate(error, 40));
                }
                content.Hint = "按 F8 打开设置 · 出问题双击插件目录的 doctor.bat";
                return content;
            }

            content.Title = "● " + StateToChinese(state);

            var sourceName = SourceToDisplayName(source);
            if (!string.IsNullOrEmpty(sourceName))
            {
                content.Infos.Add("驱动中：" + sourceName);
            }

            if (!string.IsNullOrEmpty(lastEvent))
            {
                content.Infos.Add("最近：" + EventToChinese(lastEvent) + "（" + FriendlyAge(secondsSinceLastEvent) + "）");
            }

            if (!string.IsNullOrEmpty(detail))
            {
                content.Infos.Add("最近工具：" + Truncate(detail, 40));
            }

            // "动作抽风属正常"只在工具优先真的开着时才可能出现，否则不提
            content.Hint = toolPrimary
                ? "工具优先已开 · 动作抽风属正常 · F8 设置"
                : "按 F8 打开设置";

            return content;
        }

        /// <summary>按实际行数算浮窗高度（内容行数会变，固定高度会留白或截断）。</summary>
        public static float Height(Content content)
        {
            var infoHeight = content.Infos.Count > 0
                ? content.Infos.Count * InfoHeight + Gap
                : 0f;
            return PadTop + TitleHeight + infoHeight + HintHeight + 8f;
        }

        /// <summary>设置窗口顶部那行状态摘要。</summary>
        public static string ConfigStatusLine(
            bool connected, string state, string source, string lastEvent, long secondsSinceLastEvent)
        {
            if (!connected)
            {
                return "状态：未连接（Bridge 没在运行）";
            }

            var text = "状态：" + StateToChinese(state);
            var sourceName = SourceToDisplayName(source);
            if (!string.IsNullOrEmpty(sourceName))
            {
                text += " · " + sourceName;
            }
            if (!string.IsNullOrEmpty(lastEvent))
            {
                text += " · " + FriendlyAge(secondsSinceLastEvent);
            }
            return text;
        }

        public static string StateToChinese(string state)
        {
            switch (state)
            {
                case "working": return "正在工作";
                case "justdone": return "刚完成任务";
                case "waiting": return "休息 / 待命中";
                case "waitingreview": return "等待你审批";
                case "idle": return "空闲";
                case "unknown": return "等待第一个事件";
                default: return string.IsNullOrEmpty(state) ? "未知" : state;
            }
        }

        /// <summary>把内部钩子事件名翻成用户看得懂的中文（认不出就原样显示，便于排查）。</summary>
        public static string EventToChinese(string eventName)
        {
            switch (eventName)
            {
                case "SessionStart": return "会话开始";
                case "UserPromptSubmit": return "收到你的任务";
                case "PreToolUse": return "准备调用工具";
                case "PostToolUse": return "工具调用完成";
                case "PermissionRequest": return "请求你授权";
                case "approval/decided": return "你已答复，继续干活";
                case "Stop": return "一轮任务结束";
                case "SessionEnd": return "会话结束";
                case "taskstarted": return "任务开始";
                case "taskcomplete": return "任务完成";
                case "turnaborted": return "任务已中止";
                case "preinvocation": return "任务开始";
                // dsh 的 session 事件名（转发时原样上报）
                case "agent/pre-step": return "收到你的任务";
                case "step/start": return "开始新步骤";
                case "tool/call": return "调用工具";
                case "tool/result": return "工具返回结果";
                case "approval/asked": return "请求你授权";
                case "turn/end": return "一轮任务结束";
                default: return eventName;
            }
        }

        /// <summary>把来源标识翻成工具显示名；未标注来源（老脚本/手写配置）返回空串，界面就不显示这一行。</summary>
        public static string SourceToDisplayName(string source)
        {
            if (string.IsNullOrEmpty(source))
            {
                return "";
            }
            switch (source.ToLowerInvariant())
            {
                case "codex": return "Codex";
                case "zcode": return "ZCode";
                case "antigravity": return "Antigravity";
                case "dsh": return "DeepSeek Harness";
                case "default": return "";
                default: return source;
            }
        }

        /// <summary>把"距今多少秒"说成人话。</summary>
        public static string FriendlyAge(long seconds)
        {
            if (seconds < 2)
            {
                return "刚刚";
            }
            if (seconds < 60)
            {
                return seconds + " 秒前";
            }
            if (seconds < 3600)
            {
                return (seconds / 60) + " 分钟前";
            }
            return (seconds / 3600) + " 小时前";
        }

        public static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
            {
                return text ?? "";
            }
            return text.Substring(0, max) + "...";
        }
    }
}
