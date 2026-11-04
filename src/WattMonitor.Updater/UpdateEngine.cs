using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;
using WattMonitor.Updates;

namespace WattMonitor.Updater;

internal sealed record UpdateJournal(string Product, int Format, string InstallDirectory, string OwnerSid,
    uint StartType, bool WasRunning, string PreviousHelperVersion, string NewHelperVersion);

internal sealed class UpdateEngine
{
    private const string TaskName = "WattMonitor-UpdateRecovery";
    private readonly string target, host, transaction, cleanup, versions, executable;
    private readonly Action<string> report;

    internal UpdateEngine(Action<string> report)
    {
        this.report = report;
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("更新助手需要管理员授权。");
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WattMonitor");
        var location = key?.GetValue("InstallDirectory") as string;
        if (string.IsNullOrWhiteSpace(location) || !System.Text.RegularExpressions.Regex.IsMatch(location, @"^[A-Za-z]:\\") ||
            !Path.IsPathFullyQualified(location)) throw new InvalidDataException("未找到有效的安装位置。");
        target = Path.GetFullPath(location).TrimEnd('\\');
        var drive = new DriveInfo(Path.GetPathRoot(target)!);
        if (!string.Equals(Path.GetFileName(target), "WattMonitor", StringComparison.OrdinalIgnoreCase) ||
            drive.DriveType != DriveType.Fixed || !string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("更新需要本机 NTFS 固定磁盘上的 WattMonitor 安装目录。");
        host = Path.Combine(Path.GetDirectoryName(target)!, "WattMonitor.UpdateHost");
        transaction = Path.Combine(host, "transaction");
        cleanup = Path.Combine(host, "cleanup");
        versions = Path.Combine(host, "versions");
        executable = Path.GetFullPath(Environment.ProcessPath ?? throw new InvalidDataException("无法核对更新助手位置。"));
        var slot = Path.GetDirectoryName(executable)!;
        if (!string.Equals(Path.GetFileName(executable), "WattMonitor.Updater.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(slot), versions, StringComparison.OrdinalIgnoreCase) ||
            !UpdateManifest.TryVersion(Path.GetFileName(slot), out _)) throw new InvalidDataException("更新助手不是从受保护的版本槽启动。");
        ProtectedPaths.AssertTree(slot);
        ProtectedPaths.AssertPath(host);
        if (Directory.Exists(target)) ProtectedPaths.AssertPath(target);
        if (Directory.Exists(transaction)) ProtectedPaths.AssertPath(transaction);
        if (Directory.Exists(cleanup)) ProtectedPaths.AssertPath(cleanup);
    }

    internal void Run(string source, string readyPipe)
    {
        using var updateLock = Lock();
        RetryRecovery(RecoverLocked);
        var previousVersion = ReadActive();
        if (!string.Equals(Path.GetFileName(Path.GetDirectoryName(executable)), previousVersion, StringComparison.Ordinal))
            throw new InvalidDataException("当前更新助手版本已过期，请重新检查更新。");
        if (Directory.Exists(transaction)) throw new IOException("存在未清理的更新事务，请重启电脑后重试。");
        Directory.CreateDirectory(transaction);
        ProtectedPaths.AssertPath(transaction);
        Process? watcher = null;
        var scheduled = false;
        try
        {
            report("正在将更新包复制到受保护目录并验证签名…");
            source = Path.GetFullPath(source);
            if (!System.Text.RegularExpressions.Regex.IsMatch(source, @"^[A-Za-z]:\\") ||
                new DriveInfo(Path.GetPathRoot(source)!).DriveType != DriveType.Fixed) throw new InvalidDataException("更新包必须位于本机固定磁盘。");
            CopyLimited(Path.Combine(source, UpdateManifest.ManifestName), Path.Combine(transaction, UpdateManifest.ManifestName), 16384);
            CopyLimited(Path.Combine(source, UpdateManifest.SignatureName), Path.Combine(transaction, UpdateManifest.SignatureName), 1024);
            var manifest = UpdateManifest.Verify(File.ReadAllBytes(Path.Combine(transaction, UpdateManifest.ManifestName)),
                File.ReadAllBytes(Path.Combine(transaction, UpdateManifest.SignatureName)));
            CopyLimited(Path.Combine(source, UpdateManifest.ArchiveName), Path.Combine(transaction, UpdateManifest.ArchiveName), manifest.Size);
            UpdateManifest.VerifyArchive(Path.Combine(transaction, UpdateManifest.ArchiveName), manifest);
            var current = FileVersionInfo.GetVersionInfo(Path.Combine(target, "WattMonitor.App.exe"));
            var currentVersion = new Version(current.FileMajorPart, current.FileMinorPart, current.FileBuildPart);
            if (!UpdateManifest.TryVersion(previousVersion, out var helperVersion) || helperVersion != currentVersion)
                throw new InvalidDataException("当前程序与更新助手版本不一致，请先使用可信完整安装包修复。");
            if (!UpdateManifest.TryVersion(manifest.Version, out var next) || next <= currentVersion)
                throw new InvalidDataException("更新版本不高于当前安装版本。");
            var install = ReadInstalled();
            var ownerSid = install["OwnerSid"]?.GetValue<string>() ?? throw new InvalidDataException("安装记录缺少用户标识。");
            if (!System.Text.RegularExpressions.Regex.IsMatch(ownerSid, @"^S-\d+(?:-\d+)+$")) throw new InvalidDataException("安装用户标识无效。");
            using var service = ServiceControl.Open(Path.Combine(target, "service", "WattMonitor.Service.exe"), ownerSid);
            if (service.State is not (1 or 4)) throw new IOException("采集服务正在切换状态，请稍后重试。");
            var journal = new UpdateJournal("WattMonitor", 1, target, ownerSid, service.StartType, service.State == 4, previousVersion, manifest.Version);
            WriteDurable(Path.Combine(transaction, "journal.json"), JsonSerializer.SerializeToUtf8Bytes(journal));
            report("正在准备完整的新版本，原安装仍保持可用…");
            ProtectedPaths.AssertTree(target);
            VerifyPrevious(target, journal);
            var candidate = Path.Combine(transaction, "candidate");
            CloneDirectory(target, candidate);
            ApplyArchive(Path.Combine(transaction, UpdateManifest.ArchiveName), candidate, install, manifest);
            var slot = Path.Combine(versions, manifest.Version);
            if (Directory.Exists(slot) || File.Exists(slot)) throw new IOException("更新助手版本槽已存在，请先检查之前的更新事务。");
            WriteDurable(Path.Combine(transaction, "slot-reserved"), [1]);
            CloneDirectory(Path.Combine(candidate, "updater"), slot);
            var slotFiles = Directory.EnumerateFiles(slot, "*", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(slot, path)).OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
            if (slotFiles.Contains("slot-files.json", StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("更新助手使用了保留文件名。");
            WriteDurable(Path.Combine(slot, "slot-files.json"), JsonSerializer.SerializeToUtf8Bytes(new { Product = "WattMonitor", Format = 1, Files = slotFiles }));
            ProtectedPaths.AssertTree(slot);
            RegisterRecovery();
            scheduled = true;
            watcher = StartWatcher();
            NotifyReady(readyPipe);
            report("等待 WattHalo 退出…");
            WaitForAppExit();
            report("正在停止采集服务并切换版本…");
            WriteDurable(Path.Combine(transaction, "started"), [1]);
            service.SetStartType(4);
            service.Stop();
            RetryTransient(() => DirectoryTransaction.Apply(target, transaction), "切换版本");
            RestoreService(service, journal);
            WriteActive(manifest.Version);
            WriteDurable(Path.Combine(transaction, "committed"), [1]);
            report("更新完成，正在清理旧版本…");
            VerifyCommitted(journal, transaction);
            MoveCommittedToCleanup();
            FinishCommittedCleanup();
            DeleteRecoveryTask(); scheduled = false;
            report("更新完成。请从快捷方式重新打开 WattHalo。");
        }
        catch
        {
            if (File.Exists(Path.Combine(transaction, "journal.json")) || Directory.Exists(cleanup))
            {
                try { RetryRecovery(RecoverLocked); scheduled = false; }
                catch (Exception recovery) { throw new IOException("更新或清理未完成；恢复资料与开机恢复任务已保留，请重启电脑。原因：" + recovery.Message, recovery); }
            }
            else if (Directory.Exists(transaction)) DeleteTransaction();
            throw;
        }
        finally
        {
            watcher?.Dispose();
            if (scheduled && !Directory.Exists(transaction) && !Directory.Exists(cleanup)) { try { DeleteRecoveryTask(); } catch { } }
        }
    }

    internal void Recover()
    {
        RetryRecovery(() => { using var updateLock = Lock(); RecoverLocked(); });
    }

    private void RetryRecovery(Action action) => RetryTransient(action, "恢复");

    private void RetryTransient(Action action, string operation)
    {
        int[] delays = [1, 2, 4, 8, 16];
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception error) when (attempt < delays.Length && IsTransientRecoveryError(error))
            {
                report($"{operation}遇到文件或服务暂时占用，将在 {delays[attempt]} 秒后重试…");
                Thread.Sleep(TimeSpan.FromSeconds(delays[attempt]));
            }
        }
    }

    internal static bool IsTransientRecoveryError(Exception error)
    {
        var code = error is System.ComponentModel.Win32Exception win32 ? win32.NativeErrorCode :
            error is IOException or UnauthorizedAccessException && ((uint)error.HResult & 0xffff0000) == 0x80070000
                ? error.HResult & 0xffff : -1;
        return code is 5 or 32 or 33 or 145 or 170 or 1053 or 1061 or 1062;
    }

    private void RecoverLocked()
    {
        if (Directory.Exists(cleanup))
        {
            if (Directory.Exists(transaction)) throw new IOException("更新事务与清理目录同时存在，已保留以供检查。");
            FinishCommittedCleanup();
        }
        if (!Directory.Exists(transaction)) { DeleteRecoveryTask(); return; }
        ProtectedPaths.AssertPath(transaction);
        var journalPath = Path.Combine(transaction, "journal.json");
        if (!File.Exists(journalPath))
        {
            if (Directory.Exists(Path.Combine(transaction, "old")) || !File.Exists(Path.Combine(target, "WattMonitor.App.exe")))
                throw new IOException("恢复记录缺失或安装不完整；已保留更新目录中的备份。");
            DeleteTransaction(); DeleteRecoveryTask(); return;
        }
        var journal = ReadJournal(transaction);
        var committedPath = Path.Combine(transaction, "committed");
        var committed = File.Exists(committedPath);
        if (committed)
        {
            try { VerifyCommitted(journal, transaction); }
            catch (Exception ex)
            {
                if (!Directory.Exists(Path.Combine(transaction, "old")))
                    throw new IOException("已提交的安装无法验证，且找不到原安装备份；已保留恢复资料。", ex);
                File.Delete(committedPath);
                committed = false;
            }
        }
        if (committed)
        {
            MoveCommittedToCleanup();
            FinishCommittedCleanup();
            DeleteRecoveryTask();
            return;
        }
        if (!committed && File.Exists(Path.Combine(transaction, "started")))
        {
            report("正在恢复更新前的程序和采集服务…");
            using var service = ServiceControl.Open(Path.Combine(target, "service", "WattMonitor.Service.exe"), journal.OwnerSid);
            service.SetStartType(4);
            service.Stop();
            var original = Directory.Exists(Path.Combine(transaction, "old")) ? Path.Combine(transaction, "old") : target;
            VerifyPrevious(original, journal);
            DirectoryTransaction.Restore(target, transaction);
            WriteActive(journal.PreviousHelperVersion);
            RestoreService(service, journal);
            if (!File.Exists(Path.Combine(transaction, "rolled-back"))) WriteDurable(Path.Combine(transaction, "rolled-back"), [1]);
        }
        if (!committed && File.Exists(Path.Combine(transaction, "slot-reserved")))
        {
            var slot = Path.Combine(versions, journal.NewHelperVersion);
            if (Directory.Exists(slot) && !string.Equals(Path.GetDirectoryName(executable), slot, StringComparison.OrdinalIgnoreCase))
            {
                ProtectedPaths.AssertTree(slot);
                Directory.Delete(slot, true);
            }
        }
        DeleteTransaction();
        DeleteRecoveryTask();
    }

    private UpdateJournal ReadJournal(string directory)
    {
        if (!string.Equals(directory, transaction, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(directory, cleanup, StringComparison.OrdinalIgnoreCase)) throw new IOException("恢复记录目录校验失败。");
        var journal = JsonSerializer.Deserialize<UpdateJournal>(File.ReadAllBytes(Path.Combine(directory, "journal.json")))
            ?? throw new InvalidDataException("恢复记录为空。");
        if (journal.Product != "WattMonitor" || journal.Format != 1 || !string.Equals(journal.InstallDirectory, target, StringComparison.OrdinalIgnoreCase) ||
            journal.OwnerSid is not string ownerSid || !System.Text.RegularExpressions.Regex.IsMatch(ownerSid, @"^S-\d+(?:-\d+)+$") || journal.StartType is < 2 or > 4 ||
            !UpdateManifest.TryVersion(journal.PreviousHelperVersion, out _) || !UpdateManifest.TryVersion(journal.NewHelperVersion, out _))
            throw new InvalidDataException("恢复记录无效，备份已保留。");
        return journal;
    }

    private void MoveCommittedToCleanup()
    {
        if (!string.Equals(Path.GetFullPath(transaction), Path.Combine(Path.GetFullPath(host), "transaction"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFullPath(cleanup), Path.Combine(Path.GetFullPath(host), "cleanup"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("更新事务目录校验失败。");
        ProtectedPaths.AssertTree(transaction);
        DirectoryTransaction.MoveCommittedToCleanup(transaction, cleanup);
    }

    private void FinishCommittedCleanup()
    {
        if (!string.Equals(Path.GetFullPath(cleanup), Path.Combine(Path.GetFullPath(host), "cleanup"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("更新清理目录校验失败。");
        ProtectedPaths.AssertTree(cleanup);
        DirectoryTransaction.FinishCommittedCleanup(cleanup, () => VerifyCommitted(ReadJournal(cleanup), cleanup));
    }

    private static void RestoreService(ServiceControl service, UpdateJournal journal)
    {
        service.SetStartType(journal.WasRunning && journal.StartType == 4 ? 3u : journal.StartType);
        if (journal.WasRunning) service.Start();
        if (journal.WasRunning && journal.StartType == 4) service.SetStartType(4);
    }

    private void VerifyPrevious(string root, UpdateJournal journal)
    {
        if (!Directory.Exists(root)) throw new IOException("原安装备份不存在，无法安全恢复。");
        ProtectedPaths.AssertTree(root);
        var installed = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "install-manifest.json")))?.AsObject();
        if (installed?["Product"]?.GetValue<string>() != "WattMonitor" || installed["Format"]?.GetValue<int>() != 1 ||
            !string.Equals(installed["InstallDirectory"]?.GetValue<string>()?.TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase) ||
            installed["OwnerSid"]?.GetValue<string>() != journal.OwnerSid || installed["Files"] is not JsonArray recorded)
            throw new InvalidDataException("原安装备份的归属记录无效。");
        var recordedNames = recorded.Select(node => node?.GetValue<string>()?.Replace('/', '\\') ?? "").ToHashSet(StringComparer.OrdinalIgnoreCase);
        var payload = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "payload-manifest.json")))?.AsObject();
        if (payload?["Product"]?.GetValue<string>() != "WattMonitor" || payload["Format"]?.GetValue<int>() != 1 ||
            payload["Version"]?.GetValue<string>() != journal.PreviousHelperVersion || payload["Files"] is not JsonArray files ||
            !recordedNames.Contains("payload-manifest.json")) throw new InvalidDataException("原安装备份的文件清单无效。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in files)
        {
            var name = value?["Path"]?.GetValue<string>()?.Replace('/', '\\') ?? "";
            var hash = value?["Sha256"]?.GetValue<string>() ?? "";
            if (!names.Add(name) || !recordedNames.Contains(name) || !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9A-Fa-f]{64}$"))
                throw new InvalidDataException("原安装备份清单包含无效记录。");
            using var file = File.OpenRead(ProtectedPaths.RelativePath(root, name));
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(file), Convert.FromHexString(hash))) throw new InvalidDataException("原安装备份文件损坏：" + name);
        }
        foreach (var required in new[] { "WattMonitor.App.exe", "service\\WattMonitor.Service.exe", "updater\\WattMonitor.Updater.exe" })
            if (!names.Contains(required)) throw new InvalidDataException("原安装备份缺少必要文件。");
    }

    private void VerifyCommitted(UpdateJournal journal, string transactionRoot)
    {
        if (!Directory.Exists(target)) throw new IOException("已提交的安装目录不存在。");
        ProtectedPaths.AssertTree(target);
        var signed = UpdateManifest.Verify(File.ReadAllBytes(Path.Combine(transactionRoot, UpdateManifest.ManifestName)),
            File.ReadAllBytes(Path.Combine(transactionRoot, UpdateManifest.SignatureName)));
        if (signed.Version != journal.NewHelperVersion) throw new InvalidDataException("恢复记录与签名版本不一致。");
        var archivePath = Path.Combine(transactionRoot, UpdateManifest.ArchiveName);
        UpdateManifest.VerifyArchive(archivePath, signed);
        using var archive = ZipFile.OpenRead(archivePath);
        var entry = archive.Entries.Single(x => string.Equals(x.FullName.Replace('/', '\\'), "payload-manifest.json", StringComparison.OrdinalIgnoreCase));
        if (entry.Length > 4 * 1024 * 1024) throw new InvalidDataException("签名文件清单过大。");
        byte[] expectedPayload;
        using (var input = entry.Open()) using (var memory = new MemoryStream()) { input.CopyTo(memory); expectedPayload = memory.ToArray(); }
        var actualPayload = File.ReadAllBytes(Path.Combine(target, "payload-manifest.json"));
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(expectedPayload), SHA256.HashData(actualPayload))) throw new InvalidDataException("安装文件清单已损坏。");
        JsonObject payload;
        using (var reader = new StreamReader(new MemoryStream(expectedPayload), System.Text.Encoding.UTF8, true))
            payload = JsonNode.Parse(reader.ReadToEnd())?.AsObject() ?? throw new InvalidDataException("安装文件清单为空。");
        if (payload["Product"]?.GetValue<string>() != "WattMonitor" || payload["Format"]?.GetValue<int>() != 1 ||
            payload["Version"]?.GetValue<string>() != signed.Version || payload["Files"] is not JsonArray files) throw new InvalidDataException("安装文件清单无效。");
        var installed = ReadInstalled();
        if (installed["OwnerSid"]?.GetValue<string>() != journal.OwnerSid || installed["Files"] is not JsonArray installedFiles)
            throw new InvalidDataException("安装记录与恢复记录不一致。");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var helperFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in files)
        {
            var name = value?["Path"]?.GetValue<string>()?.Replace('/', '\\') ?? "";
            var hash = value?["Sha256"]?.GetValue<string>() ?? "";
            if (!names.Add(name) || !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9A-Fa-f]{64}$")) throw new InvalidDataException("安装文件清单包含重复或无效项。");
            var path = ProtectedPaths.RelativePath(target, name);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(hash))) throw new InvalidDataException("已提交文件损坏：" + name);
            if (name.StartsWith("updater\\", StringComparison.OrdinalIgnoreCase)) helperFiles.Add(name[8..], hash);
        }
        foreach (var required in new[] { "WattMonitor.App.exe", "service\\WattMonitor.Service.exe", "updater\\WattMonitor.Updater.exe" })
            if (!names.Contains(required)) throw new InvalidDataException("已提交安装缺少必要文件。");
        names.Add("payload-manifest.json");
        var recorded = installedFiles.Select(node => node?.GetValue<string>()?.Replace('/', '\\') ?? "").ToArray();
        if (recorded.Length != names.Count || !names.SetEquals(recorded)) throw new InvalidDataException("已提交安装记录与签名清单不一致。");
        var slot = Path.Combine(versions, journal.NewHelperVersion);
        ProtectedPaths.AssertTree(slot);
        var actualSlot = Directory.EnumerateFiles(slot, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(slot, path)).ToArray();
        var expectedSlot = helperFiles.Keys.Append("slot-files.json").ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (actualSlot.Length != expectedSlot.Count || !expectedSlot.SetEquals(actualSlot)) throw new InvalidDataException("已提交更新助手文件不完整。");
        var slotRecord = JsonNode.Parse(File.ReadAllText(Path.Combine(slot, "slot-files.json")))?.AsObject();
        if (slotRecord?["Product"]?.GetValue<string>() != "WattMonitor" || slotRecord["Format"]?.GetValue<int>() != 1 || slotRecord["Files"] is not JsonArray slotFiles ||
            slotFiles.Count != helperFiles.Count || !helperFiles.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(slotFiles.Select(node => node?.GetValue<string>() ?? "")))
            throw new InvalidDataException("已提交更新助手清单无效。");
        foreach (var (name, hash) in helperFiles)
        {
            using var stream = File.OpenRead(ProtectedPaths.RelativePath(slot, name));
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), Convert.FromHexString(hash))) throw new InvalidDataException("已提交更新助手文件损坏。");
        }
        if (ReadActive() != journal.NewHelperVersion) WriteActive(journal.NewHelperVersion);
        using var service = ServiceControl.Open(Path.Combine(target, "service", "WattMonitor.Service.exe"), journal.OwnerSid);
        RestoreService(service, journal);
    }

    private FileStream Lock() => new(Path.Combine(host, "update.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private JsonObject ReadInstalled()
    {
        var path = Path.Combine(target, "install-manifest.json");
        ProtectedPaths.AssertPath(path);
        var install = JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? throw new InvalidDataException("安装记录为空。");
        if (install["Product"]?.GetValue<string>() != "WattMonitor" || install["Format"]?.GetValue<int>() != 1 ||
            !string.Equals(install["InstallDirectory"]?.GetValue<string>()?.TrimEnd('\\'), target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("安装记录与本机不一致。");
        return install;
    }

    private string ReadActive()
    {
        var path = Path.Combine(host, "active.txt");
        ProtectedPaths.AssertPath(path);
        var value = File.ReadAllText(path).Trim();
        if (!UpdateManifest.TryVersion(value, out _)) throw new InvalidDataException("更新助手版本记录无效。");
        return value;
    }

    private void WriteActive(string version)
    {
        if (!UpdateManifest.TryVersion(version, out _)) throw new InvalidDataException("更新助手版本无效。");
        var temp = Path.Combine(host, "active.pending");
        if (File.Exists(temp)) File.Delete(temp);
        WriteDurable(temp, System.Text.Encoding.UTF8.GetBytes(version));
        File.Move(temp, Path.Combine(host, "active.txt"), true);
    }

    private static void WriteDurable(string path, byte[] content)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(content); stream.Flush(true);
    }

    private void DeleteTransaction()
    {
        if (!string.Equals(Path.GetFullPath(transaction), Path.Combine(Path.GetFullPath(host), "transaction"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("更新事务目录校验失败。");
        ProtectedPaths.AssertTree(transaction);
        Directory.Delete(transaction, true);
    }

    private static void CopyLimited(string source, string destination, long maximum)
    {
        using var held = SafeInput.Open(source);
        var input = held.Stream;
        if (input.Length <= 0 || input.Length > maximum) throw new InvalidDataException("更新文件大小无效。");
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output); output.Flush(true);
    }

    private static void NotifyReady(string pipeName)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(pipeName, @"^WattHalo\.Update\.[0-9a-f]{32}$"))
            throw new InvalidDataException("更新通知通道无效。");
        using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.Out);
        pipe.Connect(15000);
        pipe.Write("READY"u8); pipe.Flush();
    }

    private static void CloneDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.EnumerateFileSystemEntries(source))
        {
            var attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("安装目录包含链接，无法安全更新。");
            var to = Path.Combine(destination, Path.GetFileName(path));
            if ((attributes & FileAttributes.Directory) != 0) CloneDirectory(path, to);
            else { using var input = File.OpenRead(path); using var output = new FileStream(to, FileMode.CreateNew, FileAccess.Write, FileShare.None); input.CopyTo(output); }
        }
    }

    private static void ApplyArchive(string archivePath, string candidate, JsonObject installed, SignedUpdateManifest signed)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > 20000) throw new InvalidDataException("更新包文件数量过多。");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('/', '\\').TrimEnd('\\');
            if (name.Length == 0) continue;
            ProtectedPaths.RelativePath(candidate, name);
            var unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (unixType == 0xa000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("更新包包含链接。");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\')) continue;
            expanded += entry.Length;
            if (entry.Length > UpdateManifest.MaximumArchiveSize || expanded > 2 * UpdateManifest.MaximumArchiveSize || !entries.TryAdd(name, entry))
                throw new InvalidDataException("更新包包含重复或过大的文件。");
        }
        if (!entries.TryGetValue("payload-manifest.json", out var payloadEntry) || payloadEntry.Length > 4 * 1024 * 1024)
            throw new InvalidDataException("更新包缺少文件清单。");
        JsonObject payload;
        using (var input = payloadEntry.Open())
        using (var reader = new StreamReader(input, System.Text.Encoding.UTF8, true)) payload = JsonNode.Parse(reader.ReadToEnd())?.AsObject() ?? throw new InvalidDataException("更新文件清单为空。");
        if (payload["Product"]?.GetValue<string>() != "WattMonitor" || payload["Format"]?.GetValue<int>() != 1 ||
            payload["Version"]?.GetValue<string>() != signed.Version || payload["Files"] is not JsonArray fileList)
            throw new InvalidDataException("更新文件清单无效。");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in fileList)
        {
            var name = value?["Path"]?.GetValue<string>()?.Replace('/', '\\') ?? "";
            var hash = value?["Sha256"]?.GetValue<string>() ?? "";
            ProtectedPaths.RelativePath(candidate, name);
            if (name.Equals("install-manifest.json", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("install-manifest.pending", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("payload-manifest.json", StringComparison.OrdinalIgnoreCase) ||
                !System.Text.RegularExpressions.Regex.IsMatch(hash, "^[0-9A-Fa-f]{64}$") || !files.TryAdd(name, hash))
                throw new InvalidDataException("更新文件清单包含无效或重复的路径。");
        }
        foreach (var required in new[] { "WattMonitor.App.exe", "service\\WattMonitor.Service.exe", "updater\\WattMonitor.Updater.exe", "Uninstall.ps1", "Uninstall.cmd" })
            if (!files.ContainsKey(required)) throw new InvalidDataException("更新包缺少必要文件：" + required);
        if (entries.Count != files.Count + 1 || files.Keys.Any(name => !entries.ContainsKey(name)))
            throw new InvalidDataException("更新文件与清单不一致。");
        if (installed["Files"] is not JsonArray existingFiles) throw new InvalidDataException("安装记录缺少文件清单。");
        var previous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in existingFiles)
        {
            var name = value?.GetValue<string>()?.Replace('/', '\\') ?? "";
            ProtectedPaths.RelativePath(candidate, name);
            if (!previous.Add(name)) throw new InvalidDataException("原安装清单包含重复路径。");
        }
        foreach (var name in files.Keys)
        {
            var destination = ProtectedPaths.RelativePath(candidate, name);
            if (Directory.Exists(destination) || File.Exists(destination) && !previous.Contains(name))
                throw new IOException("更新文件与未登记的现有文件冲突：" + name);
        }
        foreach (var old in previous)
        {
            if (!files.ContainsKey(old))
            {
                var path = ProtectedPaths.RelativePath(candidate, old);
                if (File.Exists(path)) File.Delete(path);
            }
        }
        foreach (var (name, hash) in files)
        {
            var destination = ProtectedPaths.RelativePath(candidate, name);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (File.Exists(destination)) File.Delete(destination);
            using var input = entries[name].Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920]; int read; long written = 0;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                written += read;
                if (written > entries[name].Length) throw new InvalidDataException("更新文件大小无效。");
                output.Write(buffer, 0, read); digest.AppendData(buffer, 0, read);
            }
            output.Flush(true);
            if (written != entries[name].Length || !CryptographicOperations.FixedTimeEquals(digest.GetHashAndReset(), Convert.FromHexString(hash)))
                throw new InvalidDataException("更新文件 SHA-256 不匹配：" + name);
        }
        var manifestDestination = Path.Combine(candidate, "payload-manifest.json");
        if (File.Exists(manifestDestination)) File.Delete(manifestDestination);
        using (var input = payloadEntry.Open())
        using (var output = new FileStream(manifestDestination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(output); output.Flush(true); }
        installed["Files"] = new JsonArray(files.Keys.Append("payload-manifest.json").Select(name => (JsonNode?)JsonValue.Create(name)).ToArray());
        var installPath = Path.Combine(candidate, "install-manifest.json");
        File.Delete(installPath);
        WriteDurable(installPath, JsonSerializer.SerializeToUtf8Bytes(installed));
    }

    private void WaitForAppExit()
    {
        var until = DateTime.UtcNow.AddMinutes(2);
        while (true)
        {
            var running = Process.GetProcessesByName("WattMonitor.App");
            var own = false;
            try
            {
                foreach (var process in running)
                {
                    try
                    {
                        if (!process.HasExited && string.Equals(process.MainModule?.FileName, Path.Combine(target, "WattMonitor.App.exe"), StringComparison.OrdinalIgnoreCase)) own = true;
                    }
                    catch (InvalidOperationException) { }
                    catch (System.ComponentModel.Win32Exception ex) { throw new IOException("无法核对运行中的应用，请先退出后重试。", ex); }
                }
                if (!own) return;
            }
            finally { foreach (var process in running) process.Dispose(); }
            if (DateTime.UtcNow >= until) throw new TimeoutException("请先退出 WattHalo，再重新尝试更新。");
            Thread.Sleep(500);
        }
    }

    private Process StartWatcher()
    {
        using var current = Process.GetCurrentProcess();
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("--watch"); start.ArgumentList.Add(current.Id.ToString()); start.ArgumentList.Add(current.StartTime.ToUniversalTime().Ticks.ToString());
        var watcher = Process.Start(start) ?? throw new IOException("无法启动更新恢复监护进程。");
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!File.Exists(Path.Combine(transaction, "watch-ready")))
        {
            if (watcher.HasExited || DateTime.UtcNow >= until) { watcher.Dispose(); throw new IOException("更新恢复监护进程未就绪。"); }
            Thread.Sleep(100);
        }
        return watcher;
    }

    internal void Watch(int processId, long startTicks)
    {
        WriteDurable(Path.Combine(transaction, "watch-ready"), [1]);
        Process? owner = null;
        try { owner = Process.GetProcessById(processId); if (owner.StartTime.ToUniversalTime().Ticks != startTicks) { owner.Dispose(); owner = null; } }
        catch (ArgumentException) { }
        using (owner)
        {
            while (owner is not null && !owner.HasExited) Thread.Sleep(300);
        }
        Recover();
    }

    private void RegisterRecovery()
    {
        var action = '"' + executable + "\" --recover";
        RunTaskCommand("/Create", "/TN", TaskName, "/SC", "ONSTART", "/RU", "SYSTEM", "/RL", "HIGHEST", "/TR", action);
    }
    private void DeleteRecoveryTask()
    {
        string xml;
        try { xml = RunTaskCommand("/Query", "/TN", TaskName, "/XML"); }
        catch (IOException) { return; }
        var document = System.Xml.Linq.XDocument.Parse(xml);
        var actions = document.Descendants().Where(x => x.Name.LocalName == "Exec").ToArray();
        if (actions.Length != 1) throw new IOException("恢复任务不属于本产品，未删除。");
        var command = actions[0].Elements().SingleOrDefault(x => x.Name.LocalName == "Command")?.Value.Trim('"');
        var arguments = actions[0].Elements().SingleOrDefault(x => x.Name.LocalName == "Arguments")?.Value.Trim();
        if (string.IsNullOrEmpty(command) || !string.Equals(arguments, "--recover", StringComparison.Ordinal) ||
            !string.Equals(Path.GetFileName(command), "WattMonitor.Updater.exe", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetDirectoryName(Path.GetDirectoryName(command)), versions, StringComparison.OrdinalIgnoreCase) ||
            !UpdateManifest.TryVersion(Path.GetFileName(Path.GetDirectoryName(command)), out _))
            throw new IOException("恢复任务不属于本产品，未删除。");
        RunTaskCommand("/Delete", "/TN", TaskName, "/F");
    }
    private static string RunTaskCommand(params string[] arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in arguments) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("无法访问 Windows 任务计划程序。");
        var stdout = process.StandardOutput.ReadToEnd(); var stderr = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30000) || process.ExitCode != 0) throw new IOException("Windows 恢复任务操作失败：" + (stderr.Length > 0 ? stderr : stdout).Trim());
        return stdout;
    }
}
