# Codex 工作状态通道（Status Channel）

把 OpenAI Codex（ChatGPT 桌面应用 / Codex CLI）的**真实工作状态**，通过官方 **Hooks（生命周期钩子）**机制实时转发到本地 Bridge，再由任意客户端（游戏插件、看板等）轮询。

核心特点：

- **事件驱动，零轮询**：状态由 Codex 主动推送，不需要你这边做任何轮询检测
- **App / CLI 双通道**：桌面应用和命令行跑任务都能触发
- **官方机制**：Hooks 是 Codex 官方可扩展框架（`developers.openai.com/codex/hooks`），无需逆向或侵入式修改
- **轻量**：转发脚本几毫秒完成；Bridge 为 .NET 8 单进程，内存占用可忽略

## 架构

```
Codex 桌面 App / Codex CLI
        │  生命周期事件（SessionStart / UserPromptSubmit /
        │  PostToolUse / PermissionRequest / Stop / SessionEnd）
        ▼
hooks.json ──► codex-hook.ps1（转发脚本，几毫秒）
        │  POST http://127.0.0.1:17860/codex/events
        ▼
ChillAI.Bridge（状态机）
        │  GET /codex/status
        ▼
游戏插件 / 其他客户端
```

## 快速开始（约 5 分钟）

### 前置

- Codex 桌面 App（或 CLI）已登录可用
- .NET 8（运行 Bridge；编译才需要 SDK）

### 第 1 步：配置 Codex 钩子

1. 把 `scripts/codex-hooks.json` 复制到你的 Codex 数据目录：

   ```powershell
   # Windows
   copy scripts\codex-hooks.json $HOME\.codex\hooks.json
   # macOS / Linux
   cp scripts/codex-hooks.json ~/.codex/hooks.json
   ```

2. **编辑 `~/.codex/hooks.json`，把所有 `command` 里的路径改成你 clone 本仓库的实际路径**（当前是 `D:\gitstuff\Chill_with_AI`，换成你自己的）。

3. 在 `~/.codex/config.toml` 顶部加入（如果 `[features]` 已存在就直接加一行）：

   ```toml
   [features]
   codex_hooks = true
   ```

4. **打开信任（关键，缺了钩子不执行）**：

   - **桌面 App**：设置 → Hooks（功能开关）打开；**并且**在钩子首次运行时批准"钩子信任"（App 里开关和信任是两回事，都要做）
   - **CLI**：交互模式运行一次 `codex`，在提示时批准钩子信任；自动化场景可用 `codex exec --dangerously-bypass-hook-trust "任务"` 跳过（仅单次调用生效）

5. 重启 Codex。

### 第 2 步：启动 Bridge

```powershell
# 自动编译（如需要）并启动
powershell -ExecutionPolicy Bypass -File scripts\run-bridge.ps1
# 或直接用编译好的
dotnet src\ChillAI.Bridge\bin\Release\net8.0\ChillAI.Bridge.dll
```

看到 `Now listening on: http://127.0.0.1:17860` 即成功。**这个窗口保持打开**。

### 第 3 步：验证

```powershell
Invoke-RestMethod http://127.0.0.1:17860/codex/status
```

初始为 `unknown`。然后在 Codex 里跑一个任务，期间再查，应看到：

| 时机 | state | 说明 |
|---|---|---|
| 任务执行中 | `working` | SessionStart / UserPromptSubmit / PostToolUse 触发 |
| 任务刚完成 | `justdone` | Stop 触发，15 秒后自动衰减为 `waiting` |
| 等待你输入 | `waiting` | 正常待命 |
| 弹出审批 | `waitingreview` | PermissionRequest 触发 |
| 会话结束 | `idle` | SessionEnd 触发 |

## 状态机

| 事件 | 推导状态 | 建议游戏行为 |
|---|---|---|
| SessionStart / UserPromptSubmit / taskstarted / PostToolUse | `working` | Focus：角色安静陪伴 |
| PermissionRequest | `waitingreview` | 提醒："有个决定需要你" |
| taskcomplete / stop | `justdone`（15s 后衰减 `waiting`） | 庆祝动画 + 祝贺 |
| turnaborted / sessionend | `idle` | 收工告别 |
| （兜底）working 超 10 分钟无事件 | `waiting` | 防止钩子静默失效后卡死 |

状态变更同时记录最近 50 条事件历史（`GET /codex/events`），便于诊断和做更细的行为。

## HTTP API

| 端点 | 方法 | 说明 |
|---|---|---|
| `/health` | GET | 健康检查 `{"status":"ok"}` |
| `/codex/status` | GET | 当前状态（state / detail / sinceUtc / lastEvent / secondsSinceLastEvent） |
| `/codex/events` | GET | 最近 50 条事件历史（event / detail / atUtc / stateAfter） |
| `/codex/events` | POST | 上报事件 `{"event":"Stop","detail":"可选"}`（大小写不敏感） |

## 故障排查

| 症状 | 原因与解法 |
|---|---|
| 状态一直是 `unknown` | Bridge 没启动；或钩子没触发 → 检查 App/CLI 的钩子**信任**是否已批准（最常见） |
| 状态停在 `waiting` 不变 | 钩子事件没进来 → 查 `GET /codex/events` 历史；App 里确认 Hooks 开关 + 信任都开了 |
| `clamping ... timeout to 3s` 警告 | hooks.json 里 SessionEnd 等事件的 `timeout` 超过 3s，改成 `3` 即可（官方上限） |
| 重启 App 后 `codex_hooks` 配置消失 | 桌面 App 会用内部状态重写 config.toml，清掉它不认识的键 → 在 **App 设置里**打开 Hooks 开关（App 自己持久化），不要只改 config.toml |
| 端口 17860 被占用 | 先杀旧进程再启动（`Stop-Process` / 任务管理器） |
| 模型报 503 / No available channel | 与钩子无关，是模型代理（如 cc-switch）的通道问题 |
| 转发脚本静默失败 | 脚本设计为 Bridge 未启动时静默退出、不阻塞 Codex；确认 Bridge 已启动 |

## 已知限制

- 桌面 App 的"Hooks 开关"和"钩子信任"是**两个独立步骤**，漏了信任钩子会被静默跳过
- 手动写进 config.toml 的键可能被桌面 App 重写清除——App 侧请优先用设置界面开关
- Hooks 事件粒度是"回合 / 工具调用"级（毫秒到秒级延迟），足够驱动游戏角色，但不是逐 token 流
- 钩子 `timeout` 官方上限 3 秒（部分事件），转发脚本必须保持轻量

## 与其他 AI 工具的兼容性

本通道基于 **Codex Hooks 规范**：`~/.codex/hooks.json`（6 类标准事件：SessionStart / UserPromptSubmit / PostToolUse / PermissionRequest / Stop / SessionEnd）+ `config.toml` 的 `[features] codex_hooks = true`。转发脚本 `codex-hook.ps1` 只依赖「事件名 → HTTP POST」这一个事实，因此事件名相同的工具都能复用。

- **Codex（ChatGPT 桌面应用 / CLI）**：`~/.codex/hooks.json`，6 事件，`type:"command"` 单命令字符串。插件自动写入。
- **ZCode（智谱 z.ai 的 GLM 桌面编码 Agent）**：**有自己的原生 hooks 机制**（官方文档 zcode.z.ai/docs/hooks），配置在 `~/.zcode/cli/config.json`，采用 **Claude Code hook schema**：`events` + `matcher` + `type:"process"`（command 与 args 分开、不经 shell 直接 argv 执行），事件名 SessionStart / UserPromptSubmit / PostToolUse / PermissionRequest / Stop 与 Codex 一致，但**没有 SessionEnd**。插件按该格式单独写入（合并保留用户已有 hooks，受 F8「启用 ZCode 联动」开关控制），转发脚本仍复用 `codex-hook.ps1`。
- **判断方法**：查目标工具的官方文档确认其 hooks 配置文件与 schema（Codex 式 `hooks.json` / Claude Code 式 `cli/config.json` / 其他），再按对应格式写入；不能假设"一套配置通吃"。
