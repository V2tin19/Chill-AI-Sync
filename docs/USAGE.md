# Chill with AI 使用文档

把 OpenAI Codex（ChatGPT 桌面应用 / CLI）的**真实工作状态**同步到《Chill with You: Lo-Fi Story》里——Codex 干活时女主角安静工作，Codex 休息时她喝茶看书，任务完成她会伸懒腰庆祝。

---

## 一、这个东西由什么组成

| 组件 | 作用 | 需要用户操作吗 |
|---|---|---|
| `ChillAI.Plugin.dll` | 游戏内插件：轮询状态、驱动女主角、设置窗口 | 放进游戏 BepInEx 插件目录即可 |
| `ChillAI.Bridge.exe` | 本地状态服务器（接收 Codex 钩子事件、维护状态机） | **不需要手动开**——插件会静默自动启动 |
| `codex-hook.ps1` | 转发脚本：把 Codex 生命周期事件 POST 给 Bridge | 不需要手动管——插件自动写配置 |
| `~/.codex/hooks.json` | Codex 钩子配置（6 个事件） | **插件自动写入**，路径自动指向插件目录 |

> 插件启动时会自动：① 静默启动 Bridge（无窗口，若 17860 端口已有 Bridge 在跑则直接复用）② 写入 `~/.codex/hooks.json`（指向插件目录里的转发脚本，跨机器通用）③ 确保 `~/.codex/config.toml` 含 `[features] codex_hooks = true`（CLI 通道需要）。**你不需要开任何 PowerShell 窗口，也不需要手动改任何配置文件。**

---

## 二、安装（3 步）

### 1. 把插件文件夹放进游戏

把发布包里的 `ChillAI` 文件夹完整复制到：

```
<游戏安装目录>\BepInEx\plugins\ChillAI\
```

> 游戏安装目录每台电脑可能不同（Steam 库位置不同），没关系——插件运行时完全不依赖游戏路径，放在哪台机器都行。

### 2. 让 Codex 认识钩子（一次性，约 1 分钟）

**桌面 App（推荐，最简单）：**

1. 打开 Codex（ChatGPT）桌面应用
2. 进入 **设置 → 外观（或功能开关）→ 宠物/Hooks**，打开 **Hooks 开关**
3. 重启 Codex，随便跑一个任务
4. 首次运行钩子时，Codex 会弹出"**是否信任此命令**"——**点允许**（这一步必须做，否则钩子被静默跳过，插件收不到任何事件）
5. 之后跑任务时，屏幕上应出现状态浮窗（说明通了）

**CLI（命令行）：**

1. 打开终端，运行 `codex`
2. 首次启动会列出已配置的钩子并请求信任——**批准**
3. 如果 `~/.codex/config.toml` 里没有下面这行，加一下（或运行 `codex --enable codex_hooks`）：
   ```toml
   [features]
   codex_hooks = true
   ```

### 3. 启动游戏

游戏会自动拉起 Bridge（无窗口）。左上角出现状态浮窗即成功。

---

## 三、游戏内操作

- **F8**：打开/关闭设置窗口
- 设置窗口内：
  - **↑ / ↓**：选择行（选中行显示 ▶）
  - **Enter / 空格**：切换开关
  - 鼠标点击行也可以切换

| 开关 | 说明 |
|---|---|
| 启用 Codex 联动 | 总开关。关闭后插件完全不干预游戏 |
| 以 Codex 为主（覆盖番茄钟） | 开：女主角状态完全由 Codex 决定，游戏番茄钟的自动动作会被覆盖（**动作抽风属正常现象**）；关：恢复游戏原生番茄钟驱动 |
| 显示状态浮窗 | 左上角状态浮窗显示/隐藏 |

设置自动保存在 `BepInEx\config\Chill.AI.cfg`，重启游戏后保留。

---

## 四、状态对照

| 浮窗显示 | 含义 | 女主角行为 |
|---|---|---|
| 正在工作 | Codex 执行任务中 | 电脑前工作 |
| 刚完成任务 | Codex 刚结束一轮 | 伸懒腰庆祝 |
| 休息 / 待命中 | Codex 等你输入 | 喝茶休息 |
| 等待审批 | Codex 请求权限 | 想找你说话 |
| 空闲 | Codex 无会话 | 电脑前陪伴 |

---

## 五、故障排查

| 现象 | 原因与处理 |
|---|---|
| 浮窗显示"Bridge 未连接" | Bridge 没起来：确认插件目录里有 `ChillAI.Bridge.exe`；或手动运行它看报错 |
| 状态一直是 unknown | Codex 钩子没触发：检查 App/CLI 的**钩子信任**是否批准（最常见）；确认 Codex 设置里 Hooks 开关打开；`~/.codex/hooks.json` 若被手动改坏，删掉后重启游戏让插件重写即可 |
| 浮窗一直不出现 | 游戏内按 F8 检查"显示状态浮窗"是否开启 |
| 女主角不动 | 检查"启用 Codex 联动"和"以 Codex 为主"是否开启；看 `BepInEx\LogOutput.log` 里 `[ChillAI]` 日志 |
| 钩子被 Codex 清掉 | 桌面 App 会用内部状态重写 config.toml——用 App 设置界面的 Hooks 开关（它自己持久化），不要只改 config.toml。插件每次启动会重新确保 `codex_hooks = true`（CLI 通道） |
| `clamping ... timeout` 警告 | 无害，转发脚本足够快，可忽略 |

---

## 六、环境要求与泛用性说明

- **游戏**：《Chill with You: Lo-Fi Story》（Unity 2022.3，BepInEx 5）
- **.NET 8 运行时**：Bridge 依赖（Windows 上一般已装；`dotnet --version` 可查）
- **游戏路径无关**：插件运行时不使用游戏绝对路径；Bridge/钩子脚本都从**插件自身目录**自动定位
- **钩子路径自适应**：`~/.codex/hooks.json` 由插件自动写入（指向插件目录内的 `codex-hook.ps1`，JSON 语法由插件校验保证），`config.toml` 的 `codex_hooks` 开关也自动确保——换机器、换目录都不用改
- **端口**：17860（本地回环）。若已有 Bridge 占用该端口，插件自动复用，不会重复启动
- **Codex**：ChatGPT 桌面应用或 Codex CLI，需开启 Hooks 功能并批准钩子信任

---

## 七、开发者

```powershell
# 构建插件（GameDir 换成你的游戏目录）
dotnet build src\ChillAI.Plugin\ChillAI.Plugin.csproj -c Release -p:GameDir="<游戏目录>"
# 构建 Bridge
dotnet build src\ChillAI.Bridge\ChillAI.Bridge.csproj -c Release
# 一键编译+部署插件
.\scripts\deploy.ps1 -GameDir "<游戏目录>"
# 反射查看游戏内部 API（HeroineAI 等）
dotnet tools\GameApiDump\bin\Release\net8.0\GameApiDump.dll "<游戏目录>\Chill With You_Data\Managed\Assembly-CSharp.dll" "<游戏目录>\Chill With You_Data\Managed" HeroineAI
```

技术架构详见 [docs/CODEX_STATUS.md](CODEX_STATUS.md)。
