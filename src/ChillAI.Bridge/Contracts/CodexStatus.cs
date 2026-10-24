namespace ChillAI.Bridge.Contracts;

/// <summary>Codex 工作状态（游戏侧按此映射角色行为）。</summary>
public enum CodexState
{
    /// <summary>初始 / 未收到任何事件。</summary>
    Unknown,

    /// <summary>任务执行中 → 游戏 Focus 模式（角色安静陪伴）。</summary>
    Working,

    /// <summary>等待用户输入 / 空闲 → 游戏 Break 模式（角色活泼互动）。</summary>
    Waiting,

    /// <summary>正在等待用户审批 → 游戏提醒（“有个决定需要你”）。</summary>
    WaitingReview,

    /// <summary>刚完成一轮任务 → 游戏庆祝（短暂状态，超时自动衰减为 Waiting）。</summary>
    JustDone,

    /// <summary>会话已结束。</summary>
    Idle,
}

/// <summary>
/// POST /codex/events 的请求体。
/// <paramref name="Event"/> 为 hooks 事件名（大小写不敏感）。
/// <paramref name="Source"/> 为上报来源（工具名，如 codex / zcode / antigravity / dsh）；
/// 省略时归入 "default" 单一来源——此时行为与旧版完全一致（谁最后发事件听谁的）。
/// 多个来源各自记账后聚合，避免同时开两个工具时状态互相打架。
/// </summary>
public sealed record CodexEvent(string Event, string? Detail = null, string? Source = null);

/// <summary>已收到的历史事件（诊断用）。</summary>
public sealed record CodexEventRecord(string Event, string? Detail, DateTimeOffset AtUtc, string StateAfter, string? Source = null);

/// <summary>GET /codex/status 的响应体。</summary>
public sealed record CodexStatusDto(
    string State,
    string? Detail,
    DateTimeOffset SinceUtc,
    string? LastEvent,
    long SecondsSinceLastEvent);
