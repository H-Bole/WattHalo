using System.Windows;
using System.Windows.Controls;
using WattMonitor.App.Controls;
using WattMonitor.Core;

namespace WattMonitor.App.Views;

internal sealed class DashboardView : StackPanel
{
    private readonly AppController controller;
    private readonly bool compact;
    private readonly Dictionary<MetricKind, Button> tabs = [];
    private readonly Dictionary<int, Button> ranges = [];
    private readonly TextBlock value = Ui.Text("—", 36);
    private readonly TextBlock subtitle = Ui.Text("等待本机采集", 12, Ui.Muted);
    private TextBlock info = Ui.Text("", 14, Ui.Muted);
    private TextBlock warning = Ui.Text("", 12, Ui.Muted);
    private TextBlock rankTitle = Ui.Text("应用排行", 17);
    private TextBlock rankHelp = Ui.Text("", 12, Ui.Muted);
    private readonly StackPanel detail = new();
    private StackPanel rankings = new();
    private readonly TimeSeriesChart chart = new();
    private readonly Dictionary<string, GroupRow> groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expanded = new(StringComparer.OrdinalIgnoreCase);
    private ComboBox? gpuSelector;
    private Button? gpuUsageMode, gpuMemoryMode;
    internal event Action? TransientClosed;
    private string? selectedGpu;
    private bool selectingGpu, vram, operating;
    private int openMenus, minutes = 5;
    private MetricKind? builtKind;
    private MetricKind Kind => compact ? controller.Settings.PanelSelectedTab ?? controller.Settings.SelectedTab : controller.Settings.SelectedTab;
    private bool Frozen => rankings.IsMouseOver || rankings.IsKeyboardFocusWithin || openMenus > 0 || operating;

    public DashboardView(AppController controller, bool compact = false)
    {
        this.controller = controller; this.compact = compact;
        if (compact) { value.FontSize = 26; subtitle.TextWrapping = TextWrapping.NoWrap; subtitle.TextTrimming = TextTrimming.CharacterEllipsis; chart.Height = 100; }
        var tabPanel = new WrapPanel { Margin = compact ? new Thickness(0, 0, 0, 3) : new Thickness(0, 3, 0, 8) };
        foreach (var kind in Enum.GetValues<MetricKind>())
        {
            var button = Ui.Button(Ui.NameKey(kind), () => { controller.SelectMetric(kind, compact); Refresh(true); });
            button.Padding = compact ? new Thickness(8, 4, 8, 4) : new Thickness(12, 6, 12, 6); button.MinHeight = compact ? 28 : 32; if (compact) button.Margin = new Thickness(0, 2, 5, 2); tabs[kind] = button; tabPanel.Children.Add(button);
        }
        Children.Add(tabPanel);
        var body = new StackPanel();
        var heading = new Grid(); heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var metric = new StackPanel(); if (!compact) metric.Children.Add(Ui.Text("当前读数", 12, Ui.Muted)); metric.Children.Add(value); metric.Children.Add(subtitle); heading.Children.Add(metric);
        var controls = new WrapPanel { VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var range in new[] { 5, 15, 60 })
        {
            var button = Ui.Button(range + (compact ? " 分" : " 分钟"), () => { minutes = range; Refresh(true); }); button.Padding = compact ? new Thickness(6, 3, 6, 3) : new Thickness(9, 5, 9, 5); button.MinHeight = compact ? 27 : 30; if (compact) button.Margin = new Thickness(0, 2, 4, 2); ranges[range] = button; controls.Children.Add(button);
        }
        Grid.SetColumn(controls, 1); heading.Children.Add(controls); body.Children.Add(heading); body.Children.Add(chart);
        if (!compact) body.Children.Add(Ui.Text("横轴为本地时间；断线代表缺失采样。移动鼠标查看实际时间与数值。", 12, Ui.Muted));
        Children.Add(DetailCard(body)); Children.Add(detail);
        IsVisibleChanged += (_, _) => { if (IsVisible) Refresh(true); };
        Loaded += (_, _) => Ui.ThemeChanged += ThemeChanged;
        Unloaded += (_, _) => Ui.ThemeChanged -= ThemeChanged;
        Loaded += (_, _) => Ui.LanguageChanged += ThemeChanged;
        Unloaded += (_, _) => Ui.LanguageChanged -= ThemeChanged;
    }

    private void ThemeChanged(object? sender, EventArgs e) { if (IsVisible) Refresh(true); }
    private static void Selected(Button button, bool selected)
    {
        if (selected) { button.SetResourceReference(Control.BackgroundProperty, "Ui.Accent"); button.SetResourceReference(Control.ForegroundProperty, "Ui.OnAccent"); }
        else { button.SetResourceReference(Control.BackgroundProperty, "Ui.Surface"); button.SetResourceReference(Control.ForegroundProperty, "Ui.Foreground"); }
    }

    public void Refresh(bool force = false)
    {
        if (!IsVisible && !force) return;
        var kind = Kind;
        foreach (var pair in tabs) Selected(pair.Value, pair.Key == kind);
        foreach (var pair in ranges) Selected(pair.Value, pair.Key == minutes);
        if (builtKind != kind) BuildDetails(kind);
        var stale = controller.MonitorStatus.IsStale;
        var s = stale ? null : controller.Snapshot;
        if (kind == MetricKind.Gpu && s is not null) UpdateGpuSelector(s);
        if (kind == MetricKind.Gpu) UpdateRankMode();
        value.Text = kind == MetricKind.Gpu ? Ui.Number(Ui.Value(s, kind, selectedGpu), " %") : Ui.Metric(s, kind);
        subtitle.Text = stale && controller.Snapshot is { } last
            ? Ui.T("数据已过期 · 最后更新 ", "Stale data · last updated ") + last.Timestamp.ToLocalTime().ToString("HH:mm:ss")
            : kind switch
        {
            MetricKind.Power => Ui.T("主机功率估算 · 不包含显示器", "Estimated PC power · excludes the display"),
            MetricKind.Cpu => s?.CpuName ?? Ui.T("CPU · 等待读数", "CPU · waiting for a reading"),
            MetricKind.Gpu => Ui.T("最忙引擎利用率 · 不同引擎不可直接相加", "Busiest engine utilization · engine values cannot be added directly"),
            MetricKind.Memory => s is null ? Ui.T("等待读数") : $"{Ui.Bytes(s.MemoryUsedBytes)} / {Ui.Bytes(s.MemoryTotalBytes)}",
            MetricKind.Disk => Ui.T("全局读取 + 写入 · 所有磁盘", "System read + write · all disks"),
            _ => Ui.T("全局接收 + 发送 · 所有有效网卡", "System receive + send · all active network adapters")
        };
        var series = controller.Trend.Select(p => (p.Timestamp, Value: kind is MetricKind.Disk or MetricKind.Network ? Ui.Value(p, kind, selectedGpu) / 1048576 : Ui.Value(p, kind, selectedGpu)));
        chart.SetData(series, minutes, Math.Max(5, controller.Settings.RefreshSeconds * 1.5), kind == MetricKind.Power ? "W" : kind is MetricKind.Disk or MetricKind.Network ? "MB/s" : "%", kind is MetricKind.Cpu or MetricKind.Gpu or MetricKind.Memory);
        info.Text = Info(s, kind);
        warning.Text = stale ? Ui.T("数据已过期，等待重新连接。", "Data is stale; waiting to reconnect.") :
            s is null ? Ui.T("正在连接采集源；未知读数显示为 —。", "Connecting to the data source; unavailable readings are shown as —.") : string.Join("\n", s.Warnings.Select(Ui.Diagnostic));
        warning.Visibility = string.IsNullOrWhiteSpace(warning.Text) ? Visibility.Collapsed : Visibility.Visible;
        rankings.IsEnabled = !stale;
        if (stale && kind is (MetricKind.Cpu or MetricKind.Gpu or MetricKind.Memory))
        {
            rankHelp.Text = Ui.T("排行为上次数据，恢复采样后更新。", "Ranking shows previous data and will update when sampling resumes.");
            rankHelp.Visibility = Visibility.Visible;
        }
        if (s is not null && kind is (MetricKind.Cpu or MetricKind.Gpu or MetricKind.Memory)) UpdateRankings(s);
    }

    private void BuildDetails(MetricKind kind)
    {
        builtKind = kind; detail.Children.Clear(); groups.Clear(); gpuSelector = null; gpuUsageMode = gpuMemoryMode = null;
        info = Ui.Text("", 14, Ui.Muted); warning = Ui.Text("", 12, Ui.Muted); rankTitle = Ui.Text("应用排行", 17); rankHelp = Ui.Text("", 12, Ui.Muted); rankings = new StackPanel();
        rankings.MouseLeave += (_, _) => RefreshRankingsWhenUnfrozen();
        rankings.IsKeyboardFocusWithinChanged += (_, _) => RefreshRankingsWhenUnfrozen();
        var block = new StackPanel();
        if (!compact || kind != MetricKind.Gpu) block.Children.Add(Ui.Text(kind switch { MetricKind.Power => "估算依据", MetricKind.Gpu => "图形处理器", MetricKind.Disk => "磁盘活动", MetricKind.Network => "网络活动", _ => "使用情况" }, compact ? 14 : 17));
        if (kind == MetricKind.Gpu)
        {
            gpuSelector = new ComboBox { DisplayMemberPath = "Name", SelectedValuePath = "Id", HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 200, Margin = compact ? new Thickness(0, 0, 0, 2) : new Thickness(0, 6, 0, 4) };
            gpuSelector.DropDownClosed += (_, _) => TransientClosed?.Invoke();
            gpuSelector.SelectionChanged += (_, _) => { if (!selectingGpu && gpuSelector.SelectedValue is string id) { selectedGpu = id; Refresh(true); } };
            block.Children.Add(gpuSelector);
        }
        block.Children.Add(info); if (!compact) block.Children.Add(warning);
        if (!compact || kind is not (MetricKind.Cpu or MetricKind.Memory)) detail.Children.Add(DetailCard(block));
        if (kind is not (MetricKind.Cpu or MetricKind.Gpu or MetricKind.Memory)) { if (compact) detail.Children.Add(warning); return; }
        var rank = new StackPanel(); rank.Children.Add(rankTitle);
        if (compact) rankTitle.FontSize = 15;
        if (kind == MetricKind.Gpu)
        {
            var modes = new WrapPanel();
            var usage = gpuUsageMode = Ui.Button("GPU 使用率", () => { vram = false; UpdateRankMode(); UpdateRankings(controller.Snapshot); });
            var memory = gpuMemoryMode = Ui.Button("专用显存", () => { vram = true; UpdateRankMode(); UpdateRankings(controller.Snapshot); });
            if (compact) foreach (var button in new[] { usage, memory }) { button.MinHeight = 28; button.Padding = new Thickness(8, 3, 8, 3); button.Margin = new Thickness(0, 2, 6, 2); }
            modes.Children.Add(usage); modes.Children.Add(memory);
            rank.Children.Add(modes);
            UpdateRankMode();
        }
        rank.Children.Add(rankHelp); rank.Children.Add(rankings); detail.Children.Add(DetailCard(rank));
        if (compact) detail.Children.Add(warning);
    }

    private void UpdateRankMode()
    {
        if (gpuUsageMode is null || gpuMemoryMode is null) return;
        Selected(gpuUsageMode, !vram); Selected(gpuMemoryMode, vram);
        System.Windows.Automation.AutomationProperties.SetItemStatus(gpuUsageMode, !vram ? Ui.T("已选中", "Selected") : "");
        System.Windows.Automation.AutomationProperties.SetItemStatus(gpuMemoryMode, vram ? Ui.T("已选中", "Selected") : "");
    }

    private Border DetailCard(UIElement content)
    {
        var card = Ui.Card(content);
        if (compact) { card.Padding = new Thickness(10); card.Margin = new Thickness(0, 4, 0, 4); }
        return card;
    }

    private void UpdateGpuSelector(TelemetrySnapshot s)
    {
        if (gpuSelector is null) return;
        var ids = (gpuSelector.ItemsSource as GpuSample[])?.Select(g => g.Id).ToArray() ?? [];
        if (ids.SequenceEqual(s.Gpus.Select(g => g.Id)) && gpuSelector.SelectedValue is not null) return;
        selectingGpu = true;
        gpuSelector.ItemsSource = s.Gpus;
        if (!s.Gpus.Any(g => g.Id == selectedGpu)) selectedGpu = s.Gpus.FirstOrDefault()?.Id;
        gpuSelector.SelectedValue = selectedGpu;
        selectingGpu = false;
    }

    private string Info(TelemetrySnapshot? s, MetricKind kind)
    {
        if (s is null) return Ui.T("等待数据");
        if (kind == MetricKind.Power)
        {
            var total = controller.Today;
            var completeness = s.PowerComplete ? Ui.T("读数完整", "Complete readings") : Ui.T("读数不完整", "Incomplete readings");
            if (compact) return string.Format(Ui.T("部件 {0} · CPU {1}\n其他部件 {2:0.#} W · 效率 {3:P0} · {4}", "Components {0} · CPU {1}\nOther components {2:0.#} W · efficiency {3:P0} · {4}"), Ui.Number(s.ComponentWatts, " W"), Ui.Number(s.CpuPackageWatts, " W"), controller.Settings.OtherPartsWatts, controller.Settings.PsuEfficiency, completeness);
            var gpuPower = string.Join("\n", s.Gpus.Select(g => $"{g.Name}  {Ui.Number(g.PowerWatts, " W")}  ·  {Ui.T(g.PowerSource)}"));
            var today = total.CompleteSeconds > 0 ? $"¥ {total.CostYuan:0.00} · {total.EstimatedKwh:0.000} kWh" : Ui.T("缺少完整功率记录", "No complete power records");
            return string.Format(Ui.T("部件小计  {0}\nCPU 封装  {1}  ·  {2}\n{3}\n其他部件 {4:0.#} W · 电源效率 {5:P0}\n主机估算 =（已读取部件 + 其他部件）÷ 效率。集成显卡可能包含在 CPU 封装内，不重复累计。\n今日：{6}；已记录 {7:0.0} h，功率完整 {8:0.0} h。", "Components subtotal  {0}\nCPU package  {1}  ·  {2}\n{3}\nOther components {4:0.#} W · power supply efficiency {5:P0}\nPC estimate = (measured components + other components) ÷ efficiency. Integrated graphics may be included in the CPU package and are not counted twice.\nToday: {6}; recorded {7:0.0} h, complete power {8:0.0} h."), Ui.Number(s.ComponentWatts, " W"), Ui.Number(s.CpuPackageWatts, " W"), Ui.T(s.CpuPowerSource), gpuPower, controller.Settings.OtherPartsWatts, controller.Settings.PsuEfficiency, today, total.RecordedSeconds / 3600, total.CompleteSeconds / 3600);
        }
        if (kind == MetricKind.Gpu)
        {
            var g = s.Gpus.FirstOrDefault(x => x.Id == selectedGpu) ?? s.Gpus.FirstOrDefault();
            if (g is null) return Ui.T("GPU 读数不可用", "GPU readings unavailable");
            var dedicated = Ui.T("专用显存", "Dedicated GPU memory");
            if (compact) return $"{dedicated} {Ui.Bytes(g.DedicatedUsedBytes)} / {Ui.Bytes(g.DedicatedTotalBytes)}";
            return $"{dedicated}  {Ui.Bytes(g.DedicatedUsedBytes)} / {Ui.Bytes(g.DedicatedTotalBytes)}\n{Ui.T("共享显存", "Shared GPU memory")}  {Ui.Bytes(g.SharedUsedBytes)}\n" + string.Join("  ·  ", g.Engines.OrderByDescending(e => e.Value).Select(e => e.Key + " " + Ui.Number(e.Value, " %")));
        }
        if (kind == MetricKind.Disk) return string.Format(Ui.T("读取  {0}/s     写入  {1}/s\n仅显示全局吞吐，不归因到应用。", "Read  {0}/s     Write  {1}/s\nSystem throughput only; not attributed to applications."), Ui.Bytes(s.DiskReadBytesPerSecond), Ui.Bytes(s.DiskWriteBytesPerSecond));
        if (kind == MetricKind.Network) return string.Format(Ui.T("接收  {0}/s     发送  {1}/s\n仅显示全局网速，不归因到应用。", "Receive  {0}/s     Send  {1}/s\nSystem network speed only; not attributed to applications."), Ui.Bytes(s.NetworkReceiveBytesPerSecond), Ui.Bytes(s.NetworkSendBytesPerSecond));
        return kind == MetricKind.Memory ? string.Format(Ui.T("已使用 {0}  /  总计 {1}", "Used {0}  /  Total {1}"), Ui.Bytes(s.MemoryUsedBytes), Ui.Bytes(s.MemoryTotalBytes)) : Ui.T("CPU 使用率为系统整体读数。", "CPU utilization is the system-wide reading.");
    }

    private double? Score(ProcessSample p) => Kind switch { MetricKind.Cpu => p.CpuPercent, MetricKind.Memory => p.MemoryBytes, _ => vram ? p.DedicatedGpuBytes : p.GpuPercent };
    private string ScoreText(double? score) => Kind == MetricKind.Memory || (Kind == MetricKind.Gpu && vram) ? Ui.Bytes(score) : Ui.Number(score, " %");
    private sealed record Group(string Key, ProcessSample[] Items, double? Score);

    private void RefreshRankingsWhenUnfrozen()
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (IsVisible && !Frozen && Kind is (MetricKind.Cpu or MetricKind.Gpu or MetricKind.Memory) && controller.Snapshot is { } snapshot) UpdateRankings(snapshot);
        });
    }

    private void UpdateRankings(TelemetrySnapshot? snapshot)
    {
        if (snapshot is null) return;
        var topCount = compact ? Math.Min(controller.Settings.TopCount, 10) : controller.Settings.TopCount;
        rankTitle.Text = Ui.T("应用排行", "Top applications") + $" · Top {topCount}" + (Kind == MetricKind.Gpu ? vram ? " · " + Ui.T("专用显存") : " · GPU" : "");
        rankHelp.Text = compact ? Kind == MetricKind.Gpu ? Ui.T("排行覆盖全部适配器。", "Ranking covers all adapters.") : "" : Kind == MetricKind.Gpu ? Ui.T("应用排行覆盖系统提供的全部适配器，不随上方 GPU 选择过滤。展开后可正常关闭进程。", "The ranking covers all system adapters and is not filtered by the GPU selected above. Expand an item to close a process normally.") : Ui.T("同路径与名称汇总；路径未知时按 PID 单独显示。展开后可正常关闭进程。", "Processes with the same path and name are grouped; unknown paths are listed by PID. Expand an item to close a process normally.");
        rankHelp.Visibility = string.IsNullOrEmpty(rankHelp.Text) ? Visibility.Collapsed : Visibility.Visible;
        var all = snapshot.Processes.GroupBy(p => !string.IsNullOrWhiteSpace(p.ExecutablePath) ? "path:" + p.ExecutablePath + "|" + p.Name : "unknown:" + p.Name + "|" + p.Pid, StringComparer.OrdinalIgnoreCase)
            .Select(g => new Group(g.Key, g.ToArray(), Kind == MetricKind.Gpu && !vram ? GpuUtilization.ForApplication(g) : g.Any(p => Score(p).HasValue) ? g.Sum(p => Score(p) ?? 0) : null))
            .OrderByDescending(g => g.Score).ToArray();
        var data = all.Take(topCount).ToArray();
        var frozen = Frozen;
        if (frozen)
        {
            var byKey = all.ToDictionary(g => g.Key, StringComparer.OrdinalIgnoreCase);
            foreach (var entry in groups) { if (byKey.TryGetValue(entry.Key, out var current)) entry.Value.Update(current, true); else entry.Value.Missing(); }
            return;
        }
        foreach (var group in data)
        {
            if (!groups.TryGetValue(group.Key, out var row))
            {
                if (frozen) continue;
                row = new GroupRow(this, group.Key); groups[group.Key] = row; rankings.Children.Add(row);
            }
            row.Update(group, frozen);
        }
        var keys = data.Select(g => g.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in groups.Keys.Where(k => !keys.Contains(k)).ToArray()) { rankings.Children.Remove(groups[key]); groups.Remove(key); }
        for (var i = 0; i < data.Length; i++)
        {
            var row = groups[data[i].Key]; var current = rankings.Children.IndexOf(row);
            if (current != i) { rankings.Children.RemoveAt(current); rankings.Children.Insert(i, row); }
        }
    }

    private void Close(ProcessSample process, bool force)
    {
        operating = true;
        try
        {
            if (force && Ui.Confirm(string.Format(Ui.T("强制结束 {0}（PID {1}）？\n未保存的内容可能丢失。\n{2}", "Force quit {0} (PID {1})?\nUnsaved work may be lost.\n{2}"), process.Name, process.Pid, process.ExecutablePath), "强制结束进程", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            var message = controller.CloseProcess(process, force);
            controller.Notify(new OperationResult(message.StartsWith("已请求", StringComparison.Ordinal), Ui.Diagnostic(message)));
        }
        finally { operating = false; if (controller.Snapshot is { } latest) UpdateRankings(latest); }
    }

    private sealed class GroupRow : Border
    {
        private readonly DashboardView owner;
        private readonly TextBlock title = Ui.Text("", 14);
        private readonly TextBlock score = Ui.Text("", 14, Ui.Accent);
        private readonly TextBlock path = Ui.Text("", 12, Ui.Muted);
        private readonly StackPanel rows = new();
        private readonly Dictionary<(int, long), ProcessRow> processes = [];
        public GroupRow(DashboardView owner, string key)
        {
            this.owner = owner;
            SetResourceReference(BorderBrushProperty, "Ui.Line"); BorderThickness = new Thickness(0, 0, 0, 1); Padding = new Thickness(0, owner.compact ? 2 : 6, 0, owner.compact ? 2 : 6);
            var header = new Grid(); header.ColumnDefinitions.Add(new ColumnDefinition()); header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); header.Children.Add(title); Grid.SetColumn(score, 1); header.Children.Add(score);
            var body = new StackPanel { Margin = new Thickness(8, 4, 0, 4) }; body.Children.Add(path); body.Children.Add(rows);
            var expander = new Expander { Header = header, Content = body, IsExpanded = owner.expanded.Contains(key), HorizontalContentAlignment = HorizontalAlignment.Stretch };
            expander.SetResourceReference(Control.ForegroundProperty, "Ui.Foreground"); expander.Expanded += (_, _) => owner.expanded.Add(key); expander.Collapsed += (_, _) => owner.expanded.Remove(key); Child = expander;
        }
        public void Update(Group group, bool frozen)
        {
            var first = group.Items[0]; title.Text = string.Format(Ui.T("{0}  ·  {1} 个进程", "{0}  ·  {1} process(es)"), first.Name, group.Items.Length); score.Text = owner.ScoreText(group.Score); path.Text = first.ExecutablePath ?? Ui.T("路径不可访问 · 按 PID 单独显示", "Path inaccessible · shown separately by PID");
            var sorted = group.Items.OrderByDescending(p => owner.Score(p)).ToArray();
            foreach (var p in sorted)
            {
                var key = (p.Pid, p.StartTimeUtcTicks);
                if (!processes.TryGetValue(key, out var row)) { if (frozen) continue; row = new ProcessRow(owner); processes[key] = row; rows.Children.Add(row); }
                row.Update(p);
            }
            var keys = sorted.Select(p => (p.Pid, p.StartTimeUtcTicks)).ToHashSet();
            if (frozen) { foreach (var pair in processes) if (!keys.Contains(pair.Key)) pair.Value.Missing(); return; }
            foreach (var key in processes.Keys.Where(k => !keys.Contains(k)).ToArray()) { rows.Children.Remove(processes[key]); processes.Remove(key); }
            for (var i = 0; i < sorted.Length; i++) { var row = processes[(sorted[i].Pid, sorted[i].StartTimeUtcTicks)]; var index = rows.Children.IndexOf(row); if (index != i) { rows.Children.RemoveAt(index); rows.Children.Insert(i, row); } }
        }
        public void Missing() { score.Text = Ui.T("— 已退出", "— Exited"); foreach (var row in processes.Values) row.Missing(); }
    }

    private sealed class ProcessRow : DockPanel
    {
        private readonly DashboardView owner;
        private readonly TextBlock label = Ui.Text("", 12);
        private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, Opacity = .35 };
        private readonly Button close, menuButton;
        private ProcessSample? process;
        public ProcessRow(DashboardView owner)
        {
            this.owner = owner;
            close = Ui.Button("正常关闭", () => { if (process is not null) owner.Close(process, false); }); close.Padding = new Thickness(8, 3, 8, 3); close.MinHeight = 28;
            var menu = new ContextMenu(); var force = new MenuItem(); Ui.Bind(force, HeaderedItemsControl.HeaderProperty, "强制结束…"); force.Click += (_, _) => { if (process is not null) owner.Close(process, true); }; menu.Items.Add(force);
            menu.Opened += (_, _) => owner.openMenus++; menu.Closed += (_, _) => { owner.openMenus = Math.Max(0, owner.openMenus - 1); owner.RefreshRankingsWhenUnfrozen(); };
            Button? more = null; more = Ui.Button("···", () => { menu.PlacementTarget = more; menu.IsOpen = true; }); menuButton = more; menuButton.Padding = new Thickness(8, 3, 8, 3); menuButton.MinHeight = 28; Ui.Bind(menuButton, ToolTipProperty, "更多进程操作"); Ui.Bind(menuButton, System.Windows.Automation.AutomationProperties.NameProperty, "更多进程操作");
            actions.Children.Add(close); actions.Children.Add(menuButton); DockPanel.SetDock(actions, Dock.Right); Children.Add(actions); Children.Add(label);
            MouseEnter += (_, _) => actions.Opacity = 1; MouseLeave += (_, _) => { if (!IsKeyboardFocusWithin) actions.Opacity = .35; };
            GotKeyboardFocus += (_, _) => actions.Opacity = 1; LostKeyboardFocus += (_, _) => { if (!IsKeyboardFocusWithin && !IsMouseOver) actions.Opacity = .35; };
        }
        public void Update(ProcessSample p) { process = p; label.Text = $"PID {p.Pid}  ·  {owner.ScoreText(owner.Score(p))}"; close.IsEnabled = p.CanClose; menuButton.IsEnabled = p.CanClose; }
        public void Missing() { process = null; label.Text = label.Text.Split('·')[0].TrimEnd() + Ui.T("  ·  已退出", "  ·  Exited"); close.IsEnabled = menuButton.IsEnabled = false; }
    }
}
