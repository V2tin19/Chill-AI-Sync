using ChillAI.Bridge.Contracts;
using ChillAI.Bridge.Services;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls(builder.Configuration["Bridge:ListenUrl"] ?? "http://127.0.0.1:17860");

var store = new CodexStatusStore();
builder.Services.AddSingleton(store);

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new HealthResponse("ok", "0.1.0")));

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
