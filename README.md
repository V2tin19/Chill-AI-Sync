# Chill AI: Agent Sync

把 **AI 工具（Agent）的真实工作状态** 同步到《Chill with You: Lo-Fi Story》游戏里——Agent 干活时女主角安静工作，Agent 休息时她喝茶看书，任务完成她会伸懒腰庆祝。

通过 AI 工具的 **Hooks 机制** 事件驱动，零轮询、App/CLI 双通道，全程走游戏原生动画。

## 功能

- **Agent 状态 → 女主行为**：Working / Idle / Completed 状态机驱动女主角动作
- **事件驱动**：SessionStart / UserPromptSubmit / PostToolUse / PermissionRequest / Stop / SessionEnd 六类钩子事件
- **一键安装**：`install.bat` 自动找到游戏目录、复制插件、检查运行环境，并列出每个工具「还需要你手动做的一步」
- **一键体检**：`doctor.bat` 把「游戏 → 插件 → 本地服务 → AI 工具」整条链路逐段检查，直接告诉你哪一步断了
- **番茄钟联动**：以 Agent 状态为主时，覆盖游戏番茄钟对女主角的自动动作（Bridge 不可达或无工具数据时自动让位，不压制番茄钟）
- **F8 设置窗口**：四个工具的联动开关 / 工具优先 / 状态浮窗，游戏内调整
- **自动配置钩子**：插件启动即按 F8 开关自动写入 Codex / ZCode / Antigravity / dsh 四家的钩子配置（路径指向插件目录，跨机器通用）；关闭对应联动开关时会自动摘除自己的条目、保留用户其他配置，无需手动改配置
- **断连自愈**：Bridge 崩溃/退出后状态自动复位，女主角不会被锁死在工作动画；Bridge 启动即退时日志给出可行动的提示（最常见：缺 ASP.NET Core 8 运行时）

## 架构

```
AI 工具 (Codex / ZCode / Antigravity / dsh)
  │  生命周期事件（SessionStart/UserPromptSubmit/PostToolUse/PermissionRequest/Stop/SessionEnd）
  ▼
codex-hook.ps1（Codex/ZCode/Antigravity 转发脚本）· dsh-hook.mjs（dsh 桥接插件）
  │  HTTP POST http://127.0.0.1:17860/codex/events
  ▼
ChillAI.Bridge（本地状态服务，插件静默自启，无窗口）
  │  状态机 + 存储
  ▼
ChillAI.Plugin（BepInEx 5 游戏内插件）
  ▼
游戏女主角行为（工作/喝茶/伸懒腰…）
```

## 安装（零基础：解压后双击 install.bat）

1. **下载 Release**：最新的 `ChillAI-vX.Y.Z.zip`，**完整解压**到普通文件夹（不要在压缩包里直接双击）
2. **关闭游戏**，双击 **`install.bat`**，按提示走完（它会自动找游戏目录、复制插件、检查运行时）
3. 按屏幕提示，在你自己用的 AI 工具里做那「最后一步」：
   - **Codex**：设置里打开 Hooks 开关，首次运行钩子时点「允许/信任」
   - **ZCode**：新建一个会话
   - **Antigravity**：重启一次
   - **DeepSeek Harness**：重启一次

启动游戏，左上角出现状态浮窗即成功。

> 出问题先双击插件目录里的 **`doctor.bat`**；完整排查手册见 [docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md)。
> 手动安装（不想用脚本）：把 zip 里的 `ChillAI` 文件夹放进 `<游戏目录>\BepInEx\plugins\` 即可，其余步骤相同。
> 详细使用说明见 [docs/USAGE.md](docs/USAGE.md)，技术架构与 API 见 [docs/CODEX_STATUS.md](docs/CODEX_STATUS.md)。

## 游戏内使用

- 按 **F8** 打开设置窗口（↑↓ 选择、Enter/空格 切换）
  - 启用 Codex 联动
  - 启用 ZCode 联动
  - 启用 Antigravity 联动
  - 启用 DSH 联动
  - 工具优先（覆盖番茄钟）
  - 显示状态浮窗

## 从源码构建

需要 .NET SDK；`GameDir` 指向游戏安装目录（引用游戏 Managed 程序集与 BepInEx）。

```powershell
# 插件
dotnet build src/ChillAI.Plugin/ChillAI.Plugin.csproj -c Release `
  -p:GameDir="D:\Steam\steamapps\common\Chill with You Lo-Fi Story"

# Bridge（发布包里的 ChillAI.Bridge.exe）
dotnet publish src/ChillAI.Bridge/ChillAI.Bridge.csproj -c Release -r win-x64 --self-contained false -o out

# 一键构建并部署到游戏目录（不传 -GameDir 时自动探测 Steam 安装位置）
.\scripts\deploy.ps1

# 构建 + 部署 + 体检
.\scripts\check.ps1 -GameDir "<游戏目录>"

# 打出可以直接传 GitHub Releases 的 zip（含 install.bat / doctor.bat / README-FIRST.txt）
.\scripts\build-release.ps1
```

## 项目结构

- `src/ChillAI.Plugin/` — BepInEx 插件（钩子安装、状态轮询、女主行为驱动、番茄钟联动、F8 设置）
  - `DshPatchFile.cs` — dsh profile 补丁的纯文本合并/摘除逻辑（不依赖 Unity，可单独编译测试）
- `src/ChillAI.Bridge/` — 本地状态服务（接收钩子事件、维护状态机、健康检查）
- `scripts/` — 转发脚本、dsh 桥接插件、一键安装/体检、发布打包
  - `codex-hook.ps1` / `dsh-hook.mjs` — 随发布包分发的转发脚本与 dsh 桥接插件
  - `install.bat` / `install.ps1` — 一键安装（自动定位游戏、复制插件、检查环境）
  - `doctor.bat` / `doctor.ps1` — 一键体检（全链路诊断 + 给出修复动作）
  - `build-release.ps1` — 打包 GitHub Release 用的 zip
- `docs/` — 使用文档、架构说明与[故障排查手册](docs/TROUBLESHOOTING.md)

## 兼容性说明

当前实现基于事件驱动的生命周期钩子机制，插件与 Bridge 原生支持并已适配多种主流 AI Agent 工具：

- **Codex**（ChatGPT 桌面应用 / CLI）：自动写入 `~/.codex/hooks.json`（6 事件）+ 自动配置 `config.toml` 的 `codex_hooks = true`
- **ZCode**（智谱 z.ai GLM 桌面 Agent）：自动写入 `~/.zcode/cli/config.json`（Claude Code 规范，合并保留用户既有配置）
- **Antigravity**（Google / AGY 智能助手）：自动写入 `~/.gemini/config/hooks.json`（官方 Lifecycle Hooks 规范，受 F8 开关控制，合并保留用户其余配置）
- **DeepSeek Harness**（`dsh` Agent 运行时，开源 Cordis 插件框架）：随发布包分发一个 dsh 侧桥接插件 `dsh-hook.mjs`，插件启动时按 F8 开关把它复制并挂载到当前 profile（`~/.dsh/profiles/<profile>/cordis.patch.yml`），关闭开关时只摘除自己的区块、保留你其他的 patch 条目。它挂在 dsh 官方的拦截 seam 上（`agent/created`、`agent/pre-step`、`tools/post-execute`、`agent/turn-stopping`、`agent/disposed`），并用 `approval/asked` 会话事件补齐「等待审批」——**六个状态全覆盖**，进程内直发 HTTP，不起子进程、不需要安装任何 npm 包。手动挂载方式见 `scripts/dsh-hooks.cordis.patch.yml`。

各工具 hooks 的配置路径与 schema 各异，插件内部已做格式适配与增量合流，转发脚本输出标准 JSON 适配全平台。详见 [docs/CODEX_STATUS.md](docs/CODEX_STATUS.md)。

## 许可

MIT（见 [LICENSE](LICENSE)）。
