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

        /// <summary>最新已知的工具状态（供番茄钟拦截补丁读取）。仅在 Bridge 可达时更新；断连即复位为 unknown。</summary>
        public static string CurrentCodexState = "unknown";

        /// <summary>Bridge 当前是否可达。番茄钟拦截与温和校正只在 true 且状态非 unknown（有真实工具数据）时生效。</summary>
        public static bool BridgeConnected;

        private const string BridgeBaseUrl = "http://127.0.0.1:17860";
        private const float PollIntervalSeconds = 1f;
        private const float HeartbeatIntervalSeconds = 30f;

        // Win32 虚拟键码（F8 打开窗口，避免 F12 触发 Steam 截图）
        private const int VK_F8 = 0x77;
        private const int VK_ESCAPE = 0x1B;
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

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

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
        private string _lastAppliedState = "";
        private bool _wasConnected;
        private bool _wasAnyToolOn;
        private bool _f8WasDown;
        private bool _escWasDown;
        private bool _upWasDown;
        private bool _downWasDown;
        private bool _confirmWasDown;
        private bool _lmbWasDown;
        private bool _closeHover;
        private bool _configWindowOpen;
        private int _selectedRow;
        private float _lastHeartbeatTime;
        private float _lastReassertTime;
        private int _lastTickedFrame = -1;

        private static IntPtr _gameHwnd;
        private static int _hwndFindFrame = -1;

        // 配置窗口布局（IMGUI 屏幕坐标，左上原点）
        private static readonly Rect WindowRect = new Rect(20, 20, 360, 312);

        /// <summary>右上角「隐藏窗口」按钮（可点击，等价于按 F8 / Esc）。</summary>
        private static readonly Rect CloseButtonRect = new Rect(WindowRect.xMax - 34, WindowRect.y + 8, 26, 24);
        private static readonly Rect[] RowRects =
        {
            new Rect(28, 78, 344, 30),
            new Rect(28, 112, 344, 30),
            new Rect(28, 146, 344, 30),
            new Rect(28, 180, 344, 30),
            new Rect(28, 214, 344, 30),
            new Rect(28, 248, 344, 30),
        };

        // IMGUI 样式缓存：GUIStyle 只能在 OnGUI 内创建，缓存后避免每帧分配（GC 压力）
        private static bool _stylesBuilt;
        private static GUIStyle _overlayBoxStyle;
        private static GUIStyle _overlayTitleStyle;
        private static GUIStyle _overlayInfoStyle;
        private static GUIStyle _overlayHintStyle;
        private static GUIStyle _configBgStyle;
        private static GUIStyle _configTitleStyle;
        private static GUIStyle _configStatusStyle;
        private static GUIStyle _configHintStyle;
        private static GUIStyle _rowStyle;
        private static GUIStyle _closeButtonStyle;

        private static void EnsureStyles()
        {
            if (_stylesBuilt)
            {
                return;
            }
            _stylesBuilt = true;

            _overlayBoxStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 15,
                alignment = TextAnchor.UpperLeft,
                padding = new RectOffset(12, 12, 8, 8)
            };
            // 浮窗分三层：状态标题（大、按状态着色）/ 说明信息（小、浅灰）/ 操作提示（更小、深灰）
            _overlayTitleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.UpperLeft
            };
            _overlayInfoStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _overlayInfoStyle.normal.textColor = new Color(0.82f, 0.82f, 0.82f);
            _overlayHintStyle = new GUIStyle(GUI.skin.label) { fontSize = 11 };
            _overlayHintStyle.normal.textColor = new Color(0.55f, 0.55f, 0.55f);

            _configBgStyle = new GUIStyle(GUI.skin.box) { fontSize = 13, padding = new RectOffset(10, 10, 8, 8) };
            _configTitleStyle = new GUIStyle(GUI.skin.label) { fontSize = 15, fontStyle = FontStyle.Bold };
            _configTitleStyle.normal.textColor = new Color(0.15f, 0.5f, 0.9f);
            _configStatusStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            _configHintStyle = new GUIStyle(GUI.skin.label) { fontSize = 12 };
            _configHintStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
            _rowStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 14,
                alignment = TextAnchor.MiddleLeft,
                padding = new RectOffset(10, 0, 0, 0)
            };
            _closeButtonStyle = new GUIStyle(GUI.skin.box)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                padding = new RectOffset(0, 0, 0, 0)
            };
        }

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
            if (Time.frameCount == _lastTickedFrame)
            {
                return;
            }
            _lastTickedFrame = Time.frameCount;

            // 仅在游戏窗口处于前台激活状态时检测全局按键/鼠标，
            // 避免用户在其他窗口（IDE、编辑器、浏览器）工作时误触 F8 或热键干扰设置。
            var gameHwnd = FindGameWindow();
            bool isForeground = gameHwnd != IntPtr.Zero && GetForegroundWindow() == gameHwnd;

            if (isForeground)
            {
                // F8 开关设置窗口（最先处理：不依赖客户端/Bridge 初始化是否成功）
                bool f8Down = (GetAsyncKeyState(VK_F8) & 0x8000) != 0;
                if (f8Down && !_f8WasDown)
                {
                    _configWindowOpen = !_configWindowOpen;
                    _selectedRow = 0;
                    Log("设置窗口 " + (_configWindowOpen ? "打开" : "关闭"));
                }
                _f8WasDown = f8Down;

                // Esc 的按下沿无条件更新（否则"窗口关闭期间按住 Esc"会留下过期状态，
                // 导致下次一开窗就被立刻关掉）
                bool escDown = (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0;
                if (_configWindowOpen && escDown && !_escWasDown)
                {
                    CloseConfigWindow("Esc");
                }
                _escWasDown = escDown;

                // 设置窗口输入（始终可用，即使 Codex 联动被关闭）
                if (_configWindowOpen)
                {
                    UpdateCloseButtonHover();
                    HandleWindowInput();
                }
            }
            else
            {
                // 窗口不在前台：重置按键沿检测状态
                _f8WasDown = false;
                _escWasDown = false;
                _upWasDown = false;
                _downWasDown = false;
                _confirmWasDown = false;
                _lmbWasDown = false;
                _closeHover = false;
            }

            // 惰性初始化：不依赖 OnEnable/Start 被游戏驱动（该游戏对插件组件的生命周期消息驱动不可靠）
            if (_client == null)
            {
                _client = new CodexStatusClient(BridgeBaseUrl, PollIntervalSeconds);
            }
            BridgeLauncher.EnsureBridgeRunning(); // 内部一次性，幂等

            // 总开关：任一工具（Codex/ZCode/Antigravity/dsh）启用才轮询与驱动。
            bool anyToolOn = (Plugin.EnableCodex?.Value ?? false)
                || (Plugin.EnableZcode?.Value ?? false)
                || (Plugin.EnableAntigravity?.Value ?? false)
                || (Plugin.EnableDsh?.Value ?? false);
            if (!anyToolOn)
            {
                if (_wasAnyToolOn)
                {
                    _wasAnyToolOn = false;
                    _lastAppliedState = "";
                    CurrentCodexState = "unknown";
                    HeroineDriver.ReleaseToolDrivenState();
                    Log("所有工具联动已关闭，女主角回到自然状态");
                }
                return;
            }
            _wasAnyToolOn = true;

            _client.Update();
            var snap = _client.Current;

            // 连接状态迁移：断连瞬间把状态复位为 unknown 并释放女主的工具驱动状态，
            // 防止 Bridge 崩溃后 CurrentCodexState 冻结在 "working"，把女主永久锁在工作动画。
            if (snap.Connected != _wasConnected)
            {
                _wasConnected = snap.Connected;
                BridgeConnected = snap.Connected;
                if (!snap.Connected)
                {
                    _lastAppliedState = "";
                    CurrentCodexState = "unknown";
                    HeroineDriver.ReleaseToolDrivenState();
                    Log("Bridge 断开连接，状态已复位为 unknown");
                }
            }

            // 心跳
            if (Time.unscaledTime - _lastHeartbeatTime >= HeartbeatIntervalSeconds)
            {
                _lastHeartbeatTime = Time.unscaledTime;
                Log($"心跳: connected={snap.Connected}, state={snap.State}, error={snap.Error}");
            }

            // 状态变化：驱动女主角 + 日志（仅在驱动成功时推进已记录状态，防止场景加载期间漏掉初始状态）
            if (snap.Connected && snap.State != _lastAppliedState)
            {
                if (HeroineDriver.ApplyCodexState(snap.State))
                {
                    _lastAppliedState = snap.State;
                    Log($"状态 -> {snap.State} (lastEvent={snap.LastEvent}, detail={snap.Detail})");
                }
            }

            if (snap.Connected)
            {
                CurrentCodexState = snap.State;
            }

            // 温和校正（每 4 秒）：工具 working → 女主工作；工具非工作 → 女主若在干活则拉回默认（只"停手"，不强制动作）。
            // 仅在有真实工具数据（Bridge 可达且状态非 unknown）时生效，见 HeroineDriver.Reassert。
            // 解决"女主卡在工作动画无法解除"的问题，同时避免高频强拉的不自然感。
            if (Plugin.ToolPrimary != null && Plugin.ToolPrimary.Value
                && Time.unscaledTime - _lastReassertTime >= 4f)
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
                _selectedRow = (_selectedRow + RowRects.Length - 1) % RowRects.Length;
                Log("选择行 " + (_selectedRow + 1));
            }
            if (down && !_downWasDown)
            {
                _selectedRow = (_selectedRow + 1) % RowRects.Length;
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
                    // 先判「隐藏窗口」按钮：否则它会被下面的行命中逻辑抢走
                    if (CloseButtonRect.Contains(p))
                    {
                        _lmbWasDown = lmb;
                        CloseConfigWindow("点击隐藏按钮");
                        return;
                    }

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

        /// <summary>收起设置窗口（F8 / Esc / 右上角按钮共用）。</summary>
        private void CloseConfigWindow(string cause)
        {
            if (!_configWindowOpen)
            {
                return;
            }
            _configWindowOpen = false;
            _closeHover = false;
            Log("设置窗口 关闭（" + cause + "）");
        }

        /// <summary>鼠标是否悬停在隐藏按钮上（用于高亮反馈）。每帧仅在窗口打开时探测一次。</summary>
        private void UpdateCloseButtonHover()
        {
            bool hover = false;
            if (TryGetGameClientPoint(out var p))
            {
                hover = CloseButtonRect.Contains(p);
            }
            _closeHover = hover;
        }

        private void ToggleSelectedRow()
        {
            switch (_selectedRow)
            {
                case 0:
                    if (Toggle(ref Plugin.EnableCodex, "Codex 联动"))
                    {
                        ApplyHooksToggle();
                    }
                    break;
                case 1:
                    if (Toggle(ref Plugin.EnableZcode, "ZCode 联动"))
                    {
                        ApplyHooksToggle();
                    }
                    break;
                case 2:
                    if (Toggle(ref Plugin.EnableAntigravity, "Antigravity 联动"))
                    {
                        ApplyHooksToggle();
                    }
                    break;
                case 3:
                    if (Toggle(ref Plugin.EnableDsh, "DSH 联动"))
                    {
                        ApplyHooksToggle();
                    }
                    break;
                case 4: Toggle(ref Plugin.ToolPrimary, "工具优先"); break;
                case 5: Toggle(ref Plugin.ShowOverlay, "状态浮窗"); break;
            }
        }

        /// <summary>联动开关变化后，按 AutoInstallHooks 与新开关值同步写入/移除对应工具的钩子配置。</summary>
        private static void ApplyHooksToggle()
        {
            if (!(Plugin.AutoInstallHooks?.Value ?? true))
            {
                return; // 关闭自动配置：开关只影响本会话的驱动，不动配置文件
            }
            HooksInstaller.EnsureHooksJson();        // 按 EnableCodex 写入或移除
            HooksInstaller.EnsureZcodeHooks();       // 按 EnableZcode 写入或移除
            HooksInstaller.EnsureAntigravityHooks();  // 按 EnableAntigravity 写入或移除
            HooksInstaller.EnsureDshHooks();          // 按 EnableDsh 写入或移除
        }

        private static bool Toggle(ref BepInEx.Configuration.ConfigEntry<bool> entry, string name)
        {
            if (entry == null)
            {
                return false;
            }
            entry.Value = !entry.Value;
            Instance?.Log($"设置: {name} -> {(entry.Value ? "开" : "关")}");
            return true;
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

        /// <summary>按进程 ID 找到游戏主可见窗口（每 10 秒刷新一次句柄）。</summary>
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
                if (wpid == pid && IsWindowVisible(h))
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
            EnsureStyles();
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

        /// <summary>
        /// 状态浮窗：只用中文、不暴露内部事件名，按"状态 → 谁在驱动 → 最近发生了什么 → 怎么改设置"排列。
        /// 文案由 <see cref="OverlayText"/> 组装（纯函数、可单独测试），这里只负责画。
        /// </summary>
        private void DrawStatusOverlay()
        {
            var snap = _client?.Current;

            bool anyToolOn = (Plugin.EnableCodex?.Value ?? false)
                || (Plugin.EnableZcode?.Value ?? false)
                || (Plugin.EnableAntigravity?.Value ?? false)
                || (Plugin.EnableDsh?.Value ?? false);

            var content = OverlayText.Build(
                anyToolOn,
                snap != null && snap.Connected,
                snap?.State,
                snap?.Source,
                snap?.LastEvent,
                snap?.Detail,
                snap?.Error,
                snap?.SecondsSinceLastEvent ?? 0L,
                Plugin.ToolPrimary?.Value ?? false);

            var boxRect = new Rect(10, 10, OverlayText.OverlayWidth, OverlayText.Height(content));

            _overlayBoxStyle.normal.textColor = Color.white;
            GUI.Box(boxRect, "", _overlayBoxStyle);

            float y = boxRect.y + OverlayText.PadTop;
            _overlayTitleStyle.normal.textColor = StateColor(snap);
            GUI.Label(new Rect(boxRect.x + OverlayText.PadX, y, boxRect.width - OverlayText.PadX * 2, OverlayText.TitleHeight),
                content.Title, _overlayTitleStyle);
            y += OverlayText.TitleHeight;

            foreach (var info in content.Infos)
            {
                GUI.Label(new Rect(boxRect.x + OverlayText.PadX, y, boxRect.width - OverlayText.PadX * 2, OverlayText.InfoHeight),
                    info, _overlayInfoStyle);
                y += OverlayText.InfoHeight;
            }
            if (content.Infos.Count > 0)
            {
                y += OverlayText.Gap;
            }

            GUI.Label(new Rect(boxRect.x + OverlayText.PadX, y, boxRect.width - OverlayText.PadX * 2, OverlayText.HintHeight),
                content.Hint, _overlayHintStyle);
        }

        // ---------------- 设置窗口 ----------------

        private void DrawConfigWindow()
        {
            var snap = _client?.Current;
            GUI.Box(WindowRect, "", _configBgStyle);

            // 标题留出右上角按钮的位置
            GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + 10, WindowRect.width - 60, 24), "Chill AI 设置", _configTitleStyle);
            DrawCloseButton();

            var statusText = OverlayText.ConfigStatusLine(
                snap != null && snap.Connected,
                snap?.State,
                snap?.Source,
                snap?.LastEvent,
                snap?.SecondsSinceLastEvent ?? 0L);
            _configStatusStyle.normal.textColor = StateColor(snap);
            GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + 42, WindowRect.width - 24, 24), statusText, _configStatusStyle);

            DrawToggleRow(0, "启用 Codex 联动", Plugin.EnableCodex?.Value ?? true);
            DrawToggleRow(1, "启用 ZCode 联动", Plugin.EnableZcode?.Value ?? true);
            DrawToggleRow(2, "启用 Antigravity 联动", Plugin.EnableAntigravity?.Value ?? true);
            DrawToggleRow(3, "启用 DSH 联动", Plugin.EnableDsh?.Value ?? true);
            DrawToggleRow(4, "工具优先（覆盖番茄钟）", Plugin.ToolPrimary?.Value ?? true);
            DrawToggleRow(5, "显示状态浮窗", Plugin.ShowOverlay?.Value ?? true);

            GUI.Label(new Rect(WindowRect.x + 12, WindowRect.y + 282, WindowRect.width - 24, 20),
                "↑↓ 选择 · Enter/空格 切换 · 点右上角 × 或按 Esc/F8 收起", _configHintStyle);
        }

        /// <summary>
        /// 右上角「隐藏窗口」按钮：鼠标悬停高亮，点击收起设置窗口。
        /// 用 × (U+00D7) 而不是 ✕ (U+2715)：前者在 Arial/Unity 内置字体里必定有字形，
        /// 后者可能渲染成空白方块。
        /// </summary>
        private void DrawCloseButton()
        {
            _closeButtonStyle.normal.textColor = _closeHover
                ? new Color(1f, 0.45f, 0.4f)
                : new Color(0.75f, 0.75f, 0.75f);
            GUI.Box(CloseButtonRect, "×", _closeButtonStyle);
        }

        private void DrawToggleRow(int index, string label, bool value)
        {
            bool selected = index == _selectedRow;
            _rowStyle.normal.textColor = selected
                ? new Color(1f, 0.85f, 0.2f)                          // 选中：亮黄
                : value ? new Color(0.1f, 0.6f, 0.3f) : new Color(0.5f, 0.5f, 0.5f);
            // 选中三角和勾选框分开显示，互不遮挡
            var prefix = (selected ? "▶ " : "  ") + (value ? "[✓] " : "[  ] ");
            GUI.Box(RowRects[index], prefix + label, _rowStyle);
        }

        // ---------------- 工具 ----------------

        // 文案（状态/事件/来源的中文化、时间说法）统一在 OverlayText 里，纯函数、可单独测试；
        // 这里只留与 Unity 相关的绘制与颜色。

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
    }
}
