# 安装说明

## 普通用户安装

1. 从 Releases 页面下载最新发布包。
2. 关闭游戏。
3. 将发布包中的 `ChillAI` 文件夹复制到 `BepInEx\plugins`。
4. 启动 `ChillAI\Bridge\ChillAI.Bridge.exe`。
5. 启动游戏。
6. 在 `BepInEx\LogOutput.log` 中确认插件已经加载。

正确结构：

```text
游戏目录
└─ BepInEx
   └─ plugins
      └─ ChillAI
         ├─ ChillAI.Plugin.dll
         └─ Bridge
            └─ ChillAI.Bridge.exe
```

## 开发版本安装

开发者需要安装 .NET 8 SDK，并在仓库根目录运行：

```powershell
.\scripts\deploy.ps1 -GameDir "<游戏目录>"
```

脚本会构建插件，并将生成的 DLL 复制到：

```text
BepInEx\plugins\ChillAI\ChillAI.Plugin.dll
```

Bridge 可以从源码启动：

```powershell
dotnet run --project .\src\ChillAI.Bridge\ChillAI.Bridge.csproj
```

也可以运行本地构建与产物检查：

```powershell
.\scripts\check.ps1 -GameDir "<游戏目录>"
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

关闭游戏和 Bridge，然后删除：

```text
BepInEx\plugins\ChillAI
```

插件不会覆盖游戏程序集，也不会修改存档。
