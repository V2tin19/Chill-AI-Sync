# 安装说明

## 普通用户安装

1. 从 Releases 页面下载最新发布包。
2. 关闭游戏。
3. 将发布包中的 `ChillAI` 文件夹复制到 `BepInEx\plugins`（发布包为平铺结构，`ChillAI.Plugin.dll` 与 `ChillAI.Bridge.exe` 同级）。
4. 启动游戏即可——插件会自动静默启动 Bridge、按 F8 开关写入 Codex/ZCode 钩子配置，无需手动开任何程序或窗口。
5. 在 `BepInEx\LogOutput.log` 中确认插件已加载（应包含 `Chill AI ... loaded` 和 `已静默启动本地 Bridge`）。

> **Bridge 依赖 .NET 8 的 ASP.NET Core Runtime**（不是基础 .NET Runtime）。若日志出现"Bridge 进程已退出（退出码 …）"，请到 https://dotnet.microsoft.com/download/dotnet/8.0 安装 ASP.NET Core Runtime 8.0 (Windows x64)。

正确结构：

```text
游戏目录
└─ BepInEx
   └─ plugins
      └─ ChillAI
         ├─ ChillAI.Plugin.dll
         ├─ ChillAI.Bridge.exe
         ├─ appsettings.json
         └─ codex-hook.ps1
```

## 开发版本安装与调试

开发者需要安装 .NET 8 SDK，推荐使用一键部署脚本（不指定 `-GameDir` 时会自动探测常见 Steam 安装位置）：

```powershell
.\scripts\deploy.ps1 -GameDir "<游戏目录>"
```

该脚本会自动完成：
1. 编译 `ChillAI.Plugin`
2. 发布 `ChillAI.Bridge`
3. 将插件 DLL、Bridge 运行时文件、`codex-hook.ps1` 一并部署至 `<游戏目录>\BepInEx\plugins\ChillAI\`

也可以仅运行本地构建与产物校验（不执行文件复制部署）：

```powershell
.\scripts\check.ps1 -GameDir "<游戏目录>"
```

Bridge 也可以单独从源码启动调试：

```powershell
dotnet run --project .\src\ChillAI.Bridge\ChillAI.Bridge.csproj
```

## 验证

启动游戏后，检查：

```text
BepInEx\LogOutput.log
```

正常情况下应包含：

```text
Chill AI loaded
```

## 卸载

1. 启动游戏，按 **F8**，把「启用 Codex 联动」「启用 ZCode 联动」都关掉——插件会自动从 `~/.codex/hooks.json` 和 `~/.zcode/cli/config.json` 摘除自己的钩子条目（用户其他配置保留）。
2. 关闭游戏，然后删除：

```text
BepInEx\plugins\ChillAI
```

3. 若已经直接删除了插件文件夹（没来得及先关开关）：手动删除 `~/.codex/hooks.json`（仅当其中只含 ChillAI 钩子），并从 `~/.zcode/cli/config.json` 的 `hooks.events` 里删除引用 `codex-hook.ps1` 的条目。

插件不会覆盖游戏程序集，也不会修改存档。
