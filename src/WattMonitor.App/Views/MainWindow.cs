using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WattMonitor.App.Controls;
using WattMonitor.Core;
using Path = System.Windows.Shapes.Path;

namespace WattMonitor.App.Views;

public sealed class MainWindow : Window
{
    private readonly AppController controller;
    private readonly ContentControl content = new();
    private readonly ScrollViewer scroller = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Padding = new Thickness(0, 0, 2, 10) };
    private readonly TextBlock status = Ui.Text("正在连接", 12, Ui.Muted);
    private readonly Button retry;
    private readonly TextBlock pageTitle = Ui.Text("总览", 25);
    private readonly TextBlock pageCaption = Ui.Text("", 12, Ui.Muted);
    private readonly Dictionary<string, (Button Button, Path Icon)> nav = [];
    private readonly OverviewView overview;
    private readonly DashboardView dashboard;
    private readonly HistoryView history;
    private readonly SettingsView settings;
    private string page = "";

    public MainWindow(AppController controller)
    {
        this.controller = controller; Title = "WattHalo"; Width = 1080; Height = 760; MinWidth = 900; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterScreen; Ui.Window(this);
        overview = new OverviewView(controller, NavigateTo); dashboard = new DashboardView(controller); history = new HistoryView(controller); settings = new SettingsView(controller);
        var overlay = new Grid(); var root = new Grid(); root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(192) }); root.ColumnDefinitions.Add(new ColumnDefinition()); overlay.Children.Add(root);
        var sidebar = new DockPanel { Margin = new Thickness(0) }; sidebar.SetResourceReference(BackgroundProperty, "Ui.Surface");
        var brand = new DockPanel { Margin = new Thickness(20, 22, 14, 24) };
        var logo = new Image { Width = 30, Height = 30, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center }; logo.SetResourceReference(Image.SourceProperty, "Ui.BrandMark"); DockPanel.SetDock(logo, Dock.Left); brand.Children.Add(logo);
        var name = new StackPanel(); var brandName = Ui.Text("WattHalo", 15); brandName.FontWeight = FontWeights.SemiBold; name.Children.Add(brandName); name.Children.Add(Ui.Text("能耗与性能", 12, Ui.Muted)); brand.Children.Add(name); DockPanel.SetDock(brand, Dock.Top); sidebar.Children.Add(brand);
        var footer = new StackPanel { Margin = new Thickness(16, 0, 12, 20) };
        var panelButton = Ui.Button("打开快览面板", controller.TogglePanel); panelButton.HorizontalAlignment = HorizontalAlignment.Stretch; footer.Children.Add(panelButton);
        var quit = Ui.Button("退出并停止记录", controller.Quit); quit.HorizontalAlignment = HorizontalAlignment.Stretch; footer.Children.Add(quit); DockPanel.SetDock(footer, Dock.Bottom); sidebar.Children.Add(footer);
        var navigation = new StackPanel { Margin = new Thickness(12, 0, 12, 0) };
        AddNav(navigation, "总览", "M 2,8 L 10,2 18,8 M 4,7 L 4,18 9,18 9,12 12,12 12,18 16,18 16,7");
        AddNav(navigation, "实时监测", "M 2,11 L 6,11 8,5 11,16 13,9 15,11 18,11");
        AddNav(navigation, "用电记录", "M 3,18 L 18,18 M 5,15 L 5,10 M 10,15 L 10,5 M 15,15 L 15,8");
        AddNav(navigation, "设置", "M 10,2 L 11.3,3.8 13.5,3.7 14.2,5.8 16.2,6.8 15.8,9 17,10.8 15.5,12.4 15.4,14.6 13.2,15.2 12,17 10,16 8,17 6.8,15.2 4.6,14.6 4.5,12.4 3,10.8 4.2,9 3.8,6.8 5.8,5.8 6.5,3.7 8.7,3.8 Z M 10,7 A 3,3 0 1 0 10,13 A 3,3 0 1 0 10,7");
        sidebar.Children.Add(navigation); root.Children.Add(sidebar);
        var main = new DockPanel { Margin = new Thickness(28, 19, 28, 18) }; Grid.SetColumn(main, 1);
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) }; var head = new StackPanel(); head.Children.Add(pageTitle); head.Children.Add(pageCaption); header.Children.Add(head); DockPanel.SetDock(header, Dock.Top); main.Children.Add(header);
        var bottom = new Border { Padding = new Thickness(0, 7, 0, 0), BorderThickness = new Thickness(0, 1, 0, 0) }; bottom.SetResourceReference(Border.BorderBrushProperty, "Ui.Line");
        var statusLine = new DockPanel(); retry = Ui.Button("重试连接", controller.ResumeMonitoring); retry.Margin = new Thickness(12, 0, 0, 0); retry.MinHeight = 28; retry.Padding = new Thickness(8, 2, 8, 2); DockPanel.SetDock(retry, Dock.Right); statusLine.Children.Add(retry);
        status.TextTrimming = TextTrimming.CharacterEllipsis; status.VerticalAlignment = VerticalAlignment.Center; statusLine.Children.Add(status); bottom.Child = statusLine; DockPanel.SetDock(bottom, Dock.Bottom); main.Children.Add(bottom);
        scroller.Content = content; main.Children.Add(scroller); root.Children.Add(main);
        overlay.Children.Add(new ToastHost(controller)); Content = overlay;
        Closing += (_, e) => { if (!Dispatcher.HasShutdownStarted) { e.Cancel = true; Hide(); } };
        controller.Updated += Updated; Ui.ThemeChanged += ThemeChanged; Ui.LanguageChanged += LanguageChanged;
        Closed += (_, _) => { controller.Updated -= Updated; Ui.ThemeChanged -= ThemeChanged; Ui.LanguageChanged -= LanguageChanged; };
        IsVisibleChanged += (_, _) => { if (IsVisible) Updated(this, EventArgs.Empty); };
        NavigateTo("总览");
    }

    private void AddNav(Panel parent, string title, string geometry)
    {
        var icon = new Path { Data = Geometry.Parse(geometry), Width = 20, Height = 20, Stretch = Stretch.Uniform, StrokeThickness = 1.6, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Margin = new Thickness(0, 0, 11, 0) };
        var label = Ui.Text(title, 14); label.VerticalAlignment = VerticalAlignment.Center;
        var body = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Left }; body.Children.Add(icon); body.Children.Add(label);
        var button = Ui.Button(title, () => NavigateTo(title)); button.Content = body; button.HorizontalContentAlignment = HorizontalAlignment.Left; button.Padding = new Thickness(12, 8, 12, 8); button.Margin = new Thickness(0, 3, 0, 3); button.MinHeight = 42;
        button.BorderThickness = new Thickness(0);
        nav[title] = (button, icon); parent.Children.Add(button);
    }
    private void ThemeChanged(object? sender, EventArgs e) => UpdateNavigation();
    private void LanguageChanged(object? sender, EventArgs e) { UpdatePageLabels(); Updated(this, EventArgs.Empty); }
    private void UpdateNavigation()
    {
        foreach (var pair in nav)
        {
            var active = pair.Key == page; var button = pair.Value.Button;
            button.SetResourceReference(Control.BackgroundProperty, active ? "Ui.NavSelected" : "Ui.Surface");
            button.SetResourceReference(Control.ForegroundProperty, active && SystemParameters.HighContrast ? "Ui.OnAccent" : "Ui.Foreground");
            pair.Value.Icon.SetResourceReference(Shape.StrokeProperty, active ? SystemParameters.HighContrast ? "Ui.OnAccent" : "Ui.Accent" : "Ui.Muted");
            if (button.Content is Panel body && body.Children.OfType<TextBlock>().FirstOrDefault() is { } label) label.SetResourceReference(TextBlock.ForegroundProperty, active && SystemParameters.HighContrast ? "Ui.OnAccent" : "Ui.Foreground");
        }
    }
    private void Updated(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Updated(sender, e)); return; }
        if (!IsVisible) return;
        var state = controller.MonitorStatus;
        status.Text = state.StorageError is not null ? Ui.T("记录保存失败", "Recording failed") :
            state.SamplingError is not null ? Ui.T("采集中断，正在重试", "Sampling interrupted; retrying") :
            state.IsStale ? Ui.T("数据已过期，等待恢复", "Data is stale; waiting to reconnect") :
            state.LastSample is null ? Ui.T("正在连接采集源", "Connecting to the sampling source") :
            state.IsRecording ? Ui.T("正在记录", "Recording") : Ui.T("未记录", "Not recording");
        if (state.LastSample is not null && !state.PowerComplete && state.StorageError is null && state.SamplingError is null && !state.IsStale)
            status.Text += Ui.T(" · 功率不完整", " · Power readings incomplete");
        var connection = state.Connection switch
        {
            "硬件采集服务" => Ui.T("硬件采集服务", "Hardware sampling service"),
            "本机采集 · 独立数据目录" => Ui.T("本机采集 · 独立数据目录", "Local sampling · isolated data"),
            "本机采集 · 未连接硬件服务" => Ui.T("本机采集 · 未连接硬件服务", "Local sampling · service disconnected"),
            _ => Ui.T(state.Connection)
        };
        status.ToolTip = string.Join("\n", new[] { connection,
            state.LastSample is { } time ? Ui.T("最近采样 ", "Last sample ") + time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") : Ui.T("等待采样", "Waiting for a sample"),
            state.StorageError, state.SamplingError }.Where(x => !string.IsNullOrWhiteSpace(x)));
        retry.Visibility = state.IsStale || state.SamplingError is not null ? Visibility.Visible : Visibility.Collapsed;
        if (page == "总览") overview.Refresh(); else if (page == "实时监测") dashboard.Refresh(); else if (page == "设置") settings.Refresh();
    }

    public void NavigateTo(string requested, MetricKind? metric = null)
    {
        var next = requested switch { "概览" => "总览", "历史" => "用电记录", "总览" or "实时监测" or "用电记录" or "设置" => requested, _ => "总览" };
        if (page == "设置" && next != page && !settings.CanLeave()) return;
        if (metric.HasValue) controller.SelectMetric(metric.Value, false);
        var changedPage = page != next;
        page = next; UpdatePageLabels();
        content.Content = next switch { "总览" => overview, "实时监测" => dashboard, "用电记录" => history, _ => settings };
        if (changedPage) scroller.ScrollToTop();
        UpdateNavigation();
        if (next == "总览") overview.Refresh(); else if (next == "实时监测") dashboard.Refresh(true); else if (next == "用电记录") history.Activate(); else settings.Activate();
    }
    private void UpdatePageLabels()
    {
        pageTitle.Text = Ui.T(page);
        Title = Ui.T(page) + " · WattHalo";
        pageCaption.Text = page switch
        {
            "总览" => Ui.T("电脑当前状态与今日用电", "Current PC status and today's energy use"),
            "实时监测" => Ui.T("查看实时读数与应用活动", "Live readings and application activity"),
            "用电记录" => Ui.T("回顾已记录的用电与费用", "Review recorded energy use and costs"),
            _ => Ui.T("管理显示、采集和估算方式", "Manage display, sampling and estimation")
        };
    }
    public bool ConfirmBeforeExit() => page != "设置" || settings.CanLeave();
}
