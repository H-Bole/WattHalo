using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using WattMonitor.App.Views;

namespace WattMonitor.App.Controls;

internal sealed class ToastHost : Border
{
    private readonly AppController controller;
    private readonly Queue<OperationResult> pending = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TextBlock message = Ui.Text("", 14);
    private readonly Button location;
    private OperationResult? current;
    private Window? owner;
    private double remaining;

    public ToastHost(AppController controller)
    {
        this.controller = controller;
        HorizontalAlignment = HorizontalAlignment.Right; VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(20); Padding = new Thickness(14); MaxWidth = 430; CornerRadius = new CornerRadius(10);
        SetResourceReference(BackgroundProperty, "Ui.Surface"); SetResourceReference(BorderBrushProperty, "Ui.Line"); BorderThickness = new Thickness(1);
        Visibility = Visibility.Collapsed;
        var content = new StackPanel();
        var header = new DockPanel(); var dismiss = Ui.Button("×", Dismiss); dismiss.MinHeight = 28; dismiss.Padding = new Thickness(8, 2, 8, 2); dismiss.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(dismiss, Dock.Right); header.Children.Add(dismiss); header.Children.Add(message); content.Children.Add(header);
        location = Ui.Button("打开文件位置", OpenLocation); location.MinHeight = 30; location.Padding = new Thickness(9, 4, 9, 4); location.HorizontalAlignment = HorizontalAlignment.Left; content.Children.Add(location);
        Child = content;
        System.Windows.Automation.AutomationProperties.SetLiveSetting(this, System.Windows.Automation.AutomationLiveSetting.Polite);
        timer.Tick += (_, _) =>
        {
            remaining -= timer.Interval.TotalSeconds;
            if (remaining <= 0) Dismiss();
        };
        Loaded += (_, _) =>
        {
            controller.Notice += Notice;
            owner = Window.GetWindow(this);
            if (owner is not null) { owner.IsVisibleChanged += OwnerVisibilityChanged; owner.StateChanged += OwnerStateChanged; }
            UpdateTimer();
        };
        Unloaded += (_, _) =>
        {
            controller.Notice -= Notice;
            if (owner is not null) { owner.IsVisibleChanged -= OwnerVisibilityChanged; owner.StateChanged -= OwnerStateChanged; }
            owner = null; timer.Stop();
        };
        IsVisibleChanged += (_, _) => UpdateTimer();
        IsKeyboardFocusWithinChanged += (_, _) => UpdateTimer();
        MouseEnter += (_, _) => UpdateTimer(); MouseLeave += (_, _) => UpdateTimer();
    }

    private void OwnerVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => UpdateTimer();
    private void OwnerStateChanged(object? sender, EventArgs e) => UpdateTimer();
    private void UpdateTimer()
    {
        if (IsLoaded && IsVisible && owner is { IsVisible: true, WindowState: not WindowState.Minimized } && current is { Success: true } && !IsMouseOver && !IsKeyboardFocusWithin)
        { if (!timer.IsEnabled) timer.Start(); }
        else timer.Stop();
    }

    private void Notice(OperationResult result)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => Notice(result)); return; }
        var target = System.Windows.Application.Current.Windows.OfType<Window>().Where(w => w.IsVisible && w.WindowState != WindowState.Minimized && w is (MainWindow or PanelWindow)).OrderByDescending(w => w.IsActive).FirstOrDefault();
        if (target != owner) return;
        if (result.Cancelled || string.IsNullOrWhiteSpace(result.Message)) return;
        if ((current?.Message == result.Message && current.Success == result.Success) || pending.Any(x => x.Message == result.Message && x.Success == result.Success)) return;
        pending.Enqueue(result);
        if (current is null) ShowNext();
    }

    private void ShowNext()
    {
        if (!pending.TryDequeue(out var next)) { current = null; Visibility = Visibility.Collapsed; UpdateTimer(); return; }
        current = next; remaining = 4; message.Text = next.Message;
        location.Visibility = string.IsNullOrEmpty(next.Path) ? Visibility.Collapsed : Visibility.Visible;
        Visibility = Visibility.Visible; UpdateTimer();
    }

    private void Dismiss() { current = null; ShowNext(); }
    private void OpenLocation()
    {
        if (current?.Path is not { } path) return;
        try
        {
            var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add("/select," + path); Process.Start(start);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            current = new OperationResult(false, "无法打开文件位置：" + ex.Message);
            message.Text = current.Message; location.Visibility = Visibility.Collapsed; UpdateTimer();
        }
    }
}
