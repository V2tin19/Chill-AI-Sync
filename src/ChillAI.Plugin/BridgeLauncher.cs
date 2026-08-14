using System;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Reflection;
using UnityEngine;

namespace ChillAI.Plugin
{
    /// <summary>
    /// 静默启动/复用本地 Bridge：
    /// 从插件所在目录定位 ChillAI.Bridge.exe（跨机器路径自适应），无窗口启动；
    /// 若 17860 端口已有 Bridge 在跑（如用户手动开的），则不重复启动。
    /// </summary>
    public static class BridgeLauncher
    {
        private const int BridgePort = 17860;
        private static Process _process;

        public static void EnsureBridgeRunning()
        {
            if (IsPortOpen(BridgePort))
            {
                Log("检测到已有 Bridge 在运行（端口 17860），直接复用");
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
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                _process = Process.Start(psi);
                Log("已静默启动本地 Bridge（无窗口）");
            }
            catch (Exception ex)
            {
                Log("启动 Bridge 失败: " + ex.Message);
            }
        }

        public static void Shutdown()
        {
            try
            {
                if (_process != null && !_process.HasExited)
                {
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

        private static bool IsPortOpen(int port)
        {
            try
            {
                using var client = new TcpClient();
                client.Connect("127.0.0.1", port);
                return true;
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
