namespace WattMonitor.App;

public sealed record OperationResult(bool Success, string Message, bool Cancelled = false, string? Path = null);

public sealed record AppStatus(bool IsStale, bool IsRecording, bool PowerComplete, string Connection,
    string? StorageError, DateTimeOffset? LastSample, string? SamplingError);
