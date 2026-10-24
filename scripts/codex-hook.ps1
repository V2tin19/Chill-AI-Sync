# Codex hook 转发脚本：把生命周期事件 POST 到 ChillAI.Bridge（/codex/events）
# 用法（在 hooks.json / ZCode config.json / Antigravity hooks.json 中引用）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File <本脚本> -EventName Stop -Source codex
# 设计目标：极轻量（几毫秒），Bridge 未启动时静默失败，绝不阻塞工具主流程。
#
# -Source 用来告诉 Bridge「这个事件是哪个工具发的」：Bridge 按来源分别记账再聚合，
# 这样同时开两个工具时不会互相打架（省略时归入 default 单一来源，等价于旧行为）。

param(
    [Parameter(Mandatory = $true)]
    [string]$EventName,

    [string]$Detail = "",

    [string]$Source = ""
)

$ErrorActionPreference = "Stop"

# 默认上报到本机 17860；需要换端口时设环境变量 CHILLAI_BRIDGE_URL 覆盖即可（不必改脚本）
$bridgeUrl = "http://127.0.0.1:17860/codex/events"
if (-not [string]::IsNullOrWhiteSpace($env:CHILLAI_BRIDGE_URL)) {
    $bridgeUrl = $env:CHILLAI_BRIDGE_URL
}

$body = @{ event = $EventName }
if (-not [string]::IsNullOrWhiteSpace($Detail)) {
    $body.detail = $Detail
}
if (-not [string]::IsNullOrWhiteSpace($Source)) {
    $body.source = $Source
}

try {
    Invoke-RestMethod -Uri $bridgeUrl -Method Post -Body ($body | ConvertTo-Json -Compress) `
        -ContentType "application/json" -TimeoutSec 2 | Out-Null
}
catch {
    # Bridge 未运行或不可达：什么都不做，让工具正常继续
}

# 输出标准空 JSON 响应，满足 Antigravity 等 Agent 平台的 stdout 契约
Write-Output "{}"
exit 0
