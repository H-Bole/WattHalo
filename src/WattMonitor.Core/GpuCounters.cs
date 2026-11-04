using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace WattMonitor.Core;

internal sealed class GpuCounterData
{
    public Dictionary<string, double> Engines { get; } = [];
    public long? Dedicated, Shared;
}
internal sealed class GpuCounterResult
{
    public Dictionary<string, GpuCounterData> Adapters { get; } = [];
    public Dictionary<int, GpuCounterData> Processes { get; } = [];
}

/// <summary>PDH English wildcard counters work on localized Windows and include all WDDM processes.</summary>
internal sealed class GpuCounters : IDisposable
{
    private IntPtr query, engines, dedicated, shared, adapterDedicated, adapterShared;
    private readonly string? error;
    private static readonly Regex Instance = new(@"pid_(\d+)_luid_(0x[0-9a-f]+)_(0x[0-9a-f]+)_phys_(\d+)(?:_eng_(\d+)_engtype_(.+))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AdapterInstance = new(@"luid_(0x[0-9a-f]+)_(0x[0-9a-f]+)_phys_(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GpuCounters()
    {
        if (PdhOpenQuery(null, 0, out query) != 0) { error = "无法打开GPU性能查询"; return; }
        if (PdhAddEnglishCounter(query, @"\GPU Engine(*)\Utilization Percentage", 0, out engines) != 0) error = "GPU引擎计数器不可用（需要WDDM驱动）";
        PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Dedicated Usage", 0, out dedicated);
        PdhAddEnglishCounter(query, @"\GPU Process Memory(*)\Shared Usage", 0, out shared);
        PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Dedicated Usage", 0, out adapterDedicated);
        PdhAddEnglishCounter(query, @"\GPU Adapter Memory(*)\Shared Usage", 0, out adapterShared);
        PdhCollectQueryData(query);
    }

    public GpuCounterResult Read(List<string> warnings)
    {
        var result = new GpuCounterResult();
        if (error != null) warnings.Add(error);
        if (query == IntPtr.Zero || PdhCollectQueryData(query) != 0) return result;
        void ReadCounter(IntPtr counter, int kind)
        {
            foreach (var (name, value) in Values(counter))
            {
                var match = Instance.Match(name);
                if (!match.Success || !int.TryParse(match.Groups[1].Value, out var pid)) continue;
                if (kind == 0 && !match.Groups[5].Success) continue;
                var id = match.Groups[2].Value.ToLowerInvariant() + "_" + match.Groups[3].Value.ToLowerInvariant();
                if (!result.Adapters.TryGetValue(id, out var adapter)) result.Adapters[id] = adapter = new();
                if (!result.Processes.TryGetValue(pid, out var process)) result.Processes[pid] = process = new();
                foreach (var data in new[] { adapter, process })
                {
                    if (kind == 0)
                    {
                        // Sum processes on the SAME physical engine, then select the busiest engine, like Task Manager.
                        var engine = (ReferenceEquals(data, process) ? id + "/" : "") + match.Groups[4].Value + "/" + match.Groups[5].Value + " " + match.Groups[6].Value;
                        data.Engines[engine] = data.Engines.GetValueOrDefault(engine) + value;
                    }
                    else if (kind == 1) data.Dedicated = (data.Dedicated ?? 0) + (long)value;
                    else data.Shared = (data.Shared ?? 0) + (long)value;
                }
            }
        }
        ReadCounter(engines, 0); ReadCounter(dedicated, 1); ReadCounter(shared, 2);
        // Adapter counters give resident totals; summing process allocations can count shared allocations twice.
        foreach (var (counter, isShared) in new[] { (adapterDedicated, false), (adapterShared, true) })
        {
            var totals = new Dictionary<string, long>();
            foreach (var (name, value) in Values(counter))
            {
                var match = AdapterInstance.Match(name);
                if (!match.Success) continue;
                var id = match.Groups[1].Value.ToLowerInvariant() + "_" + match.Groups[2].Value.ToLowerInvariant();
                totals[id] = totals.GetValueOrDefault(id) + (long)value;
            }
            foreach (var (id, bytes) in totals)
            {
                if (!result.Adapters.TryGetValue(id, out var adapter)) result.Adapters[id] = adapter = new();
                if (isShared) adapter.Shared = bytes; else adapter.Dedicated = bytes;
            }
        }
        if (engines == IntPtr.Zero) warnings.Add("进程GPU占用不可用");
        if (dedicated == IntPtr.Zero) warnings.Add("进程专用显存计数器不可用");
        return result;
    }

    private static IEnumerable<(string, double)> Values(IntPtr counter)
    {
        if (counter == IntPtr.Zero) return [];
        uint bytes = 0, count = 0;
        const uint format = 0x200; // PDH_FMT_DOUBLE
        if (PdhGetFormattedCounterArray(counter, format, ref bytes, ref count, IntPtr.Zero) != 0x800007D2 || bytes == 0) return [];
        var buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (PdhGetFormattedCounterArray(counter, format, ref bytes, ref count, buffer) != 0) return [];
            var result = new List<(string, double)>((int)count);
            var size = Marshal.SizeOf<CounterItem>();
            for (var i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<CounterItem>(buffer + i * size);
                if (item.Value.Status <= 1 && double.IsFinite(item.Value.Value) && item.Value.Value >= 0)
                    result.Add((Marshal.PtrToStringUni(item.Name) ?? "", item.Value.Value));
            }
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public void Dispose() { if (query != IntPtr.Zero) PdhCloseQuery(query); query = IntPtr.Zero; }
    [StructLayout(LayoutKind.Explicit, Size = 16)] private struct CounterValue { [FieldOffset(0)] public uint Status; [FieldOffset(8)] public double Value; }
    [StructLayout(LayoutKind.Sequential)] private struct CounterItem { public IntPtr Name; public CounterValue Value; }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW")] private static extern uint PdhOpenQuery(string? source, nuint data, out IntPtr query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW")] private static extern uint PdhAddEnglishCounter(IntPtr query, string path, nuint data, out IntPtr counter);
    [DllImport("pdh.dll")] private static extern uint PdhCollectQueryData(IntPtr query);
    [DllImport("pdh.dll", EntryPoint = "PdhGetFormattedCounterArrayW")] private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref uint bytes, ref uint count, IntPtr buffer);
    [DllImport("pdh.dll")] private static extern uint PdhCloseQuery(IntPtr query);
}
