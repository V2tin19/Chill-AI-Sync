# 故障排查手册

面向零基础用户。**遇到任何问题，先做第 0 步**——90% 的情况它会直接告诉你原因。

---

## 0. 先做这一步：一键体检

双击插件目录里的 **`doctor.bat`**（安装时自动放在这里）：

```text
<游戏目录>\BepInEx\plugins\ChillAI\doctor.bat
```

它会按顺序检查 6 段，并给出「接下来做什么」：

| 检查段 | 看什么 |
|---|---|
| [1] 运行环境 | PowerShell 版本、**.NET ASP.NET Core 8 运行时**是否安装 |
| [2] 安装位置 | 游戏目录、BepInEx、插件 7 个必需文件是否齐全 |
| [3] 插件加载 | 读游戏日志，确认 `[ChillAI]` 插件真的被加载了 |
| [4] 本地服务 | Bridge 端口 / `/health` / 当前状态 / 最近事件 |
| [5] 工具配置 | Codex / ZCode / Antigravity / dsh 的钩子条目在不在 |
| [6] 结论 | 通过/注意/失败 计数 + 按顺序列出的修复动作 |

它只读不改（两个例外：Bridge 没在跑时会临时帮你启动一次，**体检结束会自动停掉**；
检测到端口上是**旧版本** Bridge 时会帮你结束它，好让下次启动游戏拉起新版）。
想额外验证 Bridge 状态机，可以加参数：

```powershell
.\doctor.bat -TestEvents      # 会往 Bridge 打假事件，女主角可能动一下
```

---

## 1. 快速自查表

| 症状 | 最可能的原因 | 怎么修 |
|---|---|---|
| 双击 `install.bat` 一闪而过 | 用的是压缩包预览，没真正解压 | 先「全部解压」到普通文件夹，再双击 |
| 安装时找不到游戏 | 游戏在非常规 Steam 库位置 | 用命令行指定：`install.bat -GameDir "你的游戏目录"` |
| 复制失败 / 文件被占用 | 游戏或 Bridge 还在运行 | 完全关闭游戏再装（安装程序会自动停掉旧 Bridge） |
| 浮窗一直不出现 | 「显示状态浮窗」被关了 / 插件没加载 | 游戏内按 **F8** 看设置；再跑 `doctor.bat` 看第 3 段 |
| 浮窗显示「Bridge 未连接」 | **缺 .NET ASP.NET Core 8 运行时**（最常见） | 见下面第 4.1 节 |
| 状态一直是 `unknown` | 工具钩子没触发（Codex 没批准信任最常见） | 见下面第 3 节对应工具 |
| **同时开两个工具时状态乱跳** | 正在跑的 Bridge 是**旧版本**（不支持多来源聚合） | 见下面第 2.2 节「旧 Bridge 残留」 |
| 女主角完全不动 | 没开「工具优先」，或没有任何工具联动开着 | F8 里打开「工具优先」+ 至少一个「启用 XX 联动」 |
| 女主角卡在工作动画 | Bridge 崩了/被关了，状态冻结在 working | 重启游戏；再跑 `doctor.bat` 看第 4 段 |
| 动作抽风 | 「工具优先」开着时属正常现象 | 关掉「工具优先」恢复游戏原生番茄钟 |
| 改了配置没生效 | 各工具有各自的"快照/重启"时机 | 见下面第 3 节（ZCode 要新会话、dsh 要重启） |

---

## 2. 手动逐段排查（体检脚本不够用时）

整条链路是：

```text
AI 工具 ──钩子事件──▶ 转发脚本 ──HTTP──▶ ChillAI.Bridge ──轮询──▶ 游戏插件 ──▶ 女主角
```

从右往左查最快（越靠右越基础）。

### 2.1 游戏与插件

1. 插件目录里应有这 7 个文件：

   ```text
   <游戏目录>\BepInEx\plugins\ChillAI\
     ChillAI.Plugin.dll
     ChillAI.Bridge.exe
     ChillAI.Bridge.dll
     ChillAI.Bridge.runtimeconfig.json
     appsettings.json
     codex-hook.ps1
     dsh-hook.mjs
   ```

   少文件 = 被杀毒软件删了或下载不完整 → 重新解压覆盖安装。

2. 看游戏日志（BepInEx 5）：

   ```powershell
   Select-String -Path "<游戏目录>\BepInEx\LogOutput.log" -Pattern '\[ChillAI\]' | Select-Object -Last 20
   ```

   正常应包含 `Chill AI 1.0.9 loaded` 和 `已静默启动本地 Bridge`。
   出现 `Bridge 进程已退出（退出码 …）` → 直接跳到 4.1 节。

### 2.2 本地服务（Bridge）

```powershell
# 健康检查
Invoke-RestMethod http://127.0.0.1:17860/health

# 当前状态（state / lastEvent / 距今多少秒）
Invoke-RestMethod http://127.0.0.1:17860/codex/status

# 最近 50 条事件历史（排查"到底有没有事件进来"最有用）
Invoke-RestMethod http://127.0.0.1:17860/codex/events | Format-Table event, detail, stateAfter, atUtc
```

| 结果 | 含义与处理 |
|---|---|
| 连不上 | Bridge 没起来。手动跑一次 `<插件目录>\ChillAI.Bridge.exe` 看窗口报错；最常见就是缺 ASP.NET Core 8 运行时 |
| `/health` 正常但 `/codex/events` 为空 | Bridge 没问题，是**工具侧事件没进来** → 查第 3 节 |
| `/codex/events` 有事件，但游戏里状态不变 | 游戏插件侧问题：确认游戏在运行、`doctor.bat` 第 3 段通过 |
| 端口被别的程序占用 | 见 4.2 节改端口 |
| 同时开两个工具时状态乱跳 | 见下面「旧 Bridge 残留」 |

#### 旧 Bridge 残留（升级后必查）

游戏退出时不一定会回收 Bridge 进程，而插件发现 17860 上已有 Bridge 会**复用它**。
所以「装了新版插件、但端口上还是旧 Bridge」时，新逻辑（多工具聚合）会**静默不生效**，
表现为：同时开两个工具时状态互相顶掉（一个工具 `Stop` 就让另一个正在干活的工具状态被清掉）。

一条命令判断：

```powershell
Invoke-RestMethod http://127.0.0.1:17860/health
# 新版会返回 features：{"status":"ok","version":"1.0.9","features":["source-aggregation"]}
# 没有 features 字段 = 旧版
```

处理（任选其一）：

- **重启一次游戏**：插件启动时会自己发现旧 Bridge 并替换掉（已内置自愈）；
- 或手动结束再重启游戏：`Stop-Process -Name ChillAI.Bridge -Force`
- 或跑 `doctor.bat`：第 4 段会检测并自动清理旧 Bridge。

### 2.3 事件是否真的发出去了

不想开游戏，只想验证「AI 工具 → Bridge」这一段：

1. 先手动启动 Bridge（`<插件目录>\ChillAI.Bridge.exe`）。
2. 在你的 AI 工具里跑一个任务。
3. 立刻查：

   ```powershell
   Invoke-RestMethod http://127.0.0.1:17860/codex/events | Select-Object -Last 5
   ```

   有新事件 = 工具侧通了；仍为空 = 按第 3 节查该工具的钩子。

---

## 3. 各 AI 工具的特有注意事项

插件会**自动写入**所有钩子配置（`F8` 里可随时开关），但每个工具都有一道**只有你能做**的步骤：

### Codex（ChatGPT 桌面应用 / CLI）

- **必须手动做**：桌面 App 里打开 **Hooks 开关**，并且**首次运行钩子时点「允许/信任」**。
  这两件事是分开的：只开开关不批准信任，钩子会被**静默跳过**——这是"状态一直 unknown"最常见的原因。
- 配置文件：`~/.codex/hooks.json`（插件自动写，6 个事件）、`~/.codex/config.toml` 里的 `codex_hooks = true`。
- 已知坑：桌面 App 会用内部状态**重写 `config.toml`**，把它不认识的键清掉。
  所以请**用 App 设置界面**打开 Hooks，不要只改 `config.toml`；插件每次启动会重新确保这个键。
- 出现 `clamping ... timeout` 警告：无害，忽略。

### ZCode（z.ai GLM）

- **必须手动做**：改完配置后**新建一个会话**——ZCode 在会话启动时**快照**钩子配置。
- 配置文件：`~/.zcode/cli/config.json`（Claude Code hook schema）。
- 如果该文件里 `hooks.enabled` 是 `false`，钩子不会执行；插件会提示但**不会替你改**（尊重你的设置）。

### Antigravity（Google / AGY）

- **必须手动做**：**重启一次** Antigravity。
- 配置文件：`~/.gemini/config/hooks.json`（键名 `chill-ai-sync`；你其他的 named hooks 不会被动）。

### DeepSeek Harness（dsh）

- **必须手动做**：挂载后**重启一次 dsh**（新插件的 JS 模块要重新加载一代）。
- 插件会把 `dsh-hook.mjs` 复制到**当前 profile 目录**，并在 `cordis.patch.yml` 里挂载 `./dsh-hook.mjs`。
- profile 定位顺序：环境变量 `DSH_PROFILE` → `desktop` → `web` → 唯一子目录。
  桌面版默认是 `desktop`；如果你用的是 Web 版，确认 `DSH_PROFILE` 指向正确。
- 验证挂载：

  ```powershell
  Get-Content "$env:USERPROFILE\.dsh\profiles\desktop\cordis.patch.yml" | Select-Object -Last 6
  ```

  应能看到 `id: chillai-sync` 那段带标记的区块。
- dsh 是开发者预览版，官方会有破坏性变更；若升级 dsh 后失效，重新跑一次游戏让插件重写挂载即可。

#### 「dsh 在等我，但女主没反应」

dsh 里"智能体停下来等你"有**两种**机制，插件都做了映射（游戏里都显示"等待你审批"、女主想找你说话）：

| 你在 dsh 里看到 | dsh 内部事件 | 插件映射 |
|---|---|---|
| 权限审批弹窗（如"是否允许提权到 full access"） | 会话事件 `approval/asked` | `PermissionRequest` |
| 智能体**问你问题**（`ask_user_question` 选择框） | 只是一次普通的 `tool/call`（工具名 `ask_user_question`） | `PermissionRequest` |

排查步骤：

1. 确认 `~/.dsh/profiles/<profile>/dsh-hook.mjs` 是**较新版本**（文件里应能搜到 `ask_user_question`）：

   ```powershell
   Select-String -Path "$env:USERPROFILE\.dsh\profiles\desktop\dsh-hook.mjs" -Pattern 'ask_user_question'
   ```

   搜不到 = 旧版（老版本只处理权限审批，不处理"智能体提问"）→ 启动一次游戏让插件重新复制，或重启 dsh。

2. 跑一次 `doctor.bat`，看第 4 段的**最近事件**里有没有 `PermissionRequest`：

   ```powershell
   Invoke-RestMethod http://127.0.0.1:17860/codex/events | Select-Object -Last 5
   ```

   - 有 `PermissionRequest` 但游戏没反应 → 游戏侧问题（确认游戏在运行、第 3 段通过）；
   - 完全没有 → 说明该事件没被插件转发（确认上面的版本检查）。

3. **「工具优先」必须是开的**。女主状态的**维持**（每 4 秒一次的温和校正）只在
   「工具优先（覆盖番茄钟）」开启时运行；关着的时候插件只在状态**变化那一刻**点一下，
   之后她会被游戏自身的逻辑带回闲置——看起来就像"没反应"。

4. 直接绕过 dsh，单独验证游戏侧（最省事的隔离测法）：

   ```powershell
   # 模拟"dsh 在等你审批"
   Invoke-RestMethod http://127.0.0.1:17860/codex/events -Method Post `
     -ContentType 'application/json' -Body '{"event":"PermissionRequest","source":"dsh"}'
   # 结束测试
   Invoke-RestMethod http://127.0.0.1:17860/codex/events -Method Post `
     -ContentType 'application/json' -Body '{"event":"approval/decided","source":"dsh"}'
   ```

   > 必须带 `"source":"dsh"`：Bridge 按来源聚合，而 `working` 优先级高于 `waitingreview`，
   > 所以要覆盖 dsh 这个来源本身，否则 agent 一跑工具就把状态顶回"工作中"。

5. 还不行就看驱动层日志。**注意一个坑**：插件里 `HeroineDriver` 的日志如果只写
   `UnityEngine.Debug.Log`，会被编译器整个去掉（连字符串一起），日志里什么都看不到。
   现在它已改为同时写 BepInEx logger，所以下面这类行应该出现：

   ```powershell
   Select-String -Path "<游戏目录>\BepInEx\LogOutput.log" -Pattern '\[ChillAI\]' | Select-Object -Last 20
   ```

   正常应能看到：

   ```text
   [ChillAI] 已找到女主角 HeroineAI
   [ChillAI] 工具状态 waitingreview → 女主 WorkPC -> WantTalk（调用后回读 WantTalk）
   [ChillAI] 校正女主 -> 想找你说话（工具在等你，原状态 None）
   ```

   - 没有 `已找到女主角` → 插件没找到女主实例（可能还在加载场景）；
   - 有 `调用后回读` 但回读值不是 `WantTalk` → 游戏没接受这次状态切换（把这几行发出来）；
   - 一条都没有 → 装的还是旧版插件（旧版这些日志被编译掉了，日志里就是空白）。

3. 注意时序：dsh 的审批/提问都是**会话事件**，`session/event` 实时通道已验证可用
   （同 profile 的 `dsh-cost-meter` 就是靠它实时记账的）。若怀疑通道没通，
   直接看游戏日志里的 `[ChillAI]` 行。

---

## 4. 环境类问题

### 4.1 缺少 .NET ASP.NET Core 8 运行时（最高频）

**症状**：浮窗显示「Bridge 未连接」；日志出现 `Bridge 进程已退出（退出码 …）`；`doctor.bat` 第 4 段手动启动也起不来。

**原因**：Bridge 需要的是 **ASP.NET Core Runtime**，不是普通的 .NET Runtime。只装基础运行时会启动即退。

**修法**：

1. 打开 https://dotnet.microsoft.com/download/dotnet/8.0
2. 选 **ASP.NET Core Runtime 8.0 → Windows → x64 Installer**（不要选 .NET Runtime）
3. 安装后重启游戏。

自查：

```powershell
dotnet --list-runtimes | Select-String 'Microsoft.AspNetCore.App 8.'
```

有输出即 OK。

### 4.2 端口 17860 被占用

**症状**：Bridge 起不来，或 `/health` 返回的不是我们的服务。

```powershell
Get-NetTCPConnection -LocalPort 17860 -State Listen | Select-Object OwningProcess
Get-Process -Id <上面的 OwningProcess>
```

- 占用者是 `ChillAI.Bridge` → 正常（插件会复用它）。
- 是别的程序 → 关掉它；或改端口：编辑 `<插件目录>\appsettings.json`：

  ```json
  { "Bridge": { "ListenUrl": "http://127.0.0.1:17861" } }
  ```

  同时把 `codex-hook.ps1` 里的 `$bridgeUrl` 与 `dsh-hook.mjs` 里的默认地址改成同一端口
  （或给 dsh 设环境变量 `CHILLAI_BRIDGE_URL`）。

### 4.3 杀毒软件误删 `codex-hook.ps1` / `dsh-hook.mjs`

**症状**：日志出现 `未找到 codex-hook.ps1` / `未找到 dsh-hook.mjs`；`doctor.bat` 报插件文件缺失。

**修法**：把插件目录加入杀毒软件白名单，然后重新解压覆盖安装。

### 4.4 PowerShell 中文显示成乱码

本项目的 `.ps1` 都带 UTF-8 BOM，Windows PowerShell 5.1 也能正确显示。
若你改过脚本导致乱码：用记事本「另存为」选 **UTF-8 带 BOM**。
（`.bat` 文件刻意只用 ASCII 文件名与内容，避免解压/编码问题。）

---

## 5. 女主角行为异常

| 现象 | 说明 |
|---|---|
| 完全不动 | 「工具优先」没开，或所有「启用 XX 联动」都关着；或 Bridge 未连接 / 还没收到过任何事件（此时**故意**交还游戏原生驱动） |
| 卡在工作动画 | 状态冻结在 `working`。先看 `/codex/status`：若 Bridge 连不上，重启游戏即可恢复；插件在断连时会把状态复位为 `unknown` 并释放女主 |
| 动作抽风 | 「工具优先」开启时属**正常现象**（女主由工具状态驱动，与番茄钟抢控制权）；关掉「工具优先」即恢复游戏原生行为 |
| 工具空闲时反复去喝茶 | v1.0.6 起已改为纯事件驱动，不会再强拉；若仍出现，确认插件是较新版本 |
| 番茄钟运行中自言自语 | v1.0.6 起工具优先 + 番茄钟计时中会拦截自言自语入口 |

---

## 6. 安装 / 卸载

### 全新安装（零基础）

1. 下载 `ChillAI-vX.Y.Z.zip`，**完整解压**到普通文件夹。
2. **关闭游戏**，双击 `install.bat`，按提示走。
3. 按屏幕提示，在你自己用的 AI 工具里做第 3 节那道手动步骤。
4. 启动游戏，左上角出现浮窗即成功。

### 卸载

1. 游戏内按 **F8**，把「启用 Codex/ZCode/Antigravity/DSH 联动」都关掉
   → 插件会自动摘除自己写进各工具配置里的条目（**你自己的配置一律保留**）。
2. 关闭游戏，删除 `<游戏目录>\BepInEx\plugins\ChillAI` 整个文件夹。

### 没先关开关就删了文件夹

手动清理残留（只删我们自己的条目，别整文件删）：

| 工具 | 位置 | 删什么 |
|---|---|---|
| Codex | `~/.codex/hooks.json` | 引用 `codex-hook.ps1` 的条目（若整个文件只有这些，可删文件） |
| Codex | `~/.codex/config.toml` | `codex_hooks = true` 那行（可选） |
| ZCode | `~/.zcode/cli/config.json` | `hooks.events` 里引用 `codex-hook.ps1` 的条目 |
| Antigravity | `~/.gemini/config/hooks.json` | `chill-ai-sync` 键 |
| dsh | `~/.dsh/profiles/<profile>/cordis.patch.yml` | 带 `Chill AI DSH 联动` 标记的那段区块 |
| dsh | `~/.dsh/profiles/<profile>/dsh-hook.mjs` | 这个文件本身 |

---

## 7. 还是不行？

反馈问题时请带上这些（能极大加快定位）：

1. **`doctor.bat` 的完整输出**（截图或复制文本）。
2. `Invoke-RestMethod http://127.0.0.1:17860/codex/status` 的结果。
3. `Invoke-RestMethod http://127.0.0.1:17860/codex/events` 的结果。
4. 游戏日志里所有 `[ChillAI]` 开头的行：

   ```powershell
   Select-String -Path "<游戏目录>\BepInEx\LogOutput.log" -Pattern '\[ChillAI\]' | Select-Object -Last 30
   ```

5. 你用的是哪个 AI 工具、版本号，以及是否已经做过第 3 节那道手动步骤。

> 提交渠道见仓库 README（GitHub Issues）。
