namespace ChillAI.Bridge.Contracts;

/// <summary>
/// GET /health 的响应体。
/// <paramref name="Features"/> 是本 Bridge 支持的能力标记：游戏插件据此判断
/// 端口上正在跑的 Bridge 是不是"自己这一代"的。旧版本 Bridge 没有这个字段，
/// 插件检测到后会把它重启掉——否则会出现"装了新版插件，但旧 Bridge 还在跑，
/// 新逻辑（多来源聚合）静默不生效"。
/// </summary>
public sealed record HealthResponse(string Status, string Version, string[]? Features = null);
