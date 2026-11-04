using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WattMonitor.Updater;

// Holding every ancestor without delete sharing prevents junction swaps while the file is opened.
internal sealed class SafeInput : IDisposable
{
    private readonly List<SafeFileHandle> parents;
    internal FileStream Stream { get; }
    private SafeInput(List<SafeFileHandle> parents, FileStream stream) { this.parents = parents; Stream = stream; }

    internal static SafeInput Open(string path)
    {
        path = Path.GetFullPath(path);
        var root = Path.GetPathRoot(path) ?? throw new InvalidDataException("更新文件路径无效。");
        if (!System.Text.RegularExpressions.Regex.IsMatch(root, @"^[A-Za-z]:\\$") || new DriveInfo(root).DriveType != DriveType.Fixed)
            throw new InvalidDataException("更新文件必须位于本机固定磁盘。");
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidDataException("更新文件路径无效。");
        var handles = new List<SafeFileHandle>();
        try
        {
            var current = root;
            foreach (var part in directory[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                var handle = CreateFile(current, 0x80, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(); }
                handles.Add(handle);
                CheckHandle(handle, true);
            }
            var file = CreateFile(path, 0x80000000, 1, IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
            if (file.IsInvalid) { file.Dispose(); throw new Win32Exception(); }
            try
            {
                CheckHandle(file, false);
                return new SafeInput(handles, new FileStream(file, FileAccess.Read));
            }
            catch { file.Dispose(); throw; }
        }
        catch { foreach (var handle in handles) handle.Dispose(); throw; }
    }

    private static void CheckHandle(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info)) throw new Win32Exception();
        if ((info.Attributes & (uint)FileAttributes.ReparsePoint) != 0 ||
            ((info.Attributes & (uint)FileAttributes.Directory) != 0) != directory)
            throw new InvalidDataException("更新源路径包含链接或不符合文件类型。");
    }
    public void Dispose() { Stream.Dispose(); foreach (var handle in parents) handle.Dispose(); }

    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);
}
