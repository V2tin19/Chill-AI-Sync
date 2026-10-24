<#
  Chill AI 一键体检（doctor.ps1）
  ---------------------------------------------------------------------------
  出问题的时候双击同目录的 doctor.bat（安装后插件目录里也有一份）。
  它把「从游戏到 AI 工具」整条链路逐段查一遍，并直接给出该做什么：

    [1] 运行环境      PowerShell 版本、.NET ASP.NET Core 8 运行时
    [2] 安装位置      游戏目录、BepInEx、插件文件是否齐全
    [3] 插件有没有加载 读 BepInEx 日志里 [ChillAI] 的行
    [4] 本地服务      Bridge 端口 / /health / 当前状态 / 最近事件
    [5] 各工具配置     Codex / ZCode / Antigravity / dsh 的钩子条目在不在
    [6] 结论          按严重程度汇总，并列出「接下来做什么」

  参数：
    -GameDir <路径>   手动指定游戏目录
    -TestEvents       额外做一次「往 Bridge 打假事件」的通路测试（会让女主角动一下）
    -Pause            结束时等待按键（.bat 双击时使用）

  本脚本只读不改（两个例外：Bridge 没在跑时会临时帮你启动它，体检结束会自动停掉；
  检测到端口上是旧版本 Bridge 时会帮你结束它，好让下次启动游戏拉起新版）。
#>
[CmdletBinding()]
param(
    [string]$GameDir = "",
    [switch]$TestEvents,
    [switch]$Pause
)

$ErrorActionPreference = 'Continue'
$script:Pass = 0
$script:Warn = 0
$script:Fail = 0
$script:StartedBridge = $false
$script:NextSteps = New-Object System.Collections.Generic.List[string]

function Write-Head([string]$text) {
    Write-Host ''
    Write-Host ('=' * 66) -ForegroundColor DarkCyan
    Write-Host "  $text" -ForegroundColor Cyan
    Write-Host ('=' * 66) -ForegroundColor DarkCyan
}
function Write-Section([string]$text) { Write-Host ''; Write-Host "▶ $text" -ForegroundColor White }
function Pass([string]$text) { $script:Pass++; Write-Host "  [通过] $text" -ForegroundColor Green }
function Warn([string]$text) { $script:Warn++; Write-Host "  [注意] $text" -ForegroundColor Yellow }
function Fail([string]$text) { $script:Fail++; Write-Host "  [失败] $text" -ForegroundColor Red }
function Info([string]$text) { Write-Host "         $text" -ForegroundColor Gray }
function Fix([string]$text) { $script:NextSteps.Add($text) }

# ------------------------------------------------------------------ 共用小工具
function Test-GameDir([string]$dir) {
    if ([string]::IsNullOrWhiteSpace($dir)) { return $false }
    if (-not (Test-Path -LiteralPath $dir)) { return $false }
    return (Test-Path -LiteralPath (Join-Path $dir 'Chill With You.exe')) -or
           (Test-Path -LiteralPath (Join-Path $dir 'Chill With You_Data'))
}

function Get-SteamRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($key in @('HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKLM:\SOFTWARE\Valve\Steam')) {
        try {
            $props = Get-ItemProperty -Path $key -ErrorAction Stop
            foreach ($name in @('SteamPath', 'InstallPath')) {
                $value = $props.$name
                if ($value -and (Test-Path -LiteralPath $value)) { $roots.Add((Resolve-Path -LiteralPath $value).Path) }
            }
        } catch { }
    }
    foreach ($base in @(${env:ProgramFiles(x86)}, $env:ProgramFiles)) {
        if ($base) {
            $guess = Join-Path $base 'Steam'
            if (Test-Path -LiteralPath $guess) { $roots.Add((Resolve-Path -LiteralPath $guess).Path) }
        }
    }
    foreach ($path in @('C:\Steam', 'D:\Steam', 'D:\SBeam', 'E:\Steam', 'D:\SteamLibrary', 'E:\SteamLibrary')) {
        if (Test-Path -LiteralPath $path) { $roots.Add((Resolve-Path -LiteralPath $path).Path) }
    }
    return ($roots | Select-Object -Unique)
}

function Find-GameDirs {
    $found = New-Object System.Collections.Generic.List[string]
    foreach ($steamRoot in Get-SteamRoots) {
        $libs = New-Object System.Collections.Generic.List[string]
        $libs.Add($steamRoot)
        $vdf = Join-Path $steamRoot 'steamapps\libraryfolders.vdf'
        if (Test-Path -LiteralPath $vdf) {
            $text = Get-Content -LiteralPath $vdf -Raw -ErrorAction SilentlyContinue
            if ($text) {
                foreach ($m in [regex]::Matches($text, '"path"\s+"([^"]+)"')) {
                    $path = $m.Groups[1].Value.Replace('\\', '\')
                    if (Test-Path -LiteralPath $path) { $libs.Add($path) }
                }
            }
        }
        foreach ($lib in $libs) {
            $common = Join-Path $lib 'steamapps\common'
            if (-not (Test-Path -LiteralPath $common)) { continue }
            foreach ($candidate in Get-ChildItem -LiteralPath $common -Directory -ErrorAction SilentlyContinue |
                     Where-Object { $_.Name -like '*Chill*' }) {
                if (Test-GameDir $candidate.FullName) { $found.Add($candidate.FullName) }
            }
        }
    }
    return ($found | Select-Object -Unique)
}

function Test-AspNetCoreRuntime {
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

function Get-BridgeStatus([int]$port = 17860) {
    try { return (Invoke-RestMethod -Uri "http://127.0.0.1:$port/codex/status" -TimeoutSec 3 -ErrorAction Stop) } catch { return $null }
}
function Get-BridgeEvents([int]$port = 17860) {
    try { return @(Invoke-RestMethod -Uri "http://127.0.0.1:$port/codex/events" -TimeoutSec 3 -ErrorAction Stop) } catch { return $null }
}
function Get-BridgeHealth([int]$port = 17860) {
    try { return (Invoke-RestMethod -Uri "http://127.0.0.1:$port/health" -TimeoutSec 3 -ErrorAction Stop) } catch { return $null }
}
function Get-PortOwner([int]$port) {
    try {
        $conn = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction Stop | Select-Object -First 1
        if ($conn) { return (Get-Process -Id $conn.OwningProcess -ErrorAction SilentlyContinue) }
    } catch { }
    return $null
}

$stateChinese = @{
    'working'       = '正在工作'
    'justdone'      = '刚完成任务'
    'waiting'       = '休息 / 待命中'
    'waitingreview' = '等待审批'
    'idle'          = '空闲'
    'unknown'       = '未知（还没收到任何工具事件）'
}

# ------------------------------------------------------------------------ 开始
Write-Head 'Chill AI 一键体检'

# ============================================================ [1] 运行环境
Write-Section '[1/6] 运行环境'
Info "PowerShell $($PSVersionTable.PSVersion)"
if ($PSVersionTable.PSVersion.Major -ge 5) { Pass 'PowerShell 版本满足要求' } else { Fail 'PowerShell 版本过低（需要 5.1+）' }

if (Test-AspNetCoreRuntime) {
    Pass '已安装 .NET ASP.NET Core 8 运行时'
} else {
    Fail '缺少 .NET ASP.NET Core 8 运行时——Bridge（本地状态服务）会启动即退，女主角不会动'
    Info '必须是 ASP.NET Core Runtime，不是普通的 .NET Runtime。'
    Fix '安装 ASP.NET Core Runtime 8.0 (Windows x64)：https://dotnet.microsoft.com/download/dotnet/8.0'
}

# ============================================================ [2] 安装位置
Write-Section '[2/6] 安装位置'
$gameRoot = $null
if ($GameDir -and (Test-GameDir $GameDir)) {
    $gameRoot = (Resolve-Path -LiteralPath $GameDir).Path
} else {
    $candidates = @(Find-GameDirs)
    if ($candidates.Count -ge 1) { $gameRoot = $candidates[0] }
}

if (-not $gameRoot) {
    Fail '没找到游戏目录（无法继续检查插件安装）'
    Info '如果游戏装在非常规位置，请用：doctor.ps1 -GameDir "你的游戏目录"'
} else {
    Pass "游戏目录：$gameRoot"

    $bepInExDll = Join-Path $gameRoot 'BepInEx\core\BepInEx.dll'
    if (Test-Path -LiteralPath $bepInExDll) { Pass 'BepInEx 已就位' }
    else { Fail "没找到 BepInEx：$bepInExDll"; Fix '确认游戏能正常启动；BepInEx 通常由游戏本体自带' }

    $pluginDir = Join-Path $gameRoot 'BepInEx\plugins\ChillAI'
    if (-not (Test-Path -LiteralPath $pluginDir)) {
        Fail "插件目录不存在：$pluginDir"
        Fix '运行一键安装（install.bat），或把发布包里的 ChillAI 文件夹复制到 BepInEx\plugins\ 下'
    } else {
        $required = @('ChillAI.Plugin.dll', 'ChillAI.Bridge.exe', 'ChillAI.Bridge.dll', 'ChillAI.Bridge.runtimeconfig.json', 'appsettings.json', 'codex-hook.ps1', 'dsh-hook.mjs')
        $missing = @($required | Where-Object { -not (Test-Path -LiteralPath (Join-Path $pluginDir $_)) })
        if ($missing.Count -eq 0) {
            Pass "插件文件齐全（$($required.Count) 个）"
        } else {
            Fail "插件文件缺失：$($missing -join '、')"
            Fix '重新下载发布包并覆盖安装（可能被杀毒软件删掉了 dsh-hook.mjs / codex-hook.ps1）'
        }
        $pluginDll = Join-Path $pluginDir 'ChillAI.Plugin.dll'
        if (Test-Path -LiteralPath $pluginDll) {
            # 注意：程序集 FileVersion 固定是 1.0.0.0（csproj 没设 Version），不代表插件版本，
            # 所以这里只报文件时间（用来识别「是不是装成了旧版」）；真实版本看下面日志里插件自报的那行。
            Info "插件文件时间：$((Get-Item -LiteralPath $pluginDll).LastWriteTime)"
        }
    }
}

# ============================================================ [3] 插件是否加载
Write-Section '[3/6] 游戏插件有没有被加载'
if ($gameRoot) {
    $logs = @(
        (Join-Path $gameRoot 'BepInEx\LogOutput.log'),
        (Join-Path $gameRoot 'output_log.txt')
    ) | Where-Object { Test-Path -LiteralPath $_ }

    if ($logs.Count -eq 0) {
        Warn '还没找到游戏日志（说明游戏可能还没启动过）'
        Fix '启动一次游戏（插件在启动时自动写钩子配置、拉起 Bridge）'
    } else {
        $logPath = $logs[0]
        $logText = Get-Content -LiteralPath $logPath -Raw -ErrorAction SilentlyContinue
        Info "日志：$logPath"
        if ($logText -match '\[ChillAI\]') {
            $loadedLine = ([regex]::Matches($logText, '.*Chill AI [^\r\n]*loaded[^\r\n]*') | Select-Object -Last 1)
            if ($loadedLine) { Pass "插件已加载：$($loadedLine.Value.Trim())" } else { Pass '插件日志存在（[ChillAI] 有输出）' }

            if ($logText -match '已静默启动本地 Bridge|检测到已有 Bridge 在运行') { Pass '插件成功启动/复用了 Bridge' }
            else { Warn '日志里没看到 Bridge 启动记录（可能只启动过一半，或日志被覆盖）' }

            if ($logText -match 'Bridge 进程已退出（退出码') {
                Fail '日志显示 Bridge 启动后立刻退出了'
                Fix '这是缺少 ASP.NET Core 8 运行时的典型症状：安装 ASP.NET Core Runtime 8.0 (Windows x64) 后重开游戏'
            }
            if ($logText -match '未找到 codex-hook\.ps1|未找到 dsh-hook\.mjs') {
                Fail '插件没找到转发脚本（codex-hook.ps1 / dsh-hook.mjs）'
                Fix '确认插件目录里这两个文件都在；被杀毒软件隔离的话加白名单后重新安装'
            }
        } else {
            Warn '日志里没有 [ChillAI] 字样——插件可能没被 BepInEx 加载'
            Fix '确认 ChillAI.Plugin.dll 在 BepInEx\plugins\ChillAI\ 下；重启游戏；仍无则看日志开头的加载错误'
        }
    }
}

# ============================================================ [4] 本地服务
Write-Section '[4/6] 本地状态服务（Bridge）'
$health = Get-BridgeHealth
if ($health -and $health.status -eq 'ok') {
    Pass "Bridge 正在运行（版本 $($health.version)）"

    # 能力标记检查：旧版 Bridge 进程会残留（游戏退出时不一定被回收），
    # 而插件会复用它 —— 于是"装了新版插件却仍按旧逻辑跑"，多工具同时使用时状态互相顶掉。
    $features = @($health.features)
    if ($features -contains 'source-aggregation') {
        Pass 'Bridge 支持多来源聚合（多个工具同时使用不会互相打架）'
    } else {
        Warn '正在运行的 Bridge 是旧版本（没有多来源聚合能力）——同时开两个工具时状态会互相顶掉'
        $stale = @(Get-Process -Name 'ChillAI.Bridge' -ErrorAction SilentlyContinue)
        if ($stale.Count -gt 0) {
            Info "正在结束 $($stale.Count) 个旧 Bridge 进程（下次启动游戏会自动拉起新版）..."
            foreach ($proc in $stale) {
                try { Stop-Process -Id $proc.Id -Force -ErrorAction Stop; Info "  已结束 pid $($proc.Id)" }
                catch { Warn "  结束 pid $($proc.Id) 失败：$($_.Exception.Message)" }
            }
            Start-Sleep -Milliseconds 800
            $script:StartedBridge = $false  # 已经手动停掉了，收尾时不需要再停
            $health = Get-BridgeHealth
            if ($health) {
                Warn '端口上仍有 Bridge 在响应（可能刚被别的进程重新拉起）；重启游戏即可用上新版'
            } else {
                Pass '旧 Bridge 已结束'
            }
            Fix '重启一次游戏，让插件拉起新版 Bridge（新版才支持多工具聚合）'
        } else {
            Fix '结束旧的 ChillAI.Bridge 进程后重启游戏（或重跑一键安装）'
        }
    }
} else {
    $owner = Get-PortOwner 17860
    if ($owner) {
        Fail "17860 端口被占用，但它不是 ChillAI Bridge（占用者：$($owner.ProcessName)，pid $($owner.Id)）"
        Fix "关闭 $($owner.ProcessName)（pid $($owner.Id)），或改插件目录 appsettings.json 里的 ListenUrl 端口"
    } else {
        Warn 'Bridge 没在运行'
        if ($gameRoot) {
            $exe = Join-Path $gameRoot 'BepInEx\plugins\ChillAI\ChillAI.Bridge.exe'
            if (Test-Path -LiteralPath $exe) {
                Info '为继续排查，临时启动一次 Bridge...'
                try {
                    Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -WindowStyle Hidden | Out-Null
                    Start-Sleep -Seconds 4
                    $health = Get-BridgeHealth
                    if ($health -and $health.status -eq 'ok') {
                        $script:StartedBridge = $true
                        Pass '临时启动成功（游戏启动时也会这样自动拉起）'
                        Info '注意：这个 Bridge 是体检脚本临时拉起的，体检结束时会自动停掉它。'
                    } else {
                        Fail '手动启动也起不来——最常见原因就是缺 ASP.NET Core 8 运行时'
                        Fix '安装 ASP.NET Core Runtime 8.0 (Windows x64)：https://dotnet.microsoft.com/download/dotnet/8.0'
                    }
                } catch {
                    Fail "启动 Bridge 失败：$($_.Exception.Message)"
                }
            } else {
                Fail "插件目录里没有 ChillAI.Bridge.exe"
                Fix '重新安装插件文件'
            }
        }
    }
}

$status = Get-BridgeStatus
$events = Get-BridgeEvents
if ($status) {
    $stateText = $stateChinese[$status.state]
    if (-not $stateText) { $stateText = $status.state }
    Info "当前状态：$stateText（最近事件：$($status.lastEvent)；距今 $($status.secondsSinceLastEvent) 秒）"
}
if ($null -ne $events) {
    if ($events.Count -eq 0) {
        Warn 'Bridge 还没收到过任何工具事件'
        Fix '去你的 AI 工具里跑一个任务（Codex 需要先在设置里打开 Hooks 并批准信任）；然后重新运行体检'
    } else {
        Pass "Bridge 已收到 $($events.Count) 条事件（说明链路至少通过一次）"
        $sources = @($events | ForEach-Object { if ($_.source) { $_.source } else { '(未标注)' } } | Select-Object -Unique)
        Info "  涉及来源：$($sources -join '、')（多来源各自记账后聚合，不会互相打架）"
        $tail = $events | Select-Object -Last 5
        foreach ($e in $tail) {
            $detail = ''
            if ($e.detail) { $detail = "  detail=$($e.detail)" }
            $src = ''
            if ($e.source) { $src = "  [$($e.source)]" }
            Info ("  {0}  {1} -> {2}{3}{4}" -f $e.atUtc, $e.event, $e.stateAfter, $src, $detail)
        }
    }
}

# ============================================================ [5] 各工具配置
Write-Section '[5/6] 各 AI 工具的钩子配置'
$home2 = $env:USERPROFILE
$anyTool = $false

# --- Codex ---
$codexDir = Join-Path $home2 '.codex'
if (Test-Path -LiteralPath $codexDir) {
    $anyTool = $true
    $hooksJson = Join-Path $codexDir 'hooks.json'
    if (Test-Path -LiteralPath $hooksJson) {
        $text = Get-Content -LiteralPath $hooksJson -Raw -ErrorAction SilentlyContinue
        $count = ([regex]::Matches($text, 'codex-hook\.ps1')).Count
        if ($count -gt 0) { Pass "Codex：hooks.json 里有 $count 处指向本插件转发脚本" }
        else { Fail 'Codex：hooks.json 存在，但没有本插件的钩子条目'; Fix '游戏内按 F8，确认「启用 Codex 联动」是开的，然后重启游戏让它重写配置' }
    } else {
        Warn 'Codex：没有 ~/.codex/hooks.json（插件还没写过）'
        Fix '启动一次游戏（F8 里「启用 Codex 联动」保持开启）'
    }
    $configToml = Join-Path $codexDir 'config.toml'
    if (Test-Path -LiteralPath $configToml) {
        if ((Get-Content -LiteralPath $configToml -Raw) -match 'codex_hooks\s*=\s*true') { Pass 'Codex：config.toml 里 codex_hooks = true' }
        else { Warn 'Codex：config.toml 里没有 codex_hooks = true（CLI 通道需要）'; Fix '在 Codex 桌面 App 的设置里打开 Hooks 开关（App 会自己持久化这个键）' }
    }
    Info '别忘了：Codex 桌面 App 里要打开 Hooks 开关，并且首次运行钩子时点「允许/信任」。'
} else {
    Info 'Codex：本机没装（跳过）'
}

# --- ZCode ---
$zcodeConfig = Join-Path $home2 '.zcode\cli\config.json'
if (Test-Path -LiteralPath (Join-Path $home2 '.zcode')) {
    $anyTool = $true
    if (Test-Path -LiteralPath $zcodeConfig) {
        $text = Get-Content -LiteralPath $zcodeConfig -Raw -ErrorAction SilentlyContinue
        $count = ([regex]::Matches($text, 'codex-hook\.ps1')).Count
        if ($count -gt 0) { Pass "ZCode：config.json 里有 $count 处本插件钩子" }
        else { Fail 'ZCode：config.json 里没有本插件钩子'; Fix 'F8 确认「启用 ZCode 联动」开启后重启游戏' }
        if ($text -match '"enabled"\s*:\s*false') { Warn 'ZCode：hooks.enabled 为 false，钩子不会执行'; Fix '在 ZCode 配置里把 hooks.enabled 改成 true' }
    } else {
        Warn 'ZCode：没有 ~/.zcode/cli/config.json（插件还没写过）'
        Fix '启动一次游戏'
    }
    Info '别忘了：ZCode 在会话启动时快照钩子配置——改完要新建一个会话。'
} else {
    Info 'ZCode：本机没装（跳过）'
}

# --- Antigravity ---
$antigravityHooks = Join-Path $home2 '.gemini\config\hooks.json'
if (Test-Path -LiteralPath (Join-Path $home2 '.gemini')) {
    $anyTool = $true
    if (Test-Path -LiteralPath $antigravityHooks) {
        $text = Get-Content -LiteralPath $antigravityHooks -Raw -ErrorAction SilentlyContinue
        if ($text -match 'chill-ai-sync') { Pass 'Antigravity：hooks.json 里有 chill-ai-sync 条目' }
        else { Fail 'Antigravity：hooks.json 里没有 chill-ai-sync'; Fix 'F8 确认「启用 Antigravity 联动」开启后重启游戏' }
    } else {
        Warn 'Antigravity：没有 ~/.gemini/config/hooks.json（插件还没写过）'
        Fix '启动一次游戏'
    }
    Info '别忘了：改完重启一次 Antigravity。'
} else {
    Info 'Antigravity：本机没装（跳过）'
}

# --- DeepSeek Harness (dsh) ---
$dshRoot = Join-Path $home2 '.dsh'
if (Test-Path -LiteralPath $dshRoot) {
    $anyTool = $true
    $profileName = $env:DSH_PROFILE
    $profilesRoot = Join-Path $dshRoot 'profiles'
    $profileDir = $null
    if ($profileName -and (Test-Path -LiteralPath (Join-Path $profilesRoot $profileName))) {
        $profileDir = Join-Path $profilesRoot $profileName
    } elseif (Test-Path -LiteralPath (Join-Path $profilesRoot 'desktop')) {
        $profileDir = Join-Path $profilesRoot 'desktop'
    } elseif (Test-Path -LiteralPath (Join-Path $profilesRoot 'web')) {
        $profileDir = Join-Path $profilesRoot 'web'
    }
    if (-not $profileDir) {
        Warn 'dsh：没找到 profile 目录'
        Fix '确认 DeepSeek Harness 至少启动过一次'
    } else {
        Info "dsh profile：$profileDir"
        $hookFile = Join-Path $profileDir 'dsh-hook.mjs'
        $patchFile = Join-Path $profileDir 'cordis.patch.yml'
        if (Test-Path -LiteralPath $hookFile) { Pass 'dsh：dsh-hook.mjs 已就位' }
        else { Fail 'dsh：profile 目录里没有 dsh-hook.mjs'; Fix 'F8 确认「启用 DSH 联动」开启后重启游戏' }

        if (Test-Path -LiteralPath $patchFile) {
            $text = Get-Content -LiteralPath $patchFile -Raw -ErrorAction SilentlyContinue
            if ($text -match 'chillai-sync') { Pass 'dsh：cordis.patch.yml 里已挂载 chillai-sync' }
            else { Fail 'dsh：cordis.patch.yml 里没有挂载条目'; Fix 'F8 确认「启用 DSH 联动」开启后重启游戏' }
        } else {
            Fail 'dsh：profile 目录里没有 cordis.patch.yml'
        }
        Info '别忘了：dsh 挂载后要重启一次 DeepSeek Harness 才会加载该插件。'
        if ($profileName -and $profileDir -notlike "*$profileName") {
            Warn "dsh：环境变量 DSH_PROFILE=$profileName，但实际检查的是 $profileDir"
        }
    }
} else {
    Info 'DeepSeek Harness：本机没装（跳过）'
}

if (-not $anyTool) {
    Warn '没有检测到任何 AI 工具（Codex / ZCode / Antigravity / dsh）'
    Info '先装一个并登录，再回来跑体检。'
}

# ============================================================ [6] 通路测试
if ($TestEvents -and (Get-BridgeHealth)) {
    Write-Section '[6/6] 通路测试（往 Bridge 打假事件）'
    Info '这一步只验证 Bridge 本身是否正常，与你的 AI 工具无关。'
    try {
        $null = Invoke-RestMethod -Uri 'http://127.0.0.1:17860/codex/events' -Method Post -TimeoutSec 3 `
            -ContentType 'application/json' -Body '{"event":"SessionStart"}'
        Start-Sleep -Milliseconds 500
        $s1 = Get-BridgeStatus
        if ($s1.state -eq 'working') { Pass 'Bridge 状态机正常：SessionStart → working' }
        else { Fail "Bridge 状态机异常：期望 working，实际 $($s1.state)" }
        $null = Invoke-RestMethod -Uri 'http://127.0.0.1:17860/codex/events' -Method Post -TimeoutSec 3 `
            -ContentType 'application/json' -Body '{"event":"SessionEnd"}'
        Start-Sleep -Milliseconds 500
        $s2 = Get-BridgeStatus
        if ($s2.state -eq 'idle') { Pass 'Bridge 状态机正常：SessionEnd → idle' }
        else { Fail "Bridge 状态机异常：期望 idle，实际 $($s2.state)" }
    } catch {
        Fail "通路测试失败：$($_.Exception.Message)"
    }
}

# ============================================================ 结论
Write-Head '体检结论'
Write-Host "  通过 $($script:Pass) 项 · 注意 $($script:Warn) 项 · 失败 $($script:Fail) 项" -ForegroundColor White

if ($script:NextSteps.Count -gt 0) {
    Write-Host ''
    Write-Host '  接下来做什么（按顺序）：' -ForegroundColor Yellow
    $i = 1
    foreach ($step in ($script:NextSteps | Select-Object -Unique)) {
        Write-Host "   $i) $step" -ForegroundColor Gray
        $i++
    }
} elseif ($script:Fail -eq 0 -and $script:Warn -eq 0) {
    Write-Host ''
    Write-Host '  一切正常。' -ForegroundColor Green
    Write-Host '  在 AI 工具里跑一个任务，游戏左上角浮窗应显示「正在工作」。' -ForegroundColor Gray
}

Write-Host ''
Write-Host '  提示：游戏必须处于运行状态，女主角才会跟着动。' -ForegroundColor DarkGray
Write-Host '  完整排查手册见仓库 docs/TROUBLESHOOTING.md。' -ForegroundColor DarkGray

# 体检脚本自己拉起的 Bridge 要负责停掉：留着它会占用插件目录里的文件，
# 导致下一次安装/覆盖复制失败（真实踩过的坑）。
if ($script:StartedBridge) {
    Write-Host ''
    Info '正在停止体检脚本临时启动的 Bridge（避免占用插件文件）...'
    Get-Process -Name 'ChillAI.Bridge' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
}
Write-Host ''

if ($Pause) {
    Write-Host '按任意键关闭此窗口...' -ForegroundColor DarkGray
    $null = $Host.UI.RawUI.ReadKey('NoEcho,IncludeKeyDown')
}
exit $(if ($script:Fail -gt 0) { 1 } else { 0 })
