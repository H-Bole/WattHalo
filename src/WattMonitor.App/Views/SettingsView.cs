using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using WattMonitor.App.Controls;
using WattMonitor.Core;

namespace WattMonitor.App.Views;

internal sealed class SettingsView : StackPanel
{
    private readonly AppController controller;
    private readonly ComboBox theme = new(), language = new(), refresh = new(), top = new(), orbMetric = new();
    private readonly CheckBox reduced = new(), startup = new(), orb = new(), taskbar = new(), pinned = new(), addTariff = new();
    private readonly TextBox watts = new(), efficiency = new(), price = new();
    private readonly DatePicker effective = new();
    private readonly TextBlock wattsError = Ui.Text("", 12), efficiencyError = Ui.Text("", 12), priceError = Ui.Text("", 12), dateError = Ui.Text("", 12);
    private readonly TextBlock billingFeedback = Ui.Text("", 12, Ui.Muted), tariffList = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock orbPreview = Ui.Text("—", 19), orbPreviewLabel = Ui.Text("", 12, Ui.Muted), taskbarPreview = Ui.Text("", 14), taskbarMode = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock updateVersion = Ui.Text("", 13, Ui.Muted), updateStatus = Ui.Text("", 14), updateNotes = Ui.Text("", 12, Ui.Muted), updateHint = Ui.Text("", 12, Ui.Muted);
    private readonly ProgressBar updateProgress = new() { Minimum = 0, Maximum = 100, Height = 5, Margin = new Thickness(0, 8, 0, 8) };
    private Button? checkUpdates, installUpdate, retryTaskbar, repairService;
    private readonly StackPanel orderRows = new();
    private readonly List<MetricKind> order;
    private readonly HashSet<MetricKind> enabled;
    private string originalWatts = "", originalEfficiency = "";
    private bool suppress, billingLoaded;

    public SettingsView(AppController controller)
    {
        this.controller = controller;
        order = controller.Settings.TaskbarMetrics.Concat(Enum.GetValues<MetricKind>()).Distinct().ToList(); enabled = controller.Settings.TaskbarMetrics.ToHashSet();
        Children.Add(Ui.Text("显示选项即时生效；估算与电价修改需单独保存。", 13, Ui.Muted));
        var sections = new TabControl { Margin = new Thickness(0, 10, 0, 0), BorderThickness = new Thickness(0) };
        sections.SetResourceReference(Control.BackgroundProperty, "Ui.Background");
        sections.SetResourceReference(Control.ForegroundProperty, "Ui.Foreground");
        Children.Add(sections);
        void AddSection(string title, UIElement content)
        {
            var tab = new TabItem { Content = content, Padding = new Thickness(16, 9, 16, 9) };
            Ui.Bind(tab, HeaderedContentControl.HeaderProperty, title);
            sections.Items.Add(tab);
        }

        var appearance = Section("外观", "界面主题与动态效果");
        Choice(appearance, "主题", theme, ["深色", "浅色", "跟随系统"]);
        theme.SelectionChanged += (_, _) => { if (!suppress && theme.SelectedIndex >= 0) controller.QueueSettings(s => s with { Theme = (ThemePreference)theme.SelectedIndex }); };
        Choice(appearance, "语言", language, ["跟随系统", "简体中文", "英语"]);
        language.SelectionChanged += (_, _) => { if (!suppress && language.SelectedIndex >= 0) controller.QueueSettings(s => s with { Language = (LanguagePreference)language.SelectedIndex }); };
        Check(appearance, reduced, "减少动态效果", b => controller.QueueSettings(s => s with { ReducedMotion = b })); AddSection("外观", Ui.Card(appearance));

        var sampling = Section("采集", "影响实时刷新与应用排行显示");
        Choice(sampling, "刷新间隔", refresh, ["1 秒", "2 秒", "5 秒"]); refresh.SelectionChanged += (_, _) => { if (!suppress && refresh.SelectedIndex >= 0) controller.QueueSettings(s => s with { RefreshSeconds = new[] { 1, 2, 5 }[refresh.SelectedIndex] }); };
        Choice(sampling, "应用排行", top, ["前 3 项", "前 10 项", "前 20 项（快览最多 10 项）"]); top.SelectionChanged += (_, _) => { if (!suppress && top.SelectedIndex >= 0) controller.QueueSettings(s => s with { TopCount = new[] { 3, 10, 20 }[top.SelectedIndex] }); };
        Check(sampling, startup, "开机启动", b => controller.QueueSettings(s => s with { StartWithWindows = b }));
        repairService = Ui.Button("修复采集服务", controller.InstallHardwareSupport);
        sampling.Children.Add(repairService); sampling.Children.Add(Ui.Text("需要管理员授权；CPU 功率还需要 PawnIO 驱动。", 12, Ui.Muted)); AddSection("采集", Ui.Card(sampling));

        var desktop = Section("桌面组件", "设置悬浮圆环、快览和任务栏指标");
        Check(desktop, orb, "显示悬浮圆环", b => controller.QueueSettings(s => s with { ShowOrb = b }));
        Choice(desktop, "圆环显示指标", orbMetric, Enum.GetValues<MetricKind>().Select(Ui.NameKey).ToArray()); orbMetric.SelectionChanged += (_, _) => { if (!suppress && orbMetric.SelectedIndex >= 0) controller.QueueSettings(s => s with { OrbMetric = (MetricKind)orbMetric.SelectedIndex }); };
        Check(desktop, pinned, "固定快览（点击外部不收起）", b => controller.QueueSettings(s => s with { PanelPinned = b }));
        Check(desktop, taskbar, "显示任务栏指标", b => controller.QueueSettings(s => s with { ShowTaskbar = b }));
        retryTaskbar = Ui.Button("重新检测任务栏", controller.RetryTaskbar);
        desktop.Children.Add(retryTaskbar);
        desktop.Children.Add(Ui.Text("任务栏指标与次序", 14)); desktop.Children.Add(Ui.Text("拖动行可排序；键盘使用 Alt + ↑ / ↓ 调整，空格切换显示。", 12, Ui.Muted)); desktop.Children.Add(orderRows); DrawOrder();
        var desktopContent = new StackPanel(); desktopContent.Children.Add(Preview()); desktopContent.Children.Add(Ui.Card(desktop));
        AddSection("桌面组件", desktopContent);

        var billing = Section("估算与电价", "仅影响保存后的新采样；历史电费保留原电价");
        Input(billing, "其他部件补偿（W）", watts, wattsError); Input(billing, "电源效率（0.5–1）", efficiency, efficiencyError);
        billing.Children.Add(tariffList); Ui.Bind(addTariff, ContentControl.ContentProperty, "添加或更新电价"); addTariff.Margin = new Thickness(0, 8, 0, 8); addTariff.SetResourceReference(Control.ForegroundProperty, "Ui.Foreground"); billing.Children.Add(addTariff);
        Input(billing, "电价（元 / kWh）", price, priceError); billing.Children.Add(Ui.Text("生效日期（本地 00:00）", 13, Ui.Muted)); effective.Width = 200; effective.HorizontalAlignment = HorizontalAlignment.Left; billing.Children.Add(effective); billing.Children.Add(dateError);
        addTariff.Checked += (_, _) => EnableTariff(); addTariff.Unchecked += (_, _) => EnableTariff();
        billing.Children.Add(Ui.Button("保存估算与电价", () => SaveBilling(), true)); billing.Children.Add(billingFeedback); AddSection("估算与电价", Ui.Card(billing));

        var about = Section("关于与更新", "查看当前版本并检查新版本");
        about.Children.Add(Ui.Text("WattHalo", 18));
        about.Children.Add(updateVersion);
        about.Children.Add(updateStatus); about.Children.Add(updateProgress); about.Children.Add(updateHint);
        var updateActions = new WrapPanel();
        checkUpdates = Ui.Button("检查更新", () => _ = controller.CheckForUpdatesAsync(true));
        installUpdate = Ui.Button("下载安装更新", () => _ = controller.InstallUpdateAsync(), true);
        updateActions.Children.Add(checkUpdates); updateActions.Children.Add(installUpdate);
        updateActions.Children.Add(Ui.Button("打开发布页面", controller.OpenReleases)); about.Children.Add(updateActions);
        about.Children.Add(updateNotes); AddSection("关于与更新", Ui.Card(about));
        LoadBilling(); Activate();
        Loaded += (_, _) => { Ui.LanguageChanged += LanguageChanged; LanguageChanged(null, EventArgs.Empty); };
        Unloaded += (_, _) => Ui.LanguageChanged -= LanguageChanged;
    }

    private UIElement Preview()
    {
        var block = Section("实时预览", "预览随当前采样与设置更新"); var row = new WrapPanel();
        var circle = new Grid { Width = 98, Height = 98, Margin = new Thickness(0, 8, 24, 8) };
        var outer = new Ellipse { StrokeThickness = 3 }; outer.SetResourceReference(Shape.StrokeProperty, "Ui.Accent"); circle.Children.Add(outer);
        var inner = new Ellipse { Margin = new Thickness(7), StrokeThickness = 1 }; inner.SetResourceReference(Shape.StrokeProperty, "Ui.Line"); circle.Children.Add(inner);
        var center = new StackPanel { VerticalAlignment = VerticalAlignment.Center }; orbPreview.TextAlignment = TextAlignment.Center; orbPreviewLabel.TextAlignment = TextAlignment.Center; center.Children.Add(orbPreview); center.Children.Add(orbPreviewLabel); circle.Children.Add(center); row.Children.Add(circle);
        var task = new StackPanel { VerticalAlignment = VerticalAlignment.Center, MinWidth = 240 }; task.Children.Add(Ui.Text("任务栏", 12, Ui.Muted)); var badge = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(10, 5, 10, 5), Child = taskbarPreview }; badge.SetResourceReference(Border.BackgroundProperty, "Ui.Background"); badge.SetResourceReference(Border.BorderBrushProperty, "Ui.Line"); badge.BorderThickness = new Thickness(1); task.Children.Add(badge); task.Children.Add(taskbarMode); row.Children.Add(task); block.Children.Add(row); return Ui.Card(block);
    }
    private static StackPanel Section(string title, string description)
    {
        var stack = new StackPanel();
        var heading = Ui.Text(title, 18); heading.FontWeight = FontWeights.SemiBold; stack.Children.Add(heading);
        var hint = Ui.Text(description, 12, Ui.Muted); hint.Margin = new Thickness(0, 2, 0, 14); stack.Children.Add(hint);
        return stack;
    }
    private static Grid SettingRow(string label, UIElement control)
    {
        var row = new Grid { MinHeight = 48, Margin = new Thickness(0, 2, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var title = Ui.Text(label, 14); title.VerticalAlignment = VerticalAlignment.Center;
        title.Margin = new Thickness(0, 0, 20, 0); row.Children.Add(title);
        Grid.SetColumn(control, 1); row.Children.Add(control); return row;
    }
    private static void Choice(Panel target, string label, ComboBox box, string[] options)
    {
        foreach (var option in options) { var item = new ComboBoxItem(); Ui.Bind(item, ContentControl.ContentProperty, option); box.Items.Add(item); }
        box.Width = 230; box.MinHeight = 36; box.VerticalAlignment = VerticalAlignment.Center;
        Ui.Bind(box, System.Windows.Automation.AutomationProperties.NameProperty, label);
        target.Children.Add(SettingRow(label, box));
    }
    private void Check(Panel target, CheckBox box, string label, Action<bool> update)
    {
        Ui.Bind(box, System.Windows.Automation.AutomationProperties.NameProperty, label);
        box.VerticalAlignment = VerticalAlignment.Center; box.Margin = new Thickness(8);
        box.SetResourceReference(Control.ForegroundProperty, "Ui.Foreground");
        box.Checked += (_, _) => { if (!suppress) update(true); }; box.Unchecked += (_, _) => { if (!suppress) update(false); };
        var row = SettingRow(label, box); row.Background = Brushes.Transparent; row.Cursor = Cursors.Hand;
        row.MouseLeftButtonUp += (_, _) => { if (!box.IsMouseOver && box.IsEnabled) box.IsChecked = box.IsChecked != true; };
        target.Children.Add(row);
    }
    private static void Input(Panel target, string label, TextBox input, TextBlock error)
    {
        input.Width = 230; input.Padding = new Thickness(8); input.VerticalAlignment = VerticalAlignment.Center;
        Ui.Bind(input, System.Windows.Automation.AutomationProperties.NameProperty, label);
        target.Children.Add(SettingRow(label, input));
        error.Margin = new Thickness(0, 0, 0, 5); error.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Danger"); target.Children.Add(error);
    }
    private void EnableTariff() { price.IsEnabled = effective.IsEnabled = addTariff.IsChecked == true; }

    public void Activate()
    {
        var s = controller.Settings; suppress = true;
        theme.SelectedIndex = (int)s.Theme; language.SelectedIndex = (int)s.Language; refresh.SelectedIndex = Array.IndexOf(new[] { 1, 2, 5 }, s.RefreshSeconds); top.SelectedIndex = Array.IndexOf(new[] { 3, 10, 20 }, s.TopCount); orbMetric.SelectedIndex = (int)s.OrbMetric;
        reduced.IsChecked = s.ReducedMotion; startup.IsChecked = s.StartWithWindows; orb.IsChecked = s.ShowOrb; taskbar.IsChecked = s.ShowTaskbar; pinned.IsChecked = s.PanelPinned;
        if (!order.Where(enabled.Contains).SequenceEqual(s.TaskbarMetrics)) { enabled.Clear(); enabled.UnionWith(s.TaskbarMetrics); order.Clear(); order.AddRange(s.TaskbarMetrics.Concat(Enum.GetValues<MetricKind>()).Distinct()); DrawOrder(); }
        suppress = false; Refresh();
    }
    public void Refresh()
    {
        var s = controller.Settings; var kind = s.OrbMetric; var value = Ui.Value(controller.Snapshot, kind);
        orbPreview.Text = kind is MetricKind.Disk or MetricKind.Network ? Ui.Bytes(value) : value.HasValue ? value.Value.ToString("0") : "—";
        orbPreviewLabel.Text = Ui.Name(kind) + (kind == MetricKind.Power ? " · W" : kind is MetricKind.Disk or MetricKind.Network ? "/s" : " · %");
        var sample = controller.Snapshot;
        var gpu = sample?.Gpus.FirstOrDefault(g => g.IsDiscrete) ?? sample?.Gpus.FirstOrDefault();
        var stale = controller.MonitorStatus.IsStale;
        string PreviewMetric(MetricKind metric)
        {
            if (metric is not (MetricKind.Cpu or MetricKind.Gpu)) return Ui.Name(metric) + " " + Ui.Metric(sample, metric);
            var reading = stale ? null : metric == MetricKind.Cpu ? sample?.CpuPercent : gpu?.UtilizationPercent;
            var watts = stale ? null : metric == MetricKind.Cpu ? sample?.CpuPackageWatts : gpu?.PowerWatts;
            return Ui.Name(metric) + " " + (reading is { } value ? value.ToString("F0", CultureInfo.CurrentCulture) + "%" : "—") + " · " + (watts is { } power ? power.ToString("F1", CultureInfo.CurrentCulture) + " W" : "— W");
        }
        taskbarPreview.Text = string.Join("   ·   ", s.TaskbarMetrics.Select(PreviewMetric)); taskbarMode.Text = Ui.Diagnostic(controller.TaskbarMode);
        if (retryTaskbar is not null) retryTaskbar.Visibility = s.ShowTaskbar && !controller.TaskbarMode.StartsWith("已嵌入", StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
        if (repairService is not null) repairService.IsEnabled = !controller.RepairingService;
        var updates = controller.Updates;
        updateVersion.Text = Ui.T("版本 ", "Version ") + updates.CurrentVersion;
        updateStatus.Text = updates.Status;
        updateProgress.Value = updates.Progress;
        updateProgress.Visibility = updates.Stage == UpdateStage.Downloading ? Visibility.Visible : Visibility.Collapsed;
        updateNotes.Text = updates.Release is null || string.IsNullOrWhiteSpace(updates.Notes) ? "" :
            Ui.T("发布说明", "Release notes") + "\n" + updates.Notes;
        updateHint.Text = updates.RecoveryPending
            ? Ui.T("检测到尚未清理的更新事务，系统可能正在恢复。若重启后仍有此提示，请使用可信安装包修复；不要删除恢复目录。", "An update transaction is still present and recovery may be in progress. If this remains after a restart, repair using a trusted installer; do not delete the recovery folder.")
            : !controller.UpdateInstallAvailable
            ? Ui.T("应用内安装尚未完成恢复验证；请使用可信的完整安装包。", "In-app installation has not completed recovery verification. Use a trusted full installer.")
            : Ui.T("启动时及之后每天自动检查；安装前会验证发布者签名。", "Checks on startup and daily; publisher signatures are verified before installation.");
        if (checkUpdates is not null) checkUpdates.IsEnabled = !updates.Busy;
        if (installUpdate is not null)
        {
            installUpdate.Visibility = updates.Release is null ? Visibility.Collapsed : Visibility.Visible;
            installUpdate.IsEnabled = controller.UpdateInstallAvailable && !updates.Busy;
        }
    }

    private void LanguageChanged(object? sender, EventArgs e)
    {
        UpdateTariffs();
        if (!string.IsNullOrEmpty(wattsError.Text)) wattsError.Text = Ui.T("请输入 0–1000 之间的数字。", "Enter a number from 0 to 1000.");
        if (!string.IsNullOrEmpty(efficiencyError.Text)) efficiencyError.Text = Ui.T("请输入 0.5–1 之间的数字。", "Enter a number from 0.5 to 1.");
        if (!string.IsNullOrEmpty(priceError.Text)) priceError.Text = Ui.T("请输入 0–100 之间的电价。", "Enter a tariff from 0 to 100.");
        if (!string.IsNullOrEmpty(dateError.Text)) dateError.Text = Ui.T("请选择生效日期。", "Choose an effective date.");
        if (billingFeedback.Text is "请先修正标出的字段。" or "Correct the highlighted fields first.") billingFeedback.Text = Ui.T("请先修正标出的字段。", "Correct the highlighted fields first.");
        else if (billingFeedback.Text is "估算与电价已保存。" or "Estimation and tariff saved.") billingFeedback.Text = Ui.T("估算与电价已保存。", "Estimation and tariff saved.");
        if (IsVisible) Refresh();
    }

    private void DrawOrder()
    {
        orderRows.Children.Clear();
        foreach (var kind in order.ToArray())
        {
            var line = new DockPanel { Focusable = true, AllowDrop = true, Margin = new Thickness(0, 3, 0, 3) }; line.SetResourceReference(BackgroundProperty, "Ui.Background");
            var check = new CheckBox { IsChecked = enabled.Contains(kind), Margin = new Thickness(8), VerticalAlignment = VerticalAlignment.Center }; Ui.Bind(check, ContentControl.ContentProperty, "☰   " + Ui.NameKey(kind)); check.SetResourceReference(Control.ForegroundProperty, "Ui.Foreground");
            check.Checked += (_, _) => { if (suppress) return; enabled.Add(kind); SaveOrder(); };
            check.Unchecked += (_, _) => { if (suppress) return; if (enabled.Count == 1 && enabled.Contains(kind)) { suppress = true; check.IsChecked = true; suppress = false; controller.Notify(new OperationResult(false, "请至少保留一个任务栏指标。")); return; } enabled.Remove(kind); SaveOrder(); };
            var hint = Ui.Text("拖动  ·  Alt + ↑ / ↓", 12, Ui.Muted); hint.Margin = new Thickness(4, 8, 8, 8); DockPanel.SetDock(hint, Dock.Right); line.Children.Add(hint); line.Children.Add(check);
            Point? start = null;
            line.PreviewMouseLeftButtonDown += (_, e) => start = e.GetPosition(line);
            line.PreviewMouseMove += (_, e) => { if (start is not { } p || e.LeftButton != MouseButtonState.Pressed) return; var current = e.GetPosition(line); if (Math.Abs(current.X - p.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(current.Y - p.Y) < SystemParameters.MinimumVerticalDragDistance) return; start = null; DragDrop.DoDragDrop(line, kind.ToString(), DragDropEffects.Move); };
            line.DragOver += (_, e) => { e.Effects = e.Data.GetDataPresent(DataFormats.StringFormat) ? DragDropEffects.Move : DragDropEffects.None; e.Handled = true; };
            line.Drop += (_, e) => { if (Enum.TryParse<MetricKind>(e.Data.GetData(DataFormats.StringFormat) as string, out var moving)) Move(moving, order.IndexOf(kind)); e.Handled = true; };
            line.PreviewKeyDown += (_, e) => { var key = e.Key == Key.System ? e.SystemKey : e.Key; if (Keyboard.Modifiers != ModifierKeys.Alt || key is not (Key.Up or Key.Down)) return; Move(kind, order.IndexOf(kind) + (key == Key.Up ? -1 : 1)); e.Handled = true; };
            Ui.Bind(line, System.Windows.Automation.AutomationProperties.NameProperty, Ui.NameKey(kind) + "，按 Alt 加上下方向键排序"); orderRows.Children.Add(line);
        }
    }
    private void Move(MetricKind kind, int index)
    {
        var old = order.IndexOf(kind); index = Math.Clamp(index, 0, order.Count - 1); if (old < 0 || old == index) return;
        order.RemoveAt(old); order.Insert(index, kind); DrawOrder(); SaveOrder();
        if (orderRows.Children[index] is Panel row) row.Children.OfType<CheckBox>().FirstOrDefault()?.Focus();
    }
    private void SaveOrder() { var current = order.Where(enabled.Contains).ToArray(); controller.QueueSettings(s => s with { TaskbarMetrics = current }); }

    private void LoadBilling()
    {
        var s = controller.Settings; originalWatts = watts.Text = s.OtherPartsWatts.ToString(CultureInfo.CurrentCulture); originalEfficiency = efficiency.Text = s.PsuEfficiency.ToString(CultureInfo.CurrentCulture);
        price.Text = (s.Tariffs.LastOrDefault()?.YuanPerKwh ?? 1m).ToString(CultureInfo.CurrentCulture); effective.SelectedDate = DateTime.Today; addTariff.IsChecked = false; EnableTariff();
        UpdateTariffs();
        wattsError.Text = efficiencyError.Text = priceError.Text = dateError.Text = ""; billingFeedback.Text = ""; billingLoaded = true;
    }
    private void UpdateTariffs() => tariffList.Text = Ui.T("已设电价：\n", "Current tariffs:\n") + string.Join("\n", controller.Settings.Tariffs.OrderBy(t => t.EffectiveFrom).Select(t => $"{(t.EffectiveFrom == DateTimeOffset.UnixEpoch ? Ui.T("初始电价", "Initial tariff") : t.EffectiveFrom.ToLocalTime().ToString("yyyy-MM-dd"))}  ·  ¥ {t.YuanPerKwh:0.####} / kWh"));
    private bool Dirty => billingLoaded && (watts.Text != originalWatts || efficiency.Text != originalEfficiency || addTariff.IsChecked == true);
    public bool CanLeave()
    {
        if (!Dirty) return true;
        var answer = Ui.Confirm(Ui.T("估算与电价有未保存的修改。是否保存？\n选择“不保存”将丢弃修改。", "Estimation and tariff changes have not been saved. Save them?\nChoose Don't save to discard them."), "未保存的修改", MessageBoxButton.YesNoCancel, MessageBoxImage.Question, MessageBoxResult.Cancel);
        if (answer == MessageBoxResult.Cancel) return false;
        if (answer == MessageBoxResult.No) { LoadBilling(); return true; }
        return SaveBilling();
    }
    private bool SaveBilling()
    {
        var validWatts = double.TryParse(watts.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var w) && double.IsFinite(w) && w is >= 0 and <= 1000;
        var validEfficiency = double.TryParse(efficiency.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var psu) && double.IsFinite(psu) && psu is >= .5 and <= 1;
        var add = addTariff.IsChecked == true; decimal yuan = 0;
        var validPrice = !add || decimal.TryParse(price.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out yuan) && yuan is >= 0 and <= 100;
        var validDate = !add || effective.SelectedDate is not null;
        wattsError.Text = validWatts ? "" : Ui.T("请输入 0–1000 之间的数字。", "Enter a number from 0 to 1000."); efficiencyError.Text = validEfficiency ? "" : Ui.T("请输入 0.5–1 之间的数字。", "Enter a number from 0.5 to 1."); priceError.Text = validPrice ? "" : Ui.T("请输入 0–100 之间的电价。", "Enter a tariff from 0 to 100."); dateError.Text = validDate ? "" : Ui.T("请选择生效日期。", "Choose an effective date.");
        if (!validWatts || !validEfficiency || !validPrice || !validDate) { billingFeedback.Text = Ui.T("请先修正标出的字段。", "Correct the highlighted fields first."); return false; }
        var tariffs = controller.Settings.Tariffs;
        if (add) { var date = effective.SelectedDate!.Value.Date; var boundary = new DateTimeOffset(date, TimeZoneInfo.Local.GetUtcOffset(date)); tariffs = tariffs.Where(t => t.EffectiveFrom != boundary).Append(new Tariff(boundary, yuan)).OrderBy(t => t.EffectiveFrom).ToArray(); }
        var result = controller.SaveSettings(controller.Settings with { OtherPartsWatts = w, PsuEfficiency = psu, Tariffs = tariffs });
        if (result.Success) { LoadBilling(); billingFeedback.Text = Ui.T("估算与电价已保存。", "Estimation and tariff saved."); } else billingFeedback.Text = Ui.T(result.Message);
        controller.Notify(result); return result.Success;
    }
}
