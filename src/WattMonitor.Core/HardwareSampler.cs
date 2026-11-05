using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace WattMonitor.Core;

public sealed class HardwareSampler : IDisposable
{
    private readonly Computer computer = new() { IsCpuEnabled = true };
    private readonly GpuCounters gpuCounters = new();
    private readonly Nvml nvml = new();
    private readonly Dictionary<int, (long Start, TimeSpan Cpu, long Clock)> processes = [];
    private readonly Dictionary<string, (long Rx, long Tx)> networks = [];
    private readonly PerformanceCounter? diskRead, diskWrite;
    private ulong oldIdle, oldKernel, oldUser;
    private bool hasCpuBaseline;
    private long oldClock;
    private string? hardwareError;
    private ProcessSample[] lastProcesses = [];
    private bool disposed;

    public HardwareSampler()
    {
        try { computer.Open(); } catch (Exception e) { hardwareError = "CPU传感器初始化失败：" + e.Message; }
        try
        {
            diskRead = new("PhysicalDisk", "Disk Read Bytes/sec", "_Total", true);
            diskWrite = new("PhysicalDisk", "Disk Write Bytes/sec", "_Total", true);
            diskRead.NextValue(); diskWrite.NextValue();
        }
        catch { diskRead?.Dispose(); diskWrite?.Dispose(); diskRead = diskWrite = null; }
        hasCpuBaseline = GetSystemTimes(out oldIdle, out oldKernel, out oldUser);
        oldClock = Stopwatch.GetTimestamp();
    }

    public TelemetrySnapshot Sample(bool includeProcesses = true)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var warnings = new List<string>();
        var clock = Stopwatch.GetTimestamp();
        var elapsed = Stopwatch.GetElapsedTime(oldClock, clock).TotalSeconds;
        double? cpu = null;
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            if (hasCpuBaseline && idle >= oldIdle && kernel >= oldKernel && user >= oldUser)
            {
                var total = (kernel - oldKernel) + (user - oldUser);
                if (total > 0) cpu = Math.Clamp(100d * (total - Math.Min(total, idle - oldIdle)) / total, 0, 100);
            }
            (oldIdle, oldKernel, oldUser) = (idle, kernel, user);
            hasCpuBaseline = true;
        }
        else { hasCpuBaseline = false; warnings.Add("系统CPU占用读取失败"); }
        oldClock = clock;
        var cpuName = "CPU";
        double? cpuWatts = null;
        var cpuStart = Stopwatch.GetTimestamp();
        try
        {
            foreach (var hardware in computer.Hardware)
            {
                hardware.Update();
                if (hardware.HardwareType != HardwareType.Cpu) continue;
                cpuName = hardware.Name;
                var package = hardware.Sensors.FirstOrDefault(s => s.SensorType == SensorType.Power && s.Name.Equals("CPU Package", StringComparison.OrdinalIgnoreCase));
                if (package?.Value is > 0 && float.IsFinite(package.Value.Value)) cpuWatts = (cpuWatts ?? 0) + package.Value.Value;
            }
        }
        catch (Exception e) { hardwareError = "CPU传感器读取失败：" + e.Message; }
        var cpuEnd = Stopwatch.GetTimestamp();
        if (cpuWatts == null) warnings.Add(hardwareError ?? "CPU Package功率不可用；请确认PawnIO驱动、CPU支持情况及采集服务权限");
        var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref memory)) warnings.Add("系统内存读取失败");
        var gpuStart = Stopwatch.GetTimestamp();
        var counters = gpuCounters.Read(warnings);
        var gpus = nvml.Read(warnings).ToList();
        // DXGI can expose inactive mirrored adapters. Prefer the active LUID for each physical PCI device.
        var discoveredAdapters = DxgiAdapters.Read();
        if (discoveredAdapters is null) warnings.Add("无法完整识别显卡，主机功率估算暂不可用。");
        var adapters = (discoveredAdapters ?? []).GroupBy(a => a.PciKey ?? a.Id)
            .Select(group => group.OrderByDescending(a => counters.Adapters.ContainsKey(a.Id)).First()).ToArray();
        var matched = new HashSet<int>();
        foreach (var adapter in adapters)
        {
            counters.Adapters.TryGetValue(adapter.Id, out var data);
            var matches = gpus.Select((g, i) => (g, i)).Where(x => !matched.Contains(x.i) && nvml.PciKeys.TryGetValue(x.g.Id, out var key) && key == adapter.PciKey).ToArray();
            if (matches.Length == 0 && data != null)
                matches = gpus.Select((g, i) => (g, i)).Where(x => !matched.Contains(x.i) && x.g.Name == adapter.Name && adapters.Count(a => a.Name == adapter.Name && counters.Adapters.ContainsKey(a.Id)) == 1).ToArray();
            var index = matches.Length == 1 ? matches[0].i : -1;
            if (index < 0 && data == null && gpus.Any(g => g.Name == adapter.Name)) continue;
            var current = index >= 0 ? gpus[index] : new GpuSample { Id = adapter.Id, Name = adapter.Name, IsDiscrete = adapter.IsDiscrete, DedicatedTotalBytes = adapter.Dedicated > 0 ? adapter.Dedicated : null };
            current = current with
            {
                Id = adapter.Id,
                UtilizationPercent = data?.Engines.Count > 0 ? Math.Clamp(data.Engines.Values.Max(), 0, 100) : null,
                Engines = data?.Engines ?? [],
                DedicatedUsedBytes = index >= 0 ? current.DedicatedUsedBytes : data?.Dedicated,
                SharedUsedBytes = data?.Shared
            };
            if (index >= 0) { gpus[index] = current; matched.Add(index); } else gpus.Add(current);
        }
        foreach (var gpu in gpus.Where(g => g.IsDiscrete && g.PowerWatts == null)) warnings.Add(gpu.Name + "功率不可用，不参与完整功率估算");
        var gpuEnd = Stopwatch.GetTimestamp();
        var timestamp = DateTimeOffset.UtcNow;
        var tickCount = (long)GetTickCount64();
        long? unbiased = QueryUnbiasedInterruptTime(out var ticks) ? (long)ticks : null;
        var processStart = Stopwatch.GetTimestamp();
        if (includeProcesses) lastProcesses = ReadProcesses(counters, clock);
        var processEnd = Stopwatch.GetTimestamp();
        var (rx, tx) = ReadNetwork(elapsed, warnings);
        double? read = null, write = null;
        try { read = diskRead?.NextValue(); write = diskWrite?.NextValue(); } catch { }
        if (read == null || write == null) warnings.Add("磁盘吞吐计数器不可用");
        return new()
        {
            Timestamp = timestamp, CpuPercent = cpu, CpuName = cpuName, CpuPackageWatts = cpuWatts,
            TickCountMilliseconds = tickCount, UnbiasedTimeTicks = unbiased,
            Timings = new(Stopwatch.GetElapsedTime(clock).TotalMilliseconds,
                Stopwatch.GetElapsedTime(cpuStart, cpuEnd).TotalMilliseconds,
                Stopwatch.GetElapsedTime(gpuStart, gpuEnd).TotalMilliseconds,
                Stopwatch.GetElapsedTime(processStart, processEnd).TotalMilliseconds),
            CpuPowerSource = cpuWatts != null ? "LibreHardwareMonitor · CPU Package" : "不可用",
            MemoryTotalBytes = (long)memory.TotalPhysical, MemoryUsedBytes = (long)(memory.TotalPhysical - memory.AvailablePhysical),
            DiskReadBytesPerSecond = read, DiskWriteBytesPerSecond = write,
            NetworkReceiveBytesPerSecond = rx, NetworkSendBytesPerSecond = tx,
            Gpus = gpus.ToArray(), GpuEnumerationComplete = discoveredAdapters is not null,
            Processes = lastProcesses, Warnings = warnings.Distinct().ToArray()
        };
    }

    private ProcessSample[] ReadProcesses(GpuCounterResult counters, long clock)
    {
        var result = new List<ProcessSample>();
        var live = new HashSet<int>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var pid = process.Id;
                    if (pid == 0) continue;
                    var start = process.StartTime.ToUniversalTime().Ticks;
                    var total = process.TotalProcessorTime;
                    var percent = 0d;
                    if (processes.TryGetValue(pid, out var old) && old.Start == start)
                    {
                        var seconds = Stopwatch.GetElapsedTime(old.Clock, clock).TotalSeconds;
                        if (seconds > 0) percent = Math.Clamp((total - old.Cpu).TotalSeconds / seconds / Environment.ProcessorCount * 100, 0, 100);
                    }
                    processes[pid] = (start, total, clock); live.Add(pid);
                    string? path = null;
                    try { path = process.MainModule?.FileName; } catch { }
                    var canClose = false;
                    try { canClose = pid > 4 && pid != Environment.ProcessId && process.SessionId != 0 && path != null && IsProcessCritical(process.Handle, out var critical) && !critical; } catch { }
                    counters.Processes.TryGetValue(pid, out var gpu);
                    result.Add(new()
                    {
                        Pid = pid, StartTimeUtcTicks = start, Name = process.ProcessName, ExecutablePath = path,
                        CpuPercent = percent, MemoryBytes = process.WorkingSet64, CanClose = canClose,
                        GpuPercent = gpu?.Engines.Count > 0 ? Math.Clamp(gpu.Engines.Values.Max(), 0, 100) : null,
                        GpuEngines = gpu?.Engines ?? [],
                        DedicatedGpuBytes = gpu?.Dedicated
                    });
                }
                catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        foreach (var pid in processes.Keys.Where(pid => !live.Contains(pid)).ToArray()) processes.Remove(pid);
        return result.ToArray();
    }

    private (double? Rx, double? Tx) ReadNetwork(double elapsed, List<string> warnings)
    {
        try
        {
            long rx = 0, tx = 0; var count = 0;
            var live = new HashSet<string>();
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var stats = nic.GetIPStatistics(); live.Add(nic.Id);
                if (networks.TryGetValue(nic.Id, out var old))
                {
                    rx += Math.Max(0, stats.BytesReceived - old.Rx); tx += Math.Max(0, stats.BytesSent - old.Tx); count++;
                }
                networks[nic.Id] = (stats.BytesReceived, stats.BytesSent);
            }
            foreach (var key in networks.Keys.Where(k => !live.Contains(k)).ToArray()) networks.Remove(key);
            return count > 0 && elapsed > 0 ? (rx / elapsed, tx / elapsed) : (null, null);
        }
        catch { warnings.Add("网络吞吐读取失败"); return (null, null); }
    }

    public void Dispose()
    {
        if (disposed) return; disposed = true;
        diskRead?.Dispose(); diskWrite?.Dispose(); gpuCounters.Dispose(); nvml.Dispose(); computer.Close();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessCritical(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool critical);
    [DllImport("kernel32.dll")] private static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryUnbiasedInterruptTime(out ulong unbiasedTime);
}
