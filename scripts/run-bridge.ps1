# 构建（如需要）并启动 ChillAI.Bridge
# 用法：powershell -ExecutionPolicy Bypass -File scripts\run-bridge.ps1
$ErrorActionPreference = "Stop"

$root = Split-Path $PSScriptRoot -Parent
$csproj = Join-Path $root "src\ChillAI.Bridge\ChillAI.Bridge.csproj"
$dll = Join-Path $root "src\ChillAI.Bridge\bin\Release\net8.0\ChillAI.Bridge.dll"

if (-not (Test-Path $dll)) {
    Write-Host "[Bridge] 未找到已编译 DLL，先执行编译..." -ForegroundColor Yellow
    dotnet build $csproj -c Release
    if ($LASTEXITCODE -ne 0) { Write-Host "[Bridge] 编译失败" -ForegroundColor Red; exit 1 }
}

Write-Host "[Bridge] 启动中：$dll" -ForegroundColor Green
Write-Host "[Bridge] 状态接口：http://127.0.0.1:17860/codex/status   (Ctrl+C 停止)" -ForegroundColor Cyan
dotnet $dll
