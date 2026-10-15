using ChillAI.Bridge.Contracts;

namespace ChillAI.Bridge.Services;

/// <summary>
/// Codex 状态机：把 hooks 事件流归一化为游戏可用的状态。
/// 线程安全；状态变化即被 GET /codex/status 读取。
/// </summary>
public sealed class CodexStatusStore
{
    /// <summary>JustDone 保持多久后自动衰减为 Waiting（秒）。</summary>
    public const double JustDoneLifetimeSeconds = 15;

    /// <summary>Working 状态下若超过该时长没有任何事件，判定为 Waiting（兜底防卡死，秒）。</summary>
    public const double WorkingStaleSeconds = 120;

    /// <summary>事件历史保留条数。</summary>
    public const int HistoryCapacity = 50;

    private readonly object _lock = new();
    private readonly List<CodexEventRecord> _history = new();
    private CodexState _state = CodexState.Unknown;
    private string? _detail;
    private string? _lastEvent;
    private DateTimeOffset _lastEventAtUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _changedAtUtc = DateTimeOffset.UtcNow;

    public CodexStatusDto Apply(CodexEvent evt)
    {
        var name = evt.Event?.Trim() ?? string.Empty;
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            _lastEvent = name;
            _lastEventAtUtc = now;
            _detail = evt.Detail;

            var next = Map(name);
            if (next is { } s)
            {
                if (s != _state)
                {
                    _state = s;
                    _changedAtUtc = now;
                }
                else if (s == CodexState.JustDone || s == CodexState.Working)
                {
                    // 重复的 Stop / Working：刷新计时器。
                    // JustDone 重新起算 15s 庆祝衰减；Working 刷新以重置 SinceUtc 计时。
                    _changedAtUtc = now;
                }
            }

            _history.Add(new CodexEventRecord(name, evt.Detail, now, _state.ToString().ToLowerInvariant()));
            if (_history.Count > HistoryCapacity)
            {
                _history.RemoveAt(0);
            }

            return SnapshotLocked(now);
        }
    }

    public IReadOnlyList<CodexEventRecord> History()
    {
        lock (_lock)
        {
            return _history.ToArray();
        }
    }

    public CodexStatusDto Snapshot()
    {
        lock (_lock)
        {
            return SnapshotLocked(DateTimeOffset.UtcNow);
        }
    }

    private static CodexState? Map(string eventName) => eventName.ToLowerInvariant() switch
    {
        // Codex / ZCode / Antigravity / DeepSeek Harness 工作中事件
        "sessionstart" or "userpromptsubmit" or "taskstarted" or "preinvocation" => CodexState.Working,
        "posttooluse" or "pretooluse" or "step/start" or "agent/pre-step" or "tool/call" or "tool/result" => CodexState.Working,

        // 用户审批事件（等待人机交互审批权限）
        "permissionrequest" or "approval/asked" => CodexState.WaitingReview,

        // 任务完成（触发庆祝动画，15s 后衰减为 waiting）
        "taskcomplete" or "stop" or "turn/end" => CodexState.JustDone,

        // 会话结束 / 放弃
        "turnaborted" or "sessionend" => CodexState.Idle,

        _ => null,
    };

    private CodexStatusDto SnapshotLocked(DateTimeOffset now)
    {
        var state = _state;
        if (state == CodexState.JustDone && (now - _changedAtUtc).TotalSeconds > JustDoneLifetimeSeconds)
        {
            state = CodexState.Waiting;
        }
        else if (state == CodexState.Working && (now - _lastEventAtUtc).TotalSeconds > WorkingStaleSeconds)
        {
            state = CodexState.Waiting;
        }

        return new CodexStatusDto(
            state.ToString().ToLowerInvariant(),
            _detail,
            _changedAtUtc,
            _lastEvent,
            Math.Max(0, (long)(now - _lastEventAtUtc).TotalSeconds));
    }
}
