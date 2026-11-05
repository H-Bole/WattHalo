using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32;
using WattMonitor.Updates;

namespace WattMonitor.App.Updates;

internal sealed record UpdateRelease(string Version, Uri ArchiveUri, byte[] Manifest, byte[] Signature, long Size, string Notes);

internal sealed class DownloadedUpdate(string directory) : IDisposable
{
    internal string DirectoryPath { get; } = directory;

    // The UI keeps installation disabled until the privileged recovery flow has been independently verified.
    internal async Task LaunchUpdaterAsync(CancellationToken cancellationToken = default)
    {
        try { await LaunchUpdaterCoreAsync(cancellationToken); }
        catch (Exception failure)
        {
            try { Dispose(); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { throw new IOException("更新未完成，无法清理本次下载目录：" + DirectoryPath, new AggregateException(failure, cleanup)); }
            throw;
        }
    }

    private async Task LaunchUpdaterCoreAsync(CancellationToken cancellationToken)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WattMonitor");
        var installed = key?.GetValue("InstallDirectory") as string;
        if (string.IsNullOrWhiteSpace(installed) || !string.Equals(Path.GetFullPath(installed).TrimEnd('\\'),
            Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("只有受保护目录中的已安装程序可以启动更新助手。");
        var host = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(installed).TrimEnd('\\'))!, "WattMonitor.UpdateHost");
        var hostRecord = Path.Combine(host, "host-manifest.json");
        ProtectedPaths.AssertPath(hostRecord);
        using var hostInfo = JsonDocument.Parse(File.ReadAllText(hostRecord));
        if (hostInfo.RootElement.GetProperty("Product").GetString() != "WattMonitor" || hostInfo.RootElement.GetProperty("Format").GetInt32() != 1 ||
            !string.Equals(hostInfo.RootElement.GetProperty("InstallDirectory").GetString()?.TrimEnd('\\'), Path.GetFullPath(installed).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新助手不属于本机安装。");
        var active = Path.Combine(host, "active.txt");
        ProtectedPaths.AssertPath(active);
        var version = File.ReadAllText(active).Trim();
        if (!UpdateManifest.TryVersion(version, out var activeVersion)) throw new InvalidDataException("更新助手版本记录无效。");
        var appVersion = typeof(UpdateClient).Assembly.GetName().Version ?? throw new InvalidDataException("当前程序版本无效。");
        if (activeVersion != new Version(appVersion.Major, appVersion.Minor, Math.Max(0, appVersion.Build)))
            throw new InvalidDataException("当前程序与更新助手版本不一致，请使用可信完整安装包修复。");
        var slot = Path.Combine(host, "versions", version);
        ProtectedPaths.AssertTree(slot);
        var recordPath = Path.Combine(slot, "slot-files.json");
        using var record = JsonDocument.Parse(File.ReadAllText(recordPath));
        if (record.RootElement.GetProperty("Product").GetString() != "WattMonitor" || record.RootElement.GetProperty("Format").GetInt32() != 1)
            throw new InvalidDataException("更新助手记录无效。");
        var recorded = record.RootElement.GetProperty("Files").EnumerateArray().Select(x => x.GetString() ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var actual = Directory.EnumerateFiles(slot, "*", SearchOption.AllDirectories).Select(x => Path.GetRelativePath(slot, x)).ToArray();
        recorded.Add("slot-files.json");
        if (actual.Length != recorded.Count || !recorded.SetEquals(actual)) throw new InvalidDataException("更新助手目录包含未知或缺失文件。");
        var executable = Path.Combine(slot, "WattMonitor.Updater.exe");
        if (!File.Exists(executable)) throw new FileNotFoundException("此安装缺少更新助手。", executable);
        var identity = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("无法确认当前用户。");
        var security = new PipeSecurity(); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new PipeAccessRule(identity, PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var pipeName = "WattHalo.Update." + Guid.NewGuid().ToString("N");
        using var pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous, 0, 0, security);
        var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas", WorkingDirectory = slot };
        start.ArgumentList.Add(DirectoryPath); start.ArgumentList.Add("--ready"); start.ArgumentList.Add(pipeName);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("无法启动更新助手。");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            var connection = pipe.WaitForConnectionAsync(timeout.Token);
            var exit = process.WaitForExitAsync(timeout.Token);
            if (await Task.WhenAny(connection, exit) != connection) { await exit; throw new IOException("更新助手未能准备更新，详见其错误提示。"); }
            await connection;
            if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var clientId) || clientId != process.Id)
                throw new IOException("无法核对更新助手通信身份。");
            var bytes = new byte[5]; var read = 0;
            while (read < bytes.Length)
            {
                var count = await pipe.ReadAsync(bytes.AsMemory(read), timeout.Token);
                if (count == 0) throw new IOException("更新助手未完成安全暂存。");
                read += count;
            }
            if (!bytes.SequenceEqual("READY"u8.ToArray())) throw new IOException("更新助手返回了无效状态。");
            Dispose();
        }
        catch (Exception failure)
        {
            pipe.Dispose();
            try { Dispose(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(5)); Dispose(); }
                catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException or TimeoutException or InvalidOperationException)
                { throw new IOException("更新未完成，无法清理本次下载目录：" + DirectoryPath, failure); }
            }
            throw;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint clientProcessId);

    public void Dispose()
    {
        var expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WattMonitor", "Updates");
        var full = Path.GetFullPath(DirectoryPath);
        if (!string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(expectedRoot), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParseExact(Path.GetFileName(full), "N", out _)) throw new InvalidDataException("下载目录归属校验失败。 ");
        if (!Directory.Exists(full)) return;
        var current = full;
        var root = Path.GetPathRoot(full)!;
        while (!string.Equals(current.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new IOException("下载目录路径包含链接，未清理。");
            current = Path.GetDirectoryName(current) ?? root;
        }
        foreach (var name in new[] { UpdateManifest.ArchiveName, UpdateManifest.ManifestName, UpdateManifest.SignatureName })
        {
            var path = Path.Combine(full, name);
            if (File.Exists(path)) File.Delete(path);
        }
        Directory.Delete(full);
    }
}

internal sealed class UpdateClient
{
    internal const string ReleasesUrl = "https://github.com/H-Bole/WattHalo/releases/latest";
    private static readonly HttpClient Http = CreateClient();
    private static readonly HttpClient DiscoveryHttp = CreateClient(false);
    private static HttpClient CreateClient(bool allowAutoRedirect = true)
    {
        var http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = allowAutoRedirect, UseCookies = false, UseDefaultCredentials = false
        }) { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WattHalo-Updater/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    internal async Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Http.GetAsync("https://api.github.com/repos/H-Bole/WattHalo/releases/latest", HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        string tag, notes = "";
        JsonElement? assets = null;
        if ((response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests) &&
            !response.Headers.Contains("Retry-After") &&
            response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.SequenceEqual(new[] { "0" }))
        {
            using var latest = await DiscoveryHttp.GetAsync(ReleasesUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            const string prefix = "https://github.com/H-Bole/WattHalo/releases/tag/";
            if (latest.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.RedirectMethod or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect) ||
                !latest.Headers.NonValidated.TryGetValues("Location", out var locations) || locations.ToArray() is not [var location] ||
                !Uri.TryCreate(location, UriKind.Absolute, out _) || !location.StartsWith(prefix, StringComparison.Ordinal))
                throw new InvalidDataException("发行版跳转地址无效。");
            tag = location[prefix.Length..];
        }
        else
        {
            response.EnsureSuccessStatusCode();
            var bytes = await ReadLimitedAsync(response, 256 * 1024, cancellationToken);
            using var release = JsonDocument.Parse(bytes);
            var root = release.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean())
                throw new InvalidDataException("GitHub 返回了非正式发行版。");
            tag = root.GetProperty("tag_name").GetString() ?? "";
            assets = root.GetProperty("assets").Clone();
            notes = root.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.String ? body.GetString() ?? "" : "";
        }
        var textVersion = tag.StartsWith('v') ? tag[1..] : tag;
        if (!UpdateManifest.TryVersion(textVersion, out var releaseVersion) ||
            (assets is null && textVersion != releaseVersion.ToString(3))) throw new InvalidDataException("发行版本号无效。");
        Uri Asset(string name)
        {
            var expected = "https://github.com/H-Bole/WattHalo/releases/download/" + tag + "/" + name;
            if (assets is JsonElement apiAssets)
            {
                var matches = apiAssets.EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
                if (matches.Length != 1) throw new InvalidDataException("发行版缺少唯一的更新文件：" + name);
                var value = matches[0].GetProperty("browser_download_url").GetString();
                if (!string.Equals(value, expected, StringComparison.Ordinal)) throw new InvalidDataException("发行版更新地址无效。");
            }
            return new Uri(expected);
        }
        var manifestBytes = await ReadLimitedAsync(Asset(UpdateManifest.ManifestName), 16384, cancellationToken);
        var signature = await ReadLimitedAsync(Asset(UpdateManifest.SignatureName), 1024, cancellationToken);
        var manifest = UpdateManifest.Verify(manifestBytes, signature);
        if (manifest.Version != textVersion) throw new InvalidDataException("签名清单与发行版本不一致。");
        var current = typeof(UpdateClient).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var normalized = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
        notes = new string(notes.Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').Take(8000).ToArray());
        return releaseVersion <= normalized ? null : new UpdateRelease(textVersion, Asset(UpdateManifest.ArchiveName), manifestBytes, signature, manifest.Size, notes);
    }

    internal async Task<DownloadedUpdate> DownloadAsync(UpdateRelease release, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        var manifest = UpdateManifest.Verify(release.Manifest, release.Signature);
        if (manifest.Version != release.Version || manifest.Size != release.Size) throw new InvalidDataException("更新信息不一致，请重新检查。");
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WattMonitor", "Updates", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllBytesAsync(Path.Combine(directory, UpdateManifest.ManifestName), release.Manifest, cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(directory, UpdateManifest.SignatureName), release.Signature, cancellationToken);
            using var response = await Http.GetAsync(release.ArchiveUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != manifest.Size) throw new InvalidDataException("更新包大小与签名清单不一致。");
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            var archive = Path.Combine(directory, UpdateManifest.ArchiveName);
            await using (var output = new FileStream(archive, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920]; long total = 0; int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    total += read;
                    if (total > manifest.Size) throw new InvalidDataException("更新包超过声明大小。");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    progress?.Report((double)total / manifest.Size);
                }
            }
            UpdateManifest.VerifyArchive(archive, manifest);
            return new DownloadedUpdate(directory);
        }
        catch (Exception failure)
        {
            try { new DownloadedUpdate(directory).Dispose(); }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            { throw new IOException("下载未完成，无法清理本次下载目录：" + directory, new AggregateException(failure, cleanup)); }
            throw;
        }
    }

    private static async Task<byte[]> ReadLimitedAsync(Uri uri, int maximum, CancellationToken cancellationToken)
    {
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadLimitedAsync(response, maximum, cancellationToken);
    }

    private static async Task<byte[]> ReadLimitedAsync(HttpResponseMessage response, int maximum, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("更新元数据过大。");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[4096]; int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (output.Length + read > maximum) throw new InvalidDataException("更新元数据过大。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
