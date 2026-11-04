using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using WattMonitor.App.Controls;
using WattMonitor.Core;

namespace WattMonitor.App.Views;

internal sealed class HistoryView : StackPanel
{
    private readonly AppController controller;
    private readonly DatePicker from = new() { Width = 138 };
    private readonly DatePicker to = new() { Width = 138 };
    private readonly TextBlock feedback = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock cost = Ui.Text("—", 28);
    private readonly TextBlock energy = Ui.Text("—", 28);
    private readonly TextBlock coverage = Ui.Text("—", 18);
    private readonly TextBlock coverageHint = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock calendarHint = Ui.Text("选择方块查看当日详情", 12, Ui.Muted);
    private readonly TextBlock chartHint = Ui.Text("", 12, Ui.Muted);
    private readonly TextBlock chartDetail = Ui.Text("", 12, Ui.Muted);
    private readonly EnergyCalendar calendar = new();
    private readonly EnergyHistoryChart chart = new();
    private readonly StackPanel results = new() { Visibility = Visibility.Collapsed };
    private readonly ScrollViewer calendarScroller = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalContentAlignment = HorizontalAlignment.Left };
    private readonly DataGrid table = new()
    {
        AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, CanUserDeleteRows = false,
        HeadersVisibility = DataGridHeadersVisibility.Column, MinHeight = 120, MaxHeight = 390,
        EnableRowVirtualization = true, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
    };
    private readonly Dictionary<string, Button> presets = [];
    private readonly Dictionary<HistoryChartStyle, Button> chartStyles = [];
    private readonly Dictionary<DataGridColumn, string> columnKeys = [];
    private readonly Button query, export;
    private string activePreset = "近 7 天";
    private bool settingDates, initialized;
    private bool lastQueryEnglish;
    private DateTime presetDate;
    private int request;
    private DayRow[] rows = [];
    private HistoryVisualPoint[] graphPoints = [];

    public HistoryView(AppController controller)
    {
        this.controller = controller;
        var intro = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        var description = Ui.Text("按日期查看电脑的估算用电和费用。", 13, Ui.Muted);
        intro.Children.Add(description); Children.Add(intro);

        var filters = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
        void Preset(string label)
        {
            var button = Ui.Button(label, () => { ApplyPreset(label); _ = Query(); });
            button.MinHeight = 36; button.Padding = new Thickness(12, 6, 12, 6);
            presets.Add(label, button); filters.Children.Add(button);
        }
        Preset("今日"); Preset("近 7 天"); Preset("本月"); Preset("近一年");
        var custom = Ui.Button("自选日期", () => { SetPreset("自选日期"); from.Focus(); });
        custom.MinHeight = 36; custom.Padding = new Thickness(12, 6, 12, 6);
        presets.Add("自选日期", custom); filters.Children.Add(custom);
        Children.Add(filters);
        var dates = new WrapPanel { Margin = new Thickness(0, 0, 0, 4), VerticalAlignment = VerticalAlignment.Center };
        dates.Children.Add(Ui.Text("从  ", 13, Ui.Muted)); dates.Children.Add(from);
        dates.Children.Add(Ui.Text("  至  ", 13, Ui.Muted)); dates.Children.Add(to);
        query = Ui.Button("查询", () => _ = Query(), true); query.MinHeight = 36; query.Padding = new Thickness(14, 6, 14, 6);
        export = Ui.Button("导出 CSV", () => _ = Export()); export.MinHeight = 36; export.Padding = new Thickness(14, 6, 14, 6);
        dates.Children.Add(query); dates.Children.Add(export); Children.Add(dates); Children.Add(feedback);
        Children.Add(results);
        from.SelectedDateChanged += (_, _) => FiltersChanged(); to.SelectedDateChanged += (_, _) => FiltersChanged();
        ApplyPreset(activePreset);

        var summaries = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        for (var i = 0; i < 3; i++) summaries.ColumnDefinitions.Add(new ColumnDefinition());
        void Summary(int column, string label, TextBlock value, string note)
        {
            var body = new StackPanel(); body.Children.Add(Ui.Text(label, 13, Ui.Muted)); body.Children.Add(value);
            if (column == 2) body.Children.Add(coverageHint); else body.Children.Add(Ui.Text(note, 12, Ui.Muted));
            var card = Ui.Card(body); card.Margin = new Thickness(column == 0 ? 0 : 5, 0, column == 2 ? 0 : 5, 0);
            card.Padding = new Thickness(16, 14, 16, 14); Grid.SetColumn(card, column); summaries.Children.Add(card);
        }
        Summary(0, "估算电费", cost, "沿用记录时的电价");
        Summary(1, "估算电量", energy, "仅包含功率完整的采样");
        Summary(2, "记录覆盖", coverage, "");
        results.Children.Add(summaries);

        var calendarBody = new StackPanel();
        calendarBody.Children.Add(Ui.Text("每日用电", 17));
        calendarBody.Children.Add(Ui.Text("颜色越深表示估算电量越高；斜线表示没有完整功率记录。", 12, Ui.Muted));
        calendarScroller.Content = calendar; calendarBody.Children.Add(calendarScroller); calendarBody.Children.Add(calendarHint);
        calendar.DaySelected += day => calendarHint.Text = day.Description;
        results.Children.Add(Ui.Card(calendarBody));

        var chartBody = new StackPanel();
        var chartHead = new DockPanel();
        var controls = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        void ChartChoice(HistoryChartStyle style, string name)
        {
            var button = Ui.Button(name, () => { controller.QueueSettings(s => s with { HistoryChart = style }); UpdateChartStyle(); });
            button.MinHeight = 34; button.Padding = new Thickness(10, 5, 10, 5);
            chartStyles[style] = button; controls.Children.Add(button);
        }
        ChartChoice(HistoryChartStyle.Bars, "柱状"); ChartChoice(HistoryChartStyle.Line, "折线"); ChartChoice(HistoryChartStyle.Smooth, "曲线");
        DockPanel.SetDock(controls, Dock.Right); chartHead.Children.Add(controls); chartHead.Children.Add(Ui.Text("用电趋势", 17)); chartBody.Children.Add(chartHead);
        chartBody.Children.Add(chartHint); chartBody.Children.Add(chart); chartBody.Children.Add(chartDetail);
        chart.PointSelected += point => chartDetail.Text = point.Description;
        results.Children.Add(Ui.Card(chartBody)); UpdateChartStyle();

        var tableBody = new StackPanel(); tableBody.Children.Add(Ui.Text("每日明细", 17));
        table.SetResourceReference(Control.BackgroundProperty, "Ui.Surface");
        table.SetResourceReference(Control.ForegroundProperty, "Ui.Foreground");
        table.SetResourceReference(Control.BorderBrushProperty, "Ui.Line");
        AddColumn("日期", nameof(DayRow.DateText), 1.1, 105);
        AddColumn("电量", nameof(DayRow.EnergyText), 1.1, 95);
        AddColumn("电费", nameof(DayRow.CostText), .9, 85);
        AddColumn("已记录", nameof(DayRow.RecordedText), 1.1, 110);
        AddColumn("功率完整", nameof(DayRow.CompleteText), 1.1, 110);
        AddColumn("运行中未记录", nameof(DayRow.MissingText), 1.4, 140);
        tableBody.Children.Add(table);
        tableBody.Children.Add(Ui.Text("未记录仅统计可以核实的电脑运行时间；证据不足时显示未知。", 12, Ui.Muted));
        results.Children.Add(Ui.Card(tableBody));
        UpdateCalendarLanguage();
        Loaded += (_, _) => Ui.LanguageChanged += LanguageChanged;
        Unloaded += (_, _) => Ui.LanguageChanged -= LanguageChanged;
    }

    public void Activate()
    {
        UpdateCalendarLanguage();
        foreach (var pair in columnKeys) pair.Key.Header = Ui.T(pair.Value);
        if (!initialized) { initialized = true; _ = Query(); return; }
        if (activePreset != "自选日期" && presetDate != DateTime.Today)
        {
            ApplyPreset(activePreset); _ = Query();
        }
        else if (lastQueryEnglish != Ui.IsEnglish) _ = Query();
        UpdateChartStyle();
    }

    private void LanguageChanged(object? sender, EventArgs e)
    {
        UpdateCalendarLanguage();
        foreach (var pair in columnKeys) pair.Key.Header = Ui.T(pair.Value);
        if (IsVisible) _ = Query();
    }

    private void UpdateCalendarLanguage()
    {
        var language = XmlLanguage.GetLanguage(Ui.IsEnglish ? "en-US" : "zh-CN");
        from.Language = language; to.Language = language;
    }

    private void ApplyPreset(string selected)
    {
        var today = DateTime.Today;
        var start = selected switch
        {
            "今日" => today,
            "本月" => new DateTime(today.Year, today.Month, 1),
            "近一年" => today.AddDays(-364),
            _ => today.AddDays(-6)
        };
        settingDates = true;
        try { from.SelectedDate = start; to.SelectedDate = today; }
        finally { settingDates = false; }
        presetDate = today; SetPreset(selected);
    }

    private void SetPreset(string selected)
    {
        activePreset = selected;
        foreach (var pair in presets)
        {
            var active = pair.Key == selected;
            pair.Value.SetResourceReference(Control.BackgroundProperty, active ? "Ui.Accent" : "Ui.Surface");
            pair.Value.SetResourceReference(Control.ForegroundProperty, active ? "Ui.OnAccent" : "Ui.Foreground");
            pair.Value.SetResourceReference(Control.BorderBrushProperty, active ? "Ui.Accent" : "Ui.ControlBorder");
        }
    }

    private void FiltersChanged()
    {
        if (settingDates) return;
        SetPreset("自选日期"); request++; query.IsEnabled = true; export.IsEnabled = true;
        results.Visibility = Visibility.Collapsed;
        feedback.Text = Ui.T("日期已更改，请查询记录。", "Dates changed. Search to update the results.");
    }

    private void AddColumn(string name, string path, double width, double minWidth)
    {
        var column = new DataGridTextColumn
        {
            Header = Ui.T(name), Binding = new Binding(path), Width = new DataGridLength(width, DataGridLengthUnitType.Star), MinWidth = minWidth
        };
        columnKeys.Add(column, name);
        table.Columns.Add(column);
    }

    private bool Range(out DateTime first, out DateTime last, out DateTimeOffset start, out DateTimeOffset end)
    {
        first = last = default; start = end = default;
        if (from.SelectedDate is not { } a || to.SelectedDate is not { } b || a.Date > b.Date || b.Date == DateTime.MaxValue.Date)
        { results.Visibility = Visibility.Collapsed; feedback.Text = Ui.T("请选择有效日期，结束日期不能早于开始日期。", "Choose valid dates; the end must not precede the start."); return false; }
        first = a.Date; last = b.Date;
        if ((last - first).TotalDays > 3652)
        { results.Visibility = Visibility.Collapsed; feedback.Text = Ui.T("一次最多查询十年，请缩小日期范围。", "Choose a range of at most ten years."); return false; }
        start = Boundary(first); end = Boundary(last.AddDays(1));
        if (last >= DateTime.Today) end = DateTimeOffset.Now < end ? DateTimeOffset.Now : end;
        if (end < start) end = start;
        return true;
    }

    private static DateTimeOffset Boundary(DateTime date) => new(date, TimeZoneInfo.Local.GetUtcOffset(date));

    private async Task Query()
    {
        if (!Range(out var first, out var last, out var start, out var end)) return;
        var id = ++request; query.IsEnabled = false; export.IsEnabled = false; results.Visibility = Visibility.Collapsed;
        feedback.Text = Ui.T("正在查询记录…", "Loading records…");
        try
        {
            var ranges = new List<(DateTimeOffset From, DateTimeOffset To)>();
            for (var day = first; day <= last && day <= DateTime.Today; day = day.AddDays(1))
            {
                var begin = Boundary(day); var finish = Boundary(day.AddDays(1));
                ranges.Add((begin, finish < end ? finish : end));
            }
            var result = await controller.QueryHistoryWithCoverageAsync(start, end, ranges);
            if (id != request) return;
            rows = BuildRows(result.Points, ranges, result.Coverage);
            table.ItemsSource = rows.Reverse().ToArray();
            var complete = rows.Sum(r => r.Complete);
            var recorded = rows.Sum(r => r.Recorded);
            var timeConflict = result.Coverage.Any(c => c.HasTimeConflict);
            cost.Text = complete > 0 ? $"¥ {rows.Sum(r => r.Cost):0.00}" : "—";
            energy.Text = complete > 0 ? $"{rows.Sum(r => r.Energy):0.000} kWh" : "—";
            coverage.Text = timeConflict ? Ui.T("时间异常，时长未知", "Clock conflict; duration unknown") :
                Ui.T("已记录 ", "Recorded ") + Duration(recorded);
            var knownMissing = result.Coverage.Sum(c => c.KnownUnrecordedSeconds);
            var unknown = result.Coverage.Any(c => c.Confidence != CoverageConfidence.Observed || c.UnknownSeconds is null || c.UnknownSeconds > 1);
            coverageHint.Text = timeConflict ? Ui.T("部分记录的系统时间发生冲突。", "Some records have conflicting system times.") : unknown ? (knownMissing >= 1
                ? Ui.T("运行中未记录至少 ", "Unrecorded while running: at least ") + Duration(knownMissing) + Ui.T("，其余未知", "; remainder unknown")
                : Ui.T("运行中未记录时长部分未知", "Some unrecorded running time is unknown"))
                : Ui.T("运行中未记录 ", "Unrecorded while running: ") + Duration(knownMissing);
            var calendarRows = rows.TakeLast(365).ToArray();
            calendar.SetDays(calendarRows.Select(r => new HistoryVisualDay(r.Date, r.Complete > 0 ? r.Energy : null,
                r.Complete > 0 ? r.Cost : null, r.Description)).ToArray());
            calendarHint.Text = rows.Length > 365 ? Ui.T("热力图显示所选范围最近一年；明细和趋势保留完整范围。", "The heatmap shows the last year of this range; details and trend cover the full range.") : Ui.T("选择方块查看当日详情");
            graphPoints = BuildGraph(result.Points, rows, start, end, first, last, out var granularity);
            chartHint.Text = granularity + Ui.T(" · 缺失记录不会画成零，点击或使用方向键查看详情。", " · Missing records are not plotted as zero. Click or use arrow keys for details.");
            chartDetail.Text = graphPoints.LastOrDefault()?.Description ?? Ui.T("此范围没有完整功率记录。", "No complete power records in this range.");
            UpdateChartStyle();
            feedback.Text = result.Points.Count == 0 ? Ui.T("此范围没有采样记录。", "No sampling records in this range.") : $"{first:yyyy-MM-dd} – {last:yyyy-MM-dd}";
            lastQueryEnglish = Ui.IsEnglish;
            results.Visibility = Visibility.Visible;
            // The scroll extent is not updated until the visible calendar has been measured.
            _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,
                new Action(calendarScroller.ScrollToRightEnd));
        }
        catch (Exception ex) { if (id == request) feedback.Text = Ui.T("查询失败：", "Search failed: ") + ex.Message; }
        finally { if (id == request) { query.IsEnabled = true; export.IsEnabled = true; } }
    }

    private async Task Export()
    {
        if (!Range(out _, out _, out var start, out var end)) return;
        export.IsEnabled = false; feedback.Text = Ui.T("正在导出…", "Exporting…");
        try
        {
            var result = await controller.ExportHistoryAsync(start, end);
            feedback.Text = result.Cancelled ? Ui.T("已取消导出。", "Export cancelled.") : Ui.Diagnostic(result.Message);
            if (!result.Cancelled) controller.Notify(result);
        }
        catch (Exception ex) { feedback.Text = Ui.T("导出失败：", "Export failed: ") + ex.Message; controller.Notify(new OperationResult(false, feedback.Text)); }
        finally { export.IsEnabled = true; }
    }

    private void UpdateChartStyle()
    {
        var selected = controller.Settings.HistoryChart;
        foreach (var pair in chartStyles)
        {
            var active = pair.Key == selected;
            pair.Value.SetResourceReference(Control.BackgroundProperty, active ? "Ui.Accent" : "Ui.Surface");
            pair.Value.SetResourceReference(Control.ForegroundProperty, active ? "Ui.OnAccent" : "Ui.Foreground");
            pair.Value.SetResourceReference(Control.BorderBrushProperty, active ? "Ui.Accent" : "Ui.ControlBorder");
        }
        chart.SetData(graphPoints, selected switch { HistoryChartStyle.Line => HistoryVisualStyle.Line,
            HistoryChartStyle.Smooth => HistoryVisualStyle.Smooth, _ => HistoryVisualStyle.Bars });
    }

    private static DayRow[] BuildRows(IReadOnlyList<HistoryPoint> points,
        IReadOnlyList<(DateTimeOffset From, DateTimeOffset To)> ranges, IReadOnlyList<CoverageSummary> coverage)
    {
        var byDay = points.GroupBy(p => p.Start.ToLocalTime().Date).ToDictionary(g => g.Key, g => g.ToArray());
        var result = new DayRow[ranges.Count];
        for (var i = 0; i < ranges.Count; i++)
        {
            var date = ranges[i].From.ToLocalTime().Date;
            var day = byDay.GetValueOrDefault(date) ?? [];
            result[i] = new DayRow(date, day.Sum(p => p.EstimatedKwh), day.Sum(p => p.CostYuan),
                coverage[i].RecordedSeconds, coverage[i].CompleteSeconds, coverage[i]);
        }
        return result;
    }

    private static HistoryVisualPoint[] BuildGraph(IReadOnlyList<HistoryPoint> points, DayRow[] days,
        DateTimeOffset start, DateTimeOffset end, DateTime first, DateTime last, out string label)
    {
        if (first == last && points.All(p => !p.IsDailyAggregate))
        {
            label = Ui.T("按小时", "By hour");
            var result = new List<HistoryVisualPoint>();
            for (var cursor = start; cursor < end; cursor = cursor.AddHours(1))
            {
                var next = cursor.AddHours(1); var group = points.Where(p => p.Start >= cursor && p.Start < next).ToArray();
                var complete = group.Sum(p => p.CompleteSeconds); var amount = group.Sum(p => p.EstimatedKwh);
                var time = cursor.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
                result.Add(new HistoryVisualPoint(cursor.LocalDateTime, time, complete > 0 ? amount : null,
                    complete > 0 ? $"{time} · {amount:0.000} kWh · {Ui.T("功率完整", "Complete power")} {Duration(complete)}" : time + Ui.T(" · 无完整功率记录", " · No complete power record")));
            }
            return result.ToArray();
        }
        if (days.Length <= 90)
        {
            label = first == last && points.Any(p => p.IsDailyAggregate) ? Ui.T("旧记录仅保留按日汇总", "Older records are available by day only") : Ui.T("按天", "By day");
            return days.Select(d => new HistoryVisualPoint(d.Date, d.Date.ToString("M/d", CultureInfo.CurrentCulture),
                d.Complete > 0 ? d.Energy : null, d.Description)).ToArray();
        }
        label = Ui.T("按月", "By month");
        return days.GroupBy(d => new DateTime(d.Date.Year, d.Date.Month, 1)).OrderBy(g => g.Key).Select(g =>
        {
            var complete = g.Sum(d => d.Complete); var amount = g.Sum(d => d.Energy);
            var text = g.Key.ToString("yyyy-MM", CultureInfo.CurrentCulture);
            return new HistoryVisualPoint(g.Key, g.Key.ToString("yy/MM", CultureInfo.CurrentCulture),
                complete > 0 ? amount : null, complete > 0 ? $"{text} · {amount:0.000} kWh · {Ui.T("功率完整", "Complete power")} {Duration(complete)}" : text + Ui.T(" · 无完整功率记录", " · No complete power record"));
        }).ToArray();
    }

    internal static string Duration(double seconds)
    {
        var rounded = (long)Math.Max(0, Math.Round(seconds));
        var hours = rounded / 3600; var minutes = rounded % 3600 / 60; var remainder = rounded % 60;
        if (Ui.IsEnglish) return hours > 0 ? $"{hours} h {minutes:00} m {remainder:00} s" : minutes > 0 ? $"{minutes} m {remainder:00} s" : $"{remainder} s";
        return hours > 0 ? $"{hours} 小时 {minutes:00} 分 {remainder:00} 秒" :
            minutes > 0 ? $"{minutes} 分 {remainder:00} 秒" : $"{remainder} 秒";
    }

    private sealed record DayRow(DateTime Date, double Energy, decimal Cost, double Recorded, double Complete, CoverageSummary Coverage)
    {
        public string DateText => Date.ToString("yyyy-MM-dd", CultureInfo.CurrentCulture);
        public string EnergyText => Complete > 0 ? $"{Energy:0.000} kWh" : "—";
        public string CostText => Complete > 0 ? $"¥ {Cost:0.00}" : "—";
        public string RecordedText => Coverage.HasTimeConflict ? Ui.T("时间异常，时长未知", "Clock conflict; duration unknown") : Duration(Recorded);
        public string CompleteText => Coverage.HasTimeConflict ? Ui.T("未知", "Unknown") : Duration(Complete);
        public string MissingText => Coverage.Confidence == CoverageConfidence.Observed && Coverage.UnknownSeconds is <= 1
            ? Duration(Coverage.KnownUnrecordedSeconds) : Coverage.KnownUnrecordedSeconds >= 1
                ? Ui.T("至少 ", "At least ") + Duration(Coverage.KnownUnrecordedSeconds) + Ui.T("，其余未知", "; remainder unknown") : Ui.T("未知", "Unknown");
        public string Description => $"{DateText} · {EnergyText} · {CostText}\n{Ui.T("已记录", "Recorded")} {RecordedText} · {Ui.T("功率完整", "Complete power")} {CompleteText}\n{Ui.T("运行中未记录", "Unrecorded while running")} {MissingText}";
        public override string ToString() => Description.Replace('\n', ' ');
    }
}
