using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using WattMonitor.Core;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = MonitorProtocol.ServiceName);
builder.Services.AddHostedService<TelemetryWorker>();
await builder.Build().RunAsync();

sealed class TelemetryWorker(IConfiguration configuration, ILogger<TelemetryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var ownerSidText = configuration["owner-sid"];
        if (string.IsNullOrWhiteSpace(ownerSidText))
            throw new InvalidOperationException("服务必须配置 --owner-sid；不允许向所有用户开放硬件采集接口。");
        var ownerSid = new SecurityIdentifier(ownerSidText);
        var acl = new PipeSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(ownerSid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = NamedPipeServerStreamAcl.Create(MonitorProtocol.PipeName,
                    PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 8192, 8192, acl);
                await pipe.WaitForConnectionAsync(stoppingToken);
                using var reader = new StreamReader(pipe, leaveOpen: true);
                using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
                using var sessionTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                sessionTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                var requestText = await reader.ReadLineAsync(sessionTimeout.Token);
                if (requestText is null || requestText.Length > 256) continue;
                var request = JsonSerializer.Deserialize<PipeRequest>(requestText);
                if (request is not { Command: "subscribe" } || request.RefreshSeconds is not (1 or 2 or 5)) continue;
                // Only telemetry leaves the privileged process. No file paths, commands or MSR inputs are accepted.
                using var sampler = new HardwareSampler();
                var refresh = request.RefreshSeconds;
                var cadence = new SamplingCadence(refresh);
                var lastProcesses = System.Diagnostics.Stopwatch.GetTimestamp() - 2 * System.Diagnostics.Stopwatch.Frequency;
                while (pipe.IsConnected && !stoppingToken.IsCancellationRequested)
                {
                    var includeProcesses = System.Diagnostics.Stopwatch.GetElapsedTime(lastProcesses).TotalSeconds >= 2;
                    var sample = sampler.Sample(includeProcesses) with { Mode = "硬件采集服务" };
                    if (includeProcesses) lastProcesses = System.Diagnostics.Stopwatch.GetTimestamp();
                    using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    writeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                    await writer.WriteLineAsync(JsonSerializer.Serialize(sample).AsMemory(), writeTimeout.Token);
                    await cadence.WaitAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("采集连接中断：{Message}", ex.Message);
                await Task.Delay(1000, stoppingToken);
            }
        }
    }
}
