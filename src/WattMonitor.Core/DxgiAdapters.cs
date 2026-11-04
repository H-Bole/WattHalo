using System.Runtime.InteropServices;

namespace WattMonitor.Core;

internal static class DxgiAdapters
{
    internal sealed record Adapter(string Id, string Name, long Dedicated, bool IsDiscrete, string? PciKey);
    private static Adapter[]? cached;

    public static Adapter[]? Read()
    {
        if (cached != null) return cached;
        var result = new List<Adapter>();
        var guid = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        if (CreateDXGIFactory1(ref guid, out var factory) < 0) return null;
        try
        {
            var enumerate = Method<EnumAdapters>(factory, 12);
            for (uint i = 0; ; i++)
            {
                var status = enumerate(factory, i, out var adapter);
                if (status == unchecked((int)0x887A0002)) break; // DXGI_ERROR_NOT_FOUND: end of list.
                if (status < 0) return null;
                try
                {
                    if (Method<GetDescription>(adapter, 10)(adapter, out var desc) < 0) return null;
                    if ((desc.Flags & 2) == 0)
                    {
                        // DedicatedVideoMemory alone is not a discrete-GPU test: UMA adapters reserve memory too.
                        var integrated = desc.Name.Contains("Intel", StringComparison.OrdinalIgnoreCase) && !desc.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase)
                            || desc.Name.Contains("Radeon(TM) Graphics", StringComparison.OrdinalIgnoreCase)
                            || desc.Name.Contains("Radeon Graphics", StringComparison.OrdinalIgnoreCase)
                            || desc.Name.Contains("Vega", StringComparison.OrdinalIgnoreCase);
                        var discrete = !integrated && (desc.VendorId == 0x10de || desc.Name.Contains("Arc", StringComparison.OrdinalIgnoreCase)
                            || desc.Name.Contains("Radeon RX", StringComparison.OrdinalIgnoreCase) || desc.Name.Contains("Radeon PRO", StringComparison.OrdinalIgnoreCase));
                        result.Add(new($"0x{desc.LuidHigh:x8}_0x{desc.LuidLow:x8}", desc.Name, (long)desc.DedicatedVideoMemory, discrete, PciAddress(desc.LuidLow, desc.LuidHigh)));
                    }
                }
                finally { Marshal.Release(adapter); }
            }
        }
        finally { Marshal.Release(factory); }
        cached = result.ToArray();
        return cached;
    }

    private static T Method<T>(IntPtr instance, int slot) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));
    private static string? PciAddress(uint low, uint high)
    {
        var open = new OpenAdapter { Low = low, High = high };
        if (D3DKMTOpenAdapterFromLuid(ref open) < 0) return null;
        var buffer = Marshal.AllocHGlobal(12);
        try
        {
            var query = new QueryInfo { Handle = open.Handle, Type = 6, Data = buffer, Size = 12 };
            return D3DKMTQueryAdapterInfo(ref query) >= 0 ? $"{Marshal.ReadInt32(buffer):x}:{Marshal.ReadInt32(buffer, 4):x}" : null;
        }
        finally { Marshal.FreeHGlobal(buffer); var close = open.Handle; D3DKMTCloseAdapter(ref close); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct OpenAdapter { public uint Low, High, Handle; }
    [StructLayout(LayoutKind.Sequential)] private struct QueryInfo { public uint Handle, Type; public IntPtr Data; public uint Size; }
    [DllImport("gdi32.dll")] private static extern int D3DKMTOpenAdapterFromLuid(ref OpenAdapter open);
    [DllImport("gdi32.dll")] private static extern int D3DKMTQueryAdapterInfo(ref QueryInfo query);
    [DllImport("gdi32.dll")] private static extern int D3DKMTCloseAdapter(ref uint handle);
    [DllImport("dxgi.dll")] private static extern int CreateDXGIFactory1(ref Guid guid, out IntPtr factory);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters(IntPtr factory, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr adapter, out Description description);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct Description
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Name;
        public uint VendorId, DeviceId, SubsystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow, LuidHigh, Flags;
    }
}
