using System.Security.AccessControl;
using System.Security.Principal;

namespace WattMonitor.Updates;

internal static class ProtectedPaths
{
    private static readonly HashSet<string> Trusted = new(StringComparer.Ordinal)
    {
        "S-1-5-18", "S-1-5-32-544", "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    };
    private const long WriteMask = (long)(FileSystemRights.Write | FileSystemRights.Delete |
        FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) |
        0x40000000L | 0x10000000L;
    private const long RootDangerMask = (long)(FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership) | 0x10000000L;

    internal static void AssertPath(string path)
    {
        var current = Path.GetFullPath(path);
        var root = Path.GetPathRoot(current)!;
        while (true)
        {
            var isRoot = string.Equals(current.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("受保护路径包含链接：" + current);
                FileSystemSecurity acl = (attributes & FileAttributes.Directory) != 0
                    ? new DirectoryInfo(current).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
                    : new FileInfo(current).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
                AssertAccess(acl, current, isRoot);
            }
            if (isRoot) break;
            current = Path.GetDirectoryName(current.TrimEnd('\\')) ?? root;
        }
    }

    internal static void AssertAccess(FileSystemSecurity acl, string path, bool volumeRoot)
    {
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted.Contains(owner.Value))
            throw new InvalidDataException("受保护路径所有者不可信：" + path);
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow ||
                ((long)rule.FileSystemRights & (volumeRoot ? RootDangerMask : WriteMask)) == 0) continue;
            var inheritOnly = (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0;
            var sid = ((SecurityIdentifier)rule.IdentityReference).Value;
            if (Trusted.Contains(sid) || inheritOnly && (volumeRoot || sid == "S-1-3-0")) continue;
            throw new InvalidDataException("受保护路径允许非管理员修改：" + path);
        }
    }

    internal static void AssertTree(string path)
    {
        AssertPath(path);
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            AssertPath(entry);
            if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0) AssertTree(entry);
        }
    }

    internal static string RelativePath(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException("更新包包含不安全的路径。");
        var pieces = relative.Replace('/', '\\').Split('\\');
        foreach (var part in pieces)
        {
            if (part.Length == 0 || part is "." or ".." || part.EndsWith(' ') || part.EndsWith('.') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                System.Text.RegularExpressions.Regex.IsMatch(part, @"^(CON|CONIN\$|CONOUT\$|PRN|AUX|NUL|COM[1-9¹²³]|LPT[1-9¹²³])(?:\..*)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                throw new InvalidDataException("更新包包含不安全的路径。");
        }
        var fullRoot = Path.GetFullPath(root).TrimEnd('\\');
        var result = Path.GetFullPath(Path.Combine(fullRoot, Path.Combine(pieces)));
        if (!result.StartsWith(fullRoot + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("更新包路径超出目标目录。");
        return result;
    }
}
