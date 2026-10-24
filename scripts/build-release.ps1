<#
  Chill AI 发布打包（build-release.ps1）
  ---------------------------------------------------------------------------
  一条命令产出可以直接传到 GitHub Releases 的 zip：

    .\scripts\build-release.ps1 -GameDir "<游戏目录>"

  产出的 zip 结构（全部 ASCII 文件名，避免解压时中文名乱码）：

    ChillAI-v1.0.8\
      ├─ README-FIRST.txt   ← 中文说明（UTF-8 BOM）
      ├─ install.bat        ← 零基础用户双击这个
      ├─ install.ps1
      ├─ doctor.bat         ← 出问题时双击这个
      ├─ doctor.ps1
      └─ ChillAI\           ← 插件本体（会被复制进 BepInEx\plugins\）
          ├─ ChillAI.Plugin.dll
          ├─ ChillAI.Bridge.exe / .dll / .deps.json / .runtimeconfig.json
          ├─ appsettings.json
          ├─ codex-hook.ps1
          └─ dsh-hook.mjs

  版本号从 src\ChillAI.Plugin\Plugin.cs 读取，避免多处手写不一致。
#>
[CmdletBinding()]
param(
    [string]$GameDir = "",
    [string]$OutputDir = "",
    [switch]$SkipBuild,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step([string]$text) { Write-Host ''; Write-Host "==> $text" -ForegroundColor Green }
function Write-Note([string]$text) { Write-Host "    $text" -ForegroundColor Gray }

# ---------------------------------------------------------------- 版本号
$pluginCs = Join-Path $repoRoot 'src\ChillAI.Plugin\Plugin.cs'
$programCs = Join-Path $repoRoot 'src\ChillAI.Bridge\Program.cs'
$pluginVersion = ([regex]::Match((Get-Content -LiteralPath $pluginCs -Raw), 'PluginVersion\s*=\s*"([^"]+)"')).Groups[1].Value
# 不锚定右括号：HealthResponse 以后可能再加参数（例如 features），这里只取版本字符串
$bridgeVersion = ([regex]::Match((Get-Content -LiteralPath $programCs -Raw), 'HealthResponse\("ok",\s*"([^"]+)"')).Groups[1].Value
if (-not $pluginVersion) { throw "没能从 Plugin.cs 读到 PluginVersion" }
if (-not $bridgeVersion) { Write-Warning "没能从 Program.cs 读到 Bridge 版本号（正则可能又过时了），跳过版本一致性校验" }

Write-Step "版本：插件 $pluginVersion / Bridge $bridgeVersion"
if ($bridgeVersion -and $bridgeVersion -ne $pluginVersion) {
    Write-Warning "版本号不一致：Plugin.cs=$pluginVersion，Program.cs=$bridgeVersion。发布前建议对齐。"
}

if (-not $OutputDir) { $OutputDir = Join-Path $repoRoot '.release' }
$stageDir = Join-Path $OutputDir "ChillAI-v$pluginVersion"
$payloadDir = Join-Path $stageDir 'ChillAI'
$zipPath = Join-Path $OutputDir "ChillAI-v$pluginVersion.zip"

# ---------------------------------------------------------------- 构建
if (-not $SkipBuild) {
    # 自动探测游戏目录（与 deploy.ps1 一致）
    if ([string]::IsNullOrWhiteSpace($GameDir)) {
        foreach ($candidate in @(
                'D:\SBeam\steamapps\common\Chill with You Lo-Fi Story',
                'D:\Steam\steamapps\common\Chill with You Lo-Fi Story',
                'C:\Program Files (x86)\Steam\steamapps\common\Chill with You Lo-Fi Story')) {
            if (Test-Path -LiteralPath (Join-Path $candidate 'BepInEx\core\BepInEx.dll')) { $GameDir = $candidate; break }
        }
    }
    if ([string]::IsNullOrWhiteSpace($GameDir) -or
        -not (Test-Path -LiteralPath (Join-Path $GameDir 'BepInEx\core\BepInEx.dll'))) {
        throw '找不到游戏目录。请用 -GameDir "<游戏目录>" 指定（构建插件需要引用游戏程序集）。'
    }
    Write-Note "游戏目录：$GameDir"

    Write-Step '构建 ChillAI.Plugin'
    & dotnet build (Join-Path $repoRoot 'src\ChillAI.Plugin\ChillAI.Plugin.csproj') -c Release -p:GameDir="$GameDir" --nologo
    if ($LASTEXITCODE -ne 0) { throw '插件构建失败' }

    Write-Step '发布 ChillAI.Bridge'
    $bridgePublish = Join-Path $OutputDir 'build-temp\Bridge'
    if (Test-Path -LiteralPath $bridgePublish) { Remove-Item -LiteralPath $bridgePublish -Recurse -Force }
    & dotnet publish (Join-Path $repoRoot 'src\ChillAI.Bridge\ChillAI.Bridge.csproj') -c Release -r win-x64 --self-contained false -o $bridgePublish --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Bridge 发布失败' }
} else {
    $bridgePublish = Join-Path $OutputDir 'build-temp\Bridge'
    if (-not (Test-Path -LiteralPath $bridgePublish)) { throw "-SkipBuild 需要 $bridgePublish 已存在" }
    Write-Note '跳过构建，复用已有产物'
}

# ---------------------------------------------------------------- 组装
Write-Step "组装 $stageDir"
if (Test-Path -LiteralPath $stageDir) { Remove-Item -LiteralPath $stageDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $payloadDir | Out-Null

# 1) Bridge 发布产物（去掉 pdb 与开发期/IIS 文件，减小体积、避免干扰）
Get-ChildItem -LiteralPath $bridgePublish -File | Where-Object {
    $_.Extension -ne '.pdb' -and
    $_.Name -notin @('ChillAI.Bridge.staticwebassets.endpoints.json', 'web.config')
} | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $payloadDir -Force }

# 2) 游戏插件 DLL
$pluginDll = Join-Path $repoRoot 'src\ChillAI.Plugin\bin\Release\netstandard2.1\ChillAI.Plugin.dll'
if (-not (Test-Path -LiteralPath $pluginDll)) { throw "找不到插件产物：$pluginDll" }
Copy-Item -LiteralPath $pluginDll -Destination $payloadDir -Force

# 3) 转发脚本与 dsh 桥接插件（缺一不可，否则装机后功能不全）
foreach ($asset in @('codex-hook.ps1', 'dsh-hook.mjs')) {
    $source = Join-Path $repoRoot "scripts\$asset"
    if (-not (Test-Path -LiteralPath $source)) { throw "缺少必需文件：scripts\$asset" }
    Copy-Item -LiteralPath $source -Destination $payloadDir -Force
}

# 4) 一键安装 / 体检（zip 根目录）
foreach ($asset in @('install.bat', 'install.ps1', 'doctor.bat', 'doctor.ps1')) {
    $source = Join-Path $repoRoot "scripts\$asset"
    if (-not (Test-Path -LiteralPath $source)) { throw "缺少必需文件：scripts\$asset" }
    Copy-Item -LiteralPath $source -Destination $stageDir -Force
}

# 5) 中文说明（UTF-8 带 BOM，记事本/写字板都能正确显示）
$readme = @"
Chill AI v$pluginVersion —— 使用说明
================================================================

【这个东西是什么】
  把 AI 工具（Codex / ZCode / Antigravity / DeepSeek Harness）的真实工作状态
  同步到游戏《Chill with You: Lo-Fi Story》：
  AI 干活时女主角安静工作，等你审批时她来找你，任务完成她会伸懒腰庆祝。

【怎么装（3 步）】
  1. 把整个压缩包解压到一个普通文件夹（不要在压缩包里直接双击！）
  2. 关闭游戏，然后双击本文件夹里的  install.bat
     - 它会自动找到游戏目录、复制插件、检查运行环境
     - 中途会问你要不要顺手启动一次游戏（让插件自动写好全部配置）
  3. 按屏幕提示，在你自己用的 AI 工具里做最后一步：
     - Codex（ChatGPT）桌面应用：设置里打开 Hooks 开关，首次运行钩子时点「允许/信任」
     - ZCode：新建一个会话（它在会话启动时才读取钩子配置）
     - Antigravity：重启一次
     - DeepSeek Harness：重启一次
  之后启动游戏，左上角出现状态浮窗就成功了。

【出问题怎么办】
  双击  doctor.bat
  它会把「游戏 → 插件 → 本地服务 → AI 工具」整条链路逐段检查一遍，
  直接告诉你哪一步断了、该做什么。
  详细排查手册：https://github.com/<你的仓库>/blob/main/docs/TROUBLESHOOTING.md

【重要前提】
  本地状态服务（Bridge）需要 .NET 8 的 ASP.NET Core 运行时
  （注意：不是普通的 .NET Runtime）。
  如果 doctor.bat 提示缺少它，请安装：
  https://dotnet.microsoft.com/download/dotnet/8.0
  选「ASP.NET Core Runtime 8.0 → Windows → x64 Installer」。

【需要手动改配置文件吗】
  不需要。所有钩子配置都由游戏插件在启动时自动写入，
  游戏内按 F8 可以随时开关各个工具的联动。

【卸载】
  游戏内按 F8 把各个「启用 XX 联动」关掉（插件会自动摘除自己写进去的条目），
  然后删除 <游戏目录>\BepInEx\plugins\ChillAI 文件夹即可。
"@
$readmePath = Join-Path $stageDir 'README-FIRST.txt'
[System.IO.File]::WriteAllText($readmePath, $readme, (New-Object System.Text.UTF8Encoding($true)))

# ---------------------------------------------------------------- 校验
Write-Step '校验打包结果'
$expectPayload = @(
    'ChillAI.Plugin.dll', 'ChillAI.Bridge.exe', 'ChillAI.Bridge.dll',
    'ChillAI.Bridge.deps.json', 'ChillAI.Bridge.runtimeconfig.json',
    'appsettings.json', 'codex-hook.ps1', 'dsh-hook.mjs'
)
$missing = @($expectPayload | Where-Object { -not (Test-Path -LiteralPath (Join-Path $payloadDir $_)) })
if ($missing.Count -gt 0) { throw "打包缺少文件：$($missing -join '、')" }
Write-Note "插件本体 $($expectPayload.Count) 个必需文件齐全"

foreach ($asset in @('install.bat', 'install.ps1', 'doctor.bat', 'doctor.ps1', 'README-FIRST.txt')) {
    if (-not (Test-Path -LiteralPath (Join-Path $stageDir $asset))) { throw "zip 根目录缺少 $asset" }
}
Write-Note '一键安装 / 体检 / 说明 齐全'

# 编码自检：带中文的 .ps1 与 README 必须是 UTF-8 BOM，否则 Windows PowerShell 5.1 会乱码
foreach ($file in @((Join-Path $stageDir 'install.ps1'), (Join-Path $stageDir 'doctor.ps1'), $readmePath)) {
    $bytes = [System.IO.File]::ReadAllBytes($file)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    if (-not $hasBom) {
        Write-Warning "$(Split-Path -Leaf $file) 没有 UTF-8 BOM，Windows PowerShell 5.1 下中文可能乱码。正在补上..."
        $text = [System.IO.File]::ReadAllText($file)
        [System.IO.File]::WriteAllText($file, $text, (New-Object System.Text.UTF8Encoding($true)))
    }
}
Write-Note '中文编码（UTF-8 BOM）校验通过'

# ---------------------------------------------------------------- 压缩
if (-not $NoZip) {
    Write-Step "压缩为 $zipPath"
    if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
    Compress-Archive -Path $stageDir -DestinationPath $zipPath -CompressionLevel Optimal
    $sizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 2)
    Write-Note "完成：$zipPath（$sizeMb MB）"

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
    try {
        Write-Note "zip 内条目（$($zip.Entries.Count) 个）："
        foreach ($entry in $zip.Entries) { Write-Host "      $($entry.FullName)" -ForegroundColor DarkGray }
    } finally { $zip.Dispose() }
}

# ---------------------------------------------------------------- 收尾
Write-Host ''
Write-Host '发布包已就绪。上传到 GitHub Releases 时的建议：' -ForegroundColor Cyan
Write-Host "  1. 附件：$zipPath" -ForegroundColor Gray
Write-Host '  2. Release 说明里写清「解压后双击 install.bat」，并附上 ASP.NET Core 8 运行时的下载链接' -ForegroundColor Gray
Write-Host '  3. 提醒用户：Codex 需要手动打开 Hooks 开关并批准信任（这一步没法自动化）' -ForegroundColor Gray
Write-Host ''
