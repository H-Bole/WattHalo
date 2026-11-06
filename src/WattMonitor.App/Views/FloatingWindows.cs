using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;
using WattMonitor.App.Controls;
using WattMonitor.Core;

namespace WattMonitor.App.Views;

internal sealed class OrbFace : Viewbox
{
    private readonly TextBlock value, label;
    private readonly Ellipse disc = new() { StrokeThickness = 1 };
    private readonly Ellipse outer = new() { Margin = new Thickness(4), StrokeThickness = 1.5,
        StrokeDashArray = new DoubleCollection { 38, 4, 4, 4 }, Opacity = .9 };
    private readonly Ellipse inner = new() { Margin = new Thickness(9), StrokeThickness = .8 };
    private readonly Ellipse shine = new() { Margin = new Thickness(4), IsHitTestVisible = false,
        Fill = new LinearGradientBrush(Color.FromArgb(85, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90) };

    public OrbFace(TextBlock value, TextBlock label)
    {
        this.value = value; this.label = label;
        var face = new Grid { Width = 82, Height = 82 };
        disc.SetResourceReference(Shape.StrokeProperty, "Ui.Line");
        outer.SetResourceReference(Shape.StrokeProperty, "Ui.Accent");
        inner.SetResourceReference(Shape.StrokeProperty, "Ui.Line");
        face.Children.Add(disc); face.Children.Add(outer); face.Children.Add(inner); face.Children.Add(shine);
        var contents = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        value.TextAlignment = TextAlignment.Center; label.TextAlignment = TextAlignment.Center;
        contents.Children.Add(new Viewbox { MaxWidth = 62, MaxHeight = 28, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = value });
        contents.Children.Add(new Viewbox { MaxWidth = 62, MaxHeight = 15, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = label });
        face.Children.Add(contents); Child = face;
        Apply(OrbStyle.TechRing);
    }

    public void Apply(OrbStyle style)
    {
        var ring = style == OrbStyle.TechRing;
        outer.Visibility = inner.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
        shine.Visibility = style == OrbStyle.Glass && !SystemParameters.HighContrast ? Visibility.Visible : Visibility.Collapsed;
        disc.Opacity = style == OrbStyle.Glass && !SystemParameters.HighContrast ? .72 : 1;
        disc.SetResourceReference(Shape.FillProperty, style == OrbStyle.Solid ? "Ui.Accent" : "Ui.Surface");
        value.SetResourceReference(TextBlock.ForegroundProperty, style == OrbStyle.Solid ? "Ui.OnAccent" : "Ui.Foreground");
        label.SetResourceReference(TextBlock.ForegroundProperty, style == OrbStyle.Solid ? "Ui.OnAccent" : "Ui.Muted");
    }
}

public sealed class OrbWindow : Window
{
    private readonly AppController controller;
    private readonly TextBlock value = Ui.Text("—", 22);
    private readonly TextBlock label = Ui.Text("功率", 12, Ui.Muted);
    private readonly Grid hitArea;
    private readonly OrbFace face;
    private Point start;
    private bool moved;
    internal event EventHandler? DisplayGeometryChanged;
    internal event EventHandler? EscapePressed;
    internal bool IsPointerCaptured => hitArea.IsMouseCaptured;

    public OrbWindow(AppController controller)
    {
        this.controller = controller;
        Ui.Window(this);
        Ui.Bind(this, TitleProperty, "WattHalo 悬浮监测");
        Width = Height = Math.Clamp(controller.Settings.OrbSize, 64, 160);
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Topmost = true;
        ShowInTaskbar = false;
        var area = SystemParameters.WorkArea;
        Left = controller.Settings.OrbLeft ?? area.Right - Width - 20;
        Top = controller.Settings.OrbTop ?? area.Top + 100;

        hitArea = new Grid { Margin = new Thickness(3), Background = Brushes.Transparent, Focusable = true,
            ToolTip = "点击展开快速监测；拖动调整位置；右键切换指标。" };
        value.SetValue(System.Windows.Documents.Typography.NumeralAlignmentProperty, FontNumeralAlignment.Tabular);
        face = new OrbFace(value, label);
        hitArea.Children.Add(face);
        Content = hitArea;

        var menu = new ContextMenu();
        var metrics = new MenuItem(); Ui.Bind(metrics, HeaderedItemsControl.HeaderProperty, "显示指标");
        foreach (var kind in Enum.GetValues<MetricKind>())
        {
            var item = new MenuItem { IsCheckable = true, Tag = kind };
            Ui.Bind(item, HeaderedItemsControl.HeaderProperty, Ui.NameKey(kind));
            item.Click += (_, _) => controller.QueueSettings(current => current with { OrbMetric = kind });
            metrics.Items.Add(item);
        }
        menu.Opened += (_, _) =>
        {
            foreach (MenuItem item in metrics.Items) item.IsChecked = item.Tag is MetricKind kind && kind == controller.Settings.OrbMetric;
        };
        menu.Items.Add(metrics);
        var open = new MenuItem(); Ui.Bind(open, HeaderedItemsControl.HeaderProperty, "打开 WattHalo");
        open.Click += (_, _) => controller.ShowMain();
        menu.Items.Add(open);
        var hide = new MenuItem(); Ui.Bind(hide, HeaderedItemsControl.HeaderProperty, "隐藏悬浮圆环");
        hide.Click += (_, _) => controller.QueueSettings(current => current with { ShowOrb = false });
        menu.Items.Add(hide);
        hitArea.ContextMenu = menu;

        hitArea.MouseLeftButtonDown += (_, e) =>
        {
            start = FloatingBounds.CursorPosition() ?? PointToScreen(e.GetPosition(this));
            moved = false;
            hitArea.Focus();
            hitArea.CaptureMouse();
            e.Handled = true;
        };
        hitArea.MouseMove += (_, e) =>
        {
            if (!hitArea.IsMouseCaptured || e.LeftButton != MouseButtonState.Pressed) return;
            if (FloatingBounds.CursorPosition() is not { } now || !FloatingBounds.TryGetBounds(this, out var bounds)) return;
            var dpi = VisualTreeHelper.GetDpi(this);
            var dx = now.X - start.X;
            var dy = now.Y - start.Y;
            if (!moved && Math.Abs(dx) < SystemParameters.MinimumHorizontalDragDistance * dpi.DpiScaleX &&
                Math.Abs(dy) < SystemParameters.MinimumVerticalDragDistance * dpi.DpiScaleY) return;
            moved = true;
            FloatingBounds.Move(this, (int)Math.Round(bounds.Left + dx), (int)Math.Round(bounds.Top + dy));
            start = now;
        };
        hitArea.MouseLeftButtonUp += (_, e) =>
        {
            if (!hitArea.IsMouseCaptured) return;
            var dragged = moved;
            if (dragged) { moved = false; Snap(); }
            hitArea.ReleaseMouseCapture();
            if (!dragged) controller.TogglePanel(true);
            e.Handled = true;
        };
        hitArea.LostMouseCapture += (_, _) =>
        {
            if (moved) { moved = false; Snap(); }
        };
        hitArea.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Space or Key.Enter) { controller.TogglePanel(); e.Handled = true; }
            else if (e.Key == Key.Escape && EscapePressed is not null) { EscapePressed.Invoke(this, EventArgs.Empty); e.Handled = true; }
        };
        Ui.Bind(hitArea, System.Windows.Automation.AutomationProperties.NameProperty, "WattHalo 悬浮监测");
        Ui.Bind(hitArea, System.Windows.Automation.AutomationProperties.HelpTextProperty, "按 Enter 展开快速监测，右键切换显示指标。");
        SourceInitialized += (_, _) => { FloatingBounds.MakeToolWindow(this); Dispatcher.BeginInvoke(CorrectPosition); };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        controller.Updated += Updated;
        Ui.LanguageChanged += Updated;
        Closed += (_, _) => { controller.Updated -= Updated; Ui.LanguageChanged -= Updated; SystemEvents.DisplaySettingsChanged -= DisplayChanged; };
        IsVisibleChanged += (_, _) => { if (IsVisible) { CorrectPosition(); Updated(this, EventArgs.Empty); } };
    }

    public void RestoreKeyboardFocus() { if (IsVisible) hitArea.Focus(); }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        DisplayGeometryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Snap()
    {
        if (FloatingBounds.TryGetBounds(this, out var bounds))
        {
            var area = FloatingBounds.WorkArea(bounds);
            var scale = FloatingBounds.Scale(this);
            var x = Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - bounds.Width));
            var y = Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - bounds.Height));
            if (Math.Abs(x - area.Left) < 36 * scale) x = area.Left + (int)Math.Round(4 * scale);
            else if (Math.Abs(area.Right - x - bounds.Width) < 36 * scale) x = area.Right - bounds.Width - (int)Math.Round(4 * scale);
            x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - bounds.Width));
            FloatingBounds.Move(this, x, y);
        }
        var left = Left; var top = Top;
        controller.QueueSettings(current => current with { OrbLeft = left, OrbTop = top });
    }

    private void CorrectPosition()
    {
        if (FloatingBounds.Constrain(this))
        {
            var left = Left; var top = Top;
            controller.QueueSettings(current => current with { OrbLeft = left, OrbTop = top });
        }
    }

    private void DisplayChanged(object? sender, EventArgs e) { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(CorrectPosition); }

    private void Updated(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Updated(sender, e)); return; }
        if (!IsVisible) return;
        var size = Math.Clamp(controller.Settings.OrbSize, 64, 160);
        if (Width != size)
        {
            Width = Height = size;
            Dispatcher.BeginInvoke(() => { CorrectPosition(); DisplayGeometryChanged?.Invoke(this, EventArgs.Empty); }, System.Windows.Threading.DispatcherPriority.Loaded);
        }
        face.Apply(controller.Settings.OrbStyle);
        var kind = controller.Settings.OrbMetric;
        var number = Ui.Value(controller.Snapshot, kind);
        if (controller.MonitorStatus.IsStale || number is not { } n || !double.IsFinite(n) || n < 0 ||
            kind is MetricKind.Cpu or MetricKind.Gpu or MetricKind.Memory && n > 100) number = null;
        value.Text = number.HasValue ? kind is MetricKind.Network or MetricKind.Disk ? Ui.Bytes(number) : number.Value.ToString("0") : "—";
        value.FontSize = value.Text.Length > 7 ? 15 : value.Text.Length > 5 ? 18 : 22;
        label.Text = controller.MonitorStatus.StorageError is not null ? Ui.T("记录失败") : controller.MonitorStatus.IsStale ? Ui.T("已过期") : kind switch
        {
            MetricKind.Power => Ui.T("W · 估算"),
            MetricKind.Disk or MetricKind.Network => Ui.Name(kind) + "/s",
            _ => Ui.Name(kind) + " %"
        };
        hitArea.ToolTip = Ui.Diagnostic(controller.Status) + "\n" + Ui.Metric(controller.Snapshot, kind) +
            (controller.MonitorStatus.IsStale && controller.MonitorStatus.LastSample is { } last ? $"\n{Ui.T("最后更新", "Last updated")} {last.ToLocalTime():HH:mm:ss}" : "") +
            "\n" + Ui.T("点击展开快速监测；拖动调整位置；右键切换指标。");
    }
}

public sealed class PanelWindow : Window
{
    private readonly AppController controller;
    private readonly DashboardView dashboard;
    private readonly Border surface;
    private readonly Button pin;
    private readonly TranslateTransform entrance = new();
    private OrbWindow? anchor;
    private bool opening;
    private bool contextMenuOpen;
    private int dismissGeneration;
    private IInputElement? previousFocus;

    public PanelWindow(AppController controller)
    {
        this.controller = controller;
        Ui.Window(this);
        Title = "WattHalo";
        Width = 470; Height = 620; MinWidth = 400; MinHeight = 360;
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; Topmost = true; ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Focusable = true;
        surface = new Border { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), Padding = new Thickness(18), RenderTransform = entrance };
        surface.SetResourceReference(Border.BackgroundProperty, "Ui.Surface");
        surface.SetResourceReference(Border.BorderBrushProperty, "Ui.Line");
        var root = new DockPanel();
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        pin = Ui.Button("固定", () => controller.QueueSettings(current => current with { PanelPinned = !current.PanelPinned }));
        Ui.Bind(pin, ToolTipProperty, "固定后点击其他窗口不会自动收起。");
        controls.Children.Add(pin);
        var open = Ui.Button("打开主窗口", () => controller.ShowMain(controller.Settings.PanelSelectedTab ?? controller.Settings.SelectedTab));
        controls.Children.Add(open);
        var close = Ui.Button("×", () => ClosePanel(true));
        Ui.Bind(close, ToolTipProperty, "收起快速监测");
        Ui.Bind(close, System.Windows.Automation.AutomationProperties.NameProperty, "收起快速监测");
        controls.Children.Add(close);
        DockPanel.SetDock(controls, Dock.Right);
        top.Children.Add(controls);
        top.Children.Add(Ui.Text("快速监测", 19));
        DockPanel.SetDock(top, Dock.Top);
        root.Children.Add(top);
        dashboard = new DashboardView(controller, true);
        root.Children.Add(new ScrollViewer { Content = dashboard, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        surface.Child = root;
        var overlay = new Grid();
        overlay.Children.Add(surface);
        overlay.Children.Add(new ToastHost(controller));
        Content = overlay;
        Closing += (_, e) => { if (!Dispatcher.HasShutdownStarted) { e.Cancel = true; ClosePanel(false); } };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) { ClosePanel(true); e.Handled = true; } };
        Deactivated += (_, _) => ScheduleDismissCheck();
        AddHandler(ContextMenuService.ContextMenuOpeningEvent, new ContextMenuEventHandler((_, _) => contextMenuOpen = true), true);
        AddHandler(ContextMenuService.ContextMenuClosingEvent, new ContextMenuEventHandler((_, _) => { contextMenuOpen = false; ScheduleDismissCheck(); }), true);
        dashboard.TransientClosed += ScheduleDismissCheck;
        SourceInitialized += (_, _) => { FloatingBounds.MakeToolWindow(this); CorrectPosition(); };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        controller.Updated += Updated;
        Ui.LanguageChanged += Updated;
        Closed += (_, _) => { DetachAnchor(); controller.Updated -= Updated; Ui.LanguageChanged -= Updated; SystemEvents.DisplaySettingsChanged -= DisplayChanged; };
        IsVisibleChanged += (_, _) => { if (IsVisible) { CorrectPosition(); dashboard.Refresh(true); } };
    }

    public void ShowNear(OrbWindow? orb, bool animate = false)
    {
        DetachAnchor();
        anchor = orb;
        if (anchor is not null)
        {
            anchor.MouseLeave += AnchorMouseLeave;
            anchor.LocationChanged += AnchorMoved;
            anchor.DisplayGeometryChanged += AnchorMoved;
            anchor.EscapePressed += AnchorEscape;
            new WindowInteropHelper(anchor).EnsureHandle();
        }
        previousFocus = Keyboard.FocusedElement;
        opening = true;
        new WindowInteropHelper(this).EnsureHandle();
        PositionNearAnchor();
        var useMotion = animate && !controller.Settings.ReducedMotion && SystemParameters.ClientAreaAnimation;
        Opacity = useMotion ? 0 : 1;
        entrance.Y = useMotion ? 6 : 0;
        Show();
        if (useMotion)
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
            entrance.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(160)) { EasingFunction = easing });
        }
        Dispatcher.BeginInvoke(() => { opening = false; ScheduleDismissCheck(); }, System.Windows.Threading.DispatcherPriority.ContextIdle);
        Updated(this, EventArgs.Empty);
    }

    public void Dismiss(bool restoreFocus = false) => ClosePanel(restoreFocus);

    private void AnchorMouseLeave(object sender, MouseEventArgs e) => ScheduleDismissCheck();
    private void AnchorMoved(object? sender, EventArgs e)
    {
        dismissGeneration++;
        if (IsVisible) PositionNearAnchor();
    }
    private void AnchorEscape(object? sender, EventArgs e) { if (IsVisible) ClosePanel(true); }

    private void ScheduleDismissCheck()
    {
        var generation = dismissGeneration;
        Dispatcher.BeginInvoke(() =>
        {
            if (generation != dismissGeneration || !IsVisible || IsActive || opening || controller.Settings.PanelPinned || contextMenuOpen || anchor?.IsMouseOver == true ||
                anchor?.IsPointerCaptured == true || HasOpenPopup(this) || OwnModalIsActive()) return;
            ClosePanel(false);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    private void DetachAnchor()
    {
        dismissGeneration++;
        if (anchor is null) return;
        anchor.MouseLeave -= AnchorMouseLeave;
        anchor.LocationChanged -= AnchorMoved;
        anchor.DisplayGeometryChanged -= AnchorMoved;
        anchor.EscapePressed -= AnchorEscape;
        anchor = null;
    }

    private void ClosePanel(bool restoreFocus)
    {
        BeginAnimation(OpacityProperty, null);
        entrance.BeginAnimation(TranslateTransform.YProperty, null);
        Opacity = 1; entrance.Y = 0;
        Hide();
        controller.PanelVisibilityChanged();
        if (restoreFocus)
        {
            if (anchor?.IsVisible == true) anchor.RestoreKeyboardFocus();
            else if (previousFocus is UIElement element && element.IsVisible) element.Focus();
        }
        DetachAnchor();
    }

    private void CorrectPosition()
    {
        PositionNearAnchor();
    }

    private void PositionNearAnchor()
    {
        var anchorBounds = System.Drawing.Rectangle.Empty;
        var anchored = anchor is not null && FloatingBounds.TryGetBounds(anchor, out anchorBounds);
        var bounds = anchored ? anchorBounds : System.Windows.Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var area = anchored ? FloatingBounds.WorkArea(bounds) : bounds;
        var scale = FloatingBounds.Scale(anchored ? anchor! : this);
        var availableWidth = area.Width / scale;
        var availableHeight = area.Height / scale;
        MinWidth = Math.Min(400, availableWidth); MinHeight = Math.Min(360, availableHeight);
        Width = Math.Min(470, availableWidth); Height = Math.Min(620, availableHeight);
        var panelWidth = (int)Math.Ceiling(Width * scale);
        var panelHeight = (int)Math.Ceiling(Height * scale);
        var gap = (int)Math.Round(10 * scale);
        var x = anchored
            ? bounds.Left - panelWidth - gap >= area.Left ? bounds.Left - panelWidth - gap : bounds.Right + gap
            : area.Right - panelWidth - (int)Math.Round(16 * scale);
        var y = anchored ? bounds.Top - (int)Math.Round(12 * scale) : area.Top + (int)Math.Round(24 * scale);
        x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - panelWidth));
        y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - panelHeight));
        FloatingBounds.Move(this, x, y);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        if (IsVisible) Dispatcher.BeginInvoke(PositionNearAnchor);
    }

    private void DisplayChanged(object? sender, EventArgs e) { if (!Dispatcher.HasShutdownStarted) Dispatcher.BeginInvoke(CorrectPosition); }
    private void Updated(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Updated(sender, e)); return; }
        pin.Content = controller.Settings.PanelPinned ? Ui.T("已固定") : Ui.T("固定");
        if (IsVisible) dashboard.Refresh();
    }

    private static bool HasOpenPopup(DependencyObject root)
    {
        if (root is ComboBox { IsDropDownOpen: true } || root is DatePicker { IsDropDownOpen: true } || root is Popup { IsOpen: true }) return true;
        if (ContextMenuService.GetContextMenu(root)?.IsOpen == true) return true;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++) if (HasOpenPopup(VisualTreeHelper.GetChild(root, i))) return true;
        return false;
    }

    private bool OwnModalIsActive()
    {
        if (Application.Current.Windows.OfType<Window>().Any(window => window.IsActive && window.Owner == this)) return true;
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero) return false;
        GetWindowThreadProcessId(foreground, out var pid);
        if (pid != Environment.ProcessId) return false;
        var name = new System.Text.StringBuilder(64);
        GetClassName(foreground, name, name.Capacity);
        return name.ToString() == "#32770";
    }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out int pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);
}

internal static class FloatingBounds
{
    internal static bool TryGetBounds(Window window, out System.Drawing.Rectangle bounds)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero && GetWindowRect(handle, out var rectangle))
        {
            bounds = System.Drawing.Rectangle.FromLTRB(rectangle.Left, rectangle.Top, rectangle.Right, rectangle.Bottom);
            return bounds.Width > 0 && bounds.Height > 0;
        }
        bounds = System.Drawing.Rectangle.Empty;
        return false;
    }

    internal static System.Drawing.Rectangle WorkArea(System.Drawing.Rectangle bounds) => System.Windows.Forms.Screen.FromRectangle(bounds).WorkingArea;

    internal static double Scale(Window window)
    {
        var dpi = GetDpiForWindow(new WindowInteropHelper(window).Handle);
        return dpi == 0 ? 1 : dpi / 96d;
    }

    internal static Point? CursorPosition() => GetCursorPos(out var point) ? new Point(point.X, point.Y) : null;

    internal static void Move(Window window, int x, int y)
    {
        if (!TryGetBounds(window, out var bounds) || (bounds.Left == x && bounds.Top == y)) return;
        SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero, x, y, 0, 0, 0x0015);
    }

    internal static bool Constrain(Window window)
    {
        if (!TryGetBounds(window, out var bounds)) return false;
        var area = WorkArea(bounds);
        var x = Math.Clamp(bounds.Left, area.Left, Math.Max(area.Left, area.Right - bounds.Width));
        var y = Math.Clamp(bounds.Top, area.Top, Math.Max(area.Top, area.Bottom - bounds.Height));
        if (x == bounds.Left && y == bounds.Top) return false;
        Move(window, x, y);
        return true;
    }

    internal static void MakeToolWindow(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var style = GetWindowLongPtr(handle, -20).ToInt64();
        SetWindowLongPtr(handle, -20, new IntPtr((style | 0x80) & ~0x40000));
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct NativePoint { public int X, Y; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out NativeRect rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out NativePoint point);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
