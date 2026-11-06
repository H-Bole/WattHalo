using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Globalization;
using Media = System.Windows.Media;
using Imaging = System.Windows.Media.Imaging;
using WattMonitor.Core;

namespace WattMonitor.App;

/// <summary>A separate message thread keeps taskbar painting independent of the WPF dispatcher.</summary>
internal sealed class TaskbarWidget : IDisposable
{
    private readonly AppController controller;
    private readonly Thread thread;
    private readonly object lifecycle = new();
    private readonly WndProc callback;
    private readonly WinEventProc eventCallback;
    private readonly string className = "WattMonitor.Taskbar." + Environment.ProcessId;
    private volatile bool visible;
    private volatile bool disposed;
    private volatile TelemetrySnapshot? snapshot;
    private volatile AppSettings settings;
    private IntPtr window, parent;
    private uint threadId;
    private int width, height;
    private int shownCount;
    private volatile bool stale;
    private volatile bool lightTaskbar;
    private string reportedMode = "";
    private IntPtr tooltip, tooltipText;
    private string currentTooltip = "";
    private Task<Rect[]?>? obstacleQuery;
    private Rect[]? automationObstacles;
    private IntPtr obstacleTray, queryTray, eventHook;
    private long obstacleGeneration, queryGeneration;
    private long lastObstacleAttempt;
    private readonly LayeredSurface surface = new();
    private Media.DrawingVisual? visual;
    private Imaging.RenderTargetBitmap? renderBitmap;
    private bool layoutDirty = true;
    private int retryRequested, visibilityChanged, reportVersion;

    public TaskbarWidget(AppController controller)
    {
        this.controller = controller;
        settings = controller.Settings;
        lightTaskbar = ReadTaskbarTheme();
        callback = WindowProcedure;
        eventCallback = TaskbarEvent;
        controller.Updated += OnUpdate;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += SystemThemeChanged;
        thread = new Thread(Run) { IsBackground = true, Name = "WattMonitor.Taskbar" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    public void SetVisible(bool value)
    {
        visible = value;
        Interlocked.Increment(ref reportVersion);
        Interlocked.Exchange(ref visibilityChanged, 1);
        Wake();
    }

    internal void Retry()
    {
        if (disposed) return;
        Interlocked.Exchange(ref retryRequested, 1);
        Wake();
    }

    private void Wake()
    {
        lock (lifecycle)
            if (threadId != 0 && !disposed) PostThreadMessage(threadId, 0x8001, IntPtr.Zero, IntPtr.Zero);
    }

    private void OnUpdate(object? sender, EventArgs args)
    {
        snapshot = controller.Snapshot;
        settings = controller.Settings;
        stale = controller.MonitorStatus.IsStale;
        Wake();
    }

    private void SystemThemeChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs args)
    {
        lightTaskbar = ReadTaskbarTheme();
        Wake();
    }

    private static bool ReadTaskbarTheme()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    private void Run()
    {
        PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
        lock (lifecycle)
        {
            if (disposed) return;
            threadId = GetCurrentThreadId();
        }
        var wc = new WindowClass
        {
            Size = (uint)Marshal.SizeOf<WindowClass>(), Style = 0x0008, Procedure = Marshal.GetFunctionPointerForDelegate(callback),
            Instance = GetModuleHandle(null), ClassName = className, Cursor = LoadCursor(IntPtr.Zero, (IntPtr)32512)
        };
        var registered = RegisterClassEx(ref wc) != 0;
        if (!registered) ReportNativeFailure("RegisterClassEx", Marshal.GetLastWin32Error());
        SetTimer(IntPtr.Zero, (UIntPtr)1, 1500, IntPtr.Zero);
        if (registered) CreateOrLocate();
        while (!disposed && GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.Hwnd == IntPtr.Zero && (message.Id == 0x113 || message.Id == 0x8001))
            {
                if (Interlocked.Exchange(ref visibilityChanged, 0) != 0) reportedMode = "";
                if (Interlocked.Exchange(ref retryRequested, 0) != 0) InvalidateObstacles(true);
                if (!registered)
                {
                    registered = RegisterClassEx(ref wc) != 0;
                    if (!registered) ReportNativeFailure("RegisterClassEx", Marshal.GetLastWin32Error());
                }
                if (registered) CreateOrLocate();
                continue;
            }
            TranslateMessage(ref message);
            DispatchMessage(ref message);
        }
        WatchTaskbar(IntPtr.Zero);
        DestroyWidget();
        KillTimer(IntPtr.Zero, (UIntPtr)1);
        if (registered) UnregisterClass(className, wc.Instance);
        lock (lifecycle) threadId = 0;
    }

    private void CreateOrLocate()
    {
        if (!visible)
        {
            if (window != IntPtr.Zero && IsWindow(window)) ShowWindow(window, 0);
            WatchTaskbar(IntPtr.Zero);
            ReportMode("已隐藏");
            return;
        }
        var tray = FindWindow("Shell_TrayWnd", null);
        if (window != IntPtr.Zero && (!IsWindow(window) || parent != tray || GetParent(window) != tray)) DestroyWidget();
        parent = tray;
        WatchTaskbar(tray);
        if (tray == IntPtr.Zero)
        {
            ReportMode("未找到 Windows 任务栏，正在重试");
            return;
        }
        if (window == IntPtr.Zero && !CreateWidget()) return;
        Position();
    }

    private bool CreateWidget()
    {
        // WS_EX_LAYERED | TOOLWINDOW | NOACTIVATE | NOPARENTNOTIFY; always a WS_CHILD of Explorer.
        window = CreateWindowEx(0x08080084u, className, "WattHalo 快速监测",
            0x44000000u, 0, 0, 1, 1, parent, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (window == IntPtr.Zero)
        {
            ReportNativeFailure("CreateWindowEx", Marshal.GetLastWin32Error());
            return false;
        }
        CreateTooltip();
        return true;
    }

    private void DestroyWidget()
    {
        if (tooltip != IntPtr.Zero && IsWindow(tooltip)) DestroyWindow(tooltip);
        tooltip = IntPtr.Zero;
        if (tooltipText != IntPtr.Zero) Marshal.FreeHGlobal(tooltipText);
        tooltipText = IntPtr.Zero;
        currentTooltip = "";
        if (window != IntPtr.Zero && IsWindow(window)) DestroyWindow(window);
        window = IntPtr.Zero;
        surface.Dispose();
        renderBitmap = null;
        visual = null;
    }

    private void ReportNativeFailure(string operation, int error) =>
        ReportMode($"任务栏 {operation} 失败（Win32 {error}），正在重试");

    private void ReportMode(string mode)
    {
        if (reportedMode == mode) return;
        reportedMode = mode;
        var version = Interlocked.Increment(ref reportVersion);
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted)
            dispatcher.BeginInvoke(new Action(() =>
            {
                if (!disposed && version == Volatile.Read(ref reportVersion) && (visible || mode == "已隐藏"))
                    controller.SetTaskbarMode(mode);
            }));
    }

    private void Position()
    {
        if (window == IntPtr.Zero || !IsWindow(window)) { CreateOrLocate(); return; }
        if (!visible)
        {
            ShowWindow(window, 0);
            ReportMode("已隐藏");
            return;
        }
        var dpi = GetDpiForWindow(parent);
        if (dpi == 0) dpi = 96;
        var scale = dpi / 96d;
        var metrics = settings.TaskbarMetrics;
        if (!TryEmbeddedPlacement(parent, scale, out var left, out height, out width, out shownCount, out var reason))
        {
            ShowWindow(window, 0);
            ReportMode(reason + "，正在重试");
            return;
        }
        var origin = new Point { X = left };
        void PlacementFailed(string operation)
        {
            var error = Marshal.GetLastWin32Error();
            ShowWindow(window, 0);
            ReportNativeFailure(operation, error);
        }
        if (GetParent(window) != parent)
        {
            ShowWindow(window, 0);
            ReportMode("任务栏父窗口已变化，正在重试");
            return;
        }
        if (!ClientToScreen(parent, ref origin)) { PlacementFailed("ClientToScreen"); return; }
        if (!GetWindowRect(window, out var current)) { PlacementFailed("GetWindowRect"); return; }
        if ((current.Left != origin.X || current.Top != origin.Y || current.Right - current.Left != width || current.Bottom - current.Top != height) &&
            !SetWindowPos(window, IntPtr.Zero, left, 0, width, height, 0x0010))
        {
            PlacementFailed("SetWindowPos");
            return;
        }
        if (!RenderAndPresent(scale))
        {
            ShowWindow(window, 0);
            return;
        }
        ShowWindow(window, 8);
        if (IsWindowVisible(window) && !WidgetReceivesInput() &&
            !SetWindowPos(window, IntPtr.Zero, 0, 0, 0, 0, 0x0013))
        {
            PlacementFailed("SetWindowPos/Z序");
            return;
        }
        if (!IsWindowVisible(window) || !WidgetReceivesInput())
        {
            ShowWindow(window, 0);
            ReportMode("任务栏组件暂不可见，正在重试");
            return;
        }
        ReportMode(shownCount < metrics.Length ? $"已嵌入任务栏 · 已折叠 {metrics.Length - shownCount} 项" : "已嵌入任务栏");
        UpdateTooltip();
    }

    private bool WidgetReceivesInput()
    {
        if (!GetWindowRect(window, out var bounds)) return false;
        foreach (var fraction in new[] { 1, 2, 3 })
        {
            var point = new Point { X = bounds.Left + (bounds.Right - bounds.Left) * fraction / 4, Y = (bounds.Top + bounds.Bottom) / 2 };
            var hit = WindowFromPoint(point);
            if (hit != window && !IsChild(window, hit)) return false;
        }
        return true;
    }

    private static int WidgetWidth(int shown, int total, double scale) => (int)((shown * 108 + (shown < total ? 38 : 0) + 16) * scale);

    private bool TryEmbeddedPlacement(IntPtr tray, double scale, out int left, out int taskbarHeight, out int fittedWidth, out int fittedCount, out string reason)
    {
        left = fittedWidth = fittedCount = 0; taskbarHeight = 0;
        reason = "无法读取任务栏布局";
        if (tray == IntPtr.Zero || !GetClientRect(tray, out var client) || client.Right < 100 || client.Bottom < 24) return false;
        if (client.Bottom > client.Right)
        {
            reason = "竖直任务栏暂不支持嵌入";
            return false;
        }
        taskbarHeight = client.Bottom;
        if (!TryGetTaskbarButtons(tray, out var buttonRects))
        {
            reason = "无法确认任务栏控件位置";
            return false;
        }
        var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero || !GetWindowRect(notify, out var notifyRect) || notifyRect.Right <= notifyRect.Left)
        {
            reason = "无法确认任务栏通知区域";
            return false;
        }
        var obstacles = new List<Rect>(buttonRects) { notifyRect };
        var agentSpeed = FindWindow("AgentSpeed.TaskbarWidget", null);
        if (agentSpeed != IntPtr.Zero && IsWindowVisible(agentSpeed) && GetWindowRect(agentSpeed, out var agentRect))
            obstacles.Add(agentRect);
        EnumChildWindows(tray, (child, _) =>
        {
            if (child == window || !IsWindowVisible(child)) return true;
            var name = new System.Text.StringBuilder(128);
            GetClassName(child, name, name.Capacity);
            var type = name.ToString();
            // These shell hosts span empty space; their individual controls are obtained via UI Automation.
            if (type is "Windows.UI.Composition.DesktopWindowContentBridge" or "Windows.UI.Input.InputSite.WindowClass" or
                "ReBarWindow32" or "MSTaskSwWClass" or "MSTaskListWClass" or "TrayDummySearchControl") return true;
            if (GetWindowRect(child, out var rect)) obstacles.Add(rect);
            return true;
        }, IntPtr.Zero);

        var margin = Math.Max(1, (int)Math.Ceiling(4 * scale));
        var intervals = new List<(int Left, int Right)>();
        foreach (var obstacle in obstacles)
        {
            var point = new Point { X = obstacle.Left, Y = obstacle.Top };
            if (obstacle.Right <= obstacle.Left || obstacle.Bottom <= obstacle.Top || !ScreenToClient(tray, ref point)) continue;
            var bottom = point.Y + obstacle.Bottom - obstacle.Top;
            if (bottom <= 0 || point.Y >= client.Bottom) continue;
            var start = Math.Max(0, point.X - margin);
            var end = Math.Min(client.Right, point.X + obstacle.Right - obstacle.Left + margin);
            if (end > start) intervals.Add((start, end));
        }
        intervals.Sort((a, b) => b.Right.CompareTo(a.Right));
        for (var count = settings.TaskbarMetrics.Length; count >= 0; count--)
        {
            var candidateWidth = WidgetWidth(count, settings.TaskbarMetrics.Length, scale);
            var right = client.Right - margin;
            foreach (var obstacle in intervals)
            {
                if (obstacle.Left >= right) continue;
                if (right - obstacle.Right >= candidateWidth)
                { left = right - candidateWidth; fittedWidth = candidateWidth; fittedCount = count; return true; }
                right = Math.Min(right, obstacle.Left);
            }
            if (right - margin >= candidateWidth)
            { left = right - candidateWidth; fittedWidth = candidateWidth; fittedCount = count; return true; }
        }
        reason = "任务栏没有足够的安全空间";
        return false;
    }

    private void WatchTaskbar(IntPtr tray)
    {
        if (obstacleTray == tray) return;
        if (eventHook != IntPtr.Zero) UnhookWinEvent(eventHook);
        eventHook = IntPtr.Zero;
        obstacleTray = tray;
        automationObstacles = null;
        InvalidateObstacles(true);
        if (tray == IntPtr.Zero) return;
        GetWindowThreadProcessId(tray, out var processId);
        if (processId != 0)
            eventHook = SetWinEventHook(0x8000, 0x800B, IntPtr.Zero, eventCallback, processId, 0, 0x0002);
    }

    private void TaskbarEvent(IntPtr hook, uint eventId, IntPtr hwnd, int objectId, int childId, uint eventThread, uint time)
    {
        if (hwnd == IntPtr.Zero || hwnd == window || IsChild(window, hwnd) || childId != 0 || (objectId != 0 && objectId != -4) ||
            eventId is not (0x8000 or 0x8001 or 0x8002 or 0x8003 or 0x8004 or 0x800B) ||
            eventId == 0x800B && objectId != 0 ||
            (hwnd != obstacleTray && !IsChild(obstacleTray, hwnd))) return;
        InvalidateObstacles(false);
        Wake();
    }

    private void InvalidateObstacles(bool immediate)
    {
        if (!layoutDirty || immediate) lastObstacleAttempt = 0;
        layoutDirty = true;
        obstacleGeneration++;
    }

    // The composition host spans the taskbar; its accessible controls provide the occupied rectangles.
    private bool TryGetTaskbarButtons(IntPtr tray, out Rect[] rectangles)
    {
        if (obstacleQuery is { IsCompleted: true } completed)
        {
            obstacleQuery = null;
            if (completed.IsFaulted) _ = completed.Exception;
            if (queryTray == tray && queryGeneration == obstacleGeneration &&
                completed.IsCompletedSuccessfully && completed.Result is { Length: > 0 } result)
            {
                automationObstacles = result;
                layoutDirty = false;
            }
            else if (queryTray == tray) layoutDirty = true;
        }
        var interval = layoutDirty ? .25 : eventHook == IntPtr.Zero ? 10 : 30;
        if (obstacleQuery is null && (lastObstacleAttempt == 0 || Stopwatch.GetElapsedTime(lastObstacleAttempt).TotalSeconds >= interval))
        {
            lastObstacleAttempt = Stopwatch.GetTimestamp();
            queryTray = tray;
            queryGeneration = obstacleGeneration;
            var ownWindow = window;
            obstacleQuery = Task.Run(() => ReadTaskbarButtons(tray, ownWindow));
            _ = obstacleQuery.ContinueWith(_ => Wake(), TaskScheduler.Default);
        }
        rectangles = automationObstacles ?? [];
        return !layoutDirty && rectangles.Length > 0;
    }

    private static Rect[]? ReadTaskbarButtons(IntPtr tray, IntPtr ownWindow)
    {
        try
        {
            var root = System.Windows.Automation.AutomationElement.FromHandle(tray);
            if (root is null) return null;
            var cache = new System.Windows.Automation.CacheRequest { TreeScope = System.Windows.Automation.TreeScope.Element,
                AutomationElementMode = System.Windows.Automation.AutomationElementMode.None };
            cache.Add(System.Windows.Automation.AutomationElement.BoundingRectangleProperty);
            cache.Add(System.Windows.Automation.AutomationElement.IsOffscreenProperty);
            cache.Add(System.Windows.Automation.AutomationElement.ControlTypeProperty);
            cache.Add(System.Windows.Automation.AutomationElement.AutomationIdProperty);
            cache.Add(System.Windows.Automation.AutomationElement.NativeWindowHandleProperty);
            using (cache.Activate())
            {
                var found = root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition);
                var rectangles = new List<Rect>();
                var foundStart = false;
                foreach (System.Windows.Automation.AutomationElement element in found)
                {
                    var handle = (IntPtr)element.Cached.NativeWindowHandle;
                    if (handle != IntPtr.Zero && (handle == ownWindow || IsChild(ownWindow, handle))) continue;
                    var type = element.Cached.ControlType;
                    if (type == System.Windows.Automation.ControlType.Pane || type == System.Windows.Automation.ControlType.Window ||
                        type == System.Windows.Automation.ControlType.Group || type == System.Windows.Automation.ControlType.ToolBar ||
                        type == System.Windows.Automation.ControlType.List) continue;
                    var bounds = element.Cached.BoundingRectangle;
                    if (element.Cached.IsOffscreen || bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0 ||
                        !double.IsFinite(bounds.Left) || !double.IsFinite(bounds.Top) || !double.IsFinite(bounds.Right) || !double.IsFinite(bounds.Bottom)) continue;
                    if (type == System.Windows.Automation.ControlType.Button && element.Cached.AutomationId == "StartButton") foundStart = true;
                    rectangles.Add(new Rect { Left = (int)Math.Floor(bounds.Left), Top = (int)Math.Floor(bounds.Top),
                        Right = (int)Math.Ceiling(bounds.Right), Bottom = (int)Math.Ceiling(bounds.Bottom) });
                }
                return foundStart ? rectangles.ToArray() : null;
            }
        }
        catch (Exception ex) when (ex is System.Windows.Automation.ElementNotAvailableException or COMException or InvalidOperationException)
        { Trace.TraceWarning("Taskbar button positions are unavailable: " + ex.Message); return null; }
    }

    private void CreateTooltip()
    {
        InitCommonControls();
        tooltip = CreateWindowEx(8, "tooltips_class32", "", 0x80000003, 0, 0, 0, 0,
            window, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (tooltip == IntPtr.Zero) return;
        tooltipText = Marshal.StringToHGlobalUni("WattHalo · " + Controls.Ui.T("快速监测"));
        var info = new ToolInfo
        {
            Size = (uint)Marshal.SizeOf<ToolInfo>(), Flags = 0x11, Window = window,
            Id = (UIntPtr)(ulong)window.ToInt64(), Text = tooltipText
        };
        SendToolMessage(tooltip, 0x432, IntPtr.Zero, ref info);
        SendMessage(tooltip, 0x418, IntPtr.Zero, (IntPtr)450);
    }

    private void UpdateTooltip()
    {
        if (tooltip == IntPtr.Zero) return;
        var lines = new List<string> { "WattHalo · " + Controls.Ui.Diagnostic(reportedMode) };
        if (stale && snapshot is not null) lines.Add($"{Controls.Ui.T("数据已过期 · 最后更新", "Stale data · last updated")} {snapshot.Timestamp.ToLocalTime():HH:mm:ss}");
        foreach (var kind in settings.TaskbarMetrics)
        {
            var (top, bottom) = MetricText(kind);
            lines.Add(top + "  ·  " + bottom);
        }
        lines.Add(Controls.Ui.T("点击展开快速监测，右键打开主窗口。", "Click to open quick view; right-click to open the main window."));
        var text = string.Join("\n", lines);
        if (text == currentTooltip) return;
        var replacement = Marshal.StringToHGlobalUni(text);
        var info = new ToolInfo
        {
            Size = (uint)Marshal.SizeOf<ToolInfo>(), Flags = 0x11, Window = window,
            Id = (UIntPtr)(ulong)window.ToInt64(), Text = replacement
        };
        SendToolMessage(tooltip, 0x439, IntPtr.Zero, ref info);
        if (tooltipText != IntPtr.Zero) Marshal.FreeHGlobal(tooltipText);
        tooltipText = replacement;
        currentTooltip = text;
    }

    private IntPtr WindowProcedure(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (message)
            {
                case 0x14: return (IntPtr)1;
                case 0x0F:
                    BeginPaint(hwnd, out var paint);
                    EndPaint(hwnd, ref paint);
                    return IntPtr.Zero;
                case 0x21: return (IntPtr)3;
                case 0x201:
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(controller.TogglePanel));
                    return IntPtr.Zero;
                case 0x203:
                case 0x205: System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(controller.ShowMain)); return IntPtr.Zero;
                case 0x2E0:
                case 0x2E3:
                case 0x7E:
                case 0x1A: InvalidateObstacles(true); Position(); return IntPtr.Zero;
            }
        }
        catch (Exception ex) { Trace.TraceError("Taskbar widget message failed: " + ex); }
        return DefWindowProc(hwnd, message, wParam, lParam);
    }

    private bool RenderAndPresent(double scale)
    {
        try
        {
            if (!surface.Ensure(width, height, out var allocationError))
            {
                ReportNativeFailure("CreateDIBSection/内存 DC", allocationError);
                return false;
            }
            visual ??= new Media.DrawingVisual();
            Media.TextOptions.SetTextRenderingMode(visual, Media.TextRenderingMode.Grayscale);
            Media.TextOptions.SetTextHintingMode(visual, Media.TextHintingMode.Fixed);
            var highContrast = System.Windows.SystemParameters.HighContrast;
            var primary = highContrast ? System.Windows.SystemColors.WindowTextColor :
                lightTaskbar ? Media.Color.FromRgb(29, 37, 43) : Media.Color.FromRgb(232, 236, 237);
            var secondary = highContrast ? primary :
                lightTaskbar ? Media.Color.FromRgb(8, 127, 107) : Media.Color.FromRgb(118, 206, 187);
            var face = new Media.Typeface(new Media.FontFamily("Segoe UI"), System.Windows.FontStyles.Normal,
                System.Windows.FontWeights.Medium, System.Windows.FontStretches.Normal);
            var metrics = settings.TaskbarMetrics;
            var count = Math.Min(shownCount, metrics.Length);
            var dipWidth = width / scale;
            var dipHeight = height / scale;
            using (var drawing = visual.RenderOpen())
            {
                // Nonzero alpha keeps transparent gaps clickable on a layered child window.
                var background = highContrast ? System.Windows.SystemColors.WindowColor : Media.Color.FromArgb(1, 0, 0, 0);
                drawing.DrawRectangle(new Media.SolidColorBrush(background), null, new System.Windows.Rect(0, 0, dipWidth, dipHeight));
                void Draw(string text, double x, double y, double availableWidth, Media.Color color)
                {
                    var formatted = new Media.FormattedText(text, CultureInfo.CurrentUICulture, System.Windows.FlowDirection.LeftToRight,
                        face, 11, new Media.SolidColorBrush(color), scale)
                    { MaxTextWidth = Math.Max(1, availableWidth), MaxLineCount = 1, Trimming = System.Windows.TextTrimming.CharacterEllipsis };
                    drawing.DrawText(formatted, new System.Windows.Point(Math.Round(x * scale) / scale, Math.Round(y * scale) / scale));
                }
                for (var i = 0; i < count; i++)
                {
                    var (top, bottom) = MetricText(metrics[i]);
                    if (stale)
                    {
                        top = Controls.Ui.Name(metrics[i]) + "  —";
                        bottom = metrics[i] is MetricKind.Cpu or MetricKind.Gpu ? "— W" : Controls.Ui.T("数据已过期", "Stale data");
                    }
                    var x = 8 + i * 108;
                    Draw(top, x, dipHeight / 2 - 15, 104, primary);
                    Draw(bottom, x, dipHeight / 2, 104, secondary);
                }
                if (count < metrics.Length)
                    Draw("+" + (metrics.Length - count), 8 + count * 108, dipHeight / 2 - 8, 30, primary);
            }
            if (renderBitmap is null || renderBitmap.PixelWidth != width || renderBitmap.PixelHeight != height ||
                Math.Abs(renderBitmap.DpiX - 96 * scale) > .01)
                renderBitmap = new Imaging.RenderTargetBitmap(width, height, 96 * scale, 96 * scale, Media.PixelFormats.Pbgra32);
            else renderBitmap.Clear();
            renderBitmap.Render(visual);
            renderBitmap.CopyPixels(new System.Windows.Int32Rect(0, 0, width, height), surface.Bits, checked(width * height * 4), checked(width * 4));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or OutOfMemoryException or OverflowException or COMException)
        {
            Trace.TraceError("Taskbar layered rendering failed: " + ex);
            ReportMode($"任务栏像素渲染失败（HRESULT 0x{ex.HResult:X8}），正在重试");
            return false;
        }
        if (surface.Present(window, out var error)) return true;
        ReportNativeFailure("UpdateLayeredWindow", error);
        return false;
    }

    /// <summary>Reusable top-down premultiplied BGRA surface owned by the taskbar thread.</summary>
    private sealed class LayeredSurface : IDisposable
    {
        private IntPtr memory, bitmap, previous;
        private int width, height;
        public IntPtr Bits { get; private set; }

        public bool Ensure(int newWidth, int newHeight, out int error)
        {
            error = 0;
            if (bitmap != IntPtr.Zero && width == newWidth && height == newHeight) return true;
            Dispose();
            memory = CreateCompatibleDC(IntPtr.Zero);
            if (memory == IntPtr.Zero) { error = Marshal.GetLastWin32Error(); return false; }
            var info = new BitmapInfo
            {
                Header = new BitmapInfoHeader
                {
                    Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(), Width = newWidth, Height = -newHeight,
                    Planes = 1, BitCount = 32
                }
            };
            bitmap = CreateDIBSection(memory, ref info, 0, out var bits, IntPtr.Zero, 0);
            if (bitmap == IntPtr.Zero || bits == IntPtr.Zero)
            {
                error = Marshal.GetLastWin32Error();
                Dispose();
                return false;
            }
            previous = SelectObject(memory, bitmap);
            if (previous == IntPtr.Zero || previous == (IntPtr)(-1))
            {
                error = Marshal.GetLastWin32Error();
                previous = IntPtr.Zero;
                Dispose();
                return false;
            }
            Bits = bits;
            width = newWidth; height = newHeight;
            return true;
        }

        public bool Present(IntPtr hwnd, out int error)
        {
            var size = new Size { Width = width, Height = height };
            var source = new Point();
            var blend = new BlendFunction { SourceConstantAlpha = 255, AlphaFormat = 1 };
            var success = UpdateLayeredWindow(hwnd, IntPtr.Zero, IntPtr.Zero, ref size, memory, ref source, 0, ref blend, 2);
            error = success ? 0 : Marshal.GetLastWin32Error();
            return success;
        }

        public void Dispose()
        {
            if (memory != IntPtr.Zero && previous != IntPtr.Zero) SelectObject(memory, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memory != IntPtr.Zero) DeleteDC(memory);
            memory = bitmap = previous = Bits = IntPtr.Zero;
            width = height = 0;
        }
    }

    private (string, string) MetricText(MetricKind kind)
    {
        var s = snapshot;
        var gpu = s?.Gpus?.FirstOrDefault(x => x.IsDiscrete) ?? s?.Gpus?.FirstOrDefault();
        static bool Valid(double? x, double max = double.MaxValue) => x is >= 0 && x <= max && double.IsFinite(x.Value);
        static string W(double? x) => Valid(x) ? $"{x:F1} W" : "— W";
        static string Rate(double? x) => Valid(x) ? x >= 1048576 ? $"{x / 1048576:F1} MB/s" : $"{x / 1024:F0} KB/s" : "—";
        return kind switch
        {
            MetricKind.Cpu => (Valid(s?.CpuPercent, 100) ? $"CPU {s!.CpuPercent:F0}%" : "CPU —", W(s?.CpuPackageWatts)),
            MetricKind.Gpu => (Valid(gpu?.UtilizationPercent, 100) ? $"GPU {gpu!.UtilizationPercent:F0}%" : "GPU —", W(gpu?.PowerWatts)),
            MetricKind.Memory => (s is { MemoryTotalBytes: > 0, MemoryUsedBytes: >= 0 } && s.MemoryUsedBytes <= s.MemoryTotalBytes ? $"{Controls.Ui.T("内存")} {100d * s.MemoryUsedBytes / s.MemoryTotalBytes:F0}%" : Controls.Ui.T("内存", "Memory") + " —", s is { MemoryUsedBytes: >= 0 } ? $"{s.MemoryUsedBytes / 1073741824d:F1} GB" : "—"),
            MetricKind.Disk => (Controls.Ui.T("读 ", "Read ") + Rate(s?.DiskReadBytesPerSecond), Controls.Ui.T("写 ", "Write ") + Rate(s?.DiskWriteBytesPerSecond)),
            MetricKind.Network => ("↓ " + Rate(s?.NetworkReceiveBytesPerSecond), "↑ " + Rate(s?.NetworkSendBytesPerSecond)),
            _ => (Controls.Ui.T("主机估算", "PC estimate"), W(s?.EstimatedHostWatts))
        };
    }

    public void Dispose()
    {
        controller.Updated -= OnUpdate;
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= SystemThemeChanged;
        lock (lifecycle)
        {
            if (disposed) return;
            disposed = true;
            if (threadId != 0) PostThreadMessage(threadId, 0x12, IntPtr.Zero, IntPtr.Zero);
        }
        thread.Join(2000);
    }

    private delegate IntPtr WndProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    private delegate bool EnumProc(IntPtr h, IntPtr l);
    private delegate void WinEventProc(IntPtr hook, uint eventId, IntPtr hwnd, int objectId, int childId, uint eventThread, uint time);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WindowClass
    { public uint Size, Style; public IntPtr Procedure; public int ClassExtra, WindowExtra; public IntPtr Instance, Icon, Cursor, Background; public string? MenuName; public string ClassName; public IntPtr SmallIcon; }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)] private struct BlendFunction
    { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfoHeader
    { public uint Size; public int Width, Height; public ushort Planes, BitCount; public uint Compression, SizeImage; public int XPelsPerMeter, YPelsPerMeter; public uint ClrUsed, ClrImportant; }
    [StructLayout(LayoutKind.Sequential)] private struct BitmapInfo { public BitmapInfoHeader Header; public uint Colors; }
    [StructLayout(LayoutKind.Sequential)] private struct Message { public IntPtr Hwnd; public uint Id; public IntPtr WParam, LParam; public uint Time; public Point Point; public uint Private; }
    [StructLayout(LayoutKind.Sequential)] private struct PaintStruct { public IntPtr Dc; public int Erase; public Rect Paint; public int Restore, IncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct ToolInfo
    { public uint Size, Flags; public IntPtr Window; public UIntPtr Id; public Rect Rect; public IntPtr Instance, Text, Parameter, Reserved; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern ushort RegisterClassEx(ref WindowClass c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool UnregisterClass(string c, IntPtr instance);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint ex, string c, string title, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Message m, IntPtr h, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern int GetMessage(out Message m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref Message m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref Message m);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageW")] private static extern IntPtr SendToolMessage(IntPtr h, uint m, IntPtr w, ref ToolInfo info);
    [DllImport("user32.dll")] private static extern UIntPtr SetTimer(IntPtr h, UIntPtr id, uint ms, IntPtr callback);
    [DllImport("user32.dll")] private static extern bool KillTimer(IntPtr h, UIntPtr id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string? c, string? title);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr child, string? c, string? title);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] private static extern IntPtr GetParent(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr h, out Rect r);
    [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr h, ref Point p);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool ClientToScreen(IntPtr h, ref Point p);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr h, EnumProc callback, IntPtr l);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);
    [DllImport("user32.dll")] private static extern bool UnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, System.Text.StringBuilder text, int size);
    [DllImport("user32.dll")] private static extern IntPtr BeginPaint(IntPtr h, out PaintStruct p);
    [DllImport("user32.dll")] private static extern bool EndPaint(IntPtr h, ref PaintStruct p);
    [DllImport("user32.dll")] private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? module);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr destinationDc, IntPtr destination,
        ref Size size, IntPtr sourceDc, ref Point source, uint colorKey, ref BlendFunction blend, uint flags);
    [DllImport("comctl32.dll")] private static extern void InitCommonControls();
}
