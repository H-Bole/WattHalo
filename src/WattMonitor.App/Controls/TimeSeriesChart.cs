using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace WattMonitor.App.Controls;

internal sealed class TimeSeriesChart : FrameworkElement
{
    private (DateTimeOffset Time, double? Value)[] samples = [];
    private DateTimeOffset end;
    private TimeSpan duration = TimeSpan.FromMinutes(5);
    private TimeSpan gap = TimeSpan.FromSeconds(5);
    private string unit = "";
    private bool percentage;
    private double? hoverX;
    private readonly ToolTip tooltip = new();

    public TimeSeriesChart()
    {
        Height = 210;
        MinWidth = 180;
        Focusable = true;
        Ui.Bind(this, System.Windows.Automation.AutomationProperties.NameProperty, "实时趋势图，移动鼠标查看采样时间与数值");
        Loaded += (_, _) => Ui.ThemeChanged += ThemeChanged;
        Unloaded += (_, _) => Ui.ThemeChanged -= ThemeChanged;
        Loaded += (_, _) => Ui.LanguageChanged += ThemeChanged;
        Unloaded += (_, _) => Ui.LanguageChanged -= ThemeChanged;
        IsVisibleChanged += (_, _) => { if (!IsVisible) { hoverX = null; SetTooltip(null); } };
    }

    public void SetData(IEnumerable<(DateTimeOffset Time, double? Value)> values, int minutes, double gapSeconds, string label, bool percent)
    {
        end = DateTimeOffset.Now;
        duration = TimeSpan.FromMinutes(minutes);
        gap = TimeSpan.FromSeconds(gapSeconds);
        unit = label;
        percentage = percent;
        samples = values.Where(p => p.Time >= end - duration && p.Time <= end).OrderBy(p => p.Time).ToArray();
        if (IsVisible) InvalidateVisual();
    }

    private void ThemeChanged(object? sender, EventArgs e) { if (IsVisible) InvalidateVisual(); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); hoverX = e.GetPosition(this).X; if (IsVisible) InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { base.OnMouseLeave(e); hoverX = null; SetTooltip(null); if (IsVisible) InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        if (!IsVisible || ActualWidth < 100 || ActualHeight < 60) return;
        var rect = new Rect(54, 10, ActualWidth - 65, ActualHeight - 40);
        var start = end - duration;
        var observed = 0d; var hasValid = false;
        foreach (var sample in samples)
            if (sample.Value is { } n && double.IsFinite(n) && n >= 0) { hasValid = true; observed = Math.Max(observed, n); }
        var max = percentage ? 100 : Math.Max(unit == "MB/s" ? .001 : 1, observed * 1.1);
        double X(DateTimeOffset t) => rect.Left + (t - start).TotalSeconds / duration.TotalSeconds * rect.Width;
        double Y(double n) => rect.Bottom - Math.Clamp(n / max, 0, 1) * rect.Height;
        var linePen = new Pen(Ui.Line, 1);
        var divisions = ActualHeight < 130 ? 2 : 4;
        for (var i = 0; i <= divisions; i++)
        {
            var y = rect.Top + rect.Height * i / divisions;
            dc.DrawLine(linePen, new Point(rect.Left, y), new Point(rect.Right, y));
            DrawText(dc, (max * (divisions - i) / divisions).ToString(max < .1 ? "0.####" : max < 1 ? "0.###" : "0.#", CultureInfo.CurrentCulture), new Point(0, y - 7));
        }
        DrawText(dc, start.ToLocalTime().ToString("HH:mm"), new Point(rect.Left, rect.Bottom + 8));
        DrawText(dc, (start + duration / 2).ToLocalTime().ToString("HH:mm"), new Point(rect.Left + rect.Width / 2 - 18, rect.Bottom + 8));
        DrawText(dc, end.ToLocalTime().ToString("HH:mm"), new Point(rect.Right - 34, rect.Bottom + 8));
        DrawText(dc, unit, new Point(0, rect.Bottom + 8));
        if (!hasValid)
        {
            DrawText(dc, Ui.T("等待可用采样"), new Point(rect.Left + 10, rect.Top + rect.Height / 2));
            SetTooltip(hoverX is { } emptyX && emptyX >= rect.Left && emptyX <= rect.Right ? (start + TimeSpan.FromSeconds((emptyX - rect.Left) / rect.Width * duration.TotalSeconds)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + Ui.T("  ·  此时无采样", "  ·  No sample at this time") : null);
            return;
        }

        var pixels = Math.Max(1, (int)Math.Ceiling(rect.Width * VisualTreeHelper.GetDpi(this).DpiScaleX));
        var pen = new Pen(Ui.Accent, 1.8);
        Point? previous = null;
        DateTimeOffset? previousTime = null;
        var bucket = -1;
        double first = 0, last = 0, low = 0, high = 0;
        void Flush()
        {
            if (bucket < 0) return;
            var x = rect.Left + (bucket + .5) / pixels * rect.Width;
            var head = new Point(x, Y(first));
            if (previous.HasValue) dc.DrawLine(pen, previous.Value, head);
            else dc.DrawEllipse(Ui.Accent, null, head, 1.5, 1.5);
            if (high != low) dc.DrawLine(pen, new Point(x, Y(low)), new Point(x, Y(high)));
            previous = new Point(x, Y(last));
            bucket = -1;
        }
        foreach (var sample in samples)
        {
            if (previousTime.HasValue && sample.Time - previousTime.Value > gap) { Flush(); previous = null; }
            previousTime = sample.Time;
            if (sample.Value is not { } number || !double.IsFinite(number) || number < 0) { Flush(); previous = null; continue; }
            var next = Math.Clamp((int)((sample.Time - start).TotalSeconds / duration.TotalSeconds * pixels), 0, pixels - 1);
            if (next != bucket) { Flush(); bucket = next; first = last = low = high = number; }
            else { last = number; low = Math.Min(low, number); high = Math.Max(high, number); }
        }
        Flush();

        if (hoverX is not { } hx || hx < rect.Left || hx > rect.Right) { SetTooltip(null); return; }
        var at = start + TimeSpan.FromSeconds((hx - rect.Left) / rect.Width * duration.TotalSeconds);
        var lo = 0; var hi = samples.Length;
        while (lo < hi) { var middle = lo + (hi - lo) / 2; if (samples[middle].Time < at) lo = middle + 1; else hi = middle; }
        var index = lo == samples.Length ? lo - 1 : lo > 0 && at - samples[lo - 1].Time <= samples[lo].Time - at ? lo - 1 : lo;
        var nearest = samples[index];
        if (Math.Abs((nearest.Time - at).TotalSeconds) > gap.TotalSeconds / 2 || nearest.Value is not { } value || !double.IsFinite(value) || value < 0)
        {
            SetTooltip(at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") + Ui.T("  ·  此时无采样", "  ·  No sample at this time"));
            return;
        }
        var point = new Point(X(nearest.Time), Y(value));
        dc.DrawLine(new Pen(Ui.Muted, 1) { DashStyle = DashStyles.Dot }, new Point(point.X, rect.Top), new Point(point.X, rect.Bottom));
        dc.DrawEllipse(Ui.Accent, null, point, 3, 3);
        SetTooltip($"{nearest.Time.ToLocalTime():yyyy-MM-dd HH:mm:ss}  ·  {value:0.####} {unit}");
    }

    private void SetTooltip(string? text)
    {
        if (text is null) { if (ToolTip is not null) ToolTip = null; return; }
        if (!Equals(tooltip.Content, text)) tooltip.Content = text;
        if (!ReferenceEquals(ToolTip, tooltip)) ToolTip = tooltip;
    }

    private void DrawText(DrawingContext dc, string text, Point at) => dc.DrawText(new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Segoe UI"), 12, Ui.Muted, VisualTreeHelper.GetDpi(this).PixelsPerDip), at);
}
