using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using WattMonitor.Core;

namespace WattMonitor.App.Controls;

internal static class Ui
{
    private static readonly ConcurrentDictionary<string, Brush> brushes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly string[] semanticKeys = ["Background", "Surface", "Foreground", "Muted", "Accent", "Line", "OnAccent"];
    private static ThemePreference preference = ThemePreference.Dark;
    private static bool watchingSystem;
    private static bool isDark = true;
    private static ImageSource? icon;

    public static event EventHandler? ThemeChanged;
    public static event EventHandler? LanguageChanged { add => Localization.Changed += value; remove => Localization.Changed -= value; }
    public static bool IsEnglish => Localization.IsEnglish;
    public static string T(string chinese) => Localization.Text(chinese);
    public static string T(string chinese, string english) => Localization.Text(chinese, english);
    public static string Diagnostic(string message) => Localization.Diagnostic(message);
    public static void Bind(DependencyObject target, DependencyProperty property, string chinese) => Localization.Bind(target, property, chinese);
    public static void ApplyLanguage(LanguagePreference language) => Localization.Apply(language);
    public static bool IsDark => isDark;
    public static Brush Background => ResourceBrush("Background", "#151718");
    public static Brush Surface => ResourceBrush("Surface", "#202324");
    public static Brush Foreground => ResourceBrush("Foreground", "#F1F3F2");
    public static Brush Muted => ResourceBrush("Muted", "#B3BBB8");
    public static Brush Accent => ResourceBrush("Accent", "#6DE0B7");
    public static Brush Line => ResourceBrush("Line", "#404746");
    public static Brush Brush(string color) => brushes.GetOrAdd(color, static key => { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(key)); b.Freeze(); return b; });
    private static Brush ResourceBrush(string key, string fallback) => Application.Current?.TryFindResource("Ui." + key) as Brush ?? Brush(fallback);

    public static void ApplyTheme(ThemePreference theme)
    {
        var app = Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess()) { app.Dispatcher.Invoke(() => ApplyTheme(theme)); return; }
        preference = theme;
        if (!watchingSystem)
        {
            SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
            SystemParameters.StaticPropertyChanged += OnSystemParametersChanged;
            watchingSystem = true;
        }
#pragma warning disable WPF0001 // WPF's built-in Fluent theme selection is still marked experimental in .NET 10.
        app.ThemeMode = theme switch { ThemePreference.Light => ThemeMode.Light, ThemePreference.System => ThemeMode.System, _ => ThemeMode.Dark };
#pragma warning restore WPF0001
        UpdatePalette();
    }

    private static void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) => QueuePaletteUpdate();
    private static void OnSystemParametersChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SystemParameters.HighContrast)) QueuePaletteUpdate();
    }
    private static void QueuePaletteUpdate()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(UpdatePalette));
    }
    private static bool SystemUsesDarkTheme()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int light && light == 0; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException) { return false; }
    }
    private static Brush SystemBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static void UpdatePalette()
    {
        var app = Application.Current;
        if (app is null) return;
        var highContrast = SystemParameters.HighContrast;
        isDark = highContrast
            ? (SystemColors.WindowColor.R * 299 + SystemColors.WindowColor.G * 587 + SystemColors.WindowColor.B * 114) < 128000
            : preference == ThemePreference.Dark || (preference == ThemePreference.System && SystemUsesDarkTheme());
        var colors = highContrast
            ? new[] { SystemBrush(SystemColors.WindowColor), SystemBrush(SystemColors.WindowColor), SystemBrush(SystemColors.WindowTextColor), SystemBrush(SystemColors.WindowTextColor), SystemBrush(SystemColors.HighlightColor), SystemBrush(SystemColors.WindowTextColor), SystemBrush(SystemColors.HighlightTextColor) }
            : isDark
                ? new[] { Brush("#151718"), Brush("#202324"), Brush("#F1F3F2"), Brush("#B3BBB8"), Brush("#6DE0B7"), Brush("#404746"), Brush("#102019") }
                : new[] { Brush("#F7F8F7"), Brush("#FFFFFF"), Brush("#1E2723"), Brush("#52635C"), Brush("#087D60"), Brush("#C9D0CD"), Brush("#FFFFFF") };
        for (var i = 0; i < semanticKeys.Length; i++) app.Resources["Ui." + semanticKeys[i]] = colors[i];
        app.Resources["Ui.Focus"] = highContrast ? SystemBrush(SystemColors.HighlightColor) : colors[4];
        app.Resources["Ui.ControlBorder"] = highContrast ? SystemBrush(SystemColors.WindowTextColor) : isDark ? Brush("#81918C") : Brush("#71817B");
        app.Resources["Ui.DisabledText"] = highContrast ? SystemBrush(SystemColors.GrayTextColor) : isDark ? Brush("#89918E") : Brush("#707A76");
        app.Resources["Ui.DisabledBackground"] = highContrast ? SystemBrush(SystemColors.WindowColor) : isDark ? Brush("#2B2F30") : Brush("#E8ECEA");
        app.Resources["Ui.Danger"] = highContrast ? SystemBrush(SystemColors.HighlightColor) : isDark ? Brush("#F08B84") : Brush("#B22E31");
        app.Resources["Ui.OnDanger"] = highContrast ? SystemBrush(SystemColors.HighlightTextColor) : isDark ? Brush("#2B1615") : Brush("#FFFFFF");
        app.Resources["Ui.NavSelected"] = highContrast ? SystemBrush(SystemColors.HighlightColor) : isDark ? Brush("#2C3431") : Brush("#E3F0EA");
        // Keep Fluent's own contrast-theme resources when Windows high contrast is active.
        string[] accentKeys = ["ComboBoxItemPillFillBrush", "TextControlBorderBrushFocused", "CalendarViewTodayBackground", "CalendarViewSelectedBorderBrush",
            "CheckBoxCheckBackgroundFillChecked", "CheckBoxCheckBackgroundFillCheckedPointerOver", "CheckBoxCheckBackgroundFillCheckedPressed",
            "CheckBoxCheckBackgroundFillIndeterminate", "CheckBoxCheckBackgroundFillIndeterminatePointerOver", "CheckBoxCheckBackgroundFillIndeterminatePressed",
            "CheckBoxCheckBackgroundStrokeChecked", "CheckBoxCheckBackgroundStrokeCheckedPointerOver", "CheckBoxCheckBackgroundStrokeCheckedPressed",
            "CheckBoxCheckBackgroundStrokeIndeterminate", "CheckBoxCheckBackgroundStrokeIndeterminatePointerOver", "CheckBoxCheckBackgroundStrokeIndeterminatePressed"];
        foreach (var key in accentKeys) { if (highContrast) app.Resources.Remove(key); else app.Resources[key] = colors[4]; }
        foreach (var key in new[] { "CalendarViewTodayForeground", "CheckBoxCheckGlyphForeground", "CheckBoxCheckGlyphForegroundPressed" })
        { if (highContrast) app.Resources.Remove(key); else app.Resources[key] = colors[6]; }
        foreach (Window window in app.Windows) ApplyCaption(window);
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static void ApplyCaption(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        var dark = isDark && !SystemParameters.HighContrast ? 1 : 0;
        if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        // Explicit caption colors keep a chosen app theme independent of Windows' own app mode.
        var caption = SystemParameters.HighContrast ? -1 : isDark ? 0x181715 : 0xF7F8F7;
        var text = SystemParameters.HighContrast ? -1 : isDark ? 0xF2F3F1 : 0x23271E;
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
        DwmSetWindowAttribute(handle, 36, ref text, sizeof(int));
    }
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);

    private static void SetSemanticColor(FrameworkElement element, System.Windows.DependencyProperty property, Brush? color, string defaultKey)
    {
        var current = Application.Current;
        var key = color is null ? defaultKey : semanticKeys.FirstOrDefault(k => ReferenceEquals(current?.TryFindResource("Ui." + k), color));
        if (key is not null) element.SetResourceReference(property, "Ui." + key);
        else element.SetValue(property, color);
    }
    public static TextBlock Text(string value, double size = 14, Brush? color = null)
    {
        var text = new TextBlock { FontSize = size, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 3) };
        Bind(text, TextBlock.TextProperty, value);
        SetSemanticColor(text, TextBlock.ForegroundProperty, color, "Foreground");
        return text;
    }
    public static Button Button(string label, Action action, bool accent = false)
    {
        var button = new Button { Padding = new Thickness(14, 9, 14, 9), Margin = new Thickness(0, 4, 8, 4), MinHeight = 44, Cursor = System.Windows.Input.Cursors.Hand };
        Bind(button, ContentControl.ContentProperty, label);
        button.SetResourceReference(FrameworkElement.StyleProperty, "Ui.Button");
        button.SetResourceReference(Control.BackgroundProperty, accent ? "Ui.Accent" : "Ui.Surface");
        button.SetResourceReference(Control.ForegroundProperty, accent ? "Ui.OnAccent" : "Ui.Foreground");
        button.SetResourceReference(Control.BorderBrushProperty, accent ? "Ui.Accent" : "Ui.ControlBorder");
        Bind(button, System.Windows.Automation.AutomationProperties.NameProperty, label);
        button.Click += (_, _) => action(); return button;
    }
    public static Border Card(UIElement child)
    {
        var card = new Border { Child = child, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(18), Margin = new Thickness(0, 8, 0, 8) };
        card.SetResourceReference(Border.BackgroundProperty, "Ui.Surface");
        card.SetResourceReference(Border.BorderBrushProperty, "Ui.Line");
        return card;
    }
    public static MessageBoxResult Confirm(string message, string title, MessageBoxButton buttons, MessageBoxImage image, MessageBoxResult defaultResult)
    {
        var isUnsaved = title.Contains("未保存的修改", StringComparison.Ordinal);
        var isForce = title.Contains("强制结束", StringComparison.Ordinal);
        title = T(title); message = T(message);
        var app = Application.Current;
        if (app is null || app.Dispatcher.HasShutdownStarted)
            return MessageBox.Show(message, title, buttons, image, defaultResult);
        if (!app.Dispatcher.CheckAccess())
            return app.Dispatcher.Invoke(() => Confirm(message, title, buttons, image, defaultResult));

        var choices = buttons switch
        {
            MessageBoxButton.OK => new[] { MessageBoxResult.OK },
            MessageBoxButton.OKCancel => new[] { MessageBoxResult.OK, MessageBoxResult.Cancel },
            MessageBoxButton.YesNo => new[] { MessageBoxResult.Yes, MessageBoxResult.No },
            MessageBoxButton.YesNoCancel => new[] { MessageBoxResult.Yes, MessageBoxResult.No, MessageBoxResult.Cancel },
            _ => throw new ArgumentOutOfRangeException(nameof(buttons))
        };
        var cancelled = choices.Contains(MessageBoxResult.Cancel) ? MessageBoxResult.Cancel : choices.Contains(MessageBoxResult.No) ? MessageBoxResult.No : MessageBoxResult.OK;
        var initial = choices.Contains(defaultResult) ? defaultResult : cancelled;
        var result = cancelled;
        var owner = app.Windows.OfType<Window>()
            .Where(w => w.IsVisible && (w is Views.MainWindow || w is Views.PanelWindow))
            .OrderByDescending(w => w.IsActive).FirstOrDefault();
        var dialog = new Window
        {
            Title = title, Width = Math.Min(520, Math.Max(320, SystemParameters.WorkArea.Width - 40)),
            MaxHeight = Math.Max(200, SystemParameters.WorkArea.Height - 40), SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            WindowStartupLocation = owner is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Topmost = owner?.Topmost == true
        };
        if (owner is not null) dialog.Owner = owner;
        Window(dialog);
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var content = new DockPanel { Margin = new Thickness(0, 0, 0, 22) };
        var iconPath = DialogIcon(image);
        if (iconPath is not null) { DockPanel.SetDock(iconPath, Dock.Left); content.Children.Add(iconPath); }
        var body = new StackPanel();
        body.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        body.Children.Add(new TextBlock { Text = message, FontSize = 14, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(body);
        root.Children.Add(new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxHeight = Math.Max(80, SystemParameters.WorkArea.Height - 220) });
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(actions, 1);
        Button? initialButton = null;
        foreach (var choice in choices)
        {
            var label = choice switch
            {
                MessageBoxResult.OK => "确定",
                MessageBoxResult.Cancel => "取消",
                MessageBoxResult.Yes when isUnsaved => "保存",
                MessageBoxResult.No when isUnsaved => "不保存",
                MessageBoxResult.Yes when isForce => "强制结束",
                MessageBoxResult.Yes => "是",
                _ => "否"
            };
            var localChoice = choice;
            var primary = choice == MessageBoxResult.OK || (choice == MessageBoxResult.Yes && !isForce && (isUnsaved || initial == MessageBoxResult.Yes));
            var button = Button(label, () => { result = localChoice; dialog.Close(); }, primary);
            button.MinWidth = 88;
            button.Margin = new Thickness(8, 0, 0, 0);
            button.IsDefault = choice == initial;
            button.IsCancel = choice == cancelled;
            if (isForce && choice == MessageBoxResult.Yes)
            {
                button.SetResourceReference(Control.BackgroundProperty, "Ui.Danger");
                button.SetResourceReference(Control.ForegroundProperty, "Ui.OnDanger");
                button.SetResourceReference(Control.BorderBrushProperty, "Ui.Danger");
            }
            if (choice == initial) initialButton = button;
            actions.Children.Add(button);
        }
        root.Children.Add(actions);
        dialog.Content = root;
        dialog.ContentRendered += (_, _) => initialButton?.Focus();
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape) { result = cancelled; dialog.Close(); e.Handled = true; }
        };
        try { dialog.ShowDialog(); }
        catch (InvalidOperationException) { return MessageBox.Show(message, title, buttons, image, defaultResult); }
        return result;
    }

    private static System.Windows.Shapes.Path? DialogIcon(MessageBoxImage image)
    {
        var geometry = image switch
        {
            MessageBoxImage.Warning => "M 16,3 L 29,27 L 3,27 Z M 16,11 L 16,19 M 16,23 L 16,24",
            MessageBoxImage.Error => "M 16,3 A 13,13 0 1 1 15.99,3 M 11,11 L 21,21 M 21,11 L 11,21",
            MessageBoxImage.Question => "M 16,3 A 13,13 0 1 1 15.99,3 M 12,12 C 12,7 21,7 21,12 C 21,15 16,15 16,19 M 16,23 L 16,24",
            MessageBoxImage.Information => "M 16,3 A 13,13 0 1 1 15.99,3 M 16,14 L 16,23 M 16,9 L 16,10",
            _ => null
        };
        if (geometry is null) return null;
        var icon = new System.Windows.Shapes.Path { Data = Geometry.Parse(geometry), Width = 32, Height = 32,
            Stretch = Stretch.Uniform, StrokeThickness = 1.8, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Margin = new Thickness(0, 1, 16, 0), VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
        icon.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, image is MessageBoxImage.Warning or MessageBoxImage.Error ? "Ui.Danger" : "Ui.Accent");
        return icon;
    }

    public static string NameKey(MetricKind kind) => kind switch { MetricKind.Power => "功率", MetricKind.Cpu => "CPU", MetricKind.Gpu => "GPU", MetricKind.Memory => "内存", MetricKind.Disk => "磁盘", _ => "网络" };
    public static string Name(MetricKind kind) => T(NameKey(kind));
    public static string Number(double? value, string unit = "", string format = "0.0") => value.HasValue && double.IsFinite(value.Value) ? value.Value.ToString(format, CultureInfo.CurrentCulture) + unit : T("— 不可用");
    public static string Bytes(double? value) => value switch
    {
        null => T("— 不可用"),
        < 1024 => Number(value, " B", "0"),
        < 1048576 => Number(value / 1024, " KB", "0.#"),
        < 1073741824 => Number(value / 1048576, " MB", "0.#"),
        _ => Number(value / 1073741824, " GB", "0.#")
    };
    public static double? Value(TelemetrySnapshot? s, MetricKind kind, string? gpu = null) => s is null ? null : kind switch
    {
        MetricKind.Power => s.EstimatedHostWatts,
        MetricKind.Cpu => s.CpuPercent,
        MetricKind.Gpu => (gpu is null ? s.Gpus.FirstOrDefault() : s.Gpus.FirstOrDefault(g => g.Id == gpu))?.UtilizationPercent,
        MetricKind.Memory => s.MemoryTotalBytes > 0 ? 100d * s.MemoryUsedBytes / s.MemoryTotalBytes : null,
        MetricKind.Disk => Sum(s.DiskReadBytesPerSecond, s.DiskWriteBytesPerSecond),
        _ => Sum(s.NetworkReceiveBytesPerSecond, s.NetworkSendBytesPerSecond)
    };
    private static double? Sum(double? a, double? b) => a.HasValue && b.HasValue ? a + b : null;
    public static string Metric(TelemetrySnapshot? snapshot, MetricKind kind)
    {
        var value = Value(snapshot, kind);
        return kind is MetricKind.Disk or MetricKind.Network
            ? value.HasValue ? Bytes(value) + "/s" : T("— 不可用")
            : Number(value, kind == MetricKind.Power ? " W" : " %");
    }
    public static void Window(Window window)
    {
        window.SetResourceReference(Control.BackgroundProperty, "Ui.Background");
        window.SetResourceReference(Control.ForegroundProperty, "Ui.Foreground");
        window.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        window.FontSize = 14;
        window.SourceInitialized += (_, _) => ApplyCaption(window);
        ApplyCaption(window);
        if (icon is null)
        {
            var uri = new Uri("pack://application:,,,/WattMonitor.App;component/Assets/WattMonitor.ico", UriKind.Absolute);
            var decoder = BitmapDecoder.Create(uri, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            icon = decoder.Frames.OrderByDescending(frame => frame.PixelWidth).First();
            if (icon.CanFreeze) icon.Freeze();
        }
        window.Icon = icon;
    }
}

internal sealed class TrendChart : FrameworkElement
{
    private double?[] values = [];
    public void SetValues(IEnumerable<double?> source) { values = source.ToArray(); if (IsVisible) InvalidateVisual(); }
    public TrendChart()
    {
        Height = 150; MinWidth = 100;
        Ui.Bind(this, ToolTipProperty, "最近采集趋势；断线表示该时段读数不可用。纵轴按当前范围缩放。");
        Loaded += (_, _) => Ui.ThemeChanged += OnThemeChanged;
        Unloaded += (_, _) => Ui.ThemeChanged -= OnThemeChanged;
        Loaded += (_, _) => Ui.LanguageChanged += OnThemeChanged;
        Unloaded += (_, _) => Ui.LanguageChanged -= OnThemeChanged;
    }
    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (!IsVisible || ActualWidth < 2) return;
        var width = ActualWidth; var height = ActualHeight - 20;
        for (var i = 0; i < 4; i++) dc.DrawLine(new Pen(Ui.Line, 1), new Point(0, i * height / 3), new Point(width, i * height / 3));
        double? maximum = null;
        foreach (var sample in values) if (sample.HasValue && double.IsFinite(sample.Value)) maximum = Math.Max(maximum ?? sample.Value, sample.Value);
        if (!maximum.HasValue) { DrawText(dc, Ui.T("等待可用采样"), new Point(8, height / 2)); return; }
        var max = Math.Max(maximum.Value * 1.1, 1); var pen = new Pen(Ui.Accent, 2); Point? previous = null;
        var pixels = Math.Max(1, (int)Math.Ceiling(width * VisualTreeHelper.GetDpi(this).DpiScaleX));
        // One bucket per physical pixel. Preserve first/last and both extrema;
        // flush at every missing value so aggregation never bridges a gap.
        var bucket = -1; double first = 0, last = 0, low = 0, high = 0;
        double Y(double value) => height - Math.Clamp(value / max, 0, 1) * height;
        void Flush()
        {
            if (bucket < 0) return;
            var x = bucket * width / pixels; var head = new Point(x, Y(first));
            if (previous.HasValue) dc.DrawLine(pen, previous.Value, head);
            else dc.DrawEllipse(Ui.Accent, null, head, 1.5, 1.5);
            if (high != low) dc.DrawLine(pen, new Point(x, Y(low)), new Point(x, Y(high)));
            previous = new Point(x, Y(last)); bucket = -1;
        }
        for (var i = 0; i < values.Length; i++)
        {
            if (!values[i].HasValue || !double.IsFinite(values[i]!.Value)) { Flush(); previous = null; continue; }
            var next = Math.Min(pixels, (int)(i * (double)pixels / Math.Max(1, values.Length - 1))); var sample = values[i]!.Value;
            if (next != bucket) { Flush(); bucket = next; first = last = low = high = sample; }
            else { last = sample; low = Math.Min(low, sample); high = Math.Max(high, sample); }
        }
        Flush();
        DrawText(dc, string.Format(Ui.T("0 — {0:0.#}  ·  最近 {1} 次采样", "0 — {0:0.#}  ·  Last {1} samples"), max, values.Length), new Point(0, height + 3));
    }
    private void DrawText(DrawingContext dc, string value, Point origin) => dc.DrawText(new FormattedText(value, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Ui.Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip), origin);
}
