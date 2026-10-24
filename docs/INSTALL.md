# 安装说明

## 普通用户安装（推荐：一键安装）

1. 从 Releases 页面下载最新的 `ChillAI-vX.Y.Z.zip`。
2. **完整解压**到一个普通文件夹（不要在压缩包预览里直接双击，否则脚本会找不到同级的 `ChillAI` 文件夹）。
3. **关闭游戏**，双击 **`install.bat`**。
   它会自动完成：
   - 定位游戏目录（读 Steam 注册表 + 解析 `libraryfolders.vdf`，覆盖所有 Steam 库；找不到时才让你手填）
   - 检查游戏是否在运行、BepInEx 是否就位、**是否装了 .NET ASP.NET Core 8 运行时**
   - 把 `ChillAI` 文件夹复制到 `<游戏目录>\BepInEx\plugins\ChillAI\` 并**回读校验**
   - 列出你本机装了哪些 AI 工具，以及各自「还需要你手动做的一步」
   - 可选：直接帮你启动一次游戏，让插件自动写好全部钩子配置
4. 按提示在你自己用的 AI 工具里做最后一步（这一步无法自动化）：

   | 工具 | 你还要做什么 |
   |---|---|
   | Codex（ChatGPT 桌面应用 / CLI） | 设置里打开 **Hooks 开关**，首次运行钩子时点「**允许/信任**」 |
   | ZCode | **新建一个会话**（它在会话启动时快照钩子配置） |
   | Antigravity | **重启一次** |
   | DeepSeek Harness (dsh) | **重启一次**（新插件要重新加载一代） |

5. 启动游戏。左上角出现状态浮窗即成功。

> **出问题先双击 `doctor.bat`**（安装后插件目录里也有一份）：它会把整条链路逐段查一遍并给出修复动作。
> 完整排查手册见 [TROUBLESHOOTING.md](TROUBLESHOOTING.md)。

### 手动安装（不想用脚本）

1. 关闭游戏。
2. 把发布包里的 `ChillAI` 文件夹复制到 `<游戏目录>\BepInEx\plugins\`。
3. 启动游戏即可——插件会自动静默启动 Bridge、按 F8 开关写入 Codex/ZCode/Antigravity 钩子配置与 dsh 挂载。
4. 在 `BepInEx\LogOutput.log` 中确认插件已加载（应包含 `Chill AI ... loaded` 和 `已静默启动本地 Bridge`）。

> **Bridge 依赖 .NET 8 的 ASP.NET Core Runtime**（不是基础 .NET Runtime）。
> 若日志出现"Bridge 进程已退出（退出码 …）"，请到 https://dotnet.microsoft.com/download/dotnet/8.0
> 安装 **ASP.NET Core Runtime 8.0 (Windows x64)**。

正确结构：

```text
游戏目录
└─ BepInEx
   └─ plugins
      └─ ChillAI
         ├─ ChillAI.Plugin.dll
         ├─ ChillAI.Bridge.exe
         ├─ ChillAI.Bridge.dll
         ├─ ChillAI.Bridge.runtimeconfig.json
         ├─ appsettings.json
         ├─ codex-hook.ps1
         ├─ dsh-hook.mjs
         ├─ doctor.bat        ← 一键安装时附带的体检脚本
         └─ doctor.ps1
```

> **DeepSeek Harness 用户注意**：插件会把 `dsh-hook.mjs` 复制到 `~/.dsh/profiles/<profile>/` 并挂载到该目录的 `cordis.patch.yml`，**新挂载后需要重启一次 DeepSeek Harness** 才会加载该插件。dsh 侧不需要安装任何 npm 包。

---

## 开发版本安装与调试

开发者需要安装 .NET 8 SDK，推荐使用一键部署脚本（不指定 `-GameDir` 时会自动探测常见 Steam 安装位置）：

```powershell
.\scripts\deploy.ps1 -GameDir "<游戏目录>"
```

该脚本会自动完成：
1. 编译 `ChillAI.Plugin`
2. 发布 `ChillAI.Bridge`
3. 将插件 DLL、Bridge 运行时文件、`codex-hook.ps1`、`dsh-hook.mjs` 一并部署至 `<游戏目录>\BepInEx\plugins\ChillAI\`

也可以仅运行本地构建与产物校验（不执行文件复制部署）：

```powershell
.\scripts\check.ps1 -GameDir "<游戏目录>"
```

Bridge 也可以单独从源码启动调试：

```powershell
dotnet run --project .\src\ChillAI.Bridge\ChillAI.Bridge.csproj
```

### 打包发布

```powershell
.\scripts\build-release.ps1                 # 自动探测游戏目录
.\scripts\build-release.ps1 -GameDir "<游戏目录>"
```

产物在 `.release\ChillAI-vX.Y.Z.zip`，结构如下（**全部 ASCII 文件名**，避免解压时中文名乱码）：

```text
ChillAI-vX.Y.Z\
  ├─ README-FIRST.txt   ← 中文说明（UTF-8 BOM）
  ├─ install.bat / install.ps1
  ├─ doctor.bat  / doctor.ps1
  └─ ChillAI\           ← 插件本体
```

脚本会自动校验：插件必需文件齐全、`install/doctor` 与说明文件齐全、含中文的 `.ps1`/`.txt` 都带 UTF-8 BOM。
版本号从 `src\ChillAI.Plugin\Plugin.cs` 读取，避免多处手写不一致。

---

## 验证

安装后启动游戏，检查日志：

```text
BepInEx\LogOutput.log
```

正常情况下应包含 `Chill AI ... loaded` 与 `已静默启动本地 Bridge`。

更省事的做法是直接跑体检：

```powershell
<游戏目录>\BepInEx\plugins\ChillAI\doctor.bat
```

## 卸载

1. 启动游戏，按 **F8**，把「启用 Codex 联动」「启用 ZCode 联动」「启用 Antigravity 联动」「启用 DSH 联动」都关掉——插件会自动从 `~/.codex/hooks.json`、`~/.zcode/cli/config.json`、`~/.gemini/config/hooks.json` 以及 dsh 的 `cordis.patch.yml` 摘除自己的条目（用户其他配置保留）。
2. 关闭游戏，然后删除：

```text
BepInEx\plugins\ChillAI
```

3. 若已经直接删除了插件文件夹（没来得及先关开关）：按 [TROUBLESHOOTING.md 第 6 节](TROUBLESHOOTING.md#6-安装--卸载)的手动清理表逐项删除。

插件不会覆盖游戏程序集，也不会修改存档。
