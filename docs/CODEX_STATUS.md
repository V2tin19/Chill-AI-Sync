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

2. **编辑 `~/.codex/hooks.json`，把所有 `command` 里的路径改成你 clone 本仓库的实际路径**（当前是 `D:\gitstuff\Chill-AI-Heroine-Sync`，换成你自己的）。

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
| （兜底）working 超 2 分钟（120 秒）无事件 | `waiting` | 防止钩子静默失效后卡死 |

### 多工具聚合（重要）

Bridge **按来源（工具）分别记账，再聚合**——不是"谁最后发事件听谁的"。

早期实现只有一个全局状态，同时开两个工具时会互相打架，例如：

- Codex 正在跑任务，dsh 那边一个 `Stop` 就把状态改成 `justdone` → 女主跑去庆祝，而 Codex 还在干活；
- dsh 会话结束（`sessionend` → `idle`）会把正在工作的 Codex 状态清掉 → 女主直接停手。

现在每个来源各留一份状态（各自独立衰减），聚合优先级为：

```text
working(6) > waitingreview(5) > justdone(4) > waiting(3) > idle(2) > unknown(1)
```

即：**只要有任何一个工具在工作，女主就工作**；都不工作时才依次考虑等审批、刚完成、待命、空闲。
同优先级取最近有事件的那个来源。这样多工具并用时状态稳定，不会来回闪烁。

> 取舍说明：`working` 优先级高于 `waitingreview`，意味着一个工具在干活时，另一个工具的
> 审批提醒会被"工作"盖住（不会闪烁）。如果你更希望审批提醒一定抢到显示，把
> `CodexStatusStore.Priority` 里这两档的数字对调即可。

来源由上报方声明：`codex-hook.ps1` 用 `-Source codex|zcode|antigravity`，
`dsh-hook.mjs` 固定发 `source: "dsh"`。**不传 `source` 的上报全部归入 `default` 这一个来源**，
此时聚合结果与旧版行为完全一致（向后兼容）。

状态变更同时记录最近 50 条事件历史（`GET /codex/events`），便于诊断和做更细的行为。

## HTTP API

| 端点 | 方法 | 说明 |
|---|---|---|
| `/health` | GET | 健康检查 `{"status":"ok","version":"1.0.8","features":["source-aggregation"]}`；`features` 供插件判断端口上跑的是不是同一代 Bridge |
| `/codex/status` | GET | 当前状态（state / detail / sinceUtc / lastEvent / secondsSinceLastEvent） |
| `/codex/events` | GET | 最近 50 条事件历史（event / detail / atUtc / stateAfter / source） |
| `/codex/events` | POST | 上报事件 `{"event":"Stop","detail":"可选","source":"codex"}`（event 大小写不敏感；source 可省略） |

### 旧 Bridge 残留（升级时必看）

游戏退出时不一定会回收 Bridge 进程，而插件发现端口上已有 Bridge 会**复用**它。
于是"装了新版插件、但旧 Bridge 还在跑"会让新逻辑（多来源聚合）**静默不生效**。

插件已能自愈：启动时读 `/health` 的 `features`，缺少 `source-aggregation` 就结束旧进程再拉起新版。
`doctor.bat` 也会检查这一项并清理。若你手动排查，记住这条：

```powershell
Invoke-RestMethod http://127.0.0.1:17860/health   # 看不到 features 就是旧版，需要重启游戏
```

## 故障排查

| 症状 | 原因与解法 |
|---|---|
| 状态一直是 `unknown` | Bridge 没启动；或钩子没触发 → 检查 App/CLI 的钩子**信任**是否已批准（最常见） |
| 状态停在 `waiting` 不变 | 钩子事件没进来 → 查 `GET /codex/events` 历史；App 里确认 Hooks 开关 + 信任都开了 |
| `clamping ... timeout to 3s` 警告 | hooks.json 里 SessionEnd 等事件的 `timeout` 超过 3s，改成 `3` 即可（官方上限） |
| 重启 App 后 `codex_hooks` 配置消失 | 桌面 App 会用内部状态重写 config.toml，清掉它不认识的键 → 在 **App 设置里**打开 Hooks 开关（App 自己持久化），不要只改 config.toml |
| 端口 17860 被占用 | 先杀旧进程再启动（`Stop-Process` / 任务管理器） |
| 同时开两个工具时状态乱跳 | 正在跑的 Bridge 是旧版本（`/health` 里没有 `features`）→ 结束 `ChillAI.Bridge` 进程后重启游戏 |
| 模型报 503 / No available channel | 与钩子无关，是模型代理（如 cc-switch）的通道问题 |
| 转发脚本静默失败 | 脚本设计为 Bridge 未启动时静默退出、不阻塞 Codex；确认 Bridge 已启动 |

## 已知限制

- 桌面 App 的"Hooks 开关"和"钩子信任"是**两个独立步骤**，漏了信任钩子会被静默跳过
- 手动写进 config.toml 的键可能被桌面 App 重写清除——App 侧请优先用设置界面开关
- Hooks 事件粒度是"回合 / 工具调用"级（毫秒到秒级延迟），足够驱动游戏角色，但不是逐 token 流
- 钩子 `timeout` 官方上限 3 秒（部分事件），转发脚本必须保持轻量

## 与其他 AI 工具的兼容性

本通道基于**生命周期钩子事件驱动**。转发脚本 `codex-hook.ps1` 负责接收事件名并 POST 给 Bridge，并向 stdout 吐出标准 JSON `{}`。因此，只要目标工具具备生命周期钩子扩展能力，均可复用：

- **Codex（ChatGPT 桌面应用 / CLI）**：`~/.codex/hooks.json`，6 事件，`type:"command"` 单命令字符串。插件启动时按 F8 联动开关自动写入/摘除。
- **ZCode（智谱 z.ai 的 GLM 桌面编码 Agent）**：`~/.zcode/cli/config.json`，采用 **Claude Code hook schema**：`events` + `matcher` + `type:"process"`（command 与 args 分开、不经 shell 直接 argv 执行），事件名与 Codex 一致（无 SessionEnd）。插件按该格式合并写入/摘除。
- **Antigravity（Google / AGY 智能助手）**：`~/.gemini/config/hooks.json`，采用 **Antigravity 官方 Lifecycle Hooks schema**：顶级命名为 `chill-ai-sync`，支持 `PreInvocation`（任务启动）、`PostToolUse`（工具调用）、`Stop`（任务完成）。插件受 F8「启用 Antigravity 联动」开关控制，自动安全合并写入/摘除（保留用户其他 named hooks）。
- **DeepSeek Harness（`dsh` Agent 运行时）**：DeepSeek 官方开源的 Agent 运行时（Cordis 插件架构，MIT）。本插件随发布包分发一个 dsh 侧桥接插件 `scripts/dsh-hook.mjs`，由游戏插件按 F8 开关自动复制并挂载到当前 profile 的 `cordis.patch.yml`（关闭时自动摘除本区块，保留用户其余 patch 条目）。

  > **勘误**：本仓库 v1.0.8 早期文档曾写「通过 `dsh-hooks` 扩展配置 `rules: - on: …`」——**这个说法是错的**。npm 上并不存在 `@deepseek-ai/dsh-hooks`（404），该 schema 也不是任何真实插件的格式；`agent/pre-step` / `tool/call` / `approval/asked` / `turn/end` 是 dsh 的 **session 事件类型**（见 `dsh-session` 的 `known-event-types`），不是可配置的钩子点。

  桥接插件挂在 dsh **官方文档化的拦截 seam** 上（与官方桥接包 `@deepseek-ai/dsh-hooks-codex` 用的是同一批扩展点）：

  | dsh seam | 上报给 Bridge 的事件 | 游戏状态 |
  |---|---|---|
  | `agent/created` | `SessionStart` | 正在工作 |
  | `agent/pre-step`（waterfall，纯观测、原样 `next()`） | `UserPromptSubmit` | 正在工作 |
  | `tools/post-execute`（waterfall，纯观测、原样 `next()`） | `PostToolUse` | 正在工作 |
  | `approval/asked`（durable session 事件） | `PermissionRequest` | 等待审批 |
  | `agent/turn-stopping` | `Stop` | 刚完成任务（15s 后衰减） |
  | `agent/disposed` | `SessionEnd` | 空闲 |

  为什么不直接用官方桥接包 `@deepseek-ai/dsh-hooks-codex`：

  1. 它只支持 5 个 Codex 事件点，**不支持 `PermissionRequest`**（配置会被静默丢弃），游戏里拿不到「等待审批」；
  2. 它是 profile 级的 npm 依赖，需要用户自己往 profile 里 `pnpm` 安装，且 peer 版本与 dsh 核心严格对齐（本机 dsh 核心为 `0.2.1-rc.2`，就得装 `0.2.1-rc.2`）——游戏插件无法替用户自动完成，破坏「装上就能用」；
  3. 每个事件都要起一个 `powershell.exe` 子进程执行 hooks.json 里的命令。

  自研的 `dsh-hook.mjs` 零依赖（只用 Node 内置 `fetch`）、进程内直发、六状态全覆盖。挂载细节与手动挂载步骤见 `scripts/dsh-hooks.cordis.patch.yml`。

  > 注意：`session/end-seed` 是 fork/resume 的**种子标记**，不是「会话结束」，因此不要把它映射成 `SessionEnd`（桥接插件里用的是 `agent/disposed`）。
