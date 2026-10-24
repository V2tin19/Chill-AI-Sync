<#
  Chill AI 一键安装（install.ps1）
  ---------------------------------------------------------------------------
  给零基础用户用：双击同目录的 install.bat 即可。它会
    1. 自动找到《Chill with You: Lo-Fi Story》的安装目录（Steam 库全盘探测）
    2. 把 ChillAI 插件文件夹复制到 <游戏目录>\BepInEx\plugins\ChillAI
    3. 检查 .NET ASP.NET Core 8 运行时、端口占用、游戏是否在运行
    4. 列出你本机装了哪些 AI 工具，并给出各自「还需要你手动做的一步」
    5. 可选：直接帮你启动游戏一次，让插件自动写好所有钩子配置

  参数：
    -GameDir <路径>    手动指定游戏目录（自动探测失败时用）
    -PayloadDir <路径> 手动指定 ChillAI 插件文件夹位置（默认脚本同级的 ChillAI）
    -Yes               全部用默认答案，不交互
    -NoLaunch          不询问/不启动游戏
    -Pause             结束时等待按键（.bat 双击时使用）

  本脚本只做「复制文件 + 检查环境」，不修改任何 AI 工具的配置文件——
  那些配置由游戏插件在启动时自动写入（也可用 F8 开关随时开/关）。
#>
[CmdletBinding()]
param(
    [string]$GameDir = "",
    [string]$PayloadDir = "",
    [switch]$Yes,
    [switch]$NoLaunch,
    [switch]$Pause
)

$ErrorActionPreference = 'Stop'
$script:Problems = 0
$script:Warnings = 0

# ---------------------------------------------------------------- 输出helpers
function Write-Head([string]$text) {
    Write-Host ''
    Write-Host ('=' * 66) -ForegroundColor DarkCyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ('=' * 66) -ForegroundColor DarkCyan
}
function Write-Step([string]$text) { Write-Host ''; Write-Host "▶ $text" -ForegroundColor White }
function Write-Ok([string]$text) { Write-Host "  [OK]   $text" -ForegroundColor Green }
function Write-Warn([string]$text) { $script:Warnings++; Write-Host "  [注意] $text" -ForegroundColor Yellow }
function Write-Bad([string]$text) { $script:Problems++; Write-Host "  [问题] $text" -ForegroundColor Red }
function Write-Info([string]$text) { Write-Host "         $text" -ForegroundColor Gray }

function Read-Answer([string]$prompt, [string]$default = 'y') {
    if ($Yes) { return $default }
    $suffix = if ($default -eq 'y') { '[Y/n]' } else { '[y/N]' }
    while ($true) {
        Write-Host ''
        Write-Host "? $prompt $suffix " -ForegroundColor Yellow -NoNewline
        $answer = Read-Host
        if ([string]::IsNullOrWhiteSpace($answer)) { return $default }
        $answer = $answer.Trim().ToLowerInvariant()
        if ($answer -in @('y', 'yes', '是')) { return 'y' }
        if ($answer -in @('n', 'no', '否')) { return 'n' }
    }
}

# ------------------------------------------------------- 定位 Steam 与游戏目录
function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    $keys = @(
        'HKCU:\Software\Valve\Steam',
        'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam',
        'HKLM:\SOFTWARE\Valve\Steam'
    )
    foreach ($key in $keys) {
        try {
            $props = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in @('SteamPath', 'InstallPath')) {
                $value = $props.$name
                if ($value -and (Test-Path -LiteralPath $value)) {
                    $roots.Add((Resolve-Path -LiteralPath $value).Path)
                }
            }
        } catch { }
    }
    $guesses = New-Object System.Collections.Generic.List[string]
    # 注意：32 位进程里 ProgramFiles(x86) 可能为空，Join-Path 会直接抛错，所以逐个判空
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($base) { $guesses.Add((Join-Path $base 'Steam')) }
    }
    foreach ($path in @('C:\Steam', 'D:\Steam', 'D:\SBeam', 'E:\Steam', 'D:\SteamLibrary', 'E:\SteamLibrary')) {
        $guesses.Add($path)
    }
    foreach ($guess in $guesses) {
        if (Test-Path -LiteralPath $guess) { $roots.Add((Resolve-Path -LiteralPath $guess).Path) }
    }
    return ($roots | Select-Object -Unique)
}

function Get-SteamLibraries([string]$steamRoot) {
    $libs = New-Object System.Collections.Generic.List[string]
    $libs.Add($steamRoot)
    $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
    if (Test-Path -LiteralPath $vdf) {
        $text = Get-Content -LiteralPath $vdf -Raw -ErrorAction SilentlyContinue
        if ($text) {
            # vdf 里的路径是双反斜杠转义的
            foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                $path = $m.Groups[1].Value.Replace('\\', '\')
                if (Test-Path -LiteralPath $path) { $libs.Add($path) }
            }
        }
    }
    return ($libs | Select-Object -Unique)
}

function Test-GameDir([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    if (-not (Test-Path -LiteralPath $dir)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $dir 'Chill With You.exe')) -or
           (Test-Path -LiteralPath (Join-Path $dir 'Chill With You_Data'))
}

function Find-GameDirs {
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($steamRoot in Get-SteamRoots) {
        foreach ($lib in Get-SteamLibraries $steamRoot) {
            $common = Join-Path $lib 'steamapps\common'
            if (-not (Test-Path -LiteralPath $common)) { continue }
            # 精确名优先，其次容忍改名（*Chill*）
            $exact = Join-Path $common 'Chill with You Lo-Fi Story'
            if (Test-GameDir $exact) { $found.Add((Resolve-Path -LiteralPath $exact).Path) }
            foreach ($candidate in Get-ChildItem -LiteralPath $common -Directory -ErrorAction SilentlyContinue |
                     Where-Object { $_.Name -like '*Chill*' }) {
                if (Test-GameDir $candidate.FullName) { $found.Add($candidate.FullName) }
            }
        }
    }
    return ($found | Select-Object -Unique)
}

function Resolve-GameDir {
    if ($GameDir) {
        if (Test-GameDir $GameDir) {
            $resolved = (Resolve-Path -LiteralPath $GameDir).Path
            Write-Ok "使用指定的游戏目录：$resolved"
            return $resolved
        }
        Write-Bad "你指定的游戏目录看起来不对：$GameDir"
        Write-Info '该目录下应当有 "Chill With You.exe" 或 "Chill With You_Data" 文件夹。'
        return $null
    }

    $candidates = @(Find-GameDirs)
    if ($candidates.Count -eq 1) {
        Write-Ok "自动找到游戏目录：$($candidates[0])"
        return $candidates[0]
    }
    if ($candidates.Count -gt 1) {
        Write-Info '找到多个候选游戏目录：'
        for ($i = 0; $i -lt $candidates.Count; $i++) { Write-Info "  [$($i + 1)] $($candidates[$i])" }
        if ($Yes) { Write-Ok "使用第一个：$($candidates[0])"; return $candidates[0] }
        Write-Host ''
        Write-Host "? 请输入序号选择（直接回车用 1）： " -ForegroundColor Yellow -NoNewline
        $pick = Read-Host
        if ([string]::IsNullOrWhiteSpace($pick)) { return $candidates[0] }
        $index = 0
        if ([int]::TryParse($pick, [ref]$index) -and $index -ge 1 -and $index -le $candidates.Count) {
            return $candidates[$index - 1]
        }
        return $candidates[0]
    }

    Write-Warn '没能自动找到游戏目录（Steam 库可能是非常规位置）。'
    if ($Yes) { return $null }
    Write-Host ''
    Write-Host '? 请把游戏安装目录整个粘贴进来（右键可粘贴），直接回车放弃： ' -ForegroundColor Yellow -NoNewline
    $manual = Read-Host
    $manual = $manual.Trim('"').Trim()
    if ($manual -and (Test-GameDir $manual)) { return (Resolve-Path -LiteralPath $manual).Path }
    if ($manual) { Write-Bad "这个目录里没找到游戏：$manual" }
    return $null
}

# ------------------------------------------------------------ 环境 / 依赖检查
function Test-AspNetCoreRuntime {
    # 先看 dotnet --list-runtimes（最权威），再直接看安装目录（dotnet 不在 PATH 时兜底）
    try {
        $list = & dotnet --list-runtimes 2>$null
        if ($list -and ($list -match 'Microsoft\.AspNetCore\.App\s+8\.')) { return $true }
    } catch { }
    foreach ($base in @($env:ProgramFiles, ${env:ProgramFiles(x86)})) {
        if (-not $base) { continue }
        $root = Join-Path $base 'dotnet\shared\Microsoft.AspNetCore.App'
        if (Test-Path -LiteralPath $root) {
            if (Get-ChildItem -LiteralPath $root -Directory -ErrorAction SilentlyContinue |
                Where-Object { $_.Name -like '8.*' }) { return $true }
        }
    }
    return $false
}

function Get-PortOwner([int]$port) {
    try {
        $conn = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction Stop | Select-Object -First 1
        if ($conn) { return (Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue) }
    } catch { }
    return $null
}

function Test-BridgeAlive([int]$port = 17860) {
    try {
        $resp = Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 3 -ErrorAction Stop
        return ($resp.status -eq 'ok')
    } catch { return $false }
}

# --------------------------------------------------------------- 已安装的 AI 工具
function Get-InstalledTools {
    $home2 = $env:USERPROFILE
    $tools = @()
    $tools += [pscustomobject]@{ Name = 'Codex';       Dir = (Join-Path $home2 '.codex');        Manual = '打开 Codex（ChatGPT）桌面应用 → 设置里打开 Hooks 开关；首次运行钩子时点「允许/信任」' }
    $tools += [pscustomobject]@{ Name = 'ZCode';       Dir = (Join-Path $home2 '.zcode');        Manual = '在 ZCode 里新建一个会话（它在会话启动时才读取钩子配置）' }
    $tools += [pscustomobject]@{ Name = 'Antigravity'; Dir = (Join-Path $home2 '.gemini');       Manual = '重启一次 Antigravity' }
    $tools += [pscustomobject]@{ Name = 'DeepSeek Harness (dsh)'; Dir = (Join-Path $home2 '.dsh'); Manual = '重启一次 DeepSeek Harness（新插件要重新加载一代）' }
    return ($tools | Where-Object { Test-Path -LiteralPath $_.Dir })
}

# ------------------------------------------------------------------------ 主流程
Write-Head 'Chill AI 一键安装'

Write-Step '第 1 步 / 共 5 步：检查运行环境'
Write-Info "PowerShell 版本：$($PSVersionTable.PSVersion)"
if ($PSVersionTable.PSVersion.Major -lt 5) { Write-Bad '需要 PowerShell 5.1 或更高版本。' }

$aspnetOk = Test-AspNetCoreRuntime
if ($aspnetOk) {
    Write-Ok '已安装 .NET ASP.NET Core 8 运行时（Bridge 需要）'
} else {
    Write-Bad '缺少 .NET ASP.NET Core 8 运行时——没有它，本地状态服务（Bridge）会启动即退，女主角不会动。'
    Write-Info '注意：必须是 ASP.NET Core Runtime，不是普通的 .NET Runtime。'
    Write-Info '下载地址（Windows x64 Installer）：https://dotnet.microsoft.com/download/dotnet/8.0'
    Write-Info '装完这个运行时后，重新运行本安装程序即可（插件文件可以先复制好，不影响）。'
}

Write-Step '第 2 步 / 共 5 步：定位游戏目录'
$resolvedGame = Resolve-GameDir
if (-not $resolvedGame) {
    Write-Bad '没有定位到游戏目录，安装无法继续。'
    Write-Info '解决办法：把本压缩包解压后，右键 install.bat → 编辑，或在命令行里运行：'
    Write-Info '  powershell -ExecutionPolicy Bypass -File install.ps1 -GameDir "你的游戏目录"'
    if ($Pause) { Write-Host ''; Write-Host '按任意键关闭...' -ForegroundColor DarkGray; $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') }
    exit 2
}

$bepInExDll = Join-Path $resolvedGame 'BepInEx\core\BepInEx.dll'
if (-not (Test-Path -LiteralPath $bepInExDll)) {
    Write-Warn '该目录里没找到 BepInEx（游戏本体一般自带）。'
    Write-Info "期望位置：$bepInExDll"
    Write-Info '如果游戏能正常运行且装了 Mod，通常说明路径选错了；请确认选的是游戏根目录（能看到 "Chill With You.exe"）。'
} else {
    Write-Ok 'BepInEx 已就位（游戏自带，无需额外安装）'
}

$gameProcess = Get-Process -Name 'Chill With You' -ErrorAction SilentlyContinue
if ($gameProcess) {
    Write-Warn '游戏正在运行——必须先完全关闭游戏，否则插件文件会被占用、复制失败。'
    if (-not $Yes) {
        Write-Host ''
        Write-Host '? 关好游戏后按回车继续（直接回车继续）： ' -ForegroundColor Yellow -NoNewline
        $null = Read-Host
        $gameProcess = Get-Process -Name 'Chill With You' -ErrorAction SilentlyContinue
    }
    if ($gameProcess) {
        Write-Bad '游戏仍在运行，已中止。请关闭游戏后重新运行本安装程序。'
        if ($Pause) { Write-Host ''; Write-Host '按任意键关闭...' -ForegroundColor DarkGray; $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') }
        exit 3
    }
}

Write-Step '第 3 步 / 共 5 步：校验并复制插件文件'
if (-not $PayloadDir) { $PayloadDir = Join-Path $PSScriptRoot 'ChillAI' }
if (-not (Test-Path -LiteralPath $PayloadDir)) {
    Write-Bad "没找到插件文件夹：$PayloadDir"
    Write-Info '请确认压缩包已完整解压（不要在压缩包里直接双击运行），且 ChillAI 文件夹与本脚本在同一层。'
    if ($Pause) { Write-Host ''; Write-Host '按任意键关闭...' -ForegroundColor DarkGray; $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') }
    exit 4
}
# 统一成绝对路径：下面算「相对子路径」时两边必须同为绝对路径，否则会算错目标文件名
$PayloadDir = (Resolve-Path -LiteralPath $PayloadDir).Path.TrimEnd('\')

$required = @('ChillAI.Plugin.dll', 'ChillAI.Bridge.exe', 'ChillAI.Bridge.dll', 'ChillAI.Bridge.runtimeconfig.json', 'appsettings.json', 'codex-hook.ps1', 'dsh-hook.mjs')
$missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $PayloadDir $_)) })
if ($missing.Count -gt 0) {
    Write-Bad "插件文件夹不完整，缺少：$($missing -join '、')"
    Write-Info '请重新下载发布包（可能是下载中断或被杀毒软件删掉了文件）。'
    if ($Pause) { Write-Host ''; Write-Host '按任意键关闭...' -ForegroundColor DarkGray; $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') }
    exit 4
}
Write-Ok "插件文件完整（$($required.Count) 个必需文件都在）"

$targetDir = Join-Path $resolvedGame 'BepInEx\plugins\ChillAI'

# 插件目录里的 Bridge 可能正在运行并锁住文件（上一次装完游戏还没关、或体检脚本临时拉起过）。
# 它是我们自己的无状态服务，先停掉再复制是安全的；不停掉就会复制失败。
$bridgeProcs = @(Get-Process -Name 'ChillAI.Bridge' -ErrorAction SilentlyContinue)
try {
    $bridgeProcs += @(Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -like '*ChillAI.Bridge*' } |
        ForEach-Object { Get-Process -Id $_.ProcessId -ErrorAction SilentlyContinue })
} catch { }
$bridgeProcs = @($bridgeProcs | Where-Object { $_ } | Sort-Object Id -Unique)
if ($bridgeProcs.Count -gt 0) {
    Write-Info "检测到 $($bridgeProcs.Count) 个 ChillAI Bridge 进程正在运行（会锁住插件文件），正在停止..."
    foreach ($proc in $bridgeProcs) { try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop } catch { } }
    Start-Sleep -Milliseconds 1000
}

try {
    if (-not (Test-Path -LiteralPath $targetDir)) { New-Item -ItemType Directory -Force -Path $targetDir | Out-Null }
    Get-ChildItem -LiteralPath $PayloadDir -File -Recurse | ForEach-Object {
        $relative = $_.FullName.Substring($PayloadDir.Length).TrimStart('\')
        $dest = Join-Path $targetDir $relative
        $destParent = Split-Path -Parent $dest
        if ($destParent -and -not (Test-Path -LiteralPath $destParent)) { New-Item -ItemType Directory -Force -Path $destParent | Out-Null }
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
    }

    # 清掉旧版本遗留、现已不再分发的文件（避免「明明覆盖了却还是旧文件」的困惑）
    foreach ($obsolete in @('ChillAI.Bridge.pdb', 'ChillAI.Bridge.staticwebassets.endpoints.json', 'web.config')) {
        $obsoletePath = Join-Path $targetDir $obsolete
        if (Test-Path -LiteralPath $obsoletePath) { Remove-Item -LiteralPath $obsoletePath -Force -ErrorAction SilentlyContinue }
    }

    # 复制完必须回读校验：只报「已复制」而不核对，出问题时会静默装错（历史教训）
    $notCopied = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $targetDir $_)) })
    if ($notCopied.Count -gt 0) {
        throw "复制后校验失败，目标目录仍缺少：$($notCopied -join '、')"
    }
    Write-Ok "已复制并校验通过：$targetDir"
} catch {
    Write-Bad "复制失败：$($_.Exception.Message)"
    Write-Info '常见原因：游戏没关干净 / 没有该目录的写入权限（试试右键 install.bat → 以管理员身份运行）。'
    if ($Pause) { Write-Host ''; Write-Host '按任意键关闭...' -ForegroundColor DarkGray; $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown') }
    exit 5
}

# 顺手把「体检」脚本放到插件目录，方便以后随时排查
foreach ($extra in @('doctor.ps1', 'doctor.bat')) {
    $source = Join-Path $PSScriptRoot $extra
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $targetDir $extra) -Force }
}

Write-Step '第 4 步 / 共 5 步：检查端口与残留进程'
$portOwner = Get-PortOwner 17860
if ($portOwner) {
    if (Test-BridgeAlive 17860) {
        Write-Ok "17860 端口上已经有一个 ChillAI Bridge 在运行（pid $($portOwner.Id)），插件会自动复用它"
    } else {
        Write-Warn "17860 端口被其它程序占用（$($portOwner.ProcessName)，pid $($portOwner.Id)）——Bridge 将无法启动。"
        Write-Info '请关闭该程序，或改端口：编辑插件目录下的 appsettings.json，把 ListenUrl 换成别的端口。'
    }
} else {
    Write-Ok '17860 端口空闲（Bridge 会在游戏启动时自动占用它）'
}

Write-Step '第 5 步 / 共 5 步：完成'
Write-Ok '插件文件安装完成。'

$tools = @(Get-InstalledTools)
Write-Host ''
Write-Host '本机检测到的 AI 工具，以及各自「还需要你手动做的一步」：' -ForegroundColor White
if ($tools.Count -eq 0) {
    Write-Info '没有检测到 Codex / ZCode / Antigravity / DeepSeek Harness 的数据目录。'
    Write-Info '这没关系——只联动你实际在用的工具。装好其中任意一个后，重启一次游戏即可。'
} else {
    foreach ($tool in $tools) {
        Write-Host "  · $($tool.Name)" -ForegroundColor Cyan
        Write-Host "      $($tool.Manual)" -ForegroundColor Gray
    }
}

Write-Host ''
Write-Host '钩子配置不需要你手动改任何文件——游戏插件会在启动时自动写入（F8 可随时开关）。' -ForegroundColor White

$launch = 'n'
if (-not $NoLaunch) {
    $launch = Read-Answer '现在启动游戏一次，让插件自动写好全部钩子配置？（启动后看到左上角浮窗即可关闭游戏）'
}
if ($launch -eq 'y') {
    $exe = Join-Path $resolvedGame 'Chill With You.exe'
    try {
        Start-Process -FilePath $exe -WorkingDirectory $resolvedGame | Out-Null
        Write-Ok '已启动游戏。等左上角出现状态浮窗（或等 15 秒）后就可以关闭游戏了。'
        Write-Info '如果浮窗显示「Bridge 未连接」，请先装好上面提到的 ASP.NET Core 8 运行时。'
    } catch {
        Write-Warn "启动游戏失败：$($_.Exception.Message)。请手动启动游戏一次。"
    }
} else {
    Write-Info '好的——请稍后自己启动一次游戏（这一步不能省，钩子配置在游戏启动时写入）。'
}

Write-Head '安装结束'
Write-Host "  游戏目录 : $resolvedGame" -ForegroundColor Gray
Write-Host "  插件目录 : $targetDir" -ForegroundColor Gray
Write-Host "  体检脚本 : $targetDir\doctor.bat（以后出问题双击它）" -ForegroundColor Gray
if ($script:Warnings -gt 0) { Write-Host "  有 $($script:Warnings) 条注意事项，见上面的 [注意] 行。" -ForegroundColor Yellow }
if ($script:Problems -gt 0) { Write-Host "  有 $($script:Problems) 个问题需要先解决，见上面的 [问题] 行。" -ForegroundColor Red }
Write-Host ''

if ($Pause) {
    Write-Host '按任意键关闭此窗口...' -ForegroundColor DarkGray
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
}
exit $(if ($script:Problems -gt 0) { 1 } else { 0 })
