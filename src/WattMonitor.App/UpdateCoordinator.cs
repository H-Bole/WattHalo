using System.ComponentModel;
using System.Net;
using System.Net.Http;
using Microsoft.Win32;
using WattMonitor.App.Controls;
using WattMonitor.App.Updates;

namespace WattMonitor.App;

internal enum UpdateStage { Idle, Checking, Current, Available, Downloading, Ready, Preparing, Failed }

// Network checks are read-only. An update is downloaded only after the user chooses it.
internal sealed class UpdateCoordinator : IDisposable
{
    private readonly UpdateClient client = new();
    private readonly CancellationTokenSource lifetime = new();
    private DownloadedUpdate? package;
    private bool disposed;
    private int progress;
    private string? error;
    internal event EventHandler? Changed;
    internal UpdateStage Stage { get; private set; } = UpdateStage.Idle;
    internal UpdateRelease? Release { get; private set; }
    internal bool Busy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Preparing;
    internal int Progress => progress;
    internal string Notes => Release?.Notes ?? "";
    internal bool RecoveryPending
    {
        get
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WattMonitor");
                var installed = key?.GetValue("InstallDirectory") as string;
                if (string.IsNullOrWhiteSpace(installed)) return false;
                var full = Path.GetFullPath(installed).TrimEnd('\\');
                if (!string.Equals(full, Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) return false;
                var parent = Path.GetDirectoryName(full);
                if (parent is null) return false;
                var host = Path.Combine(parent, "WattMonitor.UpdateHost");
                return Directory.Exists(Path.Combine(host, "transaction")) || Directory.Exists(Path.Combine(host, "cleanup"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            { return false; }
        }
    }
    internal string CurrentVersion
    {
        get
        {
            var version = typeof(UpdateCoordinator).Assembly.GetName().Version;
            return version is null ? "—" : $"{version.Major}.{version.Minor}.{Math.Max(0, version.Build)}";
        }
    }

    internal string Status => Stage switch
    {
        UpdateStage.Checking => Ui.T("正在检查更新…", "Checking for updates…"),
        UpdateStage.Current => Ui.T("已经是最新版本。", "You're up to date."),
        UpdateStage.Available => string.Format(Ui.T("发现新版本 {0}", "Version {0} is available"), Release?.Version),
        UpdateStage.Downloading => string.Format(Ui.T("正在下载更新… {0}%", "Downloading update… {0}%"), progress),
        UpdateStage.Ready => Ui.T("更新已下载，等待继续操作。", "Update downloaded; waiting for the next action."),
        UpdateStage.Preparing => Ui.T("正在安全准备更新…", "Preparing the update securely…"),
        UpdateStage.Failed => Ui.T("更新检查或下载失败：", "Update check or download failed: ") + error,
        _ => Ui.T("尚未检查更新。", "Updates have not been checked yet.")
    };

    internal async Task<bool> CheckAsync()
    {
        if (disposed || Busy) return false;
        Stage = UpdateStage.Checking; error = null; Raise();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            var release = await client.CheckAsync(timeout.Token);
            if (disposed) return false;
            package?.Dispose(); package = null;
            Release = release;
            Stage = release is null ? UpdateStage.Current : UpdateStage.Available;
            Raise();
            return release is not null;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or
            ArgumentException or FormatException or KeyNotFoundException or OperationCanceledException)
        {
            if (disposed) return false;
            Release = null;
            error = ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                ? Ui.T("尚无可用的正式发行版。", "No public release is available yet.")
                : ex is OperationCanceledException ? Ui.T("请求超时。", "The request timed out.") : ex.Message;
            Stage = UpdateStage.Failed; Raise(); return false;
        }
    }

    internal async Task<bool> DownloadAsync()
    {
        if (disposed || Busy || Release is null) return false;
        Stage = UpdateStage.Downloading; progress = 0; error = null; Raise();
        try
        {
            var reporter = new Progress<double>(value =>
            {
                var next = (int)Math.Clamp(Math.Floor(value * 100), 0, 100);
                if (disposed || Stage != UpdateStage.Downloading || next == progress) return;
                progress = next; Raise();
            });
            package?.Dispose(); package = null;
            package = await client.DownloadAsync(Release, reporter, lifetime.Token);
            if (disposed) { package.Dispose(); package = null; return false; }
            Stage = UpdateStage.Ready; Raise(); return true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return false; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException or
            ArgumentException or FormatException or KeyNotFoundException or OperationCanceledException)
        {
            if (disposed) return false;
            error = ex.Message; Stage = UpdateStage.Failed; Raise(); return false;
        }
    }

    internal async Task<(bool Success, string? Failure)> LaunchAsync()
    {
        if (disposed || package is null || Stage != UpdateStage.Ready) return (false, null);
        Stage = UpdateStage.Preparing; Raise();
        try
        {
            await package.LaunchUpdaterAsync(lifetime.Token);
            // READY means the elevated helper has independently verified and staged its own copy.
            package = null;
            return (true, null);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return (false, null); }
        catch (Exception ex) when (ex is Win32Exception or IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or
            NotSupportedException or System.Security.SecurityException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or
            ArgumentException or FormatException or KeyNotFoundException or OperationCanceledException)
        {
            package = null;
            if (disposed) return (false, null);
            error = ex is OperationCanceledException ? Ui.T("更新准备超时或已取消。", "Update preparation timed out or was cancelled.") : ex.Message;
            Stage = UpdateStage.Failed; Raise();
            return (false, error);
        }
    }

    private void Raise() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; lifetime.Cancel(); lifetime.Dispose();
        try { package?.Dispose(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        { System.Diagnostics.Trace.TraceWarning("无法清理更新下载目录：{0}", ex.Message); }
        package = null;
    }
}
