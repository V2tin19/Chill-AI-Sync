using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ChillAI.Plugin
{
    /// <summary>
    /// 轮询 Bridge 状态的工作组件 + 游戏内交互配置窗口。
    /// 由 Harmony 挂钩 RoomGameManager.Update 驱动 Tick；
    /// 鼠标/键盘全部用 Win32 原生检测（本游戏的 Unity 输入系统不驱动插件）。
    /// </summary>
    public sealed class StatusWorker : MonoBehaviour
    {
        public static StatusWorker Instance { get; private set; }

        /// <summary>最新已知的 Codex 状态（供番茄钟拦截补丁读取）。</summary>
        public static string CurrentCodexState = "unknown";

        private const string BridgeBaseUrl = "http://127.0.0.1:17860";
        private const float PollIntervalSeconds = 1f;
        private const float HeartbeatIntervalSeconds = 30f;

        // Win32 虚拟键码（F8 打开窗口，避免 F12 触发 Steam 截图）
        private const int VK_F8 = 0x77;
        private const int VK_UP = 0x26;
        private const int VK_DOWN = 0x28;
        private const int VK_RETURN = 0x0D;
        private const int VK_SPACE = 0x20;
        private const int VK_LBUTTON = 0x01;

        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        private static extern bool GetCursorPos(out POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern bool ScreenToClient(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private CodexStatusClient _client;
        private string _lastLoggedState = "";
        private bool _f8WasDown;
        private bool _upWasDown;
        private bool _downWasDown;
        private bool _confirmWasDown;
        private bool _lmbWasDown;
        private bool _configWindowOpen;
        private int _selectedRow;
        private float _lastHeartbeatTime;
        private float _lastReassertTime;
        private int _lastTickedFrame = -1;

        private static IntPtr _gameHwnd;
        private static int _hwndFindFrame = -1;

        // 配置窗口布局（IMGUI 屏幕坐标，左上原点）
        private static readonly Rect WindowRect = new Rect(20, 20, 360, 200);
        private static readonly Rect[] RowRects =
        {
            new Rect(28, 82, 344, 30),
            new Rect(28, 116, 344, 30),
            new Rect(28, 150, 344, 30),
        };

        private void Log(string message)
        {
            Debug.Log("[ChillAI] " + message);
            Plugin.StaticLogger?.LogInfo("[ChillAI] " + message);
        }

        /// <summary>由 Harmony 补丁调用：确保 Worker 存在。</summary>
        public static void EnsureInstance()
        {
            if (Instance != null)
            {
                return;
            }
            var go = new GameObject("ChillAI_Worker");
            DontDestroyOnLoad(go);
            Instance = go.AddComponent<StatusWorker>();
            Plugin.StaticLogger?.LogInfo("[ChillAI] Worker 已创建");
        }

        private void OnEnable()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            if (_client == null)
            {
                _client = new CodexStatusClient(BridgeBaseUrl, PollIntervalSeconds);
            }
            Log("StatusWorker OnEnable 触发");
        }

        private void Start()
        {
            Log($"StatusWorker Start，轮询 {BridgeBaseUrl}（F8 打开/关闭设置窗口）");
            BridgeLauncher.EnsureBridgeRunning();
        }

        private void OnDestroy()
        {
            BridgeLauncher.Shutdown();
        }

        public void Tick()
        {
            if (_client == null)
            {
                return;
            }
            if (Time.frameCount == _lastTickedFrame)
            {
                return;
            }
            _lastTickedFrame = Time.frameCount;

            // F8 开关设置窗口（始终可用）
            bool f8Down = (GetAsyncKeyState(VK_F8) & 0x8000) != 0;
            if (f8Down && !_f8WasDown)
            {
                _configWindowOpen = !_configWindowOpen;
                _selectedRow = 0;
                Log("设置窗口 " + (_configWindowOpen ? "打开" : "关闭"));
            }
            _f8WasDown = f8Down;

            // 设置窗口输入（始终可用，即使 Codex 联动被关闭）
            if (_configWindowOpen)
            {
                HandleWindowInput();
            }

            // 总开关：关闭时停止轮询与驱动
            if (Plugin.EnableCodex != null && !Plugin.EnableCodex.Value)
            {
                return;
            }

            _client.Update();

            // 心跳
            if (Time.unscaledTime - _lastHeartbeatTime >= HeartbeatIntervalSeconds)
            {
                _lastHeartbeatTime = Time.unscaledTime;
                var hb = _client.Current;
                Log($"心跳: connected={hb.Connected}, state={hb.State}, error={hb.Error}");
            }

            // 状态变化：日志 + 驱动女主角
            var snap = _client.Current;
            if (snap.Connected && snap.State != _lastLoggedState)
            {
                _lastLoggedState = snap.State;
                Log($"状态 -> {snap.State} (lastEvent={snap.LastEvent}, detail={snap.Detail})");
                HeroineDriver.ApplyCodexState(snap.State);
            }

            if (snap.Connected)
            {
                CurrentCodexState = snap.State;
            }

            // 强制校正兜底：每 2 秒把女主角拉回 Codex 期望状态，
            // 覆盖游戏番茄钟通过任何内部路径改掉的状态
            if (Plugin.CodexPrimary != null && Plugin.CodexPrimary.Value
                && Time.unscaledTime - _lastReassertTime >= 2f)
            {
                _lastReassertTime = Time.unscaledTime;
                HeroineDriver.Reassert();
            }
        }

        private void HandleWindowInput()
        {
            // 键盘：↑↓ 选择，Enter/空格 切换
            bool up = (GetAsyncKeyState(VK_UP) & 0x8000) != 0;
            bool down = (GetAsyncKeyState(VK_DOWN) & 0x8000) != 0;
            if (up && !_upWasDown)
            {
                _selectedRow = (_selectedRow + 2) % 3;
                Log("选择行 " + (_selectedRow + 1));
            }
            if (down && !_downWasDown)
            {
                _selectedRow = (_selectedRow + 1) % 3;
                Log("选择行 " + (_selectedRow + 1));
            }
            _upWasDown = up;
            _downWasDown = down;

            bool confirm = ((GetAsyncKeyState(VK_RETURN) & 0x8000) != 0) || ((GetAsyncKeyState(VK_SPACE) & 0x8000) != 0);
            if (confirm && !_confirmWasDown)
            {
                ToggleSelectedRow();
            }
            _confirmWasDown = confirm;

            // 鼠标点击（按窗口精确换算）
            bool lmb = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
            if (lmb && !_lmbWasDown)
            {
                if (TryGetGameClientPoint(out var p))
                {
                    for (int i = 0; i < RowRects.Length; i++)
                    {
                        if (RowRects[i].Contains(p))
                        {
                            _selectedRow = i;
                            ToggleSelectedRow();
                            break;
                        }
                    }
                }
            }
            _lmbWasDown = lmb;
        }

        private void ToggleSelectedRow()
        {
            switch (_selectedRow)
            {
                case 0: Toggle(ref Plugin.EnableCodex, "Codex 联动"); break;
                case 1: Toggle(ref Plugin.CodexPrimary, "以 Codex 为主"); break;
                case 2: Toggle(ref Plugin.ShowOverlay, "状态浮窗"); break;
            }
        }

        private static void Toggle(ref BepInEx.Configuration.ConfigEntry<bool> entry, string name)
        {
            if (entry == null)
            {
                return;
            }
            entry.Value = !entry.Value;
            Instance?.Log($"设置: {name} -> {(entry.Value ? "开" : "关")}");
        }

        /// <summary>把 Win32 屏幕坐标换算成游戏内 IMGUI 坐标（按窗口客户区精确换算）。</summary>
        private static bool TryGetGameClientPoint(out Vector2 point)
        {
            point = default;
            var hwnd = FindGameWindow();
            if (hwnd == IntPtr.Zero)
            {
                return false;
            }
            GetCursorPos(out var pt);
            if (!ScreenToClient(hwnd, ref pt))
            {
                return false;
            }
            GetClientRect(hwnd, out var rc);
            if (rc.Right <= 0 || rc.Bottom <= 0)
            {
                return false;
            }
            point = new Vector2(
                pt.X * (Screen.width / (float)rc.Right),
                pt.Y * (Screen.height / (float)rc.Bottom));
            return true;
        }

        /// <summary>按进程 ID 找到游戏窗口（每 10 秒刷新一次句柄）。</summary>
        private static IntPtr FindGameWindow()
        {
            if (_gameHwnd != IntPtr.Zero && Time.frameCount - _hwndFindFrame < 600)
            {
                return _gameHwnd;
            }
            _gameHwnd = IntPtr.Zero;
            _hwndFindFrame = Time.frameCount;
            var pid = (uint)Process.GetCurrentProcess().Id;
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out var wpid);
                if (wpid == pid)
                {
                    _gameHwnd = h;
                    return false;
                }
                return true;
            }, IntPtr.Zero);
            return _gameHwnd;
        }

        private void Update()
        {
            Tick();
        }

        private void OnGUI()
        {
            if (_configWindowOpen)
            {
                DrawConfigWindow();
            }
            else if (Plugin.ShowOverlay != null && Plugin.ShowOverlay.Value)
            {
                DrawStatusOverlay();
            }
        }

        // ---------------- 状态浮窗 ----------------

        private void DrawStatusOverlay()
        {
            var snap = _client?.Current;
            var style = new GUIStyle(GUI.skin.box)
            {
                fontSize = 15,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(12, 12, 8, 8)
            };

            string display;
            if (Plugin.EnableCodex != null && !Plugin.EnableCodex.Value)
            {
                display = "Codex 联动已关闭（F8 打开设置）";
            }
            else if (snap != null && snap.Connected)
            {
                display = "Codex: " + StateToChinese(snap.State)
                          + "\n事件: " + (string.IsNullOrEmpty(snap.LastEvent) ? "-" : snap.LastEvent)
                          + "  | 距今 " + snap.SecondsSinceLastEvent + "s"
                          + (string.IsNullOrEmpty(snap.Detail) ? "" : "\n" + Truncate(snap.Detail, 44));
            }
            else
            {
                display = "Codex: Bridge 未连接\n" + (snap == null ? "" : Truncate(snap.Error, 44));
            }

            style.normal.textColor = StateColor(snap);
            GUI.Box(new Rect(10, 10, 360, 116), display, style);

            // 底部提示（不显眼的灰色小字）
            var hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            hintStyle.normal.textColor = new Color(0.55f, 0.55f, 0.55f);
            GUI.Label(new Rect(22, 104, 340, 16), "覆盖番茄钟时动作抽风属正常现象", hintStyle);
        }

        // ---------------- 设置窗口 ----------------

        private void DrawConfigWindow()
        {
            var snap = _client?.Current;
            var bg = new GUIStyle(GUI.skin.box) { fontSize = 13, padding = new RectOffset(10, 10, 8, 8) };
            GUI.Box(WindowRect, "", bg);

            var titleStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            titleStyle.normal.textColor = new Color(0.15f, 0.5f, 0.9f);
            GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + 10, 300, 24), "Chill AI 设置", titleStyle);

            var statusStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            string statusText = "状态: 未连接";
            if (snap != null && snap.Connected)
            {
                statusText = "Codex: " + StateToChinese(snap.State) + "  | 距今 " + snap.SecondsSinceLastEvent + "s";
            }
            statusStyle.normal.textColor = StateColor(snap);
            GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + 42, 320, 24), statusText, statusStyle);

            DrawToggleRow(0, "启用 Codex 联动", Plugin.EnableCodex?.Value ?? true);
            DrawToggleRow(1, "以 Codex 为主（覆盖番茄钟）", Plugin.CodexPrimary?.Value ?? true);
            DrawToggleRow(2, "显示状态浮窗", Plugin.ShowOverlay?.Value ?? true);

            var hintStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            hintStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
            GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + 182, 340, 20), "↑↓ 选择 · Enter/空格 切换 · F8 关闭", hintStyle);
        }

        private void DrawToggleRow(int index, string label, bool value)
        {
            var rowStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 0, 0, 0)
            };
            bool selected = index == _selectedRow;
            rowStyle.normal.textColor = selected
                ? new Color(1f, 0.85f, 0.2f)                          // 选中：亮黄
                : value ? new Color(0.1f, 0.6f, 0.3f) : new Color(0.5f, 0.5f, 0.5f);
            // 选中三角和勾选框分开显示，互不遮挡
            var prefix = (selected ? "▶ " : "  ") + (value ? "[✓] " : "[  ] ");
            GUI.Box(RowRects[index], prefix + label, rowStyle);
        }

        // ---------------- 工具 ----------------

        private static string StateToChinese(string state)
        {
            switch (state)
            {
                case "working": return "正在工作";
                case "justdone": return "刚完成任务";
                case "waiting": return "休息 / 待命中";
                case "waitingreview": return "等待审批";
                case "idle": return "空闲";
                default: return state;
            }
        }

        private static Color StateColor(CodexStatusClient.Snapshot snap)
        {
            if (snap == null || !snap.Connected)
            {
                return new Color(0.9f, 0.3f, 0.3f);
            }
            switch (snap.State)
            {
                case "working": return new Color(1f, 0.55f, 0.1f);
                case "justdone": return new Color(1f, 0.84f, 0f);
                case "waiting": return new Color(0.2f, 0.75f, 0.35f);
                case "waitingreview": return new Color(0.1f, 0.75f, 0.8f);
                case "idle": return new Color(0.6f, 0.6f, 0.6f);
                default: return Color.white;
            }
        }

        private static string Truncate(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max)
            {
                return text;
            }
            return text.Substring(0, max) + "...";
        }
    }
}
