using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using WattMonitor.Core;

namespace WattMonitor.App;

internal static class TelemetryClient
{
    public static async IAsyncEnumerable<TelemetrySnapshot> Read(int refresh, bool isolated,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (isolated)
        {
            using var local = new HardwareSampler();
            var lastProcesses = 0L;
            var deadline = Stopwatch.GetTimestamp();
            var session = Guid.NewGuid().ToString("N");
            while (!cancellationToken.IsCancellationRequested)
            {
                var includeProcesses = lastProcesses == 0 || Stopwatch.GetElapsedTime(lastProcesses).TotalSeconds >= 2;
                var snapshot = local.Sample(includeProcesses);
                if (includeProcesses) lastProcesses = Stopwatch.GetTimestamp();
                yield return snapshot with { Mode = "本机采集 · 独立数据目录", SourceSession = session };
                deadline = await WaitNext(deadline, refresh, cancellationToken);
            }
            yield break;
        }
        HardwareSampler? fallback = null;
        string? fallbackSession = null;
        var useFallbackNext = false;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                NamedPipeClientStream? pipe = null;
                try
                {
                    if (useFallbackNext)
                    {
                        useFallbackNext = false;
                        throw new IOException("采集服务暂不可用，先使用本机采集。");
                    }
                    pipe = new NamedPipeClientStream(".", MonitorProtocol.PipeName, PipeDirection.InOut,
                        PipeOptions.Asynchronous, System.Security.Principal.TokenImpersonationLevel.Identification);
                    await pipe.ConnectAsync(250, cancellationToken);
                    VerifyService(pipe);
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException or InvalidOperationException)
                {
                    pipe?.Dispose();
                    pipe = null;
                }
                if (pipe is not null)
                {
                    var session = Guid.NewGuid().ToString("N");
                    using (pipe)
                    using (var reader = new StreamReader(pipe, leaveOpen: true))
                    using (var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true })
                    {
                        var subscribed = false;
                        try
                        {
                            await writer.WriteLineAsync(JsonSerializer.Serialize(new PipeRequest("subscribe", refresh)).AsMemory(), cancellationToken);
                            subscribed = true;
                        }
                        catch (IOException) { }
                        if (!subscribed) { useFallbackNext = true; continue; }
                        while (!cancellationToken.IsCancellationRequested)
                        {
                            TelemetrySnapshot? snapshot;
                            try
                            {
                                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                                timeout.CancelAfter(TimeSpan.FromSeconds(refresh + 10));
                                var line = await reader.ReadLineAsync(timeout.Token);
                                if (line is null || line.Length > MonitorProtocol.MaxMessageChars) break;
                                snapshot = JsonSerializer.Deserialize<TelemetrySnapshot>(line);
                            }
                            catch (Exception ex) when (!cancellationToken.IsCancellationRequested &&
                                ex is IOException or JsonException or OperationCanceledException) { break; }
                            if (snapshot is not null)
                            {
                                fallback?.Dispose(); fallback = null; fallbackSession = null;
                                yield return snapshot with { SourceSession = session };
                            }
                        }
                    }
                    if (!cancellationToken.IsCancellationRequested) useFallbackNext = true;
                }
                else
                {
                    if (fallback is null) { fallback = new HardwareSampler(); fallbackSession = Guid.NewGuid().ToString("N"); }
                    var retryStarted = Stopwatch.GetTimestamp();
                    var lastProcesses = 0L;
                    var deadline = retryStarted;
                    while (Stopwatch.GetElapsedTime(retryStarted).TotalSeconds < 30 && !cancellationToken.IsCancellationRequested)
                    {
                        var includeProcesses = lastProcesses == 0 || Stopwatch.GetElapsedTime(lastProcesses).TotalSeconds >= 2;
                        var snapshot = fallback.Sample(includeProcesses);
                        if (includeProcesses) lastProcesses = Stopwatch.GetTimestamp();
                        yield return snapshot with { Mode = "本机采集 · 未连接硬件服务", SourceSession = fallbackSession };
                        deadline = await WaitNext(deadline, refresh, cancellationToken);
                    }
                }
            }
        }
        finally { fallback?.Dispose(); }
    }

    // Keep the requested cadence independent of work time; skip missed deadlines instead of spinning.
    private static async Task<long> WaitNext(long previous, int refresh, CancellationToken token)
    {
        var deadline = previous + (long)(Stopwatch.Frequency * (double)refresh);
        var now = Stopwatch.GetTimestamp();
        if (deadline <= now) return now;
        await Task.Delay(TimeSpan.FromSeconds((deadline - now) / (double)Stopwatch.Frequency), token);
        return deadline;
    }

    private static void VerifyService(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var pid))
            throw new IOException("无法验证采集服务身份。");
        // The installer writes this machine-wide key with administrator-only write access.
        using var installation = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WattMonitor");
        var directory = installation?.GetValue("InstallDirectory") as string;
        if (string.IsNullOrWhiteSpace(directory) || !Path.IsPathFullyQualified(directory))
            throw new IOException("尚未配置硬件采集服务安装目录。");
        var expected = Path.GetFullPath(Path.Combine(directory, "service", "WattMonitor.Service.exe"));
        // Standard users may not open a LocalSystem process, even for its image path.
        // SCM grants read-only status/config queries without granting hardware or process access.
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new IOException("无法验证采集服务管理器状态。");
        IntPtr service = IntPtr.Zero, buffer = IntPtr.Zero;
        try
        {
            service = OpenService(manager, MonitorProtocol.ServiceName, 5);
            if (service == IntPtr.Zero || !QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatus>(), out _) ||
                status.State != 4 || status.ProcessId != pid)
                throw new IOException("采集管道与已安装服务的进程不一致。");
            QueryServiceConfig(service, IntPtr.Zero, 0, out var size);
            if (size is 0 or > 65536) throw new IOException("采集服务配置无效。");
            buffer = Marshal.AllocHGlobal((int)size);
            if (!QueryServiceConfig(service, buffer, size, out _)) throw new IOException("无法核对服务路径。");
            var configuration = Marshal.PtrToStructure<ServiceConfig>(buffer);
            var commandLine = Marshal.PtrToStringUni(configuration.BinaryPath) ?? "";
            if (!commandLine.StartsWith("\"" + expected + "\" ", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("采集管道不属于已安装的 WattMonitor 服务。");
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (service != IntPtr.Zero) CloseServiceHandle(service);
            CloseServiceHandle(manager);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint pid);
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus
    { public uint Type, State, Controls, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig
    { public uint Type, Start, Error; public IntPtr BinaryPath, LoadOrderGroup; public uint Tag; public IntPtr Dependencies, StartName, DisplayName; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatus status, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfig(IntPtr service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
