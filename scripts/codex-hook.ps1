# Codex hook 转发脚本：把生命周期事件 POST 到 ChillAI.Bridge（/codex/events）
# 用法（在 codex-hooks.json 中引用）：
#   powershell -NoProfile -ExecutionPolicy Bypass -File <本脚本> -EventName Stop
# 设计目标：极轻量（几毫秒），Bridge 未启动时静默失败，绝不阻塞 Codex 主流程。

param(
    [Parameter(Mandatory = $true)]
    [string]$EventName,

    [string]$Detail = ""
)

$ErrorActionPreference = "Stop"
$bridgeUrl = "http://127.0.0.1:17860/codex/events"

$body = @{ event = $EventName }
if (-not [string]::IsNullOrWhiteSpace($Detail)) {
    $body.detail = $Detail
}

try {
    Invoke-RestMethod -Uri $bridgeUrl -Method Post -Body ($body | ConvertTo-Json -Compress) `
        -ContentType "application/json" -TimeoutSec 2 | Out-Null
}
catch {
    # Bridge 未运行或不可达：什么都不做，让 Codex 正常继续
}

exit 0
