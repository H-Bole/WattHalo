using System.Windows;
using System.Windows.Controls;

namespace WattMonitor.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--recover")
        {
            try { new UpdateEngine(_ => { }).Recover(); return 0; }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("WattHalo 更新恢复失败：{0}", ex); return 1; }
        }
        if (args.Length == 3 && args[0] == "--watch" && int.TryParse(args[1], out var process) && long.TryParse(args[2], out var start))
        {
            try { new UpdateEngine(_ => { }).Watch(process, start); return 0; }
            catch (Exception ex) { System.Diagnostics.Trace.TraceError("WattHalo 更新监护失败：{0}", ex); return 1; }
        }
        if (args.Length != 3 || args[1] != "--ready") return 1;
        var application = new Application();
        var status = new TextBlock { Text = "正在准备更新…", Margin = new Thickness(24), TextWrapping = TextWrapping.Wrap };
        var progress = new ProgressBar { IsIndeterminate = true, Height = 8, Margin = new Thickness(24, 0, 24, 24) };
        var panel = new StackPanel(); panel.Children.Add(status); panel.Children.Add(progress);
        var window = new Window { Title = "WattHalo 更新", Content = panel, Width = 480, Height = 170,
            WindowStartupLocation = WindowStartupLocation.CenterScreen, ResizeMode = ResizeMode.NoResize };
        var done = false;
        window.Closing += (_, e) => { if (!done) e.Cancel = true; };
        window.Loaded += async (_, _) =>
        {
            try
            {
                await Task.Run(() => new UpdateEngine(text => window.Dispatcher.BeginInvoke(() => status.Text = text)).Run(args[0], args[2]));
                done = true; progress.IsIndeterminate = false; progress.Value = 100;
            }
            catch (Exception ex)
            {
                done = true; progress.IsIndeterminate = false;
                MessageBox.Show(ex.Message, "WattHalo 更新未完成", MessageBoxButton.OK, MessageBoxImage.Error);
                window.Close();
            }
        };
        application.Run(window);
        return 0;
    }
}
