namespace WattMonitor.Core;

public static class GpuUtilization
{
    public static double? ForApplication(IEnumerable<ProcessSample> processes)
    {
        // Different engines can execute simultaneously; sum processes on each engine before taking the maximum.
        var engines = processes.SelectMany(p => p.GpuEngines)
            .Where(p => double.IsFinite(p.Value) && p.Value >= 0)
            .GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => Math.Clamp(g.Sum(p => p.Value), 0, 100));
        return engines.Select(x => (double?)x).DefaultIfEmpty(null).Max();
    }
}
