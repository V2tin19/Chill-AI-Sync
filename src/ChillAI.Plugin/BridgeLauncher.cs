using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using UnityEngine;

namespace ChillAI.Plugin
{
    /// <summary>
    /// 静默启动/复用本地 Bridge：
    /// 从插件所在目录定位 ChillAI.Bridge.exe（跨机器路径自适应），无窗口启动；
    /// 复用判定用 /health 探测（避免把恰好占用 17860 端口的无关程序当成 Bridge）；
    /// 进程启动即退时给出最可能的原因（缺 ASP.NET Core 8 运行时）。
    /// </summary>
    public static class BridgeLauncher
    {
        private const int BridgePort = 17860;

        private static readonly HttpClient HealthClient = new HttpClient { Timeout = TimeSpan.FromMilliseconds(800) };
        private static Process _process;
        private static bool _ensureAttempted;
        private static volatile bool _shuttingDown;
        private static bool _processExitHooked;

        /// <summary>
        /// 静默启动/复用 Bridge。整个游戏会话只尝试一次（Start 与每帧 Tick 都可能调用；
        /// 避免 Bridge 冷启动期间端口未开而被重复拉起）。
        /// </summary>
        public static void EnsureBridgeRunning()
        {
            if (_ensureAttempted)
            {
                return;
            }
            _ensureAttempted = true;

            if (IsBridgeAlive())
            {
                Log("检测到已有 Bridge 在运行（/health 正常），直接复用");
                return;
            }

            var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (string.IsNullOrEmpty(pluginDir))
            {
                return;
            }
            var exePath = Path.Combine(pluginDir, "ChillAI.Bridge.exe");
            if (!File.Exists(exePath))
            {
                Log($"未找到 Bridge 可执行文件（{exePath}），跳过自动启动。可手动运行 Bridge。");
                return;
            }

            try
            {
                var psi = new ProcessStartInfo(exePath)
                {
                    WorkingDirectory = pluginDir,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                _process = Process.Start(psi);
                _shuttingDown = false;
                WatchBridgeProcess(_process);
                HookProcessExit();
                Log("已静默启动本地 Bridge（无窗口）");
            }
            catch (Exception ex)
            {
                Log("启动 Bridge 失败: " + ex.Message);
            }
        }

        /// <summary>后台监视 Bridge 进程：启动即退（缺 ASP.NET Core 8 运行时等）时在日志给出可行动的提示。</summary>
        private static void WatchBridgeProcess(Process process)
        {
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    process.WaitForExit();
                    if (_shuttingDown)
                    {
                        return; // 正常关闭（游戏退出/卸载），无需告警
                    }
                    int code;
                    try { code = process.ExitCode; }
                    catch { code = -1; }
                    Log($"警告：Bridge 进程已退出（退出码 {code}）");
                    if (!IsBridgeAlive())
                    {
                        Log("Bridge 反复启动即退的最常见原因：缺少 ASP.NET Core 8 运行时" +
                            "（Bridge 需要 Microsoft.AspNetCore.App 8.x，仅装基础 .NET Runtime 不够）。" +
                            "请安装 ASP.NET Core Runtime 8.0 (Windows x64)：https://dotnet.microsoft.com/download/dotnet/8.0");
                    }
                }
                catch
                {
                    // 游戏进程退出等场景：忽略
                }
            });
        }

        /// <summary>游戏正常退出时兜底关闭我们拉起的 Bridge（OnDestroy 可能不被游戏驱动，避免遗留孤儿进程）。</summary>
        private static void HookProcessExit()
        {
            if (_processExitHooked)
            {
                return;
            }
            _processExitHooked = true;
            AppDomain.CurrentDomain.ProcessExit += (s, e) => Shutdown();
        }

        public static void Shutdown()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
                    _shuttingDown = true;
                    _process.Kill();
                    _process.Dispose();
                    _process = null;
                }
            }
            catch
            {
                // 忽略：进程可能已退出
            }
        }

        /// <summary>用 /health 探测 Bridge 是否存活（端口有人但不响应 /health 的程序不算）。</summary>
        private static bool IsBridgeAlive()
        {
            try
            {
                using var resp = HealthClient.GetAsync($"http://127.0.0.1:{BridgePort}/health").Result;
                if (!resp.IsSuccessStatusCode)
                {
                    return false;
                }
                var body = resp.Content.ReadAsStringAsync().Result;
                return body != null && body.Contains("ok");
            }
            catch
            {
                return false;
            }
        }

        private static void Log(string message)
        {
            Debug.Log("[ChillAI] " + message);
            Plugin.StaticLogger?.LogInfo("[ChillAI] " + message);
        }
    }
}
