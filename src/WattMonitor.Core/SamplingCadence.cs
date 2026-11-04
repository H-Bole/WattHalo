using System.Diagnostics;

namespace WattMonitor.Core;

/// <summary>以单调时钟安排下一次采样；过载时跳过旧周期，不积压采样。</summary>
public sealed class SamplingCadence(int refreshSeconds)
{
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly TimeSpan period = TimeSpan.FromSeconds(refreshSeconds);
    private TimeSpan next = TimeSpan.FromSeconds(refreshSeconds);

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        var remaining = next - clock.Elapsed;
        if (remaining <= TimeSpan.Zero)
        {
            next = clock.Elapsed + period;
            return;
        }
        await Task.Delay(remaining, cancellationToken);
        next += period;
    }
}
