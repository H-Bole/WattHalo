namespace WattMonitor.Core;

public enum MetricKind { Power, Cpu, Gpu, Memory, Disk, Network }
public enum ThemePreference { Dark, Light, System }
public enum LanguagePreference { System, Chinese, English }
public enum HistoryChartStyle { Bars, Line, Smooth }
public enum LiveChartStyle { Line, Area, Bars }
public enum OrbStyle { TechRing, Solid, Glass }

public sealed record AppSettings
{
    public int RefreshSeconds { get; init; } = 1;
    public int TopCount { get; init; } = 3;
    public bool StartWithWindows { get; init; } = true;
    public bool ShowOrb { get; init; } = true;
    public bool ShowTaskbar { get; init; } = true;
    public bool ReducedMotion { get; init; }
    public bool OnboardingCompleted { get; init; }
    public ThemePreference Theme { get; init; } = ThemePreference.Dark;
    public LanguagePreference Language { get; init; } = LanguagePreference.System;
    public HistoryChartStyle HistoryChart { get; init; } = HistoryChartStyle.Bars;
    public LiveChartStyle LiveChart { get; init; } = LiveChartStyle.Line;
    public OrbStyle OrbStyle { get; init; } = OrbStyle.TechRing;
    public int OrbSize { get; init; } = 88;
    public bool PanelPinned { get; init; }
    public MetricKind OrbMetric { get; init; } = MetricKind.Power;
    public MetricKind SelectedTab { get; init; } = MetricKind.Power;
    public MetricKind? PanelSelectedTab { get; init; }
    public MetricKind[] TaskbarMetrics { get; init; } = [MetricKind.Cpu, MetricKind.Gpu];
    public double OtherPartsWatts { get; init; } = 30;
    public double PsuEfficiency { get; init; } = .9;
    public double? OrbLeft { get; init; }
    public double? OrbTop { get; init; }
    public double? TaskbarLeft { get; init; }
    public double? TaskbarTop { get; init; }
    public bool TaskbarPositionPhysical { get; init; }
    public Tariff[] Tariffs { get; init; } = [new(DateTimeOffset.UnixEpoch, 1m)];
}

public sealed record Tariff(DateTimeOffset EffectiveFrom, decimal YuanPerKwh);

public sealed record ProcessSample
{
    public int Pid { get; init; }
    public long StartTimeUtcTicks { get; init; }
    public string Name { get; init; } = "";
    public string? ExecutablePath { get; init; }
    public double CpuPercent { get; init; }
    public double? GpuPercent { get; init; }
    public Dictionary<string, double> GpuEngines { get; init; } = [];
    public long MemoryBytes { get; init; }
    public long? DedicatedGpuBytes { get; init; }
    public bool CanClose { get; init; }
}

public sealed record GpuSample
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "GPU";
    public bool IsDiscrete { get; init; }
    public double? UtilizationPercent { get; init; }
    public double? PowerWatts { get; init; }
    public long? DedicatedUsedBytes { get; init; }
    public long? DedicatedTotalBytes { get; init; }
    public long? SharedUsedBytes { get; init; }
    public Dictionary<string, double> Engines { get; init; } = [];
    public string PowerSource { get; init; } = "不可用";
}

public sealed record TelemetrySnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public string? SourceSession { get; init; }
    public long? UnbiasedTimeTicks { get; init; }
    public long? TickCountMilliseconds { get; init; }
    public SamplingTimings? Timings { get; init; }
    public double? CpuPercent { get; init; }
    public string CpuName { get; init; } = "CPU";
    public double? CpuPackageWatts { get; init; }
    public string CpuPowerSource { get; init; } = "不可用";
    public long MemoryUsedBytes { get; init; }
    public long MemoryTotalBytes { get; init; }
    public double? DiskReadBytesPerSecond { get; init; }
    public double? DiskWriteBytesPerSecond { get; init; }
    public double? NetworkReceiveBytesPerSecond { get; init; }
    public double? NetworkSendBytesPerSecond { get; init; }
    public GpuSample[] Gpus { get; init; } = [];
    public bool GpuEnumerationComplete { get; init; }
    public ProcessSample[] Processes { get; init; } = [];
    public string[] Warnings { get; init; } = [];
    public double? ComponentWatts { get; init; }
    public double? EstimatedHostWatts { get; init; }
    public bool PowerComplete { get; init; }
    public string Mode { get; init; } = "本机采集";
}

public sealed record SamplingTimings(double TotalMilliseconds, double CpuMilliseconds,
    double GpuMilliseconds, double ProcessMilliseconds);

public sealed record EnergyTotals(double ComponentKwh, double EstimatedKwh, decimal CostYuan,
    double RecordedSeconds, double CompleteSeconds);

public sealed record HistoryPoint(DateTimeOffset Start, double ComponentKwh, double EstimatedKwh,
    decimal CostYuan, double RecordedSeconds, double CompleteSeconds)
{
    public bool IsDailyAggregate { get; init; }
}

public enum CoverageConfidence { Unknown, Partial, Observed }

public sealed record CoverageSummary(double RecordedSeconds, double CompleteSeconds,
    double KnownUnrecordedSeconds, double KnownSleepSeconds, double? UnknownSeconds,
    CoverageConfidence Confidence, bool IncludesLegacyData)
{
    public bool HasTimeConflict { get; init; }
}

public sealed record SamplingDiagnostics(int Samples, int DroppedIntervals, int MissingPowerIntervals,
    int ClockDiscontinuities, int BasisChanges, double MaxIntervalSeconds, double MaxTotalMilliseconds, double MaxCpuMilliseconds,
    double MaxGpuMilliseconds, double MaxProcessMilliseconds);

public sealed record PipeRequest(string Command, int RefreshSeconds = 1);

public static class MonitorProtocol
{
    public const string ServiceName = "WattMonitor";
    public const string PipeName = "WattMonitor.Telemetry.v1";
    public const int MaxMessageChars = 4_194_304;
}
