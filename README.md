# Chill AI: Heroine Sync

把 **AI 工具（Agent）的真实工作状态** 同步到《Chill with You: Lo-Fi Story》游戏里——Agent 干活时女主角安静工作，Agent 休息时她喝茶看书，任务完成她会伸懒腰庆祝。

通过 AI 工具的 **Hooks 机制** 事件驱动，零轮询、App/CLI 双通道，全程走游戏原生动画。

## 功能

- **Agent 状态 → 女主行为**：Working / Idle / Completed 状态机驱动女主角动作
- **事件驱动**：SessionStart / UserPromptSubmit / PostToolUse / PermissionRequest / Stop / SessionEnd 六类钩子事件
- **番茄钟联动**：以 Agent 状态为主时，覆盖游戏番茄钟对女主角的自动动作
- **F8 设置窗口**：总开关 / Codex 为主 / 状态浮窗，游戏内调整
- **自动配置钩子**：插件启动即自动写入 `~/.codex/hooks.json`（路径指向插件目录，跨机器通用），无需手动改配置

## 架构

```
AI 工具 (Codex 等)
  │  Hooks 事件（SessionStart/UserPromptSubmit/PostToolUse/PermissionRequest/Stop/SessionEnd）
  ▼
codex-hook.ps1（转发脚本，极轻量，几毫秒）
  │  HTTP POST http://127.0.0.1:17860/codex/events
  ▼
ChillAI.Bridge（本地状态服务，插件静默自启，无窗口）
  │  状态机 + 存储
  ▼
ChillAI.Plugin（BepInEx 5 游戏内插件）
  ▼
游戏女主角行为（工作/喝茶/伸懒腰…）
```

## 安装（3 步，约 3 分钟）

1. **下载 Release**：最新的 `ChillAI-vX.Y.Z.zip`，解压得到 `ChillAI` 文件夹
2. **复制**：整个 `ChillAI` 文件夹放进 `<游戏目录>\BepInEx\plugins\`
3. **启动一次游戏再关闭**（插件自动：静默启动 Bridge、写入 hooks.json）；然后打开 Codex（GPT）设置里开启 **Hooks** 开关，跑一个任务并**批准钩子信任**；再次启动游戏，左上角出现状态浮窗即成功

> 详细图文说明见 [docs/USAGE.md](docs/USAGE.md)。技术架构与 API 见 [docs/CODEX_STATUS.md](docs/CODEX_STATUS.md)。

## 游戏内使用

- 按 **F8** 打开设置窗口（↑↓ 选择、Enter/空格 切换）
  - 启用 Agent 联动（总开关）
  - 以 Agent 为主（覆盖番茄钟自动动作）
  - 显示状态浮窗

## 从源码构建

需要 .NET SDK；`GameDir` 指向游戏安装目录（引用游戏 Managed 程序集与 BepInEx）。

```powershell
# 插件
dotnet build src/ChillAI.Plugin/ChillAI.Plugin.csproj -c Release `
  -p:GameDir="D:\Steam\steamapps\common\Chill with You Lo-Fi Story"

# Bridge（发布包里的 ChillAI.Bridge.exe）
dotnet publish src/ChillAI.Bridge/ChillAI.Bridge.csproj -c Release -r win-x64 --self-contained false -o out
```

## 项目结构

- `src/ChillAI.Plugin/` — BepInEx 插件（钩子安装、状态轮询、女主行为驱动、番茄钟联动、F8 设置）
- `src/ChillAI.Bridge/` — 本地状态服务（接收钩子事件、维护状态机、健康检查）
- `scripts/` — 钩子转发脚本与调试工具（codex-hook.ps1 随发布包分发）
- `docs/` — 使用文档与架构说明

## 兼容性说明

当前实现基于 **Codex Hooks 规范**（`~/.codex/hooks.json`，6 类标准事件）。插件启动时会**自动探测并写入多个 AI 工具主目录**（`~/.codex`、`~/.zcode`，目录存在即写），并对已有 `config.toml` 的目录确保 `codex_hooks = true` 开关。

能否真正联动，取决于目标工具是否**实现 Codex Hooks 规范**：Codex（ChatGPT 桌面应用 / CLI）原生支持；独立配置体系的工具（如智谱 zai/glm 生态的 ZCode，配置在 `~/.zcode/v2/config.json`）目前不读取 Codex 格式的 `hooks.json`，需要工具官方支持该规范后才能联动。详见 [docs/CODEX_STATUS.md](docs/CODEX_STATUS.md) 的兼容性小节。

## 许可

MIT（见 [LICENSE](LICENSE)）。
