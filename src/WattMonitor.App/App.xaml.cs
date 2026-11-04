using System.Diagnostics;
using System.Windows;

namespace WattMonitor.App;

public partial class App : Application
{
    private Mutex? _instance;
    private AppController? _controller;
    private EventWaitHandle? _showRequest;
    private RegisteredWaitHandle? _showRegistration;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        string? dataDirectory = null;
        var dataArg = Array.IndexOf(e.Args, "--data-dir");
        if (dataArg >= 0 && dataArg + 1 < e.Args.Length) dataDirectory = Path.GetFullPath(e.Args[dataArg + 1]);
        // An explicitly isolated data directory must not wake or interfere with the installed instance.
        var identity = dataDirectory is null ? sid : sid + ".isolated." + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dataDirectory.ToUpperInvariant())))[..16];
        _instance = new Mutex(true, @"Local\WattMonitor.App." + identity, out var created);
        if (!created)
        {
            try
            {
                using var request = EventWaitHandle.OpenExisting(@"Local\WattMonitor.Show." + identity);
                request.Set();
            }
            catch (WaitHandleCannotBeOpenedException)
            { MessageBox.Show("WattHalo 正在启动，请稍后从系统托盘打开。", "WattHalo"); }
            Shutdown(); return;
        }
        try
        {
            _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\WattMonitor.Show." + identity);
            _showRegistration = ThreadPool.RegisterWaitForSingleObject(_showRequest,
                (_, _) => Dispatcher.BeginInvoke(() => _controller?.ShowMain()), null, Timeout.Infinite, false);
            _controller = new AppController(dataDirectory);
            _controller.Start(e.Args.Contains("--background"));
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败：" + ex.Message + "\n已有历史数据未删除。", "WattHalo", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _controller?.Dispose();
        _showRegistration?.Unregister(null);
        _showRequest?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
