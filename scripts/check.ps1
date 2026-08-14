[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$GameDir
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $repositoryRoot 'src\ChillAI.Plugin\ChillAI.Plugin.csproj'
$bridgeProject = Join-Path $repositoryRoot 'src\ChillAI.Bridge\ChillAI.Bridge.csproj'
$pluginOutput = Join-Path $repositoryRoot 'src\ChillAI.Plugin\bin\Release\netstandard2.1\ChillAI.Plugin.dll'
$bridgeOutput = Join-Path $repositoryRoot 'src\ChillAI.Bridge\bin\Release\net8.0\ChillAI.Bridge.dll'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK was not found.'
}

if (-not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'))) {
    throw "BepInEx was not found under: $GameDir"
}

dotnet build $pluginProject -c Release -p:GameDir="$GameDir" --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Plugin build failed.'
}

dotnet build $bridgeProject -c Release --nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Bridge build failed.'
}

if (-not (Test-Path -LiteralPath $pluginOutput)) {
    throw 'Plugin output was not created.'
}

if (-not (Test-Path -LiteralPath $bridgeOutput)) {
    throw 'Bridge output was not created.'
}

Write-Host 'Build and output checks passed.'
