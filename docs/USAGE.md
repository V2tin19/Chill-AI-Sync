# Chill with AI 使用文档

把 OpenAI Codex（ChatGPT 桌面应用 / CLI）的**真实工作状态**同步到《Chill with You: Lo-Fi Story》里——Codex 干活时女主角安静工作，Codex 休息时她喝茶看书，任务完成她会伸懒腰庆祝。

---

## 一、这个东西由什么组成

| 组件 | 作用 | 需要用户操作吗 |
|---|---|---|
| `ChillAI.Plugin.dll` | 游戏内插件：轮询状态、驱动女主角、设置窗口 | 放进游戏 BepInEx 插件目录即可 |
| `ChillAI.Bridge.exe` | 本地状态服务器（接收各工具钩子事件、维护状态机） | **不需要手动开**——插件会静默自动启动 |
| `codex-hook.ps1` | 转发脚本：把 Codex / ZCode / Antigravity 的生命周期事件 POST 给 Bridge | 不需要手动管——插件自动写配置 |
| `dsh-hook.mjs` | DeepSeek Harness（dsh）侧桥接插件：挂在 dsh 官方拦截 seam 上，把状态 POST 给 Bridge | 不需要手动管——插件自动挂载 |
| `~/.codex/hooks.json` | Codex 钩子配置（6 个事件） | **插件自动写入**，路径自动指向插件目录 |
| `~/.dsh/profiles/<profile>/cordis.patch.yml` | dsh profile 补丁（挂载 `./dsh-hook.mjs`） | **插件自动写入**（只增删自己那段带标记的区块，你的其他条目原样保留） |

> 插件启动时会自动：① 静默启动 Bridge（无窗口，若 17860 端口已有 Bridge 在跑则直接复用）② 按 F8 联动开关写入或移除各工具的钩子配置：`~/.codex/hooks.json`、`~/.zcode/cli/config.json`、`~/.gemini/config/hooks.json`，以及 dsh 的 `~/.dsh/profiles/<profile>/cordis.patch.yml`（路径都指向插件自身目录，跨机器通用）③ 确保 `~/.codex/config.toml` 含 `[features] codex_hooks = true`（CLI 通道需要，仅 Codex 联动开启时）。**你不需要开任何 PowerShell 窗口，也不需要手动改任何配置文件；关闭联动开关时插件会自动摘除自己的条目，你自己写的配置一律保留。**

---

## 二、安装（推荐：一键安装）

### 1. 解压后双击 `install.bat`

把发布包**完整解压**到一个普通文件夹（不要在压缩包预览里直接双击），**关闭游戏**，然后双击 `install.bat`。

它会自动：定位游戏目录 → 检查环境（含 .NET ASP.NET Core 8 运行时）→ 把 `ChillAI` 文件夹复制到
`<游戏安装目录>\BepInEx\plugins\ChillAI\` 并回读校验 → 列出你本机装了哪些 AI 工具、各自还要做什么。

> 不想用脚本也可以手动装：把发布包里的 `ChillAI` 文件夹完整复制到
> `<游戏安装目录>\BepInEx\plugins\ChillAI\` 即可（其余步骤完全一样）。
> 游戏安装目录每台电脑可能不同（Steam 库位置不同），没关系——插件运行时不依赖游戏路径。

### 2. 在你用的 AI 工具里做「最后一步」（一次性）

这一步无法自动化，各工具不同：

**Codex（ChatGPT 桌面应用，最常用）：**

1. 打开 Codex 桌面应用
2. 进入 **设置 → 外观（或功能开关）→ 宠物/Hooks**，打开 **Hooks 开关**
3. 重启 Codex，随便跑一个任务
4. 首次运行钩子时，Codex 会弹出"**是否信任此命令**"——**点允许**
   （这一步必须做，否则钩子被静默跳过，插件收不到任何事件）
5. 之后跑任务时，屏幕上应出现状态浮窗（说明通了）

**Codex CLI（命令行）：**

1. 打开终端，运行 `codex`
2. 首次启动会列出已配置的钩子并请求信任——**批准**
3. 如果 `~/.codex/config.toml` 里没有下面这行，加一下（或运行 `codex --enable codex_hooks`）：
   ```toml
   [features]
   codex_hooks = true
   ```

**其他工具：**

| 工具 | 你还要做什么 |
|---|---|
| ZCode | **新建一个会话**（它在会话启动时快照钩子配置） |
| Antigravity | **重启一次** |
| DeepSeek Harness (dsh) | **重启一次**（新插件要重新加载一代） |

### 3. 启动游戏

游戏会自动拉起 Bridge（无窗口）。左上角出现状态浮窗即成功。

> 出问题先双击插件目录里的 **`doctor.bat`**；完整排查手册见 [TROUBLESHOOTING.md](TROUBLESHOOTING.md)。

---

## 三、游戏内操作

- **F8**：打开/关闭设置窗口
- 设置窗口内：
  - **↑ / ↓**：选择行（选中行显示 ▶）
  - **Enter / 空格**：切换开关
  - 鼠标点击行也可以切换

| 开关 | 说明 |
|---|---|
| 启用 Codex 联动 | 接入 Codex（ChatGPT）钩子事件；关闭时会自动从 `~/.codex/hooks.json` 摘除本插件的钩子条目 |
| 启用 ZCode 联动 | 接入 ZCode（z.ai GLM）钩子事件（写 `~/.zcode/cli/config.json`，Claude Code hook schema）；关闭时自动摘除本插件的条目，用户其他配置保留 |
| 启用 Antigravity 联动 | 接入 Antigravity（Google / AGY）生命周期钩子（写 `~/.gemini/config/hooks.json`）；关闭时自动摘除本插件的条目 |
| 启用 DSH 联动 | 接入 DeepSeek Harness（`dsh`）：把 `dsh-hook.mjs` 挂载到当前 profile 的 `cordis.patch.yml`；关闭时自动摘除本插件的区块，你的其他 patch 条目保留。**新挂载后需要重启 DeepSeek Harness 才会加载该插件** |
| 工具优先（覆盖番茄钟） | 开：女主角状态完全由已启用工具（Codex/ZCode/Antigravity/DSH 任一）决定，游戏番茄钟的自动动作会被覆盖（**动作抽风属正常现象**）；仅在 Bridge 可达且有工具数据时生效，否则交还游戏原生行为。关：恢复游戏原生番茄钟驱动 |
| 显示状态浮窗 | 左上角状态浮窗显示/隐藏 |

设置自动保存在 `BepInEx\config\Chill.AI.cfg`，重启游戏后保留。

---

## 四、状态对照

| 浮窗显示 | 含义 | 女主角行为 |
|---|---|---|
| 正在工作 | 工具执行任务中 | 电脑前工作 |
| 刚完成任务 | 工具刚结束一轮 | 伸懒腰庆祝 |
| 休息 / 待命中 | 工具等你输入 | 喝茶休息 |
| 等待审批 | 工具请求权限 | 想找你说话 |
| 空闲 | 工具无会话 | 电脑前陪伴 |

---

## 五、故障排查

> **先跑体检**：双击插件目录里的 `doctor.bat`（安装时自动附带）。它会把
> 「游戏 → 插件 → 本地服务 → AI 工具」逐段查一遍，并直接列出该做什么。
> 本节只是常见问题的速查；完整手册见 [TROUBLESHOOTING.md](TROUBLESHOOTING.md)。

| 现象 | 原因与处理 |
|---|---|
| 双击 `install.bat` 一闪而过 | 用的是压缩包预览、没真正解压；先「全部解压」到普通文件夹再双击 |
| 浮窗显示"Bridge 未连接" | Bridge 没起来：确认插件目录里有 `ChillAI.Bridge.exe`，或手动运行它看报错；若日志出现"Bridge 进程已退出（退出码 …）"，最常见原因是缺 ASP.NET Core 8 运行时（见下方环境要求） |
| 状态一直是 unknown | 工具钩子没触发：检查工具 App/CLI 的**钩子信任**是否批准（最常见）；确认 Hooks 开关打开；Codex 侧 `~/.codex/hooks.json`、ZCode 侧 `~/.zcode/cli/config.json` 若被手动改坏，删掉后重启游戏让插件重写即可 |
| 浮窗一直不出现 | 游戏内按 F8 检查"显示状态浮窗"是否开启 |
| 女主角不动 | 检查"工具优先"和至少一个工具联动（Codex/ZCode/Antigravity/DSH）是否开启；看 `BepInEx\LogOutput.log` 里 `[ChillAI]` 日志。注意：Bridge 未连接或还没收到过任何工具事件时，女主行为交还游戏原生驱动，属预期 |
| 钩子被 Codex 清掉 | 桌面 App 会用内部状态重写 config.toml——用 App 设置界面的 Hooks 开关（它自己持久化），不要只改 config.toml。插件每次启动会重新确保 `codex_hooks = true`（CLI 通道） |
| dsh 侧没反应 | ① 挂载后**必须重启一次 DeepSeek Harness**（新插件的 JS 模块要重新加载一代）；② 确认 `~/.dsh/profiles/<profile>/cordis.patch.yml` 里有 `id: chillai-sync` 那段，且同目录存在 `dsh-hook.mjs`；③ 确认 `DSH_PROFILE` 指向的正是你在用的 profile（桌面版是 `desktop`，Web 版是 `web`）——插件按该环境变量优先定位，找不到才按 desktop → web 探测 |
| 覆盖安装时提示文件被占用 | 旧 Bridge 或游戏还在运行；安装程序会自动停掉旧 Bridge，游戏需你手动完全关闭 |
| `clamping ... timeout` 警告 | 无害，转发脚本足够快，可忽略 |

---

## 六、环境要求与泛用性说明

- **游戏**：《Chill with You: Lo-Fi Story》（Unity 2022.3，BepInEx 5）
- **.NET 8 的 ASP.NET Core Runtime**：Bridge 依赖（**注意不是基础 .NET Runtime**——只装基础运行时的话 Bridge 会启动即退）。检查：`dotnet --list-runtimes` 应包含 `Microsoft.AspNetCore.App 8.x`；没有的话到 https://dotnet.microsoft.com/download/dotnet/8.0 下载 **ASP.NET Core Runtime 8.0 → Windows → x64 Installer** 安装
- **游戏路径无关**：插件运行时不使用游戏绝对路径；Bridge/钩子脚本都从**插件自身目录**自动定位
- **钩子路径自适应**：`~/.codex/hooks.json` 由插件自动写入（指向插件目录内的 `codex-hook.ps1`，JSON 语法由插件校验保证），`config.toml` 的 `codex_hooks` 开关也自动确保——换机器、换目录都不用改
- **端口**：17860（本地回环）。若已有 Bridge 占用该端口，插件自动复用，不会重复启动
- **Codex**：ChatGPT 桌面应用或 Codex CLI，需开启 Hooks 功能并批准钩子信任
- **DeepSeek Harness（可选）**：装了才联动，没装则静默跳过。插件会把 `dsh-hook.mjs` 复制到 `~/.dsh/profiles/<profile>/` 并以相对路径 `./dsh-hook.mjs` 挂载（相对路径以补丁文件为锚点，因此跨机器、换目录都稳定），只依赖 dsh 自带的 Node 运行时，不需要 pnpm/npm 安装任何东西

---

## 七、开发者

```powershell
# 构建插件（GameDir 换成你的游戏目录）
dotnet build src\ChillAI.Plugin\ChillAI.Plugin.csproj -c Release -p:GameDir="<游戏目录>"
# 构建 Bridge
dotnet build src\ChillAI.Bridge\ChillAI.Bridge.csproj -c Release
# 一键编译并自动部署到游戏目录（不传 -GameDir 时自动探测默认 Steam 安装位置）
.\scripts\deploy.ps1 -GameDir "<游戏目录>"
# 一键本地构建与产物检查
.\scripts\check.ps1 -GameDir "<游戏目录>"
```

技术架构详见 [docs/CODEX_STATUS.md](CODEX_STATUS.md)。
