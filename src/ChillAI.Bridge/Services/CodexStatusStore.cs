using ChillAI.Bridge.Contracts;

namespace ChillAI.Bridge.Services;

/// <summary>
/// 工具状态机：把各 AI 工具上报的钩子事件流归一化为游戏可用的状态。
///
/// 关键点：**按来源（工具）分别记账，再聚合**。
/// 早期实现只有一个全局状态，谁最后发事件就听谁的——同时开两个工具时会互相打架：
/// 例如 Codex 正在跑任务，dsh 那边一个 Stop 就能把状态改成"刚完成"（女主去庆祝），
/// 或者 dsh 会话结束（idle）会让正在干活的 Codex 状态被清掉，女主直接停手。
/// 现在每个来源各留一份状态，聚合规则见 <see cref="Priority"/>：
/// 只要有工具在工作就是工作，其次等审批、其次刚完成、再次待命、最后空闲。
///
/// 兼容性：不传 source 的上报（老版本转发脚本、手写配置）全部归入 "default" 这一个来源，
/// 聚合结果与旧行为完全一致。
///
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

    /// <summary>未标注来源的上报归入这个来源名。</summary>
    public const string DefaultSource = "default";

    /// <summary>同时记账的来源数上限（防止异常客户端无限制造来源名）。超出时淘汰最久未更新的。</summary>
    private const int MaxSources = 16;

    private readonly object _lock = new();
    private readonly List<CodexEventRecord> _history = new();
    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.OrdinalIgnoreCase);

    private CodexState _aggregateState = CodexState.Unknown;
    private DateTimeOffset _aggregateChangedAtUtc = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastEventAtUtc = DateTimeOffset.UtcNow;
    private string? _lastEvent;
    private string? _detail;

    /// <summary>单个来源（工具）的状态。</summary>
    private sealed class SourceState
    {
        public CodexState State = CodexState.Unknown;
        public DateTimeOffset ChangedAtUtc = DateTimeOffset.UtcNow;
        public DateTimeOffset LastEventAtUtc = DateTimeOffset.UtcNow;
        public string? LastEvent;
        public string? Detail;
    }

    public CodexStatusDto Apply(CodexEvent evt)
    {
        var name = evt.Event?.Trim() ?? string.Empty;
        var source = string.IsNullOrWhiteSpace(evt.Source) ? DefaultSource : evt.Source!.Trim();
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            _lastEvent = name;
            _lastEventAtUtc = now;
            _detail = evt.Detail;

            var state = GetOrAddSource(source, now);
            state.LastEvent = name;
            state.LastEventAtUtc = now;
            state.Detail = evt.Detail;

            var next = Map(name);
            if (next is { } s)
            {
                if (s != state.State)
                {
                    state.State = s;
                    state.ChangedAtUtc = now;
                }
                else if (s == CodexState.JustDone || s == CodexState.Working)
                {
                    // 重复的 Stop / Working：刷新计时器。
                    // JustDone 重新起算 15s 庆祝衰减；Working 刷新以重置空闲计时。
                    state.ChangedAtUtc = now;
                }
            }

            var snapshot = SnapshotLocked(now);
            _history.Add(new CodexEventRecord(name, evt.Detail, now, snapshot.State, source));
            if (_history.Count > HistoryCapacity)
            {
                _history.RemoveAt(0);
            }

            return snapshot;
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

    /// <summary>取来源状态；不存在则新建（超出上限时淘汰最久未更新的那个）。</summary>
    private SourceState GetOrAddSource(string source, DateTimeOffset now)
    {
        if (_sources.TryGetValue(source, out var existing))
        {
            return existing;
        }

        if (_sources.Count >= MaxSources)
        {
            string? oldestKey = null;
            var oldestAt = DateTimeOffset.MaxValue;
            foreach (var pair in _sources)
            {
                if (pair.Value.LastEventAtUtc < oldestAt)
                {
                    oldestAt = pair.Value.LastEventAtUtc;
                    oldestKey = pair.Key;
                }
            }
            if (oldestKey != null)
            {
                _sources.Remove(oldestKey);
            }
        }

        var created = new SourceState { LastEventAtUtc = now };
        _sources[source] = created;
        return created;
    }

    /// <summary>单个来源经过衰减后的有效状态。</summary>
    private static CodexState Effective(SourceState state, DateTimeOffset now)
    {
        if (state.State == CodexState.JustDone
            && (now - state.ChangedAtUtc).TotalSeconds > JustDoneLifetimeSeconds)
        {
            return CodexState.Waiting;
        }
        if (state.State == CodexState.Working
            && (now - state.LastEventAtUtc).TotalSeconds > WorkingStaleSeconds)
        {
            return CodexState.Waiting;
        }
        return state.State;
    }

    /// <summary>聚合优先级：数字越大越优先（只要有工具在工作，女主就工作）。</summary>
    private static int Priority(CodexState state) => state switch
    {
        CodexState.Working => 6,
        CodexState.WaitingReview => 5,
        CodexState.JustDone => 4,
        CodexState.Waiting => 3,
        CodexState.Idle => 2,
        _ => 1,
    };

    private static CodexState? Map(string eventName) => eventName.ToLowerInvariant() switch
    {
        // Codex / ZCode / Antigravity / DeepSeek Harness 工作中事件
        "sessionstart" or "userpromptsubmit" or "taskstarted" or "preinvocation" => CodexState.Working,
        "posttooluse" or "pretooluse" or "step/start" or "agent/pre-step" or "tool/call" or "tool/result" => CodexState.Working,

        // 用户审批事件（等待人机交互审批权限）
        "permissionrequest" or "approval/asked" => CodexState.WaitingReview,

        // 审批已答复 / 智能体提问被回答：立刻回到工作中，不必等下一个工具事件
        "approval/decided" => CodexState.Working,

        // 任务完成（触发庆祝动画，15s 后衰减为 waiting）
        "taskcomplete" or "stop" or "turn/end" => CodexState.JustDone,

        // 会话结束 / 放弃
        "turnaborted" or "sessionend" => CodexState.Idle,

        _ => null,
    };

    private CodexStatusDto SnapshotLocked(DateTimeOffset now)
    {
        // 聚合：取优先级最高的来源；同优先级取最近有事件的那个。
        CodexState? winner = null;
        SourceState? winnerState = null;
        string? winnerSource = null;
        foreach (var pair in _sources)
        {
            var state = pair.Value;
            var effective = Effective(state, now);
            var better = winner == null
                || Priority(effective) > Priority(winner.Value)
                || (Priority(effective) == Priority(winner.Value)
                    && winnerState != null
                    && state.LastEventAtUtc > winnerState.LastEventAtUtc);
            if (better)
            {
                winner = effective;
                winnerState = state;
                winnerSource = pair.Key;
            }
        }

        var aggregate = winner ?? CodexState.Unknown;
        if (aggregate != _aggregateState)
        {
            _aggregateState = aggregate;
            _aggregateChangedAtUtc = now;
        }

        // 展示用的 lastEvent/detail 取自"当前赢家"，这样浮窗显示的事件与状态是一致的；
        // 没有任何可识别状态（只收到无法映射的事件名）时回退到全局最近事件。
        var lastEvent = winnerState?.LastEvent ?? _lastEvent;
        var detail = winnerState?.Detail ?? _detail;
        if (aggregate == CodexState.Unknown)
        {
            lastEvent = _lastEvent;
            detail = _detail;
        }

        // "default" 表示上报方没声明来源（老脚本/手写配置），对用户没有信息量，对外报 null
        var source = string.Equals(winnerSource, DefaultSource, StringComparison.OrdinalIgnoreCase)
            ? null
            : winnerSource;

        return new CodexStatusDto(
            aggregate.ToString().ToLowerInvariant(),
            detail,
            _aggregateChangedAtUtc,
            lastEvent,
            Math.Max(0, (long)(now - _lastEventAtUtc).TotalSeconds),
            source);
    }
}
