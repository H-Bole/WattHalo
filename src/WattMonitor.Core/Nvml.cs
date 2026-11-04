using System.Runtime.InteropServices;
using System.Text;

namespace WattMonitor.Core;

internal sealed class Nvml : IDisposable
{
    private IntPtr library;
    private NvmlShutdown? shutdown;
    private GetCount? count;
    private GetHandle? handle;
    private GetName? name;
    private GetPower? power;
    private GetMemory? memory;
    private GetPci? pci;
    public Dictionary<string, string> PciKeys { get; } = [];
    private string? error;

    public Nvml()
    {
        var candidates = new[] { Path.Combine(Environment.SystemDirectory, "nvml.dll"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvml.dll") };
        foreach (var path in candidates) if (NativeLibrary.TryLoad(path, out library)) break;
        if (library == IntPtr.Zero) { error = "未找到NVIDIA NVML；独立显卡功率不可用"; return; }
        try
        {
            var init = Bind<Init>("nvmlInit_v2");
            var status = init();
            if (status != 0) { error = $"NVML初始化失败（状态{status}）"; NativeLibrary.Free(library); library = IntPtr.Zero; return; }
            shutdown = Bind<NvmlShutdown>("nvmlShutdown"); count = Bind<GetCount>("nvmlDeviceGetCount_v2");
            handle = Bind<GetHandle>("nvmlDeviceGetHandleByIndex_v2"); name = Bind<GetName>("nvmlDeviceGetName");
            power = Bind<GetPower>("nvmlDeviceGetPowerUsage"); memory = Bind<GetMemory>("nvmlDeviceGetMemoryInfo");
            if (NativeLibrary.TryGetExport(library, "nvmlDeviceGetPciInfo_v3", out var pciExport)) pci = Marshal.GetDelegateForFunctionPointer<GetPci>(pciExport);
        }
        catch (Exception e) { error = "NVML接口不可用：" + e.Message; Dispose(); }
    }

    private T Bind<T>(string export) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, export));

    public GpuSample[] Read(List<string> warnings)
    {
        if (library == IntPtr.Zero || count == null || handle == null || name == null || power == null || memory == null)
        { if (error != null) warnings.Add(error); return []; }
        var status = count(out var devices);
        if (status != 0) { warnings.Add($"NVML枚举失败（状态{status}）"); return []; }
        var result = new List<GpuSample>();
        PciKeys.Clear();
        for (uint i = 0; i < devices; i++)
        {
            if (handle(i, out var device) != 0) continue;
            var text = new StringBuilder(128); name(device, text, 128);
            var powerStatus = power(device, out var milliwatts);
            var memoryStatus = memory(device, out var mem);
            if (pci != null && pci(device, out var address) == 0) PciKeys["nvml:" + i] = $"{address.Bus:x}:{address.Device:x}";
            result.Add(new()
            {
                Id = "nvml:" + i, Name = text.ToString(), IsDiscrete = true,
                PowerWatts = powerStatus == 0 ? milliwatts / 1000d : null,
                PowerSource = powerStatus == 0 ? "NVIDIA NVML · Board Power" : "不可用",
                DedicatedTotalBytes = memoryStatus == 0 ? (long)mem.Total : null,
                DedicatedUsedBytes = memoryStatus == 0 ? (long)mem.Used : null
            });
        }
        return result.ToArray();
    }

    public void Dispose()
    {
        if (library == IntPtr.Zero) return;
        shutdown?.Invoke(); NativeLibrary.Free(library); library = IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Memory { public ulong Total, Free, Used; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)] private struct Pci
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)] public string LegacyBusId;
        public uint Domain, Bus, Device, DeviceId, SubsystemId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string BusId;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NvmlShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetCount(out uint count);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetHandle(uint index, out IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)] private delegate int GetName(IntPtr handle, StringBuilder name, uint length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPower(IntPtr handle, out uint milliwatts);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetMemory(IntPtr handle, out Memory memory);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int GetPci(IntPtr handle, out Pci pci);
}
