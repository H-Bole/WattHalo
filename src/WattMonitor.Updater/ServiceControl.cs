using System.ComponentModel;
using System.Runtime.InteropServices;

namespace WattMonitor.Updater;

internal sealed class ServiceControl : IDisposable
{
    private const uint QueryConfig = 1, ChangeConfig = 2, QueryStatus = 4, StartAccess = 16, StopAccess = 32;
    private readonly IntPtr manager, service;
    private ServiceControl(IntPtr manager, IntPtr service) { this.manager = manager; this.service = service; }

    internal static ServiceControl Open(string expectedPath, string ownerSid)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception();
        var service = OpenService(manager, "WattMonitor", QueryConfig | ChangeConfig | QueryStatus | StartAccess | StopAccess);
        if (service == IntPtr.Zero) { CloseServiceHandle(manager); throw new Win32Exception(); }
        var result = new ServiceControl(manager, service);
        try
        {
            QueryServiceConfig(service, IntPtr.Zero, 0, out var length);
            var memory = Marshal.AllocHGlobal((int)length);
            try
            {
                if (!QueryServiceConfig(service, memory, length, out _)) throw new Win32Exception();
                var config = Marshal.PtrToStructure<Config>(memory);
                var command = Marshal.PtrToStringUni(config.BinaryPath);
                var account = Marshal.PtrToStringUni(config.StartName);
                if (!string.Equals(command, '"' + expectedPath + "\" --owner-sid " + ownerSid, StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(account, "LocalSystem", StringComparison.OrdinalIgnoreCase) || config.StartType is < 2 or > 4)
                    throw new InvalidDataException("采集服务配置与本机安装不一致，已停止更新。");
                result.StartType = config.StartType;
            }
            finally { Marshal.FreeHGlobal(memory); }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    internal uint StartType { get; private set; }
    internal uint State
    {
        get
        {
            if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<Status>(), out _)) throw new Win32Exception();
            return status.CurrentState;
        }
    }

    internal void SetStartType(uint type)
    {
        if (type is < 2 or > 4) throw new InvalidDataException("无效的服务启动方式。");
        if (!ChangeServiceConfig(service, uint.MaxValue, type, uint.MaxValue, null, null, IntPtr.Zero, null, null, null, null)) throw new Win32Exception();
    }

    internal void Stop()
    {
        if (State == 1) return;
        if (State != 3 && !ControlService(service, 1, out _)) throw new Win32Exception();
        WaitFor(1);
    }

    internal void Start()
    {
        if (State == 4) return;
        if (State != 2 && !StartService(service, 0, IntPtr.Zero)) throw new Win32Exception();
        WaitFor(4);
    }

    private void WaitFor(uint expected)
    {
        var until = DateTime.UtcNow.AddSeconds(60);
        while (State != expected)
        {
            if (DateTime.UtcNow >= until) throw new TimeoutException("采集服务未在规定时间内完成状态切换。");
            Thread.Sleep(250);
        }
    }
    public void Dispose() { CloseServiceHandle(service); CloseServiceHandle(manager); }

    [StructLayout(LayoutKind.Sequential)] private struct Config
    { public uint ServiceType, StartType, ErrorControl; public IntPtr BinaryPath, LoadOrderGroup; public uint TagId; public IntPtr Dependencies, StartName, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct Status
    { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags; }
    [StructLayout(LayoutKind.Sequential)] private struct BasicStatus
    { public uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(IntPtr service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out Status status, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(IntPtr service, uint control, out BasicStatus status);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartService(IntPtr service, uint count, IntPtr arguments);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl,
        string? binaryPath, string? group, IntPtr tag, string? dependencies, string? startName, string? password, string? displayName);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
