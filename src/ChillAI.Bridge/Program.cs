using ChillAI.Bridge.Contracts;
using ChillAI.Bridge.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});
builder.WebHost.UseUrls(builder.Configuration["Bridge:ListenUrl"] ?? "http://127.0.0.1:17860");

var store = new CodexStatusStore();
builder.Services.AddSingleton(store);

var app = builder.Build();

// 能力标记：插件用它判断端口上跑的是不是同一代 Bridge（见 HealthResponse 注释）。
// 新增会影响行为的能力时，往这里加一个标记即可让旧实例被自动替换。
var features = new[] { "source-aggregation" };

app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", "1.0.9", features)));

// Codex hooks 转发脚本上报生命周期事件（POST 一行 JSON）
app.MapPost("/codex/events", (CodexEvent evt, CodexStatusStore s, ILogger<Program> log) =>
{
    var snap = s.Apply(evt);
    log.LogInformation("codex event: {Event} -> {State}", evt.Event, snap.State);
    return Results.Ok(snap);
});

// 已收到的事件历史（诊断用）
app.MapGet("/codex/events", (CodexStatusStore s) => Results.Ok(s.History()));

// 游戏插件轮询的当前状态
app.MapGet("/codex/status", (CodexStatusStore s) => Results.Ok(s.Snapshot()));

app.Run();

public partial class Program;
