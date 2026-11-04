using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;

namespace WattMonitor.App.Controls;

internal sealed record HistoryVisualDay(DateTime Date, double? Energy, decimal? Cost, string Description);
internal sealed record HistoryVisualPoint(DateTime Start, string Label, double? Energy, string Description);
internal enum HistoryVisualStyle { Bars, Line, Smooth }

// Drawn elements keep a year's worth of cells lightweight. The details remain available
// through the adjacent table and through mouse/keyboard selection, not colour alone.
internal sealed class EnergyCalendar : FrameworkElement
{
    private const double Cell = 19, ShortCell = 46, LeftInset = 32, TopInset = 23;
    private IReadOnlyList<HistoryVisualDay> days = [];
    private int offset, selected = -1;
    private bool ShortRange => days.Count is > 0 and <= 14;
    public event Action<HistoryVisualDay>? DaySelected;

    public EnergyCalendar()
    {
        Height = 165;
        Focusable = true;
        Cursor = Cursors.Hand;
        AutomationProperties.SetName(this, Ui.T("每日用电热力图，可使用左右方向键查看日期", "Daily energy heatmap. Use arrow keys to inspect dates."));
        MouseMove += (_, e) => Inspect(e.GetPosition(this), false);
        MouseLeftButtonDown += (_, e) => { Focus(); Inspect(e.GetPosition(this), true); };
        KeyDown += OnKeyDown;
        Loaded += (_, _) => Ui.ThemeChanged += ThemeChanged;
        Unloaded += (_, _) => Ui.ThemeChanged -= ThemeChanged;
        Loaded += (_, _) => Ui.LanguageChanged += ThemeChanged;
        Unloaded += (_, _) => Ui.LanguageChanged -= ThemeChanged;
    }

    public void SetDays(IReadOnlyList<HistoryVisualDay> value)
    {
        days = value;
        offset = days.Count == 0 ? 0 : ((int)days[0].Date.DayOfWeek + 6) % 7;
        Height = ShortRange ? 78 : 165;
        Width = Math.Max(360, LeftInset + (ShortRange ? days.Count * ShortCell : Math.Ceiling((days.Count + offset) / 7d) * Cell) + 12);
        selected = days.Count == 0 ? -1 : days.Count - 1;
        if (IsVisible) InvalidateVisual();
    }

    private void ThemeChanged(object? sender, EventArgs e) { if (IsVisible) InvalidateVisual(); }
    private void Inspect(Point p, bool select)
    {
        var column = (int)Math.Floor((p.X - LeftInset) / (ShortRange ? ShortCell : Cell));
        var row = (int)Math.Floor((p.Y - TopInset) / (ShortRange ? 32 : Cell));
        var index = ShortRange ? column : column * 7 + row - offset;
        if (p.X < LeftInset || p.Y < TopInset || row < 0 || row > (ShortRange ? 0 : 6) || index < 0 || index >= days.Count)
        {
            ToolTip = null;
            return;
        }
        ToolTip = days[index].Description;
        if (select) Select(index);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (days.Count == 0) return;
        var step = e.Key switch { Key.Left => ShortRange ? -1 : -7, Key.Right => ShortRange ? 1 : 7, Key.Up => -1, Key.Down => 1, _ => 0 };
        if (step == 0 && e.Key is not (Key.Home or Key.End or Key.Enter or Key.Space)) return;
        if (e.Key == Key.Home) Select(0);
        else if (e.Key == Key.End) Select(days.Count - 1);
        else if (e.Key is Key.Enter or Key.Space) Select(Math.Max(0, selected));
        else Select(Math.Clamp(selected + step, 0, days.Count - 1));
        var column = ShortRange ? selected : (selected + offset) / 7;
        var row = ShortRange ? 0 : (selected + offset) % 7;
        BringIntoView(new Rect(LeftInset + column * (ShortRange ? ShortCell : Cell), TopInset + row * Cell,
            ShortRange ? 24 : 15, ShortRange ? 24 : 15));
        e.Handled = true;
    }

    private void Select(int index)
    {
        selected = index;
        ToolTip = days[index].Description;
        AutomationProperties.SetName(this, days[index].Description);
        DaySelected?.Invoke(days[index]);
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Label(string value, double x, double y) => dc.DrawText(new FormattedText(value,
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Ui.Muted, dpi), new Point(x, y));
        if (!ShortRange)
        {
            Label(Ui.T("一", "M"), 9, TopInset - 1); Label(Ui.T("三", "W"), 9, TopInset + 2 * Cell - 1); Label(Ui.T("五", "F"), 9, TopInset + 4 * Cell - 1);
        }
        if (days.Count == 0) { Label(Ui.T("暂无用电记录"), LeftInset, TopInset); return; }
        var maximum = days.Where(d => d.Energy.HasValue).Select(d => d.Energy!.Value).DefaultIfEmpty(0).Max();
        var outline = new Pen(Ui.Line, 1);
        var focus = new Pen(Ui.Accent, 1.5);
        var lastMonth = -1;
        for (var i = 0; i < days.Count; i++)
        {
            var day = days[i]; var col = (i + offset) / 7; var row = (i + offset) % 7;
            var x = LeftInset + (ShortRange ? i * ShortCell : col * Cell); var y = TopInset + (ShortRange ? 0 : row * Cell);
            if (ShortRange) Label(day.Date.ToString("M/d", CultureInfo.InvariantCulture), x, 2);
            else if (day.Date.Month != lastMonth && (day.Date.Day <= 7 || i == 0))
            {
                Label(day.Date.ToString("MMM", CultureInfo.GetCultureInfo(Ui.IsEnglish ? "en-US" : "zh-CN")), x, 2);
                lastMonth = day.Date.Month;
            }
            var size = ShortRange ? 24 : 15;
            var rect = new Rect(x, y, size, size);
            if (day.Energy is { } value)
            {
                var brush = Ui.Accent.Clone();
                brush.Opacity = maximum <= 0 ? .16 : .16 + .76 * Math.Clamp(value / maximum, 0, 1);
                brush.Freeze();
                dc.DrawRoundedRectangle(brush, null, rect, 2, 2);
            }
            else
            {
                dc.DrawRoundedRectangle(Ui.Background, outline, rect, 2, 2);
                dc.DrawLine(outline, new Point(x + 3, y + size - 3), new Point(x + size - 3, y + 3));
            }
            if (i == selected && IsKeyboardFocusWithin) dc.DrawRoundedRectangle(null, focus, new Rect(x - 1, y - 1, size + 2, size + 2), 2, 2);
        }
    }
}

internal sealed class EnergyHistoryChart : FrameworkElement
{
    private const double LeftInset = 54, RightInset = 12, TopInset = 14, BottomInset = 32;
    private IReadOnlyList<HistoryVisualPoint> points = [];
    private HistoryVisualStyle style;
    private int selected = -1;
    public event Action<HistoryVisualPoint>? PointSelected;

    public EnergyHistoryChart()
    {
        Height = 205;
        MinWidth = 180;
        Focusable = true;
        AutomationProperties.SetName(this, Ui.T("用电趋势图，可使用左右方向键查看数据", "Energy chart. Use arrow keys to inspect data."));
        MouseMove += (_, e) => Inspect(e.GetPosition(this), false);
        MouseLeftButtonDown += (_, e) => { Focus(); Inspect(e.GetPosition(this), true); };
        KeyDown += OnKeyDown;
        Loaded += (_, _) => Ui.ThemeChanged += ThemeChanged;
        Unloaded += (_, _) => Ui.ThemeChanged -= ThemeChanged;
        Loaded += (_, _) => Ui.LanguageChanged += ThemeChanged;
        Unloaded += (_, _) => Ui.LanguageChanged -= ThemeChanged;
    }

    public void SetData(IReadOnlyList<HistoryVisualPoint> value, HistoryVisualStyle chartStyle)
    {
        points = value; style = chartStyle;
        selected = points.Count == 0 ? -1 : Math.Clamp(selected < 0 ? points.Count - 1 : selected, 0, points.Count - 1);
        if (IsVisible) InvalidateVisual();
    }

    private void ThemeChanged(object? sender, EventArgs e) { if (IsVisible) InvalidateVisual(); }
    private double X(int index) => LeftInset + (points.Count <= 1 ? .5 : index / (double)(points.Count - 1)) * Math.Max(1, ActualWidth - LeftInset - RightInset);
    private void Inspect(Point point, bool select)
    {
        if (points.Count == 0 || point.X < LeftInset || point.X > ActualWidth - RightInset) { ToolTip = null; return; }
        var index = points.Count == 1 ? 0 : Math.Clamp((int)Math.Round((point.X - LeftInset) /
            Math.Max(1, ActualWidth - LeftInset - RightInset) * (points.Count - 1)), 0, points.Count - 1);
        ToolTip = points[index].Description;
        if (select) Select(index);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (points.Count == 0) return;
        if (e.Key is not (Key.Left or Key.Right or Key.Home or Key.End or Key.Enter or Key.Space)) return;
        var index = e.Key switch { Key.Home => 0, Key.End => points.Count - 1, Key.Left => selected - 1, Key.Right => selected + 1, _ => selected };
        Select(Math.Clamp(index, 0, points.Count - 1)); e.Handled = true;
    }

    private void Select(int index)
    {
        selected = index; ToolTip = points[index].Description;
        AutomationProperties.SetName(this, points[index].Description);
        PointSelected?.Invoke(points[index]); InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var width = Math.Max(1, ActualWidth - LeftInset - RightInset);
        var height = Math.Max(1, ActualHeight - TopInset - BottomInset);
        var bottom = TopInset + height;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        void Label(string value, double x, double y, Brush brush) => dc.DrawText(new FormattedText(value,
            CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, brush, dpi), new Point(x, y));
        var max = points.Where(p => p.Energy.HasValue).Select(p => p.Energy!.Value).DefaultIfEmpty(0).Max();
        var scale = max > 0 ? NiceMaximum(max) : 1;
        var grid = new Pen(Ui.Line, 1);
        for (var i = 0; i <= 2; i++)
        {
            var y = bottom - height * i / 2;
            dc.DrawLine(grid, new Point(LeftInset, y), new Point(LeftInset + width, y));
            Label((scale * i / 2).ToString("0.##", CultureInfo.CurrentCulture), 2, y - 7, Ui.Muted);
        }
        Label("kWh", 2, 0, Ui.Muted);
        if (points.Count == 0) { Label(Ui.T("暂无用电记录"), LeftInset + 10, TopInset + 16, Ui.Muted); return; }
        double Y(double v) => bottom - Math.Clamp(v / scale, 0, 1) * height;
        if (style == HistoryVisualStyle.Bars)
        {
            var barWidth = Math.Clamp(width / Math.Max(points.Count, 1) * .6, 2, 30);
            for (var i = 0; i < points.Count; i++)
                if (points[i].Energy is { } v)
                    dc.DrawRoundedRectangle(Ui.Accent, null, new Rect(X(i) - barWidth / 2, Y(v), barWidth, Math.Max(1, bottom - Y(v))), 2, 2);
        }
        else
        {
            var pen = new Pen(Ui.Accent, 2);
            for (var i = 1; i < points.Count; i++)
            {
                if (points[i - 1].Energy is not { } a || points[i].Energy is not { } b) continue;
                var first = new Point(X(i - 1), Y(a)); var next = new Point(X(i), Y(b));
                if (style == HistoryVisualStyle.Line) dc.DrawLine(pen, first, next);
                else
                {
                    var path = new StreamGeometry();
                    using (var ctx = path.Open())
                    {
                        var mid = (first.X + next.X) / 2;
                        ctx.BeginFigure(first, false, false);
                        // Control points stay within the neighbouring readings, preventing overshoot.
                        ctx.BezierTo(new Point(mid, first.Y), new Point(mid, next.Y), next, true, false);
                    }
                    path.Freeze(); dc.DrawGeometry(null, pen, path);
                }
            }
            if (points.Count == 1 && points[0].Energy is { } one) dc.DrawEllipse(Ui.Accent, null, new Point(X(0), Y(one)), 3, 3);
        }
        var labels = Math.Min(points.Count, Math.Max(2, (int)(width / 100)));
        for (var j = 0; j < labels; j++)
        {
            var i = labels == 1 ? 0 : (int)Math.Round(j * (points.Count - 1d) / (labels - 1));
            var formatted = new FormattedText(points[i].Label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 11, Ui.Muted, dpi);
            var x = Math.Clamp(X(i) - formatted.Width / 2, LeftInset, Math.Max(LeftInset, ActualWidth - RightInset - formatted.Width));
            dc.DrawText(formatted, new Point(x, bottom + 7));
        }
        if (selected >= 0 && selected < points.Count && IsKeyboardFocusWithin)
        {
            var x = X(selected); dc.DrawLine(new Pen(Ui.Muted, 1), new Point(x, TopInset), new Point(x, bottom));
            if (points[selected].Energy is { } value) dc.DrawEllipse(Ui.Accent, null, new Point(x, Y(value)), 4, 4);
        }
    }

    private static double NiceMaximum(double value)
    {
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        return (normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10) * magnitude;
    }
}
