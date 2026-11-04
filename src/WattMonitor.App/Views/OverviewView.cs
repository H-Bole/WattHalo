using System.Windows;
using System.Windows.Controls;
using WattMonitor.App.Controls;
using WattMonitor.Core;

namespace WattMonitor.App.Views;

internal sealed class OverviewView : StackPanel
{
    private readonly AppController controller;
    private readonly TextBlock power = Ui.Text("—", 36);
    private readonly TextBlock powerNote = Ui.Text("等待采集", 12, Ui.Muted);
    private readonly TextBlock cost = Ui.Text("—", 31);
    private readonly TextBlock energy = Ui.Text("等待记录", 12, Ui.Muted);
    private readonly TextBlock cpu = Ui.Text("—", 24);
    private readonly TextBlock gpu = Ui.Text("—", 24);
    private readonly TextBlock vram = Ui.Text("—", 24);
    private readonly TextBlock memory = Ui.Text("—", 24);
    private readonly TextBlock cpuNote = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock gpuNote = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock vramNote = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock memoryNote = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock disk = Ui.Text("—", 14);
    private readonly TextBlock network = Ui.Text("—", 14);
    private readonly Border intro;

    public OverviewView(AppController controller, Action<string, MetricKind?> navigate)
    {
        this.controller = controller;
        var welcome = new StackPanel();
        welcome.Children.Add(Ui.Text("让每一瓦，都有迹可循", 17));
        welcome.Children.Add(Ui.Text("主机功率按硬件读数与电源效率估算，建议使用插座功率计校准。退出或休眠期间不会补记。", 12, Ui.Muted));
        intro = Ui.Card(welcome); intro.Visibility = controller.Settings.OnboardingCompleted ? Visibility.Collapsed : Visibility.Visible;
        welcome.Children.Add(Ui.Button("了解，开始使用", () => { controller.QueueSettings(s => s with { OnboardingCompleted = true }); intro.Visibility = Visibility.Collapsed; }, true));

        var headline = new Grid { Margin = new Thickness(0, 4, 0, 10) };
        headline.ColumnDefinitions.Add(new ColumnDefinition()); headline.ColumnDefinitions.Add(new ColumnDefinition());
        var powerCard = Card("主机估算功率", power, powerNote, "查看功率详情 →", () => navigate("实时监测", MetricKind.Power));
        powerCard.Margin = new Thickness(0, 0, 6, 0); headline.Children.Add(powerCard);
        var costCard = Card("今日预计电费", cost, energy, "查看用电记录 →", () => navigate("用电记录", null));
        costCard.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(costCard, 1); headline.Children.Add(costCard);
        Children.Add(headline);

        var title = new DockPanel { Margin = new Thickness(0, 6, 0, 4) };
        var more = Ui.Button("查看实时详情 →", () => navigate("实时监测", null)); more.Padding = new Thickness(10, 4, 10, 4); more.MinHeight = 30; more.Margin = new Thickness(0); DockPanel.SetDock(more, Dock.Right); title.Children.Add(more);
        title.Children.Add(Ui.Text("硬件状态", 17)); Children.Add(title);
        var metrics = new Grid();
        metrics.ColumnDefinitions.Add(new ColumnDefinition()); metrics.ColumnDefinitions.Add(new ColumnDefinition());
        metrics.RowDefinitions.Add(new RowDefinition()); metrics.RowDefinitions.Add(new RowDefinition());
        void AddMetric(int row, int col, string name, TextBlock value, TextBlock note, MetricKind kind)
        {
            var card = Card(name, value, note, "", () => navigate("实时监测", kind));
            card.Margin = new Thickness(col == 0 ? 0 : 6, 4, col == 0 ? 6 : 0, 4);
            Grid.SetRow(card, row); Grid.SetColumn(card, col); metrics.Children.Add(card);
        }
        AddMetric(0, 0, "CPU", cpu, cpuNote, MetricKind.Cpu); AddMetric(0, 1, "GPU", gpu, gpuNote, MetricKind.Gpu);
        AddMetric(1, 0, "专用显存", vram, vramNote, MetricKind.Gpu); AddMetric(1, 1, "内存", memory, memoryNote, MetricKind.Memory);
        Children.Add(metrics);
        var throughput = new Grid { Margin = new Thickness(0, 8, 0, 0) }; throughput.ColumnDefinitions.Add(new ColumnDefinition()); throughput.ColumnDefinitions.Add(new ColumnDefinition());
        var diskCard = Card("磁盘吞吐", disk, Ui.Text("读取 + 写入", 12, Ui.Muted), "", () => navigate("实时监测", MetricKind.Disk)); diskCard.Margin = new Thickness(0, 0, 6, 0); throughput.Children.Add(diskCard);
        var netCard = Card("网络吞吐", network, Ui.Text("接收 + 发送", 12, Ui.Muted), "", () => navigate("实时监测", MetricKind.Network)); netCard.Margin = new Thickness(6, 0, 0, 0); Grid.SetColumn(netCard, 1); throughput.Children.Add(netCard); Children.Add(throughput);
        Children.Add(Ui.Text("功率与电费均为估算；仅累计功率完整的已采样时段。", 12, Ui.Muted));
        Children.Add(intro);
        Loaded += (_, _) => { Ui.LanguageChanged += LanguageChanged; if (IsVisible) Refresh(); };
        Unloaded += (_, _) => Ui.LanguageChanged -= LanguageChanged;
    }

    private static Button Card(string title, TextBlock value, TextBlock note, string action, Action click)
    {
        var body = new StackPanel(); body.Children.Add(Ui.Text(title, 12, Ui.Muted)); body.Children.Add(value); body.Children.Add(note);
        if (!string.IsNullOrEmpty(action)) body.Children.Add(Ui.Text(action, 12, Ui.Accent));
        var button = Ui.Button("", click); button.Content = body; button.Padding = new Thickness(16, 12, 16, 12); button.HorizontalContentAlignment = HorizontalAlignment.Stretch; button.VerticalContentAlignment = VerticalAlignment.Top; button.Margin = new Thickness(0);
        button.SetResourceReference(Control.BorderBrushProperty, "Ui.Line");
        Ui.Bind(button, System.Windows.Automation.AutomationProperties.NameProperty, title);
        return button;
    }

    public void Refresh()
    {
        var stale = controller.MonitorStatus.IsStale;
        var s = stale ? null : controller.Snapshot; var totals = controller.Today;
        var staleNote = controller.Snapshot is { } last
            ? Ui.T("数据已过期 · 最后更新 ", "Stale data · last updated ") + last.Timestamp.ToLocalTime().ToString("HH:mm:ss")
            : Ui.T("等待读数");
        power.Text = Ui.Number(s?.EstimatedHostWatts, " W", "0.#");
        powerNote.Text = stale ? staleNote : s is null ? Ui.T("等待本机采集") : s.PowerComplete ? Ui.T("已读取主要部件 · 未经校准", "Major components read · not calibrated") : Ui.T("功率读数不完整", "Incomplete power readings");
        cost.Text = totals.CompleteSeconds > 0 ? $"¥ {totals.CostYuan:0.00}" : "—";
        energy.Text = totals.CompleteSeconds > 0 ? string.Format(Ui.T("估算 {0:0.000} kWh · 已记录 {1:0.0} h", "Estimated {0:0.000} kWh · Recorded {1:0.0} h"), totals.EstimatedKwh, totals.RecordedSeconds / 3600) : Ui.T("暂无完整功率记录", "No complete power records yet");
        cpu.Text = Ui.Number(s?.CpuPercent, " %", "0.#"); cpuNote.Text = stale ? staleNote : s?.CpuName ?? Ui.T("等待读数");
        var g = s?.Gpus.FirstOrDefault(); gpu.Text = Ui.Number(g?.UtilizationPercent, " %", "0.#"); gpuNote.Text = stale ? staleNote : g?.Name ?? Ui.T("等待读数");
        vram.Text = Ui.Bytes(g?.DedicatedUsedBytes); vramNote.Text = stale ? staleNote : g?.DedicatedTotalBytes is { } total ? Ui.T("共 ", "Total ") + Ui.Bytes(total) : Ui.T("专用显存");
        memory.Text = Ui.Number(Ui.Value(s, MetricKind.Memory), " %", "0.#"); memoryNote.Text = stale ? staleNote : s is null ? Ui.T("等待读数") : $"{Ui.Bytes(s.MemoryUsedBytes)} / {Ui.Bytes(s.MemoryTotalBytes)}";
        disk.Text = Ui.Metric(s, MetricKind.Disk); network.Text = Ui.Metric(s, MetricKind.Network);
    }
    private void LanguageChanged(object? sender, EventArgs e) { if (IsVisible) Refresh(); }
}
