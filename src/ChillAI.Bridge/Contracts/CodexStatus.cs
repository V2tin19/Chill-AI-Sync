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

/// <summary>POST /codex/events 的请求体，event 为 hooks 事件名（大小写不敏感）。</summary>
public sealed record CodexEvent(string Event, string? Detail = null);

/// <summary>已收到的历史事件（诊断用）。</summary>
public sealed record CodexEventRecord(string Event, string? Detail, DateTimeOffset AtUtc, string StateAfter);

/// <summary>GET /codex/status 的响应体。</summary>
public sealed record CodexStatusDto(
    string State,
    string? Detail,
    DateTimeOffset SinceUtc,
    string? LastEvent,
    long SecondsSinceLastEvent);
