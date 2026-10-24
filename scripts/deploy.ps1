[CmdletBinding()]
param(
    [string]$GameDir = ""
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found. Please install .NET 8 or 9 SDK.'
}

# Auto-detect GameDir if not specified
if ([string]::IsNullOrWhiteSpace($GameDir)) {
    $candidateDirs = @(
        'D:\SBeam\steamapps\common\Chill with You Lo-Fi Story',
        'D:\Steam\steamapps\common\Chill with You Lo-Fi Story',
        'C:\Program Files (x86)\Steam\steamapps\common\Chill with You Lo-Fi Story'
    )
    foreach ($candidate in $candidateDirs) {
        if (Test-Path -LiteralPath (Join-Path $candidate 'BepInEx\core\BepInEx.dll')) {
            $GameDir = $candidate
            Write-Host "[Deploy] Auto-detected game directory: $GameDir" -ForegroundColor Cyan
            break
        }
    }
}

if ([string]::IsNullOrWhiteSpace($GameDir) -or -not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'))) {
    throw 'Valid game directory not found. Please provide -GameDir parameter.'
}

$pluginProject = Join-Path $repositoryRoot 'src\ChillAI.Plugin\ChillAI.Plugin.csproj'
$bridgeProject = Join-Path $repositoryRoot 'src\ChillAI.Bridge\ChillAI.Bridge.csproj'
$targetPluginDir = Join-Path $GameDir 'BepInEx\plugins\ChillAI'
$tempBridgePublishDir = Join-Path $repositoryRoot '.release\build-temp\Bridge'

Write-Host '==> [1/3] Building ChillAI.Plugin...' -ForegroundColor Green
dotnet build $pluginProject -c Release -p:GameDir="$GameDir" --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Plugin build failed.'
}

Write-Host '==> [2/3] Publishing ChillAI.Bridge...' -ForegroundColor Green
if (Test-Path $tempBridgePublishDir) {
    Remove-Item -Recurse -Force $tempBridgePublishDir
}
dotnet publish $bridgeProject -c Release -r win-x64 --self-contained false -o $tempBridgePublishDir --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Bridge publish failed.'
}

Write-Host "==> [3/3] Deploying files to $targetPluginDir..." -ForegroundColor Green
if (-not (Test-Path $targetPluginDir)) {
    New-Item -ItemType Directory -Force -Path $targetPluginDir | Out-Null
}

# Copy plugin dll
$pluginDll = Join-Path $repositoryRoot 'src\ChillAI.Plugin\bin\Release\netstandard2.1\ChillAI.Plugin.dll'
Copy-Item -LiteralPath $pluginDll -Destination (Join-Path $targetPluginDir 'ChillAI.Plugin.dll') -Force

# Copy bridge published artifacts
Copy-Item -Path (Join-Path $tempBridgePublishDir '*') -Destination $targetPluginDir -Recurse -Force

# Copy hook scripts
$hookScript = Join-Path $repositoryRoot 'scripts\codex-hook.ps1'
Copy-Item -LiteralPath $hookScript -Destination (Join-Path $targetPluginDir 'codex-hook.ps1') -Force

# Copy dsh bridge plugin (mounted into the dsh profile as ./dsh-hook.mjs)
$dshHook = Join-Path $repositoryRoot 'scripts\dsh-hook.mjs'
Copy-Item -LiteralPath $dshHook -Destination (Join-Path $targetPluginDir 'dsh-hook.mjs') -Force

# Copy the doctor scripts too: 否则游戏目录里会留着上一次安装的旧体检脚本，
# 它不认识新能力（例如旧 Bridge 检测），给出的结论会误导。
foreach ($doctorFile in @('doctor.ps1', 'doctor.bat')) {
    $source = Join-Path $repositoryRoot "scripts\$doctorFile"
    if (Test-Path -LiteralPath $source) {
        Copy-Item -LiteralPath $source -Destination (Join-Path $targetPluginDir $doctorFile) -Force
    }
}

# Clean temp directory
if (Test-Path $tempBridgePublishDir) {
    Remove-Item -Recurse -Force $tempBridgePublishDir
}

Write-Host "==> Deployment completed successfully to: $targetPluginDir" -ForegroundColor Green
